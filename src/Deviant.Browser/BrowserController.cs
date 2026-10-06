using Deviant.Core;
using Deviant.Export;

namespace Deviant.Browser;

/// <summary>Coordinates metadata-only browser operations; observable state is published through the UI dispatcher.</summary>
public sealed class BrowserController : IBrowserController
{
    private readonly IUiDispatcher dispatcher;
    private readonly string? cacheRoot;
    private readonly Func<string, string?, IProgress<ArchiveProgress>?, CancellationToken, Task<IBrowserCatalogSession>> factory;
    private readonly IExportService exports;
    private readonly object activityGate = new();
    private readonly HashSet<Task> activities = [];
    private readonly CancellationTokenSource lifetime = new();
    private IBrowserCatalogSession? session;
    private CancellationTokenSource? openStop, queryStop, exportStop;
    private long sourceGeneration, queryGeneration, exportGeneration;
    private int exportActive;
    private bool disposed;
    private BrowserState state = new();
    public BrowserState State => Volatile.Read(ref state);
    public event EventHandler? StateChanged;

    public BrowserController(IUiDispatcher dispatcher, string? cacheRoot = null,
        Func<string, string?, IProgress<ArchiveProgress>?, CancellationToken, Task<IBrowserCatalogSession>>? sessionFactory = null,
        IExportService? exportService = null)
    {
        this.dispatcher = dispatcher; this.cacheRoot = cacheRoot; exports = exportService ?? new RawExportService();
        factory = sessionFactory ?? (async (input, cache, progress, token) => await CatalogSession.OpenAsync(input, cache, progress, token));
    }
    public Task OpenAsync(string input, CancellationToken cancellationToken = default) => Track(OpenCoreAsync(input, cancellationToken));
    public Task RefreshAsync(CancellationToken cancellationToken = default) => State.InputPath is { } path ? OpenAsync(path, cancellationToken) : Task.CompletedTask;
    public Task QueryAsync(ResourceQuery query, CancellationToken cancellationToken = default) => Track(QueryCoreAsync(query, cancellationToken));

