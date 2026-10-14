using System.Buffers.Binary;
using System.Text;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Export.Models;
using Xunit;

namespace Deviant.Model.Tests;

public sealed partial class FbxGeometryExporterTests
{
    [Fact]
    public async Task BakedMapsUseSeparateStandardColourAndTransparencyConnections()
    {
        var root = NewRoot(); var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        var budget = new ReservationCounter();
        try
        {
            var decoded = CreateDecoded(CreateAsset());
            var material = BakedMaterial(decoded, budget.Reserve);
            var report = await new FbxGeometryExporter().ExportAsync(new(decoded, new(decoded.Identity, "selected"), budget.Reserve, [material]),
                source, Path.Combine(root, "baked.fbx"), new(MediaPackaging: FbxMediaPackaging.Embedded));
            var document = ParseBinary7400(await File.ReadAllBytesAsync(Path.Combine(root, "baked.fbx")));
            var objects = document.Roots.Single(node => node.Name == "Objects");
            Assert.Equal(2, objects.Children.Count(node => node.Name == "Video"));
            var connections = document.Roots.Single(node => node.Name == "Connections").Children;
            Assert.Contains(connections, node => node.Properties.Count == 4 && Encoding.UTF8.GetString(node.Properties[3].Payload) == "DiffuseColor");
            Assert.Contains(connections, node => node.Properties.Count == 4 && Encoding.UTF8.GetString(node.Properties[3].Payload) == "TransparentColor");
            var properties = objects.Children.Single(node => node.Name == "Material").Children.Single(node => node.Name == "Properties70").Children;
            var factor = properties.Single(node => Encoding.UTF8.GetString(node.Properties[0].Payload) == "TransparencyFactor");
            Assert.Equal(1, BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(factor.Properties[4].Payload)));
            Assert.Equal(2, report.Losses.Count(loss => loss.Code == "DerivedMaterialMapExported"));
            Assert.Equal(2, report.DerivedMaterialMaps.Count);
            Assert.Equal(2, report.DerivedMaterialMaps.Select(map => map.Sha256).Distinct().Count());
            Assert.Equal(0, budget.ActiveReservations);
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(-1f)]
    public async Task AppendedCornerUvSetPreservesOriginalControlPointsAndUvSets(float offset)
    {
        var root=NewRoot();var source=Path.Combine(root,"source");Directory.CreateDirectory(source);
        try
        {
            var decoded=CreateDecoded(CreateAsset());var material=BakedMaterial(decoded,Reserve);
            var asset = decoded.Value!;
            asset = asset with { Primitives = asset.Primitives.Select(primitive => primitive with
            {
                TextureCoordinates = primitive.TextureCoordinates!.Select(set => (IReadOnlyList<System.Numerics.Vector2>)
                    set.Select(uv => uv + new System.Numerics.Vector2(offset, 0)).ToArray()).ToArray()
            }).ToArray() };
            decoded = CreateDecoded(asset);
            var corners=new System.Numerics.Vector2[]{new(.1f,.1f),new(.9f,.1f),new(.1f,.9f)};
            material=material with {BakedMaps=material.BakedMaps.Select(map=>map with {TextureCoordinateSet=2,PolygonCornerCoordinates=corners}).ToArray()};
            var output=Path.Combine(root,"atlas.fbx");
            await new FbxGeometryExporter().ExportAsync(new(decoded,new(decoded.Identity,"selected"),Reserve,[material]),source,output,new(MediaPackaging:FbxMediaPackaging.Embedded));
            var document=ParseBinary7400(await File.ReadAllBytesAsync(output));
            var geometry=document.Roots.Single(node=>node.Name=="Objects").Children.Single(node=>node.Name=="Geometry");
            Assert.Equal(9u,geometry.Children.Single(node=>node.Name=="Vertices").Properties[0].Count);
            var sets=geometry.Children.Where(node=>node.Name=="LayerElementUV").ToArray();Assert.Equal(3,sets.Length);
            Assert.All(sets.Take(2),set=>Assert.Equal("ByVertice",Encoding.UTF8.GetString(set.Children.Single(node=>node.Name=="MappingInformationType").Properties[0].Payload)));
            Assert.Equal("ByPolygonVertex",Encoding.UTF8.GetString(sets[2].Children.Single(node=>node.Name=="MappingInformationType").Properties[0].Payload));
            Assert.Equal(6u,sets[2].Children.Single(node=>node.Name=="UV").Properties[0].Count);
            Assert.Equal(asset.Primitives[0].TextureCoordinates![0].SelectMany(uv => new double[] { uv.X, uv.Y }),
                ReadDoubles(sets[0].Children.Single(node => node.Name == "UV").Properties.Single()));
            Assert.Equal(corners.SelectMany(uv => new double[] { uv.X, uv.Y }),
                ReadDoubles(sets[2].Children.Single(node => node.Name == "UV").Properties.Single()));
        }
        finally{DeleteRoot(root);}
    }

