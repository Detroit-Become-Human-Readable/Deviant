using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class SharedCatalogReferenceHostTests
{
    [Fact]
    public void TextureContextRefreshSharesOnePendingQuery() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        NativeTextureFixture.WriteArchive(fixture.Index);
        OpenTextureCatalogs(window, fixture);
        var discovery = Field<TextureDiscoverySession>(window, "textureDiscovery");
        var sync = Field<object>(discovery, "projectionStoreGate");
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => { lock (sync) { held.Set(); release.Wait(); } });
        Assert.True(held.Wait(TimeSpan.FromSeconds(3)));
        Task? first = null, second = null;
        try
        {
            first = (Task)Invoke(window, "RefreshTextureCatalogsAsync")!;
            second = (Task)Invoke(window, "RefreshTextureCatalogsAsync")!;
            Assert.Same(first, second);
        }
        finally
        {
            release.Set(); Pump(holder);
            if (first is not null) Pump(first);
            if (second is not null) Pump(second);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceneDependenciesDeferTextureDiscoveryAndTextureConsumersReuseNativeSession(bool ownerQuery) => WorkspaceCommitTests.Run((window, fixture) =>
    {
        NativeTextureFixture.WriteArchive(fixture.Index);
        Pump(window.OpenArchiveAsync(fixture.Index));
        Pump((Task)Invoke(window, "EnsureSceneRendererAsync", CancellationToken.None)!);

        var catalogs = Field<TextureCatalogSession>(window, "textureCatalogs");
        var native = Field<NativeTextureSession>(window, "textureNative");
        var renderer = Field<SceneRenderSession>(window, "sceneRenderer");
        Assert.Null(Field<TextureDiscoverySession?>(window, "textureDiscovery"));
        Assert.NotNull(Field<IDisposable?>(window, "textureDependencyPause"));
        Assert.Same(native, window.ModelSession!.Textures);

        Pump((Task)Invoke(window, "EnsureTextureCatalogsAsync", window.CurrentSession!, CancellationToken.None, ownerQuery)!);

        var discovery = Assert.IsType<TextureDiscoverySession>(Field<TextureDiscoverySession?>(window, "textureDiscovery"));
        Assert.Same(catalogs, Field<TextureCatalogSession>(window, "textureCatalogs"));
        Assert.Same(native, Field<NativeTextureSession>(window, "textureNative"));
        Assert.Same(renderer, Field<SceneRenderSession>(window, "sceneRenderer"));
        Assert.Null(Field<IDisposable?>(window, "textureDependencyPause"));

        Pump(window.ShowTexturesAsync());
        Assert.Same(discovery, Field<TextureDiscoverySession>(window, "textureDiscovery"));
        Assert.Same(native, window.TextureBrowser.NativeSession);
    });

    [Fact]
    public void ReplacingSceneDependencySourceReleasesItsTexturePause() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        Pump(window.OpenArchiveAsync(fixture.Index));
        Pump((Task)Invoke(window, "EnsureSceneRendererAsync", CancellationToken.None)!);
        var catalogs = Field<TextureCatalogSession>(window, "textureCatalogs");
        Assert.NotNull(Field<IDisposable?>(window, "textureDependencyPause"));

        var replacement = Path.Combine(fixture.Root, "replacement", "archive.idx");
        DiscoveryFixture.WriteIndex(replacement, [(999u, new byte[] { 7, 8, 9 })]);
        Pump(window.OpenArchiveAsync(replacement));

        Assert.Null(Field<IDisposable?>(window, "textureDependencyPause"));
        Assert.Null(Field<TextureCatalogSession?>(window, "textureCatalogs"));
        Assert.Null(Field<NativeTextureSession?>(window, "textureNative"));
        Assert.Throws<ObjectDisposedException>(() => catalogs.PauseBackgroundDiscovery());
    });

    [Fact]
    public void TextureCatalogSessionReusesHostProviderBudgetAndSharedFeatureLease() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        var owner = OpenTextureCatalogs(window, fixture);
        var provider = Assert.IsType<CatalogReferenceProvider>(window.SharedCatalogReferences);
        var catalogs = Field<TextureCatalogSession>(window, "textureCatalogs");
        var lease = Field<SharedFeatureStoreLease?>(window, "catalogFeatureStoreLease");
        Pump(window.ShowTexturesAsync());

        Assert.Same(provider.Snapshot, owner.Snapshot);
        Assert.Same(provider, window.SharedCatalogReferences);
        Assert.Same(catalogs, Field<TextureCatalogSession>(window, "textureCatalogs"));
        Assert.Same(provider, catalogs.ReferenceProvider);
        Assert.Same(window.RenderMemoryBudget, provider.SharedMemoryBudget);
        Assert.NotNull(lease);
        Assert.Same(lease!.Store, Field<object?>(provider, "featureStore"));
        Assert.Equal(1, SharedLeaseCount(owner));
    });

    [Fact]
    public void SourceReplacementDisposesOldSharedProviderAndCreatesOneForTheNewSnapshot() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        var oldOwner = OpenTextureCatalogs(window, fixture);
        var oldSnapshot = oldOwner.Snapshot;
        var oldProvider = Assert.IsType<CatalogReferenceProvider>(window.SharedCatalogReferences);
        var oldLease = Field<SharedFeatureStoreLease?>(window, "catalogFeatureStoreLease")!;

        var replacement = Path.Combine(fixture.Root, "replacement", "archive.idx");
        DiscoveryFixture.WriteIndex(replacement, [(999u, new byte[] { 7, 8, 9 })]);
        Pump(window.OpenArchiveAsync(replacement));
        Assert.Null(window.SharedCatalogReferences);
        Assert.Null(Field<SharedFeatureStoreLease?>(window, "catalogFeatureStoreLease"));
        Assert.Equal(0, SharedLeaseCount(oldOwner));
        Assert.Throws<ObjectDisposedException>(() => _ = oldLease.Store);
        Pump(Assert.ThrowsAsync<ObjectDisposedException>(() => oldProvider.ValidateSnapshotAsync()));

        var newOwner = window.CurrentSession ?? throw new InvalidOperationException("The replacement archive did not open.");
        Pump(window.ShowTexturesAsync());
        var newProvider = Assert.IsType<CatalogReferenceProvider>(window.SharedCatalogReferences);
        var newCatalogs = Field<TextureCatalogSession>(window, "textureCatalogs");
        Pump(newCatalogs.EnrichmentTask);
        Assert.NotSame(oldSnapshot, newOwner.Snapshot);
        Assert.Equal(Path.GetFullPath(replacement), newOwner.Snapshot.IndexPath);
        Assert.NotSame(oldProvider, newProvider);
        Assert.Same(newProvider, newCatalogs.ReferenceProvider);
        Assert.Same(newOwner.Snapshot, newProvider.Snapshot);
        Assert.Equal(1, SharedLeaseCount(newOwner));
    });

    [Theory]
    [InlineData("cache")]
    [InlineData("close")]
    public void CacheAndWindowCloseDrainTextureConsumersBeforeReleasingSharedStoreLease(string drain) =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            var owner = OpenTextureCatalogs(window, fixture);
            var provider = Assert.IsType<CatalogReferenceProvider>(window.SharedCatalogReferences);
            var lease = Field<SharedFeatureStoreLease?>(window, "catalogFeatureStoreLease")!;

            if (drain == "cache")
            {
                Pump((Task)Invoke(window, "DrainCacheUsersAsync", CancellationToken.None)!);
            }
            else
            {
                window.Close();
                Pump(WindowTestLifetime.WaitClosedAsync(window));
            }

            Assert.Null(window.CurrentSession);
            Assert.Null(window.SharedCatalogReferences);
            Assert.Null(Field<SharedFeatureStoreLease?>(window, "catalogFeatureStoreLease"));
            Assert.Null(Field<TextureCatalogSession?>(window, "textureCatalogs"));
            Assert.Equal(0, SharedLeaseCount(owner));
            Assert.Throws<ObjectDisposedException>(() => _ = lease.Store);
            Pump(Assert.ThrowsAsync<ObjectDisposedException>(() => provider.ValidateSnapshotAsync()));
        });

    private static AudioExplorerSession OpenTextureCatalogs(AudioWindow window, DiscoveryFixture fixture,
        string? indexPath = null)
    {
        Pump(window.OpenArchiveAsync(indexPath ?? fixture.Index));
        var owner = window.CurrentSession ?? throw new InvalidOperationException("The generated archive did not open.");
        Pump(window.ShowTexturesAsync());
        var catalogs = Field<TextureCatalogSession>(window, "textureCatalogs");
        Pump(catalogs.EnrichmentTask);
        return owner;
    }

    private static int SharedLeaseCount(AudioExplorerSession session) =>
        Field<int>(session, "sharedFeatureLeaseCount");

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
