using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using Deviant.Assets;

namespace Deviant.Formats.Models;

public sealed class NativeMeshDataReader
{
    public const int MaximumBuffers = 1024;
    public const int MaximumGroups = 65536;
    public const int MaximumParts = 65536;
    public const int MaximumVertices = 4000000;
    public const int MaximumIndices = 12000000;
    public const int MaximumMetadataBytes = 64 * 1024 * 1024;

    public bool TryRead(NativeObjectFrame frame, out NativeMeshDataDocument? document,
        out string? diagnostic, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        document = null; diagnostic = null; token.ThrowIfCancellationRequested();
        try
        {
            if (frame.RawPayload.Length > MaximumMetadataBytes || frame.NativeType != 2130 ||
                frame.PayloadExtent.Length != frame.RawPayload.Length || frame.PayloadExtent.Offset < 0 ||
                frame.PayloadExtent.Offset > long.MaxValue - frame.RawPayload.Length ||
                frame.PayloadExtent.Container.IndexContentId != frame.Identity.IndexContentId ||
                frame.PayloadExtent.Container.Ordinal != frame.Identity.Ordinal)
                throw new InvalidDataException("NativeMeshSourceMismatch");
            var r = new NativeMeshByteReader(frame.RawPayload, frame.PayloadExtent);
            if (r.Word() != 3 || frame.ClassCount != 3) throw new InvalidDataException("NativeMeshClassCountUnsupported");
            r.Tag("MESHDATA"u8, 41);
            var bufferCount = r.Count(MaximumBuffers);
            var headers = new (int Fields, uint Elements, byte Flag)[bufferCount];
            for (var i = 0; i < bufferCount; i++)
            {
                token.ThrowIfCancellationRequested();
                headers[i] = (r.Count(256), (uint)r.Count(MaximumVertices), r.Byte());
                if (headers[i].Flag > 1) throw new InvalidDataException("NativeVertexStreamFlagUnsupported");
            }
            var indices = new uint[r.Count(MaximumBuffers)];
            for (var i = 0; i < indices.Length; i++) indices[i] = (uint)r.Count(MaximumIndices);
            var groups = new NativeMeshGroup[r.Count(MaximumGroups)];
            var totalParts = 0;
            for (var g = 0; g < groups.Length; g++)
            {
                token.ThrowIfCancellationRequested();
                var count = r.Count(MaximumParts);
                totalParts = checked(totalParts + count);
                if (totalParts > MaximumParts) throw new InvalidDataException("NativeMeshPartLimit");
                var h1 = r.Word(); var h2 = r.Word();
                var parts = new NativeMeshPart[count];
                for (var p = 0; p < count; p++)
                {
                    token.ThrowIfCancellationRequested();
                    var start = r.Position;
                    var leading = r.Word(); var storedByte = r.Byte(); var flags = r.Byte();
                    var vb = r.Word(); var baseVertex = r.Word(); var vertices = r.Word();
                    var ib = r.Word(); var firstIndex = r.Word(); var indexCount = r.Word();
                    var topology = r.Word(); var palette = new uint[r.Count(4096)];
                    for (var i = 0; i < palette.Length; i++) palette[i] = r.Word();
                    var material = r.Reference(frame.Identity);
                    var vectors = (uint)r.Count(65536);
                    var vectorExtent = r.Skip(checked((int)vectors * 48));
                    var control = r.Word(); var bounds = r.Bounds();
                    if (vb >= headers.Length || ib >= indices.Length || vertices > MaximumVertices ||
                        indexCount > MaximumIndices || (ulong)baseVertex + vertices > headers[vb].Elements ||
                        (ulong)firstIndex + indexCount > indices[ib])
                        throw new InvalidDataException("NativeMeshWindowInvalid");
                    parts[p] = new(leading, storedByte, flags, vb, baseVertex, vertices, ib, firstIndex,
                        indexCount, topology, palette, material, vectors, vectorExtent, control,
                        bounds, r.Extent(start, r.Position - start));
                }
                var f1 = r.Byte(); var f2 = r.Byte();
                groups[g] = new(h1, h2, parts, f1, f2, r.Bounds());
            }
            var referenceControl = r.Word();
            if ((referenceControl & ~0x1ffu) != 0) throw new InvalidDataException("NativeMeshReferenceControlUnsupported");
            var references = new ResourceReference[referenceControl & 255];
            for (var i = 0; i < references.Length; i++) references[i] = r.Reference(frame.Identity);
            float? factor = (referenceControl & 256) != 0 ? r.Float() : null;
            var stored = r.Word();
            var buffers = new NativeVertexBuffer[bufferCount];
            for (var b = 0; b < buffers.Length; b++)
            {
                token.ThrowIfCancellationRequested();
                var header = headers[b];
                var descriptors = new NativeVertexDescriptor[header.Fields];
                for (var d = 0; d < descriptors.Length; d++)
                {
                    var at = r.Position; descriptors[d] = new(r.Word(), r.Extent(at, 4));
                }
                var streams = new NativeVertexStream[4];
                for (var s = 0; s < streams.Length; s++)
                {
                    var stride = (uint)r.Count(4096);
                    var inline = header.Flag == 1 && s == 1 && stride != 0
                        ? r.Skip(checked((int)(stride * (ulong)header.Elements))) : null;
                    streams[s] = new(stride, inline);
                }
                buffers[b] = new(header.Elements, header.Flag, descriptors, streams);
            }
            var name = r.Utf8(1048576);
            var tailWord = r.Word(); var tailRef = r.Reference(frame.Identity);
            var flag1 = r.Byte(); var flag2 = r.Byte(); var tailWord2 = r.Word();
            var metadataExtent = r.Extent(0, r.Position);
            if (!new NativeMeshClusterV2Reader().TryRead(frame.RawPayload, frame.PayloadExtent,
                    r.Position, out var clusterMetadata, out var clusterError, token) || clusterMetadata is null)
                throw new InvalidDataException(clusterError ?? "NativeMeshClusterUnavailable");
            var clusterFlag = clusterMetadata.Flag;
            r.Skip(checked((int)clusterMetadata.Extent.Length));
            if (!new NativeMeshBlendShapeV3Reader().TryRead(frame.RawPayload, frame.PayloadExtent,
                    r.Position, groups.Length, out var blendShapeMetadata, out var blendShapeCounts,
                    out var blendShapeError, token) || blendShapeMetadata is null)
                throw new InvalidDataException(blendShapeError ?? "NativeMeshBlendShapeUnavailable");
            r.Skip(checked((int)blendShapeMetadata.Extent.Length));
            if (r.Position != frame.RawPayload.Length) throw new InvalidDataException("NativeMeshMetadataEndMismatch");
            document = new(frame.Identity, frame.NativeId, frame.PayloadExtent, buffers, indices, groups,
                referenceControl, references, factor, stored, name, tailWord,
                tailRef.ResourceType == 0 && tailRef.Id == "0" ? null : tailRef,
                flag1, flag2, tailWord2, metadataExtent, clusterFlag, blendShapeCounts)
                {
                    ClusterMetadata = clusterMetadata,
                    BlendShapeMetadata = blendShapeMetadata
                };
            return true;
        }
        catch (InvalidDataException error) { diagnostic = error.Message; return false; }
        catch (OverflowException) { diagnostic = "NativeMeshSizeOverflow"; return false; }
        catch (DecoderFallbackException) { diagnostic = "NativeMeshNameInvalid"; return false; }
    }
}

