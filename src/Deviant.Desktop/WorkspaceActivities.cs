namespace Deviant.Desktop;

public interface IWorkspaceActivitySource
{
    WorkspaceActivityProgress ActivityProgress { get; }
    void CancelActivity();
}

public partial class AnimationRecordView : IWorkspaceActivitySource
{
    public WorkspaceActivityProgress ActivityProgress => new(
        loadCancellation is not null || queryCancellation is not null || selectionCancellation is not null || activeJob is { IsCompleted: false },
        activeJob is { IsCompleted: false } ? "Exporting animation record" : loadCancellation is not null ? "Reading animation records" : selectionCancellation is not null ? "Reading animation details" : queryCancellation is not null ? "Searching animation records" : Status.Text);
    public void CancelActivity() { loadCancellation?.Cancel(); selectionCancellation?.Cancel(); queryCancellation?.Cancel(); jobCancellation?.Cancel(); }
}

public partial class StoredShaderView : IWorkspaceActivitySource
{
    public WorkspaceActivityProgress ActivityProgress => new(
        loadCancellation is not null || queryCancellation is not null || activeJob is { IsCompleted: false },
        activeJob is { IsCompleted: false } ? "Exporting shader" : loadCancellation is not null ? "Reading shader sources" : queryCancellation is not null ? "Searching shader sources" : Status.Text);
    public void CancelActivity() { loadCancellation?.Cancel(); queryCancellation?.Cancel(); jobCancellation?.Cancel(); }
}

public partial class SceneReferencesView : IWorkspaceActivitySource
{
    public WorkspaceActivityProgress ActivityProgress => new(
        loadCancellation is not null || queryCancellation is not null || activeJob is { IsCompleted: false } || navigationTasks.Any(task => !task.IsCompleted),
        activeJob is { IsCompleted: false } ? "Exporting references" : loadCancellation is not null ? "Reading references" : queryCancellation is not null ? "Searching references" : navigationTasks.Any(task => !task.IsCompleted) ? "Opening resource" : Status.Text);
    public void CancelActivity() { loadCancellation?.Cancel(); queryCancellation?.Cancel(); jobCancellation?.Cancel(); navigationCancellation?.Cancel(); }
}

public partial class BufferInspectionView : IWorkspaceActivitySource
{
    public WorkspaceActivityProgress ActivityProgress => new(
        loadCancellation is not null || operations.Any(task => !task.IsCompleted),
        exportTask is { IsCompleted: false } ? "Exporting resource" : loadCancellation is not null ? "Reading resource" : operations.Any(task => !task.IsCompleted) ? "Reading resource details" : Status.Text);
    public void CancelActivity() { loadCancellation?.Cancel(); pageCancellation?.Cancel(); jobCancellation?.Cancel(); navigationCancellation?.Cancel(); CancellationRequested?.Invoke(this, EventArgs.Empty); }
}

public partial class AssetFamilyView : IWorkspaceActivitySource
{
    public WorkspaceActivityProgress ActivityProgress => new(
        loadCancellation is not null || operations.Any(task => !task.IsCompleted),
        activeExport is { IsCompleted: false } ? "Exporting resources" : loadCancellation is not null ? "Reading resources" : operations.Any(task => !task.IsCompleted) ? "Reading resource details" : Status.Text);
    public void CancelActivity() { loadCancellation?.Cancel(); queryCancellation?.Cancel(); exportCancellation?.Cancel(); CancelNativeTextures(); DiscoveryCancel?.Invoke(); }
}

public partial class DdsImageView : IWorkspaceActivitySource
{
    public WorkspaceActivityProgress ActivityProgress => new(operations.Any(task => !task.IsCompleted),
        openTask is { IsCompleted: false } ? "Opening image" : operations.Any(task => !task.IsCompleted) ? "Preparing image" : Status.Text);
    public void CancelActivity() { openCancellation?.Cancel(); selectionCancellation?.Cancel(); renderCancellation?.Cancel(); }
}

public partial class TextureExplorerView : IWorkspaceActivitySource
{
    public WorkspaceActivityProgress ActivityProgress => nativeView.ActivityProgress.IsActive ? nativeView.ActivityProgress :
        new(operations.Any(task => !task.IsCompleted), exportTask is { IsCompleted: false } ? "Exporting texture" : operations.Any(task => !task.IsCompleted) ? "Reading texture list" : Status.Text);
    public void CancelActivity() { nativeView.CancelActivity(); Cancel_Click(this, new()); }
}

public partial class SceneView
{
    public void CancelActivity() => Cancel_Click(this, new());
}

public partial class CharactersView
{
    public bool IsPreviewLoading => previewTasks.Any(task => !task.IsCompleted);
    public string ActivityStatus => !string.IsNullOrWhiteSpace(PreviewStatus.Text) ? PreviewStatus.Text : Status.Text;
}

