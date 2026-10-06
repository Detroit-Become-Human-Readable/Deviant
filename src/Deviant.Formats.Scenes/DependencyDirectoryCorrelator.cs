using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public sealed record DependencyDirectoryPairMatch(int DependencyOrdinal, DependencyWords Words,
    ReferenceStatus Cardinality, IReadOnlyList<long> DirectoryEntryOrdinals, SourceExtent DependencyExtent,
    IReadOnlyList<DecodedExtent> DirectoryEntryExtents);
public sealed record DependencyDirectoryPairMultiplicity(uint Word0, uint Word1, int DependencyCount, int DirectoryCount);
public sealed record DependencyDirectoryCorrelation(bool SamePairMultiset, bool SamePairOrder,
    IReadOnlyList<DependencyDirectoryPairMatch> Matches, IReadOnlyList<DependencyDirectoryPairMultiplicity> PairCounts);

/// <summary>Compares raw dependency/directory pairs in one owner domain without resolving resource semantics.</summary>
public sealed class DependencyDirectoryCorrelator
{
    public const string Version = "owner-directory-pairs-1";
    public DecodeResult<DependencyDirectoryCorrelation> Compare(DecodeResult<SceneDependencyInspection> dependencies,
        DecodeResult<DcInfoDirectoryInspection> directory, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(dependencies); ArgumentNullException.ThrowIfNull(directory);
        if (dependencies.Identity != directory.Identity || dependencies.Revision != directory.Revision)
            throw new ArgumentException("Dependency and directory data must have the same owner and source revision.", nameof(directory));
        var dep = dependencies.Value ?? throw new ArgumentException("Dependency data is unavailable.", nameof(dependencies));
        var members = directory.Value ?? throw new ArgumentException("Directory data is unavailable.", nameof(directory));
        ArgumentNullException.ThrowIfNull(dep.References); ArgumentNullException.ThrowIfNull(members.Entries);
        if (dep.Owner != dependencies.Identity || dep.References.Any(r => r.Candidate.Owner != dependencies.Identity) || members.Extent.Container != directory.Identity ||
            members.Entries.Any(e => e.Extent.Container != directory.Identity))
            throw new ArgumentException("The record extents do not belong to the supplied owner.");
        token.ThrowIfCancellationRequested();
        var groups = members.Entries.GroupBy(e => (e.Word0, e.Word1)).ToDictionary(g => g.Key, g => g.ToArray());
        var dependencyCounts = dep.References.GroupBy(r => (r.Words.Word0, r.Words.Word1)).ToDictionary(g => g.Key, g => g.Count());
        var matches = new DependencyDirectoryPairMatch[dep.References.Count];
        for (var ordinal = 0; ordinal < dep.References.Count; ordinal++)
        {
            if ((ordinal & 1023) == 0) token.ThrowIfCancellationRequested();
            var reference = dep.References[ordinal]; var entries = groups.GetValueOrDefault((reference.Words.Word0, reference.Words.Word1)) ?? [];
            matches[ordinal] = new(ordinal, reference.Words,
                entries.Length switch { 0 => ReferenceStatus.Missing, 1 => ReferenceStatus.Unique, _ => ReferenceStatus.Ambiguous },
                entries.Select(e => e.Ordinal).ToArray(), reference.Extent, entries.Select(e => e.Extent).ToArray());
        }
        var sameMultiset = dependencyCounts.Count == groups.Count && dependencyCounts.All(pair => groups.TryGetValue(pair.Key, out var entries) && entries.Length == pair.Value);
        var sameOrder = dep.References.Select(r => (r.Words.Word0, r.Words.Word1)).SequenceEqual(members.Entries.Select(e => (e.Word0, e.Word1)));
        var pairCounts = dependencyCounts.Keys.Union(groups.Keys).OrderBy(pair => pair.Word0).ThenBy(pair => pair.Word1)
            .Select(pair => new DependencyDirectoryPairMultiplicity(pair.Word0, pair.Word1,
                dependencyCounts.GetValueOrDefault(pair), groups.GetValueOrDefault(pair)?.Length ?? 0)).ToArray();
        token.ThrowIfCancellationRequested();
        return new(new(sameMultiset, sameOrder, matches, pairCounts), dependencies.Identity, dependencies.Revision,
            dependencies.Extents.Concat(directory.Extents).Distinct().ToArray(),
            [.. dependencies.Steps, .. directory.Steps, new(nameof(DependencyDirectoryCorrelator), Version, "raw-pair-comparison; owner-local")], [],
            dependencies.UnresolvedReferences, new Dictionary<string, string> { ["relationshipKind"] = "Unknown", ["targetIdentity"] = "Unassigned" });
    }
}
