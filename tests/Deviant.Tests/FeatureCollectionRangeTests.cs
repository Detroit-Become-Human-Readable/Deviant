using System.Reflection;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Deviant.Tests;

public sealed class FeatureCollectionRangeTests(ITestOutputHelper output) : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-feature-collections-" + Guid.NewGuid().ToString("N"));
    private string CacheRoot => Path.Combine(root, "cache");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollectionReadAndReplacementWorkDoNotGrowWithUnrelatedRevisionRows(bool replace)
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        const int unrelatedRows = 20_000;
        store.UpsertItems("unrelated", "v1", Enumerable.Range(0, unrelatedRows)
            .Select(index => new KeyValuePair<string, Item>(index.ToString("D12"), new(index, "Unrelated"))));
        store.SaveItems("selected", "v1", new[] { new Item(7, "First"), new Item(8, "Second"), new Item(9, "Third") });
        long instructions = 0;
        var connection = Connection(store);
        SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1, _ => { instructions++; return 0; }, null);
        try
        {
            if (replace) store.SaveItems("selected", "v2", new[] { new Item(17, "Replacement") });
            else Assert.Equal(new[] { 7, 8, 9 }, store.LoadItems<Item>("selected", "v1").Select(item => item.Id));
        }
        finally { SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 0, null, null); }
        output.WriteLine("Operation={0}; unrelated_rows={1}; sqlite_vm_steps={2}", replace ? "SaveItems" : "LoadItems", unrelatedRows, instructions);
        Assert.InRange(instructions, 1, 1_000);
        Assert.Equal(unrelatedRows, store.LoadItems<Item>("unrelated", "v1").Count);
        if (replace)
        {
            Assert.Empty(store.LoadItems<Item>("selected", "v1"));
            Assert.Equal(17, Assert.Single(store.LoadItems<Item>("selected", "v2")).Id);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("audio.media")]
    [InlineData("literal%_collection")]
    [InlineData("音声/資源_%_😀")]
    public void PrefixIsLiteralAndReplacementPreservesNeighborsVersionsAndOrdinalOrder(string collection)
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        string lookalike = collection.Contains('%') || collection.Contains('_')
            ? collection.Replace("%", "expanded", StringComparison.Ordinal).Replace("_", "Q", StringComparison.Ordinal)
            : collection + "neighbor";
        var expected = Enumerable.Range(0, 18).Select(index => new Item(index, "Generated " + index)).ToArray();
        store.Save(collection, "head", new Item(91, "Collection head"));
        store.Save(collection + "/item", "head", new Item(92, "Before range"));
        store.Save(collection + "/item0", "head", new Item(93, "After range"));
        store.SaveItems(lookalike, "v1", new[] { new Item(94, "Lookalike") });
        store.SaveItems(collection, "v1", expected);
        Assert.Equal(expected, store.LoadItems<Item>(collection, "v1"));
        store.UpsertItems(collection, "v2", new[]
        {
            new KeyValuePair<string, Item>("000000000010", new(110, "Different version"))
        });
        Assert.Equal(expected.Where(item => item.Id != 10), store.LoadItems<Item>(collection, "v1"));
        Assert.Equal(110, Assert.Single(store.LoadItems<Item>(collection, "v2")).Id);
        store.SaveItems(collection, "v3", new[] { new Item(77, "New collection") });
        Assert.Empty(store.LoadItems<Item>(collection, "v1"));
        Assert.Empty(store.LoadItems<Item>(collection, "v2"));
        Assert.Equal(77, Assert.Single(store.LoadItems<Item>(collection, "v3")).Id);
        Assert.Equal(91, store.Load<Item>(collection, "head")!.Id);
        Assert.Equal(92, store.Load<Item>(collection + "/item", "head")!.Id);
        Assert.Equal(93, store.Load<Item>(collection + "/item0", "head")!.Id);
        Assert.Equal(94, Assert.Single(store.LoadItems<Item>(lookalike, "v1")).Id);
    }

    [Fact]
    public void CollectionRangeKeepsAnotherPhysicalRevisionSeparate()
    {
        using var first = FeatureStore.Open(Snapshot(), CacheRoot);
        using var second = FeatureStore.Open(Snapshot("second-stamp"), CacheRoot);
        first.SaveItems("shared", "v1", new[] { new Item(1, "First revision") });
        second.SaveItems("shared", "v1", new[] { new Item(2, "Second revision") });
        first.SaveItems("shared", "v2", new[] { new Item(3, "Updated first revision") });
        Assert.Equal(3, Assert.Single(first.LoadItems<Item>("shared", "v2")).Id);
        Assert.Equal(2, Assert.Single(second.LoadItems<Item>("shared", "v1")).Id);
        Assert.Empty(second.LoadItems<Item>("shared", "v2"));
    }

    [Fact]
    public void CancelledReplacementRollsBackItsDeleteAndAlreadyInsertedRows()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        const string collection = "literal%_音声";
        var original = new[] { new Item(1, "Original one"), new Item(2, "Original two") };
        store.SaveItems(collection, "v1", original);
        store.SaveItems(collection + "neighbor", "v1", new[] { new Item(3, "Neighbor") });
        using var cancellation = new CancellationTokenSource();
        IEnumerable<Item> Replacement()
        {
            yield return new(4, "Inserted before cancellation");
            cancellation.Cancel();
            yield return new(5, "Cancelled row");
        }
        Assert.ThrowsAny<OperationCanceledException>(() => store.SaveItems(collection, "v2", Replacement(), cancellation.Token));
        Assert.Equal(original, store.LoadItems<Item>(collection, "v1"));
        Assert.Empty(store.LoadItems<Item>(collection, "v2"));
        Assert.Equal(3, Assert.Single(store.LoadItems<Item>(collection + "neighbor", "v1")).Id);
    }

    [Fact]
    public void CancellationInterruptsARealCollectionReadAndLeavesTheConnectionUsable()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var expected = Enumerable.Range(0, 8).Select(index => new Item(index, "Generated")).ToArray();
        store.SaveItems("selected", "v1", expected);
        using var cancellation = new CancellationTokenSource();
        var connection = Connection(store);
        bool progressReached = false;
        SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1, _ =>
        {
            progressReached = true;
            cancellation.Cancel();
            return 0;
        }, null);
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => store.LoadItems<Item>("selected", "v1", cancellation.Token));
        }
        finally { SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 0, null, null); }
        Assert.True(progressReached);
        Assert.Equal(expected, store.LoadItems<Item>("selected", "v1"));
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.LoadItems<Item>("selected", "v1", alreadyCancelled.Token));
    }

    private static SqliteConnection Connection(FeatureStore store) => (SqliteConnection)typeof(FeatureStore)
        .GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

    private ArchiveSnapshot Snapshot(string stamp = "collection-stamp")
    {
        string source = Path.Combine(root, "source");
        return new(Path.Combine(source, "generated.idx"), "generated-collection-index", IndexLayout.SupportedVersion,
            0, [0x41], 0, [], [], new FileStamp(true, "index", 1, 1, 1, true), stamp, []);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed record Item(int Id, string Label);
}
