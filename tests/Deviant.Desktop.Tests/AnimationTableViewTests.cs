using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Animation;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class AnimationTableViewTests
{
    [Fact]
    public void TablesActionPagesOrderedRowsAndJsonUsesTheDisplayedProfileAndTail() => Run(root =>
    {
        var bytes = Fixture(205, auxiliary: true, tail: [0x91, 0x92, 0x93]); var source = Snapshot(root, bytes); var view = new AnimationRecordView();
        try
        {
            Pump(view.LoadAsync(source)); Pump(view.SelectResourceAsync(new(source.IndexContentId, 0)));
            var toggle = (CheckBox)view.FindName("TablesToggle"); Assert.True(toggle.IsEnabled); Assert.False(toggle.IsChecked);
            Assert.Equal(AnimationReadProfile.FramedRecords, view.CurrentProfile); Assert.Null(view.CurrentDocument!.Value!.Version14Layout);
            toggle.IsChecked = true;
            var destination = Path.Combine(root, "output", "animation.json"); Pump(view.ExportToAsync(AssetExportFormat.Json, destination));
            Assert.Equal(AnimationReadProfile.Version14Tables, view.CurrentProfile); Assert.Equal(2, view.CurrentDocument!.Value!.Version14Layout!.Tables.Count);
            Assert.Same(view.FindName("TableTab"), ((TabControl)view.FindName("InspectorTabs")).SelectedItem);
            var tableSelect = (ComboBox)view.FindName("TableSelect"); tableSelect.SelectedIndex = 1;
            Assert.Equal(100, view.CurrentTableRows.Count); Assert.Equal(0, view.CurrentTableRows[0].Ordinal);
            ((Button)view.FindName("TableNext")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(100, view.CurrentTableRows[0].Ordinal);
            ((Button)view.FindName("TableNext")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(5, view.CurrentTableRows.Count); Assert.Equal(200, view.CurrentTableRows[0].Ordinal);
            var rows = (DataGrid)view.FindName("TableRows"); rows.SelectedIndex = 4;
            var selected = view.SelectedTableRow!; Assert.Equal(204, selected.Ordinal); Assert.Equal(new[] { 4, 2, 2 }, selected.Fields.Select(field => field.Width));
            var advanced = ((TextBox)view.FindName("Advanced")).Text;
            Assert.Contains($"Row 205 at resource {selected.ResourceOffset}", advanced, StringComparison.Ordinal);
            Assert.Contains("Field 1: 4 bytes, row +0", advanced, StringComparison.Ordinal); Assert.Contains("0x000000D1", advanced, StringComparison.Ordinal); Assert.Contains("Remaining prefix 919293", advanced, StringComparison.Ordinal);
            using (var json = JsonDocument.Parse(File.ReadAllText(destination)))
            {
                Assert.Equal(205, json.RootElement.GetProperty("Version14Layout").GetProperty("Tables")[1].GetProperty("Rows").GetArrayLength());
                Assert.Contains("profile=Version14Tables", json.RootElement.GetProperty("Steps")[0].GetProperty("Options").GetString()!, StringComparison.Ordinal);
                Assert.Equal(bytes, Convert.FromBase64String(json.RootElement.GetProperty("EncodedResource").GetString()!));
            }
            toggle.IsChecked = false; var rawDestination = Path.Combine(root, "output", "records.json"); Pump(view.ExportToAsync(AssetExportFormat.Json, rawDestination));
            Assert.Equal(AnimationReadProfile.FramedRecords, view.CurrentProfile); Assert.Empty(view.CurrentTableRows);
            Assert.Equal(0, ((TabControl)view.FindName("InspectorTabs")).SelectedIndex);
            using var rawJson = JsonDocument.Parse(File.ReadAllText(rawDestination)); Assert.Equal(JsonValueKind.Null, rawJson.RootElement.GetProperty("Version14Layout").ValueKind);
            Assert.Equal(bytes, Convert.FromBase64String(rawJson.RootElement.GetProperty("EncodedResource").GetString()!));
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void LargeTableRapidProfileChangesAndCancellationCannotPublishOldRows() => Run(root =>
    {
        var source = Snapshot(root, Fixture(65536)); var view = new AnimationRecordView();
        try
        {
            Pump(view.LoadAsync(source)); Pump(view.SelectResourceAsync(new(source.IndexContentId, 0)));
            var toggle = (CheckBox)view.FindName("TablesToggle");
            for (var index = 0; index < 20; index++) toggle.IsChecked = index % 2 == 0;
            var final = view.SetProfileAsync(AnimationReadProfile.Version14Tables); Pump(final);
            Assert.Equal(AnimationReadProfile.Version14Tables, view.CurrentProfile); Assert.Equal(65536, view.SelectedTable!.Rows.Count); Assert.Equal(100, view.CurrentTableRows.Count);
            for (var page = 0; page < 5; page++) ((Button)view.FindName("TableNext")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(500, view.CurrentTableRows[0].Ordinal); Assert.Equal(599, view.CurrentTableRows[^1].Ordinal);
            var oldProfile = view.SetProfileAsync(AnimationReadProfile.Version14Tables); var lastProfile = view.SetProfileAsync(AnimationReadProfile.FramedRecords); Pump(Task.WhenAll(oldProfile, lastProfile));
            Assert.Equal(AnimationReadProfile.FramedRecords, view.CurrentProfile); Assert.Null(view.CurrentDocument!.Value!.Version14Layout); Assert.Empty(view.CurrentTableRows);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); Pump(view.SetProfileAsync(AnimationReadProfile.Version14Tables, cancellation.Token));
            Assert.Equal(AnimationReadProfile.FramedRecords, view.CurrentProfile); Assert.False(toggle.IsChecked); Assert.True(((Button)view.FindName("ExportJson")).IsEnabled);
            var pending = view.SetProfileAsync(AnimationReadProfile.Version14Tables); Pump(view.ClearAsync()); Pump(pending);
            Assert.Null(view.CurrentDocument); Assert.Empty(view.CurrentTableRows); Assert.False(toggle.IsEnabled);
            Pump(view.LoadAsync(source)); Pump(view.SelectResourceAsync(new(source.IndexContentId, 0))); Assert.Equal(AnimationReadProfile.FramedRecords, view.CurrentProfile);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void FailedTableLayoutRetainsRawFramingAndExportsTheSelectedProfile() => Run(root =>
    {
        var bytes = Fixture(1, declaredCount: uint.MaxValue); var source = Snapshot(root, bytes); var view = new AnimationRecordView();
        try
        {
            Pump(view.LoadAsync(source)); Pump(view.SelectResourceAsync(new(source.IndexContentId, 0))); Pump(view.SetProfileAsync(AnimationReadProfile.Version14Tables));
            Assert.NotNull(view.CurrentDocument!.Value); Assert.Null(view.CurrentDocument.Value.Version14Layout); Assert.Single(view.CurrentDocument.Diagnostics);
            Assert.Equal(AnimationReadProfile.Version14Tables, view.CurrentProfile); Assert.Equal("Tables unavailable", ((TextBlock)view.FindName("Status")).Text);
            Assert.True(((Button)view.FindName("ExportOriginal")).IsEnabled); Assert.Equal(3, ((DataGrid)view.FindName("Sections")).Items.Count);
            var original = Path.Combine(root, "output", "animation.bin"); Pump(view.ExportToAsync(AssetExportFormat.Original, original)); Assert.Equal(bytes, File.ReadAllBytes(original));
            var jsonPath = Path.Combine(root, "output", "animation.json"); Pump(view.ExportToAsync(AssetExportFormat.Json, jsonPath));
            using var json = JsonDocument.Parse(File.ReadAllText(jsonPath)); Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("Version14Layout").ValueKind); Assert.Equal(1, json.RootElement.GetProperty("Diagnostics").GetArrayLength());
            Assert.Contains("profile=Version14Tables", json.RootElement.GetProperty("Steps")[0].GetProperty("Options").GetString()!, StringComparison.Ordinal);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void TablesAreDisabledForOtherVersionsAndProfileJsonRejectsChangedSources() => Run(root =>
    {
        var bytes = Fixture(1, version: 13); var source = Snapshot(root, bytes); var view = new AnimationRecordView();
        try
        {
            Pump(view.LoadAsync(source)); Pump(view.SelectResourceAsync(new(source.IndexContentId, 0))); Assert.False(((CheckBox)view.FindName("TablesToggle")).IsEnabled);
            Assert.Throws<InvalidOperationException>(() => { _ = view.SetProfileAsync(AnimationReadProfile.Version14Tables); });
            Pump(view.ClearAsync()); bytes = Fixture(1); source = Snapshot(root, bytes); Pump(view.LoadAsync(source)); Pump(view.SelectResourceAsync(new(source.IndexContentId, 0))); Pump(view.SetProfileAsync(AnimationReadProfile.Version14Tables));
            bytes[^1] ^= 1; File.WriteAllBytes(source.Volumes[0].FilePath, bytes);
            var destination = Path.Combine(root, "output", "animation.json"); var error = Pump(Assert.ThrowsAsync<ArchiveException>(() => view.ExportToAsync(AssetExportFormat.Json, destination)));
            Assert.Equal("SourceChanged", error.Code); Assert.False(File.Exists(destination));
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    private static byte[] Fixture(int count, bool auxiliary = false, byte[]? tail = null, uint? declaredCount = null, uint version = 14)
    {
        using var body = new MemoryStream(); uint[] prefix = [2, 1, 1, auxiliary ? 0x4040u : 0x40u, 0x12345678, declaredCount ?? checked((uint)count), 0x7FC05678];
        foreach (var value in prefix) Write32(body, value);
        if (auxiliary) { Write32(body, 1); Write16(body, 1); Write16(body, 2); Write32(body, 3); Write32(body, 4); }
        for (var index = 0; index < count; index++) { Write32(body, checked((uint)index + 5)); Write16(body, (ushort)(index % 65536)); Write16(body, (ushort)((index + 1) % 65536)); }
        body.Write(tail ?? []); using var output = new MemoryStream();
        WriteRecord("COM_CONT", 6, new byte[13]); WriteRecord("LOADCONT", 2, []); WriteRecord("ANIMDATA", version, body.ToArray()); return output.ToArray();
        void WriteRecord(string tag, uint recordVersion, byte[] data) { output.Write(Encoding.ASCII.GetBytes(tag)); Write32(output, recordVersion); Write32(output, checked((uint)data.Length)); output.Write(data); }
    }
    private static void Write32(Stream stream, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); stream.Write(bytes); }
    private static void Write16(Stream stream, ushort value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, value); stream.Write(bytes); }
    private static ArchiveSnapshot Snapshot(string root, byte[] bytes)
    {
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source); var indexPath = Path.Combine(source, "archive.idx"); var volumePath = Path.Combine(source, "archive.dat");
        var index = new byte[IndexLayout.HeaderLength + IndexLayout.RecordLength]; IndexLayout.Magic.CopyTo(index); BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(20), IndexLayout.SupportedVersion); BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(101), 1);
        uint[] fields = [AnimationDataReader.ResourceType, 0, 1, 0, checked((uint)bytes.Length), 0, 0]; for (var field = 0; field < fields.Length; field++) BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(IndexLayout.HeaderLength + field * 4), fields[field]);
        File.WriteAllBytes(indexPath, index); File.WriteAllBytes(volumePath, bytes); var revisions = new FileRevisionProvider(); VolumeSource[] volumes = [new(0, "archive.dat", volumePath, revisions.Capture(volumePath))];
        return new(indexPath, Convert.ToHexString(SHA256.HashData(index)).ToLowerInvariant(), IndexLayout.SupportedVersion, 0, new byte[77], 1, [IndexLayout.ReadRecord(index.AsSpan(IndexLayout.HeaderLength), 0, "archive")], volumes, revisions.Capture(indexPath), SourceIdentity.PhysicalStamp(volumes), []);
    }
    private static void Run(Action<string> check)
    {
        Exception? failure = null; var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "deviant-animation-tables-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); check(root); }
            catch (Exception error) { failure = error; }
            finally { Directory.Delete(root, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Animation table view check timed out.");
        if (failure is not null) throw new InvalidOperationException("Animation table view check failed.", failure);
    }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
}
