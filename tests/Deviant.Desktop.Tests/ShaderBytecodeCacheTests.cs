using System.Buffers.Binary;
using System.IO;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class ShaderBytecodeCacheTests
{
    [Fact]
    public async Task BytecodeSurvivesConsumerReleaseAndCacheRestartWithoutRecompiling()
    {
        using var fixture = new Fixture(); var budget = new AssetMemoryBudget(); int compilations = 0;
        Task<byte[]> Compile(CancellationToken token) { token.ThrowIfCancellationRequested(); compilations++; return Task.FromResult(Bytecode(1)); }
        using (var first = new ShaderBytecodeCache(fixture.Disk))
        {
            using (var lease = await first.AcquireAsync(Request(), budget, Compile)) Assert.Equal(Bytecode(1), lease.Bytecode.ToArray());
            Assert.True(budget.UsedBytes > 0);
            using (var lease = await first.AcquireAsync(Request(), budget, Compile)) Assert.Equal(Bytecode(1), lease.Bytecode.ToArray());
            Assert.Equal(1, compilations); Assert.True(first.HotBytes > 0);
        }
        Assert.Equal(0, budget.UsedBytes);
        using (var restarted = new ShaderBytecodeCache(fixture.Disk))
        using (var lease = await restarted.AcquireAsync(Request(), budget, Compile))
        { Assert.Equal(1, compilations); Assert.Equal(Bytecode(1), lease.Bytecode.ToArray()); }
        Assert.Equal(0, budget.UsedBytes); Assert.Equal(0, fixture.Disk.ActivePreviewLeases);
        Assert.True(fixture.Disk.PreparedUsageBytes > 0);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("entry")]
    [InlineData("target")]
    [InlineData("flags")]
    [InlineData("compiler")]
    [InlineData("backend")]
    public async Task EveryCompilerInputSeparatesThePersistedEntry(string changed)
    {
        using var fixture = new Fixture(); var budget = new AssetMemoryBudget(); int compilations = 0;
        using var cache = new ShaderBytecodeCache(fixture.Disk);
        Task<byte[]> Compile(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Bytecode((byte)++compilations)); }
        using (var original = await cache.AcquireAsync(Request(), budget, Compile)) { }
        var request = changed switch
        {
            "source" => Request() with { Source = "float4 main() : SV_TARGET { return 0; }" },
            "entry" => Request() with { EntryPoint = "other" },
            "target" => Request() with { Target = "vs_5_0" },
            "flags" => Request() with { Flags = 1 },
            "compiler" => Request() with { Compiler = Request().Compiler with { Sha256 = new string('b', 64) } },
            _ => Request() with { Compiler = Request().Compiler with { ManagedBackendVersion = "2" } }
        };
        Assert.NotEqual(ShaderBytecodeCache.Key(Request()), ShaderBytecodeCache.Key(request));
        using var changedLease = await cache.AcquireAsync(request, budget, Compile);
        Assert.Equal(2, compilations); Assert.Equal(Bytecode(2), changedLease.Bytecode.ToArray());
    }

    [Fact]
    public async Task CorruptDiskPayloadIsReplacedAndAllDiskLeasesAreReleased()
    {
        using var fixture = new Fixture(); var budget = new AssetMemoryBudget(); int compilations = 0;
        Task<byte[]> Compile(CancellationToken token) { token.ThrowIfCancellationRequested(); compilations++; return Task.FromResult(Bytecode((byte)compilations)); }
        using (var cache = new ShaderBytecodeCache(fixture.Disk))
        using (var lease = await cache.AcquireAsync(Request(), budget, Compile)) { }
        var path = Assert.Single(Directory.EnumerateFiles(fixture.Root, "*.shader", SearchOption.AllDirectories));
        var bytes = await File.ReadAllBytesAsync(path); bytes[^1] ^= 1; await File.WriteAllBytesAsync(path, bytes);
        using (var cache = new ShaderBytecodeCache(fixture.Disk))
        using (var lease = await cache.AcquireAsync(Request(), budget, Compile)) Assert.Equal(Bytecode(2), lease.Bytecode.ToArray());
        Assert.Equal(2, compilations); Assert.Equal(0, budget.UsedBytes); Assert.Equal(0, fixture.Disk.ActivePreviewLeases);
    }

    [Fact]
    public async Task SameKeyConcurrentConsumersCompileOnceAndRetainIndependentLeases()
    {
        var budget = new AssetMemoryBudget(); using var cache = new ShaderBytecodeCache();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int compilations = 0;
        async Task<byte[]> Compile(CancellationToken token) { compilations++; entered.TrySetResult(); await release.Task.WaitAsync(token); return Bytecode(1); }
        var first = cache.AcquireAsync(Request(), budget, Compile); await entered.Task;
        var second = cache.AcquireAsync(Request(), budget, Compile); release.SetResult();
        using var a = await first; using var b = await second;
        Assert.Equal(1, compilations); a.Dispose(); Assert.Equal(Bytecode(1), b.Bytecode.ToArray());
        cache.Dispose(); Assert.True(budget.UsedBytes > 0); b.Dispose(); Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task BoundedEvictionLeavesActiveConsumersValid()
    {
        var budget = new AssetMemoryBudget(); using var cache = new ShaderBytecodeCache(maximumHotBytes: 128);
        using var a = await cache.AcquireAsync(Request(), budget, _ => Task.FromResult(Bytecode(1)));
        using var b = await cache.AcquireAsync(Request() with { Source = "second" }, budget, _ => Task.FromResult(Bytecode(2)));
        Assert.InRange(cache.HotBytes, 0, 128); Assert.Equal(Bytecode(1), a.Bytecode.ToArray());
        cache.Dispose(); Assert.True(budget.UsedBytes > 0); a.Dispose(); b.Dispose(); Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task CancellationAndInvalidCompileOutputReleaseMemoryAndTemporaryArtifacts()
    {
        using var fixture = new Fixture(); var budget = new AssetMemoryBudget();
        using (var cache = new ShaderBytecodeCache(fixture.Disk))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.AcquireAsync(Request(), budget, _ => Task.FromResult(new byte[32])));
            Assert.Equal(0, budget.UsedBytes);
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.AcquireAsync(Request(), budget, token =>
            { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(Bytecode(1)); }, cancellation.Token));
            Assert.Equal(0, budget.UsedBytes); Assert.Equal(0, fixture.Disk.ActivePreviewLeases);
            Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task ZeroDiskBudgetUsesTemporaryArtifactAndRetainsOnlyBoundedMemory()
    {
        using var fixture = new Fixture(limit: 0); var budget = new AssetMemoryBudget();
        using (var cache = new ShaderBytecodeCache(fixture.Disk))
        using (var lease = await cache.AcquireAsync(Request(), budget, _ => Task.FromResult(Bytecode(1))))
        { Assert.Equal(Bytecode(1), lease.Bytecode.ToArray()); Assert.Equal(0, fixture.Disk.ActivePreviewLeases); }
        Assert.Equal(0, budget.UsedBytes); Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.shader", SearchOption.AllDirectories));
    }

    private static ShaderBytecodeRequest Request() => new("float4 main() : SV_TARGET { return 1; }", "main", "ps_5_0", 0,
        new("test-compiler.dll", "1", new string('a', 64), "X64", "1"));
    private static byte[] Bytecode(byte value)
    {
        var bytes = new byte[128]; "DXBC"u8.CopyTo(bytes); bytes.AsSpan(32).Fill(value);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (uint)bytes.Length); return bytes;
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "deviant-shader-cache-" + Guid.NewGuid().ToString("N"));
        public CacheManager Disk { get; }
        public Fixture(long limit = 20L * 1024 * 1024 * 1024) => Disk = new(Path.Combine(Root, "settings"), new(Path.Combine(Root, "cache"), limit));
        public void Dispose() { Disk.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
