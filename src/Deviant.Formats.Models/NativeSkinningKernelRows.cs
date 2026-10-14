using System.Numerics;
using Deviant.Assets;

namespace Deviant.Formats.Models;

internal readonly record struct NativeSkinningWeightedRows(Vector4 X, Vector4 Y, Vector4 Z)
{
    public Vector3 TransformPosition(Vector3 value) => new(Position(X, value), Position(Y, value), Position(Z, value));
    public Vector3 TransformVector(Vector3 value) => new(Direction(X, value), Direction(Y, value), Direction(Z, value));
    private static float Position(Vector4 row, Vector3 value) =>
        ((row.X * value.X + row.Y * value.Y) + row.Z * value.Z) + row.W;
    private static float Direction(Vector4 row, Vector3 value) =>
        (float)((double)(row.X * value.X) + (row.Y * value.Y) + (row.Z * value.Z));
}

internal static class NativeSkinningKernelRows
{
    public static NativeSkinningBufferAttributes MatchBuffer(NativeSkinningAttributeDocument attributes,
        int bufferIndex, NativeSkinningPoseRowLease pose, SourceRevision revision, ulong snapshotSequence)
    {
        if (attributes.Identity != pose.Source.MeshIdentity || attributes.GroupIndex != pose.Source.GroupIndex ||
            pose.Source.Revision != revision || pose.Source.SnapshotSequence != snapshotSequence ||
            attributes.Identity.IndexContentId != revision.IndexContentId)
            throw new InvalidDataException("NativePoseSourceMismatch");
        var matches = attributes.Buffers.Where(buffer => buffer.BufferIndex == bufferIndex).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("NativePoseBufferMismatch");
        return matches[0];
    }

    public static NativeSkinningWeightedRows Read(NativeSkinningBufferAttributes buffer, int vertex,
        NativeSkinningPoseRowLease pose, int rowCount)
    {
        Vector4 x = default, y = default, z = default;
        var sum = 0f;
        for (var lane = 0; lane < buffer.ActiveInfluenceCount; lane++)
        {
            var influence = buffer.GetStoredInfluence(vertex, lane);
            if ((ulong)influence.MatrixRowOffset + 2 >= (ulong)rowCount)
                throw new InvalidDataException("NativePoseRowDomainMismatch");
            var weight = buffer.StoredMode == 1 ? 1f : influence.RawWeight;
            if (!float.IsFinite(weight) || weight < 0) throw new InvalidDataException("NativePoseWeightInvalid");
            x += pose.GetRow(influence.MatrixRowOffset) * weight;
            y += pose.GetRow(influence.MatrixRowOffset + 1) * weight;
            z += pose.GetRow(influence.MatrixRowOffset + 2) * weight;
            sum += weight;
        }
        if (!float.IsFinite(sum) || sum <= 0) throw new InvalidDataException("NativePoseWeightInvalid");
        if (buffer.StoredMode != 1)
        {
            var reciprocal = 1f / sum;
            x *= reciprocal; y *= reciprocal; z *= reciprocal;
        }
        return new(x, y, z);
    }
}
