using Deviant.Assets;
using Deviant.Formats.Models;

namespace Deviant.Export.Models;

public sealed class NativeMeshFbxSkinLease : IDisposable
{
    private IReadOnlyList<FbxSkinExportInput>? skins;
    private IDisposable? reservation;
    internal NativeMeshFbxSkinLease(IReadOnlyList<FbxSkinExportInput> skins, IDisposable reservation)
    { this.skins = skins; this.reservation = reservation; }
    public IReadOnlyList<FbxSkinExportInput> Skins => skins ?? throw new ObjectDisposedException(nameof(NativeMeshFbxSkinLease));
    public void Dispose() { skins = null; Interlocked.Exchange(ref reservation, null)?.Dispose(); }
}

/// <summary>Projects stored buffer influences into primitive-local control-point rows using a caller-qualified joint map.</summary>
public static class NativeMeshFbxSkinProjection
{
    public static NativeMeshFbxSkinLease Create(NativeMeshDataDocument metadata,
        NativeSkinningAttributeDocument attributes, GeometryAsset geometry, FbxBindMatrix meshBindWorld,
        IReadOnlyDictionary<uint, int> jointOrdinalsByMatrixRow, FbxExportMemoryReservation reserveMemory,
        int maximumVertices = NativeMeshGeometryDecoder.MaximumOutputVertices, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(metadata); ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(geometry); ArgumentNullException.ThrowIfNull(meshBindWorld);
        ArgumentNullException.ThrowIfNull(jointOrdinalsByMatrixRow); ArgumentNullException.ThrowIfNull(reserveMemory);
        token.ThrowIfCancellationRequested();
        if (maximumVertices < 1) throw new ArgumentOutOfRangeException(nameof(maximumVertices));
        if (metadata.Identity != attributes.Identity || attributes.GroupIndex < 0 || attributes.GroupIndex >= metadata.Groups.Count)
            throw new InvalidDataException("NativeSkinSourceMismatch");
        var parts = metadata.Groups[attributes.GroupIndex].Parts;
        if (parts.Count != geometry.Primitives.Count) throw new InvalidDataException("NativeSkinPrimitiveMismatch");
        long vertices = 0;
        foreach (var part in parts) vertices = checked(vertices + part.VertexCount);
        if (vertices == 0 || vertices > maximumVertices) throw new InvalidDataException("NativeSkinVertexLimit");
        var reservation = reserveMemory(checked(vertices * 640L + parts.Count * 512L), "FBX native skin control points");
        try
        {
            var buffers = attributes.Buffers.ToDictionary(buffer => buffer.BufferIndex);
            var skins = new List<FbxSkinExportInput>(parts.Count);
            for (var p = 0; p < parts.Count; p++)
            {
                token.ThrowIfCancellationRequested();
                var part = parts[p]; var primitive = geometry.Primitives[p];
                var expectedKey = $"{metadata.Identity.Key}/group/{attributes.GroupIndex}/part/{p}";
                if (primitive.Key != expectedKey || primitive.Positions.Count != part.VertexCount)
                    throw new InvalidDataException("NativeSkinPrimitiveMismatch");
                if (part.TopologySelector == 0) continue;
                if (
                    !buffers.TryGetValue(checked((int)part.VertexBufferIndex), out var buffer) ||
                    part.TopologySelector != buffer.StoredMode || (ulong)part.BaseVertex + part.VertexCount > (ulong)buffer.VertexCount)
                    throw new InvalidDataException("NativeSkinPrimitiveMismatch");
                var rows = new IReadOnlyList<FbxJointInfluence>[primitive.Positions.Count];
                for (var v = 0; v < rows.Length; v++)
                {
                    if ((v & 1023) == 0) token.ThrowIfCancellationRequested();
                    var sourceVertex = checked((int)part.BaseVertex + v);
                    var row = new List<FbxJointInfluence>(buffer.ActiveInfluenceCount);
                    for (var lane = 0; lane < buffer.ActiveInfluenceCount; lane++)
                    {
                        var stored = buffer.GetStoredInfluence(sourceVertex, lane);
                        var weight = buffer.StoredMode == 1 ? 1d : stored.RawWeight;
                        if (!double.IsFinite(weight) || weight < 0) throw new InvalidDataException("NativeSkinWeightInvalid");
                        if (weight == 0) continue;
                        if (!jointOrdinalsByMatrixRow.TryGetValue(stored.MatrixRowOffset, out var joint) || joint < 0)
                            throw new InvalidDataException("NativeSkinJointMappingMissing");
                        row.Add(new(joint, weight));
                    }
                    if (row.Count == 0) throw new InvalidDataException("NativeSkinWeightInvalid");
                    rows[v] = row;
                }
                skins.Add(new(primitive.Key, meshBindWorld, rows));
            }
            token.ThrowIfCancellationRequested();
            return new(skins, reservation);
        }
        catch { reservation.Dispose(); throw; }
    }
}
