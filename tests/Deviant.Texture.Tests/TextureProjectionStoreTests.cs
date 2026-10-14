using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Diagnostics;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Deviant.Texture.Tests;

public sealed class TextureProjectionStoreTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CatalogFilterUnionsDirectMembersWithOwnerMembershipAndIncludesRequestedLodRows()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var catalog = new ResourceIdentity(fixture.Snapshot.IndexContentId, 9);
        var image = fixture.Entry("Image"); var mip = fixture.Entry("Mip", 2) with { ResourceType = 2230, Kind = TextureKind.LodBuffer };
        store.InitializeTextureProjections("profile", [image, mip]);
        store.CommitTextureProjectionOwner("profile", fixture.Checkpoint, [image with { CatalogKeys = [catalog.Key] }]);
        long generation = store.CommitTextureProjectionCatalog("profile", fixture.Revision, catalog, new string('a', 64),
            [image.Identity, mip.Identity, mip.Identity]);
        Assert.Equal(image.Identity, Assert.Single(store.QueryTextureProjections("profile", generation, new(CatalogKey: catalog.Key)).Entries).Identity);
        var page = store.QueryTextureProjections("profile", generation, new(CatalogKey: catalog.Key, IncludeLodBuffers: true));
        Assert.Equal(2, page.Total); Assert.Equal(new[] { image.Identity, mip.Identity }, page.Entries.Select(entry => entry.Identity).ToArray());
        Assert.All(page.Entries, entry => Assert.Contains(catalog.Key, entry.CatalogKeys));
        Assert.Null(store.LoadTextureProjectionDetails("profile", generation, mip.Identity));
    }

    [Fact]
    public async Task DirectCatalogMembershipRestoresWithoutOwnerEvidenceOrReinitializingRows()
    {
        using var fixture = await Fixture.CreateAsync(); var catalog = new ResourceIdentity(fixture.Snapshot.IndexContentId, 9);
        var baseline = new[] { fixture.Entry("A"), fixture.Entry("B", 2) }; long generation;
        using (var cold = FeatureStore.Open(fixture.Snapshot, fixture.Cache))
        {
            cold.InitializeTextureProjections("profile", baseline);
            generation = cold.CommitTextureProjectionCatalog("profile", fixture.Revision, catalog, new string('b', 64), [baseline[1].Identity]);
        }
        using var warm = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        Assert.Equal(generation, warm.InitializeTextureProjections("profile", baseline));
        Assert.Equal(baseline[1].Identity, Assert.Single(warm.QueryTextureProjections("profile", generation, new(CatalogKey: catalog.Key)).Entries).Identity);
        Assert.Empty(warm.LoadTextureProjectionOwners("profile"));
        Assert.Null(warm.LoadTextureProjectionDetails("profile", generation, baseline[1].Identity));
    }

    [Fact]
    public async Task ChangedDirectMembershipKeepsCapturedPagesStableAndCanceledCommitDoesNotPublish()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var catalog = new ResourceIdentity(fixture.Snapshot.IndexContentId, 9);
        var baseline = new[] { fixture.Entry("A"), fixture.Entry("B", 2) };
        store.InitializeTextureProjections("profile", baseline);
        long first = store.CommitTextureProjectionCatalog("profile", fixture.Revision, catalog, new string('a', 64), baseline.Select(entry => entry.Identity).ToArray());
        long changed = store.CommitTextureProjectionCatalog("profile", fixture.Revision, catalog, new string('b', 64), [baseline[1].Identity]);
        Assert.True(changed > first);
        Assert.Equal(2, store.QueryTextureProjections("profile", first, new(CatalogKey: catalog.Key)).Total);
        Assert.Equal(baseline[1].Identity, Assert.Single(store.QueryTextureProjections("profile", changed, new(CatalogKey: catalog.Key)).Entries).Identity);
        Assert.Equal(changed, store.CommitTextureProjectionCatalog("profile", fixture.Revision, catalog, new string('b', 64), [baseline[1].Identity]));
        Assert.Throws<OperationCanceledException>(() => store.CommitTextureProjectionCatalog("profile", fixture.Revision, catalog,
            new string('c', 64), [baseline[0].Identity], new(true)));
        Assert.Equal(changed, store.CaptureTextureProjectionGeneration("profile"));
        long empty = store.CommitTextureProjectionCatalog("profile", fixture.Revision, catalog, new string('c', 64), []);
        Assert.Empty(store.QueryTextureProjections("profile", empty, new(CatalogKey: catalog.Key)).Entries);
        Assert.Throws<InvalidDataException>(() => store.CommitTextureProjectionCatalog("profile", fixture.Revision, catalog,
            new string('d', 64), [new ResourceIdentity("foreign-index", 1)]));
    }

    [Fact]
    public async Task DamagedSupportingEvidenceReturnsNamesToBaselineAndPreservesAliases()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var baseline = fixture.Entry("Texture 1"); store.InitializeTextureProjections("profile", [baseline]);
        var full = baseline with { Label = "Panel texture", NameKind = TextureNameKind.VerifiedOwnerRole,
            MetadataIdentities = [fixture.Owner with { MemberPath = "filetext/12" }],
            Owners = [new(fixture.Owner, "Panel", TextureRelationshipKind.Object, true, true, [fixture.Evidence], "Texture slot 0")],
            NameCandidates = [new("Panel texture", TextureNameKind.VerifiedOwnerRole, [fixture.Evidence], fixture.Owner, "Texture slot 0")] };
        store.CommitTextureProjectionOwner("profile", fixture.Checkpoint, [full]);
        store.SetTextureProjectionAlias("profile", baseline.Identity, "Saved label");
        CorruptEvidence(fixture, baseline.Identity, validChecksum: true);
        var rebuild = store.RepairTextureProjectionEvidence("profile", new Dictionary<ResourceIdentity, TextureDiscoveryEntry> { [baseline.Identity] = baseline },
            evidence => evidence.Revision == fixture.Revision);
        Assert.Equal(fixture.Owner, Assert.Single(rebuild));
        var current = store.CaptureTextureProjectionGeneration("profile");
        var repaired = Assert.Single(store.QueryTextureProjections("profile", current, new()).Entries);
        Assert.Equal("Texture 1", repaired.Label); Assert.Equal("Saved label", repaired.Alias); Assert.Equal(TextureNameKind.Fallback, repaired.NameKind);
        Assert.Empty(store.QueryTextureProjections("profile", current, new(Search: "forged")).Entries);
        Assert.Empty(store.LoadTextureProjectionOwners("profile")); Assert.Null(store.LoadTextureProjectionDetails("profile", current, baseline.Identity));
        Assert.Empty(store.RepairTextureProjectionEvidence("profile", new Dictionary<ResourceIdentity, TextureDiscoveryEntry> { [baseline.Identity] = baseline }));
    }

    [Fact]
    public async Task SharedDamagedTextureInvalidatesEverySupportingOwnerAndTheirOtherRows()
    {
        using var fixture = await Fixture.CreateAsync(secondOwner: true); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var baseline = new[] { fixture.Entry("Texture 1"), fixture.Entry("Texture 2", 2) };
        store.InitializeTextureProjections("profile", baseline);
        var secondOwner = new ResourceIdentity(fixture.Snapshot.IndexContentId, 3);
        var secondExtent = fixture.Extent with { Offset = 3 };
        var secondEvidence = fixture.Evidence with { Source = secondOwner, PhysicalExtent = secondExtent, DecodedExtent = new(secondOwner, 0, 1) };
        var shared = baseline[0] with { Label = "Shared surface", NameKind = TextureNameKind.VerifiedOwnerRole,
            MetadataIdentities = [fixture.Owner with { MemberPath = "filetext/12" }, secondOwner with { MemberPath = "filetext/12" }],
            NameCandidates = [new("Shared surface", TextureNameKind.VerifiedOwnerRole, [fixture.Evidence, secondEvidence])] };
        var other = baseline[1] with { Label = "Second surface", NameKind = TextureNameKind.VerifiedOwnerRole,
            MetadataIdentities = [secondOwner with { MemberPath = "filetext/12" }],
            NameCandidates = [new("Second surface", TextureNameKind.VerifiedOwnerRole, [secondEvidence])] };
        store.CommitTextureProjectionOwner("profile", fixture.Checkpoint, [shared]);
        store.CommitTextureProjectionOwner("profile", fixture.Checkpoint with { Identity = secondOwner, MetadataExtent = secondExtent }, [shared, other]);
        CorruptEvidence(fixture, shared.Identity, validChecksum: false);
        var rebuild = store.RepairTextureProjectionEvidence("profile", baseline.ToDictionary(entry => entry.Identity));
        Assert.Equal(new long[] { 0, 3 }, rebuild.Select(owner => owner.Ordinal).Order().ToArray());
        var page = store.QueryTextureProjections("profile", store.CaptureTextureProjectionGeneration("profile"), new());
        Assert.Equal(new[] { "Texture 1", "Texture 2" }, page.Entries.Select(entry => entry.Label).ToArray());
        Assert.Empty(store.LoadTextureProjectionOwners("profile"));
    }

    [Fact]
    public async Task OwnerCommitPublishesThinRowsAndLoadsEvidenceOnlyOnRequest()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        long baseline = store.InitializeTextureProjections("profile", [fixture.Entry("Texture 1"), fixture.Entry("Texture 2", 2)]);
        var full = fixture.Entry("Panel texture") with
        {
            NameKind = TextureNameKind.VerifiedOwnerRole, Width = 32, Height = 16, Format = "BC1", CatalogKeys = ["catalog"],
            Owners = [new(fixture.Owner, "Panel", TextureRelationshipKind.Object, true, true, [fixture.Evidence], "Texture slot 0")],
            NameCandidates = [new("Panel texture", TextureNameKind.VerifiedOwnerRole, [fixture.Evidence], fixture.Owner, "Texture slot 0")],
            MetadataIdentities = [fixture.Owner with { MemberPath = "filetext/12" }]
        };
        long generation = store.CommitTextureProjectionOwner("profile", fixture.Checkpoint, [full]);
        Assert.True(generation > baseline);
        var page = store.QueryTextureProjections("profile", generation, new(Search: "Panel"));
        var thin = Assert.Single(page.Entries); Assert.Empty(thin.NameCandidates); Assert.Empty(thin.Owners[0].Evidence);
        Assert.Equal("Texture slot 0", thin.Owners[0].Role);
        var details = store.LoadTextureProjectionDetails("profile", generation, full.Identity)!;
        Assert.Single(details.NameCandidates); Assert.Single(details.Owners[0].Evidence);
        Assert.Single(store.LoadTextureProjectionOwners("profile"));
        Assert.Single(store.QueryTextureProjectionsByOwner("profile", generation, fixture.Owner, 0, 10).Entries);
        Assert.Equal(1, store.TextureProjectionCounts("profile", generation).OwnerRoles);
        Assert.Single(store.TextureProjectionFilters("profile", generation).Dimensions);
    }

    [Fact]
    public async Task CapturedGenerationKeepsPagingStableAcrossLaterOwnerUpdates()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        long first = store.InitializeTextureProjections("profile", [fixture.Entry("A"), fixture.Entry("C", 2)]);
        store.CommitTextureProjectionOwner("profile", fixture.Checkpoint, [fixture.Entry("B", 2)]);
        Assert.Equal("C", Assert.Single(store.QueryTextureProjections("profile", first, new(Offset: 1, Limit: 1)).Entries).Label);
        long current = store.CaptureTextureProjectionGeneration("profile");
        Assert.Equal("B", Assert.Single(store.QueryTextureProjections("profile", current, new(Offset: 1, Limit: 1)).Entries).Label);
        Assert.Equal(1, store.LocateTextureProjection("profile", first, fixture.Entry("C", 2).Identity, new(Limit: 1))!.Offset);
    }

    [Fact]
    public async Task FailedEvidenceCommitLeavesRowsCheckpointAndGenerationUnchanged()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        long generation = store.InitializeTextureProjections("profile", [fixture.Entry("A")]);
        var invalid = fixture.Entry("Too large") with { AdvancedLines = [new string('x', FeatureStore.MaximumTextureEvidenceBytes)] };
        Assert.Throws<InvalidDataException>(() => store.CommitTextureProjectionOwner("profile", fixture.Checkpoint, [invalid]));
        Assert.Equal(generation, store.CaptureTextureProjectionGeneration("profile"));
        Assert.Empty(store.LoadTextureProjectionOwners("profile"));
        Assert.Equal("A", Assert.Single(store.QueryTextureProjections("profile", generation, new()).Entries).Label);
        Assert.Throws<OperationCanceledException>(() => store.CommitTextureProjectionOwner("profile", fixture.Checkpoint, [fixture.Entry("B")], new(true)));
    }

    [Fact]
    public async Task AliasesParticipateInIndexedSearchOrderingAndRemoval()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        long generation = store.InitializeTextureProjections("profile", [fixture.Entry("C"), fixture.Entry("B", 2)]);
        store.SetTextureProjectionAlias("profile", fixture.Entry("C").Identity, "A saved alias");
        Assert.Equal("A saved alias", Assert.Single(store.QueryTextureProjections("profile", generation, new(Search: "saved alias")).Entries).Alias);
        Assert.Equal(fixture.Entry("C").Identity, store.QueryTextureProjections("profile", generation, new()).Entries[0].Identity);
        store.SetTextureProjectionAlias("profile", fixture.Entry("C").Identity, null);
        Assert.Empty(store.QueryTextureProjections("profile", generation, new(Search: "saved alias")).Entries);
    }

    [Fact]
    public async Task CurrentPagesUseIndexedNaturalOrderingAndRetainHistoricalRows()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var entries = Enumerable.Range(1, 320).Select(i => fixture.Entry("Texture " + (321 - i), i)).ToArray();
        long first = store.InitializeTextureProjections("profile", entries);
        var initial = store.QueryTextureProjections("profile", first, new(Offset: 100, Limit: 100));
        Assert.Equal(320, initial.Total);
        Assert.Equal(Enumerable.Range(101, 100).Select(i => "Texture " + i), initial.Entries.Select(entry => entry.Label));
        long latest = store.CommitTextureProjectionOwner("profile", fixture.Checkpoint, [entries[0] with { Label = "Texture 0", SearchText = "Texture 0" }]);
        Assert.Equal("Texture 0", store.QueryTextureProjections("profile", latest, new(Limit: 1)).Entries[0].Label);
        Assert.Equal("Texture 1", store.QueryTextureProjections("profile", first, new(Limit: 1)).Entries[0].Label);
        Assert.Equal(initial.Entries.Select(entry => entry.Identity), store.QueryTextureProjections("profile", first, new(Offset: 100, Limit: 100)).Entries.Select(entry => entry.Identity));
        using var db = OpenDatabase(fixture);
        using var plan = db.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN SELECT thin FROM texture_projection_current INDEXED BY texture_current_visible_named " +
            "WHERE revision=$revision AND profile='profile' AND kind!=2 ORDER BY name_rank,effective_sort,ordinal,identity LIMIT 100 OFFSET 100";
        plan.Parameters.AddWithValue("$revision", SourceIdentity.DerivedCacheKey(fixture.Snapshot.IndexContentId, fixture.Snapshot.PhysicalSourceStamp));
        var steps = new List<string>(); using var reader = plan.ExecuteReader(); while (reader.Read()) steps.Add(reader.GetString(3));
        Assert.Contains(steps, step => step.Contains("texture_current_visible_named", StringComparison.Ordinal));
        Assert.DoesNotContain(steps, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TrigramSearchKeepsLiteralSubstringUnicodeShortTermsAndAliasSemantics()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var first = fixture.Entry("Écran diffuse 100%"); var second = fixture.Entry("Normal", 2);
        long generation = store.InitializeTextureProjections("profile", [first, second]);
        foreach (string search in new[] { "écr", "FUSE", "100%", "fu", "%" })
            Assert.Equal(first.Identity, Assert.Single(store.QueryTextureProjections("profile", generation, new(Search: search)).Entries).Identity);
        store.SetTextureProjectionAlias("profile", second.Identity, "Saved diffuse");
        Assert.Equal(2, store.QueryTextureProjections("profile", generation, new(Search: "diffuse")).Total);
        Assert.Equal(second.Identity, Assert.Single(store.QueryTextureProjections("profile", generation, new(Search: "saved diffuse")).Entries).Identity);
        store.SetTextureProjectionAlias("profile", second.Identity, "Changed normal");
        Assert.Equal(1, store.QueryTextureProjections("profile", generation, new(Search: "diffuse")).Total);
        Assert.Empty(store.QueryTextureProjections("profile", generation, new(Search: "saved")).Entries);
    }

    [Fact]
    public async Task CachedCountsAndCurrentAliasIndexesNoticeAnotherStoreWithoutGenerationChanges()
    {
        using var fixture = await Fixture.CreateAsync(); using var first = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        long generation = first.InitializeTextureProjections("profile", [fixture.Entry("A"), fixture.Entry("B", 2)]);
        Assert.Equal(0, first.QueryTextureProjections("profile", generation, new(Search: "saved")).Total);
        using var second = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        second.SetTextureProjectionAlias("profile", fixture.Entry("B", 2).Identity, "Saved alias");
        Assert.Equal(generation, first.CaptureTextureProjectionGeneration("profile"));
        Assert.Equal(1, first.QueryTextureProjections("profile", generation, new(Search: "saved", Offset: 100)).Total);
        Assert.Equal("Saved alias", Assert.Single(first.QueryTextureProjections("profile", generation, new(Search: "saved")).Entries).Alias);
        second.ReplaceTextureProjectionAliases("profile", new Dictionary<ResourceIdentity, string>());
        Assert.Equal(0, first.QueryTextureProjections("profile", generation, new(Search: "saved")).Total);
    }

    [Fact]
    public async Task CurrentProjectionMigrationRestoresExistingRowsAliasesAndDirectCatalogTables()
    {
        using var fixture = await Fixture.CreateAsync(); var entry = fixture.Entry("Texture 12");
        var catalog = new ResourceIdentity(fixture.Snapshot.IndexContentId, 9); long generation;
        using (var original = FeatureStore.Open(fixture.Snapshot, fixture.Cache))
        {
            original.InitializeTextureProjections("profile", [entry]);
            original.SetTextureProjectionAlias("profile", entry.Identity, "Saved image");
            generation = original.CommitTextureProjectionCatalog("profile", fixture.Revision, catalog, new string('c', 64), [entry.Identity]);
            Assert.Single(original.QueryTextureProjections("profile", generation, new()).Entries);
        }
        using (var db = OpenDatabase(fixture))
        using (var remove = db.CreateCommand())
        {
            remove.CommandText = "DROP TABLE texture_projection_current;DROP TABLE texture_projection_current_fts;" +
                "DROP TABLE texture_projection_current_dirty;DROP TABLE texture_projection_current_migrations";
            remove.ExecuteNonQuery();
        }
        using var restored = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var page = restored.QueryTextureProjections("profile", generation, new(Search: "saved", CatalogKey: catalog.Key));
        Assert.Equal(1, page.Total); var value = Assert.Single(page.Entries);
        Assert.Equal(entry.Identity, value.Identity); Assert.Equal("Saved image", value.Alias); Assert.Contains(catalog.Key, value.CatalogKeys);
        Assert.Equal(generation, restored.CaptureTextureProjectionGeneration("profile"));
        Assert.Null(restored.LoadTextureProjectionDetails("profile", generation, entry.Identity));
    }

    [Fact]
    public async Task CurrentPageNeverHydratesEvidenceAndCanceledUpdateKeepsItsIndexedSnapshot()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var entry = fixture.Entry("Diffuse panel"); store.InitializeTextureProjections("profile", [entry]);
        long generation = store.CommitTextureProjectionOwner("profile", fixture.Checkpoint, [entry]);
        Assert.Single(store.QueryTextureProjections("profile", generation, new(Search: "diffuse")).Entries);
        using (var db = OpenDatabase(fixture))
        using (var corrupt = db.CreateCommand())
        { corrupt.CommandText = "UPDATE texture_projection_evidence SET payload=x'FF'"; corrupt.ExecuteNonQuery(); }
        Assert.Single(store.QueryTextureProjections("profile", generation, new(Search: "diffuse")).Entries);
        Assert.Throws<InvalidDataException>(() => store.LoadTextureProjectionDetails("profile", generation, entry.Identity));
        Assert.Throws<OperationCanceledException>(() => store.CommitTextureProjectionOwner("profile", fixture.Checkpoint,
            [entry with { Label = "Normal panel", SearchText = "Normal panel" }], new(true)));
        Assert.Equal(generation, store.CaptureTextureProjectionGeneration("profile"));
        Assert.Single(store.QueryTextureProjections("profile", generation, new(Search: "diffuse")).Entries);
    }

    [Fact]
    public async Task HeadAdvanceKeepsCapturedPagesIndexedAndEmitsGeneratedPagingMeasurements()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var entries = Enumerable.Range(1, 12000).Select(i => fixture.Entry("Texture " + i, i) with
        { Kind = i % 3 == 0 ? TextureKind.LodBuffer : TextureKind.Image }).ToArray();
        long captured = store.InitializeTextureProjections("paging", entries);
        store.PrepareTextureProjectionIndexes("paging");
        var query = new TextureDiscoveryQuery(Limit: 100);
        var first = Timed(0);
        Assert.Equal(8000, first.Total);
        var expectedNext = store.QueryTextureProjections("paging", captured, query with { Offset = 100 });
        store.CommitTextureProjectionOwner("paging", fixture.Checkpoint,
            [entries[1] with { Label = "Texture 0", SearchText = "Texture 0" }]);
        Assert.True(store.CaptureTextureProjectionGeneration("paging") > captured);
        var next = Timed(100); var later = Timed(200); var repeat = Timed(100);
        Assert.Equal(expectedNext.Entries.Select(entry => entry.Identity), next.Entries.Select(entry => entry.Identity));
        Assert.Equal(next.Entries.Select(entry => entry.Identity), repeat.Entries.Select(entry => entry.Identity));
        Assert.DoesNotContain(first.Entries, entry => next.Entries.Any(item => item.Identity == entry.Identity));
        Assert.DoesNotContain(next.Entries, entry => later.Entries.Any(item => item.Identity == entry.Identity));
        Assert.All(new[] { next, later, repeat }, page => Assert.Equal(8000, page.Total));
        using var db = OpenDatabase(fixture); using var plan = db.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN SELECT identity FROM texture_projection_history_index INDEXED BY texture_history_visible_named " +
            "WHERE revision=$revision AND profile='paging' AND kind!=2 AND generation<=$generation AND (next_generation IS NULL OR next_generation>$generation) " +
            "ORDER BY name_rank,effective_sort,ordinal,identity LIMIT 100 OFFSET 100";
        plan.Parameters.AddWithValue("$revision", SourceIdentity.DerivedCacheKey(fixture.Snapshot.IndexContentId, fixture.Snapshot.PhysicalSourceStamp));
        plan.Parameters.AddWithValue("$generation", captured);
        var steps = new List<string>(); using var reader = plan.ExecuteReader(); while (reader.Read()) steps.Add(reader.GetString(3));
        Assert.Contains(steps, step => step.Contains("texture_history_visible_named", StringComparison.Ordinal));
        Assert.DoesNotContain(steps, step => step.Contains("TEMP B-TREE", StringComparison.Ordinal) || step.Contains("CORRELATED", StringComparison.Ordinal));
        TextureDiscoveryPage Timed(long offset)
        {
            long started = Stopwatch.GetTimestamp();
            var page = store.QueryTextureProjections("paging", captured, query with { Offset = offset });
            output.WriteLine("Generated captured generation {0}, offset {1}: {2:F3} ms", captured, offset, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return page;
        }
    }

    [Fact]
    public async Task HistoricalIndexUpdatesAliasesWithoutChangingCapturedTextureVersions()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var first = fixture.Entry("Diffuse"); var second = fixture.Entry("Normal", 2);
        long captured = store.InitializeTextureProjections("history", [first, second]);
        store.PrepareTextureProjectionIndexes("history");
        long current = store.CommitTextureProjectionOwner("history", fixture.Checkpoint, [second with { Label = "Renamed", SearchText = "Renamed" }]);
        store.SetTextureProjectionAlias("history", second.Identity, "A saved alias");
        var historical = Assert.Single(store.QueryTextureProjections("history", captured, new(Search: "saved")).Entries);
        Assert.Equal("Normal", historical.Label); Assert.Equal("A saved alias", historical.Alias);
        Assert.Equal(second.Identity, store.QueryTextureProjections("history", captured, new()).Entries[0].Identity);
        Assert.Equal("Renamed", Assert.Single(store.QueryTextureProjections("history", current, new(Search: "saved")).Entries).Label);
        store.SetTextureProjectionAlias("history", second.Identity, null);
        Assert.Empty(store.QueryTextureProjections("history", captured, new(Search: "saved")).Entries);
        Assert.Equal(new[] { "Diffuse", "Normal" }, store.QueryTextureProjections("history", captured, new()).Entries.Select(entry => entry.Label));
    }

    [Fact]
    public async Task HistoricalIndexHandlesEqualOrdinalVersionsAndNewIdentitiesWithoutPageDrift()
    {
        using var fixture = await Fixture.CreateAsync(); using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var first = fixture.Entry("Same") with { Identity = new(fixture.Snapshot.IndexContentId, 1, "member/1") };
        var second = fixture.Entry("Same") with { Identity = new(fixture.Snapshot.IndexContentId, 1, "member/2") };
        long captured = store.InitializeTextureProjections("ties", [second, first]);
        store.PrepareTextureProjectionIndexes("ties");
        var before = store.QueryTextureProjections("ties", captured, new(Limit: 1));
        store.CommitTextureProjectionOwner("ties", fixture.Checkpoint,
            [second with { Label = "Changed", SearchText = "Changed" }, fixture.Entry("New", 2)]);
        var after = store.QueryTextureProjections("ties", captured, new(Limit: 1, Offset: 1));
        Assert.Equal(2, before.Total); Assert.Equal(2, after.Total);
        Assert.Equal(first.Identity, Assert.Single(before.Entries).Identity);
        Assert.Equal(second.Identity, Assert.Single(after.Entries).Identity);
        Assert.Equal("Same", after.Entries[0].Label);
    }

    private static SqliteConnection OpenDatabase(Fixture fixture)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Directory.GetFiles(fixture.Cache, "catalog-*.sqlite3").Single(), Pooling = false }.ToString());
        connection.Open(); return connection;
    }

    private static void CorruptEvidence(Fixture fixture, ResourceIdentity identity, bool validChecksum)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Directory.GetFiles(fixture.Cache, "catalog-*.sqlite3").Single(), Pooling = false }.ToString());
        connection.Open(); using var read = connection.CreateCommand();
        read.CommandText = "SELECT generation,payload FROM texture_projection_evidence WHERE identity=$identity ORDER BY generation DESC LIMIT 1";
        read.Parameters.AddWithValue("$identity", identity.Key);
        using var rows = read.ExecuteReader(); Assert.True(rows.Read()); long generation = rows.GetInt64(0);
        var payload = JsonNode.Parse((byte[])rows.GetValue(1))!; rows.Close();
        payload["Label"] = "forged";
        payload["NameCandidates"]![0]!["Evidence"]![0]!["Revision"]!["PhysicalSourceStamp"] = "foreign revision";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(payload.ToJsonString());
        using var write = connection.CreateCommand();
        write.CommandText = "UPDATE texture_projection_evidence SET payload=$payload" + (validChecksum ? ",sha256=$sha" : "") +
            " WHERE identity=$identity AND generation=$generation;UPDATE texture_projection_rows SET label='forged',search='forged',thin=json_set(thin,'$.Label','forged') WHERE identity=$identity AND generation=$generation";
        write.Parameters.AddWithValue("$identity", identity.Key); write.Parameters.AddWithValue("$generation", generation); write.Parameters.AddWithValue("$payload", bytes);
        if (validChecksum) write.Parameters.AddWithValue("$sha", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        write.ExecuteNonQuery();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root;
        private Fixture(string root, ArchiveSnapshot snapshot) { this.root = root; Snapshot = snapshot; }
        public ArchiveSnapshot Snapshot { get; }
        public string Cache => Path.Combine(root, "cache");
        public ResourceIdentity Owner => new(Snapshot.IndexContentId, 0);
        public SourceRevision Revision => new(Snapshot.IndexContentId, Snapshot.PhysicalSourceStamp);
        public SourceExtent Extent => new(Snapshot.Records[0].VolumeName, 0, 1, Convert.ToHexString(SHA256.HashData(new byte[] { 0 })).ToLowerInvariant());
        public TextureEvidence Evidence => new(Owner, Revision, "GeneratedReader", "1", Extent.PayloadSha256!, "Stored field", Extent, new(Owner, 0, 1));
        public TextureProjectionOwnerCheckpoint Checkpoint => new(Revision, Owner, 1, 0, Extent, "catalog", true, true, null, true);
        public TextureDiscoveryEntry Entry(string label, long ordinal = 1) => new(new(Snapshot.IndexContentId, ordinal), (uint)ordinal, 2229,
            label, TextureNameKind.Fallback, null, null, null, null, null, null, null, [], [], [], [], [], [], label, label);
        public static async Task<Fixture> CreateAsync(bool secondOwner = false)
        {
            string root = Path.Combine(Path.GetTempPath(), "deviant-texture-projections-" + Guid.NewGuid().ToString("N"));
            string archive = Path.Combine(root, "archive"); Directory.CreateDirectory(archive);
            int count = secondOwner ? 4 : 3;
            var index = new byte[IndexLayout.HeaderLength + count * IndexLayout.RecordLength]; IndexLayout.Magic.CopyTo(index);
            BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(20), 18); BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(101), (uint)count);
            for (int i = 0; i < count; i++)
            {
                int offset = IndexLayout.HeaderLength + i * IndexLayout.RecordLength;
                BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(offset), i is 0 or 3 ? 29u : 2229u);
                BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(offset + 8), (uint)i);
                BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(offset + 12), (uint)i);
                BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(offset + 16), 1);
            }
            string path = Path.Combine(archive, "generated.idx"); File.WriteAllBytes(path, index);
            File.WriteAllBytes(Path.Combine(archive, "generated.dat"), new byte[count]);
            return new(root, await new ArchiveReader().OpenAsync(path));
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
