namespace Deviant.Browser;

public sealed partial class AudioExplorerSession
{
    public async Task PrepareCategoryChoicesAsync(CancellationToken token = default)
    {
        await PrepareAudioCategoriesAsync(token).ConfigureAwait(false);
        lock (catalogGate) recoveryFilterChoiceEpoch = -1;
        await RestoreRecoveryFilterChoicesAsync(token).ConfigureAwait(false);
    }

    private async Task PrepareAudioCategoriesAsync(CancellationToken token)
    {
        using var owner = AcquireSharedFeatureStore();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, owner.SourceCancellation);
        await Task.Run(() => owner.Store.PrepareAudioCategories(owner.Revision, BrowseScope,
            RecoveryFeaturePrefix, RecoveryVersion, linked.Token), linked.Token).ConfigureAwait(false);
    }
}
