using System.Numerics;

namespace Deviant.Assets;

public enum MaterialTextureRole { Normal, BaseColor, Roughness, Opacity }

public sealed record MaterialTextureBinding(int SlotOrdinal, ResourceReference Texture,
    MaterialTextureRole Role, int TextureCoordinateSet, Vector2 UvScale, Vector2 UvOffset,
    RelationshipEvidence Evidence);

/// <summary>Native parameters used when preparing material draw records.</summary>
public sealed record NativeMaterialRenderParameters(uint ProgramMode, uint StoredBlendSelector,
    uint Flags, uint DepthBiasBits, byte DrawSwitch, DecodedExtent SourceExtent)
{
    public uint RenderOptions { get; init; }
    public byte BlendSelector => unchecked((byte)StoredBlendSelector);
    public float? DepthBias => float.IsFinite(BitConverter.UInt32BitsToSingle(DepthBiasBits))
        ? BitConverter.UInt32BitsToSingle(DepthBiasBits) : null;
}

public sealed record MaterialAsset(ResourceIdentity Identity, SourceRevision Revision, string Profile,
    IReadOnlyList<MaterialTextureBinding> Bindings, IReadOnlyList<ResourceReference> References,
    IReadOnlyList<string> Diagnostics)
{
    public MaterialSurfaceGraph? SurfaceGraph { get; init; }
    public NativeMaterialRenderParameters? NativeRenderParameters { get; init; }
}
