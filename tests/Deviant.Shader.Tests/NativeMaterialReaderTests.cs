using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Containers;
using Deviant.Formats.Shaders;
using Xunit;

namespace Deviant.Shader.Tests;

public sealed class NativeMaterialReaderTests
{
    private static readonly ResourceIdentity Identity = new("synthetic-index", 3, "native-object/7");
    private static readonly SourceRevision Revision = new(Identity.IndexContentId, "synthetic-revision");

    [Fact]
    public void DirectNormalUsesTheSourceSamplerInsteadOfTheFirstSlot()
    {
        var result = Parse(Frame(3), Source(slot: 2, scaleX: "0.25", scaleY: "-0.75"));

        var asset = Assert.IsType<MaterialAsset>(result.Value);
        var binding = Assert.Single(asset.Bindings);
        Assert.Equal("direct-normal", asset.Profile);
        Assert.Equal(3, asset.References.Count);
        Assert.Equal(2, binding.SlotOrdinal);
        Assert.Equal("1002", binding.Texture.Id);
        Assert.Equal(MaterialTextureRole.Normal, binding.Role);
        Assert.Equal(new Vector2(0.25f, -0.75f), binding.UvScale);
        Assert.True(binding.Evidence.Verified);
        Assert.NotNull(binding.Evidence.DecodedExtent);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void RenamedTemporariesAndNestedScopesResolveByDataflow()
    {
        var source = Source().Replace("horizontal", "repeatU", StringComparison.Ordinal)
            .Replace("vertical", "repeatV", StringComparison.Ordinal)
            .Replace("sampled", "textureValue", StringComparison.Ordinal)
            .Replace("selected", "normalValue", StringComparison.Ordinal)
            .Replace("attributes", "normalAttributes", StringComparison.Ordinal);

        var asset = Parse(Frame(), source).Value!;

        Assert.Single(asset.Bindings);
        Assert.Equal(new Vector2(1, -1), asset.Bindings[0].UvScale);
    }

    [Fact]
    public void ScopedConstantShadowingDoesNotChangeTheOuterSampleCoordinates()
    {
        var source = Source(scaleX: "0.25").Replace("float vertical = -1.0;",
            "float vertical = -1.0; { float horizontal = 7.0; }", StringComparison.Ordinal);

        var binding = Assert.Single(Parse(Frame(), source).Value!.Bindings);

        Assert.Equal(new Vector2(0.25f, -1), binding.UvScale);
    }

    [Fact]
    public void ScalarCountAlignmentMatchesTheDeclaredMaterialField()
    {
        var accepted = Parse(Frame(constants: 1), Source(constants: 1));
        var rejected = Parse(Frame(constants: 1), Source(constants: 0));

        Assert.Single(accepted.Value!.Bindings);
        Assert.Equal("neutral", rejected.Value!.Profile);
        Assert.Contains("MaterialSamplerLayoutMismatch", rejected.Diagnostics);
        Assert.Single(rejected.Value.References);
    }

    [Fact]
    public void ConflictingNormalSourcesProduceNeutralMaterialWithAllReferences()
    {
        var result = Parse(Frame(2), Source(slot: 0), Source(slot: 1));

        Assert.Empty(result.Value!.Bindings);
        Assert.Equal(2, result.Value.References.Count);
        Assert.Contains("MaterialNormalSourcesDisagree", result.Diagnostics);
    }

    [Fact]
    public void SourcesWithoutNormalOutputDoNotInventABinding()
    {
        var result = Parse(Frame(), "#version 450\nvoid main() {}\n");

        Assert.Equal("neutral", result.Value!.Profile);
        Assert.Single(result.Value.References);
        Assert.Contains("MaterialNormalGraphUnsupported", result.Diagnostics);
    }

    [Fact]
    public void NonNormalSourceRegionsCanCoexistWithAgreeingNormalRegions()
    {
        var result = Parse(Frame(), Source(), "#version 450\nvoid main() {}\n", Source());

        Assert.Single(result.Value!.Bindings);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ASourceFromAnotherNativeFrameIsRejectedEvenWithinTheSameContainer()
    {
        var frame = Frame();
        var sources = Scan([Source()], Identity with { MemberPath = "native-object/8" });

        var result = new NativeMaterialReader().Parse(frame, Revision, sources);

        Assert.Empty(result.Value!.Bindings);
        Assert.Single(result.Value.References);
        Assert.Contains("MaterialSourceIdentityMismatch", result.Diagnostics);
    }

    [Theory]
    [InlineData("pass-sampler", "MaterialNormalGraphUnsupported")]
    [InlineData("world-coordinates", "MaterialNormalGraphUnsupported")]
    [InlineData("missing-consumer", "MaterialNormalConsumerUnsupported")]
    [InlineData("out-of-range", "MaterialSamplerSlotInvalid")]
    [InlineData("different-normal-decode", "MaterialNormalConsumerUnsupported")]
    public void UnsupportedDependenciesRetainTheNativeReference(string variant, string diagnostic)
    {
        var source = Source();
        source = variant switch
        {
            "pass-sampler" => source.Replace("g_rb2DTextures", "g_rb2DPassTextures", StringComparison.Ordinal),
            "world-coordinates" => source.Replace("Surface.fUv0", "Surface.fWsPosition.xz", StringComparison.Ordinal),
            "missing-consumer" => source.Replace("ComputeSurfaceFromNormalMap(surface)", "DifferentSurface(surface)", StringComparison.Ordinal),
            "out-of-range" => Source(slot: 2),
            "different-normal-decode" => source.Replace("2.0 * encoded", "3.0 * encoded", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };

        var result = Parse(Frame(), source);

        Assert.Equal("neutral", result.Value!.Profile);
        Assert.Single(result.Value.References);
        Assert.Empty(result.Value.Bindings);
        Assert.Contains(diagnostic, result.Diagnostics);
    }

    [Fact]
    public void NativeProgramCountsAreBoundedBeforeWalkingRecords()
    {
        var frame = Frame();
        var bytes = frame.RawPayload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(96), 93);

        var result = Parse(frame with { RawPayload = bytes }, Source());

        Assert.Contains("MaterialProgramCountLimit", result.Diagnostics);
        Assert.Single(result.Value!.References);
    }

    [Fact]
    public void TruncatedProgramTablesCannotYieldAValidatedLayout()
    {
        var frame = Frame();
        var bytes = frame.RawPayload[..^1];
        frame = frame with { RawPayload = bytes, PayloadExtent = frame.PayloadExtent with { Length = bytes.Length } };

        var result = Parse(frame, Source());

        Assert.Contains("MaterialProgramTruncated", result.Diagnostics);
        Assert.Empty(result.Value!.Bindings);
    }

    [Fact]
    public void SourceAndExpressionBudgetsAreEnforced()
    {
        var oversized = Source() + "//" + new string('a', NativeMaterialReader.MaximumSourceBytes);
        var deep = Source().Replace("Surface.fUv0 * vec2(horizontal, vertical)",
            new string('(', NativeMaterialReader.MaximumExpressionDepth + 1) + "Surface.fUv0" +
            new string(')', NativeMaterialReader.MaximumExpressionDepth + 1), StringComparison.Ordinal);
        var tokenHeavy = Source() + string.Concat(Enumerable.Repeat("x;", NativeMaterialReader.MaximumSourceTokens));

        Assert.Contains("MaterialSourceByteLimit", Parse(Frame(), oversized).Diagnostics);
        Assert.Contains("MaterialExpressionDepthLimit", Parse(Frame(), deep).Diagnostics);
        Assert.Contains("MaterialSourceTokenLimit", Parse(Frame(), tokenHeavy).Diagnostics);
    }

    [Fact]
    public void CancellationIsObservedBeforeReading()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new NativeMaterialReader().Parse(Frame(), Revision,
            Scan([Source()], Identity), cancellation.Token));
    }

