using System.IO;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using HelixToolkit.Wpf.SharpDX;

namespace Deviant.Desktop;

public partial class AssetViewport
{
    private readonly Dictionary<InstancingMeshGeometryModel3D, StreamedInstanceAllocation> streamedInstanceAllocations = [];

    public async Task CompleteSnapshotAsync(RenderAssetSnapshot source, CancellationToken token = default)
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        token.ThrowIfCancellationRequested();
        if (parts.Count == 0) { await LoadSnapshotAsync(source, token); return; }
        if (service is null) throw new InvalidOperationException("The viewport service is unavailable.");
        // Final snapshots can include a mesh that has not appeared in a streamed packet.
        if (source.Meshes.Keys.Any(key => !loadedGeometryKeys.Contains(key))) await AppendSnapshotAsync(source, token);
        long current = ++generation;
        loadCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        loadCancellation = cancellation;
        token = cancellation.Token;
        bool entered = false;
        try
        {
            await loadGate.WaitAsync(token); entered = true;
            leases.Add(source.AcquireLease());
            streamingSourceLease?.Dispose(); streamingSourceLease = null;
            snapshot = source;
            await service.RestoreShaderBundleAsync(source, token);
            if (source.Occurrences.Select(row => row.Key).Distinct(StringComparer.Ordinal).Count() != source.Occurrences.Count)
                throw new InvalidDataException("Duplicate occurrence keys.");
            var instanceBatch = new InstanceRetirementBatch(this);
            foreach (var group in source.Occurrences.GroupBy(row => row.GeometryKey))
                await ReconcileGeometryInstancesBatchedAsync(source, group.Key, group.ToArray(), token, instanceBatch);
            await instanceBatch.FlushAsync(token);
            for (int index = 0; index < parts.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                if (current != generation || disposed) throw new OperationCanceledException(token);
                var part = parts[index];
                if (part.GeometryKey is null || part.Submesh is null || !source.Meshes.TryGetValue(part.GeometryKey, out var mesh))
                    throw new InvalidDataException("The streamed mesh is unavailable.");
                var primitive = mesh.Geometry.Primitives.Single(value => value.Key == part.PrimitiveKey);
                RenderMaterial? materialSource = null;
                if (mesh.MaterialsByPrimitive?.TryGetValue(primitive.Key, out var identity) == true)
                    source.Materials.TryGetValue(identity, out materialSource);
                var material = await service.AcquireMaterialAsync(source, materialSource, primitive, token);
                leases.Add(material);
                var geometry = await service.AcquireGeometryAsync(source, mesh, primitive, part.Submesh,
                    material.UvSet, material.NormalMapped, token);
                leases.Add(geometry);
                if (material.RequiresSceneDepth)
                {
                    previousDepthMsaa ??= RenderView.MSAA;
                    if (RenderView.MSAA == HelixToolkit.SharpDX.MSAALevel.Disable) RenderView.MSAA = HelixToolkit.SharpDX.MSAALevel.Two;
                }
                var model = new SurfaceInstancingModel(material.TechniqueName)
                {
                    Geometry = geometry.Geometry, Material = material.Material, IsTransparent = material.IsTransparent,
                    Instances = part.Model.Instances, InstanceParamArray = part.Model.InstanceParamArray,
                    InstanceIdentifiers = part.Model.InstanceIdentifiers,
                    OctreeManager = new InstancingModel3DOctreeManager(),
                    CullMode = part.Model.CullMode, IsHitTestVisible = true
                };
                RenderView.Items.Remove(part.Model); part.Model.Dispose();
                if (streamedInstanceAllocations.Remove(part.Model, out var allocation))
                { allocation.Model = model; streamedInstanceAllocations.Add(model, allocation); }
                part.GeometryLease?.Dispose();
                if (part.GeometryLease is not null) leases.Remove(part.GeometryLease);
                var updated = part with { Model = model, GeometryLease = geometry }; parts[index] = updated;
                ApplyInstances(updated); RenderView.Items.Add(model);
                if (materialSource is not null)
                    foreach (var diagnostic in material.Diagnostics)
                        rendererIssues.Add(new(materialSource.Asset.Identity,
                            material.IsUnavailable ? AssetViewportService.UnsupportedMaterialPassIssueCode : "MaterialRenderer", diagnostic));
                LoadProgressChanged?.Invoke(this, new("Materials", index + 1, parts.Count, source.MemoryBudget.UsedBytes));
                if ((index + 1) % 64 == 0) await WaitForFrameAsync(token);
                else if ((index + 1) % 8 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
            }
            await WaitForFrameAsync(token);
            UpdateVisibleStatus(); RefreshHiddenAnimation();
            await service.PrepareShaderBundleAsync(source, token);
        }
        finally
        {
            if (entered) loadGate.Release();
            if (ReferenceEquals(loadCancellation, cancellation)) loadCancellation = null;
        }
    }

