using System.Buffers.Binary;
using System.IO.Compression;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class NativeTextureBlockPacketReaderTests
{
    [Fact]
    public void ReadsOnePacketAndReportsFollowingMipBytesWithoutTreatingThemAsPixels()
    {
        var bytes = Enumerable.Repeat((byte)73, 1024).ToArray();
        var first = Packet(bytes, bytes); var following = Packet(new byte[1024], new byte[1024]);
        var stream = first.Concat(following).ToArray();
        var decoded = NativeTextureBlockPacketReader.ReadPacket(stream, 64, 64, 2048, 2048);
        Assert.Equal(bytes.Concat(bytes), decoded.Blocks.ToArray());
        Assert.Equal(first.Length, decoded.ConsumedBodyBytes);
        Assert.Equal(following.Length, decoded.TrailingBodyBytes);
        Assert.Throws<TextureFormatException>(() => NativeTextureBlockPacketReader.Read(stream, 64, 64, 2048, 2048));
    }

    [Fact]
    public void InflatesAndCopiesChunksWithoutChangingNativeBlocks()
    {
        var first = Enumerable.Repeat((byte)47, 1024).ToArray();
        var second = Enumerable.Range(0, 1024).Select(index => (byte)(index % 251)).ToArray();
        var packet = Packet(first, second);
        Assert.Equal(first.Concat(second), NativeTextureBlockPacketReader.Read(packet, 64, 64, 2048, 2048));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void RejectsUnusedCompressedBytesEvenWhenTheFinalChecksumMatches(int unusedBytes)
    {
        var packet = Packet(new byte[1024], new byte[1024], unusedBytes);
        var error = Assert.Throws<TextureFormatException>(() =>
            NativeTextureBlockPacketReader.Read(packet, 64, 64, 2048, 2048));
        Assert.Equal("InvalidNativeBlockPacketCompression", error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    public void CopiesLiteralBc3PacketFromItsAlignedHeaderForBothStoredOffsets(int storedOffset)
    {
        var blocks = Enumerable.Range(0, 131072).Select(index => (byte)(index % 251)).ToArray();
        var packet = LiteralPacket(512, 256, blocks, storedOffset);

        Assert.Equal(blocks, NativeTextureBlockPacketReader.Read(packet, 512, 256, blocks.Length, blocks.Length));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(4, 4)]
    public void KeepsBlockRoundedBytesForSmallLiteralBc3Mips(int width, int height)
    {
        var blocks = Enumerable.Range(0, 16).Select(index => (byte)index).ToArray();
        var first = LiteralPacket(width, height, blocks, 0);
        var next = LiteralPacket(1, 1, new byte[16], 80);
        var packet = NativeTextureBlockPacketReader.ReadPacket([.. first, .. next], width, height, 16, 16);

        Assert.Equal(blocks, packet.Blocks.ToArray());
        Assert.Equal(first.Length, packet.ConsumedBodyBytes);
        Assert.Equal(next.Length, packet.TrailingBodyBytes);
    }

    [Theory]
    [InlineData("tiles")]
    [InlineData("offset")]
    [InlineData("compressed")]
    [InlineData("image-size")]
    [InlineData("budget")]
    public void LiteralBc3PacketsRetainTheirCopyAndExtentChecks(string defect)
    {
        var packet = LiteralPacket(4, 4, new byte[16], 0);
        if (defect == "tiles") BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(52), 1);
        if (defect == "offset") BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(64), 96);
        if (defect == "compressed") BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(56), 15);

        Assert.Throws<TextureFormatException>(() => NativeTextureBlockPacketReader.Read(packet, 4, 4,
            defect == "image-size" ? 8 : 16, defect == "budget" ? 15 : 16));
    }

    [Fact]
    public void ReadsLaterChunksAcrossAMiBBoundaryWithoutIncludingTheGapInPixels()
    {
        var first = Enumerable.Repeat((byte)47, 1024).ToArray();
        var second = Enumerable.Range(0, 1024).Select(index => (byte)(index % 251)).ToArray();
        var packet = Packet(first, second);
        int originalOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(80));
        var relocated = new byte[(1 << 20) + second.Length];
        packet.AsSpan(0, originalOffset).CopyTo(relocated);
        second.CopyTo(relocated, 1 << 20);
        BinaryPrimitives.WriteUInt32LittleEndian(relocated.AsSpan(80), 1 << 20);

        Assert.Equal(first.Concat(second), NativeTextureBlockPacketReader.Read(relocated, 64, 64, 2048, 2048));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectsFirstChunkRelocationAndNonMiBDirectoryGaps(bool firstChunk)
    {
        var packet = Packet(new byte[1024], new byte[1024]);
        var relocated = new byte[(1 << 20) + 16 + 1024];
        packet.CopyTo(relocated, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(relocated.AsSpan(firstChunk ? 64 : 80), (1 << 20) + (firstChunk ? 0u : 16u));

        Assert.Throws<TextureFormatException>(() => NativeTextureBlockPacketReader.Read(relocated, 64, 64, 2048, 2048));
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("overlap")]
    [InlineData("size")]
    [InlineData("trailing")]
    public void RejectsCorruptChunkChecksumsRangesAndImageSizes(string defect)
    {
        var packet = Packet(new byte[1024], new byte[1024]);
        if (defect == "checksum")
        {
            int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(64));
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(56));
            packet[offset + count - 1] ^= 1;
        }
        if (defect == "overlap") BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(80), 96);
        if (defect == "size") BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(76), 1023);
        if (defect == "trailing") packet = [..packet, 0];
        Assert.Throws<TextureFormatException>(() => NativeTextureBlockPacketReader.Read(packet, 64, 64, 2048, 2048));
    }

    [Fact]
    public void EnforcesSourceDimensionsAllocationLimitsAndCancellation()
    {
        var packet = Packet(new byte[1024], new byte[1024]);
        Assert.Throws<TextureFormatException>(() => NativeTextureBlockPacketReader.Read(packet, 32, 64, 2048, 2048));
        Assert.Throws<TextureFormatException>(() => NativeTextureBlockPacketReader.Read(packet, 64, 64, 2048, 2047));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => NativeTextureBlockPacketReader.Read(packet, 64, 64, 2048, 2048, canceled.Token));
    }

    private static byte[] LiteralPacket(int width, int height, byte[] blocks, int storedOffset)
    {
        var result = new byte[80 + blocks.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), checked((ushort)width));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(8), checked((ushort)height));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(40), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(56), checked((uint)blocks.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(60), checked((uint)blocks.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(64), checked((uint)storedOffset));
        blocks.CopyTo(result, 80);
        return result;
    }

    private static byte[] Packet(byte[] first, byte[] second, int unusedCompressedBytes = 0)
    {
        using var stream = new MemoryStream();
        using (var zlib = new ZLibStream(stream, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(first);
        var compressed = stream.ToArray(); Assert.True(compressed.Length < first.Length);
        if (unusedCompressedBytes > 0)
        {
            var original = compressed;
            compressed = new byte[original.Length + unusedCompressedBytes];
            original.CopyTo(compressed, 0);
            original.AsSpan(original.Length - 4).CopyTo(compressed.AsSpan(compressed.Length - 4));
        }
        int secondOffset = (96 + compressed.Length + 15) & ~15;
        var result = new byte[secondOffset + second.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), 255);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(8), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(40), 2);
        Write(56, compressed.Length); Write(60, first.Length); Write(64, 96);
        Write(72, second.Length); Write(76, second.Length); Write(80, secondOffset);
        compressed.CopyTo(result, 96); second.CopyTo(result, secondOffset);
        return result;
        void Write(int offset, int value) => BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset), (uint)value);
    }
}
