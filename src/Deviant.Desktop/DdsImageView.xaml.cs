using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Export.Textures;
using Deviant.Formats.Textures;
using Microsoft.Win32;

namespace Deviant.Desktop;

public partial class DdsImageView : UserControl, IAsyncDisposable
{
    private DdsTextureDocument? document;
    private TextureSurface? surface, displaySurface;
    private string? sourcePath;
    private FileStamp? sourceStamp;
    private readonly FileRevisionProvider revisions = new();
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource sourceCancellation = new();
    private readonly DdsTextureDecoder decoder = new();
    private readonly SemaphoreSlim decodeGate = new(1, 1), renderGate = new(1, 1);
    private readonly HashSet<Task> operations = [];
    private CancellationTokenSource? openCancellation, selectionCancellation, renderCancellation;
    private Task openTask = Task.CompletedTask, selectionTask = Task.CompletedTask, renderTask = Task.CompletedTask;
    private long generation, selectionGeneration, renderGeneration;
    private bool updating, documentBacked;

    public DdsImageView()
    {
        InitializeComponent(); updating = true;
        try
        {
            Channels.ItemsSource = new[] { "RGBA", "RGB", "Red", "Green", "Blue", "Alpha" }; Channels.SelectedIndex = 0;
            ToneMap.ItemsSource = Enum.GetValues<TextureToneMapOperator>(); ToneMap.SelectedItem = TextureToneMapOperator.Reinhard;
        }
        finally { updating = false; }
    }
    public event EventHandler? SurfaceChanged;
    public DdsTextureDocument? CurrentDocument => document;
    public TextureSurface? CurrentSurface => surface;
    public TextureSurface? CurrentDisplaySurface => displaySurface;
    public bool IsDocumentBacked => documentBacked;

    public Task OpenAsync(string path, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(lifetime.IsCancellationRequested, this);
        openTask = Track(OpenCoreAsync(path, token)); return openTask;
    }
    private async Task OpenCoreAsync(string path, CancellationToken token)
    {
        var version = BeginSource(false);
        var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); openCancellation = own;
        Status.Text = "Opening DDS…";
        try
        {
            var opened = await Task.Run(async () =>
            {
                var full = Path.GetFullPath(path); using var handle = SourceIdentity.OpenPinned(full); var stamp = revisions.Capture(handle, full);
                if (stamp.Length > DdsTextureReader.MaximumInputBytes || stamp.Length > int.MaxValue) throw new IOException("The image exceeds the supported size.");
                var bytes = new byte[checked((int)stamp.Length)]; var read = 0;
                while (read < bytes.Length)
                {
                    var count = await RandomAccess.ReadAsync(handle, bytes.AsMemory(read), read, own.Token);
                    if (count == 0) throw new EndOfStreamException(); read += count;
                }
                SourceIdentity.EnsureUnchanged(revisions, handle, full, stamp); own.Token.ThrowIfCancellationRequested();
                return (Document: new DdsTextureReader().Read(bytes), Path: full, Stamp: stamp);
            }, own.Token);
            own.Token.ThrowIfCancellationRequested(); if (version != generation) return;
            sourcePath = opened.Path; sourceStamp = opened.Stamp; BindDocument(opened.Document, Path.GetFileName(path));
            ExportDds.IsEnabled = true; await RequestDecodeAsync(0, own.Token); own.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { if (version == generation) Status.Text = "Cancelled"; }
        catch (Exception error) { if (version == generation) Status.Text = error.Message; }
        finally { if (ReferenceEquals(openCancellation, own)) openCancellation = null; own.Dispose(); }
    }

