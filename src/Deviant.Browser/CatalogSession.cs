using Deviant.Archives;
using Deviant.Catalog;
using Deviant.Core;

namespace Deviant.Browser;

/// <summary>A source snapshot and its single-thread-owned SQLite catalog.</summary>
public sealed class CatalogSession : IBrowserCatalogSession
{
    private readonly SerializedWorker worker;
    private readonly IResourceCatalog catalog;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object disposeGate = new();
    private Task? disposeTask;
    private TaskCompletionSource<bool>? leasesDrained;
    private int leaseCount;
    private bool disposalRequested;

    private CatalogSession(ArchiveSnapshot snapshot, IResourceCatalog catalog, SerializedWorker worker)
    {
        Snapshot = snapshot;
        this.catalog = catalog;
        this.worker = worker;
    }

    public ArchiveSnapshot Snapshot { get; }

    public IDisposable AcquireLease()
    {
        lock (disposeGate)
        {
            if (disposalRequested) throw new ObjectDisposedException(nameof(CatalogSession));
            checked { leaseCount++; }
            return new SessionLease(this);
        }
    }

    public static async Task<CatalogSession> OpenAsync(string input, string? cacheRoot = null,
        IProgress<ArchiveProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        cancellationToken.ThrowIfCancellationRequested();
        var reader = new ArchiveReader();
        var snapshot = await Task.Run(async () =>
            await reader.OpenAsync(input, progress, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

        var worker = new SerializedWorker();
        IResourceCatalog? catalog = null;
        try
        {
            catalog = await worker.InvokeAsync(
                () => CatalogService.Open(snapshot, cacheRoot, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new CatalogSession(snapshot, catalog, worker);
        }
        catch
        {
            await worker.StopAsync(catalog is null ? null : catalog.Dispose).ConfigureAwait(false);
            throw;
        }
    }

    public Task<CatalogPage> ReadPageAsync(ResourceQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return InvokeWithTokenAsync((resourceCatalog, token) =>
        {
            token.ThrowIfCancellationRequested();
            var total = resourceCatalog.Count(query, token);
            var rows = resourceCatalog.Query(query, token);
            token.ThrowIfCancellationRequested();
            return new CatalogPage(total, rows);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ResourceRecord>> ReadMatchingAsync(ResourceQuery query,
        IProgress<ArchiveProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var unpaged = query with { Offset = 0, Limit = 10_000 };
        var total = await InvokeWithTokenAsync((resourceCatalog, token) => resourceCatalog.Count(unpaged, token), cancellationToken).ConfigureAwait(false);
        if (total > int.MaxValue) throw new InvalidOperationException("The matching selection is too large to materialize in memory.");

        var records = new List<ResourceRecord>((int)total);
        progress?.Report(new("Reading matching resources", 0, total));
        for (long offset = 0; offset < total; offset += unpaged.Limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await InvokeWithTokenAsync((resourceCatalog, token) =>
            {
                token.ThrowIfCancellationRequested();
                return resourceCatalog.Query(unpaged with { Offset = offset }, token);
            }, cancellationToken).ConfigureAwait(false);
            records.AddRange(page);
            progress?.Report(new("Reading matching resources", Math.Min(total, offset + page.Count), total));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return records.AsReadOnly();
    }

    public Task<T> InvokeAsync<T>(Func<IResourceCatalog, T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeWithTokenAsync((resourceCatalog, _) => action(resourceCatalog), cancellationToken);
    }

    public Task<T> InvokeAsync<T>(Func<IResourceCatalog, CancellationToken, T> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeWithTokenAsync(action, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate)
        {
            if (disposeTask is null)
            {
                disposalRequested = true;
                disposeTask = DisposeCoreAsync();
            }
            return new ValueTask(disposeTask);
        }
    }

    private async Task<T> InvokeWithTokenAsync<T>(Func<IResourceCatalog, CancellationToken, T> action,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        return await worker.InvokeAsync(() => action(catalog, linked.Token), linked.Token).ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        Task waitForLeases;
        lock (disposeGate)
            waitForLeases = leaseCount == 0 ? Task.CompletedTask : (leasesDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        await waitForLeases.ConfigureAwait(false);
        await lifetime.CancelAsync().ConfigureAwait(false);
        try { await worker.StopAsync(catalog.Dispose).ConfigureAwait(false); }
        finally { lifetime.Dispose(); }
    }

    private void ReleaseLease()
    {
        lock (disposeGate)
        {
            if (leaseCount <= 0) return;
            leaseCount--;
            if (disposalRequested && leaseCount == 0) leasesDrained?.TrySetResult(true);
        }
    }

    private sealed class SessionLease(CatalogSession session) : IDisposable
    {
        private CatalogSession? owner = session;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.ReleaseLease();
    }
}
