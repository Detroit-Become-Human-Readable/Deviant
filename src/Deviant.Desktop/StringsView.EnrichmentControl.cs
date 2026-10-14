using Deviant.Browser;

namespace Deviant.Desktop;

public partial class StringsView
{
    private CancellationTokenSource? localizationRetryCancellation;
    private Task localizationRetryTask = Task.CompletedTask;

    public Task RetryIndexingAsync(CancellationToken token = default)
    {
        var current = session;
        if (current is null || current.IsEnrichmentComplete ||
            (!current.EnrichmentTask.IsCanceled && !current.EnrichmentTask.IsFaulted && current.Status != "Cancelled"))
            return Task.CompletedTask;
        localizationRetryCancellation?.Cancel();
        return localizationRetryTask = RetryIndexingCoreAsync(current, token);
    }

    private async Task RetryIndexingCoreAsync(LocalizationSession current, CancellationToken token)
    {
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token);
        localizationRetryCancellation = own;
        var activity = BeginActivity("Indexing text", 80, current.CompletedResources, current.Resources.Count);
        enrichmentActivityId = activity;
        string? terminal = null;
        try
        {
            await current.RetryEnrichmentAsync(own.Token);
            own.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(session, current)) return;
            UpdateChoices();
            await RefreshAsync();
        }
        catch (OperationCanceledException) { if (ReferenceEquals(session, current)) terminal = "Cancelled"; }
        catch (Exception error)
        {
            if (ReferenceEquals(session, current)) { terminal = "Failed"; Status.Text = error.Message; }
        }
        finally
        {
            if (enrichmentActivityId == activity) enrichmentActivityId = 0;
            if (ReferenceEquals(localizationRetryCancellation, own)) localizationRetryCancellation = null;
            EndActivity(activity, terminal);
        }
    }

    private async Task DrainLocalizationRetryAsync()
    {
        localizationRetryCancellation?.Cancel();
        try { await localizationRetryTask; } catch { }
    }
}
