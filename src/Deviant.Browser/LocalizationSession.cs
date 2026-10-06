using Deviant.Archives;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Export.Localization;
using Deviant.Formats.Localization;
using Deviant.Formats.Localization.Model;

namespace Deviant.Browser;

/// <summary>Indexes a single immutable archive's localization rows lazily when the Strings view is opened.</summary>
public sealed class LocalizationSession : IAsyncDisposable
{
    private const string ReadyHeadKey = "localization.ready";
    private const int ResourceBatchSize = 32;
    private const int AssetBatchSize = 512;
    private static readonly string Version = LocalizationSchema.CacheVersion;
    private static readonly string AssetIndexVersion = LocalizationSchema.AssetIndexVersion;

    private readonly ArchiveSnapshot snapshot;
    private readonly FeatureStore featureStore;
    private readonly LocalizationSnapshotReader reader;
    private readonly ISourceRevisionProvider? sourceRevisions;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim featureGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly Dictionary<string, long> sourceCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> languages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> kindCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> statusCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> versionCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> diagnosticCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<long,string> inputDigests=[];
    private Task? disposeTask;
    private int disposed;
    private long recordCount;
    private long completedResources;
    private long updateGeneration;
    private string? metadataContentId;
    private bool complete;
    private string phase = "Ready";

    private LocalizationSession(ArchiveSnapshot snapshot, FeatureStore featureStore, LocalizationSnapshotReader reader,
        IReadOnlyList<ResourceRecord> resources,ISourceRevisionProvider? sourceRevisions=null)
    {
        this.snapshot = snapshot;
        this.featureStore = featureStore;
        this.reader = reader;
        this.sourceRevisions=sourceRevisions;
        Resources = resources;
        Sources = BuildSourceChoices(snapshot.IndexContentId, resources, new Dictionary<string, long>(StringComparer.Ordinal));
    }

    public ArchiveSnapshot Snapshot => snapshot;
    public IReadOnlyList<ResourceRecord> Resources { get; }
    public IReadOnlyList<LocalizationSourceChoice> Sources { get; private set; } = [];
    public IReadOnlyList<string> Languages { get { lock (stateGate) return languages.Order(StringComparer.OrdinalIgnoreCase).ToArray(); } }
    public LocalizationIndexSummary Summary
    {
        get { lock (stateGate) return new(new Dictionary<string, long>(kindCounts), new Dictionary<string, long>(statusCounts),
            new Dictionary<string, long>(versionCounts), new Dictionary<string, long>(diagnosticCounts)); }
    }
    public long RecordCount => Interlocked.Read(ref recordCount);
    public string? MetadataContentId{get{lock(stateGate)return metadataContentId;}}
    public long CompletedResources => Interlocked.Read(ref completedResources);
    public bool IsEnrichmentComplete { get { lock (stateGate) return complete; } }
    public string Status { get { lock (stateGate) return phase; } }
    public Task EnrichmentTask { get; private set; } = Task.CompletedTask;
    public event EventHandler<LocalizationSessionUpdate>? Updated;

