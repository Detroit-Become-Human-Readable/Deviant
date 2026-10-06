using System.Collections.Concurrent;

namespace Deviant.Browser;

/// <summary>Runs catalog work on one background thread and drains accepted work before shutdown.</summary>
public sealed class SerializedWorker
{
    private readonly BlockingCollection<IWorkItem> queue = new();
    private readonly object gate = new();
    private readonly TaskCompletionSource<bool> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private bool accepting = true;
    private Task? stopTask;
    private Action? disposeOwned;

    public SerializedWorker(string name = "Deviant catalog worker")
    {
        thread = new Thread(Run) { IsBackground = true, Name = name };
        thread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);
        var item = new WorkItem<T>(action, cancellationToken);
        lock (gate)
        {
            if (!accepting)
            {
                item.Reject(new ObjectDisposedException(nameof(SerializedWorker)));
                return item.Task;
            }
            queue.Add(item);
        }
        return item.Task;
    }

    public Task StopAsync(Action? disposeOwnedOnWorker = null)
    {
        lock (gate)
        {
            if (stopTask is not null) return stopTask;
            accepting = false;
            disposeOwned = disposeOwnedOnWorker;
            queue.CompleteAdding();
            stopTask = stopped.Task;
            return stopTask;
        }
    }

    private void Run()
    {
        Exception? failure = null;
        try
        {
            foreach (var item in queue.GetConsumingEnumerable()) item.Execute();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            try { disposeOwned?.Invoke(); }
            catch (Exception ex) { failure ??= ex; }
            queue.Dispose();
            if (failure is null) stopped.TrySetResult(true);
            else stopped.TrySetException(failure);
        }
    }

    private interface IWorkItem
    {
        void Execute();
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<T> action;
        private readonly CancellationToken token;
        private readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration registration;
        private int state;

        public WorkItem(Func<T> action, CancellationToken token)
        {
            this.action = action;
            this.token = token;
            if (token.CanBeCanceled)
            {
                registration = token.Register(static state => ((WorkItem<T>)state!).CancelWhileQueued(), this);
                if (Volatile.Read(ref state) != 0) registration.Dispose();
            }
        }

        public Task<T> Task => completion.Task;

        public void Execute()
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            {
                registration.Dispose();
                return;
            }
            registration.Dispose();
            try
            {
                token.ThrowIfCancellationRequested();
                var result = action();
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                completion.TrySetCanceled(token);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally { Volatile.Write(ref state, 2); }
        }

        public void Reject(Exception exception)
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) != 0) return;
            registration.Dispose();
            completion.TrySetException(exception);
        }

        private void CancelWhileQueued()
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                completion.TrySetCanceled(token);
        }
    }
}