    [Fact]
    public void SurfaceGraphsExposeColorAlphaAndCutoffWithoutDuplicatingBindings()
    {
        var result = Parse(Frame(2), SurfaceSource());
        var graph = Assert.IsType<MaterialSurfaceGraph>(result.Value!.SurfaceGraph);

        Assert.Equal("surface-expression", result.Value.Profile);
        Assert.Equal(MaterialValueType.Vector3, graph.BaseColor!.ValueType);
        Assert.Equal(MaterialValueType.Scalar, graph.Opacity!.ValueType);
        Assert.Equal(0.42f, Assert.Single(graph.AlphaCutoff!.Constants!));
        Assert.Equal([1], graph.TextureSlots);
        Assert.Contains(MaterialInput.Uv0, graph.RequiredInputs);
        Assert.Single(result.Value.Bindings); // Existing normal binding remains independent.
        Assert.Equal(2, result.Value.References.Count);
    }

    [Fact]
    public void WorldProjectedMasksAndVertexChannelsRemainTypedInputs()
    {
        var body = """
            vec4 paint = texture(sampler2D(g_rb2DTextures[Material.s1 + glslHack].rHandle.xy), Surface.fWsPosition.xz * vec2(0.2, -0.3));
            vec4 mask = texture(sampler2D(g_rb2DTextures[Material.s2 + glslHack].rHandle.xy), Surface.fUv0);
            output.baseColor = paint.rgb * vec3(0.3, 0.4, 0.5);
            output.transparency = 1.0 - saturate((Surface.fVertexColor.r - mask.r) / max(0.7, 0.01));
            output.alphaTestValue = 0.05;
            """;
        var graph = Parse(Frame(3), SurfaceSource(body)).Value!.SurfaceGraph!;

        Assert.NotNull(graph.BaseColor);
        Assert.NotNull(graph.Opacity);
        Assert.Equal([1, 2], graph.TextureSlots);
        Assert.Contains(MaterialInput.WorldPosition, graph.RequiredInputs);
        Assert.Contains(MaterialInput.VertexColor, graph.RequiredInputs);
    }

