using System.Globalization;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;

namespace Deviant.Browser;

public sealed partial class CharacterCatalogSession
{
    private const string InventoryNameSeedVersion = "character-inventory-name-seed-v1";
    public const string NameIndexProfile = "character-name-index-v2-meshdata41-skeleton15-nodear14-blshapes3-metadata1-owner-counts1";
    private string NameIndexVersion => string.Create(CultureInfo.InvariantCulture,
        $"{NameIndexProfile}/{ModelCatalogClassification.Profile}/labels-{options.MaximumLabelObservationsPerModel}-{options.MaximumLabelBytes}-{options.MaximumLabelCharacters}-{options.MaximumLabelTextBytesPerModel}/rig-names-{options.IncludeSkeletonNameEvidence}-{options.MaximumSkeletonNameEvidence}");
    private string NameCollection => $"character-name-index/{snapshot.IndexContentId}/{snapshot.PhysicalSourceStamp}";
    private string NameEntryFeatureKey(ResourceIdentity identity) => $"{NameCollection}/item/{identity.Key}";
    private string NameOwnerKey(ResourceIdentity catalog, ResourceIdentity owner) => $"{NameCollection}/owner/{catalog.Key}/{owner.Key}";
    private string NameCheckpointKey(ResourceIdentity catalog) => $"{NameCollection}/catalog/{catalog.Key}";

    private sealed record NameOwnerManifest(SourceRevision Revision, ResourceIdentity CatalogIdentity,
        ResourceIdentity OwnerIdentity, string? ReferenceTableSha256, IReadOnlyList<SourceExtent> OwnerExtents,
        IReadOnlyList<ResourceIdentity> ModelIdentities, IReadOnlyList<ResourceIdentity> EntryIdentities, int UnsupportedModels);
    private sealed record NameCheckpoint(SourceRevision Revision, ResourceIdentity CatalogIdentity,
        string? ReferenceTableSha256, IReadOnlyList<ResourceIdentity> Owners, IReadOnlyList<int> OwnerMemberCounts);
    private sealed record InventoryNameSeed(AssetDescriptor Descriptor, InventoryNameSourceSummary SourceSummary,
        IReadOnlyList<CharacterCatalogLink> CatalogLinks);
    private sealed record InventoryNameSourceSummary(ResourceIdentity Identity, SourceRevision Revision,
        uint OwnerNativeType, uint OwnerNativeId, IReadOnlyList<SourceExtent> SourceExtents, bool Complete,
        CharacterModelStatisticsAvailability StatisticsAvailability, string? InventoryDisplayLabel,
        bool SourceSkeletonV15Validated);
    private sealed record NameIndexWriteResult(IReadOnlySet<ResourceIdentity> PersistedModels,
        IReadOnlySet<ResourceIdentity> CoveredCatalogs);
    private sealed record NameEntryCoverageProjection(ResourceIdentity? DescriptorIdentity,
        SourceRevision? DescriptorRevision, string? DescriptorFamily, string? NameProfile,
        ResourceIdentity? SourceSummaryIdentity, SourceRevision? SourceSummaryRevision,
        CharacterModelStatisticsAvailability StatisticsAvailability, IReadOnlyList<SourceExtent>? SourceExtents,
        IReadOnlyList<NameEntryCoverageLinkProjection>? CatalogLinks);
    private sealed record NameEntryCoverageLinkProjection(ResourceIdentity? CatalogIdentity,
        string? ReferenceTableSha256, IReadOnlyList<ResourceIdentity>? ResolutionCandidates);

    private CharacterCatalogModelEntry? LoadNameEntry(ResourceIdentity identity, CancellationToken token,
        ReservationGroup? retained = null)
        => LoadNameEntry(identity, token, retained, out _);

    private CharacterCatalogModelEntry? LoadNameEntry(ResourceIdentity identity, CancellationToken token,
        ReservationGroup? retained, out bool compactInventorySeed)
    {
        compactInventorySeed = false;
        if (features is null) return null;
        using var scratch = memoryBudget.Reserve(MaximumPersistedEntryBytes * 3L, "model name cache scratch");
        CharacterCatalogModelEntry? entry;
        try
        {
            var seed = features.LoadBounded<InventoryNameSeed>(NameEntryFeatureKey(identity), InventoryNameSeedVersion,
                checked((int)MaximumPersistedEntryBytes), token);
            entry = seed is null ? null : MaterializeInventoryNameSeed(seed);
            compactInventorySeed = entry is not null;
        }
        catch (InvalidDataException) { entry = null; }
        if (entry is null)
        {
            try { entry = features.LoadBounded<CharacterCatalogModelEntry>(NameEntryFeatureKey(identity), NameIndexVersion,
                checked((int)MaximumPersistedEntryBytes), token); }
            catch (InvalidDataException) { return null; }
        }
        if (entry is null || entry.Descriptor.Identity != identity || entry.Descriptor.Revision != Revision ||
            entry.SourceSummary.Identity != identity || entry.SourceSummary.Revision != Revision ||
            entry.Descriptor.Family != "character-model" ||
            entry.Descriptor.Metadata.GetValueOrDefault("model.name-profile") != NameIndexVersion ||
            entry.SourceSummary.StatisticsAvailability != CharacterModelStatisticsAvailability.DeferredUntilSelection ||
            !ValidateSourceExtents(entry.SourceSummary.SourceExtents)) return null;
        if (retained is not null) retained.Add(memoryBudget.Reserve(EstimateCachedEntryRetention(entry), "model name cache"));
        return entry;
    }