    private Task ReconcileGeometryInstancesAsync(RenderAssetSnapshot source, string geometryKey,
        IReadOnlyList<RenderOccurrence> incoming, CancellationToken token) =>
        ReconcileGeometryInstancesBatchedAsync(source, geometryKey, incoming, token, null);

    private async Task ReconcileGeometryInstancesBatchedAsync(RenderAssetSnapshot source, string geometryKey,
        IReadOnlyList<RenderOccurrence> incoming, CancellationToken token, InstanceRetirementBatch? batch)
    {
        Dispatcher.VerifyAccess(); token.ThrowIfCancellationRequested();
        var owner = service ?? throw new InvalidOperationException("The viewport service is unavailable.");
        if (incoming.Count == 0) return;
        if (!source.Meshes.ContainsKey(geometryKey)) throw new InvalidDataException("The streamed geometry is unavailable.");
        var matching = parts.Select((part, index) => (Part: part, Index: index)).Where(row => row.Part.GeometryKey == geometryKey).ToArray();
        long scratchBytes = checked(4096L + incoming.Count * 128L + matching.Sum(row => row.Part.Occurrences.Count * 128L));
        if (batch is not null) await batch.BeforeAdmissionAsync(source.MemoryBudget, owner, scratchBytes, 0, token);
        using var scratch = source.MemoryBudget.Reserve(scratchBytes, "streamed instance reconciliation");
        var byKey = incoming.ToDictionary(row => row.Key, StringComparer.Ordinal);
        foreach (var row in incoming)
        {
            token.ThrowIfCancellationRequested();
            if (row.GeometryKey != geometryKey || occurrences.TryGetValue(row.Key, out var existing) &&
                (existing.GeometryKey != geometryKey || existing.SourceVisible != row.SourceVisible))
                throw new InvalidDataException("The streamed occurrence changed its geometry or source visibility.");
        }
        var staged = new List<(int Index, Part Part, StreamedInstanceAllocation Allocation)>();
        var retired = new List<StreamedInstanceAllocation>();
        try
        {
            foreach (var match in matching)
            {
                token.ThrowIfCancellationRequested();
                var part = match.Part; var known = part.Occurrences.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
                var added = incoming.Where(row => !known.Contains(row.Key)).ToArray();
                bool changed = added.Length > 0 || part.Occurrences.Any(row => byKey.TryGetValue(row.Key, out var update) && update.World != row.World);
                if (!changed) continue;
                int count = checked(part.Occurrences.Count + added.Length);
                long cpuBytes = checked(count * 384L), gpuBytes = checked(count * 104L);
                if (batch is not null) await batch.BeforeAdmissionAsync(source.MemoryBudget, owner, cpuBytes, gpuBytes, token);
                owner.EnsureResourceAdmission(source.MemoryBudget, cpuBytes, gpuBytes);
                var cpu = source.MemoryBudget.Reserve(cpuBytes, "streamed instance upload copies");
                IDisposable? gpu = null;
                try
                {
                    gpu = owner.ReserveGpu(gpuBytes, "streamed instance buffers");
                    var rows = part.Occurrences.Select(row => byKey.GetValueOrDefault(row.Key) ?? row).Concat(added).ToArray();
                    var ids = part.Identifiers.Concat(added.Select(_ => Guid.NewGuid())).ToArray();
                    var allocation = new StreamedInstanceAllocation(this, part.Model, cpu, gpu, checked(cpuBytes + gpuBytes));
                    staged.Add((match.Index, part with { Occurrences = rows, Identifiers = ids }, allocation));
                    gpu = null;
                }
                catch { gpu?.Dispose(); cpu.Dispose(); throw; }
            }
            token.ThrowIfCancellationRequested();
            foreach (var row in incoming)
            {
                if (!occurrences.ContainsKey(row.Key)) visibility.Add(row.Key, row.SourceVisible);
                occurrences[row.Key] = row;
            }
            foreach (var update in staged)
            {
                if (streamedInstanceAllocations.TryGetValue(update.Part.Model, out var previous)) retired.Add(previous);
                streamedInstanceAllocations[update.Part.Model] = update.Allocation;
                leases.Add(update.Allocation); update.Allocation.Committed = true;
                parts[update.Index] = update.Part;
                for (int index = 0; index < update.Part.Occurrences.Count; index++)
                    instanceKeys[update.Part.Identifiers[index]] = update.Part.Occurrences[index].Key;
                ApplyInstances(update.Part);
            }
            if (staged.Count > 0)
            {
                RenderView.InvalidateRender();
                if (batch is not null)
                    await batch.AddAsync(retired, staged.Sum(row => row.Allocation.Bytes), token);
                else
                {
                    await WaitForFrameAsync(token);
                    // Keep previous reservations through upload. On cancellation they remain owned until unload.
                    foreach (var allocation in retired) { allocation.Dispose(); leases.Remove(allocation); }
                }
            }
        }
        finally
        {
            foreach (var update in staged) if (!update.Allocation.Committed) update.Allocation.Dispose();
        }
    }