    public Task LoadDocumentAsync(DdsTextureDocument value, string label, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A preview label is required.", nameof(label));
        if ((value.Identity is null) != (value.Revision is null) || value.Identity is { } identity && value.Revision is { } revision &&
            (identity.IndexContentId != revision.IndexContentId || identity.Ordinal < 0 || string.IsNullOrWhiteSpace(identity.IndexContentId) || string.IsNullOrWhiteSpace(revision.PhysicalSourceStamp)))
            throw new ArgumentException("The document identity and revision do not match.", nameof(value));
        ObjectDisposedException.ThrowIf(lifetime.IsCancellationRequested, this);
        openTask = Track(LoadDocumentCoreAsync(value, label, token)); return openTask;
    }
    private async Task LoadDocumentCoreAsync(DdsTextureDocument value, string label, CancellationToken token)
    {
        var version = BeginSource(true); var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); openCancellation = own;
        Status.Text = "Opening image\u2026";
        try
        {
            own.Token.ThrowIfCancellationRequested(); BindDocument(value, label);
            await RequestDecodeAsync(0, own.Token); own.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { if (version == generation) Status.Text = "Cancelled"; }
        catch (Exception error) { if (version == generation) Status.Text = error.Message; }
        finally { if (ReferenceEquals(openCancellation, own)) openCancellation = null; own.Dispose(); }
    }
    private long BeginSource(bool hosted)
    {
        var version = ++generation; ++selectionGeneration; ++renderGeneration;
        sourceCancellation.Cancel(); sourceCancellation.Dispose(); sourceCancellation = new();
        openCancellation?.Cancel(); selectionCancellation?.Cancel(); renderCancellation?.Cancel(); documentBacked = hosted;
        document = null; surface = displaySurface = null; sourcePath = null; sourceStamp = null;
        Preview.Source = null; ImageName.Text = Metadata.Text = ""; ExportDds.IsEnabled = ExportPng.IsEnabled = false; HdrControls.Visibility = Visibility.Collapsed;
        var actions = hosted ? Visibility.Collapsed : Visibility.Visible;
        OpenDds.Visibility = ExportDds.Visibility = ExportPng.Visibility = actions; OpenDds.IsEnabled = !hosted;
        SurfaceChanged?.Invoke(this, EventArgs.Empty); return version;
    }
    private void BindDocument(DdsTextureDocument value, string label)
    {
        document = value; ImageName.Text = label;
        Metadata.Text = $"{value.Info.Width:N0} \u00D7 {value.Info.Height:N0} \u00B7 {value.Info.Format} \u00B7 {value.Info.MipCount} mips";
        updating = true;
        try
        {
            Mips.ItemsSource = Enumerable.Range(0, value.Info.MipCount).Select(index => $"Mip {index}").ToArray(); Mips.SelectedIndex = 0;
            Layers.ItemsSource = Enumerable.Range(0, value.Info.ArrayLayerCount).ToArray(); Layers.SelectedIndex = 0; Layers.Visibility = value.Info.ArrayLayerCount > 1 ? Visibility.Visible : Visibility.Collapsed;
            Faces.ItemsSource = value.Info.CubeFaces; Faces.SelectedIndex = 0; Faces.Visibility = value.Info.IsCubeMap ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { updating = false; }
    }

    private Task RequestDecodeAsync(int mip, CancellationToken token = default)
    { selectionTask = Track(DecodeAsync(mip, token)); return selectionTask; }
    private async Task DecodeAsync(int mip, CancellationToken token)
    {
        if (document is not { } current || lifetime.IsCancellationRequested) return;
        var version = generation; var selectionVersion = ++selectionGeneration; ++renderGeneration;
        selectionCancellation?.Cancel(); renderCancellation?.Cancel();
        var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); selectionCancellation = own;
        var selected = new TextureSubresourceSelection(Layers.SelectedItem is int layer ? layer : 0, Faces.SelectedItem is CubeFace face ? face : null, mip);
        surface = displaySurface = null; Preview.Source = null; ExportPng.IsEnabled = false; Status.Text = "Decoding mip…"; SurfaceChanged?.Invoke(this, EventArgs.Empty); var entered = false;
        try
        {
            await decodeGate.WaitAsync(own.Token); entered = true;
            var decoded = await decoder.DecodeSubresourceAsync(current, selected, cancellationToken: own.Token);
            own.Token.ThrowIfCancellationRequested();
            if (version != generation || selectionVersion != selectionGeneration || !ReferenceEquals(current, document)) return;
            surface = decoded; HdrControls.Visibility = decoded.IsHdr ? Visibility.Visible : Visibility.Collapsed;
            await ScheduleRender(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (version == generation && selectionVersion == selectionGeneration) Status.Text = error.Message; }
        finally { if (entered) decodeGate.Release(); if (ReferenceEquals(selectionCancellation, own)) selectionCancellation = null; own.Dispose(); }
    }

    private Task ScheduleRender(bool coalesce = true)
    {
        if (updating || surface is not { } current || lifetime.IsCancellationRequested) return Task.CompletedTask;
        renderCancellation?.Cancel(); var own = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, selectionCancellation?.Token ?? lifetime.Token); renderCancellation = own;
        var version = ++renderGeneration; var selectionVersion = selectionGeneration;
        var exposure = (float)Exposure.Value; var toneMap = (TextureToneMapOperator)(ToneMap.SelectedItem ?? TextureToneMapOperator.Reinhard); var channel = Channels.SelectedIndex;
        displaySurface = null; ExportPng.IsEnabled = false; Status.Text = "Rendering…"; SurfaceChanged?.Invoke(this, EventArgs.Empty);
        renderTask = Track(RenderAsync(current, exposure, toneMap, channel, version, selectionVersion, coalesce, own)); return renderTask;
    }
    private async Task RenderAsync(TextureSurface current, float exposure, TextureToneMapOperator toneMap, int channel,
        long version, long selectionVersion, bool coalesce, CancellationTokenSource own)
    {
        var entered = false;
        try
        {
            if (coalesce) await Task.Delay(40, own.Token);
            await renderGate.WaitAsync(own.Token); entered = true;
            var rendered = await Task.Run(() =>
            {
                own.Token.ThrowIfCancellationRequested();
                var display = current.IsHdr ? HdrDisplayTransform.ToSdr(current, exposure, toneMap, cancellationToken: own.Token) : current;
                var input = display.Rgba8.Span; var bytes = new byte[input.Length];
                for (var offset = 0; offset < input.Length; offset += 4)
                {
                    if ((offset & 0x3FFF) == 0) own.Token.ThrowIfCancellationRequested();
                    byte red = input[offset], green = input[offset + 1], blue = input[offset + 2], alpha = input[offset + 3];
                    if (channel == 1) alpha = 255;
                    else if (channel >= 2) { red = green = blue = input[offset + Math.Clamp(channel - 2, 0, 3)]; alpha = 255; }
                    bytes[offset] = blue; bytes[offset + 1] = green; bytes[offset + 2] = red; bytes[offset + 3] = alpha;
                }
                own.Token.ThrowIfCancellationRequested();
                var bitmap = BitmapSource.Create(display.Width, display.Height, 96, 96, PixelFormats.Bgra32, null, bytes, checked(display.Width * 4)); bitmap.Freeze();
                own.Token.ThrowIfCancellationRequested(); return (Display: display, Bitmap: bitmap);
            }, own.Token);
            own.Token.ThrowIfCancellationRequested();
            if (version != renderGeneration || selectionVersion != selectionGeneration || !ReferenceEquals(current, surface)) return;
            displaySurface = rendered.Display; Preview.Source = rendered.Bitmap; ExportPng.IsEnabled = !documentBacked; SurfaceChanged?.Invoke(this, EventArgs.Empty);
            Status.Text = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (version == renderGeneration && selectionVersion == selectionGeneration) Status.Text = error.Message; }
        finally { if (entered) renderGate.Release(); if (ReferenceEquals(renderCancellation, own)) renderCancellation = null; own.Dispose(); }
    }

