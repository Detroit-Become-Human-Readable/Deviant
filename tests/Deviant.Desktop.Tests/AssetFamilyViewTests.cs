using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Export;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class AssetFamilyViewTests
{
    [Fact]
    public void CanceledGroupChangesNeverLeaveAnotherGroupsRowsAvailable() => Run(root =>
    {
        var view=new AssetFamilyView();
        try
        {
            var source=Snapshot(root,"archive",[(41u,new byte[]{1}),(41u,new byte[]{2})]);
            Pump(view.LoadAsync(source,[41u],"Asset"));
            Pump(view.SetGroupsAsync([new("first","First",[new(source.IndexContentId,0)]),new("second","Second",[new(source.IndexContentId,1)])]));
            Pump(view.SelectGroupAsync("first"));
            ((DataGrid)view.FindName("Rows")).SelectedItem=view.CurrentResources[0];
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            Pump(Assert.ThrowsAnyAsync<OperationCanceledException>(()=>view.SelectGroupAsync("second",cancelled.Token)));
            Assert.Equal("first",view.CaptureFilter()["group"]);Assert.Equal(0,view.SelectedIdentity!.Ordinal);
            var gate=(SemaphoreSlim)typeof(AssetFamilyView).GetField("queryGate",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
            gate.Wait();using var pendingCancel=new CancellationTokenSource();
            try
            {
                var pending=view.SelectGroupAsync("second",pendingCancel.Token);
                Assert.Empty(view.CurrentResources);Assert.Null(view.SelectedIdentity);
                pendingCancel.Cancel();Pump(Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending));
                Assert.Equal("second",view.CaptureFilter()["group"]);Assert.Empty(view.CurrentResources);
                Assert.False(((Button)view.FindName("ExportOriginal")).IsEnabled);
            }
            finally{gate.Release();}
            Pump(view.SelectGroupAsync("second"));Assert.Equal(1,Assert.Single(view.CurrentResources).Identity.Ordinal);
        }
        finally{Pump(view.DisposeAsync().AsTask());}
    });

    [Fact]
    public void MetadataBrowsingSearchSortAndPagingWorkAcrossTheWholeResultWithoutSourceReads() => Run(root =>
    {
        var source = Snapshot(root, "archive", Enumerable.Range(0, 205).Select(index => ((uint)(index % 2 == 0 ? 41 : 42), Enumerable.Repeat((byte)index, index + 1).ToArray())).Append((77u, new byte[1])).ToArray());
        var view = new AssetFamilyView();
        try
        {
            using var heldVolume = File.Open(source.Volumes[0].FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var heldIndex = File.Open(source.IndexPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Pump(view.LoadAsync(source, [41u, 42u], "Asset")); Assert.Equal(205, view.TotalMatches); Assert.Equal(100, view.CurrentResources.Count);
            var sort = (ComboBox)view.FindName("Sort"); sort.SelectedIndex = 3; Pump(view.RefreshAsync());
            Assert.Equal("Asset 205", view.CurrentResources[0].Label); Assert.Equal(205, view.CurrentResources[0].Resource.StoredSize);
            ((Button)view.FindName("Next")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(100, view.CurrentResources.Count); Assert.Equal("Asset 105", view.CurrentResources[0].Label);
            ((Button)view.FindName("Next")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(5, view.CurrentResources.Count);
            ((TextBox)view.FindName("Search")).Text = "Asset 205"; Pump(view.RefreshAsync()); Assert.Equal("Asset 205", Assert.Single(view.CurrentResources).Label);
            ((TextBox)view.FindName("Search")).Text = ""; ((TextBox)view.FindName("TypeFilter")).Text = "0x2A"; Pump(view.RefreshAsync());
            Assert.Equal(102, view.TotalMatches); Assert.All(view.CurrentResources, item => Assert.Equal(42u, item.Resource.Type));
            ((TextBox)view.FindName("IdFilter")).Text = "1203"; Pump(view.RefreshAsync()); Assert.Equal(203, Assert.Single(view.CurrentResources).Identity.Ordinal);
            var identity = new ResourceIdentity(source.IndexContentId, 204); Pump(view.SelectResourceAsync(identity));
            Assert.Equal(identity, view.SelectedIdentity); Assert.Equal("Asset 205", view.SelectedLabel); Assert.Equal(5, view.CurrentResources.Count);
            Assert.Equal("Preview unavailable", ((TextBlock)view.FindName("PreviewStatus")).Text);
            Assert.Equal(Visibility.Visible, ((TextBlock)view.FindName("PreviewStatus")).Visibility);
            var advanced = ((TextBox)view.FindName("Advanced")).Text; Assert.Contains("archive.dat", advanced, StringComparison.Ordinal); Assert.Contains("Type 41", advanced, StringComparison.Ordinal);
            Assert.Contains("No decoder is available.", advanced, StringComparison.Ordinal);
            Assert.True(((Button)view.FindName("ExportOriginal")).IsEnabled);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void CanceledAndSupersededLoadsQueriesAndClearCannotRestoreStaleRows() => Run(root =>
    {
        var view = new AssetFamilyView();
        try
        {
            var first = Snapshot(root, "first", Enumerable.Range(0, 300).Select(_ => (41u, new byte[1])).ToArray());
            var second = Snapshot(root, "second", [(41u, new byte[2])]);
            var oldLoad = view.LoadAsync(first, [41u], "Asset"); var currentLoad = view.LoadAsync(second, [41u], "Asset"); Pump(Task.WhenAll(oldLoad, currentLoad));
            Assert.Equal(second.IndexContentId, Assert.Single(view.CurrentResources).Identity.IndexContentId);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Pump(view.LoadAsync(first, [41u], "Asset", cancellation.Token)); Assert.Empty(view.CurrentResources); Assert.Null(view.LoadedRevision);
            Pump(view.LoadAsync(first, [41u], "Asset"));
            ((TextBox)view.FindName("Search")).Text = "Asset 205"; var oldQuery = view.RefreshAsync();
            ((TextBox)view.FindName("Search")).Text = "Asset 300"; var currentQuery = view.RefreshAsync(); Pump(Task.WhenAll(oldQuery, currentQuery));
            Assert.Equal("Asset 300", Assert.Single(view.CurrentResources).Label);
            var pending = view.LoadAsync(first, [41u], "Asset"); Pump(view.ClearAsync()); Pump(pending);
            Assert.Null(view.LoadedRevision); Assert.Null(view.SelectedIdentity); Assert.Empty(view.CurrentResources);
            Pump(view.LoadAsync(second, [41u], "Asset")); Assert.Single(view.CurrentResources);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void OriginalExportStreamsLargeResourcesToTheCallerFilenameAndRecordsPortableMetadata() => Run(root =>
    {
        var view = new AssetFamilyView();
        try
        {
            var bytes = new byte[18 * 1024 * 1024]; for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index % 251);
            var source = Snapshot(root, "archive", [(41u, bytes)]); Pump(view.LoadAsync(source, [41u], "Asset")); Pump(view.SelectResourceAsync(new(source.IndexContentId, 0)));
            var destination = Path.Combine(root, "output", "chosen-name.bin"); var report = Pump(view.ExportToAsync(destination));
            var entry = Assert.Single(report.Manifest.Entries); Assert.Equal(ExportStatus.Completed, entry.Status); Assert.Equal("chosen-name.bin", entry.RelativeOutput);
            Assert.Equal(bytes.LongLength, new FileInfo(destination).Length); Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), entry.Sha256);
            Assert.Equal(bytes, File.ReadAllBytes(destination)); Assert.Equal("archive.dat", entry.SourceVolume); Assert.Equal(0, entry.Offset);
            Assert.Equal(source.IndexContentId, report.Manifest.IndexContentId); Assert.Equal(source.PhysicalSourceStamp, report.Manifest.PhysicalSourceStamp);
            Assert.True(File.Exists(report.ManifestPath)); Assert.DoesNotContain(root, File.ReadAllText(report.ManifestPath), StringComparison.OrdinalIgnoreCase);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void ChangedSourcesAndCanceledExportPreservePreviousDestination() => Run(root =>
    {
        var view = new AssetFamilyView();
        try
        {
            var source = Snapshot(root, "archive", [(41u, new byte[] { 1, 2, 3, 4 })]); Pump(view.LoadAsync(source, [41u], "Asset")); Pump(view.SelectResourceAsync(new(source.IndexContentId, 0)));
            var directory = Path.Combine(root, "output"); Directory.CreateDirectory(directory); var destination = Path.Combine(directory, "chosen.bin"); byte[] previous = [9, 8, 7]; File.WriteAllBytes(destination, previous);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Pump(Assert.ThrowsAnyAsync<OperationCanceledException>(() => view.ExportToAsync(destination, true, cancelled.Token))); Assert.Equal(previous, File.ReadAllBytes(destination));
            File.WriteAllBytes(source.Volumes[0].FilePath, new byte[] { 4, 3, 2, 1, 0 });
            var report = Pump(view.ExportToAsync(destination, true)); var entry = Assert.Single(report.Manifest.Entries);
            Assert.Equal(ExportStatus.Failed, entry.Status); Assert.Equal("SourceChanged", entry.ErrorCode); Assert.Equal(previous, File.ReadAllBytes(destination)); Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            var unsafePath = Path.Combine(source.SourceRoot, "chosen.bin"); var error = Pump(Assert.ThrowsAsync<ArchiveException>(() => view.ExportToAsync(unsafePath)));
            Assert.Equal("UnsafeOutput", error.Code); Assert.False(File.Exists(unsafePath));
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void ManifestWriteFailureRetainsCommittedOriginalAndItsReport() => Run(root =>
    {
        var view = new AssetFamilyView(new RawExportService(manifestWriter: new FailingManifestWriter())) { PreviewUnavailableReason = "Image dimensions and pixel format are unavailable." };
        try
        {
            byte[] bytes = [1, 2, 3, 4]; var source = Snapshot(root, "archive", [(41u, bytes)]);
            Pump(view.LoadAsync(source, [41u], "Asset")); Pump(view.SelectResourceAsync(new(source.IndexContentId, 0)));
            Assert.Contains(view.PreviewUnavailableReason, ((TextBox)view.FindName("Advanced")).Text, StringComparison.Ordinal);
            var destination = Path.Combine(root, "output", "chosen.bin");
            var error = Pump(Assert.ThrowsAsync<ManifestWriteException>(() => view.ExportToAsync(destination)));
            Assert.Equal(bytes, File.ReadAllBytes(destination)); Assert.Equal(ExportStatus.Completed, Assert.Single(error.Manifest.Entries).Status);
            Assert.False(File.Exists(error.ManifestPath)); Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.tmp"));
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });
    private sealed class FailingManifestWriter : IExportManifestWriter
    {
        public Task WriteAsync(string path, ExportManifest manifest) => Task.FromException(new IOException("Report write failed."));
    }

    private static ArchiveSnapshot Snapshot(string root, string name, (uint Type, byte[] Bytes)[] payloads)
    {
        var source = Path.Combine(root, name); Directory.CreateDirectory(source); var indexPath = Path.Combine(source, "archive.idx"); var volumePath = Path.Combine(source, "archive.dat");
        var index = new byte[IndexLayout.HeaderLength + payloads.Length * IndexLayout.RecordLength]; IndexLayout.Magic.CopyTo(index);
        BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(20), IndexLayout.SupportedVersion); BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(101), checked((uint)payloads.Length));
        using var volume = new MemoryStream(); var rows = new List<ResourceRecord>();
        for (var ordinal = 0; ordinal < payloads.Length; ordinal++)
        {
            var item = payloads[ordinal]; uint[] fields = [item.Type, 0, checked((uint)ordinal + 1000), checked((uint)volume.Length), checked((uint)item.Bytes.Length), 0, 0];
            for (var field = 0; field < fields.Length; field++) BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(IndexLayout.HeaderLength + ordinal * IndexLayout.RecordLength + field * 4), fields[field]);
            rows.Add(IndexLayout.ReadRecord(index.AsSpan(IndexLayout.HeaderLength + ordinal * IndexLayout.RecordLength, IndexLayout.RecordLength), ordinal, "archive")); volume.Write(item.Bytes);
        }
        File.WriteAllBytes(indexPath, index); File.WriteAllBytes(volumePath, volume.ToArray()); var revisions = new FileRevisionProvider(); VolumeSource[] volumes = [new(0, "archive.dat", volumePath, revisions.Capture(volumePath))];
        return new(indexPath, Convert.ToHexString(SHA256.HashData(index)).ToLowerInvariant(), IndexLayout.SupportedVersion, 0, new byte[77], checked((uint)rows.Count), rows, volumes, revisions.Capture(indexPath), SourceIdentity.PhysicalStamp(volumes), []);
    }
    private static void Run(Action<string> check)
    {
        Exception? failure = null; var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "deviant-family-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); check(root); }
            catch (Exception error) { failure = error; }
            finally { Directory.Delete(root, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Asset family check timed out.");
        if (failure is not null) throw new InvalidOperationException("Asset family check failed.", failure);
    }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
}
