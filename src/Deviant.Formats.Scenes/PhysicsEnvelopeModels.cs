using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public sealed record PhysicsEnvelopeLimits(long MaximumEmbeddedBytes = 16L * 1024 * 1024,
    int MaximumPhysicalExtents = 8192, int MaximumPrecedingSteps = 128);

/// <summary>Raw projections of the first eight opaque embedded bytes.</summary>
public sealed record PhysicsEmbeddedHeader(uint RawBigEndianWord, uint Lower30Bits,
    byte High2Bits, byte[] RawTagBytes, DecodedExtent Extent);

/// <summary>A PHYSYS v5 native frame with its embedded bytes retained for inspection.</summary>
public sealed record PhysicsEnvelopeDocument(ResourceIdentity Identity, SourceRevision Revision,
    uint NativeId, uint ClassCount, uint SerializedVersion, uint EmbeddedLength,
    DecodedExtent PayloadExtent, DecodedExtent EmbeddedExtent,
    ReadOnlyMemory<byte> EmbeddedBytes, string EmbeddedSha256,
    PhysicsEmbeddedHeader? EmbeddedHeader, IReadOnlyDictionary<string, DecodedExtent> FieldExtents,
    IReadOnlyList<DecodedExtent> UnknownRanges, ReadOnlyMemory<byte> RawPayload);
