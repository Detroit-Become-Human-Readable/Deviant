using System.Globalization;
using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Formats.Textures;

public enum TextureCatalogEntryKind { Raw, RawLod }

/// <summary>A typed CATALOG reference to an indexed ETF_RAW_ or ETF_RAWL resource.</summary>
public sealed record TextureCatalogReference(int ReferenceIndex, TextureCatalogEntryKind Kind,
    ResourceReference Reference, ReferenceStatus ResolutionStatus, IReadOnlyList<ResourceIdentity> Candidates,
    byte Trailer, SourceExtent Extent, bool IsDuplicate, string? Reason);

/// <summary>Texture membership links from one verified type-3 COM_CONT catalog.</summary>
public sealed record TextureCatalogMembership(ResourceIdentity CatalogIdentity, SourceRevision Revision,
    uint CatalogId, SourceExtent CatalogExtent, int SourceReferenceCount, int NonTextureReferenceCount,
    IReadOnlyList<TextureCatalogReference> TextureReferences);

/// <summary>
/// Filters typed relationships produced by the shared COM_CONT reader. It does not parse COM_CONT bytes,
/// infer pairings, or assign image dimensions or pixel formats.
/// </summary>
public sealed class TextureCatalogMembershipReader
{
    private const uint CatalogResourceType = 3;
    private const uint RawTextureResourceType = 2229;
    private const uint RawLodTextureResourceType = 2230;

    public TextureCatalogMembership Read(ArchiveSnapshot snapshot, ResourceIdentity catalogIdentity,
        SourceRevision revision, IReadOnlyList<AssetRelationship> relationships)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(catalogIdentity);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(relationships);
        if (!StringComparer.Ordinal.Equals(catalogIdentity.IndexContentId, snapshot.IndexContentId) ||
            !StringComparer.Ordinal.Equals(revision.IndexContentId, snapshot.IndexContentId) ||
            !StringComparer.Ordinal.Equals(revision.PhysicalSourceStamp,snapshot.PhysicalSourceStamp)||
            catalogIdentity.MemberPath is not null)
            throw new ArchiveException("InvalidTextureCatalog", "The CATALOG identity or revision does not belong to the selected archive snapshot.");

        var owner = snapshot.Records.SingleOrDefault(record => record.Ordinal == catalogIdentity.Ordinal)
            ?? throw new ArchiveException("MissingTextureCatalog", "The selected CATALOG resource is absent from the archive snapshot.");
        if (owner.Type != CatalogResourceType)
            throw new ArchiveException("InvalidTextureCatalog", "Texture membership requires an indexed type-3 CATALOG resource.");
        if (owner.Validity != ResourceValidity.Valid)
            throw new ArchiveException("InvalidTextureCatalog", "The selected CATALOG row is not valid in the archive snapshot.");

        var textureRelationships = new List<(int Index, TextureCatalogEntryKind Kind, AssetRelationship Relationship)>();
        foreach (var relationship in relationships)
        {
            if (relationship.Source != catalogIdentity)
                throw new ArchiveException("InvalidTextureCatalog", "A relationship list contains a different source resource.");
            if (!relationship.Evidence.Verified || !StringComparer.Ordinal.Equals(relationship.Evidence.Source, "COM_CONT v6"))
                throw new ArchiveException("UnverifiedTextureCatalog", "Texture membership requires verified COM_CONT v6 reference rows.");
            if (relationship.Reference.Namespace != IdentifierNamespace.Archive || relationship.Reference.Owner != catalogIdentity)
                throw new ArchiveException("InvalidTextureCatalog", "A CATALOG reference is missing its typed archive owner context.");

            if (relationship.Reference.ResourceType is not (RawTextureResourceType or RawLodTextureResourceType)) continue;
            if (!uint.TryParse(relationship.Reference.Id, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw new ArchiveException("InvalidTextureReference", "A texture CATALOG reference has a non-numeric archive ID.");
            if (relationship.Evidence.PhysicalExtent is not { Length: 9 } extent ||
                !StringComparer.Ordinal.Equals(extent.RelativeSource, owner.VolumeName) ||
                owner.Offset < 0 || owner.StoredSize < 29 || extent.Offset < owner.Offset)
                throw new ArchiveException("InvalidTextureReferenceExtent", "A CATALOG texture reference does not point to a bounded nine-byte table row.");
            var resourceOffset = extent.Offset - owner.Offset;
            if (resourceOffset < 20 || resourceOffset > owner.StoredSize - 9)
                throw new ArchiveException("InvalidTextureReferenceExtent", "A CATALOG texture reference does not point to a bounded nine-byte table row.");
            var relative = resourceOffset - 20;
            if (relative % 9 != 0)
                throw new ArchiveException("InvalidTextureReferenceExtent", "A CATALOG texture reference is not aligned to a nine-byte table row.");

            var index = checked((int)(relative / 9));
            var kind = relationship.Reference.ResourceType == RawTextureResourceType
                ? TextureCatalogEntryKind.Raw : TextureCatalogEntryKind.RawLod;
            textureRelationships.Add((index, kind, relationship));
        }

        var duplicateKeys = textureRelationships.GroupBy(item => (item.Relationship.Reference.ResourceType, item.Relationship.Reference.Id))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        var textureReferences = textureRelationships.OrderBy(item => item.Index).Select(item =>
        {
            var relationship = item.Relationship;
            return new TextureCatalogReference(item.Index, item.Kind, relationship.Reference,
                relationship.Resolution.Status, relationship.Resolution.Candidates, relationship.Evidence.SourceReferenceTrailer ?? 0,
                relationship.Evidence.PhysicalExtent!,
                duplicateKeys.Contains((relationship.Reference.ResourceType, relationship.Reference.Id)),
                relationship.Resolution.Reason);
        }).ToArray();

        return new(catalogIdentity, revision, owner.Id,
            new SourceExtent(owner.VolumeName, owner.Offset, owner.StoredSize), relationships.Count,
            relationships.Count - textureReferences.Length, textureReferences);
    }
}
