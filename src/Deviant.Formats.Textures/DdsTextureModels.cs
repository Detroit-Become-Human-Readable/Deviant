using Deviant.Assets;
using System.Security.Cryptography;
using System.Buffers.Binary;
using BCnEncoder.Shared;

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

public sealed partial class TextureSurface
{
    public static bool CanRetainEncodedDds(DdsTextureDocument document, TextureSubresourceSelection selection,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(selection);
        token.ThrowIfCancellationRequested();
        if (document.Info.TrailingLength != 0) return false;
        var extent = document.Subresources.SingleOrDefault(item => item.Selection == selection);
        if (extent is null || extent.SourceOffset < 0 || extent.SourceOffset > document.OriginalBytes.Length - extent.EncodedLength)
            return false;
        var encoding = document.Encoding;
        if (document.Info.IsHdr)
            return !document.Info.IsSrgb && (document.Info.Format == TextureStorageFormat.Bc6Unsigned &&
                encoding.Compression == CompressionFormat.Bc6U || document.Info.Format == TextureStorageFormat.Bc6Signed &&
                encoding.Compression == CompressionFormat.Bc6S);
        if (document.Info.Format == TextureStorageFormat.Luminance8)
            return !document.Info.IsSrgb && encoding.Compression is null && !encoding.Luminance && !encoding.AlphaOnly &&
                encoding.BytesPerPixel == 1 && encoding.RedMask == 0xff && encoding.GreenMask == 0 &&
                encoding.BlueMask == 0 && encoding.AlphaMask == 0;
        if (document.Info.Format is not (TextureStorageFormat.Bc1 or TextureStorageFormat.Bc3)) return false;
        if (document.Info.Format == TextureStorageFormat.Bc3) return document.Encoding.Compression == CompressionFormat.Bc3;
        if (document.Encoding.Compression == CompressionFormat.Bc1WithAlpha) return true;
        if (document.Encoding.Compression != CompressionFormat.Bc1) return false;
        var blocks = document.OriginalBytes.Span.Slice(checked((int)extent.SourceOffset), extent.EncodedLength);
        for (int offset = 0; offset < blocks.Length; offset += 8)
        {
            if ((offset & 0xffff) == 0) token.ThrowIfCancellationRequested();
            if (BinaryPrimitives.ReadUInt16LittleEndian(blocks[offset..]) <=
                BinaryPrimitives.ReadUInt16LittleEndian(blocks[(offset + 2)..])) return false;
        }
        return true;
    }

    public static TextureSurface RestoreEncodedDds(int width, int height, DdsTextureDocument encodedDds,
        bool sourceIsHdr, bool isSrgb, int mipIndex, TextureStorageFormat sourceFormat,
        string sourceFormatName, string sourcePayloadSha256, TextureSubresourceSelection subresource,
        ResourceIdentity? identity, SourceRevision? revision, TextureSubresourceSelection? encodedSelection = null)
    {
        ArgumentNullException.ThrowIfNull(encodedDds); ArgumentNullException.ThrowIfNull(subresource);
        var selection = encodedSelection ?? encodedDds.DefaultSelection;
        var extent = encodedDds.Subresources.SingleOrDefault(item => item.Selection == selection);
        if (width is < 1 or > DdsTextureReader.MaximumDimension || height is < 1 or > DdsTextureReader.MaximumDimension ||
            (long)width * height > DdsTextureReader.MaximumPixelCount || extent is null || extent.Width != width || extent.Height != height ||
            sourceIsHdr != encodedDds.Info.IsHdr || isSrgb != encodedDds.Info.IsSrgb || sourceFormat != encodedDds.Info.Format || mipIndex < 0 ||
            subresource.MipIndex != mipIndex || subresource.ArrayLayer != selection.ArrayLayer || subresource.Face != selection.Face ||
            sourcePayloadSha256 is not { Length: 64 } || !sourcePayloadSha256.All(Uri.IsHexDigit) ||
            !CanRetainEncodedDds(encodedDds, selection) ||
            encodedDds.Revision is not null && encodedDds.Revision != revision ||
            encodedDds.ContentSha256 != Convert.ToHexString(SHA256.HashData(encodedDds.OriginalBytes.Span)).ToLowerInvariant())
            throw new TextureFormatException("InvalidPreparedSurface", "The prepared encoded texture surface is invalid.");
        return new(width, height, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<HdrPixel>.Empty, sourceIsHdr, sourceIsHdr, isSrgb,
            mipIndex, sourceFormat, sourceFormatName, sourcePayloadSha256, subresource, identity, revision)
            { EncodedDds = encodedDds, EncodedDdsSelection = selection };
    }

    public static TextureSurface RestoreRgba8(int width, int height, ReadOnlyMemory<byte> pixels,
        bool sourceIsHdr, bool isSrgb, int mipIndex, TextureStorageFormat sourceFormat,
        string sourceFormatName, string sourcePayloadSha256, TextureSubresourceSelection subresource,
        ResourceIdentity? identity, SourceRevision? revision, TextureDisplayTransform? displayTransform = null)
    {
        if (width is < 1 or > DdsTextureReader.MaximumDimension || height is < 1 or > DdsTextureReader.MaximumDimension ||
            (long)width * height > DdsTextureReader.MaximumPixelCount || pixels.Length != checked(width * height * 4) ||
            mipIndex < 0 || sourcePayloadSha256 is not { Length: 64 } || !sourcePayloadSha256.All(Uri.IsHexDigit))
            throw new TextureFormatException("InvalidPreparedSurface", "The prepared texture surface is invalid.");
        return new(width, height, pixels, ReadOnlyMemory<HdrPixel>.Empty, false, sourceIsHdr, isSrgb, mipIndex, sourceFormat,
            sourceFormatName, sourcePayloadSha256, subresource, identity, revision, displayTransform);
    }
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
    public bool HasRgba8 => Rgba8.Length == (long)Width * Height * 4;
    public DdsTextureDocument? EncodedDds { get; private init; }
    public TextureSubresourceSelection? EncodedDdsSelection { get; private init; }
    public ReadOnlyMemory<HdrPixel> HdrPixels { get; }
}

public sealed class TextureFormatException(string code, string message, Exception? inner = null)
    : IOException(message, inner)
{
    public string Code { get; } = code;
}
