using System.Globalization;
using Deviant.Core;
using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

public sealed partial class FeatureStore
{
    private const long WriterCacheReservationBytes = 16L * 1024 * 1024;
    private const int WriterCacheSizeKiB = 16 * 1024;
    private readonly object writerCacheLeaseGate = new();
    private object? writerCacheBudgetIdentity;
    private IDisposable? writerCacheMemoryReservation;
    private int? previousWriterCacheSize;
    private int writerCacheLeaseCount;

    /// <summary>Optionally reserves and tunes the shared SQLite page cache for a scoped session.</summary>
    /// <returns>A shared lease, or null when the store is closed or the supplied budget declines admission.</returns>
    public IDisposable? TryAcquireWriterCacheLease(object budgetIdentity,
        Func<long, IDisposable?> reserveMemory)
    {
        ArgumentNullException.ThrowIfNull(budgetIdentity);
        ArgumentNullException.ThrowIfNull(reserveMemory);
        lock (writerCacheLeaseGate)
        {
            lock (gate)
                if (connection.State != System.Data.ConnectionState.Open) return null;

            if (writerCacheLeaseCount > 0)
            {
                if (!ReferenceEquals(writerCacheBudgetIdentity, budgetIdentity))
                    throw new ArchiveException("FeatureStoreWriterCacheBudgetMismatch",
                        "All sessions sharing this feature store must share one CPU memory budget.");
                writerCacheLeaseCount++;
                return new WriterCacheLease(this);
            }

            IDisposable? reservation;
            try { reservation = reserveMemory(WriterCacheReservationBytes); }
            catch (ArchiveException error) when (error.Code == "AssetMemoryLimit") { return null; }
            if (reservation is null) return null;

            int previous;
            try
            {
                lock (gate)
                {
                    if (connection.State != System.Data.ConnectionState.Open)
                    { reservation.Dispose(); return null; }
                    previous = ReadCacheSize();
                    SetCacheSize(-WriterCacheSizeKiB);
                }
            }
            catch
            {
                reservation.Dispose();
                throw;
            }

            writerCacheBudgetIdentity = budgetIdentity;
            writerCacheMemoryReservation = reservation;
            previousWriterCacheSize = previous;
            writerCacheLeaseCount = 1;
            return new WriterCacheLease(this);
        }
    }

    private int ReadCacheSize()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA cache_size";
        return checked(Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    private void SetCacheSize(int size)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA cache_size=" + size.ToString(CultureInfo.InvariantCulture);
        command.ExecuteNonQuery();
    }

    private void ReleaseWriterCacheLease()
    {
        lock (writerCacheLeaseGate)
        {
            if (writerCacheLeaseCount == 0) return;
            if (--writerCacheLeaseCount != 0) return;

            var reservation = writerCacheMemoryReservation;
            var previous = previousWriterCacheSize;
            writerCacheMemoryReservation = null;
            previousWriterCacheSize = null;
            writerCacheBudgetIdentity = null;
            try
            {
                lock (gate)
                    if (previous is { } cacheSize && connection.State == System.Data.ConnectionState.Open)
                        SetCacheSize(cacheSize);
            }
            finally { reservation?.Dispose(); }
        }
    }

    private sealed class WriterCacheLease(FeatureStore owner) : IDisposable
    {
        private FeatureStore? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.ReleaseWriterCacheLease();
    }
}
