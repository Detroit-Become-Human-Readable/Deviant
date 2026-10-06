using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class NativeTextureDecoderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodesOpaqueBc1IndividualMipsAndPreservesIdentity(bool srgb)
    {
        var source = NativeMipFixture.Create(srgb: srgb);
        var result = new NativeTextureDecoder().ReadDetailed(source);
        Assert.Equal(TextureStorageFormat.Bc1, result.Document.Info.Format);
        Assert.Equal(srgb, result.Document.Info.IsSrgb);
        Assert.Equal(2, result.Document.Info.MipCount);
        Assert.Equal(new[] { 2, 3 }, result.OmittedMipIndices);
        var surface = new DdsTextureDecoder().DecodeMip(result.Document);
        Assert.Equal(source.Metadata.Identity, surface.Identity);
        Assert.Equal(source.Revision, surface.Revision);
        AssertPixels(surface, [0, 0, 0, 255]);
        AssertPixels(new DdsTextureDecoder().DecodeMip(result.Document, 1), [255, 0, 0, 255]);
        Assert.All(result.SupportedMips, mip => Assert.Equal(0, mip.MipExtent!.Offset));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodesEveryPackedMipWithoutTreatingAllocationPaddingAsPixels(bool srgb)
    {
        var source = NativeMipFixture.Create(packed: true, srgb: srgb);
        var result = new NativeTextureDecoder().ReadDetailed(source);
        Assert.Equal(4, result.Document.Info.MipCount);
        Assert.Empty(result.OmittedMipIndices);
        Assert.Equal(new long[] { 0, 16, 24, 32 }, result.SupportedMips.Select(mip => mip.MipExtent!.Offset));
        Assert.Equal(new long[] { 16, 8, 8, 8 }, result.SupportedMips.Select(mip => mip.MipExtent!.Length));
        Assert.Equal(40, result.Document.Info.EncodedLength);
        Assert.Equal(0, result.Document.Info.TrailingLength);
        byte[][] colors = [[0, 0, 0, 255], [255, 0, 0, 255], [0, 255, 0, 255], [0, 0, 255, 255]];
        for (var mip = 0; mip < colors.Length; mip++)
        {
            AssertPixels(new DdsTextureDecoder().DecodeMip(result.Document, mip), colors[mip]);
            Assert.Equal(source.SelectedIdentity, result.SupportedMips[mip].Identity);
            Assert.Equal(source.Payloads[0].Sha256, result.SupportedMips[mip].OriginalSha256);
        }
        Assert.Equal(4, result.TotalMipCount);
    }

    [Fact]
    public void PackedCubemapPreservesFacesAndMipOrder()
    {
        var source = NativeMipFixture.CreateCube();
        var result = new NativeTextureDecoder().ReadDetailed(source);
        Assert.True(result.Document.Info.IsCubeMap);
        Assert.Equal(3, result.Document.Info.MipCount);
        Assert.Equal(18, result.Document.Subresources.Count);
        Assert.Equal(18, result.SupportedMips.Count);
        Assert.Equal(6, result.SupportedMips.Select(mip => mip.Face).Distinct().Count());
        var expected = new byte[][] { [255, 0, 0, 255], [0, 255, 0, 255], [0, 0, 255, 255], [255, 255, 255, 255], [255, 255, 0, 255], [255, 0, 255, 255] };
        var faces = result.Document.Subresources.Select(item => item.Selection.Face).Distinct().ToArray();
        for (var face = 0; face < faces.Length; face++)
            for (var mip = 0; mip < 3; mip++)
                AssertPixels(new DdsTextureDecoder().DecodeSubresource(result.Document, new(0, faces[face], mip)), expected[face]);
    }

    [Fact]
    public void HdrCubemapKeepsFloatingPointPixelsAndCanBeToneMappedForPreview()
    {
        var source = NativeMipFixture.CreateCube(format: 25);
        var result = new NativeTextureDecoder().ReadDetailed(source);
        Assert.Equal(TextureStorageFormat.Bc6Unsigned, result.Document.Info.Format);
        Assert.True(result.Document.Info.IsHdr); Assert.True(result.Document.Info.IsCubeMap);
        Assert.False(NativeTextureDecoder.IsSrgb(source));
        var hdr = new DdsTextureDecoder().DecodeMip(result.Document);
        Assert.True(hdr.IsHdr); Assert.Equal(16, hdr.HdrPixels.Length);
        var display = HdrDisplayTransform.ToSdr(hdr);
        Assert.False(display.IsHdr); Assert.True(display.SourceIsHdr); Assert.Equal(64, display.Rgba8.Length);
    }

    [Fact]
    public void RawBc3PreservesColorAndAlpha()
    {
        var document = new NativeTextureDecoder().Read(NativeMipFixture.Create(format: 7, packed: true));
        Assert.Equal(TextureStorageFormat.Bc3, document.Info.Format);
        AssertPixels(new DdsTextureDecoder().DecodeMip(document, 1), [255, 0, 0, 200]);
    }

    [Fact]
    public void RawBc4KeepsItsScalarChannel()
    {
        var document = new NativeTextureDecoder().Read(NativeMipFixture.Create(format: 18, packed: true));
        Assert.Equal(TextureStorageFormat.Bc4, document.Info.Format);
        var surface = new DdsTextureDecoder().DecodeMip(document);
        Assert.Equal(200, surface.Rgba8.Span[0]); Assert.Equal(255, surface.Rgba8.Span[3]);
    }

    [Fact]
    public void KeepsBgraChannelAndAlphaBehavior()
    {
        var result = new NativeTextureDecoder().ReadDetailed(NativeMipFixture.Create(format: 0));
        Assert.Equal(TextureStorageFormat.Bgra8, result.Document.Info.Format);
        AssertPixels(new DdsTextureDecoder().DecodeMip(result.Document), [11, 22, 33, 44]);
    }

    [Theory]
    [InlineData("following", "NativeMipSizeMismatch")]
    [InlineData("table", "NativeMipSizeMismatch")]
    [InlineData("decoded", "UnsupportedNativeMipPayload")]
    [InlineData("suffix", "UnsupportedNativeMipPayload")]
    public void RejectsPackedSizeAndPayloadDisagreements(string defect, string code)
    {
        var source = NativeMipFixture.Create(packed: true, defect: defect);
        Assert.Equal(code, Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder().Read(source)).Code);
    }

    [Fact]
    public void RejectsChangedStoredBytesMetadataAndAmbiguousPackedSources()
    {
        var source = NativeMipFixture.Create(packed: true);
        var payload = source.Payloads[0];
        var changed = payload.OriginalBytes.ToArray(); changed[^1] ^= 1;
        Assert.Equal("NativeTextureSourceMismatch", Assert.Throws<TextureFormatException>(() =>
            new NativeTextureDecoder().Read(source with { Payloads = [payload with { OriginalBytes = changed }] })).Code);
        Assert.Equal("NativeTextureMetadataMismatch", Assert.Throws<TextureFormatException>(() =>
            new NativeTextureDecoder().Read(source with { Metadata = source.Metadata with { Mip0Width = 16 } })).Code);
        Assert.Equal("NativeTextureMetadataMismatch", Assert.Throws<TextureFormatException>(() =>
            new NativeTextureDecoder().Read(source with { MetadataSha256 = new string('0', 64) })).Code);
        Assert.Equal("AmbiguousNativeTextureReference", Assert.Throws<TextureFormatException>(() =>
            new NativeTextureDecoder().Read(source with { Payloads = [payload, payload] })).Code);
    }

    [Fact]
    public void KeepsConversionFormatBudgetAndCancellationGuards()
    {
        Assert.Equal("UnsupportedNativeTextureConversion", Assert.Throws<TextureFormatException>(() =>
            new NativeTextureDecoder().Read(NativeMipFixture.Create(packed: true, conversion: true))).Code);
        Assert.Equal("UnsupportedNativeTextureFormat", Assert.Throws<TextureFormatException>(() =>
            new NativeTextureDecoder().Read(NativeMipFixture.Create(format: 6))).Code);
        Assert.Equal("NativeBaseMipUnavailable", Assert.Throws<TextureFormatException>(() =>
            new NativeTextureDecoder().Read(NativeMipFixture.Create(format: 0, packed: true))).Code);
        Assert.Equal("NativeTextureDecodeBudget", Assert.Throws<TextureFormatException>(() =>
            new NativeTextureDecoder(255).Read(NativeMipFixture.Create(packed: true))).Code);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new NativeTextureDecoder().Read(
            NativeMipFixture.Create(packed: true), cancelled.Token));
    }

    private static void AssertPixels(TextureSurface surface, byte[] expected)
    {
        Assert.Equal(surface.Width * surface.Height * 4, surface.Rgba8.Length);
        for (var offset = 0; offset < surface.Rgba8.Length; offset += 4)
            Assert.Equal(expected, surface.Rgba8.Slice(offset, 4).ToArray());
    }
}

