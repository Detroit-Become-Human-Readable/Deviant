using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Deviant.Desktop;

public partial class AnimationPlaybackView : UserControl, IAsyncDisposable
{
    private AnimationPlaybackController? controller;
    private bool refreshing;
    private long generation;
    public AnimationPlaybackView() { InitializeComponent(); IsVisibleChanged += VisibilityChanged; }
    public event EventHandler<Exception>? PlaybackFailed;
    public async Task SetClipAsync(double duration, Func<double, bool, CancellationToken, Task> update)
    {
        long request = ++generation;
        await ClearCoreAsync();
        if (request != generation) return;
        var candidate = new AnimationPlaybackController(Dispatcher, duration, update); controller = candidate;
        candidate.Changed += PlaybackChanged; candidate.Failed += Failed;
        candidate.Loop = LoopToggle.IsChecked == true; candidate.InPlace = InPlaceToggle.IsChecked == true;
        if (SpeedSelect.SelectedItem is ComboBoxItem { Tag: string speed })
            candidate.SetSpeed(double.Parse(speed, CultureInfo.InvariantCulture));
        PositionSlider.Maximum = duration; PositionSlider.IsEnabled = PlayButton.IsEnabled = true; Refresh();
        try { await candidate.SeekAsync(0); }
        catch
        {
            if (ReferenceEquals(controller, candidate)) { ++generation; await ClearCoreAsync(); }
            throw;
        }
    }
    private void PlayClicked(object sender, RoutedEventArgs args)
    { if (controller is null) return; if (controller.IsPlaying) controller.Pause(); else controller.Play(); }
    private void PlaybackChanged(object? sender, EventArgs args) => Refresh();
    private void Failed(object? sender, Exception error) => PlaybackFailed?.Invoke(this, error);
    private void Refresh()
    {
        if (controller is null) return; refreshing = true;
        try
        {
            PositionSlider.Value = controller.Position; PlayButton.Content = controller.IsPlaying ? "Pause" : "Play";
            TimeText.Text = $"{TimeSpan.FromSeconds(controller.Position):m\\:ss} / {TimeSpan.FromSeconds(controller.Duration):m\\:ss}";
        }
        finally { refreshing = false; }
    }
    private async void PositionChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (refreshing || controller is null) return;
        var current = controller; long selected = generation;
        try { await current.SeekAsync(args.NewValue); if (selected == generation) Refresh(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (selected == generation) PlaybackFailed?.Invoke(this, error); }
    }
    private async void OptionsChanged(object sender, RoutedEventArgs args)
    {
        if (controller is null) return; controller.Loop = LoopToggle.IsChecked == true; controller.InPlace = InPlaceToggle.IsChecked == true;
        try { await controller.SeekAsync(controller.Position); } catch (OperationCanceledException) { }
        catch (Exception error) { PlaybackFailed?.Invoke(this, error); }
    }
    private void SpeedChanged(object sender, SelectionChangedEventArgs args)
    { if (controller is not null && SpeedSelect.SelectedItem is ComboBoxItem { Tag: string value }) controller.SetSpeed(double.Parse(value, CultureInfo.InvariantCulture)); }
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    { if (!IsVisible) controller?.Pause(); }
    public async Task ClearAsync()
    {
        ++generation; await ClearCoreAsync();
    }
    private async Task ClearCoreAsync()
    {
        var previous = controller; controller = null;
        if (previous is not null) { previous.Changed -= PlaybackChanged; previous.Failed -= Failed; await previous.DisposeAsync(); }
        if (controller is not null) return;
        refreshing = true;
        try { PositionSlider.IsEnabled = PlayButton.IsEnabled = false; PositionSlider.Value = 0; PlayButton.Content = "Play"; TimeText.Text = "0:00 / 0:00"; }
        finally { refreshing = false; }
    }
    public async ValueTask DisposeAsync() { IsVisibleChanged -= VisibilityChanged; await ClearAsync(); }
}
