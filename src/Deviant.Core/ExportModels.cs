namespace Deviant.Core;

public enum ExportStatus { Pending, Completed, Failed, Cancelled, Skipped }
public sealed record ExportOptions(string OutputDirectory, bool Overwrite = false);
public sealed record ExportItem(ResourceRecord Resource, string RelativeOutput);
public sealed record ExportPlan(ArchiveSnapshot Snapshot, ExportOptions Options, IReadOnlyList<ExportItem> Items);
public sealed record ExportProgress(long Ordinal, long BytesWritten, long Length, int CompletedFiles, int TotalFiles);
public sealed record ExportEntry(long Ordinal, uint Type, uint Id, uint Package, string SourceVolume,
    long Offset, long Length, string RelativeOutput, ExportStatus Status, long? CommittedLength = null,
    string? Sha256 = null, string? ErrorCode = null, string? Error = null);
public sealed record ExportManifest(int SchemaVersion, string IndexName, string IndexContentId,
    string PhysicalSourceStamp, string VerificationMethod, DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    bool Cancelled, IReadOnlyList<ExportEntry> Entries);
public sealed record ExportReport(ExportManifest Manifest, string ManifestPath);

public interface IExportManifestWriter
{
    Task WriteAsync(string path, ExportManifest manifest);
}

public interface IExportService
{
    ExportPlan Plan(ArchiveSnapshot snapshot, ExportOptions options, IEnumerable<ResourceRecord> selected);
    ExportPlan Plan(ArchiveSnapshot snapshot, ExportOptions options, IEnumerable<ResourceRecord> selected,
        CancellationToken cancellationToken, IProgress<ArchiveProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Plan(snapshot, options, selected);
    }
    Task<ExportReport> RunAsync(ExportPlan plan, IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
