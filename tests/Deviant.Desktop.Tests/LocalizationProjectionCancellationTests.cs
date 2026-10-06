using System.Reflection;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Formats.Localization.Model;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class LocalizationProjectionCancellationTests
{
    [Fact]
    public async Task CanceledQueuedProjectionLeavesTypedPagesAndVariantsUsable()
    {
        using var fixture=new DiscoveryFixture(120);var snapshot=await new ArchiveReader().OpenAsync(fixture.Index);
        await using var session=await LocalizationSession.OpenAsync(snapshot,fixture.Cache);await session.EnrichmentTask;
        var gate=(SemaphoreSlim)typeof(LocalizationSession).GetField("featureGate",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(session)!;
        await gate.WaitAsync();
        try
        {
            using var cancellation=new CancellationTokenSource();
            var pending=session.QueryAsync(new LocalizationQuery(Sort:AssetSort.Name,Descending:true,Limit:100),cancellation.Token);
            Assert.False(pending.IsCompleted);cancellation.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        }
        finally{gate.Release();}
        var pages=new List<LocalizationRecord>();for(var offset=0;offset<240;offset+=100)
        {
            var page=await session.QueryAsync(new LocalizationQuery(Sort:AssetSort.Name,Descending:true,Offset:offset));Assert.Equal(240,page.Total);pages.AddRange(page.Records);
        }
        Assert.Equal(240,pages.Count);Assert.Equal(240,pages.Select(row=>row.Identity).Distinct().Count());
        Assert.All(pages,row=>{Assert.NotEmpty(row.FieldExtents);Assert.True(row.RecordExtent.Length>0);Assert.Equal(snapshot.IndexContentId,row.Identity.IndexContentId);});
        Assert.Equal(Enumerable.Range(1,120).Reverse().SelectMany(number=>new[]{"Key "+number,"Key "+number}),pages.Select(row=>row.Key));
        var variants=await session.GetVariantsAsync(pages[0].Identity);Assert.Equal(2,variants.Count);Assert.All(variants,row=>Assert.NotEmpty(row.FieldExtents));
    }
}
