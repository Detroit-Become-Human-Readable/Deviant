using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Formats.Textures;

public sealed record TextureFileTextV24LodDescriptor(ushort Width, ushort Height, ushort ThirdU16,
    byte TrailingByte);

public sealed record TextureFileTextV24ResourceReference(int Sequence,
    ResourceReference Reference, ResourceIdentity CatalogOwner, uint FollowingUInt32,
    IReadOnlyList<int> CatalogReferenceIndices,
    ReferenceResolution Resolution);

public sealed record TextureFileTextV24Document(ResourceIdentity Identity, SourceRevision Revision,
    DecodedExtent TagExtent, DecodedExtent ExactConsumedDecodedExtent,
    byte[] RawFixedPrefix84, byte FirstByte, ushort Mip0Width, ushort Mip0Height, ushort ThirdU16,
    byte MipSizeCount, byte SizeGroupSelector, bool SkipLodSizeTables, uint NativeRegistryKey,
    byte SerializedSelector, byte SelectorLeadingByte, byte DescriptorCount,
    IReadOnlyList<TextureFileTextV24LodDescriptor> LodDescriptors,
    bool PostSelectorBoolean, byte PostSelectorByte0, byte PostSelectorByte1,
    IReadOnlyList<IReadOnlyList<uint>> MipByteLengthGroups,
    IReadOnlyList<TextureFileTextV24ResourceReference> References,
    IReadOnlyList<SourceExtent> OriginalPhysicalExtents);

/// <summary>Parses one bounded FILETEXT v24 record from an already extracted DC_DATA body.</summary>
public sealed class TextureFileTextV24Reader
{
    public const string Version = "texture-filetext-v24-reader-v1";
    public const int FixedPrefixLength = 84;
    public const int MaximumRecordBytes = 1024 * 1024;
    public const int MaximumMipSizeEntries = 16_384;
    public const int MaximumLodDescriptors = 255;

    private static ReadOnlySpan<byte> Tag => "FILETEXT"u8;

