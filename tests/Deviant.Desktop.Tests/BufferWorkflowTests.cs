using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Catalog;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class BufferWorkflowTests
{
    [Fact]
    public void ExplicitInspectionUsesDecodedSectionsAndExportsTheDisplayedMetadata() => WorkspaceCommitTests.Run((window,fixture)=>
    {
        var bytes=Stored(Enumerable.Range(0,9000).Select(index=>(byte)(index%251)).ToArray());
        DiscoveryFixture.WriteIndex(fixture.Index,[(2229u,bytes)]);Pump(window.OpenArchiveAsync(fixture.Index));
        var identity=new ResourceIdentity(window.CurrentSession!.Snapshot.IndexContentId,0);
        Pump(window.InspectTextureBufferAsync(identity));
        var inspection=window.BufferBrowser.CurrentInspection!;Assert.NotNull(inspection.Value);
        Assert.Equal(identity,window.BufferBrowser.SelectedIdentity);Assert.Equal("buffer",View(window));
        var decoded=Assert.Single(inspection.Value!.Sections,section=>section.Key=="decoded");Assert.Equal(9000,decoded.Length);
        Pump(window.BufferBrowser.SelectSectionAsync("decoded"));Assert.Equal(0,window.BufferBrowser.CurrentByteOffset);
        ((Button)window.BufferBrowser.FindName("Next")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(WaitAsync(()=>window.BufferBrowser.CurrentByteOffset==4096));
        var output=Path.Combine(fixture.Root,"exports","buffer.json");Pump(window.BufferBrowser.ExportToAsync(AssetExportFormat.Json,output));
        using(var json=JsonDocument.Parse(File.ReadAllText(output))){Assert.Equal("Observed ETF buffer pattern",json.RootElement.GetProperty("Value").GetProperty("BoundaryBasis").GetString());Assert.False(json.RootElement.GetProperty("Value").GetProperty("Sections")[0].TryGetProperty("Bytes",out _));}
        var original=Path.Combine(fixture.Root,"exports","original.bin");Pump(window.BufferBrowser.ExportToAsync(AssetExportFormat.Original,original));
        Assert.Equal(bytes,File.ReadAllBytes(original));Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),inspection.Value.OriginalExtent.PayloadSha256);
        ((Button)window.FindName("BookmarkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var library=new LibraryStore(fixture.Library);Pump(WaitAsync(()=>Pump(library.ReadAsync()).Bookmarks.Any(item=>item.View=="buffer"&&item.Identity==identity)));
    });

    [Fact]
    public void UnsupportedPatternAndCancelledNavigationLeaveNoStaleInspection() => WorkspaceCommitTests.Run((window,fixture)=>
    {
        DiscoveryFixture.WriteIndex(fixture.Index,[(2229u,new byte[]{1,2,3})]);Pump(window.OpenArchiveAsync(fixture.Index));
        var identity=new ResourceIdentity(window.CurrentSession!.Snapshot.IndexContentId,0);Pump(window.InspectTextureBufferAsync(identity));
        Assert.Null(window.BufferBrowser.CurrentInspection);Assert.False(((Button)window.BufferBrowser.FindName("ExportOriginal")).IsEnabled);
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        Pump(Assert.ThrowsAnyAsync<OperationCanceledException>(()=>window.InspectTextureBufferAsync(identity,cancelled.Token)));
        Assert.Null(window.BufferBrowser.CurrentInspection);
    });

    [Fact]
    public void ClearingAndClosingRejectNewInspectionsBeforeChangingNavigation() => WorkspaceCommitTests.Run((window,fixture)=>
    {
        DiscoveryFixture.WriteIndex(fixture.Index,[(2229u,Stored(new byte[]{1,2,3,4}))]);Pump(window.OpenArchiveAsync(fixture.Index));
        var identity=new ResourceIdentity(window.CurrentSession!.Snapshot.IndexContentId,0);
        var source=window.CurrentSession.Snapshot;
        var operations=Field<HashSet<Task>>(window.BufferBrowser,"operations");
        var held=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);operations.Add(held.Task);
        var drain=(Task)Invoke(window,"ClearBufferAsync")!;
        try
        {
            Assert.False(drain.IsCompleted);var navigation=Field<long>(window,"navigationGeneration");
            Pump(Assert.ThrowsAsync<InvalidOperationException>(()=>window.InspectTextureBufferAsync(identity)));
            Pump(Assert.ThrowsAsync<InvalidOperationException>(()=>(Task)Invoke(window,"InspectFromAdvancedAsync",source,source.Records[0],"buffer")!));
            Assert.Equal(navigation,Field<long>(window,"navigationGeneration"));Assert.Empty(Field<HashSet<Task>>(window,"bufferReads"));
        }
        finally{held.TrySetResult();Pump(drain);operations.Remove(held.Task);}
        Pump(window.InspectTextureBufferAsync(identity));Assert.Equal(identity,window.BufferBrowser.SelectedIdentity);
        var closing=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);operations.Add(closing.Task);window.Close();
        try
        {
            Assert.True(Field<bool>(window,"closingInProgress"));var navigation=Field<long>(window,"navigationGeneration");
            Pump(Assert.ThrowsAsync<InvalidOperationException>(()=>window.InspectTextureBufferAsync(identity)));
            Pump(Assert.ThrowsAsync<InvalidOperationException>(()=>(Task)Invoke(window,"InspectFromAdvancedAsync",source,source.Records[0],"buffer")!));
            Assert.Equal(navigation,Field<long>(window,"navigationGeneration"));Assert.Empty(Field<HashSet<Task>>(window,"bufferReads"));
        }
        finally{closing.TrySetResult();Pump(WindowTestLifetime.WaitClosedAsync(window));operations.Remove(closing.Task);}
    });

    private static byte[] Stored(byte[] decoded)
    {
        using var compressed=new MemoryStream();using(var zlib=new ZLibStream(compressed,CompressionLevel.Optimal,true))zlib.Write(decoded);
        var data=compressed.ToArray();var segs=new byte[32+data.Length];"segs"u8.CopyTo(segs);
        BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(4),1);BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(6),1);
        BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(8),(uint)decoded.Length);BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(12),(uint)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(16),(ushort)data.Length);BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(18),(ushort)decoded.Length);BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(20),1);data.CopyTo(segs,32);
        var result=new byte[32+segs.Length];"QZIP\0ETF_RAW_"u8.CopyTo(result);BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(21),(uint)segs.Length);segs.CopyTo(result,32);return result;
    }
    private static string View(AudioWindow window)=>(string)typeof(AudioWindow).GetField("currentView",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(target)!;
    private static object? Invoke(object target,string name,params object?[] args)=>target.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(target,args);
    private static async Task WaitAsync(Func<bool> condition){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!condition())await Task.Delay(20,timeout.Token);}
    private static T Pump<T>(Task<T> task){Pump((Task)task);return task.GetAwaiter().GetResult();}
    private static void Pump(Task task){var frame=new DispatcherFrame();var dispatcher=Dispatcher.CurrentDispatcher;task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();}
}
