using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class PhysicsEnvelopeReaderTests
{
    private static readonly ResourceIdentity Container = new("generated-index", 9);
    private static readonly SourceRevision Revision = new("generated-index", "generated-revision");
    private static readonly SourceExtent[] Physical = [new("resources.dat", 24, 1024)];

    [Fact]
    public void RetainsOpaqueEmbeddedBytesHeaderProjectionsAndSourceExtents()
    {
        var embedded = new byte[] { 0xc0, 0, 0, 17, 0, 255, 0, 127, 2, 3, 4, 5, 6 };
        var frame = Frame(embedded);
        var preceding = new DecodeStep("ContainerReader", "generated", "decoded", "source-hash");
        var result = new PhysicsEnvelopeReader().Read(frame, Revision, Physical, [preceding]);
        var value = Assert.IsType<PhysicsEnvelopeDocument>(result.Value);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(frame.Identity, value.Identity);
        Assert.Equal(Revision, value.Revision);
        Assert.Equal(embedded, value.EmbeddedBytes.ToArray());
        Assert.Equal(frame.RawPayload.ToArray(), value.RawPayload.ToArray());
        Assert.Equal(Hash(embedded), value.EmbeddedSha256);
        Assert.Equal(Hash(frame.RawPayload.Span), result.Steps[1].PayloadSha256);
        Assert.Equal(preceding, result.Steps[0]);
        Assert.Equal(Physical, result.Extents);
        Assert.Equal(frame.PayloadExtent.Offset + 20, value.EmbeddedExtent.Offset);
        Assert.Equal(13, value.EmbeddedExtent.Length);
        Assert.Equal(value.EmbeddedExtent, Assert.Single(value.UnknownRanges));
        Assert.Equal(0xc0000011u, value.EmbeddedHeader!.RawBigEndianWord);
        Assert.Equal(17u, value.EmbeddedHeader.Lower30Bits);
        Assert.Equal(3, value.EmbeddedHeader.High2Bits);
        Assert.Equal(new byte[] { 0, 255, 0, 127 }, value.EmbeddedHeader.RawTagBytes);
        Assert.Equal("Opaque", result.UnknownFields["EmbeddedObjects"]);
        Assert.Empty(result.UnresolvedReferences);
        Assert.True(MemoryMarshal.TryGetArray(value.EmbeddedBytes, out var slice));
        Assert.True(MemoryMarshal.TryGetArray(frame.RawPayload, out var payload));
        Assert.Same(payload.Array, slice.Array);
        Assert.Equal(payload.Offset + 20, slice.Offset);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(7)]
    public void AcceptsOpaqueSegmentsWithoutInventingEmbeddedHeaders(int length)
    {
        var frame = Frame(new byte[length]);
        var value = Assert.IsType<PhysicsEnvelopeDocument>(Read(frame).Value);
        Assert.Null(value.EmbeddedHeader);
        Assert.Equal((uint)length, value.EmbeddedLength);
        Assert.Equal(Hash(new byte[length]), value.EmbeddedSha256);
        Assert.Equal(length == 0 ? 0 : 1, value.UnknownRanges.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(19)]
    public void RejectsTruncatedOuterHeader(int length)
    {
        var original = Frame(new byte[8]);
        var truncated = original with { RawPayload = original.RawPayload[..length], PayloadExtent = original.PayloadExtent with { Length = length } };
        AssertFailure(truncated, "PhysicsEnvelopeClassUnsupported");
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(7u)]
    [InlineData(9u)]
    public void RejectsLengthsThatDoNotEndAtTheNativeFrameBoundary(uint declaredLength)
    {
        var original = Frame(new byte[8]);
        var changed = original.RawPayload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(16), declaredLength);
        AssertFailure(original with { RawPayload = changed }, "PhysicsEnvelopeLengthMismatch");
    }

    [Fact]
    public void EnforcesByteBudgetBeforeHashingOrCopyingEmbeddedData()
    {
        var frame = Frame(new byte[16]);
        var limited = new PhysicsEnvelopeReader().Read(frame, Revision, Physical, limits: new(15));
        Assert.Null(limited.Value);
        Assert.Contains("PhysicsEnvelopeByteLimit", limited.Diagnostics);
        var changed = frame.RawPayload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(16), uint.MaxValue);
        AssertFailure(frame with { RawPayload = changed }, "PhysicsEnvelopeByteLimit");
        Assert.Throws<ArgumentOutOfRangeException>(() => new PhysicsEnvelopeReader().Read(frame, Revision, Physical, limits: new(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PhysicsEnvelopeReader().Read(frame, Revision, Physical,
            limits: new(PhysicsEnvelopeReader.MaximumSupportedEmbeddedBytes + 1)));
    }

    [Fact]
    public void RejectsWrongNativeTypeClassCountTagAndVersion()
    {
        var frame = Frame(new byte[8]);
        AssertFailure(frame with { NativeType = 2139, Identity = frame.Identity with { MemberPath = "dc-object/0/2139/37" } },
            "PhysicsEnvelopeTypeUnsupported");
        AssertFailure(frame with { ClassCount = 2 }, "PhysicsEnvelopeClassUnsupported");
        var changed = frame.RawPayload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(changed, 2);
        AssertFailure(frame with { RawPayload = changed }, "PhysicsEnvelopeClassUnsupported");
        changed = frame.RawPayload.ToArray(); changed[4] = (byte)'X';
        AssertFailure(frame with { RawPayload = changed }, "PhysicsEnvelopeClassUnsupported");
        changed = frame.RawPayload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(12), 6);
        AssertFailure(frame with { RawPayload = changed }, "PhysicsEnvelopeVersionUnsupported");
    }

    [Fact]
    public void RejectsCrossRevisionCrossOwnerAndInvalidDecodedExtents()
    {
        var frame = Frame(new byte[8]);
        var reader = new PhysicsEnvelopeReader();
        Assert.Contains("PhysicsEnvelopeSourceMismatch", reader.Read(frame, new("other-index", "stamp"), Physical).Diagnostics);
        AssertFailure(frame with { Identity = frame.Identity with { MemberPath = null } }, "PhysicsEnvelopeSourceMismatch");
        AssertFailure(frame with { PayloadExtent = frame.PayloadExtent with { Container = Container with { Ordinal = 10 } } }, "PhysicsEnvelopeSourceMismatch");
        AssertFailure(frame with { PayloadExtent = frame.PayloadExtent with { Container = Container with { MemberPath = "other" } } }, "PhysicsEnvelopeSourceMismatch");
        AssertFailure(frame with { PayloadExtent = frame.PayloadExtent with { Offset = -1 } }, "PhysicsEnvelopeSourceMismatch");
        AssertFailure(frame with { PayloadExtent = frame.PayloadExtent with { Offset = long.MaxValue } }, "PhysicsEnvelopeSourceMismatch");
        AssertFailure(frame with { PayloadExtent = frame.PayloadExtent with { Length = 1 } }, "PhysicsEnvelopeSourceMismatch");
    }

    [Theory]
    [InlineData("../outside.dat")]
    [InlineData("C:/outside.dat")]
    [InlineData("/outside.dat")]
    [InlineData("folder//file.dat")]
    public void RejectsInvalidPhysicalSourceNames(string name)
    {
        var result = new PhysicsEnvelopeReader().Read(Frame(new byte[8]), Revision, [new(name, 0, 8)]);
        Assert.Null(result.Value);
        Assert.Contains("PhysicsEnvelopePhysicalExtentInvalid", result.Diagnostics);
    }

    [Fact]
    public void RejectsPhysicalExtentOverflowAndPreservesCancellation()
    {
        var frame = Frame(new byte[8]);
        var reader = new PhysicsEnvelopeReader();
        Assert.Contains("PhysicsEnvelopePhysicalExtentInvalid", reader.Read(frame, Revision,
            [new("resources.dat", long.MaxValue, 8)]).Diagnostics);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.Read(frame, Revision, Physical, token: cancel.Token));
    }

    private static DecodeResult<PhysicsEnvelopeDocument> Read(NativeObjectFrame frame) =>
        new PhysicsEnvelopeReader().Read(frame, Revision, Physical);

    private static void AssertFailure(NativeObjectFrame frame, string diagnostic)
    {
        var result = Read(frame); Assert.Null(result.Value); Assert.Contains(diagnostic, result.Diagnostics);
    }

    private static NativeObjectFrame Frame(ReadOnlySpan<byte> embedded)
    {
        var bytes = new byte[20 + embedded.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        "PHYSYS__"u8.CopyTo(bytes.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)embedded.Length);
        embedded.CopyTo(bytes.AsSpan(20));
        return new(new(Container.IndexContentId, Container.Ordinal, "dc-object/0/2201/37"), 0, 2201, 37,
            new(Container, 24, 8), new(Container, 128, 13), new(Container, 141, bytes.Length), new byte[13], bytes, 0, 0, 0, 1);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
