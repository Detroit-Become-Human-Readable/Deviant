using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Deviant.Assets;
using Deviant.Desktop;

namespace Deviant.AudioCheck;

public static partial class Program
{
    private static async Task<object> ExerciseReferenceLinksAsync(AudioWindow window,string input,string output,long ordinal)
    {
        await window.OpenArchiveAsync(input);var owner=new ResourceIdentity(window.CurrentSession!.Snapshot.IndexContentId,ordinal);
        await window.InspectReferencesAsync(owner);var view=window.ReferenceBrowser;await view.FilterArchiveReferencesAsync("");
        Require(view.CurrentArchiveReferences?.Status==Deviant.Browser.LocalizationReferenceReadStatus.Verified,"Archive reference table is not verified.");
        var grid=(DataGrid)view.FindName("Rows");
        Require(grid.Columns.Where(column=>column.Visibility==Visibility.Visible).Select(column=>column.Header?.ToString()).SequenceEqual(new[]{"Reference","Match"}),"Archive reference view exposed raw word columns.");
        Require(((TextBlock)view.FindName("Status")).Text==view.CurrentReferences.Count+" archive references","Archive footer does not match displayed reference count.");
        var unique=view.CurrentReferences.First(row=>row.NavigationTarget is not null);grid.SelectedItem=unique;window.UpdateLayout();
        Require(((Button)view.FindName("OpenReference")).IsEnabled,"Unique typed archive reference Open is disabled.");
        await CaptureWidthsAsync(window,output,"archive-reference-unique");
        var json=Path.Combine(output,"exports","references.json");await view.ExportToAsync(AssetExportFormat.Json,json,true);
        using(var document=JsonDocument.Parse(await File.ReadAllTextAsync(json)))Require(document.RootElement.GetProperty("ArchiveReferences").GetProperty("Relationships")[0].GetProperty("Evidence").GetProperty("Verified").GetBoolean(),"Reference JSON lost verified evidence.");
        var original=Path.Combine(output,"exports","references.bin");await view.ExportToAsync(AssetExportFormat.Original,original,true);
        var sourceRow=window.CurrentSession!.Snapshot.Records[(int)ordinal];var volume=window.CurrentSession.Snapshot.Volumes.Single(item=>item.Package==sourceRow.Package);using(var source=File.OpenRead(volume.FilePath)){source.Position=sourceRow.Offset;var expected=new byte[checked((int)sourceRow.StoredSize)];await source.ReadExactlyAsync(expected);Require((await File.ReadAllBytesAsync(original)).AsSpan().SequenceEqual(expected),"Reference Original export changed source bytes.");}
        Require(RaiseKey(grid,System.Windows.Input.Key.Enter),"Enter did not handle the verified reference.");await WaitAsync(()=>window.AssetBrowser.SelectedIdentity==unique.NavigationTarget,"Verified reference did not route to the exact texture.");
        Require(((Button)window.AssetBrowser.FindName("ExportOriginal")).IsEnabled,"Navigated texture has no Original action.");
        await window.InspectReferencesAsync(owner);await view.FilterArchiveReferencesAsync("");
        grid.SelectedItem=view.CurrentReferences.First(row=>row.NavigationTarget==unique.NavigationTarget);
        ((Button)view.FindName("OpenReference")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(()=>typeof(AudioWindow).GetField("currentView",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(window)?.ToString()=="textures","Open button did not route to texture view.");
        await window.InspectReferencesAsync(owner);await view.FilterArchiveReferencesAsync("");
        var unresolved=view.CurrentReferences.Where(row=>row.NavigationTarget is null).ToArray();Require(unresolved.Length>=2,"Reference fixture must include ambiguous and missing targets.");
        foreach(var row in unresolved){grid.SelectedItem=row;Require(!((Button)view.FindName("OpenReference")).IsEnabled,"Unresolved archive reference enabled Open.");await view.OpenSelectedReferenceAsync();Require(view.SelectedIdentity==owner,"Unresolved reference changed the inspector owner.");}
        await view.FilterArchiveReferencesAsync("Missing");Require(view.CurrentReferences.Count==1&&((TextBlock)view.FindName("Status")).Text=="1 archive reference","Filtered archive footer is incorrect.");
        await CaptureWidthsAsync(window,output,"archive-reference-unresolved");
        await view.FilterArchiveReferencesAsync("");
        ((ComboBox)view.FindName("Mode")).SelectedIndex=0;await WaitAsync(()=>view.CurrentReferences.Any(row=>row.Dependency is not null),"Generated dependency rows did not load.");
        Require(view.CurrentReferences.All(row=>row.NavigationTarget is null),"Dependency candidates became navigable.");grid.SelectedItem=view.CurrentReferences.First();Require(!((Button)view.FindName("OpenReference")).IsEnabled,"Dependency candidate enabled Open.");
        var directory=window.CurrentSession!.Snapshot.Records.First(row=>row.Type==29);await window.InspectReferencesAsync(new(owner.IndexContentId,directory.Ordinal));
        ((ComboBox)view.FindName("Mode")).SelectedIndex=1;await WaitAsync(()=>view.CurrentReferences.Any(row=>row.DirectoryEntry is not null),"Generated directory rows did not load.");Require(view.CurrentReferences.All(row=>row.NavigationTarget is null),"Directory pairs became navigable.");
        grid.SelectedItem=view.CurrentReferences.First();Require(!((Button)view.FindName("OpenReference")).IsEnabled,"Directory pair enabled Open.");await CaptureWidthsAsync(window,output,"directory-reference");
        var guard=WriteReferenceGuardFixture(Path.Combine(output,"source-guard"));await window.OpenArchiveAsync(guard);var guardOwner=new ResourceIdentity(window.CurrentSession!.Snapshot.IndexContentId,0);
        await window.InspectReferencesAsync(guardOwner);await view.FilterArchiveReferencesAsync("");grid.SelectedItem=view.CurrentReferences.Single();
        var guardData=Path.ChangeExtension(guard,".dat");var timestamp=File.GetLastWriteTimeUtc(guardData);var guardBytes=await File.ReadAllBytesAsync(guardData);guardBytes[28]^=1;await File.WriteAllBytesAsync(guardData,guardBytes);File.SetLastWriteTimeUtc(guardData,timestamp);
        var sourceChanged=false;try{await view.OpenSelectedReferenceAsync();}catch(Deviant.Core.ArchiveException error)when(error.Code=="SourceChanged"){sourceChanged=true;}
        Require(sourceChanged&&view.SelectedIdentity==guardOwner,"Changed reference source was followed.");
        return new{Owner=owner,SourceChangedRejected=true,UniqueTarget=unique.NavigationTarget,TypedNavigationPassed=true,KeyboardNavigationPassed=true,OpenButtonPassed=true,OriginalBytes=sourceRow.StoredSize,OriginalExact=true,AmbiguousAndMissingDisabled=true,DependenciesDisabled=true,DirectoryPairsDisabled=true,JsonVerifiedEvidence=true,ModeFooterCountsPassed=true,Widths=new[]{1600,1280},NativeDialogInteraction=false};
    }
    private static string WriteReferenceGuardFixture(string directory)
    {
        Directory.CreateDirectory(directory);var data=new byte[30];"COM_CONT"u8.CopyTo(data);PutLittle(data,8,6);PutLittle(data,12,13);PutLittle(data,16,1);PutLittle(data,20,2229);PutLittle(data,24,2);data[28]=0xA5;data[29]=1;
        var index=new byte[161];Deviant.Core.IndexLayout.Magic.CopyTo(index);PutBig(index,20,18);PutBig(index,101,2);uint[][] rows=[[3,0,1,0,29,0,0],[2229,0,2,29,1,0,0]];
        for(var row=0;row<2;row++)for(var field=0;field<7;field++)PutBig(index,105+28*row+4*field,rows[row][field]);
        var path=Path.Combine(directory,"archive.idx");File.WriteAllBytes(path,index);File.WriteAllBytes(Path.ChangeExtension(path,".dat"),data);return path;
        static void PutLittle(byte[] bytes,int position,uint value)=>System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(position),value);
        static void PutBig(byte[] bytes,int position,uint value)=>System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(position),value);
    }
}
