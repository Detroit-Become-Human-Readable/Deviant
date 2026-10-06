using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Deviant.Assets;
using Deviant.Graphs;

namespace Deviant.Desktop;

public partial class ReferenceGraphView : UserControl
{
    private const int MaximumNodes = 201, MaximumEdges = 100;
    private const double CardWidth = 230, CardHeight = 72, RowPitch = 90, CanvasWidth = 650;
    private const double MinimumZoom = 0.05, MaximumZoom = 3;
    private readonly Dictionary<string, EvidenceGraphNode> nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EvidenceGraphEdge> incoming = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> cards = new(StringComparer.Ordinal);
    private EvidenceGraph? graph;
    private string? sourceNodeId;
    private bool dragging, fitPending, readablePending;
    private Point dragOrigin;
    private Vector offsetOrigin;

    public ReferenceGraphView() => InitializeComponent();
    public EvidenceGraphNode? SelectedNode { get; private set; }
    public event EventHandler? SelectedNodeChanged;
    private EventHandler<EvidenceGraphNode>? openRequested;
    private bool navigationEnabled = true;
    public event EventHandler<EvidenceGraphNode>? OpenRequested
    {
        add { openRequested += value; UpdateOpenAvailability(); }
        remove { openRequested -= value; UpdateOpenAvailability(); }
    }
    public bool NavigationEnabled
    {
        get => navigationEnabled;
        set { navigationEnabled = value; UpdateOpenAvailability(); }
    }
    public double Zoom => GraphScale.ScaleX;
    public Vector Pan => new(GraphOffset.X, GraphOffset.Y);
    public int VisualNodeCount => cards.Count;
    public int VisualEdgeCount => graph?.Edges.Count ?? 0;

