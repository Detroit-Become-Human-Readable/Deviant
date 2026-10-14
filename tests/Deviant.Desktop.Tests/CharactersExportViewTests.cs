using System.IO;
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
public sealed class CharactersExportViewTests
{
    [Fact]
    public void CancelButtonWaitsForProviderCleanupBeforeEnablingAnotherExport() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            OpenCharacters(window, fixture);
            ConfigureBindingProvider(window);
            var view = window.CharactersBrowser;
            Named<ListView>(view, "Entries").SelectedIndex = 0;
            WaitUntil(() => view.ModelBinding is not null);
            using var pending = new PendingExport();
            var calls = 0;
            view.ExportProvider = (descriptor, binding, lod, token) =>
            {
                ++calls;
                return pending.Invoke(descriptor, binding, lod, token);
            };
            var export = Named<Button>(view, "ExportFbxButton");
            var cancel = Named<Button>(view, "CancelExportButton");
            export.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(pending.Entered);
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(pending.Token.IsCancellationRequested);
            Assert.Equal("Cancelling…", Named<TextBlock>(view, "ExportStatus").Text);
            Assert.False(export.IsEnabled);
            Assert.False(cancel.IsEnabled);
            export.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, calls);
            pending.Release();
            Pump(pending.Completed);
            WaitUntil(() => export.IsEnabled);
            Assert.Equal("Export cancelled.", Named<TextBlock>(view, "ExportStatus").Text);
        });

    [Fact]
    public void ExportIsHiddenUntilProviderAndEligibleSelectionAndRunsOnlyOnClick() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            var descriptor = OpenCharacters(window, fixture);
            ConfigureBindingProvider(window);
            var view = window.CharactersBrowser;
            var button = Named<Button>(view, "ExportFbxButton");
            Assert.Equal(Visibility.Collapsed, button.Visibility);

            var calls = new List<(AssetDescriptor Descriptor, CharacterModelBindingResult Binding, CharacterMeshLodBinding Lod)>();
            view.ExportProvider = (selected, binding, lod, token) =>
            {
                token.ThrowIfCancellationRequested();
                calls.Add((selected, binding, lod));
                return Task.CompletedTask;
            };
            Assert.Equal(Visibility.Collapsed, button.Visibility);

            Named<ListView>(view, "Entries").SelectedIndex = 0;
            WaitUntil(() => view.ModelBinding is not null);
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.True(button.IsEnabled);
            Assert.Empty(calls);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitUntil(() => Named<TextBlock>(view, "ExportStatus").Text == "Export complete.");
            var call = Assert.Single(calls);
            Assert.Same(descriptor, call.Descriptor);
            Assert.Same(view.ModelBinding, call.Binding);
            Assert.Same(view.SelectedModelLod, call.Lod);
        });

    [Fact]
    public void LateCancelledExportCannotOverwriteNewerExportStatus() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            OpenCharacters(window, fixture);
            ConfigureBindingProvider(window);
            var view = window.CharactersBrowser;
            Named<ListView>(view, "Entries").SelectedIndex = 0;
            WaitUntil(() => view.ModelBinding is not null);
            using var first = new PendingExport();
            using var second = new PendingExport();
            view.ExportProvider = first.Invoke;
            var button = Named<Button>(view, "ExportFbxButton");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(first.Entered);

            view.ExportProvider = second.Invoke;
            Assert.True(first.Token.IsCancellationRequested);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(second.Entered);
            Assert.Equal("Exporting…", Named<TextBlock>(view, "ExportStatus").Text);

            first.Release();
            Pump(first.Completed);
            WaitUntil(() => first.Token.IsCancellationRequested);
            Assert.Equal("Exporting…", Named<TextBlock>(view, "ExportStatus").Text);

            second.Release();
            Pump(second.Completed);
            WaitUntil(() => Named<TextBlock>(view, "ExportStatus").Text == "Export complete.");
        });

    [Fact]
    public void DrainWaitsForCancelledProviderAndIgnoresItsLateCompletion() =>
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            OpenCharacters(window, fixture);
            ConfigureBindingProvider(window);
            var view = window.CharactersBrowser;
            Named<ListView>(view, "Entries").SelectedIndex = 0;
            WaitUntil(() => view.ModelBinding is not null);
            using var pending = new PendingExport();
            view.ExportProvider = pending.Invoke;
            Named<Button>(view, "ExportFbxButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(pending.Entered);

            var drain = view.DrainExportAsync();
            Assert.True(pending.Token.IsCancellationRequested);
            Assert.False(drain.IsCompleted);
            pending.Release();
            Pump(drain);
            Pump(pending.Completed);
            Assert.Equal("", Named<TextBlock>(view, "ExportStatus").Text);
            Assert.Equal(Visibility.Collapsed, Named<Button>(view, "CancelExportButton").Visibility);
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

    private static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(root.FindName(name));

    private static void WaitUntil(Func<bool> ready)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!ready())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("The export view did not settle.");
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

    private sealed class PendingExport : IDisposable
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationToken token;

        public Task Entered => entered.Task;
        public Task Completed => completed.Task;
        public CancellationToken Token => token;

        public async Task Invoke(AssetDescriptor descriptor, CharacterModelBindingResult binding,
            CharacterMeshLodBinding lod, CancellationToken cancellationToken)
        {
            token = cancellationToken;
            entered.TrySetResult();
            await released.Task;
            completed.TrySetResult();
        }

        public void Release() => released.TrySetResult();
        public void Dispose() => released.TrySetResult();
    }
}
