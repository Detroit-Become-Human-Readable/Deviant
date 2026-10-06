using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Formats.Localization.Model;

public enum LocalizationRecordKind { Text, Pointer, Opaque }
public enum LocalizationRecordStatus { Complete, Unresolved, Malformed, Unsupported }
public sealed record LocalizationDiagnostic(string Code, string Message, string? ResourceIdentity = null);

public static class LocalizationSchema
{
    public const string AssetFamily = "localization.record";
    public const string ItemCollection = "localization.records";
    public const string ReaderVersion = "localization-v1";
    public static string CacheVersion => ReaderVersion + ":" +
        typeof(Deviant.Formats.Localization.LocalizationSnapshotReader).Assembly.ManifestModule.ModuleVersionId;
    public static string AssetIndexVersion => CacheVersion + ":asset-index-v2";
}

public sealed record LocalizationExtent(string Field, string RelativeSource, long Offset, long Length);

/// <summary>One source occurrence. Identity includes resource ordinal, record offset and key index, never a localized key.</summary>
public sealed record LocalizationRecord(
    ResourceIdentity Identity,
    ResourceIdentity ResourceIdentity,
    long ResourceOrdinal,
    uint ResourceType,
    uint Version,
    int LanguageIndex,
    string RawLanguageCode,
    string Language,
    long RecordOffset,
    int KeyIndex,
    LocalizationRecordKind Kind,
    LocalizationRecordStatus Status,
    string? Key,
    string? Text,
    string Encoding,
    LocalizationExtent RecordExtent,
    IReadOnlyList<LocalizationExtent> FieldExtents,
    IReadOnlyDictionary<string, string> UnknownFields,
    int? PointerKind = null,
    string? PointerPayloadBase64 = null,
    string? Diagnostic = null);

public sealed record LocalizationResourceResult(
    ResourceIdentity ResourceIdentity,
    long ResourceOrdinal,
    string RelativeSource,
    uint Version,
    IReadOnlyList<LocalizationRecord> Records,
    IReadOnlyList<LocalizationDiagnostic> Diagnostics,
    bool Supported)
{
    public string? InputSha256 { get; init; }
}

public sealed record LocalizationIndexSummary(IReadOnlyDictionary<string, long> KindCounts,
    IReadOnlyDictionary<string, long> StatusCounts, IReadOnlyDictionary<string, long> VersionCounts,
    IReadOnlyDictionary<string, long> DiagnosticCounts);

public sealed record LocalizationReadProgress(string Phase, long Completed, long Total, long Records, string? Detail = null);

public sealed record LocalizationQuery(
    string? Search = null,
    string? Language = null,
    ResourceIdentity? Source = null,
    LocalizationRecordKind? Kind = null,
    AssetAvailability? Availability = null,
    long Offset = 0,
    int Limit = 100,
    string? ExactKey = null,
    AssetSort Sort = AssetSort.Default,
    bool Descending = false);

public sealed record LocalizationPage(long Total, IReadOnlyList<LocalizationRecord> Records);

public sealed record LocalizationSessionUpdate(long Generation, bool IsComplete, long TotalResources,
    long CompletedResources, long RecordCount, string Phase, string? Detail = null);
