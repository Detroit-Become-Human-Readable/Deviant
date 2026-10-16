using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Textures;
using Microsoft.Win32;

namespace Deviant.Desktop;

public sealed class TextureDiscoveryLinkEventArgs(TextureDiscoveryLink link) : EventArgs
{
    public TextureDiscoveryLink Link { get; } = link;
}

public sealed record TextureExplorerCard(TextureDiscoveryEntry Entry)
{
    public ResourceIdentity Identity => Entry.Identity;
    private string SourceName => Entry.NameKind == TextureNameKind.Fallback ? $"Texture {Entry.ResourceId:X8}" : DisplayName(Entry.Label);
    public string Label => Entry.Alias ?? SourceName;
    public string RecoveredName => Entry.Alias is null ? "" : SourceName;
    public string SourceTooltip => Entry.ResourcePath ?? Entry.ResourceName ?? SourceName;
    public string SizeFormat => string.Join(" · ", new[]
    {
        Entry.Width is { } width && Entry.Height is { } height ? $"{width:N0} × {height:N0}" : null,
        Entry.Format,
        Entry.Kind == TextureKind.LodBuffer ? "Mip buffer" : Entry.Kind == TextureKind.Lighting
            ? Entry.IsCubeMap ? "Reflection probe" : "Lighting" : null
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string ContextTooltip => !string.IsNullOrWhiteSpace(Entry.ContextLabel) ? Entry.ContextLabel :
        string.Join(" / ", Entry.Owners.Where(link => link.Verified).Select(link => link.Label).Distinct(StringComparer.Ordinal));
    public string Context => ContextTooltip.Length > 0 ? DisplayContext(ContextTooltip) : Entry.VerifiedRole ?? "";
    public string Inferred => Entry.Alias is not null ? "Alias" : Entry.NameKind == TextureNameKind.InferredDescription ? "Inferred" : "";

    public static string DisplayName(string name) => TextureNameDisplay.Project(name);
    private static string DisplayContext(string value)
    {
        var parts = value.Split(':', 2);
        var name = SceneNameDisplay.Project(DisplayName(parts[0]), parts[0]);
        if (name.StartsWith("Ibl ", StringComparison.OrdinalIgnoreCase)) name = name[4..];
        return parts.Length == 1 ? name : $"{name} · {parts[1].Trim()}";
    }
}

public partial class TextureExplorerView : UserControl, IAsyncDisposable
{
    private const int PageSize = 100;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource workspace = new(), nativeWork = new();
    private CancellationTokenSource thumbnailWork = new();
    private long thumbnailEpoch;
    public static readonly DependencyProperty ThumbnailsEnabledProperty = DependencyProperty.Register(
        nameof(ThumbnailsEnabled), typeof(bool), typeof(TextureExplorerView), new PropertyMetadata(false,
            (owner, _) => ((TextureExplorerView)owner).ThumbnailSettingChanged()));
    public bool ThumbnailsEnabled
    {
        get => (bool)GetValue(ThumbnailsEnabledProperty);
        set => SetValue(ThumbnailsEnabledProperty, value);
    }
    private CancellationTokenSource? queryCancellation, exportCancellation, resourceSelection;
    private readonly SemaphoreSlim nativeGate = new(1, 1);
    private readonly HashSet<Task> operations = [];
    private readonly Dictionary<ResourceIdentity, BitmapSource> nativeThumbnails = [];
    private readonly Dictionary<ResourceIdentity, int> thumbnailBindings = [];
    private readonly Dictionary<ResourceIdentity, string> thumbnailVersions = [];
    private readonly HashSet<ResourceIdentity> thumbnailPending = [], thumbnailQueued = [];
    private readonly DispatcherTimer queryTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer retryTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private ITextureDiscoveryCatalog? discovery;
    private NativeTextureSession? native;
    private CacheManager? cache;
    private Func<NativeTextureSource, CancellationToken, DdsTextureDocument>? documentReader;
    private readonly NativeTextureView nativeView = new();
    private TextureDiscoveryEntry? selected;
    private NativePreviewSelection? nativePreviewSelection;
    private TextureMetadataContext? selectedMetadataContext;
    private bool updatingMetadataChoices;
    private long offset, total, queryVersion, publishedQueryVersion, nativeVersion, selectionVersion, resourceRequestVersion;
    private bool updating, suspended = true, cancelled, selectionSettled, selectedPending, disposed;
    private int selectedBindings, retryCount;
    private string? selectedBindingVersion;
    public TexturePreviewState CurrentPreviewState { get; private set; }
    public TexturePreviewFailure? PreviewFailure { get; private set; }
    private Task previewClear = Task.CompletedTask, queryTask = Task.CompletedTask, exportTask = Task.CompletedTask, selectionTask = Task.CompletedTask;
    private ResourceIdentity? nativeAdvancedIdentity;
    private string nativeAdvanced = "";
    private static long textureDiagnosticSequence;

    public TextureExplorerView()
    {
        InitializeComponent(); ImageHost.Content = nativeView;
        Rows.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => StartVisibleThumbnails()));
        Relationships.ItemsSource = new[] { "Any", "With owners", "Without owners" };
        Relationships.SelectedIndex = 0;
        Kinds.ItemsSource = new[] { "Images", "Lighting", "All" }; Kinds.SelectedIndex = 0;
        Sort.ItemsSource = new[] { "Named first", "Name", "Source order" }; Sort.SelectedIndex = 2;
        queryTimer.Tick += (_, _) =>
        {
            queryTimer.Stop();
            AssetLoadDiagnostics.Counters("TextureBrowser.Ui.DebounceFired", "texture-ui", new Dictionary<string, long> { ["QueryVersion"] = queryVersion });
            queryTask = Track(RefreshAsync(false));
        };
        updateTimer.Tick += (_, _) =>
        {
            updateTimer.Stop();
            if (suspended || cancelled || discovery is null) return;
            if (queryCancellation is not null || !queryTask.IsCompleted || resourceSelection is not null)
            {
                updateTimer.Start();
                return;
            }
            queryTask = Track(RefreshOptionsAndPageAsync(true));
            if (nativePreviewSelection is null && selectedMetadataContext is null && native is { } owner && selected is { } entry && owner.BindingVersion(entry.Identity) != selectedBindingVersion)
                QueueSelection();
            foreach (var card in CurrentResources)
                if (native is { } source && thumbnailVersions.TryGetValue(card.Identity, out var signature) && signature != source.BindingVersion(card.Identity))
                { nativeThumbnails.Remove(card.Identity); thumbnailPending.Add(card.Identity); }
            StartVisibleThumbnails();
        };
        retryTimer.Tick += (_, _) =>
        {
            retryTimer.Stop();
            if (suspended || cancelled) return;
            retryTimer.Interval = TimeSpan.FromMilliseconds(Math.Min(2400, 300 * (1 << Math.Min(retryCount++, 3))));
            if (selectedPending) QueueSelection(); else StartVisibleThumbnails();
        };
    }

