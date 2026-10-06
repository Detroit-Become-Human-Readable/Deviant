using System.Buffers.Binary;
using System.IO;
using Deviant.Browser;
using Deviant.Core;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class CatalogSessionTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "DeviantCatalogSessionTests", Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "source");
    private string Cache => Path.Combine(root, "cache");
    private string Index => Path.Combine(Source, "sample.idx");

    [Fact]
    public async Task SessionPagesOnOneBackgroundOwnerThreadAndDrainsOnDispose()
    {
        WriteSyntheticArchive();
        var callerThread = Environment.CurrentManagedThreadId;
        var progress = new RecordingProgress();
        await using var session = await CatalogSession.OpenAsync(Index, Cache, progress);

        Assert.Equal(4, session.Snapshot.Records.Count);
        Assert.Contains(progress.Values, value => value.Phase == "Building catalog index");
        var page = await session.ReadPageAsync(new ResourceQuery(Type: 7, Limit: 1));
        Assert.Equal(3, page.Total);
        Assert.Single(page.Rows);
        var workerThread = await session.InvokeAsync(_ => Environment.CurrentManagedThreadId);
        var repeatedWorkerThread = await session.InvokeAsync(_ => Environment.CurrentManagedThreadId);
        Assert.NotEqual(callerThread, workerThread);
        Assert.Equal(workerThread, repeatedWorkerThread);

        var pages = await Task.WhenAll(
            session.ReadPageAsync(new ResourceQuery(Type: 7, Limit: 10)),
            session.ReadPageAsync(new ResourceQuery(Package: 1, Limit: 10)));
        Assert.Equal(3, pages[0].Rows.Count);
        Assert.Single(pages[1].Rows);

        await session.DisposeAsync();
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => session.ReadPageAsync(new ResourceQuery()));
    }

    [Fact]
    public async Task MatchingRowsAreReadInCancelablePagesWithProgress()
    {
        WriteSyntheticArchive();
        await using var session = await CatalogSession.OpenAsync(Index, Cache);
        var progress = new RecordingProgress();

        var matching = await session.ReadMatchingAsync(new ResourceQuery(Package: 0), progress);

        Assert.Equal(3, matching.Count);
        Assert.Contains(progress.Values, value => value.Phase == "Reading matching resources" && value.Completed == value.Total);

        using var cancellation = new CancellationTokenSource();
        var cancelProgress = new InlineProgress(value =>
        {
            if (value.Phase == "Reading matching resources" && value.Completed > 0) cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.ReadMatchingAsync(new ResourceQuery(Package: 0), cancelProgress, cancellation.Token));
    }

    [Fact]
    public async Task SessionDisposalWaitsForMatchingSelectionLease()
    {
        WriteSyntheticArchive();
        var session = await CatalogSession.OpenAsync(Index, Cache);
        var lease = session.AcquireLease();

        var disposal = session.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        Assert.Equal(4, (await session.ReadPageAsync(new ResourceQuery(Limit: 10))).Total);

        lease.Dispose();
        await disposal;
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => session.ReadPageAsync(new ResourceQuery()));
    }

    [Fact]
    public async Task WorkerDoesNotRunQueuedCancelledWorkAndDisposesOwnedCatalogOnWorker()
    {
        var worker = new SerializedWorker();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var first = worker.InvokeAsync(() =>
        {
            entered.Set();
            release.Wait();
            return Environment.CurrentManagedThreadId;
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        var ran = 0;
        var queued = worker.InvokeAsync(() => Interlocked.Increment(ref ran), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        release.Set();
        var ownerThread = await first;
        Assert.Equal(ownerThread, await worker.InvokeAsync(() => Environment.CurrentManagedThreadId));

        var disposeThread = 0;
        await worker.StopAsync(() => disposeThread = Environment.CurrentManagedThreadId);
        Assert.Equal(0, ran);
        Assert.Equal(ownerThread, disposeThread);
    }

    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(root)) await Task.Run(() => Directory.Delete(root, recursive: true));
    }

    private void WriteSyntheticArchive()
    {
        Directory.CreateDirectory(Source);
        var rows = new (uint Type, uint Flags, uint Id, uint Offset, uint Size, uint Alt, uint Package)[]
        {
            (7, 1, 101, 0, 4, 0, 0), (8, 1, 102, 4, 4, 0, 0),
            (7, 2, 103, 8, 4, 0, 0), (7, 3, 104, 0, 4, 0, 1)
        };
        var bytes = new byte[IndexLayout.HeaderLength + rows.Length * IndexLayout.RecordLength];
        IndexLayout.Magic.CopyTo(bytes);
        Put(bytes, 20, IndexLayout.SupportedVersion);
        Put(bytes, 24, 42);
        Put(bytes, 101, (uint)rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var fields = new[] { row.Type, row.Flags, row.Id, row.Offset, row.Size, row.Alt, row.Package };
            for (var field = 0; field < fields.Length; field++) Put(bytes, IndexLayout.HeaderLength + i * IndexLayout.RecordLength + field * 4, fields[field]);
        }
        File.WriteAllBytes(Index, bytes);
        File.WriteAllBytes(Path.Combine(Source, "sample.dat"), new byte[12]);
        File.WriteAllBytes(Path.Combine(Source, "sample.d01"), new byte[4]);
    }

    private static void Put(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);

    private sealed class RecordingProgress : IProgress<ArchiveProgress>
    {
        public List<ArchiveProgress> Values { get; } = [];
        public void Report(ArchiveProgress value) => Values.Add(value);
    }

    private sealed class InlineProgress(Action<ArchiveProgress> report) : IProgress<ArchiveProgress>
    { public void Report(ArchiveProgress value) => report(value); }
}
