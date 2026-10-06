using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Localization.Model;

namespace Deviant.Browser;

public sealed record LocalizationSelectedAudioRelationship(ResourceIdentity SelectedOccurrenceIdentity,
    ResourceIdentity PointerOccurrenceIdentity, string Key, string RawLanguageCode,
    ResourceIdentity? DialogueHeaderIdentity, ResourceIdentity? AudioResourceIdentity,
    ResourceIdentity? DialogueEventIdentity, IReadOnlyList<AssetRelationship> EvidencePath);

public sealed record LocalizationRelationshipDiagnostic(ResourceIdentity SelectedOccurrenceIdentity,
    ResourceIdentity? PointerOccurrenceIdentity, LocalizationReferenceLinkStatus Status,
    string Code, string Message, IReadOnlyList<ResourceIdentity> CandidatePointerOccurrences,
    ResourceIdentity? DialogueHeaderIdentity, ResourceIdentity? AudioResourceIdentity,
    ResourceIdentity? DialogueEventIdentity, IReadOnlyList<AssetRelationship> EvidencePath);

public sealed record LocalizationRelationshipResult(ResourceIdentity SelectedOccurrenceIdentity,
    string? Key, string? RawLanguageCode, IReadOnlyList<ResourceIdentity> PointerOccurrenceIdentities,
    IReadOnlyList<LocalizationSelectedAudioRelationship> Relationships,
    IReadOnlyList<LocalizationRelationshipDiagnostic> Diagnostics);

/// <summary>Resolves selected localization occurrences through verified archive reference rows only.</summary>
public sealed class LocalizationRelationshipCoordinator
{
    public const int MaximumCachedResources = 64;
    public const int MaximumExactKeyOccurrences = 256;
    private readonly LocalizationReferenceReader referenceReader;
    private readonly int cacheCapacity;
    private readonly object cacheGate = new();
    private readonly SemaphoreSlim referenceReadGate = new(1, 1);
    private readonly LinkedList<CacheEntry> leastRecentlyUsed = [];
    private readonly Dictionary<ReferenceCacheKey, LinkedListNode<CacheEntry>> cache = [];

    public LocalizationRelationshipCoordinator(LocalizationReferenceReader? referenceReader = null,
        int cacheCapacity = MaximumCachedResources)
    {
        if (cacheCapacity is < 1 or > MaximumCachedResources)
            throw new ArgumentOutOfRangeException(nameof(cacheCapacity), $"The source-reference cache must hold between 1 and {MaximumCachedResources} resources.");
        this.referenceReader = referenceReader ?? new LocalizationReferenceReader();
        this.cacheCapacity = cacheCapacity;
    }

    public int CachedResourceCount { get { lock (cacheGate) return cache.Count; } }

