using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Export;
using Deviant.Export.Textures;
using Deviant.Formats.Models;
using Deviant.Formats.Textures;

namespace Deviant.Export.Models;

/// <summary>Writes the validated static-geometry portion of the FBX export contract.</summary>
public sealed partial class FbxGeometryExporter
{
    private const long WriterScratchBytes = 256 * 1024;
    private const double TransformTolerance = 1e-4;

    public async Task<FbxGeometryExportReport> ExportAsync(FbxModelExportInput input,
        string sourceRoot, string destination, FbxGeometryExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(input.Geometry);
        ArgumentNullException.ThrowIfNull(input.Selection);
        ArgumentNullException.ThrowIfNull(input.ReserveMemory);
        cancellationToken.ThrowIfCancellationRequested();
        if (input.ValidateGeometrySourceRevision is { } validateGeometry)
            await validateGeometry(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        if (options.MaximumTriangles < 1) throw new ArgumentOutOfRangeException(nameof(options.MaximumTriangles));
        if (options.MaximumNodes < 1) throw new ArgumentOutOfRangeException(nameof(options.MaximumNodes));
        if (options.MaximumVertices < 1) throw new ArgumentOutOfRangeException(nameof(options.MaximumVertices));
        if (options.MaximumSubmeshes < 1) throw new ArgumentOutOfRangeException(nameof(options.MaximumSubmeshes));
        if (options.MaximumMaterials < 1) throw new ArgumentOutOfRangeException(nameof(options.MaximumMaterials));
        if (options.MaximumMaterialReferences < 1) throw new ArgumentOutOfRangeException(nameof(options.MaximumMaterialReferences));
        if (options.MaximumTextures < 1) throw new ArgumentOutOfRangeException(nameof(options.MaximumTextures));
        if (options.MaximumTexturePixels < 1) throw new ArgumentOutOfRangeException(nameof(options.MaximumTexturePixels));
        if (options.MaximumMediaBytes < 1) throw new ArgumentOutOfRangeException(nameof(options.MaximumMediaBytes));
        if (options.MaximumBinaryBytes < 1) throw new ArgumentOutOfRangeException(nameof(options.MaximumBinaryBytes));
        if (options.MaximumSkinInfluences < 1) throw new ArgumentOutOfRangeException(nameof(options.MaximumSkinInfluences));
        if (options.MaximumAnimationKeys < 1) throw new ArgumentOutOfRangeException(nameof(options.MaximumAnimationKeys));
        if (!Enum.IsDefined(options.MediaPackaging)) throw new ArgumentOutOfRangeException(nameof(options.MediaPackaging));
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Selection.Key);
        if (input.Selection.LodIndex is < 0 || input.Selection.GroupIndex is < 0)
            throw new ArgumentOutOfRangeException(nameof(input), "Selected LOD and group indices must be non-negative.");

        var decoded = input.Geometry;
        var asset = decoded.Value ?? throw Error("GeometryDecodeFailed", "The decoded geometry result has no value.",
            decoded.Diagnostics.Select(message => new FbxGeometryExportLoss("DecodeDiagnostic", decoded.Identity.Key, message)).ToArray());
        if (decoded.Identity.IndexContentId != decoded.Revision.IndexContentId)
            throw new ArgumentException("Geometry identity and revision belong to different indexes.", nameof(input));
        if (input.Selection.Identity != decoded.Identity)
            throw new ArgumentException("The selected detail identity must match the decoded geometry identity.", nameof(input));
        var validation = GeometryValidator.Validate(asset);
        if (!validation.IsValid)
            throw Error("GeometryValidationFailed", "The geometry IR contains invalid references or vertex data.",
                validation.Diagnostics.Where(item => item.Severity == GeometryDiagnosticSeverity.Error)
                    .Select(item => new FbxGeometryExportLoss(item.Code, item.Subject ?? "asset", item.Message)).ToArray());
        var attributeErrors = new List<FbxGeometryExportLoss>();
        foreach (var primitive in asset.Primitives)
        {
            if (primitive.Colors is { } colors)
            {
                if (colors.Count != primitive.Positions.Count)
                    attributeErrors.Add(new("ColorCountMismatch", primitive.Key, "Color count must match the position count."));
                for (var index = 0; index < colors.Count; index++)
                    if (!Finite(colors[index]))
                        attributeErrors.Add(new("ColorNonFinite", primitive.Key, $"Color {index} contains a non-finite component."));
            }
            var orderedRanges = primitive.Submeshes.OrderBy(submesh => submesh.FirstIndex).ToArray();
            for (var index = 1; index < orderedRanges.Length; index++)
                if (orderedRanges[index - 1].FirstIndex + orderedRanges[index - 1].IndexCount > orderedRanges[index].FirstIndex)
                    attributeErrors.Add(new("SubmeshAssignmentsAmbiguous", primitive.Key,
                        $"Submeshes {orderedRanges[index - 1].Key} and {orderedRanges[index].Key} overlap; FBX supports one material assignment per face."));
        }
        if (attributeErrors.Count > 0)
            throw Error("GeometryValidationFailed", "The geometry IR contains attributes or assignments the FBX writer cannot represent safely.", attributeErrors);
        if (!Enum.IsDefined(input.CoordinateMetadata)) throw new ArgumentOutOfRangeException(nameof(input.CoordinateMetadata));
        if (input.CoordinateMetadata == FbxCoordinateMetadataPolicy.VerifiedBasis) ValidateCoordinates(asset.Coordinates);
        if (input.Skeleton is null && asset.Primitives.Any(primitive => primitive.Skin is not null))
            throw Error("SkinExportUnsupported", "Skinned geometry requires skeleton and bind-pose inputs.",
                [new("SkinExportUnsupported", decoded.Identity.Key, "This initial writer does not flatten or omit skin bindings.")]);
        if (asset.Primitives.Any(primitive => primitive.Topology != PrimitiveTopology.Triangles))
            throw Error("TopologyUnsupported", "The static FBX writer accepts triangle-list primitives only.",
                asset.Primitives.Where(primitive => primitive.Topology != PrimitiveTopology.Triangles)
                    .Select(primitive => new FbxGeometryExportLoss("TopologyUnsupported", primitive.Key, primitive.Topology.ToString())).ToArray());
        long outputModels = asset.Nodes.Count;
        var referencedPrimitives = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in asset.Nodes)
        {
            outputModels = checked(outputModels + node.PrimitiveKeys.Count);
            foreach (var key in node.PrimitiveKeys) referencedPrimitives.Add(key);
        }
        outputModels = checked(outputModels + asset.Primitives.LongCount(primitive => !referencedPrimitives.Contains(primitive.Key)));
        outputModels = checked(outputModels + (input.Skeleton?.Joints.Count ?? 0));
        if (outputModels > options.MaximumNodes)
            throw Error("NodeLimit", "The output hierarchy exceeds the configured node limit.",
                [new("NodeLimit", decoded.Identity.Key, $"Requested {outputModels}; limit is {options.MaximumNodes}.")]);
        long vertices = 0;
        long submeshes = 0, materialReferences = 0;
        foreach (var primitive in asset.Primitives)
        {
            vertices = checked(vertices + primitive.Positions.Count);
            submeshes = checked(submeshes + Math.Max(primitive.Submeshes.Count, 1));
            foreach (var submesh in primitive.Submeshes)
                materialReferences = checked(materialReferences + submesh.Materials.Count);
        }
        if (vertices > options.MaximumVertices)
            throw Error("VertexLimit", "The model exceeds the configured vertex limit.",
                [new("VertexLimit", decoded.Identity.Key, $"Requested {vertices}; limit is {options.MaximumVertices}.")]);
        if (submeshes > options.MaximumSubmeshes)
            throw Error("SubmeshLimit", "The model exceeds the configured submesh limit.",
                [new("SubmeshLimit", decoded.Identity.Key, $"Requested {submeshes}; limit is {options.MaximumSubmeshes}.")]);
        if (materialReferences > options.MaximumMaterialReferences)
            throw Error("MaterialReferenceLimit", "The model exceeds the configured material-reference limit.",
                [new("MaterialReferenceLimit", decoded.Identity.Key,
                    $"Requested {materialReferences}; limit is {options.MaximumMaterialReferences}.")]);
        long triangles = 0;
        foreach (var primitive in asset.Primitives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            triangles = checked(triangles + primitive.Indices.Count / 3L);
        }
        if (triangles > options.MaximumTriangles)
            throw Error("TriangleLimit", "The model exceeds the configured triangle limit.",
                [new("TriangleLimit", decoded.Identity.Key, $"Requested {triangles}; limit is {options.MaximumTriangles}.")]);

