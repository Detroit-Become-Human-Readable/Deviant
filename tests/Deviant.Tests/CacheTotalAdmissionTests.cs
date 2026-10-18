using Deviant.Catalog;
using Xunit;

namespace Deviant.Tests;

public sealed class CacheTotalAdmissionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-cache-admission-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Artifact = [1, 2, 3, 4, 5, 6, 7, 8];
    private CacheManager Create(long limit) => new(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), limit));
    private static Task Produce(string path, CancellationToken token) => File.WriteAllBytesAsync(path, Artifact, token);
    private static async Task<bool> Validate(string path, CancellationToken token) =>
        (await File.ReadAllBytesAsync(path, token)).AsSpan().SequenceEqual(Artifact);

    [Fact]
    public async Task MetadataDatabaseWalAndOtherFilesReducePreparedAdmissionCapacity()
    {
        using var cache = Create(31);
        var metadata = await WriteMetadataAsync(cache.Location);
        await cache.ReconcileAsync();
        Assert.Equal(24, cache.MetadataUsageBytes);

        var lease = await cache.AcquirePreparedAsync("geometry-v1", "generated", ".bin", Produce, Validate);
        try
        {
            Assert.False(IsWithin(lease.Path, cache.Location));
            Assert.Equal(Artifact, await File.ReadAllBytesAsync(lease.Path));
            Assert.Equal(24, cache.UsageBytes);
            Assert.Equal(0, cache.PreparedUsageBytes);
            await AssertFilesUnchangedAsync(metadata);
        }
        finally { await lease.DisposeAsync(); }
        Assert.False(File.Exists(lease.Path));
    }

    [Fact]
    public async Task PreparedArtifactFitsWhenTotalUsageReachesTheLimitExactly()
    {
        using var cache = Create(32);
        var metadata = await WriteMetadataAsync(cache.Location);
        await cache.ReconcileAsync();

        await using var lease = await cache.AcquirePreparedAsync("geometry-v1", "generated", ".bin", Produce, Validate);

        Assert.True(IsWithin(lease.Path, cache.Location));
        Assert.Equal(32, cache.UsageBytes);
        Assert.Equal(8, cache.PreparedUsageBytes);
        await AssertFilesUnchangedAsync(metadata);
    }

    [Fact]
    public async Task ReducingLimitEvictsReleasedPreparedDataToMakeRoomForMetadata()
    {
        using var cache = Create(64);
        var metadata = await WriteMetadataAsync(cache.Location);
        string artifactPath;
        await using (var lease = await cache.AcquirePreparedAsync("geometry-v1", "generated", ".bin", Produce, Validate))
            artifactPath = lease.Path;
        await cache.ReconcileAsync();
        Assert.Equal(32, cache.UsageBytes);

        await cache.SetLimitAsync(24);

        Assert.False(File.Exists(artifactPath));
        Assert.Equal(24, cache.UsageBytes);
        Assert.Equal(0, cache.PreparedUsageBytes);
        await AssertFilesUnchangedAsync(metadata);
    }

    [Fact]
    public async Task BudgetPressurePreservesLiveArtifactsMetadataAndProtectedDirectories()
    {
        using var cache = Create(64);
        var library = Path.Combine(root, "library");
        var exports = Path.Combine(root, "exports");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(exports);
        var protectedFiles = new Dictionary<string, byte[]>
        {
            [Path.Combine(library, "aliases.json")] = [11, 12, 13],
            [Path.Combine(exports, "model.bin")] = [21, 22, 23]
        };
        foreach (var file in protectedFiles) await File.WriteAllBytesAsync(file.Key, file.Value);
        cache.RegisterProtectedDirectory(library);
        cache.RegisterProtectedDirectory(exports);
        var metadata = await WriteMetadataAsync(cache.Location);
        var pinned = await cache.AcquirePreparedAsync("geometry-v1", "pinned", ".bin", Produce, Validate);
        try
        {
            await cache.ReconcileAsync();
            await cache.SetLimitAsync(24);
            Assert.Equal(32, cache.UsageBytes);
            Assert.Equal(Artifact, await File.ReadAllBytesAsync(pinned.Path));

            await using var fallback = await cache.AcquirePreparedAsync("geometry-v1", "additional", ".bin", Produce, Validate);
            Assert.False(IsWithin(fallback.Path, cache.Location));
            Assert.Equal(32, cache.UsageBytes);
            Assert.Equal(8, cache.PreparedUsageBytes);
            Assert.Equal(Artifact, await File.ReadAllBytesAsync(pinned.Path));
            await AssertFilesUnchangedAsync(metadata);
            await AssertFilesUnchangedAsync(protectedFiles);
        }
        finally { await pinned.DisposeAsync(); }

        await cache.SetLimitAsync(24);
        Assert.False(File.Exists(pinned.Path));
        Assert.Equal(24, cache.UsageBytes);
        await AssertFilesUnchangedAsync(metadata);
        await AssertFilesUnchangedAsync(protectedFiles);
    }

    [Fact]
    public async Task ZeroLimitProducesTemporaryArtifactsAndPreservesMetadata()
    {
        using var cache = Create(0);
        var metadata = await WriteMetadataAsync(cache.Location);
        await cache.ReconcileAsync();

        var lease = await cache.AcquirePreparedAsync("geometry-v1", "generated", ".bin", Produce, Validate);
        try
        {
            Assert.False(IsWithin(lease.Path, cache.Location));
            Assert.Equal(Artifact, await File.ReadAllBytesAsync(lease.Path));
            Assert.Equal(24, cache.UsageBytes);
            Assert.Equal(0, cache.PreparedUsageBytes);
            await AssertFilesUnchangedAsync(metadata);
        }
        finally { await lease.DisposeAsync(); }
        Assert.False(File.Exists(lease.Path));
    }

    private static async Task<Dictionary<string, byte[]>> WriteMetadataAsync(string location)
    {
        var metadata = Path.Combine(location, "metadata");
        Directory.CreateDirectory(metadata);
        var files = new Dictionary<string, byte[]>
        {
            [Path.Combine(metadata, "catalog.db")] = Enumerable.Range(1, 12).Select(value => (byte)value).ToArray(),
            [Path.Combine(metadata, "catalog.db-wal")] = Enumerable.Range(13, 8).Select(value => (byte)value).ToArray(),
            [Path.Combine(location, "state.bin")] = [21, 22, 23, 24]
        };
        foreach (var file in files) await File.WriteAllBytesAsync(file.Key, file.Value);
        return files;
    }

    private static async Task AssertFilesUnchangedAsync(IReadOnlyDictionary<string, byte[]> files)
    {
        foreach (var file in files) Assert.Equal(file.Value, await File.ReadAllBytesAsync(file.Key));
    }

    private static bool IsWithin(string path, string directory) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