    [Fact]
    public async Task MismatchedColourAndCoverageAtlasCoordinatesAreRejectedBeforePublication()
    {
        var root=NewRoot();var source=Path.Combine(root,"source");Directory.CreateDirectory(source);
        try
        {
            var decoded=CreateDecoded(CreateAsset());var material=BakedMaterial(decoded,Reserve);
            var colour=material.BakedMaps[0] with {TextureCoordinateSet=2,PolygonCornerCoordinates=[new(.1f,.1f),new(.9f,.1f),new(.1f,.9f)]};
            var coverage=material.BakedMaps[1] with {TextureCoordinateSet=2,PolygonCornerCoordinates=[new(0,0),new(1,0),new(0,1)]};
            material=material with {BakedMaps=[colour,coverage]};var output=Path.Combine(root,"atlas.fbx");
            var error=await Assert.ThrowsAsync<FbxGeometryExportException>(()=>new FbxGeometryExporter().ExportAsync(
                new(decoded,new(decoded.Identity,"selected"),Reserve,[material]),source,output));
            Assert.Equal("BakedCornerUvConflict",error.Code);Assert.False(File.Exists(output));
        }
        finally{DeleteRoot(root);}
    }

    [Fact]
    public async Task FailedBakedSourceValidationPreservesPriorExportAndRemovesNewMedia()
    {
        var root = NewRoot(); var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        var output = Path.Combine(root, "baked.fbx"); var prior = Encoding.UTF8.GetBytes("previous export");
        await File.WriteAllBytesAsync(output, prior);
        var budget = new ReservationCounter();
        try
        {
            var decoded = CreateDecoded(CreateAsset()); var material = BakedMaterial(decoded, budget.Reserve);
            int validations = 0;
            var failing = material.BakedMaps[0] with { ValidateSources = _ => ++validations > 1
                ? Task.FromException(new ArchiveException("SourceChanged", "Generated source changed.")) : Task.CompletedTask };
            material = material with { BakedMaps = [failing, material.BakedMaps[1]] };
            await Assert.ThrowsAsync<ArchiveException>(() => new FbxGeometryExporter().ExportAsync(
                new(decoded, new(decoded.Identity, "selected"), budget.Reserve, [material]), source, output, new(Overwrite: true)));
            Assert.Equal(prior, await File.ReadAllBytesAsync(output));
            Assert.Empty(Directory.GetFiles(root, "*.png", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
            Assert.Equal(0, budget.ActiveReservations);
        }
        finally { DeleteRoot(root); }
    }

    private static FbxMaterialExportInput BakedMaterial(DecodeResult<GeometryAsset> decoded, FbxExportMemoryReservation reserve)
    {
        var identity = new ResourceIdentity(decoded.Identity.IndexContentId, 3);
        var colour = new MaterialExpression(MaterialExpressionOperation.Constant, MaterialValueType.Vector3, [], [.2f, .4f, .8f]);
        var opacity = new MaterialExpression(MaterialExpressionOperation.Constant, MaterialValueType.Scalar, [], [.25f]);
        var material = new MaterialAsset(identity, decoded.Revision, "generated", [], [], [])
            { SurfaceGraph = new(colour, opacity, null, [], [], []) };
        var primitive = decoded.Value!.Primitives[0];
        var map = MaterialSurfaceMapBaker.Bake(material, primitive, new Dictionary<int, MaterialBakeTexture>(), 4, 4, reserve);
        var transparency = MaterialSurfaceMapBaker.CreateTransparencyMap(map);
        var baseMap = new FbxBakedMaterialMapInput(MaterialTextureRole.BaseColor, 0, map.Document, map.Surface, map.GraphSha256,
            [primitive.Key], [], _ => Task.CompletedTask);
        return new(material, [new(Ref("material-source"), new(ReferenceStatus.Unique, [identity]))], [], _ => Task.CompletedTask)
        { BakedMaps = [baseMap, baseMap with { Role = MaterialTextureRole.Opacity, Document = transparency.Document, Surface = transparency.Surface }] };
    }
}
