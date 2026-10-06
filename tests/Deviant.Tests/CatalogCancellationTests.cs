using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Tests;

public sealed class CatalogCancellationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "DeviantCatalogCancellation", Guid.NewGuid().ToString("N"));
    private string Cache => Path.Combine(root, "cache");

    [Fact]
    public void OpenReportsBuildProgressAndCancellationLeavesCacheReusable()
    {
        var snapshot = CreateSnapshot(12_000);
        using var cancellation = new CancellationTokenSource();
        var phases = new List<ArchiveProgress>();
        var progress = new InlineProgress<ArchiveProgress>(value =>
        {
            phases.Add(value);
            if (value.Phase == "Building catalog index" && value.Completed >= 8192) cancellation.Cancel();
        });

        Assert.ThrowsAny<OperationCanceledException>(() => CatalogService.Open(snapshot, Cache, progress, cancellation.Token));
        Assert.Contains(phases, value => value.Phase == "Building catalog index" && value.Completed >= 8192);

        using var reopened = CatalogService.Open(snapshot, Cache);
        Assert.False(reopened.ReusedIndexRows);
        Assert.Equal(snapshot.Records.Count, reopened.Count(new ResourceQuery(Limit: 100)));
    }

    [Fact]
    public void CancellationInterruptsAnActiveSqliteCacheValidationQuery()
    {
        var snapshot = CreateSnapshot(12_000);
        using (CatalogService.Open(snapshot, Cache)) { }
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<ArchiveProgress>(value =>
        {
            if (value.Phase == "Validating cached catalog" && value.Completed >= 8192) cancellation.Cancel();
        });

        var exception = Assert.ThrowsAny<OperationCanceledException>(() =>
            CatalogService.Open(snapshot, Cache, progress, cancellation.Token));
        Assert.IsType<SqliteException>(exception.InnerException);

        using var reopened = CatalogService.Open(snapshot, Cache);
        Assert.True(reopened.ReusedIndexRows);
        Assert.Equal(snapshot.Records.Count, reopened.Count(new ResourceQuery(Limit: 100)));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private ArchiveSnapshot CreateSnapshot(int count)
    {
        var source = Path.Combine(root, "source");
        var records = Enumerable.Range(0, count).Select(i => new ResourceRecord(i, (uint)(i % 37), 1,
            (uint)i, 0, i, 1, 0, "sample.dat")).ToArray();
        var stamp = new FileStamp(true, "synthetic", count + 1, 1, 2, true);
        return new ArchiveSnapshot(Path.Combine(source, "sample.idx"), "content-" + Guid.NewGuid().ToString("N"),
            IndexLayout.SupportedVersion, 0, new byte[73], (uint)count, records,
            [new VolumeSource(0, "sample.dat", Path.Combine(source, "sample.dat"), stamp)],
            stamp, "physical-synthetic", []);
    }
}
