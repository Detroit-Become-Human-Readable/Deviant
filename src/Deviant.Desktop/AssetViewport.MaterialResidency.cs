using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using HelixToolkit.Maths;
using HelixToolkit.Wpf.SharpDX;

namespace Deviant.Desktop;

public partial class AssetViewport
{
    private DispatcherTimer? materialResidencyTimer;
    private CancellationTokenSource? materialResidencyCancellation;
    private RenderAssetSnapshot? materialResidencySource;
    private MaterialBinding[] materialBindings = [];
    private readonly HashSet<int> residentMaterialParts = [];
    private readonly List<IDisposable> residencyRetirements = [];
    private FrustumFrame? materialFrustum;
    private int materialResidencyDirty;
    private long materialPriorityGeneration;
    private long materialCursorGeneration = -1;
    private int materialResidencyCursor;
    private int materialFramesPending;
    private bool materialResidencyRunning, materialResidencyPaused;
    private int visibleMaterialParts, residentVisibleMaterialParts, budgetDeferredMaterialParts;

    public bool IsStreamingMaterials => materialResidencySource is not null && !materialResidencyPaused &&
        (materialResidencyRunning || Volatile.Read(ref materialResidencyDirty) != 0);
    public bool MaterialStreamingPaused => materialResidencySource is not null && materialResidencyPaused;
    public int VisibleMaterialPartCount => visibleMaterialParts;
    public int ResidentVisibleMaterialPartCount => residentVisibleMaterialParts;
    public int BudgetDeferredMaterialPartCount => budgetDeferredMaterialParts;

    public void PauseMaterialStreaming()
    {
        Dispatcher.VerifyAccess();
        if (materialResidencySource is null) return;
        materialResidencyPaused = true;
        materialResidencyTimer?.Stop(); materialResidencyCancellation?.Cancel();
        Volatile.Write(ref materialResidencyDirty, 0);
        MaterialResidencyChanged?.Invoke(this, EventArgs.Empty);
    }

    public int DeferredMaterialPartCount => materialBindings.Count(binding => binding.Material is not null &&
        !residentMaterialParts.Contains(binding.Index));
    public int ResidentMaterialPartCount => residentMaterialParts.Count;
    public event EventHandler? MaterialResidencyChanged;

