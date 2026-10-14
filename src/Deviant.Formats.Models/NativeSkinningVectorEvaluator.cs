using System.Numerics;
using Deviant.Assets;

namespace Deviant.Formats.Models;

/// <summary>Owns packed posed normal and tangent words.</summary>
public sealed class NativeSkinningVectorLease : IDisposable
{
    private uint[]? normals;
    private uint[]? tangents;
    private IDisposable? reservation;
    internal NativeSkinningVectorLease(uint[] normals, uint[]? tangents, IDisposable reservation)
    { this.normals = normals; this.tangents = tangents; this.reservation = reservation; }
    public int Count => (normals ?? throw new ObjectDisposedException(nameof(NativeSkinningVectorLease))).Length;
    public bool HasTangents { get { _ = Count; return tangents is not null; } }
    public uint GetNormal(int index) =>
        (normals ?? throw new ObjectDisposedException(nameof(NativeSkinningVectorLease)))[index];
    public uint GetTangent(int index)
    {
        _ = Count;
        return (tangents ?? throw new InvalidOperationException("This profile has no tangent output."))[index];
    }
    public Vector3 GetNormalVector(int index)
    {
        var value = NativeMeshGeometryDecoder.ReadPackedVector(GetNormal(index));
        return new(value.X, value.Y, value.Z);
    }
    public Vector4 GetTangentVector(int index)
    {
        var packed = GetTangent(index);
        var value = NativeMeshGeometryDecoder.ReadPackedVector(packed);
        return new(value.X, value.Y, value.Z, (packed >> 30) != 0 ? -1f : 1f);
    }
    public void Dispose()
    {
        normals = tangents = null;
        Interlocked.Exchange(ref reservation, null)?.Dispose();
    }
}

/// <summary>Evaluates packed vector output for the native 1-normal, 4-TBN and 8-TBN kernels.</summary>
public sealed class NativeSkinningVectorEvaluator
{
    public NativeSkinningVectorLease Evaluate(NativeSkinningAttributeDocument attributes, int bufferIndex,
        ReadOnlySpan<uint> packedNormals, ReadOnlySpan<uint> packedTangents, NativeSkinningPoseRowLease pose,
        SourceRevision expectedRevision, ulong expectedSnapshotSequence, NativeSkinningMemoryReservation reserveMemory,
        int maximumVertices = 8_000_000, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(pose);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        ArgumentNullException.ThrowIfNull(reserveMemory);
        token.ThrowIfCancellationRequested();
        if (maximumVertices <= 0) throw new ArgumentOutOfRangeException(nameof(maximumVertices));
        var buffer = NativeSkinningKernelRows.MatchBuffer(attributes, bufferIndex, pose, expectedRevision, expectedSnapshotSequence);
        var hasTangents = buffer.StoredMode is 4 or 8;
        if (buffer.StoredMode is not (1 or 4 or 8) || packedNormals.Length != buffer.VertexCount ||
            packedNormals.Length == 0 || packedNormals.Length > maximumVertices ||
            packedTangents.Length != (hasTangents ? buffer.VertexCount : 0))
            throw new InvalidDataException("NativePoseVectorCountMismatch");
        var rowCount = pose.RowCount;
        var reservation = reserveMemory(checked(packedNormals.Length * (hasTangents ? 8L : 4L) + 192), "native posed vectors")
            ?? throw new InvalidOperationException("The memory reservation is unavailable.");
        try
        {
            token.ThrowIfCancellationRequested();
            var normals = new uint[packedNormals.Length];
            var tangents = hasTangents ? new uint[packedNormals.Length] : null;
            for (var vertex = 0; vertex < normals.Length; vertex++)
            {
                if ((vertex & 1023) == 0) token.ThrowIfCancellationRequested();
                var rows = NativeSkinningKernelRows.Read(buffer, vertex, pose, rowCount);
                normals[vertex] = Pack(rows.TransformVector(UnpackNormalized(packedNormals[vertex])), false);
                if (tangents is not null)
                    tangents[vertex] = Pack(rows.TransformVector(UnpackNormalized(packedTangents[vertex])), packedTangents[vertex] >> 30 != 0);
            }
            token.ThrowIfCancellationRequested();
            return new(normals, tangents, reservation);
        }
        catch { reservation.Dispose(); throw; }
    }

    /// <summary>Evaluates packed vectors from a caller-owned window using source-buffer vertex indices.</summary>
    public NativeSkinningVectorLease EvaluateWindow(NativeSkinningAttributeDocument attributes, int bufferIndex,
        int baseVertex, ReadOnlySpan<uint> packedNormals, ReadOnlySpan<uint> packedTangents,
        NativeSkinningPoseRowLease pose, SourceRevision expectedRevision, ulong expectedSnapshotSequence,
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
        var hasTangents = buffer.StoredMode is 4 or 8;
        if (buffer.StoredMode is not (1 or 4 or 8) || baseVertex < 0 || baseVertex >= buffer.VertexCount ||
            packedNormals.Length == 0 || packedNormals.Length > maximumVertices ||
            packedNormals.Length > buffer.VertexCount - baseVertex ||
            packedTangents.Length != (hasTangents ? packedNormals.Length : 0))
            throw new InvalidDataException("NativePoseVectorWindowInvalid");

        var rowCount = pose.RowCount;
        var reservation = reserveMemory(checked(packedNormals.Length * (hasTangents ? 8L : 4L) + 192), "native posed vectors")
            ?? throw new InvalidOperationException("The memory reservation is unavailable.");
        try
        {
            token.ThrowIfCancellationRequested();
            var normals = new uint[packedNormals.Length];
            var tangents = hasTangents ? new uint[packedNormals.Length] : null;
            for (var windowVertex = 0; windowVertex < normals.Length; windowVertex++)
            {
                if ((windowVertex & 1023) == 0) token.ThrowIfCancellationRequested();
                var sourceVertex = checked(baseVertex + windowVertex);
                var rows = NativeSkinningKernelRows.Read(buffer, sourceVertex, pose, rowCount);
                normals[windowVertex] = Pack(rows.TransformVector(UnpackNormalized(packedNormals[windowVertex])), false);
                if (tangents is not null)
                    tangents[windowVertex] = Pack(rows.TransformVector(UnpackNormalized(packedTangents[windowVertex])),
                        packedTangents[windowVertex] >> 30 != 0);
            }
            token.ThrowIfCancellationRequested();
            return new(normals, tangents, reservation);
        }
        catch { reservation.Dispose(); throw; }
    }

