namespace Deviant.Desktop;

public partial class AssetModelView
{
    private string modelActivityTerminalStage = "Ready";
    public bool IsLoading => loading;
    public event EventHandler<SceneActivityProgress>? ActivityProgressChanged;
    public SceneActivityProgress ActivityProgress { get; private set; } = new(false, "Ready", 0, 0);
    public void CancelActivity() => loadCancellation?.Cancel();

    private void BeginModelActivity()
    {
        modelActivityTerminalStage = "Ready";
        PublishModelActivity("Preparing model");
    }

    private void PublishModelActivity(string stage, int completed = 0, int total = 0)
    {
        var knownTotal = Math.Max(total, 0);
        var progress = new SceneActivityProgress(loading, stage,
            knownTotal > 0 ? Math.Clamp(completed, 0, knownTotal) : Math.Max(completed, 0), knownTotal);
        if (progress == ActivityProgress) return;
        ActivityProgress = progress;
        ActivityProgressChanged?.Invoke(this, progress);
    }
}
