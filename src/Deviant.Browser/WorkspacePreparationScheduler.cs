namespace Deviant.Browser;

public sealed record WorkspacePreparationFailure(string Key, Exception Error);

/// <summary>Runs one owned preparation job after a quiet workspace interval.</summary>
public sealed class WorkspacePreparationScheduler : IAsyncDisposable
{
    public const int MaximumKeys = 64;
    private readonly object sync = new();
    private readonly Dictionary<string, Work> jobs = new(StringComparer.Ordinal);
    private readonly LinkedList<Work> pending = [];
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TimeProvider clock;
    private readonly TimeSpan idleDelay;
    private readonly TimeSpan drainTimeout;
    private readonly Task worker;
    private Active? active;
    private long lastActivity;
    private long sequence;
    private int foreground;
    private bool disposed;
    private Task? disposal;

    public WorkspacePreparationScheduler(TimeSpan? idleDelay = null, TimeSpan? drainTimeout = null)
    {
        this.idleDelay = idleDelay ?? TimeSpan.FromSeconds(2);
        this.drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(2);
        if (this.idleDelay <= TimeSpan.Zero || this.idleDelay > TimeSpan.FromMinutes(1) ||
            this.drainTimeout <= TimeSpan.Zero || this.drainTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(idleDelay));
        clock = TimeProvider.System;
        lastActivity = this.clock.GetTimestamp(); worker = Task.Run(RunAsync);
    }

    public event Action<WorkspacePreparationFailure>? JobFailed;
    public WorkspacePreparationFailure? LastFailure { get; private set; }
    public int PendingCount { get { lock (sync) return pending.Count; } }
    public bool HasActiveJob { get { lock (sync) return active is not null; } }

