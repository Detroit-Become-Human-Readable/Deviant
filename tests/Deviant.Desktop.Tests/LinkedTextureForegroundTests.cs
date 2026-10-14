using System.Reflection;
using System.Windows.Threading;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class LinkedTextureForegroundTests
{
    [Fact]
    public void KnownNativeSelectionProducesPixelsBeforeBlockedBrowsingOrDetails()
        => Run((view, owner, cache, budget, catalog, source, native, group) =>
        {
            Pump(view.LoadNativeSelectionAsync(owner, cache, source, native, group, budget, "Linked colour"));
            AssertReady(view);
            Assert.Equal(0, catalog.Queries); Assert.Equal(0, catalog.Details);
            var pixels = view.NativeImageBrowser.ImageBrowser.CurrentDisplaySurface;
            Assert.NotNull(pixels); Assert.Equal(NativeTextureFixture.RgbaPixels(0), pixels!.Rgba8.ToArray());
            var selected = view.SelectedEntry;
            var currentSource = view.NativeImageBrowser.CurrentSource;
            var attach = view.AttachDiscoveryAsync(catalog);
            WaitUntil(() => catalog.Queries > 0 && catalog.Details > 0);
            Assert.False(attach.IsCompleted);
            AssertReady(view);
            Assert.Same(currentSource, view.NativeImageBrowser.CurrentSource);
            Assert.Same(pixels, view.NativeImageBrowser.ImageBrowser.CurrentDisplaySurface);
            catalog.Release(); Pump(attach);
            Assert.Equal(source, view.SelectedIdentity);
            Assert.Same(currentSource, view.NativeImageBrowser.CurrentSource);
            Assert.Same(pixels, view.NativeImageBrowser.ImageBrowser.CurrentDisplaySurface);
        });

    [Fact]
    public void ReselectionCancelsQueuedOlderPreviewAndPublishesOnlyTheNewLabel()
        => Run((view, owner, cache, budget, catalog, source, native, group) =>
        {
            Pump(view.LoadNativeSelectionAsync(owner, cache, source, native, group, budget, "Initial"));
            var gate = Field<SemaphoreSlim>(view, "nativeGate");
            Assert.True(gate.Wait(0));
            Task first, second;
            try
            {
                first = view.LoadNativeSelectionAsync(owner, cache, source, native, group, budget, "Older");
                second = view.LoadNativeSelectionAsync(owner, cache, source, native, group, budget, "Newer");
            }
            finally { gate.Release(); }
            Pump(Task.WhenAll(first, second));
            AssertReady(view);
            Assert.Equal("Newer", view.SelectedLabel);
            Assert.Equal(source, view.NativeImageBrowser.CurrentSource!.SelectedIdentity);
            Assert.Equal(0, catalog.Details);
        });

    [Fact]
    public void NavigationSuspensionCancelsBlockedBrowsingAndReleasesPreviewLeases()
        => Run((view, owner, cache, budget, catalog, source, native, group) =>
        {
            Pump(view.LoadNativeSelectionAsync(owner, cache, source, native, group, budget, "Linked colour"));
            AssertReady(view);
            var attach = view.AttachDiscoveryAsync(catalog);
            WaitUntil(() => catalog.Queries > 0 && catalog.Details > 0);
            Pump(view.SuspendAsync()); Pump(attach);
            Assert.True(view.IsSuspended);
            Assert.Null(view.NativeImageBrowser.CurrentSource);
            Assert.Equal(0, cache.ActivePreviewLeases);
        });

    [Fact]
    public void CallerCancellationPreemptsAQueuedForegroundPreview()
        => Run((view, owner, cache, budget, catalog, source, native, group) =>
        {
            Pump(view.LoadNativeSelectionAsync(owner, cache, source, native, group, budget, "Initial"));
            AssertReady(view);
            var gate = Field<SemaphoreSlim>(view, "nativeGate");
            Assert.True(gate.Wait(0));
            using var cancellation = new CancellationTokenSource();
            Task pending;
            try
            {
                pending = view.LoadNativeSelectionAsync(owner, cache, source, native, group, budget, "Cancelled", cancellation.Token);
                cancellation.Cancel();
            }
            finally { gate.Release(); }
            Pump(Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending));
            Pump(view.ClearAsync());
            Assert.Null(view.SelectedIdentity); Assert.Null(view.NativeImageBrowser.CurrentSource);
            Assert.Equal(0, cache.ActivePreviewLeases);
            Assert.Equal(0, budget.UsedBytes);
        });

    [Fact]
    public void ForeignSourceSelectionIsRejectedBeforeReplacingReadyPixels()
        => Run((view, owner, cache, budget, catalog, source, native, group) =>
        {
            Pump(view.LoadNativeSelectionAsync(owner, cache, source, native, group, budget, "Initial"));
            AssertReady(view);
            var pixels = view.NativeImageBrowser.ImageBrowser.CurrentDisplaySurface;
            var error = Pump(Assert.ThrowsAsync<Deviant.Core.ArchiveException>(() => view.LoadNativeSelectionAsync(owner, cache,
                source with { IndexContentId = "other-generated-index" }, native, group, budget, "Foreign")));
            Assert.Equal("SourceChanged", error.Code);
            Assert.Same(pixels, view.NativeImageBrowser.ImageBrowser.CurrentDisplaySurface);
        });

    private static void Run(Action<TextureExplorerView, NativeTextureSession, CacheManager, AssetMemoryBudget,
        BlockedCatalog, ResourceIdentity, ResourceIdentity, ResourceIdentity> check)
        => WorkspaceCommitTests.Run((window, fixture) =>
        {
            NativeTextureFixture.WriteArchive(fixture.Index);
            var snapshot = Pump(new ArchiveReader().OpenAsync(fixture.Index));
            var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
            var owner = new NativeTextureSession(catalogs);
            var budget = new AssetMemoryBudget();
            var cache = new CacheManager(fixture.Library, new(fixture.Cache));
            var view = new TextureExplorerView();
            var blocked = new BlockedCatalog(snapshot);
            try
            {
                Pump(catalogs.EnrichmentTask);
                check(view, owner, cache, budget, blocked, new(snapshot.IndexContentId, 2),
                    new(snapshot.IndexContentId, 1, "filetext/40"), new(snapshot.IndexContentId, 0));
            }
            finally
            {
                blocked.Release(); Pump(view.DisposeAsync().AsTask()); Pump(owner.DisposeAsync().AsTask());
                Pump(catalogs.DisposeAsync().AsTask());
            }
            Assert.Equal(0, budget.UsedBytes);
            Assert.Equal(0, cache.ActivePreviewLeases);
        });

    private sealed class BlockedCatalog(Deviant.Core.ArchiveSnapshot snapshot) : ITextureDiscoveryCatalog
    {
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Queries { get; private set; }
        public int Details { get; private set; }
        public Deviant.Core.ArchiveSnapshot Snapshot => snapshot;
        public SourceRevision Revision => new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        public TextureDiscoveryFilterOptions FilterOptions => new([], [], [], []);
        public TextureNamingCoverage Coverage => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, [], []);
        public event EventHandler? Updated { add { } remove { } }
        public async Task<TextureDiscoveryPage> QueryAsync(TextureDiscoveryQuery query, CancellationToken token = default)
        { Queries++; await released.Task.WaitAsync(token); return new([], 0, 0); }
        public async Task<TextureDiscoveryEntry?> GetDetailsAsync(ResourceIdentity identity, CancellationToken token = default)
        { Details++; await released.Task.WaitAsync(token); return null; }
        public Task SetAliasAsync(ResourceIdentity identity, string? alias, CancellationToken token = default) => Task.CompletedTask;
        public void Release() => released.TrySetResult();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void AssertReady(TextureExplorerView view) => Assert.True(view.CurrentPreviewState == TexturePreviewState.Ready,
        $"Preview {view.CurrentPreviewState}: {view.PreviewFailure?.Code}: {view.PreviewFailure?.Message}");
    private static void WaitUntil(Func<bool> ready)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!ready()) Pump(Task.Delay(10, timeout.Token)); }
    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
}
