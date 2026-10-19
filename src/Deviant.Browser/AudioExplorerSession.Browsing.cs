using System.Globalization;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;

namespace Deviant.Browser;

public sealed partial class AudioExplorerSession
{
    private LinkedFeatureScope BrowseScope => new("audio.browse", "audio-browse-v1", "audio.media", SnapshotVersion, AssetFamily)
    { AdditionalFeatures = [BuildSnapshotFeatureKey(Snapshot), DialogueHeaderKey(BuildSnapshotFeatureKey(Snapshot))] };
    private const int MaximumRetainedBrowseRows = 512;
    private const long MaximumRetainedBrowseBytes = 32L * 1024 * 1024;
    private Lazy<Task>? browseInitialization;
    private AudioBrowseSummary? browseSummary;
    private int completeCatalogLoaded;
    private readonly Dictionary<string, AudioExplorerRow> browseRows = new(StringComparer.Ordinal);
    private readonly Queue<string> browseRowOrder = new();
    private readonly Dictionary<string, long> browseRowBytes = new(StringComparer.Ordinal);
    private long retainedBrowseBytes;
    private LinkedFeatureDependencies? cachedBrowseDependencies;

    private sealed record AudioBrowseSummary(string IndexContentId, string PhysicalSourceStamp, FileStamp IndexStamp,
        IReadOnlyList<VolumeSource> Volumes, long MediaCount, int BankCount, IReadOnlyList<AudioBankChoice> Banks,
        IReadOnlyList<string> Languages, IReadOnlyList<string> Codecs, IReadOnlyList<int> Channels,
        int EventMediaVersion, int DialogueHeaderVersion);

    public long MediaCount => browseSummary?.MediaCount ?? (IsCatalogLoaded?Catalog.Media.Count:
        Math.Max(Catalog.Media.Count,Interlocked.Read(ref recoveryMediaCount)));
    public int BankCount => browseSummary?.BankCount ?? Catalog.Banks.Count;
    public bool IsCatalogLoaded => Volatile.Read(ref completeCatalogLoaded) != 0;
    public Task LoadCatalogAsync(CancellationToken token = default) => EnsureAudioInitializedAsync(token);

