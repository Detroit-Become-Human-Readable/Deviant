using System.Text;
using Deviant.Assets;

namespace Deviant.Formats.Models;

public sealed record NativeMeshStoredName(string Text, DecodedExtent Extent);
public sealed record NativeMeshStoredShapeName(int GroupOrdinal, int EntryOrdinal, int StringOrdinal,
    ReadOnlyMemory<byte> RawBytes, DecodedExtent BytesExtent, uint RawTrailingWord,
    DecodedExtent TrailingWordExtent);
public sealed record NativeMeshInventoryNameHints(NativeMeshStoredName? Name,
    IReadOnlyList<NativeMeshStoredShapeName> ShapeNames,
    IReadOnlyDictionary<int, int> OmittedShapeNameCountsByGroup,
    bool ShapeNamesComplete, string? ShapeNamesDiagnostic);

/// <summary>Reads a bounded stored name from the MESHDATA v41 metadata prefix.</summary>
public sealed class NativeMeshNameReader
{
    public const int MaximumNameBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly record struct BufferHeader(int Fields, uint Elements, byte Flag);

    public bool TryRead(NativeObjectFrame frame, out NativeMeshStoredName? name,
        out string? diagnostic, int maximumNameBytes = 4096, CancellationToken token = default)
    {
        var result = TryReadCore(frame, collectShapeNames: false, targetGroupOrdinal: -1,
            maximumShapeNames: 0, maximumShapeNameBytes: 0, out var hints, out diagnostic,
            maximumNameBytes, token);
        name = hints?.Name;
        return result;
    }

    /// <summary>Reads the stored mesh name and bounded BLSHAPES labels for one group, or all groups when target is -1.</summary>
    public bool TryReadInventoryHints(NativeObjectFrame frame, int targetGroupOrdinal,
        out NativeMeshInventoryNameHints? hints, out string? diagnostic,
        int maximumNameBytes = 4096, int maximumShapeNames = 512,
        long maximumShapeNameBytes = 256 * 1024, CancellationToken token = default)
        => TryReadCore(frame, collectShapeNames: true, targetGroupOrdinal, maximumShapeNames,
            maximumShapeNameBytes, out hints, out diagnostic, maximumNameBytes, token);

