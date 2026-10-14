using System.Buffers.Binary;
using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class PhysicsTaggedMetadataReaderTests
{
    [Fact]
    public void EnumeratesFramingAndStoredTokensWithOwnedMetadataOnly()
    {
        var source = TaggedFixture.Envelope(TaggedFixture.Root(
            TaggedFixture.Chunk("DATA", 1, new byte[1024 * 1024]),
            TaggedFixture.Chunk("TYPE", 0, TaggedFixture.Chunk("TSTR", 1, Encoding.ASCII.GetBytes("alpha\0beta\0\0"))),
            TaggedFixture.Chunk("INDX", 0, TaggedFixture.Chunk("ITEM", 1, [1, 2, 3, 4]))));
        var result = Read(source); var value = Assert.IsType<PhysicsTaggedMetadataDocument>(result.Value);
        Assert.Equal(PhysicsTaggedMetadataCoverage.CompleteFraming, value.Coverage); Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { "TAG0", "DATA", "TYPE", "TSTR", "INDX", "ITEM" }, value.Chunks.Select(chunk => chunk.Tag));
        Assert.Equal(new[] { "alpha", "beta" }, value.Tokens.Select(token => token.PrintableText));
        var data = value.Chunks.Single(chunk => chunk.Tag == "DATA"); Assert.Null(data.MetadataBody); Assert.Null(data.MetadataBodySha256);
        Assert.True(value.RetainedMetadataBytes < 16 * 1024); Assert.Equal(1, data.RawFlags);
        var tstr = value.Chunks.Single(chunk => chunk.Tag == "TSTR");
        Assert.Equal(tstr.Ordinal, value.Tokens[0].ChunkOrdinal); Assert.Equal(tstr.BodyExtent.Offset, value.Tokens[0].Extent.Offset);
        Assert.True(MemoryMarshal.TryGetArray(tstr.MetadataBody!.Value, out var owned));
        Assert.True(MemoryMarshal.TryGetArray(source.RawPayload, out var original)); Assert.NotSame(original.Array, owned.Array);
        Assert.Equal(tstr.BodyExtent.Length, owned.Array!.Length);
        Assert.Equal(source.Identity, value.Identity); Assert.Equal(source.Revision, value.Revision);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value));
        var chunkJson = json.RootElement.GetProperty("Chunks")[3];
        Assert.False(chunkJson.TryGetProperty("RawHeader", out _)); Assert.False(chunkJson.TryGetProperty("MetadataBody", out _));
        Assert.False(json.RootElement.GetProperty("Tokens")[0].TryGetProperty("RawBytes", out _));
    }

    [Theory]
    [InlineData("WHAT", 1)] [InlineData("TSTR", 2)] [InlineData("TYPE", 3)]
    public void UnknownTagsAndFlagsHavePartialCoverageAndPreservedBodyExtent(string tag, byte flags)
    {
        var result = Read(TaggedFixture.Envelope(TaggedFixture.Root(TaggedFixture.Chunk(tag, flags, [1, 2, 3]))));
        var value = Assert.IsType<PhysicsTaggedMetadataDocument>(result.Value);
        Assert.Equal(PhysicsTaggedMetadataCoverage.Partial, value.Coverage); Assert.Contains("TaggedMetadataUnknownProfile", result.Diagnostics);
        Assert.Equal(value.Chunks[1].BodyExtent, Assert.Single(value.UnknownRanges)); Assert.Null(value.Chunks[1].MetadataBody);
    }

    [Theory]
    [InlineData(0u)] [InlineData(7u)] [InlineData(0x3fffffffu)]
    public void MalformedChunkLengthsAreBoundedPartialRanges(uint word)
    {
        var leaf = TaggedFixture.Chunk("TSTR", 1, [65, 0]); BinaryPrimitives.WriteUInt32BigEndian(leaf, word);
        var result = Read(TaggedFixture.Envelope(TaggedFixture.Root(leaf))); var value = Assert.IsType<PhysicsTaggedMetadataDocument>(result.Value);
        Assert.Equal(PhysicsTaggedMetadataCoverage.Partial, value.Coverage); Assert.Contains("TaggedMetadataLengthInvalid", result.Diagnostics);
        Assert.Single(value.Chunks); Assert.Single(value.UnknownRanges);
    }

    [Fact]
    public void TruncatedChildHeaderAndEmptyEnvelopeRemainInspectable()
    {
        var partial = Read(TaggedFixture.Envelope(TaggedFixture.Root([1, 2, 3])));
        Assert.Contains("TaggedMetadataHeaderTruncated", partial.Diagnostics);
        Assert.Equal(PhysicsTaggedMetadataCoverage.Partial, partial.Value!.Coverage);
        var empty = Read(TaggedFixture.Envelope([])); Assert.Empty(empty.Value!.Chunks);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void DepthAndChunkBudgetsRetainTheUnvisitedRange(int kind)
    {
        var input = TaggedFixture.Envelope(TaggedFixture.Root(TaggedFixture.Chunk("TYPE", 0,
            TaggedFixture.Chunk("TSTR", 1, [65, 0]))));
        var limits = kind == 0 ? new PhysicsTaggedMetadataLimits(MaximumDepth: 0) : new(MaximumChunks: 1);
        var result = Read(input, limits); Assert.Equal(PhysicsTaggedMetadataCoverage.Partial, result.Value!.Coverage);
        Assert.Single(result.Value.UnknownRanges);
    }

    [Fact]
    public void LeafAndTokenLimitsDoNotCopyExcessiveData()
    {
        var input = TaggedFixture.Envelope(TaggedFixture.Root(TaggedFixture.Chunk("TSTR", 1, Encoding.ASCII.GetBytes("long\0next\0"))));
        var leaf = Read(input, new(MaximumLeafBytes: 1)); Assert.Null(leaf.Value!.Chunks[1].MetadataBody);
        Assert.Contains("TaggedMetadataLeafLimit", leaf.Diagnostics);
        var token = Read(input, new(MaximumTokenBytes: 2)); Assert.Empty(token.Value!.Tokens);
        Assert.Contains("TaggedMetadataTokenByteLimit", token.Diagnostics);
        var count = Read(input, new(MaximumTokens: 1)); Assert.Single(count.Value!.Tokens);
        Assert.Contains("TaggedMetadataTokenLimit", count.Diagnostics);
    }

    [Fact]
    public void NonprintableAndUnterminatedTokensRetainBytesAndPartialStatus()
    {
        var result = Read(TaggedFixture.Envelope(TaggedFixture.Root(TaggedFixture.Chunk("FSTR", 1, [255, 0, 65]))));
        Assert.Equal(PhysicsTaggedMetadataCoverage.Partial, result.Value!.Coverage);
        Assert.Equal(new byte[] { 255 }, result.Value.Tokens[0].RawBytes); Assert.Null(result.Value.Tokens[0].PrintableText);
        Assert.Equal("A", result.Value.Tokens[1].PrintableText); Assert.False(result.Value.Tokens[1].Terminated);
    }

    [Fact]
    public void ExhaustedUnknownRangeBudgetUsesOneBoundedRemainder()
    {
        var body = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("AB\0", 1000)));
        var result = Read(TaggedFixture.Envelope(TaggedFixture.Root(TaggedFixture.Chunk("TSTR", 1, body))),
            new(MaximumTokenBytes: 1, MaximumMetadataBytes: 20 * 1024));
        var value = Assert.IsType<PhysicsTaggedMetadataDocument>(result.Value);
        Assert.Equal(PhysicsTaggedMetadataCoverage.Partial, value.Coverage);
        Assert.True(value.RetainedMetadataBytes <= 20 * 1024); Assert.True(value.UnknownRanges.Count < 1000);
        Assert.NotNull(value.BoundedRemainderExtent); Assert.Empty(value.Tokens);
    }

    [Fact]
    public void RejectsForgedMemoryViewsCachedHeadersAndSourceMetadata()
    {
        var input = TaggedFixture.Envelope(TaggedFixture.Root(TaggedFixture.Chunk("TSTR", 1, [65, 0])));
        Assert.Contains("TaggedMetadataEnvelopeMismatch", Read(input with { EmbeddedBytes = input.EmbeddedBytes.ToArray() }).Diagnostics);
        Assert.Contains("TaggedMetadataEnvelopeMismatch", Read(input with { EmbeddedHeader = input.EmbeddedHeader! with { Lower30Bits = 1 } }).Diagnostics);
        Assert.Contains("TaggedMetadataEnvelopeMismatch", Read(input with { EmbeddedExtent = input.EmbeddedExtent with { Offset = long.MaxValue } }).Diagnostics);
        Assert.Contains("TaggedMetadataEnvelopeMismatch", Read(input with { Identity = input.Identity with { MemberPath = "alias" } }).Diagnostics);
        var bad = new PhysicsTaggedMetadataReader().Read(input, [new("../outside.dat", 0, 1)]);
        Assert.Contains("TaggedMetadataPhysicalExtentInvalid", bad.Diagnostics);
        Assert.Contains("TaggedMetadataStepInvalid", new PhysicsTaggedMetadataReader().Read(input, TaggedFixture.Physical, [null!]).Diagnostics);
    }

    [Fact]
    public void MetadataBudgetsRejectHostileCountsAndObserveAccessorCancellation()
    {
        var input = TaggedFixture.Envelope(TaggedFixture.Root([]));
        var hostile = new CallbackList<SourceExtent>(int.MaxValue, _ => throw new InvalidOperationException());
        Assert.Contains("TaggedMetadataContextLimit", new PhysicsTaggedMetadataReader().Read(input, hostile).Diagnostics);
        Assert.Equal(0, hostile.Accesses);
        using var stop = new CancellationTokenSource();
        var count = new CallbackList<SourceExtent>(0, _ => throw new InvalidOperationException(), () => { stop.Cancel(); return -1; });
        Assert.Throws<OperationCanceledException>(() => new PhysicsTaggedMetadataReader().Read(input, count, token: stop.Token));
        using var indexStop = new CancellationTokenSource();
        var index = new CallbackList<SourceExtent>(1, _ => { indexStop.Cancel(); return null!; });
        Assert.Throws<OperationCanceledException>(() => new PhysicsTaggedMetadataReader().Read(input, index, token: indexStop.Token));
        var huge = new DecodeStep("generated", "1", new string('x', 16 * 1024));
        Assert.Contains("TaggedMetadataContextLimit", new PhysicsTaggedMetadataReader().Read(input, TaggedFixture.Physical,
            [huge], new(MaximumMetadataBytes: 16 * 1024)).Diagnostics);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void RejectsNegativeLimits(int kind)
    {
        var limits = kind switch
        {
            0 => new PhysicsTaggedMetadataLimits(MaximumDepth: -1), 1 => new(MaximumChunks: -1),
            2 => new(MaximumTokens: -1), 3 => new(MaximumTokenBytes: -1),
            4 => new(MaximumLeafBytes: -1), _ => new(MaximumMetadataBytes: -1)
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => Read(TaggedFixture.Envelope(TaggedFixture.Root([])), limits));
    }

    private static DecodeResult<PhysicsTaggedMetadataDocument> Read(PhysicsEnvelopeDocument input, PhysicsTaggedMetadataLimits? limits = null) =>
        new PhysicsTaggedMetadataReader().Read(input, TaggedFixture.Physical, limits: limits);
    private sealed class CallbackList<T>(int count, Func<int, T> read, Func<int>? getCount = null) : IReadOnlyList<T>
    {
        public int Count => getCount?.Invoke() ?? count; public int Accesses { get; private set; }
        public T this[int index] { get { Accesses++; return read(index); } }
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

internal static class TaggedFixture
{
    internal static readonly ResourceIdentity Container = new("generated-index", 23);
    internal static readonly SourceRevision Revision = new("generated-index", "generated-revision");
    internal static readonly SourceExtent[] Physical = [new("resources.dat", 0, 4096)];
    internal static byte[] Chunk(string tag, byte flags, byte[] body)
    {
        var result = new byte[8 + body.Length]; BinaryPrimitives.WriteUInt32BigEndian(result, ((uint)flags << 30) | (uint)result.Length);
        Encoding.ASCII.GetBytes(tag).CopyTo(result, 4); body.CopyTo(result, 8); return result;
    }
    internal static byte[] Root(params byte[][] children) => Chunk("TAG0", 0, children.SelectMany(child => child).ToArray());
    internal static PhysicsEnvelopeDocument Envelope(byte[] embedded)
    {
        var bytes = new byte[20 + embedded.Length]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        "PHYSYS__"u8.CopyTo(bytes.AsSpan(4)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)embedded.Length); embedded.CopyTo(bytes, 20);
        var frame = new NativeObjectFrame(new(Container.IndexContentId, Container.Ordinal, "dc-object/0/2201/37"), 0, 2201, 37,
            new(Container, 24, 8), new(Container, 128, 13), new(Container, 141, bytes.Length), new byte[13], bytes, 0, 0, 0, 1);
        return new PhysicsEnvelopeReader().Read(frame, Revision, Physical).Value!;
    }
}
