using System.Numerics;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

/// <summary>Named component views following the NODEAR version14 input converter.</summary>
public static class NodeArrayComponentSemantics
{
    public static Quaternion QuaternionXyzw(this NodeArrayStoredComponents components)
    {
        ArgumentNullException.ThrowIfNull(components);
        return new(components.GetValue(1), components.GetValue(2), components.GetValue(3), components.GetValue(0));
    }

    public static Vector3 Translation(this NodeArrayStoredComponents components)
    {
        ArgumentNullException.ThrowIfNull(components);
        return new(components.GetValue(4), components.GetValue(5), components.GetValue(6));
    }

    public static Vector3 BasisScale(this NodeArrayStoredComponents components)
    {
        ArgumentNullException.ThrowIfNull(components);
        return new(components.GetValue(7), components.GetValue(8), components.GetValue(9));
    }

    public static DecodedExtent QuaternionStoredExtent(this NodeArrayStoredComponents components) =>
        StoredExtent(components, 0, 16);

    public static DecodedExtent TranslationStoredExtent(this NodeArrayStoredComponents components) =>
        StoredExtent(components, 16, 12);

    public static DecodedExtent BasisScaleStoredExtent(this NodeArrayStoredComponents components) =>
        StoredExtent(components, 28, 12);

    private static DecodedExtent StoredExtent(NodeArrayStoredComponents components, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(components);
        var origin = components.Extent;
        if (origin is null || origin.Container is null || origin.Offset < 0 || origin.Length != 40 ||
            origin.Offset > long.MaxValue - 40 || components.RawBytes.Length != 40)
            throw new ArgumentException("A matching forty-byte component extent is required.", nameof(components));
        return new(origin.Container, origin.Offset + offset, length);
    }
}
