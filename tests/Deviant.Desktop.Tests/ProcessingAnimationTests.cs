using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class ProcessingAnimationTests
{
    [Fact]
    public void BundledFramesKeepTheirTimingAndOnlyAnimateDuringWork() => Run(() =>
    {
        var control = new ProcessingAnimation();
        var window = Host(control);
        try
        {
            window.Show(); PumpUntil(() => control.IsLoaded);
            Assert.False(control.IsRunning); Assert.Null(control.Source);
            control.IsActive = true; PumpUntil(() => control.IsRunning && control.FrameCount > 1);
            Assert.True(control.Source!.IsFrozen); Assert.Equal(28, control.Width); Assert.Equal(28, control.Height);
            using var stream = Application.GetResourceStream(new Uri("/Deviant;component/Assets/processing.gif", UriKind.Relative))!.Stream;
            var expected = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.Equal(expected.Frames.Count, control.FrameCount);
            foreach (var index in new[] { 0, expected.Frames.Count - 1 })
            {
                var metadata = (BitmapMetadata)expected.Frames[index].Metadata;
                var delay = Convert.ToInt32(metadata.GetQuery("/grctlext/Delay"));
                Assert.Equal(TimeSpan.FromMilliseconds(delay * 10), control.FrameDuration(index));
            }
            PumpUntil(() => control.CurrentFrameIndex > 0);
            control.IsActive = false;
            Assert.False(control.IsRunning);
            Assert.Equal(Visibility.Collapsed, control.Visibility);
            var stopped = control.CurrentFrameIndex;
            PumpDelay(TimeSpan.FromMilliseconds(100));
            Assert.Equal(stopped, control.CurrentFrameIndex);
        }
        finally { control.Dispose(); window.Close(); }
    });

    [Fact]
    public void HiddenAndUnloadedControlsReleaseTheirTimerAndRestartWhenVisible() => Run(() =>
    {
        var control = new ProcessingAnimation { IsActive = true };
        var parent = new Grid(); parent.Children.Add(control);
        var window = Host(parent);
        try
        {
            window.Show(); PumpUntil(() => control.IsRunning);
            parent.Visibility = Visibility.Collapsed;
            Assert.False(control.IsRunning);
            parent.Visibility = Visibility.Visible;
            PumpUntil(() => control.IsRunning);
            window.Content = null;
            PumpUntil(() => !control.IsLoaded);
            Assert.False(control.IsRunning);
            window.Content = parent;
            PumpUntil(() => control.IsRunning);
        }
        finally { control.Dispose(); window.Close(); }
    });

    [Fact]
    public void DisposedAnimationCannotRestartOrRetainItsImage() => Run(() =>
    {
        var control = new ProcessingAnimation { IsActive = true };
        var window = Host(control);
        try
        {
            window.Show(); PumpUntil(() => control.IsRunning);
            control.Dispose();
            Assert.False(control.IsRunning); Assert.Null(control.Source); Assert.Equal(0, control.FrameCount);
            control.IsActive = false; control.IsActive = true;
            window.Content = null; window.Content = control;
            PumpDelay(TimeSpan.FromMilliseconds(100));
            Assert.False(control.IsRunning); Assert.Null(control.Source);
        }
        finally { control.Dispose(); window.Close(); }
    });

    [Fact]
    public void OversizedAnimationsStopWithoutCrashingTheWindow() => Run(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.gif");
        var image = BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[4], 4);
        var encoder = new GifBitmapEncoder();
        for (var index = 0; index < 513; index++) encoder.Frames.Add(BitmapFrame.Create(image));
        using (var output = File.Create(path)) encoder.Save(output);
        var control = new ProcessingAnimation { AnimationSource = new Uri(path), IsActive = true };
        var window = Host(control);
        try
        {
            window.Show();
            PumpUntil(() => control.IsLoaded && control.LastLoadError is not null);
            Assert.IsType<InvalidDataException>(control.LastLoadError);
            Assert.False(control.IsRunning);
            Assert.Null(control.Source);
        }
        finally { control.Dispose(); window.Close(); File.Delete(path); }
    });

    [Fact]
    public void CroppedFramesKeepCanvasSizeOffsetsAndDisposal() => Run(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.gif");
        File.WriteAllBytes(path, MakeCroppedAnimation());
        var control = new ProcessingAnimation { AnimationSource = new Uri(path), IsActive = true };
        var window = Host(control);
        try
        {
            window.Show(); PumpUntil(() => control.IsRunning);
            PumpUntil(() => control.CurrentFrameIndex == 1);
            var second = Assert.IsAssignableFrom<BitmapSource>(control.Source);
            Assert.Equal(4, second.PixelWidth); Assert.Equal(4, second.PixelHeight);
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(second, 2, 1));
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(second, 0, 0));
            PumpUntil(() => control.CurrentFrameIndex == 2);
            var third = Assert.IsAssignableFrom<BitmapSource>(control.Source);
            Assert.Equal(4, third.PixelWidth); Assert.Equal(4, third.PixelHeight);
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(third, 2, 1));
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(third, 1, 2));
            PumpUntil(() => control.CurrentFrameIndex == 3);
            var fourth = Assert.IsAssignableFrom<BitmapSource>(control.Source);
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(fourth, 1, 2));
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(fourth, 3, 3));
        }
        finally { control.Dispose(); window.Close(); File.Delete(path); }
    });

    private static byte[] Pixel(BitmapSource frame, int x, int y)
    {
        var pixel = new byte[4];
        frame.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static byte[] MakeCroppedAnimation()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("GIF89a"u8); writer.Write((ushort)4); writer.Write((ushort)4);
        writer.Write(new byte[] { 0x80, 0, 0, 255, 0, 0, 0, 0, 255 });
        WriteFrame(0, 0, 4, 4, 0, 1);
        WriteFrame(2, 1, 1, 1, 1, 2);
        WriteFrame(1, 2, 1, 1, 1, 3);
        WriteFrame(3, 3, 1, 1, 1, 1);
        writer.Write((byte)0x3B);
        return stream.ToArray();

        void WriteFrame(ushort left, ushort top, ushort width, ushort height, byte paletteIndex, byte disposal)
        {
            writer.Write(new byte[] { 0x21, 0xF9, 4, (byte)(disposal << 2), 20, 0, 0, 0, 0x2C });
            writer.Write(left); writer.Write(top); writer.Write(width); writer.Write(height); writer.Write((byte)0);
            var codes = Enumerable.Range(0, width * height).SelectMany(_ => new[] { 4, (int)paletteIndex }).Append(5).ToArray();
            var encoded = new byte[(codes.Length * 3 + 7) / 8];
            for (var index = 0; index < codes.Length; index++)
                for (var bit = 0; bit < 3; bit++)
                    if ((codes[index] & (1 << bit)) != 0) encoded[(index * 3 + bit) / 8] |= (byte)(1 << ((index * 3 + bit) % 8));
            writer.Write((byte)2); writer.Write(checked((byte)encoded.Length)); writer.Write(encoded); writer.Write((byte)0);
        }
    }

    private static Window Host(UIElement content) => new()
    { Content = content, Width = 80, Height = 80, Left = -10000, Top = -10000, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
    private static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Processing animation test timed out.");
        if (failure is not null) throw new InvalidOperationException("Processing animation test failed.", failure);
    }
    private static void PumpUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) { timeout.Token.ThrowIfCancellationRequested(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(5); }
    }
    private static void PumpDelay(TimeSpan delay)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        Task.Delay(delay).ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
    }
}
