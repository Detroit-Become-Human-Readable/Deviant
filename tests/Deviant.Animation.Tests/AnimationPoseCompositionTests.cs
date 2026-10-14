using System.Numerics;
using Deviant.Formats.Animation;
using Xunit;

namespace Deviant.Animation.Tests;

public sealed class AnimationPoseCompositionTests
{
    [Fact]
    public void RelativeTrackUsesReferenceRotationTranslationAndScaleInOrder()
    {
        var referenceRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var relativeRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 3);
        var reference = new AnimationTransformSample(Vec(referenceRotation), new(10, 20, 30, 0), new(2, 3, 4, 1));
        var sample = new AnimationTransformSample(Vec(relativeRotation), new(1, 0, 0, 0), new(.5f, 2, .25f, 1));
        var result = AnimationPoseComposition.Relative(sample, reference, 2);
        Assert.InRange(Vector3.Distance(new(result.Translation.X, result.Translation.Y, result.Translation.Z), new(10, 22, 30)), 0, 1e-5f);
        Assert.Equal(new Vector4(1, 6, 1, 1), result.Scale);
        var rotated = Vector3.Transform(Vector3.UnitY, new Quaternion(result.Rotation.X, result.Rotation.Y, result.Rotation.Z, result.Rotation.W));
        var expected = Vector3.Transform(Vector3.Transform(Vector3.UnitY, relativeRotation), referenceRotation);
        Assert.InRange(Vector3.Distance(expected, rotated), 0, 1e-5f);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void UnavailableTranslationRatioIsRejected(float ratio)
    {
        var identity = new AnimationTransformSample(new(0, 0, 0, 1), Vector4.Zero, Vector4.One);
        Assert.Throws<InvalidDataException>(() => AnimationPoseComposition.Relative(identity, identity, ratio));
    }

    private static Vector4 Vec(Quaternion value) => new(value.X, value.Y, value.Z, value.W);
}
