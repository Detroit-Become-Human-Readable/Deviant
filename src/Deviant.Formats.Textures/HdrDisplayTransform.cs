namespace Deviant.Formats.Textures;

/// <summary>Maps retained linear HDR pixels to an explicitly requested 8-bit display surface.</summary>
public static class HdrDisplayTransform
{
    public static TextureSurface ToSdr(TextureSurface hdrSurface, float exposureStops = 0,
        TextureToneMapOperator toneMap = TextureToneMapOperator.Reinhard,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hdrSurface);
        if (!hdrSurface.IsHdr) throw new TextureFormatException("NotHdr", "The selected surface is not floating-point HDR.");
        if (!float.IsFinite(exposureStops) || exposureStops is < -16 or > 16)
            throw new ArgumentOutOfRangeException(nameof(exposureStops), "Exposure must be finite and between -16 and +16 stops.");
        if (toneMap is not (TextureToneMapOperator.Reinhard or TextureToneMapOperator.Aces))
            throw new ArgumentOutOfRangeException(nameof(toneMap));

        var expectedPixels = checked(hdrSurface.Width * hdrSurface.Height);
        if (hdrSurface.HdrPixels.Length != expectedPixels)
            throw new TextureFormatException("InvalidPixelExtent", "The HDR pixel buffer does not match its dimensions.");
        cancellationToken.ThrowIfCancellationRequested();
        var exposure = MathF.Pow(2, exposureStops);
        var source = hdrSurface.HdrPixels.Span;
        var rgba = new byte[checked(expectedPixels * 4)];
        for (var index = 0; index < source.Length; index++)
        {
            if ((index & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var pixel = MapPixel(source[index], exposureStops, toneMap);
            var offset = index * 4;
            rgba[offset] = pixel.R;
            rgba[offset + 1] = pixel.G;
            rgba[offset + 2] = pixel.B;
            rgba[offset + 3] = pixel.A;
        }
        cancellationToken.ThrowIfCancellationRequested();

        return new(hdrSurface.Width, hdrSurface.Height, rgba, ReadOnlyMemory<HdrPixel>.Empty,
            false, true, true, hdrSurface.MipIndex, hdrSurface.SourceFormat, hdrSurface.SourceFormatName, hdrSurface.SourcePayloadSha256,
            hdrSurface.Subresource, hdrSurface.Identity, hdrSurface.Revision, new(toneMap, exposureStops, EncodedSrgb: true));
    }

    public static Rgba8Pixel MapPixel(HdrPixel pixel, float exposureStops = 0,
        TextureToneMapOperator toneMap = TextureToneMapOperator.Reinhard)
    {
        if (!float.IsFinite(exposureStops) || exposureStops is < -16 or > 16)
            throw new ArgumentOutOfRangeException(nameof(exposureStops), "Exposure must be finite and between -16 and +16 stops.");
        if (toneMap is not (TextureToneMapOperator.Reinhard or TextureToneMapOperator.Aces))
            throw new ArgumentOutOfRangeException(nameof(toneMap));
        var exposure = MathF.Pow(2, exposureStops);
        return new(EncodeSrgb(ToneMap(Expose(pixel.R, exposure), toneMap)),
            EncodeSrgb(ToneMap(Expose(pixel.G, exposure), toneMap)),
            EncodeSrgb(ToneMap(Expose(pixel.B, exposure), toneMap)), 255);
    }

    private static float Expose(float value, float exposure)
    {
        if (float.IsNaN(value) || value <= 0) return 0;
        if (float.IsPositiveInfinity(value)) return 1_000_000;
        return Math.Clamp(value * exposure, 0, 1_000_000);
    }

    private static float ToneMap(float value, TextureToneMapOperator toneMap) => toneMap switch
    {
        TextureToneMapOperator.Reinhard => value / (1 + value),
        TextureToneMapOperator.Aces => Math.Clamp((value * (2.51f * value + 0.03f)) /
            (value * (2.43f * value + 0.59f) + 0.14f), 0, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(toneMap))
    };

    private static byte EncodeSrgb(float linear)
    {
        var srgb = linear <= 0.0031308f ? 12.92f * linear : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
        return checked((byte)Math.Clamp((int)MathF.Round(srgb * 255, MidpointRounding.AwayFromZero), 0, 255));
    }
}
