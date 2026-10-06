using System.Buffers.Binary;
using System.Text;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class NodeInventoryAndCorrelationTests
{
    private static readonly ResourceIdentity Owner = new("index-a", 0);
    private static readonly SourceRevision Revision = new("index-a", "revision-a");
    private static readonly SourceExtent Original = new("sample.dat", 100, 200);

    [Fact]
    public void ExactTagInsideTextRemainsAnUnassignedCandidate()
    {
        var text = Encoding.ASCII.GetBytes("#version 450\n// NODE    appears as stored text\n");
        var result = new NodeTagInventoryReader().Scan(text, Owner, Revision, [Original]);
        var candidate = Assert.Single(result.Value!.Candidates);
        Assert.Equal(16, candidate.HeaderBytes.Length);
        Assert.Equal("NODE    ", Encoding.ASCII.GetString(candidate.HeaderBytes[..8]));
        Assert.Equal("Unknown", result.UnknownFields["recordExtent"]);
        Assert.Equal("Unknown", result.UnknownFields["callbackCursorOrigin"]);
        Assert.Empty(result.UnresolvedReferences);
        Assert.Equal(Original, result.Extents.Single());
    }

    [Fact]
    public void TruncatedCandidateHeaderIsRetainedWithoutReadingMissingWords()
    {
        var bytes = Encoding.ASCII.GetBytes("abcNODE    xy");
        var result = new NodeTagInventoryReader().Scan(bytes, Owner, Revision, [Original], decodedOffset: 1000);
        var candidate = Assert.Single(result.Value!.Candidates);
        Assert.Equal(new DecodedExtent(Owner, 1003, 10), candidate.HeaderExtent);
        Assert.Null(candidate.WordAt8LittleEndian); Assert.Null(candidate.WordAt12LittleEndian);
        Assert.Empty(candidate.FollowingBytes); Assert.Null(candidate.FollowingExtent);
        Assert.Contains("TruncatedNodeTagHeader", result.Diagnostics);
    }

    [Fact]
    public void PreservesCandidateBytesAndBoundsAdditionalContext()
    {
        var bytes = Tag(5, 8).Concat(new byte[] { 1, 2, 3, 4, 5 }).ToArray();
        var result = new NodeTagInventoryReader().Scan(bytes, Owner, Revision, [Original], limits: new(FollowingContextBytes: 3));
        var candidate = Assert.Single(result.Value!.Candidates);
        Assert.Equal(5u, candidate.WordAt8LittleEndian); Assert.Equal(8u, candidate.WordAt12LittleEndian);
        Assert.Equal(Tag(5, 8), candidate.HeaderBytes); Assert.Equal(new byte[] { 1, 2, 3 }, candidate.FollowingBytes);
        Assert.Equal(new DecodedExtent(Owner, 16, 3), candidate.FollowingExtent);
    }

    [Fact]
    public void EnforcesScanMatchBudgetsAndCancellation()
    {
        var reader = new NodeTagInventoryReader(); var bytes = Tag(5, 8).Concat(Tag(99, 123)).ToArray();
        var result = reader.Scan(bytes, Owner, Revision, [Original], limits: new(MaximumMatches: 1));
        Assert.Single(result.Value!.Candidates); Assert.False(result.Value.Complete); Assert.Contains("NodeInventoryMatchLimit", result.Diagnostics);
        Assert.Null(reader.Scan(bytes, Owner, Revision, [Original], limits: new(MaximumBytes: 0)).Value);
        Assert.Throws<OperationCanceledException>(() => reader.Scan(bytes, Owner, Revision, [Original], token: new(true)));
        Assert.Throws<ArgumentException>(() => reader.Scan(bytes, Owner, new("other", "revision-a"), [Original]));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Scan(bytes, Owner, Revision, [Original], decodedOffset: long.MaxValue));
    }

    [Fact]
    public void OwnerLocalCorrelationRetainsDuplicatesAndDoesNotResolveArchiveReferences()
    {
        var dependencies = Dependencies([(40, 1), (50, 2), (40, 1)]);
        var directory = Directory([(40, 1), (40, 1), (50, 2)]);
        var result = new DependencyDirectoryCorrelator().Compare(dependencies, directory);
        Assert.True(result.Value!.SamePairMultiset); Assert.False(result.Value.SamePairOrder);
        Assert.Equal(ReferenceStatus.Ambiguous, result.Value.Matches[0].Cardinality);
        Assert.Equal(new long[] { 0, 1 }, result.Value.Matches[0].DirectoryEntryOrdinals);
        Assert.Equal(2, result.Value.Matches[0].DirectoryEntryExtents.Count);
        Assert.Equal(ReferenceStatus.Unique, result.Value.Matches[1].Cardinality);
        Assert.Equal(3, result.UnresolvedReferences.Count);
        var repeated = result.Value.PairCounts.Single(pair => pair.Word0 == 40);
        Assert.Equal(2, repeated.DependencyCount); Assert.Equal(2, repeated.DirectoryCount);
        Assert.Equal("Unassigned", result.UnknownFields["targetIdentity"]);
        Assert.Equal(2, result.Extents.Count);
    }

    [Fact]
    public void CorrelationDistinguishesMissingPairsAndRejectsOtherOwnersAndRevisions()
    {
        var dependencies = Dependencies([(40, 1)]); var directory = Directory([(50, 2)]);
        var reader = new DependencyDirectoryCorrelator(); var result = reader.Compare(dependencies, directory);
        Assert.False(result.Value!.SamePairMultiset); Assert.False(result.Value.SamePairOrder);
        Assert.Equal(ReferenceStatus.Missing, result.Value.Matches.Single().Cardinality);
        Assert.Empty(result.Value.Matches.Single().DirectoryEntryOrdinals);
        Assert.Throws<ArgumentException>(() => reader.Compare(dependencies, directory with { Identity = new("index-a", 1) }));
        Assert.Throws<ArgumentException>(() => reader.Compare(dependencies, directory with { Revision = new("index-a", "revision-b") }));
        var foreign = dependencies with { Value = dependencies.Value! with
        {
            References = [dependencies.Value!.References[0] with
            { Candidate = dependencies.Value!.References[0].Candidate with { Owner = new("index-a", 1) } }]
        } };
        Assert.Throws<ArgumentException>(() => reader.Compare(foreign, directory));
        Assert.Throws<OperationCanceledException>(() => reader.Compare(dependencies, directory, new(true)));
    }

    private static byte[] Tag(uint word8, uint word12)
    {
        var bytes = new byte[16]; "NODE    "u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), word8); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), word12); return bytes;
    }
    private static DecodeResult<SceneDependencyInspection> Dependencies((uint Word0, uint Word1)[] pairs)
    {
        using var stream = new MemoryStream(); stream.Write(IndexLayout.Magic); Big(stream, 18); Big(stream, 1);
        Big(stream, 7); Big(stream, 1); Big(stream, 1); Big(stream, (uint)pairs.Length);
        foreach (var pair in pairs) { Big(stream, pair.Word0); Big(stream, pair.Word1); Big(stream, 0); Big(stream, 0); Big(stream, 0); }
        var stamp = new FileStamp(true, "file", 0, 0, 0, true);
        var archive = new ArchiveSnapshot("sample.idx", "index-a", 18, 0, [], 1,
            [new(0, 7, 1, 1, 0, 0, 0, 0, "sample.dat")], [], stamp, "revision-a", []);
        return new SceneDependencyReader().Inspect(stream.ToArray(), archive, Owner, "sample.dep");
    }
    private static DecodeResult<DcInfoDirectoryInspection> Directory((uint Word0, uint Word1)[] pairs)
    {
        var bytes = new byte[24 + pairs.Length * 8]; "DC_INFO "u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 3); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)(8 + pairs.Length * 8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), (uint)pairs.Length);
        for (var i = 0; i < pairs.Length; i++) { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + i * 8), pairs[i].Word0); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28 + i * 8), pairs[i].Word1); }
        return new DcInfoDirectoryReader().Inspect(bytes, Owner, Revision, [Original]);
    }
    private static void Big(Stream stream, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes); }
}
