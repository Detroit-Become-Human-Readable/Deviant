using Deviant.Desktop;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using HelixToolkit.SharpDX;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class ViewportResourceCacheTests
{
    [Fact]
    public void ReturnHitTransfersUnusedOwnershipWithoutReleasingTheResource()
    {
        var cache = new ViewportResourceCache(100, 100); int released = 0;
        Assert.True(cache.Retain("geometry", 40, 50, () => released++));
        Assert.Equal(40, cache.CpuBytes); Assert.Equal(50, cache.GpuBytes);
        Assert.True(cache.Take("geometry")); Assert.Equal(0, released);
        Assert.Equal(0, cache.Count); Assert.Equal(0, cache.CpuBytes); Assert.Equal(0, cache.GpuBytes); Assert.Equal(1, cache.Hits);
        Assert.False(cache.Take("geometry"));
        Assert.True(cache.Retain("geometry", 40, 50, () => released++)); cache.Clear(); Assert.Equal(1, released);
    }

    [Theory]
    [InlineData(60, 30)]
    [InlineData(30, 60)]
    public void EitherMemoryLimitEvictsTheLeastRecentlyUnusedEntry(long cpu, long gpu)
    {
        var cache = new ViewportResourceCache(100, 100); var released = new List<string>();
        Assert.True(cache.Retain("old", cpu, gpu, () => released.Add("old")));
        Assert.True(cache.Retain("new", cpu, gpu, () => released.Add("new")));
        Assert.Equal(new[] { "old" }, released); Assert.Equal(1, cache.Count);
        Assert.InRange(cache.CpuBytes, 0, 100); Assert.InRange(cache.GpuBytes, 0, 100);
        cache.Clear(); Assert.Equal(new[] { "old", "new" }, released);
    }

    [Fact]
    public void ReusedEntryBecomesMostRecentWhenReleasedAgain()
    {
        var cache = new ViewportResourceCache(100, 100); var released = new List<string>();
        cache.Retain("a", 30, 30, () => released.Add("a")); cache.Retain("b", 30, 30, () => released.Add("b"));
        Assert.True(cache.Take("a")); cache.Retain("a", 30, 30, () => released.Add("a"));
        cache.Retain("c", 50, 50, () => released.Add("c"));
        Assert.Equal(new[] { "b" }, released); cache.Clear(); Assert.Equal(new[] { "b", "a", "c" }, released);
    }

    [Fact]
    public void OversizedEntryNeverTransfersOwnershipOrEvictsUsefulEntries()
    {
        var cache = new ViewportResourceCache(100, 100); int retainedRelease = 0; int rejectedRelease = 0;
        cache.Retain("small", 20, 20, () => retainedRelease++);
        Assert.False(cache.Retain("huge", 101, 1, () => rejectedRelease++));
        Assert.Equal(1, cache.Count); Assert.Equal(0, rejectedRelease); Assert.Equal(0, retainedRelease);
        cache.Clear(); Assert.Equal(1, retainedRelease);
    }

    [Fact]
    public void PressureEvictionStopsImmediatelyWhenAdmissionHasRoom()
    {
        var cache = new ViewportResourceCache(100, 100); long reserved = 90; int released = 0;
        cache.Retain("a", 30, 30, () => { reserved -= 30; released++; });
        cache.Retain("b", 30, 30, () => { reserved -= 30; released++; });
        cache.TrimUntil(() => 40 <= 100 - reserved);
        Assert.Equal(1, released); Assert.Equal(1, cache.Count); Assert.Equal(60, reserved);
        cache.Clear(); Assert.Equal(2, released);
    }

    [Fact]
    public void EntryCountIsBoundedIndependentlyOfByteCounts()
    {
        var cache = new ViewportResourceCache(100, 100, maximumEntries: 2); int released = 0;
        cache.Retain("a", 0, 0, () => released++); cache.Retain("b", 0, 0, () => released++); cache.Retain("c", 0, 0, () => released++);
        Assert.Equal(2, cache.Count); Assert.Equal(1, released); cache.Clear(); Assert.Equal(3, released);
    }

    [Fact]
    public void ClearReleasesEveryEntryEvenWhenOneBackendReportsFailure()
    {
        var cache = new ViewportResourceCache(100, 100); int released = 0;
        cache.Retain("a", 10, 10, () => { released++; throw new IOException("backend failure"); });
        cache.Retain("b", 10, 10, () => released++);
        var failure = Assert.Throws<AggregateException>(cache.Clear);
        Assert.Single(failure.InnerExceptions); Assert.Equal(2, released);
        Assert.Equal(0, cache.Count); Assert.Equal(0, cache.CpuBytes); Assert.Equal(0, cache.GpuBytes); cache.Clear();
    }

    [Fact]
    public void InvalidCostsAndDuplicateUnusedOwnershipAreRejected()
    {
        var cache = new ViewportResourceCache(100, 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.Retain("a", -1, 1, () => { }));
        cache.Retain("a", 1, 1, () => { });
        Assert.Throws<InvalidOperationException>(() => cache.Retain("a", 1, 1, () => { })); cache.Clear();
    }

    [Fact]
    public async Task DeviceInvalidationReleasesRetainedRegistrationAndAcquisitionUsesTheNewGeneration()
    {
        // Exercise the service's real invalidation, lookup and release methods without constructing a Direct3D device.
        var service = (AssetViewportService)RuntimeHelpers.GetUninitializedObject(typeof(AssetViewportService));
        var unused = new ViewportResourceCache(100, 100);
        var entries = new Dictionary<string, AssetViewportService.GeometryEntry>();
        Set("dispatcher", Dispatcher.CurrentDispatcher); Set("unusedGeometries", unused); Set("geometries", entries);
        var budget = new AssetMemoryBudget(); var revision = new SourceRevision("generated", "initial");
        var submesh = new GeometrySubmesh("part", 0, 3, []);
        var primitive = new MeshPrimitive("triangle", PrimitiveTopology.Triangles,
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2], IndexElementFormat.UInt32, [submesh]);
        var mesh = new RenderMesh(new("generated", 1, "mesh"), 0, new(new("generated", "1"), [], [primitive], new Dictionary<string, string>()));
        using var snapshot = new RenderAssetSnapshot(revision, new Dictionary<string, RenderMesh> { [mesh.Key] = mesh }, [],
            new Dictionary<ResourceIdentity, RenderMaterial>(), new(Vector3.Zero, Vector3.One), [], [], new(0, 0, 0, 1, 1, 0), [], budget);
        var first = Registration(0, references: 0); string oldKey = Key(0); entries.Add(oldKey, first.Entry);
        Assert.True(unused.Retain(oldKey, 40, 50, () => Invoke("DestroyGeometry", oldKey, first.Entry)));
        Assert.Equal(48, first.Buffer.UploadedBytes); Assert.Equal(40, budget.UsedBytes); Assert.Equal(50, service.GpuBytes);

        Invoke("OnGeometryDeviceInvalidated", null, EventArgs.Empty);

        Assert.Equal(1, first.Buffer.DisposeCalls); Assert.Equal(0, first.Buffer.UploadedBytes);
        Assert.Empty(entries); Assert.Equal(0, unused.Count); Assert.Equal(0, budget.UsedBytes); Assert.Equal(0, service.GpuBytes);
        Assert.Equal(1, Field<int>("geometryDeviceGeneration"));
        var second = Registration(1, references: 1); string newKey = Key(1); entries.Add(newKey, second.Entry);
        try
        {
            using (var reacquired = await service.AcquireGeometryAsync(snapshot, mesh, primitive, submesh, 0, false,
                CancellationToken.None))
            {
                Assert.Same(second.Entry.Geometry, reacquired.Geometry);
                Assert.NotSame(first.Entry.Geometry, reacquired.Geometry);
                Assert.NotEqual(first.Buffer.RegistrationId, second.Buffer.RegistrationId);
                Assert.Equal(1, second.Entry.DeviceGeneration); Assert.Equal(2, second.Entry.References);
                Assert.Equal(48, second.Buffer.UploadedBytes); Assert.Equal(0, second.Buffer.DisposeCalls);
            }
            Assert.Equal(1, second.Entry.References);
        }
        finally { Invoke("ReleaseGeometry", newKey); }
        Assert.Equal(1, second.Buffer.DisposeCalls); Assert.Empty(entries); Assert.Equal(0, budget.UsedBytes); Assert.Equal(0, service.GpuBytes);

        string Key(int generation) => $"{revision}/{mesh.Key}/{mesh.Pose}/{primitive.Key}/{submesh.Key}/0/False/device/{generation}";
        (AssetViewportService.GeometryEntry Entry, FakeGeometryRegistration Buffer) Registration(int generation, int references)
        {
            var geometry = new ViewportMeshGeometry();
            var buffer = DispatchProxy.Create<IGeometryBufferModel, FakeGeometryRegistration>();
            var fake = (FakeGeometryRegistration)buffer; fake.Geometry = geometry; fake.UploadedData = new byte[48];
            Set("gpuBytes", service.GpuBytes + 50);
            return (new(geometry, budget.Reserve(40, "generated geometry"), new ReleaseAction(() => Set("gpuBytes", service.GpuBytes - 50)),
                buffer, 40, 50, generation, canRetain: false) { References = references }, fake);
        }
        void Set(string name, object value) => typeof(AssetViewportService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, value);
        T Field<T>(string name) => (T)typeof(AssetViewportService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        void Invoke(string name, params object?[] args) => typeof(AssetViewportService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, args);
    }

    public class FakeGeometryRegistration : DispatchProxy
    {
        public Guid RegistrationId { get; } = Guid.NewGuid();
        public MeshGeometry3D? Geometry { get; set; }
        public byte[]? UploadedData { get; set; }
        public int UploadedBytes => UploadedData?.Length ?? 0;
        public int DisposeCalls { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "Dispose": DisposeCalls++; UploadedData = null; return null;
                case "get_GUID": return RegistrationId;
                case "get_Geometry": return Geometry;
                default: throw new InvalidOperationException("The generated registration does not support " + method?.Name);
            }
        }
    }
    private sealed class ReleaseAction(Action release) : IDisposable
    {
        private Action? current = release;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Invoke();
    }
}
