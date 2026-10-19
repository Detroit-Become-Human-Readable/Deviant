using System.Numerics;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Export.Models;
using Deviant.Export.Textures;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed partial class NativeTextureSessionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    public async Task FbxMaterialPreparationAndPublicationContinueWhenOptionalCacheCannotFit(long cacheBytes)
    {
        using var fixture = new NativeFrameArchive(includeExportFrames: true);
        string root = Path.Combine(Path.GetTempPath(), "Deviant-material-fallback-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var manager = new CacheManager(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), cacheBytes));
            var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
            var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
            await using var catalogs = new TextureCatalogSession(snapshot, revision);
            await using var textures = new NativeTextureSession(catalogs);
            var budget = new AssetMemoryBudget();
            using var container = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, 1), budget);
            var meshIdentity = container.Directory.Objects[2].Identity;
            var materialIdentity = container.Directory.Objects[3].Identity;
            var textureReference = new ResourceReference(IdentifierNamespace.Native, "100", materialIdentity, 2137);
            var uv = new MaterialExpression(MaterialExpressionOperation.Input, MaterialValueType.Vector2, [], Input: MaterialInput.Uv0);
            var sample = new MaterialExpression(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4, [uv], TextureSlot: 0);
            var material = new MaterialAsset(materialIdentity, revision, "generated",
                [new(0, textureReference, MaterialTextureRole.BaseColor, 0, Vector2.One, Vector2.Zero, new(true, "generated", "generated slot"))],
                [textureReference], []) { SurfaceGraph = new(sample, null, null, [MaterialInput.Uv0], [0], []) };
            var primitive = new MeshPrimitive("surface", PrimitiveTopology.Triangles, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                [0, 1, 2], IndexElementFormat.UInt16,
                [new("material", 0, 3, [new(IdentifierNamespace.Native, "103", meshIdentity, 2133)])],
                TextureCoordinates: [[Vector2.Zero, Vector2.UnitX, Vector2.UnitY]]);
            var geometry = new GeometryAsset(new("generated", "1"), [], [primitive], new Dictionary<string, string>());
            var materials = new Dictionary<ResourceIdentity, RenderMaterial> { [materialIdentity] = new(material, new Dictionary<int, RenderTexture>()) };
            var source = Assert.IsType<NativeTextureSource>((await textures.ReadForNativeAsync(container.Directory.Objects[0].Identity,
                new(snapshot.IndexContentId, 0), container)).Source);
            var expected = new NativeTextureDecoder().ReadDetailed(source).Document;
            var pixels = await new DdsTextureDecoder().DecodeSubresourceAsync(expected, expected.DefaultSelection);
            long baseline = budget.UsedBytes;
            long preparedBefore = container.PreparedMetadataBytes;
            // One direct texture plus one derived map fit exactly; a second bake would exceed this limit.
            var options = new FbxGeometryExportOptions(MaximumTexturePixels: checked((long)pixels.Width * pixels.Height * 2));
            using (var prepared = await new NativeModelSession(textures, budget).CreateFbxMaterialInputsAsync(container,
                new(snapshot.IndexContentId, 0), new(meshIdentity, 0, geometry), materials, options: options,
                preparationCache: new NativeModelExportPreparationCache(manager)))
            {
                var input = Assert.Single(prepared.Inputs);
                var texture = Assert.Single(input.Textures);
                Assert.Equal(expected.ContentSha256, texture.Document.ContentSha256);
                Assert.Equal(pixels.Rgba8.ToArray(), texture.Surface.Rgba8.ToArray());
                await texture.ValidateSourceRevision(default);
                Assert.True(input.StagedMaps.Count > 0, string.Join(Environment.NewLine, input.BakingDiagnostics));
                var staged = Assert.Single(input.StagedMaps);
                Assert.Equal(expected.ContentSha256, Assert.Single(staged.Sources).PayloadSha256);
                Assert.NotNull(texture.PublishPreparedMedia);
                string directRoot = Path.Combine(root, "direct-media");
                var direct = await texture.PublishPreparedMedia(directRoot, "original.png", default);
                Assert.Equal(expected.ContentSha256, direct.InputPayloadSha256);
                Assert.Equal(AssetExportFormat.Png, direct.Format);
                Assert.Equal(direct.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    await File.ReadAllBytesAsync(Path.Combine(directRoot, direct.RelativePath)))).ToLowerInvariant());
                var referenceRoot=Path.Combine(root,"reference-media");
                var reference=await new TextureExportService().ExportAsync(texture.Document,texture.Surface,AssetExportFormat.Png,
                    snapshot.SourceRoot,referenceRoot,"reference.png",expectedRevision:revision,
                    preCommitValidation:texture.ValidateSourceRevision);
                Assert.Equal(reference.Sha256,direct.Sha256);
                Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(referenceRoot,reference.RelativePath)),
                    await File.ReadAllBytesAsync(Path.Combine(directRoot,direct.RelativePath)));
                var decoded = new DecodeResult<GeometryAsset>(geometry, meshIdentity, revision, [], [], [], [], new Dictionary<string, string>());
                var exported = await new FbxGeometryExporter().ExportAsync(new(decoded, new(meshIdentity, "detail"), budget.Reserve,
                    prepared.Inputs, CoordinateMetadata: FbxCoordinateMetadataPolicy.PreserveSourceNumbers), snapshot.SourceRoot,
                    Path.Combine(root, "model.fbx"), options);
                Assert.Empty(exported.MaterialTextures!);
                Assert.Single(exported.DerivedMaterialMaps);
                Assert.Contains(exported.Losses, loss => loss.Code == "MaterialTextureSupersededByBake");
                Assert.True(File.Exists(Path.Combine(root, "model.fbx")));
            }
            Assert.Equal(baseline + container.PreparedMetadataBytes - preparedBefore, budget.UsedBytes);
            Assert.Equal(0, manager.ActivePreviewLeases);
            Assert.Empty(Directory.EnumerateFiles(manager.Location, "*", SearchOption.AllDirectories));
            container.EnsureUnchanged();
        }
        finally { Directory.Delete(root, true); }
    }
}
