using System.Text.Json;
using System.Text.Json.Serialization;
using Deviant.Assets;

namespace Deviant.Catalog;

public sealed record WorkspaceEntry(Guid Id, string IndexPath, string Label, SourceRevision Revision, DateTimeOffset LastOpened);
public sealed record LibraryBookmark(Guid Id, Guid WorkspaceId, string View, ResourceIdentity Identity,
    SourceRevision Revision, string Label, DateTimeOffset SavedAt);
public sealed record LibraryRecent(Guid WorkspaceId, string View, ResourceIdentity Identity,
    SourceRevision Revision, string Label, DateTimeOffset VisitedAt);
public sealed record UserCollection(Guid Id, string Name, IReadOnlyList<Guid> Bookmarks);
public sealed record SavedFilter(Guid Id, Guid WorkspaceId, string View, string Name, IReadOnlyDictionary<string,string> Values);
public sealed class LibraryState
{
    public int Version { get; set; } = 1;
    public List<WorkspaceEntry> Workspaces { get; set; } = [];
    public List<LibraryBookmark> Bookmarks { get; set; } = [];
    public List<UserCollection> Collections { get; set; } = [];
    public List<LibraryRecent> RecentItems { get; set; } = [];
    public List<SavedFilter> Filters { get; set; } = [];
    [JsonExtensionData] public Dictionary<string,JsonElement>? Extensions { get; set; }
}

/// <summary>Durable, user-authored discovery data. All writers merge under one library file lease.</summary>
public sealed class LibraryStore
{
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private const long MaximumBytes = 128L * 1024 * 1024;
    public LibraryStore(string? libraryRoot = null)
    {
        libraryRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deviant", "library");
        Directory.CreateDirectory(libraryRoot);
        path = Path.Combine(Path.GetFullPath(libraryRoot), "discoveries.json");
    }
    public string Location => Path.GetDirectoryName(path)!;
    public Task<LibraryState> ReadAsync(CancellationToken token = default) => AccessAsync(state => state, false, token);

