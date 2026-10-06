using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Xml.Linq;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Desktop;

namespace Deviant.AudioCheck;

public static partial class Program
{
    private static async Task<object> ExerciseReferenceGraphAsync(AudioWindow window, string input, string output, long ordinal)
    {
        var view = window.ReferenceBrowser;
        var source = await new ArchiveReader().OpenAsync(input);
        Invoke(window, "ShowBrowse", "references");
        var calls = new List<ResourceIdentity>();
        view.RelatedNavigator = (target, token) => { token.ThrowIfCancellationRequested(); calls.Add(target); return Task.CompletedTask; };
        await GraphLoadAsync(view, source, ordinal);
        var initial = view.CurrentReferenceGraph!;
        Require(initial.Edges.Count > 0, "The selected resource contains no verified graph references.");
        var initialZoom = view.GraphBrowser.Zoom;
        if (initial.Edges.Count > 8) Require(initialZoom >= 0.7, "Large reference graph starts with unreadable labels.");
        await CaptureWidthsAsync(window, output, "reference-graph-input");
        await CheckGraphExportsAsync(view, output, "input");

        var synthetic = await new ArchiveReader().OpenAsync(WriteGraphFixture(Path.Combine(output, "fixture"), false));
        await GraphLoadAsync(view, synthetic, 0);
        var graph = view.CurrentReferenceGraph!;
        Require(graph.Nodes.Count == 5 && graph.Edges.Count == 4, "Graph lost source-order reference rows.");
        Require(graph.Nodes[1].Identity == graph.Nodes[2].Identity && graph.Nodes[1].NodeId != graph.Nodes[2].NodeId, "Duplicate references were merged.");
        Require(graph.Nodes[3].Status == ReferenceStatus.Missing && graph.Nodes[4].Status == ReferenceStatus.Ambiguous, "Graph resolution statuses differ from the source table.");
        window.UpdateLayout();
        view.GraphBrowser.Fit();
        var viewport = (FrameworkElement)view.GraphBrowser.FindName("Viewport");
        var canvas = (FrameworkElement)view.GraphBrowser.FindName("GraphCanvas");
        var actualOrigin = canvas.TranslatePoint(new Point(0, 0), viewport);
        var expectedOrigin = new Point((viewport.ActualWidth - canvas.Width * view.GraphBrowser.Zoom) / 2,
            (viewport.ActualHeight - canvas.Height * view.GraphBrowser.Zoom) / 2);
        await File.WriteAllTextAsync(Path.Combine(output, "fit-geometry.json"), JsonSerializer.Serialize(new
        { Actual = actualOrigin, Expected = expectedOrigin, viewport.ActualWidth, viewport.ActualHeight, CanvasWidth = canvas.Width, CanvasHeight = canvas.Height, view.GraphBrowser.Zoom }));
        Require(Math.Abs(actualOrigin.X - expectedOrigin.X) <= 1.1 && Math.Abs(actualOrigin.Y - expectedOrigin.Y) <= 1.1,
            "Fit is displaced by layout positioning outside its render transform.");
        var text = TextureVisibleText(view.GraphBrowser).ToArray();
        Require(new[] { "Reference 1", "Reference 2", "Reference 3", "Reference 4", "Resolved", "Missing", "Ambiguous" }.All(text.Contains), "Themed graph cards have blank labels or statuses.");
        view.GraphBrowser.SelectNode("reference/0");
        Require(view.SelectedReference?.Ordinal == 0 && ((Button)view.GraphBrowser.FindName("OpenNode")).IsEnabled, "Graph and reference-list selection differ.");
        ((Button)view.GraphBrowser.FindName("OpenNode")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => calls.Count == 1, "Graph Open did not follow the verified unique target.");
        Require(calls[0] == new ResourceIdentity(synthetic.IndexContentId, 1), "Graph Open followed another identity.");
        foreach (var key in new[] { "reference/2", "reference/3", "owner" })
        {
            view.GraphBrowser.SelectNode(key);
            Require(!((Button)view.GraphBrowser.FindName("OpenNode")).IsEnabled, "Unresolved or owner node enabled Open.");
        }
        ((DataGrid)view.FindName("Rows")).SelectedItem = view.CurrentReferences[1];
        Require(view.GraphBrowser.SelectedNode?.NodeId == "reference/1", "List selection did not update the graph.");
        await CaptureWidthsAsync(window, output, "reference-graph-states");
        await CheckGraphExportsAsync(view, output, "states");
        await Task.WhenAll(view.FilterArchiveReferencesAsync(""), view.FilterArchiveReferencesAsync("Missing"));
        Require(view.CurrentReferenceGraph!.Edges.Count == 1 && view.CurrentReferenceGraph.Nodes[1].NodeId == "reference/2", "Late query overwrote the filtered graph.");
        await CheckGraphExportsAsync(view, output, "filtered");

        var pages = await new ArchiveReader().OpenAsync(WriteGraphFixture(Path.Combine(output, "pages"), true));
        await GraphLoadAsync(view, pages, 0);
        Require(view.CurrentReferenceGraph!.Edges.Count == 100, "First graph page has the wrong size.");
        view.GraphBrowser.SelectNode("reference/0");
        ((Button)view.FindName("Next")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(view.CurrentReferenceGraph!.Nodes[1].NodeId == "reference/100" && view.CurrentReferenceGraph.Edges.Count == 100, "Second graph page is not source-order bounded.");
        await CheckGraphExportsAsync(view, output, "page2");
        view.GraphBrowser.SelectNode("reference/100");
        ((Button)view.FindName("Next")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(view.CurrentReferenceGraph!.Nodes[1].NodeId == "reference/200" && view.CurrentReferenceGraph.Edges.Count == 5, "Final graph page has the wrong rows.");
        await CaptureWidthsAsync(window, output, "reference-graph-page3");
        var paths = await CheckGraphExportsAsync(view, output, "page3");
        foreach (var (format, path) in paths)
        {
            var complete = await File.ReadAllBytesAsync(path); var cancelled = false;
            try { await view.ExportToAsync(format, path, true, new CancellationToken(true)); } catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && (await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(complete), "Cancelled graph export replaced a completed output.");
        }
        var data = Path.ChangeExtension(pages.IndexPath, ".dat"); var timestamp = File.GetLastWriteTimeUtc(data); var bytes = await File.ReadAllBytesAsync(data);
        bytes[28] ^= 1; await File.WriteAllBytesAsync(data, bytes); File.SetLastWriteTimeUtc(data, timestamp);
        foreach (var (format, path) in paths)
        {
            var complete = await File.ReadAllBytesAsync(path); var rejected = false;
            try { await view.ExportToAsync(format, path, true); } catch (ArchiveException error) when (error.Code == "SourceChanged") { rejected = true; }
            Require(rejected && (await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(complete), "Changed graph source replaced a completed export.");
        }
        view.GraphBrowser.SelectNode("reference/200");
        var callsBeforeMutation = calls.Count; var navigationRejected = false;
        try { await view.OpenSelectedReferenceAsync(); } catch (ArchiveException error) when (error.Code == "SourceChanged") { navigationRejected = true; }
        Require(navigationRejected && calls.Count == callsBeforeMutation, "Changed graph source reached the navigation callback.");
        await view.ClearAsync();
        Require(view.CurrentReferenceGraph is null && view.GraphBrowser.SelectedNode is null && view.GraphBrowser.VisualNodeCount == 0, "Clearing the source retained graph selection or cards.");
        return new { InitialOwner = new ResourceIdentity(source.IndexContentId, ordinal), InitialReferenceCount = initial.Edges.Count, InitialZoom = initialZoom,
            StyledLabelsPassed = true, UniqueOpenPassed = true, MissingAmbiguousOwnerDisabled = true, DuplicateReferencesPreserved = true,
            ListGraphSelectionPassed = true, LatestFilterPassed = true, FitCentered = true, PageCounts = new[] { 100, 100, 5 },
            JsonSvgSamePagePassed = true, CancellationPreservesOutput = true, SourceChangedPreservesOutput = true, SourceChangedNavigationRejected = true, ClearPassed = true,
            Widths = new[] { 1280, 1600 }, AudioEnrichment = false, NativeDialogs = false };
    }

    private static async Task GraphLoadAsync(SceneReferencesView view, ArchiveSnapshot source, long ordinal)
    {
        await view.LoadAsync(source, new(source.IndexContentId, ordinal), sourceLabel: "Catalog");
        await view.FilterArchiveReferencesAsync("");
        ((ToggleButton)view.FindName("GraphToggle")).IsChecked = true;
        Require(view.CurrentReferenceGraph is not null && view.GraphBrowser.IsVisible, "Reference graph did not become visible.");
    }

    private static async Task<(AssetExportFormat, string)[]> CheckGraphExportsAsync(SceneReferencesView view, string output, string label)
    {
        var graph = view.CurrentReferenceGraph!;
        var json = Path.Combine(output, "exports", label + ".json"); var svg = Path.Combine(output, "exports", label + ".svg");
        await view.ExportToAsync(AssetExportFormat.Json, json, true); await view.ExportToAsync(AssetExportFormat.Svg, svg, true);
        using var parsed = JsonDocument.Parse(await File.ReadAllTextAsync(json)); var document = parsed.RootElement;
        Require(document.GetProperty("Domain").GetString() == "ArchiveResourceReferences" && document.GetProperty("SourceRevision").GetProperty("IndexContentId").GetString() == view.LoadedRevision!.IndexContentId, "Graph JSON lost its domain or source revision.");
        Require(document.GetProperty("Nodes").EnumerateArray().Select(node => node.GetProperty("NodeId").GetString()).SequenceEqual(graph.Nodes.Select(node => node.NodeId)), "JSON nodes differ from the displayed graph page.");
        Require(document.GetProperty("Edges").EnumerateArray().Select(edge => edge.GetProperty("EdgeId").GetString()).SequenceEqual(graph.Edges.Select(edge => edge.EdgeId)), "JSON edges differ from the displayed graph page.");
        var xml = XDocument.Load(svg); XNamespace ns = "http://www.w3.org/2000/svg";
        Require(xml.Descendants(ns + "g").Select(node => node.Attribute("data-node-id")!.Value).Order().SequenceEqual(graph.Nodes.Select(node => node.NodeId).Order()), "SVG nodes differ from the displayed graph page.");
        Require(xml.Descendants(ns + "path").Count() == graph.Edges.Count, "SVG omitted page edges.");
        return [(AssetExportFormat.Json, json), (AssetExportFormat.Svg, svg)];
    }

    private static string WriteGraphFixture(string directory, bool paged)
    {
        Directory.CreateDirectory(directory);
        var refs = paged ? Enumerable.Range(0, 205).Select(_ => (Type: 2229u, Id: 2u)).ToArray()
            : new[] { (Type: 2229u, Id: 2u), (Type: 2229u, Id: 2u), (Type: 2229u, Id: 77u), (Type: 2229u, Id: 3u) };
        var owner = new byte[20 + 9 * refs.Length]; "COM_CONT"u8.CopyTo(owner);
        BinaryPrimitives.WriteUInt32LittleEndian(owner.AsSpan(8), 6); BinaryPrimitives.WriteUInt32LittleEndian(owner.AsSpan(12), (uint)owner.Length - 16);
        BinaryPrimitives.WriteUInt32LittleEndian(owner.AsSpan(16), (uint)refs.Length);
        for (var i = 0; i < refs.Length; i++) { BinaryPrimitives.WriteUInt32LittleEndian(owner.AsSpan(20 + i * 9), refs[i].Type); BinaryPrimitives.WriteUInt32LittleEndian(owner.AsSpan(24 + i * 9), refs[i].Id); owner[28 + i * 9] = (byte)i; }
        uint[] ids = paged ? [1, 2] : [1, 2, 3, 3];
        var index = new byte[IndexLayout.HeaderLength + ids.Length * IndexLayout.RecordLength]; IndexLayout.Magic.CopyTo(index);
        BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(20), 18); BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(101), (uint)ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            uint[] words = [i == 0 ? 3u : 2229u, 1, ids[i], i == 0 ? 0u : (uint)(owner.Length + i - 1), i == 0 ? (uint)owner.Length : 1u, 0, 0];
            for (var field = 0; field < words.Length; field++) BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(105 + i * 28 + field * 4), words[field]);
        }
        var path = Path.Combine(directory, "archive.idx"); File.WriteAllBytes(path, index);
        File.WriteAllBytes(Path.ChangeExtension(path, ".dat"), owner.Concat(Enumerable.Repeat((byte)1, ids.Length - 1)).ToArray()); return path;
    }
}
