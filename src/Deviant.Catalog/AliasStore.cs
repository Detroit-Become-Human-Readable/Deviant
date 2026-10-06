using System.Text.Json;
using Deviant.Assets;
namespace Deviant.Catalog;

public sealed class AliasStore
{
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, string> aliases;
    public string LibraryRoot => Path.GetDirectoryName(path)!;
    public AliasStore(string? libraryRoot = null)
    {
        libraryRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deviant", "library");
        Directory.CreateDirectory(libraryRoot);
        path = Path.Combine(Path.GetFullPath(libraryRoot), "aliases.json");
        aliases = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [] : [];
    }
    public string? Get(ResourceIdentity identity) { lock (aliases) return aliases.GetValueOrDefault(identity.Key); }
    public IReadOnlyDictionary<string, string> ReadAll(string indexId)
    { lock (aliases) return aliases.Where(a => a.Key.StartsWith(indexId + ":", StringComparison.Ordinal)).ToDictionary(); }
    public async Task SetAsync(ResourceIdentity identity, string? alias, CancellationToken token = default)
    {
        if (alias?.Length > 512) throw new ArgumentException("Alias must be 512 characters or fewer.", nameof(alias));
        await gate.WaitAsync(token).ConfigureAwait(false);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using var fileLock = await LockFileAsync(token).ConfigureAwait(false);
            Dictionary<string, string> next;
            next = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false)) ?? [] : [];
            if (string.IsNullOrWhiteSpace(alias)) next.Remove(identity.Key); else next[identity.Key] = alias.Trim();
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await JsonSerializer.SerializeAsync(file, next, cancellationToken: token).ConfigureAwait(false); await file.FlushAsync(token).ConfigureAwait(false); }
            token.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
            lock (aliases) { aliases.Clear(); foreach (var entry in next) aliases.Add(entry.Key, entry.Value); }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); gate.Release(); }
    }
    private async Task<FileStream> LockFileAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11) { await Task.Delay(50, token).ConfigureAwait(false); }
        }
    }
    // Revision migration is a reviewed list of concrete identities, never an ID/name heuristic.
    public async Task MigrateAsync(IReadOnlyDictionary<ResourceIdentity, ResourceIdentity> reviewedMatches, CancellationToken token = default)
    {
        foreach (var match in reviewedMatches)
        {
            var alias = Get(match.Key);
            if (alias is not null) await SetAsync(match.Value, alias, token).ConfigureAwait(false);
        }
    }
}
