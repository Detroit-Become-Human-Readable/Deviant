using Deviant.Core;
namespace Deviant.Assets;

public sealed class ArchiveResourceResolver(ArchiveSnapshot snapshot, ISourceRevisionProvider? revisionProvider = null) : IResourceResolver
{
    private readonly ISourceRevisionProvider revisions = revisionProvider ?? new FileRevisionProvider();
    public ReferenceResolution Resolve(ResourceReference reference)
    {
        if (reference.Namespace != IdentifierNamespace.Archive || !uint.TryParse(reference.Id, out var id))
            return new(ReferenceStatus.Unresolved, [], "This identifier requires a format-specific resolver.");
        var candidates = snapshot.Records.Where(r => r.Id == id && (reference.ResourceType is null || reference.ResourceType == r.Type))
            .Select(r => new ResourceIdentity(snapshot.IndexContentId, r.Ordinal)).ToArray();
        return new(candidates.Length switch { 0 => ReferenceStatus.Missing, 1 => ReferenceStatus.Unique, _ => ReferenceStatus.Ambiguous }, candidates);
    }
    public async Task<byte[]> ReadAsync(ResourceIdentity identity, long resourceOffset, int length, CancellationToken token = default)
    {
        if (identity.IndexContentId != snapshot.IndexContentId || identity.MemberPath is not null)
            throw new ArchiveException("InvalidIdentity", "Resource does not belong to this archive.");
        var row = snapshot.Records.SingleOrDefault(r => r.Ordinal == identity.Ordinal)
            ?? throw new ArchiveException("MissingResource", "Resource is missing.");
        if (length < 0 || length > 16 * 1024 * 1024 || resourceOffset < 0 || resourceOffset > row.StoredSize - length)
            throw new ArchiveException("InvalidRange", "Read exceeds the resource extent.");
        var volume = snapshot.Volumes.Single(v => v.Package == row.Package);
        if (revisions.Capture(snapshot.IndexPath) != snapshot.IndexStamp)
            throw new ArchiveException("SourceChanged", "Archive index changed. Reopen it before continuing.");
        using var handle = File.OpenHandle(volume.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (revisions.Capture(handle, volume.FilePath) != volume.Stamp || revisions.Capture(volume.FilePath) != volume.Stamp)
            throw new ArchiveException("SourceChanged", "Archive source changed. Reopen it before continuing.");
        var bytes = new byte[length];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await RandomAccess.ReadAsync(handle, bytes.AsMemory(count), checked(row.Offset + resourceOffset + count), token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            count += read;
        }
        if (revisions.Capture(handle, volume.FilePath) != volume.Stamp || revisions.Capture(volume.FilePath) != volume.Stamp || revisions.Capture(snapshot.IndexPath) != snapshot.IndexStamp)
            throw new ArchiveException("SourceChanged", "Archive source changed during the read.");
        return bytes;
    }
}