    public IDisposable EnterForeground()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            checked { foreground++; } Touch(); RequestCancellation(active); Signal();
            return new ForegroundLease(this);
        }
    }

    public void NotifyActivity()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Touch(); RequestCancellation(active); Signal();
        }
    }

    /// <summary>Transfers the owner after successful admission. Preemption retains it for retry;
    /// replacement, eviction, completion and workspace disposal release it.</summary>
    public void Enqueue(string stableKey, Func<CancellationToken, Task> job, IDisposable? owner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableKey); ArgumentNullException.ThrowIfNull(job);
        if (stableKey.Length > 1024) throw new ArgumentOutOfRangeException(nameof(stableKey));
        Work? dropped = null;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this); Touch();
            if (jobs.TryGetValue(stableKey, out var previous))
            {
                if (previous.Node is not null) { pending.Remove(previous.Node); previous.Node = null; dropped = previous; }
                else if (active?.Work == previous) RequestCancellation(active);
                jobs.Remove(stableKey);
            }
            else if (jobs.Count >= MaximumKeys)
            {
                var oldest = pending.Last?.Value ?? throw new InvalidOperationException("The preparation queue has no evictable job.");
                pending.Remove(oldest.Node!); oldest.Node = null; jobs.Remove(oldest.Key); dropped = oldest;
            }
            var next = new Work(stableKey, job, owner, ++sequence);
            jobs.Add(stableKey, next); Insert(next); Signal();
        }
        if (dropped is not null) ReleaseOwner(dropped);
    }

    public bool Remove(string stableKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableKey); Work? dropped = null;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!jobs.Remove(stableKey, out var work)) return false;
            if (work.Node is not null) { pending.Remove(work.Node); work.Node = null; dropped = work; }
            else if (active?.Work == work) RequestCancellation(active);
            Signal();
        }
        if (dropped is not null) ReleaseOwner(dropped);
        return true;
    }

    /// <summary>Cancels and waits for the current idle job without dropping queued work.</summary>
    public async Task DrainAsync(CancellationToken token = default)
    {
        Task? completion;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Touch(); RequestCancellation(active); completion = active?.Completion.Task; Signal();
        }
        token.ThrowIfCancellationRequested();
        if (completion is not null) await WaitForDrainAsync(completion, token).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposal is not null) return new(disposal);
            disposed = true; RequestCancellation(active);
            var removed = pending.ToArray(); pending.Clear(); jobs.Clear();
            foreach (var work in removed) { work.Node = null; work.Job = null; }
            lifetime.Cancel(); Signal();
            disposal = Task.Run(() => DisposeCoreAsync(removed));
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync(Work[] removed)
    {
        var release = Task.Run(() => { foreach (var work in removed) ReleaseOwner(work); });
        await WaitForDrainAsync(Task.WhenAll(worker, release), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WaitForDrainAsync(Task completion, CancellationToken token)
    {
        try { await completion.WaitAsync(drainTimeout, token).ConfigureAwait(false); }
        catch (TimeoutException error)
        {
            throw new TimeoutException("Workspace preparation did not stop within the cancellation deadline. Its active owner remains retained until it exits.", error);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                Active? run = null; TimeSpan wait;
                lock (sync)
                {
                    if (disposed) break;
                    wait = foreground > 0 || pending.Count == 0 ? Timeout.InfiniteTimeSpan
                        : idleDelay - clock.GetElapsedTime(lastActivity, clock.GetTimestamp());
                    if (wait != Timeout.InfiniteTimeSpan && wait <= TimeSpan.Zero)
                    {
                        var work = pending.First!.Value; pending.RemoveFirst(); work.Node = null;
                        run = new(work); active = run;
                    }
                }
                if (run is not null) { await ExecuteAsync(run).ConfigureAwait(false); continue; }
                if (wait == Timeout.InfiniteTimeSpan) await wake.WaitAsync(lifetime.Token).ConfigureAwait(false);
                else await wake.WaitAsync(wait, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { lock (sync) { wake.Dispose(); lifetime.Dispose(); } }
    }

    private async Task ExecuteAsync(Active run)
    {
        Exception? failure = null;
        try
        {
            run.Cancellation.Token.ThrowIfCancellationRequested();
            var job = run.Work.Job ?? throw new InvalidOperationException("The active preparation job is unavailable.");
            await job(run.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested) { }
        catch (Exception error) { failure = error; }
        Task? callbacks;
        lock (sync) { run.Finishing = true; callbacks = run.CancellationCallbacks; }
        if (callbacks is not null)
            try { await callbacks.ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
        bool retry;
        lock (sync)
        {
            bool current = jobs.TryGetValue(run.Work.Key, out var latest) && ReferenceEquals(latest, run.Work);
            retry = !disposed && current && run.Cancellation.IsCancellationRequested && failure is null;
            if (retry) Insert(run.Work);
            else if (current) jobs.Remove(run.Work.Key);
        }
        run.Cancellation.Dispose();
        if (!retry) ReleaseOwner(run.Work);
        if (failure is not null) Report(run.Work.Key, failure);
        lock (sync)
        {
            active = null; run.Completion.TrySetResult();
            if (!disposed) Signal();
        }
    }

    private void RequestCancellation(Active? run)
    {
        if (run is null || run.Finishing || run.CancellationCallbacks is not null) return;
        run.CancellationCallbacks = run.Cancellation.CancelAsync();
    }
    private void ReleaseForeground()
    {
        lock (sync)
        {
            if (foreground > 0) foreground--;
            if (!disposed) { Touch(); Signal(); }
        }
    }
    private void Touch() => lastActivity = clock.GetTimestamp();
    private void Signal() { if (wake.CurrentCount == 0) wake.Release(); }
    private void Insert(Work work)
    {
        var cursor = pending.First;
        while (cursor is not null && cursor.Value.Sequence > work.Sequence) cursor = cursor.Next;
        work.Node = cursor is null ? pending.AddLast(work) : pending.AddBefore(cursor, work);
    }
    private void ReleaseOwner(Work work)
    {
        work.Job = null;
        var owner = Interlocked.Exchange(ref work.Owner, null);
        try { owner?.Dispose(); } catch (Exception error) { Report(work.Key, error); }
    }
    private void Report(string key, Exception error)
    {
        var failure = new WorkspacePreparationFailure(key, error); LastFailure = failure;
        try { JobFailed?.Invoke(failure); } catch { /* Diagnostic subscribers cannot stop queue cleanup. */ }
    }
    private sealed class Work(string key, Func<CancellationToken, Task> job, IDisposable? owner, long sequence)
    {
        public string Key { get; } = key; public Func<CancellationToken, Task>? Job = job;
        public long Sequence { get; } = sequence; public IDisposable? Owner = owner; public LinkedListNode<Work>? Node;
    }
    private sealed class Active(Work work)
    {
        public Work Work { get; } = work; public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? CancellationCallbacks;
        public bool Finishing;
    }
    private sealed class ForegroundLease(WorkspacePreparationScheduler owner) : IDisposable
    {
        private WorkspacePreparationScheduler? owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.ReleaseForeground();
    }
}
