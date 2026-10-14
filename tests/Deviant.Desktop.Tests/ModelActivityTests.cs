using System.Numerics;
using System.IO;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class ModelActivityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothLoadPathsPublishBusyThenReady(bool fromScene) => Run(() =>
    {
        var budget = new AssetMemoryBudget(); using var source = Snapshot(budget);
        var view = new AssetModelView(); var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var events = new List<SceneActivityProgress>();
        view.ActivityProgressChanged += (_, progress) => { Assert.Equal(view.IsLoading, progress.IsActive); events.Add(progress); };
        view.SnapshotLoadAsyncOverride = (_, token) => { entered.TrySetResult(); return release.Task.WaitAsync(token); };
        try
        {
            var load = fromScene ? view.LoadAsync(source, source.Occurrences[0]) : view.LoadAsync(source);
            Pump(entered.Task); Assert.True(view.IsLoading); Assert.True(view.ActivityProgress.IsActive);
            Assert.Equal("Preparing model", view.ActivityProgress.Stage);
            release.TrySetResult(); Pump(load);
            Assert.False(view.IsLoading); Assert.Equal(new(false, "Ready", 0, 0), view.ActivityProgress);
            Assert.Contains(events, e => e.IsActive); Assert.Equal("Ready", events[^1].Stage);
        }
        finally { release.TrySetResult(); Pump(view.DisposeAsync().AsTask()); }
        source.Dispose(); Assert.Equal(0, budget.UsedBytes);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothLoadPathsPublishFailedAndKeepTheTerminalStageDuringCleanup(bool fromScene) => Run(() =>
    {
        var budget = new AssetMemoryBudget(); using var source = Snapshot(budget); var view = new AssetModelView();
        view.SnapshotLoadAsyncOverride = (_, _) => Task.FromException(new InvalidDataException("Generated load failure"));
        try
        {
            var load = fromScene ? view.LoadAsync(source, source.Occurrences[0]) : view.LoadAsync(source);
            Assert.Throws<InvalidDataException>(() => Pump(load));
            Assert.False(view.IsLoading); Assert.Equal("Failed", view.ActivityProgress.Stage);
            Assert.False(view.ActivityProgress.IsActive); Assert.Null(view.CurrentSnapshot);
            Pump(view.UnloadAsync()); Assert.Equal("Failed", view.ActivityProgress.Stage);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
        source.Dispose(); Assert.Equal(0, budget.UsedBytes);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothLoadPathsPublishCancelledAfterTheirWorkDrains(bool fromScene) => Run(() =>
    {
        var budget = new AssetMemoryBudget(); using var source = Snapshot(budget); var view = new AssetModelView();
        var entered = new TaskCompletionSource(); using var cancellation = new CancellationTokenSource();
        view.SnapshotLoadAsyncOverride = (_, token) => { entered.TrySetResult(); return Task.Delay(Timeout.Infinite, token); };
        try
        {
            var load = fromScene ? view.LoadAsync(source, source.Occurrences[0], cancellation.Token) : view.LoadAsync(source, cancellation.Token);
            Pump(entered.Task); Assert.True(view.IsLoading);
            view.CancelActivity(); Assert.ThrowsAny<OperationCanceledException>(() => Pump(load));
            Assert.False(cancellation.IsCancellationRequested);
            Assert.False(view.IsLoading); Assert.Equal(new(false, "Cancelled", 0, 0), view.ActivityProgress);
            Assert.Null(view.CurrentSnapshot);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
        source.Dispose(); Assert.Equal(0, budget.UsedBytes);
    });

    [Fact]
    public void ReplacingALoadDoesNotPublishAStaleTerminalState() => Run(() =>
    {
        var budget = new AssetMemoryBudget(); using var first = Snapshot(budget); using var second = Snapshot(budget);
        var view = new AssetModelView(); var entered = new TaskCompletionSource(); var calls = 0;
        var events = new List<SceneActivityProgress>();
        view.ActivityProgressChanged += (_, progress) => events.Add(progress);
        view.SnapshotLoadAsyncOverride = (_, token) =>
        {
            if (++calls != 1) return Task.CompletedTask;
            entered.TrySetResult(); return Task.Delay(Timeout.Infinite, token);
        };
        try
        {
            var old = view.LoadAsync(first); Pump(entered.Task);
            var next = view.LoadAsync(second); Assert.ThrowsAny<OperationCanceledException>(() => Pump(old)); Pump(next);
            Assert.Same(second, view.CurrentSnapshot); Assert.False(view.IsLoading);
            Assert.DoesNotContain(events, e => !e.IsActive && e.Stage is "Cancelled" or "Failed");
            Assert.Equal("Ready", events[^1].Stage);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
        first.Dispose(); second.Dispose(); Assert.Equal(0, budget.UsedBytes);
    });

    [Fact]
    public void ModelChangeCancelsPendingAnimationAndDrainsItBeforeReleasingSources() => Run(() =>
    {
        var budget = new AssetMemoryBudget(); using var source = Snapshot(budget); var view = new AssetModelView();
        view.SnapshotLoadAsyncOverride = (_, _) => Task.CompletedTask;
        var entered = new TaskCompletionSource(); bool drained = false, released = false;
        var retention = new ActionLease(() => { Assert.True(drained); released = true; });
        var clip = new AssetDescriptor(new(source.Revision.IndexContentId, 9), source.Revision, "Animations", "Motion", null,
            AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.Preview, [], []);
        try
        {
            Pump(view.LoadAsync(source));
            var preparing = view.SetAnimationAsync(clip, 2, async (_, _, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); }
                finally { drained = true; }
            }, _ => Task.CompletedTask, retention);
            Pump(entered.Task);
            var unload = view.UnloadAsync(); Pump(unload);
            Assert.ThrowsAny<OperationCanceledException>(() => Pump(preparing));
            Assert.True(released); Assert.Null(view.CurrentSnapshot);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
        source.Dispose(); Assert.Equal(0, budget.UsedBytes);
    });

    private sealed class ActionLease(Action release) : IDisposable
    { private Action? action = release; public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke(); }

    private static RenderAssetSnapshot Snapshot(AssetMemoryBudget budget)
    {
        var identity = new ResourceIdentity(new string('a', 64), 7, "generated-mesh");
        var bounds = new GeometryBounds(Vector3.Zero, Vector3.One);
        var primitive = new MeshPrimitive("triangle", PrimitiveTopology.Triangles,
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0u, 1u, 2u], IndexElementFormat.UInt16, [], Bounds: bounds);
        var geometry = new GeometryAsset(new("generated", "1"), [], [primitive], new Dictionary<string, string>());
        var mesh = new RenderMesh(identity, 0, geometry);
        var occurrence = new RenderOccurrence("placement", identity, identity, "Generated mesh", mesh.Key,
            Matrix4x4.Identity, bounds, [identity]) { PlacementOrigin = RenderPlacementOrigin.ModelLocal, ModelPlacement = new(identity, 0) };
        return new(new(identity.IndexContentId, "generated-revision"), new Dictionary<string, RenderMesh> { [mesh.Key] = mesh },
            [occurrence], new Dictionary<ResourceIdentity, RenderMaterial>(), bounds, [], [], new(1, 1, 0, 1, 0, 0), [], budget,
            [budget.Reserve(4096, "generated snapshot")]);
    }

    private static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); } catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Model activity check timed out.");
        if (failure is not null) throw new InvalidOperationException("Model activity check failed.", failure);
    }

    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
            task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
}
