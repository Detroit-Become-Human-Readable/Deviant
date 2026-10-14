using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed partial class ModelLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactSplitterCanGrowDetailsAfterItsDefaultHeightWasClamped(bool loadControlStyles)
    {
        RunSta(() =>
        {
            var view = new AssetModelView();
            var window = new Window {Content = view, Width = 420, Height = 600,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false};
            if(loadControlStyles)window.Resources.MergedDictionaries.Add(new ResourceDictionary
                {Source = new System.Uri("/Deviant;component/ControlStyles.xaml", System.UriKind.Relative)});
            try
            {
                window.Show(); Settle(window);
                var grid = Named<Grid>(view, "ModelLayout");
                var before = grid.RowDefinitions[2].ActualHeight;
                Drag(Named<GridSplitter>(view, "ModelInspectorSplitter"), 0, -36);
                var chosen = grid.RowDefinitions[2].ActualHeight;
                Assert.True(chosen >= before + 30, $"Details grew from {before:F1}px to only {chosen:F1}px.");
                Assert.True(Named<FrameworkElement>(view, "ModelViewport").ActualHeight >= 140);
                window.Width = 1000; Settle(window);
                window.Width = 420; Settle(window);
                Assert.InRange(grid.RowDefinitions[2].ActualHeight, chosen - 1.5, chosen + 1.5);
            }
            finally {window.Close();}
        });
    }

    [Fact]
    public void CompactShortPaneKeepsMostSpaceForTheViewportAsHeightChanges()
    {
        RunSta(() =>
        {
            var view = new AssetModelView();
            var window = new Window {Content = view, Width = 300, Height = 360,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false};
            try
            {
                window.Show(); Settle(window);
                var grid = Named<Grid>(view, "ModelLayout");
                var viewport = Named<FrameworkElement>(view, "ModelViewport");
                foreach(var height in new[] {360d, 460d, 320d})
                {
                    window.Height = height; Settle(window);
                    Assert.True(viewport.ActualHeight >= 140, $"Viewport height was {viewport.ActualHeight:F1}px.");
                    Assert.True(viewport.ActualHeight >= grid.ActualHeight * .6,
                        $"Viewport received only {viewport.ActualHeight:F1}px of {grid.ActualHeight:F1}px.");
                    Assert.True(Named<FrameworkElement>(view, "ModelInspector").ActualHeight > 0);
                }
            }
            finally {window.Close();}
        });
    }

    [Fact]
    public void CompactDetailsCanCollapseAndReopenWhilePreservingTheViewport()
    {
        RunSta(() =>
        {
            var view = new AssetModelView();
            var window = new Window {Content = view, Width = 300, Height = 380,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false};
            try
            {
                window.Show(); Settle(window);
                var toggle = Named<ToggleButton>(view, "ModelDetailsToggle");
                var body = Named<FrameworkElement>(view, "ModelInspectorBody");
                var viewport = Named<FrameworkElement>(view, "ModelViewport");
                var originalHeight = viewport.ActualHeight;
                toggle.IsChecked = false; Settle(window);
                Assert.Equal(Visibility.Collapsed, body.Visibility);
                Assert.True(viewport.ActualHeight > originalHeight + 30);
                Assert.False(Named<GridSplitter>(view, "ModelInspectorSplitter").IsEnabled);
                window.Width = 1000; Settle(window);
                Assert.Equal(Visibility.Visible, body.Visibility);
                window.Width = 300; Settle(window);
                Assert.Equal(Visibility.Collapsed, body.Visibility);
                toggle.IsChecked = true; Settle(window);
                Assert.Equal(Visibility.Visible, body.Visibility);
                Assert.True(viewport.ActualHeight >= 140);
            }
            finally {window.Close();}
        });
    }
}
