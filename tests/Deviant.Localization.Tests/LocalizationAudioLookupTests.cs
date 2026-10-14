using System.Buffers.Binary;
using System.Text;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Localization.Model;
using Xunit;

namespace Deviant.Localization.Tests;

public sealed class LocalizationAudioLookupTests
{
    [Fact]
    public async Task BuildAndFindPreserveDuplicateLinesLanguagesOwnersPointersAndVerifiedEdges()
    {
        using var fixture=new LocalizationFixture();
        var snapshot=await BuildAudioLookupSnapshotAsync(fixture);
        var sourceSnapshot=SetResourceTypes(snapshot,[1016,4091,1033,1031,1016]);
        try
        {
            await using var session=await LocalizationSession.OpenAsync(sourceSnapshot,fixture.CachePath);
            await session.EnrichmentTask;
            var lookup=new LocalizationAudioLookup();
            var built=await lookup.BuildAsync(session);

            Assert.False(built.ReusedCache);
            Assert.Equal<long?>(4,built.VerifiedLinks);
            Assert.Equal(2,built.ResourcesVisited);
            var audio=new ResourceIdentity(sourceSnapshot.IndexContentId,2);
            var header=new ResourceIdentity(sourceSnapshot.IndexContentId,1);
            var result=await lookup.FindAudioAsync(session,audio,[header]);

            Assert.Equal(audio,result.AudioIdentity);
            Assert.Equal(header,Assert.Single(result.DialogueHeaderIdentities));
            Assert.Equal(10,result.Occurrences.Count);
            Assert.Equal(4,result.Occurrences.Select(item=>item.TextOccurrence.Identity).Distinct().Count());
            Assert.Equal(4,result.Occurrences.Select(item=>item.PointerOccurrence.Identity).Distinct().Count());
            Assert.Equal(1,result.Occurrences.Count(item=>item.TextOccurrence.ResourceOrdinal==4));
            Assert.Contains(result.Occurrences,item=>item.TextLanguage=="FRE"&&item.PointerLanguage=="ENG");
            Assert.Contains(result.Occurrences,item=>item.TextOccurrence.Text=="Duplicate English line");
            Assert.Contains(result.Diagnostics,item=>item.Code=="MultipleVerifiedDialogueLinks"&&
                item.Status==LocalizationReferenceLinkStatus.Ambiguous);
            Assert.All(result.Occurrences,item=>
            {
                Assert.Equal("LINE_KEY",item.TextOccurrence.Key);
                Assert.Equal(item.PointerOccurrence.ResourceIdentity,item.TextOccurrence.ResourceIdentity);
                Assert.Equal(header,item.Relationship.DialogueHeaderIdentity);
                Assert.Equal(audio,item.Relationship.AudioResourceIdentity);
                Assert.Contains(item.Relationship.EvidencePath,edge=>edge.Reference.ResourceType==4091);
                Assert.Contains(item.Relationship.EvidencePath,edge=>edge.Reference.ResourceType==1033);
                Assert.All(item.Relationship.EvidencePath,edge=>
                {
                    Assert.True(edge.Evidence.Verified);
                    Assert.NotNull(edge.NavigationTarget);
                });
            });

            var warm=await lookup.BuildAsync(session);
            Assert.True(warm.ReusedCache);
            Assert.Null(warm.VerifiedLinks);
            var warmResult=await lookup.FindAudioAsync(session,audio,[header]);
            Assert.Equal(result.Occurrences.Select(item=>item.TextOccurrence.Identity),
                warmResult.Occurrences.Select(item=>item.TextOccurrence.Identity));
        }
        finally{DeleteCache(fixture);}
    }

