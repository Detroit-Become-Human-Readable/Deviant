using System.Numerics;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Export.Models;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class MaterialBakeCacheTests
{
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
