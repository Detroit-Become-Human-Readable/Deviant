using System.Numerics;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public static class ScenePreviewBounds
{
    public static GeometryBounds Choose(IReadOnlyList<GeometryBounds> bounds, float halfSize = 10)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        if (bounds.Count == 0) throw new ArgumentException("At least one object is required.", nameof(bounds));
        if (!float.IsFinite(halfSize) || halfSize <= 0) throw new ArgumentOutOfRangeException(nameof(halfSize));
        var centers = bounds.Select(value => value.Minimum * .5f + value.Maximum * .5f).ToArray();
        if (centers.Any(value => !float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)))
            throw new ArgumentException("Object bounds must be finite.", nameof(bounds));
        static float Median(IEnumerable<float> values)
        {
            var sorted = values.Order().ToArray();
            return sorted.Length % 2 == 0 ? sorted[sorted.Length / 2 - 1] * .5f + sorted[sorted.Length / 2] * .5f : sorted[sorted.Length / 2];
        }
        var middle = new Vector3(Median(centers.Select(value => value.X)), Median(centers.Select(value => value.Y)), Median(centers.Select(value => value.Z)));
        var anchor = centers.MinBy(value => Vector3.DistanceSquared(value, middle));
        return new(anchor - new Vector3(halfSize), anchor + new Vector3(halfSize));
    }
}
