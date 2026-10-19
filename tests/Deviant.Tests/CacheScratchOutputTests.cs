using System.Reflection;
using Deviant.Catalog;
using Deviant.Core;
using Xunit;

namespace Deviant.Tests;

public sealed class CacheScratchOutputTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-scratch-output-" + Guid.NewGuid().ToString("N"));
    private string Location => Path.Combine(root, "cache");
    private CacheManager Manager(string name = "settings", long limit = 1024 * 1024) => new(Path.Combine(root, name), new(Location, limit));
    private static Task<bool> Valid(string path, CancellationToken token) => Task.FromResult(new FileInfo(path).Length > 0);
    private long PhysicalBytes() => Directory.Exists(Location) ? Directory.EnumerateFiles(Location, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) : 0;

    [Theory]
    [InlineData(".bin")]
    [InlineData(".tmp")]
    public async Task ScratchIsUniqueReadableAccountedAndRemovedWhenItsConsumerReleasesIt(string extension)
    {
        using var cache = Manager();
        var expected = Enumerable.Range(0, 3000).Select(value => (byte)value).ToArray();
        string firstPath;
        await using (var lease = await cache.AcquireScratchOutputAsync(extension, async (output, token) =>
        {
            Assert.True(output.CanRead && output.CanWrite && output.CanSeek);
            await output.WriteAsync(expected, token);
        }, async (path, token) => (await File.ReadAllBytesAsync(path, token)).SequenceEqual(expected)))
        {
            firstPath = lease.Path;
            Assert.StartsWith(cache.Location + Path.DirectorySeparatorChar, lease.Path);
            Assert.Equal(expected, await File.ReadAllBytesAsync(lease.Path));
            Assert.Equal(1, cache.ActivePreviewLeases);
            Assert.Equal(expected.Length, cache.UsageBytes);
            Assert.Equal(expected.Length, PhysicalBytes());
            await cache.ReconcileAsync();
            Assert.Equal(expected.Length, cache.UsageBytes);
        }
        Assert.False(File.Exists(firstPath));
        Assert.Equal(0, cache.ActivePreviewLeases);
        Assert.Equal(0, cache.UsageBytes);
        await using var second = await cache.AcquireScratchOutputAsync(extension, (output, token) => output.WriteAsync(expected, token).AsTask(), Valid);
        Assert.NotEqual(firstPath, second.Path);
    }

    [Fact]
    public async Task OwnedScratchTemporaryExtensionIsVisibleToTheFileAccountingPredicate()
    {
        using var cache = Manager();
        var directory = Path.Combine(cache.Location, "scratch-v1");
        Directory.CreateDirectory(directory);
        var output = new CacheArtifactOutput(cache.Location, Path.Combine(directory, "generated.tmp"));
        await using (var stream = output.OpenWrite())
        {
            await stream.WriteAsync(new byte[2048]);
            await cache.ReconcileAsync();
            Assert.Equal(2048, cache.UsageBytes);
            Assert.Equal(2048, cache.MetadataUsageBytes);
        }
    }

    [Fact]
    public async Task MetadataAndScratchShareTheSameAdmissionLimit()
    {
        using var cache = Manager(limit: 64 * 1024 * 1024);
        using var store = FeatureStore.Open(Snapshot(), cache.Location);
        store.Save("retained", "v1", new SavedMetadata("Generated retained metadata"));
        await cache.ReconcileAsync();
        var retained = cache.UsageBytes;
        Assert.True(retained > 0);
        await cache.SetLimitAsync(retained + 512 * 1024);

        var error = await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using var lease = await cache.AcquireScratchOutputAsync(".bin", async (output, token) =>
            {
                for (var index = 0; index < 8; index++)
                {
                    await output.WriteAsync(new byte[128 * 1024], token);
                    Assert.True(PhysicalBytes() <= cache.LimitBytes);
                }
            }, Valid);
        });

        Assert.Equal("CacheStorageLimit", error.Data["ErrorCode"]);
        Assert.Equal("Generated retained metadata", store.Load<SavedMetadata>("retained", "v1")!.Value);
        Assert.Equal(0, cache.ActivePreviewLeases);
        await cache.ReconcileAsync();
        Assert.Equal(retained, cache.UsageBytes);
        Assert.Equal(retained, PhysicalBytes());
    }

    [Fact]
    public async Task ConcurrentScratchOutputsCannotSpendTheSameRemainingCapacity()
    {
        using var cache = Manager(limit: 256 * 1024);
        var entered = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(CacheLease? Lease, Exception? Error)> Produce()
        {
            try
            {
                var lease = await cache.AcquireScratchOutputAsync(".wav", async (output, token) =>
                {
                    if (Interlocked.Increment(ref entered) == 2) both.TrySetResult();
                    await both.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                    await output.WriteAsync(new byte[150 * 1024], token);
                }, Valid);
                return (lease, null);
            }
            catch (Exception error) { return (null, error); }
        }
        var results = await Task.WhenAll(Produce(), Produce());
        try
        {
            var winner = Assert.Single(results, value => value.Lease is not null).Lease!;
            var failure = Assert.Single(results, value => value.Error is not null).Error!;
            Assert.IsType<IOException>(failure);
            Assert.Equal("CacheStorageLimit", failure.Data["ErrorCode"]);
            Assert.Equal(150 * 1024, new FileInfo(winner.Path).Length);
            Assert.Equal(150 * 1024, PhysicalBytes());
            Assert.Equal(1, cache.ActivePreviewLeases);
        }
        finally { foreach (var value in results) if (value.Lease is not null) await value.Lease.DisposeAsync(); }
        Assert.Equal(0, PhysicalBytes());
        Assert.Equal(0, cache.UsageBytes);
    }

    [Theory]
    [InlineData("producer")]
    [InlineData("validation")]
    [InlineData("cancel")]
    public async Task FailedProductionValidationOrCancellationRemovesPartialScratch(string mode)
    {
        using var cache = Manager();
        using var cancellation = new CancellationTokenSource();
        var expected = new IOException("Generated producer failure");
        expected.Data["ErrorCode"] = "GeneratedProducer";
        var error = await Record.ExceptionAsync(async () =>
        {
            await using var lease = await cache.AcquireScratchOutputAsync(".bin", async (output, token) =>
            {
                await output.WriteAsync(new byte[4096], token);
                if (mode == "producer") throw expected;
                if (mode == "cancel") cancellation.Cancel(); // Deliberately return normally; publication must check the token.
            }, (_, _) => Task.FromResult(mode != "validation"), cancellation.Token);
        });
        Assert.NotNull(error);
        if (mode == "producer") { Assert.Same(expected, error); Assert.Equal("GeneratedProducer", error.Data["ErrorCode"]); }
        else if (mode == "validation") Assert.IsType<InvalidDataException>(error);
        else Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(0, cache.ActivePreviewLeases);
        Assert.Equal(0, PhysicalBytes());
        Assert.Equal(0, cache.UsageBytes);
        await cache.ClearAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScratchConsumerBlocksClearOrRelocationUntilDeletionCompletes(bool relocate)
    {
        using var cache = Manager();
        var lease = await cache.AcquireScratchOutputAsync(".wem", (output, token) => output.WriteAsync(new byte[4096], token).AsTask(), Valid);
        var original = lease.Path;
        var destination = Path.Combine(root, "relocated");
        var maintenance = relocate ? cache.RelocateAsync(destination) : cache.ClearAsync();
        Assert.False(maintenance.IsCompleted);
        Assert.True(File.Exists(original));
        using var cancellation = new CancellationTokenSource();
        var called = false;
        var waiting = cache.AcquireScratchOutputAsync(".bin", (_, _) => { called = true; return Task.CompletedTask; }, Valid, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(called);
        await lease.DisposeAsync();
        await maintenance.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(original));
        Assert.Equal(0, cache.ActivePreviewLeases);
        Assert.Equal(0, cache.UsageBytes);
        Assert.Empty(Directory.EnumerateFiles(cache.Location, "*", SearchOption.AllDirectories));
        if (relocate) Assert.Equal(Path.GetFullPath(destination), cache.Location);
    }

    [Fact]
    public async Task ProductionKeepsMaintenanceWaitingBeforeConsumerPublication()
    {
        using var cache = Manager();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquiring = cache.AcquireScratchOutputAsync(".ogg", async (output, token) =>
        {
            await output.WriteAsync(new byte[100], token);
            entered.TrySetResult();
            await finish.Task.WaitAsync(token);
        }, Valid);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var clearing = cache.ClearAsync();
        Assert.False(clearing.IsCompleted);
        finish.TrySetResult();
        var lease = await acquiring.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(clearing.IsCompleted);
        await lease.DisposeAsync();
        await clearing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, PhysicalBytes());
    }

    [Fact]
    public async Task SameRootManagersShareCapacityAndReleaseAccounting()
    {
        using var first = Manager("permissive", 1024 * 1024);
        using var restrictive = Manager("restrictive", 256 * 1024);
        var lease = await first.AcquireScratchOutputAsync(".bin", (output, token) => output.WriteAsync(new byte[128 * 1024], token).AsTask(), Valid);
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(async () =>
            {
                await using var blocked = await restrictive.AcquireScratchOutputAsync(".bin",
                    (output, token) => output.WriteAsync(new byte[128 * 1024], token).AsTask(), Valid);
            });
            Assert.Equal("CacheStorageLimit", error.Data["ErrorCode"]);
            await first.ReconcileAsync(); await restrictive.ReconcileAsync();
            Assert.Equal(128 * 1024, first.UsageBytes);
            Assert.Equal(first.UsageBytes, restrictive.UsageBytes);
        }
        finally { await lease.DisposeAsync(); }
        await first.ReconcileAsync(); await restrictive.ReconcileAsync();
        Assert.Equal(0, first.UsageBytes);
        Assert.Equal(0, restrictive.UsageBytes);
    }

    [Fact]
    public async Task ZeroLimitRejectsBeforeCallingAProducerOrCreatingScratch()
    {
        using var cache = Manager(limit: 0);
        var called = false;
        var error = await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using var lease = await cache.AcquireScratchOutputAsync(".bin", (_, _) => { called = true; return Task.CompletedTask; }, Valid);
        });
        Assert.Equal("CacheStorageLimit", error.Data["ErrorCode"]);
        Assert.False(called);
        Assert.Empty(Directory.EnumerateFiles(Location, "*", SearchOption.AllDirectories));
        Assert.Equal(0, cache.ActivePreviewLeases);
        Assert.Equal(0, cache.UsageBytes);
    }

    [ScratchWindowsFact]
    public async Task CleanupFailurePreservesThePrimaryErrorAndRetainedFileCharge()
    {
        using var cache = Manager();
        FileStream? blocking = null;
        var expected = new IOException("Generated validator failure");
        expected.Data["ErrorCode"] = "GeneratedValidation";
        try
        {
            var actual = await Assert.ThrowsAsync<IOException>(async () =>
            {
                await using var lease = await cache.AcquireScratchOutputAsync(".bin", (output, token) => output.WriteAsync(new byte[1024], token).AsTask(),
                    (path, _) => { blocking = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); throw expected; });
            });
            Assert.Same(expected, actual);
            Assert.Equal("GeneratedValidation", actual.Data["ErrorCode"]);
            Assert.True(actual.Data.Contains("ScratchCleanupError"));
            Assert.Equal(1024, PhysicalBytes());
            Assert.Equal(1024, cache.UsageBytes);
            Assert.Equal(0, cache.ActivePreviewLeases);
        }
        finally { blocking?.Dispose(); }
        await cache.ClearAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, cache.UsageBytes);
    }

    [Fact]
    public async Task LeaseStaysRegisteredWhileDeletionWaitsForSharedAccounting()
    {
        using var cache = Manager();
        var lease = await cache.AcquireScratchOutputAsync(".bin", (output, token) => output.WriteAsync(new byte[1024], token).AsTask(), Valid);
        Task releasing;
        Task clearing;
        using (var barrier = CacheStorageBudget.ForRoot(Location).Acquire(Path.Combine(Location, "barrier.bin"), 0, 0))
        {
            Assert.NotNull(barrier);
            releasing = Task.Run(async () => await lease.DisposeAsync());
            var operation = (SemaphoreSlim)typeof(CacheManager).GetField("operation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cache)!;
            Assert.True(SpinWait.SpinUntil(() => operation.CurrentCount == 0, TimeSpan.FromSeconds(1)));
            Assert.Equal(1, cache.ActivePreviewLeases);
            clearing = cache.ClearAsync();
            Assert.False(clearing.IsCompleted);
            barrier.Complete(0);
        }
        await releasing.WaitAsync(TimeSpan.FromSeconds(5));
        await clearing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, cache.ActivePreviewLeases);
        Assert.Equal(0, cache.UsageBytes);
    }

    private ArchiveSnapshot Snapshot() => new(Path.Combine(root, "source", "generated.idx"), "generated-scratch-index",
        IndexLayout.SupportedVersion, 0, [0x41], 0, [], [], new FileStamp(true, "index", 1, 1, 1, true), "generated-scratch-stamp", []);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    private sealed record SavedMetadata(string Value);
    private sealed class ScratchWindowsFactAttribute : FactAttribute
    { public ScratchWindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows file-sharing cleanup check."; } }
}
