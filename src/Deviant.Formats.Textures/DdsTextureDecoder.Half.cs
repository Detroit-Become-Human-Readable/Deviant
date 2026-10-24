using System.Buffers.Binary;
namespace Deviant.Formats.Textures;
public sealed partial class DdsTextureDecoder
{
    private static TextureSurface DecodeHalf(DdsTextureDocument document, TextureMipLevel level,
        TextureSubresourceSelection selection, ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        int count = checked(level.Width * level.Height);
        if (bytes.Length != checked(count * 8)) throw new TextureFormatException("InvalidPixelExtent", "The floating-point image extent is invalid.");
        var pixels = new HdrPixel[count];
        for (int i = 0; i < count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var p = bytes.Slice(i * 8, 8);
            pixels[i] = new((float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(p)),
                (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(p[2..])),
                (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(p[4..])),
                (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(p[6..])));
        }
        token.ThrowIfCancellationRequested();
        return new(level.Width, level.Height, ReadOnlyMemory<byte>.Empty, pixels, true, true, false,
            selection.MipIndex, document.Info.Format, document.Info.FormatName, document.ContentSha256, selection, document.Identity, document.Revision);
    }
}
