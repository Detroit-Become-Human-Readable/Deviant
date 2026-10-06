using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Containers;
using Deviant.Formats.Textures;
using Microsoft.Data.Sqlite;

namespace Deviant.Browser;

public sealed record TextureDiscoverySessionOptions(int MaximumContainers = int.MaxValue,
    IReadOnlyList<ResourceIdentity>? PriorityContainers = null);

public sealed record TextureDiscoveryProgress(bool IsRunning, int Completed, int Total, int ImagesRead);

/// <summary>Searchable texture metadata and relationships for one archive revision.</summary>
public sealed class TextureDiscoverySession : ITextureDiscoveryCatalog
{
    public const string Version = "texture-discovery-v5";
    private const int MaximumBytes = 16 * 1024 * 1024;
    private readonly TextureCatalogSession catalogs;
    private readonly NativeTextureSession native;
    private readonly AliasStore aliases;
    private readonly CacheManager? cache;
    private readonly TextureDiscoverySessionOptions options;
    private readonly ArchiveResourceResolver resolver;
    private readonly Dictionary<long, ResourceRecord> resources;
    private readonly Dictionary<ResourceIdentity, TextureDiscoveryEntry> entries = [];
    private readonly Dictionary<ResourceIdentity, HashSet<ResourceIdentity>> supportingCatalogs = [];
    private readonly Dictionary<ResourceIdentity, List<Binding>> metadataBindings = [];
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationTokenSource enrichmentCancellation;
    private readonly SemaphoreSlim detailsGate = new(1, 1);
    private readonly SynchronizationContext? updateContext = SynchronizationContext.Current;
    private readonly HashSet<string> blockers = new(StringComparer.Ordinal);
    private readonly ResourceRecord[] containerRows;
    private Task enrichment = Task.CompletedTask;
    private bool enrichmentRunning = true;
    private bool enrichmentRequested;
    private readonly SemaphoreSlim enrichmentGate = new(1, 1);
    private readonly Dictionary<ResourceIdentity, string> processedMemberships = [];
    private readonly HashSet<ResourceIdentity> permittedContainers = [];
    private readonly HashSet<ResourceIdentity> inspectedContainers = [];
    private readonly Dictionary<ResourceIdentity, OwnerContainerInspection> ownerContainers = [];
    private FeatureStore? features;
    private int containersInspected;
    private int metadataInspected;
    private bool enrichmentComplete;
    private bool disposed;
    private int updatePending;

    public TextureDiscoverySession(TextureCatalogSession catalogs, NativeTextureSession native,
        AliasStore aliases, CacheManager? cache = null, TextureDiscoverySessionOptions? options = null)
    {
        this.catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        this.native = native ?? throw new ArgumentNullException(nameof(native));
        this.aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
        this.cache = cache;
        cache?.RegisterProtectedDirectory(aliases.LibraryRoot);
        this.options = options ?? new();
        if (this.options.MaximumContainers < 1) throw new ArgumentOutOfRangeException(nameof(options));
        if (native.Snapshot != catalogs.Snapshot || native.Revision != catalogs.Revision)
            throw new ArgumentException("Texture sessions must use the same archive revision.", nameof(native));
        resolver = new(Snapshot);
        resources = Snapshot.Records.ToDictionary(row => row.Ordinal);
        containerRows = Snapshot.Records.Where(row => row.Type == 29 && row.Validity == ResourceValidity.Valid)
            .OrderBy(row => row.Ordinal).ToArray();
        foreach (var identity in this.options.PriorityContainers ?? [])
            if (identity.IndexContentId != Snapshot.IndexContentId || identity.MemberPath is not null ||
                !containerRows.Any(row => row.Ordinal == identity.Ordinal))
                throw new ArgumentException("Priority containers must belong to this archive.", nameof(options));
        foreach (var row in Snapshot.Records.Where(row => row.Type is 2229 or 2230))
        {
            var identity = new ResourceIdentity(Snapshot.IndexContentId, row.Ordinal);
            entries.Add(identity, BuildEntry(row, [], [], [], [], []));
        }
        enrichmentCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        enrichment = Task.Run(EnrichAsync);
        catalogs.Updated += CatalogUpdated;
    }

    public ArchiveSnapshot Snapshot => catalogs.Snapshot;
    public SourceRevision Revision => catalogs.Revision;
    public Task EnrichmentTask { get { lock (gate) return enrichment; } }
    public event EventHandler? Updated;
    public TextureDiscoveryProgress Progress
    {
        get { lock (gate) return new(enrichmentRunning, containersInspected, containerRows.Length, metadataInspected); }
    }

    public TextureDiscoveryFilterOptions FilterOptions
    {
        get
        {
            lock (gate)
            {
                var all = entries.Values.ToArray();
                return new(all.Where(entry => entry.Width.HasValue && entry.Height.HasValue)
                    .Select(entry => new TextureDimensionOption(entry.Width!.Value, entry.Height!.Value)).Distinct()
                    .OrderBy(item => item.Width).ThenBy(item => item.Height).ToArray(),
                    all.Select(entry => entry.Format).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    all.SelectMany(entry => entry.Owners.Where(owner => owner.Verified).Select(owner => owner.Role))
                        .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    all.Where(entry => entry.ContextKey is not null && entry.ContextLabel is not null)
                        .Select(entry => new TextureContextOption(entry.ContextKey!, entry.ContextLabel!)).Distinct()
                        .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray())
                { Catalogs = catalogs.Groups.Select(group => new TextureContextOption(group.CatalogIdentity.Key, group.Label)).ToArray() };
            }
        }
    }

    public TextureNamingCoverage Coverage
    {
        get
        {
            lock (gate)
            {
                var all = entries.Values.ToArray();
                var progress = catalogs.Progress;
                var remaining = blockers.Concat(catalogs.Diagnostics.Select(item => item.Code + ": " + item.Message)).Distinct().ToArray();
                return new(all.LongLength, metadataInspected,
                    all.LongCount(entry => entry.NameKind is TextureNameKind.OriginalName or TextureNameKind.OriginalPath),
                    all.LongCount(entry => entry.NameKind == TextureNameKind.VerifiedOwnerRole),
                    all.LongCount(entry => entry.NameKind == TextureNameKind.InferredDescription),
                    all.LongCount(entry => entry.NameKind == TextureNameKind.Fallback),
                    all.LongCount(entry => aliases.Get(entry.Identity) is not null),
                    all.LongCount(entry => entry.ConflictLines.Count > 0), containersInspected, containerRows.LongLength,
                    !enrichmentComplete || progress.DiscoveryIncomplete || remaining.Length > 0,
                    ["COM_CONT v6 typed catalog references", "DC_INFO v3 / DC_DATA v23 object directory",
                        "FILETEXT v24 dimensions and resource pairs", "IBLLIGHT v13 stored context and native texture pairs",
                        "SHADCUST v91 ordered native texture slots"], remaining);
            }
        }
    }

