using System.Globalization;
using System.Collections.Concurrent;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Localization.Model;

namespace Deviant.Browser;

public sealed record LocalizationAudioLookupProgress(string Phase,long CompletedResources,long TotalResources,
    long PointerOccurrences,long VerifiedLinks,string? Detail=null);

public sealed record LocalizationAudioLookupIssue(string Code,string Message,
    LocalizationReferenceLinkStatus Status,IReadOnlyList<ResourceIdentity> SourceResources,
    ResourceIdentity? PointerOccurrenceIdentity=null,long Count=1);

public sealed record LocalizationAudioLookupBuildResult(bool ReusedCache,long TotalResources,long ResourcesVisited,
    long? PointerRowsExamined,long? VerifiedLinks,long? UnmatchedPointers,long? AmbiguousPointers,
    IReadOnlyList<LocalizationAudioLookupIssue> Issues);

public sealed record LocalizationAudioTextOccurrence(LocalizationRecord TextOccurrence,
    LocalizationRecord PointerOccurrence,LocalizationSelectedAudioRelationship Relationship)
{
    public string TextLanguage=>TextOccurrence.Language;
    public string TextRawLanguageCode=>TextOccurrence.RawLanguageCode;
    public string PointerLanguage=>PointerOccurrence.Language;
    public string PointerRawLanguageCode=>PointerOccurrence.RawLanguageCode;
    public ResourceIdentity LocalizationResourceIdentity=>TextOccurrence.ResourceIdentity;
}

public sealed record LocalizationAudioLookupResult(ResourceIdentity AudioIdentity,
    IReadOnlyList<ResourceIdentity> DialogueHeaderIdentities,
    IReadOnlyList<LocalizationAudioTextOccurrence> Occurrences,
    IReadOnlyList<LocalizationAudioLookupIssue> Diagnostics)
{
    public bool IsComplete { get; init; } = true;
}

/// <summary>Builds and queries a same-archive index from verified dialogue headers to localization pointers.</summary>
public sealed class LocalizationAudioLookup
{
    public const string IndexVersion=LocalizationCacheContract.AudioLookupContractVersion;
    public const int PointerPageSize=2_000;
    public const int RelationshipBatchSize=256;
    private static readonly ConcurrentDictionary<string,SemaphoreSlim> BuildGates=new(StringComparer.Ordinal);

    private readonly LocalizationReferenceReader referenceReader;
    private readonly LocalizationRelationshipCoordinator relationshipCoordinator;

    public LocalizationAudioLookup(LocalizationReferenceReader? referenceReader=null,
        LocalizationRelationshipCoordinator? relationshipCoordinator=null)
    {
        this.referenceReader=referenceReader??new LocalizationReferenceReader();
        this.relationshipCoordinator=relationshipCoordinator??new LocalizationRelationshipCoordinator(this.referenceReader);
    }

