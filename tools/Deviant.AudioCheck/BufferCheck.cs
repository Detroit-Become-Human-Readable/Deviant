using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Desktop;
using Deviant.Formats.Textures;

namespace Deviant.AudioCheck;

public static partial class Program
{
    private static async Task<object> ExerciseBufferAsync(AudioWindow window,string input,string output,long ordinal,bool standalone)
    {
        var snapshot=await new ArchiveReader().OpenAsync(input);var identity=new ResourceIdentity(snapshot.IndexContentId,ordinal);
        if(standalone)
        {Invoke(window,"ShowBrowse","buffer");var result=await BufferInspections.InspectTextureAsync(snapshot,identity,TextureBufferPatternProfile.ObservedEtfSeGsCandidateV1);await window.BufferBrowser.LoadAsync(snapshot,result,"Texture buffer");}
        else{await window.OpenArchiveAsync(input);Require(window.BufferBrowser.CurrentInspection is null,"Buffer inspection started automatically.");await window.InspectTextureBufferAsync(identity);}
        var view=window.BufferBrowser;var inspection=view.CurrentInspection??throw new InvalidOperationException("Buffer inspection did not load.");var document=inspection.Value??throw new InvalidOperationException("Buffer pattern was unavailable.");
        var decoded=document.Sections.Single(section=>section.Key=="decoded");Require(decoded.Length>=8193,"Generated buffer fixture must contain at least8193decodedbytes.");
        Require(((TextBlock)view.FindName("Summary")).Text.Contains("SEGS",StringComparison.Ordinal)&&!((TextBlock)view.FindName("Summary")).Text.Contains("PatternInspector",StringComparison.Ordinal),"Buffer summary has incorrect display text.");
        window.UpdateLayout();Require(TextureVisibleText((ListBox)view.FindName("Sections")).Contains("Header",StringComparer.Ordinal),"Styled Buffer section labels are blank.");
        await view.SelectSectionAsync("decoded");Require(BufferHex(view).SequenceEqual(decoded.Bytes.Span[..4096].ToArray()),"First hex page differs from retained decoded bytes.");
        ((Button)view.FindName("Next")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await WaitAsync(()=>view.CurrentByteOffset==4096&&BufferHex(view).Length==4096,"Second buffer page did not settle.");Require(BufferHex(view).SequenceEqual(decoded.Bytes.Span.Slice(4096,4096).ToArray()),"Second hex page differs.");
        await CaptureWidthsAsync(window,output,"buffer-decoded");
        await Task.WhenAll(view.SelectSectionAsync("decoded"),view.SelectSectionAsync("header"));Require(view.SelectedSection?.Key=="header","Latest section did not win.");
        await CaptureWidthsAsync(window,output,"buffer-header");
        var original=Path.Combine(output,"exports","buffer.bin");var json=Path.Combine(output,"exports","buffer.json");await view.ExportToAsync(AssetExportFormat.Original,original,true);await view.ExportToAsync(AssetExportFormat.Json,json,true);
        var outputHash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(original))).ToLowerInvariant();Require(outputHash==document.OriginalExtent.PayloadSha256,"Buffer Original SHA differs from displayed provenance.");
        using(var parsed=JsonDocument.Parse(await File.ReadAllTextAsync(json)))
        {var value=parsed.RootElement.GetProperty("Value");Require(value.GetProperty("OriginalExtent").GetProperty("PayloadSha256").GetString()==outputHash,"Buffer JSON lost Original digest.");Require(value.GetProperty("Sections").EnumerateArray().All(section=>!section.TryGetProperty("Bytes",out _)),"Buffer JSON embedded raw section bytes.");Require(value.GetProperty("Sections").EnumerateArray().Single(section=>section.GetProperty("Key").GetString()=="decoded").GetProperty("Sha256").GetString()==decoded.Sha256,"Buffer JSON lost decoded digest.");}
        var bookmark=false;
        if(!standalone)
        {
            ((Button)window.FindName("BookmarkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));var aliases=(Deviant.Catalog.AliasStore)typeof(AudioWindow).GetField("aliases",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(window)!;var library=new Deviant.Catalog.LibraryStore(aliases.LibraryRoot);
            await WaitLibraryAsync(library,state=>state.Bookmarks.Any(item=>item.View=="buffer"&&item.Identity==identity));var saved=(await library.ReadAsync()).Bookmarks.Single(item=>item.View=="buffer"&&item.Identity==identity);
            Invoke(window,"Home_Click",window,new RoutedEventArgs());await InvokeTaskAsync(window,"RefreshLibraryAsync");Invoke(window,"ShowLibrary","bookmarks",null);SelectLibraryRow(window,item=>Get<Guid?>(item,"BookmarkId")==saved.Id);await InvokeTaskAsync(window,"OpenLibrarySelectionAsync");Require(view.SelectedIdentity==identity&&view.CurrentInspection?.Value?.OriginalExtent.PayloadSha256==outputHash,"Buffer bookmark did not restore exact inspection.");bookmark=true;
            var fresh=new AudioWindow((Deviant.Catalog.CacheManager)typeof(AudioWindow).GetField("cache",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(window)!,new Deviant.Catalog.AliasStore(aliases.LibraryRoot)){Width=1280,Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);fresh.Closed+=(_,_)=>closed.TrySetResult();fresh.Show();
            try{await InvokeTaskAsync(fresh,"RefreshLibraryAsync");Invoke(fresh,"ShowLibrary","bookmarks",null);SelectLibraryRow(fresh,item=>Get<Guid?>(item,"BookmarkId")==saved.Id);await InvokeTaskAsync(fresh,"OpenLibrarySelectionAsync");Require(fresh.BufferBrowser.SelectedIdentity==identity&&fresh.BufferBrowser.CurrentInspection?.Value?.OriginalExtent.PayloadSha256==outputHash,"Fresh-window Buffer bookmark did not restore exact inspection.");}
            finally{fresh.Close();await closed.Task;}
        }
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();var cancelledPath=Path.Combine(output,"exports","cancelled.json");var canceled=false;try{await view.ExportToAsync(AssetExportFormat.Json,cancelledPath,true,cancelled.Token);}catch(OperationCanceledException){canceled=true;}Require(canceled&&!File.Exists(cancelledPath),"Cancelled Buffer export published output.");
        await view.ClearAsync();Require(view.CurrentInspection is null&&((TextBox)view.FindName("Hex")).Text==""&&!((Button)view.FindName("ExportOriginal")).IsEnabled,"Buffer clear retained old content.");
        var staleRejected=false;var unsupported=false;
        if(!standalone)
        {
            var guard=WriteBufferGuardFixture(Path.Combine(output,"source-guard"),await File.ReadAllBytesAsync(original));await window.OpenArchiveAsync(guard);var guardIdentity=new ResourceIdentity(window.CurrentSession!.Snapshot.IndexContentId,0);await window.InspectTextureBufferAsync(guardIdentity);
            var sourcePath=Path.ChangeExtension(guard,".dat");var stamp=File.GetLastWriteTimeUtc(sourcePath);var altered=await File.ReadAllBytesAsync(sourcePath);altered[^1]^=1;await File.WriteAllBytesAsync(sourcePath,altered);File.SetLastWriteTimeUtc(sourcePath,stamp);
            var previousPath=Path.Combine(output,"exports","previous.bin");byte[] previous=[9,8,7];await File.WriteAllBytesAsync(previousPath,previous);
            try{await view.ExportToAsync(AssetExportFormat.Original,previousPath,true);}catch(Deviant.Core.ArchiveException error)when(error.Code=="SourceChanged"){staleRejected=true;}
            Require(staleRejected&&(await File.ReadAllBytesAsync(previousPath)).SequenceEqual(previous),"Changed Buffer source overwrote its prior output.");
            var bad=WriteBufferGuardFixture(Path.Combine(output,"unsupported-source"),new byte[]{1,2,3});await window.OpenArchiveAsync(bad);await window.InspectTextureBufferAsync(new(window.CurrentSession!.Snapshot.IndexContentId,0));unsupported=view.CurrentInspection is null&&!((Button)view.FindName("ExportOriginal")).IsEnabled;Require(unsupported,"Unsupported buffer retained actionable content.");
        }
        return new{Identity=identity,SourceChangedPreservesOutput=staleRejected,UnsupportedStatePassed=unsupported,inspection.Revision,Profile=document.Profile,BoundaryBasis=document.BoundaryBasis,SectionCount=document.Sections.Count,DecodedLength=decoded.Length,DecodedSha256=decoded.Sha256,OriginalBytes=new FileInfo(original).Length,OriginalSha256=outputHash,HexPagingPassed=true,LatestSectionPassed=true,OriginalAndJsonPassed=true,BookmarkReloadPassed=bookmark,FreshWindowBookmarkPassed=bookmark,StyledSectionLabelsVisible=true,CancellationPassed=true,ClearPassed=true,Standalone=standalone,PixelsDecoded=false,Widths=new[]{1600,1280}};
    }
    private static string WriteBufferGuardFixture(string directory,byte[] bytes)
    {
        Directory.CreateDirectory(directory);var index=new byte[133];Deviant.Core.IndexLayout.Magic.CopyTo(index);Put(20,18);Put(101,1);uint[] fields=[2229,0,1,0,(uint)bytes.Length,0,0];for(var field=0;field<7;field++)Put(105+4*field,fields[field]);var path=Path.Combine(directory,"archive.idx");File.WriteAllBytes(path,index);File.WriteAllBytes(Path.ChangeExtension(path,".dat"),bytes);return path;
        void Put(int position,uint value)=>System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(position),value);
    }
    private static byte[] BufferHex(BufferInspectionView view)=>((TextBox)view.FindName("Hex")).Text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Select(value=>Convert.ToByte(value,16)).ToArray();
}
