using System.Globalization;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Graphs;

namespace Deviant.Browser;

public static class ArchiveReferenceGraphs
{
    public const string SourceNodeId = "owner";
    public const int MaximumPageReferences = 100;

    public static EvidenceGraph BuildPage(ArchiveSnapshot snapshot, LocalizationReferenceReadResult references,
        string sourceLabel, IReadOnlyList<int> ordinals, int pageOffset, int filteredCount, string filter,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(ordinals);
        if (references.Status != LocalizationReferenceReadStatus.Verified || references.Source.MemberPath is not null ||
            references.Revision != new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp) ||
            references.Source.IndexContentId != snapshot.IndexContentId || Find(snapshot, references.Source.Ordinal) is not { Validity: ResourceValidity.Valid } ||
            references.ReferenceTableSha256 is not { Length: 64 } tableHash || !tableHash.All(Uri.IsHexDigit))
            throw new ArgumentException("Select verified references from this archive.", nameof(references));
        if (ordinals.Count > MaximumPageReferences || ordinals.Count != ordinals.Distinct().Count() ||
            pageOffset < 0 || filteredCount < ordinals.Count || pageOffset > filteredCount || ordinals.Count > filteredCount - pageOffset)
            throw new ArgumentException("The reference page is outside its bounds.", nameof(ordinals));
        token.ThrowIfCancellationRequested();
        var nodes = new List<EvidenceGraphNode>
        {
            new(SourceNodeId, references.Source, sourceLabel, IdentifierNamespace.Archive.ToString(), null, null,
                null, "Resource", [], new Dictionary<string,string>
                {
                    ["profile"] = "archive-reference-page-v1", ["referenceTableSha256"] = references.ReferenceTableSha256 ?? "",
                    ["pageOffset"] = pageOffset.ToString(CultureInfo.InvariantCulture),
                    ["filteredReferences"] = filteredCount.ToString(CultureInfo.InvariantCulture),
                    ["totalReferences"] = references.Relationships.Count.ToString(CultureInfo.InvariantCulture), ["filter"] = filter
                })
        };
        var edges = new List<EvidenceGraphEdge>(ordinals.Count);
        foreach (var ordinal in ordinals)
        {
            token.ThrowIfCancellationRequested();
            if (ordinal < 0 || ordinal >= references.Relationships.Count) throw new ArgumentOutOfRangeException(nameof(ordinals));
            var relationship = references.Relationships[ordinal];
            if (relationship.Source != references.Source || relationship.Reference.Owner != references.Source)
                throw new ArgumentException("A reference belongs to another resource.", nameof(references));
            var target = VerifiedTarget(snapshot, relationship);
            var nodeId = "reference/" + ordinal.ToString(CultureInfo.InvariantCulture);
            nodes.Add(new(nodeId, target, $"Reference {ordinal + 1}", relationship.Reference.Namespace.ToString(),
                relationship.Reference.Id, relationship.Resolution.Status, relationship.Resolution.Reason, "Reference",
                relationship.Evidence.PhysicalExtent is { } extent ? [extent] : [],
                new Dictionary<string,string> { ["rowOrdinal"] = ordinal.ToString(CultureInfo.InvariantCulture),
                    ["candidateCount"] = relationship.Resolution.Candidates.Count.ToString(CultureInfo.InvariantCulture) }));
            edges.Add(new("edge/" + ordinal.ToString(CultureInfo.InvariantCulture), SourceNodeId, [nodeId], relationship, target is not null));
        }
        return new(EvidenceGraphDomain.ArchiveResourceReferences, nodes, edges);
    }

    private static ResourceIdentity? VerifiedTarget(ArchiveSnapshot snapshot, AssetRelationship relationship)
    {
        if (relationship.Reference.Namespace != IdentifierNamespace.Archive || relationship.Reference.ResourceType is not { } type ||
            relationship.NavigationTarget is not { MemberPath: null } target || target.IndexContentId != snapshot.IndexContentId ||
            !uint.TryParse(relationship.Reference.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return null;
        var row = Find(snapshot, target.Ordinal);
        return row is { Validity: ResourceValidity.Valid } && row.Type == type && row.Id == id ? target : null;
    }

    private static ResourceRecord? Find(ArchiveSnapshot snapshot, long ordinal) =>
        ordinal >= 0 && ordinal < snapshot.Records.Count && snapshot.Records[(int)ordinal].Ordinal == ordinal
            ? snapshot.Records[(int)ordinal] : null;
}