    private CharacterCatalogModelEntry? MaterializeInventoryNameSeed(InventoryNameSeed seed)
    {
        var summary = seed.SourceSummary;
        if (summary is null || seed.Descriptor is null || seed.Descriptor.Names is null ||
            seed.Descriptor.Metadata is null || summary.SourceExtents is null || seed.CatalogLinks is null ||
            summary.Identity != seed.Descriptor.Identity || summary.Revision != seed.Descriptor.Revision ||
            summary.StatisticsAvailability != CharacterModelStatisticsAvailability.DeferredUntilSelection ||
            seed.Descriptor.Metadata.GetValueOrDefault("model.inventory-only") != "true" ||
            seed.Descriptor.Metadata.GetValueOrDefault("model.name-profile") != NameIndexVersion ||
            seed.CatalogLinks.Count > MaximumPersistedCatalogLinksPerModel ||
            !ValidateSourceExtents(summary.SourceExtents)) return null;
        var storedNames = seed.Descriptor.Names.Where(name => name.Origin == NameOrigin.Stored)
            .Select(name => new NameEvidence(name.Text, name.Origin, name.Source)).ToArray();
        var sourceSummary = new CharacterModelSourceSummary(summary.Identity, summary.Revision,
            summary.OwnerNativeType, summary.OwnerNativeId, [], storedNames, summary.SourceExtents,
            [], 0, summary.Complete)
        {
            StatisticsAvailability = summary.StatisticsAvailability,
            InventoryDisplayLabel = summary.InventoryDisplayLabel,
            SourceSkeletonV15Validated = summary.SourceSkeletonV15Validated,
            CachedVerifiedMetadata = seed.Descriptor.Metadata
                .Where(pair => CachedInventoryMetadataKeys.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
        return new(seed.Descriptor, sourceSummary, [], seed.CatalogLinks);
    }

    private NameOwnerManifest? LoadNameOwner(CatalogContext context, ResourceIdentity owner, CancellationToken token)
    {
        if (features is null) return null;
        using var scratch = memoryBudget.Reserve(MaximumPersistedEntryBytes * 3L, "model name membership scratch");
        NameOwnerManifest? manifest;
        try { manifest = features.LoadBounded<NameOwnerManifest>(NameOwnerKey(context.Identity, owner), NameIndexVersion,
            checked((int)MaximumPersistedEntryBytes), token); }
        catch (InvalidDataException) { return null; }
        if (manifest is null || manifest.Revision != Revision || manifest.CatalogIdentity != context.Identity ||
            manifest.OwnerIdentity != owner || manifest.ReferenceTableSha256 != context.Result.ReferenceTableSha256 ||
            manifest.ModelIdentities is null || manifest.EntryIdentities is null ||
            manifest.ModelIdentities.Count > MaximumPersistedEntryBytes / 128 ||
            manifest.ModelIdentities.Distinct().Count() != manifest.ModelIdentities.Count ||
            manifest.EntryIdentities.Distinct().Count() != manifest.EntryIdentities.Count ||
            manifest.EntryIdentities.Count > manifest.ModelIdentities.Count ||
            manifest.ModelIdentities.Any(identity => identity.IndexContentId != owner.IndexContentId ||
                identity.Ordinal != owner.Ordinal || identity.MemberPath is null) ||
            manifest.UnsupportedModels < 0 || manifest.UnsupportedModels > manifest.ModelIdentities.Count ||
            manifest.UnsupportedModels < manifest.ModelIdentities.Count - manifest.EntryIdentities.Count ||
            !ValidateSourceExtents(manifest.OwnerExtents)) return null;
        var members = manifest.ModelIdentities.ToHashSet();
        if (manifest.EntryIdentities.Any(identity => !members.Contains(identity))) return null;
        return manifest;
    }

    private NameEntryCoverageProjection? LoadNameEntryCoverageProjection(ResourceIdentity identity,
        ReservationGroup reservation, CancellationToken token)
    {
        if (features is null) return null;
        try
        {
            var projection = features.LoadCharacterCoverageProjection<NameEntryCoverageProjection>(NameEntryFeatureKey(identity),
                InventoryNameSeedVersion, checked((int)MaximumPersistedEntryBytes), checked((int)MaximumPersistedEntryBytes),
                bytes => reservation.Add(memoryBudget.Reserve(Math.Max(2048L, checked((long)bytes * 2L)),
                    "compact model name validation")), token);
            return projection ?? features.LoadCharacterCoverageProjection<NameEntryCoverageProjection>(NameEntryFeatureKey(identity),
                NameIndexVersion, checked((int)MaximumPersistedEntryBytes), checked((int)MaximumPersistedEntryBytes),
                bytes => reservation.Add(memoryBudget.Reserve(Math.Max(2048L, checked((long)bytes * 2L)),
                    "compact model name validation")), token);
        }
        catch (InvalidDataException) { return null; }
        catch (SqliteException) when (!token.IsCancellationRequested) { return null; }
    }

    private bool IsValidNameEntryCoverageProjection(NameEntryCoverageProjection? entry,
        ResourceIdentity identity, ResourceIdentity owner, IReadOnlyList<CatalogContext> contexts)
    {
        if (entry is null || entry.DescriptorIdentity != identity || entry.DescriptorRevision != Revision ||
            entry.DescriptorFamily != "character-model" || entry.NameProfile != NameIndexVersion ||
            entry.SourceSummaryIdentity != identity || entry.SourceSummaryRevision != Revision ||
            entry.StatisticsAvailability != CharacterModelStatisticsAvailability.DeferredUntilSelection ||
            entry.SourceExtents is null || !ValidateSourceExtents(entry.SourceExtents) ||
            entry.CatalogLinks is null || entry.CatalogLinks.Count > MaximumPersistedCatalogLinksPerModel)
            return false;

        return contexts.All(context => entry.CatalogLinks.Any(link => link.CatalogIdentity == context.Identity &&
            link.ReferenceTableSha256 == context.Result.ReferenceTableSha256 &&
            link.ResolutionCandidates?.Contains(owner) == true));
    }

    private bool TryRestoreNameOwnerMembership(ResourceIdentity ownerIdentity, bool inventoryOnly,
        IReadOnlyList<CatalogContext> contexts, Dictionary<ResourceIdentity, ModelAccumulator> accumulators,
        ReservationGroup retained, CancellationToken token, out int restoredMemberCount,
        out int restoredUnsupportedCount, List<CharacterCatalogIssue> issues,
        out IReadOnlyList<SourceExtent> restoredOwnerExtents, out IReadOnlyList<ResourceIdentity> restoredModelIdentities)
    {
        restoredMemberCount = 0; restoredUnsupportedCount = 0; restoredOwnerExtents = []; restoredModelIdentities = [];
        if (contexts.Count == 0) return false;
        using var staged = new ReservationGroup(); NameOwnerManifest? first = null;
        foreach (var context in contexts)
        {
            token.ThrowIfCancellationRequested();
            var manifest = LoadNameOwner(context, ownerIdentity, token);
            if (manifest is null || first is not null &&
                (!first.ModelIdentities.SequenceEqual(manifest.ModelIdentities) || !first.EntryIdentities.SequenceEqual(manifest.EntryIdentities) ||
                 !first.OwnerExtents.SequenceEqual(manifest.OwnerExtents) || first.UnsupportedModels != manifest.UnsupportedModels)) return false;
            first ??= manifest;
        }
        if (first is null) return false;
        staged.Add(memoryBudget.Reserve(checked(4096L + first.ModelIdentities.Sum(identity => 256L + identity.Key.Length * 2L) +
            first.OwnerExtents.Count * 512L), "model name membership"));
        var entries = new List<CharacterCatalogModelEntry>();
        foreach (var identity in first.EntryIdentities)
        {
            token.ThrowIfCancellationRequested();
            var entry = LoadNameEntry(identity, token, staged, out var compactSeed);
            if (entry is null || contexts.Any(context => !entry.CatalogLinks.Any(link => link.CatalogIdentity == context.Identity &&
                    link.ReferenceTableSha256 == context.Result.ReferenceTableSha256 &&
                    link.Relationship.Resolution.Candidates.Contains(ownerIdentity))) ||
                (inventoryOnly && entry.Descriptor.Metadata.GetValueOrDefault("model.inventory-profile") != InventoryProfile) ||
                (inventoryOnly ? entry.Bindings.Count != 0 : entry.Bindings.Count == 0)) return false;
            entries.Add(compactSeed ? entry with { Coverage = null } : entry);
        }
        retained.Add(staged.Detach());
        foreach (var entry in entries)
        {
            var accumulator = accumulators.TryGetValue(entry.Descriptor.Identity, out var existing)
                ? existing : accumulators[entry.Descriptor.Identity] = new(entry);
            accumulator.Add(entry.Bindings, entry.CatalogLinks);
            if (inventoryOnly && entry.SourceSummary.Lods.Count == 0 && entry.Bindings.Count == 0)
                accumulator.CachedInventoryDescriptor = entry.Descriptor;
        }
        restoredMemberCount = first.ModelIdentities.Count; restoredUnsupportedCount = first.UnsupportedModels;
        restoredOwnerExtents = first.OwnerExtents; restoredModelIdentities = first.ModelIdentities;
        if (first.UnsupportedModels > 0) issues.Add(new(CharacterCatalogIssueKind.MeshMetadata,
            "CharacterNameMetadataIncomplete", OwnerIdentity: ownerIdentity));
        foreach (var entry in entries)
            foreach (var binding in entry.Bindings)
            {
                if (binding.Diagnostic is { } diagnostic)
                    issues.Add(new(CharacterCatalogIssueKind.ModelBinding, diagnostic, binding.CatalogIdentity, ownerIdentity, entry.Descriptor.Identity));
                foreach (var lod in binding.Lods.Where(lod => lod.Diagnostic is not null))
                    issues.Add(new(CharacterCatalogIssueKind.MeshMetadata, lod.Diagnostic!, binding.CatalogIdentity, ownerIdentity, entry.Descriptor.Identity));
            }
        return true;
    }

    private bool TryRestoreNameOwnerCoverage(ResourceIdentity ownerIdentity,
        IReadOnlyList<CatalogContext> contexts,
        ReservationGroup retained, CancellationToken token, out int restoredMemberCount,
        out int restoredUnsupportedCount, List<CharacterCatalogIssue> issues,
        out IReadOnlyList<SourceExtent> restoredOwnerExtents, out IReadOnlyList<ResourceIdentity> restoredModelIdentities)
    {
        restoredMemberCount = 0; restoredUnsupportedCount = 0; restoredOwnerExtents = []; restoredModelIdentities = [];
        if (contexts.Count == 0) return false;
        using var staged = new ReservationGroup(); NameOwnerManifest? first = null;
        foreach (var context in contexts)
        {
            token.ThrowIfCancellationRequested();
            var manifest = LoadNameOwner(context, ownerIdentity, token);
            if (manifest is null || first is not null &&
                (!first.ModelIdentities.SequenceEqual(manifest.ModelIdentities) || !first.EntryIdentities.SequenceEqual(manifest.EntryIdentities) ||
                 !first.OwnerExtents.SequenceEqual(manifest.OwnerExtents))) return false;
            first ??= manifest;
        }
        if (first is null) return false;
        staged.Add(memoryBudget.Reserve(checked(4096L + first.ModelIdentities.Sum(identity => 256L + identity.Key.Length * 2L) +
            first.OwnerExtents.Count * 512L), "model name membership"));
        foreach (var identity in first.EntryIdentities)
        {
            token.ThrowIfCancellationRequested();
            using var compact = new ReservationGroup();
            var entry = LoadNameEntryCoverageProjection(identity, compact, token);
            if (!IsValidNameEntryCoverageProjection(entry, identity, ownerIdentity, contexts)) return false;
        }
        retained.Add(staged.Detach());
        restoredMemberCount = first.ModelIdentities.Count; restoredUnsupportedCount = first.UnsupportedModels;
        restoredOwnerExtents = first.OwnerExtents; restoredModelIdentities = first.ModelIdentities;
        if (first.UnsupportedModels > 0) issues.Add(new(CharacterCatalogIssueKind.MeshMetadata,
            "CharacterNameMetadataIncomplete", OwnerIdentity: ownerIdentity));
        return true;
    }

    private async Task<NameIndexWriteResult> PersistNameIndexAsync(IReadOnlyList<CatalogContext> contexts,
        Dictionary<ResourceIdentity, List<CatalogContext>> owners,
        Dictionary<(ResourceIdentity Catalog, ResourceIdentity Owner), OwnerMembershipBuilder> candidates,
        IReadOnlySet<ResourceIdentity> revalidated, IReadOnlyList<CharacterCatalogModelEntry> entries,
        bool inventoryOnly, CancellationToken token)
    {
        var persisted = new HashSet<ResourceIdentity>(); var covered = new HashSet<ResourceIdentity>();
        if (features is null) return new(persisted, covered);
        var entryByIdentity = entries.ToDictionary(entry => entry.Descriptor.Identity);
        await references.ValidateSnapshotAsync(token).ConfigureAwait(false); ValidateCurrentSourceStamps();
        using var eligibilityRetention = memoryBudget.Reserve(checked(4096L +
            candidates.Values.Sum(candidate => (long)candidate.ModelIdentities.Count) * 128L), "model name publication eligibility");
        var eligibleModels = new HashSet<ResourceIdentity>();
        foreach (var candidate in candidates.Values)
        {
            token.ThrowIfCancellationRequested();
            if (!revalidated.Contains(candidate.Context.Identity) || !candidate.EnumerationComplete ||
                !ValidateSourceExtents(candidate.OwnerExtents)) continue;
            foreach (var identity in candidate.ModelIdentities) eligibleModels.Add(identity);
        }
        const int batchLimit = 64;
        var batch = new List<BoundedFeatureAssetWrite>(batchLimit);
        var batchReservations = new ReservationGroup();
        long batchEstimateBytes = 0;
        void FlushModelBatch()
        {
            if (batch.Count == 0) return;
            foreach (var identity in features.PublishBoundedAssetsBatch(batch, token)) persisted.Add(identity);
            batch.Clear(); batchReservations.Dispose(); batchReservations = new ReservationGroup(); batchEstimateBytes = 0;
        }
        try
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (!eligibleModels.Contains(entry.Descriptor.Identity) || entry.Descriptor.Identity != entry.SourceSummary.Identity ||
                    entry.SourceSummary.Revision != Revision || !ValidateSourceExtents(entry.SourceSummary.SourceExtents)) continue;
                var compactSeed = inventoryOnly && IsInventoryNameSeedCandidate(entry);
                var featureVersion = compactSeed ? InventoryNameSeedVersion : NameIndexVersion;
                BoundedFeatureWrite feature;
                try
                {
                    feature = compactSeed
                        ? BoundedFeatureWrite.Create(NameEntryFeatureKey(entry.Descriptor.Identity), featureVersion,
                            CreateInventoryNameSeed(entry), checked((int)MaximumPersistedEntryBytes))
                        : BoundedFeatureWrite.Create(NameEntryFeatureKey(entry.Descriptor.Identity), featureVersion,
                            entry, checked((int)MaximumPersistedEntryBytes));
                }
                catch (InvalidDataException) { continue; }
                var estimate = feature.Utf8ByteCount;
                if (estimate > MaximumPersistedEntryBytes) continue;
                if (batch.Count > 0 && (batch.Count >= batchLimit ||
                    batchEstimateBytes + estimate > FeatureStore.MaximumBoundedFeatureBatchBytes / 2))
                    FlushModelBatch();

                var reservation = memoryBudget.Reserve(estimate, "model name cache write batch");
                batch.Add(new(feature, entry.Descriptor));
                batchReservations.Add(reservation);
                batchEstimateBytes += estimate;
            }
            FlushModelBatch();
        }
        finally { batchReservations.Dispose(); }

        // Publish membership and its completion checkpoint together, only after every model batch committed.
        foreach (var context in contexts)
        {
            token.ThrowIfCancellationRequested();
            if (!revalidated.Contains(context.Identity) || context.Links.Any(link => link.Diagnostic is not null ||
                link.Relationship.Resolution.Status != ReferenceStatus.Unique || link.Relationship.Resolution.Candidates.Count != 1)) continue;
            var targets = owners.Where(pair => pair.Value.Any(item => item.Identity == context.Identity))
                .Select(pair => pair.Key).OrderBy(identity => identity.Ordinal).ToArray();
            var memberCounts = new List<int>(targets.Length);
            var publication = new List<BoundedFeatureWrite>(targets.Length + 1);
            using var publicationReservations = new ReservationGroup();
            long publicationBytes = 0;
            bool complete = true;
            foreach (var owner in targets)
            {
                token.ThrowIfCancellationRequested();
                var candidate = candidates[(context.Identity, owner)];
                if (!candidate.EnumerationComplete || !ValidateSourceExtents(candidate.OwnerExtents)) { complete = false; break; }
                var members = candidate.ModelIdentities.Distinct().ToArray();
                memberCounts.Add(members.Length);
                var available = members.Where(persisted.Contains).ToArray();
                var unsupported = members.Count(identity => !persisted.Contains(identity) ||
                    entryByIdentity[identity] is { } entry &&
                    (!entry.SourceSummary.Complete || entry.Bindings.Any(binding => binding.Diagnostic is not null ||
                        binding.Lods.Any(lod => lod.Diagnostic is not null))));
                var manifest = new NameOwnerManifest(Revision, context.Identity, owner, context.Result.ReferenceTableSha256,
                    candidate.OwnerExtents, members, available, unsupported);
                BoundedFeatureWrite write;
                try
                {
                    write = BoundedFeatureWrite.Create(NameOwnerKey(context.Identity, owner), NameIndexVersion,
                        manifest, checked((int)MaximumPersistedEntryBytes));
                }
                catch (InvalidDataException) { complete = false; break; }
                if (publicationBytes + write.Utf8ByteCount > FeatureStore.MaximumBoundedFeatureBatchBytes)
                { complete = false; break; }
                try { publicationReservations.Add(memoryBudget.Reserve(checked(write.Utf8ByteCount * 2L), "name owner publication")); }
                catch (ArchiveException error) when (error.Code == "AssetMemoryLimit") { complete = false; break; }
                publication.Add(write); publicationBytes += write.Utf8ByteCount;
            }
            if (!complete) continue;
            await references.RevalidateAsync(context.Result, token).ConfigureAwait(false);
            await references.ValidateSnapshotAsync(token).ConfigureAwait(false); ValidateCurrentSourceStamps();
            var checkpoint = BoundedFeatureWrite.Create(NameCheckpointKey(context.Identity), NameIndexVersion,
                new NameCheckpoint(Revision, context.Identity, context.Result.ReferenceTableSha256, targets, memberCounts),
                checked((int)MaximumPersistedEntryBytes));
            if (publicationBytes + checkpoint.Utf8ByteCount > FeatureStore.MaximumBoundedFeatureBatchBytes) continue;
            try { publicationReservations.Add(memoryBudget.Reserve(checked(checkpoint.Utf8ByteCount * 2L), "name checkpoint publication")); }
            catch (ArchiveException error) when (error.Code == "AssetMemoryLimit") { continue; }
            publication.Add(checkpoint);
            features.SaveBoundedBatch(publication, token);
            covered.Add(context.Identity);
        }
        return new(persisted, covered);
    }

