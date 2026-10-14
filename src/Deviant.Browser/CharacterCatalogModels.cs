using System.Numerics;
using System.Text.Json.Serialization;
using Deviant.Assets;

namespace Deviant.Browser;

/// <summary>Raw per-group mesh counts and bounds; selector values are retained without interpretation.</summary>
public sealed record NativeModelGroupStatistics(long SummedVertexWindowCount, long SummedIndexWindowCount,
    int PartCount, IReadOnlyList<uint> VertexBufferElementCounts, IReadOnlyList<uint> IndexBufferElementCounts,
    IReadOnlyDictionary<uint, long> RawSelectorHistogram,
    IReadOnlyList<float> StoredBoundsObb, IReadOnlyList<float> StoredBoundsExtra,
    DecodedExtent BoundsExtent)
{
    [JsonIgnore]
    public NativeMeshBounds Bounds => new(StoredBoundsObb,
        new Vector4(StoredBoundsExtra[0], StoredBoundsExtra[1], StoredBoundsExtra[2], StoredBoundsExtra[3]),
        BoundsExtent);
}

public enum CharacterLabelUtf8Status { Valid, Invalid, TooLong }
public enum CharacterCatalogIssueKind { CatalogRead, CatalogReference, OwnerOpen, ModelBinding, MeshMetadata, AssetIndex }

public sealed record CharacterCatalogLink(ResourceIdentity CatalogIdentity, int RelationshipIndex,
    string? ReferenceTableSha256, AssetRelationship Relationship, string? Diagnostic = null);

public sealed record CharacterStoredLabelObservation(ResourceIdentity MeshDataIdentity, int LodOrdinal,
    int GroupOrdinal, int EntryOrdinal, int StringOrdinal, string? Text,
    CharacterLabelUtf8Status Utf8Status, int RawByteLength, string RawBytesSha256,
    DecodedExtent BytesExtent, uint RawTrailingWord, DecodedExtent TrailingWordExtent);

public sealed record CharacterModelLodChoice(int LodOrdinal, float Threshold, uint RawThresholdBits,
    ResourceReference MeshDataReference, ReferenceResolution MeshDataResolution,
    ResourceIdentity? MeshDataIdentity, uint? GroupIndex, int? GroupCount, string? StoredMeshName,
    NativeModelSelection? Selection, DecodedExtent LodExtent,
    DecodedExtent? MeshDataMetadataExtent, DecodedExtent? SelectedGroupBoundsExtent,
    string? Diagnostic)
{
    public NativeModelGroupStatistics? GroupStatistics { get; init; }
    public bool? HasSkinPalette { get; init; }
}

public sealed record CharacterModelSourceSummary(ResourceIdentity Identity, SourceRevision Revision,
    uint OwnerNativeType, uint OwnerNativeId, IReadOnlyList<CharacterModelLodChoice> Lods,
    IReadOnlyList<NameEvidence> StoredNameEvidence, IReadOnlyList<SourceExtent> SourceExtents,
    IReadOnlyList<CharacterStoredLabelObservation> StoredLabelObservations,
    int OmittedLabelObservationCount, bool Complete)
{
    public CharacterModelStatisticsAvailability StatisticsAvailability { get; init; } =
        CharacterModelStatisticsAvailability.Available;
    public string? InventoryDisplayLabel { get; init; }
    public bool SourceSkeletonV15Validated { get; init; }
    public IReadOnlyDictionary<string, string>? CachedVerifiedMetadata { get; init; }
}

public enum CharacterModelStatisticsAvailability { Available, UnavailableInLegacyCache, DeferredUntilSelection }

public sealed record CharacterCatalogModelEntry(AssetDescriptor Descriptor,
    CharacterModelSourceSummary SourceSummary,
    IReadOnlyList<CharacterModelBindingResult> Bindings,
    IReadOnlyList<CharacterCatalogLink> CatalogLinks)
{
    public CharacterCatalogCoverageSnapshot? Coverage { get; init; }
}

public sealed record CharacterCatalogCoverageSnapshot(SourceRevision Revision,
    IReadOnlyList<ResourceIdentity> CoveredCatalogs, int TotalCatalogs,
    int UnscannedCatalogs, bool AllCatalogsVisited);

public sealed class CharacterCatalogModelSnapshot : IDisposable
{
    private IDisposable? retention;
    internal CharacterCatalogModelSnapshot(CharacterCatalogModelEntry entry, IDisposable retention)
    { Entry = entry; this.retention = retention; }
    public CharacterCatalogModelEntry Entry { get; }
    public void Dispose() => Interlocked.Exchange(ref retention, null)?.Dispose();
}

public sealed class CharacterCatalogAssetPage : IDisposable
{
    private IDisposable? retention;
    internal CharacterCatalogAssetPage(IReadOnlyList<AssetDescriptor> assets, IDisposable retention)
    { Assets = assets; this.retention = retention; }
    public IReadOnlyList<AssetDescriptor> Assets { get; }
    public void Dispose() => Interlocked.Exchange(ref retention, null)?.Dispose();
}

