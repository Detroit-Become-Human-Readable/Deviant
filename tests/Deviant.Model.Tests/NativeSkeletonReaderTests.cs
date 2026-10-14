using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Formats.Models;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class NativeSkeletonReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactProfilePreservesMatricesNamesAndNestedFields(bool nested)
    {
        var sample = Create(nested); var doc = Read(sample);
        Assert.Equal(sample.Frame.Identity, doc.Identity); Assert.Equal(15u, doc.Version);
        Assert.Equal(sample.SkeletonAt, doc.MeshPrefix.NextClassOffset);
        Assert.Equal(128 + sample.SkeletonAt, doc.Extent.Offset);
        Assert.Equal(sample.Bytes.Length - sample.SkeletonAt, doc.Extent.Length);
        Assert.Equal(sample.Entity.Node, doc.EntityNode); Assert.Equal(8u, doc.LeadingScalar); Assert.Equal(19u, doc.TrailingScalar);
        var matrix = Assert.Single(doc.Matrices);
        Assert.Equal(0x7fc01234u, matrix.GetStoredWordBits(0)); Assert.Equal(0x80000000u, matrix.GetStoredWordBits(1));
        for (var i = 0; i < 16; i++) Assert.Equal(matrix.GetStoredWordBits(15 - i), matrix.GetNativeWordBits(i));
        Assert.Equal(64, matrix.Extent.Length); Assert.Equal(128 + sample.SkeletonAt + 20, matrix.Extent.Offset);
        Assert.Equal(new uint[] { 7, 7, 9 }, doc.NameTables[0].Entries.Select(e => e.Key));
        Assert.True(doc.NameTables[0].Entries[0].TryGetUtf8Text(out var name)); Assert.Equal("joint", name);
        Assert.False(doc.NameTables[0].Entries[1].TryGetUtf8Text(out _));
        Assert.True(doc.NameTables[0].Entries[2].TryGetUtf8Text(out var empty)); Assert.Equal("", empty);
        Assert.Single(doc.NameTables[1].Entries);
        if (!nested) { Assert.Empty(doc.NestedRecords); return; }
        var item = Assert.Single(doc.NestedRecords); Assert.Equal((byte)17, item.Version);
        Assert.Equal(77u, item.RawReference.TypeWord); Assert.Equal(999u, item.RawReference.IdWord);
        Assert.Equal(2, item.Words.Count); Assert.Equal(uint.MaxValue, item.Words.GetWordBits(4));
        Assert.Equal(new byte[] { 0, 1, 2, 5, 6, 7, 10 }, item.Objects.Select(o => o.Kind));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, item.Objects.Select(o => o.ExtraByte));
        Assert.Equal(0xaabbccddu, item.Objects[1].LeadingWord); Assert.Equal(0xaabbccddu, item.Objects[6].LeadingWord);
        Assert.Equal(new[] { 8, 0, 24, 60, 60, 7, 0 }, item.Objects.Select(o => o.Terminal.RawBytes.Length));
        Assert.All(item.Objects, o => Assert.Contains(o.Helpers, h => h.Helper == "E" && h.StoredCount == 1 && h.SkipByteCount == 2));
        Assert.All(item.Objects.SelectMany(o => o.Helpers), h => Assert.Equal(h.Section.RawBytes.Length, h.Section.Count * h.Section.StoredStride));
        Assert.All(item.Objects.SelectMany(o => o.Helpers).Where(h => h.Helper is "C" or "D"), h =>
        { Assert.Null(h.StoredCount); Assert.Equal(h.Helper == "C" ? 2 : 3, h.ElementCount); });
        Assert.Equal(4u, item.Scalar64); Assert.Equal(5u, item.Scalar68); Assert.Equal((byte)6, item.Flag);
        Assert.Equal((ushort)7, item.TailA); Assert.Equal((ushort)8, item.TailB);
        Assert.Equal(new byte[] { 0xff, 0, 1 }, item.Label.RawBytes.ToArray());
    }

    [Fact]
    public void RawViewsShareOneOwnedSnapshotAndJsonDoesNotExpandIt()
    {
        var sample = Create(true); var doc = Read(sample); var bits = doc.Matrices[0].GetStoredWordBits(0);
        Array.Fill(sample.Bytes, (byte)0);
        Assert.Equal(bits, doc.Matrices[0].GetStoredWordBits(0)); Assert.Equal(0x7fc01234u, bits);
        Assert.Equal(0xff, doc.NestedRecords[0].Label.RawBytes.Span[0]);
        Assert.Equal(doc.RawPayload.Slice(sample.SkeletonAt + 20, 64), doc.Matrices[0].RawBytes);
        var json = JsonSerializer.Serialize(doc);
        foreach (var property in new[] { "RawPayload", "RawBytes", "StoredBytes", "MeshPrefix", "StoredValue" })
            Assert.DoesNotContain('"' + property + '"', json);
        Assert.Contains("generated-index", json); Assert.Contains("RawReference", json);
    }

    [Fact]
    public void NameTableProjectionRetainsOnlyItsBoundedStoredNameEvidence()
    {
        var sample = Create(true);
        var reader = new NativeSkeletonReader();
        Assert.True(reader.TryReadNameTables(sample.Frame, sample.Entity, out var tables,
            out var diagnostic, maximumRetainedNameEntries: 1), diagnostic);
        Assert.Equal(2, tables.Count);
        Assert.Single(tables[0].Entries);
        Assert.Equal(7u, tables[0].Entries[0].Key);
        Assert.Equal("joint", tables[0].Entries[0].TryGetUtf8Text(out var text) ? text : null);
        Assert.Equal(12u, Assert.Single(tables[1].Entries).Key);
        Assert.Equal(128L + sample.FirstNamesAt + 10, tables[0].Entries[0].NameExtent.Offset);
    }

    [Fact]
    public void ZeroMatrixAndNestedCountsRetainTheRemainingTables()
    {
        var s = Create(false); var at = s.SkeletonAt + 20;
        var bytes = s.Bytes[..at].Concat(s.Bytes[(at + 64)..]).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(s.SkeletonAt + 16), 0);
        var frame = s.Frame with { RawPayload = bytes, PayloadExtent = s.Frame.PayloadExtent with { Length = bytes.Length } };
        Assert.True(new NativeSkeletonReader().TryRead(frame, s.Entity, out var doc, out var diag), diag);
        Assert.Empty(doc!.Matrices); Assert.Empty(doc.NestedRecords); Assert.Equal(19u, doc.TrailingScalar);
        Assert.Equal(3, doc.NameTables[0].Entries.Count); Assert.Single(doc.NameTables[1].Entries);
    }

    [Fact]
    public void EveryTruncatedPayloadAndTrailingBytesAreRejected()
    {
        var sample = Create(true);
        for (var length = 0; length < sample.Bytes.Length; length++)
        {
            var raw = sample.Bytes[..length]; var frame = sample.Frame with { RawPayload = raw,
                PayloadExtent = sample.Frame.PayloadExtent with { Length = length } };
            Assert.False(new NativeSkeletonReader().TryRead(frame, sample.Entity, out var doc, out var diagnostic), $"Accepted {length}.");
            Assert.Null(doc); Assert.NotNull(diagnostic);
        }
        var extra = sample.Bytes.Concat(new byte[] { 0 }).ToArray();
        Assert.False(new NativeSkeletonReader().TryRead(sample.Frame with { RawPayload = extra,
            PayloadExtent = sample.Frame.PayloadExtent with { Length = extra.Length } }, sample.Entity, out _, out var error));
        Assert.Equal("NativeSkeletonEndpointMismatch", error);
    }

    [Theory]
    [InlineData("class", "NativeSkeletonProfileUnsupported")]
    [InlineData("native", "NativeSkeletonProfileUnsupported")]
    [InlineData("version", "NativeSkeletonClassUnsupported")]
    [InlineData("tag", "NativeSkeletonClassUnsupported")]
    [InlineData("nested", "NativeSkeletonNestedVersionUnsupported")]
    [InlineData("kind", "NativeSkeletonKindUnsupported")]
    [InlineData("matrix-count", "NativeSkeletonCountLimit")]
    [InlineData("name-size", "NativeSkeletonCountLimit")]
    public void UnsupportedOrMalformedProfilesHaveStableDiagnostics(string defect, string expected)
    {
        var s = Create(true); var frame = s.Frame;
        switch (defect)
        {
            case "class": frame = frame with { ClassCount = 4 }; break;
            case "native": frame = frame with { NativeType = 2009 }; break;
            case "version": BinaryPrimitives.WriteUInt32LittleEndian(s.Bytes.AsSpan(s.SkeletonAt + 8), 14); break;
            case "tag": s.Bytes[s.SkeletonAt] = 0; break;
            case "nested": s.Bytes[s.NestedAt] = 16; break;
            case "kind": s.Bytes[s.KindsAt] = 3; break;
            case "matrix-count": BinaryPrimitives.WriteUInt32LittleEndian(s.Bytes.AsSpan(s.SkeletonAt + 16), uint.MaxValue); break;
            case "name-size": BinaryPrimitives.WriteUInt32LittleEndian(s.Bytes.AsSpan(s.FirstNamesAt), uint.MaxValue); break;
        }
        Assert.False(new NativeSkeletonReader().TryRead(frame, s.Entity, out var doc, out var diagnostic));
        Assert.Null(doc); Assert.Equal(expected, diagnostic); Assert.Equal(s.Frame.Identity, frame.Identity);
    }

    [Theory]
    [InlineData("member")]
    [InlineData("extent")]
    [InlineData("node")]
    [InlineData("flag")]
    [InlineData("next")]
    public void ForeignSourceAndForgedEntityCannotChangeTheEndpoint(string defect)
    {
        var s = Create(false); var frame = s.Frame; var entity = s.Entity;
        switch (defect)
        {
            case "member": frame = frame with { Identity = frame.Identity with { MemberPath = "object" } }; break;
            case "extent": frame = frame with { PayloadExtent = frame.PayloadExtent with { Container = frame.Identity } }; break;
            case "node": entity = entity with { Node = entity.Node! with { Id = "999" } }; break;
            case "flag": entity = entity with { StoredFlag = 8 }; break;
            case "next": entity = entity with { NextClassOffset = entity.NextClassOffset + 1 }; break;
        }
        Assert.False(new NativeSkeletonReader().TryRead(frame, entity, out var doc, out var diagnostic));
        Assert.Null(doc); Assert.NotNull(diagnostic);
    }

    [Fact]
    public void ExactMetadataBudgetAndAggregateCapsAreEnforced()
    {
        var s = Create(true); var d = Read(s); var reader = new NativeSkeletonReader();
        Assert.True(reader.TryRead(s.Frame, s.Entity, new(MaximumMetadataBytes: d.AccountedMetadataBytes), out _, out var diag), diag);
        Assert.False(reader.TryRead(s.Frame, s.Entity, new(MaximumMetadataBytes: d.AccountedMetadataBytes - 1), out _, out diag));
        Assert.Equal("NativeSkeletonMetadataLimit", diag);
        foreach (var limits in new NativeSkeletonLimits[] { new(MaximumMatrices: 0), new(MaximumNestedRecords: 0),
            new(MaximumPolymorphicRecords: 6), new(MaximumNameEntries: 3), new(MaximumNameBytes: 6),
            new(MaximumRawWords: 2), new(MaximumPayloadBytes: s.Bytes.Length - 1) })
            Assert.False(reader.TryRead(s.Frame, s.Entity, limits, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.TryRead(s.Frame, s.Entity, new(MaximumMatrices: -1), out _, out _));
    }

    [Fact]
    public void CancellationPrecedesParsingAndExplicitTextProjection()
    {
        var s = Create(true); var d = Read(s); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => new NativeSkeletonReader().TryRead(s.Frame, s.Entity, out _, out _, cancel.Token));
        Assert.Throws<OperationCanceledException>(() => d.NameTables[0].Entries[0].TryGetUtf8Text(out _, cancel.Token));
    }

    private static NativeSkeletonDocument Read(Sample s)
    {
        Assert.True(new NativeSkeletonReader().TryRead(s.Frame, s.Entity, out var doc, out var diagnostic), diagnostic); return doc!;
    }

    internal static Sample Create(bool nested)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(5u); Tag("COM_CONT", 6); w.Write(0u); Tag("LOADCONT", 2);
        var entityAt = (int)stream.Position; Tag("ENTITY  ", 7);
        w.Write(2138u); w.Write(22u); for (var i = 0; i < 6; i++) w.Write(0u); w.Write(3u); w.Write((byte)1);
        var meshAt = (int)stream.Position; Tag("MESH____", 29); w.Write(1u); w.Write(2130u); w.Write(50u); w.Write(.5f);
        for (var i = 0; i < 8; i++) w.Write(0u); // fixed24 + flags/group
        w.Write(0u); w.Write(0u); w.Write(0u); w.Write((byte)0); w.Write(new byte[6]); w.Write(new byte[20]);
        var skeletonAt = (int)stream.Position; Tag("SKELETON", 15); w.Write(8u); w.Write(1u);
        w.Write(0x7fc01234u); w.Write(0x80000000u); for (uint i = 2; i < 16; i++) w.Write(i);
        w.Write(nested ? 1u : 0u); var nestedAt = (int)stream.Position; var kindsAt = -1;
        if (nested)
        {
            w.Write((byte)17); w.Write(2u); w.Write(1u); w.Write(uint.MaxValue); w.Write(77u); w.Write(999u);
            byte[] kinds = [0, 1, 2, 5, 6, 7, 10]; w.Write((uint)kinds.Length); kindsAt = (int)stream.Position;
            w.Write(kinds); w.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7 });
            foreach (var kind in kinds)
            {
                switch (kind)
                {
                    case 0: A(); B(); C(); D(); E(); w.Write(new byte[8]); break;
                    case 1: case 10: w.Write(0xaabbccddu); A(); B(); E(); break;
                    case 2: A(); B(); C(); D(); E(); w.Write(new byte[24]); break;
                    case 5: A(); C(); B(); D(); E(); w.Write(new byte[60]); break;
                    case 6: A(); C(); E(); w.Write(new byte[60]); break;
                    case 7: A(); B(); E(); Sized([1, 2, 3]); break;
                }
            }
            Sized([4, 5]); w.Write(1u); w.Write(6u); Sized([0xff, 0, 1]);
            w.Write(4u); w.Write(5u); w.Write((byte)6); w.Write((ushort)7); w.Write((ushort)8);
        }
        w.Write(19u); var firstNames = (int)stream.Position;
        Map([(7u, "joint"u8.ToArray()), (7u, new byte[] { 0xff }), (9u, Array.Empty<byte>())]);
        Map([(12u, "second"u8.ToArray())]);
        var bytes = stream.ToArray(); var container = new ResourceIdentity("generated-index", 1);
        var identity = container with { MemberPath = "dc-object/7/2013/17" };
        var frame = new NativeObjectFrame(identity, 7, 2013, 17, new(container, 0, 8), new(container, 100, 13),
            new(container, 128, bytes.Length), [], bytes, 0, 0, 0, 5);
        var fields = new Dictionary<string, DecodedExtent>();
        foreach (var field in new (string Key, int Offset, int Length)[]
            { ("Node", 0, 8), ("Reference1", 8, 8), ("Reference2", 16, 8), ("Reference3", 24, 8), ("Word32", 32, 4), ("Flag36", 36, 1) })
            fields[field.Key] = new(container, 128 + entityAt + 12 + field.Offset, field.Length);
        var entity = new SceneEntityRecord(identity, 2013, 17, new(IdentifierNamespace.Native, "22", identity, 2138),
            [], 3, 1, 5, meshAt, new(container, 128 + entityAt, 49), fields);
        return new(frame, entity, bytes, skeletonAt, nestedAt, kindsAt, firstNames);

        void Tag(string tag, uint version) { w.Write(Encoding.ASCII.GetBytes(tag)); w.Write(version); }
        void Sized(byte[] b) { w.Write((uint)b.Length); w.Write(b); }
        void A() { w.Write((ushort)9); w.Write((byte)2); w.Write(new byte[] { 10, 11 }); }
        void B() { w.Write((byte)3); w.Write(new byte[] { 12, 13, 14 }); }
        void C() => w.Write(new byte[] { 15, 16 });
        void D() => w.Write(new byte[] { 17, 18, 19 });
        void E() { w.Write((byte)1); w.Write((ushort)20); w.Write((byte)21); w.Write((byte)22); w.Write((ushort)2); w.Write(new byte[] { 23, 24 }); }
        void Map((uint Key, byte[] Name)[] rows)
        {
            w.Write((uint)rows.Sum(row => 6 + row.Name.Length));
            foreach (var row in rows) { w.Write(row.Key); w.Write((ushort)row.Name.Length); w.Write(row.Name); }
        }
    }

    internal sealed record Sample(NativeObjectFrame Frame, SceneEntityRecord Entity, byte[] Bytes,
        int SkeletonAt, int NestedAt, int KindsAt, int FirstNamesAt);
}
