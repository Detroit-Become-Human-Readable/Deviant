using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Formats.Audio.Model;
using NAudio.Wave;
using Xunit;
namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class AudioPresentationTests
{
    [Fact] public void AliasDraftsStayPerIdentityUntilExplicitSave()
    {
        var drafts=new AliasDraftStore(); var a=new ResourceIdentity("index",1); var b=new ResourceIdentity("index",2);
        Assert.Equal("Saved",drafts.Read(a,"Saved")); drafts.Edit(a,"Draft");
        Assert.Equal("B",drafts.Read(b,"B")); Assert.Equal("Draft",drafts.Read(a,"Background saved"));
        drafts.Saved(a,"New"); Assert.Equal("New",drafts.Read(a,"New")); drafts.Clear(); Assert.Equal("Saved",drafts.Read(a,"Saved"));
    }
    [Fact] public void ActualControlsKeepPlayingBadgeDraftAndTextCaretAndReportDeviceFailure()
    {
        Exception? error=null;
        var thread=new Thread(()=>
        {
            var root=Path.Combine(Path.GetTempPath(),"deviant-ui-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);AudioWindow? window=null;
            try
            {
                var backend=new Backend(); var player=new Player(new Factory(backend));
                window=new AudioWindow(new CacheManager(Path.Combine(root,"settings"),new(Path.Combine(root,"cache"))),new AliasStore(Path.Combine(root,"library")),
                    playbackPlayer:player,outputDeviceProvider:new Devices()) {Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false};
                WindowTestLifetime.Observe(window);window.Show(); Pump(window.DevicesReady);
                var device=(ComboBox)window.FindName("DeviceBox"); Assert.Equal(3,device.Items.Count); Assert.DoesNotContain("Items collection",((TextBlock)window.FindName("StatusText")).Text);
                Invoke(window,"ShowBrowse","audio");
                var a=Row(1,"A",MediaCompleteness.Unverified); var b=Row(2,"B",MediaCompleteness.Complete);
                var list=(ListView)window.FindName("MediaList"); list.ItemsSource=new[]{a,b}; list.SelectedItem=a;
                var alias=(TextBox)window.FindName("AliasBox"); alias.Text="unsaved draft"; Invoke(window,"UpdateSelectedDetails"); Assert.Equal("unsaved draft",alias.Text);
                list.SelectedItem=b; list.SelectedItem=a; Assert.Equal("unsaved draft",alias.Text);
                Pump(player.LoadAsync("synthetic-A.wav",.1f));
                typeof(AudioWindow).GetField("playing",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,new PlaybackContext(a.Identity,a.Label,a.Media.Completeness,1));
                Invoke(window,"UpdatePlaybackPresentation"); list.SelectedItem=b;
                Assert.Empty(((TextBlock)window.FindName("ActionBadge")).Text); Assert.Equal("A",((TextBlock)window.FindName("PlayingLabel")).Text);
                alias.Focus(); var source=PresentationSource.FromVisual(window)!;
                var key=new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Left) {RoutedEvent=Keyboard.PreviewKeyDownEvent}; alias.RaiseEvent(key);
                Assert.False(key.Handled);
                backend.Fail(new InvalidOperationException("Synthetic device loss"));
                Pump(Task.Delay(150)); Assert.Contains("Synthetic device loss",((TextBlock)window.FindName("StatusText")).Text); Assert.Null(window.PlayingContext);
                Pump(WindowTestLifetime.CloseAsync(window));
            }
            catch(Exception e){error=e;}
            finally
            {
                try{if(window is not null)Pump(WindowTestLifetime.CloseAsync(window));}
                catch(Exception cleanupError){error=error is null?cleanupError:new AggregateException(error,cleanupError);}
                if(window is null||WindowTestLifetime.IsClosed(window))
                {try{if(Directory.Exists(root))Directory.Delete(root,true);}catch(Exception cleanupError){error=error is null?cleanupError:new AggregateException(error,cleanupError);}}
                else error=new InvalidOperationException("Window did not close; synthetic fixture retained at "+root,error);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(15)),"WPF check timed out");
        if(error is not null) throw new InvalidOperationException("WPF controls check failed",error);
    }
    private static AudioExplorerRow Row(long ordinal,string label,MediaCompleteness completeness)
    {
        var id=new ResourceIdentity("index",ordinal); var media=new MediaEntry {Identity=id,Name=label,Completeness=completeness};
        return new(new(id,new("index","revision"),"audio.media",label,null,AssetAvailability.Available,
            completeness==MediaCompleteness.Complete?AssetCompleteness.Complete:AssetCompleteness.Unverified,AssetCapabilities.Preview,[],[]),media,null);
    }
    private static void Invoke(AudioWindow window,string method,params object?[] args)=>typeof(AudioWindow).GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,args);
    private static void Pump(Task task)
    {
        var frame=new DispatcherFrame(); var dispatcher=Dispatcher.CurrentDispatcher;
        task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private sealed class Devices:IAudioOutputDeviceProvider
    { public Task<IReadOnlyList<AudioOutputDevice>> EnumerateAsync(CancellationToken token=default)=>Task.FromResult<IReadOnlyList<AudioOutputDevice>>([new("one","One"),new("two","Two")]); }
    private sealed class Factory(Backend backend):IPlaybackBackendFactory
    {public IPlaybackBackend Create(string path,float volume,double positionSeconds=0)=>backend;}
    private sealed class Backend:IPlaybackBackend
    {
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public PlaybackState State {get;private set;} public double Position=>0; public double Length=>10; public float Volume {get;set;}
        public void Play()=>State=PlaybackState.Playing; public void Pause()=>State=PlaybackState.Paused; public void Stop()=>State=PlaybackState.Stopped; public void Dispose()=>Stop();
        public void Fail(Exception error){State=PlaybackState.Stopped; PlaybackStopped?.Invoke(this,new StoppedEventArgs(error));}
    }
}
