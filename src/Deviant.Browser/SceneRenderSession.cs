using System.Numerics;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Catalog;
using System.Text.Json;
using Deviant.Formats.Containers;
using Deviant.Formats.Models;
using Deviant.Formats.Scenes;

namespace Deviant.Browser;

/// <summary>Progressive, source-pinned scene document/occurrence projection for the renderer.</summary>
public sealed class SceneRenderSession : IAsyncDisposable
{
    public const long MaximumGraphBytes = 64L * 1024 * 1024;
    public const int MaximumCatalogOccurrences = 4096;
    public const int MaximumLargeCatalogOccurrences = 65_536;
    public const long MaximumLargeSourceVerificationBytes = SourceExtentVerifier.MaximumBytes;
    private readonly ArchiveSnapshot snapshot;
    private readonly NativeModelSession models;
    private readonly AssetMemoryBudget budget;
    private readonly NativeCatalogBindingResolver bindings = new();
    private readonly LocalizationReferenceReader references = new();
    private readonly SemaphoreSlim loadGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ArchiveSourceSession.Lease sourceSession;
    private readonly CacheManager? preparedCache;
    private readonly WorkspacePreparationScheduler? preparation;
    private readonly bool ownsPreparation;
    private readonly HashSet<string> preparedKeys = new(StringComparer.Ordinal);
    private bool disposed;

    public SceneRenderSession(ArchiveSnapshot snapshot, NativeModelSession models, AssetMemoryBudget budget,
        CacheManager? preparedCache = null, WorkspacePreparationScheduler? preparation = null)
    {
        this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        ArgumentNullException.ThrowIfNull(models);
        this.models = preparedCache is null ? models : models.WithPreparedTextures(preparedCache);
        this.budget = budget ?? throw new ArgumentNullException(nameof(budget));
        if (models.Snapshot.IndexContentId != snapshot.IndexContentId) throw new ArchiveException("SourceChanged", "Model and scene sources differ.");
        sourceSession = ArchiveSourceSession.Acquire(snapshot, (bytes, reason) => budget.Reserve(bytes, reason));
        this.preparedCache = preparedCache;
        this.preparation = preparation ?? (preparedCache is not null ? new WorkspacePreparationScheduler() : null);
        ownsPreparation = preparation is null && this.preparation is not null;
    }