internal static class NativeMipFixture
{
    internal static NativeTextureSource CreateCube(byte format = 5)
    {
        const string index = "generated-cube-index";
        var catalog = new ResourceIdentity(index, 0); var container = new ResourceIdentity(index, 1);
        var revision = new SourceRevision(index, "generated-source");
        var length = format == 25 ? 16u : 8u;
        using var packed = new MemoryStream();
        var colors = new ushort[] { 0xF800, 0x07E0, 0x001F, 0xFFFF, 0xFFE0, 0xF81F };
        for (var mip = 0; mip < 3; mip++)
            for (var face = 0; face < 6; face++)
            {
                if (format == 25) { var block = new byte[16]; block[0] = 3; packed.Write(block); }
                else packed.Write(TextureFixtureBuilder.SolidDxt1Block(colors[face], 0, 0));
            }
        var padded = new byte[(packed.Length + 255) & ~255L]; packed.ToArray().CopyTo(padded, 0);
        var payload = Payload(new(index, 2), 2229, 10, Wrap(padded, true));
        var prefix = new byte[84]; prefix[0] = format;
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(1), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(3), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(5), 1);
        prefix[7] = 3; prefix[12] = 2;
        using var encodedStream = new MemoryStream();
        using (var writer = new BinaryWriter(encodedStream, System.Text.Encoding.UTF8, true))
        {
            writer.Write("FILETEXT"u8); writer.Write(24u); writer.Write(prefix);
            writer.Write((byte)1); writer.Write((byte)0); writer.Write((byte)1);
            writer.Write((ushort)4); writer.Write((ushort)4); writer.Write((ushort)1); writer.Write((byte)3);
            writer.Write(false); writer.Write((byte)0); writer.Write((byte)0);
            for (var face = 0; face < 6; face++) for (var mip = 0; mip < 3; mip++) writer.Write(length);
            writer.Write(2229u); writer.Write(payload.Id); writer.Write((uint)padded.Length);
        }
        var encoded = encodedStream.ToArray();
        var row = new TextureCatalogReference(0, TextureCatalogEntryKind.Raw,
            new(IdentifierNamespace.Archive, payload.Id.ToString(), catalog, payload.Type), ReferenceStatus.Unique,
            [payload.Identity], 0, new("generated.dat", 0, 9), false, null);
        var membership = new TextureCatalogMembership(catalog, revision, 1, new("generated.dat", 0, 9), 1, 0, [row]);
        var metadata = new TextureFileTextV24Reader().Parse(encoded, 0, encoded.Length, container, revision,
            new(container, 0, encoded.Length), [new("generated.dat", 100, 3)], membership).Value!;
        return new(payload.Identity, revision, catalog, "generated-catalog", Payload(container, 29, 2, [1, 2, 3]),
            metadata, encoded, Hash(encoded), [payload]);
    }

    internal static NativeTextureSource Create(byte format = 5, bool packed = false, bool srgb = false,
        bool conversion = false, string? defect = null)
    {
        const string index = "generated-texture-index";
        var catalog = new ResourceIdentity(index, 0); var container = new ResourceIdentity(index, 1);
        var revision = new SourceRevision(index, "generated-source");
        var lengths = Enumerable.Range(0, 4).Select(mip => format == 0
            ? Math.Max(1, 8 >> mip) * Math.Max(1, 4 >> mip) * 4
            : TextureFixtureBuilder.MipLength(Math.Max(1, 8 >> mip), Math.Max(1, 4 >> mip), format == 7 ? 16 : 8)).ToArray();
        var mipBytes = Enumerable.Range(0, 4).Select(mip => Pixels(format, mip, lengths[mip])).ToArray();
        var padded = new byte[(mipBytes.Sum(mip => mip.Length) + 255) & ~255];
        Array.Fill(padded, (byte)0xA7); var cursor = 0;
        foreach (var mip in mipBytes) { mip.CopyTo(padded, cursor); cursor += mip.Length; }
        if (defect == "decoded") Array.Resize(ref padded, padded.Length + 1);
        var raw = Payload(new(index, 2), 2229, 10, packed ? Wrap(padded, true, defect == "suffix") : [1, 2]);
        var payloads = new List<NativeTexturePayload> { raw };
        if (!packed)
            for (var mip = 0; mip < 2; mip++)
                payloads.Add(Payload(new(index, mip + 3), 2230, (uint)mip + 11, Wrap(mipBytes[mip], false)));
        var descriptors = packed ? new[] { new TextureFileTextV24LodDescriptor(8, 4, 1, 4) } :
            new TextureFileTextV24LodDescriptor[] { new(8, 4, 1, 4), new(4, 2, 1, 3), new(2, 1, 1, 2) };
        var prefix = new byte[84]; prefix[0] = format;
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(1), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(3), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(5), 1);
        prefix[7] = 4; prefix[12] = 1; prefix[14] = srgb ? (byte)1 : (byte)0;
        using var encodedStream = new MemoryStream();
        using (var writer = new BinaryWriter(encodedStream, System.Text.Encoding.UTF8, true))
        {
            writer.Write("FILETEXT"u8); writer.Write(24u); writer.Write(prefix);
            writer.Write((byte)1); writer.Write((byte)0); writer.Write((byte)descriptors.Length);
            foreach (var descriptor in descriptors)
            { writer.Write(descriptor.Width); writer.Write(descriptor.Height); writer.Write(descriptor.ThirdU16); writer.Write(descriptor.TrailingByte); }
            writer.Write(conversion); writer.Write((byte)0); writer.Write((byte)0);
            for (var mip = 0; mip < lengths.Length; mip++) writer.Write((uint)(lengths[mip] + (defect == "table" && mip == 1 ? 1 : 0)));
            writer.Write(2229u); writer.Write(raw.Id);
            writer.Write(packed ? (uint)((mipBytes.Sum(mip => mip.Length) + 255) & ~255) + (defect == "following" ? 128u : 0u) : 77u);
            if (!packed)
                for (var mip = 0; mip < 2; mip++) { writer.Write(2230u); writer.Write((uint)mip + 11); writer.Write((uint)lengths[mip]); }
        }
        var encoded = encodedStream.ToArray();
        var rows = payloads.Select((payload, ordinal) => new TextureCatalogReference(ordinal,
            ordinal == 0 ? TextureCatalogEntryKind.Raw : TextureCatalogEntryKind.RawLod,
            new(IdentifierNamespace.Archive, payload.Id.ToString(), catalog, payload.Type), ReferenceStatus.Unique,
            [payload.Identity], 0, new("generated.dat", ordinal * 9, 9), false, null)).ToArray();
        var membership = new TextureCatalogMembership(catalog, revision, 1, new("generated.dat", 0, 100), rows.Length, 0, rows);
        var metadata = new TextureFileTextV24Reader().Parse(encoded, 0, encoded.Length, container, revision,
            new(container, 0, encoded.Length), [new("generated.dat", 100, 3)], membership).Value
            ?? throw new InvalidOperationException("Generated metadata is invalid.");
        return new(raw.Identity, revision, catalog, "generated-catalog", Payload(container, 29, 2, [1, 2, 3]),
            metadata, encoded, Hash(encoded), payloads);
    }

    private static byte[] Pixels(byte format, int mip, int length)
    {
        var bytes = new byte[length];
        if (format == 0)
            for (var offset = 0; offset < bytes.Length; offset += 4) new byte[] { 33, 22, 11, 44 }.CopyTo(bytes, offset);
        else if (format == 7)
        {
            var color = new ushort[] { 0, 0xF800, 0x07E0, 0x001F }[mip];
            var block = new byte[16]; block[0] = 200; block[1] = 100;
            TextureFixtureBuilder.SolidDxt1Block(color, 0xFFFF, 0).CopyTo(block, 8);
            for (var offset = 0; offset < bytes.Length; offset += 16) block.CopyTo(bytes, offset);
        }
        else if (format == 18)
        {
            var block = new byte[] { 200, 100, 0, 0, 0, 0, 0, 0 };
            for (var offset = 0; offset < bytes.Length; offset += 8) block.CopyTo(bytes, offset);
        }
        else
        {
            var color = new ushort[] { 0, 0xF800, 0x07E0, 0x001F }[mip];
            var block = TextureFixtureBuilder.SolidDxt1Block(color, 0xFFFF, mip == 0 ? uint.MaxValue : 0);
            for (var offset = 0; offset < bytes.Length; offset += 8) block.CopyTo(bytes, offset);
        }
        return bytes;
    }

    private static NativeTexturePayload Payload(ResourceIdentity identity, uint type, uint id, byte[] bytes)
    { var hash = Hash(bytes); return new(identity, type, id, new("generated.dat", 0, bytes.Length, hash), bytes, hash); }

    private static byte[] Wrap(byte[] decoded, bool raw, bool suffix = false)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true)) zlib.Write(decoded);
        var block = compressed.ToArray(); var segs = new byte[32 + block.Length + (suffix ? 1 : 0)];
        "segs"u8.CopyTo(segs); BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(6), 1); BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(8), (uint)decoded.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(12), (uint)block.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(16), (ushort)block.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(18), (ushort)decoded.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(20), 1); block.CopyTo(segs, 32);
        var wrapped = new byte[32 + segs.Length];
        (raw ? "QZIP\0ETF_RAW_"u8 : "QZIP\0ETF_RAWL"u8).CopyTo(wrapped);
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(21), (uint)segs.Length); segs.CopyTo(wrapped, 32);
        return wrapped;
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
