namespace Deviant.Browser;

/// <summary>Reports one latest value per interval instead of posting a dispatcher callback per I/O buffer.</summary>
public sealed class CoalescedProgress<T> : IProgress<T>, IDisposable where T : class
{
    private readonly object gate = new();
    private readonly Action<T> publish;
    private readonly Timer timer;
    private T? latest;
    private bool disposed;
    public CoalescedProgress(Action<T> publish, TimeSpan? interval = null)
    {
        this.publish = publish;
        var period = interval ?? TimeSpan.FromMilliseconds(100);
        timer = new Timer(_ => Flush(), null, period, period);
    }
    public void Report(T value) { lock (gate) { if (!disposed) latest = value; } }
    public void Flush()
    {
        T? value;
        lock (gate) { if (disposed) return; value = latest; latest = null; }
        if (value is not null) publish(value);
    }
    public void Dispose()
    {
        lock (gate) { disposed = true; latest = null; }
        timer.Dispose();
    }
}
