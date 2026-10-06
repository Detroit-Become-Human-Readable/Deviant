using System.Buffers.Binary;
using System.Text;

namespace Deviant.Texture.Tests;

internal static class TextureFixtureBuilder
{
    private const int HeaderLength = 128;

    public static byte[] CreateDxt1(int width, int height, int mipCount, byte[] payload, bool addTrailingByte = false)
    {
        var bytes = new byte[HeaderLength + payload.Length + (addTrailingByte ? 1 : 0)];
        "DDS "u8.CopyTo(bytes);
        WriteUInt32(bytes, 4, 124);
        var flags = 0x0000_1007u | 0x0008_0000u;
        if (mipCount > 1) flags |= 0x0002_0000u;
        WriteUInt32(bytes, 8, flags);
        WriteUInt32(bytes, 12, checked((uint)height));
        WriteUInt32(bytes, 16, checked((uint)width));
        WriteUInt32(bytes, 20, checked((uint)MipLength(width, height, 8)));
        WriteUInt32(bytes, 28, checked((uint)mipCount));
        WriteUInt32(bytes, 76, 32);
        WriteUInt32(bytes, 80, 4);
        "DXT1"u8.CopyTo(bytes.AsSpan(84, 4));
        WriteUInt32(bytes, 108, mipCount > 1 ? 0x0040_1008u : 0x0000_1000u);
        payload.CopyTo(bytes, HeaderLength);
        return bytes;
    }

    public static byte[] CreateDx10Rgba8(int width, int height, byte[] pixels)
        => CreateDx10(width, height, 28, pixels);

    public static byte[] CreateDx10(int width, int height, uint dxgiFormat, byte[] payload,
        uint arraySize = 1, uint miscFlag = 0)
    {
        var bytes = new byte[HeaderLength + 20 + payload.Length];
        "DDS "u8.CopyTo(bytes);
        WriteUInt32(bytes, 4, 124);
        WriteUInt32(bytes, 8, 0x0000_1007u);
        WriteUInt32(bytes, 12, checked((uint)height));
        WriteUInt32(bytes, 16, checked((uint)width));
        WriteUInt32(bytes, 76, 32);
        WriteUInt32(bytes, 80, 4);
        "DX10"u8.CopyTo(bytes.AsSpan(84, 4));
        WriteUInt32(bytes, 108, arraySize > 1 || (miscFlag & 0x4) != 0 ? 0x0000_1008u : 0x0000_1000u);
        WriteUInt32(bytes, 128, dxgiFormat);
        WriteUInt32(bytes, 132, 3);  // D3D10_RESOURCE_DIMENSION_TEXTURE2D.
        WriteUInt32(bytes, 136, miscFlag);
        WriteUInt32(bytes, 140, arraySize);
        payload.CopyTo(bytes, HeaderLength + 20);
        return bytes;
    }

    public static byte[] CreateLegacyCube(int width, int height, uint faceMask, byte[] payload)
    {
        var bytes = new byte[HeaderLength + payload.Length];
        "DDS "u8.CopyTo(bytes);
        WriteUInt32(bytes, 4, 124);
        WriteUInt32(bytes, 8, 0x0000_1007u | 0x0008_0000u);
        WriteUInt32(bytes, 12, checked((uint)height));
        WriteUInt32(bytes, 16, checked((uint)width));
        WriteUInt32(bytes, 20, checked((uint)MipLength(width, height, 8)));
        WriteUInt32(bytes, 76, 32);
        WriteUInt32(bytes, 80, 4);
        "DXT1"u8.CopyTo(bytes.AsSpan(84, 4));
        WriteUInt32(bytes, 108, 0x0000_1008u);
        WriteUInt32(bytes, 112, 0x0000_0200u | faceMask);
        payload.CopyTo(bytes, HeaderLength);
        return bytes;
    }

    public static byte[] SolidDxt1Block(ushort firstRgb565, ushort secondRgb565, uint selectors = 0)
    {
        var block = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(block, firstRgb565);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2), secondRgb565);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), selectors);
        return block;
    }

    public static int MipLength(int width, int height, int blockBytes)
    {
        var blocksWide = Math.Max(1, (width + 3) / 4);
        var blocksHigh = Math.Max(1, (height + 3) / 4);
        return checked(blocksWide * blocksHigh * blockBytes);
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}
