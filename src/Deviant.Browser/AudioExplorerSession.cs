using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Audio;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;
using NameEvidence = Deviant.Formats.Audio.Model.NameEvidence;
using Deviant.Export.Audio;
using AudioNameEvidence = Deviant.Formats.Audio.Model.NameEvidence;

namespace Deviant.Browser;

/// <summary>Owns one immutable archive revision and its cached audio metadata feature set.</summary>
public sealed partial class AudioExplorerSession : IAsyncDisposable
{
    public const string AssetFamily = "audio.media";
    private const string SnapshotFeature = "audio.snapshot.ready";
    private const string SnapshotVersion = "audio-snapshot-m1.4-v3-midi";
    private const string AssetIndexVersion = "audio-asset-index-v8-browse";
    private const string PreviewVersion = "audio-preview-m1-v1";
    private const long MaximumPreviewSourceBytes = 512L * 1024 * 1024;

    private readonly CatalogSession archiveSession;
    private readonly FeatureStore featureStore;
    private readonly AliasStore aliases;
    private readonly CacheManager cache;
    private readonly AudioService audioService;
    private readonly AudioToolPaths audioTools;
    private Lazy<Task>? audioInitialization;
    private bool cachedNamesReady;
    private readonly Dictionary<AudioToolUse, Lazy<Task<string>>> toolFingerprints = [];
    private readonly Dictionary<string, MediaEntry> mediaByIdentity;
    private readonly Dictionary<string, string> mediaFingerprints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> bankLabelsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> labelsByIdentity = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Bank, uint Id), SortedDictionary<(long Ordinal, string MemberPath), MediaEntry>> fallbackLabelGroups = [];
    private readonly Dictionary<string, ((string Bank, uint Id) Group, (long Ordinal, string MemberPath) Order)> fallbackLabelMembership = new(StringComparer.Ordinal);
    private readonly object catalogGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationTokenSource metadataCancellation;
    private readonly SemaphoreSlim featureGate = new(1, 1);
    private readonly object disposeGate = new();
    private Task? disposeTask;
    private Lazy<Task>? enrichmentTask;
    private int disposed;
    private int enrichmentComplete;
    private long updateGeneration;
    private long enrichmentProgressCompleted;
    private long enrichmentProgressTotal;
    private AudioExplorerProgress currentProgress = new("Audio indexing", 0, 0);
    private readonly Dictionary<string, long> stagedGroupCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> stagedLanguages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> stagedCodecs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> stagedCategories = new(StringComparer.Ordinal);
    private readonly HashSet<int> stagedChannels = [];
    private readonly Dictionary<string, long> stagedLanguageCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> stagedCodecCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> stagedCategoryCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<int, long> stagedChannelCounts = [];
    private readonly Dictionary<string, StagedMediaFilterEntry> stagedMediaFilters = new(StringComparer.Ordinal);

    private sealed record StagedMediaFilterEntry(string Group, string? Language, string? Codec, int? Channels, string? Category);
    private sealed record AudioUserLabelDelta(Dictionary<string, string> Labels,
        Dictionary<string, MediaEntry> Entries,
        Dictionary<(string Bank, uint Id), SortedDictionary<(long Ordinal, string MemberPath), MediaEntry>?> Groups,
        Dictionary<string, ((string Bank, uint Id) Group, (long Ordinal, string MemberPath) Order)?> Membership);

    private AudioExplorerSession(ArchiveSnapshot snapshot, CatalogSession archiveSession, FeatureStore featureStore,
        AudioSnapshotResult result, AliasStore aliases, CacheManager cache, AudioToolPaths tools)
    {
        Snapshot = snapshot;
        metadataCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        this.archiveSession = archiveSession;
        this.featureStore = featureStore;
        AudioResult = result;
        this.aliases = aliases;
        this.cache = cache;
        audioTools = tools;
        audioService = new AudioService(tools, sourceStorage: new CacheAudioSourceStorage(cache));
        mediaByIdentity = new Dictionary<string, MediaEntry>(StringComparer.Ordinal);
        AudioResult = result;
        var labels = BuildUserLabels(result.Catalog);
        foreach (var media in result.Catalog.Media)
            if (media.Identity is not null)
            {
                mediaByIdentity.TryAdd(media.Identity.Key, media);
                mediaFingerprints[media.Identity.Key] = StagedMediaFingerprint(media, labels.GetValueOrDefault(media.Identity.Key, UserLabel(media)));
            }
        InitializeFilterOptions();
    }

    public ArchiveSnapshot Snapshot { get; }
    public AudioSnapshotResult AudioResult { get; private set; }
    public AudioCatalog Catalog => AudioResult.Catalog;
    public IReadOnlyList<Diagnostic> Diagnostics => AudioResult.Diagnostics;
    public IReadOnlyList<AudioBankChoice> Banks { get; private set; } = [];
    public IReadOnlyList<string> Languages { get; private set; } = [];
    public IReadOnlyList<string> Codecs { get; private set; } = [];
    public IReadOnlyList<string> Categories { get; private set; } = [];
    public IReadOnlyList<int> ChannelCounts { get; private set; } = [];
    public bool IsEnrichmentComplete => Volatile.Read(ref enrichmentComplete) != 0;
    public AudioExplorerProgress CurrentProgress => Volatile.Read(ref currentProgress);
    public bool IsEnrichmentStarted
    {
        get { lock (disposeGate) return enrichmentTask?.IsValueCreated == true; }
    }
    public event EventHandler<AudioExplorerUpdate>? Updated;

    public static async Task<AudioExplorerSession> OpenAsync(string input, CacheManager cache, AliasStore aliases,
        AudioToolPaths? tools = null, IProgress<AudioExplorerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var session = await OpenStagedAsync(input, cache, aliases, tools, progress, cancellationToken).ConfigureAwait(false);
        try
        {
            await session.EnrichmentTask.ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Opens the indexed archive first, then enriches immutable audio snapshots in the background.</summary>
    public static Task<AudioExplorerSession> OpenStagedAsync(string input, CacheManager cache, AliasStore aliases,
        AudioToolPaths? tools = null, IProgress<AudioExplorerProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => OpenStagedAsync(input, cache, aliases, tools, progress, cancellationToken, startEnrichment: true);

    /// <summary>Opens the archive and optionally starts cold audio metadata enrichment immediately.</summary>
    public static async Task<AudioExplorerSession> OpenStagedAsync(string input, CacheManager cache, AliasStore aliases,
        AudioToolPaths? tools, IProgress<AudioExplorerProgress>? progress,
        CancellationToken cancellationToken, bool startEnrichment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(aliases);
        cancellationToken.ThrowIfCancellationRequested();
        var audioTools = tools ?? AudioToolPaths.Discover(applicationDirectory: AppContext.BaseDirectory);
        CatalogSession? archiveSession = null;
        FeatureStore? featureStore = null;
        try
        {
            archiveSession = await CatalogSession.OpenAsync(input, cache.Location,
                new InlineProgress<ArchiveProgress>(value => progress?.Report(new(value.Phase, value.Completed, value.Total, value.Phase))),
                cancellationToken).ConfigureAwait(false);
            var snapshot = archiveSession.Snapshot;
            featureStore = await Task.Run(() => FeatureStore.Open(snapshot, cache.Location), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var featureKey = BuildSnapshotFeatureKey(snapshot);
            var session = new AudioExplorerSession(snapshot, archiveSession, featureStore, EmptyAudioResult(snapshot), aliases, cache, audioTools);
            session.ConfigureDeferredAudio(featureKey, progress);
            if (startEnrichment)
            {
                try
                {
                    await session.EnsureAudioInitializedAsync(cancellationToken).ConfigureAwait(false);
                    _ = session.EnrichmentTask;
                }
                catch
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    featureStore = null; archiveSession = null;
                    throw;
                }
            }
            return session;
        }
        catch
        {
            if (featureStore is not null) await Task.Run(featureStore.Dispose).ConfigureAwait(false);
            if (archiveSession is not null) await archiveSession.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task EnrichmentTask
    {
        get
        {
            lock (disposeGate)
            {
                if (enrichmentTask is null) return Task.CompletedTask;
                if (enrichmentTask.IsValueCreated) return enrichmentTask.Value;
                ThrowIfDisposed();
                return enrichmentTask.Value;
            }
        }
    }

    /// <summary>Stops background audio metadata indexing while leaving the archive session usable.</summary>
    public bool CancelEnrichment()
    {
        lock (disposeGate)
        {
            if (Volatile.Read(ref disposed) != 0 || enrichmentTask is null || !enrichmentTask.IsValueCreated ||
                enrichmentTask.Value.IsCompleted || IsEnrichmentComplete) return false;
            if (metadataCancellation.IsCancellationRequested) return false;
            var previous = CurrentProgress;
            metadataCancellation.Cancel();
            if (CurrentProgress.Phase != "Cancelled")
            {
                var terminal = new AudioExplorerProgress("Cancelled", previous.Completed, previous.Total);
                Volatile.Write(ref currentProgress, terminal);
                Interlocked.Exchange(ref enrichmentProgressCompleted, terminal.Completed);
                Interlocked.Exchange(ref enrichmentProgressTotal, terminal.Total);
                PublishUpdate(terminal.Phase, terminal.Detail, terminal.Completed, terminal.Total);
            }
            return true;
        }
    }

    private async Task EnrichAsync(string featureKey, IProgress<AudioExplorerProgress>? progress, CancellationToken token, bool upgradeNames = false)
    {
        var reader = new DetroitAudioSnapshotReader();
        try
        {
            var readerProgress = new InlineProgress<AudioReadProgress>(value =>
            {
                token.ThrowIfCancellationRequested();
                if (value.Snapshot is not null)
                {
                    var staged = new AudioSnapshotResult(value.Snapshot, value.Snapshot.Diagnostics.AsReadOnly());
                    ReplaceSnapshotAsync(staged, initializeFeatures: false, token,
                        changedMedia: value.ChangedMedia).GetAwaiter().GetResult();
                }
                Interlocked.Exchange(ref enrichmentProgressCompleted, value.Completed);
                Interlocked.Exchange(ref enrichmentProgressTotal, value.Total);
                Volatile.Write(ref currentProgress, new(value.Phase, value.Completed, value.Total, value.Detail));
                if (value.Snapshot is not null)
                    PublishUpdate(value.Phase, value.Detail, value.Completed, value.Total);
                progress?.Report(new(value.Phase, value.Completed, value.Total, value.Detail));
            });
            var result = upgradeNames
                ? await reader.ReadCachedEventLinksAsync(Snapshot, Catalog, readerProgress, token).ConfigureAwait(false)
                : await reader.ReadSnapshotAsync(Snapshot, readerProgress, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.Catalog.DialogueHeaderVersion != DetroitAudioSnapshotReader.DialogueHeaderAssociationVersion)
            {
                result.Catalog.DialogueHeaderAssociations = (await reader.ReadDialogueHeaderAssociationsAsync(Snapshot, token).ConfigureAwait(false)).ToList();
                result.Catalog.DialogueHeaderVersion = DetroitAudioSnapshotReader.DialogueHeaderAssociationVersion;
            }
            ReportEnrichmentProgress("Preparing audio library", 0, 0, progress);
            LinkedFeatureDependencies? finalFence = null;
            for (int attempt = 0; attempt < 2 && finalFence is null; attempt++)
            {
                if (attempt > 0) ReportEnrichmentProgress("Retrying audio cache", 0, 0, progress);
                var expected = await Task.Run(() => featureStore.CaptureLinkedFeatureDependencies(BrowseScope, token), token).ConfigureAwait(false);
                var assetFence = await ReplaceSnapshotAsync(result, initializeFeatures: true, token,
                    expectedBrowse: expected).ConfigureAwait(false);
                if (assetFence is null) continue;
                var mediaFence = await SaveSnapshotItemsAsync(featureKey, result, progress, token, assetFence).ConfigureAwait(false);
                if (mediaFence is null) continue;
                ReportEnrichmentProgress("Saving dialogue links", 0, result.Catalog.DialogueHeaderAssociations.Count, progress);
                finalFence = await SaveDialogueHeadersAsync(featureStore, cache, featureKey, Snapshot,
                    result.Catalog, token, BrowseScope, mediaFence).ConfigureAwait(false);
                if (finalFence is not null)
                    ReportEnrichmentProgress("Saving dialogue links", result.Catalog.DialogueHeaderAssociations.Count,
                        result.Catalog.DialogueHeaderAssociations.Count, progress);
            }
            if (finalFence is null) throw new InvalidDataException("Audio metadata changed while saving. Retry indexing after the other update finishes.");
            Interlocked.Exchange(ref enrichmentComplete, 1);
            Interlocked.Exchange(ref completeCatalogLoaded, 1);
            await PublishBrowseAsync(finalFence, token).ConfigureAwait(false);
            Volatile.Write(ref currentProgress, new("Catalogue ready", result.Catalog.Media.Count, result.Catalog.Media.Count));
            Interlocked.Exchange(ref enrichmentProgressCompleted, result.Catalog.Media.Count);
            Interlocked.Exchange(ref enrichmentProgressTotal, result.Catalog.Media.Count);
            PublishUpdate("Catalogue ready", null, result.Catalog.Media.Count, result.Catalog.Media.Count);
            progress?.Report(new("Catalogue ready", result.Catalog.Media.Count, result.Catalog.Media.Count));
        }
        catch (Exception)
        {
            var cancelled = token.IsCancellationRequested;
            var phase = cancelled ? "Cancelled" : "Enrichment failed";
            var previous = CurrentProgress;
            var terminal = new AudioExplorerProgress(phase, previous.Completed, previous.Total,
                cancelled ? null : "Audio metadata could not be completed.");
            Volatile.Write(ref currentProgress, terminal);
            Interlocked.Exchange(ref enrichmentProgressCompleted, terminal.Completed);
            Interlocked.Exchange(ref enrichmentProgressTotal, terminal.Total);
            if (previous.Phase != phase)
                PublishUpdate(phase, terminal.Detail, terminal.Completed, terminal.Total);
            progress?.Report(terminal);
            throw;
        }
    }

    private async Task<LinkedFeatureDependencies?> ReplaceSnapshotAsync(AudioSnapshotResult result, bool initializeFeatures, CancellationToken token,
        bool persistFeatures = true, IReadOnlyList<MediaEntry>? changedMedia = null, LinkedFeatureDependencies? expectedBrowse = null)
    {
        if (result.Catalog.IndexContentId != Snapshot.IndexContentId || result.Catalog.PhysicalSourceStamp != Snapshot.PhysicalSourceStamp)
            throw new InvalidDataException("Audio snapshot does not belong to the opened archive revision.");
        var fullUpdate = initializeFeatures || changedMedia is null;
        AudioUserLabelDelta? labelDelta = null;
        Dictionary<string, string> labels;
        if (fullUpdate)
        {
            lock (catalogGate) labels = BuildUserLabels(result.Catalog);
        }
        else
        {
            labelDelta = PrepareUserLabelDelta(changedMedia!);
            labels = labelDelta.Labels;
        }

        var candidates = new Dictionary<string, MediaEntry>(StringComparer.Ordinal);
        foreach (var media in fullUpdate ? result.Catalog.Media : changedMedia!)
            if (media.Identity is { } identity) candidates[identity.Key] = media;
        if (labelDelta is not null)
            foreach (var pair in labelDelta.Entries) candidates[pair.Key] = pair.Value;
        var mediaToIndex = new List<MediaEntry>();
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var media in candidates.Values)
        {
            if (media.Identity is null) continue;
            var label = labels.GetValueOrDefault(media.Identity.Key, UserLabel(media));
            var fingerprint = StagedMediaFingerprint(media, label);
            if (!fingerprints.TryAdd(media.Identity.Key, fingerprint))
                throw new InvalidDataException("Audio snapshot contains duplicate stable media identities.");
            if (initializeFeatures || !mediaFingerprints.TryGetValue(media.Identity.Key, out var previous) || previous != fingerprint)
                mediaToIndex.Add(media);
        }
        IEnumerable<AssetDescriptor> Descriptors() => mediaToIndex.Select(media => Describe(media,
            media.Identity is not null && labels.TryGetValue(media.Identity.Key, out var label) ? label : null));
        await featureGate.WaitAsync(token).ConfigureAwait(false);
        var publishedFence = expectedBrowse;
        try
        {
            if (persistFeatures && initializeFeatures)
            {
                if (expectedBrowse is null)
                    await Task.Run(() => featureStore.ReplaceAssets(AssetFamily, Descriptors(), token), token).ConfigureAwait(false);
                else publishedFence = await Task.Run(() => featureStore.ReplaceLinkedFeatureAssets(expectedBrowse, Descriptors(), token), token).ConfigureAwait(false);
            }
            else if (persistFeatures && mediaToIndex.Count > 0)
                await PublishRecoveryCheckpointsAsync(mediaToIndex, labels, token).ConfigureAwait(false);
            if (persistFeatures && initializeFeatures && (expectedBrowse is null || publishedFence is not null))
                await Task.Run(() => featureStore.Save("audio-feature-index", AssetIndexVersion, new AudioFeatureMarker(mediaToIndex.Count), token), token).ConfigureAwait(false);
            lock (catalogGate)
            {
                AudioResult = result;
                if (initializeFeatures) { mediaByIdentity.Clear(); mediaFingerprints.Clear(); labelsByIdentity.Clear(); }
                foreach (var media in mediaToIndex)
                    if (media.Identity is not null)
                    {
                        mediaByIdentity[media.Identity.Key] = media;
                        mediaFingerprints[media.Identity.Key] = fingerprints[media.Identity.Key];
                    }
                if (fullUpdate)
                {
                    labelsByIdentity.Clear();
                    foreach (var pair in labels) labelsByIdentity[pair.Key] = pair.Value;
                }
                else if (labelDelta is not null) ApplyUserLabelDelta(labelDelta);
                if (initializeFeatures) InitializeFilterOptions(labelsAlreadyBuilt: true);
                else UpdateStagedFilterOptions(result.Catalog, fullUpdate ? null : changedMedia);
            }
        }
        finally { featureGate.Release(); }
        return publishedFence;
    }

    private void UpdateStagedFilterOptions(AudioCatalog catalog, IReadOnlyCollection<MediaEntry>? changedMedia = null)
    {
        if (changedMedia is null)
        {
            stagedGroupCounts.Clear(); stagedLanguages.Clear(); stagedCodecs.Clear(); stagedChannels.Clear(); stagedCategories.Clear();
            stagedLanguageCounts.Clear(); stagedCodecCounts.Clear(); stagedChannelCounts.Clear(); stagedCategoryCounts.Clear(); stagedMediaFilters.Clear();
            foreach (var media in catalog.Media) UpdateStagedMediaFilter(media);
        }
        else foreach (var media in changedMedia) UpdateStagedMediaFilter(media);

        var banks = catalog.Banks.OrderBy(bank => bank.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        bankLabelsByKey.Clear();
        var choices = new List<AudioBankChoice>(banks.Length + 1);
        for (var index = 0; index < banks.Length; index++)
        {
            var bank = banks[index];
            var label = string.IsNullOrWhiteSpace(bank.Name) || bank.Name == bank.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ? $"Bank {index + 1}" : bank.Name;
            bankLabelsByKey[bank.Key] = label;
            choices.Add(new(bank.Key, label, stagedGroupCounts.GetValueOrDefault(bank.Key), bank.Id, bank.Name,
                bank.Diagnostics.FirstOrDefault()?.Message ?? (bank.ContainerIdentity is null ? "Bank payload unavailable." : null)) { CanExportOriginal = bank.ContainerIdentity is not null });
        }
        if (stagedGroupCounts.TryGetValue(StandaloneGroupKey, out var standalone)) choices.Add(new(StandaloneGroupKey, "Standalone", standalone, 0, "Standalone"));
        Banks = choices;
        Languages = stagedLanguages.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Codecs = stagedCodecs.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Categories = stagedCategories.Order(StringComparer.Ordinal).ToArray();
        ChannelCounts = stagedChannels.Order().ToArray();
        MergeRecoveryFilterChoices();
    }

    private void UpdateStagedMediaFilter(MediaEntry media)
    {
        if (media.Identity is not { } identity) return;
        if (stagedMediaFilters.Remove(identity.Key, out var prior))
        {
            Decrement(stagedGroupCounts, prior.Group, out _);
            if (prior.Language is { } oldLanguage && Decrement(stagedLanguageCounts, oldLanguage, out var noLanguage) && noLanguage)
                stagedLanguages.Remove(oldLanguage);
            if (prior.Codec is { } oldCodec && Decrement(stagedCodecCounts, oldCodec, out var noCodec) && noCodec)
                stagedCodecs.Remove(oldCodec);
            if (prior.Category is { } oldCategory && Decrement(stagedCategoryCounts, oldCategory, out var noCategory) && noCategory)
                stagedCategories.Remove(oldCategory);
            if (prior.Channels is { } oldChannels && Decrement(stagedChannelCounts, oldChannels, out var noChannels) && noChannels)
                stagedChannels.Remove(oldChannels);
        }

        var current = new StagedMediaFilterEntry(GroupKey(media),
            string.IsNullOrWhiteSpace(media.Language) ? null : media.Language,
            string.IsNullOrWhiteSpace(media.Codec) || media.Codec == "Unknown" ? null : media.Codec,
            media.Channels > 0 ? media.Channels : null,
            string.IsNullOrWhiteSpace(media.Category) ? null : media.Category);
        stagedMediaFilters[identity.Key] = current;
        Increment(stagedGroupCounts, current.Group);
        if (current.Language is { } language) { Increment(stagedLanguageCounts, language); stagedLanguages.Add(language); }
        if (current.Codec is { } codec) { Increment(stagedCodecCounts, codec); stagedCodecs.Add(codec); }
        if (current.Category is { } category) { Increment(stagedCategoryCounts, category); stagedCategories.Add(category); }
        if (current.Channels is { } channels) { Increment(stagedChannelCounts, channels); stagedChannels.Add(channels); }
    }

    private static void Increment<TKey>(Dictionary<TKey, long> counts, TKey key) where TKey : notnull =>
        counts[key] = counts.GetValueOrDefault(key) + 1;

    private static bool Decrement<TKey>(Dictionary<TKey, long> counts, TKey key, out bool removed) where TKey : notnull
    {
        if (!counts.TryGetValue(key, out var count)) { removed = false; return false; }
        if (count <= 1) { counts.Remove(key); removed = true; }
        else { counts[key] = count - 1; removed = false; }
        return true;
    }

    private static async Task<AudioSnapshotResult?> LoadCachedSnapshotAsync(FeatureStore store, string featureKey,
        ArchiveSnapshot snapshot, CancellationToken token)
    {
        var head = await Task.Run(() => store.Load<AudioSnapshotHead>(featureKey, SnapshotVersion, token), token).ConfigureAwait(false);
        if (!MetadataCacheSourcePolicy.CanReuseStampHead(snapshot)||head is null || head.IndexContentId != snapshot.IndexContentId || head.PhysicalSourceStamp != snapshot.PhysicalSourceStamp ||
            !SourceIdentity.SameStamp(head.IndexStamp, snapshot.IndexStamp)||!MetadataCacheSourcePolicy.SameVolumes(head.SourceVolumes,snapshot.Volumes)) return null;
        var catalog = new AudioCatalog
        {
            SourcePath = head.SourcePath, Fingerprint = head.Fingerprint, IndexContentId = head.IndexContentId,
            PhysicalSourceStamp = head.PhysicalSourceStamp, IndexStamp = head.IndexStamp,
            SourceVolumes = head.SourceVolumes, IndexedResourceCount = head.IndexedResourceCount,
            EventMediaVersion = head.EventMediaVersion
        };
        var loads = await Task.Run(() => new AudioSnapshotRows(
            store.LoadItems<BankInfo>("audio.banks", SnapshotVersion, token),
            store.LoadItems<MediaEntry>("audio.media", SnapshotVersion, token),
            store.LoadItems<AudioEvent>("audio.events", SnapshotVersion, token),
            store.LoadItems<AudioNameEvidence>("audio.names", SnapshotVersion, token),
            store.LoadItems<LocalizedText>("audio.localization", SnapshotVersion, token),
            store.LoadItems<Diagnostic>("audio.diagnostics", SnapshotVersion, token),
            store.LoadItems<MediaSourceEvidence>("audio.source-declarations", SnapshotVersion, token)), token).ConfigureAwait(false);
        catalog.Banks.AddRange(loads.Banks); catalog.Media.AddRange(loads.Media); catalog.Events.AddRange(loads.Events);
        foreach (var media in catalog.Media)
            if (media.Completeness == MediaCompleteness.Unverified &&
                media.State is (MediaState.CompleteEmbedded or MediaState.CompleteExternal) && MediaPolicy.CanPreview(media))
                media.SourceDeclarationProcessingComplete = true;
        catalog.Names.AddRange(loads.Names); catalog.Localization.AddRange(loads.Localization);
        catalog.Diagnostics.AddRange(loads.Diagnostics); catalog.SourceDeclarations.AddRange(loads.SourceDeclarations);
        return new(catalog, catalog.Diagnostics.AsReadOnly());
    }

    private void ReportEnrichmentProgress(string phase, long completed, long total, IProgress<AudioExplorerProgress>? progress)
    {
        Interlocked.Exchange(ref enrichmentProgressCompleted, completed);
        Interlocked.Exchange(ref enrichmentProgressTotal, total);
        var update = new AudioExplorerProgress(phase, completed, total);
        Volatile.Write(ref currentProgress, update);
        progress?.Report(update);
    }

    private async Task<LinkedFeatureDependencies?> SaveSnapshotItemsAsync(string featureKey, AudioSnapshotResult result, IProgress<AudioExplorerProgress>? progress,
        CancellationToken token, LinkedFeatureDependencies expected)
    {
        var catalog = result.Catalog;
        long completed = 0;
        var total = (long)catalog.Banks.Count + catalog.Media.Count + catalog.Events.Count + catalog.Names.Count +
            catalog.Localization.Count + catalog.Diagnostics.Count + catalog.SourceDeclarations.Count;
        ReportEnrichmentProgress("Saving audio library", 0, total, progress);
        IEnumerable<T> Track<T>(IEnumerable<T> items)
        {
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                yield return item;
                completed++;
                if (completed % 512 == 0 || completed == total)
                    ReportEnrichmentProgress("Saving audio library", completed, total, progress);
            }
        }
        await Task.Run(() => featureStore.SaveItems("audio.banks", SnapshotVersion, Track(catalog.Banks), token), token).ConfigureAwait(false);
        var fence = await Task.Run(() => featureStore.TrySaveLinkedFeatureItems(expected, Track(catalog.Media), token), token).ConfigureAwait(false);
        if (fence is null) return null;
        await Task.Run(() => featureStore.SaveItems("audio.events", SnapshotVersion, Track(catalog.Events), token), token).ConfigureAwait(false);
        await Task.Run(() => featureStore.SaveItems("audio.names", SnapshotVersion, Track(catalog.Names), token), token).ConfigureAwait(false);
        await Task.Run(() => featureStore.SaveItems("audio.localization", SnapshotVersion, Track(catalog.Localization), token), token).ConfigureAwait(false);
        await Task.Run(() => featureStore.SaveItems("audio.diagnostics", SnapshotVersion, Track(catalog.Diagnostics), token), token).ConfigureAwait(false);
        await Task.Run(() => featureStore.SaveItems("audio.source-declarations", SnapshotVersion, Track(catalog.SourceDeclarations), token), token).ConfigureAwait(false);
        var head = new AudioSnapshotHead(catalog.SourcePath, catalog.Fingerprint, catalog.IndexContentId,
            catalog.PhysicalSourceStamp, catalog.IndexStamp, catalog.SourceVolumes, catalog.IndexedResourceCount, catalog.EventMediaVersion);
        return await Task.Run(() => featureStore.TrySaveLinkedFeatureCheckpoint(fence,
            BoundedFeatureWrite.Create(featureKey, SnapshotVersion, head, FeatureStore.MaximumLinkedFeatureHeadBytes), token), token).ConfigureAwait(false);
    }

    private void PublishUpdate(string phase, string? detail, long progressCompleted = 0, long progressTotal = 0)
    {
        AudioExplorerUpdate update;
        lock (catalogGate)
            update = new(Interlocked.Increment(ref updateGeneration), IsEnrichmentComplete, Banks.ToArray(), Catalog.Media.Count, phase, detail,
                progressCompleted > 0 || progressTotal > 0 ? progressCompleted : Interlocked.Read(ref enrichmentProgressCompleted),
                progressCompleted > 0 || progressTotal > 0 ? progressTotal : Interlocked.Read(ref enrichmentProgressTotal));
        Updated?.Invoke(this, update);
    }

    private static AudioSnapshotResult EmptyAudioResult(ArchiveSnapshot snapshot)
    {
        var catalog = new AudioCatalog
        {
            SourcePath = snapshot.IndexPath, Fingerprint = snapshot.IndexContentId, IndexContentId = snapshot.IndexContentId,
            PhysicalSourceStamp = snapshot.PhysicalSourceStamp, SourceVolumes = snapshot.Volumes.ToList(),
            IndexedResourceCount = snapshot.Records.Count
        };
        return new(catalog, Array.Empty<Diagnostic>());
    }

    public async Task<AudioExplorerPage> QueryAsync(AudioExplorerQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query);
        ThrowIfDisposed();
        if (query.Category is not null) await PrepareAudioCategoriesAsync(cancellationToken).ConfigureAwait(false);
        var cachedPage = await ReadBrowsePageAsync(query, cancellationToken).ConfigureAwait(false);
        if (cachedPage is not null) return cachedPage;
        if (!IsCatalogLoaded && !hasCachedAudioSnapshot)
            return await ReadRecoveryPageAsync(query, cancellationToken).ConfigureAwait(false);
        var assetQuery = ToAssetQuery(query);
        await featureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var total = await Task.Run(() => featureStore.Count(assetQuery, cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var identityKeys = await Task.Run(() => featureStore.QueryIdentityKeys(assetQuery, cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new AudioExplorerPage(total, HydrateRows(identityKeys, cancellationToken));
        }
        finally { featureGate.Release(); }
    }

    public async Task<IReadOnlyList<AudioExplorerRow>> ReadMatchingAsync(AudioExplorerQuery query,
        IProgress<AudioExplorerProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return await ReadMatchingOnDemandAsync(query,progress,cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAliasAsync(ResourceIdentity identity, string? alias, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var selected = await PrepareSelectedMediaAsync(identity, cancellationToken).ConfigureAwait(false);
        await featureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var media = selected.Media;
            await aliases.SetAsync(identity, alias, cancellationToken).ConfigureAwait(false);
            var descriptor = Describe(media);
            await Task.Run(() => featureStore.UpsertAssets([descriptor], cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { featureGate.Release(); }
    }

    public async Task<PreviewSession> PreviewAsync(ResourceIdentity identity, bool partialPreview = false,
        WavFormat format = WavFormat.Pcm16, IProgress<AudioProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var diagnostic = WorkflowDiagnostics.Begin("Audio.Preview", identity.Key);
        try
        {
            var result = await PreparePreviewAsync(identity, partialPreview, format,
                new PreviewProgress(progress, diagnostic), cancellationToken).ConfigureAwait(false);
            diagnostic?.Complete();
            return result;
        }
        catch (OperationCanceledException) { diagnostic?.Cancel(); throw; }
        catch (Exception error)
        {
            diagnostic?.Fail(error, error is ArchiveException archive ? archive.Code : error.Data["ErrorCode"]?.ToString());
            throw;
        }
    }

    private sealed class PreviewProgress(IProgress<AudioProgress>? target, WorkflowDiagnosticOperation? diagnostic) : IProgress<AudioProgress>
    {
        public void Report(AudioProgress value)
        {
            diagnostic?.Progress(value.Phase, value.Total > 0 ? value.Completed : null,
                value.Total > 0 ? value.Total : null);
            target?.Report(value);
        }
    }

    private async Task<PreviewSession> PreparePreviewAsync(ResourceIdentity identity, bool partialPreview,
        WavFormat format, IProgress<AudioProgress> progress, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        progress.Report(new("Preparing selected clip"));
        var selected = await PrepareSelectedMediaAsync(identity, cancellationToken).ConfigureAwait(false);
        var media = selected.Media;
        var capabilities = selected.Descriptor.Capabilities;
        if (media.Completeness == MediaCompleteness.Fragment && !partialPreview)
            throw new AudioToolException("Choose Partial preview to audition this fragment.");
        if (partialPreview && media.Completeness != MediaCompleteness.Fragment)
            throw new AudioToolException("This media is not classified as a fragment.");
        if (media.Completeness == MediaCompleteness.Unverified && !media.SourceDeclarationProcessingComplete)
            throw new AudioToolException("Audio source declarations are still being checked.");
        if (!MediaPolicy.CanPreview(media)) throw new AudioToolException(MediaPolicy.GetUnavailableReason(media));
        if (partialPreview && !capabilities.HasFlag(AssetCapabilities.PartialPreview) ||
            !partialPreview && !capabilities.HasFlag(AssetCapabilities.Preview))
            throw new AudioToolException("The audio source could not be validated for preview.");
        using var metadataVerifier = new AudioMetadataVerifier(Snapshot).CreateLease();
        progress.Report(new("Verifying audio metadata"));
        await metadataVerifier.VerifyMediaAsync(media,cancellationToken).ConfigureAwait(false);

        var source = PinMediaSource(media);
        using (source.IndexHandle)
        using (source.VolumeHandle)
        {
            var token = cancellationToken;
            SourceIdentity.EnsureUnchanged(new FileRevisionProvider(), source.IndexHandle, Snapshot.IndexPath, Snapshot.IndexStamp);
            SourceIdentity.EnsureUnchanged(new FileRevisionProvider(), source.VolumeHandle, source.Volume.FilePath, source.Volume.Stamp);
            var payloadHash = await HashSliceAsync(source.VolumeHandle, media.Slice.Offset, media.Slice.Length, progress, token).ConfigureAwait(false);
            SourceIdentity.EnsureUnchanged(new FileRevisionProvider(), source.VolumeHandle, source.Volume.FilePath, source.Volume.Stamp);
            var sourceRevision = SourceIdentity.DerivedCacheKey(Snapshot.IndexContentId, Snapshot.PhysicalSourceStamp);
            var options = $"wav={format};partial={partialPreview};channels=source";
            progress.Report(new("Reading decoder identity"));
            var fingerprint = await GetToolFingerprintAsync(AudioToolUse.Wav, token).ConfigureAwait(false);
            var cacheKey = CacheManager.Key(sourceRevision, payloadHash, fingerprint, options);
            progress.Report(new("Preparing preview output"));
            var lease = await cache.AcquirePreviewOutputAsync(cacheKey, ".wav", async (destination, cacheToken) =>
            {
                await DecodePreviewOutputAsync(media,source.Volume,source.VolumeHandle,payloadHash,destination,
                    format,partialPreview,progress,cacheToken).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            try
            {
                progress.Report(new("Verifying prepared preview"));
                SourceIdentity.EnsureUnchanged(new FileRevisionProvider(), source.IndexHandle, Snapshot.IndexPath, Snapshot.IndexStamp);
                SourceIdentity.EnsureUnchanged(new FileRevisionProvider(), source.VolumeHandle, source.Volume.FilePath, source.Volume.Stamp);
                var afterHash = await HashSliceAsync(source.VolumeHandle, media.Slice.Offset, media.Slice.Length, progress, token).ConfigureAwait(false);
                if (!string.Equals(payloadHash, afterHash, StringComparison.Ordinal))
                    throw new ArchiveException("SourceChanged", "The selected audio payload changed while its preview was prepared.");
                await metadataVerifier.VerifyMediaAsync(media,token).ConfigureAwait(false);
                progress.Report(new("Preview ready"));
                return new PreviewSession(identity, new SourceRevision(Snapshot.IndexContentId, Snapshot.PhysicalSourceStamp),
                    lease.Path, partialPreview ? AssetCompleteness.Partial : media.Completeness switch
                    {
                        MediaCompleteness.Complete => AssetCompleteness.Complete,
                        MediaCompleteness.Fragment => AssetCompleteness.Partial,
                        _ => AssetCompleteness.Unverified
                    }, lease);
            }
            catch
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    public AudioExportReview Review(IReadOnlyList<AudioExplorerRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ThrowIfDisposed();
        if (rows.Any(row => !row.IsHydrated || !IsCatalogLoaded && !row.SelectedSourceVerified))
            throw new ArchiveException("AudioSourceDetailsRequired", "Load the selected clips before reviewing their export.");
        var byteTotal = 0L;
        foreach (var row in rows) byteTotal = checked(byteTotal + Math.Max(0, row.Media.Slice.Length));
        return new(Snapshot, rows, byteTotal,
            rows.LongCount(r => r.Media.Completeness == MediaCompleteness.Complete),
            rows.LongCount(r => r.Media.Completeness == MediaCompleteness.Unverified),
            rows.LongCount(r => r.Media.Completeness == MediaCompleteness.Fragment),
            rows.LongCount(r => !MediaPolicy.HasBytes(r.Media)));
    }

    public IReadOnlyList<AudioExplorerRow> ResolveSelection(IEnumerable<ResourceIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        ThrowIfDisposed();
        var requested = identities.Where(identity => identity.IndexContentId == Snapshot.IndexContentId).Distinct().ToArray();
        var known = ResolveKnownSelection(requested.Select(identity => identity.Key));
        if (!IsCatalogLoaded && known.Count == requested.Length) return known;
        EnsureAudioInitialized();
        lock (catalogGate)
        {
            var result = new List<AudioExplorerRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var identity in requested)
            {
                if (identity.IndexContentId != Snapshot.IndexContentId || !seen.Add(identity.Key) ||
                    !mediaByIdentity.TryGetValue(identity.Key, out var media)) continue;
                var descriptor = Describe(media);
                result.Add(new AudioExplorerRow(descriptor, media, aliases.Get(identity), UserBankLabel(media)));
            }
            return result.AsReadOnly();
        }
    }

    public IReadOnlyList<AudioExplorerRow> ResolveSelectionKeys(IEnumerable<string> identityKeys)
    {
        ArgumentNullException.ThrowIfNull(identityKeys);
        ThrowIfDisposed();
        var requested = identityKeys.Distinct(StringComparer.Ordinal).ToArray();
        var known = ResolveKnownSelection(requested);
        if (!IsCatalogLoaded && known.Count == requested.Length) return known;
        EnsureAudioInitialized();
        lock (catalogGate)
        {
            var rows = new List<AudioExplorerRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in requested)
            {
                if (!seen.Add(key) || !mediaByIdentity.TryGetValue(key, out var media) || media.Identity is null) continue;
                var descriptor = Describe(media);
                rows.Add(new AudioExplorerRow(descriptor, media, aliases.Get(media.Identity), UserBankLabel(media)));
            }
            return rows.AsReadOnly();
        }
    }

    public async Task<IReadOnlyList<AudioExplorerRow>> ReadSelectionAsync(IEnumerable<string> identityKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identityKeys);
        var keys = identityKeys.Distinct(StringComparer.Ordinal).ToArray();
        var rows = new List<AudioExplorerRow>(keys.Length);
        foreach (var page in keys.Chunk(128))
            rows.AddRange(await PrepareSelectedMediaAsync(page, cancellationToken).ConfigureAwait(false));
        return rows.AsReadOnly();
    }

    public async Task<AudioExportReport> ExportAsync(AudioExportReview review, AudioExportOptions options,
        IProgress<AudioProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();
        if (!ReferenceEquals(review.Snapshot, Snapshot) || review.Snapshot.IndexContentId != Snapshot.IndexContentId ||
            review.Snapshot.PhysicalSourceStamp != Snapshot.PhysicalSourceStamp)
            throw new ArchiveException("StaleReview", "The export review belongs to a different archive revision. Review the selection again.");
        var selectedRows = await ReadSelectionAsync(review.Rows.Select(row => row.Identity.Key), cancellationToken).ConfigureAwait(false);
        review = Review(selectedRows);
        var request = new AudioExportRequest(review.Snapshot,
            review.Rows.Select(row => new AudioExportSource(row.Identity, row.Media)).ToArray(), true);
        var toolRoots = new[] { AppContext.BaseDirectory, Environment.CurrentDirectory, audioTools.ConfiguredDirectory,
            audioTools.VgmstreamCli, audioTools.Ffmpeg, audioTools.Ffprobe, audioTools.Ww2Ogg, audioTools.Revorb,
            audioTools.PackedCodebooks, audioTools.PackedCodebooksAoTuV }
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Directory.Exists(path) ? path! : Path.GetDirectoryName(path!)!);
        var fingerprint = options.Format == AudioExportFormat.Original ? "audio-original-copy-v1" :
            !review.Rows.Any(row => MediaPolicy.CanConvert(row.Media)) ? "audio-no-conversion-v1" :
            await GetToolFingerprintAsync(options.Format == AudioExportFormat.Wav ? AudioToolUse.Wav : AudioToolUse.Ogg, cancellationToken).ConfigureAwait(false);
        return await new AudioExportService(audioService, fingerprint, privateRoots: toolRoots).ExportAsync(request, options, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AudioExportReport> ExportBankAsync(string bankKey, string outputDirectory, bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureAudioInitializedAsync(cancellationToken).ConfigureAwait(false);
        var bank = Catalog.Banks.FirstOrDefault(candidate => candidate.Key == bankKey)
            ?? throw new ArchiveException("MissingBank", "The selected bank is not part of the current audio snapshot.");
        var toolRoots = new[] { AppContext.BaseDirectory, Environment.CurrentDirectory, audioTools.ConfiguredDirectory,
            audioTools.VgmstreamCli, audioTools.Ffmpeg, audioTools.Ffprobe, audioTools.Ww2Ogg, audioTools.Revorb,
            audioTools.PackedCodebooks, audioTools.PackedCodebooksAoTuV }
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Directory.Exists(path) ? path! : Path.GetDirectoryName(path!)!);
        return await new AudioExportService(audioService, "audio-original-copy-v1", privateRoots: toolRoots)
            .ExportBankAsync(Snapshot, bank, outputDirectory, overwrite, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate)
        {
            disposeTask ??= DisposeCoreAsync();
            return new ValueTask(disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task[] started;
        lock (disposeGate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            started = new[] { enrichmentTask, audioInitialization, browseInitialization }.OfType<Lazy<Task>>()
                .Where(task => task.IsValueCreated).Select(task => task.Value)
                .Concat(toolFingerprints.Values.Where(task => task.IsValueCreated).Select(task => (Task)task.Value)).ToArray();
        }
        try { await Task.WhenAll(started).ConfigureAwait(false); }
        catch { /* Disposal drains a failed enrichment just as it drains cancellation. */ }
        await WaitForSharedFeatureLeasesAsync().ConfigureAwait(false);
        await DisposeSelectedMediaSourcesAsync().ConfigureAwait(false);
        await featureGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try { await Task.Run(featureStore.Dispose).ConfigureAwait(false); }
            finally { await archiveSession.DisposeAsync().ConfigureAwait(false); }
        }
        finally
        {
            featureGate.Release();
            metadataCancellation.Dispose();
            lifetime.Dispose();
        }
    }

    private void InitializeFilterOptions(bool labelsAlreadyBuilt = false)
    {
        if (!labelsAlreadyBuilt)
        {
            labelsByIdentity.Clear();
            foreach (var pair in BuildUserLabels(Catalog)) labelsByIdentity[pair.Key] = pair.Value;
        }
        UpdateStagedFilterOptions(Catalog);
    }

    private AssetDescriptor Describe(MediaEntry media, string? labelOverride = null)
    {
        var identity = media.Identity ?? throw new InvalidDataException($"Audio reader did not assign a stable identity to media {media.Id:X8}.");
        if (identity.IndexContentId != Snapshot.IndexContentId || identity.Ordinal < 0 || identity.Ordinal >= Snapshot.Records.Count)
            throw new InvalidDataException("Audio reader returned a media identity outside the selected source revision.");
        var payloadIdentity = media.PayloadIdentity ?? identity;
        var owner = AudioResourceBinding.ResolveOwner(Snapshot, payloadIdentity);
        var relative = owner.Volume?.RelativeName ?? owner.Resource.VolumeName;
        IReadOnlyList<SourceExtent> extents = Array.Empty<SourceExtent>();
        var sourceBindingResolved = false;
        if (media.PayloadIdentity is not null && MediaPolicy.HasBytes(media))
        {
            var binding = AudioResourceBinding.Resolve(Snapshot, media);
            extents = [new SourceExtent(binding.Volume.RelativeName, binding.Extent.Offset, binding.Extent.Length)];
            sourceBindingResolved = true;
        }
        var names = media.Names.Select(evidence => new Deviant.Assets.NameEvidence(evidence.Text, MapOrigin(evidence.Kind), evidence.Source, evidence.CollisionGroup)).ToList();
        var alias = aliases.Get(identity);
        if (!string.IsNullOrWhiteSpace(alias)) names.Add(new(alias, NameOrigin.UserAlias, "User alias"));
        var sourceMetadataReady = media.MetadataReadCoverageComplete && media.MetadataReadDigests.Count > 0 ||
            Snapshot.IndexStamp.Reliable && Snapshot.ReliableVolumeStamps;
        var sourceReady = sourceBindingResolved && sourceMetadataReady;
        var mediaClassified = media.Completeness is MediaCompleteness.Complete or MediaCompleteness.Fragment ||
            media.SourceDeclarationProcessingComplete;
        var canPreview = sourceReady && mediaClassified && MediaPolicy.CanPreview(media);
        var canConvert = sourceReady && mediaClassified && MediaPolicy.CanConvert(media);
        var capabilities = AssetCapabilities.None;
        if (sourceReady && MediaPolicy.HasBytes(media)) capabilities |= AssetCapabilities.Original;
        if (canPreview && media.Completeness != MediaCompleteness.Fragment) capabilities |= AssetCapabilities.Preview;
        if (canPreview && media.Completeness == MediaCompleteness.Fragment) capabilities |= AssetCapabilities.PartialPreview;
        if (canConvert) capabilities |= AssetCapabilities.Wav | AssetCapabilities.Ogg;
        var label = string.IsNullOrWhiteSpace(alias) ? labelOverride ?? UserLabel(media) : alias;
        return new AssetDescriptor(identity, new SourceRevision(Snapshot.IndexContentId, Snapshot.PhysicalSourceStamp),
            AssetFamily, label, GroupKey(media), MapAvailability(media),
            media.Completeness switch
            {
                MediaCompleteness.Complete => AssetCompleteness.Complete,
                MediaCompleteness.Fragment => AssetCompleteness.Partial,
                _ => AssetCompleteness.Unverified
            }, capabilities, names, extents,
            media.Language, media.Codec, media.Duration, media.Channels,
            media.ResolutionDetails.Length == 0 ? null : media.ResolutionDetails,
            NaturalOrder.Key(label))
        {
            Metadata = new Dictionary<string, string> { ["audio.source-label"] = labelOverride ?? UserLabel(media), ["audio.category"] = media.Category },
            SearchTerms = string.Join('\n', names.Select(name => name.Text).Concat(media.Aliases)
                .Prepend(label).Append(identity.Key).Append(media.IdText).Append(media.Name).Append(media.BankName))
        };
    }

    private AudioExplorerRow[] HydrateRows(IReadOnlyList<string> identityKeys, CancellationToken token)
    {
        var rows = new AudioExplorerRow[identityKeys.Count];
        for (var index = 0; index < identityKeys.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            rows[index] = ToRow(identityKeys[index]);
        }
        return rows;
    }

    private AudioExplorerRow ToRow(string identityKey)
    {
        lock (catalogGate)
        {
            if (!mediaByIdentity.TryGetValue(identityKey, out var media) || media.Identity is null)
                throw new InvalidDataException("The audio feature index references media missing from its catalog snapshot.");
            var descriptor = Describe(media);
            return new AudioExplorerRow(descriptor, media, aliases.Get(media.Identity), UserBankLabel(media));
        }
    }

    private string UserBankLabel(MediaEntry media)
    {
        var key = GroupKey(media);
        if (key == StandaloneGroupKey) return "Standalone";
        return bankLabelsByKey.TryGetValue(key, out var label) ? label : "Bank";
    }

    private static AssetQuery ToAssetQuery(AudioExplorerQuery query) => new(
        Search: string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim(),
        GroupKey: string.IsNullOrWhiteSpace(query.BankKey) ? null : query.BankKey,
        Language: query.Language,
        Codec: query.Codec,
        MinimumDuration: query.MinimumDuration,
        MaximumDuration: query.MaximumDuration,
        Channels: query.Channels,
        Availability: query.Availability,
        Completeness: query.Completeness is { } completeness ? MapCompleteness(completeness) : null,
        Offset: query.Offset, Limit: query.Limit, Family: AssetFamily,Sort:query.Sort==AssetSort.Name?AssetSort.Default:query.Sort,Descending:query.Descending)
        { AudioCategory = string.IsNullOrWhiteSpace(query.Category) ? null : query.Category };

    private string UserLabel(MediaEntry media) => media.Identity is not null && labelsByIdentity.TryGetValue(media.Identity.Key, out var label)
        ? label : FallbackMediaLabel(media);

    private static bool LooksLikeNumericId(string value, uint id)
    {
        var trimmed = value.Trim();
        return string.Equals(trimmed, id.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private Dictionary<string, string> BuildUserLabels(AudioCatalog catalog)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        fallbackLabelGroups.Clear();
        fallbackLabelMembership.Clear();
        foreach (var media in catalog.Media.OrderBy(media => media.Identity?.Ordinal ?? long.MaxValue))
        {
            if (media.Identity is null) continue;
            var stored = StoredUserLabel(media);
            if (stored is not null) { labels[media.Identity.Key] = stored; continue; }
            var group = (media.BankKey, media.Id);
            var order = (media.Identity.Ordinal, media.Identity.MemberPath ?? "");
            if (!fallbackLabelGroups.TryGetValue(group, out var entries))
                fallbackLabelGroups[group] = entries = [];
            entries[order] = media;
            fallbackLabelMembership[media.Identity.Key] = (group, order);
        }
        foreach (var entries in fallbackLabelGroups.Values)
        {
            var occurrence = 0;
            foreach (var (order, media) in entries)
            {
                if (media.Identity is { } identity)
                    labels[identity.Key] = FallbackMediaLabel(media, ++occurrence);
            }
        }
        return labels;
    }

    private AudioUserLabelDelta PrepareUserLabelDelta(IReadOnlyList<MediaEntry> changedMedia)
    {
        lock (catalogGate)
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            var entries = new Dictionary<string, MediaEntry>(StringComparer.Ordinal);
            var groupUpdates = new Dictionary<(string Bank, uint Id), SortedDictionary<(long Ordinal, string MemberPath), MediaEntry>? >();
            var membershipUpdates = new Dictionary<string, ((string Bank, uint Id) Group, (long Ordinal, string MemberPath) Order)?>(StringComparer.Ordinal);
            var affectedGroups = new HashSet<(string Bank, uint Id)>();

            SortedDictionary<(long Ordinal, string MemberPath), MediaEntry> WritableGroup((string Bank, uint Id) group)
            {
                if (groupUpdates.TryGetValue(group, out var pending))
                {
                    if (pending is not null) return pending;
                    var empty = new SortedDictionary<(long Ordinal, string MemberPath), MediaEntry>();
                    groupUpdates[group] = empty;
                    return empty;
                }
                var copy = fallbackLabelGroups.TryGetValue(group, out var existing)
                    ? new SortedDictionary<(long Ordinal, string MemberPath), MediaEntry>(existing)
                    : new SortedDictionary<(long Ordinal, string MemberPath), MediaEntry>();
                groupUpdates[group] = copy;
                return copy;
            }

            foreach (var media in changedMedia)
            {
                if (media.Identity is not { } identity) continue;
                if (!entries.TryAdd(identity.Key, media)) entries[identity.Key] = media;
                if (fallbackLabelMembership.TryGetValue(identity.Key, out var oldMembership))
                {
                    WritableGroup(oldMembership.Group).Remove(oldMembership.Order);
                    affectedGroups.Add(oldMembership.Group);
                    membershipUpdates[identity.Key] = null;
                }

                if (StoredUserLabel(media) is { } stored)
                {
                    labels[identity.Key] = stored;
                    continue;
                }

                var group = (media.BankKey, media.Id);
                var order = (identity.Ordinal, identity.MemberPath ?? "");
                WritableGroup(group)[order] = media;
                membershipUpdates[identity.Key] = (group, order);
                affectedGroups.Add(group);
            }

            foreach (var group in affectedGroups)
            {
                var groupEntries = groupUpdates[group];
                if (groupEntries is null) continue;
                var occurrence = 0;
                foreach (var (order, media) in groupEntries)
                {
                    if (media.Identity is not { } identity) continue;
                    var label = FallbackMediaLabel(media, ++occurrence);
                    entries[identity.Key] = media;
                    if (!labelsByIdentity.TryGetValue(identity.Key, out var previous) || previous != label)
                        labels[identity.Key] = label;
                }
            }

            return new(labels, entries, groupUpdates, membershipUpdates);
        }
    }

    private void ApplyUserLabelDelta(AudioUserLabelDelta delta)
    {
        foreach (var (group, entries) in delta.Groups)
        {
            if (entries is null || entries.Count == 0) fallbackLabelGroups.Remove(group);
            else fallbackLabelGroups[group] = entries;
        }
        foreach (var (identity, membership) in delta.Membership)
        {
            if (membership is null) fallbackLabelMembership.Remove(identity);
            else fallbackLabelMembership[identity] = membership.Value;
        }
        foreach (var (identity, label) in delta.Labels) labelsByIdentity[identity] = label;
    }

    private static string? StoredUserLabel(MediaEntry media)
    {
        var stored = media.NameKind is NameKind.Stored or NameKind.Verified or NameKind.Description
            ? new[] { media.DisplayLabel, media.Name }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && !LooksLikeNumericId(value, media.Id))
            : null;
        stored ??= media.Names.Where(name => name.Kind is NameKind.Verified or NameKind.Stored or NameKind.Description)
            .Select(name => name.Text).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && !LooksLikeNumericId(value, media.Id));
        stored ??= media.Aliases.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && !LooksLikeNumericId(value, media.Id));
        return stored;
    }

    private static string FallbackMediaLabel(MediaEntry media)
    {
        var bank = string.IsNullOrWhiteSpace(media.BankName) ? "Unknown bank" : media.BankName.Trim();
        return $"{bank} · {media.Id}";
    }

    private static string FallbackMediaLabel(MediaEntry media, int occurrence)
    {
        var label = FallbackMediaLabel(media);
        return occurrence == 1 ? label : $"{label} · {occurrence}";
    }

    private static string StagedMediaFingerprint(MediaEntry media, string label)
    {
        static string Extent(DataSlice? slice) => slice is null ? "" :
            $"{Path.GetFullPath(slice.FilePath)}\0{slice.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture)}\0{slice.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var fields = new List<string>
        {
            media.Identity?.Key ?? "", media.PayloadIdentity?.Key ?? "", label, media.BankKey, media.BankName,
            media.Name, media.DisplayLabel, media.Language, media.Category, media.Codec, media.State.ToString(),
            media.Completeness.ToString(), media.SourceDeclarationProcessingComplete.ToString(),
            media.ContainerValidity.ToString(), media.Availability.ToString(),
            media.NameKind.ToString(), media.ResolutionDetails, media.Subtitle,
            media.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            media.Channels.ToString(System.Globalization.CultureInfo.InvariantCulture),
            media.Duration?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            media.MeasuredDuration?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            Extent(media.Slice), Extent(media.OriginalSlice), Extent(media.DidxExtent)
        };
        fields.AddRange(media.SourceEvidence.Select(item =>
            $"{item.BankKey}:{item.ObjectId:X8}:{item.Declaration.Id:X8}:{item.Declaration.StreamType}:{item.Declaration.InMemorySize}:{item.Declaration.Flags}:{item.Declaration.PluginId:X8}:{item.Offset}"));
        fields.AddRange(media.EventKeys.Order(StringComparer.Ordinal));
        fields.AddRange(media.Aliases.Order(StringComparer.Ordinal));
        fields.AddRange(media.Names.Select(item => $"{item.Namespace}:{item.Id:X8}:{item.Kind}:{item.Source}:{item.Offset}:{item.Text}:{item.CollisionGroup}"));
        fields.AddRange(media.Candidates.Select(Extent).Order(StringComparer.Ordinal));
        fields.AddRange(media.CandidateRejections.Select(item => $"{Extent(item.Slice)}:{item.Reason}").Order(StringComparer.Ordinal));
        fields.AddRange(media.EquivalenceEvidence.Select(item => $"{item.Sha256}:{item.Comparison}:{string.Join(";", item.SourceLocations.Select(Extent).Order(StringComparer.Ordinal))}"));
        return SourceIdentity.HashFields("deviant-audio-staged-media-v1", fields);
    }





    private static string GroupKey(MediaEntry media) => string.IsNullOrWhiteSpace(media.BankKey) ? StandaloneGroupKey : media.BankKey;

    private static AssetAvailability MapAvailability(MediaEntry media)
    {
        if (media.State == MediaState.Unsupported || media.ContainerValidity == ContainerValidity.Unsupported) return AssetAvailability.Unsupported;
        if (media.State == MediaState.Malformed || media.ContainerValidity is ContainerValidity.Malformed or ContainerValidity.Truncated) return AssetAvailability.Malformed;
        return media.Availability switch
        {
            SourceAvailability.Available => AssetAvailability.Available,
            SourceAvailability.Missing => AssetAvailability.Missing,
            SourceAvailability.Ambiguous => AssetAvailability.Ambiguous,
            _ => AssetAvailability.Missing
        };
    }

    private static AssetAvailability MapAvailability(SourceAvailability availability) => availability switch
    {
        SourceAvailability.Available => AssetAvailability.Available,
        SourceAvailability.Missing => AssetAvailability.Missing,
        SourceAvailability.Ambiguous => AssetAvailability.Ambiguous,
        _ => AssetAvailability.Missing
    };

    private static AssetCompleteness MapCompleteness(MediaCompleteness completeness) => completeness switch
    {
        MediaCompleteness.Complete => AssetCompleteness.Complete,
        MediaCompleteness.Fragment => AssetCompleteness.Partial,
        _ => AssetCompleteness.Unverified
    };

    private static NameOrigin MapOrigin(NameKind kind) => kind switch
    {
        NameKind.Verified => NameOrigin.Verified,
        NameKind.Candidate => NameOrigin.Candidate,
        NameKind.Description => NameOrigin.Description,
        _ => NameOrigin.Stored
    };

    private static void ValidateQuery(AudioExplorerQuery query)
    {
        if (query.Category is { Length: > 512 }) throw new ArgumentException("The audio type filter is too long.", nameof(query));
        if (query.Offset < 0) throw new ArgumentOutOfRangeException(nameof(query), "Page offset must be zero or greater.");
        if (query.Limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(query), "Page limit must be between 1 and 10000.");
        if (query.MinimumDuration < 0 || query.MaximumDuration < 0) throw new ArgumentOutOfRangeException(nameof(query), "Duration filters must be zero or greater.");
        if (query.MinimumDuration is { } min && query.MaximumDuration is { } max && min > max)
            throw new ArgumentException("Minimum duration cannot exceed maximum duration.", nameof(query));
    }

    private (VolumeSource Volume, Microsoft.Win32.SafeHandles.SafeFileHandle IndexHandle,
        Microsoft.Win32.SafeHandles.SafeFileHandle VolumeHandle) PinMediaSource(MediaEntry media)
    {
        var binding = AudioResourceBinding.Resolve(Snapshot, media);
        var volume = binding.Volume;
        if (media.Slice.Length is < 0 or > MaximumPreviewSourceBytes || !IndexLayout.InBounds(binding.Extent.Offset, binding.Extent.Length, volume.Stamp.Length))
            throw new ArchiveException("InvalidRange", "The selected audio payload is outside its bounded volume or preview budget.");
        Microsoft.Win32.SafeHandles.SafeFileHandle? indexHandle = null;
        Microsoft.Win32.SafeHandles.SafeFileHandle? volumeHandle = null;
        try
        {
            indexHandle = SourceIdentity.OpenPinned(Snapshot.IndexPath);
            volumeHandle = SourceIdentity.OpenPinned(volume.FilePath);
            var provider = new FileRevisionProvider();
            SourceIdentity.EnsureUnchanged(provider, indexHandle, Snapshot.IndexPath, Snapshot.IndexStamp);
            SourceIdentity.EnsureUnchanged(provider, volumeHandle, volume.FilePath, volume.Stamp);
            return (volume, indexHandle, volumeHandle);
        }
        catch
        {
            indexHandle?.Dispose(); volumeHandle?.Dispose(); throw;
        }
    }

    private static async Task<string> HashSliceAsync(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long offset,
        long length, IProgress<AudioProgress>? progress, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long readTotal = 0;
        while (readTotal < length)
        {
            token.ThrowIfCancellationRequested();
            var count = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0,
                (int)Math.Min(buffer.Length, length - readTotal)), checked(offset + readTotal), token).ConfigureAwait(false);
            if (count == 0) throw new ArchiveException("SourceChanged", "Audio source ended while hashing a preview payload.");
            hash.AppendData(buffer.AsSpan(0, count)); readTotal += count;
            progress?.Report(new AudioProgress("Hashing audio payload", Completed: (int)Math.Min(int.MaxValue, readTotal), Total: (int)Math.Min(int.MaxValue, length)));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string BuildSnapshotFeatureKey(ArchiveSnapshot snapshot)
    {
        var fields = new List<string> { snapshot.IndexContentId, snapshot.PhysicalSourceStamp, Path.GetFullPath(snapshot.IndexPath) };
        fields.AddRange(snapshot.Volumes.OrderBy(volume => volume.Package).Select(volume => Path.GetFullPath(volume.FilePath)));
        return SourceIdentity.HashFields("deviant-audio-snapshot-v1", fields);
    }

    private static async Task<string> GetPreviewToolFingerprintAsync(AudioToolPaths tools, AudioToolUse use, CancellationToken token)
    {
        using var timing = AssetLoadDiagnostics.Measure("AudioToolFingerprint", use.ToString());
        var components = new (string Name, string? Path)[]
        {
            ("vgmstream-cli", tools.VgmstreamCli), ("ffmpeg", tools.Ffmpeg), ("ffprobe", tools.Ffprobe),
            ("ww2ogg", tools.Ww2Ogg), ("revorb", tools.Revorb), ("packed-codebooks", tools.PackedCodebooks),
            ("packed-codebooks-aotuv", tools.PackedCodebooksAoTuV)
        };
        if (use == AudioToolUse.Wav) components = [components[0]];
        var fields = new List<string> { PreviewVersion, use.ToString() };
        var relevantFiles = components.Where(component => !string.IsNullOrWhiteSpace(component.Path))
            .Select(component => Path.GetFullPath(component.Path!)).ToHashSet(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var directories = components.Where(component => !string.IsNullOrWhiteSpace(component.Path) && File.Exists(component.Path))
            .Select(component => Path.GetDirectoryName(Path.GetFullPath(component.Path!))!)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            token.ThrowIfCancellationRequested();
            fields.Add(Path.GetFileName(directory));
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => relevantFiles.Contains(Path.GetFullPath(path)) ||
                    Path.GetExtension(path).ToLowerInvariant() is ".dll" or ".so" or ".dylib")
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var path in files)
            {
                token.ThrowIfCancellationRequested();
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                fields.Add(Path.GetFileName(path));
                fields.Add(Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant());
            }
        }
        foreach (var (name, path) in components.Where(component => string.IsNullOrWhiteSpace(component.Path) || !File.Exists(component.Path)))
        { fields.Add(name); fields.Add("unavailable"); }
        return SourceIdentity.HashFields("deviant-audio-toolchain-v2", fields);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(AudioExplorerSession));
    }

    private static string StandaloneGroupKey => "__deviant_standalone__";
    private sealed record AudioFeatureMarker(int MediaCount);
    private sealed record AudioSnapshotHead(string SourcePath, string Fingerprint, string IndexContentId,
        string PhysicalSourceStamp, FileStamp IndexStamp, List<VolumeSource> SourceVolumes, int IndexedResourceCount, int EventMediaVersion = 0);
    private sealed record AudioSnapshotRows(List<BankInfo> Banks, List<MediaEntry> Media, List<AudioEvent> Events,
        List<NameEvidence> Names, List<LocalizedText> Localization, List<Diagnostic> Diagnostics,
        List<MediaSourceEvidence> SourceDeclarations);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    { public void Report(T value) => report(value); }
}
