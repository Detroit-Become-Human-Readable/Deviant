using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Deviant.Assets;

namespace Deviant.Formats.Containers;

/// <summary>Maps a DC_INFO v3 directory to the length-delimited DC_DATA v23 native-object payloads.</summary>
public sealed class NativeObjectDirectoryReader
{
    public const string Version = "texture-container-directory-v1";
    public const int MaximumBodyBytes = 256 * 1024 * 1024;
    public const int MaximumObjects = 131_072;
    public const int MaximumWrapperEntries = 4096;

    public DecodeResult<NativeObjectDirectoryDocument> Parse(ReadOnlySpan<byte> decodedDirectoryPrefix,
        ReadOnlyMemory<byte> logicalDcDataBody, ResourceIdentity container, SourceRevision revision,
        DecodedExtent logicalBodyExtent, IReadOnlyList<SourceExtent> originalPhysicalExtents,
        CancellationToken cancellationToken = default)
        => ParseCore(decodedDirectoryPrefix, logicalDcDataBody, container, revision, logicalBodyExtent,
            originalPhysicalExtents, false, cancellationToken);

    /// <summary>Reads every declared object frame, with the declared body retained separately from its omitted remainder.</summary>
    public DecodeResult<NativeObjectDirectoryDocument> ParseMetadata(ReadOnlySpan<byte> decodedDirectoryPrefix,
        ReadOnlyMemory<byte> completeObjectFrames, ResourceIdentity container, SourceRevision revision,
        DecodedExtent declaredLogicalBodyExtent, IReadOnlyList<SourceExtent> originalPhysicalExtents,
        CancellationToken cancellationToken = default)
        => ParseCore(decodedDirectoryPrefix, completeObjectFrames, container, revision, declaredLogicalBodyExtent,
            originalPhysicalExtents, true, cancellationToken);

