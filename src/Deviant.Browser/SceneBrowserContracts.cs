using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Browser;

public sealed record SceneDiscoveryOptions(int MaximumCandidatesPerBatch = 16,
    long MaximumStoredBytesPerBatch = 64L * 1024 * 1024,
    int MaximumContainerStoredBytes = 16 * 1024 * 1024,
    int MaximumContainerDecodedBytes = 64 * 1024 * 1024);

public sealed record SceneDiscoveryProgress(bool IsRunning, int Visited, int Candidates, int Available,
    bool HasMore, string? Message = null);

public sealed record SceneCatalogPage(IReadOnlyList<AssetDescriptor> Entries, long Total, long Offset);

public sealed record SceneLoadedSection(AssetDescriptor Descriptor, DecodeResult<SceneSectionDocument> Result,
    IReadOnlyDictionary<ResourceIdentity, IReadOnlyList<NameEvidence>> NamesByNode,
    IReadOnlyDictionary<ResourceIdentity, IReadOnlyList<SceneEntityRecord>> EntitiesByNode,
    IReadOnlyDictionary<ResourceIdentity, IReadOnlyList<AssetRelationship>> ReferencesByNode);

public interface ISceneCatalogSession : IAsyncDisposable
{
    ArchiveSnapshot Snapshot { get; }
    SourceRevision Revision { get; }
    SceneDiscoveryProgress Progress { get; }
    event EventHandler? Updated;
    Task DiscoverMoreAsync(CancellationToken token = default);
    Task<SceneCatalogPage> QueryAsync(string? search = null, long offset = 0, int limit = 100, CancellationToken token = default);
    Task<SceneLoadedSection> LoadAsync(ResourceIdentity identity, CancellationToken token = default);
    Task RevalidateAsync(SceneLoadedSection section, CancellationToken token = default);
    Task ExportMetadataAsync(SceneLoadedSection section, string destination, CancellationToken token = default);
    void CancelDiscovery();
}
