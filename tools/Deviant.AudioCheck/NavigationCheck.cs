using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Desktop;
using Deviant.Formats.Localization.Model;

namespace Deviant.AudioCheck;

public static partial class Program
{
    private static async Task<object> ExerciseNavigationAsync(AudioWindow window,string input,string output,AliasStore aliases)
    {
        await InvokeTaskAsync(window,"RefreshLibraryAsync");
        Require(((FrameworkElement)window.FindName("HomeHost")).Visibility==Visibility.Visible,"Home was not shown at startup.");
        await CaptureWidthsAsync(window,output,"home");
        await window.OpenArchiveAsync(input);
        await window.CurrentSession!.EnrichmentTask;
        await window.ShowStringsAsync();
        var strings=window.StringsBrowser;
        await strings.CurrentSession!.EnrichmentTask;
        var completedRecordCount=strings.CurrentSession.RecordCount;
        await strings.RefreshAsync();
        await WaitAsync(()=>strings.CurrentRecords.Count>0,"Strings did not populate.");
        var record=strings.CurrentRecords.First(item=>item.Kind==LocalizationRecordKind.Text);
        ((DataGrid)strings.FindName("Rows")).SelectedItem=record;
        await WaitAsync(()=>((Button)window.FindName("BookmarkButton")).IsEnabled,"Selected string could not be bookmarked.");
        ((Button)window.FindName("BookmarkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var library=new LibraryStore(aliases.LibraryRoot);
        await WaitLibraryAsync(library,state=>state.Bookmarks.Any(item=>item.Identity==record.Identity));
        var persisted=await library.ReadAsync();
        var bookmark=persisted.Bookmarks.Single(item=>item.Identity==record.Identity);
        var workspace=persisted.Workspaces.Single(item=>item.Id==bookmark.WorkspaceId);
        var collection=await library.SaveCollectionAsync("Selection",[bookmark.Id]);
        var filter=await library.SaveFilterAsync(workspace.Id,"strings","Key search",new Dictionary<string,string>{{"search",record.Key!},{"language","All languages"}});
        await InvokeTaskAsync(window,"RefreshLibraryAsync");
        Invoke(window,"ShowLibrary","bookmarks",null);
        SelectLibraryRow(window,item=>Get<Guid?>(item,"BookmarkId")==bookmark.Id);
        await InvokeTaskAsync(window,"OpenLibrarySelectionAsync");
        Require(((DataGrid)strings.FindName("Rows")).SelectedItem is LocalizationRecord selected&&selected.Identity==record.Identity,"Bookmark did not restore the selected string.");
        await CaptureWidthsAsync(window,output,"strings");
        Invoke(window,"ShowLibrary","filters",null);
        SelectLibraryRow(window,item=>Get<SavedFilter>(item,"Filter")?.Id==filter.Id);
        await InvokeTaskAsync(window,"OpenLibrarySelectionAsync");
        await WaitAsync(()=>strings.CurrentRecords.Any(item=>item.Identity==record.Identity),"Saved filter lost the matching string.");
        Require(((TextBox)strings.FindName("Search")).Text==record.Key,"Saved filter did not restore search.");
        Invoke(window,"ShowLibrary","collection",collection.Id);
        SelectLibraryRow(window,item=>Get<Guid?>(item,"BookmarkId")==bookmark.Id);
        Invoke(window,"LibraryRemove_Click",window,new RoutedEventArgs());
        await WaitLibraryAsync(library,state=>state.Collections.Single(item=>item.Id==collection.Id).Bookmarks.Count==0);
        Require((await library.ReadAsync()).Bookmarks.Any(item=>item.Id==bookmark.Id),"Removing collection membership removed its bookmark.");
        var stale=await library.BookmarkAsync(workspace.Id,"strings",record.Identity,new(record.Identity.IndexContentId,"previous-revision"),"Stale item");
        await InvokeTaskAsync(window,"RefreshLibraryAsync");
        Invoke(window,"ShowLibrary","bookmarks",null);
        SelectLibraryRow(window,item=>Get<Guid?>(item,"BookmarkId")==stale.Id);
        await InvokeTaskAsync(window,"OpenLibrarySelectionAsync");
        Require(((FrameworkElement)window.FindName("LibraryHost")).Visibility==Visibility.Visible&&((TextBlock)window.FindName("LibraryEmpty")).Text.Contains("revision match",StringComparison.OrdinalIgnoreCase),"Stale bookmark did not retain the library error view.");
        var unavailable=await library.SaveFilterAsync(workspace.Id,"strings","Missing source",new Dictionary<string,string>{{"source","missing-resource"}});
        await InvokeTaskAsync(window,"RefreshLibraryAsync");
        Invoke(window,"ShowLibrary","filters",null);
        SelectLibraryRow(window,item=>Get<SavedFilter>(item,"Filter")?.Id==unavailable.Id);
        await InvokeTaskAsync(window,"OpenLibrarySelectionAsync");
        Require(((FrameworkElement)window.FindName("LibraryHost")).Visibility==Visibility.Visible&&((TextBlock)window.FindName("LibraryEmpty")).Text.Contains("source is unavailable",StringComparison.OrdinalIgnoreCase),"Missing saved-filter source did not retain the library error view.");
        var reopening=window.OpenArchiveAsync(input);
        Invoke(window,"Home_Click",window,new RoutedEventArgs());
        await reopening;
        await Task.Delay(200);
        Require(((FrameworkElement)window.FindName("HomeHost")).Visibility==Visibility.Visible,"Pending archive open overrode Home navigation.");
        var readback=await new LibraryStore(aliases.LibraryRoot).ReadAsync();
        Require(readback.Workspaces.Any(item=>item.Id==workspace.Id)&&readback.Filters.Any(item=>item.Id==filter.Id)&&readback.Collections.Any(item=>item.Id==collection.Id),"Library data did not survive reopening its store.");
        await CaptureAudioLayoutAsync(window,output);
        var loaded=AppDomain.CurrentDomain.GetAssemblies().Where(assembly=>assembly.GetName().Name?.StartsWith("Deviant.",StringComparison.Ordinal)==true&&assembly!=Assembly.GetExecutingAssembly()).Select(assembly=>new{Name=System.IO.Path.GetFileName(assembly.Location),Sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(assembly.Location))).ToLowerInvariant()}).ToArray();
        return new {HomePassed=true,BookmarkPassed=true,SavedFilterPassed=true,CollectionMembershipPassed=true,StaleBookmarkPassed=true,MissingFilterTargetPassed=true,HomeDuringOpenPassed=true,PersistencePassed=true,RecordCount=completedRecordCount,Widths=new[]{1600,1280},AudioLayoutRowsSynthetic=true,DialogSubmissionExercised=false,LoadedAssemblies=loaded};
    }

