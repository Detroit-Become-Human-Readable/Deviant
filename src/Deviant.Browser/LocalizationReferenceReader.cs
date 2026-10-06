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
public sealed class LocalizationReferenceReader
{
    public const int MaximumResourceBytes = 16 * 1024 * 1024;
    public const int MaximumReferenceCount = 4096;
    private static ReadOnlySpan<byte> Magic => "COM_CONT"u8;
    private readonly ISourceRevisionProvider revisions;
    private readonly object lookupGate = new();
    private ArchiveSnapshot? lookupSnapshot;
    private Task<CandidateLookup>? lookupBuild;

    public LocalizationReferenceReader(ISourceRevisionProvider? revisionProvider = null) =>
        revisions = revisionProvider ?? new FileRevisionProvider();

    public async Task<LocalizationReferenceReadResult> ReadAsync(ArchiveSnapshot snapshot, ResourceIdentity identity,
        CancellationToken cancellationToken = default)
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
            return new(sourceIdentity, sourceRevision, volume.RelativeName, LocalizationReferenceReadStatus.NotContainer,
                null, Array.Empty<AssetRelationship>());

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
                new(status, candidates, status == ReferenceStatus.Unique ? null : $"The archive lookup returned {candidates.Length} rows."),
                new(true, "COM_CONT v6", description, sourceExtent, SourceReferenceTrailer: trailer)));
        }

        return new(sourceIdentity, sourceRevision, volume.RelativeName, LocalizationReferenceReadStatus.Verified,
            tableSha256, relationships.AsReadOnly());
    }

    public async Task ValidateSnapshotIndexHashAsync(ArchiveSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
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
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
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
        var currentTable = await ReadSourceRangeAsync(snapshot, row, volume, 0, tableLength, cancellationToken).ConfigureAwait(false);
        var currentHash = Convert.ToHexString(SHA256.HashData(currentTable)).ToLowerInvariant();
        if (!string.Equals(currentHash, result.ReferenceTableSha256, StringComparison.Ordinal))
            throw new ArchiveException("SourceChanged", "The COM_CONT reference table changed after it was read.");
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
        while (true)
        {
            Task<CandidateLookup> build;
            lock (lookupGate)
            {
                if (!ReferenceEquals(lookupSnapshot, snapshot))
                {
                    lookupSnapshot = snapshot;
                    lookupBuild = null;
                }
                if (lookupBuild is null)
                {
                    build = Task.Run(() => BuildCandidateLookup(snapshot, token), token);
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

    private static CandidateLookup BuildCandidateLookup(ArchiveSnapshot snapshot, CancellationToken token)
    {
        var groups = new Dictionary<(uint Type, uint Id), List<ResourceRecord>>();
        foreach (var record in snapshot.Records)
        {
            token.ThrowIfCancellationRequested();
            var key = (record.Type, record.Id);
            if (!groups.TryGetValue(key, out var rows)) groups.Add(key, rows = []);
            rows.Add(record);
        }
        token.ThrowIfCancellationRequested();
        return new(groups.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
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
        if (length < 0 || length > MaximumResourceBytes || resourceOffset < 0 || resourceOffset > resource.StoredSize - length)
            throw new ArchiveException("InvalidRange", "The bounded reference read exceeds the resource extent.");
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

    private sealed record CandidateLookup(IReadOnlyDictionary<(uint Type, uint Id), ResourceRecord[]> RowsByTypeAndId)
    {
        public bool TryGetValue((uint Type, uint Id) key, out ResourceRecord[] rows) => RowsByTypeAndId.TryGetValue(key, out rows!);
    }
}
