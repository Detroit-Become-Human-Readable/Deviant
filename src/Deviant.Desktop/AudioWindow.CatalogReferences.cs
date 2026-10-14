using Deviant.Browser;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private CatalogReferenceProvider? sharedCatalogReferences;
    private SharedFeatureStoreLease? catalogFeatureStoreLease;

    public CatalogReferenceProvider? SharedCatalogReferences => sharedCatalogReferences;

    private CatalogReferenceProvider EnsureSharedCatalogReferences(AudioExplorerSession owner)
    {
        Dispatcher.VerifyAccess();
        if (!ReferenceEquals(owner, session) || closingInProgress)
            throw new OperationCanceledException();
        if (sharedCatalogReferences is { } existing)
        {
            if (!ReferenceEquals(existing.Snapshot, owner.Snapshot))
                throw new InvalidOperationException("The catalog belongs to another workspace.");
            return existing;
        }
        var borrowed = owner.AcquireSharedFeatureStore();
        try
        {
            renderMemoryBudget ??= new();
            var provider = new CatalogReferenceProvider(owner.Snapshot, renderMemoryBudget, borrowed.Store);
            catalogFeatureStoreLease = borrowed;
            sharedCatalogReferences = provider;
            return provider;
        }
        catch { borrowed.Dispose(); throw; }
    }

    private async Task ClearSharedCatalogReferencesAsync()
    {
        var provider = sharedCatalogReferences; sharedCatalogReferences = null;
        var storeLease = catalogFeatureStoreLease; catalogFeatureStoreLease = null;
        try { if (provider is not null) await provider.DisposeAsync(); }
        finally { storeLease?.Dispose(); }
    }
}
