using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Deviant.Core;

namespace Deviant.Formats.Localization.Model;

/// <summary>Stable compatibility lanes for localization cache data.</summary>
public static class LocalizationCacheContract
{
    public const string RecordSchemaVersion = "localization-record-schema-v1";
    /// <summary>
    /// Covers LOCALIZ_ framing and record interpretation, pointer parsing and resolution inputs,
    /// identity construction, malformed/unsupported preservation, language normalization, and
    /// every transitive dependency whose semantics affect those outputs. It excludes packaging,
    /// UI, search projection, archive source fencing, and unrelated assembly build changes.
    /// </summary>
    public const string ParserContractVersion = "localization-parser-v2-bounded-pointer-arrays";
    public const string JsonPolicyVersion = "system-text-json-default-v1";
    public const string SearchProjectionVersion = "localization-search-v1";
    public const string InputDigestVersion = "localization-input-sha256-v1";
    public const string AudioLookupContractVersion = "localization-audio-header-pointer-v2";

    public static string RecordStoreVersion => HashFields(
        "deviant-localization-record-store-v1",
        RecordSchemaVersion,
        ParserContractVersion,
        JsonPolicyVersion);

    public static string InputSnapshotId(string indexContentId,
        IEnumerable<LocalizationInputDigest> inputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexContentId);
        ArgumentNullException.ThrowIfNull(inputs);
        var inputRows = inputs.ToArray();
        if (inputRows.Any(input => input is null))
            throw new ArgumentException("Localization input rows cannot contain null entries.", nameof(inputs));
        var fields = new List<string> { NormalizeSha256(indexContentId, nameof(indexContentId)), InputDigestVersion };
        long previousOrdinal = -1;
        foreach (var input in inputRows.OrderBy(item => item.ResourceOrdinal))
        {
            if (input.ResourceOrdinal < 0 || input.ResourceOrdinal <= previousOrdinal)
                throw new ArgumentException("Localization input ordinals must be unique and nonnegative.", nameof(inputs));
            if (!Enum.IsDefined(input.Status))
                throw new ArgumentException("Every localization input requires a supported status.", nameof(inputs));
            if (input.Status == LocalizationInputStatus.Read && input.Sha256 is null)
                throw new ArgumentException("A successfully read localization input requires its digest.", nameof(inputs));
            if (input.Status != LocalizationInputStatus.Read && input.Sha256 is not null)
                throw new ArgumentException("Only successfully read localization inputs may carry a digest.", nameof(inputs));
            previousOrdinal = input.ResourceOrdinal;
            fields.Add(input.ResourceOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            fields.Add(input.Status.ToString());
            fields.Add(input.Sha256 is null ? string.Empty : NormalizeSha256(input.Sha256, nameof(inputs)));
        }
        return HashFields("deviant-localization-input-snapshot-v1", fields.ToArray());
    }

    public static string ParsedResultId(string inputSnapshotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputSnapshotId);
        return HashFields("deviant-localization-parsed-result-v1", inputSnapshotId, ParserContractVersion);
    }

    public static string AudioLookupVersion(string parsedResultId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parsedResultId);
        return HashFields("deviant-localization-audio-lookup-v1", AudioLookupContractVersion, parsedResultId);
    }

    private static string HashFields(string domain, params string[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFramed(hash, domain);
        foreach (var field in fields) AppendFramed(hash, field);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendFramed(IncrementalHash hash, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(length, checked((ulong)bytes.Length));
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static string NormalizeSha256(string value, string parameterName)
    {
        if (value.Length != 64) throw new ArgumentException("A SHA-256 value must contain 64 hexadecimal characters.", parameterName);
        try { return Convert.ToHexString(Convert.FromHexString(value)).ToLowerInvariant(); }
        catch (FormatException exception) { throw new ArgumentException("A SHA-256 value must contain 64 hexadecimal characters.", parameterName, exception); }
    }
}

/// <summary>Digest state for one localization resource in a completed input snapshot.</summary>
public enum LocalizationInputStatus { Read, InvalidResource, SourceUnavailable, Empty, SizeLimit }
public sealed record LocalizationInputDigest(long ResourceOrdinal, LocalizationInputStatus Status, string? Sha256);

/// <summary>Head bound to one accepted streamed localization generation.</summary>
public sealed record LocalizationGenerationHead(
    string IndexContentId,
    string PhysicalSourceStamp,
    FileStamp IndexStamp,
    IReadOnlyList<VolumeSource> SourceVolumes,
    string InputSnapshotId,
    string ParsedResultId,
    string RecordStoreVersion,
    string SearchProjectionVersion,
    string JsonPolicyVersion,
    string InputDigestVersion,
    int ResourceCount,
    long RecordCount,
    IReadOnlyList<LocalizationInputDigest> InputDigests,
    IReadOnlyDictionary<string, long> SourceCounts,
    IReadOnlyDictionary<string, long> KindCounts,
    IReadOnlyDictionary<string, long> StatusCounts,
    IReadOnlyDictionary<string, long> VersionCounts,
    IReadOnlyDictionary<string, long> DiagnosticCounts,
    IReadOnlyList<string> Languages);
