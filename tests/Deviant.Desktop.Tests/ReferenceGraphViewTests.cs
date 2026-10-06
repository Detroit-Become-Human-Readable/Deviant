using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Graphs;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class ReferenceGraphViewTests
{
    [Fact]
    public void StyledCardsSelectWithClickAndEnterAndOpenOnlyEligibleUniqueReferences() => Run(() =>
    {
        var graph = Fixture(); var view = new ReferenceGraphView(); var selections = 0; var opened = new List<EvidenceGraphNode>();
        view.SelectedNodeChanged += (_, _) => selections++; view.OpenRequested += (_, node) => opened.Add(node);
        var window = StyledWindow(view);
        try
        {
            window.Show(); view.Load(graph, "owner"); window.UpdateLayout();
            Assert.Same(graph.Nodes[0], view.SelectedNode); Assert.False(((Button)view.FindName("OpenNode")).IsEnabled);
            var canvas = (Canvas)view.FindName("GraphCanvas"); var cards = canvas.Children.OfType<Button>().ToArray(); Assert.Equal(5, cards.Length); Assert.Equal(4, view.VisualEdgeCount);
            var labels = cards.SelectMany(card => ((StackPanel)card.Content).Children.OfType<TextBlock>()).Select(text => text.Text).ToArray();
            Assert.Contains("Catalog sample", labels); Assert.Contains("Texture sample", labels); Assert.Contains("Missing", labels); Assert.Contains("Ambiguous", labels); Assert.DoesNotContain(labels, text => text.Contains("opaque-hash", StringComparison.Ordinal));
            var card = cards.Single(item => (string)item.Tag == "reference/0"); card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Same(graph.Nodes[1], view.SelectedNode); Assert.True(((Button)view.FindName("OpenNode")).IsEnabled);
            var duplicate = cards.Single(item => (string)item.Tag == "reference/1"); var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            duplicate.RaiseEvent(key); Assert.Same(graph.Nodes[2], view.SelectedNode); Assert.Empty(opened); Assert.True(key.Handled);
            ((Button)view.FindName("OpenNode")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Same(graph.Nodes[2], Assert.Single(opened));
            var pointer = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent }; card.RaiseEvent(pointer); Assert.Equal(2, opened.Count); Assert.Same(graph.Nodes[1], opened[1]);
            view.SelectNode("reference/2"); Assert.False(((Button)view.FindName("OpenNode")).IsEnabled); ((Button)view.FindName("OpenNode")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            view.SelectNode("reference/3"); Assert.False(((Button)view.FindName("OpenNode")).IsEnabled); ((Button)view.FindName("OpenNode")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(2, opened.Count); Assert.True(selections >= 5);
            var capture = new RenderTargetBitmap(900, 600, 96, 96, PixelFormats.Pbgra32); view.Measure(new Size(900, 600)); view.Arrange(new Rect(0, 0, 900, 600)); view.UpdateLayout(); capture.Render(view); Assert.Equal(900, capture.PixelWidth);
            var changedBrush = new SolidColorBrush(Colors.DarkSlateGray); window.Resources["SurfaceBackground"] = changedBrush; window.UpdateLayout(); view.SelectNode("owner");
            Assert.Same(changedBrush, card.Background);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ZoomPanFitLoadAndClearRemainBoundedAndResetSourceSelection() => Run(() =>
    {
        var view = new ReferenceGraphView(); var window = StyledWindow(view);
        try
        {
            window.Show(); var graph = Fixture(); view.Load(graph, "owner"); window.UpdateLayout(); view.Fit(); var fitted = view.Zoom; Assert.InRange(fitted, 0.05, 1);
            view.SetZoom(100); Assert.Equal(3, view.Zoom); view.SetZoom(-100); Assert.Equal(0.05, view.Zoom);
            view.SetPan(double.MaxValue, -double.MaxValue); Assert.True(double.IsFinite(view.Pan.X)); Assert.True(double.IsFinite(view.Pan.Y)); Assert.True(Math.Abs(view.Pan.X) <= 1000); Assert.True(Math.Abs(view.Pan.Y) <= 1000);
            Assert.Throws<ArgumentOutOfRangeException>(() => view.SetZoom(double.NaN)); Assert.Throws<ArgumentOutOfRangeException>(() => view.SetPan(double.PositiveInfinity, 0));
            view.Fit(); Assert.Equal(fitted, view.Zoom, 6);
            var canvas = (Canvas)view.FindName("GraphCanvas"); var viewport = (Border)view.FindName("Viewport"); var bounds = canvas.TransformToAncestor(viewport).TransformBounds(new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight));
            Assert.InRange(Math.Abs(bounds.X + bounds.Width / 2 - viewport.ActualWidth / 2), 0, 2); Assert.InRange(Math.Abs(bounds.Y + bounds.Height / 2 - viewport.ActualHeight / 2), 0, 2);
            view.SelectNode("reference/0"); view.SetPan(500, 500); view.Load(graph, "owner"); Assert.Equal("owner", view.SelectedNode!.NodeId); Assert.Equal(fitted, view.Zoom, 6);
            view.Clear(); Assert.Null(view.SelectedNode); Assert.Equal(0, view.VisualNodeCount); Assert.Equal(0, view.VisualEdgeCount); Assert.Equal(1, view.Zoom); Assert.Equal(new Vector(0, 0), view.Pan); Assert.False(((Button)view.FindName("OpenNode")).IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void InvalidDomainOversizeAndMalformedProjectionAreRejectedWithoutStaleCards() => Run(() =>
    {
        var view = new ReferenceGraphView(); var graph = Fixture(); view.Load(graph, "owner");
        Assert.Throws<ArgumentException>(() => view.Load(graph with { Domain = EvidenceGraphDomain.SceneHierarchy }, "owner")); Assert.Null(view.SelectedNode); Assert.Equal(0, view.VisualNodeCount);
        Assert.Throws<ArgumentException>(() => view.Load(graph with { Nodes = Enumerable.Range(0, 202).Select(index => graph.Nodes[0] with { NodeId = "node/" + index }).ToArray() }, "node/0"));
        Assert.Throws<ArgumentException>(() => view.Load(graph with { Edges = Enumerable.Range(0, 101).Select(index => graph.Edges[0] with { EdgeId = "edge/" + index }).ToArray() }, "owner"));
        Assert.Throws<ArgumentException>(() => view.Load(graph with { Nodes = [graph.Nodes[0], graph.Nodes[0]] }, "owner"));
        Assert.Throws<ArgumentException>(() => view.Load(graph with { Edges = [graph.Edges[0] with { TargetNodeIds = ["reference/0", "reference/1"] }] }, "owner"));
        Assert.Throws<ArgumentException>(() => view.Load(graph, "absent")); Assert.Throws<ArgumentException>(() => view.SelectNode("absent")); Assert.Equal(0, view.VisualNodeCount);
    });

    [Fact]
    public void ClaimedNavigationCannotOverrideMissingEvidenceOrAnIdentityMismatch() => Run(() =>
    {
        var graph = Fixture(); var view = new ReferenceGraphView(); var requests = 0; view.OpenRequested += (_, _) => requests++;
        var unverified = graph.Edges.Select(edge => edge with { CanNavigate = true, Relationship = edge.Relationship with { Evidence = edge.Relationship.Evidence with { Verified = false } } }).ToArray();
        view.Load(graph with { Edges = unverified }, "owner"); view.SelectNode("reference/0"); Assert.False(((Button)view.FindName("OpenNode")).IsEnabled); ((Button)view.FindName("OpenNode")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(0, requests);
        var wrong = graph.Nodes.Select(node => node.NodeId == "reference/0" ? node with { Identity = new ResourceIdentity("index", 99) } : node).ToArray(); view.Load(graph with { Nodes = wrong }, "owner"); view.SelectNode("reference/0"); Assert.False(((Button)view.FindName("OpenNode")).IsEnabled);
    });

    [Fact]
    public void NavigationRequiresAHostHandlerAndCanBeDisabledWithoutLosingSelection() => Run(() =>
    {
        var fixture = Fixture(); var single = fixture with { Nodes = fixture.Nodes.Take(2).ToArray(), Edges = fixture.Edges.Take(1).ToArray() };
        var view = new ReferenceGraphView(); view.Load(single, "owner"); Assert.Equal("1 reference", ((TextBlock)view.FindName("Status")).Text);
        view.SelectNode("reference/0"); Assert.False(((Button)view.FindName("OpenNode")).IsEnabled);
        var calls = 0; EventHandler<EvidenceGraphNode> handler = (_, _) => calls++; view.OpenRequested += handler; Assert.True(((Button)view.FindName("OpenNode")).IsEnabled);
        view.NavigationEnabled = false; Assert.False(((Button)view.FindName("OpenNode")).IsEnabled); ((Button)view.FindName("OpenNode")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(0, calls);
        Assert.Same(single.Nodes[1], view.SelectedNode); view.NavigationEnabled = true; ((Button)view.FindName("OpenNode")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(1, calls);
        view.OpenRequested -= handler; Assert.False(((Button)view.FindName("OpenNode")).IsEnabled);
    });

    [Fact]
    public void LargePagesStartAtReadableScaleAndExplicitFitProvidesAnOverview() => Run(() =>
    {
        var original = Fixture(); var owner = original.Nodes[0]; var references = Enumerable.Range(0, 100).Select(index => original.Nodes[1] with { NodeId = "reference/" + index, Label = "Resource sample " + (index + 1) }).ToArray();
        var edges = references.Select((node, index) => original.Edges[0] with { EdgeId = "edge/" + index, TargetNodeIds = [node.NodeId] }).ToArray();
        var graph = original with { Nodes = [owner, .. references], Edges = edges }; var view = new ReferenceGraphView(); var window = StyledWindow(view);
        try
        {
            window.Show(); view.Load(graph, "owner"); window.UpdateLayout(); Assert.InRange(view.Zoom, 0.7, 1); Assert.Equal(new Vector(12, 12), view.Pan);
            Assert.Equal(101, view.VisualNodeCount); Assert.Equal(100, view.VisualEdgeCount); Assert.Equal("owner", view.SelectedNode!.NodeId);
            var canvas = (Canvas)view.FindName("GraphCanvas"); var viewport = (Border)view.FindName("Viewport");
            var source = canvas.Children.OfType<Button>().Single(button => (string)button.Tag == "owner"); var sourceBounds = source.TransformToAncestor(viewport).TransformBounds(new Rect(0, 0, source.ActualWidth, source.ActualHeight));
            Assert.True(sourceBounds.Left >= 0 && sourceBounds.Top >= 0 && sourceBounds.Right < viewport.ActualWidth && sourceBounds.Bottom < viewport.ActualHeight);
            var first = canvas.Children.OfType<Button>().Single(button => (string)button.Tag == "reference/0");
            var label = ((StackPanel)first.Content).Children.OfType<TextBlock>().First(); Assert.Equal("Resource sample 1", label.Text); Assert.True(label.FontSize * view.Zoom >= 8);
            view.Fit(); Assert.True(view.Zoom < 0.7); view.Load(graph, "owner"); Assert.InRange(view.Zoom, 0.7, 1);
        }
        finally { window.Close(); }
    });

    private static EvidenceGraph Fixture()
    {
        var owner = new ResourceIdentity("index", 0); var target = new ResourceIdentity("index", 1);
        EvidenceGraphNode Node(string id, ResourceIdentity? identity, string label, ReferenceStatus? status) => new(id, identity, label, "Archive", "opaque-id", status, null, null, [], new Dictionary<string, string> { ["sha256"] = "opaque-hash" });
        var nodes = new[] { Node("owner", owner, "Catalog sample", null), Node("reference/0", target, "Texture sample", ReferenceStatus.Unique), Node("reference/1", target, "Texture repeated", ReferenceStatus.Unique), Node("reference/2", null, "Reference missing", ReferenceStatus.Missing), Node("reference/3", null, "Reference ambiguous", ReferenceStatus.Ambiguous) };
        var edges = new List<EvidenceGraphEdge>();
        for (var index = 0; index < 4; index++)
        {
            var status = index < 2 ? ReferenceStatus.Unique : index == 2 ? ReferenceStatus.Missing : ReferenceStatus.Ambiguous; ResourceIdentity[] candidates = index < 2 ? [target] : index == 2 ? [] : [target, owner];
            var relationship = new AssetRelationship(owner, AssetRelationshipKind.Dependency, "Reference", new(IdentifierNamespace.Archive, "2", owner, 41), new(status, candidates), new(true, "Reference table", "Typed archive reference"));
            edges.Add(new("edge/" + index, "owner", ["reference/" + index], relationship, index < 2));
        }
        return new(EvidenceGraphDomain.ArchiveResourceReferences, nodes, edges);
    }
    private static Window StyledWindow(ReferenceGraphView view)
    {
        var window = new Window { Content = view, Width = 900, Height = 650, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Deviant;component/ControlStyles.xaml") }); return window;
    }
    private static void Run(Action action)
    {
        Exception? failure = null; var thread = new Thread(() =>
        {
            try { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher)); action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Reference graph check timed out."); if (failure is not null) throw new InvalidOperationException("Reference graph check failed.", failure);
    }
}
