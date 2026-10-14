using System.Buffers.Binary;
using System.Globalization;
using Deviant.Assets;
using Deviant.Formats.Containers;

namespace Deviant.Formats.Textures;

/// <summary>Reads one bounded Native:2217 PROCTEX v7 frame without executing controller payloads.</summary>
public sealed class NativeProceduralTextureReader
{
    public const string Version = "native-proctex-v7-v1";
    public const uint NativeType = 2217;
    public const int TextureReferenceCount = 4;
    private const int ClassTagBytes = 8;
    private const int ClassVersionBytes = 4;
    private const int FixedProjectionBytes = 4096;

    /// <summary>
    /// Parses a borrowed frame. The caller must pass the exact full revision from its current container
    /// lease, reserve the configured projection and provenance bounds before this call, and keep the lease
    /// alive while returned raw views are used. This frame model does not itself carry a physical stamp.
    /// </summary>
    public DecodeResult<NativeProceduralTextureDocument> Read(NativeObjectFrame frame,
        SourceRevision revision, IReadOnlyList<SourceExtent> physicalExtents,
        NativeProceduralTextureReadLimits? limits = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(physicalExtents);
        token.ThrowIfCancellationRequested();
        limits ??= new NativeProceduralTextureReadLimits();
        var steps = new[] { new DecodeStep(nameof(NativeProceduralTextureReader), Version,
            "Native:2217; PROCTEX v7; length-prefixed opaque controller bytes; borrowed frame") };
        IReadOnlyList<SourceExtent> capturedExtents = Array.Empty<SourceExtent>();
        DecodeResult<NativeProceduralTextureDocument> Fail(string code) => new(null, frame.Identity,
            revision, capturedExtents, steps, [code], [], new Dictionary<string, string> { ["code"] = code });

        if (!limits.IsValid()) return Fail("NativeProceduralTextureInvalidLimits");
        if (!ValidSourceIdentity(frame, revision)) return Fail("NativeProceduralTextureSourceMismatch");
        if (frame.NativeType != NativeType) return Fail("NativeProceduralTextureTypeUnsupported");
        if (frame.ClassCount != 3) return Fail("NativeProceduralTextureClassCountUnsupported");
        if (frame.RawPayload.Length < 20) return Fail("NativeProceduralTexturePrefixTruncated");
        if (frame.RawPayload.Length > limits.MaximumPayloadBytes)
            return Fail("NativeProceduralTexturePayloadLimit");

        var physicalCount = physicalExtents.Count;
        if (physicalCount < 1 || physicalCount > limits.MaximumPhysicalSourceExtents)
            return Fail("NativeProceduralTextureSourceExtentLimit");
        var physicalSnapshot = new SourceExtent[physicalCount];
        long sourceBytes = 0;
        long sourceProvenanceBytes = 0;
        var sourceKeys = new HashSet<(string Path, long Offset, long Length, string Hash)>();
        for (var index = 0; index < physicalCount; index++)
        {
            token.ThrowIfCancellationRequested();
            var extent = physicalExtents[index];
            physicalSnapshot[index] = extent;
            if (!ValidPhysicalExtent(extent)) return Fail("NativeProceduralTextureSourceExtentInvalid");
            try
            {
                sourceProvenanceBytes = checked(sourceProvenanceBytes + 256L +
                    extent.RelativeSource.Length * 2L + (extent.PayloadSha256!.Length * 2L));
                if (sourceProvenanceBytes > limits.MaximumSourceProvenanceBytes)
                    return Fail("NativeProceduralTextureSourceProvenanceLimit");
                if (sourceKeys.Add((extent.RelativeSource, extent.Offset, extent.Length, extent.PayloadSha256!)))
                    sourceBytes = checked(sourceBytes + extent.Length);
            }
            catch (OverflowException) { return Fail("NativeProceduralTextureSourceSizeOverflow"); }
        }
        if (sourceBytes > limits.MaximumSourceBytes)
            return Fail("NativeProceduralTextureSourceByteLimit");
        capturedExtents = Array.AsReadOnly(physicalSnapshot);

        var payload = frame.RawPayload.Span;
        var commonReferenceCount = BinaryPrimitives.ReadUInt32LittleEndian(payload[16..]);
        if (commonReferenceCount > limits.MaximumCommonPrefixReferences)
            return Fail("NativeProceduralTextureCommonReferenceLimit");
        long metadataEstimate;
        try
        {
            metadataEstimate = checked(FixedProjectionBytes + physicalCount * 16L +
                (long)commonReferenceCount * 128L + (TextureReferenceCount + 1L) * 192L + 2048L);
        }
        catch (OverflowException) { return Fail("NativeProceduralTextureMetadataSizeOverflow"); }
        if (metadataEstimate > limits.MaximumProjectionMetadataBytes)
            return Fail("NativeProceduralTextureMetadataLimit");

        token.ThrowIfCancellationRequested();

        if (!new NativeCommonPrefixReader().TryRead(frame, out var commonPrefix,
                out var prefixDiagnostic, token) || commonPrefix is null)
            return Fail("NativeProceduralTextureCommonPrefix:" + (prefixDiagnostic ?? "unavailable"));
        if (commonPrefix.ClassCount != 3 || commonPrefix.References.Count != commonReferenceCount ||
            commonPrefix.NextClassOffset < 32 ||
            commonPrefix.NextClassOffset > payload.Length - ClassTagBytes - ClassVersionBytes)
            return Fail("NativeProceduralTextureCommonPrefixMismatch");

        var classStart = commonPrefix.NextClassOffset;
        if (!payload.Slice(classStart, ClassTagBytes).SequenceEqual("PROCTEX_"u8))
            return Fail("NativeProceduralTextureTagUnsupported");
        if (ReadWord(payload, classStart + ClassTagBytes) != 7)
            return Fail("NativeProceduralTextureVersionUnsupported");

        var cursor = checked(classStart + ClassTagBytes + ClassVersionBytes);
        if (!TryWord(payload, ref cursor, frame.PayloadExtent, out var samplerWord, out var samplerExtent))
            return Fail("NativeProceduralTextureSamplerTruncated");
        if (!TryWord(payload, ref cursor, frame.PayloadExtent, out var outputModeWord, out var outputModeExtent))
            return Fail("NativeProceduralTextureOutputModeTruncated");
        if (!TryReference(payload, ref cursor, frame.PayloadExtent, frame.Identity, out var typedReference))
            return Fail("NativeProceduralTextureTypedReferenceTruncated");
        var aggregateControllerBytes = 0;
        if (!TryReadController(frame.RawPayload, payload, ref cursor, frame.PayloadExtent,
                limits, ref aggregateControllerBytes, out var powerController, out var controllerError, token))
            return Fail(controllerError);

        var references = new NativeProceduralTextureReferencePair[TextureReferenceCount];
        for (var index = 0; index < references.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            if (!TryReference(payload, ref cursor, frame.PayloadExtent, frame.Identity, out references[index]!))
                return Fail("NativeProceduralTextureReferenceTruncated");
        }
        if (!TryReadController(frame.RawPayload, payload, ref cursor, frame.PayloadExtent,
                limits, ref aggregateControllerBytes, out var speedController, out controllerError, token))
            return Fail(controllerError);
        if (cursor != payload.Length) return Fail("NativeProceduralTextureEndpointMismatch");

        token.ThrowIfCancellationRequested();
        var profileExtent = At(classStart, checked(cursor - classStart));
        var accounted = metadataEstimate;
        var document = new NativeProceduralTextureDocument(frame.Identity, revision, 7,
            frame.PayloadExtent, commonPrefix, profileExtent, samplerWord, samplerExtent,
            outputModeWord, outputModeExtent, typedReference, powerController!,
            Array.AsReadOnly(references), speedController!, capturedExtents, accounted,
            sourceProvenanceBytes, frame.RawPayload);
        var unresolved = new List<ResourceReference>(TextureReferenceCount + 1);
        if (typedReference.Reference is { } typed) unresolved.Add(typed);
        foreach (var referencePair in references)
        {
            token.ThrowIfCancellationRequested();
            if (referencePair.Reference is { } reference) unresolved.Add(reference);
        }
        token.ThrowIfCancellationRequested();
        return new(document, frame.Identity, revision, capturedExtents, steps, [], unresolved,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["classProfile"] = "PROCTEX v7",
                ["commonPrefixNextClassOffset"] = commonPrefix.NextClassOffset.ToString(CultureInfo.InvariantCulture),
                ["consumedBytes"] = cursor.ToString(CultureInfo.InvariantCulture)
            });

