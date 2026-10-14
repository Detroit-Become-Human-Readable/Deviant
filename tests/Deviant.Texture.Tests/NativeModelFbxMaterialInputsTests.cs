using System.Numerics;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Export.Models;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed partial class NativeTextureSessionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UnsupportedGraphTextureKeepsGeometryExportAndReleasesPreparationMemory(bool cameraDependent, bool mixedSources)
    {
        using var fixture = new NativeFrameArchive("unsupported-format", includeExportFrames: true);
        var archive = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var revision = new SourceRevision(archive.IndexContentId, archive.PhysicalSourceStamp);
        await using var catalogs = new TextureCatalogSession(archive, revision);
        await using var textures = new NativeTextureSession(catalogs);
        var budget = new AssetMemoryBudget();
        using var container = await NativeContainerLease.OpenAsync(archive, new(archive.IndexContentId, 1), budget);
        var meshIdentity = container.Directory.Objects[2].Identity;
        var materialIdentity = container.Directory.Objects[3].Identity;
        var reference = new ResourceReference(IdentifierNamespace.Native, "100", materialIdentity, 2137);
        var uv = new MaterialExpression(MaterialExpressionOperation.Input, MaterialValueType.Vector2, [], Input: MaterialInput.Uv0);
        if (cameraDependent)
            uv = new(MaterialExpressionOperation.Swizzle, MaterialValueType.Vector2,
                [new(MaterialExpressionOperation.Input, MaterialValueType.Vector3, [], Input: MaterialInput.ViewNormal)], Components: "xy");
        var sample = new MaterialExpression(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4, [uv], TextureSlot: 0);
        var references = new List<ResourceReference> { reference };
        if (mixedSources)
        {
            references.Add(new(IdentifierNamespace.Native, "101", materialIdentity, 2137));
            sample = new(MaterialExpressionOperation.Add, MaterialValueType.Vector4,
                [sample, new(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4, [uv], TextureSlot: 1)]);
        }
        var material = new MaterialAsset(materialIdentity, revision, "generated", [], references, [])
            { SurfaceGraph = new(sample, null, null, [cameraDependent ? MaterialInput.ViewNormal : MaterialInput.Uv0], mixedSources ? [0, 1] : [0], []) };
        var primitive = new MeshPrimitive("surface", PrimitiveTopology.Triangles, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            [0, 1, 2], IndexElementFormat.UInt16, [new("material", 0, 3, [new(IdentifierNamespace.Native, "103", meshIdentity, 2133)])],
            TextureCoordinates: [[Vector2.Zero, Vector2.UnitX, Vector2.UnitY]]);
        var geometry = new GeometryAsset(new("generated", "1"), [], [primitive], new Dictionary<string, string>());
        var materials = new Dictionary<ResourceIdentity, RenderMaterial> { { materialIdentity, new(material, new Dictionary<int, RenderTexture>()) } };
        var baseline = budget.UsedBytes;
        using (var prepared = await new NativeModelSession(textures, budget).CreateFbxMaterialInputsAsync(container,
            new(archive.IndexContentId, 0), new(meshIdentity, 0, geometry), materials))
        {
            var input = Assert.Single(prepared.Inputs);
            Assert.Empty(input.Textures); Assert.Empty(input.AllMaps); Assert.Empty(input.GraphSources);
            var notice = cameraDependent ? "camera or world-space" : "UnsupportedNativeTextureFormat";
            Assert.Null(input.Material.SurfaceGraph);
            Assert.Contains(input.BakingDiagnostics, diagnostic => diagnostic.Contains(notice, StringComparison.Ordinal));
            var result = await new FbxGeometryExporter().ExportAsync(new(new(geometry, meshIdentity, revision, [], [], [], [],
                new Dictionary<string, string>()), new(meshIdentity, "detail"), budget.Reserve, prepared.Inputs,
                CoordinateMetadata: FbxCoordinateMetadataPolicy.PreserveSourceNumbers),
                Path.Combine(Path.GetDirectoryName(fixture.IndexPath)!, "source"), Path.ChangeExtension(fixture.IndexPath, ".fbx"));
            Assert.Equal(1, result.MeshCount);
            Assert.Contains(result.Losses, loss => loss.Code == "MaterialBakeUnsupported" && loss.Detail.Contains(notice, StringComparison.Ordinal));
        }
        Assert.Equal(baseline + container.PreparedMetadataBytes, budget.UsedBytes); container.EnsureUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FbxFactoryUsesOriginalBaseMipAndRetainsSourceUntilDisposed(bool metadataOnly)
    {
        using var fixture = new NativeFrameArchive(includeExportFrames: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var revision = new SourceRevision(snapshot.IndexContentId,snapshot.PhysicalSourceStamp);
        await using var catalogs = new TextureCatalogSession(snapshot,revision);
        await using var textures = new NativeTextureSession(catalogs);
        var budget = new AssetMemoryBudget();
        using var container = metadataOnly
            ? await NativeContainerLease.OpenMetadataAsync(snapshot,new(snapshot.IndexContentId,1),budget)
            : await NativeContainerLease.OpenAsync(snapshot,new(snapshot.IndexContentId,1),budget);
        var meshIdentity=container.Directory.Objects[2].Identity; var materialIdentity=container.Directory.Objects[3].Identity;
        var materialReference=new ResourceReference(IdentifierNamespace.Native,"103",meshIdentity,2133);
        var textureReference=new ResourceReference(IdentifierNamespace.Native,"100",materialIdentity,2137);
        var binding=new MaterialTextureBinding(0,textureReference,MaterialTextureRole.BaseColor,0,Vector2.One,Vector2.Zero,new(true,"generated","typed test slot"));
        var material=new MaterialAsset(materialIdentity,revision,"generated",[binding],[textureReference],[]);
        var primitive=new MeshPrimitive("mesh",PrimitiveTopology.Triangles,[Vector3.Zero,Vector3.UnitX,Vector3.UnitY],[0,1,2],IndexElementFormat.UInt16,
            [new("surface",0,3,[materialReference])],TextureCoordinates:[[Vector2.Zero,Vector2.UnitX,Vector2.UnitY]]);
        var geometry=new GeometryAsset(new("generated","1"),[],[primitive],new Dictionary<string,string>());
        var mesh=new RenderMesh(meshIdentity,0,geometry);
        var materials=new Dictionary<ResourceIdentity,RenderMaterial>{{materialIdentity,new(material,new Dictionary<int,RenderTexture>())}};
        var model=new NativeModelSession(textures,budget);var before=budget.UsedBytes;
        using(var inputs=await model.CreateFbxMaterialInputsAsync(container,new(snapshot.IndexContentId,0),mesh,materials))
        {
            var exported=Assert.Single(Assert.Single(inputs.Inputs).Textures);
            var source=Assert.IsType<NativeTextureSource>((await textures.ReadForNativeAsync(container.Directory.Objects[0].Identity,new(snapshot.IndexContentId,0),container)).Source);
            var original=new NativeTextureDecoder().ReadDetailed(source);
            var pixels=await new DdsTextureDecoder().DecodeSubresourceAsync(original.Document,original.Document.DefaultSelection);
            Assert.Equal(0,exported.Surface.MipIndex);
            Assert.Equal(original.Document.Info.Width,exported.Surface.Width);
            Assert.Equal(original.Document.Info.Height,exported.Surface.Height);
            Assert.Equal(pixels.Rgba8.ToArray(),exported.Surface.Rgba8.ToArray());
            Assert.Equal(original.Document.ContentSha256,exported.Document.ContentSha256);
            await exported.ValidateSourceRevision(CancellationToken.None);
            var output=Path.Combine(Path.GetDirectoryName(fixture.IndexPath)!,$"material-{Guid.NewGuid():N}.fbx");
            var decodedGeometry=new DecodeResult<GeometryAsset>(geometry,meshIdentity,revision,[],[],[],[],new Dictionary<string,string>());
            var report=await new FbxGeometryExporter().ExportAsync(new(decodedGeometry,new(meshIdentity,"detail"),budget.Reserve,
                inputs.Inputs,CoordinateMetadata:FbxCoordinateMetadataPolicy.PreserveSourceNumbers),Path.Combine(Path.GetDirectoryName(output)!,"source"),output,
                new(MediaPackaging:FbxMediaPackaging.Embedded));
            Assert.Single(report.MaterialTextures!);
            var badJoin=exported.DocumentJoin! with {Evidence=new(false,"generated","unqualified")};
            var invalid=Assert.Single(inputs.Inputs) with {Textures=[exported with {DocumentJoin=badJoin}]};
            var error=await Assert.ThrowsAsync<FbxGeometryExportException>(()=>new FbxGeometryExporter().ExportAsync(
                new(decodedGeometry,new(meshIdentity,"detail"),budget.Reserve,[invalid],CoordinateMetadata:FbxCoordinateMetadataPolicy.PreserveSourceNumbers),
                Path.Combine(Path.GetDirectoryName(output)!,"source"),output+".invalid"));
            Assert.Equal("TextureDocumentJoinInvalid",error.Code);
            Assert.False(File.Exists(output+".invalid"));
            Assert.True(budget.UsedBytes>before);
        }
        Assert.Equal(before + container.PreparedMetadataBytes,budget.UsedBytes);
    }

    [Fact]
    public async Task FbxGraphSourcesKeepFingerprintsAndPinsAfterOriginalPixelsAreConsumed()
    {
        using var fixture = new NativeFrameArchive(includeExportFrames: true);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        await using var catalogs = new TextureCatalogSession(snapshot, revision);
        await using var textures = new NativeTextureSession(catalogs);
        var budget = new AssetMemoryBudget();
        using var container = await NativeContainerLease.OpenAsync(snapshot, new(snapshot.IndexContentId, 1), budget);
        var meshIdentity = container.Directory.Objects[2].Identity; var materialIdentity = container.Directory.Objects[3].Identity;
        var materialReference = new ResourceReference(IdentifierNamespace.Native, "103", meshIdentity, 2133);
        var textureReference = new ResourceReference(IdentifierNamespace.Native, "100", materialIdentity, 2137);
        var uv = new MaterialExpression(MaterialExpressionOperation.Input, MaterialValueType.Vector2, [], Input: MaterialInput.Uv0);
        var sample = new MaterialExpression(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4, [uv], TextureSlot: 0);
        var material = new MaterialAsset(materialIdentity, revision, "generated", [], [textureReference], [])
            { SurfaceGraph = new(sample, null, null, [MaterialInput.Uv0], [0], []) };
        var primitive = new MeshPrimitive("mesh", PrimitiveTopology.Triangles, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            [0, 1, 2], IndexElementFormat.UInt16, [new("surface", 0, 3, [materialReference])],
            TextureCoordinates: [[Vector2.Zero, Vector2.UnitX, Vector2.UnitY]]);
        var geometry = new GeometryAsset(new("generated", "1"), [], [primitive], new Dictionary<string, string>());
        var materials = new Dictionary<ResourceIdentity, RenderMaterial> { { materialIdentity, new(material, new Dictionary<int, RenderTexture>()) } };
        var before = budget.UsedBytes;
        using (var inputs = await new NativeModelSession(textures, budget).CreateFbxMaterialInputsAsync(container,
            new(snapshot.IndexContentId, 0), new(meshIdentity, 0, geometry), materials))
        {
            var input = Assert.Single(inputs.Inputs); Assert.Empty(input.Textures);
            var fingerprint = Assert.Single(input.GraphSources); var map = Assert.Single(input.StagedMaps);
            Assert.Equal(fingerprint.DocumentSha256, Assert.Single(map.Sources).PayloadSha256);
            Assert.Equal(fingerprint.NativeIdentity, Assert.Single(map.Sources).Identity);
            Assert.Empty(input.BakedMaps); Assert.True(File.Exists(map.Path));
            await fingerprint.ValidateSourceRevision(CancellationToken.None);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fingerprint.ValidateSourceRevision(canceled.Token));
            var output = Path.Combine(Path.GetDirectoryName(fixture.IndexPath)!, $"graph-{Guid.NewGuid():N}.fbx");
            var decoded = new DecodeResult<GeometryAsset>(geometry, meshIdentity, revision, [], [], [], [], new Dictionary<string, string>());
            var report = await new FbxGeometryExporter().ExportAsync(new(decoded, new(meshIdentity, "detail"), budget.Reserve,
                inputs.Inputs, CoordinateMetadata: FbxCoordinateMetadataPolicy.PreserveSourceNumbers),
                Path.Combine(Path.GetDirectoryName(output)!, "source"), output);
            Assert.Single(report.DerivedMaterialMaps);
            var invalid = input with { GraphSources = [fingerprint with { DocumentSha256 = new string('0', 64) }] };
            var error = await Assert.ThrowsAsync<FbxGeometryExportException>(() => new FbxGeometryExporter().ExportAsync(
                new(decoded, new(meshIdentity, "detail"), budget.Reserve, [invalid], CoordinateMetadata: FbxCoordinateMetadataPolicy.PreserveSourceNumbers),
                Path.Combine(Path.GetDirectoryName(output)!, "source"), output + ".invalid"));
            Assert.Equal("MaterialMapSourceMismatch", error.Code);
            Assert.False(File.Exists(output + ".invalid"));
        }
        Assert.Equal(before + container.PreparedMetadataBytes, budget.UsedBytes);
    }

    [Fact]
    public async Task FbxMaterialPreparationCancellationLeavesSharedBudgetUsable()
    {
        using var fixture=new NativeFrameArchive(includeExportFrames:true);
        var snapshot=await new ArchiveReader().OpenAsync(fixture.IndexPath);var revision=new SourceRevision(snapshot.IndexContentId,snapshot.PhysicalSourceStamp);
        await using var catalogs=new TextureCatalogSession(snapshot,revision);await using var textures=new NativeTextureSession(catalogs);
        var budget=new AssetMemoryBudget();using var container=await NativeContainerLease.OpenAsync(snapshot,new(snapshot.IndexContentId,1),budget);
        var geometry=new GeometryAsset(new("generated","1"),[],[],new Dictionary<string,string>());
        var mesh=new RenderMesh(container.Directory.Objects[2].Identity,0,geometry);var model=new NativeModelSession(textures,budget);
        using var canceled=new CancellationTokenSource();canceled.Cancel();var before=budget.UsedBytes;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>model.CreateFbxMaterialInputsAsync(container,new(snapshot.IndexContentId,0),mesh,
            new Dictionary<ResourceIdentity,RenderMaterial>(),canceled.Token));
        Assert.Equal(before + container.PreparedMetadataBytes,budget.UsedBytes);container.EnsureUnchanged();
    }
}