    public DecodeResult<TextureFileTextV24Document> Parse(ReadOnlySpan<byte> logicalDcDataBody,
        int tagOffset, int maximumRecordEndOffset, ResourceIdentity containerIdentity,
        SourceRevision revision, DecodedExtent logicalBodyExtent,
        IReadOnlyList<SourceExtent> originalPhysicalExtents, TextureCatalogMembership catalogMembership,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(containerIdentity);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(logicalBodyExtent);
        ArgumentNullException.ThrowIfNull(originalPhysicalExtents);
        ArgumentNullException.ThrowIfNull(catalogMembership);

        if (containerIdentity.MemberPath is not null || logicalBodyExtent.Container != containerIdentity ||
            revision.IndexContentId != containerIdentity.IndexContentId ||
            catalogMembership.Revision != revision || catalogMembership.CatalogIdentity.MemberPath is not null ||
            catalogMembership.CatalogIdentity.IndexContentId != revision.IndexContentId)
            return Failure(containerIdentity, revision, originalPhysicalExtents, "SourceIdentityMismatch",
                "The source extent, container identity, revision, or catalog membership belongs to another archive source.");

        if (originalPhysicalExtents.Any(extent => extent.Offset < 0 || extent.Length < 0 ||
            extent.Offset > long.MaxValue - extent.Length))
            return Failure(containerIdentity, revision, originalPhysicalExtents, "InvalidPhysicalExtent",
                "An original physical source extent is invalid.");
        if (catalogMembership.TextureReferences.Any(row => row.Reference.Namespace != IdentifierNamespace.Archive ||
            row.Reference.Owner != catalogMembership.CatalogIdentity ||
            row.Reference.ResourceType is not (2229 or 2230) ||
            !uint.TryParse(row.Reference.Id, NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            row.Extent.Length != 9 ||
            (row.Reference.ResourceType == 2229 && row.Kind != TextureCatalogEntryKind.Raw) ||
            (row.Reference.ResourceType == 2230 && row.Kind != TextureCatalogEntryKind.RawLod)))
            return Failure(containerIdentity, revision, originalPhysicalExtents, "InvalidCatalogMembership",
                "The selected membership contains an invalid or untyped texture CATALOG row.");

        if (logicalBodyExtent.Offset < 0 || logicalBodyExtent.Length != logicalDcDataBody.Length ||
            logicalBodyExtent.Offset > long.MaxValue - logicalBodyExtent.Length ||
            tagOffset < 0 || maximumRecordEndOffset < tagOffset || maximumRecordEndOffset > logicalDcDataBody.Length)
            return Failure(containerIdentity, revision, originalPhysicalExtents, "InvalidBounds",
                "The logical DC_DATA extent or record bounds are invalid.");

        if (maximumRecordEndOffset - tagOffset > MaximumRecordBytes)
            return Failure(containerIdentity, revision, originalPhysicalExtents, "RecordLimitExceeded",
                "The selected FILETEXT record exceeds the configured per-record limit.");

        var memberOffset = checked(logicalBodyExtent.Offset + tagOffset);
        var nestedIdentity = new ResourceIdentity(containerIdentity.IndexContentId, containerIdentity.Ordinal,
            "filetext/" + memberOffset.ToString(CultureInfo.InvariantCulture));
        cancellationToken.ThrowIfCancellationRequested();

        if (maximumRecordEndOffset - tagOffset < 12)
            return Failure(nestedIdentity, revision, originalPhysicalExtents, "TruncatedHeader",
                "The selected record does not contain the complete FILETEXT tag and version.");
        if (!logicalDcDataBody.Slice(tagOffset, Tag.Length).SequenceEqual(Tag))
            return Failure(nestedIdentity, revision, originalPhysicalExtents, "UnsupportedTag",
                "The selected serialized record is not FILETEXT.");
        var serializedVersion = BinaryPrimitives.ReadUInt32LittleEndian(logicalDcDataBody.Slice(tagOffset + 8, 4));
        if (serializedVersion != 24)
            return Failure(nestedIdentity, revision, originalPhysicalExtents, "UnsupportedVersion",
                $"FILETEXT version {serializedVersion} is not supported.");
        if (maximumRecordEndOffset - tagOffset < 12 + FixedPrefixLength)
            return Failure(nestedIdentity, revision, originalPhysicalExtents, "TruncatedFixedPrefix",
                "The FILETEXT v24 fixed prefix is incomplete.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = logicalDcDataBody.Slice(tagOffset, maximumRecordEndOffset - tagOffset);
            var cursor = 12;
            var rawPrefix = body.Slice(cursor, FixedPrefixLength).ToArray();
            cursor = checked(cursor + FixedPrefixLength);

            var firstByte = rawPrefix[0];
            var mip0Width = BinaryPrimitives.ReadUInt16LittleEndian(rawPrefix.AsSpan(1, 2));
            var mip0Height = BinaryPrimitives.ReadUInt16LittleEndian(rawPrefix.AsSpan(3, 2));
            var thirdU16 = BinaryPrimitives.ReadUInt16LittleEndian(rawPrefix.AsSpan(5, 2));
            var mipSizeCount = rawPrefix[7];
            var sizeGroupSelector = rawPrefix[12];
            var skipLodSizeTables = rawPrefix[13] == 1;
            var nativeRegistryKey = BinaryPrimitives.ReadUInt32LittleEndian(rawPrefix.AsSpan(16, 4));

            var serializedSelector = ReadByte(body, ref cursor, cancellationToken);
            var selectorLeadingByte = (byte)0;
            var descriptorCount = (byte)0;
            var lodDescriptors = new List<TextureFileTextV24LodDescriptor>();
            if (serializedSelector == 1)
            {
                selectorLeadingByte = ReadByte(body, ref cursor, cancellationToken);
                descriptorCount = ReadByte(body, ref cursor, cancellationToken);
                if (descriptorCount > MaximumLodDescriptors)
                    throw new ParseFailure("DescriptorLimitExceeded", "The FILETEXT v24 LOD descriptor count exceeds the configured limit.");
                Require(body, cursor, checked(descriptorCount * 7), cancellationToken);
                for (var i = 0; i < descriptorCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var descriptor = body.Slice(cursor, 7);
                    lodDescriptors.Add(new TextureFileTextV24LodDescriptor(
                        BinaryPrimitives.ReadUInt16LittleEndian(descriptor),
                        BinaryPrimitives.ReadUInt16LittleEndian(descriptor[2..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(descriptor[4..]), descriptor[6]));
                    cursor += 7; // Native memory uses an eight-byte stride; the stream consumes seven bytes.
                }
            }

            var postSelectorBoolean = ReadByte(body, ref cursor, cancellationToken) == 1;
            var postSelectorByte0 = ReadByte(body, ref cursor, cancellationToken);
            var postSelectorByte1 = ReadByte(body, ref cursor, cancellationToken);

            var groupCount = sizeGroupSelector == 2 ? 6 : 1;
            var totalSizeEntries = skipLodSizeTables ? 0L : checked((long)groupCount * mipSizeCount);
            if (totalSizeEntries > MaximumMipSizeEntries)
                throw new ParseFailure("MipSizeTableLimitExceeded", "The FILETEXT v24 mip-size tables exceed the configured entry limit.");
            Require(body, cursor, checked((int)(totalSizeEntries * sizeof(uint))), cancellationToken);
            var mipByteLengthGroups = new List<IReadOnlyList<uint>>(skipLodSizeTables ? 0 : groupCount);
            for (var group = 0; group < groupCount && !skipLodSizeTables; group++)
            {
                var sizes = new uint[mipSizeCount];
                for (var mip = 0; mip < mipSizeCount; mip++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sizes[mip] = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
                    cursor += 4;
                }
                mipByteLengthGroups.Add(Array.AsReadOnly(sizes));
            }

            var referenceCount = Math.Max(1, (int)descriptorCount);
            var referenceBytes = checked(referenceCount * 12);
            Require(body, cursor, referenceBytes, cancellationToken);
            var references = new List<TextureFileTextV24ResourceReference>(referenceCount);
            var matchedCatalogRows = new List<TextureCatalogReference>();
            for (var index = 0; index < referenceCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var type = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
                var id = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor + 4, 4));
                var followingUInt32 = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor + 8, 4));
                var rows = catalogMembership.TextureReferences.Where(row =>
                    row.Reference.Namespace == IdentifierNamespace.Archive &&
                    row.Reference.ResourceType == type &&
                    uint.TryParse(row.Reference.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var rowId) && rowId == id)
                    .ToArray();
                var candidates = rows.SelectMany(row => row.Candidates).Distinct().ToArray();
                var duplicate = rows.Length > 1 || rows.Any(row => row.IsDuplicate);
                var resolutionStatus = rows.Length switch
                {
                    0 => ReferenceStatus.Missing,
                    > 1 when duplicate => ReferenceStatus.Ambiguous,
                    1 when rows[0].ResolutionStatus == ReferenceStatus.Ambiguous || rows[0].IsDuplicate => ReferenceStatus.Ambiguous,
                    1 => rows[0].ResolutionStatus,
                    _ => ReferenceStatus.Ambiguous
                };
                var reference = new ResourceReference(IdentifierNamespace.Archive,
                    id.ToString(CultureInfo.InvariantCulture), nestedIdentity, type);
                var resolution = new ReferenceResolution(resolutionStatus, candidates,
                    resolutionStatus == ReferenceStatus.Unique ? null :
                        rows.Length == 0 ? "The native reference pair has no matching typed CATALOG row." :
                        "The native reference pair matches missing, ambiguous, or duplicate CATALOG evidence.");
                references.Add(new TextureFileTextV24ResourceReference(index, reference,
                    catalogMembership.CatalogIdentity, followingUInt32,
                    rows.Select(row => row.ReferenceIndex).ToArray(), resolution));
                matchedCatalogRows.AddRange(rows);
                cursor += 12;
            }

            if (cursor != body.Length)
                throw new ParseFailure("RecordBoundaryMismatch", "The parsed FILETEXT v24 fields do not end at the supplied record boundary.");

            var exactExtent = new DecodedExtent(containerIdentity, memberOffset, cursor);
            var tagExtent = new DecodedExtent(containerIdentity, memberOffset, 12);
            var document = new TextureFileTextV24Document(nestedIdentity, revision, tagExtent, exactExtent,
                rawPrefix, firstByte, mip0Width, mip0Height, thirdU16, mipSizeCount, sizeGroupSelector,
                skipLodSizeTables, nativeRegistryKey, serializedSelector, selectorLeadingByte, descriptorCount,
                lodDescriptors.AsReadOnly(), postSelectorBoolean, postSelectorByte0, postSelectorByte1,
                mipByteLengthGroups.AsReadOnly(), references.AsReadOnly(), originalPhysicalExtents.ToArray());
            var digest = Sha256(body);
            var extents = originalPhysicalExtents.Concat(matchedCatalogRows.Select(row => row.Extent)).Distinct().ToArray();
            var unresolved = references.Where(reference => reference.Resolution.Status != ReferenceStatus.Unique)
                .Select(reference => reference.Reference).ToArray();
            var diagnostics = references.Where(reference => reference.Resolution.Status != ReferenceStatus.Unique)
                .Select(reference => $"Reference {reference.Reference.ResourceType}/{reference.Reference.Id} is {reference.Resolution.Status} in the selected CATALOG membership.").ToArray();
            return new DecodeResult<TextureFileTextV24Document>(document, nestedIdentity, revision, extents,
                [new(nameof(TextureFileTextV24Reader), Version, "bounded-filetext-v24;catalog-reference-match", digest)],
                diagnostics, unresolved,
                new Dictionary<string, string>
                {
                    ["tag"] = "FILETEXT",
                    ["version"] = "24",
                    ["recordLength"] = cursor.ToString(CultureInfo.InvariantCulture),
                    ["nativeRegistryKey"] = nativeRegistryKey.ToString(CultureInfo.InvariantCulture),
                    ["format"] = "unmapped"
                });
        }
        catch (ParseFailure failure)
        {
            return Failure(nestedIdentity, revision, originalPhysicalExtents, failure.Code, failure.Message);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Failure(nestedIdentity, revision, originalPhysicalExtents, "TruncatedRecord",
                "The FILETEXT v24 record ends before a required field or table entry.");
        }
        catch (OverflowException)
        {
            return Failure(nestedIdentity, revision, originalPhysicalExtents, "RecordSizeOverflow",
                "A FILETEXT v24 field count overflows the bounded record size.");
        }
    }

    private static byte ReadByte(ReadOnlySpan<byte> body, ref int cursor, CancellationToken cancellationToken)
    {
        Require(body, cursor, 1, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return body[cursor++];
    }

    private static void Require(ReadOnlySpan<byte> body, int cursor, int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cursor < 0 || count < 0 || cursor > body.Length - count)
            throw new ParseFailure("TruncatedRecord", "The FILETEXT v24 record ends before a required field or table entry.");
    }

    private static DecodeResult<TextureFileTextV24Document> Failure(ResourceIdentity identity,
        SourceRevision revision, IReadOnlyList<SourceExtent> physicalExtents, string code, string message) =>
        new(null, identity, revision, physicalExtents.ToArray(),
            [new(nameof(TextureFileTextV24Reader), Version, "bounded-filetext-v24")],
            [$"Unsupported {code}: {message}"], [], new Dictionary<string, string>
            {
                ["status"] = "Unsupported",
                ["code"] = code
            });

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class ParseFailure(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
