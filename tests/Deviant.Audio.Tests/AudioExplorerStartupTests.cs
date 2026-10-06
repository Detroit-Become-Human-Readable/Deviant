using System.Collections.Concurrent;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
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
    public async Task DeferredWarmOpenReturnsCompletedTaskWithoutStartingColdReader()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new StartupWorkspace();
        int expected;
        await using (var initial = await AudioExplorerSession.OpenAsync(fixture.IndexPath,
            workspace.Cache, workspace.Aliases, NoTools)) expected = initial.Catalog.Media.Count;
        var progress = new RecordingProgress();
        await using var cached = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,
            workspace.Cache, workspace.Aliases, NoTools, progress, CancellationToken.None, startEnrichment: false);

        Assert.True(cached.IsEnrichmentComplete);
        Assert.False(cached.IsEnrichmentStarted);
        Assert.Equal(expected, cached.Catalog.Media.Count);
        Assert.True(cached.EnrichmentTask.IsCompletedSuccessfully);
        Assert.False(cached.IsEnrichmentStarted);
        Assert.DoesNotContain(progress.Items, item => IsAudioProgress(item.Phase));
        await cached.DisposeAsync();
        Assert.True(cached.EnrichmentTask.IsCompletedSuccessfully);
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
        public StartupWorkspace()
        {
            Cache = new(Path.Combine(root, "settings"), new CacheSettings(Path.Combine(root, "cache")));
            Aliases = new(Path.Combine(root, "library"));
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
