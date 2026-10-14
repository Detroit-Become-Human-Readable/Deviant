using System.Buffers.Binary;
using System.IO.Compression;
using Deviant.Assets;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class DdsTextureReaderTests
{
    private readonly DdsTextureReader reader = new();
    private readonly DdsTextureDecoder decoder = new();

    [Fact]
    public void OwnedBufferTransferPreservesValidationAndPixelProvenance()
    {
        var source = TextureFixtureBuilder.CreateDx10(2, 1, 29, [1, 2, 3, 4, 250, 128, 64, 255]);
        var copied = reader.Read(source);
        var transferred = reader.TakeOwnership(source);
        Assert.Equal(copied.ContentSha256, transferred.ContentSha256);
        Assert.Equal(copied.Info, transferred.Info);
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(transferred.OriginalBytes, out var owned));
        Assert.Same(source, owned.Array);
        Assert.Equal(decoder.ViewRgbaSubresource(copied, copied.DefaultSelection).Rgba8.ToArray(),
            decoder.ViewRgbaSubresource(transferred, transferred.DefaultSelection).Rgba8.ToArray());
        Assert.Throws<TextureFormatException>(() => reader.TakeOwnership(new byte[32]));
    }

    [Theory]
    [InlineData(28u)]
    [InlineData(29u)]
    public void RgbaViewSharesOnlyTheOwnedDocumentBytesAndMatchesPixelConversion(uint format)
    {
        byte[] pixels = [1, 2, 3, 4, 250, 128, 64, 255];
        var source = TextureFixtureBuilder.CreateDx10(2, 1, format, pixels);
        var document = reader.Read(source);
        Array.Fill(source, (byte)0);
        var view = decoder.ViewRgbaSubresource(document, document.DefaultSelection);
        Assert.Equal(pixels, view.Rgba8.ToArray());
        Assert.Equal(decoder.DecodeMip(document).Rgba8.ToArray(), view.Rgba8.ToArray());
        Assert.Equal(format == 29, view.IsSrgb);
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(document.OriginalBytes, out var owned));
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(view.Rgba8, out var viewed));
        Assert.Same(owned.Array, viewed.Array); Assert.NotSame(source, viewed.Array);
        Assert.Equal(document.Subresources[0].SourceOffset, viewed.Offset);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => decoder.ViewRgbaSubresource(document, document.DefaultSelection, cancel.Token));
    }

    [Fact]
    public void RgbaViewRejectsFormatsThatNeedConversion()
    {
        var document = reader.Read(TextureFixtureBuilder.CreateDx10(1, 1, 87, [1, 2, 3, 4]));
        Assert.Throws<TextureFormatException>(() => decoder.ViewRgbaSubresource(document, document.DefaultSelection));
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(1u, 0)]
    [InlineData(2u, 0)]
    [InlineData(3u, 255)]
    [InlineData(4u, 0)]
    public async Task HonorsOpaqueBc1AlphaModeWithoutChangingOtherModes(uint mode, int expectedAlpha)
    {
        var bytes = TextureFixtureBuilder.CreateDx10(1, 1, 72,
            TextureFixtureBuilder.SolidDxt1Block(0, 0xFFFF, uint.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(144), mode);
        var document = reader.Read(bytes);
        Assert.True(document.Info.IsSrgb);
        byte[] expected = [0, 0, 0, (byte)expectedAlpha];
        Assert.Equal(expected, decoder.DecodeMip(document).Rgba8.ToArray());
        Assert.Equal(expected, (await decoder.DecodeMipAsync(document)).Rgba8.ToArray());
    }

    [Fact]
    public void ReadsAndDecodesLegacyBc1ToFullRgbaPixels()
    {
        var block = TextureFixtureBuilder.SolidDxt1Block(0xF800, 0x07E0);
        var bytes = TextureFixtureBuilder.CreateDxt1(4, 4, 1, block);

        var dds = reader.Read(bytes);
        var pixels = decoder.DecodeMip(dds);

        Assert.Equal(4, dds.Info.Width);
        Assert.Equal(4, dds.Info.Height);
        Assert.Equal(1, dds.Info.MipCount);
        Assert.Equal(TextureStorageFormat.Bc1, dds.Info.Format);
        Assert.Equal(4 * 4 * 4, pixels.Rgba8.Length);
        Assert.All(Enumerable.Range(0, 16), index =>
        {
            var pixel = pixels.Rgba8.Span.Slice(index * 4, 4);
            Assert.True(pixel[0] > 245);
            Assert.True(pixel[1] < 10);
            Assert.True(pixel[2] < 10);
            Assert.Equal(255, pixel[3]);
        });
    }

    [Fact]
    public void TracksAndDecodesEveryMipInDeclaredOrder()
    {
        var payload = new List<byte>();
        var width = 8;
        var height = 8;
        for (var mip = 0; mip < 4; mip++)
        {
            var byteCount = TextureFixtureBuilder.MipLength(width, height, 8);
            for (var offset = 0; offset < byteCount; offset += 8)
                payload.AddRange(TextureFixtureBuilder.SolidDxt1Block(0x001F, 0xF800));
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
        var bytes = TextureFixtureBuilder.CreateDxt1(8, 8, 4, payload.ToArray());

        var dds = reader.Read(bytes);
        var lastMip = decoder.DecodeMip(dds, 3);

        Assert.Collection(dds.MipLevels,
            mip => Assert.Equal((0, 8, 8, 32), (mip.Index, mip.Width, mip.Height, mip.EncodedLength)),
            mip => Assert.Equal((1, 4, 4, 8), (mip.Index, mip.Width, mip.Height, mip.EncodedLength)),
            mip => Assert.Equal((2, 2, 2, 8), (mip.Index, mip.Width, mip.Height, mip.EncodedLength)),
            mip => Assert.Equal((3, 1, 1, 8), (mip.Index, mip.Width, mip.Height, mip.EncodedLength)));
        Assert.Equal(1, lastMip.Width);
        Assert.Equal(1, lastMip.Height);
        Assert.Equal(4, lastMip.Rgba8.Length);
    }

    [Fact]
    public void DecodesDx10Rgba8WithoutChangingTheSourcePixels()
    {
        byte[] pixels = [3, 17, 222, 255, 244, 100, 12, 127];
        var dds = reader.Read(TextureFixtureBuilder.CreateDx10Rgba8(2, 1, pixels));

        var decoded = decoder.DecodeMip(dds);

        Assert.Equal(TextureStorageFormat.Rgba8, dds.Info.Format);
        Assert.Equal(pixels, decoded.Rgba8.ToArray());
    }

    [Fact]
    public void RetainsHdrFormatAndFloatingPointPixels()
    {
        var bytes = TextureFixtureBuilder.CreateDx10(4, 4, 95, new byte[16]); // DXGI_FORMAT_BC6H_UF16.
        var dds = reader.Read(bytes);

        var decoded = decoder.DecodeMip(dds);

        Assert.True(dds.Info.IsHdr);
        Assert.Equal(TextureStorageFormat.Bc6Unsigned, dds.Info.Format);
        Assert.True(decoded.IsHdr);
        Assert.Equal(16, decoded.HdrPixels.Length);
        Assert.Empty(decoded.Rgba8.ToArray());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => HdrDisplayTransform.ToSdr(decoded, cancellationToken: cancellation.Token));
        using var output = new MemoryStream();
        Assert.Equal("HdrPngRequiresToneMap", Assert.Throws<TextureFormatException>(
            () => PngTextureWriter.Write(output, decoded)).Code);
    }

    [Fact]
    public void HdrDisplayMappingRequiresExplicitExposureAndToneMap()
    {
        var unexposed = HdrDisplayTransform.MapPixel(new HdrPixel(1, 0.18f, 0), 0, TextureToneMapOperator.Reinhard);
        var exposed = HdrDisplayTransform.MapPixel(new HdrPixel(1, 0.18f, 0), 1, TextureToneMapOperator.Reinhard);
        var aces = HdrDisplayTransform.MapPixel(new HdrPixel(1, 0.18f, 0), 0, TextureToneMapOperator.Aces);

        Assert.True(exposed.R > unexposed.R);
        Assert.True(exposed.G > unexposed.G);
        Assert.NotEqual(unexposed.R, aces.R);
        Assert.Equal(255, unexposed.A);
        Assert.Throws<ArgumentOutOfRangeException>(() => HdrDisplayTransform.MapPixel(new HdrPixel(1, 1, 1), 100));
    }

    [Fact]
    public void EstimatesDecoderWorkingSetAndAppliesCallerConfiguredBudget()
    {
        var rgbaDds = reader.Read(TextureFixtureBuilder.CreateDxt1(4, 4, 1,
            TextureFixtureBuilder.SolidDxt1Block(0xF800, 0x07E0)));
        Assert.Equal(128, decoder.EstimateWorkingSetBytes(rgbaDds, rgbaDds.DefaultSelection));

        var largePayload = new byte[TextureFixtureBuilder.MipLength(4098, 4098, 8)];
        var largeDds = reader.Read(TextureFixtureBuilder.CreateDxt1(4098, 4098, 1, largePayload));
        var boundedDecoder = new DdsTextureDecoder(maximumEstimatedWorkingSetBytes: 128);
        Assert.True(boundedDecoder.EstimateWorkingSetBytes(largeDds, largeDds.DefaultSelection) > 128);
        Assert.Equal("DecodeBudgetExceeded", Assert.Throws<TextureFormatException>(() => boundedDecoder.DecodeMip(largeDds)).Code);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DdsTextureDecoder(DdsTextureDecoder.MaximumEstimatedWorkingSetSafetyLimit + 1));
    }

    [Fact]
    public async Task AsyncSubresourceDecodeHonorsCancellationBeforeWorkStarts()
    {
        var dds = reader.Read(TextureFixtureBuilder.CreateDxt1(4, 4, 1,
            TextureFixtureBuilder.SolidDxt1Block(0xF800, 0x07E0)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decoder.DecodeSubresourceAsync(
            dds, dds.DefaultSelection, cancellation.Token));
    }

    [Fact]
    public void PreservesTrailingBytesWithoutIncludingThemInMipData()
    {
        var bytes = TextureFixtureBuilder.CreateDxt1(4, 4, 1,
            TextureFixtureBuilder.SolidDxt1Block(0xF800, 0x07E0), addTrailingByte: true);

        var dds = reader.Read(bytes);

        Assert.Equal(8, dds.Info.EncodedLength);
        Assert.Equal(1, dds.Info.TrailingLength);
        Assert.Equal(bytes, dds.OriginalBytes.ToArray());
    }

    [Fact]
    public void SnapshotOwnsTheValidatedSourceBytes()
    {
        var source = TextureFixtureBuilder.CreateDxt1(4, 4, 1,
            TextureFixtureBuilder.SolidDxt1Block(0xF800, 0x07E0));
        var dds = reader.Read(source);
        source[0] = 0;

        Assert.Equal((byte)'D', dds.OriginalBytes.Span[0]);
    }

    [Fact]
    public void RejectsBadHeaderTruncatedMipAndInvalidMipCount()
    {
        var valid = TextureFixtureBuilder.CreateDxt1(4, 4, 1,
            TextureFixtureBuilder.SolidDxt1Block(0xF800, 0x07E0));
        var badHeader = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(badHeader.AsSpan(4), 123);
        var truncated = valid[..^1];
        var invalidMips = TextureFixtureBuilder.CreateDxt1(4, 4, 1,
            TextureFixtureBuilder.SolidDxt1Block(0xF800, 0x07E0));
        BinaryPrimitives.WriteUInt32LittleEndian(invalidMips.AsSpan(8), 0x0002_1007);
        BinaryPrimitives.WriteUInt32LittleEndian(invalidMips.AsSpan(28), 4);

        Assert.Equal("InvalidHeader", Assert.Throws<TextureFormatException>(() => reader.Read(badHeader)).Code);
        Assert.Equal("TruncatedSubresource", Assert.Throws<TextureFormatException>(() => reader.Read(truncated)).Code);
        Assert.Equal("InvalidMipCount", Assert.Throws<TextureFormatException>(() => reader.Read(invalidMips)).Code);
    }

    [Fact]
    public void RejectsUnadvertisedDx10ArraysAndUnknownFormats()
    {
        var array = TextureFixtureBuilder.CreateDx10Rgba8(1, 1, [0, 0, 0, 255]);
        BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(140), 2);
        var unknown = TextureFixtureBuilder.CreateDx10Rgba8(1, 1, [0, 0, 0, 255]);
        BinaryPrimitives.WriteUInt32LittleEndian(unknown.AsSpan(128), 9999);

        Assert.Equal("InvalidArrayCaps", Assert.Throws<TextureFormatException>(() => reader.Read(array)).Code);
        Assert.Equal("UnsupportedFormat", Assert.Throws<TextureFormatException>(() => reader.Read(unknown)).Code);
    }

    [Fact]
    public void DecodesDx10TextureArrayInArrayThenMipOrder()
    {
        var payload = new byte[64 + 16 + 64 + 16];
        Array.Fill(payload, (byte)10, 0, 64);       // layer 0 mip 0
        Array.Fill(payload, (byte)20, 64, 16);      // layer 0 mip 1
        Array.Fill(payload, (byte)30, 80, 64);      // layer 1 mip 0
        Array.Fill(payload, (byte)40, 144, 16);    // layer 1 mip 1
        var bytes = TextureFixtureBuilder.CreateDx10(4, 4, 28, payload, arraySize: 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x0002_1007u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108), 0x0040_1008u);

        var dds = reader.Read(bytes);
        var selection = new TextureSubresourceSelection(ArrayLayer: 1, MipIndex: 1);
        var surface = decoder.DecodeSubresource(dds, selection);

        Assert.Equal(2, dds.Info.ArrayLayerCount);
        Assert.False(dds.Info.IsCubeMap);
        Assert.Collection(dds.Subresources,
            sub => Assert.Equal((0, 0, 4, 4), (sub.Selection.ArrayLayer, sub.Selection.MipIndex, sub.Width, sub.Height)),
            sub => Assert.Equal((0, 1, 2, 2), (sub.Selection.ArrayLayer, sub.Selection.MipIndex, sub.Width, sub.Height)),
            sub => Assert.Equal((1, 0, 4, 4), (sub.Selection.ArrayLayer, sub.Selection.MipIndex, sub.Width, sub.Height)),
            sub => Assert.Equal((1, 1, 2, 2), (sub.Selection.ArrayLayer, sub.Selection.MipIndex, sub.Width, sub.Height)));
        Assert.Equal(selection, surface.Subresource);
        Assert.Equal(2, surface.Width);
        Assert.All(Enumerable.Range(0, 4), index => Assert.Equal((byte)40, surface.Rgba8.Span[index * 4]));
    }

    [Fact]
    public void DecodesDx10CubemapArrayUsingSpecifiedFaceOrder()
    {
        var payload = new byte[2 * 6 * 4];
        for (var layer = 0; layer < 2; layer++)
        for (var face = 0; face < 6; face++)
        {
            var offset = (layer * 6 + face) * 4;
            payload[offset] = checked((byte)(layer * 60 + face * 10));
            payload[offset + 3] = 255;
        }
        var bytes = TextureFixtureBuilder.CreateDx10(1, 1, 28, payload, arraySize: 2, miscFlag: 0x4);
        var dds = reader.Read(bytes);
        var selection = new TextureSubresourceSelection(1, CubeFace.NegativeZ, 0);
        var surface = decoder.DecodeSubresource(dds, selection);

        Assert.True(dds.Info.IsCubeMap);
        Assert.Equal(2, dds.Info.ArrayLayerCount);
        Assert.Equal(new[] { CubeFace.PositiveX, CubeFace.NegativeX, CubeFace.PositiveY,
            CubeFace.NegativeY, CubeFace.PositiveZ, CubeFace.NegativeZ }, dds.Info.CubeFaces);
        Assert.Equal(12, dds.Subresources.Count);
        Assert.Equal((1, CubeFace.NegativeZ), (surface.Subresource.ArrayLayer, surface.Subresource.Face));
        Assert.Equal(110, surface.Rgba8.Span[0]);
    }

    [Fact]
    public void DecodesLegacyPartialCubemapFacesAndRejectsAbsentFaceSelection()
    {
        var payload = TextureFixtureBuilder.SolidDxt1Block(0xF800, 0x07E0)
            .Concat(TextureFixtureBuilder.SolidDxt1Block(0x07E0, 0xF800)).ToArray();
        var bytes = TextureFixtureBuilder.CreateLegacyCube(4, 4, 0x0000_0400u | 0x0000_8000u, payload);
        var dds = reader.Read(bytes);
        var negativeZ = decoder.DecodeSubresource(dds, new TextureSubresourceSelection(Face: CubeFace.NegativeZ));
        var firstFace = decoder.DecodeMip(dds);

        Assert.Equal(new[] { CubeFace.PositiveX, CubeFace.NegativeZ }, dds.Info.CubeFaces);
        Assert.Equal(CubeFace.NegativeZ, negativeZ.Subresource.Face);
        Assert.True(negativeZ.Rgba8.Span[1] > 245);
        Assert.Equal(CubeFace.PositiveX, firstFace.Subresource.Face);
        Assert.True(firstFace.Rgba8.Span[0] > 245);
        Assert.Equal("SubresourceNotFound", Assert.Throws<TextureFormatException>(() =>
            decoder.DecodeSubresource(dds, new TextureSubresourceSelection(Face: CubeFace.NegativeX))).Code);
    }

    [Fact]
    public void RejectsTruncatedArraysAndInconsistentLegacyFaceMasks()
    {
        var truncatedArray = TextureFixtureBuilder.CreateDx10(1, 1, 28, [1, 2, 3, 255, 4, 5, 6], arraySize: 2);
        var faceMaskWithoutCube = TextureFixtureBuilder.CreateLegacyCube(4, 4, 0x0000_0400,
            TextureFixtureBuilder.SolidDxt1Block(0xF800, 0x07E0));
        BinaryPrimitives.WriteUInt32LittleEndian(faceMaskWithoutCube.AsSpan(112), 0x0000_0400);
        var nonSquare = TextureFixtureBuilder.CreateLegacyCube(4, 2, 0x0000_0400,
            TextureFixtureBuilder.SolidDxt1Block(0xF800, 0x07E0));

        Assert.Equal("TruncatedSubresource", Assert.Throws<TextureFormatException>(() => reader.Read(truncatedArray)).Code);
        Assert.Equal("InvalidCubeFaceFlags", Assert.Throws<TextureFormatException>(() => reader.Read(faceMaskWithoutCube)).Code);
        Assert.Equal("InvalidCubeDimensions", Assert.Throws<TextureFormatException>(() => reader.Read(nonSquare)).Code);
    }

    [Fact]
    public void ParsesKnownSignedBcnExtentButDoesNotAdvertisePixelDecoding()
    {
        var dds = reader.Read(TextureFixtureBuilder.CreateDx10(4, 4, 81, new byte[8])); // DXGI_FORMAT_BC4_SNORM.
        var identity = new ResourceIdentity("index-hash", 5);
        var revision = new SourceRevision("index-hash", "physical-stamp");
        var bound = reader.Read(dds.OriginalBytes, identity, revision);
        var descriptor = DdsAssetDescriptorFactory.Create(bound, identity, revision, "Signed BC texture",
            [new SourceExtent("texture.dat", 0, bound.OriginalBytes.Length)]);

        Assert.Equal(TextureStorageFormat.Bc4Signed, dds.Info.Format);
        Assert.Equal(8, dds.MipLevels[0].EncodedLength);
        Assert.False(dds.Info.PixelDecodeSupported);
        Assert.True(descriptor.Capabilities.HasFlag(AssetCapabilities.Original));
        Assert.True(descriptor.Capabilities.HasFlag(AssetCapabilities.Dds));
        Assert.False(descriptor.Capabilities.HasFlag(AssetCapabilities.Preview));
        Assert.Equal("UnsupportedFormat", Assert.Throws<TextureFormatException>(() => decoder.DecodeMip(dds)).Code);
    }

    [Fact]
    public void CreatesAspectPreservingThumbnail()
    {
        var pixels = new byte[4 * 2 * 4];
        for (var i = 0; i < 8; i++)
        {
            pixels[i * 4] = (byte)(i * 20);
            pixels[i * 4 + 3] = 255;
        }
        var source = decoder.DecodeMip(reader.Read(TextureFixtureBuilder.CreateDx10Rgba8(4, 2, pixels)));

        var thumbnail = decoder.CreateThumbnail(source, 2);

        Assert.Equal(2, thumbnail.Width);
        Assert.Equal(1, thumbnail.Height);
        Assert.Equal(8, thumbnail.Rgba8.Length);
    }

    [Fact]
    public void WritesPngWithValidChunksAndPixelRows()
    {
        byte[] rgba = [10, 20, 30, 40, 50, 60, 70, 80];
        var surface = decoder.DecodeMip(reader.Read(TextureFixtureBuilder.CreateDx10Rgba8(2, 1, rgba)));
        using var output = new MemoryStream();

        PngTextureWriter.Write(output, surface, isSrgb: false);

        var png = output.ToArray();
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)));
        Assert.Equal(8, png[24]);
        Assert.Equal(6, png[25]);

        var idat = ReadSingleChunk(png, "IDAT");
        using var compressed = new MemoryStream(idat);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var decodedRows = new MemoryStream();
        zlib.CopyTo(decodedRows);
        Assert.Equal(new byte[] { 0 }.Concat(rgba), decodedRows.ToArray());
        Assert.True(HasValidChunkCrcs(png));
    }

    [Fact]
    public async Task ReadsArchiveDdsThroughResolverAndRetainsRevisionIdentity()
    {
        var identity = new ResourceIdentity("index-hash", 7);
        var revision = new SourceRevision("index-hash", "physical-stamp");
        var source = TextureFixtureBuilder.CreateDx10Rgba8(1, 1, [1, 2, 3, 255]);
        var resolver = new FixedResolver(source);

        var document = await reader.ReadAsync(resolver, identity, revision, source.Length);
        var descriptor = DdsAssetDescriptorFactory.Create(document, identity, revision, "Test texture",
            [new SourceExtent("segment.dat", 0, source.Length)]);

        Assert.Equal(identity, document.Identity);
        Assert.Equal(revision, document.Revision);
        Assert.Equal("1", descriptor.Metadata["width"]);
        Assert.True(descriptor.Capabilities.HasFlag(AssetCapabilities.Png));
        Assert.Single(resolver.Reads);
    }

    private static byte[] ReadSingleChunk(byte[] png, string name)
    {
        var offset = 8;
        while (offset < png.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == name) return png.AsSpan(offset + 8, length).ToArray();
            offset = checked(offset + length + 12);
        }
        throw new InvalidDataException($"PNG chunk {name} was not found.");
    }

    private static bool HasValidChunkCrcs(byte[] png)
    {
        var offset = 8;
        while (offset < png.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            var crc = ComputeCrc(png.AsSpan(offset + 4, 4 + length));
            if (crc != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length, 4))) return false;
            offset = checked(offset + length + 12);
        }
        return offset == png.Length;
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB8_8320u);
        }
        return ~crc;
    }

    private sealed class FixedResolver(byte[] source) : IResourceResolver
    {
        public List<(long Offset, int Length)> Reads { get; } = [];
        public ReferenceResolution Resolve(ResourceReference reference) => new(ReferenceStatus.Unresolved, []);

        public Task<byte[]> ReadAsync(ResourceIdentity identity, long resourceOffset, int length,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Reads.Add((resourceOffset, length));
            return Task.FromResult(source.AsSpan(checked((int)resourceOffset), length).ToArray());
        }
    }
}
