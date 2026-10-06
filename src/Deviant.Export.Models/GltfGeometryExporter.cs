using System.Numerics;
using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Export;
using Deviant.Formats.Models;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace Deviant.Export.Models;

public sealed record GltfGeometryLoss(string Code, string Subject, string Detail,
    IReadOnlyList<ResourceReference>? References = null);

public sealed record GltfGeometryExportReport(ResourceIdentity Identity, SourceRevision Revision,
    string OutputFileName, string OutputSha256, int MeshCount, int NodeCount,
    IReadOnlyList<GltfGeometryLoss> Losses);

public sealed record GltfGeometryExportOptions(bool Overwrite = false, int MaximumTriangles = 5_000_000);

/// <summary>Exports validated static geometry to GLB. It does not decode source resources or bind a skeleton.</summary>
public sealed class GltfGeometryExporter
{
    private const float PlaceholderColor = 0.65f;

    public async Task<GltfGeometryExportReport> ExportAsync(DecodeResult<GeometryAsset> decoded,
        string sourceRoot, string destination, GltfGeometryExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        options ??= new();
        if (options.MaximumTriangles < 1) throw new ArgumentOutOfRangeException(nameof(options.MaximumTriangles));
        if (decoded.Value is null)
            throw new GeometryExportException("GeometryDecodeFailed", "The decoded geometry result has no value.",
                decoded.Diagnostics.Select(message => new GltfGeometryLoss("DecodeDiagnostic", decoded.Identity.Key, message)).ToArray());

        var asset = decoded.Value;
        var validation = GeometryValidator.Validate(asset);
        if (!validation.IsValid)
            throw new GeometryExportException("GeometryValidationFailed", "The geometry IR contains invalid references or vertex data.",
                validation.Diagnostics.Where(item => item.Severity == GeometryDiagnosticSeverity.Error)
                    .Select(item => new GltfGeometryLoss(item.Code, item.Subject ?? "asset", item.Message)).ToArray());
        var basis = CreateBasis(asset.Coordinates);
        if (!asset.Coordinates.HasVerifiedBasis)
            throw new GeometryExportException("CoordinateProfileUnverified", "A verified coordinate profile is required before creating GLB geometry.",
                [new("CoordinateProfileUnverified", asset.Coordinates.Id, "Axis mapping, handedness, and unit scale must be supported by source evidence.")]);

        if (asset.Primitives.Any(primitive => primitive.Skin is not null))
            throw new GeometryExportException("SkinExportUnsupported", "Static GLB export does not flatten or omit skin bindings.",
                [new("SkinExportUnsupported", decoded.Identity.Key, "A skeleton and skin export contract is not part of the static writer.")]);
        if (asset.Primitives.Any(primitive => primitive.Topology != PrimitiveTopology.Triangles))
            throw new GeometryExportException("TopologyUnsupported", "The static GLB writer currently accepts triangle-list primitives only.",
                asset.Primitives.Where(primitive => primitive.Topology != PrimitiveTopology.Triangles)
                    .Select(primitive => new GltfGeometryLoss("TopologyUnsupported", primitive.Key, primitive.Topology.ToString())).ToArray());

        var totalTriangles = asset.Primitives.Sum(primitive => (long)primitive.Indices.Count / 3);
        if (totalTriangles > options.MaximumTriangles)
            throw new GeometryExportException("TriangleLimit", "The selected geometry exceeds the configured triangle limit.",
                [new("TriangleLimit", decoded.Identity.Key, $"Requested {totalTriangles}; limit is {options.MaximumTriangles}.")]);

        var losses = new List<GltfGeometryLoss>();
        var output = Path.GetFullPath(destination);
        OutputSafety.ValidateFilePath(output, sourceRoot);
        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        using var directoryPin = OutputSafety.PinDirectory(directory);
        OutputSafety.ValidateFilePath(output, sourceRoot);
        if (!options.Overwrite && File.Exists(output))
            throw new GeometryExportException("OutputExists", "The destination already exists.",
                [new("OutputExists", Path.GetFileName(output), "Choose a new destination or enable overwrite.")]);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        OutputSafety.ValidateFilePath(temporary, sourceRoot);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scene = BuildScene(asset, basis, losses, cancellationToken);
            var model = scene.ToGltf2();
            model.SaveGLB(temporary);
            cancellationToken.ThrowIfCancellationRequested();
            OutputSafety.ValidateFilePath(output, sourceRoot);
            var hash = await HashFileAsync(temporary, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, output, options.Overwrite);
            return new(decoded.Identity, decoded.Revision, Path.GetFileName(output), hash,
                asset.Primitives.Count, asset.Nodes.Count == 0 ? asset.Primitives.Count : asset.Nodes.Count,
                losses.AsReadOnly());
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }

