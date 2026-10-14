using System.Buffers.Binary;
using System.Collections.ObjectModel;
using Deviant.Assets;

namespace Deviant.Formats.Models;

/// <summary>Reads native skinning lanes without assigning matrix offsets to rig joints.</summary>
public sealed class NativeSkinningAttributeDecoder
{
    public NativeSkinningAttributeLease Decode(NativeMeshDataDocument document, ReadOnlyMemory<byte> gpuBytes,
        int groupIndex, NativeSkinningMemoryReservation reserveMemory,
        NativeSkinningAttributeLimits? limits = null, CancellationToken token = default,
        DecodedExtent? gpuExtent = null)
    {
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(reserveMemory);
        token.ThrowIfCancellationRequested(); limits ??= new();
        if (limits.MaximumBuffers is < 1 or > NativeMeshDataReader.MaximumBuffers ||
            limits.MaximumVertices is < 1 or > NativeMeshGeometryDecoder.MaximumOutputVertices ||
            limits.MaximumDecodedBytes is < 1 or > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(limits));
        if (groupIndex < 0 || groupIndex >= document.Groups.Count)
            throw new InvalidDataException("NativeSkinningGroupInvalid");
        if (gpuBytes.Length > NativeMeshGeometryDecoder.MaximumGpuBytes)
            throw new InvalidDataException("NativeMeshGpuLimit");
        if (document.Groups.Count > NativeMeshDataReader.MaximumGroups ||
            document.VertexBuffers.Count > limits.MaximumBuffers ||
            document.IndexCounts.Count > NativeMeshDataReader.MaximumBuffers ||
            document.Groups[groupIndex].Parts.Count > NativeMeshDataReader.MaximumParts)
            throw new InvalidDataException("NativeSkinningBufferLimit");
        var defaultGpuIdentity = document.Identity with
            { MemberPath = (document.Identity.MemberPath ?? "native-mesh") + "/gpu" };
        var source = gpuExtent ?? new(defaultGpuIdentity, 0, gpuBytes.Length);
        if ((source.Container != document.Identity && source.Container != defaultGpuIdentity) || source.Offset < 0 ||
            source.Length != gpuBytes.Length || source.Offset > long.MaxValue - source.Length)
            throw new InvalidDataException("NativeMeshGpuSourceMismatch");

        // Reserve metadata scratch before creating buffer plans or layout arrays.
        var scratchBytes = checked(4096L + document.VertexBuffers.Count * 512L);
        if (scratchBytes > limits.MaximumDecodedBytes)
            throw new InvalidDataException("NativeSkinningDecodedLimit");
        using var scratch = reserveMemory(scratchBytes, "native skinning layout") ??
            throw new InvalidOperationException("The memory reservation callback returned null.");
        var allModes = new Dictionary<int, uint>(); var totalParts = 0;
        foreach (var group in document.Groups)
        {
            if (group.Parts.Count > NativeMeshDataReader.MaximumParts - totalParts)
                throw new InvalidDataException("NativeMeshPartLimit");
            totalParts += group.Parts.Count;
            foreach (var part in group.Parts)
            {
                token.ThrowIfCancellationRequested();
                if (part.VertexBufferIndex >= document.VertexBuffers.Count ||
                    part.BaseVertex > document.VertexBuffers[(int)part.VertexBufferIndex].ElementCount ||
                    part.VertexCount > document.VertexBuffers[(int)part.VertexBufferIndex].ElementCount - part.BaseVertex)
                    throw new InvalidDataException("NativeMeshWindowInvalid");
                var index = (int)part.VertexBufferIndex;
                if (allModes.TryGetValue(index, out var previous) && previous != part.TopologySelector)
                    throw new InvalidDataException("NativeSkinningBufferModeConflict");
                allModes[index] = part.TopologySelector;
            }
        }
        var modes = new SortedDictionary<int, uint>();
        foreach (var part in document.Groups[groupIndex].Parts)
        {
            token.ThrowIfCancellationRequested();
            if (part.TopologySelector == 0) continue;
            if (part.TopologySelector is not (1 or 4 or 8))
                throw new InvalidDataException("NativeSkinningModeUnsupported");
            modes[(int)part.VertexBufferIndex] = part.TopologySelector;
        }
        long vertices = 0, accounted = checked(4096L + modes.Count * 512L);
        foreach (var pair in modes)
        {
            token.ThrowIfCancellationRequested();
            var count = document.VertexBuffers[pair.Key].ElementCount;
            vertices = checked(vertices + count);
            if (vertices > limits.MaximumVertices) throw new InvalidDataException("NativeSkinningVertexLimit");
            accounted = checked(accounted + (long)count * (pair.Value == 8 ? 8 : 4) * 8);
        }
        if (accounted + scratchBytes > limits.MaximumDecodedBytes)
            throw new InvalidDataException("NativeSkinningDecodedLimit");
        var layout = NativeMeshGeometryDecoder.BuildLayout(document, groupIndex, gpuBytes.Length, token);
        var reservation = reserveMemory(accounted, "native skinning attributes") ??
            throw new InvalidOperationException("The memory reservation callback returned null.");
        try
        {
            var outputs = new NativeSkinningBufferAttributes[modes.Count]; var outputIndex = 0;
            foreach (var pair in modes)
            {
                token.ThrowIfCancellationRequested();
                var buffer = document.VertexBuffers[pair.Key];
                if (buffer.ElementCount == 0) throw new InvalidDataException("NativeSkinningVerticesEmpty");
                var descriptors = new Dictionary<byte, NativeVertexDescriptor>();
                foreach (var descriptor in buffer.Descriptors)
                {
                    token.ThrowIfCancellationRequested();
                    if (descriptor.Semantic is not (6 or 7 or 8 or 9)) continue;
                    if (!descriptors.TryAdd(descriptor.Semantic, descriptor))
                        throw new InvalidDataException("NativeSkinningAttributeDuplicate");
                }
                var semantics = pair.Value == 8 ? new byte[] { 6, 7, 8, 9 } : new byte[] { 6, 7 };
                var extents = new Dictionary<byte, DecodedExtent>();
                foreach (var semantic in semantics)
                {
                    if (!descriptors.TryGetValue(semantic, out var descriptor))
                        throw new InvalidDataException("NativeSkinningAttributeMissing");
                    if (descriptor.Format != 6)
                        throw new InvalidDataException("NativeSkinningAttributeFormatUnsupported");
                    var stride = buffer.Streams[descriptor.Stream].Stride;
                    if (descriptor.Offset + 8 > stride)
                        throw new InvalidDataException("NativeVertexDescriptorRangeInvalid");
                    var start = checked(layout.VertexStarts[pair.Key][descriptor.Stream] + descriptor.Offset);
                    var length = checked((int)((long)(buffer.ElementCount - 1) * stride + 8));
                    extents.Add(semantic, new(source.Container, checked(source.Offset + start), length));
                }
                var lanes = pair.Value == 8 ? 8 : 4;
                var values = new NativeMatrixRowInfluence[checked((int)buffer.ElementCount * lanes)];
                for (var vertex = 0; vertex < buffer.ElementCount; vertex++)
                {
                    if ((vertex & 1023) == 0) token.ThrowIfCancellationRequested();
                    var sum = 0f;
                    for (var lane = 0; lane < lanes; lane++)
                    {
                        var weight = Read(descriptors[lane < 4 ? (byte)6 : (byte)8], vertex, lane & 3);
                        var matrixRow = Read(descriptors[lane < 4 ? (byte)7 : (byte)9], vertex, lane & 3);
                        if (!float.IsFinite(weight) || weight < 0)
                            throw new InvalidDataException("NativeSkinningWeightInvalid");
                        if (!float.IsFinite(matrixRow) || matrixRow < 0 || matrixRow != MathF.Truncate(matrixRow))
                            throw new InvalidDataException("NativeSkinningMatrixOffsetInvalid");
                        values[vertex * lanes + lane] = new(checked((uint)matrixRow), weight);
                        sum += weight;
                    }
                    if (pair.Value != 1 && (!float.IsFinite(sum) || sum <= 0))
                        throw new InvalidDataException("NativeSkinningWeightSumInvalid");
                }
                outputs[outputIndex++] = new(pair.Key, pair.Value, (int)buffer.ElementCount, values,
                    new ReadOnlyDictionary<byte, DecodedExtent>(extents));

                float Read(NativeVertexDescriptor descriptor, int vertex, int component)
                {
                    var start = checked(layout.VertexStarts[pair.Key][descriptor.Stream] +
                        (int)((long)buffer.Streams[descriptor.Stream].Stride * vertex) + descriptor.Offset + component * 2);
                    return (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(gpuBytes.Span.Slice(start, 2)));
                }
            }
            token.ThrowIfCancellationRequested();
            var result = new NativeSkinningAttributeDocument(document.Identity, groupIndex, source,
                Array.AsReadOnly(outputs), accounted);
            return new(result, reservation);
        }
        catch { reservation.Dispose(); throw; }
    }
}
