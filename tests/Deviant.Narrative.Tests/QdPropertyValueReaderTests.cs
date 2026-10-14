using System.Buffers.Binary;
using System.Buffers;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Formats.Narrative;
using Xunit;

namespace Deviant.Narrative.Tests;

public sealed class QdPropertyValueReaderTests
{
    private const string IndexId = "generated-index";
    private static readonly ResourceIdentity Resource = new(IndexId, 17);
    private static readonly SourceRevision Revision = new(IndexId, "generated-source-stamp");

    [Fact]
    public void ReadsOrderedObjectEdgesKeepsHashCollisionsAndSeparatesOwnAndSubtreeExtents()
    {
        var first = Boolean(0x00000000, 2);
        var second = String(0x00000000, "branch"u8.ToArray());
        var bytes = Container(0x88, 0x11223344, [first, second]);
        var result = Read(bytes, offset: 500);

        var document = Assert.IsType<QdPropertyValueDocument>(result.Value);
        Assert.Equal(3, document.Nodes.Count);
        var root = document.Nodes[0];
        Assert.Equal(QdPropertyValueKind.Object, root.Kind);
        Assert.Equal(0x11223344u, root.PropertyNameHash);
        Assert.Equal(new DecodedExtent(Resource, 500, 13), root.OwnExtent);
        Assert.Equal(new DecodedExtent(Resource, 500, bytes.Length), root.SubtreeExtent);
        Assert.NotEqual(root.OwnExtent, root.SubtreeExtent);
        Assert.Equal(new[] { 0, 1 }, document.ChildEdges.Select(edge => edge.ChildOrdinal));
        Assert.Equal(new uint[] { 13, 19 }, document.ChildEdges.Select(edge => edge.EncodedRelativeOffset));
        Assert.Equal(0u, document.Nodes[1].PropertyNameHash);
        Assert.Equal(0u, document.Nodes[2].PropertyNameHash);
        Assert.NotEqual(document.Nodes[1].NodeId, document.Nodes[2].NodeId);
        Assert.Equal(new DecodedExtent(Resource, 500 + 13, 6), document.Nodes[1].OwnExtent);
        Assert.Equal(new DecodedExtent(Resource, 500 + 19 + 9, 6), document.Nodes[2].ValueExtent);
    }

    [Fact]
    public void ReadsEmptyArrayAndFourByteLittleEndianChildTable()
    {
        var empty = Container(0x89, 1, []);
        var emptyDocument = Assert.IsType<QdPropertyValueDocument>(Read(empty).Value);
        Assert.Equal(QdPropertyValueKind.Array, Assert.Single(emptyDocument.Nodes).Kind);
        Assert.Empty(emptyDocument.ChildEdges);
        Assert.Equal((ushort)0, Assert.Single(emptyDocument.Nodes).ChildCount);

        var child = Type7(0x88776655);
        var wide = Container(0xc8, 2, [child]);
        var wideDocument = Assert.IsType<QdPropertyValueDocument>(Read(wide).Value);
        Assert.Equal(4, wideDocument.Nodes[0].ChildOffsetWidth);
        Assert.Equal(11u, Assert.Single(wideDocument.ChildEdges).EncodedRelativeOffset);
        Assert.Equal(QdPropertyValueKind.UnknownHeaderOnly, wideDocument.Nodes[1].Kind);
        Assert.Equal(5, wideDocument.Nodes[1].OwnExtent.Length);
        Assert.Null(wideDocument.Nodes[1].ValueExtent);
    }

    [Fact]
    public void PreservesBooleanRawByteAndInvalidUtf8Bytes()
    {
        var boolean = Assert.Single(Assert.IsType<QdPropertyValueDocument>(Read(Boolean(7, 2)).Value).Nodes);
        Assert.Equal((byte)2, boolean.BooleanRawByte);
        Assert.True(boolean.BooleanValue);
        Assert.Equal(new byte[] { 2 }, boolean.RawValueBytes.ToArray());

        var stringBytes = String(9, [0xff, 0x41]);
        var parsed = Assert.Single(Assert.IsType<QdPropertyValueDocument>(Read(stringBytes).Value).Nodes);
        Assert.False(parsed.StringIsValidUtf8);
        Assert.Equal("\uFFFDA", parsed.StringDisplay);
        Assert.Equal(new byte[] { 0xff, 0x41 }, parsed.RawValueBytes.ToArray());
    }

