using System.Windows;
using System.Windows.Controls;
using Deviant.Assets;
using Deviant.Browser;

namespace Deviant.Desktop;

public partial class CharactersView
{
    private Func<AssetDescriptor, CharacterModelBindingResult, CharacterMeshLodBinding,
        CancellationToken, Task>? poseImportProvider;

    /// <summary>Imports a pose for the exact selected model and detail level.</summary>
    public Func<AssetDescriptor, CharacterModelBindingResult, CharacterMeshLodBinding,
        CancellationToken, Task>? PoseImportProvider
    {
        get => poseImportProvider;
        set
        {
            if (ReferenceEquals(poseImportProvider, value)) return;
            CancelPreview();
            poseImportProvider = value;
            RefreshPoseImportAvailability();
        }
    }

    private bool CanImportPoseSelection => poseImportProvider is not null &&
        SelectedDescriptor is { } descriptor && modelBinding is { Diagnostic: null, OwnerNativeType: 2013 } binding &&
        binding.SelectedIdentity == descriptor.Identity && binding.Revision == descriptor.Revision &&
        binding.Revision == sourceRevision && binding.CatalogContainerResolution.Status == ReferenceStatus.Unique &&
        binding.CatalogContainerResolution.Candidates.Count == 1 &&
        binding.CatalogContainerResolution.Candidates[0] == binding.ContainerIdentity &&
        SelectedModelLod is { Selection: not null, Diagnostic: null, MeshDataIdentity: not null,
            MeshDataResolution.Status: ReferenceStatus.Unique } lod && lod.MeshDataResolution.Candidates.Count == 1 &&
        lod.MeshDataResolution.Candidates[0] == lod.MeshDataIdentity;

    public void RefreshPoseImportAvailability()
    {
        if (LoadPoseButton is null) return;
        var eligible = CanImportPoseSelection;
        LoadPoseButton.Visibility = eligible ? Visibility.Visible : Visibility.Collapsed;
        LoadPoseButton.IsEnabled = eligible && previewCancellation is null;
    }

    private void LoadPoseClicked(object sender, RoutedEventArgs e)
    {
        if (!CanImportPoseSelection || SelectedDescriptor is not { } descriptor ||
            modelBinding is not { } binding || SelectedModelLod is not { } lod || poseImportProvider is not { } provider)
        {
            RefreshPoseImportAvailability();
            return;
        }

        _ = PreviewSelectedWithProviderAsync(CancellationToken.None, (selected, token) =>
        {
            if (!ReferenceEquals(selected, descriptor) || !ReferenceEquals(binding, modelBinding) ||
                !ReferenceEquals(lod, SelectedModelLod) || !ReferenceEquals(provider, poseImportProvider))
                throw new OperationCanceledException("The pose selection changed before import began.", token);
            return provider(descriptor, binding, lod, token);
        });
    }
}
