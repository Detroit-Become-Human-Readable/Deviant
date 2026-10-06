using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class HomeWorkflowTests
{
    [Fact]
    public void InitialHomeOffersInstallationSelectionBeforeAssetLaunchers() => WorkspaceCommitTests.Run((window, _) =>
    {
        Assert.True(((FrameworkElement)window.FindName("HomeSetupActions")).IsVisible);
        Assert.False(((FrameworkElement)window.FindName("HomeLaunchers")).IsVisible);
        Assert.False(((ProcessingAnimation)window.FindName("HomeProcessing")).IsActive);
    });

    [Fact]
    public void OpeningIndexesOnHomeThenLaunchesTheSelectedView() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        var gate = (SemaphoreSlim)typeof(AudioWindow).GetField("workspaceCommit", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        gate.Wait();
        Task opening;
        try
        {
            opening = window.OpenArchiveAsync(fixture.Index);
            Assert.True(((FrameworkElement)window.FindName("HomeIndexingState")).IsVisible);
            var processing = (ProcessingAnimation)window.FindName("HomeProcessing");
            Assert.True(processing.IsActive);
            Assert.True(processing.Width > ((ProcessingAnimation)window.FindName("WindowProcessing")).Width);
            Assert.False(((FrameworkElement)window.FindName("HomeLaunchers")).IsVisible);
        }
        finally { gate.Release(); }
        Pump(opening);
        Pump(window.CurrentSession!.EnrichmentTask);
        Pump(Task.Delay(200));
        Assert.True(((FrameworkElement)window.FindName("HomeLaunchers")).IsVisible);
        Assert.Equal("Welcome, Deviant", ((TextBlock)window.FindName("HomeTitle")).Text);
        Assert.False(((ProcessingAnimation)window.FindName("HomeProcessing")).IsActive);
        Pump(window.OpenHomeViewAsync("audio"));
        Assert.False(((FrameworkElement)window.FindName("HomeHost")).IsVisible);
        Assert.True(((FrameworkElement)window.FindName("AudioLayout")).IsVisible);
        Assert.Equal("audio", ((NavigationItem)((TreeView)window.FindName("ExplorerTree")).SelectedItem).View);
    });

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
