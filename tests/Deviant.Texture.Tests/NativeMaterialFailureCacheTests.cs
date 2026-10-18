using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Text;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Models;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class NativeMaterialFailureCacheTests
{
    [Fact]
    public async Task TemporaryMaterialBudgetFailureCannotBecomeAReusablePreparedScene()
    {
        using var fixture=new MaterialArchive();
        var snapshot=await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget=new AssetMemoryBudget();
        await using var catalogs=new TextureCatalogSession(snapshot,new(snapshot.IndexContentId,snapshot.PhysicalSourceStamp));
        await using var textures=new NativeTextureSession(catalogs);
        using var container=await NativeContainerLease.OpenMetadataAsync(snapshot,new(snapshot.IndexContentId,1),budget);
        var models=new NativeModelSession(textures,budget);
        var meshIdentity=container.Directory.Objects.Single(frame=>frame.NativeType==2130).Identity;
        var materialIdentity=container.Directory.Objects.Single(frame=>frame.NativeType==2133).Identity;
        var primitive=new MeshPrimitive("triangle",PrimitiveTopology.Triangles,
            [Vector3.Zero,Vector3.UnitX,Vector3.UnitY],[0,1,2],IndexElementFormat.UInt16,[]);
        var geometry=new GeometryAsset(new("generated","1"),[],[primitive],new Dictionary<string,string>());
        var mesh=new RenderMesh(meshIdentity,0,geometry,new Dictionary<string,ResourceIdentity>
            { [primitive.Key]=materialIdentity });
        var meshes=new Dictionary<string,RenderMesh> { [mesh.Key]=mesh };
        using var geometryMemory=budget.Reserve(4096,"generated retained geometry");
        var baseline=budget.UsedBytes;
        PreparedMaterials degraded;
        using(budget.Reserve(budget.MaximumBytes-budget.UsedBytes-512*1024,"generated temporary pressure"))
        {
            degraded=await PrepareAsync(models,container,meshes);
            Assert.Same(geometry,mesh.Geometry);
            Assert.Equal(new uint[]{0,1,2},mesh.Geometry.Primitives[0].Indices);
        }
        using(degraded)
        using(var failedSnapshot=new RenderAssetSnapshot(models.Revision,meshes,[],degraded.Materials,
            new(Vector3.Zero,Vector3.One),degraded.Issues,[],new(0,0,0,1,1,0),
            container.SourceExtents.Concat(degraded.Extents).Distinct().ToArray(),budget))
        {
            Assert.Contains("AssetMemoryLimit",Assert.Single(failedSnapshot.Materials).Value.Asset.Diagnostics);
            Assert.Contains(failedSnapshot.Issues,issue=>issue.Code=="AssetMemoryLimit"&&issue.Identity==materialIdentity);
            Assert.True(failedSnapshot.HasIncompleteMaterials);
            await Assert.ThrowsAsync<InvalidDataException>(()=>PreparedSceneCodec.WriteAsync(fixture.PreparedPath,"generated-material",failedSnapshot));
            Assert.False(File.Exists(fixture.PreparedPath));
        }
        Assert.Equal(baseline,budget.UsedBytes);
        using(var retry=await PrepareAsync(models,container,meshes))
        using(var recoveredSnapshot=new RenderAssetSnapshot(models.Revision,meshes,[],retry.Materials,
            new(Vector3.Zero,Vector3.One),retry.Issues,[],new(0,0,0,1,1,0),
            container.SourceExtents.Concat(retry.Extents).Distinct().ToArray(),budget))
        {
            var recovered=Assert.Single(retry.Materials).Value.Asset;
            Assert.NotNull(recovered.NativeRenderParameters);
            Assert.DoesNotContain("AssetMemoryLimit",recovered.Diagnostics);
            Assert.Same(geometry,mesh.Geometry);
            Assert.False(recoveredSnapshot.HasIncompleteMaterials, recovered.Profile + ": " + string.Join(", ", recovered.Diagnostics));
            await PreparedSceneCodec.WriteAsync(fixture.PreparedPath,"generated-material",recoveredSnapshot);
            Assert.True(await PreparedSceneCodec.ValidateAsync(fixture.PreparedPath,"generated-material",models.Revision));
        }
        Assert.Equal(baseline,budget.UsedBytes);
        container.EnsureUnchanged();
    }

    [Fact]
    public async Task ChangedMaterialDependencyIsNotDowngradedToAFallback()
    {
        using var fixture=new MaterialArchive();
        var snapshot=await new ArchiveReader().OpenAsync(fixture.IndexPath);
        var budget=new AssetMemoryBudget();
        var revisions=new ChangedRevisionProvider(Path.ChangeExtension(fixture.IndexPath,".dat"));
        using var source=ArchiveSourceSession.Acquire(snapshot,(bytes,reason)=>budget.Reserve(bytes,reason),revisions);
        await using var catalogs=new TextureCatalogSession(snapshot,new(snapshot.IndexContentId,snapshot.PhysicalSourceStamp));
        await using var textures=new NativeTextureSession(catalogs);
        using var container=await NativeContainerLease.OpenMetadataAsync(snapshot,new(snapshot.IndexContentId,1),budget);
        var models=new NativeModelSession(textures,budget);
        var meshIdentity=container.Directory.Objects.Single(frame=>frame.NativeType==2130).Identity;
        var materialIdentity=container.Directory.Objects.Single(frame=>frame.NativeType==2133).Identity;
        var mesh=new RenderMesh(meshIdentity,0,new(new("generated","1"),[],[],new Dictionary<string,string>()),
            new Dictionary<string,ResourceIdentity> { ["primitive"]=materialIdentity });
        revisions.Armed=true;
        var error=await Record.ExceptionAsync(async()=>
        { using var result=await PrepareAsync(models,container,new Dictionary<string,RenderMesh> { [mesh.Key]=mesh }); });
        Assert.Equal("SourceChanged",Assert.IsType<ArchiveException>(error).Code);
    }

    private static async Task<PreparedMaterials> PrepareAsync(NativeModelSession models,NativeContainerLease container,
        IReadOnlyDictionary<string,RenderMesh> meshes)
    {
        var result=new PreparedMaterials();
        try
        {
            var method=typeof(NativeModelSession).GetMethod("PrepareMaterialsAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
            await (Task)method.Invoke(models,[container,new ResourceIdentity(models.Revision.IndexContentId,0),meshes,
                result.Materials,new Dictionary<ResourceIdentity,RenderTexture>(),result.Issues,result.Extents,result.Resources,
                container.Directory.Objects.ToDictionary(frame=>frame.Identity),null,null,1024,CancellationToken.None])!;
            return result;
        }
        catch { result.Dispose();throw; }
    }

    private sealed class ChangedRevisionProvider(string changedPath):ISourceRevisionProvider
    {
        private readonly FileRevisionProvider inner=new();
        public bool Armed { get; set; }
        public FileStamp Capture(string path)=>Changed(inner.Capture(path),path);
        public FileStamp Capture(Microsoft.Win32.SafeHandles.SafeFileHandle handle,string path)=>Changed(inner.Capture(handle,path),path);
        private FileStamp Changed(FileStamp stamp,string path)=>Armed&&string.Equals(path,changedPath,StringComparison.OrdinalIgnoreCase)
            ?stamp with { LastWriteUtcTicks=stamp.LastWriteUtcTicks+1 }:stamp;
    }

    private sealed class PreparedMaterials:IDisposable
    {
        public Dictionary<ResourceIdentity,RenderMaterial> Materials { get; }=[];
        public List<RenderIssue> Issues { get; }=[];
        public List<SourceExtent> Extents { get; }=[];
        public List<IDisposable> Resources { get; }=[];
        public void Dispose()
        { for(var index=Resources.Count-1;index>=0;index--)Resources[index].Dispose();Resources.Clear(); }
    }

    private sealed class MaterialArchive:IDisposable
    {
        private readonly string root=Path.Combine(Path.GetTempPath(),"DeviantMaterialFailureTests",Guid.NewGuid().ToString("N"));
        public string IndexPath=>Path.Combine(root,"sample.idx");
        public string PreparedPath=>Path.Combine(root,"prepared.scene");

        public MaterialArchive()
        {
            Directory.CreateDirectory(root);
            var catalog=new byte[20];"COM_CONT"u8.CopyTo(catalog);Put(catalog,8,6);Put(catalog,12,4);
            var shader=ShaderMetadata();
            var decoded=Encoding.UTF8.GetBytes("""
                #version 450
                S_MATERIAL_ATTRIBUTES ComputeHypershade(S_SURFACE Surface, S_MATERIAL Material) {
                    S_MATERIAL_ATTRIBUTES output;
                    output.baseColor = vec3(0.2, 0.3, 0.4);
                    output.transparency = 1.0;
                    output.alphaTestValue = 0.05;
                    return output;
                }
                void main() {
                    S_MATERIAL_ATTRIBUTES visible = ComputeHypershade(Surface, Material);
                    if (visible.transparency < visible.alphaTestValue) discard;
                }
                """ + "\0");
            var package=GpuPackage(decoded);
            using var body=new MemoryStream();
            using(var writer=new BinaryWriter(body,Encoding.UTF8,true))
            {
                writer.Write(1u);writer.Write((byte)0);writer.Write(0u);writer.Write(0u);writer.Write((byte)0);
                var header=new byte[52];Put(header,0,(uint)shader.Length);header[4]=1;Put(header,5,1);
                Put(header,36,(uint)package.Length);Put(header,40,(uint)decoded.Length);
                writer.Write(header);writer.Write(shader);writer.Write(package);
            }
            using var output=new MemoryStream();
            using(var writer=new BinaryWriter(output,Encoding.UTF8,true))
            {
                writer.Write("QZIP\0"u8);writer.Write("DC_INFO "u8);writer.Write(3u);writer.Write(24u);
                writer.Write(3u);writer.Write(2u);writer.Write(2130u);writer.Write(10u);writer.Write(2133u);writer.Write(20u);
                writer.Write("DC_DATA "u8);writer.Write(23u);writer.Write((uint)body.Length);writer.Write(body.ToArray());
            }
            var container=output.ToArray();
            File.WriteAllBytes(Path.ChangeExtension(IndexPath,".dat"),catalog.Concat(container).ToArray());
            var index=new byte[IndexLayout.HeaderLength+2*IndexLayout.RecordLength];IndexLayout.Magic.CopyTo(index);
            PutIndex(index,20,IndexLayout.SupportedVersion);PutIndex(index,101,2);
            Row(0,3,1,0,(uint)catalog.Length);Row(1,29,2,(uint)catalog.Length,(uint)container.Length);
            File.WriteAllBytes(IndexPath,index);
            void Row(int ordinal,uint type,uint id,uint offset,uint length)
            {
                var at=IndexLayout.HeaderLength+ordinal*IndexLayout.RecordLength;
                PutIndex(index,at,type);PutIndex(index,at+8,id);PutIndex(index,at+12,offset);PutIndex(index,at+16,length);
            }
        }

        private static byte[] ShaderMetadata()
        {
            using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
            writer.Write(3u);writer.Write("COM_CONT"u8);writer.Write(6u);writer.Write(0u);
            writer.Write("LOADCONT"u8);writer.Write(2u);writer.Write("SHADCUST"u8);writer.Write(91u);
            writer.Write(new byte[35]);writer.Write(0u);
            writer.Write(0u);writer.Write(0u);writer.Write(0u);writer.Write(0u);
            return stream.ToArray();
        }

        private static byte[] GpuPackage(byte[] decoded)
        {
            using var packed=new MemoryStream();
            using(var zlib=new ZLibStream(packed,CompressionLevel.Fastest,true))zlib.Write(decoded);
            var compressed=packed.ToArray();var result=new byte[32+compressed.Length];"segs"u8.CopyTo(result);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4),1);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6),1);
            Put(result,8,(uint)decoded.Length);Put(result,12,(uint)compressed.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(16),checked((ushort)compressed.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(18),checked((ushort)decoded.Length));
            Put(result,20,1);compressed.CopyTo(result,32);return result;
        }

        private static void Put(byte[] bytes,int offset,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset),value);
        private static void PutIndex(byte[] bytes,int offset,uint value)=>BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset),value);
        public void Dispose() { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
}