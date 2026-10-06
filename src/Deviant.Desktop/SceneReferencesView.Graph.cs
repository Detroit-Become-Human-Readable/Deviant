using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Graphs;
using Microsoft.Win32;

namespace Deviant.Desktop;

public partial class SceneReferencesView
{
    private bool graphUpdating;
    public EvidenceGraph? CurrentReferenceGraph { get; private set; }
    public ReferenceGraphView GraphBrowser => ReferenceGraph;

    private void InitializeReferenceGraph()
    {
        ReferenceGraph.SelectedNodeChanged += (_, _) =>
        {
            if (graphUpdating) return;
            var node = ReferenceGraph.SelectedNode;
            graphUpdating = true;
            try
            {
                Rows.SelectedItem = node?.Metadata.TryGetValue("rowOrdinal", out var ordinal) == true &&
                    int.TryParse(ordinal, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    ? CurrentReferences.SingleOrDefault(row => row.Ordinal == parsed) : null;
            }
            finally { graphUpdating = false; }
        };
        ReferenceGraph.OpenRequested += async (_, node) =>
        {
            if (!ReferenceEquals(node, ReferenceGraph.SelectedNode) || CurrentReferenceGraph?.Nodes.Contains(node) != true) return;
            await OpenReferenceSafelyAsync();
        };
    }
    private void SyncGraphSelection()
    {
        if (graphUpdating || CurrentReferenceGraph is null) return;
        graphUpdating = true;
        try { ReferenceGraph.SelectNode(SelectedReference is { } row ? "reference/" + row.Ordinal.ToString(CultureInfo.InvariantCulture) : ArchiveReferenceGraphs.SourceNodeId); }
        finally { graphUpdating = false; }
    }
    private void RefreshReferenceGraph()
    {
        ReferenceGraph.NavigationEnabled = RelatedNavigator is not null;
        var available = Mode.SelectedIndex == 2 && CurrentArchiveReferences is not null && snapshot is not null;
        GraphToggle.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        graphUpdating = true;
        try
        {
            if (!available) { CurrentReferenceGraph = null; ReferenceGraph.Clear(); GraphToggle.IsChecked = false; }
            else
            {
                CurrentReferenceGraph = ArchiveReferenceGraphs.BuildPage(snapshot!, CurrentArchiveReferences!, SourceName.Text,
                    CurrentReferences.Select(row => row.Ordinal).ToArray(), pageOffset, matches.Length, Search.Text);
                ReferenceGraph.Load(CurrentReferenceGraph, ArchiveReferenceGraphs.SourceNodeId);
            }
        }
        finally { graphUpdating = false; }
        UpdateGraphVisibility(); SyncGraphSelection();
    }
    private void UpdateGraphVisibility()
    {
        var showing = GraphToggle.IsChecked == true && CurrentReferenceGraph is not null;
        ReferenceGraph.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
        Rows.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
        ExportSvg.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
        ExportSvg.IsEnabled = showing;
    }
    private void GraphModeChanged(object sender, RoutedEventArgs e)
    { if (ReferenceGraph is not null && !graphUpdating) UpdateGraphVisibility(); }
    private void ResetReferenceGraph()
    {
        graphUpdating = true;
        try { CurrentReferenceGraph = null; ReferenceGraph.Clear(); GraphToggle.IsChecked = false; GraphToggle.Visibility = ExportSvg.Visibility = Visibility.Collapsed; }
        finally { graphUpdating = false; }
        UpdateGraphVisibility();
    }
    private async void ExportSvg_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentReferenceGraph is null || activeJob is { IsCompleted: false }) return;
        var dialog = new SaveFileDialog { Title = "Export graph", Filter = "SVG (*.svg)|*.svg", FileName = "References.svg", OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var version = generation;
        try { await ExportToAsync(AssetExportFormat.Svg, dialog.FileName, true); if (version == generation) Status.Text = "Exported"; }
        catch (OperationCanceledException) { if (version == generation) Status.Text = "Export cancelled"; }
        catch (Exception error) { if (version == generation) Status.Text = error.Message; }
    }
}
