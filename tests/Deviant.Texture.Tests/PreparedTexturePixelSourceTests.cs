using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class PreparedTexturePixelSourceTests
{
    [Fact]
    public async Task RestoresExactPixelsAndMetadataWithOnlySmallRetainedBacking()
    {
        using var fixture = new Fixture();
        var (surface, bytes) = Surface(256, 128);
        var budget = new AssetMemoryBudget();
        using var source = await PreparedTexturePixelSource.StoreAsync(fixture.Cache, "surface", surface, Hash(bytes), budget, true);
        long retained = budget.UsedBytes;
        Assert.InRange(retained, 1, bytes.Length / 4);
        Assert.Equal(1, fixture.Cache.ActivePreviewLeases);
        Assert.False(source.Surface.HasRgba8);
        Assert.Empty(source.Surface.Rgba8.ToArray());
        Assert.Same(source, source.Surface.PixelSource);
        Assert.Equal(bytes.Length, source.ByteLength);
        Assert.Equal(Hash(bytes), source.RgbaSha256);
        Assert.True(source.PreviewWasResized);
        AssertMetadata(surface, source.Surface);

        for (var index = 0; index < 3; index++)
        {
            using (var pixels = await source.Surface.AcquireRgba8Async())
            {
                Assert.Equal(bytes, pixels.Surface.Rgba8.ToArray());
                AssertMetadata(surface, pixels.Surface);
                Assert.True(budget.UsedBytes >= retained + bytes.Length);
            }
            Assert.Equal(retained, budget.UsedBytes);
            Assert.False(source.Surface.HasRgba8);
        }
        source.Dispose();
        Assert.Equal(0, budget.UsedBytes);
        Assert.Equal(0, fixture.Cache.ActivePreviewLeases);
    }

    [Fact]
    public async Task WarmAcquisitionAndConcurrentStoresShareOneArtifact()
    {
        using var fixture = new Fixture();
        var (surface, bytes) = Surface(128, 128);
        var budget = new AssetMemoryBudget();
        var owners = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            PreparedTexturePixelSource.StoreAsync(fixture.Cache, "shared", surface, Hash(bytes), budget)));
        try
        {
            Assert.Single(Directory.GetFiles(fixture.Cache.Location, "*.pixels", SearchOption.AllDirectories));
            Assert.Equal(4, fixture.Cache.ActivePreviewLeases);
            using var warm = await PreparedTexturePixelSource.TryAcquireAsync(fixture.Cache, "shared", budget);
            Assert.NotNull(warm);
            using var first = await owners[0].AcquireAsync();
            using var restored = await warm.AcquireAsync();
            Assert.Equal(bytes, first.Rgba8.ToArray());
            Assert.Equal(bytes, restored.Rgba8.ToArray());
            AssertMetadata(surface, warm.Surface);
        }
        finally { foreach (var owner in owners) owner.Dispose(); }
        Assert.Equal(0, budget.UsedBytes);
        Assert.Equal(0, fixture.Cache.ActivePreviewLeases);
    }

    [Fact]
    public async Task ReadCanFinishAcrossOwnerDisposalAndEvictionWaitsForTheOwner()
    {
        using var fixture = new Fixture();
        var (surface, bytes) = Surface(1024, 1024);
        var budget = new AssetMemoryBudget();
        using var source = await PreparedTexturePixelSource.StoreAsync(fixture.Cache, "leased", surface, Hash(bytes), budget);
        string path = Assert.Single(Directory.GetFiles(fixture.Cache.Location, "*.pixels", SearchOption.AllDirectories));
        await fixture.Cache.SetLimitAsync(0);
        Assert.True(File.Exists(path));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = AssetLoadDiagnostics.Listener.Subscribe(new ReadBarrier(path, entered, release));
        var read = Task.Run(async () => await source.AcquireAsync());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.Dispose();
            Assert.Equal(1, fixture.Cache.ActivePreviewLeases);
            Assert.True(File.Exists(path));
        }
        finally { release.TrySetResult(); }
        using (var pixels = await read)
        {
            Assert.Equal(bytes, pixels.Rgba8.ToArray());
            Assert.Equal(0, fixture.Cache.ActivePreviewLeases);
            Assert.False(File.Exists(path));
            Assert.True(budget.UsedBytes >= bytes.Length);
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => { using var _ = await source.AcquireAsync(); });
        }
        source.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task CancellationAndBudgetFailureReleaseReservationsAndCacheLeases()
    {
        using var fixture = new Fixture();
        var (surface, bytes) = Surface(1024, 512);
        var budget = new AssetMemoryBudget(512 * 1024);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PreparedTexturePixelSource.StoreAsync(fixture.Cache, "canceled", surface, Hash(bytes), budget, token: canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PreparedTexturePixelSource.TryAcquireAsync(fixture.Cache, "missing", budget, canceled.Token));
        Assert.Equal(0, budget.UsedBytes);
        Assert.Equal(0, fixture.Cache.ActivePreviewLeases);

        using (var source = await PreparedTexturePixelSource.StoreAsync(fixture.Cache, "limited", surface, Hash(bytes), budget))
        {
            long retained = budget.UsedBytes;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var _ = await source.AcquireAsync(canceled.Token); });
            var error = await Assert.ThrowsAsync<ArchiveException>(async () => { using var _ = await source.AcquireAsync(); });
            Assert.Equal("AssetMemoryLimit", error.Code);
            Assert.Equal(retained, budget.UsedBytes);
        }
        Assert.Equal(0, budget.UsedBytes);
        Assert.Equal(0, fixture.Cache.ActivePreviewLeases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptPixelBytesOrHeaderAreRejectedWithoutLeaking(bool header)
    {
        using var fixture = new Fixture();
        var (surface, bytes) = Surface(32, 32);
        var budget = new AssetMemoryBudget();
        using (await PreparedTexturePixelSource.StoreAsync(fixture.Cache, "corrupt", surface, Hash(bytes), budget)) { }
        string path = Assert.Single(Directory.GetFiles(fixture.Cache.Location, "*.pixels", SearchOption.AllDirectories));
        using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            if (header)
            {
                file.Position = 12;
                var invalid = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(invalid, int.MaxValue);
                file.Write(invalid);
            }
            else
            {
                file.Position = file.Length - 33;
                int value = file.ReadByte(); file.Position--; file.WriteByte((byte)(value ^ 255));
            }
        }
        Assert.Null(await PreparedTexturePixelSource.TryAcquireAsync(fixture.Cache, "corrupt", budget));
        Assert.Equal(0, budget.UsedBytes);
        Assert.Equal(0, fixture.Cache.ActivePreviewLeases);
    }

    [Fact]
    public async Task StoreRejectsChangedHashesAndSurfacesThatAreNotResident()
    {
        using var fixture = new Fixture();
        var (surface, bytes) = Surface(16, 16);
        var budget = new AssetMemoryBudget();
        await Assert.ThrowsAsync<TextureFormatException>(() =>
            PreparedTexturePixelSource.StoreAsync(fixture.Cache, "wrong", surface, Hash([1, 2, 3]), budget));
        using (var source = await PreparedTexturePixelSource.StoreAsync(fixture.Cache, "resident", surface, Hash(bytes), budget))
            await Assert.ThrowsAsync<TextureFormatException>(() =>
                PreparedTexturePixelSource.StoreAsync(fixture.Cache, "backed", source.Surface, source.RgbaSha256, budget));
        Assert.Equal(0, budget.UsedBytes);
        Assert.Equal(0, fixture.Cache.ActivePreviewLeases);
    }

    [Fact]
    public async Task InlineRangePinsOnlyItsPixelsAndDiscardsTheFactory()
    {
        using var fixture = new Fixture();
        var (surface, bytes) = Surface(64, 64);
        var budget = new AssetMemoryBudget();
        string path = Path.Combine(fixture.Root, "scene.data");
        await File.WriteAllBytesAsync(path, [.. new byte[73], .. bytes, .. new byte[51]]);
        int calls = 0;
        using (var source = await PreparedTexturePixelSource.OpenRangeAsync(path, 73, bytes.Length, Hash(bytes), pixels =>
        {
            calls++;
            return TextureSurface.RestoreBackedRgba8(surface.Width, surface.Height, pixels, surface.SourceIsHdr,
                surface.IsSrgb, surface.MipIndex, surface.SourceFormat, surface.SourceFormatName,
                surface.SourcePayloadSha256, surface.Subresource, surface.Identity, surface.Revision, surface.DisplayTransform);
        }, budget, true))
        {
            Assert.Equal(1, calls);
            Assert.True(source.PreviewWasResized);
            AssertMetadata(surface, source.Surface);
            using (var pixels = await source.AcquireAsync()) Assert.Equal(bytes, pixels.Rgba8.ToArray());
            Assert.Equal(1, calls);
            Assert.False(source.Surface.HasRgba8);
        }
        Assert.Equal(0, budget.UsedBytes);
        await Assert.ThrowsAnyAsync<IOException>(() => PreparedTexturePixelSource.OpenRangeAsync(path, 74, bytes.Length,
            Hash(bytes), _ => throw new InvalidOperationException("An invalid range must not reach the factory."), budget));
        await Assert.ThrowsAsync<InvalidDataException>(() => PreparedTexturePixelSource.OpenRangeAsync(path, long.MaxValue, bytes.Length,
            Hash(bytes), _ => surface, budget));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PreparedTexturePixelSource.OpenRangeAsync(path, 73, bytes.Length,
            Hash(bytes), _ => throw new InvalidOperationException("Generated factory failure."), budget));
        Assert.Equal(0, budget.UsedBytes);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task RangeKeepsItsOuterOwnerUntilActiveReadHasClosedTheFile()
    {
        using var fixture = new Fixture();
        var (surface, bytes) = Surface(64, 64);
        var budget = new AssetMemoryBudget();
        string path = Path.Combine(fixture.Root, "owned.data");
        await File.WriteAllBytesAsync(path, bytes);
        int owners = 0;
        var source = await PreparedTexturePixelSource.OpenRangeAsync(path, 0, bytes.Length, Hash(bytes), pixels =>
            TextureSurface.RestoreBackedRgba8(surface.Width, surface.Height, pixels, surface.SourceIsHdr,
                surface.IsSrgb, surface.MipIndex, surface.SourceFormat, surface.SourceFormatName,
                surface.SourcePayloadSha256, surface.Subresource, surface.Identity, surface.Revision, surface.DisplayTransform),
            budget, acquireBackingOwner: () => { Interlocked.Increment(ref owners); return new Owner(() => Interlocked.Decrement(ref owners)); });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = AssetLoadDiagnostics.Listener.Subscribe(new ReadBarrier(path, entered, release));
        var read = Task.Run(async () => await source.AcquireAsync());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.Dispose();
            Assert.Equal(1, owners);
        }
        finally { release.TrySetResult(); }
        using (var pixels = await read)
        {
            Assert.Equal(bytes, pixels.Rgba8.ToArray()); Assert.Equal(0, owners);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    private sealed class Owner(Action release) : IDisposable
    { private Action? action = release; public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke(); }

    private static void AssertMetadata(TextureSurface expected, TextureSurface actual)
    {
        Assert.Equal(expected.Width, actual.Width); Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Identity, actual.Identity); Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.SourceIsHdr, actual.SourceIsHdr); Assert.Equal(expected.IsSrgb, actual.IsSrgb);
        Assert.Equal(expected.MipIndex, actual.MipIndex); Assert.Equal(expected.Subresource, actual.Subresource);
        Assert.Equal(expected.SourceFormat, actual.SourceFormat); Assert.Equal(expected.SourceFormatName, actual.SourceFormatName);
        Assert.Equal(expected.SourcePayloadSha256, actual.SourcePayloadSha256); Assert.Equal(expected.DisplayTransform, actual.DisplayTransform);
        Assert.False(actual.IsHdr);
    }

    private static (TextureSurface Surface, byte[] Bytes) Surface(int width, int height)
    {
        var bytes = Enumerable.Range(0, width * height * 4).Select(index => (byte)(index * 29 + 7)).ToArray();
        var identity = new ResourceIdentity("generated-index", 3, "entry/2");
        var surface = TextureSurface.RestoreRgba8(width, height, bytes, true, true, 2, TextureStorageFormat.Bc6Unsigned,
            "BC6H_UF16", Hash([9, 8, 7]), new(0, null, 2), identity, new(identity.IndexContentId, "generated-revision"),
            new(TextureToneMapOperator.Reinhard, 1, true));
        return (surface, bytes);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class ReadBarrier(string path, TaskCompletionSource entered, TaskCompletionSource release)
        : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is AssetLoadCounterMeasurement { Stage: "PreparedTexturePixelsReadStarted" } read && read.Key == path)
            { entered.TrySetResult(); release.Task.Wait(TimeSpan.FromSeconds(5)); }
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Deviant-test-" + Guid.NewGuid().ToString("N"));
        public CacheManager Cache { get; }
        public Fixture() => Cache = new(Path.Combine(Root, "settings"), new(Path.Combine(Root, "cache"), 32 * 1024 * 1024));
        public void Dispose() { Cache.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
