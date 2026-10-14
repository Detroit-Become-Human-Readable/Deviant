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
public sealed class CharactersModelChoiceTests
{
    [Fact]
    public void InitialChoiceSkipsUnavailableLodsWithoutChangingSourceOrdinals()
    {
        RunSta(() =>
        {
            var view = new CharactersView();
            var descriptor = Descriptor(25);
            view.SetEntries([descriptor], descriptor.Revision);
            Entries(view).SelectedIndex = 0;
            var unavailable = Unavailable(0);
            var supported = Available(2);
            view.SetModelBinding(Binding(descriptor, unavailable, supported));
            Assert.Same(supported, view.SelectedModelLod);
            Assert.Equal(new[] { 0, 2 }, ModelRows(view).Items.Cast<object>().Select(RowBinding).Select(row => row.LodOrdinal));
            Assert.True(ModelRows(view).IsEnabled);
            var unavailableItem = new ComboBoxItem { DataContext = ModelRows(view).Items[0], Style = ModelRows(view).ItemContainerStyle };
            var supportedItem = new ComboBoxItem { DataContext = ModelRows(view).Items[1], Style = ModelRows(view).ItemContainerStyle };
            Assert.False(unavailableItem.IsEnabled);
            Assert.True(supportedItem.IsEnabled);
        });
    }

    [Fact]
    public void NoSupportedLodDisablesTheChooserAndKeepsItsDiagnosticContext()
    {
        RunSta(() =>
        {
            var view = new CharactersView();
            var descriptor = Descriptor(26);
            view.SetEntries([descriptor], descriptor.Revision);
            Entries(view).SelectedIndex = 0;
            var ambiguous = Ambiguous(0);
            view.SetModelBinding(Binding(descriptor, ambiguous));
            Assert.False(ModelRows(view).IsEnabled);
            Assert.Same(ambiguous, view.SelectedModelLod);
            Assert.Contains("Ambiguous", Named<TextBox>(view, "Advanced").Text);
            Assert.False(Named<Button>(view, "PreviewButton").IsEnabled);
        });
    }

