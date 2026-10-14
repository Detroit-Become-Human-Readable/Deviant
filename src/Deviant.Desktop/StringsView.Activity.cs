namespace Deviant.Desktop;

public partial class StringsView
{
    private sealed record ActivityEntry(string Stage, long Completed, long Total, int Priority);

    private readonly object activityGate = new();
    private readonly Dictionary<long, ActivityEntry> activities = [];
    private long nextActivityId;
    private long newestActivityId;
    private WorkspaceActivityProgress activityProgress = new(false, "Ready");
    private string? terminalActivityStage;
    private long enrichmentActivityId;

    public WorkspaceActivityProgress ActivityProgress
    {
        get { lock (activityGate) return activityProgress; }
    }

    public bool IsActivityActive => ActivityProgress.IsActive;

    public event EventHandler<WorkspaceActivityProgress>? ActivityProgressChanged;

    public void CancelActivity()
    {
        CancelViewRequests();
        session?.CancelEnrichment();
    }

    internal void CancelViewRequests()
    {
        CancelPointerNavigation();
        jobCancellation?.Cancel();
        queryCancellation?.Cancel();
        detailCancellation?.Cancel();
        loadCancellation?.Cancel();
        localizationRetryCancellation?.Cancel();
        if (Dispatcher.CheckAccess()) Status.Text = "Cancelling…";
        else _ = Dispatcher.InvokeAsync(() => Status.Text = "Cancelling…");
    }

    private long BeginActivity(string stage, int priority, long completed = 0, long total = 0)
    {
        long id;
        lock (activityGate)
        {
            id = ++nextActivityId;
            newestActivityId = id;
            activities[id] = new(stage, completed, total, priority);
            terminalActivityStage = null;
        }
        PublishActivity();
        return id;
    }

    private void UpdateActivity(long id, string stage, long completed = 0, long total = 0)
    {
        lock (activityGate)
        {
            if (!activities.TryGetValue(id, out var current)) return;
            activities[id] = current with { Stage = stage, Completed = completed, Total = total };
        }
        PublishActivity();
    }

    private void EndActivity(long id, string? terminalStage = null)
    {
        if (id == 0) return;
        lock (activityGate)
        {
            if (!activities.Remove(id)) return;
            if (terminalStage is not null && id == newestActivityId) terminalActivityStage = terminalStage;
        }
        PublishActivity();
    }

    private void PublishActivity()
    {
        WorkspaceActivityProgress update;
        lock (activityGate)
        {
            var current = activities.OrderByDescending(item => item.Value.Priority)
                .ThenByDescending(item => item.Key).Select(item => item.Value).FirstOrDefault();
            update = current is null
                ? new(false, terminalActivityStage ?? "Ready")
                : new(true, current.Stage, current.Completed, current.Total);
            activityProgress = update;
        }

        if (Dispatcher.CheckAccess()) ActivityProgressChanged?.Invoke(this, update);
        else _ = Dispatcher.BeginInvoke(new Action(() => ActivityProgressChanged?.Invoke(this, update)));
    }
}
