using System.Buffers.Binary;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class NodeFixedReadWindowTests
{
    [Fact]
    public void KnownConsumptionInventoryUsesExplicitOriginAndPreservesRawReadSpans()
    {
        var owner = new ResourceIdentity("index-a", 2); var revision = new SourceRevision("index-a", "revision-a");
        var prefix = new byte[200]; BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(12), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(12 + 144), 40); BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(12 + 148), 7);
        var directoryBytes = new byte[32]; "DC_INFO "u8.CopyTo(directoryBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(directoryBytes.AsSpan(8), 3); BinaryPrimitives.WriteUInt32LittleEndian(directoryBytes.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(directoryBytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(directoryBytes.AsSpan(24), 40); BinaryPrimitives.WriteUInt32LittleEndian(directoryBytes.AsSpan(28), 7);
        SourceExtent extent = new("sample.dat", 100, 50);
        var directory = new DcInfoDirectoryReader().Inspect(directoryBytes, owner, revision, [extent]);
        var result = new NodeFixedReadWindowInspector().Inspect(prefix, 12, owner, revision, [extent], directory, decodedOffset: 1000);
        Assert.Equal(new DecodedExtent(owner, 1012, 180), result.Value!.CandidateExtent);
        Assert.Equal(NodeFixedReadWindowInspector.ReadLengths, result.Value.Reads.Select(r => r.Bytes.Length));
        Assert.Equal(8u, result.Value.Reads[0].Word0LittleEndian);
        Assert.Equal(new DecodedExtent(owner, 1028, 64), result.Value.Reads[4].Extent);
        Assert.Equal(40u, result.Value.Reads[6].Word0LittleEndian); Assert.Equal(7u, result.Value.Reads[6].Word1LittleEndian);
        Assert.Equal(new long[] { 0 }, result.Value.Reads[6].DirectoryPairCandidates);
        Assert.Equal("Unknown", result.UnknownFields["recordBoundary"]);
        Assert.Equal("Caller-supplied candidate", result.UnknownFields["cursorOrigin"]);
        Assert.Empty(result.UnresolvedReferences);
        Assert.Equal(172, result.Value.Reads[10].RelativeOffset);
    }

    [Fact]
    public void RejectsTruncationInvalidOriginsForeignDirectoryAndCancellation()
    {
        var owner = new ResourceIdentity("index-a", 0); var revision = new SourceRevision("index-a", "revision-a");
        var reader = new NodeFixedReadWindowInspector(); var prefix = new byte[180];
        Assert.Null(reader.Inspect(prefix, 1, owner, revision, []).Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Inspect(prefix, -1, owner, revision, []));
        Assert.Throws<OperationCanceledException>(() => reader.Inspect(prefix, 0, owner, revision, [], token: new(true)));
        var foreign = new DecodeResult<DcInfoDirectoryInspection>(null, new("index-a", 1), revision, [], [], [], [], new Dictionary<string, string>());
        Assert.Throws<ArgumentException>(() => reader.Inspect(prefix, 0, owner, revision, [], foreign));
    }
}
