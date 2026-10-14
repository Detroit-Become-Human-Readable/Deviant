using System.Buffers.Binary;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class PhysicsMetadataInspectionReaderTests
{
    [Fact]
    public void ChainsCompleteMetadataStagesAndRetainsDecoderHistory()
    {
        var frame = Frame(CompleteEmbedded());
        var preceding = new DecodeStep("SyntheticFrameReader", "fixture-v1", "generated frame");

        var result = new PhysicsMetadataInspectionReader().Read(frame, TaggedFixture.Revision,
            TaggedFixture.Physical, [preceding]);

        Assert.NotNull(result.Envelope.Value);
        Assert.NotNull(result.TaggedMetadata?.Value);
        Assert.NotNull(result.TypeDeclarations?.Value);
        Assert.NotNull(result.TypeLayouts?.Value);
        Assert.NotNull(result.IndexedMetadata?.Value);
        Assert.Equal(PhysicsIndexedMetadataCoverage.CompleteStructure, result.IndexedMetadata!.Value!.Coverage);

        var stages = new (ResourceIdentity Identity, SourceRevision Revision,
            IReadOnlyList<SourceExtent> Extents, IReadOnlyList<DecodeStep> Steps)[]
        {
            (result.Envelope.Identity, result.Envelope.Revision, result.Envelope.Extents, result.Envelope.Steps),
            (result.TaggedMetadata!.Identity, result.TaggedMetadata.Revision, result.TaggedMetadata.Extents, result.TaggedMetadata.Steps),
            (result.TypeDeclarations!.Identity, result.TypeDeclarations.Revision, result.TypeDeclarations.Extents, result.TypeDeclarations.Steps),
            (result.TypeLayouts!.Identity, result.TypeLayouts.Revision, result.TypeLayouts.Extents, result.TypeLayouts.Steps),
            (result.IndexedMetadata!.Identity, result.IndexedMetadata.Revision, result.IndexedMetadata.Extents, result.IndexedMetadata.Steps)
        };
        var decoders = new[]
        {
            "SyntheticFrameReader",
            nameof(PhysicsEnvelopeReader),
            nameof(PhysicsTaggedMetadataReader),
            nameof(PhysicsTypeDeclarationsReader),
            nameof(PhysicsTypeLayoutsReader),
            nameof(PhysicsIndexedMetadataReader)
        };

        for (var index = 0; index < stages.Length; index++)
        {
            Assert.Equal(frame.Identity, stages[index].Identity);
            Assert.Equal(TaggedFixture.Revision, stages[index].Revision);
            Assert.Equal(TaggedFixture.Physical, stages[index].Extents);
            Assert.Equal(decoders.Take(index + 2), stages[index].Steps.Select(step => step.Decoder));
            Assert.Equal(preceding, stages[index].Steps[0]);
            if (index > 0)
                Assert.Equal(stages[index - 1].Steps, stages[index].Steps.Take(stages[index - 1].Steps.Count));
        }
    }

    [Fact]
    public void InvalidEnvelopeStopsBeforeLaterStages()
    {
        var frame = Frame(CompleteEmbedded());
        var rawPayload = frame.RawPayload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(rawPayload.AsSpan(12), 6);

        var result = new PhysicsMetadataInspectionReader().Read(frame with { RawPayload = rawPayload },
            TaggedFixture.Revision, TaggedFixture.Physical);

        Assert.Null(result.Envelope.Value);
        Assert.Contains("PhysicsEnvelopeVersionUnsupported", result.Envelope.Diagnostics);
        Assert.Null(result.TaggedMetadata);
        Assert.Null(result.TypeDeclarations);
        Assert.Null(result.TypeLayouts);
        Assert.Null(result.IndexedMetadata);
    }

    [Fact]
    public void InvalidDeclarationsRetainEarlierStageResultsAndStopLaterStages()
    {
        var embedded = TaggedFixture.Root(TaggedFixture.Chunk("TYPE", 0, []));

        var result = new PhysicsMetadataInspectionReader().Read(Frame(embedded), TaggedFixture.Revision,
            TaggedFixture.Physical);

        Assert.NotNull(result.Envelope.Value);
        Assert.NotNull(result.TaggedMetadata?.Value);
        Assert.NotNull(result.TypeDeclarations);
        Assert.Null(result.TypeDeclarations!.Value);
        var direct = new PhysicsTypeDeclarationsReader().Read(result.TaggedMetadata!.Value!,
            TaggedFixture.Physical, result.TaggedMetadata.Steps);
        Assert.Equal(direct.Identity, result.TypeDeclarations.Identity);
        Assert.Equal(direct.Revision, result.TypeDeclarations.Revision);
        Assert.Equal(direct.Extents, result.TypeDeclarations.Extents);
        Assert.Equal(direct.Diagnostics, result.TypeDeclarations.Diagnostics);
        Assert.Equal(direct.Steps, result.TypeDeclarations.Steps);
        Assert.Null(result.TypeLayouts);
        Assert.Null(result.IndexedMetadata);
    }

    [Fact]
    public void HonorsCancellationBeforeStartingTheChain()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => new PhysicsMetadataInspectionReader().Read(
            Frame(CompleteEmbedded()), TaggedFixture.Revision, TaggedFixture.Physical, token: stop.Token));
    }

    [Fact]
    public void OptionalStorageStageDoesNotDiscardEarlierIndexedMetadata()
    {
        var result = new PhysicsMetadataInspectionReader().Read(Frame(CompleteEmbedded()),
            TaggedFixture.Revision, TaggedFixture.Physical);

        Assert.NotNull(result.IndexedMetadata);
        Assert.NotNull(result.IndexedMetadata!.Value);
        Assert.NotNull(result.DataStorage);
        Assert.Equal(result.IndexedMetadata.Identity, result.DataStorage!.Identity);
        Assert.Equal(result.IndexedMetadata.Revision, result.DataStorage.Revision);
    }

    private static byte[] CompleteEmbedded()
    {
        var type = TaggedFixture.Chunk("TYPE", 0, Join(
            TaggedFixture.Chunk("TSTR", 1, Encoding.ASCII.GetBytes("Root\0Child\0Leaf\0")),
            TaggedFixture.Chunk("TNA1", 1, [4, 0, 0, 1, 0, 2, 0]),
            TaggedFixture.Chunk("FSTR", 1, Encoding.ASCII.GetBytes("member\0")),
            TaggedFixture.Chunk("TBDY", 1, [1, 0, 0, 2, 0, 0, 3, 0, 0])));
        var rows = Join(Row(0x2a000003, 17, 1), Row(0x1b000001, 12, uint.MaxValue),
            Row(0x20000002, 99, 0), Row(0x0f000005, 64, 1));
        var index = TaggedFixture.Chunk("INDX", 0, Join(
            TaggedFixture.Chunk("ITEM", 1, rows),
            TaggedFixture.Chunk("PTCH", 1, [2, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0])));
        return TaggedFixture.Root(TaggedFixture.Chunk("DATA", 1, new byte[128]), type, index);
    }

    private static NativeObjectFrame Frame(byte[] embedded)
    {
        var payload = new byte[20 + embedded.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 1);
        "PHYSYS__"u8.CopyTo(payload.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), (uint)embedded.Length);
        embedded.CopyTo(payload, 20);
        return new(new(TaggedFixture.Container.IndexContentId, TaggedFixture.Container.Ordinal, "dc-object/0/2201/37"),
            0, 2201, 37, new(TaggedFixture.Container, 24, 8), new(TaggedFixture.Container, 128, 13),
            new(TaggedFixture.Container, 141, payload.Length), new byte[13], payload, 0, 0, 0, 1);
    }

    private static byte[] Row(uint word0, uint word1, uint word2)
    {
        var row = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(row, word0);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(4), word1);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(8), word2);
        return row;
    }

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
}
