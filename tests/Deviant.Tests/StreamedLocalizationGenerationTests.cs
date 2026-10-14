using System.Text.Json;
using System.Reflection;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Tests;

public sealed class StreamedLocalizationGenerationTests : IDisposable
{
    private const string RecordVersion = "generated-record-v1";
    private const string SearchVersion = "generated-search-v1";
    private const string JsonVersion = "generated-json-v1";
    private readonly string root = Path.Combine(Path.GetTempPath(), "DeviantStreamedLocalizationTests", Guid.NewGuid().ToString("N"));
    private string Cache => Path.Combine(root, "cache");
    private ArchiveSnapshot Snapshot => new(Path.Combine(root, "source", "archive.idx"), "generated-content", 1, 0,
        [], 0, [], [], new(false, "", 0, 0, 0, false), "generated-physical", []);

    private sealed record Row(string Value);
    private sealed record Head(string InputSnapshotId, string ParsedResultId, string RecordStoreVersion,
        string SearchProjectionVersion, string JsonPolicyVersion, long RecordCount);

    private static LocalizationGenerationIdentity Identity(Head head) => new(head.InputSnapshotId,
        head.ParsedResultId, head.RecordStoreVersion, head.SearchProjectionVersion, head.JsonPolicyVersion, head.RecordCount);

    private static LocalizationGenerationRow<Row> MakeRow(long ordinal, string value,string? groupKey=null,string? lookupKey=null)
    {
        var identity = new ResourceIdentity("generated-content", ordinal);
        var descriptor = new AssetDescriptor(identity, new SourceRevision("generated-content", "generated-physical"),
            "localization.record", value, groupKey, AssetAvailability.Available, AssetCompleteness.Complete,
            AssetCapabilities.None, [], [], SortKey: value)
        { SearchTerms = value, Codec = "Pointer", LookupKey = lookupKey ?? value };
        return new(identity.Key, new(value), descriptor);
    }

