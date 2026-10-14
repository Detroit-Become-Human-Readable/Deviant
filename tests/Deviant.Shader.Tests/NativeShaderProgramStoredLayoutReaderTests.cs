using System.Buffers.Binary;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Containers;
using Deviant.Formats.Shaders;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Shader.Tests;

public sealed class NativeShaderProgramStoredLayoutReaderTests
{
    [Fact]
    public void ReadsOrderedRawScalarAndArrayRecordsWithExactExtents()
    {
        var fixture = Build(1, 2, writer =>
        {
            WriteScalar(writer, "scalar-a", "field-b", 0x3F800000, 0x10203040);
            WriteArray(writer, "first-a", "first-b", 0x80000000, 0xA1B2C3D4, [0x01020304, 0xFEDCBA98]);
            WriteArray(writer, "second-a", "second-b", 0x7F800000, 0x55667788, []);
        }, [0xAA, 0xBB]);

        var result = new NativeShaderProgramReader().ReadStoredLayout(fixture.Frame, fixture.Slots);

        Assert.NotNull(result.Value);
        var layout = result.Value!;
        Assert.Equal(1u, layout.ScalarConstantCount);
        Assert.Equal(2u, layout.ArrayConstantCount);
        Assert.Equal("scalar-a", layout.ScalarConstants[0].Name);
        Assert.Equal("field-b", layout.ScalarConstants[0].Expression);
        Assert.Equal(0x3F800000u, layout.ScalarConstants[0].Float32Bits);
        Assert.Equal("first-a", layout.ArrayConstants[0].TextField1);
        Assert.Equal("first-b", layout.ArrayConstants[0].TextField2);
        Assert.Equal(0x80000000u, layout.ArrayConstants[0].Float32Bits);
        Assert.Equal(0xA1B2C3D4u, layout.ArrayConstants[0].StoredWord);
        Assert.Equal(2u, layout.ArrayConstants[0].ElementCount);
        Assert.Equal(new uint[] { 0x01020304u, 0xFEDCBA98u }, layout.ArrayConstants[0].ElementWords);
        Assert.Equal(0x7F800000u, layout.ArrayConstants[1].Float32Bits);
        Assert.Empty(layout.ArrayConstants[1].ElementWords);
        Assert.Equal(layout.ArrayConstants[0].Extent.Offset,
            layout.ArrayConstants[0].TextField1Extent.Offset);
        Assert.Equal(layout.ArrayConstants[0].Extent.Offset + layout.ArrayConstants[0].Extent.Length,
            layout.ArrayConstants[0].ElementWordsExtent.Offset + layout.ArrayConstants[0].ElementWordsExtent.Length);
        Assert.Equal(fixture.Slots.UnparsedSuffixExtent.Offset + fixture.Bytes.Length - 2,
            layout.ConstantsEndpointOffset);
        Assert.Equal(2L, layout.TrailingExtent!.Length);
        Assert.InRange(layout.AccountedMetadataBytes, 1L, NativeShaderProgramReader.MaximumStoredMetadataBytes);

        var consumerLayout = new NativeShaderProgramReader().Parse(fixture.Frame, fixture.Slots);
        Assert.NotNull(consumerLayout.Value);
        Assert.Equal(4, consumerLayout.Value!.AlignedScalarConstantCount);
        Assert.Equal(2u, consumerLayout.Value.ArrayConstantCount);
        Assert.Equal(2, consumerLayout.Value.ArrayConstants.Count);
        Assert.Equal(layout.ConstantsEndpointOffset, consumerLayout.Value.ConstantsEndpointOffset);
    }

    [Fact]
    public void StrictScalarConsumerStillAcceptsScalarOnlyLayout()
    {
        var fixture = Build(1, 0, writer => WriteScalar(writer, "scalar", "field", 0x3F800000, 7));

        var result = new NativeShaderProgramReader().Parse(fixture.Frame, fixture.Slots);

        Assert.NotNull(result.Value);
        Assert.Single(result.Value!.ScalarConstants);
        Assert.Equal(1f, result.Value.ScalarConstants[0].InitialValue);
        Assert.Equal(7u, result.Value.ScalarConstants[0].StoredWord);
    }