    private sealed class InstanceRetirementBatch(AssetViewport owner)
    {
        private const int MaximumGroups = 64;
        private const long MaximumBytes = 16L * 1024 * 1024;
        private readonly List<StreamedInstanceAllocation> retired = [];
        private int groups;
        private long bytes;

        public async Task BeforeAdmissionAsync(AssetMemoryBudget budget, AssetViewportService service,
            long cpuBytes, long gpuBytes, CancellationToken token)
        {
            if (groups != 0 && (cpuBytes > budget.MaximumBytes - budget.UsedBytes ||
                gpuBytes > service.GpuLimitBytes - service.GpuBytes)) await FlushAsync(token);
        }

        public async Task AddAsync(IReadOnlyList<StreamedInstanceAllocation> previous, long uploadBytes, CancellationToken token)
        {
            long addedBytes = checked(uploadBytes + previous.Sum(row => row.Bytes));
            if (groups != 0 && addedBytes > MaximumBytes - bytes) await FlushAsync(token);
            retired.AddRange(previous);
            groups++;
            bytes = checked(bytes + addedBytes);
            if (groups >= MaximumGroups || bytes >= MaximumBytes) await FlushAsync(token);
        }

        public async Task FlushAsync(CancellationToken token)
        {
            if (groups == 0) return;
            await owner.WaitForFrameAsync(token);
            ReleaseAfterFrame();
        }

        public void ReleaseAfterFrame()
        {
            foreach (var allocation in retired) { allocation.Dispose(); owner.leases.Remove(allocation); }
            retired.Clear(); groups = 0; bytes = 0;
        }
    }

    private sealed class StreamedInstanceAllocation(AssetViewport owner, InstancingMeshGeometryModel3D model,
        IDisposable cpu, IDisposable gpu, long bytes) : IDisposable
    {
        public long Bytes { get; } = bytes;
        public InstancingMeshGeometryModel3D? Model { get; set; } = model;
        public bool Committed { get; set; }
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            if (Model is { } current && owner.streamedInstanceAllocations.TryGetValue(current, out var allocation) && ReferenceEquals(allocation, this))
                owner.streamedInstanceAllocations.Remove(current);
            Model = null;
            try { cpu.Dispose(); } finally { gpu.Dispose(); }
        }
    }
}
