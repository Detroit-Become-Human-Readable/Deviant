using System.Numerics;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Shaders;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

public sealed partial class NativeModelSession
{
    /// <summary>Loads unposed scene geometry before preparing material textures.</summary>
    internal Task<NativeModelBatch> LoadGeometryAsync(NativeContainerLease container, ResourceIdentity catalogIdentity,
        IReadOnlyList<NativeModelSelection> selections, IProgress<RenderLoadProgress>? progress = null,
        CancellationToken token = default, int maximumPreviewDimension = 1024, int maximumSelections = MaximumSelections)
        => LoadCoreAsync(container, catalogIdentity, selections, progress, token, maximumPreviewDimension,
            maximumSelections, geometryOnly: true);

    /// <summary>Prepares a separate material batch sharing the selected, already decoded scene geometry.</summary>
    internal async Task<NativeModelBatch> LoadMaterialsAsync(NativeContainerLease container, ResourceIdentity catalogIdentity,
        NativeModelBatch geometryBatch, IProgress<RenderLoadProgress>? progress = null,
        CancellationToken token = default, int maximumPreviewDimension = 1024)
    {
        ArgumentNullException.ThrowIfNull(container); ArgumentNullException.ThrowIfNull(catalogIdentity);
        ArgumentNullException.ThrowIfNull(geometryBatch); token.ThrowIfCancellationRequested(); container.EnsureUnchanged();
        if (maximumPreviewDimension is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumPreviewDimension));
        if (container.Revision != Revision || !ReferenceEquals(container.MemoryBudget, MemoryBudget) ||
            catalogIdentity.IndexContentId != Revision.IndexContentId || catalogIdentity.MemberPath is not null ||
            !ReferenceEquals(geometryBatch.GeometryPreparationSession, this) || geometryBatch.GeometryPreparationRevision != Revision ||
            geometryBatch.GeometryPreparationContainer != container.Identity || geometryBatch.GeometryPreparationCatalog != catalogIdentity)
            throw new ArchiveException("SourceChanged", "The geometry belongs to another model preparation session.");
        if (Snapshot.FindRecord(catalogIdentity.Ordinal) is not { Type: 3, Validity: ResourceValidity.Valid })
            throw new ArchiveException("NativeCatalogUnavailable", "The model catalog is unavailable.");
        container.EnsureUnchanged();
        var frames = container.Directory.Objects.ToDictionary(frame => frame.Identity);
        foreach (var mesh in geometryBatch.Meshes.Values)
        {
            token.ThrowIfCancellationRequested();
            if (mesh.Identity.IndexContentId != Revision.IndexContentId || mesh.Identity.Ordinal != container.Identity.Ordinal ||
                mesh.CatalogIdentity != catalogIdentity || mesh.Pose is not null || mesh.SkinSourceGeometry is not null ||
                !frames.TryGetValue(mesh.Identity, out var frame) || frame.NativeType != 2130)
                throw new ArchiveException("SourceChanged", "The geometry does not match the selected native container and catalog.");
        }
        var resources = new List<IDisposable>();
        try
        {
            resources.Add(geometryBatch.AcquireLease());
            var materials = new Dictionary<ResourceIdentity, RenderMaterial>();
            var surfaces = new Dictionary<ResourceIdentity, RenderTexture>();
            var issues = geometryBatch.Issues.ToList(); var extents = geometryBatch.SourceExtents.ToList();
            await PrepareMaterialsAsync(container, catalogIdentity, geometryBatch.Meshes, materials, surfaces, issues, extents,
                resources, frames, null, progress, maximumPreviewDimension, token).ConfigureAwait(false);
            await RetainSceneSourceReceiptsAsync(container, extents, resources, token).ConfigureAwait(false);
            container.EnsureUnchanged(); token.ThrowIfCancellationRequested();
            return new(geometryBatch.Meshes, materials, issues.ToArray(), extents.Distinct().ToArray(), resources);
        }
        catch { for (int index = resources.Count - 1; index >= 0; index--) resources[index].Dispose(); throw; }
    }

    private async Task PrepareMaterialsAsync(NativeContainerLease container, ResourceIdentity catalogIdentity,
        IReadOnlyDictionary<string, RenderMesh> meshes, Dictionary<ResourceIdentity, RenderMaterial> materials,
        Dictionary<ResourceIdentity, RenderTexture> surfaces, List<RenderIssue> issues, List<SourceExtent> extents,
        List<IDisposable> resources, IReadOnlyDictionary<ResourceIdentity, NativeObjectFrame> framesByIdentity,
        IReadOnlyList<Vector3>? materialJointPositions, IProgress<RenderLoadProgress>? progress,
        int maximumPreviewDimension, CancellationToken token)
    {
        var resolver = new NativeReferenceResolver(container.Directory);
        var shaderIdentities = meshes.Values.SelectMany(mesh => mesh.MaterialsByPrimitive!.Values).Distinct().ToArray();
        var completed = 0;
        foreach (var identity in shaderIdentities)
        {
            using var materialTiming = AssetLoadDiagnostics.Measure("MaterialDecode", identity.Key);
            token.ThrowIfCancellationRequested();
            var frame = framesByIdentity.GetValueOrDefault(identity) is { NativeType: 2133 } materialFrame ? materialFrame : null;
            MaterialAsset material;
            if (frame is null)
            {
                material = new(identity, Revision, "neutral", [], [], ["MaterialUnavailable"]);
                issues.Add(new(identity, "MaterialUnavailable", "The material source is unavailable."));
            }
            else
            {
                try
                {
                    using var package = await NativeGpuReader.ReadAsync(container, identity, token).ConfigureAwait(false);
                    extents.Add(package.PhysicalExtent);
                    // Stored source text, tokenizer tokens, and bounded expression graphs are temporary.
                    using var scratch = MemoryBudget.Reserve(checked(package.Decoded.Value!.DecodedPrefix.Length * 12L + 1024 * 1024), "material analysis");
                    var scan = StoredGlslReader.Scan(package.Decoded, token);
                    var programWorkingBytes = 0L;
                    var scanMatchesMaterial = scan.ContainerIdentity == frame.Identity && scan.Revision == Revision &&
                        scan.Diagnostics.Count == 0 && scan.Sources.Count <= NativeMaterialReader.MaximumSourceRegions &&
                        scan.Sources.All(source => source.Revision == Revision && source.DecodedExtent.Container == frame.Identity);
                    if (scanMatchesMaterial)
                    {
                        var adapter = new TextureContainerObjectFrame(frame.Identity, frame.DirectoryIndex, frame.NativeType,
                            frame.NativeId, frame.DirectoryEntryExtent, frame.HeaderExtent, frame.PayloadExtent, frame.RawHeader,
                            frame.RawPayload, frame.WrapperFlag, frame.WrapperCount, frame.WrapperKey, frame.ClassCount);
                        var slots = new TextureUsageReader().ParseShaderSlots(adapter, Revision, scan.SourceExtents, token).Value;
                        if (slots is not null)
                            _ = new NativeShaderProgramReader().TryEstimateWorkingBytes(frame, slots,
                                out programWorkingBytes, out _, token);
                    }
                    // The preflight estimate is retained even for malformed records; parsing may have decoded a valid prefix first.
                    using var programScratch = programWorkingBytes > 0
                        ? MemoryBudget.Reserve(programWorkingBytes, "material program records") : null;
                    var parsed = new NativeMaterialReader { UseSerializedScalarInitialValues = true,
                        ProjectionDepthNormalizationDistance = 10000f,
                        JointWorldPositions = materialJointPositions }.Parse(frame, Revision, scan, token).Value;
                    material = parsed ?? new(identity, Revision, "neutral", [], [], ["MaterialUnavailable"]);
                    if (parsed is null) issues.Add(new(identity, "MaterialUnavailable", "The material source could not be prepared."));
                }
                catch (Exception error) when (error is not ArchiveException { Code: "SourceChanged" } &&
                    (error is IOException or ArgumentException or OverflowException))
                {
                    WorkflowDiagnostics.Failure("Model.Material", error, identity.Key, partial: true);
                    var code = error is ArchiveException archive ? archive.Code : "MaterialUnavailable";
                    material = NeutralMaterial(frame, code, token);
                    issues.Add(new(identity, code, error.Message));
                }
            }
            resources.Add(MemoryBudget.Reserve(EstimateMaterialBytes(material), "material metadata"));
            var mapped = new Dictionary<int, RenderTexture>();
            var requestedTextures = material.Bindings.ToDictionary(binding => binding.SlotOrdinal, binding => binding.Texture);
            foreach (var slot in material.SurfaceGraph?.TextureSlots ?? [])
            {
                if (slot < 0 || slot >= material.References.Count)
                { issues.Add(new(identity, "MaterialTextureSlot", "A material texture slot is unavailable.")); continue; }
                requestedTextures.TryAdd(slot, material.References[slot]);
            }
            foreach (var request in requestedTextures)
            {
                token.ThrowIfCancellationRequested();
                var resolution = resolver.Resolve(request.Value);
                if (resolution is not { Status: ReferenceStatus.Unique, Candidates.Count: 1 })
                { issues.Add(new(identity, "MaterialTextureReference", "A material texture is missing or ambiguous.")); continue; }
                var nativeTexture = resolution.Candidates[0];
                if (!surfaces.TryGetValue(nativeTexture, out var surface))
                {
                    AssetLoadDiagnostics.Cache("TextureCache", nativeTexture.Key, false);
                    try
                    {
                        surface = await ReadSurfaceAsync(container, catalogIdentity, nativeTexture, resources, extents,
                            maximumPreviewDimension, token).ConfigureAwait(false);
                        surfaces.Add(nativeTexture, surface);
                    }
                    catch (Exception error) when (error is not ArchiveException { Code: "SourceChanged" } &&
                    (error is IOException or ArgumentException or OverflowException))
                    { WorkflowDiagnostics.Failure("Model.Texture", error, nativeTexture.Key, partial: true);
                        issues.Add(new(nativeTexture, error is TextureFormatException format ? format.Code : error is ArchiveException archive ? archive.Code : "MaterialTextureUnavailable", error.Message)); continue; }
                }
                else AssetLoadDiagnostics.Cache("TextureCache", nativeTexture.Key, true);
                if (material.Bindings.Any(binding => binding.SlotOrdinal == request.Key && binding.Role == MaterialTextureRole.Normal) && surface.Surface.IsSrgb)
                { issues.Add(new(nativeTexture, "MaterialNormalColorSpace", "The normal texture uses an incompatible color space.")); continue; }
                mapped.Add(request.Key, surface);
            }
            materials.Add(identity, new(material, mapped));
            progress?.Report(new("Materials", ++completed, shaderIdentities.Length, MemoryBudget.UsedBytes));
        }
    }
}
