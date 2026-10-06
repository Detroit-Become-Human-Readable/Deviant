using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Export;

public static class AssetFileExporter
{
    public static async Task WriteAsync(ArchiveSnapshot snapshot, ResourceIdentity identity, SourceRevision revision, SourceExtent extent,
        string destination, bool overwrite, Func<Stream, CancellationToken, Task> write, CancellationToken token, CancellationToken lifetime,
        Func<CancellationToken,Task>? additionalValidation = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        await Task.Run(async () =>
        {
            var cancellation = linked.Token; cancellation.ThrowIfCancellationRequested();
            if (identity.IndexContentId != snapshot.IndexContentId || revision.IndexContentId != snapshot.IndexContentId ||
                revision.PhysicalSourceStamp != snapshot.PhysicalSourceStamp)
                throw new ArchiveException("SourceChanged", "The resource belongs to another workspace revision.");
            var row = snapshot.Records.Single(item => item.Ordinal == identity.Ordinal);
            var volume = snapshot.Volumes.Single(item => item.Package == row.Package);
            var full = Path.GetFullPath(destination); var directory = Path.GetDirectoryName(full)!;
            OutputSafety.ValidateFilePath(full, snapshot.SourceRoot);
            if (!overwrite && File.Exists(full)) throw new IOException("The output file already exists.");
            Directory.CreateDirectory(directory);
            using var outputPin = OutputSafety.PinDirectory(directory);
            using var indexPin = SourceIdentity.OpenPinned(snapshot.IndexPath);
            using var sourcePin = SourceIdentity.OpenPinned(volume.FilePath);
            var revisions = new FileRevisionProvider();
            void CheckRevision()
            {
                SourceIdentity.EnsureUnchanged(revisions, indexPin, snapshot.IndexPath, snapshot.IndexStamp);
                SourceIdentity.EnsureUnchanged(revisions, sourcePin, volume.FilePath, volume.Stamp);
            }
            async Task CheckPayloadAsync()
            {
                CheckRevision();
                var indexHash = await SourceIdentity.HashAsync(indexPin, snapshot.IndexStamp.Length, cancellation);
                if (!string.Equals(indexHash, snapshot.IndexContentId, StringComparison.OrdinalIgnoreCase))
                    throw new ArchiveException("SourceChanged", "The archive index changed. Reopen it before exporting.");
                CheckRevision();
                var bytes = await new ArchiveResourceResolver(snapshot).ReadAsync(new(snapshot.IndexContentId, row.Ordinal), 0, checked((int)row.StoredSize), cancellation);
                if (extent.RelativeSource != volume.RelativeName || extent.Offset != row.Offset || extent.Length != row.StoredSize || !string.Equals(extent.PayloadSha256, Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
                    throw new ArchiveException("SourceChanged", "The resource changed. Reopen it before exporting.");
                CheckRevision();
            }
            await CheckPayloadAsync();
            if(additionalValidation is not null)await additionalValidation(cancellation);
            var temporary = Path.Combine(directory, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            OutputSafety.ValidateFilePath(temporary, snapshot.SourceRoot);
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                { await write(stream, cancellation); await stream.FlushAsync(cancellation); stream.Flush(true); }
                await CheckPayloadAsync();
                if(additionalValidation is not null)await additionalValidation(cancellation);
                cancellation.ThrowIfCancellationRequested();
                OutputSafety.ValidateFilePath(full, snapshot.SourceRoot);
                File.Move(temporary, full, overwrite);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, linked.Token);
    }
}
