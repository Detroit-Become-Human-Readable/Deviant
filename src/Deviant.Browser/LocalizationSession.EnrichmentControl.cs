using Deviant.Core;
using Deviant.Catalog;
using Deviant.Formats.Localization.Model;

namespace Deviant.Browser;

public sealed partial class LocalizationSession
{
    private readonly SemaphoreSlim enrichmentLifecycleGate = new(1, 1);
    private CancellationTokenSource? enrichmentCancellation;
    private CancellationTokenSource? retryCoordinatorCancellation;
    private Task? retryCoordinatorTask;
    private IProgress<LocalizationReadProgress>? enrichmentProgress;

    /// <summary>Stops the active localization producer while keeping this session available for queries.</summary>
    public bool CancelEnrichment()
    {
        lock (stateGate)
        {
            ThrowIfDisposed();
            if (complete) return false;
            var producerCancellation = enrichmentCancellation;
            var retryCancellation = retryCoordinatorCancellation;
            var producerActive = producerCancellation is { IsCancellationRequested: false } && !EnrichmentTask.IsCompleted;
            var retryActive = retryCancellation is { IsCancellationRequested: false };
            if (!producerActive && !retryActive)
                return false;
            producerCancellation?.Cancel();
            retryCancellation?.Cancel();
            if (!unavailable) phase = "Cancelled";
        }
        Publish(null, Status, null);
        return true;
    }

    /// <summary>Retries one cancelled or failed producer, or waits for the current producer.</summary>
    public async Task RetryEnrichmentAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await enrichmentLifecycleGate.WaitAsync(linked.Token).ConfigureAwait(false);
        Task? work = null;
        Task? drain = null;
        TaskCompletionSource? restart = null;
        CancellationTokenSource? restartCancellation = null;
        try
        {
            ThrowIfDisposed();
            lock (stateGate)
            {
                if (complete) return;
                if (retryCoordinatorTask is { IsCompleted: false } coordinator) work = coordinator;
                else
                {
                    retryCoordinatorTask = null;
                    var current = EnrichmentTask;
                    if (!current.IsCompleted && enrichmentCancellation?.IsCancellationRequested != true)
                        work = current;
                    else
                    {
                        drain = current;
                        restart = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        work = restart.Task;
                        restartCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        retryCoordinatorTask = work;
                        retryCoordinatorCancellation = restartCancellation;
                    }
                }
            }
        }
        finally { enrichmentLifecycleGate.Release(); }

