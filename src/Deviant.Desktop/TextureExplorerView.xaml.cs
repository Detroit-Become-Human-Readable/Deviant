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
        Entry.Format
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
    private CancellationTokenSource? queryCancellation, exportCancellation, resourceSelection;
    private readonly SemaphoreSlim nativeGate = new(1, 1);
    private readonly HashSet<Task> operations = [];
    private readonly Dictionary<ResourceIdentity, BitmapSource> nativeThumbnails = [];
    private readonly Dictionary<ResourceIdentity, int> thumbnailBindings = [];
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
    private long offset, total, queryVersion, publishedQueryVersion, nativeVersion, selectionVersion, resourceRequestVersion;
    private bool updating, suspended = true, cancelled, selectionSettled, selectedPending, disposed;
    private int selectedBindings, retryCount;
    private Task previewClear = Task.CompletedTask, queryTask = Task.CompletedTask, exportTask = Task.CompletedTask;
    private ResourceIdentity? nativeAdvancedIdentity;
    private string nativeAdvanced = "";

    public TextureExplorerView()
    {
        InitializeComponent(); ImageHost.Content = nativeView;
        Rows.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => StartVisibleThumbnails()));
        Relationships.ItemsSource = new[] { "Any", "With owners", "Without owners" };
        Relationships.SelectedIndex = 0;
        Sort.ItemsSource = new[] { "Named first", "Name", "Source order" }; Sort.SelectedIndex = 0;
        queryTimer.Tick += (_, _) => { queryTimer.Stop(); queryTask = Track(RefreshAsync(false)); };
        updateTimer.Tick += (_, _) =>
        {
            updateTimer.Stop();
            if (suspended || cancelled || discovery is null) return;
            if (queryCancellation is not null || !queryTask.IsCompleted)
            {
                updateTimer.Start();
                return;
            }
            RefreshOptions(); queryTask = Track(RefreshAsync(true));
            if (native is { } owner && selected is { } entry && owner.KnownCatalogCount(entry.Identity) != selectedBindings)
                QueueSelection();
            foreach (var card in CurrentResources)
                if (native is { } source && thumbnailBindings.TryGetValue(card.Identity, out var count) && count != source.KnownCatalogCount(card.Identity))
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
    public event EventHandler<TextureDiscoveryLinkEventArgs>? LinkSelected;
    public Action? DiscoveryCancel { get; set; }
    public Func<TextureDiscoveryEntry, string, bool, CancellationToken, Task>? ExportOriginalAsync { get; set; }
    public NativeTextureView NativeImageBrowser => nativeView;
    public NativeTextureSession? NativeSession => native;
    public ResourceIdentity? SelectedIdentity => selected?.Identity;
    public TextureDiscoveryEntry? SelectedEntry => selected;
    public string? SelectedLabel => selected?.Alias ?? selected?.Label;
    public SourceRevision? LoadedRevision => discovery?.Revision;
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
        RefreshOptions(); queryTask = Track(RefreshAsync(false, token)); await queryTask;
    }

    private void DiscoveryUpdated(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (sender != discovery || suspended || cancelled || disposed || lifetime.IsCancellationRequested) return;
            if (!updateTimer.IsEnabled) updateTimer.Start();
        });
    }

    private void RefreshOptions()
    {
        if (discovery is not { } owner) return;
        var dimension = Dimensions.SelectedItem;
        var format = Formats.SelectedItem as string; var role = Roles.SelectedItem as string;
        var context = (Contexts.SelectedItem as TextureContextOption)?.Key;
        var catalog = (Catalogs.SelectedItem as TextureContextOption)?.Key;
        updating = true;
        try
        {
            Dimensions.ItemsSource = new object[] { "Any" }.Concat(owner.FilterOptions.Dimensions).ToArray();
            Dimensions.SelectedItem = Dimensions.Items.Cast<object>().FirstOrDefault(item => Equals(item, dimension)) ?? Dimensions.Items[0];
            Formats.ItemsSource = new[] { "Any" }.Concat(owner.FilterOptions.Formats).ToArray();
            Formats.SelectedItem = Formats.Items.Cast<string>().FirstOrDefault(item => item == format) ?? "Any";
            Roles.ItemsSource = new[] { "Any" }.Concat(owner.FilterOptions.VerifiedRoles).ToArray();
            Roles.SelectedItem = Roles.Items.Cast<string>().FirstOrDefault(item => item == role) ?? "Any";
            Contexts.ItemsSource = new[] { new TextureContextOption("", "Any") }.Concat(owner.FilterOptions.Contexts).ToArray();
            Contexts.SelectedItem = Contexts.Items.Cast<TextureContextOption>().FirstOrDefault(item => item.Key == context) ?? Contexts.Items[0];
            ContextFilter.Visibility = owner.FilterOptions.Contexts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            Catalogs.ItemsSource = new[] { new TextureContextOption("", "Any") }.Concat(owner.FilterOptions.Catalogs).ToArray();
            Catalogs.SelectedItem = Catalogs.Items.Cast<TextureContextOption>().FirstOrDefault(item => item.Key == catalog) ?? Catalogs.Items[0];
            CatalogFilter.Visibility = owner.FilterOptions.Catalogs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { updating = false; }
    }

    private TextureDiscoveryQuery Query()
    {
        var dimension = Dimensions.SelectedItem as TextureDimensionOption;
        return new(Search.Text.Trim(), dimension?.Width, dimension?.Height,
            Formats.SelectedItem is string format && format != "Any" ? format : null,
            Roles.SelectedItem is string role && role != "Any" ? role : null,
            Relationships.SelectedIndex switch { 1 => true, 2 => false, _ => null },
            (Contexts.SelectedItem as TextureContextOption)?.Key is { Length: > 0 } context ? context : null,
            offset, PageSize, SortByName: Sort.SelectedIndex != 2,
            CatalogKey: (Catalogs.SelectedItem as TextureContextOption)?.Key is { Length: > 0 } catalog ? catalog : null, NamedFirst: Sort.SelectedIndex == 0);
    }

    public Task RefreshAsync(CancellationToken token = default) => RefreshAsync(true, token);
    private async Task RefreshAsync(bool preserveSelection, CancellationToken token = default)
    {
        if (discovery is not { } owner || suspended) return;
        queryCancellation?.Cancel();
        var version = ++queryVersion; var query = Query();
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspace.Token);
        queryCancellation = own;
        try
        {
            var result = await owner.QueryAsync(query, own.Token);
            own.Token.ThrowIfCancellationRequested();
            if (version != queryVersion || discovery != owner || suspended) return;
            if (result.Entries.Count == 0 && result.Total > 0 && offset >= result.Total)
            { offset = (result.Total - 1) / PageSize * PageSize; await RefreshAsync(preserveSelection, token); return; }
            total = result.Total; offset = result.Offset;
            var previous = selected?.Identity; var cards = result.Entries.Select(entry => new TextureExplorerCard(entry)).ToArray();
            updating = true;
            try
            {
                Rows.ItemsSource = cards;
                var same = cards.FirstOrDefault(card => card.Identity == previous);
                Rows.SelectedItem = same;
                if (same is not null) { selected = same.Entry; RenderDetails(false); }
                else if (!preserveSelection) SetSelected(null);
            }
            finally { updating = false; }
            var visible = cards.Select(card => card.Identity).ToHashSet();
            foreach (var identity in nativeThumbnails.Keys.Where(identity => !visible.Contains(identity)).ToArray())
            { nativeThumbnails.Remove(identity); thumbnailBindings.Remove(identity); }
            Previous.IsEnabled = offset > 0; Next.IsEnabled = offset + cards.Length < total;
            Page.Text = total == 0 ? "0" : $"{offset + 1:N0}–{offset + cards.Length:N0} of {total:N0}";
            Status.Text = $"{total:N0} textures";
            publishedQueryVersion = version;
            Empty.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
            StartVisibleThumbnails();
        }
        catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); }
        catch (Exception error) { if (version == queryVersion) Status.Text = error.Message; }
        finally { if (ReferenceEquals(queryCancellation, own)) queryCancellation = null; }
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) => ScheduleQuery();
    private void FilterChanged(object sender, SelectionChangedEventArgs e) => ScheduleQuery();
    private void ScheduleQuery()
    {
        if (updating || discovery is null || suspended) return;
        offset = 0; queryCancellation?.Cancel(); queryTimer.Stop(); queryTimer.Start();
    }
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        updating = true;
        try { Search.Text = ""; Dimensions.SelectedIndex = Formats.SelectedIndex = Roles.SelectedIndex = Relationships.SelectedIndex = Contexts.SelectedIndex = Catalogs.SelectedIndex = Sort.SelectedIndex = 0; }
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
            if (!CurrentResources.Any(card => card.Identity == identity))
            {
                var page = await owner.LocateAsync(identity, Query(), own.Token); var reset = false;
                if (page is null) { page = await owner.LocateAsync(identity, new(Limit: PageSize), own.Token); reset = page is not null; }
                own.Token.ThrowIfCancellationRequested(); if (owner != discovery || request != resourceRequestVersion) return;
                if (page is not null)
                {
                    updating = true;
                    try
                    {
                        if (reset) { Search.Text = ""; Dimensions.SelectedIndex = Formats.SelectedIndex = Roles.SelectedIndex = Relationships.SelectedIndex = Contexts.SelectedIndex = Catalogs.SelectedIndex = 0; }
                        Rows.ItemsSource = page.Entries.Select(item => new TextureExplorerCard(item)).ToArray();
                        offset = page.Offset; total = page.Total;
                        Previous.IsEnabled = offset > 0; Next.IsEnabled = offset + page.Entries.Count < total;
                        Page.Text = $"{offset + 1:N0}–{offset + page.Entries.Count:N0} of {total:N0}"; Status.Text = $"{total:N0} textures";
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

    private void SetSelected(TextureDiscoveryEntry? entry)
    {
        resourceSelection?.Cancel(); ++resourceRequestVersion;
        var changed = selected?.Identity != entry?.Identity;
        selected = entry;
        if (nativePreviewSelection?.SourceIdentity != entry?.Identity) nativePreviewSelection = null;
        if (changed) ++selectionVersion;
        RenderDetails(changed); QueueSelection(); SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
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

    private void QueueSelection()
    {
        if (suspended || disposed) return;
        cancelled = false; nativeWork.Cancel(); nativeWork.Dispose(); nativeWork = new();
        var version = ++nativeVersion; selectedPending = false; selectionSettled = false;
        thumbnailPending.Clear(); thumbnailQueued.Clear(); retryTimer.Stop();
        nativeAdvancedIdentity = null; nativeAdvanced = "";
        ImageHost.Visibility = Visibility.Collapsed; PreviewStatus.Visibility = Visibility.Visible;
        previewClear = Track(nativeView.ClearAsync());
        if (selected is not { } entry || native is not { } owner || discovery is not { } catalog || documentReader is null)
        { PreviewStatus.Text = "Select a texture"; selectionSettled = true; StartVisibleThumbnails(); return; }
        PreviewStatus.Text = "Opening image…";
        Track(LoadSelectionAsync(catalog, owner, entry.Identity, version, nativeWork.Token));
    }

    private async Task LoadSelectionAsync(ITextureDiscoveryCatalog catalog, NativeTextureSession owner, ResourceIdentity identity, long version, CancellationToken token)
    {
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, workspace.Token, lifetime.Token);
        var entered = false;
        NativeContainerLease? companion = null;
        try
        {
            await previewClear.WaitAsync(own.Token);
            await nativeGate.WaitAsync(own.Token); entered = true;
            if (!Current(owner, identity, version)) return;
            NativeTextureReadResult result;
            if (nativePreviewSelection is { } exact && exact.SourceIdentity == identity)
            {
                companion = await NativeContainerLease.OpenAsync(owner.Snapshot,
                    exact.NativeIdentity with { MemberPath = null }, exact.MemoryBudget, own.Token);
                result = await owner.ReadForNativeAsync(exact.NativeIdentity, exact.CatalogIdentity, companion, own.Token);
                if (result.Source is { } resolved && (resolved.SelectedIdentity != identity || resolved.NativeTextureIdentity != exact.NativeIdentity ||
                    resolved.CatalogIdentity != exact.CatalogIdentity))
                    throw new ArchiveException("TextureSourceMismatch", "The texture source does not match the selected material.");
            }
            else result = await owner.ReadAsync(identity, own.Token);
            own.Token.ThrowIfCancellationRequested(); if (!Current(owner, identity, version)) return;
            selectedBindings = result.Source?.CatalogBindingCount ?? owner.KnownCatalogCount(identity);
            selectedPending = result.Pending;
            if (result.Pending) retryTimer.Start();
            var details = await catalog.GetDetailsAsync(identity, own.Token);
            own.Token.ThrowIfCancellationRequested(); if (!Current(owner, identity, version)) return;
            if (details is not null) { selected = details; RenderDetails(false); }
            if (result.Source is not { } source) { PreviewStatus.Text = result.Reason ?? "Preview unavailable"; return; }
            var document = await Task.Run(() => documentReader!(source, own.Token), own.Token);
            own.Token.ThrowIfCancellationRequested(); if (!Current(owner, identity, version)) return;
            await nativeView.LoadAsync(owner, source, document, selected!.Label, own.Token);
            own.Token.ThrowIfCancellationRequested(); if (!Current(owner, identity, version)) return;
            ImageHost.Visibility = Visibility.Visible; PreviewStatus.Visibility = Visibility.Collapsed;
            nativeAdvancedIdentity = identity;
            nativeAdvanced = $"\nMetadata {source.Metadata.Identity.MemberPath}\nMetadata SHA-256 {source.MetadataSha256}\nDerived DDS SHA-256 {document.ContentSha256}\nMips {document.Info.MipCount}/{source.Metadata.MipSizeCount}";
            Advanced.Text += nativeAdvanced;
            if (nativePreviewSelection is null && owner.KnownCatalogCount(identity) != selectedBindings) { selectedPending = true; retryTimer.Start(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (Current(owner, identity, version)) PreviewStatus.Text = error.Message; }
        finally
        {
            if (entered) nativeGate.Release();
            companion?.Dispose();
            if (!suspended && !cancelled && version == nativeVersion && !own.IsCancellationRequested)
            { selectionSettled = true; StartVisibleThumbnails(); }
        }
    }
    private bool Current(NativeTextureSession owner, ResourceIdentity identity, long version)
        => !suspended && !cancelled && !disposed && native == owner && selected?.Identity == identity && nativeVersion == version;

    private void Thumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not TextureExplorerCard card) return;
        image.Source = nativeThumbnails.GetValueOrDefault(card.Identity);
        if (!suspended && !cancelled && (selected is null || selectionSettled)) QueueThumbnail(card, image);
    }
    private void Thumbnail_ContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Thumbnail_Loaded(sender, new());
    private void StartVisibleThumbnails()
    {
        if (suspended || cancelled || (selected is not null && !selectionSettled)) return;
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
        if (suspended || cancelled || native is not { } owner || cache is not { } thumbnailCache || documentReader is null) return;
        if (nativeThumbnails.TryGetValue(card.Identity, out var bitmap)) { image.Source = bitmap; return; }
        image.Source = null;
        if (!IsInViewport(image)) return;
        if (!thumbnailQueued.Add(card.Identity)) return;
        Track(LoadThumbnailAsync(owner, thumbnailCache, card.Identity, image, nativeVersion, nativeWork.Token));
    }
    private async Task LoadThumbnailAsync(NativeTextureSession owner, CacheManager thumbnailCache, ResourceIdentity identity, Image image, long version, CancellationToken token)
    {
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, workspace.Token, lifetime.Token);
        var entered = false;
        try
        {
            await nativeGate.WaitAsync(own.Token); entered = true;
            if (!CanPublish(owner, identity, image, version)) return;
            if (nativeThumbnails.TryGetValue(identity, out var previous)) { image.Source = previous; return; }
            var result = await owner.ReadAsync(identity, own.Token, nativeView.CurrentSource?.RetainedSourceBytes ?? 0);
            own.Token.ThrowIfCancellationRequested(); if (!CanPublish(owner, identity, image, version)) return;
            thumbnailBindings[identity] = result.Source?.CatalogBindingCount ?? owner.KnownCatalogCount(identity);
            if (result.Pending) { thumbnailPending.Add(identity); retryTimer.Start(); return; }
            thumbnailPending.Remove(identity);
            if (result.Source is not { } source) { image.ToolTip = result.Reason; return; }
            var document = await Task.Run(() => documentReader!(source, own.Token), own.Token);
            var mip = document.MipLevels.FirstOrDefault(level => Math.Max(level.Width, level.Height) <= 128)?.Index ?? document.Info.MipCount - 1;
            var key = CacheManager.Key(owner.Revision.IndexContentId + ":" + owner.Revision.PhysicalSourceStamp,
                document.ContentSha256, NativeTextureDecoder.Version + ":discovery-thumbnail-rgb-v1", $"thumbnail:128:mip:{mip}:RGB");
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
            own.Token.ThrowIfCancellationRequested(); if (!CanPublish(owner, identity, image, version)) return;
            if (owner.KnownCatalogCount(identity) != source.CatalogBindingCount)
            { thumbnailPending.Add(identity); retryTimer.Start(); return; }
            nativeThumbnails[identity] = bitmap; image.Source = bitmap; image.ToolTip = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (CanPublish(owner, identity, image, version)) image.ToolTip = error.Message; }
        finally { if (entered) nativeGate.Release(); if (version == nativeVersion) thumbnailQueued.Remove(identity); }
    }
    private bool CanPublish(NativeTextureSession owner, ResourceIdentity identity, Image image, long version)
        => !suspended && !cancelled && !disposed && native == owner && version == nativeVersion &&
            CurrentResources.Any(card => card.Identity == identity) && image.DataContext is TextureExplorerCard current && current.Identity == identity;
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
            await RefreshAsync(true);
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

    public IReadOnlyDictionary<string, string> CaptureFilter()
    {
        var dimension = Dimensions.SelectedItem as TextureDimensionOption;
        return new Dictionary<string, string>
        {
            ["search"] = Search.Text, ["width"] = dimension?.Width.ToString(CultureInfo.InvariantCulture) ?? "",
            ["height"] = dimension?.Height.ToString(CultureInfo.InvariantCulture) ?? "",
            ["format"] = Formats.SelectedItem as string ?? "Any", ["role"] = Roles.SelectedItem as string ?? "Any",
            ["relationships"] = Relationships.SelectedIndex.ToString(CultureInfo.InvariantCulture),
            ["sort"] = Sort.SelectedIndex.ToString(CultureInfo.InvariantCulture),
            ["context"] = (Contexts.SelectedItem as TextureContextOption)?.Key ?? "", ["group"] = (Catalogs.SelectedItem as TextureContextOption)?.Key ?? ""
        };
    }
    public async Task ApplyFilterAsync(IReadOnlyDictionary<string, string> values, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); RefreshOptions();
        object dimension = Dimensions.Items[0];
        if (values.GetValueOrDefault("width", "").Length > 0 || values.GetValueOrDefault("height", "").Length > 0)
        {
            if (!int.TryParse(values.GetValueOrDefault("width"), NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
                !int.TryParse(values.GetValueOrDefault("height"), NumberStyles.None, CultureInfo.InvariantCulture, out var height))
                throw new InvalidOperationException("The saved dimensions are unavailable.");
            dimension = Dimensions.Items.OfType<TextureDimensionOption>().FirstOrDefault(item => item.Width == width && item.Height == height)
                ?? throw new InvalidOperationException("The saved dimensions are unavailable.");
        }
        var format = Formats.Items.Cast<string>().FirstOrDefault(item => item == values.GetValueOrDefault("format", "Any"))
            ?? throw new InvalidOperationException("The saved format is unavailable.");
        var role = Roles.Items.Cast<string>().FirstOrDefault(item => item == values.GetValueOrDefault("role", "Any"))
            ?? throw new InvalidOperationException("The saved role is unavailable.");
        var context = Contexts.Items.Cast<TextureContextOption>().FirstOrDefault(item => item.Key == values.GetValueOrDefault("context", ""))
            ?? throw new InvalidOperationException("The saved owner is unavailable.");
        var catalog = Catalogs.Items.Cast<TextureContextOption>().FirstOrDefault(item => item.Key == values.GetValueOrDefault("group", ""))
            ?? throw new InvalidOperationException("The saved catalog is unavailable.");
        if (!int.TryParse(values.GetValueOrDefault("relationships", "0"), out var relationships) || relationships < 0 || relationships > 2)
            throw new InvalidOperationException("The saved relationship filter is unavailable.");
        if (!int.TryParse(values.GetValueOrDefault("sort", "0"), out var sort) || sort < 0 || sort > 2)
            throw new InvalidOperationException("The saved sort is unavailable.");
        updating = true;
        try { Search.Text = values.GetValueOrDefault("search", ""); Dimensions.SelectedItem = dimension; Formats.SelectedItem = format; Roles.SelectedItem = role; Contexts.SelectedItem = context; Catalogs.SelectedItem = catalog; Relationships.SelectedIndex = relationships; Sort.SelectedIndex = sort; offset = 0; }
        finally { updating = false; }
        queryTimer.Stop(); queryTask = Track(RefreshAsync(false, token)); await queryTask;
    }

    private Task Track(Task task) { operations.Add(task); _ = ObserveAsync(task); return task; }
    private async Task ObserveAsync(Task task) { try { await task; } catch { } finally { operations.Remove(task); } }
    private async Task DrainAsync() { while (operations.Count > 0) { var batch = operations.ToArray(); try { await Task.WhenAll(batch); } catch { } foreach (var task in batch) operations.Remove(task); } }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        cancelled = true; ++nativeVersion; resourceSelection?.Cancel(); queryCancellation?.Cancel(); exportCancellation?.Cancel(); nativeWork.Cancel(); queryTimer.Stop(); updateTimer.Stop(); retryTimer.Stop();
        selectedPending = false; thumbnailPending.Clear(); thumbnailQueued.Clear(); nativeThumbnails.Clear();
        ImageHost.Visibility = Visibility.Collapsed; PreviewStatus.Visibility = Visibility.Visible; PreviewStatus.Text = "Cancelled";
        foreach (var image in ThumbnailImages(Rows)) image.Source = null;
        previewClear = Track(nativeView.ClearAsync()); DiscoveryCancel?.Invoke();
    }
    public async Task SuspendAsync()
    {
        suspended = true; ++nativeVersion; resourceSelection?.Cancel(); queryCancellation?.Cancel(); exportCancellation?.Cancel(); nativeWork.Cancel(); queryTimer.Stop(); updateTimer.Stop(); retryTimer.Stop();
        selectedPending = false; thumbnailPending.Clear(); thumbnailQueued.Clear(); ImageHost.Visibility = Visibility.Collapsed;
        previewClear = Track(nativeView.ClearAsync()); await DrainAsync();
        nativeThumbnails.Clear(); thumbnailBindings.Clear(); foreach (var image in ThumbnailImages(Rows)) image.Source = null;
    }
    public void Resume()
    {
        if (discovery is null || disposed) return;
        suspended = false; cancelled = false; retryCount = 0;
        RefreshOptions(); queryTask = Track(RefreshAsync(true)); QueueSelection();
    }
    public async Task ClearAsync()
    {
        if (discovery is { } previous) previous.Updated -= DiscoveryUpdated;
        workspace.Cancel(); await SuspendAsync();
        discovery = null; native = null; cache = null; documentReader = null;
        nativePreviewSelection = null;
        workspace.Dispose(); workspace = new(); nativeWork.Dispose(); nativeWork = new();
        updating = true;
        try { Rows.ItemsSource = null; selected = null; ++selectionVersion; Search.Text = ""; Dimensions.ItemsSource = Formats.ItemsSource = Roles.ItemsSource = Contexts.ItemsSource = Catalogs.ItemsSource = null; Relationships.SelectedIndex = 0; }
        finally { updating = false; }
        offset = total = 0; Status.Text = Page.Text = ""; Empty.Visibility = Visibility.Collapsed; RenderDetails(true);
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        lifetime.Cancel(); await ClearAsync(); disposed = true; await nativeView.DisposeAsync();
        nativeWork.Dispose(); workspace.Dispose(); lifetime.Dispose(); nativeGate.Dispose();
    }
    private sealed record NativePreviewSelection(ResourceIdentity SourceIdentity, ResourceIdentity NativeIdentity,
        ResourceIdentity CatalogIdentity, AssetMemoryBudget MemoryBudget);
}
