using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Deviant.Assets;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class NativeNodeArrayBodyReaderTests
{
    [Fact]
    public void ReadsExactBodyWithRawComponentsLinksAndBorrowedSections()
    {
        var prefix = Prefix([new(uint.MaxValue, [1]), new(0, [])], secondary: 2, optional: [4, 5, 6], tables: 1);
        var result = new NativeNodeArrayBodyReader().Read(prefix);
        var doc = Assert.IsType<NativeNodeArrayBodyDocument>(result.Value);
        Assert.Empty(doc.StructuralDiagnostics); Assert.Same(prefix.Value, doc.Owner);
        Assert.Equal(2, doc.Nodes.Count); Assert.Equal(new uint[] { 1 }, doc.Nodes[0].Children);
        Assert.Equal(uint.MaxValue, doc.Nodes[0].StoredParent); Assert.Equal(0u, doc.Nodes[1].StoredParent);
        Assert.Equal(new Vector4(2, 3, 4, 1), doc.Nodes[0].Components.NativeFirstBlock);
        Assert.Equal(new Vector3(5, 6, 7), doc.Nodes[0].Components.VectorAt16);
        Assert.Equal(new Vector3(8, 9, 10), doc.Nodes[0].Components.VectorAt32);
        Assert.Equal(3, doc.CompactRecords.Count); Assert.Equal(new byte[] { 4, 5, 6 }, doc.OptionalObject.RawBytes.ToArray());
        Assert.Equal(new[] { 20, 8, 32, 16 }, doc.RemainingTables.Select(x => x.RawBytes.Length));
        Assert.True(MemoryMarshal.TryGetArray(doc.Owner.RawPayload, out var source));
        Assert.True(MemoryMarshal.TryGetArray(doc.Nodes[0].Components.RawBytes, out var pose));
        Assert.True(MemoryMarshal.TryGetArray(doc.OptionalObject.RawBytes, out var optional));
        Assert.Same(source.Array, pose.Array); Assert.Same(source.Array, optional.Array);
        Assert.Equal(doc.Owner.InheritedNode.PayloadExtent.Offset + pose.Offset, doc.Nodes[0].Components.Extent.Offset);
        Assert.Equal(prefix.Identity, doc.Identity); Assert.Equal(prefix.Revision, doc.Revision);
        Assert.NotNull(result.Steps.Last().PayloadSha256);
    }

    [Theory]
    [InlineData(0)] [InlineData(7)] [InlineData(40)] [InlineData(90)]
    public void RejectsTruncatedStoredArrays(int remove)
    {
        var p = Prefix([new(uint.MaxValue, [])]);
        var bytes = p.Value!.RawPayload.ToArray(); Array.Resize(ref bytes, bytes.Length - remove - 1);
        var r = new NativeNodeArrayBodyReader().Read(FromBytes(bytes));
        Assert.Null(r.Value); Assert.Contains("NodeArrayBodyTruncated", r.Diagnostics);
    }

    [Fact]
    public void RejectsUnknownVersionExtraObjectBranchAndRemainingBytesWithOriginalIdentity()
    {
        var p = Prefix([new(uint.MaxValue, [])]); var bytes = p.Value!.RawPayload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(208), 13);
        var version = new NativeNodeArrayBodyReader().Read(FromBytes(bytes));
        Assert.Null(version.Value); Assert.Contains("NodeArrayBodyVersionUnsupported", version.Diagnostics); Assert.Equal(p.Identity, version.Identity);
        var extra = new NativeNodeArrayBodyReader().Read(Prefix([new(uint.MaxValue, [])], extra: 1));
        Assert.Null(extra.Value); Assert.Contains("NodeArrayBodyExtraObjectsUnsupported", extra.Diagnostics); Assert.Equal(p.Identity, extra.Identity);
        var trailing = new NativeNodeArrayBodyReader().Read(FromBytes(p.Value.RawPayload.ToArray().Concat(new byte[1]).ToArray()));
        Assert.Null(trailing.Value); Assert.Contains("NodeArrayBodyEndpointMismatch", trailing.Diagnostics);
    }

    [Fact]
    public void RetainsInvalidIndexesDuplicatesAndCycleDataAsStructuralDiagnostics()
    {
        var p = Prefix([new(1, [1, 1, 99]), new(0, [0])]);
        var doc = Assert.IsType<NativeNodeArrayBodyDocument>(new NativeNodeArrayBodyReader().Read(p).Value);
        Assert.Equal(new uint[] { 1, 1, 99 }, doc.Nodes[0].Children);
        Assert.Contains(doc.StructuralDiagnostics, d => d.Code == "NodeArrayChildIndexInvalid");
        Assert.Contains(doc.StructuralDiagnostics, d => d.Code == "NodeArrayChildDuplicate");
        Assert.Contains(doc.StructuralDiagnostics, d => d.Code == "NodeArrayIndexCycle");
        Assert.Contains(doc.StructuralDiagnostics, d => d.Code == "NodeArrayStoredParentCycle");
        var invalid = Assert.IsType<NativeNodeArrayBodyDocument>(new NativeNodeArrayBodyReader().Read(Prefix([new(99, [])])).Value);
        Assert.Equal(99u, invalid.Nodes[0].StoredParent);
        Assert.Contains(invalid.StructuralDiagnostics, d => d.Code == "NodeArrayParentIndexInvalid");
    }

    [Fact]
    public void DetectsStoredOnlyCycleAndParentListDisagreement()
    {
        var doc = Assert.IsType<NativeNodeArrayBodyDocument>(new NativeNodeArrayBodyReader().Read(
            Prefix([new(1, []), new(0, [])])).Value);
        Assert.Contains(doc.StructuralDiagnostics, d => d.Code == "NodeArrayStoredParentCycle");
        Assert.Contains(doc.StructuralDiagnostics, d => d.Code == "NodeArrayParentChildMismatch");
    }

    [Fact]
    public void RetainsNonFiniteComponentBitsAndLimitsDiagnostics()
    {
        var p = Prefix([new(uint.MaxValue, [99, 99])]); var bytes = p.Value!.RawPayload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(224), 0x7fc01234);
        var result = new NativeNodeArrayBodyReader().Read(FromBytes(bytes), new(MaximumDiagnostics: 2));
        var doc = Assert.IsType<NativeNodeArrayBodyDocument>(result.Value);
        Assert.Equal(0x7fc01234u, doc.Nodes[0].Components.GetBits(0));
        Assert.True(float.IsNaN(doc.Nodes[0].Components.GetValue(0)));
        Assert.Equal(2, doc.StructuralDiagnostics.Count);
        Assert.Contains(doc.StructuralDiagnostics, d => d.Code == "NodeArrayBodyDiagnosticLimit");
    }

    [Fact]
    public void RejectsForgedPrefixIdentityRevisionExtentsAndInheritedFieldExtents()
    {
        var p = Prefix([new(uint.MaxValue, [])]); var o = p.Value!;
        Assert.Contains("NodeArrayBodyPrefixMismatch", new NativeNodeArrayBodyReader().Read(p with
        { Value = o with { Identity = o.Identity with { MemberPath = "alias" } } }).Diagnostics);
        Assert.Contains("NodeArrayBodyPrefixMismatch", new NativeNodeArrayBodyReader().Read(p with
        { Value = o with { Revision = new("other-index", "stamp") } }).Diagnostics);
        Assert.Contains("NodeArrayBodyPrefixMismatch", new NativeNodeArrayBodyReader().Read(p with
        { Value = o with { BodyExtent = o.BodyExtent with { Offset = o.BodyExtent.Offset + 1 } } }).Diagnostics);
        var fields = o.InheritedNode.FieldExtents.ToDictionary(x => x.Key, x => x.Value);
        fields["Parent"] = fields["Parent"] with { Offset = 0 };
        Assert.Contains("NodeArrayBodyPrefixMismatch", new NativeNodeArrayBodyReader().Read(p with
        { Value = o with { InheritedNode = o.InheritedNode with { FieldExtents = fields } } }).Diagnostics);
    }

    [Fact]
    public void EnforcesAggregateBudgetsAndPreservesMissingPrefixDiagnostic()
    {
        var p = Prefix([new(uint.MaxValue, [1, 1]), new(0, [1])]); var reader = new NativeNodeArrayBodyReader();
        Assert.Contains("NodeArrayBodyCountLimit", reader.Read(p, new(MaximumPrimaryNodes: 1)).Diagnostics);
        Assert.Contains("NodeArrayBodyChildLimit", reader.Read(p, new(MaximumChildReferences: 2)).Diagnostics);
        Assert.Contains("NodeArrayBodyMemoryLimit", reader.Read(p, new(MaximumMetadataBytes: 1)).Diagnostics);
        Assert.Contains("NodeArrayPrefixUnavailable", reader.Read(p with { Value = null }).Diagnostics);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Read(p, new(MaximumDiagnostics: 0)));
    }

    [Fact]
    public void BoundsProvenanceBeforeAccessAndCancelsIndexedCopies()
    {
        var p = Prefix([new(uint.MaxValue, [])]); var reader = new NativeNodeArrayBodyReader();
        Assert.Contains("NodeArrayBodyMetadataLimit", reader.Read(p with { Extents = new CountOnly<SourceExtent>(2) },
            new(MaximumPhysicalExtents: 1)).Diagnostics);
        Assert.Contains("NodeArrayBodyPhysicalExtentInvalid", reader.Read(p with { Extents = [null!] }).Diagnostics);
        Assert.Contains("NodeArrayBodyStepInvalid", reader.Read(p with { Steps = [null!] }).Diagnostics);
        Assert.Contains("NodeArrayBodyPhysicalExtentInvalid", reader.Read(p with { Extents = [new(new string('a', 4097), 0, 1)] }).Diagnostics);
        using var cancelled = new CancellationTokenSource();
        var list = new Cancelling<SourceExtent>(p.Extents[0], cancelled);
        Assert.Throws<OperationCanceledException>(() => reader.Read(p with { Extents = list }, token: cancelled.Token));
    }

    [Fact]
    public void CancellationIsNotAProfileFailureAndLargeChainsUseIterativeChecks()
    {
        var nodes = Enumerable.Range(0, 3000).Select(i => new Spec(i == 0 ? uint.MaxValue : (uint)i - 1,
            i == 2999 ? [] : [(uint)i + 1])).ToArray();
        var p = Prefix(nodes); var result = new NativeNodeArrayBodyReader().Read(p);
        Assert.Empty(Assert.IsType<NativeNodeArrayBodyDocument>(result.Value).StructuralDiagnostics);
        using var c = new CancellationTokenSource(); c.Cancel();
        Assert.Throws<OperationCanceledException>(() => new NativeNodeArrayBodyReader().Read(p, token: c.Token));
    }

    private sealed record Spec(uint Parent, uint[] Children);
    private static DecodeResult<NativeNodeArrayDocument> Prefix(Spec[] nodes, int secondary = 0,
        byte[]? optional = null, int tables = 0, uint extra = 0)
    {
        using var s = new MemoryStream(); using var w = new BinaryWriter(s);
        var baseNode = SceneFixture.Frame(0, 7, Matrix4x4.Identity).RawPayload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(baseNode, 2); w.Write(baseNode); w.Write("NODEAR  "u8); w.Write(4u); w.Write(14u);
        w.Write(nodes.Length); w.Write(secondary);
        for (var i = 0; i < nodes.Length; i++)
        {
            w.Write(66u); for (var j = 1; j <= 10; j++) w.Write((float)j);
            w.Write(123u); w.Write(nodes[i].Parent); w.Write(nodes[i].Children.Length);
            foreach (var child in nodes[i].Children) w.Write(child); w.Write(456u);
        }
        for (var i = 0; i < nodes.Length; i++) { w.Write((uint)i + 100); w.Write((ushort)2); w.Write((ushort)i); w.Write(0x12345678u); }
        optional ??= []; w.Write(optional.Length); w.Write(optional); w.Write(nodes.Length + 1);
        for (var i = 0; i <= nodes.Length; i++) { w.Write((uint)i); w.Write((ushort)i); w.Write((ushort)0); }
        w.Write(1u); w.Write((byte)0); w.Write(extra); w.Write(2u); w.Write(3u); w.Write(tables);
        w.Write(new byte[tables * 20 + secondary * 28]); return FromBytes(s.ToArray());
    }
    private static DecodeResult<NativeNodeArrayDocument> FromBytes(byte[] bytes)
    {
        var container = SceneFixture.Container; var id = new ResourceIdentity(container.IndexContentId, container.Ordinal, "dc-object/0/2138/7");
        var frame = new NativeObjectFrame(id, 0, 2138, 7, new(container, 24, 8), new(container, 512, 13),
            new(container, 525, bytes.Length), new byte[13], bytes, 0, 0, 0, 2);
        return new NativeNodeArrayReader().Read(frame, SceneFixture.Revision, SceneFixture.Physical);
    }
    private sealed class CountOnly<T>(int count) : IReadOnlyList<T>
    {
        public int Count => count; public T this[int i] => throw new InvalidOperationException("Over-limit access.");
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Enumeration.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class Cancelling<T>(T value, CancellationTokenSource c) : IReadOnlyList<T>
    {
        public int Count => 1; public T this[int i] { get { c.Cancel(); return value; } }
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Enumeration.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
