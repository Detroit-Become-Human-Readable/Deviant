using System.Numerics;
using Deviant.Assets;
using Deviant.Core;
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
    private bool disposed;

    public SceneRenderSession(ArchiveSnapshot snapshot, NativeModelSession models, AssetMemoryBudget budget)
    {
        this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        this.models = models ?? throw new ArgumentNullException(nameof(models));
        this.budget = budget ?? throw new ArgumentNullException(nameof(budget));
        if (models.Snapshot.IndexContentId != snapshot.IndexContentId) throw new ArchiveException("SourceChanged", "Model and scene sources differ.");
    }

    public ArchiveSnapshot Snapshot => snapshot;
    public SourceRevision Revision => new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);

    public async Task<RenderAssetSnapshot> LoadAsync(SceneRenderRequest request,
        IProgress<RenderLoadProgress>? progress = null, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await loadGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { return await Task.Run(() => LoadCoreAsync(request, progress, linked.Token), linked.Token).ConfigureAwait(false); }
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
        IProgress<RenderLoadProgress>? progress, CancellationToken token)
    {
        if (request.MaximumPreviewDimension is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(request), "The preview dimension must be between 1 and 4096.");
        var meshes = new Dictionary<string, RenderMesh>(StringComparer.Ordinal);
        var materials = new Dictionary<ResourceIdentity, RenderMaterial>();
        var resources = new List<IDisposable>();
        if (request.AllowLargeData) resources.Add(budget.AllowLargeSceneLoad());
        try
        {
            using var layout = await ReadLayoutAsync(request, progress, token).ConfigureAwait(false);
            var issues = new List<RenderIssue>(layout.Issues);
            var sourceExtents = new List<SourceExtent>(layout.SourceExtents);
            resources.Add(layout.TakeReservation());
            foreach (var itemGroup in layout.Work.GroupBy(item => (item.ContainerLeaseIdentity.Key, item.CatalogIdentity.Key)))
            {
                token.ThrowIfCancellationRequested();
                var item = itemGroup.First();
                using var lease = await NativeContainerLease.OpenAsync(snapshot, item.ContainerLeaseIdentity, budget, token,
                    allowLargeData: request.AllowLargeData).ConfigureAwait(false);
                var selections = itemGroup.SelectMany(value => value.Selections)
                    .DistinctBy(selection => $"{selection.MeshIdentity.Key}/{selection.GroupIndex}").ToArray();
                var batch = await models.LoadAsync(lease, item.CatalogIdentity,
                    selections.Select(selection => new NativeModelSelection(selection.MeshIdentity, selection.GroupIndex)).ToArray(),
                    progress, token, maximumPreviewDimension: request.Scope == SceneRenderScope.Scene
                        ? Math.Min(256, request.MaximumPreviewDimension) : request.MaximumPreviewDimension,
                    maximumSelections: request.AllowLargeData ? NativeModelSession.MaximumLargeSelections
                        : NativeModelSession.MaximumSelections).ConfigureAwait(false);
                resources.Add(batch);
                foreach (var mesh in batch.Meshes) meshes[mesh.Key] = mesh.Value;
                foreach (var material in batch.Materials) materials[material.Key] = material.Value;
                issues.AddRange(batch.Issues);
                sourceExtents.AddRange(batch.SourceExtents);
            }
            var occurrences = layout.Occurrences.Select(item => new RenderOccurrence(item.Key, item.Entity, item.Node,
                item.Label, $"{item.MeshIdentity.Key}/group/{item.GroupIndex}", item.World, item.Bounds, item.InstancePath)
                { NativeFlags = item.NativeFlags, SourceVisible = (item.NativeFlags & 1) != 0 }).ToArray();
            var renderedParts = layout.Occurrences.Sum(item => meshes.TryGetValue($"{item.MeshIdentity.Key}/group/{item.GroupIndex}", out var mesh)
                ? mesh.Geometry.Primitives.Count : 0);
            var distinctExtents = sourceExtents.DistinctBy(item =>
                (item.RelativeSource, item.Offset, item.Length, item.PayloadSha256)).ToArray();
            var verificationLimit = request.AllowLargeData ? MaximumLargeSourceVerificationBytes
                : request.Scope == SceneRenderScope.Scene ? 2L * 1024 * 1024 * 1024
                : SourceExtentVerifier.DefaultMaximumBytes;
            var verificationExtentLimit = request.AllowLargeData ? SourceExtentVerifier.MaximumLargeExtentCount
                : SourceExtentVerifier.MaximumExtentCount;
            if (distinctExtents.Length > verificationExtentLimit)
                throw new ArchiveException("SceneVerificationLimit", "Scene source verification exceeds its extent limit.");
            long verificationBytes = 0;
            foreach (var extent in distinctExtents)
                verificationBytes = checked(verificationBytes + extent.Length);
            if (verificationBytes > verificationLimit)
                throw new ArchiveException("SceneVerificationLimit", "Scene source verification exceeds its byte limit.");
            var finalVerification = await new SourceExtentVerifier().AcquireAsync(snapshot,
                distinctExtents, token, maximumBytes: verificationLimit,
                maximumExtentCount: verificationExtentLimit).ConfigureAwait(false);
            resources.Add(finalVerification);
            var region = request.Scope == SceneRenderScope.Region || occurrences.Length == 0
                ? request.Region : BoundsOf(occurrences.Select(item => item.WorldBounds));
            return new(Revision, meshes, occurrences, materials, region, issues, layout.Hierarchy,
                layout.Coverage with { RenderedParts = renderedParts }, distinctExtents, budget, resources);
        }
        catch { foreach (var resource in resources) resource.Dispose(); throw; }
    }

    private async Task<SceneLayoutData> ReadLayoutAsync(SceneRenderRequest request,
        IProgress<RenderLoadProgress>? progress, CancellationToken token, bool boundsOnly = false)
    {
        ValidateRequest(request);
        var section = request.Section.Result.Value ?? throw new ArchiveException("SceneUnavailable", "The scene section is unavailable.");
        var graphLimit = boundsOnly ? request.AllowLargeData ? 512L * 1024 * 1024 : 32L * 1024 * 1024
            : request.AllowLargeData ? 512L * 1024 * 1024
            : request.Scope == SceneRenderScope.Scene ? 2 * MaximumGraphBytes : MaximumGraphBytes;
        using var graphScratch = budget.Reserve(graphLimit, "scene graph and metadata");
        long graphBytes = 1024 * 1024;
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
        var roots = request.Scope == SceneRenderScope.Scene
            ? section.CatalogInstances.ToArray()
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
                { issues.Add(new(root.Identity, "CatalogBindingMissing", "The selected branch has no verified catalog target.")); continue; }
                var companion = await ResolveCompanionAsync(firstContainer, token).ConfigureAwait(false);
                if (companion is null)
                { issues.Add(new(firstContainer, "CatalogCompanionMissing", "The selected catalog has no unique type-29 companion.")); continue; }
                catalogInstanceCount++;
                if (catalogInstanceCount > catalogInstanceLimit)
                    throw new ArchiveException("SceneInstanceLimit", "The scene exceeds the catalog instance limit.");
                pending.Enqueue(new(companion, firstContainer, branchNode.Identity, branchWorld,
                    [root.Identity], [companion], 0, root.InstanceName));
            }
            catch (ArchiveException error) when (request.Scope == SceneRenderScope.Scene && !IsWorkloadLimit(error))
            { issues.Add(new(root.Identity, error.Code, error.Message)); }
        }
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var item = pending.Dequeue();
            if (item.ContainerPath.Count != item.ContainerPath.Distinct().Count()) continue;
            if (item.Depth > 64)
            {
                issues.Add(new(item.Container, "CatalogDepthLimit", "Catalog traversal exceeded the depth limit."));
                continue;
            }
            progress?.Report(new("Metadata", work.Count, work.Count + pending.Count + 1, budget.UsedBytes));
            NativeContainerLease lease;
            try { lease = await NativeContainerLease.OpenAsync(snapshot, item.Container, budget, token,
                allowLargeData: request.AllowLargeData).ConfigureAwait(false); }
            catch (ArchiveException error) when (IsWorkloadLimit(error)) { throw; }
            catch (Exception error) when (error is ArchiveException or IOException or InvalidDataException)
            { issues.Add(new(item.Container, "ContainerUnavailable", error.Message)); continue; }
            try
            {
                traversedContainers++;
                sourceExtents.AddRange(lease.SourceExtents);
                var metadata = ReadContainerMetadata(lease.Directory, lease.SourceExtents, item, request.Region, hierarchy, issues, token);
                if (metadata.Selections.Count > 0) work.Add(metadata with { NestedCatalogs = [] });
                sourceExtents.AddRange(metadata.SourceExtents);
                catalogInstanceCount += metadata.NestedCatalogs.Count;
                if (catalogInstanceCount > catalogInstanceLimit)
                    throw new ArchiveException("SceneInstanceLimit", "The scene exceeds the catalog instance limit.");
                foreach (var nested in metadata.NestedCatalogs)
                {
                    var binding = await bindings.ResolveAsync(snapshot, lease.Directory, nested.NodeIdentity, nested.Instance, token).ConfigureAwait(false);
                    if (!binding.Relationship.Evidence.Verified || binding.CompanionIdentity is not { } child)
                    {
                        issues.Add(new(nested.Instance.Identity, "CatalogBindingUnavailable", binding.Diagnostic ?? "Catalog binding failed."));
                        continue;
                    }
                    sourceExtents.Add(binding.CatalogExtent!);
                    if (item.ContainerPath.Contains(child)) continue;
                    pending.Enqueue(new(child, binding.CatalogIdentity, nested.NodeIdentity,
                        nested.World, [.. item.InstancePath, nested.Instance.Identity], [.. item.ContainerPath, child], item.Depth + 1,
                        nested.Instance.InstanceName));
                }
            }
            finally { lease.Dispose(); }
        }

        var retained = budget.Reserve(graphBytes, "scene layout");
        return new(hierarchy.Values.ToArray(), work, occurrenceRecords, issues, sourceExtents,
            new(testedOccurrences, occurrenceRecords.Count, outsideOccurrences, 0, traversedContainers, catalogInstanceCount),
            graphBytes, retained);

        void TrackGraph(long bytes)
        {
            if (bytes < 0 || bytes > graphLimit - graphBytes)
                throw new ArchiveException("SceneGraphLimit", "The scene graph exceeds its memory limit.");
            graphBytes += bytes;
        }

        ContainerMetadata ReadContainerMetadata(NativeObjectDirectoryDocument directory, IReadOnlyList<SourceExtent> physicalExtents, CatalogWork owner,
            GeometryBounds region, Dictionary<string, RenderHierarchyNode> hierarchyNodes, List<RenderIssue> diagnostics,
            CancellationToken cancellation)
        {
            var nodes = directory.Objects.Where(frame => frame.NativeType == 2002)
                .Select(frame => new NodeRecordReader().Read(frame, directory.Revision, physicalExtents, token: cancellation).Value)
                .Where(record => record is not null).Select(record => record!).ToArray();
            // Physical extents are already retained by the lease; the hierarchy reader only needs source identity here.
            var graph = new SceneHierarchyBuilder().Build(directory, nodes, token: cancellation);
            var nodeById = graph.Nodes.ToDictionary(node => node.Record.NativeId);
            var nodeByIdentity = graph.Nodes.ToDictionary(node => node.Identity);
            var meshMetadata = new Dictionary<ResourceIdentity, NativeMeshDataDocument>();
            var entityReader = new SceneEntityReader(); var meshReader = new NativeMeshEntityReader();
            var nestedCatalogs = new List<NestedCatalog>(); var selections = new List<MeshSelection>();
            var extents = new List<SourceExtent>();
            foreach (var frame in directory.Objects)
            {
                cancellation.ThrowIfCancellationRequested();
                if (frame.NativeType == SceneCatalogInstanceReader.NativeType && entityReader.TryRead(frame, out var entity, out _) && entity is not null &&
                    new SceneCatalogInstanceReader().TryRead(frame, entity, out var instance, out _) && instance is not null &&
                    nodeById.TryGetValue(ParseNodeId(entity.Node), out var instanceNode))
                {
                    if (instanceNode.TransformStatus != SceneTransformStatus.Available || instanceNode.WorldTransform is null)
                        diagnostics.Add(new(instance.Identity, "InstanceTransformUnavailable", "The catalog instance transform is unavailable."));
                    else
                    {
                        TrackGraph(4096L + instance.InstanceName.Length * 2L + instance.CatalogName.Length * 2L + owner.InstancePath.Count * 512L);
                        nestedCatalogs.Add(new(instance.Identity, instance, instanceNode.Identity, Compose(instanceNode.WorldTransform, owner.OwnerWorld)));
                    }
                }
                if (frame.NativeType != 2009) continue;
                testedOccurrences++;
                if (!entityReader.TryRead(frame, out var meshEntity, out var entityDiagnostic) || meshEntity is null)
                { diagnostics.Add(new(frame.Identity, "EntityParseFailed", entityDiagnostic ?? "ENTITY v7 could not be read.")); continue; }
                if (!meshReader.TryRead(frame, meshEntity, out var meshDocument, out var meshDiagnostic, cancellation) || meshDocument is null)
                { diagnostics.Add(new(frame.Identity, "MeshEntityParseFailed", meshDiagnostic ?? "MESH____ v29 could not be read.")); continue; }
                var meshNodeId = ParseNodeId(meshEntity.Node);
                if (meshNodeId == 0 || !nodeById.TryGetValue(meshNodeId, out var meshNode))
                { diagnostics.Add(new(frame.Identity, "MeshNodeMissing", $"The mesh entity references NODE {meshNodeId}.")); continue; }
                if (meshNode.TransformStatus != SceneTransformStatus.Available || meshNode.WorldTransform is null)
                { diagnostics.Add(new(frame.Identity, "MeshTransformUnavailable", "The mesh transform is unavailable.")); continue; }
                var world = Compose(meshNode.WorldTransform, owner.OwnerWorld);
                var meshLod = meshDocument.Lods.FirstOrDefault();
                if (meshLod is null) { diagnostics.Add(new(frame.Identity, "MeshLodMissing", "The MESH entity has no LOD records.")); continue; }
                var meshIdentity = new NativeReferenceResolver(directory).Resolve(meshLod.MeshData) is { Status: ReferenceStatus.Unique, Candidates.Count: 1 } meshResolution
                    ? meshResolution.Candidates[0] : null;
                var meshFrame = meshIdentity is null ? null : directory.Objects.SingleOrDefault(candidate => candidate.Identity == meshIdentity);
                if (meshFrame is null) { diagnostics.Add(new(frame.Identity, "MeshDataMissing", "The MESH LOD resource is missing.")); continue; }
                if (!meshMetadata.TryGetValue(meshFrame.Identity, out var meshData))
                {
                    if (!new NativeMeshDataReader().TryRead(meshFrame, out meshData, out var meshDataDiagnostic, cancellation) || meshData is null)
                    { diagnostics.Add(new(meshFrame.Identity, "MeshMetadataUnsupported", meshDataDiagnostic ?? "Mesh metadata is unavailable.")); continue; }
                    meshMetadata.Add(meshFrame.Identity, meshData);
                }
                if (meshDocument.GroupIndex >= meshData.Groups.Count) { diagnostics.Add(new(frame.Identity, "MeshGroupMissing", "The selected group is outside the mesh metadata.")); continue; }
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
    private sealed record CatalogWork(ResourceIdentity Container, ResourceIdentity CatalogIdentity, ResourceIdentity OwnerNode, Matrix4x4 OwnerWorld, IReadOnlyList<ResourceIdentity> InstancePath, IReadOnlyList<ResourceIdentity> ContainerPath, int Depth, string Label);
    private sealed record NestedCatalog(ResourceIdentity Identity, SceneCatalogInstance Instance, ResourceIdentity NodeIdentity, Matrix4x4 World);
    private sealed record MeshSelection(ResourceIdentity MeshIdentity, int GroupIndex);
    private sealed record RenderOccurrenceRecord(string Key, ResourceIdentity Entity, ResourceIdentity Node, string Label, ResourceIdentity MeshIdentity, int GroupIndex, Matrix4x4 World, GeometryBounds Bounds, IReadOnlyList<ResourceIdentity> InstancePath, uint NativeFlags);
    private sealed record ContainerMetadata(ResourceIdentity ContainerLeaseIdentity, ResourceIdentity CatalogIdentity, Matrix4x4 OwnerWorld, IReadOnlyList<MeshSelection> Selections, IReadOnlyList<NestedCatalog> NestedCatalogs, IReadOnlyList<SourceExtent> SourceExtents);
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
    }
}
