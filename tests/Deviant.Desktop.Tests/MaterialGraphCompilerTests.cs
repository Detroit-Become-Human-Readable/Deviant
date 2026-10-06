using Deviant.Assets;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class MaterialGraphCompilerTests
{
    [Fact]
    public void CompactsNativeSamplerSlotsAndKeepsColorOpacityAndCutoffIndependent()
    {
        var uv = Input(MaterialInput.Uv0, MaterialValueType.Vector2);
        var scaled = Op(MaterialExpressionOperation.Multiply, MaterialValueType.Vector2, uv, Constant(1, -1));
        var sample = new MaterialExpression(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4, [scaled], TextureSlot: 57);
        var graph = new MaterialSurfaceGraph(Swizzle(sample, "rgb"), Swizzle(sample, "a"), Constant(.375f),
            [MaterialInput.Uv0], [57], []);
        var result = MaterialGraphCompiler.Compile(graph);
        Assert.Equal([57], result.TextureSlots);
        Assert.True(result.HasBaseColor); Assert.True(result.HasOpacity); Assert.True(result.HasAlphaCutoff);
        Assert.Empty(result.Diagnostics);
        Assert.Contains("register(t64)", result.Declarations);
        Assert.Contains("surfaceTexture0.Sample", result.Body);
        Assert.DoesNotContain("$texture", result.Body);
        Assert.Contains("clip(surfaceOpacity", result.Body);
        Assert.Contains("-1.0f", result.Body);
    }

    [Fact]
    public void UnsupportedOpacityInputStillPublishesSupportedColor()
    {
        var graph = new MaterialSurfaceGraph(Constant(.2f, .5f, .8f), Input(MaterialInput.ViewDepth, MaterialValueType.Scalar),
            null, [MaterialInput.ViewDepth], [], []);
        var result = MaterialGraphCompiler.Compile(graph);
        Assert.True(result.HasBaseColor); Assert.False(result.HasOpacity);
        Assert.Single(result.Diagnostics);
        Assert.Contains("input.cDiffuse.a", result.Body);
        Assert.DoesNotContain("ViewDepth", result.Body);
    }

    [Fact]
    public void SupportsWorldProjectionAndVertexColorMaskBlending()
    {
        var world = Swizzle(Input(MaterialInput.WorldPosition, MaterialValueType.Vector3), "xz");
        var sample = new MaterialExpression(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4,
            [Op(MaterialExpressionOperation.Multiply, MaterialValueType.Vector2, world, Constant(.25f))], TextureSlot: 2);
        var blend = Swizzle(Input(MaterialInput.VertexColor, MaterialValueType.Vector4), "r");
        var color = Op(MaterialExpressionOperation.Mix, MaterialValueType.Vector3, Swizzle(sample, "rgb"), Constant(.6f, .4f, .2f), blend);
        var result = MaterialGraphCompiler.Compile(new(color, null, null,
            [MaterialInput.WorldPosition, MaterialInput.VertexColor], [2], []));
        Assert.True(result.HasBaseColor); Assert.Empty(result.Diagnostics);
        Assert.Contains("input.wp.xyz", result.Body); Assert.Contains("input.c", result.Body); Assert.Contains("lerp(", result.Body);
    }

    [Fact]
    public void RejectsChannelTextAndNonfiniteConstantsWithoutEmittingTheirText()
    {
        var injection = new MaterialExpression(MaterialExpressionOperation.Swizzle, MaterialValueType.Vector3,
            [Constant(0, 0, 0, 1)], Components: "rgb;discard");
        var result = MaterialGraphCompiler.Compile(new(injection, Constant(float.NaN), null, [], [], []));
        Assert.False(result.HasBaseColor); Assert.False(result.HasOpacity);
        Assert.Equal(2, result.Diagnostics.Count);
        Assert.DoesNotContain("discard", result.Body); Assert.DoesNotContain("NaN", result.Body);
    }

    [Fact]
    public void RejectsCyclesAndCancellation()
    {
        var arguments = new List<MaterialExpression>();
        var cycle = new MaterialExpression(MaterialExpressionOperation.Abs, MaterialValueType.Vector3, arguments);
        arguments.Add(cycle);
        var result = MaterialGraphCompiler.Compile(new(cycle, null, null, [], [], []));
        Assert.False(result.HasBaseColor); Assert.Single(result.Diagnostics);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => MaterialGraphCompiler.Compile(new(Constant(1, 1, 1), null, null, [], [], []), token: cancellation.Token));
    }

    [Fact]
    public void TextureLimitRejectsOnlyTheAffectedOutput()
    {
        MaterialExpression color = Constant(0, 0, 0);
        for (int slot = 0; slot <= MaterialGraphCompiler.MaximumTextureSlots; slot++)
        {
            var sample = new MaterialExpression(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4,
                [Input(MaterialInput.Uv0, MaterialValueType.Vector2)], TextureSlot: slot);
            color = Op(MaterialExpressionOperation.Add, MaterialValueType.Vector3, color, Swizzle(sample, "rgb"));
        }
        var graph = new MaterialSurfaceGraph(color, Constant(.5f), null, [MaterialInput.Uv0],
            Enumerable.Range(0, MaterialGraphCompiler.MaximumTextureSlots + 1).ToArray(), []);
        var result = MaterialGraphCompiler.Compile(graph);
        Assert.False(result.HasBaseColor); Assert.True(result.HasOpacity);
        Assert.Empty(result.TextureSlots); Assert.Single(result.Diagnostics);
    }

    private static MaterialExpression Input(MaterialInput input, MaterialValueType type) => new(MaterialExpressionOperation.Input, type, [], Input: input);
    private static MaterialExpression Constant(params float[] values) => new(MaterialExpressionOperation.Constant,
        (MaterialValueType)values.Length, [], Constants: values);
    private static MaterialExpression Swizzle(MaterialExpression value, string channels) => new(MaterialExpressionOperation.Swizzle,
        (MaterialValueType)channels.Length, [value], Components: channels);
    private static MaterialExpression Op(MaterialExpressionOperation operation, MaterialValueType type, params MaterialExpression[] arguments)
        => new(operation, type, arguments);
}
