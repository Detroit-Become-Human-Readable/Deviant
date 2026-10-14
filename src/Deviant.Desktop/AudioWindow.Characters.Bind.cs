using Deviant.Assets;
using Deviant.Browser;
using Deviant.Export.Models;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private async Task ExportProductionCharacterBindFbxAsync(AudioExplorerSession owner, long navigation,
        AssetDescriptor selected, CharacterModelBindingResult binding, CharacterMeshLodBinding detail, CancellationToken token)
    {
        if (binding.Rig is not { MatrixCount: > 0 })
        { await ExportProductionStaticModelFbxAsync(owner, navigation, selected, binding, detail, token); return; }
        VerifyCharacterPoseSelection(owner, navigation, selected, binding, detail, token);
        var snapshot = modelView.CurrentSnapshot;
        if (snapshot is null || !snapshot.Meshes.Values.Any(mesh => mesh.Identity == detail.MeshDataIdentity &&
                mesh.GroupIndex == detail.GroupIndex && mesh.Pose is null && mesh.SkinAttributes is not null))
        {
            await LoadCharacterModelAsync(selected, token);
            VerifyCharacterPoseSelection(owner, navigation, selected, binding, detail, token);
            snapshot = modelView.CurrentSnapshot ?? throw new InvalidOperationException("The model preview is unavailable.");
        }
        using var snapshotRetention = snapshot.AcquireLease();
        var meshes = snapshot.Meshes.Values.Where(mesh => mesh.Identity == detail.MeshDataIdentity &&
            mesh.GroupIndex == detail.GroupIndex && mesh.Pose is null).ToArray();
        if (meshes.Length != 1 || meshes[0].SkinAttributes is null)
            throw new InvalidOperationException("The displayed model has no supported skin data.");
        var mesh = meshes[0];
        var models = nativeModels ?? throw new InvalidOperationException("The model loader is unavailable.");
        var options = new FbxGeometryExportOptions(Overwrite: true, MediaPackaging: FbxMediaPackaging.Embedded);
        var result = await WithCharacterContainerAsync(owner, binding, snapshot.MemoryBudget, async (container, cancellation) =>
        {
            using var rig = await AcquireCharacterRigAsync(container, binding, cancellation);
            using var source = NativeModelBindFbxInputs.Create(container, mesh, rig, snapshot.MemoryBudget, cancellation);
            var path = PickCharacterFbxPath();
            if (path is null) throw new OperationCanceledException(cancellation);
            VerifyCharacterPoseSelection(owner, navigation, selected, binding, detail, cancellation);
            var selection = new NativeModelFbxExportSelection(mesh.Key, mesh.Identity, mesh.GroupIndex, detail.LodOrdinal,
                new(mesh.Identity, mesh.Key, detail.LodOrdinal, mesh.GroupIndex));
            return await new NativeModelFbxExportCoordinator().ExportAsync(snapshot, selected.Revision, selection,
                (sourceMesh, sharedBudget, ct) => models.CreateFbxMaterialInputsAsync(container, binding.CatalogIdentity,
                    sourceMesh, snapshot.Materials, ct, options, bakeCache: new MaterialBakeCache(cache)),
                ct => { ct.ThrowIfCancellationRequested(); container.EnsureUnchanged(); rig.EnsureUnchanged(); return Task.CompletedTask; },
                owner.Snapshot.SourceRoot, path, options, cancellation,
                (sourceMesh, sharedBudget, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(source);
                });
        }, token);
        VerifyCharacterPoseSelection(owner, navigation, selected, binding, detail, token);
        if (result.Losses.Count > 0) charactersView.SetStatus($"Exported with {result.Losses.Count:N0} notices. See Advanced.");
        charactersView.SetExportDetails(string.Join(Environment.NewLine, result.Losses.Select(loss => loss.Detail)));
    }

    private async Task ExportProductionStaticModelFbxAsync(AudioExplorerSession owner, long navigation,
        AssetDescriptor selected, CharacterModelBindingResult binding, CharacterMeshLodBinding detail, CancellationToken token)
    {
        VerifyCharacterPoseSelection(owner, navigation, selected, binding, detail, token);
        await LoadCharacterModelAsync(selected, token);
        VerifyCharacterPoseSelection(owner, navigation, selected, binding, detail, token);
        var snapshot = modelView.CurrentSnapshot ?? throw new InvalidOperationException("The model preview is unavailable.");
        using var retained = snapshot.AcquireLease();
        if (snapshot.Meshes.Values.Any(mesh => mesh.SkinAttributes is not null))
            throw new InvalidOperationException("The model contains skin data, but its rig is unavailable.");
        var path = PickCharacterFbxPath();
        if (path is null) throw new OperationCanceledException(token);
        var models = nativeModels ?? throw new InvalidOperationException("The model loader is unavailable.");
        var options = new FbxGeometryExportOptions(Overwrite: true, MediaPackaging: FbxMediaPackaging.Embedded);
        var report = await WithCharacterContainerAsync(owner, binding, snapshot.MemoryBudget, async (container, cancellation) =>
            await new SceneFbxExportCoordinator().ExportAsync(snapshot, new(snapshot.Occurrences.Select(row => new RenderMeshSelection(row.Key)).ToArray()),
                owner.Snapshot.SourceRoot, path,
                (mesh, ct) => models.CreateFbxMaterialInputsAsync(container, binding.CatalogIdentity, mesh, snapshot.Materials, ct, options,
                    bakeCache: new MaterialBakeCache(cache)),
                ct => { ct.ThrowIfCancellationRequested(); container.EnsureUnchanged(); return Task.CompletedTask; }, options: options, token: cancellation), token);
        VerifyCharacterPoseSelection(owner, navigation, selected, binding, detail, token);
        charactersView.SetExportDetails(string.Join(Environment.NewLine, report.Losses.Select(loss => loss.Detail)));
    }
}
