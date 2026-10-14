using System.Windows.Threading;

namespace Deviant.Desktop;

/// <summary>Publishes the latest progress value without queuing every worker update on the UI thread.</summary>
internal sealed class CoalescedDispatcherProgress<T> : IProgress<T>, IDisposable where T : class
{
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer timer;
    private readonly Action<T> publish;
    private T? latest;
    private int disposed;

    public CoalescedDispatcherProgress(Dispatcher dispatcher, Action<T> publish)
    {
        dispatcher.VerifyAccess(); this.dispatcher = dispatcher; this.publish = publish;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            (_, _) => Flush(), dispatcher);
        timer.Start();
    }

    public void Report(T value)
    {
        if (Volatile.Read(ref disposed) == 0) Interlocked.Exchange(ref latest, value);
    }

    private void Flush()
    {
        if (Interlocked.Exchange(ref latest, null) is { } value && Volatile.Read(ref disposed) == 0)
            publish(value);
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        timer.Stop(); Interlocked.Exchange(ref latest, null);
    }
}