    private DecodeResult<NativeObjectDirectoryDocument> ParseCore(ReadOnlySpan<byte> decodedDirectoryPrefix,
        ReadOnlyMemory<byte> logicalDcDataBody, ResourceIdentity container, SourceRevision revision,
        DecodedExtent logicalBodyExtent, IReadOnlyList<SourceExtent> originalPhysicalExtents,
        bool metadataOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(logicalBodyExtent);
        ArgumentNullException.ThrowIfNull(originalPhysicalExtents);
        var physical = originalPhysicalExtents.ToArray();
        DecodeResult<NativeObjectDirectoryDocument> Fail(string diagnostic) =>
            new(null, container, revision, physical,
                [new(nameof(NativeObjectDirectoryReader), Version, "little-endian; dc-info-v3; dc-data-v23")],
                [diagnostic], [], new Dictionary<string, string>());
        if (container.MemberPath is not null || revision.IndexContentId != container.IndexContentId ||
            logicalBodyExtent.Container != container || logicalBodyExtent.Offset < 0 ||
            (metadataOnly ? logicalBodyExtent.Length < logicalDcDataBody.Length : logicalBodyExtent.Length != logicalDcDataBody.Length) ||
            logicalBodyExtent.Offset > long.MaxValue - logicalBodyExtent.Length)
            return Fail("DirectorySourceIdentityMismatch");
        if (physical.Any(extent => extent.Offset < 0 || extent.Length < 0 || extent.Offset > long.MaxValue - extent.Length))
            return Fail("DirectoryPhysicalExtentInvalid");
        if ((!metadataOnly && logicalBodyExtent.Length > MaximumBodyBytes) || logicalBodyExtent.Length > uint.MaxValue ||
            logicalDcDataBody.Length > MaximumBodyBytes) return Fail("DirectoryBodyLimit");
        if (decodedDirectoryPrefix.Length < 24 || !decodedDirectoryPrefix[..8].SequenceEqual("DC_INFO "u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(decodedDirectoryPrefix[8..]) != 3)
            return Fail("DirectoryUnsupportedHeader");
        var directoryBodyLength = BinaryPrimitives.ReadUInt32LittleEndian(decodedDirectoryPrefix[12..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(decodedDirectoryPrefix[20..]);
        if (count > MaximumObjects || directoryBodyLength != 8L + count * 8L ||
            directoryBodyLength > decodedDirectoryPrefix.Length - 16)
            return Fail("DirectoryCountLengthMismatch");
        var directoryLength = checked(16 + (int)directoryBodyLength);
        if (decodedDirectoryPrefix.Length < directoryLength + 16 ||
            !decodedDirectoryPrefix.Slice(directoryLength, 8).SequenceEqual("DC_DATA "u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(decodedDirectoryPrefix[(directoryLength + 8)..]) != 23 ||
            BinaryPrimitives.ReadUInt32LittleEndian(decodedDirectoryPrefix[(directoryLength + 12)..]) != logicalBodyExtent.Length)
            return Fail("DirectoryDataHeaderMismatch");
        // The supplied body is the declared DC_DATA body, immediately after its 16-byte header.
        if (logicalBodyExtent.Offset != directoryLength + 16L)
            return Fail("DirectoryBodyOriginMismatch");

        var objects = new List<NativeObjectFrame>(checked((int)count));
        var cursor = 0;
        try
        {
            var body = logicalDcDataBody.Span;
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directoryOffset = checked(24 + (int)index * 8);
                var nativeType = BinaryPrimitives.ReadUInt32LittleEndian(decodedDirectoryPrefix[directoryOffset..]);
                var nativeId = BinaryPrimitives.ReadUInt32LittleEndian(decodedDirectoryPrefix[(directoryOffset + 4)..]);
                // Language-specific entries are outside this fixed-pair directory profile.
                if (nativeType is 4091 or 4092) return Fail("DirectoryLanguageEntryUnsupported");
                var headerStart = cursor;
                Require(body, cursor, 13);
                var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(body[cursor..]);
                var wrapperFlag = body[cursor + 4];
                var wrapperCount = BinaryPrimitives.ReadUInt32LittleEndian(body[(cursor + 5)..]);
                var wrapperKey = BinaryPrimitives.ReadUInt32LittleEndian(body[(cursor + 9)..]);
                if (wrapperFlag == 1 && wrapperCount > MaximumWrapperEntries) return Fail("DirectoryWrapperEntryLimit");
                var headerLength = GetFrameHeaderLength(body[cursor..]);
                Require(body, cursor, headerLength);
                cursor += headerLength;
                if (payloadLength > int.MaxValue) return Fail("DirectoryPayloadLengthLimit");
                Require(body, cursor, checked((int)payloadLength));
                var identity = new ResourceIdentity(container.IndexContentId, container.Ordinal,
                    "dc-object/" + index.ToString(CultureInfo.InvariantCulture) + "/" +
                    nativeType.ToString(CultureInfo.InvariantCulture) + "/" + nativeId.ToString(CultureInfo.InvariantCulture));
                var payload = logicalDcDataBody.Slice(cursor, checked((int)payloadLength));
                objects.Add(new(identity, checked((int)index), nativeType, nativeId,
                    new(container, directoryOffset, 8),
                    new(container, logicalBodyExtent.Offset + headerStart, cursor - headerStart),
                    new(container, logicalBodyExtent.Offset + cursor, payload.Length),
                    body.Slice(headerStart, cursor - headerStart).ToArray(), payload, wrapperFlag, wrapperCount,
                    wrapperKey, payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload.Span) : null));
                cursor += payload.Length;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var rawDirectory = decodedDirectoryPrefix[..directoryLength].ToArray();
            if (metadataOnly && cursor != logicalDcDataBody.Length) return Fail("DirectoryMetadataTrailingBytes");
            var remainder = logicalDcDataBody[cursor..];
            var value = new NativeObjectDirectoryDocument(container, revision,
                new(container, 0, directoryLength), logicalBodyExtent, rawDirectory, logicalDcDataBody,
                objects.AsReadOnly(), remainder.IsEmpty ? null : new(container, logicalBodyExtent.Offset + cursor, remainder.Length), remainder)
            {
                IsMetadataOnly = metadataOnly,
                DeclaredRemainderExtent = metadataOnly && logicalBodyExtent.Length > cursor
                    ? new(container, logicalBodyExtent.Offset + cursor, logicalBodyExtent.Length - cursor) : null
            };
            return new(value, container, revision, physical,
                [new(nameof(NativeObjectDirectoryReader), Version, "little-endian; dc-info-v3; dc-data-v23",
                    Convert.ToHexString(SHA256.HashData(logicalDcDataBody.Span)).ToLowerInvariant())], [], [],
                new Dictionary<string, string>());
        }
        catch (InvalidDataException) { return Fail("DirectoryFrameTruncated"); }
        catch (OverflowException) { return Fail("DirectoryFrameOverflow"); }
    }

    /// <summary>Returns the wrapper-header length; a 13-byte wrapper prefix requests at least 18 bytes.</summary>
    public static int GetFrameHeaderLength(ReadOnlySpan<byte> prefix)
    {
        Require(prefix, 0, 13);
        if (prefix[4] != 1) return 13;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(prefix[5..]);
        if (count > MaximumWrapperEntries) throw new InvalidDataException("DirectoryWrapperEntryLimit");
        if (prefix.Length < 18) return 18;
        var extra = prefix[17] == 1 ? checked(4 + 12 * (int)Math.Max(1u, count))
            : count > 1 ? checked(24 * (int)count) : 34;
        return checked(18 + extra);
    }

    private static void Require(ReadOnlySpan<byte> body, int cursor, int length)
    {
        if (cursor < 0 || length < 0 || cursor > body.Length - length)
            throw new InvalidDataException("The object frame extends beyond the declared DC_DATA body.");
    }
}
