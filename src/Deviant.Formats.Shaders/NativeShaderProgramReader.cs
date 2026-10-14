using System.Buffers.Binary;
using System.Text;
using Deviant.Assets;
using Deviant.Formats.Textures;

namespace Deviant.Formats.Shaders;

public sealed record NativeMaterialScalar(string Name, string Expression, float InitialValue, uint StoredWord, DecodedExtent Extent)
{
    public uint Float32Bits { get; init; } = BitConverter.SingleToUInt32Bits(InitialValue);
    public DecodedExtent? TextField1Extent { get; init; }
    public DecodedExtent? TextField2Extent { get; init; }
    public DecodedExtent? Float32Extent { get; init; }
    public DecodedExtent? StoredWordExtent { get; init; }
}
public sealed record NativeShaderProgramLayout(uint ScalarConstantCount, uint ArrayConstantCount,
    int AlignedScalarConstantCount, DecodedExtent CountExtent, DecodedExtent ProgramExtent)
{
    public IReadOnlyList<NativeMaterialScalar> ScalarConstants { get; init; } = [];
    public IReadOnlyList<NativeMaterialArrayConstant> ArrayConstants { get; init; } = [];
    public DecodedExtent? ConstantsExtent { get; init; }
    public long ConstantsEndpointOffset { get; init; }
    public DecodedExtent? TrailingExtent { get; init; }
    public long AccountedMetadataBytes { get; init; }
}

/// <summary>One array-list record; text and scalar semantics remain unassigned.</summary>
public sealed record NativeMaterialArrayConstant(string TextField1, string TextField2, uint Float32Bits,
    uint StoredWord, uint ElementCount, IReadOnlyList<uint> ElementWords, DecodedExtent Extent,
    DecodedExtent TextField1Extent, DecodedExtent TextField2Extent, DecodedExtent Float32Extent,
    DecodedExtent StoredWordExtent, DecodedExtent ElementCountExtent, DecodedExtent ElementWordsExtent);

