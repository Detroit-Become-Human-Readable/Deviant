using Deviant.Assets;
using Deviant.Catalog;

namespace Deviant.Browser;

public sealed partial class AudioExplorerSession
{
    private int sharedFeatureLeaseCount;
    private TaskCompletionSource<bool>? sharedFeatureLeasesDrained;

    public SharedFeatureStoreLease AcquireSharedFeatureStore()
    {
        lock (disposeGate)
        {
            ThrowIfDisposed();
            checked { sharedFeatureLeaseCount++; }
            return new(this, featureStore, new(Snapshot.IndexContentId, Snapshot.PhysicalSourceStamp), lifetime.Token);
        }
    }

    private Task WaitForSharedFeatureLeasesAsync()
    {
        lock (disposeGate)
        {
            if (sharedFeatureLeaseCount == 0) return Task.CompletedTask;
            sharedFeatureLeasesDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            return sharedFeatureLeasesDrained.Task;
        }
    }

    internal void ReleaseSharedFeatureStore()
    {
        lock (disposeGate)
        {
            if (sharedFeatureLeaseCount <= 0) throw new InvalidOperationException("The shared feature lease count is invalid.");
            if (--sharedFeatureLeaseCount == 0) sharedFeatureLeasesDrained?.TrySetResult(true);
        }
    }
}

/// <summary>Borrowed access to the archive session's feature store. Dispose consumers before releasing this lease.</summary>
public sealed class SharedFeatureStoreLease : IDisposable
{
    private AudioExplorerSession? owner;
    private readonly FeatureStore store;

    internal SharedFeatureStoreLease(AudioExplorerSession owner, FeatureStore store,
        SourceRevision revision, CancellationToken sourceCancellation)
    {
        this.owner = owner;
        this.store = store;
        Revision = revision;
        SourceCancellation = sourceCancellation;
    }

    public FeatureStore Store => Volatile.Read(ref owner) is null
        ? throw new ObjectDisposedException(nameof(SharedFeatureStoreLease)) : store;
    public SourceRevision Revision { get; }
    public CancellationToken SourceCancellation { get; }
    public void Dispose() => Interlocked.Exchange(ref owner, null)?.ReleaseSharedFeatureStore();
}