    [Fact]
    public void StoredReaderRejectsArrayCountBeforeAllocatingRecords()
    {
        var fixture = Build(0, NativeShaderProgramReader.MaximumArrayConstants + 1u, _ => { });

        var result = new NativeShaderProgramReader().ReadStoredLayout(fixture.Frame, fixture.Slots);

        Assert.Null(result.Value);
        Assert.Contains("MaterialArrayConstantCountLimit", result.Diagnostics);
        Assert.Contains("MaterialArrayConstantCountLimit", new NativeShaderProgramReader()
            .Parse(fixture.Frame, fixture.Slots).Diagnostics);
    }

    [Fact]
    public void StoredReaderRejectsAggregateElementLimitAndTruncatedWords()
    {
        var overLimit = Build(0, 1, writer =>
        {
            WriteText(writer, "a"); WriteText(writer, "b");
            writer.Write(0u); writer.Write(1u);
            writer.Write((uint)NativeShaderProgramReader.MaximumArrayElements + 1u);
        });
        var overLimitResult = new NativeShaderProgramReader().ReadStoredLayout(overLimit.Frame, overLimit.Slots);
        Assert.Null(overLimitResult.Value);
        Assert.Contains("MaterialArrayElementCountLimit", overLimitResult.Diagnostics);

        var truncated = Build(0, 1, writer =>
        {
            WriteText(writer, "a"); WriteText(writer, "b");
            writer.Write(0u); writer.Write(1u);
            writer.Write(2u); writer.Write(0x11223344u);
        });
        var truncatedResult = new NativeShaderProgramReader().ReadStoredLayout(truncated.Frame, truncated.Slots);
        Assert.Null(truncatedResult.Value);
        Assert.Contains("MaterialProgramTruncated", truncatedResult.Diagnostics);
    }

    [Fact]
    public void StoredReaderBoundsAggregateTextAndChecksRevision()
    {
        var largeText = new string('x', 1024 * 1024);
        var manyTexts = Build(0, 5, writer =>
        {
            for (var index = 0; index < 5; index++)
                WriteArray(writer, largeText, largeText, 0u, (uint)index, []);
        });
        var textResult = new NativeShaderProgramReader().ReadStoredLayout(manyTexts.Frame, manyTexts.Slots);
        Assert.Null(textResult.Value);
        Assert.Contains("MaterialStoredTextLimit", textResult.Diagnostics);

        var small = Build(0, 0, _ => { });
        var wrongRevision = small.Slots with
        { Revision = new SourceRevision("other-generated-index", "generated-source") };
        var revisionResult = new NativeShaderProgramReader().ReadStoredLayout(small.Frame, wrongRevision);
        Assert.Null(revisionResult.Value);
        Assert.Contains("MaterialProgramRevisionMismatch", revisionResult.Diagnostics);
    }

