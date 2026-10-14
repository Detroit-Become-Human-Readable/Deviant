using System.Buffers.Binary;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace Deviant.Formats.Textures;

/// <summary>Decodes validated DDS mip levels to WPF-independent pixels.</summary>
public sealed class DdsTextureDecoder
{
    public const long DefaultMaximumEstimatedWorkingSetBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumEstimatedWorkingSetSafetyLimit = 4L * 1024 * 1024 * 1024;
    private readonly long maximumEstimatedWorkingSetBytes;

    public DdsTextureDecoder(long maximumEstimatedWorkingSetBytes = DefaultMaximumEstimatedWorkingSetBytes)
    {
        if (maximumEstimatedWorkingSetBytes <= 0 || maximumEstimatedWorkingSetBytes > MaximumEstimatedWorkingSetSafetyLimit)
            throw new ArgumentOutOfRangeException(nameof(maximumEstimatedWorkingSetBytes),
                $"The decode budget must be between 1 and {MaximumEstimatedWorkingSetSafetyLimit:N0} bytes.");
        this.maximumEstimatedWorkingSetBytes = maximumEstimatedWorkingSetBytes;
    }

    public TextureSurface DecodeMip(DdsTextureDocument document, int mipIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(document);
        return DecodeSubresource(document, document.DefaultSelection with { MipIndex = mipIndex });
    }

    /// <summary>Returns a read-only pixel view backed by the document's owned, tightly packed RGBA bytes.</summary>
    public TextureSurface ViewRgbaSubresource(DdsTextureDocument document,
        TextureSubresourceSelection selection, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(selection);
        token.ThrowIfCancellationRequested();
        var extent = FindSubresource(document, selection);
        var encoding = document.Encoding;
        if (encoding.Compression is not null || encoding.IsHdr || encoding.AlphaOnly || encoding.Luminance ||
            encoding.BytesPerPixel != 4 || encoding.RedMask != 0xff || encoding.GreenMask != 0xff00 ||
            encoding.BlueMask != 0xff0000 || encoding.AlphaMask != 0xff000000 ||
            extent.EncodedLength != checked((long)extent.Width * extent.Height * 4))
            throw new TextureFormatException("UnsupportedRgbaView", "This DDS subresource requires pixel conversion.");
        var pixels = document.OriginalBytes.Slice(checked((int)extent.SourceOffset), extent.EncodedLength);
        return new(extent.Width, extent.Height, pixels, ReadOnlyMemory<HdrPixel>.Empty, false, false,
            document.Info.IsSrgb, selection.MipIndex, document.Info.Format, document.Info.FormatName,
            document.ContentSha256, selection, document.Identity, document.Revision);
    }

