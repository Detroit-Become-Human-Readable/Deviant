using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class PreparedSceneCodecTests
{
    [Fact]
    public async Task BackedPixelsRoundTripWithoutRetainingEveryTextureArray()
    {
        using var fixture = new GeneratedArchive();
        using var cache = new CacheManager(Path.Combine(fixture.Root, "settings"), new(Path.Combine(fixture.Root, "cache"), 64 * 1024 * 1024));
        var archive = await new ArchiveReader().OpenAsync(fixture.Index);
        var revision = new SourceRevision(archive.IndexContentId, archive.PhysicalSourceStamp);
        var budget = new AssetMemoryBudget();
        using var source = ArchiveSourceSession.Acquire(archive, (bytes, reason) => budget.Reserve(bytes, reason));
        var owners = new List<IDisposable>();
        var textures = new Dictionary<int, RenderTexture>();
        var expected = new List<byte[]>();
        for (int index = 0; index < 4; index++)
        {
            byte[] pixels = Enumerable.Range(0, 1024 * 1024 * 4).Select(value => (byte)(value + index * 7)).ToArray();
            expected.Add(pixels);
            var identity = new ResourceIdentity(archive.IndexContentId, 0, "texture/" + index);
            var hash = Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant();
            var resident = TextureSurface.RestoreRgba8(1024, 1024, pixels, false, true, 2,
                TextureStorageFormat.Rgba8, "RGBA8", hash, new(MipIndex: 2), identity, revision);
            var prepared = await PreparedTexturePixelSource.StoreAsync(cache, "texture/" + index, resident, hash, budget);
            owners.Add(prepared);
            textures.Add(index, new(identity, prepared.Surface, hash));
        }
        var aliasId = textures[0].NativeIdentity with { MemberPath = "alias" };
        textures.Add(4, textures[0] with { NativeIdentity = aliasId });
        var materialId = new ResourceIdentity(archive.IndexContentId, 0, "material");
        var material = new RenderMaterial(new(materialId, revision, "generated", [], [], []), textures);
        var extents = new SourceExtent[] { new(archive.Volumes[0].RelativeName, 0, 4,
            Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 }))) };
        var path = Path.Combine(fixture.Root, "backed.scene");
        var reads = new PixelReads(fixture.Root);
        using (var subscription = AssetLoadDiagnostics.Listener.Subscribe(reads))
        using (var original = new RenderAssetSnapshot(revision, new Dictionary<string, RenderMesh>(), [],
            new Dictionary<ResourceIdentity, RenderMaterial> { [materialId] = material }, new(Vector3.Zero, Vector3.One),
            [], [], new(0, 0, 0, 0, 0, 0), extents, budget, owners))
            await PreparedSceneCodec.WriteAsync(path, "backed", original);
        Assert.Equal(4, reads.Count);
        Assert.Equal(0, cache.ActivePreviewLeases);
        Assert.True(await PreparedSceneCodec.ValidateAsync(path, "backed", revision));
        long baseline = budget.UsedBytes;
        using (var restored = await PreparedSceneCodec.ReadAsync(path, "backed", source.Session, archive, budget, false))
        {
            var slots = Assert.Single(restored.Materials.Values).TexturesBySlot;
            Assert.All(slots.Values, texture => Assert.False(texture.Surface.HasRgba8));
            Assert.All(slots.Values, texture => Assert.NotNull(texture.Surface.PixelSource));
            Assert.Same(slots[0].Surface.PixelSource, slots[4].Surface.PixelSource);
            Assert.True(budget.UsedBytes - baseline < 20L * 1024 * 1024);
            long retained = budget.UsedBytes;
            for (int index = 0; index < 4; index++)
            {
                using (var pixels = await slots[index].Surface.AcquireRgba8Async())
                {
                    Assert.Equal(expected[index], pixels.Surface.Rgba8.ToArray());
                    Assert.True(pixels.Surface.IsSrgb); Assert.Equal(2, pixels.Surface.MipIndex);
                }
                Assert.Equal(retained, budget.UsedBytes);
            }
        }
        Assert.Equal(baseline, budget.UsedBytes);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private sealed class PixelReads(string root) : IObserver<KeyValuePair<string, object?>>
    {
        public int Count;
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is AssetLoadCounterMeasurement { Stage: "PreparedTexturePixelsReadStarted", Key: { } key } &&
                key.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref Count);
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoundTripPreservesGeometryPlacementPixelsAndSharedMaterialExpressions(bool legacy)
    {
        using var fixture = new GeneratedArchive();
        var archive = await new ArchiveReader().OpenAsync(fixture.Index);
        var revision = new SourceRevision(archive.IndexContentId, archive.PhysicalSourceStamp);
        var budget = new AssetMemoryBudget();
        using var source = ArchiveSourceSession.Acquire(archive, (bytes, reason) => budget.Reserve(bytes, reason));
        long baseline = budget.UsedBytes;
        var meshId = new ResourceIdentity(archive.IndexContentId, 0, "member/mesh");
        var materialId = meshId with { MemberPath = "member/material" };
        var textureId = meshId with { MemberPath = "member/texture" };
        var primitive = new MeshPrimitive("surface", PrimitiveTopology.Triangles,
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2], IndexElementFormat.UInt16, [],
            Normals: [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], TextureCoordinates: [[Vector2.Zero, Vector2.UnitX, Vector2.UnitY]],
            Colors: [Vector4.One, Vector4.One, Vector4.One]);
        var mesh = new RenderMesh(meshId, 0, new(new("generated", "1"), [], [primitive], new Dictionary<string, string>()),
            new Dictionary<string, ResourceIdentity> { [primitive.Key] = materialId });
        var occurrence = new RenderOccurrence("placement", meshId, meshId, "Model", mesh.Key,
            Matrix4x4.CreateTranslation(3, 2, 1), new(Vector3.Zero, Vector3.One), [meshId]) { NativeFlags = 7 };
        byte[] pixels = [10, 20, 30, 40]; var digest = Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant();
        var texture = new RenderTexture(textureId, TextureSurface.RestoreRgba8(1, 1, pixels, false, true, 2,
            TextureStorageFormat.Rgba8, "RGBA8", digest, new(MipIndex: 2), textureId, revision), digest);
        var shared = new MaterialExpression(MaterialExpressionOperation.Constant, MaterialValueType.Vector4, [], [1, 2, 3, 4]);
        var graph = new MaterialSurfaceGraph(new(MaterialExpressionOperation.Add, MaterialValueType.Vector4, [shared, shared]),
            null, null, [], [], []) { IsUnlit = true };
        var material = new RenderMaterial(new(materialId, revision, "generated", [], [], []) { SurfaceGraph = graph,
            NativeRenderParameters = new(2, 3, 4, 0, 1, new(materialId, 0, 4)) },
            new Dictionary<int, RenderTexture> { [0] = texture, [1] = texture });
        var extents = new SourceExtent[] { new(archive.Volumes[0].RelativeName, 0, 4,
            Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 }))) };
        using var original = new RenderAssetSnapshot(revision, new Dictionary<string, RenderMesh> { [mesh.Key] = mesh },
            [occurrence], new Dictionary<ResourceIdentity, RenderMaterial> { [materialId] = material },
            occurrence.WorldBounds, [], [], new(1, 1, 0, 1, 1, 1), extents, budget);
        string path = Path.Combine(fixture.Root, "prepared.scene");
        await PreparedSceneCodec.WriteAsync(path, "key", original);
        if (legacy) await RewritePreparedManifestAsync(path, manifest => manifest.Remove("RequiredMaterialKeys"));
        Assert.True(await PreparedSceneCodec.ValidateAsync(path, "key", revision));
        var validationBudget = new AssetMemoryBudget(2 * 1024 * 1024);
        Assert.True(await PreparedSceneCodec.ValidateAsync(path, "key", revision, budget: validationBudget));
        Assert.Equal(0, validationBudget.UsedBytes);
        Assert.False(await PreparedSceneCodec.ValidateAsync(path, "other", revision));
        using (var restored = await PreparedSceneCodec.ReadAsync(path, "key", source.Session, archive, budget, false))
        {
            var geometry = Assert.Single(restored.Meshes.Values).Geometry.Primitives[0];
            Assert.Equal(primitive.Positions, geometry.Positions); Assert.Equal(primitive.Indices, geometry.Indices);
            Assert.Equal(primitive.Normals, geometry.Normals); Assert.Equal(primitive.TextureCoordinates![0], geometry.TextureCoordinates![0]);
            Assert.Equal(primitive.Colors, geometry.Colors); Assert.Equal(occurrence.World, Assert.Single(restored.Occurrences).World);
            var restoredMaterial = Assert.Single(restored.Materials.Values);
            Assert.Equal(material.Asset.NativeRenderParameters, restoredMaterial.Asset.NativeRenderParameters);
            Assert.Same(restoredMaterial.Asset.SurfaceGraph!.BaseColor!.Arguments[0], restoredMaterial.Asset.SurfaceGraph.BaseColor.Arguments[1]);
            Assert.Same(restoredMaterial.TexturesBySlot[0], restoredMaterial.TexturesBySlot[1]);
            Assert.Equal(pixels, restoredMaterial.TexturesBySlot[0].Surface.Rgba8.ToArray());
            Assert.Equal(2, restoredMaterial.TexturesBySlot[0].Surface.MipIndex);
            Assert.Equal(extents, restored.SourceExtents);
        }
        Assert.True(budget.UsedBytes >= baseline);
        var bytes = await File.ReadAllBytesAsync(path); bytes[^40] ^= 1; await File.WriteAllBytesAsync(path, bytes);
        Assert.False(await PreparedSceneCodec.ValidateAsync(path, "key", revision));
        source.Dispose(); Assert.Equal(0, budget.UsedBytes);
    }
    [Theory]
    [InlineData("budget")]
    [InlineData("material")]
    [InlineData("binding")]
    [InlineData("graph")]
    public async Task LegacyIncompleteMaterialIsRejectedDespiteAValidPreparedSceneChecksum(string defect)
    {
        using var fixture=new GeneratedArchive();
        var archive=await new ArchiveReader().OpenAsync(fixture.Index);
        var revision=new SourceRevision(archive.IndexContentId,archive.PhysicalSourceStamp);
        var budget=new AssetMemoryBudget();
        using var source=ArchiveSourceSession.Acquire(archive,(bytes,reason)=>budget.Reserve(bytes,reason));
        var meshId=new ResourceIdentity(archive.IndexContentId,0,"member/mesh");
        var materialId=meshId with { MemberPath="member/material" };
        var primitive=new MeshPrimitive("triangle",PrimitiveTopology.Triangles,
            [Vector3.Zero,Vector3.UnitX,Vector3.UnitY],[0,1,2],IndexElementFormat.UInt16,[],
            TextureCoordinates:[[Vector2.Zero,Vector2.UnitX,Vector2.UnitY]]);
        var mesh=new RenderMesh(meshId,0,new(new("generated","1"),[],[primitive],new Dictionary<string,string>()),
            new Dictionary<string,ResourceIdentity> { [primitive.Key]=materialId });
        var material=new RenderMaterial(new(materialId,revision,"neutral",[],[],[]),
            new Dictionary<int,RenderTexture>());
        var extents=new SourceExtent[] { new(archive.Volumes[0].RelativeName,0,4,
            Convert.ToHexString(SHA256.HashData(new byte[]{1,2,3,4}))) };
        using var original=new RenderAssetSnapshot(revision,new Dictionary<string,RenderMesh> { [mesh.Key]=mesh },[],
            new Dictionary<ResourceIdentity,RenderMaterial> { [materialId]=material },new(Vector3.Zero,Vector3.One),
            [],[],new(0,0,0,1,1,0),extents,budget);
        var path=Path.Combine(fixture.Root,"legacy-material.scene");
        await PreparedSceneCodec.WriteAsync(path,"legacy-material",original);
        Assert.True(await PreparedSceneCodec.ValidateAsync(path,"legacy-material",revision));
        using(var healthy=await PreparedSceneCodec.ReadAsync(path,"legacy-material",source.Session,archive,budget,false))
        {
            Assert.Equal(primitive.Indices,Assert.Single(healthy.Meshes).Value.Geometry.Primitives[0].Indices);
            Assert.Empty(Assert.Single(healthy.Materials).Value.Asset.Diagnostics);
            Assert.Empty(Assert.Single(healthy.Materials).Value.TexturesBySlot);
        }
        var reference=new ResourceReference(IdentifierNamespace.Native,"1",materialId,2137);
        var options=new JsonSerializerOptions { IncludeFields=true };
        await RewritePreparedManifestAsync(path,manifest=>
        {
            var row=manifest["Materials"]!.AsArray().Single()!.AsObject();
            var asset=row["Asset"]!.AsObject();
            switch(defect)
            {
                case "material":
                    manifest.Remove("RequiredMaterialKeys");
                    manifest["Materials"] = new JsonArray();
                    break;
                case "budget":
                    asset["Profile"]="neutral";
                    asset["Diagnostics"]=new JsonArray(JsonValue.Create("AssetMemoryLimit"));
                    break;
                case "binding":
                    asset["Profile"]="direct-normal";
                    asset["References"]=JsonSerializer.SerializeToNode(new[]{reference},options);
                    var binding=new MaterialTextureBinding(0,reference,MaterialTextureRole.BaseColor,0,
                        Vector2.One,Vector2.Zero,new(true,"generated","typed texture slot"));
                    asset["Bindings"]=JsonSerializer.SerializeToNode(new[]{binding},options);
                    break;
                case "graph":
                    asset["Profile"]="surface-expression";
                    asset["References"]=JsonSerializer.SerializeToNode(new[]{reference},options);
                    row["Graph"]=new JsonObject
                    {
                        ["Nodes"]=new JsonArray(
                            Expression(MaterialExpressionOperation.Input,MaterialValueType.Vector2,[],MaterialInput.Uv0,null),
                            Expression(MaterialExpressionOperation.TextureSample,MaterialValueType.Vector4,[0],null,0)),
                        ["Color"]=1,["Opacity"]=-1,["Cutoff"]=-1,
                        ["Inputs"]=JsonSerializer.SerializeToNode(new[]{MaterialInput.Uv0}),
                        ["Slots"]=new JsonArray(JsonValue.Create(0)),["Diagnostics"]=new JsonArray(),["IsUnlit"]=false
                    };
                    break;
                default:throw new ArgumentOutOfRangeException(nameof(defect));
            }
        });
        var accepted=await PreparedSceneCodec.ValidateAsync(path,"legacy-material",revision);
        var readError=await Record.ExceptionAsync(async()=>
        {
            using var restored=await PreparedSceneCodec.ReadAsync(path,"legacy-material",source.Session,archive,budget,false);
        });
        Assert.False(accepted);
        Assert.IsType<InvalidDataException>(readError);
    }

    private static JsonObject Expression(MaterialExpressionOperation operation,MaterialValueType type,
        int[] arguments,MaterialInput? input,int? slot)=>new()
    {
        ["Operation"]=(int)operation,["Type"]=(int)type,["Arguments"]=JsonSerializer.SerializeToNode(arguments),
        ["Constants"]=null,["Input"]=input is null?null:JsonValue.Create((int)input.Value),
        ["Slot"]=slot is null?null:JsonValue.Create(slot.Value),["Components"]=null,["Extent"]=null
    };

    private static async Task RewritePreparedManifestAsync(string path,Action<JsonObject> mutate)
    {
        var original=await File.ReadAllBytesAsync(path);
        var oldMetadataBytes=BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(8,4));
        var manifest=JsonNode.Parse(original.AsSpan(12,oldMetadataBytes))!.AsObject();
        mutate(manifest);
        var metadata=Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var tail=original.AsSpan(12+oldMetadataBytes,original.Length-32-12-oldMetadataBytes).ToArray();
        var rewritten=new byte[12+metadata.Length+tail.Length+32];
        original.AsSpan(0,8).CopyTo(rewritten);
        BinaryPrimitives.WriteInt32LittleEndian(rewritten.AsSpan(8,4),metadata.Length);
        metadata.CopyTo(rewritten,12);tail.CopyTo(rewritten,12+metadata.Length);
        SHA256.HashData(rewritten.AsSpan(0,rewritten.Length-32)).CopyTo(rewritten,rewritten.Length-32);
        await File.WriteAllBytesAsync(path,rewritten);
        Assert.Equal(tail,rewritten.AsSpan(12+metadata.Length,tail.Length).ToArray());
        Assert.Equal(SHA256.HashData(rewritten.AsSpan(0,rewritten.Length-32)),rewritten.AsSpan(rewritten.Length-32).ToArray());
    }
    private sealed class GeneratedArchive : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DeviantTests", Guid.NewGuid().ToString("N"));
        public string Index => Path.Combine(Root, "sample.idx");
        public GeneratedArchive()
        {
            Directory.CreateDirectory(Root);
            var bytes = new byte[Deviant.Core.IndexLayout.HeaderLength + Deviant.Core.IndexLayout.RecordLength];
            Deviant.Core.IndexLayout.Magic.CopyTo(bytes);
            void Put(int offset,uint value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset),value);
            Put(20,18); Put(101,1); Put(105,29); Put(113,9); Put(121,4);
            File.WriteAllBytes(Index,bytes);File.WriteAllBytes(Path.Combine(Root,Deviant.Core.IndexLayout.VolumeName("sample",0)),[1,2,3,4]);
        }
        public void Dispose() => Directory.Delete(Root,true);
    }

}
