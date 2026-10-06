using System.Text.RegularExpressions;
using Deviant.Assets;
using Deviant.Formats.Localization.Model;

namespace Deviant.Browser;

public enum LocalizationPlaceholderSyntax { Braces, DollarBraces, Printf }
public enum LocalizationReferenceLinkStatus { Linked, Unmatched, Ambiguous, Unresolved, Unverified, TargetResourceNotSampled }

public sealed record LocalizationLanguageSummary(string RawLanguageCode, long Occurrences, long TextOccurrences,
    long PointerOccurrences, long OtherOccurrences);

/// <summary>A group formed only from the exact prefix stored in localization keys.</summary>
public sealed record LocalizationKeyPrefixGroup(string StoredPrefix, int SegmentCount, long Occurrences,
    int DistinctKeys, IReadOnlyList<string> Languages, IReadOnlyList<string> SampleKeys);

/// <summary>A placeholder token at its original UTF-16 string offset in one source occurrence.</summary>
public sealed record LocalizationPlaceholderOccurrence(ResourceIdentity Identity, string Key, string RawLanguageCode,
    LocalizationPlaceholderSyntax Syntax, string Token, int Utf16Offset);

public sealed record LocalizationPlaceholderSummary(LocalizationPlaceholderSyntax Syntax, string Token, long Occurrences);

public sealed record LocalizationDiscoveryOptions(int PrefixDepth = 1, int MaximumPrefixGroups = 250,
    int SamplesPerPrefix = 3, int MaximumPlaceholderOccurrences = 20_000);

public sealed record LocalizationDiscoveryResult(long Occurrences,
    IReadOnlyDictionary<string, long> KindCounts,
    IReadOnlyDictionary<string, long> StatusCounts,
    IReadOnlyList<LocalizationLanguageSummary> Languages,
    IReadOnlyList<LocalizationKeyPrefixGroup> KeyPrefixes,
    IReadOnlyList<LocalizationPlaceholderSummary> PlaceholderSummary,
    IReadOnlyList<LocalizationPlaceholderOccurrence> PlaceholderOccurrences,
    long OmittedPlaceholderOccurrences);

public sealed record LocalizationReferenceLink(AssetRelationship Relationship,
    LocalizationReferenceLinkStatus Status, IReadOnlyList<LocalizationRecord> Matches);

public sealed record LocalizationRelatedTarget(ResourceIdentity LocalizationResource,
    ResourceIdentity? DialogueHeader, uint? TargetResourceType, ResourceIdentity? TargetResource,
    LocalizationReferenceLinkStatus Status, IReadOnlyList<AssetRelationship> EvidencePath);

public sealed record LocalizationPointerReferenceLink(LocalizationRecord Occurrence, byte PayloadPrefix,
    uint TargetResourceType, uint TargetResourceId, AssetRelationship? Relationship,
    LocalizationReferenceLinkStatus Status, ResourceIdentity? TargetResource);

public sealed record LocalizationPointerRelatedTarget(LocalizationRecord Occurrence,
    ResourceIdentity? DialogueHeader, uint? TargetResourceType, ResourceIdentity? TargetResource,
    LocalizationReferenceLinkStatus Status, IReadOnlyList<AssetRelationship> EvidencePath);

public static partial class LocalizationDiscovery
{
    private static readonly Regex PlaceholderPattern = BuildPlaceholderRegex();

