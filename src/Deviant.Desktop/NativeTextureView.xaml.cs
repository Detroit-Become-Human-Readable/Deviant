using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Export.Textures;
using Deviant.Export;
using Deviant.Formats.Textures;
using Microsoft.Win32;

namespace Deviant.Desktop;

public sealed record NativeTexturePngExportResult(TextureExportResult Png,
    string? ReportRelativePath, string? ReportError);

public partial class NativeTextureView : UserControl, IAsyncDisposable
{
    private readonly DdsImageView images = new();
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource selection = new();
    private NativeTextureSession? session;
    private NativeTextureSource? source;
    private IDisposable? sourcePin;
    private long generation;
    private Task export = Task.CompletedTask;
    public Func<NativeTextureSource, string?>? ExportStemProvider { get; set; }
    public Func<NativeTextureSource, TextureDiscoveryEntry?>? NamingEntryProvider { get; set; }
    public Func<NativeTextureSource, CancellationToken, Task>? ValidateNamingAsync { get; set; }
    public NativeTextureView()
    {
        InitializeComponent(); ImageHost.Content = images;
        ((FrameworkElement)images.FindName("ImageName")).Visibility = Visibility.Collapsed;
        images.ActivityChanged += Images_ActivityChanged;
        images.SurfaceChanged += (_, _) => ExportPng.IsEnabled = source is not null &&
            images.CurrentDisplaySurface is not null && export.IsCompleted;
    }

    private void Images_ActivityChanged(object? sender, EventArgs e) => UpdateImageActivity();
    public DdsImageView ImageBrowser => images;
    public NativeTextureSource? CurrentSource => source;

    public Task LoadAsync(NativeTextureSession owner, NativeTextureSource native,
        DdsTextureDocument document, string label, CancellationToken token = default, bool fullResolution = false)
        => TrackActivity("Rendering image", 10, LoadCoreAsync(owner, native, document, label, token, fullResolution));

