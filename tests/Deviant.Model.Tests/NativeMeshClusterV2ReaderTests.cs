using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Formats.Models;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class NativeMeshClusterV2ReaderTests
{
    [Fact]
    public void JsonKeepsExtentAndScalarMetadataWithoutCopyingRawPayloads()
    {
        var wire = CreateSection(1, flag: 1, prefixLength: 0, suffix: []);
        Assert.True(new NativeMeshClusterV2Reader().TryRead(wire.Bytes, Source(wire.Bytes.Length), 0,
            out var result, out var diagnostic), diagnostic);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.True(json.RootElement.TryGetProperty("ScalarWordBits", out _));
        Assert.True(json.RootElement.TryGetProperty("Extent", out _));
        Assert.False(json.RootElement.TryGetProperty("RawScalarWords", out _));
        Assert.False(json.RootElement.TryGetProperty("RawByteValues", out _));
        Assert.False(json.RootElement.TryGetProperty("RawTupleValues", out _));
        Assert.False(json.RootElement.TryGetProperty("RawSection", out _));
    }

    [Fact]
    public void FlagOneRetainsSixRawWordsAndBothOpaqueBlocksWithExactExtents()
    {
        var wire = CreateSection(2, flag: 1, prefixLength: 5, suffix: "BLSHAPES"u8.ToArray());
        var source = Source(wire.Bytes.Length);
        var reader = new NativeMeshClusterV2Reader();

        Assert.True(reader.TryRead(wire.Bytes, source, wire.Offset, out var result, out var diagnostic), diagnostic);
        Assert.NotNull(result);
        Assert.Equal((byte)1, result.Flag);
        Assert.Equal((uint)2, result.EntryCount);
        Assert.Equal(wire.ScalarBits, result.ScalarWordBits);
        Assert.Equal(wire.ScalarBytes, result.RawScalarWords.ToArray());
        Assert.Equal(wire.ByteValues, result.RawByteValues.ToArray());
        Assert.Equal(wire.TupleValues, result.RawTupleValues.ToArray());
        Assert.Equal(wire.SectionBytes, result.RawSection.ToArray());
        Assert.Equal(24L, result.ScalarWordsExtent.Length);
        Assert.Equal(6L, result.ByteValuesExtent.Length);
        Assert.Equal(24L, result.TupleValuesExtent.Length);
        Assert.Equal(source.Offset + wire.Offset, result.Extent.Offset);
        Assert.Equal((long)wire.SectionBytes.Length, result.Extent.Length);
        Assert.Equal(source.Offset + wire.Offset + wire.SectionBytes.Length, result.TupleValuesExtent.Offset + result.TupleValuesExtent.Length);
    }

    [Fact]
    public void ZeroFlagConsumesOnlyTheTagVersionAndFlag()
    {
        var wire = CreateSection(0, flag: 0, prefixLength: 0, suffix: "BLSHAPES"u8.ToArray());
        var reader = new NativeMeshClusterV2Reader();

        Assert.True(reader.TryRead(wire.Bytes, Source(wire.Bytes.Length), 0, out var result, out var diagnostic), diagnostic);
        Assert.NotNull(result);
        Assert.Equal((byte)0, result.Flag);
        Assert.Empty(result.ScalarWordBits);
        Assert.Empty(result.RawByteValues.ToArray());
        Assert.Equal(13L, result.Extent.Length);
        Assert.Equal("BLSHAPES", Encoding.ASCII.GetString(wire.Bytes.AsSpan(13, 8)));
    }

    [Fact]
    public void EveryTruncatedFlagOnePrefixFailsWithoutPublishingASection()
    {
        var wire = CreateSection(1, flag: 1, prefixLength: 0, suffix: []);
        var reader = new NativeMeshClusterV2Reader();
        for (var length = 0; length < wire.SectionBytes.Length; length++)
        {
            var truncated = wire.Bytes[..length];
            Assert.False(reader.TryRead(truncated, Source(truncated.Length), 0, out var result, out _));
            Assert.Null(result);
        }
    }

    [Fact]
    public void CountLimitIsCheckedBeforeScalarOrArrayAllocation()
    {
        var bytes = BuildPrefix(checked((uint)NativeMeshClusterV2Reader.MaximumEntryCount + 1));
        var reader = new NativeMeshClusterV2Reader();

        Assert.False(reader.TryRead(bytes, Source(bytes.Length), 0, out var result, out var diagnostic));
        Assert.Null(result);
        Assert.Equal("NativeMeshCountLimit", diagnostic);
    }

    [Fact]
    public void ConfiguredPayloadBudgetRejectsTheSectionBeforeParsing()
    {
        var wire = CreateSection(1, flag: 1, prefixLength: 0, suffix: []);
        var reader = new NativeMeshClusterV2Reader();

        Assert.False(reader.TryRead(wire.Bytes, Source(wire.Bytes.Length), 0, out var result,
            out var diagnostic, maximumPayloadBytes: wire.Bytes.Length - 1));
        Assert.Null(result);
        Assert.Equal("NativeMeshClusterLimit", diagnostic);
    }

    [Fact]
    public void CancellationIsCheckedBeforeReading()
    {
        var wire = CreateSection(1, flag: 1, prefixLength: 0, suffix: []);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reader = new NativeMeshClusterV2Reader();

        Assert.Throws<OperationCanceledException>(() => reader.TryRead(wire.Bytes, Source(wire.Bytes.Length),
            0, out _, out _, cancellation.Token));
    }

    private static byte[] BuildPrefix(uint count)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteTag(writer, "CLUPSKME", 2);
        writer.Write((byte)1);
        writer.Write(count);
        return stream.ToArray();
    }

    private static WireSection CreateSection(uint count, byte flag, int prefixLength, byte[] suffix)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[prefixLength]);
        var sectionStart = checked((int)stream.Position);
        WriteTag(writer, "CLUPSKME", 2);
        writer.Write(flag);
        var scalarBits = new uint[] { 0x7fc00001, 0x80000000, 0x12345678, 0, uint.MaxValue, 0x3f800000 };
        byte[] scalarBytes = [];
        byte[] byteValues = [];
        byte[] tupleValues = [];
        if (flag == 1)
        {
            writer.Write(count);
            var scalarStart = checked((int)stream.Position);
            foreach (var bits in scalarBits) writer.Write(bits);
            scalarBytes = stream.ToArray()[scalarStart..];
            byteValues = Enumerable.Range(0, checked((int)count * 3)).Select(i => (byte)(i * 17)).ToArray();
            writer.Write(byteValues);
            using var tupleStream = new MemoryStream();
            using (var tupleWriter = new BinaryWriter(tupleStream))
            {
                for (var i = 0; i < checked((int)count * 4); i++)
                {
                    tupleWriter.Write((byte)(i * 3));
                    tupleWriter.Write((ushort)(0x1234 + i));
                }
            }
            tupleValues = tupleStream.ToArray();
            writer.Write(tupleValues);
        }
        var sectionEnd = checked((int)stream.Position);
        writer.Write(suffix);
        var bytes = stream.ToArray();
        return new(bytes, sectionStart, bytes[sectionStart..sectionEnd], scalarBits,
            scalarBytes, byteValues, tupleValues);
    }

    private static void WriteTag(BinaryWriter writer, string tag, uint version)
    {
        writer.Write(Encoding.ASCII.GetBytes(tag));
        writer.Write(version);
    }

    private static DecodedExtent Source(int length)
    {
        var identity = new ResourceIdentity("generated-index", 1, "object");
        return new(identity with { MemberPath = null }, 100, length);
    }

    private sealed record WireSection(byte[] Bytes, int Offset, byte[] SectionBytes,
        uint[] ScalarBits, byte[] ScalarBytes, byte[] ByteValues, byte[] TupleValues);
}
