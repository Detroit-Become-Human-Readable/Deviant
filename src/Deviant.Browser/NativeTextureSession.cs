using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Containers;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

public sealed record NativeTexturePayload(ResourceIdentity Identity, uint Type, uint Id,
    SourceExtent Extent, ReadOnlyMemory<byte> OriginalBytes, string Sha256);

public sealed record NativeTextureSource(ResourceIdentity SelectedIdentity, SourceRevision Revision,
    ResourceIdentity CatalogIdentity, string CatalogSha256, NativeTexturePayload Container,
    TextureFileTextV24Document Metadata, ReadOnlyMemory<byte> MetadataBytes, string MetadataSha256,
    IReadOnlyList<NativeTexturePayload> Payloads, int CatalogBindingCount = 1,
    NativeContainerLease? CompanionLease = null)
{
    public ResourceIdentity? NativeTextureIdentity { get; init; }
    public int? PreviewDimension { get; init; }
    public long RetainedSourceBytes => (CompanionLease is null ? Container.OriginalBytes.Length : 0L) + MetadataBytes.Length +
        Payloads.Sum(payload => (long)payload.OriginalBytes.Length);
}

public sealed record NativeTextureReadResult(NativeTextureSource? Source, string? Reason, bool Pending = false,
    IReadOnlyList<ResourceIdentity>? CandidateIdentities = null)
{
    public string FailureCode { get; init; } = Pending ? "TextureMetadataPending" : "TextureMetadataUnavailable";
}

/// <summary>Resolves a selected native texture through its verified catalog and companion metadata.</summary>
public sealed partial class NativeTextureSession : IAsyncDisposable
{
    public const string Version = "native-texture-session-v1";
    public const int MaximumResourceBytes = 16 * 1024 * 1024;
    public const long MaximumRetainedSourceBytes = 128L * 1024 * 1024;
    public const long MaximumMetadataScratchBytes = 96L * 1024 * 1024;
    private readonly TextureCatalogSession catalogs;
    private readonly LocalizationReferenceReader references = new();
    private const int ReadWorkers = 4;
    private readonly SemaphoreSlim readGate = new(ReadWorkers, ReadWorkers);
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;

    public NativeTextureSession(TextureCatalogSession catalogs)
    {
        this.catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
    }
    public ArchiveSnapshot Snapshot => catalogs.Snapshot;
    public SourceRevision Revision => catalogs.Revision;
    public AssetMemoryBudget MemoryBudget => catalogs.ReferenceProvider.SharedMemoryBudget;
    public int KnownCatalogCount(ResourceIdentity identity) => catalogs.FindGroupsForTexture(identity).Count;
    public string BindingVersion(ResourceIdentity identity) => Hash(System.Text.Encoding.UTF8.GetBytes(
        Revision.IndexContentId + ":" + Revision.PhysicalSourceStamp + "\n" + string.Join("\n",
            catalogs.FindGroupsForTexture(identity).Select(group => group.CatalogIdentity.Key + ":" + group.ReferenceTableSha256)
                .Order(StringComparer.Ordinal))));

    public static string SourceVersion(NativeTextureSource source) => Hash(System.Text.Encoding.UTF8.GetBytes(
        source.Revision.IndexContentId + ":" + source.Revision.PhysicalSourceStamp + "\n" + source.SelectedIdentity.Key + "\n" +
        source.NativeTextureIdentity?.Key + "\n" + source.Metadata.Identity.Key + ":" + source.MetadataSha256 + "\n" +
        source.CatalogIdentity.Key + ":" + source.CatalogSha256 + "\n" +
        source.PreviewDimension + "\n" +
        string.Join("\n", source.Payloads.Select(payload => payload.Identity.Key + ":" + payload.Sha256).Order(StringComparer.Ordinal))));

    public async Task RevalidateCatalogAsync(ResourceIdentity identity, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var group = catalogs.Groups.SingleOrDefault(item => item.CatalogIdentity == identity)
            ?? throw new ArchiveException("TextureCatalogUnavailable", "The texture catalog is unavailable.");
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await readGate.WaitAsync(own.Token).ConfigureAwait(false);
        try { await ReadCatalogAsync(group, own.Token).ConfigureAwait(false); }
        finally { readGate.Release(); }
    }

