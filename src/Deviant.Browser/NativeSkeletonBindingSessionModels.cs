using Deviant.Assets;

namespace Deviant.Browser;

public enum NativeSkeletonBindingSessionStatus
{
    Bound,
    EntityNodeMissing,
    EntityNodeAmbiguous,
    EntityNodeUnresolved
}

/// <summary>Small result for UI admission; borrowed parsed data is exposed only by a consumer lease.</summary>
public sealed record NativeSkeletonBindingSessionResult(
    ResourceIdentity SelectedIdentity,
    ResourceIdentity ContainerIdentity,
    SourceRevision Revision,
    uint? GroupIndex,
    NativeSkeletonBindingSessionStatus Status,
    ResourceReference? EntityNode,
    ReferenceResolution? EntityNodeResolution,
    ResourceIdentity? NodeArrayIdentity,
    string? Diagnostic);
