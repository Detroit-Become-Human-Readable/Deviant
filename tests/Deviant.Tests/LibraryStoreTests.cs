using Deviant.Assets;
using Deviant.Catalog;
using Xunit;
namespace Deviant.Tests;

public sealed class LibraryStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-library-" + Guid.NewGuid().ToString("N"));
    [Fact] public async Task DiscoveriesSurviveRestartAndCacheChangesWithoutAutomaticRevisionMigration()
    {
        var store = new LibraryStore(Path.Combine(root,"library")); var revision = new SourceRevision("index-a","stamp-a");
        var workspace = await store.OpenWorkspaceAsync(Path.Combine(root,"source","archive.idx"),revision,"Workspace");
        var bookmark = await store.BookmarkAsync(workspace.Id,"strings",new("index-a",1,"record/2"),revision,"Dialogue");
        await store.SaveCollectionAsync("My chapter",[bookmark.Id]); await store.SaveFilterAsync(workspace.Id,"strings","French",new Dictionary<string,string>{{"language","FRE"}});
        Directory.CreateDirectory(Path.Combine(root,"cache"));Directory.Delete(Path.Combine(root,"cache"));
        var restarted = new LibraryStore(Path.Combine(root,"library")); var state = await restarted.ReadAsync();
        Assert.Single(state.Bookmarks);Assert.Equal(bookmark.Id,Assert.Single(state.Collections).Bookmarks.Single());Assert.Equal("FRE",Assert.Single(state.Filters).Values["language"]);
        var nextRevision = new SourceRevision("index-b","stamp-b"); await restarted.OpenWorkspaceAsync(workspace.IndexPath,nextRevision);
        state = await restarted.ReadAsync();Assert.Equal(workspace.Id,state.Workspaces.Single().Id);Assert.Equal("index-a",state.Bookmarks.Single().Identity.IndexContentId);
        Assert.False(LibraryStore.MatchesRevision(state.Bookmarks.Single().Revision,nextRevision));
        await restarted.MigrateAsync(workspace.Id,nextRevision,new Dictionary<ResourceIdentity,ResourceIdentity>{{bookmark.Identity,new("index-b",7,"record/2")}});
        Assert.Equal(7,(await restarted.ReadAsync()).Bookmarks.Single().Identity.Ordinal);
    }
    [Fact] public async Task ConcurrentWritersMergeAndCollectionRemovalKeepsBookmarks()
    {
        var first = new LibraryStore(root);var second = new LibraryStore(root);var revision = new SourceRevision("index","stamp");
        var workspace = await first.OpenWorkspaceAsync(Path.Combine(root,"archive.idx"),revision);
        await Task.WhenAll(Enumerable.Range(0,20).Select(i=>(i%2==0?first:second).BookmarkAsync(workspace.Id,"audio",new("index",i),revision,"Clip "+i)));
        var state = await first.ReadAsync();Assert.Equal(20,state.Bookmarks.Count);
        var collection = await first.SaveCollectionAsync("Favorites",state.Bookmarks.Select(item=>item.Id).ToArray());
        await second.RemoveCollectionAsync(collection.Id);Assert.Equal(20,(await first.ReadAsync()).Bookmarks.Count);
        await first.RemoveBookmarkAsync(state.Bookmarks[0].Id);Assert.Equal(19,(await second.ReadAsync()).Bookmarks.Count);
    }
    [Fact] public async Task ConcurrentMembershipChangesMergeAndRemovalIsLocalToCollection()
    {
        var first=new LibraryStore(root);var second=new LibraryStore(root);var revision=new SourceRevision("index","stamp");var workspace=await first.OpenWorkspaceAsync(Path.Combine(root,"archive.idx"),revision);
        var a=await first.BookmarkAsync(workspace.Id,"audio",new("index",1),revision,"A");var b=await first.BookmarkAsync(workspace.Id,"audio",new("index",2),revision,"B");
        var collection=await first.SaveCollectionAsync("Favorites",[]);
        await Task.WhenAll(first.AddCollectionMembersAsync(collection.Id,[a.Id]),second.AddCollectionMembersAsync(collection.Id,[b.Id]));
        Assert.Equal(2,(await first.ReadAsync()).Collections.Single().Bookmarks.Count);
        await second.RemoveCollectionMembersAsync(collection.Id,[a.Id]);var state=await first.ReadAsync();Assert.Equal(2,state.Bookmarks.Count);Assert.Equal(b.Id,state.Collections.Single().Bookmarks.Single());
        await first.VisitAsync(workspace.Id,"audio",a.Identity,revision,"A");await second.RemoveRecentAsync(workspace.Id,"audio",a.Identity);Assert.Empty((await first.ReadAsync()).RecentItems);
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
