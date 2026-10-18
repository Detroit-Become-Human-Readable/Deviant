using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Deviant.Catalog;

public sealed record CacheSettings(string Location, long LimitBytes = 4L * 1024 * 1024 * 1024, int Workers = 0,IReadOnlyList<string>? PreviousLocations=null);
public sealed class CacheLease(string path, Func<ValueTask> release) : IAsyncDisposable, IDisposable
{
    private int disposed;
    public string Path { get; } = path;
    public void Dispose() => DisposeAsync().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref disposed, 1) == 0 ? release() : ValueTask.CompletedTask;
}
public sealed partial class CacheManager : IDisposable
{
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly object leaseGate = new();
    private readonly HashSet<string> protectedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly string settingsPath;
    private TaskCompletionSource drained = NewSignal();
    private CacheSettings settings;
    private CacheStorageBudget.ManagerLease storagePolicy;
    public CacheManager(string? settingsRoot = null, CacheSettings? initial = null)
    {
        settingsRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deviant", "settings");
        Directory.CreateDirectory(settingsRoot);
        settingsPath = Path.Combine(Path.GetFullPath(settingsRoot), "cache.json");
        settings = initial ?? (File.Exists(settingsPath) ? JsonSerializer.Deserialize<CacheSettings>(File.ReadAllText(settingsPath)) : null)
            ?? new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deviant", "cache"));
        settings = settings with { Location = Path.GetFullPath(settings.Location) };
        ValidateLimit(settings.LimitBytes); ValidateWorkers(settings.Workers); ValidateLocation(Location); Directory.CreateDirectory(Location);
        storagePolicy = CacheStorageBudget.AttachManager(Location, LimitBytes);
    }
    public string Location => settings.Location;
    public long LimitBytes => settings.LimitBytes;
    public int Workers => settings.Workers;
    public int EffectiveWorkers => Workers == 0 ? Math.Clamp(Environment.ProcessorCount / 4, 1, 2) : Workers;
    public IReadOnlyList<string> PreviousLocations => settings.PreviousLocations??[];
    public int ActivePreviewLeases { get { lock (leaseGate) return leases.Values.Sum(); } }
    public long UsageBytes => GetUsage(CacheUsage.Total);
    public long PreviewUsageBytes => GetUsage(CacheUsage.Preview);
    public long PreparedUsageBytes => GetUsage(CacheUsage.Prepared);
    public long MetadataUsageBytes => GetUsage(CacheUsage.Metadata);
    public long AvailableBytes => new DriveInfo(Path.GetPathRoot(Location)!).AvailableFreeSpace;
    public static string Key(string sourceRevision, string payloadHash, string decoderVersion, string options) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("preview-v1\n" + sourceRevision + "\n" + payloadHash + "\n" + decoderVersion + "\n" + options))).ToLowerInvariant();

    public Task<CacheLease> AcquireAsync(string key, Func<string, CancellationToken, Task> producer, CancellationToken token = default) =>
        AcquireAsync(key, producer, token, ".wav");

    public async Task<CacheLease> AcquireAsync(string key, Func<string, CancellationToken, Task> producer, CancellationToken token, string extension)
    {
        if (extension is not (".wav" or ".png")) throw new ArgumentException("Use a WAV or PNG cache entry.", nameof(extension));
        return await AcquireCoreAsync(key, producer, token, extension, null).ConfigureAwait(false);
    }

    public Task<CacheLease> AcquireMaterialBakeAsync(string key, Func<string, CancellationToken, Task> producer,
        Func<string, CancellationToken, Task<bool>> validate, CancellationToken token = default) =>
        AcquireCoreAsync("material-bake-v1/" + key, producer, token, ".bake", validate);

    public async Task SetLimitAsync(long bytes, CancellationToken token = default)
    {
        ValidateLimit(bytes); await operation.WaitAsync(token).ConfigureAwait(false);
        try { await SaveSettingsAsync(settings with { LimitBytes = bytes }, token).ConfigureAwait(false); Evict(0, null); }
        finally { operation.Release(); }
    }
    public async Task SetWorkersAsync(int workers, CancellationToken token = default)
    {
        ValidateWorkers(workers); await operation.WaitAsync(token).ConfigureAwait(false);
        try { await SaveSettingsAsync(settings with { Workers = workers }, token).ConfigureAwait(false); }
        finally { operation.Release(); }
    }
    public async Task ClearAsync(Func<CancellationToken, Task>? drainUsers = null, CancellationToken token = default)
    {
        if (drainUsers is not null) await drainUsers(token).ConfigureAwait(false);
        await BeginMaintenanceAsync(token).ConfigureAwait(false);
        try
        {
            await WaitForUsersAsync(token).ConfigureAwait(false);
            await operation.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ValidateLocation(Location);
                foreach (var file in Files(Location)) { token.ThrowIfCancellationRequested(); File.Delete(file); }
                ResetAccounting();
            }
            finally { operation.Release(); }
        }
        finally { EndMaintenance(); }
    }
    public Task ClearAsync(CancellationToken token) => ClearAsync(null, token);
    public async Task RelocateAsync(string destination, Func<CancellationToken, Task>? drainUsers = null, CancellationToken token = default)
    {
        destination = Path.GetFullPath(destination);
        ValidateLocation(destination);
        if (string.Equals(Location, destination, StringComparison.OrdinalIgnoreCase)) return;
        if (Deviant.Core.DirectoryBoundary.IsWithin(destination, Location) || Deviant.Core.DirectoryBoundary.IsWithin(Location, destination))
            throw new IOException("Choose a separate cache folder.");
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("Choose an empty cache folder.");
        if (drainUsers is not null) await drainUsers(token).ConfigureAwait(false);
        await BeginMaintenanceAsync(token).ConfigureAwait(false);
        var stage = destination.TrimEnd(Path.DirectorySeparatorChar) + ".relocating-" + Guid.NewGuid().ToString("N");
        try
        {
            await WaitForUsersAsync(token).ConfigureAwait(false);
            await operation.WaitAsync(token).ConfigureAwait(false);
            try
            {
            ValidateLocation(destination);
            if (string.Equals(Location, destination, StringComparison.OrdinalIgnoreCase)) return;
            if (Deviant.Core.DirectoryBoundary.IsWithin(destination, Location) || Deviant.Core.DirectoryBoundary.IsWithin(Location, destination))
                throw new IOException("Choose a separate cache folder.");
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                throw new IOException("Choose an empty cache folder.");
            ValidateLocation(Location);
            EnsureAccounting(); RefreshChangedPaths();
            Directory.CreateDirectory(stage);
            ValidateLocation(stage);
            if (new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace < TrackedUsageBytes)
                throw new IOException("The destination does not have enough free space.");
            foreach (var source in Files(Location))
            {
                token.ThrowIfCancellationRequested();
                var target = Path.Combine(stage, Path.GetRelativePath(Location, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = File.OpenRead(source);
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
                { await input.CopyToAsync(output, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); }
                input.Position = 0;
                var sourceHash = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
                await using var copy = File.OpenRead(target);
                var copyHash = await SHA256.HashDataAsync(copy, token).ConfigureAwait(false);
                if (!sourceHash.AsSpan().SequenceEqual(copyHash))
                    throw new IOException("Cache verification failed.");
            }
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(destination)) Directory.Delete(destination);
            Directory.Move(stage, destination);
            await SaveSettingsAsync(settings with { Location = destination,PreviousLocations=PreviousLocations.Append(Location).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() }, token).ConfigureAwait(false);
            ResetAccounting();
            }
            finally { operation.Release(); }
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
            finally { EndMaintenance(); }
        }
    }
    public Task RelocateAsync(string destination, CancellationToken token) => RelocateAsync(destination, null, token);
    public async Task RemovePreviousAsync(string previous,CancellationToken token=default)
    {
        previous=Path.GetFullPath(previous); ValidateLocation(previous);
        if(!PreviousLocations.Contains(previous,StringComparer.OrdinalIgnoreCase)||Deviant.Core.DirectoryBoundary.IsWithin(Location,previous)||Deviant.Core.DirectoryBoundary.IsWithin(previous,Location))
            throw new IOException("This folder is not a retained previous cache.");
        await operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ValidateLocation(previous);
            if (!PreviousLocations.Contains(previous, StringComparer.OrdinalIgnoreCase) ||
                Deviant.Core.DirectoryBoundary.IsWithin(Location, previous) || Deviant.Core.DirectoryBoundary.IsWithin(previous, Location))
                throw new IOException("This folder is not a retained previous cache.");
            await Task.Run(()=>
            {
                if(!Directory.Exists(previous)) return;
                if(Directory.EnumerateFileSystemEntries(previous,"*",SearchOption.AllDirectories).Any(path=>(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)) throw new IOException("Previous cache contains symbolic links.");
                foreach(var file in Files(previous)){token.ThrowIfCancellationRequested();File.Delete(file);}
                foreach(var directory in Directory.EnumerateDirectories(previous,"*",SearchOption.AllDirectories).OrderByDescending(p=>p.Length)) Directory.Delete(directory);
                Directory.Delete(previous);
            },token).ConfigureAwait(false);
            await SaveSettingsAsync(settings with { PreviousLocations=PreviousLocations.Where(p=>!p.Equals(previous,StringComparison.OrdinalIgnoreCase)).ToArray() },token).ConfigureAwait(false);
        }
        finally{operation.Release();}
    }
    private async Task SaveSettingsAsync(CacheSettings next, CancellationToken token)
    {
        var temp = settingsPath + ".tmp";
        var previous = settings;
        var relocated = !string.Equals(previous.Location, next.Location, StringComparison.OrdinalIgnoreCase);
        var policy = relocated ? CacheStorageBudget.AttachManager(next.Location, next.LimitBytes) : storagePolicy;
        if (!relocated) policy.UpdateLimit(next.LimitBytes);
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(next), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); File.Move(temp, settingsPath, true); settings = next;
            if (relocated) { var old = storagePolicy; storagePolicy = policy; old.Dispose(); }
        }
        catch
        {
            if (relocated) policy.Dispose(); else policy.UpdateLimit(previous.LimitBytes);
            throw;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static IEnumerable<string> Files(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var directories = new Stack<string>(); directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cache folders cannot contain symbolic links or junctions.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Cache folders cannot contain symbolic links or junctions.");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
                else yield return entry;
            }
        }
    }
    private static void ValidateLimit(long bytes) { if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes)); }
    private static void ValidateWorkers(int workers) { if (workers is not (0 or 1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(workers)); }
    public void RegisterProtectedDirectory(string directory)
    {
        directory = Path.GetFullPath(directory);
        lock (leaseGate)
        {
            protectedDirectories.Add(directory);
            ValidateLocation(Location);
        }
    }
    private void ValidateLocation(string path)
    {
        lock (leaseGate)
            foreach (var directory in protectedDirectories)
                if (Deviant.Core.DirectoryBoundary.IsWithin(directory, path) || Deviant.Core.DirectoryBoundary.IsWithin(path, directory))
                    throw new IOException("Choose a cache folder separate from the library.");
        if (Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar)) is null) throw new IOException("Choose an application cache folder.");
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        { if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Cache folders cannot contain symbolic links or junctions."); }
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
