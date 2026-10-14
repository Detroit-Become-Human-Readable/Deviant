using System.Text.Json;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Xunit;

namespace Deviant.Tests;

public sealed class ModelCatalogPagingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-model-pages", Guid.NewGuid().ToString("N"));
    private readonly SourceRevision revision = new("generated-model-index", "generated-model-stamp");
    private ArchiveSnapshot Snapshot() => new(Path.Combine(root, "source", "sample.idx"), revision.IndexContentId,
        IndexLayout.SupportedVersion, 0, [0x41], 0, [], [], new FileStamp(true, "index", 1, 1, 1, true), revision.PhysicalSourceStamp, []);
    private AssetDescriptor Model(long id, string name, string? actor = null, string state = "unknown", params string[] clips) =>
        new(new(revision.IndexContentId, id), revision, "character-model", name, null, AssetAvailability.Available,
            AssetCompleteness.Complete, AssetCapabilities.Preview, [], [])
        {
            SearchTerms = name,
            Metadata = new Dictionary<string, string>
            {
                ["model.kind"] = actor is null ? "static" : "character", ["character.key"] = actor ?? "",
                ["character.name"] = actor ?? "", ["model.animation-state"] = state,
                ["model.animation-count"] = clips.Distinct().Count().ToString(),
                ["model.animation-identities"] = JsonSerializer.Serialize(clips)
            }
        };

    private AssetDescriptor CandidateModel(long id, string name, string actorKey, string actorName, string variant, params string[] clips) =>
        new(new(revision.IndexContentId, id), revision, "character-model", name, null, AssetAvailability.Available,
            AssetCompleteness.Complete, AssetCapabilities.Preview, [], [])
        {
            SearchTerms = name,
            Metadata = new Dictionary<string, string>
            {
                ["model.kind"] = "unclassified", ["character.candidate-key"] = actorKey,
                ["character.candidate-name"] = actorName, ["character.candidate-variant"] = variant,
                ["model.animation-state"] = "complete", ["model.animation-count"] = "0",
                ["model.animation-identities"] = JsonSerializer.Serialize(clips)
            }
        };

    private AssetDescriptor NamedCharacterModel(long id, string name, string actorKey, string actorName, string state, params string[] clips)
    {
        var asset = Model(id, name, actorKey, state, clips);
        var metadata = asset.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        metadata["character.name"] = actorName;
        return asset with { Metadata = metadata };
    }

    [Fact]
    public void PageBoundaryExcludesNewEarlierNamesUntilRefresh()
    {
        using var store = FeatureStore.Open(Snapshot(), root);
        store.UpsertAssets([Model(1, "B"), Model(2, "C")]);
        var boundary = store.CaptureAssetPageBoundary();
        store.UpsertAssets([Model(3, "A")]);
        var query = new AssetQuery(Family: "character-model", Sort: AssetSort.Name, Limit: 1) { MaximumCatalogRow = boundary };
        Assert.Equal("B", Assert.Single(store.Query(query)).Label);
        Assert.Equal("C", Assert.Single(store.Query(query with { Offset = 1 })).Label);
        Assert.Equal(2, store.Count(query));
        Assert.Equal("A", Assert.Single(store.Query(query with { MaximumCatalogRow = null })).Label);
    }

    [Fact]
    public async Task ActorVariantPagesUseAnActorBoundIndexAndKeepNameOrder()
    {
        using var store = FeatureStore.Open(Snapshot(), root);
        var models = Enumerable.Range(1, 1024).Select(index => Model(index, $"Other {index:D4}", $"actor-{index}")).ToList();
        models.Add(Model(1025, "Later variant", "requested actor"));
        models.Add(Model(1026, "Earlier variant", "requested actor"));
        store.UpsertAssets(models.Select(asset => asset with
        { Metadata = new Dictionary<string, string>(asset.Metadata) { ["model.name-profile"] = "generated profile" } }));
        await store.PrepareCharacterProjectionsAsync();
        var query = new AssetQuery(Family: "character-model", Sort: AssetSort.Name, Limit: 1)
        { CharacterGroup = "requested actor", ModelNameProfile = "generated profile" };
        var first = await store.QueryCharacterModelsAsync(query);
        var second = await store.QueryCharacterModelsAsync(query with { Offset = 1 });
        Assert.Equal(2, first.Total); Assert.Equal("Earlier variant", Assert.Single(first.Models).Label);
        Assert.Equal("Later variant", Assert.Single(second.Models).Label);
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(FeatureStore)
            .GetField("characterReadConnection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(store)!;
        using var plan = connection.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN SELECT row_id,identity,payload,animation_state,animation_count FROM character_projection_models WHERE revision=$revision AND name_profile=$profile AND actor=$actor ORDER BY name_sort,identity LIMIT 1";
        plan.Parameters.AddWithValue("$revision", revision.IndexContentId + ":" + revision.PhysicalSourceStamp);
        plan.Parameters.AddWithValue("$profile", "generated profile"); plan.Parameters.AddWithValue("$actor", "requested actor");
        using var reader = plan.ExecuteReader();
        var details = new List<string>(); while (reader.Read()) details.Add(reader.GetString(3));
        Assert.Contains(details, detail => detail.Contains("actor=?", StringComparison.Ordinal));
        Assert.DoesNotContain(details, detail => detail.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    [Fact]
    public void CharacterGroupAliasesRespectTheRequestedCatalogBoundary()
    {
        using var store = FeatureStore.Open(Snapshot(), root);
        var first = Model(1, "First outfit", "actor-a") with
        { Names = [new("FirstAlias", NameOrigin.UserAlias, "generated fixture")] };
        store.UpsertAssets([first]);
        var boundary = store.CaptureAssetPageBoundary();
        var later = Model(2, "Later outfit", "actor-a") with
        { Names = [new("LaterAlias", NameOrigin.UserAlias, "generated fixture")] };
        store.UpsertAssets([later]);

        var page = store.QueryCharacterGroups(new AssetQuery(Family: "character-model")
            { MaximumCatalogRow = boundary }, out var total);

        var group = Assert.Single(page);
        Assert.Equal(1, total);
        Assert.Contains(group.Names, name => name.Text == "FirstAlias");
        Assert.DoesNotContain(group.Names, name => name.Text == "LaterAlias");
    }

    [Fact]
    public void CharacterCountsUnionSharedClipsAcrossVariantsAndKeepUnknownLast()
    {
        using var store = FeatureStore.Open(Snapshot(), root);
        var coat = Model(1, "Coat", "Actor A", "partial", "clip-1", "clip-2") with
        { Names = [new("Coat alias", NameOrigin.UserAlias, "generated fixture")] };
        var shirt = Model(2, "Shirt", "Actor A", "partial", "clip-2", "clip-3") with
        { Names = [new("Shirt alias", NameOrigin.UserAlias, "generated fixture")] };
        store.UpsertAssets([coat, shirt,
            Model(3, "Hat", "Actor B"), Model(4, "Dress", "Actor C", "complete", "clip-1")]);
        var query = new AssetQuery(Family: "character-model", Sort: AssetSort.Animations, Descending: true);
        var rows = store.QueryCharacterGroups(query, out var total);
        Assert.Equal(3, total);
        Assert.Equal(new[] { "Actor A", "Actor C", "Actor B" }, rows.Select(r => r.Metadata["character.name"]));
        Assert.Equal("3", rows[0].Metadata["model.animation-count"]);
        Assert.Equal("partial", rows[0].Metadata["model.animation-state"]);
        Assert.Equal("unknown", rows[2].Metadata["model.animation-state"]);
        Assert.Contains(rows[0].Names, name => name.Text == "Coat alias");
        Assert.Contains(rows[0].Names, name => name.Text == "Shirt alias");
        var searched = Assert.Single(store.QueryCharacterGroups(query with { Search = "Coat" }, out total));
        Assert.Equal(1, total); Assert.Equal("3", searched.Metadata["model.animation-count"]);
        Assert.Equal("Actor B", store.QueryCharacterGroups(query with { Descending = false }, out _).Last().Metadata["character.name"]);
    }

    [Fact]
    public async Task CandidateModelsAreIndexedAndPagedWithoutClaimingClassificationOrAnimationResults()
    {
        using var store = FeatureStore.Open(Snapshot(), root);
        store.UpsertAssets([
            CandidateModel(1, "Actor A head candidate", "actor-a", "Actor A", "Head", "validated-clip"),
            CandidateModel(2, "Actor A body candidate", "actor-a", "Actor A", "Body", "validated-clip"),
            NamedCharacterModel(3, "Actor A validated outfit", "actor-a", "Actor A", "complete", "validated-clip"),
            CandidateModel(4, "Actor B head candidate", "actor-b", "Actor B", "Head", "candidate-only-clip")
        ]);

        // The candidate clip must not decrement a validated row's shared reference count during replacement.
        store.UpsertAssets([NamedCharacterModel(1, "Actor A checked head", "actor-a", "Actor A", "complete", "validated-clip")]);
        store.UpsertAssets([Model(1, "Unclassified static head")]);

        var query = new AssetQuery(Family: "character-model", Sort: AssetSort.Name, Limit: 1);
        var firstPage = await store.QueryCharacterGroupsAsync(query);
        Assert.Equal(2, firstPage.Total);
        Assert.Equal(1, firstPage.PartialGroupCount);
        Assert.Equal(1, firstPage.UnknownGroupCount);
        Assert.Equal(1, firstPage.KnownClipCount);
        var validatedGroup = Assert.Single(firstPage.Groups);
        Assert.Equal("character", validatedGroup.Metadata["model.kind"]);
        Assert.Equal("actor-a", validatedGroup.Metadata["character.key"]);
        Assert.Equal("Actor A", validatedGroup.Metadata["character.name"]);
        Assert.False(validatedGroup.Metadata.ContainsKey("character.candidate-needs-detail-check"));

        var secondPage = await store.QueryCharacterGroupsAsync(query with { Offset = 1 });
        var candidateGroup = Assert.Single(secondPage.Groups);
        Assert.Equal("unclassified", candidateGroup.Metadata["model.kind"]);
        Assert.False(candidateGroup.Metadata.ContainsKey("character.key"));
        Assert.False(candidateGroup.Metadata.ContainsKey("character.name"));
        Assert.Equal("actor-b", candidateGroup.Metadata["character.candidate-key"]);
        Assert.Equal("Actor B", candidateGroup.Metadata["character.candidate-name"]);
        Assert.Equal("Head", candidateGroup.Metadata["character.candidate-variant"]);
        Assert.Equal("true", candidateGroup.Metadata["character.candidate-needs-detail-check"]);
        Assert.Equal("unknown", candidateGroup.Metadata["model.animation-state"]);
        Assert.False(candidateGroup.Metadata.ContainsKey("model.animation-count"));

        var searched = await store.QueryCharacterGroupsAsync(query with { Search = "body candidate", Limit = 10 });
        Assert.Equal(1, searched.Total);
        Assert.Equal("actor-a", Assert.Single(searched.Groups).Metadata["character.key"]);

        var candidateModels = await store.QueryCharacterModelsAsync(new AssetQuery(Family: "character-model", Limit: 10)
        { CharacterGroup = "actor-a", ModelKind = "unclassified" });
        Assert.Single(candidateModels.Models);
        var candidateModel = candidateModels.Models[0];
        Assert.Equal("Body", candidateModel.Metadata["character.candidate-variant"]);
        Assert.Equal("unclassified", candidateModel.Metadata["model.kind"]);
        Assert.Equal("unknown", candidateModel.Metadata["model.animation-state"]);
        Assert.False(candidateModel.Metadata.ContainsKey("model.animation-count"));
    }

    [Fact]
    public void ModelsAnimationSortKeepsUnknownLastAndUsesUniqueCounts()
    {
        using var store = FeatureStore.Open(Snapshot(), root);
        store.UpsertAssets([Model(1, "A"), Model(2, "B", null, "partial", "shared", "shared"),
            Model(3, "C", null, "complete", "first", "second")]);
        var rows = store.Query(new(Family: "character-model", Sort: AssetSort.Animations, Descending: true));
        Assert.Equal(new[] { "C", "B", "A" }, rows.Select(r => r.Label));
    }

    [Fact]
    public void CharacterGroupSearchKeepsNamesAndSemanticMetadataButSkipsTechnicalValues()
    {
        using var store = FeatureStore.Open(Snapshot(), root);
        var source = Model(10, "Catalog model label", "aurora-vale", "unknown") with
        {
            Identity = new(revision.IndexContentId, 10, "archive/member/member-path-token"),
            SearchTerms = "base-search-token member-path-token",
            Names = [new("StoredNameToken", NameOrigin.Stored, "generated fixture"),
                new("UserAliasToken", NameOrigin.UserAlias, "generated fixture")]
        };
        var metadata = source.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        metadata["character.key"] = "aurora-vale";
        metadata["character.name"] = "Aurora Vale";
        metadata["model.variant"] = "Silver Scout Variant";
        metadata["model.source-label"] = "Source Label Token";
        metadata["NativeId"] = "429";
        metadata["NativeType"] = "2013";
        metadata["model.name-profile"] = "opaque-name-profile-token";
        metadata["model.classification-profile"] = "opaque-classification-profile-token";
        metadata["model.inventory-profile"] = "opaque-inventory-profile-token";
        metadata["model.classification-evidence"] = "opaque-classification-evidence-token";
        metadata["model.animation-identities"] = "[\"opaque-animation-identity-token\"]";
        metadata["StoredNameCount"] = "87654321";
        var candidate = CandidateModel(11, "Candidate model label", "candidate-actor", "Candidate Name", "Candidate Variant");
        store.UpsertAssets([source with { Metadata = metadata }, candidate]);
        var query = new AssetQuery(Family: "character-model", Limit: 1);
        long Count(string search)
        {
            _ = store.QueryCharacterGroups(query with { Search = search }, out var total);
            return total;
        }

        foreach (var term in new[] { "StoredNameToken", "UserAliasToken", "member-path-token", "aurora-vale",
                     "Silver Scout", "Source Label Token", "429", "2013", "candidate-actor",
                     "Candidate Name", "Candidate Variant" })
            Assert.True(Count(term) == 1, $"Expected one character group for the searchable term '{term}'.");
        foreach (var term in new[] { "opaque-name-profile-token", "opaque-classification-profile-token",
                     "opaque-inventory-profile-token", "opaque-classification-evidence-token",
                     "opaque-animation-identity-token", "87654321" })
            Assert.True(Count(term) == 0, $"Technical metadata term '{term}' should not be searchable.");
    }

    [Fact]
    public void ReplacingACharacterModelWithAudioRemovesItsProjectionAndSharedClipReference()
    {
        using var store = FeatureStore.Open(Snapshot(), root);
        var removed = Model(20, "First outfit", "actor-a", "partial", "shared-clip");
        var retained = Model(21, "Second outfit", "actor-b", "complete", "shared-clip");
        store.UpsertAssets([removed, retained]);
        var audioReplacement = new AssetDescriptor(removed.Identity, revision, "audio-media", "Moved audio", null,
            AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.None, [], []);

        store.UpsertAssets([audioReplacement]);

        var groups = store.QueryCharacterGroups(new AssetQuery(Family: "character-model"), out var total);
        Assert.Equal(1, total);
        var group = Assert.Single(groups);
        Assert.Equal("actor-b", group.Metadata["character.key"]);
        Assert.Equal("1", group.Metadata["model.animation-count"]);
        Assert.Equal(audioReplacement.Identity, Assert.Single(store.Query(new AssetQuery(Family: "audio-media"))).Identity);
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
