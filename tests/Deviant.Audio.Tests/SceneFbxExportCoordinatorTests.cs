using System.Numerics;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Export.Models;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class SceneFbxExportCoordinatorTests
{
    [Fact]
    public async Task DefaultSceneExportWritesBinaryFbx()
    {
        var root = Path.Combine(Path.GetTempPath(), "deviant-scene-format", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "source"));
        var budget = new AssetMemoryBudget();
        var id = new ResourceIdentity("generated", 1); var revision = new SourceRevision("generated", "initial");
        var primitive = new MeshPrimitive("triangle", PrimitiveTopology.Triangles,
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2], IndexElementFormat.UInt16, []);
        var mesh = new RenderMesh(id, 0, new(new("generated", "mesh"), [], [primitive], new Dictionary<string, string>()));
        var occurrence = new RenderOccurrence("one", id, id, "Generated mesh", mesh.Key, Matrix4x4.Identity,
            new(Vector3.Zero, Vector3.One), []);
        using var snapshot = new RenderAssetSnapshot(revision, new Dictionary<string, RenderMesh> { [mesh.Key] = mesh },
            [occurrence], new Dictionary<ResourceIdentity, RenderMaterial>(), occurrence.WorldBounds,
            [], [], new(1, 1, 0, 1, 1, 1), [], budget);
        try
        {
            var destination = Path.Combine(root, "scene.fbx");
            await new SceneFbxExportCoordinator().ExportAsync(snapshot, new([new("one")]),
                Path.Combine(root, "source"), destination,
                (_, _) => Task.FromResult(new NativeModelFbxMaterialInputs(budget, [])),
                _ => Task.CompletedTask);
            var bytes = await File.ReadAllBytesAsync(destination);
            Assert.True(bytes.AsSpan(0, 23).SequenceEqual("Kaydara FBX Binary  \0\x1a\0"u8));
            Assert.Equal(0, budget.UsedBytes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RepeatedInstancesExportWithScopedMaterialsAndReleaseAllPlanningMemory(bool merge, bool preserveInstances)
    {
        var root = Path.Combine(Path.GetTempPath(), "deviant-scene-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "source"));
        var budget = new AssetMemoryBudget();
        var identity = new ResourceIdentity("generated-scene", 1);
        var revision = new SourceRevision(identity.IndexContentId, "generated-revision");
        var material = new MaterialAsset(new(identity.IndexContentId, 2), revision, "generated", [], [], []);
        var reference = new ResourceReference(IdentifierNamespace.Native, "material", identity);
        var primitive = new MeshPrimitive("part", PrimitiveTopology.Triangles, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            [0,1,2], IndexElementFormat.UInt16, [new("surface", 0, 3, [reference])],
            [Vector3.UnitZ,Vector3.UnitZ,Vector3.UnitZ], TextureCoordinates: [[Vector2.Zero,Vector2.UnitX,Vector2.UnitY]]);
        var mesh = new RenderMesh(identity, 0, new(new("generated", "1"), [new("node", null, reference, [primitive.Key])], [primitive], new Dictionary<string,string>()));
        var first = new RenderOccurrence("first", identity, identity, "First", mesh.Key, Matrix4x4.Identity, new(Vector3.Zero, Vector3.One), []);
        var second = first with { Key = "second", Label = "Second", World = Matrix4x4.CreateScale(-1,2,1) * Matrix4x4.CreateTranslation(4,0,0) };
        if (preserveInstances) second = second with { World = Matrix4x4.CreateScale(1,2,1) * Matrix4x4.CreateTranslation(4,0,0) };
        using var snapshot = new RenderAssetSnapshot(revision, new Dictionary<string,RenderMesh> { [mesh.Key] = mesh }, [first,second],
            new Dictionary<ResourceIdentity,RenderMaterial>(), new(Vector3.Zero,new(4,2,1)), [], [], new(2,2,0,2,1,1), [], budget);
        var preparations = 0;
        try
        {
            var report = await new SceneFbxExportCoordinator().ExportAsync(snapshot, new([new("first"),new("second")], merge) { PreserveInstances = preserveInstances },
                Path.Combine(root,"source"), Path.Combine(root,"model.fbx"), (_, _) =>
                {
                    preparations++;
                    return Task.FromResult(new NativeModelFbxMaterialInputs(budget,
                        [new(material, [new(reference, new(ReferenceStatus.Unique,[material.Identity]))], [], _ => Task.CompletedTask)]));
                }, _ => Task.CompletedTask, options: new(MaximumTriangles: preserveInstances ? 1 : 5_000_000));
            Assert.Equal(1, preparations); Assert.Equal(0, budget.UsedBytes);
            Assert.Equal(merge || preserveInstances ? 1 : 2, report.MeshCount);
            var text = await File.ReadAllTextAsync(Path.Combine(root,"model.fbx"));
            Assert.Contains("LayerElementMaterial", text);
            Assert.Equal(preserveInstances ? 1 : 2, System.Text.RegularExpressions.Regex.Matches(text, "Material: [0-9]+, ").Count);
            Assert.DoesNotContain(report.Losses, loss => loss.Detail.Contains("No unique decoded material join"));
            Assert.Empty(Directory.GetFiles(root,"*.tmp"));
        }
        finally { Directory.Delete(root, recursive:true); }
    }
}
