using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Export.Animation;
using Deviant.Formats.Animation;
using Xunit;

namespace Deviant.Animation.Tests;

public sealed class AnimationVersion14TableTests
{
    [Fact]
    public void PrimaryTableStartsAfterNestedGateAndMetadataPrefix()
    {
        var row = Pack((4, 3u), (2, 4u), (2, 5u));
        var decoded = Read(Fixture(0x40, 1, row, tail: [0x91, 0x92, 0x93]));
        var layout = decoded.Value!.Version14Layout!;
        Assert.Empty(decoded.Diagnostics);
        Assert.Equal(1u, layout.GateWord4);
        Assert.Equal(1u, layout.GateWord8);
        Assert.Equal(0x40u, layout.Flags);
        var table = Assert.Single(layout.Tables);
        Assert.Equal("0x40", table.Kind);
        Assert.Equal(81, table.CountResourceOffset);
        Assert.Equal(89, table.RowsResourceOffset);
        Assert.Equal(8, table.SerializedStride);
        Assert.Equal(new[] { new AnimationScalarField(0, 4, 3), new AnimationScalarField(4, 2, 4), new AnimationScalarField(6, 2, 5) },
            Assert.Single(table.Rows).Fields);
        Assert.Equal(3, layout.RemainingLength);
        Assert.Equal(new byte[] { 0x91, 0x92, 0x93 }, decoded.Value.EncodedResource.Span[(int)layout.RemainingResourceOffset..].ToArray());
    }

    [Fact]
    public void Flag4000RowsPrecedePrimaryRowsWithoutSkippingASeparator()
    {
        var auxiliary = Pack((2, 1u), (2, 2u), (4, 3u), (4, 4u));
        var primary = Pack((4, 0u), (2, 5u), (2, 6u));
        var decoded = Read(Fixture(0x4040, 1, [.. U32(1), .. auxiliary, .. primary]));
        var tables = decoded.Value!.Version14Layout!.Tables;
        Assert.Equal(2, tables.Count);
        Assert.Equal("0x4000", tables[0].Kind);
        Assert.Equal(89, tables[0].CountResourceOffset);
        Assert.Equal(93, tables[0].RowsResourceOffset);
        Assert.Equal(12, tables[0].SerializedStride);
        Assert.Equal(105, tables[1].RowsResourceOffset);
        Assert.Equal(new AnimationScalarField(0, 4, 0), tables[1].Rows[0].Fields[0]);
    }