    private static bool IsInventoryNameSeedCandidate(CharacterCatalogModelEntry entry) =>
        entry.SourceSummary.StatisticsAvailability == CharacterModelStatisticsAvailability.DeferredUntilSelection &&
        entry.Descriptor.Metadata.GetValueOrDefault("model.inventory-only") == "true" && entry.Bindings.Count == 0;

    private static InventoryNameSeed CreateInventoryNameSeed(CharacterCatalogModelEntry entry)
    {
        var summary = entry.SourceSummary;
        return new(entry.Descriptor, new(summary.Identity, summary.Revision, summary.OwnerNativeType,
            summary.OwnerNativeId, summary.SourceExtents, summary.Complete, summary.StatisticsAvailability,
            summary.InventoryDisplayLabel ?? entry.Descriptor.Metadata.GetValueOrDefault("model.display-label"),
            summary.SourceSkeletonV15Validated), entry.CatalogLinks);
    }


    /// <summary>Restores completed name-index passes, including empty and unsupported owners.</summary>
    public async Task<CharacterCatalogCoverageRestoreResult> RestoreNameCoverageAsync(int startCatalogIndex,
        int maximumCatalogs, IProgress<CharacterCatalogCoverageRestoreProgress>? progress = null, CancellationToken token = default)
    {
        if (maximumCatalogs < 1) throw new ArgumentOutOfRangeException(nameof(maximumCatalogs));
        int total = references.CatalogIdentities.Count;
        if (startCatalogIndex < 0 || startCatalogIndex > total) throw new ArgumentOutOfRangeException(nameof(startCatalogIndex));
        int limit = Math.Min(maximumCatalogs, Math.Min(options.MaximumCatalogsPerBatch, references.MaximumBatchCatalogs));
        int stop = Math.Min(total, checked(startCatalogIndex + limit));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            EnsureAvailable(); await references.ValidateSnapshotAsync(linked.Token).ConfigureAwait(false); ValidateCurrentSourceStamps();
            using var retained = new ReservationGroup(); var restored = new List<ResourceIdentity>();
            var issues = new List<CharacterCatalogIssue>(); int unsupported = 0;
            if (stop == startCatalogIndex)
            {
                var previous = coveredCatalogIndexes.Order().Select(index => references.CatalogIdentities[index]).ToArray();
                retained.Add(memoryBudget.Reserve(4096L + previous.Length * 512L, "model name coverage result"));
                return new(Revision, startCatalogIndex, stop, total, 0, [], [], 0, true, previous, retained.Detach());
            }
            using var candidateRetention = new ReservationGroup();
            var candidates = new List<(int Index, ResourceIdentity Identity, NameCheckpoint Checkpoint)>();
            var stagedIndexes = new List<int>();
            for (int index = startCatalogIndex; index < stop; index++)
            {
                linked.Token.ThrowIfCancellationRequested();
                var identity = references.CatalogIdentities[index];
                if (features is null) continue;
                NameCheckpoint? checkpoint;
                using (memoryBudget.Reserve(MaximumPersistedEntryBytes * 3L, "model name checkpoint scratch"))
                {
                    try { checkpoint = features.LoadBounded<NameCheckpoint>(NameCheckpointKey(identity), NameIndexVersion,
                        checked((int)MaximumPersistedEntryBytes), linked.Token); }
                    catch (InvalidDataException)
                    { issues.Add(new(CharacterCatalogIssueKind.AssetIndex, "CharacterNameCheckpointInvalid", identity)); continue; }
                }
                if (checkpoint is null || checkpoint.Revision != Revision || checkpoint.CatalogIdentity != identity ||
                    checkpoint.ReferenceTableSha256 is not { Length: 64 } hash || !IsSha256(hash) || checkpoint.Owners is null ||
                    checkpoint.Owners.Count > MaximumPersistedEntryBytes / 128 || checkpoint.OwnerMemberCounts is null ||
                    checkpoint.OwnerMemberCounts.Count != checkpoint.Owners.Count ||
                    checkpoint.OwnerMemberCounts.Any(count => count < 0 || count > MaximumPersistedEntryBytes / 128) ||
                    checkpoint.Owners.Any(owner => owner is null || owner.IndexContentId != Revision.IndexContentId ||
                        owner.MemberPath is not null || owner.Ordinal < 0 || owner.Ordinal >= snapshot.Records.Count) ||
                    checkpoint.Owners.Distinct().Count() != checkpoint.Owners.Count)
                { issues.Add(new(CharacterCatalogIssueKind.AssetIndex, "CharacterNameCheckpointMissingOrInvalid", identity)); continue; }
                candidateRetention.Add(memoryBudget.Reserve(checked(4096L + checkpoint.Owners.Sum(owner => 512L + owner.Key.Length * 2L)),
                    "model name checkpoint candidates"));
                candidates.Add((index, identity, checkpoint));
            }
            foreach (var candidate in candidates)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (candidate.Identity != references.CatalogIdentities[candidate.Index])
                    throw new ArchiveException("CharacterCoverageCursorInvalid", "The catalog order changed.");
                CatalogReferenceLease lease;
                try { lease = await references.ReadAsync(candidate.Identity, linked.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is ArchiveException archive
                    ? archive.Code is not ("SourceChanged" or "AssetMemoryLimit")
                    : error is IOException or ArgumentException or OverflowException or JsonException or SqliteException)
                {
                    issues.Add(new(CharacterCatalogIssueKind.AssetIndex,
                        error is ArchiveException failed ? failed.Code : "CatalogReferenceUnavailable", candidate.Identity));
                    continue;
                }
                using var candidateLease = lease;
                var table = lease.Result;
                if (!IsVerifiedRead(candidate.Identity, table)) continue;
                await references.RevalidateAsync(table, linked.Token).ConfigureAwait(false);
                var checkpoint = candidate.Checkpoint;
                if (checkpoint.ReferenceTableSha256 != table.ReferenceTableSha256)
                { issues.Add(new(CharacterCatalogIssueKind.AssetIndex, "CharacterNameCheckpointMissingOrInvalid", candidate.Identity)); continue; }
                var contextIssues = new List<CharacterCatalogIssue>(); var context = new CatalogContext(candidate.Identity, table,
                    MakeCatalogLinks(candidate.Identity, table, contextIssues));
                var targets = BuildOwnerContexts([context], contextIssues).Keys.OrderBy(identity => identity.Ordinal).ToArray();
                if (context.Links.Any(link => link.Diagnostic is not null || link.Relationship.Resolution.Status != ReferenceStatus.Unique ||
                        link.Relationship.Resolution.Candidates.Count != 1) || !targets.SequenceEqual(checkpoint.Owners))
                { issues.Add(new(CharacterCatalogIssueKind.AssetIndex, "CharacterNameCheckpointMembershipInvalid", candidate.Identity)); continue; }
                using var staged = new ReservationGroup(); bool valid = true; int ownerUnsupported = 0;
                for (int ownerIndex = 0; ownerIndex < targets.Length; ownerIndex++)
                {
                    var target = targets[ownerIndex];
                    if (!TryRestoreNameOwnerCoverage(target, [context], staged, linked.Token,
                        out var memberCount, out var unavailable, contextIssues, out _, out _) ||
                        memberCount != checkpoint.OwnerMemberCounts[ownerIndex]) { valid = false; break; }
                    ownerUnsupported += unavailable;
                }
                if (!valid)
                { issues.Add(new(CharacterCatalogIssueKind.AssetIndex, "CharacterNameOwnerMetadataInvalid", candidate.Identity)); continue; }
                await references.RevalidateAsync(table, linked.Token).ConfigureAwait(false);
                restored.Add(candidate.Identity); stagedIndexes.Add(candidate.Index);
                unsupported += ownerUnsupported; issues.AddRange(contextIssues);
            }
            await references.ValidateSnapshotAsync(linked.Token).ConfigureAwait(false); ValidateCurrentSourceStamps();
            linked.Token.ThrowIfCancellationRequested();
            var covered = coveredCatalogIndexes.Concat(stagedIndexes).Distinct().Order()
                .Select(index => references.CatalogIdentities[index]).ToArray();
            retained.Add(memoryBudget.Reserve(4096L + (covered.Length + restored.Count) * 512L + issues.Count * 1024L,
                "model name coverage result"));
            int attemptedCount = stop - startCatalogIndex;
            progress?.Report(new(startCatalogIndex, stop, total, attemptedCount, restored.Count,
                attemptedCount - restored.Count, unsupported, total - covered.Length, covered.Length == total));
            linked.Token.ThrowIfCancellationRequested();
            ValidateCurrentSourceStamps(); await references.ValidateSnapshotAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            foreach (int index in stagedIndexes) coveredCatalogIndexes.Add(index);
            return new(Revision, startCatalogIndex, stop, total, attemptedCount, restored.ToArray(), issues.ToArray(), unsupported,
                unsupported == 0, covered, retained.Detach());
        }
        finally { operationGate.Release(); }
    }

    /// <summary>Loads and verifies full model details when a catalog row is selected.</summary>
    public async Task<CharacterCatalogModelSnapshot> HaveModelEntryForSelectionAsync(ResourceIdentity identity,
        SourceRevision expectedRevision, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(identity); ValidateExpectedRevision(expectedRevision);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        BeginForegroundModelSelection();
        var acquired = false;
        try
        {
            await operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            EnsureAvailable(); await references.ValidateSnapshotAsync(linked.Token).ConfigureAwait(false); ValidateCurrentSourceStamps();
            using var retained = new ReservationGroup();
            var names = LoadNameEntry(identity, linked.Token, retained);
            if (names is null) throw new ArchiveException("CharacterModelUnavailable", "The selected catalog row is unavailable.");
            using var lease = await NativeContainerLease.OpenMetadataAsync(snapshot,
                new(identity.IndexContentId, identity.Ordinal), memoryBudget, linked.Token).ConfigureAwait(false);
            var frame = lease.Directory.Objects.SingleOrDefault(frame => frame.Identity == identity)
                ?? throw new ArchiveException("CharacterModelUnavailable", "The selected model member is unavailable.");
            var bindings = new List<CharacterModelBindingResult>();
            foreach (var catalogIdentity in names.CatalogLinks.Select(link => link.CatalogIdentity).Distinct())
            {
                linked.Token.ThrowIfCancellationRequested();
                using var catalog = await references.ReadAsync(catalogIdentity, linked.Token).ConfigureAwait(false);
                await references.RevalidateAsync(catalog.Result, linked.Token).ConfigureAwait(false);
                bindings.Add(bindingResolver(lease, CreateInputDescriptor(frame, lease), catalogIdentity, catalog.Result,
                    new(IncludeSkeletonNameEvidence: options.IncludeSkeletonNameEvidence,
                        MaximumSkeletonNameEvidence: options.MaximumSkeletonNameEvidence), linked.Token));
            }
            if (bindings.Count == 0) throw new ArchiveException("CharacterModelUnavailable", "The selected catalog association is unavailable.");
            var binding = bindings.FirstOrDefault(item => item.Diagnostic is null) ?? bindings[0];
            var summary = GetSourceSummary(lease, frame, binding, null, [], retained, linked.Token);
            var accumulator = new ModelAccumulator(summary); accumulator.Add(bindings, names.CatalogLinks);
            // Keep the row's published label and sort position while replacing inventory classification with validated detail.
            var enriched = BuildEntry(accumulator);
            var published = ApplyCurrentAlias(names.Descriptor);
            var descriptor = enriched.Descriptor with
            {
                Label = published.Label,
                SortKey = published.SortKey,
                Names = enriched.Descriptor.Names.Concat(published.Names.Where(name => name.Origin == NameOrigin.UserAlias))
                    .Distinct().ToArray()
            };
            descriptor = descriptor with { SearchTerms = ModelCatalogClassification.SearchTerms(descriptor) };
            var full = enriched with { Descriptor = descriptor, Coverage = names.Coverage };
            lease.EnsureUnchanged(); await references.ValidateSnapshotAsync(linked.Token).ConfigureAwait(false); ValidateCurrentSourceStamps();
            retained.Add(memoryBudget.Reserve(EstimateEntryRetention(accumulator), "selected model details"));
            await PublishSelectedModelEntryAsync(full, lease, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested(); return new(full, retained.Detach());
        }
        finally
        {
            if (acquired) operationGate.Release();
            EndForegroundModelSelection();
        }
    }

    private async Task PublishSelectedModelEntryAsync(CharacterCatalogModelEntry full,
        NativeContainerLease lease, CancellationToken token)
    {
        if (features is not { } store || EstimatePersistedEntryBytes(full) > MaximumPersistedEntryBytes) return;
        token.ThrowIfCancellationRequested();
        AssetMemoryBudget.Reservation publicationMemory;
        try
        {
            publicationMemory = memoryBudget.Reserve(checked(MaximumPersistedEntryBytes * 12L + 16384),
                "selected model publication scratch");
        }
        catch (ArchiveException error) when (error.Code == "AssetMemoryLimit") { return; }
        using (publicationMemory)
        {
            var metadata = new Dictionary<string, string>(full.Descriptor.Metadata, StringComparer.Ordinal)
            {
                ["model.inventory-only"] = "true",
                ["model.name-profile"] = NameIndexVersion
            };
            var seedEntry = full with
            {
                Descriptor = full.Descriptor with { Metadata = metadata },
                SourceSummary = full.SourceSummary with
                {
                    StatisticsAvailability = CharacterModelStatisticsAvailability.DeferredUntilSelection
                },
                Bindings = []
            };
            BoundedFeatureWrite names, detail;
            try
            {
                names = BoundedFeatureWrite.Create(NameEntryFeatureKey(full.Descriptor.Identity), InventoryNameSeedVersion,
                    CreateInventoryNameSeed(seedEntry), checked((int)MaximumPersistedEntryBytes));
                detail = BoundedFeatureWrite.Create(ModelEntryFeatureKey(full.Descriptor.Identity), modelEntryVersion, full,
                    checked((int)MaximumPersistedEntryBytes));
            }
            catch (Exception error) when (error is InvalidDataException or JsonException) { return; }
            token.ThrowIfCancellationRequested();
            lease.EnsureUnchanged();
            await references.ValidateSnapshotAsync(token).ConfigureAwait(false);
            ValidateCurrentSourceStamps();
            _ = store.PublishBoundedAssetWithEvidence(new(names, full.Descriptor), [detail], token);
        }
    }
}
