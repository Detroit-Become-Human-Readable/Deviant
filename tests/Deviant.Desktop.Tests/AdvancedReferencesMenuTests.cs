using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class AdvancedReferencesMenuTests
{
    [Fact]
    public void InspectReferencesUsesTheAdvancedWindowSourceAndExportsItsExactBytes()
    {
        WorkspaceCommitTests.Run((owner,fixture)=>
        {
            var path=Path.Combine(fixture.Root,"references","archive.idx");var payload=DirectoryPayload();DiscoveryFixture.WriteIndex(path,[(29u,payload),(41u,new byte[]{1,2,3})]);WriteDependencies(path);
            Pump(owner.OpenArchiveAsync(fixture.Index));
            var controller=new BrowserController(new WpfUiDispatcher(owner.Dispatcher),fixture.Cache);var advanced=new MainWindow(controller){Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};
            try
            {
                typeof(AudioWindow).GetMethod("AttachInspectors",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(owner,[advanced]);WindowTestLifetime.Observe(advanced);advanced.Show();Pump(controller.OpenAsync(path));controller.Focus(controller.State.Snapshot!.Records[0]);Pump(Task.Delay(50));
                var inspect=Descendants(advanced).OfType<Button>().Single(button=>Equals(button.Content,"Inspect"));inspect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(Task.Delay(50));
                var menu=inspect.ContextMenu??PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source=>source.RootVisual).OfType<DependencyObject>().SelectMany(Descendants).OfType<ContextMenu>().Single(item=>item.IsOpen);var references=menu.Items.Cast<MenuItem>().Single(item=>Equals(item.Header,"References"));references.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));menu.IsOpen=false;
                Pump(WaitAsync(()=>owner.ReferenceBrowser.SelectedIdentity?.IndexContentId==controller.State.Snapshot!.IndexContentId));
                var view=owner.ReferenceBrowser;Assert.Equal(Path.GetFullPath(path),owner.CurrentSession!.Snapshot.IndexPath);Assert.Equal(2,view.CurrentDirectoryInspection!.Value!.Entries.Count);Assert.Single(view.CurrentReferences);Assert.All(view.CurrentReferences,row=>Assert.Null(row.NavigationTarget));
                var binary=Path.Combine(fixture.Root,"output","references.bin");var json=Path.Combine(fixture.Root,"output","references.json");Pump(view.ExportToAsync(AssetExportFormat.Original,binary));Pump(view.ExportToAsync(AssetExportFormat.Json,json));Assert.Equal(payload,File.ReadAllBytes(binary));
                using var document=JsonDocument.Parse(File.ReadAllBytes(json));Assert.Equal(owner.CurrentSession.Snapshot.IndexContentId,document.RootElement.GetProperty("Identity").GetProperty("IndexContentId").GetString());Assert.Equal(owner.CurrentSession.Snapshot.PhysicalSourceStamp,document.RootElement.GetProperty("Revision").GetProperty("PhysicalSourceStamp").GetString());
                Assert.False(document.RootElement.GetProperty("Dependencies").GetProperty("Value").GetProperty("References")[0].GetProperty("Relationship").GetProperty("Evidence").GetProperty("Verified").GetBoolean());
            }
            finally{Pump(controller.CloseAsync(true));Pump(WindowTestLifetime.CloseAsync(advanced));}
        });
    }
    private static byte[] DirectoryPayload(){var bytes=new byte[40];"DC_INFO "u8.CopyTo(bytes);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8),3);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12),24);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16),2);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20),2);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24),41);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28),2);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32),41);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36),2);return bytes;}
    private static void WriteDependencies(string path){using var bytes=new MemoryStream();bytes.Write(IndexLayout.Magic);Word(18);Word(2);Word(29);Word(1);Word(1);Word(1);Word(41);Word(2);Word(100);Word(0);Word(3);Word(41);Word(1);Word(2);Word(0);File.WriteAllBytes(Path.ChangeExtension(path,".dep"),bytes.ToArray());void Word(uint value){Span<byte> word=stackalloc byte[4];BinaryPrimitives.WriteUInt32BigEndian(word,value);bytes.Write(word);}}
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){yield return root;for(var index=0;index<VisualTreeHelper.GetChildrenCount(root);index++)foreach(var child in Descendants(VisualTreeHelper.GetChild(root,index)))yield return child;}
    private static async Task WaitAsync(Func<bool> ready){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!ready())await Task.Delay(20,timeout.Token);}
    private static void Pump(Task task){var frame=new DispatcherFrame();var dispatcher=Dispatcher.CurrentDispatcher;task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();}
}
