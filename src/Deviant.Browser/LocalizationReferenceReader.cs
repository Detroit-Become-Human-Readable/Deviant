using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Core;
using Microsoft.Win32.SafeHandles;

namespace Deviant.Browser;

public enum LocalizationReferenceReadStatus { Verified, NotContainer }

/// <summary>A bounded set of source references read from one archive resource.</summary>
public sealed record LocalizationReferenceReadResult(ResourceIdentity Source, SourceRevision Revision,
    string RelativeSource, LocalizationReferenceReadStatus Status, string? ReferenceTableSha256,
    IReadOnlyList<AssetRelationship> Relationships);

/// <summary>Reads verified v6 COM_CONT reference rows without decoding the wrapped resource body.</summary>
public sealed class LocalizationReferenceReader : IDisposable
{
    public const int MaximumResourceBytes = 16 * 1024 * 1024;
    public const int MaximumReferenceCount = 4096;
    private static ReadOnlySpan<byte> Magic => "COM_CONT"u8;
    private readonly ISourceRevisionProvider revisions;
    private readonly object lookupGate = new();
    private ArchiveSnapshot? lookupSnapshot;
    private Task<CandidateLookup>? lookupBuild;
    private AssetMemoryBudget? lookupMemoryBudget;
    private int disposed;

    public LocalizationReferenceReader(ISourceRevisionProvider? revisionProvider = null) =>
        revisions = revisionProvider ?? new FileRevisionProvider();

    internal void TrackMemoryBudget(AssetMemoryBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        lock (lookupGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (lookupMemoryBudget is not null && !ReferenceEquals(lookupMemoryBudget, budget))
                throw new InvalidOperationException("A shared catalog reference reader must use one memory budget.");
            if (lookupMemoryBudget is null && (lookupSnapshot is not null || lookupBuild is not null))
                throw new InvalidOperationException("Configure the catalog reader memory budget before its first read.");
            lookupMemoryBudget = budget;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Task<CandidateLookup>? prior;
        lock (lookupGate)
        {
            prior = lookupBuild;
            lookupBuild = null;
            lookupSnapshot = null;
        }
        RetireLookup(prior);
    }

    internal LocalizationReferenceReader CreateBudgetedReader(AssetMemoryBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var clone = new LocalizationReferenceReader(revisions);
        clone.TrackMemoryBudget(budget);
        return clone;
    }

    public async Task<LocalizationReferenceReadResult> ReadAsync(ArchiveSnapshot snapshot, ResourceIdentity identity,
        CancellationToken cancellationToken = default)
    {
        using var shared = revisions is FileRevisionProvider ? ArchiveSourceSession.TryAcquire(snapshot) : null;
        if (shared is not null)
        {
            await shared.Session.ValidateIndexAsync(cancellationToken).ConfigureAwait(false);
            var value = await shared.Session.GetProjectionAsync("catalog-table-v1/" + identity.Key, async token =>
            {
                var data = await ReadForProviderAsync(snapshot, identity, token).ConfigureAwait(false);
                using var owned = data.Reservation;
                return (data.Result, checked(4096L + data.Result.Relationships.Sum(row =>
                    2048L + row.Resolution.Candidates.Count * 256L)));
            }, cancellationToken).ConfigureAwait(false);
            shared.Session.EnsureCurrent(snapshot.RequireRecord(identity.Ordinal).Package);
            return value;
        }
        var read = await ReadForProviderAsync(snapshot, identity, cancellationToken).ConfigureAwait(false);
        read.Reservation?.Dispose();
        return read.Result;
    }

    internal async Task<CatalogReferenceReadData> ReadForProviderAsync(ArchiveSnapshot snapshot,
        ResourceIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(identity);
        if (!string.Equals(identity.IndexContentId, snapshot.IndexContentId, StringComparison.Ordinal) || identity.MemberPath is not null)
            throw new ArchiveException("InvalidReferenceSource", "The reference source does not belong to the selected archive snapshot.");

        var resource = FindResourceByOrdinal(snapshot, identity.Ordinal)
            ?? throw new ArchiveException("MissingResource", "The selected reference source is absent from the archive snapshot.");
        if (resource.Validity != ResourceValidity.Valid)
            throw new ArchiveException("InvalidReferenceSource", "The selected resource row is not valid.");
        if (resource.StoredSize < 20)
            throw new ArchiveException("ReferenceResourceSizeLimit", "The resource must be at least 20 bytes.");
        var volume = snapshot.Volumes.SingleOrDefault(candidate => candidate.Package == resource.Package)
            ?? throw new ArchiveException("MissingVolume", "The reference source volume is absent from the snapshot.");
        var sourceIdentity = new ResourceIdentity(snapshot.IndexContentId, resource.Ordinal);
        var sourceRevision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);

        cancellationToken.ThrowIfCancellationRequested();
        var header = await ReadSourceRangeAsync(snapshot, resource, volume, 0, 20, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 8).SequenceEqual(Magic))
            return new(new(sourceIdentity, sourceRevision, volume.RelativeName, LocalizationReferenceReadStatus.NotContainer,
                null, Array.Empty<AssetRelationship>()), null);

