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
