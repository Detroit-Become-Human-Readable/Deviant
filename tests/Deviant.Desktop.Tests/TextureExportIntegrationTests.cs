using System.Buffers.Binary;
using System.IO;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Export.Textures;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class TextureExportIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PngRejectsSamePixelsWithDifferentIdentityOrRevision(bool changeIdentity)
    {
        await WithDestination(async(source,output)=>
        {
            var bytes=Dds(1,1,28,[1,2,3,255]);var reader=new DdsTextureReader();
            var first=reader.Read(bytes,new("index",1),new("index","revision-a"));
            var second=reader.Read(bytes,new("index",changeIdentity?2:1),new("index",changeIdentity?"revision-a":"revision-b"));
            var surface=new DdsTextureDecoder().DecodeMip(first);
            var validationCalled=false;
            var error=await Assert.ThrowsAsync<ArchiveException>(()=>new TextureExportService().ExportAsync(second,surface,AssetExportFormat.Png,source,output,"mismatch.png",expectedRevision:second.Revision,preCommitValidation:_=>{validationCalled=true;return Task.CompletedTask;}));
            Assert.Equal("TextureSurfaceMismatch",error.Code);Assert.False(File.Exists(Path.Combine(output,"mismatch.png")));
            Assert.False(validationCalled);
        });
    }

    [Fact]
    public async Task PngRejectsAResizedThumbnailAsTheOriginalMip()
    {
        await WithDestination(async(source,output)=>
        {
            var document=new DdsTextureReader().Read(Dds(4,4,28,new byte[64]));var decoder=new DdsTextureDecoder();
            var thumbnail=decoder.CreateThumbnail(decoder.DecodeMip(document),2);
            var error=await Assert.ThrowsAsync<ArchiveException>(()=>new TextureExportService().ExportAsync(document,thumbnail,AssetExportFormat.Png,source,output,"thumbnail.png"));
            Assert.Equal("TextureSurfaceMismatch",error.Code);
        });
    }

    [Fact]
    public async Task HdrPngReportsItsTransformWhileDdsDoesNot()
    {
        await WithDestination(async(source,output)=>
        {
            var document=new DdsTextureReader().Read(Dds(4,4,95,new byte[16]));
            var surface=HdrDisplayTransform.ToSdr(new DdsTextureDecoder().DecodeMip(document),-2,TextureToneMapOperator.Reinhard);
            var service=new TextureExportService();
            var png=await service.ExportAsync(document,surface,AssetExportFormat.Png,source,output,"preview.png");
            Assert.True(png.SourceIsHdr);Assert.False(png.OutputIsHdr);Assert.Equal("RGBA8",png.Precision);
            Assert.Equal(-2,png.DisplayTransform!.ExposureStops);Assert.Contains("-2 EV",png.Losses,StringComparison.Ordinal);
            var dds=await service.ExportAsync(document,surface,AssetExportFormat.Dds,source,output,"original.dds");
            Assert.True(dds.OutputIsHdr);Assert.Null(dds.DisplayTransform);Assert.Equal(document.OriginalBytes.ToArray(),await File.ReadAllBytesAsync(Path.Combine(output,"original.dds")));
        });
    }

    [Fact]
    public async Task FailedPrecommitValidationPreservesDestinationAndRemovesTemporaryFile()
    {
        await WithDestination(async(source,output)=>
        {
            Directory.CreateDirectory(output);var destination=Path.Combine(output,"existing.png");
            await File.WriteAllTextAsync(destination,"keep");var document=new DdsTextureReader().Read(Dds(1,1,28,[1,2,3,255]));
            var surface=new DdsTextureDecoder().DecodeMip(document);var validationCalled=false;
            var error=await Assert.ThrowsAsync<ArchiveException>(()=>new TextureExportService().ExportAsync(document,surface,AssetExportFormat.Png,source,output,"existing.png",overwrite:true,
                preCommitValidation:_=>{validationCalled=true;throw new ArchiveException("SourceChanged","Synthetic source replacement.");}));
            Assert.True(validationCalled);Assert.Equal("SourceChanged",error.Code);Assert.Equal("keep",await File.ReadAllTextAsync(destination));
            Assert.Empty(Directory.EnumerateFiles(output,"*.tmp",SearchOption.AllDirectories));
        });
    }

    private static async Task WithDestination(Func<string,string,Task> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"deviant-texture-integration-"+Guid.NewGuid().ToString("N"));
        var source=Path.Combine(root,"source");Directory.CreateDirectory(source);
        try{await check(source,Path.Combine(root,"output"));}finally{Directory.Delete(root,true);}
    }
    private static byte[] Dds(int width,int height,uint dxgi,byte[] payload)
    {
        var bytes=new byte[148+payload.Length];"DDS "u8.CopyTo(bytes);
        Put(bytes,4,124);Put(bytes,8,0x1007);Put(bytes,12,(uint)height);Put(bytes,16,(uint)width);
        Put(bytes,76,32);Put(bytes,80,4);"DX10"u8.CopyTo(bytes.AsSpan(84));Put(bytes,108,0x1000);
        Put(bytes,128,dxgi);Put(bytes,132,3);Put(bytes,140,1);payload.CopyTo(bytes,148);return bytes;
    }
    private static void Put(byte[] bytes,int offset,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset,4),value);
}
