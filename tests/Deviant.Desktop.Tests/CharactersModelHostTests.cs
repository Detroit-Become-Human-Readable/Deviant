using System.IO;
using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class CharactersModelHostTests
{
    [Fact]
    public void SameWorkspaceRefreshAndBrowserModeKeepTheHostedPreview()
        => WorkspaceCommitTests.Run((window, fixture) =>
        {
            var descriptor = OpenCharacters(window, fixture);
            var loadCalls = SetSnapshotLoadOverride(window);
            BuiltModel? built = null;
            window.CharacterModelProvider = (selected, budget, token) =>
            {
                token.ThrowIfCancellationRequested();
                built = CreateModel(selected, budget);
                return Task.FromResult(built.Snapshot);
            };
            StartPreview(window);
            WaitUntil(() => Named<TextBlock>(window.CharactersBrowser, "PreviewStatus").Text == "Preview ready");
            var snapshot = window.ModelBrowser.CurrentSnapshot;
            Pump(window.ShowCharactersAsync());
            Named<ComboBox>(window.CharactersBrowser, "BrowserMode").SelectedIndex = 1;
            Pump(window.ShowCharactersAsync());
            Assert.Same(snapshot, window.ModelBrowser.CurrentSnapshot);
            Assert.False(built!.Resource.IsDisposed);
            Assert.Same(descriptor, window.CharactersBrowser.SelectedDescriptor);
            Assert.Equal("Preview ready", Named<TextBlock>(window.CharactersBrowser, "PreviewStatus").Text);
            Assert.Equal(1, loadCalls());
        });

    [Theory]
    [InlineData("tab")]
    [InlineData("cache")]
    [InlineData("close")]
    public void HostedModelBorrowsProviderSnapshotUntilHostDrainReleasesItsBudget(string drain) =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            var descriptor = OpenCharacters(window, fixture);
            var loadCalls = SetSnapshotLoadOverride(window);
            BuiltModel? built = null;
            AssetDescriptor? receivedDescriptor = null;
            AssetMemoryBudget? receivedBudget = null;
            window.CharacterModelProvider = (selected, budget, token) =>
            {
                token.ThrowIfCancellationRequested();
                receivedDescriptor = selected;
                receivedBudget = budget;
                built = CreateModel(selected, budget);
                return Task.FromResult(built.Snapshot);
            };
            StartPreview(window);
            WaitUntil(() => Named<TextBlock>(window.CharactersBrowser, "PreviewStatus").Text == "Preview ready");

            var snapshot = Assert.IsType<RenderAssetSnapshot>(window.ModelBrowser.CurrentSnapshot);
            Assert.Same(descriptor, receivedDescriptor);
            Assert.Same(descriptor, window.CharactersBrowser.SelectedDescriptor);
            Assert.Same(window.RenderMemoryBudget, receivedBudget);
            Assert.Same(snapshot, built!.Snapshot);
            Assert.Same(window.ModelBrowser, window.CharactersBrowser.PreviewContent.Content);
            Assert.Equal(1, loadCalls());
            Assert.False(built.Resource.IsDisposed);
            Assert.True(receivedBudget!.UsedBytes > 0);

            Task drainTask;
            if (drain == "tab")
            {
                Named<TabControl>(window, "WorkspaceTabs").SelectedItem = Named<TabItem>(window, "LibraryTab");
                drainTask = WaitUntilAsync(() => window.ModelBrowser.CurrentSnapshot is null && receivedBudget.UsedBytes == 0);
            }
            else if (drain == "cache")
                drainTask = (Task)Invoke(window, "DrainCacheUsersAsync", CancellationToken.None)!;
            else
            {
                window.Close();
                drainTask = WindowTestLifetime.WaitClosedAsync(window);
            }

            Pump(drainTask);
            Assert.Null(window.ModelBrowser.CurrentSnapshot);
            Assert.True(built.Resource.IsDisposed);
            Assert.Equal(0, receivedBudget.UsedBytes);
        });

    [Theory]
    [InlineData("revision")]
    [InlineData("budget")]
    public void MismatchedModelRevisionOrBudgetIsRejectedAndOwnerResourcesAreReleased(string mismatch) =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            var descriptor = OpenCharacters(window, fixture);
            var loadCalls = SetSnapshotLoadOverride(window);
            BuiltModel? built = null;
            AssetMemoryBudget? providerBudget = null;
            window.CharacterModelProvider = (selected, budget, token) =>
            {
                token.ThrowIfCancellationRequested();
                providerBudget = mismatch == "budget" ? new AssetMemoryBudget(1024 * 1024) : budget;
                var revision = mismatch == "revision"
                    ? selected.Revision with { PhysicalSourceStamp = "different-generated-revision" }
                    : selected.Revision;
                built = CreateModel(selected, providerBudget, revision);
                return Task.FromResult(built.Snapshot);
            };
            StartPreview(window);
            WaitUntil(() => Named<TextBlock>(window.CharactersBrowser, "PreviewStatus").Text.Contains("does not match", StringComparison.Ordinal));

            Assert.Null(window.ModelBrowser.CurrentSnapshot);
            Assert.Equal(0, loadCalls());
            Assert.True(built!.Resource.IsDisposed);
            Assert.Equal(0, providerBudget!.UsedBytes);
            Assert.Equal(0, window.RenderMemoryBudget!.UsedBytes);
            Assert.Null(window.CharactersBrowser.PreviewContent.Content as AssetModelView);
        });

    [Theory]
    [InlineData("tab")]
    [InlineData("workspace")]
    [InlineData("close")]
    public void LateIgnoredCancellationCannotLoadAStaleCharacterModel(string destination) =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            var descriptor = OpenCharacters(window, fixture);
            var loadCalls = SetSnapshotLoadOverride(window);
            using var provider = new DelayedModelProvider();
            window.CharacterModelProvider = provider.Invoke;
            StartPreview(window);
            Pump(provider.Entered.Task);
            var budget = provider.Budget;
            Assert.Same(descriptor, provider.Descriptor);
            Assert.Equal(0, loadCalls());

            Task navigation;
            string? replacementPath = null;
            if (destination == "tab")
            {
                Named<TabControl>(window, "WorkspaceTabs").SelectedItem = Named<TabItem>(window, "LibraryTab");
                Assert.True(provider.Token.IsCancellationRequested);
                navigation = WaitUntilAsync(() => Field<HashSet<Task>>(window.CharactersBrowser, "previewTasks").Count == 0);
            }
            else if (destination == "workspace")
            {
                replacementPath = Path.Combine(fixture.Root, "replacement", "archive.idx");
                DiscoveryFixture.WriteIndex(replacementPath, [(1033u, new byte[] { 1, 2, 3 })]);
                navigation = window.OpenArchiveAsync(replacementPath);
                WaitUntil(() => Field<int>(window, "workspaceTransitionDepth") > 0);
                Assert.True(provider.Token.IsCancellationRequested);
                Assert.False(navigation.IsCompleted);
            }
            else
            {
                window.Close();
                navigation = WindowTestLifetime.WaitClosedAsync(window);
                Assert.True(provider.Token.IsCancellationRequested);
                Assert.False(navigation.IsCompleted);
            }

            var late = CreateModel(provider.Descriptor, budget);
            provider.Release(late.Snapshot);
            Pump(navigation);
            if (replacementPath is not null)
                Assert.Equal(Path.GetFullPath(replacementPath), window.CurrentSession!.Snapshot.IndexPath);
            else if (destination == "close")
                Assert.True(WindowTestLifetime.IsClosed(window));

            Assert.Equal(0, loadCalls());
            Assert.Null(window.ModelBrowser.CurrentSnapshot);
            Assert.True(late.Resource.IsDisposed);
            Assert.Equal(0, budget.UsedBytes);
            Assert.Equal(string.Empty, Named<TextBlock>(window.CharactersBrowser, "PreviewStatus").Text);
        });

    private static AssetDescriptor OpenCharacters(AudioWindow window, DiscoveryFixture fixture)
    {
        Pump(window.OpenArchiveAsync(fixture.Index));
        var descriptor = Descriptor(window.CurrentSession!.Snapshot, 271, "Generated character model");
        window.CharacterCatalogProvider = (snapshot, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Same(window.CurrentSession!.Snapshot, snapshot);
            return Task.FromResult<IReadOnlyList<AssetDescriptor>>([descriptor]);
        };
        Pump(window.ShowCharactersAsync());
        window.CharactersBrowser.PreviewContent.Visibility = Visibility.Collapsed;
        Entries(window).SelectedIndex = 0;
        Assert.Same(descriptor, window.CharactersBrowser.SelectedDescriptor);
        return descriptor;
    }

    private static AssetDescriptor Descriptor(ArchiveSnapshot snapshot, long ordinal, string label)
    {
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        return new(new(snapshot.IndexContentId, ordinal, "generated/model-entry"), revision,
            "character.model", label, "generated-group", AssetAvailability.Available,
            AssetCompleteness.Complete, AssetCapabilities.Preview,
            [new NameEvidence(label + " stored name", NameOrigin.Stored, "generated descriptor")], []);
    }

    private static BuiltModel CreateModel(AssetDescriptor descriptor, AssetMemoryBudget budget,
        SourceRevision? revision = null)
    {
        var meshIdentity = new ResourceIdentity(descriptor.Identity.IndexContentId,
            descriptor.Identity.Ordinal + 100, "generated/model-mesh");
        var bounds = new GeometryBounds(Vector3.Zero, Vector3.One);
        var primitive = new MeshPrimitive("generated-primitive", PrimitiveTopology.Triangles,
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0u, 1u, 2u], IndexElementFormat.UInt16, [], Bounds: bounds);
        var geometry = new GeometryAsset(new CoordinateProfile("generated-model", "1"),
            [new GeometryNode("generated-node", null,
                new ResourceReference(IdentifierNamespace.Native, "generated", meshIdentity), [primitive.Key])],
            [primitive], new Dictionary<string, string>());
        var mesh = new RenderMesh(meshIdentity, 0, geometry);
        var occurrence = new RenderOccurrence("generated/model-placement", descriptor.Identity, meshIdentity,
            "Generated model", mesh.Key, Matrix4x4.Identity, bounds, [descriptor.Identity])
        {
            PlacementOrigin = RenderPlacementOrigin.ModelLocal,
            ModelPlacement = new(meshIdentity, 0)
        };
        var resource = new SnapshotResource(budget.Reserve(4096, "generated model snapshot"));
        var snapshot = new RenderAssetSnapshot(revision ?? descriptor.Revision,
            new Dictionary<string, RenderMesh> { [mesh.Key] = mesh }, [occurrence],
            new Dictionary<ResourceIdentity, RenderMaterial>(), bounds, [], [], new(1, 1, 0, 1, 0, 0), [], budget,
            [resource.Reservation, resource]);
        return new(snapshot, resource, budget);
    }

    private static Func<int> SetSnapshotLoadOverride(AudioWindow window)
    {
        var calls = 0;
        Func<RenderAssetSnapshot, CancellationToken, Task> load = (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            calls++;
            return Task.CompletedTask;
        };
        typeof(AssetModelView).GetProperty("SnapshotLoadAsyncOverride", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window.ModelBrowser, load);
        return () => calls;
    }

    private static void StartPreview(AudioWindow window) =>
        Named<Button>(window.CharactersBrowser, "PreviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static ListView Entries(AudioWindow window) =>
        (ListView)window.CharactersBrowser.FindName("Entries")!;

    private static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        Assert.IsType<T>(root.FindName(name));

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static void WaitUntil(Func<bool> ready)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!ready())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("The character model host did not settle.");
            Pump(Task.Delay(10));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private sealed record BuiltModel(RenderAssetSnapshot Snapshot, SnapshotResource Resource, AssetMemoryBudget Budget);

    private sealed class SnapshotResource(AssetMemoryBudget.Reservation reservation) : IDisposable
    {
        private int disposed;
        public AssetMemoryBudget.Reservation Reservation { get; } = reservation;
        public bool IsDisposed => Volatile.Read(ref disposed) != 0;
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
    }

    private sealed class DelayedModelProvider : IDisposable
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<RenderAssetSnapshot> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private AssetDescriptor? descriptor;
        private AssetMemoryBudget? budget;
        private CancellationToken token;
        public TaskCompletionSource Entered => entered;
        public AssetDescriptor Descriptor => descriptor ?? throw new InvalidOperationException("The model provider has not started.");
        public AssetMemoryBudget Budget => budget ?? throw new InvalidOperationException("The model provider has not started.");
        public CancellationToken Token => token;
        public Task<RenderAssetSnapshot> Invoke(AssetDescriptor selected, AssetMemoryBudget sharedBudget, CancellationToken cancellationToken)
        {
            descriptor = selected;
            budget = sharedBudget;
            token = cancellationToken;
            entered.TrySetResult();
            return released.Task;
        }
        public void Release(RenderAssetSnapshot snapshot) => released.TrySetResult(snapshot);
        public void Dispose()
        {
            if (descriptor is not null && budget is not null && !released.Task.IsCompleted)
                released.TrySetResult(CreateModel(descriptor, budget).Snapshot);
            else if (released.Task.IsCompletedSuccessfully)
                released.Task.Result.Dispose();
        }
    }
}
