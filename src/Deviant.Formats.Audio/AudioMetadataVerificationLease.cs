using System.Security.Cryptography;
using Deviant.Core;
using Deviant.Formats.Audio.Model;
using Microsoft.Win32.SafeHandles;

namespace Deviant.Formats.Audio;

/// <summary>
/// Reuses metadata checks during one operation while retaining Windows read-share handles that prevent
/// source edits, replacement, or deletion until the lease is disposed.
/// </summary>
public sealed class AudioMetadataVerificationLease : IDisposable, IAsyncDisposable
{
    private const int MaximumExtentsPerCall = 65_536;
    private const long MaximumBytesPerCall = 1024L * 1024 * 1024;
    private const int DefaultMaximumCachedExtents = 65_536;
    private const long DefaultMaximumCachedBytes = 4L * 1024 * 1024 * 1024;
    private readonly AudioMetadataVerifier verifier;
    private readonly ArchiveSnapshot snapshot;
    private readonly ISourceRevisionProvider revisions;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, SafeFileHandle> handles = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, VolumeSource> usedVolumes = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<ExtentKey, string> verified = [];
    private readonly int maximumCachedExtents;
    private readonly long maximumCachedBytes;
    private SafeFileHandle? indexHandle;
    private long verifiedBytes;
    private bool indexContentVerified;
    private bool disposed;

