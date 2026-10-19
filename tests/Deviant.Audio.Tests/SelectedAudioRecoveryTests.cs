using System.Reflection;
using System.Collections.Concurrent;
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

public sealed class SelectedAudioRecoveryTests
{
    private static readonly AudioToolPaths NoTools = new(null, null, null, null, null, null, null);

    [Fact]
    public async Task LegacyAndCorruptDetailsRemainVisibleWithoutPlayableCapabilities()
    {
        using var fixture = FormatsFixtureBuilder.CreateDialogueMediaCatalog(3);
        using var workspace = new Workspace();
        await using var session = await OpenAsync(fixture, workspace);
        var descriptors = session.Snapshot.Records.Select(row => Descriptor(session.Snapshot,
            new(session.Snapshot.IndexContentId, row.Ordinal, "wem"), $"Retained name {row.Ordinal}")).ToArray();
        using (var store = FeatureStore.Open(session.Snapshot, workspace.Cache.Location))
        {
            store.UpsertAssets(descriptors);
            store.SaveBounded("audio.recovery/item/" + descriptors[1].Identity.Key, RecoveryVersion,
                new { Corrupt = true }, 4096);
        }

        var page = await session.QueryAsync(new(Limit: 10));

        Assert.Equal(3, page.Total);
        Assert.Equal(descriptors.Select(row => row.Label).Order(), page.Rows.Select(row => row.Label).Order());
        Assert.All(page.Rows, row =>
        {
            Assert.False(row.IsHydrated); Assert.Null(row.SourceMedia);
            Assert.Equal(AudioMediaHydrationState.SourceDetailsRequired, row.HydrationState);
            Assert.False(row.SelectedSourceVerified);
            Assert.False(row.CanPreview); Assert.False(row.CanPreviewPartial);
            Assert.False(row.CanConvert); Assert.False(row.CanExportOriginal);
            Assert.Equal("AudioSourceDetailsRequired", Assert.Throws<ArchiveException>(() => row.Media).Code);
        });
        Assert.False(session.IsEnrichmentStarted); Assert.False(session.IsCatalogLoaded);
        Assert.Empty(session.Catalog.Media);
    }

    [Fact]
    public async Task RecoveryPagePublishesMetadataWithoutReadingSourceDigests()
    {
        using var fixture = FormatsFixtureBuilder.CreateDialogueMediaCatalog(1);
        using var workspace = new Workspace();
        await using var session = await OpenAsync(fixture, workspace);
        var media = Assert.Single((await new DetroitAudioSnapshotReader().ReadSnapshotAsync(session.Snapshot)).Catalog.Media);
        SavePair(session.Snapshot, workspace, media, "Retained source name");
        using var measurements = new Measurements();

        var row = Assert.Single((await session.QueryAsync(new(Limit: 10))).Rows);

        Assert.True(row.IsHydrated); Assert.Same(row.Media, row.SourceMedia);
        Assert.False(row.SelectedSourceVerified);
        Assert.DoesNotContain(measurements.Items, measurement => measurement.Stage == "AudioRecoveryVerification" &&
            measurement.Key == session.Snapshot.PhysicalSourceStamp);
        Assert.False(session.IsEnrichmentStarted);
    }