    [Fact]
    public void UnknownNormalBlendDoesNotEraseIndependentSurfaceColor()
    {
        var source = SurfaceSource().Replace("attributes.normalMap = selected;", "attributes.normalMap = customNormalBlend(selected);", StringComparison.Ordinal);
        var result = Parse(Frame(2), source);

        Assert.Empty(result.Value!.Bindings);
        Assert.NotNull(result.Value.SurfaceGraph!.BaseColor);
        Assert.Equal([1], result.Value.SurfaceGraph.TextureSlots);
    }

    [Fact]
    public void ConflictingColorOutputsDoNotEraseAgreeingOpacity()
    {
        var first = SurfaceSource();
        var second = first.Replace("vec3(0.2, 0.3, 0.4)", "vec3(0.4, 0.3, 0.2)", StringComparison.Ordinal);
        var graph = Parse(Frame(2), first, second).Value!.SurfaceGraph!;

        Assert.Null(graph.BaseColor);
        Assert.NotNull(graph.Opacity);
        Assert.Contains("MaterialBaseColorSourcesDisagree", graph.Diagnostics);
    }

    [Fact]
    public void UnsupportedConditionalWritesCannotBecomeUnconditionalColor()
    {
        var body = """
            output.baseColor = vec3(0.2, 0.3, 0.4);
            output.transparency = 1.0;
            if (UnknownRuntimeCondition) { output.baseColor = vec3(0.8, 0.7, 0.6); }
            """;
        var graph = Parse(Frame(2), SurfaceSource(body)).Value!.SurfaceGraph!;

        Assert.Null(graph.BaseColor);
        Assert.NotNull(graph.Opacity);
    }

