using System.Security.Cryptography;
using Deviant.Core;
using Deviant.Formats.Audio.Model;

namespace Deviant.Formats.Audio;

/// <summary>Checks that audio metadata inputs still match the bytes used to build the catalog.</summary>
public sealed class AudioMetadataVerifier(ArchiveSnapshot snapshot, ISourceRevisionProvider? revisionProvider = null)
{
    private const int MaximumExtents = 65_536;
    private const long MaximumBytes = 1024L * 1024 * 1024;
    private readonly ISourceRevisionProvider revisions = revisionProvider ?? new FileRevisionProvider();
    private readonly SemaphoreSlim verificationGate = new(2);
    internal ArchiveSnapshot Snapshot => snapshot;
    internal ISourceRevisionProvider RevisionProvider => revisions;

    public AudioMetadataVerificationLease CreateLease() => new(this);
    internal AudioMetadataVerificationLease CreateLease(int maximumCachedExtents, long maximumCachedBytes) =>
        new(this, maximumCachedExtents, maximumCachedBytes);

    public Task VerifyMediaAsync(MediaEntry media, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        ValidateIdentity(media.Identity);
        ValidateIdentity(media.PayloadIdentity);
        ValidateDigestOwners(media.MetadataReadDigests);
        return VerifyAsync(media.MetadataReadDigests, media.MetadataReadCoverageComplete, "media classification", cancellationToken);
    }

    public Task VerifyBankAsync(BankInfo bank, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ValidateIdentity(bank.Identity);
        ValidateIdentity(bank.ContainerIdentity);
        ValidateDigestOwners(bank.MetadataReadDigests);
        return VerifyAsync(bank.MetadataReadDigests, bank.MetadataReadCoverageComplete, "bank classification", cancellationToken);
    }

    internal void ValidateIdentity(Deviant.Assets.ResourceIdentity? identity)
    {
        if (identity is not null && (identity.IndexContentId != snapshot.IndexContentId || identity.Ordinal < 0 || identity.Ordinal >= snapshot.Records.Count))
            throw new ArchiveException("SourceChanged", "An audio metadata owner identity does not belong to the selected archive revision.");
    }

    internal void ValidateDigestOwners(IReadOnlyList<AudioMetadataReadDigest> digests)
    {
        foreach (var digest in digests) ValidateIdentity(digest.Owner);
    }

