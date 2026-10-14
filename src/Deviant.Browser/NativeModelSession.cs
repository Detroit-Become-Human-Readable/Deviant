using System.Numerics;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Models;
using Deviant.Formats.Shaders;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

public sealed record NativeModelSelection(ResourceIdentity MeshIdentity, int GroupIndex);

public sealed class NativeModelBatch : IDisposable
{
    private List<IDisposable>? resources;
    private readonly object gate = new();
    private int references = 1;
    private bool ownerDisposed;
    internal NativeModelBatch(IReadOnlyDictionary<string, RenderMesh> meshes,
        IReadOnlyDictionary<ResourceIdentity, RenderMaterial> materials, IReadOnlyList<RenderIssue> issues,
        IReadOnlyList<SourceExtent> sourceExtents, List<IDisposable> resources)
    { Meshes = meshes; Materials = materials; Issues = issues; SourceExtents = sourceExtents; this.resources = resources; }
    public IReadOnlyDictionary<string, RenderMesh> Meshes { get; }
    public IReadOnlyDictionary<ResourceIdentity, RenderMaterial> Materials { get; }
    public IReadOnlyList<RenderIssue> Issues { get; }
    public IReadOnlyList<SourceExtent> SourceExtents { get; }
    internal NativeModelSession? GeometryPreparationSession { get; init; }
    internal SourceRevision? GeometryPreparationRevision { get; init; }
    internal ResourceIdentity? GeometryPreparationContainer { get; init; }
    internal ResourceIdentity? GeometryPreparationCatalog { get; init; }
    public void Dispose()
    {
        lock (gate) { if (ownerDisposed) return; ownerDisposed = true; }
        Release();
    }
    public IDisposable AcquireLease()
    { lock (gate) { ObjectDisposedException.ThrowIf(resources is null, this); references++; return new BatchLease(this); } }
    private void Release()
    {
        List<IDisposable>? released;
        lock (gate) { if (--references != 0) return; released = resources; resources = null; }
        if (released is not null)
            for (var index = released.Count - 1; index >= 0; index--) released[index].Dispose();
    }
    private sealed class BatchLease(NativeModelBatch owner) : IDisposable
    { private NativeModelBatch? owner = owner; public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(); }
}

/// <summary>Loads native meshes and their supported materials for either scene or model views.</summary>
public sealed partial class NativeModelSession
{
    public const int MaximumSelections = 8192;
    public const int MaximumLargeSelections = 65_536;
    private readonly NativeTextureSession textures;
    public NativeModelSession(NativeTextureSession textures, AssetMemoryBudget budget)
    { this.textures = textures ?? throw new ArgumentNullException(nameof(textures)); MemoryBudget = budget ?? throw new ArgumentNullException(nameof(budget)); }
    public ArchiveSnapshot Snapshot => textures.Snapshot;
    public SourceRevision Revision => textures.Revision;
    public AssetMemoryBudget MemoryBudget { get; }
    public NativeTextureSession Textures => textures;

    public Task<NativeModelBatch> LoadAsync(NativeContainerLease container, ResourceIdentity catalogIdentity,
        IReadOnlyList<NativeModelSelection> selections, IProgress<RenderLoadProgress>? progress = null,
        CancellationToken token = default, int maximumPreviewDimension = 1024,
        int maximumSelections = MaximumSelections, NativeModelPoseInputs? poseInputs = null,
        bool retainSkinSource = false,
        NativeSkeletonBindingSession.NativeSkeletonBindingLease? bindRig = null,
        Func<NativeModelBatch, CancellationToken, Task>? geometryReady = null)
        => LoadCoreAsync(container, catalogIdentity, selections, progress, token, maximumPreviewDimension,
            maximumSelections, poseInputs, retainSkinSource, bindRig, geometryReady);

