using System.IO;
using Deviant.Assets;
using HelixToolkit.SharpDX;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Matrix = System.Numerics.Matrix4x4;
using HelixToolkit;
using HelixToolkit.Maths;
using Numerics = System.Numerics;

namespace Deviant.Desktop;

public static class ViewportGeometry
{
    internal static double CameraNearPlane(double radius)
    {
        if (!double.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        // Very small near planes collapse the depth differences between adjacent surfaces.
        return Math.Max(radius / 10000, Math.Clamp(radius / 100, .001, .25));
    }

    // The mesh buffer uses a 52-byte TBN vertex and four 8-byte UV channels.
    // Converted arrays and the upload vertex array are also included in admission.
    public static long GeometryBytes(int vertices, int indices, bool colors = false) => checked((long)vertices * (colors ? 100 : 84) + (long)indices * 4);
    public static long ConversionBytes(int vertices, int indices, bool colors = false) => checked((long)vertices * (colors ? 148 : 132) + (long)indices * 4);
    public static long RetainedConversionBytes(int vertices, int indices, bool colors = false) => checked((long)vertices * (colors ? 96 : 80) + (long)indices * 4);

    public static MeshGeometry3D Convert(MeshPrimitive primitive, GeometrySubmesh submesh,
        int uvSet = 0, bool requireTangentFrame = false, CancellationToken token = default)
    {
        if (primitive.Topology != PrimitiveTopology.Triangles)
            throw new InvalidDataException($"Unsupported topology: {primitive.Topology}.");
        if (primitive.Positions.Count == 0 || submesh.IndexCount <= 0 || submesh.IndexCount % 3 != 0 ||
            submesh.FirstIndex < 0 || (long)submesh.FirstIndex + submesh.IndexCount > primitive.Indices.Count)
            throw new InvalidDataException("Invalid triangle range.");
        int count = primitive.Positions.Count;
        bool hasNormals = HasUsableNormals(primitive, token);
        bool hasTangents = HasUsableTangentFrame(primitive, token);
        var uv = primitive.TextureCoordinates is { } sets && uvSet >= 0 && uvSet < sets.Count ? sets[uvSet] : null;
        if (requireTangentFrame && (!hasNormals || !hasTangents || uv?.Count != count))
            throw new InvalidDataException("Normal mapping requires a complete normal, tangent, and UV stream.");
        var positions = new Vector3Collection(count);
        var normals = new Vector3Collection(count);
        var tangents = new Vector3Collection(count);
        var bitangents = new Vector3Collection(count);
        var texcoords = new Vector2Collection(count);
        var coordinateSets = new Vector2Collection[Math.Min(4, primitive.TextureCoordinates?.Count ?? 0)];
        for (var set = 0; set < coordinateSets.Length; set++)
            coordinateSets[set] = set == uvSet && primitive.TextureCoordinates![set].Count == count
                ? texcoords : new Vector2Collection(primitive.TextureCoordinates![set].Count == count ? count : 0);
        var colors = primitive.Colors?.Count == count ? new Color4Collection(count) : null;
        for (int i = 0; i < count; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            var p = primitive.Positions[i];
            if (!Finite(p)) throw new InvalidDataException("Non-finite vertex position.");
            positions.Add(new(p.X, p.Y, p.Z));
            var n = hasNormals ? Normalize(primitive.Normals![i], "normal") : Numerics.Vector3.Zero;
            normals.Add(new(n.X, n.Y, n.Z));
            var t4 = hasTangents ? primitive.Tangents![i] : Numerics.Vector4.Zero;
            var t = hasTangents ? Normalize(new(t4.X, t4.Y, t4.Z), "tangent") : Numerics.Vector3.Zero;
            if (requireTangentFrame && t4.W != 1 && t4.W != -1)
                throw new InvalidDataException("Invalid tangent handedness.");
            var b = hasNormals && hasTangents ? Numerics.Vector3.Cross(n, t) * t4.W : Numerics.Vector3.Zero;
            if (requireTangentFrame) b = Normalize(b, "bitangent");
            tangents.Add(new(t.X, t.Y, t.Z));
            bitangents.Add(new(b.X, b.Y, b.Z));
            var q = uv?.Count == count ? uv[i] : Numerics.Vector2.Zero;
            if (!float.IsFinite(q.X) || !float.IsFinite(q.Y)) throw new InvalidDataException("Non-finite UV.");
            texcoords.Add(new(q.X, q.Y));
            for (var set = 0; set < coordinateSets.Length; set++)
            {
                if (ReferenceEquals(coordinateSets[set], texcoords) || primitive.TextureCoordinates![set].Count != count) continue;
                var coordinate = primitive.TextureCoordinates[set][i];
                if (!float.IsFinite(coordinate.X) || !float.IsFinite(coordinate.Y)) throw new InvalidDataException("Non-finite UV.");
                coordinateSets[set].Add(coordinate);
            }
            if (colors is not null)
            {
                var c = primitive.Colors![i];
                if (!float.IsFinite(c.X) || !float.IsFinite(c.Y) || !float.IsFinite(c.Z) || !float.IsFinite(c.W))
                    throw new InvalidDataException("Non-finite vertex color.");
                colors.Add(new Color4(c.X, c.Y, c.Z, c.W));
            }
        }
        var indices = new IntCollection(submesh.IndexCount);
        for (int i = submesh.FirstIndex; i < submesh.FirstIndex + submesh.IndexCount; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            uint value = primitive.Indices[i];
            if (value >= count) throw new InvalidDataException("Triangle index exceeds the vertex stream.");
            indices.Add(checked((int)value));
        }
        var result = new ViewportMeshGeometry { Positions = positions, TriangleIndices = indices,
            Normals = normals, Tangents = tangents, BiTangents = bitangents, TextureCoordinates = texcoords,
            TextureCoordinateSets = coordinateSets, Colors = colors };
        if (!hasNormals) result.UpdateNormals();
        result.UpdateBounds();
        return result;
    }

    public static Matrix NativeMatrix(Numerics.Matrix4x4 m) => new(m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);

    public static bool Finite(Numerics.Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    internal static bool HasUsableNormals(MeshPrimitive primitive, CancellationToken token = default)
    {
        if (primitive.Normals?.Count != primitive.Positions.Count) return false;
        for (var i = 0; i < primitive.Positions.Count; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            if (!TryNormalize(primitive.Normals[i], out _)) return false;
        }
        return true;
    }

    internal static bool HasUsableTangentFrame(MeshPrimitive primitive, CancellationToken token = default)
    {
        if (primitive.Normals?.Count != primitive.Positions.Count ||
            primitive.Tangents?.Count != primitive.Positions.Count) return false;
        for (var i = 0; i < primitive.Positions.Count; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            var tangent = primitive.Tangents[i];
            if ((tangent.W != 1 && tangent.W != -1) ||
                !TryNormalize(primitive.Normals[i], out var normal) ||
                !TryNormalize(new(tangent.X, tangent.Y, tangent.Z), out var direction) ||
                !TryNormalize(Numerics.Vector3.Cross(normal, direction), out _)) return false;
        }
        return true;
    }

    private static bool TryNormalize(Numerics.Vector3 value, out Numerics.Vector3 normalized)
    {
        var length = value.LengthSquared();
        normalized = default;
        if (!Finite(value) || !float.IsFinite(length) || length < 1e-12f) return false;
        normalized = Numerics.Vector3.Normalize(value);
        return Finite(normalized);
    }
    private static Numerics.Vector3 Normalize(Numerics.Vector3 v, string field)
    {
        if (!TryNormalize(v, out var normalized)) throw new InvalidDataException($"Invalid {field} vector.");
        return normalized;
    }
}
