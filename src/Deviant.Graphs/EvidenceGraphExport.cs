using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Deviant.Assets;

namespace Deviant.Graphs;

public static partial class EvidenceGraphExport
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task WriteJsonAsync(EvidenceGraph graph, SourceRevision revision, Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(destination);
        var document = ToDocument(graph, revision);
        await JsonSerializer.SerializeAsync(destination, document, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteSvgAsync(EvidenceGraph graph, SourceRevision revision, Stream destination,
        int maximumNodes = 250, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(destination);
        if (maximumNodes < 1) throw new ArgumentOutOfRangeException(nameof(maximumNodes));

        var nodes = graph.Nodes.OrderBy(node => SafeText(node.Label), StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => SafeIdentifier(node.NodeId), StringComparer.Ordinal).Take(maximumNodes).ToArray();
        var included = nodes.Select(node => node.NodeId).ToHashSet(StringComparer.Ordinal);
        var edges = graph.Edges.Where(edge => included.Contains(edge.SourceNodeId) && edge.TargetNodeIds.Any(included.Contains)).ToArray();
        const int width = 1000;
        const int rowHeight = 42;
        const int top = 96;
        var height = Math.Max(160, top + nodes.Length * rowHeight + 24);
        var xByNode = nodes.Select((node, index) => (node.NodeId, X: 40 + index % 2 * 500, Y: top + index / 2 * rowHeight))
            .ToDictionary(item => item.NodeId, item => (item.X, item.Y), StringComparer.Ordinal);

        var settings = new XmlWriterSettings { Async = true, Encoding = new System.Text.UTF8Encoding(false), Indent = true, CloseOutput = false };
        using var writer = XmlWriter.Create(destination, settings);
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        await writer.WriteStartElementAsync(null, "svg", SvgNamespace).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "viewBox", null, $"0 0 {width} {height}").ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "role", null, "img").ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "aria-labelledby", null, "title description").ConfigureAwait(false);
        await writer.WriteStartElementAsync(null, "title", SvgNamespace).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "id", null, "title").ConfigureAwait(false);
        var domain = SafeText(graph.Domain.ToString());
        var indexContentId = SafeIdentifier(revision.IndexContentId);
        var sourceStamp = SafeText(revision.PhysicalSourceStamp);
        await writer.WriteStringAsync($"{domain} graph").ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteStartElementAsync(null, "desc", SvgNamespace).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "id", null, "description").ConfigureAwait(false);
        await writer.WriteStringAsync($"Source index {indexContentId}; {nodes.Length} nodes shown, {edges.Length} edges shown.").ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteStartElementAsync(null, "rect", SvgNamespace).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "width", null, "100%").ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "height", null, "100%").ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "fill", null, "#f7f8fa").ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteStartElementAsync(null, "text", SvgNamespace).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "x", null, "24").ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "y", null, "32").ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "font-family", null, "Segoe UI, sans-serif").ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "font-size", null, "18").ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "fill", null, "#1f2937").ConfigureAwait(false);
        await writer.WriteStringAsync($"{domain} · {nodes.Length} nodes · {edges.Length} links").ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteStartElementAsync(null, "text", SvgNamespace).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "x", null, "24").ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "y", null, "56").ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "font-family", null, "Segoe UI, sans-serif").ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "font-size", null, "11").ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "fill", null, "#536273").ConfigureAwait(false);
        await writer.WriteStringAsync($"Index {indexContentId} · Source {sourceStamp}").ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);

        foreach (var edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = xByNode[edge.SourceNodeId];
            foreach (var targetId in edge.TargetNodeIds.Where(included.Contains))
            {
                var target = xByNode[targetId];
                await writer.WriteStartElementAsync(null, "path", SvgNamespace).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "d", null,
                    $"M {source.X + 220} {source.Y + 14} C {source.X + 300} {source.Y + 14}, {target.X - 80} {target.Y + 14}, {target.X} {target.Y + 14}").ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "fill", null, "none").ConfigureAwait(false);
                var verifiedUnique = edge.Relationship.Evidence.Verified && edge.Relationship.Resolution.Status == ReferenceStatus.Unique;
                await writer.WriteAttributeStringAsync(null, "stroke", null, verifiedUnique ? "#526f8e" : "#c18a42").ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "stroke-width", null, "1.5").ConfigureAwait(false);
                if (!verifiedUnique)
                    await writer.WriteAttributeStringAsync(null, "stroke-dasharray", null, "5 4").ConfigureAwait(false);
                await writer.WriteStartElementAsync(null, "title", SvgNamespace).ConfigureAwait(false);
                await writer.WriteStringAsync(SafeText($"{edge.Relationship.Kind}: {edge.Relationship.Label} · {edge.Relationship.Reference.Namespace} {edge.Relationship.Reference.Id} · {edge.Relationship.Resolution.Status} · {edge.Relationship.Evidence.Description}")).ConfigureAwait(false);
                await writer.WriteEndElementAsync().ConfigureAwait(false);
                await writer.WriteEndElementAsync().ConfigureAwait(false);
            }
        }

        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = xByNode[node.NodeId];
            var placeholder = node.Identity is null;
            await writer.WriteStartElementAsync(null, "g", SvgNamespace).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "data-node-id", null, SafeIdentifier(node.NodeId)).ConfigureAwait(false);
            await writer.WriteStartElementAsync(null, "title", SvgNamespace).ConfigureAwait(false);
            await writer.WriteStringAsync(SafeText($"{node.Namespace} · {node.Family ?? "Reference"} · {SafeIdentifier(node.ReferenceId ?? node.Identity?.Ordinal.ToString() ?? node.NodeId)} · {node.Status?.ToString() ?? "Indexed"}")).ConfigureAwait(false);
            await writer.WriteEndElementAsync().ConfigureAwait(false);
            await writer.WriteStartElementAsync(null, "rect", SvgNamespace).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "x", null, position.X.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "y", null, position.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "width", null, "220").ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "height", null, "28").ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "rx", null, "5").ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "fill", null, placeholder ? "#fff7e6" : "#ffffff").ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "stroke", null, placeholder ? "#c18a42" : "#bdc7d3").ConfigureAwait(false);
            if (placeholder) await writer.WriteAttributeStringAsync(null, "stroke-dasharray", null, "4 3").ConfigureAwait(false);
            await writer.WriteEndElementAsync().ConfigureAwait(false);
            await writer.WriteStartElementAsync(null, "text", SvgNamespace).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "x", null, (position.X + 9).ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "y", null, (position.Y + 18).ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "font-family", null, "Segoe UI, sans-serif").ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "font-size", null, "12").ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "fill", null, "#243244").ConfigureAwait(false);
            await writer.WriteStringAsync(Truncate(SafeText(node.Label), 31)).ConfigureAwait(false);
            await writer.WriteEndElementAsync().ConfigureAwait(false);
            await writer.WriteEndElementAsync().ConfigureAwait(false);
        }

        if (graph.Nodes.Count > maximumNodes)
        {
            await writer.WriteStartElementAsync(null, "text", SvgNamespace).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "x", null, "24").ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "y", null, (height - 12).ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "font-family", null, "Segoe UI, sans-serif").ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "font-size", null, "11").ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "fill", null, "#536273").ConfigureAwait(false);
            await writer.WriteStringAsync($"Showing first {maximumNodes} nodes. Export JSON for the complete graph.").ConfigureAwait(false);
            await writer.WriteEndElementAsync().ConfigureAwait(false);
        }

        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static ExportDocument ToDocument(EvidenceGraph graph, SourceRevision revision) => new(
        "deviant-evidence-graph-v1", SafeText(graph.Domain.ToString()),
        new(SafeIdentifier(revision.IndexContentId), SafeText(revision.PhysicalSourceStamp)),
        graph.Nodes.Select(node => new ExportNode(SafeIdentifier(node.NodeId), SafeIdentity(node.Identity), SafeText(node.Label),
            SafeText(node.Namespace), SafeIdentifier(node.ReferenceId), node.Status, SafeText(node.Reason), SafeText(node.Family),
            node.SourceExtents.Select(extent => new ExportSourceExtent(SafeRelativeSource(extent.RelativeSource), extent.Offset, extent.Length, SafeText(extent.PayloadSha256))).ToArray(),
            node.Metadata.Select(pair => new ExportMetadata(SafeText(pair.Key), SafeText(pair.Value))).ToArray())).ToArray(),
        graph.Edges.Select(edge => new ExportEdge(SafeIdentifier(edge.EdgeId), SafeIdentifier(edge.SourceNodeId), edge.TargetNodeIds.Select(SafeIdentifier).ToArray(),
            edge.Relationship.Kind.ToString(), SafeText(edge.Relationship.Label), edge.Relationship.Reference.Namespace.ToString(),
            SafeIdentifier(edge.Relationship.Reference.Id), edge.Relationship.Reference.ResourceType,
            edge.Relationship.Resolution.Status, edge.Relationship.Resolution.Candidates.Select(identity => SafeIdentity(identity)!).ToArray(),
            SafeText(edge.Relationship.Resolution.Reason), edge.Relationship.Evidence.Verified,
            SafeText(edge.Relationship.Evidence.Source), SafeText(edge.Relationship.Evidence.Description),
            edge.Relationship.Evidence.PhysicalExtent is { } physical
                ? new ExportSourceExtent(SafeRelativeSource(physical.RelativeSource), physical.Offset, physical.Length, SafeText(physical.PayloadSha256)) : null,
            edge.Relationship.Evidence.DecodedExtent is { } decoded
                ? new ExportDecodedExtent(SafeIdentity(decoded.Container)!, decoded.Offset, decoded.Length) : null,
            edge.CanNavigate)).ToArray());

    private static ResourceIdentity? SafeIdentity(ResourceIdentity? identity) => identity is null ? null : identity with
    {
        IndexContentId = SafeIdentifier(identity.IndexContentId),
        MemberPath = identity.MemberPath is null ? null : SafeIdentifier(identity.MemberPath)
    };

    private static string SafeRelativeSource(string source)
    {
        if (Path.IsPathRooted(source) || WindowsPathPattern().IsMatch(source) || UnixPathPattern().IsMatch(source))
            return source.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "[local path]";
        return SafeText(source);
    }

    private static string SafeText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        if (Path.IsPathRooted(value)) return "[local path]";
        return WindowsPathPattern().Replace(UnixPathPattern().Replace(value, "[local path]"), "[local path]");
    }

    private static string SafeIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        if (Path.IsPathRooted(value) || WindowsPathPattern().IsMatch(value) || UnixPathPattern().IsMatch(value))
            return "opaque:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..24];
        return SafeText(value);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    [GeneratedRegex("(?<![A-Za-z0-9])[A-Za-z]:[\\\\/][^,;|\\r\\n<>\"']+|\\\\\\\\[^,;|\\r\\n<>\"']+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathPattern();

    [GeneratedRegex("(?<![A-Za-z0-9:/])/(?!/)[^,;|\\r\\n<>\"']+", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPathPattern();

    private sealed record ExportDocument(string Schema, string Domain, ExportRevision SourceRevision,
        IReadOnlyList<ExportNode> Nodes, IReadOnlyList<ExportEdge> Edges);
    private sealed record ExportRevision(string IndexContentId, string PhysicalSourceStamp);
    private sealed record ExportNode(string NodeId, ResourceIdentity? Identity, string Label, string Namespace,
        string? ReferenceId, ReferenceStatus? Status, string? Reason, string? Family,
        IReadOnlyList<ExportSourceExtent> SourceExtents, IReadOnlyList<ExportMetadata> Metadata);
    private sealed record ExportEdge(string EdgeId, string SourceNodeId, IReadOnlyList<string> TargetNodeIds,
        string Kind, string Label, string ReferenceNamespace, string ReferenceId, uint? ResourceType,
        ReferenceStatus Status, IReadOnlyList<ResourceIdentity> Candidates, string? ResolutionReason,
        bool Verified, string EvidenceSource, string EvidenceDescription, ExportSourceExtent? PhysicalExtent,
        ExportDecodedExtent? DecodedExtent, bool CanNavigate);
    private sealed record ExportSourceExtent(string RelativeSource, long Offset, long Length, string? PayloadSha256);
    private sealed record ExportDecodedExtent(ResourceIdentity Container, long Offset, long Length);
    private sealed record ExportMetadata(string Key, string Value);
}
