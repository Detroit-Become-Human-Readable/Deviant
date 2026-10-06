using System.Numerics;

namespace Deviant.Assets;

public sealed record SceneNodeRecord(ResourceIdentity Identity, SourceRevision Revision, uint NativeId,
    uint Flags, Vector3 Pivot, SourceTransform StoredLocalTransform, SourceTransform StoredWorldTransform,
    ResourceReference? Parent, ResourceReference? FirstChild, ResourceReference? NextSibling,
    int ParentAttachmentIndex, uint Word172, uint Word176, DecodedExtent PayloadExtent,
    IReadOnlyDictionary<string, DecodedExtent> FieldExtents, ReadOnlyMemory<byte> RawPayload);

public enum SceneTransformStatus
{
    Available, Invalid, MissingParent, AmbiguousParent, UnsupportedParent, Cycle, UnsupportedAttachment,
    ParentUnavailable, DepthLimit
}

public sealed record SceneStructureDiagnostic(string Code, ResourceIdentity Identity, string? Field = null);

public sealed record SceneHierarchyNode(SceneNodeRecord Record,
    ReferenceResolution? ParentResolution, ReferenceResolution? FirstChildResolution,
    ReferenceResolution? NextSiblingResolution, ResourceIdentity? ParentIdentity,
    IReadOnlyList<ResourceIdentity> Children, SourceTransform? LocalTransform,
    SourceTransform? WorldTransform, SceneTransformStatus TransformStatus)
{
    public ResourceIdentity Identity => Record.Identity;
}

/// <summary>Recovered node relationships, independent of scene or level classification.</summary>
public sealed record SceneHierarchy(ResourceIdentity Container, SourceRevision Revision,
    IReadOnlyList<SceneHierarchyNode> Nodes, IReadOnlyList<ResourceIdentity> Roots,
    IReadOnlyList<SceneStructureDiagnostic> Diagnostics);

public sealed record SceneEntityRecord(ResourceIdentity Identity, uint NativeType, uint NativeId,
    ResourceReference? Node, IReadOnlyList<ResourceReference> OtherReferences,
    uint StoredWord, byte StoredFlag, uint ClassCount, int NextClassOffset,
    DecodedExtent EntityExtent, IReadOnlyDictionary<string, DecodedExtent> FieldExtents);

public sealed record SceneCatalogInstance(ResourceIdentity Identity, SceneEntityRecord Entity,
    string InstanceName, string CatalogName, ResourceReference Catalog,
    IReadOnlyDictionary<string, DecodedExtent> FieldExtents);

public sealed record SceneEnvironmentRecord(ResourceIdentity Identity, uint Version,
    DecodedExtent Extent, IReadOnlyList<ResourceReference> References, ReadOnlyMemory<byte> RawPayload);

public sealed record SceneSectionDocument(ResourceIdentity Identity, ResourceIdentity SourceContainer, SourceRevision Revision,
    SceneHierarchy Hierarchy, IReadOnlyList<SceneEntityRecord> Entities,
    IReadOnlyList<SceneCatalogInstance> CatalogInstances, SceneEnvironmentRecord Environment,
    IReadOnlyList<NativeObjectFrame> Objects, IReadOnlyList<SceneStructureDiagnostic> Diagnostics);
