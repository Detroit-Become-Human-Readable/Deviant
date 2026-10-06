using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Desktop;
using Deviant.Export;
using Deviant.Formats.Textures;

namespace Deviant.AudioCheck;

public static partial class Program
{
    private static async Task<object> ExerciseNativeTexturesAsync(AudioWindow window, string input, string output,
        IReadOnlyDictionary<string, string> options, CacheManager cache)
    {
        var ordinals = options.GetValueOrDefault("texture-ordinals")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => long.Parse(value, CultureInfo.InvariantCulture)).Distinct().ToArray() ?? [];
        Require(ordinals.Length >= 2 && ordinals.All(value => value >= 0), "--texture-ordinals requires at least two distinct nonnegative ordinals.");
        Require(DirectoryBoundary.IsWithin(cache.Location, output), "Native texture acceptance clears only a temporary cache inside --out.");
        var timings = new Dictionary<string, double>(); var heartbeat = new List<double>(); var memory = new List<long>();
        var stageReports = new List<object>(); var selectedReports = new List<object>(); string? activeStage = null;
        var clock = Stopwatch.StartNew(); var previous = clock.Elapsed.TotalMilliseconds;
        using var process = Process.GetCurrentProcess();
        var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) => { var current = clock.Elapsed.TotalMilliseconds; heartbeat.Add(current - previous); previous = current; process.Refresh(); memory.Add(process.WorkingSet64); };
        timer.Start();
        object Telemetry()
        {
            process.Refresh();
            var sorted = heartbeat.Order().ToArray();
            double Percentile(double fraction) => sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * fraction) - 1, 0, sorted.Length - 1)];
            return new { ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds, Heartbeat = new { Samples = heartbeat.Count, P50Milliseconds = Percentile(.5), P95Milliseconds = Percentile(.95), P99Milliseconds = Percentile(.99), MaximumMilliseconds = heartbeat.DefaultIfEmpty().Max() }, WorkingSet = new { Samples = memory.Count, PeakBytes = Math.Max(memory.DefaultIfEmpty().Max(), process.WorkingSet64), FinalBytes = process.WorkingSet64 } };
        }
        async Task Stage(string name, Func<Task> action)
        {
            activeStage = name; var stage = Stopwatch.StartNew(); Console.WriteLine($"Native textures: {name} started.");
            try
            {
                await action(); timings[name] = stage.Elapsed.TotalMilliseconds;
                stageReports.Add(new { Name = name, Status = "Completed", Milliseconds = stage.Elapsed.TotalMilliseconds });
                Console.WriteLine($"Native textures: {name} completed ({stage.Elapsed.TotalMilliseconds:F0} ms)."); activeStage = null;
            }
            catch
            {
                timings[name] = stage.Elapsed.TotalMilliseconds;
                stageReports.Add(new { Name = name, Status = "Failed", Milliseconds = stage.Elapsed.TotalMilliseconds });
                Console.WriteLine($"Native textures: {name} failed ({stage.Elapsed.TotalMilliseconds:F0} ms)."); throw;
            }
        }
        try
        {
            await Stage("OpenArchive", () => window.OpenArchiveAsync(input));
            var snapshot = window.CurrentSession?.Snapshot ?? throw new InvalidOperationException("The archive did not open.");
            Require(window.CurrentSession is { IsEnrichmentStarted: false }, "Index opening started audio enrichment.");
            OutputSafety.ValidateOutputRoot(output, snapshot.SourceRoot);
            var identities = ordinals.Select(ordinal => new ResourceIdentity(snapshot.IndexContentId, ordinal)).ToArray();
            foreach (var id in identities) Require(snapshot.Records.SingleOrDefault(row => row.Ordinal == id.Ordinal)?.Type is 2229 or 2230, "A requested ordinal is not a native texture.");
            await Stage("ShowTextures", () => window.ShowTexturesAsync());
            var family = window.AssetBrowser;
            TextureCatalogSession CurrentCatalogs() => (TextureCatalogSession?)typeof(AudioWindow).GetField("textureCatalogs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)
                ?? throw new InvalidOperationException("The current host has no texture catalog session.");
            object? catalogReport = null; object? filterReport = null; string? catalogKey = null; ResourceIdentity? requestedCatalog = null;
            async Task EnsureRequestedCatalogAsync()
            {
                if (requestedCatalog is not { } requested) return;
                var catalogs = CurrentCatalogs();
                Require(catalogs.Snapshot.IndexContentId == requested.IndexContentId, "The reopened catalog belongs to a different archive index.");
                var choices = (ComboBox)family.FindName("Groups");
                if (catalogKey is not null && catalogs.Groups.Any(group => group.CatalogIdentity.Key == catalogKey)
                    && choices.Items.Cast<AssetFamilyGroupChoice>().Any(choice => choice.Key == catalogKey)) return;
                Console.WriteLine($"Native textures: priority catalog read {requested.Ordinal} started.");
                var group = await catalogs.ReadCatalogAsync(requested) ?? throw new InvalidOperationException("The requested catalog has no texture group.");
                Require(ReferenceEquals(catalogs, CurrentCatalogs()), "The catalog session changed during the requested catalog read.");
                await InvokeTaskAsync(window, "RefreshTextureCatalogsAsync");
                catalogKey = group.CatalogIdentity.Key;
                await WaitAsync(() => choices.Items.Cast<AssetFamilyGroupChoice>().Any(choice => choice.Key == catalogKey), "The requested catalog group did not become available.");
                Console.WriteLine($"Native textures: priority catalog read {requested.Ordinal} completed.");
                catalogReport = new { group.Label, group.CatalogIdentity, group.ReferenceTableSha256, UniqueMembers = group.UniqueTextureMembers.Count };
            }
            if (options.TryGetValue("texture-catalog", out var catalogValue))
            {
                requestedCatalog = new ResourceIdentity(snapshot.IndexContentId, long.Parse(catalogValue, CultureInfo.InvariantCulture));
                await Stage("RequestedCatalog", async () =>
                {
                    await EnsureRequestedCatalogAsync();
                    await family.SelectGroupAsync(catalogKey!);
                });
            }
            async Task SelectReady(ResourceIdentity id)
            {
                if (catalogKey is null) await family.SelectResourceAsync(id);
                else
                {
                    await EnsureRequestedCatalogAsync();
                    await family.SelectGroupAsync(catalogKey);
                    while (!family.CurrentResources.Any(row => row.Identity == id) && ((Button)family.FindName("Next")).IsEnabled)
                        ((Button)family.FindName("Next")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var row = family.CurrentResources.SingleOrDefault(row => row.Identity == id) ?? throw new InvalidOperationException("A requested native texture is outside the requested catalog.");
                    ((DataGrid)family.FindName("Rows")).SelectedItem = row; ((DataGrid)family.FindName("Rows")).ScrollIntoView(row);
                }
                await WaitAsync(() => family.SelectedIdentity == id && family.NativeImageBrowser?.CurrentSource?.SelectedIdentity == id && family.NativeImageBrowser.ImageBrowser.CurrentDisplaySurface is not null,
                    "The requested native texture did not produce its current pixel surface: " + id.Ordinal);
                await family.NativeImageBrowser!.ImageBrowser.WaitForRenderAsync(); window.UpdateLayout();
            }
            if (options.ContainsKey("native-cache-only"))
            {
                var cycles = int.Parse(options.GetValueOrDefault("native-cache-cycles") ?? "1", CultureInfo.InvariantCulture);
                Require(cycles is >= 1 and <= 5, "Native cache cycle count must be between one and five.");
                var cycleReports = new List<object>();
                var dwellMilliseconds = int.Parse(options.GetValueOrDefault("native-cache-dwell-ms") ?? "0", CultureInfo.InvariantCulture);
                Require(dwellMilliseconds is >= 0 and <= 30000, "Native active-work dwell must be between zero and 30000 milliseconds.");
                object WorkState()
                {
                    object? Field(string name) => typeof(AssetFamilyView).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(family);
                    var tasks = ((IEnumerable<Task>)Field("operations")!).ToArray();
                    var pending = Field("nativeThumbnailPending")!;
                    return new { CatalogProgress = CurrentCatalogs().Progress, TrackedOperations = tasks.Length, IncompleteOperations = tasks.Count(task => !task.IsCompleted), PendingNativeThumbnails = (int)pending.GetType().GetProperty("Count")!.GetValue(pending)!, NativeThumbnailGateAvailable = ((SemaphoreSlim)Field("nativeThumbnailGate")!).CurrentCount, NativeSelectionSettled = Field("nativeSelectionSettled"), NativeLookupPending = Field("nativeLookupPending"), NativeRetryEnabled = ((DispatcherTimer)Field("nativeRetry")!).IsEnabled, NativeSuspended = Field("nativeSuspended"), NativeCancelled = Field("nativeCancelled"), HostStatus = ((TextBlock)window.FindName("StatusText")).Text, SessionPresent = window.CurrentSession is not null };
                }
                for (var cycle = 1; cycle <= cycles; cycle++)
                {
                    var prefix = "Cycle/" + cycle + "/";
                    await Stage(prefix + "RapidSelection", async () => { await family.SelectResourceAsync(identities[0]); await family.SelectResourceAsync(identities[1]); await SelectReady(identities[1]); });
                    var cancelButton = VisualChildren<Button>(family).Single(button => Equals(button.Content, "Cancel")); cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitAsync(() => family.NativeImageBrowser?.CurrentSource is null && family.NativeImageBrowser?.ImageBrowser.CurrentDisplaySurface is null, "Actual Cancel button retained native pixels.");
                    await Stage(prefix + "ReloadAfterCancel", () => SelectReady(identities[0]));
                    await Stage(prefix + "HomeReentry", async () =>
                    {
                        Invoke(window, "Home_Click", window, new RoutedEventArgs());
                        await WaitAsync(() => family.NativeImageBrowser?.CurrentSource is null && family.NativeImageBrowser?.ImageBrowser.CurrentDisplaySurface is null, "Home navigation retained native pixels.");
                        await window.ShowTexturesAsync(); await SelectReady(identities[0]);
                    });
                    if (options.TryGetValue("unsupported-ordinal", out var unsupportedCacheValue))
                    {
                        await Stage(prefix + "UnsupportedSelection", async () =>
                        {
                            await family.SelectResourceAsync(new(snapshot.IndexContentId, long.Parse(unsupportedCacheValue, CultureInfo.InvariantCulture)));
                            await WaitAsync(() => ((TextBlock)family.FindName("PreviewStatus")).Text != "Opening image…", "Unsupported native selection did not settle.");
                            Require(family.NativeImageBrowser?.CurrentSource is null && family.NativeImageBrowser?.ImageBrowser.CurrentDisplaySurface is null, "Unsupported selection retained native pixels.");
                        });
                    }
                    if (dwellMilliseconds > 0)
                    {
                        var beforeDwell = WorkState();
                        await Stage(prefix + "ActiveWorkDwell", () => Task.Delay(dwellMilliseconds));
                        var afterDwell = WorkState();
                        await File.WriteAllTextAsync(Path.Combine(output, $"native-cache-work-{cycle}.json"), JsonSerializer.Serialize(new { BeforeDwell = beforeDwell, AfterDwell = afterDwell }, new JsonSerializerOptions { WriteIndented = true }));
                        Console.WriteLine("Native cache work before clear: " + JsonSerializer.Serialize(afterDwell));
                    }
                    await Stage(prefix + "CacheClearDrain", async () => { await cache.ClearAsync(cancellation => InvokeTaskAsync(window, "DrainCacheUsersAsync", cancellation)); Require(cache.ActivePreviewLeases == 0, "Native cache drain retained preview leases."); });
                    string? reopenStatus = null;
                    await Stage(prefix + "CacheReload", async () =>
                    {
                        await InvokeTaskAsync(window, "ReopenAfterCacheChangeAsync", CancellationToken.None);
                        reopenStatus = ((TextBlock)window.FindName("StatusText")).Text;
                        Require(window.CurrentSession is not null, "Cache reopen did not restore the archive session: " + reopenStatus);
                        await window.ShowTexturesAsync(); await SelectReady(identities[0]);
                    });
                    Require(window.CurrentSession is { IsEnrichmentStarted: false }, "Native cache reopen started audio enrichment.");
                    await WaitAsync(() => VisibleNativeThumbnails(family).Any(thumbnail => thumbnail.DataContext is AssetFamilyChoice choice && choice.Identity == identities[0]), "Cache reload did not restore the selected native thumbnail.");
                    window.UpdateLayout(); if (!options.ContainsKey("native-no-captures")) Capture(window, Path.Combine(output, $"native-cache-reloaded-{cycle}.png"));
                    cycleReports.Add(new { Cycle = cycle, HostStatus = reopenStatus, SessionPresent = window.CurrentSession is not null, SelectedIdentity = family.NativeImageBrowser!.CurrentSource!.SelectedIdentity, AudioEnrichmentRemainedDeferred = true });
                }
                return new { Classification = "Programmatic WPF native-texture cache lifecycle", CacheReloadSucceeded = true, Cycles = cycleReports, AudioEnrichmentRemainedDeferred = true, StageMilliseconds = timings, Telemetry = Telemetry(), NoAudioPlayback = true };
            }
            foreach (var id in identities)
            {
                await Stage("Select/" + id.Ordinal, () => SelectReady(id));
                if (id == identities[0]) await Stage("SearchNarrowAndRecover", async () =>
                {
                    var search = (TextBox)family.FindName("Search");
                    var label = family.SelectedResource!.Label;
                    var unfilteredCount = family.TotalMatches;
                    search.Text = label;
                    await ((Task)typeof(AssetFamilyView).GetField("queryTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(family)!);
                    Require(family.TotalMatches > 0 && family.TotalMatches < unfilteredCount && family.CurrentResources.Any(row => row.Identity == id), "Search did not narrow the real catalog to the selected label.");
                    var narrowedCount = family.TotalMatches;
                    var rows = (DataGrid)family.FindName("Rows"); rows.SelectedItem = family.CurrentResources.Single(row => row.Identity == id);
                    await WaitAsync(() => family.NativeImageBrowser?.CurrentSource?.SelectedIdentity == id && family.NativeImageBrowser.ImageBrowser.CurrentDisplaySurface is not null, "Filtered selection lost its native preview.");
                    await family.NativeImageBrowser!.ImageBrowser.WaitForRenderAsync(); window.UpdateLayout();
                    Capture(window, Path.Combine(output, "native-search.png"));
                    search.Text = "";
                    await ((Task)typeof(AssetFamilyView).GetField("queryTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(family)!);
                    Require(family.TotalMatches == unfilteredCount, "Clearing Search did not restore the catalog result count.");
                    await SelectReady(id);
                    Require(family.NativeImageBrowser!.CurrentSource!.SelectedIdentity == id, "Search recovery changed the native preview identity.");
                    filterReport = new { Query = label, UnfilteredCount = unfilteredCount, NarrowedCount = narrowedCount, RestoredCount = family.TotalMatches, SelectedIdentity = id, PreviewIdentityPreserved = true };
                });
                await Stage("InspectExport/" + id.Ordinal, async () =>
                {
                var native = family.NativeImageBrowser!; var source = native.CurrentSource!; var image = native.ImageBrowser;
                var document = image.CurrentDocument!; var surface = image.CurrentDisplaySurface!;
                Require(document.Identity == source.Metadata.Identity && document.Revision == source.Revision && surface.Identity == document.Identity && surface.Revision == source.Revision, "Native preview identity/revision mismatch.");
                Require(image.IsDocumentBacked && new[] { "OpenDds", "ExportDds", "ExportPng" }.All(name => ((Button)image.FindName(name)).Visibility == Visibility.Collapsed && !((Button)image.FindName(name)).IsEnabled), "Hosted DDS preview exposes external file actions.");
                Require(surface.Width == document.Info.Width && surface.Height == document.Info.Height, "Native mip-zero dimensions differ from the supplied document.");
                Require(((ComboBox)image.FindName("Mips")).Items.Count == document.Info.MipCount, "Native mip controls do not represent the supported document.");
                var preview = (Image)image.FindName("Preview");
                Require(preview.Parent is ScrollViewer && ((Grid)((ScrollViewer)preview.Parent).Parent).Background is DrawingBrush, "Native preview checkerboard/scroll viewport is missing.");
                Require(((TextBox)family.FindName("Advanced")).Text.Contains(source.MetadataSha256, StringComparison.OrdinalIgnoreCase), "Selected native source provenance is not visible in Advanced.");
                foreach (var width in new[] { 1600, 1280 })
                {
                    window.WindowState = WindowState.Normal; window.Width = width; window.UpdateLayout(); await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                    var rows = (DataGrid)family.FindName("Rows");
                    await WaitAsync(() => { window.UpdateLayout(); return rows.Columns[0].ActualWidth >= 180 && rows.Columns[1].ActualWidth >= 120; }, "Native list columns did not settle after resizing.");
                    ((DataGrid)family.FindName("Rows")).ScrollIntoView(family.SelectedResource);
                    await WaitAsync(() => VisibleNativeThumbnails(family).Any(thumbnail => thumbnail.DataContext is AssetFamilyChoice choice && choice.Identity == id), "The selected archive texture did not produce its visible row thumbnail.");
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                    await Task.Delay(75);
                    var currentThumbnail = VisibleNativeThumbnails(family).Single(thumbnail => thumbnail.DataContext is AssetFamilyChoice choice && choice.Identity == id);
                    Require(FirstBgraPixel((BitmapSource)currentThumbnail.Source)[3] == 255, "The selected native RGB thumbnail is transparent.");
                    var rowBounds = currentThumbnail.TransformToAncestor(rows).TransformBounds(new Rect(currentThumbnail.RenderSize));
                    currentThumbnail.InvalidateVisual(); rows.InvalidateVisual();
                    var currentCell = VisualChildren<DataGridCell>(rows).FirstOrDefault(cell => VisualChildren<Image>(cell).Contains(currentThumbnail));
                    currentCell?.InvalidateVisual();
                    await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    await Task.Delay(100);
                    Capture(window, Path.Combine(output, $"native-{id.Ordinal}-{width}.png"));
                    Capture(currentThumbnail, Path.Combine(output, $"native-{id.Ordinal}-{width}-thumbnail.png"));
                    await File.WriteAllTextAsync(Path.Combine(output, $"native-{id.Ordinal}-{width}-thumbnail.json"), JsonSerializer.Serialize(new { currentThumbnail.ActualWidth, currentThumbnail.ActualHeight, currentThumbnail.Opacity, currentThumbnail.Visibility, RowBounds = new { rowBounds.X, rowBounds.Y, rowBounds.Width, rowBounds.Height }, Pixel = FirstBgraPixel((BitmapSource)currentThumbnail.Source) }));
                    var advanced = VisualChildren<Expander>(family).Single(expander => Equals(expander.Header, "Advanced")); advanced.IsExpanded = true;
                    window.UpdateLayout(); await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                    Capture(window, Path.Combine(output, $"native-{id.Ordinal}-{width}-provenance.png")); advanced.IsExpanded = false;
                }
                var normalCount = VisibleNativeThumbnails(family).Count;
                window.WindowState = WindowState.Maximized; window.UpdateLayout(); await Task.Delay(100); var fullscreenCount = VisibleNativeThumbnails(family).Count;
                await WaitAsync(() => VisibleNativeThumbnails(family).Any(thumbnail => thumbnail.DataContext is AssetFamilyChoice choice && choice.Identity == id), "The selected maximized row has no native thumbnail.");
                Capture(window, Path.Combine(output, $"native-{id.Ordinal}-maximized.png")); window.WindowState = WindowState.Normal;
                var original = Path.Combine(output, "exports", $"texture-{id.Ordinal}.bin");
                var originalReport = await family.ExportToAsync(original, true); var entry = originalReport.Manifest.Entries.Single(); Require(entry.Status == ExportStatus.Completed, "Native Original export did not complete.");
                var row = snapshot.Records.Single(record => record.Ordinal == id.Ordinal); var raw = await new ArchiveResourceResolver(snapshot).ReadAsync(id, 0, checked((int)row.StoredSize));
                var expectedHash = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
                Require(entry.Sha256 == expectedHash && Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(original))).ToLowerInvariant() == expectedHash, "Native Original export differs from the indexed source extent.");
                var mipBox = (ComboBox)image.FindName("Mips"); mipBox.SelectedIndex = document.Info.MipCount - 1; await image.WaitForRenderAsync();
                ((ComboBox)image.FindName("Channels")).SelectedIndex = 2; await image.WaitForRenderAsync();
                var channelSurface = image.CurrentDisplaySurface!; var shown = FirstBgraPixel((BitmapSource)((Image)image.FindName("Preview")).Source); var red = channelSurface.Rgba8.Span[0];
                Require(shown.AsSpan().SequenceEqual(new byte[] { red, red, red, 255 }), "Native channel view does not show the selected channel.");
                var png = Path.Combine(output, "exports", $"texture-{id.Ordinal}.png");
                var pngReport = await native.ExportPngToAsync(png, true); Require(pngReport.ReportError is null && pngReport.ReportRelativePath is not null, "Native PNG sidecar was not saved.");
                var exportedSurface = image.CurrentDisplaySurface!;
                await VerifyNativePngAsync(png, exportedSurface);
                var sidecarPath = Path.Combine(Path.GetDirectoryName(png)!, pngReport.ReportRelativePath!);
                using (var sidecar = JsonDocument.Parse(await File.ReadAllTextAsync(sidecarPath)))
                {
                    Require(sidecar.RootElement.GetProperty("Width").GetInt32() == exportedSurface.Width && sidecar.RootElement.GetProperty("Height").GetInt32() == exportedSurface.Height, "PNG sidecar dimensions differ from the exported surface.");
                    Require(sidecar.RootElement.GetProperty("SelectedSubresource").GetProperty("MipIndex").GetInt32() == exportedSurface.MipIndex, "PNG sidecar selected mip differs from the preview.");
                    Require(sidecar.RootElement.GetProperty("MetadataSha256").GetString() == source.MetadataSha256, "PNG sidecar lost native metadata provenance.");
                    Require(!File.ReadAllText(sidecarPath).Contains(snapshot.SourceRoot, StringComparison.OrdinalIgnoreCase), "PNG sidecar contains a local source path.");
                }
                ((ComboBox)image.FindName("Channels")).SelectedIndex = 0; mipBox.SelectedIndex = 0; await image.WaitForRenderAsync();
                await File.WriteAllTextAsync(Path.Combine(output, $"native-{id.Ordinal}-source.json"), JsonSerializer.Serialize(new { source.SelectedIdentity, source.CatalogIdentity, source.CatalogSha256, source.Metadata.Identity, source.MetadataSha256, source.Revision, source.Container.Extent, Payloads = source.Payloads.Select(payload => new { payload.Identity, payload.Extent, payload.Sha256 }), document.Info, DerivedDdsSha256 = document.ContentSha256 }, new JsonSerializerOptions { WriteIndented = true }));
                selectedReports.Add(new { id.Ordinal, document.Info.Width, document.Info.Height, SupportedMips = document.Info.MipCount, TotalNativeMips = source.Metadata.MipSizeCount, source.Metadata.FirstByte, OriginalSha256 = expectedHash, PngSha256 = pngReport.Png.Sha256, NormalVisibleThumbnails = normalCount, MaximizedVisibleThumbnails = fullscreenCount, AllPngPixelsMatched = true });
                Require(window.CurrentSession is { IsEnrichmentStarted: false }, "Native preview or export started audio enrichment.");
                });
            }
            await Stage("RapidSelection", async () => { await family.SelectResourceAsync(identities[0]); await family.SelectResourceAsync(identities[1]); await WaitAsync(() => family.NativeImageBrowser?.CurrentSource?.SelectedIdentity == identities[1] && family.NativeImageBrowser.ImageBrowser.CurrentDisplaySurface is not null, "Rapid native A/B selection published the earlier source."); await family.NativeImageBrowser!.ImageBrowser.WaitForRenderAsync(); });
            var cancel = VisualChildren<Button>(family).Single(button => Equals(button.Content, "Cancel")); cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => family.NativeImageBrowser?.CurrentSource is null && family.NativeImageBrowser?.ImageBrowser.CurrentDisplaySurface is null, "Actual Cancel button retained native pixels.");
            await Stage("ReloadAfterCancel", () => SelectReady(identities[0]));
            await Stage("OriginalAfterCancel", async () =>
            {
                var original = Path.Combine(output, "exports", "after-cancel-original.bin"); var report = await family.ExportToAsync(original, true);
                Require(report.Manifest.Entries.Single().Status == ExportStatus.Completed, "Original export remained cancelled after selecting a new known texture.");
                var row = snapshot.Records.Single(item => item.Ordinal == identities[0].Ordinal); var expected = await new ArchiveResourceResolver(snapshot).ReadAsync(identities[0], 0, checked((int)row.StoredSize));
                Require(Convert.ToHexString(SHA256.HashData(expected)).Equals(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(original))), StringComparison.Ordinal), "Post-cancel Original bytes differ from the indexed source.");
            });
            await Stage("HomeSuspend", async () => { Invoke(window, "Home_Click", window, new RoutedEventArgs()); await WaitAsync(() => family.NativeImageBrowser?.CurrentSource is null && family.NativeImageBrowser?.ImageBrowser.CurrentDisplaySurface is null, "Home navigation retained active native preview state."); });
            await Stage("ReenterTextures", async () => { await window.ShowTexturesAsync(); await SelectReady(identities[0]); });
            await WaitAsync(() => VisibleNativeThumbnails(family).Count > 0, "Warm reentry did not restore native thumbnails."); var warmCount = VisibleNativeThumbnails(family).Count;
            object? unsupportedReport = null;
            if (options.TryGetValue("unsupported-ordinal", out var unsupportedValue))
            {
                var unsupported = new ResourceIdentity(snapshot.IndexContentId, long.Parse(unsupportedValue, CultureInfo.InvariantCulture));
                await family.SelectResourceAsync(unsupported);
                await WaitAsync(() => ((TextBlock)family.FindName("PreviewStatus")).Text != "Opening image…", "Unsupported native selection did not settle.");
                Require(family.NativeImageBrowser?.CurrentSource is null && family.NativeImageBrowser?.ImageBrowser.CurrentDisplaySurface is null && ((Button)family.FindName("ExportOriginal")).IsEnabled, "Unsupported entry exposed fake pixels or disabled Original.");
                unsupportedReport = new { unsupported.Ordinal, Status = ((TextBlock)family.FindName("PreviewStatus")).Text, OriginalEnabled = true, PixelsAbsent = true };
                Capture(window, Path.Combine(output, "native-unsupported.png"));
            }
            await Stage("CacheClearDrain", async () => { await cache.ClearAsync(cancellation => InvokeTaskAsync(window, "DrainCacheUsersAsync", cancellation)); Require(cache.ActivePreviewLeases == 0, "Native cache drain retained preview leases."); });
            await Stage("CacheReload", async () => { await InvokeTaskAsync(window, "ReopenAfterCacheChangeAsync", CancellationToken.None); Require(window.CurrentSession is not null, "Cache reopen did not restore the archive session: " + ((TextBlock)window.FindName("StatusText")).Text); await window.ShowTexturesAsync(); await SelectReady(identities[0]); });
            Require(window.CurrentSession is { IsEnrichmentStarted: false }, "Native cache reopen started audio enrichment.");
            await WaitAsync(() => VisibleNativeThumbnails(family).Count > 0, "Cache reload did not regenerate a visible native thumbnail.");
            var sortedBeats = heartbeat.Order().ToArray(); double Percentile(double fraction) => sortedBeats.Length == 0 ? 0 : sortedBeats[Math.Clamp((int)Math.Ceiling(sortedBeats.Length * fraction) - 1, 0, sortedBeats.Length - 1)];
            return new { Classification = "Programmatic WPF native-texture acceptance", Snapshot = new { snapshot.IndexContentId, snapshot.PhysicalSourceStamp, snapshot.IndexPath }, RequestedOrdinals = ordinals, Catalog = catalogReport, Search = filterReport, Textures = selectedReports, WarmVisibleThumbnails = warmCount, FinalVisibleThumbnails = VisibleNativeThumbnails(family).Count, Unsupported = unsupportedReport, StageMilliseconds = timings, Heartbeat = new { Samples = heartbeat.Count, P50Milliseconds = Percentile(.5), P95Milliseconds = Percentile(.95), P99Milliseconds = Percentile(.99), MaximumMilliseconds = heartbeat.DefaultIfEmpty().Max() }, WorkingSet = new { Samples = memory.Count, PeakBytes = memory.DefaultIfEmpty().Max(), FinalBytes = process.WorkingSet64 }, AudioEnrichmentRemainedDeferred = true, NoAudioPlayback = true };
        }
        catch (Exception error)
        {
            timer.Stop();
            var partial = new { Classification = "Programmatic WPF native-texture acceptance", Status = "Failed", ActiveStage = activeStage, Error = error.ToString(), HostStatus = ((TextBlock)window.FindName("StatusText")).Text, SessionPresent = window.CurrentSession is not null, RequestedOrdinals = ordinals, Textures = selectedReports, Stages = stageReports, StageMilliseconds = timings, Telemetry = Telemetry(), NoAudioPlayback = true };
            try { await File.WriteAllTextAsync(Path.Combine(output, "native-textures-partial.json"), JsonSerializer.Serialize(partial, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception reportError) { Console.Error.WriteLine("Native texture partial report could not be saved: " + reportError.Message); }
            throw;
        }
        finally { timer.Stop(); }
    }
    private static List<Image> VisibleNativeThumbnails(AssetFamilyView family)
    {
        var rows = (DataGrid)family.FindName("Rows");
        return VisualChildren<Image>(rows).Where(image =>
        {
            if (image.Name != "NativeThumbnail" || !image.IsVisible || image.Source is not BitmapSource || image.ActualWidth <= 0 || image.ActualHeight <= 0) return false;
            var bounds = image.TransformToAncestor(rows).TransformBounds(new Rect(image.RenderSize));
            return bounds.IntersectsWith(new Rect(rows.RenderSize));
        }).ToList();
    }
    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) { var child = VisualTreeHelper.GetChild(root, index); if (child is T typed) yield return typed; foreach (var nested in VisualChildren<T>(child)) yield return nested; }
    }
    private static byte[] FirstBgraPixel(BitmapSource image) { var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); var bytes = new byte[4]; converted.CopyPixels(new Int32Rect(0, 0, 1, 1), bytes, 4, 0); return bytes; }
    private static Task VerifyNativePngAsync(string path, TextureSurface expected) => Task.Run(() =>
    {
        using var stream = File.OpenRead(path); var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Require(image.PixelWidth == expected.Width && image.PixelHeight == expected.Height, "Native PNG dimensions differ from the selected surface.");
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); var bytes = new byte[checked(expected.Width * expected.Height * 4)]; converted.CopyPixels(bytes, expected.Width * 4, 0); var rgba = expected.Rgba8.Span;
        for (var offset = 0; offset < bytes.Length; offset += 4) Require(bytes[offset] == rgba[offset + 2] && bytes[offset + 1] == rgba[offset + 1] && bytes[offset + 2] == rgba[offset] && bytes[offset + 3] == rgba[offset + 3], "Native PNG pixel bytes differ from the full RGBA surface.");
    });
}
