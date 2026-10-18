using System.Windows;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;

namespace Deviant.Desktop;

public partial class TextureExplorerView
{
    private TextureBrowserQueryState queryState = TextureBrowserQueryState.Default;
    private TextureDiscoveryFilterOptions? availableFilterOptions;
    private long filterInputVersion;
    private long filterOptionsRequestVersion;
    private Task filterOptionsPublication = Task.CompletedTask;
    public TextureBrowseResponse? PublishedBrowseResponse { get; private set; }
    public event EventHandler? BrowsePublished;
    private ITextureBrowseSession? browseSession;
    private TextureDiscoverySession? browseOwner;
    private TextureDiscoveryQuery? browseKey;

    private async Task<TextureBrowseResponse> ReadBrowseResponseAsync(TextureDiscoverySession owner,
        TextureDiscoveryQuery query, long version, CancellationToken token, bool refreshGeneration = false)
    {
        var current = await GetBrowseSessionAsync(owner, query, version, token, refreshGeneration);
        try { return await current.ReadAsync(query, token); }
        catch (ArchiveException error) when (error.Code == "TextureBrowseGenerationUnavailable")
        {
            token.ThrowIfCancellationRequested();
            if (version != queryVersion) throw new OperationCanceledException(token);
            current = await GetBrowseSessionAsync(owner, query, version, token, refreshGeneration: true);
            return await current.ReadAsync(query, token);
        }
    }

    private async Task<ITextureBrowseSession> GetBrowseSessionAsync(TextureDiscoverySession owner,
        TextureDiscoveryQuery query, long version, CancellationToken token, bool refreshGeneration = false)
    {
        var key = query with { Offset = 0, Limit = 1 };
        ITextureBrowseSession current;
        if (browseSession is null || browseOwner != owner || browseKey != key || refreshGeneration)
        {
            var opened = await owner.OpenBrowseAsync(query, token);
            if (token.IsCancellationRequested || version != queryVersion || owner != discovery || suspended || disposed)
            {
                await opened.DisposeAsync();
                throw new OperationCanceledException(token);
            }
            var previous = browseSession;
            browseSession = opened; browseOwner = owner; browseKey = key;
            current = opened;
            if (previous is not null) await previous.DisposeAsync();
        }
        else current = browseSession;
        return current;
    }

    private async Task ReleaseBrowseSessionAsync()
    {
        var previous = browseSession;
        browseSession = null; browseOwner = null; browseKey = null;
        if (previous is not null) await previous.DisposeAsync();
    }

    private async Task RefreshOptionsAsync(CancellationToken token = default)
    {
        using var diagnostic = TextureDiagnostic("TextureBrowser.Ui.Options", TextureDiagnosticKey());
        long request = ++filterOptionsRequestVersion;
        var publication = RefreshOptionsCoreAsync(request, token);
        filterOptionsPublication = publication;
        await publication;
        while (request != filterOptionsRequestVersion)
        {
            var replacement = filterOptionsPublication;
            await replacement.WaitAsync(token);
            if (ReferenceEquals(replacement, filterOptionsPublication)) return;
        }
    }

    private TextureDiscoveryQuery Query() => queryState.Query with
    { Search = queryState.Query.Search?.Trim(), Offset = offset, Limit = PageSize };

    private void RenderFilterOptions(TextureDiscoveryFilterOptions options)
    {
        availableFilterOptions = options;
        var query = queryState.Query;
        var context = Contexts.SelectedItem as TextureContextOption;
        var catalog = Catalogs.SelectedItem as TextureContextOption;
        bool previous = updating;
        updating = true;
        try
        {
            var dimensions = options.Dimensions.ToList();
            TextureDimensionOption? selectedDimension = query.Width is { } w && query.Height is { } h ? new(w, h) : null;
            if (selectedDimension is not null && !dimensions.Contains(selectedDimension)) dimensions.Add(selectedDimension);
            Dimensions.ItemsSource = new object[] { "Any" }.Concat(dimensions).ToArray();
            Dimensions.SelectedItem = selectedDimension is null ? Dimensions.Items[0] : selectedDimension;
            Formats.ItemsSource = Strings(options.Formats, query.Format);
            Formats.SelectedItem = query.Format ?? "Any";
            Roles.ItemsSource = Strings(options.VerifiedRoles, query.VerifiedRole);
            Roles.SelectedItem = query.VerifiedRole ?? "Any";
            Contexts.ItemsSource = Choices(options.Contexts, query.ContextKey, context);
            Contexts.SelectedItem = Contexts.Items.Cast<TextureContextOption>().Single(item => item.Key == (query.ContextKey ?? ""));
            Catalogs.ItemsSource = Choices(options.Catalogs, query.CatalogKey, catalog);
            Catalogs.SelectedItem = Catalogs.Items.Cast<TextureContextOption>().Single(item => item.Key == (query.CatalogKey ?? ""));
            ContextFilter.Visibility = options.Contexts.Count > 0 || query.ContextKey is not null ? Visibility.Visible : Visibility.Collapsed;
            CatalogFilter.Visibility = options.Catalogs.Count > 0 || query.CatalogKey is not null ? Visibility.Visible : Visibility.Collapsed;
            Search.Text = query.Search ?? "";
            Relationships.SelectedIndex = query.HasVerifiedOwner switch { true => 1, false => 2, _ => 0 };
            Sort.SelectedIndex = !query.SortByName ? 2 : query.NamedFirst ? 0 : 1;
            Kinds.SelectedIndex = query.Kind switch { TextureKindFilter.Images => 0, TextureKindFilter.Lighting => 1, _ => 2 };
        }
        finally { updating = previous; }

        static string[] Strings(IReadOnlyList<string> values, string? selected) => new[] { "Any" }
            .Concat(values).Concat(selected is null ? [] : new[] { selected }).Distinct(StringComparer.Ordinal).ToArray();
        static TextureContextOption[] Choices(IReadOnlyList<TextureContextOption> values, string? key, TextureContextOption? previous)
        {
            var result = new List<TextureContextOption> { new("", "Any") };
            result.AddRange(values.Where(item => item.Key.Length > 0).DistinctBy(item => item.Key));
            if (key is not null && !result.Any(item => item.Key == key))
                result.Add(new(key, previous?.Key == key ? previous.Label : "Unavailable"));
            return result.ToArray();
        }
    }

