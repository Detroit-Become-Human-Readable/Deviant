using System.Security.Cryptography;
using Deviant.Catalog;
using Deviant.Core;
using Xunit;

namespace Deviant.Tests;

public sealed class CacheArtifactOutputTests : IDisposable
{
    private const int ChunkBytes = 128 * 1024;
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-artifact-output-" + Guid.NewGuid().ToString("N"));
    private string Location => Path.Combine(root, "cache");
    private CacheManager Manager(string name, long limit) => new(Path.Combine(root, name), new(Location, limit));
    private CacheArtifactOutput Output(string name) => new(Location, Path.Combine(Location, name));
    private long PhysicalBytes() => Directory.EnumerateFiles(Location, "*", SearchOption.AllDirectories)
        .Sum(path => new FileInfo(path).Length);

    [Fact]
    public async Task RestrictiveManagerRejectsTheNextChunkBeforeItGrowsTheFile()
    {
        using var permissive = Manager("first-settings", 2 * 1024 * 1024);
        using var restrictive = Manager("second-settings", 192 * 1024);
        var output = Output("first.data");
        await using var stream = output.OpenWrite();
        var bytes = new byte[2 * ChunkBytes];
        Array.Fill(bytes, (byte)0x41);

        var error = await Assert.ThrowsAsync<IOException>(() => stream.WriteAsync(bytes.AsMemory()).AsTask());

        Assert.Equal("CacheStorageLimit", error.Data["ErrorCode"]);
        Assert.Equal((long)ChunkBytes, error.Data["RequestedBytes"]);
        Assert.Equal(ChunkBytes, stream.Length);
        Assert.Equal(ChunkBytes, new FileInfo(output.Path).Length);
        Assert.Equal(ChunkBytes, PhysicalBytes());
        Assert.True(PhysicalBytes() <= restrictive.LimitBytes);
        stream.Position = 0;
        var retained = new byte[ChunkBytes];
        await stream.ReadExactlyAsync(retained);
        Assert.Equal(bytes.AsSpan(0, ChunkBytes).ToArray(), retained);

        // In-flight bytes remain charged even before the first writer closes.
        await using var second = Output("second.data").OpenWrite();
        await Assert.ThrowsAsync<IOException>(() => second.WriteAsync(new byte[64 * 1024].AsMemory()).AsTask());
        Assert.Equal(0, second.Length);
        Assert.Equal(ChunkBytes, PhysicalBytes());
    }

    [Fact]
    public async Task SeekOverwriteSparseGrowthAndChecksumTrailerRoundTrip()
    {
        using var cache = Manager("settings", 1024 * 1024);
        var output = Output("roundtrip.data");
        await using var stream = output.OpenWrite();
        Assert.True(stream.CanRead && stream.CanSeek && stream.CanWrite);
        var expected = new byte[520];
        for (int index = 0; index < 256; index++) expected[index] = (byte)index;
        stream.Write(expected, 0, 128);
        await stream.WriteAsync(expected.AsMemory(128, 128));
        stream.Seek(4, SeekOrigin.Begin);
        stream.WriteByte(0xA5); expected[4] = 0xA5;
        stream.Position = 512;
        stream.WriteByte(0x5A); expected[512] = 0x5A;
        stream.SetLength(expected.Length);
        stream.Position = 0;
        var digest = await SHA256.HashDataAsync(stream);
        Assert.Equal(SHA256.HashData(expected), digest);
        stream.Seek(0, SeekOrigin.End);
        await stream.WriteAsync(digest.AsMemory());
        await stream.FlushAsync();
        Assert.Equal(expected.Length + digest.Length, stream.Length);
        stream.Position = 0;
        var actual = new byte[expected.Length + digest.Length];
        await stream.ReadExactlyAsync(actual);
        Assert.Equal(expected.Concat(digest).ToArray(), actual);
        Assert.Equal(-1, stream.ReadByte());
        Assert.Equal(actual.Length, PhysicalBytes());
    }

