using Deviant.Assets;
using Deviant.Browser;
using Deviant.Export.Models;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private async Task<FbxGeometryExportReport> ExportSceneFbxAsync(RenderAssetSnapshot source, SceneFbxExportRequest request,
        string destination, IProgress<RenderLoadProgress>? progress, CancellationToken token)
    {
        using var foreground = BeginRenderForeground();
        await EnsureWorkspacePreparation().DrainAsync(token);
        var owner = session ?? throw new InvalidOperationException("Open an archive first.");
        var revision = new SourceRevision(owner.Snapshot.IndexContentId, owner.Snapshot.PhysicalSourceStamp);
        if (source.Revision != revision) throw new InvalidOperationException("Reload the scene before exporting.");
        var models = nativeModels ?? throw new InvalidOperationException("The model loader is unavailable.");
        using var pins = await new SourceExtentVerifier().AcquireAsync(owner.Snapshot, source.SourceExtents, token,
            SourceExtentVerifier.MaximumBytes, SourceExtentVerifier.MaximumLargeExtentCount);
        var containers = new Dictionary<ResourceIdentity, NativeContainerLease>();
        var bakeCache = new MaterialBakeCache(cache);
        var preparationCache = new NativeModelExportPreparationCache(cache);
        var options = SceneFbxExportCoordinator.CreateOptions(overwrite: true);
        try
        {
            return await new SceneFbxExportCoordinator().ExportAsync(source, request, owner.Snapshot.SourceRoot,
                destination, async (mesh, ct) =>
                {
                    var catalog = mesh.CatalogIdentity ?? throw new InvalidOperationException("The material catalog is unavailable.");
                    var identity = mesh.Identity with { MemberPath = null };
                    if (!containers.TryGetValue(identity, out var container))
                    {
                        container = await NativeContainerLease.OpenMetadataAsync(owner.Snapshot, identity, source.MemoryBudget, ct, allowLargeData: true);
                        containers.Add(identity, container);
                    }
                    return await models.CreateFbxMaterialInputsAsync(container, catalog, mesh, source.Materials, ct,
                        options, bakeCache: bakeCache, preparationCache: preparationCache);
                }, ct =>
                {
                    ct.ThrowIfCancellationRequested(); pins.EnsureUnchanged();
                    if (!ReferenceEquals(owner, session)) throw new OperationCanceledException(ct);
                    foreach (var container in containers.Values) container.EnsureUnchanged();
                    return Task.CompletedTask;
                }, progress, options, token);
        }
        finally { foreach (var container in containers.Values) container.Dispose(); }
    }
}
