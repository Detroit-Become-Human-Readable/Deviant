using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Browser;
using Deviant.Formats.Audio.Model;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class NavigationTests
{
    [Fact]
    public void HomeOpeningDoesNotRestoreUnrelatedAudioMetadata() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        DiscoveryFixture.WriteIndex(fixture.Index, [(999u, new byte[] { 1, 2, 3 })]);
        Pump(window.OpenArchiveAsync(fixture.Index));
        var initialization = (Lazy<Task>)typeof(AudioExplorerSession).GetField("audioInitialization", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window.CurrentSession!)!;
        Assert.False(initialization.IsValueCreated);
        ((TextBox)window.FindName("SearchBox")).Text = "Hidden audio filter";
        var debounce = (DispatcherTimer)typeof(AudioWindow).GetField("filterTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Assert.False(debounce.IsEnabled);
        Pump(Task.Delay(debounce.Interval + TimeSpan.FromMilliseconds(100)));
        Assert.False(initialization.IsValueCreated);
    });

    [Theory]
    [InlineData(1280)]
    [InlineData(1600)]
    public void FooterStatusAndEarlyBuildDisclaimerStayVisibleAcrossViews(double width)
    {
        WorkspaceCommitTests.Run((window, _) =>
        {
            window.Width = width;
            var footer = (FrameworkElement)window.FindName("ApplicationStatusBar");
            var notice = (TextBlock)window.FindName("PrereleaseNotice");
            var status = (FrameworkElement)window.FindName("StatusText");
            CheckLayout();
            Invoke(window, "ShowBrowse", "dds");
            CheckLayout();
            Invoke(window, "Home_Click", window, new RoutedEventArgs());
            CheckLayout();

            void CheckLayout()
            {
                window.UpdateLayout();
                Assert.True(footer.IsVisible);
                Assert.True(notice.IsVisible);
                Assert.Equal("This is an early build. Bugs are expected.", notice.Text);
                var statusBounds = status.TransformToAncestor(footer).TransformBounds(new Rect(status.RenderSize));
                Assert.True(statusBounds.Left >= 0, "The operation status extends outside the footer.");
                Assert.True(statusBounds.Right <= footer.ActualWidth, "The operation status extends outside the footer.");
            }
        });
    }

    [Fact]
    public void HomeSurvivesPendingOpenAndMissingSavedWorkspacePreservesCurrentSession()
    {
        Run((window,root)=>
        {
            var index=CreateIndex(root);
            Assert.Equal(Visibility.Visible,((FrameworkElement)window.FindName("HomeHost")).Visibility);
            var opening=window.OpenArchiveAsync(index);
            Invoke(window,"Home_Click",window,new RoutedEventArgs());
            Pump(opening);
            Assert.Equal(Visibility.Visible,((FrameworkElement)window.FindName("HomeHost")).Visibility);
            if(window.CurrentSession is null)Pump(window.OpenArchiveAsync(index));
            var original=window.CurrentSession;
            Assert.True(original is not null,((TextBlock)window.FindName("StatusText")).Text);
            var library=new LibraryStore(Path.Combine(root,"library"));
            var absent=Pump(library.OpenWorkspaceAsync(Path.Combine(root,"absent.idx"),new("absent","revision")));
            var filter=Pump(library.SaveFilterAsync(absent.Id,"audio","Search",new Dictionary<string,string>{{"search","clip"}}));
            Pump((Task)Invoke(window,"RefreshLibraryAsync")!);
            Invoke(window,"ShowLibrary","filters",null);
            var list=(ListView)window.FindName("LibraryList");
            list.SelectedItem=list.Items.Cast<object>().Single(row=>((SavedFilter?)row.GetType().GetProperty("Filter")!.GetValue(row))?.Id==filter.Id);
            Pump((Task)Invoke(window,"OpenLibrarySelectionAsync")!);
            Assert.Same(original,window.CurrentSession);
            Assert.Equal(Visibility.Visible,((FrameworkElement)window.FindName("LibraryHost")).Visibility);
            Assert.Contains("requested workspace",((TextBlock)window.FindName("LibraryEmpty")).Text,StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void OverlappingOpensKeepTheLatestWorkspaceAndHomeNavigation()
    {
        Run((window,root)=>
        {
            var original=OpenCompleted(window,root);
            var commit=(SemaphoreSlim)typeof(AudioWindow).GetField("workspaceCommit",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
            var firstPath=CreateIndex(Path.Combine(root,"first"),2);
            var latestPath=CreateIndex(Path.Combine(root,"latest"),3);
            commit.Wait();Task first,latest;
            try
            {
                first=window.OpenArchiveAsync(firstPath);Pump(Task.Delay(100));
                Assert.Same(original,window.CurrentSession);
                latest=window.OpenArchiveAsync(latestPath);Invoke(window,"Home_Click",window,new RoutedEventArgs());
            }
            finally{commit.Release();}
            Pump(Task.WhenAll(first,latest));
            Assert.Equal(latestPath,window.CurrentSession?.Snapshot.IndexPath);
            Assert.Equal(Visibility.Visible,((FrameworkElement)window.FindName("HomeHost")).Visibility);
            var workspace=typeof(AudioWindow).GetField("workspace",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window) as WorkspaceEntry;
            Assert.Equal(latestPath,workspace?.IndexPath);
            var state=Pump(new LibraryStore(Path.Combine(root,"library")).ReadAsync());
            Assert.DoesNotContain(state.Workspaces,entry=>entry.IndexPath==firstPath);
        });
    }
    [Fact]
    public void RemovingFromCollectionRetainsGlobalBookmark()
    {
        Run((window,root)=>
        {
            var library=new LibraryStore(Path.Combine(root,"library"));
            var workspace=Pump(library.OpenWorkspaceAsync(Path.Combine(root,"source.idx"),new("index","revision")));
            var bookmark=Pump(library.BookmarkAsync(workspace.Id,"audio",new("index",1),workspace.Revision,"Clip"));
            var collection=Pump(library.SaveCollectionAsync("Selection",[bookmark.Id]));
            Pump((Task)Invoke(window,"RefreshLibraryAsync")!);
            Invoke(window,"ShowLibrary","collection",collection.Id);
            var list=(ListView)window.FindName("LibraryList");list.SelectedItem=list.Items[0];
            Invoke(window,"LibraryRemove_Click",window,new RoutedEventArgs());
            Pump(WaitForMembershipAsync(library,collection.Id));
            Assert.Contains(Pump(library.ReadAsync()).Bookmarks,item=>item.Id==bookmark.Id);
        });
    }

    [Theory]
    [InlineData("language")]
    [InlineData("codec")]
    [InlineData("channels")]
    public void MissingSavedAudioChoiceShowsAnErrorInsteadOfBroadening(string choice)
    {
        Run((window,root)=>
        {
            var session=OpenCompleted(window,root);
            var library=new LibraryStore(Path.Combine(root,"library"));
            var workspace=Pump(library.ReadAsync()).Workspaces.Single();
            var filter=Pump(library.SaveFilterAsync(workspace.Id,"audio","Saved choice",new Dictionary<string,string>{{choice,"Absent"},{"search","replacement"}}));
            Pump((Task)Invoke(window,"RefreshLibraryAsync")!);
            Invoke(window,"ShowLibrary","filters",null);
            var list=(ListView)window.FindName("LibraryList");
            list.SelectedItem=list.Items.Cast<object>().Single(row=>((SavedFilter?)row.GetType().GetProperty("Filter")!.GetValue(row))?.Id==filter.Id);
            Pump((Task)Invoke(window,"OpenLibrarySelectionAsync")!);
            Assert.Equal(Visibility.Visible,((FrameworkElement)window.FindName("LibraryHost")).Visibility);
            Assert.Contains("unavailable",((TextBlock)window.FindName("LibraryEmpty")).Text,StringComparison.OrdinalIgnoreCase);
            Assert.Same(session,window.CurrentSession);
        });
    }

    [Fact]
    public void HomeCancelsSavedItemEnrichmentWaitWithoutReturningToAudio()
    {
        Run((window,root)=>
        {
            var session=OpenCompleted(window,root);
            var bookmark=AddBookmark(window,root,session);
            var priorEnrichment=session.EnrichmentTask;
            var delayed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetEnrichment(session,delayed.Task);
            try
            {
                SelectBookmark(window,bookmark.Id);
                var navigation=(Task)Invoke(window,"OpenLibrarySelectionAsync")!;
                Assert.False(navigation.IsCompleted);
                Invoke(window,"Home_Click",window,new RoutedEventArgs());
                Pump(Task.WhenAny(navigation,Task.Delay(TimeSpan.FromSeconds(2))));
                Assert.True(navigation.IsCompleted,"Saved-item navigation did not respond to Home cancellation.");
                Pump(navigation);
                delayed.SetResult();Pump(Task.Delay(100));
                Assert.Equal(Visibility.Visible,((FrameworkElement)window.FindName("HomeHost")).Visibility);
            }
            finally{delayed.TrySetResult();SetEnrichment(session,priorEnrichment);}
        });
    }

    [Fact]
    public void PendingAudioQueryCannotReplaceTheExactBookmarkedRow()
    {
        Run((window,root)=>
        {
            var session=OpenCompleted(window,root);
            var bookmark=AddBookmark(window,root,session);
            var gate=(SemaphoreSlim)typeof(AudioExplorerSession).GetField("featureGate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(session)!;
            gate.Wait();
            Task query;
            try
            {
                query=(Task)Invoke(window,"QueryAsync",true)!;
                Assert.False(query.IsCompleted);
                SelectBookmark(window,bookmark.Id);
                Pump((Task)Invoke(window,"OpenLibrarySelectionAsync")!);
                Assert.Equal(bookmark.Identity,Assert.Single(window.CurrentRows).Identity);
            }
            finally{gate.Release();}
            Pump(query);Pump(Task.Delay(300));
            Assert.Equal(bookmark.Identity,Assert.Single(window.CurrentRows).Identity);
            Assert.Equal(bookmark.Identity,((AudioExplorerRow)((ListView)window.FindName("MediaList")).SelectedItem).Identity);
        });
    }

    private static AudioExplorerSession OpenCompleted(AudioWindow window,string root)
    {
        Pump(window.OpenArchiveAsync(CreateIndex(root)));
        var session=window.CurrentSession??throw new InvalidOperationException(((TextBlock)window.FindName("StatusText")).Text);
        Pump(session.EnrichmentTask);Pump(Task.Delay(100));return session;
    }
    private static LibraryBookmark AddBookmark(AudioWindow window,string root,AudioExplorerSession session)
    {
        var identity=new ResourceIdentity(session.Snapshot.IndexContentId,0,"media/1");
        var media=new MediaEntry{Identity=identity,Id=1,Name="Clip",Slice=new(session.Snapshot.Volumes[0].FilePath,0,0)};
        var entries=(Dictionary<string,MediaEntry>)typeof(AudioExplorerSession).GetField("mediaByIdentity",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(session)!;
        entries.Add(identity.Key,media);
        var library=new LibraryStore(Path.Combine(root,"library"));
        var workspace=Pump(library.ReadAsync()).Workspaces.Single();
        var bookmark=Pump(library.BookmarkAsync(workspace.Id,"audio",identity,workspace.Revision,"Clip"));
        Pump((Task)Invoke(window,"RefreshLibraryAsync")!);return bookmark;
    }
    private static void SetEnrichment(AudioExplorerSession session,Task task)
    {
        var supplied = new Lazy<Task>(() => task, LazyThreadSafetyMode.ExecutionAndPublication);
        _ = supplied.Value;
        typeof(AudioExplorerSession).GetField("enrichmentTask", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, supplied);
    }
    private static void SelectBookmark(AudioWindow window,Guid id)
    {
        Invoke(window,"ShowLibrary","bookmarks",null);var list=(ListView)window.FindName("LibraryList");
        list.SelectedItem=list.Items.Cast<object>().Single(row=>(Guid?)row.GetType().GetProperty("BookmarkId")!.GetValue(row)==id);
    }

    private static async Task WaitForMembershipAsync(LibraryStore library,Guid id)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while((await library.ReadAsync(timeout.Token)).Collections.Single(item=>item.Id==id).Bookmarks.Count!=0)
            await Task.Delay(20,timeout.Token);
    }
    private static string CreateIndex(string root,uint id=1)
    {
        root=Path.Combine(root,"source");Directory.CreateDirectory(root);
        var bytes=new byte[IndexLayout.HeaderLength+IndexLayout.RecordLength];IndexLayout.Magic.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20),18);BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(101),1);
        var fields=new uint[]{999,0,id,0,8,0,0};
        for(var i=0;i<fields.Length;i++)BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(IndexLayout.HeaderLength+i*4),fields[i]);
        var path=Path.Combine(root,"source.idx");File.WriteAllBytes(path,bytes);File.WriteAllBytes(Path.Combine(root,"source.dat"),new byte[8]);return path;
    }
    private static object? Invoke(object target,string name,params object?[] args)=>target.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(target,args);
    private static void Run(Action<AudioWindow,string> check)
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var root=Path.Combine(Path.GetTempPath(),"deviant-navigation-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);AudioWindow? window=null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                window=new(new CacheManager(Path.Combine(root,"settings"),new(Path.Combine(root,"cache"))),new AliasStore(Path.Combine(root,"library")))
                    {Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false};
                WindowTestLifetime.Observe(window);window.Show();Pump(Task.Delay(100));check(window,root);
            }
            catch(Exception error){failure=error;}
            finally
            {
                try{if(window is not null)Pump(WindowTestLifetime.CloseAsync(window));}
                catch(Exception cleanupError){failure=failure is null?cleanupError:new AggregateException(failure,cleanupError);}
                if(window is null||WindowTestLifetime.IsClosed(window))
                {try{if(Directory.Exists(root))Directory.Delete(root,true);}catch(Exception cleanupError){failure=failure is null?cleanupError:new AggregateException(failure,cleanupError);}}
                else failure=new InvalidOperationException("Window did not close; synthetic fixture retained at "+root,failure);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(20)),"Navigation check timed out.");
        if(failure is not null)throw new InvalidOperationException("Navigation check failed.",failure);
    }
    private static T Pump<T>(Task<T> task){Pump((Task)task);return task.GetAwaiter().GetResult();}
    private static void Pump(Task task)
    {
        var frame=new DispatcherFrame();var dispatcher=Dispatcher.CurrentDispatcher;
        task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();
    }
}
