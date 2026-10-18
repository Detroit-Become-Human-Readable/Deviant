using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

/// <summary>Retains verified RGBA bytes on disk and admits each pixel read through the session budget.</summary>
public sealed class PreparedTexturePixelSource : ITexturePixelSource, IDisposable
{
    private const string Category = "texture-pixels";
    private const string Profile = "prepared-texture-pixels-v1";
    private const int PrefixBytes = 16;
    private const int MaximumHeaderBytes = 64 * 1024;
    private const int ChunkBytes = 64 * 1024;
    private const long ValidationScratchBytes = MaximumHeaderBytes * 4L + ChunkBytes + 8192;
    private static readonly JsonSerializerOptions JsonOptions = new()
    { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly FileRevisionProvider Revisions = new();
    private readonly object gate = new();
    private readonly AssetMemoryBudget budget;
    private readonly FileStamp stamp;
    private readonly long offset;
    private FileStream? input;
    private IDisposable? cacheLease;
    private AssetMemoryBudget.Reservation? metadata;
    private int activeReads;
    private bool disposed;

    private PreparedTexturePixelSource(FileStream input, IDisposable? cacheLease, FileStamp stamp,
        long offset, int byteLength, string rgbaSha256, AssetMemoryBudget budget,
        AssetMemoryBudget.Reservation metadata, bool previewWasResized)
    {
        this.input = input; this.cacheLease = cacheLease; this.stamp = stamp; this.offset = offset;
        ByteLength = byteLength; RgbaSha256 = rgbaSha256; this.budget = budget; this.metadata = metadata;
        PreviewWasResized = previewWasResized;
    }

    public TextureSurface Surface { get; private set; } = null!;
    public bool PreviewWasResized { get; }
    public long ByteLength { get; }
    public string RgbaSha256 { get; }

    public static async Task<PreparedTexturePixelSource> StoreAsync(CacheManager cache, string key,
        TextureSurface surface, string rgbaSha256, AssetMemoryBudget budget, bool previewWasResized = false,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(cache); ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(budget); ValidateKey(key); ValidateDigest(rgbaSha256);
        token.ThrowIfCancellationRequested();
        if (surface.IsHdr || !surface.HasRgba8)
            throw new TextureFormatException("TexturePixelsUnavailable", "Storing texture pixels requires resident SDR RGBA bytes.");
        using var context = budget.Reserve(32 * 1024, "texture pixel backing metadata");
        var header = Header.From(key, surface, rgbaSha256.ToLowerInvariant(), previewWasResized);
        ValidateHeader(header, key);
        if (!string.Equals(Hash(surface.Rgba8.Span, token), header.RgbaSha256, StringComparison.Ordinal))
            throw Changed("The resident RGBA hash does not match the requested backing.");
        var cacheKey = CacheManager.PreparedKey(Category, Profile, key);
        var lease = await cache.AcquirePreparedOutputAsync(Category, cacheKey, ".pixels",
            (output, cancellation) => WriteAsync(output, header, surface.Rgba8, budget, cancellation),
            (path, cancellation) => ValidArtifactAsync(path, key, budget, header, cancellation), token).ConfigureAwait(false);
        return await OpenCachedAsync(lease, key, budget, header, token).ConfigureAwait(false);
    }

    public static async Task<PreparedTexturePixelSource?> TryAcquireAsync(CacheManager cache, string key,
        AssetMemoryBudget budget, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(cache); ArgumentNullException.ThrowIfNull(budget); ValidateKey(key);
        token.ThrowIfCancellationRequested();
        var cacheKey = CacheManager.PreparedKey(Category, Profile, key);
        var lease = await cache.TryAcquirePreparedAsync(Category, cacheKey, ".pixels",
            (path, cancellation) => ValidArtifactAsync(path, key, budget, null, cancellation), token).ConfigureAwait(false);
        return lease is null ? null : await OpenCachedAsync(lease, key, budget, null, token).ConfigureAwait(false);
    }

    public static async Task<PreparedTexturePixelSource> OpenRangeAsync(string path, long offset, int byteLength,
        string rgbaSha256, Func<ITexturePixelSource, TextureSurface> surfaceFactory, AssetMemoryBudget budget,
        bool previewWasResized = false, CancellationToken token = default, Func<IDisposable>? acquireBackingOwner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); ArgumentNullException.ThrowIfNull(surfaceFactory);
        ArgumentNullException.ThrowIfNull(budget); ValidateDigest(rgbaSha256); token.ThrowIfCancellationRequested();
        if (offset < 0 || byteLength < 1 || byteLength > DdsTextureReader.MaximumPixelCount * 4L)
            throw new InvalidDataException("The prepared pixel range is outside the supported bounds.");
        FileStream? file = null;
        IDisposable? backingOwner = null;
        AssetMemoryBudget.Reservation? retained = null;
        PreparedTexturePixelSource? owner = null;
        try
        {
            backingOwner = acquireBackingOwner?.Invoke();
            file = OpenPinned(path);
            var stamp = Capture(file);
            if (offset > stamp.Length - byteLength) throw new InvalidDataException("The prepared pixel range exceeds its file.");
            using (budget.Reserve(ChunkBytes + 4096L, "texture pixel range validation"))
            {
                var buffer = new byte[ChunkBytes];
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long read = 0;
                while (read < byteLength)
                {
                    token.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(buffer.Length, byteLength - read);
                    await ReadAtAsync(file, buffer.AsMemory(0, count), checked(offset + read), token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count); read += count;
                }
                if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), rgbaSha256, StringComparison.OrdinalIgnoreCase))
                    throw Changed("The prepared pixel range hash is invalid.");
                EnsureIdentity(stamp, Capture(file));
            }
            retained = budget.Reserve(MaximumHeaderBytes * 4L + 4096, "retained texture pixel metadata");
            owner = new(file, backingOwner, stamp, offset, byteLength, rgbaSha256.ToLowerInvariant(), budget, retained, previewWasResized);
            file = null; retained = null; backingOwner = null;
            var surface = surfaceFactory(owner) ?? throw new InvalidDataException("The pixel surface factory returned no metadata.");
            if (surface.IsHdr || surface.HasRgba8 || !ReferenceEquals(surface.PixelSource, owner))
                throw new InvalidDataException("The range factory must return metadata backed by the supplied pixel source.");
            var header = Header.From("range", surface, owner.RgbaSha256, previewWasResized);
            ValidateHeader(header, "range");
            if (header.ByteLength != byteLength) throw new InvalidDataException("The range dimensions do not match its pixel length.");
            owner.Surface = surface;
            owner.metadata!.ReduceTo(MetadataBytes(header));
            token.ThrowIfCancellationRequested();
            return owner;
        }
        catch { owner?.Dispose(); throw; }
        finally { file?.Dispose(); retained?.Dispose(); backingOwner?.Dispose(); }
    }

    public async ValueTask<TexturePixelLease> AcquireAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        FileStream file;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            activeReads = checked(activeReads + 1); file = input!;
        }
        AssetMemoryBudget.Reservation? reservation = null;
        byte[]? pixels = null;
        try
        {
            try
            {
                if (AssetLoadDiagnostics.CountersEnabled)
                    AssetLoadDiagnostics.Counters("PreparedTexturePixelsReadStarted", file.Name,
                        new Dictionary<string, long> { ["Bytes"] = ByteLength });
                EnsureIdentity(stamp, Capture(file));
                reservation = budget.Reserve(checked(ByteLength + 4096), "hydrated texture pixels");
                pixels = new byte[checked((int)ByteLength)];
                for (int first = 0; first < pixels.Length; first += ChunkBytes)
                {
                    token.ThrowIfCancellationRequested();
                    int count = Math.Min(ChunkBytes, pixels.Length - first);
                    await ReadAtAsync(file, pixels.AsMemory(first, count), checked(offset + first), token).ConfigureAwait(false);
                }
                if (!string.Equals(Hash(pixels, token), RgbaSha256, StringComparison.Ordinal))
                    throw Changed("The prepared RGBA bytes changed.");
                EnsureIdentity(stamp, Capture(file));
                token.ThrowIfCancellationRequested();
            }
            finally { EndRead(); }
            var result = new TexturePixelLease(pixels!, reservation!); reservation = null;
            return result;
        }
        finally { reservation?.Dispose(); }
    }

    public void Dispose()
    {
        (FileStream?, IDisposable?, AssetMemoryBudget.Reservation?) release = default;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (activeReads == 0) release = Detach();
        }
        Release(release);
    }

    private void EndRead()
    {
        (FileStream?, IDisposable?, AssetMemoryBudget.Reservation?) release = default;
        lock (gate)
        {
            activeReads--;
            if (activeReads == 0 && disposed) release = Detach();
        }
        Release(release);
    }

    private (FileStream?, IDisposable?, AssetMemoryBudget.Reservation?) Detach()
    {
        var result = (input, cacheLease, metadata); input = null; cacheLease = null; metadata = null;
        return result;
    }

    private static void Release((FileStream? File, IDisposable? Lease, AssetMemoryBudget.Reservation? Metadata) owned)
    {
        try { owned.File?.Dispose(); }
        finally { try { owned.Lease?.Dispose(); } finally { owned.Metadata?.Dispose(); } }
    }

    private static async Task<PreparedTexturePixelSource> OpenCachedAsync(CacheLease lease, string key,
        AssetMemoryBudget budget, Header? expected, CancellationToken token)
    {
        FileStream? file = null;
        AssetMemoryBudget.Reservation? retained = null;
        PreparedTexturePixelSource? owner = null;
        try
        {
            token.ThrowIfCancellationRequested(); file = OpenPinned(lease.Path);
            var validated = await ValidateAsync(file, key, budget, expected, true, token).ConfigureAwait(false);
            retained = validated.Metadata!;
            owner = new(file, lease, validated.Stamp, validated.Offset, validated.Header.ByteLength,
                validated.Header.RgbaSha256, budget, retained, validated.Header.PreviewWasResized);
            file = null; retained = null;
            owner.Surface = validated.Header.Restore(owner);
            token.ThrowIfCancellationRequested();
            return owner;
        }
        catch
        {
            if (owner is not null) owner.Dispose();
            else { try { file?.Dispose(); } finally { try { lease.Dispose(); } finally { retained?.Dispose(); } } }
            throw;
        }
    }

    private static async Task<bool> ValidArtifactAsync(string path, string key, AssetMemoryBudget budget,
        Header? expected, CancellationToken token)
    {
        try
        {
            await using var file = OpenPinned(path);
            _ = await ValidateAsync(file, key, budget, expected, false, token).ConfigureAwait(false);
            return true;
        }
        catch (ArchiveException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or OverflowException) { return false; }
    }

    private sealed record Validated(Header Header, long Offset, FileStamp Stamp, AssetMemoryBudget.Reservation? Metadata);

    private static async Task<Validated> ValidateAsync(FileStream file, string key, AssetMemoryBudget budget,
        Header? expected, bool retainMetadata, CancellationToken token)
    {
        using var scratch = budget.Reserve(ValidationScratchBytes, "texture pixel backing validation");
        token.ThrowIfCancellationRequested(); var before = Capture(file);
        if (before.Length < PrefixBytes + 1L + 4 + 32) throw new InvalidDataException("The pixel artifact is truncated.");
        var prefix = new byte[PrefixBytes]; await file.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        if (!prefix.AsSpan(0, 8).SequenceEqual("DVPIX001"u8) || BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(8)) != 1)
            throw new InvalidDataException("The prepared pixel format is unsupported.");
        int headerLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(12));
        if (headerLength is < 1 or > MaximumHeaderBytes || headerLength > before.Length - PrefixBytes - 36)
            throw new InvalidDataException("The prepared pixel header length is invalid.");
        var json = new byte[headerLength]; await file.ReadExactlyAsync(json, token).ConfigureAwait(false);
        var header = JsonSerializer.Deserialize<Header>(json, JsonOptions) ?? throw new InvalidDataException("The pixel header is missing.");
        ValidateHeader(header, key);
        if (expected is not null && header != expected) throw new InvalidDataException("The cached pixels belong to different surface metadata.");
        long offset = PrefixBytes + headerLength;
        if (before.Length != offset + header.ByteLength + 32) throw new InvalidDataException("The pixel artifact length does not match its dimensions.");
        using var artifactHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var pixelHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        artifactHash.AppendData(prefix); artifactHash.AppendData(json);
        var buffer = new byte[ChunkBytes]; int remaining = header.ByteLength;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested(); int count = Math.Min(remaining, buffer.Length);
            await file.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            artifactHash.AppendData(buffer, 0, count); pixelHash.AppendData(buffer, 0, count); remaining -= count;
        }
        var footer = new byte[32]; await file.ReadExactlyAsync(footer, token).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(artifactHash.GetHashAndReset(), footer) ||
            !string.Equals(Convert.ToHexString(pixelHash.GetHashAndReset()), header.RgbaSha256, StringComparison.OrdinalIgnoreCase))
            throw Changed("The prepared pixel content or metadata checksum is invalid.");
        var after = Capture(file); EnsureIdentity(before, after); token.ThrowIfCancellationRequested();
        var retained = retainMetadata ? budget.Reserve(MetadataBytes(header), "retained texture pixel metadata") : null;
        return new(header, offset, after, retained);
    }

    private static async Task WriteAsync(Stream file, Header header, ReadOnlyMemory<byte> pixels,
        AssetMemoryBudget budget, CancellationToken token)
    {
        using var scratch = budget.Reserve(MaximumHeaderBytes * 4L + 8192, "texture pixel backing writer");
        token.ThrowIfCancellationRequested(); var json = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        if (json.Length > MaximumHeaderBytes) throw new InvalidDataException("The pixel metadata exceeds its header limit.");
        var prefix = new byte[PrefixBytes]; "DVPIX001"u8.CopyTo(prefix);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(12), json.Length);
        using var timing = AssetLoadDiagnostics.Measure("PreparedTexturePixelsWrite", header.Key,
            requestedBytes: checked(PrefixBytes + (long)json.Length + pixels.Length + 32));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(prefix); hash.AppendData(json);
        await file.WriteAsync(prefix, token).ConfigureAwait(false); await file.WriteAsync(json, token).ConfigureAwait(false);
        for (int first = 0; first < pixels.Length; first += ChunkBytes)
        {
            token.ThrowIfCancellationRequested(); var part = pixels.Slice(first, Math.Min(ChunkBytes, pixels.Length - first));
            hash.AppendData(part.Span); await file.WriteAsync(part, token).ConfigureAwait(false);
        }
        await file.WriteAsync(hash.GetHashAndReset(), token).ConfigureAwait(false);
        await file.FlushAsync(token).ConfigureAwait(false);
    }

    private static FileStream OpenPinned(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);

    private static FileStamp Capture(FileStream file)
    {
        var stamp = Revisions.Capture(file.SafeFileHandle, file.Name);
        if (!stamp.Exists || stamp.Length < 0 || stamp.Error is not null) throw Changed("The prepared pixel file is unavailable.");
        return stamp;
    }

    private static void EnsureIdentity(FileStamp expected, FileStamp actual)
    {
        // Cache hits update last-write time for LRU accounting. The read-only pin,
        // handle identity/length and exact content digest protect the backing.
        if (!actual.Exists || actual.Error is not null || actual.Length != expected.Length ||
            !string.Equals(actual.FileIdentity, expected.FileIdentity, StringComparison.Ordinal))
            throw Changed("The prepared pixel file identity or length changed.");
    }

    private static async Task ReadAtAsync(FileStream file, Memory<byte> bytes, long offset, CancellationToken token)
    {
        int read = 0;
        while (read < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            int count = await RandomAccess.ReadAsync(file.SafeFileHandle, bytes[read..], checked(offset + read), token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("The prepared pixel range ended early.");
            read += count;
        }
    }

    private static string Hash(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int first = 0; first < bytes.Length; first += ChunkBytes)
        { token.ThrowIfCancellationRequested(); hash.AppendData(bytes.Slice(first, Math.Min(ChunkBytes, bytes.Length - first))); }
        token.ThrowIfCancellationRequested(); return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed record Header(string Producer, string Key, int Width, int Height, bool SourceIsHdr, bool IsSrgb,
        int MipIndex, TextureStorageFormat SourceFormat, string SourceFormatName, string SourcePayloadSha256,
        TextureSubresourceSelection Subresource, ResourceIdentity? Identity, SourceRevision? Revision,
        TextureDisplayTransform? DisplayTransform, bool PreviewWasResized, int ByteLength, string RgbaSha256)
    {
        public static Header From(string key, TextureSurface surface, string hash, bool resized) => new(Profile, key,
            surface.Width, surface.Height, surface.SourceIsHdr, surface.IsSrgb, surface.MipIndex, surface.SourceFormat,
            surface.SourceFormatName, surface.SourcePayloadSha256, surface.Subresource, surface.Identity, surface.Revision,
            surface.DisplayTransform, resized, checked(surface.Width * surface.Height * 4), hash);
        public TextureSurface Restore(ITexturePixelSource source) => TextureSurface.RestoreBackedRgba8(Width, Height,
            source, SourceIsHdr, IsSrgb, MipIndex, SourceFormat, SourceFormatName, SourcePayloadSha256, Subresource,
            Identity, Revision, DisplayTransform);
    }

    private static void ValidateHeader(Header header, string key)
    {
        if (header.Producer != Profile || header.Key != key || header.Width is < 1 or > DdsTextureReader.MaximumDimension ||
            header.Height is < 1 or > DdsTextureReader.MaximumDimension || (long)header.Width * header.Height > DdsTextureReader.MaximumPixelCount ||
            header.ByteLength != (long)header.Width * header.Height * 4 || header.MipIndex < 0 ||
            header.Subresource is null || header.Subresource.MipIndex != header.MipIndex || header.Subresource.ArrayLayer < 0 ||
            header.Subresource.Face is { } face && !Enum.IsDefined(face) || !Enum.IsDefined(header.SourceFormat) ||
            !Text(header.SourceFormatName, 256) || !Digest(header.SourcePayloadSha256) || !Digest(header.RgbaSha256) ||
            header.Identity is { } identity && (!Text(identity.IndexContentId, 2048) || identity.Ordinal < 0 || identity.MemberPath?.Length > 4096) ||
            header.Revision is { } revision && (!Text(revision.IndexContentId, 2048) || !Text(revision.PhysicalSourceStamp, 4096) ||
                header.Identity is { } owner && revision.IndexContentId != owner.IndexContentId) ||
            header.DisplayTransform is { } transform && (!Enum.IsDefined(transform.Operator) || !float.IsFinite(transform.ExposureStops) ||
                !Text(transform.Version, 128) || transform.EncodedSrgb && !header.IsSrgb))
            throw new InvalidDataException("The prepared texture metadata is invalid.");
    }

    private static long MetadataBytes(Header header) => checked(8192L + 2L * (header.Key.Length + header.SourceFormatName.Length +
        header.SourcePayloadSha256.Length + header.RgbaSha256.Length + (header.Identity?.IndexContentId.Length ?? 0) +
        (header.Identity?.MemberPath?.Length ?? 0) + (header.Revision?.IndexContentId.Length ?? 0) +
        (header.Revision?.PhysicalSourceStamp.Length ?? 0) + (header.DisplayTransform?.Version.Length ?? 0)));
    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static void ValidateDigest(string value)
    { if (!Digest(value)) throw new ArgumentException("Use a SHA-256 hexadecimal digest.", nameof(value)); }
    private static void ValidateKey(string key)
    { if (!Text(key, 2048)) throw new ArgumentException("Use a nonempty pixel key of at most 2048 characters.", nameof(key)); }
    private static TextureFormatException Changed(string message) => new("PreparedTextureChanged", message);
}