    [Fact]
    public void ScalarInitialValuesRequireAnExplicitPreviewPolicy()
    {
        var source = SurfaceSource("output.baseColor = vec3(Material.c0); output.transparency = 1.0;", constants: 1);
        var frame = Frame(2, constants: 1);
        var standard = new NativeMaterialReader().Parse(frame, Revision, Scan([source], Identity));
        var initial = new NativeMaterialReader { UseSerializedScalarInitialValues = true }.Parse(frame, Revision, Scan([source], Identity));

        Assert.Null(standard.Value!.SurfaceGraph!.BaseColor);
        Assert.NotNull(initial.Value!.SurfaceGraph!.BaseColor);
        Assert.Contains("MaterialPreviewUsesSerializedScalarInitialValues", initial.Value.SurfaceGraph.Diagnostics);
    }

    [Theory]
    [InlineData("min(abs(Surface.fVertexColor.rgb), vec3(0.7))")]
    [InlineData("max(floor(Surface.fVertexColor.rgb), fract(Surface.fVertexColor.rgb))")]
    [InlineData("step(vec3(0.2), smoothstep(vec3(0.1), vec3(0.9), Surface.fVertexColor.rgb))")]
    [InlineData("vec3(dot(normalize(Surface.fWsNormal), normalize(Surface.fWsCameraDir)))")]
    [InlineData("vec3(sin(Surface.fVertexColor.r), cos(Surface.fVertexColor.g), pow(Surface.fVertexColor.b, 2.0))")]
    public void PureSurfaceOperationsBuildValidatedGraphs(string expression)
    {
        var graph = Parse(Frame(2), SurfaceSource("output.baseColor = " + expression + "; output.transparency = 1.0; output.alphaTestValue = 0.05;")).Value!.SurfaceGraph!;

        Assert.NotNull(graph.BaseColor);
        Assert.Empty(graph.Diagnostics);
    }

    [Fact]
    public void SourceDefinedIdentityHelperPreservesViewDepthWithoutNameRules()
    {
        var source = SurfaceSource("output.baseColor = vec3(0.3); output.transparency = saturate(passthrough(Surface.fVsDepth) / 2.0); output.alphaTestValue = 0.05;")
            .Replace("#version 450", "#version 450\nfloat passthrough(float value) { return value; }", StringComparison.Ordinal);
        var graph = Parse(Frame(2), source).Value!.SurfaceGraph!;

        Assert.NotNull(graph.Opacity);
        Assert.Contains(MaterialInput.ViewDepth, graph.RequiredInputs);
        Assert.Empty(graph.Diagnostics);
    }

    [Fact]
    public void WholeVectorWriteReplacesPreviouslyWrittenComponentAliases()
    {
        var source = SurfaceSource("vec3 tint = vec3(0.2); tint.r = 0.8; tint = vec3(0.4); output.baseColor = tint.rgb; output.transparency = 1.0; output.alphaTestValue = 0.05;");
        var graph = Parse(Frame(2), source).Value!.SurfaceGraph!;

        Assert.Equal([0.4f, 0.4f, 0.4f], graph.BaseColor!.Constants);
    }

    private static string SurfaceSource(string? body = null, int constants = 0)
    {
        body ??= """
            vec4 pigment = texture(sampler2D(g_rb2DTextures[Material.s1 + glslHack].rHandle.xy), Surface.fUv0 * vec2(0.2, -0.4));
            output.baseColor = mix(vec3(0.2, 0.3, 0.4), pigment.rgb * vec3(0.7, 0.8, 0.9) + vec3(0.01), 0.25);
            output.transparency = clamp(pow(pigment.a, 2.0), 0.0, 1.0);
            output.alphaTestValue = 0.42;
            """;
        var function = "S_MATERIAL_ATTRIBUTES ComputeHypershade(S_SURFACE Surface, S_MATERIAL Material) { S_MATERIAL_ATTRIBUTES output; " + body + " return output; }\n";
        return Source(constants: constants).Replace("void main()", function + "void main()", StringComparison.Ordinal)
            .Replace("surface = ComputeSurfaceFromNormalMap(surface);", "surface = ComputeSurfaceFromNormalMap(surface);\nS_MATERIAL_ATTRIBUTES visible = ComputeHypershade(surface, Material);\nif (visible.transparency < visible.alphaTestValue) discard;", StringComparison.Ordinal);
    }

