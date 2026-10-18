using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Reflection;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class ShaderBytecodeBundleTests
{
    private static string BundleKey => new('c', 64);

    [Fact]
    public async Task RestartRestoresUsedShadersWithoutIndividualRecordsOrCompilerCalls()
    {
        using var fixture = new Fixture(); var budget = new AssetMemoryBudget();
        using (var cache = new ShaderBytecodeCache(fixture.Disk))
        {
            using var vertex = await cache.AcquireAsync(Request("vertex", "vs_5_0"), budget, _ => Task.FromResult(Code(1)));
            using var pixel = await cache.AcquireAsync(Request("pixel"), budget, _ => Task.FromResult(Code(2)));
            using var unrelated = await cache.AcquireAsync(Request("unused"), budget, _ => Task.FromResult(Code(3)));
            Assert.True(await cache.PrepareBundleAsync(BundleKey, [vertex, pixel, vertex], budget));
        }
        Assert.Equal(0, budget.UsedBytes);
        Assert.Single(Directory.EnumerateFiles(fixture.Root, "*.shbundle", SearchOption.AllDirectories));
        foreach (var path in Directory.EnumerateFiles(fixture.Root, "*.shader", SearchOption.AllDirectories)) File.Delete(path);
        using (var cache = new ShaderBytecodeCache(fixture.Disk))
        {
            Assert.True(await cache.RestoreBundleAsync(BundleKey, budget));
            Assert.Equal(2 * (Code(1).Length + 512), cache.HotBytes);
            using var vertex = await cache.AcquireAsync(Request("vertex", "vs_5_0"), budget, UnexpectedCompile);
            using var pixel = await cache.AcquireAsync(Request("pixel"), budget, UnexpectedCompile);
            Assert.Equal(Code(1), vertex.Bytecode.ToArray()); Assert.Equal(Code(2), pixel.Bytecode.ToArray());
            Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.shader", SearchOption.AllDirectories));
            using var unrelated = await cache.AcquireAsync(Request("unused"), budget, _ => Task.FromResult(Code(4)));
            Assert.Equal(Code(4), unrelated.Bytecode.ToArray());
        }
        Assert.Equal(0, budget.UsedBytes); Assert.Equal(0, fixture.Disk.ActivePreviewLeases);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task UnavailableDiskCapacitySkipsBundleAndKeepsCompiledConsumersUsable(long limit)
    {
        using var fixture = new Fixture(limit); var budget = new AssetMemoryBudget(); int compilations = 0;
        using (var cache = new ShaderBytecodeCache(fixture.Disk))
        {
            using var shader = await cache.AcquireAsync(Request("pixel"), budget, token =>
            { token.ThrowIfCancellationRequested(); compilations++; return Task.FromResult(Code(1)); });
            long retained = budget.UsedBytes;
            Assert.False(await cache.PrepareBundleAsync(BundleKey, [shader], budget));
            Assert.Equal(retained, budget.UsedBytes);
            Assert.Equal(Code(1), shader.Bytecode.ToArray());
            using var reused = await cache.AcquireAsync(Request("pixel"), budget, UnexpectedCompile);
            Assert.Equal(Code(1), reused.Bytecode.ToArray());
            Assert.Equal(1, compilations);
            Assert.Equal(0, fixture.Disk.ActivePreviewLeases);
            Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.shbundle", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task LaterCompleteCaptureReplacesAnEarlierSubsetForTheSameSceneProfile()
    {
        using var fixture = new Fixture(); var budget = new AssetMemoryBudget();
        using (var cache = new ShaderBytecodeCache(fixture.Disk))
        {
            using var vertex = await cache.AcquireAsync(Request("vertex", "vs_5_0"), budget, _ => Task.FromResult(Code(1)));
            using var pixel = await cache.AcquireAsync(Request("pixel"), budget, _ => Task.FromResult(Code(2)));
            Assert.True(await cache.PrepareBundleAsync(BundleKey, [vertex], budget));
            Assert.True(await cache.PrepareBundleAsync(BundleKey, [vertex, pixel], budget));
        }
        using var restarted = new ShaderBytecodeCache(fixture.Disk);
        Assert.True(await restarted.RestoreBundleAsync(BundleKey, budget));
        using var restoredPixel = await restarted.AcquireAsync(Request("pixel"), budget, UnexpectedCompile);
        Assert.Equal(Code(2), restoredPixel.Bytecode.ToArray());
    }

    [Fact]
    public async Task DifferentSceneBundleIdentityMissesWithoutPublishingAnything()
    {
        using var fixture = new Fixture(); await Seed(fixture); var budget = new AssetMemoryBudget();
        using var cache = new ShaderBytecodeCache(fixture.Disk);
        Assert.False(await cache.RestoreBundleAsync(new string('d', 64), budget));
        Assert.Equal(0, cache.HotBytes); Assert.Equal(0, budget.UsedBytes);
        Assert.Single(Directory.EnumerateFiles(fixture.Root, "*.shbundle", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RestoredEntryStillRequiresTheExactCompilerAndSourceKey()
    {
        using var fixture = new Fixture(); await Seed(fixture); var budget = new AssetMemoryBudget();
        using var cache = new ShaderBytecodeCache(fixture.Disk);
        Assert.True(await cache.RestoreBundleAsync(BundleKey, budget));
        using var changed = await cache.AcquireAsync(Request("pixel") with
            { Compiler = Request("pixel").Compiler with { Sha256 = new string('b', 64) } },
            budget, _ => Task.FromResult(Code(9)));
        Assert.Equal(Code(9), changed.Bytecode.ToArray());
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("duplicate")]
    [InlineData("length")]
    [InlineData("identity")]
    [InlineData("key")]
    [InlineData("count")]
    [InlineData("trailing")]
    public async Task MalformedBundleCannotPartiallyPopulateTheLru(string defect)
    {
        using var fixture = new Fixture(); await Seed(fixture); var budget = new AssetMemoryBudget();
        string path = Assert.Single(Directory.EnumerateFiles(fixture.Root, "*.shbundle", SearchOption.AllDirectories));
        var bytes = await File.ReadAllBytesAsync(path);
        switch (defect)
        {
            case "payload": bytes[^1] ^= 1; break;
            case "duplicate": bytes.AsSpan(76, 32).CopyTo(bytes.AsSpan(76 + 68 + 128, 32)); break;
            case "length": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(76 + 64), int.MaxValue); break;
            case "identity": bytes[8] ^= 1; break;
            case "key": bytes[76] ^= 1; break;
            case "count": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), 4097); break;
            default: bytes = [.. bytes, 0]; break;
        }
        await File.WriteAllBytesAsync(path, bytes);
        using var cache = new ShaderBytecodeCache(fixture.Disk);
        Assert.False(await cache.RestoreBundleAsync(BundleKey, budget));
        Assert.Equal(0, cache.HotBytes); Assert.Equal(0, budget.UsedBytes); Assert.Equal(0, fixture.Disk.ActivePreviewLeases);
    }

    [Fact]
    public async Task CancellationLeavesHotEntriesAndTheirConsumersIntact()
    {
        using var fixture = new Fixture(); await Seed(fixture); var budget = new AssetMemoryBudget();
        using var cache = new ShaderBytecodeCache(fixture.Disk);
        using var existing = await cache.AcquireAsync(Request("existing"), budget, _ => Task.FromResult(Code(7)));
        long before = budget.UsedBytes; using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.RestoreBundleAsync(BundleKey, budget, canceled.Token));
        Assert.Equal(before, budget.UsedBytes); Assert.Equal(Code(7), existing.Bytecode.ToArray());
    }

    [Fact]
    public async Task CancellationAfterBundleReservationsRollsBackStagingAndKeepsExistingConsumers()
    {
        using var fixture = new Fixture(); await Seed(fixture);
        foreach (var path in Directory.EnumerateFiles(fixture.Root, "*.shader", SearchOption.AllDirectories)) File.Delete(path);
        var budget = new AssetMemoryBudget(); using var cache = new ShaderBytecodeCache(fixture.Disk);
        using var existing = await cache.AcquireAsync(Request("existing"), budget, _ => Task.FromResult(Code(7)));
        long baseline = budget.UsedBytes;
        var publicationLock = typeof(ShaderBytecodeCache).GetField("sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        using var held = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var validated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? holder = null; int observed = 0;
        using var observer = AssetLoadDiagnostics.Listener.Subscribe(new BundleObserver(measurement =>
        {
            if (measurement.Stage != "ShaderBundleCache" || measurement.Key != BundleKey || measurement.CacheHit != true ||
                Interlocked.Exchange(ref observed, 1) != 0) return;
            holder = Task.Run(() =>
            {
                lock (publicationLock)
                {
                    held.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The bundle publication gate was not released.");
                }
            });
            if (!held.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The bundle publication gate was not acquired.");
            validated.TrySetResult();
        }), name => name == AssetLoadDiagnostics.MeasurementName);
        var restore = Task.Run(() => cache.RestoreBundleAsync(BundleKey, budget, cancellation.Token));
        try
        {
            await validated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The publication lock leaves the real admission loop free to reserve every staged entry.
            long stagedBytes = baseline + ShaderBytecodeCache.MaximumBundleHotBytes + 76 + 2 * (Code(1).Length + 512);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (budget.UsedBytes != stagedBytes) await Task.Delay(1, deadline.Token);
            Assert.Equal(Code(7), existing.Bytecode.ToArray());
            cancellation.Cancel();
        }
        finally
        {
            cancellation.Cancel();
            release.Set();
            if (holder is not null) await holder.WaitAsync(TimeSpan.FromSeconds(10));
            try { await restore.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
        var hot = (System.Collections.IDictionary)typeof(ShaderBytecodeCache).GetField("hot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        Assert.Equal(new[] { existing.Key }, hot.Keys.Cast<string>().ToArray());
        Assert.Equal(baseline, budget.UsedBytes); Assert.Equal(baseline, cache.HotBytes);
        Assert.Equal(Code(7), existing.Bytecode.ToArray()); Assert.Equal(0, fixture.Disk.ActivePreviewLeases);
        Assert.True(await cache.RestoreBundleAsync(BundleKey, budget));
        using var vertex = await cache.AcquireAsync(Request("vertex", "vs_5_0"), budget, UnexpectedCompile);
        using var pixel = await cache.AcquireAsync(Request("pixel"), budget, UnexpectedCompile);
        Assert.Equal(Code(1), vertex.Bytecode.ToArray()); Assert.Equal(Code(2), pixel.Bytecode.ToArray());
    }

    [Fact]
    public async Task WholeBundleMustFitTheConfiguredLruAndActiveConsumersRemainValid()
    {
        using var fixture = new Fixture(); await Seed(fixture); var budget = new AssetMemoryBudget();
        using var cache = new ShaderBytecodeCache(fixture.Disk, maximumHotBytes: 640);
        using var existing = await cache.AcquireAsync(Request("existing"), budget, _ => Task.FromResult(Code(7)));
        Assert.False(await cache.RestoreBundleAsync(BundleKey, budget));
        Assert.Equal(640, cache.HotBytes); Assert.Equal(Code(7), existing.Bytecode.ToArray());
        Assert.True(await cache.PrepareBundleAsync(new string('e', 64), [existing, existing], budget));
    }

    [Fact]
    public async Task BundleCaptureRejectsForeignConsumersAndReleasesAnyPartialPins()
    {
        using var fixture = new Fixture(); var budget = new AssetMemoryBudget();
        using var first = new ShaderBytecodeCache(fixture.Disk); using var second = new ShaderBytecodeCache();
        using var a = await first.AcquireAsync(Request("a"), budget, _ => Task.FromResult(Code(1)));
        using var b = await second.AcquireAsync(Request("b"), budget, _ => Task.FromResult(Code(2)));
        long before = budget.UsedBytes;
        await Assert.ThrowsAsync<ArgumentException>(() => first.PrepareBundleAsync(BundleKey, [a, b], budget));
        Assert.Equal(before, budget.UsedBytes); first.Dispose(); second.Dispose(); a.Dispose(); b.Dispose(); Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task FailedRetainedMemoryAdmissionPublishesNoEntriesAndReleasesStaging()
    {
        using var fixture = new Fixture(); await Seed(fixture);
        var budget = new AssetMemoryBudget(ShaderBytecodeCache.MaximumBundleHotBytes + 76 + 700);
        using var cache = new ShaderBytecodeCache(fixture.Disk);
        await Assert.ThrowsAsync<ArchiveException>(() => cache.RestoreBundleAsync(BundleKey, budget));
        Assert.Equal(0, cache.HotBytes); Assert.Equal(0, budget.UsedBytes); Assert.Equal(0, fixture.Disk.ActivePreviewLeases);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("graph")]
    [InlineData("compiler")]
    [InlineData("backend")]
    public void SceneBundleIdentityIncludesRevisionGraphAndCompilerBackend(string changed)
    {
        using var original = Snapshot(); using var equivalent = Snapshot();
        var compiler = Request("pixel").Compiler;
        string first = AssetViewportService.ShaderBundleKey(original, compiler);
        Assert.Equal(first, AssetViewportService.ShaderBundleKey(equivalent, compiler));
        using var altered = Snapshot(stamp: changed == "revision" ? "updated" : "initial", red: changed == "graph" ? 0.5f : 1);
        var alteredCompiler = changed switch
        {
            "compiler" => compiler with { Sha256 = new string('b', 64) },
            "backend" => compiler with { ManagedBackendVersion = "2" },
            _ => compiler
        };
        Assert.NotEqual(first, AssetViewportService.ShaderBundleKey(altered, alteredCompiler));
        Assert.Equal(0, original.MemoryBudget.UsedBytes); Assert.Equal(0, altered.MemoryBudget.UsedBytes);
    }

    private static RenderAssetSnapshot Snapshot(string stamp = "initial", float red = 1)
    {
        var identity = new ResourceIdentity("generic", 1); var revision = new SourceRevision("generic", stamp);
        var graph = new MaterialSurfaceGraph(new(MaterialExpressionOperation.Constant, MaterialValueType.Vector3, [], [red, 0, 0]),
            null, null, [], [], []);
        var material = new RenderMaterial(new(identity, revision, "generic-material-v1", [], [], []) { SurfaceGraph = graph },
            new Dictionary<int, RenderTexture>());
        return new(revision, new Dictionary<string, RenderMesh>(), [],
            new Dictionary<ResourceIdentity, RenderMaterial> { [identity] = material }, new(Vector3.Zero, Vector3.One), [], [],
            new(0, 0, 0, 0, 0, 0), [new("package.bin", 0, 10, new string('a', 64))], new AssetMemoryBudget());
    }

    private static async Task Seed(Fixture fixture)
    {
        var budget = new AssetMemoryBudget(); using var cache = new ShaderBytecodeCache(fixture.Disk);
        using var a = await cache.AcquireAsync(Request("vertex", "vs_5_0"), budget, _ => Task.FromResult(Code(1)));
        using var b = await cache.AcquireAsync(Request("pixel"), budget, _ => Task.FromResult(Code(2)));
        Assert.True(await cache.PrepareBundleAsync(BundleKey, [a, b], budget));
    }
    private static Task<byte[]> UnexpectedCompile(CancellationToken _) => throw new InvalidOperationException("A bundled shader was compiled again.");
    private static ShaderBytecodeRequest Request(string source, string target = "ps_5_0") => new(source, "main", target, 0,
        new("test-compiler.dll", "1", new string('a', 64), "X64", "1"));
    private static byte[] Code(byte value)
    {
        var bytes = new byte[128]; "DXBC"u8.CopyTo(bytes); bytes.AsSpan(32).Fill(value);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (uint)bytes.Length); return bytes;
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "deviant-shader-bundle-" + Guid.NewGuid().ToString("N"));
        public CacheManager Disk { get; }
        public Fixture(long limit = 20L * 1024 * 1024 * 1024) => Disk = new(Path.Combine(Root, "settings"), new(Path.Combine(Root, "cache"), limit));
        public void Dispose() { Disk.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class BundleObserver(Action<AssetLoadMeasurement> receive) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        { if (value.Value is AssetLoadMeasurement measurement) receive(measurement); }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