    private bool TryReadCore(NativeObjectFrame frame, bool collectShapeNames, int targetGroupOrdinal,
        int maximumShapeNames, long maximumShapeNameBytes, out NativeMeshInventoryNameHints? hints,
        out string? diagnostic, int maximumNameBytes, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(frame);
        hints = null;
        diagnostic = null;
        token.ThrowIfCancellationRequested();
        if (maximumNameBytes is < 0 or > MaximumNameBytes ||
            maximumShapeNames is < 0 or > NativeMeshBlendShapeV3Reader.MaximumStringRecords ||
            maximumShapeNameBytes < 0 || maximumShapeNameBytes > NativeMeshDataReader.MaximumMetadataBytes ||
            collectShapeNames && targetGroupOrdinal < -1)
            throw new ArgumentOutOfRangeException(nameof(maximumNameBytes));

        try
        {
            if (frame.RawPayload.Length > NativeMeshDataReader.MaximumMetadataBytes || frame.NativeType != 2130 ||
                frame.ClassCount != 3 || frame.PayloadExtent.Length != frame.RawPayload.Length ||
                frame.PayloadExtent.Offset < 0 || frame.PayloadExtent.Offset > long.MaxValue - frame.RawPayload.Length ||
                frame.PayloadExtent.Container.IndexContentId != frame.Identity.IndexContentId ||
                frame.PayloadExtent.Container.Ordinal != frame.Identity.Ordinal)
                throw new InvalidDataException("NativeMeshSourceMismatch");

            var reader = new NativeMeshByteReader(frame.RawPayload, frame.PayloadExtent);
            if (reader.Word() != 3) throw new InvalidDataException("NativeMeshClassCountUnsupported");
            reader.Tag("MESHDATA"u8, 41);

            var bufferCount = reader.Count(NativeMeshDataReader.MaximumBuffers);
            var headers = new BufferHeader[bufferCount];
            for (var index = 0; index < headers.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var fields = reader.Count(256);
                var elements = (uint)reader.Count(NativeMeshDataReader.MaximumVertices);
                var flag = reader.Byte();
                if (flag > 1) throw new InvalidDataException("NativeVertexStreamFlagUnsupported");
                headers[index] = new(fields, elements, flag);
            }

            var indexCount = reader.Count(NativeMeshDataReader.MaximumBuffers);
            var indexCounts = new uint[indexCount];
            for (var index = 0; index < indexCounts.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                indexCounts[index] = (uint)reader.Count(NativeMeshDataReader.MaximumIndices);
            }

            var groupCount = reader.Count(NativeMeshDataReader.MaximumGroups);
            var totalParts = 0;
            for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
            {
                token.ThrowIfCancellationRequested();
                var partCount = reader.Count(NativeMeshDataReader.MaximumParts);
                totalParts = checked(totalParts + partCount);
                if (totalParts > NativeMeshDataReader.MaximumParts)
                    throw new InvalidDataException("NativeMeshPartLimit");
                reader.Word(); reader.Word();
                for (var partIndex = 0; partIndex < partCount; partIndex++)
                {
                    token.ThrowIfCancellationRequested();
                    reader.Word(); reader.Byte(); reader.Byte();
                    var vertexBuffer = reader.Word();
                    var baseVertex = reader.Word();
                    var vertices = reader.Word();
                    var indexBuffer = reader.Word();
                    var firstIndex = reader.Word();
                    var indices = reader.Word();
                    reader.Word();
                    var paletteCount = reader.Count(4096);
                    reader.Skip(checked(paletteCount * 4));
                    reader.Skip(8); // material reference
                    var vectorCount = reader.Count(65536);
                    reader.Skip(checked(vectorCount * 48));
                    reader.Word();
                    SkipBounds(reader, token);

                    if (vertexBuffer >= headers.Length || indexBuffer >= indexCounts.Length ||
                        vertices > NativeMeshDataReader.MaximumVertices || indices > NativeMeshDataReader.MaximumIndices ||
                        (ulong)baseVertex + vertices > headers[checked((int)vertexBuffer)].Elements ||
                        (ulong)firstIndex + indices > indexCounts[checked((int)indexBuffer)])
                        throw new InvalidDataException("NativeMeshWindowInvalid");
                }
                reader.Byte(); reader.Byte();
                SkipBounds(reader, token);
            }

            var referenceControl = reader.Word();
            if ((referenceControl & ~0x1ffu) != 0)
                throw new InvalidDataException("NativeMeshReferenceControlUnsupported");
            var referenceCount = (int)(referenceControl & 255);
            reader.Skip(checked(referenceCount * 8));
            if ((referenceControl & 256) != 0) reader.Float();
            reader.Word(); // stored mesh metadata

            for (var bufferIndex = 0; bufferIndex < headers.Length; bufferIndex++)
            {
                token.ThrowIfCancellationRequested();
                var header = headers[bufferIndex];
                for (var fieldIndex = 0; fieldIndex < header.Fields; fieldIndex++) reader.Word();
                for (var streamIndex = 0; streamIndex < 4; streamIndex++)
                {
                    token.ThrowIfCancellationRequested();
                    var stride = reader.Count(4096);
                    if (header.Flag == 1 && streamIndex == 1 && stride != 0)
                        reader.Skip(checked((int)((ulong)stride * header.Elements)));
                }
            }

            var rawNameLength = reader.Word();
            if (rawNameLength > maximumNameBytes) throw new InvalidDataException("NativeMeshNameLimit");
            var nameLength = (int)rawNameLength;
            var nameOffset = reader.Position;
            var text = StrictUtf8.GetString(reader.Take(nameLength));
            token.ThrowIfCancellationRequested();
            var name = new NativeMeshStoredName(text, reader.Extent(nameOffset, nameLength));
            var labels = Array.Empty<NativeMeshStoredShapeName>();
            IReadOnlyDictionary<int, int> omittedByGroup = new Dictionary<int, int>();
            bool shapeNamesComplete = false;
            string? shapeNamesDiagnostic = null;
            if (collectShapeNames)
            {
                if (!TryReadShapeNames(frame, reader, groupCount, targetGroupOrdinal, maximumShapeNames,
                        maximumShapeNameBytes, out labels, out omittedByGroup, out shapeNamesDiagnostic, token))
                    labels = [];
                else shapeNamesComplete = true;
            }
            hints = new(name, labels, omittedByGroup, shapeNamesComplete, shapeNamesDiagnostic);
            return true;
        }
        catch (InvalidDataException error) { diagnostic = error.Message; return false; }
        catch (OverflowException) { diagnostic = "NativeMeshSizeOverflow"; return false; }
        catch (DecoderFallbackException) { diagnostic = "NativeMeshNameInvalid"; return false; }
    }

