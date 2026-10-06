using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureCatalogMembershipReaderTests
{
    [Fact]
    public void KeepsOnlyTypedRawTextureReferencesAndTheirEvidence()
    {
        var (snapshot, catalog, revision) = CreateSnapshot(offset: 0x1000);
        var relationships = new[]
        {
            CreateRelationship(catalog, 2229, 41, 0x1014, trailer: 1),
            CreateRelationship(catalog, 17, 11, 0x101D, trailer: 0),
            CreateRelationship(catalog, 2230, 42, 0x1026, trailer: 7,
                status: ReferenceStatus.Missing, candidates: [])
        };

        var result = new TextureCatalogMembershipReader().Read(snapshot, catalog, revision, relationships);

        Assert.Equal(catalog, result.CatalogIdentity);
        Assert.Equal(revision, result.Revision);
        Assert.Equal(3, result.SourceReferenceCount);
        Assert.Equal(1, result.NonTextureReferenceCount);
        Assert.Collection(result.TextureReferences,
            raw =>
            {
                Assert.Equal(0, raw.ReferenceIndex);
                Assert.Equal(TextureCatalogEntryKind.Raw, raw.Kind);
                Assert.Equal((byte)1, raw.Trailer);
                Assert.Equal(ReferenceStatus.Unique, raw.ResolutionStatus);
                Assert.Equal(0x1014, raw.Extent.Offset);
                Assert.False(raw.IsDuplicate);
            },
            lod =>
            {
                Assert.Equal(2, lod.ReferenceIndex);
                Assert.Equal(TextureCatalogEntryKind.RawLod, lod.Kind);
                Assert.Equal((byte)7, lod.Trailer);
                Assert.Equal(ReferenceStatus.Missing, lod.ResolutionStatus);
                Assert.Equal(0x1026, lod.Extent.Offset);
            });
    }

    [Fact]
    public void RejectsTextureReferenceWhoseRowCannotFitWithoutOffsetOverflow()
    {
        var (snapshot, catalog, revision) = CreateSnapshot(long.MaxValue - 8);
        var relationship = CreateRelationship(catalog, 2229, 3, long.MaxValue - 1);

        var error = Assert.Throws<ArchiveException>(() =>
            new TextureCatalogMembershipReader().Read(snapshot, catalog, revision, [relationship]));

        Assert.Equal("InvalidTextureReferenceExtent", error.Code);
    }

    private static (ArchiveSnapshot Snapshot, ResourceIdentity Catalog, SourceRevision Revision) CreateSnapshot(long offset)
    {
        const string indexId = "synthetic-index";
        var catalog = new ResourceIdentity(indexId, 0);
        var stamp = new FileStamp(true, "synthetic", 0x10000, 1, 1, true);
        var snapshot = new ArchiveSnapshot("catalog.idx", indexId, 18, 0, [], 1,
            [new ResourceRecord(0, 3, 0, 100, 0, offset, 468, 0, "archive-volume.dat")],
            [new VolumeSource(0, "archive-volume.dat", "archive-volume.dat", stamp)],
            stamp, "synthetic-source", []);
        return (snapshot, catalog, new SourceRevision(indexId, "synthetic-source"));
    }

    private static AssetRelationship CreateRelationship(ResourceIdentity catalog, uint type, uint id,
        long offset, byte trailer = 0, ReferenceStatus status = ReferenceStatus.Unique,
        IReadOnlyList<ResourceIdentity>? candidates = null)
    {
        var reference = new ResourceReference(IdentifierNamespace.Archive, id.ToString(), catalog, type);
        var resolution = new ReferenceResolution(status, candidates ?? [new ResourceIdentity(catalog.IndexContentId, 1)]);
        var evidence = new RelationshipEvidence(true, "COM_CONT v6", "Synthetic typed row.",
            new SourceExtent("archive-volume.dat", offset, 9), SourceReferenceTrailer: trailer);
        return new(catalog, AssetRelationshipKind.Dependency, "COM_CONT reference", reference, resolution, evidence);
    }
}
