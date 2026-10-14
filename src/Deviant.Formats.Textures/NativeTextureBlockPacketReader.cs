using System.Buffers.Binary;
using System.IO.Compression;

namespace Deviant.Formats.Textures;

public sealed record NativeTextureBlockPacketDocument(ReadOnlyMemory<byte> Blocks, int ConsumedBodyBytes, int TrailingBodyBytes);

/// <summary>Reads version-16 packets that copy or inflate native texture blocks.</summary>
public static class NativeTextureBlockPacketReader
{
    public static byte[] Read(ReadOnlySpan<byte> body, int width, int height, long expectedBytes,
        long maximumDecodedBytes, CancellationToken token = default)
    {
        var packet = ReadPacket(body, width, height, expectedBytes, maximumDecodedBytes, token);
        if (packet.TrailingBodyBytes != 0)
            throw Error("InvalidNativeBlockPacketSize", "The texture packet contains bytes outside its declared extent.");
        return packet.Blocks.ToArray();
    }

    public static NativeTextureBlockPacketDocument ReadPacket(ReadOnlySpan<byte> body, int width, int height, long expectedBytes,
        long maximumDecodedBytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (body.Length < 52 || body.Length > TextureBufferPatternInspector.MaximumStoredBytes ||
            BinaryPrimitives.ReadUInt16LittleEndian(body) != 16 || BinaryPrimitives.ReadUInt16LittleEndian(body[2..]) != 255 ||
            BinaryPrimitives.ReadUInt16LittleEndian(body[6..]) != width || BinaryPrimitives.ReadUInt16LittleEndian(body[8..]) != height ||
            width is < 1 or > DdsTextureReader.MaximumDimension || height is < 1 or > DdsTextureReader.MaximumDimension ||
            (long)width * height > DdsTextureReader.MaximumPixelCount || expectedBytes < 1 || expectedBytes > maximumDecodedBytes || expectedBytes > int.MaxValue)
            throw Error("InvalidNativeBlockPacket", "The native texture packet header or limits are invalid.");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(body[40..]);
        uint footer = BinaryPrimitives.ReadUInt32LittleEndian(body[44..]);
        int end = checked((52 + count * 16 + 15) & ~15);
        if (count is < 1 or > 4096 || end > body.Length || footer > 15 || BinaryPrimitives.ReadUInt16LittleEndian(body[42..]) != 0)
            throw Error("InvalidNativeBlockPacket", "The native texture packet directory is invalid.");
        long total = 0;
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            var row = Descriptor(body, index);
            if (row.Offset != ((end + 15) & ~15) || row.Encoded < 1 || row.Decoded is < 1 or > 65536 ||
                row.Encoded > row.Decoded || row.Offset > body.Length - row.Encoded)
                throw Error("InvalidNativeBlockPacketRange", "The native texture chunk bounds are invalid.");
            end = checked(row.Offset + row.Encoded); total = checked(total + row.Decoded);
            if (total > expectedBytes) throw Error("InvalidNativeBlockPacketSize", "The texture packet exceeds its declared native image size.");
        }
        if (total != expectedBytes || end + footer > body.Length)
            throw Error("InvalidNativeBlockPacketSize", "The texture packet does not match its declared native image size.");
        var output = new byte[checked((int)expectedBytes)]; int cursor = 0;
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested(); var row = Descriptor(body, index);
            var input = body.Slice(row.Offset, row.Encoded); var destination = output.AsSpan(cursor, row.Decoded);
            if (row.Encoded == row.Decoded) input.CopyTo(destination);
            else Inflate(input, destination, token);
            cursor += row.Decoded;
        }
        int consumed = checked(end + (int)footer);
        return new(output, consumed, body.Length - consumed);
    }

    private static (int Offset, int Encoded, int Decoded) Descriptor(ReadOnlySpan<byte> body, int index)
    {
        var row = body.Slice(52 + index * 16, 16);
        uint encoded = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]), decoded = BinaryPrimitives.ReadUInt32LittleEndian(row[8..]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(row[12..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(row) != 0 || encoded > int.MaxValue || decoded > int.MaxValue || offset > int.MaxValue)
            throw Error("InvalidNativeBlockPacketRange", "The texture chunk descriptor is invalid.");
        return ((int)offset, (int)encoded, (int)decoded);
    }

    private static void Inflate(ReadOnlySpan<byte> compressed, Span<byte> output, CancellationToken token)
    {
        if (compressed.Length < 6 || (compressed[0] & 15) != 8 || compressed[0] >> 4 > 7 ||
            ((compressed[0] << 8) | compressed[1]) % 31 != 0 || (compressed[1] & 32) != 0)
            throw Error("InvalidNativeBlockPacketCompression", "The texture chunk zlib header is invalid.");
        try
        {
            using var input = new MemoryStream(compressed.ToArray(), writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            int written = 0;
            while (written < output.Length)
            {
                token.ThrowIfCancellationRequested(); int read = zlib.Read(output[written..]);
                if (read == 0) throw new InvalidDataException("The texture chunk ended before its declared size.");
                written += read;
            }
            if (zlib.ReadByte() != -1) throw new InvalidDataException("The texture chunk exceeds its declared size.");
            uint a = 1, b = 0;
            foreach (byte value in output) { a = (a + value) % 65521; b = (b + a) % 65521; }
            if (((b << 16) | a) != BinaryPrimitives.ReadUInt32BigEndian(compressed[^4..]))
                throw new InvalidDataException("The texture chunk checksum does not match.");
        }
        catch (InvalidDataException error) { throw new TextureFormatException("InvalidNativeBlockPacketCompression", error.Message, error); }
    }

    private static TextureFormatException Error(string code, string message) => new(code, message);
}
