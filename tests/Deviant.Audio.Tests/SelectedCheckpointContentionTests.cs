using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deviant.Assets;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;
using Xunit;

namespace Deviant.Audio.Tests;

[Collection("Selected checkpoint diagnostics")]
public sealed class SelectedCheckpointContentionTests
{
    [Fact]
    public async Task VerifiedSelectionDoesNotWaitForAnOptionalCheckpointWriterAndRetriesLater()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new Workspace();
        await using var session = await OpenAsync(fixture, workspace);
        var selected = await SeedNativeMediaAsync(session);
        await using var diagnostics = new DiagnosticScope(workspace.DiagnosticRoot);
        var gate = WriterGate(session);
        await gate.WaitAsync(); bool held = true;
        using var verified = new VerificationObserver(session.Snapshot.PhysicalSourceStamp);
        var reading = session.PrepareSelectedMediaAsync(selected.Identity!);
        try
        {
            await verified.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var ready = await reading.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(ready.SelectedSourceVerified); Assert.True(ready.CanExportOriginal);
            Assert.Equal("CacheWriteDeferred", ready.CachePersistenceErrorCode);
            Assert.True(ready.Media.SourceDeclarationProcessingComplete);
            Assert.True(ready.Media.MetadataReadCoverageComplete); Assert.NotEmpty(ready.Media.MetadataReadDigests);
            Assert.Equal(selected.Identity, ready.Media.Identity);
            await new AudioMetadataVerifier(session.Snapshot).VerifyMediaAsync(ready.Media);
            Assert.Same(ready.Media, Assert.Single(session.ResolveKnownSelection([ready.Identity.Key])).Media);
            Assert.Null(ReadCheckpoint(session, ready.Identity));
            Assert.False(session.IsEnrichmentStarted); Assert.False(session.IsCatalogLoaded); Assert.Empty(session.Catalog.Media);

            gate.Release(); held = false;
            var retried = await session.PrepareSelectedMediaAsync(ready.Identity).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Same(ready.Media, retried.Media); Assert.True(retried.SelectedSourceVerified);
            Assert.Null(retried.CachePersistenceErrorCode);
            Assert.NotNull(ReadCheckpoint(session, ready.Identity));
            await diagnostics.Log.FlushAsync();
            var events = Directory.EnumerateFiles(diagnostics.Log.DirectoryPath, "*.jsonl")
                .SelectMany(File.ReadAllLines).Select(line => JsonSerializer.Deserialize<WorkflowDiagnosticEvent>(line)!)
                .Where(item => item.Operation == "Audio.SelectedPreparation").ToArray();
            Assert.Equal(2, events.Select(item => item.OperationId).Distinct().Count());
            Assert.Equal(2, events.Count(item => item.State == "Started"));
            Assert.Equal(2, events.Count(item => item.State == "Completed"));
            Assert.DoesNotContain(events, item => item.State is "Failed" or "Stopped");
            Assert.Contains(events, item => item.Phase == "Waiting for selected media");
            Assert.Contains(events, item => item.Phase == "Reading selected source");
            Assert.Contains(events, item => item.Phase == "Verifying selected source");
            Assert.Contains(events, item => item.Phase == "Checkpoint deferred");
            Assert.Contains(events, item => item.Phase == "Saving selected checkpoint");
        }
        finally
        {
            if (held) gate.Release();
            try { await reading.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrDisposedSelectionCannotPublishWhileTheWriterIsBusy(bool disposeSession)
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new Workspace();
        var session = await OpenAsync(fixture, workspace);
        var selected = await SeedNativeMediaAsync(session);
        var gate = WriterGate(session);
        await gate.WaitAsync(); bool held = true;
        using var cancellation = new CancellationTokenSource();
        using var verified = new VerificationObserver(session.Snapshot.PhysicalSourceStamp, block: true);
        var reading = Task.Run(() => session.PrepareSelectedMediaAsync(selected.Identity!, cancellation.Token));
        Task? disposing = null;
        try
        {
            await verified.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (disposeSession)
            {
                disposing = session.DisposeAsync().AsTask();
                Assert.False(disposing.IsCompleted);
            }
            else cancellation.Cancel();
            verified.Release();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
            gate.Release(); held = false;
            if (disposing is not null) await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            else
            {
                Assert.Null(ReadCheckpoint(session, selected.Identity!));
                Assert.All(session.ResolveKnownSelection([selected.Identity!.Key]), row => Assert.False(row.SelectedSourceVerified));
                Assert.True((await session.PrepareSelectedMediaAsync(selected.Identity!)).SelectedSourceVerified);
            }
        }
        finally
        {
            verified.Release(); cancellation.Cancel();
            if (held) gate.Release();
            try { await reading.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            await (disposing ?? session.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<MediaEntry> SeedNativeMediaAsync(AudioExplorerSession session)
    {
        var media = Assert.Single((await new DetroitAudioSnapshotReader().ReadSnapshotAsync(session.Snapshot)).Catalog.Media,
            item => !string.IsNullOrEmpty(item.BankKey));
        var descriptor = new AssetDescriptor(media.Identity!, new(session.Snapshot.IndexContentId, session.Snapshot.PhysicalSourceStamp),
            AudioExplorerSession.AssetFamily, "Retained selection", media.BankKey, AssetAvailability.Available,
            AssetCompleteness.Complete, AssetCapabilities.None, [], [])
            { Metadata = new Dictionary<string, string> { ["audio.source-label"] = "Retained selection" } };
        using var features = session.AcquireSharedFeatureStore(); features.Store.UpsertAssets([descriptor]);
        return media;
    }

    private static JsonObject? ReadCheckpoint(AudioExplorerSession session, ResourceIdentity identity)
    {
        var version = (string)typeof(AudioExplorerSession).GetField("RecoveryVersion", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        using var features = session.AcquireSharedFeatureStore();
        return features.Store.LoadBounded<JsonObject>("audio.recovery/item/" + identity.Key, version, 4 * 1024 * 1024);
    }

    private static SemaphoreSlim WriterGate(AudioExplorerSession session) => (SemaphoreSlim)typeof(AudioExplorerSession)
        .GetField("featureGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
    private static Task<AudioExplorerSession> OpenAsync(FormatsFixtureBuilder fixture, Workspace workspace) =>
        AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases,
            new AudioToolPaths(null, null, null, null, null, null, null), null, CancellationToken.None, startEnrichment: false);

    private sealed class VerificationObserver : IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string stamp;
        private readonly bool block;
        private readonly IDisposable subscription;
        private readonly ManualResetEventSlim release = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal VerificationObserver(string stamp, bool block = false)
        { this.stamp = stamp; this.block = block; subscription = AssetLoadDiagnostics.Listener.Subscribe(this); }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is not AssetLoadMeasurement { Stage: "AudioRecoveryVerification" } measurement || measurement.Key != stamp) return;
            Entered.TrySetResult();
            if (block && !release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Selected verification barrier expired.");
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        internal void Release() => release.Set();
        public void Dispose() { subscription.Dispose(); release.Set(); release.Dispose(); }
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "Deviant-checkpoint-contention-tests", Guid.NewGuid().ToString("N"));
        internal CacheManager Cache { get; }
        internal AliasStore Aliases { get; }
        internal string DiagnosticRoot => Path.Combine(root, "diagnostics");
        internal Workspace() { Cache = new(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"))); Aliases = new(Path.Combine(root, "library")); }
        public void Dispose() { Cache.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class DiagnosticScope : IAsyncDisposable
    {
        private readonly LocalDiagnosticLog? previous = WorkflowDiagnostics.Current;
        internal LocalDiagnosticLog Log { get; }
        internal DiagnosticScope(string path) { Log = new(path, "generated-build"); WorkflowDiagnostics.Current = Log; }
        public async ValueTask DisposeAsync() { WorkflowDiagnostics.Current = previous; await Log.DisposeAsync(); }
    }
}

[CollectionDefinition("Selected checkpoint diagnostics", DisableParallelization = true)]
public sealed class SelectedCheckpointDiagnosticsCollection;
