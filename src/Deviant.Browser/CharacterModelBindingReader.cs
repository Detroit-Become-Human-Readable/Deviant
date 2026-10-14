using System.Collections.ObjectModel;
using System.Globalization;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Models;
using Deviant.Formats.Scenes;

namespace Deviant.Browser;

public sealed record CharacterNameObservation(int TableOrdinal, uint Key, string Text, DecodedExtent Extent);

public sealed record CharacterRigSummary(uint Version, int MatrixCount, int NestedRecordCount,
    IReadOnlyList<CharacterNameObservation> NameEvidence, bool NameEvidenceTruncated,
    DecodedExtent Extent)
{
    public bool NodeBindingValidated { get; init; }
}

public sealed record CharacterMeshLodBinding(int LodOrdinal, float Threshold,
    ResourceReference MeshDataReference, ReferenceResolution MeshDataResolution,
    ResourceIdentity? MeshDataIdentity, uint? GroupIndex, int? GroupCount, string? StoredMeshName,
    NativeModelSelection? Selection, DecodedExtent LodExtent,
    DecodedExtent? MeshDataMetadataExtent, DecodedExtent? SelectedGroupBoundsExtent,
    string? Diagnostic)
{
    public NativeModelGroupStatistics? GroupStatistics { get; init; }
    public bool? HasSkinPalette { get; init; }
    public bool? HasSkinAttributes { get; init; }
}

public sealed record CharacterModelBindingResult(ResourceIdentity SelectedIdentity,
    ResourceIdentity CatalogIdentity, ResourceIdentity ContainerIdentity, SourceRevision Revision,
    uint OwnerNativeType, uint OwnerNativeId, uint? NativeFlags, uint? GroupIndex,
    ResourceReference? EntityNode, ReferenceResolution? EntityNodeResolution,
    ReferenceResolution CatalogContainerResolution, IReadOnlyList<NameEvidence> NameEvidence,
    IReadOnlyList<SourceExtent> SourceExtents, IReadOnlyList<CharacterMeshLodBinding> Lods,
    CharacterRigSummary? Rig, string? Diagnostic)
{
    public IReadOnlyList<AssetRelationship> CatalogCompanionLinks { get; init; } = [];
    public IReadOnlyList<CharacterModelDirectReference> DirectReferences { get; init; } = [];
    public IReadOnlyList<CharacterModelAnimationLink> AnimationLinks { get; init; } = [];
}

public sealed record CharacterModelBindingOptions(bool ResolveEntityNode = true,
    bool IncludeSkeletonNameEvidence = false, int MaximumSkeletonNameEvidence = 256,
    bool IncludeGroupStatistics = true, bool IncludeAnimationLinks = true);

/// <summary>Projects one exact native model member through its verified archive catalog to typed mesh groups.</summary>
public sealed class CharacterModelBindingReader
{
    private const int MaximumDescriptorNames = 256;
    private const int MaximumDescriptorNameCharacters = 4096;
    private const int MaximumSkeletonEvidenceCharacters = 1024 * 1024;

