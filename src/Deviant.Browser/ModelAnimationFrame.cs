using System.Numerics;
using Deviant.Assets;
using Deviant.Export.Models;

namespace Deviant.Browser;

/// <summary>Owns one skinned preview frame in the selected mesh's source coordinates.</summary>
public sealed class ModelAnimationFrame : IDisposable
{
    private IDisposable? memory;
    private IReadOnlyDictionary<string, MeshPrimitive>? primitives;
    private ModelAnimationFrame(SourceRevision revision, Dictionary<string, MeshPrimitive> primitives, IDisposable memory)
    { Revision = revision; this.primitives = primitives; this.memory = memory; }
    public SourceRevision Revision { get; }
    public IReadOnlyDictionary<string, MeshPrimitive> Primitives => primitives ?? throw new ObjectDisposedException(nameof(ModelAnimationFrame));
    public void Dispose() { primitives = null; Interlocked.Exchange(ref memory, null)?.Dispose(); }

    public static ModelAnimationFrame Create(GeometryAsset geometry, FbxSkeletonExportInput skeleton,
        NativeAnimationRigBinding binding, IReadOnlyDictionary<int, string> jointKeysBySourceOrdinal,
        float seconds, bool inPlace, AssetMemoryBudget budget, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(geometry); ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(jointKeysBySourceOrdinal);
        ArgumentNullException.ThrowIfNull(budget); token.ThrowIfCancellationRequested();
        if (!float.IsFinite(seconds) || skeleton.Identity != binding.SkeletonIdentity || skeleton.Revision != binding.Revision ||
            skeleton.Joints.Count != binding.Joints.Count || jointKeysBySourceOrdinal.Count != binding.Joints.Count)
            throw new InvalidDataException("AnimationFrameSourceMismatch");
        long vertices = geometry.Primitives.Sum(primitive => (long)primitive.Positions.Count);
        if (vertices is < 1 or > 8_000_000 || geometry.Primitives.Count > 8192)
            throw new InvalidDataException("AnimationFrameVertexLimit");
        var memory = budget.Reserve(checked(vertices * 48 + skeleton.Joints.Count * 512L + geometry.Primitives.Count * 2048L + 8192), "animation preview frame");
        try
        {
            var joints = skeleton.Joints.Select((joint, ordinal) => (joint, ordinal)).ToDictionary(pair => pair.joint.Key, StringComparer.Ordinal);
            var global = binding.GlobalPose(seconds, inPlace, token);
            var jointWorlds = new Matrix4x4[joints.Count]; var assigned = new bool[joints.Count];
            foreach (var node in binding.Joints)
            {
                token.ThrowIfCancellationRequested();
                if (!jointKeysBySourceOrdinal.TryGetValue(node.NodeOrdinal, out var key) || !joints.TryGetValue(key, out var target) || assigned[target.ordinal] ||
                    (node.ParentOrdinal is { } parent ? !jointKeysBySourceOrdinal.TryGetValue(parent, out var parentKey) || target.joint.ParentKey != parentKey : target.joint.ParentKey is not null))
                    throw new InvalidDataException("AnimationFrameHierarchyMismatch");
                jointWorlds[target.ordinal] = global[node.NodeOrdinal]; assigned[target.ordinal] = true;
            }
            var inverseBind = new Matrix4x4[joints.Count];
            foreach (var pair in joints.Values)
                if (!Matrix4x4.Invert(Matrix(pair.joint.BindWorld), out inverseBind[pair.ordinal]))
                    throw new InvalidDataException("AnimationFrameBindSingular");
            var skins = skeleton.Skins.ToDictionary(skin => skin.PrimitiveKey, StringComparer.Ordinal);
            var result = new Dictionary<string, MeshPrimitive>(StringComparer.Ordinal);
            foreach (var primitive in geometry.Primitives)
            {
                token.ThrowIfCancellationRequested();
                if (!skins.TryGetValue(primitive.Key, out var skin)) { result.Add(primitive.Key, primitive); continue; }
                if (skin.VertexInfluences.Count != primitive.Positions.Count || primitive.Normals?.Count != primitive.Positions.Count ||
                    primitive.Tangents is not null && primitive.Tangents.Count != primitive.Positions.Count)
                    throw new InvalidDataException("AnimationFrameSkinMismatch");
                var meshBind = Matrix(skin.MeshBindWorld);
                var palette = inverseBind.Select((offset, joint) => meshBind * offset * jointWorlds[joint]).ToArray();
                var positions = new Vector3[primitive.Positions.Count]; var normals = new Vector3[positions.Length];
                var tangents = primitive.Tangents is null ? null : new Vector4[positions.Length];
                var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
                for (int vertex = 0; vertex < positions.Length; vertex++)
                {
                    if ((vertex & 511) == 0) token.ThrowIfCancellationRequested();
                    Vector3 position = default, normal = default, tangent = default; double total = 0;
                    var influences = skin.VertexInfluences[vertex];
                    if (influences.Count is < 1 or > 8) throw new InvalidDataException("AnimationFrameInfluenceLimit");
                    foreach (var influence in influences)
                    {
                        if ((uint)influence.JointOrdinal >= palette.Length || !double.IsFinite(influence.Weight) || influence.Weight < 0)
                            throw new InvalidDataException("AnimationFrameInfluenceInvalid");
                        var transform = palette[influence.JointOrdinal]; float weight = (float)influence.Weight;
                        position += Vector3.Transform(primitive.Positions[vertex], transform) * weight;
                        normal += Vector3.TransformNormal(primitive.Normals![vertex], transform) * weight;
                        if (primitive.Tangents is { } sourceTangents)
                        { var t = sourceTangents[vertex]; tangent += Vector3.TransformNormal(new(t.X, t.Y, t.Z), transform) * weight; }
                        total += influence.Weight;
                    }
                    if (Math.Abs(total - 1) > 1e-6 || !Finite(position) || !Finite(normal) || normal.LengthSquared() < 1e-12f)
                        throw new InvalidDataException("AnimationFrameNonfinite");
                    positions[vertex] = position; normals[vertex] = Vector3.Normalize(normal);
                    if (tangents is not null)
                    {
                        if (!Finite(tangent) || tangent.LengthSquared() < 1e-12f) throw new InvalidDataException("AnimationFrameTangentInvalid");
                        tangents[vertex] = new(Vector3.Normalize(tangent), primitive.Tangents![vertex].W);
                    }
                    minimum = Vector3.Min(minimum, position); maximum = Vector3.Max(maximum, position);
                }
                result.Add(primitive.Key, primitive with { Positions = positions, Normals = normals, Tangents = tangents, Bounds = new(minimum, maximum) });
            }
            if (skins.Keys.Any(key => !result.ContainsKey(key))) throw new InvalidDataException("AnimationFramePrimitiveMismatch");
            token.ThrowIfCancellationRequested(); return new(binding.Revision, result, memory);
        }
        catch { memory.Dispose(); throw; }
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static Matrix4x4 Matrix(FbxBindMatrix matrix)
    {
        if (matrix.Elements.Count != 16 || matrix.Elements.Any(value => !double.IsFinite(value) || !float.IsFinite((float)value)))
            throw new InvalidDataException("AnimationFrameBindInvalid");
        var a = matrix.Elements;
        if (Math.Abs(a[3]) > 1e-7 || Math.Abs(a[7]) > 1e-7 || Math.Abs(a[11]) > 1e-7 || Math.Abs(a[15] - 1) > 1e-7)
            throw new InvalidDataException("AnimationFrameBindInvalid");
        return new((float)a[0],(float)a[1],(float)a[2],(float)a[3],(float)a[4],(float)a[5],(float)a[6],(float)a[7],
            (float)a[8],(float)a[9],(float)a[10],(float)a[11],(float)a[12],(float)a[13],(float)a[14],(float)a[15]);
    }
}
