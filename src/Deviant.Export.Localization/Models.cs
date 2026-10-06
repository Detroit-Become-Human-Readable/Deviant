using Deviant.Assets;
using Deviant.Formats.Localization.Model;

namespace Deviant.Export.Localization;

public enum LocalizationExportFormat { Json, Csv }
public enum LocalizationExportStatus { Completed, Failed, Cancelled }

public sealed record LocalizationExportSelection(
    IReadOnlyList<ResourceIdentity>? Identities = null,
    LocalizationQuery? MatchingQuery = null)
{
    public static LocalizationExportSelection Selected(IEnumerable<ResourceIdentity> identities) => new(identities.ToArray(), null);
    public static LocalizationExportSelection Matching(LocalizationQuery query) => new(null, query);
}

public sealed record LocalizationExportEntry(LocalizationRecord? Record, LocalizationExportStatus Status,
    string? ErrorCode = null, string? Diagnostic = null)
{
    public ResourceIdentity? Identity { get; init; }
}

public sealed record LocalizationExportManifest(int SchemaVersion, string SourceIndex, string IndexContentId,
    string PhysicalSourceStamp, LocalizationExportFormat Format, DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    bool Cancelled, long MatchedCount, long CompletedCount, long FailedCount, long CancelledCount,
    string? DataSha256, string? FailureDiagnostic = null);

public sealed record LocalizationExportReport(LocalizationExportManifest Manifest, string DataPath, string ManifestPath);

public sealed class LocalizationManifestWriteException(LocalizationExportManifest manifest, string manifestPath,
    string dataPath, Exception innerException)
    : IOException("LocalizationManifestWriteFailed: the data output was retained and the report is available in memory.", innerException)
{
    public LocalizationExportManifest Manifest { get; } = manifest;
    public string ManifestPath { get; } = manifestPath;
    public string DataPath { get; } = dataPath;
}
