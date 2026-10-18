using System.Reflection;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Catalog;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Tests;

public sealed class CharacterProjectionLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedCharacterReadCannotReopenAConnectionAfterStoreDisposal(bool groups)
    {
        using var fixture=new ArchiveFixture();
        fixture.WriteIndex();
        var snapshot=await new ArchiveReader().OpenAsync(fixture.Index);
        var cacheRoot=Path.Combine(fixture.Root,"cache");
        var store=FeatureStore.Open(snapshot,cacheRoot);
        var fields=BindingFlags.Instance|BindingFlags.NonPublic;
        var readGate=typeof(FeatureStore).GetField("characterReadGate",fields)!.GetValue(store)!;
        var readConnection=typeof(FeatureStore).GetField("characterReadConnection",fields)!;
        try
        {
            await store.PrepareCharacterProjectionsAsync();
            Task queued;
            Monitor.Enter(readGate);
            try
            {
                var query=new AssetQuery(Family:"character-model",Limit:1);
                queued=groups?store.QueryCharacterGroupsAsync(query):store.QueryCharacterModelsAsync(query);
                Assert.False(queued.IsCompleted);
                store.Dispose();
            }
            finally{Monitor.Exit(readGate);}

            var readError=await Record.ExceptionAsync(()=>queued.WaitAsync(TimeSpan.FromSeconds(10)));
            var deleteError=Record.Exception(()=>Directory.Delete(cacheRoot,recursive:true));
            Assert.True(readError is ObjectDisposedException or OperationCanceledException,
                $"A queued character read must reject store disposal. Read outcome: {readError?.GetType().Name??"completed successfully"}; "+
                $"immediate cleanup: {deleteError?.GetType().Name??"succeeded"}.");
            Assert.Null(deleteError);
            Assert.False(Directory.Exists(cacheRoot));
        }
        finally
        {
            store.Dispose();
            // Release a reader leaked by a failing implementation only after recording the strict cleanup outcome.
            (readConnection.GetValue(store) as SqliteConnection)?.Dispose();
        }
    }
}