    public CharacterModelBindingResult Read(NativeContainerLease lease, AssetDescriptor selectedDescriptor,
        ResourceIdentity catalogIdentity, LocalizationReferenceReadResult catalogReferences,
        CharacterModelBindingOptions? options = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(selectedDescriptor);
        ArgumentNullException.ThrowIfNull(catalogIdentity);
        ArgumentNullException.ThrowIfNull(catalogReferences);
        options ??= new();
        ValidateOptions(options);
        token.ThrowIfCancellationRequested();
        ValidateDescriptor(lease, selectedDescriptor);
        ValidateCatalogIdentity(lease, catalogIdentity);
        lease.EnsureUnchanged();

        var ownerFrames = lease.Directory.Objects.Where(frame => frame.Identity == selectedDescriptor.Identity &&
            frame.NativeType is 2009 or 2013).ToArray();
        if (ownerFrames.Length != 1)
            throw new ArchiveException("CharacterModelMemberUnavailable", "The selected native model member is missing or ambiguous in its owner container.");
        var owner = ownerFrames[0];
        ValidateMemberPath(lease, owner);
        using var nameReservation = lease.MemoryBudget.Reserve(MaximumDescriptorNames * 64L,
            "native character descriptor evidence");
        var names = SnapshotNames(selectedDescriptor.Names, token);
        var physicalExtents = lease.SourceExtents.ToList();
        var containerResolution = ResolveCatalogContainer(lease, catalogIdentity, catalogReferences, physicalExtents, token);
        if (containerResolution.Diagnostic is { } bindingDiagnostic)
        {
            lease.EnsureUnchanged();
            return new(selectedDescriptor.Identity, catalogIdentity, lease.Identity, lease.Revision,
                owner.NativeType, owner.NativeId, null, null, null, null, containerResolution.Resolution,
                names, physicalExtents.Distinct().ToArray(), [], null, bindingDiagnostic)
                { CatalogCompanionLinks = containerResolution.CompanionLinks };
        }

        using var ownerScratch = lease.MemoryBudget.Reserve(EstimateOwnerScratch(owner), "native character model metadata");
        token.ThrowIfCancellationRequested();
        var entityReader = new SceneEntityReader();
        if (!entityReader.TryRead(owner, out var entity, out var entityDiagnostic, token) || entity is null)
        {
            lease.EnsureUnchanged();
            return new(selectedDescriptor.Identity, catalogIdentity, lease.Identity, lease.Revision,
                owner.NativeType, owner.NativeId, null, null, null, null, containerResolution.Resolution,
                names, physicalExtents.Distinct().ToArray(), [], null, entityDiagnostic ?? "CharacterEntityUnavailable")
                { CatalogCompanionLinks = containerResolution.CompanionLinks };
        }

        IReadOnlyList<NativeMeshLod> lods;
        uint groupIndex;
        uint nativeFlags;
        CharacterRigSummary? rig = null;
        NativeMeshPrefixDocument? fullMeshPrefix = null;
        DecodedExtent? meshFixedFields = null;
        if (owner.NativeType == 2009)
        {
            if (owner.RawPayload.Length > NativeMeshPrefixReader.MaximumPayloadBytes)
            {
                lease.EnsureUnchanged();
                return new(selectedDescriptor.Identity, catalogIdentity, lease.Identity, lease.Revision,
                    owner.NativeType, owner.NativeId, null, null, entity.Node,
                    ResolveEntityNode(lease, entity.Node, options, token), containerResolution.Resolution,
                    names, physicalExtents.Distinct().ToArray(), [], null, "CharacterMeshEntityPayloadLimit");
            }
            if (!new NativeMeshEntityReader().TryRead(owner, entity, out var mesh, out var meshDiagnostic, token) || mesh is null)
            {
                lease.EnsureUnchanged();
                return new(selectedDescriptor.Identity, catalogIdentity, lease.Identity, lease.Revision,
                    owner.NativeType, owner.NativeId, null, null, entity.Node,
                    ResolveEntityNode(lease, entity.Node, options, token), containerResolution.Resolution,
                    names, physicalExtents.Distinct().ToArray(), [], null, meshDiagnostic ?? "CharacterMeshUnavailable")
                    { CatalogCompanionLinks = containerResolution.CompanionLinks };
            }
            lods = mesh.Lods;
            groupIndex = mesh.GroupIndex;
            nativeFlags = mesh.NativeFlags;
            meshFixedFields = mesh.FieldExtents["FixedFields"];
            new NativeMeshPrefixReader().TryRead(owner, entity, out fullMeshPrefix, out _, token);
        }
        else
        {
            if (!new NativeSkeletonReader().TryRead(owner, entity, out var skeleton, out var skeletonDiagnostic, token) || skeleton is null)
            {
                lease.EnsureUnchanged();
                return new(selectedDescriptor.Identity, catalogIdentity, lease.Identity, lease.Revision,
                    owner.NativeType, owner.NativeId, null, null, entity.Node,
                    ResolveEntityNode(lease, entity.Node, options, token), containerResolution.Resolution,
                    names, physicalExtents.Distinct().ToArray(), [], null, skeletonDiagnostic ?? "CharacterSkeletonUnavailable")
                    { CatalogCompanionLinks = containerResolution.CompanionLinks };
            }
            lods = skeleton.MeshPrefix.Lods;
            groupIndex = skeleton.MeshPrefix.GroupIndex;
            nativeFlags = skeleton.MeshPrefix.NativeFlags;
            fullMeshPrefix = skeleton.MeshPrefix;
            rig = SummarizeSkeleton(skeleton, options, token) with
            { NodeBindingValidated = ValidateRigNodeBinding(lease, skeleton, options, token) };
        }

        IReadOnlyList<CharacterModelDirectReference> directReferences = [];
        IReadOnlyList<CharacterModelAnimationLink> animationLinks = [];
        if (options.IncludeAnimationLinks)
        {
            using var referenceScratch = lease.MemoryBudget.Reserve(
                CharacterModelAnimationLinks.MaximumDirectReferences * 768L, "model direct reference projection");
            directReferences = CharacterModelAnimationLinks.ReadReferences(owner, entity, fullMeshPrefix, meshFixedFields, token);
            animationLinks = CharacterModelAnimationLinks.Resolve(lease.Directory, owner.Identity, directReferences, token);
        }
        var nodeResolution = ResolveEntityNode(lease, entity.Node, options, token);
        var nativeResolver = new NativeReferenceResolver(lease.Directory);
        var parsedMeshes = new Dictionary<ResourceIdentity, ParsedMeshSummary>();
        var lodBindings = new List<CharacterMeshLodBinding>(lods.Count);
        for (var index = 0; index < lods.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var lod = lods[index];
            var resolution = nativeResolver.Resolve(lod.MeshData);
            ResourceIdentity? meshIdentity = null;
            int? groupCount = null;
            DecodedExtent? metadataExtent = null;
            DecodedExtent? groupBoundsExtent = null;
            string? storedMeshName = null;
            NativeModelGroupStatistics? groupStatistics = null;
            bool? hasSkinPalette = null;
            NativeModelSelection? selection = null;
            string? diagnostic = null;

            if (resolution is { Status: ReferenceStatus.Unique, Candidates.Count: 1 })
            {
                meshIdentity = resolution.Candidates[0];
                var meshFrames = lease.Directory.Objects.Where(frame => frame.Identity == meshIdentity && frame.NativeType == 2130).ToArray();
                if (meshFrames.Length != 1)
                    diagnostic = "CharacterMeshDataUnavailable";
                else
                {
                    if (!parsedMeshes.TryGetValue(meshIdentity, out var summary))
                    {
                        var dataFrame = meshFrames[0];
                        ValidateMemberPath(lease, dataFrame, expectedNativeType: 2130);
                        // The parser caps source metadata at 64 MiB; include that allowance plus the raw input
                        // for bounded group/part DTOs and the BLSHAPES group table it emits.
                        using var meshScratch = lease.MemoryBudget.Reserve(
                            checked((long)NativeMeshDataReader.MaximumMetadataBytes + dataFrame.RawPayload.Length),
                            "native character mesh metadata");
                        if (!new NativeMeshDataReader().TryRead(dataFrame, out var data, out var dataDiagnostic, token) || data is null)
                            summary = new(null, 0, null, dataDiagnostic ?? "CharacterMeshDataParseFailed");
                        else
                        {
                            var selectedGroupAvailable = groupIndex < data.Groups.Count;
                            summary = new(data.MetadataExtent, data.Groups.Count, data.Name,
                                selectedGroupAvailable ? null : "CharacterMeshGroupUnavailable",
                                selectedGroupAvailable ? data.Groups[(int)groupIndex].Bounds.Extent : null,
                                selectedGroupAvailable && options.IncludeGroupStatistics ? CreateGroupStatistics(data, data.Groups[(int)groupIndex], token) : null,
                                selectedGroupAvailable ? data.Groups[(int)groupIndex].Parts.Any(part => part.Palette.Count > 0) : null,
                                selectedGroupAvailable ? HasSupportedSkinAttributes(data, (int)groupIndex) : null);
                        }
                        parsedMeshes.Add(meshIdentity, summary);
                    }
                    groupCount = summary.GroupCount;
                    metadataExtent = summary.MetadataExtent;
                    groupBoundsExtent = summary.GroupBoundsExtent;
                    storedMeshName = summary.StoredName;
                    groupStatistics = summary.GroupStatistics;
                    hasSkinPalette = summary.HasSkinPalette;
                    diagnostic = summary.Diagnostic;
                    if (summary.MetadataExtent is not null && summary.Diagnostic is null)
                        selection = new(meshIdentity, checked((int)groupIndex));
                }
            }
            else diagnostic = resolution.Status == ReferenceStatus.Ambiguous
                ? "CharacterMeshDataAmbiguous" : "CharacterMeshDataMissing";

            lodBindings.Add(new(index, lod.Threshold, lod.MeshData, resolution, meshIdentity,
                groupIndex, groupCount, storedMeshName, selection, lod.Extent, metadataExtent, groupBoundsExtent, diagnostic)
                { GroupStatistics = groupStatistics, HasSkinPalette = hasSkinPalette,
                    HasSkinAttributes = meshIdentity is not null && parsedMeshes.TryGetValue(meshIdentity, out var parsed) ? parsed.HasSkinAttributes : null });
        }

        token.ThrowIfCancellationRequested();
        lease.EnsureUnchanged();
        return new(selectedDescriptor.Identity, catalogIdentity, lease.Identity, lease.Revision,
            owner.NativeType, owner.NativeId, nativeFlags, groupIndex, entity.Node, nodeResolution,
            containerResolution.Resolution, names, physicalExtents.Distinct().ToArray(), lodBindings, rig, null)
            { CatalogCompanionLinks = containerResolution.CompanionLinks,
                DirectReferences = directReferences, AnimationLinks = animationLinks };
    }

