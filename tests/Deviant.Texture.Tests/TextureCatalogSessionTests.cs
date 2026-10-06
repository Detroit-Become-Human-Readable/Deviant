using System.Buffers.Binary;
using System.Text;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Browser;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureCatalogSessionTests
{
    [Fact]
    public async Task DiscoversVerifiedCatalogGroupsWithinLimitAndRetainsDuplicateEvidence()
    {
        using var fixture = new TextureArchiveFixture();
        fixture.WriteArchive();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        await using var session = new TextureCatalogSession(snapshot, revision,
            sessionOptions: new(MaximumCatalogs: 1));
        await session.EnrichmentTask;

        var group = Assert.Single(session.Groups);
        Assert.Equal("Catalog 1", group.Label);
        Assert.Equal((uint)100, group.CatalogId);
        Assert.Equal(4, group.SourceReferenceCount);
        Assert.Equal(2, group.UniqueTextureMembers.Count);
        Assert.Equal(1, group.MissingCount);
        Assert.Equal(0, group.AmbiguousCount);
        Assert.Equal(2, group.DuplicateReferenceCount);
        Assert.Equal(TextureCatalogSessionState.StoppedAtLimit, session.Progress.State);
        Assert.True(session.Progress.DiscoveryIncomplete);
        Assert.Equal(1, session.Progress.CatalogsVisited);
        var target = new ResourceIdentity(snapshot.IndexContentId, 2);
        Assert.Equal(group, Assert.Single(session.FindGroupsForTexture(target)));
        Assert.Same(group, await session.ReadCatalogAsync(group.CatalogIdentity));
        var inverse = await session.FindCatalogRelationshipsForTextureAsync(target);
        var link = Assert.Single(inverse.Catalogs);
        Assert.Equal(group.CatalogExtent, link.Catalog.CatalogExtent);
        Assert.Equal(group.ReferenceTableSha256, link.Catalog.ReferenceTableSha256);
        Assert.Equal(2, link.MatchingReferences.Count);
        Assert.All(link.MatchingReferences, reference => Assert.Equal(ReferenceStatus.Unique, reference.ResolutionStatus));
        Assert.Equal(TextureCatalogSessionState.StoppedAtLimit, inverse.Discovery.State);
        Assert.True(inverse.Discovery.DiscoveryIncomplete);

        await session.RevalidateGroupAsync(group.CatalogIdentity);
    }

    [Fact]
    public async Task RelatedPanelIncludesAmbiguousCandidatesWithoutPromotingThemToUniqueMembers()
    {
        using var fixture = new TextureArchiveFixture();
        fixture.WriteArchive(ambiguousRawIdentity: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        await using var session = new TextureCatalogSession(snapshot, revision,
            sessionOptions: new(MaximumCatalogs: 1));
        await session.EnrichmentTask;
        var selected = new ResourceIdentity(snapshot.IndexContentId, 2);

        Assert.Empty(session.FindGroupsForTexture(selected));
        var result = await session.FindCatalogRelationshipsForTextureAsync(selected);
        var related = Assert.Single(result.Catalogs);
        Assert.Equal(2, related.MatchingReferences.Count);
        Assert.All(related.MatchingReferences, reference =>
        {
            Assert.Equal(ReferenceStatus.Ambiguous, reference.ResolutionStatus);
            Assert.Contains(selected, reference.Candidates);
        });
        Assert.Equal(2, related.Catalog.AmbiguousCount);
        Assert.Single(related.Catalog.UniqueTextureMembers);
        Assert.Equal(2, related.Catalog.DuplicateReferenceCount);
        Assert.Equal(TextureCatalogSessionState.StoppedAtLimit, result.Discovery.State);
        Assert.True(result.Discovery.DiscoveryIncomplete);
    }

    [Fact]
    public async Task RelatedQueryRetainsMalformedAndStoppedDiscoveryStatus()
    {
        using var fixture = new TextureArchiveFixture();
        fixture.WriteArchive(malformedFirstCatalog: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        await using var session = new TextureCatalogSession(snapshot, revision);
        await session.EnrichmentTask;

        var result = await session.FindCatalogRelationshipsForTextureAsync(new(snapshot.IndexContentId, 2));

        Assert.Single(result.Catalogs);
        Assert.Equal(TextureCatalogSessionState.Completed, result.Discovery.State);
        Assert.True(result.Discovery.DiscoveryIncomplete);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedReferenceContainerVersion");
    }

    [Fact]
    public async Task RelatedQueryRejectsForeignArchiveAndMemberIdentities()
    {
        using var fixture = new TextureArchiveFixture();
        fixture.WriteArchive(includeSecondCatalog: false);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        await using var session = new TextureCatalogSession(snapshot, revision);

        var foreign = await Assert.ThrowsAsync<ArchiveException>(() =>
            session.FindCatalogRelationshipsForTextureAsync(new("other-index", 2)));
        var member = await Assert.ThrowsAsync<ArchiveException>(() =>
            session.FindCatalogRelationshipsForTextureAsync(new(snapshot.IndexContentId, 2, "nested/item")));
        var nonTexture = await Assert.ThrowsAsync<ArchiveException>(() =>
            session.FindCatalogRelationshipsForTextureAsync(new(snapshot.IndexContentId, 0)));

        Assert.Equal("InvalidTextureIdentity", foreign.Code);
        Assert.Equal("InvalidTextureIdentity", member.Code);
        Assert.Equal("InvalidTextureIdentity", nonTexture.Code);
    }

    [Fact]
    public async Task RelatedQueryRejectsSameStampReferenceTableChange()
    {
        using var fixture = new TextureArchiveFixture();
        fixture.WriteArchive(includeSecondCatalog: false);
        var revisions = new FixedStampProvider(fixture.IndexPath, fixture.VolumePath);
        var snapshot = await new ArchiveReader(revisions).OpenAsync(fixture.IndexPath);
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        await using var session = new TextureCatalogSession(snapshot, revision,
            referenceReader: new LocalizationReferenceReader(revisions));
        await session.EnrichmentTask;
        var group = Assert.Single(session.Groups);

        var volume = await File.ReadAllBytesAsync(fixture.VolumePath);
        volume[20] ^= 1;
        await File.WriteAllBytesAsync(fixture.VolumePath, volume);

        var error = await Assert.ThrowsAsync<ArchiveException>(() =>
            session.FindCatalogRelationshipsForTextureAsync(new(snapshot.IndexContentId, 2)));

        Assert.Equal("SourceChanged", error.Code);
        Assert.Equal(TextureCatalogSessionState.Failed, session.Progress.State);
        Assert.NotNull(session.Error);
    }

    [Fact]
    public async Task ReferenceRowLimitStopsDiscoveryAndReportsTheSkippedCatalog()
    {
        using var fixture = new TextureArchiveFixture();
        fixture.WriteArchive(includeSecondCatalog: false);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        await using var session = new TextureCatalogSession(snapshot, revision,
            sessionOptions: new(MaximumReferenceRows: 3));

        await session.EnrichmentTask;

        Assert.Empty(session.Groups);
        Assert.Equal(TextureCatalogSessionState.StoppedAtLimit, session.Progress.State);
        Assert.True(session.Progress.DiscoveryIncomplete);
        Assert.Contains(session.Diagnostics, diagnostic => diagnostic.Code == "ReferenceRowLimit");
    }

    [Fact]
    public async Task RejectsRevisionFromAnotherSnapshotBeforeStartingWork()
    {
        using var fixture = new TextureArchiveFixture();
        fixture.WriteArchive(includeSecondCatalog: false);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);

        var error = Assert.Throws<ArchiveException>(() => new TextureCatalogSession(snapshot,
            new SourceRevision(snapshot.IndexContentId, "different-source")));

        Assert.Equal("InvalidTextureCatalogRevision", error.Code);
    }

    private sealed class TextureArchiveFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "DeviantTextureSessionTests", Guid.NewGuid().ToString("N"));
        public string IndexPath => Path.Combine(root, "sample.idx");
        public string VolumePath => Path.Combine(root, "sample.dat");

        public void WriteArchive(bool includeSecondCatalog = true, bool ambiguousRawIdentity = false,
            bool malformedFirstCatalog = false)
        {
            Directory.CreateDirectory(root);
            var first = CreateContainer((2229, 41, 5), (2229, 41, 6), (2230, 42, 7), (2229, 43, 8));
            if (malformedFirstCatalog) BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(8, 4), 5);
            var second = includeSecondCatalog ? CreateContainer((2229, 41, 9)) : [];
            var firstOffset = 0;
            var secondOffset = first.Length;
            var textureOffset = first.Length + second.Length;
            var volume = first.Concat(second).ToArray();
            File.WriteAllBytes(VolumePath, volume);

            var rows = new List<(uint Type, uint Id, uint Offset, uint Size)>
            {
                (3, 100, checked((uint)firstOffset), checked((uint)first.Length))
            };
            if (includeSecondCatalog) rows.Add((3, 101, checked((uint)secondOffset), checked((uint)second.Length)));
            rows.Add((2229, 41, checked((uint)textureOffset), 0));
            if (ambiguousRawIdentity) rows.Add((2229, 41, checked((uint)textureOffset), 0));
            rows.Add((2230, 42, checked((uint)textureOffset), 0));

            var index = new byte[IndexLayout.HeaderLength + rows.Count * IndexLayout.RecordLength];
            IndexLayout.Magic.CopyTo(index);
            WriteBigEndian(index, 20, IndexLayout.SupportedVersion);
            WriteBigEndian(index, 24, 0);
            WriteBigEndian(index, 101, checked((uint)rows.Count));
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var offset = IndexLayout.HeaderLength + i * IndexLayout.RecordLength;
                WriteBigEndian(index, offset, row.Type);
                WriteBigEndian(index, offset + 8, row.Id);
                WriteBigEndian(index, offset + 12, row.Offset);
                WriteBigEndian(index, offset + 16, row.Size);
            }
            File.WriteAllBytes(IndexPath, index);
        }

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        private static byte[] CreateContainer(params (uint Type, uint Id, byte Trailer)[] rows)
        {
            var data = new byte[20 + rows.Length * 9];
            Encoding.ASCII.GetBytes("COM_CONT").CopyTo(data, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 6);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), checked((uint)(4 + rows.Length * 9)));
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), checked((uint)rows.Length));
            for (var i = 0; i < rows.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20 + i * 9, 4), rows[i].Type);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24 + i * 9, 4), rows[i].Id);
                data[28 + i * 9] = rows[i].Trailer;
            }
            return data;
        }

        private static void WriteBigEndian(byte[] data, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
    }

    private sealed class FixedStampProvider : ISourceRevisionProvider
    {
        private readonly Dictionary<string, FileStamp> stamps = new(StringComparer.OrdinalIgnoreCase);

        public FixedStampProvider(params string[] paths)
        {
            foreach (var path in paths)
            {
                var fullPath = Path.GetFullPath(path);
                var length = new FileInfo(fullPath).Length;
                stamps.Add(fullPath, new(true, "fixed-" + Path.GetFileName(fullPath), length, 1, 1, true));
            }
        }

        public FileStamp Capture(string path) => stamps[Path.GetFullPath(path)];
        public FileStamp Capture(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path) => Capture(path);
    }
}
