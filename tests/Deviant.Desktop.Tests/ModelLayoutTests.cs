using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed partial class ModelLayoutTests
{
    [Fact]
    public void NarrowInspectorMovesBelowViewportThenPreservesItsHeightAndWideColumnWidth()
    {
        RunSta(() =>
        {
            var view = new AssetModelView();
            var window = new Window
            {
                Content = view,
                Width = 420,
                Height = 660,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                ShowActivated = false,
                ShowInTaskbar = false
            };

            try
            {
                window.Show();
                Settle(window);
                var grid = Named<Grid>(view, "ModelLayout");
                var viewport = Named<FrameworkElement>(view, "ModelViewport");
                var inspector = Named<DockPanel>(view, "ModelInspector");
                var splitter = Named<GridSplitter>(view, "ModelInspectorSplitter");

                Assert.True(view.ActualWidth < 640, $"Expected compact layout but view width was {view.ActualWidth:F1}px.");
                Assert.True(viewport.ActualWidth > 0 && viewport.ActualHeight > 0);
                Assert.True(inspector.ActualWidth > 0 && inspector.ActualHeight >= 80);
                Assert.Equal(2, Grid.GetRow(inspector));
                Assert.Equal(0, Grid.GetColumn(inspector));
                Assert.Equal(GridResizeDirection.Rows, splitter.ResizeDirection);
                Assert.True(splitter.ActualHeight > 0);

                var viewportHeightBefore = grid.RowDefinitions[0].ActualHeight;
                var inspectorHeightBefore = grid.RowDefinitions[2].ActualHeight;
                Drag(splitter, horizontal: 0, vertical: -36);
                var narrowInspectorHeight = grid.RowDefinitions[2].ActualHeight;
                Assert.True(narrowInspectorHeight > inspectorHeightBefore + 1);
                Assert.True(grid.RowDefinitions[0].ActualHeight < viewportHeightBefore - 1);

                window.Width = 1000;
                Settle(window);
                Assert.True(view.ActualWidth >= 640, $"Expected wide layout but view width was {view.ActualWidth:F1}px.");
                Assert.Equal(0, Grid.GetRow(inspector));
                Assert.Equal(2, Grid.GetColumn(inspector));
                Assert.True(inspector.ActualWidth >= 240);
                Assert.True(inspector.ActualHeight > 0);
                Assert.Equal(GridResizeDirection.Columns, splitter.ResizeDirection);
                Assert.True(splitter.ActualWidth > 0);

                var inspectorWidthBefore = grid.ColumnDefinitions[2].ActualWidth;
                Drag(splitter, horizontal: -42, vertical: 0);
                var wideInspectorWidth = grid.ColumnDefinitions[2].ActualWidth;
                Assert.True(wideInspectorWidth > inspectorWidthBefore + 1);

                window.Width = 420;
                Settle(window);
                Assert.True(view.ActualWidth < 640);
                Assert.Equal(2, Grid.GetRow(inspector));
                Assert.True(Math.Abs(grid.RowDefinitions[2].ActualHeight - narrowInspectorHeight) <= 1.5,
                    $"The chosen lower-pane height was not restored: expected {narrowInspectorHeight:F1}px, got {grid.RowDefinitions[2].ActualHeight:F1}px.");
                Assert.True(inspector.ActualWidth > 0 && inspector.ActualHeight >= 80);

                window.Width = 1000;
                Settle(window);
                Assert.Equal(2, Grid.GetColumn(inspector));
                Assert.True(Math.Abs(grid.ColumnDefinitions[2].ActualWidth - wideInspectorWidth) <= 1.5,
                    $"The chosen inspector width was not restored: expected {wideInspectorWidth:F1}px, got {grid.ColumnDefinitions[2].ActualWidth:F1}px.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static T Named<T>(FrameworkElement view, string name) where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(view.FindName(name));

    private static void Drag(GridSplitter splitter, double horizontal, double vertical)
    {
        Assert.True(splitter.IsEnabled);
        splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        splitter.RaiseEvent(new DragDeltaEventArgs(horizontal, vertical) { RoutedEvent = Thumb.DragDeltaEvent });
        splitter.RaiseEvent(new DragCompletedEventArgs(horizontal, vertical, false) { RoutedEvent = Thumb.DragCompletedEvent });
        Settle(splitter);
    }

    private static void Settle(FrameworkElement element)
    {
        element.UpdateLayout();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            element.UpdateLayout();
            frame.Continue = false;
        }));
        Dispatcher.PushFrame(frame);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "The model layout check timed out.");
        if (failure is not null) throw new InvalidOperationException("The model layout check failed.", failure);
    }
}