    [Theory]
    [InlineData(0, 8, 3)]
    [InlineData(0x80, 12, 4)]
    public void ActiveNestedGateSelectsStoredRowWidth(int widthFlag, int stride, int fieldCount)
    {
        var gateRow = widthFlag == 0 ? Pack((2, 1u), (2, 2u), (4, 3u))
            : Pack((2, 1u), (2, 2u), (4, 3u), (4, 4u));
        var decoded = Read(Fixture((uint)(0x40 | widthFlag), 1,
            [.. U32(1), .. gateRow, .. Pack((4, 5u), (2, 6u), (2, 7u))], gateWord4: 0));
        var tables = decoded.Value!.Version14Layout!.Tables;
        Assert.Empty(decoded.Diagnostics);
        Assert.Equal("Gate", tables[0].Kind);
        Assert.Equal(93, tables[0].RowsResourceOffset);
        Assert.Equal(stride, tables[0].SerializedStride);
        Assert.Equal(fieldCount, tables[0].Rows[0].Fields.Count);
        Assert.Equal(93 + stride, tables[1].RowsResourceOffset);
    }

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(uint.MaxValue, 1u)]
    [InlineData(1u, 0u)]
    public void GateConditionComesFromSerializedNestedWords(uint gateWord4, uint gateWord8)
    {
        var decoded = Read(Fixture(0, 0, U32(0), gateWord4, gateWord8));
        var table = Assert.Single(decoded.Value!.Version14Layout!.Tables);
        Assert.Equal("Gate", table.Kind);
        Assert.Empty(table.Rows);
    }

    [Fact]
    public void ConditionalHeaderCountDrivesASecondPrimaryTable()
    {
        var rows = Pack((4, 1u), (2, 2u), (2, 3u));
        var decoded = Read(Fixture(0x440, 1, [.. rows, .. rows], secondaryCount: 1));
        var layout = decoded.Value!.Version14Layout!;
        Assert.Equal(1u, layout.SecondaryCount28);
        Assert.Equal(2, layout.Tables.Count);
        Assert.Equal(93, layout.Tables[0].RowsResourceOffset);
        Assert.Equal(101, layout.Tables[1].RowsResourceOffset);
        Assert.Equal(89, layout.Tables[1].CountResourceOffset);
        Assert.Equal("0x400+0x40", layout.Tables[1].Kind);
    }

    [Fact]
    public void Flag100TableConsumesThreeDwordsPerRow()
    {
        var decoded = Read(Fixture(0x100, 0, [.. U32(1), .. Pack((4, 1u), (4, 2u), (4, 3u))]));
        var table = Assert.Single(decoded.Value!.Version14Layout!.Tables);
        Assert.Equal("0x100", table.Kind);
        Assert.Equal(12, table.SerializedStride);
        Assert.Equal(new[] { 4, 4, 4 }, table.Rows[0].Fields.Select(f => f.Width));
        Assert.Equal(new[] { 0, 4, 8 }, table.Rows[0].Fields.Select(f => f.RowOffset));
    }

    [Fact]
    public void DuplicatesUnknownFlagsAndRawBitsRemainInSourceOrder()
    {
        const uint flags = 0x80000040;
        var row = Pack((4, 0x7FC01234u), (2, ushort.MaxValue), (2, 0u));
        var decoded = Read(Fixture(flags, 2, [.. row, .. row], tail: [0xAA]));
        var layout = decoded.Value!.Version14Layout!;
        Assert.Empty(decoded.Diagnostics);
        Assert.Equal(flags, layout.Flags);
        var table = Assert.Single(layout.Tables);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(table.Rows[0].Fields, table.Rows[1].Fields);
        Assert.Equal(new[] { 0, 1 }, table.Rows.Select(r => r.Ordinal));
        Assert.Equal(new long[] { 89, 97 }, table.Rows.Select(r => r.ResourceOffset));
        Assert.Equal(0x7FC01234u, table.Rows[1].Fields[0].Bits);
    }

    [Theory]
    [InlineData(uint.MaxValue)]
    [InlineData(2u)]
    public void OversizedOrTruncatedPrimaryTablesKeepEncodedBytes(uint count)
    {
        var encoded = Fixture(0x40, count, new byte[8]);
        var decoded = Read(encoded);
        Assert.NotNull(decoded.Value);
        Assert.Null(decoded.Value.Version14Layout);
        Assert.Single(decoded.Diagnostics);
        Assert.Equal(encoded, decoded.Value.EncodedResource.ToArray());
    }

    [Fact]
    public void Flag4000RequiresItsCountBytes()
    {
        var encoded = Fixture(0x4000, 0, [])[..89];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(57), 28);
        var decoded = Read(encoded);
        Assert.NotNull(decoded.Value);
        Assert.Null(decoded.Value.Version14Layout);
        Assert.Single(decoded.Diagnostics);
    }

    [Fact]
    public void EmptyPrimaryTableNeedsOnlyTheConsumedHeaderPrefix()
    {
        var encoded = Fixture(0x40, 0, [])[..89];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(57), 28);
        var decoded = Read(encoded);
        Assert.Empty(decoded.Diagnostics);
        Assert.Empty(Assert.Single(decoded.Value!.Version14Layout!.Tables).Rows);
        Assert.Equal(0, decoded.Value.Version14Layout.RemainingLength);
    }

    [Fact]
    public void UnknownVariantKeepsTheFramedResource()
    {
        var encoded = Fixture(0x40, 0, [], variant: 9);
        var decoded = Read(encoded);
        Assert.Null(decoded.Value!.Version14Layout);
        Assert.Single(decoded.Diagnostics);
        Assert.Equal(encoded, decoded.Value.EncodedResource.ToArray());
    }

    [Fact]
    public async Task JsonContainsAllTablesFieldsAndEncodedTail()
    {
        var auxiliary = Pack((2, 1u), (2, 2u), (4, 3u), (4, 4u));
        var primary = Pack((4, 5u), (2, 6u), (2, 7u));
        var encoded = Fixture(0x4040, 2, [.. U32(1), .. auxiliary, .. primary, .. primary], tail: [0xA0, 0xA1]);
        using var output = new MemoryStream();
        await AnimationJsonWriter.WriteAsync(output, Read(encoded));
        using var json = JsonDocument.Parse(output.ToArray());
        var tables = json.RootElement.GetProperty("Version14Layout").GetProperty("Tables");
        Assert.Equal(2, tables.GetArrayLength());
        Assert.Equal(4, tables[0].GetProperty("Rows")[0].GetProperty("Fields").GetArrayLength());
        Assert.Equal(3, tables[1].GetProperty("Rows")[1].GetProperty("Fields").GetArrayLength());
        Assert.Equal(encoded, Convert.FromBase64String(json.RootElement.GetProperty("EncodedResource").GetString()!));
    }

    [Fact]
    public void DefaultProfileDoesNotApplyTableRequirements()
    {
        var encoded = Fixture(0x40, uint.MaxValue, new byte[8]);
        var decoded = new AnimationDataReader().Decode(encoded, new("index", 1), new("index", "revision"),
            new("volume.dat", 256, encoded.Length));
        Assert.NotNull(decoded.Value);
        Assert.Null(decoded.Value.Version14Layout);
        Assert.Empty(decoded.Diagnostics);
        Assert.Equal(uint.MaxValue, decoded.Value.HeaderWords[5].Bits);
        Assert.Contains("profile=FramedRecords", decoded.Steps[0].Options);
        Assert.Equal(encoded, decoded.Value.EncodedResource.ToArray());
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(0x4000u, 1u)]
    [InlineData(0x100u, 1u)]
    public void EachCursorCountIsBoundedBeforeAllocation(uint flags, uint gateWord4)
    {
        var encoded = Fixture(flags, 0, U32(uint.MaxValue), gateWord4: gateWord4);
        var decoded = Read(encoded);
        Assert.NotNull(decoded.Value);
        Assert.Null(decoded.Value.Version14Layout);
        Assert.Single(decoded.Diagnostics);
        Assert.Contains("row limit", decoded.Diagnostics[0]);
        Assert.Equal(encoded, decoded.Value.EncodedResource.ToArray());
    }

    private static DecodeResult<AnimationDataDocument> Read(byte[] bytes) => new AnimationDataReader().Decode(bytes,
        new("index", 1), new("index", "revision"), new("volume.dat", 256, bytes.Length), AnimationReadProfile.Version14Tables);

    private static byte[] U32(uint bits) => Pack((4, bits));

    private static byte[] Pack(params (int Width, uint Bits)[] fields)
    {
        var bytes = new byte[fields.Sum(f => f.Width)]; var offset = 0;
        foreach (var field in fields)
        {
            if (field.Width == 2) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), checked((ushort)field.Bits));
            else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), field.Bits);
            offset += field.Width;
        }
        return bytes;
    }

    private static byte[] Fixture(uint flags, uint primaryCount, byte[] tables, uint gateWord4 = 1,
        uint gateWord8 = 1, uint secondaryCount = 0, uint variant = 2, byte[]? tail = null)
    {
        tail ??= [];
        var prefixLength = (flags & 0x400) != 0 ? 32 : 28;
        var body = new byte[Math.Max(32, prefixLength + tables.Length + tail.Length)];
        uint[] prefix = [variant, gateWord4, gateWord8, flags, 0x12345678, primaryCount, 0x7FC05678];
        for (var i = 0; i < prefix.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(i * 4), prefix[i]);
        if (prefixLength == 32) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(28), secondaryCount);
        tables.CopyTo(body, prefixLength); tail.CopyTo(body, prefixLength + tables.Length);
        using var output = new MemoryStream();
        Write("COM_CONT", 6, new byte[13]); Write("LOADCONT", 2, []); Write("ANIMDATA", 14, body);
        return output.ToArray();

        void Write(string tag, uint version, byte[] data)
        {
            output.Write(Encoding.ASCII.GetBytes(tag)); Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(header, version);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)data.Length);
            output.Write(header); output.Write(data);
        }
    }
}
