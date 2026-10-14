using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private TextureCatalogSession? textureCatalogs;
    private NativeTextureSession? textureNative;
    private TextureDiscoverySession? textureDiscovery;
    private Task<TextureDiscoverySession>? textureDiscoveryCreation;
    private IDisposable? textureDependencyPause;
    private Task textureNavigationDrain = Task.CompletedTask;
    private readonly Dictionary<string, NavigationItem> textureContextNodes = new(StringComparer.Ordinal);
    private readonly DispatcherTimer textureCatalogTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Task textureContextRefresh = Task.CompletedTask;
    private bool textureContextRefreshPending;
    private CancellationTokenSource? textureContextRefreshCancellation;
    public Action TextureCatalogCancellation => CancelTextureCatalogWork;
    public void CancelTextureCatalogWork()
    {
        textureCatalogTimer.Stop(); textureContextRefreshPending = false;
        textureContextRefreshCancellation?.Cancel();
        textureExplorer.CancelWork();
        textureDiscovery?.Cancel();
        textureCatalogs?.Cancel();
    }

    private void PauseTextureWorkForNavigation()
    {
        if (currentView != "textures") return;
        var paused = textureExplorer.SuspendAsync();
        textureNavigationDrain = textureNavigationDrain.IsCompleted ? paused : Task.WhenAll(textureNavigationDrain, paused);
        TrackLibraryTask(textureNavigationDrain);
    }

    private void InitializeTextureCatalogs()
    {
        textureCatalogTimer.Tick += async (_, _) =>
        {
            textureCatalogTimer.Stop();
            try { await RefreshTextureCatalogsAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!closingInProgress) NavigationError(error); }
        };
    }

    private async Task EnsureNativeTextureSourceAsync(AudioExplorerSession owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(owner, session)) return;
        if (textureCatalogs is not null && textureNative is not null &&
            ReferenceEquals(textureCatalogs.Snapshot, owner.Snapshot) &&
            textureCatalogs.Progress.State is not (TextureCatalogSessionState.Cancelled or TextureCatalogSessionState.Failed))
            return;

        if (textureCatalogs is not null || textureNative is not null || textureDiscovery is not null || textureDiscoveryCreation is not null)
            await ClearTextureCatalogsAsync();
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(owner, session)) return;
        var catalogs = new TextureCatalogSession(owner.Snapshot, new(owner.Snapshot.IndexContentId, owner.Snapshot.PhysicalSourceStamp),
            sessionOptions: TextureCatalogOptions, referenceProvider: EnsureSharedCatalogReferences(owner));
        textureCatalogs = catalogs;
        textureDependencyPause = catalogs.PauseBackgroundDiscovery();
        RefreshForegroundDiscoveryPause();
        textureNative = new(catalogs);
        catalogs.Updated += TextureCatalogsUpdated;
        _ = TrackLibraryTask(ObserveTextureCatalogsAsync(catalogs));
    }

    private async Task EnsureTextureCatalogsAsync(AudioExplorerSession owner, CancellationToken token, bool ownerQuery = false)
    {
        await EnsureNativeTextureSourceAsync(owner, token);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(owner, session) || closingInProgress) return;
        var catalogs = textureCatalogs ?? throw new InvalidOperationException("Texture catalogs are unavailable.");
        var textureOwner = textureNative ?? throw new InvalidOperationException("Textures are unavailable.");
        textureDependencyPause?.Dispose(); textureDependencyPause = null;
        if (textureDiscovery is null)
        {
            var discoveryOptions = TextureDiscoveryOptions;
            var creation = textureDiscoveryCreation ??= Task.Run(() => new TextureDiscoverySession(catalogs, textureOwner, aliases, cache, discoveryOptions));
            TextureDiscoverySession created;
            try { created = await creation; }
            catch
            {
                if (ReferenceEquals(textureDiscoveryCreation, creation)) textureDiscoveryCreation = null;
                throw;
            }
            if (!ReferenceEquals(catalogs, textureCatalogs) || !ReferenceEquals(owner, session) || closingInProgress)
            {
                created.Cancel(); await created.DisposeAsync();
                token.ThrowIfCancellationRequested(); return;
            }
            if (token.IsCancellationRequested)
            {
                if (textureDiscovery is null && ReferenceEquals(textureDiscoveryCreation, creation))
                {
                    textureDiscoveryCreation = null;
                    created.Cancel(); await created.DisposeAsync();
                }
                token.ThrowIfCancellationRequested();
            }
            if (textureDiscovery is null && !ReferenceEquals(textureDiscoveryCreation, creation))
            {
                await EnsureTextureCatalogsAsync(owner, token, ownerQuery);
                return;
            }
            if (textureDiscovery is null)
            {
                textureDiscovery = created;
                created.Updated += TextureDiscoveryUpdated;
                textureExplorer.DiscoveryCancel = () => { textureDiscovery?.Cancel(); catalogs.Cancel(); };
            }
            if (ReferenceEquals(textureDiscoveryCreation, creation)) textureDiscoveryCreation = null;
        }
        if (!ownerQuery)
        {
            if (textureExplorer.LoadedRevision != textureDiscovery.Revision)
                await textureExplorer.LoadAsync(textureDiscovery, textureOwner, cache,
                    (source, cancellation) => new NativeTextureDecoder().ReadDetailed(source, cancellation, maximumPreviewDimension: 1024).Document, token);
            else if (!textureExplorer.HasDiscoveryCatalog)
                await textureExplorer.AttachDiscoveryAsync(textureDiscovery, token);
            else if (currentView == "textures" && textureExplorer.IsSuspended)
                textureExplorer.Resume();
            if (token.IsCancellationRequested || !ReferenceEquals(owner, session) || currentView != "textures")
            { await textureExplorer.SuspendAsync(); token.ThrowIfCancellationRequested(); return; }
            await RefreshTextureCatalogsAsync();
        }
    }

    private async Task AttachLinkedTextureCatalogsAsync(AudioExplorerSession owner, long navigation, CancellationToken token)
    {
        try
        {
            await EnsureTextureCatalogsAsync(owner, token, ownerQuery: true);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(owner, session) || navigation != navigationGeneration || currentView != "textures" ||
                textureExplorer.IsSuspended || textureDiscovery is not { } discovery) return;
            await textureExplorer.AttachDiscoveryAsync(discovery, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closingInProgress && navigation == navigationGeneration) NavigationError(error); }
    }

    private async Task<TextureOwnerQueryResult> QueryTexturesByOwnerAsync(
        ResourceIdentity ownerIdentity, long offset, int limit, CancellationToken token)
    {
        var current = session ?? throw new InvalidOperationException("Open an archive first.");
        await EnsureTextureCatalogsAsync(current, token, ownerQuery: true);
        var discovery = textureDiscovery ?? throw new InvalidOperationException("Texture discovery is unavailable.");
        var revision = new SourceRevision(current.Snapshot.IndexContentId, current.Snapshot.PhysicalSourceStamp);
        if (discovery.Revision != revision || ownerIdentity.IndexContentId != revision.IndexContentId)
            throw new ArchiveException("SourceChanged", "The texture owner belongs to another archive revision.");
        return await discovery.QueryByOwnerAsync(ownerIdentity, offset, limit, token);
    }

    private void TextureCatalogsUpdated(object? sender, TextureCatalogSessionUpdatedEventArgs e)
        => ScheduleTextureContextRefresh(sender, textureCatalogs);
    private void TextureDiscoveryUpdated(object? sender, EventArgs e)
        => ScheduleTextureContextRefresh(sender, textureDiscovery);
    private void ScheduleTextureContextRefresh(object? sender, object? expected)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(sender, expected) || closingInProgress) return;
            if (!textureContextRefresh.IsCompleted) { textureContextRefreshPending = true; return; }
            if (!textureCatalogTimer.IsEnabled) textureCatalogTimer.Start();
        });
    }
    private async Task ObserveTextureCatalogsAsync(TextureCatalogSession current)
    {
        try { await current.EnrichmentTask; } catch (OperationCanceledException) { } catch { }
        await Dispatcher.InvokeAsync(async () =>
        { if (ReferenceEquals(current, textureCatalogs) && !closingInProgress) await RefreshTextureCatalogsAsync(); }).Task.Unwrap();
    }
    private Task RefreshTextureCatalogsAsync()
    {
        if (closingInProgress || currentView != "textures" || textureDiscovery is null) return Task.CompletedTask;
        textureContextRefreshPending = true;
        if (!textureContextRefresh.IsCompleted) return textureContextRefresh;
        textureContextRefresh = TrackLibraryTask(DrainTextureContextRefreshAsync());
        return textureContextRefresh;
    }

    private async Task DrainTextureContextRefreshAsync()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(navigationCancellation.Token);
        textureContextRefreshCancellation = cancellation;
        try
        {
            while (textureContextRefreshPending && !closingInProgress)
            {
                textureContextRefreshPending = false;
                cancellation.Token.ThrowIfCancellationRequested();
                await RefreshTextureCatalogsCoreAsync(cancellation.Token);
            }
        }
        finally
        {
            if (ReferenceEquals(textureContextRefreshCancellation, cancellation)) textureContextRefreshCancellation = null;
        }
    }

    private async Task RefreshTextureCatalogsCoreAsync(CancellationToken token)
    {
        var current = textureDiscovery;
        if (currentView != "textures" || current is null || session is null || !ReferenceEquals(current.Snapshot, session.Snapshot)) return;
        var navigation = navigationGeneration;
        var options = await current.ReadFilterOptionsAsync(token);
        if (navigation != navigationGeneration || currentView != "textures" || !ReferenceEquals(current, textureDiscovery)) return;
        var contexts = options.Contexts;
        var available = contexts.Select(context => context.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in textureContextNodes.Keys.Where(key => !available.Contains(key)).ToArray())
        { textureNavigation.Children.Remove(textureContextNodes[key]); textureContextNodes.Remove(key); }
        foreach (var context in contexts)
        {
            if (!textureContextNodes.TryGetValue(context.Key, out var node))
            { node = new(context.Label, "textures") { Group = context.Key }; textureContextNodes.Add(context.Key, node); textureNavigation.Children.Add(node); }
            else node.Label = context.Label;
        }
        if (currentView == "textures") { UpdateExplorerStatus(); RefreshActivity(); }
    }
    private async Task ClearTextureCatalogsAsync()
    {
        await ClearSceneRenderingAsync();
        textureCatalogTimer.Stop();
        textureContextRefreshPending = false;
        textureContextRefreshCancellation?.Cancel();
        var catalogs = textureCatalogs; var discovery = textureDiscovery; var native = textureNative;
        var creation = textureDiscoveryCreation; textureDiscoveryCreation = null;
        textureCatalogs = null;
        if (catalogs is not null) catalogs.Updated -= TextureCatalogsUpdated;
        if (discovery is not null) discovery.Updated -= TextureDiscoveryUpdated;
        textureExplorer.DiscoveryCancel = null;
        discovery?.Cancel(); catalogs?.Cancel();
        textureDependencyPause?.Dispose(); textureDependencyPause = null;
        textureForegroundPause?.Dispose(); textureForegroundPause = null;
        if (creation is not null)
        {
            var pending = await creation;
            pending.Cancel();
            if (!ReferenceEquals(pending, discovery)) await pending.DisposeAsync();
        }
        await textureExplorer.ClearAsync();
        try { await textureContextRefresh; } catch (OperationCanceledException) { }
        textureContextRefresh = Task.CompletedTask;
        try { await textureNavigationDrain; } catch { }
        textureNavigationDrain = Task.CompletedTask;
        textureCatalogs = null; textureDiscovery = null; textureNative = null;
        textureContextNodes.Clear(); textureNavigation.Children.Clear();
        textureNavigation.Children.Add(ddsNavigation);
        if (discovery is not null) await discovery.DisposeAsync();
        if (native is not null) await native.DisposeAsync();
        if (catalogs is not null) await catalogs.DisposeAsync();
    }

    private async Task SelectTextureContextAsync(string key, long navigation, CancellationToken token)
    {
        var current = textureDiscovery ?? throw new InvalidOperationException("Open textures first.");
        if (!current.FilterOptions.Contexts.Any(context => context.Key == key))
        {
            await current.EnrichmentTask.WaitAsync(token);
            if (!current.FilterOptions.Contexts.Any(context => context.Key == key)) throw new InvalidOperationException("The saved owner is unavailable.");
        }
        token.ThrowIfCancellationRequested();
        if (navigation != navigationGeneration || current != textureDiscovery) return;
        var values = new Dictionary<string, string>(textureExplorer.CaptureFilter()) { ["context"] = key, ["kind"] = "1" };
        await textureExplorer.ApplyFilterAsync(values, token);
    }
    private async Task EnsureSavedTextureCatalogAsync(string key, CancellationToken token)
    {
        var catalogs = textureCatalogs ?? throw new InvalidOperationException("Open textures first.");
        var parts = key.Split(':');
        if (parts.Length != 3 || parts[0] != catalogs.Revision.IndexContentId || parts[2].Length != 0 ||
            !long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ordinal) ||
            ordinal < 0 || ordinal >= catalogs.Snapshot.Records.Count || catalogs.Snapshot.Records[(int)ordinal].Type != 3)
            throw new InvalidOperationException("The saved catalog is unavailable.");
        var identity = new ResourceIdentity(catalogs.Revision.IndexContentId, ordinal);
        if (!catalogs.Groups.Any(group => group.CatalogIdentity == identity)) await catalogs.ReadCatalogAsync(identity, token);
        token.ThrowIfCancellationRequested();
        if (!catalogs.Groups.Any(group => group.CatalogIdentity == identity)) throw new InvalidOperationException("The saved catalog is unavailable.");
        await catalogs.RevalidateGroupAsync(identity, token);
    }
}
