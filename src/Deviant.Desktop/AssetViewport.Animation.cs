using System.IO;
using Deviant.Assets;
using HelixToolkit;
using HelixToolkit.SharpDX;
using Vector3 = System.Numerics.Vector3;

namespace Deviant.Desktop;

public sealed record ViewportPoseFrame(SourceRevision Revision, ulong Sequence,
    IReadOnlyDictionary<string, MeshPrimitive> Primitives);

public partial class AssetViewport
{
    private readonly Dictionary<Part, ViewportMeshGeometry> poseGeometry = [];
    private ulong poseSequence;

    public async Task<bool> UpdatePoseAsync(ViewportPoseFrame frame, CancellationToken token = default)
    {
        Dispatcher.VerifyAccess(); ArgumentNullException.ThrowIfNull(frame); token.ThrowIfCancellationRequested();
        var source = snapshot ?? throw new InvalidOperationException("Load a model first.");
        if (frame.Revision != source.Revision) throw new InvalidDataException("ViewportPoseSourceMismatch");
        if (frame.Sequence <= poseSequence) return false;
        long ownGeneration = generation;
        var selected = parts.Where(part => frame.Primitives.ContainsKey(part.PrimitiveKey)).ToArray();
        if (selected.Length == 0 || frame.Primitives.Keys.Any(key => !parts.Any(part => part.PrimitiveKey == key)))
            throw new InvalidDataException("ViewportPosePrimitiveMismatch");
        foreach (var part in selected)
        {
            var primitive = frame.Primitives[part.PrimitiveKey];
            var geometry = part.Model.Geometry;
            if (primitive.Key != part.PrimitiveKey || geometry is not ViewportMeshGeometry old || old.Positions is null ||
                primitive.Positions.Count != old.Positions.Count || primitive.Normals?.Count != primitive.Positions.Count ||
                primitive.Tangents is not null && primitive.Tangents.Count != primitive.Positions.Count ||
                !ViewportGeometry.HasUsableNormals(primitive, token) ||
                primitive.Tangents is not null && !ViewportGeometry.HasUsableTangentFrame(primitive, token))
                throw new InvalidDataException("ViewportPoseVertexMismatch");
            for (int vertex = 0; vertex < primitive.Positions.Count; vertex++)
            {
                if ((vertex & 1023) == 0) token.ThrowIfCancellationRequested();
                if (!ViewportGeometry.Finite(primitive.Positions[vertex])) throw new InvalidDataException("ViewportPoseNonfinite");
            }
        }
        using var timing = AssetLoadDiagnostics.Measure("AnimationPoseUpload");
        foreach (var part in selected)
        {
            var primitive = frame.Primitives[part.PrimitiveKey];
            if (!poseGeometry.TryGetValue(part, out var geometry))
            {
                var old = part.Model.Geometry as ViewportMeshGeometry ?? throw new InvalidDataException("ViewportPoseGeometryUnavailable");
                if (old.Indices is null) throw new InvalidDataException("ViewportPoseIndicesUnavailable");
                var retained = source.MemoryBudget.Reserve(checked(primitive.Positions.Count * 48L + 4096), "animation vertex arrays");
                IDisposable? gpu = null;
                try
                {
                    gpu = service!.ReserveGpu(ViewportGeometry.GeometryBytes(primitive.Positions.Count, old.Indices.Count,
                        old.Colors?.Count == primitive.Positions.Count), "animation mesh buffers");
                    geometry = new ViewportMeshGeometry { Animated = true, UploadBudget = source.MemoryBudget,
                        Indices = old.Indices, TextureCoordinates = old.TextureCoordinates, TextureCoordinateSets = old.TextureCoordinateSets,
                        Colors = old.Colors, PreDefinedVertexCount = old.PreDefinedVertexCount, PreDefinedIndexCount = old.PreDefinedIndexCount };
                    poseGeometry.Add(part, geometry); leases.Add(retained); leases.Add(gpu); gpu = null;
                }
                catch { retained.Dispose(); gpu?.Dispose(); throw; }
            }
            var positions = new Vector3Collection(primitive.Positions.Count);
            var normals = new Vector3Collection(primitive.Positions.Count);
            var tangents = new Vector3Collection(primitive.Positions.Count);
            var bitangents = new Vector3Collection(primitive.Positions.Count);
            for (int vertex = 0; vertex < primitive.Positions.Count; vertex++)
            {
                var p = primitive.Positions[vertex]; positions.Add(new(p.X, p.Y, p.Z));
                var n = Vector3.Normalize(primitive.Normals![vertex]); normals.Add(new(n.X, n.Y, n.Z));
                var t4 = primitive.Tangents?[vertex] ?? System.Numerics.Vector4.Zero;
                var t = primitive.Tangents is null ? Vector3.Zero : Vector3.Normalize(new Vector3(t4.X, t4.Y, t4.Z));
                var b = primitive.Tangents is null ? Vector3.Zero : Vector3.Normalize(Vector3.Cross(n, t) * t4.W);
                tangents.Add(new(t.X, t.Y, t.Z)); bitangents.Add(new(b.X, b.Y, b.Z));
            }
            geometry.ReleasePendingUpload();
            geometry.SetPendingUpload(source.MemoryBudget.Reserve(checked(primitive.Positions.Count * (long)DefaultVertex.SizeInBytes), "animation vertex upload"));
            geometry.Positions = positions; geometry.Normals = normals; geometry.Tangents = tangents; geometry.BiTangents = bitangents;
            geometry.UpdateBounds(); part.Model.Geometry = geometry;
        }
        poseSequence = frame.Sequence;
        RenderView.InvalidateRender(); await WaitForFrameAsync(token);
        return ReferenceEquals(source, snapshot) && generation == ownGeneration;
    }

    private void ClearPoseGeometry()
    {
        foreach (var geometry in poseGeometry.Values) geometry.ReleasePendingUpload();
        poseGeometry.Clear(); poseSequence = 0;
    }
}
