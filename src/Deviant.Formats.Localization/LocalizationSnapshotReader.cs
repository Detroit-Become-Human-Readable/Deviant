using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Localization.Model;

namespace Deviant.Formats.Localization;

/// <summary>Reads bounded type-1016 localization records from one immutable archive snapshot.</summary>
public sealed class LocalizationSnapshotReader(ISourceRevisionProvider? revisionProvider = null)
{
    public const int MaximumResourceBytes = 16 * 1024 * 1024;
    private static readonly byte[] LocalizationMagic = "LOCALIZ_"u8.ToArray();
    private static readonly byte[] ContainerMagic = "COM_CONT"u8.ToArray();
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(false, false, true);

    public async Task<LocalizationResourceResult> ReadResourceAsync(ArchiveSnapshot snapshot, ResourceRecord resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.Type != 1016) throw new ArgumentException("The selected archive row is not a localization resource.", nameof(resource));
        var identity = new ResourceIdentity(snapshot.IndexContentId, resource.Ordinal);
        var volume = snapshot.Volumes.SingleOrDefault(candidate => candidate.Package == resource.Package);
        var relativeSource = volume?.RelativeName ?? resource.VolumeName;
        if (resource.Validity != ResourceValidity.Valid || volume is null || !volume.Stamp.Exists || volume.Stamp.Error is not null)
        {
            var message = resource.Validity != ResourceValidity.Valid ? resource.Validity.ToString() : "Localization source volume is unavailable.";
            var row = Opaque(snapshot.IndexContentId, resource, relativeSource, 0, Math.Max(0, resource.StoredSize), 0, -1,
                LocalizationRecordStatus.Malformed, "SourceUnavailable", message);
            return new(identity, resource.Ordinal, relativeSource, 0, [row], [new LocalizationDiagnostic("LocalizationSourceUnavailable", message, identity.Key)], false);
        }
        if (resource.StoredSize is <= 0 or > MaximumResourceBytes)
        {
            var message = resource.StoredSize <= 0 ? "Localization resource is empty." : $"Localization resource exceeds the {MaximumResourceBytes}-byte read limit.";
            var row = Opaque(snapshot.IndexContentId, resource, relativeSource, 0, Math.Max(0, resource.StoredSize), 0, -1,
                LocalizationRecordStatus.Unsupported, "ResourceSizeLimit", message);
            return new(identity, resource.Ordinal, relativeSource, 0, [row], [new LocalizationDiagnostic("LocalizationResourceSizeLimit", message, identity.Key)], false);
        }
        var resolver = new ArchiveResourceResolver(snapshot, revisionProvider);
        var bytes = await resolver.ReadAsync(identity, 0, checked((int)resource.StoredSize), cancellationToken).ConfigureAwait(false);
        var parsed = ParseResource(snapshot.IndexContentId, resource, relativeSource, bytes, cancellationToken);
        return parsed with { InputSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
    }

