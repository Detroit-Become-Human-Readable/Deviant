using Deviant.Catalog;

namespace Deviant.Browser;

public sealed partial class TextureDiscoverySession
{
    private Task summaryPreparation = Task.CompletedTask;
    private long summaryPreparationRequested;

    private void ScheduleSummaryPreparation()
    {
        lock (gate)
        {
            if (disposed || enrichmentCancellation.IsCancellationRequested || !projectionReady || features is not { } store) return;
            summaryPreparationRequested++;
            if (!summaryPreparation.IsCompleted) return;
            summaryPreparation = Task.Run(() => DrainSummaryPreparationAsync(store));
        }
    }

    private async Task DrainSummaryPreparationAsync(FeatureStore store)
    {
        while (true)
        {
            long requested;
            lock (gate) requested = summaryPreparationRequested;
            try
            {
                await store.PrepareTextureBrowseSummaryAsync(ProjectionProfile, enrichmentCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (enrichmentCancellation.IsCancellationRequested) { }
            catch (Exception error) { lock (gate) blockers.Add("Metadata cache: " + error.Message); }
            lock (gate)
            {
                if (!disposed && !enrichmentCancellation.IsCancellationRequested && requested != summaryPreparationRequested) continue;
                summaryPreparation = Task.CompletedTask;
                return;
            }
        }
    }
}
