using Deviant.Core;
namespace Deviant.Assets;

public enum TextureNameKind { OriginalName, OriginalPath, VerifiedOwnerRole, InferredDescription, Fallback }
public enum TextureKind { Image, Lighting, LodBuffer }
public enum TextureKindFilter { All, Images, Lighting }
public enum TexturePreviewState { Idle, Loading, Ready, Pending, Unavailable, Failed, Cancelled }
public sealed record TexturePreviewFailure(string Code, string Message);
public enum TextureRelationshipKind { Material, Object, Container, RelatedTexture }
public sealed record TextureEvidence(ResourceIdentity Source, SourceRevision Revision, string Reader, string ReaderVersion,
    string SourceSha256, string Field, SourceExtent? PhysicalExtent = null, DecodedExtent? DecodedExtent = null,
    string? Consumer = null);
public sealed record TextureNameCandidate(string Text, TextureNameKind Kind, IReadOnlyList<TextureEvidence> Evidence,
    ResourceIdentity? Owner = null, string? Role = null, string? ConflictGroup = null);
public sealed record TextureDiscoveryLink(ResourceIdentity Identity, string Label, TextureRelationshipKind Kind,
    bool CanNavigate, bool Verified, IReadOnlyList<TextureEvidence> Evidence, string? Role = null);
public sealed record TextureMetadataContext(ResourceIdentity MetadataIdentity, IReadOnlyList<ResourceIdentity> CatalogIdentities,
    int? Width, int? Height, string? Format, string? Label = null);
public sealed record TextureDiscoveryEntry(ResourceIdentity Identity, uint ResourceId, uint ResourceType, string Label,
    TextureNameKind NameKind, string? ResourceName, string? ResourcePath, string? Alias, int? Width, int? Height,
    string? Format, string? VerifiedRole, IReadOnlyList<TextureDiscoveryLink> Owners,
    IReadOnlyList<TextureDiscoveryLink> Related, IReadOnlyList<TextureNameCandidate> NameCandidates,
    IReadOnlyList<string> ProvenanceLines, IReadOnlyList<string> ConflictLines, IReadOnlyList<string> AdvancedLines,
    string ExportStem, string SearchText, string? ContextKey = null, string? ContextLabel = null)
{
    public IReadOnlyList<string> CatalogKeys { get; init; } = Array.Empty<string>();
    public TextureKind Kind { get; init; } = TextureKind.Image;
    public bool IsCubeMap { get; init; }
    public IReadOnlyList<ResourceIdentity> MetadataIdentities { get; init; } = Array.Empty<ResourceIdentity>();
    public IReadOnlyList<TextureMetadataContext> MetadataContexts { get; init; } = Array.Empty<TextureMetadataContext>();
}
public sealed record TextureDiscoveryQuery(string? Search = null, int? Width = null, int? Height = null,
    string? Format = null, string? VerifiedRole = null, bool? HasVerifiedOwner = null, string? ContextKey = null,
    long Offset = 0, int Limit = 100, bool SortByName = true, string? CatalogKey = null, bool NamedFirst = true,
    TextureKindFilter Kind = TextureKindFilter.All, bool IncludeLodBuffers = false);
public sealed record TextureDiscoveryPage(IReadOnlyList<TextureDiscoveryEntry> Entries, long Total, long Offset);
public sealed record TextureDimensionOption(int Width, int Height) { public override string ToString() => $"{Width} x {Height}"; }
public sealed record TextureContextOption(string Key, string Label);
public sealed record TextureDiscoveryFilterOptions(IReadOnlyList<TextureDimensionOption> Dimensions,
    IReadOnlyList<string> Formats, IReadOnlyList<string> VerifiedRoles, IReadOnlyList<TextureContextOption> Contexts)
{
    public IReadOnlyList<TextureContextOption> Catalogs { get; init; } = Array.Empty<TextureContextOption>();
}
public sealed record TextureNamingCoverage(long IndexedResources, long MetadataInspected, long OriginalNames,
    long VerifiedOwnerRoles, long InferredDescriptions, long Fallbacks, long AliasCount, long Conflicts,
    long ContainersInspected, long ContainersAvailable, bool DiscoveryIncomplete,
    IReadOnlyList<string> EvidenceSourcesChecked, IReadOnlyList<string> RemainingBlockers)
{
    public long Images { get; init; }
    public long LightingImages { get; init; }
    public long LodBuffers { get; init; }
    public long MetadataRecords { get; init; }
}
public enum TextureOwnerQueryState { Pending, Ready, Incomplete }
public sealed record TextureOwnerQueryResult(ResourceIdentity Owner, SourceRevision Revision, TextureDiscoveryPage Page,
    TextureOwnerQueryState State, bool DiscoveryIncomplete, string? Reason = null);
public interface ITextureDiscoveryCatalog : IAsyncDisposable
{
    ArchiveSnapshot Snapshot { get; }
    SourceRevision Revision { get; }
    TextureDiscoveryFilterOptions FilterOptions { get; }
    TextureNamingCoverage Coverage { get; }
    event EventHandler? Updated;
    Task<TextureDiscoveryPage> QueryAsync(TextureDiscoveryQuery query, CancellationToken token = default);
    Task<TextureOwnerQueryResult> QueryByOwnerAsync(ResourceIdentity owner, long offset = 0, int limit = 100, CancellationToken token = default)
        => Task.FromException<TextureOwnerQueryResult>(new NotSupportedException("Owner queries are unavailable for this catalog."));
    Task<TextureDiscoveryPage?> LocateAsync(ResourceIdentity identity, TextureDiscoveryQuery query, CancellationToken token = default)
        => Task.FromResult<TextureDiscoveryPage?>(null);
    Task<TextureDiscoveryEntry?> GetDetailsAsync(ResourceIdentity identity, CancellationToken token = default);
    Task SetAliasAsync(ResourceIdentity identity, string? alias, CancellationToken token = default);
}
