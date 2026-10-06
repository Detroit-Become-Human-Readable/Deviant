using System.Buffers.Binary;

namespace Deviant.Formats.Animation;

internal static class AnimationVersion14Blocks
{
    internal static AnimationVersion14Layout Read(AnimationRecordSection section,
        AnimationVersion14Layout layout, CancellationToken token)
    {
        var cursor = checked((int)(layout.RemainingResourceOffset - section.BodyOffset));
        var blocks = new List<AnimationDataBlock>();
        var diagnostics = new List<string>();
        try
        {
            token.ThrowIfCancellationRequested();
            if (layout.GateWord4 is 0 or uint.MaxValue || layout.GateWord8 == 0)
                return Finish("Post-table inspection stopped at the nested gate state.");
            if ((layout.Flags & 0x9000) != 0)
                return Finish("Post-table inspection stopped before the variable 0x9000 region.");
            if ((layout.Flags & 0x2000) != 0)
            {
                Require(5);
                Add("0x2000", 5, [new(0, 1, section.Body.Span[cursor]), new(1, 4, U32(cursor + 1))]);
            }
            if ((layout.Flags & 2) != 0)
                Add("0x2", (long)layout.PrimaryCount20 + (layout.SecondaryCount28 ?? 0), []);
            if ((layout.Flags & 0x200) != 0)
            {
                var count = U32(cursor);
                Add("0x200", checked(4L + 16L * count), [new(0, 4, count)]);
            }
            var word = U32(cursor);
            Add("U32", 4, [new(0, 4, word)]);
            Require(1);
            var state = section.Body.Span[cursor];
            Add("U8", 1, [new(0, 1, state)]);
            if (state == 1)
                return Finish("Post-table inspection stopped at the conditional skip state.");
            if (word != 1)
            {
                Add("1024-A", 1024, []);
                Add("1024-B", 1024, []);
                // The native helper byte-swaps already-populated buffers only;
                // it does not consume further serialized bytes.
            }
            var skip = U32(cursor);
            Add("U32+skip", checked(4L + skip), [new(0, 4, skip)]);
            return Finish();
        }
        catch (InvalidDataException ex)
        {
            return Finish(ex.Message);
        }

        AnimationVersion14Layout Finish(string? diagnostic = null)
        {
            if (diagnostic is not null) diagnostics.Add(diagnostic);
            return layout with
            {
                PostTableBlocks = blocks.AsReadOnly(),
                PostTableRemainderResourceOffset = checked(section.BodyOffset + cursor),
                PostTableRemainderLength = section.Body.Length - cursor,
                PostTableDiagnostics = diagnostics.AsReadOnly()
            };
        }

        uint U32(int offset)
        {
            token.ThrowIfCancellationRequested();
            if (offset < 0 || offset > section.Body.Length - 4)
                throw new InvalidDataException("ANIMDATA post-table scalar exceeds the record body.");
            return BinaryPrimitives.ReadUInt32LittleEndian(section.Body.Span[offset..]);
        }

        void Require(long length)
        {
            token.ThrowIfCancellationRequested();
            if (length < 0 || length > section.Body.Length - cursor)
                throw new InvalidDataException("ANIMDATA post-table region exceeds the record body.");
        }

        void Add(string kind, long length, AnimationBlockField[] fields)
        {
            Require(length);
            blocks.Add(new(kind, checked(section.BodyOffset + cursor), length, Array.AsReadOnly(fields)));
            cursor = checked(cursor + (int)length);
        }
    }
}
