using System.Collections.ObjectModel;
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
using Microsoft.Win32;

namespace Deviant.Desktop;

public sealed record SceneCatalogRow(AssetDescriptor Descriptor)
{
    public ResourceIdentity Identity => Descriptor.Identity;
    public string? DisplayLabel { get; init; }
    public string Label => DisplayLabel ?? (Descriptor.Names.Any(name => name.Origin == NameOrigin.UserAlias && name.Text == Descriptor.Label)
        ? Descriptor.Label : SceneNameDisplay.Project(Descriptor.Label, "Scene"));
    public string Status => Descriptor.Availability == AssetAvailability.Available ? "Ready" : "Unavailable";
}

public sealed class SceneTreeRow
{
    public SceneTreeRow(SceneHierarchyNode node, string label)
    {
        Node = node;
        Label = label;
        Status = node.TransformStatus == SceneTransformStatus.Available ? "" : node.TransformStatus.ToString();
    }
    public SceneHierarchyNode Node { get; }
    public ResourceIdentity Identity => Node.Identity;
    public string Label { get; }
    public string Status { get; }
    public ObservableCollection<SceneTreeRow> Children { get; } = [];
}

public sealed record SceneEntityRow(SceneEntityRecord Entity, string Label);
public sealed record SceneObjectReferenceRow(AssetRelationship Relationship)
{
    public string Label => Relationship.Resolution.Status == ReferenceStatus.Unique
        ? Relationship.Label : $"{Relationship.Label} · {Relationship.Resolution.Status}";
}

