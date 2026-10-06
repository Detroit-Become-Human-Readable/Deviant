using System.Buffers.Binary;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Containers;

namespace Deviant.Formats.Textures;

public enum TextureBufferPatternProfile { ObservedEtfSeGsCandidateV1 }
public enum TextureBufferPatternKind { Raw, RawLod }

public sealed record TextureBufferPatternBoundaryBasis(int CandidateLengthWordOffset, int CandidatePayloadOffset,
    bool CandidateLengthEndsAtStoredExtent, bool PayloadBeginsWithSegs, bool CursorOriginEstablished);

/// <summary>Opt-in inspection of a retained ETF prefix and its bounded SEGS representation.</summary>
public sealed record TextureBufferPatternInspection(
    TextureBufferPatternProfile Profile,
    TextureBufferPatternKind Kind,
    byte[] HeaderBytes,
    byte[] RawLengthWord,
    uint CandidatePayloadLength,
    byte[] SkippedBytes,
    SourceExtent StoredExtent,
    SourceExtent PayloadExtent,
    SegsDocument Segments,
    TextureBufferPatternBoundaryBasis BoundaryBasis,
    IReadOnlyDictionary<string, string> Assumptions);

/// <summary>Inspects one explicitly selected ETF buffer pattern. It does not decode image pixels.</summary>
public sealed class TextureBufferPatternInspector(ISourceRevisionProvider? revisionProvider = null)
{
    public const string Version = "texture-buffer-pattern-inspector-v1";
    public const int MaximumStoredBytes = 16 * 1024 * 1024;
    public const int MaximumDecodedBytes = 16 * 1024 * 1024;
    public const int HeaderLength = 32;
    public const int CandidateLengthWordOffset = 21;
    public const int OpaqueSkipLength = 7;
    private const uint RawType = 2229;
    private const uint RawLodType = 2230;
    private static ReadOnlySpan<byte> QzipMagic => "QZIP\0"u8;
    private static ReadOnlySpan<byte> SegsMagic => "segs"u8;
    private readonly ISourceRevisionProvider revisions = revisionProvider ?? new FileRevisionProvider();

