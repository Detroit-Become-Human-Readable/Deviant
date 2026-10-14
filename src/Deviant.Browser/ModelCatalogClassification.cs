using System.Globalization;
using System.Text.RegularExpressions;
using Deviant.Assets;

namespace Deviant.Browser;

public sealed record ModelCatalogClassificationResult(string Kind, string DisplayLabel,
    string? SourceLabel, string? CharacterKey, string? CharacterName, string? Variant,
    string Evidence);

public sealed record ModelCatalogNameCandidate(string Key, string Name, string? Variant, string SourceLabel);

/// <summary>Projects model categories from typed rig/model links and stored naming evidence.</summary>
public static class ModelCatalogClassification
{
    public const string Profile = "model-classification-v3-rig-node-skin-stream-source-names";
    private static readonly Regex AssemblyName = new(@"^(?<actor>[A-Za-z][A-Za-z0-9]*)[_. -]+(?<variant>.*?)(?:HEAD|BODY|FACE|HAIR|EYES)(?:[_. -]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Tokens = new(@"[A-Z]+(?=[A-Z][a-z]|[^A-Za-z]|$)|[A-Z]?[a-z]+|[0-9]+",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> FacialTerms = new(StringComparer.OrdinalIgnoreCase)
    { "eye", "eyelid", "lid", "brow", "cheek", "jaw", "mouth", "lip", "nose", "facial", "face" };
    private static readonly HashSet<string> LimbTerms = new(StringComparer.OrdinalIgnoreCase)
    { "arm", "forearm", "elbow", "wrist", "hand", "finger", "leg", "thigh", "knee", "calf", "ankle", "foot", "toe" };
    private static readonly HashSet<string> PrefixTerms = new(StringComparer.OrdinalIgnoreCase)
    { "sk", "chr", "ch", "rig", "skel", "skeleton", "character", "model" };

    public static ModelCatalogNameCandidate? SuggestCharacterNameCandidate(string sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(sourceLabel);
        if (sourceLabel.Length > 4096 || TryCharacterLabel(sourceLabel) is not { } candidate ||
            IsNonCharacterActor(candidate.Actor)) return null;
        return CreateCandidate(candidate.Actor, candidate.Variant, candidate.Source);
    }

    /// <summary>Returns source-backed inventory suggestions without promoting them to a character classification.</summary>
    public static IReadOnlyList<ModelCatalogNameCandidate> SuggestInventoryCandidates(
        IReadOnlyList<NameEvidence> storedNames,
        IReadOnlyList<CharacterStoredLabelObservation> groupAssociatedLabels,
        bool sourceSkeletonV15Validated)
    {
        ArgumentNullException.ThrowIfNull(storedNames);
        ArgumentNullException.ThrowIfNull(groupAssociatedLabels);
        var candidates = new List<ModelCatalogNameCandidate>();
        foreach (var name in storedNames)
            if (name.Origin == NameOrigin.Stored && HasExplicitChaMarker(name.Text) &&
                SuggestCharacterNameCandidate(name.Text) is { } candidate)
                candidates.Add(candidate);

        var validLabels = groupAssociatedLabels.Where(item => item.Utf8Status == CharacterLabelUtf8Status.Valid &&
                !string.IsNullOrWhiteSpace(item.Text) && item.Text.Length <= 4096)
            .Select(item => item.Text!).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var label in validLabels)
            if (HasExplicitChaMarker(label) && SuggestCharacterNameCandidate(label) is { } candidate)
                candidates.Add(candidate);

        if (sourceSkeletonV15Validated &&
            CharacterCatalogLabelProjector.CommonDotPrefix(groupAssociatedLabels) is { } commonPrefix &&
            SuggestCommonDotPrefixCandidate(commonPrefix) is { } prefixCandidate)
            candidates.Add(prefixCandidate);

        return candidates.DistinctBy(candidate => (candidate.Key, candidate.Variant, candidate.SourceLabel)).ToArray();
    }