    private async Task OpenCoreAsync(string input, CancellationToken external)
    {
        if (disposed || State.IsClosing) return;
        var generation = Interlocked.Increment(ref sourceGeneration);
        RequestCancel(openStop); var stop = CancellationTokenSource.CreateLinkedTokenSource(external, lifetime.Token); openStop = stop;
        await Update(s => s with { IsOpening = true, Error = null, ArchiveProgress = null, Status = "Opening…" });
        IBrowserCatalogSession? candidate = null;
        using var progress = new CoalescedProgress<ArchiveProgress>(p => Post(s => generation == sourceGeneration && s.IsOpening && !s.IsClosing ? s with { ArchiveProgress = p } : s));
        try
        {
            candidate = await Task.Run(() => factory(input, cacheRoot, progress, stop.Token), stop.Token);
            var navigation = await Task.Run(() => NavigationBuilder.Build(candidate.Snapshot, stop.Token), stop.Token);
            var refreshing = State.InputPath is not null && string.Equals(Path.GetFullPath(State.InputPath), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase);
            var query = refreshing ? State.Query with { Offset = 0 } : new ResourceQuery(Limit: State.Query.Limit);
            var page = await candidate.ReadPageAsync(query, stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            IBrowserCatalogSession? previous = null; var accepted = false;
            await dispatcher.InvokeAsync(() =>
            {
                if (generation != sourceGeneration || State.IsClosing || stop.IsCancellationRequested) return;
                previous = session; session = candidate; accepted = true;
                Interlocked.Increment(ref queryGeneration); RequestCancel(queryStop);
                var sameRevision = State.InputPath is not null && string.Equals(Path.GetFullPath(State.InputPath), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase) &&
                    State.Snapshot?.IndexContentId == candidate.Snapshot.IndexContentId && State.Snapshot.PhysicalSourceStamp == candidate.Snapshot.PhysicalSourceStamp;
                Publish(State with
                {
                    InputPath = input, Snapshot = candidate.Snapshot, Navigation = navigation, Query = query, Rows = page.Rows,
                    Total = page.Total, Focused = null, Selected = sameRevision ? State.Selected : new HashSet<long>(),
                    NeedsRefresh = false, IsOpening = false, IsQuerying = false, ArchiveProgress = null, Error = null,
                    Status = "Ready"
                });
            });
            if (accepted)
            {
                candidate = null;
                if (previous is not null) await previous.DisposeAsync();
            }
        }
        catch (OperationCanceledException)
        { if (generation == sourceGeneration) await Update(s => s with { IsOpening = false, ArchiveProgress = null, Status = "Cancelled" }); }
        catch (Exception ex)
        { if (generation == sourceGeneration) await Update(s => s with { IsOpening = false, ArchiveProgress = null, Error = ex.Message, Status = "Open failed" }); }
        finally
        {
            if (candidate is not null) await candidate.DisposeAsync();
            if (ReferenceEquals(openStop, stop)) openStop = null; stop.Dispose();
        }
    }

    private async Task QueryCoreAsync(ResourceQuery query, CancellationToken external)
    {
        if (disposed || State.IsClosing || session is null) return;
        var current = session; var generation = Interlocked.Increment(ref queryGeneration);
        RequestCancel(queryStop); var stop = CancellationTokenSource.CreateLinkedTokenSource(external, lifetime.Token); queryStop = stop;
        await Update(s => s with { Query = query, IsQuerying = true, Error = null, Status = "Loading…" });
        try
        {
            var page = await current.ReadPageAsync(query, stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            await Update(s => generation == queryGeneration && ReferenceEquals(session, current) && !s.IsClosing
                ? s with { Rows = page.Rows, Total = page.Total, Focused = null, IsQuerying = false, Status = page.Total == 0 ? "No matches" : "Ready" } : s);
        }
        catch (OperationCanceledException)
        { if (generation == queryGeneration) await Update(s => s with { IsQuerying = false }); }
        catch (ObjectDisposedException) when (!ReferenceEquals(session, current)) { }
        catch (Exception ex)
        { if (generation == queryGeneration && ReferenceEquals(session, current)) await Update(s => s with { IsQuerying = false, Error = ex.Message, Status = "The query failed." }); }
        finally { if (ReferenceEquals(queryStop, stop)) queryStop = null; stop.Dispose(); }
    }

    public void SetSelected(long ordinal, bool selected) => UpdateSelection(selected ? [ordinal] : [], selected ? [] : [ordinal]);
    public void UpdateSelection(IEnumerable<long> added, IEnumerable<long> removed)
    {
        var additions = added.ToArray(); var removals = removed.ToArray();
        Post(s =>
        {
            if (s.Snapshot is null || s.IsClosing) return s;
            var set = new HashSet<long>(s.Selected);
            foreach (var ordinal in removals) set.Remove(ordinal);
            foreach (var ordinal in additions)
                if (ordinal >= 0 && ordinal < s.Snapshot.Records.Count) set.Add(ordinal);
            return s with { Selected = set };
        });
    }
    public void SelectPage() => Post(s => s with { Selected = new HashSet<long>(s.Selected.Concat(s.Rows.Select(r => r.Ordinal))) });
    public void ClearSelection() => Post(s => s with { Selected = new HashSet<long>() });
    public void Focus(ResourceRecord? record) => Post(s => s with { Focused = record });
    public void CancelOpen() { RequestCancel(openStop); Post(s => s with { Status = "Cancelling open…" }); }
    public void CancelExport() { RequestCancel(exportStop); Post(s => s with { Status = "Cancelling export…" }); }

    public Task<ExportReview?> PrepareExportAsync(ExportScope scope, CancellationToken cancellationToken = default) => TrackResult(PrepareCoreAsync(scope, cancellationToken));
    private async Task<ExportReview?> PrepareCoreAsync(ExportScope scope, CancellationToken external)
    {
        if (!State.CanExport || session is null || Interlocked.CompareExchange(ref exportActive, 1, 0) != 0) return null;
        var current = session; var snapshot = current.Snapshot; var query = State.Query; var selected = State.Selected.ToArray();
        IDisposable? sessionLease = null;
        var generation = Interlocked.Increment(ref exportGeneration);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(external, lifetime.Token); exportStop = stop;
        await Update(s => s with { IsExporting = true, Error = null, Status = "Preparing selection…", ExportProgress = null });
        using var progress = new CoalescedProgress<ArchiveProgress>(p => Post(s => generation == exportGeneration && s.IsExporting ? s with { Status = $"{p.Phase}: {p.Completed:N0}/{p.Total:N0}" } : s));
        try
        {
            sessionLease = current.AcquireLease();
            var records = scope == ExportScope.Matching
                ? await current.ReadMatchingAsync(query with { Offset = 0 }, progress, stop.Token)
                : await Task.Run<IReadOnlyList<ResourceRecord>>(() =>
                {
                    var rows = new List<ResourceRecord>(selected.Length);
                    foreach (var ordinal in selected.Order()) { stop.Token.ThrowIfCancellationRequested(); rows.Add(snapshot.Records[(int)ordinal]); }
                    return rows;
                }, stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            if (records.Count == 0) throw new ArchiveException("EmptySelection", "No resources were selected.");
            var bytes = await Task.Run(() => records.Sum(r => r.StoredSize), stop.Token);
            return new(snapshot, records, bytes, records.LongCount(r => r.Validity != ResourceValidity.Valid));
        }
        catch (OperationCanceledException) { await Update(s => s with { Status = "Cancelled" }); return null; }
        catch (Exception ex) { await Update(s => s with { Error = ex.Message, Status = "Could not prepare the export." }); return null; }
        finally
        {
            sessionLease?.Dispose();
            if (ReferenceEquals(exportStop, stop)) exportStop = null; stop.Dispose();
            try { await Update(s => s with { IsExporting = false }); }
            finally { Volatile.Write(ref exportActive, 0); }
        }
    }

    public Task ExportAsync(ExportReview review, ExportOptions options, CancellationToken cancellationToken = default) => Track(ExportCoreAsync(review, options, cancellationToken));
    private async Task ExportCoreAsync(ExportReview review, ExportOptions options, CancellationToken external)
    {
        if (disposed || State.IsExporting || State.IsClosing || Interlocked.CompareExchange(ref exportActive, 1, 0) != 0) return;
        var generation = Interlocked.Increment(ref exportGeneration);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(external, lifetime.Token); exportStop = stop;
        await Update(s => s with { IsExporting = true, Error = null, LastExport = null, ReportSourceRoot = review.Snapshot.SourceRoot,
            RetainedManifest = null, ExportProgress = null, Status = "Preparing export…" });
        using var prep = new CoalescedProgress<ArchiveProgress>(p => Post(s => generation == exportGeneration && s.IsExporting ? s with { Status = $"{p.Phase}: {p.Completed:N0}/{p.Total:N0}" } : s));
        using var progress = new CoalescedProgress<ExportProgress>(p => Post(s => generation == exportGeneration && s.IsExporting ? s with { ExportProgress = p, Status = $"Exporting — {p.CompletedFiles:N0}/{p.TotalFiles:N0} completed." } : s));
        try
        {
            var plan = await Task.Run(() => exports.Plan(review.Snapshot, options, review.Records, stop.Token, prep), stop.Token);
            var report = await Task.Run(() => exports.RunAsync(plan, progress, stop.Token));
            var stale = report.Manifest.Entries.Any(e => e.ErrorCode == "SourceChanged");
            await Update(s => s with
            {
                LastExport = report, NeedsRefresh = s.NeedsRefresh || (stale && s.Snapshot?.IndexContentId == review.Snapshot.IndexContentId && s.Snapshot.PhysicalSourceStamp == review.Snapshot.PhysicalSourceStamp),
                Error = report.Manifest.Entries.FirstOrDefault(e => e.Status == ExportStatus.Failed)?.Error,
                Status = $"{(report.Manifest.Cancelled ? "Cancelled" : "Finished")} — {report.Manifest.Entries.Count(e => e.Status == ExportStatus.Completed):N0} completed, {report.Manifest.Entries.Count(e => e.Status == ExportStatus.Failed):N0} failed, {report.Manifest.Entries.Count(e => e.Status == ExportStatus.Cancelled):N0} cancelled, {report.Manifest.Entries.Count(e => e.Status == ExportStatus.Skipped):N0} skipped."
            });
        }
        catch (ManifestWriteException ex) { await Update(s => s with { RetainedManifest = ex.Manifest, Error = "Report not saved", Status = "Report not saved" }); }
        catch (OperationCanceledException) { await Update(s => s with { Status = "Cancelled" }); }
        catch (Exception ex) { await Update(s => s with { Error = ex.Message, Status = "Export failed" }); }
        finally
        {
            if (ReferenceEquals(exportStop, stop)) exportStop = null; stop.Dispose();
            try { await Update(s => s with { IsExporting = false }); }
            finally { Volatile.Write(ref exportActive, 0); }
        }
    }

    public async Task<bool> CloseAsync(bool acknowledgeManifestFailure = false)
    {
        if (disposed) return true;
        await Update(s => s with { IsClosing = true, Status = "Closing…" });
        RequestCancel(lifetime); RequestCancel(openStop); RequestCancel(queryStop); RequestCancel(exportStop);
        Task[] pending; lock (activityGate) pending = activities.ToArray();
        await Task.WhenAll(pending);
        if (State.RetainedManifest is not null && !acknowledgeManifestFailure)
        { await Update(s => s with { IsClosing = false, Error = "Report not saved" }); return false; }
        var previous = session; session = null; if (previous is not null) await previous.DisposeAsync();
        disposed = true; lifetime.Dispose(); return true;
    }
    public async ValueTask DisposeAsync() { await CloseAsync(acknowledgeManifestFailure: true); }
    private Task Update(Func<BrowserState, BrowserState> update) => dispatcher.InvokeAsync(() => Publish(update(State)));
    private void RequestCancel(CancellationTokenSource? source)
    {
        if (source is not null) _ = Track(CancelCallbacksAsync(source));
    }
    private static async Task CancelCallbacksAsync(CancellationTokenSource source)
    {
        // Native I/O and SQLite callbacks may wait on their owner; never execute them on the UI thread.
        try { await source.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* The operation already drained. */ }
    }
    private void Post(Func<BrowserState, BrowserState> update) { if (!disposed) _ = Update(update); }
    private void Publish(BrowserState next)
    {
        if (ReferenceEquals(state, next)) return;
        Volatile.Write(ref state, next); StateChanged?.Invoke(this, EventArgs.Empty);
    }
    private Task Track(Task task) { Register(task); return task; }
    private Task<T> TrackResult<T>(Task<T> task) { Register(task); return task; }
    private void Register(Task task)
    {
        lock (activityGate) activities.Add(task);
        _ = task.ContinueWith(t => { lock (activityGate) activities.Remove(t); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
