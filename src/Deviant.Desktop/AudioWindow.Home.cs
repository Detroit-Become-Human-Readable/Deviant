using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Core;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private bool synchronizingExplorer;
    private long explorerSelectionRequest;
    private ArchiveSnapshot? homeTypeSnapshot;
    private HashSet<uint> homeResourceTypes = [];

    private void RefreshHomeState()
    {
        RefreshWorkspaceNavigation();
        var indexing = indexOpeningActive || AudioIndexingActive;
        var ready = IsWorkspaceValidated && !indexing;
        HomeLogo.Visibility = indexing ? Visibility.Collapsed : Visibility.Visible;
        HomeProcessing.IsActive = indexing && currentView == "home";
        HomeTitle.Text = ready ? "Welcome, Deviant" : "Deviant";
        HomeQuestion.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        HomeSetupActions.Visibility = !ready && !indexing ? Visibility.Visible : Visibility.Collapsed;
        HomeIndexingState.Visibility = indexing ? Visibility.Visible : Visibility.Collapsed;
        HomeLaunchers.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        if (indexing)
        {
            var progress = session?.CurrentProgress;
            HomeIndexingDetail.Text = indexOpeningActive
                ? FormatActivity(foregroundStage, foregroundCompleted, foregroundTotal)
                : progress is null ? "Reading audio" : FormatActivity(progress.Phase, progress.Completed, progress.Total);
        }
        if (!ready || session is not { } current) return;
        if (!ReferenceEquals(homeTypeSnapshot, current.Snapshot))
        {
            homeTypeSnapshot = current.Snapshot;
            homeResourceTypes = current.Snapshot.Records.Select(record => record.Type).ToHashSet();
        }
        var types = homeResourceTypes;
        HomeAudioButton.Visibility = current.Catalog.Media.Count > 0 || current.Catalog.Banks.Count > 0 ||
            types.Overlaps([65u, 1016u, 1022u, 1023u, 1031u, 1033u, 4091u]) ? Visibility.Visible : Visibility.Collapsed;
        HomeStringsButton.Visibility = types.Contains(1016) ? Visibility.Visible : Visibility.Collapsed;
        HomeCharactersButton.Visibility = types.Contains(29) ? Visibility.Visible : Visibility.Collapsed;
        HomeTexturesButton.Visibility = types.Contains(2229) || types.Contains(2230) ? Visibility.Visible : Visibility.Collapsed;
        HomeScenesButton.Visibility = types.Contains(29) ? Visibility.Visible : Visibility.Collapsed;
        HomeAnimationButton.Visibility = types.Contains(Deviant.Formats.Animation.AnimationDataReader.ResourceType) ? Visibility.Visible : Visibility.Collapsed;
        HomeShadersButton.Visibility = types.Contains(29) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void HomeView_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string view }) return;
        await OpenHomeViewAsync(view);
    }

    public async Task OpenHomeViewAsync(string view, CancellationToken token = default)
    {
        if (!RequireWorkspaceNavigation() || session is null) return;
        var navigation = BeginNavigation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(navigation.Token, token);
        SelectExplorerView(view);
        try
        {
            switch (view)
            {
                case "audio":
                    ShowBrowse("audio");
                    await QueryAsync(true);
                    break;
                case "strings": await ShowStringsAsync(linked.Token); break;
                case "characters": await ShowCharactersCoreAsync(linked.Token, navigation); break;
                case "textures": await ShowTexturesAsync(linked.Token); break;
                case "scenes": await ShowScenesAsync(linked.Token); break;
                case "records": ShowRecords(); break;
                case "animation": await ShowAnimationAsync(linked.Token); break;
                case "shader-picker":
                    var choice = ContainerChoiceDialog.Read(this, session.Snapshot);
                    if (choice is not null) await InspectShaderResourceAsync(choice, linked.Token);
                    break;
                default: throw new ArgumentException("Unknown home view.", nameof(view));
            }
            await Dispatcher.InvokeAsync(() =>
            {
                if (navigation.Generation == navigationGeneration) SelectExplorerView(view);
            }, DispatcherPriority.Loaded, linked.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { NavigationError(error); }
    }

    private void SelectExplorerView(string view)
    {
        var request = ++explorerSelectionRequest;
        var navigation = navigationGeneration;
        if (ExplorerTree.SelectedItem is NavigationItem selected && selected.View == view) return;
        TrySelect(allowRetry: true);

        void TrySelect(bool allowRetry)
        {
            if (request != explorerSelectionRequest || navigation != navigationGeneration || closingInProgress) return;
            if (ExplorerTree.SelectedItem is NavigationItem current && current.View == view) return;
            var previousSynchronization = synchronizingExplorer;
            synchronizingExplorer = true;
            try
            {
                if (!Select(ExplorerTree) && allowRetry)
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => TrySelect(allowRetry: false));
            }
            finally { synchronizingExplorer = previousSynchronization; }
        }

        bool Select(ItemsControl parent)
        {
            foreach (var item in parent.Items.OfType<NavigationItem>())
            {
                if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) continue;
                if (item.View == view && item.Group is null && item.Language is null)
                {
                    container.IsSelected = true;
                    return true;
                }
                if (!item.Children.Any(child => child.View == view)) continue;
                container.IsExpanded = true;
                if (Select(container)) return true;
            }
            return false;
        }
    }
}
