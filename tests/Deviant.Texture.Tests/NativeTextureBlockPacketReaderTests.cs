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

    private static byte[] Packet(byte[] first, byte[] second)
    {
        using var stream = new MemoryStream();
        using (var zlib = new ZLibStream(stream, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(first);
        var compressed = stream.ToArray(); Assert.True(compressed.Length < first.Length);
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
