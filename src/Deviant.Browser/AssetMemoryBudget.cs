using Deviant.Core;

namespace Deviant.Browser;

/// <summary>Accounts for retained arrays, decode scratch, and upload copies in an asset session.</summary>
public sealed class AssetMemoryBudget
{
    public const long DefaultMaximumBytes = 512L * 1024 * 1024;
    private readonly object sync = new();
    private readonly Dictionary<string, long> categoryUsage = new(StringComparer.Ordinal);
    private long used;
    private long peak;
    private long maximumBytes;
    private int extendedLimitLeases;
    private long normalMaximumBytes;

    public AssetMemoryBudget(long maximumBytes = DefaultMaximumBytes)
    {
        if (maximumBytes < 1 || maximumBytes > 2L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        this.maximumBytes = maximumBytes;
        normalMaximumBytes = maximumBytes;
    }

    public long MaximumBytes { get { lock (sync) return maximumBytes; } }
    public long UsedBytes { get { lock (sync) return used; } }
    public long PeakBytes { get { lock (sync) return peak; } }
    public IReadOnlyDictionary<string, long> CategoryUsage
    {
        get { lock (sync) return new Dictionary<string, long>(categoryUsage, StringComparer.Ordinal); }
    }

    public Reservation Reserve(long bytes, string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (sync)
        {
            if (bytes > maximumBytes - used)
            {
                var error = new ArchiveException("AssetMemoryLimit", $"The {category} exceeds the asset memory limit.");
                error.Data["MemoryAdmission"] = new AssetMemoryAdmission(category, bytes, used, maximumBytes,
                    new Dictionary<string, long>(categoryUsage, StringComparer.Ordinal));
                throw error;
            }
            used += bytes;
            if (bytes > 0) categoryUsage[category] = checked(categoryUsage.GetValueOrDefault(category) + bytes);
            peak = Math.Max(peak, used);
            return new Reservation(this, bytes, category);
        }
    }

    /// <summary>Temporarily raises the session budget for an explicitly accepted large scene load.</summary>
    public IDisposable AllowLargeSceneLoad(long maximumBytes = 2L * 1024 * 1024 * 1024)
    {
        if (maximumBytes < 1 || maximumBytes > 2L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        lock (sync)
        {
            if (extendedLimitLeases == 0) normalMaximumBytes = this.maximumBytes;
            this.maximumBytes = Math.Max(this.maximumBytes, maximumBytes);
            extendedLimitLeases++;
        }
        return new LimitLease(this);
    }

    private void ReleaseLimitLease()
    {
        lock (sync)
        {
            if (extendedLimitLeases == 0) return;
            if (--extendedLimitLeases == 0) maximumBytes = normalMaximumBytes;
        }
    }

    private sealed class LimitLease(AssetMemoryBudget owner) : IDisposable
    {
        private AssetMemoryBudget? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.ReleaseLimitLease();
    }

    public sealed class Reservation : IDisposable
    {
        private readonly AssetMemoryBudget owner;
        private long bytes;
        private readonly string category;
        private bool disposed;
        internal Reservation(AssetMemoryBudget owner, long bytes, string category)
        { this.owner = owner; this.bytes = bytes; this.category = category; }
        public long Bytes { get { lock (owner.sync) return bytes; } }

        public void ReduceTo(long retainedBytes)
        {
            lock (owner.sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (retainedBytes < 0 || retainedBytes > bytes) throw new ArgumentOutOfRangeException(nameof(retainedBytes));
                owner.used -= bytes - retainedBytes;
                ReleaseCategory(bytes - retainedBytes);
                bytes = retainedBytes;
            }
        }

        public void Dispose()
        {
            lock (owner.sync)
            {
                if (disposed) return;
                disposed = true;
                owner.used -= bytes;
                ReleaseCategory(bytes);
                bytes = 0;
            }
        }

        private void ReleaseCategory(long released)
        {
            if (released == 0) return;
            var remaining = owner.categoryUsage[category] - released;
            if (remaining == 0) owner.categoryUsage.Remove(category);
            else owner.categoryUsage[category] = remaining;
        }
    }
}

public sealed record AssetMemoryAdmission(string Category, long RequestedBytes, long UsedBytes,
    long MaximumBytes, IReadOnlyDictionary<string, long> Categories);