    [Fact]
    public void ReadsAllSignedIntegerWidthsAndPreservesRawBits()
    {
        var samples = new (byte Tag, byte[] Bytes, int Width)[]
        {
            (0x81, [0xff], 1),
            (0x91, [0xff, 0xff], 2),
            (0xa1, [0xff, 0xff, 0xff, 0xff], 4),
            (0xb1, [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff], 8)
        };
        foreach (var sample in samples)
        {
            var node = Assert.Single(Assert.IsType<QdPropertyValueDocument>(Read(Scalar(sample.Tag, 3, sample.Bytes)).Value).Nodes);
            Assert.Equal(QdPropertyValueKind.SignedInteger, node.Kind);
            Assert.Equal(-1L, node.SignedIntegerValue);
            Assert.Equal((long)sample.Width, node.ValueExtent!.Length);
            Assert.Equal(Enumerable.Repeat((byte)0xff, sample.Width), node.RawValueBytes.ToArray());
            Assert.Equal((byte)(sample.Tag & 0x70), node.ModeFlags);
        }
    }

    [Fact]
    public void ReadsNumberModesAndKeepsNonFiniteNumberBits()
    {
        var cases = new (byte Tag, byte[] Bytes, string Text, bool Finite, ulong Bits)[]
        {
            (0x82, [0xfe], "-2", true, 0xfe),
            (0x92, [0x1e, 0xfb], "-12.5", true, 0xfb1e),
            (0xa2, [0x01, 0x00, 0xc0, 0x7f], "NaN", false, 0x7fc00001),
            (0xb2, [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xf0, 0x7f], "Infinity", false, 0x7ff0000000000000)
        };
        foreach (var sample in cases)
        {
            var node = Assert.Single(Assert.IsType<QdPropertyValueDocument>(Read(Scalar(sample.Tag, 5, sample.Bytes)).Value).Nodes);
            Assert.Equal(QdPropertyValueKind.Number, node.Kind);
            Assert.Equal(sample.Text, node.NumericDisplay);
            Assert.Equal(sample.Finite, node.NumericIsFinite);
            Assert.Equal(sample.Bits, node.NumericBits);
            Assert.Equal(sample.Bytes, node.RawValueBytes.ToArray());
        }
    }

    [Fact]
    public void PreservesPackedArrayWidthsAndBytesWithoutDecodingElements()
    {
        var cases = new (byte Tag, int Width)[]
        {
            (0x84, 1), (0x85, 1), (0x95, 2), (0xa5, 4), (0xa6, 4), (0xb5, 8), (0xb6, 8)
        };
        foreach (var (tag, width) in cases)
        {
            var raw = Enumerable.Range(0, width * 2).Select(value => (byte)(value + 1)).ToArray();
            var node = Assert.Single(Assert.IsType<QdPropertyValueDocument>(Read(Packed(tag, 0xdeadbeef, 2, raw)).Value).Nodes);
            Assert.Equal(QdPropertyValueKind.PackedArray, node.Kind);
            Assert.Equal(2u, node.PackedElementCount);
            Assert.Equal(width, node.PackedElementWidth);
            Assert.Equal(raw, node.RawValueBytes.ToArray());
            Assert.Equal(9L + raw.Length, node.OwnExtent.Length);
        }
    }