    /// <summary>Indexes all currently stored pointer occurrences once, using the existing localization FeatureStore.</summary>
    public async Task<LocalizationAudioLookupBuildResult> BuildAsync(LocalizationSession session,
        IProgress<LocalizationAudioLookupProgress>? progress=null,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        if(!session.IsEnrichmentComplete)await session.EnrichmentTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        var contentId=session.MetadataContentId;
        if(string.IsNullOrEmpty(contentId))throw new ArchiveException("LocalizationPending","Text indexing did not produce a content identity.");
        var gateKey=session.Snapshot.IndexContentId+":"+contentId;
        var buildGate=BuildGates.GetOrAdd(gateKey,_=>new SemaphoreSlim(1,1));
        await buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        if(await session.IsAudioLookupReadyAsync(IndexVersion,contentId,cancellationToken).ConfigureAwait(false))
        {
            var cachedIssues=await session.GetAudioLookupIssuesAsync(IndexVersion,contentId,cancellationToken).ConfigureAwait(false);
            ReportProgress(progress,new("Dialogue links ready",session.Resources.Count,session.Resources.Count,0,0));
            var restoredIssues=cachedIssues.Select(issue=>new LocalizationAudioLookupIssue(issue.Code,
                issue.Message??DescribeCachedIssue(issue),issue.Status,issue.SourceResources,Count:issue.Count)).ToArray();
            return new(true,session.Resources.Count,0,null,null,null,null,restoredIssues);
        }

        var generation=await session.BeginAudioLookupBuildAsync(IndexVersion,contentId,cancellationToken).ConfigureAwait(false);
        var persistedIssues=new Dictionary<string,AudioLookupIssueAccumulator>(StringComparer.Ordinal);
        long pointerRows=0,verifiedLinks=0,unmatchedPointers=0,ambiguousPointers=0,completedResources=0;
        void AddIssue(LocalizationAudioLookupIssue issue)
        {
            if(!persistedIssues.TryGetValue(issue.Code,out var aggregate))
                persistedIssues.Add(issue.Code,aggregate=new(issue.Code,issue.Status,issue.Message));
            aggregate.Add(issue);
        }

        await using var indexLease=await session.AcquirePayloadVerificationAsync(Array.Empty<ResourceIdentity>(),cancellationToken).ConfigureAwait(false);
        foreach(var resource in session.Resources.OrderBy(row=>row.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceIdentity=new ResourceIdentity(session.Snapshot.IndexContentId,resource.Ordinal);
            long offset=0,total=0;
            LocalizationReferenceReadResult? references=null;
            bool referenceReadFailed=false;
            LocalizationPayloadVerificationLease? sourceLease=null;
            string? referenceIssueCode=null,referenceIssueMessage=null;
            var referenceIssueStatus=LocalizationReferenceLinkStatus.Unresolved;
            try
            {
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var referenceFailedBeforePage=referenceReadFailed;
                    var page=await session.QueryAsync(new LocalizationQuery(Source:sourceIdentity,
                        Kind:LocalizationRecordKind.Pointer,Offset:offset,Limit:PointerPageSize),cancellationToken).ConfigureAwait(false);
                    total=page.Total;
                    pointerRows+=page.Records.Count;
                    var kindOne=page.Records.Where(record=>record.Kind==LocalizationRecordKind.Pointer&&
                        record.PointerKind==1&&record.PointerPayloadBase64 is not null).ToArray();
                    if(kindOne.Length>0&&sourceLease is null)
                    {
                        sourceLease=await session.AcquirePayloadVerificationAsync([sourceIdentity],cancellationToken,indexLease).ConfigureAwait(false);
                        sourceLease.EnsureUnchanged();
                    }
                    if(kindOne.Length>0&&!referenceReadFailed&&references is null)
                    {
                        try{references=await referenceReader.ReadAsync(session.Snapshot,sourceIdentity,cancellationToken).ConfigureAwait(false);}
                        catch(ArchiveException error) when(IsReferenceDataIssue(error.Code))
                        {
                            referenceReadFailed=true;
                            referenceIssueCode=error.Code;referenceIssueMessage=error.Message;
                            AddIssue(new(error.Code,error.Message,LocalizationReferenceLinkStatus.Unresolved,[sourceIdentity],Count:kindOne.Length));
                            unmatchedPointers+=kindOne.Length;
                        }
                        if(references?.Status==LocalizationReferenceReadStatus.NotContainer)
                        {
                            referenceReadFailed=true;
                            referenceIssueCode="ReferenceContainerMissing";referenceIssueMessage="This localization source has pointer entries but no supported reference table.";
                            referenceIssueStatus=LocalizationReferenceLinkStatus.Unmatched;
                            AddIssue(new(referenceIssueCode,referenceIssueMessage,referenceIssueStatus,[sourceIdentity],Count:kindOne.Length));
                            unmatchedPointers+=kindOne.Length;
                        }
                    }
                    if(referenceFailedBeforePage&&kindOne.Length>0)
                    {
                        AddIssue(new(referenceIssueCode??"ReferenceUnavailable",referenceIssueMessage??"The localization reference table is unavailable.",
                            referenceIssueStatus,[sourceIdentity],Count:kindOne.Length));
                        unmatchedPointers+=kindOne.Length;
                    }
                    if(kindOne.Length>0&&references?.Status==LocalizationReferenceReadStatus.Verified)
                    {
                        var links=LocalizationDiscovery.ResolvePointerReferences(kindOne,references.Relationships);
                        var candidatesByHeader=links.Where(link=>link.Status==LocalizationReferenceLinkStatus.Linked&&
                                link.TargetResourceType==4091&&link.TargetResource is not null)
                            .GroupBy(link=>link.TargetResource!.Key,StringComparer.Ordinal);
                        foreach(var group in candidatesByHeader)
                        {
                            var header=group.First().TargetResource!;
                            var identities=group.Select(link=>link.Occurrence.Identity).ToArray();
                            foreach(var batch in Batch(identities,RelationshipBatchSize))
                                await session.UpsertAudioLookupCandidatesAsync(header,batch,IndexVersion,contentId,generation,cancellationToken).ConfigureAwait(false);
                            verifiedLinks+=identities.Length;
                        }
                        foreach(var group in links.Where(link=>link.Status!=LocalizationReferenceLinkStatus.Linked)
                                    .GroupBy(link=>link.Status))
                        {
                            var count=group.LongCount();
                            if(group.Key==LocalizationReferenceLinkStatus.Ambiguous)ambiguousPointers+=count;
                            else unmatchedPointers+=count;
                            AddIssue(new("PointerReference"+group.Key,DescribePointerStatus(group.Key),group.Key,
                                [sourceIdentity],Count:count));
                        }
                        var unsupportedTargets=links.Where(link=>link.Status==LocalizationReferenceLinkStatus.Linked&&
                            link.TargetResource is not null&&link.TargetResourceType!=4091).ToArray();
                        if(unsupportedTargets.Length>0)
                        {
                            unmatchedPointers+=unsupportedTargets.Length;
                            AddIssue(new("UnsupportedPointerTargetType",
                                "A verified localization pointer targets a resource type outside the dialogue-header lookup.",
                                LocalizationReferenceLinkStatus.Unresolved,[sourceIdentity],Count:unsupportedTargets.Length));
                        }
                    }
                    offset+=page.Records.Count;
                    if(page.Records.Count==0||offset>=total)break;
                    ReportProgress(progress,new("Indexing dialogue links",completedResources,session.Resources.Count,
                        pointerRows,verifiedLinks,sourceIdentity.Ordinal.ToString(CultureInfo.InvariantCulture)));
                }while(true);

                sourceLease?.EnsureUnchanged();
                completedResources++;
                ReportProgress(progress,new("Indexing dialogue links",completedResources,session.Resources.Count,
                    pointerRows,verifiedLinks,resource.VolumeName));
            }
            finally{if(sourceLease is not null)await sourceLease.DisposeAsync().ConfigureAwait(false);}
        }

        cancellationToken.ThrowIfCancellationRequested();
        indexLease.EnsureUnchanged();
            var persisted=persistedIssues.Values.OrderBy(issue=>issue.Code,StringComparer.Ordinal)
                .Select(issue=>issue.ToCacheIssue()).ToArray();
            await session.CompleteAudioLookupBuildAsync(IndexVersion,contentId,generation,persisted,cancellationToken).ConfigureAwait(false);
            ReportProgress(progress,new("Dialogue links ready",completedResources,session.Resources.Count,pointerRows,verifiedLinks));
            return new(false,session.Resources.Count,completedResources,pointerRows,verifiedLinks,unmatchedPointers,
                ambiguousPointers,persisted.Select(cacheIssue=>new LocalizationAudioLookupIssue(cacheIssue.Code,
                    persistedIssues[cacheIssue.Code].Message,persistedIssues[cacheIssue.Code].Status,
                    cacheIssue.SourceResources,Count:cacheIssue.Count)).ToArray());
        }
        finally{buildGate.Release();}
    }

    /// <summary>Returns all localized occurrences linked through the supplied exact type-4091 header identities.</summary>
    public async Task<LocalizationAudioLookupResult> FindAudioAsync(LocalizationSession session,
        ResourceIdentity exactTopLevelAudioIdentity,IEnumerable<ResourceIdentity> exactHeaderIdentities,
        CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(session);ArgumentNullException.ThrowIfNull(exactTopLevelAudioIdentity);
        ArgumentNullException.ThrowIfNull(exactHeaderIdentities);cancellationToken.ThrowIfCancellationRequested();
        await using var indexLease=await session.AcquirePayloadVerificationAsync(Array.Empty<ResourceIdentity>(),cancellationToken).ConfigureAwait(false);
        indexLease.EnsureUnchanged();
        var snapshot=session.Snapshot;
        var audioRecord=FindTopLevelResource(snapshot,exactTopLevelAudioIdentity,1033,"audio");
        var audioResolution=new ArchiveResourceResolver(snapshot).Resolve(new ResourceReference(IdentifierNamespace.Archive,
            audioRecord.Id.ToString(CultureInfo.InvariantCulture),ResourceType:1033));
        if(audioResolution.Status==ReferenceStatus.Ambiguous)
            throw new ArchiveException("AmbiguousAudioIdentity","The selected audio resource does not resolve to one type-1033 archive row.");
        if(audioResolution.Status!=ReferenceStatus.Unique||audioResolution.Candidates.Count!=1||audioResolution.Candidates[0]!=exactTopLevelAudioIdentity)
            throw new ArchiveException("InvalidAudioIdentity","The selected audio resource did not resolve to its exact type-1033 archive row.");

        var headers=exactHeaderIdentities.DistinctBy(identity=>identity.Key).ToArray();
        foreach(var header in headers)FindTopLevelResource(snapshot,header,4091,"dialogue header");
        var completeAtStart=session.IsEnrichmentComplete;
        var sourceVerification=completeAtStart
            ?await session.AcquireAudioLookupSourceVerificationAsync(indexLease,cancellationToken).ConfigureAwait(false):null;
        await using var coverageLease=sourceVerification?.Lease;
        var contentId=session.MetadataContentId;
        var pointerHeaders=new Dictionary<string,(ResourceIdentity Pointer,List<ResourceIdentity> Headers)>(StringComparer.Ordinal);
        var output=new List<LocalizationAudioTextOccurrence>();
        var diagnostics=new List<LocalizationAudioLookupIssue>();
        if(sourceVerification is { UnverifiedSources.Count:>0 })
            diagnostics.Add(new("LocalizationSourceUnavailable","Some localization sources could not be verified.",
                LocalizationReferenceLinkStatus.Unresolved,sourceVerification.UnverifiedSources));
        if(completeAtStart&&!string.IsNullOrEmpty(contentId)&&
           await session.IsAudioLookupReadyAsync(IndexVersion,contentId,cancellationToken).ConfigureAwait(false))
        {
            foreach(var header in headers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pointers=await session.GetAudioLookupCandidatesAsync(header,IndexVersion,contentId,cancellationToken).ConfigureAwait(false);
                foreach(var pointer in pointers)AddCandidate(pointerHeaders,pointer,header);
            }
            var buildIssues=await session.GetAudioLookupIssuesAsync(IndexVersion,contentId,cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(buildIssues.Select(issue=>new LocalizationAudioLookupIssue(issue.Code,DescribeCachedIssue(issue),
                issue.Status,issue.SourceResources,Count:issue.Count)));
        }
        else if(headers.Length>0)
            await FindAvailableCandidatesAsync(session,headers,pointerHeaders,diagnostics,indexLease,cancellationToken).ConfigureAwait(false);
        if(!completeAtStart)
            diagnostics.Add(new("LocalizationPending","Localization indexing is incomplete.",
                LocalizationReferenceLinkStatus.Unresolved,Array.Empty<ResourceIdentity>()));
        if(headers.Length==0)
            diagnostics.Add(new("DialogueHeaderUnavailable","No exact dialogue header is linked to this audio resource.",
                LocalizationReferenceLinkStatus.Unmatched,Array.Empty<ResourceIdentity>()));
        var variantsByOwnerAndKey=new Dictionary<(string Owner,string Key),IReadOnlyList<LocalizationRecord>>();
        var pointerIdentities=pointerHeaders.Values.Select(item=>item.Pointer)
            .OrderBy(identity=>identity.Ordinal).ThenBy(identity=>identity.MemberPath,StringComparer.Ordinal).ToArray();
        var loadedPointerRecords=(await session.GetRecordsAsync(pointerIdentities,cancellationToken).ConfigureAwait(false))
            .ToDictionary(record=>record.Identity.Key,StringComparer.Ordinal);
        var validPointers=new List<ResourceIdentity>(pointerIdentities.Length);
        foreach(var pointerIdentity in pointerIdentities)
        {
            if(loadedPointerRecords.TryGetValue(pointerIdentity.Key,out var candidateRecord)&&
               candidateRecord.Identity==pointerIdentity&&candidateRecord.Kind==LocalizationRecordKind.Pointer&&
               candidateRecord.PointerKind==1&&candidateRecord.PointerPayloadBase64 is not null&&
               candidateRecord.Status==LocalizationRecordStatus.Unresolved&&
               candidateRecord.ResourceType==1016&&candidateRecord.ResourceIdentity.Ordinal==pointerIdentity.Ordinal&&
               candidateRecord.ResourceIdentity.IndexContentId==snapshot.IndexContentId)
                validPointers.Add(pointerIdentity);
            else
                diagnostics.Add(new("CachedPointerOccurrenceMismatch","A cached reverse-index entry no longer identifies the expected pointer occurrence.",
                    LocalizationReferenceLinkStatus.Unresolved,
                    [new ResourceIdentity(snapshot.IndexContentId,pointerIdentity.Ordinal)],pointerIdentity));
        }
        await using var sourceLease=validPointers.Count==0?null:await session.AcquirePayloadVerificationAsync(
            validPointers.Select(pointer=>new ResourceIdentity(snapshot.IndexContentId,pointer.Ordinal)),cancellationToken,indexLease).ConfigureAwait(false);
        sourceLease?.EnsureUnchanged();
        if(validPointers.Count>0)
        {
            var results=await relationshipCoordinator.ResolveSelectedAsync(session,validPointers,cancellationToken).ConfigureAwait(false);
            foreach(var result in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var acceptedRelationship=false;
                foreach(var diagnostic in result.Diagnostics)
                    diagnostics.Add(new(diagnostic.Code,diagnostic.Message,diagnostic.Status,
                        diagnostic.PointerOccurrenceIdentity is null?Array.Empty<ResourceIdentity>():
                            [new ResourceIdentity(snapshot.IndexContentId,diagnostic.PointerOccurrenceIdentity.Ordinal)],
                        diagnostic.PointerOccurrenceIdentity));
                var pointerIdentity=result.SelectedOccurrenceIdentity;
                if(!pointerHeaders.TryGetValue(pointerIdentity.Key,out var candidate))continue;
                foreach(var relationship in result.Relationships)
                {
                    var header=relationship.DialogueHeaderIdentity;
                    if(relationship.AudioResourceIdentity!=exactTopLevelAudioIdentity||header is null||
                       !candidate.Headers.Any(item=>item.Key==header.Key))continue;
                    acceptedRelationship=true;
                    if(relationship.EvidencePath.Count==0||relationship.EvidencePath.Any(edge=>!edge.Evidence.Verified||edge.NavigationTarget is null))
                    {
                        diagnostics.Add(new("UnverifiedEvidence","A candidate link did not retain a complete verified evidence path.",
                            LocalizationReferenceLinkStatus.Unverified,[new ResourceIdentity(snapshot.IndexContentId,pointerIdentity.Ordinal)],pointerIdentity));
                        continue;
                    }
                    loadedPointerRecords.TryGetValue(pointerIdentity.Key,out var pointerRecord);
                    if(pointerRecord is null||string.IsNullOrEmpty(pointerRecord.Key))
                    {
                        diagnostics.Add(new("PointerOccurrenceUnavailable","The linked pointer occurrence is unavailable in this text index.",
                            LocalizationReferenceLinkStatus.Unresolved,[new ResourceIdentity(snapshot.IndexContentId,pointerIdentity.Ordinal)],pointerIdentity));
                        continue;
                    }
                    var cacheKey=(pointerRecord.ResourceIdentity.Key,pointerRecord.Key);
                    if(!variantsByOwnerAndKey.TryGetValue(cacheKey,out var variants))
                    {
                        variants=await session.GetVariantsAsync(pointerIdentity,cancellationToken).ConfigureAwait(false);
                        variantsByOwnerAndKey[cacheKey]=variants;
                    }
                    var textRecords=variants.Where(record=>record.Kind==LocalizationRecordKind.Text).ToArray();
                    if(textRecords.Length==0)
                        diagnostics.Add(new("TextOccurrenceMissing","The linked key has no parsed text occurrence in this localization resource.",
                            LocalizationReferenceLinkStatus.Unmatched,[pointerRecord.ResourceIdentity],pointerIdentity));
                    foreach(var text in textRecords)
                        output.Add(new(text,pointerRecord,relationship));
                }
                if(!acceptedRelationship&&result.Relationships.Count>0)
                    diagnostics.Add(new("AudioTargetMismatch","The candidate pointer resolves through a different audio resource or dialogue header.",
                        LocalizationReferenceLinkStatus.Unresolved,[new ResourceIdentity(snapshot.IndexContentId,pointerIdentity.Ordinal)],pointerIdentity));
            }
        }

        output.Sort(static (left,right)=>
        {
            var compare=left.TextOccurrence.ResourceOrdinal.CompareTo(right.TextOccurrence.ResourceOrdinal);
            if(compare!=0)return compare;
            compare=left.TextOccurrence.RecordOffset.CompareTo(right.TextOccurrence.RecordOffset);
            if(compare!=0)return compare;
            compare=left.PointerOccurrence.RecordOffset.CompareTo(right.PointerOccurrence.RecordOffset);
            if(compare!=0)return compare;
            return StringComparer.Ordinal.Compare(left.PointerOccurrence.Identity.Key,right.PointerOccurrence.Identity.Key);
        });
        foreach(var group in output.GroupBy(item=>item.TextOccurrence.Identity.Key,StringComparer.Ordinal))
        {
            var pointerCount=group.Select(item=>item.PointerOccurrence.Identity.Key).Distinct(StringComparer.Ordinal).Count();
            var headerCount=group.Select(item=>item.Relationship.DialogueHeaderIdentity?.Key).Where(key=>key is not null)
                .Distinct(StringComparer.Ordinal).Count();
            if(pointerCount>1||headerCount>1)
            {
                var text=group.First().TextOccurrence;
                diagnostics.Add(new("MultipleVerifiedDialogueLinks","Several verified dialogue links refer to this localized occurrence.",
                    LocalizationReferenceLinkStatus.Ambiguous,[text.ResourceIdentity],Count:group.LongCount()));
            }
        }
        sourceLease?.EnsureUnchanged();
        coverageLease?.EnsureUnchanged();
        indexLease.EnsureUnchanged();
        return new(exactTopLevelAudioIdentity,headers,output.AsReadOnly(),diagnostics.AsReadOnly())
        { IsComplete=completeAtStart&&sourceVerification is { UnverifiedSources.Count:0 } };
    }

    private async Task FindAvailableCandidatesAsync(LocalizationSession session,IReadOnlyList<ResourceIdentity> headers,
        Dictionary<string,(ResourceIdentity Pointer,List<ResourceIdentity> Headers)> candidates,
        List<LocalizationAudioLookupIssue> diagnostics,LocalizationPayloadVerificationLease indexLease,CancellationToken token)
    {
        var headerKeys=headers.Select(header=>header.Key).ToHashSet(StringComparer.Ordinal);
        foreach(var resource in session.Resources.OrderBy(resource=>resource.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var source=new ResourceIdentity(session.Snapshot.IndexContentId,resource.Ordinal);
            var identities=await session.CapturePublishedPointerIdentitiesAsync(source,token).ConfigureAwait(false);
            if(identities.Count==0)continue;
            await using var sourceLease=await session.AcquirePayloadVerificationAsync([source],token,indexLease).ConfigureAwait(false);
            sourceLease.EnsureUnchanged();
            LocalizationReferenceReadResult references;
            try{references=await referenceReader.ReadAsync(session.Snapshot,source,token).ConfigureAwait(false);}
            catch(ArchiveException error) when(IsReferenceDataIssue(error.Code))
            {
                diagnostics.Add(new(error.Code,error.Message,LocalizationReferenceLinkStatus.Unresolved,[source]));
                continue;
            }
            if(references.Status!=LocalizationReferenceReadStatus.Verified)
            {
                diagnostics.Add(new("ReferenceContainerMissing","The localization source has no supported reference table.",
                    LocalizationReferenceLinkStatus.Unmatched,[source]));
                continue;
            }
            foreach(var batch in Batch(identities,PointerPageSize))
            {
                token.ThrowIfCancellationRequested();
                var pointers=await session.GetRecordsAsync(batch,token).ConfigureAwait(false);
                if(pointers.Count<batch.Count)
                    diagnostics.Add(new("PointerOccurrenceUnavailable","Some pointer occurrences are no longer available in this text index.",
                        LocalizationReferenceLinkStatus.Unresolved,[source],Count:batch.Count-pointers.Count));
                foreach(var link in LocalizationDiscovery.ResolvePointerReferences(pointers,references.Relationships))
                {
                    if(link.Status==LocalizationReferenceLinkStatus.Linked&&link.TargetResourceType==4091&&
                       link.TargetResource is { } header&&headerKeys.Contains(header.Key))
                        AddCandidate(candidates,link.Occurrence.Identity,header);
                    else if(link.Status!=LocalizationReferenceLinkStatus.Linked)
                        diagnostics.Add(new("PointerReference"+link.Status,DescribePointerStatus(link.Status),
                            link.Status,[source],link.Occurrence.Identity));
                }
            }
            sourceLease.EnsureUnchanged();
        }
        indexLease.EnsureUnchanged();
    }
    private static void AddCandidate(Dictionary<string,(ResourceIdentity Pointer,List<ResourceIdentity> Headers)> candidates,
        ResourceIdentity pointer,ResourceIdentity header)
    {
        if(!candidates.TryGetValue(pointer.Key,out var item))item=(pointer,[]);
        if(!item.Headers.Any(existing=>existing.Key==header.Key))item.Headers.Add(header);
        candidates[pointer.Key]=item;
    }
    private static ResourceRecord FindTopLevelResource(ArchiveSnapshot snapshot,ResourceIdentity identity,uint requiredType,string label)
    {
        if(identity.IndexContentId!=snapshot.IndexContentId||identity.MemberPath is not null||identity.Ordinal<0||identity.Ordinal>=snapshot.Records.Count)
            throw new ArchiveException("InvalidIdentity",$"The selected {label} does not belong to this archive snapshot.");
        var resource=snapshot.Records[(int)identity.Ordinal];
        if(resource.Ordinal!=identity.Ordinal||resource.Type!=requiredType||resource.Validity!=ResourceValidity.Valid)
            throw new ArchiveException("InvalidIdentity",$"The selected resource is not a valid top-level type-{requiredType} {label}.");
        return resource;
    }

    private static void ReportProgress(IProgress<LocalizationAudioLookupProgress>? progress,LocalizationAudioLookupProgress value)
    {try{progress?.Report(value);}catch{}}

    private static bool IsReferenceDataIssue(string code)=>code is "ReferenceResourceSizeLimit" or "MalformedReferenceTable" or
        "ReferenceCountLimit" or "UnsupportedReferenceContainerVersion" or "InvalidReferenceSource" or "MissingResource" or "MissingVolume";

    private static string DescribePointerStatus(LocalizationReferenceLinkStatus status)=>status switch
    {
        LocalizationReferenceLinkStatus.Ambiguous=>"A pointer matched multiple possible reference rows.",
        LocalizationReferenceLinkStatus.Unverified=>"A pointer reference did not have verified source evidence.",
        LocalizationReferenceLinkStatus.Unresolved=>"A pointer reference could not be resolved.",
        LocalizationReferenceLinkStatus.TargetResourceNotSampled=>"A pointer target was outside the inspected resource set.",
        _=>"A pointer did not match a unique verified header reference."
    };

    private static string DescribeCachedIssue(LocalizationAudioLookupCacheIssue issue)=>issue.Message??(issue.Code switch
    {
        "ReferenceContainerMissing"=>$"{issue.Count:N0} pointer occurrences in {issue.SourceResources.Count:N0} localization resources have no supported reference table.",
        "MalformedReferenceTable" or "ReferenceResourceSizeLimit" or "ReferenceCountLimit" or "UnsupportedReferenceContainerVersion" or "MissingResource" or "MissingVolume"=>
            $"The reference data for {issue.Count:N0} pointer occurrences in {issue.SourceResources.Count:N0} localization resources could not be read.",
        "PointerReferenceAmbiguous"=>$"{issue.Count:N0} pointers in {issue.SourceResources.Count:N0} localization resources have more than one possible target.",
        "PointerReferenceUnverified"=>$"The source reference evidence for {issue.Count:N0} pointers in {issue.SourceResources.Count:N0} localization resources needs review.",
        "PointerReferenceUnmatched"=>$"{issue.Count:N0} pointers in {issue.SourceResources.Count:N0} localization resources have no matching reference row.",
        "UnsupportedPointerTargetType"=>$"{issue.Count:N0} verified pointers target resources outside the dialogue audio lookup.",
        _=>$"{issue.Count:N0} pointer occurrences in {issue.SourceResources.Count:N0} localization resources could not be linked."
    });

    private static IEnumerable<IReadOnlyList<ResourceIdentity>> Batch(IReadOnlyList<ResourceIdentity> identities,int batchSize)
    {
        for(var offset=0;offset<identities.Count;offset+=batchSize)
            yield return identities.Skip(offset).Take(Math.Min(batchSize,identities.Count-offset)).ToArray();
    }

    private sealed class AudioLookupIssueAccumulator(string code,LocalizationReferenceLinkStatus status,string message)
    {
        private readonly Dictionary<string,ResourceIdentity> sources=new(StringComparer.Ordinal);
        public string Code { get; }=code;
        public LocalizationReferenceLinkStatus Status { get; }=status;
        public string Message { get; }=message;
        public long Count { get; private set; }
        public void Add(LocalizationAudioLookupIssue issue)
        {
            Count+=issue.Count;
            foreach(var source in issue.SourceResources)sources.TryAdd(source.Key,source);
        }
        public LocalizationAudioLookupCacheIssue ToCacheIssue()=>new(Code,Count,sources.Values.OrderBy(source=>source.Ordinal).ToArray(),Status,Message);
    }
}
