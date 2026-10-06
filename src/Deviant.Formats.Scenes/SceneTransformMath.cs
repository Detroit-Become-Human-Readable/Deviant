using System.Numerics;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public static class SceneTransformMath
{
    public static bool TryGetLocal(SceneNodeRecord record, out Matrix4x4 local)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!TryRead(record.StoredLocalTransform, out local)) return false;
        if ((record.Flags & 1) == 0) return true;
        if (!float.IsFinite(record.Pivot.X) || !float.IsFinite(record.Pivot.Y) || !float.IsFinite(record.Pivot.Z))
            return false;
        var squared = new Vector3(
            new Vector3(local.M11, local.M12, local.M13).LengthSquared(),
            new Vector3(local.M21, local.M22, local.M23).LengthSquared(),
            new Vector3(local.M31, local.M32, local.M33).LengthSquared());
        const float minimumNormal = 1.17549435e-38f;
        if (!float.IsFinite(squared.X) || !float.IsFinite(squared.Y) || !float.IsFinite(squared.Z) ||
            squared.X < minimumNormal || squared.Y < minimumNormal || squared.Z < minimumNormal) return false;
        var scale = new Vector3(MathF.Sqrt(squared.X), MathF.Sqrt(squared.Y), MathF.Sqrt(squared.Z));
        var pivotInBasis = Vector3.TransformNormal(record.Pivot, local);
        var adjusted = local.Translation + record.Pivot - pivotInBasis / scale;
        local.M41 = adjusted.X; local.M42 = adjusted.Y; local.M43 = adjusted.Z;
        return IsFinite(local);
    }

    public static bool TryRead(SourceTransform transform, out Matrix4x4 value)
    {
        ArgumentNullException.ThrowIfNull(transform);
        value = default;
        if (transform.Layout != MatrixLayout.RowMajor || transform.VectorSide != VectorMultiplicationSide.Row ||
            transform.Elements.Count != 16 || transform.Elements.Any(item => !float.IsFinite(item))) return false;
        var e = transform.Elements;
        value = new(e[0], e[1], e[2], e[3], e[4], e[5], e[6], e[7],
            e[8], e[9], e[10], e[11], e[12], e[13], e[14], e[15]);
        return true;
    }

    public static SourceTransform ToSource(Matrix4x4 value) => new(
        [value.M11, value.M12, value.M13, value.M14, value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34, value.M41, value.M42, value.M43, value.M44],
        MatrixLayout.RowMajor, VectorMultiplicationSide.Row);

    internal static bool IsFinite(Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
}