    public Task<WorkspaceEntry> OpenWorkspaceAsync(string indexPath, SourceRevision revision, string? label = null, CancellationToken token = default)
    {
        indexPath = Path.GetFullPath(indexPath);
        return AccessAsync(state =>
        {
            var existing = state.Workspaces.FirstOrDefault(entry => entry.IndexPath.Equals(indexPath, StringComparison.OrdinalIgnoreCase));
            var entry = new WorkspaceEntry(existing?.Id ?? Guid.NewGuid(), indexPath,
                label ?? existing?.Label ?? Path.GetFileName(Path.GetDirectoryName(indexPath)) ?? Path.GetFileName(indexPath), revision, DateTimeOffset.UtcNow);
            state.Workspaces.RemoveAll(item => item.Id == entry.Id); state.Workspaces.Insert(0, entry);
            return entry;
        }, true, token);
    }
    public Task<LibraryBookmark> BookmarkAsync(Guid workspaceId, string view, ResourceIdentity identity,
        SourceRevision revision, string label, CancellationToken token = default)
    {
        ValidateIdentity(identity, revision); ValidateLabel(label);
        return AccessAsync(state =>
        {
            RequireWorkspace(state, workspaceId);
            var existing = state.Bookmarks.FirstOrDefault(item => item.WorkspaceId == workspaceId && item.View == view && item.Identity == identity);
            var item = new LibraryBookmark(existing?.Id ?? Guid.NewGuid(), workspaceId, view, identity, revision, label, DateTimeOffset.UtcNow);
            state.Bookmarks.RemoveAll(entry => entry.Id == item.Id); state.Bookmarks.Add(item); return item;
        }, true, token);
    }
    public Task RemoveBookmarkAsync(Guid id, CancellationToken token = default) => AccessAsync(state =>
    {
        state.Bookmarks.RemoveAll(entry => entry.Id == id);
        for (var i = 0; i < state.Collections.Count; i++) state.Collections[i] = state.Collections[i] with { Bookmarks = state.Collections[i].Bookmarks.Where(bookmark => bookmark != id).ToArray() };
        return true;
    }, true, token);
    public Task VisitAsync(Guid workspaceId, string view, ResourceIdentity identity, SourceRevision revision, string label, CancellationToken token = default)
    {
        ValidateIdentity(identity, revision); ValidateLabel(label);
        return AccessAsync(state =>
        {
            RequireWorkspace(state, workspaceId);
            state.RecentItems.RemoveAll(item => item.WorkspaceId == workspaceId && item.View == view && item.Identity == identity);
            state.RecentItems.Insert(0, new(workspaceId, view, identity, revision, label, DateTimeOffset.UtcNow));
            if (state.RecentItems.Count > 200) state.RecentItems.RemoveRange(200, state.RecentItems.Count - 200);
            return true;
        }, true, token);
    }
    public Task<UserCollection> SaveCollectionAsync(string name, IReadOnlyList<Guid> bookmarks, Guid? id = null, CancellationToken token = default)
    {
        ValidateLabel(name);
        return AccessAsync(state =>
        {
            var known = state.Bookmarks.Select(bookmark => bookmark.Id).ToHashSet();
            if (bookmarks.Any(bookmark => !known.Contains(bookmark))) throw new ArgumentException("The collection contains an unknown bookmark.");
            var entry = new UserCollection(id ?? Guid.NewGuid(), name.Trim(), bookmarks.Distinct().ToArray());
            state.Collections.RemoveAll(item => item.Id == entry.Id); state.Collections.Add(entry); return entry;
        }, true, token);
    }
    public Task RemoveCollectionAsync(Guid id, CancellationToken token = default) => AccessAsync(state => { state.Collections.RemoveAll(item => item.Id == id); return true; }, true, token);
    public Task AddCollectionMembersAsync(Guid id,IReadOnlyList<Guid> bookmarks,CancellationToken token=default)=>AccessAsync(state=>
    {
        var index=state.Collections.FindIndex(item=>item.Id==id);if(index<0)throw new ArgumentException("The collection is unavailable.");
        var known=state.Bookmarks.Select(item=>item.Id).ToHashSet();if(bookmarks.Any(item=>!known.Contains(item)))throw new ArgumentException("A bookmark is unavailable.");
        var collection=state.Collections[index];state.Collections[index]=collection with{Bookmarks=collection.Bookmarks.Concat(bookmarks).Distinct().ToArray()};return true;
    },true,token);
    public Task RemoveCollectionMembersAsync(Guid id,IReadOnlyList<Guid> bookmarks,CancellationToken token=default)=>AccessAsync(state=>
    {
        var index=state.Collections.FindIndex(item=>item.Id==id);if(index<0)throw new ArgumentException("The collection is unavailable.");
        var collection=state.Collections[index];state.Collections[index]=collection with{Bookmarks=collection.Bookmarks.Except(bookmarks).ToArray()};return true;
    },true,token);
    public Task RemoveRecentAsync(Guid workspaceId,string view,ResourceIdentity identity,CancellationToken token=default)=>AccessAsync(state=>
    {state.RecentItems.RemoveAll(item=>item.WorkspaceId==workspaceId&&item.View==view&&item.Identity==identity);return true;},true,token);
    public Task<SavedFilter> SaveFilterAsync(Guid workspaceId, string view, string name, IReadOnlyDictionary<string,string> values, Guid? id = null, CancellationToken token = default)
    {
        ValidateLabel(name);
        return AccessAsync(state =>
        {
            RequireWorkspace(state, workspaceId);
            var filter = new SavedFilter(id ?? Guid.NewGuid(), workspaceId, view, name.Trim(), new Dictionary<string,string>(values));
            state.Filters.RemoveAll(item => item.Id == filter.Id); state.Filters.Add(filter); return filter;
        }, true, token);
    }
    public Task RemoveFilterAsync(Guid id, CancellationToken token = default) => AccessAsync(state => { state.Filters.RemoveAll(item => item.Id == id); return true; }, true, token);
    public Task MigrateAsync(Guid workspaceId, SourceRevision targetRevision,
        IReadOnlyDictionary<ResourceIdentity,ResourceIdentity> reviewedMatches, CancellationToken token = default) => AccessAsync(state =>
    {
        RequireWorkspace(state, workspaceId);
        foreach (var match in reviewedMatches) ValidateIdentity(match.Value, targetRevision);
        for (var i = 0; i < state.Bookmarks.Count; i++)
        {
            var bookmark = state.Bookmarks[i];
            if (bookmark.WorkspaceId == workspaceId && reviewedMatches.TryGetValue(bookmark.Identity, out var target))
                state.Bookmarks[i] = bookmark with { Identity = target, Revision = targetRevision };
        }
        return true;
    }, true, token);
    public static bool MatchesRevision(SourceRevision saved, SourceRevision current) => saved == current;

    private async Task<T> AccessAsync<T>(Func<LibraryState,T> operation, bool write, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using var lease = await LeaseAsync(token).ConfigureAwait(false);
            LibraryState state;
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > MaximumBytes) throw new IOException("The discovery library exceeds the supported size.");
                await using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous);
                state = await JsonSerializer.DeserializeAsync<LibraryState>(read, cancellationToken: token).ConfigureAwait(false) ?? throw new IOException("The discovery library is empty.");
                if (state.Version != 1) throw new IOException("This discovery library version is unsupported.");
            }
            else state = new();
            var result = operation(state);
            if (write)
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(output, state, cancellationToken: token).ConfigureAwait(false);
                    if (output.Length > MaximumBytes) throw new IOException("The discovery library exceeds the supported size.");
                    await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
                }
                token.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
            }
            return result;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); gate.Release(); }
    }
    private async Task<FileStream> LeaseAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11) { await Task.Delay(30, token).ConfigureAwait(false); }
        }
    }
    private static void RequireWorkspace(LibraryState state, Guid id)
    { if (!state.Workspaces.Any(workspace => workspace.Id == id)) throw new ArgumentException("The workspace is not in this library."); }
    private static void ValidateIdentity(ResourceIdentity identity, SourceRevision revision)
    { if (identity.Ordinal < 0 || identity.IndexContentId != revision.IndexContentId) throw new ArgumentException("The resource does not belong to this revision."); }
    private static void ValidateLabel(string label)
    { if (string.IsNullOrWhiteSpace(label) || label.Length > 512) throw new ArgumentException("Enter a name of 512 characters or fewer."); }
}
