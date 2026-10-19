using Deviant.Assets;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using System.Collections.Concurrent;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class AudioMatchingSelectionTests
{
    [Fact]
    public async Task LegacyMatchingSelectionHydratesOnlyMatchedRowsWithoutGlobalRestoration()
    {
        using var fixture = FormatsFixtureBuilder.CreateDialogueMediaCatalog(6);
        using var workspace = new Workspace();
        await using var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases,
            new AudioToolPaths(null, null, null, null, null, null, null), null, CancellationToken.None, startEnrichment: false);
        var snapshot = session.Snapshot;
        var identities = snapshot.Records.Select(row => new ResourceIdentity(snapshot.IndexContentId, row.Ordinal, "wem")).ToArray();
        using (var features = session.AcquireSharedFeatureStore())
            features.Store.UpsertAssets(identities.Select((identity, index) => new AssetDescriptor(identity,
                new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), AudioExplorerSession.AssetFamily,
                (index % 2 == 0 ? "Wanted " : "Other ") + index, "__deviant_standalone__", AssetAvailability.Available,
                AssetCompleteness.Complete, AssetCapabilities.None, [], [], "ENG", "PCM", index, 2)
                { Metadata = new Dictionary<string, string> { ["audio.source-label"] = (index % 2 == 0 ? "Wanted " : "Other ") + index } }));
        await workspace.Aliases.SetAsync(identities[2], "My selected alias");

        var rows = await session.ReadMatchingOnDemandAsync(new(Search: "Wanted", Language: "ENG", Codec: "PCM", Channels: 2,
            Sort: AssetSort.Duration, Descending: true, Offset: 99, Limit: 1));

        Assert.Equal(new[] { identities[4], identities[2], identities[0] }, rows.Select(row => row.Identity));
        Assert.All(rows, row => Assert.True(row.SelectedSourceVerified));
        Assert.Equal("My selected alias", rows[1].Label);
        Assert.False(session.IsCatalogLoaded); Assert.False(session.IsEnrichmentStarted); Assert.Empty(session.Catalog.Media);
    }

    [Fact]
    public async Task MatchingSelectionHydratesEveryCapturedPageInItsOriginalOrder()
    {
        int count = FeatureStore.MaximumLinkedFeaturePageRows * 2 + 3;
        using var fixture = FormatsFixtureBuilder.CreateDialogueMediaCatalog(count);
        using var workspace = new Workspace();
        await using var session = await OpenAsync(fixture, workspace);
        var descriptors = SeedRows(session);
        var progress = new CapturingProgress();

        var rows = await session.ReadMatchingAsync(new(Offset: count + 1, Limit: 1, Sort: AssetSort.Duration, Descending: true), progress);

        Assert.Equal(count, rows.Count);
        Assert.Equal(descriptors.Reverse().Select(row => row.Identity), rows.Select(row => row.Identity));
        Assert.Equal(count, rows.Select(row => row.Identity).Distinct().Count());
        Assert.All(rows, row => Assert.True(row.SelectedSourceVerified));
        Assert.All(progress.Items, item => Assert.Equal(count, item.Total));
        Assert.Equal(new long[] { 0, FeatureStore.MaximumLinkedFeaturePageRows, FeatureStore.MaximumLinkedFeaturePageRows * 2, count },
            progress.Items.Select(item => item.Completed));
        Assert.False(session.IsCatalogLoaded); Assert.False(session.IsEnrichmentStarted); Assert.Empty(session.Catalog.Media);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBetweenMatchingPagesDrainsItsSessionOwnership(bool disposeSession)
    {
        int count = FeatureStore.MaximumLinkedFeaturePageRows * 2 + 3;
        using var fixture = FormatsFixtureBuilder.CreateDialogueMediaCatalog(count);
        using var workspace = new Workspace();
        var session = await OpenAsync(fixture, workspace);
        Task? disposal = null;
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new CapturingProgress(item =>
        {
            if (item.Completed != FeatureStore.MaximumLinkedFeaturePageRows) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Matching page barrier expired.");
        });
        Task<IReadOnlyList<AudioExplorerRow>>? pending = null;
        try
        {
            var descriptors = SeedRows(session);
            CancellationToken sourceCancellation;
            using (var source = session.AcquireSharedFeatureStore()) sourceCancellation = source.SourceCancellation;
            pending = Task.Run(() => session.ReadMatchingAsync(new(), progress, cancellation.Token));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (disposeSession)
            {
                var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = sourceCancellation.Register(() => cancellationObserved.TrySetResult());
                disposal = session.DisposeAsync().AsTask();
                await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(disposal.IsCompleted);
            }
            else cancellation.Cancel();
            Assert.False(pending.IsCompleted);
            release.Set();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.All(progress.Items, item => Assert.Equal(count, item.Total));
            Assert.Equal(new long[] { 0, FeatureStore.MaximumLinkedFeaturePageRows }, progress.Items.Select(item => item.Completed));
            if (disposal is not null)
            {
                await disposal.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Throws<ObjectDisposedException>(() => session.AcquireSharedFeatureStore());
            }
            else
            {
                var next = descriptors[FeatureStore.MaximumLinkedFeaturePageRows].Identity;
                Assert.DoesNotContain(session.ResolveKnownSelection([next.Key]), row => row.SelectedSourceVerified);
                Assert.True((await session.PrepareSelectedMediaAsync(next)).SelectedSourceVerified);
                Assert.False(session.IsCatalogLoaded); Assert.False(session.IsEnrichmentStarted); Assert.Empty(session.Catalog.Media);
            }
        }
        finally
        {
            release.Set(); cancellation.Cancel();
            if (pending is not null) try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            await (disposal ?? session.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task RemovingACapturedLaterPageIdentityFailsInsteadOfShrinkingTheSelection()
    {
        int count = FeatureStore.MaximumLinkedFeaturePageRows * 2 + 3;
        using var fixture = FormatsFixtureBuilder.CreateDialogueMediaCatalog(count);
        using var workspace = new Workspace();
        await using var session = await OpenAsync(fixture, workspace);
        var descriptors = SeedRows(session);
        var removed = descriptors[FeatureStore.MaximumLinkedFeaturePageRows + 1].Identity;
        int changes = 0;
        var progress = new CapturingProgress(item =>
        {
            if (item.Completed != FeatureStore.MaximumLinkedFeaturePageRows || Interlocked.Exchange(ref changes, 1) != 0) return;
            using var source = session.AcquireSharedFeatureStore();
            source.Store.ReplaceAssets(AudioExplorerSession.AssetFamily, descriptors.Where(row => row.Identity != removed));
        });

        var error = await Assert.ThrowsAsync<ArchiveException>(() => session.ReadMatchingAsync(new(), progress));

        Assert.Equal("MissingMedia", error.Code); Assert.Equal(1, changes);
        Assert.Equal(new long[] { 0, FeatureStore.MaximumLinkedFeaturePageRows }, progress.Items.Select(item => item.Completed));
        Assert.All(progress.Items, item => Assert.Equal(count, item.Total));
        Assert.False(session.IsCatalogLoaded); Assert.False(session.IsEnrichmentStarted); Assert.Empty(session.Catalog.Media);
        Assert.True((await session.PrepareSelectedMediaAsync(descriptors[^1].Identity)).SelectedSourceVerified);
    }

    private static Task<AudioExplorerSession> OpenAsync(FormatsFixtureBuilder fixture, Workspace workspace) =>
        AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases,
            new AudioToolPaths(null, null, null, null, null, null, null), null, CancellationToken.None, startEnrichment: false);

    private static AssetDescriptor[] SeedRows(AudioExplorerSession session)
    {
        var snapshot = session.Snapshot;
        var descriptors = snapshot.Records.Select((row, index) => new AssetDescriptor(new(snapshot.IndexContentId, row.Ordinal, "wem"),
            new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), AudioExplorerSession.AssetFamily,
            "Generated selection " + index, "__deviant_standalone__", AssetAvailability.Available, AssetCompleteness.Complete,
            AssetCapabilities.None, [], [], "ENG", "PCM", index, 2)
            { Metadata = new Dictionary<string, string> { ["audio.source-label"] = "Generated selection " + index } }).ToArray();
        using var source = session.AcquireSharedFeatureStore(); source.Store.UpsertAssets(descriptors);
        return descriptors;
    }

    private sealed class CapturingProgress(Action<AudioExplorerProgress>? callback = null) : IProgress<AudioExplorerProgress>
    {
        internal ConcurrentQueue<AudioExplorerProgress> Items { get; } = new();
        public void Report(AudioExplorerProgress value) { Items.Enqueue(value); callback?.Invoke(value); }
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "Deviant-matching-tests", Guid.NewGuid().ToString("N"));
        internal CacheManager Cache { get; }
        internal AliasStore Aliases { get; }
        internal Workspace() { Cache = new(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"))); Aliases = new(Path.Combine(root, "library")); }
        public void Dispose() { Cache.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
