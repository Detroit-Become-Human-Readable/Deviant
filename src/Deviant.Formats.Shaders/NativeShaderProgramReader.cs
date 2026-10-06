using System.Buffers.Binary;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Textures;

namespace Deviant.Formats.Shaders;

public sealed record NativeMaterialScalar(string Name, string Expression, float InitialValue, uint StoredWord, DecodedExtent Extent);
public sealed record NativeShaderProgramLayout(uint ScalarConstantCount, uint ArrayConstantCount,
    int AlignedScalarConstantCount, DecodedExtent CountExtent, DecodedExtent ProgramExtent)
{
    public IReadOnlyList<NativeMaterialScalar> ScalarConstants { get; init; } = [];
}

/// <summary>Reads the bounded native program tables preceding the scalar material constants.</summary>
public sealed class NativeShaderProgramReader
{
    public const string Version = "native-shader-program-1";
    public const int MaximumScalarConstants = 4096;
    public const int MaximumProgramBytes = 16 * 1024 * 1024;

    public DecodeResult<NativeShaderProgramLayout> Parse(NativeObjectFrame frame,
        TextureShaderSlotsDocument slots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(slots);
        cancellationToken.ThrowIfCancellationRequested();
        DecodeResult<NativeShaderProgramLayout> Result(NativeShaderProgramLayout? value, string? diagnostic) =>
            new(value, frame.Identity, slots.Revision, slots.OriginalPhysicalExtents,
                [new(nameof(NativeShaderProgramReader), Version, "native-program-tables; scalar-layout")],
                diagnostic is null ? [] : [diagnostic], [], new Dictionary<string, string>());
        if (frame.Identity != slots.Identity || frame.PayloadExtent.Container != slots.UnparsedSuffixExtent.Container ||
            slots.UnparsedSuffixExtent.Offset < frame.PayloadExtent.Offset ||
            slots.UnparsedSuffixExtent.Offset - frame.PayloadExtent.Offset > frame.RawPayload.Length ||
            slots.UnparsedSuffixExtent.Length != slots.UnparsedSuffixBytes.Length ||
            slots.UnparsedSuffixBytes.Length != frame.RawPayload.Length - (slots.UnparsedSuffixExtent.Offset - frame.PayloadExtent.Offset) ||
            !frame.RawPayload.Span[checked((int)(slots.UnparsedSuffixExtent.Offset - frame.PayloadExtent.Offset))..].SequenceEqual(slots.UnparsedSuffixBytes.Span))
            return Result(null, "MaterialProgramIdentityMismatch");
        // This profile stores primary programs inline and secondary program metadata in the object.
        if (frame.WrapperFlag != 1 || frame.WrapperCount != 1 || frame.RawHeader.Length != 52 || frame.RawHeader[17] != 0)
            return Result(null, "MaterialProgramWrapperUnsupported");
        var data = slots.UnparsedSuffixBytes.Span;
        var cursor = 0;
        try
        {
            var primaryCount = ReadWord(data, ref cursor);
            var secondaryCount = ReadWord(data, ref cursor);
            if (primaryCount > 92 || secondaryCount > 23) return Result(null, "MaterialProgramCountLimit");
            for (var list = 0; list < 2; list++)
            {
                var count = list == 0 ? primaryCount : secondaryCount;
                var selectors = new HashSet<(uint, uint)>();
                for (var record = 0; record < count; record++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var selector = ReadWord(data, ref cursor);
                    var variant = list == 0 ? ReadWord(data, ref cursor) : 0;
                    if (selector >= 23 || variant >= 4 || !selectors.Add((selector, variant)))
                        return Result(null, "MaterialProgramSelectorInvalid");
                    var blocks = ReadWord(data, ref cursor);
                    if (blocks > byte.MaxValue) return Result(null, "MaterialProgramBlockLimit");
                    if (blocks != 0) Skip(data, ref cursor, 8);
                    for (var block = 0; block < blocks; block++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var length = ReadWord(data, ref cursor);
                        if (length == 0) continue;
                        if (length > MaximumProgramBytes) return Result(null, "MaterialProgramSizeLimit");
                        Skip(data, ref cursor, 20); // Platform selector and two resource hash words.
                        if (list == 0) Skip(data, ref cursor, checked((int)length));
                    }
                }
            }
            var countOffset = cursor;
            var scalarCount = ReadWord(data, ref cursor);
            var arrayCount = ReadWord(data, ref cursor);
            if (scalarCount > MaximumScalarConstants) return Result(null, "MaterialConstantCountLimit");
            if (arrayCount != 0) return Result(null, "MaterialArrayConstantsUnsupported");
            var scalars = new List<NativeMaterialScalar>();
            for (var constant = 0; constant < scalarCount; constant++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = cursor;
                var names = new string[2];
                for (var text = 0; text < 2; text++)
                {
                    var length = ReadWord(data, ref cursor);
                    if (length > 1024 * 1024) return Result(null, "MaterialConstantNameLimit");
                    Require(data, cursor, checked((int)length));
                    names[text] = new UTF8Encoding(false, true).GetString(data.Slice(cursor, (int)length));
                    Skip(data, ref cursor, checked((int)length));
                }
                var initial = BitConverter.Int32BitsToSingle(unchecked((int)ReadWord(data, ref cursor)));
                var word = ReadWord(data, ref cursor);
                if (!float.IsFinite(initial)) return Result(null, "MaterialConstantValueInvalid");
                scalars.Add(new(names[0], names[1], initial, word,
                    new(slots.UnparsedSuffixExtent.Container, slots.UnparsedSuffixExtent.Offset + start, cursor - start)));
            }
            var aligned = checked(((int)scalarCount + 3) & ~3);
            var layout = new NativeShaderProgramLayout(scalarCount, arrayCount, aligned,
                new(slots.UnparsedSuffixExtent.Container, slots.UnparsedSuffixExtent.Offset + countOffset, 8),
                new(slots.UnparsedSuffixExtent.Container, slots.UnparsedSuffixExtent.Offset, countOffset)) { ScalarConstants = scalars };
            return Result(layout, null);
        }
        catch (InvalidDataException) { return Result(null, "MaterialProgramTruncated"); }
        catch (OverflowException) { return Result(null, "MaterialProgramRangeInvalid"); }
        catch (DecoderFallbackException) { return Result(null, "MaterialConstantTextInvalid"); }
    }

    private static uint ReadWord(ReadOnlySpan<byte> bytes, ref int cursor)
    {
        Require(bytes, cursor, 4);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[cursor..]);
        cursor += 4;
        return value;
    }
    private static void Skip(ReadOnlySpan<byte> bytes, ref int cursor, int count)
    { Require(bytes, cursor, count); cursor = checked(cursor + count); }
    private static void Require(ReadOnlySpan<byte> bytes, int cursor, int count)
    { if (count < 0 || cursor < 0 || cursor > bytes.Length - count) throw new InvalidDataException(); }
}