    public static async Task<LocalizationSession> OpenAsync(ArchiveSnapshot snapshot, string cacheLocation,
        IProgress<LocalizationReadProgress>? progress = null, CancellationToken cancellationToken = default,
        ISourceRevisionProvider? sourceRevisionProvider = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheLocation);
        cancellationToken.ThrowIfCancellationRequested();
        var resourceRows = snapshot.Records.Where(row => row.Type == 1016).OrderBy(row => row.Ordinal).ToArray();
        var featureStore = await Task.Run(() => FeatureStore.Open(snapshot, cacheLocation), cancellationToken).ConfigureAwait(false);
        var session = new LocalizationSession(snapshot, featureStore, new LocalizationSnapshotReader(sourceRevisionProvider), resourceRows,sourceRevisionProvider);
        try
        {
            var head = await Task.Run(() => featureStore.Load<LocalizationCacheHead>(ReadyHeadKey, AssetIndexVersion, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (head is not null && head.IndexContentId == snapshot.IndexContentId &&
                head.PhysicalSourceStamp == snapshot.PhysicalSourceStamp && head.ResourceCount == resourceRows.Length)
            {
                LocalizationPayloadVerificationLease? lease=null;
                try
                {
                var reusable=MetadataCacheSourcePolicy.CanReuseStampHead(snapshot,resourceRows);
                if(!reusable&&head.InputDigests is not null&&head.DigestVersion=="localization-input-sha256-v1")
                {
                    var required=resourceRows.Where(row=>row.Validity==ResourceValidity.Valid&&row.StoredSize is >0 and <=LocalizationSnapshotReader.MaximumResourceBytes&&snapshot.Volumes.Any(volume=>volume.Package==row.Package&&volume.Stamp.Exists&&volume.Stamp.Error is null)).ToArray();
                    lease=await new LocalizationPayloadVerifier().VerifyAsync(snapshot,head.InputDigests,required,sourceRevisionProvider,cancellationToken).ConfigureAwait(false);
                    reusable=lease.DigestsMatch;
                }
                var count = await Task.Run(() => featureStore.Count(new AssetQuery(Family: LocalizationSchema.AssetFamily), cancellationToken), cancellationToken).ConfigureAwait(false);
                if (reusable&&count == head.RecordCount)
                {
                    lease?.EnsureUnchanged();
                    lock (session.stateGate)
                    {
                        session.complete = true;
                        session.phase = "Localization ready";
                        session.recordCount = head.RecordCount;
                        session.completedResources = head.ResourceCount;
                        foreach (var language in head.Languages) session.languages.Add(language);
                        CopyCounters(head.KindCounts, session.kindCounts); CopyCounters(head.StatusCounts, session.statusCounts);
                        CopyCounters(head.VersionCounts, session.versionCounts); CopyCounters(head.DiagnosticCounts, session.diagnosticCounts);
                        session.Sources = BuildSourceChoices(snapshot.IndexContentId, resourceRows, head.SourceCounts);
                        if(head.InputDigests is not null)foreach(var pair in head.InputDigests)session.inputDigests[pair.Key]=pair.Value;
                        session.metadataContentId=head.MetadataContentId;
                    }
                    session.Publish(progress, "Loaded cached localization", null);
                    return session;
                }
                }
                finally{if(lease is not null)await lease.DisposeAsync().ConfigureAwait(false);}
            }
            await Task.Run(() => featureStore.SaveItems<LocalizationRecord>(LocalizationSchema.ItemCollection, Version,
                Array.Empty<LocalizationRecord>(), cancellationToken), cancellationToken).ConfigureAwait(false);
            await Task.Run(() => featureStore.ReplaceAssets(LocalizationSchema.AssetFamily, [], cancellationToken), cancellationToken).ConfigureAwait(false);
            session.EnrichmentTask = session.EnrichAsync(progress, session.lifetime.Token);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<LocalizationPage> QueryAsync(LocalizationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfDisposed();
        ValidateQuery(query);
        var assetQuery = ToAssetQuery(query);
        await featureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var total = await Task.Run(() => featureStore.Count(assetQuery, cancellationToken), cancellationToken).ConfigureAwait(false);
            var keys = await Task.Run(() => featureStore.QueryIdentityKeys(assetQuery, cancellationToken), cancellationToken).ConfigureAwait(false);
            var rows = await Task.Run(() =>
            {
                var records=new List<LocalizationRecord>(keys.Count);
                foreach(var key in keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var record=featureStore.LoadItem<LocalizationRecord>(LocalizationSchema.ItemCollection,key,Version,cancellationToken);
                    if(record is not null)records.Add(record);
                }
                return records;
            },cancellationToken).ConfigureAwait(false);
            return new(total, rows.AsReadOnly());
        }
        finally { featureGate.Release(); }
    }

    public async Task<IReadOnlyList<LocalizationRecord>> GetRecordsAsync(IEnumerable<ResourceIdentity> identities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ThrowIfDisposed();
        var keys = identities.Where(identity => identity.IndexContentId == snapshot.IndexContentId)
            .Select(identity => identity.Key).Distinct(StringComparer.Ordinal).ToArray();
        var records = new List<LocalizationRecord>(keys.Length);
        await featureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = await Task.Run(() => featureStore.LoadItem<LocalizationRecord>(LocalizationSchema.ItemCollection,
                    key, Version, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (row is not null) records.Add(row);
            }
        }
        finally { featureGate.Release(); }
        return records.AsReadOnly();
    }

    /// <summary>Returns exact-key occurrences across languages in the same type-1016 resource only.</summary>
    public async Task<IReadOnlyList<LocalizationRecord>> GetVariantsAsync(ResourceIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var selected = await GetRecordsAsync([identity], cancellationToken).ConfigureAwait(false);
        if (selected.Count == 0 || selected[0].Key is null) return selected;
        var source = selected[0].ResourceIdentity;
        var key = selected[0].Key;
        var offset = 0L;
        var variants = new List<LocalizationRecord>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await QueryAsync(new LocalizationQuery(Source: source, Kind: LocalizationRecordKind.Text,
                Offset: offset, Limit: 10_000, ExactKey: key), cancellationToken).ConfigureAwait(false);
            variants.AddRange(page.Records);
            offset += page.Records.Count;
            if (offset >= page.Total || page.Records.Count == 0) break;
        }
        return variants.AsReadOnly();
    }

    /// <summary>Returns exact-key occurrences in other type-1016 resources in this source snapshot.</summary>
    public async Task<IReadOnlyList<LocalizationRecord>> GetOtherOccurrencesAsync(ResourceIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var selected = await GetRecordsAsync([identity], cancellationToken).ConfigureAwait(false);
        if (selected.Count == 0 || string.IsNullOrEmpty(selected[0].Key)) return Array.Empty<LocalizationRecord>();

        var source = selected[0].ResourceIdentity;
        var key = selected[0].Key;
        var offset = 0L;
        var occurrences = new List<LocalizationRecord>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await QueryAsync(new LocalizationQuery(Kind: LocalizationRecordKind.Text,
                Offset: offset, Limit: 10_000, ExactKey: key), cancellationToken).ConfigureAwait(false);
            occurrences.AddRange(page.Records.Where(record => record.ResourceIdentity.Ordinal != source.Ordinal));
            offset += page.Records.Count;
            if (offset >= page.Total || page.Records.Count == 0) break;
        }
        return occurrences.AsReadOnly();
    }