internal sealed class NativeMeshByteReader(ReadOnlyMemory<byte> bytes, DecodedExtent source)
{
    public int Position { get; private set; }
    public int Remaining => bytes.Length - Position;
    public ReadOnlySpan<byte> Take(int length)
    {
        if (length < 0 || length > bytes.Length - Position) throw new InvalidDataException("NativeMeshTruncated");
        var value = bytes.Span.Slice(Position, length); Position += length; return value;
    }
    public byte Byte() => Take(1)[0];
    public uint Word() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int Count(int maximum)
    {
        var count = Word();
        if (count > maximum) throw new InvalidDataException("NativeMeshCountLimit");
        return (int)count;
    }
    public float Float()
    {
        var value = BitConverter.UInt32BitsToSingle(Word());
        if (!float.IsFinite(value)) throw new InvalidDataException("NativeMeshNonFinite");
        return value;
    }
    public void Tag(ReadOnlySpan<byte> tag, uint version)
    {
        if (!Take(8).SequenceEqual(tag) || Word() != version)
            throw new InvalidDataException("NativeMeshClassUnsupported");
    }
    public DecodedExtent Extent(int start, int length) => new(source.Container, checked(source.Offset + start), length);
    public DecodedExtent Skip(int length) { var start = Position; Take(length); return Extent(start, length); }
    public ResourceReference Reference(ResourceIdentity owner)
    {
        var type = Word(); var id = Word();
        return new(IdentifierNamespace.Native, id.ToString(CultureInfo.InvariantCulture), owner, type);
    }
    public NativeMeshBounds Bounds()
    {
        var start = Position; var values = new float[15];
        for (var i = 0; i < values.Length; i++) values[i] = Float();
        var extra = new Vector4(Float(), Float(), Float(), Float());
        return new(values, extra, Extent(start, Position - start));
    }
    public string Utf8(int maximum) => new UTF8Encoding(false, true).GetString(Take(Count(maximum)));
}
