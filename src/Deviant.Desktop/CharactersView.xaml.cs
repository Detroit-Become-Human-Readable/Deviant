using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;

namespace Deviant.Desktop;

/// <summary>Searches generic character descriptors and delegates preview ownership to the host.</summary>
public partial class CharactersView : UserControl
{
    private readonly ObservableCollection<CharacterEntryRow> rows = [];
    private ICollectionView? entriesView;
    private bool entryFilteringDisabled;
    private SourceRevision? sourceRevision;
    private CancellationTokenSource? previewCancellation;
    private readonly HashSet<Task> previewTasks = [];
    private Func<AssetDescriptor, CancellationToken, Task>? previewProvider;
    private long previewGeneration;
    private bool clearing;
    private bool changingModelChoices;
    private CharacterModelBindingResult? modelBinding;
    private bool requireModelBinding;
    private bool loadingModelBinding;

    public CharactersView()
    {
        InitializeComponent();
        InitializeEntryColumns();
        var detailStyle = new Style(typeof(ComboBoxItem), ModelChoices.TryFindResource(typeof(ComboBoxItem)) as Style);
        detailStyle.Setters.Add(new Setter(IsEnabledProperty, new Binding(nameof(ModelLodRow.IsAvailable))));
        ModelChoices.ItemContainerStyle = detailStyle;
    }

    /// <summary>The host-owned preview component, such as an existing model or rig view.</summary>
    public ContentControl PreviewContent => PreviewHost;

