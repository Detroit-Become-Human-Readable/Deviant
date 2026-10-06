using System.Globalization;
using System.Text.RegularExpressions;

namespace Deviant.Desktop;

public static class SceneNameDisplay
{
    private static readonly Regex GenericNumber = new(@"^(?:Node|Mesh|Object|Catalog|Transform)\s*\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CamelBoundary = new(@"(?<=[a-z])(?=[A-Z])", RegexOptions.CultureInvariant);
    private static readonly Regex Separators = new(@"[_\s]+", RegexOptions.CultureInvariant);
    private static readonly Regex ExporterInstance = new(@"\s*:\s*qd\s*Instance\s*\d+\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Project(string? sourceName, string fallback)
    {
        if (string.IsNullOrWhiteSpace(sourceName)) return fallback;
        var name = sourceName.Trim();
        name = ExporterInstance.Replace(name, "").Trim();
        if (name.Contains("dc-object/", StringComparison.OrdinalIgnoreCase) || name.Contains("dc-object\\", StringComparison.OrdinalIgnoreCase) ||
            name.Length >= 32 && name.Take(32).All(Uri.IsHexDigit) || GenericNumber.IsMatch(name) || name.All(char.IsDigit)) return fallback;
        var slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (slash >= 0 && slash < name.Length - 1) name = name[(slash + 1)..];
        name = CamelBoundary.Replace(Separators.Replace(name, " "), " ").Trim();
        if (name.Length == 0) return fallback;
        if (GenericNumber.IsMatch(name) || name.All(char.IsDigit)) return fallback;
        if (name.Any(char.IsLetter) && name.Where(char.IsLetter).All(char.IsUpper))
            name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
        return name;
    }
}
