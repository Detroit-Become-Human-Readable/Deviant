using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;

namespace Deviant.Catalog;

/// <summary>Coordinates file growth for a cache root, including separate SQLite connections and processes.</summary>
internal sealed class CacheStorageBudget
{
    private const long DefaultLimit = 20L * 1024 * 1024 * 1024;
    private const long Magic = 0x4456434255444731;
    private const int SlotCount = 64;
    private const int SlotStart = 64;
    private const int SlotSize = 32;
    private const int StateSize = SlotStart + SlotCount * SlotSize;
    private const long MaximumReadCoordinationBytes = 64L * 1024 * 1024;
    private static readonly object registryGate = new();
    private static readonly Dictionary<string, CacheStorageBudget> roots = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly long processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    private readonly string root;
    private readonly Mutex mutex;
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor state;
    private readonly object managerGate = new();
    private readonly Dictionary<long, long> managers = [];
    private readonly Dictionary<string, (long Epoch, long Length)> fileLengths = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, long> unlinkedCharges = new(StringComparer.Ordinal);
    private readonly int slot;
    private long nextManager;
    private string? vfs;
    [ThreadStatic] private static (long Used, long Limit, long Requested)? lastDenial;

    internal static void ClearFailure() => lastDenial = null;
    internal static IOException StorageFailure(Exception cause)
    {
        var denial = lastDenial;
        lastDenial = null;
        var failure = new IOException(denial is null ? "The database could not grow." : "Cache storage limit reached.", cause);
        if (denial is { } values)
        {
            failure.Data["ErrorCode"] = "CacheStorageLimit";
            failure.Data["UsedBytes"] = values.Used; failure.Data["LimitBytes"] = values.Limit;
            failure.Data["RequestedBytes"] = values.Requested;
        }
        return failure;
    }

