using System.Buffers.Binary;
using Deviant.Assets;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureUsageReaderTests
{
    private static readonly ResourceIdentity Container = new(new string('b', 64), 23);
    private static readonly SourceRevision Revision = new(Container.IndexContentId, "revision");

    [Fact]
    public void RetainsOrderedTypedBindingsAndOpaqueFields()
    {
        var frame = Frame([(13u, 2137u, 111u, (byte)2), (17u, 2137u, 222u, (byte)0)]);
        var result = Read(frame);
        Assert.Empty(result.Diagnostics);
        var document = Assert.IsType<TextureShaderSlotsDocument>(result.Value);
        Assert.Equal(new[] { 0, 1 }, document.Slots.Select(item => item.SlotOrdinal));
        Assert.Equal(new uint[] { 111, 222 }, document.Slots.Select(item => item.Reference.Id));
        Assert.Equal(13u, document.Slots[0].StoredWord);
        Assert.Equal(2, document.Slots[0].TrailingByte);
        Assert.Equal(13, document.Slots[0].Extent.Length);
        Assert.Equal(8, document.Slots[0].Reference.Extent.Length);
        Assert.Equal(frame.PayloadExtent.Offset + 87, document.Slots[0].Reference.Extent.Offset);
        Assert.Equal(new byte[] { 0xC1, 0xC2, 0xC3 }, document.UnparsedSuffixBytes.ToArray());
        Assert.Equal(35, document.RawFixedFields35.Length);
        Assert.Equal(frame.Identity, document.Identity);
    }

    [Fact]
    public void EmptyBindingsRemainEmptyAndSameTextureCanOccupyMultipleSlots()
    {
        var empty = Assert.IsType<TextureShaderSlotsDocument>(Read(Frame([])).Value);
        Assert.Empty(empty.Slots);
        var shared = Assert.IsType<TextureShaderSlotsDocument>(Read(Frame(
            [(0u, 2137u, 111u, (byte)0), (0u, 2137u, 111u, (byte)2)])).Value);
        Assert.Equal(2, shared.Slots.Count);
        Assert.Equal(shared.Slots[0].Reference.Id, shared.Slots[1].Reference.Id);
        Assert.NotEqual(shared.Slots[0].Extent, shared.Slots[1].Extent);
    }

    [Fact]
    public void RejectsTruncatedSlotsHighCountAndWrongVersion()
    {
        var frame = Frame([]);
        var bytes = frame.RawPayload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(79), 1);
        Assert.Null(Read(frame with { RawPayload = bytes }).Value);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(79), 256);
        Assert.Null(Read(frame with { RawPayload = bytes }).Value);
        bytes = Frame([]).RawPayload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 90);
        Assert.Null(Read(frame with { RawPayload = bytes }).Value);
    }

    [Fact]
    public void RejectsWrongNativeOwnerEnvelopeAndSource()
    {
        var frame = Frame([]);
        Assert.Null(Read(frame with { NativeType = 2137 }).Value);
        var bytes = frame.RawPayload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 1);
        Assert.Null(Read(frame with { RawPayload = bytes }).Value);
        var wrongRevision = new SourceRevision(new string('c', 64), "revision");
        Assert.Null(new TextureUsageReader().ParseShaderSlots(frame, wrongRevision, []).Value);
    }

    private static DecodeResult<TextureShaderSlotsDocument> Read(TextureContainerObjectFrame frame) =>
        new TextureUsageReader().ParseShaderSlots(frame, Revision, [new("source.dat", 0, 128)]);

    private static TextureContainerObjectFrame Frame((uint Word, uint Type, uint Id, byte Trailer)[] slots)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(3u); writer.Write("COM_CONT"u8); writer.Write(6u); writer.Write(0u);
        writer.Write("LOADCONT"u8); writer.Write(2u); writer.Write("SHADCUST"u8); writer.Write(91u);
        writer.Write(new byte[35]); writer.Write((uint)slots.Length);
        foreach (var slot in slots)
        {
            writer.Write(slot.Word); writer.Write(slot.Type); writer.Write(slot.Id); writer.Write(slot.Trailer);
        }
        writer.Write(new byte[] { 0xC1, 0xC2, 0xC3 });
        var payload = stream.ToArray();
        var identity = Container with { MemberPath = "dc-object/0/2133/303" };
        return new(identity, 0, 2133, 303, new(Container, 24, 8), new(Container, 100, 13),
            new(Container, 113, payload.Length), new byte[13], payload, 0, 0, 0, 3);
    }
}
