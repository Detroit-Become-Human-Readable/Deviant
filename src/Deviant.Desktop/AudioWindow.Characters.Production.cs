using Deviant.Assets;
using Deviant.Browser;
using System.Windows.Threading;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private CharacterCatalogSession? productionCharacters;
    private CharacterCatalogAssetPage? characterAssetPage;
    private CharacterCatalogAssetPage? characterVariantPage;
    private CharacterCatalogSession? characterProvidersOwner;
    private AssetMemoryBudget.Reservation? characterFocusReservation;
    private IReadOnlyList<AssetDescriptor> characterFocusDescriptors = [];
    private long characterResultsRefreshGeneration;
    private int nextCharacterCatalogIndex;
    private readonly HashSet<int> inventoriedCharacterCatalogs = [];
    private const int CharacterResultsPageSize = 200;
    private long characterResultsOffset;
    private long? characterPageBoundary;
    private readonly DispatcherTimer characterSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private void InitializeProductionCharacterSearch()
    {
        charactersView.BrowserQueryChanged += async (_, _) =>
        {
            characterResultsOffset = 0;
            characterPageBoundary = null;
            if (currentView == "characters") await ShowCharactersCoreAsync(CancellationToken.None);
        };
        charactersView.VariantProviderAsync = async (descriptor, token) =>
        {
            var key = descriptor.Metadata.GetValueOrDefault("character.key") ?? descriptor.Metadata.GetValueOrDefault("character.candidate-key");
            if (key is null || productionCharacters is not { } catalog) return [descriptor];
            var result = await catalog.QueryPreparedPageAsync(new(Family: "character-model", Sort: AssetSort.Name, Limit: 512)
                { CharacterGroup = key, ModelNameProfile = catalog.NameProfile, MaximumCatalogRow = characterPageBoundary }, true, token);
            if (token.IsCancellationRequested || !ReferenceEquals(catalog, productionCharacters))
            { result.Page.Dispose(); token.ThrowIfCancellationRequested(); return []; }
            characterVariantPage?.Dispose(); characterVariantPage = result.Page;
            return result.Page.Assets;
        };
        charactersView.SelectedResourceChanged += (_, _) =>
        {
            if (charactersView.SelectedDescriptor is null) { characterVariantPage?.Dispose(); characterVariantPage = null; }
            RefreshCharacterFocusRetention();
        };
        charactersView.RefreshResultsRequested += async (_, _) =>
        {
            if (currentView != "characters" || !IsWorkspaceValidated || session is not { } owner ||
                productionCharacters is not { } catalog) return;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(navigationCancellation.Token,
                characterCatalogCancellation?.Token ?? CancellationToken.None);
            var query = CurrentCharacterResultsQuery();
            var task = PublishCharacterResultsAsync(owner, catalog, query, navigationGeneration,
                characterCatalogGeneration, refreshBoundary: true, cancellation.Token);
            characterCatalogTasks.Add(task);
            try { await task; }
            catch (OperationCanceledException) { }
            catch (Exception error) { NavigationError(error); }
            finally { characterCatalogTasks.Remove(task); }
        };
        charactersView.SearchRequested += (_, _) =>
        {
            if (characterCatalogProviderConfigured || currentView != "characters") return;
            characterResultsOffset = 0;
            characterPageBoundary = null;
            characterSearchTimer.Stop(); characterSearchTimer.Start();
        };
        charactersView.PageRequested += async direction =>
        {
            if (characterCatalogProviderConfigured || currentView != "characters" || !IsWorkspaceValidated) return;
            characterResultsOffset = Math.Max(0, characterResultsOffset + direction * CharacterResultsPageSize);
            try { await ShowCharactersAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { NavigationError(error); }
        };
        characterSearchTimer.Tick += async (_, _) =>
        {
            characterSearchTimer.Stop();
            if (characterCatalogProviderConfigured || currentView != "characters" || !IsWorkspaceValidated) return;
            try { await ShowCharactersAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { NavigationError(error); }
        };
    }

    public CharacterCatalogSession? CharacterCatalog => productionCharacters;

    private CharacterCatalogSession EnsureProductionCharacters(AudioExplorerSession owner)
    {
        if (productionCharacters is { } existing)
        {
            if (!ReferenceEquals(existing.Snapshot, owner.Snapshot)) throw new InvalidOperationException("The model catalog belongs to another workspace.");
            return existing;
        }
        var references = EnsureSharedCatalogReferences(owner);
        var budget = renderMemoryBudget ?? throw new InvalidOperationException("The asset session is unavailable.");
        return productionCharacters = new(owner.Snapshot, references, budget, aliases, catalogFeatureStoreLease!.Store,
            options: new(MaximumCatalogsPerBatch: 16));
    }

    private async Task LoadProductionCharactersAsync(AudioExplorerSession owner, long navigation,
        long generation, bool indexMore, ResourceIdentity? requestedIdentity, CancellationToken token)
    {
        var priorPageBoundary = characterPageBoundary;
        var priorPage = characterAssetPage;
        bool published = false;
        try
        {
            var catalog = EnsureProductionCharacters(owner);
            if (!ReferenceEquals(characterProvidersOwner, catalog))
            {
                CharacterBindingsProvider = ReadProductionCharacterBindingsAsync;
                CharacterModelProvider = ReadProductionCharacterModelAsync;
                charactersView.RigProvider = ReadProductionCharacterRigAsync;
                charactersView.PoseImportProvider = ImportProductionCharacterPoseAsync;
                characterProvidersOwner = catalog;
            }
            charactersView.ExportProvider ??= ExportProductionCharacterFbxAsync;
            var references = sharedCatalogReferences!;
            if (indexMore) characterIndexingCancelledByUser = false;
            if (indexMore) characterPageBoundary = null;
            if (requestedIdentity is not null) { characterResultsOffset = 0; characterPageBoundary = null; }
            var query = new AssetQuery(Search: requestedIdentity is null ? charactersView.SearchQuery : null,
                Offset: requestedIdentity is null ? characterResultsOffset : 0, Limit: CharacterResultsPageSize,
                Sort: charactersView.CatalogSort, Descending: charactersView.CatalogSortDescending,
                ExactKey: requestedIdentity?.Key);
            await catalogFeatureStoreLease!.Store.PrepareCharacterProjectionsAsync(token);
            await PublishPageAsync();
            if (characterIndexingCancelledByUser)
            {
                charactersView.SetStatus("Indexing paused");
                return;
            }
            var attemptedCatalogs = new HashSet<int>(inventoriedCharacterCatalogs);
            nextCharacterCatalogIndex = FirstUncoveredCharacterCatalog(references, attemptedCatalogs);
            int failedCatalogs = 0;
            var lastPublication = System.Diagnostics.Stopwatch.StartNew();
            charactersView.SetIndexingState(true, requestedIdentity is null && nextCharacterCatalogIndex < references.CatalogIdentities.Count);
            while (requestedIdentity is null && nextCharacterCatalogIndex < references.CatalogIdentities.Count)
            {
                token.ThrowIfCancellationRequested();
                using var progress = new CoalescedProgress<CharacterCatalogProgress>(value => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (IsCurrent() && characterCatalogCancellation is { IsCancellationRequested: false })
                    {
                        charactersView.SetStatus($"Indexing names · group {value.NextCatalogIndex:N0} of {value.TotalCatalogs:N0}");
                        characterCatalogActivity = new(true, "Indexing names", value.NextCatalogIndex, value.TotalCatalogs);
                        RefreshActivity();
                    }
                })));
                var catalogIndex = nextCharacterCatalogIndex;
                try
                {
                using var batch = await Task.Run(() => catalog.InventoryNextBatchAsync(catalogIndex, 32, progress, token), token);
                token.ThrowIfCancellationRequested();
                if (!IsCurrent()) return;
                if (batch.NextCatalogIndex <= nextCharacterCatalogIndex) break;
                var checkpointedCatalogs = batch.CheckpointedCatalogs.ToHashSet();
                for (var index = catalogIndex; index < batch.NextCatalogIndex; index++)
                {
                    attemptedCatalogs.Add(index);
                    if (checkpointedCatalogs.Contains(references.CatalogIdentities[index]))
                        inventoriedCharacterCatalogs.Add(index);
                    else
                        failedCatalogs++;
                }
                nextCharacterCatalogIndex = FirstUncoveredCharacterCatalog(references, attemptedCatalogs);
                if ((!published || lastPublication.ElapsedMilliseconds >= 1000) && characterResultsOffset == 0)
                {
                    characterPageBoundary = null;
                    await PublishPageAsync();
                    lastPublication.Restart();
                }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && IsCurrent())
                {
                    await catalog.WaitForForegroundSelectionsAsync(token);
                }
                catch (Exception error) when (error is Deviant.Core.ArchiveException archive && !archive.Code.EndsWith("SourceChanged", StringComparison.Ordinal) ||
                    error is System.IO.InvalidDataException or System.Text.Json.JsonException)
                {
                    System.Diagnostics.Trace.TraceError(error.ToString());
                    failedCatalogs++;
                    attemptedCatalogs.Add(catalogIndex);
                    nextCharacterCatalogIndex = FirstUncoveredCharacterCatalog(references, attemptedCatalogs);
                }
            }
            token.ThrowIfCancellationRequested();
            if (!IsCurrent()) return;
            nextCharacterCatalogIndex = FirstUncoveredCharacterCatalog(references, attemptedCatalogs);
            if (!published || characterResultsOffset == 0)
            { characterPageBoundary = null; await PublishPageAsync(); }
            charactersView.SetStatus($"{attemptedCatalogs.Count:N0} of {references.CatalogIdentities.Count:N0} groups checked" +
                (failedCatalogs == 0 ? "" : $" · {failedCatalogs:N0} groups unavailable"));

            async Task PublishPageAsync()
            {
                published |= await PublishCharacterResultsAsync(owner, catalog, query, navigation, generation,
                    refreshBoundary: false, token);
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent())
            {
                if (!published && ReferenceEquals(characterAssetPage, priorPage)) characterPageBoundary = priorPageBoundary;
                charactersView.SetStatus("Indexing cancelled");
            }
        }
        catch (Exception error) { Deviant.Assets.WorkflowDiagnostics.Failure("Characters.Load", error); System.Diagnostics.Trace.TraceError(error.ToString()); if (IsCurrent()) charactersView.SetStatus(error.Message); }
        finally
        {
            if (IsCurrent()) charactersView.SetIndexingState(true, false);
        }
        bool IsCurrent() => !closingInProgress && workspaceTransitionDepth == 0 && ReferenceEquals(owner, session) &&
            navigation == navigationGeneration && generation == characterCatalogGeneration && currentView == "characters";
    }

    private AssetQuery CurrentCharacterResultsQuery() => new(Search: charactersView.SearchQuery,
        Offset: characterResultsOffset, Limit: CharacterResultsPageSize, Sort: charactersView.CatalogSort,
        Descending: charactersView.CatalogSortDescending);

    private async Task<bool> PublishCharacterResultsAsync(AudioExplorerSession owner, CharacterCatalogSession catalog,
        AssetQuery query, long navigation, long generation, bool refreshBoundary, CancellationToken token)
    {
        var refresh = ++characterResultsRefreshGeneration;
        var store = catalogFeatureStoreLease!.Store;
        var boundary = refreshBoundary || characterPageBoundary is null
            ? await Task.Run(() => store.CaptureAssetPageBoundary(token), token) : characterPageBoundary.Value;
        var bounded = query with { Family = "character-model", ModelNameProfile = catalog.NameProfile, MaximumCatalogRow = boundary };
        while (true)
        {
            var result = await catalog.QueryPreparedPageAsync(bounded, charactersView.ShowsModels || query.ExactKey is not null, token);
            CharacterCatalogAssetPage? page = result.Page;
            try
            {
                token.ThrowIfCancellationRequested();
                if (closingInProgress || workspaceTransitionDepth != 0 || !ReferenceEquals(owner, session) ||
                    !ReferenceEquals(catalog, productionCharacters) || navigation != navigationGeneration ||
                    generation != characterCatalogGeneration || refresh != characterResultsRefreshGeneration ||
                    currentView != "characters") return false;
                if (bounded.Offset > 0 && bounded.Offset >= result.Total)
                {
                    bounded = bounded with { Offset = Math.Max(0, ((result.Total - 1) / CharacterResultsPageSize) * CharacterResultsPageSize) };
                    continue;
                }
                RefreshCharacterFocusRetention();
                characterResultsOffset = bounded.Offset;
                characterPageBoundary = boundary;
                charactersView.SetEntries(page.Assets, catalog.Revision, preserveSearch: true, preserveSelection: true);
                charactersView.SetResultPage(bounded.Offset, page.Assets.Count, result.Total);
                var oldPage = characterAssetPage; characterAssetPage = page; page = null; oldPage?.Dispose();
                return true;
            }
            finally { page?.Dispose(); }
        }
    }

    private void RefreshCharacterFocusRetention()
    {
        var focused = charactersView.FocusDescriptors;
        if (focused.Count == characterFocusDescriptors.Count &&
            focused.Zip(characterFocusDescriptors).All(pair => ReferenceEquals(pair.First, pair.Second))) return;
        var bytes = focused.Sum(descriptor => checked(4096L + descriptor.Extents.Count * 512L +
            (descriptor.Label.Length + (descriptor.SearchTerms?.Length ?? 0) + (descriptor.LookupKey?.Length ?? 0) +
             descriptor.Names.Sum(name => name.Text.Length + name.Source.Length) +
             descriptor.Metadata.Sum(pair => pair.Key.Length + pair.Value.Length)) * 8L));
        var reservation = bytes > 0 ? renderMemoryBudget?.Reserve(bytes, "focused character descriptors") : null;
        characterFocusReservation?.Dispose();
        characterFocusReservation = reservation;
        characterFocusDescriptors = focused;
    }

    private async Task<(IReadOnlyList<CharacterModelBindingResult> Bindings, IDisposable Retention)> ReadProductionCharacterBindingsAsync(
        AssetDescriptor selected, AssetMemoryBudget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var catalog = productionCharacters ?? throw new InvalidOperationException("The model catalog is unavailable.");
        if (!ReferenceEquals(budget, renderMemoryBudget)) throw new InvalidOperationException("The model budget changed.");
        var snapshot = await Task.Run(() => catalog.HaveModelEntryForSelectionAsync(selected.Identity, selected.Revision, token), token)
            ?? throw new InvalidOperationException("The model details are unavailable.");
        return (snapshot.Entry.Bindings, snapshot);
    }

    private async Task<RenderAssetSnapshot> ReadProductionCharacterModelAsync(AssetDescriptor selected,
        AssetMemoryBudget budget, CancellationToken token)
    {
        var owner = session ?? throw new InvalidOperationException("Open an archive first.");
        var binding = charactersView.ModelBinding ?? throw new InvalidOperationException("Choose a resource group.");
        var detail = charactersView.SelectedModelLod ?? throw new InvalidOperationException("Choose a detail level.");
        var selection = detail.Selection ?? throw new InvalidOperationException("The selected detail is unavailable.");
        if (binding.SelectedIdentity != selected.Identity || binding.Revision != selected.Revision || !ReferenceEquals(budget, renderMemoryBudget))
            throw new InvalidOperationException("The model selection changed.");
        if (HasCurrentCharacterPose(selected, binding, detail))
        {
            using var pose = characterPoseOwner!.AcquireLease();
            return await ReadPosedCharacterModelAsync(owner, selected, binding, detail, pose, budget, token);
        }
        await EnsureSceneRendererAsync(token);
        var models = nativeModels ?? throw new InvalidOperationException("The model loader is unavailable.");
        return await WithCharacterContainerAsync(owner, binding, budget, async (container, cancellation) =>
        {
            if (binding.Rig is not { MatrixCount: > 0 })
                return await models.LoadSnapshotAsync(container, binding.CatalogIdentity, [selection], token: cancellation);
            using var rig = await AcquireCharacterRigAsync(container, binding, cancellation);
            return await models.LoadSnapshotAsync(container, binding.CatalogIdentity, [selection], token: cancellation, bindRig: rig);
        }, token);
    }

    private async Task ClearProductionCharactersAsync()
    {
        ++characterResultsRefreshGeneration;
        characterFocusReservation?.Dispose(); characterFocusReservation = null;
        characterFocusDescriptors = [];
        characterProvidersOwner = null;
        characterAssetPage?.Dispose(); characterAssetPage = null;
        characterVariantPage?.Dispose(); characterVariantPage = null;
        characterSearchTimer.Stop();
        var catalog = productionCharacters; productionCharacters = null;
        nextCharacterCatalogIndex = 0;
        characterIndexingCancelledByUser = false;
        inventoriedCharacterCatalogs.Clear();
        characterCoverageRestoreCursor = 0;
        coveredCharacterCatalogs.Clear();
        characterCoverageRetention?.Dispose(); characterCoverageRetention = null;
        characterResultsOffset = 0;
        characterPageBoundary = null;
        charactersView.SetIndexingState(false, false);
        if (catalog is not null) await catalog.DisposeAsync();
    }
}
