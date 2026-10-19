using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Formats.Textures;

namespace Deviant.Desktop;

public partial class AssetFamilyView
{
    private NativeTextureSession? nativeTextures;
    private NativeTextureView? nativeView;
    private CacheManager? nativeCache;
    private Func<NativeTextureSource, CancellationToken, DdsTextureDocument>? nativeDocumentReader;
    private CancellationTokenSource nativeWork = new();
    private CancellationTokenSource? nativeSelection;
    private readonly Dictionary<ResourceIdentity, BitmapSource> nativeThumbnails = [];
    private readonly SemaphoreSlim nativeThumbnailGate = new(1, 1);
    private long nativeVersion;
    private bool nativeEventsAttached;
    private bool nativeLookupPending;
    private TextureCatalogSession? nativeCatalogs;
    private readonly HashSet<ResourceIdentity> nativeThumbnailPending = [];
    private readonly DispatcherTimer nativeRetry = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private Task nativeClear = Task.CompletedTask;
    private int nativeSelectedCatalogCount;
    private bool nativeSelectionSettled;
    private bool nativeSuspended = true;
    private bool nativeCancelled;
    private bool nativeDisposed;
    private int nativePendingRetries;
    private readonly Dictionary<ResourceIdentity, int> nativeThumbnailCatalogCounts = [];
    public NativeTextureView? NativeImageBrowser => nativeView;
    public NativeTextureSession? NativeSession => nativeTextures;

    public Task ConfigureNativeTexturesAsync(TextureCatalogSession catalogs, CacheManager cache,
        Func<NativeTextureSource, DdsTextureDocument> documentReader)
    {
        ArgumentNullException.ThrowIfNull(documentReader);
        return ConfigureNativeTexturesAsync(catalogs, cache, (source, _) => documentReader(source));
    }
    public async Task ConfigureNativeTexturesAsync(TextureCatalogSession catalogs, CacheManager cache,
        Func<NativeTextureSource, CancellationToken, DdsTextureDocument> documentReader)
    {
        ArgumentNullException.ThrowIfNull(documentReader);
        ObjectDisposedException.ThrowIf(nativeDisposed || lifetime.IsCancellationRequested, this);
        await ClearNativeTexturesAsync();
        nativeTextures = new(catalogs); nativeCache = cache; nativeDocumentReader = documentReader;
        nativeCatalogs = catalogs; catalogs.Updated += NativeCatalogsUpdated;
        nativeSuspended = false; nativeCancelled = false; nativePendingRetries = 0;
        nativeView ??= new(); NativeImageHost.Content = nativeView;
        if (!nativeEventsAttached)
        {
            SelectedResourceChanged += NativeSelectionChanged;
            nativeRetry.Tick += (_, _) =>
            {
                nativeRetry.Stop();
                if (nativeSuspended || nativeCancelled) return;
                nativeRetry.Interval = TimeSpan.FromMilliseconds(Math.Min(2000, 200 * (1 << Math.Min(nativePendingRetries++, 4))));
                if (nativeLookupPending) QueueNativeSelection();
                else if (nativeThumbnailPending.Count > 0) StartVisibleThumbnails();
            };
            nativeEventsAttached = true;
        }
        QueueNativeSelection();
        if (SelectedIdentity is null) StartVisibleThumbnails();
    }