    [Fact]
    public void StoredReaderPropagatesCancellation()
    {
        var fixture = Build(0, 0, _ => { });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => new NativeShaderProgramReader()
            .ReadStoredLayout(fixture.Frame, fixture.Slots, cancelled.Token));
    }

    [Fact]
    public void MaterialNormalAndSurfaceGraphsStillUseScalarLayoutWhenArrayRecordsAreUnreferenced()
    {
        var frame = BuildMaterialFrame(2, 1, 1, writer =>
            WriteArray(writer, "array-field-a", "array-field-b", 0x7FC01234, 0xAABBCCDD, [0x11223344]));
        var source = MaterialSurfaceSource(scalarCount: 1);
        var scan = ScanMaterialSource(source);

        var result = new NativeMaterialReader().Parse(frame, MaterialRevision, scan);

        Assert.Single(result.Value!.Bindings);
        Assert.Equal(2, result.Value.References.Count);
        Assert.NotNull(result.Value.SurfaceGraph?.BaseColor);
    }

    [Fact]
    public void ArrayDependentSamplerExpressionDoesNotBecomeAScalarBinding()
    {
        var frame = BuildMaterialFrame(1, 1, 1, writer =>
            WriteArray(writer, "array-field-a", "array-field-b", 0x3F800000, 0x55AA55AA, [7]));
        var source = MaterialNormalSource(scalarCount: 1)
            .Replace("Material.s0 + glslHack", "Material.a0[0] + glslHack", StringComparison.Ordinal);

        var result = new NativeMaterialReader().Parse(frame, MaterialRevision, ScanMaterialSource(source));

        Assert.Empty(result.Value!.Bindings);
        Assert.Single(result.Value.References);
        Assert.Contains("MaterialNormalGraphUnsupported", result.Diagnostics);
    }

    [Fact]
    public void NonAllocatingPreflightMatchesTheWireDerivedMetadataReservation()
    {
        var fixture = Build(1, 2, writer =>
        {
            WriteScalar(writer, "scalar-a", "field-b", 0x3F800000, 0x10203040);
            WriteArray(writer, "first-a", "first-b", 0x80000000, 0xA1B2C3D4, [0x01020304, 0xFEDCBA98]);
            WriteArray(writer, "second-a", "second-b", 0x7F800000, 0x55667788, []);
        });
        var reader = new NativeShaderProgramReader();

        var accepted = reader.TryEstimateWorkingBytes(fixture.Frame, fixture.Slots,
            out var estimate, out var diagnostic);
        var stored = reader.ReadStoredLayout(fixture.Frame, fixture.Slots);

        Assert.True(accepted, diagnostic);
        Assert.NotNull(stored.Value);
        Assert.Equal(4096L + 256 + 2 * 320 + 2 * 45 + 2 * sizeof(uint), estimate);
        Assert.Equal(stored.Value!.AccountedMetadataBytes, estimate);
        Assert.InRange(estimate, 1L, 1024L * 1024);
    }

    [Fact]
    public void PreflightEstimateScalesWithEncodedElementsRatherThanTheMetadataCeiling()
    {
        var elementCount = 65_536;
        var fixture = Build(0, 1, writer => WriteArray(writer, "array-a", "field-b",
            0x7FC01234, 0xAABBCCDD, Enumerable.Range(0, elementCount).Select(value => (uint)value).ToArray()));

        var accepted = new NativeShaderProgramReader().TryEstimateWorkingBytes(fixture.Frame, fixture.Slots,
            out var estimate, out var diagnostic);

        Assert.True(accepted, diagnostic);
        Assert.Equal(4096L + 320 + 2 * (7 + 7) + elementCount * sizeof(uint), estimate);
        Assert.InRange(estimate, 256L * 1024, 512L * 1024);
    }

    [Fact]
    public void PreflightBoundsAndValidatesRecordsBeforeReturningAReservation()
    {
        var reader = new NativeShaderProgramReader();
        var overCount = Build(0, NativeShaderProgramReader.MaximumArrayConstants + 1u, _ => { });
        Assert.False(reader.TryEstimateWorkingBytes(overCount.Frame, overCount.Slots, out _, out var diagnostic));
        Assert.Equal("MaterialArrayConstantCountLimit", diagnostic);

        var truncated = Build(0, 1, writer =>
        {
            WriteText(writer, "a"); WriteText(writer, "b");
            writer.Write(0u); writer.Write(1u); writer.Write(2u); writer.Write(0x11223344u);
        });
        Assert.False(reader.TryEstimateWorkingBytes(truncated.Frame, truncated.Slots, out var truncatedEstimate,
            out diagnostic));
        Assert.Equal("MaterialProgramTruncated", diagnostic);
        Assert.InRange(truncatedEstimate, 1L, NativeShaderProgramReader.MaximumStoredMetadataBytes);

        var invalidText = Build(0, 1, writer =>
        {
            writer.Write(1u); writer.Write((byte)0xFF); WriteText(writer, "b");
            writer.Write(0u); writer.Write(1u); writer.Write(0u);
        });
        Assert.False(reader.TryEstimateWorkingBytes(invalidText.Frame, invalidText.Slots, out _, out diagnostic));
        Assert.Equal("MaterialConstantTextInvalid", diagnostic);
    }

    [Fact]
    public void PreflightMatchesTheStrictParseFiniteScalarProfileAndPropagatesCancellation()
    {
        var fixture = Build(1, 1, writer =>
        {
            WriteScalar(writer, "scalar", "field", 0x7FC01234, 0);
            WriteArray(writer, "array-a", "array-b", 0x7F800000, 7, [1, 2, 3]);
        });
        var reader = new NativeShaderProgramReader();

        Assert.False(reader.TryEstimateWorkingBytes(fixture.Frame, fixture.Slots,
            out var estimate, out var diagnostic));
        Assert.Equal("MaterialConstantValueInvalid", diagnostic);
        Assert.Equal(reader.ReadStoredLayout(fixture.Frame, fixture.Slots).Value!.AccountedMetadataBytes, estimate);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.TryEstimateWorkingBytes(
            fixture.Frame, fixture.Slots, out _, out _, cancelled.Token));
    }

    private static Fixture Build(uint scalarCount, uint arrayCount, Action<BinaryWriter> writeRecords,
        byte[]? trailing = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0u); writer.Write(0u); // Empty primary and secondary program tables.
            writer.Write(scalarCount); writer.Write(arrayCount);
            writeRecords(writer);
            if (trailing is not null) writer.Write(trailing);
        }
        var bytes = stream.ToArray();
        var container = new ResourceIdentity("generated-index", 3);
        var identity = container with { MemberPath = "dc-object/0/2133/7" };
        var suffix = new DecodedExtent(container, 104, bytes.Length);
        var revision = new SourceRevision(container.IndexContentId, "generated-source");
        var frame = new NativeObjectFrame(identity, 0, 2133, 7,
            new(container, 0, 8), new(container, 52, 52), suffix, new byte[52], bytes,
            1, 1, 0, null);
        var slots = new TextureShaderSlotsDocument(identity, revision,
            new(container, 104, 12), new(container, 116, 4), new byte[35], [],
            suffix, bytes, Array.Empty<SourceExtent>());
        return new(frame, slots, bytes);
    }

    private static readonly ResourceIdentity MaterialIdentity = new("generated-material-index", 4, "native-object/7");
    private static readonly SourceRevision MaterialRevision = new(MaterialIdentity.IndexContentId, "generated-material-source");

    private static NativeObjectFrame BuildMaterialFrame(int slotCount, uint scalarCount, uint arrayCount,
        Action<BinaryWriter> writeConstants)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(3u); writer.Write("COM_CONT"u8); writer.Write(6u); writer.Write(0u);
            writer.Write("LOADCONT"u8); writer.Write(2u); writer.Write("SHADCUST"u8); writer.Write(91u);
            writer.Write(new byte[35]); writer.Write((uint)slotCount);
            for (var slot = 0; slot < slotCount; slot++)
            { writer.Write(0u); writer.Write(2137u); writer.Write((uint)(1000 + slot)); writer.Write((byte)0); }
            writer.Write(0u); writer.Write(0u); writer.Write(scalarCount); writer.Write(arrayCount);
            for (var scalar = 0; scalar < scalarCount; scalar++)
            {
                WriteText(writer, $"scalar-{scalar}"); WriteText(writer, $"field-{scalar}");
                writer.Write(0x3F800000u); writer.Write((uint)scalar);
            }
            writeConstants(writer);
        }
        var bytes = stream.ToArray();
        var header = new byte[52]; header[4] = 1; BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(5), 1);
        var extent = new DecodedExtent(MaterialIdentity, 0, bytes.Length);
        return new(MaterialIdentity, 0, 2133, 7, new(MaterialIdentity, 0, 8),
            new(MaterialIdentity, 0, 52), extent, header, bytes, 1, 1, 0, 3);
    }

    private static StoredGlslScan ScanMaterialSource(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source + '\0');
        var physical = new SourceExtent("generated-material-source.bin", 0, bytes.Length);
        var chunk = new SegsChunk(0, 0, 0, 0, 0, bytes.Length, physical,
            new(MaterialIdentity, 0, bytes.Length), bytes);
        var document = new SegsDocument(1, 1, (uint)bytes.Length, (uint)bytes.Length,
            16, 16, bytes.Length, [chunk], [], bytes, null, [], null);
        var decoded = new DecodeResult<SegsDocument>(document, MaterialIdentity, MaterialRevision,
            [physical], [], [], [], new Dictionary<string, string>());
        return StoredGlslReader.Scan(decoded);
    }

    private static string MaterialNormalSource(int scalarCount)
    {
        var fields = string.Join('\n', Enumerable.Range(0, (scalarCount + 3) & ~3).Select(i => $"float c{i};"));
        return $$"""
            #version 450
            struct S_MATERIAL { vec4 errorColor; float depth; float padA; float padB; float padC;
                {{fields}} uint s0; uint s1; uint s2; uint s3; };
            vec3 ComputeNormal(vec3 encoded, vec3 tangent, vec3 binormal, vec3 geometryNormal) {
                vec3 signedNormal = 2.0 * encoded - vec3(1.0);
                mat3 basis = mat3(tangent, binormal, geometryNormal);
                return normalize(basis * signedNormal);
            }
            S_SURFACE ComputeNormalMap(S_SURFACE Surface, S_MATERIAL Material) {
                MaterialForNormalMap attributes;
                { vec4 sampled = texture(sampler2D(g_rb2DTextures[Material.s0 + glslHack].rHandle.xy), Surface.fUv0);
                  vec3 selected; selected.rgb = sampled.rgb; attributes.normalMap = selected; }
                S_SURFACE next = Surface; next.fTsNormal = attributes.normalMap; return next;
            }
            S_SURFACE ComputeSurfaceFromNormalMap(S_SURFACE Surface) {
                Surface.normal = ComputeNormal(Surface.fTsNormal, Surface.tangent, Surface.binormal, Surface.geometryNormal);
                return Surface;
            }
            void main() { S_SURFACE surface = ComputeNormalMap(Surface, Material); surface = ComputeSurfaceFromNormalMap(surface); }
            """;
    }

    private static string MaterialSurfaceSource(int scalarCount)
    {
        var source = MaterialNormalSource(scalarCount);
        var body = """
            vec4 pigment = texture(sampler2D(g_rb2DTextures[Material.s1 + glslHack].rHandle.xy), Surface.fUv0 * vec2(0.2, -0.4));
            output.baseColor = pigment.rgb;
            output.transparency = 1.0;
            output.alphaTestValue = 0.05;
            """;
        var function = "S_MATERIAL_ATTRIBUTES ComputeHypershade(S_SURFACE Surface, S_MATERIAL Material) { S_MATERIAL_ATTRIBUTES output; " + body + " return output; }\n";
        return source.Replace("void main()", function + "void main()", StringComparison.Ordinal)
            .Replace("surface = ComputeSurfaceFromNormalMap(surface);", "surface = ComputeSurfaceFromNormalMap(surface);\nS_MATERIAL_ATTRIBUTES visible = ComputeHypershade(surface, Material);\nif (visible.transparency < visible.alphaTestValue) discard;", StringComparison.Ordinal);
    }

    private static void WriteScalar(BinaryWriter writer, string text1, string text2, uint float32Bits, uint storedWord)
    {
        WriteText(writer, text1); WriteText(writer, text2);
        writer.Write(float32Bits); writer.Write(storedWord);
    }

    private static void WriteArray(BinaryWriter writer, string text1, string text2, uint float32Bits,
        uint storedWord, uint[] elements)
    {
        WriteText(writer, text1); WriteText(writer, text2);
        writer.Write(float32Bits); writer.Write(storedWord); writer.Write((uint)elements.Length);
        foreach (var element in elements) writer.Write(element);
    }

    private static void WriteText(BinaryWriter writer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write((uint)bytes.Length); writer.Write(bytes);
    }

    private sealed record Fixture(NativeObjectFrame Frame, TextureShaderSlotsDocument Slots, byte[] Bytes);
}
