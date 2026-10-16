using System.Windows;
using Deviant.Assets;

namespace Deviant.Desktop;

public partial class TextureExplorerView
{
    private TextureBrowserQueryState queryState = TextureBrowserQueryState.Default;
    private TextureDiscoveryFilterOptions? availableFilterOptions;
    private long filterInputVersion;
    private long filterOptionsRequestVersion;
    private Task filterOptionsPublication = Task.CompletedTask;

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
            {
                Rows.ItemsSource = Array.Empty<TextureExplorerCard>();
                SetSelected(null); total = offset = 0;
                Previous.IsEnabled = Next.IsEnabled = false;
                Page.Text = "0"; Status.Text = error.Message; Empty.Visibility = Visibility.Visible;
            }
            throw;
        }
        queryTask = Track(RefreshAsync(false, token));
        await queryTask;
    }
}
