using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class CharactersHostTests
{
    [Fact]
    public void SelectingCharactersTabLoadsDescriptorsFromTheCurrentSnapshotRevision() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            Pump(window.OpenArchiveAsync(fixture.Index));
            var snapshot = window.CurrentSession!.Snapshot;
            var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
            var first = Descriptor(snapshot, 4, "Generated entry A");
            var second = Descriptor(snapshot, 5, "Generated entry B");
            ArchiveSnapshot? receivedSnapshot = null;
            window.CharacterCatalogProvider = (source, token) =>
            {
                token.ThrowIfCancellationRequested();
                receivedSnapshot = source;
                return Task.FromResult<IReadOnlyList<AssetDescriptor>>([first, second]);
            };

            var tabs = Named<TabControl>(window, "WorkspaceTabs");
            tabs.SelectedItem = Named<TabItem>(window, "CharactersTab");
            WaitUntil(() => Entries(window).Items.Count == 2);

            Assert.Same(snapshot, receivedSnapshot);
            Assert.Same(Named<TabItem>(window, "CharactersTab"), tabs.SelectedItem);
            Assert.Same(first, DescriptorOf(Entries(window).Items[0]));
            Assert.Same(second, DescriptorOf(Entries(window).Items[1]));
            Entries(window).SelectedIndex = 0;
            Assert.Equal(first.Identity, window.CharactersBrowser.SelectedIdentity);
            Assert.Same(first, window.CharactersBrowser.SelectedDescriptor);
            Assert.Contains(revision.PhysicalSourceStamp,
                ((TextBox)window.CharactersBrowser.FindName("Advanced")).Text, StringComparison.Ordinal);
        });

    [Theory]
    [InlineData("home")]
    [InlineData("library")]
    public void LeavingCharactersCancelsPendingCatalogAndRestoresThePreviousWorkspaceView(string priorView) =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            Pump(window.OpenArchiveAsync(fixture.Index));
            if (priorView == "home") Invoke(window, "Home_Click", window, new RoutedEventArgs());
            else Invoke(window, "ShowLibrary", "bookmarks", null);

            using var provider = new DelayedCatalog();
            window.CharacterCatalogProvider = provider.Invoke;
            var show = window.ShowCharactersAsync();
            Pump(provider.Entered.Task);
            Assert.Same(Named<TabItem>(window, "CharactersTab"), Named<TabControl>(window, "WorkspaceTabs").SelectedItem);
            Assert.False(show.IsCompleted);

            Named<TabControl>(window, "WorkspaceTabs").SelectedItem = Named<TabItem>(window, "LibraryTab");
            Assert.True(provider.Token.IsCancellationRequested);
            if (priorView == "home")
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("HomeHost")).Visibility);
            else
            {
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("LibraryHost")).Visibility);
                Assert.Equal("Bookmarks", ((TextBlock)window.FindName("LibraryTitle")).Text);
            }

            provider.Release(Descriptor(window.CurrentSession!.Snapshot, 71, "Stale generated entry"));
            Pump(show);
            Assert.Empty(Entries(window).Items);
            Assert.Null(window.CharactersBrowser.SelectedDescriptor);
        });

    [Fact]
    public void NewerCharacterCatalogRequestWinsWhenAnOlderProviderCompletesLate() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            Pump(window.OpenArchiveAsync(fixture.Index));
            var snapshot = window.CurrentSession!.Snapshot;
            using var provider = new DelayedFirstCatalog(snapshot,
                Descriptor(snapshot, 81, "Stale generated row"),
                Descriptor(snapshot, 82, "Current generated row"));
            window.CharacterCatalogProvider = provider.Invoke;

            var older = window.ShowCharactersAsync();
            Pump(provider.FirstEntered.Task);
            var newer = window.ShowCharactersAsync();
            Pump(newer);
            Assert.True(provider.FirstToken.IsCancellationRequested);
            Assert.Single(Entries(window).Items);
            Assert.Same(provider.Current, DescriptorOf(Entries(window).Items[0]));

            provider.ReleaseFirst();
            Pump(older);
            Assert.Single(Entries(window).Items);
            Assert.Same(provider.Current, DescriptorOf(Entries(window).Items[0]));
            Assert.NotSame(provider.Stale, window.CharactersBrowser.SelectedDescriptor);
        });

    [Theory]
    [InlineData("workspace")]
    [InlineData("cache")]
    [InlineData("close")]
    public void WorkspaceCacheAndCloseDrainsWaitForLateCatalogAndPreventStalePublication(string drain) =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            Pump(window.OpenArchiveAsync(fixture.Index));
            var snapshot = window.CurrentSession!.Snapshot;
            using var provider = new DelayedCatalog();
            window.CharacterCatalogProvider = provider.Invoke;
            var show = window.ShowCharactersAsync();
            Pump(provider.Entered.Task);

            Task drainTask;
            string? replacementPath = null;
            if (drain == "workspace")
            {
                replacementPath = Path.Combine(fixture.Root, "replacement", "archive.idx");
                DiscoveryFixture.WriteIndex(replacementPath, [(1033u, new byte[] { 1, 2, 3 })]);
                drainTask = window.OpenArchiveAsync(replacementPath);
                WaitUntil(() => Field<int>(window, "workspaceTransitionDepth") > 0);
            }
            else if (drain == "cache")
                drainTask = (Task)Invoke(window, "DrainCacheUsersAsync", CancellationToken.None)!;
            else
            {
                window.Close();
                drainTask = WindowTestLifetime.WaitClosedAsync(window);
            }

            Assert.True(provider.Token.IsCancellationRequested);
            Assert.False(drainTask.IsCompleted);
            Assert.Empty(Entries(window).Items);
            provider.Release(Descriptor(snapshot, 91, "Late generated row"));
            Pump(Task.WhenAll(show, drainTask));

            if (replacementPath is not null)
                Assert.Equal(Path.GetFullPath(replacementPath), window.CurrentSession!.Snapshot.IndexPath);
            else if (drain == "cache")
                Assert.Null(window.CurrentSession);
            else Assert.True(WindowTestLifetime.IsClosed(window));

            Assert.Empty(Entries(window).Items);
            Assert.Null(window.CharactersBrowser.SelectedDescriptor);
            Assert.Empty(Field<HashSet<Task>>(window, "characterCatalogTasks"));
        });

    [Fact]
    public void MissingCharacterProviderLeavesTheCharactersTabAvailableWithoutInventedRows() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            Pump(window.OpenArchiveAsync(fixture.Index));
            window.CharacterCatalogProvider = null;
            Pump(window.ShowCharactersAsync());

            Assert.Same(Named<TabItem>(window, "CharactersTab"), Named<TabControl>(window, "WorkspaceTabs").SelectedItem);
            Assert.Equal("Character catalog unavailable.", ((TextBlock)window.CharactersBrowser.FindName("Status")).Text);
            Assert.Empty(Entries(window).Items);
        });

    private static AssetDescriptor Descriptor(ArchiveSnapshot snapshot, long ordinal, string label)
    {
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        var identity = new ResourceIdentity(snapshot.IndexContentId, ordinal, "generated/character-entry");
        return new(identity, revision, "character.model", label, "generated-group", AssetAvailability.Available,
            AssetCompleteness.Complete, AssetCapabilities.Preview,
            [new NameEvidence(label + " stored name", NameOrigin.Stored, "generated descriptor")], []);
    }

    private static ListView Entries(AudioWindow window) =>
        (ListView)window.CharactersBrowser.FindName("Entries")!;

    private static AssetDescriptor DescriptorOf(object row) =>
        (AssetDescriptor)row.GetType().GetProperty("Descriptor", BindingFlags.Instance | BindingFlags.Public)!.GetValue(row)!;

    private static T Named<T>(AudioWindow window, string name) where T : FrameworkElement =>
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
            if (DateTime.UtcNow >= until) throw new TimeoutException("The Characters workspace state did not settle.");
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

    private sealed class DelayedCatalog : IDisposable
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyList<AssetDescriptor>> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationToken token;
        public TaskCompletionSource Entered => entered;
        public CancellationToken Token => token;
        public Task<IReadOnlyList<AssetDescriptor>> Invoke(ArchiveSnapshot _, CancellationToken cancellationToken)
        {
            token = cancellationToken;
            entered.TrySetResult();
            return released.Task;
        }
        public void Release(params AssetDescriptor[] descriptors) => released.TrySetResult(descriptors);
        public void Dispose() => Release();
    }

    private sealed class DelayedFirstCatalog(ArchiveSnapshot snapshot, AssetDescriptor stale, AssetDescriptor current) : IDisposable
    {
        private readonly TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyList<AssetDescriptor>> firstReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;
        private CancellationToken firstToken;
        public TaskCompletionSource FirstEntered => firstEntered;
        public AssetDescriptor Stale { get; } = stale;
        public AssetDescriptor Current { get; } = current;
        public CancellationToken FirstToken => firstToken;
        public Task<IReadOnlyList<AssetDescriptor>> Invoke(ArchiveSnapshot received, CancellationToken token)
        {
            Assert.Same(snapshot, received);
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstToken = token;
                firstEntered.TrySetResult();
                return firstReleased.Task;
            }
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<AssetDescriptor>>([Current]);
        }
        public void ReleaseFirst() => firstReleased.TrySetResult([Stale]);
        public void Dispose() => ReleaseFirst();
    }
}
