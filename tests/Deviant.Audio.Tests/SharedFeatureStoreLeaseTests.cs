using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class SharedFeatureStoreLeaseTests
{
    private static readonly AudioToolPaths NoTools = new(null, null, null, null, null, null, null);

    [Fact]
    public async Task BorrowersShareTheSameOwnedStoreAndRevisionWithoutStartingAudio()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new Workspace();
        await using var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,
            workspace.Cache, workspace.Aliases, NoTools, null, CancellationToken.None, startEnrichment: false);
        using var first = session.AcquireSharedFeatureStore();
        using var second = session.AcquireSharedFeatureStore();
        Assert.Same(first.Store, second.Store);
        Assert.Equal(session.Snapshot.IndexContentId, first.Revision.IndexContentId);
        Assert.Equal(session.Snapshot.PhysicalSourceStamp, first.Revision.PhysicalSourceStamp);
        first.Store.SaveBounded("generated.shared.feature", "v1", new Value(17), 1024);
        Assert.Equal(17, second.Store.LoadBounded<Value>("generated.shared.feature", "v1", 1024)!.Number);
        Assert.False(session.IsEnrichmentStarted);
    }

    [Fact]
    public async Task SessionCancellationPrecedesDrainAndReleasedLeaseCannotExposeStore()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new Workspace();
        var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,
            workspace.Cache, workspace.Aliases, NoTools, null, CancellationToken.None, startEnrichment: false);
        var lease = session.AcquireSharedFeatureStore();
        try
        {
            var disposal = session.DisposeAsync().AsTask();
            Assert.True(lease.SourceCancellation.IsCancellationRequested);
            Assert.False(disposal.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => { session.AcquireSharedFeatureStore(); });
            lease.Store.SaveBounded("generated.drain.feature", "v1", new Value(23), 1024);
            lease.Dispose();
            lease.Dispose();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<ObjectDisposedException>(() => { _ = lease.Store; });
        }
        finally
        {
            lease.Dispose();
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConcurrentReleaseDrainsAllBorrowersAndSessionDisposalIsIdempotent()
    {
        using var fixture = FormatsFixtureBuilder.CreateAudioCatalog();
        using var workspace = new Workspace();
        var session = await AudioExplorerSession.OpenStagedAsync(fixture.IndexPath,
            workspace.Cache, workspace.Aliases, NoTools, null, CancellationToken.None, startEnrichment: false);
        var leases = Enumerable.Range(0, 8).Select(_ => session.AcquireSharedFeatureStore()).ToArray();
        try
        {
            var disposal = session.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Parallel.ForEach(leases, lease => { lease.Dispose(); lease.Dispose(); });
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            await session.DisposeAsync();
            Assert.All(leases, lease => Assert.Throws<ObjectDisposedException>(() => { _ = lease.Store; }));
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
            await session.DisposeAsync();
        }
    }

    private sealed record Value(int Number);
    private sealed class Workspace : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "Deviant-feature-leases", Guid.NewGuid().ToString("N"));
        public Workspace()
        {
            Directory.CreateDirectory(root);
            Cache = new(Path.Combine(root, "settings"), new CacheSettings(Path.Combine(root, "cache")));
            Aliases = new(Path.Combine(root, "library"));
        }
        public CacheManager Cache { get; }
        public AliasStore Aliases { get; }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