    public async Task WaitForRenderAsync(CancellationToken token = default)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested(); lifetime.Token.ThrowIfCancellationRequested();
            var opening = openTask; await opening.WaitAsync(token);
            var selecting = selectionTask; await selecting.WaitAsync(token);
            var rendering = renderTask; await rendering.WaitAsync(token);
            if (ReferenceEquals(opening, openTask) && ReferenceEquals(selecting, selectionTask) && ReferenceEquals(rendering, renderTask)) return;
        }
    }
    private Task Track(Task task) { operations.Add(task); ImageProcessing.IsActive = operations.Any(operation => !operation.IsCompleted); _ = ObserveAsync(task); return task; }
    private async Task ObserveAsync(Task task) { try { await task; } catch (Exception) { } finally { operations.Remove(task); ImageProcessing.IsActive = operations.Any(operation => !operation.IsCompleted); } }

    private async void Open_Click(object sender, RoutedEventArgs e) { if (documentBacked) return; var dialog = new OpenFileDialog { Title = "Open DDS", Filter = "DDS images (*.dds)|*.dds" }; if (dialog.ShowDialog(Window.GetWindow(this)) == true) await OpenAsync(dialog.FileName); }
    private async void MipChanged(object sender, SelectionChangedEventArgs e) { if (!updating && Mips.SelectedIndex >= 0) await RequestDecodeAsync(Mips.SelectedIndex); }
    private async void SubresourceChanged(object sender, SelectionChangedEventArgs e) { if (!updating && Mips.SelectedIndex >= 0) await RequestDecodeAsync(Mips.SelectedIndex); }
    private async void ChannelChanged(object sender, SelectionChangedEventArgs e) { if (IsInitialized && !updating) await ScheduleRender(); }
    private async void HdrChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!updating && surface is { IsHdr: true }) await ScheduleRender(); }
    private async void ToneMapChanged(object sender, SelectionChangedEventArgs e) { if (!updating && surface is { IsHdr: true }) await ScheduleRender(); }
    private void ZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (ImageScale is not null) ImageScale.ScaleX = ImageScale.ScaleY = e.NewValue; }
    private async void ExportDds_Click(object sender, RoutedEventArgs e) => await ExportAsync(AssetExportFormat.Dds);
    private async void ExportPng_Click(object sender, RoutedEventArgs e) => await ExportAsync(AssetExportFormat.Png);
    private async Task ExportAsync(AssetExportFormat format)
    {
        if (document is null || sourcePath is null || sourceStamp is null) return;
        var version = generation; var selectionVersion = selectionGeneration; var renderVersion = renderGeneration;
        var dialog = new SaveFileDialog { Title = "Export image", Filter = format == AssetExportFormat.Png ? "PNG image (*.png)|*.png" : "DDS image (*.dds)|*.dds", FileName = Path.GetFileNameWithoutExtension(sourcePath) + (format == AssetExportFormat.Png ? ".png" : ".dds"), OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { await ExportToAsync(format, dialog.FileName, true); if (version == generation && selectionVersion == selectionGeneration && renderVersion == renderGeneration) Status.Text = "Exported"; }
        catch (OperationCanceledException) { if (version == generation && selectionVersion == selectionGeneration && renderVersion == renderGeneration) Status.Text = "Export cancelled"; }
        catch (Exception error) { if (version == generation && selectionVersion == selectionGeneration && renderVersion == renderGeneration) Status.Text = error.Message; }
    }
    public Task<TextureExportResult> ExportToAsync(AssetExportFormat format, string destination, bool overwrite = false, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(lifetime.IsCancellationRequested, this);
        if (documentBacked) throw new InvalidOperationException("The host owns document exports.");
        var task = ExportCoreAsync(format, destination, overwrite, token); Track(task); return task;
    }
    private async Task<TextureExportResult> ExportCoreAsync(AssetExportFormat format, string destination, bool overwrite, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, sourceCancellation.Token);
        if (format == AssetExportFormat.Png) await WaitForRenderAsync(linked.Token);
        else await openTask.WaitAsync(linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (documentBacked) throw new InvalidOperationException("The host owns document exports.");
        var current = document ?? throw new InvalidOperationException("Open an image first."); var source = sourcePath ?? throw new InvalidOperationException("Image source unavailable."); var stamp = sourceStamp ?? throw new InvalidOperationException("Image source unavailable.");
        var selectedSurface = displaySurface;
        if (format == AssetExportFormat.Png && selectedSurface is null) throw new InvalidOperationException("The selected image has no rendered pixels.");
        return await Task.Run(async () =>
        {
            using var pin = SourceIdentity.OpenPinned(source); SourceIdentity.EnsureUnchanged(revisions, pin, source, stamp);
            var sourceHash = await SourceIdentity.HashAsync(pin, stamp.Length, linked.Token);
            if (!string.Equals(sourceHash, current.ContentSha256, StringComparison.Ordinal)) throw new ArchiveException("SourceChanged", "The image changed. Reopen it before exporting.");
            return await new TextureExportService().ExportAsync(current, selectedSurface, format, Path.GetDirectoryName(source)!, Path.GetDirectoryName(Path.GetFullPath(destination))!, Path.GetFileName(destination), overwrite, linked.Token,
                preCommitValidation: async cancellation =>
                {
                    SourceIdentity.EnsureUnchanged(revisions, pin, source, stamp);
                    if (await SourceIdentity.HashAsync(pin, stamp.Length, cancellation) != current.ContentSha256) throw new ArchiveException("SourceChanged", "The image changed during export.");
                });
        }, linked.Token);
    }
    public async Task ClearAsync()
    {
        var version = ++generation; ++selectionGeneration; ++renderGeneration;
        sourceCancellation.Cancel(); openCancellation?.Cancel(); selectionCancellation?.Cancel(); renderCancellation?.Cancel();
        updating = true;
        try
        {
            document = null; surface = displaySurface = null; sourcePath = null; sourceStamp = null; Preview.Source = null;
            ImageName.Text = Metadata.Text = Status.Text = ""; Mips.ItemsSource = Layers.ItemsSource = Faces.ItemsSource = null;
            HdrControls.Visibility = Layers.Visibility = Faces.Visibility = Visibility.Collapsed; ExportDds.IsEnabled = ExportPng.IsEnabled = false;
        }
        finally { updating = false; }
        SurfaceChanged?.Invoke(this, EventArgs.Empty);
        await Task.WhenAll(operations.Select(async task => { try { await task; } catch (Exception) { } }).ToArray());
        if (version == generation) openTask = selectionTask = renderTask = Task.CompletedTask;
    }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); await ClearAsync(); ImageProcessing.Dispose(); }
}
