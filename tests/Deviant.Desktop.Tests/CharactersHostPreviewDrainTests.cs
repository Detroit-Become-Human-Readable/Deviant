using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class CharactersHostPreviewDrainTests
{
    [Theory]
    [InlineData("workspace")]
    [InlineData("cache")]
    [InlineData("close")]
    public void HostDrainsWaitForPendingPreviewAndIgnoreItsLateSuccess(string drain) =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            Pump(window.OpenArchiveAsync(fixture.Index));
            var descriptor = Descriptor(window.CurrentSession!.Snapshot, 141, "Generated preview entry");
            window.CharacterCatalogProvider = (snapshot, token) =>
            {
                token.ThrowIfCancellationRequested();
                Assert.Same(window.CurrentSession!.Snapshot, snapshot);
                return Task.FromResult<IReadOnlyList<AssetDescriptor>>([descriptor]);
            };
            Pump(window.ShowCharactersAsync());
            Assert.Same(Named<TabItem>(window, "CharactersTab"), Named<TabControl>(window, "WorkspaceTabs").SelectedItem);
            Assert.Same(descriptor, DescriptorOf(Entries(window).Items[0]));

            using var preview = new DelayedPreview();
            window.CharactersBrowser.PreviewProvider = preview.Invoke;
            Entries(window).SelectedIndex = 0;
            Named<Button>(window.CharactersBrowser, "PreviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(preview.Entered.Task);
            Assert.Single(Field<HashSet<Task>>(window.CharactersBrowser, "previewTasks"));

            Task drainTask;
            string? replacementPath = null;
            if (drain == "workspace")
            {
                replacementPath = Path.Combine(fixture.Root, "replacement", "archive.idx");
                DiscoveryFixture.WriteIndex(replacementPath, [(1033u, new byte[] { 1, 2, 3 })]);
                drainTask = window.OpenArchiveAsync(replacementPath);
                WaitUntil(() => Field<int>(window, "workspaceTransitionDepth") > 0);
                WaitUntil(() => preview.Token.IsCancellationRequested &&
                    window.CharactersBrowser.SelectedDescriptor is null && Entries(window).Items.Count == 0);
            }
            else if (drain == "cache")
                drainTask = (Task)Invoke(window, "DrainCacheUsersAsync", CancellationToken.None)!;
            else
            {
                window.Close();
                drainTask = WindowTestLifetime.WaitClosedAsync(window);
            }

            Assert.True(preview.Token.IsCancellationRequested);
            Assert.False(drainTask.IsCompleted);
            Assert.Null(window.CharactersBrowser.SelectedDescriptor);
            Assert.Empty(Entries(window).Items);
            Assert.Null(Entries(window).ItemsSource);
            Assert.Equal(string.Empty, Named<TextBlock>(window.CharactersBrowser, "PreviewStatus").Text);

            preview.Release();
            Pump(drainTask);

            if (replacementPath is not null)
                Assert.Equal(Path.GetFullPath(replacementPath), window.CurrentSession!.Snapshot.IndexPath);
            else if (drain == "cache")
                Assert.Null(window.CurrentSession);
            else
                Assert.True(WindowTestLifetime.IsClosed(window));

            Assert.Empty(Field<HashSet<Task>>(window.CharactersBrowser, "previewTasks"));
            Assert.Equal(string.Empty, Named<TextBlock>(window.CharactersBrowser, "PreviewStatus").Text);
            Assert.Null(window.CharactersBrowser.SelectedIdentity);
            Assert.Empty(Entries(window).Items);
        });

    private static AssetDescriptor Descriptor(ArchiveSnapshot snapshot, long ordinal, string label)
    {
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        return new(new(snapshot.IndexContentId, ordinal, "generated/character-entry"), revision,
            "character.model", label, "generated-group", AssetAvailability.Available,
            AssetCompleteness.Complete, AssetCapabilities.Preview,
            [new NameEvidence(label + " stored name", NameOrigin.Stored, "generated descriptor")], []);
    }

    private static ListView Entries(AudioWindow window) =>
        (ListView)window.CharactersBrowser.FindName("Entries")!;

    private static AssetDescriptor DescriptorOf(object row) =>
        (AssetDescriptor)row.GetType().GetProperty("Descriptor", BindingFlags.Instance | BindingFlags.Public)!.GetValue(row)!;

    private static T Named<T>(FrameworkElement window, string name) where T : FrameworkElement =>
        Assert.IsType<T>(window.FindName(name));

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static void WaitUntil(Func<bool> ready)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!ready())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("The character preview drain did not settle.");
            Pump(Task.Delay(10));
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

    private sealed class DelayedPreview : IDisposable
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationToken token;

        public TaskCompletionSource Entered => entered;
        public CancellationToken Token => token;
        public Task Invoke(AssetDescriptor _, CancellationToken cancellationToken)
        {
            token = cancellationToken;
            entered.TrySetResult();
            return released.Task;
        }
        public void Release() => released.TrySetResult();
        public void Dispose() => Release();
    }
}
