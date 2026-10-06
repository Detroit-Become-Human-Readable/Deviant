using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Deviant.Assets;
using Deviant.Browser;

namespace Deviant.Desktop;

public partial class AssetModelView : UserControl, IAsyncDisposable
{
    private RenderAssetSnapshot? snapshot;
    private CancellationTokenSource? loadCancellation;
    private Task loadTask = Task.CompletedTask;
    private long generation;
    private bool loading;
    public AssetModelView()
    {
        InitializeComponent();
        ModelViewport.RenderFailed += (_, error) => { ModelStatus.Text = "Unable to render model"; ModelDetails.Text = error.ToString(); };
        ModelViewport.LoadProgressChanged += (_, progress) =>
        {
            if (!loading) return;
            ModelLoadingStage.Text = progress.Stage == "Ready" ? "Finishing" : "Uploading";
            ModelLoadingCount.Text = progress.Total > 0 ? $"{progress.Completed:N0} / {progress.Total:N0}" : "";
            ModelLoadingProgress.IsIndeterminate = progress.Total <= 0;
            ModelLoadingProgress.Maximum = Math.Max(progress.Total, 1);
            ModelLoadingProgress.Value = Math.Clamp(progress.Completed, 0, Math.Max(progress.Total, 1));
        };
    }
    public event EventHandler? BackRequested;
    public Func<RenderTexture, SourceRevision, CancellationToken, Task>? TexturePreviewProvider { get; set; }
    public AssetViewport Viewport => ModelViewport;
    public RenderAssetSnapshot? CurrentSnapshot => snapshot;
    public void SetViewportService(AssetViewportService service) => ModelViewport.SetService(service);

