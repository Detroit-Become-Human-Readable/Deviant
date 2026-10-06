namespace Deviant.Assets;

/// <summary>Projects an archived label for display without changing its stored text.</summary>
public static class TextureNameDisplay
{
    private static readonly (int Length, string Format)[] GuidFormats = [(36, "D"), (32, "N"), (38, "B"), (38, "P")];

    public static string Project(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var (length, format) in GuidFormats)
            if (name.Length > length + 1 && name[length] is '_' or '-' or '.' or ' ' &&
                Guid.TryParseExact(name.AsSpan(0, length), format, out _)) return name[(length + 1)..];
        return name;
    }
}
