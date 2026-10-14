using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class PhysicsDataStorageReaderTests
{
    [Fact]
    public void DirectMaskEightWidthWinsBeforeParentStop()
    {
        var input = Fixture.Create(currentMask: 9, parentSlot: 1, firstScalar: 24, count: 2, offset: 8);
        var result = new PhysicsDataStorageReader().Read(input.Layouts, input.Indexed);
        Assert.True(result.Value is not null, string.Join(";", result.Diagnostics));
        var row = Assert.Single(Assert.IsType<PhysicsDataStorageDocument>(result.Value).Rows, item => item.Item.Identity.RowOrdinal == 1);
        Assert.Equal(PhysicsDataStorageProjectionStatus.Direct, row.StorageWidth.Status);
        Assert.Equal((ushort)24, row.StorageWidth.ElementByteStride);
        Assert.Equal(PhysicsDataWindowStatus.WithinData, row.Window.Status);
        Assert.Equal(48, row.Window.ByteLength);
    }

    [Fact]
    public void ParentMaskEightWidthIsInherited()
    {
        var input = Fixture.Create(currentMask: 0, parentSlot: 1, firstScalar: 0, count: 2, offset: 8,
            includeParent: true, parentHasGate: true);
        var result = new PhysicsDataStorageReader().Read(input.Layouts, input.Indexed);
        var row = Assert.Single(Assert.IsType<PhysicsDataStorageDocument>(result.Value).Rows, item => item.Item.Identity.RowOrdinal == 1);
        Assert.Equal(PhysicsDataStorageProjectionStatus.Inherited, row.StorageWidth.Status);
        Assert.Equal((ushort)16, row.StorageWidth.ElementByteStride);
        Assert.Equal([2, 1], row.StorageWidth.Provenance!.ParentPath);
    }

    [Fact]
    public void ParentStopLeavesWidthUnresolved()
    {
        var input = Fixture.Create(currentMask: 1, parentSlot: 1, firstScalar: 0, count: 2, offset: 8,
            includeParent: true, parentHasGate: true);
        var result = new PhysicsDataStorageReader().Read(input.Layouts, input.Indexed);
        var row = Assert.Single(Assert.IsType<PhysicsDataStorageDocument>(result.Value).Rows, item => item.Item.Identity.RowOrdinal == 1);
        Assert.Equal(PhysicsDataStorageProjectionStatus.ParentStopped, row.StorageWidth.Status);
        Assert.Equal(PhysicsDataWindowStatus.NoStride, row.Window.Status);
    }

    [Fact]
    public void MissingParentIsExplicit()
    {
        var input = Fixture.Create(currentMask: 0, parentSlot: 9, firstScalar: 0, count: 1, offset: 0);
        var result = new PhysicsDataStorageReader().Read(input.Layouts, input.Indexed);
        var row = Assert.Single(Assert.IsType<PhysicsDataStorageDocument>(result.Value).Rows, item => item.Item.Identity.RowOrdinal == 1);
        Assert.Equal(PhysicsDataStorageProjectionStatus.MissingParent, row.StorageWidth.Status);
    }

    [Fact]
    public void ParentCycleIsExplicit()
    {
        var input = Fixture.Create(currentMask: 0, parentSlot: 1, firstScalar: 0, count: 1, offset: 0,
            includeParent: true, parentCycle: true);
        var result = new PhysicsDataStorageReader().Read(input.Layouts, input.Indexed);
        var row = Assert.Single(Assert.IsType<PhysicsDataStorageDocument>(result.Value).Rows, item => item.Item.Identity.RowOrdinal == 1);
        Assert.Equal(PhysicsDataStorageProjectionStatus.Cyclic, row.StorageWidth.Status);
    }

    [Fact]
    public void DuplicateMaskEightGatesAreAmbiguous()
    {
        var input = Fixture.Create(currentMask: 9, parentSlot: 1, firstScalar: 16, count: 1, offset: 0,
            duplicateGate: true);
        var result = new PhysicsDataStorageReader().Read(input.Layouts, input.Indexed);
        var row = Assert.Single(Assert.IsType<PhysicsDataStorageDocument>(result.Value).Rows, item => item.Item.Identity.RowOrdinal == 1);
        Assert.Equal(PhysicsDataStorageProjectionStatus.Ambiguous, row.StorageWidth.Status);
    }

    [Fact]
    public void LargeRawScalarKeepsRawValueAndProjectsLowUshort()
    {
        var input = Fixture.Create(9, 1, 0x12345, 1, 0);
        var result = new PhysicsDataStorageReader().Read(input.Layouts, input.Indexed);
        var row = Assert.Single(Assert.IsType<PhysicsDataStorageDocument>(result.Value).Rows, item => item.Item.Identity.RowOrdinal == 1);
        Assert.Equal((ulong)0x12345, row.StorageWidth.RawFirstScalar!.RawBits);
        Assert.Equal((ushort)0x2345, row.StorageWidth.ElementByteStride);
    }

    [Fact]
    public void PartialLayoutStopsWithAnExplicitDiagnostic()
    {
        var input = Fixture.Create(9, 1, 16, 1, 0);
        var result = new PhysicsDataStorageReader().Read(input.Layouts with
        {
            Coverage = PhysicsTypeLayoutCoverage.Partial,
            LayoutTableFullyVisited = false
        }, input.Indexed);
        Assert.Null(result.Value);
        Assert.Contains("PhysicsDataStorageLayoutsPartial", result.Diagnostics);
    }

    [Fact]
    public void ForgedRawIntegerIsRejectedBeforeProjection()
    {
        var input = Fixture.Create(9, 1, 16, 1, 0);
        var record = input.Layouts.Records[0];
        var gate = record.GatedValues[0];
        var forged = gate with { Fields = [gate.Fields[0] with { RawBits = gate.Fields[0].RawBits + 1 }, gate.Fields[1]] };
        var layouts = input.Layouts with { Records = [record with { GatedValues = [forged] }] };
        var result = new PhysicsDataStorageReader().Read(layouts, input.Indexed);
        Assert.Null(result.Value);
        Assert.Contains("PhysicsDataStorageLayoutFieldMismatch", result.Diagnostics);
    }

    [Fact]
    public void ForgedRawItemWordIsRejectedBeforeProjection()
    {
        var input = Fixture.Create(9, 1, 16, 1, 0);
        var row = input.Indexed.Items[1] with { RawWord1 = 99 };
        var indexed = input.Indexed with { Items = [input.Indexed.Items[0], row] };
        var result = new PhysicsDataStorageReader().Read(input.Layouts, indexed);
        Assert.Null(result.Value);
        Assert.Contains("PhysicsDataStorageItemRawMismatch", result.Diagnostics);
    }

    [Theory]
    [InlineData(-1, 2, PhysicsDataWindowStatus.NegativeOffset)]
    [InlineData(8, 0, PhysicsDataWindowStatus.NoPositiveCount)]
    [InlineData(90, 2, PhysicsDataWindowStatus.OutOfRange)]
    public void SignedWindowsHaveExplicitStatuses(int offset, int count, PhysicsDataWindowStatus expected)
    {
        var input = Fixture.Create(currentMask: 9, parentSlot: 1, firstScalar: 16, count, offset);
        var result = new PhysicsDataStorageReader().Read(input.Layouts, input.Indexed);
        var row = Assert.Single(Assert.IsType<PhysicsDataStorageDocument>(result.Value).Rows, item => item.Item.Identity.RowOrdinal == 1);
        Assert.Equal(expected, row.Window.Status);
    }

    [Fact]
    public void CancellationPropagatesWithoutPartialResult()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var input = Fixture.Create(9, 1, 16, 1, 0);
        Assert.Throws<OperationCanceledException>(() => new PhysicsDataStorageReader().Read(input.Layouts, input.Indexed, token: stop.Token));
    }

    [Fact]
    public void BudgetFailureReportsDiagnostic()
    {
        var input = Fixture.Create(9, 1, 16, 1, 0);
        var result = new PhysicsDataStorageReader().Read(input.Layouts, input.Indexed,
            new PhysicsDataStorageLimits(MaximumRetainedMetadataBytes: 1));
        Assert.Null(result.Value);
        Assert.Contains("PhysicsDataStorageMetadataLimit", result.Diagnostics);
    }

    private sealed record Input(PhysicsTypeLayoutsDocument Layouts, PhysicsIndexedMetadataDocument Indexed);

    private static class Fixture
    {
        private const string Index = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string EmbeddedHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private static readonly SourceRevision Revision = new(Index, "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210");
        private static readonly ResourceIdentity Container = new(Index, 17);

        public static Input Create(int currentMask, int parentSlot, int firstScalar, int count, int offset,
            bool includeParent = false, bool parentHasGate = false, bool parentCycle = false,
            bool duplicateGate = false)
        {
            var embedded = new DecodedExtent(Container, 100, 2000);
            var layoutBody = new byte[256];
            var fields = new List<(int Offset, byte Value)>();
            var slot = Integer(1, layoutBody, fields, 0);
            var parent = Integer(parentSlot, layoutBody, fields, 1);
            var mask = Integer(currentMask, layoutBody, fields, 2);
            var currentGate = currentMask is 9 ? Gate(layoutBody, fields, 3, firstScalar) : [];
            var current = Record(2, slot, parent, mask, currentGate, embedded, layoutBody, fields, 0);
            if (duplicateGate) current = current with { GatedValues = current.GatedValues.Concat(Gate(layoutBody, fields, 5, 17)).ToArray() };
            var records = new List<PhysicsTypeLayoutRecord> { current };
            if (includeParent)
            {
                var pFields = new List<(int Offset, byte Value)>();
                var pSlot = Integer(1, layoutBody, pFields, 32);
                var pParent = Integer(parentCycle ? 2 : 0, layoutBody, pFields, 33);
                var pMask = Integer(parentCycle ? 0 : parentHasGate ? 9 : 1, layoutBody, pFields, 34);
                var pGate = parentHasGate ? Gate(layoutBody, pFields, 35, 16) : [];
                records.Add(Record(1, pSlot, pParent, pMask, pGate, embedded, layoutBody, pFields, 1));
                current = current with { Parent = new(current.Parent.Index, PhysicsLayoutReferenceStatus.Unique,
                    [records[1].Identity]) };
                records[0] = current;
                if (parentCycle)
                {
                    var parentRecord = records[1];
                    records[1] = parentRecord with { Parent = new(parentRecord.Parent.Index,
                        PhysicsLayoutReferenceStatus.Unique, [records[0].Identity]) };
                }
            }
            var layoutBodyExtent = new DecodedExtent(Container, 200, layoutBody.Length);
            var layoutLeaf = LayoutLeaf(2, layoutBodyExtent, layoutBody);
            var layouts = new PhysicsTypeLayoutsDocument(new(Index, 17, "physics"), Revision, embedded, EmbeddedHash,
                1, 4, LayoutLeaf(1, new(Container, 120, 2), [0x01, 0x00]), layoutLeaf,
                [], records, true, PhysicsTypeLayoutCoverage.CompleteStructure, [], 1024);
            var itemBytes = new byte[24];
            BinaryPrimitives.WriteUInt32LittleEndian(itemBytes.AsSpan(12), 0x10000002);
            BinaryPrimitives.WriteUInt32LittleEndian(itemBytes.AsSpan(16), unchecked((uint)offset));
            BinaryPrimitives.WriteUInt32LittleEndian(itemBytes.AsSpan(20), unchecked((uint)count));
            var itemLeaf = Leaf(4, new DecodedExtent(Container, 500, itemBytes.Length), itemBytes);
            var layoutIdentity = records[0].Identity;
            var type = new PhysicsIndexedTypeReference(2, PhysicsIndexedReferenceStatus.Unique,
                new PhysicsTypeSlotIdentity(layouts.Identity, layouts.TypeChunkOrdinal, 2),
                [new PhysicsTypeSlotIdentity(layouts.Identity, layouts.TypeChunkOrdinal, 2)],
                PhysicsIndexedReferenceStatus.Unique, [layoutIdentity]);
            var reserved = new PhysicsIndexedItemRow(new(layouts.Identity, 3, 0), true,
                PhysicsIndexedItemProjection.ReservedRow, 0, 0, 0, 0, 0, 0,
                new(Container, 500, 4), new(Container, 504, 4), new(Container, 508, 4), null,
                new(0, PhysicsIndexedDataPositionStatus.NotProjected, 0, null), null, itemBytes[..12]);
            var row = new PhysicsIndexedItemRow(new(layouts.Identity, 3, 1), false,
                PhysicsIndexedItemProjection.TypeAndData, 0x10000002, unchecked((uint)offset), unchecked((uint)count), count,
                0x10000000, 0, new(Container, 512, 4), new(Container, 516, 4), new(Container, 520, 4), type,
                new(unchecked((uint)offset), PhysicsIndexedDataPositionStatus.WithinDataBody, offset,
                    new(Container, 600 + offset, 0)), null, itemBytes[12..24]);
            var indexed = new PhysicsIndexedMetadataDocument(layouts.Identity, Revision, embedded, EmbeddedHash, 1, 3, 4,
                new DecodedExtent(Container, 600, 100), PhysicsIndexedTableStatus.Complete, [itemLeaf],
                PhysicsIndexedTableStatus.Opaque, [], [reserved, row], null, true, true, [], PhysicsIndexedMetadataCoverage.CompleteStructure, 2048);
            return new(layouts, indexed);
        }

        private static PhysicsTypeLayoutRecord Record(int slotValue, PhysicsTypeInteger slot, PhysicsTypeInteger parent,
            PhysicsTypeInteger mask, IReadOnlyList<PhysicsLayoutGatedValue> gate, DecodedExtent embedded,
            byte[] body, List<(int Offset, byte Value)> fields, int ordinal) => new(
            new(new(Index, 17, "physics"), 1, 2, ordinal, slotValue),
            new(Container, 220 + ordinal * 40, 32), slot, new(parent, PhysicsLayoutReferenceStatus.NoLinkRequested, []), mask,
            gate, null, null, [], null, []);

        private static IReadOnlyList<PhysicsLayoutGatedValue> Gate(byte[] body, List<(int Offset, byte Value)> fields, int offset, int first)
        {
            var firstField = Integer(first, body, fields, offset);
            return [new(8, [firstField, Integer(2, body, fields, checked(offset + (int)firstField.Extent.Length))])];
        }

        private static PhysicsTypeInteger Integer(int value, byte[] body, List<(int Offset, byte Value)> fields, int offset)
        {
            var width = value <= 0x7f ? 1 : value <= 0x3fff ? 2 : value <= 0x1fffff ? 3 : 4;
            var encoding = new byte[width]; var raw = (uint)value;
            if (width == 1) encoding[0] = (byte)raw;
            else
            {
                var mask = width switch { 2 => 0x3f, 3 => 0x1f, _ => 7 };
                var prefix = width == 2 ? 0x80u : width == 3 ? 0xc0u : 0xe0u;
                encoding[0] = (byte)(prefix | ((raw >> (8 * (width - 1))) & (uint)mask));
                for (var index = 1; index < width; index++) encoding[index] = (byte)(raw >> (8 * (width - 1 - index)));
            }
            encoding.CopyTo(body.AsSpan(offset)); fields.Add((offset, encoding[0]));
            return new((ulong)(uint)value, value, new(Container, 200 + offset, width), encoding);
        }

        private static PhysicsIndexedMetadataLeaf Leaf(int chunk, DecodedExtent bodyExtent, byte[] bytes)
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return new(chunk, new(Container, bodyExtent.Offset - 8, 8), bodyExtent, 0, hash, true, new byte[8], bytes);
        }

        private static PhysicsTypeDeclarationLeaf LayoutLeaf(int chunk, DecodedExtent bodyExtent, byte[] bytes)
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return new(chunk, new(Container, bodyExtent.Offset - 8, 8), bodyExtent, 0, hash, new byte[8], bytes);
        }
    }
}
