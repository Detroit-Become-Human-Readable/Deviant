using System.Text.Json.Serialization;
using Deviant.Assets;
using Deviant.Formats.Containers;

namespace Deviant.Formats.Textures;

/// <summary>Bounds for one already-owned Native:2217 PROCTEX v7 frame and its provenance.</summary>
public sealed record NativeProceduralTextureReadLimits(
    int MaximumPayloadBytes = 1024 * 1024,
    int MaximumControllerPayloadBytes = 256 * 1024,
    int MaximumAggregateControllerPayloadBytes = 512 * 1024,
    int MaximumCommonPrefixReferences = 64,
    int MaximumPhysicalSourceExtents = 4096,
    long MaximumSourceBytes = SourceExtentVerifier.DefaultMaximumBytes,
    long MaximumSourceProvenanceBytes = 4 * 1024 * 1024,
    long MaximumProjectionMetadataBytes = 64 * 1024)
{
    public bool IsValid() =>
        MaximumPayloadBytes is >= 108 and <= 64 * 1024 * 1024 &&
        MaximumControllerPayloadBytes >= 0 && MaximumControllerPayloadBytes <= MaximumPayloadBytes &&
        MaximumAggregateControllerPayloadBytes >= 0 && MaximumAggregateControllerPayloadBytes <= MaximumPayloadBytes &&
        MaximumCommonPrefixReferences is >= 0 and <= NativeCommonPrefixReader.MaximumReferences &&
        MaximumPhysicalSourceExtents is >= 0 and <= SourceExtentVerifier.MaximumLargeExtentCount &&
        MaximumSourceBytes is >= 0 and <= SourceExtentVerifier.MaximumBytes &&
        MaximumSourceProvenanceBytes is >= 0 and <= 16L * 1024 * 1024 &&
        MaximumProjectionMetadataBytes is >= 4096 and <= 1024 * 1024;
}

public enum NativeProceduralTextureReferenceState { NoTarget, Typed, UnboundRaw }

/// <summary>Retains the exact serialized native reference pair and, when bound, its typed source reference.</summary>
public sealed record NativeProceduralTextureReferencePair(uint NativeTypeWord, uint NativeIdWord,
    DecodedExtent Extent, NativeProceduralTextureReferenceState State, ResourceReference? Reference);

/// <summary>A length-prefixed controller byte payload and the following unclassified word.</summary>
public sealed record NativeProceduralTextureController(uint PayloadLengthWord, DecodedExtent LengthExtent,
    [property: JsonIgnore] ReadOnlyMemory<byte> PayloadBytes, DecodedExtent PayloadExtent,
    uint RawValueWord, DecodedExtent RawValueExtent);

/// <summary>
/// Source-qualified metadata for a Native:2217 PROCTEX v7 member. Frame and controller byte views borrow
/// the frame allocation; the caller must retain its NativeContainerLease while any raw view is used.
/// Reserve the configured projection and provenance budgets before parsing. AccountedMetadataBytes is a
/// deterministic estimate used for metadata reservation.
/// </summary>
/// <param name="AccountedMetadataBytes">Deterministic estimate of this projection's metadata allocation.</param>
public sealed record NativeProceduralTextureDocument(ResourceIdentity Identity, SourceRevision Revision,
    uint Version, DecodedExtent Extent, NativeCommonPrefix CommonPrefix, DecodedExtent ProfileExtent,
    uint SamplerWord, DecodedExtent SamplerExtent, uint OutputModeWord, DecodedExtent OutputModeExtent,
    NativeProceduralTextureReferencePair TypedReference, NativeProceduralTextureController PowerController,
    IReadOnlyList<NativeProceduralTextureReferencePair> TextureReferences,
    NativeProceduralTextureController SpeedController, IReadOnlyList<SourceExtent> OriginalPhysicalExtents,
    long AccountedMetadataBytes, long SourceProvenanceBytes,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawPayload);
