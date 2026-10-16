using System.Reflection;
using System.Diagnostics;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureBrowseGenerationTests
{
    [Fact]
    public async Task CancelledSummaryCreationCanBeRetriedWithoutPublishingPartialOptions()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        string profile = "cancel-summary-" + Guid.NewGuid().ToString("N");
        store.InitializeTextureProjections(profile, [fixture.Entry("Generated texture")]);
        using var pin = store.AcquireTextureBrowseReadLease(profile, fixture.Revision);
        using var cancellation = new CancellationTokenSource();
        int summaries = 0;
        using var observer = AssetLoadDiagnostics.Listener.Subscribe(new CounterObserver(value =>
        {
            if (value.Stage == "TextureBrowser.Store.BrowseSummary" && value.Key == profile &&
                Interlocked.Increment(ref summaries) == 1) cancellation.Cancel();
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pin.ReadAsync(new(), cancellation.Token));
        var response = await pin.ReadAsync(new());
        Assert.Equal(1, response.Coverage.Resources); Assert.Single(response.Page.Entries);
        Assert.Equal(2, Volatile.Read(ref summaries));
    }

    [Fact]
    public async Task ConcurrentSummaryReadersShareWorkAndCancelIndependently()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        string profile = "shared-summary-" + Guid.NewGuid().ToString("N");
        store.InitializeTextureProjections(profile, [fixture.Entry("Generated texture")]);
        using var first = store.AcquireTextureBrowseReadLease(profile, fixture.Revision);
        using var second = store.AcquireTextureBrowseReadLease(profile, fixture.Revision);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource(); int summaries = 0, waiters = 0;
        using var waiting = new ManualResetEventSlim();
        using var observer = AssetLoadDiagnostics.Listener.Subscribe(new CounterObserver(value =>
        {
            if (value.Key != profile) return;
            if (value.Stage == "TextureBrowser.Store.BrowseSummaryWait" && Interlocked.Increment(ref waiters) == 2)
                waiting.Set();
            if (value.Stage != "TextureBrowser.Store.BrowseSummary") return;
            Interlocked.Increment(ref summaries); entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)), "The summary barrier was not released.");
        }));
        var original = first.ReadAsync(new()); Task<TextureBrowseResponse>? other = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            other = second.ReadAsync(new(), cancellation.Token);
            Assert.True(waiting.Wait(TimeSpan.FromSeconds(5)), "The second reader must reach the occupied summary gate.");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => other.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { release.Set(); }
        var response = await original;
        var retry = await second.ReadAsync(new(Search: "Generated"));
        Assert.Same(response.Options, retry.Options); Assert.Same(response.Coverage, retry.Coverage);
        Assert.Equal(1, Volatile.Read(ref summaries));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)retry.Options.Formats).Add("BC7"));
    }

    [Fact]
    public async Task SummaryEvictionKeepsAnActiveReaderAndBoundsIdleEntries()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        using var writer = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        string profile = "active-summary-" + Guid.NewGuid().ToString("N");
        store.InitializeTextureProjections(profile, [fixture.Entry("Generated texture")]);
        using var pin = store.AcquireTextureBrowseReadLease(profile, fixture.Revision);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var observer = AssetLoadDiagnostics.Listener.Subscribe(new CounterObserver(value =>
        {
            if (value.Stage != "TextureBrowser.Store.BrowseSummary" || value.Key != profile) return;
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        }));
        var pending = pin.ReadAsync(new());
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            for (int index = 0; index < 6; index++)
            {
                string other = profile + "/" + index;
                writer.InitializeTextureProjections(other, [fixture.Entry("Other texture")]);
                using var read = store.AcquireTextureBrowseReadLease(other, fixture.Revision);
                Assert.Single((await read.ReadAsync(new())).Page.Entries);
            }
            Assert.True(Field<System.Collections.IDictionary>(store, "textureBrowseSummaries").Count <= 4);
        }
        finally { release.Set(); }
        var response = await pending;
        Assert.Single(response.Page.Entries);
        Assert.Same(response.Options, (await pin.ReadAsync(new())).Options);
        Assert.True(Field<System.Collections.IDictionary>(store, "textureBrowseSummaries").Count <= 4);
    }

    [Fact]
    public async Task RepeatedQueriesReuseOnlyTheSamePublishedGenerationSummary()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        using var writer = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        string profile = "summary-" + Guid.NewGuid().ToString("N");
        var first = fixture.Entry("Texture A"); var second = fixture.Entry("Texture B", 2);
        store.InitializeTextureProjections(profile, [first, second]);
        int summaries = 0;
        using var observer = AssetLoadDiagnostics.Listener.Subscribe(new CounterObserver(value =>
        {
            if (value.Stage == "TextureBrowser.Store.BrowseSummary" && value.Key == profile)
                Interlocked.Increment(ref summaries);
        }));
        using var original = store.AcquireTextureBrowseReadLease(profile, fixture.Revision);
        var page = await original.ReadAsync(new(Limit: 1));
        using var search = store.AcquireTextureBrowseReadLease(profile, fixture.Revision);
        var filtered = await search.ReadAsync(new(Search: "Texture B"));
        Assert.Equal(1, Volatile.Read(ref summaries));
        Assert.Equal(2, filtered.Coverage.Resources); Assert.Equal(1, filtered.Page.Total);
        Assert.Equal(second.Identity, Assert.Single(filtered.Page.Entries).Identity);
        Assert.Same(page.Options, filtered.Options); Assert.Same(page.Coverage, filtered.Coverage);
        writer.CommitTextureProjectionOwner(profile, fixture.Checkpoint,
            [fixture.Named(first, 32, 16, "BC1", "Diffuse", "Room")]);
        using var replacement = store.AcquireTextureBrowseReadLease(profile, fixture.Revision);
        var newer = await replacement.ReadAsync(new());
        Assert.Equal(2, Volatile.Read(ref summaries));
        Assert.Equal(new[] { "Diffuse" }, newer.Options.VerifiedRoles);
        Assert.Empty(page.Options.VerifiedRoles);
        var pinned = await original.ReadAsync(new(Offset: 1, Limit: 1));
        Assert.Equal(page.Token, pinned.Token); Assert.Same(page.Options, pinned.Options);
        Assert.Equal(2, Volatile.Read(ref summaries));
    }

    [Fact]
    public async Task UnpublishedSessionRebindsWithinItsFirstReadSnapshot()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        using var writer = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        string profile = "first-read-" + Guid.NewGuid().ToString("N");
        var first = fixture.Entry("Texture A"); var second = fixture.Entry("Texture B", 2);
        store.InitializeTextureProjections(profile, [first]);
        using var pin = store.AcquireTextureBrowseReadLease(profile, fixture.Revision, rebindUnpublished: true);
        long replacement = writer.InitializeTextureProjections(profile, [first, second]);
        int publications = 0;
        using var observer = AssetLoadDiagnostics.Listener.Subscribe(new CounterObserver(value =>
        {
            if (value.Stage != "TextureBrowser.Store.BrowseSnapshot" || value.Key != profile) return;
            if (Interlocked.Increment(ref publications) == 1) writer.InitializeTextureProjections(profile, [first]);
        }));
        var response = await pin.ReadAsync(new());
        Assert.Equal(replacement, response.Token.Generation); Assert.Equal(response.Token, pin.Token);
        Assert.Equal(2, response.Page.Total); Assert.Equal(2, response.Coverage.Resources);
        Assert.Equal(2, response.Page.Entries.Count);
        Assert.Equal("TextureBrowseGenerationUnavailable", (await Assert.ThrowsAsync<ArchiveException>(() => pin.ReadAsync(new()))).Code);
    }

    [Fact]
    public async Task UnpublishedLocationReadsTheReplacementPageAndCoverageTogether()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var first = fixture.Entry("Texture A"); var second = fixture.Entry("Texture B", 2);
        store.InitializeTextureProjections("locate-replacement", [first]);
        using var pin = store.AcquireTextureBrowseReadLease("locate-replacement", fixture.Revision, rebindUnpublished: true);
        long replacement = store.InitializeTextureProjections("locate-replacement", [first, second]);
        var response = Assert.IsType<TextureBrowseResponse>(await pin.LocateAsync(second.Identity, new(Limit: 1)));
        Assert.Equal(replacement, response.Token.Generation); Assert.Equal(response.Token, pin.Token);
        Assert.Equal(1, response.Page.Offset); Assert.Equal(second.Identity, Assert.Single(response.Page.Entries).Identity);
        Assert.Equal(2, response.Page.Total); Assert.Equal(2, response.Coverage.Resources);
    }

    [Fact]
    public async Task PublishedSessionsDoNotRebindSilently()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var first = fixture.Entry("Texture A");
        store.InitializeTextureProjections("published-pin", [first]);
        using var pin = store.AcquireTextureBrowseReadLease("published-pin", fixture.Revision, rebindUnpublished: true);
        var published = await pin.ReadAsync(new());
        store.InitializeTextureProjections("published-pin", [first, fixture.Entry("Texture B", 2)]);
        Assert.Equal("TextureBrowseGenerationUnavailable", (await Assert.ThrowsAsync<ArchiveException>(() => pin.ReadAsync(new()))).Code);
        Assert.Equal(published.Token, pin.Token);
    }

    [Theory]
    [InlineData("TextureHistoryRowEvents", "sqlite_autoindex_texture_projection_rows_1")]
    [InlineData("TextureHistoryAliasEvents", "sqlite_autoindex_texture_projection_alias_versions_1")]
    public async Task HistoryRefreshSeeksChangedIdentitiesInsteadOfScanningTheProfile(string eventQuery, string sourceIndex)
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        store.InitializeTextureProjections("history-plan", [fixture.Entry("Generated texture")]);
        string sql = (string)typeof(FeatureStore).GetField(eventQuery, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        using var command = Field<SqliteConnection>(store, "connection").CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        command.Parameters.AddWithValue("$revision", SourceIdentity.DerivedCacheKey(fixture.Revision.IndexContentId, fixture.Revision.PhysicalSourceStamp));
        command.Parameters.AddWithValue("$profile", "history-plan");
        var plan = new List<string>(); using var rows = command.ExecuteReader(); while (rows.Read()) plan.Add(rows.GetString(3));
        Assert.Contains("texture_projection_history_dirty", plan[0], StringComparison.Ordinal);
        Assert.Contains(plan, step => step.Contains(sourceIndex, StringComparison.Ordinal) && step.Contains("identity=?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HistoricalRolesStayCompleteWhenTheCurrentBackfillIsMissing()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var first = fixture.Entry("Texture A"); var second = fixture.Entry("Texture B", 2);
        store.InitializeTextureProjections("role-coverage", [first, second]);
        store.CommitTextureProjectionOwner("role-coverage", fixture.Checkpoint,
            [fixture.Named(first, 32, 16, "BC1", "Diffuse", "Room"), fixture.Named(second, 64, 32, "BC3", "Normal", "Hall")]);
        using (var remove = Field<SqliteConnection>(store, "connection").CreateCommand())
        {
            remove.CommandText = "DELETE FROM texture_projection_current_roles WHERE profile='role-coverage'; " +
                "DELETE FROM texture_projection_current_role_rows WHERE profile='role-coverage'; " +
                "DELETE FROM texture_projection_current_role_state WHERE profile='role-coverage';";
            remove.ExecuteNonQuery();
        }
        using var pinned = store.AcquireTextureBrowseReadLease("role-coverage", fixture.Revision);
        var response = await pinned.ReadAsync(new());
        Assert.Equal(new[] { "Diffuse", "Normal" }, response.Options.VerifiedRoles);
        Assert.Equal(2, response.Page.Total);
    }

    [Fact]
    public async Task CatalogLabelsAndMembershipStayWithTheirPublishedGeneration()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var first = fixture.Entry("Texture A"); var second = fixture.Entry("Texture B", 2);
        var catalog = new ResourceIdentity(fixture.Snapshot.IndexContentId, 9);
        store.InitializeTextureProjections("catalogs", [first, second]);
        store.CommitNamedTextureProjectionCatalog("catalogs", fixture.Revision, catalog, new string('a', 64),
            [first.Identity], "Generated room");
        using var pinned = store.AcquireTextureBrowseReadLease("catalogs", fixture.Revision);
        store.CommitNamedTextureProjectionCatalog("catalogs", fixture.Revision, catalog, new string('b', 64),
            [second.Identity], "Updated room");
        var query = new TextureDiscoveryQuery(CatalogKey: catalog.Key);
        var before = await pinned.ReadAsync(query);
        Assert.Equal(first.Identity, Assert.Single(before.Page.Entries).Identity);
        Assert.Equal(new TextureContextOption(catalog.Key, "Generated room"), Assert.Single(before.Options.Catalogs));
        using var current = store.AcquireTextureBrowseReadLease("catalogs", fixture.Revision);
        var after = await current.ReadAsync(query);
        Assert.Equal(second.Identity, Assert.Single(after.Page.Entries).Identity);
        Assert.Equal(new TextureContextOption(catalog.Key, "Updated room"), Assert.Single(after.Options.Catalogs));
        long generation = after.Token.Generation;
        Assert.Equal(generation, store.CommitNamedTextureProjectionCatalog("catalogs", fixture.Revision, catalog,
            new string('b', 64), [second.Identity], "Updated room"));
        Assert.True(store.CommitNamedTextureProjectionCatalog("catalogs", fixture.Revision, catalog,
            new string('b', 64), [second.Identity], "Renamed room") > generation);
    }

    [Fact]
    public async Task PublicationKeepsPinnedRowsOptionsAndCoverageTogether()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        using var writer = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var entry = fixture.Entry("Generated texture");
        store.InitializeTextureProjections("browse", [entry]);
        long generation = store.CommitTextureProjectionOwner("browse", fixture.Checkpoint,
            [fixture.Named(entry, 32, 16, "BC1", "Diffuse", "Room")]);
        using var pinned = store.AcquireTextureBrowseReadLease("browse", fixture.Revision);
        writer.CommitTextureProjectionOwner("browse", fixture.Checkpoint,
            [fixture.Named(entry with { Label = "Updated texture" }, 64, 32, "BC3", "Normal", "Hall")]);
        writer.SetTextureProjectionAlias("browse", entry.Identity, "Updated alias");
        var original = await pinned.ReadAsync(new());
        Assert.Equal(generation, original.Token.Generation);
        Assert.Equal(pinned.Token, original.Token);
        Assert.Equal("Generated texture", Assert.Single(original.Page.Entries).Label);
        Assert.Null(original.Page.Entries[0].Alias);
        Assert.Equal(new[] { "BC1" }, original.Options.Formats);
        Assert.Equal(new[] { "Diffuse" }, original.Options.VerifiedRoles);
        Assert.Equal(1, original.Page.Total); Assert.Equal(1, original.Coverage.Resources);
        Assert.Equal(1, original.Coverage.OwnerNames); Assert.Equal(0, original.Coverage.Aliases);
        using var current = writer.AcquireTextureBrowseReadLease("browse", fixture.Revision);
        var updated = await current.ReadAsync(new());
        Assert.True(updated.Token.Generation > original.Token.Generation);
        Assert.Equal("Updated alias", Assert.Single(updated.Page.Entries).Alias);
        Assert.Equal(new[] { "BC3" }, updated.Options.Formats);
        Assert.Equal(1, updated.Coverage.Aliases);
    }

    [Fact]
    public async Task AliasEditFromAnotherStoreKeepsPagingAndExplicitOldPinsStable()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        using var writer = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var first = fixture.Entry("Texture A"); var second = fixture.Entry("Texture B", 2);
        store.InitializeTextureProjections("pages", [first, second]);
        using var pinned = store.AcquireTextureBrowseReadLease("pages", fixture.Revision);
        var query = new TextureDiscoveryQuery(Limit: 1, NamedFirst: false);
        var before = await pinned.ReadAsync(query);
        writer.SetTextureProjectionAlias("pages", second.Identity, "A saved alias");
        var after = await pinned.ReadAsync(query with { Offset = 1 });
        Assert.Equal(before.Token, after.Token); Assert.Equal(2, after.Page.Total);
        Assert.Equal(first.Identity, Assert.Single(before.Page.Entries).Identity);
        Assert.Equal(second.Identity, Assert.Single(after.Page.Entries).Identity);
        Assert.Null(after.Page.Entries[0].Alias);
        using var restored = writer.AcquireTextureBrowseReadLease("pages", fixture.Revision, before.Token.Generation);
        Assert.Equal(before.Token, (await restored.ReadAsync(query)).Token);
        using var latest = writer.AcquireTextureBrowseReadLease("pages", fixture.Revision);
        Assert.Equal(second.Identity, Assert.Single((await latest.ReadAsync(query)).Page.Entries).Identity);
    }

    [Fact]
    public async Task ReadsProceedWithoutTheWriterGateAndDoNotWrite()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        store.InitializeTextureProjections("independent", [fixture.Entry("Generated texture")]);
        var connection = Field<SqliteConnection>(store, "connection");
        using var changes = connection.CreateCommand(); changes.CommandText = "SELECT total_changes()";
        long before = Convert.ToInt64(changes.ExecuteScalar());
        var writerGate = Field<object>(store, "gate");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var writer = Task.Run(() => { lock (writerGate) { entered.Set(); release.Wait(); } });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            using var pinned = await Task.Run(() => store.AcquireTextureBrowseReadLease("independent", fixture.Revision))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single((await pinned.ReadAsync(new()).WaitAsync(TimeSpan.FromSeconds(5))).Page.Entries);
        }
        finally { release.Set(); await writer.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(before, Convert.ToInt64(changes.ExecuteScalar()));
    }

    [Fact]
    public async Task ResetInvalidatesOldPinsAndKeepsNewGenerationsMonotonic()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var first = fixture.Entry("Texture A");
        store.InitializeTextureProjections("reset", [first]);
        using var pinned = store.AcquireTextureBrowseReadLease("reset", fixture.Revision);
        long reset = store.InitializeTextureProjections("reset", [first, fixture.Entry("Texture B", 2)]);
        Assert.True(reset > pinned.Token.Generation);
        var error = await Assert.ThrowsAsync<ArchiveException>(() => pinned.ReadAsync(new()));
        Assert.Equal("TextureBrowseGenerationUnavailable", error.Code);
        using var current = store.AcquireTextureBrowseReadLease("reset", fixture.Revision);
        Assert.Equal(2, (await current.ReadAsync(new())).Page.Total);
    }

    [Fact]
    public async Task DisposalRejectsLateReadsAndForeignRevisions()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        store.InitializeTextureProjections("lifetime", [fixture.Entry("Generated texture")]);
        var mismatch = Assert.Throws<ArchiveException>(() => store.AcquireTextureBrowseReadLease("lifetime",
            fixture.Revision with { PhysicalSourceStamp = "different-generated-source" }));
        Assert.Equal("TextureBrowseSourceMismatch", mismatch.Code);
        using var pinned = store.AcquireTextureBrowseReadLease("lifetime", fixture.Revision);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pinned.ReadAsync(new(), cancelled.Token));
        Assert.Single((await pinned.ReadAsync(new())).Page.Entries);
        store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = pinned.ReadAsync(new()); });
    }

    [Fact]
    public async Task CancelledAliasPublicationLeavesHeadCheckpointAndEvidenceUnchanged()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var entry = fixture.Entry("Generated texture");
        string profile = "cancel-" + Guid.NewGuid().ToString("N");
        store.InitializeTextureProjections(profile, [entry]);
        store.CommitTextureProjectionOwner(profile, fixture.Checkpoint, [fixture.Named(entry, 32, 16, "BC1", "Diffuse", "Room")]);
        using var pinned = store.AcquireTextureBrowseReadLease(profile, fixture.Revision);
        string before = EvidenceFingerprint(store);
        using var cancelled = new CancellationTokenSource();
        using var observer = AssetLoadDiagnostics.Listener.Subscribe(new CounterObserver(value =>
        {
            if (value.Stage == "TextureBrowser.Store.AliasPublicationPrepared" && value.Key == profile) cancelled.Cancel();
        }));
        Assert.Throws<OperationCanceledException>(() => store.SetTextureProjectionAlias(profile, entry.Identity,
            "Cancelled label", cancelled.Token));
        Assert.Equal(pinned.Token.Generation, store.CaptureTextureProjectionGeneration(profile));
        Assert.Equal(before, EvidenceFingerprint(store));
        Assert.Null(Assert.Single((await pinned.ReadAsync(new())).Page.Entries).Alias);
        store.SetTextureProjectionAlias(profile, entry.Identity, "Saved label");
        Assert.Equal(before, EvidenceFingerprint(store));
    }

    [Fact]
    public async Task LegacyAliasPreparationIsTransactionalAndKeepsEvidence()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        var entry = fixture.Entry("Generated texture");
        long published; string evidence;
        using (var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache))
        {
            store.InitializeTextureProjections("legacy-browse", [entry]);
            store.CommitTextureProjectionOwner("legacy-browse", fixture.Checkpoint, [fixture.Named(entry, 32, 16, "BC1", "Diffuse", "Room")]);
            store.SetTextureProjectionAlias("legacy-browse", entry.Identity, "Library alias");
            published = store.CaptureTextureProjectionGeneration("legacy-browse");
            evidence = EvidenceFingerprint(store);
            using var legacy = Field<SqliteConnection>(store, "connection").CreateCommand();
            legacy.CommandText = "DELETE FROM texture_projection_alias_versions; DELETE FROM texture_projection_alias_migrations; " +
                "DELETE FROM texture_projection_history_index; DELETE FROM texture_projection_history_dirty;";
            legacy.ExecuteNonQuery();
        }
        using var restored = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var pending = Assert.Throws<ArchiveException>(() => restored.AcquireTextureBrowseReadLease("legacy-browse", fixture.Revision));
        Assert.Equal("TextureBrowseGenerationUnavailable", pending.Code);
        Assert.Equal(published, restored.InitializeTextureProjections("legacy-browse", [entry]));
        using var pin = restored.AcquireTextureBrowseReadLease("legacy-browse", fixture.Revision);
        var response = await pin.ReadAsync(new(Search: "Library alias"));
        Assert.Equal("Library alias", Assert.Single(response.Page.Entries).Alias);
        Assert.Equal(1, response.Coverage.Aliases);
        Assert.Equal(evidence, EvidenceFingerprint(restored));
    }

    private static string EvidenceFingerprint(FeatureStore store)
    {
        using var command = Field<SqliteConnection>(store, "connection").CreateCommand();
        command.CommandText = "SELECT group_concat(identity||':'||generation||':'||length(payload)||':'||sha256,';') " +
            "FROM (SELECT * FROM texture_projection_evidence ORDER BY identity,generation)";
        return (string)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task DisposalCancelsAndDrainsConcurrentIndependentReads()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        string profile = "drain-" + Guid.NewGuid().ToString("N");
        store.InitializeTextureProjections(profile, [fixture.Entry("Generated texture")]);
        using var pin = store.AcquireTextureBrowseReadLease(profile, fixture.Revision);
        using var entered = new CountdownEvent(2); using var release = new ManualResetEventSlim();
        using var cancellationObserved = new ManualResetEventSlim();
        var lifetime = Field<CancellationTokenSource>(store, "textureReadLifetime");
        using var registration = lifetime.Token.Register(cancellationObserved.Set);
        using var observer = AssetLoadDiagnostics.Listener.Subscribe(new CounterObserver(value =>
        {
            if (value.Stage != "TextureBrowser.Store.BrowseSnapshot" || value.Key != profile) return;
            entered.Signal();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)), "The generated reader barrier was not released.");
        }));
        var first = pin.ReadAsync(new()); var second = pin.ReadAsync(new(Search: "Generated"));
        Task? closing = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "Both independent reads must reach the snapshot barrier.");
            var started = Stopwatch.GetTimestamp();
            closing = Task.Run(store.Dispose);
            Assert.True(cancellationObserved.Wait(TimeSpan.FromMilliseconds(250)), "Cancellation acknowledgement exceeded 250 ms.");
            release.Set();
            await closing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.Empty(Field<HashSet<SqliteConnection>>(store, "activeTextureReadConnections"));
        }
        finally
        {
            release.Set();
            if (closing is not null) await closing.WaitAsync(TimeSpan.FromSeconds(5));
            try { await Task.WhenAll(first, second); } catch (OperationCanceledException) { }
        }
    }

    private sealed class CounterObserver(Action<AssetLoadCounterMeasurement> observe) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        { if (value.Value is AssetLoadCounterMeasurement counter) observe(counter); }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private static T Field<T>(object value, string name) where T : class =>
        (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
}