    private static void ValidateDescriptor(NativeContainerLease lease, AssetDescriptor descriptor)
    {
        if (descriptor.Revision != lease.Revision || descriptor.Identity.IndexContentId != lease.Identity.IndexContentId ||
            descriptor.Identity.Ordinal != lease.Identity.Ordinal || descriptor.Identity.MemberPath is null)
            throw new ArchiveException("CharacterModelSourceMismatch", "The selected descriptor does not belong to the leased container revision.");
    }

    private static void ValidateCatalogIdentity(NativeContainerLease lease, ResourceIdentity catalogIdentity)
    {
        if (catalogIdentity.IndexContentId != lease.Snapshot.IndexContentId || catalogIdentity.MemberPath is not null)
            throw new ArchiveException("CharacterCatalogSourceMismatch", "The catalog identity belongs to another archive source.");
        var rows = lease.Snapshot.Records.Where(row => row.Ordinal == catalogIdentity.Ordinal).ToArray();
        if (rows.Length != 1 || rows[0].Type != 3 || rows[0].Validity != ResourceValidity.Valid)
            throw new ArchiveException("CharacterCatalogUnavailable", "The selected catalog is not one valid type-3 archive resource.");
    }

    private static void ValidateMemberPath(NativeContainerLease lease, NativeObjectFrame frame,
        uint? expectedNativeType = null)
    {
        var expected = string.Create(CultureInfo.InvariantCulture,
            $"dc-object/{frame.DirectoryIndex}/{frame.NativeType}/{frame.NativeId}");
        if (frame.Identity != new ResourceIdentity(lease.Identity.IndexContentId, lease.Identity.Ordinal, expected) ||
            frame.PayloadExtent.Container != lease.Identity ||
            (expectedNativeType is { } type && frame.NativeType != type) ||
            (expectedNativeType is null && frame.NativeType is not (2009 or 2013)))
            throw new ArchiveException("CharacterModelSourceMismatch", "The selected member identity does not match the leased native directory.");
    }

