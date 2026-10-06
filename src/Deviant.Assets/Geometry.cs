using System.Numerics;
namespace Deviant.Assets;

public enum CoordinateAxis { PositiveX, NegativeX, PositiveY, NegativeY, PositiveZ, NegativeZ }
public enum CoordinateHandedness { Unknown, Left, Right }
public enum MatrixLayout { Unknown, RowMajor, ColumnMajor }
public enum VectorMultiplicationSide { Unknown, Row, Column }
public enum QuaternionComponentOrder { Unknown, Xyzw, Wxyz }
public sealed record AxisMapping(CoordinateAxis Right, CoordinateAxis Up, CoordinateAxis Forward);
public sealed record CoordinateProfile(string Id, string Version, AxisMapping? Axes = null,
    CoordinateHandedness Handedness = CoordinateHandedness.Unknown, double? MetersPerUnit = null,
    MatrixLayout MatrixLayout = MatrixLayout.Unknown, VectorMultiplicationSide VectorSide = VectorMultiplicationSide.Unknown,
    QuaternionComponentOrder QuaternionOrder = QuaternionComponentOrder.Unknown, RelationshipEvidence? Evidence = null)
{
    public bool HasVerifiedBasis => Evidence?.Verified == true && Axes is not null && Handedness != CoordinateHandedness.Unknown && MetersPerUnit is > 0;
}
public sealed record SourceTransform(IReadOnlyList<float> Elements, MatrixLayout Layout, VectorMultiplicationSide VectorSide);
public enum PrimitiveTopology { Points, Lines, LineStrip, Triangles, TriangleStrip, TriangleFan }
public enum IndexElementFormat { UInt16, UInt32 }
public sealed record GeometryBounds(Vector3 Minimum, Vector3 Maximum);
public sealed record GeometrySubmesh(string Key, int FirstIndex, int IndexCount, IReadOnlyList<ResourceReference> Materials);
public sealed record VertexInfluence(int PaletteSlot, float RawWeight);
public sealed record PaletteJoint(int Slot, ResourceReference Joint);
public sealed record SkinBinding(ResourceIdentity Skeleton, IReadOnlyList<PaletteJoint> Palette,
    IReadOnlyList<IReadOnlyList<VertexInfluence>> VertexInfluences);
public sealed record MeshPrimitive(string Key, PrimitiveTopology Topology, IReadOnlyList<Vector3> Positions,
    IReadOnlyList<uint> Indices, IndexElementFormat IndexFormat, IReadOnlyList<GeometrySubmesh> Submeshes,
    IReadOnlyList<Vector3>? Normals = null, IReadOnlyList<Vector4>? Tangents = null,
    IReadOnlyList<IReadOnlyList<Vector2>>? TextureCoordinates = null, GeometryBounds? Bounds = null,
    SkinBinding? Skin = null, IReadOnlyDictionary<string,DecodedExtent>? FieldExtents = null,
    IReadOnlyList<Vector4>? Colors = null);
public sealed record GeometryNode(string Key, string? ParentKey, ResourceReference Source,
    IReadOnlyList<string> PrimitiveKeys, SourceTransform? Transform = null,
    IReadOnlyList<NameEvidence>? Names = null);
public sealed record GeometryAsset(CoordinateProfile Coordinates, IReadOnlyList<GeometryNode> Nodes,
    IReadOnlyList<MeshPrimitive> Primitives, IReadOnlyDictionary<string,string> UnknownFields);
public sealed record SkeletonJoint(string Key, string? ParentKey, ResourceReference Source,
    SourceTransform? LocalBind, SourceTransform? InverseBind, IReadOnlyList<NameEvidence> Names);
public sealed record SkeletonAsset(CoordinateProfile Coordinates, IReadOnlyList<SkeletonJoint> Joints,
    IReadOnlyDictionary<string,string> UnknownFields);
