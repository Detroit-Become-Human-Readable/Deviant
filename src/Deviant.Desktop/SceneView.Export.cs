using System.Numerics;
using System.Windows;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Export.Models;
using Microsoft.Win32;

namespace Deviant.Desktop;

public partial class SceneView
{
    private CancellationTokenSource? exportCancellation;
    private Task exportTask = Task.CompletedTask;
    private bool exportActive;
    public Func<RenderAssetSnapshot, SceneFbxExportRequest, string, IProgress<RenderLoadProgress>?, CancellationToken,
        Task<FbxGeometryExportReport>>? GeometryExportProvider { get; set; }

    public async Task<FbxGeometryExportReport> ExportGeometryAsync(string path, bool entireLoadedScene = false,
        bool allRegions = false, bool merge = false, SceneExportPlacement placement = SceneExportPlacement.Scene,
        bool includeHidden = false, CancellationToken token = default)
    {
        if (!exportTask.IsCompleted) throw new InvalidOperationException("An export is already running.");
        var provider = GeometryExportProvider ?? throw new InvalidOperationException("FBX export is unavailable.");
        var current = renderSnapshot ?? throw new InvalidOperationException("Load a scene first.");
        using var retained = current.AcquireLease();
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        exportCancellation = own; exportActive = true;
        var selected = SceneViewport.SelectedMeshes.ToArray();
        using var progress = new CoalescedDispatcherProgress<RenderLoadProgress>(Dispatcher, p =>
        {
            if (own.IsCancellationRequested) return;
            ActivityProgress = new(true, p.Stage, p.Completed, p.Total);
            ActivityProgressChanged?.Invoke(this, ActivityProgress);
            RenderStatus.Text = p.Stage;
        });
        var task = RunAsync(); exportTask = task;
        UpdateLoadingProgress();
        try { return await task; }
        finally
        {
            if (ReferenceEquals(exportCancellation, own)) exportCancellation = null;
            exportActive = false; exportTask = Task.CompletedTask; UpdateLoadingProgress();
        }
        async Task<FbxGeometryExportReport> RunAsync()
        {
            RenderAssetSnapshot? full = null;
            try
            {
                var source = current;
                if (allRegions)
                {
                    var loaded = section ?? throw new InvalidOperationException("Open a scene first.");
                    var branch = SelectedBranchIdentity ?? throw new InvalidOperationException("Select a scene branch.");
                    var loader = RenderProvider ?? throw new InvalidOperationException("Scene loading is unavailable.");
                    if (!await ConfirmLargeDataAsync("Export all regions of this scene? This can take extra time and memory.", own.Token))
                        throw new OperationCanceledException(own.Token);
                    full = await loader(new(loaded, branch, new(Vector3.Zero, Vector3.One), SceneRenderScope.Scene)
                        { AllowLargeData = true }, progress, own.Token);
                    source = full;
                }
                var choices = ResolveGeometryExportChoices(source, selected, entireLoadedScene, allRegions, includeHidden, SceneViewport.IsOccurrenceVisible);
                var exportRequest = new SceneFbxExportRequest(choices, merge, placement, includeHidden)
                    { PreserveInstances = allRegions && !merge };
                FbxGeometryExportReport result;
                try { result = await provider(source, exportRequest, path, progress, own.Token); }
                catch (ArchiveException error) when (error.Code == "AssetMemoryLimit" && source.MemoryBudget.MaximumBytes < 2L * 1024 * 1024 * 1024)
                {
                    if (!await ConfirmLargeDataAsync("This export needs more than the normal 512 MiB asset budget. Continue with the 2 GiB allowance?", own.Token))
                        throw new OperationCanceledException(own.Token);
                    using var allowance = source.MemoryBudget.AllowLargeSceneLoad();
                    result = await provider(source, exportRequest, path, progress, own.Token);
                }
                RenderStatus.Text = $"Exported · {result.Losses.Count:N0} notices";
                Advanced.Text = string.Join(Environment.NewLine, result.Losses.Select(loss => loss.Detail));
                return result;
            }
            finally { full?.Dispose(); }
        }
    }

    private static RenderMeshSelection[] ResolveGeometryExportChoices(RenderAssetSnapshot source,
        IEnumerable<RenderMeshSelection> selection, bool entire, bool allRegions, bool includeHidden, Func<string, bool> isVisible)
    {
        var selected = selection.ToArray();
        var occurrences = source.Occurrences.ToDictionary(occurrence => occurrence.Key, StringComparer.Ordinal);
        bool Eligible(RenderOccurrence occurrence) => includeHidden || occurrence.SourceVisible && (allRegions || isVisible(occurrence.Key));
        var choices = entire || allRegions
            ? source.Occurrences.Where(Eligible).Select(occurrence => new RenderMeshSelection(occurrence.Key)).ToArray()
            : selected.Where(item => occurrences.TryGetValue(item.OccurrenceKey, out var occurrence) && Eligible(occurrence)).ToArray();
        if (choices.Length > 0) return choices;
        if (source.Occurrences.Count == 0) throw new InvalidOperationException("No meshes are loaded.");
        if (entire || allRegions) throw new InvalidOperationException("No visible meshes to export. Enable Include hidden to include hidden meshes.");
        if (selected.Length == 0) throw new InvalidOperationException("Select a mesh to export.");
        if (!selected.Any(item => occurrences.ContainsKey(item.OccurrenceKey)))
            throw new InvalidOperationException("The selected meshes are no longer loaded. Select a mesh to export.");
        throw new InvalidOperationException("The selected meshes are hidden. Enable Include hidden to export them.");
    }
    private async void ExportSelection_Click(object sender, RoutedEventArgs e) => await PickExportAsync(false, false);
    private async void ExportLoadedScene_Click(object sender, RoutedEventArgs e) => await PickExportAsync(true, false);
    private async void ExportAllRegions_Click(object sender, RoutedEventArgs e) => await PickExportAsync(false, true);
    private async Task PickExportAsync(bool entire, bool allRegions)
    {
        try
        {
            if (!exportTask.IsCompleted) throw new InvalidOperationException("An export is already running.");
            if (GeometryExportProvider is null) throw new InvalidOperationException("FBX export is unavailable.");
            var source = renderSnapshot ?? throw new InvalidOperationException("Load a scene first.");
            bool includeHidden = IncludeHiddenExport.IsChecked == true;
            if (allRegions)
            {
                if (section is null || SelectedBranchIdentity is null) throw new InvalidOperationException("Select a scene branch.");
                if (RenderProvider is null) throw new InvalidOperationException("Scene loading is unavailable.");
            }
            else ResolveGeometryExportChoices(source, SceneViewport.SelectedMeshes, entire, false, includeHidden, SceneViewport.IsOccurrenceVisible);
            var dialog = new SaveFileDialog { Filter = "FBX model (*.fbx)|*.fbx", DefaultExt = ".fbx", FileName = entire || allRegions ? "Scene.fbx" : "Selection.fbx" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            await ExportGeometryAsync(dialog.FileName, entire, allRegions, MergeExport.IsChecked == true,
                ExportPlacement.SelectedIndex == 1 ? SceneExportPlacement.Centered : SceneExportPlacement.Scene, includeHidden);
        }
        catch (OperationCanceledException) { RenderStatus.Text = "Export cancelled"; }
        catch (Exception error) { RenderStatus.Text = error.Message; Advanced.Text = error.ToString(); }
    }
}
