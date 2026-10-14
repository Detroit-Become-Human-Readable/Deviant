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
    public void TerminalRgbaOutputUsesItsSampledIntensityAndAlphaWithoutLighting()
    {
        var source = SurfaceSource("output.intensity = texture(sampler2D(g_rb2DTextures[Material.s0 + glslHack].rHandle.xy), Surface.fUv0).r;",
            mainConsumer: "finalPixel.rgba = vec4(1.0 - visible.intensity);",
            outputDeclaration: "layout(location = 0) out vec4 finalPixel;");
        var graph = Assert.IsType<MaterialSurfaceGraph>(Parse(Frame(), source).Value!.SurfaceGraph);
        Assert.True(graph.IsUnlit);
        Assert.NotNull(graph.BaseColor);
        Assert.NotNull(graph.Opacity);
        Assert.Equal([0], graph.TextureSlots);
        Assert.Contains(MaterialInput.Uv0, graph.RequiredInputs);
    }

    [Theory]
    [InlineData("if (Surface.fVertexColor.r > 0.5) finalPixel = vec4(visible.intensity);")]
    [InlineData("finalPixel = vec4(visible.intensity); finalPixel = vec4(0.0);")]
    public void ConditionalOrRewrittenTerminalOutputsDoNotAcquireAnUnlitGraph(string consumer)
    {
        var source = SurfaceSource("output.intensity = 0.25;", mainConsumer: consumer,
            outputDeclaration: "layout(location = 0) out vec4 finalPixel;");
        Assert.Null(Parse(Frame(), source).Value!.SurfaceGraph);
    }

    [Fact]
    public void NativeRenderParametersRetainTheDrawStatePrefixAndItsSourceLocation()
    {
        var fields = Enumerable.Range(0, 35).Select(value => (byte)value).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(fields, 11);
        BinaryPrimitives.WriteUInt32LittleEndian(fields.AsSpan(4), 0x87654309);
        BinaryPrimitives.WriteUInt32LittleEndian(fields.AsSpan(12), 0xFEDCBA98);
        BinaryPrimitives.WriteUInt32LittleEndian(fields.AsSpan(20), BitConverter.SingleToUInt32Bits(-0.375f));
        fields[30] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(fields.AsSpan(26), 0x13579BDF);
        var frame = Frame(fixedFields: fields);
        frame = frame with { PayloadExtent = new(Identity, 4096, frame.RawPayload.Length) };

        var asset = Parse(frame, Source()).Value!;
        var state = Assert.IsType<NativeMaterialRenderParameters>(asset.NativeRenderParameters);
        Assert.Equal(11u, state.ProgramMode);
        Assert.Equal(0x87654309u, state.StoredBlendSelector);
        Assert.Equal((byte)9, state.BlendSelector);
        Assert.Equal(0xFEDCBA98u, state.Flags);
        Assert.Equal(-0.375f, state.DepthBias);
        Assert.Equal((byte)2, state.DrawSwitch);
        Assert.Equal(0x13579BDFu, state.RenderOptions);
        Assert.Equal(new DecodedExtent(Identity, 4140, 35), state.SourceExtent);
    }

    [Fact]
    public void ValidNativeStateSurvivesAnUnavailableSurfaceGraph()
    {
        var asset = Parse(Frame(), "#version 450\nvoid main() {}\n").Value!;
        Assert.Null(asset.SurfaceGraph);
        Assert.NotNull(asset.NativeRenderParameters);
    }

    [Fact]
    public void NativeRenderParametersPreserveNonfiniteDepthBitsWithoutSerializingANonfiniteFloat()
    {
        var fields = new byte[35];
        BinaryPrimitives.WriteUInt32LittleEndian(fields.AsSpan(20), 0x7FC01234);
        var state = Parse(Frame(fixedFields: fields), Source()).Value!.NativeRenderParameters!;
        Assert.Equal(0x7FC01234u, state.DepthBiasBits);
        Assert.Null(state.DepthBias);
        Assert.Contains("\"DepthBias\":null", System.Text.Json.JsonSerializer.Serialize(state));
    }

    [Fact]
    public void UnsupportedMaterialEnvelopeDoesNotAcquireNativeRenderParameters()
    {
        var frame = Frame();
        var bytes = frame.RawPayload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 92);
        var asset = Parse(frame with { RawPayload = bytes }, Source()).Value!;
        Assert.Null(asset.NativeRenderParameters);
        Assert.Contains("UsageVersionUnsupported", asset.Diagnostics);
    }

    [Theory]
    [InlineData("g", true)]
    [InlineData("r", false)]
    [InlineData("b", false)]
    public void VectorDivisionRetainsOnlyDefinedSelectedComponents(string component, bool supported)
    {
        var shader = SurfaceSource(body: "vec3 divided = vec3(4.0, 6.0, 8.0) / vec3(0.0, 3.0, 0.0); " +
            "vec3 rounded = floor(divided); vec3 copied; copied.xyz = rounded.rgb; " +
            "output.diffuseColor = vec3(copied." + component + ");");
        var graph = Assert.IsType<MaterialSurfaceGraph>(Parse(Frame(), shader).Value!.SurfaceGraph);
        Assert.Equal(supported, graph.BaseColor is not null);
        if (supported)
        {
            var operation = Assert.IsType<MaterialExpression>(graph.BaseColor!.Arguments.Single());
            Assert.Equal(MaterialExpressionOperation.Floor, operation.Operation);
            Assert.Equal(2f, Assert.Single(operation.Arguments[0].Constants!));
        }
    }

    [Theory]
    [InlineData("dot(divided, vec3(1.0))")]
    [InlineData("normalize(divided).g")]
    [InlineData("divided")]
    public void OperationsRequiringUndefinedVectorComponentsRemainUnsupported(string expression)
    {
        var shader = SurfaceSource(body: "vec3 divided = vec3(4.0, 6.0, 8.0) / vec3(0.0, 3.0, 0.0); " +
            "output.diffuseColor = vec3(" + expression + ");");
        Assert.Null(Parse(Frame(), shader).Value!.SurfaceGraph!.BaseColor);
    }

    [Fact]
    public void ComponentArithmeticRetainsItsTextureDataflowAfterPartialVectorDivision()
    {
        var shader = SurfaceSource(body: "vec3 divided = vec3(4.0, 6.0, 8.0) / vec3(0.0, 3.0, 0.0); " +
            "vec3 scaled = clamp(floor(divided) * 2.0 + vec3(1.0), 0.0, 10.0); " +
            "vec4 sampled = texture(sampler2D(g_rb2DTextures[Material.s0 + glslHack].rHandle.xy), " +
            "Surface.fUv0 + vec2(scaled.g)); output.diffuseColor = sampled.rgb;");
        var graph = Assert.IsType<MaterialSurfaceGraph>(Parse(Frame(), shader).Value!.SurfaceGraph);
        Assert.NotNull(graph.BaseColor);
        Assert.Contains(0, graph.TextureSlots);
        Assert.Contains(MaterialInput.Uv0, graph.RequiredInputs);
    }

    [Theory]
    [InlineData("vec3 copied = vec3(0.0); copied.xyz = 1.0; output.diffuseColor = copied;")]
    [InlineData("output.diffuseColor = mix(0.0, vec3(1.0), vec3(0.5));")]
    [InlineData("output.diffuseColor = clamp(0.5, vec3(0.0), vec3(1.0));")]
    [InlineData("output.diffuseColor = vec3(step(vec3(0.5), 1.0));")]
    [InlineData("output.diffuseColor = pow(vec3(0.5), 2.0);")]
    public void InvalidScalarVectorOverloadsDoNotProduceSupportedGraphs(string body)
    {
        Assert.Null(Parse(Frame(), SurfaceSource(body)).Value!.SurfaceGraph!.BaseColor);
    }

    [Fact]
    public void VectorComparisonAfterComponentAssignmentsPreservesItsBlend()
    {
        var shader = SurfaceSource(body: "vec4 sampled = texture(sampler2D(g_rb2DTextures[Material.s0 + glslHack].rHandle.xy), Surface.fUv0); " +
            "vec3 copied; copied.rgb = sampled.rgb; vec3 choose = vec3(lessThan(copied, vec3(0.5))); " +
            "output.diffuseColor = mix(vec3(1.0), copied, choose);");
        var graph = Assert.IsType<MaterialSurfaceGraph>(Parse(Frame(), shader).Value!.SurfaceGraph);
        Assert.NotNull(graph.BaseColor);
        Assert.Contains(0, graph.TextureSlots);
    }

    [Fact]
    public void DiffuseColorOutputRetainsItsTextureDataflow()
    {
        var shader = SurfaceSource(body: "vec4 pigment = texture(sampler2D(g_rb2DTextures[Material.s0 + glslHack].rHandle.xy), Surface.fUv0); output.diffuseColor = pigment.rgb;");
        var asset = Parse(Frame(), shader).Value!;
        var graph = Assert.IsType<MaterialSurfaceGraph>(asset.SurfaceGraph);
        Assert.NotNull(graph.BaseColor);
        Assert.Contains(0, graph.TextureSlots);
        Assert.DoesNotContain("MaterialBaseColorUnsupported", graph.Diagnostics);
    }

    [Fact]
    public void TransparencyProducerSuppliesSharedSampleAndQualifiedAlpha()
    {
        var shader = SurfaceSource(body: "output.diffuseColor = sharedSample.rgb;",
            mainConsumer: "if (coverage.transparency < coverage.alphaTestValue) discard;");
        shader = shader.Replace("S_MATERIAL_ATTRIBUTES ComputeHypershade", "vec4 sharedSample;\nS_MATERIAL_ATTRIBUTES ComputeTransparencyHypershade(S_SURFACE Surface, S_MATERIAL Material) { S_MATERIAL_ATTRIBUTES coverage; sharedSample = texture(sampler2D(g_rb2DTextures[Material.s0 + glslHack].rHandle.xy), Surface.fUv0); coverage.transparency = sharedSample.a; coverage.alphaTestValue = 0.25; return coverage; }\nS_MATERIAL_ATTRIBUTES ComputeHypershade", StringComparison.Ordinal)
            .Replace("S_MATERIAL_ATTRIBUTES visible = ComputeHypershade", "S_MATERIAL_ATTRIBUTES coverage = ComputeTransparencyHypershade(surface, Material);\nS_MATERIAL_ATTRIBUTES visible = ComputeHypershade", StringComparison.Ordinal);
        var graph = Assert.IsType<MaterialSurfaceGraph>(Parse(Frame(), shader).Value!.SurfaceGraph);
        Assert.NotNull(graph.BaseColor);
        Assert.NotNull(graph.Opacity);
        Assert.NotNull(graph.AlphaCutoff);
        Assert.Contains(0, graph.TextureSlots);
    }

    [Fact]
    public void NormalSphereCoordinatesRemainDependentOnTheViewNormal()
    {
        var shader = SurfaceSource(body: "vec3 direction = Surface.fVsNormal; vec2 sphereUv = direction.xy * vec2(0.5) + 0.5; vec4 sampled = texture(sampler2D(g_rb2DTextures[Material.s0 + glslHack].rHandle.xy), sphereUv); output.diffuseColor = sampled.rgb;");
        var graph = Assert.IsType<MaterialSurfaceGraph>(Parse(Frame(), shader).Value!.SurfaceGraph);
        Assert.NotNull(graph.BaseColor);
        Assert.Contains(MaterialInput.ViewNormal, graph.RequiredInputs);
        Assert.Contains(0, graph.TextureSlots);
    }

    [Fact]
    public void VectorComparisonBlendRetainsItsSourceSampleAndStrictThreshold()
    {
        var shader = SurfaceSource(body: "vec4 pigment = texture(sampler2D(g_rb2DTextures[Material.s0 + glslHack].rHandle.xy), Surface.fUv0); vec3 low = max(pigment.rgb * 2.0 - 1.0, 0.0); vec3 high = min(pigment.rgb * 2.0, 1.0); vec3 choose = vec3(lessThan(pigment.rgb, vec3(0.5))); output.diffuseColor = mix(high, low, choose);");
        var graph = Assert.IsType<MaterialSurfaceGraph>(Parse(Frame(), shader).Value!.SurfaceGraph);
        Assert.NotNull(graph.BaseColor);
        Assert.Contains(0, graph.TextureSlots);
        Assert.DoesNotContain("MaterialBaseColorUnsupported", graph.Diagnostics);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("opacity = 1.0;", false)]
    [InlineData("opacity++;", false)]
    [InlineData("float opacity = 1.0;", false)]
    [InlineData("AdjustOpacity(opacity);", false)]
    public void ZeroAlphaCoverageBranchesPreserveOpacityWithoutInventingDiscard(string mutation, bool supported)
    {
        var shader = SurfaceSource(body: "output.diffuseColor = vec3(0.3);",
            mainConsumer: "if (coverage.transparency < coverage.alphaTestValue) { float empty = 0.0; WritePixel(vec3(0.0), empty); } else { float opacity = coverage.transparency; " + mutation + " WritePixel(visible.diffuseColor, opacity); }",
            outputDeclaration: "out vec4 pixelColor;");
        shader = shader.Replace("S_MATERIAL_ATTRIBUTES ComputeHypershade",
                "S_MATERIAL_ATTRIBUTES ComputeTransparencyHypershade(S_SURFACE Surface, S_MATERIAL Material) { S_MATERIAL_ATTRIBUTES output; output.transparency = texture(sampler2D(g_rb2DTextures[Material.s0 + glslHack].rHandle.xy), Surface.fUv1).a; output.alphaTestValue = 0.2; return output; }\nS_MATERIAL_ATTRIBUTES ComputeHypershade", StringComparison.Ordinal)
            .Replace("void main()", "void WritePixel(vec3 color, float alpha) { pixelColor.rgb = color; pixelColor.a = saturate(alpha); }\nvoid main()", StringComparison.Ordinal)
            .Replace("S_MATERIAL_ATTRIBUTES visible = ComputeHypershade", "S_MATERIAL_ATTRIBUTES coverage = ComputeTransparencyHypershade(surface, Material);\nS_MATERIAL_ATTRIBUTES visible = ComputeHypershade", StringComparison.Ordinal);
        var graph = Assert.IsType<MaterialSurfaceGraph>(Parse(Frame(2), shader).Value!.SurfaceGraph);
        Assert.Null(graph.AlphaCutoff);
        Assert.Equal(supported, graph.Opacity is not null);
        if (supported)
        {
            Assert.Equal(MaterialExpressionOperation.Multiply, graph.Opacity!.Operation);
            Assert.Equal(MaterialExpressionOperation.Step, graph.Opacity.Arguments[1].Operation);
            Assert.Contains(MaterialInput.Uv1, graph.RequiredInputs);
        }
    }

    [Fact]
    public void PixelCoverageDoesNotMergeTheDepthPassAlphaTest()
    {
        var colorPass = SurfaceSource("output.baseColor = vec3(0.2); output.transparency = 0.6;",
                mainConsumer: "WritePixel(visible.baseColor, visible.transparency);", outputDeclaration: "out vec4 pixelColor;")
            .Replace("void main()", "void WritePixel(vec3 color, float coverage) { pixelColor = vec4(color, coverage); }\nvoid main()", StringComparison.Ordinal);
        var depthPass = SurfaceSource("output.baseColor = vec3(0.2); output.transparency = 0.1; output.alphaTestValue = 0.4;");
        var graph = Assert.IsType<MaterialSurfaceGraph>(Parse(Frame(2), colorPass, depthPass).Value!.SurfaceGraph);
        Assert.Equal(.6f, Assert.Single(graph.Opacity!.Constants!));
        Assert.Null(graph.AlphaCutoff);
        Assert.DoesNotContain("MaterialOpacitySourcesDisagree", graph.Diagnostics);
    }

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
    public void UnusedTransparencyInDeferredAndDebugRgbOutputsDoesNotBecomeOpacity()
    {
        var primary = SurfaceSource(
            "output.baseColor = vec3(0.2, 0.3, 0.4); output.transparency = 0.0; output.alphaTestValue = 0.05;",
            mainConsumer: "ExportPixel(vec3(visible.baseColor), GBuffer);",
            outputDeclaration: "layout(location=0) out vec4 outColor;\nlayout(location=1) out vec4 outGbuffer;")
            .Replace("void main()", "void ExportPixel(vec3 lighting, vec3 gbuffer) { outColor = vec4(lighting, 1.0); outGbuffer = vec4(gbuffer, 0.0); }\nvoid main()", StringComparison.Ordinal);
        var debug = """
            #version 450
            S_MATERIAL_ATTRIBUTES ComputeHypershade(S_SURFACE Surface, S_MATERIAL Material) {
                S_MATERIAL_ATTRIBUTES output;
                output.baseColor = vec3(0.2, 0.3, 0.4);
                output.transparency = 0.0;
                output.alphaTestValue = 0.05;
                return output;
            }
            void main() {
                S_MATERIAL_ATTRIBUTES debugValues = ComputeHypershade(Surface, Material);
                debugExportPixel(vec3(debugValues.transparency));
            }
            """;

        var result = Parse(Frame(2), primary, debug);
        var graph = Assert.IsType<MaterialSurfaceGraph>(result.Value!.SurfaceGraph);

        Assert.NotNull(graph.BaseColor);
        Assert.Null(graph.Opacity);
        Assert.Null(graph.AlphaCutoff);
        Assert.Contains("MaterialOpacityUnsupported", graph.Diagnostics);
    }

    [Fact]
    public void HelperForwardedPixelAlphaRetainsSecondaryUvCoverage()
    {
        var source = SurfaceSource("vec4 mask = texture(sampler2D(g_rb2DTextures[Material.s1 + glslHack].rHandle.xy), Surface.fUv2); output.baseColor = vec3(0.0, 0.0, 1.0); output.transparency = mask.r * mask.g * 0.15;",
            mainConsumer: "WritePixel(visible.baseColor, visible.transparency);", outputDeclaration: "out vec4 pixelColor;")
            .Replace("void main()", "void WritePixel(vec3 color, float coverage) { pixelColor.rgb = color; pixelColor.a = clamp(coverage, 0.0, 1.0); }\nvoid main()", StringComparison.Ordinal);
        var graph = Parse(Frame(2), source).Value!.SurfaceGraph!;
        Assert.NotNull(graph.Opacity);
        Assert.Null(graph.AlphaCutoff);
        Assert.Contains(MaterialInput.Uv2, graph.RequiredInputs);
        Assert.Equal([1], graph.TextureSlots);
    }

    [Theory]
    [InlineData("pixelColor.a = 1.0; pixelColor.rgb = vec3(coverage);", "WritePixel(visible.baseColor, visible.transparency);")]
    [InlineData("if (coverage > 0.5) pixelColor.a = coverage;", "WritePixel(visible.baseColor, visible.transparency);")]
    [InlineData("coverage = 1.0; pixelColor.a = coverage;", "WritePixel(visible.baseColor, visible.transparency);")]
    [InlineData("++coverage; pixelColor.a = coverage;", "WritePixel(visible.baseColor, visible.transparency);")]
    [InlineData("coverage++; pixelColor.a = coverage;", "WritePixel(visible.baseColor, visible.transparency);")]
    [InlineData("--coverage; pixelColor.a = coverage;", "WritePixel(visible.baseColor, visible.transparency);")]
    [InlineData("pixelColor.a = coverage;", "if (Surface.fUv0.x > 0.5) WritePixel(visible.baseColor, visible.transparency);")]
    [InlineData("pixelColor.a = coverage;", "WritePixel(visible.baseColor, visible.transparency); pixelColor.a = 1.0;")]
    [InlineData("pixelColor.a = coverage;", "WritePixel(visible.baseColor, visible.transparency); pixelColor.w = 1.0;")]
    [InlineData("pixelColor.a = coverage;", "visible.transparency++; WritePixel(visible.baseColor, visible.transparency);")]
    [InlineData("pixelColor.a = coverage;", "WritePixel(visible.baseColor, visible.transparency); pixelColor.a++;")]
    [InlineData("pixelColor.a = coverage;", "WritePixel(visible.baseColor, visible.transparency); pixelColor += vec4(1.0);")]
    [InlineData("pixelColor.a = coverage;", "WritePixel(visible.baseColor, visible.transparency); WritePixel(visible.baseColor, 1.0);")]
    [InlineData("pixelColor.a = coverage;", "UnresolvedPixel(visible.baseColor, visible.transparency);")]
    public void UnsafeHelperCoverageDoesNotBecomePixelAlpha(string body, string consumer)
    {
        var source = SurfaceSource(mainConsumer: consumer, outputDeclaration: "out vec4 pixelColor;")
            .Replace("void main()", "void WritePixel(vec3 color, float coverage) { " + body + " }\nvoid main()", StringComparison.Ordinal);
        Assert.Null(Parse(Frame(2), source).Value!.SurfaceGraph!.Opacity);
    }

    [Fact]
    public void DirectTypedVec4AlphaOutputKeepsOpacityWithoutInventingCutoff()
    {
        var source = SurfaceSource(mainConsumer:
                "pixelColor = vec4(visible.baseColor, visible.transparency);",
            outputDeclaration: "out vec4 pixelColor;");

        var graph = Assert.IsType<MaterialSurfaceGraph>(Parse(Frame(2), source).Value!.SurfaceGraph);

        Assert.NotNull(graph.BaseColor);
        Assert.NotNull(graph.Opacity);
        Assert.Null(graph.AlphaCutoff);
    }

    [Fact]
    public void AlphaWriteDoesNotBorrowTransparencyFromALaterRgbStatement()
    {
        var source = SurfaceSource(
            mainConsumer: "pixelColor.a = 1.0; debugExportPixel(vec3(visible.transparency));",
            outputDeclaration: "out vec4 pixelColor;");

        var graph = Assert.IsType<MaterialSurfaceGraph>(Parse(Frame(2), source).Value!.SurfaceGraph);

        Assert.Null(graph.Opacity);
        Assert.Contains("MaterialOpacityUnsupported", graph.Diagnostics);
    }

    [Fact]
    public void LaterAlphaOverwriteDoesNotRetainAnEarlierTransparencyWrite()
    {
        var source = SurfaceSource(
            mainConsumer: "pixelColor.a = visible.transparency; pixelColor.a = 1.0;",
            outputDeclaration: "out vec4 pixelColor;");

        var graph = Assert.IsType<MaterialSurfaceGraph>(Parse(Frame(2), source).Value!.SurfaceGraph);

        Assert.Null(graph.Opacity);
        Assert.Contains("MaterialOpacityUnsupported", graph.Diagnostics);
    }

    [Fact]
    public void ConditionalPixelAlphaWriteIsNotAdmittedAsOpacity()
    {
        var source = SurfaceSource(
            mainConsumer: "if (Surface.fUv0.x > 0.5) { pixelColor = vec4(visible.baseColor, visible.transparency); }",
            outputDeclaration: "out vec4 pixelColor;");

        var graph = Assert.IsType<MaterialSurfaceGraph>(Parse(Frame(2), source).Value!.SurfaceGraph);

        Assert.Null(graph.Opacity);
        Assert.Contains("MaterialOpacityUnsupported", graph.Diagnostics);
    }

    [Fact]
    public void MutatedHypershadeResultCannotQualifyPixelAlpha()
    {
        var source = SurfaceSource(
            mainConsumer: "visible.baseColor = vec3(0.7); pixelColor = vec4(visible.baseColor, visible.transparency);",
            outputDeclaration: "out vec4 pixelColor;");

        var graph = Assert.IsType<MaterialSurfaceGraph>(Parse(Frame(2), source).Value!.SurfaceGraph);

        Assert.Null(graph.Opacity);
        Assert.Contains("MaterialOpacityUnsupported", graph.Diagnostics);
    }

    [Fact]
    public void ReassignedHypershadeResultCannotQualifyCutoutDiscard()
    {
        var source = SurfaceSource(
            mainConsumer: "visible = AdjustSurface(visible); if (visible.transparency < visible.alphaTestValue) discard;");

        var graph = Assert.IsType<MaterialSurfaceGraph>(Parse(Frame(2), source).Value!.SurfaceGraph);

        Assert.Null(graph.Opacity);
        Assert.Null(graph.AlphaCutoff);
        Assert.Contains("MaterialOpacityUnsupported", graph.Diagnostics);
        Assert.Contains("MaterialAlphaCutoffUnsupported", graph.Diagnostics);
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
    public void ProjectionDepthLoweringRequiresConfiguredDistanceAndPreservesSourceMultiplier()
    {
        var source = ProjectionDepthSource();
        var frame = Frame(2);
        var scan = Scan([source], Identity);
        var graph = new NativeMaterialReader { ProjectionDepthNormalizationDistance = 2500 }
            .Parse(frame, Revision, scan).Value!.SurfaceGraph!;

        Assert.NotNull(graph.BaseColor);
        Assert.Contains(MaterialInput.SceneViewDepth, graph.RequiredInputs);
        Assert.Contains(graph.BaseColor!.Arguments, argument =>
            argument.Operation == MaterialExpressionOperation.Multiply &&
            argument.Arguments[0].Constants is [10000f] &&
            argument.Arguments[1].Operation == MaterialExpressionOperation.Divide &&
            argument.Arguments[1].Arguments[0].Input == MaterialInput.SceneViewDepth &&
            argument.Arguments[1].Arguments[1].Constants is [2500f]);
        Assert.Empty(graph.Diagnostics);

        var unconfigured = new NativeMaterialReader().Parse(frame, Revision, scan).Value!.SurfaceGraph!;
        Assert.Null(unconfigured.BaseColor);
        Assert.Contains("MaterialBaseColorUnsupported", unconfigured.Diagnostics);
        Assert.DoesNotContain(MaterialInput.SceneViewDepth, unconfigured.RequiredInputs);
    }

    [Fact]
    public void ProjectionDepthRecognitionUsesHelperBodiesRatherThanHelperNames()
    {
        var helpers = """
            struct BINDLESS_TEXTURE_AND_SAMPLER { uvec2 rHandle; };
            layout(binding = 17) buffer PassDepthArray { BINDLESS_TEXTURE_AND_SAMPLER g_rb2DPassTextures[]; };
            float decodeIdentity(float value) { return value; }
            float readPassDepth(const sampler2D source, vec2 uv) { return decodeIdentity(texture(source, uv).r); }
            float readPassDepth(const BINDLESS_TEXTURE_AND_SAMPLER source, vec2 uv) { return decodeIdentity(texture(sampler2D(source.rHandle.xy), uv).r); }
            float normalizeProjectionDepth(float raw, vec3 setup) { float result = setup.y / (raw + setup.x); return result; }
            """;
        var body = ProjectionDepthBody("normalizeProjectionDepth", "readPassDepth", "sampled", "coordinates");
        var source = SurfaceSource(body).Replace("#version 450", "#version 450\n" + helpers, StringComparison.Ordinal);
        var graph = new NativeMaterialReader { ProjectionDepthNormalizationDistance = 2500 }
            .Parse(Frame(2), Revision, Scan([source], Identity)).Value!.SurfaceGraph!;

        Assert.Contains(MaterialInput.SceneViewDepth, graph.RequiredInputs);
        Assert.Contains(graph.BaseColor!.Arguments, argument => argument.Operation == MaterialExpressionOperation.Multiply &&
            argument.Arguments[1].Arguments[1].Constants is [2500f]);
    }

    [Theory]
    [InlineData("helper-body")]
    [InlineData("coordinates")]
    [InlineData("texture-slot")]
    [InlineData("bindless-overload")]
    [InlineData("sample-before-coordinates")]
    [InlineData("linearize-before-sample")]
    public void ProjectionDepthRecognitionRejectsChangedSourceContracts(string mutation)
    {
        var source = ProjectionDepthSource();
        source = mutation switch
        {
            "helper-body" => source.Replace("depth + setup.x", "depth - setup.x", StringComparison.Ordinal),
            "coordinates" => source.Replace("Surface.fFragCoord.xy", "Surface.fFragCoord.yx", StringComparison.Ordinal),
            "texture-slot" => source.Replace("g_rb2DPassTextures[0 + glslHack]", "g_rb2DPassTextures[1 + glslHack]", StringComparison.Ordinal),
            "bindless-overload" => source.Replace("texture(sampler2D(source.rHandle.xy), uv).r", "texture(sampler2D(source.rHandle.xy), uv).g", StringComparison.Ordinal),
            "sample-before-coordinates" => source.Replace("vec2 depthUv = (Surface.fFragCoord.xy / g_P._EyeData._vFrameBufferViewportSize.xy); float sampledDepth = DecodeDepthBufferStereo(g_rb2DPassTextures[0 + glslHack], depthUv);",
                "float sampledDepth = DecodeDepthBufferStereo(g_rb2DPassTextures[0 + glslHack], depthUv); vec2 depthUv = (Surface.fFragCoord.xy / g_P._EyeData._vFrameBufferViewportSize.xy);", StringComparison.Ordinal),
            "linearize-before-sample" => source.Replace("float sampledDepth = DecodeDepthBufferStereo(g_rb2DPassTextures[0 + glslHack], depthUv); ", "", StringComparison.Ordinal)
                .Replace("output.transparency = 1.0;", "float sampledDepth = DecodeDepthBufferStereo(g_rb2DPassTextures[0 + glslHack], depthUv); output.transparency = 1.0;", StringComparison.Ordinal),
            _ => source
        };
        var graph = new NativeMaterialReader { ProjectionDepthNormalizationDistance = 2500 }
            .Parse(Frame(2), Revision, Scan([source], Identity)).Value!.SurfaceGraph!;

        Assert.Null(graph.BaseColor);
        Assert.Contains("MaterialBaseColorUnsupported", graph.Diagnostics);
        Assert.DoesNotContain(MaterialInput.SceneViewDepth, graph.RequiredInputs);
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

    private static string SurfaceSource(string? body = null, int constants = 0,
        string? mainConsumer = null, string? outputDeclaration = null)
    {
        body ??= """
            vec4 pigment = texture(sampler2D(g_rb2DTextures[Material.s1 + glslHack].rHandle.xy), Surface.fUv0 * vec2(0.2, -0.4));
            output.baseColor = mix(vec3(0.2, 0.3, 0.4), pigment.rgb * vec3(0.7, 0.8, 0.9) + vec3(0.01), 0.25);
            output.transparency = clamp(pow(pigment.a, 2.0), 0.0, 1.0);
            output.alphaTestValue = 0.42;
            """;
        var function = "S_MATERIAL_ATTRIBUTES ComputeHypershade(S_SURFACE Surface, S_MATERIAL Material) { S_MATERIAL_ATTRIBUTES output; " + body + " return output; }\n";
        var source = Source(constants: constants);
        if (outputDeclaration is not null)
            source = source.Replace("#version 450", "#version 450\n" + outputDeclaration, StringComparison.Ordinal);
        var pixelConsumer = mainConsumer ?? "if (visible.transparency < visible.alphaTestValue) discard;";
        return source.Replace("void main()", function + "void main()", StringComparison.Ordinal)
            .Replace("surface = ComputeSurfaceFromNormalMap(surface);", "surface = ComputeSurfaceFromNormalMap(surface);\nS_MATERIAL_ATTRIBUTES visible = ComputeHypershade(surface, Material);\n" + pixelConsumer, StringComparison.Ordinal);
    }

    private static string ProjectionDepthSource()
    {
        var helpers = """
            struct BINDLESS_TEXTURE_AND_SAMPLER { uvec2 rHandle; };
            layout(binding = 17) buffer PassDepthArray { BINDLESS_TEXTURE_AND_SAMPLER g_rb2DPassTextures[]; };
            float DecodeDepth(float depth) { return depth; }
            float DecodeDepthBufferStereo(const sampler2D source, vec2 uv) { return DecodeDepth(texture(source, uv).r); }
            float DecodeDepthBufferStereo(const BINDLESS_TEXTURE_AND_SAMPLER source, vec2 uv) { return DecodeDepth(texture(sampler2D(source.rHandle.xy), uv).r); }
            float LinearizeDepth(float depth, vec3 setup) { float vz = setup.y / (depth + setup.x); return vz; }
            """;
        return SurfaceSource(ProjectionDepthBody("LinearizeDepth", "DecodeDepthBufferStereo", "sampledDepth", "depthUv"))
            .Replace("#version 450", "#version 450\n" + helpers, StringComparison.Ordinal);
    }

    private static string ProjectionDepthBody(string linearizer, string decoder, string depthVariable, string uvVariable) =>
        $"vec2 {uvVariable} = (Surface.fFragCoord.xy / g_P._EyeData._vFrameBufferViewportSize.xy); " +
        $"float {depthVariable} = {decoder}(g_rb2DPassTextures[0 + glslHack], {uvVariable}); " +
        $"output.baseColor = vec3(10000.0 * {linearizer}({depthVariable}, g_P._vProjectionSetup.xyz), 0.2, 0.3); " +
        "output.transparency = 1.0; output.alphaTestValue = 0.05;";

    private static DecodeResult<MaterialAsset> Parse(NativeObjectFrame frame, params string[] sources) =>
        new NativeMaterialReader().Parse(frame, Revision, Scan(sources, Identity));

    private static NativeObjectFrame Frame(int slots = 1, int constants = 0, byte[]? fixedFields = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(3u); writer.Write("COM_CONT"u8); writer.Write(6u); writer.Write(0u);
        writer.Write("LOADCONT"u8); writer.Write(2u); writer.Write("SHADCUST"u8); writer.Write(91u);
        writer.Write(fixedFields ?? new byte[35]); writer.Write((uint)slots);
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
