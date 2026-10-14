using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Formats.Animation;
using Xunit;

namespace Deviant.Animation.Tests;

public sealed class NativeAnimationDataTests
{
    private static readonly ResourceIdentity Container = new("index", 7);
    private static readonly SourceRevision Revision = new("index", "revision");

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void NativeEnvelopeSharesTablesAndTimingWithoutSyntheticLengths(int references)
    {
        var frame = Frame(references);
        var result = Read(frame);
        var value = Assert.IsType<AnimationDataDocument>(result.Value);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(44 + references * 9, value.Data.BodyOffset);
        Assert.Equal(14u, value.Data.Version);
        Assert.Equal(3, value.Sections.Count);
        Assert.Equal(4, value.Version14Layout!.Timing!.Count);
        Assert.Equal(.25f, value.Version14Layout.Timing.Period);
        Assert.Equal(1f, value.Version14Layout.Timing.Duration);
        Assert.Equal(4u, value.Version14Layout.PrimaryCount20);
        var original = value.EncodedResource.ToArray();
        Assert.Equal(original, value.EncodedResource.ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant(), result.Steps.Single().PayloadSha256);
    }

    [Fact]
    public void NativeAndFramedEnvelopesRemainDistinct()
    {
        var frame = Frame(1);
        Assert.Null(new AnimationDataReader().Decode(frame.RawPayload, frame.Identity, Revision,
            new("source.dat", 0, frame.RawPayload.Length)).Value);
        Assert.NotNull(Read(frame).Value);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(29)]
    [InlineData(41)]
    public void RejectsTruncatedClassHeaders(int length)
    {
        var frame = Frame(1);
        var bytes = frame.RawPayload[..length];
        frame = frame with { RawPayload = bytes, PayloadExtent = frame.PayloadExtent with { Length = length } };
        Assert.Null(Read(frame).Value);
    }

    [Fact]
    public void RejectsSourceMismatchAndHonorsCancellation()
    {
        var frame = Frame(0);
        Assert.Throws<ArgumentException>(() => Read(frame with { NativeType = 2009 }));
        Assert.Throws<ArgumentException>(() => Read(frame with { PayloadExtent = frame.PayloadExtent with { Container = new("other", 7) } }));
        Assert.Throws<ArgumentException>(() => new NativeAnimationDataReader().Decode(frame, Revision, []));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new NativeAnimationDataReader().Decode(frame, Revision,
            Sources(frame), token: cancellation.Token));
    }

    private static DecodeResult<AnimationDataDocument> Read(NativeObjectFrame frame) =>
        new NativeAnimationDataReader().Decode(frame, Revision, Sources(frame), AnimationReadProfile.Version14Tables);
    private static SourceExtent[] Sources(NativeObjectFrame frame) =>
        [new("source.dat", 0, frame.RawPayload.Length, Convert.ToHexString(SHA256.HashData(frame.RawPayload.Span)))];
    private static NativeObjectFrame Frame(int references)
    {
        var bytes = new byte[44 + references * 9 + 28];
        void Word(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        Word(0, 3); "COM_CONT"u8.CopyTo(bytes.AsSpan(4)); Word(12, 6); Word(16, (uint)references);
        var at = 20 + references * 9;
        "LOADCONT"u8.CopyTo(bytes.AsSpan(at)); Word(at + 8, 2); at += 12;
        "ANIMDATA"u8.CopyTo(bytes.AsSpan(at)); Word(at + 8, 14); at += 12;
        Word(at, 2); Word(at + 4, 1); Word(at + 8, 1); Word(at + 12, 0);
        Word(at + 16, 4); Word(at + 20, 4); Word(at + 24, (uint)BitConverter.SingleToInt32Bits(.25f));
        return new(new("index", 7, "dc-object/0/2033/1"), 0, 2033, 1,
            new(Container, 0, 8), new(Container, 8, 13), new(Container, 21, bytes.Length), [], bytes, 0, 1, 0, 3);
    }
}
