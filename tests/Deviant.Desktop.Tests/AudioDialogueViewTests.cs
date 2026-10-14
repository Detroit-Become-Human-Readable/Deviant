using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Desktop;
using Deviant.Formats.Localization.Model;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class AudioDialogueViewTests
{
    [Fact]
    public void KeepsDuplicateTextOccurrencesAndEveryMappingWhileFilteringByLanguage() => RunSta(() =>
    {
        var view = ShowView(out var window);
        try
        {
            var result = CreateResult();
            view.SetResult(result);

            var rows = Named<ListView>(view, "Occurrences");
            var languages = Named<ComboBox>(view, "Languages");
            Assert.Equal(3, rows.Items.Count);
            Assert.Equal(new[] { "Any", "ENG", "FRE" }, languages.Items.Cast<object>()
                .Select(item => (string)item.GetType().GetProperty("Label")!.GetValue(item)!));

            Assert.Same(result.Occurrences[0], rows.Items[0]);
            Assert.Same(result.Occurrences[1], rows.Items[1]);
            Assert.Same(result.Occurrences[0].TextOccurrence, result.Occurrences[1].TextOccurrence);
            Assert.NotEqual(result.Occurrences[0].Relationship.DialogueHeaderIdentity,
                result.Occurrences[1].Relationship.DialogueHeaderIdentity);
            Assert.Equal(result.Occurrences[0].TextOccurrence.ResourceIdentity,
                result.Occurrences[1].TextOccurrence.ResourceIdentity);

            languages.SelectedItem = languages.Items.Cast<object>()
                .Single(item => (string)item.GetType().GetProperty("Label")!.GetValue(item)! == "FRE");
            Pump();
            Assert.Single(rows.Items.Cast<object>());
            Assert.Same(result.Occurrences[2], rows.Items[0]);

            languages.SelectedIndex = 0;
            Pump();
            Assert.Equal(3, rows.Items.Count);
            rows.SelectedItem = result.Occurrences[1];
            Assert.Same(result.Occurrences[1], view.SelectedOccurrence);
            Assert.Equal("ENG", Named<TextBlock>(view, "SelectedLanguage").Text);
            Assert.Equal("Hello from the generated fixture.", Named<TextBox>(view, "SelectedText").Text);
        }
        finally
        {
            window.Close();
            Pump();
        }
    });

    [Fact]
    public void LookupDiagnosticsStayInAdvancedAndOpenTextUsesTheSelectedOriginalOccurrence() => RunSta(() =>
    {
        var view = ShowView(out var window);
        try
        {
            var result = CreateResult();
            var calls = 0;
            LocalizationAudioTextOccurrence? navigated = null;
            CancellationToken receivedToken = default;
            using var cancellation = new CancellationTokenSource();
            view.TextNavigationToken = cancellation.Token;
            view.TextNavigator = (occurrence, token) =>
            {
                calls++;
                navigated = occurrence;
                receivedToken = token;
                return Task.CompletedTask;
            };
            view.SetResult(result);
            var rows = Named<ListView>(view, "Occurrences");
            rows.SelectedItem = result.Occurrences[0];

            Assert.Equal(0, calls);
            Assert.Contains("GlobalPointerAmbiguous", Named<TextBox>(view, "Advanced").Text, StringComparison.Ordinal);
            Assert.Contains("Two generated pointer candidates", Named<TextBox>(view, "Advanced").Text, StringComparison.Ordinal);
            Assert.Equal("Hello from the generated fixture.", Named<TextBox>(view, "SelectedText").Text);
            Assert.DoesNotContain("GlobalPointerAmbiguous", Named<TextBox>(view, "SelectedText").Text, StringComparison.Ordinal);

            Named<Button>(view, "OpenText").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(WaitUntilAsync(() => calls == 1));
            Assert.Same(result.Occurrences[0], navigated);
            Assert.Equal(cancellation.Token, receivedToken);
        }
        finally
        {
            window.Close();
            Pump();
        }
    });

    [Fact]
    public void EmptyResultAndClearShowTheTrueEmptyStateAndResetSelectionAndStatus() => RunSta(() =>
    {
        var view = ShowView(out var window);
        try
        {
            view.SetResult(CreateResult(occurrences: []));
            Assert.Equal("No dialogue linked.", Named<TextBlock>(view, "EmptyMessage").Text);
            Assert.Equal(Visibility.Visible, Named<TextBlock>(view, "EmptyMessage").Visibility);
            Assert.Empty(Named<ListView>(view, "Occurrences").Items);

            view.SetStatus("Looking up dialogue…");
            Assert.Equal("Looking up dialogue…", Named<TextBlock>(view, "Status").Text);
            view.Clear();

            Assert.Null(view.CurrentResult);
            Assert.Null(view.SelectedOccurrence);
            Assert.Null(Named<ListView>(view, "Occurrences").ItemsSource);
            Assert.Equal("No dialogue linked.", Named<TextBlock>(view, "EmptyMessage").Text);
            Assert.Equal(string.Empty, Named<TextBlock>(view, "Status").Text);
            Assert.False(Named<Button>(view, "OpenText").IsEnabled);
        }
        finally
        {
            window.Close();
            Pump();
        }
    });

    [Fact]
    public void LongDialogueAndGlobalDiagnosticsStayScrollableInA260PixelSidebar() => RunSta(() =>
    {
        var view = ShowView(out var window, width: 260, height: 360);
        try
        {
            var longText = string.Join(" ", Enumerable.Repeat("Generated dialogue remains readable in a narrow details sidebar.", 14));
            var result = CreateResult(longText: longText,
                diagnostics: Enumerable.Range(0, 12).Select(index => Diagnostic(index)).ToArray());
            view.SetResult(result);
            window.UpdateLayout();
            Pump();
            var rows = Named<ListView>(view, "Occurrences");
            rows.SelectedItem = result.Occurrences[0];
            Named<Expander>(view, "AdvancedPanel").IsExpanded = true;
            Pump();

            var selectedText = Named<TextBox>(view, "SelectedText");
            var advanced = Named<TextBox>(view, "Advanced");
            var advancedScroll = Named<ScrollViewer>(view, "AdvancedScroll");
            Assert.InRange(view.ActualWidth, 240, 260);
            Assert.True(selectedText.ActualWidth <= view.ActualWidth);
            Assert.True(selectedText.ActualHeight <= 76);
            Assert.True(selectedText.Text.Length > 100);
            Assert.Equal(ScrollBarVisibility.Auto, advancedScroll.VerticalScrollBarVisibility);
            Assert.True(advanced.ActualWidth <= view.ActualWidth);
            Assert.Contains("Lookup diagnostics", advanced.Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            Pump();
        }
    });

    private static AudioDialogueView ShowView(out Window window, double width = 420, double height = 640)
    {
        var view = new AudioDialogueView();
        window = new Window
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
        window.UpdateLayout();
        Pump();
        return view;
    }

    private static LocalizationAudioLookupResult CreateResult(string? longText = null,
        IReadOnlyList<LocalizationAudioTextOccurrence>? occurrences = null,
        IReadOnlyList<LocalizationAudioLookupIssue>? diagnostics = null)
    {
        var audio = new ResourceIdentity("fixture-index", 9, "wem");
        var headerA = new ResourceIdentity("fixture-index", 4);
        var headerB = new ResourceIdentity("fixture-index", 5);
        var textOwner = new ResourceIdentity("fixture-index", 1);
        var pointerOwnerA = new ResourceIdentity("fixture-index", 2);
        var pointerOwnerB = new ResourceIdentity("fixture-index", 3);
        var textEn = Record(textOwner, 0, LocalizationRecordKind.Text, "ENG", "Hello from the generated fixture.", 100,
            longText is null ? "dialogue_key" : "long_dialogue_key", longText ?? "Hello from the generated fixture.");
        var textFr = Record(new ResourceIdentity("fixture-index", 6), 6, LocalizationRecordKind.Text, "FRE",
            "Bonjour du fixture généré.", 600, "dialogue_key_fr", "Bonjour du fixture généré.");
        var pointerA = Record(pointerOwnerA, 2, LocalizationRecordKind.Pointer, "ENG", null, 200, "dialogue_key", null);
        var pointerB = Record(pointerOwnerB, 3, LocalizationRecordKind.Pointer, "ENG", null, 300, "dialogue_key", null);
        var pointerFr = Record(new ResourceIdentity("fixture-index", 7), 7, LocalizationRecordKind.Pointer, "FRE", null, 700,
            "dialogue_key_fr", null);
        var values = occurrences ??
        [
            Occurrence(textEn, pointerA, audio, headerA),
            Occurrence(textEn, pointerB, audio, headerB),
            Occurrence(textFr, pointerFr, audio, headerA)
        ];
        return new(audio, [headerA, headerB], values, diagnostics ?? [Diagnostic(0)]);
    }

    private static LocalizationAudioTextOccurrence Occurrence(LocalizationRecord text, LocalizationRecord pointer,
        ResourceIdentity audio, ResourceIdentity header) => new(text, pointer,
        new LocalizationSelectedAudioRelationship(text.Identity, pointer.Identity, text.Key ?? "", text.RawLanguageCode,
            header, audio, new ResourceIdentity("fixture-index", header.Ordinal + 10), []));

    private static LocalizationAudioLookupIssue Diagnostic(int index) => new(
        "GlobalPointerAmbiguous", "Two generated pointer candidates were retained.",
        LocalizationReferenceLinkStatus.Ambiguous,
        [new ResourceIdentity("fixture-index", index + 20), new ResourceIdentity("fixture-index", index + 21)],
        new ResourceIdentity("fixture-index", index + 30), 2);

    private static LocalizationRecord Record(ResourceIdentity owner, long ordinal, LocalizationRecordKind kind,
        string language, string? text, long offset, string key, string? value) => new(
        new ResourceIdentity(owner.IndexContentId, owner.Ordinal, $"record/{offset}"), owner, ordinal, 1016, 1, 0,
        language, language, offset, checked((int)ordinal), kind, LocalizationRecordStatus.Complete,
        key, value ?? text, "UTF-8", new LocalizationExtent("Record", "fixture.dat", offset, 16), [], new Dictionary<string, string>());

    private static T Named<T>(AudioDialogueView view, string name) where T : FrameworkElement =>
        Assert.IsType<T>(view.FindName(name));

    private static Task WaitUntilAsync(Func<bool> predicate)
    {
        return WaitCoreAsync(predicate);
        static async Task WaitCoreAsync(Func<bool> ready)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!ready()) await Task.Delay(10, timeout.Token);
        }
    }

    private static void Pump()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The dialogue view check timed out.");
        if (failure is not null) throw new InvalidOperationException("The dialogue view check failed.", failure);
    }
}
