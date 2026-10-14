using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class PhysicsIndexedMetadataReaderTests
{
    [Fact]
    public void RowsKeepRawBytesAndSeparateTypeDataAndItemReferenceDomains()
    {
        var row0 = Row(0x2a000003, 17, 1);
        var row1 = Row(0x1b000001, 12, uint.MaxValue);
        var row2 = Row(0x20000002, 99, 0);
        var row3 = Row(0x0f000005, 64, 1);
        var input = Fixture(Join(row0, row1, row2, row3), [2, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0]);
        var result = Read(input);
        var document = Assert.IsType<PhysicsIndexedMetadataDocument>(result.Value);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(PhysicsIndexedMetadataCoverage.CompleteStructure, document.Coverage);
        Assert.Equal(4, document.Items.Count);

        var reserved = document.Items[0];
        Assert.True(reserved.IsReservedRow);
        Assert.Equal(PhysicsIndexedItemProjection.ReservedRow, reserved.Projection);
        Assert.Equal(row0, reserved.RawBytes.ToArray());
        Assert.Equal(0x2a000003u, reserved.RawWord0);
        Assert.Null(reserved.Type);
        Assert.Null(reserved.Redirect);
        Assert.Equal(PhysicsIndexedDataPositionStatus.NotProjected, reserved.DataPosition.Status);

        var dataType = document.Items[1];
        var dataTypeRef = dataType.Type!;
        Assert.Equal(0x0b000000u, dataType.UnknownWord0Bits);
        Assert.Equal(PhysicsIndexedItemProjection.TypeAndData, dataType.Projection);
        Assert.Equal(PhysicsIndexedReferenceStatus.Unique, dataTypeRef.DeclarationStatus);
        Assert.Equal(1, dataTypeRef.DeclarationTarget!.Slot);
        Assert.Equal(PhysicsIndexedReferenceStatus.Unique, dataTypeRef.LayoutStatus);
        Assert.Equal(1, Assert.Single(dataTypeRef.LayoutCandidates).Slot);
        Assert.Equal(PhysicsIndexedDataPositionStatus.WithinDataBody, dataType.DataPosition.Status);
        Assert.Equal(12L, dataType.DataPosition.DataRelativeOffset!.Value);
        Assert.Equal(document.DataBodyExtent!.Offset + 12, dataType.DataPosition.Position!.Offset);
        Assert.Equal(-1, dataType.Word2NativeInt32);

        var typeOnly = document.Items[2];
        var typeOnlyRef = typeOnly.Type!;
        Assert.Equal(PhysicsIndexedItemProjection.TypeOnly, typeOnly.Projection);
        Assert.Equal(2, typeOnlyRef.DeclarationTarget!.Slot);
        Assert.Equal(PhysicsIndexedReferenceStatus.Unique, typeOnlyRef.LayoutStatus);
        Assert.Equal(PhysicsIndexedDataPositionStatus.NoDataPointer, typeOnly.DataPosition.Status);
        Assert.Equal(99u, typeOnly.RawWord1);

        var redirect = document.Items[3];
        Assert.Equal(PhysicsIndexedItemProjection.Kind0RowReference, redirect.Projection);
        Assert.Null(redirect.Type);
        Assert.Equal(PhysicsIndexedDataPositionStatus.NotProjected, redirect.DataPosition.Status);
        Assert.Equal(PhysicsIndexedReferenceStatus.Unique, redirect.Redirect!.Status);
        Assert.Equal(1, redirect.Redirect.Target!.RowOrdinal);

        Assert.Equal(PhysicsIndexedTableStatus.Opaque, document.PatchTableStatus);
        Assert.Equal(12, document.PatchTables[0].BodyExtent.Length);
    }

    [Fact]
    public void InitialDeclarationSlotAndItemRowZeroStayDistinct()
    {
        var row0 = Row(0x10000003, 0x12345678, 0x76543210);
        var row1 = Row(0x10000000, 0, 7);
        var document = Read(Fixture(Join(row0, row1))).Value!;
        Assert.True(document.Items[0].IsReservedRow);
        Assert.Equal(row0, document.Items[0].RawBytes.ToArray());
        Assert.Null(document.Items[0].Type);

        var item = document.Items[1];
        var typeRef = item.Type!;
        Assert.Equal(PhysicsIndexedReferenceStatus.InitialSlot, item.Type!.DeclarationStatus);
        Assert.Equal(0, typeRef.DeclarationTarget!.Slot);
        Assert.Equal(PhysicsIndexedReferenceStatus.NoLayoutForInitialSlot, typeRef.LayoutStatus);
        Assert.Equal(PhysicsIndexedDataPositionStatus.WithinDataBody, item.DataPosition.Status);
        Assert.Null(item.Redirect);
    }

    [Fact]
    public void SpecialAndUnsupportedKindsKeepTheirWordsAndUnknownBits()
    {
        var rows = Join(Row(0xa1000003, 12, 4), Row(0x3b000003, 128, 11),
            Row(0x4a000001, 45, 7), Row(0x50000002, 46, uint.MaxValue), Row(0x60000001, 47, 9));
        var result = Read(Fixture(rows)); var document = result.Value!;
        var counted = document.Items[1];
        Assert.Equal(PhysicsIndexedItemProjection.TypeAndDataWithAuxiliary, counted.Projection);
        Assert.Equal(11u, counted.RawWord2);
        Assert.Equal(PhysicsIndexedReferenceStatus.Unique, counted.Type!.DeclarationStatus);

        var descriptor4 = document.Items[2];
        Assert.Equal(PhysicsIndexedItemProjection.SpecialDescriptor4, descriptor4.Projection);
        Assert.Equal(0x0a000000u, descriptor4.UnknownWord0Bits);
        Assert.Equal(PhysicsIndexedDataPositionStatus.SpecialDescriptor, descriptor4.DataPosition.Status);
        Assert.Equal(7u, descriptor4.RawWord2);

        var descriptor5 = document.Items[3];
        Assert.Equal(PhysicsIndexedItemProjection.SpecialDescriptor5, descriptor5.Projection);
        Assert.Equal(-1, descriptor5.Word2NativeInt32);
        Assert.Equal(PhysicsIndexedReferenceStatus.Unique, descriptor5.Type!.DeclarationStatus);

        var unknown = document.Items[4];
        Assert.Equal(PhysicsIndexedItemProjection.UnsupportedKind, unknown.Projection);
        Assert.Equal(0x60000000u, unknown.RawKindNibble);
        Assert.Null(unknown.Type);
        Assert.Equal(PhysicsIndexedDataPositionStatus.UnsupportedKind, unknown.DataPosition.Status);
        Assert.Contains("PhysicsIndexedItemKindUnsupported", result.Diagnostics);
    }

    [Fact]
    public void DataOffsetsArePositionsAndNeverCarryAnInferredLength()
    {
        var document = Read(Fixture(Join(Row(0, 0, 0), Row(0x10000001, 128, 0),
            Row(0x10000002, 129, 0), Row(0x10000003, uint.MaxValue, 0)), data: new byte[128])).Value!;
        Assert.Equal(PhysicsIndexedDataPositionStatus.AtDataEnd, document.Items[1].DataPosition.Status);
        Assert.Equal(128L, document.Items[1].DataPosition.DataRelativeOffset!.Value);
        Assert.Equal(0, document.Items[1].DataPosition.Position!.Length);
        Assert.Equal(PhysicsIndexedDataPositionStatus.OutOfRange, document.Items[2].DataPosition.Status);
        Assert.Null(document.Items[2].DataPosition.Position);
        Assert.Equal(PhysicsIndexedDataPositionStatus.OutOfRange, document.Items[3].DataPosition.Status);
        Assert.Null(document.Items[3].DataPosition.Position);
        Assert.Contains("PhysicsIndexedDataOffsetOutOfRange", Read(Fixture(Join(Row(0, 0, 0), Row(0x10000002, 129, 0)), data: new byte[128])).Diagnostics);
    }

    [Fact]
    public void KindZeroRedirectsAreSingleHopAndBounded()
    {
        var rows = Join(Row(0x20000003, 8, 9), Row(0x00000005, 0, 2), Row(0x00000006, 0, 1),
            Row(0x00000007, 0, unchecked((uint)-1)), Row(0x00000008, 0, 99), Row(0x00000009, 0, 0));
        var result = Read(Fixture(rows)); var document = result.Value!;
        Assert.Null(document.Items[1].Type);
        Assert.Equal(2, document.Items[1].Redirect!.Target!.RowOrdinal);
        Assert.Equal(PhysicsIndexedReferenceStatus.Unique, document.Items[2].Redirect!.Status);
        Assert.Equal(1, document.Items[2].Redirect!.Target!.RowOrdinal);
        Assert.Equal(PhysicsIndexedReferenceStatus.Invalid, document.Items[3].Redirect!.Status);
        Assert.Equal(PhysicsIndexedReferenceStatus.OutOfRange, document.Items[4].Redirect!.Status);
        Assert.Equal(PhysicsIndexedReferenceStatus.NoRedirect, document.Items[5].Redirect!.Status);
        Assert.Contains("PhysicsIndexedItemRedirectInvalid", result.Diagnostics);
    }

    [Fact]
    public void CompleteLayoutLookupDistinguishesMissingAndAmbiguousFromPartial()
    {
        var partialInput = Fixture(Join(Row(0, 0, 0), Row(0x10000001, 1, 0)));
        var partialLayout = partialInput.Layouts with { LayoutTableFullyVisited = false };
        var partialReference = Read(partialInput with { Layouts = partialLayout }).Value!.Items[1].Type!;
        Assert.Equal(PhysicsIndexedReferenceStatus.Unresolved, partialReference.LayoutStatus);
        Assert.Equal(1, Assert.Single(partialReference.LayoutCandidates).Slot);

        var item = Join(Row(0, 0, 0), Row(0x10000002, 1, 0));
        var missingInput = Fixture(item, layoutBody: Join(RowLayout(1), RowLayout(3)));
        var missing = Read(missingInput).Value!.Items[1].Type!;
        Assert.Equal(PhysicsIndexedReferenceStatus.Unique, missing.DeclarationStatus);
        Assert.Equal(PhysicsIndexedReferenceStatus.Missing, missing.LayoutStatus);

        var duplicateInput = Fixture(item, layoutBody: Join(RowLayout(1), RowLayout(2), RowLayout(2)));
        var duplicate = Read(duplicateInput).Value!.Items[1].Type!;
        Assert.Equal(PhysicsIndexedReferenceStatus.Ambiguous, duplicate.LayoutStatus);
        Assert.Equal(2, duplicate.LayoutCandidates.Count);

        var partial = duplicateInput.Layouts with { LayoutTableFullyVisited = false };
        var unresolved = Read(duplicateInput with { Layouts = partial }).Value!.Items[1].Type!;
        Assert.Equal(PhysicsIndexedReferenceStatus.Ambiguous, unresolved.LayoutStatus);
        Assert.Equal(2, unresolved.LayoutCandidates.Count);
    }

    [Fact]
    public void PartialDeclarationSetKeepsKnownCandidateUnresolved()
    {
        var input = Fixture(Join(Row(0, 0, 0), Row(0x10000001, 4, 0)));
        var partialDeclarations = input.Declarations with { Declarations = input.Declarations.Declarations.Take(1).ToArray() };
        var document = Read(input with { Declarations = partialDeclarations }).Value!;
        var reference = document.Items[1].Type!;
        Assert.Equal(PhysicsIndexedReferenceStatus.Unresolved, reference.DeclarationStatus);
        Assert.Equal(1, reference.DeclarationTarget!.Slot);
        Assert.Single(reference.DeclarationCandidates);
        Assert.Equal(PhysicsIndexedReferenceStatus.Unique, reference.LayoutStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LayoutSlotProjectionMustMatchItsEncodedValue(bool changeRawBits)
    {
        var input = Fixture(Join(Row(0, 0, 0), Row(0x10000002, 1, 0), Row(0x10000001, 1, 0)),
            layoutBody: Join(RowLayout(1), RowLayout(3)));
        var rows = input.Layouts.Records.ToArray();
        rows[0] = rows[0] with
        {
            Identity = rows[0].Identity with { Slot = 2 },
            SlotIndex = rows[0].SlotIndex with
            {
                NativeInt32 = 2,
                RawBits = changeRawBits ? 2UL : rows[0].SlotIndex.RawBits
            }
        };

        var result = Read(input with { Layouts = input.Layouts with { Records = rows } });
        Assert.Contains("PhysicsIndexedLayoutSetIncomplete", result.Diagnostics);
        Assert.All(result.Value!.Items.Skip(1), row =>
        {
            Assert.Equal(PhysicsIndexedReferenceStatus.Unresolved, row.Type!.LayoutStatus);
            Assert.Empty(row.Type.LayoutCandidates);
        });
        Assert.Empty(result.Value.Items[1].Type!.LayoutCandidates);
    }

    [Fact]
    public void LayoutSlotEncodingCannotIncludeBytesFromTheNextField()
    {
        var input = Fixture(Join(Row(0, 0, 0), Row(0x10000001, 1, 0)));
        var rows = input.Layouts.Records.ToArray();
        var slot = rows[0].SlotIndex;
        rows[0] = rows[0] with
        {
            SlotIndex = slot with
            {
                Extent = slot.Extent with { Length = 2 },
                RawEncoding = input.Layouts.LayoutTable.RawBody[..2]
            }
        };

        var result = Read(input with { Layouts = input.Layouts with { Records = rows } });
        Assert.Contains("PhysicsIndexedLayoutSetIncomplete", result.Diagnostics);
        Assert.Equal(PhysicsIndexedReferenceStatus.Unresolved, result.Value!.Items[1].Type!.LayoutStatus);
        Assert.Empty(result.Value.Items[1].Type!.LayoutCandidates);
    }

    [Fact]
    public void PartialIndxFramingKeepsFoundRedirectCandidateUnresolved()
    {
        var input = Fixture(Join(Row(0, 0, 0), Row(0, 0, 2), Row(0, 0, 0)));
        var index = input.Metadata.Chunks.Single(chunk => chunk.Tag == "INDX");
        var partialMetadata = input.Metadata with
        {
            Coverage = PhysicsTaggedMetadataCoverage.Partial,
            UnknownRanges = [new(index.BodyExtent.Container, index.BodyExtent.Offset, 1)]
        };
        var document = Read(input with { Metadata = partialMetadata }).Value!;
        Assert.False(document.IndexTablesFullyVisited);
        var link = document.Items[1].Redirect!;
        Assert.Equal(PhysicsIndexedReferenceStatus.Unresolved, link.Status);
        Assert.Equal(2, link.Target!.RowOrdinal);
        Assert.Single(link.Candidates);
        Assert.DoesNotContain("PhysicsIndexedItemRedirectInvalid", Read(input with { Metadata = partialMetadata }).Diagnostics);
    }

    [Fact]
    public void LayoutRecordOrdinalsFollowTheEncodedSpanOrder()
    {
        var input = Fixture(Join(Row(0, 0, 0), Row(0x10000001, 1, 0), Row(0x10000002, 1, 0)));
        var rows = input.Layouts.Records.ToArray();
        var first = rows[0];
        rows[0] = rows[1] with { Identity = rows[1].Identity with { RecordOrdinal = 0 } };
        rows[1] = first with { Identity = first.Identity with { RecordOrdinal = 1 } };

        var result = Read(input with { Layouts = input.Layouts with { Records = rows } });
        Assert.Contains("PhysicsIndexedLayoutSetIncomplete", result.Diagnostics);
        Assert.All(result.Value!.Items.Skip(1), row =>
        {
            Assert.Equal(PhysicsIndexedReferenceStatus.Unresolved, row.Type!.LayoutStatus);
            Assert.Empty(row.Type.LayoutCandidates);
        });
    }

    [Fact]
    public void OmittedLayoutRowsCannotEstablishMissingTargets()
    {
        var input = Fixture(Join(Row(0, 0, 0), Row(0x10000003, 1, 0)));
        var result = Read(input with
        {
            Layouts = input.Layouts with { Records = input.Layouts.Records.Take(2).ToArray() }
        });
        Assert.Contains("PhysicsIndexedLayoutSetIncomplete", result.Diagnostics);
        Assert.Equal(PhysicsIndexedReferenceStatus.Unresolved, result.Value!.Items[1].Type!.LayoutStatus);
    }

    [Fact]
    public void TerminalZeroTailPreservesCompletedLayoutLookup()
    {
        var input = Fixture(Join(Row(0, 0, 0), Row(0x10000001, 1, 0), Row(0x10000002, 1, 0)),
            layoutBody: Join(RowLayout(1), [0, 0x7f, 0xa5]));
        Assert.True(input.Layouts.LayoutTableFullyVisited);
        var result = Read(input);
        Assert.Equal(PhysicsIndexedReferenceStatus.Unique, result.Value!.Items[1].Type!.LayoutStatus);
        Assert.Equal(PhysicsIndexedReferenceStatus.Missing, result.Value.Items[2].Type!.LayoutStatus);

        var withoutTail = Read(input with { Layouts = input.Layouts with { UnknownRanges = [] } });
        Assert.Equal(PhysicsIndexedReferenceStatus.Unresolved, withoutTail.Value!.Items[1].Type!.LayoutStatus);
        Assert.Equal(PhysicsIndexedReferenceStatus.Unresolved, withoutTail.Value.Items[2].Type!.LayoutStatus);
    }

    [Fact]
    public void PartialItemTailIsSeparateFromCompleteRows()
    {
        var body = Join(Row(0, 7, 8), Row(0x10000001, 12, 0), Row(0, 0, 1), [0xaa, 0xbb]);
        var document = Read(Fixture(body)).Value!;
        Assert.Equal(3, document.Items.Count);
        Assert.True(document.ItemRowsFullyVisited);
        Assert.Equal(PhysicsIndexedTableStatus.Partial, document.ItemTableStatus);
        Assert.Equal(2L, document.ItemRemainder!.Length);
        Assert.Equal(Assert.Single(document.ItemTables).BodyExtent.Offset + 36, document.ItemRemainder!.Offset);
        Assert.Equal(PhysicsIndexedReferenceStatus.Unique, document.Items[2].Redirect!.Status);
    }

    [Fact]
    public void OwnedLeavesExcludeDataAndOmitRawBytesFromJson()
    {
        var input = Fixture(Join(Row(0, 0, 0), Row(0x10000001, 12, 0)), [2, 0, 0, 0], new byte[128]);
        var document = Read(input).Value!;
        var source = input.Metadata.Chunks.Single(chunk => chunk.Tag == "ITEM").MetadataBody!.Value;
        Assert.True(MemoryMarshal.TryGetArray(source, out var borrowed));
        Assert.True(MemoryMarshal.TryGetArray(document.ItemTables[0].RawBody, out var owned));
        Assert.NotSame(borrowed.Array, owned.Array);
        var dataChunk = input.Metadata.Chunks.Single(chunk => chunk.Tag == "DATA");
        Assert.Null(dataChunk.MetadataBody);
        Assert.Equal(128L, document.DataBodyExtent!.Length);
        borrowed.Array![borrowed.Offset] = 255;
        Assert.Equal(0, document.ItemTables[0].RawBody.Span[0]);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(document));
        Assert.False(json.RootElement.GetProperty("ItemTables")[0].TryGetProperty("RawBody", out _));
        Assert.False(json.RootElement.GetProperty("Items")[0].TryGetProperty("RawBytes", out _));
        Assert.False(json.RootElement.GetProperty("PatchTables")[0].TryGetProperty("RawBody", out _));
    }

    [Fact]
    public void ContextMismatchAndBudgetsStayBounded()
    {
        var input = Fixture(Join(Row(0, 0, 0), Row(0x10000001, 12, 0)));
        var foreign = input.Declarations with { Revision = new("foreign-index", "other-revision") };
        var foreignResult = Read(input with { Declarations = foreign });
        Assert.Equal(PhysicsIndexedReferenceStatus.Unresolved, foreignResult.Value!.Items[1].Type!.DeclarationStatus);

        var limited = Read(input, new(MaximumItemRows: 0));
        Assert.Empty(limited.Value!.Items);
        Assert.Equal(input.Metadata.Chunks.Single(chunk => chunk.Tag == "ITEM").BodyExtent, Assert.Single(limited.Value.UnknownRanges));
        Assert.Contains("PhysicsIndexedItemRowLimit", limited.Diagnostics);

        var tiny = Read(input, new(MaximumMetadataBytes: 1));
        Assert.Null(tiny.Value);
        Assert.Contains("PhysicsIndexedContextLimit", tiny.Diagnostics);
    }

    [Fact]
    public void CollectionCountCancellationIsObserved()
    {
        var input = Fixture(Join(Row(0, 0, 0), Row(0x10000001, 12, 0)));
        using var stop = new CancellationTokenSource();
        var extents = new CallbackList<SourceExtent>(1, _ => TaggedFixture.Physical[0], () => { stop.Cancel(); return 1; });
        Assert.Throws<OperationCanceledException>(() => new PhysicsIndexedMetadataReader().Read(
            input.Metadata, input.Declarations, input.Layouts, extents, token: stop.Token));
    }

    private static Input Fixture(byte[] itemBody, byte[]? patchBody = null, byte[]? data = null, byte[]? layoutBody = null)
    {
        var declarations = Join([4, 0, 0, 1, 0, 2, 0]);
        var type = TaggedFixture.Chunk("TYPE", 0, Join(
            TaggedFixture.Chunk("TSTR", 1, Encoding.ASCII.GetBytes("Root\0Child\0Leaf\0")),
            TaggedFixture.Chunk("TNA1", 1, declarations),
            TaggedFixture.Chunk("FSTR", 1, Encoding.ASCII.GetBytes("member\0")),
            TaggedFixture.Chunk("TBDY", 1, layoutBody ?? [1, 0, 0, 2, 0, 0, 3, 0, 0])));
        var index = TaggedFixture.Chunk("INDX", 0, Join(
            TaggedFixture.Chunk("ITEM", 1, itemBody), TaggedFixture.Chunk("PTCH", 1, patchBody ?? [2, 0, 0, 0])));
        var envelope = TaggedFixture.Envelope(TaggedFixture.Root(TaggedFixture.Chunk("DATA", 1, data ?? new byte[128]), type, index));
        var metadata = new PhysicsTaggedMetadataReader().Read(envelope, TaggedFixture.Physical).Value!;
        var declarationDoc = new PhysicsTypeDeclarationsReader().Read(metadata, TaggedFixture.Physical).Value!;
        var layoutDoc = new PhysicsTypeLayoutsReader().Read(metadata, declarationDoc, TaggedFixture.Physical).Value!;
        return new(metadata, declarationDoc, layoutDoc);
    }

    private static DecodeResult<PhysicsIndexedMetadataDocument> Read(Input input, PhysicsIndexedMetadataLimits? limits = null) =>
        new PhysicsIndexedMetadataReader().Read(input.Metadata, input.Declarations, input.Layouts, TaggedFixture.Physical, limits: limits);

    private static byte[] Row(uint word0, uint word1, uint word2)
    {
        var bytes = new byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, word0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), word1); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), word2);
        return bytes;
    }
    private static byte[] RowLayout(byte slot) => [slot, 0, 0];
    private static byte[] Join(params byte[][] bytes) => bytes.SelectMany(value => value).ToArray();
    private sealed record Input(PhysicsTaggedMetadataDocument Metadata, PhysicsTypeDeclarationsDocument Declarations,
        PhysicsTypeLayoutsDocument Layouts);
    private sealed class CallbackList<T>(int count, Func<int, T> read, Func<int>? getCount = null) : IReadOnlyList<T>
    {
        public int Count => getCount?.Invoke() ?? count;
        public T this[int index] => read(index);
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
