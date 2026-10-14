using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Deviant.Assets;
using Deviant.Export.Models;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class MaterialSurfaceExpressionEvaluatorTests
{
    [Fact]
    public void MultiUvTextureGraphSupportsSwizzlesMixAndScalarOpacityBroadcast()
    {
        var uv0 = Input(MaterialInput.Uv0, MaterialValueType.Vector2);
        var uv1 = Input(MaterialInput.Uv1, MaterialValueType.Vector2);
        var sample0 = Texture(2, uv0);
        var sample1 = Texture(7, uv1);
        var blended = new MaterialExpression(MaterialExpressionOperation.Mix, MaterialValueType.Vector4,
            [sample0, sample1, Scalar(0.5f)]);
        var alpha = new MaterialExpression(MaterialExpressionOperation.Swizzle, MaterialValueType.Scalar,
            [sample0], Components: "a");
        var opacity = Binary(MaterialExpressionOperation.Multiply, MaterialValueType.Scalar, alpha, Scalar(0.25f));
        var root = Binary(MaterialExpressionOperation.Multiply, MaterialValueType.Vector4, blended, opacity);
        var inputs = new Dictionary<MaterialInput, Vector4>
        {
            [MaterialInput.Uv0] = new(0.1f, 0.2f, 0f, 0f),
            [MaterialInput.Uv1] = new(0.8f, 0.9f, 0f, 0f)
        };
        var textureCalls = new List<(int Slot, Vector2 Uv)>();

        var evaluator = new MaterialSurfaceExpressionEvaluator(root);
        var result = evaluator.Evaluate(
            input => inputs[input],
            (slot, uv) =>
            {
                textureCalls.Add((slot, uv));
                return slot switch
                {
                    2 => new Vector4(0.2f, 0.4f, 0.6f, 0.8f),
                    7 => new Vector4(0.8f, 0.6f, 0.4f, 0.2f),
                    _ => throw new InvalidOperationException("Unexpected texture slot.")
                };
            });

        Assert.Equal(new Vector4(0.1f), result);
        Assert.Equal(2, textureCalls.Count);
        Assert.Equal(2, textureCalls[0].Slot);
        Assert.Equal(new Vector2(0.1f, 0.2f), textureCalls[0].Uv);
        Assert.Equal(7, textureCalls[1].Slot);
        Assert.Equal(new Vector2(0.8f, 0.9f), textureCalls[1].Uv);
        Assert.Equal(MaterialValueType.Vector4, evaluator.RootValueType);
    }

    [Fact]
    public void FractUsesFloorForNegativeValues()
    {
        var root = new MaterialExpression(MaterialExpressionOperation.Fract, MaterialValueType.Scalar,
            [Scalar(-1.25f)]);

        var result = Evaluate(root);

        Assert.Equal(0.75f, result.X, 6);
        Assert.Equal(new Vector4(0.75f, 0f, 0f, 0f), result);
    }

    [Fact]
    public void NormalizeAndVectorConstructionPreserveDeclaredWidth()
    {
        var vector = new MaterialExpression(MaterialExpressionOperation.Construct, MaterialValueType.Vector3,
            [Scalar(3f), Scalar(0f), Scalar(4f)]);
        var root = new MaterialExpression(MaterialExpressionOperation.Normalize, MaterialValueType.Vector3, [vector]);

        var result = Evaluate(root);

        Assert.Equal(0.6f, result.X, 6);
        Assert.Equal(0f, result.Y);
        Assert.Equal(0.8f, result.Z, 6);
        Assert.Equal(0f, result.W);
    }

    [Fact]
    public void CancellationIsCheckedBeforeEvaluationCallbacks()
    {
        var evaluator = new MaterialSurfaceExpressionEvaluator(Scalar(1f));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var callbackCount = 0;

        Assert.Throws<OperationCanceledException>(() => evaluator.Evaluate(
            _ => { callbackCount++; return Vector4.Zero; },
            (_, _) => { callbackCount++; return Vector4.Zero; },
            cancellation.Token));
        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public void ConstructorRejectsMalformedArityTypesCyclesAndExcessiveDepth()
    {
        var wrongArity = new MaterialExpression(MaterialExpressionOperation.Add, MaterialValueType.Scalar, [Scalar(1f)]);
        var wrongTypes = Binary(MaterialExpressionOperation.Add, MaterialValueType.Vector3,
            new MaterialExpression(MaterialExpressionOperation.Input, MaterialValueType.Vector2, [], Input: MaterialInput.Uv0),
            Vector3Constant(new Vector3(1f, 2f, 3f)));
        var cycleArguments = new List<MaterialExpression>();
        var cyclic = new MaterialExpression(MaterialExpressionOperation.Negate, MaterialValueType.Scalar, cycleArguments);
        cycleArguments.Add(cyclic);

        var tooDeep = Scalar(1f);
        for (var index = 0; index < 64; index++)
            tooDeep = new MaterialExpression(MaterialExpressionOperation.Negate, MaterialValueType.Scalar, [tooDeep]);

        Assert.Throws<ArgumentException>(() => new MaterialSurfaceExpressionEvaluator(wrongArity));
        Assert.Throws<ArgumentException>(() => new MaterialSurfaceExpressionEvaluator(wrongTypes));
        Assert.Throws<ArgumentException>(() => new MaterialSurfaceExpressionEvaluator(cyclic));
        Assert.Throws<ArgumentException>(() => new MaterialSurfaceExpressionEvaluator(tooDeep));
    }

    [Fact]
    public void SharedDagNodeIsEvaluatedOncePerSample()
    {
        var shared = Input(MaterialInput.ViewDepth, MaterialValueType.Scalar);
        var root = Binary(MaterialExpressionOperation.Add, MaterialValueType.Scalar, shared, shared);
        var inputCalls = 0;

        var result = Evaluate(root, input: _ =>
        {
            inputCalls++;
            return new Vector4(0.75f, 0f, 0f, 0f);
        });

        Assert.Equal(1.5f, result.X);
        Assert.Equal(1, inputCalls);
    }

    [Fact]
    public void DivisionByZeroAndNonfiniteValuesAreRejected()
    {
        var division = Binary(MaterialExpressionOperation.Divide, MaterialValueType.Scalar, Scalar(2f), Scalar(0f));
        var nonfiniteInput = Input(MaterialInput.ViewDepth, MaterialValueType.Scalar);

        Assert.Throws<InvalidOperationException>(() => Evaluate(division));
        Assert.Throws<ArgumentException>(() => new MaterialSurfaceExpressionEvaluator(Scalar(float.PositiveInfinity)));
        Assert.Throws<InvalidOperationException>(() => Evaluate(nonfiniteInput,
            input: _ => new Vector4(float.NaN, 0f, 0f, 0f)));
    }

    private static Vector4 Evaluate(
        MaterialExpression root,
        Func<MaterialInput, Vector4>? input = null,
        Func<int, Vector2, Vector4>? texture = null) =>
        new MaterialSurfaceExpressionEvaluator(root).Evaluate(
            input ?? (_ => Vector4.Zero),
            texture ?? ((_, _) => Vector4.Zero));

    private static MaterialExpression Scalar(float value) =>
        new(MaterialExpressionOperation.Constant, MaterialValueType.Scalar, [], Constants: [value]);

    private static MaterialExpression Vector3Constant(Vector3 value) =>
        new(MaterialExpressionOperation.Constant, MaterialValueType.Vector3, [], Constants: [value.X, value.Y, value.Z]);

    private static MaterialExpression Input(MaterialInput input, MaterialValueType type) =>
        new(MaterialExpressionOperation.Input, type, [], Input: input);

    private static MaterialExpression Texture(int slot, MaterialExpression uv) =>
        new(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4, [uv], TextureSlot: slot);

    private static MaterialExpression Binary(
        MaterialExpressionOperation operation,
        MaterialValueType type,
        MaterialExpression left,
        MaterialExpression right) =>
        new(operation, type, [left, right]);
}
