using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class PhysicsTypeLayoutsReaderTests
{
    [Fact]
    public void ParentForwardLinksAndCyclesRemainLocalLayoutReferences()
    {
        var input = Fixture([1, 3, 0, 2, 1, 0, 3, 2, 0]); var result = Read(input);
        var doc = Assert.IsType<PhysicsTypeLayoutsDocument>(result.Value); Assert.Empty(result.Diagnostics);
        Assert.Equal(PhysicsTypeLayoutCoverage.CompleteStructure, doc.Coverage);
        Assert.Equal(new[] { 3, 1, 2 }, doc.Records.Select(row => Assert.Single(row.Parent.Candidates).Slot));
        Assert.All(doc.Records, row => Assert.Equal(PhysicsLayoutReferenceStatus.Unique, row.Parent.Status));
        Assert.All(doc.Records, row => Assert.Equal(doc.LayoutTable.ChunkOrdinal, row.Identity.LayoutChunkOrdinal));
    }

    [Fact]
    public void DuplicateSlotsAndMissingLinksArePreserved()
    {
        var input = Fixture([1, 0, 0, 1, 0, 0, 2, 1, 0, 3, 3, 0]); var result = Read(input);
        Assert.Equal(4, result.Value!.Records.Count); Assert.Contains("PhysicsLayoutDuplicateSlot", result.Diagnostics);
        Assert.Equal(PhysicsLayoutReferenceStatus.Ambiguous, result.Value.Records[2].Parent.Status);
        Assert.Equal(2, result.Value.Records[2].Parent.Candidates.Count);
        var missing = Read(Fixture([1, 3, 0])); Assert.Equal(PhysicsLayoutReferenceStatus.Missing, missing.Value!.Records[0].Parent.Status);
        Assert.Contains("PhysicsLayoutTargetMissing", missing.Diagnostics);
    }

    [Fact]
    public void GatesKeepRawValuesAndMemberNativeProjections()
    {
        var body = Join([1, 0, 63, 7, 2, 9, 16, 8, 4, 1], Wide(65536), [0], Wide(65540), [2], [2, 0, 0, 3, 0, 0]);
        var result = Read(Fixture(body)); var row = result.Value!.Records[0];
        Assert.Equal(new byte[] { 1, 2, 4, 8, 16 }, row.GatedValues.Select(value => value.MaskBit));
        Assert.Equal(2, Assert.Single(row.Subtype!.Candidates).Slot);
        var member = Assert.Single(row.Members); Assert.Equal("member", member.Name.PrintableText);
        Assert.Equal(65536UL, member.NameIndex.RawBits); Assert.Equal(65540UL, member.ThirdScalar.RawBits); Assert.Equal((ushort)4, member.ThirdScalarUInt16);
        Assert.Equal((ushort)0, member.RawFlagsUInt16); Assert.Equal((ushort)32, member.EffectiveFlagsUInt16);
        Assert.Equal(0x00200004u, member.PackedNativeWord); Assert.Equal(2, Assert.Single(member.Type.Candidates).Slot);
    }

    [Fact]
    public void ZeroLayoutTargetRequestsNoLinkRatherThanInitialDeclarationSlot()
    {
        var result = Read(Fixture([1, 0, 2, 0])); var row = Assert.Single(result.Value!.Records);
        Assert.Equal(PhysicsLayoutReferenceStatus.NoLinkRequested, row.Parent.Status); Assert.Empty(row.Parent.Candidates);
        Assert.Equal(PhysicsLayoutReferenceStatus.NoLinkRequested, row.Subtype!.Status); Assert.Empty(row.Subtype.Candidates);
    }

    [Fact]
    public void DeclarationTailIsAcceptedAndSkippedLayoutTailIsUnassigned()
    {
        var input = Fixture([1, 0, 0, 0, 255, 23], declarationTail: true);
        Assert.Equal(PhysicsTypeDeclarationCoverage.Partial, input.Declarations.Coverage);
        var result = Read(input); var doc = result.Value!; Assert.Single(doc.Records);
        var range = Assert.Single(doc.UnknownRanges); Assert.Equal(doc.LayoutTable.BodyExtent.Offset + 3, range.Offset); Assert.Equal(3, range.Length);
        Assert.Contains("PhysicsLayoutSkippedTail", result.Diagnostics);
    }

    [Theory]
    [InlineData(128)] [InlineData(256)] [InlineData(512)]
    public void ASTRAndUnknownMasksStopWithOneWholeRecordRemainder(ulong mask)
    {
        var body = Join([1, 0], Wide(mask), [9, 0]); var result = Read(Fixture(body));
        Assert.Contains("PhysicsLayoutMaskUnsupported", result.Diagnostics); Assert.Empty(result.Value!.Records);
        Assert.Equal(result.Value.LayoutTable.BodyExtent, Assert.Single(result.Value.UnknownRanges));
    }

    [Fact]
    public void MixedMemberFlagBranchIsExplicitlyPartial()
    {
        var result = Read(Fixture([1, 0, 32, 1, 0, 16, 0, 2])); Assert.Contains("PhysicsLayoutMemberOrderUnsupported", result.Diagnostics);
        Assert.Empty(result.Value!.Records); Assert.Equal(result.Value.LayoutTable.BodyExtent, Assert.Single(result.Value.UnknownRanges));
    }

    [Fact]
    public void InterfaceUsesDeclarationSlotAndSignedProjectionWithUnobservedStatus()
    {
        var result = Read(Fixture(Join([1, 0, 64, 1, 0], [249, 255, 255, 255, 255, 255, 255, 255, 255])));
        var pair = Assert.Single(Assert.Single(result.Value!.Records).Interfaces); Assert.Equal(0, pair.DeclarationTarget!.Slot);
        Assert.Equal(-1, pair.SecondScalar.NativeInt32); Assert.Contains("PhysicsLayoutInterfaceProfileUnobserved", result.Diagnostics);
        Assert.Equal(PhysicsTypeLayoutCoverage.Partial, result.Value.Coverage);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void TruncationHasDisjointWholeRecordRemainder(int length)
    {
        var full = new byte[] { 1, 0, 32, 1, 0, 32, 0, 2 }; var result = Read(Fixture(full[..length]));
        Assert.Empty(result.Value!.Records);
        if (length != 0) Assert.Equal(result.Value.LayoutTable.BodyExtent, Assert.Single(result.Value.UnknownRanges));
    }

    [Fact]
    public void UnsupportedIntegerAndNegativeNativeSlotNeverBecomeLinks()
    {
        var prefix = Read(Fixture([1, 0, 250])); Assert.Contains("PhysicsTypeIntegerPrefixUnsupported", prefix.Diagnostics);
        Assert.Equal(prefix.Value!.LayoutTable.BodyExtent, Assert.Single(prefix.Value.UnknownRanges));
        var slot = Read(Fixture([249, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0]));
        Assert.Contains("PhysicsLayoutSlotInvalid", slot.Diagnostics); Assert.Empty(slot.Value!.Records);
    }

    [Fact]
    public void OwnedLeafBytesAndJsonDoNotRetainData()
    {
        var input = Fixture([1, 0, 0], data: new byte[1024 * 1024]); var doc = Read(input).Value!;
        var source = input.Metadata.Chunks.Single(chunk => chunk.Tag == "TBDY").MetadataBody!.Value;
        Assert.True(MemoryMarshal.TryGetArray(source, out var borrowed)); Assert.True(MemoryMarshal.TryGetArray(doc.LayoutTable.RawBody, out var owned));
        Assert.NotSame(borrowed.Array, owned.Array); Assert.Equal(3, owned.Array!.Length); borrowed.Array![borrowed.Offset] = 255;
        Assert.Equal(1, doc.LayoutTable.RawBody.Span[0]); Assert.True(doc.RetainedMetadataBytes < 64 * 1024);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(doc));
        Assert.False(json.RootElement.GetProperty("LayoutTable").TryGetProperty("RawBody", out _));
        Assert.False(json.RootElement.GetProperty("LayoutTable").TryGetProperty("RawHeader", out _));
        Assert.False(json.RootElement.GetProperty("Records")[0].GetProperty("SlotIndex").TryGetProperty("RawEncoding", out _));
    }

    [Fact]
    public void ForeignOrForgedDeclarationContextIsRejected()
    {
        var input = Fixture([1, 0, 0]); var foreign = input.Declarations with { Revision = new("other-index", "revision") };
        Assert.Contains("PhysicsLayoutDeclarationContextInvalid", new PhysicsTypeLayoutsReader().Read(input.Metadata, foreign, TaggedFixture.Physical).Diagnostics);
        var count = input.Declarations.FinalSlotCount! with { RawBits = 9 };
        var forged = input.Declarations with { FinalSlotCount = count };
        Assert.Contains("PhysicsLayoutDeclarationContextInvalid", new PhysicsTypeLayoutsReader().Read(input.Metadata, forged, TaggedFixture.Physical).Diagnostics);
    }

    [Fact]
    public void MutableBorrowedBodyIsValidatedAfterProvenanceCallbacks()
    {
        var input = Fixture([1, 0, 0]); var leaf = input.Metadata.Chunks.Single(chunk => chunk.Tag == "TBDY");
        Assert.True(MemoryMarshal.TryGetArray(leaf.MetadataBody!.Value, out var bytes));
        var physical = new CallbackList<SourceExtent>(1, _ => { bytes.Array![bytes.Offset] = 2; return TaggedFixture.Physical[0]; });
        var result = new PhysicsTypeLayoutsReader().Read(input.Metadata, input.Declarations, physical);
        Assert.Contains("PhysicsLayoutLeafHashMismatch", result.Diagnostics);
    }

    [Fact]
    public void CallerCollectionCountAndIndexersFenceCancellation()
    {
        var input = Fixture([1, 0, 0]); using var stop = new CancellationTokenSource();
        var count = new CallbackList<SourceExtent>(0, _ => null!, () => { stop.Cancel(); return -1; });
        Assert.Throws<OperationCanceledException>(() => new PhysicsTypeLayoutsReader().Read(input.Metadata, input.Declarations, count, token: stop.Token));
        using var second = new CancellationTokenSource(); var index = new CallbackList<SourceExtent>(1, _ => { second.Cancel(); return null!; });
        Assert.Throws<OperationCanceledException>(() => new PhysicsTypeLayoutsReader().Read(input.Metadata, input.Declarations, index, token: second.Token));
        var huge = new CallbackList<SourceExtent>(int.MaxValue, _ => throw new InvalidOperationException());
        Assert.Contains("PhysicsLayoutContextLimit", new PhysicsTypeLayoutsReader().Read(input.Metadata, input.Declarations, huge).Diagnostics); Assert.Equal(0, huge.Accesses);
    }

    [Fact]
    public void RecordAndAggregateBudgetsKeepBoundedPartialResults()
    {
        var input = Fixture([1, 0, 32, 1, 0, 32, 0, 2]);
        var small = Read(input, new(MaximumMembers: 0)); Assert.Contains("PhysicsLayoutMemberLimit", small.Diagnostics);
        Assert.Equal(small.Value!.LayoutTable.BodyExtent, Assert.Single(small.Value.UnknownRanges));
        var bytes = Read(input, new(MaximumMetadataBytes: 1)); Assert.Null(bytes.Value); Assert.Contains("PhysicsLayoutContextLimit", bytes.Diagnostics);
    }

    [Fact]
    public void ResolutionBudgetExhaustionKeepsExistingTargetUnresolved()
    {
        var input = Fixture([1, 2, 0, 2, 0, 0]); var complete = Read(input).Value!;
        PhysicsTypeLayoutsDocument? limited = null;
        for (var budget = Math.Max(1, complete.RetainedMetadataBytes - 2048); budget < complete.RetainedMetadataBytes; budget += 8)
        {
            var result = Read(input, new(MaximumMetadataBytes: budget));
            if (result.Value is { Records.Count: 2 } candidate && result.Diagnostics.Contains("PhysicsLayoutResolutionLimit")) { limited = candidate; break; }
        }
        Assert.NotNull(limited); Assert.Equal(PhysicsLayoutReferenceStatus.Unresolved, limited!.Records[0].Parent.Status);
        Assert.Empty(limited.Records[0].Parent.Candidates);
    }

    [Fact]
    public void IncompleteTableDoesNotProveMissingOrUniqueTargets()
    {
        var result = Read(Fixture([1, 2, 0, 2, 0, 250])); var doc = result.Value!;
        Assert.False(doc.LayoutTableFullyVisited); Assert.Single(doc.Records);
        Assert.Equal(PhysicsLayoutReferenceStatus.Unresolved, doc.Records[0].Parent.Status);
        Assert.DoesNotContain("PhysicsLayoutTargetMissing", result.Diagnostics);
        var known = Read(Fixture([1, 1, 0, 2, 0, 250])); Assert.Equal(PhysicsLayoutReferenceStatus.Unresolved, known.Value!.Records[0].Parent.Status);
        Assert.Single(known.Value.Records[0].Parent.Candidates);
    }

    private static Input Fixture(byte[] body, bool declarationTail = false, byte[]? data = null)
    {
        var tna = Join([4, 0, 0, 1, 0, 2, 0], declarationTail ? new byte[] { 0 } : []);
        var envelope = TaggedFixture.Envelope(TaggedFixture.Root(TaggedFixture.Chunk("DATA", 1, data ?? []),
            TaggedFixture.Chunk("TYPE", 0, Join(TaggedFixture.Chunk("TSTR", 1, Encoding.ASCII.GetBytes("Root\0Child\0Leaf\0")),
                TaggedFixture.Chunk("TNA1", 1, tna), TaggedFixture.Chunk("FSTR", 1, Encoding.ASCII.GetBytes("member\0")), TaggedFixture.Chunk("TBDY", 1, body)))));
        var metadata = new PhysicsTaggedMetadataReader().Read(envelope, TaggedFixture.Physical).Value!;
        return new(metadata, new PhysicsTypeDeclarationsReader().Read(metadata, TaggedFixture.Physical).Value!);
    }
    private static DecodeResult<PhysicsTypeLayoutsDocument> Read(Input input, PhysicsTypeLayoutLimits? limits = null) =>
        new PhysicsTypeLayoutsReader().Read(input.Metadata, input.Declarations, TaggedFixture.Physical, limits: limits);
    private sealed record Input(PhysicsTaggedMetadataDocument Metadata, PhysicsTypeDeclarationsDocument Declarations);
    private static byte[] Join(params byte[][] bytes) => bytes.SelectMany(value => value).ToArray();
    private static byte[] Wide(ulong value) => [(byte)(0xc0 | (value >> 16)), (byte)(value >> 8), (byte)value];
    private sealed class CallbackList<T>(int count, Func<int, T> read, Func<int>? getCount = null) : IReadOnlyList<T>
    {
        public int Count => getCount?.Invoke() ?? count; public int Accesses { get; private set; }
        public T this[int index] { get { Accesses++; return read(index); } }
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException(); IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
