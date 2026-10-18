using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Localization.Model;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class StringsEnrichmentNavigationTests
{
    [Fact]
    public void SharedCancelStopsIndexingAndReopeningRetriesTheSameSession() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        Pump(window.OpenArchiveAsync(fixture.Index));
        var view = window.StringsBrowser;
        var progress = new ProducerGate();
        var opens = 0;
        view.LocalizationSessionFactoryOverride = (snapshot, cache, _, token, revisions) =>
        {
            opens++;
            return LocalizationSession.OpenAsync(snapshot, cache, progress, token, revisions);
        };
        try
        {
            var showing = window.OpenHomeViewAsync("strings");
            Pump(progress.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5))); Pump(showing);
            var current = Assert.IsType<LocalizationSession>(view.CurrentSession);
            Invoke(window, "RefreshActivity");
            Assert.True(((ProcessingAnimation)window.FindName("WindowProcessing")).IsActive);
            ((Button)window.FindName("WorkspaceCancelButton")).RaiseEvent(new(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            progress.Release.TrySetResult();
            Assert.ThrowsAny<OperationCanceledException>(() => Pump(current.EnrichmentTask));
            Wait(() => !view.IsActivityActive);
            Invoke(window, "RefreshActivity");
            Assert.False(((ProcessingAnimation)window.FindName("WindowProcessing")).IsActive);
            Pump(window.OpenHomeViewAsync("strings"));
            Wait(() => !view.IsActivityActive);
            Assert.Same(current, view.CurrentSession);
            Assert.Equal(1, opens);
            Assert.True(current.IsEnrichmentComplete);
            Assert.NotEmpty(view.CurrentRecords);
            var producer = current.EnrichmentTask;
            Pump(window.OpenHomeViewAsync("strings"));
            Assert.Same(producer, current.EnrichmentTask);
        }
        finally { progress.Release.TrySetResult(); }
    });

    [Theory]
    [InlineData("complete")]
    [InlineData("navigate")]
    [InlineData("selection")]
    [InlineData("cancel")]
    public void SelectedDialogueRefreshesAfterIndexingUnlessNavigationChanged(string action) => WorkspaceCommitTests.Run((window, fixture) =>
    {
        DiscoveryFixture.WriteLinkedStrings(fixture.Index);
        Pump(window.OpenArchiveAsync(fixture.Index));
        Pump(window.OpenHomeViewAsync("audio"));
        Wait(() => window.CurrentRows.Count == 1);
        var row = Assert.Single(window.CurrentRows);
        ((ListView)window.FindName("MediaList")).SelectedItem = row;
        var progress = new ProducerGate();
        window.StringsBrowser.LocalizationSessionFactoryOverride = (snapshot, cache, _, token, revisions) =>
            LocalizationSession.OpenAsync(snapshot, cache, progress, token, revisions);
        try
        {
            var lookup = window.FindSelectedAudioTextAsync();
            Pump(progress.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Pump(Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(2))));
            Assert.True(lookup.IsCompleted, "Selected dialogue waited for global indexing.");
            Pump(lookup);
            var early = Assert.IsType<LocalizationAudioLookupResult>(window.AudioDialogueBrowser.CurrentResult);
            Assert.False(early.IsComplete);
            var current = Assert.IsType<LocalizationSession>(window.StringsBrowser.CurrentSession);
            if (action == "navigate") Pump(window.OpenHomeViewAsync("home"));
            if (action == "selection") ((ListView)window.FindName("MediaList")).SelectedItem = null;
            if (action == "cancel")
            {
                var cancel = (Button)window.FindName("WorkspaceCancelButton");
                Assert.Equal(System.Windows.Visibility.Visible, cancel.Visibility);
                cancel.RaiseEvent(new(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }
            progress.Release.TrySetResult();
            if (action == "cancel")
            {
                Assert.ThrowsAny<OperationCanceledException>(() => Pump(current.EnrichmentTask));
                Wait(() => ((TextBlock)window.AudioDialogueBrowser.FindName("Status")).Text.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
                Assert.Same(early, window.AudioDialogueBrowser.CurrentResult);
            }
            else Pump(current.EnrichmentTask);
            if (action == "navigate")
            {
                Pump(Task.Delay(400));
                Assert.Same(early, window.AudioDialogueBrowser.CurrentResult);
            }
            else if (action == "selection")
            {
                Pump(Task.Delay(400));
                Assert.Null(window.AudioDialogueBrowser.CurrentResult);
            }
            else if (action == "complete")
            {
                Pump(WaitForCompletedResult());
                Assert.True(window.AudioDialogueBrowser.CurrentResult?.IsComplete == true,
                    "The selected dialogue remained incomplete after indexing finished.");
                Assert.Single(window.AudioDialogueBrowser.CurrentResult!.Occurrences);
            }
            async Task WaitForCompletedResult()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { while (window.AudioDialogueBrowser.CurrentResult?.IsComplete != true) await Task.Delay(10, timeout.Token); }
                catch (OperationCanceledException) { }
            }
        }
        finally { progress.Release.TrySetResult(); }
    });

    private sealed class ProducerGate : IProgress<LocalizationReadProgress>
    {
        private int entered;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(LocalizationReadProgress progress)
        {
            if (progress.Phase != "Reading localization" || Interlocked.Exchange(ref entered, 1) != 0) return;
            Entered.TrySetResult();
            Release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
    }
    private static void Wait(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Pump(Core());
        async Task Core() { while (!done()) await Task.Delay(10, timeout.Token); }
    }
    private static void Invoke(object owner, string method) => owner.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, null);
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
}
