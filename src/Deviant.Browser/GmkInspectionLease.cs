using System.Globalization;
using System.Security.Cryptography;
using System.Collections.ObjectModel;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Containers;
using Deviant.Formats.Narrative;

namespace Deviant.Browser;

/// <summary>Retains a native container while exposing one bounded GMK_COMG inspection.</summary>
public sealed class GmkInspectionLease : IDisposable
{
    private const long DecoderReservationBytes = 64L * 1024 * 1024;
    private const long ProjectionReservationBytes = 4L * 1024 * 1024;
    private const long TotalReservationBytes = DecoderReservationBytes + ProjectionReservationBytes;

    private NativeContainerLease? container;
    private IDisposable? borrowedLease;
    private AssetMemoryBudget.Reservation? reservation;
    private DecodeResult<GmkCommonGroupDocument>? document;
    private DecodeResult<BufferInspectionDocument>? inspection;
    private readonly SourceRevision revision;
    private readonly AssetMemoryBudget memoryBudget;
    private int disposed;

    private GmkInspectionLease(NativeContainerLease container, IDisposable borrowedLease,
        AssetMemoryBudget.Reservation reservation, ResourceIdentity memberIdentity,
        int? selectedItemIndex, DecodeResult<GmkCommonGroupDocument> document,
        DecodeResult<BufferInspectionDocument> inspection)
    {
        this.container = container;
        this.borrowedLease = borrowedLease;
        this.reservation = reservation;
        this.document = document;
        this.inspection = inspection;
        revision = container.Revision;
        memoryBudget = container.MemoryBudget;
        MemberIdentity = memberIdentity;
        SelectedItemIndex = selectedItemIndex;
    }

    public ResourceIdentity MemberIdentity { get; }
    public SourceRevision Revision => revision;
    public AssetMemoryBudget MemoryBudget => memoryBudget;
    public int? SelectedItemIndex { get; }

    public DecodeResult<GmkCommonGroupDocument> Document
    {
        get
        {
            ThrowIfDisposed();
            return document ?? throw new ObjectDisposedException(nameof(GmkInspectionLease));
        }
    }

    public DecodeResult<BufferInspectionDocument> Inspection
    {
        get
        {
            ThrowIfDisposed();
            return inspection ?? throw new ObjectDisposedException(nameof(GmkInspectionLease));
        }
    }

