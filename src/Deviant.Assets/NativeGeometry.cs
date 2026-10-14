using System.Numerics;
using System.Text.Json.Serialization;

namespace Deviant.Assets;

public sealed record NativeVertexDescriptor(uint Word, DecodedExtent Extent)
{
    public byte Stream => (byte)Word;
    public byte Offset => (byte)(Word >> 8);
    public byte Format => (byte)(Word >> 16);
    public byte Semantic => (byte)(Word >> 24);
}
public sealed record NativeVertexStream(uint Stride, DecodedExtent? InlineExtent);
public sealed record NativeVertexBuffer(uint ElementCount, byte Flag,
    IReadOnlyList<NativeVertexDescriptor> Descriptors, IReadOnlyList<NativeVertexStream> Streams);
public sealed record NativeMeshBounds(IReadOnlyList<float> Obb, Vector4 Extra, DecodedExtent Extent);
public sealed record NativeMeshPart(uint LeadingWord, byte StoredByte, byte Flags,
    uint VertexBufferIndex, uint BaseVertex, uint VertexCount, uint IndexBufferIndex,
    uint FirstIndex, uint IndexCount, uint TopologySelector, IReadOnlyList<uint> Palette,
    ResourceReference Material, uint VectorCount, DecodedExtent VectorExtent, uint Control,
    NativeMeshBounds Bounds, DecodedExtent Extent);
public sealed record NativeMeshGroup(uint HeaderWord1, uint HeaderWord2,
    IReadOnlyList<NativeMeshPart> Parts, byte Flag1, byte Flag2, NativeMeshBounds Bounds);
public sealed record NativeMeshDataDocument(ResourceIdentity Identity, uint NativeId, DecodedExtent PayloadExtent,
    IReadOnlyList<NativeVertexBuffer> VertexBuffers, IReadOnlyList<uint> IndexCounts,
    IReadOnlyList<NativeMeshGroup> Groups, uint ReferenceControl,
    IReadOnlyList<ResourceReference> References, float? ReferenceFactor, uint StoredWord,
    string Name, uint TailWord, ResourceReference? TailReference, byte TailFlag1, byte TailFlag2,
    uint TailWord2, DecodedExtent MetadataExtent, byte ClusterFlag,
    IReadOnlyList<DecodedExtent> BlendShapeCountExtents)
{
    public NativeMeshClusterV2Section? ClusterMetadata { get; init; }
    public NativeMeshBlendShapeSection? BlendShapeMetadata { get; init; }
}

/// <summary>Retains CLUPSKME v2 flag1 data without assigning meanings to its fields.</summary>
public sealed record NativeMeshClusterV2Section(byte Flag, uint EntryCount,
    IReadOnlyList<uint> ScalarWordBits, [property: JsonIgnore] ReadOnlyMemory<byte> RawScalarWords,
    DecodedExtent ScalarWordsExtent, [property: JsonIgnore] ReadOnlyMemory<byte> RawByteValues,
    DecodedExtent ByteValuesExtent, [property: JsonIgnore] ReadOnlyMemory<byte> RawTupleValues,
    DecodedExtent TupleValuesExtent, [property: JsonIgnore] ReadOnlyMemory<byte> RawSection,
    DecodedExtent Extent);

/// <summary>Retains BLSHAPES v3 groups and entries without assigning shape semantics.</summary>
public sealed record NativeMeshBlendShapeSection(IReadOnlyList<NativeMeshBlendShapeGroup> Groups,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawSection, DecodedExtent Extent);
public sealed record NativeMeshBlendShapeGroup(uint EntryCount, DecodedExtent CountExtent,
    IReadOnlyList<NativeMeshBlendShapeEntry> Entries, DecodedExtent Extent);
public sealed record NativeMeshBlendShapeEntry(IReadOnlyList<uint> RawHeaderWords, byte RawFlags,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawHeader, DecodedExtent HeaderExtent,
    IReadOnlyList<NativeMeshBlendShapeStringRecord> StringRecords,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawU32ArrayA, DecodedExtent U32ArrayAExtent,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawU32ArrayB, DecodedExtent U32ArrayBExtent,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawU16Table, DecodedExtent U16TableExtent,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawFloatWords, DecodedExtent FloatWordsExtent,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawFlag1Words, DecodedExtent Flag1WordsExtent,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawFlag2Words, DecodedExtent Flag2WordsExtent,
    DecodedExtent Extent);
public sealed record NativeMeshBlendShapeStringRecord(uint RawLengthWord,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawBytes,
    DecodedExtent LengthWordExtent, DecodedExtent BytesExtent,
    uint RawTrailingWord, DecodedExtent TrailingWordExtent, DecodedExtent Extent);
public sealed record NativeMeshLod(ResourceReference MeshData, float Threshold, DecodedExtent Extent);
public sealed record NativeMeshEntityDocument(ResourceIdentity Identity, SceneEntityRecord Entity,
    uint Version, IReadOnlyList<NativeMeshLod> Lods, uint GroupIndex,
    DecodedExtent MeshExtent, IReadOnlyDictionary<string, DecodedExtent> FieldExtents)
{
    public uint NativeFlags { get; init; }
    public bool IsSourceVisible => (NativeFlags & 1u) != 0;
}