    [Fact]
    public async Task MultipleHeadersKeepAllTextCandidatesAndAmbiguousEventTargetsRemainVisible()
    {
        using var fixture=new LocalizationFixture();
        var snapshot=await BuildAudioLookupSnapshotAsync(fixture,duplicateEventTarget:true,multipleHeaders:true);
        var sourceSnapshot=SetResourceTypes(snapshot,[1016,4091,1033,1031,1031,4091,1016]);
        try
        {
            await using var session=await LocalizationSession.OpenAsync(sourceSnapshot,fixture.CachePath);
            await session.EnrichmentTask;
            var lookup=new LocalizationAudioLookup();await lookup.BuildAsync(session);
            var audio=new ResourceIdentity(sourceSnapshot.IndexContentId,2);
            var headers=new[]{new ResourceIdentity(sourceSnapshot.IndexContentId,1),new(sourceSnapshot.IndexContentId,5)};
            var result=await lookup.FindAudioAsync(session,audio,headers);

            Assert.Equal(2,result.DialogueHeaderIdentities.Count);
            Assert.Equal(10,result.Occurrences.Count);
            Assert.All(result.Occurrences,item=>Assert.Contains(item.Relationship.DialogueHeaderIdentity,headers));
            Assert.Contains(result.Occurrences,item=>item.Relationship.DialogueEventIdentity is null);
            Assert.Contains(result.Diagnostics,item=>item.Code=="DialogueEventReferenceAmbiguous"&&
                item.Status==LocalizationReferenceLinkStatus.Ambiguous);
            Assert.Contains(result.Diagnostics,item=>item.Code=="MultipleVerifiedDialogueLinks"&&
                item.Status==LocalizationReferenceLinkStatus.Ambiguous);
        }
        finally{DeleteCache(fixture);}
    }

    [Fact]
    public async Task UnmatchedPointerCoverageIsCachedAndReportedAfterWarmReuse()
    {
        using var fixture=new LocalizationFixture();
        var snapshot=await BuildAudioLookupSnapshotAsync(fixture,unmatchedSecondSourcePointer:true);
        var sourceSnapshot=SetResourceTypes(snapshot,[1016,4091,1033,1031,1016]);
        try
        {
            await using var session=await LocalizationSession.OpenAsync(sourceSnapshot,fixture.CachePath);
            await session.EnrichmentTask;
            var lookup=new LocalizationAudioLookup();
            var first=await lookup.BuildAsync(session);
            var issue=Assert.Single(first.Issues,item=>item.Code=="PointerReferenceUnmatched");
            Assert.Equal(1,issue.Count);
            Assert.Equal(LocalizationReferenceLinkStatus.Unmatched,issue.Status);
            Assert.Contains("did not match a unique verified header",issue.Message,StringComparison.Ordinal);
            Assert.Equal<long?>(3,first.VerifiedLinks);
            Assert.Contains(new ResourceIdentity(sourceSnapshot.IndexContentId,4),issue.SourceResources);

            var warm=await lookup.BuildAsync(session);
            Assert.True(warm.ReusedCache);
            var restored=Assert.Single(warm.Issues,item=>item.Code==issue.Code);
            Assert.Equal(issue.Count,restored.Count);
            Assert.Equal(issue.Status,restored.Status);
            Assert.Equal(issue.Message,restored.Message);
            Assert.Equal(issue.SourceResources,restored.SourceResources);
            var result=await lookup.FindAudioAsync(session,new(sourceSnapshot.IndexContentId,2),[new(sourceSnapshot.IndexContentId,1)]);
            Assert.Contains(result.Diagnostics,item=>item.Code==issue.Code);
            Assert.DoesNotContain(result.Occurrences,item=>item.TextOccurrence.ResourceOrdinal==4);
        }
        finally{DeleteCache(fixture);}
    }

