using System.Security.Cryptography;
using Deviant.Core;
using Microsoft.Win32.SafeHandles;

namespace Deviant.Browser;

/// <summary>Checks the byte digests stored for the bounded localization resource set.</summary>
public sealed class LocalizationPayloadVerifier
{
    private const int HashBufferSize = 128 * 1024;

    public Task<LocalizationPayloadVerificationLease> VerifyAsync(ArchiveSnapshot snapshot,
        IReadOnlyDictionary<long, string> expectedDigests, IReadOnlyList<ResourceRecord> eligibleResourceRows,
        ISourceRevisionProvider? revisionProvider = null, CancellationToken cancellationToken = default,
        LocalizationPayloadVerificationLease? verifiedIndexLease = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(expectedDigests);
        ArgumentNullException.ThrowIfNull(eligibleResourceRows);
        return Task.Run(() => VerifyCoreAsync(snapshot, expectedDigests, eligibleResourceRows,
            revisionProvider ?? new FileRevisionProvider(), cancellationToken, verifiedIndexLease), cancellationToken);
    }

    private static async Task<LocalizationPayloadVerificationLease> VerifyCoreAsync(ArchiveSnapshot snapshot,
        IReadOnlyDictionary<long, string> expectedDigests, IReadOnlyList<ResourceRecord> eligibleResourceRows,
        ISourceRevisionProvider revisions, CancellationToken token, LocalizationPayloadVerificationLease? verifiedIndexLease)
    {
        SafeFileHandle? indexHandle = null;
        var volumeHandles = new Dictionary<uint, (VolumeSource Source, SafeFileHandle Handle)>();
        var transferred = false;
        try
        {
            indexHandle = SourceIdentity.OpenPinned(snapshot.IndexPath);
            SourceIdentity.EnsureUnchanged(revisions, indexHandle, snapshot.IndexPath, snapshot.IndexStamp);
            verifiedIndexLease?.EnsureProtectsIndex(snapshot);
            if (verifiedIndexLease is null)
            {
                var beforeIndexHash = await SourceIdentity.HashAsync(indexHandle, snapshot.IndexStamp.Length, token,
                    phase: "Validating localization index").ConfigureAwait(false);
                if (!string.Equals(beforeIndexHash, snapshot.IndexContentId, StringComparison.Ordinal))
                    throw new ArchiveException("SourceChanged", "The archive index contents differ from the selected localization snapshot.");
            }

            var validatedRows = new List<(ResourceRecord Row, VolumeSource Volume)>(eligibleResourceRows.Count);
            var seenOrdinals = new HashSet<long>();
            var volumesByPackage = new Dictionary<uint, VolumeSource>();
            foreach (var volume in snapshot.Volumes)
                if (!volumesByPackage.TryAdd(volume.Package, volume))
                    throw new ArchiveException("SourceChanged", "The archive snapshot contains duplicate source volume packages.");
            foreach (var row in eligibleResourceRows)
            {
                token.ThrowIfCancellationRequested();
                if (row.Type != 1016 || row.Validity != ResourceValidity.Valid || row.Offset < 0 || row.StoredSize < 0 ||
                    !seenOrdinals.Add(row.Ordinal) || !MatchesSnapshotRow(snapshot, row))
                    throw new ArchiveException("SourceChanged", "The eligible localization resource list does not match the selected archive snapshot.");
                if (!volumesByPackage.TryGetValue(row.Package, out var volume) || !volume.Stamp.Exists || volume.Stamp.Error is not null)
                    throw new ArchiveException("SourceChanged", "An eligible localization resource volume is unavailable.");
                long end;
                try { end = checked(row.Offset + row.StoredSize); }
                catch (OverflowException ex) { throw new ArchiveException("SourceChanged", "An eligible localization resource extent is invalid.", ex); }
                if (volume.Stamp.Length < 0 || end > volume.Stamp.Length)
                    throw new ArchiveException("SourceChanged", "An eligible localization resource extent exceeds its source volume.");
                validatedRows.Add((row, volume));
            }

            foreach (var volume in validatedRows.Select(item => item.Volume).DistinctBy(item => item.Package))
            {
                token.ThrowIfCancellationRequested();
                var handle = SourceIdentity.OpenPinned(volume.FilePath);
                try { SourceIdentity.EnsureUnchanged(revisions, handle, volume.FilePath, volume.Stamp); }
                catch { handle.Dispose(); throw; }
                volumeHandles.Add(volume.Package, (volume, handle));
            }

            var digestsMatch = expectedDigests.Count == validatedRows.Count;
            var hashBuffer = new byte[HashBufferSize];
            foreach (var (row, volume) in validatedRows)
            {
                token.ThrowIfCancellationRequested();
                var handle = volumeHandles[row.Package].Handle;
                var digest = await HashResourceAsync(handle, row, hashBuffer, token).ConfigureAwait(false);
                if (!expectedDigests.TryGetValue(row.Ordinal, out var expected) || !IsSha256(expected) ||
                    !string.Equals(expected, digest, StringComparison.OrdinalIgnoreCase))
                    digestsMatch = false;
            }

            SourceIdentity.EnsureUnchanged(revisions, indexHandle, snapshot.IndexPath, snapshot.IndexStamp);
            foreach (var (source, sourceHandle) in volumeHandles.Values)
                SourceIdentity.EnsureUnchanged(revisions, sourceHandle, source.FilePath, source.Stamp);
            if (verifiedIndexLease is null)
            {
                var afterIndexHash = await SourceIdentity.HashAsync(indexHandle, snapshot.IndexStamp.Length, token,
                    phase: "Revalidating localization index").ConfigureAwait(false);
                if (!string.Equals(afterIndexHash, snapshot.IndexContentId, StringComparison.Ordinal))
                    throw new ArchiveException("SourceChanged", "The archive index contents changed during localization verification.");
            }
            else verifiedIndexLease.EnsureProtectsIndex(snapshot);
            SourceIdentity.EnsureUnchanged(revisions, indexHandle, snapshot.IndexPath, snapshot.IndexStamp);

            var lease = new LocalizationPayloadVerificationLease(snapshot.IndexPath, snapshot.IndexStamp, indexHandle,
                volumeHandles.Values.Select(item => (item.Source.FilePath, item.Source.Stamp, item.Handle)).ToArray(), revisions,
                digestsMatch, snapshot.IndexContentId);
            transferred = true;
            return lease;
        }
        finally
        {
            if (!transferred)
            {
                indexHandle?.Dispose();
                foreach (var (_, handle) in volumeHandles.Values) handle.Dispose();
            }
        }
    }

