using System.Buffers.Binary;
using System.Collections;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class PhysicsEnvelopeBoundaryTests
{
    private static readonly ResourceIdentity Container = new("generated-index", 7);
    private static readonly SourceRevision Revision = new("generated-index", "generated-source");
    private static readonly SourceExtent Extent = new("generated.dat", 0, 256);

    [Theory]
    [InlineData("")]
    [InlineData("other-member")]
    [InlineData("dc-object/1/2201/37")]
    [InlineData("dc-object/0/2201/38")]
    [InlineData("dc-object/0/2200/37")]
    [InlineData("dc-object/00/2201/37")]
    [InlineData("dc-object/0/2201/37/extra")]
    public void RejectsMemberAliasesThatDoNotMatchTheFramedObject(string member)
    {
        var frame = Frame();
        var result = new PhysicsEnvelopeReader().Read(frame with { Identity = frame.Identity with { MemberPath = member } }, Revision, [Extent]);
        Assert.Null(result.Value);
        Assert.Contains("PhysicsEnvelopeSourceMismatch", result.Diagnostics);
    }

    [Fact]
    public void RejectsNegativeFrameOrdinalAndDirectoryIndexEvenWhenOwnerMatches()
    {
        var frame = Frame(); var negative = Container with { Ordinal = -1 };
        var ordinal = new PhysicsEnvelopeReader().Read(frame with
            { Identity = frame.Identity with { Ordinal = -1 }, PayloadExtent = frame.PayloadExtent with { Container = negative } }, Revision, [Extent]);
        Assert.Contains("PhysicsEnvelopeSourceMismatch", ordinal.Diagnostics);
        var index = new PhysicsEnvelopeReader().Read(frame with
            { DirectoryIndex = -1, Identity = frame.Identity with { MemberPath = "dc-object/-1/2201/37" } }, Revision, [Extent]);
        Assert.Contains("PhysicsEnvelopeSourceMismatch", index.Diagnostics);
    }

    [Fact]
    public void RejectsOversizedMetadataCollectionsBeforeAccessingElements()
    {
        var extents = new GuardedList<SourceExtent>(int.MaxValue, _ => throw new InvalidOperationException("No metadata element should be read."));
        var steps = new GuardedList<DecodeStep>(int.MaxValue, _ => throw new InvalidOperationException("No step should be read."));
        var reader = new PhysicsEnvelopeReader();
        Assert.Null(reader.Read(Frame(), Revision, extents).Value);
        Assert.Equal(0, extents.Accesses);
        Assert.Null(reader.Read(Frame(), Revision, [Extent], steps).Value);
        Assert.Equal(0, steps.Accesses);
    }

    [Fact]
    public void MetadataCopiesObserveCancellationBetweenElements()
    {
        using var physicalStop = new CancellationTokenSource();
        var extents = new GuardedList<SourceExtent>(3, _ => { physicalStop.Cancel(); return Extent; });
        Assert.Throws<OperationCanceledException>(() => new PhysicsEnvelopeReader().Read(Frame(), Revision, extents, token: physicalStop.Token));
        Assert.Equal(1, extents.Accesses);
        using var stepStop = new CancellationTokenSource();
        var steps = new GuardedList<DecodeStep>(3, _ => { stepStop.Cancel(); return new("generated", "1", "sample"); });
        Assert.Throws<OperationCanceledException>(() => new PhysicsEnvelopeReader().Read(Frame(), Revision, [Extent], steps, token: stepStop.Token));
        Assert.Equal(1, steps.Accesses);
    }

    [Fact]
    public void RejectsExcessiveSourceNameBeforeAllocatingSplitCollections()
    {
        var source = new string('a', 65_536);
        var result = new PhysicsEnvelopeReader().Read(Frame(), Revision, [Extent with { RelativeSource = source }]);
        Assert.Null(result.Value);
        Assert.Contains("PhysicsEnvelopePhysicalExtentInvalid", result.Diagnostics);
    }

    [Fact]
    public void WrapperBytesRemainAProducerResponsibilityRatherThanEmbeddedPayload()
    {
        var frame = Frame(); var changedWrapper = Enumerable.Repeat((byte)0xa5, frame.RawHeader.Length).ToArray();
        var result = new PhysicsEnvelopeReader().Read(frame with { RawHeader = changedWrapper }, Revision, [Extent]);
        var value = Assert.IsType<PhysicsEnvelopeDocument>(result.Value);
        Assert.Equal(frame.RawPayload, value.RawPayload);
        Assert.Equal(new byte[] { 1, 2, 3 }, value.EmbeddedBytes.ToArray());
        Assert.Equal(frame.PayloadExtent.Offset + 20, Assert.Single(value.UnknownRanges).Offset);
    }

    [Fact]
    public void NullSourceMetadataFailsWithoutLeakingInvalidExtents()
    {
        var result = new PhysicsEnvelopeReader().Read(Frame(), Revision, [null!]);
        Assert.Null(result.Value); Assert.Empty(result.Extents);
        Assert.Contains("PhysicsEnvelopePhysicalExtentInvalid", result.Diagnostics);
    }

    [Fact]
    public void NullPrecedingStepFailsWithoutRetainingNullSteps()
    {
        var result = new PhysicsEnvelopeReader().Read(Frame(), Revision, [Extent], [null!]);
        Assert.Null(result.Value);
        Assert.DoesNotContain(result.Steps, step => step is null);
        Assert.Contains("PhysicsEnvelopeStepInvalid", result.Diagnostics);
    }

    private static NativeObjectFrame Frame()
    {
        var bytes = new byte[23]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        "PHYSYS__"u8.CopyTo(bytes.AsSpan(4)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 3); bytes[20] = 1; bytes[21] = 2; bytes[22] = 3;
        var header = new byte[13]; BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
        return new(new(Container.IndexContentId, Container.Ordinal, "dc-object/0/2201/37"), 0, 2201, 37,
            new(Container, 24, 8), new(Container, 128, 13), new(Container, 141, bytes.Length), header, bytes, 0, 0, 0, 1);
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