    public ArchiveSnapshot Snapshot => snapshot;
    public SourceRevision Revision => new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);

    public async Task<RenderAssetSnapshot> LoadAsync(SceneRenderRequest request,
        IProgress<RenderLoadProgress>? progress = null, CancellationToken token = default,
        Func<RenderAssetSnapshot, CancellationToken, Task>? geometryReady = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await loadGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { return await Task.Run(() => LoadCoreAsync(request, progress, linked.Token, geometryReady), linked.Token).ConfigureAwait(false); }
        finally { loadGate.Release(); }
    }

    public async Task<SceneRegionDiscovery> DescribeRegionsAsync(SceneLoadedSection section,
        IProgress<RenderLoadProgress>? progress = null, CancellationToken token = default, bool allowLargeData = false)
    {
        ArgumentNullException.ThrowIfNull(section);
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await loadGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
            using var largeLoadCapacity = allowLargeData ? budget.AllowLargeSceneLoad() : null;
            var document = section.Result.Value ?? throw new ArchiveException("SceneUnavailable", "The scene section is unavailable.");
            if (section.Result.Revision != Revision)
                throw new ArchiveException("SourceChanged", "The selected scene belongs to another archive revision.");
            var regions = new List<SceneRenderRegion>();
            var issues = new List<RenderIssue>();
            for (var index = 0; index < document.CatalogInstances.Count; index++)
            {
                linked.Token.ThrowIfCancellationRequested();
                var instance = document.CatalogInstances[index];
                progress?.Report(new("Regions", index, document.CatalogInstances.Count, budget.UsedBytes));
                try
                {
                    using var layout = await ReadLayoutAsync(new(section, instance.Identity,
                        new(Vector3.Zero, Vector3.Zero), SceneRenderScope.Branch) { AllowLargeData = allowLargeData }, progress, linked.Token,
                        boundsOnly: true).ConfigureAwait(false);
                    issues.AddRange(layout.Issues);
                    if (layout.Occurrences.Count == 0) continue;
                    var bounds = BoundsOf(layout.Occurrences.Select(item => item.Bounds));
                    var visible = layout.Occurrences.Where(item => (item.NativeFlags & 1) != 0).ToArray();
                    var anchors = visible.Length > 0 ? visible : layout.Occurrences;
                    var previewBounds = ScenePreviewBounds.Choose(anchors.Select(item => item.Bounds).ToArray());
                    var label = string.IsNullOrWhiteSpace(instance.InstanceName) ? instance.CatalogName : instance.InstanceName;
                    regions.Add(new(instance.Identity, label, bounds,
                        previewBounds, layout.Occurrences.Count)
                        { VisibleOccurrenceCount = visible.Length });
                }
                catch (ArchiveException error) when (!IsWorkloadLimit(error))
                { issues.Add(new(instance.Identity, error.Code, error.Message)); }
            }
            progress?.Report(new("Regions", document.CatalogInstances.Count, document.CatalogInstances.Count, budget.UsedBytes));
            return new SceneRegionDiscovery(regions, issues);
            }, linked.Token).ConfigureAwait(false);
        }
        finally { loadGate.Release(); }
    }

    private async Task<RenderAssetSnapshot> LoadCoreAsync(SceneRenderRequest request,
        IProgress<RenderLoadProgress>? progress, CancellationToken token,
        Func<RenderAssetSnapshot, CancellationToken, Task>? geometryReady)
    {
        ValidateRequest(request);
        using var foreground = preparation?.EnterForeground();
        if (preparation is not null) await preparation.DrainAsync(token).ConfigureAwait(false);
        var key = CreatePreparedKey(request, Revision);
        if (preparedCache is not null)
        {
            var cached = await preparedCache.TryAcquirePreparedAsync("scenes", key, ".scene",
                (path, cancellation) => PreparedSceneCodec.ValidateAsync(path, key, Revision, cancellation, budget), token).ConfigureAwait(false);
            if (cached is not null)
            {
                using var cacheOwner = new RenderResourceGroup();
                cacheOwner.Add(cached);
                try
                {
                    var result = await PreparedSceneCodec.ReadAsync(cached.Path, key, sourceSession.Session,
                        snapshot, budget, request.AllowLargeData, token, cacheOwner.AcquireLease).ConfigureAwait(false);
                    result.OwnPreparedResource(cacheOwner.AcquireLease());
                    AssetLoadDiagnostics.Cache("PreparedScene", key, true);
                    return result;
                }
                catch { throw; }
            }
        }
        AssetLoadDiagnostics.Cache("PreparedScene", key, false);
        var fresh = await LoadUnpreparedAsync(request, progress, token, geometryReady).ConfigureAwait(false);
        if (preparedCache is not null && preparation is not null)
        {
            preparedKeys.Add("scene/" + key);
            preparation.Enqueue("scene/" + key, async cancellation =>
            {
                using var publication = await preparedCache.AcquirePreparedAsync("scenes", key, ".scene",
                    async (path, jobToken) =>
                    {
                        await PreparedSceneCodec.WriteAsync(path, key, fresh, jobToken).ConfigureAwait(false);
                        sourceSession.Session.EnsureCurrent();
                    }, async (path, jobToken) =>
                    {
                        if (!await PreparedSceneCodec.ValidateAsync(path, key, Revision, jobToken).ConfigureAwait(false)) return false;
                        await sourceSession.Session.VerifyAsync(fresh.SourceExtents, jobToken,
                            SourceExtentVerifier.MaximumBytes, SourceExtentVerifier.MaximumLargeExtentCount).ConfigureAwait(false);
                        sourceSession.Session.EnsureCurrent(); return true;
                    }, cancellation).ConfigureAwait(false);
                sourceSession.Session.EnsureCurrent();
            }, fresh.AcquireLease());
        }
        return fresh;
    }

    private async Task<RenderAssetSnapshot> LoadUnpreparedAsync(SceneRenderRequest request,
        IProgress<RenderLoadProgress>? progress, CancellationToken token,
        Func<RenderAssetSnapshot, CancellationToken, Task>? geometryReady)
    {
        int dimension = MaterialPreviewDimension(request);
        var meshes = new Dictionary<string, RenderMesh>(StringComparer.Ordinal);
        var materials = new Dictionary<ResourceIdentity, RenderMaterial>();
        var stagedOccurrences = new Dictionary<string, RenderOccurrence>(StringComparer.Ordinal);
        var stagedHierarchy = new Dictionary<string, RenderHierarchyNode>(StringComparer.Ordinal);
        var issues = new List<RenderIssue>();
        var sourceExtents = new List<SourceExtent>();
        var resources = new RenderResourceGroup();
        var resultGate = new object();
        using var publicationGate = new SemaphoreSlim(1, 1);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var workToken = cancellation.Token;
        var geometryQueue = System.Threading.Channels.Channel.CreateBounded<LayoutPublication>(4);
        var materialQueue = System.Threading.Channels.Channel.CreateBounded<MaterialPreparation>(4);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var scheduled = new Dictionary<(string Container, string Catalog), HashSet<MeshSelection>>();
        long lastPublication = 0;
        int selectionLimit = request.AllowLargeData ? NativeModelSession.MaximumLargeSelections : NativeModelSession.MaximumSelections;
        if (request.AllowLargeData) resources.Add(budget.AllowLargeSceneLoad());
        var geometryWorkers = Enumerable.Range(0, 2).Select(_ => Task.Run(() => GuardAsync(PrepareGeometryAsync))).ToArray();
        var materialWorkers = Enumerable.Range(0, 6).Select(_ => Task.Run(() => GuardAsync(PrepareMaterialsAsync))).ToArray();
        try
        {
            using var layout = await ReadLayoutAsync(request, progress, workToken, publishLayout: async (packet, jobToken) =>
            {
                var key = (packet.Metadata.ContainerLeaseIdentity.Key, packet.Metadata.CatalogIdentity.Key);
                if (!scheduled.TryGetValue(key, out var seen)) scheduled.Add(key, seen = []);
                var fresh = packet.Metadata.Selections.Where(selection => seen.Add(selection)).ToArray();
                lock (resultGate)
                {
                    foreach (var row in packet.Occurrences)
                        stagedOccurrences[row.Key] = new(row.Key, row.Entity, row.Node, row.Label,
                            $"{row.MeshIdentity.Key}/group/{row.GroupIndex}", row.World, row.Bounds, row.InstancePath)
                        { NativeFlags = row.NativeFlags, SourceVisible = (row.NativeFlags & 1) != 0 };
                    foreach (var node in packet.Hierarchy) stagedHierarchy[node.Key] = node;
                }
                var queued = packet with
                {
                    Metadata = packet.Metadata with { Selections = fresh, NestedCatalogs = [] },
                    Reservation = budget.Reserve(checked(4096L + packet.Occurrences.Count * 2048L +
                        packet.Hierarchy.Count * 2048L + packet.Extents.Count * 256L), "queued scene layout")
                };
                try { await geometryQueue.Writer.WriteAsync(queued, jobToken).ConfigureAwait(false); }
                catch { queued.Dispose(); throw; }
            }).ConfigureAwait(false);
            geometryQueue.Writer.TryComplete();
            await Task.WhenAll(geometryWorkers).ConfigureAwait(false);
            await PublishAsync(layout.SourceExtents, force: true).ConfigureAwait(false);
            materialQueue.Writer.TryComplete();
            await Task.WhenAll(materialWorkers).ConfigureAwait(false);
            workToken.ThrowIfCancellationRequested();
            resources.Add(layout.TakeReservation());
            issues.AddRange(layout.Issues); sourceExtents.AddRange(layout.SourceExtents);
            var occurrences = layout.Occurrences.Select(row => new RenderOccurrence(row.Key, row.Entity, row.Node, row.Label,
                $"{row.MeshIdentity.Key}/group/{row.GroupIndex}", row.World, row.Bounds, row.InstancePath)
                { NativeFlags = row.NativeFlags, SourceVisible = (row.NativeFlags & 1) != 0 }).ToArray();
            var distinctExtents = sourceExtents.DistinctBy(row => (row.RelativeSource, row.Offset, row.Length, row.PayloadSha256)).ToArray();
            long verificationLimit = request.AllowLargeData ? MaximumLargeSourceVerificationBytes :
                request.Scope == SceneRenderScope.Scene ? 2L * 1024 * 1024 * 1024 : SourceExtentVerifier.DefaultMaximumBytes;
            int extentLimit = request.AllowLargeData ? SourceExtentVerifier.MaximumLargeExtentCount : SourceExtentVerifier.MaximumExtentCount;
            if (distinctExtents.Length > extentLimit || distinctExtents.Sum(row => row.Length) > verificationLimit)
                throw new ArchiveException("SceneVerificationLimit", "Scene source verification exceeds its limit.");
            resources.Add(await new SourceExtentVerifier().AcquireAsync(snapshot, distinctExtents, workToken,
                maximumBytes: verificationLimit, maximumExtentCount: extentLimit).ConfigureAwait(false));
            var region = request.Scope == SceneRenderScope.Region || occurrences.Length == 0 ? request.Region : BoundsOf(occurrences.Select(row => row.WorldBounds));
            int renderedParts = occurrences.Sum(row => meshes.TryGetValue(row.GeometryKey, out var mesh) ? mesh.Geometry.Primitives.Count : 0);
            return new(Revision, meshes, occurrences, materials, region, issues, layout.Hierarchy,
                layout.Coverage with { RenderedParts = renderedParts }, distinctExtents, budget, [resources]);
        }
        catch
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            geometryQueue.Writer.TryComplete(); materialQueue.Writer.TryComplete();
            try { await Task.WhenAll(geometryWorkers.Concat(materialWorkers)).ConfigureAwait(false); } catch { }
            while (geometryQueue.Reader.TryRead(out var packet)) packet.Dispose();
            while (materialQueue.Reader.TryRead(out var job)) job.Dispose();
            resources.Dispose(); failure?.Throw(); throw;
        }

        async Task GuardAsync(Func<Task> operation)
        {
            try { await operation().ConfigureAwait(false); }
            catch (Exception error)
            {
                if (error is not OperationCanceledException)
                    Interlocked.CompareExchange(ref failure, System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error), null);
                await cancellation.CancelAsync().ConfigureAwait(false); throw;
            }
        }

        async Task PrepareGeometryAsync()
        {
            await foreach (var packet in geometryQueue.Reader.ReadAllAsync(workToken).ConfigureAwait(false))
            using (packet)
            {
                if (packet.Metadata.Selections.Count == 0) { await PublishAsync(packet.Extents).ConfigureAwait(false); continue; }
                NativeContainerLease? container = null; NativeModelBatch? geometry = null;
                try
                {
                    container = await NativeContainerLease.OpenMetadataAsync(snapshot, packet.Metadata.ContainerLeaseIdentity, budget,
                        workToken, allowLargeData: request.AllowLargeData).ConfigureAwait(false);
                    geometry = await models.LoadGeometryAsync(container, packet.Metadata.CatalogIdentity,
                        packet.Metadata.Selections.Select(row => new NativeModelSelection(row.MeshIdentity, row.GroupIndex)).ToArray(),
                        progress, workToken, dimension, selectionLimit).ConfigureAwait(false);
                    resources.Add(geometry.AcquireLease());
                    lock (resultGate) foreach (var mesh in geometry.Meshes) meshes[mesh.Key] = mesh.Value;
                    await PublishAsync(packet.Extents).ConfigureAwait(false);
                    await materialQueue.Writer.WriteAsync(new(container, geometry, packet.Metadata.CatalogIdentity), workToken).ConfigureAwait(false);
                    container = null; geometry = null;
                }
                finally { geometry?.Dispose(); container?.Dispose(); }
            }
        }

        async Task PrepareMaterialsAsync()
        {
            await foreach (var job in materialQueue.Reader.ReadAllAsync(workToken).ConfigureAwait(false))
            using (job)
            {
                var batch = await models.LoadMaterialsAsync(job.Container, job.Catalog, job.Geometry,
                    progress, workToken, dimension).ConfigureAwait(false);
                resources.Add(batch);
                lock (resultGate)
                {
                    foreach (var material in batch.Materials) materials[material.Key] = material.Value;
                    issues.AddRange(batch.Issues); sourceExtents.AddRange(batch.SourceExtents);
                }
            }
        }

        async Task PublishAsync(IReadOnlyList<SourceExtent> extents, bool force = false)
        {
            if (geometryReady is null) return;
            await publicationGate.WaitAsync(workToken).ConfigureAwait(false);
            try
            {
                if (!force && lastPublication != 0 &&
                    System.Diagnostics.Stopwatch.GetElapsedTime(lastPublication) < TimeSpan.FromMilliseconds(100)) return;
                Dictionary<string, RenderMesh> published; RenderOccurrence[] occurrences; RenderHierarchyNode[] hierarchy;
                lock (resultGate)
                {
                    published = new(meshes, StringComparer.Ordinal);
                    occurrences = stagedOccurrences.Values.Where(row => published.ContainsKey(row.GeometryKey)).ToArray();
                    hierarchy = stagedHierarchy.Values.ToArray();
                }
                if (occurrences.Length == 0) return;
                var visible = occurrences.Where(row => row.SourceVisible).Select(row => row.WorldBounds).ToArray();
                var region = BoundsOf(visible.Length > 0 ? visible : occurrences.Select(row => row.WorldBounds));
                using var slice = new RenderAssetSnapshot(Revision, published, occurrences,
                    new Dictionary<ResourceIdentity, RenderMaterial>(), region, [], hierarchy,
                    new(occurrences.Length, occurrences.Length, 0, 0, 0, 0), extents, budget, [resources.AcquireLease()]);
                sourceSession.Session.EnsureCurrent();
                await geometryReady(slice, workToken).ConfigureAwait(false);
                lastPublication = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            finally { publicationGate.Release(); }
        }
    }
    private static int MaterialPreviewDimension(SceneRenderRequest request)
    {
        if (request.MaximumPreviewDimension is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(request));
        // Geometry is published before material preparation finishes. The returned and
        // cached materials must already have the requested detail for every scene scope.
        return request.MaximumPreviewDimension;
    }

    private static string CreatePreparedKey(SceneRenderRequest request, SourceRevision revision) =>
        CacheManager.PreparedKey("scenes", PreparedSceneCodec.Profile + "/" + NativeTextureDecoder.PreviewVersion,
            revision.IndexContentId + "/" + revision.PhysicalSourceStamp,
            JsonSerializer.Serialize(new { Section = request.Section.Descriptor.Identity.Key, Branch = request.BranchInstance.Key,
                request.Scope, request.Region, request.MaximumPreviewDimension }, new JsonSerializerOptions { IncludeFields = true }));

    private async Task<SceneLayoutData> ReadLayoutAsync(SceneRenderRequest request,
        IProgress<RenderLoadProgress>? progress, CancellationToken token, bool boundsOnly = false,
        Func<LayoutPublication, CancellationToken, Task>? publishLayout = null)
    {
        using var timing = AssetLoadDiagnostics.Measure("SceneLayout", request.Section.Descriptor.Identity.Key);
        ValidateRequest(request);
        var section = request.Section.Result.Value ?? throw new ArchiveException("SceneUnavailable", "The scene section is unavailable.");
        var graphLimit = boundsOnly ? request.AllowLargeData ? 512L * 1024 * 1024 : 32L * 1024 * 1024
            : request.AllowLargeData ? 512L * 1024 * 1024
            : request.Scope == SceneRenderScope.Scene ? 2 * MaximumGraphBytes : MaximumGraphBytes;
        using var graphMemory = new SceneGraphMemory(budget, graphLimit);
        graphMemory.Grow(1024 * 1024);
        TrackGraph(section.Objects.Sum(frame => (long)frame.RawPayload.Length + frame.RawHeader.Length + 1024));
        var issues = new List<RenderIssue>();
        var sourceExtents = new List<SourceExtent>(request.Section.Result.Extents);
        var occurrenceRecords = new List<RenderOccurrenceRecord>();
        var hierarchy = new Dictionary<string, RenderHierarchyNode>(StringComparer.Ordinal);
        var work = new List<ContainerMetadata>();
        var traversedContainers = 0; var catalogInstanceCount = 0;
        var catalogInstanceLimit = request.AllowLargeData ? MaximumLargeCatalogOccurrences : MaximumCatalogOccurrences;
        var pending = new Queue<CatalogWork>();
        var testedOccurrences = 0; var outsideOccurrences = 0;
        TrackGraph(checked(section.CatalogInstances.Count * 32L + request.Section.Result.Extents.Count * 256L));
        var roots = request.Scope == SceneRenderScope.Scene
            ? section.CatalogInstances.OrderByDescending(root => root.Identity == request.BranchInstance).ToArray()
            : [section.CatalogInstances.Single(instance => instance.Identity == request.BranchInstance)];
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var branchNode = FindBranchNode(request.Section, root.Identity);
                var branchWorld = Matrix(branchNode.WorldTransform) ?? throw new ArchiveException("SceneBranchTransformUnavailable", "The selected branch has no usable world transform.");
                var branchBinding = request.Section.ReferencesByNode.GetValueOrDefault(branchNode.Identity)?
                    .FirstOrDefault(item => item.Kind == AssetRelationshipKind.Container && item.NavigationTarget is not null);
                if (branchBinding?.NavigationTarget is not { } firstContainer)
                { AddIssue(issues, new(root.Identity, "CatalogBindingMissing", "The selected branch has no verified catalog target.")); continue; }
                var companion = await ResolveCompanionAsync(firstContainer, token).ConfigureAwait(false);
                if (companion is null)
                { AddIssue(issues, new(firstContainer, "CatalogCompanionMissing", "The selected catalog has no unique type-29 companion.")); continue; }
                catalogInstanceCount++;
                if (catalogInstanceCount > catalogInstanceLimit)
                    throw new ArchiveException("SceneInstanceLimit", "The scene exceeds the catalog instance limit.");
                TrackGraph(4096L + root.InstanceName.Length * 2L);
                pending.Enqueue(new(companion, firstContainer, branchNode.Identity, branchWorld,
                    [root.Identity], [companion], 0, root.InstanceName));
            }
            catch (ArchiveException error) when (request.Scope == SceneRenderScope.Scene && !IsWorkloadLimit(error))
            { AddIssue(issues, new(root.Identity, error.Code, error.Message)); }
        }
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var item = pending.Dequeue();
            if (item.ContainerPath.Count != item.ContainerPath.Distinct().Count()) continue;
            if (item.Depth > 64)
            {
                AddIssue(issues, new(item.Container, "CatalogDepthLimit", "Catalog traversal exceeded the depth limit."));
                continue;
            }
            progress?.Report(new("Metadata", work.Count, work.Count + pending.Count + 1, budget.UsedBytes));
            NativeContainerLease lease;
            try { lease = await NativeContainerLease.OpenMetadataAsync(snapshot, item.Container, budget, token,
                allowLargeData: request.AllowLargeData).ConfigureAwait(false); }
            catch (ArchiveException error) when (IsWorkloadLimit(error)) { throw; }
            catch (Exception error) when (error is ArchiveException or IOException or InvalidDataException)
            { AddIssue(issues, new(item.Container, "ContainerUnavailable", error.Message)); continue; }
            try
            {
                traversedContainers++;
                TrackGraph(checked(4096L + lease.SourceExtents.Count * 256L));
                sourceExtents.AddRange(lease.SourceExtents);
                int occurrenceStart = occurrenceRecords.Count;
                int hierarchyStart = hierarchy.Count;
                var metadata = ReadContainerMetadata(lease.Directory, lease.SourceExtents, item, request.Region, hierarchy, issues, token);
                if (metadata.Selections.Count > 0) work.Add(metadata with { NestedCatalogs = [] });
                if (publishLayout is not null && metadata.Selections.Count > 0)
                {
                    await publishLayout(new(metadata, occurrenceRecords.Skip(occurrenceStart).ToArray(),
                        hierarchy.Values.Skip(hierarchyStart).ToArray(), lease.SourceExtents.ToArray()), token).ConfigureAwait(false);
                }
                TrackGraph(checked(metadata.SourceExtents.Count * 256L));
                sourceExtents.AddRange(metadata.SourceExtents);
                catalogInstanceCount += metadata.NestedCatalogs.Count;
                if (catalogInstanceCount > catalogInstanceLimit)
                    throw new ArchiveException("SceneInstanceLimit", "The scene exceeds the catalog instance limit.");
                using var referenceScratch = graphMemory.ReserveScratch(checked(64L * 1024 + lease.Directory.Objects.Count * 1024L));
                var nativeResolver = new NativeReferenceResolver(lease.Directory);
                foreach (var nested in metadata.NestedCatalogs)
                {
                    var binding = await bindings.ResolveAsync(snapshot, lease.Directory, nested.NodeIdentity, nested.Instance, token, nativeResolver).ConfigureAwait(false);
                    if (!binding.Relationship.Evidence.Verified || binding.CompanionIdentity is not { } child)
                    {
                        AddIssue(issues, new(nested.Instance.Identity, "CatalogBindingUnavailable", binding.Diagnostic ?? "Catalog binding failed."));
                        continue;
                    }
                    TrackGraph(256);
                    sourceExtents.Add(binding.CatalogExtent!);
                    if (item.ContainerPath.Contains(child)) continue;
                    TrackGraph(checked(4096L + (item.InstancePath.Count + item.ContainerPath.Count + 2L) * 128L));
                    pending.Enqueue(new(child, binding.CatalogIdentity, nested.NodeIdentity,
                        nested.World, [.. item.InstancePath, nested.Instance.Identity], [.. item.ContainerPath, child], item.Depth + 1,
                        nested.Instance.InstanceName));
                }
            }
            finally { lease.Dispose(); }
        }

        TrackGraph(checked(hierarchy.Count * 16L + work.Count * 8L + occurrenceRecords.Count * 8L + sourceExtents.Count * 8L));
        var finalHierarchy = hierarchy.Values.ToArray();
        return new(finalHierarchy, work, occurrenceRecords, issues, sourceExtents,
            new(testedOccurrences, occurrenceRecords.Count, outsideOccurrences, 0, traversedContainers, catalogInstanceCount),
            graphMemory.Bytes, graphMemory.Transfer());

        void TrackGraph(long bytes)
        {
            graphMemory.Grow(bytes);
        }

        void AddIssue(List<RenderIssue> target, RenderIssue issue)
        {
            TrackGraph(checked(1024L + issue.Code.Length * 2L + issue.Message.Length * 2L));
            target.Add(issue);
        }

        ContainerMetadata ReadContainerMetadata(NativeObjectDirectoryDocument directory, IReadOnlyList<SourceExtent> physicalExtents, CatalogWork owner,
            GeometryBounds region, Dictionary<string, RenderHierarchyNode> hierarchyNodes, List<RenderIssue> diagnostics,
            CancellationToken cancellation)
        {
            // Raw directory and payload bytes belong to the container lease. These
            // reservations cover parsed nodes, transform copies, hierarchy work maps,
            // reference indexes and parser objects until this owner has been projected.
            var nodeCount = directory.Objects.Count(frame => frame.NativeType == NodeRecordReader.NativeType);
            using var ownerScratch = graphMemory.ReserveScratch(checked(1024L * 1024 +
                directory.Objects.Count * 2048L + nodeCount * 8192L +
                nodeCount * (long)physicalExtents.Count * 32L));
            using var meshMetadataScratch = new RenderResourceGroup();
            var nodes = directory.Objects.Where(frame => frame.NativeType == 2002)
                .Select(frame => new NodeRecordReader().Read(frame, directory.Revision, physicalExtents, token: cancellation).Value)
                .Where(record => record is not null).Select(record => record!).ToArray();
            // Physical extents are already retained by the lease; the hierarchy reader only needs source identity here.
            var graph = new SceneHierarchyBuilder().Build(directory, nodes, token: cancellation);
            var nodeById = graph.Nodes.ToDictionary(node => node.Record.NativeId);
            var nodeByIdentity = graph.Nodes.ToDictionary(node => node.Identity);
            var meshMetadata = new Dictionary<ResourceIdentity, NativeMeshDataDocument>();
            var entityReader = new SceneEntityReader(); var meshReader = new NativeMeshEntityReader();
            var lodResolver = new NativeReferenceResolver(directory);
            var framesByIdentity = directory.Objects.ToDictionary(frame => frame.Identity);
            var nestedCatalogs = new List<NestedCatalog>(); var selections = new List<MeshSelection>();
            var extents = new List<SourceExtent>();
            foreach (var frame in directory.Objects)
            {
                cancellation.ThrowIfCancellationRequested();
                using var entityScratch = frame.NativeType is 2009 || frame.NativeType == SceneCatalogInstanceReader.NativeType
                    ? graphMemory.ReserveScratch(checked(4096L + frame.RawPayload.Length * 32L)) : null;
                if (frame.NativeType == SceneCatalogInstanceReader.NativeType && entityReader.TryRead(frame, out var entity, out _) && entity is not null &&
                    new SceneCatalogInstanceReader().TryRead(frame, entity, out var instance, out _) && instance is not null &&
                    nodeById.TryGetValue(ParseNodeId(entity.Node), out var instanceNode))
                {
                    if (instanceNode.TransformStatus != SceneTransformStatus.Available || instanceNode.WorldTransform is null)
                        AddIssue(diagnostics, new(instance.Identity, "InstanceTransformUnavailable", "The catalog instance transform is unavailable."));
                    else
                    {
                        TrackGraph(4096L + instance.InstanceName.Length * 2L + instance.CatalogName.Length * 2L + owner.InstancePath.Count * 512L);
                        nestedCatalogs.Add(new(instance.Identity, instance, instanceNode.Identity, Compose(instanceNode.WorldTransform, owner.OwnerWorld)));
                    }
                }
                if (frame.NativeType != 2009) continue;
                testedOccurrences++;
                if (!entityReader.TryRead(frame, out var meshEntity, out var entityDiagnostic) || meshEntity is null)
                { AddIssue(diagnostics, new(frame.Identity, "EntityParseFailed", entityDiagnostic ?? "ENTITY v7 could not be read.")); continue; }
                if (!meshReader.TryRead(frame, meshEntity, out var meshDocument, out var meshDiagnostic, cancellation) || meshDocument is null)
                { AddIssue(diagnostics, new(frame.Identity, "MeshEntityParseFailed", meshDiagnostic ?? "MESH____ v29 could not be read.")); continue; }
                var meshNodeId = ParseNodeId(meshEntity.Node);
                if (meshNodeId == 0 || !nodeById.TryGetValue(meshNodeId, out var meshNode))
                { AddIssue(diagnostics, new(frame.Identity, "MeshNodeMissing", $"The mesh entity references NODE {meshNodeId}.")); continue; }
                if (meshNode.TransformStatus != SceneTransformStatus.Available || meshNode.WorldTransform is null)
                { AddIssue(diagnostics, new(frame.Identity, "MeshTransformUnavailable", "The mesh transform is unavailable.")); continue; }
                var world = Compose(meshNode.WorldTransform, owner.OwnerWorld);
                NativeMeshLod? meshLod = null;
                ResourceIdentity? meshIdentity = null;
                NativeMeshDataDocument? meshData = null;
                foreach (var candidate in meshDocument.Lods)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (lodResolver.Resolve(candidate.MeshData) is not { Status: ReferenceStatus.Unique, Candidates.Count: 1 } resolution) continue;
                    var meshFrame = framesByIdentity.GetValueOrDefault(resolution.Candidates[0]);
                    if (meshFrame is null) continue;
                    if (!meshMetadata.TryGetValue(meshFrame.Identity, out var metadata))
                    {
                        var parserScratch = graphMemory.ReserveScratch(checked(4096L + meshFrame.RawPayload.Length * 32L));
                        try
                        {
                            if (!new NativeMeshDataReader().TryRead(meshFrame, out metadata, out _, cancellation) || metadata is null)
                            { parserScratch.Dispose(); continue; }
                            meshMetadataScratch.Add(parserScratch);
                        }
                        catch { parserScratch.Dispose(); throw; }
                        meshMetadata.Add(meshFrame.Identity, metadata);
                    }
                    if (meshDocument.GroupIndex >= metadata.Groups.Count || metadata.Groups[(int)meshDocument.GroupIndex].Parts.Count == 0) continue;
                    meshLod = candidate; meshIdentity = meshFrame.Identity; meshData = metadata; break;
                }
                if (meshLod is null || meshData is null)
                { AddIssue(diagnostics, new(frame.Identity, "MeshLodMissing", "No valid mesh detail is available.")); continue; }
                var bounds = WorldBounds(meshData.Groups[(int)meshDocument.GroupIndex].Bounds, world);
                if (boundsOnly)
                {
                    TrackGraph(1024);
                    occurrenceRecords.Add(new("", frame.Identity, meshNode.Identity, "", meshIdentity!,
                        (int)meshDocument.GroupIndex, world, bounds, owner.InstancePath, meshDocument.NativeFlags));
                    continue;
                }
                var key = string.Join(">", owner.InstancePath.Select(identity => identity.Key).Append(frame.Identity.Key));
                if (request.Scope == SceneRenderScope.Region && !Intersects(bounds, region)) { outsideOccurrences++; continue; }
                var label = string.IsNullOrWhiteSpace(meshData.Name) ? $"Mesh {frame.NativeId}" : meshData.Name;
                TrackGraph(4096L + key.Length * 2L + label.Length * 2L);
                var leafReferences = meshEntity.OtherReferences.Append(meshLod.MeshData)
                    .Concat(meshData.Groups[(int)meshDocument.GroupIndex].Parts.Select(part => part.Material)).ToArray();
                AddHierarchy(meshNode, nodeByIdentity, owner.InstancePath, hierarchyNodes, owner.OwnerWorld, key,
                    label, owner.Label, leafReferences, TrackGraph);
                TrackGraph(checked(256L + physicalExtents.Count * 256L));
                selections.Add(new(meshIdentity!, (int)meshDocument.GroupIndex));
                occurrenceRecords.Add(new(key, frame.Identity, meshNode.Identity,
                    label, meshIdentity!,
                    (int)meshDocument.GroupIndex, world, bounds, owner.InstancePath, meshDocument.NativeFlags));
                extents.AddRange(physicalExtents);
            }
            return new(owner.Container, owner.CatalogIdentity, owner.OwnerWorld,
                selections.DistinctBy(selection => $"{selection.MeshIdentity.Key}/{selection.GroupIndex}").ToArray(), nestedCatalogs,
                extents);
        }
    }

    private static SceneHierarchyNode FindBranchNode(SceneLoadedSection section, ResourceIdentity branch)
    {
        var node = section.EntitiesByNode.FirstOrDefault(pair => pair.Value.Any(entity => entity.Identity == branch)).Key;
        return section.Result.Value!.Hierarchy.Nodes.FirstOrDefault(item => item.Identity == node)
            ?? throw new ArchiveException("SceneBranchUnavailable", "The selected branch node is unavailable.");
    }
    private static uint ParseNodeId(ResourceReference? reference) => reference is not null && uint.TryParse(reference.Id, out var value) ? value : 0;
    private static Matrix4x4? Matrix(SourceTransform? transform) => transform is { Elements.Count: 16 } value
        ? new Matrix4x4(value.Elements[0], value.Elements[1], value.Elements[2], value.Elements[3], value.Elements[4], value.Elements[5], value.Elements[6], value.Elements[7], value.Elements[8], value.Elements[9], value.Elements[10], value.Elements[11], value.Elements[12], value.Elements[13], value.Elements[14], value.Elements[15])
        : null;
    private static Matrix4x4 Compose(SourceTransform? local, Matrix4x4 parent) => Matrix(local) is { } value ? value * parent
        : throw new ArchiveException("SceneTransformUnavailable", "The source transform is unavailable.");
    private static GeometryBounds WorldBounds(NativeMeshBounds bounds, Matrix4x4 world)
    {
        var values = bounds.Obb; var center = new Vector3(values[0], values[1], values[2]);
        var axes = new[] { new Vector3(values[3], values[4], values[5]), new Vector3(values[6], values[7], values[8]), new Vector3(values[9], values[10], values[11]) };
        var half = new Vector3(values[12], values[13], values[14]); var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        for (var mask = 0; mask < 8; mask++) { var point = center + (((mask & 1) == 0) ? -axes[0] * half.X : axes[0] * half.X); point += ((mask & 2) == 0) ? -axes[1] * half.Y : axes[1] * half.Y; point += ((mask & 4) == 0) ? -axes[2] * half.Z : axes[2] * half.Z; var transformed = Vector3.Transform(point, world); min = Vector3.Min(min, transformed); max = Vector3.Max(max, transformed); }
        if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) || !float.IsFinite(min.Z) ||
            !float.IsFinite(max.X) || !float.IsFinite(max.Y) || !float.IsFinite(max.Z))
            throw new ArchiveException("SceneBoundsUnavailable", "A mesh has invalid world bounds.");
        return new(min, max);
    }
    private static bool Intersects(GeometryBounds left, GeometryBounds right) => left.Minimum.X <= right.Maximum.X && left.Maximum.X >= right.Minimum.X && left.Minimum.Y <= right.Maximum.Y && left.Maximum.Y >= right.Minimum.Y && left.Minimum.Z <= right.Maximum.Z && left.Maximum.Z >= right.Minimum.Z;
    private static GeometryBounds BoundsOf(IEnumerable<GeometryBounds> bounds)
    {
        var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
        foreach (var item in bounds) { minimum = Vector3.Min(minimum, item.Minimum); maximum = Vector3.Max(maximum, item.Maximum); }
        return new(minimum, maximum);
    }

    private static bool IsWorkloadLimit(ArchiveException error) => error.Code is
        "AssetMemoryLimit" or "SceneGraphLimit" or "SceneInstanceLimit" or
        "ModelSelectionLimit" or "SceneVerificationLimit" or "NativeContainerLimit" or
        "NativePayloadLimit" or "NativeDirectoryLimit";
    private static void AddHierarchy(SceneHierarchyNode node, IReadOnlyDictionary<ResourceIdentity, SceneHierarchyNode> lookup,
        IReadOnlyList<ResourceIdentity> path, Dictionary<string, RenderHierarchyNode> output, Matrix4x4 ownerWorld,
        string occurrenceKey, string leafLabel, string rootLabel, IReadOnlyList<ResourceReference> leafReferences, Action<long> reserve)
    {
        var chain = new List<SceneHierarchyNode>(); var current = node;
        while (true) { chain.Add(current); if (current.ParentIdentity is not { } parent || !lookup.TryGetValue(parent, out current!)) break; }
        chain.Reverse(); string? parentKey = null;
        foreach (var ancestor in chain)
        {
            var key = string.Join(">", path.Select(identity => identity.Key).Append(ancestor.Identity.Key));
            var leaf = ReferenceEquals(ancestor, node);
            if (!output.TryGetValue(key, out var existing))
            {
                var label = leaf ? leafLabel : parentKey is null && !string.IsNullOrWhiteSpace(rootLabel) ? rootLabel : $"Node {ancestor.Record.NativeId}";
                IReadOnlyList<ResourceReference> links = leaf ? leafReferences : new[] { ancestor.Record.Parent, ancestor.Record.FirstChild, ancestor.Record.NextSibling }
                    .Where(reference => reference is not null).Select(reference => reference!).ToArray();
                reserve(4096L + key.Length * 2L + label.Length * 2L + links.Count * 512L);
                output.Add(key, new(key, parentKey, ancestor.Identity, label,
                    Matrix(ancestor.LocalTransform) ?? throw new ArchiveException("SceneTransformUnavailable", "The local transform is unavailable."),
                    Compose(ancestor.WorldTransform, ownerWorld), leaf ? occurrenceKey : null, links));
            }
            else if (leaf && existing.OccurrenceKey != occurrenceKey)
            {
                reserve(4096L + occurrenceKey.Length * 2L + leafLabel.Length * 2L + leafReferences.Count * 512L);
                if (existing.OccurrenceKey is null)
                    output[key] = existing with { Label = leafLabel, OccurrenceKey = occurrenceKey, References = leafReferences };
                else
                    output.Add(occurrenceKey, new(occurrenceKey, key, ancestor.Identity, leafLabel,
                        Matrix4x4.Identity, Compose(ancestor.WorldTransform, ownerWorld), occurrenceKey, leafReferences));
            }
            parentKey = key;
        }
    }
    private sealed class SceneGraphMemory(AssetMemoryBudget budget, long limit) : IDisposable
    {
        private RenderResourceGroup? retained = new();
        private readonly List<Scratch> scratchReservations = [];
        private long scratchBytes;
        public long Bytes { get; private set; }

        public void Grow(long bytes)
        {
            ObjectDisposedException.ThrowIf(retained is null, this);
            Check(bytes);
            var reservation = budget.Reserve(bytes, "scene graph and metadata");
            retained.Add(reservation);
            Bytes += bytes;
        }

        public IDisposable ReserveScratch(long bytes)
        {
            ObjectDisposedException.ThrowIf(retained is null, this);
            Check(bytes);
            var reservation = budget.Reserve(bytes, "scene graph parsing scratch");
            scratchBytes += bytes;
            var result = new Scratch(this, reservation);
            scratchReservations.Add(result);
            return result;
        }

        public IDisposable Transfer()
        {
            ObjectDisposedException.ThrowIf(retained is null, this);
            if (scratchBytes != 0) throw new InvalidOperationException("Scene parsing scratch is still in use.");
            var result = retained;
            retained = null;
            return result;
        }

        private void Check(long bytes)
        {
            if (bytes < 0 || bytes > limit - Bytes - scratchBytes)
                throw new ArchiveException("SceneGraphLimit", "The scene graph exceeds its memory limit.");
        }

        public void Dispose()
        {
            foreach (var scratch in scratchReservations.ToArray()) scratch.Dispose();
            Interlocked.Exchange(ref retained, null)?.Dispose();
        }

        private sealed class Scratch(SceneGraphMemory owner, AssetMemoryBudget.Reservation reservation) : IDisposable
        {
            private AssetMemoryBudget.Reservation? current = reservation;
            public void Dispose()
            {
                var released = Interlocked.Exchange(ref current, null);
                if (released is null) return;
                owner.scratchBytes -= released.Bytes;
                owner.scratchReservations.Remove(this);
                released.Dispose();
            }
        }
    }

    private sealed record CatalogWork(ResourceIdentity Container, ResourceIdentity CatalogIdentity, ResourceIdentity OwnerNode, Matrix4x4 OwnerWorld, IReadOnlyList<ResourceIdentity> InstancePath, IReadOnlyList<ResourceIdentity> ContainerPath, int Depth, string Label);
    private sealed record NestedCatalog(ResourceIdentity Identity, SceneCatalogInstance Instance, ResourceIdentity NodeIdentity, Matrix4x4 World);
    private sealed record MeshSelection(ResourceIdentity MeshIdentity, int GroupIndex);
    private sealed record RenderOccurrenceRecord(string Key, ResourceIdentity Entity, ResourceIdentity Node, string Label, ResourceIdentity MeshIdentity, int GroupIndex, Matrix4x4 World, GeometryBounds Bounds, IReadOnlyList<ResourceIdentity> InstancePath, uint NativeFlags);
    private sealed record ContainerMetadata(ResourceIdentity ContainerLeaseIdentity, ResourceIdentity CatalogIdentity, Matrix4x4 OwnerWorld, IReadOnlyList<MeshSelection> Selections, IReadOnlyList<NestedCatalog> NestedCatalogs, IReadOnlyList<SourceExtent> SourceExtents);
    private sealed record LayoutPublication(ContainerMetadata Metadata, IReadOnlyList<RenderOccurrenceRecord> Occurrences,
        IReadOnlyList<RenderHierarchyNode> Hierarchy, IReadOnlyList<SourceExtent> Extents) : IDisposable
    {
        public IDisposable? Reservation { get; init; }
        public void Dispose() => Reservation?.Dispose();
    }
    private sealed record MaterialPreparation(NativeContainerLease Container, NativeModelBatch Geometry,
        ResourceIdentity Catalog) : IDisposable
    {
        public void Dispose() { Geometry.Dispose(); Container.Dispose(); }
    }
    private sealed record SceneLayoutData(IReadOnlyList<RenderHierarchyNode> Hierarchy,
        IReadOnlyList<ContainerMetadata> Work, IReadOnlyList<RenderOccurrenceRecord> Occurrences,
        IReadOnlyList<RenderIssue> Issues, IReadOnlyList<SourceExtent> SourceExtents, RenderCoverage Coverage,
        long GraphBytes, IDisposable Reservation) : IDisposable
    {
        private IDisposable? retained = Reservation;
        public IDisposable TakeReservation() => Interlocked.Exchange(ref retained, null)
            ?? throw new ObjectDisposedException(nameof(SceneLayoutData));
        public void Dispose() => Interlocked.Exchange(ref retained, null)?.Dispose();
    }

    private async Task<ResourceIdentity?> ResolveCompanionAsync(ResourceIdentity catalogIdentity, CancellationToken token)
    {
        var table = await references.ReadAsync(snapshot, catalogIdentity, token).ConfigureAwait(false);
        var links = table.Relationships.Where(item => item.Reference.ResourceType == 29 && item.NavigationTarget is not null).ToArray();
        return links.Length == 1 ? links[0].NavigationTarget : null;
    }
    private void ValidateRequest(SceneRenderRequest request)
    {
        if (request.Section.Result.Value is null || request.Section.Result.Revision != Revision || request.BranchInstance.IndexContentId != snapshot.IndexContentId)
            throw new ArchiveException("SourceChanged", "The selected scene belongs to another source revision.");
        if (!Enum.IsDefined(request.Scope)) throw new ArgumentOutOfRangeException(nameof(request));
        var min = request.Region.Minimum; var max = request.Region.Maximum;
        if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) || !float.IsFinite(min.Z) || !float.IsFinite(max.X) || !float.IsFinite(max.Y) || !float.IsFinite(max.Z) ||
            min.X > max.X || min.Y > max.Y || min.Z > max.Z)
            throw new ArgumentException("The render region must be finite and ordered.", nameof(request));
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel();
        await loadGate.WaitAsync().ConfigureAwait(false);
        loadGate.Release(); lifetime.Dispose(); loadGate.Dispose();
        if (preparation is not null)
        {
            using var foreground = preparation.EnterForeground();
            foreach (var key in preparedKeys) preparation.Remove(key);
            await preparation.DrainAsync().ConfigureAwait(false);
            preparedKeys.Clear();
        }
        if (ownsPreparation && preparation is not null) await preparation.DisposeAsync().ConfigureAwait(false);
        references.Dispose(); bindings.Dispose();
        await sourceSession.DisposeAsync().ConfigureAwait(false);
    }
}
