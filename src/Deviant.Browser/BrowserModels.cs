using Deviant.Core;

namespace Deviant.Browser;

public sealed record CatalogPage(long Total, IReadOnlyList<ResourceRecord> Rows);
public sealed record NavigationNode(string Label, uint? Package, uint? Type, long Count, IReadOnlyList<NavigationNode> Children);
public enum ExportScope { Selected, Matching }
public sealed record ExportReview(ArchiveSnapshot Snapshot, IReadOnlyList<ResourceRecord> Records, long Bytes, long InvalidCount);
public sealed record BrowserState
{
    public string? InputPath { get; init; }
    public ArchiveSnapshot? Snapshot { get; init; }
    public ResourceQuery Query { get; init; } = new(Limit: 500);
    public IReadOnlyList<ResourceRecord> Rows { get; init; } = [];
    public IReadOnlyList<NavigationNode> Navigation { get; init; } = [];
    public IReadOnlySet<long> Selected { get; init; } = new HashSet<long>();
    public ResourceRecord? Focused { get; init; }
    public long Total { get; init; }
    public bool IsOpening { get; init; }
    public bool IsQuerying { get; init; }
    public bool IsExporting { get; init; }
    public bool NeedsRefresh { get; init; }
    public bool IsClosing { get; init; }
    public string Status { get; init; } = "Ready";
    public string? Error { get; init; }
    public ArchiveProgress? ArchiveProgress { get; init; }
    public ExportProgress? ExportProgress { get; init; }
    public ExportReport? LastExport { get; init; }
    public string? ReportSourceRoot { get; init; }
    public ExportManifest? RetainedManifest { get; init; }
    public bool CanExport => Snapshot is not null && !NeedsRefresh && !IsExporting && !IsClosing && RetainedManifest is null;
}

public interface IUiDispatcher
{
    Task InvokeAsync(Action action);
}

public interface IBrowserCatalogSession : IAsyncDisposable
{
    ArchiveSnapshot Snapshot { get; }
    IDisposable AcquireLease() => EmptySessionLease.Instance;
    Task<CatalogPage> ReadPageAsync(ResourceQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ResourceRecord>> ReadMatchingAsync(ResourceQuery query,
        IProgress<ArchiveProgress>? progress = null, CancellationToken cancellationToken = default);
}

internal sealed class EmptySessionLease : IDisposable
{
    public static EmptySessionLease Instance { get; } = new();
    public void Dispose() { }
}
