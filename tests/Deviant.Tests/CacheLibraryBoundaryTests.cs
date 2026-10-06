using Deviant.Assets;
using Deviant.Catalog;
using Xunit;

namespace Deviant.Tests;

public sealed class CacheLibraryBoundaryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-cache-boundary-" + Guid.NewGuid().ToString("N"));
    private static readonly ResourceIdentity Identity = new("index-a", 3);

    [Theory]
    [InlineData("same")]
    [InlineData("ancestor")]
    [InlineData("descendant")]
    public async Task RejectedLibraryRegistrationStillPreventsClearFromDeletingAliases(string overlap)
    {
        var shared = Path.Combine(root, "shared");
        var library = Path.Combine(shared, "library");
        await new AliasStore(library).SetAsync(Identity, "Surface label");
        var aliasPath = Path.Combine(library, "aliases.json");
        var originalAliases = await File.ReadAllBytesAsync(aliasPath);
        var location = overlap switch
        {
            "same" => library,
            "ancestor" => shared,
            "descendant" => Path.Combine(library, "cache"),
            _ => throw new ArgumentException(nameof(overlap))
        };
        var cache = new CacheManager(Path.Combine(root, "settings"), new(location, 64));
        var marker = Path.Combine(location, "retained.bin");
        await File.WriteAllBytesAsync(marker, [1, 2, 3]);

        Assert.Throws<IOException>(() => cache.RegisterProtectedDirectory(library));
        await Assert.ThrowsAsync<IOException>(() => cache.ClearAsync());

        Assert.Equal(originalAliases, await File.ReadAllBytesAsync(aliasPath));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(marker));
        Assert.Equal("Surface label", new AliasStore(library).Get(Identity));
        Assert.Equal(location, cache.Location);
    }

    [Fact]
    public async Task RelocationOverlappingLibraryIsRejectedBeforeDrainOrDirectoryChanges()
    {
        var shared = Path.Combine(root, "shared");
        var library = Path.Combine(shared, "library");
        await new AliasStore(library).SetAsync(Identity, "Surface label");
        var aliasPath = Path.Combine(library, "aliases.json");
        var originalAliases = await File.ReadAllBytesAsync(aliasPath);
        var location = Path.Combine(root, "cache");
        var cache = new CacheManager(Path.Combine(root, "settings"), new(location, 64));
        cache.RegisterProtectedDirectory(library);
        var marker = Path.Combine(location, "retained.bin");
        await File.WriteAllBytesAsync(marker, [4, 5, 6]);
        var drainCalls = 0;

        foreach (var destination in new[] { shared, library, Path.Combine(library, "previews") })
        {
            var existed = Directory.Exists(destination);
            await Assert.ThrowsAsync<IOException>(() => cache.RelocateAsync(destination, _ =>
            {
                ++drainCalls;
                return Task.CompletedTask;
            }));
            Assert.Equal(existed, Directory.Exists(destination));
            Assert.Equal(originalAliases, await File.ReadAllBytesAsync(aliasPath));
            Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(marker));
            Assert.Equal(location, cache.Location);
        }

        Assert.Equal(0, drainCalls);
        Assert.Empty(cache.PreviousLocations);
        Assert.Empty(Directory.EnumerateDirectories(root, "*.relocating-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SiblingCacheWithSharedPathPrefixCanClearWithoutChangingAliases()
    {
        var library = Path.Combine(root, "library");
        await new AliasStore(library).SetAsync(Identity, "Surface label");
        var aliasPath = Path.Combine(library, "aliases.json");
        var originalAliases = await File.ReadAllBytesAsync(aliasPath);
        var location = Path.Combine(root, "library-cache");
        var cache = new CacheManager(Path.Combine(root, "settings"), new(location, 64));
        cache.RegisterProtectedDirectory(library);
        await File.WriteAllBytesAsync(Path.Combine(location, "metadata.bin"), [7, 8, 9]);
        string previewPath;
        await using (var lease = await cache.AcquireAsync("surface", (path, token) => File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 }, token)))
        {
            previewPath = lease.Path;
            Assert.True(File.Exists(previewPath));
        }

        await cache.ClearAsync();

        Assert.False(File.Exists(previewPath));
        Assert.Empty(Directory.EnumerateFiles(location, "*", SearchOption.AllDirectories));
        Assert.Equal(originalAliases, await File.ReadAllBytesAsync(aliasPath));
        Assert.Equal("Surface label", new AliasStore(library).Get(Identity));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
