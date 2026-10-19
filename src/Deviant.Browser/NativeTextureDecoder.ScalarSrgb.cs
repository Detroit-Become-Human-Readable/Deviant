using System.Buffers.Binary;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

public sealed partial class NativeTextureDecoder
{
    private static bool IsScalarSrgb(NativeTextureSource source) => source.Metadata.FirstByte == 11 && IsSrgb(source);

    private long ScalarSrgbOutputBytes(long sourceBytes)
    {
        long bytes = checked(sourceBytes * 4);
        if (bytes > maximumDecodedBytes || bytes > DdsTextureReader.MaximumInputBytes - 148)
            throw Error("NativeTextureDecodeBudget", "The scalar sRGB representation exceeds the decode budget.");
        return bytes;
    }

    private DdsTextureDocument ReadNativeDocument(byte[] bytes, NativeTextureSource source, CancellationToken token)
    {
        if (!IsScalarSrgb(source)) return new DdsTextureReader().Read(bytes, source.Metadata.Identity, source.Revision);
        uint format = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(128));
        if (format == 29) return new DdsTextureReader().Read(bytes, source.Metadata.Identity, source.Revision);
        if (format != 61) throw Error("NativeScalarChannels", "The scalar texture representation is invalid.");
        int outputBytes = checked((int)ScalarSrgbOutputBytes(bytes.Length - 148L));
        token.ThrowIfCancellationRequested();
        var rgba = new byte[checked(148 + outputBytes)];
        bytes.AsSpan(0, 148).CopyTo(rgba);
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(rgba.AsSpan(20), checked(width * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(rgba.AsSpan(128), 29);
        // Preserve the encoded red bytes. The sRGB view supplies the transfer
        // before filtering, with the native missing-channel defaults G/B=0,A=1.
        for (int sourceOffset = 148, destination = 148; sourceOffset < bytes.Length; sourceOffset++, destination += 4)
        {
            if ((sourceOffset & 0xffff) == 148) token.ThrowIfCancellationRequested();
            rgba[destination] = bytes[sourceOffset]; rgba[destination + 3] = 255;
        }
        token.ThrowIfCancellationRequested();
        return new DdsTextureReader().TakeOwnership(rgba, source.Metadata.Identity, source.Revision);
    }
}
