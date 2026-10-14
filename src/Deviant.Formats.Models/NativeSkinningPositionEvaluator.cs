using System.Numerics;
using Deviant.Assets;

namespace Deviant.Formats.Models;

/// <summary>Owns evaluated vertex positions and their memory reservation.</summary>
public sealed class NativeSkinningPositionLease : IDisposable
{
    private Vector3[]? positions;
    private IDisposable? reservation;
    internal NativeSkinningPositionLease(Vector3[] positions, IDisposable reservation)
    { this.positions = positions; this.reservation = reservation; }
    public int Count => (positions ?? throw new ObjectDisposedException(nameof(NativeSkinningPositionLease))).Length;
    public Vector3 GetPosition(int index) =>
        (positions ?? throw new ObjectDisposedException(nameof(NativeSkinningPositionLease)))[index];
    public void Dispose()
    {
        positions = null;
        Interlocked.Exchange(ref reservation, null)?.Dispose();
    }
}

/// <summary>Evaluates the position branch of native modes 1, 4 and 8 from captured matrix rows.</summary>
public sealed class NativeSkinningPositionEvaluator
{
    public NativeSkinningPositionLease Evaluate(NativeSkinningAttributeDocument attributes, int bufferIndex,
        ReadOnlySpan<Vector3> positions, NativeSkinningPoseRowLease pose, SourceRevision expectedRevision, ulong expectedSnapshotSequence,
        NativeSkinningMemoryReservation reserveMemory, int maximumVertices = 8_000_000,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(pose);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        ArgumentNullException.ThrowIfNull(reserveMemory);
        token.ThrowIfCancellationRequested();
        if (maximumVertices <= 0) throw new ArgumentOutOfRangeException(nameof(maximumVertices));
        var buffer = NativeSkinningKernelRows.MatchBuffer(attributes, bufferIndex, pose, expectedRevision, expectedSnapshotSequence);
        if (buffer.StoredMode is not (1 or 4 or 8) || positions.Length != buffer.VertexCount ||
            positions.Length == 0 || positions.Length > maximumVertices)
            throw new InvalidDataException("NativePoseVertexLimit");
        var rowCount = pose.RowCount;
        var reservation = reserveMemory(checked(positions.Length * 12L + 128), "native posed positions")
            ?? throw new InvalidOperationException("The memory reservation is unavailable.");
        try
        {
            token.ThrowIfCancellationRequested();
            var output = new Vector3[positions.Length];
            for (var vertex = 0; vertex < output.Length; vertex++)
            {
                if ((vertex & 1023) == 0) token.ThrowIfCancellationRequested();
                var position = positions[vertex];
                if (!Finite(position)) throw new InvalidDataException("NativePosePositionNonFinite");
                var rows = NativeSkinningKernelRows.Read(buffer, vertex, pose, rowCount);
                var value = rows.TransformPosition(position);
                if (!Finite(value)) throw new InvalidDataException("NativePoseOutputNonFinite");
                output[vertex] = value;
            }
            token.ThrowIfCancellationRequested();
            return new(output, reservation);
        }
        catch { reservation.Dispose(); throw; }
    }

    /// <summary>Evaluates a caller-owned position window using source-buffer vertex indices.</summary>
    public NativeSkinningPositionLease EvaluateWindow(NativeSkinningAttributeDocument attributes, int bufferIndex,
        int baseVertex, ReadOnlySpan<Vector3> positions, NativeSkinningPoseRowLease pose,
        SourceRevision expectedRevision, ulong expectedSnapshotSequence,
        NativeSkinningMemoryReservation reserveMemory, int maximumVertices = 8_000_000,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(pose);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        ArgumentNullException.ThrowIfNull(reserveMemory);
        token.ThrowIfCancellationRequested();
        if (maximumVertices <= 0) throw new ArgumentOutOfRangeException(nameof(maximumVertices));
        var buffer = NativeSkinningKernelRows.MatchBuffer(attributes, bufferIndex, pose, expectedRevision, expectedSnapshotSequence);
        if (buffer.StoredMode is not (1 or 4 or 8) || baseVertex < 0 || baseVertex >= buffer.VertexCount ||
            positions.Length == 0 || positions.Length > maximumVertices ||
            positions.Length > buffer.VertexCount - baseVertex)
            throw new InvalidDataException("NativePoseVertexWindowInvalid");

        var rowCount = pose.RowCount;
        var reservation = reserveMemory(checked(positions.Length * 12L + 128), "native posed positions")
            ?? throw new InvalidOperationException("The memory reservation is unavailable.");
        try
        {
            token.ThrowIfCancellationRequested();
            var output = new Vector3[positions.Length];
            for (var windowVertex = 0; windowVertex < output.Length; windowVertex++)
            {
                if ((windowVertex & 1023) == 0) token.ThrowIfCancellationRequested();
                var sourceVertex = checked(baseVertex + windowVertex);
                var position = positions[windowVertex];
                if (!Finite(position)) throw new InvalidDataException("NativePosePositionNonFinite");
                var rows = NativeSkinningKernelRows.Read(buffer, sourceVertex, pose, rowCount);
                var value = rows.TransformPosition(position);
                if (!Finite(value)) throw new InvalidDataException("NativePoseOutputNonFinite");
                output[windowVertex] = value;
            }
            token.ThrowIfCancellationRequested();
            return new(output, reservation);
        }
        catch { reservation.Dispose(); throw; }
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
