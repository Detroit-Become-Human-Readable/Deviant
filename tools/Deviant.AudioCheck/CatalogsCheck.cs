using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Formats.Animation;

namespace Deviant.AudioCheck;

public static partial class Program
{
    private static async Task<object> ExerciseTablesAsync(Deviant.Desktop.AudioWindow window,string input,string output,long ordinal)
    {
        var snapshot=await new ArchiveReader().OpenAsync(input);Invoke(window,"ShowBrowse","animation");var view=window.AnimationBrowser;
        await view.LoadAsync(snapshot);await view.SelectResourceAsync(new(snapshot.IndexContentId,ordinal));
        Require(view.CurrentDocument?.Value?.Data.Version==14,"Selected resource has no version14 ANIMDATA record.");
        var bytes=view.CurrentDocument!.Value!.EncodedResource.ToArray();((CheckBox)view.FindName("TablesToggle")).IsChecked=true;
        await view.SetProfileAsync(AnimationReadProfile.Version14Tables);
        var layout=view.CurrentDocument?.Value?.Version14Layout??throw new InvalidOperationException("Selected version14 layout was not recognized.");
        Require(layout.Tables.Count>0,"Version14 layout contains no tables.");
        var tables=(ComboBox)view.FindName("TableSelect");tables.SelectedIndex=layout.Tables.Count-1;
        var count=view.SelectedTable!.Rows.Count;
        Require(view.CurrentTableRows.Count==Math.Min(100,count),"Table first page size is incorrect.");
        if(count>200){((Button)view.FindName("TableNext")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));((Button)view.FindName("TableNext")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));Require(view.CurrentTableRows[0].Ordinal==200,"Table paging lost source order.");}
        ((DataGrid)view.FindName("TableRows")).SelectedIndex=view.CurrentTableRows.Count-1;
        await CaptureWidthsAsync(window,output,"animation-tables");
        var destination=Path.Combine(output,"exports","animation-tables.json");await view.ExportToAsync(AssetExportFormat.Json,destination,true);
        using(var json=JsonDocument.Parse(await File.ReadAllTextAsync(destination))){Require(Convert.FromBase64String(json.RootElement.GetProperty("EncodedResource").GetString()!).AsSpan().SequenceEqual(bytes),"Table JSON changed source bytes.");Require(json.RootElement.GetProperty("Steps")[0].GetProperty("Options").GetString()!.Contains("profile=Version14Tables",StringComparison.Ordinal),"Table JSON lost selected profile.");}
        await view.SetProfileAsync(AnimationReadProfile.FramedRecords);Require(view.CurrentTableRows.Count==0,"Framed profile retained table rows.");
        return new{snapshot.IndexContentId,ResourceOrdinal=ordinal,TableCount=layout.Tables.Count,SelectedTableRows=count,ProfileJsonPassed=true,RawFramingRestored=true,Sha256=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),Widths=new[]{1600,1280}};
    }
    private static async Task<object> ExerciseTextureGroupsAsync(Deviant.Desktop.AudioWindow window,string input,string output,Deviant.Catalog.AliasStore aliases)
    {
        await window.OpenArchiveAsync(input);await window.ShowTexturesAsync();
        var catalogs=(TextureCatalogSession)typeof(Deviant.Desktop.AudioWindow).GetField("textureCatalogs",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
        await catalogs.EnrichmentTask;await InvokeTaskAsync(window,"RefreshTextureCatalogsAsync");
        var group=catalogs.Groups.First(item=>item.UniqueTextureMembers.Count>0);var view=window.AssetBrowser;await view.SelectGroupAsync(group.CatalogIdentity.Key);
        Require(view.TotalMatches==group.UniqueTextureMembers.Count,"Catalog group did not filter to unique members.");
        var identities=view.CurrentResources.Select(item=>item.Identity).ToArray();var library=new Deviant.Catalog.LibraryStore(aliases.LibraryRoot);var workspace=(await library.ReadAsync()).Workspaces.Single();
        var saved=await library.SaveFilterAsync(workspace.Id,"textures","Catalog selection",view.CaptureFilter());
        var selected=view.CurrentResources.First();((DataGrid)view.FindName("Rows")).SelectedItem=selected;
        ((Button)window.FindName("BookmarkButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));await WaitLibraryAsync(library,state=>state.Bookmarks.Any(item=>item.Identity==selected.Identity));
        await InvokeTaskAsync(window,"RefreshTextureCatalogsAsync");
        Require(view.SelectedIdentity==selected.Identity&&((TextBlock)view.FindName("SelectedName")).Text==selected.Label&&((Button)view.FindName("ExportOriginal")).IsEnabled,"Catalog refresh lost selected details or export availability.");
        await view.RefreshAsync();
        var manualRefresh=new{Expected=selected.Identity,Actual=view.SelectedIdentity,Name=((TextBlock)view.FindName("SelectedName")).Text,ExportEnabled=((Button)view.FindName("ExportOriginal")).IsEnabled};
        await File.WriteAllTextAsync(Path.Combine(output,"manual-refresh.json"),JsonSerializer.Serialize(manualRefresh,new JsonSerializerOptions{WriteIndented=true}));
        Require(view.SelectedIdentity==selected.Identity&&manualRefresh.Name==selected.Label&&manualRefresh.ExportEnabled,"Manual texture refresh lost details: "+JsonSerializer.Serialize(manualRefresh));
        var related=(ListView)view.FindName("RelatedRows");await WaitAsync(()=>related.Items.Cast<Deviant.Desktop.AssetFamilyRelatedChoice>().Any(item=>item.CanNavigate),"Selected unique texture has no navigable related catalog.");
        related.SelectedItem=related.Items.Cast<Deviant.Desktop.AssetFamilyRelatedChoice>().First(item=>item.CanNavigate);
        Require(((Button)view.FindName("OpenRelated")).IsEnabled,"Unique related catalog Open action is disabled.");
        window.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),System.Windows.Threading.DispatcherPriority.ContextIdle);
        Require(TextureVisibleText(related).Contains(group.Label,StringComparer.Ordinal),"Themed unique Related row does not render its catalog label.");
        await CaptureWidthsAsync(window,output,"texture-group");
        await view.NavigateSelectedRelationshipAsync();Require(view.CaptureFilter()["group"]==group.CatalogIdentity.Key,"Related catalog navigation lost its origin group.");
        ((DataGrid)view.FindName("Rows")).SelectedItem=view.CurrentResources.Single(item=>item.Identity==selected.Identity);
        await InvokeTaskAsync(window,"RefreshTextureCatalogsAsync");Require(view.SelectedIdentity==selected.Identity&&((Button)view.FindName("ExportOriginal")).IsEnabled,"Post-navigation refresh reset selected texture.");
        var ambiguous=catalogs.Groups.SelectMany(item=>item.References).Where(item=>item.ResolutionStatus==ReferenceStatus.Ambiguous).SelectMany(item=>item.Candidates).First();
        await view.SelectResourceAsync(ambiguous);await WaitAsync(()=>related.Items.Count>0&&related.Items.Cast<Deviant.Desktop.AssetFamilyRelatedChoice>().All(item=>!item.CanNavigate),"Ambiguous texture references did not settle without navigation.");
        related.SelectedItem=related.Items[0];Require(!((Button)view.FindName("OpenRelated")).IsEnabled,"Ambiguous related catalog Open action is enabled.");
        var before=view.CaptureFilter()["group"];await view.NavigateSelectedRelationshipAsync();Require(view.SelectedIdentity==ambiguous&&view.CaptureFilter()["group"]==before,"Ambiguous Related action changed selection or group.");
        window.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),System.Windows.Threading.DispatcherPriority.ContextIdle);
        Require(TextureVisibleText(related).Contains("Ambiguous references",StringComparer.Ordinal),"Themed ambiguous Related row does not render its status.");
        await CaptureWidthsAsync(window,output,"texture-ambiguous");
        await InvokeTaskAsync(window,"RefreshLibraryAsync");Invoke(window,"ShowLibrary","filters",null);SelectLibraryRow(window,item=>Get<Deviant.Catalog.SavedFilter>(item,"Filter")?.Id==saved.Id);await InvokeTaskAsync(window,"OpenLibrarySelectionAsync");
        Require(view.CaptureFilter()["group"]==group.CatalogIdentity.Key&&view.TotalMatches==group.UniqueTextureMembers.Count,"Saved texture filter did not restore its group.");
        ((DataGrid)view.FindName("Rows")).SelectedItem=view.CurrentResources.Single(item=>item.Identity==selected.Identity);
        var destination=Path.Combine(output,"exports","texture.bin");var report=await view.ExportToAsync(destination,true);var entry=report.Manifest.Entries.Single();
        Require(entry.Status==Deviant.Core.ExportStatus.Completed&&entry.Sha256==Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destination))).ToLowerInvariant(),"Texture Original export failed its hash check.");
        return new{group.Label,Group=group.CatalogIdentity,SelectedMembers=identities,SavedFilterPassed=true,BookmarkPassed=true,OriginalExportPassed=true,SelectionRetained=true,UniqueRelatedOpens=true,AmbiguousRelatedDisabled=true,RelatedLabelsVisible=true,entry.Sha256,Bytes=new FileInfo(destination).Length,DiscoveryIncomplete=catalogs.Progress.DiscoveryIncomplete,NativeDialogSubmission=false,Widths=new[]{1600,1280}};
    }
    private static IEnumerable<string> TextureVisibleText(System.Windows.DependencyObject root)
    {
        for(var index=0;index<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);index++)
        {var child=System.Windows.Media.VisualTreeHelper.GetChild(root,index);if(child is TextBlock{IsVisible:true} block)yield return block.Text;foreach(var text in TextureVisibleText(child))yield return text;}
    }
    private static async Task<object> ExerciseCatalogDemoAsync(Deviant.Desktop.AudioWindow window,string input,string output,long ordinal)
    {
        var snapshot=await new ArchiveReader().OpenAsync(input);
        await using var catalogs=new TextureCatalogSession(snapshot,new(snapshot.IndexContentId,snapshot.PhysicalSourceStamp),sessionOptions:new(MaximumCatalogs:1,PriorityCatalogs:[new(snapshot.IndexContentId,ordinal)]));
        await catalogs.EnrichmentTask;var group=catalogs.Groups.Single(item=>item.CatalogIdentity.Ordinal==ordinal);await catalogs.RevalidateGroupAsync(group.CatalogIdentity);
        Require(group.CatalogExtent.Length<=468,"Catalog demonstration exceeds its bounded payload budget.");
        return new{snapshot.IndexContentId,ResourceOrdinal=ordinal,group.Label,group.CatalogExtent,group.ReferenceTableSha256,group.SourceReferenceCount,UniqueMembers=group.UniqueTextureMembers,group.MissingCount,group.AmbiguousCount,catalogs.Progress,TexturePayloadsRead=false,AudioMetadataScan=false};
    }
}
