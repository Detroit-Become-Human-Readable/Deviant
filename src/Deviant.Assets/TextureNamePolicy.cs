using System.Security.Cryptography;
using System.Text;
namespace Deviant.Assets;

public sealed record TextureNameDecision(string Label, TextureNameKind Kind, string? OriginalName,
    string? OriginalPath, string ExportStem, IReadOnlyList<string> Conflicts);

public static class TextureNamePolicy
{
    public const string Version = "texture-name-policy-v2";
    public static TextureNameDecision Decide(ResourceIdentity identity, uint resourceType, uint resourceId,
        int? width, int? height, string? format, IReadOnlyList<TextureNameCandidate> candidates)
    {
        var valid = candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate.Text) && candidate.Evidence.Count > 0
            && (candidate.Owner is null || candidate.Owner.IndexContentId == identity.IndexContentId)
            && (candidate.Kind != TextureNameKind.VerifiedOwnerRole || (candidate.Owner is not null && !string.IsNullOrWhiteSpace(candidate.Role)))
            && candidate.Evidence.All(evidence => EvidenceValid(evidence) && evidence.Revision.IndexContentId == identity.IndexContentId
                && evidence.Source.IndexContentId == identity.IndexContentId)).ToArray();
        var fallback = $"Texture {resourceId:X8}";
        var conflicts = new List<string>();
        string? originalName = null, originalPath = null;
        string label = fallback; var kind = TextureNameKind.Fallback;
        foreach (var rank in new[] { TextureNameKind.OriginalName, TextureNameKind.OriginalPath,
            TextureNameKind.VerifiedOwnerRole, TextureNameKind.InferredDescription })
        {
            var choices = valid.Where(candidate => candidate.Kind == rank).Select(candidate => candidate.Text)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (choices.Length == 0) continue;
            if (choices.Length > 1 && rank is TextureNameKind.OriginalName or TextureNameKind.OriginalPath)
            {
                conflicts.AddRange(choices); break;
            }
            if (rank == TextureNameKind.OriginalName) { originalName = choices[0]; label = originalName; }
            else if (rank == TextureNameKind.OriginalPath) { originalPath = choices[0]; label = Basename(originalPath); }
            else label = string.Join(" / ", choices);
            kind = rank; break;
        }
        var paths = valid.Where(candidate => candidate.Kind == TextureNameKind.OriginalPath).Select(candidate => candidate.Text)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 1) originalPath ??= paths[0];
        if (paths.Length > 1) conflicts.AddRange(paths);
        var stem = SanitizeStem(label);
        if (kind == TextureNameKind.OriginalPath) stem = SanitizeStem(Path.GetFileNameWithoutExtension(stem));
        var member = identity.MemberPath is null ? "" : "-m" + Hash(identity.MemberPath);
        var suffix = $"-t{resourceType}-id{resourceId:X8}-r{identity.Ordinal}-i{Hash(identity.IndexContentId)}{member}";
        var available = Math.Max(1, 220 - suffix.Length);
        if (stem.Length > available) stem = stem[..available];
        if (stem.Length > 0 && char.IsHighSurrogate(stem[^1])) stem = stem[..^1];
        return new(label, kind, originalName, originalPath, stem + suffix,
            conflicts.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }
    private static bool EvidenceValid(TextureEvidence evidence)
        => evidence.SourceSha256.Length == 64 && evidence.SourceSha256.All(Uri.IsHexDigit)
        && !string.IsNullOrWhiteSpace(evidence.Reader) && !string.IsNullOrWhiteSpace(evidence.Field)
        && (evidence.DecodedExtent is null || (evidence.DecodedExtent.Container.IndexContentId == evidence.Source.IndexContentId
            && evidence.DecodedExtent.Container.Ordinal == evidence.Source.Ordinal && evidence.DecodedExtent.Offset >= 0 && evidence.DecodedExtent.Length >= 0))
        && (evidence.PhysicalExtent is null || (evidence.PhysicalExtent.Offset >= 0 && evidence.PhysicalExtent.Length >= 0));
    private static string Basename(string path) => path.Replace('\\', '/').Split('/').LastOrDefault(segment => segment.Length > 0) ?? path;
    public static string SanitizeStem(string value)
    {
        var forbidden = new HashSet<char>(Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*"));
        var result = new string(value.Select(character => char.IsControl(character) || forbidden.Contains(character) ? '_' : character).ToArray()).Trim(' ', '.');
        if (result.Length > 72) result = result[..72].TrimEnd(' ', '.');
        if (result.Length == 0) result = "asset";
        var device = result.Split('.')[0];
        if (device.Equals("CON", StringComparison.OrdinalIgnoreCase) || device.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || device.Equals("AUX", StringComparison.OrdinalIgnoreCase) || device.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (device.Length == 4 && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && device[3] is >= '1' and <= '9')) result = "asset_" + result;
        return result;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