    public Task<LocalizationExportReport> ExportAsync(LocalizationExportSelection selection,
        LocalizationExportFormat format, string destination, IProgress<LocalizationReadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ThrowIfDisposed();
        if (!IsEnrichmentComplete)
            throw new ArchiveException("LocalizationPending", "Localization export is available after the current source snapshot finishes indexing.");
        return new LocalizationExportService(snapshot, featureStore,sourceRevisions).ExportAsync(selection, format, destination, progress, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        lock (stateGate)
        {
            disposeTask ??= DisposeCoreAsync();
            return new ValueTask(disposeTask);
        }
    }

    private async Task EnrichAsync(IProgress<LocalizationReadProgress>? progress, CancellationToken token)
    {
        var rows = new List<LocalizationRecord>(AssetBatchSize);
        var descriptors = new List<AssetDescriptor>(AssetBatchSize);
        SetProgress("Reading localization");
        try
        {
            foreach (var resource in Resources)
            {
                token.ThrowIfCancellationRequested();
                var parsed = await reader.ReadResourceAsync(snapshot, resource, token).ConfigureAwait(false);
                if(parsed.InputSha256 is not null)inputDigests[resource.Ordinal]=parsed.InputSha256;
                rows.AddRange(parsed.Records);
                foreach (var record in parsed.Records)
                {
                    descriptors.Add(Describe(record));
                    if (!string.IsNullOrWhiteSpace(record.Language))
                        lock (stateGate) languages.Add(record.Language);
                    Interlocked.Increment(ref recordCount);
                }
                lock (stateGate) sourceCounts[parsed.ResourceIdentity.Key] = parsed.Records.Count;
                lock (stateGate)
                {
                    AddCount(versionCounts, parsed.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    foreach (var diagnostic in parsed.Diagnostics) AddCount(diagnosticCounts, diagnostic.Code);
                    foreach (var record in parsed.Records)
                    {
                        AddCount(kindCounts, record.Kind.ToString());
                        AddCount(statusCounts, record.Status.ToString());
                    }
                }
                Interlocked.Increment(ref completedResources);
                progress?.Report(new("Reading localization", completedResources, Resources.Count, RecordCount, parsed.RelativeSource));
                if (rows.Count >= AssetBatchSize || completedResources % ResourceBatchSize == 0)
                {
                    await FlushBatchAsync(rows, descriptors, token).ConfigureAwait(false);
                    rows.Clear(); descriptors.Clear();
                    lock (stateGate) Sources = BuildSourceChoices(snapshot.IndexContentId, Resources, sourceCounts);
                    Publish(progress, "Indexed localization", parsed.RelativeSource);
                }
            }
            if (rows.Count > 0) await FlushBatchAsync(rows, descriptors, token).ConfigureAwait(false);
            lock (stateGate)
            {
                Sources = BuildSourceChoices(snapshot.IndexContentId, Resources, sourceCounts);
                complete = true;
                phase = "Localization ready";
                metadataContentId=SourceIdentity.HashFields("deviant-localization-inputs-v1",new[]{snapshot.IndexContentId,Version}.Concat(inputDigests.OrderBy(pair=>pair.Key).Select(pair=>pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)+":"+pair.Value)));
            }
            var summary = Summary;
            var head = new LocalizationCacheHead(snapshot.IndexContentId, snapshot.PhysicalSourceStamp,
                Resources.Count, RecordCount, Languages.ToArray(), new Dictionary<string, long>(sourceCounts, StringComparer.Ordinal),
                new(summary.KindCounts), new(summary.StatusCounts), new(summary.VersionCounts), new(summary.DiagnosticCounts),new(inputDigests),"localization-input-sha256-v1",metadataContentId);
            await Task.Run(() => featureStore.Save(ReadyHeadKey, AssetIndexVersion, head, token), token).ConfigureAwait(false);
            Publish(progress, "Localization ready", null);
        }
        catch
        {
            SetProgress(token.IsCancellationRequested ? "Cancelled" : "Localization indexing failed");
            Publish(progress, Status, null);
            throw;
        }
    }

    private async Task FlushBatchAsync(IReadOnlyList<LocalizationRecord> rows, IReadOnlyList<AssetDescriptor> descriptors,
        CancellationToken token)
    {
        if (rows.Count == 0) return;
        await featureGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var items = rows.Select(row => new KeyValuePair<string, LocalizationRecord>(row.Identity.Key, row)).ToArray();
            await Task.Run(() => featureStore.UpsertItems(LocalizationSchema.ItemCollection, Version, items, token), token).ConfigureAwait(false);
            await Task.Run(() => featureStore.UpsertAssets(descriptors, token), token).ConfigureAwait(false);
        }
        finally { featureGate.Release(); }
    }

