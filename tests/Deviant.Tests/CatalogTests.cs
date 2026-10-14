using Deviant.Catalog;
using Deviant.Core;
using Xunit;

namespace Deviant.Tests;

public sealed class CatalogTests : IDisposable
{
    private readonly string cacheRoot = Path.Combine(Path.GetTempPath(), "deviant-catalog-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CatalogQueriesUseExactFiltersAndSearchSyntheticIdentifiers()
    {
        using var catalog = CatalogService.Open(Snapshot(), cacheRoot);

        Assert.Equal(3, catalog.Count(new ResourceQuery(Limit: 1)));
        Assert.Equal(2, catalog.Count(new ResourceQuery(Type: 0x2A)));
        Assert.Equal(1, catalog.Query(new ResourceQuery(Id: 0xABC, Limit: 100)).Single().Ordinal);
        Assert.Equal(1, catalog.Query(new ResourceQuery(Search: "0xabc", Limit: 100)).Single().Ordinal);
        Assert.Equal(2, catalog.Query(new ResourceQuery(Search: "volume:sample.d01", Limit: 100)).Single().Ordinal);
        Assert.Equal(new long[] { 1, 2 }, catalog.Query(new ResourceQuery(Offset: 1, Limit: 2)).Select(r => r.Ordinal));
    }

    [Fact]
    public void InMemoryQueriesMatchPersistedCatalogForFiltersSearchAndPaging()
    {
        var snapshot = Snapshot();
        using var memory = CatalogService.OpenInMemory(snapshot);
        using var persisted = CatalogService.Open(snapshot, cacheRoot);
        ResourceQuery[] queries =
        [
            new(Type: 0x2A, Offset: 1, Limit: 1),
            new(Id: 0xABC, Limit: 2),
            new(Search: "0xabc", Limit: 10),
            new(Search: "id:abc", Limit: 10),
            new(Search: "ordinal:2", Limit: 10),
            new(Search: "volume:sample.d01", Limit: 10),
            new(Search: "%", Limit: 10)
        ];

        foreach (var query in queries)
        {
            Assert.Equal(persisted.Count(query), memory.Count(query));
            Assert.Equal(persisted.Query(query).Select(row => row.Ordinal), memory.Query(query).Select(row => row.Ordinal));
        }
    }

    [Fact]
    public void ReusesRawIndexRowsButRefreshesValidityForTheFreshSnapshot()
    {
        using (var initial = CatalogService.Open(Snapshot(), cacheRoot))
            Assert.False(initial.ReusedIndexRows);

        var changed = Snapshot(missingVolume: true);
        using var reused = CatalogService.Open(changed, cacheRoot);

        Assert.True(reused.ReusedIndexRows);
        Assert.NotEqual(SourceIdentity.DerivedCacheKey(changed.IndexContentId, "earlier-physical-stamp"), reused.CacheKey);
        var affected = reused.Query(new ResourceQuery(Package: 1, Limit: 10));
        Assert.All(affected, row => Assert.Equal(ResourceValidity.MissingVolume, row.Validity));
    }

    [Fact]
    public void RejectsUnsafePagingBounds()
    {
        using var catalog = CatalogService.Open(Snapshot(), cacheRoot);
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.Query(new ResourceQuery(Offset: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.Count(new ResourceQuery(Limit: 10_001)));
    }

    [Fact]
    public void RejectsCacheInsideTheArchiveRoot()
    {
        var snapshot = Snapshot();
        Assert.Throws<ArchiveException>(() => CatalogService.Open(snapshot, snapshot.SourceRoot));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static ArchiveSnapshot Snapshot(bool missingVolume = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "deviant-synthetic-archive");
        var stamp = missingVolume ? new FileStamp(false, "", 0, 0, 0, false) : new FileStamp(true, "volume-1", 1000, 1, 2, true);
        var volumes = new[] { new VolumeSource(0, "sample.dat", Path.Combine(root, "sample.dat"), new FileStamp(true, "volume-0", 1000, 1, 2, true)),
            new VolumeSource(1, "sample.d01", Path.Combine(root, "sample.d01"), stamp) };
        var rows = new[]
        {
            new ResourceRecord(0, 0x10, 0x80000000, 10, 0, 0, 5, 0, "sample.dat"),
            new ResourceRecord(1, 0x2A, 0x80000001, 0xABC, 0, 5, 8, 4, "sample.dat"),
            new ResourceRecord(2, 0x2A, 0x00000002, 0xDEF, 1, 7, 9, 0, "sample.d01",
                missingVolume ? ResourceValidity.MissingVolume : ResourceValidity.Valid,
                missingVolume ? "MissingVolume" : null)
        };
        var diagnostics = missingVolume ? new[] { new SourceDiagnostic("MissingVolume", "A referenced volume is absent.", Package: 1) } : [];
        return new(Path.Combine(root, "sample.idx"), "synthetic-index-content-id", IndexLayout.SupportedVersion,
            0, new byte[73], 3, rows, volumes, new FileStamp(true, "index", 189, 1, 2, true),
            missingVolume ? "different-physical-stamp" : "initial-physical-stamp", diagnostics);
    }
}
