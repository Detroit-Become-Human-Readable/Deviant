using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Serialization;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public sealed record NativeNodeArrayBodyLimits(int MaximumPrimaryNodes = 65_536,
    int MaximumSecondaryRecords = 65_536, int MaximumChildReferences = 262_144,
    int MaximumTableRecords = 65_536, int MaximumOptionalBytes = 8 * 1024 * 1024,
    long MaximumMetadataBytes = 64L * 1024 * 1024, int MaximumDiagnostics = 4096,
    int MaximumPhysicalExtents = 8192, int MaximumPrecedingSteps = 128);

/// <summary>Stored component bytes borrowing the retained A1 owner; their pose-space role is unassigned.</summary>
public sealed record NodeArrayStoredComponents([property: JsonIgnore] ReadOnlyMemory<byte> RawBytes, DecodedExtent Extent)
{
    public uint GetBits(int storedIndex)
    {
        if ((uint)storedIndex >= 10) throw new ArgumentOutOfRangeException(nameof(storedIndex));
        return BinaryPrimitives.ReadUInt32LittleEndian(RawBytes.Span.Slice(storedIndex * 4, 4));
    }
    public float GetValue(int storedIndex) => BitConverter.UInt32BitsToSingle(GetBits(storedIndex));
    [JsonIgnore] public Vector4 NativeFirstBlock => new(GetValue(1), GetValue(2), GetValue(3), GetValue(0));
    [JsonIgnore] public Vector3 VectorAt16 => new(GetValue(4), GetValue(5), GetValue(6));
    [JsonIgnore] public Vector3 VectorAt32 => new(GetValue(7), GetValue(8), GetValue(9));
}

public sealed record NodeArrayBodyNode(int Ordinal, uint Flags, NodeArrayStoredComponents Components,
    uint Scalar1, uint StoredParent, IReadOnlyList<uint> Children, uint Scalar2, DecodedExtent Extent);
public sealed record NodeArrayBodyDescriptor(uint Key, ushort Word16A, ushort Word16B,
    uint RawWord, DecodedExtent Extent);
public sealed record NodeArrayCompactRecord(uint Key, ushort Index16A, ushort Index16B, DecodedExtent Extent);
public sealed record NodeArrayRawSection(string Name, int Count, int StoredStride,
    DecodedExtent Extent, [property: JsonIgnore] ReadOnlyMemory<byte> RawBytes);
public sealed record NodeArrayBodyDiagnostic(string Code, int? Ordinal = null, uint? Index = null);

/// <summary>Version14 body metadata retaining its A1 payload owner and exact raw section views.</summary>
public sealed record NativeNodeArrayBodyDocument([property: JsonIgnore] NativeNodeArrayDocument Owner,
    IReadOnlyList<NodeArrayBodyNode> Nodes, IReadOnlyList<NodeArrayBodyDescriptor> Descriptors,
    IReadOnlyList<NodeArrayCompactRecord> CompactRecords, NodeArrayRawSection OptionalObject,
    uint Scalar340, byte UnnamedByte, uint Scalar348, uint Scalar344,
    IReadOnlyList<NodeArrayRawSection> RemainingTables, IReadOnlyList<NodeArrayBodyDiagnostic> StructuralDiagnostics,
    long AccountedMetadataBytes, DecodedExtent Extent)
{
    // Additional reader metadata accounting excludes the borrowed A1 payload and caller-owned provenance strings.
    public ResourceIdentity Identity => Owner.Identity;
    public SourceRevision Revision => Owner.Revision;
    public uint BodyVersion => Owner.BodyVersion;
    [JsonIgnore] public ReadOnlyMemory<byte> RawPayload => Owner.RawPayload;
}
