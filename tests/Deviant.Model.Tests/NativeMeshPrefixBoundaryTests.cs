using System.Buffers.Binary;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Models;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class NativeMeshPrefixBoundaryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CountedOpaqueNamesAndModeSpecificBlobsReachOnlyThePrefixEnd(int mode)
    {
        var sample = Create((byte)mode);
        Assert.True(new NativeMeshPrefixReader().TryRead(sample.Frame, sample.Entity, out var doc, out var diagnostic), diagnostic);
        Assert.Equal(sample.End, doc!.NextClassOffset);
        Assert.Equal(mode, doc.ContextMode);
        Assert.Equal(2, doc.ContextEntries.Count);
        Assert.Equal(sample.Name, doc.ContextEntries[0].StoredNameBytes);
        Assert.Equal(sample.FirstBlob.Length, doc.ContextBlobExtent.Length);
        Assert.Equal(sample.SecondBlob.Length, doc.SecondaryContextBlobExtent.Length);
        Assert.Equal(sample.FirstBlob, Slice(sample, doc.ContextBlobExtent));
        Assert.Equal(sample.SecondBlob, Slice(sample, doc.SecondaryContextBlobExtent));
        Assert.Single(doc.ExtraReferences);
        Assert.Equal("9", doc.ExtraReferences[0].Reference.Id);
        Assert.Equal(sample.Frame.Identity, doc.ExtraReferences[0].Reference.Owner);
        Assert.Equal(0x80u, doc.FinalFields.Word);
        Assert.Equal((byte)0x7f, doc.FinalFields.Flag);
        Assert.Equal(0x060504030201UL, doc.FinalFields.RawSixBytes);
        Assert.Equal(new uint[] { 11, 12, 13, 14, 15 }, doc.FinalFields.Words);
        Assert.Equal(sample.Frame.PayloadExtent.Offset + sample.End, doc.TailExtent.Offset);
        Assert.Equal("SKELETON", Encoding.ASCII.GetString(sample.Bytes, sample.End, 8));
        Assert.False(new NativeMeshEntityReader().TryRead(sample.Frame, sample.Entity, out _, out _));
    }

    [Fact]
    public void StoredNamesAreCopiedBeforeTheCallerChangesItsBuffer()
    {
        var sample = Create(2);
        Assert.True(new NativeMeshPrefixReader().TryRead(sample.Frame, sample.Entity, out var doc, out _));
        var names = doc!.ContextEntries.Select(entry => entry.StoredNameBytes.ToArray()).ToArray();
        sample.Bytes.AsSpan().Fill(0xa5);
        Assert.Equal(names[0], doc.ContextEntries[0].StoredNameBytes);
        Assert.Equal(names[1], doc.ContextEntries[1].StoredNameBytes);
        Assert.Equal(0xa0000008u, doc.ContextEntries[0].ControlWord);
        Assert.Equal((uint)sample.SecondBlob.Length, doc.ContextEntries[0].SecondaryByteCount);
    }

    [Fact]
    public void EachTruncationWithinThePrefixIsRejected()
    {
        var sample = Create(2);
        for (var end = sample.Entity.NextClassOffset; end < sample.End; end++)
        {
            var frame = sample.Frame with { RawPayload = sample.Bytes.AsMemory(0, end),
                PayloadExtent = sample.Frame.PayloadExtent with { Length = end } };
            Assert.False(new NativeMeshPrefixReader().TryRead(frame, sample.Entity, out _, out _));
        }
        var exact = sample.Frame with { RawPayload = sample.Bytes.AsMemory(0, sample.End),
            PayloadExtent = sample.Frame.PayloadExtent with { Length = sample.End } };
        Assert.True(new NativeMeshPrefixReader().TryRead(exact, sample.Entity, out var doc, out _));
        Assert.Equal(0, doc!.TailExtent.Length);
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("count")]
    [InlineData("name")]
    [InlineData("blob")]
    [InlineData("secondary-blob")]
    [InlineData("extra")]
    public void VariableSectionLimitsRejectBeforeRetainingEntries(string field)
    {
        var sample = Create(2);
        var bytes = sample.Bytes.ToArray();
        var (offset, value) = field switch
        {
            "mode" => (sample.PackedOffset, 0x04000002u),
            "count" => (sample.PackedOffset, 0x02000401u),
            "name" => (sample.FirstNameLengthOffset, 4097u),
            "blob" => (sample.ControlOffset, 0x00800001u),
            "secondary-blob" => (sample.SecondaryOffset, uint.MaxValue),
            "extra" => (sample.ExtraCountOffset, 4097u),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        Assert.False(new NativeMeshPrefixReader().TryRead(sample.Frame with { RawPayload = bytes }, sample.Entity, out var doc, out _));
        Assert.Null(doc);
    }

    [Fact]
    public void AggregateNameBudgetRejectsIndividuallyAllowedNames()
    {
        var sample = Create(0, count: 257, name: new byte[4096]);
        Assert.True(sample.Bytes.Length < NativeMeshPrefixReader.MaximumPayloadBytes);
        Assert.False(new NativeMeshPrefixReader().TryRead(sample.Frame, sample.Entity, out var doc, out var diagnostic));
        Assert.Equal("NativeMeshContextNameBudget", diagnostic);
        Assert.Null(doc);
    }

    [Theory]
    [InlineData("identity-null")]
    [InlineData("identity-negative")]
    [InlineData("directory-negative")]
    [InlineData("noncanonical")]
    [InlineData("payload-null")]
    [InlineData("payload-owner")]
    [InlineData("payload-negative")]
    [InlineData("payload-overflow")]
    [InlineData("payload-length")]
    [InlineData("header-negative")]
    [InlineData("entity-start")]
    [InlineData("entity-fields-null")]
    public void InvalidSourceCoordinatesCannotProduceAPrefix(string field)
    {
        var sample = Create(1); var frame = sample.Frame; var entity = sample.Entity;
        switch (field)
        {
            case "identity-null": frame = frame with { Identity = null! }; break;
            case "identity-negative": frame = frame with { Identity = frame.Identity with { Ordinal = -1 } }; break;
            case "directory-negative": frame = frame with { DirectoryIndex = -1 }; break;
            case "noncanonical": frame = frame with { Identity = frame.Identity with { MemberPath = "object" } }; break;
            case "payload-null": frame = frame with { PayloadExtent = null! }; break;
            case "payload-owner": frame = frame with { PayloadExtent = frame.PayloadExtent with { Container = new("other-index", 1) } }; break;
            case "payload-negative": frame = frame with { PayloadExtent = frame.PayloadExtent with { Offset = -1 } }; break;
            case "payload-overflow": frame = frame with { PayloadExtent = frame.PayloadExtent with { Offset = long.MaxValue - 1 } }; break;
            case "payload-length": frame = frame with { PayloadExtent = frame.PayloadExtent with { Length = frame.PayloadExtent.Length - 1 } }; break;
            case "header-negative": frame = frame with { HeaderExtent = frame.HeaderExtent with { Offset = -1 } }; break;
            case "entity-start": entity = entity with { NextClassOffset = entity.NextClassOffset + 1 }; break;
            case "entity-fields-null": entity = entity with { FieldExtents = null! }; break;
        }
        Assert.False(new NativeMeshPrefixReader().TryRead(frame, entity, out var doc, out _));
        Assert.Null(doc);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("offset")]
    [InlineData("width")]
    [InlineData("null")]
    [InlineData("missing")]
    public void SuppliedEntityFieldExtentsMustMatchTheSerializedRecord(string mutation)
    {
        var sample = Create(1); var fields = new Dictionary<string, DecodedExtent>(sample.Entity.FieldExtents);
        fields["Node"] = mutation switch
        {
            "owner" => fields["Node"] with { Container = new("other-index", 1) },
            "offset" => fields["Node"] with { Offset = fields["Node"].Offset + 1 },
            "width" => fields["Node"] with { Length = 4 },
            "null" => null!, _ => fields["Node"]
        };
        if (mutation == "missing") fields.Remove("Node");
        Assert.False(new NativeMeshPrefixReader().TryRead(sample.Frame, sample.Entity with { FieldExtents = fields }, out var doc, out _));
        Assert.Null(doc);
    }

    [Fact]
    public void NullArgumentsAndCancellationKeepTheirExistingExceptionContract()
    {
        var sample = Create(0); var reader = new NativeMeshPrefixReader();
        Assert.Throws<ArgumentNullException>(() => reader.TryRead(null!, sample.Entity, out _, out _));
        Assert.Throws<ArgumentNullException>(() => reader.TryRead(sample.Frame, null!, out _, out _));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.TryRead(sample.Frame, sample.Entity, out _, out _, cancel.Token));
    }

    private static byte[] Slice(Sample sample, DecodedExtent extent) => sample.Bytes.AsSpan(
        checked((int)(extent.Offset - sample.Frame.PayloadExtent.Offset)), checked((int)extent.Length)).ToArray();

    private static Sample Create(byte mode, int count = 2, byte[]? name = null)
    {
        name ??= "MESH____"u8.ToArray();
        var firstBlob = mode == 0 ? Array.Empty<byte>() : "SKELETON"u8.ToArray();
        var secondBlob = mode == 2 ? "NODEAR  "u8.ToArray() : [];
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write(5u); Tag(writer, "COM_CONT", 6); writer.Write(0u); Tag(writer, "LOADCONT", 2);
        Tag(writer, "ENTITY  ", 7); writer.Write(2138u); writer.Write(3u); writer.Write(new byte[24]); writer.Write(0u); writer.Write((byte)0);
        Tag(writer, "MESH____", 29); writer.Write(1u); writer.Write(2130u); writer.Write(4u); writer.Write(.5f);
        writer.Write(1u); writer.Write(2u); writer.Write(2202u); writer.Write(5u); writer.Write(2064u); writer.Write(6u);
        writer.Write(0x000601c1u); writer.Write(7u);
        var packedAt = (int)memory.Position; writer.Write((uint)mode << 24 | (uint)count);
        int firstNameAt = 0, controlAt = 0, secondaryAt = 0;
        for (var i = 0; i < count; i++)
        {
            if (i == 0) firstNameAt = (int)memory.Position;
            writer.Write((uint)name.Length); writer.Write(name); writer.Write(2202u); writer.Write((uint)(8 + i));
            if (mode != 0)
            {
                if (i == 0) controlAt = (int)memory.Position;
                writer.Write(i == 0 ? 0xa0000000u | (uint)firstBlob.Length : 0xa0000000u);
                if (mode == 2)
                { if (i == 0) secondaryAt = (int)memory.Position; writer.Write(i == 0 ? (uint)secondBlob.Length : 0u); }
            }
        }
        writer.Write(firstBlob); writer.Write(secondBlob);
        var extraAt = (int)memory.Position; writer.Write(1u); writer.Write(2202u); writer.Write(9u);
        writer.Write(0x80u); writer.Write((byte)0x7f); writer.Write(new byte[] { 1, 2, 3, 4, 5, 6 });
        foreach (var value in new uint[] { 11, 12, 13, 14, 15 }) writer.Write(value);
        var end = (int)memory.Position; Tag(writer, "SKELETON", 15); writer.Write(0u);
        var bytes = memory.ToArray(); var identity = new ResourceIdentity("generated-index", 1, "dc-object/0/2013/17");
        var owner = identity with { MemberPath = null };
        var frame = new NativeObjectFrame(identity, 0, 2013, 17, new(owner, 24, 8), new(owner, 100, 0),
            new(owner, 100, bytes.Length), [], bytes, 0, 0, 0, 5);
        Assert.True(new SceneEntityReader().TryRead(frame, out var entity, out var diagnostic), diagnostic);
        return new(frame, entity!, bytes, name, firstBlob, secondBlob, end, packedAt, firstNameAt, controlAt, secondaryAt, extraAt);
    }
    private static void Tag(BinaryWriter writer, string tag, uint version) { writer.Write(Encoding.ASCII.GetBytes(tag)); writer.Write(version); }
    private sealed record Sample(NativeObjectFrame Frame, SceneEntityRecord Entity, byte[] Bytes, byte[] Name,
        byte[] FirstBlob, byte[] SecondBlob, int End, int PackedOffset, int FirstNameLengthOffset,
        int ControlOffset, int SecondaryOffset, int ExtraCountOffset);
}
