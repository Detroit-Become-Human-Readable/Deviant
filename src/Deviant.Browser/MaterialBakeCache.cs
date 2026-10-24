using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Export.Models;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

/// <summary>Reuses fingerprinted derived DDS maps under the shared asset cache budget.</summary>
public sealed class MaterialBakeCache(CacheManager cache)
{
    private const int HeaderBytes = 92;
    private static ReadOnlySpan<byte> Magic => "DVBAKE1\0"u8;

    public async Task<MaterialSurfaceMap> BakeAsync(MaterialAsset material, MeshPrimitive primitive,
        IReadOnlyDictionary<int, MaterialBakeTexture> textures, int width, int height, AssetMemoryBudget budget,
        Func<CancellationToken, Task> validateSources, CancellationToken token = default,
        int uvSet = 0, IReadOnlyList<Vector2>? corners = null, long reservedOutputBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(cache); ArgumentNullException.ThrowIfNull(validateSources);
        if (reservedOutputBytes < 0) throw new ArgumentOutOfRangeException(nameof(reservedOutputBytes));
        if (width < 1 || height < 1 || (long)width * height > DdsTextureReader.MaximumPixelCount)
            throw new InvalidDataException("MaterialBakeCacheDimensions");
        token.ThrowIfCancellationRequested();
        await validateSources(token).ConfigureAwait(false);
        using var io = budget.Reserve(256 * 1024, "material bake cache IO");
        string graph = MaterialSurfaceMapBaker.ComputeGraphHash(material.SurfaceGraph ?? throw new InvalidDataException("MaterialBakeGraphUnavailable"));
        string key;
        using (AssetLoadDiagnostics.Measure("BakeCacheKey", material.Identity.Key))
            key = Fingerprint(material, primitive, textures, width, height, uvSet, corners, graph, token);
        MaterialSurfaceMap? created = null;
        bool hit = true;
        CacheLease? lease = null;
        try
        {
            lease = await cache.AcquireMaterialBakeOutputAsync(key, async (output, cancellation) =>
            {
                hit = false;
                using (AssetLoadDiagnostics.Measure("ExportRasterize", key))
                    created = await Task.Run(() => MaterialSurfaceMapBaker.Bake(material, primitive, textures, width, height,
                        budget.Reserve, cancellation, uvSet, corners, reservedOutputBytes), cancellation).ConfigureAwait(false);
                using (AssetLoadDiagnostics.Measure("BakeCacheWrite", key))
                    await WriteAsync(output, created, key, cancellation).ConfigureAwait(false);
            }, (path, cancellation) => ValidateAsync(path, key, graph, width, height, uvSet, cancellation), token).ConfigureAwait(false);
        }
        catch (IOException error) when (error.Data["ErrorCode"] is "CacheStorageLimit" ||
            ((error.HResult & 0xffff) is 112 or 39))
        {
            token.ThrowIfCancellationRequested();
            hit = false;
            // A bake remains usable without retaining an optional disk entry.
            if (created is null)
                using (AssetLoadDiagnostics.Measure("ExportRasterize", key))
                    created = await Task.Run(() => MaterialSurfaceMapBaker.Bake(material, primitive, textures, width, height,
                        budget.Reserve, token, uvSet, corners, reservedOutputBytes), token).ConfigureAwait(false);
        }
        await using var heldLease = lease;
        AssetLoadDiagnostics.Cache("BakeCache", key, hit);
        await validateSources(token).ConfigureAwait(false);
        if (created is not null) return created;
        var cachedPath = lease?.Path ?? throw new InvalidOperationException("The prepared bake lease is unavailable.");
        using var timing = AssetLoadDiagnostics.Measure("BakeCacheRead", key, relativeSource: Path.GetFileName(cachedPath),
            requestedBytes: checked(148L + (long)width * height * 4));
        using var decodeScratch = budget.Reserve(checked(148L + (long)width * height * 4 + 4096 -
            Math.Min(reservedOutputBytes, 148L + (long)width * height * 4)), "material bake cache input");
        using var input = new FileStream(cachedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        input.Position = HeaderBytes;
        var encoded = new byte[checked(148 + width * height * 4)];
        await input.ReadExactlyAsync(encoded, token).ConfigureAwait(false);
        var document = new DdsTextureReader().TakeOwnership(encoded, material.Identity, material.Revision);
        var surface = new DdsTextureDecoder(budget.MaximumBytes).ViewRgbaSubresource(document, document.DefaultSelection, token);
        return new(document, surface, graph, uvSet) { PolygonCornerCoordinates = corners };
    }

    private static async Task WriteAsync(Stream output, MaterialSurfaceMap map, string key, CancellationToken token)
    {
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(Magic); writer.Write(Convert.FromHexString(key)); writer.Write(Convert.FromHexString(map.GraphSha256));
        writer.Write(map.Surface.Width); writer.Write(map.Surface.Height); writer.Write(map.TextureCoordinateSet);
        writer.Write((long)map.Document.OriginalBytes.Length); writer.Flush();
        await output.WriteAsync(map.Document.OriginalBytes, token).ConfigureAwait(false);
        await output.WriteAsync(Convert.FromHexString(map.Document.ContentSha256), token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<bool> ValidateAsync(string path, string key, string graph, int width, int height,
        int uvSet, CancellationToken token)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            using var timing = AssetLoadDiagnostics.Measure("BakeCacheVerify", key, relativeSource: Path.GetFileName(path), requestedBytes: input.Length);
            long expectedLength = checked(148L + (long)width * height * 4);
            if (input.Length != HeaderBytes + expectedLength + 32) return false;
            using var reader = new BinaryReader(input, Encoding.UTF8, true);
            if (!reader.ReadBytes(8).AsSpan().SequenceEqual(Magic) ||
                !reader.ReadBytes(32).AsSpan().SequenceEqual(Convert.FromHexString(key)) ||
                !reader.ReadBytes(32).AsSpan().SequenceEqual(Convert.FromHexString(graph)) ||
                reader.ReadInt32() != width || reader.ReadInt32() != height || reader.ReadInt32() != uvSet ||
                reader.ReadInt64() != expectedLength) return false;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024]; long remaining = expectedLength;
            while (remaining > 0)
            {
                int count = (int)Math.Min(buffer.Length, remaining);
                await input.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                hash.AppendData(buffer.AsSpan(0, count)); remaining -= count;
            }
            var footer = new byte[32]; await input.ReadExactlyAsync(footer, token).ConfigureAwait(false);
            return hash.GetHashAndReset().AsSpan().SequenceEqual(footer);
        }
        catch (IOException) { return false; }
        catch (InvalidDataException) { return false; }
    }

