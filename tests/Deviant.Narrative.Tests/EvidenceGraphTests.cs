using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Deviant.Assets;
using Deviant.Graphs;
using Xunit;

namespace Deviant.Narrative.Tests;

public sealed class EvidenceGraphTests
{
    private static readonly SourceRevision Revision = new("index-content-id", "physical-source-stamp");

    [Fact]
    public void MissingReferenceNodesUseNamespaceQualifiedIdentity()
    {
        var source = new ResourceIdentity("index-content-id", 7);
        var relationships = new[]
        {
            Relationship(source, IdentifierNamespace.Bank, "42", ReferenceStatus.Missing),
            Relationship(source, IdentifierNamespace.Hirc, "42", ReferenceStatus.Missing)
        };

        var graph = RelationshipGraphBuilder.Build(EvidenceGraphDomain.ArchiveResourceReferences, [], relationships);
        var reordered = RelationshipGraphBuilder.Build(EvidenceGraphDomain.ArchiveResourceReferences, [], relationships.Reverse());
        var placeholders = graph.Nodes.Where(node => node.Identity is null).ToArray();

        Assert.Equal(2, placeholders.Length);
        Assert.Equal(2, placeholders.Select(node => node.NodeId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(placeholders.Select(node => node.NodeId).Order(StringComparer.Ordinal),
            reordered.Nodes.Where(node => node.Identity is null).Select(node => node.NodeId).Order(StringComparer.Ordinal));
        Assert.Single(graph.Nodes, node => node.Identity == source);
        Assert.Contains(placeholders, node => node.Namespace == nameof(IdentifierNamespace.Bank) && node.ReferenceId == "42");
        Assert.Contains(placeholders, node => node.Namespace == nameof(IdentifierNamespace.Hirc) && node.ReferenceId == "42");
        Assert.All(graph.Edges, edge => Assert.False(edge.CanNavigate));
    }

    [Fact]
    public void AmbiguousRelationshipRetainsEveryCandidateAndDisablesNavigation()
    {
        var source = new ResourceIdentity("index-content-id", 10);
        var candidates = new[]
        {
            new ResourceIdentity("index-content-id", 11),
            new ResourceIdentity("index-content-id", 11),
            new ResourceIdentity("index-content-id", 12)
        };
        var relationship = Relationship(source, IdentifierNamespace.Archive, "11", ReferenceStatus.Ambiguous, candidates, verified: true);

        var graph = RelationshipGraphBuilder.Build(EvidenceGraphDomain.ArchiveResourceReferences, [], [relationship]);
        var edge = Assert.Single(graph.Edges);

        Assert.Equal(ReferenceStatus.Ambiguous, edge.Relationship.Resolution.Status);
        Assert.Equal(2, edge.TargetNodeIds.Count);
        Assert.All(edge.TargetNodeIds, targetId => Assert.Contains(graph.Nodes, node => node.NodeId == targetId));
        Assert.False(edge.CanNavigate);
    }

    [Fact]
    public void DomainIsExplicitAndIndependentOfRelationshipKind()
    {
        var source = new ResourceIdentity("index-content-id", 2);
        var relationship = Relationship(source, IdentifierNamespace.Media, "90", ReferenceStatus.Missing);

        var resourceGraph = RelationshipGraphBuilder.Build(EvidenceGraphDomain.ArchiveResourceReferences, [], [relationship]);
        var playbackGraph = RelationshipGraphBuilder.Build(EvidenceGraphDomain.WwisePlayback, [], [relationship]);

        Assert.Equal(EvidenceGraphDomain.ArchiveResourceReferences, resourceGraph.Domain);
        Assert.Equal(EvidenceGraphDomain.WwisePlayback, playbackGraph.Domain);
        Assert.Equal(resourceGraph.Edges.Single().Relationship, playbackGraph.Edges.Single().Relationship);
    }

    [Fact]
    public void NodeIdsDoNotDependOnTheLocalRootOfAnAbsoluteMemberPath()
    {
        var root = Path.GetTempPath();
        var first = new ResourceIdentity("index-content-id", 15, Path.GetFullPath(Path.Combine(root, "first", "member.bin")));
        var second = new ResourceIdentity("index-content-id", 15, Path.GetFullPath(Path.Combine(root, "second", "member.bin")));
        var firstGraph = RelationshipGraphBuilder.Build(EvidenceGraphDomain.ArchiveResourceReferences, [],
            [Relationship(first, IdentifierNamespace.Archive, "16", ReferenceStatus.Missing)]);
        var secondGraph = RelationshipGraphBuilder.Build(EvidenceGraphDomain.ArchiveResourceReferences, [],
            [Relationship(second, IdentifierNamespace.Archive, "16", ReferenceStatus.Missing)]);

        Assert.Equal(firstGraph.Nodes.Single(node => node.Identity == first).NodeId,
            secondGraph.Nodes.Single(node => node.Identity == second).NodeId);
    }

    [Fact]
    public async Task JsonExportPreservesUnicodeEvidenceAndOmitsAbsolutePaths()
    {
        var source = new ResourceIdentity("index-content-id", 3);
        var localPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "private-volume.dat"));
        var asset = new AssetDescriptor(source, Revision, "Texture", "Café texture", null,
            AssetAvailability.Available, AssetCompleteness.Unverified, AssetCapabilities.Original,
            Array.Empty<NameEvidence>(), [new SourceExtent(localPath, 100, 20)])
        { Metadata = new Dictionary<string, string> { ["display"] = "Café texture" } };
        var relationship = Relationship(source, IdentifierNamespace.Archive, "8", ReferenceStatus.Missing,
            evidenceSource: localPath, physical: new SourceExtent(localPath, 100, 20));
        var graph = RelationshipGraphBuilder.Build(EvidenceGraphDomain.ArchiveResourceReferences, [asset], [relationship]);
        await using var output = new MemoryStream();

        await EvidenceGraphExport.WriteJsonAsync(graph, Revision, output);
        using var json = JsonDocument.Parse(output.ToArray());

        Assert.Equal(nameof(EvidenceGraphDomain.ArchiveResourceReferences), json.RootElement.GetProperty("Domain").GetString());
        Assert.Equal("index-content-id", json.RootElement.GetProperty("SourceRevision").GetProperty("IndexContentId").GetString());
        Assert.Contains(json.RootElement.GetProperty("Nodes").EnumerateArray(), node => node.GetProperty("Label").GetString() == "Café texture");
        Assert.DoesNotContain(localPath, Encoding.UTF8.GetString(output.ToArray()), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("private-volume.dat", Encoding.UTF8.GetString(output.ToArray()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SvgExportCarriesDomainRevisionAndUnicodeLabels()
    {
        var source = new ResourceIdentity("index-content-id", 3);
        var asset = new AssetDescriptor(source, Revision, "Texture", "Café", null,
            AssetAvailability.Available, AssetCompleteness.Unverified, AssetCapabilities.Original,
            Array.Empty<NameEvidence>(), Array.Empty<SourceExtent>());
        var graph = RelationshipGraphBuilder.Build(EvidenceGraphDomain.ArchiveResourceReferences, [asset], []);
        await using var output = new MemoryStream();

        await EvidenceGraphExport.WriteSvgAsync(graph, Revision, output);
        output.Position = 0;
        var svg = XDocument.Load(output);
        XNamespace ns = "http://www.w3.org/2000/svg";

        var text = svg.Descendants(ns + "text").Select(element => element.Value).ToArray();
        Assert.Contains(text, value => value.Contains("ArchiveResourceReferences", StringComparison.Ordinal));
        Assert.Contains(text, value => value.Contains("physical-source-stamp", StringComparison.Ordinal));
        Assert.Contains(text, value => value.Contains("Café", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JsonAndSvgExportsRedactWindowsAndPosixPathsFromCallerSuppliedFields()
    {
        var windowsPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "private root", "volume.bin"));
        const string posixPath = "/var/tmp/narrative/private-shader.glsl";
        var source = new ResourceIdentity(windowsPath, 21, windowsPath);
        var target = new ResourceIdentity("index-content-id", 22, posixPath);
        var relationship = new AssetRelationship(source, AssetRelationshipKind.Dependency, $"uses {posixPath}",
            new(IdentifierNamespace.Archive, windowsPath, source, 0x7D5),
            new(ReferenceStatus.Ambiguous, [target], posixPath),
            new(true, windowsPath, posixPath, new SourceExtent(posixPath, 16, 64), new DecodedExtent(source, 32, 64)));
        var graph = new EvidenceGraph(EvidenceGraphDomain.ArchiveResourceReferences,
            [
                new(windowsPath, source, $"Windows {windowsPath}", "Archive", posixPath, null, null, null,
                    [new SourceExtent(windowsPath, 0, 8)], new Dictionary<string, string> { [windowsPath] = posixPath }),
                new(posixPath, target, $"POSIX {posixPath}", "Archive", windowsPath, null, null, null,
                    Array.Empty<SourceExtent>(), new Dictionary<string, string>())
            ],
            [new(posixPath, windowsPath, [posixPath], relationship, false)]);
        var revision = new SourceRevision(windowsPath, posixPath);

        await using var jsonOutput = new MemoryStream();
        await EvidenceGraphExport.WriteJsonAsync(graph, revision, jsonOutput);
        var jsonText = Encoding.UTF8.GetString(jsonOutput.ToArray());
        Assert.DoesNotContain(windowsPath, jsonText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(posixPath, jsonText, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(jsonOutput.ToArray());
        var exportedIds = json.RootElement.GetProperty("Nodes").EnumerateArray()
            .Select(node => node.GetProperty("NodeId").GetString()).ToArray();
        Assert.Equal(2, exportedIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(exportedIds, id => Assert.StartsWith("opaque:", id));

        await using var svgOutput = new MemoryStream();
        await EvidenceGraphExport.WriteSvgAsync(graph, revision, svgOutput);
        var svgText = Encoding.UTF8.GetString(svgOutput.ToArray());
        Assert.DoesNotContain(windowsPath, svgText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(posixPath, svgText, StringComparison.Ordinal);
    }

    private static AssetRelationship Relationship(ResourceIdentity source, IdentifierNamespace targetNamespace,
        string id, ReferenceStatus status, IReadOnlyList<ResourceIdentity>? candidates = null,
        bool verified = false, string evidenceSource = "dependency-record", SourceExtent? physical = null) =>
        new(source, AssetRelationshipKind.Dependency, "reference", new(targetNamespace, id, source, 0x7D5),
            new(status, candidates ?? Array.Empty<ResourceIdentity>(), "target unavailable"),
            new(verified, evidenceSource, "typed reference observation", physical));
}
