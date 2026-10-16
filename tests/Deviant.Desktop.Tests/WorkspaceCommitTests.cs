using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Deviant.Catalog;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class WorkspaceCommitTests
{
    [Fact]
    public void ClosingAfterDrainFailureStillReleasesTheWorkspace() => Run((window, fixture) =>
    {
        Pump(window.OpenArchiveAsync(fixture.Index));
        var fault = new InvalidDataException("Generated scene drain failure.");
        var sceneDrain = typeof(AudioWindow).GetField("sceneNavigationDrain", BindingFlags.NonPublic | BindingFlags.Instance)!;
        sceneDrain.SetValue(window, Task.FromException(fault));
        var unhandled = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Dispatcher.CurrentDispatcher;
        DispatcherUnhandledExceptionEventHandler handler = (_, args) =>
        { args.Handled = true; unhandled.TrySetResult(args.Exception); };
        dispatcher.UnhandledException += handler;
        bool closedWithoutUnhandled;
        try
        {
            var closed = WindowTestLifetime.WaitClosedAsync(window);
            window.Close();
            Pump(Task.WhenAny(closed, unhandled.Task).WaitAsync(TimeSpan.FromSeconds(5)));
            closedWithoutUnhandled = WindowTestLifetime.IsClosed(window) && !unhandled.Task.IsCompleted;
            if (!WindowTestLifetime.IsClosed(window))
            {
                sceneDrain.SetValue(window, Task.CompletedTask);
                typeof(AudioWindow).GetField("closingInProgress", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, false);
                Pump(WindowTestLifetime.CloseAsync(window));
            }
        }
        finally { dispatcher.UnhandledException -= handler; }
        Assert.True(closedWithoutUnhandled, "Closing escaped to the dispatcher instead of completing cleanup.");
        Assert.Null(window.CurrentSession);
        Assert.Contains(Field<List<Exception>>(window, "closeFailures"), error => error.ToString().Contains(fault.Message, StringComparison.Ordinal));
    });

    [Fact]
    public void LaterWorkspaceWinsDelayedCommitAndHomeRemainsSelected()
    {
        Run((window,fixture)=>
        {
            var second=Path.Combine(fixture.Root,"second","archive.idx");DiscoveryFixture.WriteIndex(second,[(999u,new byte[]{1,2,3})]);
            var gate=Field<SemaphoreSlim>(window,"workspaceCommit");gate.Wait();Task first,latest;
            try{first=window.OpenArchiveAsync(fixture.Index);latest=window.OpenArchiveAsync(second);Invoke(window,"Home_Click",window,new RoutedEventArgs());Assert.False(latest.IsCompleted);}
            finally{gate.Release();}
            Pump(Task.WhenAll(first,latest));Pump(Task.Delay(100));
            Assert.Equal(Path.GetFullPath(second),window.CurrentSession!.Snapshot.IndexPath);
            Assert.Equal(Visibility.Visible,((FrameworkElement)window.FindName("HomeHost")).Visibility);
            Assert.Empty(Field<HashSet<Task>>(window,"pendingOpens"));
        });
    }

    [Fact]
    public void CacheDrainCancelsAndAwaitsAnOpenWaitingToCommit()
    {
        Run((window,fixture)=>
        {
            var gate=Field<SemaphoreSlim>(window,"workspaceCommit");gate.Wait();
            try
            {
                var open=window.OpenArchiveAsync(fixture.Index);
                var drain=(Task)Invoke(window,"DrainCacheUsersAsync",CancellationToken.None)!;
                Pump(Task.WhenAny(drain,Task.Delay(TimeSpan.FromSeconds(5))));Assert.True(drain.IsCompleted,"Cache drain did not await cancellation of its open.");Pump(drain);Pump(open);
                Assert.Null(window.CurrentSession);Assert.Empty(Field<HashSet<Task>>(window,"pendingOpens"));
            }
            finally{gate.Release();}
            var cache=Field<CacheManager>(window,"cache");var moved=Path.Combine(fixture.Root,"moved-cache");Pump(cache.RelocateAsync(moved));Assert.Equal(Path.GetFullPath(moved),cache.Location);
        });
    }

    [Fact]
    public void ClosingDrainsAnOpenWaitingToCommit()
    {
        Run((window,fixture)=>
        {
            var gate=Field<SemaphoreSlim>(window,"workspaceCommit");gate.Wait();
            try{var open=window.OpenArchiveAsync(fixture.Index);window.Close();Pump(Task.WhenAny(open,Task.Delay(TimeSpan.FromSeconds(5))));Assert.True(open.IsCompleted);Pump(open);Pump(WaitClosedAsync(window));Assert.Empty(Field<HashSet<Task>>(window,"pendingOpens"));}
            finally{gate.Release();}
        });
    }
    private static Task WaitClosedAsync(Window window)=>WindowTestLifetime.WaitClosedAsync(window);
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(target)!;
    private static object? Invoke(object target,string name,params object?[] args)=>target.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(target,args);
    internal static void Run(Action<AudioWindow,DiscoveryFixture> check)
    {
        Exception? failure=null;var thread=new Thread(()=>
        {
            var fixture=new DiscoveryFixture(2);AudioWindow? window=null;
            try{SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));window=new(new CacheManager(Path.Combine(fixture.Root,"settings"),new(fixture.Cache)),new AliasStore(fixture.Library)){Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};WindowTestLifetime.Observe(window);window.Show();Pump(Task.Delay(50));check(window,fixture);}
            catch(Exception error){failure=error;}
            finally
            {
                try{if(window is not null)Pump(WindowTestLifetime.CloseAsync(window));}
                catch(Exception error){failure=failure is null?error:new AggregateException(failure,error);}
                if(window is null||WindowTestLifetime.IsClosed(window))
                {try{fixture.Dispose();}catch(Exception error){failure=failure is null?error:new AggregateException(failure,error);}}
                else failure=new InvalidOperationException("Window did not close; synthetic fixture retained at "+fixture.Root,failure);
            }
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(20)),"Workspace check timed out.");if(failure is not null)throw new InvalidOperationException("Workspace commit check failed.",failure);
    }
    private static void Pump(Task task){var frame=new DispatcherFrame();var dispatcher=Dispatcher.CurrentDispatcher;task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();}
}
