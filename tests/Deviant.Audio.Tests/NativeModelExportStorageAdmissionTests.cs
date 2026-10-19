using System.Numerics;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Export.Models;
using Deviant.Export.Textures;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class NativeModelExportStorageAdmissionTests
{
    [Theory]
    [InlineData("texture")]
    [InlineData("maps")]
    [InlineData("media")]
    [InlineData("encoded")]
    public async Task PreparationRejectsUnadmittedStorageWithoutPublishingOrLeakingLeases(string kind)
    {
        using var fixture = new Fixture();
        var staged = await fixture.StageAsync();
        CacheLease? heldBundle = null;
        try
        {
            if (kind == "media")
            {
                var prepared = await fixture.Preparation.AcquireMapsAsync("generated-maps", () => Task.FromResult<IReadOnlyList<FbxStagedMaterialMapInput>>([staged]),
                    Validate, fixture.Budget);
                await prepared.Lease.DisposeAsync();
                heldBundle = await fixture.Manager.AcquirePreparedAsync("export-maps", "generated-maps", ".maps",
                    (_, _) => throw new InvalidOperationException("The existing bundle must be reused."), (_, _) => Task.FromResult(true));
                File.Delete(Assert.Single(Directory.GetFiles(fixture.CacheRoot, "*.png", SearchOption.AllDirectories)));
                await fixture.Manager.ReconcileAsync();
                await fixture.Manager.SetLimitAsync(fixture.Manager.UsageBytes + 1);
            }
            else await fixture.Manager.SetLimitAsync(256);
            long retainedBytes = PhysicalBytes(fixture.CacheRoot);
            int retainedLeases = fixture.Manager.ActivePreviewLeases;

            var error = await Assert.ThrowsAsync<IOException>(async () =>
            {
                switch (kind)
                {
                    case "texture":
                        var texture = await fixture.Preparation.AcquireTextureAsync("generated-texture",
                            _ => Task.FromResult((fixture.Map.Document, fixture.Map.Surface)), Validate, fixture.Budget);
                        await texture.Lease.DisposeAsync();
                        break;
                    case "maps":
                    case "media":
                        var maps = await fixture.Preparation.AcquireMapsAsync("generated-maps",
                            () => kind == "media" ? throw new InvalidOperationException("The existing bundle must be reused.")
                                : Task.FromResult<IReadOnlyList<FbxStagedMaterialMapInput>>([staged]), Validate, fixture.Budget);
                        await maps.Lease.DisposeAsync();
                        break;
                    case "encoded":
                        await fixture.Preparation.PublishTextureMediaAsync("generated-texture", fixture.Map.Document, fixture.Map.Surface,
                            fixture.SourceRoot, Path.Combine(fixture.Root, "exports"), "texture.png", Validate, fixture.Budget);
                        break;
                }
            });

            Assert.Equal("CacheStorageLimit", error.Data["ErrorCode"]);
            Assert.Equal(retainedBytes, PhysicalBytes(fixture.CacheRoot));
            Assert.True(PhysicalBytes(fixture.CacheRoot) <= fixture.Manager.LimitBytes);
            Assert.Equal(retainedLeases, fixture.Manager.ActivePreviewLeases);
            Assert.Equal(0, fixture.Budget.UsedBytes);
            Assert.False(File.Exists(Path.Combine(fixture.Root, "exports", "texture.png")));
        }
        finally { if (heldBundle is not null) await heldBundle.DisposeAsync(); }
    }

    private static long PhysicalBytes(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
    private static Task Validate(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Deviant-export-storage-tests", Guid.NewGuid().ToString("N"));
        public string CacheRoot => Path.Combine(Root, "cache");
        public string SourceRoot => Path.Combine(Root, "source");
        public CacheManager Manager { get; }
        public NativeModelExportPreparationCache Preparation { get; }
        public AssetMemoryBudget Budget { get; } = new();
        public MaterialSurfaceMap Map { get; }
        public Fixture()
        {
            Directory.CreateDirectory(SourceRoot);
            Manager = new(Path.Combine(Root, "settings"), new(CacheRoot, 4 * 1024 * 1024));
            Preparation = new(Manager);
            var identity = new ResourceIdentity("generated-export-storage", 1);
            var revision = new SourceRevision(identity.IndexContentId, "revision");
            var material = new MaterialAsset(identity, revision, "generated", [], [], []) { SurfaceGraph = new(
                new(MaterialExpressionOperation.Constant, MaterialValueType.Vector3, [], [.25f, .5f, .75f]), null, null, [], [], []) };
            var primitive = new MeshPrimitive("surface", PrimitiveTopology.Triangles, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                [0, 1, 2], IndexElementFormat.UInt16, [], TextureCoordinates: [[Vector2.Zero, Vector2.UnitX, Vector2.UnitY]]);
            Map = MaterialSurfaceMapBaker.Bake(material, primitive, new Dictionary<int, MaterialBakeTexture>(), 2, 2, Budget.Reserve);
        }
        public async Task<FbxStagedMaterialMapInput> StageAsync()
        {
            string directory = Path.Combine(Root, "maps");
            var encoded = await new TextureExportService().ExportAsync(Map.Document, Map.Surface, AssetExportFormat.Png,
                SourceRoot, directory, "map.png", expectedRevision: Map.Document.Revision, preCommitValidation: Validate);
            return new(MaterialTextureRole.BaseColor, Map.TextureCoordinateSet, Map.Document.Identity!, Map.Document.Revision!, Map.Document.ContentSha256,
                Map.Surface.Width, Map.Surface.Height, Map.Surface.IsSrgb, Path.Combine(directory, encoded.RelativePath), encoded.Length,
                encoded.Sha256, Map.GraphSha256, ["surface"], [], Validate);
        }
        public void Dispose() { Manager.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
