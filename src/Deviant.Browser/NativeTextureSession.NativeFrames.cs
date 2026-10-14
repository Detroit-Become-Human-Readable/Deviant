using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Containers;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

public sealed partial class NativeTextureSession
{
    private readonly ConditionalWeakTable<NativeContainerLease, ConcurrentDictionary<string, Task<NativeTextureMetadataPlan?>>> nativePlans = new();
    private sealed record NativeTextureMetadataPlan(TextureFileTextV24Document Record, ReadOnlyMemory<byte> Bytes, string CatalogSha256);
    public async Task<NativeTextureReadResult> ReadForNativeAsync(ResourceIdentity nativeTextureIdentity,
        ResourceIdentity catalogIdentity, NativeContainerLease companion, CancellationToken token = default,
        long reservedSourceBytes = 0, int maximumPreviewDimension = int.MaxValue, bool reservePayloadMemory = false)
    {
        ArgumentNullException.ThrowIfNull(nativeTextureIdentity);
        ArgumentNullException.ThrowIfNull(catalogIdentity);
        ArgumentNullException.ThrowIfNull(companion);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (reservedSourceBytes < 0 || reservedSourceBytes > MaximumRetainedSourceBytes)
            throw new ArgumentOutOfRangeException(nameof(reservedSourceBytes));
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        own.Token.ThrowIfCancellationRequested();
        ValidateCompanion(companion);
        using var borrowed = companion.AcquireLease();
        await readGate.WaitAsync(own.Token).ConfigureAwait(false);
        try
        {
            var metadata = await ReadNativeMetadataAsync(nativeTextureIdentity, catalogIdentity, companion, own.Token)
                .ConfigureAwait(false);
            if (metadata is null) return new(null, "Texture metadata is unavailable.");
            if (metadata.Record.References.Any(reference => reference.Resolution.Status != ReferenceStatus.Unique ||
                reference.Resolution.Candidates.Count != 1))
                return new(null, "Texture references are missing or ambiguous.",
                    CandidateIdentities: [metadata.Record.Identity]) { FailureCode = "TextureReferenceUnavailable" };
            var record = metadata.Record;
            if (record.References.Count < 1 || record.References[0].Reference.ResourceType != 2229 ||
                record.References.Skip(1).Any(reference => reference.Reference.ResourceType != 2230))
                return new(null, "This texture reference layout is unavailable.", CandidateIdentities: [record.Identity])
                { FailureCode = "TextureReferenceLayout" };
            var metadataSource = CreateMetadataSource(nativeTextureIdentity, catalogIdentity, companion, metadata);
            var identities = maximumPreviewDimension == int.MaxValue
                ? record.References.Select(reference => reference.Resolution.Candidates[0]).Distinct().ToArray()
                : NativeTextureDecoder.PlanPreview(metadataSource, maximumPreviewDimension, own.Token).RequiredPayloadIdentities;
            long retainedBytes = metadata.Bytes.Length;
            foreach (var identity in identities)
            {
                var size = Snapshot.RequireRecord(identity.Ordinal).StoredSize;
                if (size < 0 || size > MaximumRetainedSourceBytes - reservedSourceBytes - retainedBytes)
                    return new(null, "The texture exceeds the source memory budget.", CandidateIdentities: [record.Identity])
                    { FailureCode = "TextureSourceBudget" };
                retainedBytes += size;
            }
            var payloads = new List<NativeTexturePayload>();
            foreach (var identity in identities)
            {
                if (reservePayloadMemory) companion.OwnPreparedMetadata(companion.MemoryBudget.Reserve(
                    Snapshot.RequireRecord(identity.Ordinal).StoredSize, "native texture payload"));
                payloads.Add(await ReadPayloadAsync(identity, own.Token).ConfigureAwait(false));
            }
            companion.EnsureUnchanged(); own.Token.ThrowIfCancellationRequested();
            var source = new NativeTextureSource(record.References[0].Resolution.Candidates[0], Revision,
                catalogIdentity, metadata.CatalogSha256, companion.MetadataSource, record, metadata.Bytes,
                Hash(metadata.Bytes.Span), payloads.AsReadOnly(), CompanionLease: companion)
            { NativeTextureIdentity = nativeTextureIdentity, PreviewDimension = maximumPreviewDimension == int.MaxValue ? null : maximumPreviewDimension };
            return new(source, null);
        }
        finally { readGate.Release(); }
    }

