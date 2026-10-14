using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class CharacterCatalogSessionTests
{
    [Fact]
    public async Task NameDiscoveryDefersDetailsAndSelectionKeepsThePublishedNameAndSortOrder()
    {
        using var generated = await GeneratedModelFixture.CreateAsync();
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var cacheRoot = Path.Combine(Path.GetTempPath(), "deviant-name-index-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = FeatureStore.Open(generated.Snapshot, cacheRoot);
            var provider = generated.Provider(budget);
            await using var session = new CharacterCatalogSession(generated.Snapshot, provider, budget, features: store);
            var sessionBaseline = budget.UsedBytes;
            AssetDescriptor published;
            using (var discovered = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
            {
                var entry = Assert.Single(discovered.Models); published = entry.Descriptor;
                Assert.Equal(CharacterModelStatisticsAvailability.DeferredUntilSelection, entry.SourceSummary.StatisticsAvailability);
                Assert.All(entry.Bindings, binding =>
                {
                    Assert.Empty(binding.AnimationLinks); Assert.Empty(binding.DirectReferences);
                    Assert.All(binding.Lods, lod => Assert.Null(lod.GroupStatistics));
                });
                Assert.Equal("static", entry.Descriptor.Metadata["model.kind"]);
                Assert.StartsWith(CharacterCatalogSession.NameIndexProfile, entry.Descriptor.Metadata["model.name-profile"]);
            }
            Assert.Equal(sessionBaseline, budget.UsedBytes);
            using (var selected = await session.HaveModelEntryForSelectionAsync(published.Identity, published.Revision))
            {
                Assert.Equal(CharacterModelStatisticsAvailability.Available, selected.Entry.SourceSummary.StatisticsAvailability);
                Assert.NotNull(Assert.Single(selected.Entry.Bindings).Lods[0].GroupStatistics);
                Assert.Equal(published.Label, selected.Entry.Descriptor.Label);
                Assert.Equal(published.SortKey, selected.Entry.Descriptor.SortKey);
            }
            using var page = session.QueryAssets(new AssetQuery(ExactKey: published.Identity.Key), published.Revision);
            var indexed = Assert.Single(page.Assets);
            Assert.Equal(published.Label, indexed.Label); Assert.Equal(published.SortKey, indexed.SortKey);
        }
        finally { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true); }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task InventoryPassListsAllMembersAndLeavesSelectionDetailsDeferred()
    {
        using var generated = await GeneratedModelFixture.CreateAsync(modelCount: 3);
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var cacheRoot = Path.Combine(Path.GetTempPath(), "deviant-inventory-seed-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = FeatureStore.Open(generated.Snapshot, cacheRoot);
            var provider = generated.Provider(budget);
            await using var session = new CharacterCatalogSession(generated.Snapshot, provider, budget, features: store);

            using (var inventory = await session.InventoryNextBatchAsync(0, 1))
            {
                Assert.Equal(3, inventory.Models.Count);
                Assert.All(inventory.Models, entry =>
                {
                    Assert.Equal(CharacterModelStatisticsAvailability.DeferredUntilSelection,
                        entry.SourceSummary.StatisticsAvailability);
                    Assert.Equal("unclassified", entry.Descriptor.Metadata["model.kind"]);
                    Assert.Equal("true", entry.Descriptor.Metadata["model.inventory-only"]);
                    Assert.Equal("Generated Model", entry.Descriptor.Label);
                    Assert.Contains(entry.Descriptor.Names, name => name.Text == "Generated Model" &&
                        name.Source.StartsWith("MESH v29 context ", StringComparison.Ordinal));
                    Assert.DoesNotContain("character.key", entry.Descriptor.Metadata.Keys);
                    Assert.Empty(entry.Bindings);
                    Assert.All(entry.SourceSummary.Lods, lod =>
                    { Assert.Null(lod.Selection); Assert.Null(lod.HasSkinPalette); Assert.Null(lod.GroupStatistics); });
                });
            }
            using (var warmInventory = await session.InventoryNextBatchAsync(0, 1))
            {
                Assert.Equal(0, warmInventory.OwnersOpened);
                Assert.Equal(3, warmInventory.Models.Count);
            }

            using var details = await session.HaveModelEntryForSelectionAsync(generated.ModelIdentity, provider.Revision);
            Assert.Equal(CharacterModelStatisticsAvailability.Available, details.Entry.SourceSummary.StatisticsAvailability);
            Assert.NotNull(Assert.Single(details.Entry.Bindings).Lods[0].GroupStatistics);
        }
        finally { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true); }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task CompactInventorySeedRestoresPublishedDescriptorCoverageAndSelectionDetails()
    {
        using var generated = await GeneratedModelFixture.CreateAsync(modelCount: 3,
            contextLabel: "Actor_Formal_CHA");
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var cacheRoot = Path.Combine(Path.GetTempPath(), "deviant-compact-inventory-seed-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = FeatureStore.Open(generated.Snapshot, cacheRoot);
            var provider = generated.Provider(budget);
            CharacterCatalogModelEntry expected;
            await using (var coldSession = new CharacterCatalogSession(generated.Snapshot, provider, budget, features: store))
            {
                using var cold = await coldSession.InventoryNextBatchAsync(0, 1);
                expected = Assert.Single(cold.Models, entry => entry.Descriptor.Identity == generated.ModelIdentity);
            }

            var collection = $"character-name-index/{generated.Snapshot.IndexContentId}/{generated.Snapshot.PhysicalSourceStamp}";
            var featureKey = $"{collection}/item/{generated.ModelIdentity.Key}";
            var loadedSeed = store.LoadBounded<JsonDocument>(featureKey,
                "character-inventory-name-seed-v1", checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes));
            Assert.NotNull(loadedSeed);
            using (var seed = loadedSeed)
            {
                var root = seed.RootElement;
                var storedIdentity = root.GetProperty("Descriptor").GetProperty("Identity");
                Assert.Equal(generated.ModelIdentity.Ordinal, storedIdentity.GetProperty("Ordinal").GetInt64());
                Assert.Equal(generated.ModelIdentity.MemberPath, storedIdentity.GetProperty("MemberPath").GetString());
                Assert.Equal(expected.Descriptor.Label, root.GetProperty("Descriptor").GetProperty("Label").GetString());
                Assert.Equal("actor", root.GetProperty("Descriptor").GetProperty("Metadata").GetProperty("character.candidate-key").GetString());
                Assert.True(root.TryGetProperty("SourceSummary", out var sourceSummary));
                Assert.False(sourceSummary.TryGetProperty("Lods", out _));
                Assert.False(sourceSummary.TryGetProperty("StoredNameEvidence", out _));
                Assert.True(sourceSummary.TryGetProperty("SourceExtents", out var sourceExtents));
                Assert.True(sourceExtents.GetArrayLength() > 0);
                Assert.True(root.GetProperty("CatalogLinks").GetArrayLength() > 0);
            }

            await using var warmSession = new CharacterCatalogSession(generated.Snapshot, provider, budget, features: store);
            using (var warm = await warmSession.InventoryNextBatchAsync(0, 1))
            {
                Assert.Equal(0, warm.OwnersOpened);
                var restored = Assert.Single(warm.Models, entry => entry.Descriptor.Identity == generated.ModelIdentity);
                Assert.Equal(expected.Descriptor.Label, restored.Descriptor.Label);
                Assert.Equal(expected.Descriptor.SortKey, restored.Descriptor.SortKey);
                Assert.True(expected.Descriptor.Names.SequenceEqual(restored.Descriptor.Names));
                Assert.Equal(expected.Descriptor.Metadata.Count, restored.Descriptor.Metadata.Count);
                foreach (var pair in expected.Descriptor.Metadata)
                    Assert.Equal(pair.Value, restored.Descriptor.Metadata[pair.Key]);
                Assert.True(expected.SourceSummary.SourceExtents.SequenceEqual(restored.SourceSummary.SourceExtents));
                Assert.Equal(CharacterModelStatisticsAvailability.DeferredUntilSelection,
                    restored.SourceSummary.StatisticsAvailability);
            }

            using (var coverage = await warmSession.RestoreNameCoverageAsync(0, 1))
                Assert.Contains(generated.CatalogIdentity, coverage.CoveredCatalogs);

            using var selected = await warmSession.HaveModelEntryForSelectionAsync(generated.ModelIdentity, provider.Revision);
            Assert.Equal(CharacterModelStatisticsAvailability.Available, selected.Entry.SourceSummary.StatisticsAvailability);
            Assert.NotEmpty(selected.Entry.SourceSummary.Lods);
            Assert.NotEmpty(selected.Entry.Bindings);
            Assert.Equal(expected.Descriptor.Label, selected.Entry.Descriptor.Label);
        }
        finally { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true); }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task InventoryDoesNotInvokeInjectedModelBindingResolver()
    {
        using var generated = await GeneratedModelFixture.CreateAsync(modelCount: 3,
            contextLabel: "Actor_Formal_CHA");
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var provider = generated.Provider(budget);
        var resolverCalls = 0;
        CharacterModelBindingResolver resolver = (_, _, _, _, _, _) =>
        {
            resolverCalls++;
            throw new InvalidOperationException("Inventory must not request selected-model bindings.");
        };
        await using (var session = new CharacterCatalogSession(generated.Snapshot, provider, budget,
            bindingResolver: resolver))
        {
            using var inventory = await session.InventoryNextBatchAsync(0, 1);
            Assert.Equal(3, inventory.Models.Count);
            Assert.Equal(0, resolverCalls);
            Assert.All(inventory.Models, entry =>
            {
                Assert.Empty(entry.Bindings);
                Assert.Equal("unclassified", entry.Descriptor.Metadata["model.kind"]);
                Assert.Equal("Actor Formal CHA", entry.Descriptor.Label);
                Assert.Equal("actor", entry.Descriptor.Metadata["character.candidate-key"]);
                Assert.Equal("Actor", entry.Descriptor.Metadata["character.candidate-name"]);
                Assert.Equal("Formal", entry.Descriptor.Metadata["character.candidate-variant"]);
                Assert.Contains(entry.SourceSummary.StoredNameEvidence, name => name.Text == "Actor_Formal_CHA" &&
                    name.Source.StartsWith("MESH v29 context ", StringComparison.Ordinal));
                Assert.DoesNotContain("character.key", entry.Descriptor.Metadata.Keys);
            });
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task InventoryReadsStoredMeshNamesWithoutParsingLaterMeshSections()
    {
        using var generated = await GeneratedModelFixture.CreateAsync(modelCount: 1, contextLabel: "",
            meshName: "Actor_Formal_CHA", unsupportedMeshTail: true);
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var provider = generated.Provider(budget);
        var resolverCalls = 0;
        CharacterModelBindingResolver resolver = (_, _, _, _, _, _) =>
        {
            resolverCalls++;
            throw new InvalidOperationException("Inventory must not request selected-model bindings.");
        };
        await using (var session = new CharacterCatalogSession(generated.Snapshot, provider, budget,
            bindingResolver: resolver))
        {
            using var inventory = await session.InventoryNextBatchAsync(0, 1);
            var entry = Assert.Single(inventory.Models);
            Assert.Equal(0, resolverCalls);
            Assert.Contains(entry.SourceSummary.StoredNameEvidence, name => name.Text == "Actor_Formal_CHA" &&
                name.Source.StartsWith("MESHDATA v41 ", StringComparison.Ordinal));
            Assert.Equal("actor", entry.Descriptor.Metadata["character.candidate-key"]);
            Assert.Equal("Actor", entry.Descriptor.Metadata["character.candidate-name"]);
            Assert.DoesNotContain("character.key", entry.Descriptor.Metadata.Keys);
            Assert.Empty(entry.Bindings);
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task InventoryKeepsTypedBlendShapeLabelsBoundToTheirSourceMeshGroup()
    {
        const string storedLabel = "GeneratedActor.Formal.HEAD";
        using var generated = await GeneratedModelFixture.CreateAsync(contextLabel: "", shapeName: storedLabel);
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        await using (var session = new CharacterCatalogSession(generated.Snapshot,
            generated.Provider(budget), budget))
        {
            using var inventory = await session.InventoryNextBatchAsync(0, 1);
            var entry = Assert.Single(inventory.Models);
            var observation = Assert.Single(entry.SourceSummary.StoredLabelObservations);
            Assert.Equal(Assert.Single(entry.SourceSummary.Lods).MeshDataIdentity,
                observation.MeshDataIdentity);
            Assert.Equal(0, observation.LodOrdinal);
            Assert.Equal(0, observation.GroupOrdinal);
            Assert.Equal(0, observation.EntryOrdinal);
            Assert.Equal(0, observation.StringOrdinal);
            Assert.Equal(storedLabel, observation.Text);
            Assert.Equal(CharacterLabelUtf8Status.Valid, observation.Utf8Status);
            Assert.Equal(observation.BytesExtent.Length, observation.RawByteLength);
            Assert.True(observation.BytesExtent.Length > 0);
            Assert.Empty(entry.Bindings);
            Assert.DoesNotContain("character.key", entry.Descriptor.Metadata.Keys);
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task InventoryUsesCommonStoredShapePrefixForUnaliasedModelLabel()
    {
        using var generated = await GeneratedModelFixture.CreateAsync(contextLabel: "",
            shapeNames: ["GeneratedActor.Formal.HEAD", "GeneratedActor.Formal.BODY"]);
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        await using (var session = new CharacterCatalogSession(generated.Snapshot,
            generated.Provider(budget), budget))
        {
            using var inventory = await session.InventoryNextBatchAsync(0, 1);
            var entry = Assert.Single(inventory.Models);
            Assert.Equal("GeneratedActor.Formal", entry.Descriptor.Label);
            Assert.Equal(2, entry.SourceSummary.StoredLabelObservations.Count);
            Assert.All(entry.SourceSummary.StoredLabelObservations,
                observation => Assert.Equal(0, observation.GroupOrdinal));
            Assert.Equal("unclassified", entry.Descriptor.Metadata["model.kind"]);
            Assert.DoesNotContain("character.key", entry.Descriptor.Metadata.Keys);
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void InventoryCandidatesRequireExplicitChaOrSkeletonBackedShapePrefixes()
    {
        var meshIdentity = new ResourceIdentity("generated-index", 0, "mesh-frame");
        var containerIdentity = meshIdentity with { MemberPath = null };
        CharacterStoredLabelObservation Label(string text, int ordinal)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var extent = new DecodedExtent(containerIdentity, ordinal * 64, bytes.Length);
            return new(meshIdentity, 0, 0, ordinal, 0, text, CharacterLabelUtf8Status.Valid,
                bytes.Length, new string('0', 64), extent, 0, new(containerIdentity, extent.Offset + extent.Length, 4));
        }

        var deformerLabels = new[] { Label("Generated.Formal.HEAD", 0), Label("Generated.Formal.BODY", 1) };
        Assert.Empty(ModelCatalogClassification.SuggestInventoryCandidates([], deformerLabels,
            sourceSkeletonV15Validated: false));
        var backed = ModelCatalogClassification.ResolveInventoryCandidate(
            ModelCatalogClassification.SuggestInventoryCandidates([], deformerLabels,
                sourceSkeletonV15Validated: true));
        Assert.NotNull(backed);
        Assert.Equal("generated", backed.Key);
        Assert.Equal("Formal", backed.Variant);

        var explicitCha = ModelCatalogClassification.ResolveInventoryCandidate(
            ModelCatalogClassification.SuggestInventoryCandidates(
                [new NameEvidence("Generated_Formal_CHA", NameOrigin.Stored, "generated source")],
                [], sourceSkeletonV15Validated: false));
        Assert.Equal("generated", explicitCha!.Key);
        Assert.Equal("Formal", explicitCha.Variant);

        var colliderLabels = new[] { Label("Collidable.Formal.HEAD", 2), Label("Collidable.Formal.BODY", 3) };
        Assert.Empty(ModelCatalogClassification.SuggestInventoryCandidates([], colliderLabels,
            sourceSkeletonV15Validated: true));

        var ambiguousLabels = new[] { Label("Generated_Formal_CHA", 4), Label("Alternate_Formal_CHA", 5) };
        var ambiguous = ModelCatalogClassification.SuggestInventoryCandidates([], ambiguousLabels,
            sourceSkeletonV15Validated: true);
        Assert.Null(ModelCatalogClassification.ResolveInventoryCandidate(ambiguous));
    }

    [Fact]
    public async Task InventoryBatchOpensASharedOwnerOnceForMultipleCatalogs()
    {
        using var generated = await GeneratedModelFixture.CreateAsync(modelCount: 3, catalogCount: 2);
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var cacheRoot = Path.Combine(Path.GetTempPath(), "deviant-inventory-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = FeatureStore.Open(generated.Snapshot, cacheRoot);
            await using var provider = new CatalogReferenceProvider(generated.Snapshot, budget, store,
                providerOptions: new(MaximumBatchCatalogs: 2));
            var resolverCalls = 0;
            CharacterModelBindingResolver resolver = (_, _, _, _, _, _) =>
            {
                resolverCalls++;
                throw new InvalidOperationException("Inventory must not request selected-model bindings.");
            };
            await using var session = new CharacterCatalogSession(generated.Snapshot, provider, budget,
                features: store, bindingResolver: resolver);

            using var inventory = await session.InventoryNextBatchAsync(0, 2);
            Assert.Equal(2, inventory.CatalogsRead);
            Assert.Equal(1, inventory.OwnersOpened);
            Assert.Equal(3, inventory.Models.Count);
            Assert.Equal(0, resolverCalls);
            Assert.All(inventory.Models, entry => Assert.Empty(entry.Bindings));
        }
        finally { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true); }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task CharacterGroupPageKeepsItsIndexedNameInsteadOfTheVariantLabel()
    {
        var actor = "Generated" + Guid.NewGuid().ToString("N");
        var sourceLabel = actor + "_Base_CHA";
        var expectedGroupName = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(actor.ToLowerInvariant());
        using var generated = await GeneratedModelFixture.CreateAsync(modelCount: 1, contextLabel: sourceLabel);
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var cacheRoot = Path.Combine(Path.GetTempPath(), "deviant-character-page-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = FeatureStore.Open(generated.Snapshot, cacheRoot);
            await store.PrepareCharacterProjectionsAsync();
            await using var provider = new CatalogReferenceProvider(generated.Snapshot, budget, store);
            await using var session = new CharacterCatalogSession(generated.Snapshot, provider, budget, features: store);
            using (var inventory = await session.InventoryNextBatchAsync(0, 1)) Assert.Single(inventory.Models);
            var query = new AssetQuery(Family: "character-model", Limit: 10) { ModelNameProfile = session.NameProfile };
            var groups = await session.QueryPreparedPageAsync(query, false);
            using (groups.Page) Assert.Equal(expectedGroupName, Assert.Single(groups.Page.Assets).Label);
            var models = await session.QueryPreparedPageAsync(query, true);
            using (models.Page) Assert.Contains(Assert.Single(models.Page.Assets).Names, name => name.Text == sourceLabel);
        }
        finally { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true); }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task CompletedEmptyOwnerHasAWarmNameCheckpoint()
    {
        using var generated = await GeneratedModelFixture.CreateAsync(modelCount: 0);
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var cacheRoot = Path.Combine(Path.GetTempPath(), "deviant-name-index-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = FeatureStore.Open(generated.Snapshot, cacheRoot);
            await using var provider = new CatalogReferenceProvider(generated.Snapshot, budget, store);
            await using var session = new CharacterCatalogSession(generated.Snapshot, provider, budget, features: store);
            using (var cold = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
                Assert.Empty(cold.Models);
            using (var restored = await session.RestoreNameCoverageAsync(0, 1))
            {
                Assert.Single(restored.RestoredCatalogs); Assert.True(restored.AllCatalogsVisited);
            }
            using (var warm = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
            { Assert.Equal(0, warm.OwnersOpened); Assert.Empty(warm.Models); }
        }
        finally { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true); }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task CancelledNameDiscoveryAndSelectionLeaveNoRetainedMemory()
    {
        using var generated = await GeneratedModelFixture.CreateAsync();
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var provider = generated.Provider(budget);
        await using var session = new CharacterCatalogSession(generated.Snapshot, provider, budget);
        var cancelled = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.DiscoverNextBatchAsync(0, 1, token: cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.HaveModelEntryForSelectionAsync(
            generated.ModelIdentity, provider.Revision, cancelled));
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task SelectingAModelPreemptsBackgroundNameDiscovery()
    {
        using var generated = await GeneratedModelFixture.CreateAsync();
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var cacheRoot = Path.Combine(Path.GetTempPath(), "deviant-priority-index-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = FeatureStore.Open(generated.Snapshot, cacheRoot);
            var provider = generated.Provider(budget);
            await using var session = new CharacterCatalogSession(generated.Snapshot, provider, budget, features: store);

            using (var initial = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
                Assert.Single(initial.Models);

            var gate = provider.BlockNextRead();
            var discovery = session.InventoryNextBatchAsync(0, 1);
            try
            {
                await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                var selected = session.HaveModelEntryForSelectionAsync(generated.ModelIdentity, provider.Revision);

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery.WaitAsync(TimeSpan.FromSeconds(5)));
                using var details = await selected.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(CharacterModelStatisticsAvailability.Available, details.Entry.SourceSummary.StatisticsAvailability);
            }
            finally
            {
                gate.Release.TrySetResult();
                try { await discovery; } catch (OperationCanceledException) { }
            }
        }
        finally { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true); }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void FullBindingReaderRetainsItsDefaultDetailAndAnimationOptions()
    {
        var options = new CharacterModelBindingOptions();
        Assert.True(options.IncludeGroupStatistics); Assert.True(options.IncludeAnimationLinks);
    }

    [Fact]
    public async Task NameReindexLeavesTheFullDetailCacheIntact()
    {
        using var generated = await GeneratedModelFixture.CreateAsync();
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var cacheRoot = Path.Combine(Path.GetTempPath(), "deviant-name-index-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = FeatureStore.Open(generated.Snapshot, cacheRoot);
            var provider = generated.Provider(budget);
            await using var session = new CharacterCatalogSession(generated.Snapshot, provider, budget, features: store);
            using (var names = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity)) Assert.Single(names.Models);
            using (var details = await session.HaveModelEntryForSelectionAsync(generated.ModelIdentity, provider.Revision))
                Assert.Equal(CharacterModelStatisticsAvailability.Available, details.Entry.SourceSummary.StatisticsAvailability);
            using (var warm = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
            { Assert.Equal(0, warm.OwnersOpened); Assert.Single(warm.Models); }
            using var preserved = session.GetModelEntry(generated.ModelIdentity, provider.Revision);
            Assert.Equal(CharacterModelStatisticsAvailability.Available, preserved!.Entry.SourceSummary.StatisticsAvailability);
            Assert.NotNull(Assert.Single(preserved.Entry.Bindings).Lods[0].GroupStatistics);
        }
        finally { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true); }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task WarmOwnerWithManyMembersRetainsEntrySizesRatherThanPayloadLimits()
    {
        using var generated = await GeneratedModelFixture.CreateAsync(modelCount: 40);
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var cacheRoot = Path.Combine(Path.GetTempPath(), "deviant-model-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var features = FeatureStore.Open(generated.Snapshot, cacheRoot);
            var provider = generated.Provider(budget);
            await using var session = new CharacterCatalogSession(generated.Snapshot, provider, budget, features: features);
            var sessionBaseline = budget.UsedBytes;
            using (var cold = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
                Assert.Equal(40, cold.Models.Count);
            Assert.Equal(sessionBaseline, budget.UsedBytes);
            using (var warm = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
            {
                Assert.Equal(0, warm.OwnersOpened);
                Assert.Equal(40, warm.Models.Count);
                Assert.InRange(budget.UsedBytes - sessionBaseline, 1, 8L * 1024 * 1024);
            }
            Assert.Equal(sessionBaseline, budget.UsedBytes);
            using (var repeatedWarm = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
            {
                Assert.Equal(0, repeatedWarm.OwnersOpened);
                Assert.Equal(40, repeatedWarm.Models.Count);
            }
            Assert.Equal(sessionBaseline, budget.UsedBytes);
        }
        finally { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true); }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task InvalidLastWarmEntryReleasesStagedReservationsBeforeColdFallback()
    {
        using var generated = await GeneratedModelFixture.CreateAsync(modelCount: 3);
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var cacheRoot = Path.Combine(Path.GetTempPath(), "deviant-model-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var features = FeatureStore.Open(generated.Snapshot, cacheRoot);
            var provider = generated.Provider(budget);
            await using var session = new CharacterCatalogSession(generated.Snapshot, provider, budget, features: features);
            var sessionBaseline = budget.UsedBytes;
            CharacterCatalogModelEntry last;
            using (var cold = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
                last = cold.Models[^1];
            var version = (string)typeof(CharacterCatalogSession).GetProperty("NameIndexVersion",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
            var key = $"character-name-index/{generated.Snapshot.IndexContentId}/{generated.Snapshot.PhysicalSourceStamp}/item/{last.Descriptor.Identity.Key}";
            features.SaveBounded(key, version, last with
            { SourceSummary = last.SourceSummary with { Revision = new(provider.Revision.IndexContentId, "stale") } },
                checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes));
            using (var fallback = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
            {
                Assert.Equal(1, fallback.OwnersOpened);
                Assert.Equal(3, fallback.Models.Count);
                Assert.InRange(budget.UsedBytes - sessionBaseline, 1, 2L * 1024 * 1024);
            }
            Assert.Equal(sessionBaseline, budget.UsedBytes);
        }
        finally { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true); }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task PreviousProfileIsReprocessedWithoutChangingIdentityOrAlias()
    {
        using var generated = await GeneratedModelFixture.CreateAsync();
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var cacheRoot = Path.Combine(Path.GetTempPath(), "deviant-model-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var features = FeatureStore.Open(generated.Snapshot, cacheRoot);
            var provider = generated.Provider(budget);
            var aliases = new AliasStore(Path.Combine(cacheRoot, "library"));
            await aliases.SetAsync(generated.ModelIdentity, "Saved model");
            await using var session = new CharacterCatalogSession(generated.Snapshot, provider, budget,
                aliases: aliases, features: features);
            CharacterCatalogModelEntry entry;
            using (var cold = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
                entry = Assert.Single(cold.Models);
            var previousVersion = (string)typeof(CharacterCatalogSession).GetProperty("NameIndexVersion",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)! + "/previous";
            var key = $"character-name-index/{generated.Snapshot.IndexContentId}/{generated.Snapshot.PhysicalSourceStamp}/item/{entry.Descriptor.Identity.Key}";
            features.SaveBounded(key, previousVersion, entry, checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes));
            using (var rebuilt = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
            {
                Assert.Equal(1, rebuilt.OwnersOpened);
                var model = Assert.Single(rebuilt.Models);
                Assert.Equal(generated.ModelIdentity, model.Descriptor.Identity);
                Assert.Equal("Saved model", model.Descriptor.Label);
                Assert.Equal("static", model.Descriptor.Metadata["model.kind"]);
                Assert.Equal(ModelCatalogClassification.Profile, model.Descriptor.Metadata["model.classification-profile"]);
            }
            using (var warm = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
                Assert.Equal(0, warm.OwnersOpened);
            using var page = session.QueryAssets(new AssetQuery(Search: generated.ModelIdentity.Key), provider.Revision);
            Assert.Equal(generated.ModelIdentity, Assert.Single(page.Assets).Identity);
            Assert.Equal("Saved model", aliases.Get(generated.ModelIdentity));
        }
        finally { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true); }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task PartialModelRemainsQueryableAndWarmWithoutClaimingComplete()
    {
        using var generated = await GeneratedModelFixture.CreateAsync(malformedMesh: true);
        var budget = new AssetMemoryBudget(128L * 1024 * 1024);
        var provider = generated.Provider(budget);
        var cacheRoot = Path.Combine(Path.GetTempPath(), "deviant-partial-model-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = FeatureStore.Open(generated.Snapshot, cacheRoot);
            await using var session = new CharacterCatalogSession(generated.Snapshot, provider, budget, features: store);
            using (var cold = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
            {
                Assert.Single(cold.Models);
                Assert.False(cold.IsComplete);
            }
            using (var warm = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
            {
                Assert.Equal(0, warm.OwnersOpened);
                Assert.Single(warm.Models);
                Assert.False(warm.IsComplete);
                Assert.Contains(warm.Issues, issue => issue.Code == "NativeMeshClassCountUnsupported");
            }
            using var entry = session.GetModelEntry(generated.ModelIdentity, provider.Revision);
            Assert.NotNull(entry);
            Assert.False(entry!.Entry.SourceSummary.Complete);
        }
        finally { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true); }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task MissingTypedCompanionIsRetainedAndDoesNotReportCompleteCoverage()
    {
        var fixture = CreateFixture();
        var budget = fixture.Provider.SharedMemoryBudget;
        await using var session = new CharacterCatalogSession(fixture.Snapshot, fixture.Provider, budget);

        using var result = await session.DiscoverModelsForCatalogAsync(fixture.CatalogIdentity);

        Assert.Empty(result.Models);
        var link = Assert.Single(result.CatalogLinks);
        Assert.Equal(0, link.RelationshipIndex);
        Assert.Equal(ReferenceStatus.Missing, link.Relationship.Resolution.Status);
        Assert.Equal("CharacterCatalogCompanionMissing", link.Diagnostic);
        Assert.Equal(1, result.VerifiedCatalogs);
        Assert.False(result.AllCatalogsVisited);
        Assert.Equal(1, result.UnscannedCatalogs);
        Assert.False(result.IsComplete);
        Assert.Equal(2, fixture.Provider.RevalidationCount);
        Assert.True(budget.UsedBytes > 0);
        result.Dispose();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task CancelledReadDoesNotPublishOrRetainReservations()
    {
        var fixture = CreateFixture();
        var budget = fixture.Provider.SharedMemoryBudget;
        await using var session = new CharacterCatalogSession(fixture.Snapshot, fixture.Provider, budget);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.DiscoverModelsForCatalogAsync(fixture.CatalogIdentity, token: cancellation.Token));

        Assert.Equal(0, fixture.Provider.ReadCount);
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void ProviderFromAnotherRevisionIsRejectedAtConstruction()
    {
        var fixture = CreateFixture();
        var stale = new TestReferenceProvider(fixture.Snapshot, fixture.Provider.CatalogIdentities,
            fixture.CatalogIdentity,
            fixture.Provider.ReadResult, new SourceRevision(fixture.Snapshot.IndexContentId, "stale-stamp"));

        Assert.Throws<ArchiveException>(() => new CharacterCatalogSession(fixture.Snapshot, stale,
            fixture.Provider.SharedMemoryBudget));
    }

    [Fact]
    public async Task StartingAtLaterCatalogDoesNotClaimEarlierCatalogCoverage()
    {
        var fixture = CreateFixture(catalogCount: 2, selectedCatalogIndex: 1);
        var budget = fixture.Provider.SharedMemoryBudget;
        await using var session = new CharacterCatalogSession(fixture.Snapshot, fixture.Provider, budget);

        using var result = await session.DiscoverModelsForCatalogAsync(fixture.CatalogIdentity);

        Assert.False(result.AllCatalogsVisited);
        Assert.Equal(2, result.UnscannedCatalogs);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task GeneratedOwnerModelIsCachedAndWarmDiscoveryDoesNotOpenOwner()
    {
        using var generated = await GeneratedModelFixture.CreateAsync();
        var sharedBudget = new AssetMemoryBudget(128L * 1024 * 1024);
        var provider = generated.Provider(sharedBudget);
        var cacheRoot = Path.Combine(Path.GetTempPath(), "deviant-character-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var features = FeatureStore.Open(generated.Snapshot, cacheRoot);
            var aliases = new AliasStore(Path.Combine(cacheRoot, "library"));
            await aliases.SetAsync(generated.ModelIdentity, "Generated alias");
            await using var session = new CharacterCatalogSession(generated.Snapshot, provider,
                sharedBudget, aliases: aliases, features: features);

            using (var cold = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
            {
                Assert.Equal(1, cold.OwnersOpened);
                var model = Assert.Single(cold.Models);
                Assert.Equal(AssetCapabilities.None, model.Descriptor.Capabilities);
                Assert.Equal(generated.Snapshot.IndexContentId, model.Descriptor.Identity.IndexContentId);
                Assert.Equal("Generated alias", model.Descriptor.Label);
            }

            using (var warm = await session.DiscoverModelsForCatalogAsync(generated.CatalogIdentity))
            {
                Assert.Equal(0, warm.OwnersOpened);
                Assert.Equal("Generated alias", Assert.Single(warm.Models).Descriptor.Label);
                Assert.True(warm.AllCatalogsVisited);
                Assert.True(warm.IsComplete);
            }

            using var page = session.QueryAssets(new AssetQuery(Limit: 10), provider.Revision);
            Assert.Single(page.Assets);
            using var details = session.GetModelEntry(generated.ModelIdentity, provider.Revision);
            Assert.NotNull(details);
            Assert.Equal(generated.ModelIdentity, details!.Entry.Descriptor.Identity);
            Assert.Equal("Generated alias", details.Entry.Descriptor.Label);
        }
        finally
        {
            if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
        }
        Assert.Equal(0, sharedBudget.UsedBytes);
    }

    private static Fixture CreateFixture(int catalogCount = 1, int selectedCatalogIndex = 0)
    {
        const string indexId = "generated-character-catalog-index";
        const string physicalStamp = "generated-character-catalog-stamp";
        var catalogs = Enumerable.Range(0, catalogCount).Select(index => new ResourceIdentity(indexId, index)).ToArray();
        var catalog = catalogs[selectedCatalogIndex];
        var rows = catalogs.Select((identity, index) => new ResourceRecord(index, 3, 0,
            checked((uint)(710 + index)), 0, 0, 128, 0, "archive.dat")).ToArray();
        var indexPath = Path.Combine(Path.GetTempPath(), "deviant-generated-index-" + Guid.NewGuid().ToString("N") + ".idx");
        var snapshot = new ArchiveSnapshot(indexPath, indexId, 18, 0, [], checked((uint)catalogCount),
            rows, [], new FileRevisionProvider().Capture(indexPath), physicalStamp, []);
        var extent = new SourceExtent("archive.dat", 20, 9, new string('a', 64));
        var relationship = new AssetRelationship(catalog, AssetRelationshipKind.Container,
            "typed container", new(IdentifierNamespace.Archive, "404", catalog, 29),
            new(ReferenceStatus.Missing, [], "not present"),
            new(true, "generated COM_CONT", "ordered typed reference", extent, SourceReferenceTrailer: 0));
        var result = new LocalizationReferenceReadResult(catalog,
            new SourceRevision(indexId, physicalStamp), "archive.dat",
            LocalizationReferenceReadStatus.Verified, new string('b', 64), [relationship]);
        var provider = new TestReferenceProvider(snapshot, catalogs, catalog, result,
            new SourceRevision(indexId, physicalStamp));
        return new(snapshot, provider, catalog);
    }

    private sealed record Fixture(ArchiveSnapshot Snapshot, TestReferenceProvider Provider,
        ResourceIdentity CatalogIdentity);

    private sealed class GeneratedModelFixture : IDisposable
    {
        private readonly FormatsFixtureBuilder builder;
        private readonly ResourceIdentity ownerIdentity;
        private readonly uint ownerId;

        private GeneratedModelFixture(FormatsFixtureBuilder builder, ArchiveSnapshot snapshot,
            IReadOnlyList<ResourceIdentity> catalogIdentities, ResourceIdentity ownerIdentity,
            ResourceIdentity modelIdentity, uint ownerId)
        {
            this.builder = builder;
            Snapshot = snapshot;
            CatalogIdentities = catalogIdentities;
            this.ownerIdentity = ownerIdentity;
            ModelIdentity = modelIdentity;
            this.ownerId = ownerId;
        }

        public ArchiveSnapshot Snapshot { get; }
        public IReadOnlyList<ResourceIdentity> CatalogIdentities { get; }
        public ResourceIdentity CatalogIdentity => CatalogIdentities[0];
        public ResourceIdentity ModelIdentity { get; }

        public static async Task<GeneratedModelFixture> CreateAsync(bool malformedMesh = false, int modelCount = 1,
            int catalogCount = 1, string contextLabel = "Generated Model", string meshName = "",
            bool unsupportedMeshTail = false, string? shapeName = null,
            IReadOnlyList<string>? shapeNames = null)
        {
            const uint containerId = 731;
            const uint catalogId = 732;
            const uint ownerId = 733;
            const uint meshId = 734;
            var builder = FormatsFixtureBuilder.CreateEmpty();
            var objects = new List<(uint Type, uint Id, byte[] Payload)>();
            for (var index = 0; index < modelCount; index++)
            {
                var modelId = checked(ownerId + (uint)index * 2);
                var dataId = checked(meshId + (uint)index * 2);
                objects.Add((2009, modelId, MeshOwner(dataId, contextLabel)));
                IReadOnlyList<string>? storedShapeNames = shapeNames;
                if (storedShapeNames is null && shapeName is not null) storedShapeNames = [shapeName];
                objects.Add((2130, dataId, malformedMesh ? new byte[8] :
                    MeshData(meshName, unsupportedMeshTail, storedShapeNames)));
            }
            builder.AddResource(29, containerId, NativeContainer(objects.ToArray()));
            for (var index = 0; index < catalogCount; index++)
                builder.AddResource(3, checked(catalogId + (uint)index), CatalogContainer(containerId));
            builder.WriteIndex();
            var snapshot = await new ArchiveReader().OpenAsync(builder.IndexPath);
            var catalogRows = snapshot.Records.Where(row => row.Type == 3).OrderBy(row => row.Ordinal).ToArray();
            Assert.Equal(catalogCount, catalogRows.Length);
            var ownerRow = Assert.Single(snapshot.Records, row => row.Type == 29);
            var owner = new ResourceIdentity(snapshot.IndexContentId, ownerRow.Ordinal);
            var catalogs = catalogRows.Select(row => new ResourceIdentity(snapshot.IndexContentId, row.Ordinal)).ToArray();
            var model = new ResourceIdentity(snapshot.IndexContentId, ownerRow.Ordinal,
                $"dc-object/0/2009/{ownerId}");
            return new(builder, snapshot, catalogs, owner, model, ownerId);
        }

        public TestReferenceProvider Provider(AssetMemoryBudget budget)
        {
            var revision = new SourceRevision(Snapshot.IndexContentId, Snapshot.PhysicalSourceStamp);
            var table = new LocalizationReferenceReader().ReadAsync(Snapshot, CatalogIdentity).GetAwaiter().GetResult();
            return new TestReferenceProvider(Snapshot, [CatalogIdentity], CatalogIdentity, table, revision, budget);
        }

        private static byte[] MeshOwner(uint meshId, string contextLabel)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(4U); writer.Write("COM_CONT"u8); writer.Write(6U); writer.Write(0U);
            writer.Write("LOADCONT"u8); writer.Write(2U);
            writer.Write("ENTITY  "u8); writer.Write(7U);
            writer.Write(0U); writer.Write(0U);
            writer.Write(0U); writer.Write(0U); writer.Write(0U); writer.Write(0U);
            writer.Write(0U); writer.Write(0U); writer.Write(0U); writer.Write((byte)0);
            writer.Write("MESH____"u8); writer.Write(29U); writer.Write(1U);
            writer.Write(2130U); writer.Write(meshId); writer.Write(1.0f);
            writer.Write(new byte[24]); writer.Write(0U); writer.Write(0U);
            var label = Encoding.UTF8.GetBytes(contextLabel);
            writer.Write(1U); writer.Write((uint)label.Length); writer.Write(label);
            writer.Write(0U); writer.Write(0U); writer.Write(0U); writer.Write(0U);
            writer.Write((byte)0); writer.Write(new byte[6]);
            for (var index = 0; index < 5; index++) writer.Write(0U);
            return stream.ToArray();
        }

        private static byte[] MeshData(string name, bool unsupportedTail, IReadOnlyList<string>? shapeNames)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(3U); writer.Write("MESHDATA"u8); writer.Write(41U);
            writer.Write(0U); writer.Write(0U); writer.Write(1U);
            writer.Write(0U); writer.Write(0U); writer.Write(0U);
            writer.Write((byte)0); writer.Write((byte)0);
            for (var index = 0; index < 19; index++) writer.Write(0.0f);
            writer.Write(0U); writer.Write(0U);
            var nameBytes = Encoding.UTF8.GetBytes(name);
            writer.Write((uint)nameBytes.Length); writer.Write(nameBytes);
            writer.Write(0U); writer.Write(0U); writer.Write(0U);
            writer.Write((byte)0); writer.Write((byte)0); writer.Write(0U);
            if (unsupportedTail) { writer.Write("BADCLUST"u8); writer.Write(2U); }
            else { writer.Write("CLUPSKME"u8); writer.Write(2U); writer.Write((byte)0); }
            writer.Write("BLSHAPES"u8); writer.Write(3U);
            if (shapeNames is null) writer.Write(0U);
            else
            {
                writer.Write((uint)shapeNames.Count);
                foreach (var shapeName in shapeNames)
                {
                    writer.Write(1U); writer.Write(0U); writer.Write(0U); writer.Write(0U); writer.Write(0U);
                    writer.Write((byte)0);
                    var shapeBytes = Encoding.UTF8.GetBytes(shapeName);
                    writer.Write((uint)shapeBytes.Length); writer.Write(shapeBytes); writer.Write(0x5a5a5a5aU);
                }
            }
            return stream.ToArray();
        }

        private static byte[] NativeContainer(params (uint Type, uint Id, byte[] Payload)[] objects)
        {
            var frames = new List<byte[]>();
            foreach (var item in objects)
            {
                var frame = new byte[13 + item.Payload.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)item.Payload.Length);
                item.Payload.CopyTo(frame, 13);
                frames.Add(frame);
            }
            using var stream = new MemoryStream();
            stream.Write("QZIP\0DC_INFO "u8); WriteUInt32(stream, 3);
            WriteUInt32(stream, checked(8U + (uint)objects.Length * 8U));
            WriteUInt32(stream, 0); WriteUInt32(stream, (uint)objects.Length);
            foreach (var item in objects) { WriteUInt32(stream, item.Type); WriteUInt32(stream, item.Id); }
            stream.Write("DC_DATA "u8); WriteUInt32(stream, 23);
            WriteUInt32(stream, (uint)frames.Sum(frame => frame.Length));
            foreach (var frame in frames) stream.Write(frame);
            return stream.ToArray();
        }

        private static byte[] CatalogContainer(uint containerId)
        {
            using var stream = new MemoryStream();
            stream.Write("COM_CONT"u8); WriteUInt32(stream, 6); WriteUInt32(stream, 13);
            WriteUInt32(stream, 1); WriteUInt32(stream, 29); WriteUInt32(stream, containerId);
            stream.WriteByte(0);
            return stream.ToArray();
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            stream.Write(bytes);
        }

        public void Dispose() => builder.Dispose();
    }

    private sealed class TestReferenceProvider : ICatalogReferenceProvider
    {
        private readonly ResourceIdentity catalog;
        private readonly LocalizationReferenceReadResult result;
        private readonly object readGate = new();
        private TaskCompletionSource? nextReadEntered;
        private TaskCompletionSource? nextReadRelease;

        public TestReferenceProvider(ArchiveSnapshot snapshot, IReadOnlyList<ResourceIdentity> catalogs,
            ResourceIdentity catalog,
            LocalizationReferenceReadResult result, SourceRevision? revision = null,
            AssetMemoryBudget? sharedMemoryBudget = null)
        {
            Snapshot = snapshot;
            this.catalog = catalog;
            this.result = result;
            Revision = revision ?? new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
            CatalogIdentities = catalogs;
            SharedMemoryBudget = sharedMemoryBudget ?? new AssetMemoryBudget(128L * 1024 * 1024);
        }

        public ArchiveSnapshot Snapshot { get; }
        public SourceRevision Revision { get; }
        public IReadOnlyList<ResourceIdentity> CatalogIdentities { get; }
        public AssetMemoryBudget SharedMemoryBudget { get; }
        public int MaximumBatchCatalogs => 16;
        public int ReadCount { get; private set; }
        public int RevalidationCount { get; private set; }
        public LocalizationReferenceReadResult ReadResult => result;

        public (Task Entered, TaskCompletionSource Release) BlockNextRead()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (readGate)
            {
                nextReadEntered = entered;
                nextReadRelease = release;
            }
            return (entered.Task, release);
        }

        public async Task<CatalogReferenceLease> ReadAsync(ResourceIdentity catalogIdentity,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (catalogIdentity != catalog) throw new ArchiveException("TestCatalogMismatch", "Unexpected catalog identity.");
            TaskCompletionSource? entered;
            TaskCompletionSource? release;
            lock (readGate)
            {
                entered = nextReadEntered;
                release = nextReadRelease;
                nextReadEntered = null;
                nextReadRelease = null;
            }
            if (release is not null)
            {
                entered!.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            ReadCount++;
            return new CatalogReferenceLease(result);
        }

        public Task ValidateSnapshotAsync(CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }

        public Task<CatalogReferenceBatch> ReadBatchAsync(int startCatalogIndex, int maximumCatalogs,
            CancellationToken token = default) => throw new NotSupportedException("This test covers direct catalog discovery.");

        public Task RevalidateAsync(LocalizationReferenceReadResult readResult, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (readResult.Source != catalog || readResult.Revision != Revision)
                throw new ArchiveException("SourceChanged", "The test provider revision changed.");
            RevalidationCount++;
            return Task.CompletedTask;
        }
    }
}
