using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using SharpDX;
using HelixToolkit.Maths;
using WpfPoint = System.Windows.Point;
using Numerics = System.Numerics;
using PerspectiveCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using HitTestResult = HelixToolkit.SharpDX.HitTestResult;

namespace Deviant.Desktop;

public partial class AssetViewport : UserControl, IAsyncDisposable
{
    private readonly SemaphoreSlim loadGate = new(1, 1);
    private readonly List<Part> parts = [];
    private readonly List<IDisposable> leases = [];
    private readonly Dictionary<string, RenderOccurrence> occurrences = [];
    private readonly Dictionary<Guid, string> instanceKeys = [];
    private readonly OccurrenceVisibility visibility = new();
    private readonly HashSet<Key> flyKeys = [];
    private readonly List<RenderIssue> rendererIssues = [];
    private readonly Stopwatch flyClock = Stopwatch.StartNew();
    private readonly DispatcherTimer flyTimer;
    private readonly DispatcherTimer hiddenTimer;
    private AssetViewportService? service;
    private RenderAssetSnapshot? snapshot;
    private CancellationTokenSource? loadCancellation;
    private long generation;
    private bool disposed;
    private WpfPoint pointerStart;
    private WpfPoint flyPointer;
    private bool flyDragging;
    private double lastFlyTime;
    private string? selection;
    private Exception? renderException;

