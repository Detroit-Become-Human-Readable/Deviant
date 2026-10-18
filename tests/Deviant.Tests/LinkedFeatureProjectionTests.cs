using System.Reflection;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Deviant.Tests;

public sealed class LinkedFeatureProjectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-linked-features-" + Guid.NewGuid().ToString("N"));
    private readonly LinkedFeatureScope scope = new("generated-linked", "v1", "generated.media", "v1", "generated-audio");
    private SourceRevision Revision => new("generated-linked-index", "generated-linked-stamp");
    private FeatureStore Open() => FeatureStore.Open(Snapshot(), Path.Combine(root, "cache"));
    private ArchiveSnapshot Snapshot() => new(Path.Combine(root, "source", "generated.idx"), Revision.IndexContentId,
        IndexLayout.SupportedVersion, 0, [0x41], 0, [], [], new FileStamp(true, "index", 1, 1, 1, true), Revision.PhysicalSourceStamp, []);
    private ResourceIdentity Identity(int id) => new(Revision.IndexContentId, id);
    private AssetDescriptor Asset(int id) => new(Identity(id), Revision, scope.Family, "Generated " + id, null,
        AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.Original, [], [], SortKey: id.ToString("D12"));
    private string FeatureKey(int id) => scope.Collection + "/item/" + id.ToString("D12");
    private LinkedFeatureReference[] References(int count = 3) => Enumerable.Range(0, count).Select(id => new LinkedFeatureReference(Identity(id), FeatureKey(id))).ToArray();
    private void Seed(FeatureStore store, int count = 3)
    {
        store.SaveItems(scope.Collection, scope.CollectionVersion, Enumerable.Range(0, count).Select(id => new Payload(id, "Original " + id)));
        store.ReplaceAssets(scope.Family, Enumerable.Range(0, count).Select(Asset));
    }
    private long Publish(FeatureStore store, int count = 3) => store.PublishLinkedFeatureProjection(
        store.CaptureLinkedFeatureDependencies(scope), References(count), new Head("Generated summary", count))!.Value;

    [Fact]
    public void LegacyStoreHasNoLinkedHeadAndPublishedPagesKeepCountsOrderAndPayloads()
    {
        using var store = Open(); Seed(store, 140);
        Assert.Null(store.ReadLinkedFeatureHead<Head>(scope));
        Assert.Null(store.ReadLinkedFeaturePage<Head, Payload>(scope, new(Limit: 100)));
        long generation = Publish(store, 140);
        var head = Assert.IsType<LinkedFeatureHead<Head>>(store.ReadLinkedFeatureHead<Head>(scope));
        Assert.Equal(generation, head.Generation); Assert.Equal(140, head.Head.Count);
        var page = Assert.IsType<LinkedFeaturePage<Head, Payload>>(store.ReadLinkedFeaturePage<Head, Payload>(scope,
            new(Offset: 100, Limit: 28), generation));
        Assert.Equal(140, page.Total); Assert.Equal(generation, page.Generation);
        Assert.Equal(Enumerable.Range(100, 28), page.Rows.Select(row => row.Feature.Id));
        Assert.All(page.Rows, row => Assert.Equal(row.Feature.Id, row.Asset.Identity.Ordinal));
        using var reopened = Open();
        Assert.Equal(generation, reopened.ReadLinkedFeatureHead<Head>(scope)!.Generation);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadLinkedFeaturePage<Head, Payload>(scope, new(Limit: 129)));
        Assert.Throws<ArgumentException>(() => store.ReadLinkedFeaturePage<Head, Payload>(scope, new(Family: "other")));
    }

    [Theory]
    [InlineData("save")]
    [InlineData("bounded")]
    [InlineData("bounded-batch")]
    [InlineData("upsert-items")]
    [InlineData("replace-items")]
    [InlineData("asset")]
    [InlineData("bounded-assets")]
    [InlineData("empty-family")]
    [InlineData("move-family")]
    public void EverySupportedMutationInvalidatesDependentHeads(string mutation)
    {
        using var store = Open(); Seed(store); long generation = Publish(store);
        var otherScope = scope with { Key = "other-head", Family = "other-family" };
        var otherBefore = store.CaptureLinkedFeatureDependencies(otherScope);
        switch (mutation)
        {
            case "save": store.Save(FeatureKey(0), "v1", new Payload(0, "Changed")); break;
            case "bounded": store.SaveBounded(FeatureKey(0), "v1", new Payload(0, "Changed"), 1024); break;
            case "bounded-batch": store.SaveBoundedBatch([BoundedFeatureWrite.Create(FeatureKey(0), "v1", new Payload(0, "Changed"), 1024)]); break;
            case "upsert-items": store.UpsertItems(scope.Collection, "v1", new[] { new KeyValuePair<string, Payload>("000000000000", new(0, "Changed")) }); break;
            case "replace-items": store.SaveItems(scope.Collection, "v1", new[] { new Payload(2, "Replacement position zero") }); break;
            case "asset": store.UpsertAssets([Asset(0) with { Label = "Changed asset" }]); break;
            case "bounded-assets": store.PublishBoundedAssetsBatch([new(BoundedFeatureWrite.Create("unrelated-evidence", "v1", new Payload(0, "Evidence"), 1024), Asset(0) with { Label = "Changed bounded asset" })]); break;
            case "empty-family": store.ReplaceAssets(scope.Family, []); break;
            case "move-family": store.UpsertAssets([Asset(0) with { Family = otherScope.Family }]); break;
        }
        Assert.Null(store.ReadLinkedFeatureHead<Head>(scope));
        Assert.Null(store.ReadLinkedFeaturePage<Head, Payload>(scope, new()));
        var unavailable = Assert.Throws<ArchiveException>(() => store.ReadLinkedFeaturePage<Head, Payload>(scope, new(), generation));
        Assert.Equal("LinkedFeatureGenerationUnavailable", unavailable.Code);
        if (mutation == "move-family") Assert.True(store.CaptureLinkedFeatureDependencies(otherScope).FamilyEpoch > otherBefore.FamilyEpoch);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("upsert-items")]
    [InlineData("asset")]
    [InlineData("bounded")]
    public void IdenticalWritesLeaveTheHeadAndDependencyEpochsUnchanged(string mutation)
    {
        using var store = Open(); Seed(store); long generation = Publish(store);
        var before = store.CaptureLinkedFeatureDependencies(scope);
        switch (mutation)
        {
            case "save": store.Save(FeatureKey(0), "v1", new Payload(0, "Original 0")); break;
            case "upsert-items": store.UpsertItems(scope.Collection, "v1", new[] { new KeyValuePair<string, Payload>("000000000000", new(0, "Original 0")) }); break;
            case "asset": store.UpsertAssets([Asset(0)]); break;
            case "bounded": store.SaveBounded(FeatureKey(0), "v1", new Payload(0, "Original 0"), 1024); break;
        }
        Assert.Equal(before, store.CaptureLinkedFeatureDependencies(scope));
        Assert.Equal(generation, store.ReadLinkedFeatureHead<Head>(scope)!.Generation);
    }

    [Fact]
    public void AStaleCollectionEpochCannotPublishAfterPositionalReplacement()
    {
        using var store = Open(); Seed(store);
        var captured = store.CaptureLinkedFeatureDependencies(scope);
        store.SaveItems(scope.Collection, "v1", new[] { new Payload(2, "New position zero") });
        Assert.Null(store.PublishLinkedFeatureProjection(captured, References(), new Head("Stale", 3)));
        Assert.Null(store.ReadLinkedFeatureHead<Head>(scope));
    }

    [Fact]
    public void OwnAssetRebuildCanRefreshOnlyTheFamilyEpochWhenTheCollectionIsUnchanged()
    {
        using var store = Open(); Seed(store);
        var captured = store.CaptureLinkedFeatureDependencies(scope);
        store.UpsertAssets([Asset(0) with { Label = "Own rebuilt asset" }]);
        var current = store.CaptureLinkedFeatureDependencies(scope);
        Assert.Equal(captured.CollectionEpoch, current.CollectionEpoch);
        Assert.Null(store.PublishLinkedFeatureProjection(captured, References(), new Head("Stale family", 3)));
        Assert.NotNull(store.PublishLinkedFeatureProjection(captured with { FamilyEpoch = current.FamilyEpoch }, References(), new Head("Current family", 3)));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("outside")]
    [InlineData("duplicate")]
    [InlineData("absent-payload")]
    public void IncompleteOrMalformedMappingsCannotReplaceAValidPublication(string damage)
    {
        using var store = Open(); Seed(store); long generation = Publish(store);
        var references = References();
        references = damage switch
        {
            "missing" => references[..2],
            "outside" => [references[0] with { FeatureKey = "other/item/000000000000" }, references[1], references[2]],
            "duplicate" => [references[0], references[0], references[2]],
            _ => [references[0] with { FeatureKey = scope.Collection + "/item/missing" }, references[1], references[2]]
        };
        Assert.Throws<InvalidDataException>(() => store.PublishLinkedFeatureProjection(store.CaptureLinkedFeatureDependencies(scope), references, new Head("Invalid", 3)));
        Assert.Equal(generation, store.ReadLinkedFeatureHead<Head>(scope)!.Generation);
    }

    [Fact]
    public async Task OneReadTransactionKeepsTheOldPayloadWhenAnotherWriterInvalidatesTheHead()
    {
        using var store = Open(); Seed(store); long generation = Publish(store);
        using var writer = Open(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var observer = AssetLoadDiagnostics.Listener.Subscribe(new Observe(counter =>
        {
            if (counter.Stage != "LinkedFeatures.ReadSnapshot" || counter.Key != scope.Key) return;
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        }));
        var read = Task.Run(() => store.ReadLinkedFeaturePage<Head, Payload>(scope, new(Limit: 3)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            writer.SaveItems(scope.Collection, "v1", new[] { new Payload(99, "New positional value") });
        }
        finally { release.Set(); }
        var page = Assert.IsType<LinkedFeaturePage<Head, Payload>>(await read);
        Assert.Equal(generation, page.Generation); Assert.Equal("Original 0", page.Rows[0].Feature.Text);
        Assert.Null(store.ReadLinkedFeatureHead<Head>(scope));
    }

    [Fact]
    public async Task LinkedReadsDoNotWaitForTheWriterMonitorAndCannotReopenAfterDisposal()
    {
        var store = Open(); Seed(store); Publish(store);
        try
        {
            var writerGate = typeof(FeatureStore).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            using var releaseWriter = new ManualResetEventSlim();
            var writerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writer = Task.Run(() =>
            {
                lock (writerGate)
                {
                    writerEntered.SetResult();
                    Assert.True(releaseWriter.Wait(TimeSpan.FromSeconds(10)));
                }
            });
            try
            {
                await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var read = await Task.Run(() => store.ReadLinkedFeaturePage<Head, Payload>(scope, new(Limit: 3))).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(3, read!.Rows.Count);
            }
            finally { releaseWriter.Set(); await writer.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        finally { store.Dispose(); }
        Assert.Throws<ObjectDisposedException>(() => store.ReadLinkedFeatureHead<Head>(scope));
        Assert.Throws<ObjectDisposedException>(() => store.ReadLinkedFeaturePage<Head, Payload>(scope, new()));
    }

    [Fact]
    public void HeadOnlyReadDoesNotDeserializeTheLinkedPayloadAndMalformedPageFailsExplicitly()
    {
        using var store = Open(); Seed(store, 1);
        store.Save(FeatureKey(0), "v1", "Unexpected payload type"); Publish(store, 1);
        Assert.Equal(1, store.ReadLinkedFeatureHead<Head>(scope)!.Head.Count);
        Assert.Throws<InvalidDataException>(() => store.ReadLinkedFeaturePage<Head, Payload>(scope, new(Limit: 1)));
    }

    [Fact]
    public void CombinedSerializedJsonBytesAreBoundedBeforePageHydration()
    {
        using var store = Open();
        string text = new('Ω', 3 * 1024 * 1024);
        store.SaveItems(scope.Collection, "v1", new[] { new Payload(0, text), new Payload(1, text) });
        store.ReplaceAssets(scope.Family, new[] { Asset(0), Asset(1) }); Publish(store, 2);
        Assert.NotNull(store.ReadLinkedFeatureHead<Head>(scope));
        Assert.Throws<InvalidDataException>(() => store.ReadLinkedFeaturePage<Head, Payload>(scope, new(Limit: 2)));
    }

    [Fact]
    public void CancellationRollsBackPublicationAndDependencyInvalidation()
    {
        using var store = Open(); Seed(store); long generation = Publish(store);
        using (var cancellation = new CancellationTokenSource())
        {
            using var observer = AssetLoadDiagnostics.Listener.Subscribe(new Observe(counter =>
            { if (counter.Stage == "LinkedFeatures.PublishPrepared" && counter.Key == scope.Key) cancellation.Cancel(); }));
            Assert.ThrowsAny<OperationCanceledException>(() => store.PublishLinkedFeatureProjection(
                store.CaptureLinkedFeatureDependencies(scope), References(), new Head("Cancelled", 3), cancellation.Token));
        }
        Assert.Equal(generation, store.ReadLinkedFeatureHead<Head>(scope)!.Generation);
        using var cancelled = new CancellationTokenSource();
        IEnumerable<Payload> Replacement()
        { yield return new(9, "Inserted"); cancelled.Cancel(); yield return new(10, "Cancelled"); }
        Assert.ThrowsAny<OperationCanceledException>(() => store.SaveItems(scope.Collection, "v1", Replacement(), cancelled.Token));
        Assert.Equal(generation, store.ReadLinkedFeatureHead<Head>(scope)!.Generation);
        using var readCancellation = new CancellationTokenSource();
        using (var observer = AssetLoadDiagnostics.Listener.Subscribe(new Observe(counter =>
            { if (counter.Stage == "LinkedFeatures.ReadSnapshot" && counter.Key == scope.Key) readCancellation.Cancel(); })))
            Assert.ThrowsAny<OperationCanceledException>(() => store.ReadLinkedFeaturePage<Head, Payload>(scope, new(), token: readCancellation.Token));
        Assert.Equal(3, store.ReadLinkedFeaturePage<Head, Payload>(scope, new())!.Rows.Count);
    }

    [Fact]
    public void AtomicCollectionSaveReturnsAFenceThatRejectsALaterPositionalReorder()
    {
        using var store = Open(); Seed(store);
        var saved = store.SaveLinkedFeatureItems(scope, Enumerable.Range(0, 3).Select(id => new Payload(id, "Saved " + id)));
        Assert.True(saved.CollectionEpoch > 0);
        Assert.Equal(saved, store.CaptureLinkedFeatureDependencies(scope));
        using var other = Open();
        other.SaveItems(scope.Collection, scope.CollectionVersion, Enumerable.Range(0, 3).Reverse().Select(id => new Payload(id, "Reordered " + id)));
        Assert.True(other.CaptureLinkedFeatureDependencies(scope).CollectionEpoch > saved.CollectionEpoch);
        Assert.Null(store.PublishLinkedFeatureProjection(saved, References(), new Head("Stale positions", 3)));
        Assert.Null(store.ReadLinkedFeatureHead<Head>(scope));
    }

    [Fact]
    public void AtomicCollectionSaveRollsBackRowsAndEpochsWhenCancelled()
    {
        using var store = Open(); Seed(store); long generation = Publish(store);
        var before = store.CaptureLinkedFeatureDependencies(scope);
        using var cancellation = new CancellationTokenSource();
        IEnumerable<Payload> Replacement()
        { yield return new(99, "Inserted"); cancellation.Cancel(); yield return new(98, "Cancelled"); }
        Assert.ThrowsAny<OperationCanceledException>(() => store.SaveLinkedFeatureItems(scope, Replacement(), cancellation.Token));
        Assert.Equal(before, store.CaptureLinkedFeatureDependencies(scope));
        Assert.Equal(generation, store.ReadLinkedFeatureHead<Head>(scope)!.Generation);
        Assert.Equal(Enumerable.Range(0, 3), store.ReadLinkedFeaturePage<Head, Payload>(scope, new())!.Rows.Select(row => row.Feature.Id));
    }

    [Fact]
    public void EachRowReportsItsActualSerializedBytesWithoutChargingTheHead()
    {
        using var store = Open(); Seed(store);
        var dependencies = store.CaptureLinkedFeatureDependencies(scope);
        store.PublishLinkedFeatureProjection(dependencies, References(), new Head(new string('x', 8192), 3));
        var page = store.ReadLinkedFeaturePage<Head, Payload>(scope, new())!;
        var connection = (SqliteConnection)typeof(FeatureStore).GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        foreach (var row in page.Rows)
        {
            using var expected = connection.CreateCommand();
            expected.CommandText = "SELECT length(CAST(a.payload AS BLOB))+length(CAST(f.payload AS BLOB)) " +
                "FROM assets_v2 a JOIN asset_features f ON f.revision=a.revision " +
                "WHERE a.revision=$revision AND a.identity=$identity AND f.feature=$feature";
            expected.Parameters.AddWithValue("$revision", dependencies.Revision);
            expected.Parameters.AddWithValue("$identity", row.Asset.Identity.Key);
            expected.Parameters.AddWithValue("$feature", FeatureKey(row.Feature.Id));
            Assert.Equal(Convert.ToInt64(expected.ExecuteScalar()), row.SerializedBytes);
            Assert.InRange(row.SerializedBytes, 1, 8191);
        }
    }
    [Theory]
    [InlineData("insert")]
    [InlineData("update")]
    [InlineData("delete")]
    public void ExactMetadataFeaturesInvalidateHeadsForEveryMutationKind(string mutation)
    {
        using var store = Open(); Seed(store);
        const string metadataKey = "generated.snapshot-head";
        var tracked = scope with { AdditionalFeatures = [metadataKey, "generated.dialogue-envelope"] };
        if (mutation != "insert") store.Save(metadataKey, "v1", new Payload(9, "Original metadata"));
        var before = store.CaptureLinkedFeatureDependencies(tracked);
        store.PublishLinkedFeatureProjection(before, References(), new Head("Tracked", 3));
        if (mutation == "insert") store.Save(metadataKey, "v1", new Payload(9, "Inserted metadata"));
        else
        {
            var connection = Connection(store);
            using var change = connection.CreateCommand();
            change.CommandText = mutation == "delete" ? "DELETE FROM asset_features WHERE revision=$revision AND feature=$feature" :
                "UPDATE asset_features SET payload=$payload WHERE revision=$revision AND feature=$feature";
            change.Parameters.AddWithValue("$revision", before.Revision); change.Parameters.AddWithValue("$feature", metadataKey);
            if (mutation == "update") change.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new Payload(9, "Updated metadata")));
            Assert.Equal(1, change.ExecuteNonQuery());
        }
        Assert.True(store.CaptureLinkedFeatureDependencies(tracked).AdditionalEpoch > before.AdditionalEpoch);
        Assert.Null(store.ReadLinkedFeatureHead<Head>(tracked));
    }

    [Fact]
    public void MetadataDependenciesAreExactCanonicalAndIgnoreUnchangedOrUnrelatedFeatures()
    {
        using var store = Open(); Seed(store);
        const string first = "generated.snapshot-head", second = "generated.dialogue-envelope";
        store.Save(first, "v1", new Payload(1, "Metadata"));
        var tracked = scope with { AdditionalFeatures = [second, first, second] };
        var dependencies = store.CaptureLinkedFeatureDependencies(tracked);
        long generation = store.PublishLinkedFeatureProjection(dependencies, References(), new Head("Tracked", 3))!.Value;
        store.Save(first, "v1", new Payload(1, "Metadata"));
        store.Save(first + "/unrelated", "v1", new Payload(2, "Unrelated metadata"));
        store.Save("texture.state", "v1", new Payload(3, "Unrelated family data"));
        Assert.Equal(dependencies, store.CaptureLinkedFeatureDependencies(tracked));
        Assert.Equal(generation, store.ReadLinkedFeatureHead<Head>(tracked with { AdditionalFeatures = [first, second] })!.Generation);
        Assert.Null(store.ReadLinkedFeatureHead<Head>(tracked with { AdditionalFeatures = [first, "different.exact-key"] }));
        store.Save(first, "v2", new Payload(1, "Metadata"));
        Assert.Null(store.ReadLinkedFeatureHead<Head>(tracked));
    }

    [Fact]
    public void FinalCheckpointFenceCanBeCombinedWithMediaFenceAndRejectsALaterMetadataWrite()
    {
        using var store = Open(); Seed(store);
        const string snapshotKey = "generated.snapshot-head", checkpointKey = "generated.dialogue-envelope";
        var tracked = scope with { AdditionalFeatures = [snapshotKey, checkpointKey] };
        var media = store.SaveLinkedFeatureItems(tracked, Enumerable.Range(0, 3).Select(id => new Payload(id, "Saved " + id)));
        store.Save(snapshotKey, "v1", new Payload(10, "Saved snapshot"));
        var checkpoint = store.SaveLinkedFeatureCheckpoint(tracked,
            BoundedFeatureWrite.Create(checkpointKey, "v1", new Payload(11, "Final envelope"), 1024));
        Assert.True(checkpoint.AdditionalEpoch > media.AdditionalEpoch);
        var combined = media with { AdditionalEpoch = checkpoint.AdditionalEpoch };
        Assert.NotNull(store.PublishLinkedFeatureProjection(combined, References(), new Head("Complete", 3)));
        using var writer = Open(); writer.Save(snapshotKey, "v2", new Payload(12, "Replacement snapshot"));
        Assert.Null(store.PublishLinkedFeatureProjection(checkpoint, References(), new Head("Stale checkpoint", 3)));
        Assert.Null(store.ReadLinkedFeatureHead<Head>(tracked));
    }

    [Fact]
    public void FencedCheckpointRejectsAnEarlierSnapshotWriteWithoutOverwritingIt()
    {
        using var store = Open(); Seed(store);
        const string snapshotKey = "generated.snapshot-head";
        var tracked = scope with { AdditionalFeatures = [snapshotKey] };
        store.Save(snapshotKey, "v1", new Payload(1, "Original snapshot"));
        var expected = store.CaptureLinkedFeatureDependencies(tracked);
        using var writer = Open(); writer.Save(snapshotKey, "v1", new Payload(2, "Other writer snapshot"));
        var current = store.CaptureLinkedFeatureDependencies(tracked);
        long generation = store.PublishLinkedFeatureProjection(current, References(), new Head("Current", 3))!.Value;

        Assert.Null(store.TrySaveLinkedFeatureCheckpoint(expected,
            BoundedFeatureWrite.Create(snapshotKey, "v1", new Payload(3, "Rejected snapshot"), 1024)));
        Assert.Equal(new Payload(2, "Other writer snapshot"), store.Load<Payload>(snapshotKey, "v1"));
        Assert.Equal(current, store.CaptureLinkedFeatureDependencies(tracked));
        Assert.Equal(generation, store.ReadLinkedFeatureHead<Head>(tracked)!.Generation);
    }

    [Fact]
    public void OwnSnapshotCheckpointDoesNotBlessAWriteBeforeTheDialogueCheckpoint()
    {
        using var store = Open(); Seed(store);
        const string snapshotKey = "generated.snapshot-head", dialogueKey = "generated.dialogue-envelope";
        var tracked = scope with { AdditionalFeatures = [snapshotKey, dialogueKey] };
        store.Save(dialogueKey, "v1", new Payload(1, "Original dialogue"));
        var before = store.CaptureLinkedFeatureDependencies(tracked);
        var snapshot = Assert.IsType<LinkedFeatureDependencies>(store.TrySaveLinkedFeatureCheckpoint(before,
            BoundedFeatureWrite.Create(snapshotKey, "v1", new Payload(2, "Own snapshot"), 1024)));
        Assert.Equal(before.CollectionEpoch, snapshot.CollectionEpoch);
        Assert.Equal(before.FamilyEpoch, snapshot.FamilyEpoch);
        Assert.True(snapshot.AdditionalEpoch > before.AdditionalEpoch);
        using var writer = Open(); writer.Save(snapshotKey, "v1", new Payload(3, "Later snapshot"));
        var current = store.CaptureLinkedFeatureDependencies(tracked);

        Assert.Null(store.TrySaveLinkedFeatureCheckpoint(snapshot,
            BoundedFeatureWrite.Create(dialogueKey, "v1", new Payload(4, "Rejected dialogue"), 1024)));
        Assert.Equal(new Payload(1, "Original dialogue"), store.Load<Payload>(dialogueKey, "v1"));
        Assert.Equal(current, store.CaptureLinkedFeatureDependencies(tracked));
    }

    [Fact]
    public void FencedFamilyReplacementReturnsOnlyItsOwnAdvancedFamilyEpoch()
    {
        using var store = Open(); Seed(store);
        const string snapshotKey = "generated.snapshot-head";
        var tracked = scope with { AdditionalFeatures = [snapshotKey] };
        store.Save(snapshotKey, "v1", new Payload(1, "Snapshot"));
        var before = store.CaptureLinkedFeatureDependencies(tracked);
        var replaced = Assert.IsType<LinkedFeatureDependencies>(store.ReplaceLinkedFeatureAssets(before,
            Enumerable.Range(0, 3).Select(id => Asset(id) with { Label = "Rebuilt " + id })));

        Assert.True(replaced.FamilyEpoch > before.FamilyEpoch);
        Assert.Equal(before.CollectionEpoch, replaced.CollectionEpoch);
        Assert.Equal(before.AdditionalEpoch, replaced.AdditionalEpoch);
        Assert.Equal(replaced, store.CaptureLinkedFeatureDependencies(tracked));
        long generation = store.PublishLinkedFeatureProjection(replaced, References(), new Head("Rebuilt", 3))!.Value;
        var page = store.ReadLinkedFeaturePage<Head, Payload>(tracked, new(Limit: 3), generation)!;
        Assert.Equal(new[] { "Rebuilt 0", "Rebuilt 1", "Rebuilt 2" }, page.Rows.Select(row => row.Asset.Label));
    }

    [Theory]
    [InlineData("family")]
    [InlineData("collection")]
    [InlineData("snapshot")]
    public void StaleFamilyReplacementDoesNotEnumerateOrChangeTheCurrentPublication(string mutation)
    {
        using var store = Open(); Seed(store);
        const string snapshotKey = "generated.snapshot-head";
        var tracked = scope with { AdditionalFeatures = [snapshotKey] };
        var expected = store.CaptureLinkedFeatureDependencies(tracked);
        using var writer = Open();
        switch (mutation)
        {
            case "family": writer.UpsertAssets([Asset(0) with { Label = "Other writer asset" }]); break;
            case "collection": writer.Save(FeatureKey(0), "v1", new Payload(0, "Other writer payload")); break;
            case "snapshot": writer.Save(snapshotKey, "v1", new Payload(1, "Other writer snapshot")); break;
        }
        var current = store.CaptureLinkedFeatureDependencies(tracked);
        long generation = store.PublishLinkedFeatureProjection(current, References(), new Head("Current", 3))!.Value;
        var before = store.ReadLinkedFeaturePage<Head, Payload>(tracked, new(Limit: 3), generation)!;
        bool enumerated = false;
        IEnumerable<AssetDescriptor> Replacement()
        { enumerated = true; yield return Asset(9); }

        Assert.Null(store.ReplaceLinkedFeatureAssets(expected, Replacement()));
        Assert.False(enumerated);
        Assert.Equal(current, store.CaptureLinkedFeatureDependencies(tracked));
        var after = store.ReadLinkedFeaturePage<Head, Payload>(tracked, new(Limit: 3), generation)!;
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.Rows.Select(row => row.Asset.Identity), after.Rows.Select(row => row.Asset.Identity));
        Assert.Equal(before.Rows.Select(row => row.Asset.Label), after.Rows.Select(row => row.Asset.Label));
        Assert.Equal(before.Rows.Select(row => row.Feature), after.Rows.Select(row => row.Feature));
    }

    [Fact]
    public void FencedMediaSaveRejectsAFamilyWriteWithoutEnumeratingOrReplacingItems()
    {
        using var store = Open(); Seed(store);
        var expected = store.CaptureLinkedFeatureDependencies(scope);
        using var writer = Open(); writer.UpsertAssets([Asset(0) with { Label = "Other writer asset" }]);
        var current = store.CaptureLinkedFeatureDependencies(scope);
        long generation = store.PublishLinkedFeatureProjection(current, References(), new Head("Current", 3))!.Value;
        bool enumerated = false;
        IEnumerable<Payload> Replacement()
        { enumerated = true; yield return new(9, "Rejected replacement"); }

        Assert.Null(store.TrySaveLinkedFeatureItems(expected, Replacement()));
        Assert.False(enumerated);
        Assert.Equal(current, store.CaptureLinkedFeatureDependencies(scope));
        var page = store.ReadLinkedFeaturePage<Head, Payload>(scope, new(Limit: 3), generation)!;
        Assert.Equal(new[] { "Original 0", "Original 1", "Original 2" }, page.Rows.Select(row => row.Feature.Text));
        Assert.Equal("Other writer asset", page.Rows[0].Asset.Label);
    }

    [Fact]
    public void AnAcceptedAssetMediaAndCheckpointChainPublishesOneConsistentHead()
    {
        using var store = Open(); Seed(store);
        const string snapshotKey = "generated.snapshot-head", dialogueKey = "generated.dialogue-envelope";
        var tracked = scope with { AdditionalFeatures = [snapshotKey, dialogueKey] };
        var before = store.CaptureLinkedFeatureDependencies(tracked);
        var assets = Assert.IsType<LinkedFeatureDependencies>(store.ReplaceLinkedFeatureAssets(before,
            Enumerable.Range(0, 3).Select(id => Asset(id) with { Label = "Rebuilt " + id })));
        var media = Assert.IsType<LinkedFeatureDependencies>(store.TrySaveLinkedFeatureItems(assets,
            Enumerable.Range(0, 3).Select(id => new Payload(id, "Saved " + id))));
        Assert.True(media.CollectionEpoch > assets.CollectionEpoch);
        Assert.Equal(assets.FamilyEpoch, media.FamilyEpoch);
        Assert.Equal(assets.AdditionalEpoch, media.AdditionalEpoch);
        var snapshot = Assert.IsType<LinkedFeatureDependencies>(store.TrySaveLinkedFeatureCheckpoint(media,
            BoundedFeatureWrite.Create(snapshotKey, "v1", new Payload(10, "Own snapshot"), 1024)));
        var dialogue = Assert.IsType<LinkedFeatureDependencies>(store.TrySaveLinkedFeatureCheckpoint(snapshot,
            BoundedFeatureWrite.Create(dialogueKey, "v1", new Payload(11, "Own dialogue"), 1024)));
        long generation = store.PublishLinkedFeatureProjection(dialogue, References(), new Head("Complete", 3))!.Value;

        Assert.Equal(dialogue, store.CaptureLinkedFeatureDependencies(tracked));
        var page = store.ReadLinkedFeaturePage<Head, Payload>(tracked, new(Limit: 3), generation)!;
        Assert.Equal("Complete", page.Head.Label);
        Assert.Equal(new[] { "Rebuilt 0", "Rebuilt 1", "Rebuilt 2" }, page.Rows.Select(row => row.Asset.Label));
        Assert.Equal(new[] { "Saved 0", "Saved 1", "Saved 2" }, page.Rows.Select(row => row.Feature.Text));
        Assert.All(page.Rows, row => Assert.Equal(row.Asset.Identity.Ordinal, (long)row.Feature.Id));
    }

    [Fact]
    public void CancelledFencedFamilyReplacementRollsBackItsRowsAndEpoch()
    {
        using var store = Open(); Seed(store); long generation = Publish(store);
        var expected = store.CaptureLinkedFeatureDependencies(scope);
        using var cancellation = new CancellationTokenSource();
        IEnumerable<AssetDescriptor> Replacement()
        {
            yield return Asset(0) with { Label = "Cancelled replacement" };
            cancellation.Cancel();
            yield return Asset(1);
        }

        Assert.ThrowsAny<OperationCanceledException>(() => store.ReplaceLinkedFeatureAssets(expected, Replacement(), cancellation.Token));
        Assert.Equal(expected, store.CaptureLinkedFeatureDependencies(scope));
        var page = store.ReadLinkedFeaturePage<Head, Payload>(scope, new(Limit: 3), generation)!;
        Assert.Equal(new[] { "Generated 0", "Generated 1", "Generated 2" }, page.Rows.Select(row => row.Asset.Label));
    }

    [Fact]
    public void CancelledCheckpointRestoresTheExactFeatureEpochAndPreviousHead()
    {
        using var store = Open(); Seed(store);
        const string checkpointKey = "generated.dialogue-envelope";
        var tracked = scope with { AdditionalFeatures = [checkpointKey] };
        store.Save(checkpointKey, "v1", new Payload(1, "Original envelope"));
        var before = store.CaptureLinkedFeatureDependencies(tracked);
        long generation = store.PublishLinkedFeatureProjection(before, References(), new Head("Original", 3))!.Value;
        using var cancellation = new CancellationTokenSource();
        using (var observer = AssetLoadDiagnostics.Listener.Subscribe(new Observe(counter =>
            { if (counter.Stage == "LinkedFeatures.CheckpointPrepared" && counter.Key == scope.Key) cancellation.Cancel(); })))
            Assert.ThrowsAny<OperationCanceledException>(() => store.SaveLinkedFeatureCheckpoint(tracked,
                BoundedFeatureWrite.Create(checkpointKey, "v1", new Payload(2, "Cancelled envelope"), 1024), cancellation.Token));
        Assert.Equal(before, store.CaptureLinkedFeatureDependencies(tracked));
        Assert.Equal(1, store.Load<Payload>(checkpointKey, "v1")!.Id);
        Assert.Equal(generation, store.ReadLinkedFeatureHead<Head>(tracked)!.Generation);
        Assert.Throws<InvalidDataException>(() => store.SaveLinkedFeatureCheckpoint(tracked,
            BoundedFeatureWrite.Create("untracked.feature", "v1", new Payload(3, "Untracked"), 1024)));
    }

    [Fact]
    public void ExistingHeadSchemaGainsMetadataColumnsAndReplacesLegacyTriggersWithoutLosingRows()
    {
        long originalGeneration;
        using (var store = Open())
        {
            Seed(store); originalGeneration = Publish(store);
            using var legacy = Connection(store).CreateCommand();
            legacy.CommandText = """
                DROP TABLE linked_feature_schema;
                ALTER TABLE linked_feature_heads DROP COLUMN additional_features;
                ALTER TABLE linked_feature_heads DROP COLUMN additional_epoch;
                DROP TRIGGER linked_feature_insert;
                CREATE TRIGGER linked_feature_insert BEFORE INSERT ON asset_features BEGIN
                    UPDATE linked_feature_dependencies SET epoch=epoch+1 WHERE kind=0 AND revision=new.revision
                        AND new.feature>=dependency_key||'/item/' AND new.feature<dependency_key||'/item0'; END;
                """;
            legacy.ExecuteNonQuery();
        }
        using var reopened = Open();
        Assert.Equal(originalGeneration, reopened.ReadLinkedFeatureHead<Head>(scope)!.Generation);
        Assert.Equal(3, reopened.ReadLinkedFeaturePage<Head, Payload>(scope, new())!.Rows.Count);
        using (var columns = Connection(reopened).CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(linked_feature_heads)";
            var names = new List<string>(); using var rows = columns.ExecuteReader(); while (rows.Read()) names.Add(rows.GetString(1));
            Assert.Contains("additional_features", names); Assert.Contains("additional_epoch", names);
        }
        const string key = "generated.snapshot-head";
        reopened.Save(key, "v1", new Payload(1, "Original metadata"));
        var tracked = scope with { AdditionalFeatures = [key] };
        reopened.PublishLinkedFeatureProjection(reopened.CaptureLinkedFeatureDependencies(tracked), References(), new Head("Migrated", 3));
        reopened.Save(key, "v1", new Payload(2, "Changed metadata"));
        Assert.Null(reopened.ReadLinkedFeatureHead<Head>(tracked));
    }

    private static SqliteConnection Connection(FeatureStore store) => (SqliteConnection)typeof(FeatureStore)
        .GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed record Payload(int Id, string Text);
    private sealed record Head(string Label, int Count);
    private sealed class Observe(Action<AssetLoadCounterMeasurement> action) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value) { if (value.Value is AssetLoadCounterMeasurement counter) action(counter); }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
