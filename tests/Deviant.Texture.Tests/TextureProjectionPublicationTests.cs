using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Catalog;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureProjectionPublicationTests
{
    [Fact]
    public async Task RepeatedUnchangedOwnerPublicationKeepsTheGenerationAndStoredBytesStable()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        const string profile = "stable-publication";
        var baseline = fixture.Entry("Generated texture");
        var named = fixture.Named(baseline, 32, 16, "BC1", "Diffuse", "Room");
        store.InitializeTextureProjections(profile, [baseline]);
        long published = store.CommitTextureProjectionOwner(profile, fixture.Checkpoint, [named]);
        var before = Stored(fixture);
        for (int repeat = 0; repeat < 8; repeat++)
            Assert.Equal(published, store.CommitTextureProjectionOwner(profile, fixture.Checkpoint, [named]));
        Assert.Equal(before, Stored(fixture));
        Assert.Equal(published, store.CaptureTextureProjectionGeneration(profile));
        using var reopened = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        Assert.Equal(published, reopened.CommitTextureProjectionOwner(profile, fixture.Checkpoint, [named]));
        Assert.Equal(before, Stored(fixture));
    }

    [Fact]
    public async Task ChangedOwnerOrEntryStillPublishesAndPreservesTheEarlierGeneration()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        const string profile = "changed-publication";
        var baseline = fixture.Entry("Generated texture");
        var named = fixture.Named(baseline, 32, 16, "BC1", "Diffuse", "Room");
        store.InitializeTextureProjections(profile, [baseline]);
        long first = store.CommitTextureProjectionOwner(profile, fixture.Checkpoint, [named]);
        long changed = store.CommitTextureProjectionOwner(profile, fixture.Checkpoint, [named with { Width = 64 }]);
        Assert.True(changed > first);
        Assert.Equal(32, Assert.Single(store.QueryTextureProjections(profile, first, new()).Entries).Width);
        Assert.Equal(64, Assert.Single(store.QueryTextureProjections(profile, changed, new()).Entries).Width);
        long ownerChanged = store.CommitTextureProjectionOwner(profile,
            fixture.Checkpoint with { MembershipKey = "Changed generated membership" }, [named with { Width = 64 }]);
        Assert.True(ownerChanged > changed);
    }

    [Theory]
    [InlineData("texture_projection_evidence")]
    [InlineData("texture_projection_name_proofs")]
    public async Task IdenticalPublicationRepairsDamagedStoredPayloads(string table)
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        const string profile = "damaged-publication";
        var baseline = fixture.Entry("Generated texture");
        var named = fixture.Named(baseline, 32, 16, "BC1", "Diffuse", "Room");
        store.InitializeTextureProjections(profile, [baseline]);
        long first = store.CommitTextureProjectionOwner(profile, fixture.Checkpoint, [named]);
        using var db = Open(fixture);
        using (var damage = db.CreateCommand())
        {
            damage.CommandText = $"UPDATE {table} SET payload=x'0001'";
            Assert.Equal(1, damage.ExecuteNonQuery());
        }
        long repaired = store.CommitTextureProjectionOwner(profile, fixture.Checkpoint, [named]);
        Assert.True(repaired > first);
        using var read = db.CreateCommand();
        read.CommandText = $"SELECT payload,sha256 FROM {table} WHERE generation=$generation";
        read.Parameters.AddWithValue("$generation", repaired);
        using var rows = read.ExecuteReader(); Assert.True(rows.Read());
        Assert.Equal(rows.GetString(1), Convert.ToHexString(SHA256.HashData((byte[])rows[0])).ToLowerInvariant());
    }

    [Theory]
    [InlineData("width")]
    [InlineData("role-link")]
    [InlineData("role-flag")]
    [InlineData("proof-sources")]
    public async Task IdenticalPublicationRepairsDerivedFieldsAndProofSources(string damage)
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        const string profile = "derived-publication";
        var baseline = fixture.Entry("Generated texture");
        var named = fixture.Named(baseline, 32, 16, "BC1", "Diffuse", "Room");
        store.InitializeTextureProjections(profile, [baseline]);
        long first = store.CommitTextureProjectionOwner(profile, fixture.Checkpoint, [named]);
        using var db = Open(fixture);
        using (var corrupt = db.CreateCommand())
        {
            if (damage == "proof-sources")
            {
                using var read = db.CreateCommand(); read.CommandText = "SELECT payload FROM texture_projection_name_proofs";
                var proof = System.Text.Json.Nodes.JsonNode.Parse((byte[])read.ExecuteScalar()!)!;
                proof["Sources"] = new System.Text.Json.Nodes.JsonArray();
                byte[] encoded = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(proof);
                corrupt.CommandText = "UPDATE texture_projection_name_proofs SET payload=$payload,sha256=$hash";
                corrupt.Parameters.AddWithValue("$payload", encoded);
                corrupt.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant());
            }
            else corrupt.CommandText = damage == "width" ? "UPDATE texture_projection_rows SET width=99 WHERE generation=2" :
                damage == "role-flag" ? "UPDATE texture_projection_links SET verified=2 WHERE relation=0" :
                "DELETE FROM texture_projection_links WHERE relation=0";
            Assert.True(corrupt.ExecuteNonQuery() > 0);
        }
        long repaired = store.CommitTextureProjectionOwner(profile, fixture.Checkpoint, [named]);
        Assert.True(repaired > first);
        Assert.Equal(32, Assert.Single(store.QueryTextureProjections(profile, repaired, new()).Entries).Width);
        Assert.Equal(repaired, store.CommitTextureProjectionOwner(profile, fixture.Checkpoint, [named]));
    }

    private static SqliteConnection Open(GeneratedTextureProjectionFixture fixture)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Directory.GetFiles(fixture.Cache, "catalog-*.sqlite3").Single(), Pooling = false }.ToString());
        db.Open(); return db;
    }

    private static (long Rows, long EvidenceBytes, long ProofBytes, long Links, long History) Stored(GeneratedTextureProjectionFixture fixture)
    {
        using var db = Open(fixture); using var read = db.CreateCommand();
        read.CommandText = "SELECT (SELECT COUNT(*) FROM texture_projection_rows)," +
            "(SELECT COALESCE(SUM(length(payload)),0) FROM texture_projection_evidence)," +
            "(SELECT COALESCE(SUM(length(payload)),0) FROM texture_projection_name_proofs)," +
            "(SELECT COUNT(*) FROM texture_projection_links),(SELECT COUNT(*) FROM texture_projection_history_index)";
        using var rows = read.ExecuteReader(); Assert.True(rows.Read());
        return (rows.GetInt64(0), rows.GetInt64(1), rows.GetInt64(2), rows.GetInt64(3), rows.GetInt64(4));
    }
}