    public async Task<LocalizationRelationshipResult> ResolveSelectedAsync(LocalizationSession session,
        ResourceIdentity selectedOccurrenceIdentity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedOccurrenceIdentity);
        var results = await ResolveSelectedAsync(session, [selectedOccurrenceIdentity], cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    public async Task<IReadOnlyList<LocalizationRelationshipResult>> ResolveSelectedAsync(LocalizationSession session,
        IEnumerable<ResourceIdentity> selectedOccurrenceIdentities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selectedOccurrenceIdentities);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = session.Snapshot;
        var identities = selectedOccurrenceIdentities.DistinctBy(identity => identity.Key).ToArray();
        if (identities.Length == 0) return Array.Empty<LocalizationRelationshipResult>();

        var requestedForSnapshot = identities.Where(identity => identity.IndexContentId == snapshot.IndexContentId).ToArray();
        var loaded = requestedForSnapshot.Length == 0
            ? Array.Empty<LocalizationRecord>()
            : (await session.GetRecordsAsync(requestedForSnapshot, cancellationToken).ConfigureAwait(false)).ToArray();
        var byIdentity = loaded.ToDictionary(record => record.Identity.Key, StringComparer.Ordinal);
        Task? indexValidation = null;
        Task ValidateIndexAsync(CancellationToken token) =>
            indexValidation ??= referenceReader.ValidateSnapshotIndexHashAsync(snapshot, token);

        var results = new List<LocalizationRelationshipResult>(identities.Length);
        foreach (var identity in identities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (identity.IndexContentId != snapshot.IndexContentId)
            {
                results.Add(EmptyResult(identity, null, null, Diagnostic(identity, null,
                    LocalizationReferenceLinkStatus.Unresolved, "StaleSelection",
                    "The selected occurrence belongs to another archive revision.")));
                continue;
            }
            if (!byIdentity.TryGetValue(identity.Key, out var record))
            {
                var code = session.IsEnrichmentComplete ? "OccurrenceNotIndexed" : "LocalizationPending";
                var status = session.IsEnrichmentComplete ? LocalizationReferenceLinkStatus.Unmatched : LocalizationReferenceLinkStatus.Unresolved;
                results.Add(EmptyResult(identity, null, null, Diagnostic(identity, null, status, code,
                    session.IsEnrichmentComplete ? "The selected localization occurrence is not in the current catalog." :
                    "The selected localization occurrence has not been indexed yet.")));
                continue;
            }
            results.Add(await ResolveRecordAsync(session, record, ValidateIndexAsync, cancellationToken).ConfigureAwait(false));
        }
        return results.AsReadOnly();
    }

