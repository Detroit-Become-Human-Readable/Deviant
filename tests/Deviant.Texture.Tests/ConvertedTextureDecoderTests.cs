using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class ConvertedTextureDecoderTests
{
    [Theory]
    [InlineData(21, 18)]
    [InlineData(7, 5)]
    public void SceneTextureDecoderUsesSelectedConvertedMipAllowanceAndReservesWorkingSet(ushort selector, byte nativeFormat)
    {
        var source = ConvertedTextureFixture.Source(soleRaw: true, selector: selector,
            width: 4096, height: 4096, emptyPayload: true);
        Assert.Equal(nativeFormat, source.Metadata.FirstByte);

        var decoder = ConvertedTextureFixture.SceneTextureDecoder(source, 1024);
        var estimatedWorkingSet = decoder.EstimatePreviewWorkingSetBytes(source, 1024);
        Assert.Equal(3L * 4096 * 4096 * 4 + 1024 * 1024, estimatedWorkingSet);

        var admitted = new AssetMemoryBudget(estimatedWorkingSet);
        using (admitted.Reserve(estimatedWorkingSet, "generated converted scene preview"))
            Assert.Equal(estimatedWorkingSet, admitted.UsedBytes);
        Assert.Equal(0, admitted.UsedBytes);

        var insufficient = new AssetMemoryBudget(estimatedWorkingSet - 1);
        Assert.Throws<ArchiveException>(() =>
        {
            using var rejected = insufficient.Reserve(estimatedWorkingSet, "generated converted scene preview");
        });
        Assert.Equal(0, insufficient.UsedBytes);
    }

    [Fact]
    public void SceneTextureDecoderRejectsConvertedMipAboveExisting128MiBCeiling()
    {
        var source = ConvertedTextureFixture.Source(soleRaw: true, selector: 21,
            width: 8192, height: 8192, emptyPayload: true);
        var error = Assert.Throws<TextureFormatException>(() => ConvertedTextureFixture.SceneTextureDecoder(source, 1024));
        Assert.Equal("NativeTextureDecodeBudget", error.Code);
    }

    [Fact]
    public void ThreeLumaPlanesPreserveRgbWithoutTheChromaTransform()
    {
        var packet = ConvertedTextureFixture.Packet(16, 8, 19, red: 40, green: 120, blue: 220);
        var result = new ConvertedTextureDecoder().Read(packet, 19, 16, 8, 512);
        Assert.All(Enumerable.Range(0, 128), pixel =>
            Assert.Equal(new byte[] { 40, 120, 220, 0 }, result.Rgba8.Slice(pixel * 4, 4).ToArray()));
        var native = new NativeTextureDecoder().ReadDetailed(ConvertedTextureFixture.Source(soleRaw: true, selector: 19));
        Assert.Equal(new byte[] { 64, 192, 96, 255 }, native.Document.OriginalBytes.Slice(148, 4).ToArray());
    }
    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(200)]
    [InlineData(255)]
    public void SingleRedPlanePreservesItsScalarSamplesAndSamplingChannels(byte red)
    {
        var packet = ConvertedTextureFixture.Packet(16, 8, 15, red: red);
        var result = new ConvertedTextureDecoder().Read(packet, 15, 16, 8, 512);
        Assert.All(Enumerable.Range(0, 128), pixel =>
            Assert.Equal(new byte[] { red, 0, 0, 255 }, result.Rgba8.Slice(pixel * 4, 4).ToArray()));
        Assert.Equal(512, Assert.Single(result.Info.Chunks).NormalizedLength);
        var source = ConvertedTextureFixture.Source(soleRaw: true, selector: 15);
        var native = new NativeTextureDecoder().ReadDetailed(source);
        Assert.Equal(15, native.Conversion!.Selector);
        Assert.Equal(new byte[] { 64, 0, 0, 255 }, native.Document.OriginalBytes.Slice(148, 4).ToArray());
    }

    [Theory]
    [InlineData(64)]
    [InlineData(-64)]
    public void SingleRedPlaneKeepsSignedVerticalFrequencyAndRowOrder(int coefficient)
    {
        var result = new ConvertedTextureDecoder().Read(
            ConvertedTextureFixture.Packet(8, 8, 15, verticalAc: coefficient, red: 128), 15, 8, 8, 256);
        int[] rows = [139, 137, 134, 130, 126, 122, 119, 117];
        for (var row = 0; row < 8; row++)
            for (var column = 0; column < 8; column++)
                Assert.Equal(new byte[] { (byte)(coefficient > 0 ? rows[row] : rows[7 - row]), 0, 0, 255 },
                    result.Rgba8.Slice((row * 8 + column) * 4, 4).ToArray());
    }

    [Theory]
    [InlineData(64, 192)]
    [InlineData(200, 40)]
    public void TwoLumaPlanesKeepIndependentPredictorsAndPackedChannels(byte red, byte green)
    {
        var packet = ConvertedTextureFixture.Packet(16, 8, 17, red: red, green: green);
        var decoded = new ConvertedTextureDecoder().Read(packet, 17, 16, 8, 512);
        Assert.All(Enumerable.Range(0, 128), pixel =>
            Assert.Equal(new byte[] { red, green, 0, 0 }, decoded.Rgba8.Slice(pixel * 4, 4).ToArray()));
        Assert.Equal(64, Assert.Single(decoded.Info.Chunks).NormalizedLength);
        Assert.Equal(packet.Length, decoded.Info.ConsumedBodyBytes);
    }

    [Fact]
    public void NativeTwoPlaneTextureUsesExistingMipSelectionAndSourceChecks()
    {
        var source = ConvertedTextureFixture.Source(selector: 17);
        var decoded = new NativeTextureDecoder().ReadDetailed(source, maximumPreviewDimension: 16);
        Assert.Equal(17, decoded.Conversion!.Selector);
        Assert.Equal(16, decoded.Document.Info.Width);
        Assert.Equal(1, Assert.Single(decoded.SupportedMips).MipIndex);
        Assert.Equal(new byte[] { 64, 192, 0, 255 }, decoded.Document.OriginalBytes.Slice(148, 4).ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(decoded.Document.OriginalBytes.Span[148..])).ToLowerInvariant(),
            Assert.Single(decoded.SupportedMips).DecodedSha256);
    }

    [Theory]
    [InlineData("normalized-size")]
    [InlineData("truncated")]
    [InlineData("budget")]
    public void TwoPlanePacketsRetainRangeAndBudgetLimits(string defect)
    {
        var packet = ConvertedTextureFixture.Packet(8, 8, 17);
        if (defect == "normalized-size") BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(60), 1);
        if (defect == "truncated") packet = packet[..^1];
        Assert.Throws<TextureFormatException>(() => new ConvertedTextureDecoder().Read(
            packet, 17, 8, 8, defect == "budget" ? 255 : 256));
    }

    [Theory]
    [InlineData(7, 255)]
    [InlineData(1, 0)]
    [InlineData(1, 127)]
    [InlineData(1, 255)]
    public void ReconstructsGeneratedColorAndAlphaWithoutApplyingHeaderFactors(int selector, byte alpha)
    {
        var packet = ConvertedTextureFixture.Packet(16, 16, (ushort)selector, alpha);
        var decoded = new ConvertedTextureDecoder().Read(packet, (ushort)selector, 16, 16, 1024);
        Assert.All(Enumerable.Range(0, 256), pixel =>
            Assert.Equal(new byte[] { 128, 128, 128, alpha }, decoded.Rgba8.Slice(pixel * 4, 4).ToArray()));
        Assert.Equal(new float[] { 0.2f, 0.3f, 0.4f }, decoded.Info.Scale);
        Assert.Equal(new float[] { 0.1f, 0.2f, 0.3f }, decoded.Info.Bias);
        Assert.Equal(0, decoded.Info.TrailingBodyBytes);
        Assert.Equal(decoded.Info.Chunks[0].EncodedLength, (decoded.Info.Chunks[0].BitsConsumed + 7) / 8);
    }

    [Fact]
    public void MaskSamplesPreserveRedOnlyComponentSemantics()
    {
        var decoded = new ConvertedTextureDecoder().Read(ConvertedTextureFixture.Packet(8, 8, 21), 21, 8, 8, 256);
        Assert.All(Enumerable.Range(0, 64), pixel =>
            Assert.Equal(new byte[] { 128, 0, 0, 255 }, decoded.Rgba8.Slice(pixel * 4, 4).ToArray()));
    }

    [Fact]
    public void TwoChannelMaskUsesIndependentLumaPlanesAndOpaqueSamplingAlpha()
    {
        var packet = ConvertedTextureFixture.Packet(8, 8, 23, red: 37, green: 209);
        var decoded = new ConvertedTextureDecoder().Read(packet, 23, 8, 8, 256);
        Assert.All(Enumerable.Range(0, 64), pixel =>
            Assert.Equal(new byte[] { 37, 209, 0, 255 }, decoded.Rgba8.Slice(pixel * 4, 4).ToArray()));
        Assert.Equal(64, Assert.Single(decoded.Info.Chunks).NormalizedLength);
    }

    [Fact]
    public void NativeTwoChannelConversionPreservesLinearSamplesAndSourceMipCoverage()
    {
        var source = ConvertedTextureFixture.Source(selector: 23);
        var decoded = new NativeTextureDecoder().ReadDetailed(source);
        var surface = new DdsTextureDecoder().DecodeMip(decoded.Document);
        Assert.False(surface.IsSrgb);
        Assert.Equal(32, surface.Width);
        Assert.Equal(new byte[] { 64, 192, 0, 255 }, surface.Rgba8.Slice(0, 4).ToArray());
        Assert.Equal(23, decoded.Conversion!.Selector);
        Assert.Single(decoded.SupportedMips);
        Assert.Equal(5, decoded.OmittedMipIndices.Count);
    }

    [Fact]
    public void TwoChannelConversionRejectsSingleChannelNormalizedExtent()
    {
        var packet = ConvertedTextureFixture.Packet(8, 8, 23);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(60), 32);
        Assert.Equal("InvalidConvertedTextureRange", Assert.Throws<TextureFormatException>(() =>
            new ConvertedTextureDecoder().Read(packet, 23, 8, 8, 256)).Code);
    }

    [Fact]
    public void ReconstructsNonzeroVerticalAcWithIntegerColumnRounding()
    {
        var decoded = new ConvertedTextureDecoder().Read(
            ConvertedTextureFixture.Packet(16, 16, 7, verticalAc: 64), 7, 16, 16, 1024);
        byte[] rows = [139, 137, 134, 130, 126, 122, 119, 117];
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
            Assert.Equal(new byte[] { rows[y % 8], rows[y % 8], rows[y % 8], 255 },
                decoded.Rgba8.Slice((y * 16 + x) * 4, 4).ToArray());
    }

    [Theory]
    [InlineData("version")]
    [InlineData("selector")]
    [InlineData("quality")]
    [InlineData("dimensions")]
    [InlineData("count")]
    [InlineData("reserved-count")]
    [InlineData("reserved-tile")]
    [InlineData("offset")]
    [InlineData("encoded-size")]
    [InlineData("normalized-size")]
    [InlineData("literal")]
    [InlineData("truncated")]
    public void RejectsMalformedPacketBeforeReturningPixels(string defect)
    {
        var packet = ConvertedTextureFixture.Packet(16, 16, 1);
        switch (defect)
        {
            case "version": packet[0] = 15; break;
            case "selector": packet[2] = 7; break;
            case "quality": packet[4] = 101; break;
            case "dimensions": packet[6] = 32; break;
            case "count": BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(40), ushort.MaxValue); break;
            case "reserved-count": packet[42] = 1; break;
            case "reserved-tile": packet[54] = 1; break;
            case "offset": BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(64), 0); break;
            case "encoded-size": BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(56), (uint)packet.Length); break;
            case "normalized-size": BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(60), 1); break;
            case "literal": packet[52] = 0; break;
            default: packet = packet[..^1]; break;
        }
        Assert.Throws<TextureFormatException>(() => new ConvertedTextureDecoder().Read(packet, 1, 16, 16, 1024));
    }

    [Fact]
    public void RejectsExtraEntropyInvalidCodesAndDecodeBudget()
    {
        var packet = ConvertedTextureFixture.Packet(16, 16, 1);
        var longer = packet.Concat(new byte[] { 0 }).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(longer.AsSpan(56), BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(56)) + 1);
        Assert.Equal("ConvertedTextureEntropyExtent", Assert.Throws<TextureFormatException>(() =>
            new ConvertedTextureDecoder().Read(longer, 1, 16, 16, 1024)).Code);
        var broken = packet.ToArray(); Array.Fill(broken, (byte)255, 80, broken.Length - 80);
        Assert.Throws<TextureFormatException>(() => new ConvertedTextureDecoder().Read(broken, 1, 16, 16, 1024));
        Assert.Equal("ConvertedTextureBudget", Assert.Throws<TextureFormatException>(() =>
            new ConvertedTextureDecoder().Read(packet, 1, 16, 16, 1023)).Code);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new ConvertedTextureDecoder().Read(packet, 1, 16, 16, 1024, cancelled.Token));
    }

    [Fact]
    public void NativeAdapterUsesPacketQualityKeepsOtherMipsOmittedAndPreservesHashes()
    {
        var source = ConvertedTextureFixture.Source();
        var decoded = new NativeTextureDecoder().ReadDetailed(source);
        Assert.Equal(TextureStorageFormat.Rgba8, decoded.Document.Info.Format);
        Assert.True(decoded.Document.Info.IsSrgb);
        Assert.Equal(100, decoded.Conversion!.Quality); // FILETEXT's quality byte is 60.
        Assert.Equal(60, source.Metadata.PostSelectorByte0);
        Assert.Equal(source.Metadata.Identity, decoded.Document.Identity);
        Assert.Equal(source.Revision, decoded.Document.Revision);
        Assert.Equal(source.Payloads[1].Sha256, Assert.Single(decoded.SupportedMips).OriginalSha256);
        Assert.Equal(0, decoded.SupportedMips[0].MipIndex);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, decoded.OmittedMipIndices);
        var pixels = new DdsTextureDecoder().DecodeMip(decoded.Document).Rgba8;
        Assert.Equal(new byte[] { 128, 128, 128, 127 }, pixels[..4].ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(pixels.Span)).ToLowerInvariant(), decoded.SupportedMips[0].DecodedSha256);
    }

    [Fact]
    public void PreviewSelectsRealMipAndEstimatorUsesTheSameSelectionAndFallback()
    {
        var source = ConvertedTextureFixture.Source(); var decoder = new NativeTextureDecoder();
        var preview = decoder.ReadDetailed(source, maximumPreviewDimension: 16);
        Assert.Equal(16, preview.Document.Info.Width); Assert.Equal(16, preview.Document.Info.Height);
        Assert.Equal(1, Assert.Single(preview.SupportedMips).MipIndex);
        Assert.Equal(new[] { 0, 2, 3, 4, 5 }, preview.OmittedMipIndices);
        Assert.Equal(3 * 16 * 16 * 4 + 1024 * 1024, decoder.EstimateWorkingSetBytes(source, 16));
        var fallback = decoder.ReadDetailed(source, maximumPreviewDimension: 8);
        Assert.Equal(32, fallback.Document.Info.Width); Assert.Equal(0, fallback.SupportedMips[0].MipIndex);
        Assert.Equal(3 * 32 * 32 * 4 + 1024 * 1024, decoder.EstimateWorkingSetBytes(source, 8));
        Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder(1023).ReadDetailed(source, maximumPreviewDimension: 16));
    }

    [Fact]
    public void SourceLoadedLegacyConvertedPreviewStillRejectsRgbaOutputAboveBudget()
    {
        var source = ConvertedTextureFixture.LargeLegacyBc4Source();
        var decoder = new NativeTextureDecoder();

        var estimate = Assert.Throws<TextureFormatException>(() => decoder.EstimatePreviewWorkingSetBytes(source, 1024));
        Assert.Equal("NativeTextureDecodeBudget", estimate.Code);
        var preview = Assert.Throws<TextureFormatException>(() => decoder.ReadPreview(source, 1024));
        Assert.Equal("NativeTextureDecodeBudget", preview.Code);
    }

    [Fact]
    public void SoleRawBaseRetainsTrailingEncodedDataWithoutClaimingItsMips()
    {
        var source = ConvertedTextureFixture.Source(soleRaw: true);
        var decoded = new NativeTextureDecoder().ReadDetailed(source);
        Assert.Equal(4, decoded.Conversion!.TrailingBodyBytes);
        Assert.Equal(1, decoded.Document.Info.MipCount);
        Assert.Equal(5, decoded.OmittedMipIndices.Count);
        Assert.Equal(source.SelectedIdentity, decoded.SupportedMips[0].Identity);
    }

    [Fact]
    public void RejectsSerializedMetadataMismatchAndChangedConvertedSource()
    {
        var source = ConvertedTextureFixture.Source(); var decoder = new NativeTextureDecoder();
        Assert.Equal("NativeTextureMetadataMismatch", Assert.Throws<TextureFormatException>(() =>
            decoder.Read(source with { Metadata = source.Metadata with { Mip0Width = 16 } })).Code);
        var payload = source.Payloads[1]; var bytes = payload.OriginalBytes.ToArray(); bytes[^1] ^= 1;
        Assert.Equal("NativeTextureSourceMismatch", Assert.Throws<TextureFormatException>(() =>
            decoder.Read(source with { Payloads = source.Payloads.Select(value => value == payload ? payload with { OriginalBytes = bytes } : value).ToArray() })).Code);
    }

    [Fact]
    public void AcceptsZeroLodDepthForConvertedTwoDimensionalTexturesAndRejectsOtherShapes()
    {
        var source = ConvertedTextureFixture.Source(firstLodDepth: 0);
        var full = new NativeTextureDecoder().ReadDetailed(source);
        Assert.Equal(32, full.Document.Info.Width);
        Assert.Equal(0, full.SupportedMips[0].MipIndex);
        var preview = new NativeTextureDecoder().ReadDetailed(source, maximumPreviewDimension: 16);
        Assert.Equal(16, preview.Document.Info.Width);
        Assert.Equal(1, preview.SupportedMips[0].MipIndex);
        Assert.Equal("NativeMipDescriptorMismatch", Assert.Throws<TextureFormatException>(() =>
            new NativeTextureDecoder().Read(ConvertedTextureFixture.Source(firstLodDepth: 2))).Code);
    }
}

