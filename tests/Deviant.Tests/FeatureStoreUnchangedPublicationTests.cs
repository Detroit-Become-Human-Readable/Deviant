using System.Reflection;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Tests;

public sealed class FeatureStoreUnchangedPublicationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-publication-tests", Guid.NewGuid().ToString("N"));
    private string CacheRoot => Path.Combine(root, "cache");

    [Fact]
    public void ExactIdentityAndSemanticLookupKeysSelectTheirOwnRows()
    {
        using var store=FeatureStore.Open(Snapshot(),CacheRoot);
        var first=Asset();
        var other=first with { Identity=new(first.Identity.IndexContentId,first.Identity.Ordinal+1),LookupKey=first.Identity.Key };
        store.UpsertAssets([first,other]);
        Assert.Equal(first.Identity,Assert.Single(store.Query(new(Family:first.Family){ExactIdentityKey=first.Identity.Key})).Identity);
        Assert.Equal(other.Identity,Assert.Single(store.Query(new(Family:first.Family,ExactKey:first.Identity.Key))).Identity);
        Assert.Equal(1,store.Count(new(Family:first.Family){ExactIdentityKey=first.Identity.Key}));
        Assert.Equal(0,store.Count(new(Family:first.Family){ExactIdentityKey=first.Identity.Key+"0"}));
    }

    [Fact]
    public void IdenticalPublicationReturnsItsIdentityWithoutDatabaseChanges()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var write = Write(Asset());
        Assert.Contains(write.Asset.Identity, store.PublishBoundedAssetsBatch([write]));
        var before = Changes(store);

        Assert.Contains(write.Asset.Identity, store.PublishBoundedAssetsBatch([write]));

        Assert.Equal(before, Changes(store));
        Assert.Equal("1", Assert.Single(store.QueryCharacterGroups(new(Family: "character-model"), out _))
            .Metadata["model.animation-count"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SwitchingPublicationApisKeepsAnUnchangedAssetGeneration(bool boundedFirst)
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var asset = Asset();
        var scope = new LinkedFeatureScope("generated-head", "v1", "model", "v1", asset.Family);
        if (boundedFirst) store.PublishBoundedAssetsBatch([Write(asset)]);
        else store.UpsertAssets([asset]);
        var before = store.CaptureLinkedFeatureDependencies(scope);
        var payload = Scalar(store, "SELECT payload FROM assets_v2 WHERE identity=$identity", asset.Identity.Key);

        if (boundedFirst) store.UpsertAssets([asset]);
        else store.PublishBoundedAssetsBatch([Write(asset)]);

        Assert.Equal(payload, Scalar(store, "SELECT payload FROM assets_v2 WHERE identity=$identity", asset.Identity.Key));
        Assert.Equal(before.FamilyEpoch, store.CaptureLinkedFeatureDependencies(scope).FamilyEpoch);
        Assert.Equal(asset.Identity, Assert.Single(store.Query(new(Family: asset.Family))).Identity);
    }

    [Theory]
    [InlineData("v1", "changed evidence")]
    [InlineData("v2", "evidence")]
    public void ChangedFeatureDoesNotRewriteAnIdenticalAsset(string version, string evidence)
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var asset = Asset();
        store.PublishBoundedAssetsBatch([Write(asset)]);
        var before = Changes(store);

        Assert.Contains(asset.Identity, store.PublishBoundedAssetsBatch([Write(asset, version, evidence)]));

        Assert.Equal(before + 1, Changes(store));
        Assert.Equal(evidence, store.LoadBounded<string>("model/1", version, 1024));
    }

    [Theory]
    [InlineData("alias")]
    [InlineData("profile")]
    [InlineData("proof")]
    [InlineData("search")]
    [InlineData("lookup")]
    public void ChangedAssetFieldsPublishWhileUnchangedEvidenceIsRetained(string field)
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var asset = Asset();
        store.PublishBoundedAssetsBatch([Write(asset)]);
        var before = Changes(store);
        var changed = field switch
        {
            "alias" => asset with { Label = "Navigator", Names = [new("Navigator", NameOrigin.UserAlias, "generated alias")] },
            "profile" => asset with { Metadata = new Dictionary<string, string>(asset.Metadata) { ["model.name-profile"] = "names-v2" } },
            "proof" => asset with { Extents = [new("sample.dat", 0, 16, new string('b', 64))] },
            "search" => asset with { SearchTerms = "unique search term" },
            "lookup" => asset with { LookupKey = "changed-lookup" },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        Assert.Contains(asset.Identity, store.PublishBoundedAssetsBatch([Write(changed)]));

        Assert.True(Changes(store) > before);
        var selected = Assert.Single(store.Query(new(Family: "character-model")));
        Assert.Equal(changed.Label, selected.Label);
        Assert.Equal(changed.Metadata["model.name-profile"], selected.Metadata["model.name-profile"]);
        Assert.Equal(changed.Extents, selected.Extents);
        if (field == "search") Assert.Single(store.Query(new(Search: "unique search term", Family: "character-model")));
        if (field == "lookup") Assert.Equal("changed-lookup", Scalar(store,
            "SELECT lookup_key FROM assets_v2 WHERE identity=$identity", asset.Identity.Key));
        var after = Changes(store);
        Assert.Contains(asset.Identity, store.PublishBoundedAssetsBatch([Write(changed)]));
        Assert.Equal(after, Changes(store));
    }

    [Fact]
    public void MissingFeatureIsRestoredWithoutRewritingTheAsset()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var write = Write(Asset());
        store.PublishBoundedAssetsBatch([write]);
        Execute(store, "DELETE FROM asset_features WHERE feature='model/1'");
        var before = Changes(store);

        Assert.Contains(write.Asset.Identity, store.PublishBoundedAssetsBatch([write]));

        Assert.Equal(before + 1, Changes(store));
        Assert.Equal("evidence", store.LoadBounded<string>("model/1", "v1", 1024));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingProjectionIsRepairedWithoutDowngradingACompleteModel(bool incomingInventory)
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var complete = Asset() with { SearchTerms = "retained searchable term", LookupKey = "retained-lookup" };
        store.PublishBoundedAssetsBatch([Write(complete)]);
        Execute(store, "DELETE FROM character_projection_models");
        var incoming = incomingInventory
            ? complete with { Label = "Inventory row", Completeness = AssetCompleteness.Partial,
                Metadata = new Dictionary<string, string>(complete.Metadata) { ["model.kind"] = "unclassified" },
                SearchTerms = "inventory search", LookupKey = "inventory-lookup" }
            : complete;

        Assert.Contains(complete.Identity, store.PublishBoundedAssetsBatch([Write(incoming)]));

        var selected = Assert.Single(store.Query(new(Family: "character-model")));
        Assert.Equal(complete.Label, selected.Label);
        Assert.Equal(AssetCompleteness.Complete, selected.Completeness);
        Assert.Equal("character", selected.Metadata["model.kind"]);
        Assert.Single(store.QueryCharacterGroups(new(Search: "retained searchable term", Family: "character-model"), out _));
        Assert.Equal(1L, Scalar(store, "SELECT ref_count FROM character_projection_clips WHERE clip_identity='generated-clip' AND $identity IS NOT NULL", complete.Identity.Key));
        Assert.Equal("retained-lookup", Scalar(store,
            "SELECT lookup_key FROM assets_v2 WHERE identity=$identity", complete.Identity.Key));
        var before = Changes(store);
        Assert.Contains(complete.Identity, store.PublishBoundedAssetsBatch([Write(incoming)]));
        Assert.Equal(before, Changes(store));
    }

    [Fact]
    public void FamilyConflictsStillRejectEvidenceAndAssetPublication()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var original = Asset();
        store.PublishBoundedAssetsBatch([Write(original)]);
        var before = Changes(store);

        Assert.Empty(store.PublishBoundedAssetsBatch([Write(original with { Family = "other", Label = "Replacement" }, "v2", "replacement")]));

        Assert.Equal(before, Changes(store));
        Assert.Equal("evidence", store.LoadBounded<string>("model/1", "v1", 1024));
        Assert.Null(store.LoadBounded<string>("model/1", "v2", 1024));
        Assert.Equal(original.Label, Assert.Single(store.Query(new(Family: "character-model"))).Label);
    }

    [Fact]
    public void PublicationFollowsAnExplicitFamilyTransitionWithoutRestoringOldProjections()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var original = Asset();
        store.PublishBoundedAssetsBatch([Write(original)]);
        var transitioned = original with { Family = "other", Label = "Other asset" };
        store.UpsertAssets([transitioned]);

        Assert.Empty(store.PublishBoundedAssetsBatch([Write(original, "v2", "rejected")]));
        Assert.Contains(transitioned.Identity, store.PublishBoundedAssetsBatch([Write(transitioned, "v2", "accepted")]));

        Assert.Empty(store.Query(new(Family: "character-model")));
        Assert.Empty(store.QueryCharacterGroups(new(Family: "character-model"), out _));
        Assert.Equal("accepted", store.LoadBounded<string>("model/1", "v2", 1024));
        var before = Changes(store);
        store.PublishBoundedAssetsBatch([Write(transitioned, "v2", "accepted")]);
        Assert.Equal(before, Changes(store));
    }

    [Fact]
    public void MissingProjectionRepairKeepsSharedClipReferenceCountsAccurate()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var first = Asset();
        var second = first with { Identity = first.Identity with { Ordinal = 2 } };
        store.PublishBoundedAssetsBatch([Write(first), Write(second)]);
        using (var remove = Connection(store).CreateCommand())
        {
            remove.CommandText = "DELETE FROM character_projection_models WHERE identity=$identity";
            remove.Parameters.AddWithValue("$identity", first.Identity.Key); remove.ExecuteNonQuery();
        }

        store.PublishBoundedAssetsBatch([Write(first)]);
        Assert.Equal(2L, Scalar(store, "SELECT ref_count FROM character_projection_clips WHERE clip_identity='generated-clip' AND $identity IS NOT NULL", first.Identity.Key));
        var changed = first with { Metadata = new Dictionary<string, string>(first.Metadata)
            { ["model.animation-identities"] = "[\"other-clip\"]" } };
        store.PublishBoundedAssetsBatch([Write(changed)]);

        Assert.Equal(1L, Scalar(store, "SELECT ref_count FROM character_projection_clips WHERE clip_identity='generated-clip' AND $identity IS NOT NULL", first.Identity.Key));
        Assert.Equal("2", Assert.Single(store.QueryCharacterGroups(new(Family: "character-model"), out _)).Metadata["model.animation-count"]);
        var before = Changes(store);
        store.PublishBoundedAssetsBatch([Write(changed), Write(second)]);
        Assert.Equal(before, Changes(store));
    }

    [Fact]
    public void SourceRevisionsRemainSeparateAndMismatchedPublicationIsRejected()
    {
        using var first = FeatureStore.Open(Snapshot(), CacheRoot);
        var original = Asset();
        first.PublishBoundedAssetsBatch([Write(original)]);
        var before = Changes(first);
        var replacement = original with { Revision = new(original.Revision.IndexContentId, "replacement-stamp"), Label = "Replacement source" };
        Assert.Throws<ArchiveException>(() => first.PublishBoundedAssetsBatch([Write(replacement)]));
        Assert.Equal(before, Changes(first));

        using var second = FeatureStore.Open(Snapshot("replacement-stamp"), CacheRoot);
        Assert.Contains(replacement.Identity, second.PublishBoundedAssetsBatch([Write(replacement)]));
        Assert.Equal(original.Label, Assert.Single(first.Query(new(Family: "character-model"))).Label);
        Assert.Equal(replacement.Label, Assert.Single(second.Query(new(Family: "character-model"))).Label);
    }

    [Fact]
    public void IdenticalOwnerAndCheckpointBatchDoesNotModifyTheDatabase()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        BoundedFeatureWrite[] writes = [BoundedFeatureWrite.Create("owner", "v1", "members", 1024),
            BoundedFeatureWrite.Create("checkpoint", "v1", "complete", 1024)];
        store.SaveBoundedBatch(writes);
        var before = Changes(store);

        store.SaveBoundedBatch(writes);

        Assert.Equal(before, Changes(store));
    }

    [Fact]
    public void FullMixedBatchPreservesPerIdentityStateAndRejectsFamilyConflicts()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var template = Asset();
        var originals = Enumerable.Range(0, FeatureStore.BoundedAssetPublicationLimit).Select(index =>
            template with
            {
                Identity = template.Identity with { Ordinal = index + 1 },
                Family = index % 8 is 4 or 5 or 6 ? "generated-media" : "character-model",
                Label = "Generated asset " + index,
                SearchTerms = "generated search " + index,
                SortKey = "generated-sort-" + index,
                LookupKey = (index + index / 8) % 2 == 0 ? null : "generated-lookup-" + index
            }).ToArray();
        var initial = originals.Where((_, index) => index % 8 is not (2 or 6)).Select(asset => Write(asset)).ToArray();
        store.PublishBoundedAssetsBatch(initial);
        foreach (var original in originals.Where((_, index) => index % 8 == 7))
        {
            using var remove = Connection(store).CreateCommand();
            remove.CommandText = "DELETE FROM character_projection_models WHERE revision=$revision AND identity=$identity";
            remove.Parameters.AddWithValue("$revision", SourceIdentity.DerivedCacheKey(
                original.Revision.IndexContentId, original.Revision.PhysicalSourceStamp));
            remove.Parameters.AddWithValue("$identity", original.Identity.Key);
            Assert.Equal(1, remove.ExecuteNonQuery());
        }

        var writes = originals.Select((original, index) =>
        {
            var incoming = (index % 8) switch
            {
                1 or 5 => original with
                {
                    Label = "Changed asset " + index,
                    SearchTerms = "changed search " + index,
                    SortKey = "changed-sort-" + index,
                    LookupKey = original.LookupKey is null ? "changed-lookup-" + index : null,
                    Completeness = index % 8 == 5 ? AssetCompleteness.Partial : AssetCompleteness.Complete
                },
                3 => original with { Family = "conflicting-family", Label = "Rejected asset " + index },
                7 => original with
                {
                    Label = "Inventory asset " + index,
                    SearchTerms = "inventory search " + index,
                    LookupKey = null,
                    Completeness = AssetCompleteness.Partial,
                    Metadata = new Dictionary<string, string>(original.Metadata) { ["model.kind"] = "unclassified" }
                },
                _ => original
            };
            return index % 8 is 0 or 4 ? Write(incoming) : Write(incoming, "v2", "changed evidence " + index);
        }).ToArray();

        var accepted = store.PublishBoundedAssetsBatch(writes);

        var expectedAccepted = originals.Where((_, index) => index % 8 != 3).Select(asset => asset.Identity).ToHashSet();
        Assert.True(expectedAccepted.SetEquals(accepted));
        var actual = store.Query(new(Limit: FeatureStore.BoundedAssetPublicationLimit)).ToDictionary(asset => asset.Identity);
        Assert.Equal(FeatureStore.BoundedAssetPublicationLimit, actual.Count);
        for (var index = 0; index < originals.Length; index++)
        {
            var original = originals[index];
            var expected = index % 8 is 3 or 7 ? original : writes[index].Asset;
            var selected = actual[original.Identity];
            Assert.Equal(expected.Family, selected.Family);
            Assert.Equal(expected.Label, selected.Label);
            Assert.Equal(expected.Completeness, selected.Completeness);
            Assert.Equal(expected.Metadata["model.kind"], selected.Metadata["model.kind"]);
            Assert.Equal((object?)expected.LookupKey ?? DBNull.Value, Scalar(store,
                "SELECT lookup_key FROM assets_v2 WHERE identity=$identity", original.Identity.Key));
            Assert.Equal(expected.SearchTerms, Scalar(store,
                "SELECT search FROM assets_v2 WHERE identity=$identity", original.Identity.Key));
            Assert.Equal(expected.SortKey, Scalar(store,
                "SELECT sort_key FROM assets_v2 WHERE identity=$identity", original.Identity.Key));
            Assert.Equal(expected.Family == "character-model" ? 1L : 0L, Scalar(store,
                "SELECT COUNT(*) FROM character_projection_models WHERE identity=$identity", original.Identity.Key));
            var featureKey = "model/" + original.Identity.Ordinal;
            if (index % 8 is 0 or 3 or 4)
            {
                Assert.Equal("evidence", store.LoadBounded<string>(featureKey, "v1", 1024));
                Assert.Null(store.LoadBounded<string>(featureKey, "v2", 1024));
            }
            else
            {
                Assert.Null(store.LoadBounded<string>(featureKey, "v1", 1024));
                Assert.Equal("changed evidence " + index, store.LoadBounded<string>(featureKey, "v2", 1024));
            }
        }

        var beforeRepeat = Changes(store);
        Assert.True(expectedAccepted.SetEquals(store.PublishBoundedAssetsBatch(writes)));
        Assert.Equal(beforeRepeat, Changes(store));
    }

    [Fact]
    public void CancellationDuringOwnerUpdateRollsBackItsCheckpointTransaction()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        store.SaveBoundedBatch([BoundedFeatureWrite.Create("owner", "v1", "original members", 1024),
            BoundedFeatureWrite.Create("checkpoint", "v1", "original checkpoint", 1024)]);
        using var cancelled = new CancellationTokenSource();
        Connection(store).CreateFunction("cancel_publication", () => { cancelled.Cancel(); return 0; });
        Execute(store, "CREATE TEMP TRIGGER cancel_owner AFTER UPDATE ON asset_features WHEN new.feature='owner' BEGIN SELECT cancel_publication(); END");

        Assert.Throws<OperationCanceledException>(() => store.SaveBoundedBatch(
            [BoundedFeatureWrite.Create("owner", "v2", "replacement members", 1024),
             BoundedFeatureWrite.Create("checkpoint", "v2", "replacement checkpoint", 1024)], cancelled.Token));

        Assert.Equal("original members", store.LoadBounded<string>("owner", "v1", 1024));
        Assert.Equal("original checkpoint", store.LoadBounded<string>("checkpoint", "v1", 1024));
        Assert.Null(store.LoadBounded<string>("owner", "v2", 1024));
        Assert.Null(store.LoadBounded<string>("checkpoint", "v2", 1024));
    }

    private static BoundedFeatureAssetWrite Write(AssetDescriptor asset, string version = "v1", string evidence = "evidence") =>
        new(BoundedFeatureWrite.Create("model/" + asset.Identity.Ordinal, version, evidence, 1024), asset);

    private static AssetDescriptor Asset() => new(new("publication-index", 1), new("publication-index", "publication-stamp"),
        "character-model", "Pilot", null, AssetAvailability.Available, AssetCompleteness.Complete,
        AssetCapabilities.None, [new("Pilot", NameOrigin.Stored, "generated name")], [new("sample.dat", 0, 16, new string('a', 64))])
    {
        Metadata = new Dictionary<string, string>
        {
            ["model.kind"] = "character", ["character.key"] = "pilot", ["character.name"] = "Pilot",
            ["model.name-profile"] = "names-v1", ["model.classification-profile"] = "classification-v1",
            ["model.animation-state"] = "complete", ["model.animation-identities"] = "[\"generated-clip\"]"
        }
    };

    private ArchiveSnapshot Snapshot(string stamp = "publication-stamp") => new(Path.Combine(root, "archive", "sample.idx"),
        "publication-index", IndexLayout.SupportedVersion, 0, [0x41], 0, [], [],
        new FileStamp(true, "index", 1, 1, 1, true), stamp, []);

    private static SqliteConnection Connection(FeatureStore store) => (SqliteConnection)typeof(FeatureStore)
        .GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

    private static long Changes(FeatureStore store)
    {
        using var command = Connection(store).CreateCommand();
        command.CommandText = "SELECT total_changes()";
        return (long)command.ExecuteScalar()!;
    }

    private static object? Scalar(FeatureStore store, string sql, string identity)
    {
        using var command = Connection(store).CreateCommand();
        command.CommandText = sql; command.Parameters.AddWithValue("$identity", identity);
        return command.ExecuteScalar();
    }

    private static void Execute(FeatureStore store, string sql)
    {
        using var command = Connection(store).CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        for (int attempt = 0; Directory.Exists(root); attempt++)
        {
            try { Directory.Delete(root, recursive: true); return; }
            catch (IOException error) when (attempt < 20 && (error.HResult & 0xffff) is 32 or 33)
            { Thread.Sleep(25); }
        }
    }
}