    public IDisposable PinSource(NativeTextureSource source)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (source.Revision != Revision) throw new ArchiveException("SourceChanged", "The texture belongs to another archive revision.");
        var handles = new List<IDisposable>();
        var revisions = new FileRevisionProvider();
        try
        {
            if (source.CompanionLease is { } companion)
            {
                ValidateCompanion(companion);
                handles.Add(companion.AcquireLease());
                companion.EnsureUnchanged();
            }
            var index = SourceIdentity.OpenPinned(Snapshot.IndexPath); handles.Add(index);
            SourceIdentity.EnsureUnchanged(revisions, index, Snapshot.IndexPath, Snapshot.IndexStamp);
            var packages = source.Payloads.Prepend(source.Container).Select(item => item.Identity)
                .Append(source.SelectedIdentity).Append(source.CatalogIdentity)
                .Select(identity => Snapshot.RequireRecord(identity.Ordinal).Package).Distinct();
            foreach (var package in packages)
            {
                var volume = Snapshot.Volumes.Single(item => item.Package == package);
                var handle = SourceIdentity.OpenPinned(volume.FilePath); handles.Add(handle);
                SourceIdentity.EnsureUnchanged(revisions, handle, volume.FilePath, volume.Stamp);
            }
            return new SourcePins(handles);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }
    private sealed class SourcePins(List<IDisposable> handles) : IDisposable
    { public void Dispose() { foreach (var handle in handles) handle.Dispose(); } }

