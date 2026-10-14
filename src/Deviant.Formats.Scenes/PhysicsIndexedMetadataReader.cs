using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

/// <summary>Reads bounded ITEM rows and retains PTCH without interpreting it.</summary>
public sealed class PhysicsIndexedMetadataReader
{
    public const string Version = "physics-indexed-metadata-v1";

    /// <summary>The P3, P4a, and P4b documents must remain available and unchanged during this synchronous read.</summary>
    public DecodeResult<PhysicsIndexedMetadataDocument> Read(PhysicsTaggedMetadataDocument metadata,
        PhysicsTypeDeclarationsDocument declarations, PhysicsTypeLayoutsDocument layouts,
        IReadOnlyList<SourceExtent> physicalExtents, IReadOnlyList<DecodeStep>? precedingSteps = null,
        PhysicsIndexedMetadataLimits? limits = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(metadata); ArgumentNullException.ThrowIfNull(declarations);
        ArgumentNullException.ThrowIfNull(layouts); ArgumentNullException.ThrowIfNull(physicalExtents);
        token.ThrowIfCancellationRequested(); limits ??= new(); ValidateLimits(limits);
        DecodeResult<PhysicsIndexedMetadataDocument> Early(string code) => new(null, metadata.Identity, metadata.Revision,
            [], [Step()], [code], [], new Dictionary<string, string>());
        if (!ValidIdentity(metadata)) return Early("PhysicsIndexedSourceMismatch");

        var physicalCount = physicalExtents.Count; token.ThrowIfCancellationRequested();
        var stepCount = precedingSteps?.Count ?? 0; token.ThrowIfCancellationRequested();
        var chunkCount = metadata.Chunks.Count; token.ThrowIfCancellationRequested();
        if (physicalCount < 0 || physicalCount > limits.MaximumPhysicalExtents || stepCount < 0 || stepCount > limits.MaximumPrecedingSteps ||
            chunkCount < 1 || chunkCount > limits.MaximumChunks) return Early("PhysicsIndexedContextLimit");

        var budget = new Budget(limits.MaximumMetadataBytes);
        if (!budget.Reserve(4096L + 8L * (physicalCount + stepCount) + 384L * chunkCount +
            TextBytes(metadata.Identity.IndexContentId) + TextBytes(metadata.Identity.MemberPath) +
            TextBytes(metadata.Revision.PhysicalSourceStamp) + TextBytes(metadata.EmbeddedSha256))) return Early("PhysicsIndexedContextLimit");

        var physical = new SourceExtent[physicalCount];
        for (var index = 0; index < physical.Length; index++)
        {
            token.ThrowIfCancellationRequested(); var extent = physicalExtents[index]; token.ThrowIfCancellationRequested();
            if (extent is null || extent.Offset < 0 || extent.Length < 0 || extent.Offset > long.MaxValue - extent.Length ||
                !ValidSourceName(extent.RelativeSource) || !OptionalHash(extent.PayloadSha256)) return Early("PhysicsIndexedPhysicalExtentInvalid");
            if (!budget.Reserve(128L + TextBytes(extent.RelativeSource) + TextBytes(extent.PayloadSha256))) return Early("PhysicsIndexedContextLimit");
            physical[index] = extent;
        }
        var steps = new DecodeStep[stepCount + 1];
        for (var index = 0; index < stepCount; index++)
        {
            token.ThrowIfCancellationRequested(); var step = precedingSteps![index]; token.ThrowIfCancellationRequested();
            if (step is null || !ValidText(step.Decoder) || !ValidText(step.Version) || !ValidText(step.Options, true) || !OptionalHash(step.PayloadSha256))
                return Early("PhysicsIndexedContextInvalid");
            if (!budget.Reserve(128L + TextBytes(step.Decoder) + TextBytes(step.Version) + TextBytes(step.Options) + TextBytes(step.PayloadSha256)))
                return Early("PhysicsIndexedContextLimit");
            steps[index] = step;
        }
        steps[^1] = Step();

        var chunks = new Chunk[chunkCount]; var previousChildEnds = new long[chunkCount];
        for (var index = 0; index < chunkCount; index++)
        {
            token.ThrowIfCancellationRequested(); var chunk = metadata.Chunks[index]; token.ThrowIfCancellationRequested();
            if (chunk is null || chunk.RawHeader is not { Length: 8 }) return Early("PhysicsIndexedChunkInvalid");
            var header = chunk.RawHeader.ToArray(); token.ThrowIfCancellationRequested();
            var word = BinaryPrimitives.ReadUInt32BigEndian(header);
            var decodedTag = Printable(header.AsSpan(4, 4)) ? Encoding.ASCII.GetString(header, 4, 4) : null;
            if (chunk.Ordinal != index || chunk.RawBigEndianWord != word || chunk.RawFlags != (byte)(word >> 30) ||
                chunk.DeclaredLength != (word & 0x3fffffff) || chunk.Tag != decodedTag || chunk.Depth < 0 || chunk.Depth > limits.MaximumDepth ||
                chunk.DeclaredLength < 8 || !ValidExtent(chunk.HeaderExtent, metadata.EmbeddedExtent) ||
                !ValidExtent(chunk.BodyExtent, metadata.EmbeddedExtent) || chunk.HeaderExtent.Length != 8 ||
                chunk.BodyExtent.Offset != chunk.HeaderExtent.Offset + 8 || chunk.BodyExtent.Length != chunk.DeclaredLength - 8)
                return Early("PhysicsIndexedChunkInvalid");
            if (index == 0)
            {
                if (decodedTag != "TAG0" || chunk.RawFlags != 0 || chunk.ParentOrdinal is not null || chunk.Depth != 0 ||
                    chunk.HeaderExtent.Offset != metadata.EmbeddedExtent.Offset || chunk.DeclaredLength != metadata.EmbeddedExtent.Length)
                    return Early("PhysicsIndexedProfileUnsupported");
            }
            else
            {
                if (chunk.ParentOrdinal is not { } parent || parent < 0 || parent >= index) return Early("PhysicsIndexedChunkInvalid");
                var parentChunk = chunks[parent];
                if (parentChunk is null || parentChunk.Flags != 0 || chunk.Depth != parentChunk.Depth + 1 ||
                    !Inside(chunk.HeaderExtent.Offset, chunk.DeclaredLength, parentChunk.BodyExtent) ||
                    chunk.HeaderExtent.Offset < previousChildEnds[parent]) return Early("PhysicsIndexedChunkInvalid");
                previousChildEnds[parent] = chunk.HeaderExtent.Offset + chunk.DeclaredLength;
            }
            chunks[index] = new(index, chunk.ParentOrdinal, chunk.Depth, decodedTag, (byte)(word >> 30), word,
                chunk.HeaderExtent, chunk.BodyExtent, header, chunk);
        }
        token.ThrowIfCancellationRequested();

        var roots = chunks.Where(chunk => chunk.Parent == 0).ToArray();
        var typeRoots = roots.Where(chunk => chunk.Tag == "TYPE").ToArray();
        var indexRoots = roots.Where(chunk => chunk.Tag == "INDX").ToArray();
        var dataRoots = roots.Where(chunk => chunk.Tag == "DATA").ToArray();
        if (indexRoots.Length != 1 || indexRoots[0].Flags != 0 || indexRoots[0].Depth != 1)
            return Early(indexRoots.Length > 1 ? "PhysicsIndexedContainerAmbiguous" : "PhysicsIndexedContainerMissing");
        var indexRoot = indexRoots[0];
        var typeOrdinal = typeRoots.Length == 1 && typeRoots[0].Flags == 0 && typeRoots[0].Depth == 1 ? typeRoots[0].Ordinal : (int?)null;
        var dataExtent = dataRoots.Length == 1 && dataRoots[0].Flags == 1 ? dataRoots[0].BodyExtent : null;
        var children = chunks.Where(chunk => chunk.Parent == indexRoot.Ordinal).ToArray();
        var itemChunks = children.Where(chunk => chunk.Tag == "ITEM").ToArray();
        var patchChunks = children.Where(chunk => chunk.Tag == "PTCH").ToArray();

        var diagnostics = new List<string>();
        if (metadata.Coverage != PhysicsTaggedMetadataCoverage.CompleteFraming) Diagnostic(diagnostics, "PhysicsIndexedSourcePartial");
        if (typeOrdinal is null) Diagnostic(diagnostics, typeRoots.Length > 1 ? "PhysicsIndexedTypeContextAmbiguous" : "PhysicsIndexedTypeContextMissing");
        if (dataExtent is null) Diagnostic(diagnostics, dataRoots.Length > 1 ? "PhysicsIndexedDataExtentAmbiguous" : "PhysicsIndexedDataExtentMissing");

        var unknown = new List<DecodedExtent>();
        var unknownCount = metadata.UnknownRanges.Count; token.ThrowIfCancellationRequested();
        if (unknownCount < 0 || unknownCount > limits.MaximumChunks * 2) return Early("PhysicsIndexedContextLimit");
        if (!budget.Reserve(64L * unknownCount + 64)) return Early("PhysicsIndexedContextLimit");
        for (var index = 0; index < unknownCount; index++)
        {
            token.ThrowIfCancellationRequested(); var extent = metadata.UnknownRanges[index]; token.ThrowIfCancellationRequested();
            if (!ValidExtent(extent, metadata.EmbeddedExtent)) return Early("PhysicsIndexedUnknownExtentInvalid");
            unknown.Add(extent);
        }
        if (metadata.BoundedRemainderExtent is { } remainder)
        {
            if (!ValidExtent(remainder, metadata.EmbeddedExtent)) return Early("PhysicsIndexedUnknownExtentInvalid");
            if (!unknown.Contains(remainder)) unknown.Add(remainder);
        }

        var indexFullyVisited = metadata.Coverage == PhysicsTaggedMetadataCoverage.CompleteFraming &&
            !unknown.Any(range => Overlaps(range, indexRoot.BodyExtent));
        var itemLeaves = new List<PhysicsIndexedMetadataLeaf>(); var itemLeafValid = new List<bool>();
        var patchLeaves = new List<PhysicsIndexedMetadataLeaf>(); var patchLeafValid = new List<bool>();
        foreach (var leafChunk in itemChunks)
        {
            token.ThrowIfCancellationRequested();
            var leaf = CopyLeaf(leafChunk, limits, budget, token, out var valid, out var code);
            itemLeaves.Add(leaf); itemLeafValid.Add(valid); if (code is not null) Diagnostic(diagnostics, code);
        }
        foreach (var leafChunk in patchChunks)
        {
            token.ThrowIfCancellationRequested();
            var leaf = CopyLeaf(leafChunk, limits, budget, token, out var valid, out var code);
            patchLeaves.Add(leaf); patchLeafValid.Add(valid); if (code is not null) Diagnostic(diagnostics, code);
            if (!valid) AddUnknown(unknown, leafChunk.BodyExtent);
        }
        if (itemChunks.Length == 0) Diagnostic(diagnostics, indexFullyVisited ? "PhysicsIndexedITEMMissing" : "PhysicsIndexedITEMUnresolved");
        else if (itemChunks.Length > 1)
        {
            Diagnostic(diagnostics, indexFullyVisited ? "PhysicsIndexedITEMAmbiguous" : "PhysicsIndexedITEMUnresolved");
            foreach (var candidate in itemChunks) AddUnknown(unknown, candidate.BodyExtent);
        }
        if (patchChunks.Length == 0) Diagnostic(diagnostics, indexFullyVisited ? "PhysicsIndexedPTCHMissing" : "PhysicsIndexedPTCHUnresolved");
        else if (patchChunks.Length > 1) Diagnostic(diagnostics, indexFullyVisited ? "PhysicsIndexedPTCHAmbiguous" : "PhysicsIndexedPTCHUnresolved");

        var declarationContext = MatchDeclarationContext(metadata, declarations, typeOrdinal, chunks, limits, budget, token, diagnostics);
        var layoutContext = MatchLayoutContext(metadata, layouts, typeOrdinal, chunks, declarationContext.FinalSlots, limits, budget, token, diagnostics);
        var declarationIndex = declarationContext.BySlot;
        var layoutIndex = layoutContext.BySlot;

        var items = new List<PhysicsIndexedItemRow>(); var itemRemainder = default(DecodedExtent?);
        var itemRowsFullyVisited = false; var itemTableStatus = TableStatus(itemChunks.Length, indexFullyVisited, itemLeaves, itemLeafValid);
        if (itemChunks.Length == 1 && (itemLeafValid.Count == 0 || !itemLeafValid[0] || !itemLeaves[0].BodyAvailable))
            AddUnknown(unknown, itemChunks[0].BodyExtent);
        if (itemChunks.Length == 1 && itemLeafValid[0] && itemLeaves[0].BodyAvailable)
        {
            var body = itemLeaves[0].RawBody;
            var rowCount = body.Length / 12; var remainderLength = body.Length % 12;
            if (rowCount > limits.MaximumItemRows)
            {
                itemTableStatus = PhysicsIndexedTableStatus.Partial; Diagnostic(diagnostics, "PhysicsIndexedItemRowLimit");
                AddUnknown(unknown, At(itemLeaves[0].BodyExtent, 0, body.Length));
            }
            else if (!budget.Reserve(384L * rowCount + 256))
            {
                itemTableStatus = PhysicsIndexedTableStatus.Partial; Diagnostic(diagnostics, "PhysicsIndexedMetadataLimit");
                AddUnknown(unknown, At(itemLeaves[0].BodyExtent, 0, body.Length));
            }
            else
            {
                var rowTableComplete = indexFullyVisited;
                for (var ordinal = 0; ordinal < rowCount; ordinal++)
                {
                    token.ThrowIfCancellationRequested();
                    var record = BuildRow(metadata.Identity, indexRoot.Ordinal, ordinal, body, itemLeaves[0].BodyExtent,
                        rowCount, rowTableComplete, declarationContext, declarationIndex, layoutContext, layoutIndex, dataExtent, token, diagnostics);
                    items.Add(record);
                }
                itemRowsFullyVisited = items.Count == rowCount;
                if (remainderLength > 0)
                {
                    itemRemainder = At(itemLeaves[0].BodyExtent, rowCount * 12, remainderLength);
                    AddUnknown(unknown, itemRemainder); itemTableStatus = PhysicsIndexedTableStatus.Partial;
                    Diagnostic(diagnostics, "PhysicsIndexedItemTail");
                }
                if (!itemRowsFullyVisited) itemTableStatus = PhysicsIndexedTableStatus.Partial;
            }
        }
        var patchStatus = TableStatus(patchChunks.Length, indexFullyVisited, patchLeaves, patchLeafValid, opaque: true);
        if (itemLeaves.Any(leaf => !leaf.BodyAvailable) || patchLeaves.Any(leaf => !leaf.BodyAvailable))
            Diagnostic(diagnostics, "PhysicsIndexedLeafUnavailable");

        NormalizeUnknown(unknown, token);
        var coverage = diagnostics.Count == 0 && itemTableStatus == PhysicsIndexedTableStatus.Complete &&
            patchStatus == PhysicsIndexedTableStatus.Opaque && indexFullyVisited && itemRowsFullyVisited
            ? PhysicsIndexedMetadataCoverage.CompleteStructure : PhysicsIndexedMetadataCoverage.Partial;
        var document = new PhysicsIndexedMetadataDocument(metadata.Identity, metadata.Revision, metadata.EmbeddedExtent,
            metadata.EmbeddedSha256, typeOrdinal, indexRoot.Ordinal, declarationContext.FinalSlots, dataExtent,
            itemTableStatus, itemLeaves.ToArray(), patchStatus, patchLeaves.ToArray(), items.ToArray(), itemRemainder,
            indexFullyVisited, itemRowsFullyVisited, unknown.ToArray(), coverage, budget.Used);
        token.ThrowIfCancellationRequested();
        return new(document, metadata.Identity, metadata.Revision, physical, steps, diagnostics.ToArray(), [], new Dictionary<string, string>());
    }

