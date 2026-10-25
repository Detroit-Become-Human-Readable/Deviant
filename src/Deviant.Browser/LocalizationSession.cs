using Deviant.Archives;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Export.Localization;
using Deviant.Formats.Localization;
using Deviant.Formats.Localization.Model;

namespace Deviant.Browser;

/// <summary>Indexes a single immutable archive's localization rows lazily when the Strings view is opened.</summary>
public sealed partial class LocalizationSession : IAsyncDisposable
{
    private const int ResourceBatchSize = 32;
    private const int AssetBatchSize = FeatureStore.LocalizationGenerationBatchLimit;
    private static readonly string Version = LocalizationCacheContract.RecordStoreVersion;
    private static readonly string AssetIndexVersion = LocalizationCacheContract.SearchProjectionVersion;

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
    private readonly Dictionary<long,LocalizationInputDigest> inputDigests=[];
    private StagedLocalizationGeneration? stagedGeneration;
    private PublishedLocalizationGeneration<LocalizationGenerationHead>? publishedGeneration;
    private Task? disposeTask;
    private TaskCompletionSource? exportsDrained;
    private int activeExports;
    private int disposed;
    private long recordCount;
    private long completedResources;
    private long updateGeneration;
    private string? metadataContentId;
    private bool complete;
    private bool unavailable;
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
        using var timing = AssetLoadDiagnostics.Measure("LocalizationOpen");
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheLocation);
        cancellationToken.ThrowIfCancellationRequested();
        var resourceRows = snapshot.Records.Where(row => row.Type == 1016).OrderBy(row => row.Ordinal).ToArray();
        var featureStore = await Task.Run(() => FeatureStore.Open(snapshot, cacheLocation), cancellationToken).ConfigureAwait(false);
        var session = new LocalizationSession(snapshot, featureStore, new LocalizationSnapshotReader(sourceRevisionProvider), resourceRows,sourceRevisionProvider);
        session.enrichmentProgress = progress;
        try
        {
            var accepted = await Task.Run(() => featureStore.OpenLocalizationGeneration<LocalizationGenerationHead>(Version,
                AssetIndexVersion, HeadIdentity, head => AcceptSourceHead(head, snapshot, resourceRows), cancellationToken), cancellationToken).ConfigureAwait(false);
            if (accepted is not null)
            {
                LocalizationPayloadVerificationLease? verification = null;
                try
                {
                    var head = accepted.Head;
                    var reusable = snapshot.IndexStamp.Reliable && MetadataCacheSourcePolicy.CanReuseStampHead(snapshot, resourceRows) &&
                        SourceIdentity.SameStamp(head.IndexStamp, snapshot.IndexStamp) && MetadataCacheSourcePolicy.SameVolumes(head.SourceVolumes, snapshot.Volumes);
                    if (!reusable && head.InputDigestVersion == LocalizationCacheContract.InputDigestVersion)
                    {
                        var expected = head.InputDigests.Where(input => input.Status == LocalizationInputStatus.Read)
                            .ToDictionary(input => input.ResourceOrdinal, input => input.Sha256!);
                        var required = resourceRows.Where(row => row.Validity == ResourceValidity.Valid &&
                            row.StoredSize is > 0 and <= LocalizationSnapshotReader.MaximumResourceBytes &&
                            snapshot.Volumes.Any(volume => volume.Package == row.Package && volume.Stamp.Exists && volume.Stamp.Error is null)).ToArray();
                        verification = await new LocalizationPayloadVerifier().VerifyAsync(snapshot, expected, required,
                            sourceRevisionProvider, cancellationToken).ConfigureAwait(false);
                        reusable = verification.DigestsMatch;
                    }
                    if (reusable && HeadInputIdentityMatches(head, resourceRows))
                    {
                        verification?.EnsureUnchanged();
                        session.publishedGeneration = accepted;
                        session.ApplyHead(head, resourceRows);
                        if (accepted.SearchProjectionMatches)
                        {
                            lock (session.stateGate) { session.complete = true; session.phase = "Localization ready"; }
                            session.Publish(progress, "Loaded cached localization", null);
                            return session;
                        }
                        session.stagedGeneration = await Task.Run(() => featureStore.BeginLocalizationGeneration(
                            Version, AssetIndexVersion, cancellationToken), cancellationToken).ConfigureAwait(false);
                        if (session.stagedGeneration.BasePublishedGeneration != accepted.Generation)
                            throw new ArchiveException("LocalizationSuperseded", "Wait for localization indexing to finish.");
                        lock (session.stateGate) session.phase = "Updating localization search";
                        session.StartOwnedEnrichment(token => session.RebuildSearchProjectionAsync(head, accepted, progress, token));
                        session.Publish(progress, "Updating localization search", null);
                        return session;
                    }
                    accepted.Dispose();
                    session.publishedGeneration = null;
                }
                finally { if (verification is not null) await verification.DisposeAsync().ConfigureAwait(false); }
            }
            session.stagedGeneration = await Task.Run(() => featureStore.BeginLocalizationGeneration(
                Version, AssetIndexVersion, cancellationToken), cancellationToken).ConfigureAwait(false);
            session.StartOwnedEnrichment(token => session.EnrichAsync(progress, token));
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static LocalizationGenerationIdentity HeadIdentity(LocalizationGenerationHead head) => new(
        head.InputSnapshotId, head.ParsedResultId, head.RecordStoreVersion,
        head.SearchProjectionVersion, head.JsonPolicyVersion, head.RecordCount);

    private static bool AcceptSourceHead(LocalizationGenerationHead head, ArchiveSnapshot snapshot,
        IReadOnlyList<ResourceRecord> resources)
    {
        if (head.IndexStamp is null || head.SourceVolumes is null || head.SourceVolumes.Any(volume => volume is null || volume.Stamp is null) ||
            head.InputDigests is null || head.InputDigests.Any(input => input is null) || head.SourceCounts is null ||
            head.KindCounts is null || head.StatusCounts is null || head.VersionCounts is null ||
            head.DiagnosticCounts is null || head.Languages is null ||
            head.IndexContentId != snapshot.IndexContentId || head.PhysicalSourceStamp != snapshot.PhysicalSourceStamp ||
            head.ResourceCount != resources.Count || !SourceIdentity.SameStamp(head.IndexStamp, snapshot.IndexStamp) ||
            !MetadataCacheSourcePolicy.SameVolumes(head.SourceVolumes, snapshot.Volumes)) return false;
        return HeadInputIdentityMatches(head, resources);
    }

    private static bool HeadInputIdentityMatches(LocalizationGenerationHead head, IReadOnlyList<ResourceRecord> resources)
    {
        if (head.InputDigests is null || head.InputDigests.Any(input => input is null) ||
            head.RecordStoreVersion != Version || head.JsonPolicyVersion != LocalizationCacheContract.JsonPolicyVersion ||
            head.InputDigestVersion != LocalizationCacheContract.InputDigestVersion || head.InputDigests.Count != resources.Count ||
            head.InputDigests.Select(input => input.ResourceOrdinal).Order().SequenceEqual(resources.Select(row => row.Ordinal).Order()) is false)
            return false;
        try
        {
            var inputId = LocalizationCacheContract.InputSnapshotId(head.IndexContentId, head.InputDigests);
            return inputId == head.InputSnapshotId && LocalizationCacheContract.ParsedResultId(inputId) == head.ParsedResultId;
        }
        catch (ArgumentException) { return false; }
    }

    private void ApplyHead(LocalizationGenerationHead head, IReadOnlyList<ResourceRecord> resources)
    {
        lock (stateGate)
        {
            recordCount = head.RecordCount;
            completedResources = head.ResourceCount;
            foreach (var language in head.Languages) languages.Add(language);
            CopyCounters(head.KindCounts, kindCounts); CopyCounters(head.StatusCounts, statusCounts);
            CopyCounters(head.VersionCounts, versionCounts); CopyCounters(head.DiagnosticCounts, diagnosticCounts);
            Sources = BuildSourceChoices(snapshot.IndexContentId, resources, head.SourceCounts);
            foreach (var input in head.InputDigests) inputDigests[input.ResourceOrdinal] = input;
            metadataContentId = head.ParsedResultId;
        }
    }

    private static LocalizationInputDigest BuildInputDigest(ResourceRecord resource, string? sha256)
    {
        var status = sha256 is not null ? LocalizationInputStatus.Read :
            resource.Validity != ResourceValidity.Valid ? LocalizationInputStatus.InvalidResource :
            resource.StoredSize <= 0 ? LocalizationInputStatus.Empty :
            resource.StoredSize > LocalizationSnapshotReader.MaximumResourceBytes ? LocalizationInputStatus.SizeLimit :
            LocalizationInputStatus.SourceUnavailable;
        return new(resource.Ordinal, status, sha256);
    }

    private async Task RebuildSearchProjectionAsync(LocalizationGenerationHead priorHead,
        PublishedLocalizationGeneration<LocalizationGenerationHead> priorGeneration,
        IProgress<LocalizationReadProgress>? progress, CancellationToken token)
    {
        try
        {
            SetProgress("Updating localization search");
            long cursor = -1;
            var visited = 0L;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var page = await Task.Run(() => featureStore.LoadLocalizationRecordPage<LocalizationGenerationHead, LocalizationRecord>(
                    priorGeneration, cursor, AssetBatchSize, token), token).ConfigureAwait(false);
                if (page.Items.Count == 0) break;
                var batch = new LocalizationGenerationRow<LocalizationRecord>[page.Items.Count];
                for (var index = 0; index < page.Items.Count; index++)
                {
                    var item = page.Items[index];
                    batch[index] = new(item.IdentityKey, item.Record, Describe(item.Record));
                }
                await AppendGenerationBatchAsync(batch, token).ConfigureAwait(false);
                visited += page.Items.Count;
                cursor = page.NextSequence;
                progress?.Report(new("Updating localization search", visited, RecordCount, RecordCount));
                Publish(progress, "Updating localization search", null);
                if (!page.HasMore) break;
            }
            var updatedHead = priorHead with { SearchProjectionVersion = AssetIndexVersion };
            await PublishGenerationAsync(updatedHead, token).ConfigureAwait(false);
            Publish(progress, "Localization ready", null);
        }
        catch (Exception error)
        {
            if (IsProducerCancellation(error, token))
            {
                SetProgress("Cancelled");
            }
            else
            {
                lock (stateGate) unavailable = true;
                SetProgress("Localization search update failed");
                await DiscardStagedGenerationAsync().ConfigureAwait(false);
            }
            Publish(progress, Status, null);
            throw;
        }
    }

    private async Task AppendGenerationBatchAsync(IReadOnlyList<LocalizationGenerationRow<LocalizationRecord>> rows,
        CancellationToken token)
    {
        if (rows.Count == 0) return;
        await featureGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var writer = stagedGeneration ?? throw new InvalidOperationException("The localization generation is not active.");
            await Task.Run(() => featureStore.AppendLocalizationGeneration(writer, rows, token), token).ConfigureAwait(false);
        }
        finally { featureGate.Release(); }
    }

    private async Task PublishGenerationAsync(LocalizationGenerationHead head, CancellationToken token)
    {
        await featureGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var writer = stagedGeneration ?? throw new InvalidOperationException("The localization generation is not active.");
            var accepted = await Task.Run(() => featureStore.PublishLocalizationGeneration(writer, head, HeadIdentity, token), token).ConfigureAwait(false);
            stagedGeneration = null;
            var previous = publishedGeneration;
            publishedGeneration = accepted;
            previous?.Dispose();
            lock (stateGate)
            {
                Sources = BuildSourceChoices(snapshot.IndexContentId, Resources, head.SourceCounts);
                metadataContentId = head.ParsedResultId;
                complete = true;
                phase = "Localization ready";
            }
        }
        finally { featureGate.Release(); }
    }

    public async Task<LocalizationPage> QueryAsync(LocalizationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfDisposed();
        ValidateQuery(query);
        var assetQuery = ToAssetQuery(query);
        await featureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await QueryUnderFeatureGateAsync(assetQuery, cancellationToken).ConfigureAwait(false); }
        finally { featureGate.Release(); }
    }

    internal async Task<bool> HasPublishedKindOnePointerAsync(ResourceIdentity source, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        var query = new LocalizationQuery(Source: source, Kind: LocalizationRecordKind.Pointer, Limit: 128);
        ValidateQuery(query);
        using var timing = AssetLoadDiagnostics.Measure("LocalizationPointerExistence", source.Key);
        await featureGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            long offset = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var page = await QueryUnderFeatureGateAsync(ToAssetQuery(query with { Offset = offset }), token).ConfigureAwait(false);
                if (page.Records.Any(record => record.PointerKind == 1 && record.PointerPayloadBase64 is not null)) return true;
                offset += page.Records.Count;
                if (page.Records.Count == 0 || offset >= page.Total) return false;
            }
        }
        finally { featureGate.Release(); }
    }

    internal async Task<IReadOnlyList<ResourceIdentity>> CapturePublishedPointerIdentitiesAsync(ResourceIdentity source,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        var query = new LocalizationQuery(Source: source, Kind: LocalizationRecordKind.Pointer,
            Limit: LocalizationAudioLookup.PointerPageSize);
        ValidateQuery(query);
        var identities = new List<ResourceIdentity>();
        using var timing = AssetLoadDiagnostics.Measure("LocalizationPointerCapture", source.Key);
        await featureGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            long offset = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var page = await QueryUnderFeatureGateAsync(ToAssetQuery(query with { Offset = offset }), token).ConfigureAwait(false);
                identities.AddRange(page.Records.Where(record => record.PointerKind == 1 && record.PointerPayloadBase64 is not null)
                    .Select(record => record.Identity));
                offset += page.Records.Count;
                if (page.Records.Count == 0 || offset >= page.Total) break;
            }
        }
        finally { featureGate.Release(); }
        return identities.AsReadOnly();
    }

    private async Task<LocalizationPage> QueryUnderFeatureGateAsync(AssetQuery assetQuery, CancellationToken token)
    {
        lock (stateGate)
            if (unavailable) throw new ArchiveException("LocalizationUnavailable", "Localization indexing did not finish.");
        LocalizationGenerationQueryPage<LocalizationRecord> page;
        if (stagedGeneration is { IsActive: true } staged)
            page = await Task.Run(() => featureStore.QueryStagedLocalizationPage<LocalizationRecord>(staged, assetQuery, token), token).ConfigureAwait(false);
        else if (publishedGeneration is { IsActive: true } published && published.SearchProjectionMatches)
            page = await Task.Run(() => featureStore.QueryLocalizationPage<LocalizationGenerationHead, LocalizationRecord>(published, assetQuery, token), token).ConfigureAwait(false);
        else
        {
            lock (stateGate)
                if (unavailable) throw new ArchiveException("LocalizationUnavailable", "Localization indexing did not finish.");
            return new(0, Array.Empty<LocalizationRecord>());
        }
        lock (stateGate)
            if (unavailable) throw new ArchiveException("LocalizationUnavailable", "Localization indexing did not finish.");
        return new(page.Total, page.Records);
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
            lock (stateGate)
                if (unavailable) throw new ArchiveException("LocalizationUnavailable", "Localization indexing did not finish.");
            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = stagedGeneration is { IsActive: true } staged
                    ? await Task.Run(() => featureStore.LoadStagedLocalizationRecord<LocalizationRecord>(staged, key, cancellationToken), cancellationToken).ConfigureAwait(false)
                    : publishedGeneration is { IsActive: true } published
                        ? await Task.Run(() => featureStore.LoadLocalizationRecord<LocalizationGenerationHead, LocalizationRecord>(published, key, cancellationToken), cancellationToken).ConfigureAwait(false)
                        : null;
                if (row is not null) records.Add(row);
            }
            lock (stateGate)
                if (unavailable) throw new ArchiveException("LocalizationUnavailable", "Localization indexing did not finish.");
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
        PublishedLocalizationGeneration<LocalizationGenerationHead> generation;
        lock (stateGate)
        {
            ThrowIfDisposed();
            if (!complete) throw new ArchiveException("LocalizationPending", "Wait for localization indexing to finish before exporting.");
            generation = publishedGeneration is { IsActive: true, SearchProjectionMatches: true } current
                ? current : throw new ArchiveException("LocalizationPending", "Wait for localization indexing to finish before exporting.");
            if (activeExports++ == 0) exportsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return ExportPinnedGenerationAsync(generation, selection, format, destination, progress, cancellationToken);
    }

    private async Task<LocalizationExportReport> ExportPinnedGenerationAsync(
        PublishedLocalizationGeneration<LocalizationGenerationHead> generation, LocalizationExportSelection selection,
        LocalizationExportFormat format, string destination, IProgress<LocalizationReadProgress>? progress, CancellationToken token)
    {
        try
        {
            return await new LocalizationExportService(snapshot, featureStore, generation, sourceRevisions)
                .ExportAsync(selection, format, destination, progress, token).ConfigureAwait(false);
        }
        finally
        {
            lock (stateGate)
            {
                if (--activeExports == 0) exportsDrained?.TrySetResult();
            }
        }
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
                lock (stateGate) inputDigests[resource.Ordinal] = BuildInputDigest(resource, parsed.InputSha256);
                foreach (var record in parsed.Records)
                {
                    rows.Add(record);
                    descriptors.Add(Describe(record));
                    if (!string.IsNullOrWhiteSpace(record.Language))
                        lock (stateGate) languages.Add(record.Language);
                    Interlocked.Increment(ref recordCount);
                    if (rows.Count == AssetBatchSize)
                    {
                        await FlushBatchAsync(rows, descriptors, token).ConfigureAwait(false);
                        rows.Clear(); descriptors.Clear();
                    }
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
                if (completedResources % ResourceBatchSize == 0)
                {
                    if (rows.Count > 0)
                    {
                        await FlushBatchAsync(rows, descriptors, token).ConfigureAwait(false);
                        rows.Clear(); descriptors.Clear();
                    }
                    lock (stateGate) Sources = BuildSourceChoices(snapshot.IndexContentId, Resources, sourceCounts);
                    Publish(progress, "Indexed localization", parsed.RelativeSource);
                }
            }
            if (rows.Count > 0) await FlushBatchAsync(rows, descriptors, token).ConfigureAwait(false);
            var summary = Summary;
            var inputList = Resources.Select(resource => inputDigests[resource.Ordinal]).OrderBy(input => input.ResourceOrdinal).ToArray();
            var inputSnapshotId = LocalizationCacheContract.InputSnapshotId(snapshot.IndexContentId, inputList);
            var parsedResultId = LocalizationCacheContract.ParsedResultId(inputSnapshotId);
            var head = new LocalizationGenerationHead(snapshot.IndexContentId, snapshot.PhysicalSourceStamp,
                snapshot.IndexStamp, snapshot.Volumes, inputSnapshotId, parsedResultId, Version, AssetIndexVersion,
                LocalizationCacheContract.JsonPolicyVersion, LocalizationCacheContract.InputDigestVersion,
                Resources.Count, RecordCount, inputList, new Dictionary<string, long>(sourceCounts, StringComparer.Ordinal),
                new Dictionary<string, long>(summary.KindCounts), new Dictionary<string, long>(summary.StatusCounts),
                new Dictionary<string, long>(summary.VersionCounts), new Dictionary<string, long>(summary.DiagnosticCounts),
                Languages.ToArray());
            await PublishGenerationAsync(head, token).ConfigureAwait(false);
            Publish(progress, "Localization ready", null);
        }
        catch (Exception error)
        {
            if (IsProducerCancellation(error, token))
            {
                lock (stateGate) Sources = BuildSourceChoices(snapshot.IndexContentId, Resources, sourceCounts);
                SetProgress("Cancelled");
            }
            else
            {
                lock (stateGate) unavailable = true;
                SetProgress("Localization indexing failed");
                await DiscardStagedGenerationAsync().ConfigureAwait(false);
            }
            Publish(progress, Status, null);
            throw;
        }
    }

    private async Task FlushBatchAsync(IReadOnlyList<LocalizationRecord> rows, IReadOnlyList<AssetDescriptor> descriptors,
        CancellationToken token)
    {
        if (rows.Count == 0) return;
        if (rows.Count != descriptors.Count) throw new InvalidDataException("Localization search descriptors do not match the record batch.");
        var offset = 0;
        while (offset < rows.Count)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(AssetBatchSize, rows.Count - offset);
            var batch = new LocalizationGenerationRow<LocalizationRecord>[count];
            for (var index = 0; index < count; index++)
            {
                var record = rows[offset + index];
                batch[index] = new(record.Identity.Key, record, descriptors[offset + index]);
            }
            await AppendGenerationBatchAsync(batch, token).ConfigureAwait(false);
            offset += count;
        }
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
        await enrichmentLifecycleGate.WaitAsync().ConfigureAwait(false);
        Task producer;
        Task? retry;
        lock (stateGate) { producer = EnrichmentTask; retry = retryCoordinatorTask; }
        enrichmentLifecycleGate.Release();
        try { await producer.ConfigureAwait(false); }
        catch { /* Preserve disposal even if a cancelled/failed enrichment task faulted. */ }
        if (retry is not null && !ReferenceEquals(retry, producer))
        {
            try { await retry.ConfigureAwait(false); }
            catch { /* Preserve disposal while a retry request drains. */ }
        }
        Task pendingExports;
        lock (stateGate) pendingExports = activeExports == 0 ? Task.CompletedTask : exportsDrained!.Task;
        await pendingExports.ConfigureAwait(false);
        await featureGate.WaitAsync().ConfigureAwait(false);
        try
        {
            stagedGeneration?.Dispose(); stagedGeneration = null;
            publishedGeneration?.Dispose(); publishedGeneration = null;
            await Task.Run(featureStore.Dispose).ConfigureAwait(false);
        }
        finally { featureGate.Release(); lifetime.Dispose(); }
    }

    private static void AddCount(Dictionary<string, long> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
    private static void CopyCounters(IReadOnlyDictionary<string, long> source, Dictionary<string, long> destination)
    { foreach (var item in source) destination[item.Key] = item.Value; }

}
