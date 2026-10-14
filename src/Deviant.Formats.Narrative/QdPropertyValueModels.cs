using System.Text.Json.Serialization;
using Deviant.Assets;

namespace Deviant.Formats.Narrative;

public sealed record QdPropertyValueReadOptions(
    int MaximumPayloadBytes = 16 * 1024 * 1024,
    int MaximumStringBytes = 4 * 1024 * 1024,
    int MaximumNodes = 100_000,
    int MaximumEdges = 250_000,
    int MaximumDepth = 64,
    long MaximumRetainedBytes = 32 * 1024 * 1024);

public enum QdPropertyValueKind
{
    Boolean,
    SignedInteger,
    Number,
    String,
    PackedArray,
    Object,
    Array,
    UnknownHeaderOnly
}

public enum QdPropertyNumberEncoding { SignedInteger, ScaledInt16Hundredths, Float32, Float64 }

public sealed record QdPropertyValueDocument(ResourceIdentity Identity, SourceRevision Revision,
    DecodedExtent InputExtent, DecodedExtent ConsumedExtent,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawBytes, string RootNodeId,
    IReadOnlyList<QdPropertyValueNode> Nodes, IReadOnlyList<QdPropertyValueChildEdge> ChildEdges,
    IReadOnlyList<SourceExtent> OriginalPhysicalExtents);

/// <summary>A raw QD value node. Property-name hashes remain numeric, and parent fields describe first discovery only.</summary>
/// <param name="ParentNodeId">The parent edge that first discovered this offset. <see cref="QdPropertyValueChildEdge"/> is authoritative for all graph links.</param>
/// <param name="ChildOrdinal">The child ordinal on the first-discovery parent edge.</param>
/// <param name="PropertyNameHash">The serialized 32-bit name hash; no property name is inferred.</param>
/// <param name="SubtreeExtent">Present only when the reachable owned node bytes form one verified contiguous span.</param>
/// <param name="SubtreeRange">The bounding range over reachable nodes; it can enclose bytes owned by siblings.</param>
/// <param name="SubtreeExtentIsContiguous">True means contiguous coverage was established. False means no contiguous-span claim is made.</param>
public sealed record QdPropertyValueNode(
    string NodeId,
    ResourceIdentity ValueIdentity,
    string? ParentNodeId,
    int? ChildOrdinal,
    int PayloadOffset,
    byte TagByte,
    byte TypeNibble,
    byte ModeFlags,
    uint PropertyNameHash,
    QdPropertyValueKind Kind,
    DecodedExtent HeaderExtent,
    DecodedExtent? ValueExtent,
    DecodedExtent OwnExtent,
    DecodedExtent? SubtreeExtent,
    DecodedExtent SubtreeRange,
    bool SubtreeExtentIsContiguous,
    bool IsAliased,
    ushort? ChildCount = null,
    int? ChildOffsetWidth = null,
    uint? PackedElementCount = null,
    int? PackedElementWidth = null,
    byte? BooleanRawByte = null,
    bool? BooleanValue = null,
    QdPropertyNumberEncoding? NumberEncoding = null,
    ulong? NumericBits = null,
    long? SignedIntegerValue = null,
    int? ScaledIntegerValue = null,
    string? NumericDisplay = null,
    bool? NumericIsFinite = null,
    string? StringDisplay = null,
    bool? StringIsValidUtf8 = null,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawValueBytes = default);

public sealed record QdPropertyValueChildEdge(
    string ParentNodeId,
    string TargetNodeId,
    int ChildOrdinal,
    uint EncodedRelativeOffset,
    int TargetPayloadOffset,
    bool IsAliasTarget);
