using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Deviant.Browser;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class RecordsNavigationTests
{
    [Fact]
    public void RecordsCategoriesFollowTheReplacedWorkspace() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        DiscoveryFixture.WriteIndex(fixture.Index, [(999u, new byte[] { 1, 2, 3 })]);
        Pump(window.OpenArchiveAsync(fixture.Index));
        Pump(window.OpenHomeViewAsync("records"));
        PumpIdle();
        var view = Assert.IsType<RecordCategoriesView>(Named<Border>(window, "RecordsHost").Child);
        var categories = Assert.IsType<ItemsControl>(view.FindName("CategoryButtons"));
        Assert.Empty(categories.Items);

        using var replacement = new DiscoveryFixture();
        DiscoveryFixture.WriteIndex(replacement.Index, [(29u, new byte[] { 1, 2, 3 })]);
        Pump(window.OpenArchiveAsync(replacement.Index));
        PumpIdle();
        Assert.Equal("records", Field<string>(window, "currentView"));
        Assert.Equal(new[] { "shader-picker", "physics-picker" },
            categories.Items.Cast<NavigationItem>().Select(item => item.View));
    });

    [Fact]
    public void RecordsNavigationSettlesWithoutStartingAudio() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        DiscoveryFixture.WriteIndex(fixture.Index, [(999u, new byte[] { 1, 2, 3 })]);
        Pump(window.OpenArchiveAsync(fixture.Index));
        Assert.True(window.IsWorkspaceValidated);
        var session = Assert.IsType<AudioExplorerSession>(window.CurrentSession);
        var audioInitialization = Field<Lazy<Task>>(session, "audioInitialization");
        Assert.False(audioInitialization.IsValueCreated);

        var explorer = Named<TreeView>(window, "ExplorerTree");
        explorer.ApplyTemplate();
        window.UpdateLayout();
        explorer.UpdateLayout();
        PumpIdle();
        var records = Assert.Single(explorer.Items.Cast<NavigationItem>(), item => item.View == "records");
        var home = Assert.Single(explorer.Items.Cast<NavigationItem>(), item => item.View == "home");
        var recordsItem = Assert.IsType<TreeViewItem>(explorer.ItemContainerGenerator.ContainerFromItem(records));
        var homeItem = Assert.IsType<TreeViewItem>(explorer.ItemContainerGenerator.ContainerFromItem(home));
        recordsItem.BringIntoView();
        PumpIdle();
        for (var visit = 0; visit < 3; visit++)
        {
            ReturnHome(window);
            PumpIdle();
            Assert.True(homeItem.IsSelected);
            Assert.Equal("home", Field<string>(window, "currentView"));
            Assert.False(audioInitialization.IsValueCreated);

            recordsItem.IsSelected = true;
            PumpIdle();
            PumpIdle();
            Assert.True(recordsItem.IsSelected);
            Assert.Same(records, explorer.SelectedItem);
            Assert.Equal("records", Field<string>(window, "currentView"));
            Assert.True(Named<FrameworkElement>(window, "RecordsHost").IsVisible);
            Assert.False(Named<FrameworkElement>(window, "AudioLayout").IsVisible);
            Assert.False(Named<FrameworkElement>(window, "HomeHost").IsVisible);
            Assert.False(audioInitialization.IsValueCreated);
        }
    });

    private static T Named<T>(AudioWindow window, string name) where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(window.FindName(name));

    private static T Field<T>(object owner, string name) =>
        Assert.IsType<T>(owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner));

    private static void ReturnHome(AudioWindow window) =>
        typeof(AudioWindow).GetMethod("Home_Click", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [window, new RoutedEventArgs()]);

    private static void PumpIdle() =>
        Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
