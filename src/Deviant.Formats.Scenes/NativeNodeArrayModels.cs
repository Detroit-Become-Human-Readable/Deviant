using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public sealed record NativeNodeArrayLimits(int MaximumPayloadBytes = 8 * 1024 * 1024,
    int MaximumPhysicalExtents = 8192, int MaximumPrecedingSteps = 128)
{
    public void Validate()
    {
        if (MaximumPayloadBytes is < NativeNodeArrayReader.MinimumPayloadBytes or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes));
        if (MaximumPhysicalExtents is < 0 or > NativeNodeArrayReader.MaximumSupportedPhysicalExtents)
            throw new ArgumentOutOfRangeException(nameof(MaximumPhysicalExtents));
        if (MaximumPrecedingSteps is < 0 or > NativeNodeArrayReader.MaximumSupportedPrecedingSteps)
            throw new ArgumentOutOfRangeException(nameof(MaximumPrecedingSteps));
    }
}

/// <summary>An owned NODEAR frame with its inherited NODE fields and remaining serialized body.</summary>
public sealed record NativeNodeArrayDocument(ResourceIdentity Identity, SourceRevision Revision,
    uint Version, uint BodyVersion, SceneNodeRecord InheritedNode,
    DecodedExtent InheritedPrefixExtent, DecodedExtent ClassHeaderExtent, DecodedExtent BodyExtent,
    IReadOnlyDictionary<string, DecodedExtent> FieldExtents, ReadOnlyMemory<byte> RawPayload)
{
    public ReadOnlyMemory<byte> EncodedBody => RawPayload[NativeNodeArrayReader.BodyOffset..];
    public int RetainedPayloadBytes => RawPayload.Length;
}
