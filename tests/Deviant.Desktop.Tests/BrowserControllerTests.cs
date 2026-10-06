using Deviant.Browser;
using System.IO;
using Deviant.Core;
using Deviant.Export;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class BrowserControllerTests
{
    [Fact]
    public async Task SelectionSurvivesFiltersAndPagesButChangesWithSourceRevision()
    {
        var first = new FakeSession(Snapshot("one", "v1")); var revised = new FakeSession(Snapshot("one", "v2")); var opens = 0;
        await using var controller = Controller((_, _, _, _) => Task.FromResult<IBrowserCatalogSession>(opens++ == 0 ? first : revised));
        await controller.OpenAsync("one.idx"); controller.SetSelected(1, true);
        await controller.QueryAsync(new(Type: 99, Offset: 1, Limit: 100)); Assert.Contains(1L, controller.State.Selected);
        controller.SelectPage(); Assert.Contains(2L, controller.State.Selected);
        await controller.RefreshAsync(); Assert.Empty(controller.State.Selected); Assert.Equal(1, first.DisposeCount);
    }
    [Fact]
    public async Task FailedAndCancelledOpenKeepTheCurrentCatalogAndSelection()
    {
        var existing = new FakeSession(Snapshot("one")); var blocked = new TaskCompletionSource<IBrowserCatalogSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var controller = Controller((input, _, _, token) => input switch
        {
            "one.idx" => Task.FromResult<IBrowserCatalogSession>(existing),
            "bad.idx" => Task.FromException<IBrowserCatalogSession>(new IOException("Synthetic open failure.")),
            _ => blocked.Task.WaitAsync(token)
        });
        await controller.OpenAsync("one.idx"); controller.SetSelected(1, true); var rows = controller.State.Rows;
        await controller.OpenAsync("bad.idx"); Assert.Same(rows, controller.State.Rows); Assert.Contains(1L, controller.State.Selected);
        var pending = controller.OpenAsync("slow.idx"); controller.CancelOpen(); await pending;
        Assert.Same(rows, controller.State.Rows); Assert.Contains(1L, controller.State.Selected); Assert.False(controller.State.IsOpening);
    }
    [Fact]
    public async Task SupersededFilterCannotPublishRowsEvenIfBackendIgnoresCancellation()
    {
        var slow = new TaskCompletionSource<CatalogPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession(Snapshot("one"));
        session.Query = (query, _) => query.Search == "slow" ? slow.Task : Task.FromResult(new CatalogPage(1, [session.Snapshot.Records[2]]));
        await using var controller = Controller((_, _, _, _) => Task.FromResult<IBrowserCatalogSession>(session));
        await controller.OpenAsync("one.idx"); var old = controller.QueryAsync(new(Search: "slow"));
        await controller.QueryAsync(new(Search: "fast")); slow.SetResult(new(1, [session.Snapshot.Records[0]])); await old;
        Assert.Equal(2, Assert.Single(controller.State.Rows).Ordinal); Assert.Equal("fast", controller.State.Query.Search); Assert.False(controller.State.IsQuerying);
    }
    [Fact]
    public async Task OldOpenCannotReplaceNewSourceAndItsSessionIsDisposed()
    {
        var slow = new TaskCompletionSource<IBrowserCatalogSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = new FakeSession(Snapshot("old")); var current = new FakeSession(Snapshot("new"));
        await using var controller = Controller((input, _, _, _) =>
        {
            if (input != "old.idx") return Task.FromResult<IBrowserCatalogSession>(current);
            entered.TrySetResult(); return slow.Task;
        });
        var oldOpen = controller.OpenAsync("old.idx"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.OpenAsync("new.idx"); slow.SetResult(stale); await oldOpen;
        Assert.Equal(current.Snapshot, controller.State.Snapshot); Assert.Equal(1, stale.DisposeCount);
    }
    [Fact]
    public async Task QueryContinuesOnCurrentCatalogWhileReplacementOpenFails()
    {
        var query = new TaskCompletionSource<CatalogPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession(Snapshot("one"));
        await using var controller = Controller((input, _, _, _) => input == "one.idx" ? Task.FromResult<IBrowserCatalogSession>(session) : Task.FromException<IBrowserCatalogSession>(new IOException("Unavailable.")));
        await controller.OpenAsync("one.idx"); session.Query = (_, _) => query.Task;
        var pending = controller.QueryAsync(new(Search: "kept")); await controller.OpenAsync("bad.idx"); query.SetResult(new(1, [session.Snapshot.Records[1]])); await pending;
        Assert.Equal(1, Assert.Single(controller.State.Rows).Ordinal); Assert.False(controller.State.IsQuerying);
    }
    [Fact]
    public async Task ExportReviewKeepsSourceAndSelectionFrozenWhileBrowsingChanges()
    {
        var first = new FakeSession(Snapshot("one")); var second = new FakeSession(Snapshot("two")); var exporter = new FakeExports();
        await using var controller = Controller((input, _, _, _) => Task.FromResult<IBrowserCatalogSession>(input == "one.idx" ? first : second), exporter);
        await controller.OpenAsync("one.idx"); controller.SetSelected(0, true); var review = await controller.PrepareExportAsync(ExportScope.Selected);
        Assert.NotNull(review); await controller.OpenAsync("two.idx"); controller.SetSelected(2, true);
        await controller.ExportAsync(review, new(Path.GetTempPath()));
        Assert.Same(first.Snapshot, exporter.LastPlan!.Snapshot); Assert.Equal(0, Assert.Single(exporter.LastPlan.Items).Resource.Ordinal);
        Assert.Equal(second.Snapshot, controller.State.Snapshot);
    }
    [Fact]
    public async Task MatchingPreparationHoldsOldSessionLeaseUntilItsFrozenReviewCompletes()
    {
        var matching = new TaskCompletionSource<IReadOnlyList<ResourceRecord>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeSession(Snapshot("one")) { Matching = (_, _) => matching.Task };
        var second = new FakeSession(Snapshot("two"));
        await using var controller = Controller((input, _, _, _) => Task.FromResult<IBrowserCatalogSession>(input == "one.idx" ? first : second));
        await controller.OpenAsync("one.idx");
        var preparation = controller.PrepareExportAsync(ExportScope.Matching);
        Assert.Equal(1, first.Leases);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.StateChanged += (_, _) => { if (ReferenceEquals(second.Snapshot, controller.State.Snapshot)) accepted.TrySetResult(); };
        var switching = controller.OpenAsync("two.idx");
        try
        {
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(second.Snapshot, controller.State.Snapshot);
            Assert.False(switching.IsCompleted);
        }
        finally { matching.TrySetResult(first.Snapshot.Records); }
        var review = await preparation; await switching;
        Assert.Same(first.Snapshot, review!.Snapshot);
        Assert.Equal(0, first.Leases); Assert.Equal(1, first.DisposeCount);
    }
    [Fact]
    public async Task ConcurrentJobsAreRejectedBeforeTheBusyStateReachesTheDispatcher()
    {
        var dispatcher = new DelayedDispatcher(); var session = new FakeSession(Snapshot("one")); var exports = new FakeExports();
        await using var controller = new BrowserController(dispatcher, sessionFactory: (_, _, _, _) => Task.FromResult<IBrowserCatalogSession>(session), exportService: exports);
        await controller.OpenAsync("one.idx"); controller.SelectPage(); dispatcher.DelayNext();
        var first = controller.PrepareExportAsync(ExportScope.Selected);
        Assert.False(controller.State.IsExporting);
        Assert.Null(await controller.PrepareExportAsync(ExportScope.Matching));
        await controller.ExportAsync(new(session.Snapshot, session.Snapshot.Records, 9, 0), new(Path.GetTempPath()));
        Assert.Null(exports.LastPlan);
        dispatcher.Release(); var review = await first;
        Assert.NotNull(review); await controller.ExportAsync(review, new(Path.GetTempPath())); Assert.NotNull(exports.LastPlan);
    }
    [Fact]
    public async Task NativeStyleCancellationCallbackRunsOffCallerAndClosingDrainsIt()
    {
        var session = new FakeSession(Snapshot("one"));
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var backend = new TaskCompletionSource<CatalogPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = Controller((_, _, _, _) => Task.FromResult<IBrowserCatalogSession>(session));
        await controller.OpenAsync("one.idx");
        var caller = Environment.CurrentManagedThreadId; var callbackThread = caller;
        CancellationTokenRegistration registration = default;
        session.Query = (query, token) =>
        {
            if (query.Search != "slow") return Task.FromResult(new CatalogPage(1, [session.Snapshot.Records[2]]));
            registration = token.Register(() => { callbackThread = Environment.CurrentManagedThreadId; entered.Set(); release.Wait(); });
            return backend.Task;
        };
        var slow = controller.QueryAsync(new(Search: "slow"));
        Task<bool>? closing = null;
        try
        {
            await controller.QueryAsync(new(Search: "fast")).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotEqual(caller, callbackThread);
            Assert.Equal(2, Assert.Single(controller.State.Rows).Ordinal);
            closing = controller.CloseAsync(); Assert.False(closing.IsCompleted);
        }
        finally
        {
            release.Set(); backend.TrySetResult(new(1, [session.Snapshot.Records[0]])); registration.Dispose();
            await slow; if (closing is not null) await closing; await controller.DisposeAsync();
        }
    }
    [Fact]
    public async Task ClosingCancelsExportDrainsManifestAndDisposesSession()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var session = new FakeSession(Snapshot("one"));
        var exporter = new FakeExports { Run = async (plan, _, token) => { started.SetResult(); try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { } return Report(plan, true); } };
        var controller = Controller((_, _, _, _) => Task.FromResult<IBrowserCatalogSession>(session), exporter);
        await controller.OpenAsync("one.idx"); controller.SetSelected(0, true); var review = (await controller.PrepareExportAsync(ExportScope.Selected))!;
        var export = controller.ExportAsync(review, new(Path.GetTempPath())); await started.Task;
        Assert.True(await controller.CloseAsync()); await export;
        Assert.True(controller.State.LastExport!.Manifest.Cancelled); Assert.Equal(1, session.DisposeCount); Assert.False(controller.State.IsExporting);
    }
    [Fact]
    public async Task ManifestFailureRetainsReportAndRequiresAcknowledgementBeforeClosing()
    {
        var session = new FakeSession(Snapshot("one")); var exporter = new FakeExports { Run = (p, _, _) => Task.FromException<ExportReport>(new ManifestWriteException(Report(p, false).Manifest, "report.json", new IOException("Synthetic storage failure."))) };
        var controller = Controller((_, _, _, _) => Task.FromResult<IBrowserCatalogSession>(session), exporter);
        await controller.OpenAsync("one.idx"); controller.SetSelected(0, true); var review = (await controller.PrepareExportAsync(ExportScope.Selected))!;
        await controller.ExportAsync(review, new(Path.GetTempPath())); Assert.NotNull(controller.State.RetainedManifest); Assert.False(controller.State.CanExport);
        Assert.False(await controller.CloseAsync()); Assert.Equal(0, session.DisposeCount); Assert.True(await controller.CloseAsync(true)); Assert.Equal(1, session.DisposeCount);
    }
    [Fact]
    public async Task SourceChangedExportRequiresRefreshOfAffectedCurrentRevision()
    {
        var session = new FakeSession(Snapshot("one")); var exporter = new FakeExports { Run = (p, _, _) => Task.FromResult(Report(p, false, "SourceChanged")) };
        await using var controller = Controller((_, _, _, _) => Task.FromResult<IBrowserCatalogSession>(session), exporter);
        await controller.OpenAsync("one.idx"); controller.SelectPage(); var review = (await controller.PrepareExportAsync(ExportScope.Selected))!;
        await controller.ExportAsync(review, new(Path.GetTempPath())); Assert.True(controller.State.NeedsRefresh); Assert.False(controller.State.CanExport);
    }
    private static BrowserController Controller(Func<string, string?, IProgress<ArchiveProgress>?, CancellationToken, Task<IBrowserCatalogSession>> factory, IExportService? exports = null)
        => new(new ImmediateDispatcher(), sessionFactory: factory, exportService: exports);
    private sealed class ImmediateDispatcher : IUiDispatcher { public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; } }
    private sealed class DelayedDispatcher : IUiDispatcher
    {
        private bool delay;
        private Action? pending;
        private TaskCompletionSource? completion;
        public void DelayNext() => delay = true;
        public Task InvokeAsync(Action action)
        {
            if (!delay) { action(); return Task.CompletedTask; }
            delay = false; pending = action; completion = new(TaskCreationOptions.RunContinuationsAsynchronously); return completion.Task;
        }
        public void Release() { pending!(); completion!.SetResult(); }
    }
    private sealed class FakeSession(ArchiveSnapshot snapshot) : IBrowserCatalogSession
    {
        public ArchiveSnapshot Snapshot { get; } = snapshot;
        public int DisposeCount { get; private set; }
        public int Leases { get; private set; }
        private readonly TaskCompletionSource leaseReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<ResourceQuery, CancellationToken, Task<CatalogPage>>? Query { get; set; }
        public Func<ResourceQuery, CancellationToken, Task<IReadOnlyList<ResourceRecord>>>? Matching { get; init; }
        public Task<CatalogPage> ReadPageAsync(ResourceQuery q, CancellationToken t = default) => Query?.Invoke(q, t) ?? Task.FromResult(new CatalogPage(Snapshot.Records.Count, Snapshot.Records.Skip((int)q.Offset).Take(q.Limit).ToArray()));
        public Task<IReadOnlyList<ResourceRecord>> ReadMatchingAsync(ResourceQuery q, IProgress<ArchiveProgress>? progress = null, CancellationToken t = default) => Matching?.Invoke(q, t) ?? Task.FromResult(Snapshot.Records);
        public IDisposable AcquireLease() { Leases++; return new Release(() => { if (--Leases == 0) leaseReleased.TrySetResult(); }); }
        public async ValueTask DisposeAsync() { if (Leases > 0) await leaseReleased.Task; DisposeCount++; }
        private sealed class Release(Action action) : IDisposable { public void Dispose() => action(); }
    }
    private sealed class FakeExports : IExportService
    {
        public ExportPlan? LastPlan { get; private set; }
        public Func<ExportPlan, IProgress<ExportProgress>?, CancellationToken, Task<ExportReport>>? Run { get; init; }
        public ExportPlan Plan(ArchiveSnapshot snapshot, ExportOptions options, IEnumerable<ResourceRecord> selected) => LastPlan = new(snapshot, options, selected.Select(r => new ExportItem(r, $"r{r.Ordinal}.bin")).ToArray());
        public Task<ExportReport> RunAsync(ExportPlan plan, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default) => Run?.Invoke(plan, progress, cancellationToken) ?? Task.FromResult(Report(plan, false));
    }
    private static ExportReport Report(ExportPlan plan, bool cancelled, string? error = null) => new(new(1, "synthetic.idx", plan.Snapshot.IndexContentId,
        plan.Snapshot.PhysicalSourceStamp, "Synthetic", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, cancelled,
        plan.Items.Select(i => new ExportEntry(i.Resource.Ordinal, i.Resource.Type, i.Resource.Id, i.Resource.Package, i.Resource.VolumeName,
            i.Resource.Offset, i.Resource.StoredSize, i.RelativeOutput, error is not null ? ExportStatus.Failed : cancelled ? ExportStatus.Cancelled : ExportStatus.Completed, ErrorCode: error)).ToArray()), "report.json");
    private static ArchiveSnapshot Snapshot(string key, string stamp = "v1") => new(Path.Combine(Path.GetTempPath(), key + ".idx"), key, 18, 0, new byte[73], 3,
        new[] { new ResourceRecord(0, 2, 7, 1, 0, 0, 3, 0, "synthetic.dat"), new ResourceRecord(1, 2, 7, 2, 0, 3, 3, 0, "synthetic.dat"), new ResourceRecord(2, 99, 1, 3, 1, 0, 3, 0, "synthetic.d01") },
        [new(0, "synthetic.dat", "synthetic.dat", new(true, "v0", 6, 1, 1, true)), new(1, "synthetic.d01", "synthetic.d01", new(true, "v1", 3, 1, 1, true))],
        new(true, "i", 189, 1, 1, true), stamp, []);
}
