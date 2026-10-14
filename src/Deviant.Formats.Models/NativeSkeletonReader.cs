using System.Buffers.Binary;
using System.Globalization;
using Deviant.Assets;

namespace Deviant.Formats.Models;

/// <summary>Reads the SKELETON v15 profile through the shared MESH v29 endpoint.</summary>
public sealed class NativeSkeletonReader
{
    public bool TryRead(NativeObjectFrame frame, SceneEntityRecord entity,
        out NativeSkeletonDocument? document, out string? diagnostic, CancellationToken token = default) =>
        TryRead(frame, entity, new NativeSkeletonLimits(), out document, out diagnostic, token);

    public bool TryRead(NativeObjectFrame frame, SceneEntityRecord entity, NativeSkeletonLimits limits,
        out NativeSkeletonDocument? document, out string? diagnostic, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(limits); token.ThrowIfCancellationRequested();
        ValidateLimits(limits); document = null; diagnostic = null;
        if (frame.NativeType != 2013 || frame.ClassCount != 5)
        { diagnostic = "NativeSkeletonProfileUnsupported"; return false; }
        if (frame.RawPayload.Length > limits.MaximumPayloadBytes ||
            frame.RawPayload.Length > NativeMeshPrefixReader.MaximumPayloadBytes)
        { diagnostic = "NativeSkeletonPayloadLimit"; return false; }
        // Reserve revalidation/output scratch before touching caller-controlled collection accessors.
        if (limits.MaximumMetadataBytes < Cursor.FixedMetadataBytes)
        { diagnostic = "NativeSkeletonMetadataLimit"; return false; }
        if (!ValidFrame(frame))
        { diagnostic = "NativeSkeletonSourceMismatch"; return false; }
        try
        {
            var stableEntity = SnapshotEntity(entity, token);
            var owned = new byte[frame.RawPayload.Length];
            for (var at = 0; at < owned.Length; at += 16 * 1024)
            {
                token.ThrowIfCancellationRequested(); var length = Math.Min(16 * 1024, owned.Length - at);
                var source = frame.RawPayload.Span; token.ThrowIfCancellationRequested();
                source.Slice(at, length).CopyTo(owned.AsSpan(at, length));
            }
            token.ThrowIfCancellationRequested();
            var snapshot = frame with { RawPayload = owned };
            if (!new NativeMeshPrefixReader().TryRead(snapshot, stableEntity, out var mesh, out diagnostic, token))
                return false;
            // Validate every variable subsection before creating Skeleton output arrays or DTOs.
            Parse(new Cursor(snapshot, limits, token, false), mesh!, false, false, int.MaxValue, out _);
            token.ThrowIfCancellationRequested();
            document = Parse(new Cursor(snapshot, limits, token, true), mesh!, true, true, int.MaxValue, out _);
            token.ThrowIfCancellationRequested(); return true;
        }
        catch (InvalidDataException error) { diagnostic = error.Message; return false; }
        catch (OverflowException) { diagnostic = "NativeSkeletonSizeOverflow"; return false; }
    }

