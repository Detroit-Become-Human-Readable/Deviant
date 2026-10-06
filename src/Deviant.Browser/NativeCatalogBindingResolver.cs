using System.Globalization;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Containers;
using Deviant.Formats.Scenes;

namespace Deviant.Browser;

public sealed record NativeCatalogBindingResult(ResourceIdentity NodeIdentity,
    SceneCatalogInstance Instance, AssetRelationship Relationship, ResourceIdentity CatalogIdentity,
    ResourceIdentity? CompanionIdentity, SourceExtent? CatalogExtent, string? Diagnostic);

/// <summary>Shares Goal A's exact native-prefix/archive-table catalog binding with render loading.</summary>
public sealed class NativeCatalogBindingResolver
{
    private readonly LocalizationReferenceReader references = new();

    public async Task<NativeCatalogBindingResult> ResolveAsync(ArchiveSnapshot snapshot,
        NativeObjectDirectoryDocument directory, ResourceIdentity nodeIdentity,
        SceneCatalogInstance instance, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(nodeIdentity);
        ArgumentNullException.ThrowIfNull(instance);
        token.ThrowIfCancellationRequested();
        var reference = instance.Catalog with { Namespace = IdentifierNamespace.Archive };
        var source = new ArchiveResourceResolver(snapshot);
        var resolution = source.Resolve(reference);
        var evidence = new RelationshipEvidence(false, SceneCatalogInstanceReader.Version,
            "Catalog reference awaits matching native prefix and archive table.",
            DecodedExtent: instance.FieldExtents["Catalog"]);
        var relationship = new AssetRelationship(nodeIdentity, AssetRelationshipKind.Container, "Catalog",
            reference, resolution, evidence);
        if (resolution is not { Status: ReferenceStatus.Unique, Candidates.Count: 1 })
            return new(nodeIdentity, instance, relationship, resolution.Candidates.FirstOrDefault() ?? instance.Identity, null, null,
                "CatalogArchiveResolutionUnavailable");
        var native = new NativeReferenceResolver(directory).Resolve(instance.Catalog);
        if (native is not { Status: ReferenceStatus.Unique, Candidates.Count: 1 })
            return new(nodeIdentity, instance, relationship, resolution.Candidates[0], null, null,
                "CatalogNativeResolutionUnavailable");
        var frame = directory.Objects.SingleOrDefault(item => item.Identity == native.Candidates[0]);
        if (frame is null || !new NativeCommonPrefixReader().TryRead(frame, out var prefix, out _, token) ||
            prefix!.ClassCount != 2 || prefix.NextClassOffset != frame.RawPayload.Length)
            return new(nodeIdentity, instance, relationship, resolution.Candidates[0], null, null,
                "CatalogNativePrefixUnsupported");
        var target = resolution.Candidates[0];
        var row = snapshot.Records[(int)target.Ordinal];
        if (row.Validity != ResourceValidity.Valid)
            return new(nodeIdentity, instance, relationship with
            { Resolution = new(ReferenceStatus.Missing, resolution.Candidates, "The catalog source is unavailable.") },
                target, null, null, "CatalogSourceUnavailable");
        var table = await references.ReadAsync(snapshot, target, token).ConfigureAwait(false);
        if (table.Status != LocalizationReferenceReadStatus.Verified || table.Relationships.Count != prefix.References.Count)
            return new(nodeIdentity, instance, relationship, target, null, null, "CatalogTableShapeMismatch");
        for (var index = 0; index < prefix.References.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var stored = prefix.References[index]; var actual = table.Relationships[index];
            if (stored.Type != actual.Reference.ResourceType ||
                stored.Id.ToString(CultureInfo.InvariantCulture) != actual.Reference.Id ||
                stored.Trailer != actual.Evidence.SourceReferenceTrailer)
                return new(nodeIdentity, instance, relationship, target, null, null, "CatalogTableReferenceMismatch");
        }
        var companionLinks = table.Relationships.Where(item => item.Reference.ResourceType == 29 && item.NavigationTarget is not null).ToArray();
        if (companionLinks.Length != 1)
            return new(nodeIdentity, instance, relationship, target, null, null, "CatalogCompanionAmbiguous");
        var companion = companionLinks[0].NavigationTarget!;
        var extent = new SourceExtent(row.VolumeName, row.Offset, checked(20 + table.Relationships.Count * 9), table.ReferenceTableSha256);
        var verifiedRelationship = relationship with
        {
            Evidence = evidence with
            {
                Verified = true, PhysicalExtent = extent,
                Description = "The stored catalog reference and member table match the archive catalog."
            }
        };
        return new(nodeIdentity, instance, verifiedRelationship, target, companion, extent, null);
    }
}
