namespace Deviant.Browser;

internal sealed class RenderResourceGroup : IDisposable
{
    private readonly object gate = new();
    private readonly List<IDisposable> resources = [];
    private int references = 1;
    private bool ownerDisposed;
    public void Add(IDisposable resource)
    { lock (gate) { if (references == 0) { resource.Dispose(); throw new ObjectDisposedException(nameof(RenderResourceGroup)); } resources.Add(resource); } }
    public IDisposable AcquireLease()
    { lock (gate) { ObjectDisposedException.ThrowIf(references == 0, this); references++; return new Lease(this); } }
    public void Dispose()
    { lock (gate) { if (ownerDisposed) return; ownerDisposed = true; } Release(); }
    private void Release()
    {
        IDisposable[] released;
        lock (gate) { if (--references != 0) return; released = resources.ToArray(); resources.Clear(); }
        for (int index = released.Length - 1; index >= 0; index--) released[index].Dispose();
    }
    private sealed class Lease(RenderResourceGroup owner) : IDisposable
    { private RenderResourceGroup? owner = owner; public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(); }
}
