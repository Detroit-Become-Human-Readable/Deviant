using System.Buffers.Binary;
using System.Numerics;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class NativeNodeArrayReaderTests
{
    [Fact]
    public void ReadsInheritedFieldsUsingOriginalIdentityAndExactExtents()
    {
        var frame = Frame();
        var result = Read(frame);
        var document = Assert.IsType<NativeNodeArrayDocument>(result.Value);
        var node = document.InheritedNode;
        Assert.Equal(frame.Identity, document.Identity);
        Assert.Equal(frame.Identity, node.Identity);
        Assert.Equal(frame.PayloadExtent, node.PayloadExtent);
        Assert.Equal(4u, document.Version); Assert.Equal(14u, document.BodyVersion);
        Assert.Equal(8u, node.Flags);
        Assert.Equal(new Vector3(10, 20, 30), node.Pivot);
        Assert.Equal(Enumerable.Range(1, 16).Reverse().Select(x => (float)x), node.StoredLocalTransform.Elements);
        Assert.Equal(Enumerable.Range(101, 16).Reverse().Select(x => (float)x), node.StoredWorldTransform.Elements);
        Assert.Equal(frame.Identity, node.Parent!.Owner);
        Assert.Equal(2002u, node.Parent.ResourceType); Assert.Equal("42", node.Parent.Id);
        Assert.Equal("43", node.FirstChild!.Id); Assert.Equal("44", node.NextSibling!.Id);
        Assert.Equal(-1, node.ParentAttachmentIndex);
        Assert.Equal(0x12345678u, node.Word172); Assert.Equal(0x87654321u, node.Word176);
        Assert.Equal(new(frame.PayloadExtent.Container, frame.PayloadExtent.Offset, 196), document.InheritedPrefixExtent);
        Assert.Equal(new(frame.PayloadExtent.Container, frame.PayloadExtent.Offset + 196, 12), document.ClassHeaderExtent);
        Assert.Equal(frame.PayloadExtent.Offset + 208, document.BodyExtent.Offset);
        Assert.Equal(frame.RawPayload.Length - 208, document.BodyExtent.Length);
        Assert.Equal(frame.PayloadExtent.Offset + 160, node.FieldExtents["Parent"].Offset);
        Assert.Equal(frame.RawPayload.ToArray(), document.RawPayload.ToArray());
        Assert.Equal(document.RawPayload.ToArray(), node.RawPayload.ToArray());
        Assert.Equal(frame.RawPayload[208..].ToArray(), document.EncodedBody.ToArray());
        Assert.Equal(frame.RawPayload.Length, document.RetainedPayloadBytes);
        Assert.Equal(3, result.UnresolvedReferences.Count);
        Assert.NotNull(Assert.Single(result.Steps).PayloadSha256);
    }

    [Fact]
    public void OwnsPayloadCopyIncludingOpaqueBody()
    {
        var frame = Frame(); var input = frame.RawPayload.ToArray();
        var document = Assert.IsType<NativeNodeArrayDocument>(Read(frame with { RawPayload = input }).Value);
        var before = document.RawPayload.ToArray();
        Array.Fill(input, (byte)0);
        Assert.Equal(before, document.RawPayload.ToArray());
        Assert.Equal(before, document.InheritedNode.RawPayload.ToArray());
    }

    [Fact]
    public void OrdinaryNodeReaderStillRejectsDerivedFrameAndExtendedOrdinaryEnvelope()
    {
        var frame = Frame();
        Assert.Contains("NodeTypeUnsupported", new NodeRecordReader().Read(frame,
            SceneFixture.Revision, SceneFixture.Physical).Diagnostics);
        var ordinary = SceneFixture.Frame(1, 7, Matrix4x4.Identity);
        var extended = ordinary.RawPayload.ToArray().Concat(new byte[4]).ToArray();
        Assert.Contains("NodeClassEnvelopeUnsupported", new NodeRecordReader().Read(ordinary with
        {
            RawPayload = extended, PayloadExtent = ordinary.PayloadExtent with { Length = extended.Length }
        }, SceneFixture.Revision, SceneFixture.Physical).Diagnostics);
        Assert.NotNull(new NodeRecordReader().Read(ordinary, SceneFixture.Revision, SceneFixture.Physical).Value);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)] [InlineData(15)] [InlineData(195)]
    [InlineData(196)] [InlineData(207)] [InlineData(208)] [InlineData(211)]
    public void RejectsTruncationBeforeEmbeddedVersion(int length)
    {
        var original = Frame(); var frame = original with
        {
            RawPayload = original.RawPayload[..length], PayloadExtent = original.PayloadExtent with { Length = length }
        };
        Assert.Contains("NodeArrayFrameTruncated", Read(frame).Diagnostics);
        Assert.Null(Read(frame).Value);
    }

    [Theory]
    [InlineData(0, 1, "NodeArrayClassEnvelopeUnsupported")]
    [InlineData(0, 3, "NodeArrayClassEnvelopeUnsupported")]
    [InlineData(12, 4, "NodeArrayNodeVersionUnsupported")]
    [InlineData(204, 3, "NodeArrayVersionUnsupported")]
    [InlineData(204, 5, "NodeArrayVersionUnsupported")]
    public void RejectsUnsupportedEnvelopeWords(int offset, uint value, string diagnostic)
    {
        var frame = Frame(); var bytes = frame.RawPayload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        Assert.Contains(diagnostic, Read(frame with { RawPayload = bytes }).Diagnostics);
    }

    [Theory]
    [InlineData(4, "NodeArrayClassEnvelopeUnsupported")]
    [InlineData(196, "NodeArrayClassHeaderUnsupported")]
    public void RejectsTagChanges(int offset, string diagnostic)
    {
        var frame = Frame(); var bytes = frame.RawPayload.ToArray(); bytes[offset] ^= 1;
        Assert.Contains(diagnostic, Read(frame with { RawPayload = bytes }).Diagnostics);
    }

    [Fact]
    public void RejectsClassMetadataAndForeignType()
    {
        var frame = Frame();
        Assert.Contains("NodeArrayClassEnvelopeUnsupported", Read(frame with { ClassCount = 1 }).Diagnostics);
        Assert.Contains("NodeArrayTypeUnsupported", Read(frame with
        {
            NativeType = 2009, Identity = frame.Identity with { MemberPath = "dc-object/0/2009/7" }
        }).Diagnostics);
    }

    [Fact]
    public void RejectsForeignAndInvalidExtents()
    {
        var frame = Frame();
        Assert.Contains("NodeArraySourceIdentityMismatch", new NativeNodeArrayReader().Read(frame,
            new("other-index", "stamp"), SceneFixture.Physical).Diagnostics);
        Assert.Contains("NodeArraySourceIdentityMismatch", Read(frame with
        {
            PayloadExtent = frame.PayloadExtent with { Container = new("generated-index", 99) }
        }).Diagnostics);
        Assert.Contains("NodeArraySourceIdentityMismatch", Read(frame with
        {
            PayloadExtent = frame.PayloadExtent with { Length = frame.RawPayload.Length - 1 }
        }).Diagnostics);
        Assert.Contains("NodeArraySourceIdentityMismatch", Read(frame with
        {
            PayloadExtent = frame.PayloadExtent with { Offset = long.MaxValue - 2 }
        }).Diagnostics);
        Assert.Contains("NodeArrayPhysicalExtentInvalid", new NativeNodeArrayReader().Read(frame,
            SceneFixture.Revision, [new("../outside.dat", 0, 1)]).Diagnostics);
    }

    [Fact]
    public void RetainsUnknownBodyVersionAndNonFiniteInheritedValues()
    {
        var frame = Frame(); var bytes = frame.RawPayload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(208), 99);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(32), float.NaN);
        var document = Assert.IsType<NativeNodeArrayDocument>(Read(frame with { RawPayload = bytes }).Value);
        Assert.Equal(99u, document.BodyVersion);
        Assert.True(float.IsNaN(document.InheritedNode.StoredLocalTransform.Elements[15]));
        Assert.False(SceneTransformMath.TryGetLocal(document.InheritedNode, out _));
    }

    [Fact]
    public void CancellationAndPayloadLimitsDoNotAllocateAnAdmittedDocument()
    {
        var frame = Frame(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new NativeNodeArrayReader().Read(frame,
            SceneFixture.Revision, SceneFixture.Physical, token: cancellation.Token));
        var limited = new NativeNodeArrayReader().Read(frame, SceneFixture.Revision, SceneFixture.Physical,
            limits: new(212));
        Assert.Null(limited.Value); Assert.Contains("NodeArrayPayloadLimit", limited.Diagnostics);
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeNodeArrayReader().Read(frame,
            SceneFixture.Revision, SceneFixture.Physical, limits: new(1)));
    }

    [Fact]
    public void RejectsNoncanonicalIdentityNegativeCoordinatesAndNestedOwner()
    {
        var frame = Frame();
        Assert.Contains("NodeArraySourceIdentityMismatch", Read(frame with
        {
            Identity = frame.Identity with { MemberPath = "dc-object/00/2138/7" }
        }).Diagnostics);
        Assert.Contains("NodeArraySourceIdentityMismatch", Read(frame with
        {
            Identity = frame.Identity with { Ordinal = -1 },
            PayloadExtent = frame.PayloadExtent with { Container = SceneFixture.Container with { Ordinal = -1 } }
        }).Diagnostics);
        Assert.Contains("NodeArraySourceIdentityMismatch", Read(frame with
        {
            DirectoryIndex = -1, Identity = frame.Identity with { MemberPath = "dc-object/-1/2138/7" }
        }).Diagnostics);
        Assert.Contains("NodeArraySourceIdentityMismatch", Read(frame with
        {
            PayloadExtent = frame.PayloadExtent with { Container = SceneFixture.Container with { MemberPath = "nested" } }
        }).Diagnostics);
        var emptyIdentity = new ResourceIdentity("", SceneFixture.Container.Ordinal, frame.Identity.MemberPath);
        Assert.Contains("NodeArraySourceIdentityMismatch", new NativeNodeArrayReader().Read(frame with
        {
            Identity = emptyIdentity, PayloadExtent = frame.PayloadExtent with { Container = emptyIdentity with { MemberPath = null } }
        }, new("", "generated-revision"), SceneFixture.Physical).Diagnostics);
    }

    [Fact]
    public void CapsProvenanceBeforeAccessAndRejectsNullEntriesAndOversizedNames()
    {
        var frame = Frame(); var reader = new NativeNodeArrayReader();
        Assert.Contains("NodeArrayMetadataLimit", reader.Read(frame, SceneFixture.Revision,
            new CountOnlyList<SourceExtent>(2), limits: new(MaximumPhysicalExtents: 1)).Diagnostics);
        Assert.Contains("NodeArrayMetadataLimit", reader.Read(frame, SceneFixture.Revision,
            SceneFixture.Physical, new CountOnlyList<DecodeStep>(2), new(MaximumPrecedingSteps: 1)).Diagnostics);
        Assert.Contains("NodeArrayPhysicalExtentInvalid", reader.Read(frame, SceneFixture.Revision, [null!]).Diagnostics);
        Assert.Contains("NodeArrayStepInvalid", reader.Read(frame, SceneFixture.Revision, SceneFixture.Physical, [null!]).Diagnostics);
        Assert.Contains("NodeArrayPhysicalExtentInvalid", reader.Read(frame, SceneFixture.Revision,
            [new(new string('a', NativeNodeArrayReader.MaximumSourceNameCharacters + 1), 0, 1)]).Diagnostics);
        Assert.Contains("NodeArrayPhysicalExtentInvalid", reader.Read(frame, SceneFixture.Revision,
            [new(null!, 0, 1)]).Diagnostics);
    }

    [Fact]
    public void ChecksCancellationDuringProvenanceCopies()
    {
        var frame = Frame(); using var cancellation = new CancellationTokenSource();
        var extents = new CancellingList<SourceExtent>(SceneFixture.Physical[0], cancellation);
        Assert.Throws<OperationCanceledException>(() => new NativeNodeArrayReader().Read(frame,
            SceneFixture.Revision, extents, token: cancellation.Token));
        using var stepCancellation = new CancellationTokenSource();
        var steps = new CancellingList<DecodeStep>(new("generated", "1", "fixture"), stepCancellation);
        Assert.Throws<OperationCanceledException>(() => new NativeNodeArrayReader().Read(frame,
            SceneFixture.Revision, SceneFixture.Physical, steps, token: stepCancellation.Token));
    }

    private static DecodeResult<NativeNodeArrayDocument> Read(NativeObjectFrame frame) =>
        new NativeNodeArrayReader().Read(frame, SceneFixture.Revision, SceneFixture.Physical);

    private static NativeObjectFrame Frame()
    {
        var bytes = new byte[243];
        Put(0, 2); "NODE    "u8.CopyTo(bytes.AsSpan(4)); Put(12, 5); Put(16, 8);
        Single(20, 10); Single(24, 20); Single(28, 30);
        for (var i = 0; i < 16; i++) { Single(32 + i * 4, i + 1); Single(96 + i * 4, i + 101); }
        Put(160, 2002); Put(164, 42); Put(168, uint.MaxValue);
        Put(172, 2002); Put(176, 43); Put(180, 2002); Put(184, 44);
        Put(188, 0x12345678); Put(192, 0x87654321);
        "NODEAR  "u8.CopyTo(bytes.AsSpan(196)); Put(204, 4); Put(208, 14);
        for (var i = 212; i < bytes.Length; i++) bytes[i] = unchecked((byte)i);
        var identity = new ResourceIdentity(SceneFixture.Container.IndexContentId, SceneFixture.Container.Ordinal,
            "dc-object/0/2138/7");
        return new(identity, 0, 2138, 7, new(SceneFixture.Container, 24, 8),
            new(SceneFixture.Container, 512, 13), new(SceneFixture.Container, 525, bytes.Length),
            new byte[13], bytes, 0, 0, 0, 2);
        void Put(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
        void Single(int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
    }

    private sealed class CountOnlyList<T>(int count) : IReadOnlyList<T>
    {
        public int Count => count;
        public T this[int index] => throw new InvalidOperationException("Over-limit metadata was accessed.");
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Metadata was enumerated.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CancellingList<T>(T item, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Count => 1;
        public T this[int index] { get { cancellation.Cancel(); return item; } }
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Metadata was enumerated.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
