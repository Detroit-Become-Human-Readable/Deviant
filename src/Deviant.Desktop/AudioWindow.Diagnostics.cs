using System.Windows;
using Deviant.Assets;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private WorkflowDiagnosticOperation? diagnosticActivity;
    private string? diagnosticActivityView;
    private void Diagnostics_Click(object sender, RoutedEventArgs args) =>
        new DiagnosticsWindow(WorkflowDiagnostics.Current) { Owner = this }.ShowDialog();
    private void UpdateDiagnosticActivity(bool active, string phase, long completed, long total)
    {
        if (diagnosticActivityView != currentView || !active)
        { diagnosticActivity?.Dispose(); diagnosticActivity = null; }
        if (!active) return;
        diagnosticActivityView = currentView;
        long? generation = currentView == "textures" ? textureExplorer.PublishedBrowseResponse?.Token.Generation :
            currentView == "audio" && displayedAudioSession is not null ? displayedAudioGeneration : null;
        diagnosticActivity ??= WorkflowDiagnostics.Begin("Workspace." + currentView, session?.Snapshot.IndexContentId, generation);
        diagnosticActivity?.Progress(phase, completed, total > 0 ? total : null, "items", generation);
    }
}