    private async Task LoadCoreAsync(NativeTextureSession owner, NativeTextureSource native,
        DdsTextureDocument document, string label, CancellationToken token, bool fullResolution)
    {
        if (document.Identity != native.Metadata.Identity || document.Revision != native.Revision || owner.Revision != native.Revision)
            throw new ArgumentException("The image document does not match the native texture source.", nameof(document));
        if (native.Metadata.RawFixedPrefix84.Length != 84 || document.Info.IsSrgb != NativeTextureDecoder.IsSrgb(native))
            throw new ArgumentException("The image color space does not match the native texture source.", nameof(document));
        var version = ++generation;
        selection.Cancel(); selection.Dispose(); selection = new();
        var previousPin = sourcePin; sourcePin = null;
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, selection.Token);
        source = null; session = null; ExportPng.IsEnabled = FullResolution.IsEnabled = false; FullResolution.Visibility = Visibility.Collapsed; Status.Text = "";
        IDisposable? pendingPin = null;
        try
        {
        pendingPin = native.CompanionLease?.AcquireLease();
        previousPin?.Dispose(); previousPin = null;
        var previewMip = fullResolution ? 0 : SelectPreviewMip(document);
        await images.LoadDocumentAsync(document, label, own.Token, previewMip);
        own.Token.ThrowIfCancellationRequested();
        if (version != generation) return;
        if (images.CurrentDocument != document || images.CurrentDisplaySurface is null)
            throw new TextureFormatException(images.CurrentFailure?.Code ?? "TexturePixelDecodeFailed",
                images.CurrentFailure?.Message ?? "The texture pixels could not be decoded.");
        var channels = (ComboBox)images.FindName("Channels");
        if (channels.SelectedIndex == 0 && images.CurrentSurface is { } current)
        {
            var zeroAlpha = await Task.Run(() => HasOnlyZeroAlpha(current, own.Token), own.Token);
            own.Token.ThrowIfCancellationRequested();
            if (version != generation || images.CurrentDocument != document || images.CurrentSurface != current) return;
            if (zeroAlpha && channels.SelectedIndex == 0)
            { channels.SelectedIndex = 1; await images.WaitForRenderAsync(own.Token); own.Token.ThrowIfCancellationRequested(); }
        }
        if (version != generation) return;
        ((TextBlock)images.FindName("Metadata")).Text = $"{document.Info.Width:N0} × {document.Info.Height:N0} · {document.Info.Format} · {document.Info.MipCount}/{native.Metadata.MipSizeCount} mips";
        source = native; session = owner; sourcePin = pendingPin; pendingPin = null; ExportPng.IsEnabled = true;
        FullResolution.Visibility = native.Metadata.Mip0Width > document.Info.Width || native.Metadata.Mip0Height > document.Info.Height
            ? Visibility.Visible : Visibility.Collapsed;
        FullResolution.IsEnabled = true;
        }
        finally { pendingPin?.Dispose(); previousPin?.Dispose(); }
    }
    public Task LoadFullResolutionAsync(CancellationToken token = default)
        => TrackActivity("Rendering image", 10, LoadFullResolutionCoreAsync(token));
    private async Task LoadFullResolutionCoreAsync(CancellationToken token)
    {
        var native = source ?? throw new InvalidOperationException("Select a texture first.");
        var owner = session ?? throw new InvalidOperationException("Open textures first.");
        var version = generation;
        var label = ((TextBlock)images.FindName("ImageName")).Text;
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, selection.Token);
        await owner.RevalidateAsync(native, own.Token);
        NativeContainerLease? acquired = null;
        try
        {
        NativeTextureSource fullSource;
        if (native.NativeTextureIdentity is { MemberPath: not null } nativeIdentity)
        {
            acquired = await NativeContainerLease.OpenMetadataAsync(owner.Snapshot,
                nativeIdentity with { MemberPath = null }, native.CompanionLease?.MemoryBudget ?? new AssetMemoryBudget(), own.Token, allowLargeData: true);
            var result = await owner.ReadForNativeAsync(nativeIdentity, native.CatalogIdentity, acquired, own.Token,
                maximumPreviewDimension: int.MaxValue, reservePayloadMemory: true);
            fullSource = result.Source ?? throw new ArchiveException(result.FailureCode,
                result.Reason ?? "The full-resolution texture source is unavailable.");
        }
        else
        {
            var result = await owner.ReadFullResolutionAsync(native, native.CompanionLease?.MemoryBudget ?? new AssetMemoryBudget(), own.Token);
            fullSource = result.Source ?? throw new ArchiveException(result.FailureCode,
                result.Reason ?? "The full-resolution texture source is unavailable.");
            acquired = fullSource.CompanionLease;
        }
        var document = await Task.Run(() => new NativeTextureDecoder(128L * 1024 * 1024).Read(fullSource, own.Token), own.Token);
        await owner.RevalidateAsync(fullSource, own.Token);
        own.Token.ThrowIfCancellationRequested();
        if (version != generation || native != source) throw new OperationCanceledException(own.Token);
        await LoadAsync(owner, fullSource, document, label, token, fullResolution: true);
        }
        finally { acquired?.Dispose(); }
    }
    private async void FullResolution_Click(object sender, RoutedEventArgs e)
    {
        var clickedSource = source;
        FullResolution.IsEnabled = false;
        try { await LoadFullResolutionAsync(); }
        catch (OperationCanceledException) { if (ReferenceEquals(clickedSource, source)) Status.Text = "Cancelled"; }
        catch (Exception error) { if (ReferenceEquals(clickedSource, source)) Status.Text = error.Message; }
        finally { FullResolution.IsEnabled = source is not null; }
    }
    public static int SelectPreviewMip(DdsTextureDocument document, int maximumEdge = 1024,
        long maximumWorkingBytes = 128L * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (maximumEdge < 1 || maximumWorkingBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumEdge));
        var decoder = new DdsTextureDecoder(maximumWorkingBytes);
        foreach (var mip in document.MipLevels.OrderBy(level => level.Index))
            if (mip.Width <= maximumEdge && mip.Height <= maximumEdge &&
                decoder.EstimateWorkingSetBytes(document, document.DefaultSelection with { MipIndex = mip.Index }) <= maximumWorkingBytes)
                return mip.Index;
        var smallest = document.MipLevels.OrderBy(level => (long)level.Width * level.Height).First();
        if (decoder.EstimateWorkingSetBytes(document, document.DefaultSelection with { MipIndex = smallest.Index }) > maximumWorkingBytes)
            throw new TextureFormatException("TexturePreviewBudget", "No mip fits the preview budget.");
        return smallest.Index;
    }
    private static bool HasOnlyZeroAlpha(TextureSurface surface, CancellationToken token)
    {
        var bytes = surface.Rgba8.Span;
        for (var offset = 3; offset < bytes.Length; offset += 4)
        {
            if ((offset & 0xffff) == 3) token.ThrowIfCancellationRequested();
            if (bytes[offset] != 0) return false;
        }
        token.ThrowIfCancellationRequested(); return bytes.Length > 0;
    }

    public Task<NativeTexturePngExportResult> ExportPngToAsync(string destination, bool overwrite = false,
        CancellationToken token = default)
    {
        if (!export.IsCompleted) throw new InvalidOperationException("An export is already running.");
        var task = TrackActivity("Exporting PNG", 20, ExportPngCoreAsync(destination, overwrite, token));
        export = task;
        return task;
    }

    private async Task<NativeTexturePngExportResult> ExportPngCoreAsync(string destination, bool overwrite,
        CancellationToken token)
    {
        if (source is { PreviewDimension: not null }) await LoadFullResolutionAsync(token);
        var native = source ?? throw new InvalidOperationException("Select a texture first.");
        var owner = session ?? throw new InvalidOperationException("Open textures first.");
        var version = generation;
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, selection.Token);
        await images.WaitForRenderAsync(own.Token);
        own.Token.ThrowIfCancellationRequested();
        if (version != generation || source != native) throw new OperationCanceledException(own.Token);
        var naming = NamingEntryProvider?.Invoke(native);
        var document = images.CurrentDocument ?? throw new InvalidOperationException("The texture document is unavailable.");
        var surface = images.CurrentDisplaySurface ?? throw new InvalidOperationException("The texture pixels are unavailable.");
        var full = Path.GetFullPath(destination);
        using var pins = owner.PinSource(native);
        var result = await new TextureExportService().ExportAsync(document, surface, AssetExportFormat.Png,
            owner.Snapshot.SourceRoot, Path.GetDirectoryName(full)!, Path.GetFileName(full), overwrite, own.Token,
            native.Revision, async cancellation =>
            {
                await owner.RevalidateAsync(native, cancellation);
                if (ValidateNamingAsync is { } validateNaming) await validateNaming(native, cancellation);
            });
        var relativeReport = Path.GetFileName(full) + ".json";
        try
        {
            var root = Path.GetDirectoryName(full)!;
            var reportPath = OutputSafety.ResolveOutputPath(root, relativeReport);
            OutputSafety.ValidateFilePath(reportPath, owner.Snapshot.SourceRoot);
            using var outputPin = OutputSafety.PinDirectory(root);
            if (File.Exists(reportPath) && !overwrite) throw new IOException("The PNG report already exists.");
            var report = new
            {
                Schema = "native-texture-png-v1", NativeTextureSession.Version,
                Naming = naming is null ? null : new { naming.Label, naming.NameKind, naming.ResourceName, naming.ResourcePath, naming.Alias, naming.NameCandidates, naming.Owners },
                LogicalIdentity = native.Metadata.Identity, native.SelectedIdentity, native.Revision,
                native.CatalogIdentity, native.CatalogSha256, native.MetadataSha256,
                MetadataDecodedExtent = native.Metadata.ExactConsumedDecodedExtent,
                Container = new { native.Container.Identity, native.Container.Type, native.Container.Id, native.Container.Extent, native.Container.Sha256 },
                NativePayloads = native.Payloads.Select(payload => new { payload.Identity, payload.Type, payload.Id, payload.Extent, payload.Sha256 }),
                DerivedDdsSha256 = document.ContentSha256, NativeFormatCode = native.Metadata.FirstByte,
                DecoderVersion = NativeTextureDecoder.Version,
                SupportedMipIndices = Enumerable.Range(0, document.Info.MipCount), TotalNativeMipCount = native.Metadata.MipSizeCount,
                OmittedMipIndices = Enumerable.Range(document.Info.MipCount, Math.Max(0, native.Metadata.MipSizeCount - document.Info.MipCount)),
                SelectedSubresource = surface.Subresource, surface.Width, surface.Height,
                PixelFormat = document.Info.FormatName, NativeColorSpace = native.Metadata.RawFixedPrefix84[14] == 1 ? "sRGB" : "UNORM",
                PngSrgbEncoded = surface.IsSrgb, AlphaHandling = "RGBA bytes retained; no premultiplication transform",
                surface.DisplayTransform, Output = new { FileName = result.RelativePath, result.Length, result.Sha256 }
            };
            var temporary = OutputSafety.ResolveOutputPath(root, relativeReport + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(file, report, new JsonSerializerOptions { WriteIndented = true }, own.Token);
                    await file.FlushAsync(own.Token); file.Flush(true);
                }
                own.Token.ThrowIfCancellationRequested(); OutputSafety.ValidateFilePath(reportPath, owner.Snapshot.SourceRoot);
                File.Move(temporary, reportPath, overwrite);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return new(result, relativeReport, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        { return new(result, null, error.Message); }
    }

    private async void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        if (source is not { } native) return;
        var dialog = new SaveFileDialog { Title = "Export PNG", Filter = "PNG image (*.png)|*.png",
            FileName = (ExportStemProvider?.Invoke(native) ?? $"Texture-{GetSelectedResourceId(native):X8}-{native.SelectedIdentity.Ordinal}")
                + $"-mip{images.CurrentSurface?.MipIndex ?? 0}.png", OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var version = generation; ExportPng.IsEnabled = false;
        try { var result = await ExportPngToAsync(dialog.FileName, true); if (version == generation) Status.Text = result.ReportError is null ? "Exported" : "PNG exported · report unavailable"; }
        catch (OperationCanceledException) { if (version == generation) Status.Text = "Cancelled"; }
        catch (Exception error) { if (version == generation) Status.Text = error.Message; }
        finally { if (version == generation) ExportPng.IsEnabled = source is not null; }
    }

    private uint GetSelectedResourceId(NativeTextureSource native)
        => session?.Snapshot.Records.FirstOrDefault(record => record.Ordinal == native.SelectedIdentity.Ordinal)?.Id ?? native.Container.Id;

    public async Task ClearAsync()
    {
        CancelActivity();
        ++generation; source = null; session = null; ExportPng.IsEnabled = FullResolution.IsEnabled = false; FullResolution.Visibility = Visibility.Collapsed; Status.Text = "";
        await images.ClearAsync();
        sourcePin?.Dispose(); sourcePin = null;
        try { await export; } catch { }
        await DrainActivityAsync();
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); await ClearAsync(); images.ActivityChanged -= Images_ActivityChanged;
        await images.DisposeAsync(); selection.Dispose(); lifetime.Dispose();
    }
}
