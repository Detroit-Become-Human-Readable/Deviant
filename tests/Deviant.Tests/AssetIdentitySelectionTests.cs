using System.Reflection;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Tests;

[Collection("Asset selection diagnostics")]
public sealed class AssetIdentitySelectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Deviant-selection-tests", Guid.NewGuid().ToString("N"));
    private SourceRevision Revision => new("generated-selection", "generated-source");
    private ArchiveSnapshot Snapshot => new(Path.Combine(root, "source", "generated.idx"), Revision.IndexContentId,
        18, 0, [], 0, [], [], new(true, "generated", 1, 1, 1, true), Revision.PhysicalSourceStamp, []);
    private FeatureStore Open() => FeatureStore.Open(Snapshot, Path.Combine(root, "cache"));
    private AssetDescriptor Asset(int index) => new(new(Revision.IndexContentId, index), Revision, "audio.media",
        "Generated " + index, "bank-" + index % 2, AssetAvailability.Available, AssetCompleteness.Complete,
        AssetCapabilities.None, [], [], index % 2 == 0 ? "ENG" : "FRA", "PCM", index, index % 2 + 1)
        { LookupKey = "lookup-" + index % 2 };

    [Fact]
    public void ScalarSelectionIgnoresDisplayPagingAndDoesNotReadPayloadJson()
    {
        using var store = Open(); store.UpsertAssets(Enumerable.Range(0, 260).Select(Asset));
        using (var damage = Connection(store).CreateCommand())
        { damage.CommandText = "UPDATE assets_v2 SET payload='{'"; damage.ExecuteNonQuery(); }

        var selected = store.ReadAssetIdentitySelection(Revision, new(Search: "Generated", GroupKey: "bank-0", Language: "ENG",
            Codec: "PCM", Channels: 1, MinimumDuration: 10, MaximumDuration: 18, Family: "audio.media", ExactKey: "lookup-0",
            Offset: 200, Limit: 1, Sort: AssetSort.Duration, Descending: true));

        Assert.Equal(Revision, selected.Revision); Assert.Null(selected.FamilyEpoch); Assert.Equal(5, selected.MatchedCount);
        Assert.Equal(new[] { 18, 16, 14, 12, 10 }.Select(index => Asset(index).Identity.Key), selected.IdentityKeys);
        Assert.InRange(selected.RetainedBytes, 1, FeatureStore.MaximumAssetSelectionKeyBytes);
    }

    [Fact]
    public void SelectionBoundsAreExplicitAndNeverReturnATruncatedResult()
    {
        using var store = Open(); store.UpsertAssets(Enumerable.Range(0, 4).Select(Asset));
        Assert.Equal("AssetSelectionLimit", Assert.Throws<ArchiveException>(() =>
            store.ReadAssetIdentitySelection(Revision, new(Family: "audio.media"), maximumIdentities: 3)).Code);
        Assert.Equal("AssetSelectionLimit", Assert.Throws<ArchiveException>(() =>
            store.ReadAssetIdentitySelection(Revision, new(Family: "audio.media"), maximumKeyBytes: 1)).Code);
        Assert.Throws<ArgumentException>(() => store.ReadAssetIdentitySelection(Revision, new(Family: "audio.media", Sort: AssetSort.Animations)));
        Assert.Equal("SourceChanged", Assert.Throws<ArchiveException>(() =>
            store.ReadAssetIdentitySelection(new(Revision.IndexContentId, "changed"), new(Family: "audio.media"))).Code);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.ReadAssetIdentitySelection(Revision, new(Family: "audio.media"), token: canceled.Token));
    }

    [Fact]
    public async Task CountEpochAndKeysComeFromOneSnapshotWhileAnotherStorePublishes()
    {
        using var store = Open(); store.UpsertAssets([Asset(0), Asset(1)]); store.PrepareAssetFacets(Revision, "audio.media");
        var epoch = store.ReadAssetFacetEpoch(Revision, "audio.media");
        using var barrier = new ReadBarrier();
        var pending = Task.Run(() => store.ReadAssetIdentitySelection(Revision, new(Family: "audio.media")));
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var writer = Open(); writer.UpsertAssets([Asset(2)]);
        }
        finally { barrier.Release.Set(); }
        var old = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(epoch, old.FamilyEpoch); Assert.Equal(2, old.MatchedCount);
        Assert.Equal(new[] { Asset(0).Identity.Key, Asset(1).Identity.Key }, old.IdentityKeys);
        var current = store.ReadAssetIdentitySelection(Revision, new(Family: "audio.media"));
        Assert.Equal(3, current.MatchedCount); Assert.True(current.FamilyEpoch > old.FamilyEpoch);
    }

    [Fact]
    public async Task SelectionReaderDoesNotAcquireTheWriterGate()
    {
        using var store = Open(); store.UpsertAssets([Asset(0)]);
        var gate = typeof(FeatureStore).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = Task.Run(() => { lock (gate) { entered.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await Task.Run(() => store.ReadAssetIdentitySelection(Revision, new(Family: "audio.media"))).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(result.IdentityKeys);
        }
        finally { release.Set(); await holding.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static SqliteConnection Connection(FeatureStore store) => (SqliteConnection)typeof(FeatureStore).GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
    private sealed class ReadBarrier : IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly IDisposable subscription;
        private int captured;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        internal ReadBarrier() => subscription = AssetLoadDiagnostics.Listener.Subscribe(this);
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is not AssetLoadCounterMeasurement { Stage: "AssetSelections.ReadSnapshot", Key: "audio.media" } || Interlocked.Exchange(ref captured, 1) != 0) return;
            Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Selection read barrier expired.");
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public void Dispose() { subscription.Dispose(); Release.Set(); Release.Dispose(); }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

[CollectionDefinition("Asset selection diagnostics", DisableParallelization = true)]
public sealed class AssetSelectionDiagnosticsCollection;
