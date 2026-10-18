using System.Collections.Concurrent;
using Deviant.Assets;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Audio;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class AudioCheckpointRecoveryTests
{
    private static readonly AudioToolPaths NoTools=new(null,null,null,null,null,null,null);

    [Fact]
    public async Task InterruptedNativeIndexingKeepsPublishedNamesBrowsableAfterReopenWithoutFinalSnapshot()
    {
        using var fixture=FormatsFixtureBuilder.CreateUnreferencedEmbeddedBank();
        using var workspace=new Workspace();
        using var release=new ManualResetEventSlim();
        var published=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held=0;
        var progress=new RecordingProgress(value=>
        {
            if(value.Phase!="Reading banks"||value.Completed<1||Interlocked.Exchange(ref held,1)!=0)return;
            published.TrySetResult();release.Wait();
        });
        ArchiveSnapshot snapshot;
        AssetDescriptor[] expected;
        await using(var cold=await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,workspace.Cache,
            workspace.Aliases,NoTools,progress,CancellationToken.None,startEnrichment:false))
        {
            snapshot=cold.Snapshot;
            var indexing=cold.EnrichmentTask;
            try
            {
                await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var staged=await cold.QueryAsync(new(Limit:10));
                var row=Assert.Single(staged.Rows);
                Assert.False(uint.TryParse(row.Label,out _));
                Assert.True(row.Media.MetadataReadCoverageComplete);
                Assert.NotEmpty(row.Media.MetadataReadDigests);
                Assert.True(row.Media.Slice.Length>0);
                await new AudioMetadataVerifier(snapshot).VerifyMediaAsync(row.Media);
                var binding=AudioResourceBinding.Resolve(snapshot,row.Media,original:true);
                Assert.Equal(row.Media.Slice,binding.Extent);
                expected=staged.Rows.Select(item=>item.Descriptor).ToArray();
                Assert.False(cold.IsEnrichmentComplete);
                Assert.True(cold.CancelEnrichment());
            }
            finally{release.Set();}
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>indexing);
        }

        Assert.DoesNotContain(progress.Items,value=>value.Phase is "Preparing audio library" or "Saving audio library");
        using(var retained=FeatureStore.Open(snapshot,workspace.Cache.Location))
        {
            Assert.Equal(expected.Length,retained.Count(new(Family:AudioExplorerSession.AssetFamily)));
            Assert.Equal(expected.Select(row=>row.Identity),retained.Query(new(Family:AudioExplorerSession.AssetFamily,Limit:10))
                .Select(row=>row.Identity));
        }
        var database=Assert.Single(Directory.GetFiles(workspace.Cache.Location,"catalog-*.sqlite3"));
        using(var connection=new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource=database,Mode=SqliteOpenMode.ReadOnly,Pooling=false }.ToString()))
        {
            connection.Open();using var command=connection.CreateCommand();
            command.CommandText="SELECT COUNT(*) FROM asset_features WHERE feature LIKE 'audio.media/item/%' OR "+
                "(json_type(payload,'$.IndexedResourceCount') IS NOT NULL AND json_type(payload,'$.SourcePath') IS NOT NULL)";
            Assert.Equal(0L,(long)command.ExecuteScalar()!);
        }

        await using var reopened=await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,workspace.Cache,
            workspace.Aliases,NoTools,null,CancellationToken.None,startEnrichment:false);
        Assert.False(reopened.IsEnrichmentStarted);
        var restored=await reopened.QueryAsync(new(Limit:10));
        using(var persisted=FeatureStore.Open(reopened.Snapshot,workspace.Cache.Location))
        {
            Assert.Equal(expected.Length,persisted.Count(new(Family:AudioExplorerSession.AssetFamily)));
        }
        Assert.False(reopened.IsEnrichmentStarted);
        Assert.False(reopened.IsEnrichmentComplete);
        Assert.Equal(expected.Length,restored.Total);
        Assert.Equal(expected.Length,reopened.MediaCount);
        Assert.Equal(expected.Select(row=>row.Identity),restored.Rows.Select(row=>row.Identity));
        Assert.Equal(expected.Select(row=>row.Label),restored.Rows.Select(row=>row.Descriptor.Label));
    }

    [Fact]
    public async Task LegacyDescriptorWithoutRecoveryPayloadIsPreservedAndRemainsUnresolved()
    {
        using var fixture=FormatsFixtureBuilder.CreateUnreferencedEmbeddedBank();
        using var workspace=new Workspace();
        ArchiveSnapshot snapshot;
        await using(var seed=await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,workspace.Cache,
            workspace.Aliases,NoTools,null,CancellationToken.None,startEnrichment:false))snapshot=seed.Snapshot;
        var identity=new ResourceIdentity(snapshot.IndexContentId,0,"media/00000001/0");
        var descriptor=new AssetDescriptor(identity,new(snapshot.IndexContentId,snapshot.PhysicalSourceStamp),
            AudioExplorerSession.AssetFamily,"Saved generated clip",null,AssetAvailability.Available,
            AssetCompleteness.Complete,AssetCapabilities.Preview|AssetCapabilities.Original,[],[]);
        using(var store=FeatureStore.Open(snapshot,workspace.Cache.Location))store.UpsertAssets([descriptor]);

        await using var reopened=await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,workspace.Cache,
            workspace.Aliases,NoTools,null,CancellationToken.None,startEnrichment:false);
        var error=await Assert.ThrowsAsync<ArchiveException>(()=>reopened.QueryAsync(new(Limit:10)));
        Assert.Equal("AudioRecoveryRequired",error.Code);
        Assert.False(reopened.IsEnrichmentStarted);
        Assert.False(reopened.IsEnrichmentComplete);
        Assert.Equal(1,reopened.MediaCount);
        Assert.Empty(reopened.Catalog.Media);
        using var retained=FeatureStore.Open(snapshot,workspace.Cache.Location);
        var saved=Assert.Single(retained.Query(new(Family:AudioExplorerSession.AssetFamily,Limit:10)));
        Assert.Equal(descriptor.Identity,saved.Identity);
        Assert.Equal(descriptor.Label,saved.Label);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryVerificationCancellationReleasesPinsAndSessionDisposalDrainsTheRead(bool disposeSession)
    {
        using var fixture=FormatsFixtureBuilder.CreateUnreferencedEmbeddedBank();
        using var workspace=new Workspace();
        await SaveInterruptedCheckpointAsync(fixture,workspace);
        var session=await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,workspace.Cache,
            workspace.Aliases,NoTools,null,CancellationToken.None,startEnrichment:false);
        ResourceIdentity identity;
        using(var store=FeatureStore.Open(session.Snapshot,workspace.Cache.Location))
            identity=Assert.Single(store.Query(new(Family:AudioExplorerSession.AssetFamily,Limit:10))).Identity;
        using var barrier=new VerificationBarrier(session.Snapshot.PhysicalSourceStamp);
        using var cancellation=new CancellationTokenSource();
        var querying=Task.Run(()=>session.QueryAsync(new(Limit:10),cancellation.Token));
        Task? disposing=null;
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if(OperatingSystem.IsWindows())
                Assert.Throws<IOException>(()=>
                {
                    using var blocked=new FileStream(session.Snapshot.Volumes.Single().FilePath,
                        FileMode.Open,FileAccess.ReadWrite,FileShare.None);
                });
            if(disposeSession)
            {
                disposing=session.DisposeAsync().AsTask();
                Assert.False(disposing.IsCompleted);
                var completed=await Task.WhenAny(disposing,Task.Delay(TimeSpan.FromMilliseconds(100)));
                Assert.NotSame(disposing,completed);
            }
            else cancellation.Cancel();
            barrier.Release();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>querying).WaitAsync(TimeSpan.FromSeconds(10));
            if(disposing is not null)await disposing.WaitAsync(TimeSpan.FromSeconds(10));
            else Assert.Empty(session.ResolveKnownSelection([identity.Key]));
            if(OperatingSystem.IsWindows())
            {
                using var exclusive=new FileStream(session.Snapshot.Volumes.Single().FilePath,
                    FileMode.Open,FileAccess.ReadWrite,FileShare.None);
            }
        }
        finally
        {
            barrier.Release();
            try{await querying.WaitAsync(TimeSpan.FromSeconds(10));}catch{}
            await (disposing??session.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task SaveInterruptedCheckpointAsync(FormatsFixtureBuilder fixture,Workspace workspace)
    {
        using var release=new ManualResetEventSlim();
        var published=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held=0;
        var progress=new RecordingProgress(value=>
        {
            if(value.Phase!="Reading banks"||value.Completed<1||Interlocked.Exchange(ref held,1)!=0)return;
            published.TrySetResult();release.Wait();
        });
        await using var session=await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,workspace.Cache,
            workspace.Aliases,NoTools,progress,CancellationToken.None,startEnrichment:false);
        var indexing=session.EnrichmentTask;
        try
        {
            await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(session.CancelEnrichment());
        }
        finally{release.Set();}
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>indexing);
    }

    private sealed class VerificationBarrier:IObserver<KeyValuePair<string,object?>>,IDisposable
    {
        private readonly string expectedStamp;
        private readonly ManualResetEventSlim release=new();
        private readonly IDisposable subscription;
        private int held;
        public TaskCompletionSource Entered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public VerificationBarrier(string expectedStamp)
        {
            this.expectedStamp=expectedStamp;
            subscription=AssetLoadDiagnostics.Listener.Subscribe(this);
        }
        public void OnNext(KeyValuePair<string,object?> value)
        {
            if(value.Value is not AssetLoadMeasurement { Stage:"AudioRecoveryVerification",Key:var key }||
                key!=expectedStamp||Interlocked.Exchange(ref held,1)!=0)return;
            Entered.TrySetResult();release.Wait();
        }
        public void Release()=>release.Set();
        public void OnCompleted(){}
        public void OnError(Exception error){}
        public void Dispose(){subscription.Dispose();release.Set();release.Dispose();}
    }
    private sealed class RecordingProgress(Action<AudioExplorerProgress> callback):IProgress<AudioExplorerProgress>
    {
        public ConcurrentQueue<AudioExplorerProgress> Items { get; }=new();
        public void Report(AudioExplorerProgress value) { Items.Enqueue(value);callback(value); }
    }

    private sealed class Workspace:IDisposable
    {
        private readonly string root=Path.Combine(Path.GetTempPath(),"deviant-audio-checkpoint-"+Guid.NewGuid().ToString("N"));
        public CacheManager Cache { get; }
        public AliasStore Aliases { get; }
        public Workspace()
        {
            Cache=new(Path.Combine(root,"settings"),new CacheSettings(Path.Combine(root,"cache")));
            Aliases=new(Path.Combine(root,"library"));
        }
        public void Dispose() { Cache.Dispose();if(Directory.Exists(root))Directory.Delete(root,true); }
    }
}