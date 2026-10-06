using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Deviant.Catalog;

public sealed record CacheSettings(string Location, long LimitBytes = 4L * 1024 * 1024 * 1024, int Workers = 0,IReadOnlyList<string>? PreviousLocations=null);
public sealed class CacheLease(string path, Func<ValueTask> release) : IAsyncDisposable
{
    private int disposed;
    public string Path { get; } = path;
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref disposed, 1) == 0 ? release() : ValueTask.CompletedTask;
}
public sealed class CacheManager
{
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly object leaseGate = new();
    private readonly HashSet<string> protectedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly string settingsPath;
    private TaskCompletionSource drained = NewSignal();
    private CacheSettings settings;
    public CacheManager(string? settingsRoot = null, CacheSettings? initial = null)
    {
        settingsRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deviant", "settings");
        Directory.CreateDirectory(settingsRoot);
        settingsPath = Path.Combine(Path.GetFullPath(settingsRoot), "cache.json");
        settings = initial ?? (File.Exists(settingsPath) ? JsonSerializer.Deserialize<CacheSettings>(File.ReadAllText(settingsPath)) : null)
            ?? new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deviant", "cache"));
        settings = settings with { Location = Path.GetFullPath(settings.Location) };
        ValidateLimit(settings.LimitBytes); ValidateWorkers(settings.Workers); ValidateLocation(Location); Directory.CreateDirectory(Location);
    }
    public string Location => settings.Location;
    public long LimitBytes => settings.LimitBytes;
    public int Workers => settings.Workers;
    public int EffectiveWorkers => Workers == 0 ? Math.Clamp(Environment.ProcessorCount / 4, 1, 2) : Workers;
    public IReadOnlyList<string> PreviousLocations => settings.PreviousLocations??[];
    public int ActivePreviewLeases { get { lock (leaseGate) return leases.Values.Sum(); } }
    public long UsageBytes => Files(Location).Sum(f => new FileInfo(f).Length);
    public long PreviewUsageBytes => Files(Path.Combine(Location,"previews-v1")).Sum(f => new FileInfo(f).Length);
    public long MetadataUsageBytes => Math.Max(0,UsageBytes-PreviewUsageBytes);
    public long AvailableBytes => new DriveInfo(Path.GetPathRoot(Location)!).AvailableFreeSpace;
    public static string Key(string sourceRevision, string payloadHash, string decoderVersion, string options) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("preview-v1\n" + sourceRevision + "\n" + payloadHash + "\n" + decoderVersion + "\n" + options))).ToLowerInvariant();

    public Task<CacheLease> AcquireAsync(string key, Func<string, CancellationToken, Task> producer, CancellationToken token = default) =>
        AcquireAsync(key, producer, token, ".wav");

    public async Task<CacheLease> AcquireAsync(string key, Func<string, CancellationToken, Task> producer, CancellationToken token, string extension)
    {
        if (extension is not (".wav" or ".png")) throw new ArgumentException("Use a WAV or PNG cache entry.", nameof(extension));
        var safeKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        await operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (LimitBytes == 0)
            {
                var uncached = Path.Combine(Path.GetTempPath(), "Deviant-preview-" + Guid.NewGuid().ToString("N") + extension);
                try { await producer(uncached, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); return Lease(uncached, true); }
                catch { if (File.Exists(uncached)) File.Delete(uncached); throw; }
            }
            var previewRoot = Path.Combine(Location, "previews-v1"); Directory.CreateDirectory(previewRoot);
            var path = Path.Combine(previewRoot, safeKey + extension);
            if (!File.Exists(path))
            {
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    try { await producer(temp, token).ConfigureAwait(false); }
                    catch (IOException error) when ((error.HResult & 0xffff) is 112 or 39)
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                        var fallback = Path.Combine(Path.GetTempPath(), "Deviant-preview-" + Guid.NewGuid().ToString("N") + extension);
                        try
                        {
                            await producer(fallback, token).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            return Lease(fallback, true);
                        }
                        catch { if (File.Exists(fallback)) File.Delete(fallback); throw; }
                    }
                    token.ThrowIfCancellationRequested();
                    if (!File.Exists(temp)) throw new IOException("Preview producer did not create a file.");
                    var size = new FileInfo(temp).Length;
                    Evict(size, temp);
                    if (size > LimitBytes || PreviewUsageBytes > LimitBytes)
                    {
                        var uncached = Path.Combine(Path.GetTempPath(), "Deviant-preview-" + Guid.NewGuid().ToString("N") + extension);
                        File.Move(temp, uncached);
                        return Lease(uncached, true);
                    }
                    File.Move(temp, path);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return Lease(path, false);
        }
        finally { operation.Release(); }
    }
    private CacheLease Lease(string path, bool temporary)
    {
        lock (leaseGate)
        {
            if (leases.Count == 0) drained = NewSignal();
            leases[path] = leases.GetValueOrDefault(path) + 1;
        }
        return new(path, () =>
        {
            lock (leaseGate)
            {
                if (--leases[path] == 0) { leases.Remove(path); if ((temporary || LimitBytes == 0) && File.Exists(path)) { try { File.Delete(path); } catch (IOException) { } } }
                if (leases.Count == 0) drained.TrySetResult();
            }
            return ValueTask.CompletedTask;
        });
    }
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
        await operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WaitForLeasesAsync(token).ConfigureAwait(false);
            ValidateLocation(Location);
            foreach (var file in Files(Location)) { token.ThrowIfCancellationRequested(); File.Delete(file); }
        }
        finally { operation.Release(); }
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
        await operation.WaitAsync(token).ConfigureAwait(false);
        var stage = destination.TrimEnd(Path.DirectorySeparatorChar) + ".relocating-" + Guid.NewGuid().ToString("N");
        try
        {
            await WaitForLeasesAsync(token).ConfigureAwait(false);
            Directory.CreateDirectory(stage);
            if (new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace < UsageBytes)
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
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); operation.Release(); }
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
    private Task WaitForLeasesAsync(CancellationToken token)
    { lock (leaseGate) return leases.Count == 0 ? Task.CompletedTask : drained.Task.WaitAsync(token); }
    private void Evict(long required, string? exclude)
    {
        var previewRoot = Path.Combine(Location, "previews-v1");
        if (!Directory.Exists(previewRoot)) return;
        var usage = PreviewUsageBytes - (exclude is not null && File.Exists(exclude) ? new FileInfo(exclude).Length : 0);
        foreach (var file in Files(previewRoot).Where(p => p != exclude).OrderBy(File.GetLastWriteTimeUtc))
        {
            if (usage + required <= LimitBytes) break;
            lock (leaseGate) { if (leases.ContainsKey(file)) continue; var size = new FileInfo(file).Length; File.Delete(file); usage -= size; }
        }
    }
    private async Task SaveSettingsAsync(CacheSettings next, CancellationToken token)
    {
        var temp = settingsPath + ".tmp";
        try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(next), token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); File.Move(temp, settingsPath, true); settings = next; }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static IEnumerable<string> Files(string root) => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) : [];
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
