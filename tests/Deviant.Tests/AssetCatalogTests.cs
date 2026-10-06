using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Xunit;
namespace Deviant.Tests;

public sealed class AssetCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-assets-" + Guid.NewGuid().ToString("N"));
    public AssetCatalogTests() => Directory.CreateDirectory(root);
    private ArchiveSnapshot Snapshot(string revision) => new(Path.Combine(root,"source","archive.idx"), "content", 18, 0, [], 0, [], [], new(false,"",0,0,0,false), revision, []);
    [Fact] public void FiltersCombineAndPagingKeepsDuplicateNamesSeparate()
    {
        using var store = FeatureStore.Open(Snapshot("a"), Path.Combine(root, "cache"));
        var revision = new SourceRevision("content", "a");
        store.UpsertAssets(Enumerable.Range(0, 8).Select(i => new AssetDescriptor(new("content", i), revision, "Audio", "Voice", "bank", AssetAvailability.Available,
            AssetCompleteness.Unverified, AssetCapabilities.Preview | AssetCapabilities.Wav, [new("Clip " + i, NameOrigin.Stored, "bank")], [], "ENG", "Vorbis", i, 2)));
        var query = new AssetQuery("Clip", "bank", "ENG", "Vorbis", 2, 6, 2, AssetAvailability.Available, AssetCompleteness.Unverified, 1, 2);
        Assert.Equal(5, store.Count(query)); Assert.Equal(new long[] { 3, 4 }, store.Query(query).Select(a => a.Identity.Ordinal));
        Assert.Equal(0, store.Count(query with { GroupKey = "other" }));
        store.UpsertAssets([new(new("content", 99), revision, "Audio", "ГОЛОС", "bank", AssetAvailability.Available,
            AssetCompleteness.Unverified, AssetCapabilities.Preview, [], [])]);
        Assert.Equal(1, store.Count(new AssetQuery(Search: "голос")));
    }
    [Fact] public void ChangedPhysicalRevisionCannotReuseEnrichment()
    {
        var cache = Path.Combine(root, "cache");
        using (var old = FeatureStore.Open(Snapshot("a"), cache)) old.Save("audio", "1", new[] { "old" });
        using var next = FeatureStore.Open(Snapshot("b"), cache);
        Assert.Null(next.Load<string[]>("audio", "1")); next.Save("audio", "2", new[] { "new" });
        Assert.Null(next.Load<string[]>("audio", "1")); Assert.Equal(new[] { "new" }, next.Load<string[]>("audio", "2"));
    }
    [Fact] public void DuplicateArchiveIdsResolveAmbiguously()
    {
        var snapshot = Snapshot("a") with { Records = [new(0, 1, 1, 42, 0, 0, 1, 0, "one.dat"), new(1, 2, 1, 42, 0, 1, 1, 0, "one.dat")] };
        var resolver = new ArchiveResourceResolver(snapshot);
        Assert.Equal(ReferenceStatus.Ambiguous, resolver.Resolve(new(IdentifierNamespace.Archive, "42")).Status);
        Assert.Equal(ReferenceStatus.Missing, resolver.Resolve(new(IdentifierNamespace.Archive, "43")).Status);
        Assert.Equal(ReferenceStatus.Unresolved, resolver.Resolve(new(IdentifierNamespace.Hirc, "42")).Status);
    }
    [Fact] public void NaturalOrderingAndIndexedLiteralSearchSurviveAliasUpdates()
    {
        using var store=FeatureStore.Open(Snapshot("a"),Path.Combine(root,"cache"));
        AssetDescriptor Row(long id,string label)=>new(new("content",id),new("content","a"),"Audio",label,"bank",AssetAvailability.Available,AssetCompleteness.Complete,AssetCapabilities.Preview,[],[]);
        store.UpsertAssets([Row(10,"Clip 10"),Row(2,"Clip 2"),Row(1,"Clip 1"),Row(5,"100% \"héllo\" 街声")]);
        Assert.Equal(new long[]{1,2,10},store.Query(new(Search:"Clip")).Select(r=>r.Identity.Ordinal));
        Assert.Equal(1,store.Count(new(Search:"% \"HÉLLO\""))); Assert.Equal(1,store.Count(new(Search:"街")));
        store.UpsertAssets([Row(1,"New alias")]); Assert.Equal(2,store.Count(new(Search:"Clip"))); Assert.Equal(1,store.Count(new(Search:"New alias")));
        store.ReplaceAssets("Audio",[Row(1,"Only")]); Assert.Equal(0,store.Count(new(Search:"Clip")));
    }
    [Fact] public void CountInvalidatesAfterAnotherConnectionWrites()
    {
        var cache=Path.Combine(root,"cache");using var first=FeatureStore.Open(Snapshot("a"),cache);using var second=FeatureStore.Open(Snapshot("a"),cache);
        Assert.Equal(0,first.Count(new(Family:"Audio")));
        second.UpsertAssets([new(new("content",1),new("content","a"),"Audio","One",null,AssetAvailability.Available,AssetCompleteness.Complete,AssetCapabilities.None,[],[])]);
        Assert.Equal(1,first.Count(new(Family:"Audio")));
        second.ReplaceAssets("Audio",[]);Assert.Equal(0,first.Count(new(Family:"Audio")));
    }
    [Fact] public void UserSortAppliesAcrossPagesAndPreservesUnknownDurations()
    {
        using var store=FeatureStore.Open(Snapshot("a"),Path.Combine(root,"cache"));
        var rows=Enumerable.Range(0,137).Reverse().Select(i=>new AssetDescriptor(new("content",i),new("content","a"),"Audio","Clip "+i,"bank",AssetAvailability.Available,AssetCompleteness.Unverified,AssetCapabilities.Preview,[],[],i%2==0?"ENG":"FRE",i%3==0?"PCM":"Vorbis",i%11==0?null:i/10d,2)).ToArray();
        store.UpsertAssets(rows);
        var query=new AssetQuery(Family:"Audio",GroupKey:"bank",Sort:AssetSort.Duration,Descending:true,Limit:19);
        var actual=new List<AssetDescriptor>();
        for(var offset=0;offset<rows.Length;offset+=query.Limit)actual.AddRange(store.Query(query with{Offset=offset}));
        var expected=rows.OrderBy(row=>row.DurationSeconds is null).ThenByDescending(row=>row.DurationSeconds).ThenBy(row=>NaturalOrder.Key(row.Label),StringComparer.Ordinal).Select(row=>row.Identity);
        Assert.Equal(expected,actual.Select(row=>row.Identity));Assert.Equal(137,store.Count(query));
        Assert.Equal(137,store.Count(query with{Sort=AssetSort.Name,Descending=false}));
        Assert.Equal(new long[]{0,1,2,3,4},store.Query(query with{Sort=AssetSort.Name,Descending=false,Limit=5}).Select(row=>row.Identity.Ordinal));
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(()=>store.Query(query,cancelled.Token));
        Assert.Equal(137,store.Count(query));
    }
    [Fact] public void KeySortIgnoresLanguageFirstDefaultAndKeepsDuplicateOccurrences()
    {
        using var store=FeatureStore.Open(Snapshot("a"),Path.Combine(root,"cache"));
        AssetDescriptor Row(long ordinal,string language,string key)=>new(new("content",ordinal,"record/"+ordinal),new("content","a"),"strings",key,"source",AssetAvailability.Available,AssetCompleteness.Complete,AssetCapabilities.None,[],[],language,SortKey:language+"\0"+NaturalOrder.Key(key));
        store.UpsertAssets([Row(10,"ENG","Line 2"),Row(2,"ENG","Line 2"),Row(3,"FRE","Line 1"),Row(4,"ENG","Line 10")]);
        Assert.Equal(new long[]{3,2,10,4},store.Query(new(Family:"strings",Sort:AssetSort.Name)).Select(row=>row.Identity.Ordinal));
        Assert.Equal(new long[]{4,2,10,3},store.Query(new(Family:"strings",Sort:AssetSort.Name,Descending:true)).Select(row=>row.Identity.Ordinal));
        Assert.Throws<ArgumentOutOfRangeException>(()=>store.Query(new(Sort:(AssetSort)999)));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
