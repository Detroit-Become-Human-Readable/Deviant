using Deviant.Core;
using Microsoft.Win32.SafeHandles;

namespace Deviant.Archives;

public static class ArchiveVerifier
{
    public static async Task<ArchiveContentVerification> VerifyContentAsync(ArchiveSnapshot snapshot,
        IProgress<ArchiveProgress>? progress = null, CancellationToken cancellationToken = default,
        ISourceRevisionProvider? revisionProvider = null)
    {
        var provider = revisionProvider ?? new FileRevisionProvider();
        var pinned = new List<(string Role, uint? Package, string Path, FileStamp Stamp, SafeFileHandle Handle)>();
        try
        {
            pinned.Add(("index", null, snapshot.IndexPath, snapshot.IndexStamp, SourceIdentity.OpenPinned(snapshot.IndexPath)));
            foreach (var volume in snapshot.Volumes) pinned.Add(("volume", volume.Package, volume.FilePath, volume.Stamp, SourceIdentity.OpenPinned(volume.FilePath)));
            foreach (var extension in new[] { ".dep", ".dbg" })
            {
                var path = Path.ChangeExtension(snapshot.IndexPath, extension); var stamp = provider.Capture(path);
                if (stamp.Exists) pinned.Add(("sidecar", null, path, stamp, SourceIdentity.OpenPinned(path)));
            }
            foreach (var file in pinned) SourceIdentity.EnsureUnchanged(provider, file.Handle, file.Path, file.Stamp);
            var digests = new List<ContentFileDigest>();
            foreach (var file in pinned)
            {
                var hash = await SourceIdentity.HashAsync(file.Handle, file.Stamp.Length, cancellationToken, progress, "Verifying " + Path.GetFileName(file.Path));
                if (file.Role == "index" && hash != snapshot.IndexContentId) throw new ArchiveException("SourceChanged", "Index contents changed since selection.");
                digests.Add(new(file.Role, file.Package, Path.GetFileName(file.Path), file.Stamp.Length, hash));
            }
            foreach (var file in pinned) SourceIdentity.EnsureUnchanged(provider, file.Handle, file.Path, file.Stamp);
            // Sidecar presence is part of the verified set, including absence.
            foreach (var extension in new[] { ".dep", ".dbg" })
            {
                var path = Path.ChangeExtension(snapshot.IndexPath, extension);
                if (provider.Capture(path).Exists != pinned.Any(f => f.Path == path)) throw new ArchiveException("SourceChanged", "Sidecar presence changed during verification.");
                if (!pinned.Any(f => f.Path == path)) digests.Add(new("absent-sidecar", null, Path.GetFileName(path), 0, ""));
            }
            return new(snapshot.IndexContentId, SourceIdentity.ContentId(digests), digests, DateTimeOffset.UtcNow);
        }
        finally { foreach (var file in pinned) file.Handle.Dispose(); }
    }
}
