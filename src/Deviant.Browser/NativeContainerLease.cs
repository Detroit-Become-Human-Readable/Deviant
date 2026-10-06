using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Containers;

namespace Deviant.Browser;

public sealed record NativeContainerSegmentLayout(int DecodedPrefixLength,
    SourceExtent? RawSuffixExtent, int RawSuffixLength);

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
    private int references = 1;
    private bool ownerDisposed;

    private NativeContainerLease(ArchiveSnapshot snapshot, NativeTexturePayload source,
        DecodeResult<SegsDocument>? segments, NativeObjectDirectoryDocument directory,
        SourceExtentVerificationLease verification, AssetMemoryBudget budget, List<IDisposable> resources,
        AssetMemoryBudget.Reservation compressionMemory)
    {
        Snapshot = snapshot; storedSource = source; this.segments = segments; this.directory = directory;
        this.verification = verification; this.resources = resources; MemoryBudget = budget;
        Identity = source.Identity; Revision = directory.Revision;
        this.compressionMemory = compressionMemory;
        SourceExtents = segments?.Extents ?? [source.Extent];
        if (segments?.Value is { } decoded)
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
    public IReadOnlyList<SourceExtent> SourceExtents { get; }
    public NativeTexturePayload StoredSource => storedSource ?? throw new ObjectDisposedException(nameof(NativeContainerLease));
    public DecodeResult<SegsDocument>? Segments => segments;
    public NativeObjectDirectoryDocument Directory => directory ?? throw new ObjectDisposedException(nameof(NativeContainerLease));

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

    public void EnsureUnchanged() { lock (sync) { ObjectDisposedException.ThrowIf(resources is null, this); verification.EnsureUnchanged(); } }

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
