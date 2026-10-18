using System.Numerics;
using System.Reflection;
using System.Text;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Export.Models;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class MaterialBakeCacheTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    public async Task OptionalCachePressureKeepsExactBakeInMemory(long limit)
    {
        var root = Path.Combine(Path.GetTempPath(), "deviant-bake-admission-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), limit));
            var (material, primitive) = Fixture();
            var budget = new AssetMemoryBudget();
            using var retained = budget.Reserve(4096, "generated bake output");
            var textures = new Dictionary<int, MaterialBakeTexture>();
            var expected = MaterialSurfaceMapBaker.Bake(material, primitive, textures, 2, 2, budget.Reserve, CancellationToken.None);
            string key = Fingerprint(material, primitive, textures);
            var observer = new BakeObserver(key);
            using var subscription = AssetLoadDiagnostics.Listener.Subscribe(observer, name => name == AssetLoadDiagnostics.MeasurementName);
            int validations = 0;
            Task Validate(CancellationToken token) { token.ThrowIfCancellationRequested(); validations++; return Task.CompletedTask; }
            var actual = await new MaterialBakeCache(manager).BakeAsync(material, primitive, textures, 2, 2, budget, Validate);
            Assert.Equal(expected.Document.OriginalBytes.ToArray(), actual.Document.OriginalBytes.ToArray());
            Assert.Equal(expected.Surface.Rgba8.ToArray(), actual.Surface.Rgba8.ToArray());
            Assert.Equal(expected.GraphSha256, actual.GraphSha256);
            Assert.True(validations >= 2);
            if (limit == 0) Assert.Equal(0, observer.Writes);
            Assert.Empty(Directory.EnumerateFiles(manager.Location, "*", SearchOption.AllDirectories));
            Assert.Equal(0, manager.ActivePreviewLeases);
            Assert.Equal(4096, budget.UsedBytes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(112)]
    [InlineData(39)]
    [InlineData(5)]
    public async Task PhysicalDiskFullAtCacheWriteFallsBackWithoutHidingOtherIoErrors(int code)
    {
        var root = Path.Combine(Path.GetTempPath(), "deviant-bake-io-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 1024 * 1024));
            var (material, primitive) = Fixture();
            var textures = new Dictionary<int, MaterialBakeTexture>();
            var budget = new AssetMemoryBudget();
            using var retained = budget.Reserve(4096, "generated bake output");
            var expected = MaterialSurfaceMapBaker.Bake(material, primitive, textures, 2, 2, budget.Reserve, CancellationToken.None);
            var injected = new IOException("Generated cache write failure", unchecked((int)0x80070000) | code);
            var observer = new BakeFailureObserver(Fingerprint(material, primitive, textures), injected);
            using var subscription = AssetLoadDiagnostics.Listener.Subscribe(observer, name => name == AssetLoadDiagnostics.MeasurementName);
            int validations = 0;
            Task Validate(CancellationToken token) { token.ThrowIfCancellationRequested(); validations++; return Task.CompletedTask; }
            MaterialSurfaceMap? result = null;
            var failure = await Record.ExceptionAsync(async () =>
                result = await new MaterialBakeCache(manager).BakeAsync(material, primitive, textures, 2, 2, budget, Validate));
            Assert.Equal(1, observer.Count);
            if (code is 112 or 39)
            {
                Assert.Null(failure); Assert.NotNull(result);
                Assert.Equal(expected.Document.OriginalBytes.ToArray(), result.Document.OriginalBytes.ToArray());
                Assert.Equal(expected.Surface.Rgba8.ToArray(), result.Surface.Rgba8.ToArray());
                Assert.True(validations >= 2);
            }
            else { Assert.Same(injected, failure); Assert.Null(result); }
            Assert.Empty(Directory.EnumerateFiles(manager.Location, "*", SearchOption.AllDirectories));
            Assert.Equal(0, manager.ActivePreviewLeases);
            Assert.Equal(4096, budget.UsedBytes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExistingBakeBytesAndNamespaceRemainAValidWarmHit()
    {
        var root = Path.Combine(Path.GetTempPath(), "deviant-bake-compatibility-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 1024 * 1024));
            var (material, primitive) = Fixture();
            var textures = new Dictionary<int, MaterialBakeTexture>();
            var budget = new AssetMemoryBudget();
            using var retained = budget.Reserve(4096, "generated bake output");
            var expected = MaterialSurfaceMapBaker.Bake(material, primitive, textures, 2, 2, budget.Reserve, CancellationToken.None);
            string key = Fingerprint(material, primitive, textures);
            byte[] encoded = LegacyBytes(expected, key);
            string legacyPath;
            await using (var lease = await manager.AcquireMaterialBakeAsync(key,
                (path, token) => File.WriteAllBytesAsync(path, encoded, token),
                async (path, token) => (await File.ReadAllBytesAsync(path, token)).AsSpan().SequenceEqual(encoded))) legacyPath = lease.Path;
            var observer = new BakeObserver(key);
            using var subscription = AssetLoadDiagnostics.Listener.Subscribe(observer, name => name == AssetLoadDiagnostics.MeasurementName);
            var actual = await new MaterialBakeCache(manager).BakeAsync(material, primitive, textures, 2, 2, budget, _ => Task.CompletedTask);
            Assert.Equal(0, observer.Writes);
            Assert.Equal(legacyPath, Assert.Single(Directory.GetFiles(manager.Location, "*.bake", SearchOption.AllDirectories)));
            Assert.Equal(encoded, await File.ReadAllBytesAsync(legacyPath));
            Assert.Equal(expected.Document.ContentSha256, actual.Document.ContentSha256);
            Assert.Equal(expected.Surface.Rgba8.ToArray(), actual.Surface.Rgba8.ToArray());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OptionalCacheFallbackDoesNotReplaceSourceValidationErrors()
    {
        var root = Path.Combine(Path.GetTempPath(), "deviant-bake-source-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 0));
            var (material, primitive) = Fixture();
            var budget = new AssetMemoryBudget();
            using var retained = budget.Reserve(4096, "generated bake output");
            var sourceFailure = new IOException("Cache storage limit reached.");
            int calls = 0;
            Task Validate(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return ++calls == 1 ? Task.CompletedTask : Task.FromException(sourceFailure);
            }
            var failure = await Assert.ThrowsAsync<IOException>(() => new MaterialBakeCache(manager).BakeAsync(material, primitive,
                new Dictionary<int, MaterialBakeTexture>(), 2, 2, budget, Validate));
            Assert.Same(sourceFailure, failure);
            Assert.Equal(0, manager.ActivePreviewLeases);
            Assert.Equal(4096, budget.UsedBytes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string Fingerprint(MaterialAsset material, MeshPrimitive primitive, IReadOnlyDictionary<int, MaterialBakeTexture> textures) =>
        (string)typeof(MaterialBakeCache).GetMethod("Fingerprint", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
            [material, primitive, textures, 2, 2, 0, null, MaterialSurfaceMapBaker.ComputeGraphHash(material.SurfaceGraph!), CancellationToken.None])!;

    private static byte[] LegacyBytes(MaterialSurfaceMap map, string key)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write("DVBAKE1\0"u8); writer.Write(Convert.FromHexString(key)); writer.Write(Convert.FromHexString(map.GraphSha256));
        writer.Write(map.Surface.Width); writer.Write(map.Surface.Height); writer.Write(map.TextureCoordinateSet);
        writer.Write((long)map.Document.OriginalBytes.Length); writer.Write(map.Document.OriginalBytes.Span);
        writer.Write(Convert.FromHexString(map.Document.ContentSha256)); writer.Flush();
        return output.ToArray();
    }

    private sealed class BakeObserver(string key) : IObserver<KeyValuePair<string, object?>>
    {
        private int writes;
        public int Writes => Volatile.Read(ref writes);
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is AssetLoadMeasurement { Stage: "BakeCacheWrite" } measurement && measurement.Key == key)
                Interlocked.Increment(ref writes);
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private sealed class BakeFailureObserver(string key, Exception failure) : IObserver<KeyValuePair<string, object?>>
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is AssetLoadMeasurement { Stage: "BakeCacheWrite" } measurement && measurement.Key == key &&
                Interlocked.CompareExchange(ref count, 1, 0) == 0) throw failure;
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    [Fact]
    public async Task WarmMapsMatchPixelsAndCorruptEntriesAreRebuilt()
    {
        var root = Path.Combine(Path.GetTempPath(), "Deviant-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 1024 * 1024));
            var cache = new MaterialBakeCache(manager); var budget = new AssetMemoryBudget();
            var (material, primitive) = Fixture(); int validations = 0;
            Task Validate(CancellationToken token) { token.ThrowIfCancellationRequested(); validations++; return Task.CompletedTask; }
            using (budget.Reserve(4096, "test output"))
            {
                var cold = await cache.BakeAsync(material, primitive, new Dictionary<int, MaterialBakeTexture>(), 2, 2, budget, Validate);
                var warm = await cache.BakeAsync(material, primitive, new Dictionary<int, MaterialBakeTexture>(), 2, 2, budget, Validate);
                Assert.Equal(cold.Document.ContentSha256, warm.Document.ContentSha256);
                Assert.Equal(cold.Surface.Rgba8.ToArray(), warm.Surface.Rgba8.ToArray());
                Assert.Equal(cold.GraphSha256, warm.GraphSha256);
                var path = Assert.Single(Directory.GetFiles(Path.Combine(root, "cache"), "*.bake", SearchOption.AllDirectories));
                using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
                { file.Position = file.Length - 1; int last = file.ReadByte(); file.Position--; file.WriteByte((byte)(last ^ 255)); }
                var recovered = await cache.BakeAsync(material, primitive, new Dictionary<int, MaterialBakeTexture>(), 2, 2, budget, Validate);
                Assert.Equal(cold.Document.ContentSha256, recovered.Document.ContentSha256);
                Assert.Equal(cold.Surface.Rgba8.ToArray(), recovered.Surface.Rgba8.ToArray());
                Assert.True(validations >= 6);
                Assert.Equal(0, manager.ActivePreviewLeases);
            }
            Assert.Equal(0, budget.UsedBytes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task GeometryAndGraphChangesUseDistinctEntriesAndCancellationReleasesLeases()
    {
        var root = Path.Combine(Path.GetTempPath(), "Deviant-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 1024 * 1024));
            var cache = new MaterialBakeCache(manager); var budget = new AssetMemoryBudget();
            var (material, primitive) = Fixture();
            Task Validate(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
            using (budget.Reserve(4096, "test output"))
            {
                await cache.BakeAsync(material, primitive, new Dictionary<int, MaterialBakeTexture>(), 2, 2, budget, Validate);
                await cache.BakeAsync(material, primitive with { Positions = [Vector3.Zero, Vector3.UnitX * 2, Vector3.UnitY] },
                    new Dictionary<int, MaterialBakeTexture>(), 2, 2, budget, Validate);
                var changed = material with { SurfaceGraph = material.SurfaceGraph! with
                    { BaseColor = new(MaterialExpressionOperation.Constant, MaterialValueType.Vector3, [], [.5f, .25f, .125f]) } };
                await cache.BakeAsync(changed, primitive, new Dictionary<int, MaterialBakeTexture>(), 2, 2, budget, Validate);
                Assert.Equal(3, Directory.GetFiles(Path.Combine(root, "cache"), "*.bake", SearchOption.AllDirectories).Length);
                using var cancel = new CancellationTokenSource(); cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.BakeAsync(material, primitive,
                    new Dictionary<int, MaterialBakeTexture>(), 2, 2, budget, Validate, cancel.Token));
                Assert.Equal(0, manager.ActivePreviewLeases);
            }
            Assert.Equal(0, budget.UsedBytes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static (MaterialAsset, MeshPrimitive) Fixture()
    {
        var identity = new ResourceIdentity("generated-cache", 7); var revision = new SourceRevision(identity.IndexContentId, "revision");
        var graph = new MaterialSurfaceGraph(new(MaterialExpressionOperation.Constant, MaterialValueType.Vector3, [], [.25f, .5f, .75f]), null, null, [], [], []);
        var material = new MaterialAsset(identity, revision, "generated", [], [], []) { SurfaceGraph = graph };
        var primitive = new MeshPrimitive("surface", PrimitiveTopology.Triangles,
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2], IndexElementFormat.UInt16, [],
            TextureCoordinates: [[Vector2.Zero, Vector2.UnitX, Vector2.UnitY]]);
        return (material, primitive);
    }
}