/// <summary>Reads bounded native program tables and stored constant records.</summary>
public sealed class NativeShaderProgramReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public const string Version = "native-shader-program-2";
    public const string StoredLayoutVersion = "native-shader-program-2-stored-layout";
    public const int MaximumScalarConstants = 4096;
    public const int MaximumProgramBytes = 16 * 1024 * 1024;
    public const int MaximumArrayConstants = 4096;
    public const int MaximumArrayElements = 1_048_576;
    public const long MaximumAggregateTextBytes = 8L * 1024 * 1024;
    public const long MaximumStoredMetadataBytes = 64L * 1024 * 1024;

    public DecodeResult<NativeShaderProgramLayout> Parse(NativeObjectFrame frame,
        TextureShaderSlotsDocument slots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(slots);
        cancellationToken.ThrowIfCancellationRequested();
        DecodeResult<NativeShaderProgramLayout> Result(NativeShaderProgramLayout? value, string? diagnostic) =>
            new(value, frame.Identity, slots.Revision, slots.OriginalPhysicalExtents,
                [new(nameof(NativeShaderProgramReader), Version, "native-program-tables; scalar-layout; raw-array-retention")],
                diagnostic is null ? [] : [diagnostic], [], new Dictionary<string, string>());
        var stored = ReadStoredLayout(frame, slots, cancellationToken);
        if (stored.Value is not { } layout)
        {
            var diagnostic = stored.Diagnostics.FirstOrDefault() ?? "MaterialProgramLayoutUnavailable";
            if (diagnostic == "MaterialStoredTextFieldLimit") diagnostic = "MaterialConstantNameLimit";
            return Result(null, diagnostic);
        }
        foreach (var scalar in layout.ScalarConstants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!float.IsFinite(scalar.InitialValue)) return Result(null, "MaterialConstantValueInvalid");
        }
        return Result(layout, null);
    }

    /// <summary>Reads stored scalar and array records without assigning storage or GPU meanings to raw fields.</summary>
    public DecodeResult<NativeShaderProgramLayout> ReadStoredLayout(NativeObjectFrame frame,
        TextureShaderSlotsDocument slots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(slots);
        cancellationToken.ThrowIfCancellationRequested();
        DecodeResult<NativeShaderProgramLayout> Result(NativeShaderProgramLayout? value, string? diagnostic) =>
            new(value, frame.Identity, slots.Revision, slots.OriginalPhysicalExtents,
                [new(nameof(NativeShaderProgramReader), StoredLayoutVersion, "ordered-list-records; raw-fields; exact-extents")],
                diagnostic is null ? [] : [diagnostic], [], new Dictionary<string, string>());
        if (!TryScanStoredLayout(frame, slots, materialize: true, finiteScalars: false,
                out var layout, out _, out var diagnostic, cancellationToken))
            return Result(null, diagnostic);
        return Result(layout, null);
    }

    /// <summary>
    /// Measures the material-record allocation estimate without creating strings or record arrays.
    /// On invalid data, the byte estimate still covers the valid prefix the matching parser can materialize.
    /// </summary>
    public bool TryEstimateWorkingBytes(NativeObjectFrame frame, TextureShaderSlotsDocument slots,
        out long estimatedBytes, out string? diagnostic, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(slots);
        cancellationToken.ThrowIfCancellationRequested();
        return TryScanStoredLayout(frame, slots, materialize: false, finiteScalars: true,
            out _, out estimatedBytes, out diagnostic, cancellationToken);
    }

    private static bool TryScanStoredLayout(NativeObjectFrame frame, TextureShaderSlotsDocument slots,
        bool materialize, bool finiteScalars, out NativeShaderProgramLayout? layout,
        out long estimatedBytes, out string? diagnostic, CancellationToken token)
    {
        layout = null;
        estimatedBytes = 0;
        diagnostic = null;
        if (!TryReadProgramPrefix(frame, slots, token, out var prefix, out diagnostic)) return false;
        if (slots.Revision.IndexContentId != frame.Identity.IndexContentId ||
            string.IsNullOrWhiteSpace(slots.Revision.PhysicalSourceStamp) || slots.Revision.PhysicalSourceStamp.Length > 4096)
        { diagnostic = "MaterialProgramRevisionMismatch"; return false; }
        if (prefix.ScalarCount > MaximumScalarConstants)
        { diagnostic = "MaterialConstantCountLimit"; return false; }
        if (prefix.ArrayCount > MaximumArrayConstants)
        { diagnostic = "MaterialArrayConstantCountLimit"; return false; }

        var estimate = StoredMetadataBaseBytes + (long)prefix.ScalarCount * StoredScalarMetadataBytes +
            (long)prefix.ArrayCount * StoredArrayMetadataBytes;
        estimatedBytes = estimate;
        if (estimate > MaximumStoredMetadataBytes)
        { diagnostic = "MaterialStoredMetadataLimit"; return false; }
        var data = slots.UnparsedSuffixBytes.Span;
        var cursor = prefix.Cursor;
        long aggregateTextBytes = 0;
        long aggregateElementCount = 0;
        var finiteScalarValues = true;
        try
        {
            var scalars = materialize ? new List<NativeMaterialScalar>(checked((int)prefix.ScalarCount)) : null;
            for (var constant = 0; constant < prefix.ScalarCount; constant++)
            {
                token.ThrowIfCancellationRequested();
                var start = cursor;
                if (!TryReadTextField(data, ref cursor, slots.UnparsedSuffixExtent, materialize,
                        prefix.ScalarCount, prefix.ArrayCount, aggregateElementCount,
                        ref aggregateTextBytes, ref estimate, out var name, out var nameExtent, out diagnostic, token))
                { estimatedBytes = estimate; return false; }
                if (!TryReadTextField(data, ref cursor, slots.UnparsedSuffixExtent, materialize,
                        prefix.ScalarCount, prefix.ArrayCount, aggregateElementCount,
                        ref aggregateTextBytes, ref estimate, out var expression, out var expressionExtent, out diagnostic, token))
                { estimatedBytes = estimate; return false; }
                var valueOffset = cursor;
                var valueBits = ReadWord(data, ref cursor);
                var value = BitConverter.UInt32BitsToSingle(valueBits);
                if (!float.IsFinite(value)) finiteScalarValues = false;
                var storedWordOffset = cursor;
                var storedWord = ReadWord(data, ref cursor);
                estimate = EstimateMetadata(prefix.ScalarCount, prefix.ArrayCount,
                    aggregateTextBytes, aggregateElementCount);
                estimatedBytes = estimate;
                EnsureStoredMetadata(estimate);
                if (materialize)
                    scalars!.Add(new(name!, expression!, value, storedWord,
                        At(slots.UnparsedSuffixExtent, start, cursor - start))
                    {
                        Float32Bits = valueBits,
                        TextField1Extent = nameExtent,
                        TextField2Extent = expressionExtent,
                        Float32Extent = At(slots.UnparsedSuffixExtent, valueOffset, 4),
                        StoredWordExtent = At(slots.UnparsedSuffixExtent, storedWordOffset, 4)
                    });
            }

            var arrays = materialize ? new List<NativeMaterialArrayConstant>(checked((int)prefix.ArrayCount)) : null;
            for (var array = 0; array < prefix.ArrayCount; array++)
            {
                token.ThrowIfCancellationRequested();
                var start = cursor;
                if (!TryReadTextField(data, ref cursor, slots.UnparsedSuffixExtent, materialize,
                        prefix.ScalarCount, prefix.ArrayCount, aggregateElementCount,
                        ref aggregateTextBytes, ref estimate, out var text1, out var text1Extent, out diagnostic, token))
                { estimatedBytes = estimate; return false; }
                if (!TryReadTextField(data, ref cursor, slots.UnparsedSuffixExtent, materialize,
                        prefix.ScalarCount, prefix.ArrayCount, aggregateElementCount,
                        ref aggregateTextBytes, ref estimate, out var text2, out var text2Extent, out diagnostic, token))
                { estimatedBytes = estimate; return false; }
                var floatOffset = cursor;
                var floatBits = ReadWord(data, ref cursor);
                var storedWordOffset = cursor;
                var storedWord = ReadWord(data, ref cursor);
                var countOffset = cursor;
                var elementCount = ReadWord(data, ref cursor);
                if (elementCount > MaximumArrayElements || aggregateElementCount > MaximumArrayElements - elementCount)
                { diagnostic = "MaterialArrayElementCountLimit"; estimatedBytes = estimate; return false; }
                aggregateElementCount = checked(aggregateElementCount + elementCount);
                estimate = EstimateMetadata(prefix.ScalarCount, prefix.ArrayCount,
                    aggregateTextBytes, aggregateElementCount);
                estimatedBytes = estimate;
                EnsureStoredMetadata(estimate);
                var wordsStart = cursor;
                var wordsLength = checked((int)(elementCount * sizeof(uint)));
                Require(data, cursor, wordsLength);
                uint[]? words = null;
                if (materialize)
                {
                    words = new uint[checked((int)elementCount)];
                    for (var word = 0; word < words.Length; word++)
                    {
                        if ((word & 1023) == 0) token.ThrowIfCancellationRequested();
                        words[word] = ReadWord(data, ref cursor);
                    }
                }
                else
                {
                    token.ThrowIfCancellationRequested();
                    Skip(data, ref cursor, wordsLength);
                }
                if (materialize)
                    arrays!.Add(new(text1!, text2!, floatBits, storedWord, elementCount,
                        Array.AsReadOnly(words!), At(slots.UnparsedSuffixExtent, start, cursor - start),
                        text1Extent!, text2Extent!, At(slots.UnparsedSuffixExtent, floatOffset, 4),
                        At(slots.UnparsedSuffixExtent, storedWordOffset, 4),
                        At(slots.UnparsedSuffixExtent, countOffset, 4),
                        At(slots.UnparsedSuffixExtent, wordsStart, wordsLength)));
            }
            token.ThrowIfCancellationRequested();
            if (finiteScalars && !finiteScalarValues)
            { diagnostic = "MaterialConstantValueInvalid"; estimatedBytes = estimate; return false; }
            if (materialize)
            {
                var constantsStart = checked(prefix.CountOffset + 8);
                var constantsEnd = cursor;
                layout = new NativeShaderProgramLayout(prefix.ScalarCount, prefix.ArrayCount,
                    checked(((int)prefix.ScalarCount + 3) & ~3),
                    At(slots.UnparsedSuffixExtent, prefix.CountOffset, 8),
                    At(slots.UnparsedSuffixExtent, 0, prefix.CountOffset))
                {
                    ConstantsExtent = At(slots.UnparsedSuffixExtent, constantsStart, constantsEnd - constantsStart),
                    ConstantsEndpointOffset = checked(slots.UnparsedSuffixExtent.Offset + constantsEnd),
                    TrailingExtent = At(slots.UnparsedSuffixExtent, constantsEnd, data.Length - constantsEnd),
                    AccountedMetadataBytes = estimate,
                    ScalarConstants = Array.AsReadOnly(scalars!.ToArray()),
                    ArrayConstants = Array.AsReadOnly(arrays!.ToArray())
                };
            }
            estimatedBytes = estimate;
            return true;
        }
        catch (NativeStoredLayoutLimitException error)
        { diagnostic = error.Code; estimatedBytes = estimate; return false; }
        catch (InvalidDataException)
        { diagnostic = "MaterialProgramTruncated"; estimatedBytes = estimate; return false; }
        catch (OverflowException)
        { diagnostic = "MaterialProgramRangeInvalid"; estimatedBytes = estimate; return false; }
        catch (DecoderFallbackException)
        { diagnostic = "MaterialConstantTextInvalid"; estimatedBytes = estimate; return false; }
    }

    private static bool TryReadTextField(ReadOnlySpan<byte> data, ref int cursor, DecodedExtent suffix,
        bool materialize, uint scalarCount, uint arrayCount, long aggregateElementCount,
        ref long aggregateTextBytes, ref long estimate, out string? value, out DecodedExtent? fieldExtent,
        out string? diagnostic, CancellationToken token)
    {
        value = null; fieldExtent = null; diagnostic = null;
        token.ThrowIfCancellationRequested();
        var start = cursor;
        var length = ReadWord(data, ref cursor);
        if (length > 1024 * 1024)
        { diagnostic = "MaterialStoredTextFieldLimit"; return false; }
        var nextTextBytes = checked(aggregateTextBytes + length);
        if (nextTextBytes > MaximumAggregateTextBytes)
        { diagnostic = "MaterialStoredTextLimit"; return false; }
        var nextEstimate = EstimateMetadata(scalarCount, arrayCount, nextTextBytes, aggregateElementCount);
        if (nextEstimate > MaximumStoredMetadataBytes)
        { diagnostic = "MaterialStoredMetadataLimit"; return false; }
        Require(data, cursor, checked((int)length));
        var bytes = data.Slice(cursor, checked((int)length));
        if (!materialize)
        {
            _ = StrictUtf8.GetCharCount(bytes);
        }
        else value = StrictUtf8.GetString(bytes);
        aggregateTextBytes = nextTextBytes;
        estimate = nextEstimate;
        cursor = checked(cursor + (int)length);
        if (materialize) fieldExtent = At(suffix, start, cursor - start);
        return true;
    }

    private static long EstimateMetadata(uint scalarCount, uint arrayCount,
        long aggregateTextBytes, long aggregateElementCount) =>
        checked(StoredMetadataBaseBytes + (long)scalarCount * StoredScalarMetadataBytes +
            (long)arrayCount * StoredArrayMetadataBytes + aggregateTextBytes * sizeof(char) +
            aggregateElementCount * sizeof(uint));

    private static bool TryReadProgramPrefix(NativeObjectFrame frame, TextureShaderSlotsDocument slots,
        CancellationToken cancellationToken, out ProgramPrefix prefix, out string? diagnostic)
    {
        prefix = default;
        diagnostic = null;
        if (frame.Identity != slots.Identity || frame.PayloadExtent.Container != slots.UnparsedSuffixExtent.Container ||
            slots.UnparsedSuffixExtent.Offset < frame.PayloadExtent.Offset ||
            slots.UnparsedSuffixExtent.Offset - frame.PayloadExtent.Offset > frame.RawPayload.Length ||
            slots.UnparsedSuffixExtent.Length != slots.UnparsedSuffixBytes.Length ||
            slots.UnparsedSuffixBytes.Length != frame.RawPayload.Length - (slots.UnparsedSuffixExtent.Offset - frame.PayloadExtent.Offset) ||
            !frame.RawPayload.Span[checked((int)(slots.UnparsedSuffixExtent.Offset - frame.PayloadExtent.Offset))..].SequenceEqual(slots.UnparsedSuffixBytes.Span))
        { diagnostic = "MaterialProgramIdentityMismatch"; return false; }
        // This profile stores primary programs inline and secondary program metadata in the object.
        if (frame.WrapperFlag != 1 || frame.WrapperCount != 1 || frame.RawHeader.Length != 52 || frame.RawHeader[17] != 0)
        { diagnostic = "MaterialProgramWrapperUnsupported"; return false; }
        var data = slots.UnparsedSuffixBytes.Span;
        var cursor = 0;
        try
        {
            var primaryCount = ReadWord(data, ref cursor);
            var secondaryCount = ReadWord(data, ref cursor);
            if (primaryCount > 92 || secondaryCount > 23)
            { diagnostic = "MaterialProgramCountLimit"; return false; }
            Span<uint> selectors = stackalloc uint[184];
            for (var list = 0; list < 2; list++)
            {
                var count = list == 0 ? primaryCount : secondaryCount;
                var used = 0;
                for (var record = 0; record < count; record++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var selector = ReadWord(data, ref cursor);
                    var variant = list == 0 ? ReadWord(data, ref cursor) : 0;
                    if (selector >= 23 || variant >= 4)
                    { diagnostic = "MaterialProgramSelectorInvalid"; return false; }
                    for (var previous = 0; previous < used; previous++)
                        if (selectors[previous * 2] == selector && selectors[previous * 2 + 1] == variant)
                        { diagnostic = "MaterialProgramSelectorInvalid"; return false; }
                    selectors[used * 2] = selector;
                    selectors[used * 2 + 1] = variant;
                    used++;
                    var blocks = ReadWord(data, ref cursor);
                    if (blocks > byte.MaxValue) { diagnostic = "MaterialProgramBlockLimit"; return false; }
                    if (blocks != 0) Skip(data, ref cursor, 8);
                    for (var block = 0; block < blocks; block++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var length = ReadWord(data, ref cursor);
                        if (length == 0) continue;
                        if (length > MaximumProgramBytes) { diagnostic = "MaterialProgramSizeLimit"; return false; }
                        Skip(data, ref cursor, 20);
                        if (list == 0) Skip(data, ref cursor, checked((int)length));
                    }
                }
            }
            var countOffset = cursor;
            var scalarCount = ReadWord(data, ref cursor);
            var arrayCount = ReadWord(data, ref cursor);
            prefix = new(scalarCount, arrayCount, cursor, countOffset);
            return true;
        }
        catch (InvalidDataException) { diagnostic = "MaterialProgramTruncated"; return false; }
        catch (OverflowException) { diagnostic = "MaterialProgramRangeInvalid"; return false; }
    }

    private static void EnsureStoredMetadata(long estimate)
    {
        if (estimate > MaximumStoredMetadataBytes)
            throw new NativeStoredLayoutLimitException("MaterialStoredMetadataLimit");
    }

    private static DecodedExtent At(DecodedExtent suffix, int offset, int length) =>
        new(suffix.Container, checked(suffix.Offset + offset), length);

    private readonly record struct ProgramPrefix(uint ScalarCount, uint ArrayCount, int Cursor, int CountOffset);
    private sealed class NativeStoredLayoutLimitException(string code) : Exception(code)
    { public string Code { get; } = code; }

    private const long StoredMetadataBaseBytes = 4096;
    private const long StoredScalarMetadataBytes = 256;
    private const long StoredArrayMetadataBytes = 320;

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