    private async Task<LocalizationRelationshipResult> ResolveRecordAsync(LocalizationSession session,
        LocalizationRecord selected, Func<CancellationToken, Task> validateIndex, CancellationToken token)
    {
        if (selected.Kind == LocalizationRecordKind.Text && !session.IsEnrichmentComplete)
            return EmptyResult(selected.Identity, selected.Key, selected.RawLanguageCode,
                Diagnostic(selected.Identity, null, LocalizationReferenceLinkStatus.Unresolved,
                    "LocalizationPending", "The source is still indexing, so same-key pointer occurrences may not be complete."));

        IReadOnlyList<LocalizationRecord> pointers;
        if (selected.Kind == LocalizationRecordKind.Pointer)
            pointers = [selected];
        else if (selected.Kind == LocalizationRecordKind.Text && !string.IsNullOrEmpty(selected.Key))
        {
            var pointerSearch = await GetSameSourceKeyPointersAsync(session, selected, token).ConfigureAwait(false);
            pointers = pointerSearch.Pointers;
            if (pointerSearch.LimitExceeded)
            {
                var diagnostic = Diagnostic(selected.Identity, null, LocalizationReferenceLinkStatus.Ambiguous,
                    "ExactKeyOccurrenceLimit", "The same-source exact-key occurrence set exceeds the bounded relationship limit.",
                    candidatePointers: pointers.Select(pointer => pointer.Identity).ToArray());
                return new(selected.Identity, selected.Key, selected.RawLanguageCode,
                    pointers.Select(pointer => pointer.Identity).ToArray(),
                    Array.Empty<LocalizationSelectedAudioRelationship>(), [diagnostic]);
            }
        }
        else
            pointers = Array.Empty<LocalizationRecord>();

        var pointerIdentities = pointers.Select(pointer => pointer.Identity).ToArray();
        if (pointers.Count == 0)
        {
            var diagnostic = Diagnostic(selected.Identity, null, LocalizationReferenceLinkStatus.Unmatched,
                selected.Kind == LocalizationRecordKind.Text ? "NoSameSourcePointer" : "NoDirectPointerReference",
                selected.Kind == LocalizationRecordKind.Text
                    ? "No pointer occurrence with this exact stored key exists in the same resource."
                    : "This occurrence has no parsed pointer reference.");
            return new(selected.Identity, selected.Key, selected.RawLanguageCode, pointerIdentities,
                Array.Empty<LocalizationSelectedAudioRelationship>(), [diagnostic]);
        }
        if (pointers.Count > 1)
        {
            var ambiguous = Diagnostic(selected.Identity, null, LocalizationReferenceLinkStatus.Ambiguous,
                "MultipleSameKeyPointers", "More than one pointer occurrence with this exact key exists in the same resource.",
                candidatePointers: pointerIdentities);
            return new(selected.Identity, selected.Key, selected.RawLanguageCode, pointerIdentities,
                Array.Empty<LocalizationSelectedAudioRelationship>(), [ambiguous]);
        }

        var pointer = pointers[0];
        if (pointer.Kind != LocalizationRecordKind.Pointer || pointer.PointerKind != 1 ||
            pointer.Status != LocalizationRecordStatus.Unresolved || pointer.PointerPayloadBase64 is null)
        {
            var code = pointer.PointerKind == 0 ? "NullPointerTarget" : "PointerPayloadUnresolved";
            var unresolved = Diagnostic(selected.Identity, pointer.Identity, LocalizationReferenceLinkStatus.Unresolved,
                code, "The selected pointer occurrence does not contain a supported 9-byte reference payload.");
            return new(selected.Identity, selected.Key, selected.RawLanguageCode, pointerIdentities,
                Array.Empty<LocalizationSelectedAudioRelationship>(), [unresolved]);
        }

        LocalizationReferenceReadResult sourceReferences;
        try
        {
            sourceReferences = await GetSourceReferencesAsync(session.Snapshot, pointer.ResourceIdentity,
                validateIndex, token).ConfigureAwait(false);
        }
        catch (ArchiveException exception) when (exception.Code is "ReferenceResourceSizeLimit" or "MalformedReferenceTable" or
            "ReferenceCountLimit" or "UnsupportedReferenceContainerVersion" or "InvalidReferenceSource" or "MissingResource" or "MissingVolume")
        {
            var diagnostic = Diagnostic(selected.Identity, pointer.Identity, LocalizationReferenceLinkStatus.Unresolved,
                exception.Code, exception.Message);
            return new(selected.Identity, selected.Key, selected.RawLanguageCode, pointerIdentities,
                Array.Empty<LocalizationSelectedAudioRelationship>(), [diagnostic]);
        }

        if (sourceReferences.Status != LocalizationReferenceReadStatus.Verified)
        {
            var diagnostic = Diagnostic(selected.Identity, pointer.Identity, LocalizationReferenceLinkStatus.Unmatched,
                "ReferenceContainerMissing", "The localization resource has no supported reference table.");
            return new(selected.Identity, selected.Key, selected.RawLanguageCode, pointerIdentities,
                Array.Empty<LocalizationSelectedAudioRelationship>(), [diagnostic]);
        }

        var pointerLink = LocalizationDiscovery.ResolvePointerReferences([pointer], sourceReferences.Relationships).Single();
        if (pointerLink.Status != LocalizationReferenceLinkStatus.Linked || pointerLink.Relationship is null ||
            pointerLink.TargetResource is null)
        {
            var diagnostic = CreatePointerDiagnostic(selected, pointer, pointerLink, sourceReferences.Relationships);
            return new(selected.Identity, selected.Key, selected.RawLanguageCode, pointerIdentities,
                Array.Empty<LocalizationSelectedAudioRelationship>(), [diagnostic]);
        }

        if (pointerLink.TargetResourceType is 1033 or 1031)
        {
            var targetType = pointerLink.TargetResourceType;
            var directTarget = CreateRelatedAsset(selected, pointer, null, targetType,
                pointerLink.TargetResource, [pointerLink.Relationship]);
            var relation = targetType == 1033
                ? directTarget with { AudioResourceIdentity = pointerLink.TargetResource }
                : directTarget with { DialogueEventIdentity = pointerLink.TargetResource };
            var companionDiagnostic = Diagnostic(selected.Identity, pointer.Identity, LocalizationReferenceLinkStatus.Unmatched,
                targetType == 1033 ? "DialogueEventReferenceMissing" : "AudioReferenceMissing",
                "The pointer has a direct media/event target but no source edge to its paired resource.",
                evidencePath: [pointerLink.Relationship],
                audioResource: targetType == 1033 ? pointerLink.TargetResource : null,
                dialogueEvent: targetType == 1031 ? pointerLink.TargetResource : null);
            return new(selected.Identity, selected.Key, selected.RawLanguageCode, pointerIdentities,
                [relation], [companionDiagnostic]);
        }
        if (pointerLink.TargetResourceType != 4091)
        {
            var unsupported = Diagnostic(selected.Identity, pointer.Identity, LocalizationReferenceLinkStatus.Unresolved,
                "UnsupportedPointerTargetType", "The verified pointer target is not a dialogue header or audio resource.",
                evidencePath: [pointerLink.Relationship]);
            return new(selected.Identity, selected.Key, selected.RawLanguageCode, pointerIdentities,
                Array.Empty<LocalizationSelectedAudioRelationship>(), [unsupported]);
        }

        var dialogueHeader = pointerLink.TargetResource;
        LocalizationReferenceReadResult headerReferences;
        try
        {
            headerReferences = await GetSourceReferencesAsync(session.Snapshot, dialogueHeader,
                validateIndex, token).ConfigureAwait(false);
        }
        catch (ArchiveException exception) when (exception.Code is "ReferenceResourceSizeLimit" or "MalformedReferenceTable" or
            "ReferenceCountLimit" or "UnsupportedReferenceContainerVersion" or "InvalidReferenceSource" or "MissingResource" or "MissingVolume")
        {
            var diagnostic = Diagnostic(selected.Identity, pointer.Identity, LocalizationReferenceLinkStatus.Unresolved,
                "DialogueHeaderUnreadable", "The unique dialogue-header target did not yield a valid reference table.",
                dialogueHeader: dialogueHeader, evidencePath: [pointerLink.Relationship]);
            return new(selected.Identity, selected.Key, selected.RawLanguageCode, pointerIdentities,
                Array.Empty<LocalizationSelectedAudioRelationship>(), [diagnostic]);
        }
        if (headerReferences.Status != LocalizationReferenceReadStatus.Verified)
        {
            var diagnostic = Diagnostic(selected.Identity, pointer.Identity, LocalizationReferenceLinkStatus.Unmatched,
                "DialogueHeaderHasNoReferenceTable", "The referenced dialogue header has no supported COM_CONT reference table.",
                dialogueHeader: dialogueHeader, evidencePath: [pointerLink.Relationship]);
            return new(selected.Identity, selected.Key, selected.RawLanguageCode, pointerIdentities,
                Array.Empty<LocalizationSelectedAudioRelationship>(), [diagnostic]);
        }

        var downstream = LocalizationDiscovery.ResolvePointerDialogueAudioTargets([pointerLink],
            headerReferences.Relationships, [dialogueHeader]);
        var diagnostics = new List<LocalizationRelationshipDiagnostic>();
        var audio = SelectUniqueTarget(session.Snapshot, selected, pointer, dialogueHeader, downstream, 1033, diagnostics);
        var dialogueEvent = SelectUniqueTarget(session.Snapshot, selected, pointer, dialogueHeader, downstream, 1031, diagnostics);
        var evidence = new List<AssetRelationship> { pointerLink.Relationship };
        foreach (var target in downstream.Where(target => target.Status == LocalizationReferenceLinkStatus.Linked &&
                     target.TargetResource is not null && target.TargetResourceType is 1033 or 1031))
        {
            foreach (var edge in target.EvidencePath)
                if (!evidence.Any(existing => SameEdge(existing, edge))) evidence.Add(edge);
        }

        var relations = audio is not null || dialogueEvent is not null
            ? new[] { CreateRelatedAsset(selected, pointer, dialogueHeader, 4091, null, evidence)
                with { AudioResourceIdentity = audio, DialogueEventIdentity = dialogueEvent } }
            : Array.Empty<LocalizationSelectedAudioRelationship>();
        return new(selected.Identity, selected.Key, selected.RawLanguageCode, pointerIdentities,
            relations, diagnostics.AsReadOnly());
    }

