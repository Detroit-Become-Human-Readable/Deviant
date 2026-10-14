using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;

namespace Deviant.Browser;

public sealed record CharacterCatalogCoverageRestoreProgress(int StartCatalogIndex,
    int NextCatalogIndex, int TotalCatalogs, int CatalogsRead, int RestoredCatalogs,
    int ExcludedCatalogs, int UnsupportedModels, int UnscannedCatalogs,
    bool AllCatalogsVisited);

/// <summary>Coverage restoration reports catalog-pass visitation separately from model support.</summary>
public sealed class CharacterCatalogCoverageRestoreResult : IDisposable
{
    private IDisposable? retention;

    internal CharacterCatalogCoverageRestoreResult(SourceRevision revision,
        int startCatalogIndex, int nextCatalogIndex, int totalCatalogs,
        int catalogsRead,
        IReadOnlyList<ResourceIdentity> restoredCatalogs,
        IReadOnlyList<CharacterCatalogIssue> issues, int unsupportedModels,
        bool allPersistedModelMetadataSupported, IReadOnlyList<ResourceIdentity> coveredCatalogs, IDisposable retention)
    {
        Revision = revision;
        StartCatalogIndex = startCatalogIndex;
        NextCatalogIndex = nextCatalogIndex;
        TotalCatalogs = totalCatalogs;
        CatalogsRead = catalogsRead;
        RestoredCatalogs = restoredCatalogs;
        Issues = issues;
        UnsupportedModels = unsupportedModels;
        CoveredCatalogs = coveredCatalogs;
        UnscannedCatalogs = Math.Max(0, totalCatalogs - coveredCatalogs.Count);
        AllCatalogsVisited = UnscannedCatalogs == 0;
        AllPersistedModelMetadataSupported = allPersistedModelMetadataSupported &&
            unsupportedModels == 0 && issues.Count == 0;
        this.retention = retention;
    }

    public SourceRevision Revision { get; }
    public int StartCatalogIndex { get; }
    public int NextCatalogIndex { get; }
    public int TotalCatalogs { get; }
    public int CatalogsRead { get; }
    public int ExcludedCatalogs => Math.Max(0, CatalogsRead - RestoredCatalogs.Count);
    public IReadOnlyList<ResourceIdentity> RestoredCatalogs { get; }
    public IReadOnlyList<ResourceIdentity> CoveredCatalogs { get; }
    public IReadOnlyList<CharacterCatalogIssue> Issues { get; }
    public int UnsupportedModels { get; }
    public int UnscannedCatalogs { get; }
    public bool AllCatalogsVisited { get; }
    public bool AllPersistedModelMetadataSupported { get; }
    public void Dispose() => Interlocked.Exchange(ref retention, null)?.Dispose();
}

/// <summary>
/// This partial is intended to be added beside the existing CharacterCatalogSession.
/// Restore never treats the highest cached ordinal as coverage; every index needs a
/// checkpoint matched to a freshly read and revalidated relationship table.
/// </summary>
public sealed partial class CharacterCatalogSession
{
    private const string CoverageCheckpointVersion = "character-catalog-coverage-v1";
    private const string CoverageOwnerManifestVersion = "character-catalog-coverage-owner-v1";
    private const int MaximumCoverageDiagnostics = 512;
    private const int MaximumCoverageModels = 131_072;

    private string CoverageCheckpointCollection =>
        $"character-catalog-coverage/{snapshot.IndexContentId}/{snapshot.PhysicalSourceStamp}";
    private string CoverageCheckpointCacheVersion => $"{CoverageCheckpointVersion}/{modelEntryVersion}";
    private string CoverageOwnerManifestCacheVersion => $"{CoverageOwnerManifestVersion}/{modelEntryVersion}";