    public async Task<DecodeResult<TextureBufferPatternInspection>> ReadAsync(ArchiveSnapshot snapshot,
        ResourceRecord indexedResource, TextureBufferPatternProfile profile, SegsDecodeLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(indexedResource);
        limits ??= DefaultLimits();
        ValidateLimits(limits);
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        var identity = new ResourceIdentity(snapshot.IndexContentId, indexedResource.Ordinal);
        var extent = new SourceExtent(indexedResource.VolumeName, indexedResource.Offset,
            Math.Max(0, indexedResource.StoredSize));
        var row = FindResource(snapshot, indexedResource.Ordinal);
        if (row is null || !IndexLayout.SameStoredRecord(indexedResource, row))
            return Failure(identity, revision, extent, profile, "UnsupportedResourceRecord", "The selected row differs from the archive index.");
        if (row.Validity != ResourceValidity.Valid)
            return Failure(identity, revision, extent, profile, "UnsupportedResourceRange", "The selected resource range is not valid.");
        if (row.Type is not (RawType or RawLodType))
            return Failure(identity, revision, extent, profile, "UnsupportedResourceType", "This inspector accepts ETF_RAW_ and ETF_RAWL resource types only.");
        if (profile != TextureBufferPatternProfile.ObservedEtfSeGsCandidateV1)
            return Failure(identity, revision, extent, profile, "UnsupportedProfile", "The selected pattern inspection profile is not supported.");
        if (row.StoredSize < HeaderLength || row.StoredSize > limits.MaximumStoredBytes || row.StoredSize > int.MaxValue)
            return Failure(identity, revision, extent, profile, "UnsupportedStoredSize", "The selected resource is outside the configured stored-byte limit.");

        await ValidateIndexAsync(snapshot, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = await new ArchiveResourceResolver(snapshot, revisions)
            .ReadAsync(identity, 0, checked((int)row.StoredSize), cancellationToken).ConfigureAwait(false);
        var result = Parse(bytes, identity, revision, extent with { PayloadSha256 = Sha256(bytes) }, row.Type,
            profile, limits, cancellationToken);
        await ValidateIndexAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public DecodeResult<TextureBufferPatternInspection> Parse(ReadOnlySpan<byte> storedBytes,
        ResourceIdentity identity, SourceRevision revision, SourceExtent originalExtent, uint resourceType,
        TextureBufferPatternProfile profile, SegsDecodeLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(originalExtent);
        limits ??= DefaultLimits();
        ValidateLimits(limits);
        if (!StringComparer.Ordinal.Equals(identity.IndexContentId, revision.IndexContentId))
            throw new ArgumentException("Resource identity and revision must refer to the same archive index.", nameof(revision));

        cancellationToken.ThrowIfCancellationRequested();
        if (originalExtent.Offset < 0 || originalExtent.Length != storedBytes.Length ||
            originalExtent.Offset > long.MaxValue - originalExtent.Length)
            return Failure(identity, revision, originalExtent, profile, "InvalidSourceExtent", "The source extent must exactly describe the supplied resource bytes.");
        if (storedBytes.Length < HeaderLength || storedBytes.Length > limits.MaximumStoredBytes)
            return Failure(identity, revision, originalExtent, profile, "UnsupportedStoredSize", "The resource is outside the configured stored-byte limit.");
        if (profile != TextureBufferPatternProfile.ObservedEtfSeGsCandidateV1)
            return Failure(identity, revision, originalExtent, profile, "UnsupportedProfile", "The selected pattern inspection profile is not supported.");
        var storedHash = Sha256(storedBytes);
        var storedExtent = originalExtent with { PayloadSha256 = storedHash };
        var step = new DecodeStep(nameof(TextureBufferPatternInspector), Version,
            $"profile={profile};candidate-pattern-only", storedHash);
        DecodeResult<TextureBufferPatternInspection> Unsupported(string code, string message,
            IReadOnlyList<SourceExtent>? extents = null, IReadOnlyList<DecodeStep>? steps = null) =>
            new(null, identity, revision, extents ?? [storedExtent], steps ?? [step],
                [$"Unsupported {code}: {message}"], [], new Dictionary<string, string> { ["status"] = "Unsupported" });

        if (profile != TextureBufferPatternProfile.ObservedEtfSeGsCandidateV1)
            return Unsupported("UnsupportedProfile", "The selected buffer inspection profile is not supported.");
        if (originalExtent.PayloadSha256 is { } expectedHash &&
            !StringComparer.OrdinalIgnoreCase.Equals(expectedHash, storedHash))
            return Unsupported("StoredBytesHashMismatch", "The owner stored-byte hash does not match the supplied bytes.");
        if (!TryGetPayload(storedBytes, originalExtent, resourceType, out var kind, out var payload,
            out var envelopeExtent, out var envelopeDiagnostic))
            return Unsupported("UnsupportedEnvelope", envelopeDiagnostic!);
        var rawLengthWord = storedBytes.Slice(CandidateLengthWordOffset, sizeof(uint)).ToArray();
        var candidatePayloadLength = BinaryPrimitives.ReadUInt32LittleEndian(rawLengthWord);
        var candidatePayloadOffset = HeaderLength;
        if (payload.Length < SegsMagic.Length || !payload[..SegsMagic.Length].SequenceEqual(SegsMagic))
            return Unsupported("UnsupportedPayloadSignature", "The candidate payload does not begin with SEGS.");
        var payloadExtent = envelopeExtent!;
        var segsResult = new SegsContainerReader(revisions).Parse(payload, identity, revision, payloadExtent,
            limits: limits, cancellationToken: cancellationToken);
        var extents = new[] { storedExtent }.Concat(segsResult.Extents).ToArray();
        var steps = new[] { step }.Concat(segsResult.Steps).ToArray();
        if (segsResult.Value is not { } segments)
            return Unsupported("UnsupportedSegsPayload", string.Join("; ", segsResult.Diagnostics), extents, steps);

        var assumptions = new Dictionary<string, string>
        {
            ["headerBytes13To20"] = "Retained in HeaderBytes with no assigned meaning.",
            ["lengthWord"] = "A little-endian interpretation at byte 21 matches the exact indexed extent in the selected samples.",
            ["cursorOrigin"] = "The native cursor origin has not been established.",
            ["skippedBytes"] = "All seven bytes are retained verbatim and left uninterpreted.",
            ["imageMetadata"] = "Dimensions, pixel format and texture pairing are not assigned."
        };
        var inspection = new TextureBufferPatternInspection(profile, kind,
            storedBytes[..HeaderLength].ToArray(), rawLengthWord, candidatePayloadLength,
            storedBytes.Slice(CandidateLengthWordOffset + sizeof(uint), OpaqueSkipLength).ToArray(),
            storedExtent, payloadExtent, segments,
            new(CandidateLengthWordOffset, candidatePayloadOffset, true, true, false), assumptions);
        return new(inspection, identity, revision, extents, steps, segsResult.Diagnostics, [],
            new Dictionary<string, string> { ["status"] = "Inspected" });
    }

    /// <summary>Validates the shared ETF envelope and returns its exact stored payload.</summary>
    public static bool TryGetPayload(ReadOnlySpan<byte> storedBytes, SourceExtent originalExtent, uint resourceType,
        out TextureBufferPatternKind kind, out ReadOnlySpan<byte> payload, out SourceExtent? payloadExtent,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(originalExtent);
        kind = default; payload = default; payloadExtent = null; diagnostic = null;
        if (originalExtent.Offset < 0 || originalExtent.Length != storedBytes.Length ||
            originalExtent.Offset > long.MaxValue - originalExtent.Length ||
            storedBytes.Length < HeaderLength || storedBytes.Length > MaximumStoredBytes)
        { diagnostic = "The texture source extent is invalid or exceeds its size limit."; return false; }
        if (originalExtent.PayloadSha256 is { } expected && !StringComparer.OrdinalIgnoreCase.Equals(expected, Sha256(storedBytes)))
        { diagnostic = "The texture bytes do not match their source hash."; return false; }
        if (!storedBytes[..QzipMagic.Length].SequenceEqual(QzipMagic))
        { diagnostic = "The resource does not begin with QZIP."; return false; }
        ReadOnlySpan<byte> tag;
        if (resourceType == RawType) { kind = TextureBufferPatternKind.Raw; tag = "ETF_RAW_"u8; }
        else if (resourceType == RawLodType) { kind = TextureBufferPatternKind.RawLod; tag = "ETF_RAWL"u8; }
        else { diagnostic = "The resource is not a supported ETF texture type."; return false; }
        if (!storedBytes.Slice(QzipMagic.Length, tag.Length).SequenceEqual(tag))
        { diagnostic = "The wrapper tag does not match the texture type."; return false; }
        var length = BinaryPrimitives.ReadUInt32LittleEndian(storedBytes[CandidateLengthWordOffset..]);
        if (length != storedBytes.Length - HeaderLength)
        { diagnostic = "The payload length does not match the stored texture extent."; return false; }
        payload = storedBytes[HeaderLength..];
        payloadExtent = new(originalExtent.RelativeSource, checked(originalExtent.Offset + HeaderLength), length, Sha256(payload));
        return true;
    }

    private async Task ValidateIndexAsync(ArchiveSnapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var handle = SourceIdentity.OpenPinned(snapshot.IndexPath);
        SourceIdentity.EnsureUnchanged(revisions, handle, snapshot.IndexPath, snapshot.IndexStamp);
        var contentId = await SourceIdentity.HashAsync(handle, snapshot.IndexStamp.Length, token,
            phase: "Validating texture buffer inspection index").ConfigureAwait(false);
        SourceIdentity.EnsureUnchanged(revisions, handle, snapshot.IndexPath, snapshot.IndexStamp);
        if (!StringComparer.Ordinal.Equals(contentId, snapshot.IndexContentId))
            throw new ArchiveException("SourceChanged", "The archive index contents changed during texture buffer inspection.");
    }

    private static ResourceRecord? FindResource(ArchiveSnapshot snapshot, long ordinal)
    {
        if (ordinal >= 0 && ordinal < snapshot.Records.Count)
        {
            var row = snapshot.Records[checked((int)ordinal)];
            if (row.Ordinal == ordinal) return row;
        }
        return snapshot.Records.SingleOrDefault(row => row.Ordinal == ordinal);
    }

    private static SegsDecodeLimits DefaultLimits() =>
        new(MaximumStoredBytes, MaximumDecodedBytes, MaximumSegments: 2048);

    private static void ValidateLimits(SegsDecodeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        if (limits.MaximumStoredBytes > MaximumStoredBytes || limits.MaximumDecodedBytes > MaximumDecodedBytes)
            throw new ArgumentOutOfRangeException(nameof(limits), "Inspection limits cannot exceed 16 MiB stored or decoded.");
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static DecodeResult<TextureBufferPatternInspection> Failure(ResourceIdentity identity,
        SourceRevision revision, SourceExtent extent, TextureBufferPatternProfile profile, string code, string message) =>
        new(null, identity, revision, [extent], [new(nameof(TextureBufferPatternInspector), Version, $"profile={profile}")],
            [$"Unsupported {code}: {message}"], [], new Dictionary<string, string> { ["status"] = "Unsupported" });
}
