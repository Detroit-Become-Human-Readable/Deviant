using Deviant.Assets;
using System.Security.Cryptography;

namespace Deviant.Formats.Textures;

public enum TextureStorageFormat
{
    Rgba8,
    Bgra8,
    Bgr24,
    Bgr565,
    Bgra5551,
    Bc1,
    Bc2,
    Bc3,
    Bc4,
    Bc4Signed,
    Bc5,
    Bc5Signed,
    Bc6Unsigned,
    Bc6Signed,
    Bc7,
    Luminance8,
    Alpha8
}

public sealed record TextureMipLevel(int Index, int Width, int Height, long SourceOffset, int EncodedLength);
public enum CubeFace { PositiveX, NegativeX, PositiveY, NegativeY, PositiveZ, NegativeZ }
public sealed record TextureSubresourceSelection(int ArrayLayer = 0, CubeFace? Face = null, int MipIndex = 0);
public sealed record TextureSubresourceExtent(TextureSubresourceSelection Selection, int Width, int Height,
    long SourceOffset, int EncodedLength);

public sealed record DdsTextureInfo(
    int Width,
    int Height,
    int MipCount,
    TextureStorageFormat Format,
    string FormatName,
    bool IsSrgb,
    bool IsHdr,
    int HeaderLength,
    long EncodedLength,
    long TrailingLength)
{
    public int ArrayLayerCount { get; init; } = 1;
    public bool IsCubeMap { get; init; }
    public bool PixelDecodeSupported { get; init; } = true;
    public IReadOnlyList<CubeFace> CubeFaces { get; init; } = [];
}

public sealed class DdsTextureDocument
{
    internal DdsTextureDocument(ReadOnlyMemory<byte> original, DdsTextureInfo info,
        IReadOnlyList<TextureMipLevel> mipLevels, IReadOnlyList<TextureSubresourceExtent> subresources,
        DdsTextureEncoding encoding,
        ResourceIdentity? identity = null, SourceRevision? revision = null)
    {
        OriginalBytes = original;
        ContentSha256 = Convert.ToHexString(SHA256.HashData(original.Span)).ToLowerInvariant();
        Info = info;
        MipLevels = mipLevels;
        Subresources = subresources;
        Encoding = encoding;
        Identity = identity;
        Revision = revision;
    }

    public ReadOnlyMemory<byte> OriginalBytes { get; }
    public string ContentSha256 { get; }
    public DdsTextureInfo Info { get; }
    public IReadOnlyList<TextureMipLevel> MipLevels { get; }
    public IReadOnlyList<TextureSubresourceExtent> Subresources { get; }
    public TextureSubresourceSelection DefaultSelection => Subresources.Count == 0
        ? new()
        : Subresources[0].Selection with { MipIndex = 0 };
    public ResourceIdentity? Identity { get; }
    public SourceRevision? Revision { get; }
    internal DdsTextureEncoding Encoding { get; }
}

public readonly record struct HdrPixel(float R, float G, float B);
public readonly record struct Rgba8Pixel(byte R, byte G, byte B, byte A);
public enum TextureToneMapOperator { Reinhard, Aces }
public sealed record TextureDisplayTransform(TextureToneMapOperator Operator, float ExposureStops,
    bool EncodedSrgb, string Version = "texture-display-v1");

public sealed class TextureSurface
{
    internal TextureSurface(int width, int height, ReadOnlyMemory<byte> rgba8, ReadOnlyMemory<HdrPixel> hdrPixels,
        bool isHdr, bool sourceIsHdr, bool isSrgb, int mipIndex, TextureStorageFormat sourceFormat,
        string sourceFormatName, string sourcePayloadSha256, TextureSubresourceSelection subresource,
        ResourceIdentity? identity, SourceRevision? revision,
        TextureDisplayTransform? displayTransform = null)
    {
        Width = width;
        Height = height;
        Rgba8 = rgba8;
        HdrPixels = hdrPixels;
        IsHdr = isHdr;
        SourceIsHdr = sourceIsHdr;
        IsSrgb = isSrgb;
        MipIndex = mipIndex;
        SourceFormat = sourceFormat;
        SourceFormatName = sourceFormatName;
        SourcePayloadSha256 = sourcePayloadSha256;
        Subresource = subresource;
        Identity = identity;
        Revision = revision;
        DisplayTransform = displayTransform;
    }

    public int Width { get; }
    public int Height { get; }
    public bool IsHdr { get; }
    public bool SourceIsHdr { get; }
    public bool IsSrgb { get; }
    public int MipIndex { get; }
    public TextureStorageFormat SourceFormat { get; }
    public string SourceFormatName { get; }
    public string SourcePayloadSha256 { get; }
    public TextureSubresourceSelection Subresource { get; }
    public ResourceIdentity? Identity { get; }
    public SourceRevision? Revision { get; }
    public TextureDisplayTransform? DisplayTransform { get; }
    public ReadOnlyMemory<byte> Rgba8 { get; }
    public ReadOnlyMemory<HdrPixel> HdrPixels { get; }
}

public sealed class TextureFormatException(string code, string message, Exception? inner = null)
    : IOException(message, inner)
{
    public string Code { get; } = code;
}