    /// <summary>Validates and restores only exact catalog passes covered by persisted checkpoints.</summary>
    public async Task<CharacterCatalogCoverageRestoreResult> RestoreCoverageAsync(
        int startCatalogIndex, int maximumCatalogs,
        IProgress<CharacterCatalogCoverageRestoreProgress>? progress = null,
        CancellationToken token = default)
    {
        if (maximumCatalogs < 1) throw new ArgumentOutOfRangeException(nameof(maximumCatalogs));
        var total = references.CatalogIdentities.Count;
        if (startCatalogIndex < 0 || startCatalogIndex > total)
            throw new ArgumentOutOfRangeException(nameof(startCatalogIndex));
        if (features is null)
            throw new ArchiveException("CharacterCoverageStoreUnavailable", "Coverage restore requires the shared feature store.");

        var limit = Math.Min(maximumCatalogs,
            Math.Min(options.MaximumCatalogsPerBatch, references.MaximumBatchCatalogs));
        var stopIndex = Math.Min(total, checked(startCatalogIndex + limit));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            EnsureAvailable();
            await references.ValidateSnapshotAsync(linked.Token).ConfigureAwait(false);
            ValidateCurrentSourceStamps();

            using var retained = new ReservationGroup();
            retained.Add(memoryBudget.Reserve(checked(4096L + limit * 70_000L),
                "coverage restore projection"));
            var restored = new List<ResourceIdentity>();
            var issues = new List<CharacterCatalogIssue>();
            var restoredUnsupported = 0;
            var allPersistedModelMetadataSupported = true;
            var catalogsRead = 0;
            var stagedIndexes = new List<int>();
            if (stopIndex > startCatalogIndex)
            {
                using var batch = await references.ReadBatchAsync(startCatalogIndex,
                    stopIndex - startCatalogIndex, linked.Token).ConfigureAwait(false);
                ValidateBatchCursor(batch, startCatalogIndex, stopIndex, total);
                catalogsRead = batch.Rows.Count;
                for (var offset = 0; offset < batch.Rows.Count; offset++)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    var row = batch.Rows[offset];
                    if (row.Identity != references.CatalogIdentities[startCatalogIndex + offset])
                        throw new ArchiveException("CharacterCoverageCursorInvalid", "The provider batch did not preserve catalog order.");
                    if (row.Lease is null)
                    {
                        AddRestoreIssue(issues, new(CharacterCatalogIssueKind.CatalogRead,
                            row.Diagnostic ?? "CharacterCoverageTableUnavailable", row.Identity));
                        continue;
                    }

                    var table = row.Lease.Result;
                    if (!IsVerifiedCoverageTable(row.Identity, table))
                    {
                        AddRestoreIssue(issues, new(CharacterCatalogIssueKind.CatalogRead,
                            "CharacterCoverageTableUnverified", row.Identity));
                        continue;
                    }

                    await references.RevalidateAsync(table, linked.Token).ConfigureAwait(false);
                    using var checkpointReservation = memoryBudget.Reserve(MaximumPersistedEntryBytes,
                        "coverage checkpoint read");
                    var checkpoint = LoadCoverageCheckpoint(startCatalogIndex + offset, linked.Token);
                    if (checkpoint is null)
                    {
                        AddRestoreIssue(issues, new(CharacterCatalogIssueKind.AssetIndex,
                            "CharacterCoverageCheckpointMissing", row.Identity));
                        continue;
                    }

                    var validation = ValidateCoverageCheckpoint(checkpoint, startCatalogIndex + offset,
                        row.Identity, table, retained, issues, linked.Token);
                    if (!validation.Valid)
                    {
                        AddRestoreIssue(issues, new(CharacterCatalogIssueKind.AssetIndex,
                            validation.Diagnostic ?? "CharacterCoverageCheckpointInvalid", row.Identity));
                        continue;
                    }

                    stagedIndexes.Add(startCatalogIndex + offset);
                    restored.Add(row.Identity);
                    restoredUnsupported = checked(restoredUnsupported + validation.UnsupportedModels);
                    allPersistedModelMetadataSupported &= validation.AllPersistedModelMetadataSupported;
                }
            }

