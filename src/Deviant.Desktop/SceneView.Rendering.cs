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

public sealed record RenderTextureRow(ResourceIdentity Material, int SlotOrdinal, RenderTexture Texture,
    MaterialTextureBinding? Binding = null, string? Usage = null)
{
    public RenderTextureRow(ResourceIdentity material, MaterialTextureBinding binding, RenderTexture texture)
        : this(material, binding.SlotOrdinal, texture, binding) { }
    public string Label => $"{Usage ?? RoleLabel(Binding?.Role)} · {Texture.Surface.Width} × {Texture.Surface.Height}";
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
    private bool selectingRenderTree;
    private bool updatingBranches;
    private readonly Dictionary<string, RenderTreeRow> renderRows = new(StringComparer.Ordinal);

    public Func<SceneRenderRequest, IProgress<RenderLoadProgress>?, CancellationToken, Task<RenderAssetSnapshot>>? RenderProvider { get; set; }
    public Func<string, CancellationToken, Task<bool>>? LargeDataConfirmationProvider { get; set; }
    public Func<RenderAssetSnapshot, RenderOccurrence, CancellationToken, Task>? ModelPreviewProvider { get; set; }
    public Func<RenderTexture, SourceRevision, CancellationToken, Task>? TexturePreviewProvider { get; set; }
    public RenderAssetSnapshot? CurrentRenderSnapshot => renderSnapshot;
    public RenderOccurrence? SelectedOccurrence => selectedOccurrence;
    public AssetViewport Viewport => SceneViewport;
    public ResourceIdentity? SelectedBranchIdentity => (BranchChoice.SelectedItem as SceneBranchChoice)?.Instance.Identity;
    public GeometryBounds Region => ReadRegion();

    private void InitializeRendering()
    {
        SceneViewport.SelectionChanged += (_, key) => SelectOccurrence(key, selectTree: true);
        SceneViewport.RenderFailed += (_, error) => { RenderStatus.Text = "Unable to render scene"; Advanced.Text = error.ToString(); };
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

    public async Task LoadRegionAsync(CancellationToken token = default, SceneRenderScope scope = SceneRenderScope.Region,
        int maximumPreviewDimension = 1024)
    {
        var loaded = section ?? throw new InvalidOperationException("Open a scene first.");
        var branch = BranchChoice.SelectedItem as SceneBranchChoice ?? throw new InvalidOperationException("Select a scene branch.");
        var provider = RenderProvider ?? throw new InvalidOperationException("Scene rendering is unavailable.");
        var region = scope == SceneRenderScope.Scene ? renderSnapshot?.Region ?? new GeometryBounds(Vector3.Zero, Vector3.One) : ReadRegion();
        renderCancellation?.Cancel();
        var previous = renderTask;
        var generation = ++renderGeneration;
        try { await previous; } catch (OperationCanceledException) { } catch { }
        if (generation != renderGeneration) return;
        var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        renderCancellation = own;
        var task = LoadRegionCoreAsync(new(loaded, branch.Instance.Identity, region, scope)
            { MaximumPreviewDimension = maximumPreviewDimension, AllowLargeData = HasLargeDataApproval(loaded) }, provider, own, generation);
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
        CancellationTokenSource cancellation, long generation)
    {
        RenderAssetSnapshot? candidate = null;
        var previousSnapshot = renderSnapshot;
        var viewportTouched = false;
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
            var progress = new Progress<RenderLoadProgress>(value =>
            {
                if (generation == renderGeneration && !cancellation.IsCancellationRequested)
                    UpdateRenderLoadingProgress(value);
            });
            try { await LoadCandidateAsync(); }
            catch (ArchiveException error) when (IsLargeWorkloadLimit(error.Code) && !request.AllowLargeData)
            {
                candidate?.Dispose();
                candidate = null;
                if (viewportTouched) await SceneViewport.UnloadAsync();
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
                await ReleaseRenderSnapshotAsync();
                UpdateRenderLoadingProgress(new("Reading scene", 0, 0, 0));
                request = request with { AllowLargeData = true };
                await LoadCandidateAsync();
            }
            if (request.Scope == SceneRenderScope.Scene && previousCamera is not null && SceneViewport.Viewport.Camera is HelixToolkit.Wpf.SharpDX.PerspectiveCamera camera)
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

            async Task LoadCandidateAsync()
            {
                candidate = await provider(request, progress, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (generation != renderGeneration || !ReferenceEquals(section, request.Section))
                    throw new OperationCanceledException(cancellation.Token);
                viewportTouched = true;
                await SceneViewport.LoadSnapshotAsync(candidate, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (generation != renderGeneration || !ReferenceEquals(section, request.Section))
                    throw new OperationCanceledException(cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            if (viewportTouched) await RestorePreviousSnapshotAsync();
            SetActivityTerminal("Cancelled");
            throw;
        }
        catch
        {
            if (viewportTouched) await RestorePreviousSnapshotAsync();
            SetActivityTerminal("Failed");
            throw;
        }
        finally
        {
            candidate?.Dispose();
            if (generation == renderGeneration)
            {
                renderLoadingActive = false;
                UpdateLoadingProgress();
                UpdateRegionButtons();
            }
        }

        async Task RestorePreviousSnapshotAsync()
        {
            await SceneViewport.UnloadAsync();
            candidate?.Dispose();
            candidate = null;
            if (previousSnapshot is null || !ReferenceEquals(renderSnapshot, previousSnapshot)) return;
            try
            {
                await SceneViewport.LoadSnapshotAsync(previousSnapshot, CancellationToken.None);
                if (previousCamera is not null && SceneViewport.Viewport.Camera is HelixToolkit.Wpf.SharpDX.PerspectiveCamera camera)
                {
                    camera.Position = previousCamera.Position;
                    camera.LookDirection = previousCamera.LookDirection;
                    camera.UpDirection = previousCamera.UpDirection;
                    camera.NearPlaneDistance = previousCamera.NearPlaneDistance;
                }
            }
            catch { await SceneViewport.UnloadAsync(); }
        }
    }

    private Task<bool> ConfirmLargeDataAsync(string message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (LargeDataConfirmationProvider is { } confirm) return confirm(message, token);
        var owner = Window.GetWindow(this);
        var result = owner is null
            ? MessageBox.Show(message, "Large scene", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : MessageBox.Show(owner, message, "Large scene", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        token.ThrowIfCancellationRequested();
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

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
        await SceneViewport.UnloadAsync();
        if (renderSnapshot is not null || selectedOccurrence is not null) ClearRenderInspector();
        renderSnapshot?.Dispose(); renderSnapshot = null;
        selectedOccurrence = null; renderRows.Clear();
        RenderHierarchy.ItemsSource = null;
        MappedTextures.ItemsSource = null; OpenModelButton.IsEnabled = false;
        RenderSummary.Text = RenderStatus.Text = "";
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
        selectedOccurrence = key is null ? null : renderSnapshot?.Occurrences.FirstOrDefault(item => item.Key == key);
        SceneViewport.SetSelection(selectedOccurrence?.Key);
        OpenModelButton.IsEnabled = selectedOccurrence is not null && ModelPreviewProvider is not null;
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
        IdentityText.Text = $"Object {occurrence.Entity.Key}\nNode {occurrence.Node.Key}\nMesh {mesh.Identity.Key}\nGroup {mesh.GroupIndex}";
        PropertiesText.Text = $"Bounds {Format(occurrence.WorldBounds.Minimum)} – {Format(occurrence.WorldBounds.Maximum)}\n" +
            $"Source visibility {occurrence.SourceVisible}\nNative flags {(occurrence.NativeFlags is { } flags ? $"0x{flags:X8}" : "Unavailable")}";
        var row = source.Hierarchy.FirstOrDefault(item => item.OccurrenceKey == occurrence.Key);
        LocalMatrix.Text = row is null ? "" : FormatMatrix(row.Local);
        WorldMatrix.Text = FormatMatrix(occurrence.World);
        Entities.ItemsSource = null;
        QueryTextures.IsEnabled = false; TextureResults.ItemsSource = null;
        MappedTextures.ItemsSource = BuildTextureRows(materials);
        References.ItemsSource = section?.ReferencesByNode.GetValueOrDefault(occurrence.Node)?.Select(item => new SceneObjectReferenceRow(item)).ToArray();
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

    private void ClearRenderInspector()
    {
        SelectedName.Text = "Select an object";
        Footer.Text = RenderSummary.Text = IdentityText.Text = PropertiesText.Text = LocalMatrix.Text = WorldMatrix.Text = Advanced.Text = "";
        Entities.ItemsSource = References.ItemsSource = TextureResults.ItemsSource = MappedTextures.ItemsSource = null;
        QueryTextures.IsEnabled = OpenReference.IsEnabled = OpenModelButton.IsEnabled = false;
    }

    private bool SelectRenderTreeItem(string occurrenceKey)
    {
        var row = renderRows.Values.FirstOrDefault(item => item.Node.OccurrenceKey == occurrenceKey);
        if (row is null) return false;
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
                return RenderHierarchy.SelectedItem is RenderTreeRow current && current.Node.OccurrenceKey == occurrenceKey;
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
        return materials.SelectMany(material => material.TexturesBySlot.OrderBy(pair => pair.Key).Select(pair =>
        {
            var binding = material.Asset.Bindings.FirstOrDefault(item => item.SlotOrdinal == pair.Key);
            var graph = material.Asset.SurfaceGraph;
            var color = UsesSlot(graph?.BaseColor, pair.Key);
            var opacity = UsesSlot(graph?.Opacity, pair.Key) || UsesSlot(graph?.AlphaCutoff, pair.Key);
            var usage = color && opacity ? "Colour / Opacity" : color ? "Colour" : opacity ? "Opacity" : null;
            return new RenderTextureRow(material.Asset.Identity, pair.Key, pair.Value, binding, usage);
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
        if (renderSnapshot is not null) RenderStatus.Text = $"{SceneViewport.VisibleOccurrenceCount:N0} objects · {renderSnapshot.Meshes.Count:N0} meshes";
    }
    private void RenderHierarchy_SelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (selectingRenderTree || e.NewValue is not RenderTreeRow row) return;
        if (row.Node.OccurrenceKey is { } key) SelectOccurrence(key, selectTree: false);
        else
        {
            selectedOccurrence = null; SceneViewport.SetSelection(null); OpenModelButton.IsEnabled = false;
            SelectedName.Text = row.Label; RenderSummary.Text = ""; Footer.Text = "";
            MappedTextures.ItemsSource = null; TextureResults.ItemsSource = null; QueryTextures.IsEnabled = false;
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
        if (MappedTextures.SelectedItem is not RenderTextureRow row || renderSnapshot is not { } source) return;
        try
        {
            if (TexturePreviewProvider is not null) await TexturePreviewProvider(row.Texture, source.Revision, lifetime.Token);
            else if (row.Texture.SourceIdentity is { } identity && AssetNavigator is not null) await AssetNavigator.OpenAsync(identity, source.Revision, lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { RenderStatus.Text = error.Message; }
    }
}
