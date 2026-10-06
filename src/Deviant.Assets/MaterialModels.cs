using System.Numerics;

namespace Deviant.Assets;

public enum MaterialTextureRole { Normal, BaseColor, Roughness, Opacity }

public sealed record MaterialTextureBinding(int SlotOrdinal, ResourceReference Texture,
    MaterialTextureRole Role, int TextureCoordinateSet, Vector2 UvScale, Vector2 UvOffset,
    RelationshipEvidence Evidence);

public sealed record MaterialAsset(ResourceIdentity Identity, SourceRevision Revision, string Profile,
    IReadOnlyList<MaterialTextureBinding> Bindings, IReadOnlyList<ResourceReference> References,
    IReadOnlyList<string> Diagnostics)
{
    public MaterialSurfaceGraph? SurfaceGraph { get; init; }
}
