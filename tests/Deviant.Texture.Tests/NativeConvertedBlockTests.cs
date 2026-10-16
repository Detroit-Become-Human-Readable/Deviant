using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Deviant.Browser;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class NativeConvertedBlockTests
{
    private const int Width = 32;
    private const int Height = 32;

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, false, true)]
    [InlineData(0, true, false)]
    [InlineData(0, true, true)]
    [InlineData(80, false, false)]
    [InlineData(80, false, true)]
    [InlineData(80, true, false)]
    [InlineData(80, true, true)]
    public void ReadsRawBlocksWithEitherStoredFirstOffset(int firstOffset, bool srgb, bool preview)
    {
        var blocks = Blocks();
        var source = Source(RawPacket(blocks, firstOffset), srgb);

        AssertBlocks(source, blocks, Decode(source, preview), srgb);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReadsCompressedNativeBlocksUnderConversionTwelve(bool srgb, bool preview)
    {
        var blocks = Blocks();
        var source = Source(CompressedPacket(blocks), srgb);

        AssertBlocks(source, blocks, Decode(source, preview), srgb);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeBlockPreviewAdmissionUsesBlockBytesInsteadOfRgbaExpansion(bool compressed)
    {
        var blocks = Blocks();
        var source = Source(compressed ? CompressedPacket(blocks) : RawPacket(blocks, 0), true);
        var decoder = new NativeTextureDecoder(blocks.Length);

        Assert.Equal(3L * blocks.Length + 1024 * 1024,
            decoder.EstimatePreviewWorkingSetBytes(source, Width));
        Assert.Equal(3L * blocks.Length + 1024 * 1024, decoder.EstimateWorkingSetBytes(source, Width));
        AssertBlocks(source, blocks, decoder.ReadPreview(source, Width).Document, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsMissingBlockSource(bool preview)
    {
        var source = Source(RawPacket(Blocks(), 80), true) with { Payloads = [] };

        var error = Assert.Throws<TextureFormatException>(() => Decode(source, preview));
        Assert.Equal("AmbiguousNativeTextureReference", error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsChangedBlockSource(bool preview)
    {
        var source = Source(CompressedPacket(Blocks()), true);
        var payload = Assert.Single(source.Payloads);
        var changed = payload.OriginalBytes.ToArray();
        changed[^1] ^= 1;
        source = source with { Payloads = [payload with { OriginalBytes = changed }] };

        var error = Assert.Throws<TextureFormatException>(() => Decode(source, preview));
        Assert.Equal("NativeTextureSourceMismatch", error.Code);
    }

    [Theory]
    [InlineData(5, 12, false)]
    [InlineData(5, 12, true)]
    [InlineData(7, 13, false)]
    [InlineData(7, 13, true)]
    public void RejectsUnsupportedFormatAndConversionPairs(byte format, byte conversion, bool preview)
    {
        var source = Metadata(Source(RawPacket(Blocks(), 80), true), true, format, conversion);

        var error = Assert.Throws<TextureFormatException>(() => Decode(source, preview));
        Assert.Equal("UnsupportedNativeTextureConversion", error.Code);
    }

    private static DdsTextureDocument Decode(NativeTextureSource source, bool preview)
    {
        var decoder = new NativeTextureDecoder();
        return preview ? decoder.ReadPreview(source, Width).Document : decoder.Read(source);
    }

    private static void AssertBlocks(NativeTextureSource source, byte[] blocks,
        DdsTextureDocument document, bool srgb)
    {
        Assert.Equal((byte)7, source.Metadata.FirstByte);
        Assert.True(source.Metadata.PostSelectorBoolean);
        Assert.Equal((byte)12, source.Metadata.PostSelectorByte1);
        Assert.Equal(TextureStorageFormat.Bc3, document.Info.Format);
        Assert.Equal(Width, document.Info.Width);
        Assert.Equal(Height, document.Info.Height);
        Assert.Equal(1, document.Info.MipCount);
        Assert.Equal(srgb, document.Info.IsSrgb);
        Assert.Equal(source.Metadata.Identity, document.Identity);
        Assert.Equal(source.Revision, document.Revision);
        Assert.Equal(blocks, document.OriginalBytes.Slice(document.Info.HeaderLength).ToArray());
        Assert.Equal(srgb ? 78u : 77u,
            BinaryPrimitives.ReadUInt32LittleEndian(document.OriginalBytes.Span[128..]));
        // BC3 alpha must not be labeled opaque in the DX10 header.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(document.OriginalBytes.Span[144..]) & 7u);

        var surface = new DdsTextureDecoder().DecodeMip(document);
        Assert.Equal(srgb, surface.IsSrgb);
        Assert.Equal((byte)17, surface.Rgba8.Span[3]);
    }

    private static byte[] Blocks()
    {
        var bytes = new byte[Width / 4 * (Height / 4) * 16];
        for (var block = 0; block < bytes.Length / 16; block++)
        {
            var at = block * 16;
            bytes[at] = (byte)(17 + block * 3);
            bytes[at + 1] = 231;
            // Every alpha index is zero, selecting the block's first endpoint.
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 8), 0xf800);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 10), 0x07e0);
            bytes.AsSpan(at + 12, 4).Fill((byte)block);
        }
        return bytes;
    }

    private static byte[] RawPacket(byte[] blocks, int firstOffset)
    {
        var packet = Header(12, blocks.Length, blocks.Length, firstOffset);
        blocks.CopyTo(packet, 80);
        return packet;
    }

    private static byte[] CompressedPacket(byte[] blocks)
    {
        using var stream = new MemoryStream();
        using (var zlib = new ZLibStream(stream, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(blocks);
        var compressed = stream.ToArray();
        Assert.True(compressed.Length < blocks.Length);
        var packet = Header(255, compressed.Length, blocks.Length, 80);
        compressed.CopyTo(packet, 80);
        return packet;
    }

    private static byte[] Header(ushort selector, int encodedLength, int decodedLength, int firstOffset)
    {
        var bytes = new byte[80 + encodedLength];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), selector);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 100);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), Width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), Height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(40), 1);
        // One zero-tile native-block descriptor; its payload begins at byte80.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), (uint)encodedLength);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), (uint)decodedLength);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), (uint)firstOffset);
        return bytes;
    }

    private static NativeTextureSource Source(byte[] packet, bool srgb)
    {
        var source = ConvertedTextureFixture.Source(soleRaw: true, selector: 12, width: Width, height: Height);
        var original = Assert.Single(source.Payloads);
        var wrapped = new byte[32 + packet.Length];
        "QZIP\0ETF_RAW_"u8.CopyTo(wrapped);
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(21), (uint)packet.Length);
        packet.CopyTo(wrapped, 32);
        var hash = Hash(wrapped);
        var payload = original with
        {
            OriginalBytes = wrapped,
            Sha256 = hash,
            Extent = original.Extent with { Length = wrapped.Length, PayloadSha256 = hash }
        };
        return Metadata(source with { Payloads = [payload] }, srgb, 7, 12);
    }

    private static NativeTextureSource Metadata(NativeTextureSource source, bool srgb, byte format, byte conversion)
    {
        var bytes = source.MetadataBytes.ToArray();
        var prefix = source.Metadata.RawFixedPrefix84.ToArray();
        prefix[0] = format;
        prefix[14] = srgb ? (byte)1 : (byte)0;
        prefix.CopyTo(bytes, 12);
        // Fixed prefix, selector/leading/count, seven-byte descriptors, then bool/quality/conversion.
        var conversionOffset = 12 + 84 + 3 + source.Metadata.DescriptorCount * 7 + 2;
        bytes[conversionOffset] = conversion;
        return source with
        {
            MetadataBytes = bytes,
            MetadataSha256 = Hash(bytes),
            Metadata = source.Metadata with
            {
                RawFixedPrefix84 = prefix,
                FirstByte = format,
                PostSelectorByte1 = conversion
            }
        };
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
