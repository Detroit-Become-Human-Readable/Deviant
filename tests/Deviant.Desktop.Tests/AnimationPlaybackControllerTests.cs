using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class AnimationPlaybackControllerTests
{
    [Fact]
    public void RapidSeekingKeepsOnlyTheNewestQueuedPoseAndResumesPlayback()
    {
        Run(async () =>
        {
            var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
            var times = new List<double>(); bool first = true;
            await using var controller = new AnimationPlaybackController(Dispatcher.CurrentDispatcher, 10,
                async (time, inPlace, token) =>
                {
                    Assert.True(inPlace); times.Add(time);
                    if (first) { first = false; entered.SetResult(); await release.Task.WaitAsync(token); }
                });
            Assert.False(controller.IsPlaying); Assert.True(controller.InPlace);
            controller.Play();
            var a = controller.SeekAsync(1); await entered.Task;
            var b = controller.SeekAsync(2); var c = controller.SeekAsync(3);
            release.SetResult(); await Task.WhenAll(a, b, c);
            Assert.Equal(new double[] { 1, 3 }, times);
            Assert.True(controller.IsPlaying); controller.Pause();
            Assert.InRange(controller.Position, 3, 3.1);
        });
    }

    [Fact]
    public void PausingDuringASeekPreventsAHiddenPreviewFromRestarting()
    {
        Run(async () =>
        {
            var release = new TaskCompletionSource();
            await using var controller = new AnimationPlaybackController(Dispatcher.CurrentDispatcher, 10,
                (_, _, token) => release.Task.WaitAsync(token));
            controller.Play(); var seek = controller.SeekAsync(2);
            controller.Pause(); release.SetResult(); await seek;
            Assert.False(controller.IsPlaying); Assert.Equal(2, controller.Position);
        });
    }

    [Fact]
    public void UnloadingCancelsAndDrainsThePendingPoseBeforeReturning()
    {
        Run(async () =>
        {
            bool released = false; var entered = new TaskCompletionSource();
            var controller = new AnimationPlaybackController(Dispatcher.CurrentDispatcher, 10,
                async (_, _, token) =>
                {
                    entered.SetResult();
                    try { await Task.Delay(Timeout.Infinite, token); }
                    finally { released = true; }
                });
            var seek = controller.SeekAsync(4); await entered.Task;
            await controller.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => seek);
            Assert.True(released); Assert.False(controller.IsPlaying);
            Assert.Throws<ObjectDisposedException>(controller.Play);
        });
    }

    private static void Run(Func<Task> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var dispatcher = Dispatcher.CurrentDispatcher;
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timeout.Tick += (_, _) => { error = new TimeoutException("Playback test exceeded its deadline."); dispatcher.InvokeShutdown(); };
            timeout.Start();
            dispatcher.BeginInvoke((Action)(async () =>
            {
                try { await body(); } catch (Exception exception) { error = exception; }
                finally { timeout.Stop(); dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Playback dispatcher did not stop.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
