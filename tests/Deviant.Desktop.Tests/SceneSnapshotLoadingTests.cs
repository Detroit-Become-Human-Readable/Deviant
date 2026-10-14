using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using HelixToolkit.Wpf.SharpDX;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class SceneSnapshotLoadingTests
{
    [Fact]
    public void DefaultLoaderUsesTheViewportAndRequiresItsService() => Run(() =>
    {
        var view = new SceneView();
        var budget = new AssetMemoryBudget();
        using var snapshot = Snapshot(budget);
        try
        {
            Assert.Null(view.SnapshotLoadAsyncOverride);
            var failure = Assert.Throws<InvalidOperationException>(() =>
                Pump(view.LoadViewportSnapshotAsync(snapshot, CancellationToken.None)));
            Assert.Contains("viewport service", failure.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void OverrideReceivesTheExactSnapshotAndCancellationToken() => Run(() =>
    {
        var view = new SceneView();
        var budget = new AssetMemoryBudget();
        using var snapshot = Snapshot(budget);
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        try
        {
            view.SnapshotLoadAsyncOverride = (actual, token) =>
            {
                calls++;
                Assert.Same(snapshot, actual);
                Assert.Equal(cancellation.Token, token);
                return Task.CompletedTask;
            };
            Pump(view.LoadViewportSnapshotAsync(snapshot, cancellation.Token));
            Assert.Equal(1, calls);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void OverrideCancellationPropagatesToTheCaller() => Run(() =>
    {
        var view = new SceneView();
        using var snapshot = Snapshot(new AssetMemoryBudget());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            view.SnapshotLoadAsyncOverride = (_, token) => Task.FromCanceled(token);
            var task = view.LoadViewportSnapshotAsync(snapshot, cancellation.Token);
            Assert.ThrowsAny<OperationCanceledException>(() => Pump(task));
            Assert.True(task.IsCanceled);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void SharedGeometryPacketsPreserveGuidsSelectionVisibilityAndFinalMaterialInstances() => Run(() =>
    {
        var budget = new AssetMemoryBudget(); using var service = new AssetViewportService();
        var viewport = new AssetViewport(); viewport.SetService(service);
        viewport.Measure(new Size(800, 600)); viewport.Arrange(new Rect(0, 0, 800, 600));
        using var first = InstancedSnapshot(budget, [("first", 10f)]);
        using var second = InstancedSnapshot(budget, [("second", 20f)]);
        using var complete = InstancedSnapshot(budget, [("first", 12f), ("second", 20f), ("third", 30f)], material: true);
        try
        {
            Pump(viewport.AppendSnapshotAsync(first));
            var original = Model(viewport); var firstId = Assert.Single(original.InstanceIdentifiers!);
            var originalMaterial = original.Material;
            Assert.Equal(10f, Assert.Single(original.Instances!).M41);
            viewport.SetSelection("first"); viewport.SetVisibility("first", false);
            Assert.Empty(original.InstanceIdentifiers!);

            Pump(viewport.AppendSnapshotAsync(second));
            Assert.Same(original, Model(viewport));
            Assert.Equal(2, viewport.RenderedOccurrenceCount); Assert.Equal(2, viewport.RenderedPartCount);
            Assert.Equal(1, viewport.VisiblePartCount); Assert.False(viewport.IsOccurrenceManuallyVisible("first"));
            Assert.Equal("first", viewport.SelectedOccurrenceKey);
            var secondId = Assert.Single(original.InstanceIdentifiers!);
            Assert.Equal(20f, Assert.Single(original.Instances!).M41);
            long cpu = budget.UsedBytes, gpu = service.GpuBytes;
            Pump(viewport.AppendSnapshotAsync(second));
            Assert.Equal(cpu, budget.UsedBytes); Assert.Equal(gpu, service.GpuBytes);
            Assert.Equal(secondId, Assert.Single(original.InstanceIdentifiers!));
            Assert.Equal(2, viewport.RenderedPartCount);

            viewport.SetVisibility("first", true);
            Assert.Equal(new[] { firstId, secondId }, original.InstanceIdentifiers!);
            viewport.SetVisibility("first", false);
            Pump(viewport.CompleteSnapshotAsync(complete));
            var finished = Model(viewport);
            Assert.NotSame(original, finished); Assert.NotSame(originalMaterial, finished.Material);
            Assert.Equal(3, viewport.RenderedOccurrenceCount); Assert.Equal(3, viewport.RenderedPartCount);
            Assert.Equal(2, viewport.VisiblePartCount); Assert.False(viewport.IsOccurrenceManuallyVisible("first"));
            Assert.Equal("first", viewport.SelectedOccurrenceKey);
            Assert.Contains(new RenderMeshSelection("first"), viewport.SelectedMeshes);
            Assert.Equal(secondId, finished.InstanceIdentifiers![0]);
            Assert.Equal(new[] { 20f, 30f }, finished.Instances!.Select(matrix => matrix.M41));
            viewport.SetVisibility("first", true);
            Assert.Equal(firstId, finished.InstanceIdentifiers![0]); Assert.Equal(secondId, finished.InstanceIdentifiers![1]);
            Assert.Equal(3, finished.InstanceIdentifiers.Count); Assert.Equal(3, finished.InstanceIdentifiers.Distinct().Count());
            Assert.Equal(new[] { 12f, 20f, 30f }, finished.Instances!.Select(matrix => matrix.M41));

            Pump(viewport.UnloadAsync()); service.ReleaseUnusedResources();
            Assert.Equal(0, budget.UsedBytes); Assert.Equal(0, service.GpuBytes);
            Assert.Empty((System.Collections.IDictionary)typeof(AssetViewport).GetField("streamedInstanceAllocations", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewport)!);
        }
        finally { Pump(viewport.DisposeAsync().AsTask()); service.ReleaseUnusedResources(); }
    });

    [Fact]
    public void InstanceAdmissionFailureLeavesExistingBuffersGuidsAndReservationsIntact() => Run(() =>
    {
        var budget = new AssetMemoryBudget(); using var service = new AssetViewportService();
        var viewport = new AssetViewport(); viewport.SetService(service);
        viewport.Measure(new Size(800, 600)); viewport.Arrange(new Rect(0, 0, 800, 600));
        using var first = InstancedSnapshot(budget, [("first", 10f)]);
        using var second = InstancedSnapshot(budget, [("second", 20f)]);
        try
        {
            Pump(viewport.AppendSnapshotAsync(first)); viewport.SetSelection("first");
            var original = Model(viewport); var id = Assert.Single(original.InstanceIdentifiers!);
            using (budget.Reserve(budget.MaximumBytes - budget.UsedBytes - (4096 + 2 * 128 + 500), "generated admission pressure"))
            {
                long cpu = budget.UsedBytes, gpu = service.GpuBytes;
                var task = (Task)typeof(AssetViewport).GetMethod("ReconcileGeometryInstancesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(viewport, [second, second.Meshes.Keys.Single(), second.Occurrences, CancellationToken.None])!;
                var error = Assert.Throws<ArchiveException>(() => Pump(task)); Assert.Equal("AssetMemoryLimit", error.Code);
                Assert.Equal(cpu, budget.UsedBytes); Assert.Equal(gpu, service.GpuBytes);
                Assert.Equal(id, Assert.Single(original.InstanceIdentifiers!)); Assert.Equal(10f, Assert.Single(original.Instances!).M41);
                Assert.Equal(1, viewport.RenderedOccurrenceCount); Assert.Equal("first", viewport.SelectedOccurrenceKey);
            }
            Pump(viewport.UnloadAsync()); service.ReleaseUnusedResources();
            Assert.Equal(0, budget.UsedBytes); Assert.Equal(0, service.GpuBytes);
        }
        finally { Pump(viewport.DisposeAsync().AsTask()); service.ReleaseUnusedResources(); }
    });

    private static InstancingMeshGeometryModel3D Model(AssetViewport viewport)
        => Assert.Single(viewport.Viewport.Items.OfType<InstancingMeshGeometryModel3D>());

    [Fact]
    public void SmallGeometryAppendDoesNotWaitForAnotherPresentationFrame() => Run(() =>
    {
        var budget = new AssetMemoryBudget(); using var service = new AssetViewportService();
        var viewport = new AssetViewport(); viewport.SetService(service);
        viewport.Measure(new Size(800, 600)); viewport.Arrange(new Rect(0, 0, 800, 600));
        using var first = InstancedSnapshot(budget, [("first", 10f)]);
        using var second = InstancedSnapshot(budget, [("second", 20f)], meshOrdinal: 2);
        var observer = new FrameWaitObserver();
        using var subscription = AssetLoadDiagnostics.Listener.Subscribe(observer);
        try
        {
            Pump(viewport.AppendSnapshotAsync(first));
            Assert.True(observer.Count > 0);
            observer.Count = 0;
            Pump(viewport.AppendSnapshotAsync(second));
            Assert.Equal(0, observer.Count);
            Assert.Equal(2, viewport.RenderedPartCount);
            Assert.Equal(2, viewport.RenderedOccurrenceCount);
            Pump(viewport.UnloadAsync()); service.ReleaseUnusedResources();
            Assert.Equal(0, budget.UsedBytes); Assert.Equal(0, service.GpuBytes);
        }
        finally { Pump(viewport.DisposeAsync().AsTask()); service.ReleaseUnusedResources(); }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ChangedGeometryGroupsSharePresentationAndRetainBuffersUntilThatFrame(bool pressure, bool cancel) => Run(() =>
    {
        var budget = new AssetMemoryBudget(); using var service = new AssetViewportService();
        var viewport = new AssetViewport(); viewport.SetService(service);
        viewport.Measure(new Size(800, 600)); viewport.Arrange(new Rect(0, 0, 800, 600));
        using var first = PairedSnapshot(budget, 10, includeSecond: false);
        using var second = PairedSnapshot(budget, 10, includeSecond: true);
        using var moved = PairedSnapshot(budget, 12, includeSecond: true);
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        IDisposable? competingMemory = null;
        var waitField = typeof(AssetViewport).GetField("frameWaitAsyncOverride", BindingFlags.Instance | BindingFlags.NonPublic)!;
        waitField.SetValue(viewport, (Func<CancellationToken, Task>)(_ => Task.CompletedTask));
        try
        {
            Pump(viewport.AppendSnapshotAsync(first));
            Pump(viewport.AppendSnapshotAsync(second));
            viewport.SetSelection("a-first"); viewport.SetVisibility("a-first", false);
            var models = viewport.Viewport.Items.OfType<InstancingMeshGeometryModel3D>().ToArray();
            var ids = models.Select(model => model.InstanceIdentifiers!.ToArray()).ToArray();
            var allocationMap = (System.Collections.IDictionary)typeof(AssetViewport)
                .GetField("streamedInstanceAllocations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!;
            var previous = allocationMap.Values.Cast<object>().ToArray();
            Assert.Equal(2, previous.Length);
            int waits = 0;
            if (pressure) competingMemory = budget.Reserve(budget.MaximumBytes - budget.UsedBytes - 6000,
                "generated competing work");
            waitField.SetValue(viewport, (Func<CancellationToken, Task>)(token =>
            {
                waits++;
                if (waits == 1)
                    foreach (var allocation in previous) Assert.Equal(0, AllocationDisposed(allocation));
                return pressure ? Task.CompletedTask : frame.Task.WaitAsync(token);
            }));
            var loading = cancel ? viewport.CompleteSnapshotAsync(moved, cancellation.Token)
                : viewport.AppendSnapshotAsync(moved, cancellation.Token);
            if (pressure)
            {
                Pump(loading);
                Assert.Equal(2, waits); // One pressure drain, then the remaining group.
            }
            else
            {
                Assert.Equal(1, waits);
                Assert.False(loading.IsCompleted);
                Assert.Equal("a-first", viewport.SelectedOccurrenceKey);
                Assert.False(viewport.IsOccurrenceManuallyVisible("a-first"));
                Assert.Equal(3, viewport.VisiblePartCount);
                for (int index = 0; index < models.Length; index++)
                    Assert.Equal(ids[index], models[index].InstanceIdentifiers!.ToArray());
                if (cancel)
                {
                    cancellation.Cancel();
                    Assert.ThrowsAny<OperationCanceledException>(() => Pump(loading));
                    foreach (var allocation in previous) Assert.Equal(0, AllocationDisposed(allocation));
                    Assert.Equal(4, viewport.RenderedOccurrenceCount);
                }
                else
                {
                    frame.SetResult(); Pump(loading);
                    Assert.Equal(1, waits);
                }
            }
            if (!cancel) foreach (var allocation in previous) Assert.Equal(1, AllocationDisposed(allocation));
            competingMemory?.Dispose(); competingMemory = null;
            Pump(viewport.UnloadAsync()); service.ReleaseUnusedResources();
            Assert.Equal(0, budget.UsedBytes); Assert.Equal(0, service.GpuBytes);
            Assert.Empty(allocationMap);
        }
        finally
        {
            frame.TrySetResult(); competingMemory?.Dispose();
            Pump(viewport.DisposeAsync().AsTask()); service.ReleaseUnusedResources();
        }
    });

    private static int AllocationDisposed(object allocation) =>
        (int)allocation.GetType().GetField("disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(allocation)!;

    private static RenderAssetSnapshot PairedSnapshot(AssetMemoryBudget budget, float position, bool includeSecond)
    {
        using var a = InstancedSnapshot(budget, includeSecond ? [("a-first", position), ("a-second", 20f)] : [("a-first", position)]);
        using var b = InstancedSnapshot(budget, includeSecond ? [("b-first", position), ("b-second", 20f)] : [("b-first", position)], meshOrdinal: 3);
        return new(a.Revision, a.Meshes.Concat(b.Meshes).ToDictionary(row => row.Key, row => row.Value),
            [.. a.Occurrences, .. b.Occurrences], new Dictionary<ResourceIdentity, RenderMaterial>(),
            new(Vector3.Zero, new(30, 1, 1)), [], [], new(4, 4, 0, 4, 2, 2), [], budget);
    }

    private sealed class FrameWaitObserver : IObserver<KeyValuePair<string, object?>>
    {
        public int Count;
        public void OnNext(KeyValuePair<string, object?> item)
        {
            if (item.Value is AssetLoadMeasurement { Stage: "UploadFrameWait" }) Count++;
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private static RenderAssetSnapshot InstancedSnapshot(AssetMemoryBudget budget, (string Key, float X)[] placements, bool material = false,
        long meshOrdinal = 1)
    {
        var revision = new SourceRevision("generated", "initial"); var meshIdentity = new ResourceIdentity("generated", meshOrdinal);
        var materialIdentity = new ResourceIdentity("generated", 2);
        var bounds = new GeometryBounds(Vector3.Zero, new(1, 1, 0));
        var primitive = new MeshPrimitive("triangle", PrimitiveTopology.Triangles, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            [0, 1, 2], IndexElementFormat.UInt16, [], Bounds: bounds);
        var mesh = new RenderMesh(meshIdentity, 0, new(new("generated", "1"), [], [primitive], new Dictionary<string, string>()),
            new Dictionary<string, ResourceIdentity> { [primitive.Key] = materialIdentity });
        var rows = placements.Select((placement, index) => new RenderOccurrence(placement.Key, new("generated", 10 + index),
            new("generated", 20 + index), placement.Key, mesh.Key, Matrix4x4.CreateTranslation(placement.X, 0, 0),
            new(new(placement.X, 0, 0), new(placement.X + 1, 1, 0)), [])).ToArray();
        var materials = new Dictionary<ResourceIdentity, RenderMaterial>();
        if (material) materials.Add(materialIdentity, new(new(materialIdentity, revision, "generated-material-v1", [], [], []),
            new Dictionary<int, RenderTexture>()));
        return new(revision, new Dictionary<string, RenderMesh> { [mesh.Key] = mesh }, rows, materials,
            new(new(placements.Min(row => row.X), 0, 0), new(placements.Max(row => row.X) + 1, 1, 0)), [], [],
            new(rows.Length, rows.Length, 0, rows.Length, 1, 1), [], budget);
    }

    private static RenderAssetSnapshot Snapshot(AssetMemoryBudget budget) =>
        new(new(new string('a', 64), "generated-revision"), new Dictionary<string, RenderMesh>(), [],
            new Dictionary<ResourceIdentity, RenderMaterial>(), new(Vector3.Zero, Vector3.One), [], [],
            new(0, 0, 0, 0, 0, 0), [], budget);

    private static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Snapshot loader check timed out.");
        if (failure is not null) throw new InvalidOperationException("Snapshot loader check failed.", failure);
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
