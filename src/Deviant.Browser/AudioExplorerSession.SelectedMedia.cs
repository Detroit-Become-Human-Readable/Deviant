using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;
using Microsoft.Data.Sqlite;

namespace Deviant.Browser;

public sealed partial class AudioExplorerSession
{
    private readonly SemaphoreSlim selectedMediaGate=new(1,1);
    private readonly AssetMemoryBudget selectedLookupMemory=new();
    private ArchiveSourceSession.Lease? selectedSourceSession;

    /// <summary>Prepares the selected occurrence without restoring unrelated audio metadata.</summary>
    public async Task<AudioExplorerRow> PrepareSelectedMediaAsync(ResourceIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if(identity.IndexContentId!=Snapshot.IndexContentId)
            throw new ArchiveException("InvalidIdentity","The selected clip belongs to another archive generation.");
        var rows=await PrepareSelectedMediaAsync(new[]{identity.Key},cancellationToken).ConfigureAwait(false);
        return rows[0];
    }

    /// <summary>Prepares up to 128 selected keys in their original order. Larger exports can prepare bounded pages.</summary>
    public async Task<IReadOnlyList<AudioExplorerRow>> PrepareSelectedMediaAsync(IEnumerable<string> identityKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identityKeys);
        cancellationToken.ThrowIfCancellationRequested();
        var supplied=identityKeys.Take(FeatureStore.MaximumLinkedFeaturePageRows+1).ToArray();
        if(supplied.Length>FeatureStore.MaximumLinkedFeaturePageRows)
            throw new ArgumentOutOfRangeException(nameof(identityKeys),"Prepare selected clips in pages of at most 128 entries.");
        var prefix=Snapshot.IndexContentId+":";
        if(supplied.Any(key=>string.IsNullOrEmpty(key)||key.Length>2048||!key.StartsWith(prefix,StringComparison.Ordinal)))
            throw new ArchiveException("InvalidIdentity","A selected clip belongs to another archive generation.");
        var keys=supplied.Distinct(StringComparer.Ordinal).ToArray();
        using var owner=AcquireSharedFeatureStore();
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,owner.SourceCancellation);
        var token=linked.Token;
        return await Task.Run(async()=>
        {
            await selectedMediaGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var rows=new List<AudioExplorerRow>(keys.Length);
                long bytes=0;
                foreach(var key in keys)
                {
                    token.ThrowIfCancellationRequested();
                    var (row,retained)=await PrepareSelectedRowAsync(owner.Store,key,token).ConfigureAwait(false);
                    if(retained>FeatureStore.MaximumLinkedFeaturePageBytes-bytes)
                        throw new ArchiveException("AudioSelectionLimit","The selected metadata exceeds one preparation page.");
                    bytes=checked(bytes+retained);rows.Add(row);
                }
                token.ThrowIfCancellationRequested();
                return (IReadOnlyList<AudioExplorerRow>)rows.AsReadOnly();
            }
            finally { selectedMediaGate.Release(); }
        },token).ConfigureAwait(false);
    }

    private async Task<(AudioExplorerRow Row,long SerializedBytes)> PrepareSelectedRowAsync(FeatureStore store,
        string key,CancellationToken token)
    {
        var page=store.ReadBoundedAssetFeaturePage<AudioRecoveryCheckpoint>(new(Family:AssetFamily,Limit:1){ExactIdentityKey=key},
            RecoveryFeaturePrefix,RecoveryVersion,MaximumRecoveryPayloadBytes,token);
        var stored=page.Rows.SingleOrDefault()
            ??throw new ArchiveException("MissingMedia","The selected clip is no longer present in this archive catalog.");
        var label=SourceLabel(stored.Asset);
        MediaEntry? media=null;
        SelectedAudioClassificationScope? scope=null;
        string? cachePersistenceError=null;
        bool persist=false;
        if(TryReadRecoveryMedia(stored,out var recovered,out label))
        { media=recovered;scope=stored.Feature!.SelectedScope; }
        else
        {
            lock(catalogGate)
            {
                if(browseRows.TryGetValue(key,out var resident)&&resident.SourceMedia is { } selected&&resident.SelectedSourceVerified&&
                    resident.Descriptor.Revision==stored.Asset.Revision&&resident.Descriptor.GroupKey==stored.Asset.GroupKey)
                {
                    media=selected;scope=resident.SelectedClassificationScope;persist=true;
                }
                // Complete numeric snapshots retain their existing source-resolution proofs.
                else if(IsCatalogLoaded||browseSummary is not null&&IsEnrichmentComplete)
                {
                    if(mediaByIdentity.TryGetValue(key,out var known))media=known;
                    else if(browseRows.TryGetValue(key,out var row))media=row.SourceMedia;
                }
            }
        }
        if(media is null||!HasSelectedClassification(media,scope)||!media.MetadataReadCoverageComplete||media.MetadataReadDigests.Count==0)
        {
            token.ThrowIfCancellationRequested();
            var logical=AudioResourceBinding.ResolveOwner(Snapshot,stored.Asset.Identity);
            ResourceIdentity? bank=null;
            if(logical.Resource.Type==29)
            {
                bank=DetroitAudioSnapshotReader.ResolveBankDescriptorIdentity(Snapshot,stored.Asset.GroupKey??"");
                _=GetSelectedSourceSession(token);
            }
            var selected=await new DetroitAudioSnapshotReader().ReadSelectedMediaAsync(Snapshot,stored.Asset.Identity,bank,token).ConfigureAwait(false);
            media=selected.Media;scope=selected.Scope;persist=true;
        }
        token.ThrowIfCancellationRequested();
        using var verification=new AudioMetadataVerifier(Snapshot).CreateLease();
        using(AssetLoadDiagnostics.Measure("AudioRecoveryVerification",Snapshot.PhysicalSourceStamp))
            await verification.VerifyMediaAsync(media,token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if(MediaPolicy.HasBytes(media))
        {
            _=AudioResourceBinding.Resolve(Snapshot,media);
            _=AudioResourceBinding.Resolve(Snapshot,media,original:true);
        }
        if(media.Identity!=stored.Asset.Identity||!HasSelectedClassification(media,scope))
            throw new ArchiveException("ClassificationPending","The selected clip's source classification is incomplete.");

        var checkpoint=new AudioRecoveryCheckpoint(new(Snapshot.IndexContentId,Snapshot.PhysicalSourceStamp),media,label)
            { SelectedScope=scope };
        var serialized=BoundedFeatureWrite.Create(RecoveryFeaturePrefix+key,RecoveryVersion,checkpoint,MaximumRecoveryPayloadBytes).Utf8ByteCount;
        if(persist)
        {
            await featureGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                var latestPage=store.ReadBoundedAssetFeaturePage<AudioRecoveryCheckpoint>(new(Family:AssetFamily,Limit:1){ExactIdentityKey=key},
                    RecoveryFeaturePrefix,RecoveryVersion,MaximumRecoveryPayloadBytes,token);
                var latest=latestPage.Rows.SingleOrDefault()
                    ??throw new ArchiveException("MissingMedia","The selected clip was removed while its details loaded.");
                if(latest.Asset.Revision!=stored.Asset.Revision||latest.Asset.GroupKey!=stored.Asset.GroupKey)
                    throw new ArchiveException("SelectionChanged","The selected clip's owner changed while its details loaded.");
                label=SourceLabel(latest.Asset);
                if(TryReadRecoveryMedia(latest,out var refreshed,out var currentLabel)&&
                    HasSelectedClassification(refreshed,latest.Feature!.SelectedScope)&&refreshed.MetadataReadCoverageComplete&&
                    refreshed.MetadataReadDigests.Count>0)
                {
                    // A newer indexing result may include names, events and complete stream resolution.
                    // Verify it before choosing it over this source-local result.
                    await verification.VerifyMediaAsync(refreshed,token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if(MediaPolicy.HasBytes(refreshed))
                    {
                        _=AudioResourceBinding.Resolve(Snapshot,refreshed);
                        _=AudioResourceBinding.Resolve(Snapshot,refreshed,original:true);
                    }
                    media=refreshed;scope=latest.Feature!.SelectedScope;label=currentLabel;
                }
                else
                {
                    IReadOnlyDictionary<string,SelectedAudioClassificationScope>? scopes=scope is { } local
                        ?new Dictionary<string,SelectedAudioClassificationScope>{{key,local}}:null;
                    try
                    {
                        await PublishRecoveryCheckpointsAsync([media],new Dictionary<string,string>{{key,label}},token,scopes).ConfigureAwait(false);
                    }
                    catch(IOException error) when(!token.IsCancellationRequested&&
                        (Equals(error.Data["ErrorCode"],"CacheStorageLimit")||error.InnerException is SqliteException { SqliteErrorCode:13 }))
                    {
                        cachePersistenceError=Equals(error.Data["ErrorCode"],"CacheStorageLimit")?"CacheStorageLimit":"CacheStorageFull";
                        WorkflowDiagnostics.Failure("Audio.SelectedCheckpoint",error,key,partial:true,phase:"Saving selected clip");
                    }
                    catch(SqliteException error) when(!token.IsCancellationRequested&&error.SqliteErrorCode==13)
                    {
                        cachePersistenceError="CacheStorageFull";
                        WorkflowDiagnostics.Failure("Audio.SelectedCheckpoint",error,key,partial:true,phase:"Saving selected clip");
                    }
                }
            }
            finally { featureGate.Release(); }
        }
        token.ThrowIfCancellationRequested();
        serialized=BoundedFeatureWrite.Create(RecoveryFeaturePrefix+key,RecoveryVersion,
            new AudioRecoveryCheckpoint(new(Snapshot.IndexContentId,Snapshot.PhysicalSourceStamp),media,label)
                {SelectedScope=scope},MaximumRecoveryPayloadBytes).Utf8ByteCount;
        AudioExplorerRow prepared;
        lock(catalogGate)
        {
            token.ThrowIfCancellationRequested();
            labelsByIdentity[key]=label;
            prepared=new(Describe(media,label),media,aliases.Get(stored.Asset.Identity),UserBankLabel(media))
                { SelectedSourceVerified=true,SelectedClassificationScope=scope,CachePersistenceErrorCode=cachePersistenceError };
            RememberBrowseRow(prepared,serialized);
        }
        return (prepared,serialized);
    }

    private static bool HasSelectedClassification(MediaEntry media,SelectedAudioClassificationScope? scope)
    {
        if(media.Completeness is MediaCompleteness.Complete or MediaCompleteness.Fragment||media.SourceDeclarationProcessingComplete)
            return true;
        return scope switch
        {
            SelectedAudioClassificationScope.Standalone=>media.Identity?.MemberPath=="wem",
            SelectedAudioClassificationScope.Midi=>media.Identity?.MemberPath=="midi"&&media.Midi is not null,
            _=>false
        };
    }

    private ArchiveSourceSession GetSelectedSourceSession(CancellationToken token)
    {
        lock(disposeGate)
        {
            token.ThrowIfCancellationRequested();ThrowIfDisposed();
            selectedSourceSession??=ArchiveSourceSession.TryAcquire(Snapshot)??ArchiveSourceSession.Acquire(Snapshot,
                (bytes,reason)=>selectedLookupMemory.Reserve(bytes,reason));
            return selectedSourceSession.Session;
        }
    }

    private async ValueTask DisposeSelectedMediaSourcesAsync()
    {
        ArchiveSourceSession.Lease? source;
        lock(disposeGate){source=selectedSourceSession;selectedSourceSession=null;}
        if(source is not null)await source.DisposeAsync().ConfigureAwait(false);
    }
}
