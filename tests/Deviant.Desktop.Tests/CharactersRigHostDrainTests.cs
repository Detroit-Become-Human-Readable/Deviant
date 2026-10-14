using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class CharactersRigHostDrainTests
{
    [Fact]
    public void SelectingCharacterDoesNotStartRigProvider()
    {
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            var descriptor = OpenCharacters(window, fixture);
            using var rig = new PendingRigProvider();
            window.CharactersBrowser.RigProvider = rig.Invoke;
            ConfigureBindingProvider(window);

            Entries(window).SelectedIndex = 0;
            WaitUntil(() => window.CharactersBrowser.ModelBinding?.SelectedIdentity == descriptor.Identity);

            Assert.Equal(0, rig.CallCount);
            Assert.False(RigExpander(window).IsExpanded);
        });
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("cache")]
    [InlineData("close")]
    public void ExpandingRigStartsLazyProviderAndHostDrainWaitsForCancellation(string drainKind) =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            OpenCharacters(window, fixture);
            ConfigureBindingProvider(window);
            using var rig = new PendingRigProvider();
            window.CharactersBrowser.RigProvider = rig.Invoke;
            var clearedCount = 0;
            window.CharactersBrowser.RigCleared += (_, _) => clearedCount++;

            Entries(window).SelectedIndex = 0;
            WaitUntil(() => window.CharactersBrowser.ModelBinding is not null);
            RigExpander(window).IsExpanded = true;
            Pump(rig.Entered.Task);
            Assert.Equal(1, rig.CallCount);
            Assert.False(rig.Token.IsCancellationRequested);

            Task drainTask;
            string? replacementPath = null;
            var clearedBeforeDrain = clearedCount;
            if (drainKind == "workspace")
            {
                replacementPath = Path.Combine(fixture.Root, "replacement", "archive.idx");
                DiscoveryFixture.WriteIndex(replacementPath, [(41u, new byte[] { 1, 2, 3 })]);
                drainTask = window.OpenArchiveAsync(replacementPath);
                WaitUntil(() => Field<int>(window, "workspaceTransitionDepth") > 0);
            }
            else if (drainKind == "cache")
                drainTask = (Task)Invoke(window, "DrainCacheUsersAsync", CancellationToken.None)!;
            else
            {
                window.Close();
                drainTask = WindowTestLifetime.WaitClosedAsync(window);
            }

            WaitUntil(() => rig.Token.IsCancellationRequested);
            Assert.False(drainTask.IsCompleted);
            WaitUntil(() => clearedCount > clearedBeforeDrain);
            Assert.Empty(RigNodes(window).Items);

            rig.ReleaseAfterCancellation();
            Pump(drainTask);
            WaitUntil(() => RigTasks(window).Count == 0);
            Assert.Equal(1, rig.CallCount);
            Assert.Empty(RigNodes(window).Items);
            Assert.False(RigExpander(window).IsExpanded);
            Assert.True(clearedCount > 0);

            if (replacementPath is not null)
                Assert.Equal(Path.GetFullPath(replacementPath), window.CurrentSession!.Snapshot.IndexPath);
            else if (drainKind == "cache")
                Assert.Null(window.CurrentSession);
            else Assert.True(WindowTestLifetime.IsClosed(window));
        });

    private static AssetDescriptor OpenCharacters(AudioWindow window, DiscoveryFixture fixture)
    {
        Pump(window.OpenArchiveAsync(fixture.Index));
        var snapshot = window.CurrentSession!.Snapshot;
        var descriptor = new AssetDescriptor(new(snapshot.IndexContentId, 0, "generated/model-entry"),
            new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), "character-model", "Generated model",
            "generated-group", AssetAvailability.Available, AssetCompleteness.Complete,
            AssetCapabilities.Preview, [new NameEvidence("Generated model", NameOrigin.Stored, "generated descriptor")], []);
        window.CharacterCatalogProvider = (source, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Same(snapshot, source);
            return Task.FromResult<IReadOnlyList<AssetDescriptor>>([descriptor]);
        };
        Pump(window.ShowCharactersAsync());
        return descriptor;
    }

    private static void ConfigureBindingProvider(AudioWindow window) =>
        window.CharacterBindingsProvider = (selected, budget, token) =>
        {
            token.ThrowIfCancellationRequested();
            var retention = budget.Reserve(512, "generated binding metadata");
            return Task.FromResult<(IReadOnlyList<CharacterModelBindingResult> Bindings, IDisposable Retention)>(
                ([Binding(selected)], retention));
        };

    private static CharacterModelBindingResult Binding(AssetDescriptor descriptor)
    {
        var owner = descriptor.Identity with { MemberPath = "generated/container" };
        var catalog = descriptor.Identity with { MemberPath = "generated/catalog" };
        var mesh = new ResourceIdentity(descriptor.Identity.IndexContentId, descriptor.Identity.Ordinal + 1,
            "generated/mesh");
        var lod = new CharacterMeshLodBinding(0, 0.5f,
            new(IdentifierNamespace.Native, "generated-mesh", owner),
            new(ReferenceStatus.Unique, [mesh]), mesh, 0, 1, null,
            new NativeModelSelection(mesh, 0), new(owner, 0, 16), null, null, null);
        return new(descriptor.Identity, catalog, owner, descriptor.Revision, 0, 0, null, 0,
            null, null, new(ReferenceStatus.Unique, [owner]), [], [], [lod], null, null);
    }

    private static ListView Entries(AudioWindow window) =>
        (ListView)window.CharactersBrowser.FindName("Entries")!;

    private static Expander RigExpander(AudioWindow window) =>
        (Expander)window.CharactersBrowser.FindName("RigExpander")!;

    private static ItemsControl RigNodes(AudioWindow window) =>
        (ItemsControl)window.CharactersBrowser.FindName("RigNodes")!;

    private static HashSet<Task> RigTasks(AudioWindow window) =>
        (HashSet<Task>)typeof(CharactersView).GetField("rigTasks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window.CharactersBrowser)!;

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);

    private static void WaitUntil(Func<bool> ready)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!ready())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("The rig host did not settle.");
            Pump(Task.Delay(10));
        }
    }

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private sealed class PendingRigProvider : IDisposable
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationToken token;

        public TaskCompletionSource Entered => entered;
        public int CallCount { get; private set; }
        public CancellationToken Token => token;

        public async Task<NativeSkeletonBindingSession.NativeSkeletonBindingLease> Invoke(
            AssetDescriptor selected, CharacterModelBindingResult binding, CancellationToken cancellationToken)
        {
            CallCount++;
            token = cancellationToken;
            entered.TrySetResult();
            await released.Task;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The generated rig provider was released without cancellation.");
        }

        public void ReleaseAfterCancellation()
        {
            if (!token.IsCancellationRequested)
                throw new InvalidOperationException("The rig provider must be cancelled before it is released.");
            released.TrySetResult();
        }

        public void Dispose()
        {
            if (!released.Task.IsCompleted) released.TrySetResult();
        }
    }
}
