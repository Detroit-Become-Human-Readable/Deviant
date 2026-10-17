using System.Reflection;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureProjectionRebuildTests
{
    [Fact]
    public async Task QuarantineRetainsARebuildRequestAcrossReopeningWithoutDiscardingTheOwner()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        const string profile = "durable-rebuild";
        var baseline = fixture.Entry("Generated texture");
        var named = fixture.Named(baseline, 32, 16, "BC1", "Diffuse", "Room");
        long request;
        using (var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache))
        {
            store.InitializeTextureProjections(profile, [baseline]);
            store.CommitTextureProjectionOwner(profile, fixture.Checkpoint, [named]);
            Corrupt(fixture, baseline.Identity);
            Assert.Single(store.QuarantineTextureProjectionEvidence(profile,
                new Dictionary<ResourceIdentity, TextureDiscoveryEntry> { [baseline.Identity] = baseline }, [baseline.Identity]).InvalidRows);
            var queued = Assert.Single(Pending(store, profile));
            Assert.Equal(fixture.Owner, queued.Identity); request = queued.RequestId;
            Assert.NotNull(store.LoadTextureProjectionOwner(profile, fixture.Owner));
        }
        using var reopened = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        Assert.Equal(request, Assert.Single(Pending(reopened, profile)).RequestId);
        Assert.Equal(TextureNameKind.Fallback, Assert.Single(reopened.QueryTextureProjections(profile,
            reopened.CaptureTextureProjectionGeneration(profile), new()).Entries).NameKind);
    }

    [Fact]
    public async Task AnOlderRebuildCannotClearARequestCreatedWhileItWasRunning()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        const string profile = "rebuild-ticket";
        var baseline = fixture.Entry("Generated texture");
        var named = fixture.Named(baseline, 32, 16, "BC1", "Diffuse", "Room");
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        store.InitializeTextureProjections(profile, [baseline]);
        store.CommitTextureProjectionOwner(profile, fixture.Checkpoint, [named]);
        var rows = new Dictionary<ResourceIdentity, TextureDiscoveryEntry> { [baseline.Identity] = baseline };
        Corrupt(fixture, baseline.Identity); store.QuarantineTextureProjectionEvidence(profile, rows, [baseline.Identity]);
        long first = Assert.Single(Pending(store, profile)).RequestId;
        store.CommitTextureProjectionOwner(profile, fixture.Checkpoint, [named]);
        Assert.Equal(first, Assert.Single(Pending(store, profile)).RequestId);
        using var writer = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        Corrupt(fixture, baseline.Identity); writer.QuarantineTextureProjectionEvidence(profile, rows, [baseline.Identity]);
        long second = Assert.Single(Pending(store, profile)).RequestId; Assert.True(second > first);
        Complete(store, profile, fixture.Checkpoint, named, first);
        Assert.Equal(second, Assert.Single(Pending(store, profile)).RequestId);
        Complete(store, profile, fixture.Checkpoint, named, second);
        Assert.Empty(Pending(store, profile));
    }

    [Fact]
    public async Task RelatedTexturesCannotHideThePublishingOwnerFromRecovery()
    {
        using var fixture = await GeneratedTextureProjectionFixture.CreateAsync();
        var baseline = fixture.Entry("Generated texture");
        var publisher = new ResourceIdentity(fixture.Snapshot.IndexContentId, 9000);
        var checkpoint = fixture.Checkpoint with { Identity = publisher };
        var named = fixture.Named(baseline, 32, 16, "BC1", "Diffuse", "Room") with
        {
            Owners = [],
            Related = Enumerable.Range(100, 132).Select(ordinal => new TextureDiscoveryLink(
                new(fixture.Snapshot.IndexContentId, ordinal), "Related", TextureRelationshipKind.RelatedTexture,
                true, true, [])).ToArray()
        };
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        store.InitializeTextureProjections("many-links", [baseline]);
        store.CommitTextureProjectionOwner("many-links", checkpoint, [named]);
        Corrupt(fixture, baseline.Identity);
        store.QuarantineTextureProjectionEvidence("many-links",
            new Dictionary<ResourceIdentity, TextureDiscoveryEntry> { [baseline.Identity] = baseline }, [baseline.Identity],
            canRebuildOwner: owner => owner == publisher);
        Assert.Equal(publisher, Assert.Single(Pending(store, "many-links")).Identity);
    }

    private static (ResourceIdentity Identity, long RequestId)[] Pending(FeatureStore store, string profile)
    {
        var method = typeof(FeatureStore).GetMethod("LoadTextureProjectionOwnerRebuilds"); Assert.NotNull(method);
        return ((System.Collections.IEnumerable)method.Invoke(store, [profile, CancellationToken.None])!).Cast<object>()
            .Select(item => ((ResourceIdentity)item.GetType().GetProperty("Identity")!.GetValue(item)!,
                (long)item.GetType().GetProperty("RequestId")!.GetValue(item)!)).ToArray();
    }
    private static void Complete(FeatureStore store, string profile, TextureProjectionOwnerCheckpoint checkpoint,
        TextureDiscoveryEntry entry, long request)
    {
        var method = typeof(FeatureStore).GetMethod("CommitTextureProjectionOwner")!;
        Assert.Equal(5, method.GetParameters().Length);
        method.Invoke(store, [profile, checkpoint, new[] { entry }, CancellationToken.None, request]);
    }
    private static void Corrupt(GeneratedTextureProjectionFixture fixture, ResourceIdentity identity)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Directory.GetFiles(fixture.Cache, "catalog-*.sqlite3").Single(), Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand();
        command.CommandText = "UPDATE texture_projection_name_proofs SET payload=x'00' WHERE identity=$identity";
        command.Parameters.AddWithValue("$identity", identity.Key); Assert.True(command.ExecuteNonQuery() > 0);
    }
}
