namespace Deviant.Assets;

public enum AssetRelationshipKind { Container, Member, Media, Event, Localization, Material, Texture, Rig, Animation, Entity, Dependency }
public sealed record RelationshipEvidence(bool Verified, string Source, string Description,
    SourceExtent? PhysicalExtent = null, DecodedExtent? DecodedExtent = null, byte? SourceReferenceTrailer = null);
public sealed record AssetRelationship(ResourceIdentity Source, AssetRelationshipKind Kind, string Label,
    ResourceReference Reference, ReferenceResolution Resolution, RelationshipEvidence Evidence)
{
    public ResourceIdentity? NavigationTarget => Evidence.Verified && Resolution.Status == ReferenceStatus.Unique && Resolution.Candidates.Count == 1
        ? Resolution.Candidates[0] : null;
}