    private static SceneBuilder BuildScene(GeometryAsset asset, Matrix4x4 basis,
        List<GltfGeometryLoss> losses, CancellationToken token)
    {
        var scene = new SceneBuilder();
        var byKey = asset.Primitives.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var meshCache = new Dictionary<string, IMeshBuilder<MaterialBuilder>>(StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in asset.Nodes)
            foreach (var primitiveKey in node.PrimitiveKeys) referenced.Add(primitiveKey);

        if (asset.Nodes.Count == 0)
        {
            foreach (var primitive in asset.Primitives)
            {
                token.ThrowIfCancellationRequested();
                var mesh = BuildMesh(primitive, basis, losses, token);
                var node = new NodeBuilder(primitive.Key);
                scene.AddRigidMesh(mesh, node);
            }
            return scene;
        }

        var nodes = asset.Nodes.ToDictionary(item => item.Key, item => new NodeBuilder(item.Key), StringComparer.Ordinal);
        foreach (var node in asset.Nodes)
        {
            token.ThrowIfCancellationRequested();
            var current = nodes[node.Key];
            if (node.Transform is { } transform) current.LocalMatrix = ConvertTransform(transform, asset.Coordinates, basis);
            if (node.ParentKey is { } parent) nodes[parent].AddNode(current);
            else scene.AddNode(current);
        }

        foreach (var node in asset.Nodes)
        {
            var nodeBuilder = nodes[node.Key];
            foreach (var primitiveKey in node.PrimitiveKeys)
            {
                token.ThrowIfCancellationRequested();
                if (!meshCache.TryGetValue(primitiveKey, out var mesh))
                {
                    mesh = BuildMesh(byKey[primitiveKey], basis, losses, token);
                    meshCache.Add(primitiveKey, mesh);
                }
                scene.AddRigidMesh(mesh, nodeBuilder);
            }
        }

        foreach (var primitive in asset.Primitives.Where(item => !referenced.Contains(item.Key)))
        {
            token.ThrowIfCancellationRequested();
            scene.AddRigidMesh(BuildMesh(primitive, basis, losses, token), new NodeBuilder(primitive.Key));
        }
        return scene;
    }

    private static IMeshBuilder<MaterialBuilder> BuildMesh(MeshPrimitive primitive, Matrix4x4 basis,
        List<GltfGeometryLoss> losses, CancellationToken token)
    {
        if (primitive.Normals is null)
            losses.Add(new("NormalsMissing", primitive.Key, "The source has no normals; GLB geometry will omit NORMAL."));
        if (primitive.Tangents is { Count: > 0 })
            losses.Add(new("TangentsOmitted", primitive.Key, "Tangents remain in the decoded IR but are not written by this exporter."));
        if (primitive.TextureCoordinates is { Count: > 1 })
            losses.Add(new("AdditionalTextureCoordinatesOmitted", primitive.Key,
                $"The GLB writer emits TEXCOORD_0 only; {primitive.TextureCoordinates.Count - 1} set(s) remain in the decoded IR."));

        var sourceUv = primitive.TextureCoordinates is { Count: > 0 } ? primitive.TextureCoordinates[0] : null;
        var submeshes = primitive.Submeshes.Count == 0
            ? new[] { new GeometrySubmesh("default", 0, primitive.Indices.Count, []) }
            : primitive.Submeshes;
        var hasNormals = primitive.Normals is not null;
        var hasUv = sourceUv is not null;

        if (hasNormals && hasUv) return BuildWithNormalsAndUv(primitive, submeshes, sourceUv!, basis, losses, token);
        if (hasNormals) return BuildWithNormals(primitive, submeshes, basis, losses, token);
        if (hasUv) return BuildWithUv(primitive, submeshes, sourceUv!, basis, losses, token);
        return BuildPositionsOnly(primitive, submeshes, basis, losses, token);
    }