    public async Task<TextureDiscoveryPage> QueryAsync(TextureDiscoveryQuery query, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 || query.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(query));
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        return await Task.Run(() =>
        {
            var found = OrderedEntries(query, own.Token);
            var page = found.Skip(checked((int)Math.Min(query.Offset, int.MaxValue))).Take(query.Limit).ToArray();
            own.Token.ThrowIfCancellationRequested();
            return new TextureDiscoveryPage(page, found.LongLength, query.Offset);
        }, own.Token).ConfigureAwait(false);
    }

    public async Task<TextureDiscoveryPage?> LocateAsync(ResourceIdentity identity, TextureDiscoveryQuery query, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(query));
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        return await Task.Run(() =>
        {
            var found = OrderedEntries(query, own.Token);
            var index = Array.FindIndex(found, entry => entry.Identity == identity);
            if (index < 0) return null;
            var offset = index / query.Limit * query.Limit;
            own.Token.ThrowIfCancellationRequested();
            return new TextureDiscoveryPage(found.Skip(offset).Take(query.Limit).ToArray(), found.LongLength, offset);
        }, own.Token).ConfigureAwait(false);
    }

    public async Task<TextureOwnerQueryResult> QueryByOwnerAsync(ResourceIdentity owner, long offset = 0,
        int limit = 100, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(owner);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var scope = ValidateOwner(owner);
        var container = new ResourceIdentity(owner.IndexContentId, owner.Ordinal);
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        while (true)
        {
            own.Token.ThrowIfCancellationRequested();
            TextureDiscoveryEntry[] matches;
            OwnerContainerInspection? inspection;
            TextureOwnerQueryState state;
            bool incomplete;
            string? reason;
            Dictionary<ResourceIdentity, string?> capturedAliases;
            lock (gate)
            {
                inspection = ownerContainers.GetValueOrDefault(container);
                (state, incomplete, reason) = OwnerQueryState(inspection, scope.Type);
                matches = entries.Values.Where(entry => entry.Owners.Any(link => link.Verified && link.Identity == owner))
                    .ToArray();
                capturedAliases = matches.ToDictionary(entry => entry.Identity, entry => aliases.Get(entry.Identity));
                matches = matches.OrderBy(NameRank)
                    .ThenBy(entry => NaturalOrder.Key(capturedAliases[entry.Identity] ?? TextureNameDisplay.Project(entry.Label)), StringComparer.Ordinal)
                    .ThenBy(entry => entry.Identity.Ordinal).ToArray();
            }
            var selected = matches.Skip(checked((int)Math.Min(offset, int.MaxValue))).Take(limit).ToArray();
            using var pins = PinOwnerSources(selected.SelectMany(entry => entry.NameCandidates.SelectMany(candidate => candidate.Evidence)
                .Concat(entry.Owners.Concat(entry.Related).SelectMany(link => link.Evidence))).Select(evidence => evidence.Source).Append(container), own.Token);
            if (inspection?.DirectoryParsed == true)
            {
                var payload = await ReadPayloadAsync(container, own.Token).ConfigureAwait(false);
                if (payload.Sha256 != inspection.Sha256)
                    throw new ArchiveException("SourceChanged", "The texture owner source changed. Reopen the archive.");
                var segs = new SegsContainerReader().Parse(payload.OriginalBytes.Span, container, Revision, payload.Extent,
                    limits: new(MaximumBytes, MaximumBytes), cancellationToken: own.Token);
                if (!TryBody(segs, own.Token, out var body, out var extent))
                    throw new ArchiveException("SourceChanged", "The texture owner directory is unavailable.");
                var directory = new TextureContainerDirectoryReader().Parse(segs.Value!.DecodedPrefix, body, container,
                    Revision, extent!, [payload.Extent], own.Token).Value
                    ?? throw new ArchiveException("SourceChanged", "The texture owner directory is unavailable.");
                var frame = directory.Objects.SingleOrDefault(item => item.DirectoryIndex == scope.Index);
                if (frame is null || frame.Identity != owner || frame.NativeType != scope.Type || frame.NativeId != scope.Id)
                    throw new ArchiveException("InvalidTextureOwner", "The owner does not match its container directory.");
                if (directory.FindNative(scope.Type, scope.Id).Count != 1)
                    throw new ArchiveException("AmbiguousTextureOwner", "The native owner identifier has multiple directory entries.");
            }
            var verified = new List<TextureDiscoveryEntry>(selected.Length);
            foreach (var entry in selected)
            {
                own.Token.ThrowIfCancellationRequested();
                var details = await GetDetailsAsync(entry.Identity, own.Token).ConfigureAwait(false);
                if (details is not null && details.Owners.Any(link => link.Verified && link.Identity == owner)) verified.Add(details);
            }
            await new LocalizationReferenceReader().ValidateSnapshotIndexHashAsync(Snapshot, own.Token).ConfigureAwait(false);
            lock (gate)
            {
                if (!ReferenceEquals(ownerContainers.GetValueOrDefault(container), inspection) ||
                    verified.Count != selected.Length || matches.Any(entry => !ReferenceEquals(entries.GetValueOrDefault(entry.Identity), entry)) ||
                    entries.Values.Count(entry => entry.Owners.Any(link => link.Verified && link.Identity == owner)) != matches.Length ||
                    capturedAliases.Any(pair => aliases.Get(pair.Key) != pair.Value) ||
                    verified.Any(entry => aliases.Get(entry.Identity) != entry.Alias)) continue;
                var current = OwnerQueryState(inspection, scope.Type);
                if (current != (state, incomplete, reason)) continue;
                own.Token.ThrowIfCancellationRequested();
                reason ??= matches.Length == 0 && state == TextureOwnerQueryState.Ready
                    ? scope.Type is 2133 or 2211 ? "No verified texture relationships are available for this owner."
                        : "No texture relationships are provided by the current adapters for this owner type." : null;
                return new(owner, Revision, new(verified, matches.LongLength, offset), state, incomplete, reason);
            }
        }
    }

    private IDisposable PinOwnerSources(IEnumerable<ResourceIdentity> sources, CancellationToken token)
    {
        var handles = new List<IDisposable>();
        var revisions = new FileRevisionProvider();
        try
        {
            token.ThrowIfCancellationRequested();
            var index = SourceIdentity.OpenPinned(Snapshot.IndexPath); handles.Add(index);
            SourceIdentity.EnsureUnchanged(revisions, index, Snapshot.IndexPath, Snapshot.IndexStamp);
            var packages = sources.Select(source => resources[source.Ordinal].Package).Distinct().ToHashSet();
            foreach (var volume in Snapshot.Volumes.Where(volume => packages.Contains(volume.Package)))
            {
                token.ThrowIfCancellationRequested();
                var handle = SourceIdentity.OpenPinned(volume.FilePath); handles.Add(handle);
                SourceIdentity.EnsureUnchanged(revisions, handle, volume.FilePath, volume.Stamp);
            }
            return new OwnerSourcePins(handles);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }

    private sealed class OwnerSourcePins(List<IDisposable> handles) : IDisposable
    { public void Dispose() { foreach (var handle in handles) handle.Dispose(); } }

    private (int Index, uint Type, uint Id) ValidateOwner(ResourceIdentity owner)
    {
        if (owner.IndexContentId != Snapshot.IndexContentId || !resources.TryGetValue(owner.Ordinal, out var row) || row.Type != 29 ||
            row.Validity != ResourceValidity.Valid || owner.MemberPath is null || owner.MemberPath.Length > 128)
            throw new ArchiveException("InvalidTextureOwner", "Select a native object from this archive.");
        var parts = owner.MemberPath.Split('/');
        if (parts.Length != 4 || parts[0] != "dc-object" ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
            !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var type) ||
            !uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
            owner.MemberPath != $"dc-object/{index.ToString(CultureInfo.InvariantCulture)}/{type.ToString(CultureInfo.InvariantCulture)}/{id.ToString(CultureInfo.InvariantCulture)}")
            throw new ArchiveException("InvalidTextureOwner", "The native object identity is invalid.");
        return (index, type, id);
    }

    private (TextureOwnerQueryState State, bool Incomplete, string? Reason) OwnerQueryState(OwnerContainerInspection? inspection, uint nativeOwnerType)
    {
        var progress = catalogs.Progress;
        var incomplete = progress.DiscoveryIncomplete || blockers.Count > 0 || !enrichmentComplete;
        if (enrichmentCancellation.IsCancellationRequested || progress.State is TextureCatalogSessionState.Cancelled or TextureCatalogSessionState.Failed)
            return (TextureOwnerQueryState.Incomplete, true, "Texture discovery has stopped.");
        if (inspection?.DirectoryParsed == true)
        {
            if (!inspection.HasCatalogMembership && progress.DiscoveryIncomplete && nativeOwnerType is 2133 or 2211)
                return (TextureOwnerQueryState.Incomplete, true, "Texture catalog discovery is incomplete for this container.");
            return (TextureOwnerQueryState.Ready, incomplete, null);
        }
        if (inspection is not null)
            return (TextureOwnerQueryState.Incomplete, true, inspection.Reason ?? "The container layout is unavailable.");
        if (blockers.Contains("Container discovery limit reached"))
            return (TextureOwnerQueryState.Incomplete, true, "The container discovery limit was reached.");
        if (!enrichmentRunning)
            return (TextureOwnerQueryState.Incomplete, true, "The container has not been enriched.");
        return (TextureOwnerQueryState.Pending, true, "Texture discovery is pending for this container.");
    }

    private TextureDiscoveryEntry[] OrderedEntries(TextureDiscoveryQuery query, CancellationToken token)
    {
        TextureDiscoveryEntry[] all;
        lock (gate) all = entries.Values.ToArray();
        var groups = catalogs.Groups;
        if (query.CatalogKey is not null && !groups.Any(group => group.CatalogIdentity.Key == query.CatalogKey))
            throw new ArchiveException("UnknownTextureCatalog", "The selected texture catalog is unavailable.");
        var catalogKeys = groups.SelectMany(group => group.UniqueTextureMembers.Select(identity => (identity, group.CatalogIdentity.Key)))
            .GroupBy(item => item.identity).ToDictionary(group => group.Key, group => group.Select(item => item.Key).Distinct().ToArray());
        var found = new List<TextureDiscoveryEntry>();
        var searchTerms = query.Search?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
        foreach (var item in all)
        {
            token.ThrowIfCancellationRequested();
            var alias = aliases.Get(item.Identity);
            var entry = item with { Alias = alias, SearchText = alias is null ? item.SearchText : item.SearchText + "\n" + alias,
                CatalogKeys = catalogKeys.GetValueOrDefault(item.Identity) ?? [] };
            if (searchTerms.Any(term => !entry.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase))) continue;
            if (query.Width.HasValue && entry.Width != query.Width || query.Height.HasValue && entry.Height != query.Height) continue;
            if (query.Format is not null && entry.Format != query.Format) continue;
            if (query.VerifiedRole is not null && !entry.Owners.Any(owner => owner.Verified && owner.Role == query.VerifiedRole)) continue;
            if (query.HasVerifiedOwner.HasValue && entry.Owners.Any(link => link.Verified) != query.HasVerifiedOwner.Value) continue;
            if (query.ContextKey is not null && entry.ContextKey != query.ContextKey) continue;
            if (query.CatalogKey is not null && !entry.CatalogKeys.Contains(query.CatalogKey, StringComparer.Ordinal)) continue;
            found.Add(entry);
        }
        token.ThrowIfCancellationRequested();
        if (!query.SortByName) return found.OrderBy(entry => entry.Identity.Ordinal).ToArray();
        return (query.NamedFirst
            ? found.OrderBy(NameRank).ThenBy(entry => NaturalOrder.Key(entry.Alias ?? TextureNameDisplay.Project(entry.Label)), StringComparer.Ordinal).ThenBy(entry => entry.Identity.Ordinal)
            : found.OrderBy(entry => NaturalOrder.Key(entry.Alias ?? TextureNameDisplay.Project(entry.Label)), StringComparer.Ordinal).ThenBy(entry => entry.Identity.Ordinal)).ToArray();
    }

    private static int NameRank(TextureDiscoveryEntry entry) => entry.NameKind switch
    {
        TextureNameKind.OriginalName or TextureNameKind.OriginalPath => 0,
        TextureNameKind.VerifiedOwnerRole => 1,
        TextureNameKind.InferredDescription => 2,
        _ => entry.Alias is not null ? 3 : 4
    };

    public async Task<TextureDiscoveryEntry?> GetDetailsAsync(ResourceIdentity identity, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await detailsGate.WaitAsync(own.Token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                own.Token.ThrowIfCancellationRequested();
                TextureDiscoveryEntry? entry;
                ResourceIdentity[] memberships;
                lock (gate)
                {
                    entry = entries.GetValueOrDefault(identity);
                    memberships = supportingCatalogs.TryGetValue(identity, out var owners) ? owners.ToArray() : [];
                }
                if (entry is null) return null;
                var catalogRelationships = catalogs.Groups.Select(group => new TextureCatalogRelatedGroup(group,
                        group.References.Where(reference => reference.Candidates.Contains(identity)).ToArray()))
                    .Where(group => group.MatchingReferences.Count > 0).ToArray();
                var evidence = entry.NameCandidates.SelectMany(candidate => candidate.Evidence)
                    .Concat(entry.Owners.Concat(entry.Related).SelectMany(link => link.Evidence)).ToArray();
                foreach (var source in evidence.GroupBy(item => item.Source))
                {
                    own.Token.ThrowIfCancellationRequested();
                    if (source.Any(item => item.Revision != Revision)) throw new ArchiveException("SourceChanged", "The texture context belongs to another revision.");
                    foreach (var range in source.GroupBy(item => item.PhysicalExtent))
                    {
                        var row = resources[source.Key.Ordinal];
                        var extent = range.Key ?? throw new ArchiveException("TextureEvidenceExtent", "The texture evidence has no source range.");
                        var relative = extent.Offset - row.Offset;
                        if (relative < 0 || extent.Length < 1 || extent.Length > MaximumBytes || extent.Length > row.StoredSize - relative)
                            throw new ArchiveException("TextureEvidenceExtent", "The texture evidence exceeds its source range.");
                        var bytes = await resolver.ReadAsync(source.Key, relative, checked((int)extent.Length), own.Token).ConfigureAwait(false);
                        var digest = Hash(bytes);
                        if (range.Any(item => item.SourceSha256 != digest))
                            throw new ArchiveException("SourceChanged", "The texture context changed. Reopen the archive.");
                    }
                }
                foreach (var catalog in memberships.Concat(catalogRelationships.Select(group => group.Catalog.CatalogIdentity)).Distinct())
                    await native.RevalidateCatalogAsync(catalog, own.Token).ConfigureAwait(false);
                await new LocalizationReferenceReader().ValidateSnapshotIndexHashAsync(Snapshot, own.Token).ConfigureAwait(false);
                lock (gate)
                {
                    if (ReferenceEquals(entries.GetValueOrDefault(identity), entry)) return WithAlias(AddCatalogDetails(entry, catalogRelationships));
                }
            }
        }
        finally { detailsGate.Release(); }
    }

    private static TextureDiscoveryEntry AddCatalogDetails(TextureDiscoveryEntry entry,
        IReadOnlyList<TextureCatalogRelatedGroup> relationships)
    {
        var advanced = entry.AdvancedLines.ToList();
        var conflicts = entry.ConflictLines.ToList();
        foreach (var relationship in relationships)
        {
            var catalog = relationship.Catalog;
            advanced.Add($"{catalog.Label}: table SHA-256 {catalog.ReferenceTableSha256}");
            foreach (var reference in relationship.MatchingReferences)
            {
                advanced.Add($"{catalog.Label}, row {reference.ReferenceIndex}: {reference.Reference.ResourceType}/{reference.Reference.Id}; " +
                    $"{reference.ResolutionStatus}; {reference.Candidates.Count} candidate(s); " +
                    $"{reference.Extent.RelativeSource} +0x{reference.Extent.Offset:X}, {reference.Extent.Length} bytes; SHA-256 {reference.Extent.PayloadSha256}");
                if (reference.ResolutionStatus == ReferenceStatus.Ambiguous || reference.IsDuplicate)
                    conflicts.Add($"{catalog.Label}, row {reference.ReferenceIndex}: {reference.ResolutionStatus}; " +
                        $"{reference.Candidates.Count} candidate(s)" + (reference.IsDuplicate ? "; duplicate reference" : ""));
            }
        }
        return entry with { AdvancedLines = advanced.Distinct().ToArray(), ConflictLines = conflicts.Distinct().ToArray() };
    }

    public async Task SetAliasAsync(ResourceIdentity identity, string? alias, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate) if (!entries.ContainsKey(identity)) throw new ArgumentException("Select a texture from this archive.", nameof(identity));
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await aliases.SetAsync(identity, alias, own.Token).ConfigureAwait(false);
        PublishUpdate();
    }

    private TextureDiscoveryEntry WithAlias(TextureDiscoveryEntry entry)
    {
        var alias = aliases.Get(entry.Identity);
        return entry with { Alias = alias, SearchText = alias is null ? entry.SearchText : entry.SearchText + "\n" + alias,
            CatalogKeys = catalogs.FindGroupsForTexture(entry.Identity).Select(group => group.CatalogIdentity.Key).Distinct().ToArray() };
    }

    private sealed record Binding(ResourceIdentity Texture, uint NativeId, int Width, int Height, string Format,
        ResourceIdentity MetadataIdentity, TextureEvidence Evidence, ResourceIdentity Catalog,
        TextureEvidence DirectoryEvidence, IReadOnlyList<TextureEvidence> ArchiveEvidence);
    private sealed record Owner(ResourceIdentity Identity, string Label, string? Path, uint NativeType, uint NativeId,
        string? Role, TextureEvidence Evidence, TextureEvidence? NameEvidence, TextureEvidence? PathEvidence,
        TextureRelationshipKind Kind = TextureRelationshipKind.Object, bool UniquePathTarget = false);
    private sealed record ContainerFeatures(string SourceSha256, IReadOnlyList<Binding> Bindings,
        IReadOnlyList<Owner> Owners, IReadOnlyList<string> Diagnostics)
    {
        public bool DirectoryParsed { get; init; }
    }
    private sealed record OwnerContainerInspection(string Sha256, bool DirectoryParsed, bool HasCatalogMembership,
        string? Reason);

    private async Task EnrichAsync()
    {
        var token = enrichmentCancellation.Token;
        await enrichmentGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await catalogs.EnrichmentTask.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (catalogs.Progress.State is TextureCatalogSessionState.Cancelled or TextureCatalogSessionState.Failed)
            { lock (gate) blockers.Add("Catalog discovery stopped"); return; }
            do
            {
                lock (gate) enrichmentRequested = false;
                var memberships = new Dictionary<ResourceIdentity, List<TextureCatalogMembership>>();
                var reader = new LocalizationReferenceReader();
                foreach (var group in catalogs.Groups)
                {
                    token.ThrowIfCancellationRequested();
                    var table = await reader.ReadAsync(Snapshot, group.CatalogIdentity, token).ConfigureAwait(false);
                    if (table.Status != LocalizationReferenceReadStatus.Verified || table.ReferenceTableSha256 != group.ReferenceTableSha256)
                        throw new ArchiveException("SourceChanged", "A texture catalog changed. Reopen the archive.");
                    var companions = table.Relationships.Where(item => item.Reference.ResourceType == 29 && item.NavigationTarget is not null)
                        .Select(item => item.NavigationTarget!).Distinct().ToArray();
                    foreach (var companion in companions)
                    {
                        if (!memberships.TryGetValue(companion, out var list)) memberships.Add(companion, list = []);
                        list.Add(new(group.CatalogIdentity, Revision, group.CatalogId, group.CatalogExtent,
                            group.SourceReferenceCount, group.SourceReferenceCount - group.References.Count, group.References));
                    }
                }
                if (features is null && cache is not null && cache.LimitBytes > 0)
                {
                    try { features = FeatureStore.Open(Snapshot, cache.Location); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
                    { lock (gate) blockers.Add("Metadata cache: " + exception.Message); }
                }
                var priorities = (options.PriorityContainers ?? []).Select((identity, index) => (identity.Ordinal, index))
                    .DistinctBy(item => item.Ordinal).ToDictionary(item => item.Ordinal, item => item.index);
                var ordered = containerRows.OrderBy(row => priorities.GetValueOrDefault(row.Ordinal, int.MaxValue))
                    .ThenByDescending(row => memberships.ContainsKey(new(Snapshot.IndexContentId, row.Ordinal))).ThenBy(row => row.Ordinal);
                foreach (var row in ordered)
                {
                    token.ThrowIfCancellationRequested();
                    var identity = new ResourceIdentity(Snapshot.IndexContentId, row.Ordinal);
                    var groups = memberships.GetValueOrDefault(identity) ?? [];
                    var memberKey = string.Join("/", groups.Select(group => group.CatalogIdentity.Key + ":" +
                        catalogs.Groups.Single(item => item.CatalogIdentity == group.CatalogIdentity).ReferenceTableSha256).Order(StringComparer.Ordinal));
                    if (processedMemberships.GetValueOrDefault(identity) == memberKey) continue;
                    if (!permittedContainers.Contains(identity))
                    {
                        if (permittedContainers.Count >= options.MaximumContainers)
                        { lock (gate) blockers.Add("Container discovery limit reached"); continue; }
                        permittedContainers.Add(identity);
                    }
                    if (row.StoredSize is < 16 or > MaximumBytes)
                    { lock (gate) blockers.Add("Container resource size limit"); continue; }
                    try
                    {
                        var payload = await ReadPayloadAsync(identity, token).ConfigureAwait(false);
                        var featureKey = "texture.context/" + identity.Ordinal + "/" + payload.Sha256 + "/" + Hash(System.Text.Encoding.UTF8.GetBytes(memberKey));
                        var version = Version + ":" + TextureContainerDirectoryReader.Version + ":" + TextureContextReader.Version + ":" + TextureFileTextV24Reader.Version + ":" + TextureUsageReader.Version;
                        ContainerFeatures? derived = null;
                        try { derived = features?.Load<ContainerFeatures>(featureKey, version, token); }
                        catch (Exception exception) when (exception is JsonException or IOException or SqliteException) { lock (gate) blockers.Add("Metadata cache: " + exception.Message); }
                        if (derived is null || !ValidFeatures(derived, payload, groups, token))
                        {
                            derived = ParseContainer(payload, groups, token);
                            if (derived is not null)
                            {
                                try { features?.Save(featureKey, version, derived, token); }
                                catch (Exception exception) when (exception is IOException or SqliteException) { lock (gate) blockers.Add("Metadata cache: " + exception.Message); }
                            }
                        }
                        processedMemberships[identity] = memberKey;
                        lock (gate)
                        {
                            if (derived is not null) ApplyContainer(derived);
                            ownerContainers[identity] = new(payload.Sha256, derived?.DirectoryParsed == true, groups.Count > 0,
                                derived?.Diagnostics.FirstOrDefault());
                            if (inspectedContainers.Add(identity)) containersInspected++;
                        }
                        PublishUpdate();
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (ArchiveException exception) when (exception.Code == "SourceChanged") { throw; }
                    catch (Exception exception) when (exception is IOException or ArgumentException or OverflowException)
                    { lock (gate) blockers.Add("Container read: " + exception.Message); }
                }
                lock (gate) if (!enrichmentRequested) break;
            } while (!token.IsCancellationRequested);
            lock (gate) enrichmentComplete = true;
        }
        catch (OperationCanceledException) when (enrichmentCancellation.IsCancellationRequested) { }
        catch (Exception exception) { lock (gate) blockers.Add(exception.Message); }
        finally
        {
            features?.Dispose(); features = null;
            lock (gate)
            {
                enrichmentRunning = false;
                if (enrichmentRequested && !disposed && !enrichmentCancellation.IsCancellationRequested)
                {
                    enrichmentRunning = true; enrichmentComplete = false;
                    enrichment = Task.Run(EnrichAsync);
                }
            }
            enrichmentGate.Release(); PublishUpdate();
        }
    }

    private bool ValidFeatures(ContainerFeatures value, NativeTexturePayload source,
        IReadOnlyList<TextureCatalogMembership> memberships, CancellationToken token)
    {
        if (value.SourceSha256 != source.Sha256 || value.Bindings is null || value.Owners is null || value.Diagnostics is null) return false;
        var sourceIdentity = source.Identity;
        bool SourceEvidence(TextureEvidence? evidence, string reader, string version)
        {
            if (evidence is null || evidence.Revision != Revision || evidence.Source != sourceIdentity ||
                evidence.Source.MemberPath is not null || evidence.SourceSha256 != source.Sha256 ||
                evidence.Reader != reader || evidence.ReaderVersion != version || evidence.PhysicalExtent != source.Extent ||
                evidence.DecodedExtent is not { } decoded || decoded.Container != sourceIdentity || decoded.Offset < 0 ||
                decoded.Length < 0 || decoded.Offset > 2L * MaximumBytes - decoded.Length) return false;
            return source.Extent.PayloadSha256 == evidence.SourceSha256;
        }
        foreach (var binding in value.Bindings)
        {
            token.ThrowIfCancellationRequested();
            if (binding is null || binding.Texture is null || binding.Texture.IndexContentId != Snapshot.IndexContentId ||
                binding.Texture.MemberPath is not null || !entries.ContainsKey(binding.Texture) ||
                binding.MetadataIdentity is null || binding.MetadataIdentity.IndexContentId != Snapshot.IndexContentId ||
                binding.MetadataIdentity.Ordinal != sourceIdentity.Ordinal ||
                binding.MetadataIdentity.MemberPath != "filetext/" + binding.Evidence?.DecodedExtent?.Offset.ToString(CultureInfo.InvariantCulture) ||
                !SourceEvidence(binding.Evidence, nameof(TextureFileTextV24Reader), TextureFileTextV24Reader.Version) ||
                !SourceEvidence(binding.DirectoryEvidence, nameof(TextureContainerDirectoryReader), TextureContainerDirectoryReader.Version) ||
                binding.Catalog is null || binding.Catalog.IndexContentId != Snapshot.IndexContentId || binding.Catalog.MemberPath is not null ||
                binding.ArchiveEvidence is null || binding.ArchiveEvidence.Count == 0) return false;
            var membership = memberships.SingleOrDefault(item => item.CatalogIdentity == binding.Catalog);
            if (membership is null) return false;
            foreach (var evidence in binding.ArchiveEvidence)
            {
                if (evidence is null || evidence.Source != binding.Catalog || evidence.Revision != Revision ||
                    evidence.Reader != nameof(TextureCatalogMembershipReader) || evidence.ReaderVersion != "catalog-membership-v1" ||
                    evidence.DecodedExtent is not null || evidence.PhysicalExtent is not { Length: 9 } extent ||
                    evidence.SourceSha256 != extent.PayloadSha256 ||
                    !membership.TextureReferences.Any(reference => reference.Extent == extent &&
                        reference.ResolutionStatus == ReferenceStatus.Unique && reference.Candidates.Count == 1 &&
                        reference.Candidates[0] == binding.Texture)) return false;
            }
        }
        foreach (var owner in value.Owners)
        {
            token.ThrowIfCancellationRequested();
            if (owner is null || owner.Identity is null || owner.Identity.IndexContentId != Snapshot.IndexContentId ||
                owner.Identity.Ordinal != sourceIdentity.Ordinal || owner.Identity.MemberPath?.StartsWith("dc-object/", StringComparison.Ordinal) != true ||
                owner.NativeType != 2137 || owner.NativeId is 0 or uint.MaxValue || owner.Kind != TextureRelationshipKind.Object) return false;
            if (owner.Role == "Lighting")
            {
                if (!SourceEvidence(owner.Evidence, nameof(TextureContextReader), TextureContextReader.Version) ||
                    !SourceEvidence(owner.NameEvidence, nameof(TextureContextReader), TextureContextReader.Version) ||
                    !SourceEvidence(owner.PathEvidence, nameof(TextureContextReader), TextureContextReader.Version)) return false;
            }
            else if (owner.Role is not null || owner.NameEvidence is not null || owner.PathEvidence is not null || owner.Path is not null ||
                !SourceEvidence(owner.Evidence, nameof(TextureUsageReader), TextureUsageReader.Version)) return false;
        }
        // Check stored field values and their complete reference chain against the bounded source bytes.
        var expected = ParseContainer(source, memberships, token);
        return expected is not null && JsonSerializer.Serialize(value) == JsonSerializer.Serialize(expected);
    }

    private ContainerFeatures? ParseContainer(NativeTexturePayload payload, IReadOnlyList<TextureCatalogMembership> memberships, CancellationToken token)
    {
        var segs = new SegsContainerReader().Parse(payload.OriginalBytes.Span, payload.Identity, Revision,
            payload.Extent, limits: new(MaximumBytes, MaximumBytes), cancellationToken: token);
        if (!TryBody(segs, token, out var body, out var bodyExtent))
            return memberships.Count == 0 ? null : new(payload.Sha256, [], [], ["Texture container body is unavailable"]);
        var parsed = new TextureContainerDirectoryReader().Parse(segs.Value!.DecodedPrefix, body, payload.Identity,
            Revision, bodyExtent!, [payload.Extent], token);
        if (parsed.Value is not { } directory)
            return memberships.Count == 0 ? null : new(payload.Sha256, [], [], parsed.Diagnostics);
        var bindings = new List<Binding>();
        var owners = new List<Owner>();
        var diagnostics = new List<string>();
        var directoryPairs = directory.Objects.GroupBy(frame => (frame.NativeType, frame.NativeId))
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (var frame in directory.Objects)
        {
            token.ThrowIfCancellationRequested();
            if (frame.NativeType == 2133)
            {
                var usage = new TextureUsageReader().ParseShaderSlots(frame, Revision, [payload.Extent], token).Value;
                if (usage is not null)
                {
                    foreach (var slot in usage.Slots.Where(item => item.Reference.IsBound && item.Reference.Type == 2137))
                    {
                        if (directoryPairs.GetValueOrDefault((slot.Reference.Type, slot.Reference.Id)) != 1) continue;
                        owners.Add(new(frame.Identity, $"Custom shader 0x{frame.NativeId:X8}", null,
                            slot.Reference.Type, slot.Reference.Id, null,
                            Evidence(payload, nameof(TextureUsageReader), TextureUsageReader.Version,
                                $"SHADCUST texture slot {slot.SlotOrdinal}", slot.Extent), null, null, TextureRelationshipKind.Object));
                    }
                }
            }
            if (frame.NativeType == 2137)
            {
                var tag = FindTag(frame.RawPayload.Span, "FILETEXT"u8, 24, token);
                if (tag < 0) continue;
                var end = FindTag(frame.RawPayload.Span[(tag + 12)..], "FTEXCRAW"u8, 1, token);
                if (end < 0) continue;
                end += tag + 12;
                var offset = checked((int)(frame.PayloadExtent.Offset - bodyExtent!.Offset)) + tag;
                foreach (var membership in memberships)
                {
                    var metadata = new TextureFileTextV24Reader().Parse(body, offset, offset + end - tag,
                        payload.Identity, Revision, bodyExtent, [payload.Extent], membership, token).Value;
                    if (metadata is null) continue;
                    var evidence = Evidence(payload, nameof(TextureFileTextV24Reader), TextureFileTextV24Reader.Version,
                        "FILETEXT dimensions and typed archive references", metadata.ExactConsumedDecodedExtent);
                    foreach (var reference in metadata.References.Where(item => item.Resolution.Status == ReferenceStatus.Unique && item.Resolution.Candidates.Count == 1))
                    {
                        var target = reference.Resolution.Candidates[0];
                        if (!entries.ContainsKey(target)) continue;
                        var width = (int)metadata.Mip0Width; var height = (int)metadata.Mip0Height;
                        if (reference.Sequence > 0 && reference.Sequence - 1 < metadata.LodDescriptors.Count)
                        { width = metadata.LodDescriptors[reference.Sequence - 1].Width; height = metadata.LodDescriptors[reference.Sequence - 1].Height; }
                        var format = metadata.PostSelectorBoolean ? "Converted" : metadata.FirstByte switch
                        {
                            0 => "BGRA8",
                            5 => "BC1",
                            7 => "BC3",
                            18 => "BC4",
                            25 => "BC6H",
                            _ => $"Format {metadata.FirstByte}"
                        };
                        var archiveEvidence = membership.TextureReferences.Where(item => reference.CatalogReferenceIndices.Contains(item.ReferenceIndex))
                            .Select(item => new TextureEvidence(membership.CatalogIdentity, Revision,
                                nameof(TextureCatalogMembershipReader), "catalog-membership-v1", item.Extent.PayloadSha256!,
                                $"Typed archive reference {reference.Reference.ResourceType}/{reference.Reference.Id}", item.Extent)).ToArray();
                        bindings.Add(new(target, frame.NativeId, width, height, format, metadata.Identity, evidence, membership.CatalogIdentity,
                            Evidence(payload, nameof(TextureContainerDirectoryReader), TextureContainerDirectoryReader.Version,
                                $"Native texture 2137/0x{frame.NativeId:X8}", frame.DirectoryEntryExtent), archiveEvidence));
                    }
                }
            }
            var iblTag = FindTag(frame.RawPayload.Span, "IBLLIGHT"u8, 13, token);
            if (iblTag < 0) continue;
            var iblOffset = checked((int)(frame.PayloadExtent.Offset - bodyExtent!.Offset)) + iblTag;
            var iblEnd = checked((int)(frame.PayloadExtent.Offset + frame.PayloadExtent.Length - bodyExtent.Offset));
            var context = new TextureContextReader().ParseIblLightV13(body, iblOffset, iblEnd, frame.Identity,
                Revision, bodyExtent, [payload.Extent], token).Value;
            if (context is null) continue;
            foreach (var reference in context.TextureReferences.Where(item => item.IsBound))
            {
                if (reference.Type != 2137 || directoryPairs.GetValueOrDefault((reference.Type, reference.Id)) != 1) continue;
                var label = string.IsNullOrWhiteSpace(context.OwnerName.Text) ? "Environment light" : context.OwnerName.Text;
                owners.Add(new(frame.Identity, label, context.SourcePath.Text, reference.Type, reference.Id, "Lighting",
                    Evidence(payload, nameof(TextureContextReader), TextureContextReader.Version, "Native texture reference", reference.Extent),
                    Evidence(payload, nameof(TextureContextReader), TextureContextReader.Version, "Stored light name", context.OwnerName.ValueExtent),
                    Evidence(payload, nameof(TextureContextReader), TextureContextReader.Version, "Stored light source path", context.SourcePath.ValueExtent),
                    UniquePathTarget: context.TextureReferences.Where(item => item.IsBound).Select(item => (item.Type, item.Id)).Distinct().Count() == 1));
            }
        }
        if (directory.RemainderBytes.Length > 0) diagnostics.Add("Unparsed container tail");
        return new(payload.Sha256, bindings.Distinct().ToArray(), owners.Distinct().ToArray(), diagnostics) { DirectoryParsed = true };
    }

    private void ApplyContainer(ContainerFeatures derived)
    {
        lock (gate)
        {
            foreach (var diagnostic in derived.Diagnostics) blockers.Add(diagnostic);
            foreach (var group in derived.Bindings.GroupBy(binding => binding.Texture))
            {
                if (!entries.TryGetValue(group.Key, out var prior)) continue;
                var currentBindings = group.ToArray();
                if (!metadataBindings.TryGetValue(group.Key, out var accumulated))
                { metadataBindings.Add(group.Key, accumulated = []); metadataInspected++; }
                accumulated.AddRange(currentBindings.Where(binding => !accumulated.Any(existing =>
                    existing.Texture == binding.Texture && existing.NativeId == binding.NativeId &&
                    existing.MetadataIdentity == binding.MetadataIdentity && existing.Catalog == binding.Catalog &&
                    existing.Evidence.SourceSha256 == binding.Evidence.SourceSha256)));
                var bindings = accumulated.ToArray();
                var linkedOwners = derived.Owners.Where(owner => currentBindings.Any(binding => binding.NativeId == owner.NativeId)).ToArray();
                var names = prior.NameCandidates.ToList();
                var ownerLinks = prior.Owners.ToList();
                foreach (var owner in linkedOwners)
                {
                    var evidence = new[] { owner.Evidence, owner.NameEvidence, owner.PathEvidence }.OfType<TextureEvidence>()
                        .Concat(currentBindings.Where(binding => binding.NativeId == owner.NativeId).SelectMany(binding =>
                            new[] { binding.Evidence, binding.DirectoryEvidence }.Concat(binding.ArchiveEvidence))).Distinct().ToArray();
                    ownerLinks.Add(new(owner.Identity, owner.Label, owner.Kind, resources.ContainsKey(owner.Identity.Ordinal), true, evidence, owner.Role));
                    if (owner.Role == "Lighting" && owner.UniquePathTarget && !string.IsNullOrWhiteSpace(owner.Path))
                        names.Add(new(owner.Path, TextureNameKind.OriginalPath, evidence, owner.Identity, "Lighting"));
                    else if (owner.Role == "Lighting")
                        names.Add(new("Lighting texture · " + owner.Label, TextureNameKind.InferredDescription, evidence, owner.Identity));
                }
                var related = prior.Related.ToList();
                foreach (var binding in currentBindings)
                {
                    related.Add(new(binding.Evidence.Source, "Texture container", TextureRelationshipKind.Container, true, true,
                        new[] { binding.Evidence, binding.DirectoryEvidence }.Concat(binding.ArchiveEvidence).ToArray()));
                    if (!supportingCatalogs.TryGetValue(group.Key, out var set)) supportingCatalogs.Add(group.Key, set = []);
                    set.Add(binding.Catalog);
                    foreach (var other in derived.Bindings.Where(item => item.NativeId == binding.NativeId && item.Texture != group.Key))
                        related.Add(new(other.Texture, $"0x{resources[other.Texture.Ordinal].Id:X8} · {other.Width} x {other.Height}",
                            TextureRelationshipKind.RelatedTexture, true, true, [other.Evidence]));
                }
                var distinctMetadata = bindings.Select(binding => (binding.Width, binding.Height, binding.Format)).Distinct().ToArray();
                var conflicts = prior.ConflictLines.ToList();
                if (distinctMetadata.Length > 1) conflicts.AddRange(distinctMetadata.Select(item => $"{item.Width} x {item.Height} · {item.Format}"));
                var selected = distinctMetadata.Length == 1 ? bindings[0] : null;
                var advanced = prior.AdvancedLines.Concat(bindings.Select(binding => $"FILETEXT: {binding.MetadataIdentity.MemberPath}; native texture 0x{binding.NativeId:X8}"))
                    .Concat(linkedOwners.Where(owner => !string.IsNullOrEmpty(owner.Path)).Select(owner => "Stored source DDS path in lighting record: " + owner.Path))
                    .Concat(derived.Diagnostics).Distinct().ToArray();
                entries[group.Key] = BuildEntry(resources[group.Key.Ordinal], names.Distinct().ToArray(), MergeLinks(ownerLinks),
                    related.DistinctBy(link => (link.Identity, link.Kind)).ToArray(), conflicts.Distinct().ToArray(), advanced,
                    selected?.Width, selected?.Height, selected?.Format);
            }
        }
    }

    private static IReadOnlyList<TextureDiscoveryLink> MergeLinks(IEnumerable<TextureDiscoveryLink> links) =>
        links.GroupBy(link => (link.Identity, link.Kind)).Select(group => group.First() with
        { Evidence = group.SelectMany(link => link.Evidence).Distinct().ToArray() }).ToArray();

    private TextureDiscoveryEntry BuildEntry(ResourceRecord row, IReadOnlyList<TextureNameCandidate> names,
        IReadOnlyList<TextureDiscoveryLink> owners, IReadOnlyList<TextureDiscoveryLink> related,
        IReadOnlyList<string> conflicts, IReadOnlyList<string> advanced, int? width = null, int? height = null, string? format = null)
    {
        var identity = new ResourceIdentity(Snapshot.IndexContentId, row.Ordinal);
        var decision = TextureNamePolicy.Decide(identity, row.Type, row.Id, width, height, format, names);
        var allConflicts = conflicts.Concat(decision.Conflicts).Distinct().ToArray();
        var context = owners.Where(owner => owner.Verified && owner.Role == "Lighting")
            .Select(owner => owner.Label).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var contextLabel = context.Length == 1 ? context[0] : null;
        var contextKey = contextLabel is null ? null : "owner/" + Hash(System.Text.Encoding.UTF8.GetBytes(contextLabel));
        var search = string.Join('\n', names.Select(candidate => candidate.Text).Concat(owners.Select(owner => owner.Label))
            .Concat(allConflicts).Concat(advanced).Append(decision.Label).Append(decision.OriginalPath ?? "").Append(identity.Key)
            .Append($"0x{row.Id:X8}").Append(row.Id.ToString(CultureInfo.InvariantCulture)).Append(format ?? ""));
        var provenance = names.SelectMany(candidate => candidate.Evidence).Concat(owners.Concat(related).SelectMany(link => link.Evidence))
            .Select(item => $"{item.Reader} · {item.Field}").Distinct().ToArray();
        var roles = owners.Where(owner => owner.Verified).Select(owner => owner.Role).OfType<string>().Distinct().ToArray();
        return new(identity, row.Id, row.Type, decision.Label, decision.Kind, decision.OriginalName, decision.OriginalPath,
            null, width, height, format, roles.Length == 1 ? roles[0] : null,
            owners, related, names, provenance, allConflicts, advanced, decision.ExportStem, search, contextKey, contextLabel);
    }

    private TextureEvidence Evidence(NativeTexturePayload source, string reader, string version, string field, DecodedExtent decoded) =>
        new(source.Identity, Revision, reader, version, source.Sha256, field, source.Extent, decoded);

    private async Task<NativeTexturePayload> ReadPayloadAsync(ResourceIdentity identity, CancellationToken token)
    {
        var row = resources[identity.Ordinal];
        if (identity.IndexContentId != Snapshot.IndexContentId || identity.MemberPath is not null || row.Validity != ResourceValidity.Valid || row.StoredSize is < 1 or > MaximumBytes)
            throw new ArchiveException("TextureResourceLimit", "The texture context exceeds the supported resource limit.");
        var bytes = await resolver.ReadAsync(identity, 0, checked((int)row.StoredSize), token).ConfigureAwait(false);
        var hash = Hash(bytes);
        var volume = Snapshot.Volumes.Single(item => item.Package == row.Package);
        return new(identity, row.Type, row.Id, new(volume.RelativeName, row.Offset, row.StoredSize, hash), bytes, hash);
    }

    private static bool TryBody(DecodeResult<SegsDocument> segs, CancellationToken token, out byte[] body, out DecodedExtent? extent)
    {
        body = []; extent = null;
        var prefix = segs.Value?.DecodedPrefix;
        if (prefix is null || prefix.Length < 24 || !prefix.AsSpan(0, 8).SequenceEqual("DC_INFO "u8) || BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(8)) != 3) return false;
        var infoLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(12));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(20));
        if (infoLength != 8L + count * 8L || infoLength > prefix.Length - 16) return false;
        var offset = checked(16L + infoLength);
        var nested = new RawNestedRecordReader().Inspect(segs, offset, token).Value;
        if (nested is null || nested.TagText != "DC_DATA " || nested.Version != 23 || nested.DeclaredBodyLength > MaximumBytes || nested.BodySlices.Count == 0) return false;
        body = new byte[checked((int)nested.DeclaredBodyLength)]; long cursor = 0;
        foreach (var slice in nested.BodySlices)
        {
            token.ThrowIfCancellationRequested();
            if (slice.BodyOffset != cursor || slice.Bytes.Length > body.Length - cursor) return false;
            slice.Bytes.Span.CopyTo(body.AsSpan(checked((int)cursor))); cursor += slice.Bytes.Length;
        }
        if (cursor != body.Length) return false;
        extent = new(segs.Identity, offset + 16, body.Length); return true;
    }

    private static int FindTag(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> tag, uint version, CancellationToken token)
    {
        for (var offset = 0; offset <= bytes.Length - 12; offset++)
        {
            if ((offset & 4095) == 0) token.ThrowIfCancellationRequested();
            if (bytes.Slice(offset, 8).SequenceEqual(tag) && BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 8)..]) == version) return offset;
        }
        return -1;
    }

    private void PublishUpdate()
    {
        if (disposed || Interlocked.Exchange(ref updatePending, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(100, lifetime.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref updatePending, 0);
                if (disposed) return;
                if (updateContext is null) Updated?.Invoke(this, EventArgs.Empty);
                else updateContext.Post(_ => { if (!disposed) Updated?.Invoke(this, EventArgs.Empty); }, null);
            }
            catch (OperationCanceledException) { Interlocked.Exchange(ref updatePending, 0); }
        });
    }

    private void CatalogUpdated(object? sender, TextureCatalogSessionUpdatedEventArgs arguments)
    {
        lock (gate)
        {
            if (!disposed && !enrichmentCancellation.IsCancellationRequested)
            {
                enrichmentRequested = true;
                if (!enrichmentRunning)
                {
                    enrichmentRunning = true; enrichmentComplete = false;
                    enrichment = Task.Run(EnrichAsync);
                }
            }
        }
        PublishUpdate();
    }

    public void Cancel()
    {
        if (disposed) return;
        enrichmentCancellation.Cancel();
        lock (gate) blockers.Add("Texture discovery stopped");
        PublishUpdate();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true; catalogs.Updated -= CatalogUpdated; lifetime.Cancel();
        try { await enrichment.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await enrichmentGate.WaitAsync().ConfigureAwait(false); enrichmentGate.Release();
        await detailsGate.WaitAsync().ConfigureAwait(false); detailsGate.Release();
        features?.Dispose(); detailsGate.Dispose(); enrichmentGate.Dispose(); enrichmentCancellation.Dispose(); lifetime.Dispose();
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
