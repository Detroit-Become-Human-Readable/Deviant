using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Deviant.Desktop;

public sealed class ProcessingAnimation : Image, IDisposable
{
    private const int MaximumFrames = 512;
    private const long MaximumPixels = 32L * 1024 * 1024;
    private static readonly Uri DefaultSource = new($"/{typeof(ProcessingAnimation).Assembly.GetName().Name};component/Assets/processing.gif", UriKind.Relative);
    private static readonly ConcurrentDictionary<string, AnimationFrames> CachedResources = new(StringComparer.Ordinal);
    private readonly DispatcherTimer timer;
    private readonly Stopwatch clock = new();
    private AnimationFrames? animation;
    private bool disposed;

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool),
        typeof(ProcessingAnimation), new PropertyMetadata(false, ActiveChanged));
    public static readonly DependencyProperty AnimationSourceProperty = DependencyProperty.Register(nameof(AnimationSource), typeof(Uri),
        typeof(ProcessingAnimation), new PropertyMetadata(DefaultSource, SourceChanged));

    public ProcessingAnimation()
    {
        Width = Height = 28; Stretch = Stretch.Uniform;
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher);
        timer.Tick += FrameTick;
        Loaded += ControlLoaded;
        Unloaded += ControlUnloaded;
        IsVisibleChanged += VisibleChanged;
    }

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public Uri AnimationSource { get => (Uri)GetValue(AnimationSourceProperty); set => SetValue(AnimationSourceProperty, value); }
    public bool IsRunning => timer.IsEnabled;
    public int CurrentFrameIndex { get; private set; }
    public int FrameCount => animation?.Frames.Length ?? 0;
    public Exception? LastLoadError { get; private set; }
    public TimeSpan FrameDuration(int index) => animation?.Durations[index] ?? throw new InvalidOperationException("The animation is not loaded.");

    private static void ActiveChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var control = (ProcessingAnimation)owner;
        control.SetCurrentValue(VisibilityProperty, (bool)args.NewValue ? Visibility.Visible : Visibility.Collapsed);
        control.UpdatePlayback();
    }
    private static void SourceChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var control = (ProcessingAnimation)owner;
        control.Stop(); control.animation = null; control.LastLoadError = null; control.Source = null;
        control.UpdatePlayback();
    }
    private void ControlLoaded(object sender, RoutedEventArgs args) => UpdatePlayback();
    private void ControlUnloaded(object sender, RoutedEventArgs args) => Stop();
    private void VisibleChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdatePlayback();

    private void UpdatePlayback()
    {
        if (disposed || !IsActive || !IsLoaded || !IsVisible) { Stop(); return; }
        if (timer.IsEnabled) return;
        try
        {
            animation ??= LoadFrames(AnimationSource);
            CurrentFrameIndex = 0; Source = animation.Frames[0];
            clock.Restart();
            if (animation.Frames.Length > 1) { timer.Interval = animation.Durations[0]; timer.Start(); }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or NotSupportedException or ArgumentException or OverflowException)
        { LastLoadError = error; Stop(); Source = null; }
    }

    private void FrameTick(object? sender, EventArgs args)
    {
        if (disposed || !IsActive || !IsLoaded || !IsVisible || animation is null) { Stop(); return; }
        var elapsed = clock.Elapsed.Ticks % animation.TotalTicks;
        var index = Array.BinarySearch(animation.FrameEnds, elapsed + 1);
        if (index < 0) index = ~index;
        index = Math.Min(index, animation.Frames.Length - 1);
        if (index != CurrentFrameIndex) { CurrentFrameIndex = index; Source = animation.Frames[index]; }
        timer.Interval = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, animation.FrameEnds[index] - elapsed));
    }

    private void Stop()
    {
        timer.Stop(); clock.Reset();
    }

    private static AnimationFrames LoadFrames(Uri source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var cacheable = !source.IsAbsoluteUri || source.Scheme == "pack";
        if (cacheable && CachedResources.TryGetValue(source.OriginalString, out var cached)) return cached;
        using var stream = source.IsAbsoluteUri && source.IsFile ? File.OpenRead(source.LocalPath) :
            Application.GetResourceStream(source)?.Stream ?? throw new IOException("The processing animation is unavailable.");
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count is < 1 or > MaximumFrames) throw new InvalidDataException("The animation has too many frames.");
        var metadata = decoder.Metadata as BitmapMetadata;
        var width = ReadMetadata(metadata, "/logscrdesc/Width", decoder.Frames[0].PixelWidth);
        var height = ReadMetadata(metadata, "/logscrdesc/Height", decoder.Frames[0].PixelHeight);
        if (width <= 0 || height <= 0 || checked((long)width * height * decoder.Frames.Count) > MaximumPixels)
            throw new InvalidDataException("The animation exceeds its image limit.");
        var stride = checked(width * 4);
        var canvas = new byte[checked(stride * height)];
        var background = ReadBackground(decoder, metadata);
        FillRectangle(canvas, stride, new Int32Rect(0, 0, width, height), background,
            ReadMetadata(decoder.Frames[0].Metadata as BitmapMetadata, "/grctlext/TransparencyFlag", 0) != 0);
        var frames = new BitmapSource[decoder.Frames.Count]; var durations = new TimeSpan[frames.Length]; var ends = new long[frames.Length];
        long totalTicks = 0;
        for (int index = 0; index < frames.Length; index++)
        {
            var frame = decoder.Frames[index];
            var frameMetadata = frame.Metadata as BitmapMetadata;
            var left = ReadMetadata(frameMetadata, "/imgdesc/Left", 0);
            var top = ReadMetadata(frameMetadata, "/imgdesc/Top", 0);
            if (left < 0 || top < 0 || frame.PixelWidth > width - left || frame.PixelHeight > height - top)
                throw new InvalidDataException("An animation frame exceeds its canvas.");
            var rectangle = new Int32Rect(left, top, frame.PixelWidth, frame.PixelHeight);
            var disposal = ReadMetadata(frameMetadata, "/grctlext/Disposal", 0);
            var previous = disposal == 3 ? canvas.ToArray() : null;
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var frameStride = checked(frame.PixelWidth * 4);
            var framePixels = new byte[checked(frameStride * frame.PixelHeight)];
            converted.CopyPixels(framePixels, frameStride, 0);
            for (var y = 0; y < frame.PixelHeight; y++)
                for (var x = 0; x < frame.PixelWidth; x++)
                {
                    var input = y * frameStride + x * 4;
                    if (framePixels[input + 3] == 0) continue;
                    var output = (top + y) * stride + (left + x) * 4;
                    framePixels.AsSpan(input, 4).CopyTo(canvas.AsSpan(output, 4));
                }
            var delay = (frame.Metadata as BitmapMetadata)?.GetQuery("/grctlext/Delay");
            int hundredths = delay is null ? 10 : Convert.ToInt32(delay, System.Globalization.CultureInfo.InvariantCulture);
            durations[index] = TimeSpan.FromMilliseconds(hundredths > 0 ? Math.Clamp(hundredths * 10L, 10, 60000) : 100);
            totalTicks = checked(totalTicks + durations[index].Ticks); ends[index] = totalTicks;
            var composed = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, canvas, stride);
            composed.Freeze(); frames[index] = composed;
            if (disposal == 2)
                FillRectangle(canvas, stride, rectangle, background, ReadMetadata(frameMetadata, "/grctlext/TransparencyFlag", 0) != 0);
            else if (previous is not null) canvas = previous;
        }
        var decoded = new AnimationFrames(frames, durations, ends, totalTicks);
        return cacheable ? CachedResources.GetOrAdd(source.OriginalString, decoded) : decoded;
    }

    private static int ReadMetadata(BitmapMetadata? metadata, string query, int fallback)
    {
        if (metadata is null || !metadata.ContainsQuery(query)) return fallback;
        var value = metadata.GetQuery(query);
        return value is null ? fallback : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Color ReadBackground(GifBitmapDecoder decoder, BitmapMetadata? metadata)
    {
        var colors = decoder.Palette?.Colors;
        var index = ReadMetadata(metadata, "/logscrdesc/BackgroundColorIndex", -1);
        return colors is not null && index >= 0 && index < colors.Count ? colors[index] : Colors.Transparent;
    }

    private static void FillRectangle(byte[] pixels, int stride, Int32Rect rectangle, Color color, bool transparent)
    {
        for (var y = rectangle.Y; y < rectangle.Y + rectangle.Height; y++)
            for (var x = rectangle.X; x < rectangle.X + rectangle.Width; x++)
            {
                var offset = y * stride + x * 4;
                pixels[offset] = transparent ? (byte)0 : color.B;
                pixels[offset + 1] = transparent ? (byte)0 : color.G;
                pixels[offset + 2] = transparent ? (byte)0 : color.R;
                pixels[offset + 3] = transparent ? (byte)0 : color.A;
            }
    }

    public void Dispose()
    {
        Dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true; Stop();
        timer.Tick -= FrameTick; Loaded -= ControlLoaded; Unloaded -= ControlUnloaded; IsVisibleChanged -= VisibleChanged;
        Source = null; animation = null;
    }
    private sealed record AnimationFrames(BitmapSource[] Frames, TimeSpan[] Durations, long[] FrameEnds, long TotalTicks);
}
