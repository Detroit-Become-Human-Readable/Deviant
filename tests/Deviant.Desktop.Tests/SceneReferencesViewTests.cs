using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Xml.Linq;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Browser;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class SceneReferencesViewTests
{
    [Fact]
    public void ActualControlPagesFiltersAndKeepsCandidatesNonNavigable() => Run(root =>
    {
        var view = new SceneReferencesView();
        try
        {
            var payload = DirectoryBytes(205);
            var source = Snapshot(root, "archive", [(29u, payload), (41u, new byte[] { 1, 2, 3 })]);
            WriteDependencies(source, 205);
            var owner = new ResourceIdentity(source.IndexContentId, 0);
            var changes = 0; view.SelectedResourceChanged += (_, _) => changes++;
            Pump(view.LoadAsync(source, owner, sourceLabel: "Sample resource"));
            Assert.Equal(owner, view.SelectedIdentity); Assert.Equal("Sample resource", view.SelectedLabel);
            Assert.Equal(100, view.CurrentReferences.Count);
            Assert.Equal(205, view.CurrentDependencyInspection!.Value!.References.Count);
            Assert.Equal(205, view.CurrentDirectoryInspection!.Value!.Entries.Count);
            Assert.All(view.CurrentReferences, row => Assert.Null(row.NavigationTarget));
            var rows = (DataGrid)view.FindName("Rows"); rows.SelectedIndex = 0;
            Assert.Equal(owner, view.SelectedIdentity);
            Assert.Contains("Raw words: 41, 2, 10000, 0, 3", ((TextBox)view.FindName("Advanced")).Text);
            ((Button)view.FindName("Next")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(100, view.CurrentReferences.Count);
            ((Button)view.FindName("Next")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(5, view.CurrentReferences.Count);
            Pump(view.FilterAsync("10199")); Assert.Equal(199, Assert.Single(view.CurrentReferences).Ordinal);
            Pump(view.FilterAsync("", directory: true)); Assert.Equal(100, view.CurrentReferences.Count);
            Assert.All(((DataGrid)view.FindName("Rows")).Columns, column => Assert.Equal(Visibility.Visible, column.Visibility));
            rows.SelectedIndex = 0; Assert.Contains("Decoded extent:", ((TextBox)view.FindName("Advanced")).Text);
            Assert.True(changes > 0);
            view.Measure(new Size(960, 640)); view.Arrange(new Rect(0, 0, 960, 640)); view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(960, 640, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var render = new MemoryStream(); encoder.Save(render); Assert.True(render.Length > 1000);
            Pump(view.ClearAsync()); Assert.Null(view.SelectedIdentity); Assert.Null(view.LoadedRevision);
            Assert.Empty(view.CurrentReferences); Assert.Null(view.CurrentDirectoryInspection);
            Pump(view.LoadAsync(source, owner)); Assert.Equal(100, view.CurrentReferences.Count);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void LatestLoadAndCancellationCannotRestoreOldWorkspaceRows() => Run(root =>
    {
        var view = new SceneReferencesView();
        try
        {
            var first = Snapshot(root, "first", [(29u, DirectoryBytes(205)), (41u, new byte[] { 1 })]); WriteDependencies(first, 205);
            var second = Snapshot(root, "second", [(29u, DirectoryBytes(1)), (41u, new byte[] { 2 })]); WriteDependencies(second, 1);
            var old = view.LoadAsync(first, new(first.IndexContentId, 0)); var latest = view.LoadAsync(second, new(second.IndexContentId, 0));
            Pump(Task.WhenAll(old, latest)); Assert.Single(view.CurrentReferences); Assert.Equal(second.IndexContentId, view.SelectedIdentity!.IndexContentId);
            Pump(view.LoadAsync(first, new(first.IndexContentId, 0), new CancellationToken(true)));
            Assert.Empty(view.CurrentReferences); Assert.Null(view.SelectedIdentity);
            Pump(view.LoadAsync(second, new(second.IndexContentId, 0))); Assert.Single(view.CurrentReferences);
            var pending = view.FilterAsync("Reference"); var clear = view.ClearAsync(); Pump(Task.WhenAll(pending, clear));
            Assert.Empty(view.CurrentReferences); Assert.Null(view.CurrentDependencyInspection);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void JsonAndOriginalExportsAreAtomicAndCheckTheirRelevantSources() => Run(root =>
    {
        var view = new SceneReferencesView();
        try
        {
            var payload = DirectoryBytes(3); var source = Snapshot(root, "archive", [(29u, payload), (41u, new byte[] { 1 })]); WriteDependencies(source, 3);
            Pump(view.LoadAsync(source, new(source.IndexContentId, 0)));
            var original = Path.Combine(root, "output", "resource.bin"); var json = Path.Combine(root, "output", "references.json");
            Pump(view.ExportToAsync(AssetExportFormat.Original, original)); Assert.Equal(payload, File.ReadAllBytes(original));
            Pump(view.ExportToAsync(AssetExportFormat.Json, json));
            var exported = File.ReadAllBytes(json);
            using (var document = JsonDocument.Parse(exported))
            {
                Assert.Equal(3, document.RootElement.GetProperty("Dependencies").GetProperty("Value").GetProperty("References").GetArrayLength());
                Assert.Equal(3, document.RootElement.GetProperty("Directory").GetProperty("Value").GetProperty("Entries").GetArrayLength());
                Assert.Equal(source.PhysicalSourceStamp, document.RootElement.GetProperty("Revision").GetProperty("PhysicalSourceStamp").GetString());
                Assert.False(document.RootElement.GetProperty("Dependencies").GetProperty("Value").GetProperty("References")[0].GetProperty("Relationship").GetProperty("Evidence").GetProperty("Verified").GetBoolean());
            }
            var unsafeError = Pump(Assert.ThrowsAsync<ArchiveException>(() => view.ExportToAsync(AssetExportFormat.Json, Path.Combine(source.SourceRoot, "references.json"))));
            Assert.Equal("UnsafeOutput", unsafeError.Code);
            Pump(Assert.ThrowsAnyAsync<OperationCanceledException>(() => view.ExportToAsync(AssetExportFormat.Json, json, true, new(true))));
            Assert.Equal(exported, File.ReadAllBytes(json));
            var depPath = Path.ChangeExtension(source.IndexPath, ".dep"); var stamp = File.GetLastWriteTimeUtc(depPath); var depBytes = File.ReadAllBytes(depPath); depBytes[52] ^= 1;
            File.WriteAllBytes(depPath, depBytes); File.SetLastWriteTimeUtc(depPath, stamp);
            var changed = Pump(Assert.ThrowsAsync<ArchiveException>(() => view.ExportToAsync(AssetExportFormat.Json, json, true)));
            Assert.Equal("SourceChanged", changed.Code); Assert.Equal(exported, File.ReadAllBytes(json));
            Pump(view.ExportToAsync(AssetExportFormat.Original, original, true)); Assert.Equal(payload, File.ReadAllBytes(original));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "output"), "*.tmp"));
            var sourceStamp = File.GetLastWriteTimeUtc(source.Volumes[0].FilePath); var raw = File.ReadAllBytes(source.Volumes[0].FilePath); raw[payload.Length - 1] ^= 1;
            File.WriteAllBytes(source.Volumes[0].FilePath, raw); File.SetLastWriteTimeUtc(source.Volumes[0].FilePath, sourceStamp);
            changed = Pump(Assert.ThrowsAsync<ArchiveException>(() => view.ExportToAsync(AssetExportFormat.Original, original, true)));
            Assert.Equal("SourceChanged", changed.Code); Assert.Equal(payload, File.ReadAllBytes(original));
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void DirectoryStillLoadsWhenDependencySidecarIsAbsent() => Run(root =>
    {
        var view = new SceneReferencesView();
        try
        {
            var source = Snapshot(root, "archive", [(29u, DirectoryBytes(2))]);
            Pump(view.LoadAsync(source, new(source.IndexContentId, 0)));
            Assert.Null(view.CurrentDependencyInspection); Assert.NotNull(view.CurrentDirectoryInspection!.Value);
            Pump(view.FilterAsync("", directory: true)); Assert.Equal(2, view.CurrentReferences.Count);
            Pump(view.ExportToAsync(AssetExportFormat.Json, Path.Combine(root, "output", "directory.json")));
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void StyledArchiveReferencesSupportOpenKeyboardAndPointerWhilePreservingEvidence() => Run(root =>
    {
        var payload = ContainerBytes([(41u, 2u, (byte)0xA5)]); var source = Snapshot(root, "archive", [(3u, payload), (41u, new byte[] { 1 })]);
        var view = new SceneReferencesView(); var calls = new List<ResourceIdentity>(); view.RelatedNavigator = (target, _) => { calls.Add(target); return Task.CompletedTask; };
        var window = new Window { Content = view, Width = 1000, Height = 700, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Deviant;component/ControlStyles.xaml") });
        try
        {
            window.Show(); Pump(view.LoadAsync(source, new(source.IndexContentId, 0), sourceLabel: "Catalog sample")); Pump(view.FilterArchiveReferencesAsync(""));
            var rows = (DataGrid)view.FindName("Rows"); rows.SelectedIndex = 0; window.UpdateLayout();
            Assert.Equal("Catalog sample", ((TextBlock)view.FindName("SourceName")).Text); Assert.Equal(Visibility.Visible, ((ComboBoxItem)view.FindName("ArchiveReferenceMode")).Visibility);
            Assert.Equal(new[] { "Reference", "Match" }, rows.Columns.Where(column => column.Visibility == Visibility.Visible).Select(column => column.Header as string));
            Assert.DoesNotContain("0x", view.CurrentReferences[0].Label, StringComparison.Ordinal);
            Assert.True(((Button)view.FindName("OpenReference")).IsEnabled); Assert.Equal(new ResourceIdentity(source.IndexContentId, 1), view.SelectedReference!.NavigationTarget);
            var detail = ((TextBox)view.FindName("Advanced")).Text; Assert.Contains("Trailer 0xA5", detail, StringComparison.Ordinal); Assert.Contains("Reference extent archive.dat: 20 + 9", detail, StringComparison.Ordinal);
            Assert.Equal((byte)0xA5, view.CurrentArchiveReferences!.Relationships[0].Evidence.SourceReferenceTrailer);
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(rows)!, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            rows.RaiseEvent(key); Pump(WaitUntilAsync(() => calls.Count == 1)); Assert.True(key.Handled);
            var row = (DataGridRow)rows.ItemContainerGenerator.ContainerFromIndex(0)!;
            var pointer = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = row };
            rows.RaiseEvent(pointer); Pump(WaitUntilAsync(() => calls.Count == 2));
            ((Button)view.FindName("OpenReference")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(WaitUntilAsync(() => calls.Count == 3));
            Assert.All(calls, target => Assert.Equal(new ResourceIdentity(source.IndexContentId, 1), target));
            var jsonPath = Path.Combine(root, "output", "references.json"); Pump(view.ExportToAsync(AssetExportFormat.Json, jsonPath));
            using var json = JsonDocument.Parse(File.ReadAllText(jsonPath)); var reference = json.RootElement.GetProperty("ArchiveReferences").GetProperty("Relationships")[0];
            Assert.True(reference.GetProperty("Evidence").GetProperty("Verified").GetBoolean()); Assert.Equal(0xA5, reference.GetProperty("Evidence").GetProperty("SourceReferenceTrailer").GetByte());
            Assert.Equal(9, reference.GetProperty("Evidence").GetProperty("PhysicalExtent").GetProperty("Length").GetInt64());
        }
        finally { Pump(view.DisposeAsync().AsTask()); window.Close(); }
    });

    [Fact]
    public void AmbiguousMissingAndTupleOnlyReferencesStayNonNavigable() => Run(root =>
    {
        var payload = ContainerBytes([(41u, 2u, (byte)0), (99u, 500u, (byte)0), (42u, 3u, (byte)0)]);
        var source = Snapshot(root, "archive", [(3u, payload), (41u, new byte[1]), (41u, new byte[1]), (42u, new byte[1])], [1, 2, 2, 3]);
        var view = new SceneReferencesView(); var calls = 0; view.RelatedNavigator = (_, _) => { calls++; return Task.CompletedTask; };
        try
        {
            Pump(view.LoadAsync(source, new(source.IndexContentId, 0))); Pump(view.FilterArchiveReferencesAsync("")); var rows = (DataGrid)view.FindName("Rows");
            rows.SelectedIndex = 0; Assert.Equal("Ambiguous", view.SelectedReference!.Match); Assert.False(((Button)view.FindName("OpenReference")).IsEnabled); Pump(view.OpenSelectedReferenceAsync());
            rows.SelectedIndex = 1; Assert.Equal("Missing", view.SelectedReference!.Match); Assert.Null(view.SelectedReference.NavigationTarget); Pump(view.OpenSelectedReferenceAsync()); Assert.Equal(0, calls);
            rows.SelectedIndex = 2; Assert.True(((Button)view.FindName("OpenReference")).IsEnabled); Pump(view.OpenSelectedReferenceAsync()); Assert.Equal(1, calls);
            var tupleSource = Snapshot(root, "tuples", [(29u, DirectoryBytes(1)), (41u, new byte[1])]); WriteDependencies(tupleSource, 1);
            Pump(view.LoadAsync(tupleSource, new(tupleSource.IndexContentId, 0))); rows.SelectedIndex = 0; Assert.Null(view.SelectedReference!.NavigationTarget); Assert.False(((Button)view.FindName("OpenReference")).IsEnabled);
            Pump(view.OpenSelectedReferenceAsync()); Pump(view.FilterAsync("", directory: true)); rows.SelectedIndex = 0; Pump(view.OpenSelectedReferenceAsync()); Assert.Equal(1, calls);
            Assert.Equal(Visibility.Collapsed, ((ComboBoxItem)view.FindName("ArchiveReferenceMode")).Visibility);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void ArchiveNavigationAndJsonRejectChangedTablesAndCanceledOrStaleSelection() => Run(root =>
    {
        var source = Snapshot(root, "archive", [(3u, ContainerBytes([(41u, 2u, (byte)0xA5), (42u, 3u, (byte)0x5A)])), (41u, new byte[1]), (42u, new byte[1])]);
        var view = new SceneReferencesView(); var calls = 0; view.RelatedNavigator = (_, _) => { calls++; return Task.CompletedTask; };
        try
        {
            Pump(view.LoadAsync(source, new(source.IndexContentId, 0))); Pump(view.FilterArchiveReferencesAsync("")); var rows = (DataGrid)view.FindName("Rows"); rows.SelectedIndex = 0;
            Pump(Assert.ThrowsAnyAsync<OperationCanceledException>(() => view.OpenSelectedReferenceAsync(new(true)))); Assert.Equal(0, calls);
            var pending = view.OpenSelectedReferenceAsync(); rows.SelectedIndex = 1;
            try { Pump(pending); } catch (OperationCanceledException) { } Assert.Equal(0, calls);
            var stamp = File.GetLastWriteTimeUtc(source.Volumes[0].FilePath); var bytes = File.ReadAllBytes(source.Volumes[0].FilePath); bytes[28] ^= 1;
            File.WriteAllBytes(source.Volumes[0].FilePath, bytes); File.SetLastWriteTimeUtc(source.Volumes[0].FilePath, stamp);
            var error = Pump(Assert.ThrowsAsync<ArchiveException>(() => view.OpenSelectedReferenceAsync())); Assert.Equal("SourceChanged", error.Code); Assert.Equal(0, calls);
            var destination = Path.Combine(root, "output", "references.json"); error = Pump(Assert.ThrowsAsync<ArchiveException>(() => view.ExportToAsync(AssetExportFormat.Json, destination)));
            Assert.Equal("SourceChanged", error.Code); Assert.False(File.Exists(destination));
            Pump(view.ClearAsync()); Assert.Null(view.CurrentArchiveReferences); Assert.False(((Button)view.FindName("OpenReference")).IsEnabled);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void LargeComContainerInspectionAndJsonReadOnlyTheReferencePrefixAndOriginalStreams() => Run(root =>
    {
        var payload = ContainerBytes([(41u, 2u, (byte)0xA5)], 18 * 1024 * 1024); payload[^1] = 0x7F;
        var source = Snapshot(root, "archive", [(3u, payload), (41u, new byte[1])]); var view = new SceneReferencesView();
        try
        {
            // Exclusive byte-range lock prevents any accidental full-resource read during metadata inspection.
            using (var held = new FileStream(source.Volumes[0].FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                held.Lock(29, payload.LongLength - 29);
                try
                {
                    Pump(view.LoadAsync(source, new(source.IndexContentId, 0))); Assert.NotNull(view.CurrentArchiveReferences); Pump(view.FilterArchiveReferencesAsync("")); Assert.Single(view.CurrentReferences);
                    var jsonPath = Path.Combine(root, "output", "references.json"); Pump(view.ExportToAsync(AssetExportFormat.Json, jsonPath)); Assert.True(new FileInfo(jsonPath).Length < 8192);
                }
                finally { held.Unlock(29, payload.LongLength - 29); }
            }
            var destination = Path.Combine(root, "output", "original.bin"); Pump(view.ExportToAsync(AssetExportFormat.Original, destination)); Assert.Equal(payload, File.ReadAllBytes(destination));
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void VerifiedCallbackCanRouteToAnotherSourceInTheSameInspector() => Run(root =>
    {
        var source = Snapshot(root, "archive", [(3u, ContainerBytes([(3u, 2u, (byte)0)])), (3u, ContainerBytes([(99u, 999u, (byte)0)]))]); var view = new SceneReferencesView();
        try
        {
            view.RelatedNavigator = (target, token) => view.LoadAsync(source, target, token);
            Pump(view.LoadAsync(source, new(source.IndexContentId, 0))); Pump(view.FilterArchiveReferencesAsync("")); ((DataGrid)view.FindName("Rows")).SelectedIndex = 0;
            Pump(view.OpenSelectedReferenceAsync()); Assert.Equal(new ResourceIdentity(source.IndexContentId, 1), view.SelectedIdentity); Assert.NotNull(view.CurrentArchiveReferences);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void EmptyAndUnsupportedComTablesDoNotOfferArchiveNavigationMode() => Run(root =>
    {
        var view = new SceneReferencesView();
        try
        {
            foreach (var unsupported in new[] { false, true })
            {
                var payload = ContainerBytes([]); if (unsupported) BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 5);
                var source = Snapshot(root, unsupported ? "unsupported" : "empty", [(3u, payload)]);
                Pump(view.LoadAsync(source, new(source.IndexContentId, 0)));
                Assert.Equal(Visibility.Collapsed, ((ComboBoxItem)view.FindName("ArchiveReferenceMode")).Visibility);
                Assert.False(((ComboBoxItem)view.FindName("ArchiveReferenceMode")).IsEnabled); Assert.False(((Button)view.FindName("OpenReference")).IsEnabled);
                Assert.True(((Button)view.FindName("ExportOriginal")).IsEnabled);
            }
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void StyledFooterCountsTheDisplayedModeAndFilteredReferences() => Run(root =>
    {
        var source = Snapshot(root, "archive", [(3u, ContainerBytes([(41u, 2u, (byte)0), (99u, 500u, (byte)0), (42u, 3u, (byte)0)])), (41u, new byte[1]), (42u, new byte[1])]);
        WriteDependencies(source, 1); var view = new SceneReferencesView();
        var window = new Window { Content = view, Width = 1280, Height = 800, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Deviant;component/ControlStyles.xaml") });
        try
        {
            window.Show(); Pump(view.LoadAsync(source, new(source.IndexContentId, 0))); window.UpdateLayout();
            var status = (TextBlock)view.FindName("Status"); Assert.Single(view.CurrentReferences); Assert.Equal("1 dependency", status.Text);
            Pump(view.FilterArchiveReferencesAsync("")); window.UpdateLayout(); Assert.Equal(3, view.CurrentReferences.Count); Assert.Equal("3 archive references", status.Text);
            Pump(view.FilterArchiveReferencesAsync("Missing")); window.UpdateLayout(); Assert.Single(view.CurrentReferences); Assert.Equal("1 archive reference", status.Text);
            Pump(view.FilterAsync("", directory: true)); window.UpdateLayout(); Assert.Empty(view.CurrentReferences); Assert.Equal("Directory unavailable", status.Text);
            var directory = Snapshot(root, "directory", [(29u, DirectoryBytes(2))]); Pump(view.LoadAsync(directory, new(directory.IndexContentId, 0)));
            Pump(view.FilterAsync("", directory: true)); window.UpdateLayout(); Assert.Equal(2, view.CurrentReferences.Count); Assert.Equal("2 directory entries", status.Text);
        }
        finally { Pump(view.DisposeAsync().AsTask()); window.Close(); }
    });

    [Fact]
    public void GraphSharesRowsAndExportsDistinctReferencesWithoutOpeningAmbiguity() => Run(root =>
    {
        var payload=ContainerBytes([(41u,2u,(byte)1),(41u,2u,(byte)2),(41u,77u,(byte)3),(41u,3u,(byte)4)]);
        var source=Snapshot(root,"archive",[(3u,payload),(41u,new byte[]{1}),(41u,new byte[]{2}),(41u,new byte[]{3})],[1,2,3,3]);
        var view=new SceneReferencesView();var calls=new List<ResourceIdentity>();view.RelatedNavigator=(target,_)=>{calls.Add(target);return Task.CompletedTask;};
        try
        {
            Pump(view.LoadAsync(source,new(source.IndexContentId,0),sourceLabel:"Catalog"));Pump(view.FilterArchiveReferencesAsync(""));
            ((ToggleButton)view.FindName("GraphToggle")).IsChecked=true;
            Assert.Equal(Visibility.Collapsed,((DataGrid)view.FindName("Rows")).Visibility);
            var graph=view.CurrentReferenceGraph!;Assert.Equal(5,graph.Nodes.Count);Assert.Equal(4,graph.Edges.Count);
            Assert.Equal(graph.Nodes[1].Identity,graph.Nodes[2].Identity);Assert.NotEqual(graph.Nodes[1].NodeId,graph.Nodes[2].NodeId);
            Assert.Null(graph.Nodes[3].Identity);Assert.Null(graph.Nodes[4].Identity);
            view.GraphBrowser.SelectNode("reference/0");Assert.Equal(0,view.SelectedReference!.Ordinal);
            ((Button)view.GraphBrowser.FindName("OpenNode")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(WaitUntilAsync(()=>calls.Count==1));
            Assert.Equal(new ResourceIdentity(source.IndexContentId,1),calls[0]);
            foreach(var key in new[]{"reference/2","reference/3"})
            {view.GraphBrowser.SelectNode(key);Assert.False(((Button)view.GraphBrowser.FindName("OpenNode")).IsEnabled);Assert.False(((Button)view.FindName("OpenReference")).IsEnabled);}
            var jsonPath=Path.Combine(root,"output","graph.json");var svgPath=Path.Combine(root,"output","graph.svg");
            Pump(view.ExportToAsync(AssetExportFormat.Json,jsonPath));Pump(view.ExportToAsync(AssetExportFormat.Svg,svgPath));
            using(var json=JsonDocument.Parse(File.ReadAllText(jsonPath)))
            {Assert.Equal("ArchiveResourceReferences",json.RootElement.GetProperty("Domain").GetString());Assert.Equal(5,json.RootElement.GetProperty("Nodes").GetArrayLength());Assert.Equal(4,json.RootElement.GetProperty("Edges").GetArrayLength());Assert.Equal(source.IndexContentId,json.RootElement.GetProperty("SourceRevision").GetProperty("IndexContentId").GetString());}
            var svg=XDocument.Load(svgPath);XNamespace ns="http://www.w3.org/2000/svg";
            Assert.Equal(5,svg.Descendants(ns+"g").Count());
            Pump(view.FilterArchiveReferencesAsync("Missing"));Assert.Equal(2,view.CurrentReferenceGraph!.Nodes.Count);Assert.Equal(2,Assert.Single(view.CurrentReferences).Ordinal);
            Assert.Equal(2,view.CurrentReferenceGraph.Nodes[1].Metadata["rowOrdinal"] is { } ordinal?int.Parse(ordinal): -1);
            Pump(view.FilterAsync(""));Assert.Null(view.CurrentReferenceGraph);Assert.Equal(Visibility.Collapsed,((ToggleButton)view.FindName("GraphToggle")).Visibility);
            Pump(Assert.ThrowsAsync<InvalidOperationException>(()=>view.ExportToAsync(AssetExportFormat.Svg,svgPath,true)));
        }
        finally{Pump(view.DisposeAsync().AsTask());}
    });

    [Fact]
    public void GraphPagesAndStaleSourceChecksPreserveCompletedExports() => Run(root =>
    {
        var payload=ContainerBytes(Enumerable.Range(0,205).Select(_=>(41u,2u,(byte)0)).ToArray());
        var source=Snapshot(root,"archive",[(3u,payload),(41u,new byte[]{1})]);var view=new SceneReferencesView();
        try
        {
            Pump(view.LoadAsync(source,new(source.IndexContentId,0)));Pump(view.FilterArchiveReferencesAsync(""));((ToggleButton)view.FindName("GraphToggle")).IsChecked=true;
            Assert.Equal(101,view.CurrentReferenceGraph!.Nodes.Count);((Button)view.FindName("Next")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("reference/100",view.CurrentReferenceGraph!.Nodes[1].NodeId);Assert.Equal("100",view.CurrentReferenceGraph.Nodes[0].Metadata["pageOffset"]);
            ((Button)view.FindName("Next")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal(6,view.CurrentReferenceGraph!.Nodes.Count);
            var path=Path.Combine(root,"output","graph.svg");Pump(view.ExportToAsync(AssetExportFormat.Svg,path));var completed=File.ReadAllBytes(path);
            Pump(Assert.ThrowsAnyAsync<OperationCanceledException>(()=>view.ExportToAsync(AssetExportFormat.Svg,path,true,new(true))));Assert.Equal(completed,File.ReadAllBytes(path));
            var volume=source.Volumes[0];var stamp=File.GetLastWriteTimeUtc(volume.FilePath);var bytes=File.ReadAllBytes(volume.FilePath);bytes[28]^=1;File.WriteAllBytes(volume.FilePath,bytes);File.SetLastWriteTimeUtc(volume.FilePath,stamp);
            var error=Pump(Assert.ThrowsAsync<ArchiveException>(()=>view.ExportToAsync(AssetExportFormat.Svg,path,true)));Assert.Equal("SourceChanged",error.Code);Assert.Equal(completed,File.ReadAllBytes(path));
            Pump(view.ClearAsync());Assert.Null(view.CurrentReferenceGraph);Assert.Null(view.GraphBrowser.SelectedNode);Assert.Empty(view.CurrentReferences);
        }
        finally{Pump(view.DisposeAsync().AsTask());}
    });

    [Fact]
    public void GraphProjectionChecksRevisionPageBoundsAndTypedTargets() => Run(root =>
    {
        var source=Snapshot(root,"archive",[(3u,ContainerBytes([(41u,2u,(byte)0)])),(41u,new byte[]{1})]);
        var references=Pump(new LocalizationReferenceReader().ReadAsync(source,new(source.IndexContentId,0)));
        Assert.Throws<ArgumentException>(()=>ArchiveReferenceGraphs.BuildPage(source,references with { Revision=new(source.IndexContentId,"other-revision") },"Catalog",[0],0,1,""));
        Assert.Throws<ArgumentException>(()=>ArchiveReferenceGraphs.BuildPage(source,references with { ReferenceTableSha256=null },"Catalog",[0],0,1,""));
        Assert.Throws<ArgumentException>(()=>ArchiveReferenceGraphs.BuildPage(source,references,"Catalog",[0,0],0,2,""));
        Assert.Throws<ArgumentException>(()=>ArchiveReferenceGraphs.BuildPage(source,references,"Catalog",Enumerable.Range(0,101).ToArray(),0,101,""));
        Assert.Throws<ArgumentOutOfRangeException>(()=>ArchiveReferenceGraphs.BuildPage(source,references,"Catalog",[1],0,1,""));
        Assert.ThrowsAny<OperationCanceledException>(()=>ArchiveReferenceGraphs.BuildPage(source,references,"Catalog",[0],0,1,"",new(true)));
        var original=Assert.Single(references.Relationships);
        var foreign=original with { Reference=original.Reference with { Namespace=IdentifierNamespace.Bank } };
        var graph=ArchiveReferenceGraphs.BuildPage(source,references with { Relationships=[foreign] },"Catalog",[0],0,1,"");
        Assert.False(Assert.Single(graph.Edges).CanNavigate);Assert.Null(graph.Nodes[1].Identity);
        var wrong=original with { Reference=original.Reference with { ResourceType=42 } };
        graph=ArchiveReferenceGraphs.BuildPage(source,references with { Relationships=[wrong] },"Catalog",[0],0,1,"");
        Assert.False(Assert.Single(graph.Edges).CanNavigate);Assert.Null(graph.Nodes[1].Identity);
    });

    private static byte[] ContainerBytes((uint Type, uint Id, byte Trailer)[] references, int length = 0)
    {
        var bytes = new byte[Math.Max(length, 20 + references.Length * 9)]; "COM_CONT"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), checked((uint)bytes.Length - 16)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), checked((uint)references.Length));
        for (var index = 0; index < references.Length; index++) { var offset = 20 + index * 9; BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), references[index].Type); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4), references[index].Id); bytes[offset + 8] = references[index].Trailer; }
        return bytes;
    }
    private static async Task WaitUntilAsync(Func<bool> ready)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!ready()) await Task.Delay(10, timeout.Token); }

    private static ArchiveSnapshot Snapshot(string root, string name, (uint Type, byte[] Bytes)[] payloads, uint[]? ids = null)
    {
        var folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
        var indexPath = Path.Combine(folder, "archive.idx"); var volumePath = Path.Combine(folder, "archive.dat");
        var index = new byte[IndexLayout.HeaderLength + payloads.Length * IndexLayout.RecordLength]; IndexLayout.Magic.CopyTo(index);
        BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(20), 18); BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(101), (uint)payloads.Length);
        using var volume = new MemoryStream(); var records = new List<ResourceRecord>();
        for (var i = 0; i < payloads.Length; i++)
        {
            var item = payloads[i]; uint[] words = [item.Type, 1, ids?[i] ?? (uint)i + 1, (uint)volume.Length, (uint)item.Bytes.Length, 0, 0];
            for (var word = 0; word < words.Length; word++) BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(105 + i * 28 + word * 4), words[word]);
            records.Add(IndexLayout.ReadRecord(index.AsSpan(105 + i * 28, 28), i, "archive")); volume.Write(item.Bytes);
        }
        File.WriteAllBytes(indexPath, index); File.WriteAllBytes(volumePath, volume.ToArray()); var provider = new FileRevisionProvider();
        VolumeSource[] volumes = [new(0, "archive.dat", volumePath, provider.Capture(volumePath))];
        return new(indexPath, Convert.ToHexString(SHA256.HashData(index)).ToLowerInvariant(), 18, 0, [], (uint)records.Count,
            records, volumes, provider.Capture(indexPath), SourceIdentity.PhysicalStamp(volumes), []);
    }
    private static void WriteDependencies(ArchiveSnapshot source, int references)
    {
        using var bytes = new MemoryStream(); bytes.Write(IndexLayout.Magic); Word(bytes, 18); Word(bytes, (uint)source.Records.Count);
        foreach (var row in source.Records)
        {
            Word(bytes, row.Type); Word(bytes, row.Flags); Word(bytes, row.Id); Word(bytes, row.Ordinal == 0 ? (uint)references : 0);
            if (row.Ordinal != 0) continue;
            for (var i = 0; i < references; i++) { Word(bytes, 41); Word(bytes, 2); Word(bytes, (uint)(10000 + i)); Word(bytes, 0); Word(bytes, 3); }
        }
        File.WriteAllBytes(Path.ChangeExtension(source.IndexPath, ".dep"), bytes.ToArray());
    }
    private static byte[] DirectoryBytes(int count)
    {
        var bytes = new byte[24 + count * 8]; "DC_INFO "u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 3); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)(8 + count * 8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 2); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), (uint)count);
        for (var i = 0; i < count; i++) { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + i * 8), 41); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28 + i * 8), (uint)i + 2); }
        return bytes;
    }
    private static void Word(Stream stream, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes); }
    private static void Run(Action<string> action)
    {
        Exception? failure = null; var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "deviant-references-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); action(root); }
            catch (Exception error) { failure = error; }
            finally
            {
                var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(root).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The fixture directory is outside the temporary root.");
                Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(25)), "References control check timed out.");
        if (failure is not null) throw new InvalidOperationException("References control check failed.", failure);
    }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
}
