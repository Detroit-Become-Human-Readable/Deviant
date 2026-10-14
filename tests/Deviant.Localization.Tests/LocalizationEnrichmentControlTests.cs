using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Reflection;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Localization.Model;
using Xunit;

namespace Deviant.Localization.Tests;

public sealed class LocalizationEnrichmentControlTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PublicationQueuesPageAndSelectedRecordReadsUntilTheGenerationTransitionFinishes(bool rebuildSearch, bool cancel)
    {
        using var fixture = CreateFixture(resourceCount: 1, recordsPerResource: FeatureStore.LocalizationGenerationBatchLimit);
        var snapshot = WithReliableStamps(await fixture.BuildAsync(), out var revisions);
        if (rebuildSearch)
        {
            await using (var seed = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath, sourceRevisionProvider: revisions))
                await seed.EnrichmentTask;
            using var store = FeatureStore.Open(snapshot, fixture.CachePath);
            using var current = store.OpenLocalizationGeneration<LocalizationGenerationHead>(
                LocalizationCacheContract.RecordStoreVersion, LocalizationCacheContract.SearchProjectionVersion, HeadIdentity, _ => true)!;
            const string oldProjection = "generated-previous-search";
            using var writer = store.BeginLocalizationGeneration(LocalizationCacheContract.RecordStoreVersion, oldProjection);
            var page = store.LoadLocalizationRecordPage<LocalizationGenerationHead, LocalizationRecord>(current, -1,
                FeatureStore.LocalizationGenerationBatchLimit);
            store.AppendLocalizationGeneration(writer, page.Items.Select(item =>
                new LocalizationGenerationRow<LocalizationRecord>(item.IdentityKey, item.Record, SearchAsset(snapshot, item.Record))).ToArray());
            using var prior = store.PublishLocalizationGeneration(writer, current.Head with { SearchProjectionVersion = oldProjection }, HeadIdentity);
        }

        using var releaseProducer = new ManualResetEventSlim();
        using var releaseStore = new ManualResetEventSlim();
        var producerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress<LocalizationReadProgress>(value =>
        {
            if (value.Phase != (rebuildSearch ? "Updating localization search" : "Reading localization") ||
                value.Completed != (rebuildSearch ? FeatureStore.LocalizationGenerationBatchLimit : 1)) return;
            producerEntered.TrySetResult();
            releaseProducer.Wait();
        });
        LocalizationSession? session = null;
        Task? storeBlock = null;
        try
        {
            session = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath, progress, sourceRevisionProvider: revisions);
            await producerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var selected = Assert.Single((await session.QueryAsync(new LocalizationQuery(Limit: 1))).Records);
            var store = (FeatureStore)typeof(LocalizationSession).GetField("featureStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            var storeGate = typeof(FeatureStore).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            var featureGate = (SemaphoreSlim)typeof(LocalizationSession).GetField("featureGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            storeBlock = Task.Run(() =>
            {
                lock (storeGate) { storeEntered.TrySetResult(); releaseStore.Wait(); }
            });
            await storeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            releaseProducer.Set();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (featureGate.CurrentCount != 0) await Task.Delay(10, deadline.Token);

            var pageRead = session.QueryAsync(new LocalizationQuery(Limit: 10));
            var recordRead = session.GetRecordsAsync([selected.Identity]);
            Assert.False(pageRead.IsCompleted);
            Assert.False(recordRead.IsCompleted);
            if (cancel) Assert.True(session.CancelEnrichment());
            releaseStore.Set();
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.EnrichmentTask);
            else await session.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(FeatureStore.LocalizationGenerationBatchLimit, (await pageRead.WaitAsync(TimeSpan.FromSeconds(10))).Total);
            var loaded = Assert.Single(await recordRead.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(selected.Identity, loaded.Identity);
            Assert.Equal(selected.Text, loaded.Text);
            Assert.Equal(!cancel, session.IsEnrichmentComplete);
        }
        finally
        {
            releaseStore.Set(); releaseProducer.Set();
            if (storeBlock is not null) await storeBlock;
            if (session is not null) await session.DisposeAsync();
            DeleteCache(fixture.CachePath);
        }
    }

    [Fact]
    public async Task CancelStopsProducerAndLeavesCompletedStagedRowsQueryable()
    {
        using var fixture = CreateFixture(resourceCount: 40, recordsPerResource: 1);
        var snapshot = await fixture.BuildAsync();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress<LocalizationReadProgress>(value =>
        {
            if (value.Phase != "Reading localization" || value.Completed != 33) return;
            entered.TrySetResult();
            release.Wait();
        });
        try
        {
            await using var session = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath, progress);
            var producer = session.EnrichmentTask;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(session.CancelEnrichment());
            Assert.Equal("Cancelled", session.Status);
            Assert.False(session.IsEnrichmentComplete);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => producer);

            var page = await session.QueryAsync(new LocalizationQuery(Limit: 100));
            Assert.Equal(32, page.Total);
            Assert.False(session.IsEnrichmentComplete);
            Assert.False(session.CancelEnrichment());
        }
        finally
        {
            release.Set();
            DeleteCache(fixture.CachePath);
        }
    }

    [Fact]
    public async Task RetryOnRunningProducerCoalescesWithoutCancellingIt()
    {
        using var fixture = CreateFixture(resourceCount: 4, recordsPerResource: 1);
        var snapshot = await fixture.BuildAsync();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress<LocalizationReadProgress>(value =>
        {
            if (value.Phase != "Reading localization" || value.Completed != 1) return;
            entered.TrySetResult();
            release.Wait();
        });
        try
        {
            await using var session = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath, progress);
            var producer = session.EnrichmentTask;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var retry = session.RetryEnrichmentAsync();
            Assert.False(retry.IsCompleted);
            Assert.Same(producer, session.EnrichmentTask);
            Assert.False(producer.IsCanceled);
            release.Set();

            await retry.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(producer, session.EnrichmentTask);
            Assert.True(session.IsEnrichmentComplete);
            Assert.False(session.CancelEnrichment());
            Assert.Equal("Localization ready", session.Status);
            Assert.Equal(4, session.RecordCount);
        }
        finally
        {
            release.Set();
            DeleteCache(fixture.CachePath);
        }
    }

    [Fact]
    public async Task ConcurrentRetryRequestsDrainOneCancelledProducerAndStartOneReplacement()
    {
        using var fixture = CreateFixture(resourceCount: 40, recordsPerResource: 1);
        var snapshot = await fixture.BuildAsync();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockFirst = 1;
        var progress = new InlineProgress<LocalizationReadProgress>(value =>
        {
            if (value.Phase != "Reading localization" || value.Completed != 33 || Interlocked.Exchange(ref blockFirst, 0) == 0) return;
            entered.TrySetResult();
            release.Wait();
        });
        try
        {
            await using var session = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath, progress);
            var firstProducer = session.EnrichmentTask;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(session.CancelEnrichment());

            var retryA = session.RetryEnrichmentAsync();
            var retryB = session.RetryEnrichmentAsync();
            Assert.False(retryA.IsCompleted);
            Assert.False(retryB.IsCompleted);
            release.Set();

            await Task.WhenAll(retryA, retryB).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(firstProducer.IsCanceled);
            Assert.NotSame(firstProducer, session.EnrichmentTask);
            Assert.True(session.IsEnrichmentComplete);
            Assert.Equal(40, session.RecordCount);
            Assert.Equal(40, (await session.QueryAsync(new LocalizationQuery(Limit: 100))).Total);
        }
        finally
        {
            release.Set();
            DeleteCache(fixture.CachePath);
        }
    }

    [Fact]
    public async Task CancelPendingRetryWhileOldProducerDrainsPreventsReplacement()
    {
        using var fixture = CreateFixture(resourceCount: 40, recordsPerResource: 1);
        var snapshot = await fixture.BuildAsync();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress<LocalizationReadProgress>(value =>
        {
            if (value.Phase != "Reading localization" || value.Completed != 33) return;
            entered.TrySetResult();
            release.Wait();
        });
        try
        {
            await using var session = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath, progress);
            var producer = session.EnrichmentTask;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(session.CancelEnrichment());

            var retry = session.RetryEnrichmentAsync();
            Assert.False(retry.IsCompleted);
            Assert.True(session.CancelEnrichment());
            release.Set();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => producer);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
            Assert.Same(producer, session.EnrichmentTask);
            Assert.False(session.IsEnrichmentComplete);
            Assert.Equal(32, (await session.QueryAsync(new LocalizationQuery(Limit: 100))).Total);
        }
        finally
        {
            release.Set();
            DeleteCache(fixture.CachePath);
        }
    }

    [Fact]
    public async Task CancelReplacementProducerLeavesItsStagedRowsQueryable()
    {
        using var fixture = CreateFixture(resourceCount: 40, recordsPerResource: 1);
        var snapshot = await fixture.BuildAsync();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseReplacement = new ManualResetEventSlim();
        var enteredFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredReplacement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedPass = 0;
        var progress = new InlineProgress<LocalizationReadProgress>(value =>
        {
            if (value.Phase != "Reading localization" || value.Completed != 33) return;
            var pass = Interlocked.Increment(ref blockedPass);
            if (pass == 1)
            {
                enteredFirst.TrySetResult();
                releaseFirst.Wait();
            }
            else if (pass == 2)
            {
                enteredReplacement.TrySetResult();
                releaseReplacement.Wait();
            }
        });
        try
        {
            await using var session = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath, progress);
            var firstProducer = session.EnrichmentTask;
            await enteredFirst.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(session.CancelEnrichment());
            releaseFirst.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstProducer);

            var retry = session.RetryEnrichmentAsync();
            await enteredReplacement.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var replacement = session.EnrichmentTask;
            Assert.NotSame(firstProducer, replacement);
            Assert.True(session.CancelEnrichment());
            releaseReplacement.Set();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacement);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
            Assert.False(session.IsEnrichmentComplete);
            Assert.Equal(32, (await session.QueryAsync(new LocalizationQuery(Limit: 100))).Total);
        }
        finally
        {
            releaseFirst.Set();
            releaseReplacement.Set();
            DeleteCache(fixture.CachePath);
        }
    }

    [Fact]
    public async Task DisposeCancelsAndDrainsProducerBlockedInProgressObserver()
    {
        using var fixture = CreateFixture(resourceCount: 40, recordsPerResource: 1);
        var snapshot = await fixture.BuildAsync();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress<LocalizationReadProgress>(value =>
        {
            if (value.Phase != "Reading localization" || value.Completed != 33) return;
            entered.TrySetResult();
            release.Wait();
        });
        try
        {
            var session = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath, progress);
            var producer = session.EnrichmentTask;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var disposal = session.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            release.Set();
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => producer);
        }
        finally
        {
            release.Set();
            DeleteCache(fixture.CachePath);
        }
    }

    [Fact]
    public async Task RetryRejectsChangedSourceAndLeavesSessionFailClosed()
    {
        using var fixture = CreateFixture(resourceCount: 4, recordsPerResource: 1);
        var snapshot = await fixture.BuildAsync();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress<LocalizationReadProgress>(value =>
        {
            if (value.Phase != "Reading localization" || value.Completed != 1) return;
            entered.TrySetResult();
            release.Wait();
        });
        try
        {
            await using var session = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath, progress);
            var producer = session.EnrichmentTask;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(session.CancelEnrichment());
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => producer);

            var volume = await File.ReadAllBytesAsync(fixture.VolumePath);
            Array.Resize(ref volume, volume.Length + 1);
            volume[^1] = 0x5A;
            await File.WriteAllBytesAsync(fixture.VolumePath, volume);

            var changed = await Assert.ThrowsAsync<ArchiveException>(() => session.RetryEnrichmentAsync());
            Assert.Equal("SourceChanged", changed.Code);
            Assert.False(session.IsEnrichmentComplete);
            var unavailable = await Assert.ThrowsAsync<ArchiveException>(() => session.QueryAsync(
                new LocalizationQuery(Limit: 10)));
            Assert.Equal("LocalizationUnavailable", unavailable.Code);
        }
        finally
        {
            release.Set();
            DeleteCache(fixture.CachePath);
        }
    }

    [Fact]
    public async Task CancelledProjectionKeepsValidatedBaseGenerationUntilRetryPublishes()
    {
        using var fixture = CreateFixture(resourceCount: 1, recordsPerResource: 513);
        var original = await fixture.BuildAsync();
        var snapshot = WithReliableStamps(original, out var revisions);
        const string oldProjectionVersion = "generated-search-projection-before-upgrade";
        try
        {
            await using (var initial = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath,
                sourceRevisionProvider: revisions))
                await initial.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(10));

            using (var store = FeatureStore.Open(snapshot, fixture.CachePath))
            using (var previous = store.OpenLocalizationGeneration<LocalizationGenerationHead>(
                LocalizationCacheContract.RecordStoreVersion,
                LocalizationCacheContract.SearchProjectionVersion, HeadIdentity, _ => true)!)
            {
                var writer = store.BeginLocalizationGeneration(LocalizationCacheContract.RecordStoreVersion,
                    oldProjectionVersion);
                var page = store.LoadLocalizationRecordPage<LocalizationGenerationHead, LocalizationRecord>(
                    previous, -1, 1024);
                var rows = page.Items.Select(item => new LocalizationGenerationRow<LocalizationRecord>(
                    item.IdentityKey, item.Record, SearchAsset(snapshot, item.Record))).ToArray();
                foreach (var batch in rows.Chunk(FeatureStore.LocalizationGenerationBatchLimit))
                    store.AppendLocalizationGeneration(writer, batch);
                using var oldProjection = store.PublishLocalizationGeneration(writer,
                    previous.Head with { SearchProjectionVersion = oldProjectionVersion }, HeadIdentity);
            }

            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockFirst = 1;
            var progress = new InlineProgress<LocalizationReadProgress>(value =>
            {
                if (value.Phase != "Updating localization search" || value.Completed != 512 ||
                    Interlocked.Exchange(ref blockFirst, 0) == 0) return;
                entered.TrySetResult();
                release.Wait();
            });

            await using var upgraded = await LocalizationSession.OpenAsync(snapshot, fixture.CachePath, progress,
                sourceRevisionProvider: revisions);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var cancelledProducer = upgraded.EnrichmentTask;
            Assert.True(upgraded.CancelEnrichment());
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledProducer);

            using (var retainedStore = FeatureStore.Open(snapshot, fixture.CachePath))
            using (var retained = retainedStore.OpenLocalizationGeneration<LocalizationGenerationHead>(
                LocalizationCacheContract.RecordStoreVersion, oldProjectionVersion, HeadIdentity, _ => true))
            {
                Assert.NotNull(retained);
                Assert.Equal(513, retained!.Head.RecordCount);
                Assert.Equal(oldProjectionVersion, retained.Head.SearchProjectionVersion);
            }

            await upgraded.RetryEnrichmentAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(upgraded.IsEnrichmentComplete);
            Assert.Equal(513, (await upgraded.QueryAsync(new LocalizationQuery(Limit: 1000))).Total);
        }
        finally { DeleteCache(fixture.CachePath); }
    }

    private static LocalizationFixture CreateFixture(int resourceCount, int recordsPerResource)
    {
        var fixture = new LocalizationFixture();
        for (var resource = 0; resource < resourceCount; resource++)
        {
            var records = Enumerable.Range(0, recordsPerResource)
                .Select(record => LocalizationFixture.DirectText($"ENTRY_{resource:D4}_{record:D4}", "generated text"))
                .ToArray();
            fixture.AddResource(LocalizationFixture.Wrap(
                LocalizationFixture.MakeLocalization(6, ("ENG", records))));
        }
        return fixture;
    }

    private static AssetDescriptor SearchAsset(ArchiveSnapshot snapshot, LocalizationRecord record)
    {
        var search = string.Join('\n', new[] { record.Key, record.Text, record.RawLanguageCode, record.Language, record.Diagnostic }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return new AssetDescriptor(record.Identity,
            new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp),
            LocalizationSchema.AssetFamily, record.Key ?? "Generated localization record", record.ResourceIdentity.Key,
            AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.None, [], [],
            Language: record.Language, Codec: record.Kind.ToString())
        { SearchTerms = search, LookupKey = record.Key };
    }

    private static ArchiveSnapshot WithReliableStamps(ArchiveSnapshot snapshot,
        out ISourceRevisionProvider revisions)
    {
        var stamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase)
        { [snapshot.IndexPath] = ReliableStamp(snapshot.IndexPath) };
        var volumes = snapshot.Volumes.Select(volume =>
        {
            var updated = volume with { Stamp = ReliableStamp(volume.FilePath) };
            stamps[updated.FilePath] = updated.Stamp;
            return updated;
        }).ToArray();
        revisions = new FixedRevisionProvider(stamps);
        return snapshot with
        {
            IndexStamp = stamps[snapshot.IndexPath],
            Volumes = volumes,
            PhysicalSourceStamp = SourceIdentity.PhysicalStamp(volumes)
        };
    }

    private static FileStamp ReliableStamp(string path)
    {
        var file = new FileInfo(path);
        return new(true, Path.GetFullPath(path), file.Length,
            file.LastWriteTimeUtc.Ticks, file.LastWriteTimeUtc.Ticks, true);
    }

    private static LocalizationGenerationIdentity HeadIdentity(LocalizationGenerationHead head) => new(
        head.InputSnapshotId, head.ParsedResultId, head.RecordStoreVersion,
        head.SearchProjectionVersion, head.JsonPolicyVersion, head.RecordCount);

    private static void DeleteCache(string cachePath)
    {
        if (Directory.Exists(cachePath)) Directory.Delete(cachePath, recursive: true);
    }

    private sealed class FixedRevisionProvider(IReadOnlyDictionary<string, FileStamp> stamps) : ISourceRevisionProvider
    {
        public FileStamp Capture(string path) => stamps.TryGetValue(path, out var stamp)
            ? stamp : new(false, "", 0, 0, 0, false);
        public FileStamp Capture(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path) => Capture(path);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
