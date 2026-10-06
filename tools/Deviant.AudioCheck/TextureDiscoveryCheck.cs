using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Desktop;
using Deviant.Export;

namespace Deviant.AudioCheck;

public static partial class Program
{
    private sealed record DiscoveryNamedCase(long Ordinal, long ContainerOrdinal, string OriginalPath, string OwnerLabel);
    private sealed record DiscoveryPixelCase(long Ordinal, int[] Mips);
    private sealed record DiscoveryWorkflowPlan(string IndexContentId, long[] PriorityContainers, long[] PriorityCatalogs,
        int MaximumContainers, DiscoveryNamedCase[] NamedCases, DiscoveryPixelCase[] PixelCases);
    private sealed record DiscoverySourceChangePlan(string IndexPath, long TextureOrdinal, long SupportingOrdinal, long RelativeByteOffset);

    private static async Task<object> ExerciseTextureDiscoveryAsync(AudioWindow window, string input, string output,
        IReadOnlyDictionary<string, string> options, CacheManager cache, AliasStore aliases)
    {
        var planPath = options.GetValueOrDefault("discovery-plan") ?? throw new ArgumentException("--discovery-plan is required.");
        var plan = JsonSerializer.Deserialize<DiscoveryWorkflowPlan>(await File.ReadAllTextAsync(planPath))
            ?? throw new ArgumentException("The discovery workflow plan is empty.");
        Require(plan.NamedCases.Length >= 3 && plan.NamedCases.Select(item => item.ContainerOrdinal).Distinct().Count() >= 3,
            "The workflow requires named examples from at least three different containers.");
        Require(plan.PixelCases.Length >= 3 && plan.MaximumContainers > 0, "The workflow requires three pixel examples and a positive container limit.");
        Require(DirectoryBoundary.IsWithin(cache.Location, output), "Discovery acceptance requires a temporary cache inside --out.");
        var stages = new List<object>(); var namedReports = new List<object>(); var pixelReports = new List<object>();
        var timings = new Dictionary<string, double>(); string? activeStage = null;
        var lifecycleReports = new List<object>();
        var exportRoot = Path.Combine(output, "exports"); Directory.CreateDirectory(exportRoot);
        async Task Stage(string name, Func<Task> action)
        {
            activeStage = name; var watch = Stopwatch.StartNew(); Console.WriteLine($"Texture discovery: {name} started.");
            try { await action(); timings[name] = watch.Elapsed.TotalMilliseconds; stages.Add(new { Name = name, Status = "Completed", Milliseconds = watch.Elapsed.TotalMilliseconds }); activeStage = null; }
            catch { timings[name] = watch.Elapsed.TotalMilliseconds; stages.Add(new { Name = name, Status = "Failed", Milliseconds = watch.Elapsed.TotalMilliseconds }); throw; }
            finally { Console.WriteLine($"Texture discovery: {name} ended ({watch.Elapsed.TotalMilliseconds:F0} ms)."); }
        }
        TextureDiscoverySession Discovery() => (TextureDiscoverySession?)typeof(AudioWindow)
            .GetField("textureDiscovery", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)
            ?? throw new InvalidOperationException("The current window has no texture discovery session.");
        async Task OpenTextures()
        {
            await window.OpenArchiveAsync(input);
            var snapshot = window.CurrentSession?.Snapshot ?? throw new InvalidOperationException("The archive did not open: " + ((TextBlock)window.FindName("StatusText")).Text);
            Require(snapshot.IndexContentId == plan.IndexContentId, "The workflow plan belongs to a different index.");
            Require(window.CurrentSession is { IsEnrichmentStarted: false }, "Opening textures started audio enrichment.");
            window.TextureDiscoveryOptions = new(plan.MaximumContainers, plan.PriorityContainers.Select(ordinal => new ResourceIdentity(snapshot.IndexContentId, ordinal)).ToArray());
            window.TextureCatalogOptions = new(MaximumCatalogs: 100_000, MaximumReferenceRows: 1_000_000,
                PriorityCatalogs: plan.PriorityCatalogs.Select(ordinal => new ResourceIdentity(snapshot.IndexContentId, ordinal)).ToArray());
            await window.ShowTexturesAsync();
        }
        Dictionary<string, string> Filter(string? search = null, string? context = null) => new()
        { ["search"] = search ?? "", ["context"] = context ?? "", ["sort"] = "1" };
        async Task SelectPixel(ResourceIdentity identity)
        {
            await window.TextureBrowser.SelectResourceAsync(identity);
            await WaitAsync(() => window.TextureBrowser.NativeImageBrowser.CurrentSource?.SelectedIdentity == identity &&
                window.TextureBrowser.NativeImageBrowser.ImageBrowser.CurrentSurface is not null,
                "The selected texture did not produce its current pixels: " + identity.Ordinal);
            await window.TextureBrowser.NativeImageBrowser.ImageBrowser.WaitForRenderAsync();
        }
        async Task VerifyOriginal(TextureDiscoveryEntry entry)
        {
            var path = Path.Combine(exportRoot, entry.ExportStem + ".bin");
            var report = await window.ExportTextureOriginalAsync(path, true);
            var completed = report.Manifest.Entries.Single();
            Require(completed.Status == ExportStatus.Completed, "Original export failed.");
            var snapshot = window.CurrentSession!.Snapshot;
            var row = snapshot.Records.Single(item => item.Ordinal == entry.Identity.Ordinal);
            var bytes = await new ArchiveResourceResolver(snapshot).ReadAsync(entry.Identity, 0, checked((int)row.StoredSize));
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Require(completed.Sha256 == hash && Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant() == hash,
                "Original export differs from the exact indexed texture record.");
        }
        try
        {
            await Stage("OpenTextures", OpenTextures);
            await Stage("EarlySelectionCancelHome", async () =>
            {
                var initial = window.CurrentSession!.Snapshot;
                var browser = window.TextureBrowser;
                var duringEnrichment = !Discovery().EnrichmentTask.IsCompleted;
                await browser.SelectResourceAsync(new(initial.IndexContentId, plan.PixelCases[0].Ordinal));
                await browser.SelectResourceAsync(new(initial.IndexContentId, plan.PixelCases[1].Ordinal));
                var cancel = VisualChildren<Button>(browser).Single(button => Equals(button.Content, "Cancel"));
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Discovery().EnrichmentTask;
                var cancelledCatalogs = (TextureCatalogSession?)typeof(AudioWindow).GetField("textureCatalogs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);
                if (cancelledCatalogs is not null)
                { try { await cancelledCatalogs.EnrichmentTask; } catch (OperationCanceledException) { } }
                await WaitAsync(() => browser.NativeImageBrowser.CurrentSource is null && browser.NativeImageBrowser.ImageBrowser.CurrentSurface is null,
                    "Cancel retained old native pixels.");
                Invoke(window, "Home_Click", window, new RoutedEventArgs());
                await WaitAsync(() => browser.IsSuspended, "Home did not suspend texture work.");
                await window.ShowTexturesAsync();
                lifecycleReports.Add(new { DuringEnrichment = duringEnrichment, CancelClearedPixels = true, HomeSuspended = true });
            });
            await Stage("DiscoveryReady", () => Discovery().EnrichmentTask);
            var snapshot = window.CurrentSession!.Snapshot; var view = window.TextureBrowser;
            await view.RefreshAsync();
            await File.WriteAllTextAsync(Path.Combine(output, "coverage.json"), JsonSerializer.Serialize(Discovery().Coverage, new JsonSerializerOptions { WriteIndented = true }));
            Capture(window, Path.Combine(output, "textures-overview.png"));
            foreach (var item in plan.NamedCases)
            {
                await Stage("Named/" + item.Ordinal, async () =>
                {
                    var identity = new ResourceIdentity(snapshot.IndexContentId, item.Ordinal);
                    await view.ApplyFilterAsync(Filter(item.OriginalPath));
                    await WaitAsync(() => !view.IsQueryPending && view.CurrentResources.Any(card => card.Identity == identity), "Full original DDS-path search did not return its texture.");
                    await view.SelectResourceAsync(identity);
                    await WaitAsync(() => ((TextBlock)view.FindName("PreviewStatus")).Text != "Opening image…", "The named selection did not settle.");
                    var entry = view.SelectedEntry ?? throw new InvalidOperationException("The named selection has no details.");
                    Require(entry.ResourcePath == item.OriginalPath && entry.NameKind == TextureNameKind.OriginalPath, "The stored path was not retained as original-path evidence.");
                    Require(entry.Owners.Any(owner => owner.Verified && owner.Label == item.OwnerLabel), "The exact source owner label is absent.");
                    Require(entry.Owners.SelectMany(owner => owner.Evidence).All(evidence => evidence.Revision == view.LoadedRevision), "Owner evidence crossed source revisions.");
                    Require(entry.Related.Any(link => link.Verified && link.Kind == TextureRelationshipKind.Container && link.Identity.Ordinal == item.ContainerOrdinal), "The named texture lost its exact container link.");
                    Require(entry.ContextKey is not null, "The uniquely named context has no filter key.");
                    await view.ApplyFilterAsync(Filter(context: entry.ContextKey));
                    await WaitAsync(() => !view.IsQueryPending && view.CurrentResources.Any(card => card.Identity == identity), "The context filter excluded its bound texture.");
                    await view.SelectResourceAsync(identity);
                    await WaitAsync(() => ((TextBlock)view.FindName("PreviewStatus")).Text != "Opening image…", "The context selection did not settle.");
                    Require(view.NativeImageBrowser.CurrentSource is null && view.NativeImageBrowser.ImageBrowser.CurrentSurface is null,
                        "The unsupported named example unexpectedly reused a decoded image.");
                    ((TabControl)view.FindName("DetailTabs")).SelectedIndex = 1; window.UpdateLayout();
                    Capture(window, Path.Combine(output, $"named-{item.Ordinal}-owners.png"));
                    ((TabControl)view.FindName("DetailTabs")).SelectedIndex = 3; window.UpdateLayout();
                    Capture(window, Path.Combine(output, $"named-{item.Ordinal}-details.png"));
                    await VerifyOriginal(view.SelectedEntry!);
                    var filters = Filter();
                    filters["width"] = entry.Width!.Value.ToString(CultureInfo.InvariantCulture);
                    filters["height"] = entry.Height!.Value.ToString(CultureInfo.InvariantCulture);
                    filters["format"] = entry.Format!; filters["role"] = entry.VerifiedRole!; filters["relationships"] = "1";
                    await view.ApplyFilterAsync(filters);
                    await WaitAsync(() => !view.IsQueryPending && view.CurrentResources.Any(card => card.Identity == identity), "Combined dimensions/format/role/owners filters excluded the known named texture.");
                    Require(view.CurrentResources.All(card => card.Entry.Width == entry.Width && card.Entry.Height == entry.Height && card.Entry.Format == entry.Format &&
                        card.Entry.Owners.Any(owner => owner.Verified && owner.Role == entry.VerifiedRole)), "Combined filters published a mismatched texture.");
                    await view.SelectResourceAsync(identity);
                    if (item == plan.NamedCases[0])
                    {
                        var beforeWindows = Application.Current.Windows.Cast<Window>().ToHashSet();
                        await window.NavigateTextureLinkAsync(entry.Owners.First(owner => owner.Verified && owner.CanNavigate));
                        var opened = Application.Current.Windows.Cast<Window>().Where(child => !beforeWindows.Contains(child)).ToArray();
                        Require(opened.Length > 0, "Used by navigation did not open its source inspector.");
                        foreach (var child in opened) { child.UpdateLayout(); Capture(child, Path.Combine(output, "used-by-source.png")); child.Close(); }
                        beforeWindows = Application.Current.Windows.Cast<Window>().ToHashSet();
                        await window.NavigateTextureLinkAsync(entry.Related.First(link => link.Verified && link.CanNavigate && link.Kind == TextureRelationshipKind.Container));
                        opened = Application.Current.Windows.Cast<Window>().Where(child => !beforeWindows.Contains(child)).ToArray();
                        Require(opened.Length > 0, "Related container navigation did not open its inspector.");
                        foreach (var child in opened) { child.UpdateLayout(); Capture(child, Path.Combine(output, "related-source.png")); child.Close(); }
                    }
                    namedReports.Add(new { entry.Identity, entry.Label, entry.ResourcePath, entry.NameKind, entry.ContextKey, entry.Owners, entry.Related,
                        entry.NameCandidates, entry.ExportStem, PreviewStatus = ((TextBlock)view.FindName("PreviewStatus")).Text, ExactOriginal = true });
                });
            }
            foreach (var item in plan.PixelCases)
            {
                await Stage("Pixels/" + item.Ordinal, async () =>
                {
                    var identity = new ResourceIdentity(snapshot.IndexContentId, item.Ordinal);
                    await view.ApplyFilterAsync(Filter()); await SelectPixel(identity);
                    var entry = view.SelectedEntry!; await VerifyOriginal(entry);
                    var native = view.NativeImageBrowser; var image = native.ImageBrowser;
                    var surfaces = new List<object>();
                    foreach (var mip in item.Mips)
                    {
                        ((ComboBox)image.FindName("Mips")).SelectedIndex = mip;
                        ((ComboBox)image.FindName("Channels")).SelectedIndex = 0;
                        await image.WaitForRenderAsync();
                        var surface = image.CurrentSurface ?? throw new InvalidOperationException("The requested mip has no source surface.");
                        Require(surface.MipIndex == mip, "The preview selected a different mip.");
                        var stem = native.ExportStemProvider?.Invoke(native.CurrentSource!) ?? throw new InvalidOperationException("The native export has no naming provider.");
                        Require(stem == view.SelectedEntry!.ExportStem, "PNG uses a stale naming stem.");
                        var pngPath = Path.Combine(exportRoot, stem + $"-mip{mip}.png");
                        var report = await native.ExportPngToAsync(pngPath, true);
                        Require(report.ReportError is null && report.ReportRelativePath is not null, "The PNG source report is unavailable.");
                        await VerifyNativePngAsync(pngPath, surface);
                        var rgbaPath = Path.Combine(output, $"pixels-{identity.Ordinal}-mip{mip}.rgba");
                        await File.WriteAllBytesAsync(rgbaPath, surface.Rgba8.ToArray());
                        using var sidecar = JsonDocument.Parse(await File.ReadAllTextAsync(pngPath + ".json"));
                        Require(sidecar.RootElement.GetProperty("SelectedIdentity").GetProperty("Ordinal").GetInt64() == identity.Ordinal,
                            "The PNG source report names a different selected texture.");
                        Require(sidecar.RootElement.GetProperty("SelectedSubresource").GetProperty("MipIndex").GetInt32() == mip,
                            "The PNG source report names a different selected mip.");
                        Require(sidecar.RootElement.GetProperty("Naming").GetProperty("Label").GetString() == view.SelectedEntry.Label,
                            "The PNG source report has a stale naming label.");
                        Require(!(await File.ReadAllTextAsync(pngPath + ".json")).Contains(snapshot.SourceRoot, StringComparison.OrdinalIgnoreCase),
                            "The PNG source report contains the local archive path.");
                        surfaces.Add(new { surface.MipIndex, surface.Width, surface.Height, Pixels = (long)surface.Width * surface.Height,
                            RgbaSha256 = Convert.ToHexString(SHA256.HashData(surface.Rgba8.Span)).ToLowerInvariant(), RgbaFile = rgbaPath, PngFile = pngPath, EveryPngPixelExact = true });
                    }
                    ((ComboBox)image.FindName("Channels")).SelectedIndex = 1; await image.WaitForRenderAsync();
                    ((TabControl)view.FindName("DetailTabs")).SelectedIndex = 0; window.UpdateLayout();
                    await WaitAsync(() => VisualChildren<Image>(view).Any(thumbnail => thumbnail.Name == "NativeThumbnail" && thumbnail.Source is BitmapSource &&
                        thumbnail.DataContext is TextureExplorerCard card && card.Identity == identity), "The real selected card has no decoded thumbnail.");
                    Capture(window, Path.Combine(output, $"pixels-{item.Ordinal}.png"));
                    var source = native.CurrentSource!;
                    pixelReports.Add(new { entry.Identity, entry.Label, entry.ExportStem,
                        Source = new { source.SelectedIdentity, source.Revision, MetadataIdentity = source.Metadata.Identity, source.MetadataSha256,
                            Container = new { source.Container.Identity, source.Container.Sha256, source.Container.Extent },
                            Payloads = source.Payloads.Select(payload => new { payload.Identity, payload.Type, payload.Id, payload.Extent, payload.Sha256 }) }, Surfaces = surfaces });
                });
            }
            var aliasIdentity = new ResourceIdentity(snapshot.IndexContentId, plan.NamedCases[0].Ordinal);
            var aliasText = "Texture workflow alias " + aliasIdentity.Ordinal;
            await Stage("AliasSaveSearch", async () =>
            {
                await view.ApplyFilterAsync(Filter()); await view.SelectResourceAsync(aliasIdentity);
                ((TextBox)view.FindName("Alias")).Text = aliasText;
                ((Button)view.FindName("SaveAlias")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitAsync(() => aliases.Get(aliasIdentity) == aliasText && ((TextBlock)view.FindName("AliasStatus")).Text == "Saved", "The alias did not save through the actual control.");
                await view.ApplyFilterAsync(Filter(aliasText));
                await WaitAsync(() => !view.IsQueryPending && view.CurrentResources.Any(card => card.Identity == aliasIdentity), "Alias search did not find the texture.");
                Capture(window, Path.Combine(output, "alias-search.png"));
            });
            await Stage("AliasRenameAndIdentityIsolation", async () =>
            {
                await view.SelectResourceAsync(aliasIdentity);
                var previous = aliasText; aliasText += " renamed";
                ((TextBox)view.FindName("Alias")).Text = aliasText;
                ((Button)view.FindName("SaveAlias")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitAsync(() => aliases.Get(aliasIdentity) == aliasText && ((TextBlock)view.FindName("AliasStatus")).Text == "Saved", "The alias did not rename.");
                Require(new AliasStore(aliases.LibraryRoot).Get(aliasIdentity) == aliasText, "Restarted library did not retain the alias rename.");
                Require(new AliasStore(aliases.LibraryRoot).Get(aliasIdentity with { IndexContentId = new string('f', 64) }) is null, "The alias crossed index identity.");
                var sameId = snapshot.Records.FirstOrDefault(row => row.Id == view.SelectedEntry!.ResourceId && row.Ordinal != aliasIdentity.Ordinal && row.Type is 2229 or 2230);
                if (sameId is not null) Require(aliases.Get(new(snapshot.IndexContentId, sameId.Ordinal)) is null, "The alias crossed same-ID resource identity.");
                await view.ApplyFilterAsync(Filter(aliasText));
                await WaitAsync(() => !view.IsQueryPending && view.CurrentResources.Any(card => card.Identity == aliasIdentity), "Renamed alias search did not settle.");
                lifecycleReports.Add(new { AliasRenameSurvivedLibraryRestart = true, SameIdIsolationChecked = sameId is not null, OriginalAlias = previous, RenamedAlias = aliasText });
            });
            var aliasBytes = await File.ReadAllBytesAsync(Path.Combine(aliases.LibraryRoot, "aliases.json"));
            await Stage("CacheClearDrain", () => cache.ClearAsync(token => InvokeTaskAsync(window, "DrainCacheUsersAsync", token)));
            Require(cache.ActivePreviewLeases == 0, "Cache clearing retained a preview lease.");
            var clearedAliasBytes = await File.ReadAllBytesAsync(Path.Combine(aliases.LibraryRoot, "aliases.json"));
            Require(aliasBytes.AsSpan().SequenceEqual(clearedAliasBytes), "Cache clearing modified aliases.");
            await Stage("CacheReopen", async () =>
            {
                await InvokeTaskAsync(window, "ReopenAfterCacheChangeAsync", CancellationToken.None);
                Require(window.CurrentSession is not null, "Cache reopen returned without an archive session: " + ((TextBlock)window.FindName("StatusText")).Text);
                await window.ShowTexturesAsync(); await Discovery().EnrichmentTask;
                var storedAlias = new AliasStore(aliases.LibraryRoot).Get(aliasIdentity);
                await view.ApplyFilterAsync(Filter(aliasText));
                await WaitAsync(() => !view.IsQueryPending && view.CurrentResources.Any(card => card.Identity == aliasIdentity), "The durable alias row did not publish after cache reopen.");
                var backendPage = await Discovery().QueryAsync(new(Search: aliasText, Limit: 100));
                await File.WriteAllTextAsync(Path.Combine(output, "cache-reopen-debug.json"), JsonSerializer.Serialize(new
                { StoredAlias = storedAlias, BackendTotal = backendPage.Total, BackendIdentities = backendPage.Entries.Select(entry => entry.Identity),
                    HostStatus = ((TextBlock)window.FindName("StatusText")).Text, Coverage = Discovery().Coverage }, new JsonSerializerOptions { WriteIndented = true }));
                Require(storedAlias == aliasText && backendPage.Entries.Any(entry => entry.Identity == aliasIdentity), "The durable alias is missing from the reopened library or backend.");
                await view.SelectResourceAsync(aliasIdentity);
                Require(view.SelectedEntry?.ResourcePath == plan.NamedCases[0].OriginalPath, "Alias persistence replaced recovered path evidence.");
                Capture(window, Path.Combine(output, "cache-reopened-alias.png"));
            });
            await Stage("CacheRelocationAliasPersistence", async () =>
            {
                await cache.RelocateAsync(Path.Combine(output, "relocated-cache"), token => InvokeTaskAsync(window, "DrainCacheUsersAsync", token));
                Require(new AliasStore(aliases.LibraryRoot).Get(aliasIdentity) == aliasText, "Cache relocation changed the durable alias.");
                await InvokeTaskAsync(window, "ReopenAfterCacheChangeAsync", CancellationToken.None);
                Require(window.CurrentSession is not null, "Cache relocation reopen returned without an archive session.");
                await window.ShowTexturesAsync(); await Discovery().EnrichmentTask;
                await view.ApplyFilterAsync(Filter(aliasText));
                await WaitAsync(() => !view.IsQueryPending && view.CurrentResources.Any(card => card.Identity == aliasIdentity), "Relocated-cache alias search did not publish.");
            });
            await Stage("AliasRemove", async () =>
            {
                await view.SelectResourceAsync(aliasIdentity);
                ((Button)view.FindName("ClearAlias")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitAsync(() => aliases.Get(aliasIdentity) is null, "The alias did not clear.");
                await view.ApplyFilterAsync(Filter(aliasText)); await WaitAsync(() => !view.IsQueryPending && view.TotalMatches == 0, "The removed alias remains searchable.");
            });
            await Stage("RapidSelectionAndHome", async () =>
            {
                await view.ApplyFilterAsync(Filter());
                var first = new ResourceIdentity(snapshot.IndexContentId, plan.PixelCases[0].Ordinal);
                var last = new ResourceIdentity(snapshot.IndexContentId, plan.PixelCases[1].Ordinal);
                await view.SelectResourceAsync(first); await view.SelectResourceAsync(last); await SelectPixel(last);
                Require(view.SelectedIdentity == last && view.NativeImageBrowser.CurrentSource?.SelectedIdentity == last, "Rapid selection published the previous texture.");
                Invoke(window, "Home_Click", window, new RoutedEventArgs());
                await WaitAsync(() => view.IsSuspended && view.NativeImageBrowser.CurrentSource is null, "Home retained native preview state.");
                await window.ShowTexturesAsync(); await SelectPixel(first);
                lifecycleReports.Add(new { RapidSelectionLastIdentity = last, HomeReleasedPixels = true, ReentryRestoredPixels = true });
            });
            await view.ApplyFilterAsync(Filter()); await SelectPixel(new(snapshot.IndexContentId, plan.PixelCases[0].Ordinal));
            Capture(window, Path.Combine(output, "final-texture-browser.png"));
            var coverage = Discovery().Coverage;
            object? sourceChange = null;
            if (options.TryGetValue("stale-source-plan", out var sourceChangePath))
            {
                await Stage("StaleSupportingSource", async () =>
                {
                    var change = JsonSerializer.Deserialize<DiscoverySourceChangePlan>(await File.ReadAllTextAsync(sourceChangePath))
                        ?? throw new ArgumentException("The source-change plan is empty.");
                    var fixtureRoot = Path.GetDirectoryName(Path.GetFullPath(change.IndexPath))!;
                    Require(DirectoryBoundary.IsWithin(fixtureRoot, output), "Source-change checks modify only a private fixture inside --out.");
                    await window.OpenArchiveAsync(change.IndexPath);
                    var fixture = window.CurrentSession?.Snapshot ?? throw new InvalidOperationException("The private source-change fixture did not open.");
                    window.TextureDiscoveryOptions = new(1, [new(fixture.IndexContentId, change.SupportingOrdinal)]);
                    window.TextureCatalogOptions = new(); await window.ShowTexturesAsync(); await Discovery().EnrichmentTask;
                    var target = new ResourceIdentity(fixture.IndexContentId, change.TextureOrdinal);
                    await view.SelectResourceAsync(target);
                    Require(view.SelectedEntry?.NameCandidates.Count > 0, "The private fixture did not recover source name evidence before mutation.");
                    await view.SuspendAsync();
                    var support = fixture.Records.Single(row => row.Ordinal == change.SupportingOrdinal);
                    var volume = fixture.Volumes.Single(item => item.Package == support.Package);
                    Require(DirectoryBoundary.IsWithin(volume.FilePath, fixtureRoot), "The mutation volume is outside the private fixture.");
                    Require(change.RelativeByteOffset >= 0 && change.RelativeByteOffset < support.StoredSize, "The source mutation is outside the supporting record.");
                    var stamp = File.GetLastWriteTimeUtc(volume.FilePath); var size = new FileInfo(volume.FilePath).Length;
                    using (var stream = new FileStream(volume.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                    {
                        stream.Position = support.Offset + change.RelativeByteOffset;
                        var previousByte = stream.ReadByte(); Require(previousByte >= 0, "The mutation position is unavailable.");
                        stream.Position--; stream.WriteByte((byte)(previousByte ^ 1)); stream.Flush(true);
                    }
                    File.SetLastWriteTimeUtc(volume.FilePath, stamp);
                    Require(new FileInfo(volume.FilePath).Length == size && File.GetLastWriteTimeUtc(volume.FilePath) == stamp,
                        "The source-change fixture did not retain size/time.");
                    var destination = Path.Combine(exportRoot, "stale-support-original.bin");
                    var sentinel = new byte[] { 11, 22, 33, 44 }; await File.WriteAllBytesAsync(destination, sentinel);
                    var rejected = false;
                    try { await window.ExportTextureOriginalAsync(destination, true); }
                    catch (ArchiveException error) when (error.Code == "SourceChanged") { rejected = true; }
                    Require(rejected, "Changed supporting name evidence was accepted for export.");
                    Require((await File.ReadAllBytesAsync(destination)).AsSpan().SequenceEqual(sentinel), "Stale source rejection replaced the existing output.");
                    sourceChange = new { SupportingSourceChangedAtSameSizeTime = true, RejectedAsSourceChanged = true, ExistingOutputPreserved = true };
                });
            }
            return new { Classification = "Programmatic WPF texture discovery workflow", Status = "Completed", SourceIndexContentId = snapshot.IndexContentId,
                Coverage = coverage, NamedContexts = namedReports, PixelTextures = pixelReports, Stages = stages, StageMilliseconds = timings,
                Lifecycle = lifecycleReports, SourceChange = sourceChange,
                AliasSurvivedCacheClearAndReopen = true, RemovedAliasNoLongerSearchable = true,
                AudioEnrichmentRemainedDeferred = window.CurrentSession is { IsEnrichmentStarted: false }, NoGameLaunch = true,
                LoadedAssemblies = VerifyPackageAssemblies(options.GetValueOrDefault("package")) };
        }
        catch (Exception error)
        {
            window.UpdateLayout(); Capture(window, Path.Combine(output, "workflow-failed.png"));
            await File.WriteAllTextAsync(Path.Combine(output, "discovery-partial.json"), JsonSerializer.Serialize(new
            { Classification = "Programmatic WPF texture discovery workflow", Status = "Failed", ActiveStage = activeStage, Error = error.ToString(),
                HostStatus = ((TextBlock)window.FindName("StatusText")).Text, SessionPresent = window.CurrentSession is not null,
                BrowserFilter = window.TextureBrowser.CaptureFilter(), BrowserTotal = window.TextureBrowser.TotalMatches,
                window.TextureBrowser.IsQueryPending, window.TextureBrowser.PublishedQueryVersion,
                BrowserRows = window.TextureBrowser.CurrentResources.Select(card => new { card.Identity, card.Entry.Label, card.Entry.Alias }),
                NamedContexts = namedReports, PixelTextures = pixelReports, Stages = stages, StageMilliseconds = timings }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
    }
}
