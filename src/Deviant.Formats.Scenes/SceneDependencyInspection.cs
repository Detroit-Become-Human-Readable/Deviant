using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public sealed record DependencyWords(uint Word0, uint Word1, uint Word2, uint Word3, uint Word4);
public sealed record DependencyCandidate(DependencyWords Words, ResourceReference Candidate,
    ReferenceResolution TopLevelMatch, SourceExtent Extent)
{
    public AssetRelationship Relationship => new(Candidate.Owner!, AssetRelationshipKind.Dependency,
        "Resource candidate", Candidate, TopLevelMatch,
        new(false, "Dependency table", "Words 0 and 1 correlate with archive type and ID fields.", Extent));
}
public sealed record SceneDependencyInspection(ResourceIdentity Owner, uint OwnerType, uint OwnerId,
    uint TableRows, long TableReferences, SourceExtent OwnerExtent,
    IReadOnlyList<DependencyCandidate> References);
public sealed record DependencyOwnerMatch(ResourceIdentity Owner, uint OwnerType, uint OwnerId,
    SourceExtent OwnerExtent, int TotalMatchingReferences, bool MatchingReferencesTruncated,
    IReadOnlyList<DependencyCandidate> References);
public sealed record DependencyOwnerPage(SourceRevision Revision, SourceExtent DependencyExtent,
    string DependencySha256, uint TableRows, long TableReferences,
    IReadOnlyList<DependencyOwnerMatch> Owners, bool HasMore, long? NextOrdinal);
public sealed record SceneDependencyLimits(int MaximumBytes = 64 * 1024 * 1024,
    int MaximumRows = 1_000_000, int MaximumReferences = 2_000_000,
    int MaximumSelectedReferences = 65_536, int MaximumMatchingReferencesPerOwner = 65_536);