    public void Load(EvidenceGraph value, string ownerNodeId)
    {
        ArgumentNullException.ThrowIfNull(value); Clear();
        if (value.Domain != EvidenceGraphDomain.ArchiveResourceReferences) throw new ArgumentException("This view requires archive resource references.", nameof(value));
        if (value.Nodes.Count is < 1 or > MaximumNodes || value.Edges.Count > MaximumEdges) throw new ArgumentException("The graph exceeds the page limits.", nameof(value));
        var lookup = new Dictionary<string, EvidenceGraphNode>(StringComparer.Ordinal);
        foreach (var node in value.Nodes)
            if (string.IsNullOrWhiteSpace(node.NodeId) || !lookup.TryAdd(node.NodeId, node)) throw new ArgumentException("Each graph node requires a unique ID.", nameof(value));
        if (!lookup.TryGetValue(ownerNodeId, out var owner)) throw new ArgumentException("The source node is unavailable.", nameof(ownerNodeId));
        var edgeIds = new HashSet<string>(StringComparer.Ordinal); var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in value.Edges)
        {
            if (string.IsNullOrWhiteSpace(edge.EdgeId) || !edgeIds.Add(edge.EdgeId) || edge.SourceNodeId != ownerNodeId || edge.TargetNodeIds.Count != 1 ||
                edge.TargetNodeIds[0] == ownerNodeId || !lookup.ContainsKey(edge.TargetNodeIds[0]) || !targets.Add(edge.TargetNodeIds[0]))
                throw new ArgumentException("Each reference card requires one distinct edge from the source.", nameof(value));
        }
        if (lookup.Count != targets.Count + 1) throw new ArgumentException("The graph contains unrelated nodes.", nameof(value));
        graph = value; sourceNodeId = ownerNodeId;
        foreach (var pair in lookup) nodes.Add(pair.Key, pair.Value);
        foreach (var edge in value.Edges) incoming.Add(edge.TargetNodeIds[0], edge);
        var height = Math.Max(140, value.Edges.Count * RowPitch + 40); GraphCanvas.Width = CanvasWidth; GraphCanvas.Height = height;
        var positions = new Dictionary<string, Point>(StringComparer.Ordinal) { [ownerNodeId] = new(24, 24) };
        for (var index = 0; index < value.Edges.Count; index++) positions[value.Edges[index].TargetNodeIds[0]] = new(380, 24 + index * RowPitch);
        foreach (var edge in value.Edges)
        {
            var from = positions[ownerNodeId]; var to = positions[edge.TargetNodeIds[0]];
            var line = new Line { X1 = from.X + CardWidth, Y1 = from.Y + CardHeight / 2, X2 = to.X, Y2 = to.Y + CardHeight / 2, StrokeThickness = 1.4, IsHitTestVisible = false };
            line.SetResourceReference(Shape.StrokeProperty, "ControlBorder"); GraphCanvas.Children.Add(line);
        }
        AddCard(owner, positions[ownerNodeId]);
        foreach (var edge in value.Edges) { var node = lookup[edge.TargetNodeIds[0]]; AddCard(node, positions[node.NodeId]); }
        Status.Text = $"{value.Edges.Count:N0} {(value.Edges.Count == 1 ? "reference" : "references")}"; SelectNode(ownerNodeId);
        if (value.Edges.Count <= 6) { fitPending = true; Fit(); }
        else { readablePending = true; PlaceReadable(); }
    }
    private void AddCard(EvidenceGraphNode node, Point position)
    {
        var label = new TextBlock { Text = node.Label, TextWrapping = TextWrapping.Wrap, MaxHeight = 38, TextTrimming = TextTrimming.CharacterEllipsis };
        var content = new StackPanel(); content.Children.Add(label);
        var status = node.Status switch { ReferenceStatus.Unique => "Resolved", ReferenceStatus.Missing => "Missing", ReferenceStatus.Ambiguous => "Ambiguous", ReferenceStatus.Unresolved => "Unresolved", _ => "" };
        if (status.Length > 0) content.Children.Add(new TextBlock { Text = status, FontSize = 11, Margin = new(0, 3, 0, 0) });
        var card = new Button { Content = content, Width = CardWidth, Height = CardHeight, Padding = new(10, 8, 10, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center, Tag = node.NodeId };
        card.SetResourceReference(Control.BackgroundProperty, "SurfaceBackground"); card.SetResourceReference(Control.ForegroundProperty, "WindowForeground"); card.SetResourceReference(Control.BorderBrushProperty, "ControlBorder");
        card.Click += (_, _) => SelectNode(node.NodeId);
        card.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { SelectNode(node.NodeId); e.Handled = true; } };
        card.MouseDoubleClick += (_, e) => { SelectNode(node.NodeId); RequestOpen(); e.Handled = true; };
        Canvas.SetLeft(card, position.X); Canvas.SetTop(card, position.Y); cards.Add(node.NodeId, card); GraphCanvas.Children.Add(card);
    }
    public void SelectNode(string nodeId)
    {
        if (!nodes.TryGetValue(nodeId, out var node)) throw new ArgumentException("The node is unavailable.", nameof(nodeId));
        if (ReferenceEquals(SelectedNode, node)) { UpdateOpenAvailability(); return; }
        if (SelectedNode is { } previous && cards.TryGetValue(previous.NodeId, out var old)) old.SetResourceReference(Control.BackgroundProperty, "SurfaceBackground");
        SelectedNode = node; cards[nodeId].SetResourceReference(Control.BackgroundProperty, "SelectionBackground"); UpdateOpenAvailability(); SelectedNodeChanged?.Invoke(this, EventArgs.Empty);
    }
    private bool IsEligible(EvidenceGraphNode node)
    {
        if (node.Identity is null || node.Status is ReferenceStatus.Missing or ReferenceStatus.Ambiguous or ReferenceStatus.Unresolved || !incoming.TryGetValue(node.NodeId, out var edge) || !edge.CanNavigate) return false;
        var relationship = edge.Relationship;
        return relationship.Reference.Namespace == IdentifierNamespace.Archive && relationship.Reference.ResourceType is not null && relationship.NavigationTarget == node.Identity &&
            sourceNodeId is not null && nodes[sourceNodeId].Identity == relationship.Source && relationship.Reference.Owner == relationship.Source;
    }
    private void UpdateOpenAvailability() => OpenNode.IsEnabled = navigationEnabled && openRequested is not null && SelectedNode is { } node && IsEligible(node);
    private void RequestOpen() { if (navigationEnabled && openRequested is not null && SelectedNode is { } node && IsEligible(node)) openRequested.Invoke(this, node); }
    private void Open_Click(object sender, RoutedEventArgs e) => RequestOpen();
    public void SetZoom(double requested)
    {
        if (!double.IsFinite(requested)) throw new ArgumentOutOfRangeException(nameof(requested));
        ZoomAt(Math.Clamp(requested, MinimumZoom, MaximumZoom), new(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2)); fitPending = readablePending = false;
    }
    private void ZoomAt(double value, Point anchor)
    {
        var previous = Zoom; GraphOffset.X = anchor.X - (anchor.X - GraphOffset.X) * value / previous; GraphOffset.Y = anchor.Y - (anchor.Y - GraphOffset.Y) * value / previous;
        GraphScale.ScaleX = GraphScale.ScaleY = value; ClampPan();
    }
    private void PlaceReadable()
    {
        if (Viewport.ActualWidth <= 1 || Viewport.ActualHeight <= 1) { readablePending = true; return; }
        var scale = Math.Clamp((Viewport.ActualWidth - 24) / CanvasWidth, 0.7, 1);
        GraphScale.ScaleX = GraphScale.ScaleY = scale; GraphOffset.X = 12; GraphOffset.Y = 12; ClampPan(); readablePending = false;
    }
    public void Fit()
    {
        if (graph is null) return;
        if (Viewport.ActualWidth <= 1 || Viewport.ActualHeight <= 1) { fitPending = true; return; }
        var scale = Math.Clamp(Math.Min((Viewport.ActualWidth - 24) / GraphCanvas.Width, (Viewport.ActualHeight - 24) / GraphCanvas.Height), MinimumZoom, 1);
        GraphScale.ScaleX = GraphScale.ScaleY = scale; GraphOffset.X = (Viewport.ActualWidth - GraphCanvas.Width * scale) / 2; GraphOffset.Y = (Viewport.ActualHeight - GraphCanvas.Height * scale) / 2; ClampPan(); fitPending = readablePending = false;
    }
    public void SetPan(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
        GraphOffset.X = x; GraphOffset.Y = y; ClampPan(); fitPending = readablePending = false;
    }
    private void ClampPan()
    {
        var horizontal = Math.Max(80, Viewport.ActualWidth); var vertical = Math.Max(80, Viewport.ActualHeight);
        GraphOffset.X = Math.Clamp(GraphOffset.X, -Math.Max(horizontal, GraphCanvas.Width * Zoom), horizontal);
        GraphOffset.Y = Math.Clamp(GraphOffset.Y, -Math.Max(vertical, GraphCanvas.Height * Zoom), vertical);
    }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(Zoom * 1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(Zoom / 1.25);
    private void Fit_Click(object sender, RoutedEventArgs e) => Fit();
    private void MouseWheelZoom(object sender, MouseWheelEventArgs e) { if (graph is null) return; ZoomAt(Math.Clamp(Zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), MinimumZoom, MaximumZoom), e.GetPosition(Viewport)); fitPending = readablePending = false; e.Handled = true; }
    private void PanStart(object sender, MouseButtonEventArgs e)
    {
        if (graph is null || (e.ChangedButton != MouseButton.Middle && !(e.ChangedButton == MouseButton.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))) return;
        dragging = true; dragOrigin = e.GetPosition(Viewport); offsetOrigin = Pan; Viewport.CaptureMouse(); e.Handled = true;
    }
    private void PanMove(object sender, MouseEventArgs e) { if (dragging) { var position = e.GetPosition(Viewport); SetPan(offsetOrigin.X + position.X - dragOrigin.X, offsetOrigin.Y + position.Y - dragOrigin.Y); e.Handled = true; } }
    private void PanEnd(object sender, MouseButtonEventArgs e) { if (dragging) { dragging = false; Viewport.ReleaseMouseCapture(); e.Handled = true; } }
    private void PanLost(object sender, MouseEventArgs e) => dragging = false;
    private void ViewportChanged(object sender, SizeChangedEventArgs e) { if (fitPending) Fit(); else if (readablePending) PlaceReadable(); else ClampPan(); }
    public void Clear()
    {
        if (dragging) { dragging = false; Viewport.ReleaseMouseCapture(); }
        graph = null; sourceNodeId = null; nodes.Clear(); incoming.Clear(); cards.Clear(); GraphCanvas.Children.Clear(); GraphCanvas.Width = GraphCanvas.Height = 0;
        GraphScale.ScaleX = GraphScale.ScaleY = 1; GraphOffset.X = GraphOffset.Y = 0; fitPending = readablePending = false; SelectedNode = null; OpenNode.IsEnabled = false; Status.Text = ""; SelectedNodeChanged?.Invoke(this, EventArgs.Empty);
    }
}
