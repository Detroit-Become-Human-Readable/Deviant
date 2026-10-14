using System.IO;
using Microsoft.Win32;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Export.Models;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private NativeModelPosePackageLease? characterPoseOwner;
    private AssetDescriptor? characterPoseDescriptor;
    private CharacterModelBindingResult? characterPoseBinding;
    private CharacterMeshLodBinding? characterPoseDetail;
    internal Func<string?>? CharacterPosePathPicker { get; set; }
    internal Func<string?>? CharacterFbxPathPicker { get; set; }

    private void ClearCharacterPose()
    {
        if (charactersView.ExportProvider == ExportProductionCharacterFbxAsync)
            charactersView.ExportProvider = productionCharacters is not null && CharacterModelProvider == ReadProductionCharacterModelAsync
                ? ExportProductionCharacterFbxAsync : null;
        characterPoseOwner?.Dispose(); characterPoseOwner = null;
        characterPoseDescriptor = null; characterPoseBinding = null; characterPoseDetail = null;
    }

    private bool HasCurrentCharacterPose(AssetDescriptor selected, CharacterModelBindingResult binding,
        CharacterMeshLodBinding detail) => characterPoseOwner is not null &&
        ReferenceEquals(selected, characterPoseDescriptor) && ReferenceEquals(binding, characterPoseBinding) &&
        ReferenceEquals(detail, characterPoseDetail);

    private async Task ImportProductionCharacterPoseAsync(AssetDescriptor selected,
        CharacterModelBindingResult binding, CharacterMeshLodBinding detail, CancellationToken token)
    {
        var owner = session ?? throw new InvalidOperationException("Open a workspace first.");
        var navigation = navigationGeneration;
        VerifyCharacterPoseSelection(owner, navigation, selected, binding, detail, token);
        var path = PickCharacterPosePath();
        if (path is null) throw new OperationCanceledException(token);
        VerifyCharacterPoseSelection(owner, navigation, selected, binding, detail, token);
        var budget = renderMemoryBudget ?? throw new InvalidOperationException("The asset session is unavailable.");
        NativeModelPosePackageLease? candidate = await NativeModelPosePackageReader.ReadAsync(path, budget, token);
        try
        {
            using (var consumer = candidate.AcquireLease()) ValidateCharacterPose(consumer.Package, selected, binding, detail);
            await LoadCharacterModelWithProviderAsync(selected, token, async (_, sharedBudget, cancellation) =>
            {
                using var pose = candidate!.AcquireLease();
                return await ReadPosedCharacterModelAsync(owner, selected, binding, detail, pose, sharedBudget, cancellation);
            });
            VerifyCharacterPoseSelection(owner, navigation, selected, binding, detail, token);
            ClearCharacterPose();
            characterPoseOwner = candidate; candidate = null;
            characterPoseDescriptor = selected; characterPoseBinding = binding; characterPoseDetail = detail;
            charactersView.ExportProvider = ExportProductionCharacterFbxAsync;
        }
        finally { candidate?.Dispose(); }
    }

    private async Task<RenderAssetSnapshot> ReadPosedCharacterModelAsync(AudioExplorerSession owner,
        AssetDescriptor selected, CharacterModelBindingResult binding, CharacterMeshLodBinding detail,
        NativeModelPosePackageLease.Consumer pose, AssetMemoryBudget budget, CancellationToken token)
    {
        ValidateCharacterPose(pose.Package, selected, binding, detail);
        await EnsureSceneRendererAsync(token);
        var models = nativeModels ?? throw new InvalidOperationException("The model loader is unavailable.");
        return await WithCharacterContainerAsync(owner, binding, budget, async (container, cancellation) =>
        {
            using var rig = await AcquireCharacterRigAsync(container, binding, cancellation);
            return await models.LoadPosedSnapshotAsync(container, binding.CatalogIdentity, detail.Selection!,
                detail.LodOrdinal, new(pose, rig), token: cancellation);
        }, token);
    }

    private async Task ExportProductionCharacterFbxAsync(AssetDescriptor selected,
        CharacterModelBindingResult binding, CharacterMeshLodBinding detail, CancellationToken token)
    {
        var owner = session ?? throw new InvalidOperationException("Open a workspace first.");
        var navigation = navigationGeneration;
        VerifyCharacterPoseSelection(owner, navigation, selected, binding, detail, token);
        if (!HasCurrentCharacterPose(selected, binding, detail))
        {
            await ExportProductionCharacterBindFbxAsync(owner, navigation, selected, binding, detail, token);
            return;
        }
        using var pose = characterPoseOwner!.AcquireLease();
        var snapshot = modelView.CurrentSnapshot ?? throw new InvalidOperationException("Preview the model first.");
        using var snapshotRetention = snapshot.AcquireLease();
        var meshes = snapshot.Meshes.Values.Where(mesh => mesh.Identity == detail.MeshDataIdentity &&
            mesh.GroupIndex == detail.GroupIndex).ToArray();
        if (meshes.Length != 1) throw new InvalidOperationException("The displayed model does not match this selection.");
        var mesh = meshes[0];
        var poseSource = pose.Package.PoseSource;
        if (mesh.Pose != new RenderPoseIdentity(poseSource.RigIdentity, poseSource.SnapshotSequence,
                poseSource.MatrixRowsSha256, poseSource.CaptureEvidenceSha256, pose.Package.SerializedLodOrdinal))
            throw new InvalidOperationException("The displayed pose changed. Preview the model again.");
        var path = PickCharacterFbxPath();
        if (path is null) throw new OperationCanceledException(token);
        VerifyCharacterPoseSelection(owner, navigation, selected, binding, detail, token);
        var models = nativeModels ?? throw new InvalidOperationException("The model loader is unavailable.");
        var options = new FbxGeometryExportOptions(Overwrite: true, MediaPackaging: FbxMediaPackaging.Embedded);
        var result = await WithCharacterContainerAsync(owner, binding, snapshot.MemoryBudget, async (container, cancellation) =>
        {
            using var rig = await AcquireCharacterRigAsync(container, binding, cancellation);
            var inputs = new NativeModelPoseInputs(pose, rig);
            var selection = new NativeModelFbxExportSelection(mesh.Key, mesh.Identity, mesh.GroupIndex, detail.LodOrdinal,
                new(mesh.Identity, mesh.Key, detail.LodOrdinal, mesh.GroupIndex));
            return await new NativeModelFbxExportCoordinator().ExportAsync(snapshot, selected.Revision, selection,
                (sourceMesh, sharedBudget, ct) => models.CreateFbxMaterialInputsAsync(container, binding.CatalogIdentity,
                    sourceMesh, snapshot.Materials, ct, options, bakeCache: new MaterialBakeCache(cache)),
                ct => { ct.ThrowIfCancellationRequested(); container.EnsureUnchanged(); pose.EnsureUnchanged(); rig.EnsureUnchanged(); return Task.CompletedTask; },
                owner.Snapshot.SourceRoot, path, options, cancellation,
                (sourceMesh, sharedBudget, ct) => Task.FromResult(NativeModelPoseFbxInputs.Create(container, sourceMesh, inputs, sharedBudget, ct)));
        }, token);
        VerifyCharacterPoseSelection(owner, navigation, selected, binding, detail, token);
        if (result.Losses.Count > 0) charactersView.SetStatus($"Exported with {result.Losses.Count:N0} notices. See Advanced.");
        charactersView.SetExportDetails(string.Join(Environment.NewLine, result.Losses.Select(loss => loss.Detail)));
    }

    private string? PickCharacterPosePath()
    {
        if (CharacterPosePathPicker is { } picker) return picker();
        var dialog = new OpenFileDialog { Title = "Load pose", Filter = "Pose files (*.json)|*.json", CheckFileExists = true };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private string? PickCharacterFbxPath()
    {
        if (CharacterFbxPathPicker is { } picker) return picker();
        var label = charactersView.SelectedDescriptor?.Label ?? "Model";
        var invalid = Path.GetInvalidFileNameChars();
        var fileName = string.Concat(label.Select(character => invalid.Contains(character) ? '_' : character)).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "Model";
        var dialog = new SaveFileDialog { Title = "Export FBX", Filter = "FBX (*.fbx)|*.fbx", AddExtension = true,
            FileName = fileName + ".fbx", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            OverwritePrompt = true };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void VerifyCharacterPoseSelection(AudioExplorerSession owner, long navigation, AssetDescriptor selected,
        CharacterModelBindingResult binding, CharacterMeshLodBinding detail, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsWorkspaceValidated || closingInProgress || workspaceTransitionDepth != 0 ||
            !ReferenceEquals(owner, session) || navigation != navigationGeneration || currentView != "characters" ||
            !ReferenceEquals(selected, charactersView.SelectedDescriptor) || !ReferenceEquals(binding, charactersView.ModelBinding) ||
            !ReferenceEquals(detail, charactersView.SelectedModelLod)) throw new OperationCanceledException(token);
        if (selected.Revision != binding.Revision || selected.Identity != binding.SelectedIdentity)
            throw new InvalidDataException("The model source changed.");
    }

    private static void ValidateCharacterPose(NativeModelPosePackage package, AssetDescriptor selected,
        CharacterModelBindingResult binding, CharacterMeshLodBinding detail)
    {
        if (package.PoseSource.Revision != selected.Revision || package.PoseSource.RigIdentity != binding.SelectedIdentity ||
            package.PoseSource.MeshIdentity != detail.MeshDataIdentity || package.PoseSource.GroupIndex != detail.GroupIndex ||
            package.SerializedLodOrdinal != detail.LodOrdinal || detail.Selection is null)
            throw new InvalidDataException("The pose belongs to another model or detail level.");
    }
}
