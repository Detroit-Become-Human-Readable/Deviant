using System.Windows;
using System.Windows.Controls;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private readonly CharactersView charactersView = new();
    private CancellationTokenSource? characterCatalogCancellation;
    private bool characterIndexingCancelledByUser;
    private readonly HashSet<Task> characterCatalogTasks = [];
    private readonly HashSet<Task> characterBindingTasks = [];
    private CancellationTokenSource? characterBindingCancellation;
    private IDisposable? characterBindingRetention;
    private long characterBindingGeneration;
    private Func<AssetDescriptor, AssetMemoryBudget, CancellationToken,
        Task<(IReadOnlyList<CharacterModelBindingResult> Bindings, IDisposable Retention)>>? characterBindingsProvider;
    private Func<ArchiveSnapshot, CancellationToken, Task<IReadOnlyList<AssetDescriptor>>>? characterCatalogProvider;
    private Func<AssetDescriptor, AssetMemoryBudget, CancellationToken, Task<RenderAssetSnapshot>>? characterModelProvider;
    private string characterLibraryView = "home";
    private long characterCatalogGeneration;
    private bool updatingWorkspaceTab, charactersInitialized;
    private bool characterCatalogProviderConfigured;
    private WorkspaceActivityProgress characterCatalogActivity = new(false, "Ready");
    public CharactersView CharactersBrowser => charactersView;

    public Func<ArchiveSnapshot, CancellationToken, Task<IReadOnlyList<AssetDescriptor>>>? CharacterCatalogProvider
    {
        get => characterCatalogProvider;
        set { characterCatalogCancellation?.Cancel(); characterCatalogProvider = value; characterCatalogProviderConfigured = true; }
    }

    public Func<AssetDescriptor, AssetMemoryBudget, CancellationToken,
        Task<(IReadOnlyList<CharacterModelBindingResult> Bindings, IDisposable Retention)>>? CharacterBindingsProvider
    {
        get => characterBindingsProvider;
        set
        {
            if (ReferenceEquals(characterBindingsProvider, value)) return;
            characterBindingsProvider = value;
            RefreshCharacterBinding();
        }
    }

    private void RefreshCharacterBinding()
    {
        ClearCharacterPose();
        ++characterBindingGeneration;
        charactersView.ClearRig();
        TrackLibraryTask(ClearCharacterContainerAsync());
        characterBindingCancellation?.Cancel();
        if (charactersInitialized && (characterBindingRetention is not null || characterBindingCancellation is not null)) charactersView.ResetModelBinding();
        characterBindingRetention?.Dispose(); characterBindingRetention = null;
        if (!charactersInitialized || characterBindingsProvider is not { } provider ||
            charactersView.SelectedDescriptor is not { } selected || session is not { } owner ||
            currentView != "characters" || !IsWorkspaceValidated) return;
        if (selected.Revision != new SourceRevision(owner.Snapshot.IndexContentId, owner.Snapshot.PhysicalSourceStamp) ||
            selected.Identity.IndexContentId != owner.Snapshot.IndexContentId)
        { charactersView.SetModelBindingError("The model belongs to another workspace."); return; }
        charactersView.BeginModelBinding();
        var own = CancellationTokenSource.CreateLinkedTokenSource(navigationCancellation.Token);
        characterBindingCancellation = own;
        var task = LoadCharacterBindingAsync(owner, selected, provider, navigationGeneration,
            characterBindingGeneration, own);
        characterBindingTasks.Add(task);
        _ = ObserveCharacterBindingAsync(task);
    }

    private async Task ObserveCharacterBindingAsync(Task task)
    {
        try { await task; }
        finally { characterBindingTasks.Remove(task); }
    }

    private async Task LoadCharacterBindingAsync(AudioExplorerSession owner, AssetDescriptor selected,
        Func<AssetDescriptor, AssetMemoryBudget, CancellationToken,
            Task<(IReadOnlyList<CharacterModelBindingResult> Bindings, IDisposable Retention)>> provider,
        long navigation, long generation, CancellationTokenSource own)
    {
        IDisposable? retention = null;
        try
        {
            var budget = renderMemoryBudget ??= new();
            var result = await provider(selected, budget, own.Token);
            retention = result.Retention;
            ArgumentNullException.ThrowIfNull(retention);
            own.Token.ThrowIfCancellationRequested();
            if (!IsCurrent()) return;
            charactersView.SetModelBindings(result.Bindings);
            characterBindingRetention = retention; retention = null;
        }
        catch (OperationCanceledException) { if (IsCurrent()) charactersView.SetModelBindingError("Cancelled"); }
        catch (Exception error)
        {
            if (IsCurrent()) charactersView.SetModelBindingError("Unable to load model details.", error.ToString());
        }
        finally
        {
            retention?.Dispose();
            if (ReferenceEquals(characterBindingCancellation, own)) characterBindingCancellation = null;
            own.Dispose();
        }

        bool IsCurrent() => !closingInProgress && workspaceTransitionDepth == 0 && IsWorkspaceValidated &&
            ReferenceEquals(owner, session) && generation == characterBindingGeneration &&
            navigation == navigationGeneration && currentView == "characters" &&
            ReferenceEquals(selected, charactersView.SelectedDescriptor);
    }

    private async Task ClearCharacterBindingsAsync()
    {
        ClearCharacterPose();
        ++characterBindingGeneration; characterBindingCancellation?.Cancel();
        charactersView.ResetModelBinding();
        var retention = characterBindingRetention; characterBindingRetention = null;
        retention?.Dispose();
        try { await Task.WhenAll(characterBindingTasks.ToArray()); } catch { }
        await charactersView.DrainRigAsync();
        await charactersView.DrainExportAsync();
        await ClearCharacterContainerAsync();
    }

    public Func<AssetDescriptor, AssetMemoryBudget, CancellationToken, Task<RenderAssetSnapshot>>? CharacterModelProvider
    {
        get => characterModelProvider;
        set
        {
            charactersView.CancelPreview();
            characterModelProvider = value;
            charactersView.PreviewProvider = value is null ? null : LoadCharacterModelAsync;
        }
    }

    private Task LoadCharacterModelAsync(AssetDescriptor descriptor, CancellationToken token) =>
        LoadCharacterModelWithProviderAsync(descriptor, token,
            characterModelProvider ?? throw new InvalidOperationException("Model preview unavailable."));

    private async Task LoadCharacterModelWithProviderAsync(AssetDescriptor descriptor, CancellationToken token,
        Func<AssetDescriptor, AssetMemoryBudget, CancellationToken, Task<RenderAssetSnapshot>> provider)
    {
        var owner = session ?? throw new InvalidOperationException("Open an archive first.");
        var revision = new SourceRevision(owner.Snapshot.IndexContentId, owner.Snapshot.PhysicalSourceStamp);
        var detail = charactersView.SelectedModelLod;
        var navigation = navigationGeneration;
        VerifySelection();
        await sceneNavigationDrain.WaitAsync(token);
        await modelView.UnloadAsync();
        VerifySelection();
        var budget = renderMemoryBudget ??= new();
        using var model = await provider(descriptor, budget, token);
        VerifySelection();
        if (model.Revision != revision || !ReferenceEquals(model.MemoryBudget, budget) ||
            model.Occurrences.Count == 0 || model.Occurrences.Any(placement => placement.PlacementOrigin != RenderPlacementOrigin.ModelLocal) ||
            model.Meshes.Values.Any(mesh => mesh.Identity.IndexContentId != revision.IndexContentId))
            throw new ArchiveException("CharacterModelMismatch", "The model does not match this workspace.");
        if (modelView.SnapshotLoadAsyncOverride is null) EnsureViewportService();
        if (ReferenceEquals(ScenesHost.Content, modelView)) ScenesHost.Content = sceneView;
        charactersView.PreviewContent.Content = modelView;
        await modelView.LoadAsync(model, token);
        VerifySelection();
        modelView.SetBackLabel("Close preview");
        modelView.SetDisplayName(descriptor.Label);

        void VerifySelection()
        {
            token.ThrowIfCancellationRequested();
            if (!IsWorkspaceValidated || !ReferenceEquals(owner, session) || navigation != navigationGeneration ||
                currentView != "characters" || !ReferenceEquals(descriptor, charactersView.SelectedDescriptor) ||
                !ReferenceEquals(detail, charactersView.SelectedModelLod))
                throw new OperationCanceledException(token);
            if (descriptor.Revision != revision || descriptor.Identity.IndexContentId != revision.IndexContentId)
                throw new ArchiveException("SourceChanged", "The character belongs to another workspace.");
        }
    }

    private void InitializeCharacters()
    {
        CharactersHost.Content = charactersView;
        charactersView.SelectedModelDetailChanged += (_, _) => ClearCharacterPose();
        charactersView.RigCleared += (_, _) =>
        {
            if (characterPoseOwner is null) TrackLibraryTask(ClearCharacterContainerAsync());
        };
        charactersView.SelectedResourceChanged += (_, _) => { SelectionForLibraryChanged(); RefreshCharacterBinding(); };
        charactersView.BindingCancelRequested += (_, _) =>
        { characterBindingCancellation?.Cancel(); charactersView.SetModelBindingError("Cancelled"); };
        charactersView.IndexMoreRequested += async (_, _) =>
        {
            try { await ShowCharactersCoreAsync(CancellationToken.None, indexMore: true); }
            catch (OperationCanceledException) { }
            catch (Exception error) { NavigationError(error); }
        };
        charactersView.IndexCancelRequested += (_, _) => CancelCharacterIndexing();
        InitializeProductionCharacterSearch();
        charactersInitialized = true;
    }

    private void SelectLibraryTab()
    {
        if (!charactersInitialized || WorkspaceTabs.SelectedItem == LibraryTab) return;
        updatingWorkspaceTab = true;
        try { WorkspaceTabs.SelectedItem = LibraryTab; }
        finally { updatingWorkspaceTab = false; }
        characterCatalogCancellation?.Cancel(); charactersView.CancelPreview();
        characterBindingCancellation?.Cancel();
        TrackLibraryTask(ClearCharacterBindingsAsync());
    }

    public Task ShowCharactersAsync(CancellationToken token = default) => ShowCharactersCoreAsync(token);

    private void CancelCharacterIndexing()
    {
        characterIndexingCancelledByUser = true;
        charactersView.SetIndexingState(true, false);
        charactersView.SetStatus("Cancelling indexing…");
        characterCatalogActivity = new(true, "Cancelling indexing");
        RefreshActivity();
        characterCatalogCancellation?.Cancel();
    }

    private async Task ShowCharactersCoreAsync(CancellationToken token,
        (long Generation, CancellationToken Token)? existingNavigation = null, bool indexMore = false,
        ResourceIdentity? requestedIdentity = null)
    {
        if (!RequireWorkspaceNavigation()) return;
        if (closingInProgress || workspaceTransitionDepth != 0) return;
        var enteringCharacters = currentView != "characters";
        if (currentView != "characters") characterLibraryView = currentView == "model" ? "scenes" : currentView;
        updatingWorkspaceTab = true;
        try { WorkspaceTabs.SelectedItem = CharactersTab; }
        finally { updatingWorkspaceTab = false; }
        var navigation = existingNavigation ?? (enteringCharacters ? BeginNavigation() : (navigationGeneration, navigationCancellation.Token));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, navigation.Token);
        currentView = "characters";
        SelectExplorerView("characters");
        audioDialogueCancellation?.Cancel(); SuspendPhysics();
        if (enteringCharacters) { PauseTextureWorkForNavigation(); PauseSceneWorkForNavigation(); }
        AudioStatusPanel.Visibility = Visibility.Collapsed;
        BookmarkButton.IsEnabled = SaveFilterButton.IsEnabled = false;
        UpdateWorkspaceStatus();
        await sceneNavigationDrain.WaitAsync(linked.Token);
        var owner = session;
        if (owner is null) { charactersView.SetStatus("Open a workspace to browse characters."); return; }
        var provider = characterCatalogProvider;
        if (characterCatalogProviderConfigured && provider is null) { charactersView.SetStatus("Character catalog unavailable."); return; }
        characterCatalogCancellation?.Cancel();
        using var own = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        characterCatalogCancellation = own;
        var generation = ++characterCatalogGeneration;
        charactersView.SetStatus("Reading characters…");
        characterCatalogActivity = new(true, "Reading characters");
        var task = characterCatalogProviderConfigured
            ? LoadCharacterEntriesAsync(owner, provider!, navigation.Generation, generation, own.Token)
            : LoadProductionCharactersAsync(owner, navigation.Generation, generation, indexMore, requestedIdentity, own.Token);
        characterCatalogTasks.Add(task);
        RefreshActivity();
        try { await task; }
        finally
        {
            characterCatalogTasks.Remove(task);
            if (ReferenceEquals(characterCatalogCancellation, own)) characterCatalogCancellation = null;
            if (generation == characterCatalogGeneration) characterCatalogActivity = new(false, charactersView.ActivityStatus);
            RefreshActivity();
        }
    }

    private async Task LoadCharacterEntriesAsync(AudioExplorerSession owner,
        Func<ArchiveSnapshot, CancellationToken, Task<IReadOnlyList<AssetDescriptor>>> provider,
        long navigation, long generation, CancellationToken token)
    {
        try
        {
            var entries = await provider(owner.Snapshot, token);
            token.ThrowIfCancellationRequested();
            if (closingInProgress || workspaceTransitionDepth != 0 || !ReferenceEquals(owner, session) ||
                generation != characterCatalogGeneration || navigation != navigationGeneration || currentView != "characters") return;
            charactersView.SetEntries(entries, new(owner.Snapshot.IndexContentId, owner.Snapshot.PhysicalSourceStamp),
                preserveSelection: true);
        }
        catch (OperationCanceledException) { if (generation == characterCatalogGeneration && currentView == "characters") charactersView.SetStatus("Cancelled"); }
        catch (Exception error) { if (generation == characterCatalogGeneration && currentView == "characters") charactersView.SetStatus(error.Message); }
    }

    private async Task ClearCharactersAsync()
    {
        ++characterCatalogGeneration; characterCatalogCancellation?.Cancel(); charactersView.Clear();
        try { await Task.WhenAll(Task.WhenAll(characterCatalogTasks.ToArray()), charactersView.DrainPreviewAsync(), ClearCharacterBindingsAsync()); } catch { }
        await ClearProductionCharactersAsync();
    }

    private async void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!charactersInitialized || updatingWorkspaceTab || e.Source != WorkspaceTabs) return;
        try
        {
            if (WorkspaceTabs.SelectedItem == CharactersTab) await ShowCharactersAsync();
            else
            {
                var navigation = BeginNavigation();
                characterCatalogCancellation?.Cancel(); charactersView.CancelPreview();
                await Task.WhenAll(charactersView.DrainPreviewAsync(), ClearCharacterBindingsAsync()).WaitAsync(navigation.Token);
                if (navigation.Generation != navigationGeneration || WorkspaceTabs.SelectedItem != LibraryTab || closingInProgress) return;
                await modelView.UnloadAsync();
                if (navigation.Generation != navigationGeneration || WorkspaceTabs.SelectedItem != LibraryTab || closingInProgress) return;
                if (characterLibraryView == "home") Home_Click(this, new());
                else if (characterLibraryView == "library") ShowLibrary(libraryView, displayedCollection);
                else if (characterLibraryView == "scenes") await ShowScenesAsync(navigation.Token);
                else if (characterLibraryView == "textures") await ShowTexturesAsync(navigation.Token);
                else ShowBrowse(characterLibraryView);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { NavigationError(error); }
    }
}