internal static class ConvertedTextureFixture
{
    internal static NativeTextureSource LargeLegacyBc4Source()
    {
        const int width = 4096;
        const int height = 4096;
        const string index = "generated-bc4-legacy-index";
        const ushort selector = 21;
        const byte format = 18;
        var catalog = new ResourceIdentity(index, 0);
        var container = new ResourceIdentity(index, 1);
        var revision = new SourceRevision(index, "generated-bc4-legacy-source");
        var raw = Payload(new(index, 2), 2229, 10, Wrap(new byte[96], raw: true));
        var prefix = new byte[84]; prefix[0] = format;
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(1), width);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(3), height);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(5), 1);
        prefix[7] = 1; prefix[12] = 1;
        using var encodedStream = new MemoryStream();
        using (var writer = new BinaryWriter(encodedStream, System.Text.Encoding.UTF8, true))
        {
            writer.Write("FILETEXT"u8); writer.Write(24u); writer.Write(prefix);
            writer.Write((byte)1); writer.Write((byte)0); writer.Write((byte)1);
            writer.Write((ushort)width); writer.Write((ushort)height); writer.Write((ushort)1); writer.Write((byte)1);
            writer.Write(true); writer.Write((byte)60); writer.Write((byte)selector);
            writer.Write(0u); writer.Write(2229u); writer.Write(10u); writer.Write(8_388_608u);
        }
        var encoded = encodedStream.ToArray();
        var row = new TextureCatalogReference(0, TextureCatalogEntryKind.Raw,
            new(IdentifierNamespace.Archive, raw.Id.ToString(), catalog, raw.Type), ReferenceStatus.Unique,
            [raw.Identity], 0, new("generated.dat", 0, 9), false, null);
        var membership = new TextureCatalogMembership(catalog, revision, 1, new("generated.dat", 0, 9), 1, 0, [row]);
        var metadata = new TextureFileTextV24Reader().Parse(encoded, 0, encoded.Length, container, revision,
            new(container, 0, encoded.Length), [new("generated.dat", 0, encoded.Length)], membership).Value!;
        return new(raw.Identity, revision, catalog, "generated-bc4-legacy-catalog",
            Payload(container, 29, 2, [1, 2, 3]), metadata, encoded, Hash(encoded), [raw]);
    }

    internal static byte[] Packet(int width, int height, ushort selector, byte alpha = 127, int verticalAc = 0,
        byte red = 64, byte green = 192, byte blue = 96)
    {
        var bits = new List<int>(); var predictors = new int[4]; var edge = selector is 15 or 17 or 19 or 21 or 23 ? 8 : 16;
        for (var tile = 0; tile < width * height / (edge * edge); tile++)
        for (var plane = 0; plane < (selector is 17 or 23 ? 2 : selector == 19 ? 3 : selector is 15 or 21 ? 1 : selector == 1 ? 10 : 6); plane++)
        {
            var component = selector is 17 or 19 or 23 ? plane : plane < 4 ? 0 : plane == 4 ? 1 : plane == 5 ? 2 : 3;
            var chroma = selector is not (17 or 19 or 23) && component is 1 or 2;
            var value = selector == 15 ? red * 8 : selector is 17 or 19 or 23 ? (plane == 0 ? red : plane == 1 ? green : blue) * 8 : component == 0 ? 1024 : component == 3 ? alpha * 8 : 0;
            var difference = value - predictors[component]; predictors[component] = value;
            var category = difference == 0 ? 0 : System.Numerics.BitOperations.Log2((uint)Math.Abs(difference)) + 1;
            var code = chroma ? ChromaDc[category] : LumaDc[category];
            Write(code);
            if (category > 0)
            {
                var encoded = difference < 0 ? difference - 1 + (1 << category) : difference;
                for (var bit = category - 1; bit >= 0; bit--) bits.Add((encoded >> bit) & 1);
            }
            if (component == 0 && verticalAc != 0)
            {
                if (verticalAc is not (64 or -64)) throw new ArgumentOutOfRangeException(nameof(verticalAc));
                // One coefficient at zigzag position 2 (vertical frequency 1), run 1 / size 7.
                Write("1111111110000101"); Write(verticalAc > 0 ? "1000000" : "0111111");
            }
            Write(chroma ? "00" : "1010");
        }
        var encodedBytes = new byte[(bits.Count + 7) / 8];
        for (var bit = 0; bit < bits.Count; bit++) encodedBytes[bit / 8] |= (byte)(bits[bit] << (7 - bit % 8));
        var packet = new byte[80 + encodedBytes.Length];
        U16(0, 16); U16(2, selector); U16(4, 100); U16(6, (ushort)width); U16(8, (ushort)height); U16(40, 1);
        for (var field = 0; field < 3; field++)
        { BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(12 + field * 4), new float[] { 0.2f, 0.3f, 0.4f }[field]); BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(24 + field * 4), new float[] { 0.1f, 0.2f, 0.3f }[field]); }
        U16(52, (ushort)(width * height / (edge * edge))); U32(56, (uint)encodedBytes.Length);
        U32(60, (uint)(width * height * (selector == 15 ? 4 : selector is 1 or 23 ? 1 : 0.5))); U32(64, 80);
        encodedBytes.CopyTo(packet, 80); return packet;
        void Write(string code) { foreach (var bit in code) bits.Add(bit - '0'); }
        void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(at), value);
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(at), value);
    }
    private static readonly string[] LumaDc = ["00", "010", "011", "100", "101", "110", "1110", "11110", "111110", "1111110", "11111110", "111111110"];
    private static readonly string[] ChromaDc = ["00", "01", "10", "110", "1110", "11110", "111110", "1111110", "11111110", "111111110", "1111111110", "11111111110"];

    internal static NativeTextureSource Source(bool soleRaw = false, ushort firstLodDepth = 1, ushort selector = 1,
        int width = 32, int height = 32, bool emptyPayload = false)
    {
        const string index = "generated-converted-index";
        var catalog = new ResourceIdentity(index, 0); var container = new ResourceIdentity(index, 1);
        var revision = new SourceRevision(index, "generated-source");
        var payloads = new List<NativeTexturePayload> { Payload(new(index, 2), 2229, 10,
            soleRaw ? Wrap(emptyPayload ? [1, 2, 3] : Packet(width, height, selector).Concat(new byte[] { 7, 8, 9, 10 }).ToArray(), true) : [1, 2]) };
        if (!soleRaw)
        { payloads.Add(Payload(new(index, 3), 2230, 11, Wrap(Packet(width, height, selector), false))); payloads.Add(Payload(new(index, 4), 2230, 12, Wrap(Packet(width / 2, height / 2, selector), false))); }
        var descriptors = soleRaw ? new TextureFileTextV24LodDescriptor[] { new((ushort)width, (ushort)height, firstLodDepth, 6) } :
            new TextureFileTextV24LodDescriptor[] { new((ushort)width, (ushort)height, firstLodDepth, 6), new((ushort)(width / 2), (ushort)(height / 2), 1, 5), new((ushort)(width / 4), (ushort)(height / 4), 1, 4) };
        var format = selector switch
        { 15 => (byte)11, 21 => (byte)18, 23 => (byte)19, 7 or 17 or 19 => (byte)5, _ => (byte)7 };
        var prefix = new byte[84]; prefix[0] = format; BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(1), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(3), (ushort)height); BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(5), 1);
        prefix[7] = 6; prefix[12] = 1; prefix[14] = selector == 15 ? (byte)0 : (byte)1;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write("FILETEXT"u8); writer.Write(24u); writer.Write(prefix); writer.Write((byte)1); writer.Write((byte)0); writer.Write((byte)descriptors.Length);
            foreach (var descriptor in descriptors) { writer.Write(descriptor.Width); writer.Write(descriptor.Height); writer.Write(descriptor.ThirdU16); writer.Write(descriptor.TrailingByte); }
            writer.Write(true); writer.Write((byte)60); writer.Write((byte)selector);
            for (var mip = 0; mip < 6; mip++) writer.Write(0u);
            writer.Write(2229u); writer.Write(10u); writer.Write(NativeMipBytes(prefix[0], width, height));
            if (!soleRaw)
            {
                writer.Write(2230u); writer.Write(11u); writer.Write(NativeMipBytes(prefix[0], width, height));
                writer.Write(2230u); writer.Write(12u); writer.Write(NativeMipBytes(prefix[0], width / 2, height / 2));
            }
        }
        var bytes = stream.ToArray();
        var references = payloads.Select((payload, sequence) => new TextureCatalogReference(sequence,
            sequence == 0 ? TextureCatalogEntryKind.Raw : TextureCatalogEntryKind.RawLod,
            new(IdentifierNamespace.Archive, payload.Id.ToString(), catalog, payload.Type), ReferenceStatus.Unique,
            [payload.Identity], 0, new("generated.dat", sequence * 9, 9), false, null)).ToArray();
        var membership = new TextureCatalogMembership(catalog, revision, 1, new("generated.dat", 0, 100), references.Length, 0, references);
        var metadata = new TextureFileTextV24Reader().Parse(bytes, 0, bytes.Length, container, revision,
            new(container, 0, bytes.Length), [new("generated.dat", 0, 3)], membership).Value!;
        return new(payloads[0].Identity, revision, catalog, "generated-catalog", Payload(container, 29, 2, [1, 2, 3]), metadata, bytes, Hash(bytes), payloads);
    }

    internal static NativeTextureDecoder SceneTextureDecoder(NativeTextureSource source, int maximumPreviewDimension)
    {
        var method = typeof(NativeModelSession).GetMethod("CreateSourceLoadedTextureDecoder",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        try
        {
            return Assert.IsType<NativeTextureDecoder>(method!.Invoke(null,
                [source, maximumPreviewDimension, CancellationToken.None]));
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static uint NativeMipBytes(byte format, int width, int height) => checked((uint)(format is 5 or 7 or 18 or 19 or 25
        ? ((width + 3L) / 4) * ((height + 3L) / 4) * (format is 7 or 19 or 25 ? 16 : 8)
        : (long)width * height * (format == 11 ? 1 : 4)));
    private static NativeTexturePayload Payload(ResourceIdentity identity, uint type, uint id, byte[] bytes) =>
        new(identity, type, id, new("generated.dat", 0, bytes.Length, Hash(bytes)), bytes, Hash(bytes));
    private static byte[] Wrap(byte[] body, bool raw)
    {
        var bytes = new byte[32 + body.Length]; (raw ? "QZIP\0ETF_RAW_"u8 : "QZIP\0ETF_RAWL"u8).CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21), (uint)body.Length); body.CopyTo(bytes, 32); return bytes;
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
