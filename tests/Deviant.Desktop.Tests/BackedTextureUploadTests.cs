using System.Security.Cryptography;
using Deviant.Browser;
using Deviant.Formats.Textures;
using SharpDX.DXGI;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class BackedTextureUploadTests
{
    [Fact]
    public async Task UploadCompletionReleasesPixelsAndRecreationReacquiresExactBytes()
    {
        var source = new Pixels(); var surface = Surface(source); var budget = new AssetMemoryBudget();
        using var loader = await AssetViewportService.RawTextureLoader.CreateBackedAsync(surface, true, budget);
        Assert.Equal(1, source.Active); Assert.Equal(1, source.Acquisitions);
        var id = Guid.NewGuid(); var first = loader.Load(id);
        Assert.Equal(source.Bytes, first.TextureRaw); Assert.Equal(2, first.Width); Assert.Equal(2, first.Height);
        Assert.Equal(Format.R8G8B8A8_UNorm_SRgb, first.PixelFormat); Assert.False(first.GenerateMipMaps);
        loader.Complete(id, first, true); Assert.Equal(0, source.Active);
        var again = Guid.NewGuid(); var recreated = loader.Load(again);
        Assert.Equal(source.Bytes, recreated.TextureRaw); Assert.Equal(2, source.Acquisitions);
        Assert.Throws<InvalidOperationException>(() => loader.Complete(again, recreated, false));
        Assert.Equal(0, source.Active);
        loader.Dispose(); Assert.Equal(0, budget.UsedBytes);
        Assert.Throws<ObjectDisposedException>(() => loader.Load(Guid.NewGuid()));
    }

    [Fact]
    public async Task UnusedPrefetchAndCancelledCreationReleasePixels()
    {
        var source = new Pixels(); var budget = new AssetMemoryBudget();
        var loader = await AssetViewportService.RawTextureLoader.CreateBackedAsync(Surface(source), false, budget);
        Assert.Equal(1, source.Active); loader.Dispose(); loader.Dispose(); Assert.Equal(0, source.Active);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AssetViewportService.RawTextureLoader.CreateBackedAsync(Surface(source), false, budget, cancelled.Token));
        Assert.Equal(0, source.Active); Assert.Equal(0, budget.UsedBytes);
    }

    private static TextureSurface Surface(ITexturePixelSource pixels) => TextureSurface.RestoreBackedRgba8(2, 2, pixels,
        false, true, 0, TextureStorageFormat.Rgba8, "RGBA8", new string('a', 64), new(), null, null);
    private sealed class Pixels : ITexturePixelSource
    {
        public byte[] Bytes { get; } = [20, 40, 60, 0, 21, 41, 61, 64, 22, 42, 62, 128, 23, 43, 63, 255];
        public int Active, Acquisitions;
        public long ByteLength => Bytes.Length;
        public string RgbaSha256 => Convert.ToHexString(SHA256.HashData(Bytes));
        public ValueTask<TexturePixelLease> AcquireAsync(CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Active++; Acquisitions++; return ValueTask.FromResult(new TexturePixelLease(Bytes, new Release(() => Active--))); }
    }
    private sealed class Release(Action action) : IDisposable
    { private Action? release = action; public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke(); }
}
