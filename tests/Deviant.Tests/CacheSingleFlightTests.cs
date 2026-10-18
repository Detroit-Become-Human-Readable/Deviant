using Deviant.Catalog;
using Xunit;

namespace Deviant.Tests;

public sealed class CacheSingleFlightTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-cache-flight-" + Guid.NewGuid().ToString("N"));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CacheManager Create(long limit = 1024) => new(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), limit));
    private static Task Produce(string path, CancellationToken token) => File.WriteAllBytesAsync(path, [1, 2, 3], token);
    private static async Task<bool> Validate(string path, CancellationToken token) =>
        (await File.ReadAllBytesAsync(path, token)).AsSpan().SequenceEqual(new byte[] { 1, 2, 3 });

    [Fact]
    public async Task DifferentKeysProduceConcurrentlyAndSameKeyPublishesOnce()
    {
        using var cache = Create();
        var entered = Signal(); var release = Signal(); int productions = 0;
        async Task First(string path, CancellationToken token)
        {
            Interlocked.Increment(ref productions); entered.SetResult();
            await release.Task.WaitAsync(token); await Produce(path, token);
        }
        var first = cache.AcquireAsync("shared", First);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var same = cache.AcquireAsync("shared", First);
        await using var other = await cache.AcquireAsync("independent", Produce).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(first.IsCompleted); Assert.False(same.IsCompleted);
        release.SetResult();
        await using var owner = await first;
        await using var follower = await same;
        Assert.Equal(1, productions); Assert.Equal(owner.Path, follower.Path); Assert.NotEqual(owner.Path, other.Path);
        Assert.Equal(6, cache.PreviewUsageBytes);
    }

    [Fact]
    public async Task ValidationForOneKeyDoesNotBlockAnotherProducer()
    {
        using var cache = Create();
        await using (var initial = await cache.AcquirePreparedAsync("geometry-v1", "one", ".bin", Produce, Validate)) { }
        var entered = Signal(); var release = Signal();
        async Task<bool> HeldValidation(string path, CancellationToken token)
        { entered.SetResult(); await release.Task.WaitAsync(token); return await Validate(path, token); }
        var first = cache.AcquirePreparedAsync("geometry-v1", "one", ".bin", (_, _) => throw new InvalidOperationException(), HeldValidation);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var other = await cache.AcquirePreparedAsync("geometry-v1", "two", ".bin", Produce, Validate).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(first.IsCompleted);
        release.SetResult();
        await using var validated = await first;
        Assert.NotEqual(validated.Path, other.Path);
    }

    [Fact]
    public async Task CanceledWaiterDoesNotCancelTheSingleFlightOwner()
    {
        using var cache = Create(); using var cancel = new CancellationTokenSource();
        var entered = Signal(); var release = Signal(); int productions = 0;
        async Task Owner(string path, CancellationToken token)
        { productions++; entered.SetResult(); await release.Task.WaitAsync(token); await Produce(path, token); }
        var owner = cache.AcquirePreparedAsync("geometry-v1", "one", ".bin", Owner, Validate);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiter = cache.AcquirePreparedAsync("geometry-v1", "one", ".bin", Owner, Validate, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(owner.IsCompleted);
        release.SetResult();
        await using var lease = await owner;
        Assert.Equal(1, productions); Assert.Equal(1, cache.ActivePreviewLeases);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(lease.Path));
    }

    [Fact]
    public async Task InvalidPreparedEntryCannotReplaceALeasedFileAndRebuildsAfterRelease()
    {
        using var cache = Create(); int productions = 0;
        Task Write(string path, CancellationToken token) { productions++; return Produce(path, token); }
        var pinned = await cache.AcquirePreparedAsync("geometry-v1", "one", ".bin", Write, Validate);
        await File.WriteAllBytesAsync(pinned.Path, [9, 9, 9]);
        await Assert.ThrowsAsync<IOException>(() => cache.AcquirePreparedAsync("geometry-v1", "one", ".bin", Write, Validate));
        Assert.Equal(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(pinned.Path));
        Assert.Equal(1, productions);
        await pinned.DisposeAsync();
        await using var repaired = await cache.AcquirePreparedAsync("geometry-v1", "one", ".bin", Write, Validate);
        Assert.Equal(2, productions); Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(repaired.Path));
        Assert.Equal(3, cache.PreparedUsageBytes);
    }

    [Fact]
    public async Task ClearWaitsForAnInFlightProducerAndItsResultLease()
    {
        using var cache = Create(); using var cancel = new CancellationTokenSource();
        var entered = Signal(); var release = Signal(); int blockedProducerCalls = 0;
        async Task Held(string path, CancellationToken token)
        { entered.SetResult(); await release.Task.WaitAsync(token); await Produce(path, token); }
        var acquiring = cache.AcquirePreparedAsync("geometry-v1", "one", ".bin", Held, Validate);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var clear = cache.ClearAsync();
        var blocked = cache.AcquireAsync("later", (path, token) => { blockedProducerCalls++; return Produce(path, token); }, cancel.Token);
        Assert.False(clear.IsCompleted); Assert.False(blocked.IsCompleted); Assert.Equal(0, blockedProducerCalls);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        release.SetResult();
        var lease = await acquiring;
        Assert.False(clear.IsCompleted); Assert.True(File.Exists(lease.Path));
        await lease.DisposeAsync(); await clear.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, cache.UsageBytes); Assert.False(File.Exists(lease.Path));
    }

    [Fact]
    public async Task CanceledMaintenanceReopensAdmissionAndKeepsExistingLeases()
    {
        using var cache = Create(); using var cancel = new CancellationTokenSource();
        await using var pinned = await cache.AcquireAsync("one", Produce);
        var clear = cache.ClearAsync(cancel.Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clear);
        await using var next = await cache.AcquireAsync("two", Produce).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(pinned.Path)); Assert.True(File.Exists(next.Path));
        Assert.Equal(2, cache.ActivePreviewLeases);
    }

    [Fact]
    public async Task PreparedArtifactsSharePreviewQuotaAndEvictionHonorsPins()
    {
        using var cache = Create(103);
        await File.WriteAllBytesAsync(Path.Combine(cache.Location, "catalog.bin"), new byte[100]);
        var preview = await cache.AcquireAsync("one", Produce);
        string previewPath = preview.Path;
        var fallback = await cache.AcquirePreparedAsync("geometry-v1", "one", ".bin", Produce, Validate);
        Assert.False(fallback.Path.StartsWith(cache.Location, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(previewPath)); Assert.Equal(3, cache.PreviewUsageBytes);
        Assert.Equal(0, cache.PreparedUsageBytes); Assert.Equal(100, cache.MetadataUsageBytes);
        await fallback.DisposeAsync(); Assert.False(File.Exists(fallback.Path));
        await preview.DisposeAsync();
        await using var prepared = await cache.AcquirePreparedAsync("geometry-v1", "one", ".bin", Produce, Validate);
        Assert.False(File.Exists(previewPath)); Assert.StartsWith(cache.Location, prepared.Path);
        Assert.Equal(0, cache.PreviewUsageBytes); Assert.Equal(3, cache.PreparedUsageBytes); Assert.Equal(103, cache.UsageBytes);
    }

    [Fact]
    public async Task PreparedKeysSeparateProfilesCategoriesContentOptionsAndExtensions()
    {
        using var cache = Create();
        string key = CacheManager.PreparedKey("geometry-v1", "layout-v1", "content", "lod0");
        Assert.NotEqual(key, CacheManager.PreparedKey("geometry-v1", "layout-v2", "content", "lod0"));
        Assert.NotEqual(key, CacheManager.PreparedKey("geometry-v1", "layout-v1", "other", "lod0"));
        Assert.NotEqual(key, CacheManager.PreparedKey("geometry-v1", "layout-v1", "content", "lod1"));
        await using var geometry = await cache.AcquirePreparedAsync("geometry-v1", key, ".bin", Produce, Validate);
        await using var material = await cache.AcquirePreparedAsync("material-v1", key, ".bin", Produce, Validate);
        await using var alternate = await cache.AcquirePreparedAsync("geometry-v1", key, ".data", Produce, Validate);
        Assert.NotEqual(geometry.Path, material.Path); Assert.NotEqual(geometry.Path, alternate.Path);
        Assert.Throws<ArgumentException>(() => { _ = cache.AcquirePreparedAsync("../geometry", key, ".bin", Produce, Validate); });
        Assert.Throws<ArgumentException>(() => { _ = cache.AcquirePreparedAsync("geometry-v1", key, "../bin", Produce, Validate); });
    }

    [Fact]
    public async Task AccountingUpdatesWithoutFullScansOnHitsAndReconcilesExternalFiles()
    {
        using var cache = Create();
        string cached;
        await using (var first = await cache.AcquirePreparedAsync("geometry-v1", "one", ".bin", Produce, Validate)) cached = first.Path;
        long reconciliations = cache.AccountingReconciliations;
        for (int i = 0; i < 20; i++)
        {
            await using var hit = await cache.AcquirePreparedAsync("geometry-v1", "one", ".bin", (_, _) => throw new InvalidOperationException(), Validate);
            Assert.Equal(3, cache.UsageBytes);
        }
        Assert.Equal(reconciliations, cache.AccountingReconciliations);
        string metadata = Path.Combine(cache.Location, "metadata", "index.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!); await File.WriteAllBytesAsync(metadata, new byte[7]);
        await cache.ReconcileAsync();
        Assert.Equal(10, cache.UsageBytes); Assert.Equal(7, cache.MetadataUsageBytes);
        File.Delete(cached); await cache.ReconcileAsync();
        Assert.Equal(7, cache.UsageBytes); Assert.Equal(0, cache.PreparedUsageBytes);
        await using var regenerated = await cache.AcquirePreparedAsync("geometry-v1", "one", ".bin", Produce, Validate);
        Assert.Equal(10, cache.UsageBytes); Assert.Equal(3, cache.PreparedUsageBytes);
    }

    [Fact]
    public async Task RelocationDrainsProductionAndKeepsPreparedArtifactIdentity()
    {
        using var cache = Create(); var entered = Signal(); var release = Signal();
        async Task Held(string path, CancellationToken token)
        { entered.SetResult(); await release.Task.WaitAsync(token); await Produce(path, token); }
        var acquiring = cache.AcquirePreparedAsync("geometry-v1", "one", ".bin", Held, Validate);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string destination = Path.Combine(root, "moved"); var moving = cache.RelocateAsync(destination);
        Assert.False(moving.IsCompleted);
        release.SetResult(); var lease = await acquiring;
        Assert.False(moving.IsCompleted);
        await lease.DisposeAsync(); await moving.WaitAsync(TimeSpan.FromSeconds(5));
        await using var copied = await cache.AcquirePreparedAsync("geometry-v1", "one", ".bin", (_, _) => throw new InvalidOperationException(), Validate);
        Assert.StartsWith(destination, copied.Path); Assert.True(File.Exists(lease.Path));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(copied.Path)); Assert.Equal(3, cache.PreparedUsageBytes);
    }

    [Fact]
    public async Task InvalidProducerAndCanceledProducerDoNotPublishOrRetainTemporaryFiles()
    {
        using var cache = Create();
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.AcquirePreparedAsync("geometry-v1", "invalid", ".bin",
            (path, token) => File.WriteAllBytesAsync(path, [9], token), Validate));
        using var cancel = new CancellationTokenSource(); var entered = Signal();
        async Task Held(string path, CancellationToken token)
        { await Produce(path, token); entered.SetResult(); await Task.Delay(Timeout.Infinite, token); }
        var acquiring = cache.AcquirePreparedAsync("geometry-v1", "canceled", ".bin", Held, Validate, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquiring);
        Assert.Equal(0, cache.UsageBytes); Assert.Equal(0, cache.ActivePreviewLeases);
        Assert.Empty(Directory.EnumerateFiles(cache.Location, "*", SearchOption.AllDirectories));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
