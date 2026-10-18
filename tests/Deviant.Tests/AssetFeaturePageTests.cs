using System.Diagnostics;
using System.Reflection;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Tests;

public sealed class AssetFeaturePageTests : IDisposable
{
    private const string Family = "generated-media";
    private const string Prefix = "generated.recovery/";
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-feature-page-" + Guid.NewGuid().ToString("N"));
    private readonly SourceRevision revision = new("generated-page-index", "generated-page-stamp");
    private FeatureStore Open() => FeatureStore.Open(new ArchiveSnapshot(Path.Combine(root, "source", "generated.idx"),
        revision.IndexContentId, IndexLayout.SupportedVersion, 0, [0x41], 0, [], [],
        new FileStamp(true, "index", 1, 1, 1, true), revision.PhysicalSourceStamp, []), Path.Combine(root, "cache"));
    private AssetDescriptor Asset(int id, string? label = null) => new(new(revision.IndexContentId, id), revision,
        Family, label ?? "Generated " + id, null, AssetAvailability.Available, AssetCompleteness.Complete,
        AssetCapabilities.None, [], [], SortKey: id.ToString("D12"));
    private BoundedFeatureAssetWrite Pair(int id, string label) => new(
        BoundedFeatureWrite.Create(Prefix + Asset(id).Identity.Key, "v1", new Payload(label), 8192), Asset(id, label));
    private static BoundedAssetFeaturePage<Payload> Page(FeatureStore store, AssetQuery? query = null, int maximum = 8192,
        CancellationToken token = default) => store.ReadBoundedAssetFeaturePage<Payload>(
            query ?? new(Family: Family), Prefix, "v1", maximum, token);

    [Fact]
    public void IncompleteFamilyPagesRetainNamesCountsOrderAndMissingEvidence()
    {
        using var store = Open();
        store.UpsertAssets(Enumerable.Range(0, 140).Select(id => Asset(id)));
        store.PublishBoundedAssetsBatch([Pair(100, "Recovered")]);
        var page = Page(store, new(Family: Family, Offset: 100, Limit: 28));
        Assert.Equal(140, page.Total);
        Assert.Equal(Enumerable.Range(100, 28).Select(id => (long)id), page.Rows.Select(row => row.Asset.Identity.Ordinal));
        Assert.Equal("Recovered", page.Rows[0].Feature!.Label);
        Assert.Null(page.Rows[0].ErrorCode);
        Assert.All(page.Rows.Skip(1), row => { Assert.Null(row.Feature); Assert.Equal("BoundedFeatureMissing", row.ErrorCode); });
        Assert.Equal(1, Page(store, new(Family: Family, Search: "Recovered")).Total);
    }

    [Theory]
    [InlineData("version", "BoundedFeatureVersion")]
    [InlineData("oversized", "BoundedFeatureLimit")]
    [InlineData("corrupt", "BoundedFeaturePayload")]
    public void IndividualEvidenceFailureDoesNotHideOtherNames(string damage, string expected)
    {
        using var store = Open();
        store.PublishBoundedAssetsBatch([Pair(0, "Kept"), Pair(1, "Damaged")]);
        if (damage == "version") store.Save(Prefix + Asset(1).Identity.Key, "v2", new Payload("Changed"));
        else if (damage == "oversized") store.Save(Prefix + Asset(1).Identity.Key, "v1", new Payload(new string('x', 1024)));
        else
        {
            var connection = (SqliteConnection)typeof(FeatureStore).GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE asset_features SET payload='{' WHERE feature=$feature";
            command.Parameters.AddWithValue("$feature", Prefix + Asset(1).Identity.Key); command.ExecuteNonQuery();
        }
        var page = Page(store, maximum: 512);
        Assert.Equal(2, page.Total); Assert.Equal("Kept", page.Rows[0].Feature!.Label);
        Assert.Equal("Damaged", page.Rows[1].Asset.Label);
        Assert.Null(page.Rows[1].Feature); Assert.Equal(expected, page.Rows[1].ErrorCode);
        Assert.InRange(page.Rows[1].SerializedBytes, 1, 1024);
    }

    [Fact]
    public async Task ReaderPinsMatchingPairAndCountWhileAnotherConnectionPublishes()
    {
        using var store = Open(); store.PublishBoundedAssetsBatch([Pair(0, "Before")]);
        using var other = Open();
        using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        using var observer = AssetLoadDiagnostics.Listener.Subscribe(new Observe(counter =>
        {
            if (counter.Stage != "BoundedFeatures.ReadSnapshot" || counter.Key != Prefix) return;
            entered.Set(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
        }));
        var reading = Task.Run(() => Page(store));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            other.PublishBoundedAssetsBatch([Pair(0, "After"), Pair(1, "Added")]);
        }
        finally { resume.Set(); }
        var page = await reading.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, page.Total); var row = Assert.Single(page.Rows);
        Assert.Equal("Before", row.Asset.Label); Assert.Equal(row.Asset.Label, row.Feature!.Label);
        Assert.Equal(2, Page(store).Total);
    }

    [Fact]
    public void MalformedEvidenceStillConsumesTheAggregateHydrationBudget()
    {
        using var store = Open();
        store.PublishBoundedAssetsBatch(Enumerable.Range(0, 9).Select(id => Pair(id, "Generated " + id)).ToArray());
        var connection = (SqliteConnection)typeof(FeatureStore).GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE asset_features SET payload=$payload WHERE feature LIKE $prefix";
            command.Parameters.AddWithValue("$prefix", Prefix + "%");
            command.Parameters.AddWithValue("$payload", "{" + new string('x', 4 * 1024 * 1024 - 1));
            Assert.Equal(9, command.ExecuteNonQuery());
        }
        var error = Assert.Throws<InvalidDataException>(() => Page(store, maximum: 5 * 1024 * 1024));
        Assert.Equal("BoundedAssetPageLimit", error.Message);
    }

    [Fact]
    public void BoundsAndCancellationAreEnforcedBeforePayloadHydration()
    {
        using var store = Open(); store.PublishBoundedAssetsBatch([Pair(0, "Kept")]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Page(store, new(Family: Family, Limit: 129)));
        Assert.ThrowsAny<ArgumentException>(() => Page(store, new()));
        Assert.Throws<ArgumentOutOfRangeException>(() => Page(store, maximum: 0));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Page(store, token: cancellation.Token));
    }

    public sealed record Payload(string Label);
    private sealed class Observe(Action<AssetLoadCounterMeasurement> action) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value) { if (value.Value is AssetLoadCounterMeasurement counter) action(counter); }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
