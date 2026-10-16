using System.Windows;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private readonly AssetModelView modelView = new();
    private AssetViewportService? viewportService;
    private AssetMemoryBudget? renderMemoryBudget;
    private NativeModelSession? nativeModels;
    private SceneRenderSession? sceneRenderer;
    private bool loadingRenderDependencies;
    private SceneRenderViewState? sceneReturnState;
    private Task sceneNavigationDrain = Task.CompletedTask;
    private WorkspacePreparationScheduler? workspacePreparation;
    private WorkspacePreparationScheduler EnsureWorkspacePreparation() => workspacePreparation ??= new();
    private int renderForegroundWork;
    private IDisposable? textureForegroundPause;
    private IDisposable BeginRenderForeground()
    {
        renderForegroundWork++; RefreshForegroundDiscoveryPause();
        return new RenderForegroundLease(this, EnsureWorkspacePreparation().EnterForeground());
    }
    private void RefreshForegroundDiscoveryPause()
    { if (renderForegroundWork > 0 && textureForegroundPause is null && textureCatalogs is not null) textureForegroundPause = textureCatalogs.PauseBackgroundDiscovery(); }
    private sealed class RenderForegroundLease(AudioWindow owner, IDisposable scheduler) : IDisposable
    {
        private AudioWindow? owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null); if (current is null) return;
            if (--current.renderForegroundWork == 0) { current.textureForegroundPause?.Dispose(); current.textureForegroundPause = null; }
            scheduler.Dispose();
        }
    }

    public AssetModelView ModelBrowser => modelView;
    public NativeModelSession? ModelSession => nativeModels;
    public AssetMemoryBudget? RenderMemoryBudget => renderMemoryBudget;
    public AssetViewportService? ViewportService => viewportService;

    private void InitializeSceneRendering()
    {
        sceneView.RenderProvider = LoadRenderedSceneAsync;
        sceneView.StreamingRenderProvider = LoadStreamedSceneAsync;
        sceneView.ForegroundWorkProvider = BeginRenderForeground;
        sceneView.BackgroundDrainProvider = token => workspacePreparation?.DrainAsync(token) ?? Task.CompletedTask;
        PreviewMouseDown += (_, _) => workspacePreparation?.NotifyActivity();
        PreviewKeyDown += (_, _) => workspacePreparation?.NotifyActivity();
        PreviewMouseWheel += (_, _) => workspacePreparation?.NotifyActivity();
        sceneView.RegionProviderWithLargeData = DescribeSceneRegionsAsync;
        sceneView.ModelPreviewProvider = ShowModelAsync;
        sceneView.GeometryExportProvider = ExportSceneFbxAsync;
        sceneView.TexturePreviewProvider = OpenRenderedTextureAsync;
        sceneView.LightingTexturePreviewProvider = OpenRenderedTextureAsync;
        modelView.TexturePreviewProvider = OpenRenderedTextureAsync;
        modelView.BackRequested += async (_, _) =>
        {
            try
            {
                if (currentView == "characters") { charactersView.CancelPreview(); await modelView.UnloadAsync(); }
                else await BackToSceneAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { NavigationError(error); }
        };
    }

    private void EnsureViewportService()
    {
        viewportService ??= new(Dispatcher);
        viewportService.ConfigureShaderBytecodeCache(cache);
        sceneView.SetViewportService(viewportService);
        modelView.SetViewportService(viewportService);
    }

    private async Task<RenderAssetSnapshot> LoadRenderedSceneAsync(SceneRenderRequest request,
        IProgress<RenderLoadProgress>? progress, CancellationToken token)
    {
        var renderer = await EnsureSceneRendererAsync(token);
        EnsureViewportService();
        return await renderer.LoadAsync(request, progress, token);
    }

    private async Task<RenderAssetSnapshot> LoadStreamedSceneAsync(SceneRenderRequest request,
        IProgress<RenderLoadProgress>? progress, Func<RenderAssetSnapshot, CancellationToken, Task> publish, CancellationToken token)
    {
        var renderer = await EnsureSceneRendererAsync(token);
        EnsureViewportService();
        return await renderer.LoadAsync(request, progress, token, publish);
    }

    private async Task<SceneRegionDiscovery> DescribeSceneRegionsAsync(SceneLoadedSection section, bool allowLargeData,
        IProgress<RenderLoadProgress>? progress, CancellationToken token)
    {
        var renderer = await EnsureSceneRendererAsync(token);
        return await renderer.DescribeRegionsAsync(section, progress, token, allowLargeData);
    }

    private async Task<SceneRenderSession> EnsureSceneRendererAsync(CancellationToken token)
    {
        var current = session ?? throw new InvalidOperationException("Open an archive first.");
        loadingRenderDependencies = true;
        try { await EnsureNativeTextureSourceAsync(current, token); }
        finally { loadingRenderDependencies = false; }
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(current, session) || closingInProgress) throw new OperationCanceledException(token);
        var textures = textureNative ?? throw new InvalidOperationException("Textures are unavailable.");
        if (nativeModels is null || !ReferenceEquals(nativeModels.Textures, textures))
        {
            renderMemoryBudget ??= new();
            nativeModels = new(textures, renderMemoryBudget, cache);
            sceneRenderer = new(current.Snapshot, nativeModels, renderMemoryBudget, cache, EnsureWorkspacePreparation());
        }
        return sceneRenderer!;
    }

    public async Task ShowModelAsync(RenderAssetSnapshot source, RenderOccurrence occurrence, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var current = session ?? throw new InvalidOperationException("Open an archive first.");
        if (source.Revision != new SourceRevision(current.Snapshot.IndexContentId, current.Snapshot.PhysicalSourceStamp))
            throw new ArchiveException("SourceChanged", "The model belongs to another archive revision.");
        var navigation = BeginNavigation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, navigation.Token);
        await sceneNavigationDrain.WaitAsync(linked.Token);
        CaptureSceneReturnState();
        EnsureViewportService();
        ShowBrowse("model");
        if (ReferenceEquals(charactersView.PreviewContent.Content, modelView)) charactersView.PreviewContent.Content = null;
        ScenesHost.Content = modelView;
        await modelView.LoadAsync(source, occurrence, linked.Token);
        modelView.SetBackLabel("Back to scene");
        await sceneView.ClearRenderingAsync();
    }

    public Task BackToSceneAsync() => BackToSceneAsync(CancellationToken.None);

    public async Task BackToSceneAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var navigation = BeginNavigation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, navigation.Token);
        await ShowScenesAsync(linked.Token);
    }

    private Task OpenRenderedTextureAsync(RenderTexture texture, SourceRevision revision, CancellationToken token) =>
        OpenRenderedTextureAsync(RenderTextureSelection.From(texture, revision, "Texture"), token);

    private async Task OpenRenderedTextureAsync(RenderTextureSelection selection, CancellationToken token)
    {
        var current = session ?? throw new InvalidOperationException("Open an archive first.");
        if (selection.Revision != new SourceRevision(current.Snapshot.IndexContentId, current.Snapshot.PhysicalSourceStamp) ||
            selection.SourceIdentity.IndexContentId != selection.Revision.IndexContentId ||
            selection.CatalogIdentity.IndexContentId != selection.Revision.IndexContentId ||
            selection.NativeIdentity.IndexContentId != selection.Revision.IndexContentId)
            throw new ArchiveException("SourceChanged", "The texture belongs to another archive revision.");
        var navigation = BeginNavigation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, navigation.Token);
        ShowBrowse("textures");
        await sceneNavigationDrain.WaitAsync(linked.Token);
        await textureNavigationDrain.WaitAsync(linked.Token);
        await EnsureNativeTextureSourceAsync(current, linked.Token);
        if (!ReferenceEquals(current, session) || navigation.Generation != navigationGeneration) return;
        var budget = renderMemoryBudget ?? throw new InvalidOperationException("The asset session is unavailable.");
        await textureExplorer.LoadNativeSelectionAsync(textureNative ?? throw new InvalidOperationException("Textures are unavailable."),
            cache, selection.SourceIdentity, selection.NativeIdentity, selection.CatalogIdentity, budget, selection.Label, linked.Token);
        if (ReferenceEquals(current, session) && navigation.Generation == navigationGeneration && currentView == "textures")
            _ = TrackLibraryTask(AttachLinkedTextureCatalogsAsync(current, navigation.Generation, navigation.Token));
    }

    private async Task ClearSceneRenderingAsync()
    {
        await sceneNavigationDrain;
        if (!loadingRenderDependencies) ClearSceneReturnState();
        if (nativeModels is null && sceneRenderer is null && modelView.CurrentSnapshot is null) return;
        await modelView.UnloadAsync();
        if (!loadingRenderDependencies) await sceneView.ClearRenderingAsync();
        viewportService?.ReleaseUnusedResources();
        if (sceneRenderer is not null) await sceneRenderer.DisposeAsync();
        nativeModels = null; sceneRenderer = null;
        ScenesHost.Content = sceneView;
    }

    private async Task DisposeSceneRenderingAsync()
    {
        await ClearSceneRenderingAsync();
        if (workspacePreparation is { } preparation)
        { workspacePreparation = null; await preparation.DisposeAsync(); }
        await modelView.DisposeAsync();
        await sceneView.DisposeAsync();
        viewportService?.Dispose(); viewportService = null;
    }

    private void CaptureSceneReturnState()
    {
        var captured = sceneView.CaptureRenderState();
        if (captured is null) return;
        var previous = sceneReturnState; sceneReturnState = captured; previous?.Dispose();
    }

    private void ClearSceneReturnState()
    {
        sceneReturnState?.Dispose(); sceneReturnState = null;
    }

    private void PauseSceneWorkForNavigation()
    {
        if (sceneNavigationDrain.IsCompleted)
        {
            try { CaptureSceneReturnState(); }
            catch (Exception error) { NavigationError(error); }
        }
        var suspended = sceneView.SuspendAsync();
        sceneNavigationDrain = ReleaseSceneViewsAsync(sceneNavigationDrain, suspended);
        TrackLibraryTask(sceneNavigationDrain);
    }

    private async Task ReleaseSceneViewsAsync(Task previous, Task suspended)
    {
        await previous; await suspended;
        await modelView.UnloadAsync();
        await sceneView.ClearRenderingAsync();
        if (sceneRenderer is not null) await sceneRenderer.DisposeAsync();
        nativeModels = null; sceneRenderer = null;
    }

    private async Task RestoreSceneReturnStateAsync(long navigation, AudioExplorerSession owner, CancellationToken token)
    {
        var state = sceneReturnState;
        if (state is null) return;
        if (state.Revision != new SourceRevision(owner.Snapshot.IndexContentId, owner.Snapshot.PhysicalSourceStamp))
        { ClearSceneReturnState(); throw new ArchiveException("SourceChanged", "The saved scene belongs to another archive revision."); }
        if (!ReferenceEquals(renderMemoryBudget, state.MemoryBudget))
            throw new InvalidOperationException("The scene memory budget changed. Reload the scene.");
        await sceneView.LoadSceneStructureAsync(state.SectionIdentity, token);
        token.ThrowIfCancellationRequested();
        if (navigation != navigationGeneration || !ReferenceEquals(owner, session) || currentView != "scenes") return;
        await sceneView.RestoreRenderStateAsync(state, token);
        if (ReferenceEquals(sceneReturnState, state)) ClearSceneReturnState();
    }
}
