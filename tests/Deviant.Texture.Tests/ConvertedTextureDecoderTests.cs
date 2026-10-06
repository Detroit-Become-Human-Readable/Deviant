using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class ConvertedTextureDecoderTests
{
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
    internal static byte[] Packet(int width, int height, ushort selector, byte alpha = 127, int verticalAc = 0)
    {
        var bits = new List<int>(); var predictors = new int[4]; var edge = selector == 21 ? 8 : 16;
        for (var tile = 0; tile < width * height / (edge * edge); tile++)
        for (var plane = 0; plane < (selector == 21 ? 1 : selector == 1 ? 10 : 6); plane++)
        {
            var component = plane < 4 ? 0 : plane == 4 ? 1 : plane == 5 ? 2 : 3;
            var value = component == 0 ? 1024 : component == 3 ? alpha * 8 : 0;
            var difference = value - predictors[component]; predictors[component] = value;
            var category = difference == 0 ? 0 : System.Numerics.BitOperations.Log2((uint)Math.Abs(difference)) + 1;
            var code = component is 1 or 2 ? ChromaDc[category] : LumaDc[category];
            Write(code);
            if (category > 0)
            {
                var encoded = difference < 0 ? difference - 1 + (1 << category) : difference;
                for (var bit = category - 1; bit >= 0; bit--) bits.Add((encoded >> bit) & 1);
            }
            if (component == 0 && verticalAc != 0)
            {
                if (verticalAc != 64) throw new ArgumentOutOfRangeException(nameof(verticalAc));
                // One coefficient at zigzag position 2 (vertical frequency 1), run 1 / size 7.
                Write("1111111110000101"); Write("1000000");
            }
            Write(component is 1 or 2 ? "00" : "1010");
        }
        var encodedBytes = new byte[(bits.Count + 7) / 8];
        for (var bit = 0; bit < bits.Count; bit++) encodedBytes[bit / 8] |= (byte)(bits[bit] << (7 - bit % 8));
        var packet = new byte[80 + encodedBytes.Length];
        U16(0, 16); U16(2, selector); U16(4, 100); U16(6, (ushort)width); U16(8, (ushort)height); U16(40, 1);
        for (var field = 0; field < 3; field++)
        { BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(12 + field * 4), new float[] { 0.2f, 0.3f, 0.4f }[field]); BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(24 + field * 4), new float[] { 0.1f, 0.2f, 0.3f }[field]); }
        U16(52, (ushort)(width * height / (edge * edge))); U32(56, (uint)encodedBytes.Length);
        U32(60, (uint)(width * height * (selector == 1 ? 1 : 0.5))); U32(64, 80);
        encodedBytes.CopyTo(packet, 80); return packet;
        void Write(string code) { foreach (var bit in code) bits.Add(bit - '0'); }
        void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(at), value);
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(at), value);
    }
    private static readonly string[] LumaDc = ["00", "010", "011", "100", "101", "110", "1110", "11110", "111110", "1111110", "11111110", "111111110"];
    private static readonly string[] ChromaDc = ["00", "01", "10", "110", "1110", "11110", "111110", "1111110", "11111110", "111111110", "1111111110", "11111111110"];

    internal static NativeTextureSource Source(bool soleRaw = false, ushort firstLodDepth = 1)
    {
        const string index = "generated-converted-index";
        var catalog = new ResourceIdentity(index, 0); var container = new ResourceIdentity(index, 1);
        var revision = new SourceRevision(index, "generated-source");
        var payloads = new List<NativeTexturePayload> { Payload(new(index, 2), 2229, 10,
            soleRaw ? Wrap(Packet(32, 32, 1).Concat(new byte[] { 7, 8, 9, 10 }).ToArray(), true) : [1, 2]) };
        if (!soleRaw)
        { payloads.Add(Payload(new(index, 3), 2230, 11, Wrap(Packet(32, 32, 1), false))); payloads.Add(Payload(new(index, 4), 2230, 12, Wrap(Packet(16, 16, 1), false))); }
        var descriptors = soleRaw ? new TextureFileTextV24LodDescriptor[] { new(32, 32, firstLodDepth, 6) } :
            new TextureFileTextV24LodDescriptor[] { new(32, 32, firstLodDepth, 6), new(16, 16, 1, 5), new(8, 8, 1, 4) };
        var prefix = new byte[84]; prefix[0] = 7; BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(1), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(3), 32); BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(5), 1);
        prefix[7] = 6; prefix[12] = 1; prefix[14] = 1;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write("FILETEXT"u8); writer.Write(24u); writer.Write(prefix); writer.Write((byte)1); writer.Write((byte)0); writer.Write((byte)descriptors.Length);
            foreach (var descriptor in descriptors) { writer.Write(descriptor.Width); writer.Write(descriptor.Height); writer.Write(descriptor.ThirdU16); writer.Write(descriptor.TrailingByte); }
            writer.Write(true); writer.Write((byte)60); writer.Write((byte)1);
            for (var mip = 0; mip < 6; mip++) writer.Write(0u);
            writer.Write(2229u); writer.Write(10u); writer.Write(soleRaw ? 1536u : 256u);
            if (!soleRaw) { writer.Write(2230u); writer.Write(11u); writer.Write(1024u); writer.Write(2230u); writer.Write(12u); writer.Write(256u); }
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
    private static NativeTexturePayload Payload(ResourceIdentity identity, uint type, uint id, byte[] bytes) =>
        new(identity, type, id, new("generated.dat", 0, bytes.Length, Hash(bytes)), bytes, Hash(bytes));
    private static byte[] Wrap(byte[] body, bool raw)
    {
        var bytes = new byte[32 + body.Length]; (raw ? "QZIP\0ETF_RAW_"u8 : "QZIP\0ETF_RAWL"u8).CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21), (uint)body.Length); body.CopyTo(bytes, 32); return bytes;
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