    public event EventHandler? SelectedResourceChanged;
    public event EventHandler? PreviewStateChanged;
    public event RoutedEventHandler? OpenDdsRequested;
    private void OpenDds_Click(object sender, RoutedEventArgs e) => OpenDdsRequested?.Invoke(this, e);
    public event EventHandler<TextureDiscoveryLinkEventArgs>? LinkSelected;
    public Action? DiscoveryCancel { get; set; }
    public Func<TextureDiscoveryEntry, string, bool, CancellationToken, Task>? ExportOriginalAsync { get; set; }
    public NativeTextureView NativeImageBrowser => nativeView;
    public NativeTextureSession? NativeSession => native;
    public ResourceIdentity? SelectedIdentity => selected?.Identity;
    public TextureDiscoveryEntry? SelectedEntry => selected;
    public string? SelectedLabel => selected?.Alias ?? selected?.Label;
    public SourceRevision? LoadedRevision => discovery?.Revision ?? native?.Revision;
    public bool HasDiscoveryCatalog => discovery is not null;
    public long SelectionVersion => selectionVersion;
    public long TotalMatches => total;
    public long PublishedQueryVersion => publishedQueryVersion;
    public bool IsQueryPending => queryCancellation is not null || queryTimer.IsEnabled || updateTimer.IsEnabled;
    public IReadOnlyList<TextureExplorerCard> CurrentResources => Rows.ItemsSource as TextureExplorerCard[] ?? [];
    public bool IsSuspended => suspended;

    public async Task LoadAsync(ITextureDiscoveryCatalog catalog, NativeTextureSession textures, CacheManager thumbnailCache,
        Func<NativeTextureSource, CancellationToken, DdsTextureDocument> reader, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(textures);
        ArgumentNullException.ThrowIfNull(thumbnailCache); ArgumentNullException.ThrowIfNull(reader);
        ObjectDisposedException.ThrowIf(disposed, this); token.ThrowIfCancellationRequested();
        if (catalog.Revision != textures.Revision || catalog.Snapshot != textures.Snapshot)
            throw new ArgumentException("The texture sessions belong to different archives.", nameof(textures));
        await ClearAsync(); token.ThrowIfCancellationRequested();
        discovery = catalog; native = textures; cache = thumbnailCache; documentReader = reader;
        catalog.Updated += DiscoveryUpdated; suspended = false; cancelled = false;
        queryTask = Track(RefreshOptionsAndPageAsync(false, token)); await queryTask;
    }

    public async Task LoadNativeSelectionAsync(NativeTextureSession textures, CacheManager thumbnailCache,
        ResourceIdentity sourceIdentity, ResourceIdentity nativeIdentity, ResourceIdentity catalogIdentity,
        AssetMemoryBudget budget, string label, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(textures); ArgumentNullException.ThrowIfNull(thumbnailCache);
        ArgumentNullException.ThrowIfNull(budget); token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (sourceIdentity.IndexContentId != textures.Revision.IndexContentId || nativeIdentity.IndexContentId != textures.Revision.IndexContentId ||
            catalogIdentity.IndexContentId != textures.Revision.IndexContentId)
            throw new ArchiveException("SourceChanged", "The linked texture belongs to another workspace.");
        if (!ReferenceEquals(native, textures)) { await ClearAsync(); token.ThrowIfCancellationRequested(); }
        native = textures; cache = thumbnailCache;
        documentReader = (source, cancellation) => new NativeTextureDecoder().ReadPreview(source, 1024, cancellation).Document;
        suspended = false; cancelled = false;
        resourceSelection?.Cancel(); queryCancellation?.Cancel(); ++queryVersion; queryTimer.Stop(); updateTimer.Stop();
        var row = textures.Snapshot.RequireRecord(sourceIdentity.Ordinal);
        var entry = new TextureDiscoveryEntry(sourceIdentity, row.Id, row.Type, label, TextureNameKind.InferredDescription,
            null, null, null, null, null, null, null, [], [], [], [], [], [], "texture", label)
            { MetadataIdentities = [nativeIdentity], CatalogKeys = [catalogIdentity.Key] };
        nativePreviewSelection = new(sourceIdentity, nativeIdentity, catalogIdentity, budget);
        updating = true;
        try { Rows.ItemsSource = new[] { new TextureExplorerCard(entry) }; Rows.SelectedItem = CurrentResources[0]; }
        finally { updating = false; }
        Empty.Visibility = Visibility.Collapsed; Status.Text = "Opening selected image…"; Page.Text = "Selected image";
        Previous.IsEnabled = Next.IsEnabled = false;
        SetSelected(entry, token); DetailTabs.SelectedIndex = 0;
        OwnerStatus.Text = RelatedStatus.Text = "Relationships not indexed";
        await selectionTask.WaitAsync(token);
        if (nativePreviewSelection?.SourceIdentity == sourceIdentity && CurrentPreviewState == TexturePreviewState.Ready)
            Status.Text = "Selected image";
    }

    public async Task AttachDiscoveryAsync(ITextureDiscoveryCatalog catalog, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(catalog); token.ThrowIfCancellationRequested();
        if (native is not { } owner || catalog.Revision != owner.Revision || !ReferenceEquals(catalog.Snapshot, owner.Snapshot))
            throw new ArgumentException("The texture catalog belongs to another workspace.", nameof(catalog));
        if (disposed || suspended || cancelled) return;
        if (!ReferenceEquals(discovery, catalog))
        {
            if (discovery is { } previous) previous.Updated -= DiscoveryUpdated;
            discovery = catalog; catalog.Updated += DiscoveryUpdated;
        }
        if (selected is { } entry && CurrentPreviewState == TexturePreviewState.Ready)
            _ = Track(LoadSelectionRelationshipEvidenceAsync(catalog, owner, entry.Identity, nativeVersion, nativeWork.Token));
        queryTask = Track(RefreshOptionsAndPageAsync(true, token)); await queryTask;
    }

