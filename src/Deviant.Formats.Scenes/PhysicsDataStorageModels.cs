using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public enum PhysicsDataStorageProjectionStatus
{
    NotProjected, Direct, Inherited, ParentStopped, MissingParent, Ambiguous, Cyclic, Partial, Invalid
}

public enum PhysicsDataWindowStatus
{
    NotProjected, NoDataPointer, NoPositiveCount, NoStride, NegativeOffset, ArithmeticOverflow, OutOfRange, WithinData, Partial
}

public sealed record PhysicsDataStorageLimits(int MaximumTypeRecords = 4096, int MaximumItemRows = 4096,
    int MaximumInheritanceSteps = 128, int MaximumLayoutFields = 65536,
    int MaximumLeafBytes = 32 * 1024, long MaximumRetainedMetadataBytes = 1024 * 1024);

public sealed record PhysicsDataStorageFieldSource(ResourceIdentity Owner, int TypeChunkOrdinal,
    int LayoutChunkOrdinal, int RecordOrdinal, int Slot, byte StoredMaskBit, uint NativeStorageMask,
    PhysicsTypeInteger RawFirstScalar, bool Inherited, IReadOnlyList<int> ParentPath, bool ParentStopObserved);

public sealed record PhysicsDataStorageProjection(PhysicsDataStorageProjectionStatus Status,
    uint NativeStorageMask, byte StoredMaskBit, PhysicsTypeInteger? RawFirstScalar,
    ushort? ElementByteStride, PhysicsDataStorageFieldSource? Provenance, string? Diagnostic = null);

public sealed record PhysicsDataWindowProjection(PhysicsDataWindowStatus Status, int SignedOffset,
    int SignedInstanceCount, ushort? ElementByteStride, long? ByteLength, DecodedExtent? Window,
    string? Diagnostic = null);

public sealed record PhysicsDataStorageRowProjection(PhysicsIndexedItemRow Item,
    PhysicsDataStorageProjection StorageWidth, PhysicsDataWindowProjection Window);

public sealed record PhysicsDataStorageDocument(ResourceIdentity Identity, SourceRevision Revision,
    DecodedExtent EmbeddedExtent, DecodedExtent DataBodyExtent, IReadOnlyList<PhysicsDataStorageRowProjection> Rows,
    PhysicsIndexedMetadataCoverage Coverage, long RetainedMetadataBytes)
{
    public PhysicsTypeLayoutCoverage LayoutCoverage { get; init; } = PhysicsTypeLayoutCoverage.CompleteStructure;
}
