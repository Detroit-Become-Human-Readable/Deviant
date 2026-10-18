using System.Collections.Concurrent;
using Deviant.Assets;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Export.Audio;
using Deviant.Formats.Audio.Model;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class AudioExplorerStartupTests
{
    private static readonly AudioToolPaths NoTools = new(null, null, null, null, null, null, null);

    [Fact]
    public async Task DeferredOpenProvidesArchiveWithoutReadingAudioUntilDemandAndStartsOnce()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new StartupWorkspace();
        var progress = new RecordingProgress();
        await using var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,
            workspace.Cache, workspace.Aliases, NoTools, progress, CancellationToken.None, startEnrichment: false);

        Assert.Equal(Path.GetFullPath(fixture.IndexPath), session.Snapshot.IndexPath);
        Assert.NotEmpty(session.Snapshot.Records);
        Assert.All(session.Snapshot.Records, row => Assert.Equal(ResourceValidity.Valid, row.Validity));
        Assert.False(session.IsEnrichmentStarted);
        Assert.False(session.IsEnrichmentComplete);
        Assert.Empty(session.Catalog.Media);
        Assert.DoesNotContain(progress.Items, item => IsAudioProgress(item.Phase));
        Assert.Equal(0, (await session.QueryAsync(new())).Total);
        Assert.False(session.IsEnrichmentStarted);

        var starts = new Task[16];
        Parallel.For(0, starts.Length, index => starts[index] = session.EnrichmentTask);
        Assert.All(starts, task => Assert.Same(starts[0], task));
        await starts[0].WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(session.IsEnrichmentStarted);
        Assert.True(session.IsEnrichmentComplete);
        Assert.True(session.Catalog.Media.Count >= 2);
        Assert.Equal("Catalogue ready", session.CurrentProgress.Phase);
        Assert.Equal(session.Catalog.Media.Count, session.CurrentProgress.Completed);
        Assert.Equal(session.Catalog.Media.Count, session.CurrentProgress.Total);
        Assert.Single(progress.Items, item => item.Phase == "Catalogue ready");
        Assert.Contains(progress.Items, item => item.Phase == "Reading banks");
        Assert.Equal(session.Catalog.Media.Count, (await session.QueryAsync(new())).Total);
    }

    [Fact]
    public async Task DisposeBeforeDemandDoesNotReadLockedAudioSourceOrCreateEnrichmentTask()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new StartupWorkspace();
        var progress = new RecordingProgress();
        var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,
            workspace.Cache, workspace.Aliases, NoTools, progress, CancellationToken.None, startEnrichment: false);
        using var sourceLock = new FileStream(session.Snapshot.Volumes.Single().FilePath,
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(session.IsEnrichmentStarted);
        Assert.False(session.IsEnrichmentComplete);
        Assert.Empty(session.Catalog.Media);
        Assert.DoesNotContain(progress.Items, item => IsAudioProgress(item.Phase));
        Assert.Throws<ObjectDisposedException>(() => { _ = session.EnrichmentTask; });
        Assert.False(session.IsEnrichmentStarted);
    }

    [Fact]
    public async Task OriginalStagedOverloadRetainsEagerStartupAndClrSignature()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new StartupWorkspace();
        var progress = new RecordingProgress();
        Assert.NotNull(typeof(AudioExplorerSession).GetMethod(nameof(AudioExplorerSession.OpenStagedAsync),
            [typeof(string), typeof(CacheManager), typeof(AliasStore), typeof(AudioToolPaths),
             typeof(IProgress<AudioExplorerProgress>), typeof(CancellationToken)]));
        await using var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,
            workspace.Cache, workspace.Aliases, NoTools, progress);
        Assert.True(session.IsEnrichmentStarted);
        await session.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(session.IsEnrichmentComplete);
        Assert.Contains(progress.Items, item => item.Phase == "Reading banks");
    }

    [Fact]
    public async Task DeferredWarmOpenPublishesBrowseMetadataBeforeCompleteCatalogRestoration()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new StartupWorkspace();
        int expected;
        await using (var initial = await AudioExplorerSession.OpenAsync(fixture.IndexPath,
            workspace.Cache, workspace.Aliases, NoTools)) expected = initial.Catalog.Media.Count;
        var progress = new RecordingProgress();
        await using var cached = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,
            workspace.Cache, workspace.Aliases, NoTools, progress, CancellationToken.None, startEnrichment: false);

        Assert.False(cached.IsEnrichmentComplete);
        Assert.False(cached.IsEnrichmentStarted);
        Assert.Empty(cached.Catalog.Media);
        Assert.Empty(cached.Catalog.Banks);
        Assert.Empty(cached.Catalog.DialogueHeaderAssociations);
        Assert.Equal(expected, (await cached.QueryAsync(new())).Total);
        Assert.True(cached.IsEnrichmentComplete);
        Assert.Equal(expected, cached.MediaCount);
        Assert.Empty(cached.Catalog.Media);
        Assert.False(cached.IsCatalogLoaded);
        Assert.False(cached.IsEnrichmentStarted);
        await cached.LoadCatalogAsync();
        Assert.Equal(expected, cached.Catalog.Media.Count);
        Assert.True(cached.EnrichmentTask.IsCompletedSuccessfully);
        Assert.False(cached.IsEnrichmentStarted);
        Assert.DoesNotContain(progress.Items, item => IsAudioProgress(item.Phase));
        await cached.DisposeAsync();
        Assert.True(cached.EnrichmentTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ConcurrentFirstAudioQueriesRestoreWarmMetadataOnceAndPreserveAliases()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new StartupWorkspace();
        string selected;
        await using (var initial = await AudioExplorerSession.OpenAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases, NoTools))
        {
            var row = (await initial.QueryAsync(new(Limit: 100))).Rows.First();
            selected = row.Identity.Key;
            await initial.SetAliasAsync(row.Identity, "Generated alias");
        }
        using var observations = new RestoreObservations();
        await using var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases,
            NoTools, null, CancellationToken.None, startEnrichment: false);
        observations.Revision = session.Snapshot.PhysicalSourceStamp;
        Assert.Empty(session.Catalog.Media); Assert.Empty(session.Catalog.Events); Assert.False(session.AreDialogueHeadersReady);
        Assert.Equal(0, observations.Count);
        int updates = 0;
        session.Updated += (_, update) => { if (update.Phase == "Loaded cached audio metadata") Interlocked.Increment(ref updates); };
        var pages = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => session.QueryAsync(new(Limit: 100))))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, observations.Count); Assert.Equal(1, updates);
        Assert.All(pages, page => Assert.Equal(session.Catalog.Media.Count, page.Total));
        var selectedRow = Assert.Single(await session.ReadSelectionAsync([selected]));
        Assert.Equal("Generated alias", selectedRow.Label);
        Assert.True(session.IsEnrichmentComplete); Assert.True(session.AreDialogueHeadersReady);
        Assert.False(session.IsEnrichmentStarted);
    }

    [Fact]
    public async Task LockedToolsDoNotAffectOpeningEnrichmentQueriesOrOriginalExports()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new StartupWorkspace();
        var tools = workspace.CreateTools();
        using var toolLock = new FileStream(tools.VgmstreamCli!, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases,
            tools, null, CancellationToken.None, startEnrichment: false);
        Assert.Empty(session.Catalog.Media);
        await session.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(10));
        var page = await session.QueryAsync(new(Limit: 100));
        var row = page.Rows.First(candidate => candidate.Media.Completeness == MediaCompleteness.Complete && MediaPolicy.HasBytes(candidate.Media));
        var original = await session.ExportAsync(session.Review([row]), new(workspace.Output("media"), AudioExportFormat.Original));
        Assert.Equal(AudioExportStatus.Completed, Assert.Single(original.Manifest.Entries).Status);
        Assert.Equal("audio-original-copy-v1", original.Manifest.ToolFingerprint);
        var bank = await session.ExportBankAsync(session.Catalog.Banks.First().Key, workspace.Output("banks"));
        Assert.Equal(AudioExportStatus.Completed, Assert.Single(bank.Manifest.Entries).Status);
        Assert.Equal("audio-original-copy-v1", bank.Manifest.ToolFingerprint);
    }

    [Fact]
    public async Task PreviewDefersDecoderFingerprintUntilTheSelectedAction()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new StartupWorkspace();
        var tools = workspace.CreateTools();
        using var toolLock = new FileStream(tools.VgmstreamCli!, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using var session = await AudioExplorerSession.OpenAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases, tools);
        var row = (await session.QueryAsync(new(Limit: 100))).Rows.First(candidate => MediaPolicy.CanPreview(candidate.Media));
        await Assert.ThrowsAnyAsync<IOException>(() => session.PreviewAsync(row.Identity));
        Assert.True(session.IsEnrichmentComplete);
        Assert.NotEmpty((await session.QueryAsync(new())).Rows);
        Assert.Equal(0, workspace.Cache.ActivePreviewLeases);
    }

    [Fact]
    public async Task PrecancelledFirstQueryDoesNotStartAudioMetadataRestore()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new StartupWorkspace();
        await using (var initial = await AudioExplorerSession.OpenAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases, NoTools)) { }
        using var observations = new RestoreObservations();
        await using var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases,
            NoTools, null, CancellationToken.None, startEnrichment: false);
        observations.Revision = session.Snapshot.PhysicalSourceStamp;
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.QueryAsync(new(), cancel.Token));
        Assert.Equal(0, observations.Count); Assert.Empty(session.Catalog.Media);
        Assert.NotEmpty((await session.QueryAsync(new())).Rows);
        Assert.Equal(0, observations.Count);
        await session.LoadCatalogAsync();
        Assert.Equal(1, observations.Count);
    }

    private sealed class RestoreObservations : IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly IDisposable subscription;
        private int count;
        public string? Revision { get; set; }
        public int Count => Volatile.Read(ref count);
        public RestoreObservations() => subscription = AssetLoadDiagnostics.Listener.Subscribe(this);
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is AssetLoadMeasurement { Stage: "AudioMetadataRestore" } measurement && measurement.Key == Revision)
                Interlocked.Increment(ref count);
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose() => subscription.Dispose();
    }

    [Fact]
    public async Task DemandSurfacesSourceFaultAndDoesNotRetryStartedTask()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new StartupWorkspace();
        await using var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,
            workspace.Cache, workspace.Aliases, NoTools, null, CancellationToken.None, startEnrichment: false);
        using var sourceLock = new FileStream(session.Snapshot.Volumes.Single().FilePath,
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var task = session.EnrichmentTask;
        var error = await Assert.ThrowsAsync<ArchiveException>(() => task);
        Assert.Equal("SourceBusy", error.Code);
        Assert.True(task.IsFaulted);
        Assert.True(session.IsEnrichmentStarted);
        Assert.False(session.IsEnrichmentComplete);
        Assert.Equal("Enrichment failed", session.CurrentProgress.Phase);
        Assert.Same(task, session.EnrichmentTask);
    }

    [Fact]
    public async Task DisposeCancelsAndDrainsStartedEnrichmentAndCancellationRemainsObservable()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new StartupWorkspace();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new RecordingProgress(value =>
        {
            if (value.Phase != "Reading banks") return;
            entered.TrySetResult();
            release.Wait();
        });
        var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,
            workspace.Cache, workspace.Aliases, NoTools, progress, CancellationToken.None, startEnrichment: false);
        var task = session.EnrichmentTask;
        Task? disposing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            disposing = session.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
        }
        finally
        {
            release.Set();
            await (disposing ?? session.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(task.IsCanceled);
        Assert.False(session.IsEnrichmentComplete);
        Assert.Same(task, session.EnrichmentTask);
    }

    [Fact]
    public async Task CancelEnrichmentStopsMetadataWorkAndKeepsStagedRowsBrowsable()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new StartupWorkspace();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new RecordingProgress(value =>
        {
            if (value.Phase != "Reading banks") return;
            entered.TrySetResult();
            release.Wait();
        });
        await using var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,
            workspace.Cache, workspace.Aliases, NoTools, progress, CancellationToken.None, startEnrichment: false);
        var task = session.EnrichmentTask;
        int stagedMediaCount;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stagedMediaCount = session.Catalog.Media.Count;
            Assert.True(stagedMediaCount > 0);
            Assert.True(session.CancelEnrichment());
            Assert.Equal("Cancelled", session.CurrentProgress.Phase);
            Assert.False(session.IsEnrichmentComplete);
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.Equal(stagedMediaCount, session.Catalog.Media.Count);
        Assert.Equal(stagedMediaCount, (await session.QueryAsync(new())).Total);
        Assert.False(session.CancelEnrichment());
        Assert.Equal(task, session.EnrichmentTask);
    }

    [Fact]
    public async Task CompleteStagedMediaRemainsPreviewableAfterEnrichmentCancellation()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new StartupWorkspace();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new RecordingProgress(value =>
        {
            if (value.Phase != "Reading dialogue media") return;
            entered.TrySetResult();
            release.Wait();
        });
        await using var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,
            workspace.Cache, workspace.Aliases, NoTools, progress, CancellationToken.None, startEnrichment: false);
        var task = session.EnrichmentTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var media = Assert.Single(session.Catalog.Media,
                item => item.BankKey.Length == 0 && MediaPolicy.CanPreview(item));
            Assert.Equal(MediaCompleteness.Complete, media.Completeness);
            var row = Assert.Single((await session.QueryAsync(new())).Rows, item => item.Media.Identity == media.Identity);
            Assert.True(row.Descriptor.Capabilities.HasFlag(AssetCapabilities.Preview));
            Assert.True(session.CancelEnrichment());
        }
        finally { release.Set(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        var retained = Assert.Single((await session.QueryAsync(new())).Rows,
            item => item.Media.Identity == session.Catalog.Media.Single(candidate =>
                candidate.BankKey.Length == 0 && MediaPolicy.CanPreview(candidate)).Identity);
        Assert.True(retained.Descriptor.Capabilities.HasFlag(AssetCapabilities.Preview));
    }

    [Fact]
    public async Task StagedPrefetchMediaRemainsPartialAndDoesNotGainFullPreviewCapabilities()
    {
        using var fixture = FormatsFixtureBuilder.CreateMediaResolutionCatalog();
        using var workspace = new StartupWorkspace();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new RecordingProgress(value =>
        {
            if (value.Phase != "Reading banks" || value.Completed != value.Total) return;
            entered.TrySetResult();
            release.Wait();
        });
        await using var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,
            workspace.Cache, workspace.Aliases, NoTools, progress, CancellationToken.None, startEnrichment: false);
        var task = session.EnrichmentTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var partial = Assert.Single(session.Catalog.Media, media => media.State == MediaState.PrefetchOnly);
            Assert.Equal(MediaCompleteness.Fragment, partial.Completeness);
            var row = Assert.Single((await session.QueryAsync(new())).Rows,
                item => item.Media.Identity == partial.Identity);
            Assert.Equal(AssetCompleteness.Partial, row.Descriptor.Completeness);
            Assert.False(row.Descriptor.Capabilities.HasFlag(AssetCapabilities.Preview));
            Assert.False(row.Descriptor.Capabilities.HasFlag(AssetCapabilities.PartialPreview));
            Assert.False(row.Descriptor.Capabilities.HasFlag(AssetCapabilities.Wav));
            Assert.False(row.Descriptor.Capabilities.HasFlag(AssetCapabilities.Ogg));
            Assert.True(session.CancelEnrichment());
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task BankEmbeddedRiffWithoutHircDeclarationIsPreviewableAfterLocalCheckWithoutPromotion()
    {
        using var fixture = FormatsFixtureBuilder.CreateUnreferencedEmbeddedBank();
        using var workspace = new StartupWorkspace();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new RecordingProgress(value =>
        {
            if (value.Phase != "Reading banks") return;
            entered.TrySetResult();
            release.Wait();
        });
        await using var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,
            workspace.Cache, workspace.Aliases, NoTools, progress, CancellationToken.None, startEnrichment: false);
        var task = session.EnrichmentTask;
        string selectedIdentity = "";
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var media = Assert.Single(session.Catalog.Media);
            selectedIdentity = Assert.IsType<Deviant.Assets.ResourceIdentity>(media.Identity).Key;
            Assert.Equal(MediaState.CompleteEmbedded, media.State);
            Assert.Equal(MediaCompleteness.Unverified, media.Completeness);
            Assert.DoesNotContain(session.Catalog.SourceDeclarations, source => source.BankKey == media.BankKey);
            Assert.True(MediaPolicy.CanPreview(media));

            var page = await session.QueryAsync(new());
            var row = Assert.Single(page.Rows);
            Assert.Equal(1, page.Total);
            Assert.Equal(AssetCompleteness.Unverified, row.Descriptor.Completeness);
            Assert.Contains("generated_music_bank", row.Descriptor.Label, StringComparison.OrdinalIgnoreCase);
            Assert.True(row.Descriptor.Capabilities.HasFlag(AssetCapabilities.Preview));
            Assert.True(session.CancelEnrichment());
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        var retained = Assert.Single((await session.QueryAsync(new())).Rows, row => row.Identity.Key == selectedIdentity);
        Assert.Equal(MediaCompleteness.Unverified, retained.Media.Completeness);
        Assert.True(retained.Descriptor.Capabilities.HasFlag(AssetCapabilities.Preview));
    }

    [Fact]
    public async Task CompletedLegacyWarmSnapshotRestoresDeclarationProcessingReadiness()
    {
        using var fixture = FormatsFixtureBuilder.CreateUnreferencedEmbeddedBank();
        using var workspace = new StartupWorkspace();
        await using (var initial = await AudioExplorerSession.OpenAsync(fixture.IndexPath,
            workspace.Cache, workspace.Aliases, NoTools))
        {
            var row = Assert.Single(initial.Catalog.Media);
            Assert.Equal(MediaCompleteness.Unverified, row.Completeness);
            Assert.True(row.SourceDeclarationProcessingComplete);
        }

        var catalogPath = Assert.Single(Directory.GetFiles(workspace.Cache.Location, "catalog-*.sqlite3"));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = catalogPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE asset_features SET payload=json_remove(payload,'$.SourceDeclarationProcessingComplete') WHERE feature LIKE 'audio.media/item/%'";
            Assert.True(command.ExecuteNonQuery() > 0);
        }

        await using var warm = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,
            workspace.Cache, workspace.Aliases, NoTools, null, CancellationToken.None, startEnrichment: false);
        var page = await warm.QueryAsync(new());
        var cached = Assert.Single(page.Rows);
        Assert.True(warm.IsEnrichmentComplete);
        Assert.Equal(MediaCompleteness.Unverified, cached.Media.Completeness);
        Assert.True(cached.Media.SourceDeclarationProcessingComplete);
        Assert.Equal(AssetCompleteness.Unverified, cached.Descriptor.Completeness);
        Assert.True(cached.Descriptor.Capabilities.HasFlag(AssetCapabilities.Preview));
    }

    [Fact]
    public async Task DeferredOpenStillHonorsPrecancelledOpenToken()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new StartupWorkspace();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AudioExplorerSession.OpenStagedAsync(
            fixture.IndexPath, workspace.Cache, workspace.Aliases, NoTools, null,
            cancellation.Token, startEnrichment: false));
    }

    private static bool IsAudioProgress(string phase) => phase is "Reading banks" or
        "Reading events" or "Reading dialogue media" or "Audio ready" or "Catalogue ready";

    private sealed class RecordingProgress(Action<AudioExplorerProgress>? onReport = null) : IProgress<AudioExplorerProgress>
    {
        public ConcurrentQueue<AudioExplorerProgress> Items { get; } = new();
        public void Report(AudioExplorerProgress value) { Items.Enqueue(value); onReport?.Invoke(value); }
    }

    private sealed class StartupWorkspace : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-startup-" + Guid.NewGuid().ToString("N"));
        public CacheManager Cache { get; }
        public AliasStore Aliases { get; }
        public string Output(string name) => Path.Combine(root, "exports", name);
        public AudioToolPaths CreateTools()
        {
            string directory = Path.Combine(root, "tools"); Directory.CreateDirectory(directory);
            string decoder = Path.Combine(directory, "decoder.exe"); File.WriteAllBytes(decoder, [1, 2, 3]);
            string encoder = Path.Combine(directory, "encoder.exe"); File.WriteAllBytes(encoder, [4, 5, 6]);
            return new(decoder, encoder, encoder, encoder, encoder, null, null, directory);
        }
        public StartupWorkspace()
        {
            Cache = new(Path.Combine(root, "settings"), new CacheSettings(Path.Combine(root, "cache")));
            Aliases = new(Path.Combine(root, "library"));
        }
        public void Dispose() { Cache.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
