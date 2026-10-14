using System.Text.Json.Serialization;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public enum PhysicsTaggedMetadataCoverage { CompleteFraming, Partial }
public sealed record PhysicsTaggedMetadataLimits(int MaximumDepth = 4, int MaximumChunks = 128,
    int MaximumTokens = 1024, int MaximumTokenBytes = 256, int MaximumLeafBytes = 32 * 1024,
    long MaximumMetadataBytes = 256 * 1024, int MaximumPhysicalExtents = 8192, int MaximumPrecedingSteps = 128);

public sealed record PhysicsTaggedChunk(int Ordinal, int? ParentOrdinal, int Depth,
    uint RawBigEndianWord, byte RawFlags, uint DeclaredLength, string? Tag,
    DecodedExtent HeaderExtent, DecodedExtent BodyExtent,
    [property: JsonIgnore] byte[] RawHeader,
    [property: JsonIgnore] ReadOnlyMemory<byte>? MetadataBody, string? MetadataBodySha256);

/// <summary>A stored NUL-delimited token candidate, independent of type records or object instances.</summary>
public sealed record PhysicsStoredToken(int ChunkOrdinal, int BodyOffset, DecodedExtent Extent,
    string? PrintableText, bool Terminated, string Sha256, [property: JsonIgnore] byte[] RawBytes);

public sealed record PhysicsTaggedMetadataDocument(ResourceIdentity Identity, SourceRevision Revision,
    DecodedExtent EmbeddedExtent, string EmbeddedSha256,
    IReadOnlyList<PhysicsTaggedChunk> Chunks, IReadOnlyList<PhysicsStoredToken> Tokens,
    PhysicsTaggedMetadataCoverage Coverage, IReadOnlyList<DecodedExtent> UnknownRanges,
    DecodedExtent? BoundedRemainderExtent, long RetainedMetadataBytes);