    private static IReadOnlyList<string> ExplainGeneratedQuery(FeatureStore store,string generation,AssetQuery query,bool page)
    {
        var connection=(SqliteConnection)typeof(FeatureStore).GetField("connection",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(store)!;
        using var tx=connection.BeginTransaction(deferred:true);
        var method=typeof(FeatureStore).GetMethod(page?"SearchPageCommand":"SearchCommand",BindingFlags.Instance|BindingFlags.NonPublic)!;
        using var command=(SqliteCommand)(page
            ?method.Invoke(store,[tx,generation,query,10,0L])!
            :method.Invoke(store,[tx,generation,query,"COUNT(*)",false])!);
        command.CommandText="EXPLAIN QUERY PLAN "+command.CommandText;
        var details=new List<string>();
        using(var reader=command.ExecuteReader())while(reader.Read())details.Add(reader.GetString(3));
        tx.Commit();return details;
    }

    private static Head MakeHead(string suffix, int count, string searchVersion = SearchVersion) =>
        new("input-" + suffix, "result-" + suffix, RecordVersion, searchVersion, JsonVersion, count);

    private static SqliteConnection OpenMutationConnection(string database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        connection.CreateFunction<string, string>("deviant_natural", static text => NaturalOrder.Key(text), isDeterministic: true);
        return connection;
    }

    private PublishedLocalizationGeneration<Head> Publish(FeatureStore store, string suffix,
        IReadOnlyList<LocalizationGenerationRow<Row>> rows, string searchVersion = SearchVersion)
    {
        var writer = store.BeginLocalizationGeneration(RecordVersion, searchVersion);
        foreach (var batch in rows.Chunk(FeatureStore.LocalizationGenerationBatchLimit))
            store.AppendLocalizationGeneration(writer, batch);
        return store.PublishLocalizationGeneration(writer, MakeHead(suffix, rows.Count, searchVersion), Identity);
    }

    [Fact]
    public void SealedGenerationRemainsReadableWhilePublicationValidatesIt()
    {
        using var store = FeatureStore.Open(Snapshot, Cache);
        using var writer = store.BeginLocalizationGeneration(RecordVersion, SearchVersion);
        var row = MakeRow(1, "generated record");
        store.AppendLocalizationGeneration(writer, [row]);
        typeof(FeatureStore).GetMethod("SealLocalizationGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [writer, Identity(MakeHead("sealed", 1)), CancellationToken.None]);

        Assert.Equal(row.Record, store.LoadStagedLocalizationRecord<Row>(writer, row.IdentityKey));
        Assert.Equal(row.Record, Assert.Single(store.QueryStagedLocalizationPage<Row>(writer,
            new AssetQuery(Family: "localization.record")).Records));
        Assert.Throws<InvalidOperationException>(() => store.AppendLocalizationGeneration(writer, [MakeRow(2, "another record")]));
    }

    [Fact]
    public void StagedQueriesArePrivateAndPublishedQueriesUseIndexedSearchAndBoundedPages()
    {
        using var store = FeatureStore.Open(Snapshot, Cache);
        using var writer = store.BeginLocalizationGeneration(RecordVersion, SearchVersion);
        store.AppendLocalizationGeneration(writer, [MakeRow(1, "generated saffron one"), MakeRow(2, "generated saffron two")]);

        var staged = store.QueryStagedLocalizationPage<Row>(writer,
            new AssetQuery(Search: "saffron", Family: "localization.record", Limit: 1));
        Assert.Equal(2, staged.Total);
        Assert.Single(staged.Records);
        Assert.Null(store.OpenLocalizationGeneration<Head>(RecordVersion, SearchVersion, Identity, _ => true));

        using var published = store.PublishLocalizationGeneration(writer, MakeHead("one", 2), Identity);
        var page = store.QueryLocalizationPage<Head, Row>(published,
            new AssetQuery(Search: "saffron", Family: "localization.record", Limit: 2));
        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Records.Count);
        var first = store.LoadLocalizationRecordPage<Head, Row>(published, -1, 1);
        Assert.Single(first.Items);
        Assert.True(first.HasMore);
        Assert.Equal(first.Items[0].Sequence, first.NextSequence);
        var second = store.LoadLocalizationRecordPage<Head, Row>(published, first.NextSequence, 1);
        Assert.Single(second.Items);
        Assert.False(second.HasMore);
    }

    [Fact]
    public void OwnerAndExactKeyQueriesUseTheirIndexesForCountAndPageWithoutCrossOwnerResults()
    {
        using var store=FeatureStore.Open(Snapshot,Cache);
        using var published=Publish(store,"query-indexes",[
            MakeRow(1,"owner A duplicate","owner-a","shared-key"),
            MakeRow(2,"owner B duplicate","owner-b","shared-key"),
            MakeRow(3,"owner A other","owner-a","other-key"),
            MakeRow(4,"empty owner","","empty-owner-key"),
            MakeRow(5,"empty exact key","owner-a","")
        ]);

        var exact=new AssetQuery(GroupKey:"owner-a",Codec:"Pointer",Family:"localization.record",ExactKey:"shared-key",Limit:10);
        var group=new AssetQuery(GroupKey:"owner-a",Codec:"Pointer",Family:"localization.record",Limit:10);
        var emptyExact=new AssetQuery(GroupKey:"owner-a",Codec:"Pointer",Family:"localization.record",ExactKey:"",Limit:10);
        var emptyGroup=new AssetQuery(GroupKey:"",Codec:"Pointer",Family:"localization.record",Limit:10);
        var planner=new AssetQuery(Codec:"Pointer",Family:"localization.record",Limit:10);

        foreach(var query in new[]{exact,group,emptyExact,emptyGroup})
        {
            var expectedIndex=query.ExactKey is not null?"loc_gen_lookup":"loc_gen_group";
            foreach(var isPage in new[]{false,true})
            {
                var plan=ExplainGeneratedQuery(store,published.Generation,query,isPage);
                Assert.True(plan.Any(detail=>detail.Contains("USING INDEX "+expectedIndex,StringComparison.Ordinal)),
                    $"Expected {expectedIndex} for {(isPage?"page":"count")} query; plan: {string.Join(" | ",plan)}");
            }
        }

        var exactPage=store.QueryLocalizationPage<Head,Row>(published,exact);
        Assert.Equal(1,exactPage.Total);
        Assert.Equal(new[]{"owner A duplicate"},exactPage.Records.Select(row=>row.Value));
        var groupPage=store.QueryLocalizationPage<Head,Row>(published,group);
        Assert.Equal(3,groupPage.Total);
        Assert.Equal(new[]{"empty exact key","owner A duplicate","owner A other"},groupPage.Records.Select(row=>row.Value));
        var middleGroupPage=store.QueryLocalizationPage<Head,Row>(published,group with{Offset=1,Limit=1});
        Assert.Equal(3,middleGroupPage.Total);
        Assert.Equal("owner A duplicate",Assert.Single(middleGroupPage.Records).Value);
        var emptyExactPage=store.QueryLocalizationPage<Head,Row>(published,emptyExact);
        Assert.Equal(1,emptyExactPage.Total);
        Assert.Equal(new[]{"empty exact key"},emptyExactPage.Records.Select(row=>row.Value));
        var emptyGroupPage=store.QueryLocalizationPage<Head,Row>(published,emptyGroup);
        Assert.Equal(1,emptyGroupPage.Total);
        Assert.Equal(new[]{"empty owner"},emptyGroupPage.Records.Select(row=>row.Value));
        var unfilteredByOwner=store.QueryLocalizationPage<Head,Row>(published,planner);
        Assert.Equal(5,unfilteredByOwner.Total);
        Assert.Equal(5,unfilteredByOwner.Records.Count);
    }

    [Theory]
    [InlineData("delete-record")]
    [InlineData("change-search-key")]
    public void CorruptStagedRowsCannotReplaceThePreviousPublishedGeneration(string corruption)
    {
        using var store = FeatureStore.Open(Snapshot, Cache);
        using var original = Publish(store, "original", [MakeRow(1, "old complete row")]);
        var writer = store.BeginLocalizationGeneration(RecordVersion, SearchVersion);
        store.AppendLocalizationGeneration(writer, [MakeRow(2, "new candidate row")]);
        var database = Directory.GetFiles(Cache, "*.sqlite3").Single();
        using (var connection = OpenMutationConnection(database))
        {
            using var command = connection.CreateCommand();
            command.CommandText = corruption == "delete-record"
                ? "DELETE FROM localization_generation_rows WHERE generation=$g AND collection='records'"
                : "UPDATE localization_generation_rows SET item_key='generated-foreign-key' WHERE generation=$g AND collection='search-assets'";
            command.Parameters.AddWithValue("$g", writer.Generation);
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => store.PublishLocalizationGeneration(writer,
            MakeHead("new", 1), Identity));
        writer.Dispose();
        using var current = store.OpenLocalizationGeneration<Head>(RecordVersion, SearchVersion, Identity, _ => true);
        Assert.NotNull(current);
        Assert.Equal("old complete row", Assert.Single(store.LoadLocalizationRecordPage<Head, Row>(current!, -1, 1).Items).Record.Value);
    }

    [Fact]
    public void SearchProjectionMismatchKeepsRecordsReadableAndBlocksOldSearchRows()
    {
        using var store = FeatureStore.Open(Snapshot, Cache);
        using var published = Publish(store, "one", [MakeRow(1, "generated search row")]);
        using var reopened = store.OpenLocalizationGeneration<Head>(RecordVersion, "generated-search-v2", Identity, _ => true);
        Assert.NotNull(reopened);
        Assert.False(reopened!.SearchProjectionMatches);
        Assert.Equal("generated search row", Assert.Single(store.LoadLocalizationRecordPage<Head, Row>(reopened, -1, 1).Items).Record.Value);
        Assert.Throws<InvalidOperationException>(() => store.QueryLocalizationPage<Head, Row>(reopened,
            new AssetQuery(Search: "search", Family: "localization.record")));
    }

    [Fact]
    public void CorruptPublishedRowsAreAWholeGenerationMiss()
    {
        using var store = FeatureStore.Open(Snapshot, Cache);
        using var published = Publish(store, "corrupt", [MakeRow(1, "generated record")]);
        var database = Directory.GetFiles(Cache, "*.sqlite3").Single();
        using (var connection = OpenMutationConnection(database))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE localization_generation_rows SET payload='not-json' WHERE generation=$g AND collection='records'";
            command.Parameters.AddWithValue("$g", published.Generation); command.ExecuteNonQuery();
        }
        Assert.Null(store.OpenLocalizationGeneration<Head>(RecordVersion, SearchVersion, Identity, _ => true));
    }

    [Fact]
    public void PinnedGenerationSurvivesPublicationAndFailsClosedIfItsHeadDisappears()
    {
        using var store = FeatureStore.Open(Snapshot, Cache);
        using var first = Publish(store, "first", [MakeRow(1, "first generation")]);
        var pinned = store.OpenLocalizationGeneration<Head>(RecordVersion, SearchVersion, Identity, _ => true)!;
        using var second = Publish(store, "second", [MakeRow(2, "second generation")]);
        Assert.Equal("first generation", Assert.Single(store.LoadLocalizationRecordPage<Head, Row>(pinned, -1, 1).Items).Record.Value);

        var database = Directory.GetFiles(Cache, "*.sqlite3").Single();
        using (var connection = OpenMutationConnection(database))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM localization_generations WHERE generation=$g";
            command.Parameters.AddWithValue("$g", pinned.Generation); command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => store.LoadLocalizationRecordPage<Head, Row>(pinned, -1, 1));
        pinned.Dispose();
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
