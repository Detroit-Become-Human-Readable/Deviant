using Deviant.Assets;
using Deviant.Core;
using Deviant.Export.Models;
using Deviant.Export.Textures;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

public sealed record NativeFbxHdrConversion(float ExposureStops, TextureToneMapOperator ToneMap);

public sealed partial class NativeModelSession
{
    /// <summary>Reads original material textures through the owning session and retains them through FBX publication.</summary>
    public async Task<NativeModelFbxMaterialInputs> CreateFbxMaterialInputsAsync(NativeContainerLease container,
        ResourceIdentity catalogIdentity, RenderMesh mesh, IReadOnlyDictionary<ResourceIdentity, RenderMaterial> materials,
        CancellationToken token = default, FbxGeometryExportOptions? options = null, NativeFbxHdrConversion? hdrConversion = null,
        MaterialBakeCache? bakeCache = null, NativeModelExportPreparationCache? preparationCache = null)
    {
        ArgumentNullException.ThrowIfNull(container); ArgumentNullException.ThrowIfNull(catalogIdentity);
        ArgumentNullException.ThrowIfNull(mesh); ArgumentNullException.ThrowIfNull(materials);
        token.ThrowIfCancellationRequested(); container.EnsureUnchanged(); options ??= new();
        if (options.MaximumMaterials < 1 || options.MaximumMaterialReferences < 1 ||
            options.MaximumTextures < 1 || options.MaximumTexturePixels < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (container.Revision != Revision || !ReferenceEquals(container.MemoryBudget, MemoryBudget) ||
            mesh.Identity.IndexContentId != Revision.IndexContentId || mesh.Identity.Ordinal != container.Identity.Ordinal ||
            catalogIdentity.IndexContentId != Revision.IndexContentId || catalogIdentity.MemberPath is not null ||
            Snapshot.FindRecord(catalogIdentity.Ordinal)?.Type != 3 ||
            !container.Directory.Objects.Any(frame => frame.Identity == mesh.Identity && frame.NativeType == 2130))
            throw new ArchiveException("SourceChanged", "The material sources belong to another model session.");
        var geometry = mesh.SkinSourceGeometry ?? mesh.Geometry;
        var refs = geometry.Primitives.SelectMany(primitive => primitive.Submeshes).SelectMany(part => part.Materials).Distinct().ToArray();
        var resolver = new NativeReferenceResolver(container.Directory);
        var joined = refs.Select(reference => new FbxMaterialReferenceJoin(reference, resolver.Resolve(reference)))
            .Where(join => join.Resolution is { Status: ReferenceStatus.Unique, Candidates.Count: 1 }).ToArray();
        var selected = joined.Select(join => join.Resolution.Candidates[0]).Distinct().ToArray();
        if (selected.Length > options.MaximumMaterials || refs.Length > options.MaximumMaterialReferences)
            throw new ArchiveException("MaterialExportLimit", "The selected material count exceeds the export limit.");
        using var materialJobTiming = AssetLoadDiagnostics.Measure("ExportMaterialJob", mesh.Key);
        bool collectCounters = AssetLoadDiagnostics.CountersEnabled, materialJobCompleted = false;
        long texturePixels = 0, retainedTextureSourceBytes = 0; int slots = 0;
        var result = new List<FbxMaterialExportInput>();
        var retained = new List<IDisposable>();
        try
        {
            retained.Add(container.AcquireLease());
            var staging = new FbxMaterialMapStaging(surfaceDiskCache); retained.Add(staging);
            retained.Add(MemoryBudget.Reserve(checked(4096L + refs.Length * 512L + selected.Length * 2048L), "FBX material sources"));
            var textureCache = new Dictionary<ResourceIdentity, FbxTexturePreparation>();
            var unsupportedTextures = new Dictionary<ResourceIdentity, string>();
            var remainingUses = new Dictionary<ResourceIdentity, int>();
            var directTextures = new HashSet<ResourceIdentity>();
            foreach (var identity in selected)
            {
                if (!materials.TryGetValue(identity, out var render)) continue;
                foreach (var slot in render.Asset.Bindings.Select(binding => binding.SlotOrdinal)
                    .Concat(HasPlacementDependentGraph(render.Asset) ? [] : render.Asset.SurfaceGraph?.TextureSlots ?? []).Distinct())
                {
                    if (slot < 0 || slot >= render.Asset.References.Count) continue;
                    var resolution = resolver.Resolve(render.Asset.References[slot]);
                    if (resolution is not { Status: ReferenceStatus.Unique, Candidates.Count: 1 }) continue;
                    var texture = resolution.Candidates[0];
                    remainingUses[texture] = remainingUses.GetValueOrDefault(texture) + 1;
                    if (render.Asset.Bindings.Any(binding => binding.SlotOrdinal == slot)) directTextures.Add(texture);
                }
            }
            foreach (var identity in selected)
            {
                token.ThrowIfCancellationRequested();
                if (!materials.TryGetValue(identity, out var renderMaterial)) continue;
                var material = renderMaterial.Asset;
                if (material.Identity != identity || material.Revision != Revision ||
                    !container.Directory.Objects.Any(frame => frame.Identity == identity && frame.NativeType == 2133))
                    throw new ArchiveException("SourceChanged", "A material no longer matches its source frame.");
                var materialTextures = new List<FbxMaterialTextureInput>();
                var bakeDiagnostics = new List<string>();
                bool unsupportedGraphTextures = false;
                bool placementDependentGraph = HasPlacementDependentGraph(material);
                if (placementDependentGraph)
                    bakeDiagnostics.Add("The colour graph requires camera or world-space inputs that a fixed UV map cannot represent.");
                var requiredSlots = material.Bindings.Select(binding => binding.SlotOrdinal)
                    .Concat(placementDependentGraph ? [] : material.SurfaceGraph?.TextureSlots ?? []).Distinct().Order().ToArray();
                foreach (var slot in requiredSlots)
                {
                    token.ThrowIfCancellationRequested();
                    if (++slots > options.MaximumTextures) throw new ArchiveException("TextureExportLimit", "The texture slot count exceeds the export limit.");
                    if (slot < 0 || slot >= material.References.Count)
                        throw new ArchiveException("MaterialSamplerSlotInvalid", "A material graph selects an unavailable source texture slot.");
                    var reference = material.References[slot];
                    var resolution = resolver.Resolve(reference);
                    if (resolution is not { Status: ReferenceStatus.Unique, Candidates.Count: 1 }) continue;
                    var textureIdentity = resolution.Candidates[0];
                    if (unsupportedTextures.TryGetValue(textureIdentity, out var unsupportedCode))
                    { unsupportedGraphTextures = true; bakeDiagnostics.Add($"Texture slot {slot}: {unsupportedCode}."); continue; }
                    if (!textureCache.TryGetValue(textureIdentity, out var original))
                    {
                        AssetLoadDiagnostics.Cache("ExportTextureDecode", textureIdentity.Key, false);
                        using var textureTiming = AssetLoadDiagnostics.Measure("ExportTextureDecode", textureIdentity.Key);
                        int preparationStart = retained.Count;
                        long sourceBytesBefore = retainedTextureSourceBytes;
                        try
                        {
                        NativeTextureSource? loadedSource = null;
                        NativeTextureValidationSnapshot? loadedValidation = null;
                        var dataReservations = new List<IDisposable>();
                        long sourceBytes = 0;
                        var plan = preparationCache is null ? null : await textures.PlanExportAsync(textureIdentity,
                            catalogIdentity, container, token).ConfigureAwait(false);
                        DdsTextureDocument document; TextureSurface surface;
                        NativeTextureValidationSnapshot validation;
                        NativeTextureSamplingProfile sampling;
                        Func<CancellationToken, Task>? exportSourceValidation = null;
                        if (preparationCache is not null)
                        {
                            var exportPlan = plan!;
                            Task<(DdsTextureDocument Document, TextureSurface Surface, NativeTextureExportSourceEvidence Evidence)>? producedOriginal = null;
                            Task<(DdsTextureDocument Document, TextureSurface Surface, NativeTextureExportSourceEvidence Evidence)> ProduceOnce(CancellationToken cancellation)
                                => producedOriginal ??= ProduceOriginalAsync(cancellation);
                            async Task<(DdsTextureDocument Document, TextureSurface Surface, NativeTextureExportSourceEvidence Evidence)> ProduceOriginalAsync(CancellationToken cancellation)
                            {
                                var decoded = await DecodeOriginalAsync(cancellation).ConfigureAwait(false);
                                return (decoded.Document, decoded.Surface, textures.CaptureExportEvidence(exportPlan, loadedSource!, cancellation));
                            }
                            var prepared = await UseOptionalExportPreparationAsync<
                                (DdsTextureDocument Document, TextureSurface Surface, NativeTextureExportSourceEvidence Evidence, IAsyncDisposable Lease)>(
                                () => preparationCache.AcquirePlannedTextureAsync(exportPlan.Fingerprint, hdrConversion,
                                    ProduceOnce, (evidence, cancellation) => textures.VerifyExportEvidenceAsync(exportPlan, evidence, cancellation),
                                    CreateContainerSourceValidation(container), MemoryBudget, token),
                                async () =>
                                {
                                    container.EnsureUnchanged();
                                    var uncachedOriginal = await ProduceOnce(token).ConfigureAwait(false);
                                    var verification = await textures.VerifyExportEvidenceAsync(exportPlan, uncachedOriginal.Evidence, token).ConfigureAwait(false);
                                    try
                                    {
                                        token.ThrowIfCancellationRequested(); container.EnsureUnchanged();
                                        return (uncachedOriginal.Document, uncachedOriginal.Surface, uncachedOriginal.Evidence, verification);
                                    }
                                    catch { verification.Dispose(); throw; }
                                }, token).ConfigureAwait(false);
                            retained.Add(new ExportPreparationLease(prepared.Lease));
                            document = prepared.Document; surface = prepared.Surface;
                            validation = textures.ExportValidation(exportPlan, prepared.Evidence);
                            if (document.Identity != exportPlan.Fingerprint.MetadataIdentity || document.Revision != Revision ||
                                document.DefaultSelection.MipIndex != 0 || surface.MipIndex != 0 ||
                                surface.Width != exportPlan.MetadataSource.Metadata.Mip0Width || surface.Height != exportPlan.MetadataSource.Metadata.Mip0Height)
                                throw new ArchiveException("TextureSourceMismatch", "The prepared texture is not the original selected base mip.");
                            exportSourceValidation = CreateExportSourceValidation(textures, exportPlan, prepared.Evidence);
                            sampling = TextureFileTextSamplingReader.Read(exportPlan.MetadataSource.Metadata);
                        }
                        else
                        {
                            (document, surface) = await DecodeOriginalAsync(token).ConfigureAwait(false);
                            validation = loadedValidation!;
                            sampling = TextureFileTextSamplingReader.Read(loadedSource!.Metadata);
                        }
                        if ((long)surface.Width * surface.Height > Math.Min(options.MaximumTexturePixels, DdsTextureReader.MaximumPixelCount))
                            throw new ArchiveException("TextureExportLimit", "An original texture exceeds the per-image pixel limit.");
                        var documentMemory = MemoryBudget.Reserve(document.OriginalBytes.Length + 4096L, "FBX retained texture document"); retained.Add(documentMemory); dataReservations.Add(documentMemory);
                        var pixelMemory = MemoryBudget.Reserve((surface.IsHdr ? surface.HdrPixels.Length * 16L : surface.Rgba8.Length) + 4096L, "FBX retained original texture pixels"); retained.Add(pixelMemory); dataReservations.Add(pixelMemory);

                        async Task<NativeTextureSource> LoadOriginalSourceAsync(CancellationToken cancellation)
                        {
                            if (loadedSource is not null) return loadedSource;
                            long estimate = plan?.EstimatedSourceBytes ?? (await textures.EstimateNativeSourceAsync(textureIdentity,
                                catalogIdentity, container, cancellation).ConfigureAwait(false)).RetainedSourceBytes;
                            if (estimate > NativeTextureSession.MaximumRetainedSourceBytes - retainedTextureSourceBytes)
                                throw new ArchiveException("TextureExportLimit", "Original texture sources exceed the shared source limit.");
                            var memory = MemoryBudget.Reserve(estimate, "FBX original texture source"); retained.Add(memory); dataReservations.Add(memory);
                            var read = await textures.ReadForNativeAsync(textureIdentity, catalogIdentity, container, cancellation,
                                reservedSourceBytes: retainedTextureSourceBytes).ConfigureAwait(false);
                            var source = read.Source ?? throw new TextureFormatException("MaterialTextureUnavailable", read.Reason ?? "The original texture is unavailable.");
                            sourceBytes = source.RetainedSourceBytes;
                            retainedTextureSourceBytes = checked(retainedTextureSourceBytes + sourceBytes);
                            memory.ReduceTo(sourceBytes); retained.Add(textures.PinSource(source));
                            loadedSource = source;
                            if (plan is null) loadedValidation = textures.CaptureNativeValidation(source, cancellation);
                            return source;
                        }

                        async Task<(DdsTextureDocument Document, TextureSurface Surface)> DecodeOriginalAsync(CancellationToken cancellation)
                        {
                            var source = await LoadOriginalSourceAsync(cancellation).ConfigureAwait(false);
                            var decoder = new NativeTextureDecoder(Math.Min(128L * 1024 * 1024, MemoryBudget.MaximumBytes));
                            using var documentScratch = MemoryBudget.Reserve(decoder.EstimateWorkingSetBytes(source), "FBX texture document");
                            var decoded = decoder.ReadDetailed(source, cancellation);
                            var decodedDocument = decoded.Document;
                            if (source.NativeTextureIdentity != textureIdentity || decodedDocument.Identity != source.Metadata.Identity || decodedDocument.Revision != Revision ||
                                !decoded.SupportedMips.Any(mip => mip.MipIndex == 0 && mip.Width == source.Metadata.Mip0Width &&
                                    mip.Height == source.Metadata.Mip0Height) || decodedDocument.DefaultSelection.MipIndex != 0)
                                throw new ArchiveException("TextureSourceMismatch", "The decoded texture is not the original selected base mip.");
                            documentScratch.ReduceTo(decodedDocument.OriginalBytes.Length + 4096L);
                            var pixelDecoder = new DdsTextureDecoder(MemoryBudget.MaximumBytes);
                            var selectedExtent = decodedDocument.Subresources.Single(item => item.Selection == decodedDocument.DefaultSelection);
                            var pixels = checked((long)selectedExtent.Width * selectedExtent.Height);
                            if (pixels > Math.Min(options.MaximumTexturePixels, DdsTextureReader.MaximumPixelCount))
                                throw new ArchiveException("TextureExportLimit", "An original texture exceeds the per-image pixel limit.");
                            using var pixelScratch = MemoryBudget.Reserve(checked(pixelDecoder.EstimateWorkingSetBytes(decodedDocument, decodedDocument.DefaultSelection) +
                                (decodedDocument.Info.IsHdr ? pixels * 4 : 0) + 4096), "FBX original texture pixels");
                            var decodedSurface = await pixelDecoder.DecodeSubresourceAsync(decodedDocument, decodedDocument.DefaultSelection, cancellation).ConfigureAwait(false);
                            if (decodedSurface.IsHdr && hdrConversion is not null)
                            {
                                decodedSurface = HdrDisplayTransform.ToSdr(decodedSurface, hdrConversion.ExposureStops, hdrConversion.ToneMap, cancellation);
                            }
                            pixelScratch.ReduceTo((decodedSurface.IsHdr ? decodedSurface.HdrPixels.Length * 16L : decodedSurface.Rgba8.Length) + 4096L);
                            return (decodedDocument, decodedSurface);
                        }
                        retained.Add(MemoryBudget.Reserve(checked(4096L + validation.Payloads.Count * 512L), "FBX texture source fingerprints"));
                        original = new(document, surface, sampling, validation, sourceBytes, dataReservations, exportSourceValidation);
                        textureCache.Add(textureIdentity, original);
                        if (collectCounters)
                            AssetLoadDiagnostics.Counters("ExportTextureDecode", textureIdentity.Key, new Dictionary<string, long>
                            {
                                ["Width"] = surface.Width, ["Height"] = surface.Height,
                                ["Pixels"] = (long)surface.Width * surface.Height, ["SourceBytes"] = sourceBytes,
                                ["DocumentBytes"] = document.OriginalBytes.Length,
                                ["SourceHdr"] = document.Info.IsHdr ? 1 : 0, ["ToneMapped"] = surface.DisplayTransform is null ? 0 : 1
                            });
                        }
                        catch (TextureFormatException error) when (!directTextures.Contains(textureIdentity) &&
                            error.Code is "UnsupportedNativeTextureFormat" or "UnsupportedNativeTextureLayout" or "UnsupportedNativeTextureConversion")
                        {
                            for (int index = retained.Count - 1; index >= preparationStart; index--) retained[index].Dispose();
                            retained.RemoveRange(preparationStart, retained.Count - preparationStart);
                            retainedTextureSourceBytes = sourceBytesBefore;
                            unsupportedTextures.Add(textureIdentity, error.Code);
                            unsupportedGraphTextures = true;
                            bakeDiagnostics.Add($"Texture slot {slot}: {error.Code}.");
                            continue;
                        }
                        catch (TextureFormatException error)
                        {
                            throw new TextureFormatException(error.Code, $"Texture slot {slot} ({textureIdentity.Key}): {error.Message}", error);
                        }
                    }
                    else AssetLoadDiagnostics.Cache("ExportTextureDecode", textureIdentity.Key, true);
                    texturePixels = checked(texturePixels + (long)original.Surface.Width * original.Surface.Height);
                    if (texturePixels > options.MaximumTexturePixels) throw new ArchiveException("TextureExportLimit", "Original texture slots exceed the pixel limit.");
                    var sourceForValidation = original.Validation;
                    if (original.Surface.IsHdr && material.SurfaceGraph?.TextureSlots.Contains(slot) == true)
                    {
                        unsupportedGraphTextures = true;
                        bakeDiagnostics.Add(material.Bindings.Any(binding => binding.SlotOrdinal == slot)
                            ? $"Texture slot {slot}: HDR is retained as encoded DDS; colour baking requires an HDR display transform."
                            : $"Texture slot {slot}: HDR colour baking requires an HDR display transform.");
                    }
                    var preparedTexture = new FbxMaterialTextureInput(slot, reference, resolution, original.Document, original.Surface,
                        Revision, original.ValidateSources ?? CreateTextureSourceValidation(sourceForValidation), new(textureIdentity, original.Document.Identity!, Revision,
                            new(true, NativeTextureSession.Version, "Native frame resolved to its verified FTEXFILE metadata document.")));
                    if (preparationCache is not null)
                    {
                        var sourceKey = NativeModelExportPreparationCache.TextureKey(sourceForValidation, hdrConversion);
                        preparedTexture = preparedTexture with { PublishPreparedMedia = CreatePreparedMediaPublisher(
                            preparationCache, sourceKey, original.Document, original.Surface, Snapshot.SourceRoot,
                            preparedTexture.ValidateSourceRevision, MemoryBudget) };
                    }
                    materialTextures.Add(preparedTexture);
                }
                var maps = new List<FbxStagedMaterialMapInput>();
                var residentMaps = new List<FbxBakedMaterialMapInput>();
                if (!placementDependentGraph && !unsupportedGraphTextures && material.SurfaceGraph is { BaseColor: not null } graph)
                {
                    bakeDiagnostics.Add("Colour maps sample the source base mip with its FILETEXT filter, addressing and border settings.");
                    var bakeTextures = materialTextures.ToDictionary(texture => texture.SlotOrdinal,
                        texture => new MaterialBakeTexture(texture.Surface, textureCache[texture.Resolution.Candidates[0]].Sampling));
                    int width = Math.Max(1, materialTextures.Where(texture => graph.TextureSlots.Contains(texture.SlotOrdinal))
                        .Select(texture => texture.Surface.Width).DefaultIfEmpty(1).Max());
                    int height = Math.Max(1, materialTextures.Where(texture => graph.TextureSlots.Contains(texture.SlotOrdinal))
                        .Select(texture => texture.Surface.Height).DefaultIfEmpty(1).Max());
                    var mapSources = materialTextures.Where(texture => graph.TextureSlots.Contains(texture.SlotOrdinal)).ToArray();
                    var mapValidations = mapSources.Select(source => source.ValidateSourceRevision).ToArray();
                    foreach (var primitive in geometry.Primitives.Where(primitive => primitive.Submeshes
                        .SelectMany(part => part.Materials).Any(reference => joined.Any(join => join.Reference == reference && join.Resolution.Candidates[0] == identity))))
                    {
                        token.ThrowIfCancellationRequested();
                        if (primitive.Submeshes.SelectMany(part => part.Materials).Distinct().Count() != 1)
                        { bakeDiagnostics.Add($"{primitive.Key}: Colour baking requires one material per primitive."); continue; }
                        if (graph.RequiredInputs.Contains(MaterialInput.WorldPosition) || graph.RequiredInputs.Contains(MaterialInput.WorldNormal))
                        { bakeDiagnostics.Add($"{primitive.Key}: World-space material inputs need an explicit export placement."); continue; }
                        if (preparationCache is null)
                            maps.AddRange(await PreparePrimitiveMapsAsync().ConfigureAwait(false));
                        else
                        {
                            var sourceKeys = mapSources.Select(texture => NativeModelExportPreparationCache.TextureKey(
                                textureCache[texture.Resolution.Candidates[0]].Validation, hdrConversion)).ToArray();
                            var key = NativeModelExportPreparationCache.MaterialKey(material, primitive, sourceKeys,
                                bakeTextures, width, height, options.MaximumTexturePixels, token);
                            int diagnosticStart = bakeDiagnostics.Count;
                            int residentStart = residentMaps.Count;
                            Task<IReadOnlyList<FbxStagedMaterialMapInput>>? producedMaps = null;
                            Task<IReadOnlyList<FbxStagedMaterialMapInput>> ProduceMapsOnce() => producedMaps ??= PreparePrimitiveMapsAsync();
                            async Task<IReadOnlyList<FbxStagedMaterialMapInput>> ProduceCacheMapsAsync()
                            {
                                var produced = await ProduceMapsOnce().ConfigureAwait(false);
                                if (residentMaps.Count != residentStart)
                                {
                                    var error = new IOException("Material maps are retained in memory for this export.");
                                    error.Data["ErrorCode"] = "CacheStorageLimit";
                                    throw error;
                                }
                                return produced;
                            }
                            var validateMaps = CreateBakeSourceValidation(container, mapValidations);
                            var prepared = await UseOptionalExportPreparationAsync<
                                (IReadOnlyList<FbxStagedMaterialMapInput> Maps, IAsyncDisposable Lease, bool CacheHit, IReadOnlyList<string> Diagnostics)>(
                                () => preparationCache.AcquireMapsAsync(key, ProduceCacheMapsAsync, validateMaps, MemoryBudget, token,
                                    () => bakeDiagnostics.Skip(diagnosticStart).ToArray()),
                                async () =>
                                {
                                    await validateMaps(token).ConfigureAwait(false);
                                    var originalMaps = await ProduceMapsOnce().ConfigureAwait(false);
                                    token.ThrowIfCancellationRequested(); await validateMaps(token).ConfigureAwait(false);
                                    return (originalMaps, EmptyExportPreparationLease.Instance, false, Array.Empty<string>());
                                }, token).ConfigureAwait(false);
                            retained.Add(new ExportPreparationLease(prepared.Lease));
                            if (prepared.CacheHit)
                            {
                                bakeDiagnostics.AddRange(prepared.Diagnostics);
                                var restoredPixels = prepared.Maps.Sum(map => checked((long)map.Width * map.Height));
                                if (restoredPixels > options.MaximumTexturePixels - texturePixels)
                                    throw new ArchiveException("TextureExportLimit", "Prepared maps exceed the shared pixel limit.");
                                texturePixels = checked(texturePixels + restoredPixels);
                            }
                            maps.AddRange(prepared.Maps);
                        }

                        async Task<IReadOnlyList<FbxStagedMaterialMapInput>> PreparePrimitiveMapsAsync()
                        {
                            var primitiveMaps = new List<FbxStagedMaterialMapInput>();
                            var pixels = checked((long)width * height);
                            if (pixels > options.MaximumTexturePixels - texturePixels)
                                throw new ArchiveException("TextureExportLimit", "Baked colour maps exceed the shared pixel limit.");
                            var mapMemory = MemoryBudget.Reserve(checked(pixels * 4 + 8192), "FBX baked material map"); retained.Add(mapMemory);
                            MaterialSurfaceMap? baked = null;
                            var failures = new List<string>();
                            int atlasRequiredFailures = 0;
                            for (int uvSet = 0; uvSet < Math.Min(4, primitive.TextureCoordinates?.Count ?? 0); uvSet++)
                            {
                                try
                                {
                                    baked = await BakeMapAsync(width, height, uvSet, null, mapMemory.Bytes).ConfigureAwait(false);
                                    break;
                                }
                                catch (MaterialBakeAtlasRequiredException error)
                                { failures.Add($"UV {uvSet}: {error.Message}"); atlasRequiredFailures++; }
                                catch (InvalidDataException error)
                                { failures.Add($"UV {uvSet}: {error.Message}"); }
                            }
                            if (baked is null)
                            {
                                mapMemory.Dispose();
                                if (atlasRequiredFailures == 0)
                                { bakeDiagnostics.Add($"{primitive.Key}: {string.Join("; ", failures)}"); return []; }
                                var cornerMemory = MemoryBudget.Reserve(checked(primitive.Indices.Count * 8L + 4096), "FBX atlas coordinates"); retained.Add(cornerMemory);
                                MaterialUvAtlasResult atlas;
                                int requestedAtlasResolution = Math.Max(16, Math.Max(width, height));
                                int atlasResolution = requestedAtlasResolution;
                                try
                                {
                                    while (true)
                                    {
                                        using (AssetLoadDiagnostics.Measure("ExportAtlas", $"{material.Identity.Key}|{primitive.Key}|resolution={atlasResolution}"))
                                            atlas = MaterialUvAtlas.Generate(primitive, atlasResolution, 64L * 1024 * 1024, MemoryBudget.Reserve, token);
                                        if (collectCounters)
                                            AssetLoadDiagnostics.Counters("ExportAtlas", $"{material.Identity.Key}|{primitive.Key}|resolution={atlasResolution}", new Dictionary<string, long>
                                            {
                                                ["RequestedResolution"] = atlasResolution, ["Width"] = atlas.Width, ["Height"] = atlas.Height,
                                                ["Pixels"] = (long)atlas.Width * atlas.Height, ["Corners"] = atlas.PolygonCornerCoordinates.Count
                                            });
                                        if (atlas.Width <= DdsTextureReader.MaximumDimension && atlas.Height <= DdsTextureReader.MaximumDimension &&
                                            (long)atlas.Width * atlas.Height <= DdsTextureReader.MaximumPixelCount) break;
                                        if (atlasResolution == 16) throw new InvalidDataException("The packed atlas exceeds the texture limits at the minimum resolution.");
                                        atlasResolution = Math.Max(16, atlasResolution / 2);
                                    }
                                    if (atlasResolution != requestedAtlasResolution)
                                        bakeDiagnostics.Add($"{primitive.Key}: Atlas packing resolution reduced from {requestedAtlasResolution} to {atlasResolution} to fit the texture limits.");
                                }
                                catch (InvalidDataException error) { cornerMemory.Dispose(); bakeDiagnostics.Add($"{primitive.Key}: {error.Message}"); return []; }
                                var atlasPixels = checked((long)atlas.Width * atlas.Height);
                                if (atlasPixels > options.MaximumTexturePixels - texturePixels)
                                    throw new ArchiveException("TextureExportLimit", "Atlas maps exceed the shared pixel limit.");
                                mapMemory = MemoryBudget.Reserve(checked(atlasPixels * 4 + 8192), "FBX baked atlas map"); retained.Add(mapMemory);
                                try
                                {
                                    baked = await BakeMapAsync(atlas.Width, atlas.Height, primitive.TextureCoordinates!.Count,
                                        atlas.PolygonCornerCoordinates, mapMemory.Bytes).ConfigureAwait(false);
                                }
                                catch (MaterialBakeAtlasRequiredException)
                                {
                                    // Keep source geometry and interpolation; only split the derived atlas charts.
                                    mapMemory.Dispose();
                                    atlas = MaterialUvAtlas.Generate(primitive, atlasResolution, 64L * 1024 * 1024,
                                        MemoryBudget.Reserve, token, separateFaces: true);
                                    atlasPixels = checked((long)atlas.Width * atlas.Height);
                                    if (atlasPixels > options.MaximumTexturePixels - texturePixels || atlasPixels > DdsTextureReader.MaximumPixelCount)
                                        throw new ArchiveException("TextureExportLimit", "The separated atlas exceeds the texture limits.");
                                    mapMemory = MemoryBudget.Reserve(checked(atlasPixels * 4 + 8192), "FBX separated atlas map"); retained.Add(mapMemory);
                                    baked = await BakeMapAsync(atlas.Width, atlas.Height, primitive.TextureCoordinates!.Count,
                                        atlas.PolygonCornerCoordinates, mapMemory.Bytes).ConfigureAwait(false);
                                    bakeDiagnostics.Add($"{primitive.Key}: Atlas faces separated to preserve distinct surface samples.");
                                }
                                catch (InvalidDataException error)
                                {
                                    throw new InvalidDataException($"Atlas coverage failed for material {material.Identity.Key}, primitive {primitive.Key}: {error.Message}", error);
                                }
                                pixels = atlasPixels;
                            }
                            mapMemory.ReduceTo(checked(baked.Document.OriginalBytes.Length + 4096L));
                            texturePixels = checked(texturePixels + pixels);
                            var colorMap = new FbxBakedMaterialMapInput(MaterialTextureRole.BaseColor, baked.TextureCoordinateSet, baked.Document, baked.Surface, baked.GraphSha256,
                                [primitive.Key], mapSources.Select(texture => new FbxBakeTextureSource(texture.SlotOrdinal,
                                    texture.Resolution.Candidates[0], Revision, texture.Document.ContentSha256, texture.Surface.Subresource)
                                    { Sampling = textureCache[texture.Resolution.Candidates[0]].Sampling }).ToArray(),
                                CreateBakeSourceValidation(container, mapValidations)) { PolygonCornerCoordinates = baked.PolygonCornerCoordinates };
                            var colorStaged = await StageMapAsync(colorMap).ConfigureAwait(false);
                            if (graph.Opacity is not null || graph.AlphaCutoff is not null)
                            {
                                if (pixels > options.MaximumTexturePixels - texturePixels)
                                    throw new ArchiveException("TextureExportLimit", "Baked transparency maps exceed the shared pixel limit.");
                                var coverageMemory = MemoryBudget.Reserve(checked(pixels * 4 + 8192), "FBX baked transparency map"); retained.Add(coverageMemory);
                                var transparency = MaterialSurfaceMapBaker.CreateTransparencyMap(baked, token);
                                coverageMemory.ReduceTo(checked(transparency.Document.OriginalBytes.Length + 4096L));
                                texturePixels = checked(texturePixels + pixels);
                                if (await StageMapAsync(colorMap with { Role = MaterialTextureRole.Opacity, Document = transparency.Document,
                                    Surface = transparency.Surface }).ConfigureAwait(false)) coverageMemory.Dispose();
                            }
                            if (colorStaged) mapMemory.Dispose();
                            return primitiveMaps;

                            async Task<bool> StageMapAsync(FbxBakedMaterialMapInput map)
                            {
                                try
                                {
                                    primitiveMaps.Add(await staging.StageAsync(map, MemoryBudget, token).ConfigureAwait(false));
                                    return true;
                                }
                                catch (IOException error) when (IsOptionalExportStorageFailure(error))
                                {
                                    token.ThrowIfCancellationRequested();
                                    // The caller's pixel reservation remains owned until FBX publication.
                                    residentMaps.Add(map);
                                    return false;
                                }
                            }

                        }

                        Task<MaterialSurfaceMap> BakeMapAsync(int mapWidth, int mapHeight, int uv,
                            IReadOnlyList<System.Numerics.Vector2>? cornerCoordinates, long reservedOutputBytes)
                        {
                            if (bakeCache is null) return Task.FromResult(MaterialSurfaceMapBaker.Bake(material, primitive,
                                bakeTextures, mapWidth, mapHeight, MemoryBudget.Reserve, token, uv, cornerCoordinates, reservedOutputBytes));
                            return bakeCache.BakeAsync(material, primitive, bakeTextures, mapWidth, mapHeight, MemoryBudget,
                                CreateBakeSourceValidation(container, mapValidations), token, uv, cornerCoordinates, reservedOutputBytes);
                        }
                    }
                }
                var directSlots = material.Bindings.Select(binding => binding.SlotOrdinal).ToHashSet();
                var graphSources = unsupportedGraphTextures || placementDependentGraph ? [] : materialTextures.Where(texture => !directSlots.Contains(texture.SlotOrdinal)).Select(texture =>
                    new FbxGraphTextureSourceInput(texture.SlotOrdinal, texture.Reference, texture.Resolution,
                        texture.Resolution.Candidates[0], texture.Document.Identity!, Revision, texture.Document.ContentSha256,
                        texture.Surface.Subresource, texture.Surface.Width, texture.Surface.Height,
                        texture.DocumentJoin!.Evidence, texture.ValidateSourceRevision)).ToArray();
                // The preview asset retains its graph. The static export omits a graph
                // whose sampled sources cannot be represented, with explicit notices.
                var exportMaterial = unsupportedGraphTextures || placementDependentGraph
                    ? material with { SurfaceGraph = null, Diagnostics = material.Diagnostics
                        .Append(placementDependentGraph ? "MaterialGraphPlacementRequired" : "MaterialGraphTextureUnavailable").ToArray() }
                    : material;
                result.Add(new(exportMaterial, joined.Where(join => join.Resolution.Candidates[0] == identity).ToArray(),
                    materialTextures.Where(texture => directSlots.Contains(texture.SlotOrdinal)).ToArray(),
                    CreateContainerSourceValidation(container))
                    { StagedMaps = maps, BakedMaps = residentMaps, BakingDiagnostics = bakeDiagnostics, GraphSources = graphSources });
                foreach (var texture in materialTextures)
                {
                    var textureIdentity = texture.Resolution.Candidates[0];
                    if (--remainingUses[textureIdentity] != 0 || directTextures.Contains(textureIdentity)) continue;
                    var prepared = textureCache[textureIdentity];
                    textureCache.Remove(textureIdentity);
                    retainedTextureSourceBytes -= prepared.SourceBytes;
                    prepared.ReleaseData();
                }
            }
            token.ThrowIfCancellationRequested(); container.EnsureUnchanged();
            var preparedInputs = new NativeModelFbxMaterialInputs(MemoryBudget, result, retained);
            materialJobCompleted = true;
            return preparedInputs;
        }
        catch (Exception error)
        {
            for (var i = retained.Count - 1; i >= 0; i--)
                try { retained[i].Dispose(); }
                catch (Exception cleanup)
                {
                    if (!error.Data.Contains("MaterialCleanupError"))
                    {
                        var detail = cleanup.GetType().Name + ": " + cleanup.Message;
                        error.Data["MaterialCleanupError"] = detail[..Math.Min(detail.Length, 2048)];
                    }
                }
            throw;
        }
        finally
        {
            if (collectCounters)
                AssetLoadDiagnostics.Counters("ExportMaterialJob", mesh.Key, new Dictionary<string, long>
                {
                    ["MaterialsSelected"] = selected.Length, ["MaterialsPrepared"] = result.Count,
                    ["TextureSlots"] = slots, ["TexturePixels"] = texturePixels,
                    ["RetainedSourceBytes"] = retainedTextureSourceBytes,
                    ["StagedMaps"] = result.Sum(input => (long)input.StagedMaps.Count),
                    ["ResidentMaps"] = result.Sum(input => (long)input.BakedMaps.Count),
                    ["Completed"] = materialJobCompleted ? 1 : 0
                });
        }
    }

    private sealed record FbxTexturePreparation(DdsTextureDocument Document, TextureSurface Surface, NativeTextureSamplingProfile Sampling,
        NativeTextureValidationSnapshot Validation, long SourceBytes, IReadOnlyList<IDisposable> DataReservations,
        Func<CancellationToken, Task>? ValidateSources = null)
    {
        public void ReleaseData() { foreach (var reservation in DataReservations) reservation.Dispose(); }
    }

    private sealed class ExportPreparationLease(IAsyncDisposable lease) : IDisposable
    {
        public void Dispose() => lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static Func<CancellationToken, Task> CreateBakeSourceValidation(NativeContainerLease container,
        IReadOnlyList<Func<CancellationToken, Task>> validations) => async cancellation =>
    {
        using var timing = AssetLoadDiagnostics.Measure("ExportBakeSourceValidation");
        cancellation.ThrowIfCancellationRequested(); container.EnsureUnchanged();
        foreach (var validate in validations) await validate(cancellation).ConfigureAwait(false);
    };

    private static Func<CancellationToken, Task> CreateExportSourceValidation(NativeTextureSession textures,
        NativeTextureExportPlan plan, NativeTextureExportSourceEvidence evidence) => async cancellation =>
    {
        using var timing = AssetLoadDiagnostics.Measure("ExportTextureSourceValidation", plan.Fingerprint.NativeIdentity.Key);
        using var verified = await textures.VerifyExportEvidenceAsync(plan, evidence, cancellation).ConfigureAwait(false);
        verified.EnsureUnchanged();
    };

    private static Func<string, string, CancellationToken, Task<TextureExportResult>> CreatePreparedMediaPublisher(
        NativeModelExportPreparationCache cache, string key, DdsTextureDocument document, TextureSurface surface,
        string sourceRoot, Func<CancellationToken, Task> validate, AssetMemoryBudget budget) => (stageRoot, name, cancellation) =>
            UseOptionalExportPreparationAsync(
                () => cache.PublishTextureMediaAsync(key, document, surface, sourceRoot, stageRoot, name, validate, budget, cancellation),
                () => new TextureExportService().ExportAsync(document, surface, surface.IsHdr ? AssetExportFormat.Dds : AssetExportFormat.Png,
                    sourceRoot, stageRoot, name, cancellationToken: cancellation, expectedRevision: document.Revision, preCommitValidation: validate),
                cancellation);

    internal static async Task<T> UseOptionalExportPreparationAsync<T>(Func<Task<T>> prepared, Func<Task<T>> fallback,
        CancellationToken token = default)
    {
        try { return await prepared().ConfigureAwait(false); }
        catch (IOException error) when (IsOptionalExportStorageFailure(error))
        {
            token.ThrowIfCancellationRequested();
            return await fallback().ConfigureAwait(false);
        }
    }

    private static bool IsOptionalExportStorageFailure(IOException error) =>
        !error.Data.Contains("ScratchCleanupError") && !error.Data.Contains("CacheCleanupError") &&
        (error.Data["ErrorCode"] is "CacheStorageLimit" || (error.HResult & 0xffff) is 112 or 39);

    private sealed class EmptyExportPreparationLease : IAsyncDisposable
    {
        internal static readonly EmptyExportPreparationLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private Func<CancellationToken, Task> CreateTextureSourceValidation(NativeTextureValidationSnapshot snapshot) => async cancellation =>
    {
        using var validationMemory = MemoryBudget.Reserve(NativeTextureSession.MaximumResourceBytes, "FBX texture source validation");
        await textures.RevalidateNativeSnapshotAsync(snapshot, cancellation).ConfigureAwait(false);
    };

    private static Func<CancellationToken, Task> CreateContainerSourceValidation(NativeContainerLease container) => cancellation =>
    {
        cancellation.ThrowIfCancellationRequested(); container.EnsureUnchanged(); return Task.CompletedTask;
    };

    private static bool HasPlacementDependentGraph(MaterialAsset material) => material.SurfaceGraph?.RequiredInputs
        .Any(input => input is MaterialInput.ViewDepth or MaterialInput.SceneViewDepth or MaterialInput.ViewDirection or MaterialInput.ViewNormal or
            MaterialInput.WorldPosition or MaterialInput.WorldNormal) == true;
}
