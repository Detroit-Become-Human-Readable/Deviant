using System.Reflection;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureSummaryProjectionTests
{
    [Fact]
    public async Task PreparedSummaryPersistsAndTracksSharedMetadataAndAliasChanges()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        const string profile = "prepared-summary";
        var first = fixture.Named(fixture.Entry("Texture A"), 32, 16, "BC1", "Diffuse", "Room");
        var second = fixture.Named(fixture.Entry("Texture B", 2), 64, 32, "BC3", "Normal", "Hall");
        var shared = new ResourceIdentity(fixture.Snapshot.IndexContentId, 5, "generated/0");
        var unique = new ResourceIdentity(fixture.Snapshot.IndexContentId, 5, "generated/1");
        first = first with { MetadataIdentities = [shared, unique] };
        second = second with { MetadataIdentities = [shared] };
        using (var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache))
        {
            store.InitializeTextureProjections(profile, [fixture.Entry("Texture A"), fixture.Entry("Texture B", 2)]);
            store.CommitTextureProjectionOwner(profile, fixture.Checkpoint, [first, second]);
            await Prepare(store, profile);
        }
        using var reopened = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        int prepared = 0;
        using var observer = AssetLoadDiagnostics.Listener.Subscribe(new Observer(value =>
        {
            if (value.Stage == "TextureBrowser.Store.PreparedSummaryRead" && value.Key == profile)
                Interlocked.Increment(ref prepared);
        }));
        using var oldPin = reopened.AcquireTextureBrowseReadLease(profile, fixture.Revision);
        var old = await oldPin.ReadAsync(new());
        Assert.Equal(1, Volatile.Read(ref prepared));
        Assert.Equal(2, old.Coverage.Resources); Assert.Equal(2, old.Coverage.MetadataRecords);
        Assert.Equal(new[] { "Diffuse", "Normal" }, old.Options.VerifiedRoles);
        using (var writer = FeatureStore.Open(fixture.Snapshot, fixture.Cache))
        {
            writer.SetTextureProjectionAlias(profile, first.Identity, "Panel alias");
            writer.CommitTextureProjectionOwner(profile, fixture.Checkpoint,
                [first with { MetadataIdentities = [], Width = 128, Height = 64, Format = "BGRA8", Owners = [] }]);
        }
        using var current = reopened.AcquireTextureBrowseReadLease(profile, fixture.Revision);
        var newer = await current.ReadAsync(new());
        Assert.Equal(2, Volatile.Read(ref prepared));
        Assert.Equal(1, newer.Coverage.MetadataRecords); Assert.Equal(1, newer.Coverage.Aliases);
        Assert.Equal(1, newer.Coverage.TexturesWithMetadata); Assert.Equal(new[] { "Normal" }, newer.Options.VerifiedRoles);
        Assert.Equal(new[] { "BC3", "BGRA8" }, newer.Options.Formats);
        Assert.Contains(new TextureDimensionOption(128, 64), newer.Options.Dimensions);
        Assert.Equal(old.Coverage, (await oldPin.ReadAsync(new())).Coverage);
    }

    [Fact]
    public async Task InterruptedPreparationKeepsBoundedCheckpointsAndNeverPublishesPartialCounts()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        const string profile = "interrupted-summary";
        var entries = Enumerable.Range(1, 300).Select(index => fixture.Entry("Generated texture " + index, index)).ToArray();
        using (var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache))
        {
            store.InitializeTextureProjections(profile, entries);
            // Simulate rows indexed before summary invalidation triggers existed.
            using (var db = Connection(fixture))
            using (var command = db.CreateCommand())
            { command.CommandText = "DELETE FROM texture_browse_summary_dirty"; command.ExecuteNonQuery(); }
            using var cancellation = new CancellationTokenSource();
            using var observer = AssetLoadDiagnostics.Listener.Subscribe(new Observer(value =>
            {
                if (value.Stage == "TextureBrowser.Store.SummaryBackfillBatch" && value.Key == profile)
                {
                    Assert.Equal(0, value.Values["Rows"]); Assert.Equal(128, value.Values["Seeded"]); cancellation.Cancel();
                }
            }));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Prepare(store, profile, cancellation.Token));
        }
        Assert.Equal(0, Scalar(fixture, "SELECT COUNT(*) FROM texture_browse_summary_members WHERE facet=0 AND key_value='resources'"));
        Assert.Equal(128, Scalar(fixture, "SELECT COUNT(*) FROM texture_browse_summary_dirty"));
        using (var reopened = FeatureStore.Open(fixture.Snapshot, fixture.Cache))
        {
            int prepared = 0;
            using var observer = AssetLoadDiagnostics.Listener.Subscribe(new Observer(value =>
            { if (value.Stage == "TextureBrowser.Store.PreparedSummaryRead" && value.Key == profile) prepared++; }));
            using var read = reopened.AcquireTextureBrowseReadLease(profile, fixture.Revision);
            Assert.Equal(300, (await read.ReadAsync(new())).Coverage.Resources); Assert.Equal(0, prepared);
            using (var writer = FeatureStore.Open(fixture.Snapshot, fixture.Cache))
                writer.CommitTextureProjectionOwner(profile, fixture.Checkpoint,
                    [fixture.Named(entries[0], 32, 16, "BC1", "Diffuse", "Room")]);
            await Prepare(reopened, profile);
        }
        Assert.Equal(0, Scalar(fixture, "SELECT COUNT(*) FROM texture_browse_summary_dirty"));
        using var final = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        using var completed = final.AcquireTextureBrowseReadLease(profile, fixture.Revision);
        var response = await completed.ReadAsync(new());
        Assert.Equal(300, response.Coverage.Resources); Assert.Equal(1, response.Coverage.OwnerNames);
        Assert.Equal(new[] { "BC1" }, response.Options.Formats);
    }

    [Fact]
    public async Task LargeOwnerUpdateUsesTheExactSnapshotUntilAllDirtyBatchesComplete()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        const string profile = "large-summary";
        var entries = Enumerable.Range(1, 256).Select(index => fixture.Entry("Generated texture " + index, index)).ToArray();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        store.InitializeTextureProjections(profile, entries); await Prepare(store, profile);
        long generation = store.CommitTextureProjectionOwner(profile, fixture.Checkpoint,
            entries.Select(entry => fixture.Named(entry, 32, 16, "BC1", "Diffuse", "Room")).ToArray());
        Assert.Equal(128, Scalar(fixture, "SELECT COUNT(*) FROM texture_browse_summary_dirty"));
        int prepared = 0;
        using var observer = AssetLoadDiagnostics.Listener.Subscribe(new Observer(value =>
        { if (value.Stage == "TextureBrowser.Store.PreparedSummaryRead" && value.Key == profile) prepared++; }));
        using var read = store.AcquireTextureBrowseReadLease(profile, fixture.Revision);
        var response = await read.ReadAsync(new());
        Assert.Equal(generation, response.Token.Generation); Assert.Equal(256, response.Coverage.OwnerNames);
        Assert.Equal(new[] { "Diffuse" }, response.Options.VerifiedRoles); Assert.Equal(0, prepared);
        await Prepare(store, profile);
        using var reopened = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        using var final = reopened.AcquireTextureBrowseReadLease(profile, fixture.Revision);
        Assert.Equal(response.Coverage, (await final.ReadAsync(new())).Coverage); Assert.Equal(1, prepared);
    }

    [Fact]
    public async Task UnknownSummaryVersionFailsPreparationWithoutLoopingOrPublishingIt()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        store.InitializeTextureProjections("future-summary", [fixture.Entry("Generated texture")]);
        await Prepare(store, "future-summary");
        using (var db = Connection(fixture))
        using (var command = db.CreateCommand())
        {
            command.CommandText = "UPDATE texture_browse_summary_state SET version=99; INSERT INTO texture_browse_summary_dirty " +
                "SELECT revision,profile,identity FROM texture_projection_rows"; command.ExecuteNonQuery();
        }
        using var cancellation = new CancellationTokenSource();
        var work = Prepare(store, "future-summary", cancellation.Token);
        try
        {
            var error = await Assert.ThrowsAsync<ArchiveException>(() => work.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("TextureSummaryVersionUnsupported", error.Code);
        }
        finally
        {
            cancellation.Cancel();
            try { await work; } catch (Exception) { }
        }
    }

    private static SqliteConnection Connection(GeneratedTextureProjectionFixture fixture)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Directory.GetFiles(fixture.Cache, "catalog-*.sqlite3").Single(), Pooling = false }.ToString());
        db.Open(); return db;
    }
    private static long Scalar(GeneratedTextureProjectionFixture fixture, string sql)
    { using var db = Connection(fixture); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }

    private static Task Prepare(FeatureStore store, string profile, CancellationToken token = default)
    {
        var method = typeof(FeatureStore).GetMethod("PrepareTextureBrowseSummaryAsync");
        Assert.NotNull(method);
        return (Task)method.Invoke(store, [profile, token])!;
    }

    private sealed class Observer(Action<AssetLoadCounterMeasurement> observe) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> item)
        { if (item.Value is AssetLoadCounterMeasurement value) observe(value); }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
