using System.Text.Json;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;

namespace Deviant.Browser;

/// <summary>Reads and shares complete COM_CONT v6 tables for one archive revision.</summary>
public sealed class CatalogReferenceProvider : ICatalogReferenceProvider, IAsyncDisposable
{
    public const string CacheFeaturePrefix = "catalog.references/v1/";
    public const string CacheFeatureVersion = "com-cont-v6-shared-v1";
    private const int ReadScratchBytes = 64 * 1024;
    private readonly LocalizationReferenceReader reader;
    private readonly FeatureStore? featureStore;
    private readonly AssetMemoryBudget memoryBudget;
    private readonly CatalogReferenceProviderOptions options;
    private readonly ResourceRecord[] catalogRows;
    private readonly Dictionary<long, int> catalogPositions;
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Lazy<Task> initialIndexValidation;
    private readonly ArchiveSourceSession.Lease sourceSession;
    private readonly object hotGate = new();
    private readonly Dictionary<long, HotEntry> hotByOrdinal = [];
    private readonly LinkedList<long> hotOrder = [];
    private long hotBytes;
    private int disposed;

    public CatalogReferenceProvider(ArchiveSnapshot snapshot, AssetMemoryBudget sharedMemoryBudget,
        FeatureStore? featureStore = null, LocalizationReferenceReader? referenceReader = null,
        CatalogReferenceProviderOptions? providerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(sharedMemoryBudget);
        Snapshot = snapshot;
        Revision = new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        memoryBudget = sharedMemoryBudget;
        this.featureStore = featureStore;
        reader = (referenceReader ?? new LocalizationReferenceReader()).CreateBudgetedReader(memoryBudget);
        options = providerOptions ?? new();
        ValidateOptions(options);
        catalogRows = snapshot.Records.Where(row => row.Type == 3 && row.Validity == ResourceValidity.Valid)
            .OrderBy(row => row.Ordinal).ToArray();
        CatalogIdentities = Array.AsReadOnly(catalogRows
            .Select(row => new ResourceIdentity(snapshot.IndexContentId, row.Ordinal)).ToArray());
        catalogPositions = catalogRows.Select((row, index) => (row.Ordinal, index))
            .ToDictionary(item => item.Ordinal, item => item.index);
        initialIndexValidation = new Lazy<Task>(
            () => reader.ValidateSnapshotIndexHashAsync(Snapshot, lifetime.Token),
            LazyThreadSafetyMode.ExecutionAndPublication);
        sourceSession = ArchiveSourceSession.Acquire(snapshot, (bytes, reason) => memoryBudget.Reserve(bytes, reason));
    }