    [Fact]
    public void BindingValidationPreservesThePriorBindingAndSelectedChoice()
    {
        RunSta(() =>
        {
            EnsureApplication();
            var (window, view) = ShowView();
            try
            {
                var descriptor = Descriptor(17);
                view.SetEntries([descriptor], descriptor.Revision);
                Entries(view).SelectedIndex = 0;
                var valid = Binding(descriptor, Available(0), Available(2));
                view.SetModelBinding(valid);

                var priorChoice = view.SelectedModelLod;
                var mismatchedIdentity = Binding(Descriptor(18), Available(0));
                Assert.Throws<ArgumentException>(() => view.SetModelBinding(mismatchedIdentity));
                Assert.Same(valid, view.ModelBinding);
                Assert.Same(priorChoice, view.SelectedModelLod);

                var mismatchedRevision = Binding(descriptor, Available(0)) with
                { Revision = descriptor.Revision with { PhysicalSourceStamp = "other-generated-revision" } };
                Assert.Throws<ArgumentException>(() => view.SetModelBinding(mismatchedRevision));
                Assert.Same(valid, view.ModelBinding);
                Assert.Same(priorChoice, view.SelectedModelLod);
            }
            finally { window.Close(); Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }).Task); }
        });
    }

    [Fact]
    public void AllLodRowsRemainAndOrdinalZeroIsTheCanonicalInitialChoice()
    {
        RunSta(() =>
        {
            EnsureApplication();
            var (window, view) = ShowView();
            try
            {
                var descriptor = Descriptor(31);
                view.SetEntries([descriptor], descriptor.Revision);
                Entries(view).SelectedIndex = 0;
                var ordinalThree = Unavailable(3);
                var ordinalTwo = Ambiguous(2);
                var ordinalOne = Missing(1);
                var ordinalZero = Available(0);
                var binding = Binding(descriptor, ordinalThree, ordinalZero, ordinalTwo, ordinalOne);
                view.SetModelBinding(binding);

                var rows = ModelRows(view);
                Assert.Equal(4, rows.Items.Count);
                Assert.Equal(new[] { 0, 1, 2, 3 }, rows.Items.Cast<object>().Select(RowBinding).Select(item => item.LodOrdinal));
                Assert.Same(ordinalZero, view.SelectedModelLod);
                Assert.Null(ordinalTwo.Selection);
                Assert.Equal(ReferenceStatus.Ambiguous, ordinalTwo.MeshDataResolution.Status);
                Assert.Equal(2, ordinalTwo.MeshDataResolution.Candidates.Count);
                Assert.Null(ordinalOne.Selection);
                Assert.Equal(ReferenceStatus.Missing, ordinalOne.MeshDataResolution.Status);
                Assert.Null(ordinalThree.Selection);
                Assert.Equal(ReferenceStatus.Unresolved, ordinalThree.MeshDataResolution.Status);
            }
            finally { window.Close(); Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }).Task); }
        });
    }

    [Fact]
    public void UnsupportedAmbiguousChoiceCannotCallPreviewProvider()
    {
        RunSta(() =>
        {
            EnsureApplication();
            var (window, view) = ShowView();
            try
            {
                var descriptor = Descriptor(45);
                view.SetEntries([descriptor], descriptor.Revision);
                Entries(view).SelectedIndex = 0;
                view.PreviewProvider = (_, _) => throw new InvalidOperationException("Unsupported choice reached the provider.");
                view.SetModelBinding(Binding(descriptor, Ambiguous(0)));

                Assert.Equal(ReferenceStatus.Ambiguous, view.SelectedModelLod!.MeshDataResolution.Status);
                Assert.Null(view.SelectedModelLod.Selection);
                Assert.False(Named<Button>(view, "PreviewButton").IsEnabled);
                Pump((Task)typeof(CharactersView).GetMethod("PreviewSelectedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(view, [CancellationToken.None])!);
                Assert.Equal("This detail level is unavailable.", Named<TextBlock>(view, "PreviewStatus").Text);
            }
            finally { window.Close(); Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }).Task); }
        });
    }

    [Fact]
    public void ManualLodChoiceCancelsPendingPreviewAndBlocksLateReadyStatus()
    {
        RunSta(() =>
        {
            EnsureApplication();
            var (window, view) = ShowView();
            try
            {
                var descriptor = Descriptor(53);
                var first = Available(0);
                var second = Available(1);
                view.SetEntries([descriptor], descriptor.Revision);
                Entries(view).SelectedIndex = 0;
                using var provider = new DelayedPreview();
                view.PreviewProvider = provider.Invoke;
                view.SetModelBinding(Binding(descriptor, first, second));
                Assert.Same(first, view.SelectedModelLod);
                Assert.True(Named<Button>(view, "PreviewButton").IsEnabled);

                Named<Button>(view, "PreviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(provider.Entered.Task);
                Assert.Single(Field<HashSet<Task>>(view, "previewTasks"));
                Assert.Equal("Preparing preview…", Named<TextBlock>(view, "PreviewStatus").Text);

                var choices = ModelRows(view);
                choices.SelectedIndex = 1;
                Assert.True(provider.Token.IsCancellationRequested);
                Assert.Same(second, view.SelectedModelLod);
                Assert.True(Named<Button>(view, "PreviewButton").IsEnabled);

                provider.Release();
                Pump(view.DrainPreviewAsync());
                Assert.Empty(Field<HashSet<Task>>(view, "previewTasks"));
                Assert.Same(second, view.SelectedModelLod);
                Assert.Equal(string.Empty, Named<TextBlock>(view, "PreviewStatus").Text);
                Assert.Equal(1, provider.CallCount);
            }
            finally { window.Close(); Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }).Task); }
        });
    }

    private static AssetDescriptor Descriptor(long ordinal)
    {
        var revision = new SourceRevision("generated-index", "generated-revision");
        return new(new("generated-index", ordinal, "generated/model-entry"), revision,
            "character.model", $"Generated model {ordinal}", "generated-group", AssetAvailability.Available,
            AssetCompleteness.Complete, AssetCapabilities.Preview, [], []);
    }

    private static CharacterModelBindingResult Binding(AssetDescriptor descriptor, params CharacterMeshLodBinding[] lods)
    {
        var catalog = new ResourceIdentity(descriptor.Identity.IndexContentId, 90, "generated/catalog");
        var container = new ResourceIdentity(descriptor.Identity.IndexContentId, 91, "generated/container");
        return new(descriptor.Identity, catalog, container, descriptor.Revision, 2009, 7, null, 0,
            null, null, new(ReferenceStatus.Unique, [container]), [], [], lods, null, null);
    }

    private static CharacterMeshLodBinding Available(int ordinal) => Lod(ordinal,
        new(ReferenceStatus.Unique, [MeshIdentity(ordinal)]),
        MeshIdentity(ordinal), new NativeModelSelection(MeshIdentity(ordinal), 0), 1, null);

    private static CharacterMeshLodBinding Missing(int ordinal) => Lod(ordinal,
        new(ReferenceStatus.Missing, []), null, null, null, "Generated mesh data is missing.");

    private static CharacterMeshLodBinding Ambiguous(int ordinal) => Lod(ordinal,
        new(ReferenceStatus.Ambiguous, [MeshIdentity(ordinal), MeshIdentity(ordinal + 100)]),
        null, null, null, "Generated mesh data is ambiguous.");

    private static CharacterMeshLodBinding Unavailable(int ordinal) => Lod(ordinal,
        new(ReferenceStatus.Unresolved, []), null, null, null, "Generated mesh data could not be resolved.");

    private static CharacterMeshLodBinding Lod(int ordinal, ReferenceResolution resolution,
        ResourceIdentity? mesh, NativeModelSelection? selection, int? groupCount, string? diagnostic)
    {
        var owner = new ResourceIdentity("generated-index", 17, "generated/model-owner");
        return new(ordinal, 0.5f + ordinal, new(IdentifierNamespace.Native, $"generated-mesh-{ordinal}", owner),
            resolution, mesh, selection is null ? null : checked((uint)selection.GroupIndex), groupCount,
            null, selection, new(owner, ordinal * 16L, 8), null, null, diagnostic);
    }

    private static ResourceIdentity MeshIdentity(int ordinal) =>
        new("generated-index", 1000 + ordinal, $"generated/mesh/{ordinal}");

    private static CharacterMeshLodBinding RowBinding(object row) =>
        (CharacterMeshLodBinding)row.GetType().GetProperty("Binding", BindingFlags.Instance | BindingFlags.Public)!.GetValue(row)!;

    private static ComboBox ModelRows(CharactersView view) => (ComboBox)view.FindName("ModelChoices")!;
    private static ListView Entries(CharactersView view) => (ListView)view.FindName("Entries")!;
    private static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(root.FindName(name));
    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

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

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void RunSta(Action action)
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Characters model choice test timed out.");
        if (failure is not null) throw new InvalidOperationException("Characters model choice test failed.", failure);
    }

    private sealed class DelayedPreview : IDisposable
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int callCount;
        private CancellationToken token;
        public TaskCompletionSource Entered => entered;
        public CancellationToken Token => token;
        public int CallCount => Volatile.Read(ref callCount);
        public Task Invoke(AssetDescriptor _, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            token = cancellationToken;
            entered.TrySetResult();
            return release.Task;
        }
        public void Release() => release.TrySetResult();
        public void Dispose() => Release();
    }
}
