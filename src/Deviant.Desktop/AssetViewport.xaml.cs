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
    private readonly HashSet<string> loadedGeometryKeys = new(StringComparer.Ordinal);
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
    private IDisposable? streamingSourceLease;
    private CancellationTokenSource? loadCancellation;
    private long generation;
    private bool cameraEdited;
    private bool disposed;
    private WpfPoint pointerStart;
    private WpfPoint flyPointer;
    private bool flyDragging;
    private double lastFlyTime;
    private string? selection;
    private readonly HashSet<RenderMeshSelection> meshSelections = [];
    private Exception? renderException;
    private Func<CancellationToken, Task>? frameWaitAsyncOverride = null;
    private MSAALevel? previousDepthMsaa;

    public AssetViewport()
    {
        InitializeComponent();
        PreviewMouseDown += (_, _) => cameraEdited = true;
        PreviewMouseWheel += (_, _) => cameraEdited = true;
        PreviewKeyDown += (_, _) => cameraEdited = true;
        foreach (var binding in RenderView.InputBindings.OfType<KeyBinding>().Where(binding =>
            binding.Key == Key.D && binding.Modifiers == ModifierKeys.None &&
            binding.Command == ViewportCommands.BottomView).ToArray())
            RenderView.InputBindings.Remove(binding);
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
    public event EventHandler<IReadOnlyList<RenderMeshSelection>>? MeshSelectionChanged;
    public IReadOnlyList<RenderMeshSelection> SelectedMeshes => meshSelections.ToArray();
    internal bool UseSelectionTint { get; set; } = true;
    public event EventHandler<RenderLoadProgress>? LoadProgressChanged;
    public event EventHandler<Exception>? RenderFailed;
    public Exception? LastRenderException => renderException;
    public string? SelectedOccurrenceKey => selection;
    public int RenderedOccurrenceCount => occurrences.Count;
    public int RenderedPartCount => parts.Sum(x => x.Occurrences.Count);
    public int VisibleOccurrenceCount => occurrences.Keys.Count(visibility.IsVisible);
    public int VisiblePartCount => parts.Sum(part => part.Occurrences.Count(row => visibility.IsVisible(row.Key)));
    public int VisibleMeshCount => parts.Where(part => part.Occurrences.Any(row => visibility.IsVisible(row.Key)))
        .Select(part => part.GeometryKey ?? part.PrimitiveKey).Distinct(StringComparer.Ordinal).Count();
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

    public Task LoadSnapshotAsync(RenderAssetSnapshot source, CancellationToken token = default)
        => LoadSnapshotCoreAsync(source, false, token);

    public Task AppendSnapshotAsync(RenderAssetSnapshot source, CancellationToken token = default)
        => LoadSnapshotCoreAsync(source, true, token);

    private async Task LoadSnapshotCoreAsync(RenderAssetSnapshot source, bool append, CancellationToken token)
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
        var pendingGeometry = new List<PendingGeometry>();
        try
        {
            await loadGate.WaitAsync(cancellation.Token);
            entered = true;
            bool firstBatch = parts.Count == 0;
            if (!append || firstBatch) cameraEdited = false;
            if (!append) ClearScene();
            renderException = null;
            CheckCurrent();
            if (append)
            {
                var held = source.AcquireLease(); streamingSourceLease?.Dispose(); streamingSourceLease = held;
            }
            else leases.Add(source.AcquireLease());
            snapshot = source;
            if (!append) await service.RestoreShaderBundleAsync(source, cancellation.Token);
            if (source.Occurrences.Select(x => x.Key).Distinct().Count() != source.Occurrences.Count)
                throw new InvalidDataException("Duplicate occurrence keys.");
            var changedInstances = new HashSet<string>(StringComparer.Ordinal);
            foreach (var occurrence in source.Occurrences)
            {
                CheckCurrent();
                if (!source.Meshes.ContainsKey(occurrence.GeometryKey))
                    throw new InvalidDataException($"The geometry for {occurrence.Label} is missing.");
                if (occurrences.TryAdd(occurrence.Key, occurrence))
                {
                    visibility.Add(occurrence.Key, occurrence.SourceVisible);
                    changedInstances.Add(occurrence.GeometryKey);
                }
                else
                {
                    var previous = occurrences[occurrence.Key];
                    if (previous.GeometryKey != occurrence.GeometryKey)
                        throw new InvalidDataException("The streamed occurrence changed its geometry.");
                    if (previous.World != occurrence.World || previous.SourceVisible != occurrence.SourceVisible)
                        changedInstances.Add(occurrence.GeometryKey);
                }
            }
            int total = source.Meshes.Values.Sum(m => m.Geometry.Primitives.Sum(p => Math.Max(1, p.Submeshes.Count)));
            int completed = 0;
            int queuedUploads = 0;
            long queuedUploadBytes = 0;
            int uploadsSinceFrame = 0;
            long bytesSinceFrame = 0;
            bool presentedInitialBatch = append && !firstBatch;
            var instanceBatch = new InstanceRetirementBatch(this);
            const int maximumQueuedUploads = 8;
            const long maximumQueuedUploadBytes = 16L * 1024 * 1024;
            async Task FlushUploadsAsync()
            {
                if (queuedUploads == 0) return;
                foreach (var job in pendingGeometry)
                {
                    var geometry = await job.Geometry;
                        leases.Add(geometry);
                        CheckCurrent();
                        // Matrices, GUIDs, row table, and pending instance-buffer copies.
                        leases.Add(source.MemoryBudget.Reserve(checked((long)job.Rows.Length * 384), "instance upload copies"));
                        leases.Add(service.ReserveGpu(checked((long)job.Rows.Length * 104), "instance buffers"));
                        var keys = job.Rows.Select(x => Guid.NewGuid()).ToArray();
                        for (int i = 0; i < job.Rows.Length; i++) instanceKeys.Add(keys[i], job.Rows[i].Key);
                        InstancingMeshGeometryModel3D model = new SurfaceInstancingModel(job.Material.TechniqueName);
                        model.Geometry = geometry.Geometry; model.Material = job.Material.Material;
                        model.IsTransparent = job.Material.IsTransparent;
                        model.Instances = job.Rows.Select(x => ViewportGeometry.NativeMatrix(x.World)).ToArray();
                        model.InstanceParamArray = job.Rows.Select(x => InstanceAppearance(x.Key)).ToArray();
                        model.InstanceIdentifiers = keys; model.OctreeManager = new InstancingModel3DOctreeManager();
                        model.CullMode = SharpDX.Direct3D11.CullMode.None; model.IsHitTestVisible = true;
                        var part = new Part(model, job.Rows, keys, job.Primitive.Key, job.Mesh.Key, job.Submesh, geometry);
                        ApplyInstances(part);
                        parts.Add(part);
                        RenderView.Items.Add(model);
                        Progress("Loading", ++completed, total);
                }
                pendingGeometry.Clear();
                uploadsSinceFrame += queuedUploads;
                bytesSinceFrame += queuedUploadBytes;
                if (!presentedInitialBatch || uploadsSinceFrame >= 64 || bytesSinceFrame >= maximumQueuedUploadBytes)
                {
                    await WaitForFrameAsync(cancellation.Token);
                    instanceBatch.ReleaseAfterFrame();
                    presentedInitialBatch = true; uploadsSinceFrame = 0; bytesSinceFrame = 0;
                }
                else await Dispatcher.Yield(DispatcherPriority.Background);
                CheckCurrent();
                queuedUploads = 0;
                queuedUploadBytes = 0;
            }
            var occurrencesByGeometry = source.Occurrences.GroupBy(row => row.GeometryKey)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            foreach (var mesh in source.Meshes.Values)
            {
                if (append && loadedGeometryKeys.Contains(mesh.Key))
                {
                    if (changedInstances.Contains(mesh.Key))
                        await ReconcileGeometryInstancesBatchedAsync(source, mesh.Key,
                            occurrencesByGeometry.GetValueOrDefault(mesh.Key) ?? [], cancellation.Token, instanceBatch);
                    CheckCurrent();
                    continue;
                }
                var rows = occurrencesByGeometry.GetValueOrDefault(mesh.Key) ?? [];
                if (rows.Length == 0) continue;
                foreach (var primitive in mesh.Geometry.Primitives)
                {
                    RenderMaterial? materialSource = null;
                    if (mesh.MaterialsByPrimitive?.TryGetValue(primitive.Key, out var identity) == true)
                        source.Materials.TryGetValue(identity, out materialSource);
                    var material = await service.AcquireMaterialAsync(source, materialSource, primitive, cancellation.Token);
                    leases.Add(material);
                    if (material.RequiresSceneDepth)
                    {
                        previousDepthMsaa ??= RenderView.MSAA;
                        if (RenderView.MSAA == MSAALevel.Disable) RenderView.MSAA = MSAALevel.Two;
                    }
                    if (materialSource is not null)
                        foreach (string diagnostic in material.Diagnostics)
                            rendererIssues.Add(new(materialSource.Asset.Identity,
                                material.IsUnavailable ? AssetViewportService.UnsupportedMaterialPassIssueCode : "MaterialRenderer", diagnostic));
                    var submeshes = primitive.Submeshes.Count > 0 ? primitive.Submeshes :
                        [new GeometrySubmesh(primitive.Key, 0, primitive.Indices.Count, [])];
                    foreach (var submesh in submeshes)
                    {
                        CheckCurrent();
                        bool colors = primitive.Colors?.Count == primitive.Positions.Count;
                        long uploadBytes = ViewportGeometry.GeometryBytes(primitive.Positions.Count, submesh.IndexCount, colors);
                        long admissionBytes = checked(ViewportGeometry.RetainedConversionBytes(primitive.Positions.Count,
                            submesh.IndexCount, colors) + (long)primitive.Positions.Count * HelixToolkit.SharpDX.DefaultVertex.SizeInBytes +
                            (long)rows.Length * 384);
                        if (queuedUploads > 0 && (queuedUploads >= maximumQueuedUploads ||
                            uploadBytes > maximumQueuedUploadBytes - queuedUploadBytes ||
                            admissionBytes > source.MemoryBudget.MaximumBytes - source.MemoryBudget.UsedBytes))
                            await FlushUploadsAsync();
                        pendingGeometry.Add(new(service.AcquireGeometryAsync(source, mesh, primitive, submesh,
                            material.UvSet, material.NormalMapped, cancellation.Token), mesh, primitive, submesh, rows, material));
                        queuedUploads++;
                        queuedUploadBytes = checked(queuedUploadBytes + uploadBytes);
                        // Bound pending uploads by count, bytes and the existing CPU admission limit.
                        if (queuedUploads >= maximumQueuedUploads || queuedUploadBytes >= maximumQueuedUploadBytes)
                            await FlushUploadsAsync();
                    }
                }
                loadedGeometryKeys.Add(mesh.Key);
            }
            await FlushUploadsAsync();
            await instanceBatch.FlushAsync(cancellation.Token);
            CheckCurrent();
            if (!append || firstBatch || !cameraEdited)
            {
                FrameRegion();
                if (!append || firstBatch)
                    await WaitForFrameAsync(cancellation.Token);
                else
                    RenderView.InvalidateRender();
                CheckCurrent();
            }
            UpdateVisibleStatus();
            RefreshHiddenAnimation();
            Progress("Ready", completed, total);
            if (!append) await service.PrepareShaderBundleAsync(source, cancellation.Token);
        }
        catch (Exception error)
        {
            cancellation.Cancel();
            foreach (var pending in pendingGeometry)
                try { (await pending.Geometry).Dispose(); } catch { }
            pendingGeometry.Clear();
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
        using var timing = AssetLoadDiagnostics.Measure("UploadFrameWait");
        if (frameWaitAsyncOverride is { } waitOverride)
        {
            await waitOverride(token);
            return;
        }
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
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var limit = TimeSpan.FromSeconds(5);
            while (!completion.Task.IsCompleted)
            {
                var remaining = limit - System.Diagnostics.Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("The viewport did not render a frame.");
                try
                {
                    await completion.Task.WaitAsync(remaining < TimeSpan.FromMilliseconds(100)
                        ? remaining : TimeSpan.FromMilliseconds(100), token);
                }
                catch (TimeoutException) when (System.Diagnostics.Stopwatch.GetElapsedTime(started) < limit)
                {
                    // A request made while the host finishes its previous frame can be consumed by that frame.
                    // Keep requesting presentation until an actual completed-frame event arrives.
                    RenderView.InvalidateRender();
                }
            }
            await completion.Task;
        }
        finally { RenderView.OnRendered -= Rendered; }
    }

    private void Progress(string stage, int completed, int total)
    {
        StatusText.Text = stage == "Ready" ? $"{VisibleOccurrenceCount:N0} objects" : $"{completed:N0} / {total:N0}";
        LoadProgressChanged?.Invoke(this, new(stage, completed, total, snapshot?.MemoryBudget.UsedBytes ?? 0));
    }

    public void SetSelection(string? occurrenceKey)
        => SetSelections(occurrenceKey is null ? [] : [new(occurrenceKey)]);

    public void SetSelections(IEnumerable<RenderMeshSelection> selected)
    {
        Dispatcher.VerifyAccess();
        meshSelections.Clear();
        foreach (var item in selected)
            if (occurrences.TryGetValue(item.OccurrenceKey, out var occurrence) && (item.PrimitiveKey is null ||
                snapshot!.Meshes[occurrence.GeometryKey].Geometry.Primitives.Any(p => p.Key == item.PrimitiveKey)))
                meshSelections.Add(item);
        selection = meshSelections.LastOrDefault()?.OccurrenceKey;
        foreach (var part in parts)
            part.Model.InstanceParamArray = part.Model.InstanceIdentifiers!.Select(id => InstanceAppearance(instanceKeys[id], part.PrimitiveKey)).ToArray();
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

    private bool IsSelected(string key, string? primitiveKey) => meshSelections.Contains(new(key)) ||
        primitiveKey is not null && meshSelections.Contains(new(key, primitiveKey));
    private InstanceParameter InstanceAppearance(string key, string? primitiveKey = null) => new()
    {
        DiffuseColor = UseSelectionTint && IsSelected(key, primitiveKey) ? new Color4(.95f, .64f, .22f, 1) : new Color4(.62f, .65f, .68f, 1),
        EmissiveColor = UseSelectionTint && IsSelected(key, primitiveKey)
            ? new Color4(.1f, .06f, 0, occurrences[key].SourceVisible ? 0 : 1)
            : new Color4(0, 0, 0, occurrences[key].SourceVisible ? 0 : 1),
        TexCoordOffset = Numerics.Vector2.Zero
    };

    private void ApplyInstances(Part part)
    {
        var indices = Enumerable.Range(0, part.Occurrences.Count).Where(i => visibility.IsVisible(part.Occurrences[i].Key)).ToArray();
        part.Model.Instances = indices.Select(i => ViewportGeometry.NativeMatrix(part.Occurrences[i].World)).ToArray();
        part.Model.InstanceIdentifiers = indices.Select(i => part.Identifiers[i]).ToArray();
        part.Model.InstanceParamArray = indices.Select(i => InstanceAppearance(part.Occurrences[i].Key, part.PrimitiveKey)).ToArray();
        part.Model.Visibility = indices.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    public void FrameSelection()
    { if (selection is not null && occurrences.TryGetValue(selection, out var row)) Frame(row.WorldBounds); }
    public void FrameRegion()
    { if (snapshot is not null) Frame(snapshot.Region); }

    private void Frame(GeometryBounds bounds)
    {
        Dispatcher.VerifyAccess();
        using var timing = AssetLoadDiagnostics.Measure("CameraFrame", $"{bounds.Minimum}|{bounds.Maximum}");
        if (!ViewportGeometry.Finite(bounds.Minimum) || !ViewportGeometry.Finite(bounds.Maximum))
            throw new InvalidDataException("Invalid camera bounds.");
        double x = (double)bounds.Maximum.X - bounds.Minimum.X;
        double y = (double)bounds.Maximum.Y - bounds.Minimum.Y;
        double z = (double)bounds.Maximum.Z - bounds.Minimum.Z;
        double radius = Math.Max(Math.Sqrt(x * x + y * y + z * z) / 2, .01);
        if (RenderView.Camera is PerspectiveCamera camera)
        {
            camera.NearPlaneDistance = ViewportGeometry.CameraNearPlane(radius);
            camera.FarPlaneDistance = Math.Max(radius * 100, 100);
        }
        var center = new Point3D(((double)bounds.Minimum.X + bounds.Maximum.X) / 2,
            ((double)bounds.Minimum.Y + bounds.Maximum.Y) / 2, ((double)bounds.Minimum.Z + bounds.Maximum.Z) / 2);
        RenderView.ZoomExtents(center, radius, 0);
    }

    private void PointerDown(object sender, MouseButtonEventArgs e)
    { pointerStart = e.GetPosition(RenderView); RenderView.Focus(); }
    private void PointerUp(object sender, MouseButtonEventArgs e)
    {
        if ((e.GetPosition(RenderView) - pointerStart).Length > 4 || snapshot is null) return;
        string? key = null;
        string? primitiveKey = null;
        foreach (var hit in RenderView.FindHits(e.GetPosition(RenderView)))
            if (TryResolveHit(hit, out key))
            { primitiveKey = parts.First(p => ReferenceEquals(p.Model, hit.ModelHit)).PrimitiveKey; break; }
        var next = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? meshSelections.ToHashSet() : [];
        if (key is not null)
        {
            var item = new RenderMeshSelection(key, primitiveKey);
            if (!next.Remove(item)) next.Add(item);
        }
        SetSelections(next);
        SelectionChanged?.Invoke(this, key);
        MeshSelectionChanged?.Invoke(this, SelectedMeshes);
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
    private void FlyEnabled(object sender, RoutedEventArgs e)
    {
        if (RenderView.Camera is PerspectiveCamera camera)
        {
            var look = RotateFlightLook(new((float)camera.LookDirection.X,
                (float)camera.LookDirection.Y, (float)camera.LookDirection.Z), 0, 0);
            camera.LookDirection = new(look.X, look.Y, look.Z);
            camera.UpDirection = new(0, 1, 0);
        }
        RenderView.Focus();
    }
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
        look = RotateFlightLook(look, delta.X, delta.Y);
        camera.LookDirection = new(look.X, look.Y, look.Z); camera.UpDirection = new(0, 1, 0);
        e.Handled = true;
    }
    private static Numerics.Vector3 RotateFlightLook(Numerics.Vector3 look, double deltaX, double deltaY)
    {
        var length = look.Length();
        if (!float.IsFinite(length) || length < 1e-6f) { look = -Numerics.Vector3.UnitZ; length = 1; }
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY)) return look;
        var horizontal = new Numerics.Vector3(look.X, 0, look.Z);
        horizontal = horizontal.LengthSquared() < 1e-8f ? -Numerics.Vector3.UnitZ : Numerics.Vector3.Normalize(horizontal);
        horizontal = Numerics.Vector3.TransformNormal(horizontal,
            Numerics.Matrix4x4.CreateFromAxisAngle(Numerics.Vector3.UnitY, (float)(-deltaX * .004)));
        var maximumPitch = Math.Asin(.995);
        var pitch = Math.Clamp(Math.Asin(Math.Clamp(look.Y / length, -1, 1)) - deltaY * .004,
            -maximumPitch, maximumPitch);
        return (horizontal * (float)Math.Cos(pitch) + Numerics.Vector3.UnitY * (float)Math.Sin(pitch)) * length;
    }
    private void KeyDownHandler(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F) { FrameSelection(); e.Handled = true; return; }
        if (e.Key is not (Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E)) return;
        if (FlyToggle.IsChecked != true) return;
        e.Handled = true;
        if (flyKeys.Count == 0) lastFlyTime = flyClock.Elapsed.TotalSeconds;
        flyKeys.Add(e.Key); flyTimer.Start();
    }
    private void KeyUpHandler(object sender, KeyEventArgs e)
    {
        if (flyKeys.Remove(e.Key) || FlyToggle.IsChecked == true &&
            e.Key is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E) e.Handled = true;
        if (flyKeys.Count == 0) flyTimer.Stop();
    }
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
        ClearPoseGeometry();
        hiddenTimer.Stop();
        foreach (var part in parts) { RenderView.Items.Remove(part.Model); part.Model.Dispose(); }
        parts.Clear(); loadedGeometryKeys.Clear(); instanceKeys.Clear(); occurrences.Clear(); visibility.Clear(); rendererIssues.Clear(); selection = null; meshSelections.Clear();
        for (int i = leases.Count - 1; i >= 0; i--) leases[i].Dispose();
        leases.Clear(); snapshot = null; StatusText.Text = "";
        streamingSourceLease?.Dispose(); streamingSourceLease = null;
        if (previousDepthMsaa is { } msaa) { RenderView.MSAA = msaa; previousDepthMsaa = null; }
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
        IReadOnlyList<Guid> Identifiers, string PrimitiveKey, string? GeometryKey = null, GeometrySubmesh? Submesh = null,
        AssetViewportService.GeometryLease? GeometryLease = null);
    private sealed record PendingGeometry(Task<AssetViewportService.GeometryLease> Geometry, RenderMesh Mesh,
        MeshPrimitive Primitive, GeometrySubmesh Submesh, RenderOccurrence[] Rows, AssetViewportService.MaterialLease Material);

}
