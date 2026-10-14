using System.Numerics;
using Deviant.Assets;
using Deviant.Formats.Shaders;
using Xunit;

namespace Deviant.Shader.Tests;

public sealed class NativeMaterialInstanceExpressionTests
{
    [Fact]
    public void DistanceUsesOrderedJointReferencesCentimetresAndNativeClamping()
    {
        Vector3[] positions = [new(0, 0, 0), new(0, .03f, 0), new(.04f, .03f, 0)];
        var expression = Record("0.2 * (dist(0,1) - 2)", [2, 0]);
        Assert.True(NativeMaterialInstanceExpression.TryEvaluate(expression, positions, out var value, out var diagnostic));
        Assert.Null(diagnostic); Assert.InRange(value, .59999f, .60001f);
        Assert.True(NativeMaterialInstanceExpression.TryEvaluate(Record("dist(0,1)", [2, 0]), positions, out value, out _));
        Assert.Equal(1, value);
        Assert.True(NativeMaterialInstanceExpression.TryEvaluate(Record("-dist(0,1)", [2, 0]), positions, out value, out _));
        Assert.Equal(0, value);
    }

    [Fact]
    public void DotProductUsesItsMiddleJointAsTheOriginAndHandlesZeroVectors()
    {
        Vector3[] positions = [Vector3.UnitX, Vector3.Zero, new(3, 0, 0)];
        Assert.True(NativeMaterialInstanceExpression.TryEvaluate(Record("dotProduct(0,1,2)", [0, 1, 2]), positions, out var value, out _));
        Assert.Equal(1, value);
        Assert.True(NativeMaterialInstanceExpression.TryEvaluate(Record("dotProduct(1,1,2)", [0, 1, 2]), positions, out value, out _));
        Assert.Equal(0, value);
    }

    [Fact]
    public void EmptyExpressionRetainsTheStoredInitialValueWithoutPoseData()
    {
        Assert.True(NativeMaterialInstanceExpression.TryEvaluate(Record("", [], 2.25f), null, out var value, out _));
        Assert.Equal(2.25f, value);
    }

    [Theory]
    [InlineData("dist(0,1)")]
    [InlineData("dist(0.5,1)")]
    [InlineData("dist(0,2)")]
    [InlineData("arbitrary(0,1)")]
    [InlineData("1/0")]
    [InlineData("1e309")]
    [InlineData("1; 2")]
    public void MissingPoseAndUnsupportedExpressionsDoNotProduceFallbackValues(string source)
    {
        Assert.False(NativeMaterialInstanceExpression.TryEvaluate(Record(source, [0, 1]), null, out _, out var diagnostic));
        Assert.NotNull(diagnostic);
    }

    [Fact]
    public void LimitsCancellationAndInconsistentReferenceCountsAreEnforced()
    {
        var invalid = Record("dist(0,1)", [0, 1]) with { ElementCount = 1 };
        Assert.False(NativeMaterialInstanceExpression.TryEvaluate(invalid, [Vector3.Zero, Vector3.One], out _, out _));
        Assert.False(NativeMaterialInstanceExpression.TryEvaluate(Record(new string('(', 65) + "1" + new string(')', 65), []), null, out _, out _));
        Assert.False(NativeMaterialInstanceExpression.TryEvaluate(Record(new string('1', 1025), []), null, out _, out _));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => NativeMaterialInstanceExpression.TryEvaluate(Record("", []), null, out _, out _, cancel.Token));
    }

    private static NativeMaterialArrayConstant Record(string expression, IReadOnlyList<uint> joints, float initial = 0)
    {
        var extent = new DecodedExtent(new("generated-index", 0), 0, 1);
        return new("parameter", expression, BitConverter.SingleToUInt32Bits(initial), 0, (uint)joints.Count, joints,
            extent, extent, extent, extent, extent, extent, extent);
    }
}