    public static ModelCatalogNameCandidate? ResolveInventoryCandidate(
        IReadOnlyList<ModelCatalogNameCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0 || candidates.Select(candidate => candidate.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1 ||
            candidates.Select(candidate => candidate.Variant ?? "")
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            return null;
        return candidates[0];
    }

    public static ModelCatalogClassificationResult Project(CharacterModelSourceSummary summary,
        IReadOnlyList<CharacterModelBindingResult> bindings)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(bindings);
        var validBindings = bindings.Where(binding => binding.Diagnostic is null &&
            binding.SelectedIdentity == summary.Identity && binding.Revision == summary.Revision &&
            binding.OwnerNativeType == summary.OwnerNativeType && binding.OwnerNativeId == summary.OwnerNativeId).ToArray();
        var linkedLods = validBindings.SelectMany(binding => binding.Lods).Where(ValidModelLink).ToArray();
        var selectedLabels = summary.StoredLabelObservations.Where(observation => linkedLods.Any(lod =>
            lod.MeshDataIdentity == observation.MeshDataIdentity && lod.GroupIndex == observation.GroupOrdinal)).ToArray();
        var names = summary.StoredNameEvidence.Where(name => name.Origin == NameOrigin.Stored).Select(name => name.Text)
            .Concat(selectedLabels.Where(item => item.Utf8Status == CharacterLabelUtf8Status.Valid)
                .Select(item => item.Text ?? ""))
            .Concat(validBindings
                .SelectMany(binding => binding.Rig?.NameEvidence.Select(item => item.Text) ?? []))
            .Where(name => !string.IsNullOrWhiteSpace(name) && name.Length <= 4096)
            .Distinct(StringComparer.Ordinal).ToArray();
        var storedNames = summary.StoredNameEvidence.Where(name => name.Origin == NameOrigin.Stored).Select(name => name.Text)
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToArray();
        var commonPrefix = CharacterCatalogLabelProjector.CommonDotPrefix(selectedLabels);
        var rigPrefix = CommonSourcePrefix(validBindings
            .SelectMany(binding => binding.Rig?.NameEvidence.Select(item => item.Text) ?? []));
        var sourceLabel = storedNames.Length == 1 ? storedNames[0] : commonPrefix;
        var rigged = validBindings.Any(binding => binding.Rig is { Version: 15 });
        var characterRig = validBindings.Any(binding => binding.Rig is
                { Version: 15, NodeBindingValidated: true } &&
            binding.Lods.Any(lod => ValidModelLink(lod) && (lod.HasSkinPalette == true || lod.HasSkinAttributes == true)));
        var explicitNames = names.Select(TryCharacterLabel).Where(item => item is not null)
            .Select(item => item!).ToArray();
        var explicitActors = explicitNames.Select(item => item.Actor).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var deformers = selectedLabels
            .Where(item => item.Utf8Status == CharacterLabelUtf8Status.Valid && item.Text is not null)
            .Select(item => item.Text!).Concat(validBindings.SelectMany(binding =>
                binding.Rig?.NameEvidence.Select(item => item.Text) ?? []));
        var deformerTokens = deformers.Where(text => text.Length <= 4096)
            .SelectMany(SplitTokens).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pairedDeformers = deformerTokens.Overlaps(FacialTerms) && deformerTokens.Overlaps(LimbTerms);
        var isCharacter = characterRig && (explicitActors.Length == 1 || pairedDeformers);
        var kind = isCharacter ? "character" : rigged || linkedLods.Any(lod => lod.HasSkinPalette == true || lod.HasSkinAttributes == true)
            ? "rigged" : linkedLods.Length > 0 && linkedLods.All(lod => lod.HasSkinPalette == false && lod.HasSkinAttributes == false)
                ? "static" : "unclassified";
        string? actor = null;
        string? variant = null;
        if (isCharacter && explicitActors.Length == 1)
        {
            actor = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(explicitNames[0].Actor.ToLowerInvariant());
            variant = explicitNames[0].Variant;
            sourceLabel = explicitNames[0].Source;
        }
        else if (isCharacter)
        {
            var labels = storedNames.Concat(commonPrefix is null ? [] : new[] { commonPrefix })
                .Concat(linkedLods.Select(lod => lod.StoredMeshName ?? ""))
                .Select(label => (Label: label, Match: AssemblyName.Match(RemoveSourcePrefix(label)))).Where(item => item.Match.Success).ToArray();
            if (labels.Select(item => item.Match.Groups["actor"].Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            {
                actor = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(labels[0].Match.Groups["actor"].Value.ToLowerInvariant());
                variant = Friendly(labels[0].Match.Groups["variant"].Value.Trim('_', '.', '-', ' '));
                sourceLabel = labels[0].Label;
            }
            else if ((commonPrefix ?? rigPrefix) is { } actorPrefix)
            {
                var parts = actorPrefix.Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && !PrefixTerms.Contains(parts[0]) && !FacialTerms.Contains(parts[0]) && !LimbTerms.Contains(parts[0]))
                { actor = Friendly(parts[0]); variant = parts.Length > 1 ? Friendly(string.Join(' ', parts.Skip(1))) : null; }
            }
        }
        var fallback = kind == "character" ? $"Character {summary.OwnerNativeId}" :
            kind == "rigged" ? $"Rig {summary.OwnerNativeId}" : $"Model {summary.OwnerNativeId}";
        var display = actor is not null ? actor + (string.IsNullOrWhiteSpace(variant) ? "" : $" · {variant}") :
            sourceLabel is not null ? Friendly(sourceLabel) : fallback;
        var key = isCharacter ? actor is not null ? actor.ToLowerInvariant().Replace(' ', '-') :
            "rig/" + summary.Identity.Key : null;
        return new(kind, display, sourceLabel, key, isCharacter ? actor ?? fallback : null, variant,
            isCharacter ? explicitActors.Length == 1 ? "rig-node+skinned-model+cha-name" :
                "rig-node+skinned-model+facial-limb-deformers" : kind);
    }

    public static string SearchTerms(AssetDescriptor descriptor) => string.Join(' ',
        descriptor.Names.Select(name => name.Text).Prepend(descriptor.Label)
            .Append(descriptor.Identity.Key).Append(descriptor.Identity.MemberPath ?? "")
            .Concat(new[] { "NativeId", "character.key", "character.name", "character.candidate-key",
                "character.candidate-name", "character.candidate-variant", "model.variant", "model.source-label" }
                .Select(key => descriptor.Metadata.GetValueOrDefault(key) ?? ""))
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));