    private static CatalogContainerBinding ResolveCatalogContainer(NativeContainerLease lease,
        ResourceIdentity catalogIdentity, LocalizationReferenceReadResult references,
        List<SourceExtent> sourceExtents, CancellationToken token)
    {
        var expectedRevision = lease.Revision;
        if (references.Source != catalogIdentity || references.Revision != expectedRevision ||
            references.Status != LocalizationReferenceReadStatus.Verified || references.ReferenceTableSha256 is null ||
            references.Relationships is null || references.Relationships.Count > LocalizationReferenceReader.MaximumReferenceCount)
            return new(new(ReferenceStatus.Unresolved, [], "The type-3 reference result is missing or stale."), "CharacterCatalogReferenceUnverified");

        var companionRows = new List<AssetRelationship>();
        var catalogRecord = lease.Snapshot.Records.Single(row => row.Ordinal == catalogIdentity.Ordinal);
        var volume = lease.Snapshot.Volumes.SingleOrDefault(item => item.Package == catalogRecord.Package);
        if (volume is null || references.RelativeSource != volume.RelativeName || !IsSha256(references.ReferenceTableSha256))
            return new(new(ReferenceStatus.Unresolved, [], "The type-3 reference table has no current source proof."),
                "CharacterCatalogReferenceUnverified");

        using var lookupMemory = lease.MemoryBudget.Reserve(
            checked(lease.Snapshot.Records.Count * 128L + references.Relationships.Count * 128L),
            "character catalog candidate validation");
        var requested = new HashSet<(uint Type, uint Id)>();
        foreach (var relationship in references.Relationships)
        {
            token.ThrowIfCancellationRequested();
            if (relationship?.Reference is { ResourceType: { } type } reference &&
                uint.TryParse(reference.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                requested.Add((type, id));
        }
        var byKey = new Dictionary<(uint Type, uint Id), List<ResourceIdentity>>();
        foreach (var row in lease.Snapshot.Records)
        {
            token.ThrowIfCancellationRequested();
            var key = (row.Type, row.Id);
            if (!requested.Contains(key)) continue;
            if (!byKey.TryGetValue(key, out var matches)) byKey.Add(key, matches = []);
            matches.Add(new ResourceIdentity(lease.Snapshot.IndexContentId, row.Ordinal));
        }

        for (var index = 0; index < references.Relationships.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var relationship = references.Relationships[index];
            if (relationship is null || relationship.Evidence is null || relationship.Reference is null ||
                relationship.Resolution is null || relationship.Resolution.Candidates is null ||
                relationship.Source != catalogIdentity || !relationship.Evidence.Verified ||
                relationship.Reference.Namespace != IdentifierNamespace.Archive || relationship.Reference.Owner != catalogIdentity ||
                relationship.Reference.ResourceType is not { } type ||
                !uint.TryParse(relationship.Reference.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                relationship.Evidence.PhysicalExtent is not { } extent || extent.RelativeSource != volume.RelativeName ||
                extent.Offset != checked(catalogRecord.Offset + 20L + index * 9L) || extent.Length != 9 ||
                !IsSha256(extent.PayloadSha256))
                return new(new(ReferenceStatus.Unresolved, [], "A catalog relationship is not bound to the selected type-3 resource."),
                    "CharacterCatalogRelationshipMismatch");

            var expectedCandidates = byKey.TryGetValue((type, id), out var matches)
                ? (IReadOnlyList<ResourceIdentity>)matches : Array.Empty<ResourceIdentity>();
            var expectedStatus = expectedCandidates.Count switch
            {
                0 => ReferenceStatus.Missing,
                1 => ReferenceStatus.Unique,
                _ => ReferenceStatus.Ambiguous
            };
            if (relationship.Resolution.Status != expectedStatus ||
                !relationship.Resolution.Candidates.SequenceEqual(expectedCandidates))
                return new(new(ReferenceStatus.Unresolved, relationship.Resolution.Candidates,
                    "The catalog relationship candidates do not match the current typed archive rows."),
                    "CharacterCatalogRelationshipMismatch");

            sourceExtents.Add(extent);
            if (type == 29) companionRows.Add(relationship);
        }

        var links = companionRows.AsReadOnly();
        var matching = companionRows.Where(link => link.Resolution is
            { Status: ReferenceStatus.Unique, Candidates.Count: 1 } && link.Resolution.Candidates[0] == lease.Identity).ToArray();
        if (matching.Length != 0)
        {
            var selected = lease.Snapshot.Records.SingleOrDefault(row => row.Ordinal == lease.Identity.Ordinal);
            if (selected is not { Type: 29, Validity: ResourceValidity.Valid })
                return new(new(ReferenceStatus.Missing, [], "The selected native container is unavailable."),
                    "CharacterCatalogContainerMismatch") { CompanionLinks = links };
            return new(new(ReferenceStatus.Unique, [lease.Identity]), null) { CompanionLinks = links };
        }

        if (companionRows.Count != 1)
        {
            var ambiguous = companionRows.Where(link => link.Resolution.Status == ReferenceStatus.Ambiguous &&
                link.Resolution.Candidates.Contains(lease.Identity)).ToArray();
            var candidates = ambiguous.SelectMany(link => link.Resolution.Candidates).Distinct().ToArray();
            var status = ambiguous.Length == 0 ? ReferenceStatus.Missing : ReferenceStatus.Ambiguous;
            return new(new(status, candidates, "The selected native container has no unique catalog link."),
                status == ReferenceStatus.Missing ? "CharacterCatalogContainerMissing" : "CharacterCatalogContainerAmbiguous")
                { CompanionLinks = links };
        }

        var companion = companionRows[0].Resolution;
        if (companion.Status != ReferenceStatus.Unique || companion.Candidates.Count != 1)
            return new(companion, companion.Status == ReferenceStatus.Ambiguous
                ? "CharacterCatalogContainerAmbiguous" : "CharacterCatalogContainerMissing") { CompanionLinks = links };
        var target = companion.Candidates[0];
        var rows = lease.Snapshot.Records.Where(row => row.Ordinal == target.Ordinal).ToArray();
        if (target != lease.Identity || target.IndexContentId != lease.Snapshot.IndexContentId ||
            target.MemberPath is not null || rows.Length != 1 || rows[0].Type != 29 || rows[0].Validity != ResourceValidity.Valid)
            return new(companion, "CharacterCatalogContainerMismatch") { CompanionLinks = links };
        return new(companion, null) { CompanionLinks = links };
    }

    private static ReferenceResolution? ResolveEntityNode(NativeContainerLease lease,
        ResourceReference? entityNode, CharacterModelBindingOptions options, CancellationToken token)
    {
        if (!options.ResolveEntityNode) return null;
        token.ThrowIfCancellationRequested();
        return entityNode is null
            ? new(ReferenceStatus.Unresolved, [], "The selected member has no entity-node reference.")
            : new NativeReferenceResolver(lease.Directory).Resolve(entityNode);
    }

    private static CharacterRigSummary SummarizeSkeleton(NativeSkeletonDocument skeleton,
        CharacterModelBindingOptions options, CancellationToken token)
    {
        if (!options.IncludeSkeletonNameEvidence)
            return new(skeleton.Version, skeleton.Matrices.Count, skeleton.NestedRecords.Count, [], false, skeleton.Extent);

        var evidence = new List<CharacterNameObservation>(Math.Min(options.MaximumSkeletonNameEvidence, 64));
        var characters = 0;
        var truncated = false;
        foreach (var table in skeleton.NameTables)
            foreach (var entry in table.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (evidence.Count >= options.MaximumSkeletonNameEvidence) { truncated = true; break; }
                if (entry.ByteCount > MaximumDescriptorNameCharacters * 4) { truncated = true; continue; }
                if (!entry.TryGetUtf8Text(out var text, token) || text is null) continue;
                if (text.Length > MaximumDescriptorNameCharacters) { truncated = true; continue; }
                characters = checked(characters + text.Length);
                if (characters > MaximumSkeletonEvidenceCharacters) { truncated = true; break; }
                evidence.Add(new(table.Ordinal, entry.Key, text, entry.NameExtent));
            }
        return new(skeleton.Version, skeleton.Matrices.Count, skeleton.NestedRecords.Count,
            evidence.AsReadOnly(), truncated, skeleton.Extent);
    }

    private static bool ValidateRigNodeBinding(NativeContainerLease lease, NativeSkeletonDocument skeleton,
        CharacterModelBindingOptions options, CancellationToken token)
    {
        if (!options.IncludeSkeletonNameEvidence || !options.ResolveEntityNode || skeleton.EntityNode is null)
            return false;
        var resolution = new NativeReferenceResolver(lease.Directory).Resolve(skeleton.EntityNode);
        if (resolution.Status != ReferenceStatus.Unique || resolution.Candidates.Count != 1) return false;
        var frames = lease.Directory.Objects.Where(frame => frame.Identity == resolution.Candidates[0] &&
            frame.NativeType == NativeNodeArrayReader.NativeType).Take(2).ToArray();
        if (frames.Length != 1) return false;
        var frame = frames[0];
        if (frame.RawPayload.Length > new NativeNodeArrayLimits().MaximumPayloadBytes) return false;
        ValidateMemberPath(lease, frame, NativeNodeArrayReader.NativeType);
        using var scratch = lease.MemoryBudget.Reserve(checked(frame.RawPayload.Length * 2L +
            2L * 64 * 1024 * 1024), "character rig classification metadata");
        var prefix = new NativeNodeArrayReader().Read(frame, lease.Revision, lease.SourceExtents,
            lease.Segments?.Steps, token: token);
        if (prefix.Value is null) return false;
        var body = new NativeNodeArrayBodyReader().Read(prefix, token: token);
        return body.Value is not null && new NativeSkeletonNodeArrayBindingReader().TryRead(skeleton,
            lease.Revision, body.Value, out var joined, out _, token) && joined is not null;
    }

    private static IReadOnlyList<NameEvidence> SnapshotNames(IReadOnlyList<NameEvidence> source, CancellationToken token)
    {
        if (source is null || source.Count > MaximumDescriptorNames)
            throw new ArchiveException("CharacterModelNameEvidenceLimit", "The selected descriptor name evidence exceeds its size limit.");
        var result = new NameEvidence[source.Count];
        long characters = 0;
        for (var i = 0; i < result.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var evidence = source[i] ?? throw new ArchiveException("CharacterModelNameEvidenceInvalid", "The selected descriptor name evidence is invalid.");
            if (string.IsNullOrWhiteSpace(evidence.Text) || evidence.Text.Length > MaximumDescriptorNameCharacters)
                throw new ArchiveException("CharacterModelNameEvidenceInvalid", "The selected descriptor name evidence is invalid.");
            characters += evidence.Text.Length;
            if (characters > MaximumSkeletonEvidenceCharacters)
                throw new ArchiveException("CharacterModelNameEvidenceLimit", "The selected descriptor name evidence exceeds its size limit.");
            result[i] = evidence;
        }
        return Array.AsReadOnly(result);
    }

    private static long EstimateOwnerScratch(NativeObjectFrame owner) => owner.NativeType switch
    {
        2009 => checked((long)NativeMeshPrefixReader.MaximumPayloadBytes + NativeMeshPrefixReader.MaximumRetainedNameBytes),
        2013 => checked((long)new NativeSkeletonLimits().MaximumMetadataBytes +
            new NativeSkeletonLimits().MaximumNameBytes + owner.RawPayload.Length * 2L),
        _ => throw new ArchiveException("CharacterModelTypeUnsupported", "The selected member type is unsupported.")
    };

    private static NativeModelGroupStatistics CreateGroupStatistics(NativeMeshDataDocument data,
        NativeMeshGroup group, CancellationToken token)
    {
        long vertexWindows = 0;
        long indexWindows = 0;
        var selectors = new SortedDictionary<uint, long>();
        foreach (var part in group.Parts)
        {
            token.ThrowIfCancellationRequested();
            vertexWindows = checked(vertexWindows + part.VertexCount);
            indexWindows = checked(indexWindows + part.IndexCount);
            selectors[part.TopologySelector] = checked(selectors.GetValueOrDefault(part.TopologySelector) + 1);
        }

        var boundsObb = Array.AsReadOnly(group.Bounds.Obb.ToArray());
        var boundsExtra = Array.AsReadOnly(new[]
        { group.Bounds.Extra.X, group.Bounds.Extra.Y, group.Bounds.Extra.Z, group.Bounds.Extra.W });
        return new(vertexWindows, indexWindows, group.Parts.Count,
            Array.AsReadOnly(data.VertexBuffers.Select(buffer => buffer.ElementCount).ToArray()),
            Array.AsReadOnly(data.IndexCounts.ToArray()),
            new ReadOnlyDictionary<uint, long>(selectors), boundsObb, boundsExtra, group.Bounds.Extent);
    }

    private static void ValidateOptions(CharacterModelBindingOptions options)
    {
        if (options.MaximumSkeletonNameEvidence is < 0 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumSkeletonNameEvidence));
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record CatalogContainerBinding(ReferenceResolution Resolution, string? Diagnostic)
    {
        public IReadOnlyList<AssetRelationship> CompanionLinks { get; init; } = [];
    }
    private sealed record ParsedMeshSummary(DecodedExtent? MetadataExtent, int GroupCount,
        string? StoredName, string? Diagnostic, DecodedExtent? GroupBoundsExtent = null,
        NativeModelGroupStatistics? GroupStatistics = null, bool? HasSkinPalette = null, bool? HasSkinAttributes = null);

    private static bool? HasSupportedSkinAttributes(NativeMeshDataDocument document, int group)
    {
        var skinned = document.Groups[group].Parts.Where(part => part.TopologySelector != 0).ToArray();
        if (skinned.Length == 0) return false;
        var supported = skinned.All(part =>
        {
            if (part.TopologySelector is not (1 or 4 or 8) || part.VertexBufferIndex >= document.VertexBuffers.Count) return false;
            var buffer = document.VertexBuffers[(int)part.VertexBufferIndex];
            var semantics = part.TopologySelector == 8 ? new byte[] { 6, 7, 8, 9 } : new byte[] { 6, 7 };
            return part.VertexCount > 0 && part.BaseVertex <= buffer.ElementCount && part.VertexCount <= buffer.ElementCount - part.BaseVertex &&
                semantics.All(semantic => buffer.Descriptors.Count(d => d.Semantic == semantic && d.Format == 6 &&
                    d.Stream < buffer.Streams.Count && d.Offset + 8 <= buffer.Streams[d.Stream].Stride) == 1);
        });
        return supported ? true : null;
    }
}