    private static async Task<string> HashResourceAsync(SafeFileHandle handle, ResourceRecord row, byte[] buffer,
        CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        while (offset < row.StoredSize)
        {
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, row.StoredSize - offset);
            var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, count), checked(row.Offset + offset), token)
                .ConfigureAwait(false);
            if (read == 0) throw new ArchiveException("SourceChanged", "An eligible localization resource ended during digest verification.");
            hash.AppendData(buffer.AsSpan(0, read));
            offset += read;
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool MatchesSnapshotRow(ArchiveSnapshot snapshot, ResourceRecord row)
    {
        if (row.Ordinal >= 0 && row.Ordinal < snapshot.Records.Count && snapshot.Records[checked((int)row.Ordinal)] == row)
            return true;
        return snapshot.Records.SingleOrDefault(candidate => candidate.Ordinal == row.Ordinal) == row;
    }

    private static bool IsSha256(string? value)
    {
        if (value is null) return false;
        if (value.Length != 64) return false;
        try { return Convert.FromHexString(value).Length == 32; }
        catch (FormatException) { return false; }
    }
}

/// <summary>Keeps the verified source files pinned until the caller finishes using the result.</summary>
public sealed class LocalizationPayloadVerificationLease : IAsyncDisposable
{
    private readonly string indexPath;
    private readonly FileStamp indexStamp;
    private readonly SafeFileHandle indexHandle;
    private readonly (string Path, FileStamp Stamp, SafeFileHandle Handle)[] volumes;
    private readonly ISourceRevisionProvider revisions;
    private readonly string indexContentId;
    private int disposed;

    internal LocalizationPayloadVerificationLease(string indexPath, FileStamp indexStamp, SafeFileHandle indexHandle,
        (string Path, FileStamp Stamp, SafeFileHandle Handle)[] volumes, ISourceRevisionProvider revisions,
        bool digestsMatch, string indexContentId)
    {
        this.indexPath = indexPath;
        this.indexStamp = indexStamp;
        this.indexHandle = indexHandle;
        this.volumes = volumes;
        this.revisions = revisions;
        DigestsMatch = digestsMatch;
        this.indexContentId = indexContentId;
    }

    public bool DigestsMatch { get; }

    internal void EnsureProtectsIndex(ArchiveSnapshot snapshot)
    {
        EnsureUnchanged();
        if (indexContentId != snapshot.IndexContentId || indexPath != snapshot.IndexPath || indexStamp != snapshot.IndexStamp)
            throw new ArchiveException("InvalidVerificationLease", "The verification lease belongs to another archive index.");
    }

    public void EnsureUnchanged()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        SourceIdentity.EnsureUnchanged(revisions, indexHandle, indexPath, indexStamp);
        foreach (var (path, stamp, handle) in volumes)
            SourceIdentity.EnsureUnchanged(revisions, handle, path, stamp);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            indexHandle.Dispose();
            foreach (var (_, _, handle) in volumes) handle.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
