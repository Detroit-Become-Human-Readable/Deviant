using System.IO;
using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Deviant.Assets;
using Deviant.Desktop;
using Deviant.Export.Textures;
using Deviant.Formats.Textures;

namespace Deviant.AudioCheck;

public static partial class Program
{
    private static async Task<object> ExerciseImagesAsync(AudioWindow window,string input,string output)
    {
        await window.OpenDdsAsync(input);
        var view=window.ImageBrowser;
        await WaitAsync(()=>view.CurrentSurface is not null,"DDS pixels did not load: "+((TextBlock)view.FindName("Status")).Text);
        await view.WaitForRenderAsync();
        var original=view.CurrentSurface!;
        var pixel=original.Rgba8.Span[..4].ToArray();
        Require(Pixel(view).SequenceEqual(new[]{pixel[2],pixel[1],pixel[0],pixel[3]}),"DDS RGBA pixels were not rendered as BGRA.");
        var channels=(ComboBox)view.FindName("Channels");
        for(var channel=2;channel<=5;channel++)
        {
            channels.SelectedIndex=channel;await view.WaitForRenderAsync();
            var expected=pixel[channel-2];
            Require(Pixel(view).SequenceEqual(new byte[]{expected,expected,expected,255}),"DDS channel display does not match source pixels.");
        }
        channels.SelectedIndex=1;await view.WaitForRenderAsync();Require(Pixel(view)[3]==255,"RGB display retained source alpha.");
        channels.SelectedIndex=0;Require(original.Rgba8.Span[..4].SequenceEqual(pixel),"Channel display changed the source surface.");
        var mips=(ComboBox)view.FindName("Mips");
        if(mips.Items.Count<2)throw new InvalidOperationException("DDS QA input must contain at least two mips.");
        mips.SelectedIndex=1;mips.SelectedIndex=0;mips.SelectedIndex=1;
        await WaitAsync(()=>view.CurrentSurface?.MipIndex==1,"Rapid mip selection did not settle on the final mip.");
        Require(view.CurrentSurface!.Width==Math.Max(1,original.Width/2)&&view.CurrentSurface.Height==Math.Max(1,original.Height/2),"Mip dimensions do not match the selected level.");
        mips.SelectedIndex=0;await WaitAsync(()=>view.CurrentSurface?.MipIndex==0,"DDS did not return to the first mip.");
        ((Slider)view.FindName("Zoom")).Value=4;window.UpdateLayout();
        await CaptureWidthsAsync(window,output,"dds");
        var alternate=Path.Combine(output,"source-check","alternate.dds");Directory.CreateDirectory(Path.GetDirectoryName(alternate)!);
        var alternateBytes=await File.ReadAllBytesAsync(input);alternateBytes[^1]^=1;await File.WriteAllBytesAsync(alternate,alternateBytes);
        var first=window.OpenDdsAsync(input);var second=window.OpenDdsAsync(alternate);await Task.WhenAll(first,second);
        Require(view.CurrentDocument?.ContentSha256==Convert.ToHexString(SHA256.HashData(alternateBytes)).ToLowerInvariant(),"Rapid DDS opens did not retain the last source document.");
        await window.OpenDdsAsync(input);
        var export=typeof(DdsImageView).GetMethod("ExportToAsync",BindingFlags.Public|BindingFlags.Instance)
            ?? throw new InvalidOperationException("DDS export test facade is not available.");
        var png=await ExportImageAsync(export,view,AssetExportFormat.Png,Path.Combine(output,"exports","image.png"));
        var dds=await ExportImageAsync(export,view,AssetExportFormat.Dds,Path.Combine(output,"exports","image.dds"));
        Require(png.Sha256==Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(output,"exports","image.png")))).ToLowerInvariant()&&dds.Sha256==Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(output,"exports","image.dds")))).ToLowerInvariant(),"DDS export output hashes do not match their reports.");
        Require(png.SelectedMip==0&&png.Precision=="RGBA8","DDS PNG export report does not describe the selected pixels.");
        Require(dds.InputPayloadSha256==view.CurrentDocument!.ContentSha256&&dds.DisplayTransform is null,"Original DDS export report has an incorrect source hash or display transform.");
        Require((await File.ReadAllBytesAsync(Path.Combine(output,"exports","image.dds"))).AsSpan().SequenceEqual(view.CurrentDocument.OriginalBytes.Span),"Original DDS export changed encoded bytes.");
        var changed=Path.Combine(output,"source-check","image.dds");Directory.CreateDirectory(Path.GetDirectoryName(changed)!);
        await File.WriteAllBytesAsync(changed,await File.ReadAllBytesAsync(input));await window.OpenDdsAsync(changed);
        var expectedStamp=typeof(DdsImageView).GetField("sourceStamp",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view);
        var expectedPath=typeof(DdsImageView).GetField("sourcePath",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view);
        var modified=await File.ReadAllBytesAsync(changed);modified[^1]^=1;await File.WriteAllBytesAsync(changed,modified);
        await File.WriteAllTextAsync(Path.Combine(output,"source-change-debug.json"),JsonSerializer.Serialize(new{ExpectedPath=expectedPath,ExpectedStamp=expectedStamp,ActualStamp=new Deviant.Core.FileRevisionProvider().Capture(changed),ActualSha256=Convert.ToHexString(SHA256.HashData(modified)),DocumentSha256=view.CurrentDocument?.ContentSha256},new JsonSerializerOptions{WriteIndented=true}));
        var changedOutput=Path.Combine(output,"exports","changed.dds");if(File.Exists(changedOutput))File.Delete(changedOutput);
        var rejected=false;
        try{await ExportImageAsync(export,view,AssetExportFormat.Dds,Path.Combine(output,"exports","changed.dds"));}
        catch(Deviant.Core.ArchiveException error)when(error.Code=="SourceChanged"){rejected=true;}
        Require(rejected&&!File.Exists(changedOutput),"Changed DDS source was exported.");
        var hdrPath=Path.Combine(output,"source-check","hdr.dds");await File.WriteAllBytesAsync(hdrPath,HdrFixture());await window.OpenDdsAsync(hdrPath);
        var hdr=view.CurrentSurface??throw new InvalidOperationException("HDR DDS did not load.");
        Require(hdr.IsHdr&&hdr.SourceIsHdr&&hdr.HdrPixels.Length==16&&hdr.Rgba8.Length==0,"HDR decoder did not retain floating-point pixels.");
        Require(((FrameworkElement)view.FindName("HdrControls")).Visibility==Visibility.Visible,"HDR display controls are hidden.");
        var retained=hdr.HdrPixels.ToArray();((Slider)view.FindName("Exposure")).Value=1;
        ((ComboBox)view.FindName("ToneMap")).SelectedItem=TextureToneMapOperator.Aces;
        Require(view.CurrentSurface==hdr&&hdr.HdrPixels.Span.SequenceEqual(retained),"HDR display changed the retained original surface.");
        var hdrPng=await ExportImageAsync(export,view,AssetExportFormat.Png,Path.Combine(output,"exports","hdr.png"));
        Require(hdrPng.SourceIsHdr&&!hdrPng.OutputIsHdr&&hdrPng.DisplayTransform is{Operator:TextureToneMapOperator.Aces,ExposureStops:1},"HDR PNG export omitted its display transform.");
        var hdrDds=await ExportImageAsync(export,view,AssetExportFormat.Dds,Path.Combine(output,"exports","hdr.dds"));
        Require(hdrDds.OutputIsHdr&&hdrDds.DisplayTransform is null,"HDR DDS export did not retain source precision.");
        await CaptureWidthsAsync(window,output,"dds-hdr");
        var arrayPath=Path.Combine(output,"source-check","array.dds");await File.WriteAllBytesAsync(arrayPath,ArrayFixture(false));await window.OpenDdsAsync(arrayPath);
        var layers=(ComboBox)view.FindName("Layers");Require(layers.Items.Count==2&&layers.Visibility==Visibility.Visible,"DDS array selector is unavailable.");
        layers.SelectedIndex=1;layers.SelectedIndex=0;layers.SelectedIndex=1;
        await WaitAsync(()=>view.CurrentSurface?.Subresource.ArrayLayer==1,"Rapid array selection did not settle on layer one.");
        await view.WaitForRenderAsync();Require(Pixel(view)[2]==52,"Selected array layer pixels are incorrect.");
        var arrayPng=await ExportImageAsync(export,view,AssetExportFormat.Png,Path.Combine(output,"exports","array.png"));
        Require(arrayPng.SelectedSubresource is{ArrayLayer:1,Face:null,MipIndex:0},"Array PNG export omitted its selected layer.");
        var cubePath=Path.Combine(output,"source-check","cube.dds");await File.WriteAllBytesAsync(cubePath,ArrayFixture(true));await window.OpenDdsAsync(cubePath);
        var faces=(ComboBox)view.FindName("Faces");Require(faces.Items.Count==6&&faces.Visibility==Visibility.Visible,"DDS cube-face selector is unavailable.");
        layers.SelectedIndex=1;faces.SelectedIndex=2;
        var selectedFace=(CubeFace)faces.SelectedItem;
        await WaitAsync(()=>view.CurrentSurface?.Subresource is{ArrayLayer:1,MipIndex:0} selection&&selection.Face==selectedFace,"Cube selection did not settle on its layer and face.");
        await view.WaitForRenderAsync();Require(Pixel(view)[2]==136,"Selected cube face pixels are incorrect.");
        var cubePng=await ExportImageAsync(export,view,AssetExportFormat.Png,Path.Combine(output,"exports","cube.png"));
        Require(cubePng.SelectedSubresource?.ArrayLayer==1&&cubePng.SelectedSubresource.Face==selectedFace,"Cube PNG export omitted its selected face.");
        await CaptureWidthsAsync(window,output,"dds-cube");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await window.OpenDdsAsync(input,cancelled.Token);Require(view.CurrentDocument is null&&view.CurrentSurface is null&&((Image)view.FindName("Preview")).Source is null&&!((Button)view.FindName("ExportPng")).IsEnabled,"Cancelled DDS open retained stale pixels or enabled export.");
        await window.OpenDdsAsync(input);await view.WaitForRenderAsync();Require(view.CurrentSurface is not null,"DDS did not recover after cancelled open.");
        var cancelledOutput=Path.Combine(output,"exports","cancelled.png");var cancellationObserved=false;
        try{await view.ExportToAsync(AssetExportFormat.Png,cancelledOutput,true,cancelled.Token);}catch(OperationCanceledException){cancellationObserved=true;}
        Require(cancellationObserved&&!File.Exists(cancelledOutput),"Cancelled DDS export published a file.");
        return new{ColorsPassed=true,ChannelsPassed=true,MipsPassed=true,RapidOpenPassed=true,OriginalBytesPassed=true,ExportHashesPassed=true,ChangedSourceRejected=true,HdrRetained=true,HdrControlsPassed=true,ArraySelectionPassed=true,CubeFaceSelectionPassed=true,CancellationPassed=true,Png=png,Dds=dds,HdrPng=hdrPng,HdrDds=hdrDds,ArrayPng=arrayPng,CubePng=cubePng,Widths=new[]{1600,1280},InputIsSyntheticLayoutFixture=true,NativeDialogSubmission=false};
    }

    private static byte[] Pixel(DdsImageView view)
    {
        var bitmap=(BitmapSource)((Image)view.FindName("Preview")).Source;
        var pixel=new byte[4];bitmap.CopyPixels(new Int32Rect(0,0,1,1),pixel,4,0);return pixel;
    }
    private static async Task<TextureExportResult> ExportImageAsync(MethodInfo method,DdsImageView view,AssetExportFormat format,string output)
    {
        var args=method.GetParameters().Select(parameter=>parameter.ParameterType==typeof(AssetExportFormat)?(object)format:
            parameter.ParameterType==typeof(string)?output:parameter.ParameterType==typeof(bool)?true:
            parameter.ParameterType==typeof(CancellationToken)?CancellationToken.None:throw new InvalidOperationException("Unexpected DDS export facade parameter.")).ToArray();
        return await (Task<TextureExportResult>)method.Invoke(view,args)!;
    }
    private static byte[] HdrFixture()
    {
        var bytes=new byte[164];"DDS "u8.CopyTo(bytes);Put(4,124);Put(8,0x1007);Put(12,4);Put(16,4);
        Put(76,32);Put(80,4);"DX10"u8.CopyTo(bytes.AsSpan(84));Put(108,0x1000);Put(128,95);Put(132,3);Put(140,1);return bytes;
        void Put(int offset,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset,4),value);
    }
    private static byte[] ArrayFixture(bool cube)
    {
        const int size=64;var surfaces=cube?12:2;var bytes=new byte[148+surfaces*size*size*4];"DDS "u8.CopyTo(bytes);
        Put(4,124);Put(8,0x1007);Put(12,size);Put(16,size);Put(76,32);Put(80,4);"DX10"u8.CopyTo(bytes.AsSpan(84));
        Put(108,0x1008);Put(112,cube?0xFE00u:0);Put(128,28);Put(132,3);Put(136,cube?4u:0);Put(140,2);
        for(var index=0;index<surfaces;index++)for(var pixel=0;pixel<size*size;pixel++){var offset=148+(index*size*size+pixel)*4;bytes[offset]=(byte)(40+index*12);bytes[offset+1]=60;bytes[offset+2]=80;bytes[offset+3]=255;}return bytes;
        void Put(int offset,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset,4),value);
    }
}
