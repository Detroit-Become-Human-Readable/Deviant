using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using Deviant.Assets;

namespace Deviant.Formats.Models;

public sealed class NativeMeshGeometryDecoder
{
    public const int MaximumGpuBytes = 512 * 1024 * 1024;
    public const int MaximumOutputVertices = 8000000;
    public const int MaximumOutputIndices = 24000000;

    public GeometryAsset Decode(NativeMeshDataDocument document, ReadOnlyMemory<byte> gpuBytes,
        int groupIndex, CancellationToken token = default, DecodedExtent? gpuExtent = null)
        => DecodeCore(document, gpuBytes, groupIndex, null, token, gpuExtent);

    /// <summary>Decodes source vertex streams for a skinning-aware caller without applying a pose.</summary>
    public GeometryAsset DecodeSkinSource(NativeMeshDataDocument document, ReadOnlyMemory<byte> gpuBytes,
        int groupIndex, NativeSkinningAttributeDocument attributes, CancellationToken token = default,
        DecodedExtent? gpuExtent = null)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        return DecodeCore(document, gpuBytes, groupIndex, attributes, token, gpuExtent);
    }

    private GeometryAsset DecodeCore(NativeMeshDataDocument document, ReadOnlyMemory<byte> gpuBytes,
        int groupIndex, NativeSkinningAttributeDocument? skinningAttributes, CancellationToken token,
        DecodedExtent? gpuExtent)
    {
        ArgumentNullException.ThrowIfNull(document); token.ThrowIfCancellationRequested();
        if (groupIndex < 0 || groupIndex >= document.Groups.Count) throw new InvalidDataException("NativeMeshGroupInvalid");
        if (gpuBytes.Length > MaximumGpuBytes) throw new InvalidDataException("NativeMeshGpuLimit");
        var gpuSource = gpuExtent ?? new(document.Identity with
            { MemberPath = (document.Identity.MemberPath ?? "native-mesh") + "/gpu" }, 0, gpuBytes.Length);
        if (gpuSource.Offset < 0 || gpuSource.Length != gpuBytes.Length || gpuSource.Offset > long.MaxValue - gpuBytes.Length)
            throw new InvalidDataException("NativeMeshGpuSourceMismatch");
        if (skinningAttributes is not null)
            ValidateSkinSource(document, groupIndex, gpuSource, skinningAttributes, token);
        var layout = BuildLayout(document, groupIndex, gpuBytes.Length, token);
        var group = document.Groups[groupIndex];
        if (group.Parts.Count > NativeMeshDataReader.MaximumParts) throw new InvalidDataException("NativeMeshPartLimit");
        long totalVertices = 0, totalIndices = 0;
        foreach (var part in group.Parts)
        {
            token.ThrowIfCancellationRequested();
            totalVertices += part.VertexCount; totalIndices += part.IndexCount;
            if (totalVertices > MaximumOutputVertices || totalIndices > MaximumOutputIndices)
                throw new InvalidDataException("NativeMeshOutputLimit");
            if (skinningAttributes is null && (part.TopologySelector != 0 || part.Palette.Count != 0))
                throw new InvalidDataException("NativeMeshPartProfileUnsupported");
        }
        var primitives = new MeshPrimitive[group.Parts.Count];
        var unknown = new Dictionary<string, string>
        {
            ["NativeProfile"] = "MESHDATA41", ["GpuBytes"] = gpuBytes.Length.ToString(CultureInfo.InvariantCulture)
        };
        for (var p = 0; p < primitives.Length; p++)
        {
            token.ThrowIfCancellationRequested();
            var part = group.Parts[p];
            if (part.VertexBufferIndex >= document.VertexBuffers.Count || part.IndexBufferIndex >= document.IndexCounts.Count)
                throw new InvalidDataException("NativeMeshBufferIndexInvalid");
            var buffer = document.VertexBuffers[(int)part.VertexBufferIndex];
            if (buffer.Flag != 0) throw new InvalidDataException("NativeVertexInlineStreamUnsupported");
            if ((ulong)part.BaseVertex + part.VertexCount > buffer.ElementCount ||
                (ulong)part.FirstIndex + part.IndexCount > document.IndexCounts[(int)part.IndexBufferIndex] ||
                part.IndexCount % 3 != 0 || part.VertexCount == 0 || part.IndexCount == 0)
                throw new InvalidDataException("NativeMeshWindowInvalid");
            var positionsDescriptor = Find(buffer, 0, required: true)!;
            if (positionsDescriptor.Format != 2) throw new InvalidDataException("NativeMeshPositionFormatUnsupported");
            var normalDescriptor = Find(buffer, 1);
            var tangentDescriptor = Find(buffer, 10);
            var colorDescriptor = Find(buffer, 2);
            var uvDescriptor = Find(buffer, 4);
            var secondaryUvCandidate = Find(buffer, 5);
            var secondaryUvDescriptor = uvDescriptor?.Format == 6 && secondaryUvCandidate?.Format is 5 or 6
                ? secondaryUvCandidate : null;
            var positions = new Vector3[part.VertexCount];
            var normals = normalDescriptor is null ? null : new Vector3[part.VertexCount];
            var tangents = tangentDescriptor is null ? null : new Vector4[part.VertexCount];
            var colors = colorDescriptor is null ? null : new Vector4[part.VertexCount];
            var uv = uvDescriptor is null ? null : new Vector2[part.VertexCount];
            var uv1 = uvDescriptor?.Format == 6 ? new Vector2[part.VertexCount] : null;
            var uv2 = secondaryUvDescriptor is null ? null : new Vector2[part.VertexCount];
            var uv3 = secondaryUvDescriptor?.Format == 6 ? new Vector2[part.VertexCount] : null;
            var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
            for (var v = 0; v < positions.Length; v++)
            {
                if ((v & 1023) == 0) token.ThrowIfCancellationRequested();
                var vertex = checked(part.BaseVertex + (uint)v);
                var point = Read(positionsDescriptor, vertex);
                positions[v] = new(point.X, point.Y, point.Z);
                minimum = Vector3.Min(minimum, positions[v]); maximum = Vector3.Max(maximum, positions[v]);
                if (normals is not null) { var n = Read(normalDescriptor!, vertex); normals[v] = new(n.X, n.Y, n.Z); }
                if (tangents is not null)
                {
                    var t = Read(tangentDescriptor!, vertex);
                    tangents[v] = new(t.X, t.Y, t.Z, tangentDescriptor!.Format == 13 ? (t.W != 0 ? -1 : 1) : t.W);
                }
                if (colors is not null) colors[v] = Read(colorDescriptor!, vertex);
                if (uv is not null)
                {
                    var t = Read(uvDescriptor!, vertex); uv[v] = new(t.X, t.Y);
                    if (uv1 is not null) uv1[v] = new(t.Z, t.W);
                }
                if (uv2 is not null)
                {
                    var t = Read(secondaryUvDescriptor!, vertex); uv2[v] = new(t.X, t.Y);
                    if (uv3 is not null) uv3[v] = new(t.Z, t.W);
                }
            }
            var decodedIndices = new uint[part.IndexCount];
            var indexStart = checked(layout.IndexStarts[(int)part.IndexBufferIndex] + (int)part.FirstIndex * 2);
            for (var i = 0; i < decodedIndices.Length; i++)
            {
                if ((i & 2047) == 0) token.ThrowIfCancellationRequested();
                var value = BinaryPrimitives.ReadUInt16LittleEndian(gpuBytes.Span[(indexStart + i * 2)..]);
                if (value >= part.VertexCount) throw new InvalidDataException("NativeMeshIndexOutOfRange");
                decodedIndices[i] = value;
            }
            var key = $"{document.Identity.Key}/group/{groupIndex}/part/{p}";
            var extents = new Dictionary<string, DecodedExtent>
            {
                ["NativePart"] = part.Extent,
                ["Indices"] = At(indexStart, checked(decodedIndices.Length * 2)),
                ["Positions"] = AttributeExtent(positionsDescriptor)
            };
            if (normalDescriptor is not null) extents["Normals"] = AttributeExtent(normalDescriptor);
            if (tangentDescriptor is not null) extents["Tangents"] = AttributeExtent(tangentDescriptor);
            if (uvDescriptor is not null) extents["TextureCoordinates0"] = AttributeExtent(uvDescriptor);
            if (uv1 is not null) extents["TextureCoordinates1"] = AttributeExtent(uvDescriptor!);
            if (uv2 is not null) extents["TextureCoordinates2"] = AttributeExtent(secondaryUvDescriptor!);
            if (uv3 is not null) extents["TextureCoordinates3"] = AttributeExtent(secondaryUvDescriptor!);
            if (colorDescriptor is not null) extents["Colors"] = AttributeExtent(colorDescriptor);
            primitives[p] = new(key, PrimitiveTopology.Triangles, positions, decodedIndices,
                IndexElementFormat.UInt16, [new(key + "/submesh", 0, decodedIndices.Length, [part.Material])],
                normals, tangents, uv is null ? null : uv3 is not null ? [uv, uv1!, uv2!, uv3]
                    : uv2 is not null ? [uv, uv1!, uv2] : uv1 is not null ? [uv, uv1] : [uv],
                new(minimum, maximum), FieldExtents: extents, Colors: colors);
            unknown[key + "/Descriptors"] = string.Join(",", buffer.Descriptors.Select(d => d.Word.ToString("X8", CultureInfo.InvariantCulture)));

            Vector4 Read(NativeVertexDescriptor descriptor, uint vertex)
            {
                var stream = buffer.Streams[descriptor.Stream];
                var start = checked(layout.VertexStarts[(int)part.VertexBufferIndex][descriptor.Stream] +
                    (int)((ulong)stream.Stride * vertex) + descriptor.Offset);
                var value = ReadVector(gpuBytes.Span.Slice(start, FormatSize(descriptor.Format)), descriptor.Format);
                if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W))
                    throw new InvalidDataException("NativeMeshVertexNonFinite");
                return value;
            }
            DecodedExtent At(int offset, int length) => new(gpuSource.Container, checked(gpuSource.Offset + offset), length);
            DecodedExtent AttributeExtent(NativeVertexDescriptor descriptor)
            {
                var stride = buffer.Streams[descriptor.Stream].Stride;
                var start = checked(layout.VertexStarts[(int)part.VertexBufferIndex][descriptor.Stream] +
                    (int)((ulong)stride * part.BaseVertex) + descriptor.Offset);
                return At(start, checked((int)((ulong)stride * (part.VertexCount - 1) + (uint)FormatSize(descriptor.Format))));
            }
        }
        var groupKey = $"{document.Identity.Key}/group/{groupIndex}";
        return new(new("native-mesh", "MESHDATA41", MatrixLayout: MatrixLayout.RowMajor,
                VectorSide: VectorMultiplicationSide.Row),
            [new(groupKey, null, new(IdentifierNamespace.Native, document.NativeId.ToString(CultureInfo.InvariantCulture), document.Identity, 2130),
                primitives.Select(p => p.Key).ToArray())], primitives, unknown);
    }

    public bool TryDecode(NativeMeshDataDocument document, ReadOnlyMemory<byte> gpuBytes,
        int groupIndex, out GeometryAsset? geometry, out string? diagnostic,
        CancellationToken token = default, DecodedExtent? gpuExtent = null)
    {
        geometry = null; diagnostic = null;
        try { geometry = Decode(document, gpuBytes, groupIndex, token, gpuExtent); return true; }
        catch (InvalidDataException error) { diagnostic = error.Message; return false; }
        catch (OverflowException) { diagnostic = "NativeMeshSizeOverflow"; return false; }
    }

    private static void ValidateSkinSource(NativeMeshDataDocument document, int groupIndex,
        DecodedExtent gpuSource, NativeSkinningAttributeDocument attributes, CancellationToken token)
    {
        if (attributes.Identity != document.Identity || attributes.GroupIndex != groupIndex ||
            attributes.GpuExtent != gpuSource || attributes.Buffers is null)
            throw new InvalidDataException("NativeSkinningAttributeSourceMismatch");
        if (document.Groups.Count > NativeMeshDataReader.MaximumGroups ||
            document.VertexBuffers.Count > NativeMeshDataReader.MaximumBuffers ||
            document.IndexCounts.Count > NativeMeshDataReader.MaximumBuffers ||
            attributes.Buffers.Count > NativeMeshDataReader.MaximumBuffers)
            throw new InvalidDataException("NativeSkinningBufferLimit");
        foreach (var attributeBuffer in attributes.Buffers)
        {
            token.ThrowIfCancellationRequested();
            if (attributeBuffer is null)
                throw new InvalidDataException("NativeSkinningAttributeBufferInvalid");
        }

        var group = document.Groups[groupIndex];
        if (group.Parts.Count > NativeMeshDataReader.MaximumParts)
            throw new InvalidDataException("NativeMeshPartLimit");
        long totalVertices = 0, totalIndices = 0;
        foreach (var part in group.Parts)
        {
            token.ThrowIfCancellationRequested();
            if (part.TopologySelector is not (0 or 1 or 4 or 8))
                throw new InvalidDataException("NativeSkinningModeUnsupported");
            if (part.Palette.Count != 0)
                throw new InvalidDataException("NativeMeshPartPaletteUnsupported");
            if (part.VertexBufferIndex >= document.VertexBuffers.Count || part.IndexBufferIndex >= document.IndexCounts.Count)
                throw new InvalidDataException("NativeMeshBufferIndexInvalid");

            var bufferIndex = (int)part.VertexBufferIndex;
            var buffer = document.VertexBuffers[bufferIndex];
            if (buffer.ElementCount > NativeMeshDataReader.MaximumVertices || buffer.Streams.Count != 4 ||
                buffer.Descriptors.Count > 256)
                throw new InvalidDataException("NativeVertexBufferUnsupported");
            if (buffer.Flag != 0) throw new InvalidDataException("NativeVertexInlineStreamUnsupported");
            if (document.IndexCounts[(int)part.IndexBufferIndex] > NativeMeshDataReader.MaximumIndices)
                throw new InvalidDataException("NativeMeshIndexLimit");
            if (part.BaseVertex > buffer.ElementCount || part.VertexCount > buffer.ElementCount - part.BaseVertex ||
                part.FirstIndex > document.IndexCounts[(int)part.IndexBufferIndex] ||
                part.IndexCount > document.IndexCounts[(int)part.IndexBufferIndex] - part.FirstIndex ||
                part.IndexCount % 3 != 0 || part.VertexCount == 0 || part.IndexCount == 0)
                throw new InvalidDataException("NativeMeshWindowInvalid");

            totalVertices += part.VertexCount;
            totalIndices += part.IndexCount;
            if (totalVertices > MaximumOutputVertices || totalIndices > MaximumOutputIndices)
                throw new InvalidDataException("NativeMeshOutputLimit");

            var position = Find(buffer, 0, required: true)!;
            if (position.Format != 2) throw new InvalidDataException("NativeMeshPositionFormatUnsupported");
            var normal = Find(buffer, 1);
            var tangent = Find(buffer, 10);
            var color = Find(buffer, 2);
            var uv = Find(buffer, 4);
            _ = FormatSize(position.Format);
            if (normal is not null) _ = FormatSize(normal.Format);
            if (tangent is not null) _ = FormatSize(tangent.Format);
            if (color is not null) _ = FormatSize(color.Format);
            if (uv is not null) _ = FormatSize(uv.Format);

            if (part.TopologySelector == 0) continue;
            NativeSkinningBufferAttributes? match = null;
            var matchCount = 0;
            foreach (var candidate in attributes.Buffers)
            {
                token.ThrowIfCancellationRequested();
                if (candidate is null) throw new InvalidDataException("NativeSkinningAttributeBufferInvalid");
                if (candidate.BufferIndex != bufferIndex) continue;
                match = candidate;
                matchCount++;
            }
            if (matchCount != 1 || match is null || match.StoredMode != part.TopologySelector ||
                match.VertexCount != buffer.ElementCount)
                throw new InvalidDataException("NativeSkinningAttributeBufferMismatch");
        }
    }

    private static NativeVertexDescriptor? Find(NativeVertexBuffer buffer, byte semantic, bool required = false)
    {
        NativeVertexDescriptor? result = null;
        foreach (var descriptor in buffer.Descriptors)
        {
            if (descriptor.Semantic != semantic) continue;
            if (result is not null) throw new InvalidDataException("NativeMeshSemanticDuplicate");
            result = descriptor;
        }
        if (result is null && required) throw new InvalidDataException("NativeMeshPositionMissing");
        return result;
    }

    internal static (int[] IndexStarts, int[][] VertexStarts) BuildLayout(NativeMeshDataDocument document,
        int groupIndex, int byteCount, CancellationToken token)
    {
        if (document.IndexCounts.Count > NativeMeshDataReader.MaximumBuffers ||
            document.VertexBuffers.Count > NativeMeshDataReader.MaximumBuffers)
            throw new InvalidDataException("NativeMeshBufferLimit");
        long cursor = 0;
        var indices = new int[document.IndexCounts.Count];
        for (var i = 0; i < indices.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if (document.IndexCounts[i] > NativeMeshDataReader.MaximumIndices) throw new InvalidDataException("NativeMeshIndexLimit");
            indices[i] = checked((int)cursor); cursor += document.IndexCounts[i] * 2L; cursor = Align(cursor);
            if (cursor > byteCount) throw new InvalidDataException("NativeMeshGpuTruncated");
        }
        var vertices = new int[document.VertexBuffers.Count][];
        var usedBuffers = document.Groups[groupIndex].Parts.Select(part => part.VertexBufferIndex).ToHashSet();
        for (var i = 0; i < vertices.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var buffer = document.VertexBuffers[i];
            if (buffer.Flag != 0 || buffer.Streams.Count != 4 || buffer.ElementCount > NativeMeshDataReader.MaximumVertices || buffer.Descriptors.Count > 256)
                throw new InvalidDataException("NativeVertexBufferUnsupported");
            var start = cursor; vertices[i] = new int[4];
            for (var s = 0; s < 4; s++)
            {
                if (buffer.Streams[s].Stride > 4096) throw new InvalidDataException("NativeVertexStrideLimit");
                vertices[i][s] = checked((int)cursor);
                cursor = checked(cursor + (long)buffer.Streams[s].Stride * buffer.ElementCount);
                if (cursor > byteCount) throw new InvalidDataException("NativeMeshGpuTruncated");
            }
            cursor = start + Align(cursor - start);
            if (cursor > byteCount) throw new InvalidDataException("NativeMeshGpuTruncated");
            foreach (var descriptor in buffer.Descriptors)
            {
                if (descriptor.Stream > 3) throw new InvalidDataException("NativeVertexDescriptorRangeInvalid");
                var size = KnownFormatSize(descriptor.Format);
                if (size is null && usedBuffers.Contains((uint)i) && descriptor.Semantic is 0 or 1 or 2 or 4 or 10)
                    throw new InvalidDataException("NativeVertexFormatUnsupported");
                // Unread attributes retain their descriptors; only their starting offset is needed.
                if (descriptor.Offset + (size ?? 1) > buffer.Streams[descriptor.Stream].Stride)
                    throw new InvalidDataException("NativeVertexDescriptorRangeInvalid");
            }
        }
        if (cursor != byteCount) throw new InvalidDataException("NativeMeshGpuEndMismatch");
        return (indices, vertices);
    }
    private static long Align(long value) => checked((value + 15) & ~15L);
    private static int? KnownFormatSize(byte format) => format switch
    {
        2 => 12, 4 or 5 or 13 => 4, 6 => 8,
        _ => null
    };
    private static int FormatSize(byte format) => KnownFormatSize(format)
        ?? throw new InvalidDataException("NativeVertexFormatUnsupported");
    private static Vector4 ReadVector(ReadOnlySpan<byte> bytes, byte format)
    {
        switch (format)
        {
            case 2: return new(Float(bytes), Float(bytes[4..]), Float(bytes[8..]), 0);
            case 4: return new(bytes[0] / 255f, bytes[1] / 255f, bytes[2] / 255f, bytes[3] / 255f);
            case 5: return new(Half(bytes), Half(bytes[2..]), 0, 0);
            case 6: return new(Half(bytes), Half(bytes[2..]), Half(bytes[4..]), Half(bytes[6..]));
            case 13: return ReadPackedVector(BinaryPrimitives.ReadUInt32LittleEndian(bytes));
            default: throw new InvalidDataException("NativeVertexFormatUnsupported");
        }
        static float Float(ReadOnlySpan<byte> b) => BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(b));
        static float Half(ReadOnlySpan<byte> b) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(b));
    }

    internal static Vector4 ReadPackedVector(uint word) =>
        new(PackedComponent(word), PackedComponent(word >> 10), PackedComponent(word >> 20), word >> 30);

    private static float PackedComponent(uint word)
    {
        var value = (int)(word & 1023);
        return (value > 512 ? value - 1024 : value) / 511f;
    }
}