    public async Task PrepareBrowseAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Task initialization;
        lock (disposeGate)
        {
            ThrowIfDisposed();
            browseInitialization ??= new(() => Task.Run(LoadBrowseSummaryAsync, lifetime.Token), LazyThreadSafetyMode.ExecutionAndPublication);
            initialization = browseInitialization.Value;
        }
        await initialization.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task LoadBrowseSummaryAsync()
    {
        using var timing = AssetLoadDiagnostics.Measure("AudioBrowseHead", Snapshot.PhysicalSourceStamp);
        try
        {
            var head = await Task.Run(() => featureStore.ReadLinkedFeatureHead<AudioBrowseSummary>(BrowseScope, lifetime.Token), lifetime.Token).ConfigureAwait(false);
            if (head is not null && ValidBrowseSummary(head.Head)) ApplyBrowseSummary(head.Head);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        { WorkflowDiagnostics.Failure("Audio.BrowseCache", error, phase: "Reading audio names"); }
    }

    private bool ValidBrowseSummary(AudioBrowseSummary summary) =>
        summary.IndexStamp is not null && summary.Volumes is not null && summary.Banks is not null &&
        summary.Languages is not null && summary.Codecs is not null && summary.Channels is not null &&
        MetadataCacheSourcePolicy.CanReuseStampHead(Snapshot) &&
        summary.IndexContentId == Snapshot.IndexContentId && summary.PhysicalSourceStamp == Snapshot.PhysicalSourceStamp &&
        SourceIdentity.SameStamp(summary.IndexStamp, Snapshot.IndexStamp) && MetadataCacheSourcePolicy.SameVolumes(summary.Volumes, Snapshot.Volumes) &&
        summary.EventMediaVersion == DetroitAudioSnapshotReader.EventMediaVersion &&
        summary.DialogueHeaderVersion == DetroitAudioSnapshotReader.DialogueHeaderAssociationVersion &&
        summary.MediaCount >= 0 && summary.BankCount >= 0 && summary.Banks.All(bank => bank.MediaCount >= 0);

    private void ApplyBrowseSummary(AudioBrowseSummary summary)
    {
        lock (catalogGate)
        {
            browseSummary = summary;
            if (!IsCatalogLoaded) Catalog.EventMediaVersion = summary.EventMediaVersion;
            Banks = summary.Banks; Languages = summary.Languages; Codecs = summary.Codecs; ChannelCounts = summary.Channels;
            bankLabelsByKey.Clear();
            foreach (var bank in summary.Banks) bankLabelsByKey[bank.Key] = bank.Label;
            Interlocked.Exchange(ref enrichmentComplete, 1);
            Volatile.Write(ref currentProgress, new("Catalogue ready", summary.MediaCount, summary.MediaCount));
            Interlocked.Exchange(ref enrichmentProgressCompleted, summary.MediaCount);
            Interlocked.Exchange(ref enrichmentProgressTotal, summary.MediaCount);
            if (Snapshot.Records.Count == 0)
            {
                Catalog.DialogueHeaderVersion = summary.DialogueHeaderVersion;
                Interlocked.Exchange(ref completeCatalogLoaded, 1);
            }
        }
        if (Snapshot.Records.Count == 0)
            lock (disposeGate) if (enrichmentTask?.IsValueCreated != true) enrichmentTask = null;
    }

    private async Task PublishBrowseAsync(LinkedFeatureDependencies expected, CancellationToken token)
    {
        if (!IsEnrichmentComplete || !MetadataCacheSourcePolicy.CanReuseStampHead(Snapshot) || !AreDialogueHeadersReady) return;
        var summary = new AudioBrowseSummary(Snapshot.IndexContentId, Snapshot.PhysicalSourceStamp, Snapshot.IndexStamp,
            Snapshot.Volumes, Catalog.Media.Count, Catalog.Banks.Count, Banks.ToArray(), Languages.ToArray(), Codecs.ToArray(),
            ChannelCounts.ToArray(), Catalog.EventMediaVersion, Catalog.DialogueHeaderVersion);
        if (!ValidBrowseSummary(summary)) return;
        var references = new LinkedFeatureReference[Catalog.Media.Count];
        for (var index = 0; index < references.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            references[index] = new(Catalog.Media[index].Identity ?? throw new InvalidDataException("Cached audio has no stable identity."),
                "audio.media/item/" + index.ToString("D12", CultureInfo.InvariantCulture));
        }
        var generation = await Task.Run(() => featureStore.PublishLinkedFeatureProjection(expected, references, summary, token), token).ConfigureAwait(false);
        if (generation is not null) ApplyBrowseSummary(summary);
    }

    private async Task<AudioExplorerPage?> ReadBrowsePageAsync(AudioExplorerQuery query, CancellationToken token)
    {
        if (query.Limit > 128) return null;
        await PrepareBrowseAsync(token).ConfigureAwait(false);
        if (browseSummary is null) return null;
        using var timing = AssetLoadDiagnostics.Measure("AudioBrowsePage", Snapshot.PhysicalSourceStamp);
        var page = await Task.Run(() => featureStore.ReadLinkedFeaturePage<AudioBrowseSummary, MediaEntry>(BrowseScope,
            ToAssetQuery(query), token: token), token).ConfigureAwait(false);
        if (page is null || !ValidBrowseSummary(page.Head)) { browseSummary = null; return null; }
        ApplyBrowseSummary(page.Head);
        var rows = new AudioExplorerRow[page.Rows.Count];
        lock (catalogGate)
        {
            for (var index = 0; index < rows.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var stored = page.Rows[index];
                var media = stored.Feature;
                if (media.Identity != stored.Asset.Identity || stored.Asset.Revision != new SourceRevision(Snapshot.IndexContentId, Snapshot.PhysicalSourceStamp) ||
                    !stored.Asset.Metadata.TryGetValue("audio.source-label", out var sourceLabel))
                    throw new InvalidDataException("The cached audio row does not match its source projection.");
                if (media.Completeness == MediaCompleteness.Unverified &&
                    media.State is (MediaState.CompleteEmbedded or MediaState.CompleteExternal) && MediaPolicy.CanPreview(media))
                    media.SourceDeclarationProcessingComplete = true;
                labelsByIdentity[media.Identity.Key] = sourceLabel;
                var descriptor = Describe(media, sourceLabel);
                var row = new AudioExplorerRow(descriptor, media, aliases.Get(media.Identity), UserBankLabel(media));
                RememberBrowseRow(row, stored.SerializedBytes);
                rows[index] = row;
            }
        }
        AssetLoadDiagnostics.Counters("AudioBrowseHydration", Snapshot.PhysicalSourceStamp,
            new Dictionary<string, long> { ["Rows"] = rows.Length, ["Generation"] = page.Generation });
        return new(page.Total, rows) { Generation = page.Generation };
    }

    private void RememberBrowseRow(AudioExplorerRow row, long serializedBytes)
    {
        var key = row.Identity.Key;
        if(row.SourceMedia is null&&browseRows.TryGetValue(key,out var resident)&&resident.SelectedSourceVerified&&
            resident.Descriptor.Revision==row.Descriptor.Revision&&resident.Descriptor.GroupKey==row.Descriptor.GroupKey&&
            SourceLabel(resident.Descriptor)==SourceLabel(row.Descriptor)) return;
        if (!browseRows.ContainsKey(key)) browseRowOrder.Enqueue(key);
        retainedBrowseBytes -= browseRowBytes.GetValueOrDefault(key);
        // Allow for UTF-16 strings, object/list overhead and references in the decoded metadata.
        var retained = checked(serializedBytes * 16 + 1024);
        browseRows[key] = row; browseRowBytes[key] = retained; retainedBrowseBytes += retained;
        var evicted = 0;
        while (browseRows.Count > MaximumRetainedBrowseRows || retainedBrowseBytes > MaximumRetainedBrowseBytes)
        {
            var expired = browseRowOrder.Dequeue(); browseRows.Remove(expired);
            retainedBrowseBytes -= browseRowBytes[expired]; browseRowBytes.Remove(expired);
            if (!IsCatalogLoaded) labelsByIdentity.Remove(expired);
            ++evicted;
        }
        if (evicted > 0) AssetLoadDiagnostics.Counters("AudioBrowseRetention", Snapshot.PhysicalSourceStamp,
            new Dictionary<string, long> { ["Rows"] = browseRows.Count, ["EstimatedBytes"] = retainedBrowseBytes, ["Evicted"] = evicted });
    }

    public IReadOnlyList<AudioExplorerRow> ResolveKnownSelection(IEnumerable<string> identityKeys)
    {
        ThrowIfDisposed();
        lock (catalogGate)
            return identityKeys.Distinct(StringComparer.Ordinal).Select(key => browseRows.GetValueOrDefault(key))
                .OfType<AudioExplorerRow>().ToArray();
    }

    private bool HasKnownMedia(ResourceIdentity identity, out MediaEntry media)
    {
        lock (catalogGate)
        {
            if (mediaByIdentity.TryGetValue(identity.Key, out media!)) return true;
            if (browseRows.TryGetValue(identity.Key, out var row) && row.SourceMedia is { } selected) { media = selected; return true; }
            return false;
        }
    }
}
