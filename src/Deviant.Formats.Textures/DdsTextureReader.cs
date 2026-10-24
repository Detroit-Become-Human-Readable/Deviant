using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Deviant.Assets;
using BCnEncoder.Shared;

namespace Deviant.Formats.Textures;

internal sealed record DdsTextureEncoding(
    TextureStorageFormat Format,
    string FormatName,
    CompressionFormat? Compression,
    int BytesPerPixel,
    uint RedMask,
    uint GreenMask,
    uint BlueMask,
    uint AlphaMask,
    bool AlphaOnly = false,
    bool Luminance = false,
    bool IsSrgb = false,
    bool IsHdr = false,
    int BlockBytes = 0)
{
    public bool CanDecode => Compression is not null || BytesPerPixel > 0;
}

/// <summary>Validates a bounded, single-surface DDS file and records every mip extent.</summary>
public sealed class DdsTextureReader
{
    public const int MaximumDimension = 16_384;
    public const long MaximumInputBytes = 512L * 1024 * 1024;
    public const long MaximumPixelCount = 64L * 1024 * 1024;
    public const int MaximumSubresources = 16_384;
    private const int MaximumArrayLayers = 2_048;
    private const int DdsHeaderLength = 128;
    private const uint DdsdMipMapCount = 0x0002_0000;
    private const uint DdsdPitch = 0x0000_0008;
    private const uint DdsdLinearSize = 0x0008_0000;
    private const uint DdsdDepth = 0x0080_0000;
    private const uint DdsRequiredHeaderFlags = 0x0000_1007;
    private const uint DdsCapsTexture = 0x0000_1000;
    private const uint DdsCapsComplex = 0x0000_0008;
    private const uint DdsCapsMipMap = 0x0040_0000;
    private const uint DdpfAlphaPixels = 0x0000_0001;
    private const uint DdpfAlpha = 0x0000_0002;
    private const uint DdpfFourCc = 0x0000_0004;
    private const uint DdpfRgb = 0x0000_0040;
    private const uint DdpfLuminance = 0x0002_0000;
    private const uint DdsCaps2CubeMap = 0x0000_0200;
    private const uint DdsCaps2CubeMapFaces = 0x0000_FC00;
    private const uint DdsCaps2Volume = 0x0020_0000;
    private const uint D3D10ResourceDimensionTexture2D = 3;
    private const uint D3D10ResourceMiscTextureCube = 0x4;

    public static bool IsDds(ReadOnlySpan<byte> bytes) => bytes.Length >= 4 && bytes[..4].SequenceEqual("DDS "u8);

    public DdsTextureDocument Read(ReadOnlyMemory<byte> bytes, ResourceIdentity? identity = null, SourceRevision? revision = null)
    {
        if (bytes.Length > MaximumInputBytes)
            throw Error("TextureTooLarge", "The DDS file exceeds the decoder's input limit.");
        return ReadOwned(bytes.ToArray(), identity, revision);
    }

    public DdsTextureDocument Read(Stream source, ResourceIdentity? identity = null, SourceRevision? revision = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException("The DDS source stream must be readable and seekable.", nameof(source));
        if (source.Length < 0 || source.Length > MaximumInputBytes)
            throw Error("TextureTooLarge", "The DDS stream exceeds the decoder's input limit.");
        var bytes = new byte[checked((int)source.Length)];
        source.Position = 0;
        source.ReadExactly(bytes);
        return ReadOwned(bytes, identity, revision);
    }

