using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class WorkspaceAdmissionTests
{
    [Fact]
    public void StartupKeepsAssetNavigationHiddenAndProgrammaticBrowseRequestsReturnHome() => WorkspaceCommitTests.Run((window, _) =>
    {
        Assert.False(window.IsWorkspaceValidated);
        AssertNavigationHidden(window);

        Pump(window.OpenHomeViewAsync("audio"));
        AssertHomeAndHidden(window);
        Assert.Contains("Choose a valid game installation", Named<TextBlock>(window, "HomeStatus").Text, StringComparison.Ordinal);

        Invoke(window, "ShowBrowse", "textures");
        AssertHomeAndHidden(window);
        Invoke(window, "Advanced_Click", window, new RoutedEventArgs());
        AssertHomeAndHidden(window);
    });

    [Theory]
    [InlineData("invalid-index")]
    [InlineData("missing-volume")]
    public void InvalidIndexOrMissingVolumeNeverCommitsWorkspace(string failure) => WorkspaceCommitTests.Run((window, fixture) =>
    {
        var invalidIndex = Path.Combine(fixture.Root, "invalid", failure + ".idx");
        if (failure == "invalid-index")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(invalidIndex)!);
            File.WriteAllBytes(invalidIndex, [0, 1, 2, 3]);
        }
        else
        {
            DiscoveryFixture.WriteIndex(invalidIndex, [(999u, new byte[] { 1, 2, 3 })]);
            File.Delete(Path.ChangeExtension(invalidIndex, ".dat"));
        }

        Pump(window.OpenArchiveAsync(invalidIndex));

        Assert.Null(window.CurrentSession);
        Assert.False(window.IsWorkspaceValidated);
        AssertHomeAndHidden(window);
    });

    [Fact]
    public void ValidGeneratedArchiveRevealsWorkspaceNavigation() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        Pump(window.OpenArchiveAsync(fixture.Index));

        Assert.NotNull(window.CurrentSession);
        Assert.True(window.IsWorkspaceValidated);
        Assert.Equal(Visibility.Visible, Named<FrameworkElement>(window, "ExplorerNavigationPane").Visibility);
        Assert.Equal(Visibility.Visible, Named<FrameworkElement>(window, "ExplorerPaneSplitter").Visibility);
        Assert.Equal(Visibility.Visible, Named<TabItem>(window, "CharactersTab").Visibility);
        Assert.True(Named<TabItem>(window, "CharactersTab").IsEnabled);
        Assert.Equal(Visibility.Visible, Named<Button>(window, "AdvancedButton").Visibility);
        window.UpdateLayout();
        Assert.True(Named<Grid>(window, "WorkspaceLayout").ColumnDefinitions[0].ActualWidth > 0);
    });

    [Fact]
    public void NavigationRemainsHiddenWhileAReplacementWaitsToCommit() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        Pump(window.OpenArchiveAsync(fixture.Index));
        Assert.True(window.IsWorkspaceValidated);

        var replacement = Path.Combine(fixture.Root, "replacement", "archive.idx");
        DiscoveryFixture.WriteIndex(replacement, [(999u, new byte[] { 4, 5, 6 })]);
        var gate = Field<SemaphoreSlim>(window, "workspaceCommit");
        gate.Wait();
        Task opening;
        try
        {
            opening = window.OpenArchiveAsync(replacement);
            Pump(WaitUntilAsync(() => Field<bool>(window, "indexOpeningActive") && !window.IsWorkspaceValidated));

            Assert.False(opening.IsCompleted);
            AssertNavigationHidden(window);
        }
        finally
        {
            gate.Release();
        }

        Pump(opening);
        Assert.True(window.IsWorkspaceValidated);
        Assert.Equal(Path.GetFullPath(replacement), window.CurrentSession!.Snapshot.IndexPath);
    });

    [Fact]
    public void CacheDrainRemovesAdmissionAndHidesWorkspaceNavigation() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        Pump(window.OpenArchiveAsync(fixture.Index));
        Assert.True(window.IsWorkspaceValidated);

        Pump((Task)Invoke(window, "DrainCacheUsersAsync", CancellationToken.None)!);

        Assert.Null(window.CurrentSession);
        Assert.False(window.IsWorkspaceValidated);
        AssertNavigationHidden(window);
    });

    [Fact]
    public void FailedReplacementPreservesPreviouslyValidatedSnapshotAndNavigation() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        Pump(window.OpenArchiveAsync(fixture.Index));
        var admitted = window.CurrentSession!;
        Assert.True(window.IsWorkspaceValidated);

        var invalidIndex = Path.Combine(fixture.Root, "replacement", "invalid.idx");
        Directory.CreateDirectory(Path.GetDirectoryName(invalidIndex)!);
        File.WriteAllBytes(invalidIndex, [0, 1, 2, 3]);
        Pump(window.OpenArchiveAsync(invalidIndex));

        Assert.Same(admitted, window.CurrentSession);
        Assert.Same(admitted.Snapshot, window.CurrentSession!.Snapshot);
        Assert.True(window.IsWorkspaceValidated);
        Assert.Equal(Visibility.Visible, Named<FrameworkElement>(window, "ExplorerNavigationPane").Visibility);
        Assert.Equal(Visibility.Visible, Named<FrameworkElement>(window, "ExplorerPaneSplitter").Visibility);
        Assert.Equal(Visibility.Visible, Named<Button>(window, "AdvancedButton").Visibility);
    });

    private static void AssertHomeAndHidden(AudioWindow window)
    {
        Assert.Equal(Visibility.Visible, Named<FrameworkElement>(window, "HomeHost").Visibility);
        AssertNavigationHidden(window);
    }

    private static void AssertNavigationHidden(AudioWindow window)
    {
        window.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, Named<FrameworkElement>(window, "ExplorerNavigationPane").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<FrameworkElement>(window, "ExplorerPaneSplitter").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<TabItem>(window, "CharactersTab").Visibility);
        Assert.False(Named<TabItem>(window, "CharactersTab").IsEnabled);
        Assert.Equal(Visibility.Collapsed, Named<Button>(window, "AdvancedButton").Visibility);
        Assert.Equal(0, Named<Grid>(window, "WorkspaceLayout").ColumnDefinitions[0].ActualWidth);
    }

    private static T Named<T>(AudioWindow window, string name) where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(window.FindName(name));

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

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
}
