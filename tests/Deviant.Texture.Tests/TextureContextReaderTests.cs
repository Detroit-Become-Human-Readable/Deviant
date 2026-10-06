using System.Buffers.Binary;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureContextReaderTests
{
    private static readonly ResourceIdentity Container = new("index", 7);
    private static readonly ResourceIdentity Member = new("index", 7, "object/9");
    private static readonly SourceRevision Revision = new("index", "revision");

    [Fact]
    public void PreservesNamesAndPathWithExactByteExtents()
    {
        var record = Record(2137, 42, "Light owner", "assets/probe.dds");
        var body = new byte[record.Length + 12];
        record.CopyTo(body, 5);
        var result = new TextureContextReader().ParseIblLightV13(body, 5, 5 + record.Length,
            Member, Revision, new(Container, 100, body.Length), []);
        var context = Assert.IsType<TextureIblLightV13Context>(result.Value);
        Assert.Equal("Light owner", context.OwnerName.Text);
        Assert.Equal("assets/probe.dds", context.SourcePath.Text);
        Assert.True(context.TextureReferences[0].IsBound);
        Assert.Equal(200, context.TextureReferences[0].Extent.Offset);
        Assert.Equal(212, context.OwnerName.LengthExtent.Offset);
        Assert.Equal(216, context.OwnerName.ValueExtent.Offset);
        Assert.Equal(11, context.OwnerName.ValueExtent.Length);
        Assert.Equal(83, context.RawFixedFields83.Length);
        Assert.Equal(8, context.RawTrailingFields8.Length);
    }

    [Fact]
    public void ReadsSecondStoredReferenceBeforeNames()
    {
        var single = Record(2137, 42, "Owner", "assets/probe.dds");
        var dual = new byte[single.Length + 8];
        single.AsSpan(0, 103).CopyTo(dual);
        single.AsSpan(103).CopyTo(dual.AsSpan(111));
        BinaryPrimitives.WriteUInt32LittleEndian(dual.AsSpan(87), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(dual.AsSpan(103), 2137);
        BinaryPrimitives.WriteUInt32LittleEndian(dual.AsSpan(107), 43);
        var context = Assert.IsType<TextureIblLightV13Context>(Read(dual).Value);
        Assert.Equal(2, context.TextureReferences.Count);
        Assert.Equal(43u, context.TextureReferences[1].Id);
        Assert.Equal("Owner", context.OwnerName.Text);
    }

    [Fact]
    public void RetainsZeroReferenceContextAndRejectsExcessiveReferenceCount()
    {
        var single = Record(2137, 42, "Owner", "assets/probe.dds");
        var empty = new byte[single.Length - 8];
        single.AsSpan(0, 95).CopyTo(empty);
        single.AsSpan(103).CopyTo(empty.AsSpan(95));
        BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(87), 0);
        var context = Assert.IsType<TextureIblLightV13Context>(Read(empty).Value);
        Assert.Empty(context.TextureReferences);
        Assert.Null(context.TextureReference);
        Assert.Equal("assets/probe.dds", context.SourcePath.Text);
        BinaryPrimitives.WriteUInt32LittleEndian(single.AsSpan(87), TextureContextReader.MaximumReferences + 1);
        Assert.Null(Read(single).Value);
    }

    [Fact]
    public void NullReferenceRetainsContextWithoutAttribution()
    {
        var result = Read(Record(0, 0, "Unbound owner", "assets/unbound.dds"));
        Assert.NotNull(result.Value);
        Assert.False(result.Value.TextureReferences[0].IsBound);
        Assert.Equal("Unbound", result.UnknownFields["referenceStatus"]);
    }

    [Theory]
    [InlineData(8, 12)]
    [InlineData(107, uint.MaxValue)]
    [InlineData(95, uint.MaxValue)]
    public void HandlesUnsupportedVersionOversizedLengthAndSentinelReference(int offset, uint value)
    {
        var record = Record(2137, 42, "Owner", "assets/probe.dds");
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), value);
        var result = Read(record);
        if (offset == 95) Assert.False(Assert.IsType<TextureIblLightV13Context>(result.Value).TextureReferences[0].IsBound);
        else Assert.Null(result.Value);
    }

    [Fact]
    public void RejectsTruncatedAndOverlongRecordsAndEmbeddedNulls()
    {
        var record = Record(2137, 42, "Owner", "assets/probe.dds");
        Assert.Null(Read(record[..^1]).Value);
        Assert.Null(Read([.. record, 0]).Value);
        record[111] = 0;
        Assert.Null(Read(record).Value);
    }

    private static DecodeResult<TextureIblLightV13Context> Read(byte[] record) =>
        new TextureContextReader().ParseIblLightV13(record, 0, record.Length, Member, Revision,
            new(Container, 0, record.Length), []);

    private static byte[] Record(uint type, uint id, string owner, string path)
    {
        var ownerBytes = Encoding.ASCII.GetBytes(owner);
        var pathBytes = Encoding.ASCII.GetBytes(path);
        var bytes = new byte[107 + 4 + ownerBytes.Length + 4 + pathBytes.Length + 8];
        "IBLLIGHT"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 13);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(87), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(95), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(99), id);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(107), checked((uint)ownerBytes.Length));
        ownerBytes.CopyTo(bytes, 111);
        var pathLengthOffset = 111 + ownerBytes.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(pathLengthOffset), checked((uint)pathBytes.Length));
        pathBytes.CopyTo(bytes, pathLengthOffset + 4);
        return bytes;
    }
}
