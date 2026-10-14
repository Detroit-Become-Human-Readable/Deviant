using System.Buffers.Binary;
using System.Collections;
using System.Runtime.InteropServices;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class PhysicsTaggedMetadataBoundaryTests
{
    private static readonly ResourceIdentity Container = new("generated-index", 9);
    private static readonly SourceRevision Revision = new("generated-index", "generated-revision");
    private static readonly SourceExtent[] Physical = [new("resources.bin", 0, 4096)];

    [Fact]
    public void RejectedTokensCannotAllocateUnbudgetedUnknownRanges()
    {
        var body = Enumerable.Range(0, 1000).SelectMany(_ => new byte[] { 65, 66, 0 }).ToArray();
        var envelope = Envelope(Chunk("TSTR", body)); const long budget = 20 * 1024;
        var result = new PhysicsTaggedMetadataReader().Read(envelope, Physical,
            limits: new(MaximumTokenBytes: 1, MaximumMetadataBytes: budget));
        var value = Assert.IsType<PhysicsTaggedMetadataDocument>(result.Value);
        Assert.Equal(PhysicsTaggedMetadataCoverage.Partial, value.Coverage);
        Assert.Empty(value.Tokens);
        Assert.InRange(value.RetainedMetadataBytes, 0, budget);
        // Each retained DecodedExtent has two longs and a reference, in addition to its object header.
        Assert.True(value.UnknownRanges.Count * 40L <= budget);
        Assert.True(value.UnknownRanges.Count < 1000);
        var remainder = Assert.IsType<DecodedExtent>(value.BoundedRemainderExtent);
        Assert.Equal(envelope.EmbeddedExtent.Container, remainder.Container);
        Assert.True(remainder.Offset >= envelope.EmbeddedExtent.Offset);
        Assert.True(remainder.Offset + remainder.Length <= envelope.EmbeddedExtent.Offset + envelope.EmbeddedExtent.Length);
        for (var i = 0; i < 1000; i++)
        {
            var start = envelope.EmbeddedExtent.Offset + 8 + i * 3;
            Assert.True(value.UnknownRanges.Any(range => Contains(range, start, 2)) || Contains(remainder, start, 2));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsDetachedEmbeddedViewsWithoutReadingData(bool changed)
    {
        var envelope = Envelope(Chunk("DATA", [1, 2, 3]));
        var detached = envelope.EmbeddedBytes.ToArray();
        if (changed) detached[^1] ^= 1;
        var result = new PhysicsTaggedMetadataReader().Read(envelope with { EmbeddedBytes = detached }, Physical);
        Assert.Null(result.Value);
        Assert.Contains("TaggedMetadataEnvelopeMismatch", result.Diagnostics);
    }

    [Fact]
    public void CancellationAfterMetadataCountPrecedesInvalidCountRejection()
    {
        var envelope = Envelope(Chunk("DATA", []));
        using var stop = new CancellationTokenSource();
        var sources = new CallbackList<SourceExtent>(0, _ => throw new InvalidOperationException(),
            () => { stop.Cancel(); return -1; });
        Assert.Throws<OperationCanceledException>(() => new PhysicsTaggedMetadataReader().Read(envelope,
            sources, token: stop.Token));
        Assert.Equal(0, sources.Accesses);
    }

    [Fact]
    public void CancellationAfterInvalidPhysicalIndexerIsPropagated()
    {
        var envelope = Envelope(Chunk("DATA", []));
        using var stop = new CancellationTokenSource();
        var sources = new CallbackList<SourceExtent>(1, _ => { stop.Cancel(); return null!; });
        Assert.Throws<OperationCanceledException>(() => new PhysicsTaggedMetadataReader().Read(envelope,
            sources, token: stop.Token));
        Assert.Equal(1, sources.Accesses);
    }

    [Fact]
    public void CancellationAfterInvalidStepIndexerIsPropagated()
    {
        var envelope = Envelope(Chunk("DATA", []));
        using var stop = new CancellationTokenSource();
        var steps = new CallbackList<DecodeStep>(1, _ => { stop.Cancel(); return null!; });
        Assert.Throws<OperationCanceledException>(() => new PhysicsTaggedMetadataReader().Read(envelope,
            Physical, steps, token: stop.Token));
        Assert.Equal(1, steps.Accesses);
    }

    [Fact]
    public void CopiedMetadataIsIndependentOfInputAndJsonOmitsRawViews()
    {
        var envelope = Envelope(Chunk("TSTR", [65, 66, 0]));
        var value = Assert.IsType<PhysicsTaggedMetadataDocument>(new PhysicsTaggedMetadataReader().Read(envelope, Physical).Value);
        var chunk = Assert.Single(value.Chunks); var stored = Assert.Single(value.Tokens);
        Assert.True(MemoryMarshal.TryGetArray(envelope.RawPayload, out var original));
        Array.Fill(original.Array!, (byte)255, original.Offset, original.Count);
        Assert.Equal(new byte[] { 65, 66, 0 }, chunk.MetadataBody!.Value.ToArray());
        Assert.Equal(new byte[] { 65, 66 }, stored.RawBytes);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value));
        var chunkJson = json.RootElement.GetProperty("Chunks")[0];
        Assert.False(chunkJson.TryGetProperty("RawHeader", out _));
        Assert.False(chunkJson.TryGetProperty("MetadataBody", out _));
        Assert.False(json.RootElement.GetProperty("Tokens")[0].TryGetProperty("RawBytes", out _));
        Assert.False(json.RootElement.TryGetProperty("RawPayload", out _));
        Assert.Equal("AB", json.RootElement.GetProperty("Tokens")[0].GetProperty("PrintableText").GetString());
    }

    private static bool Contains(DecodedExtent extent, long start, long length) =>
        extent.Offset <= start && extent.Offset + extent.Length >= start + length;

    private static byte[] Chunk(string tag, byte[] body)
    {
        var bytes = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 0x40000000u | (uint)bytes.Length);
        System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(bytes, 4); body.CopyTo(bytes, 8); return bytes;
    }

    private static PhysicsEnvelopeDocument Envelope(byte[] embedded)
    {
        var bytes = new byte[20 + embedded.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        "PHYSYS__"u8.CopyTo(bytes.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)embedded.Length);
        embedded.CopyTo(bytes, 20);
        var frame = new NativeObjectFrame(new(Container.IndexContentId, Container.Ordinal, "dc-object/0/2201/37"),
            0, 2201, 37, new(Container, 24, 8), new(Container, 128, 13), new(Container, 141, bytes.Length),
            new byte[13], bytes, 0, 0, 0, 1);
        return Assert.IsType<PhysicsEnvelopeDocument>(new PhysicsEnvelopeReader().Read(frame, Revision, Physical).Value);
    }

    private sealed class CallbackList<T>(int count, Func<int, T> read, Func<int>? readCount = null) : IReadOnlyList<T>
    {
        public int Count => readCount?.Invoke() ?? count;
        public int Accesses { get; private set; }
        public T this[int index] { get { Accesses++; return read(index); } }
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Declared-count indexing is required.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
