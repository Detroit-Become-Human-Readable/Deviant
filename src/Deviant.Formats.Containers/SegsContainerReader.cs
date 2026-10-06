using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Formats.Containers;

public sealed record SegsDecodeLimits(long MaximumStoredBytes = 16 * 1024 * 1024,
    long MaximumDecodedBytes = 64 * 1024 * 1024, int MaximumSegments = 2048)
{
    public void Validate()
    {
        if (MaximumStoredBytes is < 16 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(MaximumStoredBytes));
        if (MaximumDecodedBytes is < 1 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(MaximumDecodedBytes));
        if (MaximumSegments is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(MaximumSegments));
    }
}

public sealed record SegsChunk(int Ordinal, ushort CompressedLength, ushort DecodedLengthWord,
    uint EncodedOffsetTag, int RelativeOffset, int DecodedLength, SourceExtent PhysicalExtent,
    DecodedExtent DecodedExtent, byte[] DecodedBytes);

public sealed record SegsGap(int OrdinalBeforeChunk, SourceExtent PhysicalExtent, byte[] Bytes);

public sealed record SegsDocument(uint Version, ushort SegmentCount, uint DeclaredDecodedLength,
    uint DeclaredCompressedSpan, int TableEndOffset, int DataStartOffset, int ActualDecodedLength,
    IReadOnlyList<SegsChunk> Chunks, IReadOnlyList<SegsGap> Gaps, byte[] DecodedPrefix,
    SourceExtent? RawSuffixExtent, byte[] RawSuffix, bool? AlternativeSizeMatchesRawSuffix);

/// <summary>Reads SEGS v1 zlib and literal segments and retains the raw suffix.</summary>
public sealed class SegsContainerReader(ISourceRevisionProvider? revisionProvider = null)
{
    public const string Version = "segs-v1-2";
    public const int HeaderLength = 16;
    public const int TableEntryLength = 8;
    public const int DefaultDecodedSegmentBytes = 64 * 1024;
    private static ReadOnlySpan<byte> Magic => "segs"u8;
    private readonly ISourceRevisionProvider revisions = revisionProvider ?? new FileRevisionProvider();

    public async Task<DecodeResult<SegsDocument>> ReadAsync(ArchiveSnapshot snapshot, ResourceRecord resource,
        SegsDecodeLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resource);
        limits ??= new();
        limits.Validate();
        var identity = new ResourceIdentity(snapshot.IndexContentId, resource.Ordinal);
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        if (resource.Ordinal < 0 || resource.Ordinal >= snapshot.Records.Count)
            return Failure(identity, revision, new SourceExtent("unknown", 0, 0), "OrdinalOutOfRange",
                "The supplied resource ordinal is outside the selected index.");
        var indexedResource = snapshot.Records[checked((int)resource.Ordinal)];
        var indexedIdentity = new ResourceIdentity(snapshot.IndexContentId, indexedResource.Ordinal);
        var indexedExtent = new SourceExtent(indexedResource.VolumeName, indexedResource.Offset, Math.Max(0, indexedResource.StoredSize));
        if (!IndexLayout.SameStoredRecord(resource, indexedResource))
            return Failure(indexedIdentity, revision, indexedExtent, "ResourceRecordMismatch",
                "The supplied resource fields differ from the selected index row at this ordinal.");
        var volume = snapshot.Volumes.SingleOrDefault(candidate => candidate.Package == indexedResource.Package);
        var relativeSource = volume?.RelativeName ?? indexedResource.VolumeName;
        var extent = new SourceExtent(relativeSource, indexedResource.Offset, Math.Max(0, indexedResource.StoredSize));
        if (indexedResource.Validity != ResourceValidity.Valid || volume is null || !volume.Stamp.Exists || volume.Stamp.Error is not null)
            return Failure(identity, revision, extent, "SourceUnavailable", "The selected resource does not have a valid available source range.");
        if (indexedResource.StoredSize < HeaderLength || indexedResource.StoredSize > limits.MaximumStoredBytes || indexedResource.StoredSize > int.MaxValue)
            return Failure(identity, revision, extent, "StoredSizeLimit", "The selected resource exceeds the configured SEGS input limit.");

        try
        {
            var resolver = new ArchiveResourceResolver(snapshot, revisions);
            var bytes = await resolver.ReadAsync(indexedIdentity, 0, checked((int)indexedResource.StoredSize), cancellationToken).ConfigureAwait(false);
            return Parse(bytes, indexedIdentity, revision, extent with { PayloadSha256 = Sha256(bytes) }, indexedResource.AlternativeSize,
                limits, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            return Failure(identity, revision, extent, "SourceReadFailed", ex.Message);
        }
    }

