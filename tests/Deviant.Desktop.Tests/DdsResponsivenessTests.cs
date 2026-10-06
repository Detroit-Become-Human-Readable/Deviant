using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Formats.Textures;
using Xunit;
using Xunit.Abstractions;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class DdsResponsivenessTests(ITestOutputHelper output)
{
    [Fact]
    public void LargeHdrRapidTransformsStayResponsiveAndExportTheSettledFullImage() => Run(root =>
    {
        var view = new DdsImageView();
        var window = new Window { Content = view, Width = 1000, Height = 700, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            var path = Path.Combine(root, "source", "image.dds"); File.WriteAllBytes(path, DdsBytes(2048, 2048, true));
            var opening = Stopwatch.StartNew(); Pump(view.OpenAsync(path)); opening.Stop();
            Assert.True(view.CurrentSurface!.IsHdr);
            var exposure = (Slider)view.FindName("Exposure"); var toneMap = (ComboBox)view.FindName("ToneMap"); var channels = (ComboBox)view.FindName("Channels");
            var preview = (Image)view.FindName("Preview"); var publications = 0; var stalePublications = 0;
            var descriptor = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
            EventHandler changed = (_, _) =>
            {
                if (preview.Source is null) return;
                publications++;
                var transform = view.CurrentDisplaySurface!.DisplayTransform!;
                if (transform.ExposureStops != (float)exposure.Value || transform.Operator != (TextureToneMapOperator)toneMap.SelectedItem) stalePublications++;
            };
            descriptor.AddValueChanged(preview, changed);
            try
            {
                exposure.Value = 1;
                Pump(Task.Delay(60));
                publications = stalePublications = 0;
                var burst = Stopwatch.StartNew();
                for (var index = 0; index < 40; index++) { exposure.Value = index % 9 - 4; channels.SelectedIndex = index % 6; }
                exposure.Value = 3; toneMap.SelectedItem = TextureToneMapOperator.Aces; channels.SelectedIndex = 5;
                burst.Stop(); Assert.False(((Button)view.FindName("ExportPng")).IsEnabled);
                var destination = Path.Combine(root, "output", "image.png");
                var export = view.ExportToAsync(AssetExportFormat.Png, destination);
                var settling = Stopwatch.StartNew(); var metrics = Pump(MonitorAsync(view.WaitForRenderAsync())); settling.Stop();
                var result = Pump(export);
                Assert.Equal(0, stalePublications); Assert.Equal(1, publications);
                Assert.True(metrics.HeartbeatCount >= 2); Assert.True(burst.Elapsed < TimeSpan.FromMilliseconds(500), $"Control burst took {burst.Elapsed.TotalMilliseconds:N1} ms.");
                Assert.Equal(new TextureDisplayTransform(TextureToneMapOperator.Aces, 3, true), result.DisplayTransform);
                Assert.Equal(view.CurrentDisplaySurface!.Subresource, result.SelectedSubresource);
                Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel((BitmapSource)preview.Source));
                Assert.Equal(new byte[] { 0, 0, 0, 255 }, ReadPngPixel(destination));
                output.WriteLine($"2048×2048 HDR: open {opening.Elapsed.TotalMilliseconds:N1} ms, 40-change burst {burst.Elapsed.TotalMilliseconds:N1} ms, settle {settling.Elapsed.TotalMilliseconds:N1} ms, heartbeat maximum delay {metrics.MaximumDelayMs:N1} ms, process private-byte sample peak {metrics.PeakPrivateBytes / 1048576d:N1} MiB ({metrics.HeartbeatCount} samples). Process total includes test host and prior buffers.");
            }
            finally { descriptor.RemoveValueChanged(preview, changed); }
        }
        finally { Pump(view.DisposeAsync().AsTask()); window.Close(); }
    });

    [Fact]
    public void RapidLayerMipAndChannelChangesPublishOnlyTheFinalSubresource() => Run(root =>
    {
        var view = new DdsImageView();
        try
        {
            var path = Path.Combine(root, "source", "image.dds"); File.WriteAllBytes(path, DdsBytes(1024, 1024, false, 2, 2)); Pump(view.OpenAsync(path));
            var layers = (ComboBox)view.FindName("Layers"); var mips = (ComboBox)view.FindName("Mips"); var channels = (ComboBox)view.FindName("Channels");
            for (var index = 0; index < 30; index++) { layers.SelectedIndex = index % 2; mips.SelectedIndex = index % 2; channels.SelectedIndex = index % 6; }
            layers.SelectedIndex = 1; mips.SelectedIndex = 1; channels.SelectedIndex = 2;
            var destination = Path.Combine(root, "output", "image.png"); var exporting = view.ExportToAsync(AssetExportFormat.Png, destination);
            Pump(view.WaitForRenderAsync()); var result = Pump(exporting);
            var selected = new TextureSubresourceSelection(1, null, 1);
            Assert.Equal(selected, view.CurrentSurface!.Subresource); Assert.Equal(selected, view.CurrentDisplaySurface!.Subresource); Assert.Equal(selected, result.SelectedSubresource);
            Assert.Equal(512, view.CurrentDisplaySurface.Width);
            Assert.Equal(new byte[] { 11, 11, 11, 255 }, Pixel((BitmapSource)((Image)view.FindName("Preview")).Source));
            Assert.Equal(new byte[] { 33, 22, 11, 44 }, ReadPngPixel(destination));
            Assert.True(((Button)view.FindName("ExportPng")).IsEnabled);
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void DisposalCancelsAndDrainsPendingLargeOpenAndRendering() => Run(root =>
    {
        var view = new DdsImageView();
        var path = Path.Combine(root, "source", "image.dds"); File.WriteAllBytes(path, DdsBytes(2048, 2048, true));
        var opening = view.OpenAsync(path); Pump(Task.Delay(20));
        var disposal = Stopwatch.StartNew(); Pump(view.DisposeAsync().AsTask()); disposal.Stop(); Pump(opening);
        Assert.Null(view.CurrentDocument); Assert.Null(view.CurrentSurface); Assert.Null(view.CurrentDisplaySurface); Assert.Null(((Image)view.FindName("Preview")).Source);
        Pump(Task.Delay(100)); Assert.Null(((Image)view.FindName("Preview")).Source);
        Assert.False(((Button)view.FindName("ExportPng")).IsEnabled);
        output.WriteLine($"Dispose during large open completed in {disposal.Elapsed.TotalMilliseconds:N1} ms, with no later preview publication.");
    });

    [Fact]
    public void DisposalDrainsSupersededHdrRendersWithoutPublishingAfterClose() => Run(root =>
    {
        var view = new DdsImageView();
        try
        {
            var path = Path.Combine(root, "source", "image.dds"); File.WriteAllBytes(path, DdsBytes(2048, 2048, true)); Pump(view.OpenAsync(path));
            var exposure = (Slider)view.FindName("Exposure"); exposure.Value = 1; Pump(Task.Delay(60));
            exposure.Value = -2; var pending = view.WaitForRenderAsync();
            var disposal = Stopwatch.StartNew(); Pump(view.DisposeAsync().AsTask()); disposal.Stop();
            try { Pump(pending); } catch (OperationCanceledException) { }
            Assert.Null(view.CurrentDisplaySurface); Assert.Null(((Image)view.FindName("Preview")).Source);
            Pump(Task.Delay(100)); Assert.Null(((Image)view.FindName("Preview")).Source);
            output.WriteLine($"Dispose during superseded HDR rendering completed in {disposal.Elapsed.TotalMilliseconds:N1} ms after worker cancellation and drain.");
        }
        finally { Pump(view.DisposeAsync().AsTask()); }
    });

    private static byte[] DdsBytes(int width, int height, bool hdr, int layers = 1, int mipCount = 1)
    {
        var size = 0;
        for (var mip = 0; mip < mipCount; mip++) size += hdr ? Math.Max(1, (width >> mip) / 4) * Math.Max(1, (height >> mip) / 4) * 16 : (width >> mip) * (height >> mip) * 4;
        var bytes = new byte[148 + layers * size]; "DDS "u8.CopyTo(bytes);
        Put(4, 124); Put(8, mipCount > 1 ? 0x21007u : 0x1007u); Put(12, checked((uint)height)); Put(16, checked((uint)width)); Put(28, checked((uint)mipCount));
        Put(76, 32); Put(80, 4); "DX10"u8.CopyTo(bytes.AsSpan(84)); Put(108, layers > 1 || mipCount > 1 ? 0x401008u : 0x1000u); Put(128, hdr ? 95u : 28u); Put(132, 3); Put(140, checked((uint)layers));
        if (!hdr) for (var index = 148; index < bytes.Length; index += 4) { bytes[index] = 11; bytes[index + 1] = 22; bytes[index + 2] = 33; bytes[index + 3] = 44; }
        return bytes;
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }
    private static byte[] Pixel(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0); var result = new byte[4];
        converted.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), result, 4, 0); return result;
    }
    private static byte[] ReadPngPixel(string path)
    { using var stream = File.OpenRead(path); return Pixel(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]); }
    private static async Task<(int HeartbeatCount, double MaximumDelayMs, long PeakPrivateBytes)> MonitorAsync(Task rendering)
    {
        var count = 0; var maximumDelay = 0d; var peakPrivate = 0L; using var process = Process.GetCurrentProcess();
        while (!rendering.IsCompleted)
        {
            var tick = Stopwatch.StartNew(); await Task.Delay(10); tick.Stop(); count++;
            maximumDelay = Math.Max(maximumDelay, tick.Elapsed.TotalMilliseconds); process.Refresh(); peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
        }
        await rendering; return (count, maximumDelay, peakPrivate);
    }
    private static void Run(Action<string> check)
    {
        Exception? failure = null; var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "deviant-dds-response-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(root, "source"));
            try { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); check(root); }
            catch (Exception error) { failure = error; }
            finally { Directory.Delete(root, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "DDS responsiveness check timed out.");
        if (failure is not null) throw new InvalidOperationException("DDS responsiveness check failed.", failure);
    }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
}
