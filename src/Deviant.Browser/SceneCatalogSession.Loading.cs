using System.Globalization;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Containers;
using Deviant.Formats.Scenes;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

public sealed partial class SceneCatalogSession
{
    private readonly LocalizationReferenceReader archiveReferences = new();

    private AssetDescriptor Describe(DecodeResult<SceneSectionDocument> result, SceneSectionDocument section)
    {
        var context = section.CatalogInstances.Select(instance => instance.CatalogName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? $"Section {Snapshot.Records[(int)section.SourceContainer.Ordinal].Id:X8}";
        var names = section.CatalogInstances.SelectMany(instance => new[]
        {
            new NameEvidence(instance.CatalogName, NameOrigin.Verified, "ENTCATLG catalog context: " + instance.Identity.Key),
            new NameEvidence(instance.InstanceName, NameOrigin.Verified, "ENTCATLG instance context: " + instance.Identity.Key)
        }).Where(name => !string.IsNullOrWhiteSpace(name.Text)).ToArray();
        var descriptor = new AssetDescriptor(section.Identity, Revision, AssetFamily, context, null,
            AssetAvailability.Available, AssetCompleteness.Partial, AssetCapabilities.Json, names, result.Extents,
            SortKey: section.SourceContainer.Ordinal.ToString("D12", CultureInfo.InvariantCulture))
        {
            SearchTerms = string.Join(' ', names.Select(name => name.Text)),
            Metadata = new Dictionary<string, string>
            {
                ["scene.context"] = context, ["scene.kind"] = "Section",
                ["scene.nodes"] = section.Hierarchy.Nodes.Count.ToString(CultureInfo.InvariantCulture),
                ["scene.roots"] = section.Hierarchy.Roots.Count.ToString(CultureInfo.InvariantCulture),
                ["scene.entities"] = section.Entities.Count.ToString(CultureInfo.InvariantCulture),
                ["scene.instances"] = section.CatalogInstances.Count.ToString(CultureInfo.InvariantCulture),
                ["scene.objects"] = section.Objects.Count.ToString(CultureInfo.InvariantCulture),
                ["scene.reader"] = SceneSectionReader.Version
            }
        };
        return ApplyAlias(descriptor);
    }

    private AssetDescriptor ApplyAlias(AssetDescriptor descriptor)
    {
        var alias = aliases.Get(descriptor.Identity);
        var names = descriptor.Names.Where(name => name.Origin != NameOrigin.UserAlias).ToList();
        if (alias is not null) names.Add(new(alias, NameOrigin.UserAlias, "User alias"));
        return descriptor with
        {
            Label = alias ?? descriptor.Metadata.GetValueOrDefault("scene.context") ?? descriptor.Label,
            Names = names,
            SearchTerms = string.Join(' ', names.Select(name => name.Text))
        };
    }

    private async Task<SceneLoadedSection> EnrichAsync(AssetDescriptor descriptor, DecodedSection decoded, CancellationToken token)
    {
        using var timing = AssetLoadDiagnostics.Measure("SceneEnrichment", descriptor.Identity.Key);
        var section = decoded.Result.Value!;
        var directory = decoded.Directory!;
        var resolver = new NativeReferenceResolver(directory);
        var frames = directory.Objects.ToDictionary(frame => frame.Identity);
        var nodes = section.Hierarchy.Nodes.ToDictionary(node => node.Identity);
        var names = new Dictionary<ResourceIdentity, List<NameEvidence>>();
        var entities = new Dictionary<ResourceIdentity, List<SceneEntityRecord>>();
        var relationships = new Dictionary<ResourceIdentity, List<AssetRelationship>>();
        var lights = new Dictionary<ResourceIdentity, SceneIblLight>();
        var lightTextureResolutions = new Dictionary<(uint Type, uint Id), ReferenceResolution>();
        var extents = decoded.Result.Extents.ToList();
        foreach (var node in section.Hierarchy.Nodes)
        {
            token.ThrowIfCancellationRequested();
            AddNodeLink(node, node.Record.Parent, "Parent", "Parent");
            AddNodeLink(node, node.Record.FirstChild, "First child", "FirstChild");
            AddNodeLink(node, node.Record.NextSibling, "Next sibling", "NextSibling");
            if (aliases.Get(node.Identity) is { } alias) AddName(node.Identity, new(alias, NameOrigin.UserAlias, "User alias"));
        }
        var instances = section.CatalogInstances.ToDictionary(instance => instance.Identity);
        foreach (var entity in section.Entities)
        {
            token.ThrowIfCancellationRequested();
            TextureContextString? lightName = null;
            var frame = frames[entity.Identity];
            if (frame.NativeType == 2211 && entity.ClassCount == 4 && entity.NextClassOffset < frame.RawPayload.Length)
            {
                var context = new TextureContextReader().ParseIblLightV13(frame.RawPayload.Span,
                    entity.NextClassOffset, frame.RawPayload.Length, frame.Identity, Revision, frame.PayloadExtent,
                    decoded.Result.Extents, token);
                lightName = context.Value?.OwnerName;
                var projected = context.Value is { } stored
                    ? IblLightStoredFieldsProjection.Project(stored, token)
                    : new DecodeResult<IblLightStoredFields>(null, context.Identity, context.Revision, context.Extents,
                        context.Steps, context.Diagnostics, context.UnresolvedReferences, context.UnknownFields);
                var resolutions = new List<ReferenceResolution>();
                if (projected.Value is { } fields)
                {
                    for (var slot = 0; slot < fields.TextureReferences.Count; slot++)
                    {
                        token.ThrowIfCancellationRequested();
                        var storedReference = fields.TextureReferences[slot];
                        var pair = (storedReference.Type, storedReference.Id);
                        if (!lightTextureResolutions.TryGetValue(pair, out var resolution))
                        {
                            resolution = fields.GetTextureResourceReference(slot) is { } referenceToTexture
                                ? resolver.Resolve(referenceToTexture)
                                : new ReferenceResolution(ReferenceStatus.Unresolved, [], "Stored pair is unbound.");
                            resolution = resolution with { Candidates = Array.AsReadOnly(resolution.Candidates.ToArray()) };
                            lightTextureResolutions.Add(pair, resolution);
                        }
                        resolutions.Add(resolution);
                    }
                }
                lights.Add(entity.Identity, new(projected, resolutions.AsReadOnly()));
            }
            if (entity.Node is not { } reference || resolver.Resolve(reference) is not
                { Status: ReferenceStatus.Unique, Candidates.Count: 1 } nodeResolution || !nodes.ContainsKey(nodeResolution.Candidates[0])) continue;
            var nodeId = nodeResolution.Candidates[0];
            if (!entities.TryGetValue(nodeId, out var list)) entities[nodeId] = list = [];
            list.Add(entity);
            if (aliases.Get(entity.Identity) is { } alias) AddName(nodeId, new(alias, NameOrigin.UserAlias, "Entity alias"));
            if (instances.TryGetValue(entity.Identity, out var instance))
            {
                var label = string.IsNullOrWhiteSpace(instance.InstanceName) ? instance.CatalogName : instance.InstanceName;
                AddName(nodeId, new(label, NameOrigin.Stored, "ENTCATLG v2: " + instance.Identity.Key));
                var binding = await BindCatalogAsync(nodeId, instance, directory, token).ConfigureAwait(false);
                AddRelationship(nodeId, binding.Relationship);
                if (binding.Extent is not null) extents.Add(binding.Extent);
            }
            if (lightName is not null && !string.IsNullOrWhiteSpace(lightName.Text))
                AddName(nodeId, new(lightName.Text, NameOrigin.Stored,
                    $"{TextureContextReader.Version}: {lightName.ValueExtent.Offset}+{lightName.ValueExtent.Length}"));
        }
        var result = decoded.Result with { Extents = extents.Distinct().ToArray() };
        return new(descriptor, result,
            names.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<NameEvidence>)pair.Value.AsReadOnly()),
            entities.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<SceneEntityRecord>)pair.Value.AsReadOnly()),
            relationships.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<AssetRelationship>)pair.Value.AsReadOnly()))
        {
            IblLightsByEntity = new System.Collections.ObjectModel.ReadOnlyDictionary<ResourceIdentity, SceneIblLight>(lights)
        };

        void AddName(ResourceIdentity node, NameEvidence name)
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return;
            if (!names.TryGetValue(node, out var values)) names[node] = values = [];
            if (!values.Contains(name)) values.Add(name);
        }
        void AddRelationship(ResourceIdentity node, AssetRelationship relationship)
        {
            if (!relationships.TryGetValue(node, out var values)) relationships[node] = values = [];
            values.Add(relationship);
        }
        void AddNodeLink(SceneHierarchyNode node, ResourceReference? reference, string label, string field)
        {
            if (reference is null) return;
            AddRelationship(node.Identity, new(node.Identity, AssetRelationshipKind.Entity, label, reference,
                resolver.Resolve(reference), new(true, NodeRecordReader.Version, "Stored NODE relationship.",
                    DecodedExtent: node.Record.FieldExtents[field])));
        }
    }

    private async Task<(AssetRelationship Relationship, SourceExtent? Extent)> BindCatalogAsync(ResourceIdentity node,
        SceneCatalogInstance instance, NativeObjectDirectoryDocument directory, CancellationToken token)
    {
        var reference = instance.Catalog with { Namespace = IdentifierNamespace.Archive };
        var resolution = source.Resolve(reference);
        var evidence = new RelationshipEvidence(false, SceneCatalogInstanceReader.Version,
            "Catalog reference awaits matching source metadata.", DecodedExtent: instance.FieldExtents["Catalog"]);
        var relationship = new AssetRelationship(node, AssetRelationshipKind.Container, "Catalog", reference, resolution, evidence);
        var native = new NativeReferenceResolver(directory).Resolve(instance.Catalog);
        if (native is not { Status: ReferenceStatus.Unique, Candidates.Count: 1 } ||
            resolution is not { Status: ReferenceStatus.Unique, Candidates.Count: 1 }) return (relationship, null);
        var frame = directory.Objects.Single(item => item.Identity == native.Candidates[0]);
        if (!new NativeCommonPrefixReader().TryRead(frame, out var prefix, out _, token) ||
            prefix!.ClassCount != 2 || prefix.NextClassOffset != frame.RawPayload.Length) return (relationship, null);
        var target = resolution.Candidates[0];
        var row = Snapshot.Records[(int)target.Ordinal];
        if (row.Validity != ResourceValidity.Valid) return (relationship with
        { Resolution = new(ReferenceStatus.Missing, resolution.Candidates, "The catalog source is unavailable.") }, null);
        var table = await archiveReferences.ReadAsync(Snapshot, target, token).ConfigureAwait(false);
        if (table.Status != LocalizationReferenceReadStatus.Verified || table.Relationships.Count != prefix.References.Count)
            return (relationship, null);
        for (var index = 0; index < prefix.References.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var stored = prefix.References[index]; var actual = table.Relationships[index];
            if (stored.Type != actual.Reference.ResourceType || stored.Id.ToString(CultureInfo.InvariantCulture) != actual.Reference.Id ||
                stored.Trailer != actual.Evidence.SourceReferenceTrailer) return (relationship, null);
        }
        var extent = new SourceExtent(row.VolumeName, row.Offset, checked(20 + table.Relationships.Count * 9), table.ReferenceTableSha256);
        return (relationship with { Evidence = evidence with
        { Verified = true, PhysicalExtent = extent, Description = "The stored catalog reference and member table match the archive catalog." } }, extent);
    }
}
