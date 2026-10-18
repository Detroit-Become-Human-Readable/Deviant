using Deviant.Assets;
using Deviant.Formats.Audio;

namespace Deviant.Browser;

public sealed partial class AudioExplorerSession
{
    private enum AudioToolUse { Wav, Ogg }
    private bool hasCachedAudioSnapshot;

    private void ConfigureDeferredAudio(string featureKey, IProgress<AudioExplorerProgress>? progress)
    {
        audioInitialization = new(() => Task.Run(() => RestoreAudioMetadataAsync(featureKey, lifetime.Token), lifetime.Token),
            LazyThreadSafetyMode.ExecutionAndPublication);
        enrichmentTask = new(() => Task.Run(() => RunDeferredEnrichmentAsync(featureKey, progress, metadataCancellation.Token),
            metadataCancellation.Token), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private Task EnsureAudioInitializedAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (disposeGate)
        {
            ThrowIfDisposed();
            return audioInitialization is null ? Task.CompletedTask : audioInitialization.Value.WaitAsync(token);
        }
    }
    private void EnsureAudioInitialized() => EnsureAudioInitializedAsync(lifetime.Token).GetAwaiter().GetResult();

    private async Task RestoreAudioMetadataAsync(string featureKey, CancellationToken token)
    {
        using var timing = AssetLoadDiagnostics.Measure("AudioMetadataRestore", Snapshot.PhysicalSourceStamp);
        var dependencies = await Task.Run(() => featureStore.CaptureLinkedFeatureDependencies(BrowseScope, token), token).ConfigureAwait(false);
        cachedBrowseDependencies = dependencies;
        var result = await LoadCachedSnapshotAsync(featureStore, featureKey, Snapshot, token).ConfigureAwait(false);
        if (result is null)
        {
            Interlocked.Exchange(ref enrichmentComplete, 0);
            await RestoreRecoveryCountAsync(token).ConfigureAwait(false);
            return;
        }
        await LoadDialogueHeadersAsync(featureStore, featureKey, Snapshot, result.Catalog, token).ConfigureAwait(false);
        var marker = await Task.Run(() => featureStore.Load<AudioFeatureMarker>("audio-feature-index", AssetIndexVersion, token), token).ConfigureAwait(false);
        var projectionCount = await Task.Run(() => featureStore.Count(new(Family: AssetFamily), token), token).ConfigureAwait(false);
        cachedBrowseDependencies = await ReplaceSnapshotAsync(result, initializeFeatures: true, token,
            persistFeatures: marker?.MediaCount != result.Catalog.Media.Count || projectionCount != result.Catalog.Media.Count,
            expectedBrowse: dependencies).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        hasCachedAudioSnapshot = true;
        cachedNamesReady = result.Catalog.EventMediaVersion == DetroitAudioSnapshotReader.EventMediaVersion;
        bool ready = cachedNamesReady && result.Catalog.DialogueHeaderVersion == DetroitAudioSnapshotReader.DialogueHeaderAssociationVersion;
        Interlocked.Exchange(ref enrichmentComplete, ready ? 1 : 0);
        Interlocked.Exchange(ref completeCatalogLoaded, 1);
        Volatile.Write(ref currentProgress, new(cachedNamesReady ? "Catalogue ready" : "Updating audio names", result.Catalog.Media.Count, result.Catalog.Media.Count));
        Interlocked.Exchange(ref enrichmentProgressCompleted, result.Catalog.Media.Count);
        Interlocked.Exchange(ref enrichmentProgressTotal, result.Catalog.Media.Count);
        lock (disposeGate)
            if (ready && enrichmentTask?.IsValueCreated != true) enrichmentTask = null;
        if (ready && cachedBrowseDependencies is not null)
        {
            await PublishBrowseAsync(cachedBrowseDependencies, token).ConfigureAwait(false);
        }
        PublishUpdate("Loaded cached audio metadata", null);
    }

    private async Task RunDeferredEnrichmentAsync(string featureKey, IProgress<AudioExplorerProgress>? progress, CancellationToken token)
    {
        try
        {
            await EnsureAudioInitializedAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (IsEnrichmentComplete) return;
            if (cachedNamesReady)
                await EnrichDialogueHeadersAsync(featureKey, progress, token).ConfigureAwait(false);
            else await EnrichAsync(featureKey, progress, token, upgradeNames: hasCachedAudioSnapshot).ConfigureAwait(false);
        }
        catch
        {
            string phase = token.IsCancellationRequested ? "Cancelled" : "Enrichment failed";
            if (CurrentProgress.Phase != phase)
            {
                var previous = CurrentProgress;
                Volatile.Write(ref currentProgress, new(phase, previous.Completed, previous.Total));
                PublishUpdate(phase, null, previous.Completed, previous.Total);
            }
            throw;
        }
    }

    private Task<string> GetToolFingerprintAsync(AudioToolUse use, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (disposeGate)
        {
            ThrowIfDisposed();
            if (!toolFingerprints.TryGetValue(use, out var fingerprint))
            {
                fingerprint = new(() => Task.Run(() => GetPreviewToolFingerprintAsync(audioTools, use, lifetime.Token), lifetime.Token),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                toolFingerprints.Add(use, fingerprint);
            }
            return fingerprint.Value.WaitAsync(token);
        }
    }
}
