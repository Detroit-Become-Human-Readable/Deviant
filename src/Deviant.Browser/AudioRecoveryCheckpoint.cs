using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;

namespace Deviant.Browser;

internal sealed record AudioRecoveryCheckpoint(SourceRevision Revision,MediaEntry Media,string SourceLabel);

public sealed partial class AudioExplorerSession
{
    private const string RecoveryFeaturePrefix="audio.recovery/item/";
    private const string RecoveryVersion="audio-recovery-media-v1/"+SnapshotVersion;
    private const int MaximumRecoveryPayloadBytes=4*1024*1024;
    private long recoveryMediaCount=-1;

    private async Task PublishRecoveryCheckpointsAsync(IReadOnlyList<MediaEntry> media,
        IReadOnlyDictionary<string,string> labels,CancellationToken token)
    {
        var revision=new SourceRevision(Snapshot.IndexContentId,Snapshot.PhysicalSourceStamp);
        var batch=new List<BoundedFeatureAssetWrite>(FeatureStore.BoundedAssetPublicationLimit);
        long batchBytes=0;
        async Task FlushAsync()
        {
            if(batch.Count==0)return;
            var persisted=await Task.Run(()=>featureStore.PublishBoundedAssetsBatch(batch,token),token).ConfigureAwait(false);
            if(persisted.Count!=batch.Count)
                throw new ArchiveException("AudioRecoveryOwnershipConflict","A saved audio entry belongs to another asset family.");
            batch.Clear();batchBytes=0;
        }
        foreach(var entry in media)
        {
            token.ThrowIfCancellationRequested();
            var identity=entry.Identity??throw new InvalidDataException("The audio checkpoint has no stable identity.");
            var label=labels.GetValueOrDefault(identity.Key,UserLabel(entry));
            var descriptor=Describe(entry,label);
            var feature=BoundedFeatureWrite.Create(RecoveryFeaturePrefix+identity.Key,RecoveryVersion,
                new AudioRecoveryCheckpoint(revision,entry,label),MaximumRecoveryPayloadBytes);
            // Use the same bounded serializer as publication for the combined transaction limit.
            var assetBytes=BoundedFeatureWrite.Create("audio.recovery-size",RecoveryVersion,descriptor,
                MaximumRecoveryPayloadBytes).Utf8ByteCount;
            var bytes=checked((long)feature.Utf8ByteCount+assetBytes);
            if(batch.Count>0&&(batch.Count==FeatureStore.BoundedAssetPublicationLimit||
                bytes>FeatureStore.MaximumBoundedFeatureBatchBytes-batchBytes))
                await FlushAsync().ConfigureAwait(false);
            batch.Add(new(feature,descriptor));batchBytes=checked(batchBytes+bytes);
        }
        await FlushAsync().ConfigureAwait(false);
    }

    private async Task RestoreRecoveryCountAsync(CancellationToken token)
    {
        var count=await Task.Run(()=>featureStore.Count(new(Family:AssetFamily),token),token).ConfigureAwait(false);
        Interlocked.Exchange(ref recoveryMediaCount,count);
        if(count>0&&!IsEnrichmentStarted)
        {
            Volatile.Write(ref currentProgress,new("Audio names available",count,count));
            Interlocked.Exchange(ref enrichmentProgressCompleted,count);
            Interlocked.Exchange(ref enrichmentProgressTotal,count);
        }
    }

    private async Task<AudioExplorerPage> ReadRecoveryPageAsync(AudioExplorerQuery query,CancellationToken token)
    {
        if(query.Limit>FeatureStore.MaximumLinkedFeaturePageRows)
            throw new ArgumentOutOfRangeException(nameof(query),"Saved audio pages may contain at most 128 entries.");
        using var storeOwner=AcquireSharedFeatureStore();
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,storeOwner.SourceCancellation);
        token=linked.Token;
        token.ThrowIfCancellationRequested();
        var page=await Task.Run(()=>storeOwner.Store.ReadBoundedAssetFeaturePage<AudioRecoveryCheckpoint>(ToAssetQuery(query),
            RecoveryFeaturePrefix,RecoveryVersion,MaximumRecoveryPayloadBytes,token),token).ConfigureAwait(false);
        // This is the query's matching total; the session count remains the whole retained family.
        await RestoreRecoveryCountAsync(token).ConfigureAwait(false);
        var revision=new SourceRevision(Snapshot.IndexContentId,Snapshot.PhysicalSourceStamp);
        var rows=new List<AudioExplorerRow>(page.Rows.Count);
        using var verification=new AudioMetadataVerifier(Snapshot).CreateLease();
        foreach(var stored in page.Rows)
        {
            token.ThrowIfCancellationRequested();
            var checkpoint=stored.Feature;
            if(checkpoint is null)
            {
                var error=new ArchiveException("AudioRecoveryRequired","This saved clip needs its source details loaded.");
                error.Data["RecoveryStatus"]=stored.ErrorCode??"BoundedFeatureMissing";
                throw error;
            }
            var media=checkpoint.Media;
            if(checkpoint.Revision!=revision||media is null||media.Identity is null||media.Identity!=stored.Asset.Identity||
               stored.Asset.Revision!=revision||stored.Asset.Family!=AssetFamily||checkpoint.SourceLabel is null||stored.Asset.Metadata is null||
               media.Slice is null||media.BankKey is null||media.Language is null||media.Codec is null||media.ResolutionDetails is null||
               media.Names is null||media.Aliases is null||media.EventKeys is null||media.SourceEvidence is null||
               media.MetadataReadDigests is null||media.MetadataReadDigests.Any(digest=>digest is null||digest.Slice is null)||
               !stored.Asset.Metadata.TryGetValue("audio.source-label",out var label)||label!=checkpoint.SourceLabel)
                throw new ArchiveException("AudioRecoveryInvalid","The saved audio details do not match their source entry.");
            using(AssetLoadDiagnostics.Measure("AudioRecoveryVerification",Snapshot.PhysicalSourceStamp))
                await verification.VerifyMediaAsync(media,token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if(MediaPolicy.HasBytes(media))_ = AudioResourceBinding.Resolve(Snapshot,media,original:true);
            lock(catalogGate)
            {
                labelsByIdentity[media.Identity.Key]=checkpoint.SourceLabel;
                var descriptor=Describe(media,checkpoint.SourceLabel);
                var row=new AudioExplorerRow(descriptor,media,aliases.Get(media.Identity),
                    string.IsNullOrWhiteSpace(media.BankName)?null:media.BankName);
                RememberBrowseRow(row,stored.SerializedBytes);rows.Add(row);
            }
        }
        token.ThrowIfCancellationRequested();
        return new(page.Total,rows.AsReadOnly());
    }
}