public sealed record CharacterCatalogIssue(CharacterCatalogIssueKind Kind, string Code,
    ResourceIdentity? CatalogIdentity = null, ResourceIdentity? OwnerIdentity = null,
    ResourceIdentity? ModelIdentity = null, int? RelationshipIndex = null);

public sealed record CharacterCatalogProgress(int StartCatalogIndex, int NextCatalogIndex,
    int TotalCatalogs, int CatalogsRead, int VerifiedCatalogs, int UnsupportedCatalogs,
    long ReferenceRowsRead, int UniqueOwnerTargets, int OwnersOpened, int OwnerFailures,
    int ModelMembersSeen, int ModelsBound, int UnsupportedModels, int UnscannedCatalogs,
    bool RequestedCatalogsComplete, bool AllCatalogsVisited, bool IsComplete);

public sealed class CharacterCatalogDiscoveryResult : IDisposable
{
    private IDisposable? retention;

    internal CharacterCatalogDiscoveryResult(SourceRevision revision,
        int startCatalogIndex, int nextCatalogIndex, int totalCatalogs,
        int catalogsRead, int verifiedCatalogs, int unsupportedCatalogs,
        long referenceRowsRead, int uniqueOwnerTargets, int ownersOpened,
        IReadOnlyList<CharacterCatalogLink> catalogLinks,
        IReadOnlyList<CharacterCatalogModelEntry> models,
        IReadOnlyList<CharacterCatalogIssue> issues,
        int unscannedCatalogs, bool requestedCatalogsComplete,
        bool allCatalogsVisited, bool isComplete, IDisposable retention)
    {
        Revision = revision;
        StartCatalogIndex = startCatalogIndex;
        NextCatalogIndex = nextCatalogIndex;
        TotalCatalogs = totalCatalogs;
        CatalogsRead = catalogsRead;
        VerifiedCatalogs = verifiedCatalogs;
        UnsupportedCatalogs = unsupportedCatalogs;
        ReferenceRowsRead = referenceRowsRead;
        UniqueOwnerTargets = uniqueOwnerTargets;
        OwnersOpened = ownersOpened;
        CatalogLinks = catalogLinks;
        Models = models;
        Issues = issues;
        UnscannedCatalogs = unscannedCatalogs;
        RequestedCatalogsComplete = requestedCatalogsComplete;
        AllCatalogsVisited = allCatalogsVisited;
        IsComplete = isComplete;
        this.retention = retention;
    }

    public SourceRevision Revision { get; }
    public int StartCatalogIndex { get; }
    public int NextCatalogIndex { get; }
    public int TotalCatalogs { get; }
    public int CatalogsRead { get; }
    public int VerifiedCatalogs { get; }
    public int UnsupportedCatalogs { get; }
    public long ReferenceRowsRead { get; }
    public int UniqueOwnerTargets { get; }
    public int OwnersOpened { get; }
    public IReadOnlyList<CharacterCatalogLink> CatalogLinks { get; }
    public IReadOnlyList<CharacterCatalogModelEntry> Models { get; }
    public IReadOnlyList<CharacterCatalogIssue> Issues { get; }
    public int UnscannedCatalogs { get; }
    public bool RequestedCatalogsComplete { get; }
    public bool AllCatalogsVisited { get; }
    public bool IsComplete { get; }
    public void Dispose() => Interlocked.Exchange(ref retention, null)?.Dispose();
}

public sealed record CharacterCatalogSessionOptions(int MaximumCatalogsPerBatch = 8,
    int MaximumLabelObservationsPerModel = 512, int MaximumLabelBytes = 4096,
    int MaximumLabelCharacters = 4096, long MaximumLabelTextBytesPerModel = 256 * 1024,
    bool IncludeSkeletonNameEvidence = true, int MaximumSkeletonNameEvidence = 512);

public static class CharacterCatalogLabelProjector
{
    public static string? CommonDotPrefix(IReadOnlyList<CharacterStoredLabelObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var values = observations.Where(item => item.Utf8Status == CharacterLabelUtf8Status.Valid &&
                !string.IsNullOrWhiteSpace(item.Text))
            .Select(item => item.Text!).Distinct(StringComparer.Ordinal).Take(64).ToArray();
        if (values.Length < 2) return null;
        var commonLength = values.Min(value => value.Length);
        for (var index = 0; index < commonLength; index++)
        {
            var character = values[0][index];
            if (values.Any(value => value[index] != character))
            {
                commonLength = index;
                break;
            }
        }
        if (commonLength is < 4 or > 128) return null;
        var common = values[0][..commonLength];
        var boundary = common.LastIndexOf('.');
        if (boundary < 3) return null;
        var prefix = common[..boundary].TrimEnd('.', '_', '-', ' ');
        return prefix.Length >= 3 ? prefix : null;
    }
}