    internal AudioMetadataVerificationLease(AudioMetadataVerifier verifier,
        int maximumCachedExtents = DefaultMaximumCachedExtents, long maximumCachedBytes = DefaultMaximumCachedBytes)
    {
        if (maximumCachedExtents <= 0 || maximumCachedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCachedExtents));
        this.verifier = verifier;
        snapshot = verifier.Snapshot;
        revisions = verifier.RevisionProvider;
        this.maximumCachedExtents = maximumCachedExtents;
        this.maximumCachedBytes = maximumCachedBytes;
    }

    internal int CachedExtentCount => verified.Count;
    internal long CachedBytes => verifiedBytes;

    /// <summary>Checks one media baseline. Exact extents already verified by this lease are reused while their files remain pinned.</summary>
    public Task VerifyMediaAsync(MediaEntry media, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(media);
        verifier.ValidateIdentity(media.Identity);
        verifier.ValidateIdentity(media.PayloadIdentity);
        verifier.ValidateDigestOwners(media.MetadataReadDigests);
        if (!OperatingSystem.IsWindows()) return verifier.VerifyMediaAsync(media, cancellationToken);
        return VerifyAsync(media.MetadataReadDigests, media.MetadataReadCoverageComplete, "media classification", cancellationToken);
    }

    /// <summary>Checks one bank baseline. Exact extents already verified by this lease are reused while their files remain pinned.</summary>
    public Task VerifyBankAsync(BankInfo bank, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(bank);
        verifier.ValidateIdentity(bank.Identity);
        verifier.ValidateIdentity(bank.ContainerIdentity);
        verifier.ValidateDigestOwners(bank.MetadataReadDigests);
        if (!OperatingSystem.IsWindows()) return verifier.VerifyBankAsync(bank, cancellationToken);
        return VerifyAsync(bank.MetadataReadDigests, bank.MetadataReadCoverageComplete, "bank classification", cancellationToken);
    }

    private async Task VerifyAsync(IReadOnlyList<AudioMetadataReadDigest> digests, bool coverageComplete,
        string subject, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await EnsureIndexPinnedAsync(token).ConfigureAwait(false);
            if (!coverageComplete || digests.Count == 0)
            {
                if (!snapshot.IndexStamp.Reliable || !snapshot.ReliableVolumeStamps)
                    throw Changed($"The {subject} has no complete metadata baseline for a source with unreliable stamps.");
                foreach (var volume in snapshot.Volumes.Where(volume => volume.Stamp.Exists))
                    await EnsureVolumePinnedAsync(volume, token).ConfigureAwait(false);
                await VerifyPinnedStampsAsync(usedVolumes.Values, token).ConfigureAwait(false);
                await VerifyIndexStampAsync(token).ConfigureAwait(false);
                return;
            }
            if (digests.Count > MaximumExtentsPerCall)
                throw Changed($"The {subject} metadata baseline exceeds its extent limit.");

            var expectedInCall = new Dictionary<ExtentKey, string>();
            var prepared = new List<(AudioMetadataReadDigest Digest, ExtentKey Key, VolumeSource Volume)>();
            long callBytes = 0;
            foreach (var digest in digests)
            {
                token.ThrowIfCancellationRequested();
                var slice = digest.Slice;
                if (slice.Offset < 0 || slice.Length <= 0)
                    throw Changed($"A source extent used by the {subject} baseline is invalid.");
                if (slice.Length > MaximumBytesPerCall - callBytes)
                    throw Changed($"The {subject} metadata baseline exceeds its per-call byte limit.");
                callBytes = checked(callBytes + slice.Length);
                var volumes = snapshot.Volumes.Where(volume => AudioMetadataVerifier.PathsEqual(volume.FilePath, slice.FilePath)).Take(2).ToArray();
                if (volumes.Length != 1 || !volumes[0].Stamp.Exists || volumes[0].Stamp.Error is not null)
                    throw Changed($"A source file used by the {subject} baseline is unavailable.");
                var volume = volumes[0];
                var key = new ExtentKey(Path.GetFullPath(volume.FilePath), slice.Offset, slice.Length);
                if (expectedInCall.TryGetValue(key, out var prior) && !StringComparer.OrdinalIgnoreCase.Equals(prior, digest.Sha256))
                    throw Changed($"The {subject} baseline records conflicting hashes for one source extent.");
                expectedInCall[key] = digest.Sha256;
                if (verified.TryGetValue(key, out prior) && !StringComparer.OrdinalIgnoreCase.Equals(prior, digest.Sha256))
                    throw Changed($"The {subject} baseline conflicts with bytes verified earlier in this operation.");
                prepared.Add((digest, key, volume));
            }

            foreach (var volume in prepared.Select(item => item.Volume).DistinctBy(item => item.FilePath))
                await EnsureVolumePinnedAsync(volume, token).ConfigureAwait(false);

            var allExtents = prepared.DistinctBy(item => item.Key).ToArray();
            long allBytes = 0;
            foreach (var item in allExtents) allBytes = checked(allBytes + item.Digest.Slice.Length);
            var newExtents = allExtents.Where(item => !verified.ContainsKey(item.Key)).ToArray();
            long newBytes = 0;
            foreach (var item in newExtents) newBytes = checked(newBytes + item.Digest.Slice.Length);
            var cacheThisCall = allExtents.Length <= maximumCachedExtents && allBytes <= maximumCachedBytes;
            if (!cacheThisCall || verified.Count > maximumCachedExtents - newExtents.Length ||
                verifiedBytes > maximumCachedBytes - newBytes)
            {
                // A cache rollover trades a little repeated hashing for a hard memory bound.
                // If this one call is larger than the reuse cache, verify it fully without retaining it.
                verified.Clear();
                verifiedBytes = 0;
                newExtents = allExtents;
                newBytes = allBytes;
            }

            var buffer = new byte[128 * 1024];
            foreach (var item in newExtents)
            {
                token.ThrowIfCancellationRequested();
                var handle = handles[item.Volume.FilePath];
                var slice = item.Digest.Slice;
                if (!IndexLayout.InBounds(slice.Offset, slice.Length, RandomAccess.GetLength(handle)))
                    throw Changed($"A source extent used by the {subject} baseline is out of bounds.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long consumed = 0;
                while (consumed < slice.Length)
                {
                    token.ThrowIfCancellationRequested();
                    var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0,
                            (int)Math.Min(buffer.Length, slice.Length - consumed)), checked(slice.Offset + consumed), token)
                        .ConfigureAwait(false);
                    if (read == 0) throw Changed($"A source extent used by the {subject} ended during verification.");
                    hash.AppendData(buffer.AsSpan(0, read));
                    consumed += read;
                }
                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!StringComparer.OrdinalIgnoreCase.Equals(actual, item.Digest.Sha256))
                    throw Changed($"The {subject} source bytes changed after audio metadata was read.");
                if (cacheThisCall)
                {
                    verified.Add(item.Key, actual);
                    verifiedBytes = checked(verifiedBytes + slice.Length);
                }
            }

            await VerifyPinnedStampsAsync(prepared.Select(item => item.Volume).DistinctBy(item => item.FilePath), token).ConfigureAwait(false);
            await VerifyIndexStampAsync(token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task EnsureIndexPinnedAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (indexHandle is null)
        {
            indexHandle = SourceIdentity.OpenPinned(snapshot.IndexPath);
            try
            {
                await verifier.VerifyPinnedIndexRevisionAsync(indexHandle, token, verifyContent: !indexContentVerified).ConfigureAwait(false);
                indexContentVerified = true;
            }
            catch
            {
                indexHandle.Dispose();
                indexHandle = null;
                throw;
            }
        }
    }

    private Task EnsureVolumePinnedAsync(VolumeSource volume, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!handles.TryGetValue(volume.FilePath, out var handle))
        {
            handle = SourceIdentity.OpenPinned(volume.FilePath);
            try { SourceIdentity.EnsureUnchanged(revisions, handle, volume.FilePath, volume.Stamp); }
            catch { handle.Dispose(); throw; }
            handles.Add(volume.FilePath, handle);
            usedVolumes.Add(volume.FilePath, volume);
        }
        return Task.CompletedTask;
    }

    private Task VerifyPinnedStampsAsync(IEnumerable<VolumeSource> volumes, CancellationToken token)
    {
        foreach (var volume in volumes.DistinctBy(volume => volume.FilePath))
        {
            token.ThrowIfCancellationRequested();
            if (!handles.TryGetValue(volume.FilePath, out var handle))
                throw Changed("A source volume required by this verification was not pinned.");
            SourceIdentity.EnsureUnchanged(revisions, handle, volume.FilePath, volume.Stamp);
        }
        return Task.CompletedTask;
    }

    private async Task VerifyIndexStampAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (indexHandle is null) throw Changed("The archive index is not pinned for this verification.");
        await verifier.VerifyPinnedIndexRevisionAsync(indexHandle, token, verifyContent: !indexContentVerified).ConfigureAwait(false);
        indexContentVerified = true;
    }

    private static ArchiveException Changed(string message) => new("SourceChanged", message);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        indexHandle?.Dispose();
        indexHandle = null;
        foreach (var handle in handles.Values) handle.Dispose();
        handles.Clear();
        usedVolumes.Clear();
        gate.Dispose();
    }

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

    private readonly record struct ExtentKey(string Path, long Offset, long Length);
}