    [Fact]
    public void RetainsRepeatedTargetEdgesAsExplicitAliases()
    {
        var child = Type7(5);
        var bytes = ContainerWithOffsets(0x88, 9, [13, 13], child);
        var document = Assert.IsType<QdPropertyValueDocument>(Read(bytes).Value);
        Assert.Equal(2, document.ChildEdges.Count);
        Assert.Single(document.Nodes, node => node.Kind == QdPropertyValueKind.UnknownHeaderOnly);
        Assert.True(document.Nodes[1].IsAliased);
        Assert.All(document.ChildEdges, edge => Assert.True(edge.IsAliasTarget));
        Assert.Equal(document.ChildEdges[0].TargetNodeId, document.ChildEdges[1].TargetNodeId);
    }

    [Fact]
    public void ForwardAliasKeepsTheBoundingRangeSeparateFromContiguousSubtreeBytes()
    {
        var a = ContainerWithOffsets(0x88, 10, [16], []);
        var b = Boolean(11, 1);
        var c = Boolean(12, 0);
        var bytes = ContainerWithOffsets(0x88, 13, [16, 26, 32], [.. a, .. b, .. c]);
        var document = Assert.IsType<QdPropertyValueDocument>(Read(bytes).Value);
        var aNode = Assert.Single(document.Nodes, node => node.PayloadOffset == 16);
        Assert.Equal(new DecodedExtent(Resource, 16, 22), aNode.SubtreeRange);
        Assert.False(aNode.SubtreeExtentIsContiguous);
        Assert.Null(aNode.SubtreeExtent);
        Assert.Single(document.Nodes, node => node.PayloadOffset == 32 && node.IsAliased);
        Assert.Equal(4, document.ChildEdges.Count);
    }

    [Fact]
    public void AliasPathUsesCachedDescendantDepthEvenWhenTargetWasParsedEarlier()
    {
        var first = ContainerWithOffsets(0x88, 4, [10], []);
        var second = ContainerWithOffsets(0x88, 1, [10], []);
        var shallowAlias = ContainerWithOffsets(0x88, 3, [23, 13], [.. first, .. second, .. Boolean(5, 1)]);
        var result = Read(shallowAlias, new(MaximumDepth: 2));
        Assert.Contains("DepthLimitExceeded", Assert.Single(result.Diagnostics));
    }

    [Fact]
    public void RejectsCycleOverlapGapAndOutOfBoundsTargets()
    {
        var cycle = ContainerWithOffsets(0x88, 1, [0], []);
        Assert.Contains("ValueGraphCycle", Assert.Single(Read(cycle).Diagnostics));

        var first = Boolean(0x80, 1);
        var second = Boolean(2, 1);
        var overlap = ContainerWithOffsets(0x88, 1, [13, 14], [.. first, .. second]);
        Assert.Contains("OverlappingNodeExtents", Assert.Single(Read(overlap).Diagnostics));

        var gap = ContainerWithOffsets(0x88, 1, [14], [0xaa, 0xaa, 0xaa, 0xaa, .. Type7(2)]);
        Assert.Contains("UnsupportedPayloadGap", Assert.Single(Read(gap).Diagnostics));

        var oob = ContainerWithOffsets(0x88, 1, [0x00ffffff], []);
        Assert.Contains("ChildOffsetOutOfBounds", Assert.Single(Read(oob).Diagnostics));
    }

    [Fact]
    public void ReportsUnknownWholeProfilesWithHashAndUnknownExtent()
    {
        var bytes = Scalar(0x8a, 0x12345678, []);
        var result = Read(bytes);
        Assert.Null(result.Value);
        Assert.Contains("UnsupportedTypeProfile", Assert.Single(result.Diagnostics));
        Assert.Matches("^[0-9a-f]{64}$", Assert.Single(result.Steps).PayloadSha256!);
        Assert.Equal("0+5", result.UnknownFields["unknownExtent"]);
        Assert.Equal("8A", result.UnknownFields["rawTag"]);

        var unknownPackedMode = Read(Packed(0x94, 4, 1, [0xaa]));
        Assert.Contains("UnsupportedPackedArrayMode", Assert.Single(unknownPackedMode.Diagnostics));
        Assert.Equal($"0+{Packed(0x94, 4, 1, [0xaa]).Length}", unknownPackedMode.UnknownFields["unknownExtent"]);
        Assert.Matches("^[0-9a-f]{64}$", Assert.Single(unknownPackedMode.Steps).PayloadSha256!);
    }

