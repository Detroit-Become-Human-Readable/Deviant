namespace Deviant.Desktop;

public partial class NativeTextureView
{
    private sealed record ActivityEntry(string Stage, int Priority);

    private readonly object activityGate = new();
    private readonly Dictionary<long, ActivityEntry> activities = [];
    private readonly HashSet<Task> activityTasks = [];
    private long nextActivityId;
    private long newestActivityId;
    private long imageActivityId;
    private string? terminalActivityStage;
    private WorkspaceActivityProgress activityProgress = new(false, "Ready");

    public WorkspaceActivityProgress ActivityProgress
    {
        get { lock (activityGate) return activityProgress; }
    }

    public bool IsActivityActive => ActivityProgress.IsActive;

    public event EventHandler<WorkspaceActivityProgress>? ActivityProgressChanged;

    public void CancelActivity()
    {
        lock (activityGate) terminalActivityStage = "Cancelled";
        var current = selection;
        selection = new();
        current.Cancel();
        current.Dispose();
        images.CancelActivity();
        if (Dispatcher.CheckAccess()) Status.Text = "Cancelling…";
        else _ = Dispatcher.BeginInvoke(new Action(() => Status.Text = "Cancelling…"));
        PublishActivity();
    }

    private void UpdateImageActivity()
    {
        lock (activityGate)
        {
            if (images.IsBusy && imageActivityId == 0)
            {
                imageActivityId = ++nextActivityId;
                newestActivityId = imageActivityId;
                activities[imageActivityId] = new("Rendering image", 10);
                terminalActivityStage = null;
            }
            else if (!images.IsBusy && imageActivityId != 0)
            {
                activities.Remove(imageActivityId);
                if (imageActivityId == newestActivityId && terminalActivityStage is null)
                    terminalActivityStage = "Ready";
                imageActivityId = 0;
            }
            else return;
        }
        PublishActivity();
    }

    private Task<T> TrackActivity<T>(string stage, int priority, Task<T> task)
    {
        TrackActivity(stage, priority, (Task)task);
        return task;
    }

    private Task TrackActivity(string stage, int priority, Task task)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long id;
        lock (activityGate)
        {
            id = ++nextActivityId;
            newestActivityId = id;
            activities[id] = new(stage, priority);
            activityTasks.Add(completion.Task);
            terminalActivityStage = null;
        }
        try { PublishActivity(); }
        finally { _ = ObserveActivityAsync(task, id, completion); }
        return task;
    }

    private async Task ObserveActivityAsync(Task task, long id, TaskCompletionSource completion)
    {
        string? terminal = null;
        try { await task; }
        catch (OperationCanceledException) { terminal = "Cancelled"; }
        catch { terminal = "Failed"; }
        finally
        {
            lock (activityGate)
            {
                activities.Remove(id);
                if (terminal is not null && id == newestActivityId) terminalActivityStage = terminal;
                else if (terminal is null && id == newestActivityId && terminalActivityStage != "Cancelled") terminalActivityStage = "Ready";
            }
            try { PublishActivity(); }
            finally
            {
                lock (activityGate)
                {
                    activityTasks.Remove(completion.Task);
                    completion.TrySetResult();
                }
            }
        }
    }

    private async Task DrainActivityAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (activityGate) pending = activityTasks.ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending);
        }
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
                : new(true, current.Stage);
            activityProgress = update;
        }
        if (Dispatcher.CheckAccess()) ActivityProgressChanged?.Invoke(this, update);
        else _ = Dispatcher.BeginInvoke(new Action(() => ActivityProgressChanged?.Invoke(this, update)));
    }
}