        var materialInputs = input.Materials ?? [];
        var allMaterialInputs = materialInputs.Concat(input.MaterialsBySubmesh.Values).ToArray();
        ValidateMaterialInputBounds(allMaterialInputs, options, asset.Primitives);
        var skinBytes = EstimateSkinPlanningBytes(input.Skeleton, options, cancellationToken);
        var animationBytes = EstimateAnimationPlanningBytes(input.Animation, input.Skeleton, options, cancellationToken);
        using var reservation = input.ReserveMemory(checked(EstimatePlanningBytes(asset, allMaterialInputs, options.MediaPackaging) + skinBytes + animationBytes),
            options.MediaPackaging == FbxMediaPackaging.Embedded ? "FBX binary document and writer buffer" : "FBX export plan and writer buffer");
        cancellationToken.ThrowIfCancellationRequested();
        ValidateMaterialInputs(materialInputs, options, cancellationToken, asset.Primitives);
        var scopedMaterials = input.MaterialsBySubmesh;
        var submeshKeys = asset.Primitives.SelectMany(primitive => primitive.Submeshes).Select(submesh => submesh.Key).ToHashSet(StringComparer.Ordinal);
        if (scopedMaterials.Keys.Any(key => !submeshKeys.Contains(key)))
            throw new ArgumentException("A scoped material belongs to an unavailable submesh.", nameof(input));
        ValidateMaterialInputBounds(scopedMaterials.Values.ToArray(), options, asset.Primitives);
        foreach (var scoped in scopedMaterials.Values)
            ValidateMaterialInputs([scoped], options, cancellationToken, asset.Primitives);
        foreach (var pair in scopedMaterials)
        {
            var submesh = asset.Primitives.SelectMany(p => p.Submeshes).Single(s => s.Key == pair.Key);
            if (!pair.Value.SubmeshReferences.Any(join => submesh.Materials.Contains(join.Reference) &&
                join.Resolution.Status == ReferenceStatus.Unique && join.Resolution.Candidates.SequenceEqual([pair.Value.Material.Identity])))
                throw new ArgumentException("The scoped material has no unique source reference from its submesh.", nameof(input));
        }
        foreach (var materialMap in allMaterialInputs.SelectMany(material => material.AllMaps))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await materialMap.ValidateSources(cancellationToken).ConfigureAwait(false);
        }
        var allMaterialReferences = BuildMaterialReferenceMap(materialInputs);
        var losses = new List<FbxGeometryExportLoss>();
        if (input.CoordinateMetadata == FbxCoordinateMetadataPolicy.PreserveSourceNumbers)
            losses.Add(new("CoordinateMetadataOmitted", decoded.Identity.Key, "Source coordinate values are preserved; physical axis and unit metadata are omitted."));
        var plan = BuildPlan(asset, allMaterialReferences, cancellationToken, losses, scopedMaterials);
        ValidateBakedMapAssignments(plan, allMaterialInputs);
        if (plan.Materials.Length > options.MaximumMaterials)
            throw Error("MaterialLimit", "The material plan exceeds the configured limit after per-primitive baked maps are split.",
                [new("MaterialLimit", "materials", $"Requested {plan.Materials.Length}; limit is {options.MaximumMaterials}.")]);
        plan = AddSkinPlan(plan, input.Skeleton, asset, cancellationToken);
        if (input.Skeleton is { } skeleton)
            await skeleton.ValidateSourceRevision(cancellationToken).ConfigureAwait(false);
        if (EstimateAnimationPlanningBytes(input.Animation, input.Skeleton, options, cancellationToken) > animationBytes)
            throw AnimationError("AnimationKeyLimit", "Animation inputs grew beyond their reserved planning memory.");
        plan = AddAnimationPlan(plan, input.Animation, input.Skeleton, asset, cancellationToken);
        if (input.Animation is { } animation)
            await animation.ValidateSourceRevision(cancellationToken).ConfigureAwait(false);
        ValidateMaterialUvSets(plan);
        var output = Path.GetFullPath(destination);
        OutputSafety.ValidateFilePath(output, sourceRoot);
        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        using var directoryPin = OutputSafety.PinDirectory(directory);
        OutputSafety.ValidateFilePath(output, sourceRoot);
        if (!options.Overwrite && File.Exists(output))
            throw Error("OutputExists", "The destination already exists.",
                [new("OutputExists", Path.GetFileName(output), "Choose a new destination or enable overwrite.")]);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        var stageRoot = Path.Combine(directory, $".{Path.GetFileName(output)}.media-stage-{Guid.NewGuid():N}");
        OutputSafety.ValidateFilePath(temporary, sourceRoot);
        OutputSafety.ValidateOutputRoot(stageRoot, sourceRoot);
        var mediaParentPins = new List<IDisposable>();
        var createdMediaFiles = new List<(string Path, string Sha256)>();
        FileStream? mediaPublicationLock = null;
        var committed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(stageRoot);
            using var stagePin = OutputSafety.PinDirectory(stageRoot);
            var mediaPlan = await StageMaterialMediaAsync(input, plan, sourceRoot, stageRoot, output,
                options, losses, cancellationToken).ConfigureAwait(false);
            if (options.MediaPackaging == FbxMediaPackaging.Sidecars)
            {
                mediaPublicationLock = PrepareMaterialMediaPublication(mediaPlan, directory, sourceRoot, mediaParentPins);
                await PublishMaterialMediaAsync(mediaPlan, directory, sourceRoot, createdMediaFiles, cancellationToken).ConfigureAwait(false);
            }
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.SequentialScan))
            {
                if (options.MediaPackaging == FbxMediaPackaging.Embedded)
                {
                    var creationTime = FbxBinary7400Writer.FormatCreationTime(DateTimeOffset.UtcNow);
                    var document = BuildBinaryDocument(input, plan, mediaPlan, creationTime, cancellationToken);
                    FbxBinary7400Writer.Write(stream, document, creationTime, options.MaximumBinaryBytes, cancellationToken);
                }
                else
                {
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { NewLine = "\n" };
                    WriteFile(writer, input, plan, mediaPlan, losses, cancellationToken);
                    writer.Flush();
                }
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await ValidateSourceRevisionsAsync(plan, mediaPlan, cancellationToken).ConfigureAwait(false);
            if (input.Skeleton is { } validateSkeleton)
                await validateSkeleton.ValidateSourceRevision(cancellationToken).ConfigureAwait(false);
            if (input.Animation is { } validateAnimation)
                await validateAnimation.ValidateSourceRevision(cancellationToken).ConfigureAwait(false);
            OutputSafety.ValidateFilePath(output, sourceRoot);
            var hash = await HashFileAsync(temporary, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (input.ValidateGeometrySourceRevision is { } validateBeforeCommit)
                await validateBeforeCommit(cancellationToken).ConfigureAwait(false);
            if (input.Skeleton is { } validateSkeletonBeforeCommit)
                await validateSkeletonBeforeCommit.ValidateSourceRevision(cancellationToken).ConfigureAwait(false);
            if (input.Animation is { } validateAnimationBeforeCommit)
                await validateAnimationBeforeCommit.ValidateSourceRevision(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            OutputSafety.ValidateFilePath(output, sourceRoot);
            File.Move(temporary, output, options.Overwrite);
            committed = true;
            return new(decoded.Identity, decoded.Revision, input.Selection, Path.GetFileName(output), hash,
                asset.Primitives.Count, plan.Models.Count, plan.Materials.Length, losses.AsReadOnly(), mediaPlan.Reports.AsReadOnly(),
                options.MediaPackaging, input.CoordinateMetadata) { DerivedMaterialMaps = mediaPlan.DerivedReports.AsReadOnly() };
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            if (!committed) await CleanupCreatedMediaFilesAsync(createdMediaFiles).ConfigureAwait(false);
            mediaPublicationLock?.Dispose();
            for (var index = mediaParentPins.Count - 1; index >= 0; index--) mediaParentPins[index].Dispose();
            try { if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static FbxPlan BuildPlan(GeometryAsset asset,
        IReadOnlyDictionary<ResourceReference, FbxMaterialJoinPlan> materialInputs,
        CancellationToken token, List<FbxGeometryExportLoss> losses,
        IReadOnlyDictionary<string, FbxMaterialExportInput>? scopedMaterials = null)
    {
        var byKey = asset.Primitives.ToDictionary(primitive => primitive.Key, StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in asset.Nodes)
        {
            token.ThrowIfCancellationRequested();
            foreach (var key in node.PrimitiveKeys) referenced.Add(key);
        }

        var materialByKey = new Dictionary<string, FbxMaterialPlan>(StringComparer.Ordinal);
        var geometryByKey = new Dictionary<string, FbxPrimitivePlan>(StringComparer.Ordinal);
        foreach (var primitive in asset.Primitives)
        {
            token.ThrowIfCancellationRequested();
            if (primitive.Normals is null)
                losses.Add(new("NormalsMissing", primitive.Key, "The source has no normals; the FBX mesh omits the normal layer."));
            if (primitive.Tangents is null)
                losses.Add(new("TangentsMissing", primitive.Key, "The source has no tangents; the FBX mesh omits the tangent layer."));
            else if (primitive.TextureCoordinates is not { Count: > 0 })
                losses.Add(new("TangentUvAssociationUnavailable", primitive.Key,
                    "The tangent values are written without a UV association because the primitive has no primary UV set."));
            var submeshes = primitive.Submeshes.Count == 0
                ? new[] { new GeometrySubmesh("default", 0, primitive.Indices.Count, []) }
                : primitive.Submeshes;
            var assignments = Enumerable.Repeat(-1, primitive.Indices.Count / 3).ToArray();
            var keys = new List<string>();
            var localSlots = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var submesh in submeshes)
            {
                token.ThrowIfCancellationRequested();
                if (submesh.IndexCount == 0) continue;
                string? key = null;
                if (submesh.Materials.Count == 1)
                {
                    key = MaterialKey(submesh.Materials);
                    materialInputs.TryGetValue(submesh.Materials[0], out var join);
                    var scoped = scopedMaterials?.GetValueOrDefault(submesh.Key);
                    var resolvedMaterial = scoped ?? join?.MaterialInput;
                    if (scoped is not null) key += "/submesh/" + submesh.Key;
                    var applicableBakedMaps = resolvedMaterial?.AllMaps
                        .Where(map => map.PrimitiveKeys.Contains(primitive.Key, StringComparer.Ordinal)).ToArray() ?? [];
                    if (applicableBakedMaps.Length > 0)
                        key = BakedMaterialKey(key, primitive.Key);
                    AddMaterial(key, submesh.Materials, false, resolvedMaterial,
                        applicableBakedMaps.Length > 0 ? primitive.Key : null,
                        applicableBakedMaps.Any(map => map.Role == MaterialTextureRole.BaseColor));
                    if (resolvedMaterial is null)
                    {
                        var reason = join?.Status switch
                        {
                            ReferenceStatus.Missing => "The material reference is missing.",
                            ReferenceStatus.Ambiguous => "The material reference has multiple candidates.",
                            _ => "No unique decoded material join was supplied."
                        };
                        var code = join?.Status switch
                        {
                            ReferenceStatus.Missing => "MaterialReferenceMissing",
                            ReferenceStatus.Ambiguous => "MaterialReferenceAmbiguous",
                            _ => "MaterialValuesUnavailable"
                        };
                        losses.Add(new(code, primitive.Key,
                            $"Submesh {submesh.Key} retains its source reference. {reason}", submesh.Materials));
                    }
                    else
                        losses.Add(new("MaterialValuesUnavailable", primitive.Key,
                            $"Submesh {submesh.Key} has typed material and texture bindings, but scalar surface values are unavailable; neutral FBX factors remain.",
                            submesh.Materials));
                }
                else if (submesh.Materials.Count > 1)
                {
                    key = MaterialKey(submesh.Materials);
                    AddMaterial(key, submesh.Materials, true, null);
                    losses.Add(new("MaterialReferenceAmbiguous", primitive.Key,
                        $"Submesh {submesh.Key} has multiple material references; a neutral FBX placeholder retains the alternatives.",
                        submesh.Materials));
                }
                else
                {
                    losses.Add(new("MaterialReferenceMissing", primitive.Key, $"Submesh {submesh.Key} has no material reference."));
                }

                if (key is null) continue;
                if (!localSlots.TryGetValue(key, out var slot))
                {
                    slot = keys.Count;
                    localSlots.Add(key, slot);
                    keys.Add(key);
                }
                var firstFace = submesh.FirstIndex / 3;
                var faceCount = submesh.IndexCount / 3;
                for (var face = firstFace; face < firstFace + faceCount; face++) assignments[face] = slot;
            }
            geometryByKey.Add(primitive.Key, new(primitive, keys, assignments, 0));
        }

        var nextId = 100;
        var geometries = geometryByKey.Values.OrderBy(item => item.Primitive.Key, StringComparer.Ordinal)
            .Select(item => item with { GeometryId = nextId++ }).ToDictionary(item => item.Primitive.Key, StringComparer.Ordinal);
        var models = new List<FbxModelPlan>();
        var nodeIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var node in asset.Nodes)
        {
            token.ThrowIfCancellationRequested();
            var transform = node.Transform is { } source ? ConvertTransform(source, asset.Coordinates, node.Key) : FbxTrs.Identity;
            nodeIds.Add(node.Key, nextId);
            models.Add(new(nextId++, node.Key, "Null", node.ParentKey, 0, transform, node, null));
        }
        foreach (var node in asset.Nodes)
        {
            token.ThrowIfCancellationRequested();
            foreach (var key in node.PrimitiveKeys)
                models.Add(new(nextId++, $"{node.Key}/{key}", "Mesh", null, nodeIds[node.Key],
                    FbxTrs.Identity, null, geometries[key]));
        }
        foreach (var primitive in geometries.Values.Where(item => !referenced.Contains(item.Primitive.Key)))
            models.Add(new(nextId++, primitive.Primitive.Key, "Mesh", null, 0, FbxTrs.Identity, null, primitive));
        foreach (var model in models.Where(model => model.SourceNode is not null))
            if (model.SourceNode!.ParentKey is { } parent && !nodeIds.ContainsKey(parent))
                throw Error("HierarchyParentMissing", "A source node refers to an unavailable parent.",
                    [new("HierarchyParentMissing", model.Name, $"Parent node {parent} is missing.")]);

        var materials = materialByKey.Values.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item with { Id = nextId++ }).ToArray();
        return new(geometries, models, materials, nodeIds,
            materials.ToDictionary(item => item.Key, item => item.Id, StringComparer.Ordinal), nextId);

        void AddMaterial(string key, IReadOnlyList<ResourceReference> references, bool ambiguous,
            FbxMaterialExportInput? materialInput, string? primitiveKey = null, bool hasBakedBaseColor = false)
        {
            if (!materialByKey.ContainsKey(key))
                materialByKey.Add(key, new(key, 0, references.ToArray(), ambiguous, materialInput,
                    primitiveKey, hasBakedBaseColor));
        }
    }

    private static string BakedMaterialKey(string sourceKey, string primitiveKey) =>
        HashText($"{sourceKey}\nBakedPrimitive={primitiveKey}");

    private static void ValidateBakedMapAssignments(FbxPlan plan, IReadOnlyList<FbxMaterialExportInput> inputs)
    {
        foreach (var input in inputs)
        foreach (var map in input.AllMaps)
        foreach (var primitiveKey in map.PrimitiveKeys)
        {
            if (!plan.Materials.Any(material => ReferenceEquals(material.MaterialInput, input) &&
                    StringComparer.Ordinal.Equals(material.PrimitiveKey, primitiveKey)))
                throw Error("BakedMaterialMapAssignmentMismatch", "A baked map names geometry that is not assigned to its material.",
                    [new("BakedMaterialMapAssignmentMismatch", input.Material.Identity.Key,
                        $"{map.Role} map does not apply to a primitive using this material: {primitiveKey}.")]);
        }
    }

    private static void ValidateMaterialInputs(
        IReadOnlyList<FbxMaterialExportInput> inputs, FbxGeometryExportOptions options, CancellationToken token,
        IReadOnlyList<MeshPrimitive> primitives)
    {
        if (inputs.Count > options.MaximumMaterials)
            throw Error("MaterialLimit", "The material input count exceeds the configured limit.",
                [new("MaterialLimit", "asset", $"Requested {inputs.Count}; limit is {options.MaximumMaterials}.")]);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        long textureCount = 0, texturePixels = 0;
        foreach (var input in inputs)
        {
            token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(input.Material);
            ArgumentNullException.ThrowIfNull(input.SubmeshReferences);
            ArgumentNullException.ThrowIfNull(input.Textures);
            ArgumentNullException.ThrowIfNull(input.BakedMaps);
            ArgumentNullException.ThrowIfNull(input.StagedMaps);
            ArgumentNullException.ThrowIfNull(input.BakingDiagnostics);
            ArgumentNullException.ThrowIfNull(input.GraphSources);
            ArgumentNullException.ThrowIfNull(input.ValidateSourceRevision);
            var material = input.Material;
            if (material.Identity.IndexContentId != material.Revision.IndexContentId)
                throw new ArgumentException("Material identity and revision belong to different indexes.", nameof(inputs));
            if (material.SurfaceGraph is { } graph) ValidateSurfaceGraph(graph, material.Identity.Key);
            if (!identities.Add(material.Identity.Key))
                throw Error("MaterialIdentityDuplicate", "Material inputs must have unique identities.",
                    [new("MaterialIdentityDuplicate", material.Identity.Key, "Supply one typed material input and join list per identity.")]);
            var joinedReferences = new HashSet<ResourceReference>();
            foreach (var join in input.SubmeshReferences)
            {
                token.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(join);
                ArgumentNullException.ThrowIfNull(join.Reference);
                ArgumentNullException.ThrowIfNull(join.Resolution);
                if (!joinedReferences.Add(join.Reference))
                    throw Error("MaterialJoinDuplicate", "A material input repeats a source reference join.",
                        [new("MaterialJoinDuplicate", material.Identity.Key, ReferenceText(join.Reference), [join.Reference])]);
                if (join.Resolution.Status == ReferenceStatus.Unique &&
                    (join.Resolution.Candidates.Count != 1 || join.Resolution.Candidates[0] != material.Identity))
                    throw Error("MaterialJoinIdentityMismatch", "A unique submesh-material resolution contradicts the supplied material identity.",
                        [new("MaterialJoinIdentityMismatch", material.Identity.Key, ReferenceText(join.Reference), [join.Reference])]);
            }
            var bindings = new Dictionary<int, MaterialTextureBinding>();
            foreach (var binding in material.Bindings)
            {
                ArgumentNullException.ThrowIfNull(binding.Texture);
                ArgumentNullException.ThrowIfNull(binding.Evidence);
                if (binding.SlotOrdinal < 0 || !bindings.TryAdd(binding.SlotOrdinal, binding))
                    throw Error("MaterialTextureSlotInvalid", "Material texture slot ordinals must be unique non-negative values.",
                        [new("MaterialTextureSlotInvalid", material.Identity.Key, $"Slot {binding.SlotOrdinal} is invalid or repeated.", [binding.Texture])]);
                if (binding.TextureCoordinateSet < 0 || !Enum.IsDefined(binding.Role) ||
                    !Finite(binding.UvScale) || !Finite(binding.UvOffset))
                    throw Error("MaterialUvTransformInvalid", "Material UV sets must be non-negative and transform values finite.",
                        [new("MaterialUvTransformInvalid", material.Identity.Key, $"Slot {binding.SlotOrdinal} has invalid UV metadata.", [binding.Texture])]);
                if (binding.UvScale.X == 0 || binding.UvScale.Y == 0)
                    throw Error("MaterialUvTransformSingular", "A constant texture coordinate cannot be represented by an invertible FBX texture transform.",
                        [new("MaterialUvTransformSingular", material.Identity.Key, $"Slot {binding.SlotOrdinal} has a zero UV scale.", [binding.Texture])]);
            }
            var suppliedSlots = new HashSet<int>();
            var allInputSlots = new HashSet<int>();
            var bakedSourceSlots = input.AllMaps.SelectMany(map => map.Sources ?? [])
                .Select(source => source.SlotOrdinal).ToHashSet();
            var graphTextureSlots = material.SurfaceGraph?.TextureSlots.ToHashSet() ?? [];
            foreach (var texture in input.Textures)
            {
                token.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(texture);
                ArgumentNullException.ThrowIfNull(texture.Reference);
                ArgumentNullException.ThrowIfNull(texture.Resolution);
                ArgumentNullException.ThrowIfNull(texture.Document);
                ArgumentNullException.ThrowIfNull(texture.Surface);
                ArgumentNullException.ThrowIfNull(texture.ValidateSourceRevision);
                if (texture.SlotOrdinal < 0 || !allInputSlots.Add(texture.SlotOrdinal))
                    throw Error("MaterialTextureSlotMismatch", "Texture source slot ordinals must be unique non-negative values.",
                        [new("MaterialTextureSlotMismatch", material.Identity.Key, $"Slot {texture.SlotOrdinal} is invalid or repeated.")]);
                textureCount = checked(textureCount + 1);
                if (textureCount > options.MaximumTextures)
                    throw Error("TextureLimit", "The material texture input count exceeds the configured limit.",
                        [new("TextureLimit", material.Identity.Key, $"Limit is {options.MaximumTextures}.")]);
                var hasBinding = bindings.TryGetValue(texture.SlotOrdinal, out var binding);
                if ((hasBinding && !suppliedSlots.Add(texture.SlotOrdinal)) ||
                    (!hasBinding && !bakedSourceSlots.Contains(texture.SlotOrdinal) && !graphTextureSlots.Contains(texture.SlotOrdinal)))
                    throw Error("MaterialTextureSlotMismatch", "A texture input must match one material binding, graph slot, or baked source slot.",
                        [new("MaterialTextureSlotMismatch", material.Identity.Key, $"Slot {texture.SlotOrdinal} is missing or repeated.")]);
                if (graphTextureSlots.Contains(texture.SlotOrdinal) &&
                    (texture.SlotOrdinal >= material.References.Count || material.References[texture.SlotOrdinal] != texture.Reference))
                    throw Error("MaterialGraphTextureJoinMismatch", "A graph texture slot must join to the exact ordered material reference.",
                        [new("MaterialGraphTextureJoinMismatch", material.Identity.Key,
                            $"Slot {texture.SlotOrdinal} does not match Material.References[{texture.SlotOrdinal}].", [texture.Reference])]);
                if (hasBinding && texture.Reference != binding!.Texture)
                    throw Error("MaterialTextureReferenceMismatch", "A texture input reference does not match its material slot binding.",
                        [new("MaterialTextureReferenceMismatch", material.Identity.Key,
                            $"Slot {texture.SlotOrdinal} does not match its exact typed source reference.", [binding!.Texture, texture.Reference])]);
                if (texture.Document.Identity is not { } textureIdentity || texture.Document.Revision is not { } documentRevision ||
                    texture.ExpectedRevision is not { } expectedRevision)
                    throw Error("TextureIdentityUnavailable", "Material textures require exact source identity and revision metadata.",
                    [new("TextureIdentityUnavailable", material.Identity.Key, $"Slot {texture.SlotOrdinal} has no document identity or expected revision.", [texture.Reference])]);
                if (textureIdentity.IndexContentId != documentRevision.IndexContentId ||
                    expectedRevision != documentRevision || texture.Surface.Identity != textureIdentity ||
                    texture.Surface.Revision != documentRevision ||
                    !StringComparer.Ordinal.Equals(texture.Surface.SourcePayloadSha256, texture.Document.ContentSha256) ||
                    texture.Surface.SourceFormat != texture.Document.Info.Format ||
                    texture.Surface.SourceFormatName != texture.Document.Info.FormatName ||
                    texture.Surface.SourceIsHdr != texture.Document.Info.IsHdr)
                    throw Error("TextureSurfaceIdentityMismatch", "Material texture document, decoded pixels, and revision metadata disagree.",
                        [new("TextureSurfaceIdentityMismatch", material.Identity.Key, $"Slot {texture.SlotOrdinal} surface is not from the supplied texture revision.", [texture.Reference])]);
                var joinedIdentity = textureIdentity;
                if (texture.DocumentJoin is { } documentJoin)
                {
                    if (documentJoin.Evidence?.Verified != true || documentJoin.DocumentIdentity != textureIdentity ||
                        documentJoin.Revision != documentRevision || documentJoin.ResolvedIdentity.IndexContentId != textureIdentity.IndexContentId ||
                        documentJoin.ResolvedIdentity.Ordinal != textureIdentity.Ordinal)
                        throw Error("TextureDocumentJoinInvalid", "The native texture-to-document join is not qualified for this source revision.",
                            [new("TextureDocumentJoinInvalid", material.Identity.Key, $"Slot {texture.SlotOrdinal} document join does not match its decoded metadata.", [texture.Reference])]);
                    joinedIdentity = documentJoin.ResolvedIdentity;
                }
                if (texture.Resolution.Status != ReferenceStatus.Unique || texture.Resolution.Candidates.Count != 1 ||
                    texture.Resolution.Candidates[0] != joinedIdentity)
                    throw Error("TextureResolutionUnavailable", "A supplied texture surface must have a unique reference-to-identity join.",
                        [new("TextureResolutionUnavailable", material.Identity.Key, $"Slot {texture.SlotOrdinal} is not uniquely resolved.", [texture.Reference])]);
                var selectedExtent = texture.Document.Subresources.FirstOrDefault(extent => extent.Selection == texture.Surface.Subresource);
                if (selectedExtent is null || texture.Surface.Subresource.MipIndex != texture.Surface.MipIndex ||
                    texture.Surface.Width != selectedExtent.Width || texture.Surface.Height != selectedExtent.Height ||
                    (texture.Surface.IsHdr ? texture.Surface.HdrPixels.Length != checked((long)texture.Surface.Width * texture.Surface.Height) :
                        texture.Surface.Rgba8.Length != checked((long)texture.Surface.Width * texture.Surface.Height * 4)))
                    throw Error("TextureSurfaceSubresourceMismatch", "The selected surface does not match its exact DDS subresource.",
                        [new("TextureSurfaceSubresourceMismatch", material.Identity.Key,
                            $"Slot {texture.SlotOrdinal} selected {texture.Surface.Subresource} at {texture.Surface.Width}x{texture.Surface.Height}.", [texture.Reference])]);
                if (texture.Surface.DisplayTransform is { EncodedSrgb: true } && !texture.Surface.IsSrgb)
                    throw Error("TextureSurfaceColorSpaceMismatch", "Tone-mapped PNG pixels must be marked sRGB.",
                        [new("TextureSurfaceColorSpaceMismatch", material.Identity.Key, $"Slot {texture.SlotOrdinal} has inconsistent display-transform metadata.", [texture.Reference])]);
                var pixels = checked((long)texture.Surface.Width * texture.Surface.Height);
                texturePixels = checked(texturePixels + pixels);
                if (texturePixels > options.MaximumTexturePixels)
                    throw Error("TexturePixelLimit", "The aggregate material texture pixel count exceeds the configured limit.",
                        [new("TexturePixelLimit", material.Identity.Key, $"Requested {texturePixels}; limit is {options.MaximumTexturePixels}.", [texture.Reference])]);
            }
            foreach (var source in input.GraphSources)
            {
                token.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(source);
                ArgumentNullException.ThrowIfNull(source.ValidateSourceRevision);
                if (source.SlotOrdinal < 0 || !allInputSlots.Add(source.SlotOrdinal) ||
                    source.SlotOrdinal >= material.References.Count || material.References[source.SlotOrdinal] != source.Reference ||
                    !graphTextureSlots.Contains(source.SlotOrdinal) || source.Evidence?.Verified != true ||
                    source.NativeIdentity.IndexContentId != source.Revision.IndexContentId ||
                    source.DocumentIdentity.IndexContentId != source.Revision.IndexContentId ||
                    source.NativeIdentity.Ordinal != source.DocumentIdentity.Ordinal || source.Revision != material.Revision ||
                    source.Resolution is not { Status: ReferenceStatus.Unique, Candidates.Count: 1 } ||
                    source.Resolution.Candidates[0] != source.NativeIdentity || !IsSha256(source.DocumentSha256) ||
                    source.Width is < 1 or > DdsTextureReader.MaximumDimension || source.Height is < 1 or > DdsTextureReader.MaximumDimension ||
                    source.Subresource.ArrayLayer < 0 || source.Subresource.MipIndex < 0 ||
                    source.Subresource.Face is { } face && !Enum.IsDefined(face))
                    throw Error("MaterialGraphTextureJoinMismatch", "A sampled texture fingerprint must match its ordered material source slot.",
                        [new("MaterialGraphTextureJoinMismatch", material.Identity.Key, $"Slot {source.SlotOrdinal} has invalid source metadata.")]);
                textureCount = checked(textureCount + 1);
                texturePixels = checked(texturePixels + (long)source.Width * source.Height);
                if (textureCount > options.MaximumTextures || texturePixels > options.MaximumTexturePixels)
                    throw Error("TexturePixelLimit", "Sampled source metadata exceeds the configured texture limits.",
                        [new("TexturePixelLimit", material.Identity.Key, "The sampled source count or aggregate pixel extent exceeds the export limit.")]);
            }
            foreach (var slot in graphTextureSlots)
                if (slot < 0 || slot >= material.References.Count || !allInputSlots.Contains(slot))
                    throw Error("MaterialGraphTextureInputMissing", "Every graph-declared texture slot must have an exactly joined texture input.",
                        [new("MaterialGraphTextureInputMissing", material.Identity.Key,
                            $"Graph slot {slot} is outside the ordered reference list or has no texture input.")]);
            ValidateMaterialMaps(input, primitives, options);
            foreach (var map in input.AllMaps)
            {
                if (map is not FbxStagedMaterialMapInput)
                    texturePixels = checked(texturePixels + (long)map.Width * map.Height);
                if (texturePixels > options.MaximumTexturePixels)
                    throw Error("TexturePixelLimit", "The aggregate source and baked material texture pixels exceed the configured limit.",
                        [new("TexturePixelLimit", material.Identity.Key,
                            $"Requested {texturePixels}; limit is {options.MaximumTexturePixels}.")]);
            }
            if (input.BakingDiagnostics.Count > 1024 || input.BakingDiagnostics.Any(message => message is null || message.Length > 4096))
                throw Error("MaterialBakeDiagnosticLimit", "Bake diagnostic metadata exceeds the configured bound.",
                    [new("MaterialBakeDiagnosticLimit", material.Identity.Key, "Bake diagnostics are limited to 1024 messages of 4096 characters each.")]);
        }
    }

    private static void ValidateMaterialMaps(FbxMaterialExportInput input,
        IReadOnlyList<MeshPrimitive> primitives, FbxGeometryExportOptions options)
    {
        var material = input.Material;
        var primitiveKeys = primitives.Select(primitive => primitive.Key).ToHashSet(StringComparer.Ordinal);
        var sourceTextures = input.Textures.ToDictionary(texture => texture.SlotOrdinal);
        var sampledSources = input.GraphSources.ToDictionary(source => source.SlotOrdinal);
        var rolesByPrimitive = new HashSet<(string PrimitiveKey, MaterialTextureRole Role)>();
        foreach (var map in input.AllMaps)
        {
            ArgumentNullException.ThrowIfNull(map);
            ArgumentNullException.ThrowIfNull(map.PrimitiveKeys);
            ArgumentNullException.ThrowIfNull(map.Sources);
            ArgumentNullException.ThrowIfNull(map.ValidateSources);
            if (map.Role is not (MaterialTextureRole.BaseColor or MaterialTextureRole.Opacity))
                throw Error("BakedMaterialMapRoleUnsupported", "Only baked base-color and opacity maps can be connected safely.",
                    [new("BakedMaterialMapRoleUnsupported", material.Identity.Key, $"Role {map.Role} is unsupported for derived maps.")]);
            if (map.TextureCoordinateSet < 0 || !IsSha256(map.GraphSha256))
                throw Error("MaterialMapMetadataInvalid", "A material map requires a valid UV set and graph SHA-256.",
                    [new("MaterialMapMetadataInvalid", material.Identity.Key, $"Role {map.Role}, UV {map.TextureCoordinateSet}, graph hash {map.GraphSha256}.")]);
            if (map.Identity != material.Identity || map.Revision != material.Revision || !IsSha256(map.DdsSha256) ||
                map.Width is < 1 or > DdsTextureReader.MaximumDimension || map.Height is < 1 or > DdsTextureReader.MaximumDimension)
                throw Error("MaterialMapIdentityMismatch", "A derived map must retain its material identity, archive revision and valid DDS dimensions/hash.",
                    [new("MaterialMapIdentityMismatch", material.Identity.Key,
                        $"{map.Role} map metadata does not match {IdentityText(material.Identity)} at {RevisionText(material.Revision)}.")]);
            if (map.Role == MaterialTextureRole.Opacity && map.IsSrgb)
                throw Error("MaterialMapColorSpaceInvalid", "Opacity maps must encode linear transparency.",
                    [new("MaterialMapColorSpaceInvalid", material.Identity.Key, "The opacity map is marked sRGB.")]);
            if (map is FbxBakedMaterialMapInput baked)
            {
                ArgumentNullException.ThrowIfNull(baked.Document);
                ArgumentNullException.ThrowIfNull(baked.Surface);
                if (baked.Surface.Identity != material.Identity || baked.Surface.Revision != material.Revision ||
                    !StringComparer.Ordinal.Equals(baked.Document.ContentSha256, baked.Surface.SourcePayloadSha256) ||
                    baked.Surface.SourceFormat != baked.Document.Info.Format || baked.Surface.SourceFormatName != baked.Document.Info.FormatName ||
                    baked.Surface.SourceIsHdr != baked.Document.Info.IsHdr || baked.Surface.IsHdr)
                    throw Error("BakedMaterialMapSurfaceIdentityMismatch", "The baked pixels must match their derived DDS and material revision.",
                        [new("BakedMaterialMapSurfaceIdentityMismatch", material.Identity.Key,
                            $"{map.Role} surface does not match the material-qualified DDS document.")]);
                var selectedExtent = baked.Document.Subresources.FirstOrDefault(extent => extent.Selection == baked.Surface.Subresource);
                if (selectedExtent is null || baked.Surface.MipIndex != baked.Surface.Subresource.MipIndex ||
                    baked.Surface.Width != selectedExtent.Width || baked.Surface.Height != selectedExtent.Height ||
                    baked.Surface.Rgba8.Length != checked((long)baked.Surface.Width * baked.Surface.Height * 4))
                    throw Error("BakedMaterialMapSurfaceInvalid", "The baked pixels do not match the selected derived DDS subresource.",
                        [new("BakedMaterialMapSurfaceInvalid", material.Identity.Key,
                            $"{map.Role} selected {baked.Surface.Subresource} at {baked.Surface.Width}x{baked.Surface.Height}.")]);
            }
            else if (map is FbxStagedMaterialMapInput staged)
            {
        if (string.IsNullOrWhiteSpace(staged.Path) || !Path.IsPathFullyQualified(staged.Path) ||
                    staged.Length <= 0 || staged.Length > options.MaximumMediaBytes || !IsSha256(staged.PngSha256))
                    throw Error("StagedMaterialMapMetadataInvalid", "A staged map requires a fully qualified source path, bounded length and PNG SHA-256.",
                        [new("StagedMaterialMapMetadataInvalid", material.Identity.Key,
                            $"{map.Role} staged PNG path, length or hash is invalid.")]);
            }
            else
                throw Error("MaterialMapTypeUnsupported", "The supplied material map type is not supported by this exporter.",
                    [new("MaterialMapTypeUnsupported", material.Identity.Key, map.GetType().Name)]);
            if (map.PrimitiveKeys.Count is < 1 || map.PrimitiveKeys.Count > options.MaximumSubmeshes ||
                map.PrimitiveKeys.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 4096 || !primitiveKeys.Contains(key)) ||
                map.PrimitiveKeys.Distinct(StringComparer.Ordinal).Count() != map.PrimitiveKeys.Count)
                throw Error("BakedMaterialMapPrimitiveScopeInvalid", "A baked map must name unique existing primitives within the configured bound.",
                    [new("BakedMaterialMapPrimitiveScopeInvalid", material.Identity.Key, $"{map.Role} map has an invalid primitive scope.")]);
            if (map.PolygonCornerCoordinates is { } cornerUvs)
            {
                var primitive = map.PrimitiveKeys.Count == 1 ? primitives.Single(item => item.Key == map.PrimitiveKeys[0]) : null;
                if (primitive is null || map.TextureCoordinateSet != (primitive.TextureCoordinates?.Count ?? 0) ||
                    cornerUvs.Count != primitive.Indices.Count || cornerUvs.Any(uv => !Finite(uv) || uv.X < 0 || uv.Y < 0 || uv.X > 1 || uv.Y > 1))
                    throw Error("BakedCornerUvInvalid", "An atlas requires one normalized UV per polygon corner and one appended UV set.",
                        [new("BakedCornerUvInvalid", material.Identity.Key, "Atlas UV scope, count or coordinates are invalid.")]);
            }
            if (map.Sources.Count > 4096)
                throw Error("MaterialMapSourceLimit", "A material map supports at most 4096 source records.",
                    [new("MaterialMapSourceLimit", material.Identity.Key, $"{map.Role} has {map.Sources.Count} source records.")]);
            var uniqueSources = new HashSet<(int SlotOrdinal, TextureSubresourceSelection Subresource)>();
            foreach (var source in map.Sources)
            {
                ArgumentNullException.ThrowIfNull(source);
                if (!sourceTextures.TryGetValue(source.SlotOrdinal, out var texture))
                {
                    if (sampledSources.TryGetValue(source.SlotOrdinal, out var sampled) && source.Identity == sampled.NativeIdentity &&
                        source.Revision == sampled.Revision && source.PayloadSha256 == sampled.DocumentSha256 &&
                        source.Subresource == sampled.Subresource && uniqueSources.Add((source.SlotOrdinal, source.Subresource))) continue;
                    throw Error("MaterialMapSourceMismatch", "A map source must match an original native texture input and selected subresource.",
                        [new("BakedMaterialMapSourceMismatch", material.Identity.Key,
                            $"Slot {source.SlotOrdinal} has no supplied native texture input.")]);
                }
                var nativeIdentity = texture.DocumentJoin?.ResolvedIdentity ?? texture.Document.Identity;
                var nativeRevision = texture.DocumentJoin?.Revision ?? texture.Document.Revision;
                if (source.SlotOrdinal < 0 || source.Identity.IndexContentId != source.Revision.IndexContentId ||
                    !IsSha256(source.PayloadSha256) || !uniqueSources.Add((source.SlotOrdinal, source.Subresource)) ||
                    nativeIdentity != source.Identity || nativeRevision != source.Revision ||
                    !StringComparer.Ordinal.Equals(texture.Document.ContentSha256, source.PayloadSha256) ||
                    texture.Surface.Subresource != source.Subresource)
                    throw Error("MaterialMapSourceMismatch", "A map source must match an original native texture input and selected subresource.",
                        [new("BakedMaterialMapSourceMismatch", material.Identity.Key,
                            $"Slot {source.SlotOrdinal} source identity, revision, payload hash, or subresource disagrees with the supplied native texture input.")]);
            }
            foreach (var primitiveKey in map.PrimitiveKeys)
            {
                if (!rolesByPrimitive.Add((primitiveKey, map.Role)))
                    throw Error("BakedMaterialMapRoleDuplicate", "A primitive cannot receive duplicate baked maps for the same role.",
                        [new("BakedMaterialMapRoleDuplicate", material.Identity.Key, $"Primitive {primitiveKey} has more than one {map.Role} map.")]);
            }
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static void ValidateSurfaceGraph(MaterialSurfaceGraph graph, string subject)
    {
        if (graph.RequiredInputs.Count > 64 || graph.TextureSlots.Count > 4096 || graph.Diagnostics.Count > 1024)
            throw Error("MaterialGraphLimit", "The surface graph metadata exceeds the configured bound.",
                [new("MaterialGraphLimit", subject, "Inputs, texture slots, or diagnostics exceed the graph metadata limit.")]);
        var pending = new Stack<(MaterialExpression Node, int Depth)>();
        var visited = new HashSet<MaterialExpression>(ReferenceEqualityComparer.Instance);
        foreach (var root in new[] { graph.BaseColor, graph.Opacity, graph.AlphaCutoff })
            if (root is not null) pending.Push((root, 1));
        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            if (!visited.Add(node)) continue;
            if (visited.Count > 4096 || depth > 64 || node.Arguments.Count > 16 ||
                node.Constants is { } constants && (constants.Count > 16 || constants.Any(value => !float.IsFinite(value))))
                throw Error("MaterialGraphLimit", "The surface graph exceeds its node, depth, or constant bound.",
                    [new("MaterialGraphLimit", subject, "Graphs are limited to 4096 nodes, depth 64, and 16 constants per node.")]);
            foreach (var child in node.Arguments) pending.Push((child, depth + 1));
        }
    }

    private static void ValidateMaterialInputBounds(IReadOnlyList<FbxMaterialExportInput> inputs,
        FbxGeometryExportOptions options, IReadOnlyList<MeshPrimitive> primitives)
    {
        if (inputs.Count > options.MaximumMaterials)
            throw Error("MaterialLimit", "The material input count exceeds the configured limit.",
                [new("MaterialLimit", "asset", $"Requested {inputs.Count}; limit is {options.MaximumMaterials}.")]);
        long textureInputs = 0, bindings = 0, materialMaps = 0, stagedMapBytes = 0, mapSources = 0,
            mapPrimitiveScopes = 0, referenceJoins = 0, pixels = 0;
        var decodedImages = new HashSet<(string Hash, TextureSubresourceSelection Subresource, int Width, int Height)>();
        var stagedImages = new HashSet<(string Hash, int Width, int Height)>();
        var primitiveKeys = primitives.Select(primitive => primitive.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(input.Material);
            ArgumentNullException.ThrowIfNull(input.Textures);
            ArgumentNullException.ThrowIfNull(input.BakedMaps);
            ArgumentNullException.ThrowIfNull(input.StagedMaps);
            ArgumentNullException.ThrowIfNull(input.BakingDiagnostics);
            ArgumentNullException.ThrowIfNull(input.GraphSources);
            ArgumentNullException.ThrowIfNull(input.SubmeshReferences);
            ArgumentNullException.ThrowIfNull(input.ValidateSourceRevision);
            ArgumentNullException.ThrowIfNull(input.Material.Bindings);
            ArgumentNullException.ThrowIfNull(input.Material.References);
            ArgumentNullException.ThrowIfNull(input.Material.Diagnostics);
            if (input.Material.Profile.Length > 1024 || input.Material.Diagnostics.Count > 1024)
                throw Error("MaterialMetadataLimit", "Material profile or diagnostic metadata exceeds the configured bound.",
                    [new("MaterialMetadataLimit", input.Material.Identity.Key, "Material text fields are limited to 1024 characters and 1024 diagnostics.")]);
            textureInputs = checked(textureInputs + input.Textures.Count + input.GraphSources.Count);
            var maps = input.AllMaps.ToArray();
            materialMaps = checked(materialMaps + maps.Length);
            bindings = checked(bindings + input.Material.Bindings.Count);
            referenceJoins = checked(referenceJoins + input.SubmeshReferences.Count);
            foreach (var texture in input.Textures)
            {
                ArgumentNullException.ThrowIfNull(texture);
                ArgumentNullException.ThrowIfNull(texture.Surface);
                if (texture.Surface.Width <= 0 || texture.Surface.Height <= 0)
                    throw new ArgumentOutOfRangeException(nameof(inputs), "Texture surface dimensions must be positive.");
                if (decodedImages.Add((texture.Document.ContentSha256, texture.Surface.Subresource, texture.Surface.Width, texture.Surface.Height)))
                    pixels = checked(pixels + (long)texture.Surface.Width * texture.Surface.Height);
            }
            foreach (var source in input.GraphSources)
            {
                ArgumentNullException.ThrowIfNull(source);
                if (source.Width is < 1 or > DdsTextureReader.MaximumDimension || source.Height is < 1 or > DdsTextureReader.MaximumDimension)
                    throw new ArgumentOutOfRangeException(nameof(inputs), "Sampled texture dimensions exceed the texture limits.");
                // Graph fingerprints retain metadata after their decoded image has been released.
            }
            foreach (var map in maps)
            {
                ArgumentNullException.ThrowIfNull(map);
                ArgumentNullException.ThrowIfNull(map.PrimitiveKeys);
                ArgumentNullException.ThrowIfNull(map.Sources);
                if (map.Width is < 1 or > DdsTextureReader.MaximumDimension || map.Height is < 1 or > DdsTextureReader.MaximumDimension)
                    throw new ArgumentOutOfRangeException(nameof(inputs), "Material map dimensions must be positive and within DDS limits.");
                if (map.PrimitiveKeys.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 4096 || !primitiveKeys.Contains(key)) ||
                    map.PrimitiveKeys.Distinct(StringComparer.Ordinal).Count() != map.PrimitiveKeys.Count)
                    throw Error("BakedMaterialMapPrimitiveScopeInvalid", "A baked map must name unique existing primitives within the configured bound.",
                        [new("BakedMaterialMapPrimitiveScopeInvalid", input.Material.Identity.Key, "Primitive scope contains an unknown or repeated key.")]);
                if (map is not FbxStagedMaterialMapInput &&
                    decodedImages.Add((map.DdsSha256, new(0,null,0), map.Width, map.Height)))
                    pixels = checked(pixels + (long)map.Width * map.Height);
                mapSources = checked(mapSources + map.Sources.Count);
                mapPrimitiveScopes = checked(mapPrimitiveScopes + map.PrimitiveKeys.Count);
                if (map is FbxStagedMaterialMapInput staged)
                {
                    if (staged.Length <= 0 || !IsSha256(staged.PngSha256) || string.IsNullOrWhiteSpace(staged.Path))
                        throw Error("StagedMaterialMapMetadataInvalid", "A staged map requires a nonempty PNG path, length and SHA-256.",
                            [new("StagedMaterialMapMetadataInvalid", input.Material.Identity.Key, "Staged PNG source metadata is invalid.")]);
                    if (stagedImages.Add((staged.PngSha256, staged.Width, staged.Height)))
                        stagedMapBytes = checked(stagedMapBytes + staged.Length);
                }
            }
        }
        if (textureInputs + materialMaps > options.MaximumTextures || bindings > options.MaximumTextures || referenceJoins > options.MaximumSubmeshes)
            throw Error("MaterialTextureLimit", "Material references, bindings, or texture inputs exceed configured limits.",
                [new("MaterialTextureLimit", "materials",
                    $"References {referenceJoins}/{options.MaximumSubmeshes}; bindings {bindings}/{options.MaximumTextures}; source and derived maps {textureInputs + materialMaps}/{options.MaximumTextures}.")]);
        if (pixels > options.MaximumTexturePixels)
            throw Error("TexturePixelLimit", "Aggregate decoded material texture pixels exceed the configured limit.",
                [new("TexturePixelLimit", "materials", $"Requested {pixels}; limit is {options.MaximumTexturePixels}.")]);
        if (stagedMapBytes > options.MaximumMediaBytes || mapSources > Math.Max(1L, options.MaximumTextures) * 4 ||
            mapPrimitiveScopes > options.MaximumMaterialReferences)
            throw Error("MaterialMapMetadataLimit", "Derived map media, provenance, or primitive scopes exceed the configured bound.",
                [new("MaterialMapMetadataLimit", "materials",
                    $"Staged bytes {stagedMapBytes}/{options.MaximumMediaBytes}; source records {mapSources}/{Math.Max(1L, options.MaximumTextures) * 4}; primitive scopes {mapPrimitiveScopes}/{options.MaximumMaterialReferences}.")]);
    }

    private static Dictionary<ResourceReference, FbxMaterialJoinPlan> BuildMaterialReferenceMap(
        IReadOnlyList<FbxMaterialExportInput> inputs)
    {
        var result = new Dictionary<ResourceReference, FbxMaterialJoinPlan>();
        foreach (var input in inputs)
        foreach (var join in input.SubmeshReferences)
        {
            var current = new FbxMaterialJoinPlan(join.Resolution.Status,
                join.Resolution.Status == ReferenceStatus.Unique ? input : null);
            if (!result.TryGetValue(join.Reference, out var previous))
            { result.Add(join.Reference, current); continue; }
            if (previous.MaterialInput is { } previousInput && current.MaterialInput is { } currentInput)
            {
                if (previousInput.Material.Identity != currentInput.Material.Identity)
                    throw Error("MaterialJoinConflict", "A source material reference uniquely resolves to more than one supplied material.",
                        [new("MaterialJoinConflict", ReferenceText(join.Reference), "The typed join table contains conflicting unique destinations.", [join.Reference])]);
                continue;
            }
            result[join.Reference] = new(ReferenceStatus.Ambiguous, null);
        }
        return result;
    }

    private static void ValidateMaterialUvSets(FbxPlan plan)
    {
        foreach (var material in plan.Materials.Where(item => item.MaterialInput is not null))
        {
            foreach (var binding in material.MaterialInput!.Material.Bindings)
            {
                if (!material.MaterialInput.Textures.Any(item => item.SlotOrdinal == binding.SlotOrdinal)) continue;
                if (material.HasBakedBaseColor && binding.Role == MaterialTextureRole.BaseColor) continue;
                foreach (var primitive in plan.Geometries.Values.Where(item => item.MaterialKeys.Contains(material.Key, StringComparer.Ordinal)))
                {
                    if (primitive.Primitive.TextureCoordinates is not { } sets || binding.TextureCoordinateSet >= sets.Count)
                        throw Error("MaterialUvSetUnavailable", "A material binding selects a UV set absent from assigned geometry.",
                            [new("MaterialUvSetUnavailable", material.Name,
                                $"Slot {binding.SlotOrdinal} requests UV set {binding.TextureCoordinateSet} for primitive {primitive.Primitive.Key}.", [binding.Texture])]);
                }
            }
            if (material.PrimitiveKey is not { } primitiveKey) continue;
            var geometryPlan = plan.Geometries[primitiveKey];
            var geometry = geometryPlan.Primitive;
            foreach (var map in material.MaterialInput.AllMaps.Where(map => map.PrimitiveKeys.Contains(primitiveKey, StringComparer.Ordinal)))
            {
                if (map.PolygonCornerCoordinates is { } cornerUvs)
                {
                    if (geometryPlan.CornerUvSets.TryGetValue(map.TextureCoordinateSet, out var existing) && !existing.SequenceEqual(cornerUvs))
                        throw Error("BakedCornerUvConflict", "Colour and coverage maps must use the same atlas coordinates.",
                            [new("BakedCornerUvConflict", primitiveKey, "Atlas UV values disagree for the same appended set.")]);
                    geometryPlan.CornerUvSets[map.TextureCoordinateSet] = cornerUvs;
                    continue;
                }
                if (geometry.TextureCoordinates is not { } sets || map.TextureCoordinateSet >= sets.Count)
                    throw Error("BakedMaterialUvSetUnavailable", "A baked map selects a UV set absent from its assigned primitive.",
                        [new("BakedMaterialUvSetUnavailable", material.Name,
                            $"{map.Role} requests UV set {map.TextureCoordinateSet} for primitive {primitiveKey}.")]);
            }
        }
    }

    private static async Task<FbxMediaPlan> StageMaterialMediaAsync(FbxModelExportInput input, FbxPlan plan,
        string sourceRoot, string stageRoot, string outputPath, FbxGeometryExportOptions options,
        List<FbxGeometryExportLoss> losses, CancellationToken token)
    {
        var textures = new List<FbxTexturePlan>();
        var videos = new Dictionary<string, FbxVideoPlan>(StringComparer.Ordinal);
        var reports = new List<FbxMaterialTextureExportResult>();
        var derivedReports = new List<FbxBakedMaterialMapExportResult>();
        var stagedBindings = new Dictionary<(string MaterialIdentity, int Slot), FbxStagedTexture>();
        var reportedGraph = new HashSet<string>(StringComparer.Ordinal);
        var reportedBakeDiagnostics = new HashSet<string>(StringComparer.Ordinal);
        var reportedMaterialMaps = new HashSet<string>(StringComparer.Ordinal);
        var nextId = plan.NextId;
        long totalEncodedBytes = 0;
        var stageIndex = 0;

        foreach (var materialPlan in plan.Materials.Where(item => item.MaterialInput is not null))
        {
            token.ThrowIfCancellationRequested();
            var materialInput = materialPlan.MaterialInput!;
            var material = materialInput.Material;
            var textureBySlot = materialInput.Textures.ToDictionary(item => item.SlotOrdinal);
            foreach (var binding in material.Bindings)
            {
                token.ThrowIfCancellationRequested();
                if (materialPlan.HasBakedBaseColor && binding.Role == MaterialTextureRole.BaseColor)
                {
                    if (textureBySlot.TryGetValue(binding.SlotOrdinal, out var bakedSource))
                        losses.Add(new("MaterialTextureSupersededByBake", material.Identity.Key,
                            $"Direct base-color slot {binding.SlotOrdinal} remains in provenance but is replaced by the qualified baked map for primitive {materialPlan.PrimitiveKey}.",
                            [bakedSource.Reference]));
                    continue;
                }
                if (!textureBySlot.TryGetValue(binding.SlotOrdinal, out var textureInput))
                {
                    losses.Add(new("MaterialTextureUnavailable", material.Identity.Key,
                        $"Texture slot {binding.SlotOrdinal} ({binding.Role}) has no uniquely resolved decoded image input.", [binding.Texture]));
                    continue;
                }

                var cacheKey = (IdentityText(material.Identity), binding.SlotOrdinal);
                if (!stagedBindings.TryGetValue(cacheKey, out var staged))
                {
                    var surface = textureInput.Surface;
                    var format = surface.IsHdr ? AssetExportFormat.Dds : AssetExportFormat.Png;
                    var extension = surface.IsHdr ? ".dds" : ".png";
                    using var scratchReservation = input.ReserveMemory(surface.IsHdr ? 128 * 1024 : EstimatePngScratchBytes(surface),
                        "FBX material texture encoder scratch");
                    token.ThrowIfCancellationRequested();
                    var temporaryName = $"texture-{stageIndex++.ToString("D4", CultureInfo.InvariantCulture)}{extension}";
                    var result = textureInput.PublishPreparedMedia is { } publish
                        ? await publish(stageRoot, temporaryName, token).ConfigureAwait(false)
                        : await new TextureExportService().ExportAsync(textureInput.Document, surface,
                            format, sourceRoot, stageRoot, temporaryName, overwrite: false,
                            cancellationToken: token, expectedRevision: textureInput.ExpectedRevision,
                            preCommitValidation: textureInput.ValidateSourceRevision).ConfigureAwait(false);
                    var stagedPath = Path.Combine(stageRoot, temporaryName);
                    var writtenHash = await HashFileAsync(stagedPath, token).ConfigureAwait(false);
                    var info = new FileInfo(stagedPath);
                    if (!info.Exists || info.Length != result.Length || !StringComparer.Ordinal.Equals(writtenHash, result.Sha256) ||
                        (surface.IsHdr ? !StringComparer.Ordinal.Equals(result.Sha256, textureInput.Document.ContentSha256) :
                            result.SelectedMip != surface.MipIndex || result.SelectedSubresource != surface.Subresource))
                        throw Error("TextureExportIntegrity", "The staged texture did not match its encoder result or source selection.",
                            [new("TextureExportIntegrity", material.Identity.Key, $"Slot {binding.SlotOrdinal} encoded output hash or length mismatch.", [binding.Texture])]);
                    totalEncodedBytes = checked(totalEncodedBytes + result.Length);
                    if (totalEncodedBytes > options.MaximumMediaBytes)
                        throw Error("TextureMediaByteLimit", "The encoded material texture files exceed the configured media byte limit.",
                            [new("TextureMediaByteLimit", material.Identity.Key, $"Requested {totalEncodedBytes}; limit is {options.MaximumMediaBytes}.", [binding.Texture])]);
                    var relativePath = options.MediaPackaging == FbxMediaPackaging.Embedded
                        ? $"textures/{result.Sha256}{extension}"
                        : $"{Path.GetFileName(outputPath)}.assets/textures/{result.Sha256}{extension}";
                    staged = new(stagedPath, relativePath, result);
                    stagedBindings.Add(cacheKey, staged);
                }

                if (!videos.TryGetValue(staged.Result.Sha256, out var video))
                {
                    video = new(nextId++, "FbxVideo_" + staged.Result.Sha256[..20], staged.RelativePath, staged.StagePath, staged.Result);
                    videos.Add(staged.Result.Sha256, video);
                }
                var property = MaterialProperty(binding.Role);
                var texture = new FbxTexturePlan(nextId++, materialPlan, binding, textureInput, null, video,
                    property, property);
                textures.Add(texture);
                reports.Add(new(material.Identity, material.Revision, binding.SlotOrdinal, binding.Role,
                    binding.Texture, textureInput.Document.Identity!, textureInput.Document.Revision!,
                    textureInput.Document.ContentSha256, textureInput.Surface.Subresource,
                    binding.TextureCoordinateSet, binding.UvScale, binding.UvOffset,
                    video.RelativePath, video.Export.Length, video.Export.Sha256, video.Export.PixelFormat,
                    video.Export.SourceIsHdr, video.Export.OutputIsHdr, video.Export.SourceIsSrgb,
                    video.Export.OutputIsSrgb, video.Export.DisplayTransform, video.Export.Losses));
                if (!binding.Evidence.Verified)
                    losses.Add(new("MaterialBindingEvidenceUnverified", material.Identity.Key,
                        $"Slot {binding.SlotOrdinal} ({binding.Role}) is retained as a typed connection with unverified source evidence.",
                        [binding.Texture]));
                if (video.Export.Losses != "None; original encoded bytes copied exactly.")
                    losses.Add(new("TextureEncodingLoss", material.Identity.Key,
                        $"Slot {binding.SlotOrdinal} ({binding.Role}) PNG export: {video.Export.Losses}", [binding.Texture]));
                var expectedSrgb = binding.Role == MaterialTextureRole.BaseColor;
                if (!textureInput.Surface.IsHdr && textureInput.Surface.IsSrgb != expectedSrgb)
                    losses.Add(new("TextureColorSpaceRoleMismatch", material.Identity.Key,
                        $"Slot {binding.SlotOrdinal} role {binding.Role} retains source pixel color-space flag {(textureInput.Surface.IsSrgb ? "sRGB" : "linear")}.",
                        [binding.Texture]));
                if (binding.Role is MaterialTextureRole.Opacity or MaterialTextureRole.Roughness)
                    losses.Add(new("MaterialRoleCustomChannel", material.Identity.Key,
                        $"Slot {binding.SlotOrdinal} ({binding.Role}) is linked through a typed Deviant property because standard FBX material mapping is ambiguous.",
                        [binding.Texture]));
            }
            if (material.SurfaceGraph is { } graph && reportedGraph.Add(material.Identity.Key))
            {
                var operations = DescribeGraphOperations(graph);
                losses.Add(new("MaterialGraphApproximated", material.Identity.Key,
                    !materialInput.AllMaps.Any()
                        ? $"The stored surface graph is not evaluated; direct texture-role bindings are exported. Graph operations: {operations}."
                        : $"The exporter uses caller-supplied derived maps for their declared primitive scopes; it does not evaluate the stored graph itself. Graph operations: {operations}.",
                    material.References));
            }
            if (reportedBakeDiagnostics.Add(material.Identity.Key))
                foreach (var diagnostic in materialInput.BakingDiagnostics)
                    losses.Add(new("MaterialBakeUnsupported", material.Identity.Key, diagnostic, material.References));
            if (materialPlan.PrimitiveKey is { } primitiveKey)
            foreach (var map in materialInput.AllMaps.Where(map => map.PrimitiveKeys.Contains(primitiveKey, StringComparer.Ordinal)))
            {
                token.ThrowIfCancellationRequested();
                using var scratchReservation = input.ReserveMemory(EstimatePngScratchBytes(map.Width),
                    "FBX derived material map staging scratch");
                token.ThrowIfCancellationRequested();
                FbxStagedTexture stagedMap;
                if (map is FbxBakedMaterialMapInput bakedMap)
                {
                    var temporaryName = $"baked-{stageIndex++.ToString("D4", CultureInfo.InvariantCulture)}.png";
                    var bakedResult = await new TextureExportService().ExportAsync(bakedMap.Document, bakedMap.Surface,
                        AssetExportFormat.Png, sourceRoot, stageRoot, temporaryName, overwrite: false,
                        cancellationToken: token, expectedRevision: material.Revision,
                        preCommitValidation: bakedMap.ValidateSources).ConfigureAwait(false);
                    var bakedStagedPath = Path.Combine(stageRoot, temporaryName);
                    var writtenHash = await HashFileAsync(bakedStagedPath, token).ConfigureAwait(false);
                    var info = new FileInfo(bakedStagedPath);
                    if (!info.Exists || info.Length != bakedResult.Length || !StringComparer.Ordinal.Equals(writtenHash, bakedResult.Sha256) ||
                        bakedResult.SelectedMip != bakedMap.Surface.MipIndex || bakedResult.SelectedSubresource != bakedMap.Surface.Subresource)
                        throw Error("BakedTextureExportIntegrity", "The staged derived PNG did not match its encoder result or selected subresource.",
                            [new("BakedTextureExportIntegrity", material.Identity.Key,
                                $"{map.Role} map for {primitiveKey} has an output hash or subresource mismatch.")]);
                    var bakedRelativePath = options.MediaPackaging == FbxMediaPackaging.Embedded
                        ? $"textures/{bakedResult.Sha256}.png"
                        : $"{Path.GetFileName(outputPath)}.assets/textures/{bakedResult.Sha256}.png";
                    stagedMap = new(bakedStagedPath, bakedRelativePath, bakedResult);
                }
                else if (map is FbxStagedMaterialMapInput suppliedMap)
                {
                    var temporaryName = $"staged-{stageIndex++.ToString("D4", CultureInfo.InvariantCulture)}.png";
                    stagedMap = await CopyStagedMaterialMapAsync(suppliedMap, sourceRoot, stageRoot, outputPath,
                        temporaryName, options.MediaPackaging, options.MaximumMediaBytes - totalEncodedBytes, token).ConfigureAwait(false);
                }
                else throw Error("MaterialMapTypeUnsupported", "The supplied material map type is not supported by this exporter.",
                    [new("MaterialMapTypeUnsupported", material.Identity.Key, map.GetType().Name)]);
                var result = stagedMap.Result;
                var stagedPath = stagedMap.StagePath;
                var relativePath = stagedMap.RelativePath;
                totalEncodedBytes = checked(totalEncodedBytes + result.Length);
                if (totalEncodedBytes > options.MaximumMediaBytes)
                    throw Error("TextureMediaByteLimit", "The encoded material texture files exceed the configured media byte limit.",
                        [new("TextureMediaByteLimit", material.Identity.Key,
                            $"Requested {totalEncodedBytes}; limit is {options.MaximumMediaBytes}.")]);
                if (!videos.TryGetValue(result.Sha256, out var video))
                {
                    video = new(nextId++, "FbxVideo_" + result.Sha256[..20], relativePath, stagedPath, result);
                    videos.Add(result.Sha256, video);
                }
                var materialProperty = map.Role == MaterialTextureRole.Opacity ? "TransparentColor" : "DiffuseColor";
                textures.Add(new(nextId++, materialPlan, null, null, map, video, materialProperty, materialProperty));
                var bakedKey = string.Join("|", IdentityText(map.Identity!), RevisionText(map.Revision!), map.Role,
                    map.TextureCoordinateSet, map.GraphSha256, map.DdsSha256, primitiveKey);
                if (reportedMaterialMaps.Add(bakedKey))
                {
                    derivedReports.Add(new(map.Identity!, map.Revision!, map.Role, map.TextureCoordinateSet,
                        [primitiveKey], map.GraphSha256, map.DdsSha256, map.Sources,
                        video.RelativePath, video.Export.Length, video.Export.Sha256, video.Export.OutputIsSrgb));
                    var sourceDetails = string.Join(";", map.Sources.Select(source =>
                        $"slot={source.SlotOrdinal},identity={IdentityText(source.Identity)},revision={RevisionText(source.Revision)}," +
                        $"payload={source.PayloadSha256},subresource={SubresourceText(source.Subresource)},sampler={source.Sampling?.RawWord:X16}"));
                    losses.Add(new("DerivedMaterialMapExported", material.Identity.Key,
                        $"role={map.Role}; primitive={primitiveKey}; uv={map.TextureCoordinateSet}; " +
                        $"graph={map.GraphSha256}; derivedIdentity={IdentityText(map.Identity!)}@{RevisionText(map.Revision!)}; " +
                        $"derivedDds={map.DdsSha256}; png={video.Export.Sha256}; file={video.RelativePath}; " +
                        $"encoding={(map.Role == MaterialTextureRole.Opacity ? "linear-transparency" : (map.IsSrgb ? "sRGB-colour" : "linear-colour"))}; sources=[{sourceDetails}]"));
                }
                if (video.Export.Losses != "None; original encoded bytes copied exactly.")
                    losses.Add(new("TextureEncodingLoss", material.Identity.Key,
                        $"Derived {map.Role} map PNG staging: {video.Export.Losses}"));
                if (map.Role == MaterialTextureRole.BaseColor && map.IsSrgb != video.Export.OutputIsSrgb)
                    losses.Add(new("TextureColorSpaceRoleMismatch", material.Identity.Key,
                        $"Derived base-color pixels retain output color-space flag {(video.Export.OutputIsSrgb ? "sRGB" : "linear")}."));
            }
            foreach (var diagnostic in material.Diagnostics)
                losses.Add(new("MaterialDiagnostic", material.Identity.Key, diagnostic, material.References));
        }

        if (textures.Count > options.MaximumTextures)
            throw Error("TextureConnectionLimit", "The number of FBX texture connections exceeds the configured limit.",
                [new("TextureConnectionLimit", "materials", $"Requested {textures.Count}; limit is {options.MaximumTextures}.")]);
        foreach (var texture in textures)
            if (!Finite(texture.UvScale) || !Finite(texture.UvOffset))
                throw Error("MaterialUvTransformInvalid", "A material UV transform contains non-finite values.",
                    [new("MaterialUvTransformInvalid", texture.Material.Name,
                        $"Slot {texture.Binding?.SlotOrdinal.ToString(CultureInfo.InvariantCulture) ?? "baked"} has invalid UV values.",
                        texture.Binding is null ? null : [texture.Binding.Texture])]);
        return new(textures, videos.Values.OrderBy(item => item.Id).ToList(), reports, nextId) { DerivedReports = derivedReports };
    }

    private static async Task<FbxStagedTexture> CopyStagedMaterialMapAsync(FbxStagedMaterialMapInput map,
        string sourceRoot, string stageRoot, string outputPath, string temporaryName,
        FbxMediaPackaging packaging, long remainingMediaBytes, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(map.Path) || !Path.IsPathFullyQualified(map.Path) ||
            !IsSha256(map.PngSha256) || map.Length <= 0 || map.Length > remainingMediaBytes)
            throw Error("StagedMaterialMapBounds", "The staged PNG path, hash or length is invalid or exceeds the remaining media limit.",
                [new("StagedMaterialMapBounds", IdentityText(map.MaterialIdentity),
                    $"Declared length {map.Length}; remaining media bytes {remainingMediaBytes}.")]);
        var sourcePath = Path.GetFullPath(map.Path);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (map.OpenPreparedContent is null && (!DirectoryBoundary.IsWithin(sourcePath, tempRoot) || DirectoryBoundary.IsWithin(sourcePath, sourceRoot)))
            throw Error("StagedMaterialMapPathUnsafe", "Staged PNG inputs must be inside the temporary root and outside the source archive.",
                [new("StagedMaterialMapPathUnsafe", IdentityText(map.MaterialIdentity), "The staged PNG path is outside the allowed temporary scope.")]);
        if (map.OpenPreparedContent is null) OutputSafety.ValidateFilePath(sourcePath, sourceRoot);
        var sourceDirectory = Path.GetDirectoryName(sourcePath)!;
        using var sourceDirectoryPin = map.OpenPreparedContent is null ? OutputSafety.PinDirectory(sourceDirectory) : null;
        await using var source = map.OpenPreparedContent is { } open
            ? await open(token).ConfigureAwait(false)
            : new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != map.Length)
            throw Error("StagedMaterialMapLengthMismatch", "The staged PNG length differs from its pinned contract.",
                [new("StagedMaterialMapLengthMismatch", IdentityText(map.MaterialIdentity),
                    $"Expected {map.Length}; found {source.Length}.")]);
        var png = await ReadStagedPngMetadataAsync(source, map.Length, token).ConfigureAwait(false);
        if (png.Width != map.PixelWidth || png.Height != map.PixelHeight || png.HasSrgb != map.EncodedSrgb)
            throw Error("StagedMaterialMapPngMetadataMismatch", "The staged PNG IHDR or sRGB marker does not match the supplied map metadata.",
                [new("StagedMaterialMapPngMetadataMismatch", IdentityText(map.MaterialIdentity),
                    $"PNG is {png.Width}x{png.Height}, sRGB={png.HasSrgb}; expected {map.PixelWidth}x{map.PixelHeight}, sRGB={map.EncodedSrgb}.")]);

        source.Position = 0;
        var stagedPath = OutputSafety.ResolveOutputPath(stageRoot, temporaryName);
        var temporaryCreated = false;
        string? sha256 = null;
        long copied = 0;
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                temporaryCreated = true;
                var buffer = new byte[128 * 1024];
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (read == 0) break;
                    copied = checked(copied + read);
                    if (copied > map.Length || copied > remainingMediaBytes)
                        throw Error("StagedMaterialMapCopyLimit", "The staged PNG exceeded its declared or configured byte limit.",
                            [new("StagedMaterialMapCopyLimit", IdentityText(map.MaterialIdentity), "The bounded staged copy stopped before writing the excess bytes.")]);
                    digest.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                await destination.FlushAsync(token).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }
            sha256 = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
            if (copied != map.Length || !StringComparer.OrdinalIgnoreCase.Equals(sha256, map.PngSha256))
                throw Error("StagedMaterialMapHashMismatch", "The staged PNG byte count or SHA-256 differs from its contract.",
                    [new("StagedMaterialMapHashMismatch", IdentityText(map.MaterialIdentity),
                        $"Copied {copied}/{map.Length} bytes; computed SHA-256 {sha256}.")]);
            await map.ValidateSources(token).ConfigureAwait(false);
            var relativePath = packaging == FbxMediaPackaging.Embedded
                ? $"textures/{sha256}.png"
                : $"{Path.GetFileName(outputPath)}.assets/textures/{sha256}.png";
            var result = new TextureExportResult(relativePath, copied, sha256, AssetExportFormat.Png,
                map.DerivedDdsSha256, null, null, null, "RGBA8", false, false,
                map.EncodedSrgb, map.EncodedSrgb, "RGBA8", "None; original encoded bytes copied exactly.", null);
            return new(stagedPath, relativePath, result);
        }
        catch
        {
            if (temporaryCreated)
            {
                try { File.Delete(stagedPath); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }

    }

    private static async Task<(int Width, int Height, bool HasSrgb)> ReadStagedPngMetadataAsync(
        Stream stream, long length, CancellationToken token)
    {
        static async Task ReadExactlyAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
        {
            await source.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        if (length < 45) throw new InvalidDataException("The staged PNG is too short to contain required chunks.");
        stream.Position = 0;
        var signature = new byte[8];
        await ReadExactlyAsync(stream, signature, token).ConfigureAwait(false);
        if (!signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("The staged map does not have the PNG signature.");

        var chunkHeader = new byte[8];
        await ReadExactlyAsync(stream, chunkHeader, token).ConfigureAwait(false);
        var ihdrLength = ReadPngUInt32(chunkHeader, 0);
        if (ihdrLength != 13 || !PngChunkTypeEquals(chunkHeader, 4, "IHDR"))
            throw new InvalidDataException("The staged PNG must begin with a 13-byte IHDR chunk.");
        var ihdr = new byte[13];
        await ReadExactlyAsync(stream, ihdr, token).ConfigureAwait(false);
        var width = checked((int)ReadPngUInt32(ihdr, 0));
        var height = checked((int)ReadPngUInt32(ihdr, 4));
        if (ihdr[8] != 8 || ihdr[9] != 6 || ihdr[10] != 0 || ihdr[11] != 0 || ihdr[12] != 0 || width < 1 || height < 1)
            throw new InvalidDataException("The staged PNG must use non-interlaced 8-bit RGBA with standard compression and filtering.");
        var crcBytes = new byte[4];
        await ReadExactlyAsync(stream, crcBytes, token).ConfigureAwait(false);
        if (ReadPngUInt32(crcBytes, 0) != PngChunkCrc(chunkHeader, 4, 4, ihdr, 0, ihdr.Length))
            throw new InvalidDataException("The staged PNG IHDR CRC is invalid.");

        var hasSrgb = false;
        var seenIdat = false;
        var idatEnded = false;
        var seenPlte = false;
        var hasNonemptyIdat = false;
        var seenIend = false;
        var scratch = new byte[128 * 1024];
        while (stream.Position < length)
        {
            token.ThrowIfCancellationRequested();
            if (length - stream.Position < 12)
                throw new InvalidDataException("The staged PNG contains a truncated chunk header or trailer.");
            await ReadExactlyAsync(stream, chunkHeader, token).ConfigureAwait(false);
            var chunkLength = ReadPngUInt32(chunkHeader, 0);
            var chunkEnd = checked(stream.Position + chunkLength + 4L);
            if (chunkEnd > length) throw new InvalidDataException("The staged PNG chunk exceeds the declared file length.");
            if (PngChunkTypeEquals(chunkHeader, 4, "IHDR")) throw new InvalidDataException("The staged PNG contains more than one IHDR chunk.");
            if (!IsValidPngChunkType(chunkHeader, 4)) throw new InvalidDataException("The staged PNG has an invalid chunk type.");
            var isSrgb = PngChunkTypeEquals(chunkHeader, 4, "sRGB");
            var isIdat = PngChunkTypeEquals(chunkHeader, 4, "IDAT");
            var isIend = PngChunkTypeEquals(chunkHeader, 4, "IEND");
            var isPlte = PngChunkTypeEquals(chunkHeader, 4, "PLTE");
            if (isIdat)
            {
                if (idatEnded || chunkLength == 0)
                    throw new InvalidDataException("PNG IDAT chunks must be nonempty and consecutive.");
                seenIdat = true;
                hasNonemptyIdat = true;
            }
            else
            {
                if (seenIdat) idatEnded = true;
                if (isSrgb)
                {
                    if (seenIdat || hasSrgb || chunkLength != 1)
                        throw new InvalidDataException("The staged PNG has an invalid or repeated sRGB marker.");
                }
                else if (isPlte)
                {
                    if (seenIdat || seenPlte || chunkLength is < 3 or > 768 || chunkLength % 3 != 0)
                        throw new InvalidDataException("The staged PNG PLTE chunk is invalid or misplaced.");
                    seenPlte = true;
                }
                else if (isIend)
                {
                    if (chunkLength != 0 || !seenIdat || !hasNonemptyIdat || chunkEnd != length)
                        throw new InvalidDataException("The staged PNG has an invalid IEND position or no image data.");
                }
                else if (chunkHeader[4] is >= (byte)'A' and <= (byte)'Z')
                    throw new InvalidDataException("The staged PNG contains an unsupported critical chunk.");
            }
            var crc = uint.MaxValue;
            for (var index = 4; index < 8; index++) crc = UpdatePngCrc32(crc, chunkHeader[index]);
            uint remaining = chunkLength;
            var srgbIntent = (byte)0;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var readCount = (int)Math.Min((uint)scratch.Length, remaining);
                var memory = scratch.AsMemory(0, readCount);
                await stream.ReadExactlyAsync(memory, token).ConfigureAwait(false);
                for (var index = 0; index < readCount; index++) crc = UpdatePngCrc32(crc, scratch[index]);
                if (isSrgb) srgbIntent = scratch[0];
                remaining -= (uint)readCount;
            }
            if (isSrgb)
            {
                if (srgbIntent > 3) throw new InvalidDataException("The staged PNG sRGB intent is invalid.");
                hasSrgb = true;
            }
            await ReadExactlyAsync(stream, crcBytes, token).ConfigureAwait(false);
            if (ReadPngUInt32(crcBytes, 0) != ~crc)
                throw new InvalidDataException("The staged PNG contains a chunk with an invalid CRC.");
            if (isIend)
            {
                seenIend = true;
                break;
            }
        }
        if (!seenIend) throw new InvalidDataException("The staged PNG has no final IEND chunk.");
        return (width, height, hasSrgb);
    }

    private static uint ReadPngUInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));

    private static bool PngChunkTypeEquals(byte[] bytes, int offset, string expected) =>
        expected.Length == 4 && bytes[offset] == expected[0] && bytes[offset + 1] == expected[1] &&
        bytes[offset + 2] == expected[2] && bytes[offset + 3] == expected[3];

    private static bool IsValidPngChunkType(byte[] bytes, int offset) =>
        IsPngLetter(bytes[offset]) && IsPngLetter(bytes[offset + 1]) &&
        bytes[offset + 2] is >= (byte)'A' and <= (byte)'Z' && IsPngLetter(bytes[offset + 3]);

    private static bool IsPngLetter(byte value) => value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

    private static uint PngChunkCrc(byte[] first, int firstOffset, int firstLength,
        byte[] second, int secondOffset, int secondLength)
    {
        var crc = uint.MaxValue;
        for (var index = 0; index < firstLength; index++) crc = UpdatePngCrc32(crc, first[firstOffset + index]);
        for (var index = 0; index < secondLength; index++) crc = UpdatePngCrc32(crc, second[secondOffset + index]);
        return ~crc;
    }

    private static uint UpdatePngCrc32(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
        return crc;
    }

    private static FileStream? PrepareMaterialMediaPublication(FbxMediaPlan media, string outputDirectory,
        string sourceRoot, List<IDisposable> parentPins)
    {
        if (media.Videos.Count == 0) return null;
        var firstRelative = media.Videos[0].RelativePath;
        var assetFolder = firstRelative.Split('/')[0];
        var sidecarRoot = Path.Combine(outputDirectory, assetFolder);
        OutputSafety.ValidateOutputRoot(sidecarRoot, sourceRoot);
        Directory.CreateDirectory(sidecarRoot);
        parentPins.Add(OutputSafety.PinDirectory(sidecarRoot));
        var texturesDirectory = Path.Combine(sidecarRoot, "textures");
        Directory.CreateDirectory(texturesDirectory);
        parentPins.Add(OutputSafety.PinDirectory(texturesDirectory));
        var lockPath = Path.Combine(sidecarRoot, ".deviant-publish.lock");
        OutputSafety.ValidateFilePath(lockPath, sourceRoot);
        FileStream? publicationLock = null;
        try
        {
            publicationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                FileOptions.WriteThrough);
            File.SetAttributes(lockPath, File.GetAttributes(lockPath) | FileAttributes.Hidden);
            return publicationLock;
        }
        catch (IOException)
        {
            publicationLock?.Dispose();
            throw Error("TextureSidecarBusy", "Another export is publishing media for this FBX destination.",
                [new("TextureSidecarBusy", Path.GetFileName(sidecarRoot), "Retry the export after the active publication completes.")]);
        }
        catch
        {
            publicationLock?.Dispose();
            throw;
        }
    }

    private static async Task PublishMaterialMediaAsync(FbxMediaPlan media, string outputDirectory,
        string sourceRoot, List<(string Path, string Sha256)> createdFiles, CancellationToken token)
    {
        foreach (var video in media.Videos)
        {
            token.ThrowIfCancellationRequested();
            var destination = OutputSafety.ResolveOutputPath(outputDirectory, video.RelativePath);
            OutputSafety.ValidateFilePath(destination, sourceRoot);
            if (File.Exists(destination))
            {
                var existingHash = await HashFileAsync(destination, token).ConfigureAwait(false);
                if (!StringComparer.Ordinal.Equals(existingHash, video.Export.Sha256))
                    throw Error("TextureSidecarHashConflict", "An existing content-addressed texture has different bytes.",
                        [new("TextureSidecarHashConflict", video.RelativePath, "Refusing to overwrite an existing media file.")]);
                continue;
            }
            try
            {
                File.Move(video.StagePath, destination, overwrite: false);
                createdFiles.Add((destination, video.Export.Sha256));
            }
            catch (IOException) when (File.Exists(destination))
            {
                var concurrentHash = await HashFileAsync(destination, token).ConfigureAwait(false);
                if (!StringComparer.Ordinal.Equals(concurrentHash, video.Export.Sha256)) throw;
            }
        }
    }

    private static async Task ValidateSourceRevisionsAsync(FbxPlan plan, FbxMediaPlan media, CancellationToken token)
    {
        var materials = new Dictionary<string, FbxMaterialExportInput>(StringComparer.Ordinal);
        foreach (var material in plan.Materials)
            if (material.MaterialInput is { } input) materials.TryAdd(input.Material.Identity.Key, input);
        foreach (var input in materials.Values)
        {
            token.ThrowIfCancellationRequested();
            await input.ValidateSourceRevision(token).ConfigureAwait(false);
        }
        var materialMaps = new HashSet<FbxMaterialMapInput>(ReferenceEqualityComparer.Instance);
        foreach (var material in materials.Values)
        foreach (var map in material.AllMaps)
            if (materialMaps.Add(map))
            {
                token.ThrowIfCancellationRequested();
                await map.ValidateSources(token).ConfigureAwait(false);
            }
        var textures = new Dictionary<(string MaterialIdentity, int Slot), FbxMaterialTextureInput>();
        foreach (var item in media.Textures)
            if (item.Input is { } sourceInput && item.Binding is { } binding)
                textures.TryAdd((item.Material.MaterialInput!.Material.Identity.Key, binding.SlotOrdinal), sourceInput);
        var validatedSourceSlots = new HashSet<(string MaterialIdentity, int Slot)>();
        foreach (var pair in textures)
        {
            token.ThrowIfCancellationRequested();
            await pair.Value.ValidateSourceRevision(token).ConfigureAwait(false);
            validatedSourceSlots.Add(pair.Key);
        }
        foreach (var materialInput in materials.Values)
        foreach (var source in materialInput.AllMaps.SelectMany(map => map.Sources))
        {
            token.ThrowIfCancellationRequested();
            if (!validatedSourceSlots.Add((materialInput.Material.Identity.Key, source.SlotOrdinal))) continue;
            var sourceInput = materialInput.Textures.FirstOrDefault(item => item.SlotOrdinal == source.SlotOrdinal);
            if (sourceInput is not null) await sourceInput.ValidateSourceRevision(token).ConfigureAwait(false);
            else await materialInput.GraphSources.First(item => item.SlotOrdinal == source.SlotOrdinal).ValidateSourceRevision(token).ConfigureAwait(false);
        }
        foreach (var materialInput in materials.Values)
        foreach (var source in materialInput.GraphSources)
            if (validatedSourceSlots.Add((materialInput.Material.Identity.Key, source.SlotOrdinal)))
                await source.ValidateSourceRevision(token).ConfigureAwait(false);
    }

    private static async Task CleanupCreatedMediaFilesAsync(List<(string Path, string Sha256)> createdFiles)
    {
        foreach (var created in createdFiles)
        {
            try
            {
                if (File.Exists(created.Path) &&
                    StringComparer.Ordinal.Equals(await HashFileAsync(created.Path, CancellationToken.None).ConfigureAwait(false), created.Sha256))
                    File.Delete(created.Path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string MaterialProperty(MaterialTextureRole role) => role switch
    {
        MaterialTextureRole.BaseColor => "DiffuseColor",
        MaterialTextureRole.Normal => "NormalMap",
        MaterialTextureRole.Opacity => "DeviantOpacity",
        MaterialTextureRole.Roughness => "DeviantRoughness",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    private static long EstimatePngScratchBytes(TextureSurface surface) => EstimatePngScratchBytes(surface.Width);

    private static long EstimatePngScratchBytes(int width) => checked((long)width * 4 + 512 * 1024);

    private static string SubresourceText(TextureSubresourceSelection selection) =>
        $"array={selection.ArrayLayer};face={selection.Face?.ToString() ?? "2D"};mip={selection.MipIndex}";

    private static string DescribeGraphOperations(MaterialSurfaceGraph graph)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<MaterialExpression>();
        foreach (var root in new[] { graph.BaseColor, graph.Opacity, graph.AlphaCutoff })
            if (root is not null) pending.Push(root);
        var visited = new HashSet<MaterialExpression>(ReferenceEqualityComparer.Instance);
        while (pending.Count > 0 && visited.Count < 4096)
        {
            var node = pending.Pop();
            if (!visited.Add(node)) continue;
            result.Add(node.Operation.ToString());
            foreach (var child in node.Arguments) pending.Push(child);
        }
        foreach (var diagnostic in graph.Diagnostics) result.Add("diagnostic:" + diagnostic);
        return result.Count == 0 ? "none" : string.Join(",", result);
    }

    private static IReadOnlyList<FbxBinary7400Node> BuildBinaryDocument(FbxModelExportInput input,
        FbxPlan plan, FbxMediaPlan media, string creationTime, CancellationToken token)
    {
        var asset = input.Geometry.Value!;
        var preserveNumbers = input.CoordinateMetadata == FbxCoordinateMetadataPolicy.PreserveSourceNumbers;
        var axes = preserveNumbers ? default : GetFbxAxes(asset.Coordinates.Axes!);
        var unitScale = preserveNumbers ? 0 : checked((float)(asset.Coordinates.MetersPerUnit!.Value * 100));
        if (!preserveNumbers && (!float.IsFinite(unitScale) || unitScale <= 0))
            throw Error("CoordinateScaleInvalid", "The FBX unit scale is unavailable.",
                [new("CoordinateScaleInvalid", asset.Coordinates.Id, "Centimeters per unit must be positive and finite.")]);
        if (!DateTime.TryParseExact(creationTime, "yyyy-MM-dd HH:mm:ss:fff", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timestamp))
            throw new InvalidDataException("The binary FBX creation timestamp is invalid.");

        var roots = new List<FbxBinary7400Node>();
        roots.Add(new("FBXHeaderExtension", children:
        [
            new("FBXHeaderVersion", [FbxBinary7400Property.Int32(1003)]),
            new("FBXVersion", [FbxBinary7400Property.Int32(7400)]),
            new("CreationTimeStamp", children:
            [
                new("Version", [FbxBinary7400Property.Int32(1000)]),
                new("Year", [FbxBinary7400Property.Int32(timestamp.Year)]),
                new("Month", [FbxBinary7400Property.Int32(timestamp.Month)]),
                new("Day", [FbxBinary7400Property.Int32(timestamp.Day)]),
                new("Hour", [FbxBinary7400Property.Int32(timestamp.Hour)]),
                new("Minute", [FbxBinary7400Property.Int32(timestamp.Minute)]),
                new("Second", [FbxBinary7400Property.Int32(timestamp.Second)]),
                new("Millisecond", [FbxBinary7400Property.Int32(timestamp.Millisecond)])
            ]),
            new("Creator", [FbxBinary7400Property.String("Deviant")])
        ]));
        roots.Add(new("FileId", [FbxBinary7400Property.RawBytes(FbxBinary7400Writer.CreateFileId(creationTime))]));
        roots.Add(new("CreationTime", [FbxBinary7400Property.String(creationTime)]));
        roots.Add(new("Creator", [FbxBinary7400Property.String("Deviant")]));

        var globalProperties = preserveNumbers ? new List<FbxBinary7400Node>
        {
            PString("DeviantCoordinateProfile", asset.Coordinates.Id),
            PString("DeviantCoordinateProfileVersion", asset.Coordinates.Version),
            PString("DeviantCoordinateMetadata", input.CoordinateMetadata.ToString())
        } : new List<FbxBinary7400Node>
        {
            PInt("UpAxis", "int", "Integer", axes.Up.Index),
            PInt("UpAxisSign", "int", "Integer", axes.Up.Sign),
            PInt("FrontAxis", "int", "Integer", axes.Front.Index),
            PInt("FrontAxisSign", "int", "Integer", axes.Front.Sign),
            PInt("CoordAxis", "int", "Integer", axes.Right.Index),
            PInt("CoordAxisSign", "int", "Integer", axes.Right.Sign),
            PDouble("UnitScaleFactor", "double", "Number", unitScale),
            PDouble("OriginalUnitScaleFactor", "double", "Number", unitScale),
            PInt("OriginalUpAxis", "int", "Integer", axes.Up.Index),
            PInt("OriginalUpAxisSign", "int", "Integer", axes.Up.Sign),
            PString("DeviantCoordinateProfile", asset.Coordinates.Id),
            PString("DeviantCoordinateProfileVersion", asset.Coordinates.Version),
            PString("DeviantCoordinateHandedness", asset.Coordinates.Handedness.ToString()),
            PDouble("DeviantMetersPerUnit", "double", "Number", asset.Coordinates.MetersPerUnit!.Value)
        };
        AddBinaryAnimationSettings(globalProperties, plan.Animation);
        roots.Add(new("GlobalSettings", children:
        [
            new("Version", [FbxBinary7400Property.Int32(1000)]),
            new("Properties70", children: globalProperties)
        ]));
        roots.Add(new("Documents", children:
        [
            new("Count", [FbxBinary7400Property.Int32(1)]),
            new("Document", [FbxBinary7400Property.Int64(1), FbxBinary7400Property.String("Document::Scene"),
                FbxBinary7400Property.String("")],
            [new("Properties70"), new("RootNode", [FbxBinary7400Property.Int64(0)])])
        ]));
        roots.Add(new("References"));

        var objectDefinitions = new List<FbxBinary7400Node>();
        AddDefinition(objectDefinitions, "Model", plan.Models.Count);
        AddDefinition(objectDefinitions, "Geometry", plan.Geometries.Count);
        AddDefinition(objectDefinitions, "Material", plan.Materials.Length);
        AddDefinition(objectDefinitions, "Texture", media.Textures.Count);
        AddDefinition(objectDefinitions, "Video", media.Videos.Count);
        AddDefinition(objectDefinitions, "Deformer", plan.Skins.Sum(skin => 1 + skin.Clusters.Count));
        AddDefinition(objectDefinitions, "Pose", plan.Skins.Count);
        AddAnimationDefinitions(objectDefinitions, plan.Animation);
        var definitionCount = checked(plan.Models.Count + plan.Geometries.Count + plan.Materials.Length +
            media.Textures.Count + media.Videos.Count + plan.Skins.Sum(skin => 2 + skin.Clusters.Count) + AnimationObjectCount(plan.Animation));
        roots.Add(new("Definitions", children:
            [new("Version", [FbxBinary7400Property.Int32(100)]),
                new("Count", [FbxBinary7400Property.Int32(definitionCount)]), .. objectDefinitions]));

        var objects = new List<FbxBinary7400Node>();
        foreach (var model in plan.Models)
        {
            token.ThrowIfCancellationRequested();
            objects.Add(BuildBinaryModel(model, input.Geometry.Identity, input.Geometry.Revision));
        }
        foreach (var geometry in plan.Geometries.Values.OrderBy(item => item.GeometryId))
        {
            token.ThrowIfCancellationRequested();
            objects.Add(BuildBinaryGeometry(geometry, input.Geometry.Identity, input.Geometry.Revision, token));
        }
        foreach (var material in plan.Materials)
        {
            token.ThrowIfCancellationRequested();
            objects.Add(BuildBinaryMaterial(material, input.Geometry.Identity, input.Geometry.Revision));
        }
        foreach (var texture in media.Textures)
        {
            token.ThrowIfCancellationRequested();
            objects.Add(BuildBinaryTexture(texture));
        }
        foreach (var video in media.Videos)
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(video.StagePath);
            if (!info.Exists || info.Length != video.Export.Length)
                throw new InvalidDataException("Staged PNG media changed before binary document planning.");
            objects.Add(new("Video", [FbxBinary7400Property.Int64(video.Id),
                FbxBinary7400Property.String($"{video.Name}\0\u0001Video"), FbxBinary7400Property.String("Clip")],
            [
                new("Type", [FbxBinary7400Property.String("Clip")]),
                new("Properties70"),
                new("UseMipMap", [FbxBinary7400Property.Int32(0)]),
                new("Filename", [FbxBinary7400Property.String(video.RelativePath)]),
                new("RelativeFilename", [FbxBinary7400Property.String(video.RelativePath)]),
                new("Content", [FbxBinary7400Property.RawFile(video.StagePath, video.Export.Length, video.Export.Sha256)])
            ]));
        }
        foreach (var skin in plan.Skins) objects.AddRange(BuildBinarySkin(skin, token));
        objects.AddRange(BuildBinaryAnimation(plan.Animation, token));
        roots.Add(new("Objects", children: objects));

        var connections = new List<FbxBinary7400Node>();
        foreach (var model in plan.Models)
        {
            token.ThrowIfCancellationRequested();
            var parent = model.SourceNode?.ParentKey is { } key ? plan.SourceNodeIds[key] : model.ParentModelId;
            connections.Add(Connection("OO", model.Id, parent));
            if (model.Primitive is { } primitive)
            {
                connections.Add(Connection("OO", primitive.GeometryId, model.Id));
                foreach (var materialKey in primitive.MaterialKeys)
                    connections.Add(Connection("OO", plan.MaterialIds[materialKey], model.Id));
            }
        }
        foreach (var texture in media.Textures)
        {
            token.ThrowIfCancellationRequested();
            connections.Add(Connection("OO", texture.Video.Id, texture.Id));
            connections.Add(new("C", [FbxBinary7400Property.String("OP"), FbxBinary7400Property.Int64(texture.Id),
                FbxBinary7400Property.Int64(texture.Material.Id), FbxBinary7400Property.String(texture.MaterialProperty)]));
        }
        foreach (var skin in plan.Skins)
            foreach (var (child, parent) in SkinConnections(skin)) connections.Add(Connection("OO", child, parent));
        connections.AddRange(BuildBinaryAnimationConnections(plan.Animation));
        roots.Add(new("Connections", children: connections));
        roots.Add(BuildBinaryTakes(plan.Animation));
        return roots;
    }

    private static FbxBinary7400Node BuildBinaryModel(FbxModelPlan model, ResourceIdentity identity, SourceRevision revision)
    {
        var properties = new List<FbxBinary7400Node>
        {
            PVector("Lcl Translation", "Lcl Translation", model.Transform.Translation),
            PVector("Lcl Rotation", "Lcl Rotation", model.Transform.EulerDegrees),
            PVector("Lcl Scaling", "Lcl Scaling", model.Transform.Scale),
            PInt("RotationOrder", "enum", "", 0)
        };
        if (model.SourceNode is { } node)
        {
            properties.Add(PString("DeviantSourceNodeKey", node.Key));
            properties.Add(PString("DeviantSourceReference", ReferenceText(node.Source)));
        }
        if (model.Primitive is { } primitive)
        {
            properties.Add(PString("DeviantSourceIdentity", IdentityText(identity)));
            properties.Add(PString("DeviantSourceRevision", RevisionText(revision)));
            properties.Add(PString("DeviantSourcePrimitiveKey", primitive.Primitive.Key));
        }
        return new("Model", [FbxBinary7400Property.Int64(model.Id), FbxBinary7400Property.String($"{model.Name}\0\u0001Model"),
            FbxBinary7400Property.String(model.Type)],
        [new("Version", [FbxBinary7400Property.Int32(232)]), new("Properties70", children: properties),
            new("Shading", [FbxBinary7400Property.Boolean(true)]),
            new("Culling", [FbxBinary7400Property.String("CullingOff")])]);
    }

    private static FbxBinary7400Node BuildBinaryGeometry(FbxPrimitivePlan plan, ResourceIdentity identity,
        SourceRevision revision, CancellationToken token)
    {
        var primitive = plan.Primitive;
        var children = new List<FbxBinary7400Node>
        {
            new("GeometryVersion", [FbxBinary7400Property.Int32(124)]),
            new("Vertices", [DoubleArray(checked(primitive.Positions.Count * 3), index =>
                Component(primitive.Positions[index / 3], index % 3))]),
            new("PolygonVertexIndex", [Int32Array(primitive.Indices.Count, index =>
            {
                var value = checked((int)primitive.Indices[index]);
                return index % 3 == 2 ? checked(-value - 1) : value;
            })])
        };
        var primaryLayer = new List<(string Type, int Index)>();
        var extraUvLayers = new List<int>();
        if (primitive.Normals is { } normals)
        {
            children.Add(VectorLayer("LayerElementNormal", "Normals", "Normal", normals));
            primaryLayer.Add(("LayerElementNormal", 0));
        }
        if (primitive.Tangents is { } tangents)
        {
            children.Add(new("LayerElementTangent", [FbxBinary7400Property.Int32(0)],
            [
                new("Version", [FbxBinary7400Property.Int32(101)]),
                new("Name", [FbxBinary7400Property.String("Tangent")]),
                new("MappingInformationType", [FbxBinary7400Property.String("ByVertice")]),
                new("ReferenceInformationType", [FbxBinary7400Property.String("Direct")]),
                new("Tangents", [DoubleArray(checked(tangents.Count * 3), index => Component(new Vector3(tangents[index / 3].X,
                    tangents[index / 3].Y, tangents[index / 3].Z), index % 3))]),
                new("TangentsW", [DoubleArray(tangents.Count, index => tangents[index].W)])
            ]));
            primaryLayer.Add(("LayerElementTangent", 0));
        }
        if (primitive.Colors is { } colors)
        {
            children.Add(new("LayerElementColor", [FbxBinary7400Property.Int32(0)],
            [
                new("Version", [FbxBinary7400Property.Int32(101)]),
                new("Name", [FbxBinary7400Property.String("Color")]),
                new("MappingInformationType", [FbxBinary7400Property.String("ByVertice")]),
                new("ReferenceInformationType", [FbxBinary7400Property.String("Direct")]),
                new("Colors", [DoubleArray(checked(colors.Count * 4), index => Component(colors[index / 4], index % 4))])
            ]));
            primaryLayer.Add(("LayerElementColor", 0));
        }
        if (primitive.TextureCoordinates is { } uvSets)
        {
            for (var setIndex = 0; setIndex < uvSets.Count; setIndex++)
            {
                token.ThrowIfCancellationRequested();
                var uv = uvSets[setIndex];
                children.Add(new("LayerElementUV", [FbxBinary7400Property.Int32(setIndex)],
                [
                    new("Version", [FbxBinary7400Property.Int32(101)]),
                    new("Name", [FbxBinary7400Property.String($"UVSet{setIndex}")]),
                    new("MappingInformationType", [FbxBinary7400Property.String("ByVertice")]),
                    new("ReferenceInformationType", [FbxBinary7400Property.String("Direct")]),
                    new("UV", [DoubleArray(checked(uv.Count * 2), index => index % 2 == 0 ? uv[index / 2].X : uv[index / 2].Y)])
                ]));
                if (setIndex == 0) primaryLayer.Add(("LayerElementUV", 0));
                else extraUvLayers.Add(setIndex);
            }
        }
        foreach (var (setIndex, uv) in plan.CornerUvSets.OrderBy(pair => pair.Key))
        {
            token.ThrowIfCancellationRequested();
            children.Add(new("LayerElementUV", [FbxBinary7400Property.Int32(setIndex)],
            [new("Version", [FbxBinary7400Property.Int32(101)]),
                new("Name", [FbxBinary7400Property.String($"UVSet{setIndex}")]),
                new("MappingInformationType", [FbxBinary7400Property.String("ByPolygonVertex")]),
                new("ReferenceInformationType", [FbxBinary7400Property.String("Direct")]),
                new("UV", [DoubleArray(checked(uv.Count * 2), index => index % 2 == 0 ? uv[index / 2].X : uv[index / 2].Y)])]));
            if (setIndex == 0) primaryLayer.Add(("LayerElementUV", 0));
            else extraUvLayers.Add(setIndex);
        }
        if (plan.FaceMaterialSlots.Any(slot => slot >= 0))
        {
            children.Add(new("LayerElementMaterial", [FbxBinary7400Property.Int32(0)],
            [
                new("Version", [FbxBinary7400Property.Int32(101)]),
                new("Name", [FbxBinary7400Property.String("")]),
                new("MappingInformationType", [FbxBinary7400Property.String("ByPolygon")]),
                new("ReferenceInformationType", [FbxBinary7400Property.String("IndexToDirect")]),
                new("Materials", [Int32Array(plan.FaceMaterialSlots.Length, index => plan.FaceMaterialSlots[index])])
            ]));
            primaryLayer.Add(("LayerElementMaterial", 0));
        }
        AddLayer(children, 0, primaryLayer);
        foreach (var uvSet in extraUvLayers) AddLayer(children, uvSet, [("LayerElementUV", uvSet)]);
        children.Add(new("Properties70", children:
        [
            PString("DeviantSourceIdentity", IdentityText(identity)),
            PString("DeviantSourceRevision", RevisionText(revision)),
            PString("DeviantSourcePrimitiveKey", primitive.Key)
        ]));
        return new("Geometry", [FbxBinary7400Property.Int64(plan.GeometryId),
            FbxBinary7400Property.String($"{primitive.Key}\0\u0001Geometry"), FbxBinary7400Property.String("Mesh")], children);
    }

    private static FbxBinary7400Node BuildBinaryMaterial(FbxMaterialPlan material,
        ResourceIdentity identity, SourceRevision revision)
    {
        var properties = new List<FbxBinary7400Node>
        {
            PVector("DiffuseColor", "Color", material.HasBakedBaseColor ? Vector3.One : new Vector3(0.65f, 0.65f, 0.65f)),
            PDouble("DiffuseFactor", "Number", "", 1),
            PVector("SpecularColor", "Color", Vector3.Zero),
            PDouble("TransparencyFactor", "Number", "", material.HasBakedOpacity ? 1 : 0),
            PString("DeviantSourceIdentity", IdentityText(identity)),
            PString("DeviantSourceRevision", RevisionText(revision)),
            PString("DeviantSourceReferences", string.Join(";", material.References.Select(ReferenceText))),
            PBool("DeviantMaterialPlaceholder", true)
        };
        if (material.HasBakedOpacity)
            properties.Add(PVector("TransparentColor", "Color", Vector3.One));
        if (material.HasBakedBaseColor)
        {
            properties.Add(PBool("DeviantBakedBaseColorAttached", true));
            properties.Add(PBool("DeviantDiffuseFactorNeutralizedForBakedBaseColor", true));
        }
        if (material.MaterialInput is { } input)
        {
            properties.Add(PString("DeviantMaterialIdentity", IdentityText(input.Material.Identity)));
            properties.Add(PString("DeviantMaterialRevision", RevisionText(input.Material.Revision)));
            properties.Add(PString("DeviantMaterialProfile", input.Material.Profile));
            foreach (var binding in input.Material.Bindings.Where(binding => binding.Role is MaterialTextureRole.Opacity or MaterialTextureRole.Roughness))
                properties.Add(PString($"DeviantTextureRole_{binding.SlotOrdinal}", binding.Role.ToString()));
        }
        return new("Material", [FbxBinary7400Property.Int64(material.Id),
            FbxBinary7400Property.String($"{material.Name}\0\u0001Material"), FbxBinary7400Property.String("")],
        [new("Version", [FbxBinary7400Property.Int32(102)]),
            new("ShadingModel", [FbxBinary7400Property.String("phong")]),
            new("MultiLayer", [FbxBinary7400Property.Int32(0)]),
            new("Properties70", children: properties)]);
    }

    private static FbxBinary7400Node BuildBinaryTexture(FbxTexturePlan texture)
    {
        var name = $"{texture.Material.Name}_{texture.TextureName}_{texture.Id.ToString(CultureInfo.InvariantCulture)}";
        var scale = texture.UvScale;
        var offset = texture.UvOffset;
        var inverseScaleX = 1.0 / scale.X;
        var inverseScaleY = 1.0 / scale.Y;
        var inverseOffsetX = -(double)offset.X / scale.X;
        var inverseOffsetY = -(double)offset.Y / scale.Y;
        if (!double.IsFinite(inverseScaleX) || !double.IsFinite(inverseScaleY) ||
            !double.IsFinite(inverseOffsetX) || !double.IsFinite(inverseOffsetY))
            throw Error("MaterialUvTransformInvalid", "A material UV transform cannot be represented by FBX texture properties.",
                [new("MaterialUvTransformInvalid", texture.Material.Name,
                    $"Slot {texture.Binding?.SlotOrdinal.ToString(CultureInfo.InvariantCulture) ?? "baked"} has an unbounded inverse transform.",
                    texture.Binding is null ? null : [texture.Binding.Texture])]);
        var properties = new List<FbxBinary7400Node>
        {
            PVector("Scaling", "Vector3D", inverseScaleX, inverseScaleY, 1),
            PVector("Translation", "Vector3D", inverseOffsetX, inverseOffsetY, 0),
            PString("UVSet", $"UVSet{texture.TextureCoordinateSet.ToString(CultureInfo.InvariantCulture)}"),
            PString("DeviantTextureEncodedSha256", texture.Video.Export.Sha256),
            PString("DeviantTextureMediaFormat", texture.Video.Export.Format.ToString()),
            PString("DeviantTextureMediaScope", texture.Video.Export.Format == AssetExportFormat.Dds ? "WholeDocument" : "SelectedSubresource"),
            PString("DeviantTextureColorSpace", texture.Video.Export.OutputIsSrgb ? "sRGB" : "linear")
        };
        if (texture.Video.Export.Format == AssetExportFormat.Png)
            properties.Add(PString("DeviantTexturePngSha256", texture.Video.Export.Sha256));
        if (texture.Map is { } materialMap)
        {
            var sourceDetails = string.Join(";", materialMap.Sources.Select(source =>
                $"slot={source.SlotOrdinal},identity={IdentityText(source.Identity)},revision={RevisionText(source.Revision)}," +
                $"payload={source.PayloadSha256},subresource={SubresourceText(source.Subresource)},sampler={source.Sampling?.RawWord:X16}"));
            properties.Add(PString("DeviantTextureOrigin", materialMap is FbxStagedMaterialMapInput ? "StagedMaterialMapPng" : "BakedMaterialMapDds"));
            properties.Add(PString("DeviantMaterialMapRole", materialMap.Role.ToString()));
            properties.Add(PString("DeviantMaterialMapGraphSha256", materialMap.GraphSha256));
            properties.Add(PString("DeviantMaterialMapPrimitiveKeys", string.Join(";", materialMap.PrimitiveKeys.OrderBy(key => key, StringComparer.Ordinal))));
            properties.Add(PString("DeviantMaterialMapEncoding", materialMap.Role == MaterialTextureRole.Opacity ? "LinearTransparency" : (materialMap.IsSrgb ? "SrgbColour" : "LinearColour")));
            properties.Add(PString("DeviantMaterialMapIdentity", IdentityText(materialMap.Identity!)));
            properties.Add(PString("DeviantMaterialMapRevision", RevisionText(materialMap.Revision!)));
            properties.Add(PString("DeviantMaterialMapDdsSha256", materialMap.DdsSha256));
            properties.Add(PString("DeviantMaterialMapSources", sourceDetails));
            if (materialMap is FbxStagedMaterialMapInput stagedMap)
                properties.Add(PString("DeviantStagedPngSha256", stagedMap.PngSha256));
        }
        else if (texture.Input is { } sourceInput && texture.Binding is { } binding)
        {
            properties.Add(PString("DeviantTextureRole", binding.Role.ToString()));
            properties.Add(PString("DeviantMaterialIdentity", IdentityText(texture.Material.MaterialInput!.Material.Identity)));
            properties.Add(PString("DeviantMaterialRevision", RevisionText(texture.Material.MaterialInput.Material.Revision)));
            properties.Add(PInt("DeviantMaterialTextureSlot", "int", "Integer", binding.SlotOrdinal));
            properties.Add(PString("DeviantTextureIdentity", IdentityText(sourceInput.Document.Identity!)));
            properties.Add(PString("DeviantTextureRevision", RevisionText(sourceInput.Document.Revision!)));
            properties.Add(PString("DeviantTextureSubresource", SubresourceText(sourceInput.Surface.Subresource)));
            properties.Add(PString("DeviantTextureSourcePayloadSha256", sourceInput.Document.ContentSha256));
        }
        if (texture.Video.Export.DisplayTransform is { } display)
            properties.Add(PString("DeviantTextureDisplayTransform",
                $"{display.Operator}|{Format(display.ExposureStops)}|encoded-srgb={display.EncodedSrgb}"));
        var videoName = $"Video::{texture.Video.Name}";
        return new("Texture", [FbxBinary7400Property.Int64(texture.Id), FbxBinary7400Property.String($"{name}\0\u0001Texture"),
            FbxBinary7400Property.String("")],
        [new("Type", [FbxBinary7400Property.String("TextureVideoClip")]),
            new("Version", [FbxBinary7400Property.Int32(202)]),
            new("TextureName", [FbxBinary7400Property.String(texture.TextureName)]),
            new("Media", [FbxBinary7400Property.String(videoName)]),
            new("FileName", [FbxBinary7400Property.String(texture.Video.RelativePath)]),
            new("RelativeFilename", [FbxBinary7400Property.String(texture.Video.RelativePath)]),
            new("ModelUVScaling", [FbxBinary7400Property.Double(scale.X), FbxBinary7400Property.Double(scale.Y)]),
            new("ModelUVTranslation", [FbxBinary7400Property.Double(offset.X), FbxBinary7400Property.Double(offset.Y)]),
            new("Properties70", children: properties)]);
    }

    private static FbxBinary7400Node VectorLayer(string nodeName, string valuesName, string layerName,
        IReadOnlyList<Vector3> values) => new(nodeName, [FbxBinary7400Property.Int32(0)],
    [new("Version", [FbxBinary7400Property.Int32(101)]), new("Name", [FbxBinary7400Property.String(layerName)]),
        new("MappingInformationType", [FbxBinary7400Property.String("ByVertice")]),
        new("ReferenceInformationType", [FbxBinary7400Property.String("Direct")]),
        new(valuesName, [DoubleArray(checked(values.Count * 3), index => Component(values[index / 3], index % 3))])]);

    private static void AddLayer(List<FbxBinary7400Node> children, int index, IReadOnlyList<(string Type, int Index)> elements)
    {
        if (elements.Count == 0) return;
        children.Add(new("Layer", [FbxBinary7400Property.Int32(index)],
            new[] { new FbxBinary7400Node("Version", [FbxBinary7400Property.Int32(100)]) }
                .Concat(elements.Select(element => new FbxBinary7400Node("LayerElement",
                    children:
                    [new("Type", [FbxBinary7400Property.String(element.Type)]),
                        new("TypedIndex", [FbxBinary7400Property.Int32(element.Index)])]))).ToArray()));
    }

    private static void AddDefinition(List<FbxBinary7400Node> definitions, string kind, int count) =>
        definitions.Add(new("ObjectType", [FbxBinary7400Property.String(kind)],
            [new("Count", [FbxBinary7400Property.Int32(count)])]));

    private static FbxBinary7400Node Connection(string type, long source, long destination) =>
        new("C", [FbxBinary7400Property.String(type), FbxBinary7400Property.Int64(source),
            FbxBinary7400Property.Int64(destination)]);

    private static FbxBinary7400Node PString(string name, string value) =>
        P(name, "KString", "", "", FbxBinary7400Property.String(value));
    private static FbxBinary7400Node PInt(string name, string type, string subtype, int value) =>
        P(name, type, subtype, "", FbxBinary7400Property.Int32(value));
    private static FbxBinary7400Node PDouble(string name, string type, string subtype, double value) =>
        P(name, type, subtype, "", FbxBinary7400Property.Double(value));
    private static FbxBinary7400Node PBool(string name, bool value) =>
        P(name, "bool", "", "", FbxBinary7400Property.Boolean(value));
    private static FbxBinary7400Node PVector(string name, string type, Vector3 value) =>
        P(name, type, "", "A", FbxBinary7400Property.Double(value.X), FbxBinary7400Property.Double(value.Y), FbxBinary7400Property.Double(value.Z));
    private static FbxBinary7400Node PVector(string name, string type, Vector2 value) =>
        P(name, type, "", "A", FbxBinary7400Property.Double(value.X), FbxBinary7400Property.Double(value.Y));
    private static FbxBinary7400Node PVector(string name, string type, double x, double y, double z) =>
        P(name, type, "", "A", FbxBinary7400Property.Double(x), FbxBinary7400Property.Double(y), FbxBinary7400Property.Double(z));
    private static FbxBinary7400Node P(string name, string type, string subtype, string flags,
        params FbxBinary7400Property[] values) => new("P",
        [FbxBinary7400Property.String(name), FbxBinary7400Property.String(type),
            FbxBinary7400Property.String(subtype), FbxBinary7400Property.String(flags), .. values]);

    private static FbxBinary7400Property DoubleArray(int count, Func<int, double> value)
    {
        var elementCount = checked((uint)count);
        return FbxBinary7400Property.Array((byte)'d', elementCount, sizeof(double), (writer, cancellation) =>
        {
            for (var index = 0; index < count; index++)
            {
                if ((index & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                writer.Write(value(index));
            }
        });
    }

    private static FbxBinary7400Property Int32Array(int count, Func<int, int> value)
    {
        var elementCount = checked((uint)count);
        return FbxBinary7400Property.Array((byte)'i', elementCount, sizeof(int), (writer, cancellation) =>
        {
            for (var index = 0; index < count; index++)
            {
                if ((index & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                writer.Write(value(index));
            }
        });
    }

    private static double Component(Vector3 value, int index) => index switch
    { 0 => value.X, 1 => value.Y, _ => value.Z };
    private static double Component(Vector4 value, int index) => index switch
    { 0 => value.X, 1 => value.Y, 2 => value.Z, _ => value.W };

    private static void WriteFile(TextWriter writer, FbxModelExportInput input, FbxPlan plan,
        FbxMediaPlan media, List<FbxGeometryExportLoss> losses, CancellationToken token)
    {
        var decoded = input.Geometry;
        var asset = decoded.Value!;
        var preserveNumbers = input.CoordinateMetadata == FbxCoordinateMetadataPolicy.PreserveSourceNumbers;
        var axes = preserveNumbers ? default : GetFbxAxes(asset.Coordinates.Axes!);
        var unitScale = preserveNumbers ? 0 : checked((float)(asset.Coordinates.MetersPerUnit!.Value * 100));
        if (!preserveNumbers && (!float.IsFinite(unitScale) || unitScale <= 0))
            throw Error("CoordinateScaleInvalid", "The FBX unit scale does not fit in a finite single-precision value.",
                [new("CoordinateScaleInvalid", asset.Coordinates.Id, "Centimeters per unit must remain positive and finite after conversion to float.")]);
        writer.WriteLine("; FBX 7.4.0 project file");
        writer.WriteLine("FBXHeaderExtension: {");
        writer.WriteLine("\tFBXHeaderVersion: 1003");
        writer.WriteLine("\tFBXVersion: 7400");
        writer.WriteLine("\tCreator: \"Deviant\"");
        writer.WriteLine("}");
        writer.WriteLine("GlobalSettings: { Version: 1000 Properties70: {");
        if (!preserveNumbers)
        {
        WriteProperty(writer, "UpAxis", "int", "Integer", axes.Up.Index);
        WriteProperty(writer, "UpAxisSign", "int", "Integer", axes.Up.Sign);
        WriteProperty(writer, "FrontAxis", "int", "Integer", axes.Front.Index);
        WriteProperty(writer, "FrontAxisSign", "int", "Integer", axes.Front.Sign);
        WriteProperty(writer, "CoordAxis", "int", "Integer", axes.Right.Index);
        WriteProperty(writer, "CoordAxisSign", "int", "Integer", axes.Right.Sign);
        WriteScalarProperty(writer, "UnitScaleFactor", "double", "Number", Format(unitScale));
        WriteScalarProperty(writer, "OriginalUnitScaleFactor", "double", "Number", Format(unitScale));
        WriteProperty(writer, "OriginalUpAxis", "int", "Integer", axes.Up.Index);
        WriteProperty(writer, "OriginalUpAxisSign", "int", "Integer", axes.Up.Sign);
        }
        WriteProperty(writer, "DeviantCoordinateProfile", "KString", "", asset.Coordinates.Id);
        WriteProperty(writer, "DeviantCoordinateProfileVersion", "KString", "", asset.Coordinates.Version);
        if (!preserveNumbers)
        {
            WriteProperty(writer, "DeviantCoordinateHandedness", "KString", "", asset.Coordinates.Handedness.ToString());
            WriteScalarProperty(writer, "DeviantMetersPerUnit", "double", "Number", Format(asset.Coordinates.MetersPerUnit!.Value));
        }
        else WriteProperty(writer, "DeviantCoordinateMetadata", "KString", "", input.CoordinateMetadata.ToString());
        WriteAnimationSettings(writer, plan.Animation);
        writer.WriteLine("} }");
        writer.WriteLine("Documents: { Count: 1 Document: 1, \"Document::Scene\" { Properties70: {} RootNode: 0 } }");
        writer.WriteLine("References: {}");
        writer.WriteLine("Definitions: {");
        writer.WriteLine("\tVersion: 100");
        writer.WriteLine($"\tCount: {plan.Models.Count + plan.Geometries.Count + plan.Materials.Length + media.Textures.Count + media.Videos.Count + plan.Skins.Sum(skin => 2 + skin.Clusters.Count) + AnimationObjectCount(plan.Animation)}");
        WriteDefinition(writer, "Model", plan.Models.Count);
        WriteDefinition(writer, "Geometry", plan.Geometries.Count);
        WriteDefinition(writer, "Material", plan.Materials.Length);
        WriteDefinition(writer, "Texture", media.Textures.Count);
        WriteDefinition(writer, "Video", media.Videos.Count);
        WriteDefinition(writer, "Deformer", plan.Skins.Sum(skin => 1 + skin.Clusters.Count));
        WriteDefinition(writer, "Pose", plan.Skins.Count);
        WriteAnimationDefinitions(writer, plan.Animation);
        writer.WriteLine("}");
        writer.WriteLine("Objects: {");
        foreach (var model in plan.Models)
        {
            token.ThrowIfCancellationRequested();
            WriteModel(writer, model, decoded.Identity, decoded.Revision);
        }
        foreach (var geometry in plan.Geometries.Values.OrderBy(item => item.GeometryId))
        {
            token.ThrowIfCancellationRequested();
            WriteGeometry(writer, geometry, decoded.Identity, decoded.Revision, token);
        }
        foreach (var material in plan.Materials)
        {
            token.ThrowIfCancellationRequested();
            WriteMaterial(writer, material, decoded.Identity, decoded.Revision);
        }
        foreach (var texture in media.Textures)
        {
            token.ThrowIfCancellationRequested();
            WriteTexture(writer, texture);
        }
        foreach (var video in media.Videos)
        {
            token.ThrowIfCancellationRequested();
            WriteVideo(writer, video);
        }
        foreach (var skin in plan.Skins) WriteSkin(writer, skin, token);
        WriteAnimation(writer, plan.Animation, token);
        writer.WriteLine("}");
        writer.WriteLine("Connections: {");
        WriteAnimationConnections(writer, plan.Animation);
        foreach (var skin in plan.Skins)
            foreach (var (child, parent) in SkinConnections(skin)) writer.WriteLine($"\tC: \"OO\",{child},{parent}");
        foreach (var model in plan.Models)
        {
            token.ThrowIfCancellationRequested();
            var parent = model.SourceNode?.ParentKey is { } key ? plan.SourceNodeIds[key] : model.ParentModelId;
            writer.WriteLine($"\tC: \"OO\",{model.Id},{parent}");
            if (model.Primitive is { } primitive)
            {
                writer.WriteLine($"\tC: \"OO\",{primitive.GeometryId},{model.Id}");
                foreach (var materialKey in primitive.MaterialKeys)
                    writer.WriteLine($"\tC: \"OO\",{plan.MaterialIds[materialKey]},{model.Id}");
            }
        }
        foreach (var texture in media.Textures)
        {
            token.ThrowIfCancellationRequested();
            writer.WriteLine($"\tC: \"OO\",{texture.Video.Id},{texture.Id}");
            writer.WriteLine($"\tC: \"OP\",{texture.Id},{texture.Material.Id},\"{texture.MaterialProperty}\"");
        }
        writer.WriteLine("}");
        WriteTakes(writer, plan.Animation);
    }

    private static void WriteGeometry(TextWriter writer, FbxPrimitivePlan plan, ResourceIdentity identity,
        SourceRevision revision, CancellationToken token)
    {
        var primitive = plan.Primitive;
        writer.WriteLine($"\tGeometry: {plan.GeometryId}, \"Geometry::{Escape(primitive.Key)}\", \"Mesh\" {{");
        writer.WriteLine("\t\tGeometryVersion: 124");
        writer.WriteLine($"\t\tVertices: *{primitive.Positions.Count * 3} {{ a: ");
        WriteValues(writer, primitive.Positions.Count, index => Format(primitive.Positions[index]), token);
        writer.WriteLine(" }");
        writer.WriteLine($"\t\tPolygonVertexIndex: *{primitive.Indices.Count} {{ a: ");
        WriteValues(writer, primitive.Indices.Count, index =>
        {
            var value = checked((int)primitive.Indices[index]);
            return (index % 3 == 2 ? checked(-value - 1) : value).ToString(CultureInfo.InvariantCulture);
        }, token);
        writer.WriteLine(" }");

        var primaryLayer = new List<(string Type, int Index)>();
        var additionalUvLayers = new List<int>();
        if (primitive.Normals is { } normals)
        {
            WriteVec3Layer(writer, "LayerElementNormal", "Normals", 0, "Normal", normals, token);
            primaryLayer.Add(("LayerElementNormal", 0));
        }
        if (primitive.Tangents is { } tangents)
        {
            writer.WriteLine($"\t\tLayerElementTangent: 0 {{ Version: 101 Name: \"Tangent\" MappingInformationType: \"ByVertice\" ReferenceInformationType: \"Direct\" Tangents: *{tangents.Count * 3} {{ a: ");
            WriteValues(writer, tangents.Count, index => Format(new Vector3(tangents[index].X, tangents[index].Y, tangents[index].Z)), token);
            writer.WriteLine($"\n\t\t\t}} TangentsW: *{tangents.Count} {{ a: ");
            WriteValues(writer, tangents.Count, index => Format(tangents[index].W), token);
            writer.WriteLine(" } }");
            primaryLayer.Add(("LayerElementTangent", 0));
        }
        if (primitive.Colors is { } colors)
        {
            writer.WriteLine($"\t\tLayerElementColor: 0 {{ Version: 101 Name: \"Color\" MappingInformationType: \"ByVertice\" ReferenceInformationType: \"Direct\" Colors: *{colors.Count * 4} {{ a: ");
            WriteValues(writer, colors.Count, index => Format(colors[index]), token);
            writer.WriteLine(" } }");
            primaryLayer.Add(("LayerElementColor", 0));
        }
        if (primitive.TextureCoordinates is { } uvSets)
        {
            for (var index = 0; index < uvSets.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                writer.WriteLine($"\t\tLayerElementUV: {index} {{ Version: 101 Name: \"UVSet{index}\" MappingInformationType: \"ByVertice\" ReferenceInformationType: \"Direct\" UV: *{uvSets[index].Count * 2} {{ a: ");
                WriteValues(writer, uvSets[index].Count, vertex => Format(uvSets[index][vertex]), token);
                writer.WriteLine(" } }");
                if (index == 0) primaryLayer.Add(("LayerElementUV", index));
                else additionalUvLayers.Add(index);
            }
        }
        foreach (var (setIndex, uv) in plan.CornerUvSets.OrderBy(pair => pair.Key))
        {
            token.ThrowIfCancellationRequested();
            writer.WriteLine($"\t\tLayerElementUV: {setIndex} {{ Version: 101 Name: \"UVSet{setIndex}\" MappingInformationType: \"ByPolygonVertex\" ReferenceInformationType: \"Direct\" UV: *{uv.Count * 2} {{ a: ");
            WriteValues(writer, uv.Count, corner => Format(uv[corner]), token);
            writer.WriteLine(" } }");
            if (setIndex == 0) primaryLayer.Add(("LayerElementUV", 0));
            else additionalUvLayers.Add(setIndex);
        }
        if (plan.FaceMaterialSlots.Any(slot => slot >= 0))
        {
            writer.WriteLine($"\t\tLayerElementMaterial: 0 {{ Version: 101 Name: \"\" MappingInformationType: \"ByPolygon\" ReferenceInformationType: \"IndexToDirect\" Materials: *{plan.FaceMaterialSlots.Length} {{ a: ");
            WriteValues(writer, plan.FaceMaterialSlots.Length, face => plan.FaceMaterialSlots[face].ToString(CultureInfo.InvariantCulture), token);
            writer.WriteLine(" } }");
            primaryLayer.Add(("LayerElementMaterial", 0));
        }
        WriteLayer(writer, 0, primaryLayer);
        foreach (var uvIndex in additionalUvLayers)
            WriteLayer(writer, uvIndex, [("LayerElementUV", uvIndex)]);
        writer.WriteLine("\t\tProperties70: {");
        WriteProperty(writer, "DeviantSourceIdentity", "KString", "", IdentityText(identity));
        WriteProperty(writer, "DeviantSourceRevision", "KString", "", RevisionText(revision));
        WriteProperty(writer, "DeviantSourcePrimitiveKey", "KString", "", primitive.Key);
        writer.WriteLine("\t\t}");
        writer.WriteLine("\t}");
    }

    private static void WriteVec3Layer(TextWriter writer, string type, string valuesName, int index,
        string name, IReadOnlyList<Vector3> values, CancellationToken token)
    {
        writer.WriteLine($"\t\t{type}: {index} {{ Version: 101 Name: \"{Escape(name)}\" MappingInformationType: \"ByVertice\" ReferenceInformationType: \"Direct\" {valuesName}: *{values.Count * 3} {{ a: ");
        WriteValues(writer, values.Count, vertex => Format(values[vertex]), token);
        writer.WriteLine(" } }");
    }

    private static void WriteLayer(TextWriter writer, int layerIndex, IReadOnlyList<(string Type, int Index)> elements)
    {
        if (elements.Count == 0) return;
        writer.WriteLine($"\t\tLayer: {layerIndex} {{ Version: 100");
        foreach (var element in elements)
            writer.WriteLine($"\t\t\tLayerElement: {{ Type: \"{element.Type}\" TypedIndex: {element.Index} }}");
        writer.WriteLine("\t\t}");
    }

    private static void WriteModel(TextWriter writer, FbxModelPlan model, ResourceIdentity identity, SourceRevision revision)
    {
        writer.WriteLine($"\tModel: {model.Id}, \"Model::{Escape(model.Name)}\", \"{model.Type}\" {{");
        writer.WriteLine("\t\tVersion: 232");
        writer.WriteLine("\t\tProperties70: {");
        WriteNumericProperty(writer, "Lcl Translation", "Lcl Translation", "", Format(model.Transform.Translation));
        WriteNumericProperty(writer, "Lcl Rotation", "Lcl Rotation", "", Format(model.Transform.EulerDegrees));
        WriteNumericProperty(writer, "Lcl Scaling", "Lcl Scaling", "", Format(model.Transform.Scale));
        WriteProperty(writer, "RotationOrder", "enum", "", 0);
        if (model.SourceNode is { } node)
        {
            WriteProperty(writer, "DeviantSourceNodeKey", "KString", "", node.Key);
            WriteProperty(writer, "DeviantSourceReference", "KString", "", ReferenceText(node.Source));
        }
        if (model.Primitive is { } primitive)
        {
            WriteProperty(writer, "DeviantSourceIdentity", "KString", "", IdentityText(identity));
            WriteProperty(writer, "DeviantSourceRevision", "KString", "", RevisionText(revision));
            WriteProperty(writer, "DeviantSourcePrimitiveKey", "KString", "", primitive.Primitive.Key);
        }
        writer.WriteLine("\t\t}");
        writer.WriteLine("\t\tShading: T");
        writer.WriteLine("\t\tCulling: \"CullingOff\"");
        writer.WriteLine("\t}");
    }

    private static void WriteMaterial(TextWriter writer, FbxMaterialPlan material,
        ResourceIdentity identity, SourceRevision revision)
    {
        writer.WriteLine($"\tMaterial: {material.Id}, \"Material::{material.Name}\", \"\" {{");
        writer.WriteLine("\t\tVersion: 102");
        writer.WriteLine("\t\tShadingModel: \"phong\"");
        writer.WriteLine("\t\tMultiLayer: 0");
        writer.WriteLine("\t\tProperties70: {");
        WriteNumericProperty(writer, "DiffuseColor", "Color", "", material.HasBakedBaseColor ? "1,1,1" : "0.65,0.65,0.65");
        WriteScalarProperty(writer, "DiffuseFactor", "Number", "", "1");
        WriteNumericProperty(writer, "SpecularColor", "Color", "", "0,0,0");
        WriteScalarProperty(writer, "TransparencyFactor", "Number", "", material.HasBakedOpacity ? "1" : "0");
        if (material.HasBakedOpacity)
            WriteNumericProperty(writer, "TransparentColor", "Color", "", "1,1,1");
        WriteProperty(writer, "DeviantSourceIdentity", "KString", "", IdentityText(identity));
        WriteProperty(writer, "DeviantSourceRevision", "KString", "", RevisionText(revision));
        WriteProperty(writer, "DeviantSourceReferences", "KString", "", string.Join(";", material.References.Select(ReferenceText)));
        WriteScalarProperty(writer, "DeviantMaterialPlaceholder", "bool", "", "1");
        if (material.HasBakedBaseColor)
        {
            WriteScalarProperty(writer, "DeviantBakedBaseColorAttached", "bool", "", "1");
            WriteScalarProperty(writer, "DeviantDiffuseFactorNeutralizedForBakedBaseColor", "bool", "", "1");
        }
        if (material.MaterialInput is { } input)
        {
            WriteProperty(writer, "DeviantMaterialIdentity", "KString", "", IdentityText(input.Material.Identity));
            WriteProperty(writer, "DeviantMaterialRevision", "KString", "", RevisionText(input.Material.Revision));
            WriteProperty(writer, "DeviantMaterialProfile", "KString", "", input.Material.Profile);
            foreach (var binding in input.Material.Bindings.Where(binding => binding.Role is MaterialTextureRole.Opacity or MaterialTextureRole.Roughness))
                WriteProperty(writer, $"DeviantTextureRole_{binding.SlotOrdinal}", "KString", "", binding.Role.ToString());
        }
        writer.WriteLine("\t\t}");
        writer.WriteLine("\t}");
    }

    private static void WriteTexture(TextWriter writer, FbxTexturePlan texture)
    {
        var name = $"{texture.Material.Name}_{texture.TextureName}_{texture.Id.ToString(CultureInfo.InvariantCulture)}";
        writer.WriteLine($"\tTexture: {texture.Id}, \"Texture::{Escape(name)}\", \"\" {{");
        writer.WriteLine("\t\tType: \"TextureVideoClip\"");
        writer.WriteLine("\t\tVersion: 202");
        writer.WriteLine($"\t\tTextureName: \"{Escape(texture.TextureName)}\"");
        writer.WriteLine($"\t\tMedia: \"Video::{Escape(texture.Video.Name)}\"");
        writer.WriteLine($"\t\tFileName: \"{Escape(texture.Video.RelativePath)}\"");
        writer.WriteLine($"\t\tRelativeFilename: \"{Escape(texture.Video.RelativePath)}\"");
        writer.WriteLine($"\t\tModelUVScaling: {Format(texture.UvScale)}");
        writer.WriteLine($"\t\tModelUVTranslation: {Format(texture.UvOffset)}");
        writer.WriteLine("\t\tProperties70: {");
        // FBX stores texture-to-UV; source bindings transform UV-to-texture.
        WriteNumericProperty(writer, "Scaling", "Vector3D", "",
            $"{Format(1.0 / texture.UvScale.X)},{Format(1.0 / texture.UvScale.Y)},1");
        WriteNumericProperty(writer, "Translation", "Vector3D", "",
            $"{Format(-(double)texture.UvOffset.X / texture.UvScale.X)},{Format(-(double)texture.UvOffset.Y / texture.UvScale.Y)},0");
        WriteProperty(writer, "UVSet", "KString", "", $"UVSet{texture.TextureCoordinateSet.ToString(CultureInfo.InvariantCulture)}");
        if (texture.Map is { } materialMap)
        {
            var sourceDetails = string.Join(";", materialMap.Sources.Select(source =>
                $"slot={source.SlotOrdinal},identity={IdentityText(source.Identity)},revision={RevisionText(source.Revision)}," +
                $"payload={source.PayloadSha256},subresource={SubresourceText(source.Subresource)},sampler={source.Sampling?.RawWord:X16}"));
            WriteProperty(writer, "DeviantTextureOrigin", "KString", "", materialMap is FbxStagedMaterialMapInput ? "StagedMaterialMapPng" : "BakedMaterialMapDds");
            WriteProperty(writer, "DeviantMaterialMapRole", "KString", "", materialMap.Role.ToString());
            WriteProperty(writer, "DeviantMaterialMapGraphSha256", "KString", "", materialMap.GraphSha256);
            WriteProperty(writer, "DeviantMaterialMapPrimitiveKeys", "KString", "", string.Join(";", materialMap.PrimitiveKeys.OrderBy(key => key, StringComparer.Ordinal)));
            WriteProperty(writer, "DeviantMaterialMapEncoding", "KString", "", materialMap.Role == MaterialTextureRole.Opacity ? "LinearTransparency" : (materialMap.IsSrgb ? "SrgbColour" : "LinearColour"));
            WriteProperty(writer, "DeviantMaterialMapIdentity", "KString", "", IdentityText(materialMap.Identity!));
            WriteProperty(writer, "DeviantMaterialMapRevision", "KString", "", RevisionText(materialMap.Revision!));
            WriteProperty(writer, "DeviantMaterialMapDdsSha256", "KString", "", materialMap.DdsSha256);
            WriteProperty(writer, "DeviantMaterialMapSources", "KString", "", sourceDetails);
            if (materialMap is FbxStagedMaterialMapInput stagedMap)
                WriteProperty(writer, "DeviantStagedPngSha256", "KString", "", stagedMap.PngSha256);
        }
        else if (texture.Input is { } sourceInput && texture.Binding is { } binding)
        {
            WriteProperty(writer, "DeviantTextureRole", "KString", "", binding.Role.ToString());
            WriteProperty(writer, "DeviantMaterialIdentity", "KString", "", IdentityText(texture.Material.MaterialInput!.Material.Identity));
            WriteProperty(writer, "DeviantMaterialRevision", "KString", "", RevisionText(texture.Material.MaterialInput.Material.Revision));
            WriteProperty(writer, "DeviantMaterialTextureSlot", "int", "Integer", binding.SlotOrdinal);
            WriteProperty(writer, "DeviantTextureIdentity", "KString", "", IdentityText(sourceInput.Document.Identity!));
            WriteProperty(writer, "DeviantTextureRevision", "KString", "", RevisionText(sourceInput.Document.Revision!));
            WriteProperty(writer, "DeviantTextureSubresource", "KString", "", SubresourceText(sourceInput.Surface.Subresource));
            WriteProperty(writer, "DeviantTextureSourcePayloadSha256", "KString", "", sourceInput.Document.ContentSha256);
        }
        WriteProperty(writer, "DeviantTextureEncodedSha256", "KString", "", texture.Video.Export.Sha256);
        WriteProperty(writer, "DeviantTextureMediaFormat", "KString", "", texture.Video.Export.Format.ToString());
        WriteProperty(writer, "DeviantTextureMediaScope", "KString", "", texture.Video.Export.Format == AssetExportFormat.Dds ? "WholeDocument" : "SelectedSubresource");
        if (texture.Video.Export.Format == AssetExportFormat.Png)
            WriteProperty(writer, "DeviantTexturePngSha256", "KString", "", texture.Video.Export.Sha256);
        WriteProperty(writer, "DeviantTextureColorSpace", "KString", "", texture.Video.Export.OutputIsSrgb ? "sRGB" : "linear");
        if (texture.Video.Export.DisplayTransform is { } display)
            WriteProperty(writer, "DeviantTextureDisplayTransform", "KString", "",
                $"{display.Operator}|{Format(display.ExposureStops)}|encoded-srgb={display.EncodedSrgb}");
        writer.WriteLine("\t\t}");
        writer.WriteLine("\t}");
    }

    private static void WriteVideo(TextWriter writer, FbxVideoPlan video)
    {
        writer.WriteLine($"\tVideo: {video.Id}, \"Video::{Escape(video.Name)}\", \"Clip\" {{");
        writer.WriteLine("\t\tType: \"Clip\"");
        writer.WriteLine("\t\tProperties70: {}");
        writer.WriteLine("\t\tUseMipMap: 0");
        writer.WriteLine($"\t\tFilename: \"{Escape(video.RelativePath)}\"");
        writer.WriteLine($"\t\tRelativeFilename: \"{Escape(video.RelativePath)}\"");
        writer.WriteLine("\t}");
    }

    private static void WriteDefinition(TextWriter writer, string kind, int count)
    {
        writer.WriteLine($"\tObjectType: \"{kind}\" {{");
        writer.WriteLine($"\t\tCount: {count}");
        writer.WriteLine("\t}");
    }

    private static void WriteValues(TextWriter writer, int count, Func<int, string> format, CancellationToken token)
    {
        for (var index = 0; index < count; index++)
        {
            if ((index & 1023) == 0) token.ThrowIfCancellationRequested();
            if (index > 0) writer.Write(',');
            writer.Write(format(index));
        }
    }

    private static void WriteProperty(TextWriter writer, string name, string type, string subtype, object value) =>
        WriteScalarProperty(writer, name, type, subtype, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0");
    private static void WriteProperty(TextWriter writer, string name, string type, string subtype, string value) =>
        writer.WriteLine($"\t\t\tP: \"{Escape(name)}\",\"{Escape(type)}\",\"{Escape(subtype)}\",\"\",\"{Escape(value)}\"");
    private static void WriteNumericProperty(TextWriter writer, string name, string type, string subtype, string values) =>
        writer.WriteLine($"\t\t\tP: \"{Escape(name)}\",\"{Escape(type)}\",\"{Escape(subtype)}\",\"A\",{values}");
    private static void WriteScalarProperty(TextWriter writer, string name, string type, string subtype, string value) =>
        writer.WriteLine($"\t\t\tP: \"{Escape(name)}\",\"{Escape(type)}\",\"{Escape(subtype)}\",\"\",{value}");

    private static void ValidateCoordinates(CoordinateProfile profile)
    {
        if (!profile.HasVerifiedBasis || profile.Axes is null || profile.MetersPerUnit is not > 0 ||
            !double.IsFinite(profile.MetersPerUnit.Value))
            throw Error("CoordinateProfileUnverified", "A verified coordinate profile is required for FBX export.",
                [new("CoordinateProfileUnverified", profile.Id, "Axis mapping, handedness, and unit scale need source evidence.")]);
        if (new[] { profile.Axes.Right, profile.Axes.Up, profile.Axes.Forward }.Select(AxisIndex).Distinct().Count() != 3)
            throw Error("CoordinateAxisMapInvalid", "The coordinate axis map must use each source axis exactly once.",
                [new("CoordinateAxisMapInvalid", profile.Id, "Right, up, and forward must map to distinct axes.")]);
        var orientation = BasisOrientation(profile.Axes);
        var expectedOrientation = profile.Handedness == CoordinateHandedness.Right ? 1 : -1;
        if (orientation != expectedOrientation)
            throw Error("CoordinateHandednessMismatch", "The coordinate-axis directions conflict with the declared handedness.",
                [new("CoordinateHandednessMismatch", profile.Id, "Right, up, and forward must form the declared handed basis.")]);
        if (!double.IsFinite(profile.MetersPerUnit.Value * 100) || profile.MetersPerUnit.Value * 100 <= 0)
            throw Error("CoordinateScaleInvalid", "The FBX unit scale is unavailable.",
                [new("CoordinateScaleInvalid", profile.Id, "The centimeters-per-unit value must be positive and finite.")]);
    }

    private static FbxAxes GetFbxAxes(AxisMapping axes)
    {
        var right = Axis(axes.Right);
        var up = Axis(axes.Up);
        var front = Axis(axes.Forward);
        return new(right, up, front);
    }

    private static int BasisOrientation(AxisMapping axes)
    {
        var right = Axis(axes.Right);
        var up = Axis(axes.Up);
        var forward = Axis(axes.Forward);
        var rx = right.Index == 0 ? right.Sign : 0;
        var ry = right.Index == 1 ? right.Sign : 0;
        var rz = right.Index == 2 ? right.Sign : 0;
        var ux = up.Index == 0 ? up.Sign : 0;
        var uy = up.Index == 1 ? up.Sign : 0;
        var uz = up.Index == 2 ? up.Sign : 0;
        var fx = forward.Index == 0 ? forward.Sign : 0;
        var fy = forward.Index == 1 ? forward.Sign : 0;
        var fz = forward.Index == 2 ? forward.Sign : 0;
        var determinant = rx * (uy * fz - uz * fy) - ry * (ux * fz - uz * fx) + rz * (ux * fy - uy * fx);
        return Math.Sign(determinant);
    }

    private static FbxAxis Axis(CoordinateAxis axis) => axis switch
    {
        CoordinateAxis.PositiveX => new(0, 1), CoordinateAxis.NegativeX => new(0, -1),
        CoordinateAxis.PositiveY => new(1, 1), CoordinateAxis.NegativeY => new(1, -1),
        CoordinateAxis.PositiveZ => new(2, 1), CoordinateAxis.NegativeZ => new(2, -1),
        _ => throw new ArgumentOutOfRangeException(nameof(axis))
    };
    private static int AxisIndex(CoordinateAxis axis) => Axis(axis).Index;

    private static FbxTrs ConvertTransform(SourceTransform transform, CoordinateProfile profile, string subject)
    {
        if (transform.Elements.Count != 16 || transform.Layout == MatrixLayout.Unknown || transform.VectorSide == VectorMultiplicationSide.Unknown)
            throw Error("TransformConventionUnknown", "A node matrix has no complete transform convention.",
                [new("TransformConventionUnknown", subject, "Matrix layout and vector side must be known.")]);
        if (profile.MatrixLayout != MatrixLayout.Unknown && profile.MatrixLayout != transform.Layout)
            throw Error("TransformLayoutMismatch", "The source matrix layout conflicts with the coordinate profile.",
                [new("TransformLayoutMismatch", subject, "The transform and coordinate profile specify different layouts.")]);
        if (profile.VectorSide != VectorMultiplicationSide.Unknown && profile.VectorSide != transform.VectorSide)
            throw Error("TransformVectorSideMismatch", "The transform vector side conflicts with the coordinate profile.",
                [new("TransformVectorSideMismatch", subject, "The transform and coordinate profile specify different vector sides.")]);
        var e = transform.Elements;
        var matrix = new Matrix4x4(e[0], e[1], e[2], e[3], e[4], e[5], e[6], e[7],
            e[8], e[9], e[10], e[11], e[12], e[13], e[14], e[15]);
        if (transform.Layout == MatrixLayout.ColumnMajor) matrix = Matrix4x4.Transpose(matrix);
        if (transform.VectorSide == VectorMultiplicationSide.Column) matrix = Matrix4x4.Transpose(matrix);
        if (!Finite(matrix))
            throw Error("TransformNonFinite", "A node matrix contains non-finite values.",
                [new("TransformNonFinite", subject, "All transform elements must be finite.")]);
        if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation) ||
            !Finite(scale) || !Finite(translation) || !float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) ||
            !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W) || rotation.LengthSquared() < 1e-12f)
            throw Error("TransformNotRepresentable", "The node matrix cannot be represented as FBX TRS.",
                [new("TransformNotRepresentable", subject, "The affine matrix did not decompose to finite values.")]);
        rotation = Quaternion.Normalize(rotation);
        var reconstructed = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) *
            Matrix4x4.CreateTranslation(translation);
        if (!NearlyEqual(matrix, reconstructed))
            throw Error("TransformNotRepresentable", "The node matrix contains shear FBX TRS cannot preserve.",
                [new("TransformNotRepresentable", subject, "Decomposed TRS does not reproduce the source matrix.")]);
        var euler = QuaternionToEulerXyz(rotation);
        var eulerRadians = euler * (MathF.PI / 180f);
        var emittedRotation = Matrix4x4.CreateRotationX(eulerRadians.X) *
            Matrix4x4.CreateRotationY(eulerRadians.Y) * Matrix4x4.CreateRotationZ(eulerRadians.Z);
        var emitted = Matrix4x4.CreateScale(scale) * emittedRotation * Matrix4x4.CreateTranslation(translation);
        if (!NearlyEqual(matrix, emitted))
            throw Error("TransformNotRepresentable", "The node matrix cannot be represented by the emitted FBX Euler rotation order.",
                [new("TransformNotRepresentable", subject, "The emitted XYZ Euler channels do not reproduce the source matrix.")]);
        return new(translation, euler, scale);
    }

    private static Vector3 QuaternionToEulerXyz(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(q);
        var y = MathF.Asin(Math.Clamp(-m.M13, -1f, 1f));
        float x, z;
        if (MathF.Abs(MathF.Cos(y)) > 1e-6f)
        { x = MathF.Atan2(m.M23, m.M33); z = MathF.Atan2(m.M12, m.M11); }
        else { x = 0; z = MathF.Atan2(-m.M21, m.M22); }
        const float toDegrees = 180f / MathF.PI;
        return new(x * toDegrees, y * toDegrees, z * toDegrees);
    }

    private static bool NearlyEqual(Matrix4x4 a, Matrix4x4 b)
    {
        var left = new[] { a.M11,a.M12,a.M13,a.M14,a.M21,a.M22,a.M23,a.M24,a.M31,a.M32,a.M33,a.M34,a.M41,a.M42,a.M43,a.M44 };
        var right = new[] { b.M11,b.M12,b.M13,b.M14,b.M21,b.M22,b.M23,b.M24,b.M31,b.M32,b.M33,b.M34,b.M41,b.M42,b.M43,b.M44 };
        for (var i = 0; i < left.Length; i++)
        {
            var scale = Math.Max(1, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
            if (Math.Abs(left[i] - right[i]) > TransformTolerance * scale) return false;
        }
        return true;
    }

    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);
    private static bool Finite(Matrix4x4 m) =>
        float.IsFinite(m.M11)&&float.IsFinite(m.M12)&&float.IsFinite(m.M13)&&float.IsFinite(m.M14)&&
        float.IsFinite(m.M21)&&float.IsFinite(m.M22)&&float.IsFinite(m.M23)&&float.IsFinite(m.M24)&&
        float.IsFinite(m.M31)&&float.IsFinite(m.M32)&&float.IsFinite(m.M33)&&float.IsFinite(m.M34)&&
        float.IsFinite(m.M41)&&float.IsFinite(m.M42)&&float.IsFinite(m.M43)&&float.IsFinite(m.M44);

    private static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Format(Vector2 value) => $"{Format(value.X)},{Format(value.Y)}";
    private static string Format(Vector3 value) => $"{Format(value.X)},{Format(value.Y)},{Format(value.Z)}";
    private static string Format(Vector4 value) => $"{Format(value.X)},{Format(value.Y)},{Format(value.Z)},{Format(value.W)}";
    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default: if (char.IsControl(ch)) builder.Append('_'); else builder.Append(ch); break;
            }
        }
        return builder.ToString();
    }
    private static string IdentityText(ResourceIdentity identity) => $"{identity.IndexContentId}:{identity.Ordinal}:{identity.MemberPath ?? ""}";
    private static string RevisionText(SourceRevision revision) => $"{revision.IndexContentId}:{revision.PhysicalSourceStamp}";
    private static string ReferenceText(ResourceReference reference) =>
        $"{reference.Namespace}|{reference.ResourceType?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}|{reference.Id}|{(reference.Owner is { } owner ? IdentityText(owner) : "")}";
    private static string MaterialKey(IReadOnlyList<ResourceReference> references) => HashText(string.Join("\n",
        references.Select(ReferenceText).OrderBy(value => value, StringComparer.Ordinal)));
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static long EstimatePlanningBytes(GeometryAsset asset, IReadOnlyList<FbxMaterialExportInput> materials,
        FbxMediaPackaging packaging)
    {
        long refs = 0, faces = 0, modelPrimitives = 0, materialRefs = 0, materialSlots = 0, texturePixels = 0;
        foreach (var primitive in asset.Primitives)
        {
            refs = checked(refs + primitive.Submeshes.Sum(submesh => (long)submesh.Materials.Count));
            faces = checked(faces + primitive.Indices.Count / 3L);
            modelPrimitives = checked(modelPrimitives + primitive.Indices.Count + primitive.Positions.Count);
        }
        foreach (var material in materials)
        {
            materialRefs = checked(materialRefs + material.SubmeshReferences.Count);
            materialSlots = checked(materialSlots + material.Material.Bindings.Count + material.Textures.Count + material.GraphSources.Count + material.AllMaps.Count());
            foreach (var texture in material.Textures)
                texturePixels = checked(texturePixels + (long)texture.Surface.Width * texture.Surface.Height);
            foreach (var map in material.AllMaps)
                texturePixels = checked(texturePixels + (long)map.Width * map.Height);
        }
        var estimate = checked(WriterScratchBytes + asset.Nodes.Count * 1024L + asset.Primitives.Count * 2048L +
            refs * 768L + faces * sizeof(int) + modelPrimitives * sizeof(int) +
            materials.Count * 2048L + materialRefs * 512L + materialSlots * 1024L + texturePixels / 16);
        if (packaging == FbxMediaPackaging.Embedded)
        {
            var documentMetadata = checked(asset.Nodes.Count * 4096L + asset.Primitives.Count * 32L * 1024L +
                materials.Count * 32L * 1024L + materialRefs * 1024L + materialSlots * 16L * 1024L);
            estimate = checked(estimate + 64 * 1024L + documentMetadata);
        }
        return estimate;
    }
    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            sha.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
    private static FbxGeometryExportException Error(string code, string message,
        IReadOnlyList<FbxGeometryExportLoss> losses) => new(code, message, losses);

    private sealed record FbxPlan(Dictionary<string,FbxPrimitivePlan> Geometries, List<FbxModelPlan> Models,
        FbxMaterialPlan[] Materials, Dictionary<string,long> SourceNodeIds, Dictionary<string,long> MaterialIds, long NextId)
    {
        public List<FbxSkinPlan> Skins { get; init; } = [];
        public FbxAnimationPlan? Animation { get; init; }
    }
    private sealed record FbxMaterialJoinPlan(ReferenceStatus Status, FbxMaterialExportInput? MaterialInput);
    private sealed record FbxPrimitivePlan(MeshPrimitive Primitive, List<string> MaterialKeys,
        int[] FaceMaterialSlots, long GeometryId)
    {
        public bool HasMaterialLayer => FaceMaterialSlots.Any(index => index >= 0);
        public Dictionary<int, IReadOnlyList<Vector2>> CornerUvSets { get; } = [];
    }
    private sealed record FbxMaterialPlan(string Key, long Id, IReadOnlyList<ResourceReference> References, bool Ambiguous,
        FbxMaterialExportInput? MaterialInput, string? PrimitiveKey = null, bool HasBakedBaseColor = false)
    {
        public string Name => "DeviantMaterial_" + Key[..Math.Min(20, Key.Length)];
        public bool HasBakedOpacity => PrimitiveKey is { } primitiveKey && MaterialInput?.AllMaps.Any(map =>
            map.PrimitiveKeys.Contains(primitiveKey, StringComparer.Ordinal) &&
            map.Role == MaterialTextureRole.Opacity) == true;
    }
    private sealed record FbxStagedTexture(string StagePath, string RelativePath, TextureExportResult Result);
    private sealed record FbxVideoPlan(long Id, string Name, string RelativePath, string StagePath, TextureExportResult Export);
    private sealed record FbxTexturePlan(long Id, FbxMaterialPlan Material, MaterialTextureBinding? Binding,
        FbxMaterialTextureInput? Input, FbxMaterialMapInput? Map, FbxVideoPlan Video,
        string MaterialProperty, string TextureName)
    {
        public MaterialTextureRole Role => Binding?.Role ?? Map!.Role;
        public int TextureCoordinateSet => Binding?.TextureCoordinateSet ?? Map!.TextureCoordinateSet;
        public Vector2 UvScale => Binding?.UvScale ?? Vector2.One;
        public Vector2 UvOffset => Binding?.UvOffset ?? Vector2.Zero;
    }
    private sealed record FbxMediaPlan(List<FbxTexturePlan> Textures, List<FbxVideoPlan> Videos,
        List<FbxMaterialTextureExportResult> Reports, long NextId)
    { public List<FbxBakedMaterialMapExportResult> DerivedReports { get; init; } = []; }
    private sealed record FbxModelPlan(long Id, string Name, string Type, string? ParentKey,
        long ParentModelId, FbxTrs Transform, GeometryNode? SourceNode, FbxPrimitivePlan? Primitive);
    private readonly record struct FbxAxis(int Index, int Sign);
    private readonly record struct FbxAxes(FbxAxis Right, FbxAxis Up, FbxAxis Front);
    private readonly record struct FbxTrs(Vector3 Translation, Vector3 EulerDegrees, Vector3 Scale)
    { public static FbxTrs Identity => new(Vector3.Zero, Vector3.Zero, Vector3.One); }
}
