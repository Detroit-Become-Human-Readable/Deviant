using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Browser;

/// <summary>Retains intrinsic metadata during one serialized region-discovery pass.</summary>
internal sealed class SceneMetadataLeaseCache : IDisposable
{
    internal const int MaximumEntries = 128;
    internal const long MaximumRetainedBytes = 64L * 1024 * 1024;
    private const long IndexBytes = 16 * 1024;
    private readonly ArchiveSnapshot snapshot;
    private readonly AssetMemoryBudget budget;
    private readonly bool allowLargeData;
    private readonly int maximumEntries;
    private readonly long maximumRetainedBytes;
    private Dictionary<ResourceIdentity, Entry> entries = [];
    private readonly LinkedList<ResourceIdentity> order = [];
    private AssetMemoryBudget.Reservation? indexMemory;
    private long retainedBytes;
    private long peakRetainedBytes;
    private int peakEntries;
    private long hits, misses, sourceChecks, evictions, retries, unreliableBypasses, uncachedReads;
    private bool disposed;

    internal SceneMetadataLeaseCache(ArchiveSnapshot snapshot, AssetMemoryBudget budget, bool allowLargeData,
        int maximumEntries = MaximumEntries, long maximumRetainedBytes = MaximumRetainedBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(budget);
        if (maximumEntries is < 1 or > MaximumEntries) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (maximumRetainedBytes is < 1 or > MaximumRetainedBytes) throw new ArgumentOutOfRangeException(nameof(maximumRetainedBytes));
        this.snapshot = snapshot; this.budget = budget; this.allowLargeData = allowLargeData;
        this.maximumEntries = maximumEntries; this.maximumRetainedBytes = maximumRetainedBytes;
    }

    internal int Count => entries.Count;
    internal long RetainedBytes => retainedBytes + (indexMemory?.Bytes ?? 0);

