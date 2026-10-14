using Deviant.Assets;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Deviant.Formats.Containers;

/// <summary>Reconstructs a native directory through the shared container and nested-record readers.</summary>
public sealed class NativeObjectDirectoryLoader
{
    public const string DirectVersion = "native-directory-qzip-v1";
    public const string MetadataVersion = "native-directory-metadata-v1";

    /// <summary>Reads the complete object-frame prefix while checking the original logical container bounds.</summary>
    public DecodeResult<NativeObjectDirectoryDocument> ReadMetadata(ReadOnlyMemory<byte> logicalMetadata,
        long originalLogicalLength, ResourceIdentity identity, SourceRevision revision,
        IReadOnlyList<SourceExtent> physicalExtents, bool requireExactBodyLength = false,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(physicalExtents); token.ThrowIfCancellationRequested();
        DecodeResult<NativeObjectDirectoryDocument> Fail(string code) => new(null, identity, revision,
            physicalExtents, [], [code], [], new Dictionary<string, string>());
        if (originalLogicalLength < logicalMetadata.Length || logicalMetadata.Length < 40)
            return Fail("NativeMetadataLogicalBounds");
        var bytes = logicalMetadata.Span;
        if (!bytes[..8].SequenceEqual("DC_INFO "u8) || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != 3)
            return Fail("NativeDirectoryHeaderUnsupported");
        var directoryLength = 16L + BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        if (directoryLength < 24 || directoryLength > bytes.Length - 16)
            return Fail("NativeMetadataDirectoryBounds");
        var dataOffset = checked((int)directoryLength);
        if (!bytes.Slice(dataOffset, 8).SequenceEqual("DC_DATA "u8) || BinaryPrimitives.ReadUInt32LittleEndian(bytes[(dataOffset + 8)..]) != 23)
            return Fail("NativeDataHeaderUnsupported");
        var bodyOffset = checked(dataOffset + 16);
        var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(dataOffset + 12)..]);
        if (bodyLength > originalLogicalLength - bodyOffset ||
            requireExactBodyLength && bodyLength != originalLogicalLength - bodyOffset)
            return Fail("NativeMetadataDeclaredBodyBounds");
        var result = new NativeObjectDirectoryReader().ParseMetadata(bytes[..bodyOffset], logicalMetadata[bodyOffset..],
            identity, revision, new(identity, bodyOffset, bodyLength), physicalExtents, token);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return result with { Steps = [new(nameof(NativeObjectDirectoryLoader), MetadataVersion,
            "complete-object-frames;declared-body-validated;remainder-omitted", hash), .. result.Steps] };
    }

    public DecodeResult<NativeObjectDirectoryDocument> ReadDirectMetadata(ReadOnlyMemory<byte> storedMetadata,
        long originalStoredLength, ResourceIdentity identity, SourceRevision revision, SourceExtent metadataExtent,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(metadataExtent); token.ThrowIfCancellationRequested();
        if (storedMetadata.Length < 45 || !storedMetadata.Span[..5].SequenceEqual("QZIP\0"u8) ||
            metadataExtent.Length != storedMetadata.Length || originalStoredLength < storedMetadata.Length ||
            metadataExtent.Offset < 0 || metadataExtent.Offset > long.MaxValue - metadataExtent.Length)
            return new(null, identity, revision, [metadataExtent], [], ["NativeDirectMetadataSourceMismatch"], [], new Dictionary<string, string>());
        var hash = Convert.ToHexString(SHA256.HashData(storedMetadata.Span)).ToLowerInvariant();
        if (metadataExtent.PayloadSha256 is { } expected && !StringComparer.OrdinalIgnoreCase.Equals(hash, expected))
            return new(null, identity, revision, [metadataExtent], [], ["NativeDirectSourceHashMismatch"], [], new Dictionary<string, string>());
        return ReadMetadata(storedMetadata[5..], originalStoredLength - 5, identity, revision,
            [metadataExtent with { PayloadSha256 = hash }], true, token);
    }

    /// <summary>Reads a QZIP-wrapped native directory directly from its source memory.</summary>
    public DecodeResult<NativeObjectDirectoryDocument> ReadDirect(ReadOnlyMemory<byte> storedBytes,
        ResourceIdentity identity, SourceRevision revision, SourceExtent originalExtent, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(originalExtent); token.ThrowIfCancellationRequested();
        DecodeResult<NativeObjectDirectoryDocument> Fail(string diagnostic) =>
            new(null, identity, revision, [originalExtent], [], [diagnostic], [], new Dictionary<string, string>());
        if (identity.MemberPath is not null || identity.IndexContentId != revision.IndexContentId ||
            originalExtent.Offset < 0 || originalExtent.Length != storedBytes.Length ||
            originalExtent.Offset > long.MaxValue - originalExtent.Length || storedBytes.Length < 45 ||
            !storedBytes.Span[..5].SequenceEqual("QZIP\0"u8))
            return Fail("NativeDirectSourceMismatch");
        var sourceHash = Convert.ToHexString(SHA256.HashData(storedBytes.Span)).ToLowerInvariant();
        if (originalExtent.PayloadSha256 is { } expected && !StringComparer.OrdinalIgnoreCase.Equals(expected, sourceHash))
            return Fail("NativeDirectSourceHashMismatch");
        var logical = storedBytes[5..]; var bytes = logical.Span;
        if (!bytes[..8].SequenceEqual("DC_INFO "u8) || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != 3)
            return Fail("NativeDirectoryHeaderUnsupported");
        var directoryBodyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        var dataHeader = 16L + directoryBodyLength;
        if (directoryBodyLength < 8 || dataHeader > bytes.Length - 16)
            return Fail("NativeDirectDirectoryBounds");
        var at = checked((int)dataHeader);
        if (!bytes.Slice(at, 8).SequenceEqual("DC_DATA "u8) || BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 8)..]) != 23)
            return Fail("NativeDataHeaderUnsupported");
        var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 12)..]);
        var bodyOffset = checked(at + 16);
        if (bodyLength > NativeObjectDirectoryReader.MaximumBodyBytes || bodyLength > bytes.Length - bodyOffset)
            return Fail("NativeDirectBodyBounds");
        if (bodyLength != bytes.Length - bodyOffset)
            return Fail("NativeDirectTrailingBytes");
        var extent = originalExtent with { PayloadSha256 = sourceHash };
        var parsed = new NativeObjectDirectoryReader().Parse(bytes[..bodyOffset],
            logical.Slice(bodyOffset, checked((int)bodyLength)), identity, revision,
            new(identity, bodyOffset, bodyLength), [extent], token);
        var step = new DecodeStep(nameof(NativeObjectDirectoryLoader), DirectVersion,
            $"QZIP;skip5;DC_INFO-v3;DC_DATA-v23;body={bodyOffset}:{bodyLength}", sourceHash);
        token.ThrowIfCancellationRequested();
        return parsed with { Steps = [step, .. parsed.Steps] };
    }

    public DecodeResult<NativeObjectDirectoryDocument> Read(DecodeResult<SegsDocument> container,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        token.ThrowIfCancellationRequested();
        DecodeResult<NativeObjectDirectoryDocument> Fail(string diagnostic, IReadOnlyList<DecodeStep>? steps = null) =>
            new(null, container.Identity, container.Revision, container.Extents, steps ?? container.Steps,
                [diagnostic], container.UnresolvedReferences, container.UnknownFields);
        if (container.Value is not { } decoded)
            return Fail("NativeContainerUnavailable");
        var records = new RawNestedRecordReader();
        var info = records.Inspect(container, 0, token);
        if (info.Value is not { TagText: "DC_INFO ", Version: 3 } directoryHeader)
            return Fail(info.Diagnostics.FirstOrDefault() ?? "NativeDirectoryHeaderUnsupported", info.Steps);
        var dataOffset = checked(16L + directoryHeader.DeclaredBodyLength);
        var data = records.Inspect(container, dataOffset, token);
        if (data.Value is not { TagText: "DC_DATA ", Version: 23 } dataHeader)
            return Fail(data.Diagnostics.FirstOrDefault() ?? "NativeDataHeaderUnsupported", data.Steps);
        if (dataHeader.DeclaredBodyLength > NativeObjectDirectoryReader.MaximumBodyBytes)
            return Fail("DirectoryBodyLimit", data.Steps);
        var body = new byte[checked((int)dataHeader.DeclaredBodyLength)];
        var written = 0;
        foreach (var slice in dataHeader.BodySlices)
        {
            token.ThrowIfCancellationRequested();
            if (slice.BodyOffset != written || slice.Bytes.Length > body.Length - written)
                return Fail("NativeBodySliceMismatch", data.Steps);
            for (var offset = 0; offset < slice.Bytes.Length;)
            {
                token.ThrowIfCancellationRequested();
                var length = Math.Min(128 * 1024, slice.Bytes.Length - offset);
                slice.Bytes.Slice(offset, length).CopyTo(body.AsMemory(written, length));
                offset += length; written += length;
            }
        }
        if (written != body.Length) return Fail("NativeBodySliceMismatch", data.Steps);
        var parsed = new NativeObjectDirectoryReader().Parse(decoded.DecodedPrefix, body,
            container.Identity, container.Revision, new(container.Identity, checked(dataOffset + 16), body.Length),
            container.Extents, token);
        return parsed with { Steps = [.. data.Steps, .. parsed.Steps] };
    }
}
