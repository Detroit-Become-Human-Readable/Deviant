using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Deviant.Assets;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class WorkspaceNavigationConsistencyTests
{
    [Theory]
    [InlineData(1000)]
    [InlineData(1600)]
    public void CharactersKeepExplorerAndShareTheContentArea(double width) => WorkspaceCommitTests.Run((window, fixture) =>
    {
        Pump(window.OpenArchiveAsync(fixture.Index));
        window.Width = width;
        window.CharacterCatalogProvider = (_, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<AssetDescriptor>>([]); };
        Pump(window.OpenHomeViewAsync("characters"));
        window.UpdateLayout();
        var explorer = Named<TreeView>(window, "ExplorerTree");
        Assert.True(Named<FrameworkElement>(window, "ExplorerNavigationPane").IsVisible);
        Assert.True(Named<FrameworkElement>(window, "ExplorerPaneSplitter").IsVisible);
        Assert.True(window.CharactersBrowser.IsVisible);
        Assert.Equal("characters", Assert.IsType<NavigationItem>(explorer.SelectedItem).View);
        Assert.Same(Named<Grid>(window, "WorkspaceLayout"), VisualTreeHelper.GetParent(Named<TabControl>(window, "WorkspaceTabs")));
        Assert.Single(explorer.Items.Cast<NavigationItem>(), item => item.Label == "Dialogue & Strings");
        Assert.DoesNotContain(explorer.Items.Cast<NavigationItem>(), item => item.Label is "Dialogue" or "Strings");
        Assert.Equal("Dialogue & Strings", Named<Button>(window, "HomeStringsButton").Content);
        Invoke(window, "Home_Click", window, new RoutedEventArgs());
        Assert.True(Named<FrameworkElement>(window, "ExplorerNavigationPane").IsVisible);
        Assert.True(Named<FrameworkElement>(window, "HomeHost").IsVisible);
        Assert.False(window.CharactersBrowser.IsVisible);
    });

    [Fact]
    public void SharedCancelDrainsCharacterQueryAndClearsFooter() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        Pump(window.OpenArchiveAsync(fixture.Index));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<AssetDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken received = default;
        window.CharacterCatalogProvider = (_, token) => { received = token; entered.SetResult(); return release.Task.WaitAsync(token); };
        var show = window.ShowCharactersAsync();
        Pump(entered.Task);
        Invoke(window, "RefreshActivity");
        Assert.True(Named<ProcessingAnimation>(window, "WindowProcessing").IsActive);
        Assert.True(Named<Button>(window, "WorkspaceCancelButton").IsVisible);
        Assert.Contains("Reading characters", Named<TextBlock>(window, "StatusText").Text, StringComparison.Ordinal);
        Invoke(window, "WorkspaceCancel_Click", window, new RoutedEventArgs());
        Pump(show);
        Pump(Task.Delay(50));
        Invoke(window, "RefreshActivity");
        Assert.True(received.IsCancellationRequested);
        Assert.False(Named<ProcessingAnimation>(window, "WindowProcessing").IsActive);
        Assert.Equal(Visibility.Collapsed, Named<Button>(window, "WorkspaceCancelButton").Visibility);
        Assert.Contains("Cancelled", Named<TextBlock>(window, "StatusText").Text, StringComparison.Ordinal);
    });

    [Fact]
    public void CharacterFailureAndLeavingPendingQueryClearTheFooter() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        Pump(window.OpenArchiveAsync(fixture.Index));
        window.CharacterCatalogProvider = (_, _) => Task.FromException<IReadOnlyList<AssetDescriptor>>(new IOException("Catalog unavailable"));
        Pump(window.ShowCharactersAsync());
        Invoke(window, "RefreshActivity");
        Assert.False(Named<ProcessingAnimation>(window, "WindowProcessing").IsActive);
        Assert.Equal("Catalog unavailable", Named<TextBlock>(window, "StatusText").Text);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<AssetDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.CharacterCatalogProvider = (_, token) => { entered.SetResult(); return release.Task.WaitAsync(token); };
        var show = window.ShowCharactersAsync(); Pump(entered.Task);
        Invoke(window, "Home_Click", window, new RoutedEventArgs()); Pump(show);
        Invoke(window, "RefreshActivity");
        Assert.True(Named<FrameworkElement>(window, "HomeHost").IsVisible);
        Assert.False(Named<ProcessingAnimation>(window, "WindowProcessing").IsActive);
        Assert.DoesNotContain("Reading characters", Named<TextBlock>(window, "StatusText").Text, StringComparison.Ordinal);
    });

    private static T Named<T>(AudioWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static object? Invoke(object owner, string method, params object?[] args) => owner.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, args);
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
}