    public AssetViewport()
    {
        InitializeComponent();
        flyTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, FlyTick, Dispatcher);
        flyTimer.Stop();
        hiddenTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, HiddenTick, Dispatcher);
        hiddenTimer.Stop();
        LostKeyboardFocus += (_, _) => StopFlight();
        Loaded += (_, _) => RefreshHiddenAnimation();
        IsVisibleChanged += (_, _) => RefreshHiddenAnimation();
        RenderView.Loaded += (_, _) => RefreshHiddenAnimation();
        RenderView.IsVisibleChanged += (_, _) => RefreshHiddenAnimation();
        Unloaded += (_, _) => { StopFlight(); hiddenTimer.Stop(); };
        RenderView.RenderExceptionOccurred += (_, args) =>
        {
            args.Handled = true;
            void Report()
            {
                renderException = args.Exception;
                StatusText.Text = "Unable to render scene";
                loadCancellation?.Cancel();
                RefreshHiddenAnimation();
                RenderFailed?.Invoke(this, args.Exception);
            }
            if (Dispatcher.CheckAccess()) Report(); else Dispatcher.BeginInvoke(Report);
        };
    }

    public event EventHandler<string?>? SelectionChanged;
    public event EventHandler<RenderLoadProgress>? LoadProgressChanged;
    public event EventHandler<Exception>? RenderFailed;
    public Exception? LastRenderException => renderException;
    public string? SelectedOccurrenceKey => selection;
    public int RenderedOccurrenceCount => occurrences.Count;
    public int RenderedPartCount => parts.Sum(x => x.Occurrences.Count);
    public int VisibleOccurrenceCount => occurrences.Keys.Count(visibility.IsVisible);
    public int VisiblePartCount => parts.Sum(part => part.Occurrences.Count(row => visibility.IsVisible(row.Key)));
    public bool IsHiddenAnimationRunning => hiddenTimer.IsEnabled;
    public bool ShowHidden
    {
        get => visibility.ShowHidden;
        set
        {
            Dispatcher.VerifyAccess();
            if (visibility.ShowHidden == value) return;
            visibility.ShowHidden = value;
            foreach (var part in parts) ApplyInstances(part);
            UpdateVisibleStatus(); RefreshHiddenAnimation(); RenderView.InvalidateRender();
        }
    }
    public IReadOnlyList<RenderIssue> RendererIssues => rendererIssues;
    public Viewport3DX Viewport => RenderView;

    public void SetService(AssetViewportService sharedService)
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(sharedService);
        if (service is not null && !ReferenceEquals(service, sharedService))
            throw new InvalidOperationException("Unload the viewport before changing its shared service.");
        service = sharedService;
        RenderView.EffectsManager = sharedService.EffectsManager;
    }

    public async Task LoadSnapshotAsync(RenderAssetSnapshot source, CancellationToken token = default)
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        if (service is null) throw new InvalidOperationException("The viewport service has not been assigned.");
        long currentGeneration = ++generation;
        loadCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        loadCancellation = cancellation;
        bool entered = false;
        try
        {
            await loadGate.WaitAsync(cancellation.Token);
            entered = true;
            ClearScene();
            renderException = null;
            CheckCurrent();
            leases.Add(source.AcquireLease());
            snapshot = source;
            if (source.Occurrences.Select(x => x.Key).Distinct().Count() != source.Occurrences.Count)
                throw new InvalidDataException("Duplicate occurrence keys.");
            foreach (var occurrence in source.Occurrences)
            {
                CheckCurrent();
                if (!source.Meshes.ContainsKey(occurrence.GeometryKey))
                    throw new InvalidDataException($"The geometry for {occurrence.Label} is missing.");
                occurrences.Add(occurrence.Key, occurrence);
                visibility.Add(occurrence.Key, occurrence.SourceVisible);
            }
            int total = source.Meshes.Values.Sum(m => m.Geometry.Primitives.Sum(p => Math.Max(1, p.Submeshes.Count)));
            int completed = 0;
            foreach (var mesh in source.Meshes.Values)
            {
                var rows = source.Occurrences.Where(x => x.GeometryKey == mesh.Key).ToArray();
                if (rows.Length == 0) continue;
                foreach (var primitive in mesh.Geometry.Primitives)
                {
                    RenderMaterial? materialSource = null;
                    if (mesh.MaterialsByPrimitive?.TryGetValue(primitive.Key, out var identity) == true)
                        source.Materials.TryGetValue(identity, out materialSource);
                    var material = await service.AcquireMaterialAsync(source, materialSource, primitive, cancellation.Token);
                    leases.Add(material);
                    if (materialSource is not null)
                        foreach (string diagnostic in material.Diagnostics)
                            rendererIssues.Add(new(materialSource.Asset.Identity, "MaterialRenderer", diagnostic));
                    var submeshes = primitive.Submeshes.Count > 0 ? primitive.Submeshes :
                        [new GeometrySubmesh(primitive.Key, 0, primitive.Indices.Count, [])];
                    foreach (var submesh in submeshes)
                    {
                        CheckCurrent();
                        var geometry = await service.AcquireGeometryAsync(source, mesh, primitive, submesh,
                            material.UvSet, material.NormalMapped, cancellation.Token);
                        leases.Add(geometry);
                        CheckCurrent();
                        // Matrices, GUIDs, row table, and pending instance-buffer copies.
                        leases.Add(source.MemoryBudget.Reserve(checked((long)rows.Length * 384), "instance upload copies"));
                        leases.Add(service.ReserveGpu(checked((long)rows.Length * 104), "instance buffers"));
                        var keys = rows.Select(x => Guid.NewGuid()).ToArray();
                        for (int i = 0; i < rows.Length; i++) instanceKeys.Add(keys[i], rows[i].Key);
                        InstancingMeshGeometryModel3D model = new SurfaceInstancingModel(material.TechniqueName);
                        model.Geometry = geometry.Geometry; model.Material = material.Material;
                        model.Instances = rows.Select(x => ViewportGeometry.NativeMatrix(x.World)).ToArray();
                        model.InstanceParamArray = rows.Select(x => InstanceAppearance(x.Key)).ToArray();
                        model.InstanceIdentifiers = keys; model.OctreeManager = new InstancingModel3DOctreeManager();
                        model.CullMode = SharpDX.Direct3D11.CullMode.None; model.IsHitTestVisible = true;
                        var part = new Part(model, rows, keys);
                        ApplyInstances(part);
                        parts.Add(part);
                        RenderView.Items.Add(model);
                        Progress("Loading", ++completed, total);
                        // Let Helix publish one bounded mesh upload before the next conversion.
                        await WaitForFrameAsync(cancellation.Token);
                    }
                }
            }
            CheckCurrent();
            FrameRegion();
            UpdateVisibleStatus();
            RefreshHiddenAnimation();
            Progress("Ready", completed, total);
        }
        catch (Exception error)
        {
            if (entered)
            {
                ClearScene();
                StatusText.Text = renderException is not null ? "Unable to render scene" :
                    cancellation.IsCancellationRequested ? "" : "Unable to load scene";
            }
            if (renderException is not null && error is OperationCanceledException)
                throw new InvalidOperationException("Unable to render scene.", renderException);
            throw;
        }
        finally
        {
            if (entered) loadGate.Release();
            if (ReferenceEquals(loadCancellation, cancellation)) loadCancellation = null;
            cancellation.Dispose();
        }

        void CheckCurrent()
        {
            if (renderException is not null) throw new InvalidOperationException("Unable to render scene.", renderException);
            cancellation.Token.ThrowIfCancellationRequested();
            if (currentGeneration != generation || disposed) throw new OperationCanceledException(cancellation.Token);
        }
    }

    private async Task WaitForFrameAsync(CancellationToken token)
    {
        if (!RenderView.IsLoaded || !RenderView.IsVisible || RenderView.RenderHost?.IsRendering != true)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render, token);
            return;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Rendered(object? sender, EventArgs args) => completion.TrySetResult();
        RenderView.OnRendered += Rendered;
        try
        {
            RenderView.InvalidateRender();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        }
        finally { RenderView.OnRendered -= Rendered; }
    }

    private void Progress(string stage, int completed, int total)
    {
        StatusText.Text = stage == "Ready" ? $"{VisibleOccurrenceCount:N0} objects" : $"{completed:N0} / {total:N0}";
        LoadProgressChanged?.Invoke(this, new(stage, completed, total, snapshot?.MemoryBudget.UsedBytes ?? 0));
    }

    public void SetSelection(string? occurrenceKey)
    {
        Dispatcher.VerifyAccess();
        var next = occurrenceKey is not null && occurrences.ContainsKey(occurrenceKey) ? occurrenceKey : null;
        if (selection == next) return;
        selection = next;
        foreach (var part in parts)
            part.Model.InstanceParamArray = part.Model.InstanceIdentifiers!.Select(id => InstanceAppearance(instanceKeys[id])).ToArray();
        RenderView.InvalidateRender();
    }

    public void SetVisibility(string occurrenceKey, bool visible)
    {
        Dispatcher.VerifyAccess();
        if (!occurrences.ContainsKey(occurrenceKey)) return;
        bool changed = visibility.SetManualVisibility(occurrenceKey, visible);
        if (!changed) return;
        foreach (var part in parts.Where(p => p.Occurrences.Any(x => x.Key == occurrenceKey)))
            ApplyInstances(part);
        UpdateVisibleStatus(); RefreshHiddenAnimation(); RenderView.InvalidateRender();
    }

    public bool IsOccurrenceVisible(string key) => visibility.IsVisible(key);
    public bool IsOccurrenceManuallyVisible(string key) => visibility.IsManuallyVisible(key);

    private void UpdateVisibleStatus()
    { if (snapshot is not null) StatusText.Text = $"{VisibleOccurrenceCount:N0} objects"; }

    private InstanceParameter InstanceAppearance(string key) => new()
    {
        DiffuseColor = key == selection ? new Color4(.95f, .64f, .22f, 1) : new Color4(.62f, .65f, .68f, 1),
        EmissiveColor = key == selection
            ? new Color4(.1f, .06f, 0, occurrences[key].SourceVisible ? 0 : 1)
            : new Color4(0, 0, 0, occurrences[key].SourceVisible ? 0 : 1),
        TexCoordOffset = Numerics.Vector2.Zero
    };

    private void ApplyInstances(Part part)
    {
        var indices = Enumerable.Range(0, part.Occurrences.Count).Where(i => visibility.IsVisible(part.Occurrences[i].Key)).ToArray();
        part.Model.Instances = indices.Select(i => ViewportGeometry.NativeMatrix(part.Occurrences[i].World)).ToArray();
        part.Model.InstanceIdentifiers = indices.Select(i => part.Identifiers[i]).ToArray();
        part.Model.InstanceParamArray = indices.Select(i => InstanceAppearance(part.Occurrences[i].Key)).ToArray();
        part.Model.Visibility = indices.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    public void FrameSelection()
    { if (selection is not null && occurrences.TryGetValue(selection, out var row)) Frame(row.WorldBounds); }
    public void FrameRegion()
    { if (snapshot is not null) Frame(snapshot.Region); }

    private void Frame(GeometryBounds bounds)
    {
        Dispatcher.VerifyAccess();
        if (!ViewportGeometry.Finite(bounds.Minimum) || !ViewportGeometry.Finite(bounds.Maximum))
            throw new InvalidDataException("Invalid camera bounds.");
        var size = bounds.Maximum - bounds.Minimum;
        double radius = Math.Max(size.Length() / 2, .01);
        if (RenderView.Camera is PerspectiveCamera camera)
        {
            camera.NearPlaneDistance = Math.Max(radius / 10000, .0001);
            camera.FarPlaneDistance = Math.Max(radius * 100, 100);
        }
        var center = (bounds.Minimum + bounds.Maximum) / 2;
        RenderView.ZoomExtents(new Point3D(center.X, center.Y, center.Z), radius, 0);
    }

    private void PointerDown(object sender, MouseButtonEventArgs e)
    { pointerStart = e.GetPosition(RenderView); RenderView.Focus(); }
    private void PointerUp(object sender, MouseButtonEventArgs e)
    {
        if ((e.GetPosition(RenderView) - pointerStart).Length > 4 || snapshot is null) return;
        string? key = null;
        foreach (var hit in RenderView.FindHits(e.GetPosition(RenderView)))
            if (TryResolveHit(hit, out key)) break;
        SetSelection(key);
        SelectionChanged?.Invoke(this, key);
    }

    public bool TryResolveHit(HitTestResult hit, out string? occurrenceKey)
    {
        Dispatcher.VerifyAccess();
        occurrenceKey = null;
        if (!hit.IsValid || snapshot is null) return false;
        var part = parts.FirstOrDefault(p => ReferenceEquals(p.Model, hit.ModelHit));
        if (part is null) return false;
        Guid? identity = hit.Tag switch
        {
            Guid id => id,
            int index when index >= 0 && index < (part.Model.InstanceIdentifiers?.Count ?? 0) => part.Model.InstanceIdentifiers![index],
            _ => null
        };
        if (identity is not { } guid || !instanceKeys.TryGetValue(guid, out var key) || !visibility.IsVisible(key)) return false;
        occurrenceKey = key;
        return true;
    }

    private void FrameClicked(object sender, RoutedEventArgs e) => FrameRegion();
    private void SelectionClicked(object sender, RoutedEventArgs e) => FrameSelection();
    private void FlyDisabled(object sender, RoutedEventArgs e) => StopFlight();
    private void FlyDown(object sender, MouseButtonEventArgs e)
    {
        if (FlyToggle.IsChecked != true) return;
        flyDragging = true;
        flyPointer = e.GetPosition(RenderView);
        RenderView.Focus(); RenderView.CaptureMouse(); e.Handled = true;
    }
    private void FlyUp(object sender, MouseButtonEventArgs e)
    { if (flyDragging) { flyDragging = false; RenderView.ReleaseMouseCapture(); e.Handled = true; } }
    private void FlyMove(object sender, MouseEventArgs e)
    {
        if (!flyDragging || RenderView.Camera is not PerspectiveCamera camera) return;
        var next = e.GetPosition(RenderView);
        var delta = next - flyPointer; flyPointer = next;
        var look = new Numerics.Vector3((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z);
        var yaw = Numerics.Matrix4x4.CreateFromAxisAngle(Numerics.Vector3.UnitY, (float)(-delta.X * .004));
        look = Numerics.Vector3.TransformNormal(look, yaw);
        var right = Numerics.Vector3.Cross(look, Numerics.Vector3.UnitY);
        right = right.LengthSquared() < 1e-8f ? Numerics.Vector3.UnitX : Numerics.Vector3.Normalize(right);
        var pitched = Numerics.Vector3.TransformNormal(look, Numerics.Matrix4x4.CreateFromAxisAngle(right, (float)(-delta.Y * .004)));
        if (Math.Abs(Numerics.Vector3.Dot(Numerics.Vector3.Normalize(pitched), Numerics.Vector3.UnitY)) < .995f) look = pitched;
        camera.LookDirection = new(look.X, look.Y, look.Z); camera.UpDirection = new(0, 1, 0);
        e.Handled = true;
    }
    private void KeyDownHandler(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F) { FrameSelection(); e.Handled = true; return; }
        if (FlyToggle.IsChecked != true || e.Key is not (Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E)) return;
        flyKeys.Add(e.Key); lastFlyTime = flyClock.Elapsed.TotalSeconds; flyTimer.Start(); e.Handled = true;
    }
    private void KeyUpHandler(object sender, KeyEventArgs e)
    { if (flyKeys.Remove(e.Key)) e.Handled = true; if (flyKeys.Count == 0) flyTimer.Stop(); }
    private void FlyTick(object? sender, EventArgs e)
    {
        if (FlyToggle.IsChecked != true || RenderView.Camera is not PerspectiveCamera camera) { StopFlight(); return; }
        double now = flyClock.Elapsed.TotalSeconds, elapsed = Math.Clamp(now - lastFlyTime, 0, .05); lastFlyTime = now;
        var forward = new Vector3D(camera.LookDirection.X, camera.LookDirection.Y, camera.LookDirection.Z); forward.Normalize();
        var right = Vector3D.CrossProduct(forward, new(0, 1, 0));
        if (right.LengthSquared < 1e-8) right = new(1, 0, 0); else right.Normalize();
        var direction = new Vector3D();
        if (flyKeys.Contains(Key.W)) direction += forward;
        if (flyKeys.Contains(Key.S)) direction -= forward;
        if (flyKeys.Contains(Key.D)) direction += right;
        if (flyKeys.Contains(Key.A)) direction -= right;
        if (flyKeys.Contains(Key.E)) direction += new Vector3D(0, 1, 0);
        if (flyKeys.Contains(Key.Q)) direction -= new Vector3D(0, 1, 0);
        if (direction.LengthSquared == 0) return;
        direction.Normalize();
        double speed = Math.Max(camera.LookDirection.Length * .12, .1);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) speed *= 4;
        camera.Position += direction * (speed * elapsed);
    }
    private void StopFlight()
    { flyKeys.Clear(); flyTimer.Stop(); flyDragging = false; if (RenderView.IsMouseCaptured) RenderView.ReleaseMouseCapture(); }

    private bool ShouldAnimateHidden() => !disposed && renderException is null && IsLoaded && IsVisible &&
        RenderView.IsLoaded && RenderView.IsVisible && snapshot is not null &&
        occurrences.Values.Any(row => !row.SourceVisible && visibility.IsVisible(row.Key));
    private void RefreshHiddenAnimation()
    { if (ShouldAnimateHidden()) hiddenTimer.Start(); else hiddenTimer.Stop(); }
    private void HiddenTick(object? sender, EventArgs args)
    { if (!ShouldAnimateHidden()) hiddenTimer.Stop(); else RenderView.InvalidateRender(); }

    private void ClearScene()
    {
        Dispatcher.VerifyAccess();
        StopFlight();
        hiddenTimer.Stop();
        foreach (var part in parts) { RenderView.Items.Remove(part.Model); part.Model.Dispose(); }
        parts.Clear(); instanceKeys.Clear(); occurrences.Clear(); visibility.Clear(); rendererIssues.Clear(); selection = null;
        for (int i = leases.Count - 1; i >= 0; i--) leases[i].Dispose();
        leases.Clear(); snapshot = null; StatusText.Text = "";
    }

    public async Task UnloadAsync()
    {
        Dispatcher.VerifyAccess();
        ++generation; loadCancellation?.Cancel();
        await loadGate.WaitAsync();
        try { ClearScene(); } finally { loadGate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        Dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true;
        await UnloadAsync();
        RenderView.EffectsManager = null;
        RenderView.Dispose(); service = null;
    }
    private sealed record Part(InstancingMeshGeometryModel3D Model, IReadOnlyList<RenderOccurrence> Occurrences,
        IReadOnlyList<Guid> Identifiers);
}