    private NativeTextureSource CreateMetadataSource(ResourceIdentity identity, ResourceIdentity catalog,
        NativeContainerLease companion, NativeTextureMetadataPlan metadata)
        => new(metadata.Record.References[0].Resolution.Candidates[0], Revision, catalog, metadata.CatalogSha256,
            companion.MetadataSource, metadata.Record, metadata.Bytes, Hash(metadata.Bytes.Span), [], CompanionLease: companion)
            { NativeTextureIdentity = identity };

    private void ValidateCompanion(NativeContainerLease companion)
    {
        if (companion.Revision != Revision || companion.Snapshot.IndexContentId != Snapshot.IndexContentId ||
            companion.Snapshot.PhysicalSourceStamp != Snapshot.PhysicalSourceStamp ||
            companion.Identity.IndexContentId != Snapshot.IndexContentId || companion.Identity.MemberPath is not null ||
            companion.MetadataSource.Identity != companion.Identity || companion.MetadataSource.Type != 29 ||
            companion.Directory.Identity != companion.Identity || companion.Directory.Revision != Revision)
            throw new ArchiveException("SourceChanged", "The texture companion belongs to another archive revision.");
        companion.EnsureUnchanged();
    }

    private async Task<NativeTextureMetadataPlan?>
        ReadNativeMetadataAsync(ResourceIdentity nativeTextureIdentity, ResourceIdentity catalogIdentity,
            NativeContainerLease companion, CancellationToken token)
    {
        ValidateCompanion(companion);
        var plans = nativePlans.GetValue(companion, _ => new(StringComparer.Ordinal));
        Task<NativeTextureMetadataPlan?> task;
        lock (plans)
        {
            var key = nativeTextureIdentity.Key + "/" + catalogIdentity.Key;
            if (!plans.TryGetValue(key, out task!))
                plans[key] = task = BuildAnyNativeMetadataAsync(nativeTextureIdentity, catalogIdentity, companion, token);
        }
        try { return await task.WaitAsync(token).ConfigureAwait(false); }
        catch { lock (plans) if (task.IsCanceled || task.IsFaulted) plans.TryRemove(nativeTextureIdentity.Key + "/" + catalogIdentity.Key, out _); throw; }
    }

