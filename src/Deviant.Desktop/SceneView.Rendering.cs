using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Scenes;

namespace Deviant.Desktop;

public sealed record SceneBranchChoice(SceneCatalogInstance Instance)
{
    public string Label => SceneNameDisplay.Project(string.IsNullOrWhiteSpace(Instance.InstanceName) ? Instance.CatalogName : Instance.InstanceName, "Placement");
}

public sealed class RenderTreeRow : INotifyPropertyChanged
{
    private bool visible = true;
    private bool selected, expanded;
    private readonly Action<RenderTreeRow, bool> visibilityChanged;
    public RenderTreeRow(RenderHierarchyNode node, Action<RenderTreeRow, bool> visibilityChanged)
    { Node = node; this.visibilityChanged = visibilityChanged; Label = SceneNameDisplay.Project(node.Label, node.OccurrenceKey is null ? "Transform" : "Mesh"); }
    public RenderHierarchyNode Node { get; }
    public string Label { get; internal set; }
    public ObservableCollection<RenderTreeRow> Children { get; } = [];
    public bool IsSelected
    {
        get => selected;
        set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
    }
    public bool IsExpanded
    {
        get => expanded;
        set { if (expanded == value) return; expanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); }
    }
    public bool IsVisible
    {
        get => visible;
        set
        {
            if (visible == value) return;
            visible = value; PropertyChanged?.Invoke(this, new(nameof(IsVisible)));
            visibilityChanged(this, value);
            foreach (var child in Children) child.IsVisible = value;
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record RenderTextureRow(ResourceIdentity Material, int SlotOrdinal, RenderTexture? Texture,
    MaterialTextureBinding? Binding = null, string? Usage = null)
{
    public RenderTextureRow(ResourceIdentity material, MaterialTextureBinding binding, RenderTexture texture)
        : this(material, binding.SlotOrdinal, texture, binding) { }
    public string Label => $"{Usage ?? RoleLabel(Binding?.Role)} · " + (Texture is { } texture
        ? $"{texture.Surface.Width} × {texture.Surface.Height}" : "Unavailable");
    public string Details => Texture is { } texture ? SceneView.FormatTextureDetails(SlotOrdinal, texture) :
        $"Slot {SlotOrdinal}\nReference {Binding?.Texture.ResourceType}/{Binding?.Texture.Id}\n{Failure ?? "Texture could not be loaded."}";
    public string? Failure { get; init; }
    private static string RoleLabel(MaterialTextureRole? role) => role switch
    {
        MaterialTextureRole.Normal => "Normal",
        MaterialTextureRole.BaseColor => "Colour",
        MaterialTextureRole.Opacity => "Opacity",
        MaterialTextureRole.Roughness => "Roughness",
        _ => "Texture"
    };
}

public partial class SceneView
{
    private RenderAssetSnapshot? renderSnapshot;
    private RenderOccurrence? selectedOccurrence;
    private CancellationTokenSource? renderCancellation;
    private Task renderTask = Task.CompletedTask;
    private long renderGeneration;
    private long restoreGeneration;
    private SceneRenderRequest? lastSuccessfulSceneRenderRequest;
    private AssetMemoryBudget? lastSuccessfulRenderBudget;
    private bool selectingRenderTree;
    private bool updatingBranches;
    private readonly Dictionary<string, RenderTreeRow> renderRows = new(StringComparer.Ordinal);

    public Func<SceneRenderRequest, IProgress<RenderLoadProgress>?, CancellationToken, Task<RenderAssetSnapshot>>? RenderProvider { get; set; }
    public Func<SceneRenderRequest, IProgress<RenderLoadProgress>?, Func<RenderAssetSnapshot, CancellationToken, Task>, CancellationToken, Task<RenderAssetSnapshot>>? StreamingRenderProvider { get; set; }
    public Func<IDisposable>? ForegroundWorkProvider { get; set; }
    public Func<CancellationToken, Task>? BackgroundDrainProvider { get; set; }
    public Func<string, CancellationToken, Task<bool>>? LargeDataConfirmationProvider { get; set; }
    public Func<RenderAssetSnapshot, RenderOccurrence, CancellationToken, Task>? ModelPreviewProvider { get; set; }
    public Func<RenderTexture, SourceRevision, CancellationToken, Task>? TexturePreviewProvider { get; set; }
    public Func<RenderTextureSelection, CancellationToken, Task>? LightingTexturePreviewProvider { get; set; }
    public RenderAssetSnapshot? CurrentRenderSnapshot => renderSnapshot;
    public RenderOccurrence? SelectedOccurrence => selectedOccurrence;
    public AssetViewport Viewport => SceneViewport;
    public ResourceIdentity? SelectedBranchIdentity => (BranchChoice.SelectedItem as SceneBranchChoice)?.Instance.Identity;
    public GeometryBounds Region => ReadRegion();

    private void InitializeRendering()
    {
        SceneViewport.MeshSelectionChanged += (_, selected) => SelectMeshes(selected);
        SceneViewport.RenderFailed += (_, error) => { WorkflowDiagnostics.Failure("Scene.Render", error, section?.Descriptor.Identity.Key); RenderStatus.Text = "Unable to render scene"; Advanced.Text = error.ToString(); };
        SceneViewport.LoadProgressChanged += (_, progress) => UpdateRenderLoadingProgress(progress);
    }

    public void SetViewportService(AssetViewportService service) => SceneViewport.SetService(service);

    public void SetRegion(GeometryBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        var center = (bounds.Minimum + bounds.Maximum) * .5f;
        var size = bounds.Maximum - bounds.Minimum;
        RegionCenterX.Text = Number(center.X); RegionCenterY.Text = Number(center.Y); RegionCenterZ.Text = Number(center.Z);
        RegionSizeX.Text = Number(size.X); RegionSizeY.Text = Number(size.Y); RegionSizeZ.Text = Number(size.Z);
    }

    public void SelectBranch(ResourceIdentity identity)
    {
        var choice = BranchChoice.Items.OfType<SceneBranchChoice>().SingleOrDefault(item => item.Instance.Identity == identity)
            ?? throw new InvalidOperationException("The requested branch is unavailable.");
        updatingBranches = true;
        try { BranchChoice.SelectedItem = choice; }
        finally { updatingBranches = false; }
        ResetRegionFromBranch();
    }

    private GeometryBounds ReadRegion()
    {
        var center = new Vector3(Parse(RegionCenterX), Parse(RegionCenterY), Parse(RegionCenterZ));
        var size = new Vector3(Parse(RegionSizeX), Parse(RegionSizeY), Parse(RegionSizeZ));
        if (size.X <= 0 || size.Y <= 0 || size.Z <= 0) throw new InvalidOperationException("Region size must be positive.");
        var minimum = center - size * .5f; var maximum = center + size * .5f;
        if (!Finite(minimum) || !Finite(maximum)) throw new InvalidOperationException("The region is too large.");
        return new(minimum, maximum);
        static float Parse(TextBox input) => float.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && float.IsFinite(number)
            ? number : throw new InvalidOperationException("Enter a valid region value.");
        static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
    private static string Number(float value) => value.ToString("G9", CultureInfo.InvariantCulture);

    private void BuildBranchChoices(SceneLoadedSection loaded)
    {
        updatingBranches = true;
        try
        {
            BranchChoice.ItemsSource = loaded.Result.Value!.CatalogInstances.Select(item => new SceneBranchChoice(item)).ToArray();
            BranchChoice.SelectedIndex = BranchChoice.Items.Count > 0 ? 0 : -1;
        }
        finally { updatingBranches = false; }
        ResetRegionFromBranch();
        UpdateRegionButtons();
    }

    private void SelectBranchForNode(SceneHierarchyNode node, ResourceIdentity? entity)
    {
        if (section?.Result.Value is not { } document) return;
        var choices = BranchChoice.Items.OfType<SceneBranchChoice>().ToArray();
        var preferred = entity is { } identity ? choices.FirstOrDefault(item => item.Instance.Identity == identity) : null;
        var current = node;
        var visited = new HashSet<ResourceIdentity>();
        while (preferred is null && visited.Add(current.Identity))
        {
            var entities = section.EntitiesByNode.GetValueOrDefault(current.Identity) ?? [];
            preferred = choices.FirstOrDefault(item => entities.Any(owner => owner.Identity == item.Instance.Identity));
            if (current.ParentIdentity is not { } parent) break;
            var parentNode = document.Hierarchy.Nodes.FirstOrDefault(item => item.Identity == parent);
            if (parentNode is null) break;
            current = parentNode;
        }
        if (preferred is not null && !ReferenceEquals(BranchChoice.SelectedItem, preferred)) BranchChoice.SelectedItem = preferred;
    }

    private void ResetRegionFromBranch()
    {
        if (BranchChoice.SelectedItem is not SceneBranchChoice choice || section is null) return;
        var node = FindEntityNode(section, choice.Instance.Identity);
        var values = node?.WorldTransform?.Elements;
        var center = values is { Count: 16 } ? new Vector3(values[12], values[13], values[14]) : Vector3.Zero;
        SetRegion(new(center - new Vector3(10), center + new Vector3(10)));
    }

    private void BranchChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingBranches) ResetRegionFromBranch();
        UpdateRegionButtons();
    }

    public Task LoadRegionAsync(CancellationToken token = default, SceneRenderScope scope = SceneRenderScope.Region,
        int maximumPreviewDimension = 1024) => LoadRegionAsync(token, scope, maximumPreviewDimension, null);

    private async Task LoadRegionAsync(CancellationToken token, SceneRenderScope scope,
        int maximumPreviewDimension, GeometryBounds? regionOverride, bool? allowLargeDataOverride = null,
        AssetMemoryBudget? expectedBudget = null)
    {
        var loaded = section ?? throw new InvalidOperationException("Open a scene first.");
        var branch = BranchChoice.SelectedItem as SceneBranchChoice ?? throw new InvalidOperationException("Select a scene branch.");
        var provider = RenderProvider ?? throw new InvalidOperationException("Scene rendering is unavailable.");
        var region = regionOverride ?? (scope == SceneRenderScope.Scene
            ? renderSnapshot?.Region ?? new GeometryBounds(Vector3.Zero, Vector3.One) : ReadRegion());
        renderCancellation?.Cancel();
        var previous = renderTask;
        var generation = ++renderGeneration;
        try { await previous; } catch (OperationCanceledException) { } catch { }
        if (generation != renderGeneration) return;
        var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        renderCancellation = own;
        var task = LoadRegionCoreAsync(new(loaded, branch.Instance.Identity, region, scope)
            { MaximumPreviewDimension = maximumPreviewDimension, AllowLargeData = allowLargeDataOverride ?? HasLargeDataApproval(loaded) },
            provider, own, generation, expectedBudget);
        renderTask = task;
        try { await task; }
        finally
        {
            if (ReferenceEquals(renderTask, task)) renderTask = Task.CompletedTask;
            if (ReferenceEquals(renderCancellation, own)) renderCancellation = null;
            own.Dispose();
        }
    }

    private async Task LoadRegionCoreAsync(SceneRenderRequest request,
        Func<SceneRenderRequest, IProgress<RenderLoadProgress>?, CancellationToken, Task<RenderAssetSnapshot>> provider,
        CancellationTokenSource cancellation, long generation, AssetMemoryBudget? expectedBudget = null)
    {
        RenderAssetSnapshot? candidate = null;
        using var diagnostic = WorkflowDiagnostics.Begin("Scene.Load", request.Section.Descriptor.Identity.Key);
        using var foreground = ForegroundWorkProvider?.Invoke();
        var previousSnapshot = renderSnapshot;
        var previousOccurrenceKey = selectedOccurrence?.Key;
        var previousMeshSelections = SceneViewport.SelectedMeshes;
        var previousRenderTreeKey = RenderedTab.IsSelected
            ? (RenderHierarchy.SelectedItem as RenderTreeRow)?.Node.Key : null;
        var previousStructureIdentity = previousOccurrenceKey is null && SceneTabs.SelectedIndex == 1
            ? selectedEntity ?? selectedNode?.Identity : null;
        var viewportTouched = false;
        bool streamed = false;
        IDisposable? provisionalLease = null;
        var previousCamera = renderSnapshot is not null
            ? SceneViewport.Viewport.Camera?.CloneCurrentValue() as HelixToolkit.Wpf.SharpDX.PerspectiveCamera : null;
        LoadRegionButton.IsEnabled = false;
        activityTerminalStage = null;
        renderLoadingActive = true;
        renderLoadingStage = "Reading scene";
        renderLoadingCompleted = renderLoadingTotal = 0;
        renderLoadingTotalKnown = false;
        UpdateRegionButtons();
        UpdateLoadingProgress();
        try
        {
            if (BackgroundDrainProvider is { } drain) await drain(cancellation.Token);
            using var progress = new CoalescedDispatcherProgress<RenderLoadProgress>(Dispatcher, value =>
            {
                if (generation == renderGeneration && !cancellation.IsCancellationRequested)
                {
                    diagnostic?.Progress(value.Stage, value.Completed, value.Total > 0 ? value.Total : null, "parts");
                    UpdateRenderLoadingProgress(value);
                }
            });
            try { await LoadCandidateAsync(); }
            catch (ArchiveException error) when (IsLargeWorkloadLimit(error.Code) && !request.AllowLargeData)
            {
                candidate?.Dispose();
                candidate = null;
                var subject = request.Scope switch
                {
                    SceneRenderScope.Scene => "This scene contains a lot of data and may take extra time to load. Continue?",
                    SceneRenderScope.Region => "This region contains a lot of data and may take extra time to load. Continue?",
                    _ => "This part of the scene contains a lot of data and may take extra time to load. Continue?"
                };
                cancellation.Token.ThrowIfCancellationRequested();
                renderLoadingStage = "Confirming large load";
                UpdateLoadingProgress();
                if (!await ConfirmLargeDataAsync(subject, cancellation.Token))
                {
                    RenderStatus.Text = "Load cancelled";
                    throw new OperationCanceledException(cancellation.Token);
                }
                cancellation.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(section, request.Section) || request.Section.Result.Revision != session?.Revision)
                    throw new OperationCanceledException(cancellation.Token);
                ApproveLargeData(request.Section);
                var releasedPrevious = previousSnapshot;
                previousSnapshot = null;
                previousOccurrenceKey = null;
                previousMeshSelections = [];
                previousRenderTreeKey = null;
                previousStructureIdentity = null;
                previousCamera = null;
                viewportTouched = true;
                try { await ReleaseRenderSnapshotAsync(); }
                finally { releasedPrevious?.Dispose(); }
                provisionalLease?.Dispose();
                provisionalLease = null;
                UpdateRenderLoadingProgress(new("Reading scene", 0, 0, 0));
                request = request with { AllowLargeData = true };
                await LoadCandidateAsync();
            }
            if (!streamed && request.Scope == SceneRenderScope.Scene && previousCamera is not null && SceneViewport.Viewport.Camera is HelixToolkit.Wpf.SharpDX.PerspectiveCamera camera)
            {
                camera.Position = previousCamera.Position;
                camera.LookDirection = previousCamera.LookDirection;
                camera.UpDirection = previousCamera.UpDirection;
                camera.NearPlaneDistance = previousCamera.NearPlaneDistance;
            }
            if (previousSnapshot is not null && ReferenceEquals(renderSnapshot, previousSnapshot)) ClearRenderInspector();
            renderSnapshot = candidate ?? throw new InvalidOperationException("The scene renderer returned no snapshot."); candidate = null;
            if (previousSnapshot is not null && !ReferenceEquals(previousSnapshot, renderSnapshot)) previousSnapshot.Dispose();
            BuildRenderHierarchy(renderSnapshot);
            RenderedTab.IsSelected = true;
            UpdateRenderedCount();
            Advanced.Text = FormatCoverage(renderSnapshot) + "\n" + FormatIssues(renderSnapshot.Issues.Concat(SceneViewport.RendererIssues));
            lastSuccessfulSceneRenderRequest = request;
            lastSuccessfulRenderBudget = renderSnapshot.MemoryBudget;
            if (renderSnapshot.HasIncompleteMaterials)
            {
                SetActivityTerminal("Materials incomplete");
                diagnostic?.Partial();
            }

            async Task LoadCandidateAsync()
            {
                if (StreamingRenderProvider is { } streaming)
                    candidate = await streaming(request, progress, async (slice, sliceToken) =>
                    {
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            sliceToken.ThrowIfCancellationRequested();
                            if (generation != renderGeneration || !ReferenceEquals(section, request.Section)) throw new OperationCanceledException(sliceToken);
                            if (!streamed) await SceneViewport.UnloadAsync();
                            viewportTouched = true;
                            await SceneViewport.AppendSnapshotAsync(slice, sliceToken);
                            var held = slice.AcquireLease(); provisionalLease?.Dispose(); provisionalLease = held;
                            renderSnapshot = slice; streamed = true;
                            RenderedTab.IsSelected = true; UpdateRenderedCount();
                        }, DispatcherPriority.Normal, sliceToken).Task.Unwrap();
                    }, cancellation.Token);
                else candidate = await provider(request, progress, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (generation != renderGeneration || !ReferenceEquals(section, request.Section))
                    throw new OperationCanceledException(cancellation.Token);
                if (expectedBudget is not null && !ReferenceEquals(candidate.MemoryBudget, expectedBudget))
                    throw new InvalidOperationException("The restored scene belongs to another render budget.");
                viewportTouched = true;
                if (streamed) await SceneViewport.CompleteSnapshotAsync(candidate, cancellation.Token);
                else await LoadViewportSnapshotAsync(candidate, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (generation != renderGeneration || !ReferenceEquals(section, request.Section))
                    throw new OperationCanceledException(cancellation.Token);
            }
        }
        catch (OperationCanceledException error)
        {
            diagnostic?.Cancel();
            if (viewportTouched) await RestorePreviousSnapshotAsync(error);
            SetActivityTerminal("Cancelled");
            throw;
        }
        catch (Exception error)
        {
            diagnostic?.Fail(error, error is ArchiveException archive ? archive.Code : null, partial: viewportTouched);
            if (viewportTouched) await RestorePreviousSnapshotAsync(error);
            SetActivityTerminal("Failed");
            throw;
        }
        finally
        {
            diagnostic?.Complete();
            candidate?.Dispose();
            provisionalLease?.Dispose();
            if (generation == renderGeneration)
            {
                renderLoadingActive = false;
                UpdateLoadingProgress();
                UpdateRegionButtons();
            }
        }

        async Task RestorePreviousSnapshotAsync(Exception originalFailure)
        {
            try
            {
                await SceneViewport.UnloadAsync();
                var discarded = candidate;
                candidate = null;
                discarded?.Dispose();
                if (previousSnapshot is null)
                {
                    await ReleaseRenderSnapshotAsync();
                    return;
                }
                renderSnapshot = previousSnapshot;
                await LoadViewportSnapshotAsync(previousSnapshot, CancellationToken.None);
                if (previousCamera is not null && SceneViewport.Viewport.Camera is HelixToolkit.Wpf.SharpDX.PerspectiveCamera camera)
                {
                    camera.Position = previousCamera.Position;
                    camera.LookDirection = previousCamera.LookDirection;
                    camera.UpDirection = previousCamera.UpDirection;
                    camera.NearPlaneDistance = previousCamera.NearPlaneDistance;
                }
                ClearRenderInspector();
                selectedOccurrence = null;
                BuildRenderHierarchy(previousSnapshot);
                UpdateRenderedCount();
                Advanced.Text = FormatCoverage(previousSnapshot) + "\n" + FormatIssues(previousSnapshot.Issues.Concat(SceneViewport.RendererIssues));
                if (previousMeshSelections.Count > 0)
                    SelectMeshes(previousMeshSelections);
                else if (previousOccurrenceKey is { } occurrenceKey && previousSnapshot.Occurrences.Any(item => item.Key == occurrenceKey))
                    SelectOccurrence(occurrenceKey);
                else if (previousRenderTreeKey is { } treeKey && renderRows.TryGetValue(treeKey, out var selectedRow))
                {
                    RenderedTab.IsSelected = true;
                    selectedRow.IsSelected = true;
                    SelectRenderTreeItem(selectedRow);
                }
                else if (previousStructureIdentity is { } structureIdentity)
                    TrySelectIdentity(request.Section, structureIdentity);
            }
            catch (Exception restoreError)
            {
                originalFailure.Data["SceneRestoreError"] = restoreError.ToString();
                WorkflowDiagnostics.Failure("Scene.Restore", restoreError, request.Section.Descriptor.Identity.Key);
                try { await ReleaseRenderSnapshotAsync(); }
                catch (Exception cleanupError)
                {
                    originalFailure.Data["SceneRestoreCleanupError"] = cleanupError.ToString();
                    WorkflowDiagnostics.Failure("Scene.RestoreCleanup", cleanupError, request.Section.Descriptor.Identity.Key);
                }
                finally
                {
                    try { previousSnapshot?.Dispose(); }
                    catch (Exception cleanupError)
                    {
                        originalFailure.Data["ScenePreviousSnapshotCleanupError"] = cleanupError.ToString();
                        WorkflowDiagnostics.Failure("Scene.PreviousSnapshotCleanup", cleanupError, request.Section.Descriptor.Identity.Key);
                    }
                }
            }
        }
    }

    private TaskCompletionSource<bool>? largeLoadDecision;
    private bool cancellingActivity;

    private async Task<bool> ConfirmLargeDataAsync(string message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (LargeDataConfirmationProvider is { } confirm) return await confirm(message, token).WaitAsync(token);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        largeLoadDecision = decision;
        LargeLoadMessage.Text = message;
        LargeLoadDecisionPanel.Visibility = Visibility.Visible;
        UpdateLoadingProgress();
        try { return await decision.Task.WaitAsync(token); }
        finally
        {
            if (ReferenceEquals(largeLoadDecision, decision))
            { largeLoadDecision = null; LargeLoadDecisionPanel.Visibility = Visibility.Collapsed; UpdateLoadingProgress(); }
        }
    }

    private void ContinueLargeLoad_Click(object sender, RoutedEventArgs e) => largeLoadDecision?.TrySetResult(true);
    private void DeclineLargeLoad_Click(object sender, RoutedEventArgs e) => largeLoadDecision?.TrySetResult(false);

    public async Task ClearRenderingAsync()
    {
        ++renderGeneration;
        renderCancellation?.Cancel();
        try { await renderTask; } catch (OperationCanceledException) { } catch { }
        await ReleaseRenderSnapshotAsync();
        renderLoadingActive = false;
        UpdateLoadingProgress();
        UpdateRegionButtons();
    }

    private async Task ReleaseRenderSnapshotAsync()
    {
        try { await SceneViewport.UnloadAsync(); }
        finally
        {
            var released = renderSnapshot;
            renderSnapshot = null;
            try { released?.Dispose(); }
            finally
            {
                ClearRenderInspector();
                selectedOccurrence = null; renderRows.Clear();
                RenderHierarchy.ItemsSource = null;
                MappedTextures.ItemsSource = null; OpenModelButton.IsEnabled = false;
                RenderSummary.Text = RenderStatus.Text = "";
                lastSuccessfulSceneRenderRequest = null;
                lastSuccessfulRenderBudget = null;
            }
        }
    }

    private void BuildRenderHierarchy(RenderAssetSnapshot source)
    {
        renderRows.Clear();
        foreach (var node in source.Hierarchy) renderRows.Add(node.Key, new(node, (row, visible) =>
        { if (row.Node.OccurrenceKey is { } key) SceneViewport.SetVisibility(key, visible); }));
        var roots = new List<RenderTreeRow>();
        foreach (var row in renderRows.Values)
            if (row.Node.ParentKey is { } parent && renderRows.TryGetValue(parent, out var owner)) owner.Children.Add(row);
            else roots.Add(row);
        Disambiguate(roots);
        foreach (var row in renderRows.Values) Disambiguate(row.Children);
        RenderHierarchy.ItemsSource = roots;

        static void Disambiguate(IEnumerable<RenderTreeRow> siblings)
        {
            foreach (var group in siblings.GroupBy(row => row.Label, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            {
                int index = 0;
                foreach (var row in group.OrderBy(row => row.Node.Key, StringComparer.Ordinal)) row.Label = $"{row.Label} · {++index}";
            }
        }
    }

    public void SelectOccurrence(string? key, bool selectTree = true)
    {
        InvalidateTextureQuery();
        ClearLightingInspector();
        selectedOccurrence = key is null ? null : renderSnapshot?.Occurrences.FirstOrDefault(item => item.Key == key);
        SceneViewport.SetSelection(selectedOccurrence?.Key);
        OpenModelButton.IsEnabled = selectedOccurrence is not null && ModelPreviewProvider is not null;
        ExportSelectionButton.IsEnabled = selectedOccurrence is not null;
        SelectionSummary.Text = selectedOccurrence is null ? "" : "1 selected · Ctrl+click to add";
        if (selectedOccurrence is not { } occurrence || renderSnapshot is not { } source)
        {
            ClearRenderInspector();
            if (selectTree) ClearRenderTreeSelection(RenderHierarchy);
            return;
        }
        var mesh = source.Meshes[occurrence.GeometryKey];
        var materials = (mesh.MaterialsByPrimitive?.Values ?? []).Distinct().Where(source.Materials.ContainsKey)
            .Select(identity => source.Materials[identity]).ToArray();
        SelectedName.Text = renderRows.Values.FirstOrDefault(item => item.Node.OccurrenceKey == occurrence.Key)?.Label ?? SceneNameDisplay.Project(occurrence.Label, "Mesh");
        Footer.Text = "";
        RenderSummary.Text = $"{mesh.Geometry.Primitives.Sum(item => item.Positions.Count):N0} vertices · {mesh.Geometry.Primitives.Sum(item => item.Indices.Count / 3):N0} triangles";
        IdentityText.Text = occurrence.PlacementOrigin == RenderPlacementOrigin.ModelLocal
            ? $"Model-local placement\nMesh anchor {mesh.Identity.Key}\nGroup {mesh.GroupIndex}\nScene entity unavailable\nScene node unavailable"
            : $"Object {occurrence.SceneEntityIdentity?.Key ?? occurrence.Entity.Key}\nNode {occurrence.SceneNodeIdentity?.Key ?? occurrence.Node.Key}\nMesh {mesh.Identity.Key}\nGroup {mesh.GroupIndex}";
        PropertiesText.Text = $"Bounds {Format(occurrence.WorldBounds.Minimum)} – {Format(occurrence.WorldBounds.Maximum)}\n" +
            $"Source visibility {(occurrence.SourceVisibilityKnown ? occurrence.SourceVisible.ToString() : "Unavailable")}\nNative flags {(occurrence.NativeFlags is { } flags ? $"0x{flags:X8}" : "Unavailable")}";
        var row = source.Hierarchy.FirstOrDefault(item => item.OccurrenceKey == occurrence.Key);
        LocalMatrix.Text = row is null ? "" : FormatMatrix(row.Local);
        WorldMatrix.Text = FormatMatrix(occurrence.World);
        Entities.ItemsSource = null;
        QueryTextures.IsEnabled = false; TextureResults.ItemsSource = null;
        MappedTextures.ItemsSource = BuildTextureRows(materials);
        MappedTexturesPanel.Visibility = MappedTextures.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        References.ItemsSource = occurrence.SceneNodeIdentity is { } node
            ? section?.ReferencesByNode.GetValueOrDefault(node)?.Select(item => new SceneObjectReferenceRow(item)).ToArray()
            : null;
        OpenReference.IsEnabled = false;
        Advanced.Text = $"Source name {occurrence.Label}\nOccurrence {occurrence.Key}\nInstance path:\n{string.Join("\n", occurrence.InstancePath.Select(item => item.Key))}\n\n" +
            string.Join("\n", materials.Select(material => $"Material {material.Asset.Identity.Key}\n{material.Asset.Profile}\n" +
                string.Join("\n", material.Asset.Bindings.Select(binding => $"Slot {binding.SlotOrdinal}: {binding.Role}, UV {binding.TextureCoordinateSet}, scale {binding.UvScale}, offset {binding.UvOffset}")) +
                $"\nGraph slots: {string.Join(", ", material.Asset.SurfaceGraph?.TextureSlots ?? [])}\n" +
                $"Inputs: {string.Join(", ", material.Asset.SurfaceGraph?.RequiredInputs ?? [])}\n" +
                string.Join("\n", material.Asset.Diagnostics.Concat(material.Asset.SurfaceGraph?.Diagnostics ?? [])))) +
            "\n\n" + string.Join("\n", materials.SelectMany(material => material.TexturesBySlot.Select(pair => FormatTextureDetails(pair.Key, pair.Value)))) +
            $"\n\n{FormatCoverage(source)}\n" + FormatIssues(source.Issues.Where(issue => issue.OccurrenceKey is null || issue.OccurrenceKey == key)
                .Concat(SceneViewport.RendererIssues.Where(issue => materials.Any(material => material.Asset.Identity == issue.Identity))));
        if (selectTree)
        {
            RenderedTab.IsSelected = true;
            selectingRenderTree = true;
            try
            {
                foreach (var item in renderRows.Values) item.IsSelected = item.Node.OccurrenceKey == key;
                if (!SelectRenderTreeItem(key!)) QueueRenderTreeSelection(key!, renderGeneration, 0);
            }
            finally { selectingRenderTree = false; }
        }
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectMeshes(IReadOnlyList<RenderMeshSelection> selected)
    {
        SelectOccurrence(selected.LastOrDefault()?.OccurrenceKey);
        SceneViewport.SetSelections(selected);
        SelectionSummary.Text = selected.Count == 0 ? "" : $"{selected.Count:N0} selected · Ctrl+click to add";
        ExportSelectionButton.IsEnabled = selected.Count > 0;
        if (selected.LastOrDefault()?.PrimitiveKey is { } primitiveKey && selectedOccurrence is { } occurrence && renderSnapshot is { } source)
        {
            var mesh = source.Meshes[occurrence.GeometryKey];
            var primitive = mesh.Geometry.Primitives.Single(p => p.Key == primitiveKey);
            RenderSummary.Text = $"{primitive.Positions.Count:N0} vertices · {primitive.Indices.Count / 3:N0} triangles";
            var identities = mesh.MaterialsByPrimitive?.TryGetValue(primitiveKey, out var material) == true ? new[] { material } : [];
            MappedTextures.ItemsSource = BuildTextureRows(identities.Where(source.Materials.ContainsKey).Select(id => source.Materials[id]));
        }
    }

    private void ClearRenderInspector()
    {
        SelectionSummary.Text = "";
        ExportSelectionButton.IsEnabled = false;
        InvalidateTextureQuery();
        ClearLightingInspector();
        SelectedName.Text = "Select an object";
        Footer.Text = RenderSummary.Text = IdentityText.Text = PropertiesText.Text = LocalMatrix.Text = WorldMatrix.Text = Advanced.Text = "";
        Entities.ItemsSource = References.ItemsSource = TextureResults.ItemsSource = MappedTextures.ItemsSource = null;
        QueryTextures.IsEnabled = OpenReference.IsEnabled = OpenModelButton.IsEnabled = false;
        MappedTexturesPanel.Visibility = Visibility.Collapsed;
    }

    private bool SelectRenderTreeItem(string occurrenceKey)
    {
        var row = renderRows.Values.FirstOrDefault(item => item.Node.OccurrenceKey == occurrenceKey);
        return row is not null && SelectRenderTreeItem(row);
    }

    private bool SelectRenderTreeItem(RenderTreeRow selectedRow)
    {
        var row = selectedRow;
        var path = new Stack<RenderTreeRow>();
        var visited = new HashSet<string>();
        while (visited.Add(row.Node.Key))
        {
            path.Push(row);
            if (row.Node.ParentKey is not { } parentKey || !renderRows.TryGetValue(parentKey, out var parentRow)) break;
            parentRow.IsExpanded = true;
            row = parentRow;
        }
        ItemsControl parent = RenderHierarchy;
        while (path.TryPop(out var child))
        {
            parent.ApplyTemplate();
            parent.UpdateLayout();
            if (parent.ItemContainerGenerator.ContainerFromItem(child) is not TreeViewItem container) return false;
            container.ApplyTemplate();
            if (path.Count == 0)
            {
                container.BringIntoView();
                container.IsSelected = true;
                parent.UpdateLayout();
                return ReferenceEquals(RenderHierarchy.SelectedItem, selectedRow);
            }
            container.IsExpanded = true; parent = container;
        }
        return false;
    }

    private void QueueRenderTreeSelection(string occurrenceKey, long generation, int attempt)
    {
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (generation != renderGeneration || selectedOccurrence?.Key != occurrenceKey || lifetime.IsCancellationRequested) return;
            selectingRenderTree = true;
            try
            {
                if (!SelectRenderTreeItem(occurrenceKey) && attempt < 3)
                    QueueRenderTreeSelection(occurrenceKey, generation, attempt + 1);
            }
            finally { selectingRenderTree = false; }
        }));
    }

    private static void ClearRenderTreeSelection(ItemsControl parent)
    {
        foreach (var row in parent.Items.OfType<RenderTreeRow>())
        {
            row.IsSelected = false;
            if (parent.ItemContainerGenerator.ContainerFromItem(row) is not TreeViewItem container) continue;
            container.IsSelected = false;
            ClearRenderTreeSelection(container);
        }
    }

    private static string FormatMatrix(Matrix4x4 value) => Format(new SourceTransform(
        [value.M11,value.M12,value.M13,value.M14,value.M21,value.M22,value.M23,value.M24,
         value.M31,value.M32,value.M33,value.M34,value.M41,value.M42,value.M43,value.M44], MatrixLayout.RowMajor, VectorMultiplicationSide.Row));
    private static string FormatCoverage(RenderAssetSnapshot source) =>
        $"Revision {source.Revision.PhysicalSourceStamp}\nIncluded {source.Coverage.IncludedOccurrences}, outside {source.Coverage.OutsideOccurrences}, tested {source.Coverage.TestedOccurrences}\n" +
        $"Parts {source.Coverage.RenderedParts}, containers {source.Coverage.Containers}, instances {source.Coverage.CatalogInstances}\n" +
        $"Memory {source.MemoryBudget.UsedBytes:N0} / {source.MemoryBudget.MaximumBytes:N0} bytes\nIssues {source.Issues.Count}";
    internal static string FormatTextureDetails(int slot, RenderTexture texture) =>
        $"Slot {slot}\nNative texture {texture.NativeIdentity.Key}\nSource {texture.SourceIdentity?.Key}\nCatalog {texture.CatalogIdentity?.Key}\nRGBA SHA-256 {texture.RgbaSha256}" +
        (texture.PreviewWasResized ? "\nPreview resized" : "");
    internal static string FormatIssues(IEnumerable<RenderIssue> issues) => string.Join("\n", issues.Distinct()
        .Select(issue => $"{issue.Code}: {issue.Message}\nResource {issue.Identity.Key}"));

    public static IReadOnlyList<RenderTextureRow> BuildTextureRows(IEnumerable<RenderMaterial> materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        return materials.SelectMany(material => material.TexturesBySlot.Keys.Concat(material.Asset.Bindings.Select(b => b.SlotOrdinal))
            .Concat(material.Asset.SurfaceGraph?.TextureSlots ?? [])
            .Distinct().Order().Select(slot =>
        {
            var binding = material.Asset.Bindings.FirstOrDefault(item => item.SlotOrdinal == slot);
            var graph = material.Asset.SurfaceGraph;
            var color = UsesSlot(graph?.BaseColor, slot);
            var opacity = UsesSlot(graph?.Opacity, slot) || UsesSlot(graph?.AlphaCutoff, slot);
            var usage = color && opacity ? "Colour / Opacity" : color ? "Colour" : opacity ? "Opacity" : null;
            return new RenderTextureRow(material.Asset.Identity, slot, material.TexturesBySlot.GetValueOrDefault(slot), binding, usage)
                { Failure = string.Join("\n", material.Asset.Diagnostics.Concat(material.Asset.SurfaceGraph?.Diagnostics ?? [])) };
        })).ToArray();
    }

    private static bool UsesSlot(MaterialExpression? expression, int slot)
    {
        if (expression is null) return false;
        var pending = new Stack<MaterialExpression>(); pending.Push(expression);
        int visited = 0;
        while (pending.TryPop(out var node) && ++visited <= 4096)
        {
            if (node.Operation == MaterialExpressionOperation.TextureSample && node.TextureSlot == slot) return true;
            foreach (var argument in node.Arguments) pending.Push(argument);
        }
        return false;
    }

    private async void LoadRegion_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadRegionAsync(lifetime.Token); }
        catch (OperationCanceledException) { RenderStatus.Text = "Load cancelled"; }
        catch (Exception error) { RenderStatus.Text = error.Message; }
    }
    private void ShowHidden_Changed(object sender, RoutedEventArgs e)
    {
        if (SceneViewport is null) return;
        SceneViewport.ShowHidden = ShowHidden.IsChecked == true;
        UpdateRenderedCount();
    }
    private void UpdateRenderedCount()
    {
        if (renderSnapshot is not null) RenderStatus.Text = $"{SceneViewport.VisibleOccurrenceCount:N0} objects · {SceneViewport.VisibleMeshCount:N0} meshes" +
            (renderSnapshot.HasIncompleteMaterials ? " · Materials incomplete" : "");
    }
    private void RenderHierarchy_SelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (selectingRenderTree || e.NewValue is not RenderTreeRow row) return;
        if (row.Node.OccurrenceKey is { } key)
        {
            var selected = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? SceneViewport.SelectedMeshes.ToHashSet() : [];
            var item = new RenderMeshSelection(key);
            if (!selected.Remove(item)) selected.Add(item);
            SelectMeshes(selected.ToArray());
        }
        else
        {
            InvalidateTextureQuery();
            ClearLightingInspector();
            selectedOccurrence = null; SceneViewport.SetSelection(null); OpenModelButton.IsEnabled = false;
            SelectedName.Text = row.Label; RenderSummary.Text = ""; Footer.Text = "";
            MappedTextures.ItemsSource = null; TextureResults.ItemsSource = null; QueryTextures.IsEnabled = false;
            MappedTexturesPanel.Visibility = Visibility.Collapsed;
            IdentityText.Text = row.Node.Node.Key; PropertiesText.Text = "";
            LocalMatrix.Text = FormatMatrix(row.Node.Local); WorldMatrix.Text = FormatMatrix(row.Node.World);
            Entities.ItemsSource = null; References.ItemsSource = null; OpenReference.IsEnabled = false;
            Advanced.Text = $"Path {row.Node.Key}\n" + string.Join("\n", row.Node.References.Select(reference => $"{reference.ResourceType}/{reference.Id}"));
        }
    }
    private async void OpenModel_Click(object sender, RoutedEventArgs e)
    {
        if (renderSnapshot is not { } source || selectedOccurrence is not { } occurrence || ModelPreviewProvider is null) return;
        try { await ModelPreviewProvider(source, occurrence, lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { RenderStatus.Text = error.Message; }
    }
    private async void MappedTextures_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MappedTextures.SelectedItem is not RenderTextureRow { Texture: { } texture } row || renderSnapshot is not { } source) return;
        try
        {
            if (TexturePreviewProvider is not null) await TexturePreviewProvider(texture, source.Revision, lifetime.Token);
            else if (texture.SourceIdentity is { } identity && AssetNavigator is not null) await AssetNavigator.OpenAsync(identity, source.Revision, lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { RenderStatus.Text = error.Message; }
    }
}
