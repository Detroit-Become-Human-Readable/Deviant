using System.Buffers.Binary;
using System.Text;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Desktop;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class SceneLightingTextureTests
{
    [Fact]
    public void BindsOnlyExactVerifiedOwnerRevisionAndReferenceSpan()
    {
        var fields = Fields((2137, 5101));
        var reference = fields.TextureReferences[0];
        var native = Native(fields, 5101);
        var good = Entry(fields, reference, new(fields.Identity.IndexContentId, 8101), "Generated texture");
        var wrongOwner = Entry(fields, reference, new(fields.Identity.IndexContentId, 8102), "Wrong owner",
            owner: new(fields.Identity.IndexContentId, fields.Identity.Ordinal + 1));
        var wrongSpan = Entry(fields, reference, new(fields.Identity.IndexContentId, 8103), "Wrong span",
            decodedExtent: new(reference.Extent.Container, reference.Extent.Offset + 1, reference.Extent.Length));
        var wrongField = Entry(fields, reference, new(fields.Identity.IndexContentId, 8104), "Wrong field",
            field: "Stored resource reference");
        var wrongRevision = Entry(fields, reference, new(fields.Identity.IndexContentId, 8105), "Wrong revision",
            revision: new(fields.Revision.IndexContentId, "stale-revision"));
        var result = Result(fields, [good, wrongOwner, wrongSpan, wrongField, wrongRevision]);

        var row = Assert.Single(SceneView.BuildLightingTextureRows(fields,
            [new(ReferenceStatus.Unique, [native])], result));

        Assert.Equal(0, row.Slot);
        Assert.Equal(ReferenceStatus.Unique, row.NativeStatus);
        Assert.Equal(good.Identity, row.Target);
        Assert.Equal(native, row.Preview!.NativeIdentity);
        Assert.Equal(fields.Revision, row.Preview.Revision);
    }

    [Fact]
    public void MemberPathFileTextEntryDoesNotBecomeNavigable()
    {
        var fields = Fields((2137, 5201));
        var reference = fields.TextureReferences[0];
        var fileText = Entry(fields, reference,
            new(fields.Identity.IndexContentId, 8201, "filetext/generated"), "Generated member texture");
        var result = Result(fields, [fileText]);

        var row = Assert.Single(SceneView.BuildLightingTextureRows(fields,
            [new(ReferenceStatus.Unique, [Native(fields, 5201)])], result));

        Assert.Null(row.Target);
        Assert.Equal(ReferenceStatus.Unique, row.NativeStatus);
        Assert.Contains("Unavailable", row.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingAmbiguousAndUnboundNativePairsNeverNavigate()
    {
        var fields = Fields((2137, 5301), (2137, 5302), (0, 0));
        var resolution = new ReferenceResolution[]
        {
            new(ReferenceStatus.Missing, []),
            new(ReferenceStatus.Ambiguous,
                [new(fields.Identity.IndexContentId, 9301), new(fields.Identity.IndexContentId, 9302)]),
            new(ReferenceStatus.Unique, [new(fields.Identity.IndexContentId, 5303)])
        };
        var result = Result(fields, []);

        var rows = SceneView.BuildLightingTextureRows(fields, resolution, result);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Null(row.Target));
        Assert.Equal(ReferenceStatus.Missing, rows[0].NativeStatus);
        Assert.Contains("Missing", rows[0].Label, StringComparison.Ordinal);
        Assert.Equal(ReferenceStatus.Ambiguous, rows[1].NativeStatus);
        Assert.Contains("Ambiguous", rows[1].Label, StringComparison.Ordinal);
        Assert.Equal(ReferenceStatus.Unresolved, rows[2].NativeStatus);
        Assert.Contains("Unbound", rows[2].Label, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsEveryVerifiedArchiveCandidateForOneUniqueNativeSlot()
    {
        var fields = Fields((2137, 5401));
        var reference = fields.TextureReferences[0];
        var first = Entry(fields, reference, new(fields.Identity.IndexContentId, 8401), "Generated texture A");
        var second = Entry(fields, reference, new(fields.Identity.IndexContentId, 8402), "Generated texture B");

        var rows = SceneView.BuildLightingTextureRows(fields,
            [new(ReferenceStatus.Unique, [Native(fields, 5401)])], Result(fields, [first, second]));

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { first.Identity, second.Identity }, rows.Select(row => row.Target));
        Assert.All(rows, row => Assert.Equal(0, row.Slot));
    }

    [Fact]
    public void RejectsTextureResultsForAnotherOwnerOrRevision()
    {
        var fields = Fields((2137, 5501));
        var owner = new ResourceIdentity(fields.Identity.IndexContentId, fields.Identity.Ordinal + 1);
        var wrongOwner = new TextureOwnerQueryResult(owner, fields.Revision,
            new([], 0, 0), TextureOwnerQueryState.Ready, false);
        var wrongRevision = new TextureOwnerQueryResult(fields.Identity,
            new(fields.Revision.IndexContentId, "stale-revision"), new([], 0, 0),
            TextureOwnerQueryState.Ready, false);

        Assert.Throws<InvalidOperationException>(() =>
            SceneView.BuildLightingTextureRows(fields, [], wrongOwner));
        Assert.Throws<InvalidOperationException>(() =>
            SceneView.BuildLightingTextureRows(fields, [], wrongRevision));
    }

    [Fact]
    public void IncompleteOwnerQueryKeepsUniqueSlotPending()
    {
        var fields = Fields((2137, 5601));
        var pending = new TextureOwnerQueryResult(fields.Identity, fields.Revision,
            new([], 1, 0), TextureOwnerQueryState.Pending, true, "More texture discovery is needed.");

        var row = Assert.Single(SceneView.BuildLightingTextureRows(fields,
            [new(ReferenceStatus.Unique, [Native(fields, 5601)])], pending));

        Assert.Null(row.Target);
        Assert.Equal(ReferenceStatus.Unique, row.NativeStatus);
        Assert.Contains("Pending", row.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("Unavailable", row.Label, StringComparison.Ordinal);
        Assert.Contains("More texture discovery is needed.", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesEachCatalogChoiceForOneArchiveTexture()
    {
        var fields = Fields((2137, 5701));
        var identity = new ResourceIdentity(fields.Identity.IndexContentId, 8701);
        var first = CatalogEvidence(fields, identity, new(fields.Identity.IndexContentId, 91));
        var second = CatalogEvidence(fields, identity, new(fields.Identity.IndexContentId, 92));
        var entry = Entry(fields, fields.TextureReferences[0], identity, "Generated texture",
            catalogEvidence: [second, first, second]);

        var rows = SceneView.BuildLightingTextureRows(fields,
            [new(ReferenceStatus.Unique, [Native(fields, 5701)])], Result(fields, [entry]));

        Assert.Equal(new[] { first.Source, second.Source }, rows.Select(row => row.Preview!.CatalogIdentity));
        Assert.All(rows, row =>
        {
            Assert.Equal(identity, row.Preview!.SourceIdentity);
            Assert.Equal(Native(fields, 5701), row.Preview.NativeIdentity);
            Assert.Equal(fields.Revision, row.Preview.Revision);
        });
    }

    [Fact]
    public void MissingAndMismatchedCatalogEvidenceCannotCreateAPreview()
    {
        var fields = Fields((2137, 5801));
        var identity = new ResourceIdentity(fields.Identity.IndexContentId, 8801);
        var valid = CatalogEvidence(fields, identity, new(fields.Identity.IndexContentId, 93));
        TextureEvidence[][] candidates =
        [
            [], [valid with { Field = "Typed archive reference 2229/1" }],
            [valid with { Field = $"Typed archive reference 2230/{identity.Ordinal}" }],
            [valid with { Revision = new(fields.Revision.IndexContentId, "stale-revision") }],
            [valid with { Source = valid.Source with { MemberPath = "generated/member" } }],
            [valid with { Source = new("another-index", valid.Source.Ordinal) }],
            [valid with { Reader = "AnotherReader" }], [valid with { ReaderVersion = "another-version" }],
            [valid with { PhysicalExtent = valid.PhysicalExtent! with { Length = 8 } }],
            [valid with { SourceSha256 = new string('b', 64) }]
        ];
        foreach (var evidence in candidates)
        {
            var entry = Entry(fields, fields.TextureReferences[0], identity, "Generated texture", catalogEvidence: evidence);
            var row = Assert.Single(SceneView.BuildLightingTextureRows(fields,
                [new(ReferenceStatus.Unique, [Native(fields, 5801)])], Result(fields, [entry])));
            Assert.Null(row.Target);
            Assert.Null(row.Preview);
        }
    }

    internal static TextureOwnerQueryResult Result(IblLightStoredFields fields,
        IReadOnlyList<TextureDiscoveryEntry> entries) => new(fields.Identity, fields.Revision,
        new(entries, entries.Count, 0), TextureOwnerQueryState.Ready, false);

    internal static TextureDiscoveryEntry Entry(IblLightStoredFields fields, TextureContextReference reference,
        ResourceIdentity identity, string label, ResourceIdentity? owner = null,
        DecodedExtent? decodedExtent = null, SourceRevision? revision = null, string field = "Native texture reference",
        IReadOnlyList<TextureEvidence>? catalogEvidence = null)
    {
        var evidence = new TextureEvidence(fields.Identity, revision ?? fields.Revision,
            nameof(TextureContextReader), TextureContextReader.Version, new string('a', 64), field,
            DecodedExtent: decodedExtent ?? reference.Extent);
        var link = new TextureDiscoveryLink(owner ?? fields.Identity, "Generated light owner",
            TextureRelationshipKind.Object, true, true,
            [evidence, .. catalogEvidence ?? [CatalogEvidence(fields, identity, new(fields.Identity.IndexContentId, 90))]]);
        return new(identity, checked((uint)identity.Ordinal), 2229, label, TextureNameKind.Fallback,
            null, null, null, null, null, null, null, [link], [], [], [], [], [], "generated-texture", label);
    }

    internal static TextureEvidence CatalogEvidence(IblLightStoredFields fields, ResourceIdentity texture,
        ResourceIdentity catalog) => new(catalog, fields.Revision, nameof(TextureCatalogMembershipReader),
        "catalog-membership-v1", new string('a', 64), $"Typed archive reference 2229/{texture.Ordinal}",
        new SourceExtent("generated.dat", 20, 9, new string('a', 64)));

    private static ResourceIdentity Native(IblLightStoredFields fields, uint id) =>
        fields.Identity with { MemberPath = $"generated/texture/{id}" };

    private static IblLightStoredFields Fields(params (uint Type, uint Id)[] pairs) => FieldsFor(
        new("generated-index", 17, "generated/entity"), new("generated-index", "generated-revision"),
        "Generated light", Bits(2), pairs);

    internal static IblLightStoredFields FieldsFor(ResourceIdentity identity, SourceRevision revision,
        string ownerName, uint intensityBits, params (uint Type, uint Id)[] pairs)
    {
        const int tagOffset = 3;
        const string sourcePath = "assets/generated-light.bin";
        var ownerBytes = Encoding.ASCII.GetBytes(ownerName);
        var pathBytes = Encoding.ASCII.GetBytes(sourcePath);
        var firstIndexOffset = checked(95 + pairs.Length * 8);
        var ownerLengthOffset = firstIndexOffset + 4;
        var ownerValueOffset = ownerLengthOffset + 4;
        var pathLengthOffset = ownerValueOffset + ownerBytes.Length;
        var pathValueOffset = pathLengthOffset + 4;
        var trailingOffset = pathValueOffset + pathBytes.Length;
        var record = new byte[trailingOffset + 8];
        "IBLLIGHT"u8.CopyTo(record);
        WriteWord(record, 8, 13);
        record[12] = 1;
        for (var index = 0; index < 12; index++) WriteWord(record, 13 + index * 4, Bits(index % 2));
        WriteWord(record, 61, 2229); WriteWord(record, 65, 7999);
        WriteWord(record, 69, Bits(1)); WriteWord(record, 73, intensityBits); WriteWord(record, 77, Bits(3));
        record[81] = 1; record[82] = 0; WriteWord(record, 83, Bits(4));
        WriteWord(record, 87, checked((uint)pairs.Length)); WriteWord(record, 91, 0x12345678);
        for (var index = 0; index < pairs.Length; index++)
        {
            WriteWord(record, 95 + index * 8, pairs[index].Type);
            WriteWord(record, 99 + index * 8, pairs[index].Id);
        }
        WriteWord(record, firstIndexOffset, 0);
        WriteWord(record, ownerLengthOffset, checked((uint)ownerBytes.Length));
        ownerBytes.CopyTo(record, ownerValueOffset);
        WriteWord(record, pathLengthOffset, checked((uint)pathBytes.Length));
        pathBytes.CopyTo(record, pathValueOffset);
        WriteWord(record, trailingOffset, Bits(5)); WriteWord(record, trailingOffset + 4, Bits(6));

        var container = identity with { MemberPath = null };
        var body = new byte[tagOffset + record.Length + 2];
        record.CopyTo(body, tagOffset);
        var bodyExtent = new DecodedExtent(container, 0x1200, body.Length);
        var parsed = new TextureContextReader().ParseIblLightV13(body, tagOffset, tagOffset + record.Length,
            identity, revision, bodyExtent, []);
        var context = Assert.IsType<TextureIblLightV13Context>(parsed.Value);
        var projected = IblLightStoredFieldsProjection.Project(context);
        return Assert.IsType<IblLightStoredFields>(projected.Value);
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    private static void WriteWord(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)), value);
}
