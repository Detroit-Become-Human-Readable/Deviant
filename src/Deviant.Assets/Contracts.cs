using Deviant.Core;
namespace Deviant.Assets;

public sealed record ResourceIdentity(string IndexContentId, long Ordinal, string? MemberPath = null)
{
    public string Key => $"{IndexContentId}:{Ordinal}:{MemberPath ?? ""}";
}
public sealed record SourceRevision(string IndexContentId, string PhysicalSourceStamp);
public enum IdentifierNamespace { Archive, Bank, Hirc, Media, Localization, Story, Native }
public enum ReferenceStatus { Unique, Missing, Ambiguous, Unresolved }
public sealed record ResourceReference(IdentifierNamespace Namespace, string Id, ResourceIdentity? Owner = null, uint? ResourceType = null);
public sealed record ReferenceResolution(ReferenceStatus Status, IReadOnlyList<ResourceIdentity> Candidates, string? Reason = null);
public enum NameOrigin { Stored, Verified, Candidate, UserAlias, Description }
public sealed record NameEvidence(string Text, NameOrigin Origin, string Source, string? CollisionGroup = null);
public enum AssetCompleteness { Complete, Unverified, Partial }
public enum AssetAvailability { Available, Missing, Ambiguous, Unsupported, Malformed }
[Flags] public enum AssetCapabilities { None = 0, Original = 1, Preview = 2, PartialPreview = 4, Wav = 8, Ogg = 16, Png = 32, Dds = 64, Glb = 128, Json = 256, Csv = 512, Svg = 1024, Fbx = 2048 }
public sealed record SourceExtent(string RelativeSource, long Offset, long Length, string? PayloadSha256 = null);
public sealed record DecodedExtent(ResourceIdentity Container, long Offset, long Length);
public sealed record AssetDescriptor(ResourceIdentity Identity, SourceRevision Revision, string Family, string Label,
    string? GroupKey, AssetAvailability Availability, AssetCompleteness Completeness, AssetCapabilities Capabilities,
    IReadOnlyList<NameEvidence> Names, IReadOnlyList<SourceExtent> Extents,
    string? Language = null, string? Codec = null, double? DurationSeconds = null, int? Channels = null,
    string? Reason = null, string? SortKey = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SearchTerms { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LookupKey { get; init; }
    public IReadOnlyDictionary<string,string> Metadata { get; init; } = new Dictionary<string,string>();
}
public enum AssetSort { Default, Name, Language, Codec, Duration, Channels, Availability, Completeness, Animations }
public sealed record CachedAssetPayloadFilter(SourceRevision Revision, string MetadataKey, string MetadataValue,
    string MemberPath, long MaximumOrdinal, int MaximumPayloadBytes = 4 * 1024 * 1024);
public sealed record AssetQuery(string? Search = null, string? GroupKey = null, string? Language = null,
    string? Codec = null, double? MinimumDuration = null, double? MaximumDuration = null, int? Channels = null,
    AssetAvailability? Availability = null, AssetCompleteness? Completeness = null, long Offset = 0, int Limit = 100, string? Family = null, string? ExactKey = null,
    AssetSort Sort = AssetSort.Default, bool Descending = false)
{
    public string? ExactIdentityKey { get; init; }
    public string? ModelKind { get; init; }
    public string? ModelNameProfile { get; init; }
    public string? CharacterGroup { get; init; }
    public long? MaximumCatalogRow { get; init; }
    public CachedAssetPayloadFilter? CachedPayloadFilter { get; init; }
}
public sealed record DecodeStep(string Decoder, string Version, string Options, string? PayloadSha256 = null);
public sealed record DecodeResult<T>(T? Value, ResourceIdentity Identity, SourceRevision Revision,
    IReadOnlyList<SourceExtent> Extents, IReadOnlyList<DecodeStep> Steps, IReadOnlyList<string> Diagnostics,
    IReadOnlyList<ResourceReference> UnresolvedReferences, IReadOnlyDictionary<string, string> UnknownFields);
public sealed record PreviewSession(ResourceIdentity Identity, SourceRevision Revision, string Path,
    AssetCompleteness Completeness, IAsyncDisposable Lease) : IAsyncDisposable
{ public ValueTask DisposeAsync() => Lease.DisposeAsync(); }
public enum AssetExportFormat { Original, Wav, Ogg, Png, Dds, Glb, Json, Csv, Svg, Fbx }
public sealed record AssetExportPlan(IReadOnlyList<ResourceIdentity> Resources, SourceRevision Revision,
    AssetExportFormat Format, string Destination, string Precision = "Pcm16");

public interface IAssetCatalog
{
    long Count(AssetQuery query, CancellationToken token = default);
    IReadOnlyList<AssetDescriptor> Query(AssetQuery query, CancellationToken token = default);
}
public interface IResourceResolver
{
    ReferenceResolution Resolve(ResourceReference reference);
    Task<byte[]> ReadAsync(ResourceIdentity identity, long resourceOffset, int length, CancellationToken token = default);
}
public interface IAssetDecoder
{
    string Family { get; }
    string Version { get; }
    bool Supports(AssetDescriptor descriptor);
}
public sealed class DecoderRegistry
{
    private readonly List<IAssetDecoder> decoders = [];
    public void Register(IAssetDecoder decoder) => decoders.Add(decoder);
    public IReadOnlyList<IAssetDecoder> Find(AssetDescriptor descriptor) => decoders.Where(d => d.Supports(descriptor)).ToArray();
}
