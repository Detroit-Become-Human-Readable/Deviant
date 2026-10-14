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
public sealed class CharactersPoseViewTests
{
    [Fact]
    public void PoseActionStaysHiddenWithoutProviderAndRunsOnlyOnExplicitClick() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            var descriptor = OpenCharacters(window, fixture, count: 1)[0];
            ConfigureBindingProvider(window);
            var view = window.CharactersBrowser;
            var button = Named<Button>(view, "LoadPoseButton");
            Named<ListView>(view, "Entries").SelectedIndex = 0;
            WaitUntil(() => view.ModelBinding is not null);
            Assert.Equal(Visibility.Collapsed, button.Visibility);

            var calls = new List<(AssetDescriptor Descriptor, CharacterModelBindingResult Binding, CharacterMeshLodBinding Lod)>();
            view.PoseImportProvider = (selected, binding, lod, token) =>
            {
                token.ThrowIfCancellationRequested();
                calls.Add((selected, binding, lod));
                return Task.CompletedTask;
            };
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.Empty(calls);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitUntil(() => Named<TextBlock>(view, "PreviewStatus").Text == "Preview ready");
            var call = Assert.Single(calls);
            Assert.Same(descriptor, call.Descriptor);
            Assert.Same(view.ModelBinding, call.Binding);
            Assert.Same(view.SelectedModelLod, call.Lod);
        });

    [Fact]
    public void SelectionChangeCancelsPoseImportAndIgnoresItsLateCompletion() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            var descriptors = OpenCharacters(window, fixture, count: 2);
            ConfigureBindingProvider(window);
            var view = window.CharactersBrowser;
            var entries = Named<ListView>(view, "Entries");
            using var pose = new PendingPose();
            view.PoseImportProvider = pose.Invoke;
            entries.SelectedIndex = 0;
            WaitUntil(() => view.ModelBinding?.SelectedIdentity == descriptors[0].Identity);

            Named<Button>(view, "LoadPoseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(pose.Entered);
            Assert.Same(descriptors[0], pose.Descriptor);
            Assert.Same(view.ModelBinding, pose.Binding);
            Assert.Same(view.SelectedModelLod, pose.Lod);

            entries.SelectedIndex = 1;
            Assert.True(pose.Token.IsCancellationRequested);
            WaitUntil(() => view.ModelBinding?.SelectedIdentity == descriptors[1].Identity);
            pose.Release();
            Pump(pose.Completed);
            WaitUntil(() => PreviewTasks(view).Count == 0);

            Assert.Equal("", Named<TextBlock>(view, "PreviewStatus").Text);
            Assert.Equal(Visibility.Visible, Named<Button>(view, "LoadPoseButton").Visibility);
        });

    private static AssetDescriptor[] OpenCharacters(AudioWindow window, DiscoveryFixture fixture, int count)
    {
        Pump(window.OpenArchiveAsync(fixture.Index));
        var snapshot = window.CurrentSession!.Snapshot;
        var descriptors = Enumerable.Range(0, count).Select(index => new AssetDescriptor(
            new(snapshot.IndexContentId, index, $"generated/model-entry/{index}"),
            new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), "character-model",
            $"Generated model {index + 1}", "generated-group", AssetAvailability.Available,
            AssetCompleteness.Complete, AssetCapabilities.Preview,
            [new NameEvidence($"Generated model {index + 1}", NameOrigin.Stored, "generated descriptor")], [])).ToArray();
        window.CharacterCatalogProvider = (source, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Same(snapshot, source);
            return Task.FromResult<IReadOnlyList<AssetDescriptor>>(descriptors);
        };
        Pump(window.ShowCharactersAsync());
        return descriptors;
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
        return new(descriptor.Identity, catalog, owner, descriptor.Revision, 2013, 0, null, 0,
            null, null, new(ReferenceStatus.Unique, [owner]), [], [], [lod], null, null);
    }

    private static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(root.FindName(name));

    private static HashSet<Task> PreviewTasks(CharactersView view) =>
        (HashSet<Task>)typeof(CharactersView).GetField("previewTasks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(view)!;

    private static void WaitUntil(Func<bool> ready)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!ready())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("The pose view did not settle.");
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

    private sealed class PendingPose : IDisposable
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => entered.Task;
        public Task Completed => completed.Task;
        public AssetDescriptor? Descriptor { get; private set; }
        public CharacterModelBindingResult? Binding { get; private set; }
        public CharacterMeshLodBinding? Lod { get; private set; }
        public CancellationToken Token { get; private set; }

        public async Task Invoke(AssetDescriptor descriptor, CharacterModelBindingResult binding,
            CharacterMeshLodBinding lod, CancellationToken token)
        {
            Descriptor = descriptor;
            Binding = binding;
            Lod = lod;
            Token = token;
            entered.TrySetResult();
            await released.Task;
            completed.TrySetResult();
        }

        public void Release() => released.TrySetResult();
        public void Dispose() => released.TrySetResult();
    }
}
