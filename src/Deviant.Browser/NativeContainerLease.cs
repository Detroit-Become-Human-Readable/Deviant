using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Containers;

namespace Deviant.Browser;

public sealed record NativeContainerSegmentLayout(int DecodedPrefixLength,
    SourceExtent? RawSuffixExtent, int RawSuffixLength);

internal sealed record NativeContainerMetadataBody(ReadOnlyMemory<byte> Bytes,
    IReadOnlyList<SourceExtent> PhysicalExtents);

/// <summary>One source read and native-directory decode shared by an asset load.</summary>
public sealed class NativeContainerLease : IDisposable
{
    public const int MaximumStoredBytes = 128 * 1024 * 1024;
    public const int MaximumDecodedBytes = 256 * 1024 * 1024;
    public const int MaximumLargeStoredBytes = 512 * 1024 * 1024;
    public const int MaximumLargeDecodedBytes = 1024 * 1024 * 1024;
    private readonly object sync = new();
    private List<IDisposable>? resources;
    private NativeTexturePayload? storedSource;
    private DecodeResult<SegsDocument>? segments;
    private NativeObjectDirectoryDocument? directory;
    private readonly SourceExtentVerificationLease verification;
    private readonly AssetMemoryBudget.Reservation compressionMemory;
    private readonly List<SourceExtent> sourceExtents;
    private readonly List<SourceExtentVerificationLease> additionalVerifications = [];
    private int references = 1;
    private bool ownerDisposed;
    private long preparedMetadataBytes;
    private readonly int? metadataDecodedPrefixLength;
    private readonly bool metadataSourceIsDirect;

    private NativeContainerLease(ArchiveSnapshot snapshot, NativeTexturePayload source,
        DecodeResult<SegsDocument>? segments, NativeObjectDirectoryDocument directory,
        SourceExtentVerificationLease verification, AssetMemoryBudget budget, List<IDisposable> resources,
        AssetMemoryBudget.Reservation compressionMemory, IReadOnlyList<SourceExtent>? physicalExtents = null)
    {
        Snapshot = snapshot; storedSource = source; this.segments = segments; this.directory = directory;
        this.verification = verification; this.resources = resources; MemoryBudget = budget;
        Identity = source.Identity; Revision = directory.Revision;
        this.compressionMemory = compressionMemory;
        IsMetadataOnly = directory.IsMetadataOnly;
        metadataDecodedPrefixLength = segments?.Value?.DecodedPrefix.Length;
        metadataSourceIsDirect = source.OriginalBytes.Span.StartsWith("QZIP\0"u8);
        sourceExtents = (physicalExtents ?? segments?.Extents ?? [source.Extent]).ToList();
        if (IsMetadataOnly)
            DataLayout = new(checked((int)(directory.BodyExtent.Offset + directory.RawBody.Length)), null, 0);
        else if (segments?.Value is { } decoded)
            DataLayout = new(decoded.DecodedPrefix.Length, decoded.RawSuffixExtent, decoded.RawSuffix.Length);
        else if (directory.RemainderExtent is { } remainder)
        {
            var bytes = directory.RemainderBytes;
            DataLayout = new(checked((int)remainder.Offset), new(source.Extent.RelativeSource,
                checked(source.Extent.Offset + 5 + remainder.Offset), bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant()), bytes.Length);
        }
        else DataLayout = new(checked((int)(directory.BodyExtent.Offset + directory.BodyExtent.Length)), null, 0);
    }

