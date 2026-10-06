using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Formats.Containers;

public enum RawRecordSliceSource { DecodedPrefix, RawSuffix }

public sealed record RawRecordBodySlice(long BodyOffset, RawRecordSliceSource Source,
    ReadOnlyMemory<byte> Bytes, DecodedExtent? DecodedExtent, SourceExtent? PhysicalExtent);

public sealed record RawNestedRecordInspection(byte[] RawHeader, string TagText, uint Version,
    uint DeclaredBodyLength, DecodedExtent HeaderExtent, IReadOnlyList<RawRecordBodySlice> BodySlices,
    DecodedExtent? UnparsedDecodedRemainder, SourceExtent? UnparsedRawSuffixRemainder);

/// <summary>Inspects a bounded 16-byte tagged-record header and keeps its body slices raw.</summary>
public sealed class RawNestedRecordReader
{
    public const string Version = "raw-nested-record-v1";
    public const int HeaderLength = 16;

    public DecodeResult<RawNestedRecordInspection> Inspect(DecodeResult<SegsDocument> decodedContainer,
        long decodedRecordOffset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decodedContainer);
        var identity = decodedContainer.Identity;
        var revision = decodedContainer.Revision;
        var extents = decodedContainer.Extents;
        var priorSteps = decodedContainer.Steps;
        DecodeResult<RawNestedRecordInspection> Fail(string code, string message) =>
            new(null, identity, revision, extents,
                [.. priorSteps, new(nameof(RawNestedRecordReader), Version, "little-endian-u32-header; raw-body-slices")],
                [code + ": " + message], decodedContainer.UnresolvedReferences,
                new Dictionary<string, string> { ["recordOffset"] = decodedRecordOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        cancellationToken.ThrowIfCancellationRequested();
        if (decodedContainer.Value is not { } document)
            return Fail("ContainerDecodeFailed", "The SEGS result has no decoded value.");
        if (identity.IndexContentId != revision.IndexContentId)
            return Fail("ContainerRevisionMismatch", "The container identity and source revision refer to different index snapshots.");
        if (decodedRecordOffset < 0 || decodedRecordOffset > document.DecodedPrefix.Length - HeaderLength)
            return Fail("RecordHeaderOutOfRange", "The complete record header must lie within the decoded prefix.");
        if (document.RawSuffix.Length == 0 && document.RawSuffixExtent is not null ||
            document.RawSuffix.Length > 0 && (document.RawSuffixExtent is not { } rawSuffixExtent ||
                rawSuffixExtent.Length != document.RawSuffix.Length || rawSuffixExtent.Offset < 0 ||
                rawSuffixExtent.Offset > long.MaxValue - rawSuffixExtent.Length ||
                !string.Equals(rawSuffixExtent.PayloadSha256, Sha256(document.RawSuffix), StringComparison.OrdinalIgnoreCase)))
            return Fail("RawSuffixExtentMismatch", "The raw suffix bytes do not match their physical source extent.");

        var prefixOffset = checked((int)decodedRecordOffset);
        var header = document.DecodedPrefix.AsSpan(prefixOffset, HeaderLength).ToArray();
        var tagBytes = header.AsSpan(0, 8);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
        var bodyStart = checked(prefixOffset + HeaderLength);
        var availablePrefixBody = document.DecodedPrefix.Length - bodyStart;
        var availableBodyLength = (ulong)availablePrefixBody + (ulong)document.RawSuffix.Length;
        if (bodyLength > availableBodyLength)
            return Fail("RecordBodyOutOfRange", "The declared record body exceeds the remaining decoded-prefix and raw-suffix bytes.");
        var prefixBodyLength = checked((int)Math.Min(bodyLength, (uint)availablePrefixBody));
        var suffixBodyLength = checked((int)(bodyLength - (uint)prefixBodyLength));

        cancellationToken.ThrowIfCancellationRequested();
        var slices = new List<RawRecordBodySlice>(2);
        if (prefixBodyLength > 0)
        {
            var prefixBytes = new ReadOnlyMemory<byte>(document.DecodedPrefix, bodyStart, prefixBodyLength);
            slices.Add(new RawRecordBodySlice(0, RawRecordSliceSource.DecodedPrefix, prefixBytes,
                new DecodedExtent(identity, bodyStart, prefixBodyLength), null));
        }
        if (suffixBodyLength > 0)
        {
            var suffixExtent = document.RawSuffixExtent!;
            var suffixBytes = new ReadOnlyMemory<byte>(document.RawSuffix, 0, suffixBodyLength);
            slices.Add(new RawRecordBodySlice(prefixBodyLength, RawRecordSliceSource.RawSuffix, suffixBytes, null,
                suffixExtent with
                {
                    Length = suffixBodyLength,
                    PayloadSha256 = Sha256(suffixBytes.Span)
                }));
        }

        var decodedRemainderOffset = checked((long)bodyStart + prefixBodyLength);
        var decodedRemainder = decodedRemainderOffset == document.DecodedPrefix.Length ? null :
            new DecodedExtent(identity, decodedRemainderOffset, document.DecodedPrefix.Length - decodedRemainderOffset);
        var suffixRemainderLength = document.RawSuffix.Length - suffixBodyLength;
        SourceExtent? suffixRemainder = null;
        if (suffixRemainderLength > 0)
        {
            var originalSuffix = document.RawSuffixExtent!;
            var remainderBytes = document.RawSuffix.AsSpan(suffixBodyLength, suffixRemainderLength);
            suffixRemainder = originalSuffix with
            {
                Offset = checked(originalSuffix.Offset + suffixBodyLength),
                Length = suffixRemainderLength,
                PayloadSha256 = Sha256(remainderBytes)
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        var value = new RawNestedRecordInspection(header, Encoding.ASCII.GetString(tagBytes), version, bodyLength,
            new DecodedExtent(identity, decodedRecordOffset, HeaderLength), slices.AsReadOnly(), decodedRemainder, suffixRemainder);
        var digest = Sha256(header);
        return new(value, identity, revision, extents,
            [.. priorSteps, new(nameof(RawNestedRecordReader), Version,
                "tag-bytes-preserved;little-endian-version-and-body-length;split-body-extents", digest)],
            [], decodedContainer.UnresolvedReferences,
            new Dictionary<string, string>
            {
                ["tag"] = Convert.ToHexString(tagBytes).ToLowerInvariant(),
                ["bodyInterpretation"] = "Raw bytes only; no nested field meanings assigned.",
                ["bodySliceCount"] = slices.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
