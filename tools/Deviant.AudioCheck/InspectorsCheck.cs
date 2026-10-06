using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Desktop;
using Deviant.Formats.Animation;

namespace Deviant.AudioCheck;

public static partial class Program
{
    private static async Task<object> ExerciseInspectorsAsync(AudioWindow window,string input,string output,AliasStore aliases)
    {
        await window.OpenArchiveAsync(input);
        var snapshot=window.CurrentSession?.Snapshot??throw new InvalidOperationException("Inspector source did not open.");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await window.ShowAnimationAsync(cancelled.Token);await window.ShowAnimationAsync();
        var animation=window.AnimationBrowser;
        Require(animation.CurrentResources.Count>=2,"Animation normal show did not recover from a cancelled first show.");
        var first=animation.CurrentResources[0];var second=animation.CurrentResources[1];
        await Task.WhenAll(animation.SelectResourceAsync(first.Identity),animation.SelectResourceAsync(second.Identity));
        Require(animation.SelectedIdentity==second.Identity&&animation.CurrentDocument?.Identity==second.Identity,"Rapid animation selection restored stale data.");
        var encoded=animation.CurrentDocument!.Value!.EncodedResource.ToArray();
        var exportRoot=Path.Combine(output,"exports");var original=Path.Combine(exportRoot,"animation.bin");var jsonPath=Path.Combine(exportRoot,"animation.json");
        await animation.ExportToAsync(AssetExportFormat.Original,original,true);await animation.ExportToAsync(AssetExportFormat.Json,jsonPath,true);
        Require((await File.ReadAllBytesAsync(original)).AsSpan().SequenceEqual(encoded),"Animation Original export changed the resource.");
        using(var json=JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath)))
        {Require(Convert.FromBase64String(json.RootElement.GetProperty("EncodedResource").GetString()!).AsSpan().SequenceEqual(encoded),"Animation JSON did not retain encoded bytes.");Require(json.RootElement.GetProperty("Revision").GetProperty("PhysicalSourceStamp").GetString()==snapshot.PhysicalSourceStamp,"Animation JSON lost source revision.");}
        ((Button)window.FindName("BookmarkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));var library=new LibraryStore(aliases.LibraryRoot);
        await WaitLibraryAsync(library,state=>state.Bookmarks.Any(item=>item.Identity==second.Identity));var bookmark=(await library.ReadAsync()).Bookmarks.Single(item=>item.Identity==second.Identity);
        Invoke(window,"Home_Click",window,new RoutedEventArgs());await InvokeTaskAsync(window,"RefreshLibraryAsync");Invoke(window,"ShowLibrary","bookmarks",null);
        SelectLibraryRow(window,item=>Get<Guid?>(item,"BookmarkId")==bookmark.Id);await InvokeTaskAsync(window,"OpenLibrarySelectionAsync");
        Require(animation.SelectedIdentity==second.Identity,"Animation bookmark did not restore exact selection.");
        ((TextBox)animation.FindName("Search")).Text=first.Label;await WaitAsync(()=>animation.CurrentResources.Count==1,"Animation search did not narrow results.");
        await animation.SelectResourceAsync(second.Identity);await CaptureWidthsAsync(window,output,"animation");
        var container=snapshot.Records.First(row=>row.Type==29);await window.InspectShaderResourceAsync(new(snapshot.IndexContentId,container.Ordinal));var shader=window.ShaderBrowser;
        Require(shader.CurrentScan?.Sources.Count>=2,"Shader fixture did not provide complete and partial sources.");
        var complete=shader.CurrentScan!.Sources.First(item=>item.HasNullTerminator);var partial=shader.CurrentScan.Sources.First(item=>!item.HasNullTerminator);
        await shader.SelectResourceAsync(complete.Identity);Require(((TextBox)shader.FindName("Code")).Text==complete.Text,"Shader preview changed stored text.");
        Require(((Border)shader.FindName("PartialBadge")).Visibility==Visibility.Collapsed,"Complete shader was marked partial.");
        var glsl=Path.Combine(exportRoot,"source.glsl");await shader.ExportToAsync(glsl,true);Require((await File.ReadAllBytesAsync(glsl)).AsSpan().SequenceEqual(complete.Utf8Bytes.Span),"Shader GLSL export changed source bytes.");
        await shader.SelectResourceAsync(partial.Identity);Require(((Border)shader.FindName("PartialBadge")).Visibility==Visibility.Visible,"Partial shader badge is missing.");
        var binary=Path.Combine(exportRoot,"partial.bin");await shader.ExportToAsync(binary,true);Require((await File.ReadAllBytesAsync(binary)).AsSpan().SequenceEqual(partial.Utf8Bytes.Span),"Partial shader binary export changed bytes.");
        await CaptureWidthsAsync(window,output,"shader-partial");((TextBox)shader.FindName("Search")).Text="token";await WaitAsync(()=>shader.CurrentSources.Count==1,"Shader search did not narrow to its fixture token.");
        await shader.SelectResourceAsync(complete.Identity);await CaptureWidthsAsync(window,output,"shader");
        await window.InspectShaderResourceAsync(new(snapshot.IndexContentId,container.Ordinal),cancelled.Token);Require(shader.CurrentScan is null&&shader.CurrentSources.Count==0,"Cancelled shader scan retained stale rows.");
        await window.InspectShaderResourceAsync(new(snapshot.IndexContentId,container.Ordinal));Require(shader.CurrentSources.Count>=2,"Shader scan could not resume after cancellation.");
        await window.OpenArchiveAsync(input);Require(shader.CurrentScan is null&&animation.CurrentDocument is null,"Workspace replacement did not clear inspectors.");
        await window.ShowAnimationAsync();Require(animation.CurrentResources.Count>=2,"Animation inspector could not reload after workspace clear.");
        var copiedIndex=WriteAnimationIndex(Path.Combine(output,"source-check"),encoded);var copiedSnapshot=await new ArchiveReader().OpenAsync(copiedIndex);
        await animation.LoadAsync(copiedSnapshot);await animation.SelectResourceAsync(new(copiedSnapshot.IndexContentId,0));var timestamp=File.GetLastWriteTimeUtc(copiedIndex);
        var changed=await File.ReadAllBytesAsync(copiedIndex);changed[IndexLayout.HeaderLength+11]^=1;await File.WriteAllBytesAsync(copiedIndex,changed);File.SetLastWriteTimeUtc(copiedIndex,timestamp);
        var rejected=false;var staleOutput=Path.Combine(exportRoot,"stale-index.bin");if(File.Exists(staleOutput))File.Delete(staleOutput);
        try{await animation.ExportToAsync(AssetExportFormat.Original,staleOutput,true);}catch(ArchiveException error)when(error.Code=="SourceChanged"){rejected=true;}
        Require(rejected&&!File.Exists(staleOutput),"A changed source index was exported under its old revision.");
        return new{AnimationSelectionPassed=true,AnimationBookmarkPassed=true,AnimationSearchPassed=true,AnimationExportPassed=true,ShaderTextAndExportsPassed=true,PartialBadgePassed=true,ShaderSearchPassed=true,CancelledReloadPassed=true,WorkspaceClearPassed=true,ChangedIndexRejected=true,AnimationSha256=Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant(),ShaderSha256=complete.Sha256,PartialSha256=partial.Sha256,SyntheticInput=true,Widths=new[]{1600,1280}};
    }

    private static async Task<object> ExerciseShaderDemoAsync(AudioWindow window,string input,string output,long ordinal)
    {
        var snapshot=await new ArchiveReader().OpenAsync(input);Invoke(window,"ShowBrowse","shader");var shader=window.ShaderBrowser;
        await shader.LoadAsync(snapshot,new(snapshot.IndexContentId,ordinal));var scan=shader.CurrentScan??throw new InvalidOperationException("Shader container did not load.");
        Require(scan.Sources.Count>0,"Selected container has no stored shader sources.");var chosen=scan.Sources.First(source=>source.CanPreview);
        await shader.SelectResourceAsync(chosen.Identity);await CaptureWidthsAsync(window,output,"shader-real");var destination=Path.Combine(output,"exports","source.glsl");
        await shader.ExportToAsync(destination,true);var exported=await File.ReadAllBytesAsync(destination);
        Require(exported.AsSpan().SequenceEqual(chosen.Utf8Bytes.Span)&&Convert.ToHexString(SHA256.HashData(exported)).Equals(chosen.Sha256,StringComparison.OrdinalIgnoreCase),"Stored real shader export hash differs.");
        return new{snapshot.IndexContentId,ResourceOrdinal=ordinal,SourceCount=scan.Sources.Count,Selected=chosen.Identity,chosen.Sha256,chosen.HasNullTerminator,chosen.DecodedExtent,chosen.PhysicalExtents,ExportedBytes=exported.Length,ExactExportPassed=true,UsedAudioMetadataScan=false,SyntheticInput=false,Widths=new[]{1600,1280}};
    }

    private static string WriteAnimationIndex(string directory,byte[] bytes)
    {
        Directory.CreateDirectory(directory);var index=new byte[IndexLayout.HeaderLength+IndexLayout.RecordLength];IndexLayout.Magic.CopyTo(index);
        BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(20),IndexLayout.SupportedVersion);BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(101),1);
        uint[] fields=[AnimationDataReader.ResourceType,0,1,0,checked((uint)bytes.Length),0,0];for(var field=0;field<fields.Length;field++)BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(IndexLayout.HeaderLength+field*4),fields[field]);
        var path=Path.Combine(directory,"archive.idx");File.WriteAllBytes(path,index);File.WriteAllBytes(Path.Combine(directory,"archive.dat"),bytes);return path;
    }
}