    private static async Task CaptureAudioLayoutAsync(AudioWindow window,string output)
    {
        var identity=new ResourceIdentity("layout",1);
        var media=new Deviant.Formats.Audio.Model.MediaEntry{Identity=identity,Name="Clip 1",Language="ENG",BankName="Dialogue bank",Duration=12.345,Channels=2,Codec="Vorbis",Completeness=Deviant.Formats.Audio.Model.MediaCompleteness.Unverified};
        var descriptor=new AssetDescriptor(identity,new("layout","revision"),"audio.media","Dialogue clip 1",null,AssetAvailability.Available,AssetCompleteness.Unverified,AssetCapabilities.None,[],[]);
        var row=new Deviant.Browser.AudioExplorerRow(descriptor,media,null);
        Invoke(window,"ShowBrowse","audio");
        var list=(ListView)window.FindName("MediaList");list.ItemsSource=new[]{row};list.SelectedItem=row;
        await CaptureWidthsAsync(window,output,"audio-layout");
    }

    private static async Task CaptureWidthsAsync(AudioWindow window,string output,string name)
    {
        foreach(var width in new[]{1600,1280}){window.Width=width;window.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),System.Windows.Threading.DispatcherPriority.ContextIdle);await Task.Delay(50);Capture(window,System.IO.Path.Combine(output,name+"-"+width+".png"));}
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static object? Invoke(object target,string name,params object?[] args)=>target.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(target,args);
    private static async Task InvokeTaskAsync(object target,string name,params object?[] args)=>await (Task)Invoke(target,name,args)!;
    private static T? Get<T>(object target,string name)=>(T?)target.GetType().GetProperty(name)!.GetValue(target);
    private static void SelectLibraryRow(AudioWindow window,Func<object,bool> predicate)
    {var list=(ListView)window.FindName("LibraryList");list.SelectedItem=list.Items.Cast<object>().Single(predicate);}
    private static async Task WaitLibraryAsync(LibraryStore store,Func<LibraryState,bool> predicate)
    {var clock=System.Diagnostics.Stopwatch.StartNew();while(!predicate(await store.ReadAsync())){if(clock.Elapsed>TimeSpan.FromSeconds(15))throw new InvalidOperationException("Library update did not finish.");await Task.Delay(50);}}
}
