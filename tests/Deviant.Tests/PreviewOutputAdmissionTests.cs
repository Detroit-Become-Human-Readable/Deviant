using Deviant.Catalog;
using Xunit;

namespace Deviant.Tests;

public sealed class PreviewOutputAdmissionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-preview-output-" + Guid.NewGuid().ToString("N"));
    private CacheManager Open(long limit) => new(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), limit));

    [Fact]
    public async Task PreviewUsesTheExistingKeyAndDoesNotRunAProducerOnHits()
    {
        using var cache = Open(1024 * 1024);
        var bytes = new byte[] { 1, 4, 8, 16 };
        using (var first = await cache.AcquireAsync("existing", (path, token) => File.WriteAllBytesAsync(path, bytes, token), default, ".png"))
        {
            using var hit = await cache.AcquirePreviewOutputAsync("existing", ".png", (_, _) => throw new InvalidOperationException("Cache hit ran the producer."));
            Assert.Equal(first.Path, hit.Path);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(hit.Path));
        }
        using var produced = await cache.AcquirePreviewOutputAsync("new", ".png", (stream, token) => stream.WriteAsync(bytes.AsMemory(), token).AsTask());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(produced.Path));
        Assert.Equal(bytes.Length * 2, cache.UsageBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    public async Task PreviewCannotEscapeItsStorageBudget(long limit)
    {
        using var cache = Open(limit);
        var error = await Assert.ThrowsAsync<IOException>(() => cache.AcquirePreviewOutputAsync("oversized", ".png",
            (stream, token) => stream.WriteAsync(new byte[256].AsMemory(), token).AsTask()));
        Assert.Equal("CacheStorageLimit", error.Data["ErrorCode"]);
        Assert.Equal(0, cache.ActivePreviewLeases);
        Assert.Equal(0, cache.UsageBytes);
        Assert.Empty(Directory.EnumerateFiles(cache.Location, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PreviewCancellationDoesNotPublishPartialBytes()
    {
        using var cache = Open(1024 * 1024);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.AcquirePreviewOutputAsync("cancelled", ".png", async (stream, token) =>
        {
            await stream.WriteAsync(new byte[32].AsMemory(), token);
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
        }, cancel.Token));
        Assert.Equal(0, cache.UsageBytes);
        Assert.Equal(0, cache.ActivePreviewLeases);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