            linked.Token.ThrowIfCancellationRequested();
            await references.ValidateSnapshotAsync(linked.Token).ConfigureAwait(false);
            ValidateCurrentSourceStamps();
            var prospectiveIndexes = coveredCatalogIndexes.Concat(stagedIndexes).Distinct().Order().ToArray();
            var covered = prospectiveIndexes.Select(index => references.CatalogIdentities[index]).ToArray();
            var excluded = Math.Max(0, catalogsRead - restored.Count);
            var unscanned = Math.Max(0, total - prospectiveIndexes.Length);
            var allVisited = unscanned == 0;
            var restoredArray = restored.ToArray();
            var issueArray = issues.ToArray();
            retained.Add(memoryBudget.Reserve(checked(1024L + covered.Length * 128L +
                restoredArray.Length * 128L + issueArray.Length * 256L), "character catalog coverage restore result"));
            linked.Token.ThrowIfCancellationRequested();
            progress?.Report(new(startCatalogIndex, stopIndex, total, catalogsRead,
                restored.Count, excluded, restoredUnsupported, unscanned, allVisited));
            linked.Token.ThrowIfCancellationRequested();
            ValidateCurrentSourceStamps();
            foreach (var index in stagedIndexes) coveredCatalogIndexes.Add(index);
            return new(Revision, startCatalogIndex, stopIndex, total,
                catalogsRead, restoredArray, issueArray, restoredUnsupported,
                allPersistedModelMetadataSupported,
                covered, retained.Detach());
        }
        finally { operationGate.Release(); }
    }

    /// <summary>
    /// Called by discovery after table/owner revalidation and entry persistence. The existing
    /// OwnerMembershipBuilder.Complete retains its original all-model-support meaning;
    /// EnumerationComplete is set only after a complete, unchanged owner walk or a validated
    /// previously published owner membership restore.
    /// </summary>
    private Task<IReadOnlySet<ResourceIdentity>> PersistCoverageCheckpointsAsync(
        IReadOnlyList<CatalogContext> contexts,
        IReadOnlyDictionary<ResourceIdentity, List<CatalogContext>> ownerContexts,
        IReadOnlyDictionary<(ResourceIdentity Catalog, ResourceIdentity Owner), OwnerMembershipBuilder> membershipCandidates,
        IReadOnlySet<ResourceIdentity> revalidatedCatalogs,
        IReadOnlySet<ResourceIdentity> persistedModelIdentities,
        IReadOnlyList<CharacterCatalogModelEntry> entries,
        List<CharacterCatalogIssue> issues,
        CancellationToken token)
    {
        if (features is null)
        {
            var inMemoryPasses = new HashSet<ResourceIdentity>();
            var inMemoryEntries = entries.ToDictionary(entry => entry.Descriptor.Identity);
            foreach (var context in contexts)
            {
                if (!revalidatedCatalogs.Contains(context.Identity)) continue;
                var owners = GetUniqueCoverageOwners(context);
                var enumerated = owners.All(owner => membershipCandidates.TryGetValue((context.Identity, owner), out var candidate) &&
                    candidate.EnumerationComplete &&
                    candidate.OwnerExtents.Count > 0 && ValidateSourceExtents(candidate.OwnerExtents) &&
                    candidate.ModelIdentities.All(identity => inMemoryEntries.TryGetValue(identity, out var entry)
                        ? entry.Bindings.Any(binding => binding.CatalogIdentity == context.Identity) ||
                            issues.Any(issue => issue.CatalogIdentity == context.Identity && issue.ModelIdentity == identity)
                        : issues.Any(issue => issue.CatalogIdentity == context.Identity && issue.ModelIdentity == identity)));
                if (enumerated) inMemoryPasses.Add(context.Identity);
            }
            return Task.FromResult<IReadOnlySet<ResourceIdentity>>(inMemoryPasses);
        }
        var savedOwnerManifests = new HashSet<(ResourceIdentity Catalog, ResourceIdentity Owner)>();
        var persistedPasses = new HashSet<ResourceIdentity>();
        var entriesByIdentity = entries.ToDictionary(entry => entry.Descriptor.Identity);

        foreach (var candidate in membershipCandidates.Values)
        {
            token.ThrowIfCancellationRequested();
            if (!revalidatedCatalogs.Contains(candidate.Context.Identity) ||
                candidate.Context.Result.ReferenceTableSha256 is not { } tableHash ||
                !candidate.EnumerationComplete ||
                candidate.OwnerExtents.Count == 0 || !ValidateSourceExtents(candidate.OwnerExtents) ||
                candidate.ModelIdentities.Count > MaximumCoverageModels ||
                candidate.ModelIdentities.Distinct().Count() != candidate.ModelIdentities.Count)
                continue;

            var entryIdentities = candidate.ModelIdentities.Where(persistedModelIdentities.Contains)
                .OrderBy(identity => identity.Ordinal).ThenBy(identity => identity.MemberPath, StringComparer.Ordinal).ToArray();
            var unsupportedIds = candidate.ModelIdentities.Where(identity =>
            {
                entriesByIdentity.TryGetValue(identity, out var entry);
                return !persistedModelIdentities.Contains(identity) ||
                    issues.Any(issue => issue.ModelIdentity == identity && issue.CatalogIdentity == candidate.Context.Identity &&
                        IsModelMetadataIssue(issue)) ||
                    entry is null || !entry.SourceSummary.Complete ||
                    entry.SourceSummary.StatisticsAvailability != CharacterModelStatisticsAvailability.Available ||
                    !entry.Bindings.Any(binding => binding.CatalogIdentity == candidate.Context.Identity) ||
                    entry.Bindings.Any(binding => binding.CatalogIdentity == candidate.Context.Identity &&
                        (binding.Diagnostic is not null || binding.Lods.Any(lod => lod.Diagnostic is not null)));
            }).ToArray();
            var unexplainedCacheMiss = unsupportedIds.Any(identity =>
                !persistedModelIdentities.Contains(identity) &&
                !issues.Any(issue => issue.ModelIdentity == identity && issue.CatalogIdentity == candidate.Context.Identity) &&
                !(entriesByIdentity.TryGetValue(identity, out var unavailableEntry) &&
                    unavailableEntry.SourceSummary.StatisticsAvailability != CharacterModelStatisticsAvailability.Available));
            if (unexplainedCacheMiss) continue;
            var unsupported = unsupportedIds
                .Select(identity => new CoverageUnsupportedMember(identity,
                    issues.Where(issue => issue.ModelIdentity == identity && issue.CatalogIdentity == candidate.Context.Identity &&
                            IsModelMetadataIssue(issue))
                        .Select(issue => LimitText(issue.Code, 128)).Distinct(StringComparer.Ordinal).Take(16)
                        .DefaultIfEmpty(entriesByIdentity.TryGetValue(identity, out var unavailableEntry) &&
                                unavailableEntry.SourceSummary.StatisticsAvailability != CharacterModelStatisticsAvailability.Available
                            ? "CharacterModelStatisticsUnavailable"
                            : persistedModelIdentities.Contains(identity)
                                ? "CharacterModelMetadataIncomplete" : "CharacterModelEntryNotPersisted").ToArray()))
                .ToArray();
            var manifest = new CoverageOwnerManifest(Revision, candidate.Context.Identity, tableHash,
                candidate.OwnerIdentity, candidate.OwnerExtents.ToArray(),
                candidate.ModelIdentities.OrderBy(identity => identity.Ordinal)
                    .ThenBy(identity => identity.MemberPath, StringComparer.Ordinal).ToArray(),
                entryIdentities.Select(ModelEntryFeatureKey).ToArray(), entryIdentities,
                unsupported, candidate.Complete, EnumerationComplete: candidate.EnumerationComplete);
            var key = CoverageOwnerManifestKey(candidate.Context.Identity, candidate.OwnerIdentity);
            try
            {
                using var saveReservation = memoryBudget.Reserve(MaximumPersistedEntryBytes,
                    "coverage owner manifest write");
                features.SaveBounded(key, CoverageOwnerManifestCacheVersion, manifest,
                    checked((int)MaximumPersistedEntryBytes), token);
                savedOwnerManifests.Add((candidate.Context.Identity, candidate.OwnerIdentity));
            }
            catch (InvalidDataException)
            {
                AddRestoreIssue(issues, new(CharacterCatalogIssueKind.AssetIndex,
                    "CharacterCoverageOwnerManifestLimit", candidate.Context.Identity, candidate.OwnerIdentity));
            }
        }

        foreach (var context in contexts)
        {
            token.ThrowIfCancellationRequested();
            if (!revalidatedCatalogs.Contains(context.Identity) ||
                context.Result.ReferenceTableSha256 is not { } tableHash) continue;
            var owners = GetUniqueCoverageOwners(context);
            if (owners.Any(owner => !savedOwnerManifests.Contains((context.Identity, owner)))) continue;
            if (owners.Any(owner => !ownerContexts.TryGetValue(owner, out var contextsForOwner) ||
                !contextsForOwner.Any(item => item.Identity == context.Identity))) continue;

            var modelIdentities = owners.SelectMany(owner =>
                    membershipCandidates.TryGetValue((context.Identity, owner), out var candidate)
                        ? candidate.ModelIdentities : Enumerable.Empty<ResourceIdentity>())
                .Distinct().ToArray();
            var modelIdentitySet = modelIdentities.ToHashSet();
            var unsupportedIds = modelIdentitySet.Where(identity =>
                !persistedModelIdentities.Contains(identity) ||
                issues.Any(issue => issue.CatalogIdentity == context.Identity && issue.ModelIdentity == identity &&
                    IsModelMetadataIssue(issue)) ||
                entriesByIdentity.TryGetValue(identity, out var entry) &&
                (!entry.SourceSummary.Complete ||
                    entry.SourceSummary.StatisticsAvailability != CharacterModelStatisticsAvailability.Available ||
                    !entry.Bindings.Any(binding => binding.CatalogIdentity == context.Identity) ||
                    entry.Bindings.Any(binding => binding.Diagnostic is not null ||
                    binding.Lods.Any(lod => lod.Diagnostic is not null)))).ToHashSet();
            var unsupportedCount = unsupportedIds.Count;
            var checkpointIssues = issues.Where(issue => issue.CatalogIdentity == context.Identity)
                .Select(issue => new CoverageIssue(issue.Kind, LimitText(issue.Code, 128),
                    issue.OwnerIdentity, issue.ModelIdentity, issue.RelationshipIndex))
                .Take(MaximumCoverageDiagnostics).ToArray();
            var omittedIssues = Math.Max(0, issues.Count(issue => issue.CatalogIdentity == context.Identity) - checkpointIssues.Length);
            var modelMetadataSupported = unsupportedCount == 0 && checkpointIssues.Length == 0 &&
                omittedIssues == 0 && owners.All(owner => membershipCandidates[(context.Identity, owner)].Complete);
            var checkpoint = new CatalogCoverageCheckpoint(Revision,
                references.CatalogIdentities.IndexOfIdentity(context.Identity), context.Identity,
                tableHash, context.Result.Relationships.Count, owners,
                modelIdentities.Length, unsupportedCount,
                EnumerationComplete: true, AllPersistedModelMetadataSupported: modelMetadataSupported,
                OmittedIssueCount: omittedIssues,
                Issues: checkpointIssues);
            using var saveReservation = memoryBudget.Reserve(MaximumPersistedEntryBytes,
                "catalog coverage checkpoint write");
            try
            {
                features.SaveBounded(CoverageCheckpointKey(checkpoint.CatalogIndex),
                    CoverageCheckpointCacheVersion, checkpoint, checked((int)MaximumPersistedEntryBytes), token);
                persistedPasses.Add(context.Identity);
            }
            catch (InvalidDataException)
            {
                AddRestoreIssue(issues, new(CharacterCatalogIssueKind.AssetIndex,
                    "CharacterCoverageCheckpointLimit", context.Identity));
            }
        }
        return Task.FromResult<IReadOnlySet<ResourceIdentity>>(persistedPasses);
    }

    private CoverageValidation ValidateCoverageCheckpoint(CatalogCoverageCheckpoint checkpoint,
        int expectedIndex, ResourceIdentity expectedIdentity, LocalizationReferenceReadResult table,
        ReservationGroup retained, List<CharacterCatalogIssue> restoredIssues, CancellationToken token)
    {
        if (checkpoint.Revision != Revision || checkpoint.CatalogIndex != expectedIndex ||
            checkpoint.CatalogIdentity != expectedIdentity ||
            checkpoint.ReferenceTableSha256 != table.ReferenceTableSha256 ||
            checkpoint.ReferenceRowCount != table.Relationships.Count || !checkpoint.EnumerationComplete ||
            checkpoint.OwnerIdentities is null || checkpoint.Issues is null ||
            checkpoint.OwnerIdentities.Count > LocalizationReferenceReader.MaximumReferenceCount ||
            checkpoint.ModelMemberCount < 0 || checkpoint.UnsupportedModelCount < 0 ||
            checkpoint.UnsupportedModelCount > checkpoint.ModelMemberCount ||
            checkpoint.Issues.Count > MaximumCoverageDiagnostics || checkpoint.OmittedIssueCount < 0 ||
            checkpoint.Issues.Any(issue => issue.Code.Length > 128))
            return CoverageValidation.Invalid("CharacterCoverageSourceOrTableMismatch");

        var links = new List<CharacterCatalogLink>();
        var temporaryIssues = new List<CharacterCatalogIssue>();
        links.AddRange(MakeCatalogLinks(expectedIdentity, table, temporaryIssues));
        var currentOwners = GetUniqueCoverageOwners(expectedIdentity, links);
        if (!currentOwners.SequenceEqual(checkpoint.OwnerIdentities))
            return CoverageValidation.Invalid("CharacterCoverageOwnerSetMismatch");
        foreach (var issue in checkpoint.Issues)
            AddRestoreIssue(restoredIssues, new(issue.Kind, issue.Code, expectedIdentity,
                issue.OwnerIdentity, issue.ModelIdentity, issue.RelationshipIndex));

        var unsupportedIds = new HashSet<ResourceIdentity>();
        var modelMemberCount = 0;
        var allOwnerMetadataComplete = true;
        foreach (var owner in checkpoint.OwnerIdentities)
        {
            token.ThrowIfCancellationRequested();
            var reservation = memoryBudget.Reserve(MaximumPersistedEntryBytes, "coverage owner manifest validation");
            try
            {
                CoverageOwnerManifest? manifest;
                try
                {
                    manifest = features!.LoadBounded<CoverageOwnerManifest>(
                        CoverageOwnerManifestKey(expectedIdentity, owner), CoverageOwnerManifestCacheVersion,
                        checked((int)MaximumPersistedEntryBytes), token);
                }
                catch (InvalidDataException) { return CoverageValidation.Invalid("CharacterCoverageOwnerManifestInvalid"); }
                if (manifest is null || manifest.Revision != Revision || manifest.CatalogIdentity != expectedIdentity ||
                    manifest.OwnerIdentity != owner || manifest.ReferenceTableSha256 != table.ReferenceTableSha256 ||
                    !manifest.EnumerationComplete || manifest.OwnerExtents is null ||
                    manifest.ModelIdentities is null || manifest.ModelEntryFeatureKeys is null ||
                    manifest.ModelEntryIdentities is null || manifest.UnsupportedMembers is null ||
                    !ValidateSourceExtents(manifest.OwnerExtents) ||
                    manifest.ModelIdentities.Count > MaximumCoverageModels ||
                    manifest.ModelIdentities.Distinct().Count() != manifest.ModelIdentities.Count ||
                    manifest.ModelEntryIdentities.Count > MaximumCoverageModels ||
                    manifest.UnsupportedMembers.Count > MaximumCoverageModels)
                    return CoverageValidation.Invalid("CharacterCoverageOwnerManifestMismatch");
                allOwnerMetadataComplete &= manifest.Complete;
                var entryIds = manifest.ModelEntryIdentities.ToHashSet();
                var manifestUnsupportedIds = manifest.UnsupportedMembers.Select(item => item.Identity).ToHashSet();
                var manifestModelIds = manifest.ModelIdentities.ToHashSet();
                if (!manifestModelIds.IsSupersetOf(entryIds) ||
                    !manifestModelIds.IsSupersetOf(manifestUnsupportedIds) ||
                    manifestModelIds.Except(entryIds).Any(identity => !manifestUnsupportedIds.Contains(identity)) ||
                    manifest.ModelEntryFeatureKeys.Count != manifest.ModelEntryIdentities.Count ||
                    !manifest.ModelEntryFeatureKeys.SequenceEqual(manifest.ModelEntryIdentities.Select(ModelEntryFeatureKey)))
                    return CoverageValidation.Invalid("CharacterCoverageMembershipPartitionInvalid");

                modelMemberCount = checked(modelMemberCount + manifest.ModelIdentities.Count);
                var ownerUnsupported = manifestUnsupportedIds.Overlaps(entryIds);
                foreach (var member in manifest.UnsupportedMembers)
                {
                    if (member.Identity is null || member.DiagnosticCodes is null || member.DiagnosticCodes.Count == 0)
                        return CoverageValidation.Invalid("CharacterCoverageUnsupportedMemberUnexplained");
                    unsupportedIds.Add(member.Identity);
                }

                foreach (var modelIdentity in manifest.ModelEntryIdentities)
                {
                    token.ThrowIfCancellationRequested();
                    using var entryReservation = memoryBudget.Reserve(MaximumPersistedEntryBytes,
                        "coverage model entry validation");
                    CharacterCatalogModelEntry? entry;
                    try
                    {
                        entry = features.LoadBounded<CharacterCatalogModelEntry>(ModelEntryFeatureKey(modelIdentity),
                            modelEntryVersion, checked((int)MaximumPersistedEntryBytes), token);
                    }
                    catch (InvalidDataException) { return CoverageValidation.Invalid("CharacterCoverageModelEntryInvalid"); }
                    if (entry is null || entry.Descriptor.Identity != modelIdentity ||
                        entry.SourceSummary.Identity != modelIdentity || entry.SourceSummary.Revision != Revision ||
                        !ValidateSourceExtents(entry.SourceSummary.SourceExtents))
                        return CoverageValidation.Invalid("CharacterCoverageModelEntryMismatch");
                    var bindingAndLinkPresent = entry.Bindings.Any(binding => binding.CatalogIdentity == expectedIdentity) &&
                        entry.CatalogLinks.Any(link => link.CatalogIdentity == expectedIdentity &&
                            link.ReferenceTableSha256 == table.ReferenceTableSha256 &&
                            link.Relationship.Resolution.Candidates.Contains(owner));
                    if (!bindingAndLinkPresent && !manifestUnsupportedIds.Contains(modelIdentity))
                        return CoverageValidation.Invalid("CharacterCoverageModelEntryMismatch");
                    if (!entry.SourceSummary.Complete ||
                        entry.SourceSummary.StatisticsAvailability != CharacterModelStatisticsAvailability.Available ||
                        entry.Bindings.Any(binding => binding.Diagnostic is not null ||
                        binding.Lods.Any(lod => lod.Diagnostic is not null)))
                    {
                        unsupportedIds.Add(modelIdentity);
                        ownerUnsupported = true;
                    }
                }
                if (manifest.Complete && ownerUnsupported)
                    return CoverageValidation.Invalid("CharacterCoverageCompletenessContradiction");
                reservation.Dispose();
                reservation = null!;
            }
            finally { reservation?.Dispose(); }
        }

        foreach (var issue in checkpoint.Issues)
        {
            if (issue.ModelIdentity is { } modelIdentity &&
                issue.Kind is CharacterCatalogIssueKind.ModelBinding or CharacterCatalogIssueKind.MeshMetadata)
                unsupportedIds.Add(modelIdentity);
            AddRestoreIssue(restoredIssues, new(issue.Kind, issue.Code, expectedIdentity,
                issue.OwnerIdentity, issue.ModelIdentity, issue.RelationshipIndex));
        }
        if (checkpoint.OmittedIssueCount > 0)
            AddRestoreIssue(restoredIssues, new(CharacterCatalogIssueKind.AssetIndex,
                $"CharacterCoverageDiagnosticsOmitted:{checkpoint.OmittedIssueCount}", expectedIdentity));
        var unsupported = unsupportedIds.Count;
        if (checkpoint.ModelMemberCount != modelMemberCount || checkpoint.UnsupportedModelCount != unsupported ||
            checkpoint.AllPersistedModelMetadataSupported != (allOwnerMetadataComplete && unsupported == 0 &&
                checkpoint.Issues.Count == 0 && checkpoint.OmittedIssueCount == 0))
            return CoverageValidation.Invalid("CharacterCoverageUnsupportedCountMismatch");
        return new(true, null, unsupported, checkpoint.AllPersistedModelMetadataSupported);
    }

    private CatalogCoverageCheckpoint? LoadCoverageCheckpoint(int index, CancellationToken token)
    {
        try
        {
            return features!.LoadBounded<CatalogCoverageCheckpoint>(CoverageCheckpointKey(index),
                CoverageCheckpointCacheVersion, checked((int)MaximumPersistedEntryBytes), token);
        }
        catch (InvalidDataException) { return null; }
    }

    private IReadOnlyList<ResourceIdentity> GetUniqueCoverageOwners(CatalogContext context) =>
        GetUniqueCoverageOwners(context.Identity, context.Links);

    private IReadOnlyList<ResourceIdentity> GetUniqueCoverageOwners(ResourceIdentity catalog,
        IReadOnlyList<CharacterCatalogLink> links)
    {
        return links.Where(link => link.CatalogIdentity == catalog && link.Diagnostic is null &&
                link.Relationship.Resolution.Status == ReferenceStatus.Unique)
            .SelectMany(link => link.Relationship.Resolution.Candidates)
            .Distinct().OrderBy(identity => identity.Ordinal).ToArray();
    }

    private static void ValidateBatchCursor(CatalogReferenceBatch batch,
        int start, int stop, int total)
    {
        if (batch.StartCatalogIndex != start || batch.NextCatalogIndex != stop ||
            batch.Rows.Count != stop - start || batch.NextCatalogIndex > total ||
            batch.IsComplete != (batch.NextCatalogIndex == total))
            throw new ArchiveException("CharacterCoverageCursorInvalid", "The provider batch returned an unexpected coverage cursor.");
    }

    private bool IsVerifiedCoverageTable(ResourceIdentity identity,
        LocalizationReferenceReadResult result) => result.Source == identity && result.Revision == Revision &&
        result.Status == LocalizationReferenceReadStatus.Verified &&
        result.ReferenceTableSha256 is { Length: 64 } hash && IsSha256(hash) &&
        result.Relationships.Count <= LocalizationReferenceReader.MaximumReferenceCount;

    private string CoverageCheckpointKey(int index) =>
        $"{CoverageCheckpointCollection}/catalog/{index:D10}";

    private string CoverageOwnerManifestKey(ResourceIdentity catalog, ResourceIdentity owner) =>
        $"{CoverageCheckpointCollection}/owner/{catalog.Key}/{owner.Key}";

    private static void AddRestoreIssue(List<CharacterCatalogIssue> issues, CharacterCatalogIssue issue)
    {
        if (issues.Count < MaximumCoverageDiagnostics && !issues.Contains(issue)) issues.Add(issue);
    }

    private static string LimitText(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static bool IsModelMetadataIssue(CharacterCatalogIssue issue) =>
        issue.Kind is CharacterCatalogIssueKind.ModelBinding or CharacterCatalogIssueKind.MeshMetadata;

    private sealed record CatalogCoverageCheckpoint(SourceRevision Revision, int CatalogIndex,
        ResourceIdentity CatalogIdentity, string ReferenceTableSha256, int ReferenceRowCount,
        IReadOnlyList<ResourceIdentity> OwnerIdentities, int ModelMemberCount, int UnsupportedModelCount,
        bool EnumerationComplete, bool AllPersistedModelMetadataSupported, int OmittedIssueCount,
        IReadOnlyList<CoverageIssue> Issues);

    private sealed record CoverageOwnerManifest(SourceRevision Revision, ResourceIdentity CatalogIdentity,
        string ReferenceTableSha256, ResourceIdentity OwnerIdentity, IReadOnlyList<SourceExtent> OwnerExtents,
        IReadOnlyList<ResourceIdentity> ModelIdentities, IReadOnlyList<string> ModelEntryFeatureKeys,
        IReadOnlyList<ResourceIdentity> ModelEntryIdentities, IReadOnlyList<CoverageUnsupportedMember> UnsupportedMembers,
        bool Complete, bool EnumerationComplete);

    private sealed record CoverageUnsupportedMember(ResourceIdentity Identity,
        IReadOnlyList<string> DiagnosticCodes);
    private sealed record CoverageIssue(CharacterCatalogIssueKind Kind, string Code,
        ResourceIdentity? OwnerIdentity, ResourceIdentity? ModelIdentity, int? RelationshipIndex);
    private sealed record CoverageValidation(bool Valid, string? Diagnostic, int UnsupportedModels,
        bool AllPersistedModelMetadataSupported)
    {
        public static CoverageValidation Invalid(string diagnostic) => new(false, diagnostic, 0, false);
    }
}

internal static class CharacterCatalogCoverageIdentityExtensions
{
    public static int IndexOfIdentity(this IReadOnlyList<ResourceIdentity> identities, ResourceIdentity identity)
    {
        for (var index = 0; index < identities.Count; index++)
            if (identities[index] == identity) return index;
        return -1;
    }
}
