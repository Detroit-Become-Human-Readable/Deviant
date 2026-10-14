using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Desktop;
using Deviant.Browser;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class CharactersLibraryTests
{
    [Fact]
    public void SelectedCharacterBookmarksExactViewIdentityAndRevisionThenSavedItemRestoresItWithoutPreview() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            Pump(window.OpenArchiveAsync(fixture.Index));
            var snapshot = window.CurrentSession!.Snapshot;
            var descriptor = Descriptor(snapshot, 1, "Generated character");
            window.CharacterCatalogProvider = (source, token) =>
            {
                token.ThrowIfCancellationRequested();
                Assert.Same(snapshot, source);
                return Task.FromResult<IReadOnlyList<AssetDescriptor>>([descriptor]);
            };
            var previewCalls = 0;
            window.CharacterModelProvider = (_, _, _) =>
            {
                previewCalls++;
                return Task.FromException<RenderAssetSnapshot>(new InvalidOperationException("Model preview should not start."));
            };

            Pump(window.ShowCharactersAsync());
            var view = window.CharactersBrowser;
            var search = (TextBox)view.FindName("Search")!;
            search.Text = "no generated entries match this";
            Assert.Empty(Entries(window).Items);
            view.SelectResource(descriptor.Identity);
            Assert.Equal(string.Empty, search.Text);
            Assert.Same(descriptor, view.SelectedDescriptor);
            Assert.True(Named<Button>(window, "BookmarkButton").IsEnabled);

            Named<Button>(window, "BookmarkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var library = new LibraryStore(fixture.Library);
            WaitUntil(() => library.ReadAsync().GetAwaiter().GetResult().Bookmarks.Any(item =>
                item.View == "characters" && item.Identity == descriptor.Identity));
            WaitUntil(() => Named<TextBlock>(window, "StatusText").Text == "Bookmarked");
            Pump((Task)Invoke(window, "RefreshLibraryAsync")!);
            var bookmark = Assert.Single(library.ReadAsync().GetAwaiter().GetResult().Bookmarks,
                item => item.View == "characters" && item.Identity == descriptor.Identity);
            var workspace = Assert.Single(library.ReadAsync().GetAwaiter().GetResult().Workspaces);
            Assert.Equal("characters", bookmark.View);
            Assert.Equal(descriptor.Identity, bookmark.Identity);
            Assert.Equal(descriptor.Revision, bookmark.Revision);
            Assert.Equal(workspace.Revision, bookmark.Revision);

            SelectSavedBookmark(window, bookmark.Id);
            Pump((Task)Invoke(window, "OpenLibrarySelectionAsync")!);

            Assert.Same(Named<TabItem>(window, "CharactersTab"), Named<TabControl>(window, "WorkspaceTabs").SelectedItem);
            Assert.Same(descriptor, view.SelectedDescriptor);
            Assert.Equal(descriptor.Identity, view.SelectedIdentity);
            Assert.Equal(0, previewCalls);
            Assert.Null(view.PreviewContent.Content as AssetModelView);
        });

    [Fact]
    public void SavedCharacterWithChangedSourceKeepsItsOldRevisionAndDoesNotSelectAgainstNewSource() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            Pump(window.OpenArchiveAsync(fixture.Index));
            var originalSnapshot = window.CurrentSession!.Snapshot;
            var descriptor = Descriptor(originalSnapshot, 1, "Generated character");
            window.CharacterCatalogProvider = (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<AssetDescriptor>>([descriptor]);
            };
            var previewCalls = 0;
            window.CharacterModelProvider = (_, _, _) =>
            {
                previewCalls++;
                return Task.FromException<RenderAssetSnapshot>(new InvalidOperationException("Model preview should not start."));
            };
            Pump(window.ShowCharactersAsync());
            window.CharactersBrowser.SelectResource(descriptor.Identity);
            Named<Button>(window, "BookmarkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var library = new LibraryStore(fixture.Library);
            WaitUntil(() => library.ReadAsync().GetAwaiter().GetResult().Bookmarks.Any(item => item.Identity == descriptor.Identity));
            var bookmark = Assert.Single(library.ReadAsync().GetAwaiter().GetResult().Bookmarks,
                item => item.Identity == descriptor.Identity);

            DiscoveryFixture.WriteIndex(fixture.Index, [(999u, new byte[] { 9, 8, 7, 6 })]);
            Pump(window.OpenArchiveAsync(fixture.Index));
            var changedSession = window.CurrentSession!;
            Assert.NotEqual(originalSnapshot.IndexContentId, changedSession.Snapshot.IndexContentId);
            Assert.Equal(descriptor.Revision, Assert.Single(library.ReadAsync().GetAwaiter().GetResult().Bookmarks).Revision);

            SelectSavedBookmark(window, bookmark.Id);
            Pump((Task)Invoke(window, "OpenLibrarySelectionAsync")!);

            Assert.Equal("This bookmark needs a revision match.", Named<TextBlock>(window, "StatusText").Text);
            Assert.Equal(Visibility.Visible, Named<FrameworkElement>(window, "LibraryHost").Visibility);
            Assert.Same(Named<TabItem>(window, "LibraryTab"), Named<TabControl>(window, "WorkspaceTabs").SelectedItem);
            Assert.Null(window.CharactersBrowser.SelectedDescriptor);
            Assert.Equal(0, previewCalls);
        });

    [Fact]
    public void DuplicateSavedCharacterIdentityShowsPlainErrorWithoutSelectingOrPreviewingEitherEntry() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            Pump(window.OpenArchiveAsync(fixture.Index));
            var snapshot = window.CurrentSession!.Snapshot;
            var first = Descriptor(snapshot, 1, "Generated character A");
            var second = first with { Label = "Generated character B" };
            window.CharacterCatalogProvider = (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<AssetDescriptor>>([first, second]);
            };
            var previewCalls = 0;
            window.CharacterModelProvider = (_, _, _) =>
            {
                previewCalls++;
                return Task.FromException<RenderAssetSnapshot>(new InvalidOperationException("Model preview should not start."));
            };
            Pump(window.ShowCharactersAsync());
            Assert.Equal(2, Entries(window).Items.Count);
            Assert.Null(window.CharactersBrowser.SelectedDescriptor);

            var library = new LibraryStore(fixture.Library);
            var workspace = Assert.Single(library.ReadAsync().GetAwaiter().GetResult().Workspaces);
            var bookmark = library.BookmarkAsync(workspace.Id, "characters", first.Identity, first.Revision, "Generated duplicate").GetAwaiter().GetResult();
            Pump((Task)Invoke(window, "RefreshLibraryAsync")!);
            SelectSavedBookmark(window, bookmark.Id);
            Pump((Task)Invoke(window, "OpenLibrarySelectionAsync")!);

            Assert.Contains("Multiple entries match this item", Named<TextBlock>(window, "StatusText").Text, StringComparison.Ordinal);
            Assert.Equal(Visibility.Visible, Named<FrameworkElement>(window, "LibraryHost").Visibility);
            Assert.Same(Named<TabItem>(window, "LibraryTab"), Named<TabControl>(window, "WorkspaceTabs").SelectedItem);
            Assert.Null(window.CharactersBrowser.SelectedDescriptor);
            Assert.Equal(0, previewCalls);
        });

    private static AssetDescriptor Descriptor(ArchiveSnapshot snapshot, long ordinal, string label)
    {
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        var identity = new ResourceIdentity(snapshot.IndexContentId, ordinal, "generated/character-entry");
        return new(identity, revision, "character.model", label, "generated-group", AssetAvailability.Available,
            AssetCompleteness.Complete, AssetCapabilities.Preview,
            [new NameEvidence(label + " stored name", NameOrigin.Stored, "generated descriptor")], []);
    }

    private static ListView Entries(AudioWindow window) =>
        (ListView)window.CharactersBrowser.FindName("Entries")!;

    private static void SelectSavedBookmark(AudioWindow window, Guid bookmarkId)
    {
        Invoke(window, "ShowLibrary", "bookmarks", null);
        var list = Named<ListView>(window, "LibraryList");
        list.SelectedItem = Assert.Single(list.Items.Cast<object>(), item =>
            (Guid?)item.GetType().GetProperty("BookmarkId", BindingFlags.Instance | BindingFlags.Public)?.GetValue(item) == bookmarkId);
    }

    private static T Named<T>(AudioWindow window, string name) where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(window.FindName(name));

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static void WaitUntil(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready())
        {
            timeout.Token.ThrowIfCancellationRequested();
            Pump(Task.Delay(10, timeout.Token));
        }
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
