using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Formats.Models;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class NativeMeshBlendShapeV3ReaderTests
{
    [Fact]
    public void ReadsGeneratedEntryAndRetainsRawFieldsAndSourceExtents()
    {
        var entryData = Entry([2, 2, 3, 2, uint.MaxValue], flags: 3);
        var sample = CreateSection([entryData]);
        var reader = new NativeMeshBlendShapeV3Reader();

        Assert.True(reader.TryRead(sample.Bytes, Source(sample.Bytes.Length), sample.Offset, 1,
            out var section, out var countExtents, out var diagnostic), diagnostic);
        Assert.NotNull(section);
        var group = Assert.Single(section.Groups);
        var entry = Assert.Single(group.Entries);
        Assert.Equal((uint)1, group.EntryCount);
        Assert.Equal(4L, group.CountExtent.Length);
        Assert.Single(countExtents);
        Assert.Equal(new uint[] { 2, 2, 3, 2, uint.MaxValue }, entry.RawHeaderWords);
        Assert.Equal((byte)3, entry.RawFlags);
        Assert.Equal(21L, entry.HeaderExtent.Length);
        Assert.Equal(21, entry.RawHeader.Length);
        Assert.Equal(2, entry.StringRecords.Count);
        Assert.Equal(sample.StringBytes[0], entry.StringRecords[0].RawBytes.ToArray());
        Assert.Equal(sample.StringBytes[1], entry.StringRecords[1].RawBytes.ToArray());
        Assert.Equal(8, entry.RawU32ArrayA.Length);
        Assert.Equal(8, entry.RawU32ArrayB.Length);
        Assert.Equal(8, entry.RawU16Table.Length);
        Assert.Equal(36, entry.RawFloatWords.Length);
        Assert.Equal(12, entry.RawFlag1Words.Length);
        Assert.Equal(12, entry.RawFlag2Words.Length);
        Assert.Equal(SerializeEntry(entryData), entryExtentBytes(sample, entry));
        Assert.Equal(sample.SectionBytes, section.RawSection.ToArray());
        Assert.Equal((long)sample.SectionBytes.Length, section.Extent.Length);
        Assert.Equal(Source(sample.Bytes.Length).Offset + sample.Offset, section.Extent.Offset);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    [InlineData((byte)0x80)]
    public void PreservesEveryFlagByteAndConsumesOptionalArraysOnlyForLowBits(byte flags)
    {
        var sample = CreateSection([Entry([0, 0, 1, 0, 7], flags)]);
        var reader = new NativeMeshBlendShapeV3Reader();

        Assert.True(reader.TryRead(sample.Bytes, Source(sample.Bytes.Length), sample.Offset, 1,
            out var section, out _, out var diagnostic), diagnostic);
        var entry = Assert.Single(Assert.Single(section!.Groups).Entries);
        Assert.Equal(flags, entry.RawFlags);
        Assert.Equal((flags & 1) != 0 ? 4 : 0, entry.RawFlag1Words.Length);
        Assert.Equal((flags & 2) != 0 ? 4 : 0, entry.RawFlag2Words.Length);
        Assert.Equal(sample.SectionBytes, section.RawSection.ToArray());
    }

    [Fact]
    public void ZeroEntryGroupRetainsItsCountExtent()
    {
        var sample = CreateSection([]);
        var reader = new NativeMeshBlendShapeV3Reader();

        Assert.True(reader.TryRead(sample.Bytes, Source(sample.Bytes.Length), sample.Offset, 1,
            out var section, out var countExtents, out var diagnostic), diagnostic);
        var group = Assert.Single(section!.Groups);
        Assert.Equal((uint)0, group.EntryCount);
        Assert.Empty(group.Entries);
        Assert.Single(countExtents);
        Assert.Equal(4L, group.CountExtent.Length);
    }

    [Fact]
    public void EveryTruncatedEntryBoundaryFailsWithoutReturningASection()
    {
        var sample = CreateSection([Entry([1, 1, 1, 1, 0], flags: 3)]);
        var reader = new NativeMeshBlendShapeV3Reader();
        for (var length = 0; length < sample.SectionBytes.Length; length++)
        {
            var truncated = sample.Bytes[..(sample.Offset + length)];
            Assert.False(reader.TryRead(truncated, Source(truncated.Length), sample.Offset, 1,
                out var section, out _, out _));
            Assert.Null(section);
        }
    }

    [Fact]
    public void CountProductIsBoundedBeforeRowsAreAllocated()
    {
        var sample = CreateHeaderOnly([50000, 50000, 1, 0, uint.MaxValue], flags: 0);
        var reader = new NativeMeshBlendShapeV3Reader();

        Assert.False(reader.TryRead(sample.Bytes, Source(sample.Bytes.Length), sample.Offset, 1,
            out var section, out _, out var diagnostic));
        Assert.Null(section);
        Assert.Equal("NativeMeshBlendShapeDataLimit", diagnostic);
    }

    [Fact]
    public void AggregateEntryDtoBudgetIsCheckedBeforeEntryArraysAreAllocated()
    {
        var sample = CreateEntryCountOnly(checked((uint)NativeMeshBlendShapeV3Reader.MaximumEntries));
        var reader = new NativeMeshBlendShapeV3Reader();

        Assert.False(reader.TryRead(sample.Bytes, Source(sample.Bytes.Length), sample.Offset, 1,
            out var section, out _, out var diagnostic));
        Assert.Null(section);
        Assert.Equal("NativeMeshBlendShapeMetadataLimit", diagnostic);
    }

    [Fact]
    public void AggregateStringDtoBudgetIsCheckedBeforeStringArraysAreAllocated()
    {
        var sample = CreateHeaderOnly([NativeMeshBlendShapeV3Reader.MaximumStringRecords, 0, 0, 0, 0], flags: 0);
        var reader = new NativeMeshBlendShapeV3Reader();

        Assert.False(reader.TryRead(sample.Bytes, Source(sample.Bytes.Length), sample.Offset, 1,
            out var section, out _, out var diagnostic));
        Assert.Null(section);
        Assert.Equal("NativeMeshBlendShapeMetadataLimit", diagnostic);
    }

    [Fact]
    public void EntryLimitIsAggregatedAcrossGroupsBeforeSecondEntryArrayAllocation()
    {
        var sample = CreateEntryOnlyGroups([32768, 32769]);
        var reader = new NativeMeshBlendShapeV3Reader();

        Assert.False(reader.TryRead(sample.Bytes, Source(sample.Bytes.Length), sample.Offset, 2,
            out var section, out _, out var diagnostic));
        Assert.Null(section);
        Assert.Equal("NativeMeshBlendShapeEntryLimit", diagnostic);
    }

    [Fact]
    public void StringRecordLimitIsAggregatedAcrossGroupsBeforeSecondStringArrayAllocation()
    {
        var sample = CreateStringCountGroups([32768, 32769]);
        var reader = new NativeMeshBlendShapeV3Reader();

        Assert.False(reader.TryRead(sample.Bytes, Source(sample.Bytes.Length), sample.Offset, 2,
            out var section, out _, out var diagnostic));
        Assert.Null(section);
        Assert.Equal("NativeMeshBlendShapeStringRecordLimit", diagnostic);
    }

    [Fact]
    public void NegativeAndOversizedStringLengthsAreRejected()
    {
        foreach (var length in new[] { -1, NativeMeshBlendShapeV3Reader.MaximumStringBytes + 1 })
        {
            var sample = CreateInvalidStringLength(length);
            var reader = new NativeMeshBlendShapeV3Reader();

            Assert.False(reader.TryRead(sample.Bytes, Source(sample.Bytes.Length), sample.Offset, 1,
                out var section, out _, out _));
            Assert.Null(section);
        }
    }

    [Fact]
    public void ConfiguredPayloadBudgetRejectsBeforeParsing()
    {
        var sample = CreateSection([Entry([0, 0, 0, 0, 0], flags: 0)]);
        var reader = new NativeMeshBlendShapeV3Reader();

        Assert.False(reader.TryRead(sample.Bytes, Source(sample.Bytes.Length), sample.Offset, 1,
            out var section, out _, out var diagnostic, maximumPayloadBytes: sample.Bytes.Length - 1));
        Assert.Null(section);
        Assert.Equal("NativeMeshBlendShapeLimit", diagnostic);
    }

    [Fact]
    public void SourceExtentAndOffsetMustMatchThePayload()
    {
        var sample = CreateSection([]);
        var validSource = Source(sample.Bytes.Length);
        var reader = new NativeMeshBlendShapeV3Reader();
        var invalidSources = new[]
        {
            new DecodedExtent(validSource.Container, -1, sample.Bytes.Length),
            new DecodedExtent(validSource.Container, validSource.Offset, sample.Bytes.Length + 1L),
            new DecodedExtent(validSource.Container, long.MaxValue, sample.Bytes.Length)
        };
        foreach (var invalidSource in invalidSources)
        {
            Assert.False(reader.TryRead(sample.Bytes, invalidSource, sample.Offset, 1,
                out var section, out _, out var diagnostic));
            Assert.Null(section);
            Assert.Equal("NativeMeshBlendShapeSourceMismatch", diagnostic);
        }
        Assert.False(reader.TryRead(sample.Bytes, validSource, -1, 1, out _, out _, out var offsetError));
        Assert.Equal("NativeMeshBlendShapeSourceMismatch", offsetError);
    }

    [Fact]
    public void CancellationIsCheckedBeforeReading()
    {
        var sample = CreateSection([]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reader = new NativeMeshBlendShapeV3Reader();

        Assert.Throws<OperationCanceledException>(() => reader.TryRead(sample.Bytes, Source(sample.Bytes.Length),
            sample.Offset, 1, out _, out _, out _, cancellation.Token));
    }

    [Fact]
    public void JsonOmitsRawMemoryViews()
    {
        var sample = CreateSection([Entry([1, 1, 1, 1, 0], flags: 3)]);
        var reader = new NativeMeshBlendShapeV3Reader();
        Assert.True(reader.TryRead(sample.Bytes, Source(sample.Bytes.Length), sample.Offset, 1,
            out var section, out _, out var diagnostic), diagnostic);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(section));
        var root = json.RootElement;
        Assert.True(root.TryGetProperty("Groups", out _));
        Assert.False(root.TryGetProperty("RawSection", out _));
        var entry = root.GetProperty("Groups")[0].GetProperty("Entries")[0];
        Assert.True(entry.TryGetProperty("RawHeaderWords", out _));
        Assert.False(entry.TryGetProperty("RawHeader", out _));
        Assert.False(entry.TryGetProperty("RawU32ArrayA", out _));
        Assert.False(entry.TryGetProperty("RawFloatWords", out _));
        Assert.False(entry.GetProperty("StringRecords")[0].TryGetProperty("RawBytes", out _));
    }

    private static byte[] entryExtentBytes(Sample sample, NativeMeshBlendShapeEntry entry)
    {
        var start = checked((int)(entry.Extent.Offset - Source(sample.Bytes.Length).Offset));
        return sample.Bytes.AsSpan(start, checked((int)entry.Extent.Length)).ToArray();
    }

    private static Sample CreateSection(IReadOnlyList<EntryData> entries)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[3]);
        var offset = checked((int)stream.Position);
        WriteTag(writer, "BLSHAPES", 3);
        writer.Write((uint)entries.Count);
        var strings = new List<byte[]>();
        foreach (var entry in entries) WriteEntry(writer, entry, strings);
        var end = checked((int)stream.Position);
        writer.Write("TAIL"u8);
        var bytes = stream.ToArray();
        return new(bytes, offset, bytes[offset..end], strings.ToArray());
    }

    private static Sample CreateHeaderOnly(IReadOnlyList<uint> words, byte flags)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var offset = checked((int)stream.Position);
        WriteTag(writer, "BLSHAPES", 3);
        writer.Write(1u);
        foreach (var word in words) writer.Write(word);
        writer.Write(flags);
        return new(stream.ToArray(), offset, [], []);
    }

    private static Sample CreateEntryCountOnly(uint entryCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var offset = checked((int)stream.Position);
        WriteTag(writer, "BLSHAPES", 3);
        writer.Write(entryCount);
        return new(stream.ToArray(), offset, [], []);
    }

    private static Sample CreateEntryOnlyGroups(IReadOnlyList<uint> counts)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var offset = checked((int)stream.Position);
        WriteTag(writer, "BLSHAPES", 3);
        foreach (var count in counts)
        {
            writer.Write(count);
            for (var index = 0; index < checked((int)count); index++) WriteEmptyEntry(writer);
        }
        var bytes = stream.ToArray();
        return new(bytes, offset, bytes[offset..], []);
    }

    private static Sample CreateStringCountGroups(IReadOnlyList<uint> counts)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var offset = checked((int)stream.Position);
        WriteTag(writer, "BLSHAPES", 3);
        foreach (var count in counts)
        {
            writer.Write(1u);
            writer.Write(count); writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write((byte)0);
            for (var index = 0; index < checked((int)count); index++)
            {
                writer.Write(0u);
                writer.Write(0u);
            }
        }
        var bytes = stream.ToArray();
        return new(bytes, offset, bytes[offset..], []);
    }

    private static void WriteEmptyEntry(BinaryWriter writer)
    {
        for (var index = 0; index < 5; index++) writer.Write(0u);
        writer.Write((byte)0);
    }

    private static Sample CreateInvalidStringLength(int length)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var offset = checked((int)stream.Position);
        WriteTag(writer, "BLSHAPES", 3);
        writer.Write(1u);
        writer.Write(1u); writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write((byte)0);
            writer.Write(length);
            writer.Write(0u);
        return new(stream.ToArray(), offset, [], []);
    }

    private static byte[] SerializeEntry(EntryData entry)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteEntry(writer, entry, []);
        return stream.ToArray();
    }

    private static EntryData Entry(uint[] words, byte flags) => new(words, flags);

    private static void WriteEntry(BinaryWriter writer, EntryData entry, List<byte[]> strings)
    {
        foreach (var word in entry.Words) writer.Write(word);
        writer.Write(entry.Flags);
        for (var index = 0; index < checked((int)entry.Words[0]); index++)
        {
            var bytes = new byte[] { (byte)(0x41 + index), 0, (byte)(0x61 + index) };
            strings.Add(bytes);
            writer.Write(bytes.Length);
            writer.Write(bytes);
            writer.Write(0x3f000000u + (uint)index);
        }
        for (var index = 0; index < checked((int)entry.Words[3] * 2); index++) writer.Write(0x1000u + (uint)index);
        for (var index = 0; index < checked((int)(entry.Words[0] * entry.Words[1])); index++) writer.Write((ushort)(0x2000 + index));
        for (var index = 0; index < checked((int)entry.Words[2] * 3); index++) writer.Write(0x3f800000u + (uint)index);
        if ((entry.Flags & 1) != 0)
            for (var index = 0; index < checked((int)entry.Words[2]); index++) writer.Write(0x3000u + (uint)index);
        if ((entry.Flags & 2) != 0)
            for (var index = 0; index < checked((int)entry.Words[2]); index++) writer.Write(0x4000u + (uint)index);
    }

    private static void WriteTag(BinaryWriter writer, string tag, uint version)
    {
        writer.Write(Encoding.ASCII.GetBytes(tag));
        writer.Write(version);
    }

    private static DecodedExtent Source(int length)
    {
        var identity = new ResourceIdentity("generated-index", 1, "object");
        return new(identity with { MemberPath = null }, 250, length);
    }

    private sealed record EntryData(uint[] Words, byte Flags);
    private sealed record Sample(byte[] Bytes, int Offset, byte[] SectionBytes, byte[][] StringBytes);
}