    private static bool TryReadShapeNames(NativeObjectFrame frame, NativeMeshByteReader reader, int groupCount,
        int targetGroupOrdinal, int maximumNames, long maximumTextBytes,
        out NativeMeshStoredShapeName[] names, out IReadOnlyDictionary<int, int> omittedByGroup,
        out string? diagnostic,
        CancellationToken token)
    {
        names = [];
        var omitted = new Dictionary<int, int>();
        omittedByGroup = omitted;
        diagnostic = null;
        try
        {
            reader.Word(); // trailing mesh scalar
            reader.Word(); reader.Word(); // trailing native reference
            reader.Byte(); reader.Byte(); reader.Word();
            SkipClusterSection(reader, token);
            reader.Tag("BLSHAPES"u8, 3);

            var results = new List<NativeMeshStoredShapeName>(Math.Min(maximumNames, 32));
            var maximumEntries = NativeMeshBlendShapeV3Reader.MaximumEntries;
            long totalEntries = 0, totalStrings = 0, totalTextBytes = 0, retainedTextBytes = 0;
            for (var groupOrdinal = 0; groupOrdinal < groupCount; groupOrdinal++)
            {
                token.ThrowIfCancellationRequested();
                var entryCount = reader.Count(maximumEntries);
                totalEntries = checked(totalEntries + entryCount);
                if (totalEntries > maximumEntries) throw new InvalidDataException("NativeMeshBlendShapeEntryLimit");
                for (var entryOrdinal = 0; entryOrdinal < entryCount; entryOrdinal++)
                {
                    token.ThrowIfCancellationRequested();
                    var stringCount = reader.Word();
                    var tableColumnCount = reader.Word();
                    var floatTupleCount = reader.Word();
                    var wordArrayCount = reader.Word();
                    reader.Word(); // reserved word
                    var flags = reader.Byte();
                    if (stringCount > maximumEntries || tableColumnCount > maximumEntries ||
                        floatTupleCount > maximumEntries || wordArrayCount > maximumEntries)
                        throw new InvalidDataException("NativeMeshBlendShapeCountLimit");
                    totalStrings = checked(totalStrings + stringCount);
                    if (totalStrings > NativeMeshBlendShapeV3Reader.MaximumStringRecords)
                        throw new InvalidDataException("NativeMeshBlendShapeStringRecordLimit");

                    for (var stringOrdinal = 0; stringOrdinal < (int)stringCount; stringOrdinal++)
                    {
                        token.ThrowIfCancellationRequested();
                        var rawLength = reader.Word();
                        if (rawLength > NativeMeshBlendShapeV3Reader.MaximumStringBytes)
                            throw new InvalidDataException("NativeMeshBlendShapeStringLimit");
                        int length = checked((int)rawLength);
                        var bytesStart = reader.Position;
                        reader.Skip(length);
                        var trailingStart = reader.Position;
                        var trailing = reader.Word();
                        totalTextBytes = checked(totalTextBytes + length);
                        if (totalTextBytes > NativeMeshDataReader.MaximumMetadataBytes)
                            throw new InvalidDataException("NativeMeshBlendShapeDataLimit");
                        if (targetGroupOrdinal >= 0 && groupOrdinal != targetGroupOrdinal) continue;
                        if (results.Count >= maximumNames || checked(retainedTextBytes + length) > maximumTextBytes)
                        {
                            omitted[groupOrdinal] = checked(omitted.GetValueOrDefault(groupOrdinal) + 1);
                            continue;
                        }
                        results.Add(new(groupOrdinal, entryOrdinal, stringOrdinal,
                            frame.RawPayload.Slice(bytesStart, length), reader.Extent(bytesStart, length),
                            trailing, reader.Extent(trailingStart, sizeof(uint))));
                        retainedTextBytes = checked(retainedTextBytes + length);
                    }

                    var tableBytes = checked((ulong)stringCount * tableColumnCount * sizeof(ushort));
                    var arrayBytes = checked((ulong)wordArrayCount * sizeof(uint));
                    var floatBytes = checked((ulong)floatTupleCount * 3 * sizeof(uint));
                    var flagBytes = ((flags & 1) != 0 ? checked((ulong)floatTupleCount * sizeof(uint)) : 0) +
                        ((flags & 2) != 0 ? checked((ulong)floatTupleCount * sizeof(uint)) : 0);
                    SkipBounded(reader, arrayBytes);
                    SkipBounded(reader, arrayBytes);
                    SkipBounded(reader, tableBytes);
                    SkipBounded(reader, floatBytes);
                    SkipBounded(reader, flagBytes);
                }
            }
            if (reader.Remaining != 0) throw new InvalidDataException("NativeMeshMetadataEndMismatch");
            names = results.ToArray();
            omittedByGroup = new Dictionary<int, int>(omitted);
            return true;
        }
        catch (InvalidDataException error) { diagnostic = error.Message; return false; }
        catch (OverflowException) { diagnostic = "NativeMeshBlendShapeSizeOverflow"; return false; }
    }

    private static void SkipBounded(NativeMeshByteReader reader, ulong length)
    {
        if (length > NativeMeshDataReader.MaximumMetadataBytes || length > int.MaxValue)
            throw new InvalidDataException("NativeMeshBlendShapeDataLimit");
        reader.Skip(checked((int)length));
    }

    private static void SkipClusterSection(NativeMeshByteReader reader, CancellationToken token)
    {
        reader.Tag("CLUPSKME"u8, 2);
        var flag = reader.Byte();
        if (flag == 0) return;
        if (flag != 1) throw new InvalidDataException("NativeMeshClustersUnsupported");
        var count = (uint)reader.Count(NativeMeshClusterV2Reader.MaximumEntryCount);
        token.ThrowIfCancellationRequested();
        reader.Skip(24); // six scalar words
        reader.Skip(checked((int)count * 3));
        reader.Skip(checked((int)count * 12));
        token.ThrowIfCancellationRequested();
    }

    private static void SkipBounds(NativeMeshByteReader reader, CancellationToken token)
    {
        // MESHDATA bounds contain nineteen finite scalar values.
        for (var index = 0; index < 19; index++)
        {
            token.ThrowIfCancellationRequested();
            reader.Float();
        }
    }
}
