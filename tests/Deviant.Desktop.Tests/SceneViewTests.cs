using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class SceneViewTests
{
    [Fact]
    public void LoadsPagesAndSearchesThroughTheSession() => Run(() =>
    {
        var session = new FakeSession(205);
        var view = new SceneView();
        try
        {
            Pump(view.LoadAsync(session));
            Assert.Equal(100, view.CurrentRows.Count);
            Assert.Equal(0, session.LastOffset);
            ((System.Windows.Controls.Button)view.FindName("Next")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpUntil(() => session.LastOffset == 100);
            Assert.Equal(100, view.CurrentRows.First().Identity.Ordinal);
            var search = (System.Windows.Controls.TextBox)view.FindName("Search");
            search.Text = "Scene 17";
            PumpUntil(() => session.LastSearch == "Scene 17");
            Assert.Equal("Scene 17", session.LastSearch);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void DuplicateSceneDisplayNamesHaveStableSuffixesAndRetainRawDescriptors() => Run(() =>
    {
        var session = new FakeSession(2);
        session.Entries[0] = session.Entries[0] with { Label = "ROOM_EXTERIOR:qd Instance1" };
        session.Entries[1] = session.Entries[1] with { Label = "ROOM_EXTERIOR:qd Instance2" };
        var view = new SceneView();
        try
        {
            Pump(view.LoadAsync(session));
            Assert.Equal(new[] { "Room Exterior · 1", "Room Exterior · 2" }, view.CurrentRows.Select(row => row.Label));
            Assert.Equal(session.Entries.Select(entry => entry.Identity), view.CurrentRows.Select(row => row.Identity));
            Assert.Equal(session.Entries.Select(entry => entry.Label), view.CurrentRows.Select(row => row.Descriptor.Label));
            Pump(view.LoadAsync(session));
            Assert.Equal(new[] { "Room Exterior · 1", "Room Exterior · 2" }, view.CurrentRows.Select(row => row.Label));
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void NewLoadCancelsOldQueryAndKeepsLatestSessionRows()
    {
        Run(() =>
        {
            var first = new FakeSession(2) { DelayQueries = true };
            var second = new FakeSession(1);
            var view = new SceneView();
            try
            {
                var oldLoad = view.LoadAsync(first);
                PumpUntil(() => first.QueryStarted);
                var latestLoad = view.LoadAsync(second);
                Pump(Task.WhenAll(oldLoad, latestLoad));
                Assert.Same(second, view.CurrentSession);
                Assert.Single(view.CurrentRows);
                Assert.True(first.QueryCancelled);
            }
            finally { Pump(view.DisposeAsync().AsTask()); }
        });
    }

    [Fact]
    public void FailedExplicitLoadClearsPreviousSceneState()
    {
        Run(() =>
        {
            var session = new FakeSession(1);
            var view = new SceneView();
            try
            {
                Pump(view.LoadAsync(session));
                Pump(Assert.ThrowsAsync<InvalidOperationException>(() =>
                    view.SelectResourceAsync(new(session.Revision.IndexContentId, 0))));
                Assert.Null(view.CurrentSection);
                Assert.Null(((System.Windows.Controls.TreeView)view.FindName("Hierarchy")).ItemsSource);
            }
            finally { Pump(view.DisposeAsync().AsTask()); }
        });
    }

    [Fact]
    public void ProgrammaticNodeSelectionSelectsTheRealTreeItem()
    {
        Run(() =>
        {
            var session = new FakeSession(1) { LoadedSection = SyntheticSection() };
            var view = new SceneView();
            try
            {
                Pump(view.LoadAsync(session));
                Pump(view.SelectResourceAsync(SyntheticSection().Result.Value!.Hierarchy.Nodes[0].Identity));
                view.Measure(new Size(960, 640));
                view.Arrange(new Rect(0, 0, 960, 640));
                view.UpdateLayout();
                var tree = (TreeView)view.FindName("Hierarchy");
                tree.UpdateLayout();
                var item = Assert.IsType<TreeViewItem>(tree.ItemContainerGenerator.ContainerFromIndex(0));
                PumpUntil(() => item.IsSelected);
                Assert.True(item.IsSelected);
                Assert.Equal(view.SelectedIdentity, SyntheticSection().Result.Value!.Hierarchy.Nodes[0].Identity);
            }
            finally { Pump(view.DisposeAsync().AsTask()); }
        });
    }

    [Fact]
    public void DoubleClickLoadFailureIsShownInStatus()
    {
        Run(() =>
        {
            var view = new SceneView();
            try
            {
                Pump(view.LoadAsync(new FakeSession(1)));
                var rows = (ListBox)view.FindName("Rows");
                rows.SelectedIndex = 0;
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent };
                typeof(SceneView).GetMethod("Rows_DoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, args]);
                PumpUntil(() => ((TextBlock)view.FindName("Status")).Text.Contains("Synthetic session", StringComparison.Ordinal));
            }
            finally { Pump(view.DisposeAsync().AsTask()); }
        });
    }

    [Fact]
    public void TextureOwnerQueryUsesOnlySelectedNodeEntities()
    {
        Run(() =>
        {
            var loaded = SyntheticSection();
            var session = new FakeSession(1) { LoadedSection = loaded };
            ResourceIdentity? requestedOwner = null;
            var view = new SceneView
            {
                TextureOwnerQueryProvider = (owner, offset, limit, token) =>
                {
                    requestedOwner = owner;
                    var entry = new TextureDiscoveryEntry(new(new string('d', 64), 7), 7, 2229, "Texture",
                        TextureNameKind.Fallback, null, null, null, null, null, null, null, [], [], [], [], [], [], "texture", "");
                    var page = new TextureDiscoveryPage([entry], 1, offset);
                    return Task.FromResult(new TextureOwnerQueryResult(owner, session.Revision, page, TextureOwnerQueryState.Ready, false));
                }
            };
            try
            {
                Pump(view.LoadAsync(session));
                Pump(view.SelectResourceAsync(loaded.Result.Value!.Hierarchy.Nodes[0].Identity));
                ((Button)view.FindName("QueryTextures")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => requestedOwner is not null);
                Assert.Equal(loaded.Result.Value!.Entities.Single().Identity, requestedOwner);
            }
            finally { Pump(view.DisposeAsync().AsTask()); }
        });
    }

    [Fact]
    public void TextureOwnerQueryRejectsStaleProviderRevision()
    {
        Run(() =>
        {
            var loaded = SyntheticSection();
            var session = new FakeSession(1) { LoadedSection = loaded };
            var view = new SceneView
            {
                TextureOwnerQueryProvider = (owner, offset, limit, token) =>
                {
                    var page = new TextureDiscoveryPage([], 0, offset);
                    return Task.FromResult(new TextureOwnerQueryResult(owner,
                        new SourceRevision(new string('e', 64), "stale"), page, TextureOwnerQueryState.Ready, false));
                }
            };
            try
            {
                Pump(view.LoadAsync(session));
                Pump(view.SelectResourceAsync(loaded.Result.Value!.Hierarchy.Nodes[0].Identity));
                ((Button)view.FindName("QueryTextures")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => ((TextBlock)view.FindName("Status")).Text.Contains("another archive revision", StringComparison.Ordinal));
                Assert.Empty(((ListBox)view.FindName("TextureResults")).Items);
            }
            finally { Pump(view.DisposeAsync().AsTask()); }
        });
    }

    private static SceneLoadedSection SyntheticSection()
    {
        var indexId = new string('c', 64);
        var revision = new SourceRevision(indexId, "synthetic-revision");
        var container = new ResourceIdentity(indexId, 0);
        var nodeIdentity = container with { MemberPath = "dc-object/0/2002/1" };
        var matrix = new SourceTransform([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1], MatrixLayout.RowMajor, VectorMultiplicationSide.Row);
        var node = new SceneNodeRecord(nodeIdentity, revision, 1, 0, Vector3.Zero, matrix, matrix, null, null, null, -1, 0, 0,
            new(container, 0, 196), new Dictionary<string, DecodedExtent>(), new byte[196]);
        var hierarchyNode = new SceneHierarchyNode(node, null, null, null, null, [], matrix, matrix, SceneTransformStatus.Available);
        var hierarchy = new SceneHierarchy(container, revision, [hierarchyNode], [nodeIdentity], []);
        var entityIdentity = container with { MemberPath = "dc-object/2/3000/1" };
        var entity = new SceneEntityRecord(entityIdentity, 3000, 1,
            new ResourceReference(IdentifierNamespace.Native, "1", entityIdentity, 2002), [], 0, 0, 3, 0,
            new(container, 0, 49), new Dictionary<string, DecodedExtent>());
        var environment = new SceneEnvironmentRecord(container with { MemberPath = "dc-object/1/3000/1" }, 1,
            new(container, 0, 0), [], ReadOnlyMemory<byte>.Empty);
        var sectionIdentity = container with { MemberPath = SceneSectionReader.MemberPath };
        var document = new SceneSectionDocument(sectionIdentity, container, revision, hierarchy, [entity], [], environment, [], []);
        var decoded = new DecodeResult<SceneSectionDocument>(document, sectionIdentity, revision, [], [], [], [], new Dictionary<string, string>());
        var descriptor = new AssetDescriptor(sectionIdentity, revision, "scene", "Synthetic scene", null,
            AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.Json, [], []);
        return new SceneLoadedSection(descriptor, decoded, new Dictionary<ResourceIdentity, IReadOnlyList<NameEvidence>>(),
            new Dictionary<ResourceIdentity, IReadOnlyList<SceneEntityRecord>> { [nodeIdentity] = [entity] },
            new Dictionary<ResourceIdentity, IReadOnlyList<AssetRelationship>>());
    }

    private static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Scene view test timed out.");
        if (failure is not null) throw new InvalidOperationException("Scene view test failed.", failure);
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(5);
        }
    }

    private sealed class FakeSession : ISceneCatalogSession
    {
        private readonly string indexId = new('b', 64);
        public FakeSession(int count)
        {
            var revision = new SourceRevision(indexId, "synthetic-revision");
            var entries = Enumerable.Range(0, count)
                .Select(index => new AssetDescriptor(new(indexId, index), revision, "scene", $"Scene {index}", null,
                    AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.Json, [], []))
                .ToArray();
            Entries = entries;
            Revision = revision;
        }
        public AssetDescriptor[] Entries { get; }
        public SceneLoadedSection? LoadedSection { get; init; }
        public bool DelayQueries { get; init; }
        public bool QueryStarted { get; private set; }
        public bool QueryCancelled { get; private set; }
        public string? LastSearch { get; private set; }
        public long LastOffset { get; private set; }
        public ArchiveSnapshot Snapshot => null!;
        public SourceRevision Revision { get; }
        public SceneDiscoveryProgress Progress => new(false, Entries.Length, Entries.Length, Entries.Length, false);
        public event EventHandler? Updated;

        public async Task DiscoverMoreAsync(CancellationToken token = default)
        {
            await Task.CompletedTask;
            Updated?.Invoke(this, EventArgs.Empty);
        }
        public async Task<SceneCatalogPage> QueryAsync(string? search = null, long offset = 0, int limit = 100, CancellationToken token = default)
        {
            QueryStarted = true;
            LastSearch = search;
            LastOffset = offset;
            if (DelayQueries)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { QueryCancelled = true; throw; }
            }
            var filtered = Entries.Where(entry => string.IsNullOrWhiteSpace(search) ||
                entry.Label.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
            return new(filtered.Skip((int)offset).Take(limit).ToArray(), filtered.Length, offset);
        }
        public Task<SceneLoadedSection> LoadAsync(ResourceIdentity identity, CancellationToken token = default) =>
            LoadedSection is { } loaded ? Task.FromResult(loaded) :
            Task.FromException<SceneLoadedSection>(new InvalidOperationException("Synthetic session does not load sections."));
        public Task RevalidateAsync(SceneLoadedSection section, CancellationToken token = default) => Task.CompletedTask;
        public Task ExportMetadataAsync(SceneLoadedSection section, string destination, CancellationToken token = default) => Task.CompletedTask;
        public void CancelDiscovery() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