    private async Task<NativeTextureMetadataPlan?> BuildNativeMetadataAsync(ResourceIdentity nativeTextureIdentity,
        ResourceIdentity catalogIdentity, NativeContainerLease companion, CancellationToken token,
        bool reservePlanMemory = true)
    {
        if (nativeTextureIdentity.IndexContentId != Snapshot.IndexContentId ||
            nativeTextureIdentity.Ordinal != companion.Identity.Ordinal || nativeTextureIdentity.MemberPath is null ||
            catalogIdentity.IndexContentId != Snapshot.IndexContentId || catalogIdentity.MemberPath is not null ||
            Snapshot.FindRecord(catalogIdentity.Ordinal)?.Type != 3)
            throw new ArchiveException("InvalidTextureIdentity", "Select a texture and its catalog from this archive.");
        ValidateCompanion(companion);
        var frames = companion.Directory.Objects.Where(frame => frame.Identity == nativeTextureIdentity && frame.NativeType == 2137).ToArray();
        if (frames.Length != 1) return null;
        await references.ValidateSnapshotIndexHashAsync(Snapshot, token).ConfigureAwait(false);
        var table = await references.ReadAsync(Snapshot, catalogIdentity, token).ConfigureAwait(false);
        if (table.Status != LocalizationReferenceReadStatus.Verified || table.ReferenceTableSha256 is null)
            throw new ArchiveException("TextureCatalogUnavailable", "The texture catalog is unavailable.");
        await references.ValidateReferenceTableHashAsync(Snapshot, table, token).ConfigureAwait(false);
        var companions = table.Relationships.Where(relationship => relationship.Reference.ResourceType == 29).ToArray();
        if (companions.Length != 1 || companions[0].Resolution.Status != ReferenceStatus.Unique ||
            companions[0].Resolution.Candidates.Count != 1 || companions[0].NavigationTarget != companion.Identity)
            throw new ArchiveException("TextureSourceMismatch", "The texture companion does not match its catalog.");
        var membership = new TextureCatalogMembershipReader().Read(Snapshot, catalogIdentity, Revision, table.Relationships);
        var frame = frames[0];
        if (!new NativeCommonPrefixReader().TryRead(frame, out var prefix, out _, token) || prefix!.ClassCount != 4)
            return null;
        var end = frame.RawPayload.Length - 12;
        if (end < prefix.NextClassOffset + 96 || !frame.RawPayload.Span.Slice(end, 8).SequenceEqual("FTEXCRAW"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(frame.RawPayload.Span[(end + 8)..]) != 1)
            return null;
        var parsed = new TextureFileTextV24Reader().Parse(frame.RawPayload.Span, prefix.NextClassOffset, end,
            companion.Identity, Revision, frame.PayloadExtent, [companion.MetadataSource.Extent], membership, token);
        if (parsed.Value is not { } record) return null;
        var bytes = frame.RawPayload.Slice(prefix.NextClassOffset,
            checked((int)record.ExactConsumedDecodedExtent.Length));
        if (reservePlanMemory)
            companion.OwnPreparedMetadata(companion.MemoryBudget.Reserve(
                checked(4096L + record.References.Count * 2048L), "native texture source plan"));
        return new(record, bytes, table.ReferenceTableSha256);
    }

    private async Task RevalidateNativeSourceAsync(NativeTextureSource source, CancellationToken token)
    {
        var companion = source.CompanionLease!;
        ValidateCompanion(companion);
        using var borrowed = companion.AcquireLease();
        if (source.NativeTextureIdentity is null || source.Container != companion.MetadataSource ||
            source.Metadata.Revision != Revision || source.Metadata.Identity.Ordinal != companion.Identity.Ordinal ||
            source.MetadataSha256 != Hash(source.MetadataBytes.Span))
            throw new ArchiveException("TextureSourceMismatch", "The texture source does not match its companion metadata.");
        var metadata = await ReadNativeMetadataAsync(source.NativeTextureIdentity, source.CatalogIdentity, companion, token)
            .ConfigureAwait(false);
        if (metadata is null || metadata.CatalogSha256 != source.CatalogSha256 ||
            metadata.Record.Identity != source.Metadata.Identity || Hash(metadata.Bytes.Span) != source.MetadataSha256 ||
            metadata.Record.References.Any(reference => reference.Resolution.Status != ReferenceStatus.Unique ||
                reference.Resolution.Candidates.Count != 1))
            throw new ArchiveException("TextureSourceMismatch", "The texture source does not match its catalog metadata.");
        var selectedReferences = metadata.Record.References.Where(reference =>
            reference.Resolution.Candidates[0] == source.SelectedIdentity).ToArray();
        if (selectedReferences.Length != 1 || source.SelectedIdentity.IndexContentId != Snapshot.IndexContentId ||
            source.SelectedIdentity.MemberPath is not null)
            throw new ArchiveException("TextureSourceMismatch", "The selected texture resource is not referenced by its metadata.");
        var selectedRow = Snapshot.RequireRecord(source.SelectedIdentity.Ordinal);
        if (selectedRow.Type != selectedReferences[0].Reference.ResourceType ||
            !uint.TryParse(selectedReferences[0].Reference.Id, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var selectedId) || selectedRow.Id != selectedId)
            throw new ArchiveException("TextureSourceMismatch", "The selected texture resource does not match its metadata reference.");
        var required = source.PreviewDimension is { } dimension
            ? NativeTextureDecoder.PlanPreview(source, dimension, token).RequiredPayloadIdentities
            : metadata.Record.References.Select(reference => reference.Resolution.Candidates[0]).Distinct().ToArray();
        if (source.Payloads.Count != required.Count || source.Payloads.Any(payload => !required.Contains(payload.Identity)))
            throw new ArchiveException("TextureSourceMismatch", "The texture payload set changed.");
        foreach (var reference in metadata.Record.References.Where(reference => required.Contains(reference.Resolution.Candidates[0])))
        {
            token.ThrowIfCancellationRequested();
            var expected = source.Payloads.Where(payload => payload.Identity == reference.Resolution.Candidates[0]).ToArray();
            if (expected.Length != 1 || expected[0].Sha256 != Hash(expected[0].OriginalBytes.Span))
                throw new ArchiveException("TextureSourceMismatch", "The texture payload does not match its source.");
            await VerifyPayloadAsync(expected[0].Identity, expected[0].Type, expected[0].Id, expected[0].Sha256, token).ConfigureAwait(false);
        }
        companion.EnsureUnchanged();
    }

    private async Task RevalidateLegacyPreviewSourceAsync(NativeTextureSource source, CancellationToken token)
    {
        var companion = source.CompanionLease!;
        ValidateCompanion(companion);
        if (source.Container != companion.MetadataSource || source.Metadata.Revision != Revision ||
            source.MetadataSha256 != Hash(source.MetadataBytes.Span))
            throw new ArchiveException("TextureSourceMismatch", "The legacy texture preview does not match its companion metadata.");
        await using var independent = new NativeTextureSession(catalogs);
        var result = await independent.ReadPreviewAsync(source.SelectedIdentity, companion.MemoryBudget,
            source.PreviewDimension ?? 1024, token, metadataIdentity: source.Metadata.Identity).ConfigureAwait(false);
        var current = result.Source ?? throw new ArchiveException("TextureSourceMismatch",
            result.Reason ?? "The legacy texture metadata is unavailable.");
        try
        {
            if (current.Revision != source.Revision || current.CatalogIdentity != source.CatalogIdentity ||
                current.CatalogSha256 != source.CatalogSha256 || current.Metadata.Identity != source.Metadata.Identity ||
                current.MetadataSha256 != source.MetadataSha256 || current.NativeTextureIdentity != source.NativeTextureIdentity)
                throw new ArchiveException("TextureSourceMismatch", "The legacy texture preview no longer matches its source.");

            if (source.Metadata.References.Any(reference => reference.Resolution is not
                { Status: ReferenceStatus.Unique, Candidates.Count: 1 }))
                throw new ArchiveException("TextureSourceMismatch", "The legacy texture references are no longer unique.");
            var required = source.PreviewDimension is { } dimension
                ? NativeTextureDecoder.PlanPreview(source, dimension, token).RequiredPayloadIdentities
                : source.Metadata.References.Select(reference => reference.Resolution.Candidates.Single()).Distinct().ToArray();
            if (source.Payloads.Count != required.Count || source.Payloads.Any(payload => !required.Contains(payload.Identity)))
                throw new ArchiveException("TextureSourceMismatch", "The legacy texture payload set changed.");
            foreach (var reference in source.Metadata.References.Where(reference => required.Contains(reference.Resolution.Candidates.Single())))
            {
                token.ThrowIfCancellationRequested();
                var matching = source.Payloads.Where(payload => payload.Identity == reference.Resolution.Candidates[0]).ToArray();
                if (matching.Length != 1 || matching[0].Type != reference.Reference.ResourceType ||
                    !uint.TryParse(reference.Reference.Id, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var id) || matching[0].Id != id ||
                    matching[0].Sha256 != Hash(matching[0].OriginalBytes.Span))
                    throw new ArchiveException("TextureSourceMismatch", "A legacy texture payload does not match its metadata.");
                await VerifyPayloadAsync(matching[0].Identity, matching[0].Type, matching[0].Id,
                    matching[0].Sha256, token).ConfigureAwait(false);
            }
            companion.EnsureUnchanged();
        }
        finally { current.CompanionLease?.Dispose(); }
    }

    private async Task VerifyPayloadAsync(ResourceIdentity identity, uint type, uint id, string digest, CancellationToken token)
    {
        var row = Snapshot.RequireRecord(identity.Ordinal);
        if (row.Type != type || row.Id != id || identity.MemberPath is not null || identity.IndexContentId != Snapshot.IndexContentId)
            throw new ArchiveException("TextureSourceMismatch", "The texture payload does not match its metadata.");
        using var verified = await new SourceExtentVerifier().AcquireAsync(Snapshot,
            [new(row.VolumeName, row.Offset, row.StoredSize, digest)], token).ConfigureAwait(false);
        verified.EnsureUnchanged();
    }
}
