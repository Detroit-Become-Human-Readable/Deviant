using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json.Serialization;
using Deviant.Assets;

namespace Deviant.Formats.Textures;

public sealed record IblFloat32(uint Bits, DecodedExtent Extent)
{
    public float GetValue() => BitConverter.UInt32BitsToSingle(Bits);
    public bool IsFinite => float.IsFinite(GetValue());
}

public sealed record IblByte(byte Value, DecodedExtent Extent)
{
    public bool IsNonzero => Value != 0;
}

public sealed record IblUInt32(uint Value, DecodedExtent Extent);

/// <summary>Six stored words in xmin, xmax, ymin, ymax, zmin, zmax order.</summary>
public sealed record IblStoredBounds(IblFloat32 MinimumX, IblFloat32 MaximumX,
    IblFloat32 MinimumY, IblFloat32 MaximumY, IblFloat32 MinimumZ, IblFloat32 MaximumZ)
{
    public bool IsFinite => MinimumX.IsFinite && MaximumX.IsFinite && MinimumY.IsFinite &&
        MaximumY.IsFinite && MinimumZ.IsFinite && MaximumZ.IsFinite;
    public bool IsOrdered => IsFinite && MinimumX.GetValue() <= MaximumX.GetValue() &&
        MinimumY.GetValue() <= MaximumY.GetValue() && MinimumZ.GetValue() <= MaximumZ.GetValue();
    public bool HasPositiveVolume => IsOrdered && MinimumX.GetValue() < MaximumX.GetValue() &&
        MinimumY.GetValue() < MaximumY.GetValue() && MinimumZ.GetValue() < MaximumZ.GetValue();

    public bool TryGetBounds([NotNullWhen(true)] out GeometryBounds? bounds)
    {
        bounds = IsOrdered ? new GeometryBounds(
            new Vector3(MinimumX.GetValue(), MinimumY.GetValue(), MinimumZ.GetValue()),
            new Vector3(MaximumX.GetValue(), MaximumY.GetValue(), MaximumZ.GetValue())) : null;
        return bounds is not null;
    }
}

/// <summary>Stored IBLLIGHT v13 fields and their source locations.</summary>
public sealed record IblLightStoredFields(ResourceIdentity Identity, SourceRevision Revision,
    DecodedExtent Extent, IblByte Enabled, IblStoredBounds ExtentBounds, IblStoredBounds LocalBoxBounds,
    TextureContextReference StoredReference, IblFloat32 AttenuationStart, IblFloat32 Intensity,
    IblFloat32 Scalar77, IblByte SpecularTexturesEnabled, IblByte Byte82, IblFloat32 Scalar83,
    IblUInt32 TextureReferenceCount, IblUInt32 Word91, IReadOnlyList<TextureContextReference> TextureReferences,
    IblUInt32 FirstSpecularSetIndex, TextureContextString OwnerName, TextureContextString SourcePath,
    IblFloat32 TrailingScalar0, IblFloat32 TrailingScalar1,
    IReadOnlyList<SourceExtent> OriginalPhysicalExtents,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawFixedFields83,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawTrailingFields8)
{
    public ResourceReference? GetStoredResourceReference() => Bind(StoredReference);
    public ResourceReference? GetTextureResourceReference(int index) => Bind(TextureReferences[index]);

    private ResourceReference? Bind(TextureContextReference reference) => reference.IsBound
        ? new ResourceReference(IdentifierNamespace.Native, reference.Id.ToString(CultureInfo.InvariantCulture),
            Identity, reference.Type)
        : null;
}