    private static IMeshBuilder<MaterialBuilder> BuildWithNormalsAndUv(MeshPrimitive source,
        IReadOnlyList<GeometrySubmesh> submeshes, IReadOnlyList<Vector2> uvs, Matrix4x4 basis,
        List<GltfGeometryLoss> losses, CancellationToken token)
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(source.Key);
        var reflected = basis.GetDeterminant() < 0;
        foreach (var submesh in submeshes)
        {
            token.ThrowIfCancellationRequested();
            var primitive = mesh.UsePrimitive(CreateMaterial(source.Key, submesh, losses));
            for (var i = submesh.FirstIndex; i < submesh.FirstIndex + submesh.IndexCount; i += 3)
            {
                token.ThrowIfCancellationRequested();
                var a = Vertex(source, uvs, basis, source.Indices[i]);
                var b = Vertex(source, uvs, basis, source.Indices[i + (reflected ? 2 : 1)]);
                var c = Vertex(source, uvs, basis, source.Indices[i + (reflected ? 1 : 2)]);
                primitive.AddTriangle(a, b, c);
            }
        }
        return mesh;
    }

    private static IMeshBuilder<MaterialBuilder> BuildWithNormals(MeshPrimitive source,
        IReadOnlyList<GeometrySubmesh> submeshes, Matrix4x4 basis, List<GltfGeometryLoss> losses, CancellationToken token)
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty>(source.Key);
        var reflected = basis.GetDeterminant() < 0;
        foreach (var submesh in submeshes)
        {
            token.ThrowIfCancellationRequested();
            var primitive = mesh.UsePrimitive(CreateMaterial(source.Key, submesh, losses));
            for (var i = submesh.FirstIndex; i < submesh.FirstIndex + submesh.IndexCount; i += 3)
            {
                token.ThrowIfCancellationRequested();
                var a = VertexNormal(source, basis, source.Indices[i]);
                var b = VertexNormal(source, basis, source.Indices[i + (reflected ? 2 : 1)]);
                var c = VertexNormal(source, basis, source.Indices[i + (reflected ? 1 : 2)]);
                primitive.AddTriangle(a, b, c);
            }
        }
        return mesh;
    }

    private static IMeshBuilder<MaterialBuilder> BuildWithUv(MeshPrimitive source,
        IReadOnlyList<GeometrySubmesh> submeshes, IReadOnlyList<Vector2> uvs, Matrix4x4 basis,
        List<GltfGeometryLoss> losses, CancellationToken token)
    {
        var mesh = new MeshBuilder<VertexPosition, VertexTexture1, VertexEmpty>(source.Key);
        var reflected = basis.GetDeterminant() < 0;
        foreach (var submesh in submeshes)
        {
            token.ThrowIfCancellationRequested();
            var primitive = mesh.UsePrimitive(CreateMaterial(source.Key, submesh, losses));
            for (var i = submesh.FirstIndex; i < submesh.FirstIndex + submesh.IndexCount; i += 3)
            {
                token.ThrowIfCancellationRequested();
                var a = VertexPositionUv(source, uvs, basis, source.Indices[i]);
                var b = VertexPositionUv(source, uvs, basis, source.Indices[i + (reflected ? 2 : 1)]);
                var c = VertexPositionUv(source, uvs, basis, source.Indices[i + (reflected ? 1 : 2)]);
                primitive.AddTriangle(a, b, c);
            }
        }
        return mesh;
    }

    private static IMeshBuilder<MaterialBuilder> BuildPositionsOnly(MeshPrimitive source,
        IReadOnlyList<GeometrySubmesh> submeshes, Matrix4x4 basis, List<GltfGeometryLoss> losses, CancellationToken token)
    {
        var mesh = new MeshBuilder<VertexPosition>(source.Key);
        var reflected = basis.GetDeterminant() < 0;
        foreach (var submesh in submeshes)
        {
            token.ThrowIfCancellationRequested();
            var primitive = mesh.UsePrimitive(CreateMaterial(source.Key, submesh, losses));
            for (var i = submesh.FirstIndex; i < submesh.FirstIndex + submesh.IndexCount; i += 3)
            {
                token.ThrowIfCancellationRequested();
                var a = new VertexPosition(ConvertPosition(source.Indices[i]));
                var b = new VertexPosition(ConvertPosition(source.Indices[i + (reflected ? 2 : 1)]));
                var c = new VertexPosition(ConvertPosition(source.Indices[i + (reflected ? 1 : 2)]));
                primitive.AddTriangle(a, b, c);
            }
        }
        return mesh;

        Vector3 ConvertPosition(uint index) => Vector3.Transform(source.Positions[checked((int)index)], basis);
    }

    private static VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> Vertex(MeshPrimitive source,
        IReadOnlyList<Vector2> uvs, Matrix4x4 basis, uint index)
    {
        var position = Vector3.Transform(source.Positions[checked((int)index)], basis);
        var normalMatrix = Matrix4x4.Transpose(Matrix4x4.Invert(basis, out var inverse) ? inverse : Matrix4x4.Identity);
        var normal = Vector3.Normalize(Vector3.TransformNormal(source.Normals![checked((int)index)], normalMatrix));
        return new(new VertexPositionNormal(position, normal), new VertexTexture1(uvs[checked((int)index)]));
    }

    private static VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> VertexNormal(MeshPrimitive source,
        Matrix4x4 basis, uint index)
    {
        var position = Vector3.Transform(source.Positions[checked((int)index)], basis);
        var inverse = Matrix4x4.Invert(basis, out var result) ? result : Matrix4x4.Identity;
        var normal = Vector3.Normalize(Vector3.TransformNormal(source.Normals![checked((int)index)], Matrix4x4.Transpose(inverse)));
        return new(new VertexPositionNormal(position, normal));
    }

    private static VertexBuilder<VertexPosition, VertexTexture1, VertexEmpty> VertexPositionUv(MeshPrimitive source,
        IReadOnlyList<Vector2> uvs, Matrix4x4 basis, uint index)
    {
        var position = Vector3.Transform(source.Positions[checked((int)index)], basis);
        return new(new VertexPosition(position), new VertexTexture1(uvs[checked((int)index)]));
    }

    private static MaterialBuilder CreateMaterial(string primitiveKey, GeometrySubmesh submesh,
        List<GltfGeometryLoss> losses)
    {
        string name;
        if (submesh.Materials.Count == 0)
        {
            name = $"{primitiveKey}_{submesh.Key}_placeholder";
            losses.Add(new("MaterialReferenceMissing", primitiveKey, $"Submesh {submesh.Key} has no source material reference."));
        }
        else if (submesh.Materials.Count == 1)
        {
            var reference = submesh.Materials[0];
            name = $"{primitiveKey}_{submesh.Key}_{reference.Namespace}_{reference.ResourceType?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}_{reference.Id}";
            losses.Add(new("MaterialValuesUnavailable", primitiveKey,
                $"Submesh {submesh.Key} retains a typed material reference but has no decoded material values.", submesh.Materials));
        }
        else
        {
            name = $"{primitiveKey}_{submesh.Key}_ambiguous_material";
            losses.Add(new("MaterialReferenceAmbiguous", primitiveKey,
                $"Submesh {submesh.Key} has {submesh.Materials.Count} material references; a neutral placeholder is used.", submesh.Materials));
        }
        return new MaterialBuilder(name)
            .WithMetallicRoughnessShader()
            .WithChannelParam(KnownChannel.BaseColor, KnownProperty.RGBA, new Vector4(PlaceholderColor, PlaceholderColor, PlaceholderColor, 1));
    }

    private static Matrix4x4 CreateBasis(CoordinateProfile profile)
    {
        if (profile.Axes is null || profile.MetersPerUnit is not > 0 || !double.IsFinite(profile.MetersPerUnit.Value))
            throw new GeometryExportException("CoordinateProfileIncomplete", "The coordinate basis or unit scale is missing.",
                [new("CoordinateProfileIncomplete", profile.Id, "A verified axis map and positive finite meters-per-unit value are required.")]);
        if (profile.Handedness == CoordinateHandedness.Unknown)
            throw new GeometryExportException("CoordinateHandednessUnknown", "Coordinate handedness must be known before GLB export.",
                [new("CoordinateHandednessUnknown", profile.Id, "Set the handedness in the evidence-backed coordinate profile.")]);

        var axes = new[] { profile.Axes.Right, profile.Axes.Up, profile.Axes.Forward };
        var sourceIndices = axes.Select(AxisIndex).ToArray();
        if (sourceIndices.Distinct().Count() != 3)
            throw new GeometryExportException("CoordinateAxisMapInvalid", "The coordinate axis map must use each source axis exactly once.",
                [new("CoordinateAxisMapInvalid", profile.Id, "Right, up, and forward must map to three distinct source axes.")]);
        var scale = checked((float)profile.MetersPerUnit.Value);
        if (!float.IsFinite(scale) || scale <= 0)
            throw new GeometryExportException("CoordinateScaleInvalid", "The coordinate scale does not fit in a finite single-precision value.",
                [new("CoordinateScaleInvalid", profile.Id, "MetersPerUnit must remain finite after conversion to float.")]);
        var matrix = new Matrix4x4();
        for (var destinationAxis = 0; destinationAxis < 3; destinationAxis++)
        {
            var sourceAxis = sourceIndices[destinationAxis];
            var sign = AxisSign(axes[destinationAxis]) * scale;
            Set(ref matrix, sourceAxis, destinationAxis, sign);
        }
        matrix.M44 = 1;
        var determinant = matrix.GetDeterminant();
        if (!float.IsFinite(determinant) || determinant == 0)
            throw new GeometryExportException("CoordinateBasisInvalid", "The coordinate axis mapping is singular.",
                [new("CoordinateBasisInvalid", profile.Id, "The coordinate transform cannot be inverted.")]);
        return matrix;
    }

    private static Matrix4x4 ConvertTransform(SourceTransform transform, CoordinateProfile profile, Matrix4x4 basis)
    {
        if (transform.Elements.Count != 16 || transform.Layout == MatrixLayout.Unknown || transform.VectorSide == VectorMultiplicationSide.Unknown)
            throw new GeometryExportException("TransformConventionUnknown", "A source node transform lacks a complete matrix convention.",
                [new("TransformConventionUnknown", "node", "The matrix layout and vector multiplication side must be decoded before export.")]);
        if (profile.MatrixLayout != MatrixLayout.Unknown && profile.MatrixLayout != transform.Layout)
            throw new GeometryExportException("TransformLayoutMismatch", "The node matrix layout conflicts with the coordinate profile.",
                [new("TransformLayoutMismatch", "node", "The source transform and coordinate profile declare different layouts.")]);
        if (profile.VectorSide != VectorMultiplicationSide.Unknown && profile.VectorSide != transform.VectorSide)
            throw new GeometryExportException("TransformVectorSideMismatch", "The node matrix convention conflicts with the coordinate profile.",
                [new("TransformVectorSideMismatch", "node", "The source transform and coordinate profile declare different vector sides.")]);
        var e = transform.Elements;
        var source = new Matrix4x4(
            e[0], e[1], e[2], e[3], e[4], e[5], e[6], e[7],
            e[8], e[9], e[10], e[11], e[12], e[13], e[14], e[15]);
        if (transform.Layout == MatrixLayout.ColumnMajor) source = Matrix4x4.Transpose(source);
        if (transform.VectorSide == VectorMultiplicationSide.Column) source = Matrix4x4.Transpose(source);
        if (!IsFinite(source))
            throw new GeometryExportException("TransformNonFinite", "A node transform contains a non-finite value.",
                [new("TransformNonFinite", "node", "All 16 matrix values must be finite.")]);
        if (!Matrix4x4.Invert(basis, out var inverseBasis))
            throw new GeometryExportException("CoordinateBasisInvalid", "The coordinate basis cannot be inverted.", []);
        // System.Numerics uses row vectors. This conjugation changes the source frame to glTF space.
        return inverseBasis * source * basis;
    }

    private static int AxisIndex(CoordinateAxis axis) => axis switch
    {
        CoordinateAxis.PositiveX or CoordinateAxis.NegativeX => 0,
        CoordinateAxis.PositiveY or CoordinateAxis.NegativeY => 1,
        CoordinateAxis.PositiveZ or CoordinateAxis.NegativeZ => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(axis))
    };

    private static float AxisSign(CoordinateAxis axis) => axis switch
    {
        CoordinateAxis.PositiveX or CoordinateAxis.PositiveY or CoordinateAxis.PositiveZ => 1,
        CoordinateAxis.NegativeX or CoordinateAxis.NegativeY or CoordinateAxis.NegativeZ => -1,
        _ => throw new ArgumentOutOfRangeException(nameof(axis))
    };

    private static void Set(ref Matrix4x4 matrix, int row, int column, float value)
    {
        if (row is < 0 or > 2 || column is < 0 or > 2) throw new ArgumentOutOfRangeException();
        var index = row * 4 + column;
        switch (index)
        {
            case 0: matrix.M11 = value; break; case 1: matrix.M12 = value; break; case 2: matrix.M13 = value; break;
            case 4: matrix.M21 = value; break; case 5: matrix.M22 = value; break; case 6: matrix.M23 = value; break;
            case 8: matrix.M31 = value; break; case 9: matrix.M32 = value; break; case 10: matrix.M33 = value; break;
            default: throw new ArgumentOutOfRangeException();
        }
    }

    private static bool IsFinite(Matrix4x4 matrix) =>
        float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) && float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14) &&
        float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) && float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24) &&
        float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) && float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34) &&
        float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) && float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);

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
}

public sealed class GeometryExportException(string code, string message, IReadOnlyList<GltfGeometryLoss> losses,
    Exception? inner = null) : IOException(message, inner)
{
    public string Code { get; } = code;
    public IReadOnlyList<GltfGeometryLoss> Losses { get; } = losses;
}
