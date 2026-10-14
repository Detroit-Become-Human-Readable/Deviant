using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;

namespace Deviant.Formats.Narrative;

/// <summary>Reads GMK_COMG v22 stored text and raw subobject fields. Reference lookup scope remains unclassified.</summary>
public sealed class GmkCommonGroupReader
{
    public const uint NativeType = 4241;
    public const uint ItemFactoryClassId = 4421;
    public const string Version = "gmk-common-group-v22-1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public DecodeResult<GmkCommonGroupDocument> Read(NativeObjectFrame frame, SourceRevision revision,
        IReadOnlyList<SourceExtent> sources, GmkCommonGroupLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(sources); cancellationToken.ThrowIfCancellationRequested();
        limits ??= new(); IReadOnlyList<SourceExtent> physical = [];
        var steps = new[] { new DecodeStep(nameof(GmkCommonGroupReader), Version, "GMK_COMG-v22;class4421-items;stored-text;unclassified-pairs") };
        var unknown = new Dictionary<string, string> { ["storedTextRuntimeRole"] = "Unknown", ["referenceLookupScope"] = "Unclassified", ["wordRoles"] = "Unknown" };
        DecodeResult<GmkCommonGroupDocument> Fail(string code) => new(null, frame.Identity, revision, physical, steps, [code], [], unknown);
        if (!ValidLimits(limits)) return Fail("GmkCommonGroupInvalidLimits");
        if (!ValidFrame(frame, revision, limits.MaximumMetadataCharacters)) return Fail("GmkCommonGroupSourceMismatch");
        if (frame.NativeType != NativeType) return Fail("GmkCommonGroupTypeUnsupported");
        if (frame.RawPayload.Length > limits.MaximumPayloadBytes) return Fail("GmkCommonGroupPayloadLimit");
        var sourceCount = sources.Count; cancellationToken.ThrowIfCancellationRequested();
        if (sourceCount < 0 || sourceCount > limits.MaximumPhysicalSources) return Fail("GmkCommonGroupPhysicalSourceLimit");
        try
        {
            var budget = new Budget(limits.MaximumRetainedBytes);
            budget.Add(checked(4096L + frame.RawPayload.Length * 2L + sourceCount * 16L +
                (frame.Identity.IndexContentId.Length + frame.Identity.MemberPath!.Length + revision.PhysicalSourceStamp.Length) * 2L));
            var sourceArray = new SourceExtent[sourceCount];
            for (var i = 0; i < sourceCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested(); var source = sources[i];
                if (source is null || !ValidSource(source, limits.MaximumMetadataCharacters)) return Fail("GmkCommonGroupPhysicalSourceInvalid");
                budget.Add(checked(512L + source.RelativeSource.Length * 2L + (source.PayloadSha256?.Length ?? 0) * 2L)); sourceArray[i] = source;
            }
            physical = Array.AsReadOnly(sourceArray);
            var raw = new byte[frame.RawPayload.Length];
            for (var at = 0; at < raw.Length; at += 128 * 1024)
            { cancellationToken.ThrowIfCancellationRequested(); frame.RawPayload.Slice(at, Math.Min(128 * 1024, raw.Length - at)).CopyTo(raw.AsMemory(at)); }
            var cursor = new Cursor(raw, frame.PayloadExtent, cancellationToken);
            if (frame.ClassCount != 1 || cursor.Word() != 1 || !cursor.Take(8).SequenceEqual("GMK_COMG"u8)) return Fail("GmkCommonGroupClassUnsupported");
            var version = cursor.Word(); if (version != 22) return Fail("GmkCommonGroupVersionUnsupported");
            var word16 = cursor.Word(); var count = cursor.Count(limits.MaximumItems, "GmkCommonGroupItemLimit");
            if (count > cursor.Remaining / 17) return Fail("GmkCommonGroupItemBounds");
            budget.Add(checked(64L + count * 16L)); var items = new GmkCommonGroupItem[count]; var totalWords = 0; var invalidUtf8 = 0;
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget.Add(checked(1024L + (frame.Identity.MemberPath!.Length + 32) * 2L));
                var identity = frame.Identity with { MemberPath = frame.Identity.MemberPath + "/group-item/" + i.ToString(CultureInfo.InvariantCulture) };
                var start = cursor.Position; var name = ReadText(ref cursor, limits, budget, ref invalidUtf8);
                var text = ReadText(ref cursor, limits, budget, ref invalidUtf8);
                var wordCountAt = cursor.Position; var wordCount = cursor.Count(limits.MaximumWords, "GmkCommonGroupWordLimit");
                if (wordCount > limits.MaximumWords - totalWords) return Fail("GmkCommonGroupWordLimit");
                totalWords += wordCount;
                if (cursor.Remaining < 5 || wordCount > (cursor.Remaining - 5) / 4) return Fail("GmkCommonGroupWordBounds");
                budget.Add(checked(64L + wordCount * 128L)); var words = new GmkGroupWord[wordCount];
                for (var wi = 0; wi < wordCount; wi++) { var at = cursor.Position; words[wi] = new(wi, cursor.Word(), cursor.At(at, 4)); }
                var flagAt = cursor.Position; var flag = cursor.Take(1)[0]; GmkUnclassifiedReferencePair? pair = null;
                if (flag == 1)
                {
                    budget.Add(256); var pairAt = cursor.Position; var pairBytes = cursor.Memory(8);
                    var word0 = BinaryPrimitives.ReadUInt32LittleEndian(pairBytes.Span); var word1 = BinaryPrimitives.ReadUInt32LittleEndian(pairBytes.Span[4..]);
                    var normalized0 = word0 == uint.MaxValue ? 0U : word0; var normalized1 = word1 == uint.MaxValue ? 0U : word1;
                    pair = new(word0, word1, normalized0, normalized1, unchecked((int)normalized0) > 0 && unchecked((int)normalized1) > 0,
                        GmkReferenceLookupClassification.Unclassified, cursor.At(pairAt, 8), pairBytes);
                }
                var finalAt = cursor.Position; var finalWord = cursor.Word();
                items[i] = new(identity, ItemFactoryClassId, name, Crc32(name.RawBytes.Span, cancellationToken), text,
                    Array.AsReadOnly(words), flag, pair, finalWord, cursor.At(start, cursor.Position - start), cursor.At(wordCountAt, 4), cursor.At(flagAt, 1), cursor.At(finalAt, 4));
            }
            if (cursor.Remaining != 0) return Fail("GmkCommonGroupTrailingBytes");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var at = 0; at < raw.Length; at += 128 * 1024)
            { cancellationToken.ThrowIfCancellationRequested(); hash.AppendData(raw.AsSpan(at, Math.Min(128 * 1024, raw.Length - at))); }
            var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(); cancellationToken.ThrowIfCancellationRequested();
            steps[0] = steps[0] with { PayloadSha256 = digest };
            var diagnostics = invalidUtf8 == 0 ? Array.Empty<string>() : new[] { "GmkCommonGroupInvalidUtf8: " + invalidUtf8 };
            var document = new GmkCommonGroupDocument(frame.Identity, revision, frame.NativeId, version, word16, frame.PayloadExtent,
                cursor.At(0, 16), cursor.At(16, 4), cursor.At(20, 4), Array.AsReadOnly(items), physical, raw);
            cancellationToken.ThrowIfCancellationRequested();
            return new(document, frame.Identity, revision, physical, steps, diagnostics, [], unknown);
        }
        catch (ReadFailure failure) { return Fail(failure.Code); }
        catch (OverflowException) { return Fail("GmkCommonGroupIntegerOverflow"); }
    }

    private static GmkStoredText ReadText(ref Cursor c, GmkCommonGroupLimits limits, Budget budget, ref int invalidUtf8)
    {
        var lengthAt = c.Position; var n = c.Count(limits.MaximumStringBytes, "GmkCommonGroupStringLimit"); var at = c.Position; var bytes = c.Memory(n);
        budget.Add(checked(512L + n * 2L)); string text; var valid = true;
        try { text = StrictUtf8.GetString(bytes.Span); }
        catch (DecoderFallbackException) { valid = false; invalidUtf8++; text = Encoding.UTF8.GetString(bytes.Span); }
        return new(text, valid, c.At(lengthAt, 4), c.At(at, n), bytes);
    }
    private static uint Crc32(ReadOnlySpan<byte> data, CancellationToken token)
    {
        uint crc = uint.MaxValue;
        for (var i = 0; i < data.Length; i++)
        { if ((i & 255) == 0) token.ThrowIfCancellationRequested(); crc ^= data[i]; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320U & unchecked(0U - (crc & 1))); }
        return ~crc;
    }
    private static bool ValidLimits(GmkCommonGroupLimits l) => l.MaximumPayloadBytes is >= 24 and <= 128 * 1024 * 1024 &&
        l.MaximumItems is >= 0 and <= 1_000_000 && l.MaximumWords is >= 0 and <= 1_000_000 &&
        l.MaximumStringBytes is >= 0 and <= 16 * 1024 * 1024 && l.MaximumPhysicalSources is >= 0 and <= 16_384 &&
        l.MaximumMetadataCharacters is > 0 and <= 16_384 && l.MaximumRetainedBytes is >= 0 and <= 512L * 1024 * 1024;
    private static bool Token(string? text, int max) => !string.IsNullOrWhiteSpace(text) && text.Length <= max && !text.Any(char.IsControl);
    private static bool Relative(string? text, int max)
    {
        if (!Token(text, max) || text![0] is '/' or '\\' || text.Contains(':')) return false;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        { if (i < text.Length && text[i] is not ('/' or '\\')) continue; var part = text.AsSpan(start, i - start); if (part.IsEmpty || part.SequenceEqual(".".AsSpan()) || part.SequenceEqual("..".AsSpan())) return false; start = i + 1; }
        return true;
    }
    private static bool Range(DecodedExtent? e) => e is not null && e.Offset >= 0 && e.Length >= 0 && e.Offset <= long.MaxValue - e.Length;
    private static bool ValidFrame(NativeObjectFrame f, SourceRevision r, int max)
    {
        if (f.Identity is null || f.PayloadExtent?.Container is null || f.RawHeader is null || !Token(f.Identity.IndexContentId, max) ||
            !Token(r.IndexContentId, max) || !Token(r.PhysicalSourceStamp, max) || !Relative(f.Identity.MemberPath, max) ||
            f.Identity.IndexContentId != r.IndexContentId || f.Identity.Ordinal < 0 || f.DirectoryIndex < 0 ||
            f.PayloadExtent.Container != new ResourceIdentity(f.Identity.IndexContentId, f.Identity.Ordinal) ||
            !Range(f.PayloadExtent) || f.PayloadExtent.Length != f.RawPayload.Length || !Range(f.HeaderExtent) ||
            !Range(f.DirectoryEntryExtent) || f.HeaderExtent.Container != f.PayloadExtent.Container ||
            f.DirectoryEntryExtent.Container != f.PayloadExtent.Container || f.HeaderExtent.Length != f.RawHeader.Length ||
            f.HeaderExtent.Length < 13 || f.HeaderExtent.Offset + f.HeaderExtent.Length != f.PayloadExtent.Offset || f.DirectoryEntryExtent.Length != 8) return false;
        var expected = "dc-object/" + f.DirectoryIndex.ToString(CultureInfo.InvariantCulture) + "/" + f.NativeType.ToString(CultureInfo.InvariantCulture) + "/" + f.NativeId.ToString(CultureInfo.InvariantCulture);
        return f.Identity.MemberPath == expected;
    }
    private static bool ValidSource(SourceExtent e, int max) => Relative(e.RelativeSource, max) && e.Offset >= 0 && e.Length >= 0 && e.Offset <= long.MaxValue - e.Length &&
        (e.PayloadSha256 is null || e.PayloadSha256.Length == 64 && e.PayloadSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'));
    private sealed class Budget(long max) { private long used; public void Add(long n) { if (n < 0 || n > max - used) throw new ReadFailure("GmkCommonGroupMemoryLimit"); used += n; } }
    private ref struct Cursor(ReadOnlyMemory<byte> data, DecodedExtent parent, CancellationToken token)
    {
        private int position; public readonly int Position => position; public readonly int Remaining => data.Length - position;
        public ReadOnlyMemory<byte> Memory(int n) { token.ThrowIfCancellationRequested(); if (n < 0 || n > Remaining) throw new ReadFailure("GmkCommonGroupTruncatedData"); var m = data.Slice(position, n); position += n; return m; }
        public ReadOnlySpan<byte> Take(int n) => Memory(n).Span;
        public uint Word() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int Count(int max, string code) { var n = Word(); if (n > max || n > int.MaxValue) throw new ReadFailure(code); return (int)n; }
        public readonly DecodedExtent At(int offset, int length) => new(parent.Container, checked(parent.Offset + offset), length);
    }
    private sealed class ReadFailure(string code) : Exception(code) { public string Code { get; } = code; }
}
