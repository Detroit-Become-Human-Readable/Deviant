using System.Buffers.Binary;

namespace Deviant.Formats.Animation;

internal static class AnimationVersion14Reader
{
    internal const int MaximumTableRows = 65536;

    internal static AnimationVersion14Layout? Read(AnimationRecordSection section, List<string> diagnostics, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var cursor = 0;
        var tables = new List<AnimationScalarTable>();
        try
        {
            var variant = ReadU32();
            if (variant != 2)
            {
                diagnostics.Add($"ANIMDATA serialized variant {variant} has no decoded table layout.");
                return null;
            }
            // The nested header reader consumes these fields before the flags.
            var gateWord4 = ReadU32();
            var gateWord8 = ReadU32();
            var flags = ReadU32();
            var scalar16 = ReadU32();
            var primaryCountOffset = cursor;
            var primaryCount20 = ReadU32();
            var scalarBits24 = ReadU32();
            int? secondaryCountOffset = null;
            uint? secondaryCount28 = null;
            if ((flags & 0x400) != 0)
            {
                secondaryCountOffset = cursor;
                secondaryCount28 = ReadU32();
            }
            if (gateWord4 is 0 or uint.MaxValue || gateWord8 == 0)
            {
                var countOffset = cursor;
                var count = ReadU32();
                ReadTable("Gate", countOffset, count, (flags & 0x80) != 0 ? [2, 2, 4, 4] : [2, 2, 4]);
            }
            if ((flags & 0x4000) != 0)
            {
                var countOffset = cursor;
                var count = ReadU32();
                ReadTable("0x4000", countOffset, count, [2, 2, 4, 4]);
            }
            if ((flags & 0x40) != 0)
            {
                ReadTable("0x40", primaryCountOffset, primaryCount20, [4, 2, 2]);
                if (secondaryCount28 is uint secondaryCount)
                    ReadTable("0x400+0x40", secondaryCountOffset!.Value, secondaryCount, [4, 2, 2]);
            }
            if ((flags & 0x100) != 0)
            {
                var countOffset = cursor;
                var count = ReadU32();
                ReadTable("0x100", countOffset, count, [4, 4, 4]);
            }
            // Later native fields, blobs and codec blocks are outside this
            // table profile. This is their first encoded byte, not parser EOF.
            return new(variant, gateWord4, gateWord8, flags, scalar16, primaryCount20, scalarBits24,
                secondaryCount28, tables.AsReadOnly(), checked(section.BodyOffset + cursor), section.BodyLength - cursor);
        }
        catch (InvalidDataException ex)
        {
            diagnostics.Add(ex.Message);
            return null;
        }

        uint ReadU32()
        {
            Require(4);
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(section.Body.Span[cursor..]);
            cursor += 4;
            return bits;
        }

        void Require(long length)
        {
            if (length < 0 || length > section.Body.Length - cursor)
                throw new InvalidDataException("ANIMDATA version 14 table bytes exceed the record body.");
        }

        void ReadTable(string kind, int countOffset, uint count, int[] widths)
        {
            if (count > MaximumTableRows)
                throw new InvalidDataException("ANIMDATA version 14 table exceeds the configured row limit.");
            var stride = widths.Sum();
            Require((long)count * stride);
            var rowsOffset = cursor;
            var rows = new AnimationScalarRow[checked((int)count)];
            for (var ordinal = 0; ordinal < rows.Length; ordinal++)
            {
                if ((ordinal & 255) == 0) token.ThrowIfCancellationRequested();
                var rowStart = cursor;
                var fields = new AnimationScalarField[widths.Length];
                for (var field = 0; field < widths.Length; field++)
                {
                    var width = widths[field];
                    var bits = width == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(section.Body.Span[cursor..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(section.Body.Span[cursor..]);
                    fields[field] = new(cursor - rowStart, width, bits);
                    cursor += width;
                }
                rows[ordinal] = new(ordinal, checked(section.BodyOffset + rowStart), Array.AsReadOnly(fields));
            }
            tables.Add(new(kind, checked(section.BodyOffset + countOffset), checked(section.BodyOffset + rowsOffset),
                count, stride, Array.AsReadOnly(rows)));
        }
    }
}