    private void DiscoveryUpdated(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (sender != discovery || suspended || cancelled || disposed || lifetime.IsCancellationRequested) return;
            if (!updateTimer.IsEnabled) updateTimer.Start();
        });
    }

    private async Task RefreshOptionsAndPageAsync(bool preserveSelection, CancellationToken token = default)
    {
        using var diagnostic = TextureDiagnostic("TextureBrowser.Ui.OptionsAndPage", TextureDiagnosticKey());
        try
        {
            await RefreshAsync(preserveSelection, token, refreshGeneration: offset == 0);
            if (discovery is TextureDiscoverySession) return;
            var before = Query();
            await RefreshOptionsAsync(token);
            if (before != Query()) await RefreshAsync(preserveSelection, token);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RefreshOptionsCoreAsync(long request, CancellationToken token)
    {
        if (discovery is not { } owner) return;
        if (owner is TextureDiscoverySession)
        {
            await RefreshAsync(true, token);
            return;
        }
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspace.Token, nativeWork.Token);
        var options = await Task.Run(() => owner.FilterOptions, own.Token);
        own.Token.ThrowIfCancellationRequested();
        if (request != filterOptionsRequestVersion)
        {
            AssetLoadDiagnostics.Counters("TextureBrowser.Ui.OptionsSuperseded", owner.Revision.IndexContentId,
                new Dictionary<string, long> { ["Request"] = request, ["Current"] = filterOptionsRequestVersion });
            return;
        }
        if (!ReferenceEquals(owner, discovery) || suspended || cancelled) return;
        RenderFilterOptions(options);
    }

    private TextureDiscoveryQuery QueryFromControls()
    {
        var dimension = Dimensions.SelectedItem as TextureDimensionOption;
        return new(Search.Text, dimension?.Width, dimension?.Height,
            Formats.SelectedItem is string format && format != "Any" ? format : null,
            Roles.SelectedItem is string role && role != "Any" ? role : null,
            Relationships.SelectedIndex switch { 1 => true, 2 => false, _ => null },
            (Contexts.SelectedItem as TextureContextOption)?.Key is { Length: > 0 } context ? context : null,
            offset, PageSize, SortByName: Sort.SelectedIndex != 2,
            CatalogKey: (Catalogs.SelectedItem as TextureContextOption)?.Key is { Length: > 0 } catalog ? catalog : null, NamedFirst: Sort.SelectedIndex == 0,
            Kind: Kinds.SelectedIndex switch { 0 => TextureKindFilter.Images, 1 => TextureKindFilter.Lighting, _ => TextureKindFilter.All },
            IncludeLodBuffers: Kinds.SelectedIndex == 2);
    }

    public Task RefreshAsync(CancellationToken token = default) => RefreshAsync(true, token, refreshGeneration: true);
    private async Task RefreshAsync(bool preserveSelection, CancellationToken token = default, bool throwOnError = false,
        bool refreshGeneration = false)
    {
        if (discovery is not { } owner || suspended) return;
        queryCancellation?.Cancel();
        var version = ++queryVersion; var query = Query();
        var diagnosticKey = TextureDiagnosticKey();
        using var diagnostic = TextureDiagnostic("TextureBrowser.Ui.Refresh", diagnosticKey);
        AssetLoadDiagnostics.Counters("TextureBrowser.Ui.Request", diagnosticKey, new Dictionary<string, long>
        { ["QueryVersion"] = version, ["Offset"] = query.Offset, ["Limit"] = query.Limit, ["SearchLength"] = query.Search?.Length ?? 0 });
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspace.Token);
        queryCancellation = own;
        try
        {
            TextureDiscoveryPage result;
            TextureBrowseResponse? response = null;
            using (TextureDiagnostic("TextureBrowser.Ui.QueryAwait", diagnosticKey))
            {
                if (owner is TextureDiscoverySession production)
                {
                    response = await ReadBrowseResponseAsync(production, query, version, own.Token, refreshGeneration);
                    result = response.Page;
                }
                else result = await owner.QueryAsync(query, own.Token);
            }
            own.Token.ThrowIfCancellationRequested();
            if (version != queryVersion || discovery != owner || suspended)
            {
                AssetLoadDiagnostics.Counters("TextureBrowser.Ui.PublicationSkipped", diagnosticKey, new Dictionary<string, long>
                { ["VersionChanged"] = version != queryVersion ? 1 : 0, ["OwnerChanged"] = discovery != owner ? 1 : 0, ["Suspended"] = suspended ? 1 : 0 });
                return;
            }
            using var publicationDiagnostic = TextureDiagnostic("TextureBrowser.Ui.Publish", diagnosticKey);
            if (result.Entries.Count == 0 && result.Total > 0 && offset >= result.Total)
            { offset = (result.Total - 1) / PageSize * PageSize; await RefreshAsync(preserveSelection, token); return; }
            PublishBrowseResult(result, response, version, preserveSelection, diagnosticKey);
        }
        catch (OperationCanceledException)
        { AssetLoadDiagnostics.Counters("TextureBrowser.Ui.Cancelled", diagnosticKey, new Dictionary<string, long> { ["QueryVersion"] = version }); token.ThrowIfCancellationRequested(); }
        catch (Exception error)
        { AssetLoadDiagnostics.Counters("TextureBrowser.Ui.Failed", diagnosticKey, new Dictionary<string, long> { ["QueryVersion"] = version }); if (version == queryVersion) Status.Text = error.Message; if (throwOnError) throw; }
        finally { if (ReferenceEquals(queryCancellation, own)) queryCancellation = null; }
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) => ScheduleQuery();
    private void FilterChanged(object sender, SelectionChangedEventArgs e) => ScheduleQuery();
    private void ScheduleQuery()
    {
        if (updating || discovery is null || suspended) return;
        offset = 0; ++filterInputVersion;
        queryState = new(QueryFromControls(), selected?.Identity);
        queryCancellation?.Cancel(); queryTimer.Stop(); queryTimer.Start();
        AssetLoadDiagnostics.Counters("TextureBrowser.Ui.DebounceScheduled", "texture-ui", new Dictionary<string, long>
        { ["QueryVersion"] = queryVersion, ["SearchLength"] = Search.Text.Length });
    }
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        updating = true;
        try { Search.Text = ""; Dimensions.SelectedIndex = Formats.SelectedIndex = Roles.SelectedIndex = Relationships.SelectedIndex = Contexts.SelectedIndex = Catalogs.SelectedIndex = Kinds.SelectedIndex = 0; Sort.SelectedIndex = 2; }
        finally { updating = false; }
        ScheduleQuery();
    }
    private void Previous_Click(object sender, RoutedEventArgs e) { if (offset > 0) { offset = Math.Max(0, offset - PageSize); queryTask = Track(RefreshAsync(false)); } }
    private void Next_Click(object sender, RoutedEventArgs e) { if (offset + PageSize < total) { offset += PageSize; queryTask = Track(RefreshAsync(false)); } }

    public Task SelectResourceAsync(ResourceIdentity identity, CancellationToken token = default) =>
        SelectResourceCoreAsync(identity, null, token);

    public Task SelectNativeResourceAsync(ResourceIdentity sourceIdentity, ResourceIdentity nativeIdentity,
        ResourceIdentity catalogIdentity, AssetMemoryBudget memoryBudget, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(memoryBudget);
        return SelectResourceCoreAsync(sourceIdentity, new(sourceIdentity, nativeIdentity, catalogIdentity, memoryBudget), token);
    }

    private async Task SelectResourceCoreAsync(ResourceIdentity identity, NativePreviewSelection? exact, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var owner = discovery ?? throw new InvalidOperationException("Open textures first.");
        nativePreviewSelection = exact;
        resourceSelection?.Cancel();
        var request = ++resourceRequestVersion;
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspace.Token);
        resourceSelection = own; queryCancellation?.Cancel(); ++queryVersion; queryTimer.Stop(); updateTimer.Stop();
        try
        {
            var entry = await owner.GetDetailsAsync(identity, own.Token) ?? throw new InvalidOperationException("The texture is unavailable.");
            own.Token.ThrowIfCancellationRequested(); if (owner != discovery || request != resourceRequestVersion) return;
            if (!CurrentResources.Any(card => card.Identity == identity) && owner is TextureDiscoverySession production)
            {
                var query = Query(); var version = queryVersion;
                var browsing = await GetBrowseSessionAsync(production, query, version, own.Token);
                var response = await browsing.LocateAsync(identity, query, own.Token);
                bool reset = response is null;
                var resetQuery = new TextureDiscoveryQuery(Limit: PageSize, SortByName: query.SortByName,
                    NamedFirst: query.NamedFirst, IncludeLodBuffers: true);
                if (reset)
                {
                    browsing = await GetBrowseSessionAsync(production, resetQuery, version, own.Token);
                    response = await browsing.LocateAsync(identity, resetQuery, own.Token);
                }
                own.Token.ThrowIfCancellationRequested();
                if (owner != discovery || request != resourceRequestVersion || version != queryVersion || suspended) return;
                if (response is not null)
                {
                    if (reset)
                    {
                        updating = true;
                        try
                        {
                            Search.Text = ""; Relationships.SelectedIndex = 0; Kinds.SelectedIndex = 2;
                            queryState = new(resetQuery, selected?.Identity); ++filterInputVersion;
                        }
                        finally { updating = false; }
                    }
                    PublishBrowseResult(response.Page, response, version, true, TextureDiagnosticKey());
                }
            }
            else if (!CurrentResources.Any(card => card.Identity == identity))
            {
                var page = await owner.LocateAsync(identity, Query(), own.Token); var reset = false;
                if (page is null) { page = await owner.LocateAsync(identity, new(Limit: PageSize), own.Token); reset = page is not null; }
                own.Token.ThrowIfCancellationRequested(); if (owner != discovery || request != resourceRequestVersion) return;
                if (page is not null)
                {
                    updating = true;
                    try
                    {
                        if (reset)
                        {
                            Search.Text = ""; Dimensions.SelectedIndex = Formats.SelectedIndex = Roles.SelectedIndex = Relationships.SelectedIndex = Contexts.SelectedIndex = Catalogs.SelectedIndex = 0; Kinds.SelectedIndex = 2;
                            queryState = new(QueryFromControls(), selected?.Identity); ++filterInputVersion;
                        }
                        Rows.ItemsSource = page.Entries.Select(item => new TextureExplorerCard(item)).ToArray();
                        offset = page.Offset; total = page.Total;
                        Previous.IsEnabled = offset > 0; Next.IsEnabled = offset + page.Entries.Count < total;
                        Page.Text = $"{offset + 1:N0}–{offset + page.Entries.Count:N0} of {total:N0}"; Status.Text = $"{total:N0} images";
                    }
                    finally { updating = false; }
                }
            }
            updating = true;
            try { Rows.SelectedItem = CurrentResources.FirstOrDefault(card => card.Identity == identity); if (Rows.SelectedItem is not null) Rows.ScrollIntoView(Rows.SelectedItem); }
            finally { updating = false; }
            SetSelected(entry); DetailTabs.SelectedIndex = 0;
        }
        finally { if (ReferenceEquals(resourceSelection, own)) resourceSelection = null; }
    }

    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!updating) SetSelected((Rows.SelectedItem as TextureExplorerCard)?.Entry); }

    private void SetSelected(TextureDiscoveryEntry? entry, CancellationToken token = default)
    {
        queryState = queryState with { SelectedIdentity = entry?.Identity };
        resourceSelection?.Cancel(); ++resourceRequestVersion;
        var changed = selected?.Identity != entry?.Identity;
        selected = entry;
        if (nativePreviewSelection?.SourceIdentity != entry?.Identity) nativePreviewSelection = null;
        if (changed) ++selectionVersion;
        if (changed) selectedMetadataContext = null;
        RefreshMetadataChoices();
        RenderDetails(changed); QueueSelection(token); SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
    }

    private ResourceIdentity[] KnownCatalogs(TextureDiscoveryEntry entry)
    {
        if (native is not { } owner) return [];
        return entry.CatalogKeys.Distinct(StringComparer.Ordinal).Select(key =>
        {
            var parts = key.Split(':');
            if (parts.Length != 3 || parts[0] != owner.Revision.IndexContentId || parts[2].Length != 0 ||
                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) ||
                owner.Snapshot.FindRecord(ordinal) is not { Type: 3, Validity: ResourceValidity.Valid })
                throw new ArchiveException("TextureCatalogSourceMismatch", "A texture catalog belongs to another workspace.");
            return new ResourceIdentity(parts[0], ordinal);
        }).ToArray();
    }

    private void RefreshMetadataChoices()
    {
        TextureMetadataContext[] contexts = selected is { } entry && nativePreviewSelection is null
            ? entry.MetadataContexts.Count > 0 ? entry.MetadataContexts.ToArray() :
                entry.MetadataIdentities.Distinct().Select(identity => new TextureMetadataContext(identity, KnownCatalogs(entry),
                    entry.Width, entry.Height, entry.Format)).ToArray() : [];
        var choices = contexts.GroupBy(context => context.MetadataIdentity).Select((group, index) =>
            new TextureMetadataChoiceRow(group.First() with { CatalogIdentities = group.SelectMany(context => context.CatalogIdentities).Distinct().ToArray() }, index + 1)).ToArray();
        updatingMetadataChoices = true;
        try
        {
            MetadataChoices.ItemsSource = choices;
            var same = choices.SingleOrDefault(choice => choice.Context.MetadataIdentity == selectedMetadataContext?.MetadataIdentity);
            if (same is null && choices.Length == 1) same = choices[0];
            selectedMetadataContext = same?.Context;
            MetadataChoices.SelectedItem = same;
            MetadataChoicePanel.Visibility = choices.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { updatingMetadataChoices = false; }
    }

    private void MetadataChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingMetadataChoices || suspended || disposed) return;
        selectedMetadataContext = (MetadataChoices.SelectedItem as TextureMetadataChoiceRow)?.Context;
        QueueSelection();
    }

    private void RenderDetails(bool replaceAlias)
    {
        if (selected is not { } entry)
        {
            SelectedName.Text = "Select a texture"; SelectedSummary.Text = Names.Text = Provenance.Text = Conflicts.Text = Advanced.Text = "";
            Alias.Text = AliasStatus.Text = ""; OwnerRows.ItemsSource = RelatedRows.ItemsSource = null;
            OwnerStatus.Text = RelatedStatus.Text = ""; ExportOriginal.IsEnabled = SaveAlias.IsEnabled = ClearAlias.IsEnabled = false;
            SelectedInferred.Visibility = ConflictPanel.Visibility = Visibility.Collapsed; return;
        }
        SelectedName.Text = new TextureExplorerCard(entry).Label;
        SelectedSummary.Text = new TextureExplorerCard(entry).SizeFormat;
        SelectedInferred.Text = entry.Alias is not null ? "Alias" : "Inferred";
        SelectedInferred.Visibility = entry.Alias is not null || entry.NameKind == TextureNameKind.InferredDescription ? Visibility.Visible : Visibility.Collapsed;
        if (replaceAlias) { Alias.Text = entry.Alias ?? ""; AliasStatus.Text = ""; }
        SaveAlias.IsEnabled = ClearAlias.IsEnabled = true; ExportOriginal.IsEnabled = exportTask.IsCompleted && ExportOriginalAsync is not null;
        Names.Text = string.Join("\n", new[] { entry.Alias is not null ? $"Source: {entry.Label}" : null, entry.ResourceName is { } name ? $"Name: {name}" : null, entry.ResourcePath is { } path ? $"Path: {path}" : null, entry.VerifiedRole is { } role ? $"Role: {role}" : null }.Where(line => line is not null));
        var owners = entry.Owners.Where(link => link.Verified).ToArray();
        var related = entry.Related.Where(link => link.Verified).ToArray();
        OwnerRows.ItemsSource = owners;
        RelatedRows.ItemsSource = related;
        OwnerStatus.Text = owners.Length == 0 ? "No resolved owners" : $"{owners.Length:N0} owners";
        RelatedStatus.Text = related.Length == 0 ? "No related textures" : $"{related.Length:N0} related textures";
        var candidates = entry.NameCandidates.Select(candidate => $"{candidate.Text} · {NameKindLabel(candidate.Kind)}\n" + string.Join("\n", candidate.Evidence.Select(FormatEvidence)));
        Provenance.Text = string.Join("\n\n", entry.ProvenanceLines.Concat(candidates));
        Conflicts.Text = string.Join("\n", entry.ConflictLines);
        ConflictPanel.Visibility = entry.ConflictLines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var evidence = entry.NameCandidates.SelectMany(candidate => candidate.Evidence).Distinct();
        Advanced.Text = string.Join("\n", entry.AdvancedLines.Concat(evidence.Select(item =>
            $"{item.Reader} {item.ReaderVersion} · {item.Field}\nResource {item.Source.Key}\nSHA-256 {item.SourceSha256}" +
            (item.PhysicalExtent is { } physical ? $"\nSource {physical.RelativeSource}: {physical.Offset} + {physical.Length}" : "") +
            (item.DecodedExtent is { } decoded ? $"\nDecoded extent {decoded.Offset} + {decoded.Length}" : ""))));
        if (nativeAdvancedIdentity == entry.Identity) Advanced.Text += nativeAdvanced;
    }
    private static string NameKindLabel(TextureNameKind kind) => kind switch
    { TextureNameKind.OriginalName => "Original name", TextureNameKind.OriginalPath => "Original path", TextureNameKind.VerifiedOwnerRole => "Owner and role", TextureNameKind.InferredDescription => "Inferred", _ => "Fallback" };
    private static string FormatEvidence(TextureEvidence evidence) => $"{evidence.Reader} {evidence.ReaderVersion} · {evidence.Field}" +
        (string.IsNullOrWhiteSpace(evidence.Consumer) ? "" : $"\n{evidence.Consumer}");

    private void QueueSelection(CancellationToken token = default)
    {
        if (suspended || disposed) return;
        cancelled = false; nativeWork.Cancel(); nativeWork.Dispose(); nativeWork = CancellationTokenSource.CreateLinkedTokenSource(token);
        var version = ++nativeVersion; selectedPending = false; selectionSettled = false;
        CurrentPreviewState = TexturePreviewState.Loading; PreviewFailure = null;
        PreviewStateChanged?.Invoke(this, EventArgs.Empty);
        thumbnailPending.Clear(); thumbnailQueued.Clear(); retryTimer.Stop();
        nativeAdvancedIdentity = null; nativeAdvanced = "";
        ImageHost.Visibility = Visibility.Collapsed; PreviewStatus.Visibility = Visibility.Visible;
        previewClear = Track(nativeView.ClearAsync());
        if (selected is not { } entry || native is not { } owner || discovery is null && nativePreviewSelection is null || documentReader is null)
        { PreviewStatus.Text = "Select a texture"; CurrentPreviewState = TexturePreviewState.Idle; selectionSettled = true; StartVisibleThumbnails(); return; }
        if (nativePreviewSelection is null && entry.MetadataIdentities.Distinct().Count() > 1 && selectedMetadataContext is null)
        {
            PreviewStatus.Text = "Choose a texture record.";
            CurrentPreviewState = TexturePreviewState.Pending;
            PreviewFailure = new("TextureMetadataChoiceRequired", PreviewStatus.Text); selectionSettled = true;
            return;
        }
        nativeThumbnails.Remove(entry.Identity); thumbnailVersions.Remove(entry.Identity);
        foreach (var image in ThumbnailImages(Rows))
            if (image.DataContext is TextureExplorerCard card && card.Identity == entry.Identity) image.Source = null;
        PreviewStatus.Text = "Opening image…";
        selectionTask = Track(LoadSelectionAsync(discovery, owner, entry.Identity, version, nativeWork.Token));
    }

    private async Task LoadSelectionAsync(ITextureDiscoveryCatalog? catalog, NativeTextureSession owner, ResourceIdentity identity, long version, CancellationToken token)
    {
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, workspace.Token, lifetime.Token);
        var entered = false;
        NativeContainerLease? companion = null;
        NativeContainerLease? previewCompanion = null;
        try
        {
            await previewClear.WaitAsync(own.Token);
            await nativeGate.WaitAsync(own.Token); entered = true;
            if (!Current(owner, identity, version)) return;
            NativeTextureReadResult result;
            if (nativePreviewSelection is { } exact && exact.SourceIdentity == identity)
            {
                var legacyMetadata = exact.NativeIdentity.MemberPath?.StartsWith("filetext/", StringComparison.Ordinal) == true;
                if (legacyMetadata)
                {
                    result = await Task.Run(() => owner.ReadPreviewAsync(identity, exact.MemoryBudget, 1024, own.Token,
                        metadataIdentity: exact.NativeIdentity, knownCatalogs: [exact.CatalogIdentity]), own.Token);
                    previewCompanion = result.Source?.CompanionLease;
                }
                else
                {
                    companion = await NativeContainerLease.OpenMetadataAsync(owner.Snapshot,
                        exact.NativeIdentity with { MemberPath = null }, exact.MemoryBudget, own.Token, allowLargeData: true);
                    result = await Task.Run(() => owner.ReadForNativeAsync(exact.NativeIdentity, exact.CatalogIdentity, companion, own.Token,
                        maximumPreviewDimension: 1024, reservePayloadMemory: true), own.Token);
                }
                if (result.Source is { } resolved && (resolved.SelectedIdentity != identity || resolved.NativeTextureIdentity != exact.NativeIdentity ||
                    resolved.CatalogIdentity != exact.CatalogIdentity))
                {
                    if (!legacyMetadata || resolved.SelectedIdentity != identity || resolved.CatalogIdentity != exact.CatalogIdentity ||
                        resolved.Metadata.Identity != exact.NativeIdentity || resolved.NativeTextureIdentity is not null)
                        throw new ArchiveException("TextureSourceMismatch", "The texture source does not match the selected material.");
                }
            }
            else
            {
                var metadataIdentity = selectedMetadataContext?.MetadataIdentity ??
                    (selected?.MetadataIdentities.Count == 1 ? selected.MetadataIdentities[0] : null);
                var knownCatalogs = selectedMetadataContext?.CatalogIdentities ?? (selected is { } entry ? KnownCatalogs(entry) : []);
                result = await Task.Run(() => owner.ReadPreviewAsync(identity, owner.MemoryBudget, 1024, own.Token,
                    metadataIdentity: metadataIdentity, knownCatalogs: knownCatalogs), own.Token);
                previewCompanion = result.Source?.CompanionLease;
            }
            own.Token.ThrowIfCancellationRequested(); if (!Current(owner, identity, version)) return;
            selectedBindings = result.Source?.CatalogBindingCount ?? owner.KnownCatalogCount(identity);
            selectedBindingVersion = owner.BindingVersion(identity);
            selectedPending = result.Pending;
            if (result.Pending) retryTimer.Start();
            if (result.Source is not { } source)
            {
                if (result.FailureCode == "TextureMetadataAmbiguous" && result.CandidateIdentities is { Count: > 1 } candidates && selected is { } entry)
                {
                    selected = entry with { MetadataIdentities = entry.MetadataIdentities.Concat(candidates).Distinct().ToArray() };
                    RefreshMetadataChoices();
                }
                CurrentPreviewState = result.Pending ? TexturePreviewState.Pending : TexturePreviewState.Unavailable;
                PreviewFailure = new(result.FailureCode, result.Reason ?? "Preview unavailable");
                PreviewStatus.Text = PreviewFailure.Message; return;
            }
            var document = await Task.Run(() => documentReader!(source, own.Token), own.Token);
            own.Token.ThrowIfCancellationRequested(); if (!Current(owner, identity, version)) return;
            await nativeView.LoadAsync(owner, source, document, selected?.Label ?? $"Texture {identity.Ordinal}", own.Token);
            own.Token.ThrowIfCancellationRequested(); if (!Current(owner, identity, version)) return;
            ImageHost.Visibility = Visibility.Visible; PreviewStatus.Visibility = Visibility.Collapsed;
            CurrentPreviewState = TexturePreviewState.Ready; PreviewFailure = null;
            nativeAdvancedIdentity = identity;
            nativeAdvanced = $"\nMetadata {source.Metadata.Identity.MemberPath}\nMetadata SHA-256 {source.MetadataSha256}\nDerived DDS SHA-256 {document.ContentSha256}\nMips {document.Info.MipCount}/{source.Metadata.MipSizeCount}";
            Advanced.Text += nativeAdvanced;
            if (nativePreviewSelection is null && owner.BindingVersion(identity) != selectedBindingVersion) { selectedPending = true; retryTimer.Start(); }
            if (catalog is not null) _ = Track(LoadSelectionRelationshipEvidenceAsync(catalog, owner, identity, version, nativeWork.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (Current(owner, identity, version))
            {
                CurrentPreviewState = TexturePreviewState.Failed;
                PreviewFailure = new(error is TextureFormatException format ? format.Code : error is ArchiveException archive ? archive.Code : "TexturePreviewFailed", error.Message);
                PreviewStatus.Text = error.Message; nativeThumbnails.Remove(identity); thumbnailVersions.Remove(identity);
            }
        }
        finally
        {
            if (entered) nativeGate.Release();
            previewCompanion?.Dispose();
            companion?.Dispose();
            if (!suspended && !cancelled && version == nativeVersion && !own.IsCancellationRequested)
            { selectionSettled = true; PreviewStateChanged?.Invoke(this, EventArgs.Empty); StartVisibleThumbnails(); }
        }
    }
    private bool Current(NativeTextureSession owner, ResourceIdentity identity, long version)
        => !suspended && !cancelled && !disposed && native == owner && selected?.Identity == identity && nativeVersion == version;

    private async Task LoadSelectionRelationshipEvidenceAsync(ITextureDiscoveryCatalog catalog,
        NativeTextureSession owner, ResourceIdentity identity, long version, CancellationToken token)
    {
        try
        {
            var details = await Task.Run(() => catalog.GetDetailsAsync(identity, token), token);
            if (details is null || !Current(owner, identity, version)) return;
            selected = details;
            RenderDetails(false);
        }
        catch (OperationCanceledException) { }
    }

    private void Thumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not TextureExplorerCard card) return;
        image.Source = ThumbnailsEnabled ? nativeThumbnails.GetValueOrDefault(card.Identity) : null;
        if (card.Identity == SelectedIdentity && CurrentPreviewState != TexturePreviewState.Ready) image.Source = null;
        if (!suspended && !cancelled && (selected is null || selectionSettled)) QueueThumbnail(card, image);
    }
    private void Thumbnail_ContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Thumbnail_Loaded(sender, new());
    private void StartVisibleThumbnails()
    {
        if (!ThumbnailsEnabled || suspended || cancelled || (selected is not null && !selectionSettled)) return;
        foreach (var image in ThumbnailImages(Rows).OrderByDescending(image => (image.DataContext as TextureExplorerCard)?.Identity == SelectedIdentity))
            if (image.DataContext is TextureExplorerCard card) QueueThumbnail(card, image);
    }
    private static IEnumerable<Image> ThumbnailImages(DependencyObject parent)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is Image { Name: "NativeThumbnail" } image) yield return image;
            foreach (var nested in ThumbnailImages(child)) yield return nested;
        }
    }
    private void QueueThumbnail(TextureExplorerCard card, Image image)
    {
        if (!ThumbnailsEnabled || suspended || cancelled || native is not { } owner || cache is not { } thumbnailCache || documentReader is null) return;
        if (card.Identity == SelectedIdentity && CurrentPreviewState != TexturePreviewState.Ready) { image.Source = null; image.ToolTip = PreviewFailure?.Message; return; }
        if (thumbnailVersions.GetValueOrDefault(card.Identity) == owner.BindingVersion(card.Identity) && nativeThumbnails.TryGetValue(card.Identity, out var bitmap)) { image.Source = bitmap; return; }
        image.Source = null;
        if (!IsInViewport(image)) return;
        if (!thumbnailQueued.Add(card.Identity)) return;
        Track(LoadThumbnailAsync(owner, thumbnailCache, card.Identity, image, nativeVersion, nativeWork.Token));
    }
    private async Task LoadThumbnailAsync(NativeTextureSession owner, CacheManager thumbnailCache, ResourceIdentity identity, Image image, long version, CancellationToken token)
    {
        var epoch = thumbnailEpoch;
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, workspace.Token, lifetime.Token, thumbnailWork.Token);
        var entered = false;
        try
        {
            await nativeGate.WaitAsync(own.Token); entered = true;
            if (!CanPublish(owner, identity, image, version, epoch)) return;
            if (identity == SelectedIdentity && CurrentPreviewState != TexturePreviewState.Ready) return;
            var bindingVersion = owner.BindingVersion(identity);
            if (thumbnailVersions.GetValueOrDefault(identity) == bindingVersion && nativeThumbnails.TryGetValue(identity, out var previous)) { image.Source = previous; return; }
            var currentSource = identity == SelectedIdentity ? nativeView.CurrentSource : null;
            if (currentSource is not null) await Task.Run(() => owner.RevalidateAsync(currentSource, own.Token), own.Token);
            var entry = CurrentResources.FirstOrDefault(card => card.Identity == identity)?.Entry;
            if (entry?.MetadataIdentities.Distinct().Count() > 1) return;
            var metadataIdentity = entry?.MetadataIdentities.Count == 1 ? entry.MetadataIdentities[0] : null;
            var result = currentSource is not null ? new NativeTextureReadResult(currentSource, null) :
                await Task.Run(() => owner.ReadPreviewAsync(identity, owner.MemoryBudget, 1024, own.Token,
                    metadataIdentity: metadataIdentity, knownCatalogs: entry is null ? null : KnownCatalogs(entry)), own.Token);
            var transientCompanion = currentSource is null ? result.Source?.CompanionLease : null;
            try
            {
            own.Token.ThrowIfCancellationRequested(); if (!CanPublish(owner, identity, image, version, epoch)) return;
            thumbnailBindings[identity] = result.Source?.CatalogBindingCount ?? owner.KnownCatalogCount(identity);
            if (result.Pending) { thumbnailPending.Add(identity); retryTimer.Start(); return; }
            thumbnailPending.Remove(identity);
            if (result.Source is not { } source) { image.ToolTip = result.Reason; return; }
            var document = currentSource is not null ? nativeView.ImageBrowser.CurrentDocument! :
                await Task.Run(() => documentReader!(source, own.Token), own.Token);
            var mip = document.MipLevels.FirstOrDefault(level => Math.Max(level.Width, level.Height) <= 128)?.Index ?? document.Info.MipCount - 1;
            var key = CacheManager.Key(owner.Revision.IndexContentId + ":" + owner.Revision.PhysicalSourceStamp,
                NativeTextureSession.SourceVersion(source) + ":" + document.ContentSha256, NativeTextureDecoder.Version + ":discovery-thumbnail-rgb-v2", $"thumbnail:128:mip:{mip}:RGB");
            await using var lease = await thumbnailCache.AcquireAsync(key, async (path, cancellation) =>
            {
                var decoder = new DdsTextureDecoder(128L * 1024 * 1024);
                var surface = await decoder.DecodeMipAsync(document, mip, cancellation);
                if (surface.IsHdr) surface = HdrDisplayTransform.ToSdr(surface, cancellationToken: cancellation);
                var thumbnail = decoder.CreateThumbnail(surface, 128, ignoreAlpha: true);
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                PngTextureWriter.Write(file, thumbnail, thumbnail.IsSrgb, cancellation);
            }, own.Token, ".png");
            BitmapSource bitmap;
            await using (var stream = new FileStream(lease.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            { bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]; bitmap.Freeze(); }
            own.Token.ThrowIfCancellationRequested(); if (!CanPublish(owner, identity, image, version, epoch)) return;
            if (owner.BindingVersion(identity) != bindingVersion)
            { thumbnailPending.Add(identity); retryTimer.Start(); return; }
            thumbnailVersions[identity] = bindingVersion; nativeThumbnails[identity] = bitmap; image.Source = bitmap; image.ToolTip = null;
            }
            finally { transientCompanion?.Dispose(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (CanPublish(owner, identity, image, version, epoch)) image.ToolTip = error.Message; }
        finally { if (entered) nativeGate.Release(); if (version == nativeVersion && epoch == thumbnailEpoch) thumbnailQueued.Remove(identity); }
    }
    private bool CanPublish(NativeTextureSession owner, ResourceIdentity identity, Image image, long version, long epoch)
        => ThumbnailsEnabled && epoch == thumbnailEpoch && !suspended && !cancelled && !disposed && native == owner && version == nativeVersion &&
            CurrentResources.Any(card => card.Identity == identity) && image.DataContext is TextureExplorerCard current && current.Identity == identity;
    private void ThumbnailSettingChanged()
    {
        StopThumbnailWork();
        Rows.ItemsPanel = (ItemsPanelTemplate)Resources[ThumbnailsEnabled ? "TextureThumbnailTiles" : "TextureMetadataRows"];
        if (ThumbnailsEnabled && !disposed)
            _ = Dispatcher.BeginInvoke(new Action(StartVisibleThumbnails), DispatcherPriority.Loaded);
    }
    private void StopThumbnailWork()
    {
        ++thumbnailEpoch; thumbnailWork.Cancel(); thumbnailWork.Dispose(); thumbnailWork = new();
        thumbnailPending.Clear(); thumbnailQueued.Clear(); nativeThumbnails.Clear(); thumbnailBindings.Clear(); thumbnailVersions.Clear();
        foreach (var image in ThumbnailImages(Rows)) { image.Source = null; image.ToolTip = null; }
        if (!selectedPending) retryTimer.Stop();
    }
    private bool IsInViewport(Image image)
    {
        var tile = image.Parent as FrameworkElement ?? image;
        if (!tile.IsVisible || tile.ActualWidth <= 0 || tile.ActualHeight <= 0) return false;
        try
        {
            var bounds = tile.TransformToAncestor(Rows).TransformBounds(new Rect(0, 0, tile.ActualWidth, tile.ActualHeight));
            return bounds.IntersectsWith(new Rect(0, 0, Rows.ActualWidth, Rows.ActualHeight));
        }
        catch (InvalidOperationException) { return false; }
    }

    private async void SaveAlias_Click(object sender, RoutedEventArgs e) => await SaveAliasAsync(Alias.Text);
    private async void ClearAlias_Click(object sender, RoutedEventArgs e) => await SaveAliasAsync(null);
    private async Task SaveAliasAsync(string? alias)
    {
        if (discovery is not { } owner || selected is not { } entry) return;
        var identity = entry.Identity; var version = selectionVersion;
        try
        {
            await owner.SetAliasAsync(identity, alias, workspace.Token);
            var details = await owner.GetDetailsAsync(identity, workspace.Token);
            if (owner != discovery || identity != SelectedIdentity || version != selectionVersion) return;
            if (details is not null) { selected = details; RenderDetails(false); Alias.Text = details.Alias ?? ""; }
            AliasStatus.Text = "Saved"; SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
            await RefreshAsync(true, refreshGeneration: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (identity == SelectedIdentity && version == selectionVersion) AliasStatus.Text = error.Message; }
    }

    private void OpenLink(ListBox rows)
    {
        if (rows.SelectedItem is TextureDiscoveryLink { CanNavigate: true, Verified: true } link)
            LinkSelected?.Invoke(this, new(link));
    }
    private void OpenOwner_Click(object sender, RoutedEventArgs e) => OpenLink(OwnerRows);
    private void OpenRelated_Click(object sender, RoutedEventArgs e) => OpenLink(RelatedRows);
    private void Owner_DoubleClick(object sender, MouseButtonEventArgs e) => OpenLink(OwnerRows);
    private void Related_DoubleClick(object sender, MouseButtonEventArgs e) => OpenLink(RelatedRows);
    private void Owner_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; OpenLink(OwnerRows); } }
    private void Related_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; OpenLink(RelatedRows); } }

    public Task ExportToAsync(string destination, bool overwrite = false, CancellationToken token = default)
    {
        if (!exportTask.IsCompleted) throw new InvalidOperationException("An export is already running.");
        var entry = selected ?? throw new InvalidOperationException("Select a texture first.");
        var exporter = ExportOriginalAsync ?? throw new InvalidOperationException("Original export is unavailable.");
        exportTask = Track(ExportCoreAsync(entry, exporter, destination, overwrite, token)); return exportTask;
    }
    private async Task ExportCoreAsync(TextureDiscoveryEntry entry, Func<TextureDiscoveryEntry, string, bool, CancellationToken, Task> exporter, string destination, bool overwrite, CancellationToken token)
    {
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, workspace.Token, lifetime.Token);
        exportCancellation = own;
        try { await exporter(entry, destination, overwrite, own.Token); }
        finally { if (ReferenceEquals(exportCancellation, own)) exportCancellation = null; }
    }
    private async void ExportOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (selected is not { } entry) return;
        var dialog = new SaveFileDialog { Title = "Export original", Filter = "Binary (*.bin)|*.bin", FileName = entry.ExportStem + ".bin", OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var version = selectionVersion; ExportOriginal.IsEnabled = false;
        try { await ExportToAsync(dialog.FileName, true); if (version == selectionVersion) Status.Text = "Exported"; }
        catch (OperationCanceledException) { if (version == selectionVersion) Status.Text = "Cancelled"; }
        catch (Exception error) { if (version == selectionVersion) Status.Text = error.Message; }
        finally { if (version == selectionVersion) ExportOriginal.IsEnabled = selected is not null && ExportOriginalAsync is not null; }
    }

    private Task Track(Task task) { operations.Add(task); _ = ObserveAsync(task); return task; }
    private async Task ObserveAsync(Task task) { try { await task; } catch { } finally { operations.Remove(task); } }
    private async Task DrainAsync() { while (operations.Count > 0) { var batch = operations.ToArray(); try { await Task.WhenAll(batch); } catch { } foreach (var task in batch) operations.Remove(task); } }
    private void Cancel_Click(object sender, RoutedEventArgs e)
        => CancelWork();
    public void CancelWork()
    {
        CurrentPreviewState = TexturePreviewState.Cancelled; PreviewFailure = null;
        PreviewStateChanged?.Invoke(this, EventArgs.Empty);
        cancelled = true; ++nativeVersion; resourceSelection?.Cancel(); queryCancellation?.Cancel(); exportCancellation?.Cancel(); nativeWork.Cancel(); queryTimer.Stop(); updateTimer.Stop(); retryTimer.Stop();
        StopThumbnailWork();
        selectedPending = false; thumbnailPending.Clear(); thumbnailQueued.Clear(); nativeThumbnails.Clear(); thumbnailVersions.Clear();
        ImageHost.Visibility = Visibility.Collapsed; PreviewStatus.Visibility = Visibility.Visible; PreviewStatus.Text = "Cancelled";
        foreach (var image in ThumbnailImages(Rows)) image.Source = null;
        previewClear = Track(nativeView.ClearAsync()); DiscoveryCancel?.Invoke();
        _ = Track(ReleaseBrowseSessionAsync());
    }
    public async Task SuspendAsync()
    {
        suspended = true; ++nativeVersion; resourceSelection?.Cancel(); queryCancellation?.Cancel(); exportCancellation?.Cancel(); nativeWork.Cancel(); queryTimer.Stop(); updateTimer.Stop(); retryTimer.Stop();
        StopThumbnailWork();
        selectedPending = false; thumbnailPending.Clear(); thumbnailQueued.Clear(); ImageHost.Visibility = Visibility.Collapsed;
        previewClear = Track(nativeView.ClearAsync()); await DrainAsync();
        await ReleaseBrowseSessionAsync();
        nativeThumbnails.Clear(); thumbnailBindings.Clear(); thumbnailVersions.Clear(); foreach (var image in ThumbnailImages(Rows)) image.Source = null;
    }
    public void Resume()
    {
        if (native is null || disposed) return;
        if (!suspended && CurrentPreviewState == TexturePreviewState.Ready) return;
        suspended = false; cancelled = false; retryCount = 0;
        queryTask = Track(RefreshOptionsAndPageAsync(true)); QueueSelection();
    }
    public async Task ClearAsync()
    {
        if (discovery is { } previous) previous.Updated -= DiscoveryUpdated;
        ThumbnailsEnabled = false;
        workspace.Cancel(); await SuspendAsync();
        discovery = null; native = null; cache = null; documentReader = null;
        queryState = TextureBrowserQueryState.Default; availableFilterOptions = null; ++filterInputVersion; ++filterOptionsRequestVersion;
        filterOptionsPublication = Task.CompletedTask;
        PublishedBrowseResponse = null;
        nativePreviewSelection = null;
        selectedMetadataContext = null;
        workspace.Dispose(); workspace = new(); nativeWork.Dispose(); nativeWork = new();
        updating = true;
        try { Rows.ItemsSource = null; selected = null; ++selectionVersion; Search.Text = ""; Dimensions.ItemsSource = Formats.ItemsSource = Roles.ItemsSource = Contexts.ItemsSource = Catalogs.ItemsSource = null; Relationships.SelectedIndex = Kinds.SelectedIndex = 0; Sort.SelectedIndex = 2; }
        finally { updating = false; }
        offset = total = 0; Status.Text = Page.Text = ""; Empty.Visibility = Visibility.Collapsed; RenderDetails(true);
        RefreshMetadataChoices();
        CurrentPreviewState = TexturePreviewState.Idle; PreviewFailure = null; selectedBindingVersion = null;
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        lifetime.Cancel(); await ClearAsync(); disposed = true; await nativeView.DisposeAsync();
        nativeWork.Dispose(); thumbnailWork.Dispose(); workspace.Dispose(); lifetime.Dispose(); nativeGate.Dispose();
    }
    private sealed record NativePreviewSelection(ResourceIdentity SourceIdentity, ResourceIdentity NativeIdentity,
        ResourceIdentity CatalogIdentity, AssetMemoryBudget MemoryBudget);
    private static string TextureDiagnosticKey() => "texture-ui/" + Interlocked.Increment(ref textureDiagnosticSequence).ToString(CultureInfo.InvariantCulture);
    private static IDisposable? TextureDiagnostic(string stage, string key) =>
        AssetLoadDiagnostics.CountersEnabled || AssetLoadDiagnostics.Listener.IsEnabled(AssetLoadDiagnostics.MeasurementName)
            ? new TextureDiagnosticPhase(stage, key) : null;
    private sealed class TextureDiagnosticPhase : IDisposable
    {
        private readonly string stage, key;
        private readonly IDisposable? timing;
        private int disposed;
        public TextureDiagnosticPhase(string stage, string key)
        {
            this.stage = stage; this.key = key;
            AssetLoadDiagnostics.Counters(stage, key, new Dictionary<string, long> { ["Active"] = 1, ["ThreadId"] = Environment.CurrentManagedThreadId });
            timing = AssetLoadDiagnostics.Measure(stage, key);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            timing?.Dispose();
            AssetLoadDiagnostics.Counters(stage, key, new Dictionary<string, long> { ["Active"] = 0, ["ThreadId"] = Environment.CurrentManagedThreadId });
        }
    }

    private sealed record TextureMetadataChoiceRow(TextureMetadataContext Context, int Number)
    {
        public string Label => string.Join(" · ", new[] { Context.Label ?? $"Texture record {Number}",
            Context.Width is { } width && Context.Height is { } height ? $"{width:N0} × {height:N0}" : null, Context.Format }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }
}