    private CacheStorageBudget(string root)
    {
        this.root = root;
        var canonical = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        mutex = new Mutex(false, "Deviant.Storage.Lock." + key);
        if (OperatingSystem.IsWindows())
            mapping = MemoryMappedFile.CreateOrOpen("Deviant.Storage.State." + key, StateSize, MemoryMappedFileAccess.ReadWrite);
        else
        {
            var directory = Path.Combine(Path.GetTempPath(), "DeviantStorageCoordination");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, key + ".state");
            using (var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
                if (file.Length < StateSize) file.SetLength(StateSize);
            mapping = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, StateSize, MemoryMappedFileAccess.ReadWrite);
        }
        state = mapping.CreateViewAccessor(0, StateSize, MemoryMappedFileAccess.ReadWrite);
        Enter();
        try
        {
            if (state.ReadInt64(0) != Magic)
            {
                state.Write(8, MeasureRoot());
                state.Write(16, DefaultLimit);
                state.Write(24, 0L);
                state.Write(0, Magic);
            }
            ReapProcesses();
            slot = FindProcessSlot();
            if (slot < 0) throw new IOException("Too many processes are using this cache.");
            state.Write(slot, Environment.ProcessId);
            state.Write(slot + 8, processStart);
            state.Write(slot + 16, long.MaxValue);
        }
        finally { mutex.ReleaseMutex(); }
    }

    internal static CacheStorageBudget ForRoot(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        lock (registryGate)
        {
            if (!roots.TryGetValue(root, out var budget)) roots.Add(root, budget = new(root));
            return budget;
        }
    }

    internal static ManagerLease AttachManager(string root, long limit) => ForRoot(root).AddManager(limit);

    internal string Vfs
    {
        get
        {
            lock (managerGate) return vfs ??= CacheStorageVfs.Register(root, Acquire);
        }
    }

    internal long Limit
    {
        get { Enter(); try { ReapProcesses(); return EffectiveLimit(); } finally { mutex.ReleaseMutex(); } }
    }

    internal void Reconcile()
    {
        Enter();
        try { state.Write(8, MeasureRoot()); state.Write(24, 0L); AdvanceEpoch(); }
        finally { mutex.ReleaseMutex(); }
    }

    internal IDisposable BeginManagedMutation()
    {
        Enter();
        try
        {
            state.Write(8, MeasureRoot()); state.Write(24, 0L); AdvanceEpoch();
            return new ManagedMutation(this);
        }
        catch { mutex.ReleaseMutex(); throw; }
    }

    private ManagerLease AddManager(long limit)
    {
        lock (managerGate)
        {
            var id = ++nextManager;
            managers.Add(id, limit);
            try { PublishManagerPolicy(); }
            catch { managers.Remove(id); throw; }
            return new(this, id);
        }
    }

    private void UpdateManager(long id, long? limit)
    {
        lock (managerGate)
        {
            if (!managers.TryGetValue(id, out var previous)) return;
            if (limit is { } value) managers[id] = value;
            else managers.Remove(id);
            try { PublishManagerPolicy(); }
            catch { managers[id] = previous; throw; }
        }
    }

    private void PublishManagerPolicy()
    {
        Enter();
        try
        {
            ReapProcesses();
            var measured = MeasureRoot();
            var limit = managers.Count == 0 ? long.MaxValue : managers.Values.Min();
            state.Write(slot + 4, managers.Count);
            state.Write(slot + 16, limit);
            if (limit != long.MaxValue) state.Write(16, limit);
            state.Write(8, measured);
            state.Write(24, 0L);
            AdvanceEpoch();
        }
        finally { mutex.ReleaseMutex(); }
    }

    private long EffectiveLimit()
    {
        var limit = long.MaxValue;
        for (int index = 0; index < SlotCount; index++)
        {
            var at = SlotStart + index * SlotSize;
            if (state.ReadInt32(at) != 0 && state.ReadInt32(at + 4) > 0)
                limit = Math.Min(limit, state.ReadInt64(at + 16));
        }
        return limit == long.MaxValue ? state.ReadInt64(16) : limit;
    }

    internal ICacheStorageWriteLease? Acquire(string path, long previousLength, long requestedLength)
    {
        path = Path.GetFullPath(path);
        // The VFS validates reparse ancestors at open. Do not walk the filesystem
        // again for every SQLite page write; the admitted handle remains pinned.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison)) return null;
        Enter();
        var transferred = false;
        try
        {
            if (state.ReadInt64(24) != 0) { state.Write(8, MeasureRoot()); state.Write(24, 0L); AdvanceEpoch(); }
            // Another connection may have extended a shared sidecar before this lock was acquired.
            bool unlinked = IsUnlinkedTemporary(path);
            long epoch = state.ReadInt64(32);
            if (!unlinked && fileLengths.TryGetValue(path, out var cached) && cached.Epoch == epoch)
                previousLength = cached.Length;
            else
            {
                try { previousLength = new FileInfo(path).Length; unlinked = false; }
                catch (FileNotFoundException) { if (!unlinked) previousLength = 0; }
                catch (DirectoryNotFoundException) { if (!unlinked) previousLength = 0; }
            }
            if (unlinked) previousLength = unlinkedCharges.GetValueOrDefault(path, previousLength);
            var used = state.ReadInt64(8);
            var limit = EffectiveLimit();
            var growth = Math.Max(0, checked(requestedLength - previousLength));
            bool Admitted(long candidateLimit)
            {
                var coordination = path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase);
                var headroom = Math.Min(128L * 1024 * 1024, candidateLimit / 8);
                var admissionLimit = coordination ? candidateLimit : Math.Max(0, candidateLimit - headroom);
                // Existing over-budget metadata still needs its WAL index for read recovery.
                var requiredReadCoordination = coordination && used > candidateLimit && requestedLength <= MaximumReadCoordinationBytes;
                return growth == 0 || requiredReadCoordination || growth <= admissionLimit && used <= admissionLimit - growth;
            }
            if (!Admitted(limit))
            {
                ReapProcesses();
                var currentLimit = EffectiveLimit();
                if (!Admitted(currentLimit)) { lastDenial = (used, currentLimit, growth); return null; }
            }
            state.Write(8, checked(used + growth));
            transferred = true;
            return new WriteLease(this, path, previousLength, growth, unlinked);
        }
        finally { if (!transferred) mutex.ReleaseMutex(); }
    }

    private static bool IsUnlinkedTemporary(string path) => !OperatingSystem.IsWindows() &&
        Path.GetFileName(path).StartsWith(".sqlite-temp-", StringComparison.Ordinal);

    private long AdvanceEpoch()
    {
        long epoch = unchecked(state.ReadInt64(32) + 1);
        state.Write(32, epoch);
        return epoch;
    }

    private void Enter()
    {
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(2))) throw new IOException("Cache storage is busy. Retry the operation.");
        }
        catch (AbandonedMutexException)
        {
            state.Write(24, 1L);
        }
    }

    private long MeasureRoot()
    {
        long bytes = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        foreach (var path in Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", options) : [])
        {
            try { bytes = checked(bytes + new FileInfo(path).Length); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        for (int index = 0; index < SlotCount; index++)
        {
            int at = SlotStart + index * SlotSize;
            if (state.ReadInt32(at) != 0) bytes = checked(bytes + state.ReadInt64(at + 24));
        }
        return bytes;
    }

    private void ReapProcesses()
    {
        for (int index = 0; index < SlotCount; index++)
        {
            var at = SlotStart + index * SlotSize;
            var pid = state.ReadInt32(at);
            if (pid == 0 || pid == Environment.ProcessId && state.ReadInt64(at + 8) == processStart) continue;
            var dead = false;
            try
            {
                using var process = Process.GetProcessById(pid);
                dead = process.HasExited || process.StartTime.ToUniversalTime().Ticks != state.ReadInt64(at + 8);
            }
            catch (ArgumentException) { dead = true; }
            catch (InvalidOperationException) { dead = true; }
            catch (System.ComponentModel.Win32Exception) { }
            if (dead)
            {
                if (state.ReadInt64(at + 24) != 0) state.Write(24, 1L);
                state.Write(at, 0); state.Write(at + 4, 0); state.Write(at + 16, long.MaxValue); state.Write(at + 24, 0L);
            }
        }
    }

    private int FindProcessSlot()
    {
        var empty = -1;
        for (int index = 0; index < SlotCount; index++)
        {
            var at = SlotStart + index * SlotSize;
            var pid = state.ReadInt32(at);
            if (pid == Environment.ProcessId && state.ReadInt64(at + 8) == processStart) return at;
            if (pid == 0 && empty < 0) empty = at;
        }
        return empty;
    }

    internal sealed class ManagerLease(CacheStorageBudget owner, long id) : IDisposable
    {
        private readonly object gate = new();
        private CacheStorageBudget? current = owner;
        internal void UpdateLimit(long limit) { lock (gate) current?.UpdateManager(id, limit); }
        public void Dispose()
        {
            lock (gate)
            {
                if (current is null) return;
                current.UpdateManager(id, null);
                current = null;
            }
        }
    }

    private sealed class WriteLease(CacheStorageBudget owner, string path, long previousLength, long reservation, bool unlinked) : ICacheStorageWriteLease
    {
        private CacheStorageBudget? current = owner;
        private bool complete;
        public long ReservedLength => checked(previousLength + reservation);
        public void Complete(long actualLength)
        {
            var budget = current ?? throw new ObjectDisposedException(nameof(WriteLease));
            if (complete) throw new InvalidOperationException("Storage write was already reconciled.");
            budget.state.Write(8, checked(budget.state.ReadInt64(8) + actualLength - previousLength - reservation));
            if (unlinked)
            {
                budget.state.Write(budget.slot + 24, checked(budget.state.ReadInt64(budget.slot + 24) + actualLength - previousLength));
                if (actualLength == 0) budget.unlinkedCharges.Remove(path);
                else budget.unlinkedCharges[path] = actualLength;
            }
            long epoch = budget.AdvanceEpoch();
            if (!unlinked)
            {
                if (budget.fileLengths.Count >= 1024) budget.fileLengths.Clear();
                budget.fileLengths[path] = (epoch, actualLength);
            }
            complete = true;
        }
        public void Dispose()
        {
            var budget = Interlocked.Exchange(ref current, null);
            if (budget is null) return;
            try
            {
                if (!complete)
                {
                    if (unlinked)
                    {
                        budget.state.Write(budget.slot + 24, checked(budget.state.ReadInt64(budget.slot + 24) + reservation));
                        budget.unlinkedCharges[path] = checked(previousLength + reservation);
                    }
                    budget.state.Write(24, 1L);
                }
            }
            finally { budget.mutex.ReleaseMutex(); }
        }
    }

    private sealed class ManagedMutation(CacheStorageBudget owner) : IDisposable
    {
        private CacheStorageBudget? current = owner;
        public void Dispose()
        {
            var budget = Interlocked.Exchange(ref current, null);
            if (budget is null) return;
            try { budget.state.Write(8, budget.MeasureRoot()); budget.state.Write(24, 0L); budget.AdvanceEpoch(); }
            catch { budget.state.Write(24, 1L); throw; }
            finally { budget.mutex.ReleaseMutex(); }
        }
    }
}
