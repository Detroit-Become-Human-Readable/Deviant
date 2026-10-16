using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureProjectionAliasTests
{
    private static TextureDiscoveryQuery AliasOrderQuery() => new(SortByName: true, NamedFirst: false);

    [Fact]
    public async Task AliasVersionsKeepOwnedCatalogAndRoleRelationships()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var baseline = fixture.Entry("Generated texture");
        var catalog = new ResourceIdentity(fixture.Snapshot.IndexContentId, 9);
        store.InitializeTextureProjections("relationships", [baseline]);
        var named = fixture.Named(baseline, 64, 32, "BC1", "Diffuse", "Generated context") with
        { CatalogKeys = [catalog.Key], MetadataIdentities = [fixture.Owner with { MemberPath = "member/0" }] };
        store.CommitTextureProjectionOwner("relationships", fixture.Checkpoint, [named]);
        store.SetTextureProjectionAlias("relationships", baseline.Identity, "First alias");
        long captured = store.CaptureTextureProjectionGeneration("relationships");
        store.SetTextureProjectionAlias("relationships", baseline.Identity, "Second alias");
        var filtered = store.QueryTextureProjections("relationships", captured,
            AliasOrderQuery() with { CatalogKey = catalog.Key, VerifiedRole = "Diffuse", HasVerifiedOwner = true });
        Assert.Equal(baseline.Identity, Assert.Single(filtered.Entries).Identity);
        Assert.Equal("First alias", filtered.Entries[0].Alias);
        Assert.Single(store.QueryTextureProjectionsByOwner("relationships", captured, fixture.Owner, 0, 10).Entries);
        Assert.Equal(1, store.TextureProjectionCounts("relationships", captured).MetadataRecords);
    }

    [Fact]
    public async Task SettingAliasPublishesANewHead()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var entry = fixture.Entry("Generated texture");
        long initial = store.InitializeTextureProjections("aliases", [entry]);

        store.SetTextureProjectionAlias("aliases", entry.Identity, "Saved name");

        long current = store.CaptureTextureProjectionGeneration("aliases");
        Assert.True(current > initial);
        Assert.Equal("Saved name", Assert.Single(store.QueryTextureProjections("aliases", current, AliasOrderQuery()).Entries).Alias);
    }

    [Fact]
    public async Task AliasEditsLeaveAnEarlierGenerationOrderSearchAndAliasesUnchanged()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var first = fixture.Entry("Texture A");
        var second = fixture.Entry("Texture B", 2);
        store.InitializeTextureProjections("history", [first, second]);
        long before = store.CaptureTextureProjectionGeneration("history");

        store.SetTextureProjectionAlias("history", second.Identity, "A saved label");
        long after = store.CaptureTextureProjectionGeneration("history");

        var oldPage = store.QueryTextureProjections("history", before, AliasOrderQuery());
        Assert.Equal(new[] { first.Identity, second.Identity }, oldPage.Entries.Select(item => item.Identity).ToArray());
        Assert.All(oldPage.Entries, item => Assert.Null(item.Alias));
        Assert.Empty(store.QueryTextureProjections("history", before, AliasOrderQuery() with { Search = "saved label" }).Entries);

        var newPage = store.QueryTextureProjections("history", after, AliasOrderQuery());
        Assert.Equal(new[] { second.Identity, first.Identity }, newPage.Entries.Select(item => item.Identity).ToArray());
        Assert.Equal("A saved label", newPage.Entries[0].Alias);
        Assert.Equal(second.Identity, Assert.Single(store.QueryTextureProjections("history", after,
            AliasOrderQuery() with { Search = "saved label" }).Entries).Identity);
    }

    [Fact]
    public async Task DeletingAliasPublishesAndKeepsTheOldAliasedValueQueryable()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var entry = fixture.Entry("Generated texture");
        store.InitializeTextureProjections("delete", [entry]);
        store.SetTextureProjectionAlias("delete", entry.Identity, "Retained label");
        long aliased = store.CaptureTextureProjectionGeneration("delete");

        store.SetTextureProjectionAlias("delete", entry.Identity, null);

        long deleted = store.CaptureTextureProjectionGeneration("delete");
        Assert.True(deleted > aliased);
        Assert.Equal("Retained label", Assert.Single(store.QueryTextureProjections("delete", aliased, AliasOrderQuery()).Entries).Alias);
        Assert.Null(Assert.Single(store.QueryTextureProjections("delete", deleted, AliasOrderQuery()).Entries).Alias);
    }

    [Fact]
    public async Task ReplacementIsAtomicAndNoOpSetOrReplacementDoesNotAdvanceTheHead()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var first = fixture.Entry("Texture A");
        var second = fixture.Entry("Texture B", 2);
        store.InitializeTextureProjections("replace", [first, second]);

        store.SetTextureProjectionAlias("replace", first.Identity, "First label");
        long firstHead = store.CaptureTextureProjectionGeneration("replace");
        store.SetTextureProjectionAlias("replace", first.Identity, "First label");
        Assert.Equal(firstHead, store.CaptureTextureProjectionGeneration("replace"));

        store.ReplaceTextureProjectionAliases("replace", new Dictionary<ResourceIdentity, string>
        {
            [second.Identity] = "Second label"
        });
        long replaced = store.CaptureTextureProjectionGeneration("replace");
        Assert.True(replaced > firstHead);
        Assert.Empty(store.QueryTextureProjections("replace", replaced,
            AliasOrderQuery() with { Search = "First label" }).Entries);
        Assert.Equal(second.Identity, Assert.Single(store.QueryTextureProjections("replace", replaced,
            AliasOrderQuery() with { Search = "Second label" }).Entries).Identity);

        store.ReplaceTextureProjectionAliases("replace", new Dictionary<ResourceIdentity, string>
        {
            [second.Identity] = "Second label"
        });
        Assert.Equal(replaced, store.CaptureTextureProjectionGeneration("replace"));
    }

    [Fact]
    public async Task AliasCountsAreSpecificToTheRequestedGeneration()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var entry = fixture.Entry("Generated texture");
        store.InitializeTextureProjections("counts", [entry]);
        long initial = store.CaptureTextureProjectionGeneration("counts");
        store.SetTextureProjectionAlias("counts", entry.Identity, "Counted label");
        long aliased = store.CaptureTextureProjectionGeneration("counts");

        Assert.Equal(0, store.TextureProjectionCounts("counts", initial).Aliased);
        Assert.Equal(1, store.TextureProjectionCounts("counts", aliased).Aliased);

        store.SetTextureProjectionAlias("counts", entry.Identity, null);
        long deleted = store.CaptureTextureProjectionGeneration("counts");
        Assert.Equal(1, store.TextureProjectionCounts("counts", aliased).Aliased);
        Assert.Equal(0, store.TextureProjectionCounts("counts", deleted).Aliased);
    }

    [Fact]
    public async Task NamedEvidenceAndDirectCatalogMembershipRemainAvailableAfterAliasChanges()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var baseline = fixture.Entry("Generated texture");
        store.InitializeTextureProjections("details", [baseline]);
        var named = fixture.Named(baseline, 64, 32, "BC1", "Diffuse", "Generated context");
        store.CommitTextureProjectionOwner("details", fixture.Checkpoint, [named]);
        var catalog = new ResourceIdentity(fixture.Snapshot.IndexContentId, 9);
        long withCatalog = store.CommitTextureProjectionCatalog("details", fixture.Revision, catalog,
            new string('a', 64), [baseline.Identity]);

        store.SetTextureProjectionAlias("details", baseline.Identity, "User label");
        long aliased = store.CaptureTextureProjectionGeneration("details");

        var catalogPage = store.QueryTextureProjections("details", aliased,
            AliasOrderQuery() with { CatalogKey = catalog.Key });
        Assert.Equal(baseline.Identity, Assert.Single(catalogPage.Entries).Identity);
        Assert.Contains(catalog.Key, catalogPage.Entries[0].CatalogKeys);
        var details = Assert.IsType<TextureDiscoveryEntry>(store.LoadTextureProjectionDetails("details", aliased, baseline.Identity));
        Assert.Equal("Generated texture", details.Label);
        Assert.Equal("Diffuse", Assert.Single(details.NameCandidates).Role);
        Assert.Equal(fixture.Evidence, Assert.Single(details.NameCandidates[0].Evidence));
        Assert.True(aliased > withCatalog);
    }

    [Fact]
    public async Task PreCancelledAliasUpdatesPreserveHeadAndPublishedData()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var first = fixture.Entry("Texture A");
        var second = fixture.Entry("Texture B", 2);
        store.InitializeTextureProjections("cancel", [first, second]);
        store.SetTextureProjectionAlias("cancel", first.Identity, "Existing label");
        long head = store.CaptureTextureProjectionGeneration("cancel");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => store.SetTextureProjectionAlias("cancel", second.Identity,
            "Cancelled label", cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => store.ReplaceTextureProjectionAliases("cancel",
            new Dictionary<ResourceIdentity, string> { [second.Identity] = "Cancelled replacement" }, cancelled.Token));

        Assert.Equal(head, store.CaptureTextureProjectionGeneration("cancel"));
        var page = store.QueryTextureProjections("cancel", head, AliasOrderQuery());
        Assert.Equal("Existing label", Assert.Single(page.Entries, item => item.Identity == first.Identity).Alias);
        Assert.Null(Assert.Single(page.Entries, item => item.Identity == second.Identity).Alias);
    }
}
