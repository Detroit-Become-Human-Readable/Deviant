using System.Numerics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Desktop;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class SceneHostNavigationTests
{
    private const string SnapshotLoadHookName = "SnapshotLoadAsyncOverride";

    [Theory]
    [InlineData("audio")]
    [InlineData("strings")]
    [InlineData("textures")]
    [InlineData("references")]
    public void HidingSceneReleasesSnapshotAndReturningRestoresSavedBudgetRegionCameraSelectionAndApproval(string hiddenView) =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            var scene = PrepareScene(window, fixture);
            var view = window.SceneBrowser;
            var section = scene.Section;
            var entityIdentity = section.Result.Value!.CatalogInstances.Single().Identity;

            Pump(view.LoadSceneAsync(section.Descriptor.Identity));
            Assert.Equal(1, scene.ConfirmationCount);
            Assert.Equal(new[] { false, true }, scene.RenderRequests.Select(request => request.AllowLargeData));

            Pump(view.SelectResourceAsync(entityIdentity));
            var expectedRegion = new GeometryBounds(new(-8, -5, -3), new(12, 9, 13));
            view.SetRegion(expectedRegion);
            Pump(view.LoadRegionAsync(scope: SceneRenderScope.Region, maximumPreviewDimension: 512));
            Assert.True(scene.RenderRequests[^1].AllowLargeData);
            Assert.Equal(expectedRegion, scene.RenderRequests[^1].Region);

            var camera = Assert.IsType<HelixToolkit.Wpf.SharpDX.PerspectiveCamera>(view.Viewport.Viewport.Camera);
            var expectedPosition = new Point3D(17, 23, 31);
            var expectedLook = new Vector3D(-5, -7, -11);
            var expectedUp = new Vector3D(0, 1, 0);
            camera.Position = expectedPosition;
            camera.LookDirection = expectedLook;
            camera.UpDirection = expectedUp;
            camera.FieldOfView = 47;
            ((CheckBox)view.FindName("ShowHidden")).IsChecked = true;
            Assert.Equal(entityIdentity, Field<ResourceIdentity?>(view, "selectedEntity"));

            Invoke(window, "ShowBrowse", hiddenView);
            Pump(Field<Task>(window, "sceneNavigationDrain"));

            var state = RetainedState(window);
            Assert.Equal(section.Descriptor.Identity, state.SectionIdentity);
            Assert.Equal(entityIdentity, state.SelectedStructureIdentity);
            Assert.Equal(expectedRegion, state.Region);
            Assert.Equal(SceneRenderScope.Region, state.Scope);
            Assert.True(state.AllowLargeData);
            Assert.Same(scene.Budget, state.MemoryBudget);
            Assert.Equal(expectedPosition.X, state.Camera.Position.X);
            Assert.Equal(expectedPosition.Y, state.Camera.Position.Y);
            Assert.Equal(expectedPosition.Z, state.Camera.Position.Z);
            Assert.Equal(expectedLook.X, state.Camera.Target.X - state.Camera.Position.X);
            Assert.Equal(expectedLook.Y, state.Camera.Target.Y - state.Camera.Position.Y);
            Assert.Equal(expectedLook.Z, state.Camera.Target.Z - state.Camera.Position.Z);
            Assert.Equal(47, state.Camera.FieldOfView);
            Assert.True(state.ReservedBytes is > 0 and < 1024 * 1024);
            Assert.Null(view.CurrentRenderSnapshot);
            Assert.Null(window.ModelSession);
            Assert.All(scene.SnapshotResources, resource => Assert.True(resource.IsDisposed));
            Assert.Equal(state.ReservedBytes, scene.Budget.UsedBytes);

            var requestsBeforeReturn = scene.RenderRequests.Count;
            Pump(window.ShowScenesAsync());
            Assert.Equal(requestsBeforeReturn + 1, scene.RenderRequests.Count);
            Assert.Equal(expectedRegion, scene.RenderRequests[^1].Region);
            Assert.Equal(512, scene.RenderRequests[^1].MaximumPreviewDimension);

            var restored = Assert.IsType<RenderAssetSnapshot>(view.CurrentRenderSnapshot);
            Assert.Same(scene.Budget, restored.MemoryBudget);
            Assert.Equal(expectedRegion, view.Region);
            Assert.True(((CheckBox)view.FindName("ShowHidden")).IsChecked);
            Assert.Equal(view.CurrentSection!.Result.Value!.Hierarchy.Nodes.Single().Identity, view.SelectedIdentity);
            Assert.Equal(entityIdentity, Field<ResourceIdentity?>(view, "selectedEntity"));
            var restoredCamera = Assert.IsType<HelixToolkit.Wpf.SharpDX.PerspectiveCamera>(view.Viewport.Viewport.Camera);
            Assert.Equal(expectedPosition, restoredCamera.Position);
            Assert.Equal(expectedLook, restoredCamera.LookDirection);
            Assert.Equal(expectedUp, restoredCamera.UpDirection);
            Assert.Equal(47, restoredCamera.FieldOfView);
            Assert.Null(Field<SceneRenderViewState?>(window, "sceneReturnState"));
            Assert.Equal(1, scene.ConfirmationCount);
            Assert.All(scene.RenderRequests.Skip(1), request => Assert.True(request.AllowLargeData));
            Pump(view.LoadRegionAsync());
            Assert.True(scene.RenderRequests[^1].AllowLargeData);
            Assert.Equal(1, scene.ConfirmationCount);
        });

    [Theory]
    [InlineData("workspace")]
    [InlineData("cache")]
    [InlineData("close")]
    public void WorkspaceCacheAndCloseDrainsDisposeRetainedSceneState(string drain) =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            var scene = PrepareScene(window, fixture);
            Pump(window.SceneBrowser.LoadSceneAsync(scene.Section.Descriptor.Identity));
            Pump(window.SceneBrowser.SelectResourceAsync(scene.Section.Result.Value!.CatalogInstances.Single().Identity));
            Invoke(window, "ShowBrowse", "audio");
            Pump(Field<Task>(window, "sceneNavigationDrain"));

            var state = RetainedState(window);
            Assert.True(state.ReservedBytes > 0);
            Assert.All(scene.SnapshotResources, resource => Assert.True(resource.IsDisposed));
            Assert.Equal(state.ReservedBytes, scene.Budget.UsedBytes);

            if (drain == "workspace")
            {
                var replacement = Path.Combine(fixture.Root, "replacement", "archive.idx");
                DiscoveryFixture.WriteIndex(replacement, [(1033u, new byte[] { 1, 2, 3 })]);
                Pump(window.OpenArchiveAsync(replacement));
                Assert.Equal(Path.GetFullPath(replacement), window.CurrentSession!.Snapshot.IndexPath);
            }
            else if (drain == "cache")
                Pump((Task)Invoke(window, "DrainCacheUsersAsync", CancellationToken.None)!);
            else
            {
                window.Close();
                Pump(WindowTestLifetime.WaitClosedAsync(window));
            }

            Assert.Null(Field<SceneRenderViewState?>(window, "sceneReturnState"));
            Assert.Equal(0, scene.Budget.UsedBytes);
            Assert.All(scene.SnapshotResources, resource => Assert.True(resource.IsDisposed));
        });

    private static SceneHarness PrepareScene(AudioWindow window, DiscoveryFixture fixture)
    {
        Pump(window.OpenArchiveAsync(fixture.Index));
        var archive = window.CurrentSession?.Snapshot ?? throw new InvalidOperationException("Open the generated archive first.");
        var section = CreateSection(archive);
        var session = new SyntheticSceneSession(archive, section);
        window.SceneSessionFactory = (_, _, _, _, _) => Task.FromResult<ISceneCatalogSession>(session);

        var budget = new AssetMemoryBudget(64L * 1024 * 1024);
        typeof(AudioWindow).GetField("renderMemoryBudget", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, budget);

        var requests = new List<SceneRenderRequest>();
        var snapshots = new List<SnapshotResource>();
        var regularRegionCalls = 0;
        var largeRegionCalls = new List<bool>();
        var confirmations = 0;
        var view = window.SceneBrowser;
        view.RegionProvider = (_, _, _) =>
        {
            regularRegionCalls++;
            return Task.FromResult(Regions(section));
        };
        view.RegionProviderWithLargeData = (_, allowLargeData, _, _) =>
        {
            largeRegionCalls.Add(allowLargeData);
            return Task.FromResult(Regions(section));
        };
        view.LargeDataConfirmationProvider = (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            confirmations++;
            return Task.FromResult(true);
        };
        view.StreamingRenderProvider = null;
        view.RenderProvider = (request, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            requests.Add(request);
            if (requests.Count == 1)
                return Task.FromException<RenderAssetSnapshot>(new ArchiveException("AssetMemoryLimit", "Generated large scene."));
            var probe = new SnapshotResource(budget.Reserve(3L * 1024 * 1024, "generated render snapshot"));
            snapshots.Add(probe);
            return Task.FromResult(CreateSnapshot(request, budget, probe));
        };

        var fakeSnapshotLoad = new Func<RenderAssetSnapshot, CancellationToken, Task>((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        var hook = typeof(SceneView).GetProperty(SnapshotLoadHookName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"The CPU-only scene load hook '{SnapshotLoadHookName}' is unavailable.");
        hook.SetValue(view, fakeSnapshotLoad);
        Pump(window.ShowScenesAsync());

        return new(section, session, budget, requests, snapshots, largeRegionCalls, () => confirmations,
            () => regularRegionCalls);
    }

    private static SceneRegionDiscovery Regions(SceneLoadedSection section)
    {
        var bounds = new GeometryBounds(new(-2), new(2));
        var branch = section.Result.Value!.CatalogInstances.Single();
        return new([new SceneRenderRegion(branch.Identity, "Generated region", bounds, bounds, 1)
        { VisibleOccurrenceCount = 1 }], []);
    }

    private static RenderAssetSnapshot CreateSnapshot(SceneRenderRequest request, AssetMemoryBudget budget,
        SnapshotResource resource) => new(request.Section.Result.Revision,
        new Dictionary<string, RenderMesh>(), [], new Dictionary<ResourceIdentity, RenderMaterial>(),
        request.Region, [], [], new RenderCoverage(0, 0, 0, 0, 0, 0), [], budget,
        [resource.Reservation, resource]);

    private static SceneLoadedSection CreateSection(ArchiveSnapshot snapshot)
    {
        var container = new ResourceIdentity(snapshot.IndexContentId, 0);
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        var sectionIdentity = container with { MemberPath = "generated/scene-section" };
        var nodeIdentity = container with { MemberPath = "generated/scene-node" };
        var entityIdentity = container with { MemberPath = "generated/scene-entity" };
        var transform = new SourceTransform([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
            MatrixLayout.RowMajor, VectorMultiplicationSide.Row);
        var node = new SceneNodeRecord(nodeIdentity, revision, 1, 0, Vector3.Zero, transform, transform,
            null, null, null, -1, 0, 0, new(container, 0, 196), new Dictionary<string, DecodedExtent>(), new byte[196]);
        var hierarchyNode = new SceneHierarchyNode(node, null, null, null, null, [], transform, transform,
            SceneTransformStatus.Available);
        var entity = new SceneEntityRecord(entityIdentity, 2207, 2,
            new(IdentifierNamespace.Native, "generated", container, 2002), [], 0, 0, 4, 0,
            new(container, 196, 48), new Dictionary<string, DecodedExtent>());
        var branch = new SceneCatalogInstance(entity.Identity, entity, "Generated branch", "Generated catalog",
            new(IdentifierNamespace.Native, "generated", container, 3), new Dictionary<string, DecodedExtent>());
        var environment = new SceneEnvironmentRecord(container with { MemberPath = "generated/environment" }, 1,
            new(container, 0, 0), [], ReadOnlyMemory<byte>.Empty);
        var document = new SceneSectionDocument(sectionIdentity, container, revision,
            new(container, revision, [hierarchyNode], [nodeIdentity], []), [entity], [branch], environment, [], []);
        var result = new DecodeResult<SceneSectionDocument>(document, document.Identity, revision, [], [], [], [],
            new Dictionary<string, string>());
        var descriptor = new AssetDescriptor(document.Identity, revision, "generated.scene", "Generated scene",
            null, AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.Json, [], []);
        return new(descriptor, result, new Dictionary<ResourceIdentity, IReadOnlyList<NameEvidence>>(),
            new Dictionary<ResourceIdentity, IReadOnlyList<SceneEntityRecord>> { [nodeIdentity] = [entity] },
            new Dictionary<ResourceIdentity, IReadOnlyList<AssetRelationship>>());
    }

    private static SceneRenderViewState RetainedState(AudioWindow window) =>
        Field<SceneRenderViewState?>(window, "sceneReturnState")
        ?? throw new InvalidOperationException("The scene view state was not retained.");

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static T Pump<T>(Task<T> task)
    {
        Pump((Task)task);
        return task.GetAwaiter().GetResult();
    }

    private sealed class SceneHarness(SceneLoadedSection section, SyntheticSceneSession session,
        AssetMemoryBudget budget, List<SceneRenderRequest> renderRequests,
        List<SnapshotResource> snapshotResources, List<bool> largeRegionCalls,
        Func<int> getConfirmationCount, Func<int> getRegularRegionCalls)
    {
        public SceneLoadedSection Section { get; } = section;
        public SyntheticSceneSession Session { get; } = session;
        public AssetMemoryBudget Budget { get; } = budget;
        public List<SceneRenderRequest> RenderRequests { get; } = renderRequests;
        public List<SnapshotResource> SnapshotResources { get; } = snapshotResources;
        public List<bool> LargeRegionCalls { get; } = largeRegionCalls;
        public int ConfirmationCount => getConfirmationCount();
        public int RegularRegionCalls => getRegularRegionCalls();
    }

    private sealed class SyntheticSceneSession(ArchiveSnapshot snapshot, SceneLoadedSection section) : ISceneCatalogSession
    {
        public ArchiveSnapshot Snapshot { get; } = snapshot;
        public SourceRevision Revision => section.Result.Revision;
        public SceneDiscoveryProgress Progress => new(false, 1, 1, 1, false);
        public event EventHandler? Updated { add { } remove { } }
        public Task DiscoverMoreAsync(CancellationToken token = default) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task<SceneCatalogPage> QueryAsync(string? search = null, long offset = 0, int limit = 100,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new SceneCatalogPage([section.Descriptor], 1, 0));
        }
        public Task<SceneLoadedSection> LoadAsync(ResourceIdentity identity, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (identity != section.Descriptor.Identity) throw new InvalidOperationException("Unexpected generated section.");
            return Task.FromResult(section);
        }
        public Task RevalidateAsync(SceneLoadedSection loaded, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task ExportMetadataAsync(SceneLoadedSection loaded, string destination, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public void CancelDiscovery() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SnapshotResource(AssetMemoryBudget.Reservation reservation) : IDisposable
    {
        private int disposed;
        public AssetMemoryBudget.Reservation Reservation { get; } = reservation;
        public bool IsDisposed => Volatile.Read(ref disposed) != 0;
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
    }
}
