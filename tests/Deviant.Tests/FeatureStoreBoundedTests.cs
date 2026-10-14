using Deviant.Catalog;
using Deviant.Core;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Deviant.Tests;

public sealed class FeatureStoreBoundedTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deviant-bounded-feature-tests", Guid.NewGuid().ToString("N"));
    private string CacheRoot => Path.Combine(root, "cache");

    [Fact]
    public void ModelNameProfileFiltersPagesCountsAndGroupedAnimationLinks()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var revision = new Deviant.Assets.SourceRevision("bounded-feature-index", "bounded-feature-stamp");
        Deviant.Assets.AssetDescriptor Row(long ordinal, string profile, string clips) => new(
            new(revision.IndexContentId, ordinal), revision, "character-model", "Pilot", null,
            Deviant.Assets.AssetAvailability.Available, Deviant.Assets.AssetCompleteness.Complete,
            Deviant.Assets.AssetCapabilities.None, [], [])
        {
            Metadata = new Dictionary<string, string>
            {
                ["model.kind"] = "character", ["character.key"] = "pilot", ["character.name"] = "Pilot",
                ["model.name-profile"] = profile, ["model.animation-state"] = "complete",
                ["model.animation-identities"] = clips
            }
        };
        store.UpsertAssets([Row(1, "previous", "[\"legacy-clip\"]"), Row(2, "current", "[\"current-clip\"]")]);
        var query = new Deviant.Assets.AssetQuery(Family: "character-model") { ModelNameProfile = "current" };

        Assert.Equal(1, store.Count(query));
        Assert.Equal(2, Assert.Single(store.Query(query)).Identity.Ordinal);
        var group = Assert.Single(store.QueryCharacterGroups(query, out var total));
        Assert.Equal(1, total);
        Assert.Equal(2, group.Identity.Ordinal);
        Assert.Equal("1", group.Metadata["model.animation-count"]);
    }

    [Fact]
    public void BoundedFeatureRoundTripsWithinTheUtf8Limit()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var expected = new SampleFeature("dialogue-header", 3);

        store.SaveBounded("sample", "v1", expected, 1024);

        Assert.Equal(expected, store.LoadBounded<SampleFeature>("sample", "v1", 1024));
    }

    [Fact]
    public void BoundedFeatureIdentityPayloadOmitsComputedKeysAndReadsLegacyPayloads()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var identity = new Deviant.Assets.ResourceIdentity("generated-index", 27, "dc-object/3/2013/91");
        var value = new NestedIdentityFeature(identity,
            [identity, identity with { Ordinal = 28, MemberPath = "dc-object/4/2013/92" }]);
        var legacyJson = JsonSerializer.Serialize(value);
        var legacyBytes = Encoding.UTF8.GetByteCount(legacyJson);
        var write = BoundedFeatureWrite.Create("nested", "v1", value, 16 * 1024);
        var repeated = BoundedFeatureWrite.Create("nested", "v1", value, 16 * 1024);
        var payloadProperty = typeof(BoundedFeatureWrite).GetProperty("Payload",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var payload = (string)payloadProperty.GetValue(write)!;
        var repeatedPayload = (string)payloadProperty.GetValue(repeated)!;

        Assert.DoesNotContain("\"Key\"", payload, StringComparison.Ordinal);
        Assert.Equal(payload, repeatedPayload);
        Assert.Equal(Encoding.UTF8.GetByteCount(payload), write.Utf8ByteCount);
        Assert.True(write.Utf8ByteCount < legacyBytes);
        Assert.Throws<InvalidDataException>(() => BoundedFeatureWrite.Create("too-small", "v1", value,
            write.Utf8ByteCount - 1));

        store.SaveBounded("nested", "v1", value, 16 * 1024);
        var loaded = store.LoadBounded<NestedIdentityFeature>("nested", "v1", 16 * 1024);
        Assert.Equal(value.Owner, loaded!.Owner);
        Assert.Equal(value.Related, loaded.Related);
        Assert.Equal(identity.Key, loaded!.Owner.Key);

        store.Save("legacy-nested", "v1", value);
        var legacy = store.LoadBounded<NestedIdentityFeature>("legacy-nested", "v1", 16 * 1024);
        Assert.Equal(value.Owner, legacy!.Owner);
        Assert.Equal(value.Related, legacy.Related);
        Assert.Equal(identity.Key, legacy.Owner.Key);
    }

    [Fact]
    public void OversizedRowsAreRejectedAndOversizedWritesPreserveThePreviousValue()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var original = new SampleFeature("kept", 1);
        store.SaveBounded("sample", "v1", original, 1024);
        store.Save("oversized", "v1", new string('x', 4096));

        Assert.Throws<InvalidDataException>(() => store.LoadBounded<string>("oversized", "v1", 128));
        Assert.Throws<InvalidDataException>(() => store.SaveBounded("sample", "v1", new string('y', 4096), 128));
        Assert.Throws<ArgumentNullException>(() => store.SaveBounded<string?>("sample", "v1", null, 128));
        Assert.Equal(original, store.LoadBounded<SampleFeature>("sample", "v1", 1024));
    }

    [Fact]
    public void CancellationDoesNotReplaceAnExistingBoundedFeature()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var original = new SampleFeature("kept", 1);
        store.SaveBounded("sample", "v1", original, 1024);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => store.SaveBounded("sample", "v1", new SampleFeature("replacement", 2), 1024, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => store.LoadBounded<SampleFeature>("sample", "v1", 1024, cancelled.Token));
        Assert.Equal(original, store.LoadBounded<SampleFeature>("sample", "v1", 1024));
    }

    [Fact]
    public void BoundedFeatureBatchRejectsDuplicateKeysBeforeWritingAnyRows()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var first = BoundedFeatureWrite.Create("first", "v1", new SampleFeature("first", 1), 1024);
        var duplicate = BoundedFeatureWrite.Create("first", "v1", new SampleFeature("second", 2), 1024);

        Assert.Throws<ArgumentException>(() => store.SaveBoundedBatch([first, duplicate]));
        Assert.Null(store.LoadBounded<SampleFeature>("first", "v1", 1024));
    }

    [Fact]
    public void BoundedFeatureAndAssetPublicationWritesBothViews()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var revision = new Deviant.Assets.SourceRevision("bounded-feature-index", "bounded-feature-stamp");
        var asset = new Deviant.Assets.AssetDescriptor(new(revision.IndexContentId, 41), revision,
            "bounded-test", "A bounded asset", null, Deviant.Assets.AssetAvailability.Available,
            Deviant.Assets.AssetCompleteness.Complete, Deviant.Assets.AssetCapabilities.None, [], []);
        var write = new BoundedFeatureAssetWrite(
            BoundedFeatureWrite.Create("entry/41", "v1", new SampleFeature("published", 41), 1024), asset);

        var published = store.PublishBoundedAssetsBatch([write]);

        Assert.Contains(asset.Identity, published);
        Assert.Equal(new SampleFeature("published", 41), store.LoadBounded<SampleFeature>("entry/41", "v1", 1024));
        Assert.Equal(asset.Identity, Assert.Single(store.Query(new Deviant.Assets.AssetQuery(Family: "bounded-test"))).Identity);
    }

    [Fact]
    public void BoundedPublicationDoesNotCrossAnExistingAssetFamilyBoundary()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        var revision = new Deviant.Assets.SourceRevision("bounded-feature-index", "bounded-feature-stamp");
        var identity = new Deviant.Assets.ResourceIdentity(revision.IndexContentId, 42);
        Deviant.Assets.AssetDescriptor Asset(string family, string label) => new(identity, revision, family, label, null,
            Deviant.Assets.AssetAvailability.Available, Deviant.Assets.AssetCompleteness.Complete,
            Deviant.Assets.AssetCapabilities.None, [], []);
        store.UpsertAssets([Asset("first-family", "Original")]);
        var write = new BoundedFeatureAssetWrite(
            BoundedFeatureWrite.Create("entry/42", "v1", new SampleFeature("must not publish", 42), 1024),
            Asset("second-family", "Replacement"));

        var published = store.PublishBoundedAssetsBatch([write]);

        Assert.Empty(published);
        Assert.Null(store.LoadBounded<SampleFeature>("entry/42", "v1", 1024));
        Assert.Equal("Original", Assert.Single(store.Query(new Deviant.Assets.AssetQuery(Family: "first-family"))).Label);
    }

    [Fact]
    public void JsonNullIsRejectedAsAnInvalidBoundedFeature()
    {
        using var store = FeatureStore.Open(Snapshot(), CacheRoot);
        store.Save<string?>("sample", "v1", null);

        Assert.Throws<InvalidDataException>(() => store.LoadBounded<string?>("sample", "v1", 1024));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private ArchiveSnapshot Snapshot()
    {
        var sourceRoot = Path.Combine(root, "archive");
        return new(Path.Combine(sourceRoot, "sample.idx"), "bounded-feature-index", IndexLayout.SupportedVersion,
            0, new byte[] { 0x41 }, 0, [], [], new FileStamp(true, "index", 1, 1, 1, true), "bounded-feature-stamp", []);
    }

    private sealed record SampleFeature(string Name, int Count);
    private sealed record NestedIdentityFeature(Deviant.Assets.ResourceIdentity Owner,
        IReadOnlyList<Deviant.Assets.ResourceIdentity> Related);
}
