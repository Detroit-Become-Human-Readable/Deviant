using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Deviant.Assets;
using Deviant.Browser;

namespace Deviant.Desktop;

public partial class AssetModelView : UserControl, IAsyncDisposable
{
    private RenderAssetSnapshot? snapshot;
    private IDisposable? snapshotLease;
    private CancellationTokenSource? loadCancellation;
    private Task loadTask = Task.CompletedTask;
    private long generation;
    private bool loading;
    private bool? compactLayout;
    private bool compactInspectorSizedByUser;
    private GridLength inspectorWidth = new(260), inspectorHeight = new(180);
    public AssetModelView()
    {
        InitializeComponent();
        ModelViewport.UseSelectionTint = false;
        ModelViewport.RenderFailed += (_, error) =>
        {
            ModelStatus.Text = "Unable to render model"; ModelDetails.Text = error.ToString();
            modelActivityTerminalStage = "Failed";
            PublishModelActivity("Failed");
        };
        ModelViewport.LoadProgressChanged += (_, progress) =>
        {
            if (!loading) return;
            ModelLoadingStage.Text = progress.Stage == "Ready" ? "Finishing" : "Uploading";
            ModelLoadingCount.Text = progress.Total > 0 ? $"{progress.Completed:N0} / {progress.Total:N0}" : "";
            ModelLoadingProgress.IsIndeterminate = progress.Total <= 0;
            ModelLoadingProgress.Maximum = Math.Max(progress.Total, 1);
            ModelLoadingProgress.Value = Math.Clamp(progress.Completed, 0, Math.Max(progress.Total, 1));
            PublishModelActivity(ModelLoadingStage.Text, progress.Completed, progress.Total);
        };
    }
    public event EventHandler? BackRequested;
    public Func<RenderTexture, SourceRevision, CancellationToken, Task>? TexturePreviewProvider { get; set; }
    public AssetViewport Viewport => ModelViewport;
    public RenderAssetSnapshot? CurrentSnapshot => snapshot;
    internal Func<RenderAssetSnapshot, CancellationToken, Task>? SnapshotLoadAsyncOverride { get; set; }
    public void SetBackLabel(string label) => BackButton.Content = label;
    public void SetDisplayName(string name) => ModelName.Text = name;
    public void SetViewportService(AssetViewportService service) => ModelViewport.SetService(service);

    private Task LoadViewportSnapshotAsync(RenderAssetSnapshot model, CancellationToken token) =>
        SnapshotLoadAsyncOverride is { } load ? load(model, token) : ModelViewport.LoadSnapshotAsync(model, token);

    private void ModelLayout_SizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyModelLayout();

