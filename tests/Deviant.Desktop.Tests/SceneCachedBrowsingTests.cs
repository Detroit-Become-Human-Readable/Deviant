using System.Reflection;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Scenes;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class SceneCachedBrowsingTests
{
    [Fact]
    public async Task CachedPagesSearchAndCountsAreAvailableBeforeAnyDiscoveryAndOnlyRequestedRowsAreDecoded()
    {
        using var fixture = new CachedSceneArchive(1205);
        var snapshot = await fixture.OpenAsync();
        using (var store = FeatureStore.Open(snapshot, fixture.CacheRoot)) store.UpsertAssets(fixture.Descriptors(snapshot));
        await using var session = await SceneCatalogSession.OpenAsync(snapshot, fixture.Cache(), new AliasStore(fixture.LibraryRoot));
        var first = await session.QueryAsync(limit: 100);
        Assert.Equal(1205, first.Total); Assert.Equal(100, first.Entries.Count);
        Assert.Equal(0, session.Progress.Visited); Assert.Equal(1205, session.Progress.Available);
        var second = await session.QueryAsync(offset: 100, limit: 100);
        Assert.Equal(1205, second.Total); Assert.Equal(100, second.Entries[0].Identity.Ordinal);
        var search = await session.QueryAsync("Generated scene 1204");
        Assert.Equal(1, search.Total); Assert.Equal(1204, Assert.Single(search.Entries).Identity.Ordinal);
        var storeField = typeof(SceneCatalogSession).GetField("store", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var ownedStore = (FeatureStore)storeField.GetValue(session)!;
        Assert.Equal(1205, ownedStore.Count(new(Family: SceneCatalogSession.AssetFamily)));
        await Assert.ThrowsAnyAsync<IOException>(() => session.LoadAsync(first.Entries[0].Identity));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("profile")]
    [InlineData("revision")]
    [InlineData("identity")]
    [InlineData("shape")]
    [InlineData("names_entry")]
    [InlineData("extent_entry")]
    [InlineData("metadata_value")]
    public async Task CorruptOrStaleCachedRowsAreExcludedFromBothCountsAndPagingWithoutDeletingThem(string defect)
    {
        using var fixture = new CachedSceneArchive(12);
        var snapshot = await fixture.OpenAsync();
        var rows = fixture.Descriptors(snapshot);
        using (var store = FeatureStore.Open(snapshot, fixture.CacheRoot))
        {
            store.UpsertAssets(rows);
            var payload = defect switch
            {
                "json" => "{not-json",
                "profile" => JsonSerializer.Serialize(rows[4] with { Metadata = new Dictionary<string, string>(rows[4].Metadata)
                    { ["scene.reader"] = "older-reader" } }),
                "revision" => JsonSerializer.Serialize(rows[4] with { Revision = rows[4].Revision with { PhysicalSourceStamp = "older-stamp" } }),
                "identity" => JsonSerializer.Serialize(rows[4] with { Identity = rows[4].Identity with { Ordinal = 5 } }),
                "shape" => "{}",
                "names_entry" => NestedDefect(rows[4], "Names"),
                "extent_entry" => NestedDefect(rows[4], "Extents"),
                "metadata_value" => NestedDefect(rows[4], "Metadata"),
                _ => throw new ArgumentOutOfRangeException(nameof(defect))
            };
            using var update = Connection(store).CreateCommand();
            update.CommandText = "UPDATE assets_v2 SET payload=$payload WHERE identity=$identity";
            update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$identity", rows[4].Identity.Key);
            update.ExecuteNonQuery();
        }
        await using var session = await SceneCatalogSession.OpenAsync(snapshot, fixture.Cache(), new AliasStore(fixture.LibraryRoot));
        var first = await session.QueryAsync(limit: 5);
        Assert.Equal(11, first.Total); Assert.Equal(5, first.Entries.Count);
        Assert.Equal(new long[] { 0, 1, 2, 3, 5 }, first.Entries.Select(entry => entry.Identity.Ordinal));
        var second = await session.QueryAsync(offset: 5, limit: 5);
        Assert.Equal(11, second.Total); Assert.Equal(6, second.Entries[0].Identity.Ordinal);
        var owned = (FeatureStore)typeof(SceneCatalogSession).GetField("store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        Assert.Equal(12, owned.Count(new(Family: SceneCatalogSession.AssetFamily)));
    }

    [Fact]
    public async Task ChangedSourceGenerationIsRejectedBeforeCachedNamesArePublished()
    {
        using var fixture = new CachedSceneArchive(12);
        var snapshot = await fixture.OpenAsync();
        using (var store = FeatureStore.Open(snapshot, fixture.CacheRoot)) store.UpsertAssets(fixture.Descriptors(snapshot));
        await using var session = await SceneCatalogSession.OpenAsync(snapshot, fixture.Cache(), new AliasStore(fixture.LibraryRoot));
        Assert.NotEmpty((await session.QueryAsync()).Entries);
        await File.AppendAllTextAsync(snapshot.Volumes[0].FilePath, "changed");
        var error = await Assert.ThrowsAsync<ArchiveException>(() => session.QueryAsync());
        Assert.Equal("SourceChanged", error.Code);
    }

    [Fact]
    public async Task CancelledCachedQueryCanBeRetriedWithoutStartingDiscoveryOrChangingCacheRows()
    {
        using var fixture = new CachedSceneArchive(12);
        var snapshot = await fixture.OpenAsync();
        using (var store = FeatureStore.Open(snapshot, fixture.CacheRoot)) store.UpsertAssets(fixture.Descriptors(snapshot));
        await using var session = await SceneCatalogSession.OpenAsync(snapshot, fixture.Cache(), new AliasStore(fixture.LibraryRoot));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.QueryAsync(token: cancellation.Token));
        Assert.Equal(12, (await session.QueryAsync()).Total);
        Assert.Equal(0, session.Progress.Visited);
        await session.DisposeAsync();
        await fixture.Cache().ClearAsync();
    }

    [Fact]
    public async Task CachedSceneStillUsesTheFullNativeReaderWhenSelected()
    {
        var type = typeof(SceneCatalogSessionTests).GetNestedType("SceneArchiveFixture", BindingFlags.NonPublic)!;
        using var fixture = (IDisposable)Activator.CreateInstance(type, nonPublic: true)!;
        type.GetMethod("WriteArchive")!.Invoke(fixture, [false, false]);
        var snapshot = await (Task<ArchiveSnapshot>)type.GetMethod("OpenSnapshotAsync")!.Invoke(fixture, null)!;
        var cache = (CacheManager)type.GetMethod("CreateCache")!.Invoke(fixture, null)!;
        var aliases = new AliasStore((string)type.GetProperty("LibraryRoot")!.GetValue(fixture)!);
        AssetDescriptor descriptor;
        await using (var first = await SceneCatalogSession.OpenAsync(snapshot, cache, aliases))
        { await first.DiscoverMoreAsync(); descriptor = Assert.Single((await first.QueryAsync()).Entries); }
        await using var reopened = await SceneCatalogSession.OpenAsync(snapshot, cache, aliases);
        Assert.Equal(descriptor.Identity, Assert.Single((await reopened.QueryAsync()).Entries).Identity);
        Assert.Equal(0, reopened.Progress.Visited);
        var loaded = await reopened.LoadAsync(descriptor.Identity);
        Assert.NotNull(loaded.Result.Value);
        Assert.NotEmpty(loaded.Result.Value!.Hierarchy.Nodes);
        Assert.Equal(1, reopened.Progress.Available);
    }

    private static SqliteConnection Connection(FeatureStore store) => (SqliteConnection)typeof(FeatureStore)
        .GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

    private static string NestedDefect(AssetDescriptor descriptor, string property)
    {
        var payload = JsonNode.Parse(JsonSerializer.Serialize(descriptor))!.AsObject();
        if (property == "Metadata") payload[property]!.AsObject()["extra"] = 7;
        else payload[property] = new JsonArray(JsonValue.Create(7));
        return payload.ToJsonString();
    }

    private sealed class CachedSceneArchive(int count) : IDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "DeviantCachedSceneTests", Guid.NewGuid().ToString("N"));
        public string CacheRoot => Path.Combine(Root, "cache");
        public string LibraryRoot => Path.Combine(Root, "library");
        private string Index => Path.Combine(Root, "source", "sample.idx");
        public async Task<ArchiveSnapshot> OpenAsync()
        {
            DiscoveryFixture.WriteIndex(Index, Enumerable.Range(0, count).Select(_ => (29u, new byte[] { 1, 2, 3, 4 })).ToArray());
            return await new ArchiveReader().OpenAsync(Index);
        }
        public CacheManager Cache() => new(Path.Combine(Root, "settings"), new(CacheRoot));
        public AssetDescriptor[] Descriptors(ArchiveSnapshot snapshot) => snapshot.Records.Select(row => new AssetDescriptor(
            new(snapshot.IndexContentId, row.Ordinal, SceneSectionReader.MemberPath), new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp),
            SceneCatalogSession.AssetFamily, $"Generated scene {row.Ordinal:D4}", null, AssetAvailability.Available, AssetCompleteness.Partial,
            AssetCapabilities.Json, [], [new(row.VolumeName, row.Offset, row.StoredSize, Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 })))],
            SortKey: row.Ordinal.ToString("D12"))
        {
            SearchTerms = $"Generated scene {row.Ordinal:D4}",
            Metadata = new Dictionary<string, string> { ["scene.reader"] = SceneSectionReader.Version, ["scene.context"] = $"Generated scene {row.Ordinal:D4}" }
        }).ToArray();
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
