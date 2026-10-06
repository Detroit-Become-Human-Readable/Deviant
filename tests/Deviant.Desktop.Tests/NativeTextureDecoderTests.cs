using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class NativeTextureDecoderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodesExactNonSquareRawLMipsWithAllColorChannelsAndAlpha(bool srgb)
    {
        var source = NativeTextureFixture.Create(srgb: srgb);
        var result = new NativeTextureDecoder().ReadDetailed(source);
        Assert.Equal(4, result.Document.Info.Width); Assert.Equal(2, result.Document.Info.Height);
        Assert.Equal(2, result.Document.Info.MipCount); Assert.Equal(3, result.TotalMipCount);
        Assert.Equal(new[] { 0, 1 }, result.SupportedMips.Select(mip => mip.MipIndex)); Assert.Equal(new[] { 2 }, result.OmittedMipIndices);
        Assert.Equal(srgb, result.Document.Info.IsSrgb); Assert.Equal(TextureStorageFormat.Bgra8, result.Document.Info.Format);
        var decoder = new DdsTextureDecoder();
        for (var mip = 0; mip < 2; mip++)
        {
            var pixels = decoder.DecodeMip(result.Document, mip);
            Assert.Equal(NativeTextureFixture.RgbaPixels(mip), pixels.Rgba8.ToArray());
            Assert.Equal(source.Metadata.Identity, pixels.Identity); Assert.Equal(source.Revision, pixels.Revision);
            Assert.Equal(srgb, pixels.IsSrgb);
        }
        Assert.Equal(new byte[] { 99, 88 }, source.Payloads[0].OriginalBytes.ToArray());
        Assert.NotEqual(source.Payloads[1].Sha256, result.Document.ContentSha256);
    }

    [Fact]
    public void RejectsProprietaryConversionEvenWithExactRawLMipBytes()
    {
        var error = Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder().Read(NativeTextureFixture.Create(conversion: true)));
        Assert.Equal("UnsupportedNativeTextureConversion", error.Code);
    }

    [Theory]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("mips")]
    [InlineData("selector")]
    [InlineData("descriptor")]
    public void RejectsPublicMetadataPropertiesThatDifferFromSerializedContract(string field)
    {
        var source = NativeTextureFixture.Create(); var metadata = source.Metadata;
        metadata = field switch
        {
            "width" => metadata with { Mip0Width = 8 }, "height" => metadata with { Mip0Height = 4 },
            "mips" => metadata with { MipSizeCount = 2 }, "selector" => metadata with { SizeGroupSelector = 2 },
            _ => metadata with { LodDescriptors = metadata.LodDescriptors.Select((value, index) => index == 0 ? value with { Width = 8 } : value).ToArray() }
        };
        var error = Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder().Read(source with { Metadata = metadata }));
        Assert.Equal("NativeTextureMetadataMismatch", error.Code);
    }

    [Fact]
    public void RejectsTruncatedWrongLengthAndTrailingRawLPayloads()
    {
        var source = NativeTextureFixture.Create(); var original = source.Payloads[1];
        foreach (var bytes in new[] { original.OriginalBytes.ToArray()[..^1], NativeTextureFixture.WrapRawL(new byte[31]), NativeTextureFixture.WrapRawL(new byte[32], suffix: true) })
        {
            var replacement = NativeTextureFixture.Payload(original.Identity, original.Type, original.Id, bytes);
            var changed = source with { Payloads = source.Payloads.Select(payload => payload.Identity == replacement.Identity ? replacement : payload).ToArray() };
            Assert.Equal("UnsupportedNativeMipPayload", Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder().Read(changed)).Code);
        }
        Assert.Equal("NativeMipSizeMismatch", Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder().Read(NativeTextureFixture.Create(wrongFollowing: true))).Code);
    }

    [Fact]
    public void RejectsAmbiguousRefsDuplicateMipIndicesOtherFormatsAndMissingBase()
    {
        Assert.Equal("AmbiguousNativeTextureReference", Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder().Read(NativeTextureFixture.Create(ambiguous: true))).Code);
        Assert.Equal("AmbiguousNativeMip", Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder().Read(NativeTextureFixture.Create(indices: [0, 0]))).Code);
        Assert.Equal("UnsupportedNativeTextureFormat", Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder().Read(NativeTextureFixture.Create(format: 6))).Code);
        Assert.Equal("NativeBaseMipUnavailable", Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder().Read(NativeTextureFixture.Create(indices: [1]))).Code);
        Assert.Equal("NativeBaseMipUnavailable", Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder().Read(NativeTextureFixture.Create(indices: []))).Code);
    }

    [Fact]
    public void KeepsOnlyContiguousLevelsAndHonorsCancellationAndByteBudget()
    {
        var result = new NativeTextureDecoder().ReadDetailed(NativeTextureFixture.Create(indices: [0, 2]));
        Assert.Equal(1, result.Document.Info.MipCount); Assert.Equal(new[] { 1, 2 }, result.OmittedMipIndices);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new NativeTextureDecoder().Read(NativeTextureFixture.Create(), cancelled.Token));
        Assert.Equal("NativeTextureDecodeBudget", Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder(31).Read(NativeTextureFixture.Create())).Code);
    }
}