        if (restart is not null)
            _ = RunRetryCoordinatorAsync(drain!, restart, restartCancellation!);
        await work!.WaitAsync(token).ConfigureAwait(false);
    }

    private void StartOwnedEnrichment(Func<CancellationToken, Task> producer,
        CancellationToken additionalCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(producer);
        var cancellation = additionalCancellation.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, additionalCancellation)
            : CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            lock (stateGate)
            {
                ThrowIfDisposed();
                enrichmentCancellation = cancellation;
                EnrichmentTask = Task.Run(() => RunOwnedEnrichmentAsync(producer, cancellation));
            }
        }
        catch
        {
            cancellation.Dispose();
            throw;
        }
    }

    private async Task RunOwnedEnrichmentAsync(Func<CancellationToken, Task> producer,
        CancellationTokenSource cancellation)
    {
        try { await producer(cancellation.Token).ConfigureAwait(false); }
        finally
        {
            lock (stateGate)
                if (ReferenceEquals(enrichmentCancellation, cancellation)) enrichmentCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task RunRetryCoordinatorAsync(Task drain, TaskCompletionSource completion,
        CancellationTokenSource retryCancellation)
    {
        var retryToken = retryCancellation.Token;
        try
        {
            try { await drain.ConfigureAwait(false); }
            catch { /* A cancelled or failed producer must reach a terminal state before replacement. */ }

            retryToken.ThrowIfCancellationRequested();
            await enrichmentLifecycleGate.WaitAsync(retryToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                lock (stateGate) if (complete) { completion.TrySetResult(); return; }
            }
            finally { enrichmentLifecycleGate.Release(); }

            await VerifySameSnapshotAsync(retryToken).ConfigureAwait(false);
            await enrichmentLifecycleGate.WaitAsync(retryToken).ConfigureAwait(false);
            Task producer;
            try
            {
                ThrowIfDisposed();
                lock (stateGate) if (complete) { completion.TrySetResult(); return; }
                retryToken.ThrowIfCancellationRequested();
                await featureGate.WaitAsync(retryToken).ConfigureAwait(false);
                try
                {
                    ThrowIfDisposed();
                    retryToken.ThrowIfCancellationRequested();
                    var previousStage = stagedGeneration;
                    var writer = await Task.Run(() => featureStore.BeginLocalizationGeneration(
                        Version, AssetIndexVersion, retryToken), retryToken).ConfigureAwait(false);
                    try
                    {
                        retryToken.ThrowIfCancellationRequested();
                        lock (stateGate)
                        {
                            ThrowIfDisposed();
                            retryToken.ThrowIfCancellationRequested();
                            stagedGeneration = writer;
                            previousStage?.Dispose();
                            if (publishedGeneration is { IsActive: true, SearchProjectionMatches: false } prior)
                            {
                                unavailable = false;
                                phase = "Updating localization search";
                                producer = StartProjectionRetry(prior, retryToken);
                            }
                            else
                            {
                                ResetSourceProgress();
                                phase = "Reading localization";
                                producer = StartSourceRetry(retryToken);
                            }
                        }
                    }
                    catch
                    {
                        if (!ReferenceEquals(stagedGeneration, writer)) writer.Dispose();
                        throw;
                    }
                }
                finally { featureGate.Release(); }
            }
            finally { enrichmentLifecycleGate.Release(); }

            Publish(enrichmentProgress, Status, null);
            await producer.ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (OperationCanceledException) when (retryToken.IsCancellationRequested)
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                lock (stateGate) if (!complete && !unavailable) phase = "Cancelled";
                Publish(enrichmentProgress, Status, null);
            }
            completion.TrySetCanceled(retryToken);
        }
        catch (Exception error)
        {
            lock (stateGate)
            {
                if (Volatile.Read(ref disposed) == 0 && !complete) unavailable = true;
            }
            if (Volatile.Read(ref disposed) == 0)
            {
                SetProgress("Localization indexing failed");
                Publish(null, Status, null);
            }
            completion.TrySetException(error);
        }
        finally
        {
            lock (stateGate)
            {
                if (ReferenceEquals(retryCoordinatorTask, completion.Task)) retryCoordinatorTask = null;
                if (ReferenceEquals(retryCoordinatorCancellation, retryCancellation)) retryCoordinatorCancellation = null;
            }
            retryCancellation.Dispose();
        }
    }

    private Task StartProjectionRetry(PublishedLocalizationGeneration<LocalizationGenerationHead> prior,
        CancellationToken retryToken)
    {
        var head = prior.Head;
        StartOwnedEnrichment(runToken => RebuildSearchProjectionAsync(head, prior, enrichmentProgress, runToken), retryToken);
        return EnrichmentTask;
    }

    private Task StartSourceRetry(CancellationToken retryToken)
    {
        StartOwnedEnrichment(runToken => EnrichAsync(enrichmentProgress, runToken), retryToken);
        return EnrichmentTask;
    }

    private void ResetSourceProgress()
    {
        lock (stateGate)
        {
            sourceCounts.Clear();
            languages.Clear();
            kindCounts.Clear();
            statusCounts.Clear();
            versionCounts.Clear();
            diagnosticCounts.Clear();
            inputDigests.Clear();
            recordCount = 0;
            completedResources = 0;
            metadataContentId = null;
            complete = false;
            unavailable = false;
            Sources = BuildSourceChoices(snapshot.IndexContentId, Resources, sourceCounts);
        }
    }

    private async Task VerifySameSnapshotAsync(CancellationToken token)
    {
        var revisions = sourceRevisions ?? new FileRevisionProvider();
        Dictionary<long, string> expected;
        lock (stateGate)
            expected = inputDigests.Values
                .Where(input => input.Status == LocalizationInputStatus.Read && input.Sha256 is not null)
                .ToDictionary(input => input.ResourceOrdinal, input => input.Sha256!);
        var verifiedRows = Resources.Where(resource => expected.ContainsKey(resource.Ordinal)).ToArray();
        await using var verification = await new LocalizationPayloadVerifier().VerifyAsync(
            snapshot, expected, verifiedRows, revisions, token).ConfigureAwait(false);
        if (!verification.DigestsMatch)
            throw new ArchiveException("SourceChanged", "Localization inputs no longer match this session's validated snapshot.");
        verification.EnsureUnchanged();
        foreach (var volume in snapshot.Volumes)
        {
            token.ThrowIfCancellationRequested();
            if (!SourceIdentity.SameStamp(volume.Stamp, revisions.Capture(volume.FilePath)))
                throw new ArchiveException("SourceChanged", "An archive volume no longer matches the selected localization snapshot.");
        }
    }

    private async Task DiscardStagedGenerationAsync()
    {
        await featureGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { stagedGeneration?.Dispose(); stagedGeneration = null; }
        finally { featureGate.Release(); }
    }

    private static bool IsProducerCancellation(Exception error, CancellationToken token) =>
        token.IsCancellationRequested && error is OperationCanceledException;
}
