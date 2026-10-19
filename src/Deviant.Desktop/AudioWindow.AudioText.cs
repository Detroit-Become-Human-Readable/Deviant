using System.Windows;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private readonly AudioDialogueView audioDialogueView = new();
    private readonly LocalizationAudioLookup audioDialogueLookup = new();
    private readonly HashSet<Task> audioDialogueTasks = [];
    private CancellationTokenSource audioDialogueSelection = new();
    private CancellationTokenSource? audioDialogueCancellation;
    private ResourceIdentity? audioDialogueIdentity;
    private SourceRevision? audioDialogueRevision;
    private long audioDialogueGeneration;
    private DialogueReturnContext? dialogueReturnContext;
    private PendingDialogueRefresh? pendingDialogueRefresh;
    private sealed record PendingDialogueRefresh(AudioExplorerSession Owner, AudioExplorerRow Row,
        LocalizationSession Localization, long Generation, CancellationToken RequestToken, CancellationToken NavigationToken);
    private WorkspaceActivityProgress audioDialogueActivity = new(false, "Ready");
    public AudioDialogueView AudioDialogueBrowser => audioDialogueView;

    private void InitializeAudioDialogue()
    {
        AudioDialogueHost.Content = audioDialogueView;
        audioDialogueView.TextNavigator = OpenAudioDialogueOccurrenceAsync;
    }

    private ResourceIdentity? DialogueAudioOwner(AudioExplorerRow row)
    {
        if (!row.IsHydrated) return null;
        var current = session;
        var identity = row.Identity;
        if (current is null || identity.IndexContentId != current.Snapshot.IndexContentId ||
            identity.Ordinal < 0 || identity.Ordinal >= current.Snapshot.Records.Count ||
            current.Snapshot.Records[(int)identity.Ordinal].Type != 1033 || row.Media.BankKey.Length != 0 ||
            identity.MemberPath is not (null or "wem") || row.Media.Identity != identity ||
            (row.Media.PayloadIdentity is not null && row.Media.PayloadIdentity != identity)) return null;
        return new(identity.IndexContentId, identity.Ordinal);
    }

    private void UpdateAudioDialogueSelection(AudioExplorerRow? row)
    {
        if (row is null && suppressSelectionTracking) return;
        if (audioDialogueIdentity != row?.Identity || audioDialogueRevision != row?.Descriptor.Revision)
        {
            pendingDialogueRefresh = null;
            audioDialogueSelection.Cancel(); audioDialogueSelection.Dispose(); audioDialogueSelection = new();
            audioDialogueCancellation?.Cancel(); ++audioDialogueGeneration;
            audioDialogueIdentity = row?.Identity; audioDialogueRevision = row?.Descriptor.Revision;
            audioDialogueView.Clear(); audioDialogueView.TextNavigationToken = audioDialogueSelection.Token;
            if (dialogueReturnContext is {} context &&
                (context.AudioIdentity != row?.Identity || context.Revision != row?.Descriptor.Revision))
                dialogueReturnContext = null;
        }
        var owner = row is null ? null : DialogueAudioOwner(row);
        FindDialogueButton.IsEnabled = owner is not null && session is { AreDialogueHeadersReady: true } current &&
            current.HasDialogueLink(owner) != false;
        ReturnToTextButton.Visibility = dialogueReturnContext is null ? Visibility.Collapsed : Visibility.Visible;
        if (row is null) audioDialogueView.SetStatus("Select a clip");
        else if (owner is not null && session?.HasDialogueLink(owner) is null && audioDialogueView.CurrentResult is null)
            audioDialogueView.SetStatus("Find dialogue");
        else if (!FindDialogueButton.IsEnabled && audioDialogueView.CurrentResult is null)
            audioDialogueView.SetStatus(owner is not null && session?.AreDialogueHeadersReady != true
                ? "Reading dialogue links…" : "No dialogue link available.");
    }

    public Task FindSelectedAudioTextAsync(CancellationToken token = default) =>
        TrackJobAsync(() => RunAudioDialogueLookupAsync(token));

    private async Task RunAudioDialogueLookupAsync(CancellationToken token)
    {
        var current = session ?? throw new InvalidOperationException("Open a workspace first.");
        var row = CurrentRow ?? throw new InvalidOperationException("Select a clip first.");
        var owner = DialogueAudioOwner(row) ?? throw new InvalidOperationException("This clip has no dialogue link.");
        pendingDialogueRefresh = null;
        audioDialogueCancellation?.Cancel();
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, audioDialogueSelection.Token, navigationCancellation.Token);
        audioDialogueCancellation = own; jobCancellation = own;
        audioDialogueActivity = new(true, "Reading dialogue");
        var generation = ++audioDialogueGeneration;
        var task = PrepareAndFindAudioDialogueAsync(current, row, owner, generation, own.Token, token);
        audioDialogueTasks.Add(task);
        RefreshActivity();
        try { await task; }
        finally
        {
            audioDialogueTasks.Remove(task);
            if (ReferenceEquals(audioDialogueCancellation, own)) audioDialogueCancellation = null;
            if (ReferenceEquals(jobCancellation, own)) jobCancellation = null;
            if (generation == audioDialogueGeneration) audioDialogueActivity = audioDialogueActivity with { IsActive = false };
            RefreshActivity();
        }
    }

    private async Task PrepareAndFindAudioDialogueAsync(AudioExplorerSession current, AudioExplorerRow row,
        ResourceIdentity owner, long generation, CancellationToken token, CancellationToken requestToken)
    {
        audioDialogueView.SetStatus("Reading dialogue…");
        await current.PrepareDialogueLookupAsync(token);
        token.ThrowIfCancellationRequested();
        if (!AudioDialogueCurrent(current, row.Identity, generation)) return;
        var headers = current.Catalog.DialogueHeaderAssociations.Where(link => link.AudioResolution.Candidates.Contains(owner))
            .Select(link => link.HeaderIdentity).Distinct().ToArray();
        if (headers.Length == 0)
        {
            audioDialogueView.SetStatus(current.AreDialogueHeadersReady ? "No dialogue linked." : "Reading dialogue links…");
            return;
        }
        await FindAudioDialogueCoreAsync(current, row, owner, headers, generation, token, requestToken);
    }

    private bool AudioDialogueCurrent(AudioExplorerSession owner, ResourceIdentity identity, long generation) =>
        !closingInProgress && workspaceTransitionDepth == 0 && currentView == "audio" &&
        ReferenceEquals(session, owner) && CurrentRow?.Identity == identity && generation == audioDialogueGeneration;

    private async Task FindAudioDialogueCoreAsync(AudioExplorerSession owner, AudioExplorerRow row,
        ResourceIdentity audio, ResourceIdentity[] headers, long generation, CancellationToken token, CancellationToken requestToken)
    {
        audioDialogueView.SetStatus("Reading dialogue…");
        using var diagnostic = WorkflowDiagnostics.Begin("Audio.Dialogue", audio.Key);
        try
        {
            if (stringsView.CurrentSession?.Snapshot.IndexContentId != owner.Snapshot.IndexContentId ||
                stringsView.CurrentSession?.Snapshot.PhysicalSourceStamp != owner.Snapshot.PhysicalSourceStamp)
                await stringsView.LoadAsync(owner.Snapshot, cache.Location, token);
            else
                // StringsView owns and drains the retry; selected lookup uses already published rows.
                _ = stringsView.RetryIndexingAsync(navigationCancellation.Token);
            var localization = stringsView.CurrentSession ?? throw new InvalidOperationException("Dialogue is unavailable.");
            token.ThrowIfCancellationRequested();
            if (!AudioDialogueCurrent(owner, row.Identity, generation)) { diagnostic?.Cancel(); return; }
            diagnostic?.Progress("Reading dialogue", 0, null, "occurrences");
            var result = await audioDialogueLookup.FindAudioAsync(localization, audio, headers, token);
            token.ThrowIfCancellationRequested();
            if (AudioDialogueCurrent(owner, row.Identity, generation))
            {
                audioDialogueView.SetResult(result);
                if (!result.IsComplete && result.Diagnostics.Any(issue => issue.Code == "LocalizationPending"))
                {
                    pendingDialogueRefresh = new(owner, row, localization, generation, requestToken, navigationCancellation.Token);
                    RefreshPendingAudioDialogue();
                }
            }
            diagnostic?.Progress("Dialogue available", result.Occurrences.Count, null, "occurrences");
            bool unresolved = result.Occurrences.Count == 0 && result.Diagnostics.Any(issue => issue.Status is
                LocalizationReferenceLinkStatus.Unresolved or LocalizationReferenceLinkStatus.Ambiguous);
            if (result.IsComplete && !unresolved) diagnostic?.Complete(); else diagnostic?.Partial();
        }
        catch (OperationCanceledException)
        {
            diagnostic?.Cancel();
            if (AudioDialogueCurrent(owner, row.Identity, generation)) audioDialogueView.SetStatus("Cancelled");
        }
        catch (Exception error)
        {
            diagnostic?.Fail(error, error is ArchiveException archive ? archive.Code : null);
            if (AudioDialogueCurrent(owner, row.Identity, generation)) audioDialogueView.SetStatus(error.Message);
        }
    }

    private void RefreshPendingAudioDialogue()
    {
        if (pendingDialogueRefresh is not { } pending || !pending.Localization.EnrichmentTask.IsCompleted) return;
        pendingDialogueRefresh = null;
        var task = RefreshCompletedAudioDialogueAsync(pending);
        audioDialogueTasks.Add(task);
        _ = ObserveDialogueRefreshAsync(task);
    }

    private async Task ObserveDialogueRefreshAsync(Task task)
    {
        try { await task; }
        finally { audioDialogueTasks.Remove(task); RefreshActivity(); }
    }

    private async Task RefreshCompletedAudioDialogueAsync(PendingDialogueRefresh pending)
    {
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        if (!AudioDialogueCurrent(pending.Owner, pending.Row.Identity, pending.Generation) ||
            pending.RequestToken.IsCancellationRequested || pending.NavigationToken.IsCancellationRequested) return;
        try
        {
            if (pending.Localization.EnrichmentTask.IsCanceled)
                audioDialogueView.SetStatus("Dialogue indexing cancelled.");
            else if (pending.Localization.EnrichmentTask.IsFaulted || !pending.Localization.IsEnrichmentComplete)
                audioDialogueView.SetStatus("Dialogue unavailable.");
            else await RunAudioDialogueLookupAsync(pending.RequestToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            WorkflowDiagnostics.Failure("Audio.Dialogue", error);
            if (AudioDialogueCurrent(pending.Owner, pending.Row.Identity, pending.Generation)) audioDialogueView.SetStatus(error.Message);
        }
    }

    private async Task OpenAudioDialogueOccurrenceAsync(LocalizationAudioTextOccurrence occurrence, CancellationToken token)
    {
        var owner = session ?? throw new InvalidOperationException("Open a workspace first.");
        var row = CurrentRow ?? throw new InvalidOperationException("Select a clip first.");
        var localization = stringsView.CurrentSession ?? throw new InvalidOperationException("Dialogue is unavailable.");
        var audio = DialogueAudioOwner(row) ?? throw new InvalidOperationException("This clip has no dialogue link.");
        using var selected = CancellationTokenSource.CreateLinkedTokenSource(token, audioDialogueSelection.Token);
        var resolved = await new LocalizationRelationshipCoordinator().ResolveSelectedAsync(localization, occurrence.PointerOccurrence.Identity, selected.Token);
        if (!resolved.Relationships.Any(link => link.AudioResourceIdentity == audio &&
            link.DialogueHeaderIdentity == occurrence.Relationship.DialogueHeaderIdentity && link.EvidencePath.Count > 0 &&
            link.EvidencePath.All(edge => edge.Evidence.Verified && edge.NavigationTarget is not null)))
            throw new ArchiveException("SourceChanged", "The dialogue link changed. Find dialogue again.");
        await using var pin = await localization.AcquirePayloadVerificationAsync([occurrence.TextOccurrence.ResourceIdentity], selected.Token);
        if (!ReferenceEquals(owner, session) || CurrentRow?.Identity != row.Identity) throw new OperationCanceledException(selected.Token);
        dialogueReturnContext = new(row.Identity, occurrence.TextOccurrence.Identity, occurrence.TextOccurrence.ResourceIdentity, row.Descriptor.Revision);
        await OpenDialogueTextAsync(dialogueReturnContext, selected.Token);
        pin.EnsureUnchanged();
    }

    public async Task ReturnToDialogueAsync(CancellationToken token = default)
    {
        var context = dialogueReturnContext ?? throw new InvalidOperationException("Open a linked dialogue entry first.");
        var current = session ?? throw new InvalidOperationException("Open a workspace first.");
        if (context.AudioIdentity != CurrentRow?.Identity || context.Revision != new SourceRevision(current.Snapshot.IndexContentId, current.Snapshot.PhysicalSourceStamp))
            throw new ArchiveException("SourceChanged", "The selected dialogue belongs to another clip or archive revision.");
        await OpenDialogueTextAsync(context, token);
    }

    private async Task OpenDialogueTextAsync(DialogueReturnContext context, CancellationToken token)
    {
        var navigation = BeginNavigation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, navigation.Token);
        await ShowStringsAsync(linked.Token);
        var localization = stringsView.CurrentSession ?? throw new InvalidOperationException("Dialogue is unavailable.");
        await using var pin = await localization.AcquirePayloadVerificationAsync([context.TextOwner], linked.Token);
        await stringsView.SelectRecordAsync(context.TextIdentity, linked.Token);
        pin.EnsureUnchanged();
    }

    private async Task ClearAudioDialogueAsync()
    {
        pendingDialogueRefresh = null;
        audioDialogueSelection.Cancel(); audioDialogueCancellation?.Cancel(); ++audioDialogueGeneration;
        var pending = audioDialogueTasks.ToArray();
        try { await Task.WhenAll(pending); } catch { }
        audioDialogueView.Clear(); audioDialogueIdentity = null; audioDialogueRevision = null; dialogueReturnContext = null;
        ReturnToTextButton.Visibility = Visibility.Collapsed;
    }

    private async void FindDialogue_Click(object sender, RoutedEventArgs e)
    { try { await FindSelectedAudioTextAsync(); } catch (OperationCanceledException) { } catch (Exception error) { audioDialogueView.SetStatus(error.Message); } }
    private async void ReturnToText_Click(object sender, RoutedEventArgs e)
    { try { await ReturnToDialogueAsync(); } catch (OperationCanceledException) { } catch (Exception error) { StatusText.Text = error.Message; } }
    private void CancelDialogue_Click(object sender, RoutedEventArgs e) => audioDialogueCancellation?.Cancel();
    private sealed record DialogueReturnContext(ResourceIdentity AudioIdentity, ResourceIdentity TextIdentity,
        ResourceIdentity TextOwner, SourceRevision Revision);
}
