using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;
using System.Diagnostics.CodeAnalysis;

namespace Deviant.Browser;

internal sealed record AudioRecoveryCheckpoint(SourceRevision Revision,MediaEntry Media,string SourceLabel)
{
    public SelectedAudioClassificationScope? SelectedScope { get; init; }
}

public sealed partial class AudioExplorerSession
{
    private const string RecoveryFeaturePrefix="audio.recovery/item/";
    private const string RecoveryVersion="audio-recovery-media-v1/"+SnapshotVersion;
    private const int MaximumRecoveryPayloadBytes=4*1024*1024;
    private long recoveryMediaCount=-1;

    private async Task PublishRecoveryCheckpointsAsync(IReadOnlyList<MediaEntry> media,
        IReadOnlyDictionary<string,string> labels,CancellationToken token,
        IReadOnlyDictionary<string,SelectedAudioClassificationScope>? selectedScopes=null)
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
                new AudioRecoveryCheckpoint(revision,entry,label)
                { SelectedScope=selectedScopes is not null&&selectedScopes.TryGetValue(identity.Key,out var selectedScope)?selectedScope:null },MaximumRecoveryPayloadBytes);
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
        var assetQuery=ToAssetQuery(query);
        var page=await Task.Run(()=>storeOwner.Store.ReadBoundedAssetFeaturePage<AudioRecoveryCheckpoint>(assetQuery,
            RecoveryFeaturePrefix,RecoveryVersion,MaximumRecoveryPayloadBytes,token),token).ConfigureAwait(false);
        // Only an unfiltered count describes the retained family. Filtered totals stay with this page.
        if((assetQuery with { Offset=0,Limit=100,Sort=AssetSort.Default,Descending=false })==new AssetQuery(Family:AssetFamily))
            Interlocked.Exchange(ref recoveryMediaCount,page.Total);
        var rows=new List<AudioExplorerRow>(page.Rows.Count);
        foreach(var stored in page.Rows)
        {
            token.ThrowIfCancellationRequested();
            lock(catalogGate)
            {
                var row=RecoveryMetadataRow(stored);
                labelsByIdentity[row.Identity.Key]=SourceLabel(stored.Asset);
                RememberBrowseRow(row,stored.SerializedBytes);rows.Add(row);
            }
        }
        token.ThrowIfCancellationRequested();
        return new(page.Total,rows.AsReadOnly());
    }

    private AudioExplorerRow RecoveryMetadataRow(BoundedAssetFeatureRow<AudioRecoveryCheckpoint> stored)
    {
        string? error=stored.ErrorCode;
        if(TryReadRecoveryMedia(stored,out var media,out var label))
        {
            try
            {
                return new(Describe(media,label),media,aliases.Get(stored.Asset.Identity),
                    string.IsNullOrWhiteSpace(media.BankName)?null:media.BankName);
            }
            catch(Exception invalid) when(invalid is ArchiveException or InvalidDataException or ArgumentException or OverflowException)
            { error="AudioRecoveryInvalid"; }
        }
        else error??=stored.Feature is null?"BoundedFeatureMissing":"AudioRecoveryInvalid";
        var descriptor=stored.Asset with { Capabilities=AssetCapabilities.None };
        return new(descriptor,null,aliases.Get(descriptor.Identity),
            bankLabelsByKey.GetValueOrDefault(descriptor.GroupKey??StandaloneGroupKey)) { HydrationErrorCode=error };
    }

    private static string SourceLabel(AssetDescriptor descriptor) =>
        descriptor.Metadata?.GetValueOrDefault("audio.source-label")??descriptor.Label;

    private bool TryReadRecoveryMedia(BoundedAssetFeatureRow<AudioRecoveryCheckpoint> stored,
        [NotNullWhen(true)] out MediaEntry? media,out string label)
    {
        var revision=new SourceRevision(Snapshot.IndexContentId,Snapshot.PhysicalSourceStamp);
        var checkpoint=stored.Feature;media=checkpoint?.Media;label=SourceLabel(stored.Asset);
        return stored.ErrorCode is null&&checkpoint is not null&&checkpoint.Revision==revision&&media is not null&&
            media.Identity is not null&&media.Identity==stored.Asset.Identity&&stored.Asset.Revision==revision&&
            stored.Asset.Family==AssetFamily&&checkpoint.SourceLabel is not null&&stored.Asset.Metadata is not null&&
            media.Slice is not null&&media.Slice.FilePath is not null&&media.BankKey is not null&&media.Language is not null&&
            media.Codec is not null&&media.ResolutionDetails is not null&&media.Names is not null&&
            media.Names.All(item=>item is not null)&&media.Aliases is not null&&media.EventKeys is not null&&
            media.SourceEvidence is not null&&media.SourceEvidence.All(item=>item is not null)&&media.MetadataReadDigests is not null&&
            media.MetadataReadDigests.All(digest=>digest is not null&&digest.Slice is not null&&digest.Slice.FilePath is not null&&digest.Sha256 is not null)&&
            stored.Asset.Metadata.TryGetValue("audio.source-label",out var sourceLabel)&&sourceLabel==checkpoint.SourceLabel;
    }
}
