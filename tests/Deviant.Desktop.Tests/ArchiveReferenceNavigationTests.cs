using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Core;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class ArchiveReferenceNavigationTests
{
    [Fact]
    public void VerifiedAudioAndLocalizationTargetsUseTheirDedicatedViews() => WorkspaceCommitTests.Run((window,fixture)=>
    {
        var indexBytes=System.IO.File.ReadAllBytes(fixture.Index);var data=System.IO.File.ReadAllBytes(System.IO.Path.ChangeExtension(fixture.Index,".dat"));
        byte[] Payload(int ordinal)
        {
            var row=IndexLayout.ReadRecord(indexBytes.AsSpan(IndexLayout.HeaderLength+ordinal*IndexLayout.RecordLength,IndexLayout.RecordLength),ordinal,"archive");
            return data.AsSpan((int)row.Offset,(int)row.StoredSize).ToArray();
        }
        DiscoveryFixture.WriteIndex(fixture.Index,[(3u,Container((1033,2),(1016,3))),(1033u,Payload(0)),(1016u,Payload(2))]);
        Pump(window.OpenArchiveAsync(fixture.Index));Pump(window.CurrentSession!.EnrichmentTask);
        var index=window.CurrentSession.Snapshot.IndexContentId;var owner=new ResourceIdentity(index,0);
        Pump(window.InspectReferencesAsync(owner));Select(window,1);Pump(window.ReferenceBrowser.OpenSelectedReferenceAsync());
        Assert.Equal(new ResourceIdentity(index,1,"wem"),((Deviant.Browser.AudioExplorerRow)((ListView)window.FindName("MediaList")).SelectedItem).Identity);
        Pump(window.InspectReferencesAsync(owner));Select(window,2);Pump(window.ReferenceBrowser.OpenSelectedReferenceAsync());
        Assert.Equal(new ResourceIdentity(index,2).Key,window.StringsBrowser.CaptureFilter()["source"]);
        Pump(window.StringsBrowser.CurrentSession!.EnrichmentTask);Pump(window.StringsBrowser.RefreshAsync());
        Assert.NotEmpty(window.StringsBrowser.CurrentRecords);Assert.All(window.StringsBrowser.CurrentRecords,row=>Assert.Equal(2,row.ResourceIdentity.Ordinal));
    });

    [Fact]
    public void VerifiedReferencesOpenTextureAndAnotherCatalogInTheCurrentWorkspace() => WorkspaceCommitTests.Run((window,fixture)=>
    {
        WriteArchive(fixture.Index);Pump(window.OpenArchiveAsync(fixture.Index));
        var index=window.CurrentSession!.Snapshot.IndexContentId;var owner=new ResourceIdentity(index,0);
        Pump(window.InspectReferencesAsync(owner));Select(window,1);Pump(window.ReferenceBrowser.OpenSelectedReferenceAsync());
        Assert.Equal(new ResourceIdentity(index,1),window.TextureBrowser.SelectedIdentity);
        Pump(window.InspectReferencesAsync(owner));Select(window,2);Pump(window.ReferenceBrowser.OpenSelectedReferenceAsync());
        Assert.Equal(new ResourceIdentity(index,2),window.ReferenceBrowser.SelectedIdentity);
        Pump(window.ReferenceBrowser.FilterArchiveReferencesAsync(""));
        Assert.Equal(new ResourceIdentity(index,1),Assert.Single(window.ReferenceBrowser.CurrentReferences).NavigationTarget);
    });

    [Fact]
    public void UnsupportedReferencedResourceOpensItsExactAdvancedRecord() => WorkspaceCommitTests.Run((window,fixture)=>
    {
        WriteArchive(fixture.Index);Pump(window.OpenArchiveAsync(fixture.Index));var index=window.CurrentSession!.Snapshot.IndexContentId;
        Pump(window.InspectReferencesAsync(new(index,0)));Select(window,3);Pump(window.ReferenceBrowser.OpenSelectedReferenceAsync());
        var windows=(IList)typeof(AudioWindow).GetField("advancedWindows",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
        Assert.Single(windows.Cast<object>());
        var item=windows[0]!;var controller=(Deviant.Browser.BrowserController)item.GetType().GetField("Item2")!.GetValue(item)!;
        Assert.Equal(3,controller.State.Focused?.Ordinal);Assert.Equal(index,controller.State.Snapshot!.IndexContentId);
        var pendingClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activities = (HashSet<Task>)typeof(Deviant.Browser.BrowserController)
            .GetField("activities", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(controller)!;
        activities.Add(pendingClose.Task);
        try
        {
            window.Close();
            Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
            Assert.False(WindowTestLifetime.IsClosed(window), "The owner closed before its advanced browser drained.");
        }
        finally { pendingClose.TrySetResult(); }
        Pump(WindowTestLifetime.WaitClosedAsync(window));
        Assert.True(controller.State.IsClosing);
        Assert.Null(typeof(Deviant.Browser.BrowserController).GetField("session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(controller));
        foreach (var catalog in System.IO.Directory.EnumerateFiles(fixture.Cache, "*.sqlite3", System.IO.SearchOption.AllDirectories))
        {
            using var exclusive = System.IO.File.Open(catalog, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None);
        }
    });

    private static void Select(AudioWindow window,long ordinal)
    {
        Pump(window.ReferenceBrowser.FilterArchiveReferencesAsync(""));
        ((DataGrid)window.ReferenceBrowser.FindName("Rows")).SelectedItem=window.ReferenceBrowser.CurrentReferences.Single(row=>row.NavigationTarget?.Ordinal==ordinal);
    }
    private static void WriteArchive(string path)=>DiscoveryFixture.WriteIndex(path,
        [(3u,Container((2229,2),(3,3),(999,4))),(2229u,new byte[]{1}),(3u,Container((2229,2))),(999u,new byte[]{2,3})]);
    private static byte[] Container(params(uint Type,uint Id)[] references)
    {
        var bytes=new byte[20+references.Length*9];"COM_CONT"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8),6);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12),(uint)bytes.Length-16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16),(uint)references.Length);
        for(var row=0;row<references.Length;row++){BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20+row*9),references[row].Type);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24+row*9),references[row].Id);}
        return bytes;
    }
    private static void Pump(Task task)
    {var frame=new DispatcherFrame();var dispatcher=Dispatcher.CurrentDispatcher;task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();}
}