    private async Task VerifyAsync(IReadOnlyList<AudioMetadataReadDigest> digests, bool coverageComplete,
        string subject, CancellationToken token)
    {
        await VerifyIndexRevisionAsync(token).ConfigureAwait(false);
        if (!coverageComplete || digests.Count == 0)
        {
            if (!snapshot.IndexStamp.Reliable || !snapshot.ReliableVolumeStamps)
                throw new ArchiveException("SourceChanged", $"The {subject} has no complete metadata read baseline for a source with unreliable stamps.");
            await VerifyAllVolumeStampsAsync(token).ConfigureAwait(false);
            await VerifyIndexRevisionAsync(token).ConfigureAwait(false);
            return;
        }
        if (digests.Count > MaximumExtents)
            throw new ArchiveException("SourceChanged", $"The {subject} metadata baseline exceeds its verification limit.");

        long totalBytes = 0;
        await verificationGate.WaitAsync(token).ConfigureAwait(false);
        var handles = new Dictionary<string, Microsoft.Win32.SafeHandles.SafeFileHandle>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var usedVolumes = new Dictionary<string, VolumeSource>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        try
        {
            var buffer = new byte[128 * 1024];
            foreach (var digest in digests)
            {
                token.ThrowIfCancellationRequested();
                var slice = digest.Slice;
                if (slice.Offset < 0 || slice.Length <= 0 || slice.Length > MaximumBytes - totalBytes)
                    throw new ArchiveException("SourceChanged", $"A source extent used by the {subject} baseline is outside its verification limit.");
                totalBytes += slice.Length;
                var volumes = snapshot.Volumes.Where(volume => PathsEqual(volume.FilePath, slice.FilePath)).Take(2).ToArray();
                if (volumes.Length != 1 || !volumes[0].Stamp.Exists || volumes[0].Stamp.Error is not null)
                    throw new ArchiveException("SourceChanged", $"A source file used by the {subject} baseline is unavailable.");

                var volume = volumes[0];
                if (!handles.TryGetValue(volume.FilePath, out var handle))
                {
                    handle = SourceIdentity.OpenPinned(volume.FilePath);
                    SourceIdentity.EnsureUnchanged(revisions, handle, volume.FilePath, volume.Stamp);
                    handles.Add(volume.FilePath, handle);
                    usedVolumes.Add(volume.FilePath, volume);
                }
                if (!IndexLayout.InBounds(slice.Offset, slice.Length, RandomAccess.GetLength(handle)))
                    throw new ArchiveException("SourceChanged", $"A source extent used by the {subject} baseline is out of bounds.");

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long consumed = 0;
                while (consumed < slice.Length)
                {
                    token.ThrowIfCancellationRequested();
                    var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0,
                            (int)Math.Min(buffer.Length, slice.Length - consumed)), checked(slice.Offset + consumed), token)
                        .ConfigureAwait(false);
                    if (read == 0)
                        throw new ArchiveException("SourceChanged", $"A source extent used by the {subject} ended during verification.");
                    hash.AppendData(buffer.AsSpan(0, read));
                    consumed += read;
                }
                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!StringComparer.OrdinalIgnoreCase.Equals(actual, digest.Sha256))
                    throw new ArchiveException("SourceChanged", $"The {subject} source bytes changed after audio metadata was read.");
            }
            foreach (var (path, volume) in usedVolumes)
                SourceIdentity.EnsureUnchanged(revisions, handles[path], path, volume.Stamp);
        }
        finally
        {
            foreach (var handle in handles.Values) handle.Dispose();
            verificationGate.Release();
        }
        await VerifyIndexRevisionAsync(token).ConfigureAwait(false);
    }

    internal async Task VerifyIndexRevisionAsync(CancellationToken token)
    {
        using var handle = SourceIdentity.OpenPinned(snapshot.IndexPath);
        await VerifyPinnedIndexRevisionAsync(handle, token, verifyContent: true).ConfigureAwait(false);
    }

    internal async Task VerifyPinnedIndexRevisionAsync(Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        CancellationToken token, bool verifyContent)
    {
        SourceIdentity.EnsureUnchanged(revisions, handle, snapshot.IndexPath, snapshot.IndexStamp);
        if (verifyContent && !snapshot.IndexStamp.Reliable)
        {
            if (snapshot.IndexStamp.Length < 0 || snapshot.IndexStamp.Length > 128L * 1024 * 1024)
                throw new ArchiveException("SourceChanged", "The archive index has an unreliable stamp and exceeds its verification limit.");
            var current = await SourceIdentity.HashAsync(handle, snapshot.IndexStamp.Length, token, phase: "Verifying audio metadata").ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(current, snapshot.IndexContentId))
                throw new ArchiveException("SourceChanged", "The archive index content differs from the selected audio revision.");
        }
        SourceIdentity.EnsureUnchanged(revisions, handle, snapshot.IndexPath, snapshot.IndexStamp);
    }

    internal Task VerifyAllVolumeStampsAsync(CancellationToken token)
    {
        foreach (var volume in snapshot.Volumes.Where(volume => volume.Stamp.Exists))
        {
            token.ThrowIfCancellationRequested();
            using var handle = SourceIdentity.OpenPinned(volume.FilePath);
            SourceIdentity.EnsureUnchanged(revisions, handle, volume.FilePath, volume.Stamp);
        }
        return Task.CompletedTask;
    }

    internal static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
