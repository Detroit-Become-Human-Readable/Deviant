using System.Buffers.Binary;
using System.Collections;
using System.Runtime.InteropServices;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class NativeNodeArrayBoundaryTests
{
    private static readonly ResourceIdentity Container = new("generated-index", 7);
    private static readonly SourceRevision Revision = new("generated-index", "generated-source");
    private static readonly SourceExtent Extent = new("generated.dat", 0, 4096);

    [Theory]
    [InlineData("")]
    [InlineData("other-member")]
    [InlineData("dc-object/1/2138/37")]
    [InlineData("dc-object/0/2002/37")]
    [InlineData("dc-object/0/2138/38")]
    public void RejectsIdentityAliasesRatherThanRelabelingDerivedNode(string member)
    {
        var frame = Frame();
        Assert.Null(Read(frame with { Identity = frame.Identity with { MemberPath = member } }).Value);
    }

    [Fact]
    public void RejectsNegativeIndicesAndNestedPayloadOwner()
    {
        var frame = Frame(); var negative = Container with { Ordinal = -1 };
        Assert.Null(Read(frame with { Identity = frame.Identity with { Ordinal = -1 },
            PayloadExtent = frame.PayloadExtent with { Container = negative } }).Value);
        Assert.Null(Read(frame with { DirectoryIndex = -1, Identity = frame.Identity with { MemberPath = "dc-object/-1/2138/37" } }).Value);
        Assert.Null(Read(frame with { PayloadExtent = frame.PayloadExtent with { Container = Container with { MemberPath = "nested" } } }).Value);
    }

    [Fact]
    public void MetadataLimitsRejectHugeCountsBeforeAnyElementIsAccessed()
    {
        var sources = new GuardedList<SourceExtent>(int.MaxValue, _ => throw new InvalidOperationException("Source should not be accessed."));
        var steps = new GuardedList<DecodeStep>(int.MaxValue, _ => throw new InvalidOperationException("Step should not be accessed."));
        Assert.Null(new NativeNodeArrayReader().Read(Frame(), Revision, sources).Value);
        Assert.Equal(0, sources.Accesses);
        Assert.Null(new NativeNodeArrayReader().Read(Frame(), Revision, [Extent], steps).Value);
        Assert.Equal(0, steps.Accesses);
    }

    [Fact]
    public void SourceAndStepCopiesStopOnCancellationFromAnIndexer()
    {
        using var sourceStop = new CancellationTokenSource();
        var sources = new GuardedList<SourceExtent>(3, _ => { sourceStop.Cancel(); return Extent; });
        Assert.Throws<OperationCanceledException>(() => new NativeNodeArrayReader().Read(Frame(), Revision, sources, token: sourceStop.Token));
        Assert.Equal(1, sources.Accesses);
        using var stepStop = new CancellationTokenSource();
        var steps = new GuardedList<DecodeStep>(3, _ => { stepStop.Cancel(); return new("generated", "1", "test"); });
        Assert.Throws<OperationCanceledException>(() => new NativeNodeArrayReader().Read(Frame(), Revision, [Extent], steps, token: stepStop.Token));
        Assert.Equal(1, steps.Accesses);
    }

    [Fact]
    public void NullAndOversizedSourceMetadataCannotEscapeThroughSuccessfulDocuments()
    {
        Assert.Null(new NativeNodeArrayReader().Read(Frame(), Revision, [null!]).Value);
        Assert.Null(new NativeNodeArrayReader().Read(Frame(), Revision, [Extent], [null!]).Value);
        Assert.Null(new NativeNodeArrayReader().Read(Frame(), Revision, [Extent with { RelativeSource = new string('a', 65_536) }]).Value);
    }

    [Fact]
    public void FullPayloadAndInheritedPrefixStayDistinctOnOneOwnedSnapshot()
    {
        var frame = Frame(); var original = frame.RawPayload;
        var document = Assert.IsType<NativeNodeArrayDocument>(Read(frame).Value);
        Assert.Equal(2138u, frame.NativeType);
        Assert.Equal(frame.Identity, document.InheritedNode.Identity);
        Assert.Equal(196, document.InheritedPrefixExtent.Length);
        Assert.Equal(frame.PayloadExtent, document.InheritedNode.PayloadExtent);
        Assert.Equal(frame.RawPayload.Length, document.InheritedNode.RawPayload.Length);
        Assert.True(MemoryMarshal.TryGetArray(original, out var input));
        Assert.True(MemoryMarshal.TryGetArray(document.RawPayload, out var owned));
        Assert.True(MemoryMarshal.TryGetArray(document.EncodedBody, out var body));
        Assert.NotSame(input.Array, owned.Array);
        Assert.Same(owned.Array, body.Array);
        Assert.Equal(owned.Offset + 208, body.Offset);
        Assert.Contains("NodeTypeUnsupported", new NodeRecordReader().Read(frame, Revision, [Extent]).Diagnostics);
    }

    private static DecodeResult<NativeNodeArrayDocument> Read(NativeObjectFrame frame) =>
        new NativeNodeArrayReader().Read(frame, Revision, [Extent]);
    private static NativeObjectFrame Frame()
    {
        var bytes = new byte[216]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);
        "NODE    "u8.CopyTo(bytes.AsSpan(4)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 5);
        "NODEAR  "u8.CopyTo(bytes.AsSpan(196)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(204), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(208), 14); bytes[212] = 9;
        var header = new byte[13]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
        return new(new(Container.IndexContentId, Container.Ordinal, "dc-object/0/2138/37"), 0, 2138, 37,
            new(Container, 24, 8), new(Container, 128, 13), new(Container, 141, bytes.Length), header, bytes, 0, 0, 0, 2);
    }
    private sealed class GuardedList<T>(int count, Func<int, T> read) : IReadOnlyList<T>
    {
        public int Count => count;
        public int Accesses { get; private set; }
        public T this[int index] { get { Accesses++; return read(index); } }
        public IEnumerator<T> GetEnumerator() { for (var index = 0; index < count; index++) yield return this[index]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
