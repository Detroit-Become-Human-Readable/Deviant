using Deviant.Catalog;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Tests;

public sealed class TemporaryStorageAdmissionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-temporary-storage-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AnonymousDatabaseRetainsNativePlacementWhileSharingTheCacheLimit()
    {
        using var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 512 * 1024));
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = "", Vfs = CacheStorageBudget.ForRoot(cache.Location).Vfs, Pooling = false }.ToString()))
        {
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = "PRAGMA cache_size=1; CREATE TABLE entries(payload BLOB); INSERT INTO entries VALUES(zeroblob(65536));";
            command.ExecuteNonQuery();
            Assert.True(cache.UsageBytes >= 65536);
            Assert.Empty(Directory.EnumerateFiles(cache.Location, "*", SearchOption.AllDirectories));
            await cache.ReconcileAsync();
            Assert.True(cache.UsageBytes >= 65536);
            command.CommandText = "INSERT INTO entries VALUES(zeroblob(1048576));";
            var failure = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
            Assert.Equal(13, failure.SqliteErrorCode);
            command.CommandText = "SELECT COUNT(*) FROM entries;";
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
            Assert.True(cache.UsageBytes <= cache.LimitBytes);
        }
        Assert.Equal(0, cache.UsageBytes);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