    private void NativeSelectionChanged(object? sender, EventArgs e) => QueueNativeSelection();
    private void NativeCatalogsUpdated(object? sender, TextureCatalogSessionUpdatedEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (nativeSuspended || nativeCancelled || sender != nativeCatalogs || nativeTextures is not { } owner) return;
            if (SelectedIdentity is { } selected && owner.KnownCatalogCount(selected) != nativeSelectedCatalogCount)
                nativeLookupPending = true;
            foreach (var choice in CurrentResources)
                if (nativeThumbnailCatalogCounts.TryGetValue(choice.Identity, out var count) && owner.KnownCatalogCount(choice.Identity) != count)
                { nativeThumbnails.Remove(choice.Identity); nativeThumbnailPending.Add(choice.Identity); }
            if (nativeLookupPending || nativeThumbnailPending.Count > 0)
            { nativeRetry.Interval = TimeSpan.FromMilliseconds(200); nativePendingRetries = 0; nativeRetry.Start(); }
        });
    }
    private void QueueNativeSelection()
    {
        if (nativeSuspended) return;
        nativeCancelled = false;
        ++nativeVersion; nativeSelection?.Cancel();
        nativeLookupPending = false;
        nativeSelectionSettled = false;
        nativeSelection?.Dispose(); nativeSelection = null;
        nativeWork.Cancel(); nativeWork.Dispose(); nativeWork = new();
        nativeThumbnails.Clear(); nativeThumbnailPending.Clear(); nativeThumbnailCatalogCounts.Clear();
        NativeImageHost.Visibility = Visibility.Collapsed;
        if (nativeView is not null) ClearNativePreview();
        if (nativeTextures is not { } owner || nativeView is null || nativeDocumentReader is null ||
            SelectedResource is not { } choice || snapshot != owner.Snapshot) return;
        nativeSelection = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, workspace.Token, nativeWork.Token);
        PreviewStatus.Text = "Opening image…";
        var version = nativeVersion;
        Track(LoadNativeSelectionAsync(owner, choice, version, nativeSelection.Token));
    }

    private async Task LoadNativeSelectionAsync(NativeTextureSession owner, AssetFamilyChoice choice,
        long version, CancellationToken token)
    {
        try
        {
            await nativeClear.WaitAsync(token);
            var result = await owner.ReadAsync(choice.Identity, token);
            token.ThrowIfCancellationRequested();
            if (version != nativeVersion || SelectedIdentity != choice.Identity) return;
            nativeLookupPending = result.Pending;
            if (result.Pending) nativeRetry.Start();
            nativeSelectedCatalogCount = result.Source?.CatalogBindingCount ?? owner.KnownCatalogCount(choice.Identity);
            if (result.Source is not { } source) { PreviewStatus.Text = result.Reason ?? "Preview unavailable"; return; }
            var document = await Task.Run(() => nativeDocumentReader!(source, token), token);
            token.ThrowIfCancellationRequested();
            if (version != nativeVersion || SelectedIdentity != choice.Identity) return;
            await nativeView!.LoadAsync(owner, source, document, choice.Label, token);
            token.ThrowIfCancellationRequested();
            if (version != nativeVersion || SelectedIdentity != choice.Identity) return;
            NativeImageHost.Visibility = Visibility.Visible; PreviewStatus.Visibility = Visibility.Collapsed;
            if (owner.KnownCatalogCount(choice.Identity) != source.CatalogBindingCount)
            { nativeLookupPending = true; nativeRetry.Start(); }
            Advanced.Text += $"\nMetadata {source.Metadata.Identity.MemberPath}\nMetadata SHA-256 {source.MetadataSha256}\n" +
                $"Derived DDS SHA-256 {document.ContentSha256}\nSupported mips {document.Info.MipCount}/{source.Metadata.MipSizeCount}\n" +
                $"Omitted mips {string.Join(", ", Enumerable.Range(document.Info.MipCount, Math.Max(0, source.Metadata.MipSizeCount - document.Info.MipCount)))}\n" + string.Join("\n", source.Payloads.Select(payload =>
                    $"Texture source {payload.Type}/{payload.Id}: {payload.Extent.RelativeSource} {payload.Extent.Offset} + {payload.Extent.Length}\nSHA-256 {payload.Sha256}"));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (version == nativeVersion) PreviewStatus.Text = error.Message; }
        finally
        {
            if (!nativeSuspended && !nativeCancelled && version == nativeVersion && !token.IsCancellationRequested)
            { nativeSelectionSettled = true; StartVisibleThumbnails(); }
        }
    }

    private void NativeThumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not AssetFamilyChoice choice) return;
        image.Source = null;
        image.Visibility = nativeTextures is null || nativeSuspended || nativeCancelled ? Visibility.Collapsed : Visibility.Visible;
        if (nativeSuspended || nativeCancelled) return;
        if (nativeThumbnails.TryGetValue(choice.Identity, out var bitmap)) image.Source = bitmap;
        else if (nativeTextures is not null && (SelectedIdentity is null || nativeSelectionSettled)) QueueThumbnail(choice, image);
    }
    private void NativeThumbnail_ContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => NativeThumbnail_Loaded(sender, new RoutedEventArgs());

    private void StartVisibleThumbnails()
    {
        if (nativeSuspended || nativeCancelled) return;
        foreach (var image in NativeImages(Rows).OrderByDescending(image => image.DataContext is AssetFamilyChoice choice && choice.Identity == SelectedIdentity))
            if (image.DataContext is AssetFamilyChoice choice) { image.Visibility = Visibility.Visible; QueueThumbnail(choice, image); }
    }
    private static IEnumerable<Image> NativeImages(DependencyObject parent)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is Image { Name: "NativeThumbnail" } image) yield return image;
            foreach (var nested in NativeImages(child)) yield return nested;
        }
    }
    private void QueueThumbnail(AssetFamilyChoice choice, Image image)
    {
        if (nativeSuspended || nativeCancelled || nativeTextures is not { } owner || nativeCache is not { } cache || nativeDocumentReader is null) return;
        if (nativeThumbnails.TryGetValue(choice.Identity, out var bitmap)) { PublishNativeThumbnail(image, bitmap); return; }
        var version = nativeVersion;
        Track(LoadThumbnailAsync(owner, cache, choice, image, version, nativeWork.Token));
    }
    private async Task LoadThumbnailAsync(NativeTextureSession owner, CacheManager cache,
        AssetFamilyChoice choice, Image image, long version, CancellationToken token)
    {
        var entered = false;
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspace.Token);
        try
        {
            await nativeThumbnailGate.WaitAsync(own.Token); entered = true;
            own.Token.ThrowIfCancellationRequested();
            if (!CanPublishNativeThumbnail(owner, choice, image, version)) return;
            if (nativeThumbnails.TryGetValue(choice.Identity, out var previous)) { PublishNativeThumbnail(image, previous); return; }
            var result = await owner.ReadAsync(choice.Identity, own.Token,
                nativeView?.CurrentSource?.RetainedSourceBytes ?? 0);
            own.Token.ThrowIfCancellationRequested();
            if (!CanPublishNativeThumbnail(owner, choice, image, version)) return;
            nativeThumbnailCatalogCounts[choice.Identity] = result.Source?.CatalogBindingCount ?? owner.KnownCatalogCount(choice.Identity);
            if (result.Pending) { nativeThumbnailPending.Add(choice.Identity); nativeRetry.Start(); return; }
            nativeThumbnailPending.Remove(choice.Identity);
            if (result.Source is not { } source) { image.ToolTip = result.Reason; return; }
            var document = await Task.Run(() => nativeDocumentReader!(source, own.Token), own.Token);
            var mip = document.MipLevels.FirstOrDefault(level => Math.Max(level.Width, level.Height) <= 64)?.Index
                ?? document.Info.MipCount - 1;
            var key = CacheManager.Key(owner.Revision.IndexContentId + ":" + owner.Revision.PhysicalSourceStamp,
                document.ContentSha256, NativeTextureDecoder.Version + ":bgra-thumbnail-rgb-v2", $"thumbnail:64:mip:{mip}:RGB");
            await using var lease = await cache.AcquirePreviewOutputAsync(key, ".png", async (file, cancellation) =>
            {
                var decoder = new DdsTextureDecoder(128L * 1024 * 1024);
                var surface = await decoder.DecodeMipAsync(document, mip, cancellation);
                var thumbnail = decoder.CreateThumbnail(surface, 64, ignoreAlpha: true);
                PngTextureWriter.Write(file, thumbnail, thumbnail.IsSrgb, cancellation);
            }, own.Token);
            BitmapSource bitmap;
            await using (var stream = new FileStream(lease.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                bitmap.Freeze();
            }
            own.Token.ThrowIfCancellationRequested();
            if (!CanPublishNativeThumbnail(owner, choice, image, version)) return;
            nativeThumbnails[choice.Identity] = bitmap;
            if (owner.KnownCatalogCount(choice.Identity) != source.CatalogBindingCount)
            { nativeThumbnails.Remove(choice.Identity); nativeThumbnailPending.Add(choice.Identity); nativeRetry.Start(); return; }
            if (image.DataContext is AssetFamilyChoice current && current.Identity == choice.Identity) PublishNativeThumbnail(image, bitmap);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (version == nativeVersion) image.ToolTip = error.Message; }
        finally { if (entered) nativeThumbnailGate.Release(); }
    }
    private bool CanPublishNativeThumbnail(NativeTextureSession owner, AssetFamilyChoice choice, Image image, long version)
        => !nativeSuspended && !nativeCancelled && version == nativeVersion && nativeTextures == owner && snapshot == owner.Snapshot &&
            CurrentResources.Any(current => current.Identity == choice.Identity) &&
            image.DataContext is AssetFamilyChoice current && current.Identity == choice.Identity;
    private static void PublishNativeThumbnail(Image image, BitmapSource bitmap)
    {
        image.Source = null; image.Source = bitmap;
        image.InvalidateVisual();
        if (image.Parent is FrameworkElement parent) parent.InvalidateVisual();
    }

    public async Task SuspendNativeTexturesAsync()
    {
        nativeSuspended = true;
        ++nativeVersion; nativeWork.Cancel(); nativeSelection?.Cancel(); NativeImageHost.Visibility = Visibility.Collapsed;
        nativeRetry.Stop(); nativeLookupPending = false; nativeThumbnailPending.Clear();
        if (nativeView is not null) await ClearNativePreview();
        await Task.WhenAll(operations.Select(async task => { try { await task; } catch { } }).ToArray());
        nativeThumbnails.Clear();
    }
    private void CancelNativeTextures()
    {
        if (nativeDisposed || nativeTextures is null) return;
        nativeCancelled = true; ++nativeVersion;
        nativeWork.Cancel(); nativeSelection?.Cancel(); nativeRetry.Stop();
        nativeLookupPending = false; nativeSelectionSettled = false;
        nativeThumbnailPending.Clear(); nativeThumbnails.Clear(); nativeThumbnailCatalogCounts.Clear();
        NativeImageHost.Visibility = Visibility.Collapsed;
        if (nativeView is not null) ClearNativePreview();
        foreach (var image in NativeImages(Rows)) image.Source = null;
        if (SelectedIdentity is not null) { PreviewStatus.Text = "Cancelled"; PreviewStatus.Visibility = Visibility.Visible; }
    }
    private Task ClearNativePreview()
    {
        var current = nativeView!.ClearAsync();
        nativeClear = nativeClear.IsCompleted ? current : Task.WhenAll(nativeClear, current);
        return Track(nativeClear);
    }
    public void ResumeNativeTextures()
    {
        if (nativeTextures is null) return;
        nativeSuspended = false; nativeCancelled = false; nativePendingRetries = 0;
        nativeWork.Cancel(); nativeWork.Dispose(); nativeWork = new();
        QueueNativeSelection();
        if (SelectedIdentity is null) StartVisibleThumbnails();
    }
    public async Task ClearNativeTexturesAsync()
    {
        if (nativeDisposed) return;
        if (nativeCatalogs is not null) nativeCatalogs.Updated -= NativeCatalogsUpdated;
        nativeCatalogs = null;
        await SuspendNativeTexturesAsync();
        var previous = nativeTextures; nativeTextures = null; nativeDocumentReader = null; nativeCache = null;
        if (previous is not null) await previous.DisposeAsync();
        nativeWork.Dispose(); nativeWork = new(); nativeSelection?.Dispose(); nativeSelection = null;
    }
    public async Task DisposeNativeTexturesAsync()
    {
        if (nativeDisposed) return;
        await ClearNativeTexturesAsync();
        nativeDisposed = true;
        if (nativeView is not null) await nativeView.DisposeAsync();
        nativeView = null; NativeImageHost.Content = null;
        SelectedResourceChanged -= NativeSelectionChanged;
        nativeWork.Dispose(); nativeThumbnailGate.Dispose();
    }
}
