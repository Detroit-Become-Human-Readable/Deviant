using Deviant.Browser;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class WorkspacePreparationSchedulerTests
{
    [Fact]
    public async Task ForegroundLeaseDefersIdleWorkUntilTheQuietPeriod()
    {
        await using var scheduler = NewScheduler(); using var foreground = scheduler.EnterForeground();
        var entered = Signal();
        scheduler.Enqueue("asset", _ => { entered.TrySetResult(); return Task.CompletedTask; });
        await Task.Delay(70); Assert.False(entered.Task.IsCompleted);
        foreground.Dispose(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await scheduler.DrainAsync();
    }

    [Fact]
    public async Task ForegroundPreemptsOneActiveJobAndRetriesWithItsOwner()
    {
        await using var scheduler = NewScheduler(); var owner = new Owner();
        var entered = Signal(); var retried = Signal(); int attempts = 0; int concurrent = 0; int peak = 0;
        scheduler.Enqueue("asset", async token =>
        {
            int current = Interlocked.Increment(ref concurrent); peak = Math.Max(peak, current);
            try
            {
                if (Interlocked.Increment(ref attempts) == 1) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
                else retried.TrySetResult();
            }
            finally { Interlocked.Decrement(ref concurrent); }
        }, owner);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using (scheduler.EnterForeground())
        {
            await scheduler.DrainAsync(); Assert.False(scheduler.HasActiveJob); Assert.Equal(1, scheduler.PendingCount);
            Assert.Equal(0, owner.Disposals); await Task.Delay(70); Assert.Equal(1, attempts);
        }
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(2)); await owner.Released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, attempts); Assert.Equal(1, peak); Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task ActivityCancelsIdleWorkAndDoesNotPublishItsCanceledAttempt()
    {
        await using var scheduler = NewScheduler(); var entered = Signal(); var finished = Signal();
        int attempts = 0; int published = 0;
        scheduler.Enqueue("asset", async token =>
        {
            if (Interlocked.Increment(ref attempts) == 1) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref published); finished.TrySetResult();
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using (scheduler.EnterForeground())
        { scheduler.NotifyActivity(); await scheduler.DrainAsync(); Assert.Equal(0, published); }
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(1, published);
    }

    [Fact]
    public async Task RecentFirstQueueIsBoundedAndReplacementReleasesItsTransferredOwner()
    {
        await using var scheduler = NewScheduler(); using var foreground = scheduler.EnterForeground();
        var owners = Enumerable.Range(0, 66).Select(_ => new Owner()).ToArray(); var replacement = new Owner();
        var ran = new List<int>(); var done = Signal();
        for (int index = 0; index < owners.Length; index++)
        {
            int captured = index;
            scheduler.Enqueue(index.ToString(), _ => { lock (ran) { ran.Add(captured); if (ran.Count == 64) done.TrySetResult(); } return Task.CompletedTask; }, owners[index]);
        }
        scheduler.Enqueue("65", _ => { lock (ran) { ran.Add(65); if (ran.Count == 64) done.TrySetResult(); } return Task.CompletedTask; }, replacement);
        Assert.Equal(64, scheduler.PendingCount); Assert.Equal(1, owners[0].Disposals); Assert.Equal(1, owners[1].Disposals);
        Assert.Equal(1, owners[65].Disposals);
        foreground.Dispose(); await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await replacement.Released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(Enumerable.Range(2, 64).Reverse(), ran);
        Assert.Equal(1, replacement.Disposals);
    }

    [Fact]
    public async Task ReplacingAnActiveKeyCancelsOldWorkBeforeStartingItsReplacement()
    {
        await using var scheduler = NewScheduler(); var firstOwner = new Owner(); var secondOwner = new Owner();
        var entered = Signal(); var ran = Signal(); int active = 0;
        scheduler.Enqueue("asset", async token =>
        { Interlocked.Increment(ref active); entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); } finally { Interlocked.Decrement(ref active); } }, firstOwner);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scheduler.Enqueue("asset", _ => { Assert.Equal(0, active); ran.TrySetResult(); return Task.CompletedTask; }, secondOwner);
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(2)); await secondOwner.Released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, firstOwner.Disposals); Assert.Equal(1, secondOwner.Disposals);
    }

    [Fact]
    public async Task DrainDeadlineIsVisibleAndActiveOwnerStaysAliveUntilActualExit()
    {
        await using var scheduler = NewScheduler(TimeSpan.FromMilliseconds(50)); var owner = new Owner();
        var entered = Signal(); var exit = Signal();
        scheduler.Enqueue("asset", async _ => { entered.TrySetResult(); await exit.Task; }, owner);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); using var foreground = scheduler.EnterForeground();
        var failure = await Assert.ThrowsAsync<TimeoutException>(() => scheduler.DrainAsync());
        Assert.Contains("active owner remains retained", failure.Message); Assert.Equal(0, owner.Disposals); Assert.True(scheduler.HasActiveJob);
        Assert.True(scheduler.Remove("asset")); exit.TrySetResult();
        await owner.Released.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task DisposalDropsQueuedJobsAndDoesNotRetryTheCanceledActiveJob()
    {
        var scheduler = NewScheduler(); var activeOwner = new Owner(); var pendingOwner = new Owner(); var entered = Signal();
        int attempts = 0; bool queuedRan = false;
        scheduler.Enqueue("active", async token => { Interlocked.Increment(ref attempts); entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }, activeOwner);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scheduler.Enqueue("pending", _ => { queuedRan = true; return Task.CompletedTask; }, pendingOwner);
        await scheduler.DisposeAsync(); Assert.False(queuedRan); Assert.Equal(1, attempts);
        Assert.Equal(1, activeOwner.Disposals); Assert.Equal(1, pendingOwner.Disposals);
        Assert.Throws<ObjectDisposedException>(() => scheduler.EnterForeground());
        var untransferred = new Owner();
        Assert.Throws<ObjectDisposedException>(() => scheduler.Enqueue("rejected", _ => Task.CompletedTask, untransferred));
        Assert.Equal(0, untransferred.Disposals); untransferred.Dispose(); await scheduler.DisposeAsync();
    }

    [Fact]
    public async Task DisposeDeadlineDoesNotReleaseAnUncooperativeActiveOwnerPrematurely()
    {
        var scheduler = NewScheduler(TimeSpan.FromMilliseconds(50)); var owner = new Owner(); var entered = Signal(); var exit = Signal();
        scheduler.Enqueue("asset", async _ => { entered.TrySetResult(); await exit.Task; }, owner);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<TimeoutException>(() => scheduler.DisposeAsync().AsTask()); Assert.Equal(0, owner.Disposals);
        exit.TrySetResult(); await owner.Released.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task FailedJobIsReportedReleasedAndDoesNotPreventTheNextJob()
    {
        await using var scheduler = NewScheduler(); using var foreground = scheduler.EnterForeground();
        var owner = new Owner(); var failed = Signal(); var success = Signal();
        scheduler.JobFailed += failure => { Assert.Equal("bad", failure.Key); Assert.IsType<IOException>(failure.Error); failed.TrySetResult(); };
        scheduler.Enqueue("good", _ => { success.TrySetResult(); return Task.CompletedTask; });
        scheduler.Enqueue("bad", _ => Task.FromException(new IOException("failed")), owner);
        foreground.Dispose(); await failed.Task.WaitAsync(TimeSpan.FromSeconds(2)); await success.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, owner.Disposals); Assert.Equal("bad", scheduler.LastFailure!.Key);
    }

    private static WorkspacePreparationScheduler NewScheduler(TimeSpan? timeout = null) =>
        new(idleDelay: TimeSpan.FromMilliseconds(20), drainTimeout: timeout ?? TimeSpan.FromSeconds(1));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Owner : IDisposable
    {
        private int disposals; public int Disposals => Volatile.Read(ref disposals);
        public TaskCompletionSource Released { get; } = Signal();
        public void Dispose() { Interlocked.Increment(ref disposals); Released.TrySetResult(); }
    }
}
