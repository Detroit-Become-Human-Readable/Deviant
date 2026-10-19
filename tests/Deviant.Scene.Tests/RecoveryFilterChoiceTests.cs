using System.Buffers.Binary;
using System.Reflection;
using Deviant.Assets;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Formats.Audio.Model;
using Xunit;

namespace Deviant.Scene.Tests;

public sealed class RecoveryFilterChoiceTests
{
    private static readonly AudioToolPaths NoTools = new(null, null, null, null, null, null, null);

    [Fact]
    public async Task IncompleteDescriptorsSupplyChoicesWithoutHydratingMediaOrDeclaringCompletion()
    {
        using var fixture = new Fixture();
        await using var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, fixture.Cache,
            fixture.Aliases, NoTools, null, CancellationToken.None, startEnrichment: false);
        using (var owner = session.AcquireSharedFeatureStore())
            owner.Store.UpsertAssets(Enumerable.Range(0, 260).Select(index => Descriptor(session, index)));

        await session.RestoreRecoveryFilterChoicesAsync();

        Assert.Equal(260, session.Languages.Count); Assert.Equal("L000", session.Languages[0]); Assert.Equal("L259", session.Languages[^1]);
        Assert.Equal(new[] { "PCM", "Vorbis" }, session.Codecs); Assert.Equal(new[] { 1, 2 }, session.ChannelCounts);
        Assert.Empty(session.Banks); Assert.Empty(session.Catalog.Media);
        Assert.False(session.IsCatalogLoaded); Assert.False(session.IsEnrichmentComplete); Assert.False(session.IsEnrichmentStarted);
    }

    [Fact]
    public async Task EpochRefreshReplacesSavedChoicesAndMergesCurrentStagedDiscoveries()
    {
        using var fixture = new Fixture();
        await using var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, fixture.Cache,
            fixture.Aliases, NoTools, null, CancellationToken.None, startEnrichment: false);
        using (var owner = session.AcquireSharedFeatureStore()) owner.Store.UpsertAssets([Descriptor(session, 0)]);
        await session.RestoreRecoveryFilterChoicesAsync();
        var media = new MediaEntry { Identity = new(session.Snapshot.IndexContentId, 0, "staged"),
            Language = "LIVE", Codec = "Live codec", Channels = 6 };
        session.Catalog.Media.Add(media);
        typeof(AudioExplorerSession).GetMethod("UpdateStagedFilterOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [session.Catalog, null]);
        using (var owner = session.AcquireSharedFeatureStore())
            owner.Store.ReplaceAssets(AudioExplorerSession.AssetFamily, [Descriptor(session, 1)]);

        await session.RestoreRecoveryFilterChoicesAsync();

        Assert.Equal(new[] { "L001", "LIVE" }, session.Languages); Assert.DoesNotContain("L000", session.Languages);
        Assert.Equal(new[] { "Live codec", "Vorbis" }, session.Codecs); Assert.Equal(new[] { 2, 6 }, session.ChannelCounts);
        Assert.Same(media, Assert.Single(session.Catalog.Media)); Assert.False(session.IsEnrichmentComplete);
        Assert.False(session.IsEnrichmentStarted);
    }

    [Fact]
    public async Task CanceledRestorationLeavesExistingChoicesAndReleasesItsFeatureLease()
    {
        using var fixture = new Fixture();
        var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, fixture.Cache,
            fixture.Aliases, NoTools, null, CancellationToken.None, startEnrichment: false);
        try
        {
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RestoreRecoveryFilterChoicesAsync(canceled.Token));
            Assert.Empty(session.Languages); Assert.Empty(session.Codecs); Assert.Empty(session.ChannelCounts);
        }
        finally { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task HighCardinalityChoicesRemainBoundedWithoutDroppingSavedAssets()
    {
        using var fixture = new Fixture();
        await using var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath, fixture.Cache,
            fixture.Aliases, NoTools, null, CancellationToken.None, startEnrichment: false);
        using (var owner = session.AcquireSharedFeatureStore())
            owner.Store.UpsertAssets(Enumerable.Range(0, 4097).Select(index => Descriptor(session, index)));
        await session.RestoreRecoveryFilterChoicesAsync();
        Assert.Equal(4096, session.Languages.Count); Assert.True(session.RecoveryFilterChoicesTruncated);
        using var retained = session.AcquireSharedFeatureStore();
        Assert.Equal(4097, retained.Store.Count(new(Family: AudioExplorerSession.AssetFamily)));
        Assert.Empty(session.Catalog.Media); Assert.False(session.IsEnrichmentStarted); Assert.False(session.IsEnrichmentComplete);
    }

    private static AssetDescriptor Descriptor(AudioExplorerSession session, int index) => new(
        new(session.Snapshot.IndexContentId, 0, "generated/media/" + index),
        new(session.Snapshot.IndexContentId, session.Snapshot.PhysicalSourceStamp), AudioExplorerSession.AssetFamily,
        "Generated clip " + index, "saved-group", AssetAvailability.Available, AssetCompleteness.Partial,
        AssetCapabilities.None, [], [], "L" + index.ToString("D3"), index % 2 == 0 ? "PCM" : "Vorbis", Channels: index % 2 + 1);

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "Deviant-recovery-filters", Guid.NewGuid().ToString("N"));
        internal string IndexPath { get; }
        internal CacheManager Cache { get; }
        internal AliasStore Aliases { get; }
        internal Fixture()
        {
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            IndexPath = Path.Combine(source, "generated.idx");
            var bytes = new byte[105 + 28]; "QUANTICDREAMTABINDEX"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 18); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(101), 1);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(105), 1033); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(109), 1);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(113), 1); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(121), 4);
            File.WriteAllBytes(IndexPath, bytes); File.WriteAllBytes(Path.Combine(source, "generated.dat"), [1, 2, 3, 4]);
            Cache = new(Path.Combine(root, "settings"), new(Path.Combine(root, "cache"), 32 * 1024 * 1024));
            Aliases = new(Path.Combine(root, "library"));
        }
        public void Dispose() { Cache.Dispose(); Directory.Delete(root, true); }
    }
}
