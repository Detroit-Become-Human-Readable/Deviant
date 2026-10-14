using Deviant.Catalog;
using Deviant.Core;

namespace Deviant.Browser;

public sealed partial class CatalogSession
{
    private readonly CancellationTokenSource persistenceCancellation;
    private readonly Task persistenceTask;

    private CatalogSession(ArchiveSnapshot snapshot, IResourceCatalog catalog, SerializedWorker worker,
        string? cacheRoot, IProgress<ArchiveProgress>? progress, CancellationToken openingToken)
    {
        Snapshot = snapshot;
        this.catalog = catalog;
        this.worker = worker;
        persistenceCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, openingToken);
        persistenceTask = PersistCatalogAsync(cacheRoot, progress);
        ObservePersistence(persistenceTask);
    }

    /// <summary>Completes when the snapshot's raw catalog cache has been validated or written.</summary>
    /// <remarks>It may be awaited to observe cache failures. Reads use the verified in-memory snapshot while it runs.</remarks>
    public Task PersistenceTask => persistenceTask;

    private async Task PersistCatalogAsync(string? cacheRoot, IProgress<ArchiveProgress>? progress)
    {
        await Task.Run(() =>
        {
            persistenceCancellation.Token.ThrowIfCancellationRequested();
            using var persisted = CatalogService.Open(Snapshot, cacheRoot, progress, persistenceCancellation.Token);
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private static void ObservePersistence(Task task) => _ = task.ContinueWith(
        static completed => _ = completed.Exception,
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
}
