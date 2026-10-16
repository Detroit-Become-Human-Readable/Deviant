using System.Diagnostics;
using Deviant.Browser;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private readonly List<Exception> textureBrowseFailures = [];

    private void RecordTextureBrowseFailure(Exception error)
    {
        if (textureBrowseFailures.Count == 64) textureBrowseFailures.RemoveAt(0);
        textureBrowseFailures.Add(error);
        Trace.TraceError("Texture browsing: {0}", error);
    }

    private async Task DrainTextureBrowseWorkAsync()
    {
        var refresh = textureContextRefresh;
        var navigation = textureNavigationDrain;
        textureContextRefresh = textureNavigationDrain = Task.CompletedTask;
        foreach (var operation in new[] { refresh, navigation })
        {
            try { await operation; }
            catch (OperationCanceledException) { }
            catch (Exception error) { RecordTextureBrowseFailure(error); }
        }
    }

    private async Task ClearTextureCatalogOwnersAsync()
    {
        Dispatcher.VerifyAccess();
        textureCatalogTimer.Stop();
        textureContextRefreshPending = false;
        var catalogs = textureCatalogs;
        var discovery = textureDiscovery;
        var native = textureNative;
        var creation = textureDiscoveryCreation;
        var dependencyPause = textureDependencyPause;
        var foregroundPause = textureForegroundPause;
        textureCatalogs = null; textureDiscovery = null; textureNative = null;
        textureDiscoveryCreation = null; textureDependencyPause = textureForegroundPause = null;
        if (catalogs is not null) catalogs.Updated -= TextureCatalogsUpdated;
        if (discovery is not null) discovery.Updated -= TextureDiscoveryUpdated;
        textureExplorer.DiscoveryCancel = null;
        var failures = new List<Exception>();

        Attempt(() => textureContextRefreshCancellation?.Cancel());
        Attempt(() => discovery?.Cancel());
        Attempt(() => catalogs?.Cancel());
        Attempt(() => dependencyPause?.Dispose());
        Attempt(() => foregroundPause?.Dispose());
        await AttemptAsync(ClearSceneRenderingAsync);
        if (creation is not null)
        {
            TextureDiscoverySession? pending = null;
            try { pending = await creation; }
            catch (OperationCanceledException) { }
            catch (Exception error) { RecordTextureBrowseFailure(error); }
            if (pending is not null)
            {
                Attempt(pending.Cancel);
                if (!ReferenceEquals(pending, discovery)) await AttemptAsync(() => pending.DisposeAsync().AsTask());
            }
        }
        await AttemptAsync(textureExplorer.ClearAsync);
        await DrainTextureBrowseWorkAsync();
        textureContextNodes.Clear(); textureNavigation.Children.Clear();
        textureNavigation.Children.Add(ddsNavigation);
        if (discovery is not null) await AttemptAsync(() => discovery.DisposeAsync().AsTask());
        if (native is not null) await AttemptAsync(() => native.DisposeAsync().AsTask());
        if (catalogs is not null) await AttemptAsync(() => catalogs.DisposeAsync().AsTask());
        if (failures.Count > 0) throw new AggregateException("Texture cleanup failed.", failures);

        void Attempt(Action action)
        {
            try { action(); }
            catch (Exception error) { failures.Add(error); RecordTextureBrowseFailure(error); }
        }
        async Task AttemptAsync(Func<Task> action)
        {
            try { await action(); }
            catch (Exception error) { failures.Add(error); RecordTextureBrowseFailure(error); }
        }
    }
}
