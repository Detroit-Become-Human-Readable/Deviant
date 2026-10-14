using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Deviant.Core;
using Microsoft.Win32.SafeHandles;

namespace Deviant.Assets;

/// <summary>Shares pinned source handles, lookups and verified ranges for one open workspace.</summary>
public sealed class ArchiveSourceSession : IDisposable
{
    private static readonly ConditionalWeakTable<ArchiveSnapshot, Slot> sessions = new();
    private readonly ArchiveSnapshot snapshot;
    private readonly ISourceRevisionProvider revisions;
    private readonly Func<long, string, IDisposable>? reserve;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim io = new(4, 4);
    private readonly object gate = new();
    private readonly Dictionary<uint, Pin> volumes = [];
    private readonly ConcurrentDictionary<RangeKey, Lazy<Task>> verified = new();
    private readonly ConcurrentDictionary<ReadKey, SharedRead> reads = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> projections = new(StringComparer.Ordinal);
    private readonly List<IDisposable> projectionMemory = [];
    private long projectionBytes;
    private readonly Lazy<Task> indexValidation;
    private Dictionary<uint, ResourceRecord[]>? ids;
    private IDisposable? lookupMemory;
    private Pin? index;
    private int owners;
    private int producers;
    private bool disposed;

    private ArchiveSourceSession(ArchiveSnapshot snapshot, Func<long, string, IDisposable>? reserve,
        ISourceRevisionProvider? revisions)
    {
        this.snapshot = snapshot; this.reserve = reserve;
        this.revisions = revisions ?? new FileRevisionProvider();
        indexValidation = new(() => RunProducerAsync(() => ValidateIndexCoreAsync(lifetime.Token)), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static Lease Acquire(ArchiveSnapshot snapshot, Func<long, string, IDisposable>? reserve = null,
        ISourceRevisionProvider? revisions = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var slot = sessions.GetValue(snapshot, _ => new());
        lock (slot)
        {
            if (slot.Session is null || slot.Session.disposed) slot.Session = new(snapshot, reserve, revisions);
            slot.Session.owners++;
            return new(slot, slot.Session);
        }
    }

    public static Lease? TryAcquire(ArchiveSnapshot snapshot)
    {
        if (!sessions.TryGetValue(snapshot, out var slot)) return null;
        lock (slot)
        {
            if (slot.Session is not { disposed: false } session) return null;
            session.owners++;
            return new(slot, session);
        }
    }

    public ResourceRecord FindRecord(long ordinal)
        => snapshot.RequireRecord(ordinal);

    public ResourceRecord[] FindCandidates(uint id, uint? type = null)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureLookups();
            var matching = ids!.GetValueOrDefault(id) ?? [];
            return type is null || matching.All(row => row.Type == type) ? matching : matching.Where(row => row.Type == type).ToArray();
        }
    }

    public async Task<T> GetProjectionAsync<T>(string key,
        Func<CancellationToken, Task<(T Value, long RetainedBytes)>> producer, CancellationToken token = default) where T : class
    {
        token.ThrowIfCancellationRequested(); ThrowIfDisposed();
        var entry = projections.GetOrAdd(key, cacheKey => new(() => RunProducerAsync(async () =>
        {
            var result = await producer(lifetime.Token).ConfigureAwait(false);
            lock (gate)
            {
                ThrowIfDisposed();
                if (result.RetainedBytes < 0 || result.RetainedBytes > 64L * 1024 * 1024 - projectionBytes)
                    projections.TryRemove(key, out _);
                else
                {
                    var held = reserve?.Invoke(result.RetainedBytes, "archive metadata projection");
                    if (held is not null) projectionMemory.Add(held);
                    projectionBytes += result.RetainedBytes;
                }
            }
            return (object)result.Value;
        }), LazyThreadSafetyMode.ExecutionAndPublication));
        try { return (T)await entry.Value.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            if (entry.IsValueCreated && entry.Value.IsCompleted && !entry.Value.IsCompletedSuccessfully)
                projections.TryRemove(new KeyValuePair<string, Lazy<Task<object>>>(key, entry));
            throw;
        }
    }