    private static DecodeResult<MaterialAsset> Parse(NativeObjectFrame frame, params string[] sources) =>
        new NativeMaterialReader().Parse(frame, Revision, Scan(sources, Identity));

    private static NativeObjectFrame Frame(int slots = 1, int constants = 0)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(3u); writer.Write("COM_CONT"u8); writer.Write(6u); writer.Write(0u);
        writer.Write("LOADCONT"u8); writer.Write(2u); writer.Write("SHADCUST"u8); writer.Write(91u);
        writer.Write(new byte[35]); writer.Write((uint)slots);
        for (var slot = 0; slot < slots; slot++) { writer.Write(0u); writer.Write(2137u); writer.Write((uint)(1000 + slot)); writer.Write((byte)0); }
        writer.Write(0u); writer.Write(0u); writer.Write((uint)constants); writer.Write(0u);
        for (var scalar = 0; scalar < constants; scalar++)
        { writer.Write(0u); writer.Write(0u); writer.Write(0f); writer.Write(0u); }
        var bytes = stream.ToArray();
        var header = new byte[52]; header[4] = 1; BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(5), 1);
        var extent = new DecodedExtent(Identity, 0, bytes.Length);
        return new(Identity, 0, 2133, 7, new(Identity, 0, 8), new(Identity, 0, 52), extent, header, bytes, 1, 1, 0, 3);
    }

    private static StoredGlslScan Scan(string[] sources, ResourceIdentity identity)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join('\0', sources) + '\0');
        var physical = new SourceExtent("synthetic-package.bin", 0, bytes.Length);
        var chunk = new SegsChunk(0, 0, 0, 0, 0, bytes.Length, physical, new(identity, 0, bytes.Length), bytes);
        var document = new SegsDocument(1, 1, (uint)bytes.Length, (uint)bytes.Length, 16, 16, bytes.Length,
            [chunk], [], bytes, null, [], null);
        var decoded = new DecodeResult<SegsDocument>(document, identity, Revision, [physical], [], [], [], new Dictionary<string, string>());
        return StoredGlslReader.Scan(decoded);
    }

    private static string Source(int slot = 0, int constants = 0, string scaleX = "1.0", string scaleY = "-1.0")
    {
        var scalarFields = string.Join('\n', Enumerable.Range(0, (constants + 3) & ~3).Select(index => $"float c{index};"));
        return $$"""
            #version 450
            struct S_MATERIAL {
                vec4 errorColor; float depth; float padA; float padB; float padC;
                {{scalarFields}}
                uint s0; uint s1; uint s2; uint s3;
            };
            vec3 ComputeNormal(vec3 encoded, vec3 tangent, vec3 binormal, vec3 geometryNormal) {
                vec3 signedNormal = 2.0 * encoded - vec3(1.0);
                mat3 basis = mat3(tangent, binormal, geometryNormal);
                return normalize(basis * signedNormal);
            }
            S_SURFACE ComputeNormalMap(S_SURFACE Surface, S_MATERIAL Material) {
                MaterialForNormalMap attributes;
                { float horizontal = {{scaleX}}; float vertical = {{scaleY}};
                  vec4 sampled = texture(sampler2D(g_rb2DTextures[Material.s{{slot}} + glslHack].rHandle.xy), Surface.fUv0 * vec2(horizontal, vertical));
                  vec3 selected; selected.rgb = sampled.rgb;
                  attributes.normalMap = selected; }
                S_SURFACE next = Surface;
                next.fTsNormal = attributes.normalMap;
                return next;
            }
            S_SURFACE ComputeSurfaceFromNormalMap(S_SURFACE Surface) {
                Surface.normal = ComputeNormal(Surface.fTsNormal, Surface.tangent, Surface.binormal, Surface.geometryNormal);
                return Surface;
            }
            void main() {
                S_SURFACE surface = ComputeNormalMap(Surface, Material);
                surface = ComputeSurfaceFromNormalMap(surface);
            }
            """;
    }
}
