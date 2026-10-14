using System.Reflection;
using System.Windows.Threading;
using Deviant.Desktop;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class NativeTextureActivityDrainTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    public void DrainWaitsForActivityCleanupWithoutStarvingUiWork(int outcome, bool initialPublicationThrows)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                var view = new NativeTextureView();
                var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var track = typeof(NativeTextureView).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Single(method => method.Name == "TrackActivity" && !method.IsGenericMethod);
                var drainMethod = typeof(NativeTextureView).GetMethod("DrainActivityAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Task? reentrantDrain = null;
                var reentrantDrainReturnedEarly = false;
                var throwPublication = initialPublicationThrows;
                view.ActivityProgressChanged += (_, update) =>
                {
                    if (update.IsActive && throwPublication)
                    {
                        throwPublication = false;
                        throw new InvalidOperationException("Activity subscriber failed");
                    }
                    if (!update.IsActive)
                    {
                        reentrantDrain = (Task)drainMethod.Invoke(view, null)!;
                        reentrantDrainReturnedEarly |= reentrantDrain.IsCompleted;
                    }
                };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher, DispatcherPriority.Background));
                if (initialPublicationThrows)
                    Assert.Throws<TargetInvocationException>(() => track.Invoke(view, ["Preview", 10, operation.Task]));
                else track.Invoke(view, ["Preview", 10, operation.Task]);
                track.Invoke(view, ["Preview", 10, operation.Task]);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher, DispatcherPriority.Send));
                if (outcome == 0) operation.SetResult();
                else if (outcome == 1) operation.SetCanceled();
                else operation.SetException(new InvalidOperationException("Preview failed"));

                var drain = (Task)drainMethod.Invoke(view, null)!;
                var frame = new DispatcherFrame();
                var dispatched = 0;
                var renderRan = false;
                dispatcher.Hooks.OperationStarted += (_, _) =>
                {
                    if (++dispatched >= 200) frame.Continue = false;
                };
                dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => renderRan = true));
                _ = drain.ContinueWith(async _ =>
                {
                    await reentrantDrain!;
                    await dispatcher.InvokeAsync(() => frame.Continue = false, DispatcherPriority.Background);
                }, TaskScheduler.Default).Unwrap();
                Dispatcher.PushFrame(frame);

                Assert.True(drain.IsCompletedSuccessfully, "Activity cleanup did not finish before repeated UI callbacks exhausted the bound.");
                Assert.True(renderRan, "Activity draining starved queued rendering work.");
                Assert.False(view.IsActivityActive);
                Assert.False(reentrantDrainReturnedEarly, "A drain returned before the terminal activity publication finished.");
                Assert.True(reentrantDrain?.IsCompletedSuccessfully == true);
                Assert.Empty(((System.Collections.IDictionary)typeof(NativeTextureView)
                    .GetField("activities", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!).Keys.Cast<object>());
            }
            catch (Exception error) { failure = error; }
            finally { dispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Activity drain check timed out.");
        if (failure is not null) throw new InvalidOperationException("Activity drain check failed.", failure);
    }
}
