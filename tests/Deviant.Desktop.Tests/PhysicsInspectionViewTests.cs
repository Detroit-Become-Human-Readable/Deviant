using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Desktop;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class PhysicsInspectionViewTests
{
    [Fact]
    public void SetResultFiltersRowsShowsSelectedDetailsAndResetsSearchWhenChangingSections() => RunSta(() =>
    {
        EnsureApplication();
        var (window, view) = ShowView();
        try
        {
            view.SetResult(CreateFullResult());
            Pump();

            var search = Named<TextBox>(view, "Search");
            var types = Named<ListView>(view, "Types");
            var layouts = Named<ListView>(view, "Layouts");
            var items = Named<ListView>(view, "Items");
            var sections = Named<TabControl>(view, "Sections");

            Assert.Equal(2, types.Items.Count);
            search.Text = "Alpha";
            var matchingType = Assert.Single(types.Items.Cast<object>());
            Assert.Contains("Alpha", ((PhysicsTypeRow)matchingType).Label, StringComparison.Ordinal);

            types.SelectedIndex = 0;
            Assert.Contains("Alpha", Named<TextBlock>(view, "TypeTitle").Text, StringComparison.Ordinal);
            Assert.Contains("parameters", Named<TextBox>(view, "TypeDetails").Text, StringComparison.Ordinal);
            Assert.Contains("Identity:", Named<TextBox>(view, "TypeAdvanced").Text, StringComparison.Ordinal);

            sections.SelectedIndex = 1;
            Pump();
            Assert.Equal(string.Empty, search.Text);
            Assert.Equal(2, layouts.Items.Count);
            layouts.SelectedIndex = 1;
            Assert.Contains("Beta", Named<TextBlock>(view, "LayoutTitle").Text, StringComparison.Ordinal);
            Assert.Contains("Parent:", Named<TextBox>(view, "LayoutDetails").Text, StringComparison.Ordinal);

            sections.SelectedIndex = 2;
            Pump();
            Assert.Equal(2, items.Items.Count);
            items.SelectedIndex = 1;
            Assert.Contains("Beta", Named<TextBox>(view, "ItemDetails").Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            Pump();
        }
    });

    [Fact]
    public void EmptyPartialResultKeepsEarlierDiagnosticVisible() => RunSta(() =>
    {
        EnsureApplication();
        var (window, view) = ShowView();
        try
        {
            var owner = Owner();
            var revision = Revision();
            view.SetResult(new PhysicsMetadataInspectionResult(
                Decode<PhysicsEnvelopeDocument>(null, owner, revision, "EnvelopeRejected"), null, null, null, null));
            Pump();

            Assert.Equal("No complete metadata stages", Named<TextBlock>(view, "Summary").Text);
            Assert.Equal("1 diagnostic entries", Named<TextBlock>(view, "Status").Text);
            Assert.Empty(Named<ListView>(view, "Types").Items);
            var diagnostic = Assert.IsType<PhysicsInspectionDiagnostic>(Named<ListView>(view, "Diagnostics").Items[0]);
            Assert.Equal("Envelope", diagnostic.Stage);
            Assert.Equal("EnvelopeRejected", diagnostic.Code);
        }
        finally
        {
            window.Close();
            Pump();
        }
    });

    [Fact]
    public void CompactWindowCanResizeTypeLayoutItemAndDiagnosticsPanels()
    {
        RunSta(() =>
        {
            EnsureApplication();
            var (window, view) = ShowView(width: 1000, height: 660);
            try
            {
                view.SetResult(CreateFullResult());
                window.UpdateLayout();
                Pump();

                var sections = Named<TabControl>(view, "Sections");
                for (var tab = 0; tab < 3; tab++)
                {
                    sections.SelectedIndex = tab;
                    Pump();
                    var tabItem = Assert.IsType<TabItem>(sections.Items[tab]);
                    var content = Assert.IsType<Grid>(tabItem.Content);
                    var splitter = content.Children.OfType<GridSplitter>().Single();
                    var before = content.ColumnDefinitions.Select(column => column.ActualWidth).ToArray();
                    Drag(splitter, horizontal: 36, vertical: 0);
                    var after = content.ColumnDefinitions.Select(column => column.ActualWidth).ToArray();
                    Assert.True(Math.Abs(after[0] - before[0]) > 1,
                        $"The tab {tab} list/details splitter did not resize at 1000x660. Before={before[0]:F1}, after={after[0]:F1}.");
                    Assert.True(after.All(double.IsFinite));
                    Assert.True(after[0] >= 220 && after[2] >= 260);
                }

                sections.SelectedIndex = 2;
                Pump();
                var details = Named<TextBox>(view, "ItemDetails");
                Assert.True(details.IsVisible);
                Assert.True(details.ActualHeight >= 120,
                    $"Item details are clipped in the collapsed 1000x660 layout: {details.ActualHeight:F1}px.");

                var mainGrid = Ancestor<Grid>(sections)!;
                var diagnostics = Named<Expander>(view, "DiagnosticsExpander");
                var rowSplitter = Named<GridSplitter>(view, "DiagnosticsSplitter");
                var beforeRows = mainGrid.RowDefinitions.Select(row => row.ActualHeight).ToArray();
                Assert.Equal(Visibility.Collapsed, rowSplitter.Visibility);
                Assert.True(beforeRows[2] <= 50,
                    $"Collapsed diagnostics still reserve {beforeRows[2]:F1}px at 1000x660.");
                Assert.True(beforeRows[0] >= 400,
                    $"The collapsed diagnostics row did not return space to the main content: {beforeRows[0]:F1}px.");

                diagnostics.IsExpanded = true;
                Pump();
                var expandedRows = mainGrid.RowDefinitions.Select(row => row.ActualHeight).ToArray();
                Assert.Equal(Visibility.Visible, rowSplitter.Visibility);
                Assert.InRange(expandedRows[2], 135, 145);

                Drag(rowSplitter, horizontal: 0, vertical: -28);
                var afterRows = mainGrid.RowDefinitions.Select(row => row.ActualHeight).ToArray();
                Assert.True(afterRows[0] < expandedRows[0] - 1,
                    $"The diagnostics splitter did not reclaim main-pane height. Before={expandedRows[0]:F1}, after={afterRows[0]:F1}.");
                Assert.True(afterRows[2] > expandedRows[2] + 1,
                    $"The diagnostics splitter did not increase its row. Before={expandedRows[2]:F1}, after={afterRows[2]:F1}.");
                Assert.True(afterRows[0] >= 140, $"The expanded main pane fell below its 140px minimum: {afterRows[0]:F1}px.");
            }
            finally
            {
                window.Close();
                Pump();
            }
        });
    }

    [Fact]
    public void ExpandedDiagnosticsFitACompactHostedViewWithinStandardWindow()
    {
        RunSta(() =>
        {
            EnsureApplication();
            var (window, view) = ShowView(width: 1000, height: 660, viewHeight: 360);
            try
            {
                view.SetResult(CreateFullResult());
                var sections = Named<TabControl>(view, "Sections");
                sections.SelectedIndex = 2;
                Pump();

                Assert.InRange(window.ActualWidth, 995, 1005);
                Assert.InRange(window.ActualHeight, 655, 665);
                Assert.InRange(view.ActualHeight, 350, 370);
                var details = Named<TextBox>(view, "ItemDetails");
                Assert.True(details.IsVisible);
                Assert.True(details.ActualHeight >= 60,
                    $"Item details are unusable in the compact hosted view: {details.ActualHeight:F1}px.");

                var mainGrid = Ancestor<Grid>(sections)!;
                var diagnostics = Named<Expander>(view, "DiagnosticsExpander");
                var splitter = Named<GridSplitter>(view, "DiagnosticsSplitter");
                diagnostics.IsExpanded = true;
                Pump();

                var expandedRows = mainGrid.RowDefinitions.Select(row => row.ActualHeight).ToArray();
                Assert.Equal(Visibility.Visible, splitter.Visibility);
                Assert.True(expandedRows[0] >= 140,
                    $"Expanded main content fell below 140px: {expandedRows[0]:F1}px.");
                Assert.InRange(expandedRows[2], 80, 140);
                Assert.True(expandedRows.Sum() <= mainGrid.ActualHeight + 1,
                    $"Expanded diagnostics exceed the hosted view: rows={expandedRows.Sum():F1}px, grid={mainGrid.ActualHeight:F1}px.");

                Drag(splitter, horizontal: 0, vertical: -20);
                var resizedRows = mainGrid.RowDefinitions.Select(row => row.ActualHeight).ToArray();
                Assert.True(resizedRows[0] >= 140,
                    $"Resizing diagnostics pushed the compact main pane below 140px: {resizedRows[0]:F1}px.");
                Assert.True(resizedRows[2] >= expandedRows[2],
                    $"The compact diagnostics row did not retain its resized height: before={expandedRows[2]:F1}, after={resizedRows[2]:F1}.");
                Assert.True(resizedRows.Sum() <= mainGrid.ActualHeight + 1,
                    $"Resized diagnostics exceed the hosted view: rows={resizedRows.Sum():F1}px, grid={mainGrid.ActualHeight:F1}px.");
            }
            finally
            {
                window.Close();
                Pump();
            }
        });
    }

    [Fact]
    public void ClearReleasesResultAfterFilteredCollectionViewsAreDetached() => RunSta(() =>
    {
        EnsureApplication();
        var (window, view) = ShowView();
        try
        {
            var resultReference = SetFilterAndClear(view);
            CollectGarbage();
            Assert.False(resultReference.IsAlive, "Clear should detach filtered views and release the accepted result.");
            Assert.Null(Named<ListView>(view, "Types").ItemsSource);
            Assert.Null(Named<ListView>(view, "Layouts").ItemsSource);
            Assert.Null(Named<ListView>(view, "Items").ItemsSource);
            Assert.Null(Named<ListView>(view, "Diagnostics").ItemsSource);
        }
        finally
        {
            window.Close();
            Pump();
        }
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SetFilterAndClear(PhysicsInspectionView view)
    {
        var result = CreateFullResult();
        var weakReference = new WeakReference(result);
        view.SetResult(result);
        Named<TextBox>(view, "Search").Text = "Beta";
        Assert.Single(Named<ListView>(view, "Types").Items.Cast<object>());
        view.Clear();
        return weakReference;
    }

    private static PhysicsMetadataInspectionResult CreateFullResult()
    {
        var owner = Owner();
        var revision = Revision();
        var alpha = Declaration(owner, chunk: 3, slot: 1, "Alpha", 100);
        var beta = Declaration(owner, chunk: 3, slot: 2, "Beta", 120);
        var declarations = new PhysicsTypeDeclarationsDocument(owner, revision, Extent(owner, 20, 120), "fixture-hash",
            3, Leaf(owner, 3, 30), Leaf(owner, 4, 40), 1, Integer(owner, 50, 2),
            [alpha.Name!, beta.Name!], [alpha, beta], PhysicsTypeDeclarationCoverage.CompleteStructure, [], 0);

        var layoutAlpha = Layout(owner, typeChunk: 3, layoutChunk: 5, ordinal: 0, slot: 1);
        var layoutBeta = Layout(owner, typeChunk: 3, layoutChunk: 5, ordinal: 1, slot: 2);
        var layoutReferenceAlpha = new PhysicsLayoutReference(Integer(owner, 201, 1), PhysicsLayoutReferenceStatus.NoLinkRequested, []);
        var layoutReferenceBeta = new PhysicsLayoutReference(Integer(owner, 211, 2), PhysicsLayoutReferenceStatus.NoLinkRequested, []);
        var layouts = new PhysicsTypeLayoutsDocument(owner, revision, Extent(owner, 20, 120), "fixture-hash",
            3, 2, Leaf(owner, 3, 30), Leaf(owner, 5, 50), [alpha.Name!, beta.Name!],
            [LayoutRecord(layoutAlpha, layoutReferenceAlpha), LayoutRecord(layoutBeta, layoutReferenceBeta)],
            true, PhysicsTypeLayoutCoverage.CompleteStructure, [], 0);

        var itemAlphaIdentity = new PhysicsIndexedItemIdentity(owner, 6, 0);
        var itemBetaIdentity = new PhysicsIndexedItemIdentity(owner, 6, 1);
        var itemAlpha = Item(itemAlphaIdentity, new PhysicsIndexedTypeReference(1,
            PhysicsIndexedReferenceStatus.Unique, alpha.Identity, [], PhysicsIndexedReferenceStatus.Unique, [layoutAlpha]));
        var itemBeta = Item(itemBetaIdentity, new PhysicsIndexedTypeReference(2,
            PhysicsIndexedReferenceStatus.Unique, beta.Identity, [], PhysicsIndexedReferenceStatus.Unique, [layoutBeta]));
        var indexed = new PhysicsIndexedMetadataDocument(owner, revision, Extent(owner, 20, 120), "fixture-hash",
            3, 6, 2, Extent(owner, 200, 16), PhysicsIndexedTableStatus.Complete, [IndexedLeaf(owner, 6, 60)],
            PhysicsIndexedTableStatus.Missing, [], [itemAlpha, itemBeta], null, true, true, [],
            PhysicsIndexedMetadataCoverage.CompleteStructure, 0);

        var envelope = new PhysicsEnvelopeDocument(owner, revision, 1, 1, 5, 0,
            Extent(owner, 10, 8), Extent(owner, 20, 0), ReadOnlyMemory<byte>.Empty, "fixture-hash",
            null, new Dictionary<string, DecodedExtent>(), [], ReadOnlyMemory<byte>.Empty);
        var tagged = new PhysicsTaggedMetadataDocument(owner, revision, Extent(owner, 20, 0), "fixture-hash",
            [], [], PhysicsTaggedMetadataCoverage.CompleteFraming, [], null, 0);
        return new PhysicsMetadataInspectionResult(
            Decode(envelope, owner, revision), Decode(tagged, owner, revision),
            Decode(declarations, owner, revision), Decode(layouts, owner, revision), Decode(indexed, owner, revision));
    }

    private static PhysicsTypeDeclaration Declaration(ResourceIdentity owner, int chunk, int slot, string name, long offset) =>
        new(new PhysicsTypeSlotIdentity(owner, chunk, slot), Extent(owner, offset, 8), Integer(owner, offset + 1, slot),
            new PhysicsTypeName(slot, name, Extent(owner, offset + 2, name.Length), ReadOnlyMemory<byte>.Empty),
            Integer(owner, offset + 3, 0), []);

    private static PhysicsTypeLayoutRecord LayoutRecord(PhysicsLayoutRecordIdentity identity, PhysicsLayoutReference parent) =>
        new(identity, Extent(identity.Owner, identity.RecordOrdinal * 10L + 1, 9), Integer(identity.Owner, 2, identity.Slot),
            parent, Integer(identity.Owner, 3, 0), [], null, null, [], null, []);

    private static PhysicsIndexedItemRow Item(PhysicsIndexedItemIdentity identity, PhysicsIndexedTypeReference type) =>
        new(identity, false, PhysicsIndexedItemProjection.TypeAndData, 1, 0, 0, 0, 0, 0,
            Extent(identity.Owner, 1, 4), Extent(identity.Owner, 5, 4), Extent(identity.Owner, 9, 4), type,
            new PhysicsIndexedDataPosition(0, PhysicsIndexedDataPositionStatus.WithinDataBody, 0, Extent(identity.Owner, 200, 0)),
            null, ReadOnlyMemory<byte>.Empty);

    private static PhysicsLayoutRecordIdentity Layout(ResourceIdentity owner, int typeChunk, int layoutChunk, int ordinal, int slot) =>
        new(owner, typeChunk, layoutChunk, ordinal, slot);

    private static PhysicsTypeDeclarationLeaf Leaf(ResourceIdentity owner, int chunk, long offset) =>
        new(chunk, Extent(owner, offset, 8), Extent(owner, offset + 8, 4), 0, "fixture-hash",
            ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);

    private static PhysicsIndexedMetadataLeaf IndexedLeaf(ResourceIdentity owner, int chunk, long offset) =>
        new(chunk, Extent(owner, offset, 8), Extent(owner, offset + 8, 4), 0, "fixture-hash", true,
            ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);

    private static PhysicsTypeInteger Integer(ResourceIdentity owner, long offset, int value) =>
        new(unchecked((uint)value), value, Extent(owner, offset, 1), ReadOnlyMemory<byte>.Empty);

    private static DecodedExtent Extent(ResourceIdentity owner, long offset, long length) => new(owner, offset, length);
    private static ResourceIdentity Owner() => new("fixture-index", 7, "fixture-owner");
    private static SourceRevision Revision() => new("fixture-index", "fixture-revision");

    private static DecodeResult<T> Decode<T>(T? value, ResourceIdentity identity, SourceRevision revision,
        params string[] diagnostics) => new(value, identity, revision, [], [], diagnostics, [], new Dictionary<string, string>());

    private static (Window Window, PhysicsInspectionView View) ShowView(double width = 1000, double height = 660,
        double? viewHeight = null)
    {
        var view = new PhysicsInspectionView { Height = viewHeight ?? double.NaN };
        var window = new Window
        {
            Content = view,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        window.Show();
        window.UpdateLayout();
        Pump();
        return (window, view);
    }

    private static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        Assert.IsType<T>(root.FindName(name));

    private static void Drag(GridSplitter splitter, double horizontal, double vertical)
    {
        splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        splitter.RaiseEvent(new DragDeltaEventArgs(horizontal, vertical) { RoutedEvent = Thumb.DragDeltaEvent });
        splitter.RaiseEvent(new DragCompletedEventArgs(horizontal, vertical, false) { RoutedEvent = Thumb.DragCompletedEvent });
        splitter.UpdateLayout();
        Pump();
    }

    private static T? Ancestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private static void EnsureApplication()
    {
        var app = Application.Current ?? new App();
        var assemblyName = typeof(PhysicsInspectionView).Assembly.GetName().Name
            ?? throw new InvalidOperationException("The desktop assembly has no name.");
        if (app.Resources.MergedDictionaries.Any(dictionary =>
                dictionary.Source?.OriginalString.EndsWith("ControlStyles.xaml", StringComparison.OrdinalIgnoreCase) == true))
            return;

        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"/{assemblyName};component/ControlStyles.xaml", UriKind.Relative)
        });
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        var settle = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => settle.Continue = false));
        Dispatcher.PushFrame(settle);
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("The WPF physics view check failed.", error);
    }
}
