using System.Text;
using Deviant.Assets;
using Deviant.Formats.Animation;
using Xunit;

namespace Deviant.Animation.Tests;

public sealed class AnimationStreamDescriptorTests
{
    [Fact]
    public void SizesTheExternalStreamFromInspectedRegions()
    {
        var result = new NativeAnimationDataReader().Decode(Frame(), new("index", "revision"),
            [new("source.dat", 0, 1, new string('0', 64))]);
        var descriptor = AnimationVersion14Streams.ReadDescriptor(result.Value!);
        Assert.Equal(new byte[] { 3 }, descriptor.PrimaryFlags.ToArray());
        Assert.Empty(descriptor.SecondaryFlags.ToArray());
        Assert.Equal(48, descriptor.ConstantVectors.Length);
        Assert.Equal(1024, descriptor.CoefficientLow.Length);
        Assert.Equal(1024, descriptor.CoefficientHigh.Length);
        Assert.Equal(20ul, descriptor.Sizing.SizeA);
        Assert.Equal(7ul, descriptor.Sizing.SizeB);
        Assert.Equal(4ul, descriptor.Sizing.SizeC);
        Assert.Equal(4ul, descriptor.Sizing.WindowCount);
        Assert.Equal(138ul, descriptor.Sizing.LogicalByteLength);
        Assert.Equal(256ul, descriptor.Sizing.AlignedSourceByteLength);
    }

    [Fact]
    public void RejectsIncompleteTablesAndUnsupportedConditionalState()
    {
        var revision = new SourceRevision("index", "revision");
        var extents = new SourceExtent[] { new("source.dat", 0, 1, new string('0', 64)) };
        var reader = new NativeAnimationDataReader();
        var data = reader.Decode(Frame(state: 1), revision, extents).Value!;
        Assert.Throws<InvalidDataException>(() => AnimationVersion14Streams.ReadDescriptor(data));
        data = reader.Decode(Frame(), revision, extents).Value!;
        var mismatched = data with { Version14Layout = data.Version14Layout! with { PrimaryCount20 = 2 } };
        Assert.Throws<InvalidDataException>(() => AnimationVersion14Streams.ReadDescriptor(mismatched));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => AnimationVersion14Streams.ReadDescriptor(data, cancel.Token));
    }

    private static NativeObjectFrame Frame(byte state = 0)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, Encoding.ASCII, true);
        writer.Write(3u); writer.Write("COM_CONT"u8); writer.Write(6u); writer.Write(0u);
        writer.Write("LOADCONT"u8); writer.Write(2u); writer.Write("ANIMDATA"u8); writer.Write(14u);
        writer.Write(2u); writer.Write(1u); writer.Write(1u); writer.Write(0x242u);
        writer.Write(10u); writer.Write(1u); writer.Write(.25f);
        writer.Write(17u); writer.Write((ushort)4); writer.Write((ushort)0);
        writer.Write((byte)3); writer.Write(3u); writer.Write(new byte[48]);
        writer.Write(3u); writer.Write(state); writer.Write(new byte[2048]); writer.Write(0u);
        var payload = bytes.ToArray(); var container = new ResourceIdentity("index", 7);
        return new(new("index", 7, "dc-object/0/2033/1"), 0, 2033, 1,
            new(container, 0, 8), new(container, 8, 13), new(container, 21, payload.Length), [], payload, 0, 1, 0, 3);
    }
}
