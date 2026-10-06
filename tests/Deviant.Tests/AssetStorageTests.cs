using Deviant.Assets;
using Deviant.Catalog;
using Xunit;
namespace Deviant.Tests;

public sealed class AssetStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-storage-" + Guid.NewGuid().ToString("N"));
    public AssetStorageTests() => Directory.CreateDirectory(root);
    [Fact] public async Task PreviewQuotaDoesNotChargeMetadataBytes()
    {
        var cache=new CacheManager(Path.Combine(root,"settings"),new(Path.Combine(root,"cache"),8));
        await File.WriteAllBytesAsync(Path.Combine(cache.Location,"catalog.sqlite3"),new byte[100]);
        await using var preview=await cache.AcquireAsync("one",(p,t)=>File.WriteAllBytesAsync(p,new byte[8],t));
        Assert.StartsWith(cache.Location,preview.Path); Assert.Equal(8,cache.PreviewUsageBytes); Assert.Equal(100,cache.MetadataUsageBytes);
    }
    [Fact] public async Task AliasSurvivesRestartClearAndRelocationAndDoesNotCrossRevision()
    {
        var library = Path.Combine(root, "library"); var identity = new ResourceIdentity("index-a", 5, "media/one");
        await new AliasStore(library).SetAsync(identity, "街の声");
        var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 64));
        await using (var preview = await cache.AcquireAsync("one", (p, t) => File.WriteAllBytesAsync(p, new byte[8], t))) { Assert.True(File.Exists(preview.Path)); }
        await cache.RelocateAsync(Path.Combine(root, "relocated")); await cache.ClearAsync();
        var reopened = new AliasStore(library);
        Assert.Equal("街の声", reopened.Get(identity)); Assert.Null(reopened.Get(identity with { IndexContentId = "index-b" }));
        await reopened.SetAsync(identity, null); Assert.Null(new AliasStore(library).Get(identity));
    }
    [Fact] public async Task RelocationWaitsForPreviewLeaseAndKeepsPreviousCopy()
    {
        var original = Path.Combine(root, "cache"); var target = Path.Combine(root, "new");
        var cache = new CacheManager(Path.Combine(root, "settings"), new(original, 1024));
        var lease = await cache.AcquireAsync("one", (p, t) => File.WriteAllBytesAsync(p, new byte[] { 1, 2, 3 }, t));
        var moving = cache.RelocateAsync(target);
        Assert.False(moving.IsCompleted); Assert.Equal(original, cache.Location);
        await lease.DisposeAsync(); await moving;
        Assert.Equal(target, cache.Location); Assert.True(File.Exists(lease.Path));
        await using var next = await cache.AcquireAsync("one", (_, _) => throw new InvalidOperationException("Cache was not copied"));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(next.Path));
    }
    [Fact] public async Task ConcurrentLibraryInstancesPreserveBothAliases()
    {
        var library = Path.Combine(root, "library");
        var first = new AliasStore(library); var second = new AliasStore(library);
        await Task.WhenAll(first.SetAsync(new("index", 1), "One"), second.SetAsync(new("index", 2), "Two"));
        var reopened = new AliasStore(library);
        Assert.Equal("One", reopened.Get(new("index", 1))); Assert.Equal("Two", reopened.Get(new("index", 2)));
    }
    [Fact] public async Task CancellationDuringRelocationKeepsOriginalLocationAndPreview()
    {
        var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 1024));
        await using var lease = await cache.AcquireAsync("one", (p, t) => File.WriteAllBytesAsync(p, new byte[3], t));
        using var cancellation = new CancellationTokenSource();
        var moving = cache.RelocateAsync(Path.Combine(root, "new"), cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moving);
        Assert.Equal(Path.Combine(root, "cache"), cache.Location); Assert.True(File.Exists(lease.Path));
    }
    [Fact] public async Task PreviousCacheRemovalRequiresARecordedSeparateLocation()
    {
        var original=Path.Combine(root,"cache"); var moved=Path.Combine(root,"moved");
        var cache=new CacheManager(Path.Combine(root,"settings"),new(original,64));
        await using(var lease=await cache.AcquireAsync("one",(p,t)=>File.WriteAllBytesAsync(p,new byte[8],t))) { }
        await cache.RelocateAsync(moved); Assert.Contains(original,cache.PreviousLocations);
        await Assert.ThrowsAsync<IOException>(()=>cache.RemovePreviousAsync(moved));
        await cache.RemovePreviousAsync(original); Assert.False(Directory.Exists(original));Assert.True(Directory.Exists(moved));Assert.Empty(cache.PreviousLocations);
    }
    [Fact] public async Task QuotaEvictionPreservesLeasedPreviewAndAllowsUncachedOutput()
    {
        var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 8));
        await using var first = await cache.AcquireAsync("one", (p, t) => File.WriteAllBytesAsync(p, new byte[8], t));
        var second = await cache.AcquireAsync("two", (p, t) => File.WriteAllBytesAsync(p, new byte[8], t));
        Assert.True(File.Exists(first.Path)); Assert.True(File.Exists(second.Path));
        Assert.False(second.Path.StartsWith(cache.Location, StringComparison.OrdinalIgnoreCase));
        await second.DisposeAsync(); Assert.False(File.Exists(second.Path));
    }
    [Fact] public async Task FailedProducerLeavesNoPublishedCacheEntry()
    {
        var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 64));
        await Assert.ThrowsAsync<IOException>(() => cache.AcquireAsync("one", async (p, t) => { await File.WriteAllBytesAsync(p, new byte[3], t); throw new IOException("Disk full"); }));
        Assert.Equal(0, cache.UsageBytes);
    }
    [Fact] public async Task FullCacheDriveCanFallBackToAnUncachedPreview()
    {
        var cache = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 64));
        var attempts = 0;
        await using var preview = await cache.AcquireAsync("one", async (path, token) =>
        {
            if (++attempts == 1) throw new IOException("Disk full", unchecked((int)0x80070070));
            await File.WriteAllBytesAsync(path, new byte[] { 3, 4 }, token);
        });
        Assert.Equal(2, attempts); Assert.False(preview.Path.StartsWith(cache.Location, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(preview.Path));
    }
    [Fact] public async Task ImageAndAudioEntriesHaveSeparateLeasesAndQuotaFallbacks()
    {
        var cache=new CacheManager(Path.Combine(root,"settings"),new(Path.Combine(root,"cache"),8));
        await using var audio=await cache.AcquireAsync("same-key",(path,token)=>File.WriteAllBytesAsync(path,new byte[8],token));
        var image=await cache.AcquireAsync("same-key",(path,token)=>File.WriteAllBytesAsync(path,new byte[]{1,2,3},token),CancellationToken.None,".png");
        Assert.EndsWith(".wav",audio.Path);Assert.EndsWith(".png",image.Path);Assert.NotEqual(audio.Path,image.Path);
        Assert.True(File.Exists(audio.Path));Assert.Equal(new byte[]{1,2,3},await File.ReadAllBytesAsync(image.Path));
        Assert.False(image.Path.StartsWith(cache.Location,StringComparison.OrdinalIgnoreCase));
        await image.DisposeAsync();Assert.False(File.Exists(image.Path));Assert.True(File.Exists(audio.Path));
        await Assert.ThrowsAsync<ArgumentException>(()=>cache.AcquireAsync("invalid",(_,_)=>Task.CompletedTask,CancellationToken.None,"../file.png"));
    }
    [Fact] public void DerivedCacheKeyIncludesRevisionPayloadDecoderAndOptions()
    {
        var key = CacheManager.Key("a", "hash", "1", "mono");
        Assert.NotEqual(key, CacheManager.Key("b", "hash", "1", "mono"));
        Assert.NotEqual(key, CacheManager.Key("a", "other", "1", "mono"));
        Assert.NotEqual(key, CacheManager.Key("a", "hash", "2", "mono"));
        Assert.NotEqual(key, CacheManager.Key("a", "hash", "1", "stereo"));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
