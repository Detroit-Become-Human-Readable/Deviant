using System.Buffers.Binary;
using System.Text;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Shaders;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class NativeMaterialProgramBudgetTests
{
    [Fact]
    public void PreflightEstimateReservesArrayMetadataBeforeParserAllocations()
    {
        const int decodedSourceBytes = 64 * 1024;
        var fixture = BuildArrayFrame(65_536);
        var reader = new NativeShaderProgramReader();
        Assert.True(reader.TryEstimateWorkingBytes(fixture.Frame, fixture.Slots,
            out var programBytes, out var diagnostic), diagnostic);
        Assert.Equal(4096L + 320 + 2 * (7 + 7) + 65_536L * sizeof(uint), programBytes);
        Assert.InRange(programBytes, 256L * 1024, 512L * 1024);

        var glslScratchBytes = decodedSourceBytes * 12L + 1024 * 1024;
        var tightMaximum = glslScratchBytes + programBytes - 1;
        var tightBudget = new AssetMemoryBudget(tightMaximum);
        var glslScratch = tightBudget.Reserve(glslScratchBytes, "material analysis");
        var parserReached = false;
        try
        {
            Assert.Throws<ArchiveException>(() => ParseWithProgramReservation(
                tightBudget, programBytes, reader, fixture, () => parserReached = true));
            Assert.False(parserReached);
            Assert.Equal(glslScratchBytes, tightBudget.UsedBytes);
            Assert.Equal(tightMaximum, tightBudget.MaximumBytes);
        }
        finally { glslScratch.Dispose(); }
        Assert.Equal(0, tightBudget.UsedBytes);

        var fittingBudget = new AssetMemoryBudget(glslScratchBytes + programBytes);
        using (fittingBudget.Reserve(glslScratchBytes, "material analysis"))
        using (fittingBudget.Reserve(programBytes, "material program records"))
        {
            var parsed = reader.ReadStoredLayout(fixture.Frame, fixture.Slots);
            Assert.NotNull(parsed.Value);
            Assert.Equal(65_536u, Assert.Single(parsed.Value!.ArrayConstants).ElementCount);
            Assert.Equal(glslScratchBytes + programBytes, fittingBudget.UsedBytes);
            Assert.Equal(glslScratchBytes + programBytes, fittingBudget.MaximumBytes);
        }
        Assert.Equal(0, fittingBudget.UsedBytes);
    }

    private static void ParseWithProgramReservation(AssetMemoryBudget budget, long bytes,
        NativeShaderProgramReader reader, Fixture fixture, Action parserReached)
    {
        using var programScratch = budget.Reserve(bytes, "material program records");
        parserReached();
        _ = reader.ReadStoredLayout(fixture.Frame, fixture.Slots);
    }

    private static Fixture BuildArrayFrame(int elementCount)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0u); writer.Write(0u); // Empty primary and secondary program tables.
            writer.Write(0u); writer.Write(1u); // No scalar rows and one array row.
            WriteText(writer, "array-a"); WriteText(writer, "field-b");
            writer.Write(0x7FC01234u); writer.Write(0xAABBCCDDu); writer.Write((uint)elementCount);
            for (var value = 0; value < elementCount; value++) writer.Write((uint)value);
        }
        var bytes = stream.ToArray();
        var container = new ResourceIdentity("generated-budget-index", 1);
        var identity = container with { MemberPath = "dc-object/0/2133/1" };
        var revision = new SourceRevision(container.IndexContentId, "generated-budget-source");
        var suffix = new DecodedExtent(container, 104, bytes.Length);
        var header = new byte[52];
        var frame = new NativeObjectFrame(identity, 0, 2133, 1, new(container, 0, 8),
            new(container, 52, 52), suffix, header, bytes, 1, 1, 0, null);
        var slots = new TextureShaderSlotsDocument(identity, revision, new(container, 104, 12),
            new(container, 116, 4), new byte[35], [], suffix, bytes, Array.Empty<SourceExtent>());
        return new(frame, slots);
    }

    private static void WriteText(BinaryWriter writer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write((uint)bytes.Length); writer.Write(bytes);
    }

    private sealed record Fixture(NativeObjectFrame Frame, TextureShaderSlotsDocument Slots);
}
