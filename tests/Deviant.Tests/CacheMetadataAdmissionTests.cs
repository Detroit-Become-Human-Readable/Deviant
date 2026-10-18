using Deviant.Catalog;
using Deviant.Core;
using System.Diagnostics;
using Xunit;

namespace Deviant.Tests;

public sealed class CacheMetadataAdmissionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-metadata-admission-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MetadataPublicationCannotGrowPastTheConfiguredWholeCacheLimit()
    {
        using var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 64L * 1024 * 1024));
        using var store = FeatureStore.Open(Snapshot(), cache.Location);
        var retained = new Metadata("Retained metadata");
        store.Save("retained", "v1", retained);
        Assert.Equal(retained, store.Load<Metadata>("retained", "v1"));
        await cache.ReconcileAsync();
        long baseline = cache.UsageBytes;
        Assert.True(baseline > 0);
        await cache.SetLimitAsync(baseline + 64 * 1024);
        Assert.True(cache.UsageBytes <= cache.LimitBytes);
        var incoming = new Metadata(new string('x', 1024 * 1024));

        Exception? rejection = Record.Exception(() => store.Save("incoming", "v1", incoming));

        await cache.ReconcileAsync();
        Assert.True(cache.UsageBytes <= cache.LimitBytes,
            $"Metadata publication exceeded the configured cache limit: {cache.UsageBytes} bytes used, {cache.LimitBytes} allowed.");
        if (rejection is null) Assert.Equal(incoming, store.Load<Metadata>("incoming", "v1"));
        else
        {
            Assert.IsAssignableFrom<IOException>(rejection);
            Assert.Equal("CacheStorageLimit", rejection.Data["ErrorCode"]);
            Assert.Equal(cache.LimitBytes, rejection.Data["LimitBytes"]);
            Assert.Null(store.Load<Metadata>("incoming", "v1"));
        }
        Assert.Equal(retained, store.Load<Metadata>("retained", "v1"));
    }

    [Fact]
    public async Task ConcurrentDatabasesCannotEachSpendTheSameRemainingCapacity()
    {
        using var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 64L * 1024 * 1024));
        using var first = FeatureStore.Open(Snapshot(), cache.Location);
        using var second = FeatureStore.Open(Snapshot() with { IndexContentId = "generated-second-index" }, cache.Location);
        first.Save("retained", "v1", new Metadata("First"));
        second.Save("retained", "v1", new Metadata("Second"));
        await cache.ReconcileAsync();
        await cache.SetLimitAsync(cache.UsageBytes + 2 * 1024 * 1024);
        var incoming = new Metadata(new string('x', 1280 * 1024));
        var results = await Task.WhenAll(new[] { first, second }.Select(store => Task.Run(() =>
            Record.Exception(() => store.Save("incoming", "v1", incoming)))));
        Assert.Contains(results, result => result is IOException);
        Assert.All(results.Where(result => result is not null), result => Assert.IsAssignableFrom<IOException>(result));
        Assert.Equal("First", first.Load<Metadata>("retained", "v1")!.Value);
        Assert.Equal("Second", second.Load<Metadata>("retained", "v1")!.Value);
        await cache.ReconcileAsync();
        Assert.True(cache.UsageBytes <= cache.LimitBytes);
    }

    [Fact]
    public async Task CancelledCollectionPublicationPreservesCommittedRowsAndStorageAccounting()
    {
        using var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 64L * 1024 * 1024));
        using var store = FeatureStore.Open(Snapshot(), cache.Location);
        var retained = new Metadata("Retained");
        store.SaveItems("collection", "v1", new[] { retained });
        using var cancellation = new CancellationTokenSource();
        IEnumerable<Metadata> Incoming()
        {
            yield return new(new string('x', 256 * 1024));
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        }
        Assert.ThrowsAny<OperationCanceledException>(() => store.SaveItems("collection", "v1", Incoming(), cancellation.Token));
        Assert.Equal(retained, Assert.Single(store.LoadItems<Metadata>("collection", "v1")));
        await cache.ReconcileAsync();
        await cache.SetLimitAsync(cache.UsageBytes + 64 * 1024);
        Assert.ThrowsAny<IOException>(() => store.Save("incoming", "v1", new Metadata(new string('x', 1024 * 1024))));
        await cache.ReconcileAsync();
        Assert.True(cache.UsageBytes <= cache.LimitBytes);
    }

    [Theory]
    [InlineData("bounded")]
    [InlineData("items")]
    [InlineData("linked")]
    public async Task CollectionAndBoundedWritesRollBackWhenStorageIsFull(string kind)
    {
        using var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 64L * 1024 * 1024));
        using var store = FeatureStore.Open(Snapshot(), cache.Location);
        var retained = new Metadata("Retained metadata");
        store.Save("retained", "v1", retained);
        store.SaveItems("collection", "v1", new[] { retained });
        await cache.ReconcileAsync();
        await cache.SetLimitAsync(cache.UsageBytes + 64 * 1024);
        var incoming = new Metadata(new string('x', 1024 * 1024));
        Assert.ThrowsAny<IOException>(() =>
        {
            if (kind == "bounded") store.SaveBounded("incoming", "v1", incoming, 8 * 1024 * 1024);
            else if (kind == "items") store.SaveItems("collection", "v1", new[] { incoming });
            else store.SaveLinkedFeatureItems(new("generated", "v1", "collection", "v1", "generated"), new[] { incoming });
        });
        Assert.Equal(retained, store.Load<Metadata>("retained", "v1"));
        Assert.Equal(retained, Assert.Single(store.LoadItems<Metadata>("collection", "v1")));
        Assert.Null(store.Load<Metadata>("incoming", "v1"));
        await cache.ReconcileAsync();
        Assert.True(cache.UsageBytes <= cache.LimitBytes);
    }

    [Fact]
    public async Task SeparateManagersAndStoresShareTheRestrictiveLimit()
    {
        var location = Path.Combine(root, "cache");
        using var first = new CacheManager(Path.Combine(root, "first-settings"), new(location, 64L * 1024 * 1024));
        using var second = new CacheManager(Path.Combine(root, "second-settings"), new(location, 64L * 1024 * 1024));
        using var firstStore = FeatureStore.Open(Snapshot(), location);
        using var secondStore = FeatureStore.Open(Snapshot(), location);
        firstStore.Save("retained", "v1", new Metadata("Retained metadata"));
        await first.ReconcileAsync();
        await first.SetLimitAsync(first.UsageBytes + 64 * 1024);
        Assert.ThrowsAny<IOException>(() => secondStore.Save("incoming", "v1", new Metadata(new string('x', 1024 * 1024))));
        Assert.Null(firstStore.Load<Metadata>("incoming", "v1"));
        Assert.Equal("Retained metadata", secondStore.Load<Metadata>("retained", "v1")!.Value);
        await first.ReconcileAsync();
        Assert.True(first.UsageBytes <= first.LimitBytes);
    }

    [Fact]
    public async Task MetadataAdmissionIncludesNewlyPublishedPreparedAssets()
    {
        using var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 64L * 1024 * 1024));
        using var store = FeatureStore.Open(Snapshot(), cache.Location);
        store.Save("retained", "v1", new Metadata("Retained"));
        await cache.ReconcileAsync();
        await cache.SetLimitAsync(cache.UsageBytes + 2 * 1024 * 1024);
        await using var artifact = await cache.AcquirePreparedAsync("generated", "pinned", ".bin",
            (path, token) => File.WriteAllBytesAsync(path, new byte[1024 * 1024], token),
            (_, _) => Task.FromResult(true));
        Assert.True(DirectoryBoundary.IsWithin(artifact.Path, cache.Location));
        Assert.ThrowsAny<IOException>(() => store.Save("incoming", "v1", new Metadata(new string('x', 1536 * 1024))));
        Assert.Null(store.Load<Metadata>("incoming", "v1"));
        Assert.True(File.Exists(artifact.Path));
        await cache.ReconcileAsync();
        Assert.True(cache.UsageBytes <= cache.LimitBytes);
    }

    [Fact]
    public void RawIndexOpeningRemainsUsableWhenNoPersistentGrowthFits()
    {
        using var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 1));
        var snapshot = Snapshot() with { Records = [new ResourceRecord(0, 42, 0, 7, 0, 0, 1, 0, "generated.dat")] };
        using var catalog = CatalogService.Open(snapshot, cache.Location);
        Assert.Equal(1, catalog.Count(new()));
        Assert.Equal(snapshot.Records, catalog.Query(new()));
        Assert.True(cache.UsageBytes <= cache.LimitBytes);
    }

    [Fact]
    public async Task ExistingOverBudgetMetadataCanStillBeReadAfterReopening()
    {
        using var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 64L * 1024 * 1024));
        var retained = new Metadata(new string('r', 512 * 1024));
        using (var store = FeatureStore.Open(Snapshot(), cache.Location)) store.Save("retained", "v1", retained);
        await cache.SetLimitAsync(32 * 1024);
        using var reopened = FeatureStore.Open(Snapshot(), cache.Location);
        Assert.Equal(retained, reopened.Load<Metadata>("retained", "v1"));
        Assert.ThrowsAny<IOException>(() => reopened.Save("incoming", "v1", new Metadata(new string('x', 1024 * 1024))));
        Assert.Equal(retained, reopened.Load<Metadata>("retained", "v1"));
    }

    [Fact]
    public void ZeroLimitKeepsMetadataInMemory()
    {
        using var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 0));
        using (var store = FeatureStore.Open(Snapshot(), cache.Location))
        {
            store.Save("generated", "v1", new Metadata("Transient"));
            Assert.Equal("Transient", store.Load<Metadata>("generated", "v1")!.Value);
        }
        using var reopened = FeatureStore.Open(Snapshot(), cache.Location);
        Assert.Null(reopened.Load<Metadata>("generated", "v1"));
        Assert.Empty(Directory.EnumerateFiles(cache.Location, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DeferredCloseCheckpointKeepsCommittedMetadataAfterReopen()
    {
        using var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 64L * 1024 * 1024));
        var retained = new Metadata("Committed metadata");
        using (var store = FeatureStore.Open(Snapshot(), cache.Location))
        {
            store.Save("retained", "v1", retained);
            await cache.ReconcileAsync();
            await cache.SetLimitAsync(cache.UsageBytes + 64 * 1024);
            Assert.ThrowsAny<IOException>(() => store.Save("incoming", "v1", new Metadata(new string('x', 1024 * 1024))));
        }
        using var reopened = FeatureStore.Open(Snapshot(), cache.Location);
        Assert.Equal(retained, reopened.Load<Metadata>("retained", "v1"));
        Assert.Null(reopened.Load<Metadata>("incoming", "v1"));
        await cache.ReconcileAsync();
        Assert.True(cache.UsageBytes <= cache.LimitBytes);
    }

    [Fact]
    public void RecreatedNamedSidecarReservesItsFullSizeDespiteAStaleObservation()
    {
        var location = Path.Combine(root, "cache");
        using var cache = new CacheManager(Path.Combine(root, "settings"), new(location, 64 * 1024));
        var budget = CacheStorageBudget.ForRoot(location);
        var sidecar = Path.Combine(location, "generated-shm");
        File.WriteAllBytes(sidecar, new byte[32 * 1024]);
        budget.Reconcile();
        using (var deletion = budget.Acquire(sidecar, 32 * 1024, 32 * 1024))
        {
            Assert.NotNull(deletion);
            File.Delete(sidecar); deletion.Complete(0);
        }
        using (var recreation = budget.Acquire(sidecar, 32 * 1024, 32 * 1024))
        {
            Assert.NotNull(recreation);
            File.WriteAllBytes(sidecar, new byte[32 * 1024]); recreation.Complete(32 * 1024);
        }
        using var denied = budget.Acquire(Path.Combine(location, "generated.data"), 0, 40 * 1024);
        Assert.Null(denied);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("update")]
    [InlineData("remove")]
    public async Task FailedPolicyPublicationCanBeRetriedWithoutLeavingARestrictiveOwner(string action)
    {
        const long permissive = 64L * 1024 * 1024;
        using var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), action == "remove" ? 64 * 1024 : permissive));
        using var other = new CacheManager(Path.Combine(root, "other-settings"), new(cache.Location, permissive));
        var budget = CacheStorageBudget.ForRoot(cache.Location);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocker = Task.Run(() =>
        {
            using var barrier = budget.Acquire(Path.Combine(cache.Location, "barrier.data"), 0, 0);
            Assert.NotNull(barrier);
            held.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            barrier.Complete(0);
        });
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
            Assert.ThrowsAny<IOException>(() =>
            {
                if (action == "add") { using var attempt = new CacheManager(Path.Combine(root, "failed-settings"), new(cache.Location, 1)); }
                else if (action == "update") cache.SetLimitAsync(1).GetAwaiter().GetResult();
                else cache.Dispose();
            });
        }
        finally { release.Set(); }
        await blocker.WaitAsync(TimeSpan.FromSeconds(5));
        if (action == "remove") cache.Dispose();
        else
        {
            using var joined = new CacheManager(Path.Combine(root, "joined-settings"), new(cache.Location, permissive));
            Assert.Equal(permissive, budget.Limit);
        }
        Assert.Equal(permissive, budget.Limit);
    }

    [Fact]
    public async Task AnotherProcessInvalidatesCachedFileLengthsBeforeRecreationAdmission()
    {
        using var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 256 * 1024));
        var ready = Path.Combine(root, "ready");
        var trigger = Path.Combine(root, "trigger");
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "fixtures", "CachePolicyWorker.dll"));
        foreach (var value in new[] { AppContext.BaseDirectory, root, "262144", ready, trigger, "swap" }) info.ArgumentList.Add(value);
        using var worker = Process.Start(info)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        async Task WaitFor(string path)
        {
            while (!File.Exists(path) && !worker.HasExited) await Task.Delay(20, timeout.Token);
            Assert.True(File.Exists(path), worker.HasExited ? await worker.StandardError.ReadToEndAsync(timeout.Token) : "Policy worker timed out.");
        }
        try
        {
            await WaitFor(ready);
            var first = Path.Combine(cache.Location, "first.data");
            var budget = CacheStorageBudget.ForRoot(cache.Location);
            using (var creation = budget.Acquire(first, 0, 192 * 1024))
            {
                Assert.NotNull(creation);
                File.WriteAllBytes(first, new byte[192 * 1024]); creation.Complete(192 * 1024);
            }
            await File.WriteAllTextAsync(trigger, "swap", timeout.Token);
            await WaitFor(ready + ".swapped");
            using var rejected = budget.Acquire(first, 192 * 1024, 192 * 1024);
            Assert.Null(rejected);
            Assert.False(File.Exists(first));
            Assert.Equal(192 * 1024, new FileInfo(Path.Combine(cache.Location, "second.data")).Length);
        }
        finally
        {
            await File.WriteAllTextAsync(trigger + ".exit", "stop");
            if (!worker.HasExited)
            {
                try { await worker.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(); }
            }
        }
    }

    [Fact]
    public async Task SurvivingProcessRecoversAfterTheRestrictiveOwnerExits()
    {
        using var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 64L * 1024 * 1024));
        using var store = FeatureStore.Open(Snapshot(), cache.Location);
        store.Save("retained", "v1", new Metadata("Retained"));
        await cache.ReconcileAsync();
        var ready = Path.Combine(root, "ready");
        var stop = Path.Combine(root, "stop");
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "fixtures", "CachePolicyWorker.dll"));
        foreach (var value in new[] { AppContext.BaseDirectory, root,
            (cache.UsageBytes + 64 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture), ready, stop }) info.ArgumentList.Add(value);
        using var worker = Process.Start(info)!;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(ready) && !worker.HasExited) await Task.Delay(20, timeout.Token);
            var error = worker.HasExited ? await worker.StandardError.ReadToEndAsync(timeout.Token) : "Policy worker did not become ready.";
            Assert.True(File.Exists(ready), error);
            Assert.Equal(Path.GetFullPath(typeof(CacheManager).Assembly.Location), Path.GetFullPath(await File.ReadAllTextAsync(ready, timeout.Token)));
            var incoming = new Metadata(new string('x', 1024 * 1024));
            Assert.ThrowsAny<IOException>(() => store.Save("incoming", "v1", incoming));
            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync(timeout.Token);
            store.Save("incoming", "v1", incoming);
            Assert.Equal(incoming, store.Load<Metadata>("incoming", "v1"));
        }
        finally
        {
            if (!worker.HasExited) { worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(); }
        }
    }

    private ArchiveSnapshot Snapshot() => new(Path.Combine(root, "source", "generated.idx"), "generated-metadata-index",
        IndexLayout.SupportedVersion, 0, [0x41], 0, [], [], new FileStamp(true, "index", 1, 1, 1, true), "generated-metadata-stamp", []);

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed record Metadata(string Value);
}