    private static bool ValidModelLink(CharacterMeshLodBinding lod) => lod.Diagnostic is null &&
        lod.Selection is { } selection && lod.MeshDataIdentity == selection.MeshIdentity &&
        lod.GroupIndex == selection.GroupIndex && lod.MeshDataResolution.Status == ReferenceStatus.Unique &&
        lod.MeshDataResolution.Candidates.Count == 1 && lod.MeshDataResolution.Candidates[0] == selection.MeshIdentity;

    private static bool IsDeformer(string text) => text.Length <= 4096 &&
        SplitTokens(text).Any(token => token.Equals("def", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("deform", StringComparison.OrdinalIgnoreCase) || token.Equals("deformer", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> SplitTokens(string text) => Tokens.Matches(text).Select(match => match.Value);

    private static string? CommonSourcePrefix(IEnumerable<string> source)
    {
        var names = source.Where(name => name.Length is >= 4 and <= 4096)
            .Distinct(StringComparer.Ordinal).Take(64).ToArray();
        if (names.Length < 2) return null;
        var length = Math.Min(128, names.Min(name => name.Length));
        for (var index = 0; index < length; index++)
            if (names.Any(name => name[index] != names[0][index])) { length = index; break; }
        var common = names[0][..length];
        var boundary = common.LastIndexOf('.');
        return boundary >= 3 ? common[..boundary] : null;
    }

    private static string RemoveSourcePrefix(string source)
    {
        var value = source.Trim(':', '_', '.', '-', ' ');
        var separators = new[] { ':', '_', '.', '-', ' ' };
        while (true)
        {
            var split = value.IndexOfAny(separators);
            if (split < 0) return value;
            var token = value[..split];
            if (!PrefixTerms.Contains(token) && !token.Equals("CHA", StringComparison.OrdinalIgnoreCase)) return value;
            value = value[(split + 1)..].Trim(':', '_', '.', '-', ' ');
        }
    }

    private static ModelCatalogNameCandidate? SuggestCommonDotPrefixCandidate(string sourcePrefix)
    {
        if (sourcePrefix.Length is < 3 or > 4096) return null;
        var value = RemoveSourcePrefix(sourcePrefix);
        var parts = value.Split(['.', '_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !char.IsLetter(parts[0][0]) || PrefixTerms.Contains(parts[0]) ||
            FacialTerms.Contains(parts[0]) || LimbTerms.Contains(parts[0]) || IsNonCharacterActor(parts[0]))
            return null;
        if (parts[0].Any(character => !char.IsLetterOrDigit(character))) return null;
        var variant = parts.Length > 1 ? Friendly(string.Join(' ', parts.Skip(1))) : null;
        return CreateCandidate(parts[0], variant, sourcePrefix);
    }

    private static ModelCatalogNameCandidate CreateCandidate(string actor, string? variant, string source)
    {
        var name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(actor.ToLowerInvariant());
        return new(name.ToLowerInvariant().Replace(' ', '-'), name, variant, source);
    }

    private static bool IsNonCharacterActor(string actor) => actor.Equals("collidable", StringComparison.OrdinalIgnoreCase) ||
        actor.Equals("collider", StringComparison.OrdinalIgnoreCase) ||
        actor.Equals("collision", StringComparison.OrdinalIgnoreCase);

    private static bool HasExplicitChaMarker(string source)
    {
        var leading = source.TrimStart(':', '_', '.', '-', ' ');
        if (leading.StartsWith("CHA", StringComparison.OrdinalIgnoreCase) && leading.Length > 3 &&
            !char.IsLetterOrDigit(leading[3])) return true;
        var marker = source.IndexOf("_CHA", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 && (marker + 4 == source.Length ||
            !char.IsLetterOrDigit(source[marker + 4]));
    }

    private static SourceCharacterLabel? TryCharacterLabel(string source)
    {
        var leading = source.TrimStart(':', '_', '.', '-', ' ');
        if (leading.StartsWith("CHA", StringComparison.OrdinalIgnoreCase) && leading.Length > 3 &&
            !char.IsLetterOrDigit(leading[3]))
        {
            var match = AssemblyName.Match(RemoveSourcePrefix(leading));
            if (match.Success)
                return new(match.Groups["actor"].Value, Friendly(match.Groups["variant"].Value.Trim('_', '.', '-', ' ')), source);
        }
        var marker = source.IndexOf("_CHA", StringComparison.OrdinalIgnoreCase);
        if (marker < 1 || marker + 4 < source.Length && char.IsLetterOrDigit(source[marker + 4])) return null;
        var prefix = source[..marker].Split(['_', '.', '-', ' ', '/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .SkipWhile(PrefixTerms.Contains).ToArray();
        if (prefix.Length == 0 || !char.IsLetter(prefix[0][0])) return null;
        var tail = source[(marker + 4)..].Trim('_', '.', '-', ' ');
        var variant = string.Join(' ', prefix.Skip(1).Append(tail).Where(value => !string.IsNullOrWhiteSpace(value)));
        return new(prefix[0], variant.Length == 0 ? null : Friendly(variant), source);
    }

    private static string Friendly(string source)
    {
        var words = source.Replace('_', ' ').Replace('.', ' ').Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(word => word.All(char.IsUpper) ? word :
            CultureInfo.InvariantCulture.TextInfo.ToTitleCase(word)));
    }

    private sealed record SourceCharacterLabel(string Actor, string? Variant, string Source);
}