    public static async Task<GmkInspectionLease> OpenAsync(ArchiveSnapshot snapshot,
        ResourceIdentity exactMember, AssetMemoryBudget budget, CancellationToken token = default,
        int? itemIndex = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(exactMember);
        ArgumentNullException.ThrowIfNull(budget);
        token.ThrowIfCancellationRequested();
        ValidateMemberOwner(snapshot.IndexContentId, snapshot.Records, exactMember);
        _ = ParseMember(exactMember);

        var containerIdentity = new ResourceIdentity(exactMember.IndexContentId, exactMember.Ordinal);
        using var opened = await NativeContainerLease.OpenAsync(snapshot, containerIdentity, budget, token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return FromContainer(opened, exactMember, token, itemIndex);
    }

    /// <summary>Inspects one exact GMK frame using the shared native container and G1 reader.</summary>
    public static GmkInspectionLease FromContainer(NativeContainerLease container,
        ResourceIdentity exactMember, CancellationToken token = default, int? itemIndex = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(exactMember);
        token.ThrowIfCancellationRequested();

        IDisposable? borrowed = null;
        AssetMemoryBudget.Reservation? heldReservation = null;
        try
        {
            borrowed = container.AcquireLease();
            ValidateMemberOwner(container.Snapshot.IndexContentId, container.Snapshot.Records, exactMember);
            var member = ParseMember(exactMember);
            ValidateContainer(container, exactMember);
            container.EnsureUnchanged();
            token.ThrowIfCancellationRequested();

            var frame = FindFrame(container.Directory, exactMember, member.DirectoryIndex, member.NativeId, token);
            heldReservation = container.MemoryBudget.Reserve(TotalReservationBytes,
                "GMK_COMG decoder and inspection projection");
            token.ThrowIfCancellationRequested();

            var decoded = new GmkCommonGroupReader().Read(frame, container.Revision,
                container.SourceExtents, cancellationToken: token);
            token.ThrowIfCancellationRequested();
            container.EnsureUnchanged();

            int? selected = null;
            DecodeResult<BufferInspectionDocument> projected;
            if (decoded.Value is { } parsed)
            {
                if (itemIndex is { } requested && (requested < 0 || requested >= parsed.Items.Count))
                    throw new ArgumentOutOfRangeException(nameof(itemIndex), requested,
                        parsed.Items.Count == 0 ? "The group contains no items to select." :
                        $"Choose an item index between 0 and {parsed.Items.Count - 1}.");
                selected = itemIndex ?? (parsed.Items.Count > 0 ? 0 : null);
                projected = Project(decoded, parsed, selected, container.StoredSource.Extent,
                    container.Identity, SharedSteps(container, decoded.Steps), token);
            }
            else
            {
                if (itemIndex is not null)
                    throw new ArgumentOutOfRangeException(nameof(itemIndex), itemIndex,
                        "An item cannot be selected because the group did not parse successfully.");
                var steps = SharedSteps(container, decoded.Steps);
                steps = steps.Append(new(nameof(GmkInspectionLease), "gmk-inspection-v1", "GMK_COMG buffer inspection projection")).ToArray();
                projected = new(null, container.Identity, decoded.Revision, decoded.Extents, steps,
                    decoded.Diagnostics, decoded.UnresolvedReferences, decoded.UnknownFields);
            }

            token.ThrowIfCancellationRequested();
            container.EnsureUnchanged();
            var lease = new GmkInspectionLease(container, borrowed, heldReservation, exactMember,
                selected, decoded, projected);
            borrowed = null;
            heldReservation = null;
            return lease;
        }
        catch
        {
            heldReservation?.Dispose();
            borrowed?.Dispose();
            throw;
        }
    }

    /// <summary>Rechecks the source revision retained by the shared native-container lease.</summary>
    public void EnsureUnchanged()
    {
        ThrowIfDisposed();
        var current = container ?? throw new ObjectDisposedException(nameof(GmkInspectionLease));
        ValidateContainer(current, MemberIdentity);
        current.EnsureUnchanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Interlocked.Exchange(ref document, null);
        Interlocked.Exchange(ref inspection, null);
        Interlocked.Exchange(ref container, null);
        Interlocked.Exchange(ref reservation, null)?.Dispose();
        Interlocked.Exchange(ref borrowedLease, null)?.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private static DecodeResult<BufferInspectionDocument> Project(
        DecodeResult<GmkCommonGroupDocument> source, GmkCommonGroupDocument document,
        int? selectedItemIndex, SourceExtent originalExtent, ResourceIdentity parentIdentity,
        IReadOnlyList<DecodeStep> precedingSteps, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sections = new List<BufferInspectionSection>();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Native ID"] = document.NativeId.ToString(CultureInfo.InvariantCulture),
            ["Version"] = document.ClassVersion.ToString(CultureInfo.InvariantCulture),
            ["Word 16"] = document.Word16.ToString(CultureInfo.InvariantCulture),
            ["Items"] = document.Items.Count.ToString(CultureInfo.InvariantCulture),
            ["Payload bytes"] = document.RawPayload.Length.ToString(CultureInfo.InvariantCulture),
            ["Source member"] = source.Identity.MemberPath ?? string.Empty
        };

        AddSection(sections, "group", "Group payload", document.RawPayload,
            document.PayloadExtent, token);
        if (selectedItemIndex is { } index)
        {
            token.ThrowIfCancellationRequested();
            var item = document.Items[index];
            var raw = document.RawPayload;
            AddSection(sections, "item", "Selected item", Slice(raw, document.PayloadExtent, item.Extent),
                item.Extent, token);
            AddSection(sections, "name", "Stored name", Slice(raw, document.PayloadExtent, item.StoredName.TextExtent),
                item.StoredName.TextExtent, token);
            AddSection(sections, "text", "Stored text", Slice(raw, document.PayloadExtent, item.StoredText.TextExtent),
                item.StoredText.TextExtent, token);
            var wordExtent = WordsExtent(document.PayloadExtent, item, token);
            AddSection(sections, "words", "Words", Slice(raw, document.PayloadExtent, wordExtent), wordExtent, token);
            AddSection(sections, "reference-flag", "Reference flag",
                Slice(raw, document.PayloadExtent, item.ReferenceFlagExtent), item.ReferenceFlagExtent, token);
            if (item.ReferencePair is { } pair)
                AddSection(sections, "reference-pair", "Reference pair",
                    Slice(raw, document.PayloadExtent, pair.Extent), pair.Extent, token);
            AddSection(sections, "final-word", "Final word",
                Slice(raw, document.PayloadExtent, item.FinalWordExtent), item.FinalWordExtent, token);

            fields["Item"] = index.ToString(CultureInfo.InvariantCulture);
            fields["Factory class"] = item.FactoryClassId.ToString(CultureInfo.InvariantCulture);
            fields["Name"] = item.StoredName.DisplayText;
            fields["Name CRC"] = item.ComputedNameCrc32.ToString("x8", CultureInfo.InvariantCulture);
            fields["Name UTF-8"] = item.StoredName.IsValidUtf8.ToString();
            fields["Stored text"] = item.StoredText.DisplayText;
            fields["Text UTF-8"] = item.StoredText.IsValidUtf8.ToString();
            fields["Word count"] = item.Words.Count.ToString(CultureInfo.InvariantCulture);
            fields["Reference flag"] = item.ReferenceFlag.ToString(CultureInfo.InvariantCulture);
            fields["Final word"] = item.FinalWord.ToString(CultureInfo.InvariantCulture);
            if (item.ReferencePair is { } reference)
            {
                fields["Reference word 1"] = reference.RawWord0.ToString(CultureInfo.InvariantCulture);
                fields["Reference word 2"] = reference.RawWord1.ToString(CultureInfo.InvariantCulture);
                fields["Reference lookup"] = reference.LookupClassification.ToString();
            }
        }

        token.ThrowIfCancellationRequested();
        var inspection = new BufferInspectionDocument("Common group", "GMK_COMG v22",
            "Native object payload with exact decoded field extents", originalExtent,
            Array.AsReadOnly(sections.ToArray()), Array.AsReadOnly(Array.Empty<AssetRelationship>()),
            new ReadOnlyDictionary<string, string>(fields));
        var steps = precedingSteps.Append(new(nameof(GmkInspectionLease), "gmk-inspection-v1",
            "GMK_COMG buffer inspection projection")).ToArray();
        return new(inspection, parentIdentity, source.Revision, source.Extents, steps,
            source.Diagnostics, source.UnresolvedReferences, source.UnknownFields);
    }

    private static void AddSection(List<BufferInspectionSection> sections, string key, string label,
        ReadOnlyMemory<byte> bytes, DecodedExtent extent, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        sections.Add(new(key, label, BufferSectionKind.DecodedBuffer, null, extent,
            Hash(bytes, token), bytes));
    }

    private static ReadOnlyMemory<byte> Slice(ReadOnlyMemory<byte> raw, DecodedExtent payload,
        DecodedExtent field)
    {
        if (field.Container != payload.Container || field.Offset < payload.Offset || field.Length < 0 ||
            field.Offset > long.MaxValue - field.Length || field.Offset + field.Length > payload.Offset + payload.Length)
            throw new ArchiveException("GmkInspectionExtentInvalid", "A decoded field falls outside the GMK payload.");
        var relative = checked((int)(field.Offset - payload.Offset));
        var length = checked((int)field.Length);
        if (relative > raw.Length - length)
            throw new ArchiveException("GmkInspectionExtentInvalid", "A decoded field falls outside the retained payload bytes.");
        return raw.Slice(relative, length);
    }

    private static DecodedExtent WordsExtent(DecodedExtent payload, GmkCommonGroupItem item,
        CancellationToken token)
    {
        if (item.Words.Count == 0)
            return new(payload.Container, checked(item.WordCountExtent.Offset + item.WordCountExtent.Length), 0);
        var first = item.Words[0].Extent;
        var length = checked((long)item.Words.Count * 4);
        for (var index = 0; index < item.Words.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var word = item.Words[index].Extent;
            if (word.Container != payload.Container || word.Offset != first.Offset + index * 4L || word.Length != 4)
                throw new ArchiveException("GmkInspectionExtentInvalid", "The word fields are not one contiguous payload span.");
        }
        return new(payload.Container, first.Offset, length);
    }

    private static string Hash(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        const int blockBytes = 128 * 1024;
        for (var offset = 0; offset < bytes.Length; offset += blockBytes)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(bytes.Span.Slice(offset, Math.Min(blockBytes, bytes.Length - offset)));
        }
        token.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static NativeObjectFrame FindFrame(NativeObjectDirectoryDocument directory,
        ResourceIdentity exactMember, int directoryIndex, uint nativeId, CancellationToken token)
    {
        NativeObjectFrame? found = null;
        foreach (var frame in directory.Objects)
        {
            token.ThrowIfCancellationRequested();
            if (frame.DirectoryIndex != directoryIndex || frame.NativeType != GmkCommonGroupReader.NativeType ||
                frame.NativeId != nativeId || frame.Identity != exactMember)
                continue;
            if (found is not null)
                throw new ArchiveException("GmkMemberAmbiguous", "The exact GMK frame identity is ambiguous in this container.");
            found = frame;
        }
        return found ?? throw new ArchiveException("GmkMemberUnavailable", "The exact GMK frame is missing from this container.");
    }

    private static IReadOnlyList<DecodeStep> SharedSteps(NativeContainerLease container,
        IReadOnlyList<DecodeStep> parserSteps)
    {
        var steps = container.Segments?.Steps.ToList() ?? [];
        if (container.Segments is null)
            steps.Add(new(nameof(NativeObjectDirectoryLoader), NativeObjectDirectoryLoader.DirectVersion,
                "QZIP direct container"));
        steps.Add(new(nameof(NativeObjectDirectoryReader), NativeObjectDirectoryReader.Version,
            "borrowed shared directory"));
        steps.AddRange(parserSteps);
        return steps.AsReadOnly();
    }

    private static (int DirectoryIndex, uint NativeId) ParseMember(ResourceIdentity member)
    {
        var path = member.MemberPath;
        if (path is null)
            throw new ArchiveException("GmkMemberUnavailable", "The GMK member identity is not fully qualified.");
        var parts = path.Split('/');
        if (parts.Length != 4 || parts[0] != "dc-object" ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var directoryIndex) ||
            directoryIndex < 0 || parts[1] != directoryIndex.ToString(CultureInfo.InvariantCulture) ||
            parts[2] != GmkCommonGroupReader.NativeType.ToString(CultureInfo.InvariantCulture) ||
            !uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var nativeId) ||
            parts[3] != nativeId.ToString(CultureInfo.InvariantCulture))
            throw new ArchiveException("GmkMemberUnavailable", "The GMK member identity is not canonical.");
        return (directoryIndex, nativeId);
    }

    private static void ValidateMemberOwner(string indexContentId, IReadOnlyList<ResourceRecord> records,
        ResourceIdentity member)
    {
        if (member.IndexContentId != indexContentId || member.MemberPath is null || member.Ordinal < 0 ||
            member.Ordinal >= records.Count || records[checked((int)member.Ordinal)].Type != 29)
            throw new ArchiveException("GmkSourceMismatch", "Select a GMK member from a native container in this archive revision.");
    }

    private static void ValidateContainer(NativeContainerLease container, ResourceIdentity member)
    {
        var snapshot = container.Snapshot;
        var expectedRevision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        if (container.Revision != expectedRevision || container.Directory.Revision != expectedRevision ||
            container.Identity.IndexContentId != snapshot.IndexContentId || container.Identity.Ordinal != member.Ordinal ||
            container.Identity.MemberPath is not null || container.StoredSource.Identity != container.Identity ||
            container.StoredSource.Type != 29 || container.Directory.Identity != container.Identity ||
            member.IndexContentId != snapshot.IndexContentId || member.Ordinal != container.Identity.Ordinal)
            throw new ArchiveException("GmkSourceMismatch", "The GMK frame and native container do not match the selected source revision.");
        _ = ParseMember(member);
    }
}
