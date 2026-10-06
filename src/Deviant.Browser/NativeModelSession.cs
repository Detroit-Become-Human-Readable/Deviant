using System.Security.Cryptography;
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
    internal NativeModelBatch(IReadOnlyDictionary<string, RenderMesh> meshes,
        IReadOnlyDictionary<ResourceIdentity, RenderMaterial> materials, IReadOnlyList<RenderIssue> issues,
        IReadOnlyList<SourceExtent> sourceExtents, List<IDisposable> resources)
    { Meshes = meshes; Materials = materials; Issues = issues; SourceExtents = sourceExtents; this.resources = resources; }
    public IReadOnlyDictionary<string, RenderMesh> Meshes { get; }
    public IReadOnlyDictionary<ResourceIdentity, RenderMaterial> Materials { get; }
    public IReadOnlyList<RenderIssue> Issues { get; }
    public IReadOnlyList<SourceExtent> SourceExtents { get; }
    public void Dispose()
    {
        var released = Interlocked.Exchange(ref resources, null);
        if (released is not null)
            for (var index = released.Count - 1; index >= 0; index--) released[index].Dispose();
    }
}

/// <summary>Loads native meshes and their supported materials for either scene or model views.</summary>
public sealed class NativeModelSession
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

    public async Task<NativeModelBatch> LoadAsync(NativeContainerLease container, ResourceIdentity catalogIdentity,
        IReadOnlyList<NativeModelSelection> selections, IProgress<RenderLoadProgress>? progress = null,
        CancellationToken token = default, int maximumPreviewDimension = 1024,
        int maximumSelections = MaximumSelections)
    {
        ArgumentNullException.ThrowIfNull(container); ArgumentNullException.ThrowIfNull(selections);
        if (maximumPreviewDimension is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumPreviewDimension));
        token.ThrowIfCancellationRequested(); container.EnsureUnchanged();
        if (container.Revision != Revision || !ReferenceEquals(container.MemoryBudget, MemoryBudget) ||
            catalogIdentity.IndexContentId != Revision.IndexContentId || catalogIdentity.MemberPath is not null)
            throw new ArchiveException("SourceChanged", "The model belongs to another asset session.");
        if (Snapshot.Records.SingleOrDefault(row => row.Ordinal == catalogIdentity.Ordinal) is not
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
        var extents = new List<SourceExtent> { container.StoredSource.Extent };
        var resolver = new NativeReferenceResolver(container.Directory);
        try
        {
            var grouped = selections.Distinct().GroupBy(selection => selection.MeshIdentity).ToArray();
            var completed = 0;
            foreach (var group in grouped)
            {
                token.ThrowIfCancellationRequested();
                var frame = container.Directory.Objects.SingleOrDefault(item => item.Identity == group.Key && item.NativeType == 2130)
                    ?? throw new ArchiveException("NativeMeshMissing", "The mesh metadata is unavailable.");
                if (!new NativeMeshDataReader().TryRead(frame, out var metadata, out var diagnostic, token))
                    throw new ArchiveException("NativeMeshMetadata", diagnostic ?? "The mesh metadata is unavailable.");
                using var gpu = NativeGpuReader.Read(container, frame.Identity, token);
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
                    var geometry = new NativeMeshGeometryDecoder().Decode(metadata, gpu.Decoded.Value!.DecodedPrefix,
                        selection.GroupIndex, token, new(frame.Identity, 0, gpu.Decoded.Value.ActualDecodedLength));
                    var validation = GeometryValidator.Validate(geometry);
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
                    var mesh = new RenderMesh(frame.Identity, selection.GroupIndex, geometry, bindings);
                    meshes.Add(mesh.Key, mesh);
                }
                progress?.Report(new("Geometry", ++completed, grouped.Length, MemoryBudget.UsedBytes));
            }

            var shaderIdentities = meshes.Values.SelectMany(mesh => mesh.MaterialsByPrimitive!.Values).Distinct().ToArray();
            completed = 0;
            foreach (var identity in shaderIdentities)
            {
                token.ThrowIfCancellationRequested();
                var frame = container.Directory.Objects.SingleOrDefault(item => item.Identity == identity && item.NativeType == 2133);
                MaterialAsset material;
                if (frame is null)
                {
                    material = new(identity, Revision, "neutral", [], [], ["MaterialUnavailable"]);
                }
                else
                {
                    try
                    {
                        using var package = NativeGpuReader.Read(container, identity, token);
                        extents.Add(package.PhysicalExtent);
                        // Stored source text, tokenizer tokens, and bounded expression graphs are temporary.
                        using var scratch = MemoryBudget.Reserve(checked(package.Decoded.Value!.DecodedPrefix.Length * 12L + 1024 * 1024), "material analysis");
                        var scan = StoredGlslReader.Scan(package.Decoded, token);
                        material = new NativeMaterialReader { UseSerializedScalarInitialValues = true }.Parse(frame, Revision, scan, token).Value
                            ?? new(identity, Revision, "neutral", [], [], ["MaterialUnavailable"]);
                    }
                    catch (Exception error) when (error is IOException or ArgumentException or OverflowException)
                    {
                        material = NeutralMaterial(frame, error is ArchiveException archive ? archive.Code : "MaterialUnavailable", token);
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
                        try
                        {
                            surface = await ReadSurfaceAsync(container, catalogIdentity, nativeTexture, resources, extents,
                                maximumPreviewDimension, token).ConfigureAwait(false);
                            surfaces.Add(nativeTexture, surface);
                        }
                        catch (Exception error) when (error is IOException or ArgumentException or OverflowException)
                        { issues.Add(new(nativeTexture, error is TextureFormatException format ? format.Code : error is ArchiveException archive ? archive.Code : "MaterialTextureUnavailable", error.Message)); continue; }
                    }
                    if (material.Bindings.Any(binding => binding.SlotOrdinal == request.Key && binding.Role == MaterialTextureRole.Normal) && surface.Surface.IsSrgb)
                    { issues.Add(new(nativeTexture, "MaterialNormalColorSpace", "The normal texture uses an incompatible color space.")); continue; }
                    mapped.Add(request.Key, surface);
                }
                materials.Add(identity, new(material, mapped));
                progress?.Report(new("Materials", ++completed, shaderIdentities.Length, MemoryBudget.UsedBytes));
            }
            container.EnsureUnchanged(); token.ThrowIfCancellationRequested();
            return new(meshes, materials, issues, extents.Distinct().ToArray(), resources);
        }
        catch
        {
            for (var index = resources.Count - 1; index >= 0; index--) resources[index].Dispose();
            throw;
        }
    }

    private async Task<RenderTexture> ReadSurfaceAsync(NativeContainerLease container, ResourceIdentity catalog,
        ResourceIdentity identity, List<IDisposable> retained, List<SourceExtent> extents, int maximumPreviewDimension,
        CancellationToken token)
    {
        var estimate = await textures.EstimateNativeSourceAsync(identity, catalog, container, token).ConfigureAwait(false);
        using var sourceMemory = MemoryBudget.Reserve(estimate.RetainedSourceBytes, "texture source");
        var read = await textures.ReadForNativeAsync(identity, catalog, container, token).ConfigureAwait(false);
        var source = read.Source ?? throw new TextureFormatException("MaterialTextureUnavailable", read.Reason ?? "The material texture is unavailable.");
        sourceMemory.ReduceTo(source.RetainedSourceBytes);
        var nativeDecoder = new NativeTextureDecoder();
        using var decodeScratch = MemoryBudget.Reserve(nativeDecoder.EstimateWorkingSetBytes(source, maximumPreviewDimension), "texture decoding");
        var decoded = nativeDecoder.ReadDetailed(source, token, maximumPreviewDimension);
        var document = decoded.Document;
        decodeScratch.ReduceTo(document.OriginalBytes.Length);
        var pixelDecoder = new DdsTextureDecoder(MemoryBudget.MaximumBytes);
        var mip = document.MipLevels.FirstOrDefault(level => level.Width <= maximumPreviewDimension && level.Height <= maximumPreviewDimension)
            ?? document.MipLevels[^1];
        var selection = document.DefaultSelection with { MipIndex = mip.Index };
        var resize = mip.Width > maximumPreviewDimension || mip.Height > maximumPreviewDimension;
        var resizeBytes = resize ? checked((long)maximumPreviewDimension * maximumPreviewDimension * 4) : 0;
        var pixelMemory = MemoryBudget.Reserve(checked(pixelDecoder.EstimateWorkingSetBytes(document, selection) + resizeBytes + 4096), "texture pixels");
        try
        {
            var pixels = await pixelDecoder.DecodeSubresourceAsync(document, selection, token).ConfigureAwait(false);
            if (pixels.IsHdr || pixels.Rgba8.Length != checked(pixels.Width * pixels.Height * 4))
                throw new TextureFormatException("MaterialSurfaceFormat", "The material texture surface is unavailable.");
            if (resize)
            {
                token.ThrowIfCancellationRequested();
                pixels = pixelDecoder.CreateThumbnail(pixels, maximumPreviewDimension);
            }
            pixelMemory.ReduceTo(pixels.Rgba8.Length + 4096L);
            using (MemoryBudget.Reserve(NativeTextureSession.MaximumResourceBytes, "texture validation"))
                await textures.RevalidateAsync(source, token).ConfigureAwait(false);
            extents.AddRange(source.Payloads.Select(payload => payload.Extent));
            token.ThrowIfCancellationRequested();
            var texture = new RenderTexture(identity, pixels,
                Convert.ToHexString(SHA256.HashData(pixels.Rgba8.Span)).ToLowerInvariant(), source.SelectedIdentity, catalog)
                { PreviewWasResized = resize };
            retained.Add(pixelMemory);
            return texture;
        }
        catch { pixelMemory.Dispose(); throw; }
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
}
