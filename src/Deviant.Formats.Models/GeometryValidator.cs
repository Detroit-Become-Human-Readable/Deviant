using System.Numerics;
using Deviant.Assets;

namespace Deviant.Formats.Models;

public enum GeometryDiagnosticSeverity { Information, Warning, Error }

public sealed record GeometryDiagnostic(string Code, GeometryDiagnosticSeverity Severity, string Message,
    string? Subject = null);

public sealed record GeometryValidationReport(bool IsValid, IReadOnlyList<GeometryDiagnostic> Diagnostics)
{
    public int ErrorCount => Diagnostics.Count(item => item.Severity == GeometryDiagnosticSeverity.Error);
    public int WarningCount => Diagnostics.Count(item => item.Severity == GeometryDiagnosticSeverity.Warning);
}

/// <summary>Validates decoded geometry without assigning meaning to source type labels.</summary>
public static class GeometryValidator
{
    private const float BoundsTolerance = 1e-4f;

    public static GeometryValidationReport Validate(GeometryAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var diagnostics = new List<GeometryDiagnostic>();
        var primitives = new Dictionary<string, MeshPrimitive>(StringComparer.Ordinal);
        foreach (var primitive in asset.Primitives)
        {
            if (string.IsNullOrWhiteSpace(primitive.Key))
            {
                Add("PrimitiveKeyEmpty", GeometryDiagnosticSeverity.Error, "A mesh primitive has no stable key.");
                continue;
            }
            if (!primitives.TryAdd(primitive.Key, primitive))
                Add("PrimitiveKeyDuplicate", GeometryDiagnosticSeverity.Error, "Mesh primitive keys must be unique.", primitive.Key);
            ValidatePrimitive(primitive);
        }

        var nodes = new Dictionary<string, GeometryNode>(StringComparer.Ordinal);
        foreach (var node in asset.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Key))
            {
                Add("NodeKeyEmpty", GeometryDiagnosticSeverity.Error, "A geometry node has no stable key.");
                continue;
            }
            if (!nodes.TryAdd(node.Key, node))
                Add("NodeKeyDuplicate", GeometryDiagnosticSeverity.Error, "Geometry node keys must be unique.", node.Key);
            foreach (var primitiveKey in node.PrimitiveKeys)
                if (!primitives.ContainsKey(primitiveKey))
                    Add("NodePrimitiveMissing", GeometryDiagnosticSeverity.Error,
                        "A geometry node points to a primitive that is absent from the asset.", node.Key);
            ValidateTransform(node);
        }

        foreach (var node in nodes.Values)
            if (node.ParentKey is { } parent && !nodes.ContainsKey(parent))
                Add("NodeParentMissing", GeometryDiagnosticSeverity.Error, "A geometry node's parent key is unresolved.", node.Key);

        ValidateNodeCycles(nodes);
        if (asset.Coordinates.Evidence?.Verified != true)
            Add("CoordinateProfileUnverified", GeometryDiagnosticSeverity.Warning,
                "The coordinate profile is not backed by verified source evidence.", asset.Coordinates.Id);
        if (asset.Nodes.Count == 0)
            Add("GeometryNodesMissing", GeometryDiagnosticSeverity.Warning,
                "The asset has no scene nodes; unplaced primitives remain available in the primitive list.");
        if (asset.Primitives.Count == 0)
            Add("GeometryPrimitivesMissing", GeometryDiagnosticSeverity.Error, "The asset has no mesh primitives.");

        return new GeometryValidationReport(
            diagnostics.All(item => item.Severity != GeometryDiagnosticSeverity.Error), diagnostics);

        void Add(string code, GeometryDiagnosticSeverity severity, string message, string? subject = null) =>
            diagnostics.Add(new GeometryDiagnostic(code, severity, message, subject));

        void ValidatePrimitive(MeshPrimitive primitive)
        {
            if (primitive.Positions.Count == 0)
                Add("PositionsEmpty", GeometryDiagnosticSeverity.Error, "A mesh primitive has no positions.", primitive.Key);
            for (var i = 0; i < primitive.Positions.Count; i++)
                if (!IsFinite(primitive.Positions[i]))
                    Add("PositionNonFinite", GeometryDiagnosticSeverity.Error, $"Position {i} contains a non-finite component.", primitive.Key);

            if (primitive.Normals is { } normals)
            {
                if (normals.Count != primitive.Positions.Count)
                    Add("NormalCountMismatch", GeometryDiagnosticSeverity.Error, "Normal count must match the position count.", primitive.Key);
                for (var i = 0; i < normals.Count; i++)
                    if (!IsFinite(normals[i])) Add("NormalNonFinite", GeometryDiagnosticSeverity.Error, $"Normal {i} contains a non-finite component.", primitive.Key);
            }

            if (primitive.Tangents is { } tangents)
            {
                if (tangents.Count != primitive.Positions.Count)
                    Add("TangentCountMismatch", GeometryDiagnosticSeverity.Error, "Tangent count must match the position count.", primitive.Key);
                for (var i = 0; i < tangents.Count; i++)
                    if (!IsFinite(tangents[i])) Add("TangentNonFinite", GeometryDiagnosticSeverity.Error, $"Tangent {i} contains a non-finite component.", primitive.Key);
            }

            if (primitive.TextureCoordinates is { } textureSets)
            {
                for (var setIndex = 0; setIndex < textureSets.Count; setIndex++)
                {
                    var set = textureSets[setIndex];
                    if (set.Count != primitive.Positions.Count)
                        Add("TextureCoordinateCountMismatch", GeometryDiagnosticSeverity.Error,
                            $"Texture coordinate set {setIndex} must match the position count.", primitive.Key);
                    for (var vertex = 0; vertex < set.Count; vertex++)
                        if (!IsFinite(set[vertex]))
                            Add("TextureCoordinateNonFinite", GeometryDiagnosticSeverity.Error,
                                $"Texture coordinate set {setIndex}, vertex {vertex}, contains a non-finite component.", primitive.Key);
                }
            }

            ValidateTopology(primitive);
            for (var i = 0; i < primitive.Indices.Count; i++)
                if (primitive.Indices[i] >= primitive.Positions.Count)
                    Add("IndexOutOfRange", GeometryDiagnosticSeverity.Error,
                        $"Index {i} points beyond the position array.", primitive.Key);
            if (primitive.IndexFormat == IndexElementFormat.UInt16 && primitive.Indices.Any(index => index > ushort.MaxValue))
                Add("IndexFormatMismatch", GeometryDiagnosticSeverity.Error,
                    "The index format is UInt16 but an index exceeds UInt16.MaxValue.", primitive.Key);

            if (primitive.Submeshes.Count > 0)
            {
                var sortedRanges = primitive.Submeshes.OrderBy(item => item.FirstIndex).ToArray();
                for (var i = 0; i < sortedRanges.Length; i++)
                {
                    var submesh = sortedRanges[i];
                    if (submesh.FirstIndex < 0 || submesh.IndexCount <= 0 ||
                        submesh.FirstIndex > primitive.Indices.Count - submesh.IndexCount)
                        Add("SubmeshRangeInvalid", GeometryDiagnosticSeverity.Error,
                            "A submesh index range is outside the primitive index buffer.", primitive.Key);
                    else if (primitive.Topology is (PrimitiveTopology.Triangles or PrimitiveTopology.Lines) &&
                             (submesh.FirstIndex % (primitive.Topology == PrimitiveTopology.Triangles ? 3 : 2) != 0 ||
                              submesh.IndexCount % (primitive.Topology == PrimitiveTopology.Triangles ? 3 : 2) != 0))
                        Add("SubmeshTopologyRangeInvalid", GeometryDiagnosticSeverity.Error,
                            "A submesh range splits a primitive group.", primitive.Key);
                    if (submesh.Materials.Count > 1)
                        Add("SubmeshMaterialAmbiguous", GeometryDiagnosticSeverity.Warning,
                            "A submesh carries multiple material references; the renderer must retain the ambiguity.", primitive.Key);
                    if (i > 0 && sortedRanges[i - 1].FirstIndex + sortedRanges[i - 1].IndexCount > submesh.FirstIndex)
                        Add("SubmeshRangesOverlap", GeometryDiagnosticSeverity.Warning,
                            "Submesh index ranges overlap.", primitive.Key);
                }
            }

            ValidateBounds(primitive);
            ValidateSkin(primitive);
        }

        void ValidateTopology(MeshPrimitive primitive)
        {
            var count = primitive.Indices.Count;
            var valid = primitive.Topology switch
            {
                PrimitiveTopology.Points => count > 0,
                PrimitiveTopology.Lines => count > 0 && count % 2 == 0,
                PrimitiveTopology.LineStrip => count >= 2,
                PrimitiveTopology.Triangles => count > 0 && count % 3 == 0,
                PrimitiveTopology.TriangleStrip or PrimitiveTopology.TriangleFan => count >= 3,
                _ => false
            };
            if (!valid)
                Add("TopologyCountInvalid", GeometryDiagnosticSeverity.Error,
                    "The index count is invalid for the declared primitive topology.", primitive.Key);
        }

        void ValidateBounds(MeshPrimitive primitive)
        {
            if (primitive.Bounds is not { } bounds) return;
            if (!IsFinite(bounds.Minimum) || !IsFinite(bounds.Maximum) ||
                bounds.Minimum.X > bounds.Maximum.X || bounds.Minimum.Y > bounds.Maximum.Y || bounds.Minimum.Z > bounds.Maximum.Z)
            {
                Add("BoundsInvalid", GeometryDiagnosticSeverity.Error, "Geometry bounds are non-finite or inverted.", primitive.Key);
                return;
            }
            for (var i = 0; i < primitive.Positions.Count; i++)
            {
                var p = primitive.Positions[i];
                if (p.X < bounds.Minimum.X - BoundsTolerance || p.Y < bounds.Minimum.Y - BoundsTolerance || p.Z < bounds.Minimum.Z - BoundsTolerance ||
                    p.X > bounds.Maximum.X + BoundsTolerance || p.Y > bounds.Maximum.Y + BoundsTolerance || p.Z > bounds.Maximum.Z + BoundsTolerance)
                {
                    Add("BoundsDoNotContainPositions", GeometryDiagnosticSeverity.Warning,
                        $"Position {i} lies outside the reported bounds.", primitive.Key);
                    return;
                }
            }
        }

        void ValidateSkin(MeshPrimitive primitive)
        {
            if (primitive.Skin is not { } skin) return;
            var palette = skin.Palette.OrderBy(item => item.Slot).ToArray();
            for (var i = 0; i < palette.Length; i++)
                if (palette[i].Slot != i)
                    Add("SkinPaletteSlotsInvalid", GeometryDiagnosticSeverity.Error,
                        "Skin palette slots must be unique and contiguous from zero.", primitive.Key);
            if (skin.VertexInfluences.Count != primitive.Positions.Count)
                Add("SkinInfluenceCountMismatch", GeometryDiagnosticSeverity.Error,
                    "Skin influence rows must match the position count.", primitive.Key);
            for (var vertex = 0; vertex < skin.VertexInfluences.Count; vertex++)
            {
                var influences = skin.VertexInfluences[vertex];
                if (influences.Count == 0)
                    Add("SkinInfluencesEmpty", GeometryDiagnosticSeverity.Error,
                        $"Vertex {vertex} has no source skin influences.", primitive.Key);
                foreach (var influence in influences)
                {
                    if (influence.PaletteSlot < 0 || influence.PaletteSlot >= skin.Palette.Count)
                        Add("SkinPaletteSlotOutOfRange", GeometryDiagnosticSeverity.Error,
                            $"Vertex {vertex} references an absent palette slot.", primitive.Key);
                    if (!float.IsFinite(influence.RawWeight) || influence.RawWeight < 0)
                        Add("SkinWeightInvalid", GeometryDiagnosticSeverity.Error,
                            $"Vertex {vertex} has a non-finite or negative source weight.", primitive.Key);
                }
            }
        }

        void ValidateTransform(GeometryNode node)
        {
            if (node.Transform is not { } transform) return;
            if (transform.Elements.Count != 16)
                Add("TransformElementCountInvalid", GeometryDiagnosticSeverity.Error,
                    "A source transform must retain all 16 matrix elements.", node.Key);
            else if (transform.Elements.Any(value => !float.IsFinite(value)))
                Add("TransformNonFinite", GeometryDiagnosticSeverity.Error,
                    "A source transform contains a non-finite value.", node.Key);
            if (transform.Layout == MatrixLayout.Unknown || transform.VectorSide == VectorMultiplicationSide.Unknown)
                Add("TransformConventionUnknown", GeometryDiagnosticSeverity.Warning,
                    "The source transform is preserved but its layout or multiplication side is unknown.", node.Key);
        }

        void ValidateNodeCycles(IReadOnlyDictionary<string, GeometryNode> allNodes)
        {
            var state = new Dictionary<string, byte>(StringComparer.Ordinal);
            foreach (var key in allNodes.Keys) Visit(key);

            void Visit(string key)
            {
                if (state.TryGetValue(key, out var current))
                {
                    if (current == 1) Add("NodeHierarchyCycle", GeometryDiagnosticSeverity.Error,
                        "The source node hierarchy contains a cycle.", key);
                    return;
                }
                state[key] = 1;
                if (allNodes.TryGetValue(key, out var node) && node.ParentKey is { } parent && allNodes.ContainsKey(parent)) Visit(parent);
                state[key] = 2;
            }
        }
    }

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool IsFinite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