    [Fact]
    public async Task CancelledWriteDoesNotCreateSparseGrowthOrChangePosition()
    {
        using var cache = Manager("settings", 1024 * 1024);
        await using var stream = Output("cancelled.data").OpenWrite();
        stream.Position = 8192;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stream.WriteAsync(new byte[2 * ChunkBytes].AsMemory(), cancellation.Token).AsTask());

        Assert.Equal(0, stream.Length);
        Assert.Equal(8192, stream.Position);
        Assert.Equal(0, PhysicalBytes());
        stream.WriteByte(0x31);
        Assert.Equal(8193, stream.Length);
    }

    [Fact]
    public async Task QueuedWriteCanCancelWhileAnotherWriteWaitsForRootAdmission()
    {
        using var cache = Manager("settings", 1024 * 1024);
        await using var stream = Output("queued.data").OpenWrite();
        using var cancellation = new CancellationTokenSource();
        Task first;
        using (var barrier = CacheStorageBudget.ForRoot(Location).Acquire(Path.Combine(Location, "barrier.data"), 0, 0))
        {
            Assert.NotNull(barrier);
            // No await occurs while this test thread owns the root mutex. The
            // first writer owns its stream gate and queues its bounded worker.
            first = stream.WriteAsync(new byte[32].AsMemory()).AsTask();
            var cancelled = stream.WriteAsync(new byte[64].AsMemory(), cancellation.Token).AsTask();
            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => cancelled.GetAwaiter().GetResult());
            Assert.False(first.IsCompleted);
            barrier.Complete(0);
        }
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(32, stream.Length);
        Assert.Equal(32, PhysicalBytes());
    }

    [Fact]
    public async Task FlushAndCloseCannotGrowADeniedBufferedTailOrRefundRetainedBytes()
    {
        using var cache = Manager("settings", 192 * 1024);
        var output = Output("closed.data");
        var stream = output.OpenWrite();
        try
        {
            stream.Write(new byte[ChunkBytes]);
            Assert.Throws<IOException>(() => stream.Write(new byte[ChunkBytes]));
            Assert.Equal(ChunkBytes, new FileInfo(output.Path).Length);
            stream.Flush();
            await stream.FlushAsync();
            Assert.Equal(ChunkBytes, PhysicalBytes());
        }
        finally { await stream.DisposeAsync(); }
        stream.Dispose();
        Assert.False(stream.CanRead || stream.CanSeek || stream.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => stream.WriteByte(1));
        Assert.Equal(ChunkBytes, new FileInfo(output.Path).Length);

        using var next = Output("next.data").OpenWrite();
        Assert.Throws<IOException>(() => next.Write(new byte[64 * 1024]));
        Assert.Equal(ChunkBytes, PhysicalBytes());
    }

    [Fact]
    public void SetLengthReservesBeforeSparseGrowthAndReleasesSuccessfulShrinks()
    {
        using var cache = Manager("settings", 192 * 1024);
        using var stream = Output("resized.data").OpenWrite();
        stream.SetLength(ChunkBytes);
        Assert.Throws<IOException>(() => stream.SetLength(192 * 1024));
        Assert.Equal(ChunkBytes, stream.Length);
        Assert.Equal(ChunkBytes, PhysicalBytes());
        stream.SetLength(0);
        using var second = Output("after-shrink.data").OpenWrite();
        second.Write(new byte[ChunkBytes]);
        Assert.Equal(ChunkBytes, PhysicalBytes());
    }

    [Fact]
    public void ExistingFilesAndPathsOutsideTheRootArePreserved()
    {
        using var cache = Manager("settings", 1024 * 1024);
        var outside = Path.Combine(root, "outside.data");
        Assert.Throws<IOException>(() => new CacheArtifactOutput(Location, outside));
        Assert.Throws<IOException>(() => new CacheArtifactOutput(Location, Location));
        Assert.False(File.Exists(outside));

        var output = Output("existing.data");
        using (var first = output.OpenWrite()) first.WriteByte(0x71);
        Assert.Throws<IOException>(() => output.OpenWrite());
        Assert.Equal(new byte[] { 0x71 }, File.ReadAllBytes(output.Path));
    }

    [Fact]
    public async Task AdmittedProducerRejectsBeforeOvershootAndRefundsItsPartialOutput()
    {
        using var permissive = Manager("first-settings", 2 * 1024 * 1024);
        using var restrictive = Manager("second-settings", 192 * 1024);
        int calls = 0;
        var failure = await Assert.ThrowsAsync<IOException>(() => permissive.AcquirePreparedOutputAsync(
            "generated", "too-large", ".data", async (output, token) =>
            {
                calls++;
                try { await output.WriteAsync(new byte[2 * ChunkBytes].AsMemory(), token); }
                catch (IOException)
                {
                    Assert.Equal(ChunkBytes, PhysicalBytes());
                    Assert.True(PhysicalBytes() <= restrictive.LimitBytes);
                    throw;
                }
            }, HasBytes));
        Assert.Equal("CacheStorageLimit", failure.Data["ErrorCode"]);
        Assert.Equal(1, calls);
        Assert.Equal(0, PhysicalBytes());
        // No Reconcile is permitted to repair an incorrectly retained charge.
        using var available = CacheStorageBudget.ForRoot(Location).Acquire(Path.Combine(Location, "probe.data"), 0, ChunkBytes);
        Assert.NotNull(available);
        available.Complete(0);
    }

    [Fact]
    public async Task MetadataSeesInFlightOutputAndProducerFailurePreservesMetadata()
    {
        using var cache = Manager("settings", 8 * 1024 * 1024);
        using var restrictive = Manager("second-settings", 8 * 1024 * 1024);
        using var store = FeatureStore.Open(Snapshot(), Location);
        store.Save("retained", "v1", "Retained metadata");
        await cache.ReconcileAsync();
        long desiredAdmission = checked(cache.UsageBytes + 3L * ChunkBytes);
        await restrictive.SetLimitAsync(checked((desiredAdmission * 8 + 6) / 7));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("Generated producer failure");
        var production = cache.AcquirePreparedOutputAsync("generated", "held", ".data", async (output, token) =>
        {
            await output.WriteAsync(new byte[2 * ChunkBytes].AsMemory(), token);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            throw expected;
        }, HasBytes);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(PhysicalBytes() <= restrictive.LimitBytes);
            Assert.ThrowsAny<IOException>(() => store.Save("incoming", "v1", new string('x', 2 * ChunkBytes)));
            Assert.Null(store.Load<string>("incoming", "v1"));
            Assert.Equal("Retained metadata", store.Load<string>("retained", "v1"));
        }
        finally { release.TrySetResult(); }
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => production);
        Assert.Same(expected, failure);
        Assert.DoesNotContain(Directory.EnumerateFiles(Location, "*", SearchOption.AllDirectories),
            path => Path.GetExtension(path) is ".data" or ".tmp");
        store.Save("incoming", "v1", new string('x', 2 * ChunkBytes));
        Assert.Equal(2 * ChunkBytes, store.Load<string>("incoming", "v1")!.Length);
    }

    [Fact]
    public async Task AwaitingProducerAllowsOtherKeysAndSharesLegacySingleFlight()
    {
        using var cache = Manager("settings", 4 * 1024 * 1024);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int producerCalls = 0;
        var first = cache.AcquirePreparedOutputAsync("generated", "same", ".data", async (output, token) =>
        {
            producerCalls++;
            await output.WriteAsync(new byte[] { 1, 2, 3 }.AsMemory(), token);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        }, HasBytes);
        CacheLease? other = null;
        Task<CacheLease>? legacy = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            legacy = cache.AcquirePreparedAsync("generated", "same", ".data",
                (_, _) => throw new InvalidOperationException("The existing flight should be shared."), HasBytes);
            other = await cache.AcquirePreparedOutputAsync("generated", "other", ".data",
                async (output, token) => await output.WriteAsync(new byte[] { 4, 5 }.AsMemory(), token), HasBytes)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(other.Path));
            Assert.False(first.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await using var produced = await first.WaitAsync(TimeSpan.FromSeconds(5));
        await using var shared = await legacy!.WaitAsync(TimeSpan.FromSeconds(5));
        if (other is not null) await other.DisposeAsync();
        Assert.Equal(produced.Path, shared.Path);
        Assert.Equal(1, producerCalls);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(shared.Path));
    }

    [Fact]
    public async Task AdmittedCancellationDeletesAndRefundsOutput()
    {
        using var cache = Manager("settings", 192 * 1024);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquisition = cache.AcquirePreparedOutputAsync("generated", "cancelled", ".data", async (output, token) =>
        {
            await output.WriteAsync(new byte[ChunkBytes].AsMemory(), token);
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, HasBytes, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);
        Assert.Equal(0, PhysicalBytes());
        using var available = CacheStorageBudget.ForRoot(Location).Acquire(Path.Combine(Location, "probe.data"), 0, ChunkBytes);
        Assert.NotNull(available); available.Complete(0);
    }

    [Fact]
    public async Task AdmittedZeroPolicyFailsBeforeCallingProducer()
    {
        using var cache = Manager("settings", 0);
        int calls = 0;
        var failure = await Assert.ThrowsAsync<IOException>(() => cache.AcquirePreparedOutputAsync(
            "generated", "disabled", ".data", (_, _) => { calls++; return Task.CompletedTask; }, HasBytes));
        Assert.Equal("CacheStorageLimit", failure.Data["ErrorCode"]);
        Assert.Equal(0, calls);
        Assert.Equal(0, PhysicalBytes());
    }

    [Fact]
    public async Task AdmittedHitSharesTheExistingPreparedNamespace()
    {
        using var cache = Manager("settings", 1024 * 1024);
        string path;
        await using (var legacy = await cache.AcquirePreparedAsync("generated", "existing", ".data",
            (destination, token) => File.WriteAllBytesAsync(destination, [1, 2, 3], token), HasBytes)) path = legacy.Path;
        await using var hit = await cache.AcquirePreparedOutputAsync("generated", "existing", ".data",
            (_, _) => throw new InvalidOperationException("A valid hit must not produce again."), HasBytes);
        Assert.Equal(path, hit.Path);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(hit.Path));
    }

    [Fact]
    public async Task AdmittedLiveLeaseSurvivesZeroPolicyUntilRelease()
    {
        using var cache = Manager("settings", 1024 * 1024);
        var lease = await cache.AcquirePreparedOutputAsync("generated", "leased", ".data",
            async (output, token) => await output.WriteAsync(new byte[] { 8, 9 }.AsMemory(), token), HasBytes);
        try
        {
            await cache.SetLimitAsync(0);
            Assert.Equal(new byte[] { 8, 9 }, await File.ReadAllBytesAsync(lease.Path));
        }
        finally { await lease.DisposeAsync(); }
        Assert.False(File.Exists(lease.Path));
        Assert.Equal(0, PhysicalBytes());
        Assert.Equal(0, cache.UsageBytes);
    }

    private static async Task<bool> HasBytes(string path, CancellationToken token) =>
        (await File.ReadAllBytesAsync(path, token)).Length > 0;

    private ArchiveSnapshot Snapshot() => new(Path.Combine(root, "source", "generated.idx"), "generated-output-index",
        IndexLayout.SupportedVersion, 0, [0x41], 0, [], [], new FileStamp(true, "index", 1, 1, 1, true), "generated-output-stamp", []);

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