    /// <summary>Evaluates both packed normal and tangent streams from a caller-owned source window.</summary>
    public NativeSkinningVectorLease EvaluateTbnWindow(NativeSkinningAttributeDocument attributes, int bufferIndex,
        int baseVertex, ReadOnlySpan<uint> packedNormals, ReadOnlySpan<uint> packedTangents,
        NativeSkinningPoseRowLease pose, SourceRevision expectedRevision, ulong expectedSnapshotSequence,
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
            packedNormals.Length == 0 || packedNormals.Length > maximumVertices ||
            packedNormals.Length > buffer.VertexCount - baseVertex || packedTangents.Length != packedNormals.Length)
            throw new InvalidDataException("NativePoseTbnWindowInvalid");

        var rowCount = pose.RowCount;
        var reservation = reserveMemory(checked(packedNormals.Length * 8L + 192), "native posed vectors")
            ?? throw new InvalidOperationException("The memory reservation is unavailable.");
        try
        {
            token.ThrowIfCancellationRequested();
            var normals = new uint[packedNormals.Length];
            var tangents = new uint[packedTangents.Length];
            for (var windowVertex = 0; windowVertex < normals.Length; windowVertex++)
            {
                if ((windowVertex & 1023) == 0) token.ThrowIfCancellationRequested();
                var sourceVertex = checked(baseVertex + windowVertex);
                var rows = NativeSkinningKernelRows.Read(buffer, sourceVertex, pose, rowCount);
                normals[windowVertex] = Pack(rows.TransformVector(UnpackNormalized(packedNormals[windowVertex])), false);
                tangents[windowVertex] = Pack(rows.TransformVector(UnpackNormalized(packedTangents[windowVertex])),
                    packedTangents[windowVertex] >> 30 != 0);
            }
            token.ThrowIfCancellationRequested();
            return new(normals, tangents, reservation);
        }
        catch { reservation.Dispose(); throw; }
    }

    /// <summary>Evaluates normal-only output from a source window without creating tangent data.</summary>
    public NativeSkinningVectorLease EvaluateNormalWindow(NativeSkinningAttributeDocument attributes,
        int bufferIndex, int baseVertex, ReadOnlySpan<uint> packedNormals,
        NativeSkinningPoseRowLease pose, SourceRevision expectedRevision, ulong expectedSnapshotSequence,
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
            packedNormals.Length == 0 || packedNormals.Length > maximumVertices ||
            packedNormals.Length > buffer.VertexCount - baseVertex)
            throw new InvalidDataException("NativePoseNormalWindowInvalid");

        var rowCount = pose.RowCount;
        var reservation = reserveMemory(checked(packedNormals.Length * 4L + 192), "native posed normals")
            ?? throw new InvalidOperationException("The memory reservation is unavailable.");
        try
        {
            token.ThrowIfCancellationRequested();
            var normals = new uint[packedNormals.Length];
            for (var windowVertex = 0; windowVertex < normals.Length; windowVertex++)
            {
                if ((windowVertex & 1023) == 0) token.ThrowIfCancellationRequested();
                var sourceVertex = checked(baseVertex + windowVertex);
                var rows = NativeSkinningKernelRows.Read(buffer, sourceVertex, pose, rowCount);
                normals[windowVertex] = Pack(rows.TransformVector(UnpackNormalized(packedNormals[windowVertex])), false);
            }
            token.ThrowIfCancellationRequested();
            return new(normals, null, reservation);
        }
        catch { reservation.Dispose(); throw; }
    }

    private static Vector3 UnpackNormalized(uint packed)
    {
        const float factor = 0.001956947147846222f;
        var value = new Vector3(Component(packed, 0) * factor, Component(packed, 10) * factor, Component(packed, 20) * factor);
        var x = value.X * value.X; var y = value.Y * value.Y; var z = value.Z * value.Z;
        var length = MathF.Sqrt((float)((double)x + y + z));
        if (!float.IsFinite(length) || length <= 0) throw new InvalidDataException("NativePoseVectorZero");
        return new(value.X / length, value.Y / length, value.Z / length);
    }
    private static int Component(uint packed, int shift)
    {
        var value = (int)(packed >> shift & 1023);
        // The native unpack branch compares strictly greater than 512.
        return value > 512 ? value - 1024 : value;
    }
    private static uint Pack(Vector3 value, bool flag) =>
        Quantize(value.X) | Quantize(value.Y) << 10 | Quantize(value.Z) << 20 | (flag ? 1u << 30 : 0);
    private static uint Quantize(float value)
    {
        var scaled = value * 511f;
        if (!float.IsFinite(scaled) || scaled < -2147483648f || scaled >= 2147483648f)
            throw new InvalidDataException("NativePoseVectorQuantizationUndefined");
        return (uint)Math.Max(-511, (int)scaled) & 1023u;
    }
}
