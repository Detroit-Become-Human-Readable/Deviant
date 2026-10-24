using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;

namespace Deviant.Browser;

public sealed partial class AudioExplorerSession
{
    private const long MaximumMatchingRetainedBytes = 128L * 1024 * 1024;

    public async Task<IReadOnlyList<AudioExplorerRow>> ReadMatchingOnDemandAsync(AudioExplorerQuery query,
        IProgress<AudioExplorerProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query);
        if (query.Category is not null) await PrepareAudioCategoriesAsync(cancellationToken).ConfigureAwait(false);
        using var owner = AcquireSharedFeatureStore();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.SourceCancellation);
        var token = linked.Token;
        var selection = await Task.Run(() => owner.Store.ReadAssetIdentitySelection(owner.Revision,
            ToAssetQuery(query with { Offset = 0, Limit = 128 }), token: token), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        progress?.Report(new("Preparing matching audio", 0, selection.MatchedCount));
        var result = new List<AudioExplorerRow>(selection.IdentityKeys.Count);
        long retainedBytes = checked(selection.RetainedBytes + selection.MatchedCount * IntPtr.Size);
        foreach (var keys in selection.IdentityKeys.Chunk(FeatureStore.MaximumLinkedFeaturePageRows))
        {
            token.ThrowIfCancellationRequested();
            var rows = await PrepareSelectedMediaAsync(keys, token).ConfigureAwait(false);
            if (rows.Count != keys.Length || !rows.Select(row => row.Identity.Key).SequenceEqual(keys, StringComparer.Ordinal))
                throw new ArchiveException("SelectionChanged", "The matching audio identities changed while their details loaded.");
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                // Reserve a conservative decoded-metadata estimate across all returned pages,
                // independently of the smaller resident browsing cache.
                var metadataBytes = BoundedFeatureWrite.Create("audio.selection-size", RecoveryVersion,
                    new AudioRecoveryCheckpoint(owner.Revision, row.Media, SourceLabel(row.Descriptor))
                        { SelectedScope = row.SelectedClassificationScope }, MaximumRecoveryPayloadBytes).Utf8ByteCount;
                var descriptorBytes = BoundedFeatureWrite.Create("audio.selection-row-size", RecoveryVersion,
                    row.Descriptor, MaximumRecoveryPayloadBytes).Utf8ByteCount;
                long bytes = checked((metadataBytes + (long)descriptorBytes) * 16 + 1024);
                if (bytes > MaximumMatchingRetainedBytes - retainedBytes)
                    throw new ArchiveException("AudioSelectionLimit",
                        "The matching audio metadata exceeds its supported memory limit. Narrow the filters and try again.");
                retainedBytes += bytes; result.Add(row);
            }
            progress?.Report(new("Preparing matching audio", result.Count, selection.MatchedCount));
        }
        token.ThrowIfCancellationRequested();
        AssetLoadDiagnostics.Counters("AudioMatchingSelection", Snapshot.PhysicalSourceStamp, new Dictionary<string, long>
            { ["MatchedCount"] = selection.MatchedCount, ["FamilyEpoch"] = selection.FamilyEpoch ?? -1,
                ["PreparedCount"] = result.Count, ["EstimatedBytes"] = retainedBytes });
        return result.AsReadOnly();
    }
}
