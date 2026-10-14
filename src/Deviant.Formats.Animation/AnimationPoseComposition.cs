using System.Numerics;

namespace Deviant.Formats.Animation;

public static class AnimationPoseComposition
{
    /// <summary>Applies a relative track to its joint reference transform using the supplied native translation ratio.</summary>
    public static AnimationTransformSample Relative(AnimationTransformSample sample,
        AnimationTransformSample reference, float translationScale)
    {
        if (!float.IsFinite(translationScale) || translationScale <= 0 ||
            !Finite(sample.Rotation) || !Finite(sample.Translation) || !Finite(sample.Scale) ||
            !Finite(reference.Rotation) || !Finite(reference.Translation) || !Finite(reference.Scale))
            throw new InvalidDataException("AnimationRelativeTransformInvalid");
        var q = new Quaternion(reference.Rotation.X, reference.Rotation.Y, reference.Rotation.Z, reference.Rotation.W);
        var relative = new Quaternion(sample.Rotation.X, sample.Rotation.Y, sample.Rotation.Z, sample.Rotation.W);
        var rotation = Quaternion.Multiply(q, relative);
        var position = Vector3.Transform(new Vector3(sample.Translation.X, sample.Translation.Y, sample.Translation.Z) * translationScale, q) +
            new Vector3(reference.Translation.X, reference.Translation.Y, reference.Translation.Z);
        var result = new AnimationTransformSample(new(rotation.X, rotation.Y, rotation.Z, rotation.W),
            new(position, sample.Translation.W + reference.Translation.W), sample.Scale * reference.Scale);
        if (!Finite(result.Rotation) || !Finite(result.Translation) || !Finite(result.Scale))
            throw new InvalidDataException("AnimationRelativeTransformNonfinite");
        return result;
    }

    private static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);
}
