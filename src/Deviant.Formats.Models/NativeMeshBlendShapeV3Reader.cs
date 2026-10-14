using Deviant.Assets;

namespace Deviant.Formats.Models;

/// <summary>Reads bounded BLSHAPES v3 wire data without assigning shape semantics.</summary>
public sealed class NativeMeshBlendShapeV3Reader
{
    public const int MaximumGroups = NativeMeshDataReader.MaximumGroups;
    public const int MaximumEntries = NativeMeshDataReader.MaximumParts;
    public const int MaximumStringRecords = NativeMeshDataReader.MaximumParts;
    public const int MaximumStringBytes = 1024 * 1024;
    public const int MaximumRetainedMetadataBytes = 32 * 1024 * 1024;
    private const int GroupMetadataAllowanceBytes = 160;
    private const int EntryMetadataAllowanceBytes = 512;
    private const int StringRecordMetadataAllowanceBytes = 512;

    public bool TryRead(ReadOnlyMemory<byte> payload, DecodedExtent source, int offset, int groupCount,
        out NativeMeshBlendShapeSection? section, out IReadOnlyList<DecodedExtent> countExtents,
        out string? diagnostic, CancellationToken token = default,
        int maximumPayloadBytes = NativeMeshDataReader.MaximumMetadataBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        section = null;
        countExtents = [];
        diagnostic = null;
        if (maximumPayloadBytes is < 0 or > NativeMeshDataReader.MaximumMetadataBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        token.ThrowIfCancellationRequested();
        if (payload.Length > maximumPayloadBytes)
        {
            diagnostic = "NativeMeshBlendShapeLimit";
            return false;
        }
        if (source.Length != payload.Length || source.Offset < 0 ||
            source.Offset > long.MaxValue - payload.Length || offset < 0 || offset > payload.Length ||
            groupCount < 0 || groupCount > MaximumGroups)
        {
            diagnostic = "NativeMeshBlendShapeSourceMismatch";
            return false;
        }

        try
        {
            var sectionSource = new DecodedExtent(source.Container,
                checked(source.Offset + offset), payload.Length - offset);
            var reader = new NativeMeshByteReader(payload[offset..], sectionSource);
            reader.Tag("BLSHAPES"u8, 3);
            var minimumGroupBytes = checked((long)groupCount * sizeof(uint));
            EnsureRemaining(reader, minimumGroupBytes);
            long retainedMetadataBytes = ReserveMetadata(0, groupCount, GroupMetadataAllowanceBytes);
            var groups = new NativeMeshBlendShapeGroup[groupCount];
            var extents = new DecodedExtent[groupCount];
            long totalEntries = 0;
            long totalStrings = 0;

            for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
            {
                token.ThrowIfCancellationRequested();
                var groupStart = reader.Position;
                var countStart = reader.Position;
                var entryCount = (uint)reader.Count(MaximumEntries);
                var countExtent = reader.Extent(countStart, reader.Position - countStart);
                extents[groupIndex] = countExtent;
                totalEntries = checked(totalEntries + entryCount);
                if (totalEntries > MaximumEntries)
                    throw new InvalidDataException("NativeMeshBlendShapeEntryLimit");
                retainedMetadataBytes = ReserveMetadata(retainedMetadataBytes, entryCount,
                    EntryMetadataAllowanceBytes);
                EnsureMinimumBytes(entryCount, 21, reader.Position, payload.Length - offset);

                var entries = new NativeMeshBlendShapeEntry[checked((int)entryCount)];
                for (var entryIndex = 0; entryIndex < entries.Length; entryIndex++)
                {
                    token.ThrowIfCancellationRequested();
                    entries[entryIndex] = ReadEntry(payload, offset, reader, ref totalStrings,
                        ref retainedMetadataBytes, token);
                }
                groups[groupIndex] = new(entryCount, countExtent, entries,
                    reader.Extent(groupStart, reader.Position - groupStart));
            }

            token.ThrowIfCancellationRequested();
            var length = reader.Position;
            section = new(groups, payload.Slice(offset, length), reader.Extent(0, length));
            countExtents = extents;
            return true;
        }
        catch (InvalidDataException error)
        {
            diagnostic = error.Message;
            return false;
        }
        catch (OverflowException)
        {
            diagnostic = "NativeMeshBlendShapeSizeOverflow";
            return false;
        }
    }

    private static NativeMeshBlendShapeEntry ReadEntry(ReadOnlyMemory<byte> payload, int payloadOffset,
        NativeMeshByteReader reader, ref long totalStrings, ref long retainedMetadataBytes,
        CancellationToken token)
    {
        var entryStart = reader.Position;
        var words = new uint[5];
        for (var index = 0; index < words.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            words[index] = reader.Word();
        }
        var flags = reader.Byte();
        var headerLength = reader.Position - entryStart;
        var headerExtent = reader.Extent(entryStart, headerLength);
        var rawHeader = payload.Slice(checked(payloadOffset + entryStart), headerLength);

        var stringCount = BoundCount(words[0]);
        var tableColumnCount = BoundCount(words[1]);
        var floatTupleCount = BoundCount(words[2]);
        var wordArrayCount = BoundCount(words[3]);
        totalStrings = checked(totalStrings + stringCount);
        if (totalStrings > MaximumStringRecords)
            throw new InvalidDataException("NativeMeshBlendShapeStringRecordLimit");
        retainedMetadataBytes = ReserveMetadata(retainedMetadataBytes, stringCount,
            StringRecordMetadataAllowanceBytes);

        var tableElements = checked((ulong)stringCount * (uint)tableColumnCount);
        var tableBytes = CheckedBlockLength(tableElements, sizeof(ushort));
        var arrayBytes = CheckedBlockLength((ulong)wordArrayCount, sizeof(uint));
        var floatWordCount = checked((ulong)floatTupleCount * 3);
        var floatBytes = CheckedBlockLength(floatWordCount, sizeof(uint));
        var flag1Bytes = (flags & 1) != 0
            ? CheckedBlockLength((uint)floatTupleCount, sizeof(uint)) : 0;
        var flag2Bytes = (flags & 2) != 0
            ? CheckedBlockLength((uint)floatTupleCount, sizeof(uint)) : 0;
        var minimumStringBytes = checked((long)stringCount * 8);
        var fixedBytes = checked((long)tableBytes + arrayBytes * 2L + floatBytes + flag1Bytes + flag2Bytes);
        var minimumBytes = checked(minimumStringBytes + fixedBytes);
        if (minimumBytes > NativeMeshDataReader.MaximumMetadataBytes)
            throw new InvalidDataException("NativeMeshBlendShapeDataLimit");
        EnsureRemaining(reader, minimumBytes);

        var strings = new NativeMeshBlendShapeStringRecord[stringCount];
        long aggregateStringBytes = 0;
        for (var index = 0; index < strings.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var recordStart = reader.Position;
            var lengthStart = reader.Position;
            var rawLength = reader.Word();
            var signedLength = unchecked((int)rawLength);
            if (signedLength < 0) throw new InvalidDataException("NativeMeshBlendShapeStringLengthInvalid");
            if (signedLength > MaximumStringBytes)
                throw new InvalidDataException("NativeMeshBlendShapeStringLimit");
            var lengthExtent = reader.Extent(lengthStart, sizeof(uint));
            var bytesStart = reader.Position;
            var bytesExtent = reader.Skip(signedLength);
            var rawBytes = payload.Slice(checked(payloadOffset + bytesStart), signedLength);
            aggregateStringBytes = checked(aggregateStringBytes + signedLength);
            if (checked(aggregateStringBytes + fixedBytes) > NativeMeshDataReader.MaximumMetadataBytes)
                throw new InvalidDataException("NativeMeshBlendShapeDataLimit");
            token.ThrowIfCancellationRequested();
            var trailingStart = reader.Position;
            var trailingWord = reader.Word();
            var trailingExtent = reader.Extent(trailingStart, sizeof(uint));
            strings[index] = new(rawLength, rawBytes, lengthExtent, bytesExtent,
                trailingWord, trailingExtent, reader.Extent(recordStart, reader.Position - recordStart));
        }

        var arrayA = ReadRawBlock(payload, payloadOffset, reader, arrayBytes);
        var arrayB = ReadRawBlock(payload, payloadOffset, reader, arrayBytes);
        var table = ReadRawBlock(payload, payloadOffset, reader, tableBytes);
        var floatWords = ReadRawBlock(payload, payloadOffset, reader, floatBytes);
        var flag1 = ReadRawBlock(payload, payloadOffset, reader, flag1Bytes);
        var flag2 = ReadRawBlock(payload, payloadOffset, reader, flag2Bytes);
        return new(words, flags, rawHeader, headerExtent, strings, arrayA.Bytes, arrayA.Extent,
            arrayB.Bytes, arrayB.Extent, table.Bytes, table.Extent,
            floatWords.Bytes, floatWords.Extent, flag1.Bytes, flag1.Extent,
            flag2.Bytes, flag2.Extent, reader.Extent(entryStart, reader.Position - entryStart));
    }

    private static (ReadOnlyMemory<byte> Bytes, DecodedExtent Extent) ReadRawBlock(
        ReadOnlyMemory<byte> payload, int payloadOffset, NativeMeshByteReader reader, int length)
    {
        var start = reader.Position;
        var extent = reader.Skip(length);
        return (payload.Slice(checked(payloadOffset + start), length), extent);
    }

    private static int BoundCount(uint value)
    {
        if (value > MaximumEntries) throw new InvalidDataException("NativeMeshBlendShapeCountLimit");
        return checked((int)value);
    }

    private static int CheckedBlockLength(ulong count, int stride)
    {
        var bytes = checked(count * (uint)stride);
        if (bytes > NativeMeshDataReader.MaximumMetadataBytes || bytes > int.MaxValue)
            throw new InvalidDataException("NativeMeshBlendShapeDataLimit");
        return checked((int)bytes);
    }

    private static long ReserveMetadata(long currentBytes, long itemCount, int itemAllowance)
    {
        var total = checked(currentBytes + checked(itemCount * itemAllowance));
        if (total > MaximumRetainedMetadataBytes)
            throw new InvalidDataException("NativeMeshBlendShapeMetadataLimit");
        return total;
    }

    private static void EnsureMinimumBytes(uint count, int minimumPerEntry, int position, int sectionLength)
    {
        var minimum = checked((long)count * minimumPerEntry);
        if (minimum > NativeMeshDataReader.MaximumMetadataBytes)
            throw new InvalidDataException("NativeMeshBlendShapeEntryLimit");
        if (minimum > sectionLength - position)
            throw new InvalidDataException("NativeMeshTruncated");
    }

    private static void EnsureRemaining(NativeMeshByteReader reader, long required)
    {
        if (required < 0 || required > NativeMeshDataReader.MaximumMetadataBytes)
            throw new InvalidDataException("NativeMeshBlendShapeDataLimit");
        if (required > reader.Remaining) throw new InvalidDataException("NativeMeshTruncated");
    }
}