    /// <summary>
    /// Summarizes stored keys, raw language blocks and text placeholders without deduplicating occurrences.
    /// Prefixes are exact leading key text ending at a separator; they are not assigned narrative meanings.
    /// </summary>
    public static LocalizationDiscoveryResult Analyze(IEnumerable<LocalizationRecord> records,
        LocalizationDiscoveryOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var accumulator = new StreamingAccumulator(options);
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            accumulator.Add(record, cancellationToken);
        }
        return accumulator.Snapshot();
    }

    /// <summary>Inspects placeholders for one selected complete text occurrence.</summary>
    public static IReadOnlyList<LocalizationPlaceholderOccurrence> InspectPlaceholders(LocalizationRecord record,
        int maximumTokens = 256)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (maximumTokens is < 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(maximumTokens));
        if (maximumTokens == 0 || record.Kind != LocalizationRecordKind.Text ||
            record.Status != LocalizationRecordStatus.Complete || record.Text is null)
            return Array.Empty<LocalizationPlaceholderOccurrence>();

        var results = new List<LocalizationPlaceholderOccurrence>(Math.Min(maximumTokens, 8));
        foreach (Match match in PlaceholderPattern.Matches(record.Text))
        {
            if (results.Count >= maximumTokens) break;
            results.Add(CreatePlaceholderOccurrence(record, match));
        }
        return results.AsReadOnly();
    }

    private static LocalizationPlaceholderOccurrence CreatePlaceholderOccurrence(LocalizationRecord record, Match match)
    {
        var token = match.Value;
        var syntax = token.StartsWith("${", StringComparison.Ordinal)
            ? LocalizationPlaceholderSyntax.DollarBraces
            : token.StartsWith('{') ? LocalizationPlaceholderSyntax.Braces : LocalizationPlaceholderSyntax.Printf;
        return new(record.Identity, record.Key ?? string.Empty, record.RawLanguageCode, syntax, token, match.Index);
    }

    /// <summary>
    /// Links records only through verified, unique archive references to type-1016 resources.
    /// Names and matching key text are never used as relationship evidence.
    /// </summary>
    public static IReadOnlyList<LocalizationReferenceLink> ResolveDirectReferences(
        IEnumerable<LocalizationRecord> records, IEnumerable<AssetRelationship> relationships)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(relationships);
        var recordsByResource = records.Where(record => record.ResourceType == 1016)
            .GroupBy(record => record.ResourceIdentity.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var links = new List<LocalizationReferenceLink>();
        foreach (var relationship in relationships)
        {
            if (relationship.Reference.ResourceType != 1016) continue;
            if (!relationship.Evidence.Verified)
            {
                links.Add(new(relationship, LocalizationReferenceLinkStatus.Unverified, Array.Empty<LocalizationRecord>()));
                continue;
            }
            if (relationship.Resolution.Status == ReferenceStatus.Ambiguous)
            {
                links.Add(new(relationship, LocalizationReferenceLinkStatus.Ambiguous, Array.Empty<LocalizationRecord>()));
                continue;
            }
            if (relationship.Resolution.Status == ReferenceStatus.Missing)
            {
                links.Add(new(relationship, LocalizationReferenceLinkStatus.Unmatched, Array.Empty<LocalizationRecord>()));
                continue;
            }
            if (relationship.Resolution.Status != ReferenceStatus.Unique || relationship.Resolution.Candidates.Count != 1)
            {
                links.Add(new(relationship, LocalizationReferenceLinkStatus.Unresolved, Array.Empty<LocalizationRecord>()));
                continue;
            }

            var target = relationship.Resolution.Candidates[0];
            if (recordsByResource.TryGetValue(target.Key, out var matches))
                links.Add(new(relationship, LocalizationReferenceLinkStatus.Linked, matches));
            else
                links.Add(new(relationship, LocalizationReferenceLinkStatus.TargetResourceNotSampled, Array.Empty<LocalizationRecord>()));
        }
        return links.AsReadOnly();
    }

    /// <summary>
    /// Follows only stored type-1016-to-type-4091 and type-4091-to-type-1033/1031 edges.
    /// The result is resource-level; it does not assign individual localization keys to events.
    /// </summary>
    public static IReadOnlyList<LocalizationRelatedTarget> ResolveDialogueAudioTargets(
        IEnumerable<ResourceIdentity> localizationResources, IEnumerable<AssetRelationship> relationships,
        IEnumerable<ResourceIdentity> inspectedDialogueHeaders)
    {
        ArgumentNullException.ThrowIfNull(localizationResources);
        ArgumentNullException.ThrowIfNull(relationships);
        ArgumentNullException.ThrowIfNull(inspectedDialogueHeaders);
        var edges = relationships.ToArray();
        var inspected = inspectedDialogueHeaders.Select(identity => identity.Key).ToHashSet(StringComparer.Ordinal);
        var targets = new List<LocalizationRelatedTarget>();

        foreach (var localization in localizationResources.DistinctBy(identity => identity.Key))
        {
            var headerReferences = edges.Where(edge => edge.Source.Key == localization.Key && edge.Reference.ResourceType == 4091)
                .ToArray();
            if (headerReferences.Length == 0)
            {
                targets.Add(new(localization, null, 4091, null, LocalizationReferenceLinkStatus.Unmatched,
                    Array.Empty<AssetRelationship>()));
                continue;
            }

            foreach (var headerReference in headerReferences)
            {
                var headerStatus = Classify(headerReference);
                var header = headerReference.NavigationTarget;
                if (headerStatus != LocalizationReferenceLinkStatus.Linked || header is null)
                {
                    targets.Add(new(localization, null, 4091, null, headerStatus, [headerReference]));
                    continue;
                }
                if (!inspected.Contains(header.Key))
                {
                    targets.Add(new(localization, header, 4091, header,
                        LocalizationReferenceLinkStatus.TargetResourceNotSampled, [headerReference]));
                    continue;
                }

                var downstream = edges.Where(edge => edge.Source.Key == header.Key &&
                    edge.Reference.ResourceType is 1033 or 1031).ToArray();
                if (downstream.Length == 0)
                {
                    targets.Add(new(localization, header, null, null, LocalizationReferenceLinkStatus.Unmatched,
                        [headerReference]));
                    continue;
                }
                foreach (var edge in downstream)
                {
                    var status = Classify(edge);
                    targets.Add(new(localization, header, edge.Reference.ResourceType,
                        status == LocalizationReferenceLinkStatus.Linked ? edge.NavigationTarget : null,
                        status, [headerReference, edge]));
                }
            }
        }
        return targets.AsReadOnly();
    }

    /// <summary>
    /// Resolves a kind-1 pointer only when bytes 1-4 and 5-8 match a verified nine-byte
    /// source-reference edge from the same localization resource. Byte zero is retained as data.
    /// </summary>
    public static IReadOnlyList<LocalizationPointerReferenceLink> ResolvePointerReferences(
        IEnumerable<LocalizationRecord> records, IEnumerable<AssetRelationship> sourceReferences)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(sourceReferences);
        var references = sourceReferences.ToArray();
        var links = new List<LocalizationPointerReferenceLink>();
        foreach (var record in records)
        {
            if (record.Kind != LocalizationRecordKind.Pointer || record.PointerKind != 1 || record.PointerPayloadBase64 is null)
                continue;

            byte[] payload;
            try { payload = Convert.FromBase64String(record.PointerPayloadBase64); }
            catch (FormatException)
            {
                links.Add(new(record, 0, 0, 0, null, LocalizationReferenceLinkStatus.Unresolved, null));
                continue;
            }
            if (payload.Length != 9)
            {
                links.Add(new(record, payload.Length > 0 ? payload[0] : (byte)0, 0, 0, null,
                    LocalizationReferenceLinkStatus.Unresolved, null));
                continue;
            }

            var payloadSpan = payload.AsSpan();
            var targetType = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payloadSpan.Slice(1, 4));
            var targetId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payloadSpan.Slice(5, 4));
            var matches = references.Where(reference => reference.Source.Key == record.ResourceIdentity.Key &&
                reference.Reference.ResourceType == targetType &&
                uint.TryParse(reference.Reference.Id, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var referenceId) && referenceId == targetId &&
                reference.Evidence.PhysicalExtent?.Length == 9 &&
                reference.Evidence.SourceReferenceTrailer == payload[0]).ToArray();
            if (matches.Length == 0)
            {
                links.Add(new(record, payload[0], targetType, targetId, null,
                    LocalizationReferenceLinkStatus.Unmatched, null));
                continue;
            }
            if (matches.Length > 1)
            {
                links.Add(new(record, payload[0], targetType, targetId, null,
                    matches.Any(match => !match.Evidence.Verified)
                        ? LocalizationReferenceLinkStatus.Unverified
                        : LocalizationReferenceLinkStatus.Ambiguous, null));
                continue;
            }

            var relationship = matches[0];
            var status = Classify(relationship);
            links.Add(new(record, payload[0], targetType, targetId, relationship, status,
                status == LocalizationReferenceLinkStatus.Linked ? relationship.NavigationTarget : null));
        }
        return links.AsReadOnly();
    }

    /// <summary>Follows a resolved kind-1 pointer through its header's explicit audio and event references.</summary>
    public static IReadOnlyList<LocalizationPointerRelatedTarget> ResolvePointerDialogueAudioTargets(
        IEnumerable<LocalizationPointerReferenceLink> pointerLinks, IEnumerable<AssetRelationship> relationships,
        IEnumerable<ResourceIdentity> inspectedDialogueHeaders)
    {
        ArgumentNullException.ThrowIfNull(pointerLinks);
        ArgumentNullException.ThrowIfNull(relationships);
        ArgumentNullException.ThrowIfNull(inspectedDialogueHeaders);
        var edges = relationships.ToArray();
        var inspected = inspectedDialogueHeaders.Select(identity => identity.Key).ToHashSet(StringComparer.Ordinal);
        var targets = new List<LocalizationPointerRelatedTarget>();
        foreach (var pointer in pointerLinks)
        {
            var relationship = pointer.Relationship;
            var header = pointer.TargetResourceType == 4091 ? pointer.TargetResource : null;
            if (pointer.Status != LocalizationReferenceLinkStatus.Linked || relationship is null || header is null)
            {
                targets.Add(new(pointer.Occurrence, header, pointer.TargetResourceType, pointer.TargetResource,
                    pointer.Status, relationship is null ? Array.Empty<AssetRelationship>() : [relationship]));
                continue;
            }
            if (!inspected.Contains(header.Key))
            {
                targets.Add(new(pointer.Occurrence, header, 4091, header,
                    LocalizationReferenceLinkStatus.TargetResourceNotSampled, [relationship]));
                continue;
            }

            var downstream = edges.Where(edge => edge.Source.Key == header.Key && edge.Reference.ResourceType is 1033 or 1031)
                .ToArray();
            if (downstream.Length == 0)
            {
                targets.Add(new(pointer.Occurrence, header, null, null, LocalizationReferenceLinkStatus.Unmatched,
                    [relationship]));
                continue;
            }
            foreach (var edge in downstream)
            {
                var status = Classify(edge);
                targets.Add(new(pointer.Occurrence, header, edge.Reference.ResourceType,
                    status == LocalizationReferenceLinkStatus.Linked ? edge.NavigationTarget : null,
                    status, [relationship, edge]));
            }
        }
        return targets.AsReadOnly();
    }

    private static LocalizationReferenceLinkStatus Classify(AssetRelationship relationship)
    {
        if (!relationship.Evidence.Verified) return LocalizationReferenceLinkStatus.Unverified;
        return relationship.Resolution.Status switch
        {
            ReferenceStatus.Unique when relationship.Resolution.Candidates.Count == 1 => LocalizationReferenceLinkStatus.Linked,
            ReferenceStatus.Missing => LocalizationReferenceLinkStatus.Unmatched,
            ReferenceStatus.Ambiguous => LocalizationReferenceLinkStatus.Ambiguous,
            _ => LocalizationReferenceLinkStatus.Unresolved
        };
    }

    /// <summary>Returns the exact leading stored key text through the requested separator depth.</summary>
    public static bool TryGetStoredPrefix(string key, int depth, out string prefix)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (depth is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(depth));
        var separators = 0;
        for (var index = 0; index < key.Length; index++)
        {
            if (!IsSeparator(key[index])) continue;
            separators++;
            if (separators == depth)
            {
                prefix = key[..(index + 1)];
                return index > 0;
            }
        }
        prefix = string.Empty;
        return false;
    }

    private static Regex BuildPlaceholderRegex() => new(
        @"\$\{[^{}\r\n]{1,128}\}|\{[^{}\r\n]{1,128}\}|%(?:\d+\$)?[-+#0 ]*\d*(?:\.\d+)?[a-zA-Z]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static bool IsSeparator(char value) => value is '_' or '.' or '/' or '\\' or ':' or '-';

    private static void Increment(Dictionary<string, long> values, string key) =>
        values[key] = values.GetValueOrDefault(key) + 1;

    private static void ValidateOptions(LocalizationDiscoveryOptions options)
    {
        if (options.PrefixDepth is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(options.PrefixDepth));
        if (options.MaximumPrefixGroups is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(options.MaximumPrefixGroups));
        if (options.SamplesPerPrefix is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(options.SamplesPerPrefix));
        if (options.MaximumPlaceholderOccurrences is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(options.MaximumPlaceholderOccurrences));
    }

    /// <summary>
    /// Accumulates compact discovery metadata as records are already being read. Callers can persist
    /// <see cref="Snapshot"/> once and load the summaries without materializing every text record again.
    /// </summary>
    public sealed class StreamingAccumulator
    {
        private readonly LocalizationDiscoveryOptions options;
        private readonly Dictionary<string, long> kinds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> statuses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LanguageCounter> languages = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PrefixCounter> prefixes = new(StringComparer.Ordinal);
        private readonly Dictionary<(LocalizationPlaceholderSyntax Syntax, string Token), long> placeholderCounts = [];
        private readonly List<LocalizationPlaceholderOccurrence> placeholderOccurrences;
        private long occurrenceCount;
        private long omittedPlaceholderOccurrences;

        public StreamingAccumulator(LocalizationDiscoveryOptions? options = null)
        {
            this.options = options ?? new LocalizationDiscoveryOptions();
            ValidateOptions(this.options);
            placeholderOccurrences = new(Math.Min(this.options.MaximumPlaceholderOccurrences, 4096));
        }

        public long Occurrences => occurrenceCount;

        public void Add(LocalizationRecord? record, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record is null) return;
            occurrenceCount++;
            Increment(kinds, record.Kind.ToString());
            Increment(statuses, record.Status.ToString());

            var language = string.IsNullOrWhiteSpace(record.RawLanguageCode) ? "(empty)" : record.RawLanguageCode;
            if (!languages.TryGetValue(language, out var languageCounter))
                languages.Add(language, languageCounter = new LanguageCounter());
            languageCounter.Occurrences++;
            switch (record.Kind)
            {
                case LocalizationRecordKind.Text: languageCounter.TextOccurrences++; break;
                case LocalizationRecordKind.Pointer: languageCounter.PointerOccurrences++; break;
                default: languageCounter.OtherOccurrences++; break;
            }

            if (!string.IsNullOrEmpty(record.Key) && TryGetStoredPrefix(record.Key, options.PrefixDepth, out var prefix))
            {
                if (!prefixes.TryGetValue(prefix, out var counter)) prefixes.Add(prefix, counter = new PrefixCounter());
                counter.Occurrences++;
                counter.Keys.Add(record.Key);
                counter.Languages.Add(language);
                if (counter.SampleKeys.Count < options.SamplesPerPrefix && counter.SampleKeySet.Add(record.Key))
                    counter.SampleKeys.Add(record.Key);
            }

            if (record.Kind != LocalizationRecordKind.Text || record.Status != LocalizationRecordStatus.Complete ||
                record.Text is null) return;
            foreach (Match match in PlaceholderPattern.Matches(record.Text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var placeholder = CreatePlaceholderOccurrence(record, match);
                var summaryKey = (placeholder.Syntax, placeholder.Token);
                placeholderCounts[summaryKey] = placeholderCounts.GetValueOrDefault(summaryKey) + 1;
                if (placeholderOccurrences.Count < options.MaximumPlaceholderOccurrences)
                    placeholderOccurrences.Add(placeholder);
                else
                    omittedPlaceholderOccurrences++;
            }
        }

        public LocalizationDiscoveryResult Snapshot()
        {
            var prefixGroups = prefixes
                .Select(pair => new LocalizationKeyPrefixGroup(pair.Key, options.PrefixDepth, pair.Value.Occurrences,
                    pair.Value.Keys.Count, pair.Value.Languages.Order(StringComparer.Ordinal).ToArray(),
                    pair.Value.SampleKeys.ToArray()))
                .OrderByDescending(group => group.Occurrences)
                .ThenBy(group => group.StoredPrefix, StringComparer.Ordinal)
                .Take(options.MaximumPrefixGroups)
                .ToArray();
            var languageSummaries = languages
                .Select(pair => new LocalizationLanguageSummary(pair.Key, pair.Value.Occurrences,
                    pair.Value.TextOccurrences, pair.Value.PointerOccurrences, pair.Value.OtherOccurrences))
                .OrderByDescending(summary => summary.Occurrences)
                .ThenBy(summary => summary.RawLanguageCode, StringComparer.Ordinal)
                .ToArray();
            var placeholderSummaries = placeholderCounts
                .Select(pair => new LocalizationPlaceholderSummary(pair.Key.Syntax, pair.Key.Token, pair.Value))
                .OrderByDescending(summary => summary.Occurrences)
                .ThenBy(summary => summary.Syntax)
                .ThenBy(summary => summary.Token, StringComparer.Ordinal)
                .ToArray();

            return new(occurrenceCount, new Dictionary<string, long>(kinds, StringComparer.Ordinal),
                new Dictionary<string, long>(statuses, StringComparer.Ordinal), languageSummaries, prefixGroups,
                placeholderSummaries, placeholderOccurrences.ToArray(), omittedPlaceholderOccurrences);
        }
    }

    private sealed class LanguageCounter
    {
        public long Occurrences;
        public long TextOccurrences;
        public long PointerOccurrences;
        public long OtherOccurrences;
    }

    private sealed class PrefixCounter
    {
        public long Occurrences;
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Languages { get; } = new(StringComparer.Ordinal);
        public HashSet<string> SampleKeySet { get; } = new(StringComparer.Ordinal);
        public List<string> SampleKeys { get; } = [];
    }
}
