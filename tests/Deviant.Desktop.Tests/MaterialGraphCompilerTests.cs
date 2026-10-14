using System.IO;
using Deviant.Assets;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class MaterialGraphCompilerTests
{
    [Fact]
    public void TerminalColorAndSurfaceLightingDoNotShareAProgramSignature()
    {
        var color = new MaterialExpression(MaterialExpressionOperation.Constant, MaterialValueType.Vector3,
            [], Constants: [.25f, .5f, .75f]);
        var graph = new MaterialSurfaceGraph(color, null, null, [], [], []);
        var lit = MaterialGraphCompiler.Compile(graph);
        var unlit = MaterialGraphCompiler.Compile(graph with { IsUnlit = true });
        Assert.NotEqual(lit.Signature, unlit.Signature);
        Assert.Contains("surfaceUnlit = false", lit.Body);
        Assert.Contains("surfaceUnlit = true", unlit.Body);
    }

    [Fact]
    public void BindsSeparateSourceSamplerGroupsToTheirTextureExpressions()
    {
        var uv = Input(MaterialInput.Uv0, MaterialValueType.Vector2);
        var first = new MaterialExpression(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4, [uv], TextureSlot: 3);
        var second = new MaterialExpression(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4, [uv], TextureSlot: 9);
        var graph = new MaterialSurfaceGraph(Op(MaterialExpressionOperation.Add, MaterialValueType.Vector4, first, second),
            null, null, [MaterialInput.Uv0], [3, 9], []);
        var compiled = MaterialGraphCompiler.Compile(graph, samplerGroups: new Dictionary<int, int> { [3] = 0, [9] = 1 });
        Assert.Contains("surfaceSampler0 : register(s1)", compiled.Declarations);
        Assert.Contains("surfaceSampler1 : register(s2)", compiled.Declarations);
        Assert.Contains("surfaceTexture0.Sample(surfaceSampler0", compiled.Body);
        Assert.Contains("surfaceTexture1.Sample(surfaceSampler1", compiled.Body);
        Assert.DoesNotContain("$sampler", compiled.Body);
        Assert.Throws<InvalidDataException>(() => MaterialGraphCompiler.Compile(graph,
            samplerGroups: new Dictionary<int, int> { [3] = 0, [9] = 15 }));
    }

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
    public void UnsupportedCoverageCannotPublishAnOpaqueColorFallback()
    {
        var graph = new MaterialSurfaceGraph(Constant(.2f, .5f, .8f), Input(MaterialInput.ViewDepth, MaterialValueType.Scalar),
            null, [MaterialInput.ViewDepth], [], []);
        Assert.Throws<InvalidDataException>(() => MaterialGraphCompiler.Compile(graph, new HashSet<MaterialInput>()));
    }

    [Fact]
    public void ViewDepthCoverageCompilesWhenCameraInputIsAvailable()
    {
        var graph = new MaterialSurfaceGraph(Constant(.2f, .5f, .8f), Input(MaterialInput.ViewDepth, MaterialValueType.Scalar),
            null, [MaterialInput.ViewDepth], [], []);
        var program = MaterialGraphCompiler.Compile(graph);
        Assert.True(program.HasOpacity);
        Assert.Empty(program.Diagnostics);
    }

    [Fact]
    public void UsesTheAuthoredSecondaryUvForMaskedCoverage()
    {
        var sample = new MaterialExpression(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4,
            [Input(MaterialInput.Uv2, MaterialValueType.Vector2)], TextureSlot: 0);
        var graph = new MaterialSurfaceGraph(Constant(0, 0, 1), Swizzle(sample, "r"), null, [MaterialInput.Uv2], [0], []);
        var compiled = MaterialGraphCompiler.Compile(graph, new HashSet<MaterialInput> { MaterialInput.Uv2 });
        Assert.True(compiled.HasOpacity);
        Assert.Contains("input.uv2", compiled.Body);
        Assert.Equal([0], compiled.TextureSlots);
        Assert.Throws<InvalidDataException>(() => MaterialGraphCompiler.Compile(graph, new HashSet<MaterialInput> { MaterialInput.Uv0 }));
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
        var result = MaterialGraphCompiler.Compile(new(injection, null, null, [], [], []));
        Assert.False(result.HasBaseColor); Assert.False(result.HasOpacity);
        Assert.Single(result.Diagnostics);
        Assert.DoesNotContain("discard", result.Body); Assert.DoesNotContain("NaN", result.Body);
        Assert.Throws<InvalidDataException>(() => MaterialGraphCompiler.Compile(new(null, Constant(float.NaN), null, [], [], [])));
    }

    [Fact]
    public void MissingCoverageTextureCannotBecomeANeutralOpaqueMaterial()
    {
        var sample = new MaterialExpression(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4,
            [Input(MaterialInput.Uv2, MaterialValueType.Vector2)], TextureSlot: 0);
        var graph = new MaterialSurfaceGraph(Constant(0, 0, 1), Swizzle(sample, "a"), null, [MaterialInput.Uv2], [0], []);
        Assert.Throws<InvalidDataException>(() => AssetViewportService.RestrictMissingTextures(graph,
            new Dictionary<int, Deviant.Browser.RenderTexture>(), []));
    }

    [Fact]
    public void SecondaryUvMaterialCompilesAgainstTheViewportShaderInterface()
    {
        var sample = new MaterialExpression(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4,
            [Input(MaterialInput.Uv2, MaterialValueType.Vector2)], TextureSlot: 0);
        var program = MaterialGraphCompiler.Compile(new(Constant(0, 0, 1), Swizzle(sample, "r"), null,
            [MaterialInput.Uv2], [0], []));
        var layouts = Read("SurfaceLayouts");
        using var vertex = SharpDX.D3DCompiler.ShaderBytecode.Compile(layouts + Read("SurfaceVertex"), "main", "vs_5_0");
        using var pixel = SharpDX.D3DCompiler.ShaderBytecode.Compile(layouts + program.Declarations + Read("SurfacePixel")
            .Replace("// HIDDEN_GRID_HELPER", Read("HiddenGrid"), StringComparison.Ordinal)
            .Replace("// MATERIAL_BODY", program.Body, StringComparison.Ordinal), "main", "ps_5_0");
        Assert.NotEmpty(vertex.Bytecode.Data); Assert.NotEmpty(pixel.Bytecode.Data);

        static string Read(string name)
        {
            using var stream = typeof(AssetViewportService).Assembly.GetManifestResourceStream($"Deviant.Desktop.Rendering.{name}.hlsl")!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    [Fact]
    public void CameraNormalSamplingCompilesAgainstTheViewportShaderInterface()
    {
        var normal = Input(MaterialInput.ViewNormal, MaterialValueType.Vector3);
        var coordinates = Op(MaterialExpressionOperation.Add, MaterialValueType.Vector2,
            Op(MaterialExpressionOperation.Multiply, MaterialValueType.Vector2, Swizzle(normal, "xy"), Constant(.5f)), Constant(.5f));
        var sample = new MaterialExpression(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4, [coordinates], TextureSlot: 0);
        var program = MaterialGraphCompiler.Compile(new(Swizzle(sample, "rgb"), null, null, [MaterialInput.ViewNormal], [0], []));
        Assert.Contains("(float3x3)mView", program.Body);
        Assert.True(program.HasBaseColor);
        string Read(string name)
        {
            using var stream = typeof(AssetViewportService).Assembly.GetManifestResourceStream($"Deviant.Desktop.Rendering.{name}.hlsl")!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        using var pixel = SharpDX.D3DCompiler.ShaderBytecode.Compile(Read("SurfaceLayouts") + program.Declarations + Read("SurfacePixel")
            .Replace("// HIDDEN_GRID_HELPER", Read("HiddenGrid"), StringComparison.Ordinal)
            .Replace("// MATERIAL_BODY", program.Body, StringComparison.Ordinal), "main", "ps_5_0");
        Assert.NotEmpty(pixel.Bytecode.Data);
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
