using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class SceneProgressTests
{
    [Fact]
    public void AutomaticDiscoveryContinuesThroughEveryBoundedBatch() => Run(() =>
    {
        var session = new DiscoverySession(7, 2);
        var view = new SceneView();
        try
        {
            Pump(view.LoadAsync(session)); Pump(view.DiscoverAllAsync());
            Assert.Equal(4, session.DiscoveryCalls);
            Assert.Equal(7, session.Progress.Visited);
            Assert.Equal(7, view.CurrentRows.Count);
            Assert.False(view.IsDiscoveringAll);
            Assert.Equal("7 scenes", ((TextBlock)view.FindName("SceneCount")).Text);
            Assert.False(view.ActivityProgress.IsActive);
            Assert.Equal("Ready", view.ActivityProgress.Stage);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void SceneSearchPublishesActivityAndCancelDrainsTheQuery() => Run(() =>
    {
        var session = new DiscoverySession(1, 1) { QueryGated = true };
        var view = new SceneView();
        var updates = new List<SceneActivityProgress>();
        view.ActivityProgressChanged += (_, update) => updates.Add(update);
        try
        {
            var load = view.LoadAsync(session);
            PumpUntil(() => session.QueryStarted.Task.IsCompleted);
            Assert.True(view.ActivityProgress.IsActive);
            Assert.Equal("Searching scenes", view.ActivityProgress.Stage);
            view.CancelActivity();
            Pump(load);
            Assert.True(session.QueryCancelled);
            Assert.Contains(updates, update => update.IsActive && update.Stage == "Searching scenes");
            Assert.False(view.ActivityProgress.IsActive);
            Assert.Equal("Cancelled", view.ActivityProgress.Stage);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void SceneSearchStaysActiveUntilEveryOverlappingQueryDrains() => Run(() =>
    {
        var session = new DiscoverySession(1, 1);
        var view = new SceneView();
        try
        {
            Pump(view.LoadAsync(session));
            session.QueryGated = true;
            session.IgnoreQueryCancellation = true;
            var query = typeof(SceneView).GetMethod("QueryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var first = (Task)query.Invoke(view, [CancellationToken.None])!;
            PumpUntil(() => session.GatedQueryCalls == 1);
            var second = (Task)query.Invoke(view, [CancellationToken.None])!;
            PumpUntil(() => session.GatedQueryCalls == 2);
            session.SecondQueryRelease.SetResult();
            Pump(second);
            Assert.True(view.ActivityProgress.IsActive);
            Assert.Equal("Searching scenes", view.ActivityProgress.Stage);

            var suspend = view.SuspendAsync();
            Assert.False(suspend.IsCompleted);
            session.FirstQueryRelease.SetResult();
            Pump(first);
            Pump(suspend);
            Assert.False(view.ActivityProgress.IsActive);
            Assert.Equal("Ready", view.ActivityProgress.Stage);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void DiscoveryShowsUnknownAndKnownProgressAndResumesAfterCancellation() => Run(() =>
    {
        var session = new DiscoverySession(5, 2) { Gated = true, ExposeTotal = false };
        var view = new SceneView();
        try
        {
            Pump(view.LoadAsync(session));
            var discovery = view.DiscoverAllAsync();
            PumpUntil(() => session.DiscoveryCalls == 1);
            Assert.True(view.ActivityProgress.IsActive);
            Assert.Equal("Discovering scenes", view.ActivityProgress.Stage);
            Assert.Equal(0, view.ActivityProgress.Total);
            Assert.Equal(Visibility.Collapsed, ((Grid)view.FindName("ViewportLoadingOverlay")).Visibility);
            Assert.False(((ProcessingAnimation)view.FindName("ViewportProcessing")).IsActive);
            session.Permits.Release();
            PumpUntil(() => session.DiscoveryCalls == 2);
            Assert.Equal(5, view.ActivityProgress.Total);
            Assert.Equal(2, view.ActivityProgress.Completed);
            Pump(view.SuspendAsync()); Pump(discovery);
            Assert.True(session.Cancelled);
            Assert.Equal(2, session.Progress.Visited);
            Assert.False(view.ActivityProgress.IsActive);
            session.Gated = false;
            Pump(view.DiscoverAllAsync());
            Assert.Equal(5, view.CurrentRows.Count);
            Assert.False(view.IsDiscoveringAll);
        }
        finally { Pump(view.DisposeAsync().AsTask()); session.Permits.Dispose(); }
    });

    [Fact]
    public void RenderProgressIsPublishedAcrossMetadataGeometryMaterialAndUploadStages() => Run(() =>
    {
        IProgress<RenderLoadProgress>? progress = null;
        var view = new SceneView
        {
            RenderProvider = async (_, updates, token) =>
            {
                progress = updates;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The generated provider ended unexpectedly.");
            }
        };
        var session = new DiscoverySession(1, 1);
        try
        {
            Pump(view.LoadAsync(session)); Pump(view.SelectResourceAsync(session.LoadedSection.Result.Identity));
            var loading = view.LoadRegionAsync(); PumpUntil(() => progress is not null);
            var overlay = (Grid)view.FindName("ViewportLoadingOverlay");
            var animation = (ProcessingAnimation)view.FindName("ViewportProcessing");
            var bar = (ProgressBar)view.FindName("ViewportLoadingProgress");
            var stageText = (TextBlock)view.FindName("ViewportLoadingStage");
            var countText = (TextBlock)view.FindName("ViewportLoadingCount");
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.True(animation.IsActive);
            Assert.False(view.Viewport.IsEnabled);
            Assert.True(bar.IsIndeterminate);
            Assert.Equal(Visibility.Collapsed, countText.Visibility);
            Assert.True(view.ActivityProgress.IsActive);
            Assert.Equal("Reading scene", view.ActivityProgress.Stage);
            progress!.Report(new("Metadata", 3, 12, 0));
            PumpUntil(() => view.ActivityProgress.Completed == 3);
            Assert.Equal("Reading scene", view.ActivityProgress.Stage);
            Assert.Equal(0, view.ActivityProgress.Total);
            foreach (var update in new[] { new RenderLoadProgress("Geometry", 2, 7, 0), new("Materials", 3, 5, 0), new("Loading", 4, 9, 0) })
            {
                progress.Report(update);
                var stage = update.Stage switch
                {
                    "Geometry" => "Loading geometry",
                    "Materials" => "Loading materials",
                    "Loading" => "Uploading",
                    _ => update.Stage
                };
                PumpUntil(() => view.ActivityProgress.Stage == stage && view.ActivityProgress.Completed == update.Completed);
                Assert.True(view.ActivityProgress.IsActive);
                Assert.Equal(update.Total, view.ActivityProgress.Total);
                Assert.Equal(stage, stageText.Text);
                Assert.False(bar.IsIndeterminate);
                Assert.Equal((double)update.Total, bar.Maximum);
                Assert.Equal((double)update.Completed, bar.Value);
                Assert.Equal($"{update.Completed:N0} / {update.Total:N0}", countText.Text);
                Assert.Equal(Visibility.Visible, countText.Visibility);
            }
            Assert.Equal("Uploading", view.ActivityProgress.Stage);
            Pump(view.SuspendAsync());
            Assert.True(loading.IsCanceled);
            Assert.False(view.ActivityProgress.IsActive);
            Assert.False(view.IsLoading);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.False(animation.IsActive);
            Assert.True(view.Viewport.IsEnabled);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceneOverlayTracksRegionDiscoveryAndClearsOnCompletionOrFailure(bool fail) => Run(() =>
    {
        IProgress<RenderLoadProgress>? progress = null;
        var pending = new TaskCompletionSource<SceneRegionDiscovery>(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = new SceneView
        {
            RegionProvider = (_, updates, _) => { progress = updates; return pending.Task; }
        };
        var session = new DiscoverySession(1, 1);
        try
        {
            Pump(view.LoadAsync(session));
            var loading = view.LoadSceneAsync(session.LoadedSection.Result.Identity);
            PumpUntil(() => progress is not null);
            var overlay = (Grid)view.FindName("ViewportLoadingOverlay");
            var animation = (ProcessingAnimation)view.FindName("ViewportProcessing");
            var bar = (ProgressBar)view.FindName("ViewportLoadingProgress");
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.True(animation.IsActive);
            Assert.True(bar.IsIndeterminate);
            Assert.Equal("Finding regions", ((TextBlock)view.FindName("ViewportLoadingStage")).Text);
            progress!.Report(new("Metadata", 2, 5, 0));
            PumpUntil(() => bar.Value == 2);
            Assert.False(bar.IsIndeterminate);
            Assert.Equal(5d, bar.Maximum);
            if (fail)
            {
                pending.SetException(new InvalidOperationException("Generated discovery failure"));
                Pump(Assert.ThrowsAsync<InvalidOperationException>(() => loading));
            }
            else
            {
                pending.SetResult(new([], []));
                Pump(loading);
            }
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.False(animation.IsActive);
            Assert.True(view.Viewport.IsEnabled);
        }
        finally { pending.TrySetResult(new([], [])); Pump(view.DisposeAsync().AsTask()); }
    });

    [Theory]
    [InlineData("MESH_PANEL_MAIN", "Mesh", "Mesh Panel Main")]
    [InlineData("partMeshName", "Mesh", "part Mesh Name")]
    [InlineData("Node 123", "Transform", "Transform")]
    [InlineData("NODE_123", "Transform", "Transform")]
    [InlineData("MESH_PANEL_MAIN:qd Instance42", "Mesh", "Mesh Panel Main")]
    [InlineData("MESH_PANEL_MAIN:qdInstance42", "Mesh", "Mesh Panel Main")]
    [InlineData("main_part : qd Instance 3", "Mesh", "main part")]
    [InlineData("dc-object/0/2002/1", "Transform", "Transform")]
    public void DisplayNamesKeepTechnicalIdentifiersOutOfPrimaryLabels(string source, string fallback, string expected) =>
        Assert.Equal(expected, SceneNameDisplay.Project(source, fallback));

    [Fact]
    public void RepeatedSiblingLabelsAreReadableAndDistinctWithoutChangingTheirSourceKeys() => Run(() =>
    {
        var view = new SceneView();
        var identity = new ResourceIdentity(new string('c', 64), 1, "dc-object/0/2002/1");
        var nodes = new[]
        {
            new RenderHierarchyNode("root", null, identity, identity.Key, Matrix4x4.Identity, Matrix4x4.Identity, null, []),
            new("first", "root", identity, "MESH_PANEL_MAIN", Matrix4x4.Identity, Matrix4x4.Identity, "first", []),
            new("second", "root", identity, "MESH_PANEL_MAIN", Matrix4x4.Identity, Matrix4x4.Identity, "second", [])
        };
        var snapshot = new RenderAssetSnapshot(new(identity.IndexContentId, "generated"), new Dictionary<string, RenderMesh>(), [],
            new Dictionary<ResourceIdentity, RenderMaterial>(), new(Vector3.Zero, Vector3.One), [], nodes, new(0,0,0,0,0,0), [], new());
        try
        {
            typeof(SceneView).GetMethod("BuildRenderHierarchy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [snapshot]);
            var root = Assert.IsType<RenderTreeRow>(((TreeView)view.FindName("RenderHierarchy")).Items[0]);
            Assert.Equal("Transform", root.Label);
            Assert.Equal(new[] { "Mesh Panel Main · 1", "Mesh Panel Main · 2" }, root.Children.Select(child => child.Label));
            Assert.Equal(new[] { "first", "second" }, root.Children.Select(child => child.Node.OccurrenceKey));
            Assert.All(root.Children, child => Assert.Equal("MESH_PANEL_MAIN", child.Node.Label));
        }
        finally { snapshot.Dispose(); Pump(view.DisposeAsync().AsTask()); }
    });

    private sealed class DiscoverySession : ISceneCatalogSession
    {
        private readonly int count, batchSize;
        private int visited;
        private int gatedQueryCalls;
        private bool running;
        private string? message;
        public DiscoverySession(int count, int batchSize) { this.count = count; this.batchSize = batchSize; LoadedSection = Section(Revision); }
        public bool Gated { get; set; }
        public bool QueryGated { get; set; }
        public bool IgnoreQueryCancellation { get; set; }
        public bool ExposeTotal { get; set; } = true;
        public bool Cancelled { get; private set; }
        public bool QueryCancelled { get; private set; }
        public int DiscoveryCalls { get; private set; }
        public TaskCompletionSource QueryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstQueryRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondQueryRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int GatedQueryCalls => Volatile.Read(ref gatedQueryCalls);
        public SemaphoreSlim Permits { get; } = new(0);
        public SceneLoadedSection LoadedSection { get; }
        public ArchiveSnapshot Snapshot => null!;
        public SourceRevision Revision { get; } = new(new string('c', 64), "generated-revision");
        public SceneDiscoveryProgress Progress => new(running, visited, ExposeTotal ? count : 0, visited, visited < count, message);
        public event EventHandler? Updated;
        public async Task DiscoverMoreAsync(CancellationToken token = default)
        {
            DiscoveryCalls++; running = true; message = null; Updated?.Invoke(this, EventArgs.Empty);
            try
            {
                if (Gated) await Permits.WaitAsync(token); else await Task.Yield();
                token.ThrowIfCancellationRequested();
                visited = Math.Min(count, visited + batchSize); ExposeTotal = true; Updated?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) { Cancelled = true; message = "Cancelled"; throw; }
            finally { running = false; Updated?.Invoke(this, EventArgs.Empty); }
        }
        public async Task<SceneCatalogPage> QueryAsync(string? search = null, long offset = 0, int limit = 100, CancellationToken token = default)
        {
            if (QueryGated)
            {
                QueryStarted.TrySetResult();
                try
                {
                    if (IgnoreQueryCancellation)
                    {
                        var gate = Interlocked.Increment(ref gatedQueryCalls) switch
                        {
                            1 => FirstQueryRelease,
                            2 => SecondQueryRelease,
                            _ => throw new InvalidOperationException("The generated query gate supports two concurrent calls.")
                        };
                        await gate.Task;
                    }
                    else await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) { QueryCancelled = true; throw; }
            }
            token.ThrowIfCancellationRequested();
            var rows = Enumerable.Range(0, visited).Select(index => new AssetDescriptor(new(Revision.IndexContentId, index, "scene"), Revision,
                "scene", $"Scene {index}", null, AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.Json, [], [])).ToArray();
            return new SceneCatalogPage(rows.Skip((int)offset).Take(limit).ToArray(), rows.Length, offset);
        }
        public Task<SceneLoadedSection> LoadAsync(ResourceIdentity identity, CancellationToken token = default) => Task.FromResult(LoadedSection);
        public Task RevalidateAsync(SceneLoadedSection section, CancellationToken token = default) => Task.CompletedTask;
        public Task ExportMetadataAsync(SceneLoadedSection section, string destination, CancellationToken token = default) => Task.CompletedTask;
        public void CancelDiscovery() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static SceneLoadedSection Section(SourceRevision revision)
    {
        var container = new ResourceIdentity(revision.IndexContentId, 0);
        var nodeIdentity = container with { MemberPath = "dc-object/0/2002/1" };
        var transform = new SourceTransform([1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1], MatrixLayout.RowMajor, VectorMultiplicationSide.Row);
        var node = new SceneNodeRecord(nodeIdentity, revision, 1, 0, Vector3.Zero, transform, transform, null, null, null, -1, 0, 0,
            new(container, 0, 196), new Dictionary<string, DecodedExtent>(), new byte[196]);
        var hierarchyNode = new SceneHierarchyNode(node, null, null, null, null, [], transform, transform, SceneTransformStatus.Available);
        var entity = new SceneEntityRecord(container with { MemberPath = "dc-object/1/2207/2" }, 2207, 2,
            new(IdentifierNamespace.Native, "1", container, 2002), [], 0, 0, 4, 0, new(container, 196, 48), new Dictionary<string, DecodedExtent>());
        var branch = new SceneCatalogInstance(entity.Identity, entity, "Branch", "Catalog", new(IdentifierNamespace.Native, "3", container, 3), new Dictionary<string, DecodedExtent>());
        var environment = new SceneEnvironmentRecord(container with { MemberPath = "environment" }, 1, new(container, 0, 0), [], ReadOnlyMemory<byte>.Empty);
        var document = new SceneSectionDocument(container with { MemberPath = "scene" }, container, revision,
            new(container, revision, [hierarchyNode], [nodeIdentity], []), [entity], [branch], environment, [], []);
        var result = new DecodeResult<SceneSectionDocument>(document, document.Identity, revision, [], [], [], [], new Dictionary<string, string>());
        return new(new(document.Identity, revision, "scene", "Scene", null, AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.Json, [], []),
            result, new Dictionary<ResourceIdentity, IReadOnlyList<NameEvidence>>(),
            new Dictionary<ResourceIdentity, IReadOnlyList<SceneEntityRecord>> { [nodeIdentity] = [entity] }, new Dictionary<ResourceIdentity, IReadOnlyList<AssetRelationship>>());
    }
    private static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Scene progress test timed out.");
        if (failure is not null) throw new InvalidOperationException("Scene progress test failed.", failure);
    }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private static void PumpUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) { timeout.Token.ThrowIfCancellationRequested(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); }
    }
}
