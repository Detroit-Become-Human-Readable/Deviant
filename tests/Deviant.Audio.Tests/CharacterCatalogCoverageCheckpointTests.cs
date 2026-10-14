using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class CharacterCatalogCoverageCheckpointTests
{
    [Fact]
    public async Task LargeCachedModelDetailsDoNotPreventCompactNameCoverageRestore()
    {
        await using var fixture = await Fixture.CreateAsync(catalogCount: 1, modelMemberCount: 2);
        CharacterCatalogModelEntry[] modelEntries;
        await using (var discovery = fixture.NewSession())
        using (var scan = await discovery.DiscoverNextBatchAsync(0, 1))
        {
            Assert.True(scan.AllCatalogsVisited);
            modelEntries = scan.Models.ToArray();
        }

        foreach (var modelEntry in modelEntries)
        {
            var metadata = new Dictionary<string, string>(modelEntry.Descriptor.Metadata)
            {
                ["generated.large-detail"] = new string('x', 1_000_000)
            };
            fixture.Store.SaveBounded(NameEntryKey(fixture.Snapshot, modelEntry.Descriptor.Identity),
                NameIndexVersion(), modelEntry with
                {
                    Descriptor = modelEntry.Descriptor with { Metadata = metadata }
                }, checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes));
        }

        var budget = new AssetMemoryBudget(32L * 1024 * 1024);
        await using var references = new CatalogReferenceProvider(fixture.Snapshot, budget, fixture.Store);
        await using var resumed = new CharacterCatalogSession(fixture.Snapshot, references, budget, features: fixture.Store);
        using var restored = await resumed.RestoreNameCoverageAsync(0, 1);

        Assert.Equal(fixture.CatalogIdentities, restored.RestoredCatalogs);
        Assert.Equal(fixture.CatalogIdentities, restored.CoveredCatalogs);
        Assert.True(restored.AllCatalogsVisited);
        Assert.True(restored.AllPersistedModelMetadataSupported);
    }

    [Fact]
    public async Task EmptyCheckpointCacheSkipsAllReferenceReadsAndAdvancesAttemptedRange()
    {
        await using var fixture = await Fixture.CreateAsync(catalogCount: 3);
        var counted = new CountingReferenceProvider(fixture.References);
        await using var session = fixture.NewSession(references: counted);
        CharacterCatalogCoverageRestoreProgress? reported = null;
        using var restored = await session.RestoreNameCoverageAsync(0, 3,
            new InlineProgress<CharacterCatalogCoverageRestoreProgress>(value => reported = value));
        Assert.Equal(3, restored.NextCatalogIndex); Assert.Equal(3, restored.ExcludedCatalogs);
        Assert.Empty(restored.RestoredCatalogs); Assert.Empty(restored.CoveredCatalogs);
        Assert.Empty(counted.ReadIdentities); Assert.Equal(0, counted.BatchReads); Assert.Equal(0, counted.Revalidations);
        Assert.NotNull(reported); Assert.Equal(3, reported!.NextCatalogIndex);
    }

    [Fact]
    public async Task OnlyStructurallyPresentCheckpointsReadAndValidateTheirReferenceTables()
    {
        await using var fixture = await Fixture.CreateAsync(catalogCount: 3);
        await using (var discovery = fixture.NewSession())
        using (var pass = await discovery.DiscoverModelsForCatalogAsync(fixture.CatalogIdentities[1]))
            Assert.Single(pass.Models);
        var counted = new CountingReferenceProvider(fixture.References);
        await using var session = fixture.NewSession(references: counted);
        using var restored = await session.RestoreNameCoverageAsync(0, 3);
        Assert.Equal(fixture.CatalogIdentities[1], Assert.Single(counted.ReadIdentities));
        Assert.Equal(fixture.CatalogIdentities[1], Assert.Single(restored.RestoredCatalogs));
        Assert.Equal(3, restored.NextCatalogIndex); Assert.Equal(2, restored.ExcludedCatalogs);
        Assert.Equal(0, counted.BatchReads); Assert.Equal(2, counted.Revalidations);
    }

    [Fact]
    public async Task MalformedCheckpointEnvelopeDoesNotReadAReferenceTable()
    {
        await using var fixture = await Fixture.CreateAsync(catalogCount: 1);
        fixture.Store.SaveBounded(NameCheckpointKey(fixture.Snapshot, fixture.CatalogIdentities[0]), NameIndexVersion(),
            new { Revision = fixture.References.Revision, CatalogIdentity = fixture.CatalogIdentities[0],
                ReferenceTableSha256 = "invalid", Owners = Array.Empty<ResourceIdentity>(), OwnerMemberCounts = Array.Empty<int>() },
            checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes));
        var counted = new CountingReferenceProvider(fixture.References);
        await using var session = fixture.NewSession(references: counted);
        using var restored = await session.RestoreNameCoverageAsync(0, 1);
        Assert.Empty(counted.ReadIdentities); Assert.Empty(restored.CoveredCatalogs);
        Assert.Equal(1, restored.ExcludedCatalogs); Assert.NotEmpty(restored.Issues);
    }

    [Fact]
    public async Task VerifiedZeroModelCheckpointStillChecksItsCatalogSource()
    {
        await using var fixture = await Fixture.CreateAsync(catalogCount: 1, modelMemberCount: 0);
        await using (var discovery = fixture.NewSession())
        using (var pass = await discovery.DiscoverNextBatchAsync(0, 1))
        { Assert.Empty(pass.Models); Assert.True(pass.AllCatalogsVisited); }
        var counted = new CountingReferenceProvider(fixture.References);
        await using var session = fixture.NewSession(references: counted);
        using var restored = await session.RestoreNameCoverageAsync(0, 1);
        Assert.Single(counted.ReadIdentities); Assert.Equal(2, counted.Revalidations);
        Assert.Single(restored.CoveredCatalogs); Assert.True(restored.AllCatalogsVisited);
    }

    [Fact]
    public async Task CandidateSourceChangeCannotCommitAnEarlierRestoredCandidate()
    {
        await using var fixture = await Fixture.CreateAsync(catalogCount: 2);
        await using (var discovery = fixture.NewSession())
        using (var pass = await discovery.DiscoverNextBatchAsync(0, 2)) Assert.True(pass.AllCatalogsVisited);
        var counted = new CountingReferenceProvider(fixture.References) { FailAtRevalidation = 3 };
        await using var session = fixture.NewSession(references: counted);
        var failure = await Assert.ThrowsAsync<ArchiveException>(() => session.RestoreNameCoverageAsync(0, 2));
        Assert.Equal("SourceChanged", failure.Code); Assert.Empty(ReadCoveredCatalogIndexes(session));
        Assert.Equal(2, counted.ReadIdentities.Count); Assert.Equal(3, counted.Revalidations);
    }

    [Fact]
    public async Task MissingTypedCompanionCannotCommitAnEmptyNameCheckpoint()
    {
        await using var fixture = await Fixture.CreateAsync(catalogCount: 1, missingCompanion: true);
        await using var session = fixture.NewSession();
        using (var discovery = await session.DiscoverNextBatchAsync(0, 1))
        {
            Assert.Empty(discovery.Models); Assert.False(discovery.AllCatalogsVisited);
            Assert.Equal(1, discovery.UnscannedCatalogs); Assert.False(discovery.IsComplete);
            Assert.Contains(discovery.CatalogLinks, link => link.Diagnostic == "CharacterCatalogCompanionMissing");
        }
        using var restored = await session.RestoreNameCoverageAsync(0, 1);
        Assert.Empty(restored.RestoredCatalogs); Assert.Empty(restored.CoveredCatalogs);
        Assert.Equal(1, restored.UnscannedCatalogs); Assert.False(restored.AllCatalogsVisited);
    }

    [Fact]
    public async Task FreshSessionRestoresExactVerifiedPassesAndKeepsWarmModels()
    {
        await using var fixture = await Fixture.CreateAsync(catalogCount: 2);
        await using (var discovery = fixture.NewSession())
        {
            using var scanned = await discovery.DiscoverNextBatchAsync(0, 2);
            Assert.True(scanned.AllCatalogsVisited);
            Assert.Equal(2, scanned.TotalCatalogs);
        }

        await using var resumed = fixture.NewSession();
        using var restored = await resumed.RestoreNameCoverageAsync(0, 2);
        Assert.Equal(fixture.CatalogIdentities, restored.RestoredCatalogs);
        Assert.Equal(fixture.CatalogIdentities, restored.CoveredCatalogs);
        Assert.Equal(0, restored.UnscannedCatalogs);
        Assert.True(restored.AllCatalogsVisited);
        Assert.True(restored.AllPersistedModelMetadataSupported);

        using var warm = await resumed.DiscoverNextBatchAsync(0, 2);
        Assert.Equal(0, warm.OwnersOpened);
        Assert.Single(warm.Models);
        Assert.True(warm.AllCatalogsVisited);
    }

    [Fact]
    public async Task MissingPrefixCheckpointLeavesGapAndCursorStillAdvances()
    {
        await using var fixture = await Fixture.CreateAsync(catalogCount: 2);
        await using (var discovery = fixture.NewSession())
        {
            using var scanned = await discovery.DiscoverModelsForCatalogAsync(fixture.CatalogIdentities[1]);
            Assert.False(scanned.AllCatalogsVisited);
            Assert.Equal(2, scanned.NextCatalogIndex);
        }

        await using var resumed = fixture.NewSession();
        using (var missingPrefix = await resumed.RestoreNameCoverageAsync(0, 1))
        {
            Assert.Empty(missingPrefix.RestoredCatalogs);
            Assert.Empty(missingPrefix.CoveredCatalogs);
            Assert.Equal(1, missingPrefix.NextCatalogIndex);
            Assert.Equal(2, missingPrefix.UnscannedCatalogs);
            Assert.Equal(1, missingPrefix.ExcludedCatalogs);
            Assert.False(missingPrefix.AllCatalogsVisited);
        }

        using var later = await resumed.RestoreNameCoverageAsync(1, 1);
        Assert.Equal(fixture.CatalogIdentities[1], Assert.Single(later.RestoredCatalogs));
        Assert.Equal(fixture.CatalogIdentities[1], Assert.Single(later.CoveredCatalogs));
        Assert.Equal(2, later.NextCatalogIndex);
        Assert.Equal(1, later.UnscannedCatalogs);
        Assert.False(later.AllCatalogsVisited);
    }

    [Theory]
    [InlineData("checkpoint-table")]
    [InlineData("membership-key")]
    [InlineData("empty-membership")]
    [InlineData("owner-extent")]
    [InlineData("entry-extent")]
    public async Task CorruptTableMembershipOrSourceExtentFailsClosed(string corruption)
    {
        await using var fixture = await Fixture.CreateAsync(catalogCount: 1);
        CharacterCatalogModelEntry modelEntry;
        await using (var discovery = fixture.NewSession())
        using (var scan = await discovery.DiscoverModelsForCatalogAsync(fixture.CatalogIdentities[0]))
            modelEntry = Assert.Single(scan.Models);

        await fixture.CorruptCoverageRecordAsync(corruption, modelEntry);

        await using var resumed = fixture.NewSession();
        using var result = await resumed.RestoreNameCoverageAsync(0, 1);
        Assert.Empty(result.RestoredCatalogs);
        Assert.Empty(result.CoveredCatalogs);
        Assert.Equal(1, result.UnscannedCatalogs);
        Assert.False(result.AllCatalogsVisited);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public async Task CancellationAtCommitBoundaryDoesNotPartiallyRestoreCoverage()
    {
        await using var fixture = await Fixture.CreateAsync(catalogCount: 2);
        await using (var discovery = fixture.NewSession())
        {
            using var scan = await discovery.DiscoverNextBatchAsync(0, 2);
            Assert.Single(scan.Models); // Both generated catalogs link to the same owner/model.
        }

        await using var resumed = fixture.NewSession();
        using var cancellation = new CancellationTokenSource();
        var cancelOnProgress = new InlineProgress<CharacterCatalogCoverageRestoreProgress>(
            _ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await resumed.RestoreNameCoverageAsync(0, 2, cancelOnProgress, cancellation.Token));

        using var afterCancellation = await resumed.RestoreNameCoverageAsync(2, 1);
        Assert.Empty(afterCancellation.CoveredCatalogs);
        Assert.Equal(2, afterCancellation.UnscannedCatalogs);
    }

    [Fact]
    public async Task CancellationAfterOwnerOpenAndFirstMemberDoesNotCheckpointPartialEnumeration()
    {
        await using var fixture = await Fixture.CreateAsync(catalogCount: 1, modelMemberCount: 2);
        await using var session = fixture.NewSession();
        using var cancellation = new CancellationTokenSource();
        var cancelAfterFirstMember = new InlineProgress<CharacterCatalogProgress>(value =>
        {
            if (value.OwnersOpened > 0 && value.ModelMembersSeen > 0)
                cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.DiscoverModelsForCatalogAsync(fixture.CatalogIdentities[0],
                cancelAfterFirstMember, cancellation.Token));

        using var restored = await session.RestoreNameCoverageAsync(0, 1);
        Assert.Empty(restored.RestoredCatalogs);
        Assert.Empty(restored.CoveredCatalogs);
        Assert.Equal(1, restored.UnscannedCatalogs);
    }

    [Fact]
    public async Task PinnedSourceChangeAtCommitBoundaryLeavesCoverageUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync(catalogCount: 2);
        await using (var discovery = fixture.NewSession())
        {
            using var scan = await discovery.DiscoverNextBatchAsync(0, 2);
            Assert.True(scan.AllCatalogsVisited);
        }

        await using var resumed = fixture.NewSession();
        var volumePath = fixture.Snapshot.Volumes[0].FilePath;
        var originalLength = new FileInfo(volumePath).Length;
        var changeAfterValidation = new InlineProgress<CharacterCatalogCoverageRestoreProgress>(
            _ => File.AppendAllText(volumePath, "x"));
        await Assert.ThrowsAsync<IOException>(async () =>
            await resumed.RestoreNameCoverageAsync(0, 2, changeAfterValidation));
        Assert.Equal(originalLength, new FileInfo(volumePath).Length);

        var coveredField = typeof(CharacterCatalogSession).GetField("coveredCatalogIndexes",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(coveredField);
        Assert.Empty(Assert.IsType<HashSet<int>>(coveredField!.GetValue(resumed)));
    }

    [Fact]
    public async Task CancellationFromFinalDiscoveryProgressDoesNotCommitCoverage()
    {
        await using var fixture = await Fixture.CreateAsync(catalogCount: 2);
        await using var session = fixture.NewSession();
        using (var firstPass = await session.DiscoverModelsForCatalogAsync(fixture.CatalogIdentities[0]))
            Assert.Single(firstPass.Models);
        Assert.Equal(new[] { 0 }, ReadCoveredCatalogIndexes(session).Order().ToArray());

        using var cancellation = new CancellationTokenSource();
        var cancelOnFinalProgress = new InlineProgress<CharacterCatalogProgress>(value =>
        {
            if (value.IsComplete) cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.DiscoverModelsForCatalogAsync(fixture.CatalogIdentities[1],
                cancelOnFinalProgress, cancellation.Token));

        Assert.Equal(new[] { 0 }, ReadCoveredCatalogIndexes(session).Order().ToArray());
    }

    [Fact]
    public async Task PinnedSourceChangeFromFinalDiscoveryProgressDoesNotCommitCoverage()
    {
        await using var fixture = await Fixture.CreateAsync(catalogCount: 2);
        await using var session = fixture.NewSession();
        using (var firstPass = await session.DiscoverModelsForCatalogAsync(fixture.CatalogIdentities[0]))
            Assert.Single(firstPass.Models);
        Assert.Equal(new[] { 0 }, ReadCoveredCatalogIndexes(session).Order().ToArray());

        var volumePath = fixture.Snapshot.Volumes[0].FilePath;
        var originalLength = new FileInfo(volumePath).Length;
        var changeOnFinalProgress = new InlineProgress<CharacterCatalogProgress>(value =>
        {
            if (value.IsComplete) File.AppendAllText(volumePath, "x");
        });

        await Assert.ThrowsAsync<IOException>(async () =>
            await session.DiscoverModelsForCatalogAsync(fixture.CatalogIdentities[1], changeOnFinalProgress));

        Assert.Equal(originalLength, new FileInfo(volumePath).Length);
        Assert.Equal([0], ReadCoveredCatalogIndexes(session).Order().ToArray());
    }

    [Fact]
    public async Task LegacyV2OwnerMembershipReprocessesModelsForCurrentClassification()
    {
        await using var fixture = await Fixture.CreateAsync(catalogCount: 1);
        CharacterCatalogModelEntry modelEntry;
        await using (var discovery = fixture.NewSession())
        using (var scan = await discovery.DiscoverModelsForCatalogAsync(fixture.CatalogIdentities[0]))
            modelEntry = Assert.Single(scan.Models);

        var modelKey = ModelEntryKey(fixture.Snapshot, fixture.ModelIdentity);
        var legacyEntryVersion = LegacyModelEntryVersion();
        fixture.Store.SaveBounded(modelKey, legacyEntryVersion, modelEntry,
            checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes));
        fixture.Store.SaveBounded(NameEntryKey(fixture.Snapshot, fixture.ModelIdentity), NameIndexVersion() + "/old",
            modelEntry, checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes));

        await using var resumed = fixture.NewSession();
        using var warm = await resumed.DiscoverModelsForCatalogAsync(fixture.CatalogIdentities[0]);

        Assert.Equal(1, warm.OwnersOpened);
        var model = Assert.Single(warm.Models);
        Assert.Single(model.Bindings);
        Assert.Equal(CharacterModelStatisticsAvailability.DeferredUntilSelection,
            model.SourceSummary.StatisticsAvailability);
        Assert.Equal(ModelCatalogClassification.Profile, model.Descriptor.Metadata["model.classification-profile"]);
        Assert.NotNull(fixture.Store.LoadBounded<CharacterCatalogModelEntry>(modelKey, legacyEntryVersion,
            checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes)));
    }

    [Fact]
    public async Task UnsupportedOldV3EntryCannotSkipOwnerRepairAndKeepsStoredNameAndAlias()
    {
        await using var fixture = await Fixture.CreateAsync(catalogCount: 1,
            storedMeshName: "generated stored mesh label");
        CharacterCatalogModelEntry previousEntry;
        await using (var discovery = fixture.NewSession())
        using (var scan = await discovery.DiscoverModelsForCatalogAsync(fixture.CatalogIdentities[0]))
            previousEntry = Assert.Single(scan.Models);

        Assert.Equal("generated stored mesh label", Assert.Single(previousEntry.SourceSummary.StoredNameEvidence).Text);
        fixture.Store.SaveBounded(ModelEntryKey(fixture.Snapshot, fixture.ModelIdentity),
            PreviousV3ModelEntryVersion(), previousEntry with
            {
                Descriptor = previousEntry.Descriptor with { Label = "old cached label" },
                SourceSummary = previousEntry.SourceSummary with
                {
                    Complete = false,
                    StatisticsAvailability = CharacterModelStatisticsAvailability.Available
                }
            },
            checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes));
        fixture.Store.SaveBounded(NameEntryKey(fixture.Snapshot, fixture.ModelIdentity), NameIndexVersion() + "/old",
            previousEntry, checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes));

        using (var tableLease = await fixture.References.ReadAsync(fixture.CatalogIdentities[0]))
        {
            var ownerRow = fixture.Snapshot.Records[checked((int)fixture.OwnerIdentity.Ordinal)];
            var ownerExtent = Assert.Single(previousEntry.SourceSummary.SourceExtents, extent =>
                extent.RelativeSource == ownerRow.VolumeName && extent.Offset == ownerRow.Offset &&
                extent.Length == ownerRow.StoredSize);
            fixture.Store.SaveBounded(OwnerMembershipKey(fixture.Snapshot,
                    fixture.CatalogIdentities[0], fixture.OwnerIdentity), "character-owner-membership-v2",
                new
                {
                    Revision = fixture.References.Revision,
                    CatalogIdentity = fixture.CatalogIdentities[0],
                    ReferenceTableSha256 = tableLease.Result.ReferenceTableSha256!,
                    OwnerIdentity = fixture.OwnerIdentity,
                    OwnerExtents = new[] { ownerExtent },
                    ModelIdentities = new[] { fixture.ModelIdentity }
                }, checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes));

            var profile = PreviousV3ModelEntryVersion();
            fixture.Store.SaveBounded(CoverageOwnerManifestKey(fixture.Snapshot,
                    fixture.CatalogIdentities[0], fixture.OwnerIdentity),
                $"character-catalog-coverage-owner-v1/{profile}",
                new
                {
                    Revision = fixture.References.Revision,
                    CatalogIdentity = fixture.CatalogIdentities[0],
                    ReferenceTableSha256 = tableLease.Result.ReferenceTableSha256!,
                    OwnerIdentity = fixture.OwnerIdentity,
                    OwnerExtents = new[] { ownerExtent },
                    ModelIdentities = new[] { fixture.ModelIdentity },
                    ModelEntryFeatureKeys = new[] { ModelEntryKey(fixture.Snapshot, fixture.ModelIdentity) },
                    ModelEntryIdentities = new[] { fixture.ModelIdentity },
                    UnsupportedMembers = new[]
                    {
                        new { Identity = fixture.ModelIdentity, DiagnosticCodes = new[] { "CharacterModelMetadataIncomplete" } }
                    },
                    Complete = false,
                    EnumerationComplete = true
                }, checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes));
            fixture.Store.SaveBounded(CoverageCheckpointKey(fixture.Snapshot, 0),
                $"character-catalog-coverage-v1/{profile}",
                new
                {
                    Revision = fixture.References.Revision,
                    CatalogIndex = 0,
                    CatalogIdentity = fixture.CatalogIdentities[0],
                    ReferenceTableSha256 = tableLease.Result.ReferenceTableSha256!,
                    ReferenceRowCount = tableLease.Result.Relationships.Count,
                    OwnerIdentities = new[] { fixture.OwnerIdentity },
                    ModelMemberCount = 1,
                    UnsupportedModelCount = 1,
                    EnumerationComplete = true,
                    AllPersistedModelMetadataSupported = false,
                    OmittedIssueCount = 0,
                    Issues = Array.Empty<object>()
                }, checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes));
        }

        var aliases = new AliasStore(Path.Combine(fixture.CacheRoot, "library"));
        await aliases.SetAsync(fixture.ModelIdentity, "generated alias");
        await using var resumed = fixture.NewSession(aliases);
        using (var oldCoverage = await resumed.RestoreCoverageAsync(0, 1))
        {
            Assert.Empty(oldCoverage.RestoredCatalogs);
            Assert.Empty(oldCoverage.CoveredCatalogs);
            Assert.Equal(1, oldCoverage.UnscannedCatalogs);
        }
        using var repaired = await resumed.DiscoverNextBatchAsync(0, 1);

        Assert.Equal(1, repaired.OwnersOpened);
        var model = Assert.Single(repaired.Models);
        Assert.True(model.SourceSummary.Complete);
        Assert.Equal(CharacterModelStatisticsAvailability.DeferredUntilSelection, model.SourceSummary.StatisticsAvailability);
        Assert.Equal("generated stored mesh label", Assert.Single(model.SourceSummary.StoredNameEvidence).Text);
        Assert.Contains(model.Descriptor.Names, name => name.Origin == NameOrigin.Stored &&
            name.Text == "generated stored mesh label");
        Assert.Equal("generated alias", model.Descriptor.Label);
        Assert.NotNull(fixture.Store.LoadBounded<CharacterCatalogModelEntry>(ModelEntryKey(fixture.Snapshot, fixture.ModelIdentity),
            PreviousV3ModelEntryVersion(), checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes)));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly FormatsFixtureBuilder builder;

        private Fixture(FormatsFixtureBuilder builder, ArchiveSnapshot snapshot,
            IReadOnlyList<ResourceIdentity> catalogIdentities, ResourceIdentity ownerIdentity,
            IReadOnlyList<ResourceIdentity> modelIdentities, string cacheRoot,
            FeatureStore store, AssetMemoryBudget budget, CatalogReferenceProvider references)
        {
            this.builder = builder;
            Snapshot = snapshot;
            CatalogIdentities = catalogIdentities;
            OwnerIdentity = ownerIdentity;
            ModelIdentities = modelIdentities;
            CacheRoot = cacheRoot;
            Store = store;
            Budget = budget;
            References = references;
        }

        public ArchiveSnapshot Snapshot { get; }
        public IReadOnlyList<ResourceIdentity> CatalogIdentities { get; }
        public ResourceIdentity OwnerIdentity { get; }
        public IReadOnlyList<ResourceIdentity> ModelIdentities { get; }
        public ResourceIdentity ModelIdentity => ModelIdentities[0];
        public string CacheRoot { get; }
        public FeatureStore Store { get; }
        public AssetMemoryBudget Budget { get; }
        public CatalogReferenceProvider References { get; }

        public CharacterCatalogSession NewSession(AliasStore? aliases = null, ICatalogReferenceProvider? references = null) =>
            new(Snapshot, references ?? References, Budget, aliases: aliases, features: Store);

        public static async Task<Fixture> CreateAsync(int catalogCount, int modelMemberCount = 1,
            string storedMeshName = "", bool missingCompanion = false)
        {
            const uint containerId = 731;
            const uint firstOwnerId = 733;
            const uint firstMeshId = 834;
            var builder = FormatsFixtureBuilder.CreateEmpty();
            var objects = new List<(uint Type, uint Id, byte[] Payload)>();
            for (var index = 0; index < modelMemberCount; index++)
            {
                var ownerId = checked(firstOwnerId + (uint)index);
                var meshId = checked(firstMeshId + (uint)index);
                objects.Add((2009, ownerId, MeshOwner(meshId)));
                objects.Add((2130, meshId, MeshData(storedMeshName)));
            }
            builder.AddResource(29, containerId, NativeContainer(objects.ToArray()));
            for (var index = 0; index < catalogCount; index++)
                builder.AddResource(3, checked((uint)(740 + index)), CatalogContainer(missingCompanion ? containerId + 1 : containerId));
            builder.WriteIndex();

            var snapshot = await new ArchiveReader().OpenAsync(builder.IndexPath);
            var containerRow = Assert.Single(snapshot.Records, row => row.Type == 29);
            var catalogs = snapshot.Records.Where(row => row.Type == 3)
                .OrderBy(row => row.Ordinal)
                .Select(row => new ResourceIdentity(snapshot.IndexContentId, row.Ordinal)).ToArray();
            var owner = new ResourceIdentity(snapshot.IndexContentId, containerRow.Ordinal);
            var models = Enumerable.Range(0, modelMemberCount)
                .Select(index => new ResourceIdentity(snapshot.IndexContentId, containerRow.Ordinal,
                    $"dc-object/{index * 2}/2009/{firstOwnerId + (uint)index}"))
                .ToArray();
            var cacheRoot = Path.Combine(Path.GetTempPath(), "deviant-character-coverage-" + Guid.NewGuid().ToString("N"));
            var store = FeatureStore.Open(snapshot, cacheRoot);
            var budget = new AssetMemoryBudget(128L * 1024 * 1024);
            var provider = new CatalogReferenceProvider(snapshot, budget, store);
            return new(builder, snapshot, catalogs, owner, models, cacheRoot, store, budget, provider);
        }

        public async Task CorruptCoverageRecordAsync(string corruption, CharacterCatalogModelEntry modelEntry)
        {
            var catalog = CatalogIdentities[0];
            using var lease = await References.ReadAsync(catalog);
            var table = lease.Result;
            var ownerRow = Snapshot.Records[checked((int)OwnerIdentity.Ordinal)];
            var revision = new SourceRevision(Snapshot.IndexContentId, Snapshot.PhysicalSourceStamp);
            string profile = NameIndexVersion();
            switch (corruption)
            {
                case "checkpoint-table":
                    Store.SaveBounded(NameCheckpointKey(Snapshot, catalog), profile, new
                    {
                        Revision = revision, CatalogIdentity = catalog,
                        ReferenceTableSha256 = new string('0', 64), Owners = new[] { OwnerIdentity }, OwnerMemberCounts = new[] { 1 }
                    }, checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes));
                    break;
                case "membership-key":
                case "empty-membership":
                case "owner-extent":
                    var extents = modelEntry.SourceSummary.SourceExtents.Where(extent =>
                        extent.RelativeSource == ownerRow.VolumeName && extent.Offset >= ownerRow.Offset &&
                        extent.Offset + extent.Length <= ownerRow.Offset + ownerRow.StoredSize).ToArray();
                    Store.SaveBounded(NameOwnerKey(Snapshot, catalog, OwnerIdentity), profile, new
                    {
                        Revision = revision, CatalogIdentity = catalog, OwnerIdentity,
                        ReferenceTableSha256 = table.ReferenceTableSha256!,
                        OwnerExtents = corruption == "owner-extent"
                            ? new[] { new SourceExtent(ownerRow.VolumeName, long.MaxValue, 1, new string('a', 64)) } : extents,
                        ModelIdentities = corruption == "empty-membership" ? Array.Empty<ResourceIdentity>() : new[] { ModelIdentity },
                        EntryIdentities = corruption == "empty-membership" ? Array.Empty<ResourceIdentity>() : corruption == "membership-key"
                            ? new[] { new ResourceIdentity(ModelIdentity.IndexContentId, ModelIdentity.Ordinal, "dc-object/99/2009/999") }
                            : new[] { ModelIdentity },
                        UnsupportedModels = 0
                    }, checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes));
                    break;
                case "entry-extent":
                    Store.SaveBounded(NameEntryKey(Snapshot, ModelIdentity), profile, modelEntry with
                    {
                        SourceSummary = modelEntry.SourceSummary with
                        { SourceExtents = [new SourceExtent(ownerRow.VolumeName, long.MaxValue, 1, new string('a', 64))] }
                    }, checked((int)CharacterCatalogSession.MaximumPersistedEntryBytes));
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(corruption));
            }
        }

        public async ValueTask DisposeAsync()
        {
            await References.DisposeAsync();
            Store.Dispose();
            builder.Dispose();
            if (Directory.Exists(CacheRoot)) Directory.Delete(CacheRoot, recursive: true);
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class CountingReferenceProvider(ICatalogReferenceProvider inner) : ICatalogReferenceProvider
    {
        public ArchiveSnapshot Snapshot => inner.Snapshot;
        public SourceRevision Revision => inner.Revision;
        public IReadOnlyList<ResourceIdentity> CatalogIdentities => inner.CatalogIdentities;
        public int MaximumBatchCatalogs => inner.MaximumBatchCatalogs;
        public AssetMemoryBudget SharedMemoryBudget => inner.SharedMemoryBudget;
        public List<ResourceIdentity> ReadIdentities { get; } = [];
        public int BatchReads { get; private set; }
        public int Revalidations { get; private set; }
        public int? FailAtRevalidation { get; init; }
        public Task<CatalogReferenceLease> ReadAsync(ResourceIdentity identity, CancellationToken token = default)
        { ReadIdentities.Add(identity); return inner.ReadAsync(identity, token); }
        public Task ValidateSnapshotAsync(CancellationToken token = default) => inner.ValidateSnapshotAsync(token);
        public Task<CatalogReferenceBatch> ReadBatchAsync(int start, int maximum, CancellationToken token = default)
        { BatchReads++; return inner.ReadBatchAsync(start, maximum, token); }
        public Task RevalidateAsync(LocalizationReferenceReadResult result, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Revalidations++;
            return Revalidations == FailAtRevalidation
                ? Task.FromException(new ArchiveException("SourceChanged", "The candidate source changed."))
                : inner.RevalidateAsync(result, token);
        }
    }

    private static HashSet<int> ReadCoveredCatalogIndexes(CharacterCatalogSession session)
    {
        var field = typeof(CharacterCatalogSession).GetField("coveredCatalogIndexes",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<HashSet<int>>(field!.GetValue(session));
    }

    private static string ModelEntryKey(ArchiveSnapshot snapshot, ResourceIdentity identity) =>
        $"{CharacterCatalogSession.ModelEntryCollection}/{snapshot.IndexContentId}/{snapshot.PhysicalSourceStamp}/item/{identity.Key}";

    private static string NameEntryKey(ArchiveSnapshot snapshot, ResourceIdentity identity) =>
        $"character-name-index/{snapshot.IndexContentId}/{snapshot.PhysicalSourceStamp}/item/{identity.Key}";
    private static string NameOwnerKey(ArchiveSnapshot snapshot, ResourceIdentity catalog, ResourceIdentity owner) =>
        $"character-name-index/{snapshot.IndexContentId}/{snapshot.PhysicalSourceStamp}/owner/{catalog.Key}/{owner.Key}";
    private static string NameCheckpointKey(ArchiveSnapshot snapshot, ResourceIdentity catalog) =>
        $"character-name-index/{snapshot.IndexContentId}/{snapshot.PhysicalSourceStamp}/catalog/{catalog.Key}";
    private static string NameIndexVersion() =>
        $"{CharacterCatalogSession.NameIndexProfile}/{ModelCatalogClassification.Profile}/labels-512-4096-4096-262144/rig-names-True-512";

    private static string OwnerMembershipKey(ArchiveSnapshot snapshot,
        ResourceIdentity catalog, ResourceIdentity owner) =>
        $"character-model-owner-membership/{snapshot.IndexContentId}/{snapshot.PhysicalSourceStamp}/item/{catalog.Key}/{owner.Key}";

    private static string CurrentModelEntryVersion() =>
        $"{CharacterCatalogSession.ModelEntryVersion}/labels-512-4096-4096-262144/rig-names-True-512";

    private static string PreviousV3ModelEntryVersion() =>
        "character-model-entry-v3-meshdata41-clupskme2-blshapes3-binding2-lodstats1/labels-512-4096-4096-262144";

    private static string LegacyModelEntryVersion() =>
        $"character-model-entry-v2-meshdata41-clupskme2-blshapes3-binding2/labels-512-4096-4096-262144";

    private static string CoverageCollection(ArchiveSnapshot snapshot) =>
        $"character-catalog-coverage/{snapshot.IndexContentId}/{snapshot.PhysicalSourceStamp}";

    private static string CoverageCheckpointKey(ArchiveSnapshot snapshot, int index) =>
        $"{CoverageCollection(snapshot)}/catalog/{index:D10}";

    private static string CoverageCheckpointVersion() =>
        $"character-catalog-coverage-v1/{CurrentModelEntryVersion()}";

    private static string CoverageOwnerManifestKey(ArchiveSnapshot snapshot,
        ResourceIdentity catalog, ResourceIdentity owner) =>
        $"{CoverageCollection(snapshot)}/owner/{catalog.Key}/{owner.Key}";

    private static string CoverageOwnerManifestVersion() =>
        $"character-catalog-coverage-owner-v1/{CurrentModelEntryVersion()}";

    private static byte[] MeshOwner(uint meshId)
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
        return stream.ToArray();
    }

    private static byte[] MeshData(string storedName)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(3U); writer.Write("MESHDATA"u8); writer.Write(41U);
        writer.Write(0U); writer.Write(0U); writer.Write(1U);
        writer.Write(0U); writer.Write(0U); writer.Write(0U);
        writer.Write((byte)0); writer.Write((byte)0);
        for (var index = 0; index < 19; index++) writer.Write(0.0f);
        writer.Write(0U); writer.Write(0U);
        var nameBytes = Encoding.UTF8.GetBytes(storedName);
        writer.Write(checked((uint)nameBytes.Length)); writer.Write(nameBytes);
        writer.Write(0U); writer.Write(0U); writer.Write(0U);
        writer.Write((byte)0); writer.Write((byte)0); writer.Write(0U);
        writer.Write("CLUPSKME"u8); writer.Write(2U); writer.Write((byte)0);
        writer.Write("BLSHAPES"u8); writer.Write(3U); writer.Write(0U);
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
}
