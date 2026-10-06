using Deviant.Assets;

namespace Deviant.Graphs;

public enum EvidenceGraphDomain
{
    ArchiveResourceReferences,
    WwisePlayback,
    NarrativeStructure,
    SceneHierarchy,
    Geometry,
    Animation
}

public sealed record EvidenceGraphNode(
    string NodeId,
    ResourceIdentity? Identity,
    string Label,
    string Namespace,
    string? ReferenceId,
    ReferenceStatus? Status,
    string? Reason,
    string? Family,
    IReadOnlyList<SourceExtent> SourceExtents,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record EvidenceGraphEdge(
    string EdgeId,
    string SourceNodeId,
    IReadOnlyList<string> TargetNodeIds,
    AssetRelationship Relationship,
    bool CanNavigate);

/// <summary>A view over recorded assets and relationships. Domain is chosen by the caller.</summary>
public sealed record EvidenceGraph(
    EvidenceGraphDomain Domain,
    IReadOnlyList<EvidenceGraphNode> Nodes,
    IReadOnlyList<EvidenceGraphEdge> Edges)
{
    public IReadOnlyList<EvidenceGraphNode> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Nodes;
        return Nodes.Where(node =>
            node.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            node.Namespace.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (node.ReferenceId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (node.Identity?.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
            node.Metadata.Any(pair => pair.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                pair.Value.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
}
