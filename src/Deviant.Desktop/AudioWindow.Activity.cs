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

    private sealed record AudioUpdateEnvelope(AudioExplorerSession Session, AudioExplorerUpdate Update, long Opening);

    private void InitializeActivity()
    {
        activityTimer.Tick += (_, _) =>
        {
            if (foregroundBusy || sceneView.IsLoading || AudioIndexingActive || TextureIndexingActive) RefreshActivity();
        };
        audioUpdates = new(value => Dispatcher.BeginInvoke(new Action(async () => await ApplyAudioUpdateAsync(value))));
        sceneView.ActivityProgressChanged += SceneActivityChanged;
        Loaded += (_, _) => { activityTimer.Start(); RefreshHomeState(); };
    }

    private bool AudioIndexingActive => session is { IsEnrichmentStarted: true, IsEnrichmentComplete: false } current &&
        !current.EnrichmentTask.IsCompleted;

    private bool TextureIndexingActive => textureCatalogs?.Progress.State == TextureCatalogSessionState.Running ||
        textureDiscovery?.Progress.IsRunning == true;

    private void SceneActivityChanged(object? sender, SceneActivityProgress progress)
    {
        if (closingInProgress || currentView is not ("scenes" or "model")) return;
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
        long completed = 0, total = 0;
        if (foregroundBusy)
        {
            active = true; stage = foregroundStage; completed = foregroundCompleted; total = foregroundTotal;
        }
        else if (currentView is "scenes" or "model" && sceneView.ActivityProgress is { IsActive: true } scene)
        {
            active = true; stage = scene.Stage; completed = scene.Completed; total = scene.Total;
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
        if (active) StatusText.Text = FormatActivity(stage, completed, total);
        RefreshHomeState();
    }

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
