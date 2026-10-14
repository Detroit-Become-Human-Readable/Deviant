using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Deviant.Assets;

namespace Deviant.Formats.Models;

public sealed record NativeSkinningPoseSource(ResourceIdentity MeshIdentity,
    ResourceIdentity RigIdentity, SourceRevision Revision, int GroupIndex, ulong SnapshotSequence,
    string CaptureEvidenceSha256, string MatrixRowsSha256, DecodedExtent MatrixRowsExtent);

public sealed record NativeSkinningPoseLimits(int MaximumMatrices = 65_536,
    long MaximumDecodedBytes = 16L * 1024 * 1024);

/// <summary>Owns captured float4 matrix rows and their memory reservation.</summary>
public sealed class NativeSkinningPoseRowLease : IDisposable
{
    private Vector4[]? rows;
    private IDisposable? reservation;

    internal NativeSkinningPoseRowLease(NativeSkinningPoseSource source, Vector4[] rows,
        IDisposable reservation)
    { Source = source; this.rows = rows; this.reservation = reservation; }

    public NativeSkinningPoseSource Source { get; }
    public int RowCount => (rows ?? throw new ObjectDisposedException(nameof(NativeSkinningPoseRowLease))).Length;
    public int MatrixCount => RowCount / 3;
    public Vector4 GetRow(uint index)
    {
        var values = rows ?? throw new ObjectDisposedException(nameof(NativeSkinningPoseRowLease));
        if (index >= values.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return values[(int)index];
    }
    public void Dispose()
    {
        rows = null;
        Interlocked.Exchange(ref reservation, null)?.Dispose();
    }
}

/// <summary>Reads a bounded capture of three float4 rows per matrix.</summary>
public sealed class NativeSkinningPoseRowsReader
{
    public NativeSkinningPoseRowLease Read(NativeSkinningPoseSource source, ReadOnlySpan<byte> bytes,
        ResourceIdentity expectedMesh, ResourceIdentity expectedRig, SourceRevision expectedRevision,
        ulong expectedSnapshotSequence, int expectedMatrixCount,
        NativeSkinningMemoryReservation reserveMemory, NativeSkinningPoseLimits? limits = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.MeshIdentity);
        ArgumentNullException.ThrowIfNull(source.RigIdentity);
        ArgumentNullException.ThrowIfNull(source.Revision);
        ArgumentNullException.ThrowIfNull(source.MatrixRowsExtent);
        ArgumentNullException.ThrowIfNull(expectedMesh);
        ArgumentNullException.ThrowIfNull(expectedRig);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        ArgumentNullException.ThrowIfNull(reserveMemory);
        token.ThrowIfCancellationRequested();
        limits ??= new();
        if (limits.MaximumMatrices <= 0 || limits.MaximumDecodedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
        if (expectedMatrixCount <= 0 || expectedMatrixCount > limits.MaximumMatrices)
            throw new ArgumentOutOfRangeException(nameof(expectedMatrixCount));
        if (source.MeshIdentity != expectedMesh || source.RigIdentity != expectedRig ||
            source.Revision != expectedRevision || source.SnapshotSequence != expectedSnapshotSequence || source.GroupIndex < 0 ||
            source.MeshIdentity.IndexContentId != source.Revision.IndexContentId ||
            source.RigIdentity.IndexContentId != source.Revision.IndexContentId)
            throw new InvalidDataException("NativePoseSourceMismatch");
        var extent = source.MatrixRowsExtent;
        if (extent.Container.IndexContentId != source.Revision.IndexContentId ||
            extent.Offset < 0 || extent.Length != bytes.Length || extent.Offset > long.MaxValue - extent.Length)
            throw new InvalidDataException("NativePoseExtentMismatch");
        if (bytes.Length == 0 || bytes.Length % 48 != 0 || bytes.Length / 48 > limits.MaximumMatrices ||
            bytes.Length > limits.MaximumDecodedBytes)
            throw new InvalidDataException("NativePoseRowLimit");
        if (bytes.Length / 48 != expectedMatrixCount)
            throw new InvalidDataException("NativePoseMatrixCountMismatch");
        if (!IsDigest(source.CaptureEvidenceSha256) || !IsDigest(source.MatrixRowsSha256) ||
            !Convert.ToHexString(SHA256.HashData(bytes)).Equals(source.MatrixRowsSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NativePoseDigestMismatch");
        token.ThrowIfCancellationRequested();
        var reservation = reserveMemory(checked(bytes.Length + 512L), "native pose matrix rows")
            ?? throw new InvalidOperationException("The memory reservation is unavailable.");
        try
        {
            token.ThrowIfCancellationRequested();
            var values = new Vector4[bytes.Length / 16];
            for (var index = 0; index < values.Length; index++)
            {
                if ((index & 1023) == 0) token.ThrowIfCancellationRequested();
                var at = bytes.Slice(index * 16, 16);
                var value = new Vector4(ReadFloat(at), ReadFloat(at[4..]), ReadFloat(at[8..]), ReadFloat(at[12..]));
                if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W))
                    throw new InvalidDataException("NativePoseNonFinite");
                values[index] = value;
            }
            token.ThrowIfCancellationRequested();
            return new(source, values, reservation);
        }
        catch { reservation.Dispose(); throw; }
    }

    private static float ReadFloat(ReadOnlySpan<byte> bytes) =>
        BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(bytes));
    private static bool IsDigest(string? text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);
}
