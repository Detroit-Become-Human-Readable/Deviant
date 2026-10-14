using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class CharactersBindingHostTests
{
    [Fact]
    public void SelectingAnotherEntrySupersedesPendingBindingAndDisposesLateRetention()
    {
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            var (first, second) = OpenCharacters(window, fixture);
            using var provider = new SupersedingBindingProvider(first, second);
            window.CharacterBindingsProvider = provider.Invoke;
            var entries = Entries(window);

            entries.SelectedIndex = 0;
            Pump(provider.FirstEntered.Task);
            entries.SelectedIndex = 1;
            WaitUntil(() => window.CharactersBrowser.ModelBinding?.SelectedIdentity == second.Identity);

            Assert.True(provider.FirstToken.IsCancellationRequested);
            Assert.Same(provider.CurrentRetention, Field<IDisposable?>(window, "characterBindingRetention"));
            Assert.False(provider.CurrentRetention!.IsDisposed);

            provider.ReleaseFirst();
            WaitUntil(() => Field<HashSet<Task>>(window, "characterBindingTasks").Count == 0);
            Assert.True(provider.FirstRetention!.IsDisposed);
            Assert.Same(second.Identity, window.CharactersBrowser.ModelBinding!.SelectedIdentity);
            Assert.False(provider.CurrentRetention!.IsDisposed);
            Assert.True(provider.Budget!.UsedBytes > 0);

            entries.SelectedIndex = -1;
            Assert.Null(window.CharactersBrowser.ModelBinding);
            Assert.True(provider.CurrentRetention.IsDisposed);
            Assert.Equal(0, provider.Budget.UsedBytes);
        });
    }

    [Fact]
    public void WrongSourceBindingIsRejectedAndProviderRetentionIsReleased()
    {
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            var (descriptor, _) = OpenCharacters(window, fixture);
            RetentionProbe? retention = null;
            AssetMemoryBudget? budget = null;
            window.CharacterBindingsProvider = (selected, sharedBudget, token) =>
            {
                token.ThrowIfCancellationRequested();
                budget = sharedBudget;
                retention = new RetentionProbe(sharedBudget.Reserve(512, "generated character binding metadata"));
                return Task.FromResult<(IReadOnlyList<CharacterModelBindingResult> Bindings, IDisposable Retention)>(
                    ([Binding(selected, selected.Revision with { PhysicalSourceStamp = "different-generated-revision" })], retention));
            };

            Entries(window).SelectedIndex = 0;
            WaitUntil(() => ((TextBlock)window.CharactersBrowser.FindName("BindingStatus")).Text == "Unable to load model details." &&
                retention?.IsDisposed == true && Field<HashSet<Task>>(window, "characterBindingTasks").Count == 0);

            Assert.Null(window.CharactersBrowser.ModelBinding);
            Assert.True(retention!.IsDisposed);
            Assert.Equal(Field<AssetMemoryBudget.Reservation>(window, "characterFocusReservation").Bytes, budget!.UsedBytes);
            Assert.Empty(Field<HashSet<Task>>(window, "characterBindingTasks"));
            Assert.Contains("ArgumentException", ((TextBox)window.CharactersBrowser.FindName("Advanced")).Text, StringComparison.Ordinal);
            Assert.Equal(descriptor.Identity, window.CharactersBrowser.SelectedIdentity);
            window.CharactersBrowser.ClearSelection();
            Assert.Equal(0, budget.UsedBytes);
        });
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("cache")]
    [InlineData("close")]
    public void WorkspaceCacheAndCloseDrainsWaitForBindingAndDisposeLateRetention(string drain) =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            var (descriptor, _) = OpenCharacters(window, fixture);
            using var provider = new DelayedBindingProvider();
            window.CharacterBindingsProvider = provider.Invoke;
            Entries(window).SelectedIndex = 0;
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
            Assert.Null(window.CharactersBrowser.ModelBinding);
            Assert.Null(Field<IDisposable?>(window, "characterBindingRetention"));
            provider.Release();
            Pump(drainTask);
            WaitUntil(() => Field<HashSet<Task>>(window, "characterBindingTasks").Count == 0);

            Assert.True(provider.Retention!.IsDisposed);
            Assert.Equal(0, provider.Budget!.UsedBytes);
            Assert.Null(window.CharactersBrowser.ModelBinding);
            Assert.Empty(Field<HashSet<Task>>(window, "characterBindingTasks"));
            if (replacementPath is not null)
                Assert.Equal(Path.GetFullPath(replacementPath), window.CurrentSession!.Snapshot.IndexPath);
            else if (drain == "cache")
                Assert.Null(window.CurrentSession);
            else Assert.True(WindowTestLifetime.IsClosed(window));
        });

    [Fact]
    public void CancelButtonCancelsPendingBindingAndReleasesItsLateRetention()
    {
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            OpenCharacters(window, fixture);
            using var provider = new DelayedBindingProvider();
            window.CharacterBindingsProvider = provider.Invoke;
            Entries(window).SelectedIndex = 0;
            Pump(provider.Entered.Task);

            var cancel = (Button)window.CharactersBrowser.FindName("CancelPreviewButton")!;
            Assert.Equal(Visibility.Visible, cancel.Visibility);
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(provider.Token.IsCancellationRequested);
            Assert.Equal("Cancelled", ((TextBlock)window.CharactersBrowser.FindName("BindingStatus")).Text);

            provider.Release();
            WaitUntil(() => Field<HashSet<Task>>(window, "characterBindingTasks").Count == 0 && provider.Retention?.IsDisposed == true);
            Assert.True(provider.Retention!.IsDisposed);
            Assert.Equal(Field<AssetMemoryBudget.Reservation>(window, "characterFocusReservation").Bytes, provider.Budget!.UsedBytes);
            Assert.Null(window.CharactersBrowser.ModelBinding);
            Assert.Equal("Cancelled", ((TextBlock)window.CharactersBrowser.FindName("BindingStatus")).Text);
            window.CharactersBrowser.ClearSelection();
            Assert.Equal(0, provider.Budget.UsedBytes);
        });
    }

    private static (AssetDescriptor First, AssetDescriptor Second) OpenCharacters(AudioWindow window,
        DiscoveryFixture fixture)
    {
        Pump(window.OpenArchiveAsync(fixture.Index));
        var snapshot = window.CurrentSession!.Snapshot;
        var first = Descriptor(snapshot, 311, "Generated model A");
        var second = Descriptor(snapshot, 312, "Generated model B");
        window.CharacterCatalogProvider = (source, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Same(snapshot, source);
            return Task.FromResult<IReadOnlyList<AssetDescriptor>>([first, second]);
        };
        Pump(window.ShowCharactersAsync());
        Assert.Same(Named<TabItem>(window, "CharactersTab"), Named<TabControl>(window, "WorkspaceTabs").SelectedItem);
        return (first, second);
    }

    private static AssetDescriptor Descriptor(ArchiveSnapshot snapshot, long ordinal, string label)
    {
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        return new(new(snapshot.IndexContentId, ordinal, "generated/model-entry"), revision,
            "character.model", label, "generated-group", AssetAvailability.Available,
            AssetCompleteness.Complete, AssetCapabilities.Preview,
            [new NameEvidence(label + " stored name", NameOrigin.Stored, "generated descriptor")], []);
    }

    private static CharacterModelBindingResult Binding(AssetDescriptor descriptor,
        SourceRevision? revision = null)
    {
        var owner = descriptor.Identity with { MemberPath = "generated/container" };
        var catalog = descriptor.Identity with { MemberPath = "generated/catalog" };
        var mesh = new ResourceIdentity(descriptor.Identity.IndexContentId, descriptor.Identity.Ordinal + 1000,
            "generated/mesh");
        var lod = new CharacterMeshLodBinding(0, 0.5f,
            new(IdentifierNamespace.Native, "generated-mesh", owner),
            new(ReferenceStatus.Unique, [mesh]), mesh, 0, 1, null,
            new NativeModelSelection(mesh, 0), new(owner, 0, 16), null, null, null);
        return new(descriptor.Identity, catalog, owner, revision ?? descriptor.Revision, 2009, 7, null, 0,
            null, null, new(ReferenceStatus.Unique, [owner]), [], [], [lod], null, null);
    }

    private static ListView Entries(AudioWindow window) =>
        (ListView)window.CharactersBrowser.FindName("Entries")!;

    private static T Named<T>(FrameworkElement window, string name) where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(window.FindName(name));

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static void WaitUntil(Func<bool> ready)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!ready())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("The character metadata host did not settle.");
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

    private sealed class RetentionProbe(AssetMemoryBudget.Reservation reservation) : IDisposable
    {
        private int disposed;
        public bool IsDisposed => Volatile.Read(ref disposed) != 0;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) reservation.Dispose();
        }
    }

    private sealed class DelayedBindingProvider : IDisposable
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<(IReadOnlyList<CharacterModelBindingResult> Bindings, IDisposable Retention)> released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private AssetDescriptor? descriptor;
        private AssetMemoryBudget? budget;
        private CancellationToken token;
        public TaskCompletionSource Entered => entered;
        public AssetDescriptor Descriptor => descriptor ?? throw new InvalidOperationException("The binding provider has not started.");
        public AssetMemoryBudget Budget => budget ?? throw new InvalidOperationException("The binding provider has not started.");
        public CancellationToken Token => token;
        public RetentionProbe? Retention { get; private set; }
        public Task<(IReadOnlyList<CharacterModelBindingResult> Bindings, IDisposable Retention)> Invoke(
            AssetDescriptor selected, AssetMemoryBudget sharedBudget, CancellationToken cancellationToken)
        {
            descriptor = selected; budget = sharedBudget; token = cancellationToken;
            entered.TrySetResult();
            return released.Task;
        }
        public void Release()
        {
            if (descriptor is null || budget is null || released.Task.IsCompleted) return;
            var retention = new RetentionProbe(budget.Reserve(512, "generated character binding metadata"));
            Retention = retention;
            released.TrySetResult(([Binding(descriptor)], retention));
        }
        public void Dispose()
        {
            if (released.Task.IsCompletedSuccessfully) return;
            Release();
        }
    }

    private sealed class SupersedingBindingProvider(AssetDescriptor stale, AssetDescriptor current) : IDisposable
    {
        private readonly TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<(IReadOnlyList<CharacterModelBindingResult> Bindings, IDisposable Retention)> firstReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;
        private AssetMemoryBudget? firstBudget;
        private CancellationToken firstToken;
        private RetentionProbe? firstRetention;
        public TaskCompletionSource FirstEntered => firstEntered;
        public CancellationToken FirstToken => firstToken;
        public AssetDescriptor Stale { get; } = stale;
        public RetentionProbe? FirstRetention => firstRetention;
        public RetentionProbe? CurrentRetention { get; private set; }
        public AssetMemoryBudget? Budget { get; private set; }
        public Task<(IReadOnlyList<CharacterModelBindingResult> Bindings, IDisposable Retention)> Invoke(
            AssetDescriptor selected, AssetMemoryBudget budget, CancellationToken token)
        {
            Budget = budget;
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstBudget = budget; firstToken = token;
                firstEntered.TrySetResult();
                return firstReleased.Task;
            }
            Assert.Same(current, selected);
            var retention = new RetentionProbe(budget.Reserve(512, "generated selected model binding"));
            CurrentRetention = retention;
            return Task.FromResult<(IReadOnlyList<CharacterModelBindingResult> Bindings, IDisposable Retention)>(
                ([Binding(selected)], retention));
        }
        public void ReleaseFirst()
        {
            if (firstReleased.Task.IsCompleted || firstBudget is null) return;
            var retention = new RetentionProbe(firstBudget.Reserve(512, "generated stale model binding"));
            firstRetention = retention;
            firstReleased.TrySetResult(([Binding(Stale)], retention));
        }
        public void Dispose() => ReleaseFirst();
    }
}
