using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Win32;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private LibraryStore library = null!;
    private LibraryState libraryState = new();
    private WorkspaceEntry? workspace;
    private string currentView = "home";
    private string audioResultStatus = "Ready";
    private string libraryView = "bookmarks";
    private Guid? displayedCollection;
    private ResourceIdentity? lastVisitedIdentity;
    private readonly NavigationItem audioNavigation = new("Audio", "audio");
    private readonly NavigationItem bankNavigation = new("Banks", "audio");
    private readonly NavigationItem audioLanguageNavigation = new("Languages", "audio");
    private readonly NavigationItem stringsNavigation = new("Dialogue & Strings", "strings");
    private readonly NavigationItem charactersNavigation = new("Characters", "characters");
    private readonly NavigationItem textureNavigation = new("Textures","textures");
    private readonly NavigationItem ddsNavigation = new("DDS images", "dds");
    private readonly NavigationItem scenesNavigation = new("Scenes", "scenes");
    private readonly ObservableCollection<NavigationItem> explorerRoots=[];
    private readonly NavigationItem stringLanguageNavigation = new("Languages", "strings");
    private readonly NavigationItem collectionsNavigation = new("Collections", "collections");
    private readonly NavigationItem inspectionNavigation = new("Records","records");
    private SourceRevision? animationLoadedRevision;
    private readonly Dictionary<string,NavigationItem> bankNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string,NavigationItem> audioLanguageNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,NavigationItem> stringLanguageNodes = new(StringComparer.OrdinalIgnoreCase);
    private long libraryGeneration;
    private long navigationGeneration;
    private readonly HashSet<Task> libraryTasks=[];
    private Task TrackLibraryTask(Task task){libraryTasks.Add(task);_=ObserveLibraryTaskAsync(task);return task;}
    private async Task ObserveLibraryTaskAsync(Task task){try{await task;}catch{}finally{libraryTasks.Remove(task);}}
    private async Task DrainLibraryTasksAsync(){while(libraryTasks.Count>0){var batch=libraryTasks.ToArray();try{await Task.WhenAll(batch);}catch{}foreach(var task in batch)libraryTasks.Remove(task);}}
    private CancellationTokenSource navigationCancellation=new();
    private (long Generation,CancellationToken Token) BeginNavigation()
    {CancelAudioQueries();pendingDialogueRefresh=null;workspacePreparation?.NotifyActivity();navigationCancellation.Cancel();navigationCancellation.Dispose();navigationCancellation=new();PauseCommonGroupsForNavigation();return(++navigationGeneration,navigationCancellation.Token);}
    private void NavigationError(Exception error){Deviant.Assets.WorkflowDiagnostics.Failure("Workspace.Navigation",error);HomeStatus.Text=StatusText.Text=LibraryEmpty.Text=error.Message;}

    private void InitializeNavigation()
    {
        InitializeTextureCatalogs();
        library = new(aliases.LibraryRoot);
        audioNavigation.Children.Add(bankNavigation); audioNavigation.Children.Add(audioLanguageNavigation);
        stringsNavigation.Children.Add(stringLanguageNavigation);
        textureNavigation.Children.Add(ddsNavigation);
        textureExplorer.OpenDdsRequested += OpenDds_Click;
        foreach(var node in new[]{new NavigationItem("Home","home"),audioNavigation,stringsNavigation,textureNavigation,charactersNavigation,scenesNavigation,inspectionNavigation,new NavigationItem("Bookmarks","bookmarks"),collectionsNavigation,new NavigationItem("Recent items","recent"),new NavigationItem("Saved filters","filters")})explorerRoots.Add(node);
        ExplorerTree.ItemsSource=explorerRoots;
        recordsView.OpenCategoryRequested += OpenRecordCategory;
        stringsView.SelectedRecordChanged += (_, _) => SelectionForLibraryChanged();
        stringsView.ChoicesChanged += (_, _) => UpdateExplorerGroups();
        stringsView.EnrichmentFinished += (_, _) => RefreshPendingAudioDialogue();
        animationView.SelectedResourceChanged+=(_,_)=>SelectionForLibraryChanged();shaderView.SelectedResourceChanged+=(_,_)=>SelectionForLibraryChanged();
        referencesView.SelectedResourceChanged+=(_,_)=>SelectionForLibraryChanged();
        referencesView.RelatedNavigator=NavigateArchiveReferenceAsync;
        sceneView.SelectedResourceChanged+=(_,_)=>SelectionForLibraryChanged();
        bufferView.SelectedResourceChanged+=(_,_)=>SelectionForLibraryChanged();
        Loaded += async (_, _) => { try { await TrackLibraryTask(RefreshLibraryAsync()); } catch (Exception error) { HomeStatus.Text = error.Message; } };
        AudioStatusPanel.Visibility = Visibility.Collapsed;
        RefreshWorkspaceNavigation();
    }

    private async Task RefreshLibraryAsync()
    {
        var generation = ++libraryGeneration;
        var state = await library.ReadAsync();
        if (generation != libraryGeneration || closingInProgress) return;
        libraryState = state;



        collectionsNavigation.Children.Clear();
        foreach (var collection in state.Collections.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            collectionsNavigation.Children.Add(new(collection.Name,"collection") { Id=collection.Id });
        if (currentView == "library") ShowLibrary(libraryView, displayedCollection);
    }
    private async Task WorkspaceOpenedAsync(string path, ArchiveSnapshot snapshot,long navigation,long opening,AudioExplorerSession owner)
    {
        try
        {
            var entry = await library.OpenWorkspaceAsync(snapshot.IndexPath, new(snapshot.IndexContentId,snapshot.PhysicalSourceStamp));
            if(!ReferenceEquals(session,owner)||closingInProgress)return;
            workspace=entry;
            await RefreshLibraryAsync();
        }
        catch (Exception error) { HomeStatus.Text = error.Message; }
        if(!ReferenceEquals(session,owner)||closingInProgress)return;
        lastVisitedIdentity = null;
        audioResultStatus = "Ready";
        relatedCancellation?.Cancel();relatedIdentity=null;
        audioRelationships?.ClearBankGraphCache();audioRelationships=null;relationshipSession=null;
        animationLoadedRevision=null;inspectionNavigation.Children.Clear();
        if(snapshot.Records.Any(row=>row.Type==Deviant.Formats.Animation.AnimationDataReader.ResourceType))inspectionNavigation.Children.Add(new("Animation records","animation"));
        if(snapshot.Records.Any(row=>row.Type==29))inspectionNavigation.Children.Add(new("Container sources","shader-picker"));
        if(snapshot.Records.Any(row=>row.Type==29))inspectionNavigation.Children.Add(new("Physics","physics-picker"));
        recordsView.SetCategories(inspectionNavigation.Children);
        bankNodes.Clear(); audioLanguageNodes.Clear(); stringLanguageNodes.Clear();
        bankNavigation.Children.Clear(); audioLanguageNavigation.Children.Clear(); stringLanguageNavigation.Children.Clear();
        UpdateExplorerGroups();
        if (navigation == navigationGeneration && currentView != "home" && currentView != "library")
            ShowBrowse(currentView);
    }
    private void UpdateExplorerGroups()
    {
        var current = session;
        if (current is null) return;
        RefreshHomeState();
        UpdateExplorerStatus();
        foreach (var bank in current.Banks)
        {
            if (!bankNodes.TryGetValue(bank.Key,out var node)) { node = new(bank.Label,"audio") { Group=bank.Key }; bankNodes.Add(bank.Key,node); bankNavigation.Children.Add(node); }
            node.Label = bank.Label;
        }
        foreach (var language in current.Languages)
            if (!audioLanguageNodes.ContainsKey(language)) { var node = new NavigationItem(language,"audio") { Language=language }; audioLanguageNodes.Add(language,node); audioLanguageNavigation.Children.Add(node); }
        foreach (var language in stringsView.CurrentSession?.Languages ?? [])
            if (!stringLanguageNodes.ContainsKey(language)) { var node = new NavigationItem(language,"strings") { Language=language }; stringLanguageNodes.Add(language,node); stringLanguageNavigation.Children.Add(node); }
    }
    private void ShowBrowse(string view)
    {
        if (!RequireWorkspaceNavigation()) return;
        SelectLibraryTab();
        if (view != "audio") { audioDialogueCancellation?.Cancel(); CancelAudioQueries(); }
        if (view != "physics") SuspendPhysics();
        PhysicsHost.Visibility = view == "physics" ? Visibility.Visible : Visibility.Collapsed;
        if (view != "textures") PauseTextureWorkForNavigation();
        if (view is not ("scenes" or "model")) PauseSceneWorkForNavigation();
        if (view != "buffer") PauseCommonGroupsForNavigation();
        currentView = view;
        if (view == "records") recordsView.SetCategories(inspectionNavigation.Children);
        if (view == "audio") EnsureAudioEnrichment();
        HomeHost.Visibility = LibraryHost.Visibility = Visibility.Collapsed; ContentHost.Visibility = Visibility.Visible;
        AudioLayout.Visibility = view == "audio" ? Visibility.Visible : Visibility.Collapsed;
        RecordsHost.Visibility = view == "records" ? Visibility.Visible : Visibility.Collapsed;
        StringsHost.Visibility = view == "strings" ? Visibility.Visible : Visibility.Collapsed;
        DdsHost.Visibility = view == "dds" ? Visibility.Visible : Visibility.Collapsed;
        AnimationHost.Visibility=view=="animation"?Visibility.Visible:Visibility.Collapsed;ShaderHost.Visibility=view=="shader"?Visibility.Visible:Visibility.Collapsed;
        ReferencesHost.Visibility=view=="references"?Visibility.Visible:Visibility.Collapsed;
        AssetsHost.Visibility=view=="textures"?Visibility.Visible:Visibility.Collapsed;
        BufferHost.Visibility=view=="buffer"?Visibility.Visible:Visibility.Collapsed;
        ScenesHost.Visibility=view is "scenes" or "model"?Visibility.Visible:Visibility.Collapsed;
        AudioStatusPanel.Visibility = view == "audio" ? Visibility.Visible : Visibility.Collapsed;
        stringsMode = view == "strings";
        SaveFilterButton.IsEnabled = session is not null&&view is "audio" or "strings" or "textures";
        SelectionForLibraryChanged(); UpdatePlaybackPresentation();UpdateExplorerStatus();
        UpdateWorkspaceStatus();
        SelectExplorerView(view switch
        {
            "model" => "scenes", "dds" => "textures", "shader" => "shader-picker",
            "physics" => "physics-picker", _ => view
        });
    }
    private void ShowRecords()
    {
        ShowBrowse("records");
    }

    private async void OpenRecordCategory(NavigationItem category)
    {
        if (!RequireWorkspaceNavigation()) return;
        var navigation = BeginNavigation();
        try { await OpenRecordCategoryAsync(category.View, navigation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { NavigationError(error); }
    }

    private async Task OpenRecordCategoryAsync(string view, CancellationToken token)
    {
        if (session is null) return;
        if (view == "animation") { await ShowAnimationAsync(token); return; }
        var choice = view is "shader-picker" or "physics-picker"
            ? ContainerChoiceDialog.Read(this, session.Snapshot) : null;
        if (choice is null) return;
        token.ThrowIfCancellationRequested();
        if (view == "shader-picker") await InspectShaderResourceAsync(choice, token);
        else await InspectPhysicsAsync(choice, token);
    }
    private void Home_Click(object sender,RoutedEventArgs e)
    {
        SelectLibraryTab();
        SuspendPhysics();
        BeginNavigation();
        PauseSceneWorkForNavigation();
        PauseTextureWorkForNavigation();
        currentView="home";HomeHost.Visibility=Visibility.Visible;LibraryHost.Visibility=ContentHost.Visibility=AudioStatusPanel.Visibility=Visibility.Collapsed;
        SelectExplorerView("home");
        BookmarkButton.IsEnabled=SaveFilterButton.IsEnabled=false;UpdatePlaybackPresentation();
        UpdateExplorerStatus();
        UpdateWorkspaceStatus();
    }
    private void UpdateWorkspaceStatus()
    {
        if (!foregroundBusy)
            StatusText.Text = currentView switch { "audio" => audioResultStatus, "physics" => physicsStatus.Text, _ => ExplorerStatus.Text };
        RefreshActivity();
    }
    private void UpdateExplorerStatus()
    {
        ExplorerStatus.Text=currentView switch
        {
            "audio" when session?.EnrichmentTask.IsCanceled==true=>"Audio indexing cancelled",
            "audio" when session?.EnrichmentTask.IsFaulted==true=>"Audio indexing failed",
            "audio" when session?.IsEnrichmentComplete!=true=>"Indexing audio…",
            "strings" when stringsView.CurrentSession?.EnrichmentTask.IsCanceled==true=>"Text indexing cancelled",
            "strings" when stringsView.CurrentSession?.EnrichmentTask.IsFaulted==true=>"Text indexing failed",
            "strings" when stringsView.CurrentSession?.IsEnrichmentComplete!=true=>"Indexing text…",
            "textures" when textureCatalogs is null=>"Loading catalogs…",
            "textures" when textureCatalogs?.Progress.State==TextureCatalogSessionState.Running=>"Indexing catalogs…",
            "textures" when textureCatalogs?.Progress.State==TextureCatalogSessionState.Cancelled=>"Catalog indexing cancelled",
            "textures" when textureCatalogs?.Progress.State==TextureCatalogSessionState.Failed=>"Catalog indexing failed",
            "textures" when textureCatalogs?.Progress.DiscoveryIncomplete==true=>"Catalog discovery incomplete",
            "textures" when textureDiscovery?.EnrichmentTask.IsCanceled==true=>"Texture indexing cancelled",
            "textures" when textureDiscovery?.EnrichmentTask.IsFaulted==true=>"Texture indexing failed",
            "textures" when textureDiscovery?.EnrichmentTask.IsCompleted!=true=>"Indexing names…",
            "scenes" when sceneSession?.Progress.IsRunning==true=>"Discovering scenes…",
            "scenes" when sceneSession?.Progress.HasMore==true=>"More scenes available",
            "scenes" when sceneSession is not null=>$"{sceneSession.Progress.Available:N0} scenes",
            _=>"Ready"
        };
    }
    private async void Installation_Click(object sender,RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title="Choose installation" };
        if(dialog.ShowDialog(this)==true) { Home_Click(this,new()); await OpenArchiveAsync(dialog.FolderName); }
    }
    public DdsImageView ImageBrowser=>ddsView;
    public AnimationRecordView AnimationBrowser=>animationView;
    public StoredShaderView ShaderBrowser=>shaderView;
    public SceneReferencesView ReferenceBrowser=>referencesView;
    public AssetFamilyView AssetBrowser=>familyView;
    public async Task ShowTexturesAsync(CancellationToken token=default)
    {
        if (!RequireWorkspaceNavigation()) return;
        var current = session;
        if (current is null) return;
        var navigation = navigationGeneration;
        ShowBrowse("textures");
        await sceneNavigationDrain.WaitAsync(token);
        await textureNavigationDrain.WaitAsync(token);
        if (navigation != navigationGeneration || currentView != "textures" || !ReferenceEquals(current, session)) return;
        token.ThrowIfCancellationRequested();
        if (navigation != navigationGeneration || currentView != "textures" || !ReferenceEquals(current, session)) return;
        await EnsureTextureCatalogsAsync(current, token);
        if (navigation == navigationGeneration && currentView == "textures" && ReferenceEquals(current, session))
            textureExplorer.Resume();
        else
            await textureExplorer.SuspendAsync();
    }
    public async Task InspectReferencesAsync(ResourceIdentity identity,CancellationToken token=default)
    {if(session is null)return;ShowBrowse("references");await referencesView.LoadAsync(session.Snapshot,new(identity.IndexContentId,identity.Ordinal),token);}
    public async Task ShowAnimationAsync(CancellationToken token=default)
    {if(session is null)return;ShowBrowse("animation");var revision=new SourceRevision(session.Snapshot.IndexContentId,session.Snapshot.PhysicalSourceStamp);if(animationLoadedRevision!=revision){await animationView.LoadAsync(session.Snapshot,token);animationLoadedRevision=animationView.LoadedRevision;}}
    public async Task InspectShaderResourceAsync(ResourceIdentity identity,CancellationToken token=default)
    {if(session is null)return;ShowBrowse("shader");await shaderView.LoadAsync(session.Snapshot,new(identity.IndexContentId,identity.Ordinal),token);}
    public async Task OpenDdsAsync(string path,CancellationToken token=default){BeginNavigation();ShowBrowse("dds");await ddsView.OpenAsync(path,token);}
    private async void OpenDds_Click(object sender,RoutedEventArgs e)
    {var dialog=new OpenFileDialog{Title="Open DDS",Filter="DDS images (*.dds)|*.dds"};if(dialog.ShowDialog(this)==true)await OpenDdsAsync(dialog.FileName);}
    private async void Explorer_SelectedItemChanged(object sender,RoutedPropertyChangedEventArgs<object> e)
    {
        if(synchronizingExplorer || e.NewValue is not NavigationItem node) return;
        if (!IsWorkspaceValidated && node.View != "home") { RequireWorkspaceNavigation(); return; }
        var navigation=BeginNavigation();
        try
        {
            if(node.View=="home") { Home_Click(this,new());return; }
            if(node.View=="characters") { await ShowCharactersCoreAsync(navigation.Token, navigation); return; }
            if(node.View=="dds") {ShowBrowse("dds");return;}
            if(node.View=="textures"){await ShowTexturesAsync(navigation.Token);if(node.Group is not null)await SelectTextureContextAsync(node.Group,navigation.Generation,navigation.Token);return;}
            if(node.View=="scenes"){await ShowScenesAsync(navigation.Token);return;}
            if(node.View=="records"){ShowRecords();return;}
            if(node.View is "animation" or "shader-picker" or "physics-picker")
            {await OpenRecordCategoryAsync(node.View,navigation.Token);return;}
            if(node.View is "bookmarks" or "recent" or "filters" or "collections" or "collection") { ShowLibrary(node.View,node.Id);return; }
            if(session is null) { Home_Click(this,new());HomeStatus.Text="Open a workspace to browse assets.";return; }
            if(node.View=="strings") { await ShowStringsAsync(navigation.Token);if(navigation.Generation!=navigationGeneration)return;if(node.Language is not null) await stringsView.ApplyFilterAsync(new Dictionary<string,string>{{"language",node.Language}},navigation.Token); }
            else
            {
                ShowBrowse("audio"); syncingControls=true;
                try { BankBox.SelectedItem=node.Group is null?BankBox.Items[0]:BankBox.Items.Cast<AudioBankChoice>().FirstOrDefault(bank=>bank.Key==node.Group);LanguageBox.SelectedItem=node.Language??"Any"; }
                finally { syncingControls=false; }
                await QueryAsync(true);
            }
        }
        catch(OperationCanceledException){}catch(Exception error){NavigationError(error);}
    }
    private (string View,ResourceIdentity Identity,SourceRevision Revision,string Label)? SelectedDiscovery()
    {
        if(currentView=="characters" && charactersView.SelectedDescriptor is {} character)return("characters",character.Identity,character.Revision,character.Label);
        if(currentView=="physics" && physicsLease is {} physics)return("physics",physics.MemberIdentity,physics.Revision,(physicsFrames.SelectedItem as PhysicsFrameChoice)?.Label ?? "Physics record");
        if(currentView=="references"&&referencesView.SelectedIdentity is{}reference&&referencesView.LoadedRevision is{}referenceRevision)return("references",reference,referenceRevision,referencesView.SelectedLabel??"Resource references");
        if(currentView=="textures"&&textureExplorer.SelectedIdentity is{}texture&&textureExplorer.LoadedRevision is{}textureRevision)return("textures",texture,textureRevision,textureExplorer.SelectedLabel??"Texture");
        if(currentView=="buffer" && commonGroupMode && commonGroupLease is {} group && ReferenceEquals(bufferView.CurrentInspection,group.Inspection))return("gmk",group.MemberIdentity,group.Revision,(commonGroupFrames.SelectedItem as CommonGroupChoice)?.Label ?? "Common group");
        if(currentView=="buffer"&&bufferView.SelectedIdentity is{}buffer&&bufferView.LoadedRevision is{}bufferRevision)return("buffer",buffer,bufferRevision,bufferView.SelectedLabel??"Buffer");
        if(currentView=="animation"&&animationView.SelectedIdentity is{}animation&&session is{}animationSession)return("animation",animation,new(animationSession.Snapshot.IndexContentId,animationSession.Snapshot.PhysicalSourceStamp),animationView.SelectedLabel??"Animation record");
        if(currentView=="scenes"&&sceneView.SelectedIdentity is{}scene&&sceneView.LoadedRevision is{}sceneRevision)return("scenes",scene,sceneRevision,sceneView.SelectedLabel??"Scene node");
        if(currentView=="shader"&&shaderView.SelectedSource is{}shader)return("shader",shader.Identity,shader.Revision,shaderView.SelectedLabel??"Shader source");
        if(currentView=="strings"&&stringsView.SelectedRecord is{}record&&session is{}textSession)
            return("strings",record.Identity,new(textSession.Snapshot.IndexContentId,textSession.Snapshot.PhysicalSourceStamp),record.Key??record.Kind.ToString());
        if(currentView=="audio"&&CurrentRow is{}audio) return("audio",audio.Identity,audio.Descriptor.Revision,audio.Label);
        return null;
    }
    private void SelectionForLibraryChanged()
    {
        var selected=SelectedDiscovery();BookmarkButton.IsEnabled=workspace is not null&&selected is not null;
        if(selected is{}item&&workspace is{}active&&lastVisitedIdentity!=item.Identity)
        {lastVisitedIdentity=item.Identity;_=TrackLibraryTask(RecordVisitAsync(active.Id,item));}
    }
    private async Task RecordVisitAsync(Guid id,(string View,ResourceIdentity Identity,SourceRevision Revision,string Label) item)
    {try{await library.VisitAsync(id,item.View,item.Identity,item.Revision,item.Label);}catch(Exception error){HomeStatus.Text=error.Message;}}
    private async void Bookmark_Click(object sender,RoutedEventArgs e)
    {
        if(workspace is null||SelectedDiscovery() is not{}selected)return;
        try{await library.BookmarkAsync(workspace.Id,selected.View,selected.Identity,selected.Revision,selected.Label);await RefreshLibraryAsync();StatusText.Text="Bookmarked";}
        catch(Exception error){StatusText.Text=error.Message;}
    }
    private async void SaveFilter_Click(object sender,RoutedEventArgs e)
    {
        if(workspace is null||currentView is not("audio" or "strings" or "textures"))return;
        var name=LibraryNameDialog.Read(this,"Save filter");if(name is null)return;
        try{await library.SaveFilterAsync(workspace.Id,currentView,name,currentView=="strings"?stringsView.CaptureFilter():currentView=="textures"?textureExplorer.CaptureFilter():CaptureAudioFilter());await RefreshLibraryAsync();StatusText.Text="Filter saved";}
        catch(Exception error){StatusText.Text=error.Message;}
    }
    private IReadOnlyDictionary<string,string> CaptureAudioFilter()=>new Dictionary<string,string>
    {
        ["search"]=SearchBox.Text,["bank"]=(BankBox.SelectedItem as AudioBankChoice)?.Key??"",["language"]=LanguageBox.SelectedItem as string??"Any",
        ["codec"]=CodecBox.SelectedItem as string??"Any",["channels"]=ChannelsBox.SelectedItem?.ToString()??"Any",
        ["availability"]=AvailabilityBox.SelectedIndex.ToString(),["completeness"]=CompletenessBox.SelectedIndex.ToString(),["minimum"]=MinimumDurationBox.Text,["maximum"]=MaximumDurationBox.Text,
        ["sort"]=((AudioSortBox.SelectedItem as AssetSortChoice)?.Sort??AssetSort.Name).ToString(),["descending"]=(AudioDescending.IsChecked==true).ToString()
    };
    private void ShowLibrary(string view,Guid? collectionId=null)
    {
        PauseCommonGroupsForNavigation();
        SelectLibraryTab();
        PauseSceneWorkForNavigation();
        SuspendPhysics();
        PauseTextureWorkForNavigation();
        currentView="library";libraryView=view;displayedCollection=collectionId;
        HomeHost.Visibility=ContentHost.Visibility=AudioStatusPanel.Visibility=Visibility.Collapsed;LibraryHost.Visibility=Visibility.Visible;
        BookmarkButton.IsEnabled=SaveFilterButton.IsEnabled=false;
        var names=libraryState.Workspaces.ToDictionary(item=>item.Id,item=>item.Label);
        var collection=libraryState.Collections.FirstOrDefault(item=>item.Id==collectionId);
        LibraryTitle.Text=view switch{"recent"=>"Recent items","filters"=>"Saved filters","collections"=>"Collections","collection"=>collection?.Name+" · User collection",_=>"Bookmarks"};
        IEnumerable<LibraryRow> rows=view switch
        {
            "recent"=>libraryState.RecentItems.Select(item=>new LibraryRow(item.Label,item.View,names.GetValueOrDefault(item.WorkspaceId,"Workspace"),RevisionStatus(item.WorkspaceId,item.Revision),item.WorkspaceId,item.Identity,item.Revision)),
            "filters"=>libraryState.Filters.Select(item=>new LibraryRow(item.Name,item.View,names.GetValueOrDefault(item.WorkspaceId,"Workspace"),"",item.WorkspaceId,Filter:item)),
            "collections"=>libraryState.Collections.Select(item=>new LibraryRow(item.Name,"User collection","",$"{item.Bookmarks.Count:N0} bookmarks",CollectionId:item.Id)),
            _=>libraryState.Bookmarks.Where(item=>view!="collection"||collection?.Bookmarks.Contains(item.Id)==true).Select(item=>new LibraryRow(item.Label,item.View,names.GetValueOrDefault(item.WorkspaceId,"Workspace"),RevisionStatus(item.WorkspaceId,item.Revision),item.WorkspaceId,item.Identity,item.Revision,item.Id))
        };
        var visible=rows.ToArray();LibraryList.ItemsSource=visible;LibraryEmpty.Text=visible.Length==0?view=="filters"?"Save a filter from Audio or Dialogue & Strings.":view=="collections"?"Create a collection for your bookmarks.":"Bookmark an asset to return to it later.":"";
        UpdatePlaybackPresentation();
        UpdateWorkspaceStatus();
    }
    private string RevisionStatus(Guid id,SourceRevision revision)=>libraryState.Workspaces.FirstOrDefault(item=>item.Id==id)?.Revision==revision?"":"Source changed";
    private async void Library_DoubleClick(object sender,MouseButtonEventArgs e)=>await OpenLibrarySelectionAsync();
    private async void LibraryOpen_Click(object sender,RoutedEventArgs e)=>await OpenLibrarySelectionAsync();
    private async Task OpenLibrarySelectionAsync()
    {
        if(LibraryList.SelectedItem is not LibraryRow row)return;
        var navigation=BeginNavigation();
        try
        {
            if(row.CollectionId is{}collection){ShowLibrary("collection",collection);return;}
            var saved=libraryState.Workspaces.FirstOrDefault(item=>item.Id==row.WorkspaceId)??throw new InvalidOperationException("Workspace unavailable.");
            if(workspace?.Id!=saved.Id||session is null) await OpenAsync(saved.IndexPath);
            if(navigation.Generation!=navigationGeneration)return;
            if(session is null||workspace?.Id!=saved.Id||!session.Snapshot.IndexPath.Equals(saved.IndexPath,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The requested workspace could not be opened.");
            var revision=new SourceRevision(session.Snapshot.IndexContentId,session.Snapshot.PhysicalSourceStamp);
            if(row.Revision is not null&&!LibraryStore.MatchesRevision(row.Revision,revision))throw new InvalidOperationException("This bookmark needs a revision match.");
            if(row.Filter is{}filter){await ApplySavedFilterAsync(filter,navigation.Generation,navigation.Token);return;}
            if(row.Identity is{}identity)await NavigateDiscoveryAsync(row.View,identity,navigation.Generation,navigation.Token);
        }
        catch(OperationCanceledException){}catch(Exception error){ShowLibrary(libraryView,displayedCollection);NavigationError(error);}
    }
    private async Task NavigateDiscoveryAsync(string view,ResourceIdentity identity,long generation,CancellationToken token)
    {
        if(session is null||session.Snapshot.IndexContentId!=identity.IndexContentId)throw new InvalidOperationException("This item belongs to another archive.");
        var current=session;
        if(view=="characters")
        {
            charactersView.SetSearchQuery("", notify: false);
            await ShowCharactersCoreAsync(token, (generation, token), requestedIdentity: identity);
            if(generation!=navigationGeneration || !ReferenceEquals(current,session))return;
            charactersView.SelectResource(identity);
            return;
        }
        if(view=="references"){await InspectReferencesAsync(identity,token);return;}
        if(view=="gmk"){await InspectCommonGroupsCoreAsync(identity,generation,token);return;}
        if(view=="buffer"){await InspectBufferAsync(identity,generation,token);return;}
        if(view=="physics"){await InspectPhysicsAsync(identity,token);return;}
        if(view=="textures"){await ShowTexturesAsync(token);if(generation!=navigationGeneration)return;await textureExplorer.SelectResourceAsync(identity,token);return;}
        if(view=="animation"){await ShowAnimationAsync(token);if(generation!=navigationGeneration)return;await animationView.SelectResourceAsync(identity,token);return;}
        if(view=="scenes"){await ShowScenesAsync(token);if(generation!=navigationGeneration)return;await sceneView.SelectResourceAsync(identity,token);return;}
        if(view=="shader"){await InspectShaderResourceAsync(identity,token);if(generation!=navigationGeneration)return;await shaderView.SelectResourceAsync(identity,token);return;}
        if(view=="strings"){await ShowStringsAsync(token);if(generation!=navigationGeneration)return;await stringsView.SelectRecordAsync(identity,token);return;}
        ShowBrowse("audio");StatusText.Text="Finding saved item…";await current.EnrichmentTask.WaitAsync(token);if(generation!=navigationGeneration||!ReferenceEquals(current,session))return;
        var row=current.ResolveSelection([identity]).SingleOrDefault()??throw new InvalidOperationException("The saved clip is unavailable.");
        filterTimer.Stop();queryCancellation?.Cancel();++queryGeneration;
        syncingControls=true;try{SearchBox.Text="";BankBox.SelectedIndex=0;LanguageBox.SelectedIndex=CodecBox.SelectedIndex=ChannelsBox.SelectedIndex=AvailabilityBox.SelectedIndex=CompletenessBox.SelectedIndex=0;MinimumDurationBox.Clear();MaximumDurationBox.Clear();}finally{syncingControls=false;}
        suppressSelectionTracking=true;try{MediaList.ItemsSource=new[]{row};MediaList.SelectedItem=row;}finally{suppressSelectionTracking=false;}
        selectedIdentities.Clear();selectedIdentities.Add(identity.Key);SelectionText.Text="1 selected";PageText.Text="Saved item";UpdateSelectedDetails();SelectionForLibraryChanged();
    }
    private async Task ApplySavedFilterAsync(SavedFilter filter,long generation,CancellationToken token)
    {
        if(filter.View=="textures")
        {
            await ShowTexturesAsync(token);if(generation!=navigationGeneration)return;
            var discovery=textureDiscovery??throw new InvalidOperationException("Open textures first.");
            var key=filter.Values.GetValueOrDefault("group","");
            if(key.Length>0)await EnsureSavedTextureCatalogAsync(key,token);
            else await discovery.EnrichmentTask.WaitAsync(token);
            if(generation!=navigationGeneration||discovery!=textureDiscovery)return;
            await textureExplorer.ApplyFilterAsync(filter.Values,token);return;
        }
        if(filter.View=="strings"){await ShowStringsAsync(token);if(generation!=navigationGeneration)return;await stringsView.ApplyFilterAsync(filter.Values,token);return;}
        var current=session;if(current is null)return;await current.EnrichmentTask.WaitAsync(token);if(generation!=navigationGeneration||!ReferenceEquals(current,session))return;UpdateFilterChoices(current);ShowBrowse("audio");var values=filter.Values;
        foreach(var pair in new[]{("language",LanguageBox),("codec",CodecBox),("channels",ChannelsBox)})
            if(!pair.Item2.Items.Cast<object>().Any(item=>item.ToString()==values.GetValueOrDefault(pair.Item1,"Any")))throw new InvalidOperationException($"The saved {pair.Item1} is unavailable.");
        var sort=Enum.TryParse<AssetSort>(values.GetValueOrDefault("sort","Name"),out var parsed)?parsed:throw new InvalidOperationException("The saved sort is unavailable.");
        var choice=AudioSortBox.Items.Cast<AssetSortChoice>().FirstOrDefault(item=>item.Sort==sort)??throw new InvalidOperationException("The saved sort is unavailable.");
        syncingControls=true;
        try
        {
            var bank=values.GetValueOrDefault("bank","");BankBox.SelectedItem=bank.Length==0?BankBox.Items[0]:BankBox.Items.Cast<AudioBankChoice>().FirstOrDefault(item=>item.Key==bank)??throw new InvalidOperationException("The saved bank is unavailable.");
            SearchBox.Text=values.GetValueOrDefault("search","");LanguageBox.SelectedItem=values.GetValueOrDefault("language","Any");CodecBox.SelectedItem=values.GetValueOrDefault("codec","Any");ChannelsBox.SelectedItem=values.GetValueOrDefault("channels","Any");
            AvailabilityBox.SelectedIndex=int.TryParse(values.GetValueOrDefault("availability"),out var availability)?Math.Clamp(availability,0,5):0;CompletenessBox.SelectedIndex=int.TryParse(values.GetValueOrDefault("completeness"),out var completeness)?Math.Clamp(completeness,0,3):0;
            MinimumDurationBox.Text=values.GetValueOrDefault("minimum","");MaximumDurationBox.Text=values.GetValueOrDefault("maximum","");
            AudioSortBox.SelectedItem=choice;AudioDescending.IsChecked=bool.TryParse(values.GetValueOrDefault("descending"),out var descending)&&descending;
        }
        finally{syncingControls=false;}
        await QueryAsync(true);
    }
    private async void NewCollection_Click(object sender,RoutedEventArgs e)
    {
        var name=LibraryNameDialog.Read(this,"New collection");if(name is null)return;
        try{var selected=LibraryList.SelectedItems.Cast<LibraryRow>().Where(row=>row.BookmarkId is not null).Select(row=>row.BookmarkId!.Value).ToArray();await library.SaveCollectionAsync(name,selected);await RefreshLibraryAsync();ShowLibrary("collections");}
        catch(Exception error){LibraryEmpty.Text=error.Message;}
    }
    private void AddToCollection_Click(object sender,RoutedEventArgs e)
    {
        var ids=LibraryList.SelectedItems.Cast<LibraryRow>().Where(row=>row.BookmarkId is not null).Select(row=>row.BookmarkId!.Value).ToArray();
        if(ids.Length==0){LibraryEmpty.Text="Select bookmarks to add.";return;}
        var menu=new ContextMenu();
        foreach(var collection in libraryState.Collections)
        {var item=new MenuItem{Header=collection.Name};item.Click+=async(_,_)=>{try{await library.AddCollectionMembersAsync(collection.Id,ids);await RefreshLibraryAsync();}catch(Exception error){NavigationError(error);}};menu.Items.Add(item);}
        if(menu.Items.Count==0){LibraryEmpty.Text="Create a collection first.";return;}menu.IsOpen=true;
    }
    private async void LibraryRemove_Click(object sender,RoutedEventArgs e)
    {
        try{foreach(var row in LibraryList.SelectedItems.Cast<LibraryRow>().ToArray()){if(row.BookmarkId is{}bookmark){if(libraryView=="collection"&&displayedCollection is{}collectionId)await library.RemoveCollectionMembersAsync(collectionId,[bookmark]);else await library.RemoveBookmarkAsync(bookmark);}else if(row.CollectionId is{}collection)await library.RemoveCollectionAsync(collection);else if(row.Filter is{}filter)await library.RemoveFilterAsync(filter.Id);else if(libraryView=="recent"&&row.Identity is{}identity)await library.RemoveRecentAsync(row.WorkspaceId,row.View,identity);}await RefreshLibraryAsync();}
        catch(Exception error){LibraryEmpty.Text=error.Message;}
    }
    private sealed record LibraryRow(string Label,string View,string Workspace,string Status,Guid WorkspaceId=default,
        ResourceIdentity? Identity=null,SourceRevision? Revision=null,Guid? BookmarkId=null,SavedFilter? Filter=null,Guid? CollectionId=null);
}

internal sealed class NavigationItem(string label,string view) : INotifyPropertyChanged
{
    public string Label{get=>label;set{if(label==value)return;label=value;PropertyChanged?.Invoke(this,new(nameof(Label)));}}
    public string View{get;}=view; public string? Group{get;init;} public string? Language{get;init;} public Guid? Id{get;init;}
    public ObservableCollection<NavigationItem> Children{get;}=[];
    public event PropertyChangedEventHandler? PropertyChanged;
}
internal sealed class LibraryNameDialog : Window
{
    private readonly TextBox name=new(){MaxLength=512,MinWidth=280,Margin=new(0,8,0,12)};
    private LibraryNameDialog(string title)
    {
        Title=title;SizeToContent=SizeToContent.WidthAndHeight;WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty,"WindowBackground");SetResourceReference(ForegroundProperty,"WindowForeground");
        var panel=new StackPanel{Margin=new(20)};panel.Children.Add(new TextBlock{Text="Name"});panel.Children.Add(name);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};var save=new Button{Content="Save",IsDefault=true};save.Click+=(_,_)=>{if(!string.IsNullOrWhiteSpace(name.Text))DialogResult=true;};buttons.Children.Add(save);buttons.Children.Add(new Button{Content="Cancel",IsCancel=true});panel.Children.Add(buttons);Content=panel;Loaded+=(_,_)=>name.Focus();
    }
    public static string? Read(Window owner,string title){var dialog=new LibraryNameDialog(title){Owner=owner};return dialog.ShowDialog()==true?dialog.name.Text.Trim():null;}
}


