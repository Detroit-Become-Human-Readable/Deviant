using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Deviant.Archives;
using Deviant.Formats.Containers;

namespace Deviant.ContainerProbe;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ProbeOptions.Parse(args);
            var snapshot = await new ArchiveReader().OpenAsync(options.Input).ConfigureAwait(false);
            if (options.Ordinal < 0 || options.Ordinal >= snapshot.Records.Count)
                throw new ArgumentOutOfRangeException(nameof(options.Ordinal), "The ordinal is outside the selected index.");
            var resource = snapshot.Records[checked((int)options.Ordinal)];
            var result = await new SegsContainerReader().ReadAsync(snapshot, resource,
                new SegsDecodeLimits(options.MaximumStoredBytes, options.MaximumDecodedBytes, options.MaximumSegments)).ConfigureAwait(false);
            var document = result.Value;
            var nestedResult = options.RecordOffset is { } recordOffset
                ? new RawNestedRecordReader().Inspect(result, recordOffset)
                : null;
            var report = new
            {
                Format = "deviant-segs-container-probe-v1",
                IndexContentId = snapshot.IndexContentId,
                Identity = result.Identity.Key,
                Revision = new { result.Revision.IndexContentId, result.Revision.PhysicalSourceStamp },
                Resource = new
                {
                    resource.Ordinal,
                    resource.Type,
                    resource.Flags,
                    resource.Id,
                    resource.Package,
                    resource.VolumeName,
                    resource.Offset,
                    resource.StoredSize,
                    resource.AlternativeSize,
                    resource.Validity
                },
                Decode = document is null ? null : new
                {
                    document.Version,
                    document.SegmentCount,
                    document.DeclaredDecodedLength,
                    document.DeclaredCompressedSpan,
                    document.TableEndOffset,
                    document.DataStartOffset,
                    document.ActualDecodedLength,
                    RawSuffixLength = document.RawSuffix.Length,
                    RawSuffixSha256 = Sha256(document.RawSuffix),
                    document.AlternativeSizeMatchesRawSuffix,
                    SegmentExtentCount = document.Chunks.Count,
                    GapCount = document.Gaps.Count,
                    Segments = document.Chunks.Select(chunk => new
                    {
                        chunk.Ordinal,
                        chunk.CompressedLength,
                        chunk.DecodedLengthWord,
                        chunk.DecodedLength,
                        chunk.EncodedOffsetTag,
                        chunk.RelativeOffset,
                        Physical = new { chunk.PhysicalExtent.RelativeSource, chunk.PhysicalExtent.Offset, chunk.PhysicalExtent.Length, chunk.PhysicalExtent.PayloadSha256 },
                        Decoded = new { chunk.DecodedExtent.Offset, chunk.DecodedExtent.Length },
                        DecodedSha256 = Sha256(chunk.DecodedBytes)
                    }),
                    Gaps = document.Gaps.Select(gap => new
                    {
                        gap.OrdinalBeforeChunk,
                        gap.PhysicalExtent.Offset,
                        gap.PhysicalExtent.Length,
                        gap.PhysicalExtent.PayloadSha256
                    }),
                    DecodedPrefixFirstBytesHex = Convert.ToHexString(document.DecodedPrefix.AsSpan(0, Math.Min(document.DecodedPrefix.Length, 64))).ToLowerInvariant()
                },
                NestedRecord = nestedResult is null ? null : new
                {
                    Value = nestedResult.Value is { } nested ? new
                    {
                        HeaderHex = Convert.ToHexString(nested.RawHeader).ToLowerInvariant(),
                        nested.TagText,
                        nested.Version,
                        nested.DeclaredBodyLength,
                        nested.HeaderExtent,
                        BodySlices = nested.BodySlices.Select(slice => new
                        {
                            slice.BodyOffset,
                            slice.Source,
                            Length = slice.Bytes.Length,
                            Sha256 = Sha256(slice.Bytes.Span),
                            slice.DecodedExtent,
                            slice.PhysicalExtent
                        }),
                        nested.UnparsedDecodedRemainder,
                        nested.UnparsedRawSuffixRemainder
                    } : null,
                    nestedResult.Steps,
                    nestedResult.Extents,
                    nestedResult.Diagnostics,
                    nestedResult.UnknownFields
                },
                result.Steps,
                result.Extents,
                result.Diagnostics,
                result.UnresolvedReferences,
                result.UnknownFields
            };
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return document is null || nestedResult is { Value: null } ||
                result.Diagnostics.Any(diagnostic => diagnostic.StartsWith("StoredSizeLimit", StringComparison.Ordinal) || diagnostic.StartsWith("SourceReadFailed", StringComparison.Ordinal)) ? 2 : 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(ProbeOptions.Usage);
            return 2;
        }
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record ProbeOptions(string Input, long Ordinal, long? RecordOffset, long MaximumStoredBytes,
        long MaximumDecodedBytes, int MaximumSegments)
    {
        public const string Usage = "Usage: Deviant.ContainerProbe --input <index-or-directory> --ordinal <n> [--record-offset <decoded-prefix-offset>] [--max-stored-bytes <n>] [--max-decoded-bytes <n>] [--max-segments <n>]";

        public static ProbeOptions Parse(string[] args)
        {
            string? input = null;
            long? ordinal = null;
            long? recordOffset = null;
            long storedBytes = 16 * 1024 * 1024;
            long decodedBytes = 64 * 1024 * 1024;
            var segments = 2048;
            for (var i = 0; i < args.Length; i++)
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[i]}.");
                var option = args[i];
                var value = args[++i];
                switch (option)
                {
                    case "--input": input = value; break;
                    case "--ordinal":
                        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedOrdinal))
                            throw new ArgumentException("Ordinal must be a non-negative decimal integer.");
                        ordinal = parsedOrdinal;
                        break;
                    case "--record-offset":
                        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedRecordOffset))
                            throw new ArgumentException("Record offset must be a non-negative decimal integer.");
                        recordOffset = parsedRecordOffset;
                        break;
                    case "--max-stored-bytes":
                        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out storedBytes))
                            throw new ArgumentException("Stored-byte limit must be a positive decimal integer.");
                        break;
                    case "--max-decoded-bytes":
                        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out decodedBytes))
                            throw new ArgumentException("Decoded-byte limit must be a positive decimal integer.");
                        break;
                    case "--max-segments":
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out segments))
                            throw new ArgumentException("Segment limit must be a positive decimal integer.");
                        break;
                    default: throw new ArgumentException($"Unknown option {option}.");
                }
            }
            if (string.IsNullOrWhiteSpace(input) || ordinal is null)
                throw new ArgumentException(Usage);
            return new(input, ordinal.Value, recordOffset, storedBytes, decodedBytes, segments);
        }
    }
}
