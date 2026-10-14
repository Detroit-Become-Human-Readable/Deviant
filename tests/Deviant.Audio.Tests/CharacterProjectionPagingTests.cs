using System.Text.Json;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class CharacterProjectionPagingTests
{
    [Fact]
    public async Task CharacterGroupsAggregateDistinctClipsAndKeepUnknownPartialAndCompleteStates()
    {
        using var generated = await EmptyArchive.CreateAsync();
        var cacheRoot = NewCacheRoot();
        try
        {
            using var store = FeatureStore.Open(generated.Snapshot, cacheRoot);
            store.UpsertAssets([
                Model(generated.Snapshot, 1, "actor-a", "Actor Alpha", "profile-current", "class-current", ["clip-a", "clip-b"], "partial", "ActorAlias"),
                Model(generated.Snapshot, 2, "actor-a", "Actor Alpha", "profile-current", "class-current", ["clip-b", "clip-c"], "complete"),
                Model(generated.Snapshot, 3, "actor-b", "Actor Beta", "profile-current", "class-current", [], "unknown"),
                Model(generated.Snapshot, 4, "actor-c", "Actor Gamma", "profile-current", "class-current", [], "complete")
            ]);

            await store.PrepareCharacterProjectionsAsync();
            var page = await store.QueryCharacterGroupsAsync(new AssetQuery(
                Search: "actoralias", Family: "character-model", Limit: 10)
                { ModelKind = "character", ModelNameProfile = "profile-current" });

            Assert.Equal(1, page.Total);
            var group = Assert.Single(page.Groups);
            Assert.Equal("actor-a", group.Metadata["character.key"]);
            Assert.Equal("Actor Alpha", group.Metadata["character.name"]);
            Assert.Equal("3", group.Metadata["model.animation-count"]);
            Assert.Equal("partial", group.Metadata["model.animation-state"]);
            Assert.Equal(1, page.PartialGroupCount);
            Assert.Equal(0, page.UnknownGroupCount);
            Assert.Equal(0, page.CompleteGroupCount);

            var models = await store.QueryCharacterModelsAsync(new AssetQuery(
                Search: "actoralias", Family: "character-model", Limit: 10)
                { ModelKind = "character", ModelNameProfile = "profile-current" });
            Assert.Equal(1, models.Total);
            Assert.Contains("ActorAlias", Assert.Single(models.Models).Names.Select(name => name.Text));
        }
        finally { DeleteCacheRoot(cacheRoot); }
    }

    [Fact]
    public async Task CharacterGroupPagesUseNaturalNamesProfilesAndStableCatalogBoundary()
    {
        using var generated = await EmptyArchive.CreateAsync();
        var cacheRoot = NewCacheRoot();
        try
        {
            using var store = FeatureStore.Open(generated.Snapshot, cacheRoot);
            store.UpsertAssets([
                Model(generated.Snapshot, 1, "actor-10", "Actor 10", "profile-current", "class-current", [], "complete"),
                Model(generated.Snapshot, 2, "actor-2", "Actor 2", "profile-current", "class-current", [], "complete"),
                Model(generated.Snapshot, 3, "actor-old", "Old Profile", "profile-legacy", "class-current", [], "complete")
            ]);
            await store.PrepareCharacterProjectionsAsync();
            var boundary = store.CaptureAssetPageBoundary();

            var first = await store.QueryCharacterGroupsAsync(new AssetQuery(
                Family: "character-model", Limit: 1)
                { ModelKind = "character", ModelNameProfile = "profile-current", MaximumCatalogRow = boundary });
            store.UpsertAssets([Model(generated.Snapshot, 4, "actor-1", "Actor 1", "profile-current", "class-current", [], "complete")]);
            var second = await store.QueryCharacterGroupsAsync(new AssetQuery(
                Offset: 1, Family: "character-model", Limit: 1)
                { ModelKind = "character", ModelNameProfile = "profile-current", MaximumCatalogRow = boundary });

            Assert.Equal("Actor 2", Assert.Single(first.Groups).Metadata["character.name"]);
            Assert.Equal("Actor 10", Assert.Single(second.Groups).Metadata["character.name"]);
            Assert.Equal(2, first.Total);
            Assert.Equal(first.Total, second.Total);

            var models = await store.QueryCharacterModelsAsync(new AssetQuery(
                Search: "Actor", Family: "character-model", Limit: 10)
                { ModelKind = "character", ModelNameProfile = "profile-current", MaximumCatalogRow = boundary });
            Assert.Equal(2, models.Total);
            Assert.Equal(new[] { "Actor 2", "Actor 10" }, models.Models.Select(model => model.Label));
        }
        finally { DeleteCacheRoot(cacheRoot); }
    }

    [Fact]
    public async Task StaticModelsAppearInModelPagesButNeverInCharacterGroups()
    {
        using var generated = await EmptyArchive.CreateAsync();
        var cacheRoot = NewCacheRoot();
        try
        {
            using var store = FeatureStore.Open(generated.Snapshot, cacheRoot);
            store.UpsertAssets([
                Model(generated.Snapshot, 1, "", "Static Prop", "profile-current", "class-current", [], "unknown", modelKind: "static")
            ]);
            await store.PrepareCharacterProjectionsAsync();

            var models = await store.QueryCharacterModelsAsync(new AssetQuery(
                Family: "character-model", Limit: 10) { ModelKind = "static" });
            var groups = await store.QueryCharacterGroupsAsync(new AssetQuery(
                Family: "character-model", Limit: 10));

            Assert.Equal(1, models.Total);
            Assert.Equal("static", Assert.Single(models.Models).Metadata["model.kind"]);
            Assert.Empty(groups.Groups);
            Assert.Equal(0, groups.Total);
        }
        finally { DeleteCacheRoot(cacheRoot); }
    }

    [Fact]
    public async Task GroupPagesKeepCapturedBoundaryWhenNewModelChangesExistingActor()
    {
        using var generated = await EmptyArchive.CreateAsync();
        var cacheRoot = NewCacheRoot();
        try
        {
            using var store = FeatureStore.Open(generated.Snapshot, cacheRoot);
            store.UpsertAssets([
                Model(generated.Snapshot, 1, "actor-a", "Actor Alpha", "profile-current", "class-current", ["clip-shared"], "complete")
            ]);
            await store.PrepareCharacterProjectionsAsync();
            var oldBoundary = store.CaptureAssetPageBoundary();

            store.UpsertAssets([
                Model(generated.Snapshot, 2, "actor-a", "Actor Alpha", "profile-current", "class-current",
                    ["clip-shared", "clip-new"], "partial", "LaterAlias")
            ]);

            var oldPage = await store.QueryCharacterGroupsAsync(new AssetQuery(Family: "character-model", Limit: 10)
                { ModelKind = "character", ModelNameProfile = "profile-current", MaximumCatalogRow = oldBoundary });
            var oldAliasSearch = await store.QueryCharacterGroupsAsync(new AssetQuery(
                Search: "LaterAlias", Family: "character-model", Limit: 10)
                { ModelKind = "character", ModelNameProfile = "profile-current", MaximumCatalogRow = oldBoundary });
            var newBoundary = store.CaptureAssetPageBoundary();
            var newPage = await store.QueryCharacterGroupsAsync(new AssetQuery(Family: "character-model", Limit: 10)
                { ModelKind = "character", ModelNameProfile = "profile-current", MaximumCatalogRow = newBoundary });
            var newAliasSearch = await store.QueryCharacterGroupsAsync(new AssetQuery(
                Search: "LaterAlias", Family: "character-model", Limit: 10)
                { ModelKind = "character", ModelNameProfile = "profile-current", MaximumCatalogRow = newBoundary });

            var oldGroup = Assert.Single(oldPage.Groups);
            Assert.Equal("1", oldGroup.Metadata["model.animation-count"]);
            Assert.Equal("complete", oldGroup.Metadata["model.animation-state"]);
            Assert.Equal(0, oldAliasSearch.Total);
            var newGroup = Assert.Single(newPage.Groups);
            Assert.Equal("2", newGroup.Metadata["model.animation-count"]);
            Assert.Equal("partial", newGroup.Metadata["model.animation-state"]);
            Assert.Equal(1, newAliasSearch.Total);
        }
        finally { DeleteCacheRoot(cacheRoot); }
    }

    private static AssetDescriptor Model(ArchiveSnapshot snapshot, long ordinal, string characterKey, string characterName,
        string nameProfile, string classificationProfile, IReadOnlyList<string> animationIdentities, string state,
        string? alias = null, string modelKind = "character")
    {
        var identity = new ResourceIdentity(snapshot.IndexContentId, ordinal, "generated/model");
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["model.kind"] = modelKind,
            ["model.name-profile"] = nameProfile,
            ["model.classification-profile"] = classificationProfile,
            ["model.animation-identities"] = JsonSerializer.Serialize(animationIdentities),
            ["model.animation-state"] = state
        };
        if (modelKind == "character")
        {
            metadata["character.key"] = characterKey;
            metadata["character.name"] = characterName;
        }
        var names = alias is null ? Array.Empty<NameEvidence>() : [new NameEvidence(alias, NameOrigin.UserAlias, "generated")];
        return new AssetDescriptor(identity, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), "character-model",
            characterName, null, AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.None,
            names, Array.Empty<SourceExtent>(), SortKey: characterName)
        { Metadata = metadata };
    }

    private static string NewCacheRoot() => Path.Combine(Path.GetTempPath(), "deviant-character-projection-" + Guid.NewGuid().ToString("N"));

    private static void DeleteCacheRoot(string path)
    {
        for (int attempt = 0; Directory.Exists(path); attempt++)
        {
            try { Directory.Delete(path, recursive: true); return; }
            catch (IOException error) when (attempt < 20 && (error.HResult & 0xffff) is 32 or 33)
            { Thread.Sleep(25); }
        }
    }

    private sealed class EmptyArchive : IDisposable
    {
        private readonly FormatsFixtureBuilder builder;
        private EmptyArchive(FormatsFixtureBuilder builder, ArchiveSnapshot snapshot)
        { this.builder = builder; Snapshot = snapshot; }
        public ArchiveSnapshot Snapshot { get; }
        public static async Task<EmptyArchive> CreateAsync()
        {
            var builder = FormatsFixtureBuilder.CreateEmpty();
            builder.WriteIndex();
            var snapshot = await new ArchiveReader().OpenAsync(builder.IndexPath);
            return new(builder, snapshot);
        }
        public void Dispose() => builder.Dispose();
    }
}
