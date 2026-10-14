using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Formats.Audio.Model;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class ResizableLayoutTests
{
    [Fact]
    public void WorkspaceAndAudioSplittersResizeTheirAdjacentColumns()
    {
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            PumpWorkspace(window.OpenArchiveAsync(fixture.Index));
            LoadControlStyles(window);
            window.Width = 1600;
            window.Height = 900;
            Invoke(window, "ShowBrowse", "audio");
            SettleLayout(window);

            var workspace = Assert.IsType<Grid>(window.FindName("WorkspaceLayout"));
            var audio = Assert.IsType<Grid>(window.FindName("AudioLayout"));
            Assert.Equal(3, workspace.ColumnDefinitions.Count);
            Assert.Equal(5, audio.ColumnDefinitions.Count);

            var explorerBefore = workspace.ColumnDefinitions[0].ActualWidth;
            var browseBefore = audio.ColumnDefinitions[0].ActualWidth;
            var resultsBefore = audio.ColumnDefinitions[2].ActualWidth;
            var detailsBefore = audio.ColumnDefinitions[4].ActualWidth;
            Assert.True(explorerBefore > 0 && browseBefore > 0 && resultsBefore > 0 && detailsBefore > 0);

            DragThumb(Assert.IsAssignableFrom<Thumb>(window.FindName("ExplorerPaneSplitter")), 42, 0);
            SettleLayout(window);
            Assert.True(workspace.ColumnDefinitions[0].ActualWidth > explorerBefore + 12);

            DragThumb(Assert.IsAssignableFrom<Thumb>(window.FindName("AudioBrowseSplitter")), 34, 0);
            SettleLayout(window);
            Assert.True(audio.ColumnDefinitions[0].ActualWidth > browseBefore + 8);
            Assert.True(audio.ColumnDefinitions[2].ActualWidth < resultsBefore - 4);

            var resultsAfterBrowseResize = audio.ColumnDefinitions[2].ActualWidth;
            DragThumb(Assert.IsAssignableFrom<Thumb>(window.FindName("AudioDetailsSplitter")), 38, 0);
            SettleLayout(window);
            Assert.True(audio.ColumnDefinitions[2].ActualWidth > resultsAfterBrowseResize + 8);
            Assert.True(audio.ColumnDefinitions[4].ActualWidth < detailsBefore - 4);

            Assert.True(workspace.ColumnDefinitions[1].ActualWidth > 0);
            Assert.True(audio.ColumnDefinitions[1].ActualWidth > 0);
            Assert.True(audio.ColumnDefinitions[3].ActualWidth > 0);
        });
    }

    [Fact]
    public void WideAudioLayoutKeepsNameAndDefaultBankMetadataBalanced()
    {
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            PumpWorkspace(window.OpenArchiveAsync(fixture.Index));
            LoadControlStyles(window);
            window.Width = 2560;
            window.Height = 1392;
            Invoke(window, "ShowBrowse", "audio");
            var mediaList = (ListView)window.FindName("MediaList");
            var selected = Row(1, "Generated dialogue");
            mediaList.ItemsSource = new[] { selected };
            mediaList.SelectedItem = selected;
            SettleLayout(window);
            AssertUsableLayout(window);

            Assert.InRange(window.ActualWidth, 2500, 2600);
            var columns = Assert.IsType<GridView>(mediaList.View).Columns;
            Assert.Collection(columns,
                column => AssertColumn(column, "Name", 300),
                column => AssertColumn(column, "Bank", 180),
                column => AssertColumn(column, "Language", 100),
                column => AssertColumn(column, "Codec", 110),
                column => AssertColumn(column, "Duration", 80),
                column => AssertColumn(column, "Ch", 48));

            Assert.True(columns[0].ActualWidth > columns[1].ActualWidth);
            Assert.True(columns[1].ActualWidth > columns[2].ActualWidth);
            Assert.True(columns[1].ActualWidth >= 160);
            Assert.True(columns[2].ActualWidth >= 90);
            Assert.Equal("Generated bank", selected.Bank);
            Assert.Equal("ENG", selected.Media.Language);
        });
    }

    [Fact]
    public void NameGripperDragPersistsAcrossWindowAndPaneResizesWithoutHidingMetadata()
    {
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            PumpWorkspace(window.OpenArchiveAsync(fixture.Index));
            LoadControlStyles(window);
            window.Width = 1600;
            window.Height = 900;
            Invoke(window, "ShowBrowse", "audio");
            var mediaList = (ListView)window.FindName("MediaList");
            mediaList.ItemsSource = new[] { Row(1, "Generated dialogue"), Row(2, "Generated event") };
            mediaList.SelectedIndex = 0;
            mediaList.ApplyTemplate();
            SettleLayout(window);

            var view = Assert.IsType<GridView>(mediaList.View);
            var name = Assert.Single(view.Columns.Cast<GridViewColumn>(), column => Equals(column.Header, "Name"));
            var beforeDrag = name.ActualWidth;
            var header = FindVisualDescendants<GridViewColumnHeader>(mediaList)
                .SingleOrDefault(item => ReferenceEquals(item.Column, name))
                ?? throw new InvalidOperationException("The Name header was not realized.");
            header.ApplyTemplate();
            SettleLayout(window);
            var styles = window.Resources.MergedDictionaries.Single(dictionary =>
                dictionary.Source?.OriginalString.EndsWith("ControlStyles.xaml", StringComparison.OrdinalIgnoreCase) == true);
            var expectedHeaderStyle = Assert.IsType<Style>(styles[typeof(GridViewColumnHeader)]);
            Assert.Same(expectedHeaderStyle, header.Style);
            var gripper = Assert.IsType<Thumb>(header.Template.FindName("PART_HeaderGripper", header));

            DragThumb(gripper, 46, 0);
            SettleLayout(window);
            var afterDrag = name.ActualWidth;
            Assert.True(afterDrag > beforeDrag + 16, $"The real header gripper changed Name width from {beforeDrag} to {afterDrag}.");

            Resize(window, 1000, 660);
            AssertUsableLayout(window);
            AssertResponsivePlayerWithinPanel(window);
            Assert.True(name.ActualWidth >= afterDrag - 2, "The narrow-window resize reset the user's Name width.");
            AssertBankAndLanguageRemainVisible(view);

            var browseSplitter = Assert.IsAssignableFrom<Thumb>(window.FindName("AudioBrowseSplitter"));
            DragThumb(browseSplitter, 24, 0);
            SettleLayout(window);
            Assert.True(name.ActualWidth >= afterDrag - 2, "Resizing the Browse pane reset the user's Name width.");
            AssertBankAndLanguageRemainVisible(view);

            Resize(window, 2560, 1392);
            AssertUsableLayout(window);
            Assert.True(name.ActualWidth >= afterDrag - 2, "The wide-window resize reset the user's Name width.");
            AssertBankAndLanguageRemainVisible(view);
        });
    }

    [Theory]
    [InlineData("asset-family")]
    [InlineData("buffer")]
    [InlineData("shader")]
    [InlineData("animation")]
    public void ExistingInspectionViewsKeepTheirTwoPaneSplittersInteractive(string viewName)
    {
        WorkspaceCommitTests.Run((window, fixture) =>
        {
            PumpWorkspace(window.OpenArchiveAsync(fixture.Index));
            LoadControlStyles(window);
            window.Width = 1600;
            window.Height = 900;
            var view = viewName switch
            {
                "asset-family" => (UserControl)window.AssetBrowser,
                "buffer" => window.BufferBrowser,
                "shader" => window.ShaderBrowser,
                "animation" => window.AnimationBrowser,
                _ => throw new ArgumentOutOfRangeException(nameof(viewName))
            };
            if (viewName == "asset-family")
                ((ContentControl)window.FindName("AssetsHost")).Content = view;
            Invoke(window, "ShowBrowse", viewName == "asset-family" ? "textures" : viewName);
            SettleLayout(window);

            var splitter = Assert.Single(FindVisualDescendants<GridSplitter>(view), item =>
                AutomationProperties.GetName(item) == "Resize results and details");
            var parent = Assert.IsType<Grid>(VisualTreeHelper.GetParent(splitter));
            Assert.Equal(3, parent.ColumnDefinitions.Count);
            var leftBefore = parent.ColumnDefinitions[0].ActualWidth;
            var rightBefore = parent.ColumnDefinitions[2].ActualWidth;
            Assert.True(leftBefore > 0 && rightBefore > 0);

            DragThumb(splitter, 32, 0);
            SettleLayout(window);

            Assert.True(parent.ColumnDefinitions[0].ActualWidth > leftBefore + 6);
            Assert.True(parent.ColumnDefinitions[2].ActualWidth < rightBefore - 6);
            Assert.True(splitter.ActualWidth > 0);
        });
    }

    private static void AssertColumn(GridViewColumn column, string header, double expectedWidth)
    {
        Assert.Equal(header, column.Header?.ToString());
        Assert.InRange(column.Width, expectedWidth - 1, expectedWidth + 1);
        Assert.InRange(column.ActualWidth, expectedWidth - 1, expectedWidth + 1);
    }

    private static void AssertBankAndLanguageRemainVisible(GridView view)
    {
        var bank = Assert.Single(view.Columns.Cast<GridViewColumn>(), column => Equals(column.Header, "Bank"));
        var language = Assert.Single(view.Columns.Cast<GridViewColumn>(), column => Equals(column.Header, "Language"));
        Assert.True(bank.ActualWidth >= 160);
        Assert.True(language.ActualWidth >= 90);
    }

    private static void AssertUsableLayout(AudioWindow window)
    {
        var workspace = Assert.IsType<Grid>(window.FindName("WorkspaceLayout"));
        var audio = Assert.IsType<Grid>(window.FindName("AudioLayout"));
        Assert.True(workspace.ColumnDefinitions[0].ActualWidth >= 140);
        Assert.True(audio.ColumnDefinitions[0].ActualWidth >= 160);
        Assert.True(audio.ColumnDefinitions[2].ActualWidth >= 240);
        Assert.True(audio.ColumnDefinitions[4].ActualWidth >= 230);
        Assert.True(((FrameworkElement)window.FindName("ExplorerPaneSplitter")).ActualWidth > 0);
        Assert.True(((FrameworkElement)window.FindName("AudioBrowseSplitter")).ActualWidth > 0);
        Assert.True(((FrameworkElement)window.FindName("AudioDetailsSplitter")).ActualWidth > 0);
    }

    private static void Resize(AudioWindow window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        SettleLayout(window);
        Assert.InRange(window.ActualWidth, width - 5, width + 5);
        Assert.InRange(window.ActualHeight, height - 5, height + 5);
    }

    private static void LoadControlStyles(AudioWindow window)
    {
        var assemblyName = typeof(AudioWindow).Assembly.GetName().Name
            ?? throw new InvalidOperationException("The desktop assembly has no name.");
        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"/{assemblyName};component/ControlStyles.xaml", UriKind.Relative)
        };
        window.Resources.MergedDictionaries.Add(dictionary);
        SettleLayout(window);
    }

    private static void SettleLayout(AudioWindow window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            window.UpdateLayout();
            frame.Continue = false;
        }));
        Dispatcher.PushFrame(frame);
    }

    private static void AssertResponsivePlayerWithinPanel(AudioWindow window)
    {
        var panel = (FrameworkElement)window.FindName("PlayerPanel");
        Assert.True(panel.ActualWidth > 0 && panel.ActualHeight > 0);
        foreach (var name in new[] { "PlayButton", "PauseButton", "DeviceBox", "PlayingLabel", "SeekSlider", "PlayTime", "VolumeSlider" })
        {
            var control = (FrameworkElement)window.FindName(name);
            if (control.Visibility != Visibility.Visible) continue;
            var bounds = control.TransformToAncestor(panel).TransformBounds(new Rect(new Point(0, 0), control.RenderSize));
            Assert.True(bounds.Left >= -2 && bounds.Top >= -2 && bounds.Right <= panel.ActualWidth + 2 &&
                bounds.Bottom <= panel.ActualHeight + 2,
                $"{name} extends beyond PlayerPanel at the narrow window size: {bounds} / {panel.RenderSize}.");
        }
    }

    private static void DragThumb(Thumb thumb, double horizontal, double vertical)
    {
        Assert.True(thumb.IsEnabled);
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(horizontal, vertical) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(horizontal, vertical, false) { RoutedEvent = Thumb.DragCompletedEvent });
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is not Visual) yield break;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualDescendants<T>(child)) yield return nested;
        }
    }

    private static AudioExplorerRow Row(long ordinal, string label)
    {
        var identity = new ResourceIdentity("generated-index", ordinal);
        var revision = new SourceRevision("generated-index", "generated-source");
        var media = new MediaEntry
        {
            Identity = identity,
            Name = label,
            BankName = "Generated bank",
            Language = "ENG",
            Codec = "Generated codec",
            Duration = 12.5,
            Channels = 2,
            Completeness = MediaCompleteness.Complete,
            Availability = SourceAvailability.Available
        };
        var descriptor = new AssetDescriptor(identity, revision, AudioExplorerSession.AssetFamily, label, null,
            AssetAvailability.Available, AssetCompleteness.Complete, AssetCapabilities.Preview, [], []);
        return new(descriptor, media, null);
    }

    private static void PumpWorkspace(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
    private static object? Invoke(AudioWindow window, string name, params object?[] args) =>
        window.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
}
