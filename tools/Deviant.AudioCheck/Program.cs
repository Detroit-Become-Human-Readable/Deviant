using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Desktop;
using NAudio.Wave;

namespace Deviant.AudioCheck;
public static partial class Program
{
    [STAThread] public static int Main(string[] args)
    {
        var options=new Dictionary<string,string>();
        for(var i=0;i<args.Length;i++) if(args[i].StartsWith("--",StringComparison.Ordinal)) options[args[i][2..]]=i+1<args.Length&&!args[i+1].StartsWith("--",StringComparison.Ordinal)?args[++i]:"true";
        if(!options.TryGetValue("out",out var output)||!options.TryGetValue("input",out var input))
        { Console.Error.WriteLine("Deviant.AudioCheck --input <archive-or-dds> --out <directory> --tools <bundle> [--theme dark|light] [--play] [--strings-only | --navigation | --images | --native-textures --texture-ordinals <csv> [--texture-catalog <ordinal>] [--unsupported-ordinal <ordinal>] | --texture-discovery --discovery-plan <json> [--stale-source-plan <json>]] [--cache <directory>] [--package <directory>]"); return 1; }
        output=Path.GetFullPath(output);
        if(options.ContainsKey("native-textures")||options.ContainsKey("texture-discovery"))
        {
            var sourceRoot=Directory.Exists(input)?Path.GetFullPath(input):Path.GetDirectoryName(Path.GetFullPath(input))!;
            Deviant.Export.OutputSafety.ValidateOutputRoot(output,sourceRoot);
            if(options.TryGetValue("cache",out var nativeCache)&&!Deviant.Core.DirectoryBoundary.IsWithin(Path.GetFullPath(nativeCache),output))throw new ArgumentException("Native texture acceptance requires a temporary cache inside --out.");
        }
        Directory.CreateDirectory(output);
        var cache=new CacheManager(Path.Combine(output,"settings"),new(options.GetValueOrDefault("cache")??Path.Combine(output,"cache"),4L*1024*1024*1024));
        var aliases=new AliasStore(Path.Combine(output,"library"));
        var tools=AudioToolPaths.Discover(options.GetValueOrDefault("tools"));
        var app=new App { ThemeOverride=options.GetValueOrDefault("theme")!="dark",ShutdownMode=ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var result=1;
        app.AudioWindowFactory=()=>
        {
            var window=new AudioWindow(cache,aliases,tools);
            if(options.TryGetValue("width",out var width))window.Width=double.Parse(width,System.Globalization.CultureInfo.InvariantCulture);
            var started=false;
            window.Loaded+=async(_,_)=>
            {
                if(started)return;started=true;
                try
                {
                    var report=options.ContainsKey("texture-discovery")?await ExerciseTextureDiscoveryAsync(window,input,output,options,cache,aliases):options.ContainsKey("native-textures")?await ExerciseNativeTexturesAsync(window,input,output,options,cache):options.ContainsKey("reference-graph")?await ExerciseReferenceGraphAsync(window,input,output,long.Parse(options.GetValueOrDefault("reference-ordinal")??"0",System.Globalization.CultureInfo.InvariantCulture)):
                        options.ContainsKey("buffer")?await ExerciseBufferAsync(window,input,output,long.Parse(options.GetValueOrDefault("buffer-ordinal")??"0",System.Globalization.CultureInfo.InvariantCulture),options.ContainsKey("buffer-standalone")):
                        options.ContainsKey("reference-links")?await ExerciseReferenceLinksAsync(window,input,output,long.Parse(options.GetValueOrDefault("reference-ordinal")??"0",System.Globalization.CultureInfo.InvariantCulture)):
                        options.ContainsKey("texture-groups")?await ExerciseTextureGroupsAsync(window,input,output,aliases):
                        options.ContainsKey("tables")?await ExerciseTablesAsync(window,input,output,long.Parse(options.GetValueOrDefault("animation-ordinal")??"0",System.Globalization.CultureInfo.InvariantCulture)):
                        options.ContainsKey("catalog-demo")?await ExerciseCatalogDemoAsync(window,input,output,long.Parse(options.GetValueOrDefault("catalog-ordinal")??throw new ArgumentException("--catalog-ordinal is required."),System.Globalization.CultureInfo.InvariantCulture)):
                        options.ContainsKey("shader-demo")?await ExerciseShaderDemoAsync(window,input,output,long.Parse(options.GetValueOrDefault("shader-ordinal")??throw new ArgumentException("--shader-ordinal is required."),System.Globalization.CultureInfo.InvariantCulture)):
                        options.ContainsKey("inspectors")?await ExerciseInspectorsAsync(window,input,output,aliases):options.ContainsKey("images")?await ExerciseImagesAsync(window,input,output):options.ContainsKey("navigation")?await ExerciseNavigationAsync(window,input,output,aliases):options.ContainsKey("strings-only")?await ExerciseStringsAsync(window,input,output,options.GetValueOrDefault("package")):await ExerciseAsync(window,input,output,options.ContainsKey("play"),cache,aliases,tools,options.GetValueOrDefault("package"),options.ContainsKey("strings"),options.ContainsKey("all-devices")?int.MaxValue:2);
                    await File.WriteAllTextAsync(Path.Combine(output,"ui-check.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions { WriteIndented=true }));
                    if(options.TryGetValue("package",out var checkedPackage))
                        await File.WriteAllTextAsync(Path.Combine(output,"package-assemblies.json"),JsonSerializer.Serialize(VerifyPackageAssemblies(checkedPackage),new JsonSerializerOptions { WriteIndented=true }));
                    var errorPath=Path.Combine(output,"error.txt");if(File.Exists(errorPath))File.Delete(errorPath);
                    result=0;
                }
                catch(Exception error) { await File.WriteAllTextAsync(Path.Combine(output,"error.txt"),error.ToString()); Console.Error.WriteLine(error); }
                finally { var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);window.Closed+=(_,_)=>closed.TrySetResult();window.Close();await closed.Task;app.Shutdown(result); }
            };
            return window;
        };
        app.Run(); return result;
    }
    private static async Task<object> ExerciseAsync(AudioWindow window,string input,string output,bool play,CacheManager cache,AliasStore aliases,AudioToolPaths tools,string? package,bool strings,int maximumDevices)
    {
        var beats=new List<double>(); var clock=Stopwatch.StartNew(); var previous=clock.Elapsed.TotalMilliseconds;
        var timer=new DispatcherTimer(DispatcherPriority.Background,window.Dispatcher) { Interval=TimeSpan.FromMilliseconds(25) };
        timer.Tick+=(_,_)=> { var now=clock.Elapsed.TotalMilliseconds; beats.Add(now-previous); previous=now; }; timer.Start();
        await window.DevicesReady;
        var deviceBox=(ComboBox)window.FindName("DeviceBox");
        var devices=deviceBox.Items.Cast<AudioOutputDevice>().Where(device=>device.Id.Length>0).ToArray();
        if(deviceBox.ItemsSource is null||deviceBox.Items.Count==0||((TextBlock)window.FindName("StatusText")).Text.Contains("Output devices unavailable"))throw new InvalidOperationException("Device dropdown failed to initialize.");
        await window.OpenArchiveAsync(input);
        Invoke(window,"ShowBrowse","audio");
        Console.WriteLine("Index opened");
        var session=window.CurrentSession ?? throw new InvalidOperationException("Archive session did not open.");
        await session.EnrichmentTask;
        Console.WriteLine("Metadata ready");
        await Task.Delay(600);
        if(window.CurrentRows.Count==0) throw new InvalidOperationException("The All banks view is empty.");
        var suitable=await window.QueryAudioAsync(new AudioExplorerQuery(MinimumDuration:1,MaximumDuration:120,Limit:10_000));
        var codec=suitable.Rows.Where(row=>row.CanPreview).GroupBy(row=>row.Descriptor.Codec)
            .FirstOrDefault(group=>group.Count()>=2&&group.Any(row=>row.Descriptor.Completeness==AssetCompleteness.Unverified))?.Key
            ?? throw new InvalidOperationException("The sample requires two playable clips of one codec, including an Unverified clip.");
        ((TextBox)window.FindName("SearchBox")).Text="";
        ((ComboBox)window.FindName("CodecBox")).SelectedItem=codec;
        ((TextBox)window.FindName("MinimumDurationBox")).Text="1";
        ((TextBox)window.FindName("MaximumDurationBox")).Text="120";
        var settled=Stopwatch.StartNew();
        while(settled.Elapsed<TimeSpan.FromSeconds(10) && !window.CurrentRows.Any(r=>r.CanPreview&&r.Descriptor.Codec==codec)) await Task.Delay(100);
        await Task.Delay(600);
        var page=await window.QueryAudioAsync(new AudioExplorerQuery(Codec:codec,MinimumDuration:1,MaximumDuration:120,Limit:100));
        await File.WriteAllTextAsync(Path.Combine(output,"selection-debug.json"),JsonSerializer.Serialize(new { page.Total, Rows=page.Rows.Take(10).Select(r=>new {r.Label,r.CanPreview,r.CanConvert,r.Descriptor.Capabilities,r.Media.State,r.Media.Completeness,r.Media.ContainerValidity,r.Media.Availability,r.Media.IsRiff}) }));
        var row=page.Rows.First(r=>r.CanPreview);
        var list=(ListView)window.FindName("MediaList"); list.SelectedItem=window.CurrentRows.First(r=>r.Identity==row.Identity);
        window.UpdateLayout(); await Task.Delay(150);
        Capture(window,Path.Combine(output,"audio.png"));
        var alias=(TextBox)window.FindName("AliasBox");alias.Text="Session draft";
        var second=window.CurrentRows.First(r=>r.CanPreview&&r.Identity!=row.Identity);list.SelectedItem=second;list.SelectedItem=window.CurrentRows.First(r=>r.Identity==row.Identity);
        if(alias.Text!="Session draft")throw new InvalidOperationException("Alias draft was lost when changing selection.");
        string? badge=null;
        var deviceReports=new List<object>();
        if(play)
        {
            var player=window.PlaybackPlayer;
            ((Slider)window.FindName("VolumeSlider")).Value=.02;
            ((Button)window.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(()=>window.PlayingContext?.Identity==row.Identity,"Play button did not start selected audio.");
            Console.WriteLine("UI preview prepared");
            await Task.Delay(150);
            if(player.State!=PlaybackState.Playing) throw new InvalidOperationException("The UI Play action did not start audio.");
            var startPosition=player.Position; await Task.Delay(700);
            if(player.Position<=startPosition+.3) throw new InvalidOperationException("Playback did not progress with the UI timer active.");
            badge=((TextBlock)window.FindName("ActionBadge")).Text;
            if(badge!=window.PlayingContext?.Badge)throw new InvalidOperationException("Playback badge differs from playing context.");
            foreach(var device in devices.Take(maximumDevices))
            {
                deviceBox.SelectedItem=device;await WaitAsync(()=>player.OutputDeviceId==device.Id&&player.State==PlaybackState.Playing,"Device dropdown did not switch playback.");
                var started=player.State==PlaybackState.Playing;
                await Task.Run(()=>player.Position=Math.Min(player.Length*.25,.1)); await Task.Delay(50);
                await Task.Run(player.Toggle); var paused=player.State==PlaybackState.Paused;
                await Task.Run(player.Toggle); var resumed=player.State==PlaybackState.Playing;
                deviceReports.Add(new { device.Name, Started=started, Paused=paused, Resumed=resumed,player.Length,player.Position });
                await File.WriteAllTextAsync(Path.Combine(output,"device-debug.json"),JsonSerializer.Serialize(deviceReports));
                if(!started||!paused||!resumed) throw new InvalidOperationException("Device playback lifecycle failed.");
            }
            list.SelectedItem=second;
            if(window.PlayingContext?.Identity!=row.Identity||((TextBlock)window.FindName("ActionBadge")).Text!=badge)throw new InvalidOperationException("Selection changed the playing badge.");
            list.Focus();RaiseKey(list,Key.Space);await WaitAsync(()=>window.PlayingContext?.Identity==second.Identity&&player.State==PlaybackState.Playing,"Space did not audition B.");
            RaiseKey(list,Key.Space);await WaitAsync(()=>player.State==PlaybackState.Paused,"Space did not pause B.");
            RaiseKey(list,Key.Space);await WaitAsync(()=>player.State==PlaybackState.Playing,"Space did not resume B.");
            foreach(var editor in new[]{alias,(TextBox)window.FindName("SearchBox")}){editor.Focus();if(RaiseKey(editor,Key.Left)||RaiseKey(editor,Key.Right)||RaiseKey(editor,Key.Space))throw new InvalidOperationException("Player shortcuts intercepted text editing.");}
            await window.StopPreviewAudioAsync(); await Task.Delay(150);
            if(devices.Length==0) throw new InvalidOperationException("No audio output device is available.");
        }
        var singleWindowWorkingSet=Process.GetCurrentProcess().WorkingSet64;
        var unverifiedPage=await window.QueryAudioAsync(new AudioExplorerQuery(Codec:codec,Completeness:Deviant.Formats.Audio.Model.MediaCompleteness.Unverified,Limit:100));
        var unverified=unverifiedPage.Rows.First(r=>r.CanPreview);
        list.ItemsSource=unverifiedPage.Rows; list.SelectedItem=unverified; window.UpdateLayout();
        await window.StartPreviewAudioAsync(unverified);
        if(((TextBlock)window.FindName("ActionBadge")).Text!="Completeness unverified" || ((TextBlock)window.FindName("SelectionBadge")).Text!="Completeness unverified") throw new InvalidOperationException("Unverified playback/export badges are missing.");
        Capture(window,Path.Combine(output,"unverified.png"));
        await window.StopPreviewAudioAsync();
        object? stringsReport=null;
        if(strings){await window.ShowStringsAsync();await window.StringsBrowser.CurrentSession!.EnrichmentTask;await window.StringsBrowser.RefreshAsync();if(window.StringsBrowser.CurrentRecords.Count==0)throw new InvalidOperationException("Strings view is empty.");((DataGrid)window.StringsBrowser.FindName("Rows")).SelectedItem=window.StringsBrowser.CurrentRecords.First();await Task.Delay(500);Capture(window,Path.Combine(output,"strings.png"));stringsReport=new{window.StringsBrowser.CurrentSession.RecordCount,window.StringsBrowser.CurrentSession.Languages};}
        var failed=new AudioWindow(cache,aliases,tools,new Player(new FailingBackendFactory())) { Owner=window };
        failed.Show(); await failed.OpenArchiveAsync(input); Invoke(failed,"ShowBrowse","audio"); await failed.CurrentSession!.EnrichmentTask;
        Console.WriteLine("Failure test opened");
        var failedRow=(await failed.QueryAudioAsync(new AudioExplorerQuery(Codec:codec,MinimumDuration:1,Limit:100))).Rows.First(r=>r.CanPreview);
        await failed.StartPreviewAudioAsync(failedRow);
        Console.WriteLine("Failure test returned");
        await File.WriteAllTextAsync(Path.Combine(output,"lease-debug.json"),JsonSerializer.Serialize(new {cache.ActivePreviewLeases, FailedPlayerState=failed.PlaybackPlayer.State.ToString()}));
        await failed.CurrentSession.DisposeAsync();
        if(cache.ActivePreviewLeases!=0)throw new InvalidOperationException("Failed playback retained a preview lease.");
        Console.WriteLine("Leases drained");
        failed.Close(); await Task.Delay(150);
        timer.Stop(); var sorted=beats.Order().ToArray(); var dpi=VisualTreeHelper.GetDpi(window);
        return new { window.Title, Rows=page.Rows.Count,Total=page.Total,Banks=session.Banks.Count,PlaybackBadge=badge,
            HeartbeatP95Milliseconds=sorted.Length==0?0:sorted[(int)Math.Ceiling((sorted.Length-1)*.95)],HeartbeatMaxMilliseconds=sorted.Length==0?0:sorted[^1],
            DpiX=dpi.DpiScaleX,DpiY=dpi.DpiScaleY,window.ActualWidth,window.ActualHeight,WpfRenderTier=RenderCapability.Tier>>16,
            OutputDevices=devices.Select(d=>d.Name).ToArray(),Playback=deviceReports,UnverifiedBadgesPassed=true,SpaceAuditionsSelection=play,TextEditingPassed=play,DraftsPassed=true,Strings=stringsReport,PackageAssemblies=VerifyPackageAssemblies(package),FailedPlaybackLeaseDrained=true,SingleWindowWorkingSetBytes=singleWindowWorkingSet,WorkingSetBytes=Process.GetCurrentProcess().WorkingSet64 };
    }
    private static bool RaiseKey(UIElement target,Key key){var args=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(target),Environment.TickCount,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};target.RaiseEvent(args);return args.Handled;}
    private static async Task<object> ExerciseStringsAsync(AudioWindow window,string input,string output,string? package)
    {
        await window.OpenArchiveAsync(input);await window.ShowStringsAsync();var view=window.StringsBrowser;
        await view.CurrentSession!.EnrichmentTask;await view.RefreshAsync();
        await WaitAsync(()=>view.CurrentRecords.Count>0,"Strings view is empty after enrichment: "+view.CurrentSession.RecordCount+" records; "+((TextBlock)view.FindName("Status")).Text);
        var grid=(DataGrid)view.FindName("Rows");var row=view.CurrentRecords.First(record=>record.Kind==Deviant.Formats.Localization.Model.LocalizationRecordKind.Text);grid.SelectedItem=row;
        await Task.Delay(600);if(((TextBox)view.FindName("FullText")).Text!=row.Text)throw new InvalidOperationException("Strings detail does not match selection.");
        var search=(TextBox)view.FindName("Search");search.Text=row.Key;await view.RefreshAsync();await WaitAsync(()=>view.CurrentRecords.Any(record=>record.Identity==row.Identity),"Strings key search lost selection.");
        var variants=await view.CurrentSession.GetVariantsAsync(row.Identity);
        await WaitAsync(()=>((ListView)view.FindName("Variants")).Items.Count==variants.Count,"Strings language occurrences did not finish: "+((TextBlock)view.FindName("Status")).Text);
        Capture(window,Path.Combine(output,"strings.png"));return new{view.CurrentSession.RecordCount,view.CurrentSession.Languages,VisibleRows=view.CurrentRecords.Count,VariantCount=variants.Count,DetailsPassed=true,SearchPassed=true,PackageAssemblies=VerifyPackageAssemblies(package)};
    }
    private static async Task WaitAsync(Func<bool> condition,string diagnostic){var timer=Stopwatch.StartNew();while(!condition()){if(timer.Elapsed>TimeSpan.FromSeconds(30))throw new InvalidOperationException(diagnostic);await Task.Delay(50);}}
    private static object[] VerifyPackageAssemblies(string? package)
    {
        if(package is null)return[];using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(package,"package-manifest.json")));
        var files=manifest.RootElement.GetProperty("files").EnumerateArray().ToDictionary(entry=>entry.GetProperty("file").GetString()!,entry=>entry.GetProperty("sha256").GetString()!,StringComparer.OrdinalIgnoreCase);
        return AppDomain.CurrentDomain.GetAssemblies().Where(assembly=>assembly.GetName().Name?.StartsWith("Deviant.",StringComparison.Ordinal)==true&&assembly!=Assembly.GetExecutingAssembly()).Select(assembly=>{var name=Path.GetFileName(assembly.Location);var hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant();if(!files.TryGetValue(name,out var expected)||hash!=expected)throw new InvalidOperationException("Loaded assembly differs from package: "+name);return(object)new{Name=name,Sha256=hash,assembly.Location};}).ToArray();
    }
    private static void Capture(FrameworkElement element,string path)
    {
        var dpi=VisualTreeHelper.GetDpi(element); var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(element.ActualHeight*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output=File.Create(path); encoder.Save(output);
    }
    private sealed class FailingBackendFactory : IPlaybackBackendFactory
    { public IPlaybackBackend Create(string path,float volume,double positionSeconds=0) => throw new AudioToolException("Synthetic output-device initialization failure."); }
}
