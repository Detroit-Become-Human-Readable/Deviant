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

    public AssetModelView ModelBrowser => modelView;
    public NativeModelSession? ModelSession => nativeModels;
    public AssetMemoryBudget? RenderMemoryBudget => renderMemoryBudget;
    public AssetViewportService? ViewportService => viewportService;

    private void InitializeSceneRendering()
    {
        sceneView.RenderProvider = LoadRenderedSceneAsync;
        sceneView.RegionProviderWithLargeData = DescribeSceneRegionsAsync;
        sceneView.ModelPreviewProvider = ShowModelAsync;
        sceneView.TexturePreviewProvider = OpenRenderedTextureAsync;
        modelView.TexturePreviewProvider = OpenRenderedTextureAsync;
        modelView.BackRequested += async (_, _) =>
        {
            try { await BackToSceneAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { NavigationError(error); }
        };
    }

    private void EnsureViewportService()
    {
        viewportService ??= new(Dispatcher);
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
        try { await EnsureTextureCatalogsAsync(current, token, ownerQuery: true); }
        finally { loadingRenderDependencies = false; }
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(current, session) || closingInProgress) throw new OperationCanceledException(token);
        var textures = textureNative ?? throw new InvalidOperationException("Textures are unavailable.");
        if (nativeModels is null || !ReferenceEquals(nativeModels.Textures, textures))
        {
            renderMemoryBudget = new();
            nativeModels = new(textures, renderMemoryBudget);
            sceneRenderer = new(current.Snapshot, nativeModels, renderMemoryBudget);
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
        EnsureViewportService();
        ShowBrowse("model");
        ScenesHost.Content = modelView;
        await modelView.LoadAsync(source, occurrence, linked.Token);
    }

    public async Task BackToSceneAsync()
    {
        BeginNavigation();
        await modelView.UnloadAsync();
        ScenesHost.Content = sceneView;
        ShowBrowse("scenes");
    }

    private async Task OpenRenderedTextureAsync(RenderTexture texture, SourceRevision revision, CancellationToken token)
    {
        if (texture.SourceIdentity is not { } identity || texture.CatalogIdentity is not { } catalog)
            throw new InvalidOperationException("The texture source is unavailable.");
        var current = session ?? throw new InvalidOperationException("Open an archive first.");
        if (revision != new SourceRevision(current.Snapshot.IndexContentId, current.Snapshot.PhysicalSourceStamp) ||
            identity.IndexContentId != revision.IndexContentId || catalog.IndexContentId != revision.IndexContentId ||
            texture.NativeIdentity.IndexContentId != revision.IndexContentId)
            throw new ArchiveException("SourceChanged", "The texture belongs to another archive revision.");
        var navigation = BeginNavigation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, navigation.Token);
        await ShowTexturesAsync(linked.Token);
        if (!ReferenceEquals(current, session) || navigation.Generation != navigationGeneration) return;
        var budget = renderMemoryBudget ?? throw new InvalidOperationException("The asset session is unavailable.");
        await textureExplorer.SelectNativeResourceAsync(identity, texture.NativeIdentity, catalog, budget, linked.Token);
    }

    private async Task ClearSceneRenderingAsync()
    {
        if (nativeModels is null && sceneRenderer is null && modelView.CurrentSnapshot is null) return;
        await modelView.UnloadAsync();
        if (!loadingRenderDependencies) await sceneView.ClearRenderingAsync();
        if (sceneRenderer is not null) await sceneRenderer.DisposeAsync();
        nativeModels = null; sceneRenderer = null; renderMemoryBudget = null;
        ScenesHost.Content = sceneView;
    }

    private async Task DisposeSceneRenderingAsync()
    {
        await ClearSceneRenderingAsync();
        await modelView.DisposeAsync();
        await sceneView.DisposeAsync();
        viewportService?.Dispose(); viewportService = null;
    }
}
