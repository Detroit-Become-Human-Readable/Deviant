using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Models;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class NativeMeshPrefixTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ContextModesPreserveStoredFieldsAndReachExactEndpoint(int mode)
    {
        var sample = Create((byte)mode, lods: 4);
        var document = Read(sample);
        Assert.Equal(sample.End, document.NextClassOffset);
        Assert.Equal(sample.Frame.PayloadExtent.Offset + sample.MeshStart, document.PrefixExtent.Offset);
        Assert.Equal(sample.End - sample.MeshStart, document.PrefixExtent.Length);
        Assert.Equal(sample.Frame.PayloadExtent.Offset + sample.End, document.TailExtent.Offset);
        Assert.Equal(sample.Bytes.Length - sample.End, document.TailExtent.Length);
        Assert.Equal(4, document.Lods.Count);
        Assert.All(document.Lods, lod => Assert.Equal(sample.Frame.Identity, lod.MeshData.Owner));
        Assert.Equal(0xff001001u, document.NativeFlags); Assert.Equal(7u, document.GroupIndex);
        Assert.Equal(0x7f800001u, document.FixedFields.Word0); Assert.Equal(uint.MaxValue, document.FixedFields.Word1);
        Assert.Equal(sample.Frame.Identity, document.FixedFields.Reference0.Owner);
        Assert.Equal(0u, document.FixedFields.Reference1.ResourceType);
        Assert.Equal((byte)mode, document.ContextMode);
        Assert.Equal((uint)mode << 24 | 3u, document.PackedContextControl);
        Assert.Equal(3, document.ContextEntries.Count);
        Assert.Equal(new byte[] { 0xff, 0, 0x80 }, document.ContextEntries[0].StoredNameBytes);
        Assert.Empty(document.ContextEntries[1].StoredNameBytes);
        Assert.Equal("MESH____"u8.ToArray(), document.ContextEntries[2].StoredNameBytes);
        Assert.Equal(11, document.RetainedNameBytes);
        for (var i = 0; i < document.ContextEntries.Count; i++)
        {
            var entry = document.ContextEntries[i];
            Assert.Equal(i, entry.Ordinal); Assert.Equal(sample.Frame.Identity, entry.Reference.Owner);
            Assert.Equal((uint)2197, entry.Reference.ResourceType);
            Assert.Equal((100 + i).ToString(CultureInfo.InvariantCulture), entry.Reference.Id);
            Assert.Equal(entry.StoredNameBytes.Length, entry.NameExtent.Length);
            Assert.Equal(8, entry.ReferenceExtent.Length);
            if (mode == 0) { Assert.Null(entry.ControlWord); Assert.Null(entry.ControlExtent); }
            else Assert.Equal(sample.Specs[i].Control, entry.ControlWord);
            if (mode == 2) Assert.Equal(sample.Specs[i].SecondaryBytes, entry.SecondaryByteCount);
            else { Assert.Null(entry.SecondaryByteCount); Assert.Null(entry.SecondaryLengthExtent); }
        }
        Assert.Equal(mode == 0 ? 0 : 12, document.ContextBlobExtent.Length);
        Assert.Equal(mode == 2 ? 6 : 0, document.SecondaryContextBlobExtent.Length);
        Assert.Equal(2, document.ExtraReferences.Count);
        Assert.All(document.ExtraReferences, reference => Assert.Equal(sample.Frame.Identity, reference.Reference.Owner));
        Assert.Equal(0xdeadbeefu, document.FinalFields.Word); Assert.Equal((byte)0xff, document.FinalFields.Flag);
        Assert.Equal(0x665544332211ul, document.FinalFields.RawSixBytes);
        Assert.Equal(new uint[] { uint.MaxValue, 0, 0x7f800000, 0x3f800000, 0xcafebabe }, document.FinalFields.Words);
        Assert.Equal(31, document.FinalFields.Extent.Length);
    }

    [Fact]
    public void ReturnedStoredNamesDoNotBorrowPayloadMemory()
    {
        var sample = Create(3);
        var document = Read(sample);
        var expected = document.ContextEntries.Select(entry => entry.StoredNameBytes.ToArray()).ToArray();
        Array.Fill(sample.Bytes, (byte)0);
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], document.ContextEntries[i].StoredNameBytes);
        Assert.Equal(0xff001001u, document.NativeFlags);
        Assert.Equal(0x665544332211ul, document.FinalFields.RawSixBytes);
    }

    [Fact]
    public void StaticPrefixAndLegacyOpaqueTailKeepTheirOwnAdmission()
    {
        var sample = Create(2, nativeType: 2009);
        var prefix = Read(sample);
        Assert.Equal(sample.End, prefix.NextClassOffset);
        Assert.Equal(0, prefix.TailExtent.Length);
        Assert.True(new NativeMeshEntityReader().TryRead(sample.Frame, sample.Entity, out var legacy, out var diagnostic), diagnostic);
        Assert.Equal(sample.Bytes.Length - sample.MeshStart, legacy!.MeshExtent.Length);
        Assert.Equal(sample.Frame.PayloadExtent.Offset + sample.CoreEnd, legacy.FieldExtents["Tail"].Offset);
        Assert.Equal(sample.Bytes.Length - sample.CoreEnd, legacy.FieldExtents["Tail"].Length);
        var minimal = sample.Bytes[..sample.CoreEnd];
        var frame = sample.Frame with { Identity = sample.Frame.Identity with { MemberPath = "object" },
            RawPayload = minimal, PayloadExtent = sample.Frame.PayloadExtent with { Length = minimal.Length } };
        var entity = sample.Entity with { Identity = frame.Identity,
            Node = sample.Entity.Node! with { Owner = frame.Identity },
            OtherReferences = sample.Entity.OtherReferences.Select(reference => reference with { Owner = frame.Identity }).ToArray() };
        Assert.True(new NativeMeshEntityReader().TryRead(frame, entity, out var opaque, out diagnostic), diagnostic);
        Assert.Equal(0, opaque!.FieldExtents["Tail"].Length);
        Assert.False(new NativeMeshPrefixReader().TryRead(frame, entity, out _, out var strict));
        Assert.Equal("NativeMeshPrefixSourceMismatch", strict);
        Assert.False(new NativeMeshEntityReader().TryRead(Create(3).Frame, Create(3).Entity, out _, out var derived));
        Assert.Equal("NativeMeshEntitySourceMismatch", derived);
    }

    [Theory]
    [InlineData(2009u, 5u)]
    [InlineData(2013u, 4u)]
    [InlineData(2130u, 3u)]
    public void UnsupportedNativeProfilesAreRejected(uint nativeType, uint classes)
    {
        var sample = Create(3);
        var frame = sample.Frame with { NativeType = nativeType, ClassCount = classes };
        Assert.False(new NativeMeshPrefixReader().TryRead(frame, sample.Entity, out _, out var diagnostic));
        Assert.Equal("NativeMeshPrefixProfileUnsupported", diagnostic);
    }

    [Theory]
    [InlineData("member")]
    [InlineData("payload-owner")]
    [InlineData("node-owner")]
    [InlineData("node-extent")]
    [InlineData("entity-word")]
    [InlineData("payload-offset")]
    public void SourceAndEntityProvenanceAreValidated(string defect)
    {
        var sample = Create(1); var frame = sample.Frame; var entity = sample.Entity;
        switch (defect)
        {
            case "member": frame = frame with { Identity = frame.Identity with { MemberPath = "object" } }; break;
            case "payload-owner": frame = frame with { PayloadExtent = frame.PayloadExtent with { Container = frame.Identity } }; break;
            case "node-owner": entity = entity with { Node = entity.Node! with { Owner = frame.Identity with { Ordinal = 2 } } }; break;
            case "node-extent":
                var fields = new Dictionary<string, DecodedExtent>(entity.FieldExtents);
                fields["Node"] = fields["Node"] with { Offset = fields["Node"].Offset + 1 };
                entity = entity with { FieldExtents = fields }; break;
            case "entity-word": entity = entity with { StoredWord = entity.StoredWord + 1 }; break;
            case "payload-offset": frame = frame with { PayloadExtent = frame.PayloadExtent with { Offset = long.MaxValue } }; break;
        }
        Assert.False(new NativeMeshPrefixReader().TryRead(frame, entity, out _, out var diagnostic));
        Assert.Equal("NativeMeshPrefixSourceMismatch", diagnostic);
    }

    [Fact]
    public void EveryTruncatedPrefixIsRejectedAndTagLikeDataIsNotScanned()
    {
        var sample = Create(2);
        for (var length = 0; length < sample.End; length++)
        {
            var bytes = sample.Bytes[..length];
            var frame = sample.Frame with { RawPayload = bytes, PayloadExtent = sample.Frame.PayloadExtent with { Length = length } };
            Assert.False(new NativeMeshPrefixReader().TryRead(frame, sample.Entity, out var document, out var diagnostic), $"Accepted length {length}.");
            Assert.Null(document); Assert.NotNull(diagnostic);
        }
        Assert.Equal(sample.End, Read(sample).NextClassOffset);
    }

    [Theory]
    [InlineData("mesh-version", "NativeMeshClassUnsupported")]
    [InlineData("empty-lods", "NativeMeshLodsEmpty")]
    [InlineData("lod-limit", "NativeMeshCountLimit")]
    [InlineData("lod-type", "NativeMeshLodTypeUnsupported")]
    [InlineData("threshold", "NativeMeshNonFinite")]
    [InlineData("mode", "NativeMeshContextModeUnsupported")]
    [InlineData("contexts", "NativeMeshContextCountLimit")]
    [InlineData("name", "NativeMeshContextNameLimit")]
    [InlineData("blob", "NativeMeshContextBlobLimit")]
    [InlineData("secondary", "NativeMeshContextBlobLimit")]
    [InlineData("references", "NativeMeshExtraReferenceLimit")]
    public void MalformedWidthsCountsAndControlsAreRejected(string defect, string expected)
    {
        var sample = Create(2);
        var at = defect switch
        {
            "mesh-version" => sample.MeshStart + 8, "empty-lods" or "lod-limit" => sample.MeshStart + 12,
            "lod-type" => sample.MeshStart + 16, "threshold" => sample.MeshStart + 24,
            "mode" or "contexts" => sample.PackedAt, "name" => sample.NameCountAt,
            "blob" => sample.ControlAt, "secondary" => sample.ControlAt + 4,
            "references" => sample.ExtraCountAt, _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        var value = defect switch
        {
            "mesh-version" => 30u, "empty-lods" => 0u, "lod-limit" => 65u, "lod-type" => 2131u,
            "threshold" => 0x7fc00000u, "mode" => 4u << 24 | 3u, "contexts" => 2u << 24 | 1025u,
            "name" => 4097u, "blob" => 0xaaffffffu, "secondary" => uint.MaxValue,
            "references" => 4097u, _ => 0u
        };
        BinaryPrimitives.WriteUInt32LittleEndian(sample.Bytes.AsSpan(at), value);
        Assert.False(new NativeMeshPrefixReader().TryRead(sample.Frame, sample.Entity, out var document, out var diagnostic));
        Assert.Null(document); Assert.Equal(expected, diagnostic);
    }

    [Fact]
    public void SupportedCountAndNameBoundariesStayBounded()
    {
        var name = Enumerable.Repeat((byte)0xff, 4096).ToArray();
        var fullName = Read(Create(0, entries: [new(name, 0, 0)]));
        Assert.Equal(4096, fullName.RetainedNameBytes);
        var entries = Enumerable.Range(0, 1024).Select(_ => new Entry([], 0, 0)).ToArray();
        Assert.Equal(1024, Read(Create(0, entries: entries)).ContextEntries.Count);
        Assert.Equal(4096, Read(Create(0, entries: [], extraReferences: 4096)).ExtraReferences.Count);
        Assert.Equal(64, Read(Create(0, entries: [], lods: 64)).Lods.Count);
        var overBudget = Create(0, entries: Enumerable.Range(0, 257).Select(_ => new Entry(name, 0, 0)).ToArray());
        Assert.False(new NativeMeshPrefixReader().TryRead(overBudget.Frame, overBudget.Entity, out _, out var diagnostic));
        Assert.Equal("NativeMeshContextNameBudget", diagnostic);
    }

    [Fact]
    public void CancellationPropagatesWithoutAResult()
    {
        var sample = Create(3);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new NativeMeshPrefixReader().TryRead(sample.Frame, sample.Entity, out _, out _, cancellation.Token));
    }

    private static NativeMeshPrefixDocument Read(Sample sample)
    {
        Assert.True(new NativeMeshPrefixReader().TryRead(sample.Frame, sample.Entity, out var document, out var diagnostic), diagnostic);
        return document!;
    }

    private static Sample Create(byte mode, uint nativeType = 2013, int lods = 1, Entry[]? entries = null, int extraReferences = 2)
    {
        entries ??= [new([0xff, 0, 0x80], 0xa5000008, 3), new([], 0xff000003, 1), new("MESH____"u8.ToArray(), 0x7f000001, 2)];
        var classes = nativeType == 2009 ? 4u : 5u;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(classes); Tag(writer, "COM_CONT", 6); writer.Write(0u); Tag(writer, "LOADCONT", 2);
        var entityAt = (int)stream.Position; Tag(writer, "ENTITY  ", 7);
        writer.Write(2138u); writer.Write(22u); writer.Write(0u); writer.Write(0u);
        writer.Write(2002u); writer.Write(33u); writer.Write(0u); writer.Write(0u); writer.Write(0xaabbccddu); writer.Write((byte)1);
        var meshAt = (int)stream.Position; Tag(writer, "MESH____", 29); writer.Write((uint)lods);
        for (var i = 0; i < lods; i++) { writer.Write(2130u); writer.Write((uint)i + 50); writer.Write(.25f * (i + 1)); }
        writer.Write(0x7f800001u); writer.Write(uint.MaxValue); writer.Write(2140u); writer.Write(44u); writer.Write(0u); writer.Write(0u);
        writer.Write(0xff001001u); writer.Write(7u);
        var coreEnd = (int)stream.Position; var packedAt = coreEnd;
        writer.Write((uint)mode << 24 | (uint)entries.Length);
        var nameCountAt = (int)stream.Position; var controlAt = -1;
        long blob = 0, secondaryBlob = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i]; writer.Write((uint)entry.Name.Length); writer.Write(entry.Name);
            writer.Write(2197u); writer.Write((uint)i + 100);
            if (mode == 0) continue;
            if (i == 0) controlAt = (int)stream.Position;
            writer.Write(entry.Control); blob += entry.Control & 0xffffff;
            if (mode == 2) { writer.Write(entry.SecondaryBytes); secondaryBlob += entry.SecondaryBytes; }
        }
        var marker = "MESH____"u8.ToArray();
        for (long i = 0; i < blob; i++) writer.Write(marker[i % marker.Length]);
        for (long i = 0; i < secondaryBlob; i++) writer.Write((byte)(0xf0 + i % 8));
        var extraAt = (int)stream.Position; writer.Write((uint)extraReferences);
        for (var i = 0; i < extraReferences; i++) { writer.Write(2207u); writer.Write((uint)i + 500); }
        writer.Write(0xdeadbeefu); writer.Write((byte)0xff); writer.Write(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 });
        foreach (var word in new uint[] { uint.MaxValue, 0, 0x7f800000, 0x3f800000, 0xcafebabe }) writer.Write(word);
        var end = (int)stream.Position;
        if (nativeType == 2013) { Tag(writer, "NEXTCLSS", 99); writer.Write(new byte[] { 7, 8, 9 }); }
        var bytes = stream.ToArray(); var container = new ResourceIdentity("generated-index", 1);
        var identity = container with { MemberPath = $"dc-object/7/{nativeType}/17" };
        var frame = new NativeObjectFrame(identity, 7, nativeType, 17, new(container, 0, 8), new(container, 100, 13),
            new(container, 128, bytes.Length), [], bytes, 0, 0, 0, classes);
        var fields = new Dictionary<string, DecodedExtent>();
        foreach (var field in new (string Key, int Offset, int Length)[]
            { ("Node", 0, 8), ("Reference1", 8, 8), ("Reference2", 16, 8), ("Reference3", 24, 8), ("Word32", 32, 4), ("Flag36", 36, 1) })
            fields[field.Key] = new(container, 128 + entityAt + 12 + field.Offset, field.Length);
        var entity = new SceneEntityRecord(identity, nativeType, 17, new(IdentifierNamespace.Native, "22", identity, 2138),
            [new(IdentifierNamespace.Native, "33", identity, 2002)], 0xaabbccdd, 1, classes, meshAt,
            new(container, 128 + entityAt, 49), fields);
        return new(frame, entity, bytes, entries, meshAt, coreEnd, packedAt, nameCountAt, controlAt, extraAt, end);
    }

    private static void Tag(BinaryWriter writer, string tag, uint version) { writer.Write(Encoding.ASCII.GetBytes(tag)); writer.Write(version); }
    private sealed record Entry(byte[] Name, uint Control, uint SecondaryBytes);
    private sealed record Sample(NativeObjectFrame Frame, SceneEntityRecord Entity, byte[] Bytes, Entry[] Specs,
        int MeshStart, int CoreEnd, int PackedAt, int NameCountAt, int ControlAt, int ExtraCountAt, int End);
}
