using Deviant.Assets;
using Deviant.Browser;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private async Task PrepareSelectedAudioExportAsync(bool allowPartial)
    {
        if (session is not { } owner) return;
        var keys = allowPartial
            ? CurrentRow is { } row ? new[] { row.Identity.Key } : Array.Empty<string>()
            : selectedIdentities.ToArray();
        var navigation = navigationGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(navigationCancellation.Token);
        jobCancellation?.Cancel();
        jobCancellation = cancellation;
        SetBusy(true, "Preparing selected export…");
        try
        {
            var rows = await owner.ReadSelectionAsync(keys, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(owner, session) || navigation != navigationGeneration) return;
            var review = owner.Review(rows);
            SetBusy(false, "Export ready");
            await ExportAsync(review, allowPartial, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(owner, session) && navigation == navigationGeneration)
            {
                SetBusy(false, "Export cancelled.");
                StatusText.Text = "Export cancelled.";
            }
        }
        catch (Exception error)
        {
            WorkflowDiagnostics.Failure("Audio.ExportSelection", error, phase: "Preparing selected export");
            if (ReferenceEquals(owner, session) && navigation == navigationGeneration)
            {
                SetBusy(false, error.Message);
                StatusText.Text = error.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(jobCancellation, cancellation))
            {
                jobCancellation = null;
                if (foregroundBusy) SetBusy(false, StatusText.Text);
            }
        }
    }

    private CancellationTokenSource? selectedAudioDetailsCancellation;
    private ResourceIdentity? selectedAudioDetailsIdentity;
    private AudioExplorerSession? selectedAudioDetailsOwner;
    private long selectedAudioDetailsGeneration;
    private CancellationTokenSource? recoveryFilterCancellation;
    private AudioExplorerSession? recoveryFilterOwner;
    private Task? recoveryFilterTask;

    private void RefreshSavedAudioFilters(AudioExplorerSession owner)
    {
        if (owner.IsCatalogLoaded || currentView != "audio" ||
            ReferenceEquals(recoveryFilterOwner, owner) && recoveryFilterTask is { IsCompleted: false }) return;
        recoveryFilterCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(navigationCancellation.Token);
        recoveryFilterCancellation = cancellation;
        recoveryFilterOwner = owner;
        recoveryFilterTask = TrackLibraryTask(RestoreSavedAudioFiltersAsync(owner, cancellation));
    }

    private async Task RestoreSavedAudioFiltersAsync(AudioExplorerSession owner, CancellationTokenSource cancellation)
    {
        try
        {
            await owner.RestoreRecoveryFilterChoicesAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(owner, session) || currentView != "audio") return;
            UpdateFilterChoices(owner);
            UpdateExplorerGroups();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { WorkflowDiagnostics.Failure("Audio.FilterChoices", error, phase: "Reading saved filters"); }
        finally
        {
            if (ReferenceEquals(recoveryFilterCancellation, cancellation)) recoveryFilterCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelSelectedAudioDetails()
    {
        ++selectedAudioDetailsGeneration;
        selectedAudioDetailsCancellation?.Cancel();
        selectedAudioDetailsIdentity = null;
        selectedAudioDetailsOwner = null;
    }

    private void EnsureSelectedAudioDetails(AudioExplorerRow? row)
    {
        if (row is null || session is not { } owner || row.SelectedSourceVerified ||
            ReferenceEquals(selectedAudioDetailsOwner, owner) && selectedAudioDetailsIdentity == row.Identity) return;
        CancelSelectedAudioDetails();
        selectedAudioDetailsOwner = owner;
        selectedAudioDetailsIdentity = row.Identity;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(navigationCancellation.Token);
        selectedAudioDetailsCancellation = cancellation;
        var generation = selectedAudioDetailsGeneration;
        _ = TrackLibraryTask(LoadSelectedAudioDetailsAsync(owner, row.Identity, generation, cancellation));
    }

    private async Task LoadSelectedAudioDetailsAsync(AudioExplorerSession owner, ResourceIdentity identity,
        long generation, CancellationTokenSource cancellation)
    {
        try
        {
            var row = await owner.PrepareSelectedMediaAsync(identity, cancellation.Token);
            if (cancellation.IsCancellationRequested || generation != selectedAudioDetailsGeneration ||
                !ReferenceEquals(owner, session) || CurrentRow?.Identity != identity) return;
            selectedAudioRows[identity.Key] = row;
            var visible = CurrentRows;
            if (visible.Any(item => item.Identity == identity))
            {
                var focused = (MediaList.SelectedItem as AudioExplorerRow)?.Identity;
                var rows = visible.Select(item => item.Identity == identity ? row : item).ToArray();
                suppressSelectionTracking = true;
                try
                {
                    MediaList.ItemsSource = rows;
                    MediaList.SelectedItems.Clear();
                    foreach (var item in rows.Where(item => selectedIdentities.Contains(item.Identity.Key)))
                        MediaList.SelectedItems.Add(item);
                    if (focused is not null && rows.FirstOrDefault(item => item.Identity == focused) is { } focus)
                        MediaList.SelectedItem = focus;
                }
                finally { suppressSelectionTracking = false; }
            }
            UpdateSelectedDetails();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            WorkflowDiagnostics.Failure("Audio.Selection", error, phase: "Reading clip details");
            if (generation == selectedAudioDetailsGeneration && ReferenceEquals(owner, session) && CurrentRow?.Identity == identity)
                DetailDiagnostic.Text = error.Message;
        }
        finally
        {
            if (ReferenceEquals(selectedAudioDetailsCancellation, cancellation)) selectedAudioDetailsCancellation = null;
            cancellation.Dispose();
        }
    }
}
