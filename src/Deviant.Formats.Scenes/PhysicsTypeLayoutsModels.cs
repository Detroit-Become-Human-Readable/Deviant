using System.Text.Json.Serialization;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public enum PhysicsTypeLayoutCoverage { CompleteStructure, Partial }
public enum PhysicsLayoutReferenceStatus { NoLinkRequested, Unresolved, Unique, Missing, Ambiguous }
public sealed record PhysicsTypeLayoutLimits(int MaximumChunks = 128, int MaximumDepth = 4,
    int MaximumLeafBytes = 32 * 1024, int MaximumStrings = 4096, int MaximumStringBytes = 256,
    int MaximumRecords = 4096, int MaximumMembersPerRecord = 4096, int MaximumMembers = 65536,
    long MaximumMetadataBytes = 1024 * 1024, int MaximumPhysicalExtents = 8192, int MaximumPrecedingSteps = 128);

public sealed record PhysicsLayoutRecordIdentity(ResourceIdentity Owner, int TypeChunkOrdinal, int LayoutChunkOrdinal, int RecordOrdinal, int Slot);
public sealed record PhysicsLayoutReference(PhysicsTypeInteger Index, PhysicsLayoutReferenceStatus Status,
    IReadOnlyList<PhysicsLayoutRecordIdentity> Candidates);
public sealed record PhysicsLayoutGatedValue(byte MaskBit, IReadOnlyList<PhysicsTypeInteger> Fields);
public sealed record PhysicsLayoutMember(int StreamOrdinal, PhysicsTypeInteger NameIndex, PhysicsTypeName Name,
    PhysicsTypeInteger Flags, ushort RawFlagsUInt16, ushort EffectiveFlagsUInt16,
    PhysicsTypeInteger ThirdScalar, ushort ThirdScalarUInt16, uint PackedNativeWord, PhysicsLayoutReference Type,
    DecodedExtent Extent)
{
    /// <summary>Native byte offset consumed by reflected-member fixup traversal.</summary>
    public ushort NativeByteOffset => ThirdScalarUInt16;
}
public sealed record PhysicsLayoutInterface(PhysicsTypeInteger TypeIndex, PhysicsTypeSlotIdentity? DeclarationTarget,
    PhysicsTypeInteger SecondScalar, DecodedExtent Extent);
public sealed record PhysicsTypeLayoutRecord(PhysicsLayoutRecordIdentity Identity, DecodedExtent Extent,
    PhysicsTypeInteger SlotIndex, PhysicsLayoutReference Parent, PhysicsTypeInteger Mask,
    IReadOnlyList<PhysicsLayoutGatedValue> GatedValues, PhysicsLayoutReference? Subtype,
    PhysicsTypeInteger? MemberCount, IReadOnlyList<PhysicsLayoutMember> Members,
    PhysicsTypeInteger? InterfaceCount, IReadOnlyList<PhysicsLayoutInterface> Interfaces);
public sealed record PhysicsTypeLayoutsDocument(ResourceIdentity Identity, SourceRevision Revision, DecodedExtent EmbeddedExtent,
    string InheritedEmbeddedSha256, int TypeChunkOrdinal, int FinalDeclarationSlots,
    PhysicsTypeDeclarationLeaf StringTable, PhysicsTypeDeclarationLeaf LayoutTable,
    IReadOnlyList<PhysicsTypeName> Names, IReadOnlyList<PhysicsTypeLayoutRecord> Records,
    bool LayoutTableFullyVisited, PhysicsTypeLayoutCoverage Coverage, IReadOnlyList<DecodedExtent> UnknownRanges, long RetainedMetadataBytes);