    /// <summary>The exact selected descriptor, including when duplicate identities are present.</summary>
    private AssetDescriptor? variantSelection;
    private AssetDescriptor? focusedEntrySelection;
    private AssetDescriptor? FocusedEntryDescriptor => (Entries.SelectedItem as CharacterEntryRow)?.Descriptor ?? focusedEntrySelection;
    public AssetDescriptor? SelectedDescriptor => variantSelection ?? FocusedEntryDescriptor;
    internal IReadOnlyList<AssetDescriptor> FocusDescriptors => FocusedEntryDescriptor is not { } focused ? [] :
        variantSelection is { } variant && !ReferenceEquals(variant, focused) ? [focused, variant] : [focused];
    public bool ShowsModels => BrowserMode.SelectedIndex == 1;
    public AssetSort CatalogSort => BrowserSort.SelectedIndex == 0 ? AssetSort.Name : AssetSort.Animations;
    public bool CatalogSortDescending => BrowserSort.SelectedIndex == 1;
    public event EventHandler? BrowserQueryChanged;
    public Func<AssetDescriptor, IReadOnlyList<AssetDescriptor>>? VariantProvider { get; set; }
    public Func<AssetDescriptor, CancellationToken, Task<IReadOnlyList<AssetDescriptor>>>? VariantProviderAsync { get; set; }
    private CancellationTokenSource? variantCancellation;
    private void BrowserModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BrowserHeading is null || Entries is null) return;
        BrowserHeading.Text = ShowsModels ? "Models" : "Characters";
        BrowserQueryChanged?.Invoke(this, EventArgs.Empty);
    }
    private void CharacterVariantChanged(object sender, SelectionChangedEventArgs e)
    {
        if (clearing || CharacterVariants.SelectedItem is not CharacterEntryRow variant) return;
        variantSelection = variant.Descriptor; CancelPreview(); ClearModelBinding(); ShowSelection();
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
    }

    public ResourceIdentity? SelectedIdentity => SelectedDescriptor?.Identity;
    public event EventHandler? SelectedResourceChanged;
    public event EventHandler? SelectedModelDetailChanged;
    public event EventHandler? BindingCancelRequested;
    public event EventHandler? IndexMoreRequested;
    public event EventHandler? RefreshResultsRequested;
    public event EventHandler? IndexCancelRequested;
    public event EventHandler? SearchRequested;
    public event Action<int>? PageRequested;

    public void SetResultPage(long offset, int visible, long total, bool running = false)
    {
        ResultsPaging.Visibility = Visibility.Visible;
        ResultRange.Text = visible == 0 ? $"0 of {total:N0}" : $"{offset + 1:N0}–{offset + visible:N0} of {total:N0}";
        PreviousResults.IsEnabled = !running && offset > 0;
        NextResults.IsEnabled = !running && offset + visible < total;
    }

    private void PreviousResultsClicked(object sender, RoutedEventArgs e) => PageRequested?.Invoke(-1);
    private void NextResultsClicked(object sender, RoutedEventArgs e) => PageRequested?.Invoke(1);
    public string SearchQuery => Search.Text.Trim();
    private bool suppressSearchRequest;
    private bool indexingResults;

    public void SetSearchQuery(string query, bool notify = true)
    {
        suppressSearchRequest = !notify;
        try { Search.Text = query; }
        finally { suppressSearchRequest = false; }
    }

    public void SetIndexingState(bool enabled, bool running)
    {
        IndexMoreButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        IndexMoreButton.IsEnabled = enabled;
        indexingResults = running;
        IndexCancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    private void IndexMoreClicked(object sender, RoutedEventArgs e)
    {
        if (indexingResults) RefreshResultsRequested?.Invoke(this, EventArgs.Empty);
        else IndexMoreRequested?.Invoke(this, EventArgs.Empty);
    }
    private void IndexCancelClicked(object sender, RoutedEventArgs e) => IndexCancelRequested?.Invoke(this, EventArgs.Empty);
    public CharacterModelBindingResult? ModelBinding => modelBinding;
    public CharacterMeshLodBinding? SelectedModelLod => (ModelChoices.SelectedItem as ModelLodRow)?.Binding;

    public void SetModelBinding(CharacterModelBindingResult binding) => SetModelBindings([binding]);

    public void SetModelBindings(IReadOnlyList<CharacterModelBindingResult> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (SelectedDescriptor is not { } selected || bindings.Any(binding => binding.SelectedIdentity != selected.Identity ||
            binding.Revision != selected.Revision || binding.Revision != sourceRevision))
            throw new ArgumentException("The model binding belongs to another selected entry or revision.", nameof(bindings));
        CancelPreview();
        ClearModelBinding();
        requireModelBinding = true;
        loadingModelBinding = false;
        changingModelChoices = true;
        try
        {
            ModelContexts.ItemsSource = bindings.Select(binding => new ModelContextRow(binding)).ToArray();
            ModelContexts.SelectedIndex = bindings.Count == 1 ? 0 : -1;
            ModelContextPanel.Visibility = bindings.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { changingModelChoices = false; }
        if (bindings.Count == 1) ApplyModelBinding(bindings[0]);
        else
        {
            BindingStatus.Text = bindings.Count == 0 ? "Model details unavailable." : "Choose a resource group.";
            PreviewButton.IsEnabled = false;
        }
    }

    public void BeginModelBinding()
    {
        CancelPreview(); ClearModelBinding(); requireModelBinding = true;
        loadingModelBinding = true;
        BindingStatus.Text = "Reading model details…";
        PreviewButton.IsEnabled = false;
        CancelPreviewButton.Visibility = Visibility.Visible;
    }

    public void ResetModelBinding()
    {
        CancelPreview(); ClearModelBinding();
        ShowSelection();
    }

    public void SetModelBindingError(string message, string? diagnostic = null)
    {
        CancelPreview(); ClearModelBinding(); requireModelBinding = true;
        BindingStatus.Text = message; PreviewButton.IsEnabled = false;
        if (diagnostic is not null) Advanced.AppendText("\nDetails error: " + diagnostic);
    }

    private void ModelContextChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingModelChoices || clearing) return;
        CancelPreview();
        CancelExport();
        ClearRig();
        if (ModelContexts.SelectedItem is ModelContextRow context) ApplyModelBinding(context.Binding);
        else
        {
            modelBinding = null;
            changingModelChoices = true;
            try { ModelChoices.ItemsSource = null; ModelChoicesPanel.Visibility = Visibility.Collapsed; }
            finally { changingModelChoices = false; }
            BindingStatus.Text = "Choose a resource group.";
            PreviewButton.IsEnabled = false;
        }
        SelectedModelDetailChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyModelBinding(CharacterModelBindingResult binding)
    {
        modelBinding = binding;
        changingModelChoices = true;
        try
        {
            var choices = binding.Lods.OrderBy(lod => lod.LodOrdinal).Select(lod => new ModelLodRow(lod)).ToArray();
            var firstSupported = Array.FindIndex(choices, row => row.IsAvailable);
            ModelChoices.ItemsSource = choices;
            ModelChoices.IsEnabled = firstSupported >= 0;
            ModelChoices.SelectedIndex = firstSupported >= 0 ? firstSupported : choices.Length > 0 ? 0 : -1;
            ModelChoicesPanel.Visibility = Visibility.Visible;
        }
        finally { changingModelChoices = false; }
        BindingStatus.Text = "";
        UpdateModelChoicePresentation();
    }

    private bool CanPreviewSelection => SelectedDescriptor is not null && PreviewProvider is not null &&
        ((!requireModelBinding && modelBinding is null) || (modelBinding is not null && modelBinding.Diagnostic is null &&
            modelBinding.CatalogContainerResolution.Status == ReferenceStatus.Unique &&
            SelectedModelLod is { Selection: not null, Diagnostic: null, MeshDataResolution.Status: ReferenceStatus.Unique }));

    private void ClearModelBinding()
    {
        CancelExport();
        ClearRig();
        modelBinding = null;
        requireModelBinding = false;
        loadingModelBinding = false;
        changingModelChoices = true;
        try
        {
            ModelChoices.ItemsSource = ModelContexts.ItemsSource = null;
            ModelChoicesPanel.Visibility = ModelContextPanel.Visibility = Visibility.Collapsed;
            BindingStatus.Text = "";
        }
        finally { changingModelChoices = false; }
        RefreshExportAvailability();
        RefreshPoseImportAvailability();
    }

    private void ModelChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingModelChoices || clearing) return;
        CancelPreview();
        CancelExport();
        UpdateModelChoicePresentation();
        SelectedModelDetailChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateModelChoicePresentation()
    {
        RefreshExportAvailability();
        RefreshPoseImportAvailability();
        PreviewButton.IsEnabled = CanPreviewSelection && previewCancellation is not { IsCancellationRequested: false };
        if (SelectedDescriptor is not { } selected || modelBinding is not { } binding) return;
        var detail = SelectedModelLod;
        PreviewStatus.Text = CanPreviewSelection || CanImportPoseSelection ? "" : "This detail level is unavailable.";
        Advanced.Text = Describe(selected, sourceRevision) +
            $"\nCatalog: {binding.CatalogIdentity.Key}\nOwner: {binding.ContainerIdentity.Key}\nBinding: {binding.Diagnostic ?? "Available"}" +
            (detail is null ? "" : $"\nLOD: {detail.LodOrdinal}\nStored threshold: {detail.Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                $"\nMesh: {detail.MeshDataIdentity?.Key}\nGroup: {detail.GroupIndex}\nResolution: {detail.MeshDataResolution.Status}\nDiagnostic: {detail.Diagnostic}");
    }

    public void SelectResource(ResourceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var matches = rows.Where(row => row.Descriptor.Identity == identity).ToArray();
        if (matches.Length == 0) throw new InvalidOperationException("The character entry is unavailable.");
        if (matches.Length != 1) throw new InvalidOperationException("Multiple entries match this item. Choose an entry.");
        SetSearchQuery("", notify: false);
        Entries.SelectedItem = matches[0];
        Entries.ScrollIntoView(matches[0]);
    }

    public void SetStatus(string message) => Status.Text = message;

    public void CancelPreview()
    {
        ++previewGeneration;
        previewCancellation?.Cancel();
        PreviewStatus.Text = "";
        CancelPreviewButton.Visibility = Visibility.Collapsed;
        RefreshPoseImportAvailability();
    }

    public async Task DrainPreviewAsync()
    {
        CancelPreview();
        while (previewTasks.Count > 0)
        {
            var pending = previewTasks.ToArray();
            await Task.WhenAll(pending);
            foreach (var task in pending) previewTasks.Remove(task);
        }
    }

    /// <summary>Asks the host to preview the selected descriptor.</summary>
    public Func<AssetDescriptor, CancellationToken, Task>? PreviewProvider
    {
        get => previewProvider;
        set
        {
            if (!ReferenceEquals(previewProvider, value)) CancelPreview();
            previewProvider = value;
            PreviewButton.IsEnabled = CanPreviewSelection &&
                previewCancellation is not { IsCancellationRequested: false };
            if (modelBinding is not null) UpdateModelChoicePresentation();
        }
    }

    public void SetEntries(IReadOnlyList<AssetDescriptor> entries, SourceRevision revision, bool preserveSearch = false,
        bool preserveSelection = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(revision);
        if (entries.Any(entry => entry.Revision != revision || entry.Identity.IndexContentId != revision.IndexContentId))
            throw new ArgumentException("Character entries belong to another archive revision.", nameof(entries));
        var savedSearch = preserveSearch ? Search.Text : "";
        if (preserveSelection && sourceRevision == revision)
        {
            var focused = FocusedEntryDescriptor;
            var priorSelectedRow = Entries.SelectedItem as CharacterEntryRow;
            var uniqueFocus = focused is not null && entries.Count(entry => entry.Identity == focused.Identity) == 1;
            clearing = true;
            try
            {
                Search.Text = savedSearch;
                focusedEntrySelection = focused;
                var nextRows = new List<CharacterEntryRow>(entries.Count);
                CharacterEntryRow? selectedRow = null;
                foreach (var entry in entries)
                {
                    var matchesFocus = focused is not null && entry.Identity == focused.Identity &&
                        (uniqueFocus || ReferenceEquals(entry, focused));
                    var grouped = preserveSearch && !ShowsModels;
                    var row = matchesFocus && priorSelectedRow is not null && priorSelectedRow.CharacterGroup == grouped
                        ? priorSelectedRow : new CharacterEntryRow(matchesFocus ? focused! : entry, grouped);
                    nextRows.Add(row);
                    if (matchesFocus && selectedRow is null) selectedRow = row;
                }
                for (var index = 0; index < nextRows.Count; index++)
                {
                    var next = nextRows[index];
                    if (index >= rows.Count) { rows.Add(next); continue; }
                    if (ReferenceEquals(rows[index], next)) continue;
                    if (rows[index].Descriptor.Identity != next.Descriptor.Identity)
                    {
                        var found = -1;
                        for (var candidate = index + 1; candidate < rows.Count; candidate++)
                            if (rows[candidate].Descriptor.Identity == next.Descriptor.Identity) { found = candidate; break; }
                        if (found >= 0) rows.Move(found, index);
                        else { rows.Insert(index, next); continue; }
                    }
                    if (!ReferenceEquals(rows[index], next)) rows[index] = next;
                }
                while (rows.Count > nextRows.Count) rows.RemoveAt(rows.Count - 1);
                entriesView = CollectionViewSource.GetDefaultView(rows);
                if (entryFilteringDisabled != preserveSearch)
                {
                    entriesView.Filter = entry => preserveSearch || MatchesSearch(entry);
                    entryFilteringDisabled = preserveSearch;
                }
                Entries.ItemsSource = entriesView;
                Entries.SelectedItem = selectedRow;
                RefreshStatus();
            }
            finally { clearing = false; }
            return;
        }
        Clear();
        clearing = true;
        try { Search.Text = savedSearch; }
        finally { clearing = false; }
        sourceRevision = revision;
        foreach (var entry in entries)
            rows.Add(new(entry, preserveSearch && !ShowsModels));
        entriesView = CollectionViewSource.GetDefaultView(rows);
        entriesView.Filter = entry => preserveSearch || MatchesSearch(entry);
        entryFilteringDisabled = preserveSearch;
        Entries.ItemsSource = entriesView;
        RefreshStatus();
    }

    public void ClearSelection()
    {
        variantCancellation?.Cancel();
        clearing = true;
        try
        {
            Entries.SelectedItem = null;
            variantSelection = focusedEntrySelection = null;
            CharacterVariants.ItemsSource = null;
            VariantsPanel.Visibility = Visibility.Collapsed;
        }
        finally { clearing = false; }
        CancelPreview(); ClearModelBinding(); ShowSelection();
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clears row and result references while leaving the host-owned preview control intact.</summary>
    public void Clear()
    {
        variantCancellation?.Cancel();
        clearing = true;
        try
        {
            ++previewGeneration;
            previewCancellation?.Cancel();
            Entries.ItemsSource = null;
            Entries.SelectedItem = null;
            variantSelection = null;
            focusedEntrySelection = null;
            CharacterVariants.ItemsSource = null;
            VariantsPanel.Visibility = Visibility.Collapsed;
            entriesView = null;
            rows.Clear();
            sourceRevision = null;
            ClearModelBinding();
            Search.Clear();
            SelectedName.Text = "Select an entry";
            ClearNameEvidence();
            PreviewPrompt.Text = "Select an entry";
            Summary.Text = Advanced.Text = PreviewStatus.Text = "";
            Status.Text = "No entries";
            ResultsPaging.Visibility = Visibility.Collapsed;
            PreviewButton.IsEnabled = false;
            CancelPreviewButton.Visibility = Visibility.Collapsed;
        }
        finally { clearing = false; }
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
    }

    private string Query => Search.Text.Trim();

    private bool MatchesSearch(object value)
    {
        if (value is not CharacterEntryRow row) return false;
        var query = Query;
        if (query.Length == 0) return true;
        var descriptor = row.Descriptor;
        if (Contains(descriptor.Label) || Contains(descriptor.Family) || Contains(descriptor.GroupKey) ||
            Contains(descriptor.Reason) || Contains(descriptor.SearchTerms) || Contains(descriptor.LookupKey) ||
            Contains(descriptor.Identity.Key) || Contains(descriptor.Revision.IndexContentId) ||
            Contains(descriptor.Revision.PhysicalSourceStamp) || descriptor.Metadata.Any(pair => Contains(pair.Key) || Contains(pair.Value)))
            return true;
        return descriptor.Names.Any(name => Contains(name.Text) || Contains(name.Origin.ToString()) ||
            Contains(name.Source) || Contains(name.CollisionGroup));

        bool Contains(string? value) => value?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true;
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (clearing) return;
        entriesView?.Refresh();
        RefreshStatus();
        if (!suppressSearchRequest) SearchRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void EntrySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (clearing) return;
        variantCancellation?.Cancel();
        using var own = new CancellationTokenSource();
        variantCancellation = own;
        var selected = (Entries.SelectedItem as CharacterEntryRow)?.Descriptor;
        variantSelection = null;
        focusedEntrySelection = selected;
        IReadOnlyList<AssetDescriptor>? variants = null;
        try
        {
            if (selected is not null && !ShowsModels)
                variants = VariantProviderAsync is { } provider
                    ? await provider(selected, own.Token) : VariantProvider?.Invoke(selected);
            own.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(selected, FocusedEntryDescriptor)) return;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { SetStatus(error.Message); }
        finally { if (ReferenceEquals(variantCancellation, own)) variantCancellation = null; }
        clearing = true;
        try
        {
            CharacterVariants.ItemsSource = variants?.Select(descriptor => new CharacterEntryRow(descriptor)).ToArray();
            var variantIndex = variants is null ? -1 : variants.ToList().FindIndex(item => item.Identity == selected?.Identity);
            if (variantIndex < 0 && variants?.Count > 0) variantIndex = 0;
            CharacterVariants.SelectedIndex = variantIndex;
            VariantsPanel.Visibility = variants?.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (variantIndex >= 0) variantSelection = variants![variantIndex];
        }
        finally { clearing = false; }
        CancelPreview();
        ClearModelBinding();
        ShowSelection();
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowSelection()
    {
        PreviewPrompt.Text = SelectedDescriptor is null ? "Select an entry" : "Choose Preview";
        if (SelectedDescriptor is not { } descriptor)
        {
            SelectedName.Text = "Select an entry";
            ClearNameEvidence();
            Summary.Text = Advanced.Text = "";
            PreviewStatus.Text = "";
            PreviewButton.IsEnabled = false;
            return;
        }

        SelectedName.Text = string.IsNullOrWhiteSpace(descriptor.Label) ? descriptor.Family : descriptor.Label;
        ShowNameEvidence(descriptor);
        Summary.Text = $"Model · {descriptor.Availability}" +
            (string.IsNullOrWhiteSpace(descriptor.Reason) ? "" : $"\n{descriptor.Reason}");
        Advanced.Text = Describe(descriptor, sourceRevision);
        PreviewStatus.Text = "";
        PreviewButton.IsEnabled = CanPreviewSelection;
    }

    private static string Describe(AssetDescriptor descriptor, SourceRevision? currentRevision)
    {
        var builder = new StringBuilder();
        builder.Append("Identity: ").AppendLine(descriptor.Identity.Key);
        builder.Append("Descriptor revision: ").Append(descriptor.Revision.IndexContentId)
            .Append(" · ").AppendLine(descriptor.Revision.PhysicalSourceStamp);
        if (currentRevision is { } revision)
            builder.Append("Source revision: ").Append(revision.IndexContentId).Append(" · ").AppendLine(revision.PhysicalSourceStamp);
        builder.Append("Family: ").AppendLine(descriptor.Family);
        if (descriptor.GroupKey is not null) builder.Append("Group: ").AppendLine(descriptor.GroupKey);
        builder.Append("Availability: ").AppendLine(descriptor.Availability.ToString());
        builder.Append("Completeness: ").AppendLine(descriptor.Completeness.ToString());
        builder.Append("Capabilities: ").AppendLine(descriptor.Capabilities.ToString());
        if (descriptor.Reason is not null) builder.Append("Reason: ").AppendLine(descriptor.Reason);
        builder.Append("Names: ").AppendLine(descriptor.Names.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var name in descriptor.Names)
        {
            builder.Append("  ").Append(name.Text).Append(" · ").Append(name.Origin).Append(" · ").Append(name.Source);
            if (name.CollisionGroup is not null) builder.Append(" · collision ").Append(name.CollisionGroup);
            builder.AppendLine();
        }
        foreach (var item in descriptor.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append(item.Key).Append(": ").AppendLine(item.Value);
        return builder.ToString().TrimEnd();
    }

    private void RefreshStatus()
    {
        var total = rows.Count;
        var visible = entriesView?.Cast<object>().Count() ?? 0;
        Status.Text = visible == total
            ? $"{total:N0} entries"
            : $"{visible:N0} of {total:N0} entries";
    }

    private void PreviewClicked(object sender, RoutedEventArgs e) => _ = PreviewSelectedAsync();
    private void CancelPreviewClicked(object sender, RoutedEventArgs e)
    {
        if (loadingModelBinding) BindingCancelRequested?.Invoke(this, EventArgs.Empty);
        CancelPreview();
    }

    private void EntriesDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Entries.SelectedItem is CharacterEntryRow) _ = PreviewSelectedAsync();
    }

    private void EntriesPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearSelection();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter || SelectedDescriptor is null || PreviewProvider is null) return;
        e.Handled = true;
        _ = PreviewSelectedAsync();
    }

    private Task PreviewSelectedAsync(CancellationToken token = default) =>
        PreviewSelectedWithProviderAsync(token, null);

    private Task PreviewSelectedWithProviderAsync(CancellationToken token,
        Func<AssetDescriptor, CancellationToken, Task>? providerOverride)
    {
        var task = PreviewSelectedCoreAsync(token, providerOverride);
        previewTasks.Add(task);
        _ = ObservePreviewAsync(task);
        return task;
    }

    private async Task ObservePreviewAsync(Task task)
    {
        try { await task; }
        finally { previewTasks.Remove(task); }
    }

    private async Task PreviewSelectedCoreAsync(CancellationToken token,
        Func<AssetDescriptor, CancellationToken, Task>? providerOverride)
    {
        if ((providerOverride is null ? !CanPreviewSelection : !CanImportPoseSelection) ||
            SelectedDescriptor is not { } descriptor || (providerOverride ?? PreviewProvider) is not { } provider) return;
        previewCancellation?.Cancel();
        var own = CancellationTokenSource.CreateLinkedTokenSource(token);
        previewCancellation = own;
        var generation = ++previewGeneration;
        PreviewButton.IsEnabled = false;
        CancelPreviewButton.Visibility = Visibility.Visible;
        PreviewStatus.Text = "Preparing preview…";
        RefreshPoseImportAvailability();
        try
        {
            await provider(descriptor, own.Token);
            if (generation == previewGeneration && ReferenceEquals(descriptor, SelectedDescriptor))
                PreviewStatus.Text = "Preview ready";
        }
        catch (OperationCanceledException)
        {
            if (generation == previewGeneration) PreviewStatus.Text = "";
        }
        catch (Exception error)
        {
            if (generation == previewGeneration)
            {
                PreviewStatus.Text = error switch
                {
                    InvalidDataException when error.Message.StartsWith("Native", StringComparison.Ordinal) =>
                        error.Message.Contains("Unsupported", StringComparison.Ordinal) ? "Unsupported model layout." : "Unable to read model data.",
                    ArchiveException { Code: "AssetMemoryLimit" } => "This model exceeds the memory limit. Choose a lower detail level.",
                    _ => error.Message
                };
                Advanced.AppendText("\nPreview error: " + error);
            }
        }
        finally
        {
            if (ReferenceEquals(previewCancellation, own))
            {
                previewCancellation = null;
                CancelPreviewButton.Visibility = Visibility.Collapsed;
                PreviewButton.IsEnabled = CanPreviewSelection;
                RefreshPoseImportAvailability();
            }
            own.Dispose();
        }
    }

    private sealed record CharacterEntryRow(AssetDescriptor Descriptor, bool CharacterGroup = false)
    {
        public string Label => CharacterGroup ? Descriptor.Metadata.GetValueOrDefault("character.name") ?? Descriptor.Label :
            Descriptor.Label == "Model" ? "Model " + Descriptor.Identity.Ordinal.ToString(CultureInfo.InvariantCulture) +
                (Descriptor.Metadata.GetValueOrDefault("NativeId") is { Length: > 0 } nativeId ? ":" + nativeId : "") :
            string.IsNullOrWhiteSpace(Descriptor.Label) ? Descriptor.Family : Descriptor.Label;
        public string Availability => Descriptor.Metadata.ContainsKey("character.candidate-key") &&
            Descriptor.Metadata.GetValueOrDefault("model.kind") != "character" ? "Check details" : Descriptor.Availability.ToString();
        public string AnimationCount => Descriptor.Metadata.GetValueOrDefault("model.animation-state") switch
        {
            "complete" => Descriptor.Metadata.GetValueOrDefault("model.animation-count") ?? "0",
            "partial" => (Descriptor.Metadata.GetValueOrDefault("model.animation-count") ?? "0") + "+",
            _ => "—"
        };
    }

    private sealed record ModelLodRow(CharacterMeshLodBinding Binding)
    {
        public bool IsAvailable => Binding.Selection is not null && Binding.Diagnostic is null &&
            Binding.MeshDataResolution.Status == ReferenceStatus.Unique;
        public string Label => $"LOD {Binding.LodOrdinal}" +
            (string.IsNullOrWhiteSpace(Binding.StoredMeshName) ? "" : $" · {Binding.StoredMeshName}") +
            (Binding.Selection is not null && Binding.Diagnostic is null ? "" :
                Binding.MeshDataResolution.Status == ReferenceStatus.Ambiguous ? " · Ambiguous" :
                Binding.MeshDataResolution.Status == ReferenceStatus.Missing ? " · Missing" : " · Unavailable");
    }

    private sealed record ModelContextRow(CharacterModelBindingResult Binding)
    {
        public string Label => $"Group {Binding.CatalogIdentity.Ordinal + 1:N0}";
    }
}
