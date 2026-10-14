using System.Buffers.Binary;
using System.Collections;
using System.Numerics;
using Deviant.Assets;
using Deviant.Formats.Models;

namespace Deviant.Browser;

internal static class NativeModelPoseProjection
{
    internal static GeometryAsset Apply(GeometryAsset source, NativeMeshDataDocument metadata,
        ReadOnlyMemory<byte> gpu, NativeSkinningAttributeDocument attributes, NativeSkinningPoseRowLease pose,
        SourceRevision revision, AssetMemoryBudget budget, List<IDisposable> retained, CancellationToken token)
    {
        var parts = metadata.Groups[attributes.GroupIndex].Parts;
        if (source.Primitives.Count != parts.Count) throw new InvalidDataException("NativePosePrimitiveMismatch");
        var primitives = new MeshPrimitive[parts.Count];
        for (var p = 0; p < parts.Count; p++)
        {
            token.ThrowIfCancellationRequested();
            var part = parts[p]; var primitive = source.Primitives[p];
            if (part.TopologySelector == 0) { primitives[p] = primitive; continue; }
            var bufferIndex = checked((int)part.VertexBufferIndex);
            var buffer = metadata.VertexBuffers[bufferIndex];
            using var scratch = budget.Reserve(checked(primitive.Positions.Count * 20L + 256), "pose vertex window");
            var positions = primitive.Positions.ToArray();
            var posed = new NativeSkinningPositionEvaluator().EvaluateWindow(attributes, bufferIndex,
                checked((int)part.BaseVertex), positions, pose, revision, pose.Source.SnapshotSequence, budget.Reserve, token: token);
            retained.Add(posed);
            IReadOnlyList<Vector3>? normals = null; IReadOnlyList<Vector4>? tangents = null;
            var normalDescriptor = buffer.Descriptors.SingleOrDefault(descriptor => descriptor.Semantic == 1);
            var tangentDescriptor = buffer.Descriptors.SingleOrDefault(descriptor => descriptor.Semantic == 10);
            var binormalDescriptor = buffer.Descriptors.SingleOrDefault(descriptor => descriptor.Semantic == 11);
            if (normalDescriptor is null || normalDescriptor.Format != 13 ||
                tangentDescriptor is not null && tangentDescriptor.Format != 13 ||
                tangentDescriptor is null && binormalDescriptor is not null)
                throw new InvalidDataException("NativePoseVectorProfileUnsupported");
            var packedNormals = ReadPacked(primitive, "Normals", normalDescriptor);
            var evaluator = new NativeSkinningVectorEvaluator();
            var vectors = tangentDescriptor is null
                ? evaluator.EvaluateNormalWindow(attributes, bufferIndex, checked((int)part.BaseVertex), packedNormals,
                    pose, revision, pose.Source.SnapshotSequence, budget.Reserve, token: token)
                : evaluator.EvaluateTbnWindow(attributes, bufferIndex, checked((int)part.BaseVertex), packedNormals,
                    ReadPacked(primitive, "Tangents", tangentDescriptor), pose, revision,
                    pose.Source.SnapshotSequence, budget.Reserve, token: token);
            retained.Add(vectors);
            normals = new VectorList<Vector3>(vectors.Count, vectors.GetNormalVector);
            if (vectors.HasTangents) tangents = new VectorList<Vector4>(vectors.Count, vectors.GetTangentVector);
            var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
            for (var i = 0; i < posed.Count; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                var position = posed.GetPosition(i); minimum = Vector3.Min(minimum, position); maximum = Vector3.Max(maximum, position);
            }
            primitives[p] = primitive with { Positions = new VectorList<Vector3>(posed.Count, posed.GetPosition),
                Normals = normals, Tangents = tangents, Bounds = new(minimum, maximum) };

            uint[] ReadPacked(MeshPrimitive input, string field, NativeVertexDescriptor descriptor)
            {
                if (input.FieldExtents is null || !input.FieldExtents.TryGetValue(field, out var extent) ||
                    extent is null || extent.Container != attributes.GpuExtent.Container)
                    throw new InvalidDataException("NativePoseVectorSourceMismatch");
                var start = checked((int)(extent.Offset - attributes.GpuExtent.Offset));
                var stride = buffer.Streams[descriptor.Stream].Stride;
                if (start < 0 || stride < 4 || (ulong)start + (ulong)(positions.Length - 1) * stride + 4 > (ulong)gpu.Length)
                    throw new InvalidDataException("NativePoseVectorSourceMismatch");
                var values = new uint[positions.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                    values[i] = BinaryPrimitives.ReadUInt32LittleEndian(gpu.Span.Slice(checked(start + (int)(i * stride)), 4));
                }
                return values;
            }
        }
        return source with { Primitives = primitives };
    }

    private sealed class VectorList<T>(int count, Func<int,T> get) : IReadOnlyList<T>
    {
        public int Count => count;
        public T this[int index] => get(index);
        public IEnumerator<T> GetEnumerator() { for (var i = 0; i < count; i++) yield return get(i); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