        DecodedExtent At(int offset, int length) => new(frame.PayloadExtent.Container,
            checked(frame.PayloadExtent.Offset + offset), length);
    }

    private static bool TryReadController(ReadOnlyMemory<byte> rawPayload, ReadOnlySpan<byte> payload,
        ref int cursor, DecodedExtent payloadExtent, NativeProceduralTextureReadLimits limits,
        ref int aggregateControllerBytes,
        out NativeProceduralTextureController? controller, out string diagnostic, CancellationToken token)
    {
        controller = null;
        diagnostic = "NativeProceduralTextureControllerTruncated";
        token.ThrowIfCancellationRequested();
        if (!TryWord(payload, ref cursor, payloadExtent, out var lengthWord, out var lengthExtent)) return false;
        var signedLength = unchecked((int)lengthWord);
        if (signedLength < 0)
        { diagnostic = "NativeProceduralTextureControllerLengthNegative"; return false; }
        if (signedLength > limits.MaximumControllerPayloadBytes)
        { diagnostic = "NativeProceduralTextureControllerPayloadLimit"; return false; }
        int updatedAggregate;
        try { updatedAggregate = checked(aggregateControllerBytes + signedLength); }
        catch (OverflowException)
        { diagnostic = "NativeProceduralTextureControllerAggregateSizeOverflow"; return false; }
        if (updatedAggregate > limits.MaximumAggregateControllerPayloadBytes)
        { diagnostic = "NativeProceduralTextureControllerAggregateLimit"; return false; }
        if (signedLength > payload.Length - cursor)
        { diagnostic = "NativeProceduralTextureControllerPayloadTruncated"; return false; }
        var payloadStart = cursor;
        var rawBytes = rawPayload.Slice(payloadStart, signedLength);
        aggregateControllerBytes = updatedAggregate;
        cursor = checked(cursor + signedLength);
        token.ThrowIfCancellationRequested();
        if (!TryWord(payload, ref cursor, payloadExtent, out var valueWord, out var valueExtent))
        { diagnostic = "NativeProceduralTextureControllerValueTruncated"; return false; }
        controller = new(lengthWord, lengthExtent, rawBytes,
            new(payloadExtent.Container, checked(payloadExtent.Offset + payloadStart), signedLength),
            valueWord, valueExtent);
        diagnostic = string.Empty;
        return true;
    }

    private static bool TryReference(ReadOnlySpan<byte> payload, ref int cursor,
        DecodedExtent payloadExtent, ResourceIdentity owner,
        out NativeProceduralTextureReferencePair pair)
    {
        pair = null!;
        var start = cursor;
        if (!TryWord(payload, ref cursor, payloadExtent, out var typeWord, out _) ||
            !TryWord(payload, ref cursor, payloadExtent, out var idWord, out _)) return false;
        var state = typeWord == 0 && idWord == 0
            ? NativeProceduralTextureReferenceState.NoTarget
            : typeWord is 0 or uint.MaxValue || idWord is 0 or uint.MaxValue
                ? NativeProceduralTextureReferenceState.UnboundRaw
                : NativeProceduralTextureReferenceState.Typed;
        ResourceReference? reference = state == NativeProceduralTextureReferenceState.Typed
            ? new(IdentifierNamespace.Native, idWord.ToString(CultureInfo.InvariantCulture), owner, typeWord)
            : null;
        pair = new(typeWord, idWord,
            new(payloadExtent.Container, checked(payloadExtent.Offset + start), 8), state, reference);
        return true;
    }

    private static bool TryWord(ReadOnlySpan<byte> payload, ref int cursor,
        DecodedExtent payloadExtent, out uint word, out DecodedExtent extent)
    {
        word = 0; extent = null!;
        if (cursor < 0 || cursor > payload.Length - 4) return false;
        var start = cursor;
        word = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(start, 4));
        cursor = checked(start + 4);
        extent = new(payloadExtent.Container, checked(payloadExtent.Offset + start), 4);
        return true;
    }

    private static bool ValidSourceIdentity(NativeObjectFrame frame, SourceRevision revision)
    {
        if (frame.Identity is null || frame.PayloadExtent is null || frame.PayloadExtent.Container is null ||
            string.IsNullOrWhiteSpace(frame.Identity.IndexContentId) || frame.Identity.IndexContentId.Length > 4096 ||
            frame.Identity.Ordinal < 0 || frame.DirectoryIndex < 0 ||
            string.IsNullOrWhiteSpace(revision.IndexContentId) || revision.IndexContentId.Length > 4096 ||
            string.IsNullOrWhiteSpace(revision.PhysicalSourceStamp) || revision.PhysicalSourceStamp.Length > 4096 ||
            revision.IndexContentId != frame.Identity.IndexContentId ||
            frame.PayloadExtent.Container.MemberPath is not null ||
            frame.PayloadExtent.Container.IndexContentId != frame.Identity.IndexContentId ||
            frame.PayloadExtent.Container.Ordinal != frame.Identity.Ordinal ||
            frame.PayloadExtent.Length != frame.RawPayload.Length || frame.PayloadExtent.Offset < 0 ||
            frame.PayloadExtent.Offset > long.MaxValue - frame.PayloadExtent.Length)
            return false;
        var expectedPath = string.Create(CultureInfo.InvariantCulture,
            $"dc-object/{frame.DirectoryIndex}/{NativeProceduralTextureReader.NativeType}/{frame.NativeId}");
        return frame.Identity.MemberPath is { Length: <= 4096 } path &&
            string.Equals(path, expectedPath, StringComparison.Ordinal);
    }

    private static bool ValidPhysicalExtent(SourceExtent extent)
    {
        if (extent is null || extent.RelativeSource is null || string.IsNullOrWhiteSpace(extent.RelativeSource) ||
            extent.RelativeSource.Length > 4096 || extent.RelativeSource.Contains(':') ||
            extent.RelativeSource.StartsWith('/') || extent.RelativeSource.StartsWith('\\') ||
            extent.RelativeSource.Split('/', '\\').Any(part => part is "" or "." or "..") ||
            extent.Offset < 0 || extent.Length < 0 || extent.Offset > long.MaxValue - extent.Length ||
            extent.PayloadSha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit)) return false;
        return true;
    }

    private static uint ReadWord(ReadOnlySpan<byte> payload, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
}