    private void ModelDetailsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (ModelLayout is null || ModelInspectorBody is null) return;
        if (compactLayout == true && compactInspectorSizedByUser && ModelLayout.RowDefinitions[2].Height.IsAbsolute &&
            ModelLayout.RowDefinitions[2].Height.Value > 0)
            inspectorHeight = ModelLayout.RowDefinitions[2].Height;
        ApplyModelLayout();
    }

    private void ModelInspectorSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (e.Canceled || compactLayout != true || ModelDetailsToggle.IsChecked != true) return;
        compactInspectorSizedByUser = true;
        inspectorHeight = ModelLayout.RowDefinitions[2].Height;
    }

    private void ApplyModelLayout()
    {
        var compact = ActualWidth < 640;
        if (ModelLayout is null || ModelDetailsToggle is null || ModelInspectorBody is null) return;
        if (compactLayout == false) inspectorWidth = ModelLayout.ColumnDefinitions[2].Width;
        if (compactLayout == true && compactInspectorSizedByUser && ModelLayout.RowDefinitions[2].Height.IsAbsolute &&
            ModelLayout.RowDefinitions[2].Height.Value > 0)
            inspectorHeight = ModelLayout.RowDefinitions[2].Height;
        compactLayout = compact;
        var expanded = !compact || ModelDetailsToggle.IsChecked == true;
        var availableHeight = Math.Max(0, ModelLayout.ActualHeight);
        var minimumViewport = Math.Min(Math.Max(0, availableHeight - 40 - 8), 140);
        ModelLayout.ColumnDefinitions[1].Width = new(compact ? 0 : 8);
        ModelLayout.ColumnDefinitions[2].Width = compact ? new(0) : inspectorWidth;
        ModelLayout.RowDefinitions[0].MinHeight = compact ? minimumViewport : 0;
        ModelLayout.RowDefinitions[1].Height = new(compact && expanded ? 8 : 0);
        ModelLayout.RowDefinitions[2].MinHeight = compact && expanded ? Math.Min(80, availableHeight * .2) : 0;
        ModelLayout.RowDefinitions[2].MaxHeight = compact && expanded
            ? Math.Max(ModelLayout.RowDefinitions[2].MinHeight, availableHeight - minimumViewport - 8)
            : double.PositiveInfinity;
        var compactHeight = inspectorHeight;
        if (!compactInspectorSizedByUser && inspectorHeight.IsAbsolute)
            compactHeight = new(Math.Clamp(Math.Min(inspectorHeight.Value, availableHeight * .35 - 8),
                ModelLayout.RowDefinitions[2].MinHeight, ModelLayout.RowDefinitions[2].MaxHeight));
        ModelLayout.RowDefinitions[2].Height = compact
            ? expanded ? compactHeight : GridLength.Auto : new(0);
        ModelDetailsToggle.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        ModelInspectorBody.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ModelInspectorSplitter.IsEnabled = expanded;
        Grid.SetColumnSpan(ModelViewport, compact ? 3 : 1);
        Grid.SetRow(ModelInspector, compact ? 2 : 0);
        Grid.SetColumn(ModelInspector, compact ? 0 : 2);
        Grid.SetColumnSpan(ModelInspector, compact ? 3 : 1);
        ModelInspector.Margin = compact ? new(0, 8, 0, 0) : new(8, 0, 0, 0);
        Grid.SetRow(ModelInspectorSplitter, compact ? 1 : 0);
        Grid.SetColumn(ModelInspectorSplitter, compact ? 0 : 1);
        Grid.SetColumnSpan(ModelInspectorSplitter, compact ? 3 : 1);
        ModelInspectorSplitter.Width = compact ? double.NaN : 8;
        ModelInspectorSplitter.Height = compact ? 8 : double.NaN;
        ModelInspectorSplitter.ResizeDirection = compact ? GridResizeDirection.Rows : GridResizeDirection.Columns;
    }

    public Task LoadAsync(RenderAssetSnapshot scene, RenderOccurrence sourceOccurrence, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(sourceOccurrence);
        return BeginLoadAsync((cancellation, current) => LoadCoreAsync(scene, sourceOccurrence, cancellation, current), token);
    }

    /// <summary>Loads all model-local placements from a reusable native model snapshot.</summary>
    public Task LoadAsync(RenderAssetSnapshot model, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Occurrences.Count == 0) throw new ArgumentException("The model snapshot contains no placements.", nameof(model));
        return BeginLoadAsync((cancellation, current) => LoadSnapshotCoreAsync(model, cancellation, current), token);
    }

    private async Task BeginLoadAsync(Func<CancellationTokenSource, long, Task> operation, CancellationToken token)
    {
        loadCancellation?.Cancel();
        long current = ++generation;
        try { await loadTask; } catch (OperationCanceledException) { } catch { }
        if (current != generation) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        loadCancellation = cancellation;
        var task = operation(cancellation, current);
        loadTask = task;
        try { await task; }
        finally
        {
            if (ReferenceEquals(loadTask, task)) loadTask = Task.CompletedTask;
            if (ReferenceEquals(loadCancellation, cancellation)) loadCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task LoadSnapshotCoreAsync(RenderAssetSnapshot model, CancellationTokenSource cancellation, long current)
    {
        IDisposable? borrowed = null;
        loading = true;
        ModelLoadingPanel.Visibility = Visibility.Visible;
        ModelProcessing.IsActive = true;
        ModelLoadingStage.Text = "Preparing model";
        ModelLoadingCount.Text = "";
        ModelLoadingProgress.IsIndeterminate = true;
        BeginModelActivity();
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (current != generation) throw new OperationCanceledException(cancellation.Token);
            borrowed = model.AcquireLease();
            await ReleaseAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            if (current != generation) throw new OperationCanceledException(cancellation.Token);
            ModelViewport.ShowHidden = true;
            await LoadViewportSnapshotAsync(model, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (current != generation) throw new OperationCanceledException(cancellation.Token);
            snapshot = model;
            snapshotLease = borrowed;
            borrowed = null;
            var meshes = model.Occurrences.Select(occurrence => model.Meshes[occurrence.GeometryKey])
                .DistinctBy(mesh => mesh.Key).ToArray();
            ModelViewport.SetSelection(model.Occurrences[0].Key);
            ModelName.Text = meshes.Length == 1 ? "Model" : $"Models · {meshes.Length:N0} groups";
            ModelSummary.Text = $"{meshes.Sum(mesh => mesh.Geometry.Primitives.Sum(primitive => primitive.Positions.Count)):N0} vertices · " +
                $"{meshes.Sum(mesh => mesh.Geometry.Primitives.Sum(primitive => primitive.Indices.Count / 3)):N0} triangles";
            var materials = meshes.SelectMany(mesh => mesh.MaterialsByPrimitive?.Values ?? [])
                .Distinct().Where(model.Materials.ContainsKey).Select(identity => model.Materials[identity]).ToArray();
            ModelTextures.ItemsSource = SceneView.BuildTextureRows(materials);
            ModelDetails.Text = string.Join("\n", model.Occurrences.Select(occurrence =>
                occurrence.ModelPlacement is { } placement
                    ? $"Model-local mesh {placement.MeshIdentity.Key}\nGroup {placement.GroupIndex}\nPlacement Model local"
                    : $"Placement {occurrence.Label}\nOrigin {occurrence.PlacementOrigin}")) +
                "\n" + string.Join("\n", materials.Select(material => $"Material {material.Asset.Identity.Key}\nProfile {material.Asset.Profile}\n" +
                    $"Graph slots: {string.Join(", ", material.Asset.SurfaceGraph?.TextureSlots ?? [])}\n" +
                    $"Inputs: {string.Join(", ", material.Asset.SurfaceGraph?.RequiredInputs ?? [])}\n" +
                    string.Join("\n", material.Asset.Diagnostics.Concat(material.Asset.SurfaceGraph?.Diagnostics ?? [])) + "\n" +
                    string.Join("\n", material.TexturesBySlot.Select(pair => SceneView.FormatTextureDetails(pair.Key, pair.Value))))) +
                "\n" + SceneView.FormatIssues(model.Issues.Concat(ModelViewport.RendererIssues));
            ModelStatus.Text = "";
        }
        catch (OperationCanceledException)
        {
            if (current == generation) modelActivityTerminalStage = "Cancelled";
            await ReleaseAsync(); ModelStatus.Text = ""; throw;
        }
        catch
        {
            if (current == generation) modelActivityTerminalStage = "Failed";
            await ReleaseAsync(); ModelStatus.Text = ""; throw;
        }
        finally
        {
            borrowed?.Dispose();
            if (current == generation)
            {
                loading = false; ModelLoadingPanel.Visibility = Visibility.Collapsed; ModelProcessing.IsActive = false;
                PublishModelActivity(modelActivityTerminalStage);
            }
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
        BeginModelActivity();
        try
        {
            await ReleaseAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            var mesh = scene.Meshes[sourceOccurrence.GeometryKey];
            candidate = CreateModelSnapshot(scene, sourceOccurrence, cancellation.Token);
            var occurrence = candidate.Occurrences[0];
            ModelViewport.ShowHidden = true;
            await LoadViewportSnapshotAsync(candidate, cancellation.Token);
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
        catch (OperationCanceledException)
        {
            if (current == generation) modelActivityTerminalStage = "Cancelled";
            await ReleaseAsync(); ModelStatus.Text = ""; throw;
        }
        catch
        {
            if (current == generation) modelActivityTerminalStage = "Failed";
            await ReleaseAsync(); ModelStatus.Text = ""; throw;
        }
        finally
        {
            candidate?.Dispose();
            if (current == generation)
            {
                loading = false; ModelLoadingPanel.Visibility = Visibility.Collapsed; ModelProcessing.IsActive = false;
                PublishModelActivity(modelActivityTerminalStage);
            }
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
        var wasLoading = loading;
        ++generation; loadCancellation?.Cancel();
        try { await loadTask; } catch (OperationCanceledException) { } catch { }
        await ReleaseAsync();
        loading = false; ModelLoadingPanel.Visibility = Visibility.Collapsed; ModelProcessing.IsActive = false;
        if (wasLoading) modelActivityTerminalStage = "Cancelled";
        PublishModelActivity(modelActivityTerminalStage);
    }
    private async Task ReleaseAsync()
    {
        await ClearAnimationAsync();
        await ModelViewport.UnloadAsync();
        if (snapshotLease is { } lease)
        {
            snapshotLease = null;
            lease.Dispose();
        }
        else snapshot?.Dispose();
        snapshot = null;
        ModelTextures.ItemsSource = null; ModelDetails.Text = ModelSummary.Text = ModelName.Text = ModelStatus.Text = "";
    }
    public async ValueTask DisposeAsync() { await UnloadAsync(); await ModelAnimationPlayback.DisposeAsync(); await ModelViewport.DisposeAsync(); ModelProcessing.Dispose(); }
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
    private async void ModelTextures_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ModelTextures.SelectedItem is not RenderTextureRow { Texture: { } texture } || snapshot is not { } source || TexturePreviewProvider is null) return;
        try { await TexturePreviewProvider(texture, source.Revision, CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ModelStatus.Text = error.Message; }
    }
}
