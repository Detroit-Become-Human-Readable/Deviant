namespace Deviant.Assets;

public sealed record TextureBrowseToken(SourceRevision Revision, string Profile, long Generation);

public sealed record TextureBrowseIndexCoverage(long Resources, long TexturesWithMetadata, long OriginalNames,
    long OwnerNames, long InferredNames, long FallbackNames, long Aliases, long Conflicts,
    long Images, long Lighting, long LodBuffers, long MetadataRecords);

public sealed record TextureBrowseResponse(TextureBrowseToken Token, TextureDiscoveryPage Page,
    TextureDiscoveryFilterOptions Options, TextureBrowseIndexCoverage Coverage);

public interface ITextureBrowseSession : IAsyncDisposable
{
    Task<TextureBrowseResponse> ReadAsync(TextureDiscoveryQuery query, CancellationToken token = default);
    Task<TextureBrowseResponse?> LocateAsync(ResourceIdentity identity, TextureDiscoveryQuery query, CancellationToken token = default);
}
