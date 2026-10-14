using System.Text.Json.Serialization;
using Deviant.Assets;

namespace Deviant.Formats.Models;

public delegate IDisposable NativeSkinningMemoryReservation(long bytes, string category);

public sealed record NativeSkinningAttributeLimits(int MaximumBuffers = 1024,
    int MaximumVertices = 8_000_000, long MaximumDecodedBytes = 256L * 1024 * 1024);

public readonly record struct NativeMatrixRowInfluence(uint MatrixRowOffset, float RawWeight);
public readonly record struct NativeNormalizedMatrixRowInfluence(uint MatrixRowOffset, float Weight);

/// <summary>Stored influence lanes for a vertex buffer. Matrix offsets address float4 rows.</summary>
public sealed class NativeSkinningBufferAttributes
{
    private readonly NativeMatrixRowInfluence[] values;

    internal NativeSkinningBufferAttributes(int bufferIndex, uint mode, int vertices,
        NativeMatrixRowInfluence[] values, IReadOnlyDictionary<byte, DecodedExtent> extents)
    {
        BufferIndex = bufferIndex; StoredMode = mode; VertexCount = vertices;
        StoredLaneCount = mode == 8 ? 8 : 4; this.values = values;
        AttributeExtents = extents;
    }

    public int BufferIndex { get; }
    public uint StoredMode { get; }
    public int VertexCount { get; }
    public int StoredLaneCount { get; }
    public int ActiveInfluenceCount => (int)StoredMode;
    public IReadOnlyDictionary<byte, DecodedExtent> AttributeExtents { get; }
    [JsonIgnore] public ReadOnlyMemory<NativeMatrixRowInfluence> StoredInfluences => values;

    public NativeMatrixRowInfluence GetStoredInfluence(int vertex, int lane)
    {
        if ((uint)vertex >= (uint)VertexCount) throw new ArgumentOutOfRangeException(nameof(vertex));
        if ((uint)lane >= (uint)StoredLaneCount) throw new ArgumentOutOfRangeException(nameof(lane));
        return values[checked(vertex * StoredLaneCount + lane)];
    }

    /// <summary>Projects the named native kernel's effective influence weights.</summary>
    public NativeNormalizedMatrixRowInfluence GetNormalizedInfluence(int vertex, int influence)
    {
        if ((uint)influence >= (uint)ActiveInfluenceCount)
            throw new ArgumentOutOfRangeException(nameof(influence));
        var value = GetStoredInfluence(vertex, influence);
        if (StoredMode == 1) return new(value.MatrixRowOffset, 1);
        var sum = 0f;
        for (var i = 0; i < ActiveInfluenceCount; i++) sum += GetStoredInfluence(vertex, i).RawWeight;
        return new(value.MatrixRowOffset, value.RawWeight * (1f / sum));
    }
}

public sealed record NativeSkinningAttributeDocument(ResourceIdentity Identity, int GroupIndex,
    DecodedExtent GpuExtent, IReadOnlyList<NativeSkinningBufferAttributes> Buffers, long AccountedBytes);

/// <summary>Owns decoded influence storage and its session memory reservation.</summary>
public sealed class NativeSkinningAttributeLease : IDisposable
{
    private NativeSkinningAttributeDocument? document;
    private IDisposable? reservation;
    internal NativeSkinningAttributeLease(NativeSkinningAttributeDocument document, IDisposable reservation)
    { this.document = document; this.reservation = reservation; }

    public NativeSkinningAttributeDocument Document => document ??
        throw new ObjectDisposedException(nameof(NativeSkinningAttributeLease));

    public void Dispose()
    {
        document = null;
        Interlocked.Exchange(ref reservation, null)?.Dispose();
    }
}
