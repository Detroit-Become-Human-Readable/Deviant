using System.Reflection;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class SceneReturnCancellationTests
{
    [Fact]
    public void CanceledReturnDisposesAFactoryResultThatArrivesLate()
    {
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            Pump(window.OpenArchiveAsync(fixture.Index));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var result = new TaskCompletionSource<ISceneCatalogSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            var created = new LateSceneSession(window.CurrentSession!.Snapshot);
            window.SceneSessionFactory = (_, _, _, _, _) => { entered.TrySetResult(); return result.Task; };
            using var caller = new CancellationTokenSource();
            var back = Return(window, caller.Token);
            Pump(entered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            caller.Cancel();
            result.SetResult(created);
            Pump(Task.WhenAny(back, Task.Delay(TimeSpan.FromSeconds(5))));
            Assert.True(back.IsCompleted);
            Assert.True(created.Disposed, "A late canceled scene factory result was retained.");
            Assert.Null(Field<ISceneCatalogSession?>(window, "sceneSession"));
            Assert.ThrowsAny<OperationCanceledException>(() => Pump(back));
        });
    }

    [Fact]
    public void PreCanceledReturnDoesNotReplaceCurrentNavigationAndLegacyEntryRemains()
    {
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            var generation = Field<long>(window, "navigationGeneration");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var task = Return(window, canceled.Token);
            Assert.ThrowsAny<OperationCanceledException>(() => Pump(task));
            Assert.Equal(generation, Field<long>(window, "navigationGeneration"));
            Assert.NotNull(typeof(AudioWindow).GetMethod(nameof(AudioWindow.BackToSceneAsync), Type.EmptyTypes));
        });
    }

    [Fact]
    public void CallerCancellationReachesTheSceneLoadAndUnwindsTheReturn()
    {
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            Pump(window.OpenArchiveAsync(fixture.Index));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken received = default;
            window.SceneSessionFactory = async (_, _, _, _, token) =>
            {
                received = token;
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The canceled factory cannot publish a scene.");
            };
            using var caller = new CancellationTokenSource();
            var back = Return(window, caller.Token);
            Pump(entered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(back.IsCompleted);
            caller.Cancel();
            Pump(Task.WhenAny(back, Task.Delay(TimeSpan.FromSeconds(5))));
            Assert.True(back.IsCompleted, "Scene return did not unwind caller cancellation.");
            Assert.True(received.IsCancellationRequested);
            Assert.ThrowsAny<OperationCanceledException>(() => Pump(back));
            Assert.Null(window.SceneBrowser.CurrentSession);
        });
    }

    private static Task Return(AudioWindow window, CancellationToken token)
    {
        var method = typeof(AudioWindow).GetMethod(nameof(AudioWindow.BackToSceneAsync),
            BindingFlags.Instance | BindingFlags.Public, null, [typeof(CancellationToken)], null);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method.Invoke(window, [token]));
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;

    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private sealed class LateSceneSession(ArchiveSnapshot snapshot) : ISceneCatalogSession
    {
        public bool Disposed { get; private set; }
        public ArchiveSnapshot Snapshot => snapshot;
        public SourceRevision Revision => new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        public SceneDiscoveryProgress Progress => new(false, 0, 0, 0, false);
        public event EventHandler? Updated { add { } remove { } }
        public Task DiscoverMoreAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<SceneCatalogPage> QueryAsync(string? search = null, long offset = 0, int limit = 100,
            CancellationToken token = default) => Task.FromResult(new SceneCatalogPage([], 0, offset));
        public Task<SceneLoadedSection> LoadAsync(ResourceIdentity identity, CancellationToken token = default) =>
            Task.FromException<SceneLoadedSection>(new InvalidOperationException("The canceled scene cannot load."));
        public Task RevalidateAsync(SceneLoadedSection section, CancellationToken token = default) => Task.CompletedTask;
        public Task ExportMetadataAsync(SceneLoadedSection section, string destination, CancellationToken token = default) => Task.CompletedTask;
        public void CancelDiscovery() { }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
