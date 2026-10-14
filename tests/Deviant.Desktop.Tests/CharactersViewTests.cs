using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class CharactersViewTests
{
    [Fact]
    public void DelayedVariantsCannotReplaceANewerSelection() => RunSta(() =>
    {
        EnsureApplication();
        var (window, view) = ShowView();
        try
        {
            var revision = Revision();
            var first = Descriptor(new("generated-index", 1), revision, "First", "First", "generated");
            var second = Descriptor(new("generated-index", 2), revision, "Second", "Second", "generated");
            var delayed = new TaskCompletionSource<IReadOnlyList<AssetDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.VariantProviderAsync = (selected, _) => selected == first ? delayed.Task : Task.FromResult<IReadOnlyList<AssetDescriptor>>([second]);
            view.SetEntries([first, second], revision);
            var entries = Named<ListView>(view, "Entries");
            entries.SelectedIndex = 0;
            entries.SelectedIndex = 1;
            Assert.Same(second, view.SelectedDescriptor);
            delayed.SetResult([first]);
            Pump(Task.Delay(50));
            Assert.Same(second, view.SelectedDescriptor);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void RejectsAnotherArchiveRevisionBeforeReplacingSelectedEntry() => RunSta(() =>
    {
        EnsureApplication();
        var (window, view) = ShowView();
        try
        {
            var revision = Revision();
            var current = Descriptor(new("generated-index", 1), revision, "Existing entry", "Stored name", "source");
            view.SetEntries([current], revision);
            Named<ListView>(view, "Entries").SelectedIndex = 0;
            var foreign = current with { Revision = new("generated-index", "another-revision") };
            Assert.Throws<ArgumentException>(() => view.SetEntries([foreign], revision));
            Assert.Same(current, view.SelectedDescriptor);
            Assert.Single(Named<ListView>(view, "Entries").Items.Cast<object>());
            Assert.Throws<ArgumentException>(() => view.SetEntries([current with { Identity = new("another-index", 1) }], revision));
            Assert.Same(current, view.SelectedDescriptor);
        }
        finally { window.Close(); Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }).Task); }
    });

    [Fact]
    public void SearchIncludesDescriptorLabelsAndEveryNameEvidenceWithoutMergingDuplicates() => RunSta(() =>
    {
        EnsureApplication();
        var (window, view) = ShowView();
        try
        {
            var revision = Revision();
            var identity = new ResourceIdentity("generated-index", 7, "generated/member-a");
            var first = Descriptor(identity, revision, "Candidate entry", "First stored name", "first-source");
            var duplicate = Descriptor(identity, revision, "Candidate entry", "Alternate verified name", "second-source");
            duplicate = duplicate with { Names = [.. duplicate.Names,
                new NameEvidence("Shared candidate label", NameOrigin.Candidate, "generated naming evidence", "collision-one")] };
            var sibling = Descriptor(new("generated-index", 8), revision, "Another entry", "Distinct name", "third-source");

            view.SetEntries([first, duplicate, sibling], revision);
            var entries = Named<ListView>(view, "Entries");
            var search = Named<TextBox>(view, "Search");
            Assert.Equal(3, entries.Items.Count);

            search.Text = "Shared candidate label";
            var match = Assert.Single(entries.Items.Cast<object>());
            Assert.Same(duplicate, DescriptorOf(match));
            entries.SelectedItem = match;
            Assert.Equal(identity, view.SelectedIdentity);
            Assert.Same(duplicate, view.SelectedDescriptor);
            Assert.Contains("collision-one", Named<TextBox>(view, "Advanced").Text, StringComparison.Ordinal);
            Assert.Contains("second-source", Named<TextBox>(view, "Advanced").Text, StringComparison.Ordinal);

            search.Text = "first-source";
            Assert.Same(first, DescriptorOf(Assert.Single(entries.Items.Cast<object>())));
            search.Text = "Another entry";
            Assert.Same(sibling, DescriptorOf(Assert.Single(entries.Items.Cast<object>())));
            search.Text = "Alternate verified name";
            Assert.Same(duplicate, DescriptorOf(Assert.Single(entries.Items.Cast<object>())));
            Assert.Equal("1 of 3 entries", Named<TextBlock>(view, "Status").Text);
        }
        finally { window.Close(); Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }).Task); }
    });

    [Fact]
    public void PreviewRunsOnlyOnButtonDoubleClickOrEnterAndReceivesTheExactDescriptor() => RunSta(() =>
    {
        EnsureApplication();
        var (window, view) = ShowView();
        try
        {
            var descriptor = Descriptor(new("generated-index", 12), Revision(), "Generated entry", "Evidence name", "generated source");
            var previews = new List<AssetDescriptor>();
            view.PreviewProvider = (selected, token) =>
            {
                token.ThrowIfCancellationRequested();
                previews.Add(selected);
                return Task.CompletedTask;
            };
            view.SetEntries([descriptor], descriptor.Revision);

            var entries = Named<ListView>(view, "Entries");
            var preview = Named<Button>(view, "PreviewButton");
            entries.SelectedIndex = 0;
            Assert.True(preview.IsEnabled);
            Assert.Empty(previews);

            preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitUntil(() => previews.Count == 1 && Named<TextBlock>(view, "PreviewStatus").Text == "Preview ready");
            Assert.Same(descriptor, Assert.Single(previews));

            entries.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = Control.MouseDoubleClickEvent });
            WaitUntil(() => previews.Count == 2);

            var source = PresentationSource.FromVisual(window)!;
            var enter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            entries.RaiseEvent(enter);
            Assert.True(enter.Handled);
            WaitUntil(() => previews.Count == 3);
            Assert.All(previews, item => Assert.Same(descriptor, item));
        }
        finally { window.Close(); Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }).Task); }
    });

    [Fact]
    public void ClearReleasesRowsAndPendingPreviewWithoutReplacingHostPreviewContent() => RunSta(() =>
    {
        EnsureApplication();
        var (window, view) = ShowView();
        try
        {
            var hostContent = new Border();
            view.PreviewContent.Content = hostContent;
            var descriptor = Descriptor(new("generated-index", 21), Revision(), "Generated entry", "Generated name", "generated source");
            using var previewStarted = new ManualResetEventSlim();
            CancellationToken previewToken = default;
            view.PreviewProvider = (_, token) =>
            {
                previewToken = token;
                previewStarted.Set();
                return Task.Delay(Timeout.Infinite, token);
            };
            view.SetEntries([descriptor], descriptor.Revision);
            var entries = Named<ListView>(view, "Entries");
            entries.SelectedIndex = 0;
            Named<Button>(view, "PreviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(previewStarted.Wait(TimeSpan.FromSeconds(2)));

            view.Clear();

            Assert.True(previewToken.IsCancellationRequested);
            Assert.Empty(entries.Items);
            Assert.Null(entries.ItemsSource);
            Assert.Null(view.SelectedIdentity);
            Assert.Null(view.SelectedDescriptor);
            Assert.Same(hostContent, view.PreviewContent.Content);
            Assert.Equal("No entries", Named<TextBlock>(view, "Status").Text);
        }
        finally { window.Close(); Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }).Task); }
    });

    [Fact]
    public void UnsupportedLayoutShowsPlainMessageAndRetainsTheDiagnosticInAdvanced() => RunSta(() =>
    {
        EnsureApplication();
        var (window, view) = ShowView();
        try
        {
            var descriptor = Descriptor(new("generated-index", 35), Revision(), "Generated model", "Stored name", "generated source");
            view.SetEntries([descriptor], descriptor.Revision);
            view.PreviewProvider = (_, _) => Task.FromException(new InvalidDataException("NativeMeshPartProfileUnsupported"));
            Named<ListView>(view, "Entries").SelectedIndex = 0;
            Named<Button>(view, "PreviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitUntil(() => Named<TextBlock>(view, "PreviewStatus").Text == "Unsupported model layout.");
            Assert.Contains("NativeMeshPartProfileUnsupported", Named<TextBox>(view, "Advanced").Text);
            Assert.DoesNotContain("character.model", Named<TextBox>(view, "Summary").Text);
        }
        finally { window.Close(); Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }).Task); }
    });

    [Fact]
    public void CancelButtonCancelsPendingWorkAndItsLateSuccessCannotMarkThePreviewReady() => RunSta(() =>
    {
        EnsureApplication();
        var (window, view) = ShowView();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            CancellationToken previewToken = default;
            var descriptor = Descriptor(new("generated-index", 34), Revision(), "Generated entry", "Generated name", "generated source");
            view.PreviewProvider = (_, token) => { previewToken = token; return released.Task; };
            view.SetEntries([descriptor], descriptor.Revision);
            Named<ListView>(view, "Entries").SelectedIndex = 0;
            Named<Button>(view, "PreviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var pending = Assert.Single((HashSet<Task>)typeof(CharactersView)
                .GetField("previewTasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!);
            var cancel = Named<Button>(view, "CancelPreviewButton");
            Assert.Equal(Visibility.Visible, cancel.Visibility);
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(previewToken.IsCancellationRequested);
            Assert.Equal(Visibility.Collapsed, cancel.Visibility);
            released.SetResult();
            Pump(pending);
            Assert.Equal("", Named<TextBlock>(view, "PreviewStatus").Text);
            Assert.True(Named<Button>(view, "PreviewButton").IsEnabled);
        }
        finally { released.TrySetResult(); window.Close(); Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }).Task); }
    });

    [Fact]
    public void ReturningToTheSameSelectionDoesNotPublishAnEarlierCancelledPreview() => RunSta(() =>
    {
        EnsureApplication();
        var (window, view) = ShowView();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            CancellationToken previewToken = default;
            var first = Descriptor(new("generated-index", 32), Revision(), "Entry A", "Name A", "source A");
            var second = Descriptor(new("generated-index", 33), Revision(), "Entry B", "Name B", "source B");
            view.PreviewProvider = (_, token) => { previewToken = token; return released.Task; };
            view.SetEntries([first, second], first.Revision);
            var entries = Named<ListView>(view, "Entries");
            entries.SelectedIndex = 0;
            Named<Button>(view, "PreviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var pending = Assert.Single((HashSet<Task>)typeof(CharactersView)
                .GetField("previewTasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!);
            entries.SelectedIndex = 1;
            entries.SelectedIndex = 0;
            Assert.True(previewToken.IsCancellationRequested);
            released.SetResult();
            Pump(pending);
            Assert.Same(first, view.SelectedDescriptor);
            Assert.Equal("", Named<TextBlock>(view, "PreviewStatus").Text);
            Pump(view.DrainPreviewAsync());
        }
        finally { released.TrySetResult(); window.Close(); Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }).Task); }
    });

    [Fact]
    public void DrainWaitsForLatePreviewAndSuppressesItsStatusAfterClear() => RunSta(() =>
    {
        EnsureApplication();
        var (window, view) = ShowView();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            CancellationToken previewToken = default;
            var descriptor = Descriptor(new("generated-index", 31), Revision(), "Generated entry", "Generated name", "generated source");
            view.PreviewProvider = (_, token) => { previewToken = token; return released.Task; };
            view.SetEntries([descriptor], descriptor.Revision);
            Named<ListView>(view, "Entries").SelectedIndex = 0;
            Named<Button>(view, "PreviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            view.Clear();
            var drain = view.DrainPreviewAsync();
            Assert.True(previewToken.IsCancellationRequested);
            Assert.False(drain.IsCompleted);
            released.SetException(new InvalidOperationException("Generated late preview failure"));
            Pump(drain);
            Assert.Equal("", Named<TextBlock>(view, "PreviewStatus").Text);
            Assert.False(Named<Button>(view, "PreviewButton").IsEnabled);
            Assert.Null(view.SelectedDescriptor);
        }
        finally { released.TrySetResult(); window.Close(); Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }).Task); }
    });

    [Fact]
    public void CompactLayoutKeepsThreePanesUsableAndBothSplittersResize() => RunSta(() =>
    {
        EnsureApplication();
        var (window, view) = ShowView(width: 840, height: 620);
        try
        {
            view.SetEntries([Descriptor(new("generated-index", 1), Revision(), "Entry A", "Name A", "source A"),
                Descriptor(new("generated-index", 2), Revision(), "Entry B", "Name B", "source B")], Revision());
            view.PreviewProvider = (_, _) => Task.CompletedTask;
            SettleLayout(window);

            var layout = Named<Grid>(view, "ContentLayout");
            Assert.Equal(5, layout.ColumnDefinitions.Count);
            var widthsBefore = layout.ColumnDefinitions.Select(column => column.ActualWidth).ToArray();
            Assert.All(new[] { 0, 2, 4 }, index => Assert.True(widthsBefore[index] >= 190));
            Assert.True(Named<ListView>(view, "Entries").IsVisible);
            var columns = Assert.IsType<GridView>(Named<ListView>(view, "Entries").View).Columns;
            Assert.Equal(0d, columns[1].Width);
            Assert.True(columns[0].Width >= 140);
            Assert.True(view.PreviewContent.IsVisible);
            Assert.True(Named<TextBox>(view, "Summary").IsVisible);

            Drag(Named<GridSplitter>(view, "EntriesPreviewSplitter"), 28, 0);
            SettleLayout(window);
            var afterFirst = layout.ColumnDefinitions.Select(column => column.ActualWidth).ToArray();
            Assert.True(afterFirst[0] > widthsBefore[0] + 5);
            Assert.True(afterFirst[2] < widthsBefore[2] - 5);

            Drag(Named<GridSplitter>(view, "PreviewDetailsSplitter"), 30, 0);
            SettleLayout(window);
            var afterSecond = layout.ColumnDefinitions.Select(column => column.ActualWidth).ToArray();
            Assert.True(afterSecond[2] > afterFirst[2] + 5);
            Assert.True(afterSecond[4] < afterFirst[4] - 5);
            Assert.All(new[] { 0, 2, 4 }, index => Assert.True(afterSecond[index] >= 190));
        }
        finally { window.Close(); Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }).Task); }
    });

    private static AssetDescriptor Descriptor(ResourceIdentity identity, SourceRevision revision,
        string label, string alternateName, string source) =>
        new(identity, revision, "character.model", label, "generated-group", AssetAvailability.Available,
            AssetCompleteness.Complete, AssetCapabilities.Preview,
            [new NameEvidence(alternateName, NameOrigin.Verified, source)], [], Reason: "Generated descriptor")
        { Metadata = new Dictionary<string, string> { ["classification"] = "generated" } };

    private static SourceRevision Revision() => new("generated-index", "generated-source-revision");

    private static (Window Window, CharactersView View) ShowView(double width = 1000, double height = 700)
    {
        var view = new CharactersView();
        var window = new Window
        {
            Content = view,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        window.Show();
        SettleLayout(window);
        return (window, view);
    }

    private static T Named<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        Assert.IsType<T>(root.FindName(name));

    private static AssetDescriptor DescriptorOf(object row) =>
        (AssetDescriptor)row.GetType().GetProperty("Descriptor", BindingFlags.Instance | BindingFlags.Public)!.GetValue(row)!;

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

    private static void Drag(GridSplitter splitter, double horizontal, double vertical)
    {
        splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        splitter.RaiseEvent(new DragDeltaEventArgs(horizontal, vertical) { RoutedEvent = Thumb.DragDeltaEvent });
        splitter.RaiseEvent(new DragCompletedEventArgs(horizontal, vertical, false) { RoutedEvent = Thumb.DragCompletedEvent });
        splitter.UpdateLayout();
        Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
    }

    private static void SettleLayout(Window window)
    {
        window.UpdateLayout();
        Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
        window.UpdateLayout();
    }

    private static void WaitUntil(Func<bool> ready)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!ready())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("The preview action did not complete.");
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Characters view test timed out.");
        if (failure is not null) throw new InvalidOperationException("Characters view test failed.", failure);
    }
}
