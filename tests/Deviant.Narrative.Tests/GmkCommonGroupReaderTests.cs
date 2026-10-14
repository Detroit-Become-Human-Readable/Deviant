using System.Buffers.Binary;
using System.Collections;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Narrative;
using Xunit;

namespace Deviant.Narrative.Tests;

public sealed class GmkCommonGroupReaderTests
{
    private static readonly ResourceIdentity Parent = new("test-index", 7);
    private static readonly SourceRevision Revision = new("test-index", "source-stamp");
    [Fact]
    public void PreservesCountedItemsStoredTextRawWordsAndExactExtents()
    {
        var name = Encoding.UTF8.GetBytes("Café 雪"); var text = Encoding.UTF8.GetBytes("function(self) return 1 end");
        var bytes = Build(99, new Item(name, text, [1, uint.MaxValue, 3], 1, 5, 8, 42), new(name, [0xff], [], 0xfe, 0, 0, 9));
        var result = Read(bytes); Assert.NotNull(result.Value); Assert.Equal(99U, result.Value.Word16);
        Assert.Equal(2, result.Value.Items.Count); var item = result.Value.Items[0];
        Assert.Equal("dc-object/0/4241/3/group-item/0", item.Identity.MemberPath);
        Assert.Equal(4421U, item.FactoryClassId);
        Assert.Equal("Café 雪", item.StoredName.DisplayText); Assert.Equal(name, item.StoredName.RawBytes.ToArray());
        Assert.Equal(text, item.StoredText.RawBytes.ToArray()); Assert.Equal(141, item.StoredName.TextExtent.Offset);
        Assert.Equal(new uint[] { 1, uint.MaxValue, 3 }, item.Words.Select(x => x.Value));
        Assert.Equal(42U, item.FinalWord); Assert.True(item.ReferencePair!.LookupEligible);
        Assert.Equal(GmkReferenceLookupClassification.Unclassified, item.ReferencePair.LookupClassification);
        Assert.Equal(8, item.ReferencePair.Extent.Length);
        Assert.Null(result.Value.Items[1].ReferencePair); Assert.Equal(0xfe, result.Value.Items[1].ReferenceFlag);
        Assert.False(result.Value.Items[1].StoredText.IsValidUtf8);
        Assert.Equal(9U, result.Value.Items[1].FinalWord);
        Assert.Empty(result.UnresolvedReferences); Assert.Equal("Unclassified", result.UnknownFields["referenceLookupScope"]);
    }
    [Theory]
    [InlineData(uint.MaxValue, 2U, 0U, 2U, false)]
    [InlineData(1U, uint.MaxValue, 1U, 0U, false)]
    [InlineData(0U, 7U, 0U, 7U, false)]
    [InlineData(0x80000000U, 1U, 0x80000000U, 1U, false)]
    [InlineData(1U, 0x80000000U, 1U, 0x80000000U, false)]
    [InlineData(0x7fffffffU, 1U, 0x7fffffffU, 1U, true)]
    public void RetainsRawPairAndMatchesSentinelNormalizationAndSignedGating(uint a, uint b, uint normalizedA, uint normalizedB, bool eligible)
    {
        var result = Read(Build(0, new Item([], [], [], 1, a, b, 0))); Assert.NotNull(result.Value);
        var pair = Assert.Single(result.Value.Items).ReferencePair!;
        Assert.Equal(a, pair.RawWord0); Assert.Equal(b, pair.RawWord1);
        Assert.Equal(normalizedA, pair.NormalizedWord0); Assert.Equal(normalizedB, pair.NormalizedWord1); Assert.Equal(eligible, pair.LookupEligible);
        Assert.Equal(a, BinaryPrimitives.ReadUInt32LittleEndian(pair.RawBytes.Span));
        Assert.Equal(b, BinaryPrimitives.ReadUInt32LittleEndian(pair.RawBytes.Span[4..]));
    }
    [Fact]
    public void RetainsDuplicateNamesAndComputesIeeeNameCrc()
    {
        var result = Read(Build(0, new Item("123456789"u8.ToArray(), [1], [], 0, 0, 0, 0), new("123456789"u8.ToArray(), [2], [], 0, 0, 0, 0)));
        Assert.NotNull(result.Value); Assert.Equal(2, result.Value.Items.Count);
        Assert.Equal(0xcbf43926U, result.Value.Items[0].ComputedNameCrc32);
        Assert.Equal(result.Value.Items[0].ComputedNameCrc32, result.Value.Items[1].ComputedNameCrc32);
        Assert.NotEqual(result.Value.Items[0].Identity, result.Value.Items[1].Identity);
    }
    [Fact]
    public void SnapshotOwnsStoredBytesAndSourceListAfterContainerRelease()
    {
        var bytes = Build(0, new Item([1], [2], [3], 0, 0, 0, 4)); var original = bytes.ToArray();
        var sources = new[] { new SourceExtent("v.bin", 20, bytes.Length) };
        var result = Read(bytes, sources: sources); Assert.NotNull(result.Value);
        Array.Fill(bytes, (byte)0xff); sources[0] = new("other.bin", 0, 0);
        Assert.Equal(original, result.Value.RawPayload.ToArray());
        Assert.Equal(new byte[] { 1 }, result.Value.Items[0].StoredName.RawBytes.ToArray());
        Assert.Equal(new byte[] { 2 }, result.Value.Items[0].StoredText.RawBytes.ToArray());
        Assert.Equal("v.bin", Assert.Single(result.Value.OriginalPhysicalExtents).RelativeSource);
    }
    [Theory]
    [InlineData(0, "GmkCommonGroupClassUnsupported")]
    [InlineData(4, "GmkCommonGroupClassUnsupported")]
    [InlineData(12, "GmkCommonGroupVersionUnsupported")]
    public void GatesUnsupportedProfiles(int at, string code)
    { var bytes = Build(0); bytes[at] ^= 1; Assert.Contains(code, Read(bytes).Diagnostics); }
    [Fact]
    public void RejectsMalformedCountsLengthsReferencesAndTail()
    {
        var bytes = Build(0, new Item([], [], [1], 1, 2, 3, 4));
        Assert.Contains("GmkCommonGroupTruncatedData", Read(bytes[..6]).Diagnostics);
        var items = bytes.ToArray(); Word(items, 20, uint.MaxValue); Assert.Contains("GmkCommonGroupItemLimit", Read(items).Diagnostics);
        var length = bytes.ToArray(); Word(length, 24, uint.MaxValue); Assert.Contains("GmkCommonGroupStringLimit", Read(length).Diagnostics);
        var words = bytes.ToArray(); Word(words, 32, uint.MaxValue); Assert.Contains("GmkCommonGroupWordLimit", Read(words).Diagnostics);
        Assert.Contains("GmkCommonGroupTruncatedData", Read(bytes[..^6]).Diagnostics);
        Assert.Contains("GmkCommonGroupTrailingBytes", Read([.. bytes, 0]).Diagnostics);
        var countBounds = Build(0); Word(countBounds, 20, 1); Assert.Contains("GmkCommonGroupItemBounds", Read(countBounds).Diagnostics);
    }
    [Fact]
    public void EnforcesGlobalWordLimitAcrossItemsAndRetainedBudgets()
    {
        var bytes = Build(0, new Item([1], [2], [1], 0, 0, 0, 0), new([1], [2], [2], 0, 0, 0, 0));
        Assert.Contains("GmkCommonGroupWordLimit", Read(bytes, new(MaximumWords: 1)).Diagnostics);
        Assert.Contains("GmkCommonGroupItemLimit", Read(bytes, new(MaximumItems: 1)).Diagnostics);
        Assert.Contains("GmkCommonGroupStringLimit", Read(bytes, new(MaximumStringBytes: 0)).Diagnostics);
        Assert.Contains("GmkCommonGroupPayloadLimit", Read(bytes, new(MaximumPayloadBytes: 24)).Diagnostics);
        var many = Build(0, Enumerable.Range(0, 100).Select(_ => new Item([], [], [], 0, 0, 0, 0)).ToArray());
        Assert.Contains("GmkCommonGroupMemoryLimit", Read(many, new(MaximumRetainedBytes: 32 * 1024)).Diagnostics);
        var words = Build(0, new Item([], [], Enumerable.Range(0, 1000).Select(i => (uint)i).ToArray(), 0, 0, 0, 0));
        Assert.Contains("GmkCommonGroupMemoryLimit", Read(words, new(MaximumRetainedBytes: 32 * 1024)).Diagnostics);
        var sources = Enumerable.Range(0, 100).Select(_ => new SourceExtent("v.bin", 0, 0)).ToArray();
        Assert.Contains("GmkCommonGroupMemoryLimit", Read(Build(0), new(MaximumRetainedBytes: 32 * 1024), sources).Diagnostics);
        Assert.Contains("GmkCommonGroupInvalidLimits", Read(bytes, new(MaximumWords: -1)).Diagnostics);
    }
    [Fact]
    public void ValidatesIdentityRevisionAndSourceMetadata()
    {
        var frame = Frame(Build(0)); var reader = new GmkCommonGroupReader();
        Assert.Contains("GmkCommonGroupSourceMismatch", reader.Read(frame with { Identity = frame.Identity with { MemberPath = "wrong" } }, Revision, []).Diagnostics);
        Assert.Contains("GmkCommonGroupSourceMismatch", reader.Read(frame, Revision with { IndexContentId = "other" }, []).Diagnostics);
        Assert.Contains("GmkCommonGroupSourceMismatch", reader.Read(frame with { PayloadExtent = frame.PayloadExtent with { Offset = long.MaxValue } }, Revision, []).Diagnostics);
        Assert.Contains("GmkCommonGroupSourceMismatch", reader.Read(frame with { DirectoryEntryExtent = frame.DirectoryEntryExtent with { Container = Parent with { Ordinal = 8 } } }, Revision, []).Diagnostics);
        Assert.Contains("GmkCommonGroupPhysicalSourceInvalid", reader.Read(frame, Revision, [new("/v.bin", 0, 0)]).Diagnostics);
        Assert.Contains("GmkCommonGroupPhysicalSourceInvalid", reader.Read(frame, Revision, [new("v.bin", long.MaxValue, 1)]).Diagnostics);
        Assert.Contains("GmkCommonGroupPhysicalSourceInvalid", reader.Read(frame, Revision, [new("v.bin", 0, 0, "bad")]).Diagnostics);
    }
    [Fact]
    public void BoundsSourcesBeforeIndexingAndThrowsCancellation()
    {
        var hostile = new Sources(int.MaxValue, _ => throw new InvalidOperationException());
        Assert.Contains("GmkCommonGroupPhysicalSourceLimit", Read(Build(0), sources: hostile).Diagnostics); Assert.Equal(0, hostile.Reads);
        using var cts = new CancellationTokenSource(); var sources = new Sources(2, _ => { cts.Cancel(); return new("v.bin", 0, 0); });
        Assert.Throws<OperationCanceledException>(() => Read(Build(0), sources: sources, token: cts.Token)); Assert.Equal(1, sources.Reads);
        Assert.Throws<OperationCanceledException>(() => Read(Build(0), token: cts.Token));
    }
    private static DecodeResult<GmkCommonGroupDocument> Read(byte[] bytes, GmkCommonGroupLimits? limits = null,
        IReadOnlyList<SourceExtent>? sources = null, CancellationToken token = default) => new GmkCommonGroupReader().Read(Frame(bytes), Revision, sources ?? [], limits, token);
    private static NativeObjectFrame Frame(byte[] bytes) => new(new(Parent.IndexContentId, Parent.Ordinal, "dc-object/0/4241/3"), 0, 4241, 3,
        new(Parent, 24, 8), new(Parent, 100, 13), new(Parent, 113, bytes.Length), new byte[13], bytes, 0, 0, 0, 1);
    private static void Word(byte[] bytes, int at, uint word) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), word);
    private sealed record Item(byte[] Name, byte[] Text, uint[] Words, byte Flag, uint Reference0, uint Reference1, uint Final);
    private static byte[] Build(uint word16, params Item[] items)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(1U); writer.Write("GMK_COMG"u8); writer.Write(22U); writer.Write(word16); writer.Write((uint)items.Length);
        foreach (var i in items)
        {
            writer.Write((uint)i.Name.Length); writer.Write(i.Name); writer.Write((uint)i.Text.Length); writer.Write(i.Text);
            writer.Write((uint)i.Words.Length); foreach (var w in i.Words) writer.Write(w);
            writer.Write(i.Flag); if (i.Flag == 1) { writer.Write(i.Reference0); writer.Write(i.Reference1); } writer.Write(i.Final);
        }
        writer.Flush(); return stream.ToArray();
    }
    private sealed class Sources(int count, Func<int, SourceExtent> get) : IReadOnlyList<SourceExtent>
    {
        public int Count => count; public int Reads { get; private set; }
        public SourceExtent this[int i] { get { Reads++; return get(i); } }
        public IEnumerator<SourceExtent> GetEnumerator() => throw new InvalidOperationException(); IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