    /// <summary>Validates a bounded SKELETON v15 frame while retaining only its two typed name tables.</summary>
    public bool TryReadNameTables(NativeObjectFrame frame, SceneEntityRecord entity,
        out IReadOnlyList<SkeletonNameTable> nameTables, out string? diagnostic,
        NativeSkeletonLimits? limits = null, CancellationToken token = default,
        int maximumRetainedNameEntries = 512)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(entity);
        limits ??= new();
        token.ThrowIfCancellationRequested();
        ValidateLimits(limits); nameTables = []; diagnostic = null;
        if (maximumRetainedNameEntries is < 0 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedNameEntries));
        if (frame.NativeType != 2013 || frame.ClassCount != 5)
        { diagnostic = "NativeSkeletonProfileUnsupported"; return false; }
        if (frame.RawPayload.Length > limits.MaximumPayloadBytes ||
            frame.RawPayload.Length > NativeMeshPrefixReader.MaximumPayloadBytes)
        { diagnostic = "NativeSkeletonPayloadLimit"; return false; }
        if (limits.MaximumMetadataBytes < Cursor.FixedMetadataBytes)
        { diagnostic = "NativeSkeletonMetadataLimit"; return false; }
        if (!ValidFrame(frame))
        { diagnostic = "NativeSkeletonSourceMismatch"; return false; }
        try
        {
            var stableEntity = SnapshotEntity(entity, token);
            if (!new NativeMeshPrefixReader().TryRead(frame, stableEntity, out var mesh, out diagnostic, token) || mesh is null)
                return false;
            Parse(new Cursor(frame, limits, token, false), mesh, false, true,
                maximumRetainedNameEntries, out nameTables);
            token.ThrowIfCancellationRequested();
            return true;
        }
        catch (InvalidDataException error) { diagnostic = error.Message; return false; }
        catch (OverflowException) { diagnostic = "NativeSkeletonSizeOverflow"; return false; }
    }

    private static bool ValidFrame(NativeObjectFrame frame)
    {
        var identity = frame.Identity; var extent = frame.PayloadExtent;
        if (identity is null || extent is null || extent.Container is null || frame.HeaderExtent is null ||
            frame.DirectoryEntryExtent is null || identity.IndexContentId is null ||
            string.IsNullOrWhiteSpace(identity.IndexContentId) || identity.IndexContentId.Length > 4096 ||
            identity.Ordinal < 0 || frame.DirectoryIndex < 0 || identity.MemberPath is null || identity.MemberPath.Length > 4096 ||
            extent.Container.MemberPath is not null || extent.Container.IndexContentId != identity.IndexContentId ||
            extent.Container.Ordinal != identity.Ordinal || extent.Length != frame.RawPayload.Length || !ValidExtent(extent) ||
            !ValidExtent(frame.HeaderExtent) || !ValidExtent(frame.DirectoryEntryExtent) ||
            frame.HeaderExtent.Container != extent.Container || frame.DirectoryEntryExtent.Container != extent.Container)
            return false;
        var expected = string.Create(CultureInfo.InvariantCulture, $"dc-object/{frame.DirectoryIndex}/2013/{frame.NativeId}");
        return StringComparer.Ordinal.Equals(expected, identity.MemberPath);
    }
    private static bool ValidExtent(DecodedExtent extent) => extent.Offset >= 0 && extent.Length >= 0 &&
        extent.Offset <= long.MaxValue - extent.Length;

    private static SceneEntityRecord SnapshotEntity(SceneEntityRecord entity, CancellationToken token)
    {
        if (entity.OtherReferences is null || entity.FieldExtents is null)
            throw new InvalidDataException("NativeSkeletonEntityMetadataInvalid");
        var count = entity.OtherReferences.Count; token.ThrowIfCancellationRequested();
        var fieldCount = entity.FieldExtents.Count; token.ThrowIfCancellationRequested();
        if (count is < 0 or > 3 || fieldCount != 6)
            throw new InvalidDataException("NativeSkeletonEntityMetadataInvalid");
        var references = new ResourceReference[count];
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested(); var value = entity.OtherReferences[i]; token.ThrowIfCancellationRequested();
            if (value is null) throw new InvalidDataException("NativeSkeletonEntityMetadataInvalid");
            references[i] = value;
        }
        var fields = new Dictionary<string, DecodedExtent>(6, StringComparer.Ordinal);
        foreach (var key in new[] { "Node", "Reference1", "Reference2", "Reference3", "Word32", "Flag36" })
        {
            token.ThrowIfCancellationRequested(); var found = entity.FieldExtents.TryGetValue(key, out var value);
            token.ThrowIfCancellationRequested();
            if (!found || value is null) throw new InvalidDataException("NativeSkeletonEntityMetadataInvalid");
            fields.Add(key, value);
        }
        return entity with { OtherReferences = Array.AsReadOnly(references), FieldExtents = fields };
    }

    private static NativeSkeletonDocument? Parse(Cursor r, NativeMeshPrefixDocument mesh, bool emit,
        bool captureNameTables, int maximumCapturedNameEntries,
        out IReadOnlyList<SkeletonNameTable> nameTables)
    {
        r.Skip(mesh.NextClassOffset); var start = r.Position;
        r.Tag("SKELETON"u8, 15); var leading = r.Word();
        var matricesCount = r.Count(r.Limits.MaximumMatrices); r.Require(checked(matricesCount * 64));
        r.Account(checked(matricesCount * 128L));
        var matrices = emit ? new SkeletonStoredMatrix[matricesCount] : null;
        for (var i = 0; i < matricesCount; i++)
        {
            r.Check(); var at = r.Position; r.Skip(64);
            if (emit) matrices![i] = new(i, r.Extent(at, 64), r.Bytes(at, 64));
        }
        var nestedCount = r.Count(r.Limits.MaximumNestedRecords);
        r.Require(checked(nestedCount * 42)); r.Account(checked(nestedCount * 1536L));
        var nested = emit ? new SkeletonNestedRecord[nestedCount] : null;
        for (var i = 0; i < nestedCount; i++)
        {
            r.Check(); var value = ReadNested(r, i, emit); if (emit) nested![i] = value!;
        }
        var trailing = r.Word();
        var first = ReadNames(r, 0, emit || captureNameTables, maximumCapturedNameEntries);
        var second = ReadNames(r, 1, emit || captureNameTables, maximumCapturedNameEntries);
        nameTables = emit || captureNameTables ? Array.AsReadOnly(new[] { first!, second! }) : [];
        if (r.Position != r.Length) throw new InvalidDataException("NativeSkeletonEndpointMismatch");
        r.Check();
        return !emit ? null : new(r.Frame.Identity, 15, mesh.Entity.Node, mesh, leading,
            Array.AsReadOnly(matrices!), Array.AsReadOnly(nested!), trailing,
            Array.AsReadOnly(new[] { first!, second! }), r.Extent(start, r.Position - start), r.Accounted, r.Frame.RawPayload);
    }

    private static SkeletonNestedRecord? ReadNested(Cursor r, int ordinal, bool emit)
    {
        var start = r.Position; var version = r.Byte();
        if (version != 17) throw new InvalidDataException("NativeSkeletonNestedVersionUnsupported");
        var words = r.WordSection("Words", emit);
        var referenceAt = r.Position; var type = r.Word(); var id = r.Word();
        var count = r.Count(r.Limits.MaximumPolymorphicRecords);
        r.TotalObjects = checked(r.TotalObjects + count);
        if (r.TotalObjects > r.Limits.MaximumPolymorphicRecords)
            throw new InvalidDataException("NativeSkeletonObjectLimit");
        r.Require(checked(count * 2)); r.Account(checked(count * 1536L));
        var kindsAt = r.Position; var kinds = r.Section("Kinds", count, 1, emit);
        var extraAt = r.Position; var extra = r.Section("ExtraBytes", count, 1, emit);
        var objects = emit ? new SkeletonPolymorphicRecord[count] : null;
        for (var i = 0; i < count; i++)
        {
            r.Check(); var value = ReadObject(r, i, r.ByteAt(kindsAt + i), r.ByteAt(extraAt + i), emit);
            if (emit) objects![i] = value!;
        }
        var blob = r.SizedSection("Blob", emit); var otherWords = r.WordSection("OtherWords", emit);
        var label = r.SizedSection("Label", emit);
        var scalar64 = r.Word(); var scalar68 = r.Word(); var flag = r.Byte(); var tailA = r.Half(); var tailB = r.Half();
        return !emit ? null : new(ordinal, version, words!, new(type, id, r.Extent(referenceAt, 8)), kinds!, extra!,
            Array.AsReadOnly(objects!), blob!, otherWords!, label!, scalar64, scalar68, flag, tailA, tailB,
            r.Extent(start, r.Position - start));
    }

    private static SkeletonPolymorphicRecord? ReadObject(Cursor r, int ordinal, byte kind, byte extra, bool emit)
    {
        var start = r.Position; var a = 0; var b = 0; uint? leadingWord = null;
        var helpers = emit ? new List<SkeletonHelperBlock>(5) : null;
        void Helper(string name)
        {
            r.Check(); var at = r.Position; ushort? leading = null; ushort? skipped = null; int? count = null;
            switch (name)
            {
                case "A": leading = r.Half(); a = r.Byte(); count = a; r.Skip(a); break;
                case "B": b = r.Byte(); count = b; r.Skip(b); break;
                case "C": count = a; r.Skip(a); break;
                case "D": count = b; r.Skip(b); break;
                case "E": count = r.Byte(); r.Skip(checked(count.Value * 4)); skipped = r.Half(); r.Skip(skipped.Value); break;
            }
            if (emit) helpers!.Add(new(name, leading, name is "C" or "D" ? null : count, count ?? 0, skipped,
                new(name, r.Position - at, 1, r.Extent(at, r.Position - at), r.Bytes(at, r.Position - at))));
        }
        int tailAt;
        switch (kind)
        {
            case 0: Helper("A"); Helper("B"); Helper("C"); Helper("D"); Helper("E"); tailAt = r.Position; r.Skip(8); break;
            case 1:
            case 10:
                // The leading u32 is part of the total record span; helpers begin after it.
                leadingWord = r.Word(); Helper("A"); Helper("B"); Helper("E"); tailAt = r.Position; break;
            case 2: Helper("A"); Helper("B"); Helper("C"); Helper("D"); Helper("E"); tailAt = r.Position; r.Skip(24); break;
            case 5: Helper("A"); Helper("C"); Helper("B"); Helper("D"); Helper("E"); tailAt = r.Position; r.Skip(60); break;
            case 6: Helper("A"); Helper("C"); Helper("E"); tailAt = r.Position; r.Skip(60); break;
            case 7: Helper("A"); Helper("B"); Helper("E"); tailAt = r.Position; r.SizedSection("TerminalString", false); break;
            default: throw new InvalidDataException("NativeSkeletonKindUnsupported");
        }
        r.Check();
        return !emit ? null : new(ordinal, kind, extra, leadingWord, Array.AsReadOnly(helpers!.ToArray()),
            new("Terminal", r.Position - tailAt, 1, r.Extent(tailAt, r.Position - tailAt), r.Bytes(tailAt, r.Position - tailAt)),
            r.Extent(start, r.Position - start));
    }

    private static SkeletonNameTable? ReadNames(Cursor r, int ordinal, bool emit, int maximumCapturedEntries)
    {
        var start = r.Position; var size = r.Count(r.Length); r.Require(size); var end = checked(r.Position + size);
        var entries = emit ? new List<SkeletonNameEntry>() : null; var index = 0;
        while (r.Position < end)
        {
            r.Check(); if (end - r.Position < 6) throw new InvalidDataException("NativeSkeletonNameBounds");
            var at = r.Position; var key = r.Word(); var bytes = r.Half(); var nameAt = r.Position;
            if (bytes > end - nameAt) throw new InvalidDataException("NativeSkeletonNameBounds");
            r.TotalNames = checked(r.TotalNames + 1); r.TotalNameBytes = checked(r.TotalNameBytes + bytes);
            if (r.TotalNames > r.Limits.MaximumNameEntries || r.TotalNameBytes > r.Limits.MaximumNameBytes)
                throw new InvalidDataException("NativeSkeletonNameLimit");
            // Includes object/extent/list capacity and the final read-only reference array.
            r.Account(256); r.Skip(bytes);
            if (emit && entries!.Count < maximumCapturedEntries)
                entries.Add(new(index, key, bytes, r.Extent(at, 6 + bytes), r.Extent(nameAt, bytes), r.Bytes(nameAt, bytes)));
            index++;
        }
        r.Check();
        return !emit ? null : new(ordinal, r.Extent(start, r.Position - start), Array.AsReadOnly(entries!.ToArray()));
    }

    private static void ValidateLimits(NativeSkeletonLimits limits)
    {
        if (limits.MaximumPayloadBytes < 0 || limits.MaximumMatrices < 0 || limits.MaximumNestedRecords < 0 ||
            limits.MaximumPolymorphicRecords < 0 || limits.MaximumNameEntries < 0 || limits.MaximumNameBytes < 0 ||
            limits.MaximumRawWords < 0 || limits.MaximumMetadataBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    private sealed class Cursor
    {
        public const long FixedMetadataBytes = 4096;
        public NativeObjectFrame Frame { get; }
        public NativeSkeletonLimits Limits { get; }
        public int Position { get; private set; }
        public int Length => Frame.RawPayload.Length;
        public long Accounted { get; private set; } = FixedMetadataBytes;
        public long TotalObjects; public long TotalNames; public long TotalNameBytes; private long totalWords;
        private readonly CancellationToken token;
        public Cursor(NativeObjectFrame frame, NativeSkeletonLimits limits, CancellationToken token, bool emit)
        { Frame = frame; Limits = limits; this.token = token; }
        public void Check() => token.ThrowIfCancellationRequested();
        public void Require(int count)
        {
            Check(); if (count < 0 || count > Length - Position) throw new InvalidDataException("NativeSkeletonBounds");
        }
        public void Account(long count)
        {
            Check(); Accounted = checked(Accounted + count);
            if (Accounted > Limits.MaximumMetadataBytes) throw new InvalidDataException("NativeSkeletonMetadataLimit");
        }
        public void Skip(int count) { Require(count); Position += count; }
        public uint Word() { Require(4); var value = BinaryPrimitives.ReadUInt32LittleEndian(Frame.RawPayload.Span.Slice(Position, 4)); Position += 4; return value; }
        public ushort Half() { Require(2); var value = BinaryPrimitives.ReadUInt16LittleEndian(Frame.RawPayload.Span.Slice(Position, 2)); Position += 2; return value; }
        public byte Byte() { Require(1); return Frame.RawPayload.Span[Position++]; }
        public byte ByteAt(int at) { Check(); return Frame.RawPayload.Span[at]; }
        public int Count(int maximum)
        {
            var value = Word(); if (value > maximum) throw new InvalidDataException("NativeSkeletonCountLimit"); return (int)value;
        }
        public void Tag(ReadOnlySpan<byte> tag, uint version)
        {
            Require(12);
            if (!Frame.RawPayload.Span.Slice(Position, 8).SequenceEqual(tag) ||
                BinaryPrimitives.ReadUInt32LittleEndian(Frame.RawPayload.Span.Slice(Position + 8, 4)) != version)
                throw new InvalidDataException("NativeSkeletonClassUnsupported");
            Position += 12;
        }
        public ReadOnlyMemory<byte> Bytes(int at, int length) => Frame.RawPayload.Slice(at, length);
        public DecodedExtent Extent(int at, int length) => new(Frame.PayloadExtent.Container,
            checked(Frame.PayloadExtent.Offset + at), length);
        public SkeletonRawSection? Section(string name, int count, int stride, bool emit)
        {
            var at = Position; var length = checked(count * stride); Skip(length);
            return !emit ? null : new(name, count, stride, Extent(at, length), Bytes(at, length));
        }
        public SkeletonRawSection? SizedSection(string name, bool emit) => Section(name, Count(Length), 1, emit);
        public SkeletonRawSection? WordSection(string name, bool emit)
        {
            var count = Count(Limits.MaximumRawWords); totalWords = checked(totalWords + count);
            if (totalWords > Limits.MaximumRawWords) throw new InvalidDataException("NativeSkeletonWordLimit");
            return Section(name, count, 4, emit);
        }
    }
}
