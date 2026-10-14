using System.Windows;
using System.Windows.Threading;
using Deviant.Browser;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private readonly DispatcherTimer activityTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private CoalescedProgress<AudioUpdateEnvelope>? audioUpdates;
    private CoalescedProgress<AudioExplorerProgress>? archiveProgress;
    private bool foregroundBusy, indexOpeningActive;
    private string foregroundStage = "Ready";
    private long foregroundCompleted, foregroundTotal;
    private long displayedAudioGeneration = -1;
    private AudioExplorerSession? displayedAudioSession;
    private bool workspaceActivityDisplayed;
    private WorkspaceActivityProgress? displayedChildActivity;
    private string? displayedActivityView;

    private sealed record AudioUpdateEnvelope(AudioExplorerSession Session, AudioExplorerUpdate Update, long Opening);

    private void InitializeActivity()
    {
        activityTimer.Tick += (_, _) =>
        {
            RefreshActivity();
        };
        audioUpdates = new(value => Dispatcher.BeginInvoke(new Action(async () => await ApplyAudioUpdateAsync(value))));
        sceneView.ActivityProgressChanged += SceneActivityChanged;
        modelView.ActivityProgressChanged += (_, _) => { if (currentView is "model" or "characters") RefreshActivity(); };
        textureExplorer.NativeImageBrowser.ActivityProgressChanged += (_, _) => { if (currentView == "textures") RefreshActivity(); };
        stringsView.ActivityProgressChanged += (_, _) => { if (currentView is "strings" or "audio") RefreshActivity(); };
        Loaded += (_, _) => { activityTimer.Start(); RefreshHomeState(); };
    }

    private bool AudioIndexingActive => session is { IsEnrichmentStarted: true, IsEnrichmentComplete: false } current &&
        !current.EnrichmentTask.IsCompleted;

    private bool TextureIndexingActive => textureCatalogs?.Progress.State == TextureCatalogSessionState.Running ||
        textureDiscovery?.Progress.IsRunning == true;

    private void SceneActivityChanged(object? sender, SceneActivityProgress progress)
    {
        if (closingInProgress || currentView != "scenes") return;
        if (!progress.IsActive) StatusText.Text = progress.Stage switch
        {
            "Ready" => "Scene ready",
            "Cancelled" => "Scene loading cancelled",
            "Failed" => "Scene loading failed",
            _ => progress.Stage
        };
        RefreshActivity();
    }

    private void RefreshActivity()
    {
        if (closingInProgress) return;
        var active = false;
        var stage = StatusText.Text;
        var childActivity = GetWorkspaceActivity();
        var childChanged = displayedActivityView != currentView || displayedChildActivity != childActivity;
        long completed = 0, total = 0;
        if (foregroundBusy)
        {
            active = true; stage = foregroundStage; completed = foregroundCompleted; total = foregroundTotal;
        }
        else if (currentView == "physics" && physicsBusy)
        {
            active = true; stage = physicsStatus.Text;
        }
        else if (currentView == "scenes" && sceneView.ActivityProgress is { IsActive: true } scene)
        {
            active = true; stage = scene.Stage; completed = scene.Completed; total = scene.Total;
        }
        else if (childActivity is { IsActive: true } child)
        {
            active = true; stage = child.Stage; completed = child.Completed; total = child.Total;
        }
        else if (currentView is "audio" or "home" && AudioIndexingActive && session is { } current)
        {
            var progress = current.CurrentProgress;
            active = true; stage = progress.Phase; completed = progress.Completed; total = progress.Total;
        }
        else if (currentView == "textures" && TextureIndexingActive)
        {
            active = true;
            if (textureCatalogs?.Progress is { State: TextureCatalogSessionState.Running } catalogs)
            {
                stage = "Reading image catalogs"; completed = catalogs.CatalogsVisited; total = catalogs.TotalCatalogs;
            }
            else if (textureDiscovery?.Progress is { } images)
            {
                stage = "Reading image details"; completed = images.Completed; total = images.Total;
            }
        }
        WindowProcessing.IsActive = active;
        ProgressBar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        ProgressBar.IsIndeterminate = active && total <= 0;
        ProgressBar.Maximum = Math.Max(1, total);
        ProgressBar.Value = Math.Clamp(completed, 0, Math.Max(1, total));
        WorkspaceCancelButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceCancelButton.IsEnabled = !workspaceCancellationActive;
        if (active) StatusText.Text = FormatActivity(stage, completed, total);
        else if ((workspaceActivityDisplayed || childChanged) && childActivity is { IsActive: false, Stage.Length: > 0 } settled) StatusText.Text = settled.Stage;
        else if (workspaceActivityDisplayed) StatusText.Text = currentView == "audio" ? audioResultStatus : "Ready";
        workspaceActivityDisplayed = active;
        displayedChildActivity = childActivity;
        displayedActivityView = currentView;
        RefreshHomeState();
    }

    private WorkspaceActivityProgress? GetWorkspaceActivity() => currentView switch
    {
        "scenes" => new(sceneView.ActivityProgress.IsActive, sceneView.ActivityProgress.Stage switch
            { "Ready" => "Scene ready", "Cancelled" => "Scene loading cancelled", "Failed" => "Scene loading failed", var value => value },
            sceneView.ActivityProgress.Completed, sceneView.ActivityProgress.Total),
        "model" => new(modelView.ActivityProgress.IsActive, modelView.ActivityProgress.Stage, modelView.ActivityProgress.Completed, modelView.ActivityProgress.Total),
        "physics" => new(physicsBusy, physicsStatus.Text),
        "strings" => stringsView.ActivityProgress,
        "characters" when characterCatalogTasks.Any(task => !task.IsCompleted) => characterCatalogActivity with { IsActive = true },
        "characters" when characterBindingTasks.Any(task => !task.IsCompleted) => new(true, "Reading model details"),
        "characters" when modelView.IsLoading => new(true, modelView.ActivityProgress.Stage, modelView.ActivityProgress.Completed, modelView.ActivityProgress.Total),
        "characters" => new(charactersView.IsPreviewLoading, charactersView.ActivityStatus),
        "audio" when audioDialogueTasks.Any(task => !task.IsCompleted) => stringsView.ActivityProgress.IsActive
            ? stringsView.ActivityProgress : audioDialogueActivity with { IsActive = true },
        "animation" => animationView.ActivityProgress,
        "shader" => shaderView.ActivityProgress,
        "references" => referencesView.ActivityProgress,
        "buffer" => bufferView.ActivityProgress,
        "dds" => ddsView.ActivityProgress,
        "textures" when textureExplorer.ActivityProgress.IsActive => textureExplorer.ActivityProgress,
        _ => null
    };

    private static string FormatActivity(string stage, long completed, long total) =>
        total > 0 ? $"{stage} · {completed:N0} / {total:N0}" : stage;

    private void ReceiveArchiveProgress(AudioExplorerProgress progress, long opening)
    {
        if (opening != openGeneration || closingInProgress) return;
        if (indexOpeningActive)
        {
            foregroundStage = progress.Phase;
            foregroundCompleted = progress.Completed;
            foregroundTotal = progress.Total;
        }
        RefreshActivity();
    }

    private async Task ApplyAudioUpdateAsync(AudioUpdateEnvelope envelope)
    {
        var current = envelope.Session;
        var update = envelope.Update;
        if (closingInProgress || envelope.Opening != openGeneration || !ReferenceEquals(current, session)) return;
        var changed = !ReferenceEquals(displayedAudioSession, current) || displayedAudioGeneration != update.Generation;
        if (changed)
        {
            displayedAudioSession = current;
            displayedAudioGeneration = update.Generation;
            UpdateFilterChoices(current);
            UpdateExplorerGroups();
            if (currentView == "audio") await QueryAsync(resetOffset: false);
        }
        if (update.IsComplete && currentView is "audio" or "home") StatusText.Text = $"{update.MediaCount:N0} audio resources ready";
        RefreshActivity();
    }

    private void DisposeActivity()
    {
        activityTimer.Stop();
        audioUpdates?.Dispose();
        archiveProgress?.Dispose();
        sceneView.ActivityProgressChanged -= SceneActivityChanged;
        HomeProcessing.Dispose();
    }
}