public partial class SceneView : UserControl, IAsyncDisposable
{
    private const int PageSize = 100;
    private readonly CancellationTokenSource lifetime = new();
    private ISceneCatalogSession? session;
    private SceneLoadedSection? section;
    private CancellationTokenSource? queryCancellation;
    private CancellationTokenSource? loadCancellation;
    private CancellationTokenSource? discoveryCancellation;
    private long queryGeneration;
    private long loadGeneration;
    private long discoveryGeneration;
    private long offset;
    private bool updating;
    private bool selectingTree = false;
    private SceneTreeRow? selectedNode;
    private ResourceIdentity? selectedEntity;
    private Task queryTask = Task.CompletedTask;
    private Task discoveryTask = Task.CompletedTask;
    private Task loadTask = Task.CompletedTask;
    private Task textureTask = Task.CompletedTask;
    private CancellationTokenSource? textureCancellation;
    private long textureGeneration;
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };

    public SceneView()
    {
        InitializeComponent();
        InitializeRendering();
        searchTimer.Tick += async (_, _) => { searchTimer.Stop(); await QueryAsync(); };
        Hierarchy.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (Hierarchy.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated ||
                selectedNode is not { } selected || selectingTree) return;
            var identity = selected.Identity;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (selectedNode?.Identity != identity || selectingTree) return;
                selectingTree = true;
                try { SelectTreeItem(identity); }
                finally { selectingTree = false; }
            }));
        };
    }

    public event EventHandler? SelectedResourceChanged;
    public IAssetNavigator? AssetNavigator { get; set; }
    public Func<ResourceIdentity, long, int, CancellationToken, Task<TextureOwnerQueryResult>>? TextureOwnerQueryProvider { get; set; }
    public ISceneCatalogSession? CurrentSession => session;
    public SceneLoadedSection? CurrentSection => section;
    public ResourceIdentity? SelectedIdentity => selectedOccurrence?.Node ?? selectedNode?.Identity;
    public string? SelectedLabel => selectedOccurrence is not null ? SelectedName.Text : selectedNode?.Label;
    public SourceRevision? LoadedRevision => session?.Revision;
    public IReadOnlyList<SceneCatalogRow> CurrentRows =>
        Rows.ItemsSource is IEnumerable<SceneCatalogRow> rows ? rows.ToArray() : [];

    public async Task LoadAsync(ISceneCatalogSession source, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(session, source))
        {
            await QueryAsync(token);
            return;
        }
        var generation = ++loadGeneration;
        ClearLargeDataApproval();
        await SuspendAsync();
        await ClearRenderingAsync();
        if (session is not null) session.Updated -= Session_Updated;
        session = source;
        session.Updated += Session_Updated;
        section = null;
        ClearRegionChoices();
        sectionLoadingActive = false;
        BranchChoice.ItemsSource = null;
        LoadRegionButton.IsEnabled = false;
        selectedNode = null;
        selectedEntity = null;
        textureCancellation?.Cancel();
        ++textureGeneration;
        TextureResults.ItemsSource = null;
        offset = 0;
        updating = true;
        try { Rows.ItemsSource = null; Hierarchy.ItemsSource = null; ClearInspector(); }
        finally { updating = false; }
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
        if (generation != loadGeneration) return;
        Status.Text = "Reading scenes…";
        await QueryAsync(token);
        UpdateLoadingProgress();
    }

    public async Task DiscoverMoreAsync(CancellationToken token = default)
    {
        var current = session;
        if (current is null) return;
        var generation = ++discoveryGeneration;
        discoveryCancellation?.Cancel();
        discoveryCancellation?.Dispose();
        discoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        var own = discoveryCancellation;
        activityTerminalStage = null;
        batchDiscoveryActive = true;
        UpdateLoadingProgress();
        DiscoverMore.IsEnabled = false;
        try
        {
            Status.Text = "Discovering scenes…";
            discoveryTask = current.DiscoverMoreAsync(own.Token);
            await discoveryTask;
            if (generation == discoveryGeneration && ReferenceEquals(current, session)) await QueryAsync(own.Token);
        }
        catch (OperationCanceledException)
        {
            if (generation == discoveryGeneration) { Status.Text = "Discovery cancelled"; SetActivityTerminal("Cancelled"); }
        }
        catch (Exception error)
        {
            if (generation == discoveryGeneration) { Status.Text = error.Message; SetActivityTerminal("Failed"); }
        }
        finally
        {
            if (ReferenceEquals(discoveryCancellation, own)) discoveryCancellation = null;
            own.Dispose();
            if (generation == discoveryGeneration)
            {
                batchDiscoveryActive = false;
                DiscoverMore.IsEnabled = !automaticDiscoveryActive && current.Progress.HasMore;
                UpdateLoadingProgress();
            }
        }
    }

    public async Task SelectResourceAsync(ResourceIdentity identity, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        _ = session ?? throw new InvalidOperationException("Open Scenes first.");
        var currentSection = section;
        if (currentSection is null || !TrySelectIdentity(currentSection, identity))
        {
            var loaded = await TrackLoadAsync(identity, token);
            if (!TrySelectIdentity(loaded, identity))
            {
                var node = FindEntityNode(loaded, identity);
                if (node is null) throw new InvalidOperationException("The requested scene resource is unavailable.");
                SelectNode(node, identity);
            }
        }
    }

    public async Task SuspendAsync()
    {
        searchTimer.Stop();
        queryCancellation?.Cancel();
        loadCancellation?.Cancel();
        discoveryCancellation?.Cancel();
        automaticDiscoveryCancellation?.Cancel();
        textureCancellation?.Cancel();
        renderCancellation?.Cancel();
        scenePipelineCancellation?.Cancel();
        try { await queryTask; } catch (OperationCanceledException) { } catch { }
        try { await discoveryTask; } catch (OperationCanceledException) { } catch { }
        try { await automaticDiscoveryTask; } catch (OperationCanceledException) { } catch { }
        try { await loadTask; } catch (OperationCanceledException) { } catch { }
        try { await textureTask; } catch (OperationCanceledException) { } catch { }
        try { await renderTask; } catch (OperationCanceledException) { } catch { }
        try { await scenePipelineTask; } catch (OperationCanceledException) { } catch { }
    }

    public async Task UnloadAsync()
    {
        ++loadGeneration;
        ++queryGeneration;
        ++discoveryGeneration;
        await SuspendAsync();
        await ClearRenderingAsync();
        if (session is not null) session.Updated -= Session_Updated;
        session = null;
        section = null;
        ClearLargeDataApproval();
        ClearRegionChoices();
        sectionLoadingActive = batchDiscoveryActive = automaticDiscoveryActive = false;
        UpdateLoadingProgress();
        BranchChoice.ItemsSource = null;
        LoadRegionButton.IsEnabled = false;
        selectedNode = null;
        selectedEntity = null;
        textureCancellation?.Cancel();
        ++textureGeneration;
        TextureResults.ItemsSource = null;
        updating = true;
        try { Rows.ItemsSource = null; Hierarchy.ItemsSource = null; ClearInspector(); }
        finally { updating = false; }
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task QueryAsync(CancellationToken token = default)
    {
        var current = session;
        if (current is null) return;
        queryCancellation?.Cancel();
        queryCancellation?.Dispose();
        queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        var own = queryCancellation;
        var generation = ++queryGeneration;
        queryTask = QueryCoreAsync(current, own, generation);
        try { await queryTask; }
        finally { if (ReferenceEquals(queryCancellation, own)) queryCancellation = null; own.Dispose(); }
    }

    private async Task QueryCoreAsync(ISceneCatalogSession current, CancellationTokenSource own, long generation)
    {
        try
        {
            var page = await current.QueryAsync(Search.Text.Trim(), offset, PageSize, own.Token);
            if (generation != queryGeneration || own.IsCancellationRequested || !ReferenceEquals(current, session)) return;
            var rows = page.Entries.Select(item => new SceneCatalogRow(item)).ToArray();
            var displayLabels = new Dictionary<ResourceIdentity, string>();
            foreach (var group in rows.GroupBy(row => row.Label, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            {
                int number = 0;
                foreach (var row in group.OrderBy(row => row.Identity.Ordinal).ThenBy(row => row.Identity.MemberPath, StringComparer.Ordinal))
                    displayLabels[row.Identity] = $"{row.Label} · {++number}";
            }
            rows = rows.Select(row => displayLabels.TryGetValue(row.Identity, out var label) ? row with { DisplayLabel = label } : row).ToArray();
            updating = true;
            try { Rows.ItemsSource = rows; }
            finally { updating = false; }
            Page.Text = page.Total == 0 ? "0 scenes" : $"{page.Offset + 1:N0}–{Math.Min(page.Offset + rows.Length, page.Total):N0} / {page.Total:N0}";
            Previous.IsEnabled = page.Offset > 0;
            Next.IsEnabled = page.Offset + rows.Length < page.Total;
            var progress = current.Progress;
            Status.Text = progress.IsRunning ? $"Discovering · {progress.Available:N0} available" : $"{page.Total:N0} scenes";
            DiscoverMore.IsEnabled = progress.HasMore || progress.Candidates == 0;
            if (automaticDiscoveryActive || batchDiscoveryActive) DiscoverMore.IsEnabled = false;
            UpdateLoadingProgress();
        }
        catch (OperationCanceledException) { if (generation == queryGeneration) Status.Text = "Search cancelled"; }
        catch (Exception error) { if (generation == queryGeneration) Status.Text = error.Message; }
    }

    private async Task<SceneLoadedSection> TrackLoadAsync(ResourceIdentity identity, CancellationToken token)
    {
        var task = LoadSectionAsync(identity, token);
        loadTask = task;
        try { return await task; }
        finally { if (ReferenceEquals(loadTask, task)) loadTask = Task.CompletedTask; }
    }

    private async Task<SceneLoadedSection> LoadSectionAsync(ResourceIdentity identity, CancellationToken token)
    {
        var current = session ?? throw new InvalidOperationException("Open Scenes first.");
        var generation = ++loadGeneration;
        await ClearRenderingAsync();
        section = null;
        ClearLargeDataApproval();
        ClearRegionChoices();
        BranchChoice.ItemsSource = null;
        LoadRegionButton.IsEnabled = false;
        selectedNode = null;
        selectedEntity = null;
        updating = true;
        try { Hierarchy.ItemsSource = null; ClearInspector(); }
        finally { updating = false; }
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        var own = loadCancellation;
        activityTerminalStage = null;
        sectionLoadingActive = true;
        UpdateLoadingProgress();
        try
        {
            Status.Text = "Reading scene…";
            var loaded = await current.LoadAsync(identity, own.Token);
            own.Token.ThrowIfCancellationRequested();
            if (generation != loadGeneration || !ReferenceEquals(current, session)) throw new OperationCanceledException(own.Token);
            if (!ReferenceEquals(section, loaded)) ClearLargeDataApproval();
            section = loaded;
            BuildHierarchy(loaded);
            BuildBranchChoices(loaded);
            UpdateRegionButtons();
            return loaded;
        }
        catch (OperationCanceledException) { SetActivityTerminal("Cancelled"); throw; }
        catch { SetActivityTerminal("Failed"); throw; }
        finally
        {
            if (ReferenceEquals(loadCancellation, own)) loadCancellation = null;
            own.Dispose();
            if (generation == loadGeneration)
            {
                sectionLoadingActive = false;
                UpdateLoadingProgress();
            }
        }
    }

    private void BuildHierarchy(SceneLoadedSection loaded)
    {
        var document = loaded.Result.Value ?? throw new InvalidOperationException("The scene section has no structure.");
        var byId = document.Hierarchy.Nodes.ToDictionary(item => item.Identity);
        var visited = new HashSet<ResourceIdentity>();
        var roots = new List<SceneTreeRow>();
        foreach (var root in document.Hierarchy.Roots)
            if (byId.TryGetValue(root, out var node)) roots.Add(BuildNode(node));
        foreach (var node in document.Hierarchy.Nodes)
            if (!visited.Contains(node.Identity)) roots.Add(BuildNode(node));
        Hierarchy.ItemsSource = roots;
        var placedEntities = loaded.EntitiesByNode.Values.SelectMany(items => items).Select(item => item.Identity).ToHashSet();
        var unplaced = document.Entities.Count(item => !placedEntities.Contains(item.Identity));
        Footer.Text = unplaced == 0 ? "" : $"{unplaced:N0} entities without supported node placement";

        SceneTreeRow BuildNode(SceneHierarchyNode node)
        {
            if (!visited.Add(node.Identity)) return new(node, LabelFor(node));
            var row = new SceneTreeRow(node, LabelFor(node));
            foreach (var child in node.Children)
                if (byId.TryGetValue(child, out var childNode)) row.Children.Add(BuildNode(childNode));
            return row;
        }
        Status.Text = document.Diagnostics.Count == 0 ? $"{document.Hierarchy.Nodes.Count:N0} nodes" :
            $"{document.Hierarchy.Nodes.Count:N0} nodes · {document.Diagnostics.Count:N0} notices";
    }

    private bool TrySelectIdentity(SceneLoadedSection loaded, ResourceIdentity identity)
    {
        var document = loaded.Result.Value;
        if (document is null) return false;
        var node = document.Hierarchy.Nodes.FirstOrDefault(item => item.Identity == identity);
        if (node is not null) { SelectNode(node, null); return true; }
        var entityNode = FindEntityNode(loaded, identity);
        if (entityNode is not null) { SelectNode(entityNode, identity); return true; }
        if (identity == loaded.Result.Identity)
        {
            var root = document.Hierarchy.Roots.Select(rootId =>
                document.Hierarchy.Nodes.FirstOrDefault(item => item.Identity == rootId)).FirstOrDefault(item => item is not null);
            if (root is not null) SelectNode(root, null);
            return true;
        }
        return false;
    }

    private SceneHierarchyNode? FindEntityNode(SceneLoadedSection loaded, ResourceIdentity identity) =>
        loaded.EntitiesByNode.FirstOrDefault(pair => pair.Value.Any(entity => entity.Identity == identity)).Key is { } node
            ? loaded.Result.Value?.Hierarchy.Nodes.FirstOrDefault(item => item.Identity == node) : null;

    private void SelectNode(SceneHierarchyNode node, ResourceIdentity? associatedEntity)
    {
        SceneTabs.SelectedIndex = 1;
        SelectBranchForNode(node, associatedEntity);
        selectedOccurrence = null;
        SceneViewport.SetSelection(null);
        OpenModelButton.IsEnabled = false;
        MappedTextures.ItemsSource = null;
        RenderSummary.Text = "";
        selectedNode = new SceneTreeRow(node, LabelFor(node));
        selectedEntity = associatedEntity;
        textureCancellation?.Cancel();
        ++textureGeneration;
        TextureResults.ItemsSource = null;
        ApplyInspector(node);
        if (!selectingTree)
        {
            selectingTree = true;
            try { SelectTreeItem(node.Identity); }
            finally { selectingTree = false; }
            var identity = node.Identity;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (selectedNode?.Identity != identity || selectingTree) return;
                selectingTree = true;
                try { SelectTreeItem(identity); }
                finally { selectingTree = false; }
            }));
        }
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool SelectTreeItem(ResourceIdentity identity)
    {
        Hierarchy.UpdateLayout();
        return SelectTreeItem(Hierarchy, identity);
    }

    private static bool SelectTreeItem(ItemsControl parent, ResourceIdentity identity)
    {
        foreach (var item in parent.Items.OfType<SceneTreeRow>())
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) continue;
            if (item.Identity == identity)
            {
                container.IsSelected = true;
                container.BringIntoView();
                return true;
            }
            container.IsExpanded = true;
            container.UpdateLayout();
            if (SelectTreeItem(container, identity)) return true;
        }
        return false;
    }

    private string LabelFor(SceneHierarchyNode node)
    {
        if (section?.NamesByNode.TryGetValue(node.Identity, out var names) == true &&
            names.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Text)) is { } name)
            return name.Origin == NameOrigin.UserAlias ? name.Text : SceneNameDisplay.Project(name.Text, "Transform");
        return "Transform";
    }

    private void ApplyInspector(SceneHierarchyNode node)
    {
        var document = section?.Result.Value;
        if (document is null) return;
        var entities = section!.EntitiesByNode.GetValueOrDefault(node.Identity) ?? [];
        var references = section.ReferencesByNode.GetValueOrDefault(node.Identity) ?? [];
        SelectedName.Text = LabelFor(node);
        Footer.Text = entities.Count == 0 ? "" : $"{entities.Count:N0} associated entities";
        IdentityText.Text = $"Native ID {node.Record.NativeId}\nName: {LabelFor(node)}";
        var parentNode = node.ParentIdentity is { } parentIdentity
            ? document.Hierarchy.Nodes.FirstOrDefault(item => item.Identity == parentIdentity) : null;
        PropertiesText.Text = $"Transform {node.TransformStatus}\nParent {(parentNode is null ? "Root" : $"Node {parentNode.Record.NativeId}")}\nChildren {node.Children.Count:N0}";
        LocalMatrix.Text = Format(node.LocalTransform);
        WorldMatrix.Text = Format(node.WorldTransform);
        Entities.ItemsSource = entities.Select(entity => new SceneEntityRow(entity, $"Entity {entity.NativeType}/{entity.NativeId}")).ToArray();
        QueryTextures.IsEnabled = entities.Count > 0 && TextureOwnerQueryProvider is not null;
        TextureResults.ItemsSource = null;
        References.ItemsSource = references.Select(item => new SceneObjectReferenceRow(item)).ToArray();
        References.SelectedIndex = -1;
        OpenReference.IsEnabled = false;
        Advanced.Text = $"Identity {node.Identity.Key}\nRevision {node.Record.Revision.PhysicalSourceStamp}\n" +
            $"Stored names: {string.Join(", ", section.NamesByNode.GetValueOrDefault(node.Identity)?.Select(item => item.Text) ?? [])}\n" +
            $"Flags 0x{node.Record.Flags:X8}\nPivot {Format(node.Record.Pivot)}\n" +
            $"Parent attachment {node.Record.ParentAttachmentIndex}\nWord172 0x{node.Record.Word172:X8} · Word176 0x{node.Record.Word176:X8}\n" +
            $"Payload {node.Record.PayloadExtent.Offset} + {node.Record.PayloadExtent.Length}\n" +
            string.Join("\n", node.Record.FieldExtents.Select(item => $"{item.Key}: {item.Value.Offset} + {item.Value.Length}")) +
            $"\nAssociated entity: {selectedEntity?.Key ?? "None"}\n" +
            $"Diagnostics: {string.Join(", ", document.Diagnostics.Where(item => item.Identity == node.Identity).Select(item => item.Code))}";
    }

    private void ClearInspector()
    {
        SelectedName.Text = "Select a node";
        IdentityText.Text = PropertiesText.Text = LocalMatrix.Text = WorldMatrix.Text = Advanced.Text = "";
        Entities.ItemsSource = References.ItemsSource = null;
        TextureResults.ItemsSource = null;
        QueryTextures.IsEnabled = false;
        OpenReference.IsEnabled = false;
        Status.Text = "";
        RenderSummary.Text = "";
        MappedTextures.ItemsSource = null;
        OpenModelButton.IsEnabled = false;
        Page.Text = "";
        Previous.IsEnabled = Next.IsEnabled = false;
    }

    private async Task OpenSelectedReferenceAsync()
    {
        if (References.SelectedItem is not SceneObjectReferenceRow row || row.Relationship.NavigationTarget is not { } target ||
            !row.Relationship.Evidence.Verified || row.Relationship.Resolution.Status != ReferenceStatus.Unique)
            return;
        var current = session;
        if (current is null) return;
        try
        {
            var sourceContainer = section?.Result.Value?.SourceContainer;
            if (sourceContainer is not null && target.IndexContentId == sourceContainer.IndexContentId &&
                target.Ordinal == sourceContainer.Ordinal && target.MemberPath is not null)
            {
                await SelectResourceAsync(target);
                return;
            }
            if (AssetNavigator is not null) await AssetNavigator.OpenAsync(target, current.Revision);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void Session_Updated(object? sender, EventArgs e) =>
        _ = Dispatcher.InvokeAsync(async () => { if (ReferenceEquals(sender, session)) await QueryAsync(); });
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (!updating) { offset = 0; searchTimer.Stop(); searchTimer.Start(); } }
    private async void Previous_Click(object sender, RoutedEventArgs e) { if (offset >= PageSize) { offset -= PageSize; await QueryAsync(); } }
    private async void Next_Click(object sender, RoutedEventArgs e) { offset += PageSize; await QueryAsync(); }
    private async void DiscoverMore_Click(object sender, RoutedEventArgs e) => await DiscoverAllAsync();
    private async void ExportMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (session is null || section is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export scene metadata",
            Filter = "JSON (*.json)|*.json",
            FileName = "scene-metadata.json",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { await session.ExportMetadataAsync(section, dialog.FileName, lifetime.Token); Status.Text = "Metadata exported"; }
        catch (OperationCanceledException) { Status.Text = "Export cancelled"; }
        catch (Exception error) { Status.Text = error.Message; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { queryCancellation?.Cancel(); loadCancellation?.Cancel(); discoveryCancellation?.Cancel(); automaticDiscoveryCancellation?.Cancel(); renderCancellation?.Cancel(); scenePipelineCancellation?.Cancel(); session?.CancelDiscovery(); }
    private async void Rows_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Rows.SelectedItem is not SceneCatalogRow row) return;
        try { await LoadSceneAsync(row.Identity, lifetime.Token); }
        catch (OperationCanceledException) { Status.Text = "Scene load cancelled"; }
        catch (Exception error) { Status.Text = error.Message; }
    }
    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (Rows.SelectedItem is SceneCatalogRow row) Status.Text = row.Label; }
    private void Hierarchy_SelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) { if (!selectingTree && e.NewValue is SceneTreeRow row) SelectNode(row.Node, null); }
    private async void References_DoubleClick(object sender, MouseButtonEventArgs e) => await OpenSelectedReferenceAsync();
    private async void OpenReference_Click(object sender, RoutedEventArgs e) => await OpenSelectedReferenceAsync();
    private void References_SelectionChanged(object sender, SelectionChangedEventArgs e) => OpenReference.IsEnabled = References.SelectedItem is SceneObjectReferenceRow row && row.Relationship.NavigationTarget is not null;
    private async void QueryTextures_Click(object sender, RoutedEventArgs e) => await QueryTexturesAsync();
    private async void TextureResults_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TextureResults.SelectedItem is not TextureDiscoveryEntry entry || AssetNavigator is null || session is null) return;
        try { await AssetNavigator.OpenAsync(entry.Identity, session.Revision, lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private async Task QueryTexturesAsync()
    {
        var task = QueryTexturesCoreAsync();
        textureTask = task;
        try { await task; }
        finally { if (ReferenceEquals(textureTask, task)) textureTask = Task.CompletedTask; }
    }

    private async Task QueryTexturesCoreAsync()
    {
        if (selectedNode is null || section is null || TextureOwnerQueryProvider is null) return;
        var entities = section.EntitiesByNode.GetValueOrDefault(selectedNode.Identity) ?? [];
        if (entities.Count == 0) { Status.Text = "No associated entities"; return; }
        textureCancellation?.Cancel();
        textureCancellation?.Dispose();
        textureCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var own = textureCancellation;
        var generation = ++textureGeneration;
        QueryTextures.IsEnabled = false;
        try
        {
            Status.Text = "Querying textures…";
            var pages = new List<TextureOwnerQueryResult>(entities.Count);
            foreach (var entity in entities)
            {
                own.Token.ThrowIfCancellationRequested();
                pages.Add(await TextureOwnerQueryProvider(entity.Identity, 0, 100, own.Token));
            }
            if (generation != textureGeneration || own.IsCancellationRequested || section is null) return;
            var expectedRevision = session?.Revision;
            var allowedOwners = entities.Select(entity => entity.Identity).ToHashSet();
            if (expectedRevision is null || pages.Any(page => page.Revision != expectedRevision ||
                !allowedOwners.Contains(page.Owner)))
                throw new ArchiveException("SourceChanged", "The texture owner belongs to another archive revision.");
            TextureResults.ItemsSource = pages.SelectMany(page => page.Page.Entries).ToArray();
            var total = pages.Sum(page => page.Page.Total);
            Status.Text = pages.Any(page => page.State == TextureOwnerQueryState.Pending)
                ? "Discovering textures…"
                : pages.Any(page => page.DiscoveryIncomplete || page.State == TextureOwnerQueryState.Incomplete)
                    ? $"Textures · {total:N0} · incomplete" : $"Textures · {total:N0}";
        }
        catch (OperationCanceledException) { if (generation == textureGeneration) Status.Text = "Texture query cancelled"; }
        catch (Exception error) { if (generation == textureGeneration) Status.Text = error.Message; }
        finally
        {
            if (ReferenceEquals(textureCancellation, own)) textureCancellation = null;
            own.Dispose();
            QueryTextures.IsEnabled = selectedNode is not null && entities.Count > 0 && TextureOwnerQueryProvider is not null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        textureCancellation?.Cancel();
        searchTimer.Stop();
        await UnloadAsync();
        await SceneViewport.DisposeAsync();
        lifetime.Dispose();
    }

    private static string Format(Vector3 value) =>
        $"({value.X.ToString("G7", CultureInfo.InvariantCulture)}, {value.Y.ToString("G7", CultureInfo.InvariantCulture)}, {value.Z.ToString("G7", CultureInfo.InvariantCulture)})";

    private static string Format(SourceTransform? transform) =>
        transform is null ? "Unavailable" : string.Join("\n", Enumerable.Range(0, 4).Select(row =>
            string.Join("  ", transform.Elements.Skip(row * 4).Take(4).Select(value => value.ToString("G7", CultureInfo.InvariantCulture)))));
}