    private async Task<NativeModelBatch> LoadCoreAsync(NativeContainerLease container, ResourceIdentity catalogIdentity,
        IReadOnlyList<NativeModelSelection> selections, IProgress<RenderLoadProgress>? progress = null,
        CancellationToken token = default, int maximumPreviewDimension = 1024,
        int maximumSelections = MaximumSelections, NativeModelPoseInputs? poseInputs = null,
        bool retainSkinSource = false,
        NativeSkeletonBindingSession.NativeSkeletonBindingLease? bindRig = null,
        Func<NativeModelBatch, CancellationToken, Task>? geometryReady = null, bool geometryOnly = false)
    {
        ArgumentNullException.ThrowIfNull(container); ArgumentNullException.ThrowIfNull(selections);
        if (maximumPreviewDimension is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumPreviewDimension));
        token.ThrowIfCancellationRequested(); container.EnsureUnchanged();
        if (bindRig is not null && (poseInputs is not null || selections.Count != 1))
            throw new ArgumentException("A source bind rig requires one unposed mesh selection.", nameof(bindRig));
        retainSkinSource |= bindRig is not null;
        if (container.Revision != Revision || !ReferenceEquals(container.MemoryBudget, MemoryBudget) ||
            catalogIdentity.IndexContentId != Revision.IndexContentId || catalogIdentity.MemberPath is not null)
            throw new ArchiveException("SourceChanged", "The model belongs to another asset session.");
        if (Snapshot.FindRecord(catalogIdentity.Ordinal) is not
            { Type: 3, Validity: ResourceValidity.Valid })
            throw new ArchiveException("NativeCatalogUnavailable", "The model catalog is unavailable.");
        if (maximumSelections is < 1 or > MaximumLargeSelections) throw new ArgumentOutOfRangeException(nameof(maximumSelections));
        if (selections.Count > maximumSelections) throw new ArchiveException("ModelSelectionLimit", "Too many mesh groups were selected.");
        container.ReleaseCompressionBuffers();
        var resources = new List<IDisposable>();
        var meshes = new Dictionary<string, RenderMesh>(StringComparer.Ordinal);
        var materials = new Dictionary<ResourceIdentity, RenderMaterial>();
        var surfaces = new Dictionary<ResourceIdentity, RenderTexture>();
        var issues = new List<RenderIssue>();
        var extents = container.SourceExtents.ToList();
        NativeModelBatch? published = null;
        var resolver = new NativeReferenceResolver(container.Directory);
        var framesByIdentity = container.Directory.Objects.ToDictionary(frame => frame.Identity);
        var detachSceneGeometry = geometryOnly && !retainSkinSource && poseInputs is null && bindRig is null;
        using var temporaryContainerLease = detachSceneGeometry ? container.AcquireLease() : null;
        try
        {
            // Keep all verified package-range receipts alive for as long as a model batch exposes them.
            if (!detachSceneGeometry) resources.Add(container.AcquireLease());
            using var poseRows = poseInputs is null ? null : ReadPoseRows();
            using var materialPoseScratch = poseInputs?.Pose.Package.MaterialWorldTransform is not null || bindRig is not null
                ? MemoryBudget.Reserve(checked((poseInputs?.Pose.Package.Joints.Count ?? bindRig!.Skeleton.Matrices.Count) * 512L + 4096), "material pose positions") : null;
            var materialJointPositions = poseInputs is null ? null :
                NativeModelPosePackageReader.CreateMaterialWorldPositions(poseInputs.Pose.Package, token);
            var grouped = selections.Distinct().GroupBy(selection => selection.MeshIdentity).ToArray();
            var completed = 0;
            foreach (var group in grouped)
            {
                using var geometryTiming = AssetLoadDiagnostics.Measure("GeometryDecode", group.Key.Key);
                token.ThrowIfCancellationRequested();
                var frame = framesByIdentity.GetValueOrDefault(group.Key) is { NativeType: 2130 } meshFrame ? meshFrame : null;
                frame = frame
                    ?? throw new ArchiveException("NativeMeshMissing", "The mesh metadata is unavailable.");
                if (!new NativeMeshDataReader().TryRead(frame, out var metadata, out var diagnostic, token))
                    throw new ArchiveException("NativeMeshMetadata", diagnostic ?? "The mesh metadata is unavailable.");
                using var gpu = await NativeGpuReader.ReadAsync(container, frame.Identity, token).ConfigureAwait(false);
                extents.Add(gpu.PhysicalExtent);
                foreach (var selection in group)
                {
                    token.ThrowIfCancellationRequested();
                    if (selection.GroupIndex < 0 || selection.GroupIndex >= metadata!.Groups.Count)
                        throw new ArchiveException("NativeMeshGroup", "The mesh group is unavailable.");
                    var parts = metadata.Groups[selection.GroupIndex].Parts;
                    var estimate = checked(parts.Sum(part => (long)part.VertexCount * 128 + (long)part.IndexCount * 4 + 4096));
                    var retained = MemoryBudget.Reserve(estimate, "decoded geometry");
                    resources.Add(retained);
                    var gpuExtent = new DecodedExtent(frame.Identity, 0, gpu.Decoded.Value!.ActualDecodedLength);
                    NativeSkinningAttributeLease? skinAttributes = null;
                    if (poseInputs is not null || retainSkinSource)
                    {
                        skinAttributes = new NativeSkinningAttributeDecoder().Decode(metadata, gpu.Decoded.Value.DecodedPrefix,
                            selection.GroupIndex, MemoryBudget.Reserve, gpuExtent: gpuExtent, token: token);
                        resources.Add(skinAttributes);
                    }
                    var decoder = new NativeMeshGeometryDecoder();
                    var geometry = skinAttributes is null
                        ? decoder.Decode(metadata, gpu.Decoded.Value.DecodedPrefix, selection.GroupIndex, token, gpuExtent)
                        : decoder.DecodeSkinSource(metadata, gpu.Decoded.Value.DecodedPrefix, selection.GroupIndex,
                            skinAttributes.Document, token, gpuExtent);
                    var validation = GeometryValidator.Validate(geometry, token);
                    if (!validation.IsValid)
                        throw new ArchiveException("NativeMeshValidation", string.Join("; ", validation.Diagnostics.Where(item => item.Severity == GeometryDiagnosticSeverity.Error).Select(item => item.Code)));
                    retained.ReduceTo(checked(geometry.Primitives.Sum(primitive =>
                        primitive.Positions.Count * 12L + primitive.Indices.Count * 4L +
                        (primitive.Normals?.Count ?? 0) * 12L + (primitive.Tangents?.Count ?? 0) * 16L +
                        (primitive.Colors?.Count ?? 0) * 16L +
                        (primitive.TextureCoordinates?.Sum(set => set.Count * 8L) ?? 0) + 4096L)));
                    var bindings = new Dictionary<string, ResourceIdentity>(StringComparer.Ordinal);
                    for (var index = 0; index < parts.Count; index++)
                    {
                        var resolved = resolver.Resolve(parts[index].Material);
                        if (resolved is { Status: ReferenceStatus.Unique, Candidates.Count: 1 }) bindings.Add(geometry.Primitives[index].Key, resolved.Candidates[0]);
                        else issues.Add(new(frame.Identity, "MaterialReference", "A material reference is missing or ambiguous."));
                    }
                    var sourceGeometry = geometry;
                    if (skinAttributes is not null && poseRows is not null)
                        geometry = NativeModelPoseProjection.Apply(sourceGeometry, metadata, gpu.Decoded.Value.DecodedPrefix,
                            skinAttributes.Document, poseRows!, Revision, MemoryBudget, resources, token);
                    var mesh = new RenderMesh(frame.Identity, selection.GroupIndex, geometry, bindings)
                    {
                        CatalogIdentity = catalogIdentity,
                        Pose = poseInputs?.Identity,
                        SkinSourceGeometry = skinAttributes is null ? null : sourceGeometry,
                        SkinSourceMetadata = skinAttributes is null ? null : metadata,
                        SkinAttributes = skinAttributes?.Document
                    };
                    meshes.Add(mesh.Key, mesh);
                }
                progress?.Report(new("Geometry", ++completed, grouped.Length, MemoryBudget.UsedBytes));
            }

            if (geometryOnly)
            {
                if (detachSceneGeometry)
                    await RetainSceneSourceReceiptsAsync(container, extents, resources, token).ConfigureAwait(false);
                container.EnsureUnchanged(); token.ThrowIfCancellationRequested();
                return new NativeModelBatch(meshes, materials, issues.ToArray(), extents.Distinct().ToArray(), resources)
                {
                    GeometryPreparationSession = this, GeometryPreparationRevision = Revision,
                    GeometryPreparationContainer = container.Identity, GeometryPreparationCatalog = catalogIdentity
                };
            }

            if (geometryReady is not null)
            {
                container.EnsureUnchanged();
                published = new(meshes, materials, issues, extents, resources);
                await geometryReady(published, token).ConfigureAwait(false);
            }
            if (bindRig is not null)
            {
                // Model-local occurrences use an explicit identity placement. Evaluate
                // instance expressions from that same source bind frame.
                using var bind = NativeModelBindFbxInputs.Create(container, meshes.Values.Single(), bindRig, MemoryBudget, token);
                materialJointPositions = bind.Skeleton.Joints.Select(joint =>
                {
                    var matrix = joint.BindWorld.Elements;
                    var position = new Vector3((float)matrix[12], (float)matrix[13], (float)matrix[14]);
                    if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                        throw new InvalidDataException("BindMaterialWorldInvalid");
                    return position;
                }).ToArray();
            }
            await PrepareMaterialsAsync(container, catalogIdentity, meshes, materials, surfaces, issues, extents,
                resources, framesByIdentity, materialJointPositions, progress, maximumPreviewDimension, token).ConfigureAwait(false);
            container.EnsureUnchanged(); token.ThrowIfCancellationRequested();
            poseInputs?.Pose.EnsureUnchanged(); poseInputs?.Rig.EnsureUnchanged();
            return published ?? new(meshes, materials, issues, extents.Distinct().ToArray(), resources);

            NativeSkinningPoseRowLease ReadPoseRows()
            {
                poseInputs!.Validate(container, Revision, selections);
                var package = poseInputs.Pose.Package;
                return new NativeSkinningPoseRowsReader().Read(package.PoseSource, package.MatrixRows.Span,
                    package.PoseSource.MeshIdentity, package.PoseSource.RigIdentity, Revision,
                    package.PoseSource.SnapshotSequence, package.Joints.Count, MemoryBudget.Reserve, token: token);
            }
        }
        catch
        {
            if (published is not null) published.Dispose();
            else for (var index = resources.Count - 1; index >= 0; index--) resources[index].Dispose();
            throw;
        }
    }

    private async Task RetainSceneSourceReceiptsAsync(NativeContainerLease container, List<SourceExtent> extents,
        List<IDisposable> resources, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); container.EnsureUnchanged();
        var consumed = extents.Concat(container.SourceExtents)
            .DistinctBy(extent => (extent.RelativeSource, extent.Offset, extent.Length, extent.PayloadSha256)).ToArray();
        if (consumed.Length > SourceExtentVerifier.MaximumLargeExtentCount)
            throw new ArchiveException("SceneVerificationLimit", "Scene source verification exceeds its extent limit.");
        long consumedBytes = 0;
        foreach (var extent in consumed)
        {
            token.ThrowIfCancellationRequested();
            if (extent.Length < 0 || extent.Length > SourceExtentVerifier.MaximumBytes - consumedBytes)
                throw new ArchiveException("SceneVerificationLimit", "Scene source verification exceeds its byte limit.");
            consumedBytes += extent.Length;
        }
        AssetMemoryBudget.Reservation? retained = MemoryBudget.Reserve(
            checked(4096L + consumed.Length * 512L), "model source receipts");
        SourceExtentVerificationLease? receipt = null;
        try
        {
            using var scratch = MemoryBudget.Reserve(256 * 1024, "model source verification scratch");
            receipt = await new SourceExtentVerifier().AcquireAsync(Snapshot, consumed, token,
                maximumBytes: Math.Max(1, consumedBytes), maximumExtentCount: Math.Max(1, consumed.Length)).ConfigureAwait(false);
            receipt.EnsureUnchanged(); container.EnsureUnchanged(); token.ThrowIfCancellationRequested();
            extents.Clear(); extents.AddRange(consumed);
            resources.Add(retained); retained = null;
            resources.Add(receipt); receipt = null;
        }
        finally { receipt?.Dispose(); retained?.Dispose(); }
    }

    /// <summary>Loads selected native mesh groups into a model-local snapshot without scene placement facts.</summary>
    public Task<RenderAssetSnapshot> LoadPosedSnapshotAsync(NativeContainerLease container, ResourceIdentity catalogIdentity,
        NativeModelSelection selection, int serializedLodOrdinal, NativeModelPoseInputs poseInputs,
        IProgress<RenderLoadProgress>? progress = null, CancellationToken token = default,
        int maximumPreviewDimension = 1024)
    {
        ArgumentNullException.ThrowIfNull(poseInputs); ArgumentNullException.ThrowIfNull(selection);
        if (poseInputs.Pose.Package.SerializedLodOrdinal != serializedLodOrdinal)
            throw new ArchiveException("NativePoseLodMismatch", "The imported pose belongs to another selected detail.");
        return LoadSnapshotAsync(container, catalogIdentity, [selection], progress, token,
            maximumPreviewDimension, poseInputs: poseInputs);
    }

    /// <summary>Loads selected native mesh groups into a model-local snapshot without scene placement facts.</summary>
    public async Task<RenderAssetSnapshot> LoadSnapshotAsync(NativeContainerLease container, ResourceIdentity catalogIdentity,
        IReadOnlyList<NativeModelSelection> selections, IProgress<RenderLoadProgress>? progress = null,
        CancellationToken token = default, int maximumPreviewDimension = 1024,
        int maximumSelections = MaximumSelections, NativeModelPoseInputs? poseInputs = null,
        bool retainSkinSource = false,
        NativeSkeletonBindingSession.NativeSkeletonBindingLease? bindRig = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(catalogIdentity);
        ArgumentNullException.ThrowIfNull(selections);
        token.ThrowIfCancellationRequested();
        if (maximumSelections is < 1 or > MaximumLargeSelections) throw new ArgumentOutOfRangeException(nameof(maximumSelections));
        if (selections.Count > maximumSelections)
            throw new ArchiveException("ModelSelectionLimit", "Too many mesh groups were selected.");
        var requested = selections.Distinct().ToArray();
        if (requested.Length == 0)
            throw new ArchiveException("ModelSelectionEmpty", "Select at least one native mesh group.");

        NativeModelBatch? batch = await LoadAsync(container, catalogIdentity, requested, progress, token,
            maximumPreviewDimension, maximumSelections, poseInputs, retainSkinSource, bindRig).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            container.EnsureUnchanged();
            if (batch.Meshes.Count != requested.Length)
                throw new ArchiveException("ModelSelectionMismatch", "The loaded mesh groups do not match the selection.");

            var boundsByKey = new Dictionary<string, GeometryBounds>(StringComparer.Ordinal);
            foreach (var selection in requested)
            {
                token.ThrowIfCancellationRequested();
                var key = $"{selection.MeshIdentity.Key}/group/{selection.GroupIndex}";
                if (!batch.Meshes.TryGetValue(key, out var mesh) || mesh.Identity != selection.MeshIdentity ||
                    mesh.GroupIndex != selection.GroupIndex)
                    throw new ArchiveException("ModelSelectionMismatch", "The loaded mesh groups do not match the selection.");
                var validation = GeometryValidator.Validate(mesh.Geometry, token);
                if (!validation.IsValid)
                    throw new ArchiveException("NativeMeshValidation", string.Join("; ", validation.Diagnostics
                        .Where(item => item.Severity == GeometryDiagnosticSeverity.Error).Select(item => item.Code)));
                boundsByKey.Add(key, ModelBounds(mesh.Geometry, token));
            }

            var minimum = new Vector3(float.PositiveInfinity);
            var maximum = new Vector3(float.NegativeInfinity);
            var occurrences = new RenderOccurrence[requested.Length];
            var renderedParts = 0;
            for (var index = 0; index < requested.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var selection = requested[index];
                var key = $"{selection.MeshIdentity.Key}/group/{selection.GroupIndex}";
                var mesh = batch.Meshes[key];
                var bounds = boundsByKey[key];
                minimum = Vector3.Min(minimum, bounds.Minimum);
                maximum = Vector3.Max(maximum, bounds.Maximum);
                renderedParts = checked(renderedParts + mesh.Geometry.Primitives.Count);
                var occurrenceKey = $"model-local/{key}";
                occurrences[index] = new(occurrenceKey, mesh.Identity, mesh.Identity,
                    $"Mesh {selection.MeshIdentity.Ordinal} · group {selection.GroupIndex}", mesh.Key,
                    Matrix4x4.Identity, bounds, [])
                {
                    PlacementOrigin = RenderPlacementOrigin.ModelLocal,
                    ModelPlacement = new(mesh.Identity, mesh.GroupIndex),
                    SourceVisibilityKnown = false,
                    SourceVisible = true
                };
            }

            token.ThrowIfCancellationRequested();
            container.EnsureUnchanged();
            var snapshot = new RenderAssetSnapshot(Revision, batch.Meshes, occurrences,
                batch.Materials, new(minimum, maximum), batch.Issues, [],
                new(0, 0, 0, renderedParts, 1, 0), batch.SourceExtents, MemoryBudget, [batch]);
            batch = null;
            return snapshot;
        }
        finally { batch?.Dispose(); }
    }

    private static GeometryBounds ModelBounds(GeometryAsset geometry, CancellationToken token)
    {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var found = false;
        foreach (var primitive in geometry.Primitives)
        foreach (var position in primitive.Positions)
        {
            token.ThrowIfCancellationRequested();
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                throw new ArchiveException("NativeMeshValidation", "The selected model contains a non-finite position.");
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
            found = true;
        }
        if (!found || !Finite(minimum) || !Finite(maximum))
            throw new ArchiveException("NativeMeshEmpty", "The selected model has no finite positions.");
        return new(minimum, maximum);

        static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private async Task<RenderTexture> ReadSurfaceAsync(NativeContainerLease container, ResourceIdentity catalog,
        ResourceIdentity identity, List<IDisposable> retained, List<SourceExtent> extents, int maximumPreviewDimension,
        CancellationToken token)
    {
        using var timing = AssetLoadDiagnostics.Measure("TextureReadDecode", identity.Key);
        var estimate = await textures.EstimateNativeSourceAsync(identity, catalog, container, maximumPreviewDimension, token).ConfigureAwait(false);
        var sourceMemory = MemoryBudget.Reserve(estimate.RetainedSourceBytes, "texture source");
        IDisposable? producerContainerLease = null;
        var producerOwnsSource = 0;
        try
        {
            var read = await textures.ReadForNativeAsync(identity, catalog, container, token,
                maximumPreviewDimension: maximumPreviewDimension).ConfigureAwait(false);
            var source = read.Source ?? throw new TextureFormatException("MaterialTextureUnavailable",
                read.Reason ?? "The material texture is unavailable.");
            sourceMemory.ReduceTo(source.RetainedSourceBytes);
            var key = CreateSurfacePreparationKey(source, maximumPreviewDimension);
            producerContainerLease = container.AcquireLease();
            Func<CancellationToken, Task<SharedSurfacePixels>> factory = sharedToken =>
            {
                Interlocked.Exchange(ref producerOwnsSource, 1);
                try
                {
                    return Task.Run(async () =>
                    {
                        try
                        {
                            return await DecodeSharedSurfaceAsync(source, container, maximumPreviewDimension, sharedToken)
                                .ConfigureAwait(false);
                        }
                        finally
                        {
                            sourceMemory.Dispose();
                            producerContainerLease!.Dispose();
                        }
                    }, CancellationToken.None);
                }
                catch
                {
                    sourceMemory.Dispose();
                    producerContainerLease!.Dispose();
                    throw;
                }
            };
            var sharedLease = await surfaceCache.AcquireAsync(key, factory, token).ConfigureAwait(false);
            try
            {
                container.EnsureUnchanged();
                using (MemoryBudget.Reserve(NativeTextureSession.MaximumResourceBytes, "texture validation"))
                    await textures.RevalidateAsync(source, token).ConfigureAwait(false);
                container.EnsureUnchanged();
                extents.AddRange(source.Payloads.Select(payload => payload.Extent));
                token.ThrowIfCancellationRequested();
                var pixels = sharedLease.Value;
                var surface = pixels.EncodedDds is { } encoded
                    ? TextureSurface.RestoreEncodedDds(pixels.Width, pixels.Height, encoded, pixels.SourceIsHdr,
                        pixels.IsSrgb, pixels.MipIndex, pixels.SourceFormat, pixels.SourceFormatName,
                        pixels.SourcePayloadSha256, pixels.Subresource, identity, source.Revision, pixels.EncodedSelection)
                    : TextureSurface.RestoreRgba8(pixels.Width, pixels.Height, pixels.Rgba8,
                        pixels.SourceIsHdr, pixels.IsSrgb, pixels.MipIndex, pixels.SourceFormat,
                        pixels.SourceFormatName, pixels.SourcePayloadSha256, pixels.Subresource,
                        identity, source.Revision, pixels.DisplayTransform);
                var texture = new RenderTexture(identity, surface, pixels.RgbaSha256, source.SelectedIdentity, catalog)
                { PreviewWasResized = pixels.PreviewWasResized, Sampling = TextureFileTextSamplingReader.Read(source.Metadata) };
                retained.Add(sharedLease);
                sharedLease = null!;
                return texture;
            }
            finally { sharedLease?.Dispose(); }
        }
        finally
        {
            if (Volatile.Read(ref producerOwnsSource) == 0)
            {
                sourceMemory.Dispose();
                producerContainerLease?.Dispose();
            }
        }
    }

    private MaterialAsset NeutralMaterial(NativeObjectFrame frame, string diagnostic, CancellationToken token)
    {
        var adapter = new TextureContainerObjectFrame(frame.Identity, frame.DirectoryIndex, frame.NativeType,
            frame.NativeId, frame.DirectoryEntryExtent, frame.HeaderExtent, frame.PayloadExtent, frame.RawHeader,
            frame.RawPayload, frame.WrapperFlag, frame.WrapperCount, frame.WrapperKey, frame.ClassCount);
        var slots = new TextureUsageReader().ParseShaderSlots(adapter, Revision, [], token).Value;
        return new(frame.Identity, Revision, "neutral", [], slots?.Slots.Select(slot =>
            new ResourceReference(IdentifierNamespace.Native, slot.Reference.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), frame.Identity, slot.Reference.Type)).ToArray() ?? [], [diagnostic]);
    }

    private static long EstimateMaterialBytes(MaterialAsset material)
    {
        var bytes = checked(4096L + material.References.Count * 512L + material.Bindings.Count * 1024L);
        if (material.SurfaceGraph is not { } graph) return bytes;
        var seen = new HashSet<MaterialExpression>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<MaterialExpression>();
        if (graph.BaseColor is { } color) pending.Push(color);
        if (graph.Opacity is { } opacity) pending.Push(opacity);
        if (graph.AlphaCutoff is { } cutoff) pending.Push(cutoff);
        while (pending.TryPop(out var expression))
        {
            if (!seen.Add(expression)) continue;
            bytes = checked(bytes + 256 + expression.Arguments.Count * 16L +
                (expression.Constants?.Count ?? 0) * 4L + (expression.Components?.Length ?? 0) * 2L);
            foreach (var argument in expression.Arguments) pending.Push(argument);
        }
        return bytes;
    }
    private static NativeTextureDecoder CreateSourceLoadedTextureDecoder(NativeTextureSource source,
        int maximumPreviewDimension, CancellationToken token)
    {
        var plan = NativeTextureDecoder.PlanPreview(source, maximumPreviewDimension, token);
        if (!plan.IsConverted) return new NativeTextureDecoder();

        var decodedBytes = checked((long)plan.Width * plan.Height * 4);
        const long existingMaximumDecodedBytes = 128L * 1024 * 1024;
        if (decodedBytes > existingMaximumDecodedBytes)
            throw new TextureFormatException("NativeTextureDecodeBudget", "Mip data exceeds the decode budget.");
        return new NativeTextureDecoder(Math.Max(NativeTextureDecoder.DefaultMaximumDecodedBytes, decodedBytes));
    }
}
