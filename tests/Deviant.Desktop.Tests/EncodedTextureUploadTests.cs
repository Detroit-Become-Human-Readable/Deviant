using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class EncodedTextureUploadTests
{
    private static readonly ResourceIdentity Identity = new(new string('a', 64), 1);
    private static readonly SourceRevision Revision = new(new string('a', 64), new string('b', 64));

    [Theory]
    [InlineData(71u)]
    [InlineData(77u)]
    public void SelectedBlocksAndAlphaReachTheLoaderUnchanged(uint format)
    {
        var document = Document(format);
        using var loader = AssetViewportService.RawTextureLoader.CreateEncoded2D(document, Revision, false, new AssetMemoryBudget());
        var id = Guid.NewGuid(); var info = loader.Load(id);
        Assert.True(info.IsCompressed); Assert.False(info.GenerateMipMaps);
        using var output = new MemoryStream(); info.Texture!.CopyTo(output);
        Assert.Equal(document.OriginalBytes.ToArray(), output.ToArray());
        var received = new DdsTextureReader().Read(output.ToArray(), Identity, Revision);
        var pixels = new DdsTextureDecoder().DecodeMip(received);
        Assert.Equal(format == 71 ? (byte)255 : (byte)200, pixels.Rgba8.Span[3]);
        var stream = info.Texture;
        loader.Complete(id, info, true);
        Assert.Throws<ObjectDisposedException>(() => stream!.ReadByte());
    }

    [Fact]
    public void LinearNormalAliasChangesOnlyTheDxgiHeaderAndOwnsItsCopy()
    {
        var document = Document(78);
        var original = document.OriginalBytes.ToArray();
        var budget = new AssetMemoryBudget();
        using var loader = AssetViewportService.RawTextureLoader.CreateEncoded2D(document, Revision, false, budget);
        Assert.Equal(document.OriginalBytes.Length, budget.UsedBytes);
        var info = loader.Load(Guid.NewGuid()); using var output = new MemoryStream(); info.Texture!.CopyTo(output);
        var bytes = output.ToArray();
        Assert.Equal(77u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(128)));
        Assert.Equal(original.AsSpan(148).ToArray(), bytes.AsSpan(148).ToArray());
        Assert.Equal(original, document.OriginalBytes.ToArray());
        var alias = new DdsTextureReader().Read(bytes, Identity, Revision);
        Assert.False(alias.Info.IsSrgb);
        loader.Complete(Guid.NewGuid(), info, true); loader.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void EncodedKeysSeparateGammaFormatsAndChangedBlocksAndShareNativeAliases()
    {
        var texture = Texture(Document(78));
        var key = TextureContentKey.CreateEncoded(texture, Revision, true);
        Assert.NotEqual(key, TextureContentKey.CreateEncoded(texture, Revision, false));
        Assert.NotEqual(key, TextureContentKey.CreateEncoded(Texture(Document(72)), Revision, true));
        Assert.NotEqual(key, TextureContentKey.CreateEncoded(Texture(Document(78, redEndpoint: 0x07E0)), Revision, true));
        Assert.Equal(key, TextureContentKey.CreateEncoded(texture with { NativeIdentity = Identity with { Ordinal = 99 } }, Revision, true));
        Assert.Equal(TextureStorageFormat.Bc3, key.EncodedFormat);
        Assert.Equal(1, key.MipCount);
    }

    [Fact]
    public void SharedAliasesChargeCompressedBytesAndReleaseAfterTheLastLease()
    {
        var document = Document(71); var texture = Texture(document);
        var pool = new SharedTexturePool<Upload>(); var budget = new AssetMemoryBudget();
        long gpu = 0; int uploads = 0;
        var key = TextureContentKey.CreateEncoded(texture, Revision, false);
        var first = pool.Acquire(key, Create);
        var second = pool.Acquire(TextureContentKey.CreateEncoded(Texture(Document(71)), Revision, false),
            () => throw new InvalidOperationException("An alias must share its upload."));
        Assert.Same(first.Resource, second.Resource); Assert.Equal(1, uploads);
        Assert.Equal(8, gpu); Assert.Equal(156, budget.UsedBytes);
        first.Dispose(); Assert.Equal(8, gpu);
        second.Dispose(); Assert.Equal(0, gpu); Assert.Equal(0, budget.UsedBytes); Assert.Equal(0, pool.Count);

        Upload Create()
        {
            uploads++; gpu += document.Info.EncodedLength;
            var ownership = budget.Reserve(document.OriginalBytes.Length, "generated DDS ownership");
            var loader = AssetViewportService.RawTextureLoader.CreateEncoded2D(document, Revision, false, budget);
            return new(loader, ownership, () => gpu -= document.Info.EncodedLength);
        }
    }

    [Fact]
    public void FailedUploadAndDeviceRecreationUseFreshStreamsWithoutLosingTheBacking()
    {
        var document = Document(77);
        using var loader = AssetViewportService.RawTextureLoader.CreateEncoded2D(document, Revision, false, new AssetMemoryBudget());
        var first = loader.Load(Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => loader.Complete(Guid.NewGuid(), first, false));
        Assert.Throws<ObjectDisposedException>(() => first.Texture!.ReadByte());
        var recreated = loader.Load(Guid.NewGuid()); using var output = new MemoryStream(); recreated.Texture!.CopyTo(output);
        Assert.Equal(document.OriginalBytes.ToArray(), output.ToArray());
        loader.Complete(Guid.NewGuid(), recreated, true); loader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => loader.Load(Guid.NewGuid()));
    }

    [Fact]
    public void CorruptContentAndChangedMetadataAreRejectedBeforeAdmission()
    {
        var document = Document(71);
        Assert.True(MemoryMarshal.TryGetArray(document.OriginalBytes, out var bytes));
        bytes.Array![bytes.Offset + 148] ^= 1;
        var budget = new AssetMemoryBudget();
        Assert.Throws<InvalidDataException>(() => AssetViewportService.RawTextureLoader.CreateEncoded2D(document, Revision, false, budget));
        Assert.Equal(0, budget.UsedBytes);
        var changed = Document(71);
        var mips = Assert.IsType<TextureMipLevel[]>(changed.MipLevels); mips[0] = mips[0] with { Width = 8 };
        Assert.Throws<InvalidDataException>(() => TextureContentKey.CreateEncoded(Texture(changed), Revision, false));
        Assert.Throws<InvalidDataException>(() => AssetViewportService.RawTextureLoader.CreateEncoded2D(Document(71),
            Revision with { PhysicalSourceStamp = "replacement" }, false, budget));
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void GammaAliasCopyHonorsAdmissionAndCanBeRetriedAfterPressureReleases()
    {
        var document = Document(78); var budget = new AssetMemoryBudget(document.OriginalBytes.Length);
        using (budget.Reserve(1, "generated pressure"))
        {
            var error = Assert.Throws<ArchiveException>(() => AssetViewportService.RawTextureLoader.CreateEncoded2D(document, Revision, false, budget));
            Assert.Equal("AssetMemoryLimit", error.Code); Assert.Equal(1, budget.UsedBytes);
        }
        using var loader = AssetViewportService.RawTextureLoader.CreateEncoded2D(document, Revision, false, budget);
        Assert.Equal(document.OriginalBytes.Length, budget.UsedBytes);
        loader.Dispose(); Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void AmbiguousOpaqueBc1SamplingKeepsTheRgbaFallback()
    {
        var document = Document(71, redEndpoint: 0, greenEndpoint: 1);
        Assert.False(TextureSurface.CanRetainEncodedDds(document, document.DefaultSelection));
        Assert.Throws<InvalidDataException>(() => AssetViewportService.RawTextureLoader.CreateEncoded2D(document, Revision, false, new AssetMemoryBudget()));
    }

    [Fact]
    public void NativeR8ForwardsOneBytePerPixelWithRedOnlySampling()
    {
        var document = ScalarDocument();
        Assert.Equal(TextureStorageFormat.Luminance8, document.Info.Format);
        Assert.Equal("R8_UNORM", document.Info.FormatName);
        Assert.Equal(16, document.Info.EncodedLength);
        var texture = Texture(document);
        Assert.False(texture.Surface.HasRgba8);
        var key = TextureContentKey.CreateEncoded(texture, Revision, false);
        Assert.Equal(TextureStorageFormat.Luminance8, key.EncodedFormat);
        var budget = new AssetMemoryBudget();
        using var ownedDds = budget.Reserve(document.OriginalBytes.Length, "generated R8 ownership");
        using var loader = AssetViewportService.RawTextureLoader.CreateEncoded2D(document, Revision, false, budget);
        Assert.Equal(164, budget.UsedBytes);
        var info = loader.Load(Guid.NewGuid()); using var output = new MemoryStream(); info.Texture!.CopyTo(output);
        Assert.Equal(document.OriginalBytes.ToArray(), output.ToArray());
        var received = new DdsTextureReader().Read(output.ToArray(), Identity, Revision);
        var pixels = new DdsTextureDecoder().DecodeMip(received);
        for (int offset = 0; offset < pixels.Rgba8.Length; offset += 4)
            Assert.Equal(new byte[] { 200, 0, 0, 255 }, pixels.Rgba8.Span.Slice(offset, 4).ToArray());
        loader.Complete(Guid.NewGuid(), info, true);
        Assert.Throws<InvalidDataException>(() => TextureContentKey.CreateEncoded(texture, Revision, true));
        Assert.Throws<InvalidDataException>(() => AssetViewportService.RawTextureLoader.CreateEncoded2D(document, Revision, true, budget));
        Assert.Equal(164, budget.UsedBytes);
        loader.Dispose(); ownedDds.Dispose(); Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void LegacyLuminanceKeepsItsGreyChannelExpansionAndCannotUseR8Upload()
    {
        var document = ScalarDocument(legacy: true);
        var pixels = new DdsTextureDecoder().DecodeMip(document);
        Assert.Equal(new byte[] { 200, 200, 200, 255 }, pixels.Rgba8.Span[..4].ToArray());
        Assert.False(TextureSurface.CanRetainEncodedDds(document, document.DefaultSelection));
        var budget = new AssetMemoryBudget();
        Assert.Throws<InvalidDataException>(() => AssetViewportService.RawTextureLoader.CreateEncoded2D(document, Revision, false, budget));
        Assert.Equal(0, budget.UsedBytes);
    }

    private static DdsTextureDocument ScalarDocument(bool legacy = false)
    {
        int header = legacy ? 128 : 148;
        var bytes = new byte[header + 16]; "DDS "u8.CopyTo(bytes);
        Put(4, 124); Put(8, 0x100F); Put(12, 4); Put(16, 4); Put(20, 4); Put(28, 1);
        Put(76, 32); Put(108, 0x1000);
        if (legacy)
        {
            Put(80, 0x20000); Put(88, 8); Put(92, 0xFF);
        }
        else
        {
            Put(80, 4); "DX10"u8.CopyTo(bytes.AsSpan(84)); Put(128, 61); Put(132, 3); Put(140, 1);
        }
        Array.Fill(bytes, (byte)200, header, 16);
        return new DdsTextureReader().Read(bytes, Identity, Revision);
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    }

    private static RenderTexture Texture(DdsTextureDocument document)
    {
        var pixels = new DdsTextureDecoder().DecodeMip(document);
        var digest = Convert.ToHexString(SHA256.HashData(pixels.Rgba8.Span)).ToLowerInvariant();
        var surface = TextureSurface.RestoreEncodedDds(4, 4, document, false, document.Info.IsSrgb, 2,
            document.Info.Format, document.Info.FormatName, document.ContentSha256, new(MipIndex: 2), Identity, Revision);
        return new(Identity, surface, digest);
    }

    private static DdsTextureDocument Document(uint format, ushort redEndpoint = 0xF800, ushort greenEndpoint = 0)
    {
        var blockBytes = format is 71 or 72 ? 8 : 16;
        var bytes = new byte[148 + blockBytes]; "DDS "u8.CopyTo(bytes);
        Put(4, 124); Put(8, 0x81007); Put(12, 4); Put(16, 4); Put(20, (uint)blockBytes); Put(28, 1);
        Put(76, 32); Put(80, 4); "DX10"u8.CopyTo(bytes.AsSpan(84)); Put(108, 0x1000);
        Put(128, format); Put(132, 3); Put(140, 1); Put(144, format is 71 or 72 ? 3u : 0);
        var colorOffset = 148;
        if (blockBytes == 16) { bytes[148] = 200; bytes[149] = 50; colorOffset += 8; }
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(colorOffset), redEndpoint);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(colorOffset + 2), greenEndpoint);
        return new DdsTextureReader().Read(bytes, Identity, Revision);
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    }

    private sealed class Upload(AssetViewportService.RawTextureLoader loader, IDisposable source, Action releaseGpu) : IDisposable
    {
        public void Dispose() { loader.Dispose(); source.Dispose(); releaseGpu(); }
    }
}