    public TextureSurface DecodeSubresource(DdsTextureDocument document, TextureSubresourceSelection selection)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selection);
        var extent = FindSubresource(document, selection);
        EnsureDecodeBudget(extent, document.Encoding);
        if (document.Encoding.BlockBytes > 0 && document.Encoding.Compression is null)
            throw new TextureFormatException("UnsupportedFormat", $"Pixel decoding for {document.Info.FormatName} is unavailable.");
        var level = new TextureMipLevel(selection.MipIndex, extent.Width, extent.Height, extent.SourceOffset, extent.EncodedLength);
        var bytes = document.OriginalBytes.Slice(checked((int)level.SourceOffset), level.EncodedLength);
        if (document.Encoding.Compression is { } compression)
        {
            try
            {
                var decoder = new BcDecoder();
                if (document.Encoding.IsHdr)
                {
                    var pixels = decoder.DecodeRawHdr(bytes.ToArray(), level.Width, level.Height, compression);
                    var hdr = new HdrPixel[pixels.Length];
                    for (var i = 0; i < pixels.Length; i++) hdr[i] = new(pixels[i].r, pixels[i].g, pixels[i].b);
                    return new(level.Width, level.Height, ReadOnlyMemory<byte>.Empty, hdr, true, true, false,
                        selection.MipIndex, document.Info.Format, document.Info.FormatName, document.ContentSha256,
                        selection, document.Identity, document.Revision);
                }

                var decoded = decoder.DecodeRaw(bytes.ToArray(), level.Width, level.Height, compression);
                var rgba = new byte[checked(level.Width * level.Height * 4)];
                if (decoded.Length != level.Width * level.Height)
                    throw new InvalidDataException("The BC decoder returned an unexpected pixel count.");
                for (var i = 0; i < decoded.Length; i++)
                {
                    var p = decoded[i];
                    var destination = i * 4;
                    rgba[destination] = p.r;
                    rgba[destination + 1] = p.g;
                    rgba[destination + 2] = p.b;
                    rgba[destination + 3] = p.a;
                }
                return new(level.Width, level.Height, rgba, ReadOnlyMemory<HdrPixel>.Empty, false, false,
                    document.Info.IsSrgb, selection.MipIndex, document.Info.Format, document.Info.FormatName,
                    document.ContentSha256, selection, document.Identity, document.Revision);
            }
            catch (Exception ex) when (ex is not TextureFormatException and not OperationCanceledException)
            {
                throw new TextureFormatException("DecodeFailed", "The BCn decoder could not decode this DDS mip.", ex);
            }
        }

        return DecodeUncompressed(document, level, selection, bytes.Span);
    }

    public Task<TextureSurface> DecodeMipAsync(DdsTextureDocument document, int mipIndex = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return DecodeSubresourceAsync(document, document.DefaultSelection with { MipIndex = mipIndex }, cancellationToken);
    }

    public async Task<TextureSurface> DecodeSubresourceAsync(DdsTextureDocument document,
        TextureSubresourceSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        var extent = FindSubresource(document, selection);
        EnsureDecodeBudget(extent, document.Encoding);
        if (document.Encoding.BlockBytes > 0 && document.Encoding.Compression is null)
            throw new TextureFormatException("UnsupportedFormat", $"Pixel decoding for {document.Info.FormatName} is unavailable.");
        var bytes = document.OriginalBytes.Slice(checked((int)extent.SourceOffset), extent.EncodedLength);
        if (document.Encoding.Compression is { } compression)
        {
            var decoder = new BcDecoder();
            try
            {
                if (document.Encoding.IsHdr)
                {
                    var decoded = await decoder.DecodeRawHdrAsync(bytes, compression, extent.Width, extent.Height, cancellationToken)
                        .ConfigureAwait(false);
                    var hdr = new HdrPixel[decoded.Length];
                    for (var i = 0; i < decoded.Length; i++)
                    {
                        if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                        hdr[i] = new(decoded[i].r, decoded[i].g, decoded[i].b);
                    }
                    return new(extent.Width, extent.Height, ReadOnlyMemory<byte>.Empty, hdr, true, true, false,
                        selection.MipIndex, document.Info.Format, document.Info.FormatName, document.ContentSha256,
                        selection, document.Identity, document.Revision);
                }

                var decodedPixels = await decoder.DecodeRawAsync(bytes, compression, extent.Width, extent.Height, cancellationToken)
                    .ConfigureAwait(false);
                var rgba = new byte[checked(extent.Width * extent.Height * 4)];
                if (decodedPixels.Length != extent.Width * extent.Height)
                    throw new InvalidDataException("The BC decoder returned an unexpected pixel count.");
                for (var i = 0; i < decodedPixels.Length; i++)
                {
                    if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var pixel = decodedPixels[i];
                    var destination = i * 4;
                    rgba[destination] = pixel.r;
                    rgba[destination + 1] = pixel.g;
                    rgba[destination + 2] = pixel.b;
                    rgba[destination + 3] = pixel.a;
                }
                return new(extent.Width, extent.Height, rgba, ReadOnlyMemory<HdrPixel>.Empty, false, false,
                    document.Info.IsSrgb, selection.MipIndex, document.Info.Format, document.Info.FormatName,
                    document.ContentSha256, selection, document.Identity, document.Revision);
            }
            catch (Exception ex) when (ex is not TextureFormatException and not OperationCanceledException)
            {
                throw new TextureFormatException("DecodeFailed", "The BCn decoder could not decode this DDS subresource.", ex);
            }
        }

        return DecodeUncompressed(document,
            new TextureMipLevel(selection.MipIndex, extent.Width, extent.Height, extent.SourceOffset, extent.EncodedLength),
            selection, bytes.Span, cancellationToken);
    }

    public TextureSurface CreateThumbnail(TextureSurface source, int maximumEdge = 256)
        => CreateThumbnail(source, maximumEdge, ignoreAlpha: false);

    public TextureSurface CreateThumbnail(TextureSurface source, int maximumEdge, bool ignoreAlpha)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.IsHdr) throw new TextureFormatException("HdrThumbnailRequiresToneMap", "HDR thumbnails require an explicit tone-mapping choice.");
        if (maximumEdge is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumEdge));
        if (!ignoreAlpha && source.Width <= maximumEdge && source.Height <= maximumEdge) return source;

        var scale = Math.Min(1, Math.Min(maximumEdge / (double)source.Width, maximumEdge / (double)source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale, MidpointRounding.AwayFromZero));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale, MidpointRounding.AwayFromZero));
        var sourcePixels = source.Rgba8.Span;
        var result = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var sourceX = Math.Min(source.Width - 1, (int)((x + 0.5) * source.Width / width));
            var sourceY = Math.Min(source.Height - 1, (int)((y + 0.5) * source.Height / height));
            sourcePixels.Slice((sourceY * source.Width + sourceX) * 4, 4).CopyTo(result.AsSpan((y * width + x) * 4, 4));
            if (ignoreAlpha) result[(y * width + x) * 4 + 3] = 255;
        }
        return new(width, height, result, ReadOnlyMemory<HdrPixel>.Empty, false, source.SourceIsHdr, source.IsSrgb,
            source.MipIndex, source.SourceFormat, source.SourceFormatName, source.SourcePayloadSha256, source.Subresource,
            source.Identity, source.Revision, source.DisplayTransform);
    }

    private static TextureSurface DecodeUncompressed(DdsTextureDocument document, TextureMipLevel level,
        TextureSubresourceSelection selection, ReadOnlySpan<byte> encoded, CancellationToken cancellationToken = default)
    {
        var format = document.Encoding;
        var pixels = new byte[checked(level.Width * level.Height * 4)];
        var bytesPerPixel = format.BytesPerPixel;
        for (var index = 0; index < level.Width * level.Height; index++)
        {
            if ((index & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var sourceOffset = index * bytesPerPixel;
            uint packed = bytesPerPixel switch
            {
                1 => encoded[sourceOffset],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(encoded.Slice(sourceOffset, 2)),
                3 => (uint)(encoded[sourceOffset] | encoded[sourceOffset + 1] << 8 | encoded[sourceOffset + 2] << 16),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(encoded.Slice(sourceOffset, 4)),
                _ => throw new TextureFormatException("UnsupportedPixelWidth", "The DDS pixel width is unsupported.")
            };

            var destinationOffset = index * 4;
            if (format.AlphaOnly)
            {
                pixels[destinationOffset] = 255;
                pixels[destinationOffset + 1] = 255;
                pixels[destinationOffset + 2] = 255;
                pixels[destinationOffset + 3] = Scale(packed, format.AlphaMask);
            }
            else if (format.Luminance)
            {
                var luminance = Scale(packed, format.RedMask);
                pixels[destinationOffset] = luminance;
                pixels[destinationOffset + 1] = luminance;
                pixels[destinationOffset + 2] = luminance;
                pixels[destinationOffset + 3] = 255;
            }
            else
            {
                pixels[destinationOffset] = Scale(packed, format.RedMask);
                pixels[destinationOffset + 1] = Scale(packed, format.GreenMask);
                pixels[destinationOffset + 2] = Scale(packed, format.BlueMask);
                pixels[destinationOffset + 3] = format.AlphaMask == 0 ? (byte)255 : Scale(packed, format.AlphaMask);
            }
        }
        return new(level.Width, level.Height, pixels, ReadOnlyMemory<HdrPixel>.Empty, false, false,
            document.Info.IsSrgb, selection.MipIndex, document.Info.Format, document.Info.FormatName,
            document.ContentSha256, selection, document.Identity, document.Revision);
    }

    private static TextureSubresourceExtent FindSubresource(DdsTextureDocument document, TextureSubresourceSelection selection) =>
        document.Subresources.FirstOrDefault(extent => extent.Selection == selection)
        ?? throw new TextureFormatException("SubresourceNotFound", "The DDS does not contain the selected array layer, face, and mip.");

    /// <summary>Estimates temporary decoded-pixel storage. Excludes the retained source DDS and caller-owned surfaces.</summary>
    public long EstimateWorkingSetBytes(DdsTextureDocument document, TextureSubresourceSelection selection)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selection);
        var extent = FindSubresource(document, selection);
        if (document.Encoding.BlockBytes > 0 && document.Encoding.Compression is null)
            throw new TextureFormatException("UnsupportedFormat", $"Pixel decoding for {document.Info.FormatName} is unavailable.");
        return EstimateWorkingSetBytes(extent, document.Encoding);
    }

    private void EnsureDecodeBudget(TextureSubresourceExtent extent, DdsTextureEncoding encoding)
    {
        var estimatedBytes = EstimateWorkingSetBytes(extent, encoding);
        if (estimatedBytes > maximumEstimatedWorkingSetBytes)
            throw new TextureFormatException("DecodeBudgetExceeded",
                $"The selected surface needs an estimated {estimatedBytes:N0} temporary pixel bytes, above the configured {maximumEstimatedWorkingSetBytes:N0}-byte limit.");
    }

    private static long EstimateWorkingSetBytes(TextureSubresourceExtent extent, DdsTextureEncoding encoding)
    {
        var pixels = checked((long)extent.Width * extent.Height);
        if (encoding.BlockBytes > 0 && encoding.Compression is null)
            throw new TextureFormatException("UnsupportedFormat", $"Pixel decoding for {encoding.FormatName} is unavailable.");
        var bytesPerPixel = encoding.IsHdr ? 24 : encoding.Compression is null ? 4 : 8;
        return checked(pixels * bytesPerPixel);
    }

    private static byte Scale(uint value, uint mask)
    {
        if (mask == 0) return 0;
        var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        var maximum = mask >> shift;
        var component = (value & mask) >> shift;
        return checked((byte)((component * 255UL + maximum / 2UL) / maximum));
    }
}
