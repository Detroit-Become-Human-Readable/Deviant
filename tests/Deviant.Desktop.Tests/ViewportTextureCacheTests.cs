using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Formats.Textures;
using HelixToolkit.SharpDX;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class ViewportTextureCacheTests
{
    [Fact]
    public void NativeAliasesReuseTheUploadUntilTheirLastLeaseCloses()
    {
        var revision = new SourceRevision("index", "revision");
        byte[] pixels = [20, 60, 100, 255, 30, 70, 110, 255, 40, 80, 120, 255, 50, 90, 130, 255];
        var first = Texture(new("index", 1), revision, 2, 2, pixels);
        var alias = Texture(new("index", 2), revision, 2, 2, pixels);
        var pool = new SharedTexturePool<Upload>();
        var budget = new AssetMemoryBudget();
        int allocations = 0; long gpuBytes = 0;
        var firstLease = pool.Acquire(TextureContentKey.Create(first, revision, false), CreateUpload);
        var aliasLease = pool.Acquire(TextureContentKey.Create(alias, revision, false), () => throw new InvalidOperationException("The alias must reuse its upload."));
        Assert.NotEqual(first.NativeIdentity, alias.NativeIdentity);
        Assert.Same(firstLease.Resource.Model, aliasLease.Resource.Model);
        Assert.Equal(1, allocations); Assert.Equal(pixels.Length, gpuBytes); Assert.Equal(pixels.Length, budget.UsedBytes);
        firstLease.Dispose();
        Assert.Equal(pixels.Length, gpuBytes); Assert.Equal(1, pool.Count);
        var model = aliasLease.Resource.Model;
        aliasLease.Dispose(); aliasLease.Dispose();
        Assert.Equal(0, gpuBytes); Assert.Equal(0, budget.UsedBytes); Assert.Equal(0, pool.Count);
        Assert.Throws<ObjectDisposedException>(() => model.Load());

        Upload CreateUpload()
        {
            allocations++; gpuBytes += pixels.Length;
            var loader = new AssetViewportService.RawTextureLoader(pixels.ToArray(), 2, 2, false);
            return new(new TextureModel(Guid.NewGuid(), loader), loader, budget.Reserve(pixels.Length, "texture upload"),
                () => gpuBytes -= pixels.Length);
        }
    }

    [Fact]
    public void DimensionsSrgbContentAndRevisionKeepSeparateUploads()
    {
        var revision = new SourceRevision("index", "revision");
        byte[] pixels = Enumerable.Repeat((byte)80, 16).ToArray();
        var square = Texture(new("index", 1), revision, 2, 2, pixels);
        var narrow = Texture(new("index", 2), revision, 1, 4, pixels);
        var srgb = Texture(new("index", 3), revision, 2, 2, pixels, srgb: true);
        byte[] changed = pixels.ToArray(); changed[0]++;
        var different = Texture(new("index", 4), revision, 2, 2, changed);
        var nextRevision = new SourceRevision("index", "next");
        var next = Texture(new("index", 1), nextRevision, 2, 2, pixels);
        TextureContentKey[] keys = [TextureContentKey.Create(square, revision, false), TextureContentKey.Create(narrow, revision, false),
            TextureContentKey.Create(srgb, revision, true), TextureContentKey.Create(different, revision, false),
            TextureContentKey.Create(next, nextRevision, false)];
        Assert.Equal(5, keys.Distinct().Count());
        var pool = new SharedTexturePool<Upload>(); var budget = new AssetMemoryBudget(); long gpuBytes = 0;
        RenderTexture[] sources = [square, narrow, srgb, different, next];
        var leases = keys.Select((key, index) => pool.Acquire(key, () =>
        {
            var source = sources[index].Surface;
            gpuBytes += source.Rgba8.Length;
            var loader = new AssetViewportService.RawTextureLoader(source.Rgba8.ToArray(), key.Width, key.Height, key.Srgb);
            return new Upload(new TextureModel(Guid.NewGuid(), loader), loader, budget.Reserve(source.Rgba8.Length, "texture upload"),
                () => gpuBytes -= source.Rgba8.Length);
        })).ToArray();
        Assert.Equal(5, leases.Select(x => x.Resource.Model.Guid).Distinct().Count());
        Assert.Equal(5 * pixels.Length, gpuBytes);
        foreach (var lease in leases) lease.Dispose();
        Assert.Equal(0, pool.Count); Assert.Equal(0, gpuBytes); Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void VerifiesPixelHashesBeforeContentSharing()
    {
        var revision = new SourceRevision("index", "revision");
        var texture = Texture(new("index", 1), revision, 1, 1, [10, 20, 30, 255]);
        var uppercase = texture with { RgbaSha256 = texture.RgbaSha256.ToUpperInvariant() };
        Assert.Equal(TextureContentKey.Create(texture, revision, false), TextureContentKey.Create(uppercase, revision, false));
        Assert.Throws<System.IO.InvalidDataException>(() => TextureContentKey.Create(texture with { RgbaSha256 = new string('0', 64) }, revision, false));
    }

    private static RenderTexture Texture(ResourceIdentity identity, SourceRevision revision, int width, int height, byte[] rgba, bool srgb = false)
    {
        var bytes = new byte[148 + rgba.Length]; "DDS "u8.CopyTo(bytes);
        Write(4, 124); Write(8, 0x1007); Write(12, (uint)height); Write(16, (uint)width);
        Write(76, 32); Write(80, 4); "DX10"u8.CopyTo(bytes.AsSpan(84)); Write(108, 0x1000);
        Write(128, srgb ? 29u : 28u); Write(132, 3); Write(140, 1); rgba.CopyTo(bytes, 148);
        var surface = new DdsTextureDecoder().DecodeMip(new DdsTextureReader().Read(bytes, identity, revision));
        return new(identity, surface, Convert.ToHexString(SHA256.HashData(surface.Rgba8.Span)).ToLowerInvariant());
        void Write(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }

    private sealed class Upload(TextureModel model, IDisposable loader, IDisposable cpu, Action releaseGpu) : IDisposable
    {
        public TextureModel Model { get; } = model;
        public void Dispose() { loader.Dispose(); cpu.Dispose(); releaseGpu(); }
    }
}