    public ReferenceResolution Resolve(ResourceReference reference)
    {
        if (reference.Namespace != IdentifierNamespace.Archive || !uint.TryParse(reference.Id, out var id))
            return new(ReferenceStatus.Unresolved, [], "This identifier requires a format-specific resolver.");
        ResourceRecord[] matching;
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureLookups();
            matching = ids!.GetValueOrDefault(id) ?? [];
        }
        var candidates = matching.Where(row => reference.ResourceType is null || row.Type == reference.ResourceType)
            .Select(row => new ResourceIdentity(snapshot.IndexContentId, row.Ordinal)).ToArray();
        return new(candidates.Length switch { 0 => ReferenceStatus.Missing, 1 => ReferenceStatus.Unique, _ => ReferenceStatus.Ambiguous }, candidates);
    }

    private void EnsureLookups()
    {
        if (ids is not null) return;
        var held = reserve?.Invoke(checked(4096L + snapshot.Records.Count * 128L), "archive source lookups");
        try
        {
            ids = snapshot.Records.GroupBy(row => row.Id).ToDictionary(group => group.Key, group => group.ToArray());
            lookupMemory = held;
        }
        catch { held?.Dispose(); throw; }
    }

    public async Task ValidateIndexAsync(CancellationToken token = default)
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        await indexValidation.Value.WaitAsync(token).ConfigureAwait(false);
        EnsureIndexCurrent();
        if (!snapshot.IndexStamp.Reliable)
        {
            var held = GetIndex();
            using var scratch = reserve?.Invoke(128 * 1024, "unreliable index verification");
            using var timing = AssetLoadDiagnostics.Measure("VerificationRead", relativeSource: Path.GetFileName(snapshot.IndexPath), requestedBytes: snapshot.IndexStamp.Length);
            if (await SourceIdentity.HashAsync(held.Handle, held.Stamp.Length, token).ConfigureAwait(false) != snapshot.IndexContentId)
                throw Changed();
            EnsureIndexCurrent();
        }
    }

    private async Task ValidateIndexCoreAsync(CancellationToken token)
    {
        var held = GetIndex();
        using var scratch = reserve?.Invoke(128 * 1024, "archive index verification");
        using var timing = AssetLoadDiagnostics.Measure("VerificationRead", relativeSource: Path.GetFileName(snapshot.IndexPath), requestedBytes: snapshot.IndexStamp.Length);
        SourceIdentity.EnsureUnchanged(revisions, held.Handle, held.Path, held.Stamp);
        if (await SourceIdentity.HashAsync(held.Handle, held.Stamp.Length, token).ConfigureAwait(false) != snapshot.IndexContentId)
            throw Changed();
        SourceIdentity.EnsureUnchanged(revisions, held.Handle, held.Path, held.Stamp);
    }

    public async Task<byte[]> ReadAsync(ResourceIdentity identity, long resourceOffset, int length, CancellationToken token = default)
    {
        if (identity.IndexContentId != snapshot.IndexContentId || identity.MemberPath is not null)
            throw new ArchiveException("InvalidIdentity", "Resource does not belong to this archive.");
        var row = FindRecord(identity.Ordinal);
        if (length < 0 || length > 16 * 1024 * 1024 || resourceOffset < 0 || resourceOffset > row.StoredSize - length)
            throw new ArchiveException("InvalidRange", "Read exceeds the resource extent.");
        await ValidateIndexAsync(token).ConfigureAwait(false);
        var key = new ReadKey(identity.Ordinal, resourceOffset, length);
        SharedRead producer;
        while (true)
        {
            producer = reads.GetOrAdd(key, _ => new(() => RunProducerAsync(() => ReadCoreAsync(row, resourceOffset, length, lifetime.Token))));
            if (producer.TryJoin()) break;
            reads.TryRemove(new KeyValuePair<ReadKey, SharedRead>(key, producer));
        }
        AssetLoadDiagnostics.Cache("ArchiveReadShare", identity.Key, producer.Value.IsValueCreated);
        try
        {
            // The public resolver returns writable buffers, so callers receive independent ownership.
            var data = await producer.Value.Value.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); EnsureCurrent(row.Package);
            return data.Bytes.ToArray();
        }
        finally
        {
            if (producer.Leave()) reads.TryRemove(new KeyValuePair<ReadKey, SharedRead>(key, producer));
        }
    }

    private async Task<SharedBuffer> ReadCoreAsync(ResourceRecord row, long offset, int length, CancellationToken token)
    {
        await io.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var memory = reserve?.Invoke(checked(length + 4096L), "shared archive read");
            try
            {
            var pin = GetVolume(row.Package); EnsureCurrent(row.Package);
            using var timing = AssetLoadDiagnostics.Measure("ArchiveRead", new ResourceIdentity(snapshot.IndexContentId, row.Ordinal).Key,
                pin.RelativeName, checked(row.Offset + offset), length);
            var data = new byte[length]; int read = 0;
            while (read < data.Length)
            {
                int count = await RandomAccess.ReadAsync(pin.Handle, data.AsMemory(read), checked(row.Offset + offset + read), token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException();
                read += count;
            }
            EnsureCurrent(row.Package); return new(data, memory);
            }
            catch { memory?.Dispose(); throw; }
        }
        finally { io.Release(); }
    }

    public async Task VerifyAsync(IReadOnlyList<SourceExtent> extents, CancellationToken token,
        long maximumBytes = SourceExtentVerifier.DefaultMaximumBytes,
        int maximumExtentCount = SourceExtentVerifier.MaximumExtentCount)
    {
        if (maximumBytes is < 1 or > SourceExtentVerifier.MaximumBytes ||
            maximumExtentCount is < 1 or > SourceExtentVerifier.MaximumLargeExtentCount)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (extents.Count is < 1 || extents.Count > maximumExtentCount) throw new ArgumentException("Source extent count is invalid.", nameof(extents));
        await ValidateIndexAsync(token).ConfigureAwait(false);
        long total = 0;
        foreach (var extent in extents.Distinct())
        {
            token.ThrowIfCancellationRequested();
            var volume = snapshot.Volumes.SingleOrDefault(volume => StringComparer.OrdinalIgnoreCase.Equals(volume.RelativeName, extent.RelativeSource));
            if (volume is null || extent.Offset < 0 || extent.Length < 0 || extent.Offset > volume.Stamp.Length - extent.Length ||
                extent.PayloadSha256 is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit))
                throw new ArgumentException("Source extent is invalid.", nameof(extents));
            total = checked(total + extent.Length);
            if (total > maximumBytes) throw new ArgumentException("Source verification exceeds its byte limit.", nameof(extents));
            EnsureCurrent(volume.Package);
            var key = new RangeKey(volume.Package, extent.Offset, extent.Length, digest.ToUpperInvariant());
            if (!volume.Stamp.Reliable)
                await VerifyCoreAsync(volume.Package, extent, token).ConfigureAwait(false);
            else
            {
                var check = verified.GetOrAdd(key, _ => new(() => RunProducerAsync(() => VerifyCoreAsync(volume.Package, extent, lifetime.Token)), LazyThreadSafetyMode.ExecutionAndPublication));
                AssetLoadDiagnostics.Cache("VerifiedRange", extent.RelativeSource + ":" + extent.Offset, check.IsValueCreated);
                await check.Value.WaitAsync(token).ConfigureAwait(false);
            }
            EnsureCurrent(volume.Package);
        }
        EnsureCurrent();
    }

    private async Task VerifyCoreAsync(uint package, SourceExtent extent, CancellationToken token)
    {
        await io.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var scratch = reserve?.Invoke(128 * 1024, "archive range verification");
            var pin = GetVolume(package); EnsureCurrent(package);
            using var timing = AssetLoadDiagnostics.Measure("VerificationRead", relativeSource: extent.RelativeSource, offset: extent.Offset, requestedBytes: extent.Length);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            for (long read = 0; read < extent.Length;)
            {
                int count = await RandomAccess.ReadAsync(pin.Handle, buffer.AsMemory(0, (int)Math.Min(buffer.Length, extent.Length - read)), extent.Offset + read, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException();
                hash.AppendData(buffer, 0, count); read += count;
            }
            if (!StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(hash.GetHashAndReset()), extent.PayloadSha256)) throw Changed();
            EnsureCurrent(package);
        }
        finally { io.Release(); }
    }

    public void EnsureCurrent(uint? package = null)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (index is { } held) SourceIdentity.EnsureUnchanged(revisions, held.Handle, held.Path, held.Stamp);
            if (package is { } selected)
            { var pin = GetVolume(selected); SourceIdentity.EnsureUnchanged(revisions, pin.Handle, pin.Path, pin.Stamp); }
            else foreach (var pin in volumes.Values) SourceIdentity.EnsureUnchanged(revisions, pin.Handle, pin.Path, pin.Stamp);
        }
    }

    private void EnsureIndexCurrent()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (index is { } held) SourceIdentity.EnsureUnchanged(revisions, held.Handle, held.Path, held.Stamp);
        }
    }

    private Pin GetIndex()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return index ??= new(snapshot.IndexPath, Path.GetFileName(snapshot.IndexPath), snapshot.IndexStamp, SourceIdentity.OpenPinned(snapshot.IndexPath));
        }
    }

    private Pin GetVolume(uint package)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (volumes.TryGetValue(package, out var held)) return held;
            var source = snapshot.Volumes.Single(volume => volume.Package == package);
            var handle = SourceIdentity.OpenPinned(source.FilePath);
            try
            {
                SourceIdentity.EnsureUnchanged(revisions, handle, source.FilePath, source.Stamp);
                held = new(source.FilePath, source.RelativeName, source.Stamp, handle); volumes.Add(package, held); return held;
            }
            catch { handle.Dispose(); throw; }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private static ArchiveException Changed() => new("SourceChanged", "The source changed. Reopen the archive.");

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true; lifetime.Cancel();
            if (producers == 0) ReleasePins();
        }
    }

    private async Task RunProducerAsync(Func<Task> action)
    {
        lock (gate) { ThrowIfDisposed(); producers++; }
        try { await action().ConfigureAwait(false); }
        finally { lock (gate) if (--producers == 0 && disposed) ReleasePins(); }
    }

    private async Task<T> RunProducerAsync<T>(Func<Task<T>> action)
    {
        lock (gate) { ThrowIfDisposed(); producers++; }
        try { return await action().ConfigureAwait(false); }
        finally { lock (gate) if (--producers == 0 && disposed) ReleasePins(); }
    }

    private void ReleasePins()
    {
        foreach (var pin in volumes.Values) pin.Handle.Dispose();
        volumes.Clear(); index?.Handle.Dispose(); index = null;
        lookupMemory?.Dispose(); lookupMemory = null; ids = null;
        foreach (var held in projectionMemory) held.Dispose();
        projectionMemory.Clear(); projections.Clear(); projectionBytes = 0;
        verified.Clear(); reads.Clear();
    }

    private sealed record SharedBuffer(byte[] Bytes, IDisposable? Reservation) : IDisposable
    { public void Dispose() => Reservation?.Dispose(); }

    private sealed class SharedRead(Func<Task<SharedBuffer>> factory)
    {
        private readonly object gate = new();
        private int consumers;
        private bool closed;
        public Lazy<Task<SharedBuffer>> Value { get; } = new(factory, LazyThreadSafetyMode.ExecutionAndPublication);
        public bool TryJoin()
        { lock (gate) { if (closed) return false; consumers++; return true; } }
        public bool Leave()
        {
            lock (gate)
            {
                if (--consumers != 0) return false;
                closed = true;
                if (Value.IsValueCreated)
                    _ = Value.Value.ContinueWith(task => { if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose(); },
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return true;
            }
        }
    }

    internal sealed class Slot { public ArchiveSourceSession? Session; }
    private sealed record Pin(string Path, string RelativeName, FileStamp Stamp, SafeFileHandle Handle);
    private readonly record struct ReadKey(long Ordinal, long Offset, int Length);
    private readonly record struct RangeKey(uint Package, long Offset, long Length, string Digest);

    public sealed class Lease : IDisposable
    {
        private Slot? slot;
        public ArchiveSourceSession Session { get; }
        internal Lease(Slot slot, ArchiveSourceSession session) { this.slot = slot; Session = session; }
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref slot, null); if (current is null) return;
            lock (current) if (--Session.owners == 0) { Session.Dispose(); if (ReferenceEquals(current.Session, Session)) current.Session = null; }
        }
    }
}
