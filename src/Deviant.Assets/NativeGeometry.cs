using System.Numerics;

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
    IReadOnlyList<DecodedExtent> BlendShapeCountExtents);
public sealed record NativeMeshLod(ResourceReference MeshData, float Threshold, DecodedExtent Extent);
public sealed record NativeMeshEntityDocument(ResourceIdentity Identity, SceneEntityRecord Entity,
    uint Version, IReadOnlyList<NativeMeshLod> Lods, uint GroupIndex,
    DecodedExtent MeshExtent, IReadOnlyDictionary<string, DecodedExtent> FieldExtents)
{
    public uint NativeFlags { get; init; }
    public bool IsSourceVisible => (NativeFlags & 1u) != 0;
}