        var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        if (version != 6)
            throw new ArchiveException("UnsupportedReferenceContainerVersion", "Only v6 COM_CONT reference tables are supported.");

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
        var referenceCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16, 4));
        if (referenceCount > MaximumReferenceCount)
            throw new ArchiveException("ReferenceCountLimit", $"The reference count exceeds {MaximumReferenceCount}.");
        var requiredPayloadLength = checked(4u + referenceCount * 9u);
        if (payloadLength < requiredPayloadLength || payloadLength > resource.StoredSize - 16)
            throw new ArchiveException("MalformedReferenceTable", "The declared COM_CONT body does not contain the full reference table.");
        var tableLength = checked(20 + (int)referenceCount * 9);
        if (tableLength > resource.StoredSize)
            throw new ArchiveException("MalformedReferenceTable", "The reference rows extend beyond the archive resource.");

        var tableBytes = await ReadSourceRangeAsync(snapshot, resource, volume, 0, tableLength, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan().SequenceEqual(tableBytes.AsSpan(0, header.Length)))
            throw new ArchiveException("SourceChanged", "The COM_CONT header changed between bounded reads.");
        var tableSha256 = Convert.ToHexString(SHA256.HashData(tableBytes)).ToLowerInvariant();

        var rowsByTypeAndId = await GetCandidateLookupAsync(snapshot, cancellationToken).ConfigureAwait(false);
        long candidateCount = 0;
        for (var index = 0; index < referenceCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowOffset = checked(20 + (int)index * 9);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(tableBytes.AsSpan(rowOffset, 4));
            var id = BinaryPrimitives.ReadUInt32LittleEndian(tableBytes.AsSpan(rowOffset + 4, 4));
            if (rowsByTypeAndId.TryGetValue((type, id), out var matches))
                candidateCount = checked(candidateCount + matches.Length);
        }
        var retainedEstimate = checked(1024L * 1024 + tableBytes.Length * 2L + referenceCount * 2048L + candidateCount * 256L);
        var resultReservation = lookupMemoryBudget?.Reserve(retainedEstimate, "catalog reference result");
        try
        {
            var relationships = new List<AssetRelationship>(checked((int)referenceCount));
            for (var index = 0; index < referenceCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rowOffset = checked(20 + (int)index * 9);
                var rawRow = tableBytes.AsSpan(rowOffset, 9);
                var type = BinaryPrimitives.ReadUInt32LittleEndian(rawRow[..4]);
                var id = BinaryPrimitives.ReadUInt32LittleEndian(rawRow.Slice(4, 4));
                var trailer = rawRow[8];
                var candidates = rowsByTypeAndId.TryGetValue((type, id), out var matches)
                    ? matches.Select(match => new ResourceIdentity(snapshot.IndexContentId, match.Ordinal)).ToArray()
                    : Array.Empty<ResourceIdentity>();
                var status = candidates.Length switch
                {
                    0 => ReferenceStatus.Missing,
                    1 => ReferenceStatus.Unique,
                    _ => ReferenceStatus.Ambiguous
                };
                var sourceExtent = new SourceExtent(volume.RelativeName,
                    checked(resource.Offset + rowOffset), 9,
                    Convert.ToHexString(SHA256.HashData(rawRow)).ToLowerInvariant());
                var reference = new ResourceReference(IdentifierNamespace.Archive,
                    id.ToString(CultureInfo.InvariantCulture), sourceIdentity, type);
                var description = $"COM_CONT v6 row {index} stores a type/id pair and one trailer byte.";
                relationships.Add(new(sourceIdentity, RelationshipKind(type), "COM_CONT reference", reference,
                    new(status, Array.AsReadOnly(candidates), status == ReferenceStatus.Unique ? null : $"The archive lookup returned {candidates.Length} rows."),
                    new(true, "COM_CONT v6", description, sourceExtent, SourceReferenceTrailer: trailer)));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = new LocalizationReferenceReadResult(sourceIdentity, sourceRevision, volume.RelativeName,
                LocalizationReferenceReadStatus.Verified, tableSha256, relationships.AsReadOnly());
            return new(result, resultReservation);
        }
        catch
        {
            resultReservation?.Dispose();
            throw;
        }
    }

    public async Task ValidateSnapshotIndexHashAsync(ArchiveSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        using var shared = revisions is FileRevisionProvider ? ArchiveSourceSession.TryAcquire(snapshot) : null;
        if (shared is not null)
        {
            await shared.Session.ValidateIndexAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        using var handle = SourceIdentity.OpenPinned(snapshot.IndexPath);
        SourceIdentity.EnsureUnchanged(revisions, handle, snapshot.IndexPath, snapshot.IndexStamp);
        var contentId = await SourceIdentity.HashAsync(handle, snapshot.IndexStamp.Length, cancellationToken,
            phase: "Validating localization reference index").ConfigureAwait(false);
        SourceIdentity.EnsureUnchanged(revisions, handle, snapshot.IndexPath, snapshot.IndexStamp);
        if (!string.Equals(contentId, snapshot.IndexContentId, StringComparison.Ordinal))
            throw new ArchiveException("SourceChanged", "The archive index contents differ from the selected reference snapshot.");
    }

    public async Task ValidateReferenceTableHashAsync(ArchiveSnapshot snapshot,
        LocalizationReferenceReadResult result, CancellationToken cancellationToken = default)
    {
        using var shared = revisions is FileRevisionProvider ? ArchiveSourceSession.TryAcquire(snapshot) : null;
        if (shared is not null && result.Status == LocalizationReferenceReadStatus.Verified &&
            result.ReferenceTableSha256 is { } digest && result.Source.IndexContentId == snapshot.IndexContentId &&
            result.Revision == new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp))
        {
            var row = snapshot.RequireRecord(result.Source.Ordinal);
            await shared.Session.VerifyAsync([new(row.VolumeName, row.Offset,
                checked(20L + result.Relationships.Count * 9L), digest)], cancellationToken).ConfigureAwait(false);
            return;
        }
        _ = await ReadCurrentReferenceTableAsync(snapshot, result, cancellationToken, mismatchIsSourceChange: true)
            .ConfigureAwait(false);
    }

    /// <summary>Validates a cached table envelope, its row provenance, and its typed archive candidates.</summary>
    public async Task<bool> ValidateCachedResultAsync(ArchiveSnapshot snapshot,
        LocalizationReferenceReadResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Status != LocalizationReferenceReadStatus.Verified || result.ReferenceTableSha256 is null ||
            result.Source.IndexContentId != snapshot.IndexContentId || result.Source.MemberPath is not null ||
            result.Revision != new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp) ||
            result.Relationships is null || result.Relationships.Count > MaximumReferenceCount)
            return false;

        var row = FindResourceByOrdinal(snapshot, result.Source.Ordinal)
            ?? throw new ArchiveException("MissingResource", "The cached reference source is absent from the selected snapshot.");
        if (row.Type != 3 || row.Validity != ResourceValidity.Valid)
            return false;
        var tableBytes = await ReadCurrentReferenceTableAsync(snapshot, result, cancellationToken,
            mismatchIsSourceChange: false).ConfigureAwait(false);
        if (tableBytes is null) return false;
        if (tableBytes.Length < 20 || !tableBytes.AsSpan(0, 8).SequenceEqual(Magic) ||
            BinaryPrimitives.ReadUInt32LittleEndian(tableBytes.AsSpan(8, 4)) != 6 ||
            BinaryPrimitives.ReadUInt32LittleEndian(tableBytes.AsSpan(16, 4)) != result.Relationships.Count)
            return false;

        var volume = snapshot.Volumes.SingleOrDefault(candidate => candidate.Package == row.Package)
            ?? throw new ArchiveException("MissingVolume", "The cached reference source volume is absent from the snapshot.");
        if (!StringComparer.Ordinal.Equals(result.RelativeSource, volume.RelativeName)) return false;
        var candidateLookup = await GetCandidateLookupAsync(snapshot, cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < result.Relationships.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relationship = result.Relationships[index];
            if (relationship is null || relationship.Evidence is null || relationship.Reference is null ||
                relationship.Resolution is null || relationship.Resolution.Candidates is null ||
                relationship.Source != result.Source || !relationship.Evidence.Verified ||
                !StringComparer.Ordinal.Equals(relationship.Evidence.Source, "COM_CONT v6") ||
                relationship.Reference.Namespace != IdentifierNamespace.Archive || relationship.Reference.Owner != result.Source)
                return false;

            var rowOffset = checked(20 + index * 9);
            if (rowOffset > tableBytes.Length - 9) return false;
            var rawRow = tableBytes.AsSpan(rowOffset, 9);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(rawRow[..4]);
            var id = BinaryPrimitives.ReadUInt32LittleEndian(rawRow.Slice(4, 4));
            var trailer = rawRow[8];
            if (relationship.Reference.ResourceType != type ||
                !StringComparer.Ordinal.Equals(relationship.Reference.Id, id.ToString(CultureInfo.InvariantCulture)) ||
                relationship.Evidence.SourceReferenceTrailer != trailer ||
                relationship.Kind != RelationshipKind(type) || !StringComparer.Ordinal.Equals(relationship.Label, "COM_CONT reference"))
                return false;

            var expectedExtent = new SourceExtent(volume.RelativeName, checked(row.Offset + rowOffset), 9,
                Convert.ToHexString(SHA256.HashData(rawRow)).ToLowerInvariant());
            if (relationship.Evidence.PhysicalExtent != expectedExtent) return false;

            candidateLookup.TryGetValue((type, id), out var candidateRows);
            var candidateCount = candidateRows?.Length ?? 0;
            var expectedStatus = candidateCount switch
            {
                0 => ReferenceStatus.Missing,
                1 => ReferenceStatus.Unique,
                _ => ReferenceStatus.Ambiguous
            };
            if (relationship.Resolution.Status != expectedStatus ||
                relationship.Resolution.Candidates.Count != candidateCount) return false;
            for (var candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var actual = relationship.Resolution.Candidates[candidateIndex];
                if (actual.IndexContentId != snapshot.IndexContentId || actual.Ordinal != candidateRows![candidateIndex].Ordinal ||
                    actual.MemberPath is not null) return false;
            }
        }
        return true;
    }

    private async Task<byte[]?> ReadCurrentReferenceTableAsync(ArchiveSnapshot snapshot,
        LocalizationReferenceReadResult result, CancellationToken token, bool mismatchIsSourceChange)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(result);
        token.ThrowIfCancellationRequested();
        if (result.Status != LocalizationReferenceReadStatus.Verified || result.ReferenceTableSha256 is null ||
            result.Source.IndexContentId != snapshot.IndexContentId || result.Source.MemberPath is not null)
            throw new ArchiveException("InvalidReferenceSnapshot", "The cached reference result does not belong to the selected archive snapshot.");

        var row = FindResourceByOrdinal(snapshot, result.Source.Ordinal)
            ?? throw new ArchiveException("MissingResource", "The cached reference source is absent from the selected snapshot.");
        var tableLength = checked(20 + result.Relationships.Count * 9);
        if (tableLength > MaximumResourceBytes || tableLength > row.StoredSize)
            throw new ArchiveException("MalformedReferenceTable", "The cached reference table exceeds the selected resource extent.");
        var volume = snapshot.Volumes.SingleOrDefault(candidate => candidate.Package == row.Package)
            ?? throw new ArchiveException("MissingVolume", "The cached reference source volume is absent from the snapshot.");
        var currentTable = await ReadSourceRangeAsync(snapshot, row, volume, 0, tableLength, token).ConfigureAwait(false);
        var currentHash = Convert.ToHexString(SHA256.HashData(currentTable)).ToLowerInvariant();
        if (!string.Equals(currentHash, result.ReferenceTableSha256, StringComparison.Ordinal))
        {
            if (mismatchIsSourceChange)
                throw new ArchiveException("SourceChanged", "The COM_CONT reference table changed after it was read.");
            return null;
        }
        return currentTable;
    }

    private static AssetRelationshipKind RelationshipKind(uint type) => type switch
    {
        1016 => AssetRelationshipKind.Localization,
        1031 => AssetRelationshipKind.Event,
        1033 => AssetRelationshipKind.Media,
        4091 => AssetRelationshipKind.Container,
        _ => AssetRelationshipKind.Dependency
    };

    private async Task<CandidateLookup> GetCandidateLookupAsync(ArchiveSnapshot snapshot, CancellationToken token)
    {
        using var shared = revisions is FileRevisionProvider ? ArchiveSourceSession.TryAcquire(snapshot) : null;
        if (shared is not null) return new(shared.Session);
        while (true)
        {
            Task<CandidateLookup> build;
            lock (lookupGate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                if (!ReferenceEquals(lookupSnapshot, snapshot))
                {
                    RetireLookup(lookupBuild);
                    lookupSnapshot = snapshot;
                    lookupBuild = null;
                }
                if (lookupBuild is null)
                {
                    var budget = lookupMemoryBudget;
                    build = Task.Run(() => BuildCandidateLookup(snapshot, token, budget), token);
                    lookupBuild = build;
                    _ = build.ContinueWith(completed =>
                    {
                        if (completed.IsFaulted) _ = completed.Exception;
                        if (!completed.IsCanceled && !completed.IsFaulted) return;
                        lock (lookupGate)
                        {
                            if (ReferenceEquals(lookupSnapshot, snapshot) && ReferenceEquals(lookupBuild, completed))
                                lookupBuild = null;
                        }
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                build = lookupBuild;
            }

            try { return await build.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && build.IsCanceled)
            {
                lock (lookupGate)
                    if (ReferenceEquals(lookupSnapshot, snapshot) && ReferenceEquals(lookupBuild, build)) lookupBuild = null;
            }
            catch
            {
                if (build.IsCanceled || build.IsFaulted)
                {
                    lock (lookupGate)
                        if (ReferenceEquals(lookupSnapshot, snapshot) && ReferenceEquals(lookupBuild, build)) lookupBuild = null;
                }
                throw;
            }
        }
    }

    private static CandidateLookup BuildCandidateLookup(ArchiveSnapshot snapshot, CancellationToken token,
        AssetMemoryBudget? budget)
    {
        AssetMemoryBudget.Reservation? reservation = null;
        try
        {
            reservation = budget?.Reserve(checked(1024L * 1024 + snapshot.Records.Count * 160L),
                "catalog typed-candidate index");
            var groups = new Dictionary<(uint Type, uint Id), List<ResourceRecord>>();
            foreach (var record in snapshot.Records)
            {
                token.ThrowIfCancellationRequested();
                var key = (record.Type, record.Id);
                if (!groups.TryGetValue(key, out var rows)) groups.Add(key, rows = []);
                rows.Add(record);
            }
            token.ThrowIfCancellationRequested();
            return new(groups.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()), reservation);
        }
        catch { reservation?.Dispose(); throw; }
    }

    private static void RetireLookup(Task<CandidateLookup>? lookup)
    {
        if (lookup is null) return;
        if (lookup.IsCompletedSuccessfully) { lookup.Result.Dispose(); return; }
        _ = lookup.ContinueWith(completed =>
        {
            if (completed.IsCompletedSuccessfully) completed.Result.Dispose();
            if (completed.IsFaulted) _ = completed.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static ResourceRecord? FindResourceByOrdinal(ArchiveSnapshot snapshot, long ordinal)
    {
        if (ordinal >= 0 && ordinal < snapshot.Records.Count)
        {
            var row = snapshot.Records[checked((int)ordinal)];
            if (row.Ordinal == ordinal) return row;
        }
        return snapshot.Records.SingleOrDefault(record => record.Ordinal == ordinal);
    }

    private async Task<byte[]> ReadSourceRangeAsync(ArchiveSnapshot snapshot, ResourceRecord resource,
        VolumeSource volume, long resourceOffset, int length, CancellationToken token)
    {
        using var timing = AssetLoadDiagnostics.Measure("CatalogRead", relativeSource: volume.RelativeName,
            offset: checked(resource.Offset + resourceOffset), requestedBytes: length);
        if (length < 0 || length > MaximumResourceBytes || resourceOffset < 0 || resourceOffset > resource.StoredSize - length)
            throw new ArchiveException("InvalidRange", "The bounded reference read exceeds the resource extent.");
        using var shared = revisions is FileRevisionProvider ? ArchiveSourceSession.TryAcquire(snapshot) : null;
        if (shared is not null)
            return await shared.Session.ReadAsync(new(snapshot.IndexContentId, resource.Ordinal), resourceOffset, length, token).ConfigureAwait(false);
        using var indexHandle = SourceIdentity.OpenPinned(snapshot.IndexPath);
        SourceIdentity.EnsureUnchanged(revisions, indexHandle, snapshot.IndexPath, snapshot.IndexStamp);
        using var volumeHandle = SourceIdentity.OpenPinned(volume.FilePath);
        SourceIdentity.EnsureUnchanged(revisions, volumeHandle, volume.FilePath, volume.Stamp);
        var bytes = new byte[length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = await RandomAccess.ReadAsync(volumeHandle, bytes.AsMemory(offset),
                checked(resource.Offset + resourceOffset + offset), token).ConfigureAwait(false);
            if (read == 0) throw new ArchiveException("SourceChanged", "The archive volume ended during a bounded reference read.");
            offset += read;
        }
        SourceIdentity.EnsureUnchanged(revisions, volumeHandle, volume.FilePath, volume.Stamp);
        SourceIdentity.EnsureUnchanged(revisions, indexHandle, snapshot.IndexPath, snapshot.IndexStamp);
        return bytes;
    }

    private sealed class CandidateLookup(IReadOnlyDictionary<(uint Type, uint Id), ResourceRecord[]> rowsByTypeAndId,
        AssetMemoryBudget.Reservation? reservation) : IDisposable
    {
        private ArchiveSourceSession? shared;
        public CandidateLookup(ArchiveSourceSession shared) : this(new Dictionary<(uint Type, uint Id), ResourceRecord[]>(), null)
        { this.shared = shared; }
        private AssetMemoryBudget.Reservation? currentReservation = reservation;
        public bool TryGetValue((uint Type, uint Id) key, out ResourceRecord[] rows)
        {
            if (shared is null) return rowsByTypeAndId.TryGetValue(key, out rows!);
            rows = shared.FindCandidates(key.Id, key.Type); return rows.Length > 0;
        }
        public void Dispose() => Interlocked.Exchange(ref currentReservation, null)?.Dispose();
    }
}

internal sealed record CatalogReferenceReadData(LocalizationReferenceReadResult Result,
    AssetMemoryBudget.Reservation? Reservation);
