using System.Security.Cryptography;

namespace Deviant.Formats.Textures;

public interface ITexturePixelSource
{
    long ByteLength { get; }
    string RgbaSha256 { get; }
    ValueTask<TexturePixelLease> AcquireAsync(CancellationToken token = default);
}

public sealed class TexturePixelLease : IDisposable
{
    private ReadOnlyMemory<byte> pixels;
    private IDisposable? reservation;
    public TexturePixelLease(ReadOnlyMemory<byte> pixels, IDisposable reservation)
    { this.pixels = pixels; this.reservation = reservation ?? throw new ArgumentNullException(nameof(reservation)); }
    public ReadOnlyMemory<byte> Rgba8 => reservation is not null ? pixels : throw new ObjectDisposedException(nameof(TexturePixelLease));
    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref reservation, null);
        pixels = ReadOnlyMemory<byte>.Empty;
        owned?.Dispose();
    }
}

public sealed class TextureSurfaceLease(TextureSurface surface, IDisposable? pixels = null) : IDisposable
{
    private IDisposable? owned = pixels;
    private TextureSurface? value = surface;
    public TextureSurface Surface => value ?? throw new ObjectDisposedException(nameof(TextureSurfaceLease));
    public void Dispose()
    { value = null; Interlocked.Exchange(ref owned, null)?.Dispose(); }
}

public sealed partial class TextureSurface
{
    public ITexturePixelSource? PixelSource { get; private init; }

    public static TextureSurface RestoreBackedRgba8(int width, int height, ITexturePixelSource pixels,
        bool sourceIsHdr, bool isSrgb, int mipIndex, TextureStorageFormat sourceFormat,
        string sourceFormatName, string sourcePayloadSha256, TextureSubresourceSelection subresource,
        Deviant.Assets.ResourceIdentity? identity, Deviant.Assets.SourceRevision? revision,
        TextureDisplayTransform? displayTransform = null)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width is < 1 or > DdsTextureReader.MaximumDimension || height is < 1 or > DdsTextureReader.MaximumDimension ||
            (long)width * height > DdsTextureReader.MaximumPixelCount || pixels.ByteLength != (long)width * height * 4 ||
            mipIndex < 0 || subresource is null || subresource.MipIndex != mipIndex ||
            pixels.RgbaSha256 is not { Length: 64 } || !pixels.RgbaSha256.All(Uri.IsHexDigit) ||
            sourcePayloadSha256 is not { Length: 64 } || !sourcePayloadSha256.All(Uri.IsHexDigit) ||
            !Enum.IsDefined(sourceFormat) || string.IsNullOrWhiteSpace(sourceFormatName))
            throw new TextureFormatException("InvalidPreparedSurface", "The prepared texture backing is invalid.");
        return new(width, height, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<HdrPixel>.Empty,
            false, sourceIsHdr, isSrgb, mipIndex, sourceFormat, sourceFormatName, sourcePayloadSha256,
            subresource, identity, revision, displayTransform) { PixelSource = pixels };
    }

    public async ValueTask<TextureSurfaceLease> AcquireRgba8Async(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (HasRgba8) return new(this);
        if (PixelSource is not { } source)
            throw new TextureFormatException("TexturePixelsUnavailable", "The texture pixel source is unavailable.");
        TexturePixelLease? pixels = await source.AcquireAsync(token).ConfigureAwait(false);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int offset = 0; offset < pixels.Rgba8.Length; offset += 65536)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(pixels.Rgba8.Span.Slice(offset, Math.Min(65536, pixels.Rgba8.Length - offset)));
            }
            if (pixels.Rgba8.Length != (long)Width * Height * 4 ||
                !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), source.RgbaSha256, StringComparison.OrdinalIgnoreCase))
                throw new TextureFormatException("PreparedTextureChanged", "The prepared texture pixels changed.");
            token.ThrowIfCancellationRequested();
            var surface = RestoreRgba8(Width, Height, pixels.Rgba8, SourceIsHdr, IsSrgb, MipIndex,
                SourceFormat, SourceFormatName, SourcePayloadSha256, Subresource, Identity, Revision, DisplayTransform);
            var result = new TextureSurfaceLease(surface, pixels); pixels = null;
            return result;
        }
        finally { pixels?.Dispose(); }
    }
}
