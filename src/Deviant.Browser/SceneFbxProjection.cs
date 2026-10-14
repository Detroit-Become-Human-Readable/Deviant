using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;

namespace Deviant.Browser;

public enum SceneExportPlacement { Scene, Centered }
public sealed record SceneFbxExportRequest(IReadOnlyList<RenderMeshSelection> Selection,
    bool Merge = false, SceneExportPlacement Placement = SceneExportPlacement.Scene, bool IncludeHidden = false)
{
    public bool PreserveInstances { get; init; }
}
public sealed record SceneFbxPart(string OutputKey, RenderOccurrence Occurrence, RenderMesh Mesh,
    MeshPrimitive Source, bool Mirrored, int FirstIndex, int FirstVertex);
public sealed record SceneFbxProjectionResult(GeometryAsset Geometry, IReadOnlyList<SceneFbxPart> Parts,
    int BaseUvCount, bool Merged);

public static class SceneFbxProjection
{
    public static SceneFbxProjectionResult Build(RenderAssetSnapshot snapshot, SceneFbxExportRequest request,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(request);
        var selected = request.Selection.Distinct().ToArray();
        if (selected.Length == 0) throw new InvalidDataException("Select at least one mesh.");
        var occurrences = snapshot.Occurrences.ToDictionary(row => row.Key, StringComparer.Ordinal);
        var projected = new List<MeshPrimitive>(); var sourceParts = new List<SceneFbxPart>();
        var nodes = new List<GeometryNode>(); var emitted = new HashSet<(string, string)>();
        var shared = new HashSet<string>(StringComparer.Ordinal);
        var preserveInstances = request.PreserveInstances && !request.Merge;
        CoordinateProfile? coordinates = null;
        foreach (var choice in selected)
        {
            token.ThrowIfCancellationRequested();
            if (!occurrences.TryGetValue(choice.OccurrenceKey, out var occurrence))
                throw new InvalidDataException("The mesh selection belongs to another scene snapshot.");
            if (!request.IncludeHidden && !occurrence.SourceVisible) continue;
            var mesh = snapshot.Meshes[occurrence.GeometryKey];
            coordinates ??= mesh.Geometry.Coordinates;
            if (coordinates with { Evidence = null } != mesh.Geometry.Coordinates with { Evidence = null })
                throw new InvalidDataException("The selected meshes use different coordinate profiles.");
            var choices = mesh.Geometry.Primitives.Where(part => choice.PrimitiveKey is null || part.Key == choice.PrimitiveKey).ToArray();
            if (choices.Length == 0) throw new InvalidDataException("The selected mesh part is unavailable.");
            if (!Matrix4x4.Invert(occurrence.World, out var inverse)) throw new InvalidDataException("The mesh has a singular scene transform.");
            var normalMatrix = Matrix4x4.Transpose(inverse); var mirrored = occurrence.World.GetDeterminant() < 0;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(occurrence.Key))).ToLowerInvariant()[..16];
            var nodeKey = $"{occurrence.Label} [{hash}]";
            var keys = new List<string>();
            foreach (var source in choices)
            {
                if (!emitted.Add((occurrence.Key, source.Key))) continue;
                token.ThrowIfCancellationRequested();
                if (source.Topology != PrimitiveTopology.Triangles || source.Indices.Count % 3 != 0)
                    throw new InvalidDataException("Scene FBX export requires triangle meshes.");
                var geometryHash = preserveInstances ? Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(mesh.Key + "/" + source.Key + "/" + mirrored))).ToLowerInvariant()[..16] : hash;
                var key = $"scene/{geometryHash}/part/{mesh.Geometry.Primitives.ToList().IndexOf(source)}";
                keys.Add(key);
                if (preserveInstances && !shared.Add(key)) continue;
                var indices = source.Indices.ToArray();
                if (mirrored) for (var i = 0; i < indices.Length; i += 3) (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
                var positions = preserveInstances ? source.Positions : source.Positions.Select(position => Vector3.Transform(position, occurrence.World)).ToArray();
                if (positions.Any(position => !Finite(position))) throw new InvalidDataException("The transformed mesh contains non-finite positions.");
                var normals = preserveInstances ? source.Normals : source.Normals?.Select(normal => Normalize(Vector3.TransformNormal(normal, normalMatrix))).ToArray();
                var tangents = preserveInstances ? source.Tangents : source.Tangents?.Select(tangent =>
                {
                    var value = Normalize(Vector3.TransformNormal(new(tangent.X, tangent.Y, tangent.Z), occurrence.World));
                    return new Vector4(value, mirrored ? -tangent.W : tangent.W);
                }).ToArray();
                var submeshes = source.Submeshes.Select((submesh, i) => submesh with { Key = key + "/material/" + i }).ToArray();
                projected.Add(source with { Key = key, Positions = positions, Indices = indices, Normals = normals,
                    Tangents = tangents, Submeshes = submeshes, Skin = null, Bounds = Bounds(positions) });
                sourceParts.Add(new(key, occurrence, mesh, source, mirrored, 0, 0));
            }
            if (keys.Count > 0)
            {
                var existing = nodes.FindIndex(node => node.Key == nodeKey);
                if (existing < 0) nodes.Add(new(nodeKey, null, new(IdentifierNamespace.Native, mesh.Identity.Key, mesh.Identity), keys,
                    preserveInstances ? Transform(occurrence.World) : null));
                else nodes[existing] = nodes[existing] with { PrimitiveKeys = nodes[existing].PrimitiveKeys.Concat(keys).ToArray() };
            }
        }
        if (projected.Count == 0) throw new InvalidDataException("No visible meshes are selected.");
        if (!request.Merge && snapshot.Hierarchy.Count > 0)
        {
            var hierarchy = snapshot.Hierarchy.ToDictionary(item => item.Key, StringComparer.Ordinal);
            var byOccurrence = snapshot.Hierarchy.Where(item => item.OccurrenceKey is not null)
                .GroupBy(item => item.OccurrenceKey!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var hierarchyNodes = new Dictionary<string, GeometryNode>(StringComparer.Ordinal);
            foreach (var occurrence in selected.Select(choice => occurrences[choice.OccurrenceKey]).DistinctBy(item => item.Key))
            {
                if (!byOccurrence.TryGetValue(occurrence.Key, out var row)) continue;
                var parentKey = HierarchyKey(row.Key);
                var occurrenceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(occurrence.Key))).ToLowerInvariant()[..16];
                var modelNode = nodes.FindIndex(node => node.Key == $"{occurrence.Label} [{occurrenceHash}]");
                if (modelNode >= 0) nodes[modelNode] = nodes[modelNode] with { ParentKey = parentKey };
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (!visited.Add(row.Key) || visited.Count > 256) throw new InvalidDataException("The scene hierarchy contains a cycle or exceeds its depth limit.");
                    var parent = row.ParentKey is { } key && hierarchy.TryGetValue(key, out var ancestor) ? ancestor : null;
                    hierarchyNodes.TryAdd(row.Key, new(HierarchyKey(row.Key), parent is null ? null : HierarchyKey(parent.Key),
                        new(IdentifierNamespace.Native, row.Node.Key, row.Node), [], Names: [new(row.Label, NameOrigin.Stored, "scene hierarchy")]));
                    if (parent is null) break;
                    row = parent;
                }
            }
            nodes.AddRange(hierarchyNodes.Values);
        }
        var primitivesByKey = projected.ToDictionary(primitive => primitive.Key, StringComparer.Ordinal);
        var allBounds = preserveInstances && request.Placement == SceneExportPlacement.Centered ? Bounds(nodes.Where(node => node.Transform is not null).SelectMany(node =>
            node.PrimitiveKeys.SelectMany(key => primitivesByKey[key].Positions.Select(position =>
                Vector3.Transform(position, Matrix(node.Transform!)))))) : Bounds(projected.SelectMany(part => part.Positions));
        var center = request.Placement == SceneExportPlacement.Centered ? (allBounds.Minimum + allBounds.Maximum) * .5f : Vector3.Zero;
        if (center != Vector3.Zero && preserveInstances)
            for (var i = 0; i < nodes.Count; i++)
                if (nodes[i].Transform is { } transform)
                {
                    var matrix = Matrix(transform); matrix.Translation -= center;
                    nodes[i] = nodes[i] with { Transform = Transform(matrix) };
                }
        if (center != Vector3.Zero && !preserveInstances)
            for (var i = 0; i < projected.Count; i++) projected[i] = projected[i] with
            { Positions = projected[i].Positions.Select(position => position - center).ToArray(), Bounds = new(projected[i].Bounds!.Minimum - center, projected[i].Bounds!.Maximum - center) };
        var baseUvs = projected.Max(part => part.TextureCoordinates?.Count ?? 0);
        if (request.Merge)
        {
            var positions = new List<Vector3>(); var indices = new List<uint>(); var submeshes = new List<GeometrySubmesh>();
            var normals = projected.All(part => part.Normals is not null) ? new List<Vector3>() : null;
            var tangents = projected.All(part => part.Tangents is not null) ? new List<Vector4>() : null;
            var colors = projected.Any(part => part.Colors is not null) ? new List<Vector4>() : null;
            var uv = Enumerable.Range(0, baseUvs).Select(_ => new List<Vector2>()).ToArray();
            for (var i = 0; i < projected.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var part = projected[i]; var firstVertex = positions.Count; var firstIndex = indices.Count;
                sourceParts[i] = sourceParts[i] with { OutputKey = "scene/merged", FirstIndex = firstIndex, FirstVertex = firstVertex };
                positions.AddRange(part.Positions); indices.AddRange(part.Indices.Select(index => checked(index + (uint)firstVertex)));
                submeshes.AddRange(part.Submeshes.Select(submesh => submesh with { FirstIndex = checked(submesh.FirstIndex + firstIndex) }));
                normals?.AddRange(part.Normals!); tangents?.AddRange(part.Tangents!);
                colors?.AddRange(part.Colors ?? Enumerable.Repeat(Vector4.One, part.Positions.Count).ToArray());
                for (var set = 0; set < uv.Length; set++) uv[set].AddRange(part.TextureCoordinates is { } sets && set < sets.Count
                    ? sets[set] : Enumerable.Repeat(Vector2.Zero, part.Positions.Count).ToArray());
            }
            projected = [new("scene/merged", PrimitiveTopology.Triangles, positions, indices, IndexElementFormat.UInt32,
                submeshes, normals, tangents, uv.Select(set => (IReadOnlyList<Vector2>)set).ToArray(), Bounds(positions), Colors: colors)];
            nodes = [new("Merged selection", null, new(IdentifierNamespace.Native, "scene/merged"), ["scene/merged"])];
        }
        return new(new(coordinates!, nodes, projected, new Dictionary<string, string>()), sourceParts, baseUvs, request.Merge);
    }
    private static Vector3 Normalize(Vector3 value) => Finite(value) && value.LengthSquared() > 1e-20f
        ? Vector3.Normalize(value) : throw new InvalidDataException("The mesh contains an invalid normal or tangent.");
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static string HierarchyKey(string key) => "scene/node/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..16];
    private static SourceTransform Transform(Matrix4x4 m) => new(
        [m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44],
        MatrixLayout.RowMajor, VectorMultiplicationSide.Row);
    private static Matrix4x4 Matrix(SourceTransform transform)
    {
        var e = transform.Elements;
        return new(e[0],e[1],e[2],e[3],e[4],e[5],e[6],e[7],e[8],e[9],e[10],e[11],e[12],e[13],e[14],e[15]);
    }
    private static GeometryBounds Bounds(IEnumerable<Vector3> points)
    {
        var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
        foreach (var point in points) { minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point); }
        return new(minimum, maximum);
    }
}
