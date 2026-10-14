using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class PhysicsTypeDeclarationsReaderTests
{
    [Fact]
    public void PreservesForwardReferencesAndCyclesWithinOneTable()
    {
        var bytes = Join([4], [0, 1, 3, 3], [1, 1, 3, 1], [2, 1, 3, 2]);
        var input = Fixture(bytes, "Root\0Child\0Leaf\0tType\0"); var result = Read(input);
        var doc = Assert.IsType<PhysicsTypeDeclarationsDocument>(result.Value);
        Assert.Empty(result.Diagnostics); Assert.Equal(PhysicsTypeDeclarationCoverage.CompleteStructure, doc.Coverage);
        Assert.Equal(1, doc.InitialSlotCount); Assert.Equal(4UL, doc.FinalSlotCount!.RawBits);
        Assert.Equal(new[] { 3, 1, 2 }, doc.Declarations.Select(row => row.Parameters[0].Target!.Slot));
        Assert.All(doc.Declarations, row => Assert.Equal(doc.TypeChunkOrdinal, row.Identity.TypeChunkOrdinal));
        Assert.All(doc.Declarations, row => Assert.Equal(doc.Identity, row.Parameters[0].Target!.Owner));
        Assert.Equal(doc.DeclarationTable.BodyExtent.Offset + 1, doc.Declarations[0].Extent.Offset);
        Assert.Equal(TaggedFixture.Physical, result.Extents); Assert.Equal(nameof(PhysicsTypeDeclarationsReader), result.Steps[^1].Decoder);
    }

    [Theory]
    [InlineData(1, 127UL)] [InlineData(2, 16383UL)] [InlineData(3, 2097151UL)] [InlineData(4, 134217727UL)]
    [InlineData(5, 34359738367UL)] [InlineData(6, 1099511627775UL)] [InlineData(8, 576460752303423487UL)] [InlineData(9, ulong.MaxValue)]
    public void NativeIntegerFormsPreserveUnsignedBitsAndRawEncoding(int width, ulong value)
    {
        var encoding = Integer(value, width); var doc = Read(Fixture(Join([2, 0, 1, 1], encoding), "Root\0vValue\0")).Value!;
        var field = Assert.Single(Assert.Single(doc.Declarations).Parameters).Value;
        Assert.Equal(value, field.RawBits); Assert.Equal(unchecked((int)value), field.NativeInt32);
        Assert.Equal(encoding, field.RawEncoding.ToArray()); Assert.Equal(width, field.Extent.Length);
        Assert.Equal(PhysicsTypeParameterKind.NumericValue, doc.Declarations[0].Parameters[0].Kind);
    }

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(8)] [InlineData(9)]
    public void NonminimalIntegerEncodingsAreAccepted(int width)
    {
        var doc = Read(Fixture(Join([2, 0, 1, 1], Integer(1, width)), "Root\0vValue\0")).Value!;
        Assert.Equal(PhysicsTypeDeclarationCoverage.CompleteStructure, doc.Coverage);
        Assert.Equal(1UL, doc.Declarations[0].Parameters[0].Value.RawBits);
    }

    [Fact]
    public void NegativeNativeNameIndexOmitsNameAndSlotZeroStaysInitial()
    {
        var body = Join([2], Integer(ulong.MaxValue, 9), [1, 0, 0]);
        var doc = Read(Fixture(body, "tType\0")).Value!;
        Assert.Null(doc.Declarations[0].Name); Assert.Equal(-1, doc.Declarations[0].NameIndex.NativeInt32);
        var parameter = doc.Declarations[0].Parameters[0]; Assert.True(parameter.TargetsInitialSlot); Assert.Equal(0, parameter.Target!.Slot);
    }

    [Fact]
    public void OnlyLowercaseRawTSelectsTypeReference()
    {
        var doc = Read(Fixture([2, 0, 1, 1, 127], "Root\0Type\0")).Value!;
        Assert.Equal(PhysicsTypeParameterKind.NumericValue, doc.Declarations[0].Parameters[0].Kind);
        Assert.Null(doc.Declarations[0].Parameters[0].Target);
    }

    [Fact]
    public void EmptyStringSegmentsDoNotShiftNativeIndexes()
    {
        var doc = Read(Fixture([2, 1, 0], "\0Root\0\0Child\0\0")).Value!;
        Assert.Equal(new[] { "Root", "Child" }, doc.Names.Select(name => name.PrintableText)); Assert.Equal("Child", doc.Declarations[0].Name!.PrintableText);
    }

    [Theory]
    [InlineData(250)] [InlineData(251)] [InlineData(252)] [InlineData(253)] [InlineData(254)] [InlineData(255)]
    public void UnsupportedPrefixesStopWithFullIncompleteRecordExtent(byte prefix)
    {
        var doc = Read(Fixture([2, prefix, 0])).Value!;
        Assert.Equal(PhysicsTypeDeclarationCoverage.Partial, doc.Coverage); Assert.Empty(doc.Declarations);
        Assert.Single(doc.UnknownRanges);
        Assert.Contains(doc.UnknownRanges, range => range.Offset == doc.DeclarationTable.BodyExtent.Offset + 1 && range.Length == 2);
    }

    [Fact]
    public void IntegerFailureInsideParameterHasOneWholeIncompleteRecordRange()
    {
        var result = Read(Fixture([2, 0, 1, 1, 249], "Root\0vValue\0")); var doc = result.Value!;
        Assert.Contains("PhysicsTypeIntegerTruncated", result.Diagnostics);
        var range = Assert.Single(doc.UnknownRanges); Assert.Equal(doc.DeclarationTable.BodyExtent.Offset + 1, range.Offset); Assert.Equal(4, range.Length);
    }

    [Theory]
    [InlineData(128)] [InlineData(192)] [InlineData(224)] [InlineData(232)] [InlineData(240)] [InlineData(248)] [InlineData(249)]
    public void TruncatedWideIntegerNeverReadsBeyondLeaf(byte prefix)
    {
        var result = Read(Fixture([2, prefix])); Assert.Contains("PhysicsTypeIntegerTruncated", result.Diagnostics);
        Assert.Empty(result.Value!.Declarations);
    }

    [Fact]
    public void TrailingBytesAndUnterminatedStringTailRemainUnassigned()
    {
        var result = Read(Fixture([2, 0, 0, 0], "Root\0tail") with { Coverage = PhysicsTaggedMetadataCoverage.CompleteFraming }); var doc = result.Value!;
        Assert.Single(doc.Declarations); Assert.Equal(PhysicsTypeDeclarationCoverage.Partial, doc.Coverage);
        Assert.Contains("PhysicsTypeDeclarationTail", result.Diagnostics); Assert.Contains("PhysicsTypeStringTail", result.Diagnostics);
        Assert.Equal(2, doc.UnknownRanges.Count);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void MalformedNamesTargetsAndCountsAreBounded(int kind)
    {
        var bytes = kind switch { 0 => new byte[] { 2, 9, 0 }, 1 => new byte[] { 2, 0, 1, 1, 9 }, _ => new byte[] { 0 } };
        var result = Read(Fixture(bytes, "Root\0tType\0")); Assert.Equal(PhysicsTypeDeclarationCoverage.Partial, result.Value!.Coverage);
        Assert.NotEmpty(result.Diagnostics); Assert.Empty(result.Value.Declarations);
    }

    [Fact]
    public void OwnsOnlySmallLeafBytesAndJsonOmitsRawViews()
    {
        var input = Fixture([2, 0, 1, 1, 7], "Root\0vValue\0", new byte[2 * 1024 * 1024]); var doc = Read(input).Value!;
        var original = input.Chunks.Single(chunk => chunk.Tag == "TNA1").MetadataBody!.Value;
        Assert.True(MemoryMarshal.TryGetArray(original, out var source)); Assert.True(MemoryMarshal.TryGetArray(doc.DeclarationTable.RawBody, out var owned));
        Assert.NotSame(source.Array, owned.Array); Assert.Equal(5, owned.Array!.Length); source.Array![source.Offset] = 255;
        Assert.Equal(2, doc.DeclarationTable.RawBody.Span[0]); Assert.True(doc.RetainedMetadataBytes < 32 * 1024);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(doc));
        Assert.False(json.RootElement.GetProperty("DeclarationTable").TryGetProperty("RawBody", out _));
        Assert.False(json.RootElement.GetProperty("DeclarationTable").TryGetProperty("RawHeader", out _));
        Assert.False(json.RootElement.GetProperty("Declarations")[0].GetProperty("NameIndex").TryGetProperty("RawEncoding", out _));
        Assert.False(json.RootElement.GetProperty("Names")[0].TryGetProperty("RawBytes", out _));
    }

    [Fact]
    public void RejectsHeaderHashParentAndIdentityForgery()
    {
        var input = Fixture([2, 0, 0]); var chunks = input.Chunks.ToArray(); var tna = Array.FindIndex(chunks, chunk => chunk.Tag == "TNA1");
        chunks[tna] = chunks[tna] with { ParentOrdinal = 0 }; Assert.Contains("PhysicsTypeChunkInvalid", Read(input with { Chunks = chunks }).Diagnostics);
        chunks = input.Chunks.ToArray(); chunks[tna].RawHeader[0] ^= 1; Assert.Contains("PhysicsTypeChunkInvalid", Read(input with { Chunks = chunks }).Diagnostics);
        input = Fixture([2, 0, 0]); chunks = input.Chunks.ToArray(); chunks[tna] = chunks[tna] with { MetadataBodySha256 = new string('0', 64) };
        Assert.Contains("PhysicsTypeLeafHashMismatch", Read(input with { Chunks = chunks }).Diagnostics);
        Assert.Contains("PhysicsTypeSourceMismatch", Read(input with { Identity = input.Identity with { MemberPath = "dc-object/00/2201/37" } }).Diagnostics);
    }

    [Fact]
    public void MutationDuringContextCopyIsValidatedOnOwnedBytes()
    {
        var input = Fixture([2, 0, 0]); var leaf = input.Chunks.Single(chunk => chunk.Tag == "TNA1");
        Assert.True(MemoryMarshal.TryGetArray(leaf.MetadataBody!.Value, out var bytes));
        var physical = new CallbackList<SourceExtent>(1, _ => { bytes.Array![bytes.Offset] = 3; return TaggedFixture.Physical[0]; });
        var result = new PhysicsTypeDeclarationsReader().Read(input, physical); Assert.Contains("PhysicsTypeLeafHashMismatch", result.Diagnostics);
    }

    [Fact]
    public void CountAndIndexerCallbacksHonorCancellationBeforeDiagnostics()
    {
        var input = Fixture([2, 0, 0]); using var first = new CancellationTokenSource();
        var count = new CallbackList<SourceExtent>(0, _ => null!, () => { first.Cancel(); return -1; });
        Assert.Throws<OperationCanceledException>(() => new PhysicsTypeDeclarationsReader().Read(input, count, token: first.Token));
        using var second = new CancellationTokenSource(); var indexes = new CallbackList<SourceExtent>(1, _ => { second.Cancel(); return null!; });
        Assert.Throws<OperationCanceledException>(() => new PhysicsTypeDeclarationsReader().Read(input, indexes, token: second.Token));
        var huge = new CallbackList<SourceExtent>(int.MaxValue, _ => throw new InvalidOperationException());
        Assert.Contains("PhysicsTypeContextLimit", new PhysicsTypeDeclarationsReader().Read(input, huge).Diagnostics); Assert.Equal(0, huge.Accesses);
    }

    [Fact]
    public void AggregateBudgetsStopBeforeLargeOutputAllocations()
    {
        var input = Fixture([3, 0, 0, 0, 0]); var context = Read(input, new(MaximumMetadataBytes: 1)); Assert.Null(context.Value);
        var small = Read(input, new(MaximumSlots: 2)); Assert.Contains("PhysicsTypeSlotLimit", small.Diagnostics);
        var strings = Read(input, new(MaximumStrings: 0)); Assert.Contains("PhysicsTypeStringLimit", strings.Diagnostics);
        Assert.Equal(strings.Value!.DeclarationTable.BodyExtent, strings.Value.UnknownRanges[^1]);
        Assert.True(strings.Value.RetainedMetadataBytes <= 512 * 1024);
    }

    internal static PhysicsTaggedMetadataDocument Fixture(byte[] tna, string strings = "Root\0", byte[]? data = null) =>
        new PhysicsTaggedMetadataReader().Read(TaggedFixture.Envelope(TaggedFixture.Root(
            TaggedFixture.Chunk("DATA", 1, data ?? []), TaggedFixture.Chunk("TYPE", 0, Join(
                TaggedFixture.Chunk("TSTR", 1, Encoding.ASCII.GetBytes(strings)), TaggedFixture.Chunk("TNA1", 1, tna))))), TaggedFixture.Physical).Value!;
    private static DecodeResult<PhysicsTypeDeclarationsDocument> Read(PhysicsTaggedMetadataDocument input, PhysicsTypeDeclarationLimits? limits = null) =>
        new PhysicsTypeDeclarationsReader().Read(input, TaggedFixture.Physical, limits: limits);
    private static byte[] Join(params byte[][] bytes) => bytes.SelectMany(value => value).ToArray();
    private static byte[] Integer(ulong value, int width)
    {
        var bytes = new byte[width]; var prefixes = new Dictionary<int, byte> { [1] = 0, [2] = 0x80, [3] = 0xc0, [4] = 0xe0, [5] = 0xe8, [6] = 0xf8, [8] = 0xf0, [9] = 0xf9 };
        for (var index = width - 1; index > 0; index--) { bytes[index] = (byte)value; value >>= 8; }
        bytes[0] = (byte)(prefixes[width] | (width is 6 or 9 ? 0 : value)); return bytes;
    }
    private sealed class CallbackList<T>(int count, Func<int, T> read, Func<int>? getCount = null) : IReadOnlyList<T>
    {
        public int Count => getCount?.Invoke() ?? count; public int Accesses { get; private set; }
        public T this[int index] { get { Accesses++; return read(index); } }
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException(); IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