    public async Task LoadAsync(RenderAssetSnapshot scene, RenderOccurrence sourceOccurrence, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(sourceOccurrence);
        loadCancellation?.Cancel();
        long current = ++generation;
        try { await loadTask; } catch (OperationCanceledException) { } catch { }
        if (current != generation) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        loadCancellation = cancellation;
        var task = LoadCoreAsync(scene, sourceOccurrence, cancellation, current);
        loadTask = task;
        try { await task; }
        finally
        {
            if (ReferenceEquals(loadTask, task)) loadTask = Task.CompletedTask;
            if (ReferenceEquals(loadCancellation, cancellation)) loadCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task LoadCoreAsync(RenderAssetSnapshot scene, RenderOccurrence sourceOccurrence, CancellationTokenSource cancellation, long current)
    {
        RenderAssetSnapshot? candidate = null;
        loading = true;
        ModelLoadingPanel.Visibility = Visibility.Visible;
        ModelProcessing.IsActive = true;
        ModelLoadingStage.Text = "Preparing model";
        ModelLoadingCount.Text = "";
        ModelLoadingProgress.IsIndeterminate = true;
        try
        {
            await ReleaseAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            var mesh = scene.Meshes[sourceOccurrence.GeometryKey];
            candidate = CreateModelSnapshot(scene, sourceOccurrence, cancellation.Token);
            var occurrence = candidate.Occurrences[0];
            ModelViewport.ShowHidden = true;
            await ModelViewport.LoadSnapshotAsync(candidate, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (current != generation) throw new OperationCanceledException(cancellation.Token);
            snapshot = candidate; candidate = null;
            ModelViewport.SetSelection(occurrence.Key);
            ModelName.Text = SceneNameDisplay.Project(sourceOccurrence.Label, "Model");
            ModelSummary.Text = $"{mesh.Geometry.Primitives.Sum(item => item.Positions.Count):N0} vertices · {mesh.Geometry.Primitives.Sum(item => item.Indices.Count / 3):N0} triangles";
            var materials = (mesh.MaterialsByPrimitive?.Values ?? []).Distinct().Where(scene.Materials.ContainsKey).Select(id => scene.Materials[id]).ToArray();
            ModelTextures.ItemsSource = SceneView.BuildTextureRows(materials);
            ModelDetails.Text = $"Source name {sourceOccurrence.Label}\nMesh {mesh.Identity.Key}\nGroup {mesh.GroupIndex}\nSource occurrence {sourceOccurrence.Key}\n" +
                string.Join("\n", materials.Select(material => $"Material {material.Asset.Identity.Key}\nProfile {material.Asset.Profile}\n" +
                    $"Graph slots: {string.Join(", ", material.Asset.SurfaceGraph?.TextureSlots ?? [])}\n" +
                    $"Inputs: {string.Join(", ", material.Asset.SurfaceGraph?.RequiredInputs ?? [])}\n" +
                    string.Join("\n", material.Asset.Diagnostics.Concat(material.Asset.SurfaceGraph?.Diagnostics ?? [])) + "\n" +
                    string.Join("\n", material.TexturesBySlot.Select(pair => SceneView.FormatTextureDetails(pair.Key, pair.Value))))) +
                "\n" + SceneView.FormatIssues(scene.Issues.Where(issue => issue.OccurrenceKey is null || issue.OccurrenceKey == sourceOccurrence.Key).Concat(ModelViewport.RendererIssues));
            ModelStatus.Text = "";
        }
        catch
        {
            await ReleaseAsync(); ModelStatus.Text = ""; throw;
        }
        finally
        {
            candidate?.Dispose();
            if (current == generation) { loading = false; ModelLoadingPanel.Visibility = Visibility.Collapsed; ModelProcessing.IsActive = false; }
        }
    }

    public static RenderAssetSnapshot CreateModelSnapshot(RenderAssetSnapshot scene, RenderOccurrence sourceOccurrence,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(sourceOccurrence);
        token.ThrowIfCancellationRequested();
        var mesh = scene.Meshes[sourceOccurrence.GeometryKey];
        var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
        foreach (var primitive in mesh.Geometry.Primitives)
        {
            token.ThrowIfCancellationRequested();
            foreach (var position in primitive.Positions) { minimum = Vector3.Min(minimum, position); maximum = Vector3.Max(maximum, position); }
        }
        if (!Finite(minimum) || !Finite(maximum)) throw new InvalidOperationException("The model bounds are unavailable.");
        token.ThrowIfCancellationRequested();
        var bounds = new GeometryBounds(minimum, maximum);
        var occurrence = sourceOccurrence with { Key = "model/" + sourceOccurrence.Key, World = Matrix4x4.Identity, WorldBounds = bounds };
        return new(scene.Revision, new Dictionary<string, RenderMesh> { [mesh.Key] = mesh }, [occurrence], scene.Materials,
            bounds, scene.Issues, [], new(1, 1, 0, mesh.Geometry.Primitives.Count, 1, 0), scene.SourceExtents,
            scene.MemoryBudget, [scene.AcquireLease()]);
        static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    public async Task UnloadAsync()
    {
        ++generation; loadCancellation?.Cancel();
        try { await loadTask; } catch (OperationCanceledException) { } catch { }
        await ReleaseAsync();
        loading = false; ModelLoadingPanel.Visibility = Visibility.Collapsed; ModelProcessing.IsActive = false;
    }
    private async Task ReleaseAsync()
    {
        await ModelViewport.UnloadAsync();
        snapshot?.Dispose(); snapshot = null;
        ModelTextures.ItemsSource = null; ModelDetails.Text = ModelSummary.Text = ModelName.Text = ModelStatus.Text = "";
    }
    public async ValueTask DisposeAsync() { await UnloadAsync(); await ModelViewport.DisposeAsync(); ModelProcessing.Dispose(); }
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
    private async void ModelTextures_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ModelTextures.SelectedItem is not RenderTextureRow row || snapshot is not { } source || TexturePreviewProvider is null) return;
        try { await TexturePreviewProvider(row.Texture, source.Revision, CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ModelStatus.Text = error.Message; }
    }
}
