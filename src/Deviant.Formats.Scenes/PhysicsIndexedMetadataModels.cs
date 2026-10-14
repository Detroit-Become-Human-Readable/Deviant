using System.Text.Json.Serialization;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public enum PhysicsIndexedMetadataCoverage { CompleteStructure, Partial }
public enum PhysicsIndexedTableStatus { Complete, Opaque, Partial, Missing, Ambiguous, Unresolved }
public enum PhysicsIndexedReferenceStatus
{
    NotProjected, NoRedirect, NoLayoutForInitialSlot, InitialSlot, Unique, Missing, Ambiguous, Unresolved, OutOfRange, Invalid
}
public enum PhysicsIndexedItemProjection
{
    ReservedRow, Kind0RowReference, TypeAndData, TypeOnly, TypeAndDataWithAuxiliary,
    SpecialDescriptor4, SpecialDescriptor5, UnsupportedKind
}
public enum PhysicsIndexedDataPositionStatus
{
    NotProjected, NoDataPointer, WithinDataBody, AtDataEnd, OutOfRange, Unresolved, SpecialDescriptor, UnsupportedKind
}

public sealed record PhysicsIndexedMetadataLimits(int MaximumChunks = 128, int MaximumDepth = 4,
    int MaximumLeafBytes = 32 * 1024, int MaximumTypeSlots = 4096, int MaximumItemRows = 4096,
    long MaximumMetadataBytes = 1024 * 1024, int MaximumPhysicalExtents = 8192, int MaximumPrecedingSteps = 128);

public sealed record PhysicsIndexedItemIdentity(ResourceIdentity Owner, int IndexChunkOrdinal, int RowOrdinal);

public sealed record PhysicsIndexedMetadataLeaf(int ChunkOrdinal, DecodedExtent HeaderExtent, DecodedExtent BodyExtent,
    uint RawBigEndianWord, string? Sha256, bool BodyAvailable,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawHeader,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawBody);

public sealed record PhysicsIndexedTypeReference(uint RawSlot,
    PhysicsIndexedReferenceStatus DeclarationStatus, PhysicsTypeSlotIdentity? DeclarationTarget,
    IReadOnlyList<PhysicsTypeSlotIdentity> DeclarationCandidates,
    PhysicsIndexedReferenceStatus LayoutStatus, IReadOnlyList<PhysicsLayoutRecordIdentity> LayoutCandidates);

public sealed record PhysicsIndexedItemReference(int RawSignedIndex, PhysicsIndexedReferenceStatus Status,
    PhysicsIndexedItemIdentity? Target, IReadOnlyList<PhysicsIndexedItemIdentity> Candidates);

public sealed record PhysicsIndexedDataPosition(uint RawOffset, PhysicsIndexedDataPositionStatus Status,
    long? DataRelativeOffset, DecodedExtent? Position);

public sealed record PhysicsIndexedItemRow(PhysicsIndexedItemIdentity Identity, bool IsReservedRow,
    PhysicsIndexedItemProjection Projection, uint RawWord0, uint RawWord1, uint RawWord2,
    int Word2NativeInt32, uint RawKindNibble, uint UnknownWord0Bits,
    DecodedExtent Word0Extent, DecodedExtent Word1Extent, DecodedExtent Word2Extent,
    PhysicsIndexedTypeReference? Type, PhysicsIndexedDataPosition DataPosition,
    PhysicsIndexedItemReference? Redirect,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawBytes);

public sealed record PhysicsIndexedMetadataDocument(ResourceIdentity Identity, SourceRevision Revision,
    DecodedExtent EmbeddedExtent, string InheritedEmbeddedSha256, int? TypeChunkOrdinal, int IndexChunkOrdinal,
    int? FinalDeclarationSlots, DecodedExtent? DataBodyExtent,
    PhysicsIndexedTableStatus ItemTableStatus, IReadOnlyList<PhysicsIndexedMetadataLeaf> ItemTables,
    PhysicsIndexedTableStatus PatchTableStatus, IReadOnlyList<PhysicsIndexedMetadataLeaf> PatchTables,
    IReadOnlyList<PhysicsIndexedItemRow> Items, DecodedExtent? ItemRemainder,
    bool IndexTablesFullyVisited, bool ItemRowsFullyVisited, IReadOnlyList<DecodedExtent> UnknownRanges,
    PhysicsIndexedMetadataCoverage Coverage, long RetainedMetadataBytes);
