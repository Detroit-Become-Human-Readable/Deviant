using System.Buffers.Binary;

namespace Deviant.Formats.Textures;

/// <summary>Projects FILETEXT v24 sampler fields through the native texture creation contract.</summary>
public static class TextureFileTextSamplingReader
{
    public static NativeTextureSamplingProfile Read(TextureFileTextV24Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var prefix = document.RawFixedPrefix84;
        if (prefix.Length != 84 || prefix[7] != document.MipSizeCount || prefix[8] > 15 ||
            prefix[9] > 7 || prefix[10] > 7 || prefix[11] > 7)
            throw new TextureFormatException("NativeTextureSamplingInvalid", "The serialized texture sampling fields are invalid.");
        ulong key = 0x7FFE000080000UL;
        key &= ~0xffffUL;
        key &= 0xFFFFF0007FFFFFUL;
        key |= 0x1F00000003000000UL;
        int filter = document.MipSizeCount <= 1 && prefix[8] >= 2 ? 1 : prefix[8];
        key = (key & ~0x780000UL) | ((ulong)filter << 19);
        key = (key & ~0x38000000UL) | ((ulong)prefix[9] << 27);
        key = (key & ~0x1C0000000UL) | ((ulong)prefix[10] << 30);
        key = (key & ~0xE00000000UL) | ((ulong)prefix[11] << 33);
        var border = new float[4];
        for (int i = 0; i < border.Length; i++)
        {
            border[i] = BinaryPrimitives.ReadSingleLittleEndian(prefix.AsSpan(68 + i * 4, 4));
            if (!float.IsFinite(border[i]))
                throw new TextureFormatException("NativeTextureSamplingInvalid", "The serialized texture border colour is invalid.");
        }
        uint borderIndex = border[3] == 1 && border[0] == 0 && border[1] == 0 && border[2] == 0 ? 1u
            : border[3] == 1 && border[0] == 1 && border[1] == 1 && border[2] == 1 ? 2u : 0u;
        if (((key >> 53) & 3) != borderIndex)
            key = (key & 0xFF80001FFFFFFFFFUL) | ((ulong)borderIndex << 53) | 0x7FFE000000000UL;
        return NativeTextureSamplingDecoder.Decode(key);
    }
}
