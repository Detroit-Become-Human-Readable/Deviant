using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Catalog;
using Deviant.Export;
using Deviant.Formats.Textures;
using Xunit;
using Microsoft.Win32.SafeHandles;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class NativeTextureWorkflowTests
{
    [Fact]
    public void ZeroAlphaNativeColorsUseRgbPreviewAndThumbnailsWhilePngRetainsAlpha() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        NativeTextureFixture.WriteArchive(fixture.Index, zeroAlpha: true); Pump(window.OpenArchiveAsync(fixture.Index)); Pump(window.ShowTexturesAsync());
        Pump(Field<TextureCatalogSession>(window, "textureCatalogs").EnrichmentTask);
        var identity = new ResourceIdentity(window.CurrentSession!.Snapshot.IndexContentId, 2);
        Pump(window.TextureBrowser.SelectResourceAsync(identity));
        Pump(WaitUntilAsync(() => window.TextureBrowser.NativeImageBrowser?.CurrentSource?.SelectedIdentity == identity));
        var pane = window.TextureBrowser.NativeImageBrowser!; var channels = (ComboBox)pane.ImageBrowser.FindName("Channels");
        Assert.Equal("RGB", channels.SelectedItem);
        var preview = (BitmapSource)((Image)pane.ImageBrowser.FindName("Preview")).Source;
        var first = new byte[4]; preview.CopyPixels(new Int32Rect(0, 0, 1, 1), first, 4, 0);
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, first);
        var thumbnails = Field<Dictionary<ResourceIdentity, BitmapSource>>(window.TextureBrowser, "nativeThumbnails");
        Pump(WaitUntilAsync(() => thumbnails.ContainsKey(identity)));
        var thumbnail = new FormatConvertedBitmap(thumbnails[identity], System.Windows.Media.PixelFormats.Bgra32, null, 0);
        thumbnail.CopyPixels(new Int32Rect(0, 0, 1, 1), first, 4, 0); Assert.Equal(new byte[] { 30, 20, 10, 255 }, first);
        var output = Path.Combine(fixture.Root, "exports", "transparent.png"); Pump(pane.ExportPngToAsync(output));
        var png = new BitmapImage(); png.BeginInit(); png.CacheOption = BitmapCacheOption.OnLoad; png.UriSource = new Uri(output); png.EndInit();
        var pixels = new byte[32]; new FormatConvertedBitmap(png, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 16, 0);
        Assert.Equal(NativeTextureFixture.BgraPixels(0, zeroAlpha: true), pixels);
        channels.SelectedIndex = 3; Pump(pane.ImageBrowser.WaitForRenderAsync());
        Pump(pane.LoadAsync(window.TextureBrowser.NativeSession!, pane.CurrentSource!, pane.ImageBrowser.CurrentDocument!, "Texture"));
        Assert.Equal("Green", channels.SelectedItem);
    });
    [Fact]
    public void HostCreatesNativePreviewExportsFullMipAndWaitsForNavigationDrain() => WorkspaceCommitTests.Run((window, fixture) =>
    {
        NativeTextureFixture.WriteArchive(fixture.Index); Pump(window.OpenArchiveAsync(fixture.Index));
        var audio = window.CurrentSession!; Assert.True(audio.IsEnrichmentStarted || audio.IsEnrichmentComplete);
        Pump(window.ShowTexturesAsync());
        var catalogs = Field<TextureCatalogSession>(window, "textureCatalogs"); Pump(catalogs.EnrichmentTask);
        var identity = new ResourceIdentity(window.CurrentSession!.Snapshot.IndexContentId, 2);
        Pump(window.TextureBrowser.SelectResourceAsync(identity));
        Pump(WaitUntilAsync(() => window.TextureBrowser.NativeImageBrowser?.CurrentSource?.SelectedIdentity == identity));
        var owner = window.TextureBrowser.NativeSession!; var pane = window.TextureBrowser.NativeImageBrowser!;
        Assert.Equal(2, pane.ImageBrowser.CurrentDocument!.Info.MipCount);
        Assert.Equal(NativeTextureFixture.RgbaPixels(0), pane.ImageBrowser.CurrentDisplaySurface!.Rgba8.ToArray());
        Assert.True(audio.IsEnrichmentStarted || audio.IsEnrichmentComplete);
        Assert.Contains("2/3 mips", ((TextBlock)pane.ImageBrowser.FindName("Metadata")).Text);
        ((ComboBox)pane.ImageBrowser.FindName("Channels")).SelectedIndex = 5;
        var output = Path.Combine(fixture.Root, "exports", "native.png"); var exported = Pump(pane.ExportPngToAsync(output));
        Assert.Null(exported.ReportError);
        using (var report = JsonDocument.Parse(File.ReadAllText(output + ".json")))
        {
            Assert.Equal(3, report.RootElement.GetProperty("TotalNativeMipCount").GetInt32());
            Assert.Equal(new[] { 0, 1 }, report.RootElement.GetProperty("SupportedMipIndices").EnumerateArray().Select(value => value.GetInt32()));
            Assert.Equal(new[] { 2 }, report.RootElement.GetProperty("OmittedMipIndices").EnumerateArray().Select(value => value.GetInt32()));
        }
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(output); bitmap.EndInit();
        var pixels = new byte[32]; new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 16, 0);
        Assert.Equal(NativeTextureFixture.BgraPixels(0), pixels);
        Assert.True(audio.IsEnrichmentStarted || audio.IsEnrichmentComplete);
        Pump((Task)Invoke(window, "RefreshTextureCatalogsAsync")!);
        var group = Assert.Single(catalogs.Groups);
        Pump(window.TextureBrowser.ApplyFilterAsync(new Dictionary<string, string> { ["group"] = group.CatalogIdentity.Key }));
        var rows = (ListBox)window.TextureBrowser.FindName("Rows");
        rows.SelectedItem = window.TextureBrowser.CurrentResources.Single(choice => choice.Identity == identity);
        Pump(WaitUntilAsync(() => pane.CurrentSource?.SelectedIdentity == identity));
        Invoke(window.TextureBrowser, "Cancel_Click", window.TextureBrowser, new RoutedEventArgs());
        Assert.True(Field<bool>(window.TextureBrowser, "cancelled"));
        var next = new ResourceIdentity(identity.IndexContentId, 3);
        rows.SelectedItem = window.TextureBrowser.CurrentResources.Single(choice => choice.Identity == next);
        Pump(WaitUntilAsync(() => pane.CurrentSource?.SelectedIdentity == next));
        Assert.Equal(group.CatalogIdentity.Key, window.TextureBrowser.CaptureFilter()["group"]);
        var original = Path.Combine(fixture.Root, "exports", "original.bin");
        var originalReport = Pump(window.ExportTextureOriginalAsync(original)); var entry = Assert.Single(originalReport.Manifest.Entries);
        var expected = NativeTextureFixture.Create().Payloads[1].OriginalBytes.ToArray();
        Assert.Equal(ExportStatus.Completed, entry.Status); Assert.Equal(expected, File.ReadAllBytes(original));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(), entry.Sha256);
        rows.SelectedItem = window.TextureBrowser.CurrentResources.Single(choice => choice.Identity == identity);
        Pump(WaitUntilAsync(() => pane.CurrentSource?.SelectedIdentity == identity));
        Invoke(window, "Home_Click", window, new RoutedEventArgs()); Assert.True(window.TextureBrowser.IsSuspended); Assert.Null(pane.CurrentSource);
        var actualDrain = Field<Task>(window, "textureNavigationDrain");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.GetType().GetField("textureNavigationDrain", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, Task.WhenAll(actualDrain, gate.Task));
        var entering = window.ShowTexturesAsync(); Pump(Task.Delay(30)); Assert.False(entering.IsCompleted); Assert.True(window.TextureBrowser.IsSuspended);
        gate.SetResult(); Pump(entering); Pump(WaitUntilAsync(() => pane.CurrentSource?.SelectedIdentity == identity)); Assert.Same(owner, window.TextureBrowser.NativeSession);
        var beforeRefresh = window.CurrentSession!;
        Invoke(window, "Refresh_Click", window, new RoutedEventArgs());
        Pump(WaitUntilAsync(() => window.CurrentSession is { } refreshed && refreshed != beforeRefresh && ((Button)window.FindName("RefreshButton")).IsEnabled));
        Assert.Equal("textures", Field<string>(window, "currentView")); Assert.True(window.CurrentSession!.IsEnrichmentStarted || window.CurrentSession.IsEnrichmentComplete);
        Pump(window.ShowTexturesAsync()); Pump(window.TextureBrowser.SelectResourceAsync(identity));
        Pump(WaitUntilAsync(() => window.TextureBrowser.NativeImageBrowser?.CurrentSource?.SelectedIdentity == identity));
        var cache = Field<CacheManager>(window, "cache");
        Pump(cache.ClearAsync(token => (Task)Invoke(window, "DrainCacheUsersAsync", token)!));
        Pump((Task)Invoke(window, "ReopenAfterCacheChangeAsync", CancellationToken.None)!);
        Assert.Equal("textures", Field<string>(window, "currentView")); Assert.True(window.CurrentSession!.IsEnrichmentStarted || window.CurrentSession.IsEnrichmentComplete);
        Pump(window.ShowTexturesAsync()); Pump(window.TextureBrowser.SelectResourceAsync(identity));
        Pump(WaitUntilAsync(() => window.TextureBrowser.NativeImageBrowser?.CurrentSource?.SelectedIdentity == identity));
        Assert.True(window.CurrentSession!.IsEnrichmentStarted || window.CurrentSession.IsEnrichmentComplete);
        var second = Path.Combine(fixture.Root, "other", "archive.idx"); DiscoveryFixture.WriteIndex(second, [(999, new byte[] { 1 })]);
        Pump(window.OpenArchiveAsync(second)); Assert.Null(window.TextureBrowser.NativeSession);
        Pump(Assert.ThrowsAsync<ObjectDisposedException>(() => owner.ReadAsync(identity)));
    });
    [Fact]
    public void CancelButtonStopsNativeConverterAndCallbacksThenAllowsFreshSelection() => RunSta(() =>
    {
        using var fixture = new DiscoveryFixture(1); WriteNativeArchive(fixture.Index);
        var snapshot = Pump(new ArchiveReader().OpenAsync(fixture.Index));
        var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp)); Pump(catalogs.EnrichmentTask);
        var cache = new CacheManager(fixture.Library, new(fixture.Cache)); var view = new AssetFamilyView();
        using var entered = new ManualResetEventSlim(); using var cancelled = new ManualResetEventSlim(); var produced = 0;
        try
        {
            Pump(view.LoadAsync(snapshot, [2229], "Texture")); Pump(view.SelectResourceAsync(new(snapshot.IndexContentId, 2)));
            view.DiscoveryCancel = catalogs.Cancel;
            Pump(view.ConfigureNativeTexturesAsync(catalogs, cache, (source, token) =>
            {
                if (Interlocked.Increment(ref produced) == 1)
                {
                    entered.Set();
                    try { if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5))) throw new IOException("Cancel test timed out."); token.ThrowIfCancellationRequested(); }
                    finally { if (token.IsCancellationRequested) cancelled.Set(); }
                }
                return new DdsTextureReader().Read(BgraDds(), source.Metadata.Identity, source.Revision);
            }));
            Pump(WaitUntilAsync(() => entered.IsSet)); Invoke(view, "Cancel_Click", view, new RoutedEventArgs());
            Assert.True(Field<bool>(view, "nativeCancelled")); Assert.True(Field<CancellationTokenSource>(view, "nativeWork").IsCancellationRequested);
            Assert.Equal(Visibility.Collapsed, ((ContentControl)view.FindName("NativeImageHost")).Visibility);
            Pump(WaitUntilAsync(() => cancelled.IsSet));
            Invoke(view, "NativeCatalogsUpdated", catalogs, new TextureCatalogSessionUpdatedEventArgs(catalogs.Progress));
            var retry = Field<DispatcherTimer>(view, "nativeRetry"); retry.Interval = TimeSpan.FromMilliseconds(30); retry.Start();
            Pump(Task.Delay(120)); Assert.Equal(1, Volatile.Read(ref produced)); Assert.Equal(0, cache.ActivePreviewLeases);
            Assert.Null(view.NativeImageBrowser!.CurrentSource); Assert.Null(view.NativeImageBrowser.ImageBrowser.CurrentDocument);
            Pump(view.SelectResourceAsync(new(snapshot.IndexContentId, 2)));
            Pump(WaitUntilAsync(() => view.NativeImageBrowser.CurrentSource is not null));
            Assert.False(Field<bool>(view, "nativeCancelled")); Assert.True(Volatile.Read(ref produced) > 1);
            Pump(view.SuspendNativeTexturesAsync()); var count = Volatile.Read(ref produced);
            Invoke(view, "Cancel_Click", view, new RoutedEventArgs()); Pump(view.SelectResourceAsync(new(snapshot.IndexContentId, 2))); Pump(Task.Delay(60));
            Assert.True(Field<bool>(view, "nativeSuspended")); Assert.True(Field<bool>(view, "nativeCancelled")); Assert.Equal(count, Volatile.Read(ref produced));
        }
        finally { Pump(view.DisposeAsync().AsTask()); Pump(catalogs.DisposeAsync().AsTask()); }
    });
    [Fact]
    public void StandaloneClearAndDisposeReleaseNativeOwnersAndPreviewState() => RunSta(() =>
    {
        using var fixture = new DiscoveryFixture(1); WriteNativeArchive(fixture.Index);
        var snapshot = Pump(new ArchiveReader().OpenAsync(fixture.Index));
        var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp)); Pump(catalogs.EnrichmentTask);
        var cache = new CacheManager(fixture.Library, new(fixture.Cache)); var view = new AssetFamilyView();
        try
        {
            void Open()
            {
                Pump(view.LoadAsync(snapshot, [2229], "Texture")); Pump(view.ConfigureNativeTexturesAsync(catalogs, cache,
                    source => new DdsTextureReader().Read(BgraDds(), source.Metadata.Identity, source.Revision)));
                Pump(view.SelectResourceAsync(new(snapshot.IndexContentId, 2)));
                Pump(WaitUntilAsync(() => view.NativeImageBrowser?.CurrentSource is not null));
            }
            Open(); var previous = view.NativeSession!; var pane = view.NativeImageBrowser!;
            Pump(view.ClearAsync()); Assert.Null(view.NativeSession); Assert.Null(pane.CurrentSource); Assert.Null(pane.ImageBrowser.CurrentDocument);
            Pump(Assert.ThrowsAsync<ObjectDisposedException>(() => previous.ReadAsync(new(snapshot.IndexContentId, 2))));
            Open(); Assert.NotSame(previous, view.NativeSession);
            Pump(view.DisposeAsync().AsTask()); Assert.Null(view.NativeSession); Assert.Null(view.NativeImageBrowser);
            Assert.Null(((ContentControl)view.FindName("NativeImageHost")).Content);
            Pump(Assert.ThrowsAsync<ObjectDisposedException>(() => view.ConfigureNativeTexturesAsync(catalogs, cache,
                source => new DdsTextureReader().Read(BgraDds(), source.Metadata.Identity, source.Revision))));
        }
        finally { Pump(view.DisposeAsync().AsTask()); Pump(catalogs.DisposeAsync().AsTask()); }
    });
    [Fact]
    public void SuspensionRejectsQueuedCatalogUpdatesUntilExplicitResume() => RunSta(() =>
    {
        using var fixture = new DiscoveryFixture(1); WriteNativeArchive(fixture.Index);
        var snapshot = Pump(new ArchiveReader().OpenAsync(fixture.Index));
        var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp)); Pump(catalogs.EnrichmentTask);
        var cache = new CacheManager(fixture.Library, new(fixture.Cache)); var view = new AssetFamilyView();
        var window = new Window { Content = view, Width = 900, Height = 600, Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); var produced = 0;
        try
        {
            Pump(view.LoadAsync(snapshot, [2229], "Texture")); window.Show(); Pump(Task.Delay(20));
            Pump(view.ConfigureNativeTexturesAsync(catalogs, cache, source =>
            {
                if (Interlocked.Increment(ref produced) == 1) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Thumbnail gate timed out."); }
                return new DdsTextureReader().Read(BgraDds(), source.Metadata.Identity, source.Revision);
            }));
            Pump(WaitUntilAsync(() => entered.IsSet));
            Invoke(view, "NativeCatalogsUpdated", catalogs, new TextureCatalogSessionUpdatedEventArgs(catalogs.Progress));
            var suspended = view.SuspendNativeTexturesAsync(); Pump(Task.Delay(30));
            Assert.False(suspended.IsCompleted); Assert.True(Field<bool>(view, "nativeSuspended"));
            Assert.Equal(1, Volatile.Read(ref produced)); Assert.Equal(0, cache.ActivePreviewLeases);
            release.Set(); Pump(suspended); Pump(Task.Delay(250));
            Assert.Equal(1, Volatile.Read(ref produced)); Assert.Equal(0, cache.PreviewUsageBytes);
            view.ResumeNativeTextures(); Pump(WaitUntilAsync(() => cache.PreviewUsageBytes > 0));
            Assert.True(Volatile.Read(ref produced) > 1);
        }
        finally { release.Set(); Pump(view.ClearNativeTexturesAsync()); Pump(view.DisposeAsync().AsTask()); Pump(catalogs.DisposeAsync().AsTask()); window.Close(); }
    });

    [Fact]
    public void RecycledRowRejectsCachedThumbnailAfterGateWait() => RunSta(() =>
    {
        using var fixture = new DiscoveryFixture(1); WriteNativeArchive(fixture.Index);
        var snapshot = Pump(new ArchiveReader().OpenAsync(fixture.Index));
        var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp)); Pump(catalogs.EnrichmentTask);
        var cache = new CacheManager(fixture.Library, new(fixture.Cache)); var view = new AssetFamilyView();
        try
        {
            Pump(view.LoadAsync(snapshot, [2229], "Texture")); Pump(view.ConfigureNativeTexturesAsync(catalogs, cache,
                source => new DdsTextureReader().Read(BgraDds(), source.Metadata.Identity, source.Revision)));
            var choice = Assert.Single(view.CurrentResources); var image = new Image { DataContext = choice };
            var gate = Field<SemaphoreSlim>(view, "nativeThumbnailGate"); Assert.True(gate.Wait(0));
            var version = Field<long>(view, "nativeVersion");
            var pending = (Task)Invoke(view, "LoadThumbnailAsync", view.NativeSession!, cache, choice, image, version, CancellationToken.None)!;
            Field<Dictionary<ResourceIdentity, BitmapSource>>(view, "nativeThumbnails")[choice.Identity] = BitmapSource.Create(1, 1, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 30, 20, 10, 128 }, 4);
            image.DataContext = choice with { Identity = new(snapshot.IndexContentId, 99), Label = "Other texture" };
            gate.Release(); Pump(pending); Assert.Null(image.Source); Assert.Equal(0, cache.ActivePreviewLeases);
        }
        finally { Pump(view.ClearNativeTexturesAsync()); Pump(view.DisposeAsync().AsTask()); Pump(catalogs.DisposeAsync().AsTask()); }
    });

    [Fact]
    public void PendingSelectionReconcilesCompletionWithoutAnotherCatalogEvent() => RunSta(() =>
    {
        using var fixture = new DiscoveryFixture(1); WriteNativeArchive(fixture.Index);
        var snapshot = Pump(new ArchiveReader().OpenAsync(fixture.Index)); using var revisions = new BlockingRevisionProvider();
        var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp), referenceReader: new LocalizationReferenceReader(revisions));
        var cache = new CacheManager(fixture.Library, new(fixture.Cache)); var view = new AssetFamilyView();
        try
        {
            Assert.True(revisions.Entered.Wait(TimeSpan.FromSeconds(3))); Pump(view.LoadAsync(snapshot, [2229], "Texture"));
            Pump(view.SelectResourceAsync(new(snapshot.IndexContentId, 2))); Pump(view.ConfigureNativeTexturesAsync(catalogs, cache,
                source => new DdsTextureReader().Read(BgraDds(), source.Metadata.Identity, source.Revision)));
            Pump(WaitUntilAsync(() => Field<bool>(view, "nativeLookupPending")));
            var method = typeof(AssetFamilyView).GetMethod("NativeCatalogsUpdated", BindingFlags.Instance | BindingFlags.NonPublic)!;
            catalogs.Updated -= (EventHandler<TextureCatalogSessionUpdatedEventArgs>)Delegate.CreateDelegate(typeof(EventHandler<TextureCatalogSessionUpdatedEventArgs>), view, method);
            revisions.Release.Set(); Pump(catalogs.EnrichmentTask);
            Pump(WaitUntilAsync(() => view.NativeImageBrowser?.CurrentSource is not null));
            Assert.False(Field<bool>(view, "nativeLookupPending"));
        }
        finally { revisions.Release.Set(); Pump(view.ClearNativeTexturesAsync()); Pump(view.DisposeAsync().AsTask()); Pump(catalogs.DisposeAsync().AsTask()); }
    });
    [Fact]
    public async Task ReturnsPendingDuringCatalogDiscoveryAndResolvesAfterUpdate()
    {
        using var fixture = new DiscoveryFixture(1); WriteNativeArchive(fixture.Index);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        using var revisions = new BlockingRevisionProvider();
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp),
            referenceReader: new LocalizationReferenceReader(revisions));
        await using var native = new NativeTextureSession(catalogs);
        var identity = new ResourceIdentity(snapshot.IndexContentId, 2);
        try
        {
            Assert.True(revisions.Entered.Wait(TimeSpan.FromSeconds(3)));
            var pending = await native.ReadAsync(identity).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(pending.Pending); Assert.Null(pending.Source);
        }
        finally { revisions.Release.Set(); }
        await catalogs.EnrichmentTask;
        var resolved = await native.ReadAsync(identity);
        Assert.False(resolved.Pending); Assert.NotNull(resolved.Source);
    }
    [Fact]
    public async Task ResolvesCatalogMetadataAcrossDecodedPrefixAndRawSuffixAndRejectsChangedPayload()
    {
        using var fixture = new DiscoveryFixture(1);
        WriteNativeArchive(fixture.Index);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await catalogs.EnrichmentTask;
        var selected = new ResourceIdentity(snapshot.IndexContentId, 2);
        var result = await native.ReadAsync(selected);
        var source = Assert.IsType<NativeTextureSource>(result.Source);
        Assert.Equal(selected, source.SelectedIdentity);
        Assert.Equal("filetext/40", source.Metadata.Identity.MemberPath);
        Assert.Equal(1, source.Metadata.Mip0Width);
        Assert.Equal(1, source.Metadata.Mip0Height);
        Assert.Equal(new byte[] { 30, 20, 10, 128 }, Assert.Single(source.Payloads).OriginalBytes.ToArray());
        Assert.NotEqual(source.Container.Sha256, source.MetadataSha256);
        var overBudget = await native.ReadAsync(selected, reservedSourceBytes: NativeTextureSession.MaximumRetainedSourceBytes);
        Assert.Null(overBudget.Source); Assert.Contains("memory budget", overBudget.Reason);
        await native.RevalidateAsync(source);
        var volume = Path.ChangeExtension(fixture.Index, ".dat");
        var timestamp = File.GetLastWriteTimeUtc(volume);
        var bytes = File.ReadAllBytes(volume); bytes[^1] ^= 1;
        File.WriteAllBytes(volume, bytes); File.SetLastWriteTimeUtc(volume, timestamp);
        var error = await Assert.ThrowsAsync<ArchiveException>(() => native.RevalidateAsync(source));
        Assert.Equal("SourceChanged", error.Code);
    }

    [Fact]
    public async Task ResolvesAmbiguousMetadataBeforeAdmittingAnyNativePayloadRead()
    {
        using var fixture = new DiscoveryFixture(1); WriteNativeArchive(fixture.Index, duplicateMetadata: true);
        var index = File.ReadAllBytes(fixture.Index);
        BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(IndexLayout.HeaderLength + 2 * IndexLayout.RecordLength + 12), uint.MaxValue);
        File.WriteAllBytes(fixture.Index, index);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        Assert.Equal(ResourceValidity.InvalidRange, snapshot.Records[2].Validity);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await catalogs.EnrichmentTask;
        await using var native = new NativeTextureSession(catalogs);
        var result = await native.ReadAsync(new(snapshot.IndexContentId, 2));
        Assert.Null(result.Source); Assert.Contains("Multiple texture records", result.Reason);
        Assert.Equal(2, result.CandidateIdentities!.Count);
        Assert.NotEqual(result.CandidateIdentities[0], result.CandidateIdentities[1]);
    }

    [Fact]
    public async Task SourcePinsIncludeCatalogInAnotherArchiveVolume()
    {
        using var fixture = new DiscoveryFixture(1); WriteNativeArchive(fixture.Index);
        var catalogPath = Path.ChangeExtension(fixture.Index, ".d01");
        File.WriteAllBytes(catalogPath, File.ReadAllBytes(Path.ChangeExtension(fixture.Index, ".dat"))[..38]);
        var index = File.ReadAllBytes(fixture.Index);
        BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(IndexLayout.HeaderLength + 24), 1);
        File.WriteAllBytes(fixture.Index, index);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await catalogs.EnrichmentTask; await using var native = new NativeTextureSession(catalogs);
        var source = (await native.ReadAsync(new(snapshot.IndexContentId, 2))).Source!;
        using (native.PinSource(source))
            Assert.Throws<IOException>(() => { using var writer = File.OpenHandle(catalogPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
        using var released = File.OpenHandle(catalogPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InvalidSerializedBoundaryAndDuplicateNativeReferencesDoNotSelectAnotherTexture(bool invalidBoundary, bool duplicateReferences)
    {
        using var fixture = new DiscoveryFixture(1);
        WriteNativeArchive(fixture.Index, invalidBoundary, duplicateReferences);
        var snapshot = await new ArchiveReader().OpenAsync(fixture.Index);
        await using var catalogs = new TextureCatalogSession(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
        await using var native = new NativeTextureSession(catalogs);
        await catalogs.EnrichmentTask;
        var result = await native.ReadAsync(new(snapshot.IndexContentId, 2));
        Assert.Null(result.Source); Assert.NotNull(result.Reason);
    }

    [Fact]
    public void HostedNativePngKeepsAllChannelsAndSourceValidationProtectsPreviousOutput()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            using var fixture = new DiscoveryFixture(1);
            WriteNativeArchive(fixture.Index);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var view = new NativeTextureView();
            var window = new Window { Content = view, Width = 800, Height = 600, ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000 };
            TextureCatalogSession? catalogs = null; NativeTextureSession? native = null;
            try
            {
                var snapshot = Pump(new ArchiveReader().OpenAsync(fixture.Index));
                catalogs = new(snapshot, new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp)); native = new(catalogs);
                Pump(catalogs.EnrichmentTask);
                var source = Pump(native.ReadAsync(new(snapshot.IndexContentId, 2))).Source!;
                var document = new DdsTextureReader().Read(BgraDds(), source.Metadata.Identity, source.Revision);
                window.Show(); Pump(view.LoadAsync(native, source, document, "Texture"));
                ((ComboBox)view.ImageBrowser.FindName("Channels")).SelectedIndex = 3;
                var destination = Path.Combine(fixture.Root, "exports", "texture.png");
                var exported = Pump(view.ExportPngToAsync(destination));
                Assert.Null(exported.ReportError);
                Assert.Equal("texture.png.json", exported.ReportRelativePath);
                using (var report = JsonDocument.Parse(File.ReadAllText(destination + ".json")))
                {
                    Assert.Equal(source.MetadataSha256, report.RootElement.GetProperty("MetadataSha256").GetString());
                    Assert.Equal(document.ContentSha256, report.RootElement.GetProperty("DerivedDdsSha256").GetString());
                    Assert.Equal(2, report.RootElement.GetProperty("SelectedIdentity").GetProperty("Ordinal").GetInt64());
                    Assert.Equal(exported.Png.Sha256, report.RootElement.GetProperty("Output").GetProperty("Sha256").GetString());
                    Assert.DoesNotContain(fixture.Root, report.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
                }
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(destination); bitmap.EndInit();
                var pixels = new byte[4]; new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 4, 0);
                Assert.Equal(new byte[] { 30, 20, 10, 128 }, pixels);
                var reportBlocked = Path.Combine(fixture.Root, "exports", "report-blocked.png");
                Directory.CreateDirectory(reportBlocked + ".json");
                var partial = Pump(view.ExportPngToAsync(reportBlocked));
                Assert.NotNull(partial.ReportError); Assert.True(File.Exists(reportBlocked));
                var previous = File.ReadAllBytes(destination);
                var volume = Path.ChangeExtension(fixture.Index, ".dat"); var bytes = File.ReadAllBytes(volume); bytes[^1] ^= 1; File.WriteAllBytes(volume, bytes);
                Assert.Throws<ArchiveException>(() => Pump(view.ExportPngToAsync(destination, true)));
                Assert.Equal(previous, File.ReadAllBytes(destination));
                Pump(view.ClearAsync()); Assert.Null(view.CurrentSource); Assert.Null(view.ImageBrowser.CurrentDocument);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                Pump(view.DisposeAsync().AsTask()); window.Close();
                if (native is not null) Pump(native.DisposeAsync().AsTask());
                if (catalogs is not null) Pump(catalogs.DisposeAsync().AsTask());
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)), "Native texture view check timed out.");
        if (failure is not null) throw new InvalidOperationException("Native texture view check failed.", failure);
    }

    private static void WriteNativeArchive(string path, bool invalidBoundary = false, bool duplicateReferences = false, bool duplicateMetadata = false)
    {
        var metadata = new byte[116]; "FILETEXT"u8.CopyTo(metadata); Put(metadata, 8, 24);
        BinaryPrimitives.WriteUInt16LittleEndian(metadata.AsSpan(13), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(metadata.AsSpan(15), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(metadata.AsSpan(17), 1);
        metadata[19] = 1; metadata[24] = 1;
        Put(metadata, 100, 4); Put(metadata, 104, 2229); Put(metadata, 108, 3);
        var body = new byte[metadata.Length + 12 + (invalidBoundary ? 1 : 0)]; metadata.CopyTo(body, 0);
        var next = metadata.Length + (invalidBoundary ? 1 : 0); "FTEXCRAW"u8.CopyTo(body.AsSpan(next)); Put(body, next + 8, 1);
        if (duplicateMetadata) body = body.Concat(body).ToArray();
        var logical = new byte[40 + body.Length]; "DC_INFO "u8.CopyTo(logical); Put(logical, 8, 3); Put(logical, 12, 8);
        "DC_DATA "u8.CopyTo(logical.AsSpan(24)); Put(logical, 32, 23); Put(logical, 36, (uint)body.Length); body.CopyTo(logical, 40);
        var prefixLength = 80;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true)) zlib.Write(logical.AsSpan(0, prefixLength));
        var compressedBytes = compressed.ToArray(); var segs = new byte[32 + compressedBytes.Length + logical.Length - prefixLength];
        "segs"u8.CopyTo(segs); BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(4), 1); BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(6), 1);
        Put(segs, 8, (uint)prefixLength); Put(segs, 12, (uint)compressedBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(16), (ushort)compressedBytes.Length); BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(18), (ushort)prefixLength);
        Put(segs, 20, 1);
        compressedBytes.CopyTo(segs, 32); logical.AsSpan(prefixLength).CopyTo(segs.AsSpan(32 + compressedBytes.Length));
        var catalog = new byte[duplicateReferences ? 47 : 38]; "COM_CONT"u8.CopyTo(catalog); Put(catalog, 8, 6); Put(catalog, 12, (uint)catalog.Length - 16); Put(catalog, 16, duplicateReferences ? 3u : 2u);
        Put(catalog, 20, 29); Put(catalog, 24, 2); Put(catalog, 29, 2229); Put(catalog, 33, 3);
        if (duplicateReferences) { Put(catalog, 38, 2229); Put(catalog, 42, 3); }
        DiscoveryFixture.WriteIndex(path, [(3, catalog), (29, segs), (2229, new byte[] { 30, 20, 10, 128 })]);
    }
    private static byte[] BgraDds()
    {
        var bytes = new byte[152]; "DDS "u8.CopyTo(bytes); Put(bytes, 4, 124); Put(bytes, 8, 0x1007); Put(bytes, 12, 1); Put(bytes, 16, 1);
        Put(bytes, 76, 32); Put(bytes, 80, 4); "DX10"u8.CopyTo(bytes.AsSpan(84)); Put(bytes, 108, 0x1000); Put(bytes, 128, 87); Put(bytes, 132, 3); Put(bytes, 140, 1);
        new byte[] { 30, 20, 10, 128 }.CopyTo(bytes, 148); return bytes;
    }
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static async Task WaitUntilAsync(Func<bool> predicate)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!predicate()) await Task.Delay(10, timeout.Token); }
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Native workflow timed out.");
        if (failure is not null) throw new InvalidOperationException("Native workflow failed.", failure);
    }
    private sealed class BlockingRevisionProvider : ISourceRevisionProvider, IDisposable
    {
        private readonly FileRevisionProvider inner = new(); private int entered;
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public FileStamp Capture(string path) => inner.Capture(path);
        public FileStamp Capture(SafeFileHandle handle, string path)
        {
            if (Interlocked.Exchange(ref entered, 1) == 0)
            { Entered.Set(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Catalog test gate timed out."); }
            return inner.Capture(handle, path);
        }
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame(); task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
}
