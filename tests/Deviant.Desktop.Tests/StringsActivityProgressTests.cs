using System.Windows.Threading;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Desktop;
using Deviant.Formats.Localization.Model;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class StringsActivityProgressTests
{
    [Fact]
    public void LoadingPublishesActivityAndCancelDrainsTheOwnedLoad()
    {
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            Pump(window.OpenArchiveAsync(fixture.Index));
            var view = window.StringsBrowser;
            var changes = new List<WorkspaceActivityProgress>();
            view.ActivityProgressChanged += (_, progress) => changes.Add(progress);

            var loading = view.LoadAsync(window.CurrentSession!.Snapshot, fixture.Cache);
            Assert.True(view.IsActivityActive);
            Assert.Equal("Opening strings", view.ActivityProgress.Stage);
            view.CancelActivity();
            try { Pump(loading); }
            catch (OperationCanceledException) { }

            if (view.CurrentSession is { } current)
            {
                try { Pump(current.EnrichmentTask); }
                catch (OperationCanceledException) { }
                Pump(view.RefreshAsync());
            }
            var drain = typeof(StringsView).GetMethod("DrainRelatedAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Pump((Task)drain.Invoke(view, null)!);
            Pump(Task.Delay(50));
            Assert.Contains(changes, progress => progress.IsActive && progress.Stage == "Opening strings");
            Assert.False(view.IsActivityActive);
            Assert.Contains(view.ActivityProgress.Stage, new[] { "Cancelled", "Ready" });
        });
    }

    [Fact]
    public void CancellingViewRequestsDoesNotReportActiveLocalizationEnrichmentAsFinished()
    {
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            Pump(window.OpenArchiveAsync(fixture.Index));
            var view = window.StringsBrowser;
            var enteredGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var progress = new BlockingLocalizationProgress(enteredGate, releaseGate);
            var factory = new Func<ArchiveSnapshot,string,IProgress<LocalizationReadProgress>?,CancellationToken,ISourceRevisionProvider?,Task<LocalizationSession>>(
                (snapshot, cache, _, token, revisions) => Task.Run(
                    () => LocalizationSession.OpenAsync(snapshot, cache, progress, token, revisions), token));
            typeof(StringsView).GetProperty("LocalizationSessionFactoryOverride", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(view, factory);

            try
            {
                var loading = view.LoadAsync(window.CurrentSession!.Snapshot, fixture.Cache);
                Pump(enteredGate.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                Pump(loading);
                var current = Assert.IsType<LocalizationSession>(view.CurrentSession);
                Assert.False(current.EnrichmentTask.IsCompleted);
                Assert.True(view.IsActivityActive);
                Assert.Equal("Indexing strings", view.ActivityProgress.Stage);

                var query = view.RefreshAsync();
                view.CancelViewRequests();
                Pump(query);
                Assert.True(view.IsActivityActive);
                Assert.Equal("Indexing strings", view.ActivityProgress.Stage);
                Assert.False(current.EnrichmentTask.IsCanceled);

                releaseGate.TrySetResult();
                Pump(current.EnrichmentTask);
                Pump(view.RefreshAsync());
                var drain = typeof(StringsView).GetMethod("DrainRelatedAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                Pump((Task)drain.Invoke(view, null)!);
                var debounce = (DispatcherTimer)typeof(StringsView).GetField("debounce", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
                WaitUntil(() => !view.IsActivityActive && !debounce.IsEnabled, TimeSpan.FromSeconds(5));
                Assert.False(view.IsActivityActive);
            }
            finally { releaseGate.TrySetResult(); }
        });
    }

    [Fact]
    public void LateOlderCancellationDoesNotReplaceNewerSuccessfulActivity()
    {
        WorkspaceCommitTests.Run((window, _) =>
        {
            var view = window.StringsBrowser;
            var olderQuery = BeginActivity(view, "Searching strings", 60);
            var newerDetails = BeginActivity(view, "Loading details", 40);
            EndActivity(view, newerDetails);
            EndActivity(view, olderQuery, "Cancelled");

            Assert.False(view.IsActivityActive);
            Assert.Equal("Ready", view.ActivityProgress.Stage);
        });
    }

    [Fact]
    public void LateOlderCancellationDoesNotReplaceNewerFailure()
    {
        WorkspaceCommitTests.Run((window, _) =>
        {
            var view = window.StringsBrowser;
            var olderDetails = BeginActivity(view, "Loading details", 40);
            var newerQuery = BeginActivity(view, "Searching strings", 60);
            EndActivity(view, newerQuery, "Failed");
            EndActivity(view, olderDetails, "Cancelled");

            Assert.False(view.IsActivityActive);
            Assert.Equal("Failed", view.ActivityProgress.Stage);
        });
    }

    private sealed class BlockingLocalizationProgress(TaskCompletionSource entered, TaskCompletionSource release) : IProgress<LocalizationReadProgress>
    {
        private int blocked;

        public void Report(LocalizationReadProgress value)
        {
            if (value.Phase != "Reading localization" || Interlocked.Exchange(ref blocked, 1) != 0) return;
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do { Pump(Task.Delay(20)); }
        while (!condition() && DateTime.UtcNow < deadline);
        Assert.True(condition(), "Strings view activity did not drain before timeout.");
    }

    private static long BeginActivity(StringsView view, string stage, int priority) =>
        (long)typeof(StringsView).GetMethod("BeginActivity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(view, [stage, priority, 0L, 0L])!;

    private static void EndActivity(StringsView view, long id, string? terminal = null) =>
        typeof(StringsView).GetMethod("EndActivity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(view, [id, terminal]);
}
