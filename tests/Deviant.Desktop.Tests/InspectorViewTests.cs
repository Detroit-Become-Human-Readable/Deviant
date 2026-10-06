using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
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
public sealed class InspectorViewTests
{
    [Fact]
    public void AnimationPagingRestoresSelectionAndWorkspaceResetAllowsReuse() => Run(root =>
    {
        var view = new AnimationRecordView();
        try
        {
            var payload = AnimationBytes();
            var source = Snapshot(root, "first", Enumerable.Range(0, 205).Select(_ => (AnimationDataReader.ResourceType, payload)).ToArray());
            var changes = 0; view.SelectedResourceChanged += (_, _) => changes++;
            Pump(view.LoadAsync(source)); Assert.Equal(100, view.CurrentResources.Count);
            Assert.Equal(new SourceRevision(source.IndexContentId, source.PhysicalSourceStamp), view.LoadedRevision);
            var identity = new ResourceIdentity(source.IndexContentId, 204);
            Pump(view.SelectResourceAsync(identity));
            Assert.Equal(identity, view.SelectedIdentity); Assert.Equal("Animation205", view.SelectedLabel);
            Assert.Equal(5, view.CurrentResources.Count); Assert.NotNull(view.CurrentDocument?.Value);
            Assert.Equal("201\u2013205 / 205",((TextBlock)view.FindName("Page")).Text);
            Assert.Equal(3, view.CurrentDocument!.Value!.Sections.Count);
            Assert.True(changes > 0);
            Pump(view.ClearAsync()); Assert.Null(view.SelectedIdentity); Assert.Null(view.CurrentDocument); Assert.Empty(view.CurrentResources); Assert.Null(view.LoadedRevision);
            var replacement = Snapshot(root, "second", [(AnimationDataReader.ResourceType, payload)]);
            Pump(view.LoadAsync(replacement)); Pump(view.SelectResourceAsync(new(replacement.IndexContentId, 0)));
            Assert.Equal("Animation1", view.SelectedLabel); Assert.Equal(replacement.IndexContentId, view.CurrentDocument!.Revision.IndexContentId);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void LatestAnimationLoadAndCancellationCannotRestoreOldRows() => Run(root =>
    {
        var view = new AnimationRecordView();
        try
        {
            var payload = AnimationBytes();
            var first = Snapshot(root, "first", Enumerable.Range(0, 300).Select(_ => (AnimationDataReader.ResourceType, payload)).ToArray());
            var second = Snapshot(root, "second", [(AnimationDataReader.ResourceType, payload)]);
            var oldLoad = view.LoadAsync(first); var latestLoad = view.LoadAsync(second); Pump(Task.WhenAll(oldLoad, latestLoad));
            Assert.Equal(second.IndexContentId, Assert.Single(view.CurrentResources).Identity.IndexContentId);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Pump(view.LoadAsync(first, cancellation.Token)); Assert.Empty(view.CurrentResources); Assert.Null(view.CurrentDocument); Assert.Null(view.LoadedRevision);
            Pump(view.LoadAsync(second)); Pump(view.SelectResourceAsync(new(second.IndexContentId, 0)));
            Assert.NotNull(view.CurrentDocument?.Value);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void AnimationExportsRetainResourceBytesAndRejectUnsafeOrChangedSources() => Run(root =>
    {
        var view = new AnimationRecordView();
        try
        {
            var payload = AnimationBytes(); var source = Snapshot(root, "archive", [(AnimationDataReader.ResourceType, payload)]);
            Pump(view.LoadAsync(source)); Pump(view.SelectResourceAsync(new(source.IndexContentId, 0)));
            var destination = Path.Combine(root, "output", "animation.bin");
            Pump(view.ExportToAsync(AssetExportFormat.Original, destination)); Assert.Equal(payload, File.ReadAllBytes(destination));
            var jsonPath = Path.Combine(root, "output", "animation.json"); Pump(view.ExportToAsync(AssetExportFormat.Json, jsonPath));
            using (var json = JsonDocument.Parse(File.ReadAllText(jsonPath)))
            {
                Assert.Equal(payload, Convert.FromBase64String(json.RootElement.GetProperty("EncodedResource").GetString()!));
                Assert.Equal(source.PhysicalSourceStamp, json.RootElement.GetProperty("Revision").GetProperty("PhysicalSourceStamp").GetString());
                Assert.Equal(source.Volumes[0].RelativeName, json.RootElement.GetProperty("Extents")[0].GetProperty("RelativeSource").GetString());
            }
            var unsafePath = Path.Combine(source.SourceRoot, "animation.bin");
            var unsafeError = Pump(Assert.ThrowsAsync<ArchiveException>(() => view.ExportToAsync(AssetExportFormat.Original, unsafePath)));
            Assert.Equal("UnsafeOutput", unsafeError.Code); Assert.False(File.Exists(unsafePath));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Pump(Assert.ThrowsAnyAsync<OperationCanceledException>(() => view.ExportToAsync(AssetExportFormat.Json, destination, true, cancelled.Token)));
            Assert.Equal(payload, File.ReadAllBytes(destination));
            var stamp = File.GetLastWriteTimeUtc(source.Volumes[0].FilePath); var changed = payload.ToArray(); changed[^1] ^= 1;
            File.WriteAllBytes(source.Volumes[0].FilePath, changed); File.SetLastWriteTimeUtc(source.Volumes[0].FilePath, stamp);
            var changedError = Pump(Assert.ThrowsAsync<ArchiveException>(() => view.ExportToAsync(AssetExportFormat.Original, destination, true)));
            Assert.Equal("SourceChanged", changedError.Code); Assert.Equal(payload, File.ReadAllBytes(destination));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "output"), "*.tmp"));
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void ShaderSelectionSearchAndExportPreserveLineEndingsAndPartialBadge() => Run(root =>
    {
        var view = new StoredShaderView();
        try
        {
            var complete = "#version 330\r\nvoid main() { /* token */ }\r\n";
            var partial = "#version 330\nvoid other() {}\n";
            var decoded = Encoding.UTF8.GetBytes("AB" + complete + "\0" + partial);
            var source = Snapshot(root, "archive", [(42u, SegsBytes(decoded)), (AnimationDataReader.ResourceType, AnimationBytes())]);
            Pump(view.LoadAsync(source, new(source.IndexContentId, 0)));
            Assert.Equal(2, view.CurrentSources.Count); Assert.Equal(0, view.ContainerIdentity!.Ordinal);
            var completeIdentity = view.CurrentSources[0].Source.Identity;
            Pump(view.SelectResourceAsync(completeIdentity));
            Assert.Equal(complete, ((TextBox)view.FindName("Code")).Text);
            Assert.Equal(Visibility.Collapsed, ((Border)view.FindName("PartialBadge")).Visibility);
            var export = Path.Combine(root, "output", "source.glsl"); Pump(view.ExportToAsync(export));
            Assert.Equal(Encoding.UTF8.GetBytes(complete), File.ReadAllBytes(export));
            var partialIdentity = view.CurrentScan!.Sources[1].Identity;
            Pump(view.SelectResourceAsync(partialIdentity)); Assert.Equal(Visibility.Visible, ((Border)view.FindName("PartialBadge")).Visibility);
            var binary = Path.Combine(root, "output", "source.bin"); Pump(view.ExportToAsync(binary));
            Assert.Equal(Encoding.UTF8.GetBytes(partial), File.ReadAllBytes(binary));
            ((TextBox)view.FindName("Search")).Text = "token";
            Pump(WaitUntilAsync(() => view.CurrentSources.Count == 1)); Assert.Equal(completeIdentity, Assert.Single(view.CurrentSources).Source.Identity);
            Pump(view.ClearAsync()); Assert.Null(view.CurrentScan); Assert.Empty(view.CurrentSources); Assert.Equal("", ((TextBox)view.FindName("Code")).Text);
            Pump(view.LoadAsync(source, new(source.IndexContentId, 0))); Assert.Equal(2, view.CurrentSources.Count);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void ShaderReloadCancellationAndChangedSourceDoNotPublishStaleData() => Run(root =>
    {
        var view = new StoredShaderView();
        try
        {
            var first = Snapshot(root, "first", [(42u, SegsBytes("#version 330\nvoid first() {}\0"u8.ToArray()))]);
            var second = Snapshot(root, "second", [(42u, SegsBytes("#version 330\nvoid second() {}\0"u8.ToArray()))]);
            var oldLoad = view.LoadAsync(first, new(first.IndexContentId, 0)); var latestLoad = view.LoadAsync(second, new(second.IndexContentId, 0));
            Pump(Task.WhenAll(oldLoad, latestLoad)); Assert.Equal(second.IndexContentId, view.CurrentScan!.ContainerIdentity.IndexContentId);
            var identity = Assert.Single(view.CurrentScan.Sources).Identity; Pump(view.SelectResourceAsync(identity));
            var sourcePath = second.Volumes[0].FilePath; var bytes = File.ReadAllBytes(sourcePath); bytes[^1] ^= 1; File.WriteAllBytes(sourcePath, bytes);
            var output = Path.Combine(root, "output", "source.glsl");
            var error = Pump(Assert.ThrowsAsync<ArchiveException>(() => view.ExportToAsync(output)));
            Assert.Equal("SourceChanged", error.Code); Assert.False(File.Exists(output));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Pump(view.LoadAsync(first, new(first.IndexContentId, 0), cancellation.Token));
            Assert.Null(view.CurrentScan); Assert.Empty(view.CurrentSources); Assert.Null(view.SelectedIdentity);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void ShaderPreviewCrossesChunkAndUtf8BoundariesWithAllPhysicalExtents() => Run(root =>
    {
        var view = new StoredShaderView();
        try
        {
            var text = "#version 330\r\n// café\r\nvoid main() {}\r\n";
            var decoded = Encoding.UTF8.GetBytes("AB" + text + "\0");
            var split = Array.IndexOf(decoded, (byte)0xC3) + 1;
            var source = Snapshot(root, "archive", [(42u, SegsBytes(decoded[..split], decoded[split..]))]);
            Pump(view.LoadAsync(source, new(source.IndexContentId, 0)));
            var found = Assert.Single(view.CurrentScan!.Sources);
            Assert.True(found.HasNullTerminator); Assert.Equal(2, found.PhysicalExtents.Count);
            Pump(view.SelectResourceAsync(found.Identity));
            Assert.Equal(text, ((TextBox)view.FindName("Code")).Text);
            Assert.Equal(Visibility.Collapsed, ((Border)view.FindName("PartialBadge")).Visibility);
            foreach (var extent in found.PhysicalExtents)
                Assert.Contains($"{extent.Offset} + {extent.Length}", ((TextBox)view.FindName("Advanced")).Text, StringComparison.Ordinal);
            var destination = Path.Combine(root, "output", "source.glsl"); Pump(view.ExportToAsync(destination));
            Assert.Equal(Encoding.UTF8.GetBytes(text), File.ReadAllBytes(destination));
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void InspectorExportsRejectIndexMutationWithRetainedLengthAndWriteTime() => Run(root =>
    {
        var animation = new AnimationRecordView(); var shader = new StoredShaderView();
        try
        {
            var source = Snapshot(root, "archive", [(AnimationDataReader.ResourceType, AnimationBytes()), (42u, SegsBytes("#version 330\nvoid main() {}\0"u8.ToArray()))]);
            var selectedDigest = source.IndexContentId; var retainedTime = File.GetLastWriteTimeUtc(source.IndexPath); var bytes = File.ReadAllBytes(source.IndexPath);
            bytes[30] ^= 1; File.WriteAllBytes(source.IndexPath, bytes); File.SetLastWriteTimeUtc(source.IndexPath, retainedTime);
            Assert.Equal(source.IndexStamp.Length, new FileInfo(source.IndexPath).Length); Assert.Equal(retainedTime, File.GetLastWriteTimeUtc(source.IndexPath));
            // Keep the selected digest and accept the current stamp, so the check exercises hashing on every filesystem.
            source = source with { IndexStamp = new FileRevisionProvider().Capture(source.IndexPath) };
            Assert.Equal(selectedDigest, source.IndexContentId);
            Pump(animation.LoadAsync(source)); Pump(animation.SelectResourceAsync(new(source.IndexContentId, 0)));
            Pump(shader.LoadAsync(source, new(source.IndexContentId, 1))); Pump(shader.SelectResourceAsync(Assert.Single(shader.CurrentSources).Source.Identity));
            var directory = Path.Combine(root, "output"); Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, "animation.bin"); byte[] previous = [1, 2, 3]; File.WriteAllBytes(destination, previous);
            var error = Pump(Assert.ThrowsAsync<ArchiveException>(() => animation.ExportToAsync(AssetExportFormat.Original, destination, true)));
            Assert.Equal("SourceChanged", error.Code); Assert.Contains("index", error.Message, StringComparison.OrdinalIgnoreCase); Assert.Equal(previous, File.ReadAllBytes(destination));
            var shaderDestination = Path.Combine(directory, "shader.glsl");
            var shaderError = Pump(Assert.ThrowsAsync<ArchiveException>(() => shader.ExportToAsync(shaderDestination)));
            Assert.Equal("SourceChanged", shaderError.Code); Assert.Contains("index", shaderError.Message, StringComparison.OrdinalIgnoreCase); Assert.False(File.Exists(shaderDestination));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Pump(animation.DisposeAsync().AsTask()); Pump(shader.DisposeAsync().AsTask()); }
    });

    private static ArchiveSnapshot Snapshot(string root, string name, (uint Type, byte[] Bytes)[] payloads)
    {
        var source = Path.Combine(root, name); Directory.CreateDirectory(source);
        var indexPath = Path.Combine(source, "archive.idx"); var volumePath = Path.Combine(source, "archive.dat");
        var index = new byte[IndexLayout.HeaderLength + payloads.Length * IndexLayout.RecordLength]; IndexLayout.Magic.CopyTo(index);
        BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(20), IndexLayout.SupportedVersion);
        BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(101), checked((uint)payloads.Length));
        using var volume = new MemoryStream(); var rows = new List<ResourceRecord>();
        for (var ordinal = 0; ordinal < payloads.Length; ordinal++)
        {
            var item = payloads[ordinal]; var offset = checked((uint)volume.Length);
            uint[] fields = [item.Type, 0, checked((uint)ordinal + 1), offset, checked((uint)item.Bytes.Length), 0, 0];
            for (var field = 0; field < fields.Length; field++) BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(IndexLayout.HeaderLength + ordinal * IndexLayout.RecordLength + field * 4), fields[field]);
            rows.Add(IndexLayout.ReadRecord(index.AsSpan(IndexLayout.HeaderLength + ordinal * IndexLayout.RecordLength, IndexLayout.RecordLength), ordinal, "archive"));
            volume.Write(item.Bytes);
        }
        File.WriteAllBytes(indexPath, index); File.WriteAllBytes(volumePath, volume.ToArray());
        var revisions = new FileRevisionProvider(); VolumeSource[] volumes = [new(0, "archive.dat", volumePath, revisions.Capture(volumePath))];
        return new(indexPath, Convert.ToHexString(SHA256.HashData(index)).ToLowerInvariant(), IndexLayout.SupportedVersion, 0, new byte[77], checked((uint)rows.Count), rows, volumes, revisions.Capture(indexPath), SourceIdentity.PhysicalStamp(volumes), []);
    }

    private static byte[] AnimationBytes()
    {
        using var stream = new MemoryStream();
        foreach (var tag in new[] { "COM_CONT", "LOADCONT", "ANIMDATA" })
        {
            stream.Write(Encoding.ASCII.GetBytes(tag)); var body = tag == "ANIMDATA" ? new byte[40] : new byte[4];
            var words = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(words, tag == "ANIMDATA" ? 14u : 1u); BinaryPrimitives.WriteUInt32LittleEndian(words.AsSpan(4), checked((uint)body.Length));
            stream.Write(words); stream.Write(body);
        }
        return stream.ToArray();
    }

    private static byte[] SegsBytes(params byte[][] decodedChunks)
    {
        var compressedChunks = decodedChunks.Select(decoded =>
        {
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) zlib.Write(decoded);
            return compressed.ToArray();
        }).ToArray();
        using var stored = new MemoryStream(); var offsets = new List<int>();
        foreach (var compressed in compressedChunks)
        {
            if (stored.Length % 2 != 0) stored.WriteByte(0);
            offsets.Add(checked((int)stored.Length)); stored.Write(compressed);
        }
        var dataStart = (16 + decodedChunks.Length * 8 + 15) & ~15;
        var result = new byte[dataStart + stored.Length]; "segs"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), 1); BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), checked((ushort)decodedChunks.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), checked((uint)decodedChunks.Sum(item => item.Length))); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), checked((uint)stored.Length));
        for (var index = 0; index < decodedChunks.Length; index++)
        {
            var entry = result.AsSpan(16 + index * 8, 8);
            BinaryPrimitives.WriteUInt16LittleEndian(entry, checked((ushort)compressedChunks[index].Length));
            BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], checked((ushort)decodedChunks[index].Length));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], checked((uint)offsets[index]) | 1u);
        }
        stored.ToArray().CopyTo(result, dataStart); return result;
    }

    private static async Task WaitUntilAsync(Func<bool> ready)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!ready()) await Task.Delay(10, timeout.Token); }
    private static void Run(Action<string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "deviant-inspector-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); check(root); }
            catch (Exception error) { failure = error; }
            finally { Directory.Delete(root, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Inspector check timed out.");
        if (failure is not null) throw new InvalidOperationException("Inspector check failed.", failure);
    }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
}
