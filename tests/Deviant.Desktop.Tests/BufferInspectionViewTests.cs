using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Core;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class BufferInspectionViewTests
{
    [Fact]
    public void StyledPanelPagesBytesAndExportsTheExactSharedRepresentation() => Run(root =>
    {
        var fixture = Fixture(root, "archive"); var view = new BufferInspectionView();
        var window = new Window { Content = view, Width = 1280, Height = 800, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Deviant;component/ControlStyles.xaml") });
        try
        {
            window.Show(); Pump(view.LoadAsync(fixture.Snapshot, fixture.Inspection, "Buffer sample")); window.UpdateLayout();
            Assert.Same(fixture.Inspection, view.CurrentInspection); Assert.Equal("Buffer sample", ((TextBlock)view.FindName("SourceName")).Text);
            var summary = ((TextBlock)view.FindName("Summary")).Text; Assert.Contains("SEGS", summary, StringComparison.Ordinal); Assert.DoesNotContain("PatternInspector", summary, StringComparison.Ordinal);
            Assert.Equal("Header", view.SelectedSection!.Label); Assert.Contains("Profile PatternInspector", ((TextBox)view.FindName("Advanced")).Text, StringComparison.Ordinal);
            Pump(view.SelectSectionAsync("decoded")); Assert.Equal(4096, HexBytes(view).Length); Assert.Equal(fixture.Decoded[..4096], HexBytes(view));
            ((Button)view.FindName("Next")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(WaitUntilAsync(() => ((TextBlock)view.FindName("Page")).Text.StartsWith("4,097", StringComparison.Ordinal)));
            Assert.Equal(fixture.Decoded[4096..8192], HexBytes(view));
            ((Button)view.FindName("Next")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(WaitUntilAsync(() => view.CurrentByteOffset == 8192 && HexBytes(view).Length == 808)); Assert.Equal(fixture.Decoded[8192..], HexBytes(view));
            var jsonPath = Path.Combine(root, "output", "inspection.json"); Pump(view.ExportToAsync(AssetExportFormat.Json, jsonPath));
            using var json = JsonDocument.Parse(File.ReadAllText(jsonPath)); var value = json.RootElement.GetProperty("Value");
            Assert.Equal("PatternInspector", value.GetProperty("Profile").GetString()); Assert.Equal(fixture.Inspection.Value!.BoundaryBasis, value.GetProperty("BoundaryBasis").GetString());
            Assert.Equal(3, value.GetProperty("Sections").GetArrayLength()); Assert.False(value.GetProperty("Sections")[2].TryGetProperty("Bytes", out _));
            Assert.Equal(9000, value.GetProperty("Sections")[2].GetProperty("Length").GetInt64()); Assert.Equal(fixture.Inspection.Value.Sections[2].Sha256, value.GetProperty("Sections")[2].GetProperty("Sha256").GetString());
            Assert.DoesNotContain(root, File.ReadAllText(jsonPath), StringComparison.OrdinalIgnoreCase);
            var originalPath = Path.Combine(root, "output", "original.bin"); Pump(view.ExportToAsync(AssetExportFormat.Original, originalPath)); Assert.Equal(fixture.Stored, File.ReadAllBytes(originalPath));
        }
        finally { Pump(view.DisposeAsync().AsTask()); window.Close(); }
    });

    [Fact]
    public void LatestLoadSectionAndCancellationCannotPublishOldInspection() => Run(root =>
    {
        var first = Fixture(root, "first"); var second = Fixture(root, "second", 11000); var view = new BufferInspectionView();
        try
        {
            var old = view.LoadAsync(first.Snapshot, first.Inspection, "First"); var latest = view.LoadAsync(second.Snapshot, second.Inspection, "Second"); Pump(Task.WhenAll(old, latest)); Assert.Same(second.Inspection, view.CurrentInspection);
            var oldSection = view.SelectSectionAsync("decoded"); var latestSection = view.SelectSectionAsync("header"); Pump(Task.WhenAll(oldSection, latestSection)); Assert.Equal("header", view.SelectedSection!.Key); Assert.Equal(second.Stored[..32], HexBytes(view));
            Pump(view.LoadAsync(first.Snapshot, first.Inspection, "First", new(true))); Assert.Null(view.CurrentInspection); Assert.Equal("", ((TextBox)view.FindName("Hex")).Text);
            Pump(view.LoadAsync(second.Snapshot, second.Inspection, "Second")); var pending = view.SelectSectionAsync("decoded"); Pump(view.ClearAsync()); Pump(pending); Assert.Null(view.SelectedIdentity); Assert.Null(view.CurrentInspection);
            Pump(view.LoadAsync(first.Snapshot, first.Inspection, "First")); Assert.Same(first.Inspection, view.CurrentInspection);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void OnlyVerifiedUniqueTypedRelationshipsCanNavigateAndStaleRowsCancel() => Run(root =>
    {
        var fixture = Fixture(root, "archive"); var view = new BufferInspectionView(); var calls = 0; view.RelatedNavigator = (_, _) => { calls++; return Task.CompletedTask; };
        try
        {
            var owner = fixture.Inspection.Identity; var target = new ResourceIdentity(owner.IndexContentId, 1);
            AssetRelationship Link(bool verified, ReferenceStatus status, IReadOnlyList<ResourceIdentity> targets) => new(owner, AssetRelationshipKind.Dependency, "Reference", new(IdentifierNamespace.Archive, "2", owner, 41), new(status, targets), new(verified, "Reference table", "Typed archive reference"));
            var links = new[] { Link(true, ReferenceStatus.Unique, [target]), Link(false, ReferenceStatus.Unique, [target]), Link(true, ReferenceStatus.Ambiguous, [target, owner]), Link(true, ReferenceStatus.Missing, []) };
            var result = fixture.Inspection with { Value = fixture.Inspection.Value! with { Relationships = links } };
            Pump(view.LoadAsync(fixture.Snapshot, result, "Buffer sample")); var related = (ListBox)view.FindName("Related"); Assert.Single(related.Items.Cast<BufferRelatedChoice>());
            related.SelectedIndex = 0; Assert.True(((Button)view.FindName("OpenRelated")).IsEnabled); Pump(view.OpenSelectedRelatedAsync()); Assert.Equal(1, calls);
            var pending = view.OpenSelectedRelatedAsync(); related.SelectedIndex = -1; try { Pump(pending); } catch (OperationCanceledException) { } Assert.Equal(1, calls);
            Assert.Contains("Ambiguous", ((TextBox)view.FindName("Advanced")).Text, StringComparison.Ordinal);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void SourceChangesUnsafeOutputsAndCanceledOverwriteRetainPriorFiles() => Run(root =>
    {
        var fixture = Fixture(root, "archive"); var view = new BufferInspectionView();
        try
        {
            Pump(view.LoadAsync(fixture.Snapshot, fixture.Inspection, "Buffer sample")); var directory = Path.Combine(root, "output"); Directory.CreateDirectory(directory); var destination = Path.Combine(directory, "inspection.json"); byte[] previous = [1, 2, 3]; File.WriteAllBytes(destination, previous);
            Pump(Assert.ThrowsAnyAsync<OperationCanceledException>(() => view.ExportToAsync(AssetExportFormat.Json, destination, true, new(true)))); Assert.Equal(previous, File.ReadAllBytes(destination));
            var unsafeError = Pump(Assert.ThrowsAsync<ArchiveException>(() => view.ExportToAsync(AssetExportFormat.Json, Path.Combine(fixture.Snapshot.SourceRoot, "inspection.json")))); Assert.Equal("UnsafeOutput", unsafeError.Code);
            var stamp = File.GetLastWriteTimeUtc(fixture.Snapshot.Volumes[0].FilePath); var changed = fixture.Stored.ToArray(); changed[^1] ^= 1; File.WriteAllBytes(fixture.Snapshot.Volumes[0].FilePath, changed); File.SetLastWriteTimeUtc(fixture.Snapshot.Volumes[0].FilePath, stamp);
            var error = Pump(Assert.ThrowsAsync<ArchiveException>(() => view.ExportToAsync(AssetExportFormat.Json, destination, true))); Assert.Equal("SourceChanged", error.Code); Assert.Equal(previous, File.ReadAllBytes(destination));
            error = Pump(Assert.ThrowsAsync<ArchiveException>(() => view.ExportToAsync(AssetExportFormat.Original, Path.Combine(directory, "original.bin")))); Assert.Equal("SourceChanged", error.Code); Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void UnusableInspectionNeverOffersEmptyFeatureActions() => Run(root =>
    {
        var fixture = Fixture(root, "archive"); var view = new BufferInspectionView();
        try
        {
            var result = fixture.Inspection with { Value = null, Diagnostics = ["Unsupported source"] }; Pump(view.LoadAsync(fixture.Snapshot, result, "Buffer sample"));
            Assert.Null(view.CurrentInspection); Assert.False(((Button)view.FindName("ExportOriginal")).IsEnabled); Assert.False(((Button)view.FindName("ExportJson")).IsEnabled); Assert.Equal("Inspection unavailable", ((TextBlock)view.FindName("Status")).Text);
            Assert.Equal(Visibility.Collapsed, ((StackPanel)view.FindName("RelatedPanel")).Visibility);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    private sealed record InspectionFixture(ArchiveSnapshot Snapshot, DecodeResult<BufferInspectionDocument> Inspection, byte[] Stored, byte[] Decoded);
    private static InspectionFixture Fixture(string root, string name, int storedLength = 10000)
    {
        var directory = Path.Combine(root, name); Directory.CreateDirectory(directory); var stored = new byte[storedLength]; for (var index = 0; index < stored.Length; index++) stored[index] = (byte)(index % 251);
        var volumeBytes = stored.Concat(new byte[] { 1 }).ToArray(); var indexBytes = new byte[IndexLayout.HeaderLength + IndexLayout.RecordLength * 2]; IndexLayout.Magic.CopyTo(indexBytes);
        BinaryPrimitives.WriteUInt32BigEndian(indexBytes.AsSpan(20), 18); BinaryPrimitives.WriteUInt32BigEndian(indexBytes.AsSpan(101), 2); var records = new List<ResourceRecord>();
        for (var index = 0; index < 2; index++)
        {
            uint[] words = [index == 0 ? 29u : 41u, 0, (uint)index + 1, index == 0 ? 0u : (uint)stored.Length, index == 0 ? (uint)stored.Length : 1u, 0, 0];
            for (var word = 0; word < words.Length; word++) BinaryPrimitives.WriteUInt32BigEndian(indexBytes.AsSpan(IndexLayout.HeaderLength + index * IndexLayout.RecordLength + word * 4), words[word]);
            records.Add(IndexLayout.ReadRecord(indexBytes.AsSpan(IndexLayout.HeaderLength + index * IndexLayout.RecordLength, IndexLayout.RecordLength), index, "archive"));
        }
        var indexPath = Path.Combine(directory, "archive.idx"); var volumePath = Path.Combine(directory, "archive.dat"); File.WriteAllBytes(indexPath, indexBytes); File.WriteAllBytes(volumePath, volumeBytes); var revisions = new FileRevisionProvider();
        VolumeSource[] volumes = [new(0, "archive.dat", volumePath, revisions.Capture(volumePath))]; var contentId = Hash(indexBytes); var snapshot = new ArchiveSnapshot(indexPath, contentId, 18, 0, [], 2, records, volumes, revisions.Capture(indexPath), SourceIdentity.PhysicalStamp(volumes), []);
        var identity = new ResourceIdentity(contentId, 0); var original = new SourceExtent("archive.dat", 0, stored.Length, Hash(stored)); var decoded = new byte[9000]; for (var index = 0; index < decoded.Length; index++) decoded[index] = (byte)(255 - index % 251);
        BufferInspectionSection[] sections = [new("header", "Header", BufferSectionKind.Header, new("archive.dat", 0, 32), null, Hash(stored[..32]), stored.AsMemory(0, 32)), new("stored", "Stored payload", BufferSectionKind.StoredPayload, new("archive.dat", 32, stored.Length - 32), null, Hash(stored[32..]), stored.AsMemory(32)), new("decoded", "Decoded buffer", BufferSectionKind.DecodedBuffer, null, new(identity, 0, decoded.Length), Hash(decoded), decoded)];
        var document = new BufferInspectionDocument("SEGS", "PatternInspector", "Observed sample pattern", original, sections, [], new Dictionary<string, string> { ["Raw word"] = "0x12345678" });
        var inspection = new DecodeResult<BufferInspectionDocument>(document, identity, new(contentId, snapshot.PhysicalSourceStamp), [original], [new("Reader", "1", "PatternInspector")], [], [], new Dictionary<string, string>());
        return new(snapshot, inspection, stored, decoded);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static byte[] HexBytes(BufferInspectionView view) => ((TextBox)view.FindName("Hex")).Text.Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(value => Convert.ToByte(value, 16)).ToArray();
    private static async Task WaitUntilAsync(Func<bool> ready) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!ready()) await Task.Delay(10, timeout.Token); }
    private static void Run(Action<string> action)
    {
        Exception? failure = null; var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "deviant-buffer-view-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); action(root); }
            catch (Exception error) { failure = error; }
            finally { Directory.Delete(root, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Buffer view check timed out."); if (failure is not null) throw new InvalidOperationException("Buffer view check failed.", failure);
    }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(Task task) { var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher; task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult(); }
}
