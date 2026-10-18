using System.Reflection;
using Deviant.Assets;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class AudioBrowseReadinessTests
{
    [Fact]
    public async Task ColdPublicationRepairsACompetingAssetWriteBeforePublishingCachedPages()
    {
        using var fixture = FormatsFixtureBuilder.CreateDialogueMediaCatalog(3);
        var root = Path.Combine(Path.GetTempPath(), "deviant-audio-publication-" + Guid.NewGuid().ToString("N"));
        using var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache")));
        var aliases = new AliasStore(Path.Combine(root, "library"));
        var tools = new AudioToolPaths(null, null, null, null, null, null, null);
        var restore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<AudioExplorerProgress>? onReport = null;
        var progress = new PublicationProgress(value => onReport?.Invoke(value));
        try
        {
            await using (var cold = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, cache, aliases, tools,
                progress, CancellationToken.None, startEnrichment: false))
            {
                using var writer = FeatureStore.Open(cold.Snapshot, cache.Location);
                var writes = 0;
                onReport = update =>
                {
                    if (update.Phase != "Saving audio library" || update.Completed != 0 || writes != 0) return;
                    var asset = writer.Query(new(Family: AudioExplorerSession.AssetFamily, Limit: 1)).Single();
                    writer.UpsertAssets([asset with { Label = "Competing update" }]);
                    writes++;
                };
                await cold.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(1, writes);
                Assert.DoesNotContain(writer.Query(new(Family: AudioExplorerSession.AssetFamily, Limit: 100)), asset => asset.Label == "Competing update");
            }
            await using var warm = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, cache, aliases, tools,
                null, CancellationToken.None, startEnrichment: false);
            var complete = new Lazy<Task>(() => restore.Task);
            typeof(AudioExplorerSession).GetField("audioInitialization", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(warm, complete);
            try
            {
                Assert.Equal(3, (await warm.QueryAsync(new()).WaitAsync(TimeSpan.FromSeconds(5))).Total);
                Assert.False(complete.IsValueCreated);
            }
            finally { restore.TrySetResult(); }
        }
        finally
        {
            restore.TrySetResult(); cache.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CancellationAfterAnEvictionKeepsRetainedRowsAndEstimatedBytesBounded()
    {
        using var fixture = FormatsFixtureBuilder.CreateDialogueMediaCatalog(1200);
        var root = Path.Combine(Path.GetTempPath(), "deviant-audio-retention-" + Guid.NewGuid().ToString("N"));
        var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache")));
        var aliases = new AliasStore(Path.Combine(root, "library"));
        var tools = new AudioToolPaths(null, null, null, null, null, null, null);
        try
        {
            await using (var cold = await AudioExplorerSession.OpenAsync(fixture.IndexPath, cache, aliases, tools)) { }
            await using var warm = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, cache, aliases, tools,
                null, CancellationToken.None, startEnrichment: false);
            using var cancellation = new CancellationTokenSource();
            long observedRows = -1, observedBytes = -1;
            using var subscription = AssetLoadDiagnostics.Listener.Subscribe(new RetentionObserver(value =>
            {
                if (value.Key != warm.Snapshot.PhysicalSourceStamp) return;
                observedRows = value.Values["Rows"]; observedBytes = value.Values["EstimatedBytes"];
                cancellation.Cancel();
            }));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                for (int offset = 0; offset < 1200; offset += 100)
                    await warm.QueryAsync(new(Offset: offset), cancellation.Token);
            });
            Assert.InRange(observedRows, 0, 512);
            Assert.InRange(observedBytes, 0, 32L * 1024 * 1024);
            var retainedBytes = (long)typeof(AudioExplorerSession).GetField("retainedBrowseBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(warm)!;
            Assert.InRange(retainedBytes, 0, 32L * 1024 * 1024);
            Assert.False(warm.IsCatalogLoaded);
            Assert.NotEmpty((await warm.QueryAsync(new())).Rows);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CachedPagesAndFacetsDoNotWaitForCompleteCatalogRestoration()
    {
        using var fixture = FormatsFixtureBuilder.CreateDialogueMediaCatalog(1200);
        var root = Path.Combine(Path.GetTempPath(), "deviant-audio-browse-" + Guid.NewGuid().ToString("N"));
        var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache")));
        var aliases = new AliasStore(Path.Combine(root, "library"));
        var tools = new AudioToolPaths(null, null, null, null, null, null, null);
        var restore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using (var cold = await AudioExplorerSession.OpenAsync(fixture.IndexPath, cache, aliases, tools))
                Assert.Equal(1200, (await cold.QueryAsync(new())).Total);
            await using var warm = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, cache, aliases, tools,
                null, CancellationToken.None, startEnrichment: false);
            var complete = new Lazy<Task>(() => restore.Task);
            typeof(AudioExplorerSession).GetField("audioInitialization", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(warm, complete);
            try
            {
                var first = await warm.QueryAsync(new()).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1200, first.Total);
                Assert.Equal(100, first.Rows.Count);
                Assert.Equal(0, first.Rows[0].Identity.Ordinal);
                var second = await warm.QueryAsync(new(Offset: 100)).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1200, second.Total);
                Assert.Equal(100, second.Rows.Count);
                Assert.Equal(100, second.Rows[0].Identity.Ordinal);
                Assert.Empty(first.Rows.Select(row => row.Identity).Intersect(second.Rows.Select(row => row.Identity)));
                Assert.Contains("ENG", warm.Languages);
                Assert.Contains("Wwise Vorbis", warm.Codecs);
                Assert.Single(warm.Banks);
                Assert.False(complete.IsValueCreated);
                Assert.All(first.Rows, row => Assert.True(row.CanPreview));
                Assert.Equal(first.Generation, second.Generation);
                Assert.Equal(2, warm.ResolveSelection([first.Rows[0].Identity, second.Rows[0].Identity]).Count);
                Assert.False(complete.IsValueCreated);
            }
            finally { restore.TrySetResult(); }
        }
        finally
        {
            restore.TrySetResult();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class RetentionObserver(Action<AssetLoadCounterMeasurement> report) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        { if (value.Value is AssetLoadCounterMeasurement { Stage: "AudioBrowseRetention" } measurement) report(measurement); }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private sealed class PublicationProgress(Action<AudioExplorerProgress> report) : IProgress<AudioExplorerProgress>
    {
        public void Report(AudioExplorerProgress value) => report(value);
    }
}
