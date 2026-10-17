using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class TextureMetadataChoiceTests
{
    [Fact]
    public void DistinctMetadataRequiresAChoiceBeforeDecodingPixels()
        => Run(true, (view, owner, cache, catalog, decoded) =>
        {
            Pump(view.SelectResourceAsync(catalog.Entry.Identity));
            Assert.Equal(TexturePreviewState.Pending, view.CurrentPreviewState);
            Assert.Equal("TextureMetadataChoiceRequired", view.PreviewFailure!.Code);
            Assert.Equal(0, decoded());
            var choices = (ComboBox)view.FindName("MetadataChoices");
            Assert.Equal(2, choices.Items.Count); Assert.Null(choices.SelectedItem);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)view.FindName("MetadataChoicePanel")).Visibility);
            choices.SelectedIndex = 0; // Explicit user choice, rather than catalog ordering selecting it.
            WaitReady(view);
            Assert.Equal(1, decoded());
            Assert.Equal(NativeTextureFixture.RgbaPixels(0), view.NativeImageBrowser.ImageBrowser.CurrentDisplaySurface!.Rgba8.ToArray());
            Assert.Equal(catalog.Entry.MetadataIdentities[0], view.NativeImageBrowser.CurrentSource!.Metadata.Identity);
        });

    [Fact]
    public void UniqueMetadataLoadsAutomaticallyAndKeepsPixelsAcrossNameRefresh()
        => Run(false, (view, owner, cache, catalog, decoded) =>
        {
            Pump(view.SelectResourceAsync(catalog.Entry.Identity)); WaitReady(view);
            var surface = view.NativeImageBrowser.ImageBrowser.CurrentDisplaySurface;
            var source = view.NativeImageBrowser.CurrentSource;
            Assert.Equal(1, decoded());
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)view.FindName("MetadataChoicePanel")).Visibility);
            catalog.Rename("Updated panel texture"); Pump(view.RefreshAsync());
            Assert.Equal("Updated panel texture", view.SelectedLabel);
            Assert.Same(source, view.NativeImageBrowser.CurrentSource);
            Assert.Same(surface, view.NativeImageBrowser.ImageBrowser.CurrentDisplaySurface);
            Assert.Equal(1, decoded());
        });

    [Fact]
    public void StaleRelationshipDetailsAreRereadWithoutOverwritingARenamedSelection()
        => Run(false, (view, owner, cache, catalog, decoded) =>
        {
            Pump(view.SelectResourceAsync(catalog.Entry.Identity)); WaitReady(view);
            var source = view.NativeImageBrowser.CurrentSource;
            var surface = view.NativeImageBrowser.ImageBrowser.CurrentDisplaySurface;
            Pump((Task)typeof(TextureExplorerView).GetMethod("DrainAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!);
            var captured = new TaskCompletionSource<TextureDiscoveryEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<TextureDiscoveryEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var detailReads = 0;
            catalog.ReadDetails = entry =>
            {
                if (Interlocked.Increment(ref detailReads) == 1) { captured.TrySetResult(entry); return release.Task; }
                return Task.FromResult<TextureDiscoveryEntry?>(entry with
                {
                    Related = [new(entry.Identity, "Refreshed related texture", TextureRelationshipKind.RelatedTexture, true, true, [])]
                });
            };
            var pending = (Task)typeof(TextureExplorerView).GetMethod("LoadSelectionRelationshipEvidenceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, [catalog, owner, catalog.Entry.Identity, Field<long>(view, "nativeVersion"), CancellationToken.None])!;
            var stale = Pump(captured.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            try
            {
                Assert.Equal("Panel texture", stale.Label);
                Assert.False(pending.IsCompleted);
                catalog.Rename("Updated panel texture"); Pump(view.RefreshAsync());
                Assert.Equal("Updated panel texture", view.SelectedLabel);
                release.TrySetResult(stale); Pump(pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(2, Volatile.Read(ref detailReads));
                Assert.Equal("Updated panel texture", view.SelectedLabel);
                Assert.Equal("Refreshed related texture", Assert.Single(view.SelectedEntry!.Related).Label);
                Assert.Single(((ListBox)view.FindName("RelatedRows")).Items.Cast<object>());
                Assert.Same(source, view.NativeImageBrowser.CurrentSource);
                Assert.Same(surface, view.NativeImageBrowser.ImageBrowser.CurrentDisplaySurface);
                Assert.Equal(1, decoded());
            }
            finally { release.TrySetResult(stale); }
        });
    [Fact]
    public void NavigationCancelsAQueuedRecordChoiceAndDrainsItsLeases()
        => Run(true, (view, owner, cache, catalog, decoded) =>
        {
            Pump(view.SelectResourceAsync(catalog.Entry.Identity));
            var gate = Field<SemaphoreSlim>(view, "nativeGate"); Assert.True(gate.Wait(0));
            Task suspension;
            try
            {
                ((ComboBox)view.FindName("MetadataChoices")).SelectedIndex = 0;
                suspension = view.SuspendAsync();
            }
            finally { gate.Release(); }
            Pump(suspension);
            Assert.True(view.IsSuspended); Assert.Null(view.NativeImageBrowser.CurrentSource);
            Assert.Equal(0, decoded()); Assert.Equal(0, cache.ActivePreviewLeases);
        });

    [Fact]
    public void ForeignCatalogContextIsRejectedBeforeSourcePixelsAreDecoded()
        => Run(false, (view, owner, cache, catalog, decoded) =>
        {
            var metadata = catalog.Entry.MetadataIdentities[0];
            catalog.Replace(catalog.Entry with { MetadataContexts = [new(metadata, [new("other-generated-index", 0)], 4, 2, "BGRA8")] });
            Pump(view.SelectResourceAsync(catalog.Entry.Identity));
            WaitSettled(view);
            Assert.Equal(TexturePreviewState.Failed, view.CurrentPreviewState);
            Assert.Equal("TextureCatalogSourceMismatch", view.PreviewFailure!.Code);
            Assert.Equal(0, decoded()); Assert.Null(view.NativeImageBrowser.CurrentSource);
        });

    [Fact]
    public void LinkedExactContextKeepsItsSelectionWithoutShowingOrdinaryChoices()
        => Run(true, (view, owner, cache, catalog, decoded) =>
        {
            Pump(view.LoadNativeSelectionAsync(owner, cache, catalog.Entry.Identity, catalog.Entry.MetadataIdentities[0],
                new(catalog.Snapshot.IndexContentId, 0), owner.MemoryBudget, "Linked panel"));
            WaitReady(view);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)view.FindName("MetadataChoicePanel")).Visibility);
            Assert.Equal(catalog.Entry.MetadataIdentities[0], view.NativeImageBrowser.CurrentSource!.Metadata.Identity);
        });

    private static void Run(bool ambiguous, Action<TextureExplorerView, NativeTextureSession, CacheManager, Catalog, Func<int>> check)
        => WorkspaceCommitTests.Run((window, fixture) =>
        {
            if (ambiguous) WriteDuplicateArchive(fixture.Index);
            else NativeTextureFixture.WriteArchive(fixture.Index);
            var snapshot = Pump(new ArchiveReader().OpenAsync(fixture.Index));
            var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
            var owner = new NativeTextureSession(catalogs); var view = new TextureExplorerView();
            var cache = new CacheManager(fixture.Library, new(fixture.Cache));
            var catalog = new Catalog(snapshot, ambiguous); int decoded = 0;
            try
            {
                Pump(catalogs.EnrichmentTask);
                if (ambiguous)
                {
                    var result = Pump(owner.ReadAsync(catalog.Entry.Identity));
                    Assert.Null(result.Source); Assert.Contains("Multiple texture records", result.Reason);
                    Assert.Equal(catalog.Entry.MetadataIdentities, result.CandidateIdentities);
                }
                Pump(view.LoadAsync(catalog, owner, cache, (source, token) =>
                { Interlocked.Increment(ref decoded); return new NativeTextureDecoder().ReadPreview(source, 1024, token).Document; }));
                check(view, owner, cache, catalog, () => Volatile.Read(ref decoded));
            }
            finally
            {
                Pump(view.DisposeAsync().AsTask()); Pump(owner.DisposeAsync().AsTask()); Pump(catalogs.DisposeAsync().AsTask());
            }
            Assert.Equal(0, cache.ActivePreviewLeases);
        });

    private sealed class Catalog : ITextureDiscoveryCatalog
    {
        public Catalog(ArchiveSnapshot snapshot, bool ambiguous)
        {
            Snapshot = snapshot;
            var source = new ResourceIdentity(snapshot.IndexContentId, 2);
            var first = new ResourceIdentity(snapshot.IndexContentId, 1, "filetext/40");
            var second = new ResourceIdentity(snapshot.IndexContentId, 1, "filetext/" + (40 + NativeTextureFixture.Create().MetadataBytes.Length + 12));
            var catalog = new ResourceIdentity(snapshot.IndexContentId, 0);
            Entry = new(source, 3, 2229, "Panel texture", TextureNameKind.Fallback,
                null, null, null, 4, 2, "BGRA8", null, [], [], [], [], [], [], "panel", "Panel texture")
            {
                MetadataIdentities = ambiguous ? [first, second] : [first], CatalogKeys = [catalog.Key],
                MetadataContexts = ambiguous ? [new(first, [catalog], 4, 2, "BGRA8"), new(second, [catalog], 4, 2, "BGRA8")] :
                    [new(first, [catalog], 4, 2, "BGRA8")]
            };
        }
        public TextureDiscoveryEntry Entry { get; private set; }
        public ArchiveSnapshot Snapshot { get; }
        public SourceRevision Revision => new(Snapshot.IndexContentId, Snapshot.PhysicalSourceStamp);
        public TextureDiscoveryFilterOptions FilterOptions => new([], [], [], []);
        public TextureNamingCoverage Coverage => new(1, 1, 0, 0, 0, 1, 0, 0, 1, 1, false, [], []);
        public event EventHandler? Updated;
        public Task<TextureDiscoveryPage> QueryAsync(TextureDiscoveryQuery query, CancellationToken token = default) => Task.FromResult(new TextureDiscoveryPage([Entry], 1, 0));
        public Func<TextureDiscoveryEntry, Task<TextureDiscoveryEntry?>>? ReadDetails { get; set; }
        public Task<TextureDiscoveryEntry?> GetDetailsAsync(ResourceIdentity identity, CancellationToken token = default)
            => ReadDetails?.Invoke(Entry) ?? Task.FromResult<TextureDiscoveryEntry?>(Entry);
        public Task SetAliasAsync(ResourceIdentity identity, string? alias, CancellationToken token = default) => Task.CompletedTask;
        public void Rename(string label) => Replace(Entry with { Label = label, SearchText = label });
        public void Replace(TextureDiscoveryEntry entry) { Entry = entry; Updated?.Invoke(this, EventArgs.Empty); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void WaitReady(TextureExplorerView view)
    {
        WaitSettled(view);
        Assert.True(view.CurrentPreviewState == TexturePreviewState.Ready, $"{view.CurrentPreviewState}: {view.PreviewFailure?.Code}: {view.PreviewFailure?.Message}");
    }
    private static void WaitSettled(TextureExplorerView view)
    {
        var task = Field<Task>(view, "selectionTask");
        Pump(task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(view.CurrentPreviewState == TexturePreviewState.Loading,
            $"{view.CurrentPreviewState}: {view.PreviewFailure?.Code}: {view.PreviewFailure?.Message}");
    }
    private static void WriteDuplicateArchive(string path)
    {
        var source = NativeTextureFixture.Create(); var metadata = source.MetadataBytes.ToArray();
        for (int reference = 0; reference < 3; reference++)
            BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(metadata.Length - 36 + reference * 12 + 4), (uint)reference + 3);
        var record = new byte[metadata.Length + 12]; metadata.CopyTo(record, 0); "FTEXCRAW"u8.CopyTo(record.AsSpan(metadata.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(metadata.Length + 8), 1);
        var second = record.ToArray(); second[12 + 16] = 1; // Distinct stored sampler data; identical dimensions, mip layout and pixels.
        var logical = new byte[40 + record.Length * 2]; "DC_INFO "u8.CopyTo(logical);
        BinaryPrimitives.WriteUInt32LittleEndian(logical.AsSpan(8), 3); BinaryPrimitives.WriteUInt32LittleEndian(logical.AsSpan(12), 8);
        "DC_DATA "u8.CopyTo(logical.AsSpan(24)); BinaryPrimitives.WriteUInt32LittleEndian(logical.AsSpan(32), 23);
        BinaryPrimitives.WriteUInt32LittleEndian(logical.AsSpan(36), (uint)(record.Length * 2));
        record.CopyTo(logical, 40); second.CopyTo(logical, 40 + record.Length);
        using var catalog = new MemoryStream();
        using (var writer = new BinaryWriter(catalog, System.Text.Encoding.UTF8, true))
        {
            writer.Write("COM_CONT"u8); writer.Write(6u); writer.Write(40u); writer.Write(4u);
            foreach (var pair in new[] { (29u, 2u), (2229u, 3u), (2230u, 4u), (2230u, 5u) })
            { writer.Write(pair.Item1); writer.Write(pair.Item2); writer.Write((byte)0); }
        }
        DiscoveryFixture.WriteIndex(path, [(3, catalog.ToArray()), (29, NativeTextureFixture.WrapRawL(logical)[32..]),
            (2229, source.Payloads[0].OriginalBytes.ToArray()), (2230, source.Payloads[1].OriginalBytes.ToArray()), (2230, source.Payloads[2].OriginalBytes.ToArray())]);
    }
    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
}
