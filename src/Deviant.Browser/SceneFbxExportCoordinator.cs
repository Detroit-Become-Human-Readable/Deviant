using System.Numerics;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Export.Models;

namespace Deviant.Browser;

public sealed class SceneFbxExportCoordinator
{
    public static FbxGeometryExportOptions CreateOptions(bool overwrite = false) =>
        new(Overwrite: overwrite, MaximumMediaBytes: 32L * 1024 * 1024 * 1024,
            MediaPackaging: FbxMediaPackaging.Embedded);
    public async Task<FbxGeometryExportReport> ExportAsync(RenderAssetSnapshot snapshot, SceneFbxExportRequest request,
        string sourceRoot, string destination,
        Func<RenderMesh, CancellationToken, Task<NativeModelFbxMaterialInputs>> prepareMaterials,
        Func<CancellationToken, Task> validateSources, IProgress<RenderLoadProgress>? progress = null,
        FbxGeometryExportOptions? options = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(request);
        options ??= CreateOptions(); using var sourceLease = snapshot.AcquireLease();
        await validateSources(token).ConfigureAwait(false);
        var selectedOccurrences = request.Selection.Select(selection => selection.OccurrenceKey).ToHashSet(StringComparer.Ordinal);
        var selected = snapshot.Occurrences.Where(occurrence => selectedOccurrences.Contains(occurrence.Key) &&
            (request.IncludeHidden || occurrence.SourceVisible)).ToArray();
        long vertices = 0, indices = 0;
        var shared = new HashSet<(string Mesh, string Primitive, bool Mirrored)>();
        foreach (var occurrence in selected)
            foreach (var primitive in snapshot.Meshes[occurrence.GeometryKey].Geometry.Primitives.Where(primitive =>
                request.Selection.Any(selection => selection.OccurrenceKey == occurrence.Key &&
                    (selection.PrimitiveKey is null || selection.PrimitiveKey == primitive.Key))))
            {
                if (request.PreserveInstances && !request.Merge && !shared.Add((occurrence.GeometryKey, primitive.Key, occurrence.World.GetDeterminant() < 0))) continue;
                vertices = checked(vertices + primitive.Positions.Count); indices = checked(indices + primitive.Indices.Count);
            }
        if (vertices > options.MaximumVertices || indices / 3 > options.MaximumTriangles)
            throw new ArchiveException("SceneExportLimit", $"The selection has {vertices:N0} vertices and {indices / 3:N0} triangles; the FBX limits are {options.MaximumVertices:N0} and {options.MaximumTriangles:N0}. Export smaller selections or regions.");
        using var geometryMemory = snapshot.MemoryBudget.Reserve(request.PreserveInstances && !request.Merge
            ? checked(indices * 4 + selected.Length * 4096L + 65536)
            : checked(vertices * 192 + indices * 32 + 65536), "scene export geometry");
        progress?.Report(new("Preparing geometry", 0, selected.Length, snapshot.MemoryBudget.UsedBytes));
        var projection = SceneFbxProjection.Build(snapshot, request, token);
        var prepared = new Dictionary<string, NativeModelFbxMaterialInputs>(StringComparer.Ordinal);
        try
        {
            foreach (var mesh in projection.Parts.Select(part => part.Mesh).DistinctBy(mesh => mesh.Key))
            {
                token.ThrowIfCancellationRequested();
                var materialInput = await prepareMaterials(mesh, token).ConfigureAwait(false);
                if (!ReferenceEquals(materialInput.MemoryBudget, snapshot.MemoryBudget))
                { materialInput.Dispose(); throw new InvalidOperationException("Material preparation must use the scene memory budget."); }
                prepared.Add(mesh.Key, materialInput);
                progress?.Report(new("Preparing materials", prepared.Count, projection.Parts.Select(part => part.Mesh.Key).Distinct().Count(), snapshot.MemoryBudget.UsedBytes));
            }
            var atlas = BuildMergedAtlas(projection, prepared);
            var scopes = new Dictionary<string, FbxMaterialExportInput>(StringComparer.Ordinal);
            foreach (var part in projection.Parts)
            {
                var sourceInputs = prepared[part.Mesh.Key].Inputs;
                var output = projection.Geometry.Primitives.Single(primitive => primitive.Key == part.OutputKey);
                for (var slot = 0; slot < part.Source.Submeshes.Count; slot++)
                {
                    var sourceSubmesh = part.Source.Submeshes[slot];
                    var material = sourceInputs.SingleOrDefault(input => input.SubmeshReferences.Any(join => sourceSubmesh.Materials.Contains(join.Reference)));
                    if (material is null) continue;
                    var hashKey = ScenePartKey(part);
                    var outputSubmesh = output.Submeshes.Single(submesh => submesh.Key == hashKey + "/material/" + slot);
                    scopes[outputSubmesh.Key] = Remap(material, part, projection, atlas);
                }
            }
            await validateSources(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (options.MediaPackaging == FbxMediaPackaging.Sidecars)
                EnsureMediaSpace(scopes.Values, destination, projection.Geometry);
            var identity = selected.FirstOrDefault()?.Entity ?? throw new InvalidDataException("The selection has no source identity.");
            var decoded = new DecodeResult<GeometryAsset>(projection.Geometry, identity, snapshot.Revision,
                snapshot.SourceExtents, [], [], [], new Dictionary<string, string>());
            var input = new FbxModelExportInput(decoded, new(identity, "scene-selection"), snapshot.MemoryBudget.Reserve)
            { MaterialsBySubmesh = scopes, ValidateGeometrySourceRevision = validateSources, CoordinateMetadata = FbxCoordinateMetadataPolicy.PreserveSourceNumbers };
            progress?.Report(new("Writing FBX", 0, 1, snapshot.MemoryBudget.UsedBytes));
            return await new FbxGeometryExporter().ExportAsync(input, sourceRoot, destination, options, token).ConfigureAwait(false);
        }
        finally { foreach (var input in prepared.Values) input.Dispose(); }
    }

    private static void EnsureMediaSpace(IEnumerable<FbxMaterialExportInput> materials, string destination, GeometryAsset geometry)
    {
        var inputs = materials.ToArray();
        long staged = inputs.SelectMany(input => input.StagedMaps).DistinctBy(map => map.PngSha256).Sum(map => map.Length);
        if (staged <= 2L * 1024 * 1024 * 1024) return;
        long direct = inputs.SelectMany(input => input.Textures).DistinctBy(texture => texture.Document.ContentSha256)
            .Sum(texture => Math.Max(texture.Document.OriginalBytes.Length, checked((long)texture.Surface.Width * texture.Surface.Height * 4 + 65536)));
        long required = checked(staged + direct + geometry.Primitives.Sum(primitive => primitive.Positions.Count * 96L + primitive.Indices.Count * 16L) + 16L * 1024 * 1024);
        var root = Path.GetPathRoot(Path.GetFullPath(destination));
        if (string.IsNullOrEmpty(root) || root.StartsWith("\\\\", StringComparison.Ordinal)) return;
        var drive = new DriveInfo(root);
        if (drive.IsReady && drive.AvailableFreeSpace < required)
            throw new IOException($"The export needs approximately {required / (1024d * 1024 * 1024):F1} GiB of free space.");
    }

    private static string ScenePartKey(SceneFbxPart part) => part.OutputKey == "scene/merged"
        ? "scene/" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(part.Occurrence.Key))).ToLowerInvariant()[..16] +
          "/part/" + part.Mesh.Geometry.Primitives.ToList().IndexOf(part.Source) : part.OutputKey;

    private static IReadOnlyList<Vector2>? BuildMergedAtlas(SceneFbxProjectionResult projection,
        IReadOnlyDictionary<string, NativeModelFbxMaterialInputs> materials)
    {
        if (!projection.Merged || !projection.Parts.Any(part => materials[part.Mesh.Key].Inputs.SelectMany(input => input.AllMaps)
            .Any(map => map.PrimitiveKeys.Contains(part.Source.Key) && map.PolygonCornerCoordinates is not null))) return null;
        var result = new Vector2[projection.Geometry.Primitives[0].Indices.Count];
        foreach (var part in projection.Parts)
        {
            var maps = materials[part.Mesh.Key].Inputs.SelectMany(input => input.AllMaps)
                .Where(map => map.PrimitiveKeys.Contains(part.Source.Key) && map.PolygonCornerCoordinates is not null).ToArray();
            var corners = maps.FirstOrDefault()?.PolygonCornerCoordinates;
            if (maps.Skip(1).Any(map => !map.PolygonCornerCoordinates!.SequenceEqual(corners!)))
                throw new InvalidDataException("The material maps use different atlas coordinates.");
            for (var corner = 0; corner < part.Source.Indices.Count; corner++)
            {
                var original = part.Mirrored && corner % 3 != 0 ? corner - corner % 3 + (3 - corner % 3) : corner;
                result[part.FirstIndex + corner] = corners is not null ? corners[original] :
                    part.Source.TextureCoordinates is { Count: > 0 } uv ? uv[0][(int)part.Source.Indices[original]] : Vector2.Zero;
            }
        }
        return result;
    }
    private static FbxMaterialExportInput Remap(FbxMaterialExportInput input, SceneFbxPart part,
        SceneFbxProjectionResult projection, IReadOnlyList<Vector2>? mergedAtlas)
    {
        IReadOnlyList<Vector2>? Corners(FbxMaterialMapInput map)
        {
            if (map.PolygonCornerCoordinates is not { } corners) return null;
            if (projection.Merged) return mergedAtlas;
            return part.Mirrored ? Enumerable.Range(0, corners.Count).Select(corner =>
                corners[corner % 3 == 0 ? corner : corner - corner % 3 + 3 - corner % 3]).ToArray() : corners;
        }
        int Uv(FbxMaterialMapInput map) => projection.Merged && map.PolygonCornerCoordinates is not null ? projection.BaseUvCount : map.TextureCoordinateSet;
        return input with
        {
            BakedMaps = input.BakedMaps.Where(map => map.PrimitiveKeys.Contains(part.Source.Key)).Select(map =>
                map with { PrimitiveKeys = [part.OutputKey], TextureCoordinateSet = Uv(map), PolygonCornerCoordinates = Corners(map) }).ToArray(),
            StagedMaps = input.StagedMaps.Where(map => map.PrimitiveKeys.Contains(part.Source.Key)).Select(map =>
                map with { PrimitiveKeys = [part.OutputKey], TextureCoordinateSet = Uv(map), PolygonCornerCoordinates = Corners(map) }).ToArray()
        };
    }
}