    private static string Fingerprint(MaterialAsset material, MeshPrimitive primitive,
        IReadOnlyDictionary<int, MaterialBakeTexture> textures, int width, int height, int uvSet,
        IReadOnlyList<Vector2>? corners, string graph, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sink = new HashSink(hash); using var writer = new BinaryWriter(sink, Encoding.UTF8, true);
        writer.Write("material-bake-2"); writer.Write(material.Identity.Key); writer.Write(material.Revision.PhysicalSourceStamp);
        writer.Write(graph); writer.Write(width); writer.Write(height); writer.Write(uvSet); writer.Write((int)primitive.Topology);
        writer.Write(textures.Count);
        foreach (var pair in textures.OrderBy(x => x.Key))
        {
            token.ThrowIfCancellationRequested(); var texture = pair.Value;
            if (texture.Surface.IsHdr) throw new InvalidDataException("MaterialBakeCacheHdrSurface");
            if (!texture.Surface.HasRgba8)
                throw new InvalidDataException("Material baking requires decoded RGBA pixels.");
            writer.Write(pair.Key); writer.Write(texture.Surface.Width); writer.Write(texture.Surface.Height);
            writer.Write(texture.Surface.IsSrgb); writer.Write(JsonSerializer.Serialize(texture.Surface.Subresource));
            writer.Write(JsonSerializer.Serialize(texture.Sampling));
            writer.Write(SHA256.HashData(texture.Surface.Rgba8.Span));
        }
        writer.Write(primitive.Positions.Count);
        for (int i = 0; i < primitive.Positions.Count; i++) { Check(i); Vector3(primitive.Positions[i]); }
        writer.Write(primitive.Indices.Count);
        for (int i = 0; i < primitive.Indices.Count; i++) { Check(i); writer.Write(primitive.Indices[i]); }
        writer.Write(primitive.TextureCoordinates?.Count ?? -1);
        foreach (var set in primitive.TextureCoordinates ?? [])
        { writer.Write(set.Count); for (int i = 0; i < set.Count; i++) { Check(i); Vector2(set[i]); } }
        writer.Write(primitive.Colors?.Count ?? -1);
        if (primitive.Colors is { } colors) for (int i = 0; i < colors.Count; i++)
        { Check(i); var v = colors[i]; writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); writer.Write(v.W); }
        writer.Write(primitive.Normals?.Count ?? -1);
        if (primitive.Normals is { } normals) for (int i = 0; i < normals.Count; i++) { Check(i); Vector3(normals[i]); }
        writer.Write(corners?.Count ?? -1);
        if (corners is not null) for (int i = 0; i < corners.Count; i++) { Check(i); Vector2(corners[i]); }
        writer.Flush(); return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        void Check(int i) { if ((i & 1023) == 0) token.ThrowIfCancellationRequested(); }
        void Vector2(Vector2 value) { writer.Write(value.X); writer.Write(value.Y); }
        void Vector3(Vector3 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
    }

    private sealed class HashSink(IncrementalHash hash) : Stream
    {
        private readonly byte[] buffer = new byte[64 * 1024];
        private int used;
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { if (used == 0) return; hash.AppendData(buffer.AsSpan(0, used)); used = 0; }
        public override void Write(byte[] value, int offset, int count) => Write(value.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> value)
        {
            while (!value.IsEmpty)
            {
                int count = Math.Min(value.Length, buffer.Length - used);
                value[..count].CopyTo(buffer.AsSpan(used)); used += count; value = value[count..];
                if (used == buffer.Length) Flush();
            }
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
