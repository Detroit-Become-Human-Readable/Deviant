using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
public sealed class CharactersContextTests
{
    [Fact]
    public void DuplicateNativeIdentityContextsRemainSeparateAndRequireExplicitContextAndLodChoice()
    {
        RunSta(() =>
        {
            EnsureApplication();
            var (window, view) = ShowView();
            try
            {
                var descriptor = Descriptor(101);
                view.SetEntries([descriptor], descriptor.Revision);
                Entries(view).SelectedItem = Entries(view).Items[0];
                var providerCalls = 0;
                view.PreviewProvider = (_, _) =>
                {
                    providerCalls++;
                    return Task.CompletedTask;
                };

                var contextA = Binding(descriptor, catalogOrdinal: 40, lods: [Available(0), Available(2)]);
                var contextB = Binding(descriptor, catalogOrdinal: 41, lods: [Available(1), Available(3)]);
                Assert.Equal(contextA.SelectedIdentity, contextB.SelectedIdentity);
                Assert.Equal(contextA.Revision, contextB.Revision);
                Assert.NotEqual(contextA.CatalogIdentity, contextB.CatalogIdentity);

                view.SetModelBindings([contextA, contextB]);

                var contexts = Named<ComboBox>(view, "ModelContexts");
                var lods = Named<ComboBox>(view, "ModelChoices");
                Assert.Equal(2, contexts.Items.Count);
                Assert.Equal(-1, contexts.SelectedIndex);
                Assert.Empty(lods.Items);
                Assert.Null(view.ModelBinding);
                Assert.Null(view.SelectedModelLod);
                Assert.False(Named<Button>(view, "PreviewButton").IsEnabled);
                Assert.Equal(0, providerCalls);

                contexts.SelectedIndex = 0;
                Assert.Same(contextA, view.ModelBinding);
                Assert.Same(contextA.Lods[0], view.SelectedModelLod);
                Assert.Equal(2, lods.Items.Count);
                Assert.Equal(0, lods.SelectedIndex);

                lods.SelectedIndex = 1;
                Assert.Same(contextA, view.ModelBinding);
                Assert.Same(contextA.Lods[1], view.SelectedModelLod);
                Assert.True(Named<Button>(view, "PreviewButton").IsEnabled);
                Assert.Equal(0, providerCalls);

                Named<Button>(view, "PreviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(WaitUntilAsync(() => providerCalls == 1));
                Assert.Same(descriptor, view.SelectedDescriptor);
                Assert.Same(contextA, view.ModelBinding);
                Assert.Same(contextA.Lods[1], view.SelectedModelLod);
                Assert.Equal(1, providerCalls);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SwitchingResourceGroupCancelsPendingPreviewAndIgnoresItsLateReadyResult()
    {
        RunSta(() =>
        {
            EnsureApplication();
            var (window, view) = ShowView();
            try
            {
                var descriptor = Descriptor(202);
                view.SetEntries([descriptor], descriptor.Revision);
                Entries(view).SelectedIndex = 0;
                var contextA = Binding(descriptor, catalogOrdinal: 60, lods: [Available(0)]);
                var contextB = Binding(descriptor, catalogOrdinal: 61, lods: [Available(1)]);
                using var preview = new DelayedPreview();
                view.PreviewProvider = preview.Invoke;
                view.SetModelBindings([contextA, contextB]);

                var contexts = Named<ComboBox>(view, "ModelContexts");
                contexts.SelectedIndex = 0;
                Assert.Same(contextA, view.ModelBinding);
                Assert.True(Named<Button>(view, "PreviewButton").IsEnabled);
                Named<Button>(view, "PreviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(WaitUntilAsync(() => preview.CallCount == 1));
                Assert.Equal("Preparing preview…", Named<TextBlock>(view, "PreviewStatus").Text);

                contexts.SelectedIndex = 1;
                Assert.True(preview.Token.IsCancellationRequested);
                Assert.Same(contextB, view.ModelBinding);
                Assert.Same(contextB.Lods[0], view.SelectedModelLod);
                Assert.True(Named<Button>(view, "PreviewButton").IsEnabled);

                preview.Release();
                Pump(view.DrainPreviewAsync());
                Assert.Same(contextB, view.ModelBinding);
                Assert.Same(contextB.Lods[0], view.SelectedModelLod);
                Assert.NotEqual("Preview ready", Named<TextBlock>(view, "PreviewStatus").Text);
                Assert.Equal(1, preview.CallCount);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DeselectingContextClearsBindingLodChoicesAndPreviewEligibility()
    {
        RunSta(() =>
        {
            EnsureApplication();
            var (window, view) = ShowView();
            try
            {
                var descriptor = Descriptor(303);
                view.SetEntries([descriptor], descriptor.Revision);
                Entries(view).SelectedIndex = 0;
                view.PreviewProvider = (_, _) => Task.CompletedTask;
                view.SetModelBindings([
                    Binding(descriptor, catalogOrdinal: 70, lods: [Available(0)]),
                    Binding(descriptor, catalogOrdinal: 71, lods: [Available(1)])
                ]);
                var contexts = Named<ComboBox>(view, "ModelContexts");
                contexts.SelectedIndex = 0;
                Assert.NotNull(view.ModelBinding);

                contexts.SelectedIndex = -1;

                Assert.Null(view.ModelBinding);
                Assert.Null(view.SelectedModelLod);
                Assert.Null(Named<ComboBox>(view, "ModelChoices").ItemsSource);
                Assert.Equal(Visibility.Collapsed, Named<FrameworkElement>(view, "ModelChoicesPanel").Visibility);
                Assert.False(Named<Button>(view, "PreviewButton").IsEnabled);
                Assert.Contains("Choose a resource group", Named<TextBlock>(view, "BindingStatus").Text, StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InvalidBindingInIncomingContextListPreservesPriorValidContextAndLod()
    {
        RunSta(() =>
        {
            EnsureApplication();
            var (window, view) = ShowView();
            try
            {
                var descriptor = Descriptor(404);
                view.SetEntries([descriptor], descriptor.Revision);
                Entries(view).SelectedIndex = 0;
                view.PreviewProvider = (_, _) => Task.CompletedTask;
                var priorA = Binding(descriptor, catalogOrdinal: 80, lods: [Available(0), Available(2)]);
                var priorB = Binding(descriptor, catalogOrdinal: 81, lods: [Available(1), Available(3)]);
                view.SetModelBindings([priorA, priorB]);
                var contexts = Named<ComboBox>(view, "ModelContexts");
                contexts.SelectedIndex = 1;
                var choices = Named<ComboBox>(view, "ModelChoices");
                choices.SelectedIndex = 1;
                var selectedLod = view.SelectedModelLod;
                Assert.Same(priorB, view.ModelBinding);

                var replacement = Binding(descriptor, catalogOrdinal: 82, lods: [Available(4)]);
                var wrongIdentity = Binding(Descriptor(405), catalogOrdinal: 83, lods: [Available(5)]);
                Assert.Throws<ArgumentException>(() => view.SetModelBindings([replacement, wrongIdentity]));

                Assert.Same(priorB, view.ModelBinding);
                Assert.Same(selectedLod, view.SelectedModelLod);
                Assert.Equal(1, contexts.SelectedIndex);
                Assert.Equal(2, contexts.Items.Count);
                Assert.Equal(2, choices.Items.Count);
                Assert.True(Named<Button>(view, "PreviewButton").IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static AssetDescriptor Descriptor(long ordinal)
    {
        var revision = new SourceRevision("generated-index", "generated-revision");
        return new(new("generated-index", ordinal, "generated/model-entry"), revision,
            "character.model", $"Generated model {ordinal}", "generated-group", AssetAvailability.Available,
            AssetCompleteness.Complete, AssetCapabilities.Preview, [], []);
    }

    private static CharacterModelBindingResult Binding(AssetDescriptor descriptor, long catalogOrdinal,
        IReadOnlyList<CharacterMeshLodBinding> lods)
    {
        var catalog = new ResourceIdentity(descriptor.Identity.IndexContentId, catalogOrdinal, "generated/catalog");
        var container = new ResourceIdentity(descriptor.Identity.IndexContentId, catalogOrdinal + 100, "generated/container");
        return new(descriptor.Identity, catalog, container, descriptor.Revision, 2009, 7, null, 0,
            null, null, new(ReferenceStatus.Unique, [container]), [], [], lods, null, null);
    }

    private static CharacterMeshLodBinding Available(int ordinal)
    {
        var mesh = MeshIdentity(ordinal);
        return new(ordinal, 0.5f + ordinal, new(IdentifierNamespace.Native, $"generated-mesh-{ordinal}", mesh),
            new(ReferenceStatus.Unique, [mesh]), mesh, checked((uint)ordinal), ordinal + 1,
            $"Generated mesh {ordinal}", new NativeModelSelection(mesh, ordinal), new(mesh, ordinal * 16L, 8),
            null, null, null);
    }

    private static ResourceIdentity MeshIdentity(int ordinal) =>
        new("generated-index", 1000 + ordinal, $"generated/mesh/{ordinal}");

    private static ListView Entries(CharactersView view) => (ListView)view.FindName("Entries")!;
    private static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(root.FindName(name));

    private static (Window Window, CharactersView View) ShowView()
    {
        var view = new CharactersView();
        var window = new Window
        {
            Content = view,
            Width = 1000,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        window.Show();
        window.UpdateLayout();
        Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
        return (window, view);
    }

    private static void EnsureApplication()
    {
        var app = Application.Current ?? new App();
        var assemblyName = typeof(CharactersView).Assembly.GetName().Name
            ?? throw new InvalidOperationException("The desktop assembly has no name.");
        if (app.Resources.MergedDictionaries.Any(dictionary =>
                dictionary.Source?.OriginalString.EndsWith("ControlStyles.xaml", StringComparison.OrdinalIgnoreCase) == true))
            return;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"/{assemblyName};component/ControlStyles.xaml", UriKind.Relative)
        });
    }

    private static Task WaitUntilAsync(Func<bool> predicate)
    {
        return WaitCoreAsync(predicate);
        static async Task WaitCoreAsync(Func<bool> ready)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!ready()) await Task.Delay(10, timeout.Token);
        }
    }

    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The character context check timed out.");
        if (failure is not null) throw new InvalidOperationException("The character context check failed.", failure);
    }

    private sealed class DelayedPreview : IDisposable
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationToken token;
        private int calls;

        public CancellationToken Token => token;
        public int CallCount => Volatile.Read(ref calls);

        public Task Invoke(AssetDescriptor descriptor, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref calls);
            token = cancellationToken;
            return release.Task;
        }

        public void Release() => release.TrySetResult();
        public void Dispose() => Release();
    }
}
