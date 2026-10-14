using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public sealed class PhysicsDataStorageReader
{
    public const string Version = "physics-data-storage-v1";
    private const byte StoredMaskBit = 8;
    private const uint NativeStorageMask = 0x800000;

    public DecodeResult<PhysicsDataStorageDocument> Read(PhysicsTypeLayoutsDocument layouts,
        PhysicsIndexedMetadataDocument indexed, PhysicsDataStorageLimits? limits = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(layouts); ArgumentNullException.ThrowIfNull(indexed);
        token.ThrowIfCancellationRequested(); limits ??= new(); ValidateLimits(limits);
        DecodeResult<PhysicsDataStorageDocument> Early(string code) => new(null, indexed.Identity,
            indexed.Revision, [], [Step()], [code], [], new Dictionary<string, string>());
        if (layouts.Identity != indexed.Identity || layouts.Revision != indexed.Revision ||
            layouts.EmbeddedExtent != indexed.EmbeddedExtent ||
            !string.Equals(layouts.InheritedEmbeddedSha256, indexed.InheritedEmbeddedSha256, StringComparison.Ordinal) ||
            layouts.TypeChunkOrdinal != indexed.TypeChunkOrdinal || layouts.FinalDeclarationSlots != indexed.FinalDeclarationSlots)
            return Early("PhysicsDataStorageSourceMismatch");
        if (!IsSha256(layouts.Identity.IndexContentId) || !IsSha256(layouts.Revision.IndexContentId) ||
            !IsSha256(layouts.Revision.PhysicalSourceStamp) || !IsSha256(layouts.InheritedEmbeddedSha256))
            return Early("PhysicsDataStorageRevisionHashInvalid");
        if (indexed.DataBodyExtent is not { } dataBody || !Inside(dataBody, indexed.EmbeddedExtent))
            return Early("PhysicsDataStorageDataExtentInvalid");
        if (layouts.Records.Count > limits.MaximumTypeRecords || indexed.Items.Count > limits.MaximumItemRows ||
            layouts.LayoutTable.RawBody.Length > limits.MaximumLeafBytes || indexed.ItemTables.Count != 1 ||
            indexed.ItemTables[0].RawBody.Length > limits.MaximumLeafBytes)
            return Early("PhysicsDataStorageContextLimit");
        var preflightEstimate = checked(4096L + layouts.Records.Count * 256L + indexed.Items.Count * 256L);
        if (layouts.Records.Count * 16L > limits.MaximumLayoutFields || preflightEstimate > limits.MaximumRetainedMetadataBytes)
            return Early("PhysicsDataStorageMetadataLimit");
        if (!Inside(layouts.LayoutTable.BodyExtent, layouts.EmbeddedExtent) ||
            !Inside(indexed.ItemTables[0].BodyExtent, indexed.EmbeddedExtent)) return Early("PhysicsDataStorageExtentMismatch");
        if (!ValidLeaf(layouts.LayoutTable) || indexed.ItemTableStatus != PhysicsIndexedTableStatus.Complete ||
            !ValidLeaf(indexed.ItemTables[0])) return Early("PhysicsDataStorageMetadataHashMismatch");
        if (!indexed.ItemRowsFullyVisited || indexed.ItemRemainder is not null)
            return Early("PhysicsDataStorageIndexedRowsPartial");
        if (!layouts.LayoutTableFullyVisited || (layouts.Coverage != PhysicsTypeLayoutCoverage.CompleteStructure &&
            !UnknownTailOnly(layouts)))
            return Early("PhysicsDataStorageLayoutsPartial");
        if (!ValidateLayoutMetadata(layouts, limits, token)) return Early("PhysicsDataStorageLayoutFieldMismatch");

        var records = layouts.Records.GroupBy(record => record.Identity)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var maximumParentPath = MaximumParentPath(records, layouts, limits, token);
        var estimate = checked(4096L + layouts.Records.Count * 256L +
            indexed.Items.Count * (256L + maximumParentPath * 16L));
        if (estimate > limits.MaximumRetainedMetadataBytes) return Early("PhysicsDataStorageMetadataLimit");
        var rows = new List<PhysicsDataStorageRowProjection>(indexed.Items.Count);
        for (var index = 0; index < indexed.Items.Count; index++)
        {
            token.ThrowIfCancellationRequested(); var item = indexed.Items[index];
            if (item.Identity.Owner != indexed.Identity || item.Identity.IndexChunkOrdinal != indexed.IndexChunkOrdinal ||
                item.Identity.RowOrdinal != index || item.RawBytes.Length != 12 ||
                item.Word0Extent.Length != 4 || item.Word1Extent.Length != 4 || item.Word2Extent.Length != 4 ||
                item.Word1Extent.Offset != item.Word0Extent.Offset + 4 || item.Word2Extent.Offset != item.Word0Extent.Offset + 8 ||
                !Inside(item.Word0Extent, indexed.ItemTables[0].BodyExtent) ||
                !Inside(item.Word1Extent, indexed.ItemTables[0].BodyExtent) ||
                !Inside(item.Word2Extent, indexed.ItemTables[0].BodyExtent)) return Early("PhysicsDataStorageItemIdentityMismatch");
            var local = checked((int)(item.Word0Extent.Offset - indexed.ItemTables[0].BodyExtent.Offset));
            if (local < 0 || local > indexed.ItemTables[0].RawBody.Length - 12 ||
                !indexed.ItemTables[0].RawBody.Span.Slice(local, 12).SequenceEqual(item.RawBytes.Span)) return Early("PhysicsDataStorageItemRawMismatch");
            var raw0 = BinaryPrimitives.ReadUInt32LittleEndian(item.RawBytes.Span);
            var raw1 = BinaryPrimitives.ReadUInt32LittleEndian(item.RawBytes.Span[4..]);
            var raw2 = BinaryPrimitives.ReadUInt32LittleEndian(item.RawBytes.Span[8..]);
            if (raw0 != item.RawWord0 || raw1 != item.RawWord1 || raw2 != item.RawWord2 ||
                unchecked((int)raw2) != item.Word2NativeInt32 || (raw0 & 0xf0000000) != item.RawKindNibble ||
                (raw0 & 0x0f000000) != item.UnknownWord0Bits) return Early("PhysicsDataStorageItemRawMismatch");
            if (item.Type is { } type)
            {
                var slot = raw0 & 0x00ffffff;
                if (type.RawSlot != slot || type.DeclarationCandidates.Any(candidate => candidate.Owner != layouts.Identity ||
                    candidate.TypeChunkOrdinal != layouts.TypeChunkOrdinal || candidate.Slot != slot) ||
                    type.LayoutCandidates.Any(candidate => candidate.Owner != layouts.Identity ||
                    candidate.TypeChunkOrdinal != layouts.TypeChunkOrdinal || candidate.Slot != slot))
                    return Early("PhysicsDataStorageTypeProjectionMismatch");
            }
            var storage = ProjectStorage(item, records, layouts, limits, token);
            rows.Add(new(item, storage, ProjectWindow(item, storage, dataBody)));
        }
        var retained = estimate;
        return new(new(indexed.Identity, indexed.Revision, indexed.EmbeddedExtent, dataBody, rows, indexed.Coverage, retained)
            { LayoutCoverage = layouts.Coverage },
            indexed.Identity, indexed.Revision, [], [Step()], [], [], new Dictionary<string, string>());
    }

    private static PhysicsDataStorageProjection ProjectStorage(PhysicsIndexedItemRow item,
        IReadOnlyDictionary<PhysicsLayoutRecordIdentity, PhysicsTypeLayoutRecord[]> records,
        PhysicsTypeLayoutsDocument layouts, PhysicsDataStorageLimits limits, CancellationToken token)
    {
        if (item.IsReservedRow || item.Type is null) return new(PhysicsDataStorageProjectionStatus.NotProjected,
            NativeStorageMask, StoredMaskBit, null, null, null);
        if (item.Type.LayoutStatus != PhysicsIndexedReferenceStatus.Unique || item.Type.LayoutCandidates.Count != 1)
            return new(item.Type.LayoutStatus == PhysicsIndexedReferenceStatus.Ambiguous ? PhysicsDataStorageProjectionStatus.Ambiguous : PhysicsDataStorageProjectionStatus.MissingParent,
                NativeStorageMask, StoredMaskBit, null, null, null, "ITEM layout target is not unique.");
        var candidate = item.Type.LayoutCandidates[0];
        if (candidate.Owner != layouts.Identity || candidate.TypeChunkOrdinal != layouts.TypeChunkOrdinal || !records.TryGetValue(candidate, out var candidates))
            return new(PhysicsDataStorageProjectionStatus.Invalid, NativeStorageMask, StoredMaskBit, null, null, null, "ITEM layout target is outside the table.");
        if (candidates.Length != 1) return new(PhysicsDataStorageProjectionStatus.Ambiguous, NativeStorageMask, StoredMaskBit, null, null, null, "ITEM layout identity is duplicated.");
        var record = candidates[0]; var path = new List<int>(); var visited = new HashSet<PhysicsLayoutRecordIdentity>(); var inherited = false;
        for (var depth = 0; depth <= limits.MaximumInheritanceSteps; depth++)
        {
            token.ThrowIfCancellationRequested(); if (!visited.Add(record.Identity)) return new(PhysicsDataStorageProjectionStatus.Cyclic, NativeStorageMask, StoredMaskBit, null, null, null, "Parent cycle.");
            path.Add(record.Identity.Slot);
            var gates = record.GatedValues.Where(value => value.MaskBit == StoredMaskBit).ToArray();
            if (gates.Length > 1) return new(PhysicsDataStorageProjectionStatus.Ambiguous, NativeStorageMask, StoredMaskBit, null, null, null, "Duplicate mask-bit-8 gate.");
            if (gates.Length == 1)
            {
                if (gates[0].Fields.Count != 2) return new(PhysicsDataStorageProjectionStatus.Invalid, NativeStorageMask, StoredMaskBit, null, null, null, "Malformed mask-bit-8 gate.");
                var first = gates[0].Fields[0];
                var source = new PhysicsDataStorageFieldSource(layouts.Identity, layouts.TypeChunkOrdinal, layouts.LayoutTable.ChunkOrdinal,
                    record.Identity.RecordOrdinal, record.Identity.Slot, StoredMaskBit, NativeStorageMask, first, inherited, path.ToArray(), (record.Mask.RawBits & 1) != 0);
                return new(inherited ? PhysicsDataStorageProjectionStatus.Inherited : PhysicsDataStorageProjectionStatus.Direct,
                    NativeStorageMask, StoredMaskBit, first, (ushort)first.RawBits, source);
            }
            if ((record.Mask.RawBits & 1) != 0) return new(PhysicsDataStorageProjectionStatus.ParentStopped, NativeStorageMask, StoredMaskBit, null, null, null, "Parent stop flag.");
            if (depth == limits.MaximumInheritanceSteps) return new(PhysicsDataStorageProjectionStatus.Partial, NativeStorageMask, StoredMaskBit, null, null, null, "Parent depth limit.");
            if (record.Parent.Status != PhysicsLayoutReferenceStatus.Unique || record.Parent.Candidates.Count != 1)
                return new(record.Parent.Status == PhysicsLayoutReferenceStatus.Ambiguous ? PhysicsDataStorageProjectionStatus.Ambiguous : PhysicsDataStorageProjectionStatus.MissingParent,
                    NativeStorageMask, StoredMaskBit, null, null, null, "Parent target is unresolved.");
            var parent = record.Parent.Candidates[0];
            if (parent.Owner != layouts.Identity || parent.TypeChunkOrdinal != layouts.TypeChunkOrdinal || !records.TryGetValue(parent, out var parents))
                return new(PhysicsDataStorageProjectionStatus.MissingParent, NativeStorageMask, StoredMaskBit, null, null, null, "Parent target outside table.");
            if (parents.Length != 1) return new(PhysicsDataStorageProjectionStatus.Ambiguous, NativeStorageMask, StoredMaskBit, null, null, null, "Parent identity duplicated.");
            record = parents[0]; inherited = true;
        }
        return new(PhysicsDataStorageProjectionStatus.Partial, NativeStorageMask, StoredMaskBit, null, null, null, "Parent not resolved.");
    }

    private static PhysicsDataWindowProjection ProjectWindow(PhysicsIndexedItemRow item, PhysicsDataStorageProjection storage, DecodedExtent data)
    {
        var offset = unchecked((int)item.RawWord1);
        if (item.IsReservedRow || item.Projection is not PhysicsIndexedItemProjection.TypeAndData and not PhysicsIndexedItemProjection.TypeAndDataWithAuxiliary)
            return new(PhysicsDataWindowStatus.NotProjected, offset, item.Word2NativeInt32, storage.ElementByteStride, null, null, "Non-DATA ITEM kind.");
        if (item.DataPosition.Status is not PhysicsIndexedDataPositionStatus.WithinDataBody and not PhysicsIndexedDataPositionStatus.AtDataEnd)
            return new(PhysicsDataWindowStatus.NoDataPointer, offset, item.Word2NativeInt32, storage.ElementByteStride, null, null, "Non-ordinary DATA position.");
        if (item.Word2NativeInt32 <= 0) return new(PhysicsDataWindowStatus.NoPositiveCount, offset, item.Word2NativeInt32, storage.ElementByteStride, null, null);
        if (storage.ElementByteStride is not { } stride) return new(PhysicsDataWindowStatus.NoStride, offset, item.Word2NativeInt32, null, null, null);
        if (offset < 0) return new(PhysicsDataWindowStatus.NegativeOffset, offset, item.Word2NativeInt32, stride, null, null);
        try
        {
            var length = checked((long)item.Word2NativeInt32 * stride); var end = checked((long)offset + length);
            if (offset > data.Length || end > data.Length) return new(PhysicsDataWindowStatus.OutOfRange, offset, item.Word2NativeInt32, stride, length, null);
            return new(PhysicsDataWindowStatus.WithinData, offset, item.Word2NativeInt32, stride, length, new(data.Container, checked(data.Offset + offset), length));
        }
        catch (OverflowException) { return new(PhysicsDataWindowStatus.ArithmeticOverflow, offset, item.Word2NativeInt32, stride, null, null); }
    }

    private static bool ValidateLayoutMetadata(PhysicsTypeLayoutsDocument layouts, PhysicsDataStorageLimits limits, CancellationToken token)
    {
        var body = layouts.LayoutTable.RawBody.Span; if (!string.Equals(Hash(body), layouts.LayoutTable.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        var fields = 0;
        for (var index = 0; index < layouts.Records.Count; index++)
        {
            token.ThrowIfCancellationRequested(); var record = layouts.Records[index];
            if (record.Identity.Owner != layouts.Identity || record.Identity.TypeChunkOrdinal != layouts.TypeChunkOrdinal || record.Identity.LayoutChunkOrdinal != layouts.LayoutTable.ChunkOrdinal || record.Identity.RecordOrdinal != index || !Inside(record.Extent, layouts.LayoutTable.BodyExtent)) return false;
            if (record.Parent.Status == PhysicsLayoutReferenceStatus.Unique && record.Parent.Candidates.Count == 1 && record.Parent.Candidates[0].Slot != record.Parent.Index.NativeInt32) return false;
            foreach (var field in Fields(record)) { token.ThrowIfCancellationRequested(); if (++fields > limits.MaximumLayoutFields || !ValidField(field, layouts.LayoutTable.BodyExtent, body)) return false; }
        }
        return true;
    }

    private static int MaximumParentPath(IReadOnlyDictionary<PhysicsLayoutRecordIdentity, PhysicsTypeLayoutRecord[]> records,
        PhysicsTypeLayoutsDocument layouts, PhysicsDataStorageLimits limits, CancellationToken token)
    {
        var maximum = 1;
        foreach (var candidates in records.Values)
        {
            token.ThrowIfCancellationRequested(); if (candidates.Length != 1) continue;
            var current = candidates[0]; var visited = new HashSet<PhysicsLayoutRecordIdentity>(); var depth = 1;
            while (current.Parent.Status == PhysicsLayoutReferenceStatus.Unique && current.Parent.Candidates.Count == 1 &&
                (current.Mask.RawBits & 1) == 0 && depth <= limits.MaximumInheritanceSteps)
            {
                token.ThrowIfCancellationRequested(); var parent = current.Parent.Candidates[0];
                if (!visited.Add(current.Identity) || parent.Owner != layouts.Identity || parent.TypeChunkOrdinal != layouts.TypeChunkOrdinal ||
                    !records.TryGetValue(parent, out var parentRows) || parentRows.Length != 1) break;
                current = parentRows[0]; depth++;
            }
            maximum = Math.Max(maximum, depth);
        }
        return maximum;
    }

    private static bool UnknownTailOnly(PhysicsTypeLayoutsDocument layouts) => layouts.UnknownRanges.All(unknown =>
        !layouts.Records.Any(record => Overlaps(unknown, record.Extent)));

    private static IEnumerable<PhysicsTypeInteger> Fields(PhysicsTypeLayoutRecord record)
    {
        yield return record.SlotIndex; yield return record.Parent.Index; yield return record.Mask;
        foreach (var gate in record.GatedValues) foreach (var field in gate.Fields) yield return field;
        if (record.Subtype is not null) yield return record.Subtype.Index;
        if (record.MemberCount is not null) yield return record.MemberCount;
        foreach (var member in record.Members) { yield return member.NameIndex; yield return member.Flags; yield return member.ThirdScalar; yield return member.Type.Index; }
        if (record.InterfaceCount is not null) yield return record.InterfaceCount;
        foreach (var item in record.Interfaces) { yield return item.TypeIndex; yield return item.SecondScalar; }
    }

    private static bool ValidField(PhysicsTypeInteger field, DecodedExtent bodyExtent, ReadOnlySpan<byte> body)
    {
        if (!Inside(field.Extent, bodyExtent) || field.Extent.Length <= 0 || field.RawEncoding.Length != field.Extent.Length) return false;
        var cursor = 0;
        if (!PhysicsTypeIntegerReader.TryRead(field.RawEncoding.Span, ref cursor, out var raw, out var width, out _) || width != checked((int)field.Extent.Length) || raw != field.RawBits || unchecked((int)raw) != field.NativeInt32) return false;
        var local = checked((int)(field.Extent.Offset - bodyExtent.Offset)); return body.Slice(local, checked((int)field.Extent.Length)).SequenceEqual(field.RawEncoding.Span);
    }

    private static bool ValidLeaf(PhysicsTypeDeclarationLeaf leaf) => leaf.RawBody.Length == leaf.BodyExtent.Length && string.Equals(Hash(leaf.RawBody.Span), leaf.Sha256, StringComparison.OrdinalIgnoreCase);
    private static bool ValidLeaf(PhysicsIndexedMetadataLeaf leaf) => leaf.BodyAvailable && leaf.RawBody.Length == leaf.BodyExtent.Length && leaf.Sha256 is not null && string.Equals(Hash(leaf.RawBody.Span), leaf.Sha256, StringComparison.OrdinalIgnoreCase);
    private static bool Inside(DecodedExtent extent, DecodedExtent parent) => parent.Length >= 0 && parent.Offset <= long.MaxValue - parent.Length && extent.Container == parent.Container && extent.Offset >= parent.Offset && extent.Length >= 0 && extent.Offset <= long.MaxValue - extent.Length && extent.Offset + extent.Length <= parent.Offset + parent.Length;
    private static bool Overlaps(DecodedExtent left, DecodedExtent right) => left.Container == right.Container &&
        left.Offset < right.Offset + right.Length && right.Offset < left.Offset + left.Length;
    private static DecodeStep Step() => new(nameof(PhysicsDataStorageReader), Version, "mask8-storage-width; bounded-parent-chain; signed-data-window");
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool IsSha256(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void ValidateLimits(PhysicsDataStorageLimits limits)
    {
        if (limits.MaximumTypeRecords is < 1 or > 4096 || limits.MaximumItemRows is < 1 or > 4096 || limits.MaximumInheritanceSteps is < 1 or > 128 || limits.MaximumLayoutFields is < 1 or > 65536 || limits.MaximumLeafBytes is < 1 or > 32 * 1024 || limits.MaximumRetainedMetadataBytes < 1 || limits.MaximumRetainedMetadataBytes > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(limits));
    }
}