    public ArchiveSnapshot Snapshot { get; }
    public SourceRevision Revision { get; }
    public IReadOnlyList<ResourceIdentity> CatalogIdentities { get; }
    public int MaximumBatchCatalogs => options.MaximumBatchCatalogs;
    public AssetMemoryBudget SharedMemoryBudget => memoryBudget;
    public static string GetCacheFeatureKey(ResourceIdentity catalogIdentity)
    {
        ArgumentNullException.ThrowIfNull(catalogIdentity);
        if (catalogIdentity.Ordinal < 0) throw new ArgumentOutOfRangeException(nameof(catalogIdentity));
        return CacheFeaturePrefix + catalogIdentity.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task ValidateSnapshotAsync(CancellationToken token = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);
        await readGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureInitialIndexValidAsync(linked.Token).ConfigureAwait(false);
        }
        finally { readGate.Release(); }
    }

    public async Task<CatalogReferenceLease> ReadAsync(ResourceIdentity catalogIdentity,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(catalogIdentity);
        ValidateCatalogIdentity(catalogIdentity);
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);
        await readGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureInitialIndexValidAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();

            if (TryGetHot(catalogIdentity.Ordinal, out var hot))
            {
                using var validationMemory = memoryBudget.Reserve(ReadScratchBytes, "catalog table validation");
                bool valid;
                try { valid = await reader.ValidateCachedResultAsync(Snapshot, hot.Result, linked.Token).ConfigureAwait(false); }
                catch (ArchiveException error) when (error.Code == "SourceChanged")
                { RemoveHot(catalogIdentity.Ordinal); throw; }
                if (!valid)
                {
                    RemoveHot(catalogIdentity.Ordinal);
                    throw new ArchiveException("SourceChanged", "A retained catalog table no longer matches its source.");
                }
                try
                {
                    var callerMemory = memoryBudget.Reserve(hot.Bytes, "catalog reference consumer");
                    return new(hot.Result, callerMemory);
                }
                catch (ArchiveException error) when (error.Code == "AssetMemoryLimit")
                {
                    if (TryTakeHot(catalogIdentity.Ordinal, out var taken))
                        return new(taken.Result, taken.Reservation);
                    throw;
                }
            }

            if (featureStore is not null)
            {
                var cached = TryLoadPersistent(catalogIdentity.Ordinal, linked.Token);
                if (cached.Result is not null)
                {
                    using (cached.Reservation)
                    {
                        using var validationMemory = memoryBudget.Reserve(ReadScratchBytes, "catalog table validation");
                        if (await reader.ValidateCachedResultAsync(Snapshot, cached.Result, linked.Token).ConfigureAwait(false))
                        {
                            var frozenBytes = EstimateRetainedBytes(cached.Result);
                            AssetMemoryBudget.Reservation? frozenMemory = null;
                            try { frozenMemory = memoryBudget.Reserve(frozenBytes, "catalog cached reference result"); }
                            catch (ArchiveException error) when (error.Code == "AssetMemoryLimit") { }
                            if (frozenMemory is not null)
                            {
                                try
                                {
                                    var frozen = Freeze(cached.Result, linked.Token);
                                    try
                                    {
                                        var callerMemory = memoryBudget.Reserve(frozenBytes, "catalog reference consumer");
                                        if (TryRememberHot(catalogIdentity.Ordinal, frozen, frozenBytes, frozenMemory))
                                        {
                                            frozenMemory = null;
                                            return new(frozen, callerMemory);
                                        }
                                        callerMemory.Dispose();
                                    }
                                    catch (ArchiveException error) when (error.Code == "AssetMemoryLimit")
                                    { }
                                    var retained = frozenMemory;
                                    frozenMemory = null;
                                    return new(frozen, retained);
                                }
                                finally { frozenMemory?.Dispose(); }
                            }
                        }
                    }
                }
            }

            using var readMemory = memoryBudget.Reserve(ReadScratchBytes, "catalog reference table read");
            var read = await reader.ReadForProviderAsync(Snapshot, catalogIdentity, linked.Token).ConfigureAwait(false);
            var result = read.Result;
            AssetMemoryBudget.Reservation? resultMemory = read.Reservation;
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                if (resultMemory is null)
                    resultMemory = memoryBudget.Reserve(EstimateRetainedBytes(result), "catalog reference consumer");
                if (result.Status == LocalizationReferenceReadStatus.Verified)
                    TrySavePersistent(catalogIdentity.Ordinal, result, linked.Token);

                var resultBytes = resultMemory.Bytes;
                try
                {
                    var callerMemory = memoryBudget.Reserve(resultBytes, "catalog reference consumer");
                    if (result.Status == LocalizationReferenceReadStatus.Verified &&
                        TryRememberHot(catalogIdentity.Ordinal, result, resultBytes, resultMemory))
                    {
                        resultMemory = null;
                        return new(result, callerMemory);
                    }
                    callerMemory.Dispose();
                }
                catch (ArchiveException error) when (error.Code == "AssetMemoryLimit") { }

                var retained = resultMemory;
                resultMemory = null;
                return new(result, retained);
            }
            finally { resultMemory?.Dispose(); }
        }
        finally { readGate.Release(); }
    }

    public async Task<CatalogReferenceBatch> ReadBatchAsync(int startCatalogIndex, int maximumCatalogs,
        CancellationToken token = default)
    {
        ThrowIfDisposed();
        if (startCatalogIndex < 0 || startCatalogIndex > CatalogIdentities.Count)
            throw new ArgumentOutOfRangeException(nameof(startCatalogIndex));
        if (maximumCatalogs < 1 || maximumCatalogs > options.MaximumBatchCatalogs)
            throw new ArgumentOutOfRangeException(nameof(maximumCatalogs));
        token.ThrowIfCancellationRequested();
        await ValidateSnapshotAsync(token).ConfigureAwait(false);

        var rowReservation = memoryBudget.Reserve(checked(maximumCatalogs * 256L), "catalog reference batch rows");
        var rows = new List<CatalogReferenceBatchRow>(Math.Min(maximumCatalogs, CatalogIdentities.Count - startCatalogIndex));
        var reservations = new List<AssetMemoryBudget.Reservation> { rowReservation };
        var referenceRowsRead = 0;
        var next = startCatalogIndex;
        try
        {
            var end = checked((int)Math.Min(CatalogIdentities.Count, (long)startCatalogIndex + maximumCatalogs));
            while (next < end)
            {
                token.ThrowIfCancellationRequested();
                var identity = CatalogIdentities[next];
                try
                {
                    var lease = await ReadAsync(identity, token).ConfigureAwait(false);
                    rows.Add(new(identity, lease, null));
                    referenceRowsRead = checked(referenceRowsRead + lease.Result.Relationships.Count);
                }
                catch (OperationCanceledException) { throw; }
                catch (ArchiveException error) when (error.Code == "SourceChanged") { throw; }
                catch (Exception error) when (IsPerCatalogFailure(error))
                {
                    rows.Add(new(identity, null, DiagnosticCode(error)));
                }
                next++;
            }

            token.ThrowIfCancellationRequested();
            return new(startCatalogIndex, next, referenceRowsRead, next == CatalogIdentities.Count,
                Array.AsReadOnly(rows.ToArray()), reservations);
        }
        catch
        {
            foreach (var row in rows) row.Lease?.Dispose();
            foreach (var reservation in reservations) reservation.Dispose();
            throw;
        }
    }

    public async Task RevalidateAsync(LocalizationReferenceReadResult result, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ThrowIfDisposed();
        if (result.Status != LocalizationReferenceReadStatus.Verified ||
            result.Revision != Revision || result.Source.IndexContentId != Revision.IndexContentId ||
            result.Source.MemberPath is not null || !catalogPositions.ContainsKey(result.Source.Ordinal))
            throw new ArchiveException("InvalidReferenceSnapshot", "The catalog reference result belongs to another source revision.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);
        await readGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureInitialIndexValidAsync(linked.Token).ConfigureAwait(false);
            using var validationMemory = memoryBudget.Reserve(ReadScratchBytes, "catalog table revalidation");
            if (!await reader.ValidateCachedResultAsync(Snapshot, result, linked.Token).ConfigureAwait(false))
            {
                RemoveHot(result.Source.Ordinal);
                throw new ArchiveException("SourceChanged", "The catalog relationship result no longer matches its verified source table.");
            }
        }
        catch (ArchiveException error) when (error.Code == "SourceChanged")
        {
            RemoveHot(result.Source.Ordinal);
            throw;
        }
        finally { readGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        if (initialIndexValidation.IsValueCreated)
        {
            try { await initialIndexValidation.Value.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (ArchiveException) { }
            catch { }
        }
        await readGate.WaitAsync().ConfigureAwait(false);
        readGate.Release();
        lock (hotGate)
        {
            foreach (var entry in hotByOrdinal.Values) entry.Reservation.Dispose();
            hotByOrdinal.Clear();
            hotOrder.Clear();
            hotBytes = 0;
        }
        readGate.Dispose();
        reader.Dispose();
        sourceSession.Dispose();
        lifetime.Dispose();
    }

    private PersistentCacheRead TryLoadPersistent(long ordinal, CancellationToken token)
    {
        if (featureStore is null) return new(null, null);
        AssetMemoryBudget.Reservation? loadingMemory = null;
        try
        {
            loadingMemory = memoryBudget.Reserve(checked(options.MaximumCachedTableBytes * 2L),
                "catalog cache load and expanded DTOs");
            var result = featureStore.LoadBounded<LocalizationReferenceReadResult>(CacheFeatureKey(ordinal),
                CacheFeatureVersion, options.MaximumCachedTableBytes, token);
            if (result is null)
            {
                loadingMemory.Dispose();
                return new(null, null);
            }
            return new(result, loadingMemory);
        }
        catch (OperationCanceledException) { loadingMemory?.Dispose(); throw; }
        catch (ArchiveException error) when (error.Code == "AssetMemoryLimit")
        { loadingMemory?.Dispose(); return new(null, null); }
        catch (Exception error) when (error is JsonException or IOException or
            SqliteException or ArgumentException or ObjectDisposedException)
        { loadingMemory?.Dispose(); return new(null, null); }
    }

    private void TrySavePersistent(long ordinal, LocalizationReferenceReadResult result, CancellationToken token)
    {
        if (featureStore is null || result.Status != LocalizationReferenceReadStatus.Verified) return;
        AssetMemoryBudget.Reservation? serializationMemory = null;
        try
        {
            serializationMemory = memoryBudget.Reserve(options.MaximumCachedTableBytes, "catalog cache serialization");
            token.ThrowIfCancellationRequested();
            featureStore.SaveBounded(CacheFeatureKey(ordinal), CacheFeatureVersion, result,
                options.MaximumCachedTableBytes, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidDataException) { }
        catch (ArchiveException error) when (error.Code == "AssetMemoryLimit") { }
        catch (Exception error) when (error is IOException or SqliteException or ArgumentException or ObjectDisposedException) { }
        finally { serializationMemory?.Dispose(); }
    }

    private bool TryGetHot(long ordinal, out HotEntry entry)
    {
        lock (hotGate)
        {
            if (!hotByOrdinal.TryGetValue(ordinal, out entry!))
            { entry = null!; return false; }
            hotOrder.Remove(entry.Node);
            hotOrder.AddLast(entry.Node);
            return true;
        }
    }

    private bool TryRememberHot(long ordinal, LocalizationReferenceReadResult result, long size,
        AssetMemoryBudget.Reservation reservation)
    {
        if (size <= 0 || size > options.MaximumHotBytes || options.MaximumHotCatalogs == 0) return false;
        lock (hotGate)
        {
            if (hotByOrdinal.Remove(ordinal, out var existing))
            {
                hotOrder.Remove(existing.Node);
                hotBytes -= existing.Bytes;
                existing.Reservation.Dispose();
            }

            while (hotOrder.First is { } first &&
                (hotByOrdinal.Count >= options.MaximumHotCatalogs || hotBytes > options.MaximumHotBytes - size))
                EvictHot(first.Value);

            var node = hotOrder.AddLast(ordinal);
            hotByOrdinal.Add(ordinal, new(result, size, reservation, node));
            hotBytes += size;
            return true;
        }
    }

    private bool TryTakeHot(long ordinal, out HotEntry entry)
    {
        lock (hotGate)
        {
            if (!hotByOrdinal.Remove(ordinal, out entry!)) { entry = null!; return false; }
            hotOrder.Remove(entry.Node);
            hotBytes -= entry.Bytes;
            return true;
        }
    }

    private void RemoveHot(long ordinal)
    {
        if (!TryTakeHot(ordinal, out var entry)) return;
        entry.Reservation.Dispose();
    }

    private void EvictHot(long ordinal)
    {
        if (!hotByOrdinal.Remove(ordinal, out var entry)) return;
        hotOrder.Remove(entry.Node);
        hotBytes -= entry.Bytes;
        entry.Reservation.Dispose();
    }

    private async Task EnsureInitialIndexValidAsync(CancellationToken token)
    {
        try { await initialIndexValidation.Value.WaitAsync(token).ConfigureAwait(false); }
        catch (ArchiveException error) when (error.Code == "SourceChanged") { throw; }
    }

    private LocalizationReferenceReadResult Freeze(LocalizationReferenceReadResult source, CancellationToken token)
    {
        if (source.Status != LocalizationReferenceReadStatus.Verified)
            return source with { Relationships = Array.Empty<AssetRelationship>() };
        if (source.Relationships.Count > LocalizationReferenceReader.MaximumReferenceCount)
            throw new ArchiveException("ReferenceCountLimit", "The catalog table exceeds its configured relationship limit.");
        var relationships = new AssetRelationship[source.Relationships.Count];
        for (var index = 0; index < relationships.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var relationship = source.Relationships[index];
            relationships[index] = relationship with
            {
                Resolution = relationship.Resolution with
                { Candidates = Array.AsReadOnly(relationship.Resolution.Candidates.ToArray()) }
            };
        }
        return source with { Relationships = Array.AsReadOnly(relationships) };
    }

    private static long EstimateRetainedBytes(LocalizationReferenceReadResult result)
    {
        if (result.Status != LocalizationReferenceReadStatus.Verified)
            return checked(512L + result.Source.IndexContentId.Length * 2L + result.RelativeSource.Length * 2L);
        long bytes = 1024L * 1024 + result.Relationships.Count * 2048L +
            result.Source.IndexContentId.Length * 2L + result.RelativeSource.Length * 2L +
            (result.ReferenceTableSha256?.Length ?? 0) * 2L;
        foreach (var relationship in result.Relationships)
        {
            bytes = checked(bytes + relationship.Resolution.Candidates.Count * 256L +
                relationship.Label.Length * 2L + relationship.Reference.Id.Length * 2L +
                (relationship.Evidence.Source.Length + relationship.Evidence.Description.Length) * 2L);
            if (relationship.Reference.Owner is { } owner)
                bytes = checked(bytes + (owner.IndexContentId.Length + (owner.MemberPath?.Length ?? 0)) * 2L);
            if (relationship.Evidence.PhysicalExtent is { } extent)
                bytes = checked(bytes + (extent.RelativeSource.Length + (extent.PayloadSha256?.Length ?? 0)) * 2L);
            if (relationship.Resolution.Reason is { } reason) bytes = checked(bytes + reason.Length * 2L);
            foreach (var candidate in relationship.Resolution.Candidates)
                bytes = checked(bytes + (candidate.IndexContentId.Length + (candidate.MemberPath?.Length ?? 0)) * 2L);
        }
        return bytes;
    }

    private static string CacheFeatureKey(long ordinal) => CacheFeaturePrefix + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void ValidateCatalogIdentity(ResourceIdentity identity)
    {
        if (identity.IndexContentId != Revision.IndexContentId || identity.MemberPath is not null ||
            !catalogPositions.ContainsKey(identity.Ordinal))
            throw new ArchiveException("InvalidCatalogReference", "The selected identity is not a valid type-3 catalog in this archive revision.");
    }

    private static bool IsPerCatalogFailure(Exception error)
    {
        if (error is ArchiveException archive)
            return archive.Code is not ("SourceChanged" or "AssetMemoryLimit");
        return error is IOException or ArgumentException or OverflowException or JsonException or SqliteException;
    }

    private static string DiagnosticCode(Exception error) => error is ArchiveException archive
        ? archive.Code : "CatalogReferenceUnavailable";

    private static void ValidateOptions(CatalogReferenceProviderOptions value)
    {
        if (value.MaximumBatchCatalogs is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(value.MaximumBatchCatalogs));
        if (value.MaximumHotCatalogs is < 0 or > 4096) throw new ArgumentOutOfRangeException(nameof(value.MaximumHotCatalogs));
        if (value.MaximumHotBytes is < 0 or > 512L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(value.MaximumHotBytes));
        if (value.MaximumCachedTableBytes is < 1024 or > LocalizationReferenceReader.MaximumResourceBytes)
            throw new ArgumentOutOfRangeException(nameof(value.MaximumCachedTableBytes));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private sealed record PersistentCacheRead(LocalizationReferenceReadResult? Result,
        AssetMemoryBudget.Reservation? Reservation);
    private sealed record HotEntry(LocalizationReferenceReadResult Result, long Bytes,
        AssetMemoryBudget.Reservation Reservation, LinkedListNode<long> Node);
}
