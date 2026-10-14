using System.Diagnostics;
using System.Windows.Threading;

namespace Deviant.Desktop;

/// <summary>Owns one preview clock and one asynchronous pose update at a time.</summary>
internal sealed class AnimationPlaybackController : IAsyncDisposable
{
    private readonly DispatcherTimer timer;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Func<double, bool, CancellationToken, Task> update;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim updateGate = new(1, 1);
    private long seekGeneration;
    private bool resumeAfterSeek;
    private Task? frameTask;
    private double baseSeconds, startedSeconds;
    private bool disposed;
    public AnimationPlaybackController(Dispatcher dispatcher, double duration,
        Func<double, bool, CancellationToken, Task> update)
    {
        if (!double.IsFinite(duration) || duration <= 0) throw new ArgumentOutOfRangeException(nameof(duration));
        Duration = duration; this.update = update;
        timer = new DispatcherTimer(DispatcherPriority.Render, dispatcher) { Interval = TimeSpan.FromSeconds(1.0 / 30) };
        timer.Tick += Tick;
    }
    public double Duration { get; }
    public double Position => IsPlaying ? CurrentTime() : baseSeconds;
    public double Speed { get; private set; } = 1;
    public bool IsPlaying { get; private set; }
    public bool Loop { get; set; } = true;
    public bool InPlace { get; set; } = true;
    public event EventHandler? Changed;
    public event EventHandler<Exception>? Failed;

    public void Play()
    { ObjectDisposedException.ThrowIf(disposed, this); if (IsPlaying) return; if (!Loop && baseSeconds >= Duration) baseSeconds = 0; startedSeconds = clock.Elapsed.TotalSeconds; IsPlaying = true; timer.Start(); Changed?.Invoke(this, EventArgs.Empty); }
    public void Pause()
    { resumeAfterSeek = false; PauseClock(); }
    private void PauseClock()
    { if (!IsPlaying) return; baseSeconds = CurrentTime(); IsPlaying = false; timer.Stop(); Changed?.Invoke(this, EventArgs.Empty); }
    public void SetSpeed(double speed)
    { if (!double.IsFinite(speed) || speed is < .1 or > 4) throw new ArgumentOutOfRangeException(nameof(speed)); bool playing = IsPlaying; PauseClock(); Speed = speed; if (playing) Play(); }
    public async Task SeekAsync(double seconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        resumeAfterSeek |= IsPlaying; PauseClock(); baseSeconds = Math.Clamp(seconds, 0, Duration);
        long requested = ++seekGeneration;
        await updateGate.WaitAsync(lifetime.Token);
        try { if (requested == seekGeneration) await RunFrameAsync(baseSeconds); }
        finally { updateGate.Release(); }
        if (requested != seekGeneration || disposed) return;
        bool resume = resumeAfterSeek; resumeAfterSeek = false;
        if (resume) Play(); Changed?.Invoke(this, EventArgs.Empty);
    }
    private double CurrentTime()
    {
        double value = baseSeconds + (clock.Elapsed.TotalSeconds - startedSeconds) * Speed;
        return Loop ? value % Duration : Math.Min(Duration, value);
    }
    private async void Tick(object? sender, EventArgs args)
    {
        if (disposed || !IsPlaying || frameTask is { IsCompleted: false } || !updateGate.Wait(0)) return;
        double time = CurrentTime();
        try { await RunFrameAsync(time); if (!Loop && time >= Duration) Pause(); Changed?.Invoke(this, EventArgs.Empty); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Pause(); Failed?.Invoke(this, error); }
        finally { updateGate.Release(); }
    }
    private async Task RunFrameAsync(double time)
    {
        var task = update(time, InPlace, lifetime.Token); frameTask = task;
        try { await task; } finally { if (ReferenceEquals(frameTask, task)) frameTask = null; }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; Pause(); timer.Tick -= Tick; lifetime.Cancel();
        if (frameTask is { } pending) try { await pending; } catch (OperationCanceledException) { }
        await updateGate.WaitAsync(); updateGate.Release();
        lifetime.Dispose();
    }
}