    private async Task<PointerSearchResult> GetSameSourceKeyPointersAsync(LocalizationSession session,
        LocalizationRecord selected, CancellationToken token)
    {
        var query = new LocalizationQuery(Source: selected.ResourceIdentity, ExactKey: selected.Key,
            Offset: 0, Limit: MaximumExactKeyOccurrences + 1);
        var page = await session.QueryAsync(query, token).ConfigureAwait(false);
        var pointers = page.Records.Where(record => record.Kind == LocalizationRecordKind.Pointer &&
                record.ResourceIdentity.Key == selected.ResourceIdentity.Key &&
                string.Equals(record.Key, selected.Key, StringComparison.Ordinal)).ToArray();
        return new(pointers, page.Total > MaximumExactKeyOccurrences);
    }

    private async Task<LocalizationReferenceReadResult> GetSourceReferencesAsync(ArchiveSnapshot snapshot,
        ResourceIdentity identity, Func<CancellationToken, Task> validateIndex, CancellationToken token)
    {
        await validateIndex(token).ConfigureAwait(false);
        var key = new ReferenceCacheKey(snapshot.IndexContentId, snapshot.PhysicalSourceStamp, identity.Ordinal);
        LocalizationReferenceReadResult? cached = null;
        lock (cacheGate)
        {
            if (cache.TryGetValue(key, out var node))
            {
                leastRecentlyUsed.Remove(node);
                leastRecentlyUsed.AddFirst(node);
                cached = node.Value.Result;
            }
        }
        if (cached is not null)
        {
            await referenceReader.ValidateReferenceTableHashAsync(snapshot, cached, token).ConfigureAwait(false);
            return cached;
        }

        await referenceReadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (cacheGate)
            {
                if (cache.TryGetValue(key, out var node))
                {
                    leastRecentlyUsed.Remove(node);
                    leastRecentlyUsed.AddFirst(node);
                    cached = node.Value.Result;
                }
            }
            if (cached is not null)
            {
                await referenceReader.ValidateReferenceTableHashAsync(snapshot, cached, token).ConfigureAwait(false);
                return cached;
            }

            var result = await referenceReader.ReadAsync(snapshot, identity, token).ConfigureAwait(false);
            if (result.Status == LocalizationReferenceReadStatus.Verified)
                AddCacheEntry(key, result);
            return result;
        }
        finally { referenceReadGate.Release(); }
    }

    private void AddCacheEntry(ReferenceCacheKey key, LocalizationReferenceReadResult result)
    {
        lock (cacheGate)
        {
            if (cache.TryGetValue(key, out var existing))
            {
                leastRecentlyUsed.Remove(existing);
                cache.Remove(key);
            }
            var node = leastRecentlyUsed.AddFirst(new CacheEntry(key, result));
            cache.Add(key, node);
            while (cache.Count > cacheCapacity)
            {
                var oldest = leastRecentlyUsed.Last!;
                leastRecentlyUsed.RemoveLast();
                cache.Remove(oldest.Value.Key);
            }
        }
    }

    private static ResourceIdentity? SelectUniqueTarget(ArchiveSnapshot snapshot, LocalizationRecord selected, LocalizationRecord pointer,
        ResourceIdentity header, IReadOnlyList<LocalizationPointerRelatedTarget> targets, uint resourceType,
        List<LocalizationRelationshipDiagnostic> diagnostics)
    {
        var candidates = targets.Where(target => target.TargetResourceType == resourceType).ToArray();
        var linked = candidates.Where(target => target.Status == LocalizationReferenceLinkStatus.Linked &&
            target.TargetResource is not null).ToArray();
        if (linked.Length == 1 && candidates.Length == 1 && HasValidResourceExtent(snapshot, linked[0].TargetResource!))
            return linked[0].TargetResource;

        if (linked.Length == 1 && candidates.Length == 1)
        {
            diagnostics.Add(Diagnostic(selected.Identity, pointer.Identity, LocalizationReferenceLinkStatus.Unresolved,
                resourceType == 1033 ? "AudioResourceUnavailable" : "DialogueEventUnavailable",
                "The unique referenced resource has no valid archive extent.", dialogueHeader: header,
                audioResource: resourceType == 1033 ? linked[0].TargetResource : null,
                dialogueEvent: resourceType == 1031 ? linked[0].TargetResource : null,
                evidencePath: linked[0].EvidencePath));
            return null;
        }
        if (candidates.Length == 0)
        {
            var hasUnmatchedHeader = targets.Any(target => target.Status == LocalizationReferenceLinkStatus.Unmatched &&
                target.TargetResourceType is null);
            diagnostics.Add(Diagnostic(selected.Identity, pointer.Identity, LocalizationReferenceLinkStatus.Unmatched,
                hasUnmatchedHeader ? "DialogueHeaderReferenceMissing" :
                    resourceType == 1033 ? "AudioReferenceMissing" : "DialogueEventReferenceMissing",
                hasUnmatchedHeader ? "The dialogue header has no audio or event references." :
                    resourceType == 1033 ? "The dialogue header has no audio reference." :
                    "The dialogue header has no dialogue-event reference.", dialogueHeader: header));
            return null;
        }

        var status = candidates.Length > 1 || candidates.Any(target => target.Status == LocalizationReferenceLinkStatus.Ambiguous)
            ? LocalizationReferenceLinkStatus.Ambiguous
            : candidates.Select(target => target.Status).FirstOrDefault(LocalizationReferenceLinkStatus.Unresolved);
        if (status == LocalizationReferenceLinkStatus.Linked) status = LocalizationReferenceLinkStatus.Unresolved;
        var code = status switch
        {
            LocalizationReferenceLinkStatus.Ambiguous => resourceType == 1033 ? "AudioReferenceAmbiguous" : "DialogueEventReferenceAmbiguous",
            LocalizationReferenceLinkStatus.Unverified => "ReferenceEvidenceUnverified",
            LocalizationReferenceLinkStatus.TargetResourceNotSampled => "ReferenceTargetNotSampled",
            LocalizationReferenceLinkStatus.Unmatched => resourceType == 1033 ? "AudioReferenceMissing" : "DialogueEventReferenceMissing",
            _ => "ReferenceUnresolved"
        };
        foreach (var target in candidates)
            diagnostics.Add(Diagnostic(selected.Identity, pointer.Identity, status, code,
                status == LocalizationReferenceLinkStatus.Ambiguous ? "The dialogue header has multiple candidates for this target." :
                "The dialogue header target could not be resolved uniquely.", dialogueHeader: header,
                audioResource: resourceType == 1033 ? target.TargetResource : null,
                dialogueEvent: resourceType == 1031 ? target.TargetResource : null,
                evidencePath: target.EvidencePath));
        return null;
    }

    private static bool HasValidResourceExtent(ArchiveSnapshot snapshot, ResourceIdentity identity)
    {
        if (identity.IndexContentId != snapshot.IndexContentId || identity.MemberPath is not null) return false;
        var record = snapshot.Records.SingleOrDefault(item => item.Ordinal == identity.Ordinal);
        if (record is null || record.Validity != ResourceValidity.Valid || record.StoredSize <= 0 || record.Offset < 0) return false;
        var volume = snapshot.Volumes.SingleOrDefault(item => item.Package == record.Package);
        return volume is not null && volume.Stamp.Exists && volume.Stamp.Error is null &&
            record.Offset <= volume.Stamp.Length - record.StoredSize;
    }

    private static bool SameEdge(AssetRelationship first, AssetRelationship second) =>
        first.Source.Key == second.Source.Key && first.Reference.ResourceType == second.Reference.ResourceType &&
        first.Reference.Id == second.Reference.Id && first.Evidence.PhysicalExtent?.Offset == second.Evidence.PhysicalExtent?.Offset;

    private static LocalizationSelectedAudioRelationship CreateRelatedAsset(LocalizationRecord selected,
        LocalizationRecord pointer, ResourceIdentity? header, uint targetType, ResourceIdentity? target,
        IReadOnlyList<AssetRelationship> evidence) =>
        new(selected.Identity, pointer.Identity, selected.Key ?? pointer.Key ?? string.Empty,
            selected.RawLanguageCode, header,
            targetType == 1033 ? target : null, targetType == 1031 ? target : null, evidence);

    private static LocalizationRelationshipResult EmptyResult(ResourceIdentity selectedIdentity, string? key,
        string? language, LocalizationRelationshipDiagnostic diagnostic) =>
        new(selectedIdentity, key, language, diagnostic.CandidatePointerOccurrences,
            Array.Empty<LocalizationSelectedAudioRelationship>(), [diagnostic]);

    private static LocalizationRelationshipDiagnostic Diagnostic(ResourceIdentity selectedIdentity,
        ResourceIdentity? pointerIdentity, LocalizationReferenceLinkStatus status, string code, string message,
        IReadOnlyList<ResourceIdentity>? candidatePointers = null, ResourceIdentity? dialogueHeader = null,
        ResourceIdentity? audioResource = null, ResourceIdentity? dialogueEvent = null,
        IReadOnlyList<AssetRelationship>? evidencePath = null) =>
        new(selectedIdentity, pointerIdentity, status, code, message, candidatePointers ?? Array.Empty<ResourceIdentity>(),
            dialogueHeader, audioResource, dialogueEvent, evidencePath ?? Array.Empty<AssetRelationship>());

    private static LocalizationRelationshipDiagnostic CreatePointerDiagnostic(LocalizationRecord selected,
        LocalizationRecord pointer, LocalizationPointerReferenceLink link, IReadOnlyList<AssetRelationship> sourceReferences)
    {
        var payloadType = link.TargetResourceType;
        var payloadId = link.TargetResourceId;
        var referenceCandidates = sourceReferences.Where(reference => reference.Reference.ResourceType == payloadType &&
            reference.Reference.Id == payloadId.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            reference.Evidence.PhysicalExtent?.Length == 9).ToArray();
        var trailerMatches = referenceCandidates.Any(reference =>
            reference.Evidence.SourceReferenceTrailer == link.PayloadPrefix);
        var code = link.Status switch
        {
            LocalizationReferenceLinkStatus.Ambiguous => "PointerReferenceAmbiguous",
            LocalizationReferenceLinkStatus.Unverified => "PointerReferenceUnverified",
            LocalizationReferenceLinkStatus.Unresolved => "PointerReferenceUnresolved",
            LocalizationReferenceLinkStatus.TargetResourceNotSampled => "PointerReferenceTargetNotSampled",
            _ when referenceCandidates.Length > 0 && !trailerMatches => "PointerTrailerMismatch",
            _ when link.Relationship?.Resolution.Status == ReferenceStatus.Missing => "PointerReferenceTargetMissing",
            _ => "PointerReferenceNotFound"
        };
        var path = link.Relationship is null ? Array.Empty<AssetRelationship>() : [link.Relationship];
        return Diagnostic(selected.Identity, pointer.Identity, link.Status, code,
            link.Status switch
            {
                LocalizationReferenceLinkStatus.Ambiguous => "The pointer matches multiple source-reference rows.",
                LocalizationReferenceLinkStatus.Unverified => "The pointer source-reference evidence is not verified.",
                LocalizationReferenceLinkStatus.Unresolved => "The pointer fields could not be resolved.",
                _ when referenceCandidates.Length > 0 && !trailerMatches => "The pointer type/id matches a source row, but its trailer byte differs.",
                _ when link.Relationship?.Resolution.Status == ReferenceStatus.Missing => "The pointer reference is present, but no archive target row exists.",
                _ => "No matching source-reference row exists for the pointer payload."
            }, [pointer.Identity], evidencePath: path);
    }

    private sealed record ReferenceCacheKey(string IndexContentId, string PhysicalSourceStamp, long ResourceOrdinal);
    private sealed record CacheEntry(ReferenceCacheKey Key, LocalizationReferenceReadResult Result);
    private sealed record PointerSearchResult(IReadOnlyList<LocalizationRecord> Pointers, bool LimitExceeded);
}
