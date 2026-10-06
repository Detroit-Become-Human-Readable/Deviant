using Deviant.Assets;
using Deviant.Formats.Containers;

namespace Deviant.Formats.Scenes;

/// <summary>Reads a bounded graph section with scene globals and catalog instances.</summary>
public sealed class SceneSectionReader
{
    public const string Version = "native-scene-section-v1";
    public const string MemberPath = "scene-section";

    public DecodeResult<SceneSectionDocument> Read(DecodeResult<NativeObjectDirectoryDocument> input,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(input); token.ThrowIfCancellationRequested();
        var identity = input.Identity with { MemberPath = MemberPath };
        DecodeResult<SceneSectionDocument> Fail(string code) => new(null, identity, input.Revision, input.Extents,
            [.. input.Steps, Step()], [code], input.UnresolvedReferences, input.UnknownFields);
        if (input.Value is not { } directory) return Fail("SceneDirectoryUnavailable");
        if (directory.Identity != input.Identity || directory.Revision != input.Revision)
            return Fail("SceneDirectorySourceMismatch");
        var environments = directory.Objects.Where(frame => frame.NativeType == SceneEnvironmentReader.NativeType).ToArray();
        if (environments.Length != 1) return Fail(environments.Length == 0 ? "SceneEnvironmentMissing" : "SceneEnvironmentAmbiguous");
        if (!new SceneEnvironmentReader().TryRead(environments[0], out var environment, out var diagnostic, token))
            return Fail(diagnostic!);
        var records = new List<SceneNodeRecord>();
        var entities = new List<SceneEntityRecord>();
        var instances = new List<SceneCatalogInstance>();
        var diagnostics = new List<SceneStructureDiagnostic>();
        var nodeReader = new NodeRecordReader();
        var entityReader = new SceneEntityReader();
        var instanceReader = new SceneCatalogInstanceReader();
        foreach (var frame in directory.Objects)
        {
            token.ThrowIfCancellationRequested();
            if (frame.NativeType == NodeRecordReader.NativeType)
            {
                var node = nodeReader.Read(frame, input.Revision, input.Extents, token: token);
                if (node.Value is { } value) records.Add(value);
                else foreach (var code in node.Diagnostics) diagnostics.Add(new(code, frame.Identity));
            }
            else if (entityReader.TryRead(frame, out var entity, out var entityDiagnostic, token))
            {
                entities.Add(entity!);
                if (frame.NativeType == SceneCatalogInstanceReader.NativeType)
                {
                    if (instanceReader.TryRead(frame, entity!, out var instance, out diagnostic, token)) instances.Add(instance!);
                    else diagnostics.Add(new(diagnostic!, frame.Identity));
                }
            }
            else if (frame.NativeType is 2006 or 2009 or SceneCatalogInstanceReader.NativeType or 2211)
                diagnostics.Add(new(entityDiagnostic ?? "EntityProfileUnsupported", frame.Identity));
        }
        if (records.Count == 0) return Fail("SceneNodesUnavailable");
        if (instances.Count == 0) return Fail("SceneCatalogInstancesUnavailable");
        var resolver = new NativeReferenceResolver(directory);
        var nodeIds = records.Select(record => record.Identity).ToHashSet();
        if (!instances.Any(instance => instance.Entity.Node is { } reference &&
                resolver.Resolve(reference) is { Status: ReferenceStatus.Unique, Candidates.Count: 1 } resolved &&
                nodeIds.Contains(resolved.Candidates[0])))
            return Fail("SceneInstancePlacementUnavailable");
        var graph = new SceneHierarchyBuilder().Build(directory, records, token: token);
        diagnostics.AddRange(graph.Diagnostics);
        var unresolved = new List<ResourceReference>();
        foreach (var entity in entities)
        {
            token.ThrowIfCancellationRequested();
            if (entity.Node is { } reference && resolver.Resolve(reference) is not { Status: ReferenceStatus.Unique })
            { unresolved.Add(reference); diagnostics.Add(new("EntityNodeUnresolved", entity.Identity, "Node")); }
        }
        foreach (var instance in instances)
        {
            token.ThrowIfCancellationRequested();
            if (resolver.Resolve(instance.Catalog) is not { Status: ReferenceStatus.Unique })
            { unresolved.Add(instance.Catalog); diagnostics.Add(new("InstanceCatalogUnresolved", instance.Identity, "Catalog")); }
        }
        var section = new SceneSectionDocument(identity, input.Identity, input.Revision, graph, entities, instances,
            environment!, directory.Objects, diagnostics.ToArray());
        return new(section, identity, input.Revision, input.Extents, [.. input.Steps, Step()],
            diagnostics.Select(item => item.Code).Distinct().ToArray(), unresolved, input.UnknownFields);
    }

    private static DecodeStep Step() => new(nameof(SceneSectionReader), Version,
        $"{NodeRecordReader.Version};{SceneEntityReader.Version};{SceneCatalogInstanceReader.Version};{SceneEnvironmentReader.Version}");
}
