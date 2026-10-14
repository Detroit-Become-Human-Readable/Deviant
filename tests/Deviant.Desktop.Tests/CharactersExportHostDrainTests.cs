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
public sealed class CharactersExportHostDrainTests
{
    [Theory]
    [InlineData("workspace")]
    [InlineData("cache")]
    [InlineData("close")]
    public void HostDrainCancelsPendingExportWaitsAndIgnoresLateCompletion(string drainKind) =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            var descriptor = OpenCharacters(window, fixture);
            var bindingRetention = ConfigureBindingProvider(window);
            var view = window.CharactersBrowser;
            Named<ListView>(view, "Entries").SelectedIndex = 0;
            WaitUntil(() => view.ModelBinding?.SelectedIdentity == descriptor.Identity);
            var binding = view.ModelBinding!;
            var lod = view.SelectedModelLod!;

            using var provider = new PendingExportProvider();
            view.ExportProvider = provider.Invoke;
            var exportButton = Named<Button>(view, "ExportFbxButton");
            Assert.Equal(Visibility.Visible, exportButton.Visibility);
            Assert.True(exportButton.IsEnabled);
            exportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(provider.Entered);
            Assert.Same(descriptor, provider.Descriptor);
            Assert.Same(binding, provider.Binding);
            Assert.Same(lod, provider.Lod);
            Assert.False(provider.Token.IsCancellationRequested);

            Task drainTask;
            string? replacementPath = null;
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

            WaitUntil(() => provider.Token.IsCancellationRequested);
            Assert.False(drainTask.IsCompleted);
            Assert.Equal("", Named<TextBlock>(view, "ExportStatus").Text);
            provider.Release();
            Pump(drainTask);
            Pump(provider.Completed);

            Assert.Equal("", Named<TextBlock>(view, "ExportStatus").Text);
            Assert.Equal(Visibility.Collapsed, Named<Button>(view, "CancelExportButton").Visibility);
            Assert.True(bindingRetention.IsDisposed);
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

    private static BindingRetention ConfigureBindingProvider(AudioWindow window)
    {
        DisposableProbe? retention = null;
        window.CharacterBindingsProvider = (selected, budget, token) =>
        {
            token.ThrowIfCancellationRequested();
            retention = new(budget.Reserve(512, "generated binding metadata"));
            return Task.FromResult<(IReadOnlyList<CharacterModelBindingResult> Bindings, IDisposable Retention)>(
                ([Binding(selected)], retention));
        };
        return new(() => retention ?? throw new InvalidOperationException("The generated binding provider did not run."));
    }

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

    private static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(root.FindName(name));

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);

    private static void WaitUntil(Func<bool> ready)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!ready())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("The export host did not settle.");
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

    private sealed class PendingExportProvider : IDisposable
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationToken token;

        public Task Entered => entered.Task;
        public Task Completed => completed.Task;
        public AssetDescriptor Descriptor { get; private set; } = null!;
        public CharacterModelBindingResult Binding { get; private set; } = null!;
        public CharacterMeshLodBinding Lod { get; private set; } = null!;
        public CancellationToken Token => token;

        public async Task Invoke(AssetDescriptor descriptor, CharacterModelBindingResult binding,
            CharacterMeshLodBinding lod, CancellationToken cancellationToken)
        {
            Descriptor = descriptor;
            Binding = binding;
            Lod = lod;
            token = cancellationToken;
            entered.TrySetResult();
            await released.Task;
            completed.TrySetResult();
        }

        public void Release() => released.TrySetResult();
        public void Dispose() => released.TrySetResult();
    }

    private sealed class BindingRetention(Func<DisposableProbe> current)
    {
        public bool IsDisposed => current().IsDisposed;
    }

    private sealed class DisposableProbe(AssetMemoryBudget.Reservation reservation) : IDisposable
    {
        private int disposed;
        public bool IsDisposed => Volatile.Read(ref disposed) != 0;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) reservation.Dispose();
        }
    }
}