internal static class NativeTextureFixture
{
    internal static void WriteArchive(string path, bool zeroAlpha = false)
    {
        var source = Create(zeroAlpha: zeroAlpha); var metadata = source.MetadataBytes.ToArray();
        for (var reference = 0; reference < 3; reference++)
            BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(metadata.Length - 36 + reference * 12 + 4), (uint)reference + 3);
        var body = new byte[metadata.Length + 12]; metadata.CopyTo(body, 0); "FTEXCRAW"u8.CopyTo(body.AsSpan(metadata.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(metadata.Length + 8), 1);
        var logical = new byte[40 + body.Length]; "DC_INFO "u8.CopyTo(logical);
        BinaryPrimitives.WriteUInt32LittleEndian(logical.AsSpan(8), 3); BinaryPrimitives.WriteUInt32LittleEndian(logical.AsSpan(12), 8);
        "DC_DATA "u8.CopyTo(logical.AsSpan(24)); BinaryPrimitives.WriteUInt32LittleEndian(logical.AsSpan(32), 23);
        BinaryPrimitives.WriteUInt32LittleEndian(logical.AsSpan(36), (uint)body.Length); body.CopyTo(logical, 40);
        using var table = new MemoryStream();
        using (var writer = new BinaryWriter(table, System.Text.Encoding.UTF8, true))
        {
            writer.Write("COM_CONT"u8); writer.Write(6u); writer.Write(40u); writer.Write(4u);
            foreach (var pair in new[] { (29u, 2u), (2229u, 3u), (2230u, 4u), (2230u, 5u) })
            { writer.Write(pair.Item1); writer.Write(pair.Item2); writer.Write((byte)0); }
        }
        DiscoveryFixture.WriteIndex(path, [(3, table.ToArray()), (29, WrapRawL(logical)[32..]),
            (2229, source.Payloads[0].OriginalBytes.ToArray()), (2230, source.Payloads[1].OriginalBytes.ToArray()), (2230, source.Payloads[2].OriginalBytes.ToArray())]);
    }
    internal static NativeTextureSource Create(bool srgb = false, bool conversion = false, bool wrongFollowing = false,
        bool ambiguous = false, byte format = 0, int[]? indices = null, bool zeroAlpha = false)
    {
        indices ??= [0, 1]; const string index = "synthetic-native-index";
        var catalog = new ResourceIdentity(index, 0); var container = new ResourceIdentity(index, 1); var revision = new SourceRevision(index, "synthetic-native-source");
        var raw = Payload(new(index, 2), 2229, 100, [99, 88]);
        var payloads = new List<NativeTexturePayload> { raw };
        foreach (var (mip, sequence) in indices.Select((mip, sequence) => (mip, sequence)))
            payloads.Add(Payload(new(index, sequence + 3), 2230, (uint)sequence + 110, WrapRawL(BgraPixels(mip, zeroAlpha))));
        var descriptors = indices.Select(mip => new TextureFileTextV24LodDescriptor((ushort)Math.Max(1, 4 >> mip), (ushort)Math.Max(1, 2 >> mip), 1, (byte)(3 - mip)))
            .Append(new(1, 1, 1, 1)).ToArray();
        var prefix = new byte[84]; prefix[0] = format; BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(1), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(3), 2); BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(5), 1);
        prefix[7] = 3; prefix[12] = 1; prefix[14] = srgb ? (byte)1 : (byte)0;
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
        {
            writer.Write("FILETEXT"u8); writer.Write(24u); writer.Write(prefix); writer.Write((byte)1); writer.Write((byte)0); writer.Write((byte)descriptors.Length);
            foreach (var descriptor in descriptors) { writer.Write(descriptor.Width); writer.Write(descriptor.Height); writer.Write(descriptor.ThirdU16); writer.Write(descriptor.TrailingByte); }
            writer.Write(conversion ? (byte)1 : (byte)0); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write(32u); writer.Write(8u); writer.Write(4u);
            writer.Write(2229u); writer.Write(100u); writer.Write(77u);
            foreach (var (mip, sequence) in indices.Select((mip, sequence) => (mip, sequence)))
            { writer.Write(2230u); writer.Write((uint)sequence + 110); writer.Write((uint)BgraPixels(mip).Length + (wrongFollowing && sequence == 0 ? 1u : 0u)); }
        }
        var encoded = bytes.ToArray();
        var rows = payloads.Select((payload, ordinal) => new TextureCatalogReference(ordinal,
            ordinal == 0 ? TextureCatalogEntryKind.Raw : TextureCatalogEntryKind.RawLod,
            new(IdentifierNamespace.Archive, payload.Id.ToString(), catalog, payload.Type),
            ambiguous && ordinal == 1 ? ReferenceStatus.Ambiguous : ReferenceStatus.Unique,
            ambiguous && ordinal == 1 ? new[] { payload.Identity, new ResourceIdentity(index, 99) } : new[] { payload.Identity },
            0, new("volume.dat", ordinal * 9, 9), false, null)).ToArray();
        var membership = new TextureCatalogMembership(catalog, revision, 1, new("volume.dat", 0, 100), rows.Length, 0, rows);
        var metadata = new TextureFileTextV24Reader().Parse(encoded, 0, encoded.Length, container, revision,
            new(container, 32, encoded.Length), [new("volume.dat", 100, 3)], membership).Value
            ?? throw new InvalidOperationException("Generated metadata is invalid.");
        var companion = Payload(container, 29, 2, [1, 2, 3]);
        return new(raw.Identity, revision, catalog, "synthetic-catalog-hash", companion, metadata, encoded, Hash(encoded), payloads);
    }
    internal static NativeTexturePayload Payload(ResourceIdentity identity, uint type, uint id, byte[] bytes)
    { var hash = Hash(bytes); return new(identity, type, id, new("volume.dat", 0, bytes.Length, hash), bytes, hash); }
    internal static byte[] BgraPixels(int mip, bool zeroAlpha = false)
    {
        var bytes = new byte[Math.Max(1, 4 >> mip) * Math.Max(1, 2 >> mip) * 4];
        for (var pixel = 0; pixel < bytes.Length / 4; pixel++)
        { bytes[pixel * 4] = (byte)(30 + pixel + mip); bytes[pixel * 4 + 1] = (byte)(20 + pixel); bytes[pixel * 4 + 2] = (byte)(10 + pixel); bytes[pixel * 4 + 3] = zeroAlpha || pixel == 0 ? (byte)0 : pixel == 1 ? (byte)128 : (byte)255; }
        return bytes;
    }
    internal static byte[] RgbaPixels(int mip)
    {
        var bytes = BgraPixels(mip);
        for (var offset = 0; offset < bytes.Length; offset += 4) (bytes[offset], bytes[offset + 2]) = (bytes[offset + 2], bytes[offset]);
        return bytes;
    }
    internal static byte[] WrapRawL(byte[] decoded, bool suffix = false)
    {
        using var compressed = new MemoryStream(); using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true)) zlib.Write(decoded);
        var block = compressed.ToArray(); var segs = new byte[32 + block.Length + (suffix ? 1 : 0)];
        "segs"u8.CopyTo(segs); BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(4), 1); BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(8), (uint)decoded.Length); BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(12), (uint)block.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(16), (ushort)block.Length); BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(18), (ushort)decoded.Length); BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(20), 1); block.CopyTo(segs, 32);
        var wrapped = new byte[32 + segs.Length]; "QZIP\0ETF_RAWL"u8.CopyTo(wrapped);
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(21), (uint)segs.Length); segs.CopyTo(wrapped, 32); return wrapped;
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