    internal async Task<SceneMetadataUse> AcquireAsync(ResourceIdentity identity, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this); token.ThrowIfCancellationRequested();
        if (identity.IndexContentId != snapshot.IndexContentId || identity.MemberPath is not null ||
            identity.Ordinal < 0 || identity.Ordinal >= snapshot.Records.Count)
            throw new ArgumentException("Select a native container from this archive.", nameof(identity));
        var row = snapshot.RequireRecord(identity.Ordinal);
        bool reliable = snapshot.IndexStamp.Reliable &&
            snapshot.Volumes.SingleOrDefault(item => item.Package == row.Package)?.Stamp.Reliable == true;
        if (reliable && entries.TryGetValue(identity, out var cached))
        {
            cached.Owner.EnsureUnchanged(); sourceChecks++;
            token.ThrowIfCancellationRequested();
            var use = Borrow(cached);
            order.Remove(cached.Node); order.AddLast(cached.Node);
            hits++; AssetLoadDiagnostics.Cache("RegionMetadataCache", identity.Key, true);
            return use;
        }
        if (!reliable) unreliableBypasses++;
        misses++; AssetLoadDiagnostics.Cache("RegionMetadataCache", identity.Key, false);
        NativeContainerLease owner;
        try { owner = await OpenAsync(identity, token).ConfigureAwait(false); }
        catch (ArchiveException error) when (error.Code == "AssetMemoryLimit" && EvictIdle() > 0)
        {
            token.ThrowIfCancellationRequested(); retries++;
            owner = await OpenAsync(identity, token).ConfigureAwait(false);
        }
        try
        {
            token.ThrowIfCancellationRequested(); owner.EnsureUnchanged();
            if (reliable && TryRetain(identity, owner, out var retained)) return Borrow(retained!);
            uncachedReads++;
            return new(owner, owner);
        }
        catch { owner.Dispose(); throw; }
    }

    private async Task<NativeContainerLease> OpenAsync(ResourceIdentity identity, CancellationToken token)
    {
        using var timing = AssetLoadDiagnostics.Measure("RegionMetadataOpen", identity.Key);
        return await NativeContainerLease.OpenMetadataAsync(snapshot, identity, budget, token, allowLargeData).ConfigureAwait(false);
    }

    private bool TryRetain(ResourceIdentity identity, NativeContainerLease owner, out Entry? retained)
    {
        retained = null;
        long wrapperBytes = checked(1024L + identity.Key.Length * 2L + owner.SourceExtents.Count * 256L);
        long bytes = checked(owner.RetainedReservationBytes + wrapperBytes);
        if (bytes > maximumRetainedBytes - IndexBytes) return false;
        while (entries.Count >= maximumEntries || bytes > maximumRetainedBytes - RetainedBytes)
            if (!EvictOldestIdle()) return false;
        AssetMemoryBudget.Reservation? wrapper = null;
        try
        {
            indexMemory ??= budget.Reserve(IndexBytes, "scene metadata cache index");
            wrapper = budget.Reserve(wrapperBytes, "scene metadata cache entry");
        }
        catch (ArchiveException error) when (error.Code == "AssetMemoryLimit")
        {
            wrapper?.Dispose();
            EvictIdle();
            // The successful read remains usable even when retaining its wrapper is not admitted.
            ReleaseEmptyIndex();
            return false;
        }
        var node = order.AddLast(identity);
        retained = new(owner, wrapper, bytes, node);
        entries.Add(identity, retained); retainedBytes += bytes;
        peakRetainedBytes = Math.Max(peakRetainedBytes, RetainedBytes);
        peakEntries = Math.Max(peakEntries, entries.Count);
        return true;
    }

    private SceneMetadataUse Borrow(Entry entry)
    {
        var lifetime = entry.Owner.AcquireLease(); entry.Borrows++;
        return new(entry.Owner, new BorrowedEntry(entry, lifetime));
    }

    internal int EvictIdle()
    {
        int removed = 0;
        while (EvictOldestIdle()) removed++;
        ReleaseEmptyIndex();
        return removed;
    }

    private bool EvictOldestIdle()
    {
        for (var node = order.First; node is not null; node = node.Next)
        {
            var entry = entries[node.Value];
            if (entry.Borrows != 0) continue;
            Retire(entry); return true;
        }
        return false;
    }

    private void Retire(Entry entry, bool eviction = true)
    {
        entries.Remove(entry.Node.Value); order.Remove(entry.Node); retainedBytes -= entry.Bytes;
        entry.Retired = true; entry.Owner.Dispose();
        if (entry.Borrows == 0) entry.Wrapper.Dispose();
        if (eviction) evictions++;
    }

    private void ReleaseEmptyIndex()
    {
        if (entries.Count != 0) return;
        entries = []; indexMemory?.Dispose(); indexMemory = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        while (order.First is { } node) Retire(entries[node.Value], eviction: false);
        ReleaseEmptyIndex();
        AssetLoadDiagnostics.Counters("RegionMetadataCache", snapshot.IndexContentId, new Dictionary<string, long>
        {
            ["Hits"] = hits, ["Misses"] = misses, ["ReliableHitSourceChecks"] = sourceChecks,
            ["Evictions"] = evictions, ["AdmissionRetries"] = retries, ["UnreliableBypasses"] = unreliableBypasses,
            ["UncachedReads"] = uncachedReads, ["RetainedBytes"] = RetainedBytes,
            ["PeakRetainedBytes"] = peakRetainedBytes, ["Entries"] = entries.Count, ["PeakEntries"] = peakEntries
        });
    }

    private sealed class Entry(NativeContainerLease owner, AssetMemoryBudget.Reservation wrapper, long bytes,
        LinkedListNode<ResourceIdentity> node)
    {
        internal NativeContainerLease Owner { get; } = owner;
        internal AssetMemoryBudget.Reservation Wrapper { get; } = wrapper;
        internal long Bytes { get; } = bytes;
        internal LinkedListNode<ResourceIdentity> Node { get; } = node;
        internal int Borrows;
        internal bool Retired;
    }

    private sealed class BorrowedEntry(Entry entry, IDisposable lifetime) : IDisposable
    {
        private IDisposable? current = lifetime;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref current, null) is not { } released) return;
            released.Dispose(); entry.Borrows--;
            if (entry.Retired && entry.Borrows == 0) entry.Wrapper.Dispose();
        }
    }
}

internal sealed class SceneMetadataUse(NativeContainerLease owner, IDisposable lifetime) : IDisposable
{
    private IDisposable? current = lifetime;
    private NativeContainerLease Owner
    { get { ObjectDisposedException.ThrowIf(current is null, this); return owner; } }
    internal NativeObjectDirectoryDocument Directory => Owner.Directory;
    internal IReadOnlyList<SourceExtent> SourceExtents => Owner.SourceExtents;
    internal void EnsureUnchanged() => Owner.EnsureUnchanged();
    public void Dispose() => Interlocked.Exchange(ref current, null)?.Dispose();
}
