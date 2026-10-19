using System.Globalization;
using Deviant.Assets;
using Deviant.Catalog;

namespace Deviant.Browser;

public sealed partial class AudioExplorerSession
{
    private const int MaximumRecoveryFacetValues = 4096;
    private const long MaximumRecoveryFacetBytes = 1024 * 1024;
    private readonly SemaphoreSlim recoveryFilterChoiceGate = new(1, 1);
    private bool recoveryFacetPreparationReady;
    private long recoveryFilterChoiceEpoch = -1;
    private string[] recoveryLanguages = [], recoveryCodecs = [];
    private int[] recoveryChannels = [];
    private bool recoveryFilterChoicesTruncated;

    internal bool RecoveryFilterChoicesTruncated { get { lock (catalogGate) return recoveryFilterChoicesTruncated; } }

    public async Task RestoreRecoveryFilterChoicesAsync(CancellationToken token = default)
    {
        using var owner = AcquireSharedFeatureStore();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner.SourceCancellation);
        await recoveryFilterChoiceGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                var cancellation = linked.Token;
                lock (catalogGate) if (IsCatalogLoaded || browseSummary is not null) return;
                if (!recoveryFacetPreparationReady)
                {
                    owner.Store.PrepareAssetFacets(owner.Revision, AssetFamily, cancellation);
                    recoveryFacetPreparationReady = true;
                }
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var epoch = owner.Store.ReadAssetFacetEpoch(owner.Revision, AssetFamily, cancellation);
                    if (epoch is null) { recoveryFacetPreparationReady = false; return; }
                    lock (catalogGate)
                    {
                        if (recoveryFilterChoiceEpoch == epoch.Value)
                        { MergeRecoveryFilterChoicesUnsafe(); return; }
                    }
                    long bytes = 0;
                    var languages = ReadRecoveryFacetValues(owner.Store, owner.Revision, AssetFacet.Language, epoch.Value, ref bytes, cancellation);
                    if (languages is null) continue;
                    var codecs = ReadRecoveryFacetValues(owner.Store, owner.Revision, AssetFacet.Codec, epoch.Value, ref bytes, cancellation);
                    if (codecs is null) continue;
                    var channels = ReadRecoveryFacetValues(owner.Store, owner.Revision, AssetFacet.Channels, epoch.Value, ref bytes, cancellation);
                    if (channels is null) continue;
                    var channelValues = channels.Values.Select(value => int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray();
                    if (owner.Store.ReadAssetFacetEpoch(owner.Revision, AssetFamily, cancellation) != epoch.Value) continue;
                    cancellation.ThrowIfCancellationRequested();
                    lock (catalogGate)
                    {
                        if (IsCatalogLoaded || browseSummary is not null) return;
                        recoveryLanguages = languages.Values; recoveryCodecs = codecs.Values; recoveryChannels = channelValues;
                        recoveryFilterChoiceEpoch = epoch.Value;
                        recoveryFilterChoicesTruncated = languages.Truncated || codecs.Truncated || channels.Truncated;
                        MergeRecoveryFilterChoicesUnsafe();
                    }
                    AssetLoadDiagnostics.Counters("AudioRecovery.FilterChoices", Snapshot.PhysicalSourceStamp, new Dictionary<string, long>
                    {
                        ["FamilyEpoch"] = epoch.Value, ["Languages"] = languages.Values.Length,
                        ["Codecs"] = codecs.Values.Length, ["Channels"] = channels.Values.Length,
                        ["EstimatedBytes"] = bytes, ["Truncated"] = recoveryFilterChoicesTruncated ? 1 : 0
                    });
                    return;
                }
            }, linked.Token).ConfigureAwait(false);
        }
        finally { recoveryFilterChoiceGate.Release(); }
    }

    internal void MergeRecoveryFilterChoices()
    { lock (catalogGate) MergeRecoveryFilterChoicesUnsafe(); }

    private void MergeRecoveryFilterChoicesUnsafe()
    {
        if (recoveryFilterChoiceEpoch < 0 || IsCatalogLoaded || browseSummary is not null) return;
        Languages = recoveryLanguages.Concat(stagedLanguages).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Codecs = recoveryCodecs.Concat(stagedCodecs).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        ChannelCounts = recoveryChannels.Concat(stagedChannels).Distinct().Order().ToArray();
    }

    private sealed record RecoveryFacetValues(string[] Values, bool Truncated);

    private static RecoveryFacetValues? ReadRecoveryFacetValues(FeatureStore store, SourceRevision revision,
        AssetFacet facet, long epoch, ref long retainedBytes, CancellationToken token)
    {
        var values = new List<string>(); string? continuation = null;
        do
        {
            token.ThrowIfCancellationRequested();
            var page = store.ReadAssetFacetPage(revision, new(Family: AssetFamily), facet, continuation,
                expectedFamilyEpoch: epoch, token: token);
            if (page is null) return null;
            foreach (var value in page.Values)
            {
                token.ThrowIfCancellationRequested();
                long bytes = checked(value.Value.Length * 2L + 64);
                if (values.Count == MaximumRecoveryFacetValues || bytes > MaximumRecoveryFacetBytes - retainedBytes)
                    return new(values.ToArray(), true);
                retainedBytes += bytes; values.Add(value.Value);
            }
            continuation = page.Continuation;
            if (values.Count == MaximumRecoveryFacetValues && continuation is not null)
                return new(values.ToArray(), true);
        } while (continuation is not null);
        return new(values.ToArray(), false);
    }
}
