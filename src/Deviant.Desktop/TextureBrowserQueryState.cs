using System.Globalization;
using Deviant.Assets;

namespace Deviant.Desktop;

internal sealed record TextureBrowserQueryState(TextureDiscoveryQuery Query, ResourceIdentity? SelectedIdentity = null)
{
    public static TextureBrowserQueryState Default { get; } = new(new TextureDiscoveryQuery(Search: "", SortByName: false,
        NamedFirst: false, Kind: TextureKindFilter.Images));

    public static TextureBrowserQueryState FromSaved(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int? width = null, height = null;
        if (Text("width").Length > 0 || Text("height").Length > 0)
        {
            if (!int.TryParse(Text("width"), NumberStyles.None, CultureInfo.InvariantCulture, out int w) || w < 1 ||
                !int.TryParse(Text("height"), NumberStyles.None, CultureInfo.InvariantCulture, out int h) || h < 1)
                throw new InvalidOperationException("The saved dimensions are unavailable.");
            width = w; height = h;
        }
        int relationship = Choice("relationships", 0, "relationship filter");
        int sort = Choice("sort", 2, "sort");
        int kind = Choice("kind", 0, "texture kind");
        return new(new TextureDiscoveryQuery(Search: Text("search"), Width: width, Height: height,
            Format: Facet("format"), VerifiedRole: Facet("role"),
            HasVerifiedOwner: relationship switch { 1 => true, 2 => false, _ => null },
            ContextKey: Key("context"), CatalogKey: Key("group"), SortByName: sort != 2, NamedFirst: sort == 0,
            Kind: kind switch { 0 => TextureKindFilter.Images, 1 => TextureKindFilter.Lighting, _ => TextureKindFilter.All },
            IncludeLodBuffers: kind == 2));

        string Text(string key, string fallback = "") => values.GetValueOrDefault(key) ?? fallback;
        string? Key(string key) => Text(key) is { Length: > 0 } value ? value : null;
        string? Facet(string key) => Text(key, "Any") is { Length: > 0 } value && value != "Any" ? value : null;
        int Choice(string key, int fallback, string label) =>
            int.TryParse(Text(key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.None,
                CultureInfo.InvariantCulture, out int value) && value is >= 0 and <= 2
                ? value : throw new InvalidOperationException($"The saved {label} is unavailable.");
    }

    public IReadOnlyDictionary<string, string> ToSaved() => new Dictionary<string, string>
    {
        ["search"] = Query.Search ?? "", ["width"] = Query.Width?.ToString(CultureInfo.InvariantCulture) ?? "",
        ["height"] = Query.Height?.ToString(CultureInfo.InvariantCulture) ?? "", ["format"] = Query.Format ?? "Any",
        ["role"] = Query.VerifiedRole ?? "Any", ["context"] = Query.ContextKey ?? "", ["group"] = Query.CatalogKey ?? "",
        ["relationships"] = Query.HasVerifiedOwner switch { true => "1", false => "2", _ => "0" },
        ["sort"] = !Query.SortByName ? "2" : Query.NamedFirst ? "0" : "1",
        ["kind"] = Query.Kind switch { TextureKindFilter.Images => "0", TextureKindFilter.Lighting => "1", _ => "2" }
    };

    public void EnsureAvailable(TextureDiscoveryFilterOptions options)
    {
        if (Query.Width is { } width && Query.Height is { } height && !options.Dimensions.Contains(new(width, height)))
            throw new InvalidOperationException("The saved dimensions are unavailable.");
        if (Query.Format is { } format && !options.Formats.Contains(format, StringComparer.Ordinal))
            throw new InvalidOperationException("The saved format is unavailable.");
        if (Query.VerifiedRole is { } role && !options.VerifiedRoles.Contains(role, StringComparer.Ordinal))
            throw new InvalidOperationException("The saved role is unavailable.");
        if (Query.ContextKey is { } context && !options.Contexts.Any(item => item.Key == context))
            throw new InvalidOperationException("The saved owner is unavailable.");
        if (Query.CatalogKey is { } catalog && !options.Catalogs.Any(item => item.Key == catalog))
            throw new InvalidOperationException("The saved catalog is unavailable.");
    }
}
