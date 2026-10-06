using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Tests;

public sealed class AssetCatalogProjectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-projection-tests", Guid.NewGuid().ToString("N"));
    private readonly ArchiveSnapshot snapshot;

    public AssetCatalogProjectionTests()
    {
        Directory.CreateDirectory(root);
        snapshot = new ArchiveSnapshot(Path.Combine(root, "source", "archive.idx"), "projection-content", 18, 0, [], 0,
            [], [], new(false, "", 0, 0, 0, false), "projection-revision", []);
    }

    [Fact]
    public void KeyProjectionMatchesDescriptorQueryAcrossFiltersSortsAndPages()
    {
        using var store = FeatureStore.Open(snapshot, Path.Combine(root, "cache"));
        store.UpsertAssets([
            Row(20, "Line 10", "bank-a", "ENG", "PCM", 3.2, 2, "DUPLICATE", "voice line ten"),
            Row(2, "Line 2", "bank-a", "ENG", "Vorbis", 2.1, 1, "DUPLICATE", "voice line two"),
            Row(3, "Line 2", "bank-a", "FRE", "PCM", null, 2, "DUPLICATE", "voix line two"),
            Row(4, "街声 3", "bank-b", "JPN", "PCM", 1.5, 2, "UNICODE", "街声 dialogue"),
            Row(5, "Line 20", "bank-b", "ENG", "Vorbis", 8, 2, "OTHER", "voice line twenty"),
            Row(6, "Line 1", "bank-c", "ENG", "PCM", 0.5, 1, "OTHER", "voice line one", AssetAvailability.Missing)
        ]);

        var queries = new[]
        {
            new AssetQuery(Family: "strings", Sort: AssetSort.Default, Offset: 1, Limit: 3),
            new AssetQuery(GroupKey: "bank-a", Language: "ENG", Sort: AssetSort.Name, Descending: true, Offset: 1, Limit: 1),
            new AssetQuery(Search: "街声", Family: "strings", Sort: AssetSort.Name),
            new AssetQuery(Codec: "PCM", MinimumDuration: 1, MaximumDuration: 4, Channels: 2, Availability: AssetAvailability.Available,
                Completeness: AssetCompleteness.Complete, Sort: AssetSort.Duration, Descending: true, Limit: 10),
            new AssetQuery(ExactKey: "DUPLICATE", Family: "strings", Sort: AssetSort.Language, Descending: true, Limit: 10),
            new AssetQuery(GroupKey: "bank-b", Search: "voice", Sort: AssetSort.Completeness, Descending: true, Limit: 2)
        };

        foreach (var query in queries)
        {
            var expected = store.Query(query).Select(descriptor => descriptor.Identity.Key).ToArray();
            Assert.Equal(expected, store.QueryIdentityKeys(query));
        }

        var duplicates = store.QueryIdentityKeys(new AssetQuery(ExactKey: "DUPLICATE", Family: "strings", Limit: 10));
        Assert.Equal(3, duplicates.Count);
        Assert.Equal(3, duplicates.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void KeyProjectionDoesNotDeserializeMalformedDescriptorPayloads()
    {
        var cache = Path.Combine(root, "cache");
        using var store = FeatureStore.Open(snapshot, cache);
        var descriptor = Row(7, "Alias", "bank", "ENG", "PCM", 1, 2, "KEY", "indexed search term");
        store.UpsertAssets([descriptor]);

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(IndexLayout.ParserVersion + "\n" +
            SourceIdentity.IndexCacheKey(snapshot.IndexContentId)))).ToLowerInvariant();
        var database = Path.Combine(cache, "catalog-" + key + ".sqlite3");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE assets_v2 SET payload='not-json' WHERE identity=$identity";
            command.Parameters.AddWithValue("$identity", descriptor.Identity.Key);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var query = new AssetQuery(Family: "strings", Search: "indexed search term");
        Assert.Equal([descriptor.Identity.Key], store.QueryIdentityKeys(query));
        Assert.Throws<JsonException>(() => store.Query(query));
    }

    [Fact]
    public void KeyProjectionCancellationLeavesConnectionUsable()
    {
        using var store = FeatureStore.Open(snapshot, Path.Combine(root, "cache"));
        store.UpsertAssets(Enumerable.Range(0, 64).Select(index =>
            Row(index, "Line " + index, "bank", "ENG", "PCM", index, 2, "KEY-" + index, "line " + index)));
        var query = new AssetQuery(Family: "strings", Sort: AssetSort.Name, Limit: 20);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => store.QueryIdentityKeys(query, cancellation.Token));
        Assert.Equal(store.Query(query).Select(descriptor => descriptor.Identity.Key), store.QueryIdentityKeys(query));
    }

    private static AssetDescriptor Row(long ordinal, string label, string bank, string language, string codec,
        double? duration, int channels, string lookupKey, string searchTerms,
        AssetAvailability availability = AssetAvailability.Available)
    {
        var identity = new ResourceIdentity("projection-content", ordinal, "row/" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new AssetDescriptor(identity, new SourceRevision("projection-content", "projection-revision"), "strings", label, bank,
            availability, AssetCompleteness.Complete, AssetCapabilities.None, [new NameEvidence(label, NameOrigin.Stored, "test")], [],
            language, codec, duration, channels)
        {
            LookupKey = lookupKey,
            SearchTerms = searchTerms
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
