using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Formats.Localization.Model;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class DiscoverySortTests
{
    [Fact]
    public async Task AudioSortPagesTheFullResultAndAliasKeepsConcreteIdentity()
    {
        using var fixture=new DiscoveryFixture();var cache=new CacheManager(System.IO.Path.Combine(fixture.Root,"settings"),new(fixture.Cache));
        await using var session=await AudioExplorerSession.OpenAsync(fixture.Index,cache,new AliasStore(fixture.Library));
        Assert.True(session.Catalog.Media.Count==220,$"Audio fixture yielded {session.Catalog.Media.Count} media; {string.Join(';',session.Diagnostics.Select(item=>item.Message))}");
        var rows=new List<AudioExplorerRow>();for(var offset=0;offset<220;offset+=100){var page=await session.QueryAsync(new(Sort:AssetSort.Duration,Descending:true,Offset:offset));Assert.Equal(220,page.Total);rows.AddRange(page.Rows);}
        Assert.Equal(220,rows.Count);Assert.Equal(220,rows.Select(row=>row.Identity).Distinct().Count());
        Assert.Equal(Enumerable.Range(0,220).Reverse().Select(value=>(long)value),rows.Select(row=>row.Identity.Ordinal));
        var selected=rows[17].Identity;await session.SetAliasAsync(selected,"AAA renamed");
        var first=Assert.Single((await session.QueryAsync(new(Sort:AssetSort.Name,Limit:1))).Rows);Assert.Equal(selected,first.Identity);Assert.Equal("AAA renamed",first.Label);
        Assert.Equal(selected,Assert.Single(session.ResolveSelection([selected])).Identity);
    }

    [Fact]
    public async Task StringKeySortSpansEveryPageAndSavedSortSurvivesStoreRestart()
    {
        using var fixture=new DiscoveryFixture();var snapshot=await new ArchiveReader().OpenAsync(fixture.Index);
        await using var session=await LocalizationSession.OpenAsync(snapshot,fixture.Cache);await session.EnrichmentTask;
        var records=new List<LocalizationRecord>();for(var offset=0;offset<440;offset+=100){var page=await session.QueryAsync(new(Sort:AssetSort.Name,Descending:true,Offset:offset));Assert.Equal(440,page.Total);records.AddRange(page.Records);}
        Assert.Equal(440,records.Count);Assert.Equal(440,records.Select(record=>record.Identity).Distinct().Count());
        Assert.Equal(Enumerable.Range(1,220).Reverse().SelectMany(value=>new[]{"Key "+value,"Key "+value}),records.Select(record=>record.Key));
        var library=new LibraryStore(fixture.Library);var workspace=await library.OpenWorkspaceAsync(fixture.Index,new(snapshot.IndexContentId,snapshot.PhysicalSourceStamp));
        await library.SaveFilterAsync(workspace.Id,"strings","Descending key",new Dictionary<string,string>{{"sort","Name"},{"descending","True"}});
        var restored=Assert.Single((await new LibraryStore(fixture.Library).ReadAsync()).Filters);Assert.Equal("Name",restored.Values["sort"]);Assert.Equal("True",restored.Values["descending"]);
    }
}
