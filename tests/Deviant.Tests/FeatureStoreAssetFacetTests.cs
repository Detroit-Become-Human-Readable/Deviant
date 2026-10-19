using System.Reflection;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Tests;

[Collection("Asset facet diagnostics")]
public sealed class FeatureStoreAssetFacetTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Deviant-facet-tests", Guid.NewGuid().ToString("N"));
    private const string Family = "audio.media";
    private SourceRevision Revision => new("generated-index", "generated-source");
    private ArchiveSnapshot Snapshot => new(Path.Combine(root, "source", "generated.idx"), Revision.IndexContentId,
        18, 0, [], 0, [], [], new(true, "generated", 1, 1, 1, true), Revision.PhysicalSourceStamp, []);
    private FeatureStore Open() => FeatureStore.Open(Snapshot, Path.Combine(root, "cache"));
    private AssetDescriptor Asset(int index) => new(new(Revision.IndexContentId, index), Revision, Family,
        $"Generated clip {index}", "group-" + index % 2, AssetAvailability.Available, AssetCompleteness.Partial,
        AssetCapabilities.None, [], [], "L" + index.ToString("D3"), "C" + index % 3, index, index % 2 + 1)
        { LookupKey = "lookup-" + index % 2 };

    [Fact]
    public void FacetsPageScalarValuesWithExactCountsAndStableContinuation()
    {
        using var store = Open(); store.UpsertAssets(Enumerable.Range(0, 260).Select(Asset));
        store.PrepareAssetFacets(Revision, Family);
        var first = Assert.IsType<AssetFacetPage>(store.ReadAssetFacetPage(Revision, new(Family: Family), AssetFacet.Language));
        Assert.Equal(260, first.MatchedCount); Assert.Equal(260, first.DistinctCount); Assert.Equal(128, first.Values.Count);
        Assert.Equal("L000", first.Values[0].Value); Assert.Equal("L127", first.Continuation);
        var second = Assert.IsType<AssetFacetPage>(store.ReadAssetFacetPage(Revision, new(Family: Family), AssetFacet.Language,
            first.Continuation, expectedFamilyEpoch: first.FamilyEpoch));
        var third = Assert.IsType<AssetFacetPage>(store.ReadAssetFacetPage(Revision, new(Family: Family), AssetFacet.Language,
            second.Continuation, expectedFamilyEpoch: first.FamilyEpoch));
        Assert.Equal(128, second.Values.Count); Assert.Equal(4, third.Values.Count); Assert.Null(third.Continuation);
        Assert.Equal(260, first.Values.Concat(second.Values).Concat(third.Values).Select(item => item.Value).Distinct().Count());
        Assert.All(first.Values.Concat(second.Values).Concat(third.Values), value => Assert.Equal(1, value.Count));
        var channels = store.ReadAssetFacetPage(Revision, new(Family: Family), AssetFacet.Channels)!;
        Assert.Equal(new[] { new AssetFacetValue("1", 130), new AssetFacetValue("2", 130) }, channels.Values);
        var groups = store.ReadAssetFacetPage(Revision, new(Family: Family), AssetFacet.Group)!;
        Assert.Equal(new[] { new AssetFacetValue("group-0", 130), new AssetFacetValue("group-1", 130) }, groups.Values);
    }

    [Fact]
    public void FiltersUseIndexedScalarsIncludingExactIdentityWithoutReadingEvidence()
    {
        using var store = Open(); store.UpsertAssets(Enumerable.Range(0, 30).Select(Asset)); store.PrepareAssetFacets(Revision, Family);
        using (var command = Connection(store).CreateCommand())
        {
            command.CommandText = "UPDATE assets_v2 SET payload='{'"; command.ExecuteNonQuery();
        }
        var filtered = store.ReadAssetFacetPage(Revision, new(GroupKey: "group-1", Codec: "C1", Channels: 2,
            Family: Family, ExactKey: "lookup-1", MinimumDuration: 10, MaximumDuration: 20), AssetFacet.Language)!;
        Assert.Equal(new[] { "L013", "L019" }, filtered.Values.Select(value => value.Value));
        Assert.Equal(2, filtered.MatchedCount); Assert.Equal(2, filtered.DistinctCount);
        var exact = store.ReadAssetFacetPage(Revision, new(Family: Family) { ExactIdentityKey = Asset(17).Identity.Key }, AssetFacet.Codec)!;
        Assert.Equal(new AssetFacetValue("C2", 1), Assert.Single(exact.Values));
    }

    [Fact]
    public void UnpreparedAndChangedFamilyEpochNeverAuthorizeMixedPages()
    {
        using var store = Open(); store.UpsertAssets([Asset(0), Asset(1)]);
        Assert.Null(store.ReadAssetFacetEpoch(Revision, Family));
        Assert.Null(store.ReadAssetFacetPage(Revision, new(Family: Family), AssetFacet.Language));
        store.PrepareAssetFacets(Revision, Family);
        var first = store.ReadAssetFacetPage(Revision, new(Family: Family), AssetFacet.Language, limit: 1)!;
        store.UpsertAssets([Asset(2)]);
        Assert.Null(store.ReadAssetFacetPage(Revision, new(Family: Family), AssetFacet.Language,
            first.Continuation, expectedFamilyEpoch: first.FamilyEpoch));
        Assert.True(store.ReadAssetFacetEpoch(Revision, Family) > first.FamilyEpoch);
        Assert.Equal(3, store.ReadAssetFacetPage(Revision, new(Family: Family), AssetFacet.Language)!.MatchedCount);
    }

    [Fact]
    public async Task PreparationIsIdempotentAndReadersDoNotNeedTheWriterGate()
    {
        using var store = Open(); store.UpsertAssets([Asset(0), Asset(1)]); store.PrepareAssetFacets(Revision, Family);
        var epoch = store.ReadAssetFacetEpoch(Revision, Family); store.PrepareAssetFacets(Revision, Family);
        Assert.Equal(epoch, store.ReadAssetFacetEpoch(Revision, Family));
        var gate = typeof(FeatureStore).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = Task.Run(() =>
        {
            lock (gate) { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var page = await Task.Run(() => store.ReadAssetFacetPage(Revision, new(Family: Family), AssetFacet.Codec))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, page!.MatchedCount);
        }
        finally { release.Set(); await holding.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public void CancellationRevisionAndPageLimitsAreExplicit()
    {
        using var store = Open(); store.PrepareAssetFacets(Revision, Family);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.ReadAssetFacetPage(Revision, new(Family: Family), AssetFacet.Codec, token: canceled.Token));
        var error = Assert.Throws<ArchiveException>(() => store.ReadAssetFacetEpoch(new(Revision.IndexContentId, "different"), Family));
        Assert.Equal("SourceChanged", error.Code);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadAssetFacetPage(Revision, new(Family: Family), AssetFacet.Codec, limit: 129));
        Assert.Throws<ArgumentException>(() => store.ReadAssetFacetPage(Revision, new(Family: Family), AssetFacet.Codec, "C1"));
        Assert.Throws<ArgumentException>(() => store.ReadAssetFacetPage(Revision, new(Family: Family) { ModelKind = "character" }, AssetFacet.Codec));
    }

    [Fact]
    public async Task APagePinsItsCountsAndEpochWhileAnotherConnectionPublishes()
    {
        using var store = Open(); store.UpsertAssets([Asset(0), Asset(1)]); store.PrepareAssetFacets(Revision, Family);
        using var barrier = new ReadBarrier();
        var reading = Task.Run(() => store.ReadAssetFacetPage(Revision, new(Family: Family), AssetFacet.Language, limit: 1));
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var writer = Open(); writer.UpsertAssets([Asset(2)]);
        }
        finally { barrier.Release.Set(); }
        var oldPage = (await reading.WaitAsync(TimeSpan.FromSeconds(5)))!;
        Assert.Equal(2, oldPage.MatchedCount); Assert.Equal(2, oldPage.DistinctCount);
        Assert.Null(store.ReadAssetFacetPage(Revision, new(Family: Family), AssetFacet.Language,
            oldPage.Continuation, expectedFamilyEpoch: oldPage.FamilyEpoch));
        Assert.Equal(3, store.ReadAssetFacetPage(Revision, new(Family: Family), AssetFacet.Language)!.MatchedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndDisposalDrainTheIndependentReader(bool disposeStore)
    {
        using var store = Open(); store.UpsertAssets([Asset(0)]); store.PrepareAssetFacets(Revision, Family);
        using var barrier = new ReadBarrier(); using var canceled = new CancellationTokenSource();
        var reading = Task.Run(() => store.ReadAssetFacetPage(Revision, new(Family: Family), AssetFacet.Codec, token: canceled.Token));
        Task? disposal = null;
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (disposeStore)
            {
                var lifetime = (CancellationTokenSource)typeof(FeatureStore).GetField("linkedReadLifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
                var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = lifetime.Token.Register(() => cancellationObserved.TrySetResult());
                disposal = Task.Run(store.Dispose);
                await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(disposal.IsCompleted);
            }
            else canceled.Cancel();
        }
        finally { barrier.Release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
        if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        else Assert.Equal(1, store.ReadAssetFacetPage(Revision, new(Family: Family), AssetFacet.Codec)!.MatchedCount);
    }

    private sealed class ReadBarrier : IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly IDisposable subscription;
        private int captured;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        internal ReadBarrier() => subscription = AssetLoadDiagnostics.Listener.Subscribe(this);
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is not AssetLoadCounterMeasurement { Stage: "AssetFacets.ReadSnapshot", Key: Family } ||
                Interlocked.Exchange(ref captured, 1) != 0) return;
            Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Facet read barrier expired.");
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose() { Release.Set(); subscription.Dispose(); Release.Dispose(); }
    }

    private static SqliteConnection Connection(FeatureStore store) => (SqliteConnection)typeof(FeatureStore)
        .GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

[CollectionDefinition("Asset facet diagnostics", DisableParallelization = true)]
public sealed class AssetFacetDiagnosticsCollection;
