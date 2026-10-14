using System.Text.Json.Serialization;
using Deviant.Assets;

namespace Deviant.Formats.Narrative;

public sealed record ScriptBundleLimits(
    int MaximumInputBytes = 16 * 1024 * 1024,
    int MaximumBytecodeBytes = 16 * 1024 * 1024,
    int MaximumMetadataPayloadBytes = 16 * 1024 * 1024,
    int MaximumChunks = 65_536,
    int MaximumMetadataRecords = 262_144,
    int MaximumPhysicalSources = 4096,
    int MaximumIdentityCharacters = 4096,
    int MaximumSourceNameCharacters = 4096,
    long MaximumRetainedBytes = 64 * 1024 * 1024);

public sealed record ScriptBundleDocument(ResourceIdentity Identity, SourceRevision Revision,
    DecodedExtent InputExtent, DecodedExtent HeaderExtent, DecodedExtent ConsumedExtent,
    uint FormatVersion, uint DeclaredBodyLength, IReadOnlyList<ScriptBundleChunk> Chunks,
    IReadOnlyList<SourceExtent> OriginalPhysicalExtents,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawBytes);

public sealed record ScriptBundleChunk(ResourceIdentity Identity, ulong OpaqueKey,
    DecodedExtent Extent, DecodedExtent HeaderExtent, DecodedExtent KeyExtent,
    DecodedExtent BytecodeSpan, DecodedExtent MetadataCountExtent,
    IReadOnlyList<ScriptBundleMetadata> Metadata,
    [property: JsonIgnore] ReadOnlyMemory<byte> BytecodeBytes);

public sealed record ScriptBundleMetadata(ResourceIdentity Identity, ulong OpaqueKey,
    uint RawDiscriminator, uint ClassCount, uint FormatVersion, byte Flag0, byte Flag1,
    DecodedExtent Extent, DecodedExtent HeaderExtent, DecodedExtent KeyExtent,
    DecodedExtent FlagsExtent, DecodedExtent PayloadLengthExtent, DecodedExtent PayloadExtent,
    [property: JsonIgnore] ReadOnlyMemory<byte> OpaquePayload);
