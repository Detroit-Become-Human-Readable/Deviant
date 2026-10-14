using System.Windows;
using Deviant.Core;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private ArchiveSnapshot? admittedWorkspace;
    private GridLength explorerWorkspaceWidth = new(220);
    private bool workspaceNavigationVisible = true;
    public bool IsWorkspaceValidated => session is {} current && ReferenceEquals(admittedWorkspace, current.Snapshot) &&
        !indexOpeningActive && workspaceTransitionDepth == 0 && !closingInProgress;

    private static void ValidateWorkspace(ArchiveSnapshot snapshot)
    {
        if (!snapshot.IndexStamp.Exists || snapshot.IndexStamp.Error is not null || snapshot.Records.Count == 0 ||
            snapshot.Volumes.Count == 0 || snapshot.Volumes.Any(volume => !volume.Stamp.Exists || volume.Stamp.Error is not null) ||
            snapshot.Records.Any(record => record.Validity != ResourceValidity.Valid))
            throw new ArchiveException("InstallationIncomplete", "The selected installation is incomplete or unavailable. Choose a valid game installation.");
    }

    private void RefreshWorkspaceNavigation()
    {
        if (ExplorerNavigationPane is null || CharactersTab is null) return;
        var visible = IsWorkspaceValidated;
        if (workspaceNavigationVisible != visible)
        {
            if (!visible && workspaceNavigationVisible) explorerWorkspaceWidth = ExplorerPaneWidth.Width;
            ExplorerPaneWidth.MinWidth = visible ? 140 : 0;
            ExplorerPaneWidth.Width = visible ? explorerWorkspaceWidth : new GridLength(0);
            WorkspaceLayout.ColumnDefinitions[1].Width = new GridLength(visible ? 8 : 0);
            workspaceNavigationVisible = visible;
        }
        ExplorerNavigationPane.Visibility = ExplorerPaneSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ExplorerTree.IsEnabled = visible;
        CharactersTab.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CharactersTab.IsEnabled = visible;
        LibraryTab.Header = visible ? "Library" : "Home";
        AdvancedButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = visible;
        if (!visible && charactersInitialized && WorkspaceTabs.SelectedItem == CharactersTab) SelectLibraryTab();
    }

    private bool RequireWorkspaceNavigation()
    {
        if (IsWorkspaceValidated) return true;
        if (session is {} current && ReferenceEquals(admittedWorkspace, current.Snapshot)) return false;
        Home_Click(this, new());
        HomeStatus.Text = "Choose a valid game installation to browse assets.";
        return false;
    }
}