    public ArchiveSnapshot Snapshot { get; }
    public ResourceIdentity Identity { get; }
    public SourceRevision Revision { get; }
    public AssetMemoryBudget MemoryBudget { get; }
    public NativeContainerSegmentLayout DataLayout { get; }
    public NativeContainerSegmentLayout SegmentLayout => DataLayout;
    public IReadOnlyList<SourceExtent> SourceExtents { get { lock (sync) return sourceExtents.ToArray(); } }
    public IReadOnlyList<SourceExtent> PhysicalExtents => SourceExtents;
    public bool IsMetadataOnly { get; }
    public long PreparedMetadataBytes { get { lock (sync) return preparedMetadataBytes; } }
    internal long RetainedReservationBytes
    {
        get
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(resources is null, this);
                return resources.OfType<AssetMemoryBudget.Reservation>().Sum(item => item.Bytes);
            }
        }
    }
    internal NativeTexturePayload MetadataSource => storedSource ?? throw new ObjectDisposedException(nameof(NativeContainerLease));
    internal int? MetadataDecodedPrefixLength => metadataDecodedPrefixLength;
    internal bool MetadataSourceIsDirect => metadataSourceIsDirect;
    public NativeTexturePayload StoredSource => IsMetadataOnly
        ? throw new InvalidOperationException("Open the complete container before reading its data buffers.")
        : storedSource ?? throw new ObjectDisposedException(nameof(NativeContainerLease));
    public DecodeResult<SegsDocument>? Segments => segments;
    public NativeObjectDirectoryDocument Directory => directory ?? throw new ObjectDisposedException(nameof(NativeContainerLease));

    public static async Task<NativeContainerLease> OpenMetadataAsync(ArchiveSnapshot snapshot, ResourceIdentity identity,
        AssetMemoryBudget budget, CancellationToken token = default, bool allowLargeData = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(budget); token.ThrowIfCancellationRequested();
        if (identity.IndexContentId != snapshot.IndexContentId || identity.MemberPath is not null ||
            identity.Ordinal < 0 || identity.Ordinal >= snapshot.Records.Count)
            throw new ArgumentException("Select a native container from this archive.", nameof(identity));
        var resources = new List<IDisposable>();
        int maximumStored = allowLargeData ? MaximumLargeStoredBytes : MaximumStoredBytes;
        int maximumDecoded = allowLargeData ? MaximumLargeDecodedBytes : MaximumDecodedBytes;
        try
        {
            var read = await NativeContainerMetadataReader.ReadAsync(snapshot, identity, budget, resources,
                maximumStored, maximumDecoded, token).ConfigureAwait(false);
            using var verificationScratch = budget.Reserve(256 * 1024, "container metadata verification scratch");
            var pin = await new SourceExtentVerifier().AcquireAsync(snapshot, read.PhysicalExtents, token,
                maximumBytes: maximumStored).ConfigureAwait(false);
            resources.Add(pin); token.ThrowIfCancellationRequested();
            return new(snapshot, read.Source, read.Segments, read.Directory, pin, budget, resources,
                read.CompressionMemory, read.PhysicalExtents);
        }
        catch
        {
            for (int i = resources.Count - 1; i >= 0; i--) resources[i].Dispose();
            throw;
        }
    }

    public void RequireDataBuffers()
    {
        EnsureUnchanged();
        if (IsMetadataOnly) throw new InvalidOperationException("Open the complete container before reading its data buffers.");
    }

    internal async Task<NativeContainerMetadataBody> ReadDeclaredMetadataBodyAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        EnsureUnchanged();
        var extent = Directory.BodyExtent;
        if (extent.Length < 0 || extent.Length > NativeTextureSession.MaximumResourceBytes ||
            extent.Length > int.MaxValue || extent.Offset < 0 || extent.Offset > long.MaxValue - extent.Length)
            throw new ArchiveException("NativeMetadataLimit", "The declared metadata body exceeds the supported limit.");
        if (extent.Length == 0) return new(ReadOnlyMemory<byte>.Empty, SourceExtents);

        var decoded = Segments?.Value?.DecodedPrefix;
        var start = checked((int)Math.Min(extent.Offset, int.MaxValue));
        int decodedCount = decoded is null || start >= decoded.Length
            ? 0 : checked((int)Math.Min(extent.Length, decoded.Length - (long)start));
        if (decodedCount == extent.Length)
            return new(decoded.AsMemory(start, decodedCount), SourceExtents);

        AssetMemoryBudget.Reservation? bodyReservation = MemoryBudget.Reserve(extent.Length, "declared texture metadata body");
        try
        {
            var bytes = new byte[checked((int)extent.Length)];
            if (decodedCount > 0) decoded!.AsMemory(start, decodedCount).CopyTo(bytes);
            long rawLogicalOffset = checked(extent.Offset + decodedCount);
            int remaining = bytes.Length - decodedCount;
            long sourceOffset = decoded is null
                ? checked(5L + rawLogicalOffset)
                : checked(MetadataSource.Extent.Length + rawLogicalOffset - decoded.Length);
            if (sourceOffset < 0 || sourceOffset > Snapshot.RequireRecord(Identity.Ordinal).StoredSize - remaining)
                throw new ArchiveException("NativeMetadataUnavailable", "The declared metadata body exceeds its stored source.");

            var stored = MetadataSource.OriginalBytes;
            if (sourceOffset <= stored.Length - (long)remaining)
                stored.Span.Slice(checked((int)sourceOffset), remaining).CopyTo(bytes.AsSpan(decodedCount));
            else
            {
                using var scratch = MemoryBudget.Reserve(Math.Min(4L * 1024 * 1024, remaining), "metadata body read scratch");
                var resolver = new ArchiveResourceResolver(Snapshot);
                int cursor = 0;
                while (cursor < remaining)
                {
                    token.ThrowIfCancellationRequested();
                    int length = Math.Min(4 * 1024 * 1024, remaining - cursor);
                    var chunk = await resolver.ReadAsync(Identity, sourceOffset + cursor, length, token).ConfigureAwait(false);
                    chunk.CopyTo(bytes, decodedCount + cursor); cursor += length;
                }

                var row = Snapshot.RequireRecord(Identity.Ordinal);
                var volume = Snapshot.Volumes.Single(item => item.Package == row.Package);
                var physical = new SourceExtent(volume.RelativeName, checked(row.Offset + sourceOffset), remaining,
                    Convert.ToHexString(SHA256.HashData(bytes.AsSpan(decodedCount, remaining))).ToLowerInvariant());
                var pin = await new SourceExtentVerifier().AcquireAsync(Snapshot, [physical], token,
                    maximumBytes: Math.Max(1, remaining)).ConfigureAwait(false);
                try { OwnVerifiedExtent(physical, pin); }
                catch { pin.Dispose(); throw; }
            }

            token.ThrowIfCancellationRequested();
            OwnPreparedMetadata(bodyReservation);
            bodyReservation = null;
            EnsureUnchanged();
            return new(bytes, SourceExtents);
        }
        finally { bodyReservation?.Dispose(); }
    }

    internal void OwnVerifiedExtent(SourceExtent extent, SourceExtentVerificationLease pin)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(resources is null, this);
            sourceExtents.Add(extent);
            additionalVerifications.Add(pin);
            resources.Add(pin);
        }
    }

    public static async Task<NativeContainerLease> OpenAsync(ArchiveSnapshot snapshot, ResourceIdentity identity,
        AssetMemoryBudget budget, CancellationToken token = default, bool allowLargeData = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(budget); token.ThrowIfCancellationRequested();
        if (identity.IndexContentId != snapshot.IndexContentId || identity.MemberPath is not null ||
            identity.Ordinal < 0 || identity.Ordinal >= snapshot.Records.Count)
            throw new ArgumentException("Select a native container from this archive.", nameof(identity));
        var maximumStoredBytes = allowLargeData ? MaximumLargeStoredBytes : MaximumStoredBytes;
        var maximumDecodedBytes = allowLargeData ? MaximumLargeDecodedBytes : MaximumDecodedBytes;
        var row = snapshot.Records[checked((int)identity.Ordinal)];
        if (row.Type != 29 || row.Validity != ResourceValidity.Valid || row.StoredSize < 16 || row.StoredSize > maximumStoredBytes)
            throw new ArchiveException("NativeContainerLimit", "The native container is unavailable or exceeds its size limit.");
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        var resources = new List<IDisposable>();
        try
        {
            resources.Add(budget.Reserve(checked(row.StoredSize + 1024 * 1024), "container source"));
            var source = new ArchiveResourceResolver(snapshot);
            var bytes = new byte[checked((int)row.StoredSize)];
            using (budget.Reserve(4 * 1024 * 1024, "container read scratch"))
            {
                for (var offset = 0; offset < bytes.Length;)
                {
                    token.ThrowIfCancellationRequested();
                    var length = Math.Min(4 * 1024 * 1024, bytes.Length - offset);
                    var chunk = await source.ReadAsync(identity, offset, length, token).ConfigureAwait(false);
                    chunk.CopyTo(bytes, offset);
                    offset += length;
                }
            }
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var extent = new SourceExtent(row.VolumeName, row.Offset, bytes.Length, hash);
            var direct = bytes.Length >= 5 && bytes.AsSpan(0, 5).SequenceEqual("QZIP\0"u8);
            var compressed = bytes.Length >= 16 && bytes.AsSpan(0, 4).SequenceEqual("segs"u8);
            if (!direct && !compressed)
                throw new ArchiveException("NativeContainerSignature", "The native container format is unavailable.");
            var decodeReservation = budget.Reserve(direct ? 0 : EstimateSegsMemory(bytes, maximumDecodedBytes), "container decoding");
            resources.Add(decodeReservation);
            DecodeResult<SegsDocument>? result = null;
            ReadOnlyMemory<byte> prefix;
            long retainedDirectoryBytes;
            if (direct)
            {
                prefix = bytes.AsMemory(5);
                retainedDirectoryBytes = 0;
            }
            else
            {
                result = new SegsContainerReader().Parse(bytes, identity, revision, extent, row.AlternativeSize,
                    new(maximumStoredBytes, maximumDecodedBytes), token);
                var decoded = result.Value ?? throw new ArchiveException("NativeContainerUnavailable", string.Join("; ", result.Diagnostics));
                decodeReservation.ReduceTo(SegsRetainedBytes(decoded));
                prefix = decoded.DecodedPrefix;
                retainedDirectoryBytes = checked((long)decoded.DecodedPrefix.Length + decoded.RawSuffix.Length);
            }
            if (prefix.Length < 24)
                throw new ArchiveException("NativeDirectoryUnavailable", "The native directory is truncated.");
            // The shared directory reader validates the count and all framing.
            var count = BinaryPrimitives.ReadUInt32LittleEndian(prefix.Span[20..]);
            if (count > NativeObjectDirectoryReader.MaximumObjects)
                throw new ArchiveException("NativeDirectoryLimit", "The native directory exceeds its object limit.");
            resources.Add(budget.Reserve(checked(retainedDirectoryBytes + count * 2048L + 1024 * 1024), "native directory"));
            var loader = new NativeObjectDirectoryLoader();
            var parsed = direct ? loader.ReadDirect(bytes, identity, revision, extent, token) : loader.Read(result!, token);
            var document = parsed.Value ?? throw new ArchiveException("NativeDirectoryUnavailable", string.Join("; ", parsed.Diagnostics));
            var pin = await new SourceExtentVerifier().AcquireAsync(snapshot, [extent], token,
                maximumBytes: maximumStoredBytes).ConfigureAwait(false);
            resources.Add(pin);
            token.ThrowIfCancellationRequested();
            return new(snapshot, new(identity, row.Type, row.Id, extent, bytes, hash), result, document, pin, budget, resources, decodeReservation);
        }
        catch
        {
            for (var index = resources.Count - 1; index >= 0; index--) resources[index].Dispose();
            throw;
        }
    }

    internal static long EstimateSegsMemory(ReadOnlySpan<byte> bytes, long maximumDecodedBytes)
    {
        if (bytes.Length < SegsContainerReader.HeaderLength)
            throw new ArchiveException("NativePayloadTruncated", "The compressed payload is truncated.");
        if (!bytes[..4].SequenceEqual("segs"u8))
            throw new ArchiveException("NativePayloadSignature", "The compressed payload format is unavailable.");
        // Reserve before the authoritative SEGS parse creates chunk and prefix arrays.
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (declared == 0 || declared > maximumDecodedBytes)
            throw new ArchiveException("NativePayloadLimit", "The compressed payload exceeds its decode limit.");
        var chunks = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        return checked(bytes.Length + 2L * declared + chunks * 512L + (chunks + 1L) * 128 + 256 * 1024);
    }

    internal static long SegsRetainedBytes(SegsDocument value) => checked(
        value.Chunks.Sum(chunk => (long)chunk.DecodedBytes.Length + 512) +
        value.Gaps.Sum(gap => (long)gap.Bytes.Length + 128) +
        value.DecodedPrefix.Length + value.RawSuffix.Length + 256 * 1024);

    public void EnsureUnchanged()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(resources is null, this);
            verification.EnsureUnchanged();
            foreach (var additional in additionalVerifications) additional.EnsureUnchanged();
        }
    }

    internal void ReleaseCompressionBuffers()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(resources is null, this);
            segments = null;
            compressionMemory.Dispose();
        }
    }

    public IDisposable AcquireLease()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(resources is null, this);
            checked { references++; }
            return new BorrowedLease(this);
        }
    }

    internal void OwnPreparedMetadata(AssetMemoryBudget.Reservation resource)
    {
        lock (sync)
        {
            if (resources is null) { resource.Dispose(); throw new ObjectDisposedException(nameof(NativeContainerLease)); }
            preparedMetadataBytes = checked(preparedMetadataBytes + resource.Bytes);
            resources.Add(resource);
        }
    }

    public void Dispose()
    {
        lock (sync) { if (ownerDisposed) return; ownerDisposed = true; }
        Release();
    }

    private void Release()
    {
        List<IDisposable>? released = null;
        lock (sync)
        {
            if (--references != 0) return;
            released = resources; resources = null; storedSource = null; segments = null; directory = null;
        }
        if (released is not null)
            for (var index = released.Count - 1; index >= 0; index--) released[index].Dispose();
    }

    private sealed class BorrowedLease(NativeContainerLease owner) : IDisposable
    {
        private NativeContainerLease? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Release();
    }
}