    [Fact]
    public async Task ExactLegacyBankSelectionHydratesAndPersistsWithoutCompletingCatalog()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new Workspace();
        ResourceIdentity identity;
        await using (var session = await OpenAsync(fixture, workspace))
        {
            var container = session.Snapshot.Records.Single(row => row.Type == 29);
            var bank = session.Snapshot.Records.Single(row => row.Type == 1022);
            identity = new(session.Snapshot.IndexContentId, container.Ordinal, "media/8A7B6C5D/0");
            var descriptor = Descriptor(session.Snapshot, identity, "Historical clip name",
                session.Snapshot.IndexContentId + ":resource:" + bank.Ordinal);
            using (var store = FeatureStore.Open(session.Snapshot, workspace.Cache.Location)) store.UpsertAssets([descriptor]);
            await workspace.Aliases.SetAsync(identity, "My clip");

            var prepared = await session.PrepareSelectedMediaAsync(identity);

            Assert.Equal(identity, prepared.Identity); Assert.True(prepared.IsHydrated);
            Assert.True(prepared.SelectedSourceVerified); Assert.Equal("My clip", prepared.Label);
            Assert.Equal("Historical clip name", prepared.Descriptor.Metadata["audio.source-label"]);
            Assert.True(prepared.Media.SourceDeclarationProcessingComplete);
            Assert.True(prepared.Media.MetadataReadCoverageComplete);
            await new AudioMetadataVerifier(session.Snapshot).VerifyMediaAsync(prepared.Media);
            Assert.False(session.IsEnrichmentStarted); Assert.False(session.IsEnrichmentComplete);
            Assert.False(session.IsCatalogLoaded); Assert.Empty(session.Catalog.Media);
        }
        await using var reopened = await OpenAsync(fixture, workspace);
        var retained = Assert.Single((await reopened.QueryAsync(new(Limit: 10))).Rows);
        Assert.Equal(identity, retained.Identity); Assert.True(retained.IsHydrated);
        Assert.Equal("My clip", retained.Label); Assert.False(retained.SelectedSourceVerified);
        Assert.False(reopened.IsEnrichmentStarted);
    }

    [Fact]
    public async Task SelectedKeysPreserveOrderDeduplicateAndRejectUnknownOrStaleIdentities()
    {
        using var fixture = FormatsFixtureBuilder.CreateDialogueMediaCatalog(2);
        using var workspace = new Workspace();
        await using var session = await OpenAsync(fixture, workspace);
        var identities = session.Snapshot.Records.Select(row => new ResourceIdentity(session.Snapshot.IndexContentId, row.Ordinal, "wem")).ToArray();
        using (var store = FeatureStore.Open(session.Snapshot, workspace.Cache.Location))
            store.UpsertAssets(identities.Select((identity, index) => Descriptor(session.Snapshot, identity, "Retained " + index)).ToArray());

        var rows = await session.PrepareSelectedMediaAsync(new[] { identities[1].Key, identities[0].Key, identities[1].Key });

        Assert.Equal(new[] { identities[1], identities[0] }, rows.Select(row => row.Identity));
        Assert.All(rows, row => Assert.True(row.SelectedSourceVerified));
        Assert.False(session.IsEnrichmentStarted); Assert.Empty(session.Catalog.Media);
        Assert.Equal("MissingMedia", (await Assert.ThrowsAsync<ArchiveException>(() => session.PrepareSelectedMediaAsync(
            new[] { new ResourceIdentity(session.Snapshot.IndexContentId, 100, "wem").Key }))).Code);
        Assert.Equal("InvalidIdentity", (await Assert.ThrowsAsync<ArchiveException>(() => session.PrepareSelectedMediaAsync(
            new[] { new ResourceIdentity("other", 0, "wem").Key }))).Code);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.PrepareSelectedMediaAsync(
            Enumerable.Range(0, 129).Select(index => new ResourceIdentity(session.Snapshot.IndexContentId, index, "wem").Key)));
    }

    [Fact]
    public async Task ChangedSelectedSourceDoesNotPublishOrEraseLegacyDescriptor()
    {
        using var fixture = FormatsFixtureBuilder.CreateDialogueMediaCatalog(1);
        using var workspace = new Workspace();
        await using var session = await OpenAsync(fixture, workspace);
        var identity = new ResourceIdentity(session.Snapshot.IndexContentId, 0, "wem");
        var descriptor = Descriptor(session.Snapshot, identity, "Retained name");
        using (var store = FeatureStore.Open(session.Snapshot, workspace.Cache.Location)) store.UpsertAssets([descriptor]);
        var path = session.Snapshot.Volumes.Single().FilePath;
        var bytes = await File.ReadAllBytesAsync(path); bytes[0] ^= 1; await File.WriteAllBytesAsync(path, bytes);

        Assert.Equal("SourceChanged", (await Assert.ThrowsAsync<ArchiveException>(() => session.PrepareSelectedMediaAsync(identity))).Code);

        using var retained = FeatureStore.Open(session.Snapshot, workspace.Cache.Location);
        Assert.Equal(descriptor.Label, Assert.Single(retained.Query(new(Family: AudioExplorerSession.AssetFamily))).Label);
        Assert.All(session.ResolveKnownSelection([identity.Key]), row => Assert.False(row.SelectedSourceVerified));
        Assert.False(session.IsEnrichmentStarted);
    }

    [Fact]
    public async Task ExactSelectionDoesNotResolveAnotherRowsLookupAlias()
    {
        using var fixture = FormatsFixtureBuilder.CreateDialogueMediaCatalog(2);
        using var workspace = new Workspace();
        await using var session = await OpenAsync(fixture, workspace);
        var wanted = new ResourceIdentity(session.Snapshot.IndexContentId, 0, "wem");
        var other = new ResourceIdentity(session.Snapshot.IndexContentId, 1, "wem");
        using (var store = FeatureStore.Open(session.Snapshot, workspace.Cache.Location))
            store.UpsertAssets([Descriptor(session.Snapshot, wanted, "Wanted"),
                Descriptor(session.Snapshot, other, "Other") with { LookupKey = wanted.Key }]);

        var selected = await session.PrepareSelectedMediaAsync(wanted);

        Assert.Equal(wanted, selected.Identity);
        Assert.Equal("Wanted", selected.Descriptor.Metadata["audio.source-label"]);
        Assert.Equal("Generated_Line_00000_ENG", selected.Media.Name);
    }

    [Fact]
    public async Task SourceHydrationPreservesNewerPairedIndexingResults()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new Workspace();
        await using var session = await OpenAsync(fixture, workspace);
        var latest = Assert.Single((await new DetroitAudioSnapshotReader().ReadSnapshotAsync(session.Snapshot)).Catalog.Media,
            media => !string.IsNullOrEmpty(media.BankKey));
        var old = Descriptor(session.Snapshot, latest.Identity!, "Earlier name", latest.BankKey);
        using (var store = FeatureStore.Open(session.Snapshot, workspace.Cache.Location)) store.UpsertAssets([old]);
        using var barrier = new VerificationBarrier(session.Snapshot.PhysicalSourceStamp);
        var reading = Task.Run(() => session.PrepareSelectedMediaAsync(latest.Identity!));
        try
        {
            var entered = await Task.WhenAny(barrier.Entered.Task, reading).WaitAsync(TimeSpan.FromSeconds(10));
            if (ReferenceEquals(entered, reading)) await reading;
            SavePair(session.Snapshot, workspace, latest, "Later indexed name");
            barrier.Release();
            var prepared = await reading.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("Later indexed name", prepared.Descriptor.Metadata["audio.source-label"]);
            Assert.Equal(latest.EventKeys, prepared.Media.EventKeys);
            Assert.Equal(latest.SourceEvidence, prepared.Media.SourceEvidence);
            using var retained = FeatureStore.Open(session.Snapshot, workspace.Cache.Location);
            Assert.Equal("Later indexed name", Assert.Single(retained.Query(new(Family: AudioExplorerSession.AssetFamily))).Label);
            Assert.False(session.IsEnrichmentStarted);
        }
        finally { barrier.Release(); try { await reading.WaitAsync(TimeSpan.FromSeconds(10)); } catch { } }
    }

    [Fact]
    public async Task VerifiedSelectedMediaRemainsUsableWhenOptionalCheckpointCannotGrow()
    {
        using var fixture = FormatsFixtureBuilder.CreateEmpty();
        using var workspace = new Workspace();
        const uint mediaId = 41;
        const string bankName = "generated_admission_bank";
        var sources = Enumerable.Repeat((mediaId, (byte)0, 0u, 0x00040001u), 1000).ToArray();
        fixture.AddBankFixture(1, bankName, FormatsFixtureBuilder.MakeBankForSources(FormatsFixtureBuilder.Fnv1(bankName),
            [(mediaId, FormatsFixtureBuilder.MakeWem([1, 2, 3, 4]))], sources));
        fixture.WriteIndex();
        await using var session = await OpenAsync(fixture, workspace);
        var owner = session.Snapshot.Records.Single(row => row.Type == 29);
        var bank = session.Snapshot.Records.Single(row => row.Type == 1022);
        var identity = new ResourceIdentity(session.Snapshot.IndexContentId, owner.Ordinal, $"media/{mediaId:X8}/0");
        var descriptor = Descriptor(session.Snapshot, identity, "Retained clip",
            session.Snapshot.IndexContentId + ":resource:" + bank.Ordinal);
        using var retained = FeatureStore.Open(session.Snapshot, workspace.Cache.Location);
        retained.UpsertAssets([descriptor]);
        await workspace.Cache.ReconcileAsync();
        await workspace.Cache.SetLimitAsync(workspace.Cache.UsageBytes + 16 * 1024);

        var selected = await session.PrepareSelectedMediaAsync(identity);

        Assert.True(selected.IsHydrated); Assert.True(selected.SelectedSourceVerified);
        Assert.True(selected.CanExportOriginal); Assert.Equal(1000, selected.Media.SourceEvidence.Count);
        Assert.Null(selected.HydrationErrorCode);
        Assert.Equal("CacheStorageLimit", selected.CachePersistenceErrorCode);
        Assert.True(Assert.Single(session.ResolveKnownSelection([identity.Key])).SelectedSourceVerified);
        Assert.False(Assert.Single((await session.QueryAsync(new(Limit:10))).Rows).IsHydrated);
        var repeated = await session.PrepareSelectedMediaAsync(identity);
        Assert.Same(selected.Media, repeated.Media);
        Assert.True(repeated.SelectedSourceVerified); Assert.True(repeated.CanExportOriginal);
        Assert.Equal("CacheStorageLimit", repeated.CachePersistenceErrorCode);
        Assert.Null(retained.LoadBounded<JsonObject>("audio.recovery/item/" + identity.Key, RecoveryVersion, 4 * 1024 * 1024));
        var saved = Assert.Single(retained.Query(new(Family: AudioExplorerSession.AssetFamily)));
        Assert.Equal(descriptor.Label, saved.Label); Assert.Equal(descriptor.Metadata["audio.source-label"], saved.Metadata["audio.source-label"]);
        await workspace.Cache.ReconcileAsync();
        Assert.True(workspace.Cache.UsageBytes <= workspace.Cache.LimitBytes);
        Assert.False(session.IsEnrichmentStarted); Assert.False(session.IsEnrichmentComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedVerificationCancellationAndDisposalDrainBeforePublishing(bool disposeSession)
    {
        using var fixture = FormatsFixtureBuilder.CreateDialogueMediaCatalog(1);
        using var workspace = new Workspace();
        var session = await OpenAsync(fixture, workspace);
        var media = Assert.Single((await new DetroitAudioSnapshotReader().ReadSnapshotAsync(session.Snapshot)).Catalog.Media);
        SavePair(session.Snapshot, workspace, media, "Retained source name");
        using var barrier = new VerificationBarrier(session.Snapshot.PhysicalSourceStamp);
        using var cancellation = new CancellationTokenSource();
        var reading = Task.Run(() => session.PrepareSelectedMediaAsync(media.Identity!, cancellation.Token));
        Task? disposing = null;
        try
        {
            var entered = await Task.WhenAny(barrier.Entered.Task, reading).WaitAsync(TimeSpan.FromSeconds(10));
            if (ReferenceEquals(entered, reading)) await reading;
            if (OperatingSystem.IsWindows())
                Assert.Throws<IOException>(() =>
                {
                    using var blocked = new FileStream(session.Snapshot.Volumes.Single().FilePath,
                        FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                });
            if (disposeSession)
            {
                disposing = session.DisposeAsync().AsTask();
                Assert.False(disposing.IsCompleted);
            }
            else cancellation.Cancel();
            barrier.Release();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading).WaitAsync(TimeSpan.FromSeconds(10));
            if (disposing is not null) await disposing.WaitAsync(TimeSpan.FromSeconds(10));
            else Assert.All(session.ResolveKnownSelection([media.Identity!.Key]), row => Assert.False(row.SelectedSourceVerified));
            if (OperatingSystem.IsWindows())
            {
                using var exclusive = new FileStream(session.Snapshot.Volumes.Single().FilePath,
                    FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        }
        finally
        {
            barrier.Release();
            try { await reading.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            await (disposing ?? session.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static Task<AudioExplorerSession> OpenAsync(FormatsFixtureBuilder fixture, Workspace workspace) =>
        AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, workspace.Cache, workspace.Aliases, NoTools, null,
            CancellationToken.None, startEnrichment: false);

    private static AssetDescriptor Descriptor(ArchiveSnapshot snapshot, ResourceIdentity identity, string label, string? bank = null) =>
        new(identity, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), AudioExplorerSession.AssetFamily, label,
            bank ?? "__deviant_standalone__", AssetAvailability.Available, AssetCompleteness.Complete,
            AssetCapabilities.Preview | AssetCapabilities.Original | AssetCapabilities.Wav, [], [])
        { Metadata = new Dictionary<string, string> { ["audio.source-label"] = label } };

    private static string RecoveryVersion => (string)typeof(AudioExplorerSession)
        .GetField("RecoveryVersion", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;

    private static void SavePair(ArchiveSnapshot snapshot, Workspace workspace, MediaEntry media, string label)
    {
        var descriptor = Descriptor(snapshot, media.Identity!, label, string.IsNullOrWhiteSpace(media.BankKey) ? null : media.BankKey);
        var payload = new { Revision = descriptor.Revision, Media = media, SourceLabel = label };
        using var store = FeatureStore.Open(snapshot, workspace.Cache.Location);
        Assert.Single(store.PublishBoundedAssetsBatch([new(BoundedFeatureWrite.Create("audio.recovery/item/" + media.Identity!.Key,
            RecoveryVersion, payload, 4 * 1024 * 1024), descriptor)]));
    }

    private sealed class Measurements : IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly IDisposable subscription;
        public ConcurrentQueue<AssetLoadMeasurement> Items { get; } = new();
        public Measurements() => subscription = AssetLoadDiagnostics.Listener.Subscribe(this);
        public void OnNext(KeyValuePair<string, object?> value) { if (value.Value is AssetLoadMeasurement measurement) Items.Enqueue(measurement); }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose() => subscription.Dispose();
    }

    private sealed class VerificationBarrier : IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string stamp;
        private readonly ManualResetEventSlim release = new();
        private readonly IDisposable subscription;
        private int held;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public VerificationBarrier(string stamp)
        { this.stamp = stamp; subscription = AssetLoadDiagnostics.Listener.Subscribe(this); }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is not AssetLoadMeasurement { Stage: "AudioRecoveryVerification", Key: var key } ||
                key != stamp || Interlocked.Exchange(ref held, 1) != 0) return;
            Entered.TrySetResult(); release.Wait();
        }
        public void Release() => release.Set();
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose() { subscription.Dispose(); release.Set(); release.Dispose(); }
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-selected-audio-" + Guid.NewGuid().ToString("N"));
        public CacheManager Cache { get; }
        public AliasStore Aliases { get; }
        public Workspace()
        { Cache = new(Path.Combine(root, "settings"), new CacheSettings(Path.Combine(root, "cache"))); Aliases = new(Path.Combine(root, "library")); }
        public void Dispose() { Cache.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
