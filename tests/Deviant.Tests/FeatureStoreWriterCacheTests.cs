using System.Reflection;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Tests;

public sealed class FeatureStoreWriterCacheTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-writer-cache-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void WriterCacheIsSharedByReferencesAndRestoresThePriorPragma()
    {
        using var store = FeatureStore.Open(Snapshot(), Path.Combine(root, "cache"));
        var previous = CacheSize(store);
        var budgetIdentity = new object();
        var charges = 0;
        var releases = 0;
        IDisposable? Reserve(long bytes)
        {
            Assert.Equal(16L * 1024 * 1024, bytes);
            charges++;
            return new CallbackLease(() => releases++);
        }

        var first = store.TryAcquireWriterCacheLease(budgetIdentity, Reserve);
        Assert.NotNull(first);
        Assert.Equal(-16 * 1024, CacheSize(store));
        var second = store.TryAcquireWriterCacheLease(budgetIdentity, Reserve);
        Assert.NotNull(second);
        Assert.Equal(1, charges);
        Assert.Throws<ArchiveException>(() => store.TryAcquireWriterCacheLease(new object(), Reserve));

        first.Dispose();
        Assert.Equal(-16 * 1024, CacheSize(store));
        Assert.Equal(0, releases);
        second.Dispose();
        Assert.Equal(previous, CacheSize(store));
        Assert.Equal(1, releases);
    }

    [Fact]
    public void WriterCacheAdmissionFailureLeavesPragmaAndBudgetUntouched()
    {
        using var store = FeatureStore.Open(Snapshot(), Path.Combine(root, "cache"));
        var previous = CacheSize(store);
        var charges = 0;
        var lease = store.TryAcquireWriterCacheLease(new object(), bytes =>
        {
            Assert.Equal(16L * 1024 * 1024, bytes);
            charges++;
            throw new ArchiveException("AssetMemoryLimit", "The generated budget declined the optional cache.");
        });

        Assert.Null(lease);
        Assert.Equal(1, charges);
        Assert.Equal(previous, CacheSize(store));
    }

    [Fact]
    public void WriterCacheLeaseCanBeReleasedAfterTheStoreCloses()
    {
        var store = FeatureStore.Open(Snapshot(), Path.Combine(root, "cache"));
        var releases = 0;
        var lease = store.TryAcquireWriterCacheLease(new object(), _ => new CallbackLease(() => releases++));
        Assert.NotNull(lease);

        store.Dispose();
        lease.Dispose();

        Assert.Equal(1, releases);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static int CacheSize(FeatureStore store)
    {
        var field = typeof(FeatureStore).GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var connection = (SqliteConnection)field.GetValue(store)!;
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA cache_size";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ArchiveSnapshot Snapshot()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), "deviant-writer-cache-source");
        return new(Path.Combine(sourceRoot, "sample.idx"), "writer-cache-index", IndexLayout.SupportedVersion,
            0, new byte[] { 0x41 }, 0, [], [], new FileStamp(true, "index", 1, 1, 1, true),
            "writer-cache-stamp", []);
    }

    private sealed class CallbackLease(Action release) : IDisposable
    {
        private Action? callback = release;
        public void Dispose() => Interlocked.Exchange(ref callback, null)?.Invoke();
    }
}