    public DecodeResult<SegsDocument> Parse(ReadOnlySpan<byte> storedBytes, ResourceIdentity identity,
        SourceRevision revision, SourceExtent originalExtent, uint? alternativeSize = null,
        SegsDecodeLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(originalExtent);
        limits ??= new();
        limits.Validate();
        if (identity.IndexContentId != revision.IndexContentId)
            throw new ArgumentException("Resource identity and revision must refer to the same index.", nameof(revision));
        if (string.IsNullOrWhiteSpace(originalExtent.RelativeSource) || originalExtent.Offset < 0 ||
            originalExtent.Length != storedBytes.Length || originalExtent.Offset > long.MaxValue - originalExtent.Length)
            throw new ArgumentException("The source extent must exactly describe the supplied SEGS bytes.", nameof(originalExtent));
        var payloadSha256 = Sha256(storedBytes);
        var extents = new[] { originalExtent with { PayloadSha256 = payloadSha256 } };
        DecodeStep Step(string options, string? digest = null) => new(nameof(SegsContainerReader), Version, options, digest);
        DecodeResult<SegsDocument> Failure(string code, string message, IReadOnlyDictionary<string, string>? fields = null) =>
            new(null, identity, revision, extents, [Step("little-endian; independent-zlib-chunks; raw-suffix-retained")],
                [code + ": " + message], [], fields ?? new Dictionary<string, string>());

        cancellationToken.ThrowIfCancellationRequested();
        if (originalExtent.PayloadSha256 is { } expectedHash &&
            !string.Equals(expectedHash, payloadSha256, StringComparison.OrdinalIgnoreCase))
            return Failure("PayloadHashMismatch", "The supplied extent digest does not match the bytes being parsed.",
                new Dictionary<string, string> { ["expectedSha256"] = expectedHash, ["actualSha256"] = payloadSha256 });
        if (storedBytes.Length < HeaderLength) return Failure("TruncatedHeader", "The SEGS header is shorter than 16 bytes.");
        if (storedBytes.Length > limits.MaximumStoredBytes)
            return Failure("StoredSizeLimit", "The supplied SEGS resource exceeds the configured stored-size limit.");
        if (!storedBytes[..4].SequenceEqual(Magic)) return Failure("InvalidSignature", "The resource does not begin with the lowercase SEGS signature.");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(storedBytes[4..]);
        if (version != 1) return Failure("UnsupportedVersion", $"SEGS version {version} is not supported.",
            new Dictionary<string, string> { ["version"] = version.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var segmentCount = BinaryPrimitives.ReadUInt16LittleEndian(storedBytes[6..]);
        var declaredDecodedLength = BinaryPrimitives.ReadUInt32LittleEndian(storedBytes[8..]);
        var declaredCompressedSpan = BinaryPrimitives.ReadUInt32LittleEndian(storedBytes[12..]);
        if (segmentCount is 0 || segmentCount > limits.MaximumSegments)
            return Failure("SegmentCountLimit", "The SEGS segment count is zero or exceeds the configured limit.");
        if (declaredDecodedLength == 0 || declaredDecodedLength > limits.MaximumDecodedBytes)
            return Failure("DecodedSizeLimit", "The declared decoded size is zero or exceeds the configured limit.");
        long tableEndLong;
        long dataStartLong;
        try
        {
            tableEndLong = checked((long)HeaderLength + (long)segmentCount * TableEntryLength);
            dataStartLong = checked((tableEndLong + 15) & ~15L);
        }
        catch (OverflowException) { return Failure("TableLengthOverflow", "The SEGS table length overflows the supported range."); }
        if (tableEndLong > storedBytes.Length || dataStartLong > storedBytes.Length || dataStartLong > int.MaxValue)
            return Failure("TruncatedTable", "The SEGS chunk table or aligned data start extends past the stored resource.");
        var tableEnd = checked((int)tableEndLong);
        var dataStart = checked((int)dataStartLong);

        var chunks = new List<SegsChunk>(segmentCount);
        var ranges = new List<(int Start, int End, int Index)>();
        long decodedTotal = 0;
        long maximumRelativeEnd = 0;
        for (var ordinal = 0; ordinal < segmentCount; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryOffset = checked(HeaderLength + ordinal * TableEntryLength);
            var compressedLength = BinaryPrimitives.ReadUInt16LittleEndian(storedBytes[entryOffset..]);
            var decodedLengthWord = BinaryPrimitives.ReadUInt16LittleEndian(storedBytes[(entryOffset + 2)..]);
            var encodedOffsetTag = BinaryPrimitives.ReadUInt32LittleEndian(storedBytes[(entryOffset + 4)..]);
            var isCompressed = (encodedOffsetTag & 1) != 0;
            var relativeOffsetValue = encodedOffsetTag & 0xFFFFFFFEu;
            if (relativeOffsetValue > int.MaxValue)
                return Failure("SegmentOffsetLimit", $"Segment {ordinal} offset exceeds the configured in-memory input range.");
            var relativeOffset = checked((int)relativeOffsetValue);
            var expectedDecodedLength = decodedLengthWord == 0 ? DefaultDecodedSegmentBytes : decodedLengthWord;
            var storedLength = !isCompressed && compressedLength == 0 ? DefaultDecodedSegmentBytes : compressedLength;
            if (isCompressed && compressedLength == 0)
                return Failure("EmptyCompressedSegment", $"Segment {ordinal} declares an empty zlib extent.");
            if (!isCompressed && storedLength != expectedDecodedLength)
                return Failure("LiteralSegmentLengthMismatch", $"Segment {ordinal} literal and decoded lengths differ.");
            if (expectedDecodedLength <= 0 || expectedDecodedLength > DefaultDecodedSegmentBytes)
                return Failure("DecodedSegmentLengthLimit", $"Segment {ordinal} declares an unsupported decoded length.");
            if (decodedTotal > limits.MaximumDecodedBytes - expectedDecodedLength)
                return Failure("DecodedSizeLimit", "The segment lengths exceed the configured decoded-size limit.");

            long relativeEnd;
            long physicalStart;
            long physicalEnd;
            try
            {
                relativeEnd = checked((long)relativeOffset + storedLength);
                physicalStart = checked(dataStartLong + relativeOffset);
                physicalEnd = checked(physicalStart + storedLength);
            }
            catch (OverflowException) { return Failure("SegmentExtentOverflow", $"Segment {ordinal} extent overflows the supported range."); }
            if (relativeEnd > declaredCompressedSpan || physicalEnd > storedBytes.Length)
                return Failure("SegmentExtentOutOfRange", $"Segment {ordinal} compressed range exceeds the declared or stored extent.");
            var relativeStart = checked((int)physicalStart);
            var relativePhysicalEnd = checked((int)physicalEnd);
            if (ranges.Any(range => relativeStart < range.End && range.Start < relativePhysicalEnd))
                return Failure("SegmentExtentOverlap", $"Segment {ordinal} overlaps an earlier compressed extent.");

            var compressed = storedBytes.Slice(checked((int)physicalStart), storedLength);
            byte[] decoded;
            try { decoded = isCompressed ? DecompressOne(compressed, expectedDecodedLength) : compressed.ToArray(); }
            catch (InvalidDataException ex) { return Failure("InvalidZlibSegment", $"Segment {ordinal}: {ex.Message}"); }
            if (decoded.Length != expectedDecodedLength)
                return Failure("DecodedSegmentLengthMismatch", $"Segment {ordinal} produced {decoded.Length} bytes, expected {expectedDecodedLength}.");

            decodedTotal += decoded.Length;
            maximumRelativeEnd = Math.Max(maximumRelativeEnd, relativeEnd);
            var physicalExtent = originalExtent with
            {
                Offset = checked(originalExtent.Offset + physicalStart),
                Length = storedLength,
                PayloadSha256 = Sha256(compressed)
            };
            var decodedExtent = new DecodedExtent(identity, decodedTotal - decoded.Length, decoded.Length);
            chunks.Add(new SegsChunk(ordinal, compressedLength, decodedLengthWord, encodedOffsetTag,
                relativeOffset, decoded.Length, physicalExtent, decodedExtent, decoded));
            ranges.Add((relativeStart, relativePhysicalEnd, ordinal));
        }

        if (decodedTotal != declaredDecodedLength)
            return Failure("DecodedTotalMismatch", $"Decoded segments total {decodedTotal} bytes, header declares {declaredDecodedLength}.");
        if (maximumRelativeEnd != declaredCompressedSpan)
            return Failure("CompressedSpanMismatch", $"Segment extents end at {maximumRelativeEnd}, header declares span {declaredCompressedSpan}.");

        ranges.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        var gaps = new List<SegsGap>();
        if (tableEnd < dataStart)
        {
            var padding = storedBytes[tableEnd..dataStart].ToArray();
            gaps.Add(new SegsGap(-1, originalExtent with
            {
                Offset = checked(originalExtent.Offset + tableEnd),
                Length = padding.Length,
                PayloadSha256 = Sha256(padding)
            }, padding));
        }
        var previousEnd = dataStart;
        foreach (var range in ranges)
        {
            if (range.Start < previousEnd)
                return Failure("SegmentExtentOverlap", $"Segment {range.Index} overlaps an earlier compressed extent.");
            if (range.Start > previousEnd)
            {
                var gapBytes = storedBytes[previousEnd..range.Start].ToArray();
                gaps.Add(new SegsGap(range.Index, originalExtent with
                {
                    Offset = checked(originalExtent.Offset + previousEnd),
                    Length = gapBytes.Length,
                    PayloadSha256 = Sha256(gapBytes)
                }, gapBytes));
            }
            previousEnd = range.End;
        }
        var compressedEnd = checked(dataStart + (int)declaredCompressedSpan);
        if (previousEnd > compressedEnd)
            return Failure("CompressedEndOverflow", "A compressed extent extends beyond the declared compressed span.");
        if (previousEnd < compressedEnd)
        {
            var gapBytes = storedBytes[previousEnd..compressedEnd].ToArray();
            gaps.Add(new SegsGap(segmentCount, originalExtent with
            {
                Offset = checked(originalExtent.Offset + previousEnd),
                Length = gapBytes.Length,
                PayloadSha256 = Sha256(gapBytes)
            }, gapBytes));
        }

        var suffixOffset = compressedEnd;
        var suffix = storedBytes[suffixOffset..].ToArray();
        var suffixExtent = suffix.Length == 0 ? null : originalExtent with
        {
            Offset = checked(originalExtent.Offset + suffixOffset),
            Length = suffix.Length,
            PayloadSha256 = Sha256(suffix)
        };
        bool? altMatches = alternativeSize is null ? null : suffix.Length == alternativeSize.Value;
        var output = new byte[checked((int)decodedTotal)];
        var outputOffset = 0;
        foreach (var chunk in chunks)
        {
            chunk.DecodedBytes.CopyTo(output, outputOffset);
            outputOffset += chunk.DecodedBytes.Length;
        }

        var document = new SegsDocument(version, segmentCount, declaredDecodedLength, declaredCompressedSpan,
            tableEnd, dataStart, checked((int)decodedTotal), chunks.AsReadOnly(), gaps.AsReadOnly(), output,
            suffixExtent, suffix, altMatches);
        var unknown = new Dictionary<string, string>
        {
            ["encodedOffsetLowBit"] = "One selects zlib; zero selects literal bytes. The bit is masked for extent addressing.",
            ["decodedLengthWordZero"] = "Treated as 65,536 bytes based on the observed full-chunk convention.",
            ["rawSuffix"] = suffix.Length == 0 ? "Absent" : "Retained separately; not included in decoded prefix."
        };
        if (altMatches == false) unknown["alternativeSize"] = "Does not equal raw suffix length for this sample.";
        var steps = new[] { new DecodeStep(nameof(SegsContainerReader), Version,
            "version1; header16; entries8; zlib-or-literal-segments; lowbit-masked-offset; raw-suffix-separate", Sha256(storedBytes)) };
        var diagnostics = altMatches == false ? new[] { "AlternativeSizeDoesNotMatchSuffix" } : Array.Empty<string>();
        return new(document, identity, revision, extents, steps, diagnostics, [], unknown);
    }

    private static byte[] DecompressOne(ReadOnlySpan<byte> compressed, int expectedLength)
    {
        if (compressed.Length < 6)
            throw new InvalidDataException("The zlib stream is shorter than its header and Adler-32 trailer.");

        var cmf = compressed[0];
        var flg = compressed[1];
        if ((cmf & 0x0F) != 8 || (cmf >> 4) > 7 || ((cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0)
            throw new InvalidDataException("The zlib header is invalid or requests an unsupported preset dictionary.");

        using var input = new MemoryStream(compressed.ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: false);
        var output = new byte[expectedLength];
        var written = 0;
        while (written < output.Length)
        {
            var count = zlib.Read(output, written, output.Length - written);
            if (count == 0) throw new InvalidDataException("The zlib stream ended before its declared chunk size.");
            written += count;
        }
        if (zlib.ReadByte() != -1) throw new InvalidDataException("The zlib stream exceeds its declared chunk size.");

        var expectedAdler32 = BinaryPrimitives.ReadUInt32BigEndian(compressed[^4..]);
        var actualAdler32 = Adler32(output);
        if (actualAdler32 != expectedAdler32)
            throw new InvalidDataException("The zlib Adler-32 trailer is missing or does not match the decoded chunk.");
        return output;
    }

    private static uint Adler32(ReadOnlySpan<byte> bytes)
    {
        const uint modulus = 65521;
        uint a = 1;
        uint b = 0;
        foreach (var value in bytes)
        {
            a = (a + value) % modulus;
            b = (b + a) % modulus;
        }
        return (b << 16) | a;
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static DecodeResult<SegsDocument> Failure(ResourceIdentity identity, SourceRevision revision,
        SourceExtent extent, string code, string message) =>
        new(null, identity, revision, [extent], [new(nameof(SegsContainerReader), Version, "little-endian; raw-suffix-retained")],
            [$"{code}: {message}"], [], new Dictionary<string, string>());
}