    [Fact]
    public async Task LookupBuildCancellationLeavesNoReadyPartialIndex()
    {
        using var fixture=new LocalizationFixture();
        var snapshot=await BuildAudioLookupSnapshotAsync(fixture);
        var sourceSnapshot=SetResourceTypes(snapshot,[1016,4091,1033,1031,1016]);
        try
        {
            await using var session=await LocalizationSession.OpenAsync(sourceSnapshot,fixture.CachePath);
            await session.EnrichmentTask;
            var contentId=Assert.IsType<string>(session.MetadataContentId);
            using var cancellation=new CancellationTokenSource();
            var progress=new InlineProgress<LocalizationAudioLookupProgress>(update=>
            {if(update.CompletedResources==1)cancellation.Cancel();});

            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new LocalizationAudioLookup().BuildAsync(session,progress,cancellation.Token));
            Assert.False(await session.IsAudioLookupReadyAsync(LocalizationAudioLookup.IndexVersion,contentId));
            await Assert.ThrowsAsync<ArchiveException>(()=>session.GetAudioLookupCandidatesAsync(
                new ResourceIdentity(sourceSnapshot.IndexContentId,1),LocalizationAudioLookup.IndexVersion,contentId));
        }
        finally{DeleteCache(fixture);}
    }

    [Fact]
    public async Task SupersededLookupGenerationCannotMarkItsPartialRowsReady()
    {
        using var fixture=new LocalizationFixture();
        var snapshot=await BuildAudioLookupSnapshotAsync(fixture);
        var sourceSnapshot=SetResourceTypes(snapshot,[1016,4091,1033,1031,1016]);
        try
        {
            await using var session=await LocalizationSession.OpenAsync(sourceSnapshot,fixture.CachePath);
            await session.EnrichmentTask;
            var contentId=Assert.IsType<string>(session.MetadataContentId);var replaced=false;
            var progress=new InlineProgress<LocalizationAudioLookupProgress>(update=>
            {
                if(replaced||update.CompletedResources!=1)return;
                _=session.BeginAudioLookupBuildAsync(LocalizationAudioLookup.IndexVersion,contentId).GetAwaiter().GetResult();
                replaced=true;
            });

            var error=await Assert.ThrowsAsync<ArchiveException>(()=>new LocalizationAudioLookup().BuildAsync(session,progress));
            Assert.Equal("AudioLookupSuperseded",error.Code);
            Assert.False(await session.IsAudioLookupReadyAsync(LocalizationAudioLookup.IndexVersion,contentId));
        }
        finally{DeleteCache(fixture);}
    }

    [Fact]
    public async Task SeparateSessionsSharingFeatureStoreSerializeReverseIndexBuilds()
    {
        using var fixture=new LocalizationFixture();
        var snapshot=SetResourceTypes(await BuildAudioLookupSnapshotAsync(fixture),[1016,4091,1033,1031,1016]);
        try
        {
            await using var firstSession=await LocalizationSession.OpenAsync(snapshot,fixture.CachePath);
            await firstSession.EnrichmentTask;
            await using var secondSession=await LocalizationSession.OpenAsync(snapshot,fixture.CachePath);
            await secondSession.EnrichmentTask;
            var firstLookup=new LocalizationAudioLookup();var secondLookup=new LocalizationAudioLookup();

            var results=await Task.WhenAll(firstLookup.BuildAsync(firstSession),secondLookup.BuildAsync(secondSession));

            Assert.Single(results,result=>!result.ReusedCache);
            Assert.Single(results,result=>result.ReusedCache);
            Assert.True(await firstSession.IsAudioLookupReadyAsync(LocalizationAudioLookup.IndexVersion,firstSession.MetadataContentId!));
            Assert.True(await secondSession.IsAudioLookupReadyAsync(LocalizationAudioLookup.IndexVersion,secondSession.MetadataContentId!));
        }
        finally{DeleteCache(fixture);}
    }

    [Fact]
    public async Task SharedFeatureStoreBuildTokenRejectsSupersededSessionWrites()
    {
        using var fixture=new LocalizationFixture();
        var snapshot=SetResourceTypes(await BuildAudioLookupSnapshotAsync(fixture),[1016,4091,1033,1031,1016]);
        try
        {
            await using var first=await LocalizationSession.OpenAsync(snapshot,fixture.CachePath);await first.EnrichmentTask;
            await using var second=await LocalizationSession.OpenAsync(snapshot,fixture.CachePath);await second.EnrichmentTask;
            var contentId=Assert.IsType<string>(first.MetadataContentId);
            var header=new ResourceIdentity(snapshot.IndexContentId,1);
            var source=new ResourceIdentity(snapshot.IndexContentId,0);
            var pointer=(await first.QueryAsync(new LocalizationQuery(Source:source,
                Kind:LocalizationRecordKind.Pointer,Limit:10))).Records.First(record=>record.PointerKind==1);

            var firstGeneration=await first.BeginAudioLookupBuildAsync(LocalizationAudioLookup.IndexVersion,contentId);
            var secondGeneration=await second.BeginAudioLookupBuildAsync(LocalizationAudioLookup.IndexVersion,contentId);
            await Assert.ThrowsAsync<ArchiveException>(()=>first.UpsertAudioLookupCandidatesAsync(header,[pointer.Identity],
                LocalizationAudioLookup.IndexVersion,contentId,firstGeneration));
            await Assert.ThrowsAsync<ArchiveException>(()=>first.CompleteAudioLookupBuildAsync(LocalizationAudioLookup.IndexVersion,
                contentId,firstGeneration,Array.Empty<LocalizationAudioLookupCacheIssue>()));

            await second.UpsertAudioLookupCandidatesAsync(header,[pointer.Identity],LocalizationAudioLookup.IndexVersion,
                contentId,secondGeneration);
            await second.CompleteAudioLookupBuildAsync(LocalizationAudioLookup.IndexVersion,contentId,secondGeneration,
                Array.Empty<LocalizationAudioLookupCacheIssue>());
            Assert.Equal(new[]{pointer.Identity},await first.GetAudioLookupCandidatesAsync(header,
                LocalizationAudioLookup.IndexVersion,contentId));
        }
        finally{DeleteCache(fixture);}
    }

    [Fact]
    public async Task RevalidationRejectsReferenceBytesChangedAfterTheIndexWasBuilt()
    {
        using var fixture=new LocalizationFixture();
        var snapshot=await BuildAudioLookupSnapshotAsync(fixture);
        var sourceSnapshot=SetResourceTypes(snapshot,[1016,4091,1033,1031,1016]);
        try
        {
            await using var session=await LocalizationSession.OpenAsync(sourceSnapshot,fixture.CachePath);
            await session.EnrichmentTask;var lookup=new LocalizationAudioLookup();await lookup.BuildAsync(session);
            var bytes=await File.ReadAllBytesAsync(fixture.VolumePath);
            var source=sourceSnapshot.Records.Single(row=>row.Ordinal==0);
            var timestamp=File.GetLastWriteTimeUtc(fixture.VolumePath);
            bytes[checked((int)source.Offset+28)]^=1;
            await File.WriteAllBytesAsync(fixture.VolumePath,bytes);
            File.SetLastWriteTimeUtc(fixture.VolumePath,timestamp);

            var error=await Assert.ThrowsAsync<ArchiveException>(()=>lookup.FindAudioAsync(session,
                new ResourceIdentity(sourceSnapshot.IndexContentId,2),[new ResourceIdentity(sourceSnapshot.IndexContentId,1)]));
            Assert.Equal("SourceChanged",error.Code);
        }
        finally{DeleteCache(fixture);}
    }

    [Fact]
    public async Task DuplicateType1033IdsDoNotPromoteEitherAudioCandidate()
    {
        using var fixture=new LocalizationFixture();
        var snapshot=await BuildAmbiguousAudioSnapshotAsync(fixture);
        var sourceSnapshot=SetResourceTypes(snapshot,[1016,4091,1033,1033,1031],[1,2,3,3,5]);
        try
        {
            await using var session=await LocalizationSession.OpenAsync(sourceSnapshot,fixture.CachePath);
            await session.EnrichmentTask;var lookup=new LocalizationAudioLookup();await lookup.BuildAsync(session);

            var error=await Assert.ThrowsAsync<ArchiveException>(()=>lookup.FindAudioAsync(session,
                new ResourceIdentity(sourceSnapshot.IndexContentId,2),[new ResourceIdentity(sourceSnapshot.IndexContentId,1)]));
            Assert.Equal("AmbiguousAudioIdentity",error.Code);
        }
        finally{DeleteCache(fixture);}
    }

    [Fact]
    public async Task LookupRequiresCanonicalTopLevelAudioResourceIdentity()
    {
        using var fixture=new LocalizationFixture();
        var snapshot=SetResourceTypes(await BuildAudioLookupSnapshotAsync(fixture),[1016,4091,1033,1031,1016]);
        try
        {
            await using var session=await LocalizationSession.OpenAsync(snapshot,fixture.CachePath);
            await session.EnrichmentTask;var lookup=new LocalizationAudioLookup();await lookup.BuildAsync(session);
            var error=await Assert.ThrowsAsync<ArchiveException>(()=>lookup.FindAudioAsync(session,
                new ResourceIdentity(snapshot.IndexContentId,2,"wem"),[new ResourceIdentity(snapshot.IndexContentId,1)]));
            Assert.Equal("InvalidIdentity",error.Code);
        }
        finally{DeleteCache(fixture);}
    }

    private static async Task<ArchiveSnapshot> BuildAudioLookupSnapshotAsync(LocalizationFixture fixture,
        bool duplicateEventTarget=false,bool multipleHeaders=false,bool unmatchedSecondSourcePointer=false)
    {
        var secondHeaderId=multipleHeaders?(duplicateEventTarget?6u:5u):2u;
        var secondHeaderTrailer=multipleHeaders?(byte)0xA6:(byte)0xA5;
        var eng=new List<byte[]>
        {
            LocalizationFixture.DirectText("LINE_KEY","First English line"),
            LocalizationFixture.DirectText("LINE_KEY","Duplicate English line"),
            LocalizationFixture.PointerRecord(("LINE_KEY",1,PointerPayload(0xA5,4091,2))),
            LocalizationFixture.PointerRecord(("LINE_KEY",1,PointerPayload(0xA5,4091,2)))
        };
        var fre=new List<byte[]>
        {
            LocalizationFixture.DirectText("LINE_KEY","Ligne française"),
            LocalizationFixture.PointerRecord(("LINE_KEY",1,PointerPayload(0xA5,4091,2)))
        };
        var secondSource=LocalizationFixture.MakeLocalization(6,
            ("ENG",[LocalizationFixture.DirectText("LINE_KEY","Second owner line"),
                LocalizationFixture.PointerRecord(("LINE_KEY",1,PointerPayload(secondHeaderTrailer,4091,
                    unmatchedSecondSourcePointer?999u:secondHeaderId)))]));
        var firstPayload=LocalizationFixture.MakeLocalization(6,("ENG",eng.ToArray()),("FRE",fre.ToArray()));
        fixture.AddResource(Wrap([(4091u,2u,(byte)0xA5)],firstPayload));
        var headerReferences=new List<(uint Type,uint Id,byte Trailer)>
        { (1033u,3u,0x11),(1031u,4u,0x12) };
        if(duplicateEventTarget)headerReferences.Add((1031u,5u,0x13));
        fixture.AddResource(Wrap(headerReferences.ToArray()));
        fixture.AddResource([0x01,0x02,0x03]);
        fixture.AddResource([0x04,0x05,0x06]);
        if(duplicateEventTarget)fixture.AddResource([0x07,0x08,0x09]);
        if(multipleHeaders)fixture.AddResource(Wrap([(1033u,3u,0x21),(1031u,4u,0x22)]));
        var secondReferences=(4091u,secondHeaderId,secondHeaderTrailer);
        fixture.AddResource(Wrap([secondReferences],secondSource));
        return await fixture.BuildAsync();
    }

    private static async Task<ArchiveSnapshot> BuildAmbiguousAudioSnapshotAsync(LocalizationFixture fixture)
    {
        var localization=LocalizationFixture.MakeLocalization(6,
            ("ENG",[LocalizationFixture.DirectText("LINE_KEY","Ambiguous target line"),
                LocalizationFixture.PointerRecord(("LINE_KEY",1,PointerPayload(0xA5,4091,2)))]));
        fixture.AddResource(Wrap([(4091u,2u,(byte)0xA5)],localization));
        fixture.AddResource(Wrap([(1033u,3u,(byte)0x11),(1031u,5u,(byte)0x12)]));
        fixture.AddResource([0x01,0x02,0x03]);fixture.AddResource([0x04,0x05,0x06]);fixture.AddResource([0x07,0x08,0x09]);
        return await fixture.BuildAsync();
    }

    private static ArchiveSnapshot SetResourceTypes(ArchiveSnapshot snapshot,IReadOnlyList<uint> types,
        IReadOnlyList<uint>? ids=null)=>snapshot with
    { Records=snapshot.Records.Select(record=>record with
        { Type=types[checked((int)record.Ordinal)],Id=ids is null?record.Id:ids[checked((int)record.Ordinal)] }).ToArray() };

    private static byte[] PointerPayload(byte trailer,uint type,uint id)
    {
        var payload=new byte[9];payload[0]=trailer;BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1,4),type);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(5,4),id);return payload;
    }

    private static byte[] Wrap((uint Type,uint Id,byte Trailer)[] references,byte[]? body=null)
    {
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream,Encoding.UTF8,true);
        writer.Write("COM_CONT"u8);writer.Write(6u);writer.Write((uint)(4+references.Length*9+(body?.Length??0)));
        writer.Write((uint)references.Length);
        foreach(var reference in references){writer.Write(reference.Type);writer.Write(reference.Id);writer.Write(reference.Trailer);}
        if(body is not null)writer.Write(body);return stream.ToArray();
    }

    private static void DeleteCache(LocalizationFixture fixture)
    {if(Directory.Exists(fixture.CachePath))Directory.Delete(fixture.CachePath,true);}

    private sealed class InlineProgress<T>(Action<T> callback):IProgress<T>
    {public void Report(T value)=>callback(value);}
}
