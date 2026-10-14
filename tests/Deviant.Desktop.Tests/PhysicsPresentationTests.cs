using Deviant.Assets;
using Deviant.Desktop;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class PhysicsPresentationTests
{
    [Fact]
    public void ProjectsFullTypeLayoutAndItemRecordsWithoutCollapsingCandidates()
    {
        var owner = new ResourceIdentity("fixture-index", 7, "fixture-owner");
        var revision = new SourceRevision("fixture-index", "fixture-revision");
        var root = new PhysicsTypeDeclaration(Slot(owner, 3, 1), Extent(owner, 100, 8),
            Integer(owner, 101, 1), Name(owner, 1, "Root", 102), Integer(owner, 103, 1),
            [Parameter(owner, 104, "member")]);
        var duplicateA = new PhysicsTypeDeclaration(Slot(owner, 3, 2), Extent(owner, 110, 8),
            Integer(owner, 111, 2), Name(owner, 2, "Duplicate A", 112), Integer(owner, 113, 0), []);
        var duplicateB = new PhysicsTypeDeclaration(Slot(owner, 3, 2), Extent(owner, 120, 8),
            Integer(owner, 121, 3), Name(owner, 3, "Duplicate B", 122), Integer(owner, 123, 0), []);
        var unnamed = new PhysicsTypeDeclaration(Slot(owner, 3, 3), Extent(owner, 130, 8),
            Integer(owner, 131, 4), null, Integer(owner, 132, 0), []);
        var declarations = new PhysicsTypeDeclarationsDocument(owner, revision, Extent(owner, 20, 100), "fixture-embedded-hash",
            3, Leaf(owner, 3, 30), Leaf(owner, 4, 40), 1, Integer(owner, 50, 4),
            [Name(owner, 1, "Root", 60), Name(owner, 2, "Duplicate A", 70), Name(owner, 3, "Duplicate B", 80)],
            [root, duplicateA, duplicateB, unnamed], PhysicsTypeDeclarationCoverage.CompleteStructure, [], 0);

        var layoutRootIdentity = Layout(owner, typeChunk: 3, layoutChunk: 5, recordOrdinal: 0, slot: 1);
        var duplicateLayoutA = Layout(owner, typeChunk: 3, layoutChunk: 5, recordOrdinal: 1, slot: 2);
        var duplicateLayoutB = Layout(owner, typeChunk: 3, layoutChunk: 5, recordOrdinal: 2, slot: 2);
        var unresolvedLayout = Layout(owner, typeChunk: 3, layoutChunk: 5, recordOrdinal: 3, slot: 3);
        var layoutRecords = new[]
        {
            LayoutRecord(layoutRootIdentity, Reference(owner, PhysicsLayoutReferenceStatus.Unique, layoutRootIdentity)),
            LayoutRecord(duplicateLayoutA, Reference(owner, PhysicsLayoutReferenceStatus.Ambiguous, duplicateLayoutA, duplicateLayoutB)),
            LayoutRecord(duplicateLayoutB, Reference(owner, PhysicsLayoutReferenceStatus.Ambiguous, duplicateLayoutA, duplicateLayoutB)),
            LayoutRecord(unresolvedLayout, Reference(owner, PhysicsLayoutReferenceStatus.Unresolved))
        };
        var layouts = new PhysicsTypeLayoutsDocument(owner, revision, Extent(owner, 20, 100), "fixture-embedded-hash",
            3, 4, Leaf(owner, 3, 30), Leaf(owner, 5, 50), declarations.Names, layoutRecords,
            false, PhysicsTypeLayoutCoverage.Partial, [], 0);

        var itemIdentityRoot = new PhysicsIndexedItemIdentity(owner, 6, 0);
        var itemIdentityAmbiguous = new PhysicsIndexedItemIdentity(owner, 6, 1);
        var itemIdentityInitial = new PhysicsIndexedItemIdentity(owner, 6, 2);
        var itemIdentityOutOfRange = new PhysicsIndexedItemIdentity(owner, 6, 3);
        var itemIdentityUnresolvedLayout = new PhysicsIndexedItemIdentity(owner, 6, 4);
        var itemIdentityOutOfRangeData = new PhysicsIndexedItemIdentity(owner, 6, 5);
        var itemIdentityRedirect = new PhysicsIndexedItemIdentity(owner, 6, 6);
        var indexedItems = new[]
        {
            Item(new(owner, 6, 0), reserved: true, projection: PhysicsIndexedItemProjection.ReservedRow),
            Item(itemIdentityRoot, type: TypeReference(root.Identity, PhysicsIndexedReferenceStatus.Unique,
                PhysicsIndexedReferenceStatus.Unique, [layoutRootIdentity])),
            Item(itemIdentityAmbiguous, type: new PhysicsIndexedTypeReference(2, PhysicsIndexedReferenceStatus.Ambiguous,
                null, [duplicateA.Identity, duplicateB.Identity], PhysicsIndexedReferenceStatus.Ambiguous,
                [duplicateLayoutA, duplicateLayoutB])),
            Item(itemIdentityInitial, type: new PhysicsIndexedTypeReference(0, PhysicsIndexedReferenceStatus.InitialSlot,
                Slot(owner, 3, 0), [], PhysicsIndexedReferenceStatus.NoLayoutForInitialSlot, []),
                dataStatus: PhysicsIndexedDataPositionStatus.AtDataEnd),
            Item(itemIdentityOutOfRange, type: new PhysicsIndexedTypeReference(99, PhysicsIndexedReferenceStatus.OutOfRange,
                null, [], PhysicsIndexedReferenceStatus.OutOfRange, []), dataStatus: PhysicsIndexedDataPositionStatus.OutOfRange),
            Item(itemIdentityUnresolvedLayout, type: TypeReference(unnamed.Identity,
                PhysicsIndexedReferenceStatus.Unique, PhysicsIndexedReferenceStatus.Unresolved, [])),
            Item(itemIdentityOutOfRangeData, type: TypeReference(root.Identity,
                PhysicsIndexedReferenceStatus.Unique, PhysicsIndexedReferenceStatus.Unique, [layoutRootIdentity]),
                dataStatus: PhysicsIndexedDataPositionStatus.OutOfRange),
            Item(itemIdentityRedirect, projection: PhysicsIndexedItemProjection.Kind0RowReference,
                redirect: new PhysicsIndexedItemReference(-1, PhysicsIndexedReferenceStatus.Ambiguous,
                    null, [itemIdentityRoot, itemIdentityAmbiguous]))
        };
        var indexed = new PhysicsIndexedMetadataDocument(owner, revision, Extent(owner, 20, 100), "fixture-embedded-hash",
            3, 6, 4, Extent(owner, 200, 20), PhysicsIndexedTableStatus.Complete, [IndexedLeaf(owner, 6, 60)],
            PhysicsIndexedTableStatus.Opaque, [IndexedLeaf(owner, 7, 70)], indexedItems, null,
            true, true, [], PhysicsIndexedMetadataCoverage.CompleteStructure, 0);

        var sourceResult = Complete(owner, revision, declarations, layouts, indexed);
        var presentation = new PhysicsInspectionPresentation(sourceResult);

        Assert.Same(sourceResult, presentation.SourceResult);
        Assert.Equal(4, presentation.Types.Count);
        Assert.Same(root, presentation.Types[0].Source);
        Assert.Equal(root.Identity, presentation.Types[0].Identity);
        Assert.Contains("Root", presentation.Types[0].Label, StringComparison.Ordinal);
        Assert.Contains("Type 1", presentation.Types[0].Label, StringComparison.Ordinal);
        Assert.Contains("Type 4", presentation.Types[3].Label, StringComparison.Ordinal);
        Assert.Equal(1, presentation.Types[0].ParameterCount);

        Assert.Equal(layoutRecords.Length, presentation.Layouts.Count);
        Assert.Same(layoutRecords[1], presentation.Layouts[1].Source);
        Assert.Equal(PhysicsLayoutReferenceStatus.Ambiguous, presentation.Layouts[1].ParentStatus);
        Assert.Contains("Layout 2", presentation.Layouts[1].Label, StringComparison.Ordinal);
        Assert.DoesNotContain("Duplicate A", presentation.Layouts[1].Label, StringComparison.Ordinal);
        Assert.DoesNotContain("Duplicate B", presentation.Layouts[1].Label, StringComparison.Ordinal);

        Assert.Equal(indexedItems.Length, presentation.Items.Count);
        Assert.Same(indexedItems[1], presentation.Items[1].Source);
        Assert.Equal(itemIdentityAmbiguous, presentation.Items[2].Identity);
        Assert.Contains("declaration ambiguous", presentation.Items[2].TypeLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("layout ambiguous", presentation.Items[2].TypeLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Ambiguous", presentation.Items[2].Status);
        Assert.Contains("Initial type slot", presentation.Items[3].TypeLabel, StringComparison.Ordinal);
        Assert.Contains("out of range", presentation.Items[4].TypeLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Unknown", presentation.Items[4].Status);
        Assert.Equal("Unknown", presentation.Items[5].Status);
        Assert.Equal("Unknown", presentation.Items[6].Status);
        Assert.Equal("Ambiguous", presentation.Items[7].Status);

        Assert.Equal(new[]
        {
            ("Envelope", "EnvelopeNote"),
            ("Tagged metadata", "TaggedNote"),
            ("Type declarations", "DeclarationNote"),
            ("Type layouts", "LayoutNote"),
            ("Indexed metadata", "IndexedNote")
        }, presentation.Diagnostics.Select(diagnostic => (diagnostic.Stage, diagnostic.Code)));
    }

    [Fact]
    public void PartialPipelineRetainsEarlierDiagnosticsAndEmitsNoUnvisitedRows()
    {
        var owner = new ResourceIdentity("fixture-index", 2, "fixture-owner");
        var revision = new SourceRevision("fixture-index", "fixture-revision");
        var result = new PhysicsMetadataInspectionResult(
            Decode<PhysicsEnvelopeDocument>(null, owner, revision, "EnvelopeRejected"),
            Decode<PhysicsTaggedMetadataDocument>(null, owner, revision, "TaggedRejected"),
            null, null, null);

        var presentation = new PhysicsInspectionPresentation(result);

        Assert.Same(result, presentation.SourceResult);
        Assert.Empty(presentation.Types);
        Assert.Empty(presentation.Layouts);
        Assert.Empty(presentation.Items);
        Assert.Equal(new[] { ("Envelope", "EnvelopeRejected"), ("Tagged metadata", "TaggedRejected") },
            presentation.Diagnostics.Select(diagnostic => (diagnostic.Stage, diagnostic.Code)));
    }

    [Fact]
    public void DuplicateDeclarationIdentityNeverChoosesTheFirstRecoveredName()
    {
        var owner = new ResourceIdentity("fixture-index", 9, "fixture-owner");
        var revision = new SourceRevision("fixture-index", "fixture-revision");
        var identity = Slot(owner, 2, 4);
        var first = Declaration(identity, "First recovered name", 100);
        var second = Declaration(identity, "Second recovered name", 120);
        var declarations = Declarations(owner, revision, [first, second]);
        var item = Item(new(owner, 5, 0), type: TypeReference(identity,
            PhysicsIndexedReferenceStatus.Unique, PhysicsIndexedReferenceStatus.Unique, []));
        var indexed = Indexed(owner, revision, [item]);

        var presentation = new PhysicsInspectionPresentation(Complete(owner, revision, declarations, null, indexed));

        var row = Assert.Single(presentation.Items);
        Assert.DoesNotContain("First recovered name", row.TypeLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("Second recovered name", row.TypeLabel, StringComparison.Ordinal);
        Assert.Contains("Type slot 4", row.TypeLabel, StringComparison.Ordinal);
        Assert.Same(item, row.Source);
    }

    private static PhysicsMetadataInspectionResult Complete(ResourceIdentity owner, SourceRevision revision,
        PhysicsTypeDeclarationsDocument declarations, PhysicsTypeLayoutsDocument? layouts,
        PhysicsIndexedMetadataDocument? indexed) => new(
        Decode(new PhysicsEnvelopeDocument(owner, revision, 1, 1, 5, 0, Extent(owner, 10, 8), Extent(owner, 20, 0),
            ReadOnlyMemory<byte>.Empty, "fixture-hash", null, new Dictionary<string, DecodedExtent>(), [], ReadOnlyMemory<byte>.Empty),
            owner, revision, "EnvelopeNote"),
        Decode(Tagged(owner, revision), owner, revision, "TaggedNote"),
        Decode(declarations, owner, revision, "DeclarationNote"),
        layouts is null ? null : Decode(layouts, owner, revision, "LayoutNote"),
        indexed is null ? null : Decode(indexed, owner, revision, "IndexedNote"));

    private static PhysicsTaggedMetadataDocument Tagged(ResourceIdentity owner, SourceRevision revision) =>
        new(owner, revision, Extent(owner, 20, 0), "fixture-embedded-hash", [], [],
            PhysicsTaggedMetadataCoverage.CompleteFraming, [], null, 0);

    private static PhysicsTypeDeclarationsDocument Declarations(ResourceIdentity owner, SourceRevision revision,
        IReadOnlyList<PhysicsTypeDeclaration> declarations) => new(owner, revision, Extent(owner, 20, 200), "fixture-hash",
        2, Leaf(owner, 2, 30), Leaf(owner, 3, 40), 1, null,
        declarations.Select(declaration => declaration.Name).OfType<PhysicsTypeName>().ToArray(), declarations,
        PhysicsTypeDeclarationCoverage.Partial, [], 0);

    private static PhysicsIndexedMetadataDocument Indexed(ResourceIdentity owner, SourceRevision revision,
        IReadOnlyList<PhysicsIndexedItemRow> items) => new(owner, revision, Extent(owner, 20, 200), "fixture-hash",
        2, 5, 1, Extent(owner, 200, 16), PhysicsIndexedTableStatus.Complete, [IndexedLeaf(owner, 5, 80)],
        PhysicsIndexedTableStatus.Missing, [], items, null, true, true, [], PhysicsIndexedMetadataCoverage.Partial, 0);

    private static PhysicsTypeDeclaration Declaration(PhysicsTypeSlotIdentity identity, string? name, long offset) =>
        new(identity, Extent(identity.Owner, offset, 8), Integer(identity.Owner, offset + 1, identity.Slot),
            name is null ? null : Name(identity.Owner, identity.Slot, name, offset + 2), Integer(identity.Owner, offset + 3, 0), []);

    private static PhysicsTypeParameter Parameter(ResourceIdentity owner, long offset, string name) =>
        new(Integer(owner, offset, 0), Name(owner, 0, name, offset + 1), Integer(owner, offset + 2, 7),
            PhysicsTypeParameterKind.NumericValue, null, false);

    private static PhysicsTypeLayoutRecord LayoutRecord(PhysicsLayoutRecordIdentity identity, PhysicsLayoutReference parent) =>
        new(identity, Extent(identity.Owner, identity.RecordOrdinal * 10L, 9), Integer(identity.Owner, 1, identity.Slot),
            parent, Integer(identity.Owner, 2, 0), [], null, null, [], null, []);

    private static PhysicsIndexedItemRow Item(PhysicsIndexedItemIdentity identity, bool reserved = false,
        PhysicsIndexedItemProjection projection = PhysicsIndexedItemProjection.TypeAndData,
        PhysicsIndexedTypeReference? type = null, PhysicsIndexedDataPositionStatus dataStatus = PhysicsIndexedDataPositionStatus.WithinDataBody,
        PhysicsIndexedItemReference? redirect = null) => new(identity, reserved, projection, 0, 0, 0, 0, 0, 0,
        Extent(identity.Owner, 1, 4), Extent(identity.Owner, 5, 4), Extent(identity.Owner, 9, 4), type,
        new PhysicsIndexedDataPosition(0, dataStatus, 0, dataStatus == PhysicsIndexedDataPositionStatus.WithinDataBody ? Extent(identity.Owner, 20, 0) : null),
        redirect, ReadOnlyMemory<byte>.Empty);

    private static PhysicsIndexedTypeReference TypeReference(PhysicsTypeSlotIdentity target,
        PhysicsIndexedReferenceStatus declarationStatus, PhysicsIndexedReferenceStatus layoutStatus,
        IReadOnlyList<PhysicsLayoutRecordIdentity> layoutCandidates) => new((uint)target.Slot, declarationStatus,
        declarationStatus == PhysicsIndexedReferenceStatus.Unique ? target : null,
        declarationStatus == PhysicsIndexedReferenceStatus.Ambiguous ? [target] : [], layoutStatus, layoutCandidates);

    private static PhysicsLayoutReference Reference(ResourceIdentity owner, PhysicsLayoutReferenceStatus status,
        params PhysicsLayoutRecordIdentity[] candidates) => new(Integer(owner, 1, 1), status, candidates);

    private static PhysicsTypeSlotIdentity Slot(ResourceIdentity owner, int chunkOrdinal, int slot) => new(owner, chunkOrdinal, slot);
    private static PhysicsLayoutRecordIdentity Layout(ResourceIdentity owner, int typeChunk, int layoutChunk,
        int recordOrdinal, int slot) => new(owner, typeChunk, layoutChunk, recordOrdinal, slot);
    private static PhysicsTypeName Name(ResourceIdentity owner, int index, string text, long offset) =>
        new(index, text, Extent(owner, offset, text.Length), ReadOnlyMemory<byte>.Empty);
    private static PhysicsTypeInteger Integer(ResourceIdentity owner, long offset, int value) =>
        new(unchecked((uint)value), value, Extent(owner, offset, 1), ReadOnlyMemory<byte>.Empty);
    private static DecodedExtent Extent(ResourceIdentity owner, long offset, long length) => new(owner, offset, length);
    private static PhysicsTypeDeclarationLeaf Leaf(ResourceIdentity owner, int chunk, long offset) =>
        new(chunk, Extent(owner, offset, 8), Extent(owner, offset + 8, 4), 0, "fixture-hash",
            ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);

    private static PhysicsIndexedMetadataLeaf IndexedLeaf(ResourceIdentity owner, int chunk, long offset) =>
        new(chunk, Extent(owner, offset, 8), Extent(owner, offset + 8, 4), 0, "fixture-hash", true,
            ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);

    private static DecodeResult<T> Decode<T>(T? value, ResourceIdentity identity, SourceRevision revision,
        params string[] diagnostics) => new(value, identity, revision, [], [], diagnostics, [], new Dictionary<string, string>());
}
