using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Export;
using Xunit;
using Microsoft.Win32.SafeHandles;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class TextureCatalogWorkflowTests
{
    [Fact]
    public void SavedCatalogKeyReadsRequestedGroupBeforeGlobalDiscoveryCompletes() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        WriteManyCatalogs(fixture.Index); Pump(window.OpenArchiveAsync(fixture.Index));
        using var held = new HeldDiscovery(window); Pump(window.ShowTexturesAsync());
        var catalogs = held.Session; Assert.False(catalogs.EnrichmentTask.IsCompleted);
        var target = new ResourceIdentity(window.CurrentSession!.Snapshot.IndexContentId, 8000);
        var library = new LibraryStore(fixture.Library); var workspace = Pump(library.ReadAsync()).Workspaces.Single();
        var saved = Pump(library.SaveFilterAsync(workspace.Id, "textures", "Requested catalog", new Dictionary<string, string> { ["group"] = target.Key }));
        Pump((Task)Invoke(window, "RefreshLibraryAsync")!); SelectSaved(window, "filters", "Filter", saved.Id);
        var pending = (Task)Invoke(window, "OpenLibrarySelectionAsync")!; Pump(Task.Delay(20)); Assert.False(pending.IsCompleted);
        held.Release(); Pump(pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(target.Key, window.TextureBrowser.CaptureFilter()["group"]);
        Assert.Equal(new[] { 8192L }, window.TextureBrowser.CurrentResources.Select(choice => choice.Identity.Ordinal));
        Assert.Contains(catalogs.Groups, group => group.CatalogIdentity == target);
        Assert.Null(window.AssetBrowser.NativeSession);
        var textureRoot = Field<object>(window, "textureNavigation");
        var children = (IEnumerable)textureRoot.GetType().GetProperty("Children")!.GetValue(textureRoot)!;
        Assert.DoesNotContain(children.Cast<object>(), item => (string?)item.GetType().GetProperty("Label")!.GetValue(item) == "Catalogs");
    });

    [Fact]
    public void AudioUpdatesCannotOverwriteTheVisibleBoundedCatalogState() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        window.TextureCatalogOptions = new(MaximumCatalogs: 2048);
        WriteManyCatalogs(fixture.Index); Pump(window.OpenArchiveAsync(fixture.Index)); Pump(window.ShowTexturesAsync());
        var catalogs = Field<TextureCatalogSession>(window, "textureCatalogs"); Pump(catalogs.EnrichmentTask);
        Assert.Equal(TextureCatalogSessionState.StoppedAtLimit, catalogs.Progress.State); Assert.True(catalogs.Progress.DiscoveryIncomplete);
        Invoke(window, "UpdateExplorerGroups"); Assert.Equal("Catalog discovery incomplete", ((TextBlock)window.FindName("ExplorerStatus")).Text);
        Invoke(window, "Home_Click", window, new RoutedEventArgs()); Invoke(window, "UpdateExplorerGroups");
        Assert.Equal("Ready", ((TextBlock)window.FindName("ExplorerStatus")).Text);
    });

    [Fact]
    public void SelectingAnotherTextureCancelsTheOriginOfPendingRelatedNavigation() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        OpenNative(window, fixture); var index = window.CurrentSession!.Snapshot.IndexContentId;
        Pump(window.TextureBrowser.SelectResourceAsync(new(index, 2)));
        Pump(WaitAsync(() => window.TextureBrowser.NativeImageBrowser.CurrentSource is not null));
        var source = window.TextureBrowser.NativeImageBrowser.CurrentSource!;
        var link = FixtureLink(source, new(index, 3), TextureRelationshipKind.RelatedTexture);
        var other = new ResourceIdentity(index, 4);
        using var held = new HeldGate(Field<SemaphoreSlim>(Field<TextureDiscoverySession>(window, "textureDiscovery"), "detailsGate"));
        var pending = window.NavigateTextureLinkAsync(link); Assert.False(pending.IsCompleted);
        ((ListBox)window.TextureBrowser.FindName("Rows")).SelectedItem = window.TextureBrowser.CurrentResources.Single(card => card.Identity == other);
        held.Release(); Pump(pending);
        Assert.Equal(other, window.TextureBrowser.SelectedIdentity); Assert.Empty(Field<IList>(window, "advancedWindows"));
    });

    [Fact]
    public void SelectedTextureOpensExactContainerMemberAndKeepsFocusDuringUpdates() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        OpenNative(window, fixture); var texture = new ResourceIdentity(window.CurrentSession!.Snapshot.IndexContentId, 2);
        Pump(window.TextureBrowser.SelectResourceAsync(texture));
        Pump(WaitAsync(() => window.TextureBrowser.NativeImageBrowser.CurrentSource?.SelectedIdentity == texture));
        var source = window.TextureBrowser.NativeImageBrowser.CurrentSource!;
        var link = FixtureLink(source, source.Metadata.Identity, TextureRelationshipKind.Container);
        Pump(window.NavigateTextureLinkAsync(link));
        var advanced = Field<IList>(window, "advancedWindows"); var item = Assert.Single(advanced.Cast<object>());
        var controller = (BrowserController)item.GetType().GetField("Item2")!.GetValue(item)!;
        var target = (MainWindow)item.GetType().GetField("Item1")!.GetValue(item)!;
        Assert.Equal(source.Metadata.Identity.Ordinal, controller.State.Focused!.Ordinal);
        target.UpdateLayout();
        Pump(WaitAsync(() => Descendants(target).OfType<TextBox>().Any(box => box.Text.Contains(source.Metadata.Identity.MemberPath!, StringComparison.Ordinal))));
        Pump((Task)Invoke(window, "RefreshTextureCatalogsAsync")!); Pump(window.TextureBrowser.RefreshAsync());
        Assert.Equal(texture, window.TextureBrowser.SelectedIdentity);
        Assert.Equal(window.TextureBrowser.SelectedLabel, ((TextBlock)window.TextureBrowser.FindName("SelectedName")).Text);
        Assert.True(((Button)window.TextureBrowser.FindName("ExportOriginal")).IsEnabled);
        target.Close(); Pump(WaitAsync(() => advanced.Count == 0));
    });

    [Fact]
    public void RapidTextureSelectionKeepsAmbiguousCatalogEvidenceInAdvanced() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        WriteCatalogArchive(fixture.Index); Pump(window.OpenArchiveAsync(fixture.Index)); Pump(window.ShowTexturesAsync());
        var catalogs = Field<TextureCatalogSession>(window, "textureCatalogs"); Pump(catalogs.EnrichmentTask);
        var index = window.CurrentSession!.Snapshot.IndexContentId;
        Pump(window.TextureBrowser.SelectResourceAsync(new(index, 2))); Pump(window.TextureBrowser.SelectResourceAsync(new(index, 3)));
        Assert.Equal(new ResourceIdentity(index, 3), window.TextureBrowser.SelectedIdentity);
        Assert.DoesNotContain(window.TextureBrowser.SelectedEntry!.Owners, link => link.Verified);
        Assert.Empty(((ListBox)window.TextureBrowser.FindName("OwnerRows")).Items.Cast<object>());
        Assert.Equal("", window.TextureBrowser.CaptureFilter()["group"]);
        var related = Pump(catalogs.FindCatalogRelationshipsForTextureAsync(new(index, 3)));
        Assert.Equal(2, related.Catalogs.Count); Assert.All(related.Catalogs.SelectMany(group => group.MatchingReferences), reference => Assert.Equal(ReferenceStatus.Ambiguous, reference.ResolutionStatus));
    });

    [Fact]
    public void AdvancedCatalogFilterBookmarkAndRestartExportExactMember() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        WriteCatalogArchive(fixture.Index); Pump(window.OpenArchiveAsync(fixture.Index)); Pump(window.ShowTexturesAsync());
        var catalogs = Field<TextureCatalogSession>(window, "textureCatalogs"); Pump(catalogs.EnrichmentTask);
        var group = catalogs.Groups.Single(item => item.CatalogIdentity.Ordinal == 0);
        Pump(window.TextureBrowser.ApplyFilterAsync(new Dictionary<string, string> { ["group"] = group.CatalogIdentity.Key }));
        Assert.Equal(new long[] { 2, 5 }, window.TextureBrowser.CurrentResources.Select(item => item.Identity.Ordinal));
        var filters = window.TextureBrowser.CaptureFilter(); var library = new LibraryStore(fixture.Library);
        var workspace = Pump(library.ReadAsync()).Workspaces.Single(); var saved = Pump(library.SaveFilterAsync(workspace.Id, "textures", "Catalog selection", filters));
        var member = window.TextureBrowser.CurrentResources.First(); ((ListBox)window.TextureBrowser.FindName("Rows")).SelectedItem = member;
        ((Button)window.FindName("BookmarkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(WaitAsync(() => Pump(library.ReadAsync()).Bookmarks.Any(item => item.Identity == member.Identity)));
        var bookmark = Pump(library.ReadAsync()).Bookmarks.Single(item => item.Identity == member.Identity);
        Pump(WindowTestLifetime.CloseAsync(window));
        var restarted = new AudioWindow(new CacheManager(Path.Combine(fixture.Root, "restart-settings"), new(fixture.Cache)), new AliasStore(fixture.Library)) { Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
        WindowTestLifetime.Observe(restarted); restarted.Show();
        try
        {
            Pump((Task)Invoke(restarted, "RefreshLibraryAsync")!); OpenSaved(restarted, "filters", "Filter", saved.Id);
            Assert.Equal(group.CatalogIdentity.Key, restarted.TextureBrowser.CaptureFilter()["group"]);
            OpenSaved(restarted, "bookmarks", "BookmarkId", bookmark.Id); Assert.Equal(member.Identity, restarted.TextureBrowser.SelectedIdentity);
            var output = Path.Combine(fixture.Root, "exports", "member.bin"); var report = Pump(restarted.ExportTextureOriginalAsync(output));
            var entry = Assert.Single(report.Manifest.Entries); Assert.Equal(ExportStatus.Completed, entry.Status); Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(output));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant(), entry.Sha256); Assert.Equal(member.Identity.IndexContentId, report.Manifest.IndexContentId);
            Assert.Null(restarted.AssetBrowser.NativeSession); Assert.Same(restarted.TextureBrowser, ((ContentControl)restarted.FindName("AssetsHost")).Content);
        }
        finally { Pump(WindowTestLifetime.CloseAsync(restarted)); }
    });

    [Fact]
    public void MissingAmbiguousAndMalformedCatalogsNeverBroadenToFirstTexture() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        WriteCatalogArchive(fixture.Index); Pump(window.OpenArchiveAsync(fixture.Index)); Pump(window.ShowTexturesAsync());
        var catalogs = Field<TextureCatalogSession>(window, "textureCatalogs"); Pump(catalogs.EnrichmentTask);
        Assert.Equal(3, catalogs.Progress.CatalogsVisited); Assert.True(catalogs.Progress.DiscoveryIncomplete); Assert.Contains(catalogs.Diagnostics, item => item.CatalogIdentity?.Ordinal == 1);
        var mixed = catalogs.Groups.Single(item => item.CatalogIdentity.Ordinal == 0); Assert.Equal(1, mixed.MissingCount); Assert.Equal(1, mixed.AmbiguousCount);
        var unresolved = catalogs.Groups.Single(item => item.CatalogIdentity.Ordinal == 6);
        Pump(window.TextureBrowser.ApplyFilterAsync(new Dictionary<string, string> { ["group"] = unresolved.CatalogIdentity.Key })); Assert.Empty(window.TextureBrowser.CurrentResources); Assert.Equal(0, window.TextureBrowser.TotalMatches);
        Pump(Assert.ThrowsAsync<InvalidOperationException>(() => window.TextureBrowser.ApplyFilterAsync(new Dictionary<string, string> { ["group"] = "missing-catalog" }))); Assert.Empty(window.TextureBrowser.CurrentResources);
        var library = new LibraryStore(fixture.Library); var workspace = Pump(library.ReadAsync()).Workspaces.Single();
        var filter = Pump(library.SaveFilterAsync(workspace.Id, "textures", "Missing catalog", new Dictionary<string, string> { ["group"] = "missing-catalog" }));
        Pump((Task)Invoke(window, "RefreshLibraryAsync")!); OpenSaved(window, "filters", "Filter", filter.Id);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("LibraryHost")).Visibility);
        Assert.Contains("unavailable", ((TextBlock)window.FindName("LibraryEmpty")).Text, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public void HomeCancelsPendingSavedGroupWithoutLateBrowseOverride() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        WriteManyCatalogs(fixture.Index); Pump(window.OpenArchiveAsync(fixture.Index));
        using var held = new HeldDiscovery(window); Pump(window.ShowTexturesAsync()); var catalogs = held.Session;
        var library = new LibraryStore(fixture.Library); var workspace = Pump(library.ReadAsync()).Workspaces.Single();
        var key = new ResourceIdentity(window.CurrentSession!.Snapshot.IndexContentId, 8000).Key;
        var saved = Pump(library.SaveFilterAsync(workspace.Id, "textures", "Pending group", new Dictionary<string, string> { ["group"] = key }));
        Pump((Task)Invoke(window, "RefreshLibraryAsync")!); SelectSaved(window, "filters", "Filter", saved.Id);
        var pending = (Task)Invoke(window, "OpenLibrarySelectionAsync")!; Pump(Task.Delay(30)); Assert.False(pending.IsCompleted);
        Invoke(window, "Home_Click", window, new RoutedEventArgs()); Pump(pending);
        held.Release(); Pump(catalogs.EnrichmentTask); Pump(Task.Delay(300)); Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("HomeHost")).Visibility);
    });

    [Fact]
    public void WorkspaceReplacementAndCloseCancelAndDrainCatalogDiscovery() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        WriteManyCatalogs(fixture.Index); Pump(window.OpenArchiveAsync(fixture.Index));
        using var held = new HeldDiscovery(window); Pump(window.ShowTexturesAsync()); var first = held.Session;
        var replacement = Path.Combine(fixture.Root, "second", "archive.idx"); DiscoveryFixture.WriteIndex(replacement, [(2229u, new byte[] { 8 })]);
        var opening = window.OpenArchiveAsync(replacement); Pump(Task.Delay(20)); held.Release(); Pump(opening);
        Assert.True(first.EnrichmentTask.IsCompleted); Assert.Equal(TextureCatalogSessionState.Cancelled, first.Progress.State); Assert.Null(Field<object?>(window, "textureCatalogs"));
        Assert.Null(window.TextureBrowser.NativeSession); Assert.Null(window.AssetBrowser.NativeSession);
        Pump(window.OpenArchiveAsync(fixture.Index)); using var nextHeld = new HeldDiscovery(window); Pump(window.ShowTexturesAsync()); var second = nextHeld.Session;
        window.Close(); Pump(Task.Delay(20)); nextHeld.Release(); Pump(WindowTestLifetime.WaitClosedAsync(window));
        Assert.True(second.EnrichmentTask.IsCompleted); Assert.Equal(TextureCatalogSessionState.Cancelled, second.Progress.State);
    });

    private static void OpenNative(AudioWindow window, DiscoveryFixture fixture)
    { NativeTextureFixture.WriteArchive(fixture.Index); Pump(window.OpenArchiveAsync(fixture.Index)); Pump(window.ShowTexturesAsync()); Pump(Field<TextureDiscoverySession>(window, "textureDiscovery").EnrichmentTask); Pump(window.TextureBrowser.RefreshAsync()); }
    private static TextureDiscoveryLink FixtureLink(NativeTextureSource source, ResourceIdentity target, TextureRelationshipKind kind)
        => new(target, "Texture metadata", kind, true, true,
            [new(source.Container.Identity, source.Revision, "FILETEXT fixture reader", "1", source.Container.Sha256,
                "Archive texture references", source.Container.Extent, source.Metadata.ExactConsumedDecodedExtent)]);
    private sealed class HeldDiscovery : IDisposable
    {
        private readonly InitialRevisionGate revisions = new(); private readonly HeldGate held;
        public TextureCatalogSession Session { get; }
        public HeldDiscovery(AudioWindow window)
        {
            var snapshot = window.CurrentSession!.Snapshot;
            Session = new(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), referenceReader: new LocalizationReferenceReader(revisions));
            Assert.True(revisions.Entered.Wait(TimeSpan.FromSeconds(3)), "Catalog validation did not reach its initial gate.");
            held = new(Field<SemaphoreSlim>(Session, "readGate"));
            var native = new NativeTextureSession(Session); var cache = Field<CacheManager>(window, "cache");
            var discovery = new TextureDiscoverySession(Session, native, Field<AliasStore>(window, "aliases"), cache);
            SetField(window, "textureCatalogs", Session); SetField(window, "textureNative", native); SetField(window, "textureDiscovery", discovery);
            var callback = typeof(AudioWindow).GetMethod("TextureCatalogsUpdated", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Session.Updated += (EventHandler<TextureCatalogSessionUpdatedEventArgs>)Delegate.CreateDelegate(typeof(EventHandler<TextureCatalogSessionUpdatedEventArgs>), window, callback);
            Pump(window.TextureBrowser.LoadAsync(discovery, native, cache, (source, token) => new NativeTextureDecoder().Read(source, token)));
            window.TextureBrowser.DiscoveryCancel = () => { discovery.Cancel(); Session.Cancel(); };
            revisions.Release.Set();
        }
        public void Release() => held.Release();
        public void Dispose() { Release(); revisions.Dispose(); }
    }
    private sealed class InitialRevisionGate : ISourceRevisionProvider, IDisposable
    {
        private readonly FileRevisionProvider inner = new(); private int entered;
        public ManualResetEventSlim Entered { get; } = new(); public ManualResetEventSlim Release { get; } = new();
        public FileStamp Capture(string path) => inner.Capture(path);
        public FileStamp Capture(SafeFileHandle handle, string path)
        { if (Interlocked.Exchange(ref entered, 1) == 0) { Entered.Set(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Initial catalog gate timed out."); } return inner.Capture(handle, path); }
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }
    private sealed class HeldGate : IDisposable
    {
        private readonly SemaphoreSlim gate; private bool held;
        public HeldGate(SemaphoreSlim gate) { this.gate = gate; Assert.True(gate.Wait(TimeSpan.FromSeconds(2))); held = true; }
        public void Release() { if (!held) return; held = false; gate.Release(); }
        public void Dispose() => Release();
    }
    private static void WriteCatalogArchive(string path)
    {
        DiscoveryFixture.WriteIndex(path, [(3u, Container((2229, 3), (2229, 4), (2229, 999), (2230, 6))), (3u, "BAD!"u8.ToArray()), (2229u, new byte[] { 1, 2, 3, 4 }), (2229u, new byte[] { 5 }), (2229u, new byte[] { 6 }), (2230u, new byte[] { 7, 8 }), (3u, Container((2229, 4), (2229, 999)))]);
        var index = File.ReadAllBytes(path); BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(105 + 4 * 28 + 8), 4); File.WriteAllBytes(path, index);
    }
    private static void WriteManyCatalogs(string path) => DiscoveryFixture.WriteIndex(path, Enumerable.Range(0, 8192).Select(_ => (3u, Container((2229, 8193)))).Append((2229u, new byte[] { 1 })).ToArray());
    private static byte[] Container(params (uint Type, uint Id)[] references)
    { var bytes = new byte[20 + references.Length * 9]; "COM_CONT"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 6); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)(bytes.Length - 16)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)references.Length); for (var row = 0; row < references.Length; row++) { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20 + row * 9), references[row].Type); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + row * 9), references[row].Id); } return bytes; }
    private static void OpenSaved(AudioWindow window, string view, string property, Guid id) { SelectSaved(window, view, property, id); Pump((Task)Invoke(window, "OpenLibrarySelectionAsync")!); }
    private static void SelectSaved(AudioWindow window, string view, string property, Guid id)
    { Invoke(window, "ShowLibrary", view, null); var list = (ListView)window.FindName("LibraryList"); list.SelectedItem = list.Items.Cast<object>().Single(item => property == "Filter" ? ((SavedFilter?)item.GetType().GetProperty(property)!.GetValue(item))?.Id == id : (Guid?)item.GetType().GetProperty(property)!.GetValue(item) == id); }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    { for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++) { var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; } }
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static async Task WaitAsync(Func<bool> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(20, timeout.Token); }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(Task task) { var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher; task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult(); }
}
