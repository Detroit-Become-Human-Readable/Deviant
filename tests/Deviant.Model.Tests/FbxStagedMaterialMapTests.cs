using System.Numerics;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Export.Models;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Model.Tests;

public sealed partial class FbxGeometryExporterTests
{
    [Fact]
    public async Task StagedMapsRetainAtlasAndHashProvenanceWithoutDecodedInputs()
    {
        var root=NewRoot();var source=Path.Combine(root,"source");Directory.CreateDirectory(source);
        try
        {
            var decoded=CreateDecoded(CreateAsset());var material=BakedMaterial(decoded,Reserve);
            var maps=new List<FbxStagedMaterialMapInput>();
            foreach(var map in material.BakedMaps)
            {
                var path=Path.Combine(root,Guid.NewGuid().ToString("N")+".png");
                using(var output=File.Create(path))PngTextureWriter.Write(output,map.Surface,map.IsSrgb);
                var hash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();
                maps.Add(new(map.Role,map.TextureCoordinateSet,map.Identity!,map.Revision!,map.DdsSha256,map.Width,map.Height,map.IsSrgb,
                    path,new FileInfo(path).Length,hash,map.GraphSha256,map.PrimitiveKeys,map.Sources,map.ValidateSources));
            }
            material=material with {BakedMaps=[],StagedMaps=maps};
            var report=await new FbxGeometryExporter().ExportAsync(new(decoded,new(decoded.Identity,"selected"),Reserve,[material]),
                source,Path.Combine(root,"staged.fbx"),new(MediaPackaging:FbxMediaPackaging.Embedded,MaximumTexturePixels:1));
            Assert.Equal(2,report.DerivedMaterialMaps.Count);
            Assert.Equal(maps.Select(map=>map.PngSha256).Order(),report.DerivedMaterialMaps.Select(map=>map.Sha256).Order());
            Assert.True(File.Exists(maps[0].Path));
        }
        finally{DeleteRoot(root);}
    }

    [Fact]
    public async Task TamperedStagedPngCannotReplaceAnExistingExport()
    {
        var root=NewRoot();var source=Path.Combine(root,"source");Directory.CreateDirectory(source);
        try
        {
            var decoded=CreateDecoded(CreateAsset());var material=BakedMaterial(decoded,Reserve);var map=material.BakedMaps[0];
            var path=Path.Combine(root,"map.png");using(var output=File.Create(path))PngTextureWriter.Write(output,map.Surface,map.IsSrgb);
            var bytes=await File.ReadAllBytesAsync(path);var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var staged=new FbxStagedMaterialMapInput(map.Role,map.TextureCoordinateSet,map.Identity!,map.Revision!,map.DdsSha256,
                map.Width,map.Height,map.IsSrgb,path,bytes.Length,hash,map.GraphSha256,map.PrimitiveKeys,map.Sources,map.ValidateSources);
            bytes[^5]^=1;await File.WriteAllBytesAsync(path,bytes);
            material=material with {BakedMaps=[],StagedMaps=[staged]};var destination=Path.Combine(root,"old.fbx");
            byte[] prior=[1,2,3,4];await File.WriteAllBytesAsync(destination,prior);
            await Assert.ThrowsAnyAsync<InvalidDataException>(()=>new FbxGeometryExporter().ExportAsync(
                new(decoded,new(decoded.Identity,"selected"),Reserve,[material]),source,destination,new(Overwrite:true)));
            Assert.Equal(prior,await File.ReadAllBytesAsync(destination));
            Assert.Empty(Directory.GetFiles(root,"*.tmp",SearchOption.AllDirectories));
        }
        finally{DeleteRoot(root);}
    }
}
