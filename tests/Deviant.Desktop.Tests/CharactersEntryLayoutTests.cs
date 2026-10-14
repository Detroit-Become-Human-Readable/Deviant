using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Deviant.Assets;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class CharactersEntryLayoutTests
{
    [Fact]
    public void CompactDefaultsKeepAvailabilityInsideTheEntriesList()
    {
        RunSta(() => WithView(760, (window, view, list, columns) =>
        {
            var availability = Descendants(list).OfType<GridViewColumnHeader>()
                .Single(header => ReferenceEquals(header.Column, columns[1]));
            var location = availability.TransformToAncestor(list).Transform(new Point());
            Assert.InRange(columns[0].ActualWidth, 64, 320);
            Assert.True(location.X + availability.ActualWidth <= list.ActualWidth,
                "The default Name width hides Availability at compact size.");
        }));
    }

    [Fact]
    public void HeaderDragKeepsTheUserWidthAcrossWindowAndPaneResizes()
    {
        RunSta(() => WithView(1050, (window, view, list, columns) =>
        {
            var header = Descendants(list).OfType<GridViewColumnHeader>()
                .Single(item => ReferenceEquals(item.Column, columns[0]));
            header.ApplyTemplate();
            var grip = Assert.IsType<Thumb>(header.Template.FindName("PART_HeaderGripper", header));
            var initialWidth = columns[0].ActualWidth;
            Drag(grip, 35);
            window.UpdateLayout();
            var userWidth = columns[0].ActualWidth;
            Assert.True(userWidth > initialWidth + 20, "The Name header did not resize.");
            window.Width = 760;
            window.UpdateLayout();
            Assert.InRange(columns[0].ActualWidth, userWidth - 1, userWidth + 1);
            Drag(Assert.IsAssignableFrom<Thumb>(view.FindName("EntriesPreviewSplitter")), 25);
            window.UpdateLayout();
            Assert.InRange(columns[0].ActualWidth, userWidth - 1, userWidth + 1);
        }));
    }

    private static void WithView(double width, Action<Window, CharactersView, ListView, GridViewColumnCollection> verify)
    {
        var view = new CharactersView();
        var window = new Window { Content = view, Width = width, Height = 580,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var revision = new SourceRevision("generated-index", "generated-revision");
            view.SetEntries([new(new("generated-index", 1), revision, "character-model", new string('x', 200),
                null, AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.None, [], [])], revision);
            window.UpdateLayout();
            var list = Assert.IsType<ListView>(view.FindName("Entries"));
            var columns = Assert.IsType<GridView>(list.View).Columns;
            verify(window, view, list, columns);
        }
        finally { window.Close(); }
    }

    private static void Drag(Thumb grip, double horizontal)
    {
        grip.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        grip.RaiseEvent(new DragDeltaEventArgs(horizontal, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        grip.RaiseEvent(new DragCompletedEventArgs(horizontal, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Entry layout did not finish.");
        if (failure is not null) throw new InvalidOperationException("Entry layout failed.", failure);
    }
}
