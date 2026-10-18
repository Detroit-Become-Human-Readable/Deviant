using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Deviant.Texture.Tests;

public sealed class TextureHistoryAppendTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(32, false)]
    [InlineData(128, false)]
    [InlineData(32, true)]
    [InlineData(128, true)]
    public async Task OneNewEventPreservesPublishedReadsWithBoundedHistoryWrites(int versions, bool aliasOnly)
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        const string profile = "history-append";
        var target = fixture.Entry("Target texture");
        var unrelated = fixture.Entry("Unrelated texture", 2);
        Populate(fixture, store, profile, target, unrelated, versions);
        store.SetTextureProjectionAlias(profile, target.Identity, "Retained alias");
        long previous = store.CaptureTextureProjectionGeneration(profile);
        using var pin = store.AcquireTextureBrowseReadLease(profile, fixture.Revision, previous);
        var beforeRead = await pin.ReadAsync(new(Limit: 10));
        using var db = Open(fixture);
        InstallAudit(db);
        var before = Stored(fixture, db);
        var measurements = new ConcurrentQueue<AssetLoadMeasurement>();
        using var observer = AssetLoadDiagnostics.Listener.Subscribe(new MeasurementObserver(measurements, Environment.CurrentManagedThreadId));
        var watch = Stopwatch.StartNew();
        if (aliasOnly) store.SetTextureProjectionAlias(profile, target.Identity, "Updated alias");
        else store.CommitTextureProjectionOwner(profile, fixture.Checkpoint,
            [fixture.Named(fixture.Entry("Current target"), 2048, 1024, "BC5", "Specular", "Updated context")]);
        watch.Stop();
        long current = store.CaptureTextureProjectionGeneration(profile);
        var after = Stored(fixture, db);
        var writes = ReadAudit(db, target.Identity);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            Versions = versions, Event = aliasOnly ? "alias" : "row", Before = before, After = after,
            Writes = writes, PublicationMilliseconds = watch.Elapsed.TotalMilliseconds,
            Measurements = measurements.Where(value => value.Stage is "TextureBrowser.Store.HistoryIndex" or
                "TextureBrowser.Store.OwnerCommit" or "TextureBrowser.Store.OwnerTransaction")
                .Select(value => new { value.Stage, value.ElapsedMilliseconds }).ToArray()
        }));

        Assert.True(current > previous);
        AssertReadEqual(beforeRead, await pin.ReadAsync(new(Limit: 10)));
        using var latest = store.AcquireTextureBrowseReadLease(profile, fixture.Revision, current);
        var currentRead = await latest.ReadAsync(new(Limit: 10));
        Assert.Equal(2, currentRead.Page.Total);
        var oldTarget = Assert.Single(beforeRead.Page.Entries, entry => entry.Identity == target.Identity);
        var newTarget = Assert.Single(currentRead.Page.Entries, entry => entry.Identity == target.Identity);
        Assert.Equal("Retained alias", oldTarget.Alias);
        Assert.Equal(aliasOnly ? "Updated alias" : "Retained alias", newTarget.Alias);
        Assert.Equal(aliasOnly ? oldTarget.Width : 2048, newTarget.Width);
        Assert.Equal(aliasOnly ? oldTarget.Label : "Current target", newTarget.Label);
        Assert.Equal(EntryView(Assert.Single(beforeRead.Page.Entries, entry => entry.Identity == unrelated.Identity)),
            EntryView(Assert.Single(currentRead.Page.Entries, entry => entry.Identity == unrelated.Identity)));
        Assert.Empty((await pin.ReadAsync(new(Search: "Updated alias", Limit: 10))).Page.Entries);
        Assert.Equal(target.Identity, Assert.Single((await latest.ReadAsync(new(
            Search: aliasOnly ? "Updated alias" : "Retained alias", Limit: 10))).Page.Entries).Identity);
        Assert.Equal(beforeRead.Options.Formats, store.TextureProjectionFilters(profile, previous).Formats);
        Assert.Equal(beforeRead.Options.Dimensions, store.TextureProjectionFilters(profile, previous).Dimensions);
        if (!aliasOnly)
        {
            Assert.Contains("BC5", currentRead.Options.Formats);
            Assert.DoesNotContain("BC1", currentRead.Options.Formats);
            Assert.Contains("Specular", currentRead.Options.VerifiedRoles);
            Assert.DoesNotContain("Diffuse", currentRead.Options.VerifiedRoles);
        }
        Assert.Equal(fixture.Checkpoint, store.LoadTextureProjectionOwner(profile, fixture.Owner));
        Assert.NotNull(store.LoadTextureProjectionDetails(profile, previous, target.Identity));
        Assert.NotNull(store.LoadTextureProjectionDetails(profile, current, target.Identity));
        using (var reopened = FeatureStore.Open(fixture.Snapshot, fixture.Cache))
        {
            using var reopenedOld = reopened.AcquireTextureBrowseReadLease(profile, fixture.Revision, previous);
            using var reopenedNew = reopened.AcquireTextureBrowseReadLease(profile, fixture.Revision, current);
            AssertReadEqual(beforeRead, await reopenedOld.ReadAsync(new(Limit: 10)));
            AssertReadEqual(currentRead, await reopenedNew.ReadAsync(new(Limit: 10)));
            Assert.Equal(fixture.Checkpoint, reopened.LoadTextureProjectionOwner(profile, fixture.Owner));
        }
        Assert.Equal(before.HistoryRows + 1, after.HistoryRows);
        if (aliasOnly)
        {
            Assert.Equal(before.Rows, after.Rows);
            Assert.Equal(before.RowThinBytes, after.RowThinBytes);
            Assert.Equal(before.EvidenceBytes, after.EvidenceBytes);
            Assert.Equal(before.ProofBytes, after.ProofBytes);
            Assert.Equal(before.Links, after.Links);
            Assert.Equal(before.LinkTextBytes, after.LinkTextBytes);
        }
        else
        {
            Assert.Equal(before.Rows + 1, after.Rows);
            Assert.True(after.EvidenceBytes > before.EvidenceBytes);
            Assert.True(after.ProofBytes > before.ProofBytes);
            Assert.True(after.Links > before.Links);
        }
        Assert.Equal(0, writes.Unrelated);
        Assert.Equal(0, writes.Deletes);
        Assert.InRange(writes.Inserts, 1, 2);
        Assert.InRange(writes.Updates, 0, 2);
        Assert.InRange(writes.Inserts + writes.Updates + writes.Deletes, 1, 4);
    }

    [Fact]
    public async Task APreviouslyDirtyHistoricalAliasIsReconstructedBeforePublishingTheNextRow()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        const string profile = "history-repair";
        var target = fixture.Entry("Target texture");
        var unrelated = fixture.Entry("Unrelated texture", 2);
        Populate(fixture, store, profile, target, unrelated, 32);
        store.SetTextureProjectionAlias(profile, target.Identity, "Current alias");
        using var oldPin = store.AcquireTextureBrowseReadLease(profile, fixture.Revision, 2);
        using var db = Open(fixture);
        using (var repair = db.CreateCommand())
        {
            repair.CommandText = "INSERT INTO texture_projection_alias_versions(revision,profile,identity,generation,alias) " +
                "SELECT revision,profile,identity,2,'Restored historical alias' FROM texture_projection_rows " +
                "WHERE profile=$profile AND identity=$identity AND generation=2;" +
                "INSERT OR IGNORE INTO texture_projection_history_dirty(revision,profile,identity) " +
                "SELECT revision,profile,identity FROM texture_projection_rows WHERE profile=$profile AND identity=$identity AND generation=2;";
            repair.Parameters.AddWithValue("$profile", profile);
            repair.Parameters.AddWithValue("$identity", target.Identity.Key);
            Assert.True(repair.ExecuteNonQuery() > 0);
        }
        InstallAudit(db);
        var before = Stored(fixture, db);
        long current = store.CommitTextureProjectionOwner(profile, fixture.Checkpoint,
            [fixture.Named(fixture.Entry("Rebuilt target"), 4096, 2048, "BC7", "Specular", "Rebuilt context")]);
        var after = Stored(fixture, db);
        var writes = ReadAudit(db, target.Identity);
        output.WriteLine(JsonSerializer.Serialize(new { Event = "historical-repair", Before = before, After = after, Writes = writes }));
        Assert.Equal("Restored historical alias", Assert.Single((await oldPin.ReadAsync(new(Search: "Restored historical alias")))
            .Page.Entries).Alias);
        using var latest = store.AcquireTextureBrowseReadLease(profile, fixture.Revision, current);
        var currentRead = await latest.ReadAsync(new(Limit: 10));
        Assert.Equal("Current alias", Assert.Single(currentRead.Page.Entries, entry => entry.Identity == target.Identity).Alias);
        Assert.Equal("Rebuilt target", Assert.Single(currentRead.Page.Entries, entry => entry.Identity == target.Identity).Label);
        Assert.Equal(2, currentRead.Page.Total);
        Assert.Equal(0, writes.Unrelated);
        Assert.True(writes.Deletes >= 32);
        Assert.True(writes.Inserts >= 33);
        using var reopened = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        Assert.Equal("Restored historical alias", Assert.Single(reopened.QueryTextureProjections(profile, 2,
            new(Search: "Restored historical alias")).Entries).Alias);
    }

    [Fact]
    public async Task AnInconsistentOpenIntervalUsesReconstructionBeforeTheNextPublication()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        const string profile = "history-tail-repair";
        var target = fixture.Entry("Target texture");
        Populate(fixture, store, profile, target, fixture.Entry("Unrelated texture", 2), 8);
        long previous = store.CaptureTextureProjectionGeneration(profile);
        using var oldPin = store.AcquireTextureBrowseReadLease(profile, fixture.Revision, previous);
        var before = await oldPin.ReadAsync(new(Limit: 10));
        using var db = Open(fixture);
        using (var damage = db.CreateCommand())
        {
            damage.CommandText = "UPDATE texture_projection_history_index SET next_generation=999999 " +
                "WHERE profile=$profile AND identity=$identity AND next_generation IS NULL";
            damage.Parameters.AddWithValue("$profile", profile);
            damage.Parameters.AddWithValue("$identity", target.Identity.Key);
            Assert.Equal(1, damage.ExecuteNonQuery());
        }
        InstallAudit(db);
        long current = store.CommitTextureProjectionOwner(profile, fixture.Checkpoint,
            [fixture.Named(fixture.Entry("Current target"), 2048, 1024, "BC5", "Specular", "Updated context")]);
        AssertReadEqual(before, await oldPin.ReadAsync(new(Limit: 10)));
        Assert.Equal("Current target", Assert.Single(store.QueryTextureProjections(profile, current,
            new(Search: "Current target")).Entries).Label);
        var writes = ReadAudit(db, target.Identity);
        Assert.True(writes.Deletes > 0);
        Assert.Equal(0, writes.Unrelated);
    }

    [Fact]
    public async Task CancellationAfterHistoryRefreshRollsBackIntervalsRowsEvidenceAndThePublishedHead()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        const string profile = "history-cancel";
        var target = fixture.Entry("Target texture");
        Populate(fixture, store, profile, target, fixture.Entry("Unrelated texture", 2), 4);
        store.SetTextureProjectionAlias(profile, target.Identity, "Retained alias");
        long previous = store.CaptureTextureProjectionGeneration(profile);
        using var pin = store.AcquireTextureBrowseReadLease(profile, fixture.Revision, previous);
        var beforeRead = await pin.ReadAsync(new(Limit: 10));
        using var db = Open(fixture);
        InstallAudit(db);
        var before = Stored(fixture, db);
        using var cancellation = new CancellationTokenSource();
        using var observer = AssetLoadDiagnostics.Listener.Subscribe(new CancelAfterHistoryRefresh(
            Environment.CurrentManagedThreadId, cancellation));
        Assert.ThrowsAny<OperationCanceledException>(() => store.CommitTextureProjectionOwner(profile, fixture.Checkpoint,
            [fixture.Named(fixture.Entry("Cancelled target"), 2048, 1024, "BC5", "Specular", "Updated context")], cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(previous, store.CaptureTextureProjectionGeneration(profile));
        AssertReadEqual(beforeRead, await pin.ReadAsync(new(Limit: 10)));
        var after = Stored(fixture, db);
        Assert.Equal(before with { DatabaseBytes = after.DatabaseBytes, WalBytes = after.WalBytes }, after);
        Assert.Equal(new HistoryWrites(0, 0, 0, 0), ReadAudit(db, target.Identity));
        Assert.Equal(fixture.Checkpoint, store.LoadTextureProjectionOwner(profile, fixture.Owner));
        Assert.Empty(store.QueryTextureProjections(profile, previous, new(Search: "Cancelled target")).Entries);
    }
    private static void Populate(GeneratedTextureProjectionFixture fixture, FeatureStore store, string profile,
        TextureDiscoveryEntry target, TextureDiscoveryEntry unrelated, int versions)
    {
        store.InitializeTextureProjections(profile, [target, unrelated]);
        for (int version = 0; version < versions; version++)
            store.CommitTextureProjectionOwner(profile, fixture.Checkpoint,
            [
                fixture.Named(fixture.Entry("Target version " + version), 32 + version, 16, "BC1", "Diffuse", "Target context"),
                fixture.Named(fixture.Entry("Unrelated version " + version, 2), 64 + version, 32, "BC3", "Normal", "Other context")
            ]);
    }

    private static void AssertReadEqual(TextureBrowseResponse expected, TextureBrowseResponse actual)
    {
        Assert.Equal(expected.Token, actual.Token);
        Assert.Equal(expected.Page.Total, actual.Page.Total);
        Assert.Equal(expected.Page.Entries.Select(EntryView).ToArray(), actual.Page.Entries.Select(EntryView).ToArray());
        Assert.Equal(expected.Options.Dimensions, actual.Options.Dimensions);
        Assert.Equal(expected.Options.Formats, actual.Options.Formats);
        Assert.Equal(expected.Options.VerifiedRoles, actual.Options.VerifiedRoles);
        Assert.Equal(expected.Options.Contexts, actual.Options.Contexts);
        Assert.Equal(expected.Coverage, actual.Coverage);
    }

    private static string EntryView(TextureDiscoveryEntry entry) => JsonSerializer.Serialize(new
    { entry.Identity, entry.Label, entry.Alias, entry.Width, entry.Height, entry.Format, entry.NameKind, entry.Kind });

    private static SqliteConnection Open(GeneratedTextureProjectionFixture fixture)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath(fixture), Pooling = false }.ToString());
        db.Open(); return db;
    }

    private static string DatabasePath(GeneratedTextureProjectionFixture fixture) =>
        Directory.GetFiles(fixture.Cache, "catalog-*.sqlite3").Single();

    private static void InstallAudit(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE generated_history_audit(action TEXT NOT NULL,identity TEXT NOT NULL);
            CREATE TRIGGER generated_history_insert AFTER INSERT ON texture_projection_history_index BEGIN
                INSERT INTO generated_history_audit VALUES('insert',new.identity); END;
            CREATE TRIGGER generated_history_update AFTER UPDATE ON texture_projection_history_index BEGIN
                INSERT INTO generated_history_audit VALUES('update',new.identity); END;
            CREATE TRIGGER generated_history_delete AFTER DELETE ON texture_projection_history_index BEGIN
                INSERT INTO generated_history_audit VALUES('delete',old.identity); END;
            """;
        command.ExecuteNonQuery();
    }

    private static HistoryWrites ReadAudit(SqliteConnection db, ResourceIdentity target)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(action='insert'),0),COALESCE(SUM(action='update'),0)," +
            "COALESCE(SUM(action='delete'),0),COALESCE(SUM(identity<>$target),0) FROM generated_history_audit";
        command.Parameters.AddWithValue("$target", target.Key);
        using var reader = command.ExecuteReader(); Assert.True(reader.Read());
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private static Storage Stored(GeneratedTextureProjectionFixture fixture, SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM texture_projection_rows),
                (SELECT COALESCE(SUM(length(CAST(thin AS BLOB))),0) FROM texture_projection_rows),
                (SELECT COALESCE(SUM(length(payload)),0) FROM texture_projection_evidence),
                (SELECT COALESCE(SUM(length(payload)),0) FROM texture_projection_name_proofs),
                (SELECT COUNT(*) FROM texture_projection_links),
                (SELECT COALESCE(SUM(length(CAST(identity||link_key||role||label AS BLOB))),0) FROM texture_projection_links),
                (SELECT COUNT(*) FROM texture_projection_history_index),
                (SELECT COALESCE(SUM(length(CAST(identity||label||sort_key||search||COALESCE(alias,'')||effective_sort||search_fold AS BLOB))),0)
                    FROM texture_projection_history_index)
            """;
        using var reader = command.ExecuteReader(); Assert.True(reader.Read());
        string path = DatabasePath(fixture);
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
            reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), new FileInfo(path).Length,
            File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0);
    }

    private sealed record HistoryWrites(long Inserts, long Updates, long Deletes, long Unrelated);
    private sealed record Storage(long Rows, long RowThinBytes, long EvidenceBytes, long ProofBytes, long Links,
        long LinkTextBytes, long HistoryRows, long HistoryTextBytes, long DatabaseBytes, long WalBytes);
    private sealed class CancelAfterHistoryRefresh(int threadId, CancellationTokenSource cancellation) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (Environment.CurrentManagedThreadId == threadId && value.Value is AssetLoadMeasurement
                { Stage: "TextureBrowser.Store.HistoryIndex" }) cancellation.Cancel();
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
    private sealed class MeasurementObserver(ConcurrentQueue<AssetLoadMeasurement> measurements, int threadId) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        { if (Environment.CurrentManagedThreadId == threadId && value.Value is AssetLoadMeasurement measurement) measurements.Enqueue(measurement); }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
