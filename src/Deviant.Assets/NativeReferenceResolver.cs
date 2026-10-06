using System.Globalization;

namespace Deviant.Assets;

/// <summary>Resolves typed native references within their owning container.</summary>
public sealed class NativeReferenceResolver
{
    private readonly ResourceIdentity container;
    private readonly HashSet<ResourceIdentity> members;
    private readonly IReadOnlyDictionary<(uint Type, uint Id), ResourceIdentity[]> byNativeId;

    public NativeReferenceResolver(NativeObjectDirectoryDocument directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        container = directory.Identity;
        members = directory.Objects.Select(item => item.Identity).ToHashSet();
        if (container.MemberPath is not null || directory.Revision.IndexContentId != container.IndexContentId ||
            members.Count != directory.Objects.Count || directory.Objects.Any(item =>
                item.Identity.IndexContentId != container.IndexContentId || item.Identity.Ordinal != container.Ordinal ||
                item.Identity.MemberPath is null))
            throw new ArgumentException("Native directory identities are inconsistent.", nameof(directory));
        byNativeId = directory.Objects.GroupBy(item => (item.NativeType, item.NativeId))
            .ToDictionary(group => group.Key, group => group.Select(item => item.Identity).ToArray());
    }

    public ReferenceResolution Resolve(ResourceReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Namespace != IdentifierNamespace.Native || reference.ResourceType is not { } type ||
            !uint.TryParse(reference.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return new(ReferenceStatus.Unresolved, [], "A typed native reference is required.");
        if (reference.Owner is not { } owner || owner.IndexContentId != container.IndexContentId ||
            owner.Ordinal != container.Ordinal || owner != container && !members.Contains(owner))
            return new(ReferenceStatus.Unresolved, [], "The reference belongs to another container.");
        var candidates = byNativeId.GetValueOrDefault((type, id)) ?? [];
        return new(candidates.Length switch
        {
            0 => ReferenceStatus.Missing,
            1 => ReferenceStatus.Unique,
            _ => ReferenceStatus.Ambiguous
        }, candidates);
    }
}
