using System.Reflection;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureCurrentRoleProjectionTests
{
    [Fact]
    public async Task LegacyBatchSeeksOrdinalAndIdentityWithoutScanningOrSortingTheWholeCurrentTable()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var baseline = Enumerable.Range(1, CurrentRows).Select(index => fixture.Entry("Generated texture " + index, index)).ToArray();
        store.InitializeTextureProjections("roles", baseline); store.PrepareTextureProjectionIndexes("roles"); fixture.MakeLegacy();
        var method = typeof(FeatureStore).GetMethod("CurrentTextureRoleBackfillCommand", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var command = (SqliteCommand)method.Invoke(store, ["roles", 128L, baseline[127].Identity.Key, null])!;
        Assert.Contains("(p.ordinal,p.identity)>($cursor,$identity)", command.CommandText);
        command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        var plan = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) plan.Add(reader.GetString(3));
        Assert.Contains(plan, line => line.Contains("texture_current_ordinal", StringComparison.Ordinal) && line.Contains("ordinal>?", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("TEMP B-TREE", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN p", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SharedVerifiedRolesAreDistinctAndUpdatesRemoveRetiredValues()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var baseline = fixture.Entry("Generated texture", 1); store.InitializeTextureProjections("roles", [baseline]);
        var full = fixture.WithRoles(baseline, "Diffuse", "Diffuse", " ");
        long oldGeneration = store.CommitTextureProjectionOwner("roles", fixture.Checkpoint, [full]);
        Assert.Equal(new[] { " ", "Diffuse" }, store.TextureProjectionFilters("roles", oldGeneration).VerifiedRoles);
        Assert.Equal(2, fixture.Count("texture_projection_current_roles"));
        long latest = store.CommitTextureProjectionOwner("roles", fixture.Checkpoint, [fixture.WithRoles(baseline, "Normal")]);
        Assert.Equal(new[] { "Normal" }, store.TextureProjectionFilters("roles", latest).VerifiedRoles);
        Assert.Equal(new[] { " ", "Diffuse" }, store.TextureProjectionFilters("roles", oldGeneration).VerifiedRoles);
        Assert.Equal(1, fixture.Count("texture_projection_current_roles"));
    }

    [Fact]
    public async Task QuarantinePublishesZeroRolesAndDoesNotReuseOlderRoleEvidence()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var baseline = fixture.Entry("Generated texture", 1); store.InitializeTextureProjections("roles", [baseline]);
        long oldGeneration = store.CommitTextureProjectionOwner("roles", fixture.Checkpoint, [fixture.WithRoles(baseline, "Diffuse")]);
        store.QuarantineTextureProjectionEvidence("roles", new Dictionary<ResourceIdentity, TextureDiscoveryEntry> { [baseline.Identity] = baseline },
            [baseline.Identity], _ => false);
        Assert.Empty(store.TextureProjectionFilters("roles", store.CaptureTextureProjectionGeneration("roles")).VerifiedRoles);
        Assert.Equal(0, fixture.Count("texture_projection_current_roles")); Assert.Equal(1, fixture.Count("texture_projection_current_role_rows"));
        Assert.Null(store.LoadTextureProjectionDetails("roles", store.CaptureTextureProjectionGeneration("roles"), baseline.Identity));
        Assert.Equal(TextureNameKind.Fallback, Assert.Single(store.QueryTextureProjections("roles",
            store.CaptureTextureProjectionGeneration("roles"), new())).Entries).NameKind);
    }

    [Fact]
    public async Task InterruptedLegacyBackfillKeepsItsCheckpointAndResumesWithoutHistoricalLinks()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var baseline = Enumerable.Range(1, CurrentRows).Select(index => fixture.Entry("Generated texture " + index, index)).ToArray();
        store.InitializeTextureProjections("roles", baseline);
        long generation = store.CommitTextureProjectionOwner("roles", fixture.Checkpoint, baseline.Select(entry => fixture.WithRoles(entry, "Diffuse")).ToArray());
        store.PrepareTextureProjectionIndexes("roles"); fixture.MakeLegacy();
        using var cancellation = new CancellationTokenSource();
        using (var subscription = AssetLoadDiagnostics.Listener.Subscribe(new CancelAfterBatch(cancellation), name => name == AssetLoadDiagnostics.CounterName))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PrepareCurrentTextureRolesAsync("roles", generation, cancellation.Token));
        Assert.Equal(FeatureStore.CurrentTextureRoleBatchSize, fixture.Count("texture_projection_current_role_rows"));
        Assert.True(fixture.Cursor() > 0);
        // Historical relationships are deliberately unavailable; the current thin rows carry the complete owner/role projection.
        fixture.Execute("DELETE FROM texture_projection_links");
        await store.PrepareCurrentTextureRolesAsync("roles", generation);
        Assert.Equal(CurrentRows, fixture.Count("texture_projection_current_role_rows"));
        Assert.Equal(new[] { "Diffuse" }, store.TextureProjectionFilters("roles", generation).VerifiedRoles);
    }

    [Fact]
    public async Task CurrentUpdateRechecksGenerationAndPreservesCommittedMigrationProgress()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var baseline = Enumerable.Range(1, CurrentRows).Select(index => fixture.Entry("Generated texture " + index, index)).ToArray();
        store.InitializeTextureProjections("roles", baseline);
        long generation = store.CommitTextureProjectionOwner("roles", fixture.Checkpoint, baseline.Select(entry => fixture.WithRoles(entry, "Diffuse")).ToArray());
        store.PrepareTextureProjectionIndexes("roles"); fixture.MakeLegacy();
        using var cancellation = new CancellationTokenSource();
        using (var subscription = AssetLoadDiagnostics.Listener.Subscribe(new CancelAfterBatch(cancellation), name => name == AssetLoadDiagnostics.CounterName))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PrepareCurrentTextureRolesAsync("roles", generation, cancellation.Token));
        long cursor = fixture.Cursor();
        long updated = store.CommitTextureProjectionOwner("roles", fixture.Checkpoint, [fixture.WithRoles(baseline[0], "Normal")]);
        var error = await Assert.ThrowsAsync<ArchiveException>(() => store.PrepareCurrentTextureRolesAsync("roles", generation));
        Assert.Equal("TextureProjectionGenerationChanged", error.Code); Assert.Equal(cursor, fixture.Cursor());
        await store.PrepareCurrentTextureRolesAsync("roles", updated);
        Assert.Equal(new[] { "Diffuse", "Normal" }, store.TextureProjectionFilters("roles", updated).VerifiedRoles);
        Assert.Equal(CurrentRows, fixture.Count("texture_projection_current_role_rows"));
    }

    [Fact]
    public async Task CorruptLegacyThinDoesNotCommitRolesOrAdvanceCheckpoint()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var baseline = fixture.Entry("Generated texture", 1); store.InitializeTextureProjections("roles", [baseline]);
        long generation = store.CommitTextureProjectionOwner("roles", fixture.Checkpoint, [fixture.WithRoles(baseline, "Diffuse")]);
        store.PrepareTextureProjectionIndexes("roles"); fixture.MakeLegacy();
        fixture.Execute("UPDATE texture_projection_current SET thin='{broken'");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => store.PrepareCurrentTextureRolesAsync("roles", generation));
        Assert.Equal(0, fixture.Count("texture_projection_current_role_rows")); Assert.Equal(-1, fixture.Cursor());
    }

    [Fact]
    public async Task CompactRolesAreIsolatedBySourceRevision()
    {
        using var first = await Fixture.CreateAsync(); using var second = await Fixture.CreateAsync();
        Assert.Equal(first.Snapshot.IndexContentId, second.Snapshot.IndexContentId);
        Assert.NotEqual(first.Snapshot.PhysicalSourceStamp, second.Snapshot.PhysicalSourceStamp);
        using var original = FeatureStore.Open(first.Snapshot, first.Cache);
        var a = first.Entry("Generated texture", 1); original.InitializeTextureProjections("roles", [a]);
        long firstGeneration = original.CommitTextureProjectionOwner("roles", first.Checkpoint, [first.WithRoles(a, "Diffuse")]);
        using var replaced = FeatureStore.Open(second.Snapshot, first.Cache);
        var b = second.Entry("Generated texture", 1); replaced.InitializeTextureProjections("roles", [b]);
        long secondGeneration = replaced.CommitTextureProjectionOwner("roles", second.Checkpoint, [second.WithRoles(b, "Normal")]);
        Assert.Equal(new[] { "Diffuse" }, original.TextureProjectionFilters("roles", firstGeneration).VerifiedRoles);
        Assert.Equal(new[] { "Normal" }, replaced.TextureProjectionFilters("roles", secondGeneration).VerifiedRoles);
    }

    private const int CurrentRows = FeatureStore.CurrentTextureRoleBatchSize * 2 + 3;
    private sealed class CancelAfterBatch(CancellationTokenSource cancellation) : IObserver<KeyValuePair<string, object?>>
    {
        private int cancelled;
        public void OnNext(KeyValuePair<string, object?> item)
        {
            if (item.Value is AssetLoadCounterMeasurement { Stage: "TextureBrowser.Store.RoleBackfillRows" } batch &&
                batch.Values["Rows"] > 0 && Interlocked.Exchange(ref cancelled, 1) == 0) cancellation.Cancel();
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly object value;
        private Fixture(object value) => this.value = value;
        private T Get<T>(string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
        public ArchiveSnapshot Snapshot => Get<ArchiveSnapshot>("Snapshot");
        public string Cache => Get<string>("Cache");
        public ResourceIdentity Owner => Get<ResourceIdentity>("Owner");
        public TextureEvidence Evidence => Get<TextureEvidence>("Evidence");
        public TextureProjectionOwnerCheckpoint Checkpoint => Get<TextureProjectionOwnerCheckpoint>("Checkpoint");
        public TextureDiscoveryEntry Entry(string label, long ordinal) => (TextureDiscoveryEntry)value.GetType().GetMethod("Entry")!.Invoke(value, [label, ordinal])!;
        public TextureDiscoveryEntry WithRoles(TextureDiscoveryEntry entry, params string[] roles) => entry with
        {
            NameKind = TextureNameKind.VerifiedOwnerRole,
            Owners = roles.Select((role, index) => new TextureDiscoveryLink(Owner with { MemberPath = "owner/" + index }, "Generated panel",
                TextureRelationshipKind.Object, true, true, [Evidence], role)).Append(new(Owner, "Generated candidate", TextureRelationshipKind.Object,
                    true, false, [Evidence], "Unverified")).Append(new(Owner, "Generated empty role", TextureRelationshipKind.Object, true, true, [Evidence], "")).ToArray(),
            NameCandidates = [new(entry.Label, TextureNameKind.VerifiedOwnerRole, [Evidence], Owner)]
        };
        private SqliteConnection OpenDatabase()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Directory.GetFiles(Cache, "catalog-*.sqlite3").Single(), Pooling = false }.ToString());
            connection.Open(); return connection;
        }
        public void Execute(string sql) { using var connection = OpenDatabase(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public void MakeLegacy() => Execute("DELETE FROM texture_projection_current_roles;DELETE FROM texture_projection_current_role_rows;DELETE FROM texture_projection_current_role_state");
        public long Count(string table) { using var connection = OpenDatabase(); using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table; return Convert.ToInt64(command.ExecuteScalar()); }
        public long Cursor() { using var connection = OpenDatabase(); using var command = connection.CreateCommand(); command.CommandText = "SELECT COALESCE(MAX(cursor_ordinal),-1) FROM texture_projection_current_role_state"; return Convert.ToInt64(command.ExecuteScalar()); }
        public static async Task<Fixture> CreateAsync()
        {
            var type = typeof(TextureProjectionStoreTests).GetNestedType("Fixture", BindingFlags.NonPublic)!;
            var task = (Task)type.GetMethod("CreateAsync")!.Invoke(null, [false])!; await task;
            return new(task.GetType().GetProperty("Result")!.GetValue(task)!);
        }
        public void Dispose() => ((IDisposable)value).Dispose();
    }
}