    [Fact]
    public void EnforcesDepthNodeEdgeStringAndRetainedLimits()
    {
        var leaf = Type7(4);
        var nested = Container(0x88, 1, [Container(0x88, 2, [leaf])]);
        Assert.Contains("DepthLimitExceeded", Assert.Single(Read(nested, new(MaximumDepth: 1)).Diagnostics));
        Assert.Contains("NodeLimitExceeded", Assert.Single(Read(nested, new(MaximumNodes: 1)).Diagnostics));
        Assert.Contains("MemoryLimitExceeded", Assert.Single(Read(nested, new(MaximumRetainedBytes: nested.Length + 1536L)).Diagnostics));
        Assert.Contains("PayloadLimitExceeded", Assert.Single(Read(nested, new(MaximumPayloadBytes: nested.Length - 1)).Diagnostics));
        Assert.Contains("StringLimitExceeded", Assert.Single(Read(String(1, new byte[8]), new(MaximumPayloadBytes: 64, MaximumStringBytes: 4, MaximumRetainedBytes: 4096, MaximumNodes: 10, MaximumEdges: 10, MaximumDepth: 4)).Diagnostics));

        var excessiveOffsetCount = new byte[] { 0x88, 1, 0, 0, 0, 0xff, 0xff };
        Assert.Contains("TruncatedOffsetTable", Assert.Single(Read(excessiveOffsetCount).Diagnostics));
    }

    [Fact]
    public void ChecksProvenanceAndCancellationAndKeepsAnOwnedSnapshot()
    {
        var mutable = Boolean(3, 1);
        var original = mutable.ToArray();
        var identity = Resource with { MemberPath = "bundle/value" };
        var parent = Resource;
        var extent = new DecodedExtent(parent, 200, mutable.Length);
        var sources = new[] { new SourceExtent("volumes/pc.bin", 1000, mutable.Length) };
        var result = new QdPropertyValueReader().Read(mutable, identity, Revision, extent, sources);
        var document = Assert.IsType<QdPropertyValueDocument>(result.Value);
        Array.Fill(mutable, (byte)0xff);
        sources[0] = new("other.bin", 0, 0);
        Assert.Equal(original, document.RawBytes.ToArray());
        Assert.Equal(new byte[] { 1 }, Assert.Single(document.Nodes).RawValueBytes.ToArray());
        Assert.Equal("bundle/value/qd-value/00000000", Assert.Single(document.Nodes).ValueIdentity.MemberPath);
        Assert.Equal(new DecodedExtent(parent, 200, original.Length), document.ConsumedExtent);
        Assert.Equal("volumes/pc.bin", Assert.Single(document.OriginalPhysicalExtents).RelativeSource);

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => Read(original, token: cancelled.Token));
        var foreign = new QdPropertyValueReader().Read(original, identity, Revision,
            new DecodedExtent(Resource with { Ordinal = 18 }, 0, original.Length), []);
        Assert.Contains("SourceIdentityMismatch", Assert.Single(foreign.Diagnostics));
        var unsafeSource = Read(original, sources: [new SourceExtent("../private.bin", 0, original.Length)]);
        Assert.Contains("InvalidPhysicalExtent", Assert.Single(unsafeSource.Diagnostics));
        var negativeSourceCount = new QdPropertyValueReader().Read(original, Resource, Revision,
            new DecodedExtent(Resource, 0, original.Length), new GuardedSources(-1, _ =>
                throw new InvalidOperationException("A negative source count must be rejected before access.")));
        Assert.Contains("PhysicalExtentLimitExceeded", Assert.Single(negativeSourceCount.Diagnostics));

