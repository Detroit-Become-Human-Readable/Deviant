using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Deviant.Catalog;

public sealed partial class CacheManager
{
    private sealed class KeyFlight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Users;
    }
    private sealed record CacheFile(long Length, DateTime LastWriteUtc, bool Preview, bool Prepared);
    private readonly Dictionary<string, KeyFlight> flights = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CacheFile> catalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> changedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> rescanPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> knownDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim maintenance = new(1, 1);
    private TaskCompletionSource admission = NewSignal();
    private bool maintenanceActive, accountingInitialized, reconcileRequested, disposed;
    private int acquisitions;
    private long totalBytes, previewBytes, preparedBytes;
    private long accountingReconciliations;
    private FileSystemWatcher? watcher;
    private enum CacheUsage { Total, Preview, Prepared, Metadata }

    /// <summary>Combines the artifact profile, content identity and options without delimiter ambiguity.</summary>
    public static string PreparedKey(string category, string profile, string contentKey, string options = "")
    {
        ValidateCategory(category);
        ArgumentException.ThrowIfNullOrEmpty(profile);
        ArgumentException.ThrowIfNullOrEmpty(contentKey);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new[] { "prepared-v1", category, profile, contentKey, options }))).ToLowerInvariant();
    }

    /// <summary>Acquires a validated prepared artifact. The key must include its producer profile and source identity.</summary>
    public Task<CacheLease> AcquirePreparedAsync(string category, string key, string extension,
        Func<string, CancellationToken, Task> producer, Func<string, CancellationToken, Task<bool>> validator,
        CancellationToken token = default)
    {
        ValidateCategory(category);
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (extension is null || extension.Length is < 2 or > 17 || extension[0] != '.' ||
            extension.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Use a single artifact file extension.", nameof(extension));
        ArgumentNullException.ThrowIfNull(validator);
        return AcquireCoreAsync("prepared-v1/" + category + "/" + key, producer, token,
            extension.ToLowerInvariant(), validator, Path.Combine("prepared-v1", category));
    }

    public async Task<CacheLease?> TryAcquirePreparedAsync(string category, string key, string extension,
        Func<string, CancellationToken, Task<bool>> validator, CancellationToken token = default)
    {
        try
        {
            return await AcquirePreparedAsync(category, key, extension,
                (_, _) => Task.FromException(new PreparedCacheMissException()), validator, token).ConfigureAwait(false);
        }
        catch (PreparedCacheMissException) { return null; }
    }

    private sealed class PreparedCacheMissException : Exception;

    private static void ValidateCategory(string category)
    {
        if (string.IsNullOrEmpty(category) || category.Length > 64 ||
            category.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Use an artifact category name containing letters, numbers, hyphens or underscores.", nameof(category));
    }

    private async Task<CacheLease> AcquireCoreAsync(string key, Func<string, CancellationToken, Task> producer,
        CancellationToken token, string extension, Func<string, CancellationToken, Task<bool>>? validate,
        string subdirectory = "previews-v1")
    {
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentNullException.ThrowIfNull(key);
        string location = await BeginAcquisitionAsync(token).ConfigureAwait(false);
        string safeKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        string path = Path.Combine(location, subdirectory, safeKey + extension);
        KeyFlight flight;
        lock (leaseGate)
        {
            if (!flights.TryGetValue(path, out flight!)) flights.Add(path, flight = new());
            flight.Users++;
        }
        bool entered = false;
        CacheLease? pin = null;
        try
        {
            await flight.Gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            await operation.WaitAsync(token).ConfigureAwait(false);
            bool uncached;
            try
            {
                ValidateLocation(location);
                RefreshChangedPaths();
                uncached = LimitBytes == 0;
                if (!uncached)
                {
                    var directory = Path.GetDirectoryName(path)!;
                    ValidateLocation(directory);
                    Directory.CreateDirectory(directory);
                    lock (leaseGate) knownDirectories.Add(directory);
                    bool exists = ObserveFile(path);
                    lock (leaseGate)
                    {
                        if (!exists && leases.ContainsKey(path))
                            throw new IOException("A cached artifact was removed while in use.");
                    }
                    if (exists) pin = Lease(path, false);
                }
            }
            finally { operation.Release(); }

            if (uncached) return await ProduceTemporaryAsync(producer, validate, extension, token).ConfigureAwait(false);
            if (pin is not null)
            {
                bool valid = validate is null || await validate(path, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await operation.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (valid && File.Exists(path))
                    {
                        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                        ObserveFile(path);
                        var result = pin; pin = null;
                        return result;
                    }
                    lock (leaseGate)
                        if (leases.GetValueOrDefault(path) > 1)
                            throw new IOException("A cached artifact changed while in use.");
                    await pin.DisposeAsync().ConfigureAwait(false); pin = null;
                    DeleteEntry(path);
                }
                finally { operation.Release(); }
            }

            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                try { await producer(temp, token).ConfigureAwait(false); }
                catch (IOException error) when ((error.HResult & 0xffff) is 112 or 39)
                {
                    DeleteTemporary(temp);
                    return await ProduceTemporaryAsync(producer, validate, extension, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                if (!File.Exists(temp)) throw new IOException("Cache producer did not create a file.");
                if (validate is not null && !await validate(temp, token).ConfigureAwait(false))
                    throw new InvalidDataException("Cache producer created an invalid artifact.");
                token.ThrowIfCancellationRequested();
                bool fallback;
                await operation.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    using var storage = CacheStorageBudget.ForRoot(location).BeginManagedMutation();
                    ValidateLocation(Path.GetDirectoryName(path)!);
                    RefreshChangedPaths();
                    var size = new FileInfo(temp).Length;
                    Evict(size, path);
                    fallback = LimitBytes == 0 || size > LimitBytes || TrackedUsageBytes > LimitBytes - size;
                    if (!fallback)
                    {
                        lock (leaseGate)
                            if (leases.ContainsKey(path)) throw new IOException("A cached artifact changed while in use.");
                        File.Move(temp, path, true);
                        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                        ObserveFile(path);
                        return Lease(path, false);
                    }
                }
                finally { operation.Release(); }
                var temporary = TemporaryPath(extension);
                try
                {
                    await using (var input = File.OpenRead(temp))
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        131072, FileOptions.Asynchronous))
                    { await input.CopyToAsync(output, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); }
                    token.ThrowIfCancellationRequested();
                    return Lease(temporary, true);
                }
                catch { DeleteTemporary(temporary); throw; }
            }
            finally { DeleteTemporary(temp); }
        }
        finally
        {
            if (pin is not null) await pin.DisposeAsync().ConfigureAwait(false);
            if (entered) flight.Gate.Release();
            lock (leaseGate)
            {
                if (--flight.Users == 0) { flights.Remove(path); flight.Gate.Dispose(); }
                EndAcquisition();
            }
        }
    }

    private async Task<CacheLease> ProduceTemporaryAsync(Func<string, CancellationToken, Task> producer,
        Func<string, CancellationToken, Task<bool>>? validate, string extension, CancellationToken token)
    {
        string path = TemporaryPath(extension);
        try
        {
            await producer(path, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (!File.Exists(path)) throw new IOException("Cache producer did not create a file.");
            if (validate is not null && !await validate(path, token).ConfigureAwait(false))
                throw new InvalidDataException("Cache producer created an invalid artifact.");
            token.ThrowIfCancellationRequested();
            return Lease(path, true);
        }
        catch { DeleteTemporary(path); throw; }
    }
    private static string TemporaryPath(string extension) =>
        Path.Combine(Path.GetTempPath(), "Deviant-preview-" + Guid.NewGuid().ToString("N") + extension);
    private void DeleteTemporary(string path)
    {
        if (!File.Exists(path)) return;
        using var storage = IsLexicallyWithin(path, Location) ? CacheStorageBudget.ForRoot(Location).BeginManagedMutation() : null;
        File.Delete(path);
    }

    private async Task<string> BeginAcquisitionAsync(CancellationToken token)
    {
        while (true)
        {
            await operation.WaitAsync(token).ConfigureAwait(false);
            Task? wait = null;
            bool registered = false;
            try
            {
                lock (leaseGate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    if (maintenanceActive) wait = admission.Task;
                    else
                    {
                        if (acquisitions == 0 && leases.Count == 0) drained = NewSignal();
                        acquisitions++; registered = true;
                    }
                }
                if (wait is null)
                {
                    ValidateLocation(Location);
                    EnsureAccounting(); RefreshChangedPaths();
                    return Location;
                }
            }
            catch
            {
                if (registered) lock (leaseGate) EndAcquisition();
                throw;
            }
            finally { operation.Release(); }
            await wait!.WaitAsync(token).ConfigureAwait(false);
        }
    }
    private void EndAcquisition()
    {
        acquisitions--;
        if (acquisitions == 0 && leases.Count == 0) drained.TrySetResult();
    }
    private CacheLease Lease(string path, bool temporary)
    {
        lock (leaseGate)
        {
            if (acquisitions == 0 && leases.Count == 0) drained = NewSignal();
            leases[path] = leases.GetValueOrDefault(path) + 1;
        }
        return new(path, () =>
        {
            lock (leaseGate)
            {
                if (--leases[path] == 0)
                {
                    leases.Remove(path);
                    if ((temporary || LimitBytes == 0) && File.Exists(path))
                    {
                        try { File.Delete(path); RemoveObservedFile(path); }
                        catch (IOException) { }
                    }
                }
                if (acquisitions == 0 && leases.Count == 0) drained.TrySetResult();
            }
            return ValueTask.CompletedTask;
        });
    }

    private async Task BeginMaintenanceAsync(CancellationToken token)
    {
        await maintenance.WaitAsync(token).ConfigureAwait(false);
        lock (leaseGate) { maintenanceActive = true; admission = NewSignal(); }
    }
    private void EndMaintenance()
    {
        lock (leaseGate) { maintenanceActive = false; admission.TrySetResult(); }
        maintenance.Release();
    }
    private Task WaitForUsersAsync(CancellationToken token)
    {
        lock (leaseGate) return acquisitions == 0 && leases.Count == 0 ? Task.CompletedTask : drained.Task.WaitAsync(token);
    }

    /// <summary>Reconciles externally created, removed or resized files. Normal acquisitions consume only the change journal.</summary>
    public async Task ReconcileAsync(CancellationToken token = default)
    {
        await operation.WaitAsync(token).ConfigureAwait(false);
        try { token.ThrowIfCancellationRequested(); RebuildCatalog(token); CacheStorageBudget.ForRoot(Location).Reconcile(); }
        finally { operation.Release(); }
    }
    private long TrackedUsageBytes { get { lock (leaseGate) return totalBytes; } }
    public long AccountingReconciliations { get { lock (leaseGate) return accountingReconciliations; } }
    private long GetUsage(CacheUsage kind)
    {
        operation.Wait();
        try
        {
            EnsureAccounting(); RefreshChangedPaths();
            lock (leaseGate) return kind switch
            {
                CacheUsage.Preview => previewBytes,
                CacheUsage.Prepared => preparedBytes,
                CacheUsage.Metadata => totalBytes - previewBytes - preparedBytes,
                _ => totalBytes
            };
        }
        finally { operation.Release(); }
    }
    private void EnsureAccounting()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!accountingInitialized) RebuildCatalog(CancellationToken.None);
    }
    private void ResetAccounting()
    {
        lock (leaseGate)
        {
            watcher?.Dispose(); watcher = null;
            catalog.Clear(); knownDirectories.Clear(); changedPaths.Clear(); rescanPaths.Clear();
            totalBytes = previewBytes = preparedBytes = 0;
            accountingInitialized = false; reconcileRequested = false;
        }
    }
    private void RebuildCatalog(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateLocation(Location);
        Directory.CreateDirectory(Location);
        lock (leaseGate)
        {
            if (watcher is null)
            {
                string watchedRoot = Location;
                var owner = new WeakReference<CacheManager>(this);
                watcher = new(watchedRoot)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    InternalBufferSize = 32768
                };
                watcher.Created += (_, e) => { if (owner.TryGetTarget(out var cache)) cache.NoteChangedPath(watchedRoot, e.FullPath, true); };
                watcher.Changed += (_, e) => { if (owner.TryGetTarget(out var cache)) cache.NoteChangedPath(watchedRoot, e.FullPath); };
                watcher.Deleted += (_, e) => { if (owner.TryGetTarget(out var cache)) cache.NoteChangedPath(watchedRoot, e.FullPath, true); };
                watcher.Renamed += (_, e) =>
                {
                    if (owner.TryGetTarget(out var cache))
                    { cache.NoteChangedPath(watchedRoot, e.OldFullPath, true); cache.NoteChangedPath(watchedRoot, e.FullPath, true); }
                };
                watcher.Error += (_, _) =>
                {
                    if (owner.TryGetTarget(out var cache))
                        lock (cache.leaseGate) if (watchedRoot == cache.Location) cache.reconcileRequested = true;
                };
                watcher.EnableRaisingEvents = true;
            }
            catalog.Clear(); knownDirectories.Clear(); changedPaths.Clear(); rescanPaths.Clear();
            totalBytes = previewBytes = preparedBytes = 0;
            reconcileRequested = false; accountingInitialized = false;
        }
        foreach (string file in Files(Location)) { token.ThrowIfCancellationRequested(); ObserveFile(file); }
        lock (leaseGate) { knownDirectories.Add(Location); accountingInitialized = true; accountingReconciliations++; }
    }
    private void NoteChangedPath(string watchedRoot, string path, bool rescan = false)
    {
        if (IsTemporaryEntry(path)) return;
        lock (leaseGate)
        {
            if (disposed || !string.Equals(watchedRoot, Location, StringComparison.OrdinalIgnoreCase)) return;
            if (changedPaths.Count >= 8192) { changedPaths.Clear(); rescanPaths.Clear(); reconcileRequested = true; }
            else { changedPaths.Add(path); if (rescan) rescanPaths.Add(path); }
        }
    }
    private static bool IsTemporaryEntry(string path) => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    private void RefreshChangedPaths()
    {
        string[] paths;
        HashSet<string> rescans;
        bool rebuild;
        lock (leaseGate)
        {
            rebuild = reconcileRequested;
            paths = changedPaths.ToArray(); changedPaths.Clear();
            rescans = new(rescanPaths, StringComparer.OrdinalIgnoreCase); rescanPaths.Clear();
        }
        if (rebuild) { RebuildCatalog(CancellationToken.None); return; }
        foreach (string path in paths)
        {
            if (!IsLexicallyWithin(path, Location)) continue;
            if (Directory.Exists(path))
            {
                bool known;
                lock (leaseGate) known = knownDirectories.Contains(path);
                if (!known || rescans.Contains(path))
                {
                    ValidateLocation(path);
                    var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string file in Files(path)) { present.Add(file); ObserveFile(file); }
                    lock (leaseGate)
                    {
                        knownDirectories.Add(path);
                        foreach (string stale in catalog.Keys.Where(p => IsLexicallyWithin(p, path) && !present.Contains(p)).ToArray())
                            RemoveObservedFile(stale);
                    }
                }
            }
            else
            {
                ObserveFile(path);
                lock (leaseGate)
                {
                    if (knownDirectories.Remove(path))
                    {
                        foreach (string child in catalog.Keys.Where(p => IsLexicallyWithin(p, path)).ToArray())
                            RemoveObservedFile(child);
                        knownDirectories.RemoveWhere(p => IsLexicallyWithin(p, path));
                    }
                }
            }
        }
    }
    private bool ObserveFile(string path)
    {
        if (IsTemporaryEntry(path)) return false;
        var info = new FileInfo(path);
        if (!info.Exists) { lock (leaseGate) RemoveObservedFile(path); return false; }
        ValidateLocation(Path.GetDirectoryName(path)!);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Cache files cannot be symbolic links.");
        var entry = new CacheFile(info.Length, info.LastWriteTimeUtc,
            IsLexicallyWithin(path, Path.Combine(Location, "previews-v1")),
            IsLexicallyWithin(path, Path.Combine(Location, "prepared-v1")));
        lock (leaseGate)
        {
            RemoveObservedFile(path); catalog.Add(path, entry);
            totalBytes += entry.Length;
            if (entry.Preview) previewBytes += entry.Length;
            if (entry.Prepared) preparedBytes += entry.Length;
            string? directory = Path.GetDirectoryName(path);
            while (directory is not null && IsLexicallyWithin(directory, Location))
            { knownDirectories.Add(directory); directory = Path.GetDirectoryName(directory); }
        }
        return true;
    }
    private void RemoveObservedFile(string path)
    {
        if (!catalog.Remove(path, out var entry)) return;
        totalBytes -= entry.Length;
        if (entry.Preview) previewBytes -= entry.Length;
        if (entry.Prepared) preparedBytes -= entry.Length;
    }
    private static bool IsLexicallyWithin(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
    private void DeleteEntry(string path)
    {
        using var storage = CacheStorageBudget.ForRoot(Location).BeginManagedMutation();
        lock (leaseGate)
        {
            if (leases.ContainsKey(path)) throw new IOException("A cached artifact is in use.");
            ValidateLocation(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) File.Delete(path);
            RemoveObservedFile(path);
        }
    }
    private void Evict(long required, string? exclude)
    {
        EnsureAccounting(); RefreshChangedPaths();
        if (required <= LimitBytes && TrackedUsageBytes <= LimitBytes - required) return;
        KeyValuePair<string, CacheFile>[] candidates;
        lock (leaseGate) candidates = catalog.Where(p => (p.Value.Preview || p.Value.Prepared) && p.Key != exclude)
            .OrderBy(p => p.Value.LastWriteUtc).ToArray();
        foreach (var candidate in candidates)
        {
            if (required <= LimitBytes && TrackedUsageBytes <= LimitBytes - required) break;
            lock (leaseGate) if (leases.ContainsKey(candidate.Key)) continue;
            ObserveFile(candidate.Key);
            bool exists;
            lock (leaseGate) exists = catalog.ContainsKey(candidate.Key);
            if (exists) DeleteEntry(candidate.Key);
        }
    }
    public void Dispose()
    {
        lock (leaseGate)
        {
            if (!disposed) { disposed = true; watcher?.Dispose(); watcher = null; }
        }
        storagePolicy.Dispose();
        GC.SuppressFinalize(this);
    }
    ~CacheManager() { watcher?.Dispose(); }
}
