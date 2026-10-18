using System.Security.Cryptography;
using Deviant.Catalog;
using Xunit;

namespace Deviant.Tests;

public sealed class CacheArtifactOutputTests : IDisposable
{
    private const int ChunkBytes = 128 * 1024;
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-artifact-output-" + Guid.NewGuid().ToString("N"));
    private string Location => Path.Combine(root, "cache");
    private CacheManager Manager(string name, long limit) => new(Path.Combine(root, name), new(Location, limit));
    private CacheArtifactOutput Output(string name) => new(Location, Path.Combine(Location, name));
    private long PhysicalBytes() => Directory.EnumerateFiles(Location, "*", SearchOption.AllDirectories)
        .Sum(path => new FileInfo(path).Length);

    [Fact]
    public async Task RestrictiveManagerRejectsTheNextChunkBeforeItGrowsTheFile()
    {
        using var permissive = Manager("first-settings", 2 * 1024 * 1024);
        using var restrictive = Manager("second-settings", 192 * 1024);
        var output = Output("first.data");
        await using var stream = output.OpenWrite();
        var bytes = new byte[2 * ChunkBytes];
        Array.Fill(bytes, (byte)0x41);

        var error = await Assert.ThrowsAsync<IOException>(() => stream.WriteAsync(bytes.AsMemory()).AsTask());

        Assert.Equal("CacheStorageLimit", error.Data["ErrorCode"]);
        Assert.Equal((long)ChunkBytes, error.Data["RequestedBytes"]);
        Assert.Equal(ChunkBytes, stream.Length);
        Assert.Equal(ChunkBytes, new FileInfo(output.Path).Length);
        Assert.Equal(ChunkBytes, PhysicalBytes());
        Assert.True(PhysicalBytes() <= restrictive.LimitBytes);
        stream.Position = 0;
        var retained = new byte[ChunkBytes];
        await stream.ReadExactlyAsync(retained);
        Assert.Equal(bytes.AsSpan(0, ChunkBytes).ToArray(), retained);

        // In-flight bytes remain charged even before the first writer closes.
        await using var second = Output("second.data").OpenWrite();
        await Assert.ThrowsAsync<IOException>(() => second.WriteAsync(new byte[64 * 1024].AsMemory()).AsTask());
        Assert.Equal(0, second.Length);
        Assert.Equal(ChunkBytes, PhysicalBytes());
    }

    [Fact]
    public async Task SeekOverwriteSparseGrowthAndChecksumTrailerRoundTrip()
    {
        using var cache = Manager("settings", 1024 * 1024);
        var output = Output("roundtrip.data");
        await using var stream = output.OpenWrite();
        Assert.True(stream.CanRead && stream.CanSeek && stream.CanWrite);
        var expected = new byte[520];
        for (int index = 0; index < 256; index++) expected[index] = (byte)index;
        stream.Write(expected, 0, 128);
        await stream.WriteAsync(expected.AsMemory(128, 128));
        stream.Seek(4, SeekOrigin.Begin);
        stream.WriteByte(0xA5); expected[4] = 0xA5;
        stream.Position = 512;
        stream.WriteByte(0x5A); expected[512] = 0x5A;
        stream.SetLength(expected.Length);
        stream.Position = 0;
        var digest = await SHA256.HashDataAsync(stream);
        Assert.Equal(SHA256.HashData(expected), digest);
        stream.Seek(0, SeekOrigin.End);
        await stream.WriteAsync(digest.AsMemory());
        await stream.FlushAsync();
        Assert.Equal(expected.Length + digest.Length, stream.Length);
        stream.Position = 0;
        var actual = new byte[expected.Length + digest.Length];
        await stream.ReadExactlyAsync(actual);
        Assert.Equal(expected.Concat(digest).ToArray(), actual);
        Assert.Equal(-1, stream.ReadByte());
        Assert.Equal(actual.Length, PhysicalBytes());
    }

