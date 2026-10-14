using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Export.Models;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class EncodedTextureSurfaceTests
{
    [Theory]
    [InlineData(71u, false)]
    [InlineData(72u, true)]
    [InlineData(77u, false)]
    [InlineData(78u, true)]
    public void EncodedFactoryPreservesNativeMipColorSpaceAndExactDocument(uint format, bool srgb)
    {
        var document = Document(format);
        var surface = Surface(document, nativeMip: 3);
        Assert.Same(document, surface.EncodedDds);
        Assert.Empty(surface.Rgba8.ToArray()); Assert.False(surface.HasRgba8);
        Assert.Equal(document.Info.Width, surface.Width); Assert.Equal(document.Info.Height, surface.Height);
        Assert.Equal(srgb, surface.IsSrgb); Assert.Equal(3, surface.MipIndex);
        Assert.Equal(3, surface.Subresource.MipIndex); Assert.Equal(0, surface.EncodedDdsSelection!.MipIndex);
        Assert.Equal(document.ContentSha256, surface.SourcePayloadSha256);
    }

    [Fact]
    public void OpaqueBc1ThreeColorBlocksFallBackButAlphaBc1RemainsEligible()
    {
        var block = TextureFixtureBuilder.SolidDxt1Block(0, ushort.MaxValue, uint.MaxValue);
        var bytes = TextureFixtureBuilder.CreateDx10(4, 4, 71, block);
        var alpha = new DdsTextureReader().Read(bytes);
        Assert.True(TextureSurface.CanRetainEncodedDds(alpha, alpha.DefaultSelection));
        Assert.Equal(0, new DdsTextureDecoder().DecodeMip(alpha).Rgba8.Span[3]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(144), 3);
        var opaque = new DdsTextureReader().Read(bytes);
        Assert.False(TextureSurface.CanRetainEncodedDds(opaque, opaque.DefaultSelection));
        Assert.Equal(255, new DdsTextureDecoder().DecodeMip(opaque).Rgba8.Span[3]);
        Assert.Throws<TextureFormatException>(() => Surface(opaque));
    }

    [Theory]
    [InlineData(80u)]
    [InlineData(83u)]
    public void NormalizedBc4AndBc5StayOnThePixelPath(uint format)
    {
        var document = Document(format);
        Assert.False(TextureSurface.CanRetainEncodedDds(document, document.DefaultSelection));
        Assert.Throws<TextureFormatException>(() => Surface(document));
    }

    [Fact]
    public void EncodedFactoryRejectsMismatchedDimensionsColorSpaceAndChangedBytes()
    {
        var document = Document(71);
        Assert.Throws<TextureFormatException>(() => TextureSurface.RestoreEncodedDds(1, document.Info.Height, document,
            false, false, 0, document.Info.Format, document.Info.FormatName, document.ContentSha256, new(), null, null));
        Assert.Throws<TextureFormatException>(() => TextureSurface.RestoreEncodedDds(document.Info.Width, document.Info.Height, document,
            false, true, 0, document.Info.Format, document.Info.FormatName, document.ContentSha256, new(), null, null));
        var bytes = document.OriginalBytes.ToArray();
        var owned = new DdsTextureReader().TakeOwnership(bytes);
        bytes[^1] ^= 1;
        Assert.Throws<TextureFormatException>(() => Surface(owned));
    }

    [Theory]
    [InlineData((ushort)15)]
    [InlineData((ushort)21)]
    public void LinearScalarPreviewUsesR8WithEveryRgbaChannelIdenticalToFullDecode(ushort selector)
    {
        var source = ConvertedTextureFixture.Source(soleRaw: true, selector: selector);
        var decoder = new NativeTextureDecoder();
        var full = decoder.ReadDetailed(source);
        var preview = decoder.ReadPreview(source, 32);
        Assert.Equal(TextureStorageFormat.Rgba8, full.Document.Info.Format);
        Assert.Equal("R8_UNORM", preview.Document.Info.FormatName);
        Assert.Equal(TextureStorageFormat.Luminance8, preview.Document.Info.Format);
        Assert.True(TextureSurface.CanRetainEncodedDds(preview.Document, preview.Document.DefaultSelection));
        var pixels = new DdsTextureDecoder();
        var expected = pixels.DecodeMip(full.Document).Rgba8.ToArray();
        var actual = pixels.DecodeMip(preview.Document).Rgba8.ToArray();
        Assert.Equal(expected, actual);
        for (int pixel = 0; pixel < actual.Length; pixel += 4)
        { Assert.Equal(0, actual[pixel + 1]); Assert.Equal(0, actual[pixel + 2]); Assert.Equal(255, actual[pixel + 3]); }
        Assert.Equal(148 + 32 * 32, preview.Document.OriginalBytes.Length);
        Assert.Equal(full.SupportedMips[0].OriginalSha256, preview.SupportedMips[0].OriginalSha256);
        Assert.Equal(full.SupportedMips[0].MipIndex, preview.SupportedMips[0].MipIndex);
        Assert.Equal(full.SupportedMips[0].DecodedSha256, preview.SupportedMips[0].DecodedSha256);
        Assert.Equal(full.SupportedMips[0].MipExtent, preview.SupportedMips[0].MipExtent);
    }

    [Theory]
    [InlineData(77u)]
    [InlineData(61u)]
    public async Task PreparedRoundTripRetainsSharedDdsWithoutWarmPixelHydrationAndRejectsCorruption(uint format)
    {
        using var fixture = new GeneratedArchive();
        var archive = await new ArchiveReader().OpenAsync(fixture.Index);
        var revision = new SourceRevision(archive.IndexContentId, archive.PhysicalSourceStamp);
        var budget = new AssetMemoryBudget();
        using var source = ArchiveSourceSession.Acquire(archive, (bytes, reason) => budget.Reserve(bytes, reason));
        var document = Document(format);
        var expectedPixels = new DdsTextureDecoder().DecodeMip(document).Rgba8.ToArray();
        var pixelHash = Hash(expectedPixels);
        var id = new ResourceIdentity(archive.IndexContentId, 0, "generated/texture");
        var first = new RenderTexture(id, Surface(document, 2, id, revision), pixelHash);
        var alias = first with { NativeIdentity = id with { MemberPath = "generated/alias" },
            Surface = Surface(document, 2, id with { MemberPath = "generated/alias" }, revision) };
        var material = new RenderMaterial(new(id with { MemberPath = "generated/material" }, revision, "generated", [], [], []),
            new Dictionary<int, RenderTexture> { [0] = first, [1] = alias });
        var extent = new SourceExtent(archive.Volumes[0].RelativeName, 0, 4, Hash([1, 2, 3, 4]));
        using var snapshot = new RenderAssetSnapshot(revision, new Dictionary<string, RenderMesh>(), [],
            new Dictionary<ResourceIdentity, RenderMaterial> { [material.Asset.Identity] = material },
            new(Vector3.Zero, Vector3.One), [], [], new(0, 0, 0, 0, 0, 0), [extent], budget);
        var path = Path.Combine(fixture.Root, "prepared.scene");
        await PreparedSceneCodec.WriteAsync(path, "generated", snapshot);
        Assert.True(await PreparedSceneCodec.ValidateAsync(path, "generated", revision));
        var baseline = budget.UsedBytes;
        using (var restored = await PreparedSceneCodec.ReadAsync(path, "generated", source.Session, archive, budget, false))
        {
            var slots = Assert.Single(restored.Materials.Values).TexturesBySlot;
            Assert.False(slots[0].Surface.HasRgba8); Assert.False(slots[1].Surface.HasRgba8);
            Assert.Empty(slots[0].Surface.Rgba8.ToArray());
            Assert.Same(slots[0].Surface.EncodedDds, slots[1].Surface.EncodedDds);
            Assert.Equal(document.ContentSha256, slots[0].Surface.EncodedDds!.ContentSha256);
            Assert.Equal(pixelHash, slots[0].RgbaSha256); Assert.Equal(2, slots[0].Surface.MipIndex);
            Assert.Equal(expectedPixels, new DdsTextureDecoder().DecodeSubresource(slots[0].Surface.EncodedDds!,
                slots[0].Surface.EncodedDdsSelection!).Rgba8.ToArray());
        }
        Assert.Equal(baseline, budget.UsedBytes);
        var bytes = await File.ReadAllBytesAsync(path);
        var blob = document.OriginalBytes.ToArray();
        var at = bytes.AsSpan().IndexOf(blob);
        Assert.True(at >= 0); bytes[at + blob.Length - 1] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);
        Assert.False(await PreparedSceneCodec.ValidateAsync(path, "generated", revision));
        await Assert.ThrowsAsync<InvalidDataException>(() => PreparedSceneCodec.ReadAsync(path, "generated", source.Session, archive, budget, false));
        Assert.Equal(baseline, budget.UsedBytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 2);
        await File.WriteAllBytesAsync(path, bytes);
        Assert.False(await PreparedSceneCodec.ValidateAsync(path, "generated", revision));
    }

    [Fact]
    public void BakeCacheKeyRequiresExplicitPixelHydrationForEncodedSurfaces()
    {
        var surface = Surface(Document(71));
        var identity = new ResourceIdentity("generated", 0);
        var material = new MaterialAsset(identity, new("generated", "revision"), "generated", [], [], []);
        var primitive = new MeshPrimitive("generated", PrimitiveTopology.Triangles, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            [0, 1, 2], IndexElementFormat.UInt16, []);
        var textures = new Dictionary<int, MaterialBakeTexture>
        { [0] = new(surface, NativeTextureSamplingDecoder.Decode(0)) };
        var method = typeof(MaterialBakeCache).GetMethod("Fingerprint", BindingFlags.NonPublic | BindingFlags.Static)!;
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null,
            [material, primitive, textures, 32, 16, 0, null, "generated graph", CancellationToken.None]));
        Assert.Contains("requires decoded RGBA", Assert.IsType<InvalidDataException>(error.InnerException).Message);
    }

    private static TextureSurface Surface(DdsTextureDocument document, int nativeMip = 0,
        ResourceIdentity? identity = null, SourceRevision? revision = null) => TextureSurface.RestoreEncodedDds(
        document.Info.Width, document.Info.Height, document, false, document.Info.IsSrgb, nativeMip,
        document.Info.Format, document.Info.FormatName, document.ContentSha256, new(MipIndex: nativeMip),
        identity, revision, document.DefaultSelection);

    private static DdsTextureDocument Document(uint format)
    {
        const int width = 32, height = 16;
        var blockBytes = format is 71 or 72 or 80 ? 8 : 16;
        var payload = new byte[TextureFixtureBuilder.MipLength(width, height, blockBytes)];
        for (int at = 0; at < payload.Length; at += blockBytes)
        {
            var color = format is 77 or 78 ? at + 8 : at;
            if (format is 77 or 78) { payload[at] = 128; payload[at + 1] = 64; }
            TextureFixtureBuilder.SolidDxt1Block(0xf800, 0x07e0).CopyTo(payload, color);
        }
        return new DdsTextureReader().Read(TextureFixtureBuilder.CreateDx10(width, height, format, payload));
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed class GeneratedArchive : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DeviantTests", Guid.NewGuid().ToString("N"));
        public string Index => Path.Combine(Root, "sample.idx");
        public GeneratedArchive()
        {
            Directory.CreateDirectory(Root);
            var bytes = new byte[IndexLayout.HeaderLength + IndexLayout.RecordLength]; IndexLayout.Magic.CopyTo(bytes);
            void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
            Put(20, IndexLayout.SupportedVersion); Put(101, 1); Put(IndexLayout.HeaderLength, 29);
            Put(IndexLayout.HeaderLength + 8, 1); Put(IndexLayout.HeaderLength + 16, 4);
            File.WriteAllBytes(Index, bytes); File.WriteAllBytes(Path.Combine(Root, IndexLayout.VolumeName("sample", 0)), [1, 2, 3, 4]);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