    private void InitializeMaterialResidency()
    {
        materialResidencyTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background,
            async (_, _) => await RefreshMaterialResidencyAsync(), Dispatcher);
        materialResidencyTimer.Stop();
        RenderView.CameraChanged += (_, _) => MarkMaterialResidencyDirty();
        RenderView.SizeChanged += (_, _) => MarkMaterialResidencyDirty();
        RenderView.OnRendered += (_, _) =>
        {
            if (Volatile.Read(ref materialResidencyDirty) != 0 && RenderView.RenderContext is { } context)
            {
                long requested = Volatile.Read(ref materialPriorityGeneration);
                if (Volatile.Read(ref materialFramesPending) > 0) Interlocked.Decrement(ref materialFramesPending);
                Volatile.Write(ref materialFrustum, new(context.BoundingFrustum, requested, context.ViewMatrixInv.Translation));
            }
        };
        Loaded += (_, _) => { if (materialResidencySource is not null) { MarkMaterialResidencyDirty(); materialResidencyTimer.Start(); } };
        Unloaded += (_, _) => { materialResidencyTimer.Stop(); materialResidencyCancellation?.Cancel(); };
    }

    private void MarkMaterialResidencyDirty()
    {
        if (materialResidencySource is null || materialResidencyPaused) return;
        Interlocked.Increment(ref materialPriorityGeneration);
        Interlocked.Exchange(ref materialFramesPending, 2);
        Volatile.Write(ref materialResidencyDirty, 1); RenderView.InvalidateRender();
    }

    private void StopMaterialResidency()
    {
        materialResidencyTimer?.Stop(); materialResidencyCancellation?.Cancel();
        foreach (var binding in materialBindings)
            if (binding.Placeholder is { } placeholder) residencyRetirements.Add(placeholder);
        materialResidencySource = null; materialBindings = []; residentMaterialParts.Clear();
        Volatile.Write(ref materialResidencyDirty, 0); Volatile.Write(ref materialFrustum, null);
        materialCursorGeneration = -1; materialResidencyCursor = 0;
        Interlocked.Exchange(ref materialFramesPending, 0);
        materialResidencyPaused = false;
        visibleMaterialParts = residentVisibleMaterialParts = budgetDeferredMaterialParts = 0;
        MaterialResidencyChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task InitializeSceneMaterialsAsync(RenderAssetSnapshot source, long current, CancellationToken token)
    {
        StopMaterialResidency();
        await FlushMaterialRetirementsAsync(token);
        materialResidencySource = source;
        leases.Add(source.MemoryBudget.Reserve(checked(parts.Count * 128L + 4096), "material residency"));
        materialBindings = parts.Select((part, index) =>
        {
            if (part.GeometryKey is null || part.Submesh is null || !source.Meshes.TryGetValue(part.GeometryKey, out var mesh))
                throw new InvalidOperationException("The streamed mesh is unavailable.");
            var primitive = mesh.Geometry.Primitives.Single(value => value.Key == part.PrimitiveKey);
            RenderMaterial? material = null;
            if (mesh.MaterialsByPrimitive?.TryGetValue(primitive.Key, out var identity) == true)
                source.Materials.TryGetValue(identity, out material);
            return new MaterialBinding(index, mesh, primitive, material);
        }).ToArray();
        foreach (var binding in materialBindings)
        {
            CheckMaterialGeneration(current, token);
            if (binding.Material is null) { residentMaterialParts.Add(binding.Index); continue; }
            var material = await service!.AcquireMaterialAsync(source, binding.Material, binding.Primitive, token, deferred: true);
            binding.Placeholder = material.Retain(); leases.Add(binding.Placeholder);
            await BindPartMaterialAsync(source, binding, material, prepareGeometry: false, current, token);
            if ((binding.Index + 1) % 32 == 0 || binding.Index == materialBindings.Length - 1)
                LoadProgressChanged?.Invoke(this, new("Preparing material bindings", binding.Index + 1,
                    materialBindings.Length, source.MemoryBudget.UsedBytes));
            if ((binding.Index + 1) % 64 == 0) await FlushMaterialRetirementsAsync(token);
            else if ((binding.Index + 1) % 8 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
        }
        MarkMaterialResidencyDirty();
        await FlushMaterialRetirementsAsync(token);
        await WaitForFrameAsync(token);
        await UpdateMaterialResidencyAsync(source, current, token);
        materialResidencyTimer!.Start();
    }

    private async Task RefreshMaterialResidencyAsync()
    {
        if (disposed || !IsLoaded || !IsVisible || materialResidencyPaused || materialResidencySource is not { } source ||
            Volatile.Read(ref materialResidencyDirty) == 0 || !await loadGate.WaitAsync(0)) return;
        var current = generation;
        using var own = new CancellationTokenSource(); materialResidencyCancellation = own;
        try { await UpdateMaterialResidencyAsync(source, current, own.Token); UpdateVisibleStatus(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            materialResidencyTimer?.Stop();
            Volatile.Write(ref materialResidencyDirty, 0);
            WorkflowDiagnostics.Failure("Viewport.MaterialResidency", error, source.Revision.IndexContentId);
            StatusText.Text = error.Message; RenderFailed?.Invoke(this, error);
        }
        finally
        {
            try
            {
                if (materialResidencyPaused && residencyRetirements.Count > 0)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    try { await FlushMaterialRetirementsAsync(cleanup.Token); }
                    catch (OperationCanceledException) when (cleanup.IsCancellationRequested) { }
                    catch (Exception error) { WorkflowDiagnostics.Failure("Viewport.MaterialRetirement", error, source.Revision.IndexContentId); }
                }
            }
            finally
            {
                if (ReferenceEquals(materialResidencyCancellation, own)) materialResidencyCancellation = null;
                loadGate.Release();
            }
        }
    }

    private async Task UpdateMaterialResidencyAsync(RenderAssetSnapshot source, long current, CancellationToken token)
    {
        materialResidencyRunning = true;
        MaterialResidencyChanged?.Invoke(this, EventArgs.Empty);
        MaterialPartRank[]? ordered = null;
        try
        {
            using var scratch = source.MemoryBudget.Reserve(checked(parts.Count * 64L + 4096), "material residency sorting");
            await FlushMaterialRetirementsAsync(token);
            var frame = Volatile.Read(ref materialFrustum);
            for (int attempt = 0; attempt < 2 && frame is null; attempt++)
            { await WaitForFrameAsync(token); frame = Volatile.Read(ref materialFrustum); }
            if (frame is null)
            { Volatile.Write(ref materialResidencyDirty, 1); return; }
            Interlocked.Exchange(ref materialResidencyDirty, 0);
            long priorityGeneration = frame.Generation;
            if (materialCursorGeneration != priorityGeneration)
            { materialCursorGeneration = priorityGeneration; materialResidencyCursor = 0; }
            ordered = materialBindings.Select(binding => RankMaterialPart(binding, frame, frame.Position)).OrderByDescending(row => row.Selected)
                .ThenByDescending(row => row.InView).ThenBy(row => row.Distance).ThenBy(row => row.Index).ToArray();
            visibleMaterialParts = ordered.Count(row => (row.InView || row.Selected) && materialBindings[row.Index].Material is not null);
            residentVisibleMaterialParts = ordered.Count(row => (row.InView || row.Selected) &&
                materialBindings[row.Index].Material is not null && residentMaterialParts.Contains(row.Index));
            budgetDeferredMaterialParts = 0;
            var rank = ordered.Select((row, index) => (row.Index, Rank: index)).ToDictionary(row => row.Index, row => row.Rank);
            int promoted = 0;
            var batchClock = Stopwatch.StartNew();
            while (materialResidencyCursor < ordered.Length)
            {
                CheckMaterialGeneration(current, token);
                if (promoted >= 8 || batchClock.ElapsedMilliseconds >= 75)
                { Volatile.Write(ref materialResidencyDirty, 1); break; }
                var row = ordered[materialResidencyCursor++];
                if (!row.InView && !row.Selected) continue;
                if (residentMaterialParts.Contains(row.Index)) continue;
                var binding = materialBindings[row.Index];
                while (true)
                {
                    try
                    {
                        var material = await service!.AcquireMaterialAsync(source, binding.Material, binding.Primitive, token);
                        await BindPartMaterialAsync(source, binding, material, prepareGeometry: true, current, token);
                        residentMaterialParts.Add(row.Index);
                        promoted++;
                        foreach (var diagnostic in material.Diagnostics)
                        {
                            var issue = new RenderIssue(binding.Material!.Asset.Identity,
                                material.IsUnavailable ? AssetViewportService.UnsupportedMaterialPassIssueCode : "MaterialRenderer", diagnostic);
                            if (!rendererIssues.Contains(issue)) rendererIssues.Add(issue);
                        }
                        break;
                    }
                    catch (ViewportGpuBudgetException)
                    {
                        if (residencyRetirements.Count > 0)
                        { await FlushMaterialRetirementsAsync(token); continue; }
                        var victim = ordered.LastOrDefault(candidate => rank[candidate.Index] > rank[row.Index] &&
                            residentMaterialParts.Contains(candidate.Index) && materialBindings[candidate.Index].Material is not null);
                        if (victim is null) { budgetDeferredMaterialParts++; break; }
                        var victimBinding = materialBindings[victim.Index];
                        var placeholder = victimBinding.Placeholder!.Retain();
                        await BindPartMaterialAsync(source, victimBinding, placeholder, prepareGeometry: false, current, token);
                        residentMaterialParts.Remove(victim.Index);
                        await FlushMaterialRetirementsAsync(token);
                    }
                }
                if (residencyRetirements.Count >= 32) await FlushMaterialRetirementsAsync(token);
                else await Dispatcher.Yield(DispatcherPriority.Background);
            }
            await FlushMaterialRetirementsAsync(token);
            CheckMaterialGeneration(current, token);
            if (priorityGeneration != Volatile.Read(ref materialPriorityGeneration) || Volatile.Read(ref materialFramesPending) > 0)
                Volatile.Write(ref materialResidencyDirty, 1);
            if (Volatile.Read(ref materialResidencyDirty) != 0) RenderView.InvalidateRender();
            residentVisibleMaterialParts = ordered.Count(row => (row.InView || row.Selected) &&
                materialBindings[row.Index].Material is not null && residentMaterialParts.Contains(row.Index));
            LoadProgressChanged?.Invoke(this, new("Streaming materials", residentVisibleMaterialParts,
                visibleMaterialParts, source.MemoryBudget.UsedBytes));
            AssetLoadDiagnostics.Counters("MaterialResidency", source.Revision.IndexContentId,
                new Dictionary<string, long> { ["ResidentParts"] = ResidentMaterialPartCount,
                    ["DeferredParts"] = DeferredMaterialPartCount, ["GpuBytes"] = service!.GpuBytes });
        }
        finally
        {
            materialResidencyRunning = false;
            if (materialResidencySource is not null && ordered is not null && materialResidencyCursor >= ordered.Length &&
                Volatile.Read(ref materialResidencyDirty) == 0 && !materialResidencyPaused)
                budgetDeferredMaterialParts = Math.Max(0, visibleMaterialParts - residentVisibleMaterialParts);
            MaterialResidencyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private MaterialPartRank RankMaterialPart(MaterialBinding binding, FrustumFrame frame, System.Numerics.Vector3 position)
    {
        bool selected = false, inView = false; float distance = float.PositiveInfinity;
        foreach (var occurrence in parts[binding.Index].Occurrences)
        {
            if (!visibility.IsVisible(occurrence.Key)) continue;
            selected |= IsSelected(occurrence.Key, binding.Primitive.Key);
            var bounds = occurrence.WorldBounds;
            var closest = System.Numerics.Vector3.Clamp(position, bounds.Minimum, bounds.Maximum);
            float nextDistance = System.Numerics.Vector3.DistanceSquared(position, closest);
            if (!float.IsFinite(nextDistance)) { inView = true; continue; }
            distance = Math.Min(distance, nextDistance);
            inView |= frame.Frustum.Contains(new BoundingBox(bounds.Minimum, bounds.Maximum)) != ContainmentType.Disjoint;
        }
        return new(binding.Index, selected, inView, distance);
    }

    private async Task BindPartMaterialAsync(RenderAssetSnapshot source, MaterialBinding binding,
        AssetViewportService.MaterialLease material, bool prepareGeometry, long current, CancellationToken token)
    {
        AssetViewportService.GeometryLease? geometry = null;
        try
        {
            CheckMaterialGeneration(current, token);
            var old = parts[binding.Index];
            if (prepareGeometry) geometry = await service!.AcquireGeometryAsync(source, binding.Mesh, binding.Primitive,
                old.Submesh!, material.UvSet, material.NormalMapped, token);
            CheckMaterialGeneration(current, token);
            if (material.RequiresSceneDepth)
            {
                previousDepthMsaa ??= RenderView.MSAA;
                if (RenderView.MSAA == HelixToolkit.SharpDX.MSAALevel.Disable) RenderView.MSAA = HelixToolkit.SharpDX.MSAALevel.Two;
            }
            var model = new SurfaceInstancingModel(material.TechniqueName)
            {
                Geometry = geometry?.Geometry ?? old.Model.Geometry, Material = material.Material, IsTransparent = material.IsTransparent,
                Instances = old.Model.Instances, InstanceParamArray = old.Model.InstanceParamArray, InstanceIdentifiers = old.Model.InstanceIdentifiers,
                OctreeManager = new InstancingModel3DOctreeManager(), CullMode = old.Model.CullMode, IsHitTestVisible = true
            };
            RenderView.Items.Remove(old.Model); old.Model.Dispose();
            if (streamedInstanceAllocations.Remove(old.Model, out var allocation))
            { allocation.Model = model; streamedInstanceAllocations.Add(model, allocation); }
            leases.Add(material);
            if (geometry is not null) leases.Add(geometry);
            if (old.MaterialLease is not null) residencyRetirements.Add(old.MaterialLease);
            if (geometry is not null && old.GeometryLease is not null) residencyRetirements.Add(old.GeometryLease);
            var updated = old with { Model = model, MaterialLease = material, GeometryLease = geometry ?? old.GeometryLease };
            parts[binding.Index] = updated; ApplyInstances(updated); RenderView.Items.Add(model);
        }
        catch { material.Dispose(); geometry?.Dispose(); throw; }
    }

    private async Task FlushMaterialRetirementsAsync(CancellationToken token)
    {
        if (residencyRetirements.Count == 0) return;
        await WaitForFrameAsync(token);
        foreach (var retired in residencyRetirements) { retired.Dispose(); leases.Remove(retired); }
        residencyRetirements.Clear();
    }

    private void CheckMaterialGeneration(long current, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (disposed || materialResidencyPaused || current != generation) throw new OperationCanceledException(token);
    }

    private sealed record MaterialBinding(int Index, RenderMesh Mesh, MeshPrimitive Primitive, RenderMaterial? Material)
    { public AssetViewportService.MaterialLease? Placeholder { get; set; } }
    private sealed record MaterialPartRank(int Index, bool Selected, bool InView, float Distance);
    private sealed record FrustumFrame(BoundingFrustum Frustum, long Generation, System.Numerics.Vector3 Position);
}