    private AssetDescriptor Describe(LocalizationRecord record)
    {
        var names = new List<NameEvidence>();
        if (!string.IsNullOrEmpty(record.Key)) names.Add(new(record.Key, NameOrigin.Stored, "Localization key"));
        if (!string.IsNullOrEmpty(record.RawLanguageCode)) names.Add(new(record.RawLanguageCode, NameOrigin.Stored, "Raw language code"));
        if (!string.IsNullOrEmpty(record.Diagnostic)) names.Add(new(record.Diagnostic, NameOrigin.Description, "Parser diagnostic"));
        foreach (var field in record.UnknownFields) names.Add(new($"{field.Key}={field.Value}", NameOrigin.Description, "Localization field"));
        var extents = new[] { new SourceExtent(record.RecordExtent.RelativeSource, record.RecordExtent.Offset, record.RecordExtent.Length) };
        var availability = record.Status switch
        {
            LocalizationRecordStatus.Unresolved => AssetAvailability.Missing,
            LocalizationRecordStatus.Malformed => AssetAvailability.Malformed,
            LocalizationRecordStatus.Unsupported => AssetAvailability.Unsupported,
            _ => AssetAvailability.Available
        };
        var completeness = record.Status == LocalizationRecordStatus.Complete ? AssetCompleteness.Complete : AssetCompleteness.Unverified;
        var label = !string.IsNullOrEmpty(record.Key) ? record.Key : record.Kind switch
        {
            LocalizationRecordKind.Pointer => "Unresolved localization pointer",
            _ => "Opaque localization record"
        };
        var sortKey = string.Join('\0', NaturalOrder.Key(record.Language), NaturalOrder.Key(record.Key ?? ""), record.ResourceOrdinal.ToString("D20", System.Globalization.CultureInfo.InvariantCulture),
            record.RecordOffset.ToString("D20", System.Globalization.CultureInfo.InvariantCulture), record.KeyIndex.ToString("D8", System.Globalization.CultureInfo.InvariantCulture));
        return new AssetDescriptor(record.Identity, new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp),
            LocalizationSchema.AssetFamily, label, record.ResourceIdentity.Key, availability, completeness, AssetCapabilities.None,
            names, extents, record.Language, record.Kind.ToString(), Reason: record.Diagnostic ?? (record.Status == LocalizationRecordStatus.Unresolved ? "Pointer target unresolved." : null),
            SortKey: sortKey)
        {
            SearchTerms = BuildSearchTerms(record),
            LookupKey = record.Key
        };
    }

    private static string? BuildSearchTerms(LocalizationRecord record)
    {
        var builder = new System.Text.StringBuilder();
        AppendSearchTerm(builder, record.Key);
        AppendSearchTerm(builder, record.Text);
        AppendSearchTerm(builder, record.RawLanguageCode);
        AppendSearchTerm(builder, record.Language);
        AppendSearchTerm(builder, record.Diagnostic);
        return builder.Length == 0 ? null : builder.ToString();
    }

    private static void AppendSearchTerm(System.Text.StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (builder.Length > 0) builder.Append('\n');
        builder.Append(value);
    }

    private static AssetQuery ToAssetQuery(LocalizationQuery query) => new(
        Search: string.IsNullOrWhiteSpace(query.Search) ? null : query.Search,
        GroupKey: query.Source?.Key,
        Language: query.Language,
        Codec: query.Kind?.ToString(),
        Availability: query.Availability,
        Offset: query.Offset,
        Limit: query.Limit,
        Family: LocalizationSchema.AssetFamily,
        ExactKey: query.ExactKey,Sort:query.Sort,Descending:query.Descending);

    private void ValidateQuery(LocalizationQuery query)
    {
        if (query.Offset < 0 || query.Limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(query));
        if (query.Source is { } source && (source.IndexContentId != snapshot.IndexContentId || source.MemberPath is not null))
            throw new ArchiveException("InvalidSourceFilter", "The localization source filter belongs to another revision or is not a top-level resource.");
    }

    private static IReadOnlyList<LocalizationSourceChoice> BuildSourceChoices(string indexContentId, IReadOnlyList<ResourceRecord> resources,
        IReadOnlyDictionary<string, long> counts)
    {
        var choices = new List<LocalizationSourceChoice>(resources.Count);
        for (var index = 0; index < resources.Count; index++)
        {
            var resource = resources[index];
            var identity = new ResourceIdentity(indexContentId, resource.Ordinal);
            var label = $"Localization resource {index + 1}";
            choices.Add(new(identity, label, resource.VolumeName, resource.Package, counts.GetValueOrDefault(identity.Key)));
        }
        return choices.AsReadOnly();
    }

    private void SetProgress(string value)
    { lock (stateGate) phase = value; }

    private void Publish(IProgress<LocalizationReadProgress>? progress, string currentPhase, string? detail)
    {
        LocalizationSessionUpdate update;
        lock (stateGate)
            update = new(Interlocked.Increment(ref updateGeneration), complete, Resources.Count, CompletedResources, RecordCount, currentPhase, detail);
        try { progress?.Report(new(currentPhase, update.CompletedResources, update.TotalResources, update.RecordCount, detail)); }
        catch { /* Progress observers cannot break the source snapshot index. */ }
        try { Updated?.Invoke(this, update); }
        catch { /* UI observers cannot break the source snapshot index. */ }
    }

    private void ThrowIfDisposed()
    { if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(LocalizationSession)); }

    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await lifetime.CancelAsync().ConfigureAwait(false);
        try { await EnrichmentTask.ConfigureAwait(false); }
        catch { /* Preserve disposal even if a cancelled/failed enrichment task faulted. */ }
        await featureGate.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(featureStore.Dispose).ConfigureAwait(false); }
        finally { featureGate.Release(); lifetime.Dispose(); }
    }

    private static void AddCount(Dictionary<string, long> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
    private static void CopyCounters(Dictionary<string, long> source, Dictionary<string, long> destination)
    { foreach (var item in source) destination[item.Key] = item.Value; }

    private sealed record LocalizationCacheHead(string IndexContentId, string PhysicalSourceStamp,
        int ResourceCount, long RecordCount, string[] Languages, Dictionary<string, long> SourceCounts,
        Dictionary<string, long> KindCounts, Dictionary<string, long> StatusCounts,
        Dictionary<string, long> VersionCounts, Dictionary<string, long> DiagnosticCounts,
        Dictionary<long,string>? InputDigests=null,string? DigestVersion=null,string? MetadataContentId=null);
}
