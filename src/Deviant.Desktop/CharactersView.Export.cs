using System.Windows;
using System.Windows.Controls;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;

namespace Deviant.Desktop;

public partial class CharactersView
{
    public void SetExportDetails(string details)
    {
        if (!string.IsNullOrWhiteSpace(details)) Advanced.AppendText("\nExport notices:\n" + details);
    }
    private readonly HashSet<Task> exportTasks = [];
    private Func<AssetDescriptor, CharacterModelBindingResult, CharacterMeshLodBinding,
        CancellationToken, Task>? exportProvider;
    private CancellationTokenSource? exportCancellation;
    private long exportGeneration;
    private int exportDrainDepth;

    /// <summary>Exports only the currently selected, uniquely resolved model detail level.</summary>
    public Func<AssetDescriptor, CharacterModelBindingResult, CharacterMeshLodBinding,
        CancellationToken, Task>? ExportProvider
    {
        get => exportProvider;
        set
        {
            if (ReferenceEquals(exportProvider, value)) return;
            CancelExport();
            exportProvider = value;
            RefreshExportAvailability();
        }
    }

    public void RefreshExportAvailability()
    {
        if (ExportFbxButton is null) return;
        var eligible = exportProvider is not null && SelectedDescriptor is { } descriptor &&
            modelBinding is { Diagnostic: null } binding && binding.SelectedIdentity == descriptor.Identity &&
            binding.Revision == descriptor.Revision && binding.Revision == sourceRevision &&
            binding.CatalogContainerResolution.Status == ReferenceStatus.Unique &&
            SelectedModelLod is { Selection: not null, Diagnostic: null,
                MeshDataIdentity: not null, MeshDataResolution.Status: ReferenceStatus.Unique };
        ExportFbxButton.Visibility = eligible ? Visibility.Visible : Visibility.Collapsed;
        ExportFbxButton.IsEnabled = eligible && exportCancellation is null && Volatile.Read(ref exportDrainDepth) == 0;
        if (CancelExportButton is not null)
        {
            CancelExportButton.Visibility = exportCancellation is null ? Visibility.Collapsed : Visibility.Visible;
            CancelExportButton.IsEnabled = exportCancellation is { IsCancellationRequested: false };
        }
    }

    /// <summary>Cancels the current request and clears its status without affecting a later selection.</summary>
    public void CancelExport()
    {
        ++exportGeneration;
        var cancellation = exportCancellation;
        exportCancellation = null;
        cancellation?.Cancel();
        if (ExportStatus is not null) ExportStatus.Text = "";
        RefreshExportAvailability();
    }

    public async Task DrainExportAsync()
    {
        Interlocked.Increment(ref exportDrainDepth);
        try
        {
            CancelExport();
            while (exportTasks.Count > 0)
            {
                var pending = exportTasks.ToArray();
                await Task.WhenAll(pending);
                foreach (var task in pending) exportTasks.Remove(task);
            }
        }
        finally { Interlocked.Decrement(ref exportDrainDepth); RefreshExportAvailability(); }
    }

    private void ExportFbxClicked(object sender, RoutedEventArgs e) => StartExport();

    private void CancelExportClicked(object sender, RoutedEventArgs e)
    {
        if (exportCancellation is not { IsCancellationRequested: false } request) return;
        request.Cancel();
        if (ExportStatus is not null) ExportStatus.Text = "Cancelling…";
        RefreshExportAvailability();
    }

    private void StartExport()
    {
        if (Volatile.Read(ref exportDrainDepth) != 0 || exportCancellation is not null) return;
        if (!CanExportSelection(out var descriptor, out var binding, out var lod) || exportProvider is not { } provider)
        {
            RefreshExportAvailability();
            return;
        }

        CancelExport();
        var version = ++exportGeneration;
        var request = new CancellationTokenSource();
        exportCancellation = request;
        if (ExportStatus is not null) ExportStatus.Text = "Exporting…";
        RefreshExportAvailability();
        var task = ExportSelectionAsync(version, descriptor, binding, lod, provider, request);
        exportTasks.Add(task);
        _ = ObserveExportTaskAsync(task);
    }

    private async Task ObserveExportTaskAsync(Task task)
    {
        try { await task; }
        finally { exportTasks.Remove(task); }
    }

    private async Task ExportSelectionAsync(long version, AssetDescriptor descriptor,
        CharacterModelBindingResult binding, CharacterMeshLodBinding lod,
        Func<AssetDescriptor, CharacterModelBindingResult, CharacterMeshLodBinding, CancellationToken, Task> provider,
        CancellationTokenSource request)
    {
        try
        {
            await provider(descriptor, binding, lod, request.Token);
            if (IsCurrentExport(version, descriptor, binding, lod, provider, request, includeCancellation: true) && ExportStatus is not null)
                ExportStatus.Text = request.IsCancellationRequested ? "Export cancelled." : "Export complete.";
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentExport(version, descriptor, binding, lod, provider, request, includeCancellation: true) && ExportStatus is not null)
                ExportStatus.Text = "Export cancelled.";
        }
        catch (Exception error)
        {
            WorkflowDiagnostics.Failure("Characters.Export", error, descriptor.Identity.Key);
            if (IsCurrentExport(version, descriptor, binding, lod, provider, request, includeCancellation: true))
            {
                if (ExportStatus is not null)
                    ExportStatus.Text = error is ArchiveException { Code: "AssetMemoryLimit" }
                        ? "This export exceeds the memory limit."
                        : "Export failed.";
                if (Advanced is not null) Advanced.AppendText("\nExport details: " + error.Message);
            }
        }
        finally
        {
            var stillCurrent = ReferenceEquals(exportCancellation, request);
            if (stillCurrent) exportCancellation = null;
            request.Dispose();
            if (stillCurrent) RefreshExportAvailability();
        }
    }

    private bool IsCurrentExport(long version, AssetDescriptor descriptor, CharacterModelBindingResult binding,
        CharacterMeshLodBinding lod, Func<AssetDescriptor, CharacterModelBindingResult, CharacterMeshLodBinding, CancellationToken, Task> provider,
        CancellationTokenSource request, bool includeCancellation = false) => version == exportGeneration &&
        (includeCancellation || !request.IsCancellationRequested) &&
        ReferenceEquals(descriptor, SelectedDescriptor) && ReferenceEquals(binding, modelBinding) &&
        ReferenceEquals(lod, SelectedModelLod) && ReferenceEquals(provider, exportProvider) &&
        descriptor.Revision == sourceRevision;

    private bool CanExportSelection(out AssetDescriptor descriptor, out CharacterModelBindingResult binding,
        out CharacterMeshLodBinding lod)
    {
        descriptor = null!; binding = null!; lod = null!;
        if (SelectedDescriptor is not { } selected || modelBinding is not { Diagnostic: null } selectedBinding ||
            SelectedModelLod is not { Selection: not null, Diagnostic: null,
                MeshDataIdentity: not null, MeshDataResolution.Status: ReferenceStatus.Unique } selectedLod ||
            selectedBinding.SelectedIdentity != selected.Identity || selectedBinding.Revision != selected.Revision ||
            selectedBinding.Revision != sourceRevision || selectedBinding.CatalogContainerResolution.Status != ReferenceStatus.Unique)
            return false;
        descriptor = selected;
        binding = selectedBinding;
        lod = selectedLod;
        return true;
    }
}