    private static PhysicsIndexedItemRow BuildRow(ResourceIdentity owner, int indexOrdinal, int ordinal,
        ReadOnlyMemory<byte> body, DecodedExtent bodyExtent, int rowCount, bool indexFullyVisited,
        DeclarationContext declarations, Dictionary<int, List<PhysicsTypeSlotIdentity>>? declarationIndex,
        LayoutContext layouts, Dictionary<int, List<PhysicsLayoutRecordIdentity>>? layoutIndex,
        DecodedExtent? dataExtent, CancellationToken token, List<string> diagnostics)
    {
        token.ThrowIfCancellationRequested(); var offset = ordinal * 12; var bytes = body.Span.Slice(offset, 12);
        var word0 = BinaryPrimitives.ReadUInt32LittleEndian(bytes); var word1 = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var word2 = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]); var nativeWord2 = unchecked((int)word2);
        var identity = new PhysicsIndexedItemIdentity(owner, indexOrdinal, ordinal);
        var word0Extent = At(bodyExtent, offset, 4); var word1Extent = At(bodyExtent, offset + 4, 4); var word2Extent = At(bodyExtent, offset + 8, 4);
        var rawKind = word0 & 0xf0000000u; var unknownBits = word0 & 0x0f000000u;
        var rawBytes = body.Slice(offset, 12);
        if (ordinal == 0)
            return new(identity, true, PhysicsIndexedItemProjection.ReservedRow, word0, word1, word2, nativeWord2,
                rawKind, unknownBits, word0Extent, word1Extent, word2Extent, null,
                new(word1, PhysicsIndexedDataPositionStatus.NotProjected, null, null), null, rawBytes);

        if (rawKind == 0)
        {
            var redirect = ResolveItemRedirect(nativeWord2, owner, indexOrdinal, rowCount, indexFullyVisited);
            if (redirect.Status == PhysicsIndexedReferenceStatus.Invalid || redirect.Status == PhysicsIndexedReferenceStatus.OutOfRange)
                Diagnostic(diagnostics, "PhysicsIndexedItemRedirectInvalid");
            else if (redirect.Status == PhysicsIndexedReferenceStatus.Unresolved)
                Diagnostic(diagnostics, "PhysicsIndexedItemRedirectUnresolved");
            return new(identity, false, PhysicsIndexedItemProjection.Kind0RowReference, word0, word1, word2, nativeWord2,
                rawKind, unknownBits, word0Extent, word1Extent, word2Extent, null,
                new(word1, PhysicsIndexedDataPositionStatus.NotProjected, null, null), redirect, rawBytes);
        }

        if (rawKind is not (0x10000000u or 0x20000000u or 0x30000000u or 0x40000000u or 0x50000000u))
        {
            Diagnostic(diagnostics, "PhysicsIndexedItemKindUnsupported");
            return new(identity, false, PhysicsIndexedItemProjection.UnsupportedKind, word0, word1, word2, nativeWord2,
                rawKind, unknownBits, word0Extent, word1Extent, word2Extent,
                null, new(word1, PhysicsIndexedDataPositionStatus.UnsupportedKind, null, null), null, rawBytes);
        }

        var slot = word0 & 0x00ffffffu;
        var type = ResolveTypeReference(slot, owner, declarations, declarationIndex, layouts, layoutIndex);
        AddReferenceDiagnostic(diagnostics, "PhysicsIndexedDeclarationTarget", type.DeclarationStatus);
        AddReferenceDiagnostic(diagnostics, "PhysicsIndexedLayoutTarget", type.LayoutStatus);
        var projection = rawKind switch
        {
            0x10000000u => PhysicsIndexedItemProjection.TypeAndData,
            0x20000000u when word2 == 0 => PhysicsIndexedItemProjection.TypeOnly,
            0x20000000u or 0x30000000u => PhysicsIndexedItemProjection.TypeAndDataWithAuxiliary,
            0x40000000u => PhysicsIndexedItemProjection.SpecialDescriptor4,
            _ => PhysicsIndexedItemProjection.SpecialDescriptor5
        };
        var dataPosition = rawKind switch
        {
            0x10000000u or 0x30000000u => ResolveDataPosition(word1, dataExtent),
            0x20000000u when word2 == 0 => new(word1, PhysicsIndexedDataPositionStatus.NoDataPointer, null, null),
            0x20000000u => ResolveDataPosition(word1, dataExtent),
            0x40000000u or 0x50000000u => new(word1, PhysicsIndexedDataPositionStatus.SpecialDescriptor, null, null),
            _ => new(word1, PhysicsIndexedDataPositionStatus.UnsupportedKind, null, null)
        };
        if (dataPosition.Status == PhysicsIndexedDataPositionStatus.OutOfRange) Diagnostic(diagnostics, "PhysicsIndexedDataOffsetOutOfRange");
        return new(identity, false, projection, word0, word1, word2, nativeWord2, rawKind, unknownBits,
            word0Extent, word1Extent, word2Extent, type, dataPosition, null, rawBytes);
    }

    private static PhysicsIndexedTypeReference ResolveTypeReference(uint slot, ResourceIdentity owner,
        DeclarationContext declarations, Dictionary<int, List<PhysicsTypeSlotIdentity>>? declarationIndex,
        LayoutContext layouts, Dictionary<int, List<PhysicsLayoutRecordIdentity>>? layoutIndex)
    {
        List<PhysicsTypeSlotIdentity> declared = [];
        if (declarationIndex is not null && declarationIndex.TryGetValue((int)slot, out var declaredRows)) declared = declaredRows;
        List<PhysicsLayoutRecordIdentity> layoutRows = [];
        if (layoutIndex is not null && layoutIndex.TryGetValue((int)slot, out var layoutCandidates)) layoutRows = layoutCandidates;
        if (slot == 0)
        {
            if (!declarations.AdmissionMatches || declarations.TypeChunkOrdinal is not { } typeOrdinal)
                return new(slot, PhysicsIndexedReferenceStatus.Unresolved, null, [], PhysicsIndexedReferenceStatus.Unresolved, []);
            var initial = new PhysicsTypeSlotIdentity(owner, typeOrdinal, 0);
            return new(slot, declarations.Complete ? PhysicsIndexedReferenceStatus.InitialSlot : PhysicsIndexedReferenceStatus.Unresolved,
                initial, [initial], layouts.AdmissionMatches
                    ? PhysicsIndexedReferenceStatus.NoLayoutForInitialSlot : PhysicsIndexedReferenceStatus.Unresolved, []);
        }
        if (declarations.FinalSlots is not { } finalSlots)
            return new(slot, PhysicsIndexedReferenceStatus.Unresolved, declared.Count == 1 ? declared[0] : null, declared,
                PhysicsIndexedReferenceStatus.Unresolved, layoutRows);
        if (slot >= finalSlots)
        {
            var outOfRangeDeclarationStatus = declarations.Complete ? PhysicsIndexedReferenceStatus.OutOfRange : PhysicsIndexedReferenceStatus.Unresolved;
            var outOfRangeLayoutStatus = layouts.AdmissionMatches && layouts.Complete
                ? PhysicsIndexedReferenceStatus.OutOfRange : PhysicsIndexedReferenceStatus.Unresolved;
            return new(slot, outOfRangeDeclarationStatus, declared.Count == 1 ? declared[0] : null, declared, outOfRangeLayoutStatus, layoutRows);
        }
        var declarationStatus = MatchStatus(declared, declarations.Complete);
        var declarationTarget = declared.Count == 1 ? declared[0] : null;
        var layoutStatus = MatchStatus(layoutRows, layouts.Complete);
        return new(slot, declarationStatus, declarationTarget, declared, layoutStatus, layoutRows);
    }

    private static PhysicsIndexedReferenceStatus MatchStatus<T>(IReadOnlyList<T> candidates, bool complete) =>
        candidates.Count > 1 ? PhysicsIndexedReferenceStatus.Ambiguous : candidates.Count == 1
            ? complete ? PhysicsIndexedReferenceStatus.Unique : PhysicsIndexedReferenceStatus.Unresolved
            : complete ? PhysicsIndexedReferenceStatus.Missing : PhysicsIndexedReferenceStatus.Unresolved;

    private static void AddReferenceDiagnostic(List<string> diagnostics, string prefix, PhysicsIndexedReferenceStatus status)
    {
        if (status is PhysicsIndexedReferenceStatus.Missing or PhysicsIndexedReferenceStatus.Ambiguous or
            PhysicsIndexedReferenceStatus.Unresolved or PhysicsIndexedReferenceStatus.OutOfRange or PhysicsIndexedReferenceStatus.Invalid)
            Diagnostic(diagnostics, prefix + status);
    }

    private static PhysicsIndexedItemReference ResolveItemRedirect(int index, ResourceIdentity owner, int indexOrdinal,
        int rowCount, bool complete)
    {
        if (index == 0) return new(index, PhysicsIndexedReferenceStatus.NoRedirect, null, []);
        if (index < 0) return new(index, PhysicsIndexedReferenceStatus.Invalid, null, []);
        if (index >= rowCount) return new(index, complete ? PhysicsIndexedReferenceStatus.OutOfRange : PhysicsIndexedReferenceStatus.Unresolved, null, []);
        var target = new PhysicsIndexedItemIdentity(owner, indexOrdinal, index);
        return new(index, complete ? PhysicsIndexedReferenceStatus.Unique : PhysicsIndexedReferenceStatus.Unresolved,
            target, [target]);
    }

    private static PhysicsIndexedDataPosition ResolveDataPosition(uint offset, DecodedExtent? data)
    {
        if (data is null || offset > long.MaxValue - data.Offset)
            return new(offset, PhysicsIndexedDataPositionStatus.Unresolved, null, null);
        var relative = (long)offset;
        if (relative > data.Length) return new(offset, PhysicsIndexedDataPositionStatus.OutOfRange, relative, null);
        var status = relative == data.Length ? PhysicsIndexedDataPositionStatus.AtDataEnd : PhysicsIndexedDataPositionStatus.WithinDataBody;
        return new(offset, status, relative, new(data.Container, data.Offset + relative, 0));
    }

    private static PhysicsIndexedTableStatus TableStatus(int count, bool complete,
        IReadOnlyList<PhysicsIndexedMetadataLeaf> leaves, IReadOnlyList<bool> valid, bool opaque = false)
    {
        if (count == 0) return complete ? PhysicsIndexedTableStatus.Missing : PhysicsIndexedTableStatus.Unresolved;
        if (count > 1) return complete ? PhysicsIndexedTableStatus.Ambiguous : PhysicsIndexedTableStatus.Unresolved;
        if (valid.Count == 0 || !valid[0] || !leaves[0].BodyAvailable) return PhysicsIndexedTableStatus.Partial;
        if (!complete) return PhysicsIndexedTableStatus.Unresolved;
        return opaque ? PhysicsIndexedTableStatus.Opaque : PhysicsIndexedTableStatus.Complete;
    }

    private static PhysicsIndexedMetadataLeaf CopyLeaf(Chunk chunk, PhysicsIndexedMetadataLimits limits, Budget budget,
        CancellationToken token, out bool valid, out string? diagnostic)
    {
        valid = false; diagnostic = null; var source = chunk.Source;
        var hash = source.MetadataBodySha256;
        if (chunk.Flags != 1 || hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit))
        {
            diagnostic = "PhysicsIndexedLeafInvalid";
            return new(chunk.Ordinal, chunk.HeaderExtent, chunk.BodyExtent, chunk.Word, hash, false, chunk.Header, ReadOnlyMemory<byte>.Empty);
        }
        if (source.MetadataBody is not { } borrowed)
        {
            diagnostic = "PhysicsIndexedLeafUnavailable";
            return new(chunk.Ordinal, chunk.HeaderExtent, chunk.BodyExtent, chunk.Word, hash, false, chunk.Header, ReadOnlyMemory<byte>.Empty);
        }
        if (chunk.BodyExtent.Length > int.MaxValue || borrowed.Length != chunk.BodyExtent.Length ||
            borrowed.Length > limits.MaximumLeafBytes || !budget.Reserve(256L + borrowed.Length))
        {
            diagnostic = borrowed.Length > limits.MaximumLeafBytes ? "PhysicsIndexedLeafLimit" : "PhysicsIndexedLeafUnavailable";
            return new(chunk.Ordinal, chunk.HeaderExtent, chunk.BodyExtent, chunk.Word, hash, false, chunk.Header, ReadOnlyMemory<byte>.Empty);
        }
        var copy = new byte[borrowed.Length];
        for (var offset = 0; offset < copy.Length;)
        {
            token.ThrowIfCancellationRequested(); var count = Math.Min(4096, copy.Length - offset);
            borrowed.Span.Slice(offset, count).CopyTo(copy.AsSpan(offset, count)); offset += count;
        }
        token.ThrowIfCancellationRequested(); var actualHash = Hash(copy, token);
        valid = string.Equals(actualHash, hash, StringComparison.OrdinalIgnoreCase);
        if (!valid) diagnostic = "PhysicsIndexedLeafHashMismatch";
        return new(chunk.Ordinal, chunk.HeaderExtent, chunk.BodyExtent, chunk.Word, actualHash, true, chunk.Header, copy);
    }

    private static DeclarationContext MatchDeclarationContext(PhysicsTaggedMetadataDocument metadata,
        PhysicsTypeDeclarationsDocument declarations, int? typeOrdinal, Chunk[] chunks,
        PhysicsIndexedMetadataLimits limits, Budget budget, CancellationToken token, List<string> diagnostics)
    {
        var declarationTable = declarations.DeclarationTable; var stringTable = declarations.StringTable; var rows = declarations.Declarations;
        var tstr = SingleChild(chunks, typeOrdinal, "TSTR"); var tna = SingleChild(chunks, typeOrdinal, "TNA1");
        var contextMatches = declarations.Identity == metadata.Identity && declarations.Revision == metadata.Revision &&
            declarations.EmbeddedExtent == metadata.EmbeddedExtent && declarations.InheritedEmbeddedSha256 == metadata.EmbeddedSha256 &&
            declarationTable is not null && stringTable is not null && rows is not null &&
            Enum.IsDefined(declarations.Coverage) && typeOrdinal is { } ordinal && declarations.TypeChunkOrdinal == ordinal && tstr is not null && tna is not null &&
            MatchesLeaf(stringTable, tstr, limits, token) && MatchesLeaf(declarationTable, tna, limits, token);
        if (!contextMatches) Diagnostic(diagnostics, "PhysicsIndexedDeclarationContextMismatch");
        var finalSlots = default(int?); var countField = declarations.FinalSlotCount;
        if (contextMatches && declarationTable is not null && declarations.InitialSlotCount == 1 && countField is not null &&
            countField.Extent is not null && countField.RawBits <= int.MaxValue &&
            countField.NativeInt32 == (int)countField.RawBits && countField.NativeInt32 >= 1 && countField.NativeInt32 <= limits.MaximumTypeSlots)
        {
            var cursor = 0; token.ThrowIfCancellationRequested();
            if (PhysicsTypeIntegerReader.TryRead(declarationTable!.RawBody.Span, ref cursor, out var bits, out var width, out _) &&
                bits == countField.RawBits && countField.Extent.Container == declarationTable.BodyExtent.Container &&
                countField.Extent.Offset == declarationTable.BodyExtent.Offset && countField.Extent.Length == width &&
                countField.RawEncoding.Span.SequenceEqual(declarationTable.RawBody.Span[..width])) finalSlots = countField.NativeInt32;
        }
        var rowsComplete = false; var slots = new List<PhysicsTypeSlotIdentity>(); var slotMatches = new Dictionary<int, List<PhysicsTypeSlotIdentity>>();
        var declarationBodyExtent = declarationTable?.BodyExtent;
        var rowCount = rows?.Count ?? -1; token.ThrowIfCancellationRequested();
        if (contextMatches && rowCount >= 0 && rowCount <= limits.MaximumTypeSlots && finalSlots is { } count && budget.Reserve(96L * rowCount + 256))
        {
            var validRows = true;
            for (var index = 0; index < rowCount; index++)
            {
                token.ThrowIfCancellationRequested(); var row = rows![index]; token.ThrowIfCancellationRequested();
                if (row is null || row.Identity is null || row.Identity.Owner != metadata.Identity || row.Identity.TypeChunkOrdinal != typeOrdinal ||
                    row.Identity.Slot != index + 1 || row.Identity.Slot >= count || row.Extent is null || declarationBodyExtent is null ||
                    !ValidExtent(row.Extent, declarationBodyExtent) || row.NameIndex is null ||
                    !IntegerMatchesBody(row.NameIndex, row.Extent, declarationTable!)) { validRows = false; continue; }
                slots.Add(row.Identity);
                if (!slotMatches.TryGetValue(row.Identity.Slot, out var group)) slotMatches[row.Identity.Slot] = group = [];
                group.Add(row.Identity);
            }
            rowsComplete = validRows && metadata.Coverage == PhysicsTaggedMetadataCoverage.CompleteFraming &&
                slots.Count == count - 1 && rowCount == count - 1 &&
                Enumerable.Range(1, count - 1).All(slot => slotMatches.TryGetValue(slot, out var group) && group.Count == 1);
        }
        if (finalSlots is null || !rowsComplete) Diagnostic(diagnostics, "PhysicsIndexedDeclarationSetIncomplete");
        return new(contextMatches && declarations.InitialSlotCount == 1, typeOrdinal, finalSlots, rowsComplete, slotMatches);
    }

    private static LayoutContext MatchLayoutContext(PhysicsTaggedMetadataDocument metadata, PhysicsTypeLayoutsDocument layouts,
        int? typeOrdinal, Chunk[] chunks, int? finalSlots, PhysicsIndexedMetadataLimits limits,
        Budget budget, CancellationToken token, List<string> diagnostics)
    {
        var stringTable = layouts.StringTable; var layoutTable = layouts.LayoutTable; var records = layouts.Records;
        var fstr = SingleChild(chunks, typeOrdinal, "FSTR"); var tbdy = SingleChild(chunks, typeOrdinal, "TBDY");
        var contextMatches = layouts.Identity == metadata.Identity && layouts.Revision == metadata.Revision &&
            layouts.EmbeddedExtent == metadata.EmbeddedExtent && layouts.InheritedEmbeddedSha256 == metadata.EmbeddedSha256 &&
            stringTable is not null && layoutTable is not null && records is not null &&
            Enum.IsDefined(layouts.Coverage) && typeOrdinal is { } ordinal && layouts.TypeChunkOrdinal == ordinal && fstr is not null && tbdy is not null &&
            MatchesLeaf(stringTable, fstr, limits, token) && MatchesLeaf(layoutTable, tbdy, limits, token) &&
            finalSlots is { } slots && layouts.FinalDeclarationSlots == slots;
        if (!contextMatches) Diagnostic(diagnostics, "PhysicsIndexedLayoutContextMismatch");
        var rowCount = records?.Count ?? -1; token.ThrowIfCancellationRequested(); var expectedSlots = finalSlots ?? -1;
        var nextRecordOffset = layoutTable?.BodyExtent.Offset ?? 0;
        var slotMatches = new Dictionary<int, List<PhysicsLayoutRecordIdentity>>(); var rowsValid = contextMatches && rowCount >= 0 &&
            rowCount <= limits.MaximumTypeSlots && budget.Reserve(128L * rowCount + 256);
        if (rowsValid)
        {
            for (var index = 0; index < rowCount; index++)
            {
                token.ThrowIfCancellationRequested(); var row = records![index]; token.ThrowIfCancellationRequested();
                if (row is null || row.Identity is null || row.Identity.Owner != metadata.Identity || row.Identity.TypeChunkOrdinal != typeOrdinal ||
                    row.Identity.LayoutChunkOrdinal != tbdy!.Ordinal || row.Identity.RecordOrdinal != index || row.Identity.Slot < 1 || row.Identity.Slot >= expectedSlots ||
                    row.SlotIndex is null || row.SlotIndex.NativeInt32 != row.Identity.Slot ||
                    row.SlotIndex.Extent is null || row.Extent is null || !ValidExtent(row.Extent, layoutTable!.BodyExtent) ||
                    row.Extent.Offset != nextRecordOffset || row.SlotIndex.Extent.Offset != row.Extent.Offset ||
                    !ValidExtent(row.SlotIndex.Extent, row.Extent) || !IntegerMatchesBody(row.SlotIndex, row.Extent, layoutTable))
                { rowsValid = false; break; }
                nextRecordOffset = row.Extent.Offset + row.Extent.Length;
                if (!slotMatches.TryGetValue(row.Identity.Slot, out var group)) slotMatches[row.Identity.Slot] = group = [];
                group.Add(row.Identity);
            }
        }
        if (rowsValid && layouts.LayoutTableFullyVisited &&
            nextRecordOffset != layoutTable!.BodyExtent.Offset + layoutTable.BodyExtent.Length)
        {
            var relativeOffset = nextRecordOffset - layoutTable.BodyExtent.Offset;
            var tail = new DecodedExtent(layoutTable.BodyExtent.Container, nextRecordOffset,
                layoutTable.BodyExtent.Length - relativeOffset);
            var unknownCount = layouts.UnknownRanges?.Count ?? -1; token.ThrowIfCancellationRequested();
            var matchedTail = false;
            if (relativeOffset >= 0 && relativeOffset < layoutTable.RawBody.Length &&
                layoutTable.RawBody.Span[(int)relativeOffset] == 0 && unknownCount >= 0 && unknownCount <= limits.MaximumChunks * 2)
                for (var index = 0; index < unknownCount; index++)
                {
                    token.ThrowIfCancellationRequested();
                    if (layouts.UnknownRanges![index] == tail) matchedTail = true;
                }
            rowsValid = matchedTail;
        }
        var complete = rowsValid && layouts.LayoutTableFullyVisited && metadata.Coverage == PhysicsTaggedMetadataCoverage.CompleteFraming;
        if (!complete) Diagnostic(diagnostics, "PhysicsIndexedLayoutSetIncomplete");
        return new(contextMatches, complete, slotMatches);
    }

    private static Chunk? SingleChild(Chunk[] chunks, int? parent, string tag)
    {
        if (parent is null) return null; Chunk? found = null;
        foreach (var chunk in chunks)
        {
            if (chunk.Parent != parent || chunk.Tag != tag) continue;
            if (found is not null) return null; found = chunk;
        }
        return found;
    }

    private static bool MatchesLeaf(PhysicsTypeDeclarationLeaf input, Chunk? source,
        PhysicsIndexedMetadataLimits limits, CancellationToken token)
    {
        if (source is null || source.Flags != 1 || input.ChunkOrdinal != source.Ordinal || input.HeaderExtent != source.HeaderExtent ||
            input.BodyExtent != source.BodyExtent || input.BodyExtent.Length > limits.MaximumLeafBytes ||
            input.RawBigEndianWord != source.Word || input.RawHeader.Length != 8 || !input.RawHeader.Span.SequenceEqual(source.Header) ||
            input.RawBody.Length != source.BodyExtent.Length || !HashShape(input.Sha256) ||
            !string.Equals(input.Sha256, source.Source.MetadataBodySha256, StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(Hash(input.RawBody.Span, token), input.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IntegerMatchesBody(PhysicsTypeInteger value, DecodedExtent record, PhysicsTypeDeclarationLeaf table)
    {
        if (value.Extent is null || value.Extent.Container != table.BodyExtent.Container || value.Extent.Length <= 0 ||
            value.RawEncoding.Length != value.Extent.Length || value.Extent.Offset < table.BodyExtent.Offset ||
            value.Extent.Offset > long.MaxValue - value.Extent.Length || record.Offset < table.BodyExtent.Offset ||
            record.Offset > long.MaxValue - record.Length || value.Extent.Offset < record.Offset ||
            value.Extent.Offset + value.Extent.Length > record.Offset + record.Length) return false;
        var offset = value.Extent.Offset - table.BodyExtent.Offset;
        if (offset < 0 || offset > table.RawBody.Length - value.RawEncoding.Length) return false;
        var encoded = table.RawBody.Span.Slice((int)offset, value.RawEncoding.Length);
        if (!encoded.SequenceEqual(value.RawEncoding.Span)) return false;
        var cursor = 0;
        return PhysicsTypeIntegerReader.TryRead(encoded, ref cursor, out var bits, out var width, out _) &&
            cursor == encoded.Length && width == encoded.Length && bits == value.RawBits &&
            unchecked((int)bits) == value.NativeInt32;
    }

    private static bool ValidIdentity(PhysicsTaggedMetadataDocument input)
    {
        if (input.Identity is not { } identity || input.Revision is not { } revision || input.EmbeddedExtent is not { } extent ||
            input.Chunks is null || input.UnknownRanges is null || !ValidText(identity.IndexContentId) || identity.Ordinal < 0 || !ValidText(identity.MemberPath) ||
            identity.IndexContentId != revision.IndexContentId || !ValidText(revision.PhysicalSourceStamp) || extent.Container is null ||
            extent.Container.MemberPath is not null || extent.Container.IndexContentId != identity.IndexContentId ||
            extent.Container.Ordinal != identity.Ordinal || extent.Offset < 0 || extent.Length < 8 || extent.Offset > long.MaxValue - extent.Length ||
            !HashShape(input.EmbeddedSha256) || !Enum.IsDefined(input.Coverage)) return false;
        var parts = identity.MemberPath!.Split('/');
        return parts.Length == 4 && parts[0] == "dc-object" && parts[2] == "2201" &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var directory) && directory >= 0 &&
            parts[1] == directory.ToString(CultureInfo.InvariantCulture) &&
            uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var nativeId) &&
            parts[3] == nativeId.ToString(CultureInfo.InvariantCulture);
    }

    private static bool ValidSourceName(string? text) => ValidText(text) && !text!.Contains(':') && !text.StartsWith('/') &&
        !text.StartsWith('\\') && !text.Split('/', '\\').Any(part => part is "" or "." or "..");
    private static bool ValidText(string? text, bool empty = false) => text is not null && text.Length <= 4096 && (empty || !string.IsNullOrWhiteSpace(text));
    private static long TextBytes(string? text) => text is null ? 0 : 24L + 2L * text.Length;
    private static bool HashShape(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private static bool OptionalHash(string? hash) => hash is null || HashShape(hash);
    private static bool Printable(ReadOnlySpan<byte> bytes) { foreach (var value in bytes) if (value < 32 || value > 126) return false; return true; }
    private static bool Inside(long offset, long length, DecodedExtent parent) => offset >= parent.Offset && length >= 0 &&
        offset <= long.MaxValue - length && parent.Offset >= 0 && parent.Length >= 0 && parent.Offset <= long.MaxValue - parent.Length &&
        offset + length <= parent.Offset + parent.Length;
    private static bool ValidExtent(DecodedExtent? extent, DecodedExtent parent) => extent is not null && extent.Container == parent.Container && Inside(extent.Offset, extent.Length, parent);
    private static bool Overlaps(DecodedExtent left, DecodedExtent right) => left.Container == right.Container && left.Length > 0 && right.Length > 0 &&
        left.Offset < right.Offset + right.Length && right.Offset < left.Offset + left.Length;
    private static DecodedExtent At(DecodedExtent body, int offset, int length) => new(body.Container, checked(body.Offset + offset), length);
    private static void AddUnknown(List<DecodedExtent> unknown, DecodedExtent extent) { if (!unknown.Contains(extent)) unknown.Add(extent); }
    private static void NormalizeUnknown(List<DecodedExtent> unknown, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        unknown.Sort((left, right) =>
        {
            var container = CompareContainer(left.Container, right.Container);
            return container != 0 ? container : left.Offset.CompareTo(right.Offset);
        });
        token.ThrowIfCancellationRequested();
        for (var index = 1; index < unknown.Count;)
        {
            token.ThrowIfCancellationRequested(); var previous = unknown[index - 1]; var current = unknown[index];
            var previousEnd = previous.Offset + previous.Length;
            if (previous.Container == current.Container && current.Offset <= previousEnd)
            {
                var end = Math.Max(previousEnd, current.Offset + current.Length);
                unknown[index - 1] = previous with { Length = end - previous.Offset }; unknown.RemoveAt(index);
            }
            else index++;
        }
    }
    private static int CompareContainer(ResourceIdentity left, ResourceIdentity right)
    {
        var index = string.CompareOrdinal(left.IndexContentId, right.IndexContentId);
        if (index != 0) return index;
        var ordinal = left.Ordinal.CompareTo(right.Ordinal);
        return ordinal != 0 ? ordinal : string.CompareOrdinal(left.MemberPath, right.MemberPath);
    }
    private static void Diagnostic(List<string> diagnostics, string code) { if (!diagnostics.Contains(code)) diagnostics.Add(code); }
    private static DecodeStep Step() => new(nameof(PhysicsIndexedMetadataReader), Version, "ITEM-12-byte-rows; opaque-PTCH; indexed-references; opaque-DATA");
    private static string Hash(ReadOnlySpan<byte> bytes, CancellationToken token) { token.ThrowIfCancellationRequested(); var value = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); token.ThrowIfCancellationRequested(); return value; }
    private static void ValidateLimits(PhysicsIndexedMetadataLimits limits)
    {
        if (limits.MaximumChunks < 1 || limits.MaximumChunks > 4096 || limits.MaximumDepth < 0 || limits.MaximumDepth > 64 ||
            limits.MaximumLeafBytes < 0 || limits.MaximumLeafBytes > 1024 * 1024 || limits.MaximumTypeSlots < 1 || limits.MaximumTypeSlots > 65_536 ||
            limits.MaximumItemRows < 0 || limits.MaximumItemRows > 65_536 ||
            limits.MaximumMetadataBytes < 0 || limits.MaximumMetadataBytes > 64L * 1024 * 1024 ||
            limits.MaximumPhysicalExtents < 0 || limits.MaximumPhysicalExtents > 262_144 ||
            limits.MaximumPrecedingSteps < 0 || limits.MaximumPrecedingSteps > 4096) throw new ArgumentOutOfRangeException(nameof(limits));
    }

    private sealed class Budget(long maximum)
    {
        public long Used { get; private set; }
        public bool Reserve(long bytes) { if (bytes < 0 || bytes > maximum - Used) return false; Used += bytes; return true; }
    }
    private sealed record Chunk(int Ordinal, int? Parent, int Depth, string? Tag, byte Flags, uint Word,
        DecodedExtent HeaderExtent, DecodedExtent BodyExtent, byte[] Header, PhysicsTaggedChunk Source);
    private sealed record DeclarationContext(bool AdmissionMatches, int? TypeChunkOrdinal, int? FinalSlots,
        bool Complete, Dictionary<int, List<PhysicsTypeSlotIdentity>> BySlot);
    private sealed record LayoutContext(bool AdmissionMatches, bool Complete,
        Dictionary<int, List<PhysicsLayoutRecordIdentity>> BySlot);
}