        using var duringCopy = new CancellationTokenSource();
        var memory = new CancellationMemoryManager(original, duringCopy.Cancel);
        Assert.Throws<OperationCanceledException>(() => new QdPropertyValueReader().Read(memory.Memory,
            Resource, Revision, new DecodedExtent(Resource, 0, original.Length), [], cancellationToken: duringCopy.Token));

        using var duringSourceCopy = new CancellationTokenSource();
        var guardedSources = new GuardedSources(3, access =>
        {
            if (access == 4) duringSourceCopy.Cancel();
            return new("generated/volume.bin", 0, original.Length);
        });
        Assert.Throws<OperationCanceledException>(() => new QdPropertyValueReader().Read(original,
            Resource, Revision, new DecodedExtent(Resource, 0, original.Length), guardedSources,
            cancellationToken: duringSourceCopy.Token));
        Assert.Equal(4, guardedSources.Accesses);
    }

    [Fact]
    public void JsonOutputOmitsOwnedRawPayloadCopies()
    {
        var document = Assert.IsType<QdPropertyValueDocument>(Read(String(10, "word"u8.ToArray())).Value);
        var json = JsonSerializer.Serialize(document);
        Assert.DoesNotContain("RawBytes", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RawValueBytes", json, StringComparison.Ordinal);
    }

    private static DecodeResult<QdPropertyValueDocument> Read(byte[] bytes,
        QdPropertyValueReadOptions? options = null, long offset = 0,
        IReadOnlyList<SourceExtent>? sources = null, CancellationToken token = default)
    {
        var identity = Resource;
        return new QdPropertyValueReader().Read(bytes, identity, Revision,
            new DecodedExtent(identity, offset, bytes.Length), sources ?? [], options, token);
    }

    private static byte[] Boolean(uint hash, byte value) => Scalar(0x80, hash, [value]);
    private static byte[] Type7(uint hash) => Scalar(0x87, hash, []);

    private static byte[] String(uint hash, byte[] bytes)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((byte)0x83); writer.Write(hash); writer.Write((uint)bytes.Length); writer.Write(bytes);
        return stream.ToArray();
    }

    private static byte[] Packed(byte tag, uint hash, uint count, byte[] contents)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(tag); writer.Write(hash); writer.Write(count); writer.Write(contents);
        return stream.ToArray();
    }

    private static byte[] Scalar(byte tag, uint hash, byte[] contents)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(tag); writer.Write(hash); writer.Write(contents);
        return stream.ToArray();
    }

    private static byte[] Container(byte tag, uint hash, IReadOnlyList<byte[]> children)
    {
        var width = (tag & 0x40) != 0 ? 4 : 3;
        var headerLength = 7 + children.Count * width;
        var offsets = new uint[children.Count];
        var cursor = headerLength;
        for (var i = 0; i < children.Count; i++) { offsets[i] = checked((uint)cursor); cursor += children[i].Length; }
        return ContainerWithOffsets(tag, hash, offsets, [.. children.SelectMany(child => child)]);
    }

    private static byte[] ContainerWithOffsets(byte tag, uint hash, IReadOnlyList<uint> offsets, byte[] body)
    {
        var width = (tag & 0x40) != 0 ? 4 : 3;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(tag); writer.Write(hash); writer.Write(checked((ushort)offsets.Count));
        foreach (var offset in offsets)
        {
            if (width == 4) writer.Write(offset);
            else { writer.Write((byte)(offset >> 16)); writer.Write((byte)(offset >> 8)); writer.Write((byte)offset); }
        }
        writer.Write(body);
        return stream.ToArray();
    }

    private sealed class GuardedSources(int count, Func<int, SourceExtent> read) : IReadOnlyList<SourceExtent>
    {
        public int Count => count;
        public int Accesses { get; private set; }
        public SourceExtent this[int index] { get { Accesses++; return read(Accesses); } }
        public IEnumerator<SourceExtent> GetEnumerator() { for (var i = 0; i < count; i++) yield return this[i]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CancellationMemoryManager(byte[] bytes, Action cancel) : MemoryManager<byte>
    {
        public override Span<byte> GetSpan() { cancel(); return bytes; }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