    public LocalizationResourceResult ParseResource(string indexContentId, ResourceRecord resource, string relativeSource,
        ReadOnlySpan<byte> bytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexContentId);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeSource);
        if (resource.Type != 1016) throw new ArgumentException("The selected archive row is not a localization resource.", nameof(resource));
        if (bytes.Length > MaximumResourceBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        var sourceIdentity = new ResourceIdentity(indexContentId, resource.Ordinal);
        var records = new List<LocalizationRecord>();
        var diagnostics = new List<LocalizationDiagnostic>();
        var markerOffsets = FindLocalizationMarkers(bytes);
        if (markerOffsets.Count == 0)
        {
            const string message = "The resource does not contain a LOCALIZ_ payload.";
            records.Add(Opaque(indexContentId, resource, relativeSource, 0, bytes.Length, 0, -1,
                LocalizationRecordStatus.Unsupported, "LocalizationSignatureMissing", message));
            diagnostics.Add(new("LocalizationSignatureMissing", message, sourceIdentity.Key));
            return new(sourceIdentity, resource.Ordinal, relativeSource, 0, records, diagnostics, false);
        }

        var candidateList = new List<LocalizationHeader>();
        var declaredBodyError = false;
        foreach (var candidateMarker in markerOffsets)
        {
            if (TryReadHeader(bytes, candidateMarker, out var header, out var candidateTruncated)) candidateList.Add(header);
            else declaredBodyError |= candidateTruncated;
        }
        var candidates = candidateList.ToArray();
        if (candidates.Length != 1)
        {
            var errorMarker = markerOffsets[0];
            var errorVersion = errorMarker <= bytes.Length - LocalizationMagic.Length - 4
                ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(errorMarker + LocalizationMagic.Length, 4)) : 0;
            var unsupportedVersion = markerOffsets.Count == 1 && errorVersion is not (5 or 6);
            var code = candidates.Length > 1 ? "MultipleLocalizationPayloadCandidates" :
                declaredBodyError ? "LocalizationBodyTruncated" : unsupportedVersion ? "UnsupportedLocalizationVersion" : "MalformedLocalizationHeader";
            var message = candidates.Length > 1
                ? "More than one bounded LOCALIZ_ payload candidate exists; no candidate was selected."
                : declaredBodyError ? "The declared localization body extends beyond the resource extent; the resource was preserved as opaque data."
                : unsupportedVersion ? $"Localization version {errorVersion} is not supported; its resource extent was preserved as opaque data."
                : "No LOCALIZ_ payload has a supported version and a fully bounded header.";
            records.Add(Opaque(indexContentId, resource, relativeSource, 0, bytes.Length, errorVersion, -1,
                candidates.Length > 1 || unsupportedVersion ? LocalizationRecordStatus.Unsupported : LocalizationRecordStatus.Malformed, code, message));
            diagnostics.Add(new(code, message, sourceIdentity.Key));
            return new(sourceIdentity, resource.Ordinal, relativeSource, errorVersion, records, diagnostics, false);
        }

        var selectedHeader = candidates[0];
        var marker = selectedHeader.Marker;
        var version = selectedHeader.Version;
        var languageCount = selectedHeader.LanguageCount;
        var bodyStart = selectedHeader.BodyStart;
        var declaredEnd = selectedHeader.BodyEnd;
        var bodyEnd = checked((int)declaredEnd);
        if (languageCount > 64)
        {
            const string message = "The localization language count exceeds the safety limit.";
            diagnostics.Add(new("LocalizationLanguageCountLimit", message, sourceIdentity.Key));
            records.Add(Opaque(indexContentId, resource, relativeSource, bodyStart, bodyEnd - bodyStart, version, -1,
                LocalizationRecordStatus.Malformed, "LocalizationLanguageCountLimit", message));
            return new(sourceIdentity, resource.Ordinal, relativeSource, version, records, diagnostics, false);
        }

        var pointerCapacity = TryParseContainerReferenceCount(bytes, out var references) ? references : 0;
        var cursor = bodyStart;
        var sawMalformedRecord = false;
        for (var languageIndex = 0; languageIndex < languageCount; languageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cursor > bodyEnd - 8)
            {
                const string message = "The declared language count exceeds the available language blocks.";
                diagnostics.Add(new("LocalizationLanguageCountMismatch", message, sourceIdentity.Key));
                if (cursor < bodyEnd)
                    records.Add(Opaque(indexContentId, resource, relativeSource, cursor, bodyEnd - cursor, version, languageIndex,
                        LocalizationRecordStatus.Malformed, "LocalizationLanguageCountMismatch", message));
                sawMalformedRecord = true;
                break;
            }
            var markerValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, 4));
            if (markerValue != 0x00000301)
            {
                const string message = "A language block has an invalid marker or truncated header.";
                diagnostics.Add(new("MalformedLocalizationLanguage", message, sourceIdentity.Key));
                records.Add(Opaque(indexContentId, resource, relativeSource, cursor, bodyEnd - cursor, version, languageIndex,
                    LocalizationRecordStatus.Malformed, "MalformedLocalizationLanguage", message));
                sawMalformedRecord = true;
                break;
            }
            var rawLanguage = DecodeLanguageCode(bytes.Slice(cursor + 5, 3));
            var displayLanguage = NormalizeLanguage(rawLanguage);
            cursor += 8;
            while (cursor <= bodyEnd - 8)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, 4)) == 0x00000301) break;
                var recordStart = cursor;
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, 4));
                var keyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor + 4, 4));
                cursor += 8;
                if (keyLength == 0) break;

                if (keyLength <= 256 && keyLength <= bodyEnd - cursor && IsAsciiKey(bytes.Slice(cursor, (int)keyLength)))
                {
                    var keyOffset = cursor;
                    var key = Encoding.ASCII.GetString(bytes.Slice(cursor, (int)keyLength));
                    cursor += (int)keyLength;
                    if (cursor > bodyEnd - 4)
                    {
                        records.Add(Opaque(indexContentId, resource, relativeSource, recordStart, bodyEnd - recordStart,
                            version, languageIndex, LocalizationRecordStatus.Malformed, "LocalizationTextLengthTruncated",
                            "A text byte length is truncated.", rawLanguage, displayLanguage, key,
                            [Extent("RecordHeader", resource, relativeSource, recordStart, 8), Extent("Key", resource, relativeSource, keyOffset, (int)keyLength)],
                            new Dictionary<string, string> { ["recordFlags"] = flags.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
                        diagnostics.Add(new("LocalizationTextLengthTruncated", "A text byte length is truncated.", sourceIdentity.Key));
                        sawMalformedRecord = true; break;
                    }
                    var lengthOffset = cursor;
                    var textLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, 4));
                    cursor += 4;
                    if ((textLength & 1) != 0 || textLength > bodyEnd - cursor)
                    {
                        var message = (textLength & 1) != 0 ? "The UTF-16 text byte length is odd." : "The UTF-16 text extends beyond the declared resource body.";
                        records.Add(Opaque(indexContentId, resource, relativeSource, recordStart, bodyEnd - recordStart,
                            version, languageIndex, LocalizationRecordStatus.Malformed,
                            (textLength & 1) != 0 ? "MalformedLocalizationTextLength" : "MalformedLocalizationText",
                            message, rawLanguage, displayLanguage, key,
                            [Extent("RecordHeader", resource, relativeSource, recordStart, 8), Extent("Key", resource, relativeSource, keyOffset, (int)keyLength), Extent("TextLength", resource, relativeSource, lengthOffset, 4)],
                            new Dictionary<string, string> { ["recordFlags"] = flags.ToString(System.Globalization.CultureInfo.InvariantCulture), ["declaredTextBytes"] = textLength.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
                        diagnostics.Add(new("MalformedLocalizationText", message, sourceIdentity.Key));
                        sawMalformedRecord = true; break;
                    }
                    var textOffset = cursor;
                    string? text = null;
                    string? decodeFailure = null;
                    try { text = StrictUtf16LittleEndian.GetString(bytes.Slice(cursor, (int)textLength)); }
                    catch (DecoderFallbackException ex) { decodeFailure = ex.Message; }
                    cursor += (int)textLength;
                    var fields = new[]
                    {
                        Extent("RecordHeader", resource, relativeSource, recordStart, 8),
                        Extent("Key", resource, relativeSource, keyOffset, (int)keyLength),
                        Extent("TextLength", resource, relativeSource, lengthOffset, 4),
                        Extent("Text", resource, relativeSource, textOffset, (int)textLength)
                    };
                    var unknown = new Dictionary<string, string>
                    {
                        ["recordFlags"] = flags.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    };
                    if (decodeFailure is not null)
                    {
                        unknown["decodeError"] = decodeFailure;
                        records.Add(CreateRecord(indexContentId, resource, relativeSource, version, languageIndex,
                            rawLanguage, displayLanguage, recordStart, keyIndex: 0, LocalizationRecordKind.Opaque,
                            LocalizationRecordStatus.Malformed, key, null, "UTF-16LE", Extent("Record", resource, relativeSource, recordStart, cursor - recordStart), fields,
                            unknown, diagnostic: "UTF-16 text contains an invalid surrogate sequence."));
                        diagnostics.Add(new("MalformedLocalizationUtf16", "UTF-16 text contains an invalid surrogate sequence.", sourceIdentity.Key));
                        sawMalformedRecord = true;
                    }
                    else
                        records.Add(CreateRecord(indexContentId, resource, relativeSource, version, languageIndex,
                            rawLanguage, displayLanguage, recordStart, keyIndex: 0, LocalizationRecordKind.Text,
                            LocalizationRecordStatus.Complete, key, text, "UTF-16LE", Extent("Record", resource, relativeSource, recordStart, cursor - recordStart), fields, unknown));
                }
                else if (keyLength is > 0 and <= 64 && keyLength <= pointerCapacity)
                {
                    if (!TryParsePointerRecord(indexContentId, resource, relativeSource, bytes, bodyEnd, version,
                        languageIndex, rawLanguage, displayLanguage, recordStart, cursor, flags, (int)keyLength,
                        out var pointerRows, out var pointerEnd, out var pointerDiagnostic))
                    {
                        var message = pointerDiagnostic ?? "The localization pointer record is malformed.";
                        records.Add(Opaque(indexContentId, resource, relativeSource, recordStart, bodyEnd - recordStart,
                            version, languageIndex, LocalizationRecordStatus.Malformed, "MalformedLocalizationPointer",
                            message, rawLanguage, displayLanguage,
                            fields: [Extent("RecordHeader", resource, relativeSource, recordStart, 8)],
                            unknown: new Dictionary<string, string>
                            {
                                ["recordFlags"] = flags.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ["pointerCount"] = keyLength.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            }));
                        diagnostics.Add(new("MalformedLocalizationPointer", message, sourceIdentity.Key));
                        sawMalformedRecord = true; break;
                    }
                    records.AddRange(pointerRows);
                    cursor = pointerEnd;
                }
                else
                {
                    var message = "An unknown localization record form was preserved as an opaque source extent.";
                    records.Add(Opaque(indexContentId, resource, relativeSource, recordStart, bodyEnd - recordStart,
                        version, languageIndex, LocalizationRecordStatus.Unsupported, "UnsupportedLocalizationRecord",
                        message, rawLanguage, displayLanguage, key: null,
                        fields: [Extent("RecordHeader", resource, relativeSource, recordStart, 8)],
                        unknown: new Dictionary<string, string>
                        {
                            ["recordFlags"] = flags.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["keyLengthOrPointerCount"] = keyLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["pointerCapacity"] = pointerCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        }));
                    diagnostics.Add(new("UnsupportedLocalizationRecord", message, sourceIdentity.Key));
                    sawMalformedRecord = true; break;
                }
            }
            if (sawMalformedRecord) break;
        }

        if (!sawMalformedRecord && cursor < bodyEnd)
        {
            const string message = "Trailing bytes after the declared localization language blocks were preserved as opaque data.";
            records.Add(Opaque(indexContentId, resource, relativeSource, cursor, bodyEnd - cursor, version, -1,
                LocalizationRecordStatus.Unsupported, "LocalizationTrailingBytes", message));
            diagnostics.Add(new("LocalizationTrailingBytes", message, sourceIdentity.Key));
        }
        if (declaredEnd < bytes.Length)
        {
            const string message = "Bytes after the declared localization body were preserved as an opaque source extent.";
            records.Add(Opaque(indexContentId, resource, relativeSource, bodyEnd, bytes.Length - bodyEnd, version, -1,
                LocalizationRecordStatus.Unsupported, "LocalizationBytesAfterBody", message));
            diagnostics.Add(new("LocalizationBytesAfterBody", message, sourceIdentity.Key));
        }
        return new(sourceIdentity, resource.Ordinal, relativeSource, version, records, diagnostics, !sawMalformedRecord);
    }

    private static bool TryParsePointerRecord(string indexContentId, ResourceRecord resource, string relativeSource,
        ReadOnlySpan<byte> bytes, int bodyEnd, uint version, int languageIndex, string rawLanguage, string displayLanguage,
        int recordStart, int cursor, uint flags, int pointerCount, out IReadOnlyList<LocalizationRecord> rows,
        out int next, out string? diagnostic)
    {
        var parsed = new List<LocalizationRecord>(pointerCount);
        var recordCursor = cursor;
        for (var keyIndex = 0; keyIndex < pointerCount; keyIndex++)
        {
            if (recordCursor > bodyEnd - 4) return PointerFailure(recordStart, "A pointer-key length is truncated.", out rows, out next, out diagnostic);
            var keyLengthOffset = recordCursor;
            var keyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(recordCursor, 4));
            recordCursor += 4;
            if (keyLength is 0 or > 256 || keyLength > bodyEnd - recordCursor || !IsAsciiKey(bytes.Slice(recordCursor, (int)keyLength)))
                return PointerFailure(recordStart, "A pointer key is invalid or truncated.", out rows, out next, out diagnostic);
            var keyOffset = recordCursor;
            var key = Encoding.ASCII.GetString(bytes.Slice(recordCursor, (int)keyLength));
            recordCursor += (int)keyLength;
            if (recordCursor >= bodyEnd) return PointerFailure(recordStart, "A pointer kind byte is truncated.", out rows, out next, out diagnostic);
            var kindOffset = recordCursor;
            var pointerKind = bytes[recordCursor++];
            var pointerLength = pointerKind switch { 0 => 0, 1 => 9, _ => -1 };
            if (pointerLength < 0 || pointerLength > bodyEnd - recordCursor)
                return PointerFailure(recordStart, "A pointer kind or payload extent is invalid.", out rows, out next, out diagnostic);
            var pointerOffset = recordCursor;
            var payload = pointerLength == 0 ? null : Convert.ToBase64String(bytes.Slice(pointerOffset, pointerLength));
            recordCursor += pointerLength;
            var fields = new[]
            {
                Extent("RecordHeader", resource, relativeSource, recordStart, 8),
                Extent("PointerKeyLength", resource, relativeSource, keyLengthOffset, 4),
                Extent("PointerKey", resource, relativeSource, keyOffset, (int)keyLength),
                Extent("PointerKind", resource, relativeSource, kindOffset, 1),
                Extent("PointerPayload", resource, relativeSource, pointerOffset, pointerLength)
            };
            parsed.Add(CreateRecord(indexContentId, resource, relativeSource, version, languageIndex,
                rawLanguage, displayLanguage, recordStart, keyIndex, LocalizationRecordKind.Pointer,
                LocalizationRecordStatus.Unresolved, key, null, "opaque-pointer", Extent("Record", resource, relativeSource, recordStart, 0), fields,
                new Dictionary<string, string>
                {
                    ["recordFlags"] = flags.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["pointerCount"] = pointerCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }, pointerKind, payload, "Pointer target is not interpreted."));
        }
        var fullLength = recordCursor - recordStart;
        rows = parsed.Select(row => row with { RecordExtent = Extent("Record", resource, relativeSource, recordStart, fullLength) }).ToArray();
        next = recordCursor;
        diagnostic = null;
        return true;
    }

    private static bool PointerFailure(int recordStart, string message, out IReadOnlyList<LocalizationRecord> rows,
        out int next, out string? diagnostic)
    {
        rows = [];
        next = recordStart;
        diagnostic = message;
        return false;
    }

    private static LocalizationRecord Opaque(string indexContentId, ResourceRecord resource, string relativeSource,
        long relativeOffset, long length, uint version, int languageIndex, LocalizationRecordStatus status,
        string diagnosticCode, string diagnostic, string rawLanguage = "", string displayLanguage = "", string? key = null,
        IReadOnlyList<LocalizationExtent>? fields = null, IReadOnlyDictionary<string, string>? unknown = null)
    {
        var safeOffset = Math.Clamp(relativeOffset, 0, Math.Max(0, resource.StoredSize));
        var safeLength = Math.Clamp(length, 0, Math.Max(0, resource.StoredSize - safeOffset));
        return CreateRecord(indexContentId, resource, relativeSource, version, languageIndex, rawLanguage, displayLanguage,
            safeOffset, 0, LocalizationRecordKind.Opaque, status, key, null, "opaque", Extent("Record", resource, relativeSource, safeOffset, safeLength),
            fields ?? [], unknown ?? new Dictionary<string, string>(), diagnostic: diagnostic, diagnosticCode: diagnosticCode);
    }

    private static LocalizationRecord CreateRecord(string indexContentId, ResourceRecord resource, string relativeSource,
        uint version, int languageIndex, string rawLanguage, string displayLanguage, long recordOffset, int keyIndex,
        LocalizationRecordKind kind, LocalizationRecordStatus status, string? key, string? text, string encoding,
        LocalizationExtent recordExtent, IReadOnlyList<LocalizationExtent> fields,
        IReadOnlyDictionary<string, string> unknown, int? pointerKind = null, string? pointerPayload = null,
        string? diagnostic = null, string? diagnosticCode = null)
    {
        var path = $"loc/v{version}/l{languageIndex}/r{recordOffset:D12}/k{keyIndex:D4}/{kind.ToString().ToLowerInvariant()}";
        var identity = new ResourceIdentity(indexContentId, resource.Ordinal, path);
        var allUnknown = new Dictionary<string, string>(unknown, StringComparer.Ordinal);
        if (diagnosticCode is not null) allUnknown["diagnosticCode"] = diagnosticCode;
        return new(identity, new ResourceIdentity(indexContentId, resource.Ordinal), resource.Ordinal, resource.Type, version,
            languageIndex, rawLanguage, displayLanguage, recordOffset, keyIndex, kind, status, key, text,
            encoding, recordExtent, fields, allUnknown, pointerKind, pointerPayload, diagnostic);
    }

    private static LocalizationExtent Extent(string field, ResourceRecord resource, string relativeSource, long relativeOffset, long length) =>
        new(field, relativeSource, checked(resource.Offset + relativeOffset), Math.Max(0, length));

    private static bool TryParseContainerReferenceCount(ReadOnlySpan<byte> bytes, out int count)
    {
        count = 0;
        if (bytes.Length < 20 || !bytes[..8].SequenceEqual(ContainerMagic)) return false;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..12]);
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]);
        var parsedCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..20]);
        if (version != 6 || parsedCount > 4096 || payloadLength > bytes.Length - 16 || 20L + parsedCount * 9L > bytes.Length) return false;
        count = checked((int)parsedCount);
        return true;
    }

    private static List<int> FindLocalizationMarkers(ReadOnlySpan<byte> bytes)
    {
        var markers = new List<int>();
        var cursor = 0;
        while (cursor <= bytes.Length - LocalizationMagic.Length)
        {
            var relative = bytes[cursor..].IndexOf(LocalizationMagic);
            if (relative < 0) break;
            var marker = cursor + relative;
            markers.Add(marker);
            cursor = marker + LocalizationMagic.Length;
        }
        return markers;
    }

    private static bool TryReadHeader(ReadOnlySpan<byte> bytes, int marker, out LocalizationHeader header, out bool declaredBodyTruncated)
    {
        header = default;
        declaredBodyTruncated = false;
        var start = marker + LocalizationMagic.Length;
        if (start > bytes.Length - sizeof(uint)) return false;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start, 4));
        int bodyStart;
        int declaredStart;
        uint bodyLength;
        uint languages;
        if (version == 6)
        {
            if (start > bytes.Length - 12) return false;
            bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start + 4, 4));
            languages = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start + 8, 4));
            declaredStart = start + 8;
            bodyStart = start + 12;
        }
        else if (version == 5)
        {
            if (start > bytes.Length - 13) return false;
            bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start + 4, 4));
            languages = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start + 9, 4));
            declaredStart = start + 8;
            bodyStart = start + 13;
        }
        else return false;
        if (bodyLength < bodyStart - declaredStart) return false;
        if (bodyLength > bytes.Length - declaredStart) { declaredBodyTruncated = true; return false; }
        header = new(marker, version, languages, bodyStart, checked(declaredStart + (int)bodyLength));
        return true;
    }

    private static bool IsAsciiKey(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return false;
        foreach (var item in value) if (item < 0x20 || item > 0x7E) return false;
        return true;
    }

    private static string DecodeLanguageCode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 3) return Convert.ToHexString(bytes);
        foreach (var value in bytes) if (value > 0x7F) return Convert.ToHexString(bytes);
        return Encoding.ASCII.GetString(bytes);
    }

    private static string NormalizeLanguage(string raw) => raw.Equals("JAP", StringComparison.OrdinalIgnoreCase)
        ? "JPN" : raw.ToUpperInvariant();

    private readonly record struct LocalizationHeader(int Marker, uint Version, uint LanguageCount, int BodyStart, int BodyEnd);
}
