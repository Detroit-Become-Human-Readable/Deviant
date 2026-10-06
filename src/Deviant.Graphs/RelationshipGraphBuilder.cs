using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Deviant.Assets;

namespace Deviant.Graphs;

public static partial class RelationshipGraphBuilder
{
    public static EvidenceGraph Build(
        EvidenceGraphDomain domain,
        IEnumerable<AssetDescriptor> assets,
        IEnumerable<AssetRelationship> relationships)
    {
        var nodes = new Dictionary<string, EvidenceGraphNode>(StringComparer.Ordinal);
        var descriptors = assets.ToDictionary(asset => asset.Identity);

        foreach (var asset in descriptors.Values)
            nodes[NodeId(asset.Identity)] = new(NodeId(asset.Identity), asset.Identity,
                string.IsNullOrWhiteSpace(asset.Label) ? asset.Identity.Ordinal.ToString() : asset.Label,
                asset.Family, null, null, asset.Reason, asset.Family,
                asset.Extents.ToArray(), new Dictionary<string, string>(asset.Metadata));

        var edges = new List<EvidenceGraphEdge>();
        foreach (var relationship in relationships)
        {
            var sourceId = NodeId(relationship.Source);
            if (!nodes.ContainsKey(sourceId))
                nodes[sourceId] = IdentityNode(relationship.Source);

            var targets = new List<string>();
            if (relationship.Resolution.Candidates.Count > 0)
            {
                foreach (var candidate in relationship.Resolution.Candidates.Distinct())
                {
                    var targetId = NodeId(candidate);
                    targets.Add(targetId);
                    if (!nodes.ContainsKey(targetId))
                        nodes[targetId] = descriptors.TryGetValue(candidate, out var asset)
                            ? AssetNode(asset)
                            : IdentityNode(candidate);
                }
            }
            else
            {
                var targetId = ReferenceNodeId(relationship);
                targets.Add(targetId);
                if (!nodes.ContainsKey(targetId))
                    nodes[targetId] = new(targetId, null, relationship.Reference.Id,
                        relationship.Reference.Namespace.ToString(), relationship.Reference.Id,
                        relationship.Resolution.Status, relationship.Resolution.Reason, null,
                        Array.Empty<SourceExtent>(), new Dictionary<string, string>());
            }

            var canNavigate = relationship.NavigationTarget is not null;
            edges.Add(new(EdgeId(relationship, sourceId, targets), sourceId,
                targets.Distinct(StringComparer.Ordinal).ToArray(), relationship, canNavigate));
        }

        return new(domain, nodes.Values.ToArray(), edges);
    }

    private static EvidenceGraphNode AssetNode(AssetDescriptor asset) => new(
        NodeId(asset.Identity), asset.Identity,
        string.IsNullOrWhiteSpace(asset.Label) ? asset.Identity.Ordinal.ToString() : asset.Label,
        IdentifierNamespace.Archive.ToString(), null, null, asset.Reason, asset.Family,
        asset.Extents.ToArray(), new Dictionary<string, string>(asset.Metadata));

    private static EvidenceGraphNode IdentityNode(ResourceIdentity identity) => new(
        NodeId(identity), identity,
        identity.MemberPath ?? identity.Ordinal.ToString(),
        IdentifierNamespace.Archive.ToString(), null, null, null, null,
        Array.Empty<SourceExtent>(), new Dictionary<string, string>());

    private static string NodeId(ResourceIdentity identity) =>
        "resource:" + Hash(IdentityKey(identity));

    private static string ReferenceNodeId(AssetRelationship relationship)
    {
        var reference = relationship.Reference;
        var owner = reference.Owner is { } identity ? IdentityKey(identity) : IdentityKey(relationship.Source);
        return "reference:" + Hash($"{reference.Namespace}\0{reference.ResourceType}\0{StableText(reference.Id)}\0{owner}");
    }

    private static string EdgeId(AssetRelationship relationship, string sourceId, IEnumerable<string> targetIds)
    {
        var targets = string.Join("\0", targetIds.Order(StringComparer.Ordinal));
        var reference = relationship.Reference;
        var evidence = relationship.Evidence;
        var physical = evidence.PhysicalExtent is { } p ? $"{StablePath(p.RelativeSource)}\0{p.Offset}\0{p.Length}\0{p.PayloadSha256}" : string.Empty;
        var decoded = evidence.DecodedExtent is { } d ? $"{IdentityKey(d.Container)}\0{d.Offset}\0{d.Length}" : string.Empty;
        return "edge:" + Hash($"{sourceId}\0{relationship.Kind}\0{StableText(relationship.Label)}\0{reference.Namespace}\0{reference.ResourceType}\0{StableText(reference.Id)}\0{targets}\0{StableText(evidence.Source)}\0{StableText(evidence.Description)}\0{physical}\0{decoded}");
    }

    private static string IdentityKey(ResourceIdentity identity) =>
        $"{identity.IndexContentId}\0{identity.Ordinal}\0{StablePath(identity.MemberPath)}";

    private static string StablePath(string? value) => string.IsNullOrEmpty(value) ? string.Empty :
        Path.IsPathRooted(value) ? Path.GetFileName(value) : StableText(value);

    private static string StableText(string? value) => string.IsNullOrEmpty(value) ? string.Empty :
        AbsolutePathPattern().Replace(value, "[local path]");

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];

    [GeneratedRegex("(?<![A-Za-z0-9])[A-Za-z]:[\\\\/][^\\s\"']+|\\\\\\\\[^\\s\"']+", RegexOptions.CultureInvariant)]
    private static partial Regex AbsolutePathPattern();
}
