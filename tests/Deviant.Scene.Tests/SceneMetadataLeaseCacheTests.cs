using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Deviant.Scene.Tests;

[Collection("Scene metadata diagnostics")]
public sealed class SceneMetadataLeaseCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusesDirectoryWithoutDuplicatingOwnedReservations(bool compressed)
    {
        using var fixture = new SceneMetadataFixture(compressed: compressed);
        var budget = new AssetMemoryBudget();
        using (var cache = new SceneMetadataLeaseCache(fixture.Snapshot, budget, false))
        {
            using var first = await cache.AcquireAsync(fixture.Container);
            long retained = budget.UsedBytes;
            using var second = await cache.AcquireAsync(fixture.Container);
            Assert.Same(first.Directory, second.Directory);
            Assert.Equal(first.SourceExtents, second.SourceExtents);
            Assert.Equal(retained, budget.UsedBytes);
            Assert.Equal(retained, cache.RetainedBytes);
            Assert.Equal(1, cache.Count);
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task DisposingCachePreservesActiveBorrowUntilItsFinalRelease()
    {
        using var fixture = new SceneMetadataFixture(); var budget = new AssetMemoryBudget();
        var cache = new SceneMetadataLeaseCache(fixture.Snapshot, budget, false);
        var first = await cache.AcquireAsync(fixture.Container);
        var second = await cache.AcquireAsync(fixture.Container);
        var directory = first.Directory;
        cache.Dispose(); cache.Dispose();
        Assert.Equal(0, cache.RetainedBytes);
        Assert.True(budget.UsedBytes > 0);
        first.Dispose(); first.Dispose();
        second.EnsureUnchanged(); Assert.Same(directory, second.Directory);
        Assert.Equal(3, second.Directory.Objects.Count);
        second.Dispose(); Assert.Equal(0, budget.UsedBytes);
        Assert.Throws<ObjectDisposedException>(() => directory = second.Directory);
    }

    [Fact]
    public async Task EntryAndByteCapsEvictIdleOwnersButKeepActiveBorrows()
    {
        using var fixture = new SceneMetadataFixture(companionCount: 2);
        var budget = new AssetMemoryBudget();
        using (var cache = new SceneMetadataLeaseCache(fixture.Snapshot, budget, false, maximumEntries: 1))
        {
            var first = await cache.AcquireAsync(fixture.Container);
            var firstDirectory = first.Directory;
            using (var second = await cache.AcquireAsync(new(fixture.Container.IndexContentId, 2)))
            {
                Assert.Equal(1, cache.Count);
                Assert.NotSame(first.Directory, second.Directory);
                Assert.True(budget.UsedBytes > cache.RetainedBytes);
            }
            Assert.Equal(0, cache.EvictIdle());
            first.Dispose();
            using (var second = await cache.AcquireAsync(new(fixture.Container.IndexContentId, 2)))
                Assert.Equal(1, cache.Count);
            using var reloaded = await cache.AcquireAsync(fixture.Container);
            Assert.NotSame(firstDirectory, reloaded.Directory);
            Assert.InRange(cache.RetainedBytes, 1, SceneMetadataLeaseCache.MaximumRetainedBytes);
        }
        Assert.Equal(0, budget.UsedBytes);
        using (var cache = new SceneMetadataLeaseCache(fixture.Snapshot, budget, false, maximumRetainedBytes: 1))
        {
            using var first = await cache.AcquireAsync(fixture.Container);
            using var second = await cache.AcquireAsync(fixture.Container);
            Assert.NotSame(first.Directory, second.Directory); Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.RetainedBytes); Assert.True(budget.UsedBytes > 0);
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task MissClearsIdleOwnershipBeforeRetryingTheExistingMemoryAdmission()
    {
        using var fixture = new SceneMetadataFixture(companionCount: 2);
        var calibration = new AssetMemoryBudget();
        using (await NativeContainerLease.OpenMetadataAsync(fixture.Snapshot, fixture.Container, calibration)) { }
        long existingReadPeak = calibration.PeakBytes;
        var budget = new AssetMemoryBudget(existingReadPeak + 4096);
        using (var cache = new SceneMetadataLeaseCache(fixture.Snapshot, budget, false))
        {
            using (await cache.AcquireAsync(fixture.Container)) { }
            using var second = await cache.AcquireAsync(new(fixture.Container.IndexContentId, 2));
            Assert.Equal(1, cache.Count);
            Assert.True(budget.PeakBytes <= budget.MaximumBytes);
            Assert.Equal(3, second.Directory.Objects.Count);
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task CanceledMissAndHitNeverLeaveUnfinishedOwnership()
    {
        using var fixture = new SceneMetadataFixture(); var budget = new AssetMemoryBudget();
        using (var cache = new SceneMetadataLeaseCache(fixture.Snapshot, budget, false))
        {
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.AcquireAsync(fixture.Container, canceled.Token));
            Assert.Equal(0, cache.Count); Assert.Equal(0, budget.UsedBytes);
            using (var duringOpen = new CancellationTokenSource())
            using (var observer = new CancelAfterVerification(duringOpen))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.AcquireAsync(fixture.Container, duringOpen.Token));
            Assert.Equal(0, cache.Count); Assert.Equal(0, budget.UsedBytes);
            using (await cache.AcquireAsync(fixture.Container)) { }
            var retained = budget.UsedBytes;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.AcquireAsync(fixture.Container, canceled.Token));
            Assert.Equal(retained, budget.UsedBytes); Assert.Equal(1, cache.Count);
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedPinnedOrNamedSourceFailsOnTheNextHit(bool indexChanged)
    {
        using var fixture = new SceneMetadataFixture(); var budget = new AssetMemoryBudget();
        var revisions = new ControlledRevisions(fixture.Snapshot);
        await using (var source = ArchiveSourceSession.Acquire(fixture.Snapshot, revisions: revisions))
        using (var cache = new SceneMetadataLeaseCache(fixture.Snapshot, budget, false))
        {
            using (await cache.AcquireAsync(fixture.Container)) { }
            revisions.ChangedPath = indexChanged ? fixture.Snapshot.IndexPath : fixture.Snapshot.Volumes[0].FilePath;
            var error = await Assert.ThrowsAsync<ArchiveException>(() => cache.AcquireAsync(fixture.Container));
            Assert.Equal("SourceChanged", error.Code);
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreliableStampsBypassReuseAndRetainStrongExtentVerification(bool unreliableIndex)
    {
        using var fixture = new SceneMetadataFixture(); var budget = new AssetMemoryBudget();
        var snapshot = fixture.Snapshot;
        snapshot = unreliableIndex
            ? snapshot with { IndexStamp = snapshot.IndexStamp with { Reliable = false } }
            : snapshot with { Volumes = [snapshot.Volumes[0] with { Stamp = snapshot.Volumes[0].Stamp with { Reliable = false } }] };
        var revisions = new ControlledRevisions(snapshot);
        await using (var source = ArchiveSourceSession.Acquire(snapshot, revisions: revisions))
        using (var observer = new SceneRegionMetadataReuseTests.MeasurementObserver())
        using (var cache = new SceneMetadataLeaseCache(snapshot, budget, false))
        {
            using var first = await cache.AcquireAsync(fixture.Container);
            using var second = await cache.AcquireAsync(fixture.Container);
            Assert.NotSame(first.Directory, second.Directory); Assert.Equal(0, cache.Count);
            Assert.Equal(2, observer.Count("SourceVerification"));
            Assert.Equal(2, observer.Count("RegionMetadataCache"));
            Assert.Equal(unreliableIndex ? 1 : 2, observer.CountReads(snapshot.Volumes[0].RelativeName));
            if (unreliableIndex) Assert.True(observer.CountReads(Path.GetFileName(snapshot.IndexPath)) > 1);
            revisions.ChangedPath = snapshot.Volumes[0].FilePath;
            var error = await Assert.ThrowsAsync<ArchiveException>(() => cache.AcquireAsync(fixture.Container));
            Assert.Equal("SourceChanged", error.Code);
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task OwnersIsolateRevisionsAndRejectMemberOrForeignIdentity()
    {
        using var firstFixture = new SceneMetadataFixture(); using var secondFixture = new SceneMetadataFixture();
        var budget = new AssetMemoryBudget();
        using (var firstCache = new SceneMetadataLeaseCache(firstFixture.Snapshot, budget, false))
        using (var secondCache = new SceneMetadataLeaseCache(secondFixture.Snapshot, budget, true))
        {
            using var first = await firstCache.AcquireAsync(firstFixture.Container);
            using var second = await secondCache.AcquireAsync(secondFixture.Container);
            Assert.NotEqual(first.Directory.Revision, second.Directory.Revision); Assert.NotSame(first.Directory, second.Directory);
            await Assert.ThrowsAsync<ArgumentException>(() => firstCache.AcquireAsync(firstFixture.Container with { MemberPath = "member" }));
            await Assert.ThrowsAsync<ArgumentException>(() => firstCache.AcquireAsync(new("another-index", 1)));
            await Assert.ThrowsAsync<ArgumentException>(() => firstCache.AcquireAsync(new(firstFixture.Container.IndexContentId, -1)));
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task InvalidContainerNeverEntersCacheOrRetainsReservations()
    {
        using var fixture = new SceneMetadataFixture(malformed: true); var budget = new AssetMemoryBudget();
        using var cache = new SceneMetadataLeaseCache(fixture.Snapshot, budget, false);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var error = await Assert.ThrowsAsync<ArchiveException>(() => cache.AcquireAsync(fixture.Container));
            Assert.Equal("NativeContainerSignature", error.Code);
            Assert.Equal(0, cache.Count); Assert.Equal(0, budget.UsedBytes);
        }
    }

    [Fact]
    public async Task MissingVolumeUsesTheAuthoritativeContainerAdmissionError()
    {
        using var fixture = new SceneMetadataFixture(); var budget = new AssetMemoryBudget();
        var snapshot = fixture.Snapshot with
        {
            Volumes = [],
            Records = fixture.Snapshot.Records.Select(item => item with { Validity = ResourceValidity.MissingVolume }).ToArray()
        };
        var authoritative = await Assert.ThrowsAsync<ArchiveException>(() =>
            NativeContainerLease.OpenMetadataAsync(snapshot, fixture.Container, budget));
        using var cache = new SceneMetadataLeaseCache(snapshot, budget, false);
        var throughCache = await Assert.ThrowsAsync<ArchiveException>(() => cache.AcquireAsync(fixture.Container));
        Assert.Equal("NativeContainerLimit", authoritative.Code);
        Assert.Equal(authoritative.Code, throughCache.Code);
        Assert.Equal(0, cache.Count); Assert.Equal(0, budget.UsedBytes);
    }

    private sealed class ControlledRevisions(ArchiveSnapshot snapshot) : ISourceRevisionProvider
    {
        internal string? ChangedPath;
        public FileStamp Capture(string path)
        {
            var stamp = path == snapshot.IndexPath ? snapshot.IndexStamp : snapshot.Volumes.Single(item => item.FilePath == path).Stamp;
            return path == ChangedPath ? stamp with { LastWriteUtcTicks = stamp.LastWriteUtcTicks + 1 } : stamp;
        }
        public FileStamp Capture(SafeFileHandle handle, string path) => Capture(path);
    }

    private sealed class CancelAfterVerification : IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly IDisposable subscription;
        private readonly CancellationTokenSource cancellation;
        internal CancelAfterVerification(CancellationTokenSource cancellation)
        { this.cancellation = cancellation; subscription = AssetLoadDiagnostics.Listener.Subscribe(this); }
        public void OnNext(KeyValuePair<string, object?> value)
        { if (value.Value is AssetLoadMeasurement { Stage: "SourceVerification" }) cancellation.Cancel(); }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose() => subscription.Dispose();
    }
}