    public IReadOnlyDictionary<string, string> CaptureFilter() => queryState.ToSaved();

    public async Task ApplyFilterAsync(IReadOnlyDictionary<string, string> values, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var requested = TextureBrowserQueryState.FromSaved(values) with { SelectedIdentity = selected?.Identity };
        queryState = requested;
        long request = ++filterInputVersion;
        queryCancellation?.Cancel(); queryTimer.Stop();
        offset = 0;
        if (discovery is TextureDiscoverySession)
        {
            try
            {
                queryTask = Track(RefreshAsync(false, token, throwOnError: true, refreshGeneration: true));
                await queryTask;
                token.ThrowIfCancellationRequested();
                if (request != filterInputVersion) throw new OperationCanceledException();
                requested.EnsureAvailable(availableFilterOptions ?? throw new InvalidOperationException("Texture filters are unavailable."));
            }
            catch (ArchiveException error) when (error.Code == "UnknownTextureCatalog")
            {
                if (request == filterInputVersion) ShowUnavailableFilter(error.Message);
                throw new InvalidOperationException(error.Message, error);
            }
            catch (InvalidOperationException error)
            {
                if (request == filterInputVersion) ShowUnavailableFilter(error.Message);
                throw;
            }
            return;
        }
        try
        {
            await RefreshOptionsAsync(token);
            token.ThrowIfCancellationRequested();
            if (request != filterInputVersion) throw new OperationCanceledException();
            requested.EnsureAvailable(availableFilterOptions ?? throw new InvalidOperationException("Texture filters are unavailable."));
            offset = 0;
        }
        catch (InvalidOperationException error)
        {
            if (request == filterInputVersion)
                ShowUnavailableFilter(error.Message);
            throw;
        }
        queryTask = Track(RefreshAsync(false, token));
        await queryTask;
    }

    private void ShowUnavailableFilter(string message)
    {
        Rows.ItemsSource = Array.Empty<TextureExplorerCard>();
        SetSelected(null); total = offset = 0; PublishedBrowseResponse = null;
        Previous.IsEnabled = Next.IsEnabled = false;
        Page.Text = "0"; Status.Text = message; Empty.Visibility = Visibility.Visible;
    }

    private void PublishBrowseResult(TextureDiscoveryPage result, TextureBrowseResponse? response, long version,
        bool preserveSelection, string diagnosticKey)
    {
        total = result.Total; offset = result.Offset;
        if (response is not null) RenderFilterOptions(response.Options);
        var previous = selected?.Identity; var cards = result.Entries.Select(entry => new TextureExplorerCard(entry)).ToArray();
        updating = true;
        try
        {
            Rows.ItemsSource = cards;
            var same = cards.FirstOrDefault(card => card.Identity == previous);
            Rows.SelectedItem = same;
            if (same is not null)
            {
                if (nativePreviewSelection is null)
                {
                    selected = same.Entry;
                    if (CurrentPreviewState == TexturePreviewState.Ready && discovery is { } catalog && native is { } owner)
                        _ = Track(LoadSelectionRelationshipEvidenceAsync(catalog, owner, same.Identity, nativeVersion, nativeWork.Token));
                }
                RenderDetails(false);
            }
            else if (!preserveSelection) SetSelected(null);
        }
        finally { updating = false; }
        var visible = cards.Select(card => card.Identity).ToHashSet();
        foreach (var identity in nativeThumbnails.Keys.Where(identity => !visible.Contains(identity)).ToArray())
        { nativeThumbnails.Remove(identity); thumbnailBindings.Remove(identity); thumbnailVersions.Remove(identity); }
        Previous.IsEnabled = offset > 0; Next.IsEnabled = offset + cards.Length < total;
        Page.Text = total == 0 ? "0" : $"{offset + 1:N0}–{offset + cards.Length:N0} of {total:N0}";
        Status.Text = $"{total:N0} images";
        publishedQueryVersion = version;
        PublishedBrowseResponse = response;
        AssetLoadDiagnostics.Counters("TextureBrowser.Ui.Published", diagnosticKey, new Dictionary<string, long>
        { ["QueryVersion"] = version, ["Rows"] = cards.Length, ["Total"] = total, ["Offset"] = offset });
        Empty.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        BrowsePublished?.Invoke(this, EventArgs.Empty);
        StartVisibleThumbnails();
    }
}