    [Fact]
    public async Task CancelledWriteDoesNotCreateSparseGrowthOrChangePosition()
    {
        using var cache = Manager("settings", 1024 * 1024);
        await using var stream = Output("cancelled.data").OpenWrite();
        stream.Position = 8192;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stream.WriteAsync(new byte[2 * ChunkBytes].AsMemory(), cancellation.Token).AsTask());

        Assert.Equal(0, stream.Length);
        Assert.Equal(8192, stream.Position);
        Assert.Equal(0, PhysicalBytes());
        stream.WriteByte(0x31);
        Assert.Equal(8193, stream.Length);
    }

    [Fact]
    public async Task QueuedWriteCanCancelWhileAnotherWriteWaitsForRootAdmission()
    {
        using var cache = Manager("settings", 1024 * 1024);
        await using var stream = Output("queued.data").OpenWrite();
        using var cancellation = new CancellationTokenSource();
        Task first;
        using (var barrier = CacheStorageBudget.ForRoot(Location).Acquire(Path.Combine(Location, "barrier.data"), 0, 0))
        {
            Assert.NotNull(barrier);
            // No await occurs while this test thread owns the root mutex. The
            // first writer owns its stream gate and queues its bounded worker.
            first = stream.WriteAsync(new byte[32].AsMemory()).AsTask();
            var cancelled = stream.WriteAsync(new byte[64].AsMemory(), cancellation.Token).AsTask();
            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => cancelled.GetAwaiter().GetResult());
            Assert.False(first.IsCompleted);
            barrier.Complete(0);
        }
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(32, stream.Length);
        Assert.Equal(32, PhysicalBytes());
    }

    [Fact]
    public async Task FlushAndCloseCannotGrowADeniedBufferedTailOrRefundRetainedBytes()
    {
        using var cache = Manager("settings", 192 * 1024);
        var output = Output("closed.data");
        var stream = output.OpenWrite();
        try
        {
            stream.Write(new byte[ChunkBytes]);
            Assert.Throws<IOException>(() => stream.Write(new byte[ChunkBytes]));
            Assert.Equal(ChunkBytes, new FileInfo(output.Path).Length);
            stream.Flush();
            await stream.FlushAsync();
            Assert.Equal(ChunkBytes, PhysicalBytes());
        }
        finally { await stream.DisposeAsync(); }
        stream.Dispose();
        Assert.False(stream.CanRead || stream.CanSeek || stream.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => stream.WriteByte(1));
        Assert.Equal(ChunkBytes, new FileInfo(output.Path).Length);

        using var next = Output("next.data").OpenWrite();
        Assert.Throws<IOException>(() => next.Write(new byte[64 * 1024]));
        Assert.Equal(ChunkBytes, PhysicalBytes());
    }

    [Fact]
    public void SetLengthReservesBeforeSparseGrowthAndReleasesSuccessfulShrinks()
    {
        using var cache = Manager("settings", 192 * 1024);
        using var stream = Output("resized.data").OpenWrite();
        stream.SetLength(ChunkBytes);
        Assert.Throws<IOException>(() => stream.SetLength(192 * 1024));
        Assert.Equal(ChunkBytes, stream.Length);
        Assert.Equal(ChunkBytes, PhysicalBytes());
        stream.SetLength(0);
        using var second = Output("after-shrink.data").OpenWrite();
        second.Write(new byte[ChunkBytes]);
        Assert.Equal(ChunkBytes, PhysicalBytes());
    }

    [Fact]
    public void ExistingFilesAndPathsOutsideTheRootArePreserved()
    {
        using var cache = Manager("settings", 1024 * 1024);
        var outside = Path.Combine(root, "outside.data");
        Assert.Throws<IOException>(() => new CacheArtifactOutput(Location, outside));
        Assert.Throws<IOException>(() => new CacheArtifactOutput(Location, Location));
        Assert.False(File.Exists(outside));

        var output = Output("existing.data");
        using (var first = output.OpenWrite()) first.WriteByte(0x71);
        Assert.Throws<IOException>(() => output.OpenWrite());
        Assert.Equal(new byte[] { 0x71 }, File.ReadAllBytes(output.Path));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
