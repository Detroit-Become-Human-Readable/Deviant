using System.Reflection;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class FeatureStoreTextureReadTests
{
    [Fact]
    public async Task ReaderDoesNotCaptureTheCallingSynchronizationContext()
    {
        using var fixture = await ProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        store.InitializeTextureProjections("context", [fixture.Entry("Generated texture")]);
        using var lease = store.AcquireTextureProjectionReadLease("context");
        var context = new CountingContext();
        var previous = SynchronizationContext.Current;
        Task<TextureDiscoveryFilterOptions> read;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            read = lease.ReadFilterOptionsAsync();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await read.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, context.Posts);
    }

    private sealed class CountingContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref Posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    [Fact]
    public async Task OptionsLeaseKeepsOneSourceGenerationAndCancellationDoesNotPoisonLaterReads()
    {
        using var fixture = await ProjectionFixture.CreateAsync();
        using var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        var baseline = fixture.Entry("Generated texture");
        store.InitializeTextureProjections("options", [baseline]);
        var generation = store.CommitTextureProjectionOwner("options", fixture.Checkpoint,
            [fixture.Named(baseline, 32, 16, "BC1", "Diffuse", "Room")]);

        using (var lease = store.AcquireTextureProjectionReadLease("options"))
        {
            var first = lease.ReadFilterOptionsAsync();
            Assert.Same(first, lease.ReadFilterOptionsAsync());
            var options = await first;
            Assert.Equal(generation, lease.Generation);
            Assert.Equal(SourceIdentity.DerivedCacheKey(fixture.Snapshot.IndexContentId,
                fixture.Snapshot.PhysicalSourceStamp), lease.SourceRevision);
            Assert.True(lease.CurrentEpoch >= 0);
            Assert.True(lease.IsCurrent);
            Assert.Equal(new[] { new TextureDimensionOption(32, 16) }, options.Dimensions);
            Assert.Equal(new[] { "BC1" }, options.Formats);
            Assert.Equal(new[] { "Diffuse" }, options.VerifiedRoles);
            Assert.Equal(new[] { new TextureContextOption("Room", "Room") }, options.Contexts);
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using (var lease = store.AcquireTextureProjectionReadLease("options"))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.ReadFilterOptionsAsync(cancelled.Token));

        using (var retry = store.AcquireTextureProjectionReadLease("options"))
        {
            var retried = await retry.ReadFilterOptionsAsync();
            Assert.Equal(generation, retry.Generation);
            Assert.Equal(new[] { "BC1" }, retried.Formats);
        }
    }

    [Fact]
    public async Task SessionOptionsWaitReleasesProjectionGateSoPageQueryCanStart()
    {
        using var fixture = DiscoveryFixture.Create();
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        var cache = new CacheManager(fixture.Settings, new(fixture.Cache));
        await using var discovery = new TextureDiscoverySession(catalogs, native,
            new AliasStore(fixture.Library), cache);
        await discovery.EnrichmentTask.WaitAsync(TimeSpan.FromSeconds(10));
        var store = Field<FeatureStore>(discovery, "features");
        Assert.NotNull(store);
        var writerGate = Field<object>(store!, "gate");
        var projectionGate = Field<object>(discovery, "projectionStoreGate");
        var leases = Field<HashSet<FeatureStore.TextureProjectionReadLease>>(store!, "textureReadLeases");

        using var writerHeld = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            lock (writerGate)
            {
                writerHeld.Set();
                releaseWriter.Wait();
            }
        });
        if (!writerHeld.Wait(TimeSpan.FromSeconds(5)))
        {
            releaseWriter.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Fail("The generated writer did not acquire its gate.");
        }

        try
        {
            var options = discovery.ReadFilterOptionsAsync();
            Assert.True(SpinWait.SpinUntil(() => leases.Count > 0, TimeSpan.FromSeconds(5)));
            Assert.False(options.IsCompleted);
            Assert.True(Monitor.TryEnter(projectionGate, TimeSpan.FromSeconds(5)),
                "The options operation retained projectionStoreGate while waiting on FeatureStore.");
            Monitor.Exit(projectionGate);

            var page = discovery.QueryAsync(new());
            Assert.False(page.IsCompleted);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                if (Monitor.TryEnter(projectionGate)) { Monitor.Exit(projectionGate); return false; }
                return true;
            }, TimeSpan.FromSeconds(5)), "The page query did not enter projectionStoreGate while options waited on the writer.");
            releaseWriter.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEmpty((await options.WaitAsync(TimeSpan.FromSeconds(10))).Catalogs);
            Assert.Single((await page.WaitAsync(TimeSpan.FromSeconds(10))).Entries);
        }
        finally { releaseWriter.Set(); }
    }

    [Fact]
    public async Task DisposingStoreDrainsRegisteredReadLeasesAndRejectsLaterReads()
    {
        using var fixture = await ProjectionFixture.CreateAsync();
        var store = FeatureStore.Open(fixture.Snapshot, fixture.Cache);
        using var lease = store.AcquireTextureProjectionReadLease("not-initialized");

        store.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = lease.ReadFilterOptionsAsync(); });
    }

    private static T Field<T>(object target, string name) where T : class =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private sealed class ProjectionFixture : IDisposable
    {
        private readonly object value;
        private ProjectionFixture(object value) => this.value = value;
        private T Get<T>(string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
        public ArchiveSnapshot Snapshot => Get<ArchiveSnapshot>("Snapshot");
        public string Cache => Get<string>("Cache");
        public ResourceIdentity Owner => Get<ResourceIdentity>("Owner");
        public TextureEvidence Evidence => Get<TextureEvidence>("Evidence");
        public TextureProjectionOwnerCheckpoint Checkpoint => Get<TextureProjectionOwnerCheckpoint>("Checkpoint");
        public TextureDiscoveryEntry Entry(string label) => (TextureDiscoveryEntry)value.GetType().GetMethod("Entry")!.Invoke(value, [label, 1L])!;
        public TextureDiscoveryEntry Named(TextureDiscoveryEntry entry, int width, int height, string format,
            string role, string context) => entry with
        {
            NameKind = TextureNameKind.VerifiedOwnerRole, Width = width, Height = height, Format = format,
            ContextKey = context, ContextLabel = context,
            Owners = [new(Owner, "Generated panel", TextureRelationshipKind.Object, true, true, [Evidence], role)],
            NameCandidates = [new(entry.Label, TextureNameKind.VerifiedOwnerRole, [Evidence], Owner, role)]
        };
        public static async Task<ProjectionFixture> CreateAsync()
        {
            var type = typeof(TextureProjectionStoreTests).GetNestedType("Fixture", BindingFlags.NonPublic)!;
            var task = (Task)type.GetMethod("CreateAsync")!.Invoke(null, [false])!;
            await task.ConfigureAwait(false);
            return new(task.GetType().GetProperty("Result")!.GetValue(task)!);
        }
        public void Dispose() => ((IDisposable)value).Dispose();
    }

    private sealed class DiscoveryFixture : IDisposable
    {
        private readonly object value;
        private DiscoveryFixture(object value) => this.value = value;
        private T Get<T>(string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
        public string Index => Get<string>("Index");
        public string Cache => Get<string>("Cache");
        public string Settings => Get<string>("Settings");
        public string Library => Get<string>("Library");
        public static DiscoveryFixture Create()
        {
            var type = typeof(TextureDiscoverySessionTests).GetNestedType("DiscoveryFixture", BindingFlags.NonPublic)!;
            var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
            var value = constructor.Invoke([0, false, false, 0, false, 0, null, 1, false, false, false, false]);
            return new(value);
        }
        public void Dispose() => ((IDisposable)value).Dispose();
    }
}
