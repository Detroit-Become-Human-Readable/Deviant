using System.Security.Cryptography;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TexturePixelSourceTests
{
    [Fact]
    public async Task BackedSurfaceKeepsMetadataAndAcquiresExactPixelsOnlyWhileLeased()
    {
        var bytes = Enumerable.Range(0, 64).Select(value => (byte)(value * 3)).ToArray();
        var source = new Pixels(bytes);
        var surface = Surface(source);
        Assert.Empty(surface.Rgba8.ToArray()); Assert.False(surface.HasRgba8); Assert.Equal(0, source.Acquired);
        Assert.True(surface.IsSrgb); Assert.Equal(3, surface.MipIndex); Assert.Equal(new TextureSubresourceSelection(MipIndex: 3), surface.Subresource);
        using (var lease = await surface.AcquireRgba8Async())
        {
            Assert.Equal(bytes, lease.Surface.Rgba8.ToArray()); Assert.Equal(1, source.Active);
            Assert.Equal(surface.IsSrgb, lease.Surface.IsSrgb); Assert.Equal(surface.SourcePayloadSha256, lease.Surface.SourcePayloadSha256);
            Assert.Equal(surface.Subresource, lease.Surface.Subresource);
        }
        Assert.Equal(0, source.Active); Assert.Empty(surface.Rgba8.ToArray());
        using var repeated = await surface.AcquireRgba8Async();
        Assert.Equal(2, source.Acquired);
    }

    [Fact]
    public async Task ChangedPixelsAndCancellationReleaseTheirReservations()
    {
        var source = new Pixels(new byte[64]); var surface = Surface(source);
        source.Bytes[0] = 17;
        await Assert.ThrowsAsync<TextureFormatException>(async () => { using var lease = await surface.AcquireRgba8Async(); });
        Assert.Equal(0, source.Active);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var lease = await surface.AcquireRgba8Async(cancellation.Token); });
        Assert.Equal(0, source.Active);
    }

    [Fact]
    public void BackingMustMatchDimensionsAndDigest()
    {
        Assert.Throws<TextureFormatException>(() => Surface(new Pixels(new byte[60])));
        Assert.Throws<TextureFormatException>(() => Surface(new Pixels(new byte[64], "bad")));
    }

    private static TextureSurface Surface(ITexturePixelSource source) => TextureSurface.RestoreBackedRgba8(4, 4, source,
        false, true, 3, TextureStorageFormat.Rgba8, "RGBA8", new string('a', 64), new(MipIndex: 3), null, null);

    private sealed class Pixels(byte[] bytes, string? hash = null) : ITexturePixelSource
    {
        public byte[] Bytes { get; } = bytes;
        public int Active, Acquired;
        public long ByteLength => Bytes.Length;
        public string RgbaSha256 { get; } = hash ?? Convert.ToHexString(SHA256.HashData(bytes));
        public ValueTask<TexturePixelLease> AcquireAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Acquired++; Active++;
            return ValueTask.FromResult(new TexturePixelLease(Bytes, new Release(() => Active--)));
        }
    }
    private sealed class Release(Action action) : IDisposable
    { private Action? release = action; public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke(); }
}
