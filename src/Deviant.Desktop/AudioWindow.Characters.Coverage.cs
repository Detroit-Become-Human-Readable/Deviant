using System.IO;
using Deviant.Assets;
using Deviant.Browser;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private int characterCoverageRestoreCursor;
    private readonly HashSet<ResourceIdentity> coveredCharacterCatalogs = [];
    private IDisposable? characterCoverageRetention;

    private void ApplyRestoredCharacterCoverage(CharacterCatalogCoverageRestoreResult restored,
        CatalogReferenceProvider references)
    {
        if (restored.CoveredCatalogs.Count > 0 && characterCoverageRetention is null)
            characterCoverageRetention = references.SharedMemoryBudget.Reserve(
                checked(4096L + references.CatalogIdentities.Count * 256L), "character coverage state");
        coveredCharacterCatalogs.Clear();
        coveredCharacterCatalogs.UnionWith(restored.CoveredCatalogs);
    }

    private int FirstUncoveredCharacterCatalog(CatalogReferenceProvider references, IReadOnlySet<int>? attempted = null)
    {
        var index = 0;
        while (index < references.CatalogIdentities.Count &&
            (coveredCharacterCatalogs.Contains(references.CatalogIdentities[index]) || attempted?.Contains(index) == true)) index++;
        return index;
    }

    private async Task RestoreProductionCharacterCoverageAsync(CharacterCatalogSession catalog,
        CatalogReferenceProvider references, Func<bool> isCurrent, CancellationToken token, Action? batchRestored = null)
    {
        using var progress = new CoalescedProgress<CharacterCatalogCoverageRestoreProgress>(value => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!isCurrent() || characterCatalogCancellation is not { IsCancellationRequested: false }) return;
            charactersView.SetStatus($"Restoring names · group {value.NextCatalogIndex:N0} of {value.TotalCatalogs:N0}");
            characterCatalogActivity = new(true, "Restoring names", value.NextCatalogIndex, value.TotalCatalogs);
            RefreshActivity();
        })));
        while (characterCoverageRestoreCursor < references.CatalogIdentities.Count)
        {
            using var restored = await Task.Run(() => catalog.RestoreNameCoverageAsync(characterCoverageRestoreCursor, 8, progress, token), token);
            token.ThrowIfCancellationRequested();
            if (!isCurrent()) return;
            if (restored.NextCatalogIndex <= characterCoverageRestoreCursor)
                throw new InvalidDataException("The model cache check did not advance.");
            ApplyRestoredCharacterCoverage(restored, references);
            characterCoverageRestoreCursor = restored.NextCatalogIndex;
            batchRestored?.Invoke();
        }
        nextCharacterCatalogIndex = FirstUncoveredCharacterCatalog(references);
        // A prior discovery may have committed just before its UI request was cancelled.
        while (nextCharacterCatalogIndex < references.CatalogIdentities.Count)
        {
            var index = nextCharacterCatalogIndex;
            using var restored = await Task.Run(() => catalog.RestoreNameCoverageAsync(index, 1, token: token), token);
            token.ThrowIfCancellationRequested();
            if (!isCurrent()) return;
            ApplyRestoredCharacterCoverage(restored, references);
            nextCharacterCatalogIndex = FirstUncoveredCharacterCatalog(references);
            if (nextCharacterCatalogIndex == index) break;
        }
    }
}
