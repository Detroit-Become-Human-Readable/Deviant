using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Models;
using Deviant.Formats.Scenes;

namespace Deviant.Browser;

public delegate CharacterModelBindingResult CharacterModelBindingResolver(
    NativeContainerLease lease, AssetDescriptor selectedDescriptor, ResourceIdentity catalogIdentity,
    LocalizationReferenceReadResult catalogReferences, CharacterModelBindingOptions options,
    CancellationToken token);

/// <summary>Discovers native model members linked by verified archive catalogs.</summary>
public sealed partial class CharacterCatalogSession : IAsyncDisposable
{
    public const string ModelEntryCollection = "character-model-catalog-entries";
    public const string InventoryProfile = "native-model-inventory-v3-meshdata-shape-and-skeleton-names";
    public const string ModelEntryVersion = "character-model-entry-v8-meshdata41-clupskme2-blshapes3-binding2-lodstats1-classification3-rignames1-skinpalette1-skinstream1-retention2-direct-animation1-membership3";
    public const int MaximumMeshLabelParseBytes = 1024 * 1024;
    public const long MaximumPersistedEntryBytes = 4L * 1024 * 1024;
    public const int MaximumPersistedCatalogLinksPerModel = 2048;
    private static readonly HashSet<string> CachedInventoryMetadataKeys = new(StringComparer.Ordinal)
    {
        "model.kind", "model.classification-profile", "model.classification-evidence", "model.display-label",
        "model.source-label", "model.variant", "character.key", "character.name", "model.animation-profile",
        "model.animation-state", "model.animation-count"
    };
    private const string LegacyModelEntryVersionBase = "character-model-entry-v2-meshdata41-clupskme2-blshapes3-binding2";
    private const string PreviousModelEntryVersionBase = "character-model-entry-v3-meshdata41-clupskme2-blshapes3-binding2-lodstats1-descriptor-retention1";
    private const string PreviousClassifiedModelEntryVersionBase = "character-model-entry-v4-meshdata41-clupskme2-blshapes3-binding2-lodstats1-classification1-rignames1-skinpalette1-retention2";
    private const string PreviousDirectModelEntryVersionBase = "character-model-entry-v5-meshdata41-clupskme2-blshapes3-binding2-lodstats1-classification1-rignames1-skinpalette1-retention2-direct-animation1";
    private const string PreviousSkinModelEntryVersionBase = "character-model-entry-v6-meshdata41-clupskme2-blshapes3-binding2-lodstats1-classification2-rignames1-skinpalette1-skinstream1-retention2-direct-animation1";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ArchiveSnapshot snapshot;
    private readonly ICatalogReferenceProvider references;
    private readonly AssetMemoryBudget memoryBudget;
    private readonly AliasStore? aliases;
    private readonly FeatureStore? features;
    private readonly IDisposable? featureStoreWriterCacheLease;
    private readonly CharacterModelBindingReader bindingReader;
    private readonly CharacterModelBindingResolver bindingResolver;
    private readonly CharacterCatalogSessionOptions options;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly object operationPriorityGate = new();
    private CancellationTokenSource? activeModelDiscovery;
    private int foregroundModelSelections;
    private TaskCompletionSource foregroundSelectionsDrained = CompletedSignal();
    private const long MaximumOwnerInventoryCacheBytes = 16L * 1024 * 1024;
    private readonly object ownerInventoryCacheGate = new();
    private readonly Dictionary<(SourceRevision Revision, ResourceIdentity Owner, bool InventoryOnly), LinkedListNode<OwnerInventoryCacheEntry>> ownerInventoryCache = [];
    private readonly LinkedList<OwnerInventoryCacheEntry> ownerInventoryCacheLru = [];
    private long ownerInventoryCacheBytes;
    private readonly HashSet<int> coveredCatalogIndexes = [];
    private readonly string modelEntryCollection;
    private readonly string modelEntryVersion;
    private readonly string legacyModelEntryVersion;
    private readonly string previousModelEntryVersion;
    private readonly string previousClassifiedModelEntryVersion;
    private readonly string previousDirectModelEntryVersion;
    private readonly string previousSkinModelEntryVersion;
    private readonly string ownerMembershipCollection;
    private string OwnerMembershipVersion => $"character-owner-membership-v3/{modelEntryVersion}";
    private int disposed;

    public CharacterCatalogSession(ArchiveSnapshot snapshot, ICatalogReferenceProvider references,
        AssetMemoryBudget memoryBudget, AliasStore? aliases = null, FeatureStore? features = null,
        CharacterCatalogSessionOptions? options = null, CharacterModelBindingReader? bindingReader = null,
        CharacterModelBindingResolver? bindingResolver = null)
    {
        this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        this.references = references ?? throw new ArgumentNullException(nameof(references));
        this.memoryBudget = memoryBudget ?? throw new ArgumentNullException(nameof(memoryBudget));
        this.aliases = aliases;
        this.features = features;
        this.options = options ?? new();
        this.bindingReader = bindingReader ?? new CharacterModelBindingReader();
        this.bindingResolver = bindingResolver ?? this.bindingReader.Read;
        ValidateOptions(this.options);
        var expectedRevision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        if (references.Snapshot.IndexContentId != snapshot.IndexContentId ||
            references.Snapshot.PhysicalSourceStamp != snapshot.PhysicalSourceStamp || references.Revision != expectedRevision)
            throw new ArchiveException("CharacterCatalogSourceMismatch", "The reference provider belongs to another archive revision.");
        if (!ReferenceEquals(references.SharedMemoryBudget, memoryBudget))
            throw new ArchiveException("CharacterCatalogBudgetMismatch", "The session and reference provider must share one asset memory budget.");
        ValidateCatalogOrder(references.CatalogIdentities, snapshot);
        modelEntryCollection = string.Create(CultureInfo.InvariantCulture,
            $"{ModelEntryCollection}/{snapshot.IndexContentId}/{snapshot.PhysicalSourceStamp}");
        modelEntryVersion = string.Create(CultureInfo.InvariantCulture,
            $"{ModelEntryVersion}/labels-{this.options.MaximumLabelObservationsPerModel}-{this.options.MaximumLabelBytes}-{this.options.MaximumLabelCharacters}-{this.options.MaximumLabelTextBytesPerModel}/rig-names-{this.options.IncludeSkeletonNameEvidence}-{this.options.MaximumSkeletonNameEvidence}");
        previousSkinModelEntryVersion = string.Create(CultureInfo.InvariantCulture,
            $"{PreviousSkinModelEntryVersionBase}/labels-{this.options.MaximumLabelObservationsPerModel}-{this.options.MaximumLabelBytes}-{this.options.MaximumLabelCharacters}-{this.options.MaximumLabelTextBytesPerModel}/rig-names-{this.options.IncludeSkeletonNameEvidence}-{this.options.MaximumSkeletonNameEvidence}");
        previousModelEntryVersion = string.Create(CultureInfo.InvariantCulture,
            $"{PreviousModelEntryVersionBase}/labels-{this.options.MaximumLabelObservationsPerModel}-{this.options.MaximumLabelBytes}-{this.options.MaximumLabelCharacters}-{this.options.MaximumLabelTextBytesPerModel}");
        previousClassifiedModelEntryVersion = string.Create(CultureInfo.InvariantCulture,
            $"{PreviousClassifiedModelEntryVersionBase}/labels-{this.options.MaximumLabelObservationsPerModel}-{this.options.MaximumLabelBytes}-{this.options.MaximumLabelCharacters}-{this.options.MaximumLabelTextBytesPerModel}/rig-names-{this.options.IncludeSkeletonNameEvidence}-{this.options.MaximumSkeletonNameEvidence}");
        previousDirectModelEntryVersion = string.Create(CultureInfo.InvariantCulture,
            $"{PreviousDirectModelEntryVersionBase}/labels-{this.options.MaximumLabelObservationsPerModel}-{this.options.MaximumLabelBytes}-{this.options.MaximumLabelCharacters}-{this.options.MaximumLabelTextBytesPerModel}/rig-names-{this.options.IncludeSkeletonNameEvidence}-{this.options.MaximumSkeletonNameEvidence}");
        legacyModelEntryVersion = string.Create(CultureInfo.InvariantCulture,
            $"{LegacyModelEntryVersionBase}/labels-{this.options.MaximumLabelObservationsPerModel}-{this.options.MaximumLabelBytes}-{this.options.MaximumLabelCharacters}-{this.options.MaximumLabelTextBytesPerModel}");
        ownerMembershipCollection = string.Create(CultureInfo.InvariantCulture,
            $"character-model-owner-membership/{snapshot.IndexContentId}/{snapshot.PhysicalSourceStamp}");
        featureStoreWriterCacheLease = features?.TryAcquireWriterCacheLease(memoryBudget,
            bytes => memoryBudget.Reserve(bytes, "feature store writer cache"));
    }

    public ArchiveSnapshot Snapshot => snapshot;
    public SourceRevision Revision => references.Revision;
    public string NameProfile => NameIndexVersion;

    public Task WaitForForegroundSelectionsAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Task wait;
        lock (operationPriorityGate)
            wait = foregroundModelSelections == 0 ? Task.CompletedTask : foregroundSelectionsDrained.Task;
        return wait.WaitAsync(token);
    }

    public CharacterCatalogAssetPage QueryAssets(AssetQuery query, SourceRevision expectedRevision,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateExpectedRevision(expectedRevision);
        token.ThrowIfCancellationRequested();
        ValidateCurrentSourceStamps();
        var limit = Math.Clamp(query.Limit, 1, 1000);
        var bounded = query with { Family = "character-model", Limit = limit };
        RepairPendingAliasRefreshes(token);
        var reservation = memoryBudget.Reserve(checked(limit * 4096L), "character catalog asset page");
        using var retained = new ReservationGroup(); retained.Add(reservation);
        try
        {
            var rows = features?.QueryRetained(bounded, chars => retained.Add(memoryBudget.Reserve(
                checked(chars * 8L), "model catalog descriptor payload")), token) ?? [];
            var refreshed = rows.Select(ApplyCurrentAlias).ToArray();
            return new(refreshed, retained.Detach());
        }
        catch { reservation.Dispose(); throw; }
    }

    public CharacterCatalogAssetPage QueryCharacterGroups(AssetQuery query, out long total,
        CancellationToken token = default)
    {
        ValidateExpectedRevision(Revision);
        ValidateCurrentSourceStamps();
        token.ThrowIfCancellationRequested();
        var bounded = query with { Family = "character-model", Limit = Math.Clamp(query.Limit, 1, 1000) };
        var reservation = memoryBudget.Reserve(checked(bounded.Limit * 4096L), "character groups page");
        using var retained = new ReservationGroup(); retained.Add(reservation);
        try
        {
            total = 0;
            var rows = features?.QueryCharacterGroups(bounded, out total, token, chars => retained.Add(memoryBudget.Reserve(
                checked(chars * 8L), "character group descriptor payload"))) ?? [];
            return new(rows.Select(ApplyCurrentAlias).ToArray(), retained.Detach());
        }
        catch { reservation.Dispose(); throw; }
    }

    public CharacterCatalogModelSnapshot? GetModelEntry(ResourceIdentity identity,
        SourceRevision expectedRevision, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateExpectedRevision(expectedRevision);
        token.ThrowIfCancellationRequested();
        ValidateCurrentSourceStamps();
        if (identity.IndexContentId != snapshot.IndexContentId || identity.MemberPath is null) return null;
        if (features is null) return null;
        RepairPendingAliasRefreshes(token);
        var reservation = memoryBudget.Reserve(MaximumPersistedEntryBytes * 3L, "character model details scratch");
        using var retained = new ReservationGroup();
        retained.Add(reservation);
        try
        {
            var cached = LoadCachedModelEntryForLookup(identity, token, out _) ?? LoadNameEntry(identity, token);
            if (cached is null || cached.SourceSummary.Identity != identity ||
                cached.SourceSummary.Revision != expectedRevision ||
                !ValidateSourceExtents(cached.SourceSummary.SourceExtents))
            {
                reservation.Dispose();
                return null;
            }
            var accumulator = new ModelAccumulator(cached.SourceSummary);
            accumulator.Add(cached.Bindings, cached.CatalogLinks);
            if (cached.Descriptor.Metadata.GetValueOrDefault("model.inventory-only") == "true" &&
                cached.SourceSummary.Lods.Count == 0 && cached.Bindings.Count == 0)
                accumulator.CachedInventoryDescriptor = cached.Descriptor;
            var entry = BuildEntry(accumulator) with { Coverage = cached.Coverage };
            if (cached.Descriptor.Metadata.GetValueOrDefault("model.name-profile") == NameIndexVersion)
                entry = entry with { Descriptor = ApplyCurrentAlias(cached.Descriptor) };
            ReduceLoadedReservation(reservation,
                checked(EstimateCachedEntryRetention(cached) + EstimateEntryRetention(accumulator)), retained);
            return new(entry, retained.Detach());
        }
        catch { reservation.Dispose(); throw; }
    }

    private CharacterCatalogModelEntry? LoadCachedModelEntry(ResourceIdentity identity,
        IReadOnlyList<CharacterModelLodChoice> expectedLods, ReservationGroup retained,
        CancellationToken token)
    {
        if (features is null) return null;
        AssetMemoryBudget.Reservation? reservation = memoryBudget.Reserve(
            MaximumPersistedEntryBytes * 3L, "cached character model entry scratch");
        try
        {
            CharacterCatalogModelEntry? cached;
            try
            {
                cached = features.LoadBounded<CharacterCatalogModelEntry>(ModelEntryFeatureKey(identity),
                    modelEntryVersion, checked((int)MaximumPersistedEntryBytes), token);
            }
            catch (InvalidDataException) { return null; }
            if (cached is null || cached.SourceSummary.Identity != identity ||
                cached.SourceSummary.Revision != Revision ||
                !LodChoicesMatch(cached.SourceSummary.Lods, expectedLods) ||
                !ValidateSourceExtents(cached.SourceSummary.SourceExtents)) return null;
            ReduceLoadedReservation(reservation, EstimateCachedEntryRetention(cached), retained);
            retained.Add(reservation);
            reservation = null;
            return cached;
        }
        finally { reservation?.Dispose(); }
    }

    private CharacterCatalogModelEntry? LoadCachedModelEntryForLookup(ResourceIdentity identity,
        CancellationToken token, out bool legacyProfile)
    {
        legacyProfile = false;
        if (features is null) return null;
        CharacterCatalogModelEntry? cached = null;
        try
        {
            cached = features.LoadBounded<CharacterCatalogModelEntry>(ModelEntryFeatureKey(identity),
                modelEntryVersion, checked((int)MaximumPersistedEntryBytes), token);
        }
        catch (InvalidDataException) { }
        if (cached is not null) return cached;

        try
        { cached = features.LoadBounded<CharacterCatalogModelEntry>(ModelEntryFeatureKey(identity), previousSkinModelEntryVersion, checked((int)MaximumPersistedEntryBytes), token); }
        catch (InvalidDataException) { }
        if (cached is not null) { legacyProfile = true; return cached; }

        try
        { cached = features.LoadBounded<CharacterCatalogModelEntry>(ModelEntryFeatureKey(identity), previousDirectModelEntryVersion, checked((int)MaximumPersistedEntryBytes), token); }
        catch (InvalidDataException) { }
        if (cached is not null) { legacyProfile = true; return cached; }

        try
        {
            cached = features.LoadBounded<CharacterCatalogModelEntry>(ModelEntryFeatureKey(identity),
                previousClassifiedModelEntryVersion, checked((int)MaximumPersistedEntryBytes), token);
        }
        catch (InvalidDataException) { }
        if (cached is not null)
        {
            legacyProfile = true;
            return cached;
        }

        try
        {
            cached = features.LoadBounded<CharacterCatalogModelEntry>(ModelEntryFeatureKey(identity),
                previousModelEntryVersion, checked((int)MaximumPersistedEntryBytes), token);
        }
        catch (InvalidDataException) { }
        if (cached is not null)
        {
            legacyProfile = true;
            return cached;
        }

        try
        {
            cached = features.LoadBounded<CharacterCatalogModelEntry>(ModelEntryFeatureKey(identity),
                legacyModelEntryVersion, checked((int)MaximumPersistedEntryBytes), token);
        }
        catch (InvalidDataException) { return null; }
        if (cached is null) return null;
        legacyProfile = true;
        return MarkLegacyStatisticsUnavailable(cached);
    }

    private static CharacterCatalogModelEntry MarkLegacyStatisticsUnavailable(CharacterCatalogModelEntry entry)
    {
        var summary = entry.SourceSummary with
        {
            Lods = Array.AsReadOnly(entry.SourceSummary.Lods
                .Select(lod => lod with { GroupStatistics = null }).ToArray()),
            StatisticsAvailability = CharacterModelStatisticsAvailability.UnavailableInLegacyCache
        };
        var bindings = Array.AsReadOnly(entry.Bindings.Select(binding => binding with
        {
            Lods = Array.AsReadOnly(binding.Lods.Select(lod => lod with { GroupStatistics = null }).ToArray())
        }).ToArray());
        return entry with { SourceSummary = summary, Bindings = bindings };
    }

    private bool TryRestoreOwnerMembership(ResourceIdentity ownerIdentity,
        IReadOnlyList<CatalogContext> contexts, Dictionary<ResourceIdentity, ModelAccumulator> accumulators,
        ReservationGroup retained, CancellationToken token, out int restoredMemberCount,
        out int restoredUnsupportedCount, List<CharacterCatalogIssue> issues,
        out IReadOnlyList<SourceExtent> restoredOwnerExtents, out IReadOnlyList<ResourceIdentity> restoredModelIdentities)
    {
        restoredMemberCount = 0;
        restoredUnsupportedCount = 0;
        restoredOwnerExtents = [];
        restoredModelIdentities = [];
        if (features is null || contexts.Count == 0) return false;
        using var staged = new ReservationGroup();
        ResourceIdentity[]? modelIdentities = null;
        IReadOnlyList<SourceExtent>? ownerExtents = null;
        foreach (var context in contexts)
        {
            token.ThrowIfCancellationRequested();
            OwnerMembershipManifest? manifest;
            using var manifestScratch = memoryBudget.Reserve(MaximumPersistedEntryBytes * 3L,
                "warm model membership scratch");
            try
            {
                manifest = features.LoadBounded<OwnerMembershipManifest>(
                    OwnerMembershipKey(context.Identity, ownerIdentity), OwnerMembershipVersion,
                    checked((int)MaximumPersistedEntryBytes), token);
            }
            catch (InvalidDataException) { return false; }
            if (manifest is null || manifest.Revision != Revision || manifest.CatalogIdentity != context.Identity ||
                manifest.OwnerIdentity != ownerIdentity ||
                manifest.ModelIdentities is null || manifest.ModelIdentities.Count > MaximumPersistedEntryBytes / 128 ||
                !string.Equals(manifest.ReferenceTableSha256, context.Result.ReferenceTableSha256, StringComparison.Ordinal) ||
                !ValidateSourceExtents(manifest.OwnerExtents)) return false;
            if (modelIdentities is null) modelIdentities = manifest.ModelIdentities.ToArray();
            else if (!modelIdentities.SequenceEqual(manifest.ModelIdentities)) return false;
            if (ownerExtents is null) ownerExtents = manifest.OwnerExtents.ToArray();
            else if (!ownerExtents.SequenceEqual(manifest.OwnerExtents)) return false;
        }

        if (modelIdentities is null || ownerExtents is null) return false;
        staged.Add(memoryBudget.Reserve(checked(4096L + modelIdentities.Sum(identity =>
            256L + identity.Key.Length * 2L) + ownerExtents.Count * 512L), "warm model membership"));
        var restored = new List<CharacterCatalogModelEntry>(modelIdentities.Length);
        foreach (var identity in modelIdentities)
        {
            token.ThrowIfCancellationRequested();
            var reservation = memoryBudget.Reserve(MaximumPersistedEntryBytes * 3L, "warm character model entry scratch");
            try
            {
                var entry = LoadCachedModelEntryForLookup(identity, token, out var legacyProfile);
                if (legacyProfile || entry is null || entry.Descriptor.Identity != identity || entry.SourceSummary.Identity != identity ||
                    entry.SourceSummary.Revision != Revision ||
                    !ValidateSourceExtents(entry.SourceSummary.SourceExtents)) return false;
                foreach (var context in contexts)
                {
                    if (!entry.Bindings.Any(binding => binding.CatalogIdentity == context.Identity) ||
                        !entry.CatalogLinks.Any(link => link.CatalogIdentity == context.Identity &&
                            string.Equals(link.ReferenceTableSha256, context.Result.ReferenceTableSha256, StringComparison.Ordinal) &&
                            link.Relationship.Resolution.Candidates.Contains(ownerIdentity))) return false;
                }
                ReduceLoadedReservation(reservation, EstimateCachedEntryRetention(entry), staged);
                staged.Add(reservation);
                reservation = null!;
                restored.Add(entry);
            }
            finally { reservation?.Dispose(); }
        }

        retained.Add(staged.Detach());

        foreach (var entry in restored)
        {
            if (!accumulators.TryGetValue(entry.Descriptor.Identity, out var accumulator))
                accumulators.Add(entry.Descriptor.Identity, accumulator = new(entry));
            else accumulator.Add(entry.Bindings, entry.CatalogLinks);
            if (!entry.SourceSummary.Complete || entry.Bindings.Any(binding => binding.Diagnostic is not null ||
                binding.Lods.Any(lod => lod.Diagnostic is not null)))
            {
                restoredUnsupportedCount++;
                issues.Add(new(CharacterCatalogIssueKind.MeshMetadata, "CharacterModelMetadataIncomplete",
                    OwnerIdentity: ownerIdentity, ModelIdentity: entry.Descriptor.Identity));
                foreach (var binding in entry.Bindings)
                {
                    if (binding.Diagnostic is { } bindingDiagnostic)
                        issues.Add(new(CharacterCatalogIssueKind.ModelBinding, bindingDiagnostic,
                            binding.CatalogIdentity, ownerIdentity, entry.Descriptor.Identity));
                    foreach (var lod in binding.Lods.Where(lod => lod.Diagnostic is not null))
                        issues.Add(new(CharacterCatalogIssueKind.MeshMetadata, lod.Diagnostic!,
                            binding.CatalogIdentity, ownerIdentity, entry.Descriptor.Identity));
                }
            }
        }
        restoredMemberCount = restored.Count;
        restoredOwnerExtents = ownerExtents;
        restoredModelIdentities = modelIdentities;
        return true;
    }

    private bool TryRestoreOwnerInventory(ResourceIdentity ownerIdentity, bool inventoryOnly,
        IReadOnlyList<CatalogContext> contexts, IReadOnlyList<CharacterCatalogLink> allLinks,
        Dictionary<ResourceIdentity, ModelAccumulator> accumulators, CancellationToken token,
        out IReadOnlyList<SourceExtent> ownerExtents, out IReadOnlyList<ResourceIdentity> modelIdentities,
        out int restoredUnsupported)
    {
        ownerExtents = [];
        modelIdentities = [];
        restoredUnsupported = 0;
        var key = (Revision, ownerIdentity, inventoryOnly);
        OwnerInventoryCacheEntry? cached;
        lock (ownerInventoryCacheGate)
        {
            if (!ownerInventoryCache.TryGetValue(key, out var node)) return false;
            ownerInventoryCacheLru.Remove(node);
            ownerInventoryCacheLru.AddFirst(node);
            cached = node.Value;
        }
        if (cached.Revision != Revision || cached.OwnerIdentity != ownerIdentity || cached.InventoryOnly != inventoryOnly ||
            !ValidateSourceExtents(cached.OwnerExtents) || cached.Models.Any(entry =>
                (inventoryOnly ? entry.Bindings.Count != 0 : entry.Bindings.Count == 0) ||
                entry.SourceSummary.Revision != Revision || entry.SourceSummary.Identity.Ordinal != ownerIdentity.Ordinal ||
                entry.SourceSummary.Identity.IndexContentId != Revision.IndexContentId ||
                !ValidateSourceExtents(entry.SourceSummary.SourceExtents)))
        {
            RemoveOwnerInventory(key);
            return false;
        }

        var links = allLinks.Where(link => link.Relationship.Resolution.Candidates.Contains(ownerIdentity))
            .DistinctBy(LinkKey).ToArray();
        var containerResolution = new ReferenceResolution(ReferenceStatus.Unique, [ownerIdentity]);
        foreach (var entry in cached.Models)
        {
            token.ThrowIfCancellationRequested();
            if (!accumulators.TryGetValue(entry.Descriptor.Identity, out var accumulator))
                accumulators.Add(entry.Descriptor.Identity, accumulator = new(entry));
            if (inventoryOnly) accumulator.Add([], links);
            else
            {
                var sourceBinding = entry.Bindings.FirstOrDefault(binding => binding.Diagnostic is null) ??
                    entry.Bindings.FirstOrDefault();
                if (sourceBinding is null) return false;
                var bindings = contexts.Select(context => sourceBinding with
                {
                    CatalogIdentity = context.Identity,
                    CatalogContainerResolution = containerResolution,
                    CatalogCompanionLinks = context.Links.Select(link => link.Relationship).ToArray()
                }).ToArray();
                accumulator.Add(bindings, links);
            }
        }
        ownerExtents = cached.OwnerExtents;
        modelIdentities = cached.Models.Select(entry => entry.Descriptor.Identity).ToArray();
        return true;
    }

    private void RememberOwnerInventory(ResourceIdentity ownerIdentity, bool inventoryOnly,
        IReadOnlyList<ResourceIdentity> modelIdentities, IReadOnlyList<SourceExtent> ownerExtents,
        Dictionary<ResourceIdentity, ModelAccumulator> accumulators)
    {
        if (!inventoryOnly) return;
        if (ownerExtents.Count == 0 || !ValidateSourceExtents(ownerExtents)) return;
        var models = new List<CharacterCatalogModelEntry>(modelIdentities.Count);
        foreach (var identity in modelIdentities.Distinct())
        {
            if (!accumulators.TryGetValue(identity, out var accumulator)) return;
            var entry = BuildEntry(accumulator);
            if (!entry.SourceSummary.Complete ||
                (inventoryOnly ? entry.Bindings.Count != 0 : entry.Bindings.Count == 0) ||
                entry.Bindings.Any(binding => binding.Diagnostic is not null ||
                    binding.Lods.Any(lod => lod.Diagnostic is not null))) return;
            models.Add(entry);
        }
        var retainedBytes = checked(2048L + ownerExtents.Count * 512L +
            models.Sum(EstimateCachedEntryRetention));
        if (retainedBytes > MaximumOwnerInventoryCacheBytes) return;
        AssetMemoryBudget.Reservation reservation;
        try { reservation = memoryBudget.Reserve(retainedBytes, "bounded character owner inventory cache"); }
        catch (ArchiveException error) when (error.Code == "AssetMemoryLimit") { return; }
        var cached = new OwnerInventoryCacheEntry(Revision, ownerIdentity, inventoryOnly, ownerExtents.ToArray(),
            models.ToArray(), retainedBytes, reservation);
        var key = (Revision, ownerIdentity, inventoryOnly);
        lock (ownerInventoryCacheGate)
        {
            RemoveOwnerInventoryCore(key);
            while (ownerInventoryCacheBytes + retainedBytes > MaximumOwnerInventoryCacheBytes &&
                ownerInventoryCacheLru.Last is { } last)
                RemoveOwnerInventoryCore((last.Value.Revision, last.Value.OwnerIdentity, last.Value.InventoryOnly));
            ownerInventoryCacheLru.AddFirst(cached);
            ownerInventoryCache.Add(key, ownerInventoryCacheLru.First!);
            ownerInventoryCacheBytes += retainedBytes;
        }
    }

    private void RemoveOwnerInventory((SourceRevision Revision, ResourceIdentity Owner, bool InventoryOnly) key)
    {
        lock (ownerInventoryCacheGate) RemoveOwnerInventoryCore(key);
    }

    private void RemoveOwnerInventoryCore((SourceRevision Revision, ResourceIdentity Owner, bool InventoryOnly) key)
    {
        if (!ownerInventoryCache.Remove(key, out var node)) return;
        ownerInventoryCacheLru.Remove(node);
        ownerInventoryCacheBytes -= node.Value.RetainedBytes;
        node.Value.Reservation.Dispose();
    }

    private void ClearOwnerInventoryCache()
    {
        lock (ownerInventoryCacheGate)
        {
            foreach (var entry in ownerInventoryCacheLru) entry.Reservation.Dispose();
            ownerInventoryCache.Clear();
            ownerInventoryCacheLru.Clear();
            ownerInventoryCacheBytes = 0;
        }
    }

    private bool ValidateSourceExtents(IReadOnlyList<SourceExtent> extents)
    {
        if (extents.Count == 0 || extents.Count > SourceExtentVerifier.MaximumExtentCount) return false;
        var volumes = snapshot.Volumes.ToDictionary(volume => volume.RelativeName, StringComparer.OrdinalIgnoreCase);
        foreach (var extent in extents)
        {
            if (extent.Offset < 0 || extent.Length < 0 || extent.Offset > long.MaxValue - extent.Length ||
                extent.PayloadSha256 is not { Length: 64 } hash || !IsSha256(hash) ||
                !volumes.TryGetValue(extent.RelativeSource, out var volume) || !volume.Stamp.Exists ||
                extent.Offset + extent.Length > volume.Stamp.Length ||
                !SourceIdentity.SameStamp(volume.Stamp, new FileRevisionProvider().Capture(volume.FilePath))) return false;
        }
        return true;
    }

    private void ValidateCurrentSourceStamps()
    {
        var provider = new FileRevisionProvider();
        if (!SourceIdentity.SameStamp(snapshot.IndexStamp, provider.Capture(snapshot.IndexPath)))
            throw new ArchiveException("CharacterCatalogSourceChanged", "The archive index changed; reopen the archive.");
        foreach (var volume in snapshot.Volumes)
            if (!SourceIdentity.SameStamp(volume.Stamp, provider.Capture(volume.FilePath)))
                throw new ArchiveException("CharacterCatalogSourceChanged", "An archive volume changed; reopen the archive.");
    }

    public Task<CharacterCatalogDiscoveryResult> DiscoverModelsForCatalogAsync(
        ResourceIdentity catalogIdentity, IProgress<CharacterCatalogProgress>? progress = null,
        CancellationToken token = default) => DiscoverCatalogCoreAsync(catalogIdentity, progress, token, inventoryOnly: false);

    private async Task<CharacterCatalogDiscoveryResult> DiscoverCatalogCoreAsync(
        ResourceIdentity catalogIdentity, IProgress<CharacterCatalogProgress>? progress,
        CancellationToken token, bool inventoryOnly)
    {
        ValidateCatalogRequest(catalogIdentity, out var catalogIndex);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        var registered = false;
        try
        {
            RegisterModelDiscovery(linked);
            registered = true;
            EnsureAvailable();
            await references.ValidateSnapshotAsync(linked.Token).ConfigureAwait(false);
            using var readLease = await references.ReadAsync(catalogIdentity, linked.Token).ConfigureAwait(false);
            var result = readLease.Result;
            var row = new CatalogReadRow(catalogIdentity, result, null);
            var next = checked(catalogIndex + 1);
            return await DiscoverRowsAsync(catalogIndex, next, [row], result.Relationships.Count,
                requestedCatalogsComplete: true, progress: progress, token: linked.Token, inventoryOnly: inventoryOnly)
                .ConfigureAwait(false);
        }
        finally
        {
            if (registered) UnregisterModelDiscovery(linked);
            operationGate.Release();
        }
    }

    public Task<CharacterCatalogDiscoveryResult> DiscoverNextBatchAsync(int startCatalogIndex,
        int maximumCatalogs, IProgress<CharacterCatalogProgress>? progress = null,
        CancellationToken token = default) =>
        DiscoverNextBatchCoreAsync(startCatalogIndex, maximumCatalogs, progress, token, inventoryOnly: false);

    private async Task<CharacterCatalogDiscoveryResult> DiscoverNextBatchCoreAsync(int startCatalogIndex,
        int maximumCatalogs, IProgress<CharacterCatalogProgress>? progress, CancellationToken token,
        bool inventoryOnly)
    {
        if (maximumCatalogs < 1) throw new ArgumentOutOfRangeException(nameof(maximumCatalogs));
        if (startCatalogIndex < 0 || startCatalogIndex > references.CatalogIdentities.Count)
            throw new ArgumentOutOfRangeException(nameof(startCatalogIndex));
        var effectiveBatchSize = Math.Min(maximumCatalogs,
            Math.Min(options.MaximumCatalogsPerBatch, references.MaximumBatchCatalogs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        var registered = false;
        try
        {
            RegisterModelDiscovery(linked);
            registered = true;
            EnsureAvailable();
            await references.ValidateSnapshotAsync(linked.Token).ConfigureAwait(false);
            using var batch = await references.ReadBatchAsync(startCatalogIndex, effectiveBatchSize, linked.Token)
                .ConfigureAwait(false);
            if (batch.StartCatalogIndex != startCatalogIndex || batch.NextCatalogIndex < batch.StartCatalogIndex ||
                batch.NextCatalogIndex > references.CatalogIdentities.Count ||
                batch.IsComplete != (batch.NextCatalogIndex == references.CatalogIdentities.Count))
                throw new ArchiveException("CharacterCatalogCursorInvalid", "The catalog provider returned an invalid resume cursor.");
            for (var index = 0; index < batch.Rows.Count; index++)
                if (startCatalogIndex + index >= references.CatalogIdentities.Count ||
                    batch.Rows[index].Identity != references.CatalogIdentities[startCatalogIndex + index])
                    throw new ArchiveException("CharacterCatalogCursorInvalid", "The provider batch is not in catalog ordinal order.");
            var rows = batch.Rows.Select(row =>
                new CatalogReadRow(row.Identity, row.Lease?.Result, row.Diagnostic)).ToArray();
            return await DiscoverRowsAsync(batch.StartCatalogIndex, batch.NextCatalogIndex, rows,
                batch.ReferenceRowsRead,
                requestedCatalogsComplete: rows.Length == batch.NextCatalogIndex - batch.StartCatalogIndex,
                progress: progress, token: linked.Token, inventoryOnly: inventoryOnly).ConfigureAwait(false);
        }
        finally
        {
            if (registered) UnregisterModelDiscovery(linked);
            operationGate.Release();
        }
    }

    /// <summary>Inventories model members from a bounded batch of verified catalogs.</summary>
    /// <remarks>
    /// This pass reads native member identities, MESH v29 prefixes, and stored context labels. It defers
    /// mesh-data, skeleton, skin, group-statistic, and animation parsing to selection. Owners shared by
    /// catalogs in the batch are opened once and projected against each verified catalog context.
    /// </remarks>
    public Task<CharacterCatalogDiscoveryResult> InventoryNextBatchAsync(int startCatalogIndex,
        int maximumCatalogs, IProgress<CharacterCatalogProgress>? progress = null,
        CancellationToken token = default)
    {
        if (maximumCatalogs < 1) throw new ArgumentOutOfRangeException(nameof(maximumCatalogs));
        if (startCatalogIndex < 0 || startCatalogIndex > references.CatalogIdentities.Count)
            throw new ArgumentOutOfRangeException(nameof(startCatalogIndex));
        if (maximumCatalogs == 1 && startCatalogIndex < references.CatalogIdentities.Count)
            return DiscoverCatalogCoreAsync(references.CatalogIdentities[startCatalogIndex], progress, token, inventoryOnly: true);
        return DiscoverNextBatchCoreAsync(startCatalogIndex, maximumCatalogs, progress, token, inventoryOnly: true);
    }

    private void RegisterModelDiscovery(CancellationTokenSource operation)
    {
        lock (operationPriorityGate)
        {
            if (foregroundModelSelections != 0)
            {
                operation.Cancel();
                operation.Token.ThrowIfCancellationRequested();
            }
            activeModelDiscovery = operation;
        }
    }

    private void UnregisterModelDiscovery(CancellationTokenSource operation)
    {
        lock (operationPriorityGate)
            if (ReferenceEquals(activeModelDiscovery, operation)) activeModelDiscovery = null;
    }

    private void BeginForegroundModelSelection()
    {
        lock (operationPriorityGate)
        {
            if (foregroundModelSelections == 0)
                foregroundSelectionsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            foregroundModelSelections++;
            activeModelDiscovery?.Cancel();
        }
    }

    private void EndForegroundModelSelection()
    {
        lock (operationPriorityGate)
        {
            if (foregroundModelSelections <= 0)
                throw new InvalidOperationException("The model selection priority state is unbalanced.");
            if (--foregroundModelSelections == 0) foregroundSelectionsDrained.TrySetResult();
        }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private async Task<CharacterCatalogDiscoveryResult> DiscoverRowsAsync(int startCatalogIndex,
        int nextCatalogIndex, IReadOnlyList<CatalogReadRow> rows, long referenceRowsRead,
        bool requestedCatalogsComplete,
        IProgress<CharacterCatalogProgress>? progress, CancellationToken token, bool inventoryOnly = false)
    {
        token.ThrowIfCancellationRequested();
        using var discoveryTiming = AssetLoadDiagnostics.Measure(inventoryOnly ? "ModelInventoryBatch" : "ModelDiscoveryBatch",
            $"{startCatalogIndex}:{nextCatalogIndex}");
        using var retained = new ReservationGroup();
        if (referenceRowsRead < 0 || referenceRowsRead > checked((long)rows.Count * LocalizationReferenceReader.MaximumReferenceCount))
            throw new ArchiveException("CharacterCatalogBatchLimit", "The provider returned an invalid reference-row count.");
        retained.Add(memoryBudget.Reserve(checked((long)rows.Count * 16 * 1024 + referenceRowsRead * 192),
            "character catalog relationship projection"));
        var totalCatalogs = references.CatalogIdentities.Count;
        var issues = new List<CharacterCatalogIssue>();
        var allLinks = new List<CharacterCatalogLink>();
        var contexts = new List<CatalogContext>();
        var verifiedCatalogs = 0;
        var unsupportedCatalogs = 0;

        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (row.Result is null)
            {
                unsupportedCatalogs++;
                issues.Add(new(CharacterCatalogIssueKind.CatalogRead,
                    row.Diagnostic ?? "CharacterCatalogReadUnavailable", row.Identity));
                continue;
            }

            var links = MakeCatalogLinks(row.Identity, row.Result, issues);
            allLinks.AddRange(links);
            if (row.Diagnostic is not null || !IsVerifiedRead(row.Identity, row.Result))
            {
                unsupportedCatalogs++;
                issues.Add(new(CharacterCatalogIssueKind.CatalogRead,
                    row.Diagnostic ?? "CharacterCatalogReferenceUnverified", row.Identity));
                continue;
            }

            try
            {
                await references.RevalidateAsync(row.Result, token).ConfigureAwait(false);
                contexts.Add(new(row.Identity, row.Result, links));
                verifiedCatalogs++;
            }
            catch (ArchiveException error) when (!IsSourceChanged(error))
            {
                unsupportedCatalogs++;
                issues.Add(new(CharacterCatalogIssueKind.CatalogRead, error.Code, row.Identity));
            }
        }

        var ownerContexts = BuildOwnerContexts(contexts, issues);
        var accumulators = new Dictionary<ResourceIdentity, ModelAccumulator>();
        var ownersOpened = 0;
        var ownerFailures = 0;
        var modelMembersSeen = 0;
        var modelsBound = 0;
        var unsupportedModels = 0;
        var membershipCandidates = new Dictionary<(ResourceIdentity Catalog, ResourceIdentity Owner), OwnerMembershipBuilder>();
        foreach (var ownerTarget in ownerContexts)
            foreach (var context in ownerTarget.Value)
                membershipCandidates[(context.Identity, ownerTarget.Key)] = new(context, ownerTarget.Key);
        var allCatalogsVisited = coveredCatalogIndexes.Count == totalCatalogs;
        Report(progress, startCatalogIndex, nextCatalogIndex, totalCatalogs, rows.Count, verifiedCatalogs,
            unsupportedCatalogs, referenceRowsRead, ownerContexts.Count, ownersOpened, ownerFailures,
            modelMembersSeen, modelsBound, unsupportedModels, allCatalogsVisited, requestedCatalogsComplete, issues);

        foreach (var ownerTarget in ownerContexts.OrderBy(pair => pair.Key.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (TryRestoreNameOwnerMembership(ownerTarget.Key, inventoryOnly, ownerTarget.Value, accumulators, retained, token,
                out var restoredMemberCount, out var restoredUnsupportedCount, issues, out var restoredOwnerExtents,
                out var restoredModelIdentities))
            {
                foreach (var context in ownerTarget.Value)
                {
                    var candidate = membershipCandidates[(context.Identity, ownerTarget.Key)];
                    candidate.OwnerExtents = restoredOwnerExtents;
                    foreach (var identity in restoredModelIdentities) candidate.ModelIdentities.Add(identity);
                    candidate.EnumerationComplete = true;
                }
                modelMembersSeen += restoredMemberCount;
                modelsBound += restoredMemberCount;
                unsupportedModels += restoredUnsupportedCount;
                Report(progress, startCatalogIndex, nextCatalogIndex, totalCatalogs, rows.Count, verifiedCatalogs,
                    unsupportedCatalogs, referenceRowsRead, ownerContexts.Count, ownersOpened, ownerFailures,
                    modelMembersSeen, modelsBound, unsupportedModels, allCatalogsVisited, requestedCatalogsComplete, issues);
                continue;
            }

            if (TryRestoreOwnerInventory(ownerTarget.Key, inventoryOnly, ownerTarget.Value, allLinks, accumulators, token,
                out var cachedOwnerExtents, out var cachedModelIdentities, out restoredUnsupportedCount))
            {
                foreach (var context in ownerTarget.Value)
                {
                    var candidate = membershipCandidates[(context.Identity, ownerTarget.Key)];
                    candidate.OwnerExtents = cachedOwnerExtents;
                    foreach (var identity in cachedModelIdentities) candidate.ModelIdentities.Add(identity);
                    candidate.EnumerationComplete = true;
                }
                modelMembersSeen += cachedModelIdentities.Count;
                modelsBound += cachedModelIdentities.Count;
                unsupportedModels += restoredUnsupportedCount;
                Report(progress, startCatalogIndex, nextCatalogIndex, totalCatalogs, rows.Count, verifiedCatalogs,
                    unsupportedCatalogs, referenceRowsRead, ownerContexts.Count, ownersOpened, ownerFailures,
                    modelMembersSeen, modelsBound, unsupportedModels, allCatalogsVisited, requestedCatalogsComplete, issues);
                continue;
            }

            NativeContainerLease lease;
            try
            {
                lease = await NativeContainerLease.OpenMetadataAsync(snapshot, ownerTarget.Key, memoryBudget, token)
                    .ConfigureAwait(false);
            }
            catch (ArchiveException error) when (!IsSourceChanged(error))
            {
                ownerFailures++;
                foreach (var context in ownerTarget.Value)
                    membershipCandidates[(context.Identity, ownerTarget.Key)].Complete = false;
                issues.Add(new(CharacterCatalogIssueKind.OwnerOpen, error.Code, OwnerIdentity: ownerTarget.Key));
                Report(progress, startCatalogIndex, nextCatalogIndex, totalCatalogs, rows.Count, verifiedCatalogs,
                    unsupportedCatalogs, referenceRowsRead, ownerContexts.Count, ownersOpened, ownerFailures,
                    modelMembersSeen, modelsBound, unsupportedModels, allCatalogsVisited, requestedCatalogsComplete, issues);
                continue;
            }

            using (lease)
            {
                ownersOpened++;
                using var inventoryReadContext = inventoryOnly
                    ? new InventoryOwnerReadContext(lease, memoryBudget) : null;
                var members = lease.Directory.Objects
                    .Where(frame => frame.NativeType is 2009 or 2013)
                    .OrderBy(frame => frame.DirectoryIndex).ToArray();
                foreach (var candidate in ownerTarget.Value)
                    membershipCandidates[(candidate.Identity, ownerTarget.Key)].OwnerExtents = lease.SourceExtents.ToArray();
                foreach (var frame in members)
                {
                    token.ThrowIfCancellationRequested();
                    modelMembersSeen++;
                    foreach (var candidate in ownerTarget.Value)
                        membershipCandidates[(candidate.Identity, ownerTarget.Key)].ModelIdentities.Add(frame.Identity);
                    var descriptor = CreateInputDescriptor(frame, lease);
                    if (inventoryOnly)
                    {
                        var priorNameEntry = LoadNameEntry(frame.Identity, token, retained);
                        var inventoryEntry = BuildInventoryEntry(lease, frame, priorNameEntry,
                            inventoryReadContext ?? throw new InvalidOperationException("Inventory context unavailable."),
                            ownerTarget.Value, allLinks, issues, token);
                        if (!accumulators.TryGetValue(frame.Identity, out var inventoryAccumulator))
                            accumulators.Add(frame.Identity, inventoryAccumulator = new(inventoryEntry.SourceSummary));
                        inventoryAccumulator.Add([], inventoryEntry.CatalogLinks);
                        modelsBound++;
                        if (!inventoryEntry.SourceSummary.Complete) unsupportedModels++;
                        Report(progress, startCatalogIndex, nextCatalogIndex, totalCatalogs, rows.Count, verifiedCatalogs,
                            unsupportedCatalogs, referenceRowsRead, ownerContexts.Count, ownersOpened, ownerFailures,
                            modelMembersSeen, modelsBound, unsupportedModels, allCatalogsVisited, requestedCatalogsComplete, issues);
                        continue;
                    }
                    var bindings = new List<CharacterModelBindingResult>(ownerTarget.Value.Count);
                    var bindingFailure = false;
                    foreach (var context in ownerTarget.Value)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var binding = bindingResolver(lease, descriptor, context.Identity,
                                context.Result, new(ResolveEntityNode: true,
                                    IncludeSkeletonNameEvidence: options.IncludeSkeletonNameEvidence,
                                    MaximumSkeletonNameEvidence: options.MaximumSkeletonNameEvidence,
                                    IncludeGroupStatistics: false, IncludeAnimationLinks: false), token);
                            bindings.Add(binding);
                        }
                        catch (ArchiveException error) when (!IsSourceChanged(error))
                        {
                            bindingFailure = true;
                            issues.Add(new(CharacterCatalogIssueKind.ModelBinding, error.Code,
                                context.Identity, lease.Identity, frame.Identity));
                        }
                    }

                    if (bindings.Count == 0)
                    {
                        unsupportedModels++;
                        foreach (var candidate in ownerTarget.Value)
                            membershipCandidates[(candidate.Identity, ownerTarget.Key)].Complete = false;
                        Report(progress, startCatalogIndex, nextCatalogIndex, totalCatalogs, rows.Count, verifiedCatalogs,
                            unsupportedCatalogs, referenceRowsRead, ownerContexts.Count, ownersOpened, ownerFailures,
                            modelMembersSeen, modelsBound, unsupportedModels, allCatalogsVisited, requestedCatalogsComplete, issues);
                        continue;
                    }

                    modelsBound++;
                    var successfulBinding = bindings.FirstOrDefault(binding => binding.Diagnostic is null) ?? bindings[0];
                    var bindingLods = successfulBinding.Lods.Select(ToLodChoice).ToArray();
                    var cachedEntry = LoadNameEntry(frame.Identity, token, retained);
                    var sourceSummary = GetSourceSummary(lease, frame, successfulBinding,
                        cachedEntry?.SourceSummary, issues, retained, token) with
                        { StatisticsAvailability = CharacterModelStatisticsAvailability.DeferredUntilSelection };
                    if (bindingFailure || !sourceSummary.Complete || bindings.Any(binding => binding.Diagnostic is not null ||
                        binding.Lods.Any(lod => lod.Diagnostic is not null))) unsupportedModels++;
                    var modelLinks = allLinks.Where(link => link.Relationship.Resolution.Candidates.Contains(lease.Identity))
                        .DistinctBy(LinkKey).ToArray();
                    retained.Add(memoryBudget.Reserve(EstimateModelWorkingSet(bindings, modelLinks.Length),
                        "character model catalog bindings"));
                    if (!accumulators.TryGetValue(frame.Identity, out var accumulator))
                    {
                        accumulator = cachedEntry is { } cached
                            ? new ModelAccumulator(cached with { SourceSummary = sourceSummary })
                            : new ModelAccumulator(sourceSummary);
                        accumulators.Add(frame.Identity, accumulator);
                    }
                    else if (accumulator.SourceSummary.Revision != sourceSummary.Revision ||
                        accumulator.SourceSummary.Identity != sourceSummary.Identity)
                    {
                        throw new ArchiveException("CharacterModelIdentityConflict", "One native identity resolved to conflicting source metadata.");
                    }
                    accumulator.Add(bindings, modelLinks);
                    Report(progress, startCatalogIndex, nextCatalogIndex, totalCatalogs, rows.Count, verifiedCatalogs,
                        unsupportedCatalogs, referenceRowsRead, ownerContexts.Count, ownersOpened, ownerFailures,
                        modelMembersSeen, modelsBound, unsupportedModels, allCatalogsVisited, requestedCatalogsComplete, issues);
                }
                lease.EnsureUnchanged();
                foreach (var context in ownerTarget.Value)
                    membershipCandidates[(context.Identity, ownerTarget.Key)].EnumerationComplete = true;
            }
            var ownerMembership = membershipCandidates[(ownerTarget.Value[0].Identity, ownerTarget.Key)];
            if (ownerTarget.Value.All(context => membershipCandidates[(context.Identity, ownerTarget.Key)].Complete &&
                membershipCandidates[(context.Identity, ownerTarget.Key)].EnumerationComplete))
                RememberOwnerInventory(ownerTarget.Key, inventoryOnly, ownerMembership.ModelIdentities, ownerMembership.OwnerExtents,
                    accumulators);
        }

        var entryList = new List<CharacterCatalogModelEntry>(accumulators.Count);
        foreach (var accumulator in accumulators.Values)
        {
            token.ThrowIfCancellationRequested();
            retained.Add(memoryBudget.Reserve(EstimateEntryRetention(accumulator),
                "character catalog model result"));
            entryList.Add(BuildEntry(accumulator));
        }
        var entries = entryList.OrderBy(entry => entry.Descriptor.Identity.Ordinal)
            .ThenBy(entry => entry.Descriptor.Identity.MemberPath, StringComparer.Ordinal).ToArray();

        var revalidated = new HashSet<ResourceIdentity>();
        foreach (var context in contexts)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await references.RevalidateAsync(context.Result, token).ConfigureAwait(false);
                revalidated.Add(context.Identity);
            }
            catch (ArchiveException error) when (!IsSourceChanged(error))
            {
                issues.Add(new(CharacterCatalogIssueKind.CatalogRead, error.Code, context.Identity));
            }
        }

        NameIndexWriteResult nameWrite;
        using (AssetLoadDiagnostics.Measure("ModelIndexPersistence", $"{startCatalogIndex}:{nextCatalogIndex}"))
            nameWrite = await PersistNameIndexAsync(contexts, ownerContexts, membershipCandidates,
                revalidated, entries, inventoryOnly, token).ConfigureAwait(false);
        var persistedModelIdentities = nameWrite.PersistedModels;
        var checkpointedPasses = nameWrite.CoveredCatalogs;
        var catalogIndexes = references.CatalogIdentities
            .Select((identity, index) => (Identity: identity, Index: index))
            .ToDictionary(item => item.Identity, item => item.Index);
        var stagedCoverageIndexes = new HashSet<int>();
        foreach (var context in contexts)
            if (checkpointedPasses.Contains(context.Identity) && catalogIndexes.TryGetValue(context.Identity, out var index))
                stagedCoverageIndexes.Add(index);
        var prospectiveCoverageIndexes = coveredCatalogIndexes.Concat(stagedCoverageIndexes)
            .Distinct().Order().ToArray();
        allCatalogsVisited = prospectiveCoverageIndexes.Length == totalCatalogs;
        var unscanned = Math.Max(0, totalCatalogs - prospectiveCoverageIndexes.Length);
        var coverage = new CharacterCatalogCoverageSnapshot(Revision,
            prospectiveCoverageIndexes.Select(index => references.CatalogIdentities[index]).ToArray(),
            totalCatalogs, unscanned, allCatalogsVisited);
        entries = entries.Select(entry => entry with { Coverage = coverage }).ToArray();

        if (!inventoryOnly && features is not null && persistedModelIdentities.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            await references.ValidateSnapshotAsync(token).ConfigureAwait(false);
            ValidateCurrentSourceStamps();
            var persistedEntries = entries.Where(entry => persistedModelIdentities.Contains(entry.Descriptor.Identity)).ToArray();
            using var featureWrite = memoryBudget.Reserve(
                persistedEntries.Sum(entry => (long)EstimatePersistedEntryBytes(entry)),
                "character catalog coverage snapshot write");
            foreach (var entry in persistedEntries)
            {
                token.ThrowIfCancellationRequested();
                features.SaveBounded(NameEntryFeatureKey(entry.Descriptor.Identity), NameIndexVersion,
                    entry, checked((int)MaximumPersistedEntryBytes), token);
            }
        }

        var issueList = issues.ToArray();
        var isComplete = requestedCatalogsComplete && allCatalogsVisited && unscanned == 0 &&
            unsupportedCatalogs == 0 && ownerFailures == 0 && unsupportedModels == 0 && issueList.Length == 0;
        await references.ValidateSnapshotAsync(token).ConfigureAwait(false);
        ValidateCurrentSourceStamps();
        Report(progress, startCatalogIndex, nextCatalogIndex, totalCatalogs, rows.Count, verifiedCatalogs,
            unsupportedCatalogs, referenceRowsRead, ownerContexts.Count, ownersOpened, ownerFailures,
            modelMembersSeen, modelsBound, unsupportedModels, allCatalogsVisited, requestedCatalogsComplete, issues,
            isComplete, unscanned);
        token.ThrowIfCancellationRequested();
        await references.ValidateSnapshotAsync(token).ConfigureAwait(false);
        ValidateCurrentSourceStamps();
        token.ThrowIfCancellationRequested();
        foreach (var index in stagedCoverageIndexes) coveredCatalogIndexes.Add(index);

        return new(Revision, startCatalogIndex, nextCatalogIndex, totalCatalogs,
            rows.Count, verifiedCatalogs, unsupportedCatalogs, referenceRowsRead,
            ownerContexts.Count, ownersOpened, allLinks.ToArray(), entries, issueList,
            unscanned, requestedCatalogsComplete, allCatalogsVisited, isComplete, retained.Detach())
        {
            CheckpointedCatalogs = Array.AsReadOnly(checkpointedPasses.OrderBy(identity => identity.Ordinal).ToArray())
        };
    }

    private CharacterCatalogModelEntry BuildInventoryEntry(NativeContainerLease lease,
        NativeObjectFrame frame, CharacterCatalogModelEntry? priorNameEntry,
        InventoryOwnerReadContext inventoryContext, IReadOnlyList<CatalogContext> contexts,
        IReadOnlyList<CharacterCatalogLink> allLinks, List<CharacterCatalogIssue> issues,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var extents = lease.SourceExtents.ToArray();
        var names = new List<NameEvidence>();
        if (priorNameEntry is not null)
            names.AddRange(priorNameEntry.SourceSummary.StoredNameEvidence.Where(name => name.Origin == NameOrigin.Stored)
                .Take(options.MaximumLabelObservationsPerModel));
        var lods = new List<CharacterModelLodChoice>();
        var labelObservations = new List<CharacterStoredLabelObservation>();
        var observedShapeNames = new HashSet<(ResourceIdentity Identity, int Group, int Entry, int String)>();
        var shapeNameGroups = new HashSet<(ResourceIdentity Identity, int Group)>();
        var omittedLabelObservations = 0;
        long labelTextBytes = 0;
        bool sourceSkeletonV15Validated = false;
        bool complete = false;
        SceneEntityRecord? entity = null;
        string? entityDiagnostic = null;
        NativeMeshPrefixDocument? prefix = null;
        string? prefixDiagnostic = null;
        var entityRead = new SceneEntityReader().TryRead(frame, out entity, out entityDiagnostic, token);
        var prefixRead = entityRead && entity is not null &&
            new NativeMeshPrefixReader().TryRead(frame, entity, out prefix, out prefixDiagnostic, token);
        if (prefixRead && prefix is not null)
        {
            if (frame.NativeType == 2013 && entity is not null)
                names.AddRange(inventoryContext.ReadSkeletonNameEvidence(frame, entity,
                    options.MaximumSkeletonNameEvidence, out sourceSkeletonV15Validated, token));
            foreach (var item in prefix.ContextEntries)
            {
                token.ThrowIfCancellationRequested();
                if (item.StoredNameBytes.Length == 0 || item.StoredNameBytes.Length > NativeMeshPrefixReader.MaximumNameBytes) continue;
                string text;
                try { text = StrictUtf8.GetString(item.StoredNameBytes); }
                catch (DecoderFallbackException) { continue; }
                if (string.IsNullOrWhiteSpace(text) || text.Length > options.MaximumLabelCharacters) continue;
                names.Add(new(text, NameOrigin.Stored,
                    $"MESH v29 context {item.Ordinal}@{item.NameExtent.Offset}+{item.NameExtent.Length}"));
                if (names.Count >= options.MaximumLabelObservationsPerModel) break;
            }

            for (var index = 0; index < prefix.Lods.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var lod = prefix.Lods[index];
                var resolution = inventoryContext.Resolver.Resolve(lod.MeshData);
                ResourceIdentity? meshIdentity = null;
                string? diagnostic = null;
                if (resolution is { Status: ReferenceStatus.Unique, Candidates.Count: 1 })
                {
                    meshIdentity = resolution.Candidates[0];
                    if (!inventoryContext.MeshDataFrames.TryGetValue(meshIdentity, out var meshFrame))
                    {
                        meshIdentity = null;
                        diagnostic = "CharacterMeshDataUnavailable";
                    }
                    else
                    {
                        var hints = inventoryContext.ReadInventoryHints(meshIdentity, meshFrame,
                            options.MaximumLabelBytes, options.MaximumLabelObservationsPerModel,
                            options.MaximumLabelTextBytesPerModel, token);
                        var storedName = hints?.Name;
                        if (storedName is { Text.Length: > 0 } && !string.IsNullOrWhiteSpace(storedName.Text))
                            names.Add(new(storedName.Text, NameOrigin.Stored,
                                $"MESHDATA v41 {meshIdentity.Key}@{storedName.Extent.Offset}+{storedName.Extent.Length}"));
                        if (hints is not null && prefix.GroupIndex <= int.MaxValue)
                        {
                            int sourceGroup = (int)prefix.GroupIndex;
                            if (shapeNameGroups.Add((meshIdentity, sourceGroup)))
                                omittedLabelObservations = checked(omittedLabelObservations +
                                    hints.OmittedShapeNameCountsByGroup.GetValueOrDefault(sourceGroup));
                            foreach (var shapeName in hints.ShapeNames)
                            {
                                token.ThrowIfCancellationRequested();
                                if (shapeName.GroupOrdinal != sourceGroup ||
                                    !observedShapeNames.Add((meshIdentity, shapeName.GroupOrdinal,
                                        shapeName.EntryOrdinal, shapeName.StringOrdinal))) continue;
                                var bytes = shapeName.RawBytes;
                                if (labelObservations.Count >= options.MaximumLabelObservationsPerModel ||
                                    checked(labelTextBytes + bytes.Length) > options.MaximumLabelTextBytesPerModel)
                                {
                                    omittedLabelObservations++;
                                    continue;
                                }

                                string? text = null;
                                var status = CharacterLabelUtf8Status.Valid;
                                if (bytes.Length > options.MaximumLabelBytes) status = CharacterLabelUtf8Status.TooLong;
                                else
                                {
                                    try
                                    {
                                        text = StrictUtf8.GetString(bytes.Span);
                                        if (text.Length > options.MaximumLabelCharacters)
                                        { text = null; status = CharacterLabelUtf8Status.TooLong; }
                                        else labelTextBytes = checked(labelTextBytes + bytes.Length);
                                    }
                                    catch (DecoderFallbackException) { status = CharacterLabelUtf8Status.Invalid; }
                                }
                                labelObservations.Add(new(meshIdentity, index, shapeName.GroupOrdinal,
                                    shapeName.EntryOrdinal, shapeName.StringOrdinal, text, status, bytes.Length,
                                    Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant(),
                                    shapeName.BytesExtent, shapeName.RawTrailingWord, shapeName.TrailingWordExtent));
                            }
                        }
                    }
                }
                else diagnostic = resolution.Status == ReferenceStatus.Ambiguous
                    ? "CharacterMeshDataAmbiguous" : "CharacterMeshDataMissing";
                lods.Add(new(index, lod.Threshold, BitConverter.SingleToUInt32Bits(lod.Threshold), lod.MeshData,
                    resolution, meshIdentity, prefix.GroupIndex, null, null, null, lod.Extent, null, null, diagnostic));
            }
            complete = true;
        }
        else
        {
            var diagnostic = entityDiagnostic ?? prefixDiagnostic ?? "CharacterModelPrefixUnavailable";
            foreach (var context in contexts)
                issues.Add(new(CharacterCatalogIssueKind.MeshMetadata, diagnostic, context.Identity, lease.Identity, frame.Identity));
        }

        var sourceSummary = new CharacterModelSourceSummary(frame.Identity, Revision, frame.NativeType, frame.NativeId,
            lods.ToArray(), names.Distinct().ToArray(), extents, labelObservations.ToArray(),
            omittedLabelObservations, complete)
        {
            StatisticsAvailability = CharacterModelStatisticsAvailability.DeferredUntilSelection,
            SourceSkeletonV15Validated = sourceSkeletonV15Validated,
            InventoryDisplayLabel = priorNameEntry?.Descriptor.Metadata.GetValueOrDefault("model.display-label") ??
                (priorNameEntry is null ? null : priorNameEntry.Descriptor.Label),
            CachedVerifiedMetadata = priorNameEntry is null ? null : priorNameEntry.Descriptor.Metadata
                .Where(pair => CachedInventoryMetadataKeys.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
        var catalogLinks = allLinks.Where(link => link.Relationship.Resolution.Candidates.Contains(lease.Identity))
            .DistinctBy(LinkKey).ToArray();
        var accumulator = new ModelAccumulator(sourceSummary);
        accumulator.Add([], catalogLinks);
        return BuildEntry(accumulator);
    }

    private CharacterModelSourceSummary GetSourceSummary(NativeContainerLease lease,
        NativeObjectFrame ownerFrame, CharacterModelBindingResult binding,
        CharacterModelSourceSummary? cachedSummary,
        List<CharacterCatalogIssue> issues, ReservationGroup retained, CancellationToken token)
    {
        var bindingLods = binding.Lods.Select(ToLodChoice).ToArray();
        if (cachedSummary is { Complete: true } && cachedSummary.Identity == ownerFrame.Identity &&
            cachedSummary.Revision == Revision && LodChoicesMatch(cachedSummary.Lods, bindingLods))
            return cachedSummary;

        var lods = bindingLods;
        var nameEvidence = lods.Where(lod => !string.IsNullOrWhiteSpace(lod.StoredMeshName) && lod.MeshDataIdentity is not null)
            .Select(lod => new NameEvidence(lod.StoredMeshName!, NameOrigin.Stored,
                $"MESHDATA v41 {lod.MeshDataIdentity!.Key}"))
            .Distinct().ToArray();
        var extents = binding.SourceExtents.Distinct().ToArray();
        retained.Add(memoryBudget.Reserve(EstimateSourceSummaryRetention(lods) +
            nameEvidence.Length * 384L + nameEvidence.Sum(name => name.Text.Length * 2L) + extents.Length * 128L,
            "character model source summary"));
        var labelObservations = new List<CharacterStoredLabelObservation>();
        var omitted = 0;
        var textBytes = 0L;
        var labelMetadataReserved = 0L;
        var labelMetadataUsed = 0L;
        var complete = binding.Diagnostic is null;
        var parsedMeshIdentities = new HashSet<ResourceIdentity>();
        var meshFrames = lease.Directory.Objects.Where(frame => frame.NativeType == 2130)
            .GroupBy(frame => frame.Identity).ToDictionary(group => group.Key, group => group.ToArray());

        foreach (var lod in lods)
        {
            token.ThrowIfCancellationRequested();
            if (lod.MeshDataIdentity is not { } meshIdentity ||
                lod.MeshDataResolution is not { Status: ReferenceStatus.Unique, Candidates.Count: 1 }) continue;
            if (!parsedMeshIdentities.Add(meshIdentity)) continue;
            if (!meshFrames.TryGetValue(meshIdentity, out var candidates) || candidates.Length != 1)
            {
                complete = false;
                issues.Add(new(CharacterCatalogIssueKind.MeshMetadata, "CharacterMeshMetadataUnavailable",
                    OwnerIdentity: lease.Identity, ModelIdentity: ownerFrame.Identity));
                continue;
            }

            var frame = candidates[0];
            if (frame.Identity.IndexContentId != lease.Identity.IndexContentId ||
                frame.Identity.Ordinal != lease.Identity.Ordinal || frame.Identity.MemberPath is null)
            {
                complete = false;
                issues.Add(new(CharacterCatalogIssueKind.MeshMetadata, "CharacterMeshSourceMismatch",
                    OwnerIdentity: lease.Identity, ModelIdentity: ownerFrame.Identity));
                continue;
            }

            using var reservation = lease.MemoryBudget.Reserve(
                checked((long)NativeMeshDataReader.MaximumMetadataBytes + frame.RawPayload.Length),
                "character model label metadata");
            if (!new NativeMeshDataReader().TryRead(frame, out var data, out var diagnostic, token) || data is null)
            {
                complete = false;
                issues.Add(new(CharacterCatalogIssueKind.MeshMetadata,
                    diagnostic ?? "CharacterMeshMetadataUnavailable", OwnerIdentity: lease.Identity,
                    ModelIdentity: ownerFrame.Identity));
                continue;
            }

            if (data.BlendShapeMetadata is not { } shapeMetadata) continue;
            for (var groupIndex = 0; groupIndex < shapeMetadata.Groups.Count; groupIndex++)
            {
                var group = shapeMetadata.Groups[groupIndex];
                for (var entryIndex = 0; entryIndex < group.Entries.Count; entryIndex++)
                {
                    var entry = group.Entries[entryIndex];
                    for (var stringIndex = 0; stringIndex < entry.StringRecords.Count; stringIndex++)
                    {
                        token.ThrowIfCancellationRequested();
                        var textRecord = entry.StringRecords[stringIndex];
                        var bytes = textRecord.RawBytes;
                        if (labelObservations.Count >= options.MaximumLabelObservationsPerModel ||
                            checked(textBytes + bytes.Length) > options.MaximumLabelTextBytesPerModel)
                        {
                            omitted++;
                            continue;
                        }

                        var labelEstimate = checked(256L + bytes.Length * 2L);
                        if (checked(labelMetadataUsed + labelEstimate) > labelMetadataReserved)
                        {
                            var additional = Math.Max(64 * 1024L, checked(labelMetadataUsed + labelEstimate - labelMetadataReserved));
                            retained.Add(memoryBudget.Reserve(additional, "character model label observations"));
                            labelMetadataReserved = checked(labelMetadataReserved + additional);
                        }
                        labelMetadataUsed = checked(labelMetadataUsed + labelEstimate);

                        string? text = null;
                        CharacterLabelUtf8Status utf8Status;
                        if (bytes.Length > options.MaximumLabelBytes) utf8Status = CharacterLabelUtf8Status.TooLong;
                        else
                        {
                            try
                            {
                                text = StrictUtf8.GetString(bytes.Span);
                                if (text.Length > options.MaximumLabelCharacters)
                                {
                                    text = null;
                                    utf8Status = CharacterLabelUtf8Status.TooLong;
                                }
                                else
                                {
                                    utf8Status = CharacterLabelUtf8Status.Valid;
                                    textBytes = checked(textBytes + bytes.Length);
                                }
                            }
                            catch (DecoderFallbackException) { utf8Status = CharacterLabelUtf8Status.Invalid; }
                        }

                        var hash = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
                        labelObservations.Add(new(meshIdentity, lod.LodOrdinal, groupIndex, entryIndex,
                            stringIndex, text, utf8Status, bytes.Length, hash, textRecord.BytesExtent,
                            textRecord.RawTrailingWord, textRecord.TrailingWordExtent));
                    }
                }
            }
        }

        lease.EnsureUnchanged();
        var summary = new CharacterModelSourceSummary(ownerFrame.Identity, Revision,
            ownerFrame.NativeType, ownerFrame.NativeId, lods, nameEvidence, extents,
            labelObservations.ToArray(), omitted, complete);
        return summary;
    }

    private CharacterCatalogModelEntry BuildEntry(ModelAccumulator accumulator)
    {
        var summary = accumulator.SourceSummary;
        if (accumulator.CachedInventoryDescriptor is { } cachedInventoryDescriptor &&
            summary.StatisticsAvailability == CharacterModelStatisticsAvailability.DeferredUntilSelection &&
            accumulator.Bindings.Count == 0)
        {
            var seedDescriptor = ApplyCurrentAlias(cachedInventoryDescriptor);
            seedDescriptor = seedDescriptor with { SearchTerms = ModelCatalogClassification.SearchTerms(seedDescriptor) };
            return new(seedDescriptor, summary, [], accumulator.Links.OrderBy(link => link.CatalogIdentity.Ordinal)
                .ThenBy(link => link.RelationshipIndex).ToArray());
        }
        var classification = ModelCatalogClassification.Project(summary, accumulator.Bindings);
        var storedNames = summary.StoredNameEvidence;
        var distinctStoredNames = storedNames.Select(name => name.Text).Distinct(StringComparer.Ordinal).ToArray();
        var alias = aliases?.Get(summary.Identity);
        var inventoryOnly = summary.StatisticsAvailability == CharacterModelStatisticsAvailability.DeferredUntilSelection &&
            accumulator.Bindings.Count == 0;
        var commonDotShapePrefix = CharacterCatalogLabelProjector.CommonDotPrefix(summary.StoredLabelObservations);
        var commonShapePrefix = inventoryOnly || distinctStoredNames.Length == 0 ? commonDotShapePrefix : null;
        var label = alias ?? (inventoryOnly
            ? commonDotShapePrefix ?? summary.InventoryDisplayLabel ??
                (distinctStoredNames.Length == 0 ? "Model" : classification.DisplayLabel)
            : summary.InventoryDisplayLabel ?? classification.DisplayLabel);
        var names = storedNames.ToList();
        foreach (var observation in summary.StoredLabelObservations)
        {
            if (observation.Utf8Status != CharacterLabelUtf8Status.Valid ||
                string.IsNullOrEmpty(observation.Text)) continue;
            var source = $"BLSHAPES v3 {observation.MeshDataIdentity.Key}@{observation.BytesExtent.Offset}+{observation.BytesExtent.Length}";
            names.Add(new(observation.Text, NameOrigin.Stored, source));
        }
        if (commonShapePrefix is not null)
            names.Add(new(commonShapePrefix, NameOrigin.Description, "Common prefix of stored BLSHAPES v3 labels"));
        foreach (var binding in accumulator.Bindings.Where(binding => binding.Diagnostic is null))
            if (binding.Rig is { } rig)
                foreach (var observation in rig.NameEvidence)
                {
                    var source = $"SKELETON v15 {binding.SelectedIdentity.Key} table {observation.TableOrdinal} key {observation.Key}@{observation.Extent.Offset}+{observation.Extent.Length}";
                    var evidence = new NameEvidence(observation.Text, NameOrigin.Stored, source);
                    if (!names.Contains(evidence)) names.Add(evidence);
                }
        if (alias is not null) names.Add(new(alias, NameOrigin.UserAlias, "AliasStore"));

        var selectedCount = summary.Lods.Count(lod => lod.Selection is not null);
        var completeness = summary.Lods.Count == 0 || selectedCount == 0
            ? AssetCompleteness.Unverified
            : selectedCount == summary.Lods.Count ? AssetCompleteness.Complete : AssetCompleteness.Partial;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NativeType"] = summary.OwnerNativeType.ToString(CultureInfo.InvariantCulture),
            ["NativeId"] = summary.OwnerNativeId.ToString(CultureInfo.InvariantCulture),
            ["LodCount"] = summary.Lods.Count.ToString(CultureInfo.InvariantCulture),
            ["CatalogLinkCount"] = accumulator.Links.Count.ToString(CultureInfo.InvariantCulture),
            ["StoredNameCount"] = storedNames.Count.ToString(CultureInfo.InvariantCulture)
        };
        metadata["model.kind"] = classification.Kind;
        metadata["model.name-profile"] = NameIndexVersion;
        metadata["model.classification-profile"] = ModelCatalogClassification.Profile;
        metadata["model.classification-evidence"] = classification.Evidence;
        metadata["model.display-label"] = inventoryOnly
            ? commonDotShapePrefix ?? summary.InventoryDisplayLabel ??
                (distinctStoredNames.Length == 0 ? "Model" : classification.DisplayLabel)
            : summary.InventoryDisplayLabel ?? classification.DisplayLabel;
        if (inventoryOnly)
        {
            metadata["model.inventory-only"] = "true";
            metadata["model.inventory-profile"] = InventoryProfile;
        }
        if (classification.CharacterKey is null)
        {
            if (inventoryOnly)
            {
                var candidate = ModelCatalogClassification.ResolveInventoryCandidate(
                    ModelCatalogClassification.SuggestInventoryCandidates(storedNames,
                        summary.StoredLabelObservations, summary.SourceSkeletonV15Validated));
                if (candidate is not null)
                {
                    metadata["character.candidate-key"] = candidate.Key;
                    metadata["character.candidate-name"] = candidate.Name;
                    if (!string.IsNullOrWhiteSpace(candidate.Variant))
                        metadata["character.candidate-variant"] = candidate.Variant;
                }
            }
            else
            {
                var candidates = storedNames.Select(name => ModelCatalogClassification.SuggestCharacterNameCandidate(name.Text))
                    .Where(candidate => candidate is not null).Select(candidate => candidate!).ToArray();
                var candidateKeys = candidates.Select(candidate => candidate.Key)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (candidateKeys.Length == 1)
                {
                    var candidate = candidates[0];
                    var variants = candidates.Select(item => item.Variant ?? "")
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    metadata["character.candidate-key"] = candidate.Key;
                    metadata["character.candidate-name"] = candidate.Name;
                    if (variants.Length == 1 && variants[0].Length > 0)
                        metadata["character.candidate-variant"] = variants[0];
                }
            }
        }
        if (inventoryOnly && summary.CachedVerifiedMetadata is { } priorMetadata)
            foreach (var pair in priorMetadata)
                if (CachedInventoryMetadataKeys.Contains(pair.Key)) metadata[pair.Key] = pair.Value;
        if (metadata.ContainsKey("character.key"))
        {
            metadata.Remove("character.candidate-key");
            metadata.Remove("character.candidate-name");
            metadata.Remove("character.candidate-variant");
        }
        if (classification.SourceLabel is { } sourceLabel) metadata["model.source-label"] = sourceLabel;
        if (classification.Variant is { } variant) metadata["model.variant"] = variant;
        if (classification.CharacterKey is { } characterKey) metadata["character.key"] = characterKey;
        if (classification.CharacterName is { } characterName) metadata["character.name"] = characterName;
        if (distinctStoredNames.Length > 1) metadata["StoredNameConflict"] = "true";
        if (summary.OmittedLabelObservationCount > 0)
            metadata["OmittedLabelObservationCount"] = summary.OmittedLabelObservationCount.ToString(CultureInfo.InvariantCulture);

        var descriptor = new AssetDescriptor(summary.Identity, summary.Revision,
            "character-model", label, null, AssetAvailability.Available, completeness,
            AssetCapabilities.None, names, summary.SourceExtents,
            Reason: null, SortKey: label)
        {
            LookupKey = summary.Identity.Key,
            Metadata = metadata
        };
        descriptor = ApplyModelAnimationMetadata(descriptor, accumulator.Bindings);
        descriptor = descriptor with { SearchTerms = ModelCatalogClassification.SearchTerms(descriptor) };
        var links = accumulator.Links.OrderBy(link => link.CatalogIdentity.Ordinal)
            .ThenBy(link => link.RelationshipIndex).ToArray();
        return new(descriptor, summary, accumulator.Bindings.ToArray(), links);
    }

    private static CharacterModelLodChoice ToLodChoice(CharacterMeshLodBinding lod) =>
        new(lod.LodOrdinal, lod.Threshold, BitConverter.SingleToUInt32Bits(lod.Threshold),
            lod.MeshDataReference, lod.MeshDataResolution, lod.MeshDataIdentity,
            lod.GroupIndex, lod.GroupCount, lod.StoredMeshName, lod.Selection, lod.LodExtent,
            lod.MeshDataMetadataExtent, lod.SelectedGroupBoundsExtent, lod.Diagnostic)
        { GroupStatistics = lod.GroupStatistics, HasSkinPalette = lod.HasSkinPalette };

    private static bool LodChoicesMatch(IReadOnlyList<CharacterModelLodChoice> left,
        IReadOnlyList<CharacterModelLodChoice> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            var a = left[index]; var b = right[index];
            if (a.LodOrdinal != b.LodOrdinal || a.Threshold != b.Threshold || a.RawThresholdBits != b.RawThresholdBits ||
                a.MeshDataReference != b.MeshDataReference || a.MeshDataResolution.Status != b.MeshDataResolution.Status ||
                a.MeshDataResolution.Reason != b.MeshDataResolution.Reason ||
                !a.MeshDataResolution.Candidates.SequenceEqual(b.MeshDataResolution.Candidates) ||
                a.MeshDataIdentity != b.MeshDataIdentity || a.GroupIndex != b.GroupIndex || a.GroupCount != b.GroupCount ||
                a.StoredMeshName != b.StoredMeshName || a.HasSkinPalette != b.HasSkinPalette || a.Selection != b.Selection || a.LodExtent != b.LodExtent ||
                a.MeshDataMetadataExtent != b.MeshDataMetadataExtent || a.SelectedGroupBoundsExtent != b.SelectedGroupBoundsExtent ||
                a.Diagnostic != b.Diagnostic || !GroupStatisticsMatch(a.GroupStatistics, b.GroupStatistics))
                return false;
        }
        return true;
    }

    private static bool GroupStatisticsMatch(NativeModelGroupStatistics? left, NativeModelGroupStatistics? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.SummedVertexWindowCount != right.SummedVertexWindowCount ||
            left.SummedIndexWindowCount != right.SummedIndexWindowCount || left.PartCount != right.PartCount ||
            !left.VertexBufferElementCounts.SequenceEqual(right.VertexBufferElementCounts) ||
            !left.IndexBufferElementCounts.SequenceEqual(right.IndexBufferElementCounts) ||
            left.RawSelectorHistogram.Count != right.RawSelectorHistogram.Count ||
            left.Bounds.Extent != right.Bounds.Extent || left.Bounds.Extra != right.Bounds.Extra ||
            !left.Bounds.Obb.SequenceEqual(right.Bounds.Obb)) return false;
        foreach (var pair in left.RawSelectorHistogram)
            if (!right.RawSelectorHistogram.TryGetValue(pair.Key, out var count) || count != pair.Value) return false;
        return true;
    }

    private static AssetDescriptor CreateInputDescriptor(NativeObjectFrame frame, NativeContainerLease lease)
    {
        var fallback = frame.NativeType == 2013 ? "Native rig" : "Native model";
        return new(frame.Identity, lease.Revision, "character-model", fallback, null,
            AssetAvailability.Available, AssetCompleteness.Unverified, AssetCapabilities.None,
            [], lease.SourceExtents);
    }

    private Dictionary<ResourceIdentity, List<CatalogContext>> BuildOwnerContexts(
        IReadOnlyList<CatalogContext> contexts, List<CharacterCatalogIssue> issues)
    {
        var output = new Dictionary<ResourceIdentity, List<CatalogContext>>();
        foreach (var context in contexts)
        {
            var targets = context.Links.Where(link => link.Diagnostic is null &&
                    link.Relationship.Resolution.Status == ReferenceStatus.Unique)
                .SelectMany(link => link.Relationship.Resolution.Candidates)
                .Distinct().ToArray();
            foreach (var target in targets)
            {
                if (!IsValidContainerIdentity(context.Result, target, out var diagnostic))
                {
                    issues.Add(new(CharacterCatalogIssueKind.CatalogReference, diagnostic!, context.Identity,
                        RelationshipIndex: context.Links.First(link => link.Relationship.Resolution.Candidates.Contains(target)).RelationshipIndex));
                    continue;
                }
                if (!output.TryGetValue(target, out var owners)) output[target] = owners = [];
                if (!owners.Any(item => item.Identity == context.Identity)) owners.Add(context);
            }
        }
        return output;
    }

    private IReadOnlyList<CharacterCatalogLink> MakeCatalogLinks(ResourceIdentity catalogIdentity,
        LocalizationReferenceReadResult result, List<CharacterCatalogIssue> issues)
    {
        var links = new List<CharacterCatalogLink>();
        for (var index = 0; index < result.Relationships.Count; index++)
        {
            var relation = result.Relationships[index];
            if (relation.Reference.ResourceType != 29) continue;
            var diagnostic = ValidateCompanionRelationship(catalogIdentity, relation);
            if (diagnostic is null && relation.Resolution.Status != ReferenceStatus.Unique)
                diagnostic = relation.Resolution.Status == ReferenceStatus.Ambiguous
                    ? "CharacterCatalogCompanionAmbiguous" : "CharacterCatalogCompanionMissing";
            if (diagnostic is not null)
                issues.Add(new(CharacterCatalogIssueKind.CatalogReference, diagnostic, catalogIdentity,
                    RelationshipIndex: index));
            links.Add(new(catalogIdentity, index, result.ReferenceTableSha256, relation, diagnostic));
        }
        return links;
    }

    private string? ValidateCompanionRelationship(ResourceIdentity catalogIdentity,
        AssetRelationship relationship)
    {
        if (relationship.Source != catalogIdentity || relationship.Reference.Namespace != IdentifierNamespace.Archive ||
            relationship.Reference.Owner != catalogIdentity || relationship.Reference.ResourceType != 29 ||
            !relationship.Evidence.Verified)
            return "CharacterCatalogCompanionReferenceInvalid";
        if (!uint.TryParse(relationship.Reference.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return "CharacterCatalogCompanionIdInvalid";
        var expected = snapshot.Records.Where(row => row.Type == 29 && row.Id == id)
            .Select(row => new ResourceIdentity(snapshot.IndexContentId, row.Ordinal)).ToArray();
        if (relationship.Resolution.Status != StatusFor(expected.Length) ||
            !relationship.Resolution.Candidates.SequenceEqual(expected))
            return "CharacterCatalogCompanionResolutionMismatch";
        return null;
    }

    private bool IsValidContainerIdentity(LocalizationReferenceReadResult result,
        ResourceIdentity identity, out string? diagnostic)
    {
        diagnostic = null;
        if (identity.IndexContentId != snapshot.IndexContentId || identity.MemberPath is not null ||
            identity.Ordinal < 0 || identity.Ordinal >= snapshot.Records.Count)
        {
            diagnostic = "CharacterCatalogOwnerIdentityInvalid";
            return false;
        }
        var row = snapshot.Records[checked((int)identity.Ordinal)];
        if (row.Type != 29 || row.Validity != ResourceValidity.Valid)
        {
            diagnostic = "CharacterCatalogOwnerUnavailable";
            return false;
        }
        if (result.Revision != Revision)
        {
            diagnostic = "CharacterCatalogOwnerRevisionMismatch";
            return false;
        }
        return true;
    }

    private bool IsVerifiedRead(ResourceIdentity catalogIdentity, LocalizationReferenceReadResult result) =>
        result.Source == catalogIdentity && result.Revision == Revision &&
        result.Status == LocalizationReferenceReadStatus.Verified &&
        result.ReferenceTableSha256 is { Length: 64 } hash && IsSha256(hash) &&
        result.Relationships.Count <= LocalizationReferenceReader.MaximumReferenceCount;

    private static ReferenceStatus StatusFor(int count) => count switch
    {
        0 => ReferenceStatus.Missing,
        1 => ReferenceStatus.Unique,
        _ => ReferenceStatus.Ambiguous
    };

    private static string LinkKey(CharacterCatalogLink link) =>
        $"{link.CatalogIdentity.Key}/{link.RelationshipIndex:D8}";

    private static bool IsSourceChanged(ArchiveException error) =>
        error.Code is "SourceChanged" or "ArchiveSourceChanged" or "CharacterCatalogSourceChanged" ||
        error.Code.EndsWith("SourceChanged", StringComparison.Ordinal);

    private static bool IsSha256(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private AssetDescriptor ApplyCurrentAlias(AssetDescriptor descriptor)
    {
        var names = descriptor.Names.Where(name => name.Origin != NameOrigin.UserAlias).ToList();
        var storedNames = names.Where(name => name.Origin == NameOrigin.Stored &&
                name.Source.StartsWith("MESHDATA v41 ", StringComparison.Ordinal))
            .Select(name => name.Text).Distinct(StringComparer.Ordinal).ToArray();
        var descriptionLabel = names.FirstOrDefault(name => name.Origin == NameOrigin.Description &&
            name.Source.StartsWith("Common prefix of stored BLSHAPES v3 labels", StringComparison.Ordinal))?.Text;
        var nativeId = descriptor.Metadata.GetValueOrDefault("NativeId") ?? "";
        var fallback = descriptor.Metadata.GetValueOrDefault("model.display-label") ??
            (descriptor.Metadata.GetValueOrDefault("NativeType") == "2013" ? $"Rig {nativeId}" : $"Model {nativeId}");
        var alias = aliases?.Get(descriptor.Identity);
        var label = !string.IsNullOrWhiteSpace(alias) ? alias :
            descriptor.Metadata.ContainsKey("model.display-label") ? fallback :
                storedNames.Length == 1 ? storedNames[0] : descriptionLabel ?? fallback;
        if (!string.IsNullOrWhiteSpace(alias)) names.Add(new(alias, NameOrigin.UserAlias, "AliasStore"));
        var refreshed = descriptor with
        {
            Label = label,
            Names = names,
            SortKey = label
        };
        return refreshed with { SearchTerms = ModelCatalogClassification.SearchTerms(refreshed) };
    }

    private void ValidateExpectedRevision(SourceRevision expectedRevision)
    {
        if (expectedRevision != Revision || expectedRevision.IndexContentId != snapshot.IndexContentId)
            throw new ArchiveException("CharacterCatalogSourceMismatch", "The requested model belongs to another archive revision.");
        EnsureAvailable();
    }

    private static void ValidateCatalogOrder(IReadOnlyList<ResourceIdentity> identities, ArchiveSnapshot snapshot)
    {
        long previous = -1;
        foreach (var identity in identities)
        {
            if (identity.IndexContentId != snapshot.IndexContentId || identity.MemberPath is not null ||
                identity.Ordinal <= previous || identity.Ordinal < 0 || identity.Ordinal >= snapshot.Records.Count)
                throw new ArchiveException("CharacterCatalogProviderOrderInvalid", "Catalog identities must belong to the source and remain in ordinal order.");
            var row = snapshot.Records[checked((int)identity.Ordinal)];
            if (row.Type != 3 || row.Validity != ResourceValidity.Valid)
                throw new ArchiveException("CharacterCatalogProviderIdentityInvalid", "The provider returned a non-catalog resource identity.");
            previous = identity.Ordinal;
        }
    }

    private void ValidateCatalogRequest(ResourceIdentity catalogIdentity, out int catalogIndex)
    {
        ArgumentNullException.ThrowIfNull(catalogIdentity);
        EnsureAvailable();
        if (catalogIdentity.IndexContentId != snapshot.IndexContentId || catalogIdentity.MemberPath is not null)
            throw new ArchiveException("CharacterCatalogSourceMismatch", "The catalog identity belongs to another archive revision.");
        catalogIndex = -1;
        var catalogs = references.CatalogIdentities;
        for (var index = 0; index < catalogs.Count; index++)
            if (catalogs[index] == catalogIdentity) { catalogIndex = index; break; }
        if (catalogIndex < 0)
            throw new ArchiveException("CharacterCatalogUnavailable", "The catalog is not in the provider's valid catalog list.");
    }

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (references.Revision != Revision || references.Snapshot.IndexContentId != snapshot.IndexContentId ||
            references.Snapshot.PhysicalSourceStamp != snapshot.PhysicalSourceStamp)
            throw new ArchiveException("CharacterCatalogSourceChanged", "The archive changed during character discovery.");
    }

    private static long EstimateSourceSummaryRetention(IReadOnlyList<CharacterModelLodChoice> lods)
    {
        var bytes = checked(2048L + lods.Count * 1024L);
        foreach (var lod in lods) bytes = checked(bytes + EstimateGroupStatisticsRetention(lod.GroupStatistics));
        return bytes;
    }

    private static long EstimateModelWorkingSet(IReadOnlyList<CharacterModelBindingResult> bindings, int catalogLinkCount)
    {
        var bytes = checked(4096L + bindings.Count * 4096L + catalogLinkCount * 512L);
        foreach (var binding in bindings)
        {
            bytes = checked(bytes + CharacterModelAnimationLinks.EstimateRetention(binding));
            bytes = checked(bytes + (binding.Rig?.NameEvidence.Sum(name => 512L + name.Text.Length * 2L) ?? 0L));
            foreach (var lod in binding.Lods)
                bytes = checked(bytes + EstimateGroupStatisticsRetention(lod.GroupStatistics));
        }
        return bytes;
    }

    private static long EstimateEntryRetention(ModelAccumulator accumulator) =>
        checked(2048L + accumulator.Bindings.Count * 128L + accumulator.LinkMap.Count * 32L +
            accumulator.Bindings.Sum(CharacterModelAnimationLinks.EstimateRetention) +
            accumulator.SourceSummary.StoredNameEvidence.Sum(name => name.Text.Length * 2L + 256L) +
            accumulator.SourceSummary.StoredLabelObservations.Sum(label => 512L +
                accumulator.SourceSummary.Identity.Key.Length * 2L + (label.Text?.Length ?? 0) * 2L) +
            accumulator.Bindings.Sum(binding => binding.Rig?.NameEvidence.Sum(name => 512L +
                binding.SelectedIdentity.Key.Length * 2L + name.Text.Length * 2L) ?? 0L) +
            accumulator.SourceSummary.Lods.Sum(lod => EstimateGroupStatisticsRetention(lod.GroupStatistics)));

    private static long EstimateGroupStatisticsRetention(NativeModelGroupStatistics? stats) => stats is null ? 0 :
        checked(256L + stats.VertexBufferElementCounts.Count * 16L + stats.IndexBufferElementCounts.Count * 16L +
            stats.RawSelectorHistogram.Count * 48L +
            (stats.StoredBoundsObb.Count + stats.StoredBoundsExtra.Count) * sizeof(float));

    private string ModelEntryFeatureKey(ResourceIdentity identity) =>
        $"{modelEntryCollection}/item/{identity.Key}";

    private string OwnerMembershipKey(ResourceIdentity catalogIdentity, ResourceIdentity ownerIdentity) =>
        $"{ownerMembershipCollection}/item/{catalogIdentity.Key}/{ownerIdentity.Key}";

    private static long EstimatePersistedEntryBytes(CharacterCatalogModelEntry entry)
    {
        long bytes = 8192 + entry.Bindings.Count * 4096L + entry.CatalogLinks.Count * 1536L +
            entry.SourceSummary.Lods.Count * 2048L + entry.SourceSummary.StoredNameEvidence.Sum(name => name.Text.Length * 12L + 256L) +
            entry.SourceSummary.SourceExtents.Count * 512L + entry.SourceSummary.StoredLabelObservations.Count * 1024L;
        bytes = checked(bytes + entry.SourceSummary.Lods.Sum(lod => EstimateGroupStatisticsRetention(lod.GroupStatistics) * 2L));
        bytes = checked(bytes + entry.Bindings.Sum(binding => CharacterModelAnimationLinks.EstimateRetention(binding) * 4L));
        foreach (var observation in entry.SourceSummary.StoredLabelObservations)
            bytes = checked(bytes + (observation.Text?.Length ?? 0) * 12L + observation.RawBytesSha256.Length * 2L);
        foreach (var link in entry.CatalogLinks)
            bytes = checked(bytes + link.Relationship.Resolution.Candidates.Count * 256L +
                link.Relationship.Label.Length * 4L);
        bytes = checked(bytes + entry.Bindings.Sum(binding => (binding.Rig?.NameEvidence.Sum(name =>
            name.Text.Length * 12L + 512L) ?? 0L) +
            binding.NameEvidence.Sum(name => name.Text.Length * 12L + 128L) +
            binding.Lods.Sum(lod => (lod.StoredMeshName?.Length ?? 0) * 12L)));
        return checked(bytes + entry.Descriptor.Names.Sum(name => name.Text.Length * 12L + 128L) +
            entry.Descriptor.Metadata.Sum(pair => (pair.Key.Length + pair.Value.Length) * 12L + 128L) +
            (entry.Descriptor.SearchTerms?.Length ?? 0) * 12L);
    }

    private static long EstimateCachedEntryRetention(CharacterCatalogModelEntry entry) =>
        checked(EstimatePersistedEntryBytes(entry) + 4096L + entry.Bindings.Sum(binding =>
            binding.SourceExtents.Count * 512L + binding.CatalogCompanionLinks.Count * 2048L));

    private void ReduceLoadedReservation(AssetMemoryBudget.Reservation reservation, long retainedBytes,
        ReservationGroup retained)
    {
        var reservedBytes = reservation.Bytes;
        if (retainedBytes > reservedBytes)
            retained.Add(memoryBudget.Reserve(retainedBytes - reservedBytes, "cached model retained metadata"));
        reservation.ReduceTo(Math.Min(retainedBytes, reservedBytes));
    }

    private bool HasForeignFamilyIdentity(AssetDescriptor descriptor, CancellationToken token)
    {
        if (features is null) return false;
        var exactLookup = features.Query(new AssetQuery(ExactKey: descriptor.Identity.Key, Limit: 64), token);
        var identityText = features.Query(new AssetQuery(Search: descriptor.Identity.Key, Limit: 64), token);
        return exactLookup.Concat(identityText).Any(existing => existing.Identity == descriptor.Identity &&
            !string.Equals(existing.Family, descriptor.Family, StringComparison.Ordinal));
    }

    private static void ValidateOptions(CharacterCatalogSessionOptions options)
    {
        if (options.MaximumCatalogsPerBatch is < 1 or > 64 ||
            options.MaximumLabelObservationsPerModel is < 1 or > 4096 ||
            options.MaximumLabelBytes is < 0 or > 1024 * 1024 ||
            options.MaximumLabelCharacters is < 0 or > 64 * 1024 ||
            options.MaximumLabelTextBytesPerModel < 0 || options.MaximumLabelTextBytesPerModel > 16 * 1024 * 1024 ||
            options.MaximumSkeletonNameEvidence is < 0 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    private static void Report(IProgress<CharacterCatalogProgress>? progress,
        int start, int next, int total, int read, int verified, int unsupported,
        long referenceRows, int ownerTargets, int opened, int ownerFailures,
        int membersSeen, int bound, int unsupportedModels, bool allCatalogsVisited,
        bool requestedCatalogsComplete, IReadOnlyList<CharacterCatalogIssue> issues,
        bool? isComplete = null, int? unscanned = null)
    {
        var remaining = unscanned ?? Math.Max(0, total - next);
        var complete = isComplete ?? (requestedCatalogsComplete && allCatalogsVisited && remaining == 0 &&
            unsupported == 0 && ownerFailures == 0 && unsupportedModels == 0 && issues.Count == 0);
        progress?.Report(new(start, next, total, read, verified, unsupported, referenceRows,
            ownerTargets, opened, ownerFailures, membersSeen, bound, unsupportedModels,
            remaining, requestedCatalogsComplete, allCatalogsVisited, complete));
    }

    private static void MergeIssues(List<CharacterCatalogIssue> issues,
        IEnumerable<CharacterCatalogIssue> add)
    {
        foreach (var issue in add)
            if (!issues.Contains(issue)) issues.Add(issue);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        await operationGate.WaitAsync().ConfigureAwait(false);
        ClearOwnerInventoryCache();
        operationGate.Release();
        await aliasIndexGate.WaitAsync().ConfigureAwait(false);
        pendingAliasRefreshes.Clear();
        aliasIndexGate.Release();
        featureStoreWriterCacheLease?.Dispose();
        operationGate.Dispose();
        aliasIndexGate.Dispose();
        lifetime.Dispose();
    }

    private sealed class ReservationGroup : IDisposable
    {
        private List<IDisposable>? items = [];

        public void Add(IDisposable item)
        {
            ArgumentNullException.ThrowIfNull(item);
            var current = items ?? throw new ObjectDisposedException(nameof(ReservationGroup));
            current.Add(item);
        }

        public IDisposable Detach()
        {
            var current = Interlocked.Exchange(ref items, null) ??
                throw new ObjectDisposedException(nameof(ReservationGroup));
            return new CompositeDisposable(current);
        }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref items, null);
            if (current is not null) DisposeReverse(current);
        }
    }

    private sealed class CompositeDisposable(List<IDisposable> items) : IDisposable
    {
        private List<IDisposable>? current = items;
        public void Dispose()
        {
            var resources = Interlocked.Exchange(ref current, null);
            if (resources is not null) DisposeReverse(resources);
        }
    }

    private static void DisposeReverse(List<IDisposable> resources)
    {
        for (var index = resources.Count - 1; index >= 0; index--) resources[index].Dispose();
    }

    private sealed class InventoryOwnerReadContext : IDisposable
    {
        private const int MaximumCachedMeshNamesPerOwner = 16_384;
        private readonly AssetMemoryBudget.Reservation indexReservation;
        private readonly AssetMemoryBudget budget;
        private readonly ReservationGroup nameCacheReservations = new();
        private readonly Dictionary<ResourceIdentity, NativeMeshInventoryNameHints?> meshNameHints = [];
        private readonly Dictionary<ResourceIdentity, SkeletonInventoryNameEvidence> skeletonNameEvidence = [];
        private readonly NativeMeshNameReader nameReader = new();

        public InventoryOwnerReadContext(NativeContainerLease lease, AssetMemoryBudget budget)
        {
            this.budget = budget;
            var objects = lease.Directory.Objects;
            var meshCount = objects.Count(frame => frame.NativeType == 2130);
            indexReservation = budget.Reserve(checked(16 * 1024L + objects.Count * 384L + meshCount * 128L),
                "owner model inventory indices");
            try
            {
                Resolver = new NativeReferenceResolver(lease.Directory);
                var frames = new Dictionary<ResourceIdentity, NativeObjectFrame>(meshCount);
                foreach (var frame in objects)
                    if (frame.NativeType == 2130) frames.Add(frame.Identity, frame);
                MeshDataFrames = frames;
            }
            catch
            {
                indexReservation.Dispose();
                throw;
            }
        }

        public NativeReferenceResolver Resolver { get; }
        public IReadOnlyDictionary<ResourceIdentity, NativeObjectFrame> MeshDataFrames { get; }

        public NativeMeshInventoryNameHints? ReadInventoryHints(ResourceIdentity identity,
            NativeObjectFrame frame, int maximumNameBytes, int maximumShapeNames,
            long maximumShapeNameBytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (meshNameHints.TryGetValue(identity, out var cached)) return cached;
            NativeMeshInventoryNameHints? hints = null;
            if (!nameReader.TryReadInventoryHints(frame, -1, out hints, out _,
                    maximumNameBytes, maximumShapeNames, maximumShapeNameBytes, token)) hints = null;
            if (meshNameHints.Count >= MaximumCachedMeshNamesPerOwner) return hints;
            try
            {
                long labelBytes = hints?.ShapeNames.Sum(item => (long)item.RawBytes.Length + 192) ?? 0;
                nameCacheReservations.Add(budget.Reserve(checked(128L + (hints?.Name?.Text.Length ?? 0) * 2L +
                    labelBytes + (hints?.OmittedShapeNameCountsByGroup.Count ?? 0) * 64L),
                    "owner mesh-name cache"));
                meshNameHints.Add(identity, hints);
            }
            catch (ArchiveException error) when (error.Code == "AssetMemoryLimit") { }
            return hints;
        }

        public IReadOnlyList<NameEvidence> ReadSkeletonNameEvidence(NativeObjectFrame frame,
            SceneEntityRecord entity, int maximumEntries, out bool sourceSkeletonV15Validated,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (skeletonNameEvidence.TryGetValue(frame.Identity, out var cached))
            { sourceSkeletonV15Validated = cached.Validated; return cached.Evidence; }
            var evidence = new List<NameEvidence>(Math.Min(maximumEntries, 64));
            var limits = new NativeSkeletonLimits(MaximumNameEntries: 65_536, MaximumNameBytes: 1024 * 1024);
            sourceSkeletonV15Validated = new NativeSkeletonReader().TryReadNameTables(frame, entity,
                out var tables, out _, limits, token, maximumEntries);
            if (sourceSkeletonV15Validated)
            {
                foreach (var table in tables)
                foreach (var entry in table.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (evidence.Count >= maximumEntries) break;
                    if (!entry.TryGetUtf8Text(out var text, token) || string.IsNullOrWhiteSpace(text) || text.Length > 4096)
                        continue;
                    var source = $"SKELETON v15 {frame.Identity.Key} table {table.Ordinal} key {entry.Key}@{entry.NameExtent.Offset}+{entry.NameExtent.Length}";
                    evidence.Add(new(text!, NameOrigin.Stored, source));
                }
            }
            var result = new SkeletonInventoryNameEvidence(sourceSkeletonV15Validated, evidence.ToArray());
            try
            {
                nameCacheReservations.Add(budget.Reserve(checked(128L + result.Evidence.Sum(item => 256L + item.Text.Length * 2L)),
                    "owner skeleton-name cache"));
                skeletonNameEvidence.Add(frame.Identity, result);
            }
            catch (ArchiveException error) when (error.Code == "AssetMemoryLimit") { }
            return result.Evidence;
        }

        public void Dispose()
        {
            nameCacheReservations.Dispose();
            indexReservation.Dispose();
        }
    }

    private sealed record SkeletonInventoryNameEvidence(bool Validated, IReadOnlyList<NameEvidence> Evidence);

    private sealed record CatalogContext(ResourceIdentity Identity,
        LocalizationReferenceReadResult Result, IReadOnlyList<CharacterCatalogLink> Links);
    private sealed record CatalogReadRow(ResourceIdentity Identity,
        LocalizationReferenceReadResult? Result, string? Diagnostic);

    private sealed record OwnerMembershipManifest(SourceRevision Revision,
        ResourceIdentity CatalogIdentity, string ReferenceTableSha256, ResourceIdentity OwnerIdentity,
        IReadOnlyList<SourceExtent> OwnerExtents, IReadOnlyList<ResourceIdentity> ModelIdentities);

    private sealed record OwnerInventoryCacheEntry(SourceRevision Revision, ResourceIdentity OwnerIdentity, bool InventoryOnly,
        IReadOnlyList<SourceExtent> OwnerExtents, IReadOnlyList<CharacterCatalogModelEntry> Models,
        long RetainedBytes, AssetMemoryBudget.Reservation Reservation);

    private sealed class OwnerMembershipBuilder(CatalogContext context, ResourceIdentity ownerIdentity)
    {
        public CatalogContext Context { get; } = context;
        public ResourceIdentity OwnerIdentity { get; } = ownerIdentity;
        public List<ResourceIdentity> ModelIdentities { get; } = [];
        public IReadOnlyList<SourceExtent> OwnerExtents { get; set; } = [];
        public bool Complete { get; set; } = true;
        public bool EnumerationComplete { get; set; }
    }

    private sealed class ModelAccumulator(CharacterModelSourceSummary sourceSummary)
    {
        public ModelAccumulator(CharacterCatalogModelEntry entry) : this(entry.SourceSummary) =>
            Add(entry.Bindings, entry.CatalogLinks);

        public CharacterModelSourceSummary SourceSummary { get; } = sourceSummary;
        public AssetDescriptor? CachedInventoryDescriptor { get; set; }
        public List<CharacterModelBindingResult> Bindings { get; } = [];
        public Dictionary<string, CharacterCatalogLink> LinkMap { get; } = new(StringComparer.Ordinal);
        public IReadOnlyList<CharacterCatalogLink> Links => LinkMap.Values.ToArray();
        public void Add(IEnumerable<CharacterModelBindingResult> bindings, IEnumerable<CharacterCatalogLink> links)
        {
            foreach (var binding in bindings)
                if (!Bindings.Any(existing => existing.CatalogIdentity == binding.CatalogIdentity)) Bindings.Add(binding);
            foreach (var link in links) LinkMap.TryAdd(LinkKey(link), link);
        }
    }
}
