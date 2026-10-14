using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Browser;

public sealed record CatalogReferenceProviderOptions(int MaximumBatchCatalogs = 16,
    int MaximumHotCatalogs = 128, long MaximumHotBytes = 32L * 1024 * 1024,
    int MaximumCachedTableBytes = 4 * 1024 * 1024);

public sealed record CatalogReferenceBatchRow(ResourceIdentity Identity,
    CatalogReferenceLease? Lease, string? Diagnostic);

/// <summary>Holds one immutable reference result and the memory charged for the caller's use.</summary>
public sealed class CatalogReferenceLease : IDisposable
{
    private LocalizationReferenceReadResult? result;
    private IDisposable? retention;

    public CatalogReferenceLease(LocalizationReferenceReadResult result, IDisposable? retention = null)
    {
        this.result = result ?? throw new ArgumentNullException(nameof(result));
        this.retention = retention;
    }

    public LocalizationReferenceReadResult Result => result ?? throw new ObjectDisposedException(nameof(CatalogReferenceLease));

    public void Dispose()
    {
        result = null;
        Interlocked.Exchange(ref retention, null)?.Dispose();
    }
}

public sealed class CatalogReferenceBatch : IDisposable
{
    private IReadOnlyList<AssetMemoryBudget.Reservation>? reservations;

    internal CatalogReferenceBatch(int startCatalogIndex, int nextCatalogIndex, int referenceRowsRead,
        bool isComplete, IReadOnlyList<CatalogReferenceBatchRow> rows,
        IReadOnlyList<AssetMemoryBudget.Reservation> reservations)
    {
        StartCatalogIndex = startCatalogIndex;
        NextCatalogIndex = nextCatalogIndex;
        ReferenceRowsRead = referenceRowsRead;
        IsComplete = isComplete;
        Rows = rows;
        this.reservations = reservations;
    }

    public int StartCatalogIndex { get; }
    public int NextCatalogIndex { get; }
    public int ReferenceRowsRead { get; }
    public bool IsComplete { get; }
    public IReadOnlyList<CatalogReferenceBatchRow> Rows { get; }

    public void Dispose()
    {
        var released = Interlocked.Exchange(ref reservations, null);
        if (released is not null)
            foreach (var reservation in released) reservation.Dispose();
        foreach (var row in Rows) row.Lease?.Dispose();
    }
}

/// <summary>Shares complete verified type-3 relationship tables for one archive revision.</summary>
public interface ICatalogReferenceProvider
{
    ArchiveSnapshot Snapshot { get; }
    SourceRevision Revision { get; }
    IReadOnlyList<ResourceIdentity> CatalogIdentities { get; }
    int MaximumBatchCatalogs { get; }
    AssetMemoryBudget SharedMemoryBudget { get; }
    Task<CatalogReferenceLease> ReadAsync(ResourceIdentity catalogIdentity,
        CancellationToken token = default);
    Task ValidateSnapshotAsync(CancellationToken token = default);
    Task<CatalogReferenceBatch> ReadBatchAsync(int startCatalogIndex, int maximumCatalogs,
        CancellationToken token = default);
    Task RevalidateAsync(LocalizationReferenceReadResult result, CancellationToken token = default);
}
