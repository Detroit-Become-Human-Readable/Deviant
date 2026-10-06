namespace Deviant.Core;

public enum ResourceValidity { Valid, MissingVolume, InvalidRange, SourceUnavailable }
public sealed record ResourceRecord(long Ordinal, uint Type, uint Flags, uint Id, uint Package,
    long Offset, long StoredSize, uint AlternativeSize, string VolumeName,
    ResourceValidity Validity = ResourceValidity.Valid, string? Diagnostic = null);
public sealed record FileStamp(bool Exists, string FileIdentity, long Length, long LastWriteUtcTicks,
    long ChangeTimeUtcTicks, bool Reliable, string? Error = null);
public sealed record VolumeSource(uint Package, string RelativeName, string FilePath, FileStamp Stamp);
public sealed record ArchiveProgress(string Phase, long Completed, long Total);
public sealed record SourceDiagnostic(string Code, string Message, long? Ordinal = null, uint? Package = null);
public sealed record ArchiveSnapshot(string IndexPath, string IndexContentId, uint Version,
    uint UnknownHeaderWord, byte[] ReservedHeaderBytes, uint DeclaredCount,
    IReadOnlyList<ResourceRecord> Records, IReadOnlyList<VolumeSource> Volumes,
    FileStamp IndexStamp, string PhysicalSourceStamp, IReadOnlyList<SourceDiagnostic> Diagnostics)
{
    public string SourceRoot => Path.GetDirectoryName(IndexPath)!;
    public bool ReliableVolumeStamps => Volumes.All(v => !v.Stamp.Exists || v.Stamp.Reliable);
}
public sealed record ResourceQuery(uint? Type = null, uint? Id = null, uint? Package = null,
    string? Search = null, long Offset = 0, int Limit = 100);
public sealed record ArchiveContentVerification(string IndexContentId, string ArchiveContentId,
    IReadOnlyList<ContentFileDigest> Files, DateTimeOffset VerifiedAt);
public sealed record ContentFileDigest(string Role, uint? Package, string RelativeName, long Length, string Sha256);

public interface IArchiveReader
{
    Task<ArchiveSnapshot> OpenAsync(string input, IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
public interface IResourceCatalog : IDisposable
{
    ArchiveSnapshot Snapshot { get; }
    string CacheKey { get; }
    bool ReusedIndexRows { get; }
    long Count(ResourceQuery query);
    long Count(ResourceQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Count(query);
    }
    IReadOnlyList<ResourceRecord> Query(ResourceQuery query, CancellationToken cancellationToken = default);
}
public sealed class ArchiveException(string code, string message, Exception? inner = null) : IOException(message, inner)
{
    public string Code { get; } = code;
}