    public async Task<NativeTextureReadResult> ReadAsync(ResourceIdentity selected,
        CancellationToken token = default, long reservedSourceBytes = 0)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (reservedSourceBytes < 0 || reservedSourceBytes > MaximumRetainedSourceBytes)
            throw new ArgumentOutOfRangeException(nameof(reservedSourceBytes));
        if (selected.IndexContentId != Snapshot.IndexContentId || selected.MemberPath is not null ||
            Snapshot.FindRecord(selected.Ordinal)?.Type is not (2229 or 2230))
            throw new ArgumentException("Select a texture from this archive.", nameof(selected));
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await readGate.WaitAsync(own.Token).ConfigureAwait(false);
        try
        {
            var discovery = await DiscoverAsync(selected, own.Token).ConfigureAwait(false);
            if (discovery.Candidate is not { } candidate)
                return new(null, discovery.Reason, discovery.Pending, discovery.Identities)
                { FailureCode = discovery.Code };
            var identities = candidate.Metadata.References.Select(item => item.Resolution.Candidates[0]).Distinct().ToArray();
            var retainedBytes = candidate.MetadataBytes.Length +
                Snapshot.RequireRecord(candidate.ContainerIdentity.Ordinal).StoredSize;
            foreach (var identity in identities)
            {
                var size = Snapshot.RequireRecord(identity.Ordinal).StoredSize;
                if (size < 0 || size > MaximumRetainedSourceBytes - reservedSourceBytes - retainedBytes)
                    return new(null, "The texture exceeds the source memory budget.", CandidateIdentities: [candidate.Metadata.Identity])
                    { FailureCode = "TextureSourceBudget" };
                retainedBytes += size;
            }
            var container = await ReadPayloadAsync(candidate.ContainerIdentity, own.Token).ConfigureAwait(false);
            if (container.Sha256 != candidate.ContainerSha256)
                throw new ArchiveException("SourceChanged", "The texture metadata source changed. Reopen the archive.");
            var payloads = new List<NativeTexturePayload>();
            foreach (var identity in identities) payloads.Add(await ReadPayloadAsync(identity, own.Token).ConfigureAwait(false));
            own.Token.ThrowIfCancellationRequested();
            return new(new(selected, Revision, candidate.CatalogIdentity, candidate.CatalogSha256, container,
                candidate.Metadata, candidate.MetadataBytes, candidate.MetadataSha256, payloads.AsReadOnly(), discovery.BindingCount), null);
        }
        finally { readGate.Release(); }
    }

    private sealed record MetadataCandidate(ResourceIdentity CatalogIdentity, string CatalogSha256,
        ResourceIdentity ContainerIdentity, string ContainerSha256, TextureFileTextV24Document Metadata,
        byte[] MetadataBytes, string MetadataSha256);
    private sealed record MetadataDiscovery(MetadataCandidate? Candidate, string? Reason,
        bool Pending, IReadOnlyList<ResourceIdentity>? Identities, int BindingCount)
    {
        public string Code { get; init; } = Pending ? "TextureMetadataPending" : "TextureMetadataUnavailable";
    }

    private async Task<MetadataDiscovery> DiscoverAsync(ResourceIdentity selected, CancellationToken token)
    {
            MetadataCandidate? candidate = null;
            var groups = catalogs.FindGroupsForTexture(selected);
            if (groups.Count == 0 && !catalogs.EnrichmentTask.IsCompleted)
                return new(null, "Reading texture metadata…", true, null, groups.Count);
            foreach (var group in groups)
            {
                token.ThrowIfCancellationRequested();
                var table = await ReadCatalogAsync(group, token).ConfigureAwait(false);
                var companions = table.Relationships.Where(item => item.Reference.ResourceType == 29).ToArray();
                if (companions.Length != 1 || companions[0].NavigationTarget is not { } companion) continue;
                var container = await ReadPayloadAsync(companion, token).ConfigureAwait(false);
                var segs = new SegsContainerReader().Parse(container.OriginalBytes.Span, companion, Revision,
                    container.Extent, limits: new(MaximumResourceBytes, MaximumResourceBytes), cancellationToken: token);
                if (!TryGetBody(segs, token, out var body, out var bodyExtent)) continue;
                var membership = new TextureCatalogMembership(group.CatalogIdentity, Revision, group.CatalogId,
                    group.CatalogExtent, group.SourceReferenceCount, group.SourceReferenceCount - group.References.Count,
                    group.References);
                foreach (var record in FindMetadata(body, bodyExtent!, container.Extent, membership, token))
                {
                    if (!record.References.Any(reference => reference.Resolution.Status == ReferenceStatus.Unique &&
                        reference.Resolution.Candidates.Count == 1 && reference.Resolution.Candidates[0] == selected)) continue;
                    if (record.References.Any(reference => reference.Resolution.Status != ReferenceStatus.Unique ||
                        reference.Resolution.Candidates.Count != 1))
                        return new(null, "Texture references are missing or ambiguous.", false, [record.Identity], groups.Count)
                        { Code = "TextureReferenceUnavailable" };
                    var offset = checked((int)(record.ExactConsumedDecodedExtent.Offset - bodyExtent!.Offset));
                    var bytes = body.AsMemory(offset, checked((int)record.ExactConsumedDecodedExtent.Length));
                    var hash = Hash(bytes.Span);
                    if (candidate is not null && (candidate.Metadata.Identity != record.Identity || candidate.MetadataSha256 != hash))
                        return new(null, "Multiple texture records reference this resource.", false,
                            [candidate.Metadata.Identity, record.Identity], groups.Count)
                        { Code = "TextureMetadataAmbiguous" };
                    candidate ??= new(group.CatalogIdentity, group.ReferenceTableSha256, companion, container.Sha256,
                        record, bytes.ToArray(), hash);
                }
            }
            token.ThrowIfCancellationRequested();
            return new(candidate, candidate is not null ? null : groups.Count == 0 ?
                "No texture metadata catalog is available." : "Texture metadata is unavailable.", false,
                candidate is null ? null : [candidate.Metadata.Identity], groups.Count);
    }

    public async Task RevalidateAsync(NativeTextureSource source, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (source.CompanionLease is not null && source.NativeTextureIdentity is null)
        {
            await RevalidateLegacyPreviewSourceAsync(source, token).ConfigureAwait(false);
            return;
        }
        await readGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
        if (source.Revision != Revision || source.SelectedIdentity.IndexContentId != Snapshot.IndexContentId)
            throw new ArchiveException("SourceChanged", "The texture belongs to another archive revision.");
        if (source.CompanionLease is not null)
        {
            await RevalidateNativeSourceAsync(source, token).ConfigureAwait(false);
            return;
        }
        if (KnownCatalogCount(source.SelectedIdentity) != source.CatalogBindingCount)
        {
            var current = (await DiscoverAsync(source.SelectedIdentity, token).ConfigureAwait(false)).Candidate;
            if (current is null || current.Metadata.Identity != source.Metadata.Identity || current.MetadataSha256 != source.MetadataSha256)
                throw new ArchiveException("TextureSourceMismatch", "The texture metadata references became ambiguous.");
        }
        var group = catalogs.Groups.SingleOrDefault(item => item.CatalogIdentity == source.CatalogIdentity);
        if (group is null || source.CatalogSha256 != group.ReferenceTableSha256 ||
            source.Metadata.Revision != Revision || source.Metadata.Identity.Ordinal != source.Container.Identity.Ordinal ||
            source.MetadataSha256 != Hash(source.MetadataBytes.Span) ||
            source.MetadataBytes.Length < 96 || !source.Metadata.RawFixedPrefix84.AsSpan().SequenceEqual(source.MetadataBytes.Span.Slice(12, 84)) ||
            !source.Payloads.Any(item => item.Identity == source.SelectedIdentity))
            throw new ArchiveException("TextureSourceMismatch", "The texture source does not match its catalog metadata.");
        await ReadCatalogAsync(group, token).ConfigureAwait(false);
        foreach (var expected in source.Payloads.Prepend(source.Container))
        {
            var actual = await ReadPayloadAsync(expected.Identity, token).ConfigureAwait(false);
            if (actual.Sha256 != expected.Sha256)
                throw new ArchiveException("SourceChanged", "The texture source changed. Reopen the archive.");
        }
        await references.ValidateSnapshotIndexHashAsync(Snapshot, token).ConfigureAwait(false);
        }
        finally { readGate.Release(); }
    }

    private async Task<NativeTexturePayload> ReadPayloadAsync(ResourceIdentity identity, CancellationToken token)
    {
        var row = Snapshot.RequireRecord(identity.Ordinal);
        if (row.Validity != ResourceValidity.Valid || row.StoredSize < 1 || row.StoredSize > MaximumResourceBytes)
            throw new ArchiveException("TextureResourceLimit", "The texture source exceeds the supported resource limit.");
        var bytes = await new ArchiveResourceResolver(Snapshot).ReadAsync(identity, 0, checked((int)row.StoredSize), token).ConfigureAwait(false);
        var volume = Snapshot.Volumes.Single(item => item.Package == row.Package);
        var hash = Hash(bytes);
        return new(identity, row.Type, row.Id, new(volume.RelativeName, row.Offset, row.StoredSize, hash), bytes, hash);
    }
    private async Task<LocalizationReferenceReadResult> ReadCatalogAsync(TextureCatalogGroup group, CancellationToken token)
    {
        await references.ValidateSnapshotIndexHashAsync(Snapshot, token).ConfigureAwait(false);
        var table = await references.ReadAsync(Snapshot, group.CatalogIdentity, token).ConfigureAwait(false);
        if (table.Status != LocalizationReferenceReadStatus.Verified || table.ReferenceTableSha256 != group.ReferenceTableSha256)
            throw new ArchiveException("SourceChanged", "The texture catalog changed. Reopen the archive.");
        await references.ValidateReferenceTableHashAsync(Snapshot, table, token).ConfigureAwait(false);
        return table;
    }

    private static bool TryGetBody(DecodeResult<SegsDocument> container, CancellationToken token,
        out byte[] bytes, out DecodedExtent? extent)
    {
        bytes = []; extent = null;
        var prefix = container.Value?.DecodedPrefix;
        if (prefix is null || prefix.Length < 24 || !prefix.AsSpan(0, 8).SequenceEqual("DC_INFO "u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(8)) != 3) return false;
        var infoLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(12));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(20));
        if (infoLength != 8L + count * 8L || infoLength > prefix.Length - 16) return false;
        var dataOffset = checked(16L + infoLength);
        var nested = new RawNestedRecordReader().Inspect(container, dataOffset, token).Value;
        if (nested is null || nested.TagText != "DC_DATA " || nested.Version != 23 ||
            nested.DeclaredBodyLength > MaximumResourceBytes || nested.BodySlices.Count == 0) return false;
        bytes = new byte[checked((int)nested.DeclaredBodyLength)];
        long cursor = 0;
        foreach (var slice in nested.BodySlices)
        {
            token.ThrowIfCancellationRequested();
            if (slice.BodyOffset != cursor || slice.Bytes.Length > bytes.Length - cursor) return false;
            slice.Bytes.Span.CopyTo(bytes.AsSpan(checked((int)cursor))); cursor += slice.Bytes.Length;
        }
        if (cursor != bytes.Length) return false;
        extent = new(container.Identity, dataOffset + 16, bytes.Length);
        return true;
    }

    private static IEnumerable<TextureFileTextV24Document> FindMetadata(byte[] body, DecodedExtent extent,
        SourceExtent physical, TextureCatalogMembership membership, CancellationToken token)
    {
        for (var cursor = 0; cursor <= body.Length - 12; cursor++)
        {
            if ((cursor & 4095) == 0) token.ThrowIfCancellationRequested();
            if (!body.AsSpan(cursor, 8).SequenceEqual("FILETEXT"u8) ||
                BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(cursor + 8)) != 24) continue;
            var maximum = Math.Min(body.Length - 12, cursor + TextureFileTextV24Reader.MaximumRecordBytes);
            for (var end = cursor + 12; end <= maximum; end++)
            {
                if ((end & 4095) == 0) token.ThrowIfCancellationRequested();
                if (!body.AsSpan(end, 8).SequenceEqual("FTEXCRAW"u8) ||
                    BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(end + 8)) != 1) continue;
                var candidate = new TextureFileTextV24Reader().Parse(body, cursor, end, extent.Container,
                    membership.Revision, extent, [physical], membership, token);
                if (candidate.Value is { } record) { cursor = end - 1; yield return record; }
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; lifetime.Cancel();
        for (var worker = 0; worker < ReadWorkers; worker++) await readGate.WaitAsync().ConfigureAwait(false);
        readGate.Release(ReadWorkers);
        references.Dispose();
        readGate.Dispose(); lifetime.Dispose();
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