    /// <summary>Transfers a buffer to the document. The caller must not modify it after this call.</summary>
    public DdsTextureDocument TakeOwnership(byte[] bytes, ResourceIdentity? identity = null, SourceRevision? revision = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaximumInputBytes)
            throw Error("TextureTooLarge", "The DDS file exceeds the decoder's input limit.");
        return ReadOwned(bytes, identity, revision);
    }

    private static DdsTextureDocument ReadOwned(byte[] bytes, ResourceIdentity? identity, SourceRevision? revision)
    {
        if (revision is not null && identity is null)
            throw new ArgumentException("A source revision requires a concrete archive resource identity.");
        if (identity is not null && revision is not null &&
            !StringComparer.Ordinal.Equals(identity.IndexContentId, revision.IndexContentId))
            throw new ArgumentException("A DDS identity and source revision must refer to the same archive revision.");
        var data = bytes.AsSpan();
        if (data.Length < DdsHeaderLength || !IsDds(data))
            throw Error("InvalidSignature", "The input does not contain a complete DDS header.");
        if (ReadUInt32(data, 4) != 124 || ReadUInt32(data, 76) != 32)
            throw Error("InvalidHeader", "The DDS header or pixel-format record has an unexpected size.");

        var flags = ReadUInt32(data, 8);
        if ((flags & DdsRequiredHeaderFlags) != DdsRequiredHeaderFlags)
            throw Error("InvalidHeaderFlags", "The DDS omits one or more required header flags.");
        // dwDepth is unused for flat textures unless DDSD_DEPTH is set.
        if ((flags & DdsdDepth) != 0)
            throw Error("UnsupportedResourceShape", "Volume and depth textures are not supported by this reader.");
        var caps2 = ReadUInt32(data, 112);
        if ((caps2 & DdsCaps2Volume) != 0)
            throw Error("UnsupportedResourceShape", "Volume textures are not supported by this reader.");
        var legacyCube = (caps2 & DdsCaps2CubeMap) != 0;
        var legacyFaceMask = caps2 & DdsCaps2CubeMapFaces;
        if (legacyFaceMask != 0 && !legacyCube)
            throw Error("InvalidCubeFaceFlags", "DDS cube-face flags are set without the cubemap flag.");
        if (legacyCube && legacyFaceMask == 0)
            throw Error("InvalidCubeFaceFlags", "The legacy cubemap has no declared faces.");
        var height = ReadDimension(ReadUInt32(data, 12), "height");
        var width = ReadDimension(ReadUInt32(data, 16), "width");
        if ((long)width * height > MaximumPixelCount)
            throw Error("ImageTooLarge", "The DDS dimensions exceed the decoder's pixel limit.");

        var maximumMipCount = BitOperations.Log2((uint)Math.Max(width, height)) + 1;
        var declaredMipCount = ReadUInt32(data, 28);
        int mipCount;
        if ((flags & DdsdMipMapCount) == 0)
        {
            if (declaredMipCount > 1)
                throw Error("InvalidMipCount", "The DDS declares multiple mip levels without the mip-count flag.");
            mipCount = 1;
        }
        else
        {
            if (declaredMipCount is 0 or > MaximumDimension)
                throw Error("InvalidMipCount", "The DDS mip count is zero or exceeds the format limit.");
            mipCount = checked((int)declaredMipCount);
            if (mipCount > maximumMipCount)
                throw Error("InvalidMipCount", "The DDS mip count exceeds the dimensions' full mip chain.");
        }

        var pixelFlags = ReadUInt32(data, 80);
        var fourCc = ReadUInt32(data, 84);
        var headerLength = DdsHeaderLength;
        var arrayLayerCount = 1;
        var isCubeMap = legacyCube;
        IReadOnlyList<CubeFace> cubeFaces = legacyCube ? FacesFromMask(legacyFaceMask) : [];
        DdsTextureEncoding encoding;
        if ((pixelFlags & DdpfFourCc) != 0)
        {
            if (fourCc == FourCc("DX10"))
            {
                if (data.Length < DdsHeaderLength + 20)
                    throw Error("TruncatedDx10Header", "The DDS DX10 extension is truncated.");
                headerLength += 20;
                var format = ReadUInt32(data, 128);
                var resourceDimension = ReadUInt32(data, 132);
                var miscFlag = ReadUInt32(data, 136);
                var arraySize = ReadUInt32(data, 140);
                if (resourceDimension != D3D10ResourceDimensionTexture2D)
                    throw Error("UnsupportedResourceShape", "Only 2D DDS textures are supported.");
                if (arraySize is 0 or > MaximumArrayLayers)
                    throw Error("InvalidArraySize", "The DX10 texture array size is zero or exceeds the supported limit.");
                arrayLayerCount = checked((int)arraySize);
                isCubeMap = (miscFlag & D3D10ResourceMiscTextureCube) != 0;
                if (isCubeMap)
                {
                    if (legacyFaceMask != 0 && legacyFaceMask != DdsCaps2CubeMapFaces)
                        throw Error("InvalidCubeFaceFlags", "A DX10 cubemap must contain all six faces.");
                    cubeFaces = Enum.GetValues<CubeFace>();
                }
                else
                {
                    if (legacyCube || legacyFaceMask != 0)
                        throw Error("InvalidCubeFaceFlags", "The DX10 cubemap flag disagrees with the legacy header flags.");
                    cubeFaces = [];
                }
                encoding = DxgiEncoding(format);
                if (encoding.Format == TextureStorageFormat.Bc1 && (ReadUInt32(data, 144) & 7) == 3)
                    encoding = encoding with { Compression = CompressionFormat.Bc1 };
            }
            else
            {
                encoding = LegacyFourCcEncoding(fourCc);
            }
        }
        else
        {
            encoding = LegacyPixelEncoding(data, pixelFlags);
        }

        if (isCubeMap && width != height)
            throw Error("InvalidCubeDimensions", "Cubemap faces must be square.");

        var faces = isCubeMap ? cubeFaces.Select(face => (CubeFace?)face).ToArray() : new CubeFace?[] { null };
        var subresourceCount = checked((long)arrayLayerCount * faces.Length * mipCount);
        if (subresourceCount > MaximumSubresources)
            throw Error("TooManySubresources", "The DDS array, face, and mip count exceeds the parser limit.");
        if ((arrayLayerCount > 1 || isCubeMap) && (ReadUInt32(data, 108) & DdsCapsComplex) == 0)
            throw Error("InvalidArrayCaps", "DDS arrays and cubemaps must declare complex surface caps.");

        var subresources = new List<TextureSubresourceExtent>(checked((int)subresourceCount));
        var offset = (long)headerLength;
        for (var arrayLayer = 0; arrayLayer < arrayLayerCount; arrayLayer++)
        foreach (var face in faces)
        {
            var mipWidth = width;
            var mipHeight = height;
            for (var mip = 0; mip < mipCount; mip++)
            {
            var length = GetMipByteLength(mipWidth, mipHeight, encoding);
                if (length > int.MaxValue || offset > data.Length || length > data.Length - offset)
                    throw Error("TruncatedSubresource", $"The DDS payload ends inside array {arrayLayer}, face {face?.ToString() ?? "2D"}, mip {mip}.");
                var selection = new TextureSubresourceSelection(arrayLayer, face, mip);
                subresources.Add(new(selection, mipWidth, mipHeight, offset, checked((int)length)));
                offset = checked(offset + length);
                mipWidth = Math.Max(1, mipWidth / 2);
                mipHeight = Math.Max(1, mipHeight / 2);
            }
        }

        var defaultFace = faces[0];
        var mipLevels = subresources.Where(resource => resource.Selection.ArrayLayer == 0 && resource.Selection.Face == defaultFace)
            .Select(resource => new TextureMipLevel(resource.Selection.MipIndex, resource.Width, resource.Height,
                resource.SourceOffset, resource.EncodedLength)).ToArray();
        var firstMipLength = subresources[0].EncodedLength;
        var pitchOrSize = ReadUInt32(data, 20);
        if ((flags & DdsdPitch) != 0 && encoding.BlockBytes == 0 && pitchOrSize != width * encoding.BytesPerPixel)
            throw Error("UnsupportedRowPitch", "The DDS uses a padded row pitch that is not supported by this decoder.");
        if ((flags & DdsdLinearSize) != 0 && encoding.BlockBytes > 0 && pitchOrSize != firstMipLength)
            throw Error("InvalidLinearSize", "The DDS linear size does not match its top mip extent.");

        var caps = ReadUInt32(data, 108);
        if ((caps & DdsCapsTexture) == 0)
            throw Error("InvalidCaps", "The DDS does not declare a texture surface.");
        if (mipCount > 1 && (caps & (DdsCapsComplex | DdsCapsMipMap)) != (DdsCapsComplex | DdsCapsMipMap))
            throw Error("InvalidMipCaps", "The DDS mip chain is missing the required complex or mip-map caps.");

        var info = new DdsTextureInfo(width, height, mipCount, encoding.Format, encoding.FormatName, encoding.IsSrgb, encoding.IsHdr,
            headerLength, offset - headerLength, data.Length - offset)
        {
            ArrayLayerCount = arrayLayerCount,
            IsCubeMap = isCubeMap,
            CubeFaces = cubeFaces,
            PixelDecodeSupported = encoding.CanDecode
        };
        return new(bytes, info, mipLevels, subresources.AsReadOnly(), encoding, identity, revision);
    }

    public async Task<DdsTextureDocument> ReadAsync(IResourceResolver resolver, ResourceIdentity identity,
        long storedLength, CancellationToken cancellationToken = default)
        => await ReadArchiveBytesAsync(resolver, identity, null, storedLength, cancellationToken).ConfigureAwait(false);

    public async Task<DdsTextureDocument> ReadAsync(IResourceResolver resolver, ResourceIdentity identity,
        SourceRevision revision, long storedLength, CancellationToken cancellationToken = default)
        => await ReadArchiveBytesAsync(resolver, identity, revision, storedLength, cancellationToken).ConfigureAwait(false);

    private async Task<DdsTextureDocument> ReadArchiveBytesAsync(IResourceResolver resolver, ResourceIdentity identity,
        SourceRevision? revision, long storedLength, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(identity);
        if (storedLength < 0 || storedLength > MaximumInputBytes)
            throw Error("TextureTooLarge", "The DDS resource extent exceeds the decoder's input limit.");
        var bytes = new byte[checked((int)storedLength)];
        const int chunkSize = 8 * 1024 * 1024;
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(chunkSize, bytes.Length - offset);
            var chunk = await resolver.ReadAsync(identity, offset, length, cancellationToken).ConfigureAwait(false);
            if (chunk.Length != length)
                throw Error("TruncatedResource", "The archive resolver returned fewer bytes than the DDS resource extent declares.");
            chunk.CopyTo(bytes, offset);
            offset += length;
        }
        return ReadOwned(bytes, identity, revision);
    }

    internal static long GetMipByteLength(int width, int height, DdsTextureEncoding encoding)
    {
        if (encoding.BlockBytes > 0)
        {
            var blocksWide = Math.Max(1L, (width + 3L) / 4);
            var blocksHigh = Math.Max(1L, (height + 3L) / 4);
            return checked(blocksWide * blocksHigh * encoding.BlockBytes);
        }
        return checked((long)width * height * encoding.BytesPerPixel);
    }

    private static DdsTextureEncoding LegacyFourCcEncoding(uint fourCc) => fourCc switch
    {
        0x3154_5844 => Compressed(TextureStorageFormat.Bc1, "BC1/DXT1", CompressionFormat.Bc1WithAlpha), // DXT1
        0x3354_5844 => Compressed(TextureStorageFormat.Bc2, "BC2/DXT3", CompressionFormat.Bc2), // DXT3
        0x3554_5844 => Compressed(TextureStorageFormat.Bc3, "BC3/DXT5", CompressionFormat.Bc3), // DXT5
        0x3149_5441 or 0x5534_4342 => Compressed(TextureStorageFormat.Bc4, "BC4", CompressionFormat.Bc4), // ATI1 / BC4U
        0x3249_5441 or 0x5535_4342 => Compressed(TextureStorageFormat.Bc5, "BC5", CompressionFormat.Bc5), // ATI2 / BC5U
        0x5334_4342 => UnsupportedCompressed(TextureStorageFormat.Bc4Signed, "BC4_SNORM", 8), // BC4S
        0x5335_4342 => UnsupportedCompressed(TextureStorageFormat.Bc5Signed, "BC5_SNORM", 16), // BC5S
        0x4836_4342 => Compressed(TextureStorageFormat.Bc6Unsigned, "BC6H", CompressionFormat.Bc6U, isHdr: true),
        0x2037_4342 => Compressed(TextureStorageFormat.Bc7, "BC7", CompressionFormat.Bc7),
        0x3254_5844 or 0x3454_5844 => throw Error("UnsupportedFormat", "Premultiplied-alpha DXT2/DXT4 textures are not supported."),
        _ => throw Error("UnsupportedFormat", $"The DDS FourCC '{FourCcText(fourCc)}' is not supported.")
    };

    private static DdsTextureEncoding DxgiEncoding(uint format) => format switch
    {
        71 or 72 => Compressed(TextureStorageFormat.Bc1, "BC1", CompressionFormat.Bc1WithAlpha, isSrgb: format == 72),
        74 or 75 => Compressed(TextureStorageFormat.Bc2, "BC2", CompressionFormat.Bc2, isSrgb: format == 75),
        77 or 78 => Compressed(TextureStorageFormat.Bc3, "BC3", CompressionFormat.Bc3, isSrgb: format == 78),
        80 => Compressed(TextureStorageFormat.Bc4, "BC4_UNORM", CompressionFormat.Bc4),
        81 => UnsupportedCompressed(TextureStorageFormat.Bc4Signed, "BC4_SNORM", 8),
        83 => Compressed(TextureStorageFormat.Bc5, "BC5_UNORM", CompressionFormat.Bc5),
        84 => UnsupportedCompressed(TextureStorageFormat.Bc5Signed, "BC5_SNORM", 16),
        95 => Compressed(TextureStorageFormat.Bc6Unsigned, "BC6H_UF16", CompressionFormat.Bc6U, isHdr: true),
        96 => Compressed(TextureStorageFormat.Bc6Signed, "BC6H_SF16", CompressionFormat.Bc6S, isHdr: true),
        98 or 99 => Compressed(TextureStorageFormat.Bc7, "BC7", CompressionFormat.Bc7, isSrgb: format == 99),
        28 or 29 => new(TextureStorageFormat.Rgba8, format == 29 ? "R8G8B8A8_UNORM_SRGB" : "R8G8B8A8_UNORM",
            null, 4, 0x0000_00FF, 0x0000_FF00, 0x00FF_0000, 0xFF00_0000, IsSrgb: format == 29),
        87 or 91 => new(TextureStorageFormat.Bgra8, format == 91 ? "B8G8R8A8_UNORM_SRGB" : "B8G8R8A8_UNORM",
            null, 4, 0x00FF_0000, 0x0000_FF00, 0x0000_00FF, 0xFF00_0000, IsSrgb: format == 91),
        88 or 93 => new(TextureStorageFormat.Bgra8, format == 93 ? "B8G8R8X8_UNORM_SRGB" : "B8G8R8X8_UNORM",
            null, 4, 0x00FF_0000, 0x0000_FF00, 0x0000_00FF, 0, IsSrgb: format == 93),
        85 => new(TextureStorageFormat.Bgr565, "B5G6R5_UNORM", null, 2, 0xF800, 0x07E0, 0x001F, 0),
        86 => new(TextureStorageFormat.Bgra5551, "B5G5R5A1_UNORM", null, 2, 0x7C00, 0x03E0, 0x001F, 0x8000),
        61 => new(TextureStorageFormat.Luminance8, "R8_UNORM", null, 1, 0xFF, 0, 0, 0),
        65 => new(TextureStorageFormat.Alpha8, "A8_UNORM", null, 1, 0, 0, 0, 0xFF, AlphaOnly: true),
        _ => throw Error("UnsupportedFormat", $"DXGI format {format} is not supported.")
    };

    private static DdsTextureEncoding LegacyPixelEncoding(ReadOnlySpan<byte> data, uint pixelFlags)
    {
        var bits = ReadUInt32(data, 88);
        var red = ReadUInt32(data, 92);
        var green = ReadUInt32(data, 96);
        var blue = ReadUInt32(data, 100);
        var alpha = ReadUInt32(data, 104);
        if ((pixelFlags & DdpfLuminance) != 0)
        {
            if (bits == 8 && red == 0xFF)
                return new(TextureStorageFormat.Luminance8, "L8", null, 1, red, 0, 0, 0, Luminance: true);
            throw Error("UnsupportedFormat", "Only 8-bit luminance DDS textures are supported.");
        }
        if ((pixelFlags & DdpfAlpha) != 0 && (pixelFlags & DdpfRgb) == 0)
        {
            if (bits == 8)
                return new(TextureStorageFormat.Alpha8, "A8", null, 1, 0, 0, 0, 0xFF, AlphaOnly: true);
            throw Error("UnsupportedFormat", "Only 8-bit alpha-only DDS textures are supported.");
        }
        if ((pixelFlags & DdpfRgb) == 0 || bits is not (16 or 24 or 32))
            throw Error("UnsupportedFormat", "The legacy DDS pixel format is not supported.");
        var bytesPerPixel = checked((int)bits / 8);
        if (red == 0 || green == 0 || blue == 0 ||
            (red & green) != 0 || (red & blue) != 0 || (green & blue) != 0 ||
            (alpha & (red | green | blue)) != 0)
            throw Error("InvalidPixelMasks", "The DDS RGB channel masks are missing or overlap.");
        var maskLimit = bits == 32 ? uint.MaxValue : (1u << checked((int)bits)) - 1;
        if (((red | green | blue | alpha) & ~maskLimit) != 0 || !IsContiguous(red) || !IsContiguous(green) || !IsContiguous(blue) || (alpha != 0 && !IsContiguous(alpha)))
            throw Error("InvalidPixelMasks", "The DDS pixel masks are outside the pixel width or are not contiguous.");
        var storage = bits == 16 && green == 0x07E0 ? TextureStorageFormat.Bgr565
            : bits == 16 && alpha == 0x8000 ? TextureStorageFormat.Bgra5551
            : bits == 24 ? TextureStorageFormat.Bgr24
            : red == 0x0000_00FF ? TextureStorageFormat.Rgba8 : TextureStorageFormat.Bgra8;
        var name = bits == 16 && green == 0x07E0 ? "RGB565" : bits == 16 && alpha == 0x8000 ? "ARGB1555" : $"RGB{bits}";
        return new(storage, name, null, bytesPerPixel, red, green, blue, alpha);
    }

    private static DdsTextureEncoding Compressed(TextureStorageFormat format, string name, CompressionFormat compression,
        bool isSrgb = false, bool isHdr = false) =>
        new(format, name, compression, 0, 0, 0, 0, 0, IsSrgb: isSrgb, IsHdr: isHdr,
            BlockBytes: compression is CompressionFormat.Bc1 or CompressionFormat.Bc1WithAlpha or CompressionFormat.Bc4 ? 8 : 16);

    private static DdsTextureEncoding UnsupportedCompressed(TextureStorageFormat format, string name, int blockBytes) =>
        new(format, name, null, 0, 0, 0, 0, 0, BlockBytes: blockBytes);

    private static int ReadDimension(uint value, string name)
    {
        if (value is 0 or > MaximumDimension) throw Error("InvalidDimensions", $"The DDS {name} is outside the supported range.");
        return checked((int)value);
    }

    private static bool IsContiguous(uint mask)
    {
        if (mask == 0) return false;
        var shifted = mask >> BitOperations.TrailingZeroCount(mask);
        return (shifted & (shifted + 1)) == 0;
    }

    private static IReadOnlyList<CubeFace> FacesFromMask(uint mask)
    {
        var faces = new List<CubeFace>(6);
        if ((mask & 0x0000_0400) != 0) faces.Add(CubeFace.PositiveX);
        if ((mask & 0x0000_0800) != 0) faces.Add(CubeFace.NegativeX);
        if ((mask & 0x0000_1000) != 0) faces.Add(CubeFace.PositiveY);
        if ((mask & 0x0000_2000) != 0) faces.Add(CubeFace.NegativeY);
        if ((mask & 0x0000_4000) != 0) faces.Add(CubeFace.PositiveZ);
        if ((mask & 0x0000_8000) != 0) faces.Add(CubeFace.NegativeZ);
        return faces.AsReadOnly();
    }

    internal static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    private static uint FourCc(string text) => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(text));
    private static string FourCcText(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return Encoding.ASCII.GetString(bytes);
    }
    private static TextureFormatException Error(string code, string message) => new(code, message);
}
