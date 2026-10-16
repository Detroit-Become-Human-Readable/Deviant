using System.IO;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class TextureExplorerViewTests
{
    [Fact]
    public void SavedFilterWaitsForTheOptionsReadThatSupersededItsOwn() => Run((view, catalog) =>
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var superseded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        catalog.FilterQuery = () =>
        {
            bool first = Interlocked.Increment(ref calls) == 1;
            (first ? firstEntered : secondEntered).TrySetResult();
            (first ? releaseFirst : releaseSecond).Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return new TextureDiscoveryFilterOptions([new(4, 2)], ["BGRA8"], ["Diffuse"], [])
            { Catalogs = [new("generated-catalog", "Generated catalog")] };
        };
        using var observer = AssetLoadDiagnostics.Listener.Subscribe(new OptionsCounterObserver(counter =>
        {
            if (counter.Stage == "TextureBrowser.Ui.OptionsSuperseded" && counter.Key == catalog.Revision.IndexContentId)
                superseded.TrySetResult();
        }));
        var applying = view.ApplyFilterAsync(new Dictionary<string, string> { ["group"] = "generated-catalog" });
        Task newer = Task.CompletedTask;
        bool completedBeforeReplacement;
        try
        {
            Pump(firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            newer = (Task)Invoke(view, "RefreshOptionsAsync", CancellationToken.None)!;
            Pump(secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            releaseFirst.TrySetResult();
            Pump(superseded.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            completedBeforeReplacement = applying.IsCompleted;
        }
        finally { releaseFirst.TrySetResult(); releaseSecond.TrySetResult(); }
        var replacementError = Record.Exception(() => Pump(newer));
        var error = Record.Exception(() => Pump(applying));
        Assert.False(completedBeforeReplacement, "The saved filter completed before its replacement options were available.");
        Assert.Null(replacementError);
        Assert.Null(error);
        Assert.Equal("generated-catalog", view.CaptureFilter()["group"]);
    });

    private sealed class OptionsCounterObserver(Action<AssetLoadCounterMeasurement> observe)
        : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> item)
        { if (item.Value is AssetLoadCounterMeasurement counter) observe(counter); }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    [Fact]
    public void OlderOptionReadCannotOverwriteNewerChoices() => Run((view, catalog) =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        catalog.FilterQuery = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                return new([new(4, 2)], ["BC1"], ["Diffuse"], []);
            }
            return new([new(4, 2)], ["BC3"], ["Diffuse"], []);
        };
        var old = (Task)Invoke(view, "RefreshOptionsAsync", CancellationToken.None)!;
        try
        {
            Pump(entered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Pump((Task)Invoke(view, "RefreshOptionsAsync", CancellationToken.None)!);
        }
        finally { release.TrySetResult(); }
        Pump(old);
        var formats = ((ComboBox)view.FindName("Formats")).Items.Cast<string>().ToArray();
        Assert.Contains("BC3", formats);
        Assert.DoesNotContain("BC1", formats);
    });

    [Fact]
    public void OlderOptionsCannotClearTheRequestedCatalog() => Run((view, catalog) =>
    {
        const string key = "generated-catalog";
        TextureDiscoveryQuery? requested = null;
        catalog.QueryOverride = (query, token) =>
        { requested = query; return Task.FromResult(catalog.Page(query, token)); };
        Pump(view.ApplyFilterAsync(new Dictionary<string, string> { ["group"] = key, ["kind"] = "2" }));
        Assert.Equal(key, requested!.CatalogKey);
        catalog.FilterQuery = () => new([new(4, 2)], ["BGRA8"], ["Diffuse"], []);
        Pump((Task)Invoke(view, "RefreshOptionsAsync", CancellationToken.None)!);
        Assert.Equal(key, view.CaptureFilter()["group"]);
        Pump(view.RefreshAsync());
        Assert.Equal(key, requested!.CatalogKey);
        ((ComboBox)view.FindName("Catalogs")).SelectedIndex = 0;
        Assert.Equal("", view.CaptureFilter()["group"]);
        Pump(view.RefreshAsync());
        Assert.Null(requested!.CatalogKey);
    }, beforeLoad: (_, catalog) => catalog.FilterQuery = () =>
        new TextureDiscoveryFilterOptions([new(4, 2)], ["BGRA8"], ["Diffuse"], [])
        { Catalogs = [new("generated-catalog", "Generated catalog")] });

    [Fact]
    public void UnavailableSavedCatalogKeepsItsRequestedKey() => Run((view, _) =>
    {
        var error = Record.Exception(() => Pump(view.ApplyFilterAsync(
            new Dictionary<string, string> { ["group"] = "missing-generated-catalog" })));
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("missing-generated-catalog", view.CaptureFilter()["group"]);
    });

    [Fact]
    public void InitialPagePublishesBeforeFilterOptionsAreRead() => Run((view, catalog) =>
    {
        Assert.NotEmpty(view.CurrentResources);
    }, beforeLoad: (view, catalog) => catalog.FilterQuery = () =>
    {
        view.Dispatcher.Invoke(() => Assert.NotEmpty(view.CurrentResources));
        return new([new(4, 2)], ["BGRA8"], ["Diffuse"], []);
    });

    [Fact]
    public void FilterQueriesRunOutsideTheDispatcher() => Run((view, catalog) =>
    {
        Pump(view.ApplyFilterAsync(new Dictionary<string, string>()));
        Assert.NotEmpty(view.CurrentResources);
    }, beforeLoad: (view, catalog) => catalog.FilterQuery = () =>
    {
        Assert.False(view.Dispatcher.CheckAccess());
        return new([new(4, 2)], ["BGRA8"], ["Diffuse"], []);
    });

    [Fact]
    public void MetadataListingDoesNotReadNativePixelsUntilThumbnailsAreEnabled()
    {
        NativeReadObserver? observer = null; IDisposable? subscription = null;
        try
        {
            Run((view, catalog) =>
            {
                var readsObserver = observer!;
                Assert.False(view.ThumbnailsEnabled);
                Assert.NotEmpty(view.CurrentResources);
                Assert.Empty(readsObserver.Reads);
                Pump(view.RefreshAsync());
                catalog.Replace(catalog.Entries[0] with { Label = "Updated metadata" });
                Pump(WaitUntilAsync(() => view.CurrentResources.Any(card => card.Label == "Updated metadata")));
                Assert.Empty(readsObserver.Reads);
                Assert.Null(view.NativeImageBrowser.CurrentSource);
                view.ThumbnailsEnabled = true;
                Pump(WaitUntilAsync(() => readsObserver.Reads.Count > 0));
                Pump(WaitUntilAsync(() => Field<Dictionary<ResourceIdentity, BitmapSource>>(view, "nativeThumbnails").Count > 0));
                view.ThumbnailsEnabled = false;
                Pump(WaitUntilAsync(() => Field<HashSet<Task>>(view, "operations").Count == 0));
                Assert.Empty(Field<Dictionary<ResourceIdentity, BitmapSource>>(view, "nativeThumbnails"));
                var reads = readsObserver.Reads.Count;
                Pump(view.RefreshAsync());
                Assert.Equal(reads, readsObserver.Reads.Count);
            }, beforeLoad: (_, catalog) =>
            {
                observer = new(catalog.Entries.Select(entry => entry.Identity.Key).ToHashSet(StringComparer.Ordinal));
                subscription = AssetLoadDiagnostics.Listener.Subscribe(observer, name => name == AssetLoadDiagnostics.MeasurementName);
            });
        }
        finally { subscription?.Dispose(); }
    }

    [Fact]
    public void TurningThumbnailsOffDoesNotCancelAnAwaitingSelectedPreview() => Run((view, catalog) =>
    {
        var gate = Field<SemaphoreSlim>(view, "nativeGate");
        Assert.True(gate.Wait(0));
        try
        {
            view.ThumbnailsEnabled = true;
            Pump(view.SelectResourceAsync(catalog.Entries[0].Identity));
            Assert.Equal(TexturePreviewState.Loading, view.CurrentPreviewState);
            view.ThumbnailsEnabled = false;
        }
        finally { gate.Release(); }
        Pump(WaitUntilAsync(() => view.CurrentPreviewState == TexturePreviewState.Ready));
        Assert.Equal(catalog.Entries[0].Identity, view.NativeImageBrowser.CurrentSource!.SelectedIdentity);
        Assert.Empty(Field<Dictionary<ResourceIdentity, BitmapSource>>(view, "nativeThumbnails"));
    });

    [Fact]
    public void NameUpdatesKeepValidThumbnailsWithoutNativePayloadReloads()
    {
        NativeReadObserver? observer = null; IDisposable? subscription = null;
        try
        {
            Run((view, catalog) =>
            {
                var readsObserver = observer!;
                view.ThumbnailsEnabled = true;
                Pump(WaitUntilAsync(() => Field<Dictionary<ResourceIdentity, BitmapSource>>(view, "nativeThumbnails").Count == 3));
                Pump(WaitUntilAsync(() => Field<HashSet<Task>>(view, "operations").Count == 0));
                var thumbs = Field<Dictionary<ResourceIdentity, BitmapSource>>(view, "nativeThumbnails");
                var first = catalog.Entries[0]; var bitmap = thumbs[first.Identity]; var reads = readsObserver.Reads.Count;
                catalog.Replace(first with { Label = "Renamed metadata" });
                Pump(WaitUntilAsync(() => view.CurrentResources.Any(card => card.Label == "Renamed metadata")));
                Assert.Same(bitmap, thumbs[first.Identity]);
                Assert.Equal(reads, readsObserver.Reads.Count);
            }, beforeLoad: (_, catalog) =>
            {
                observer = new(catalog.Entries.Select(entry => entry.Identity.Key).ToHashSet(StringComparer.Ordinal));
                subscription = AssetLoadDiagnostics.Listener.Subscribe(observer, name => name == AssetLoadDiagnostics.MeasurementName);
            });
        }
        finally { subscription?.Dispose(); }
    }

    [Fact]
    public void DefaultScopeAndResetKeepUnnamedImagesAheadOfLightingNames() => Run((view, catalog) =>
    {
        TextureDiscoveryQuery? captured = null;
        catalog.QueryOverride = (query, token) => { captured = query; return Task.FromResult(catalog.Page(query, token)); };
        Pump(view.RefreshAsync());
        Assert.Equal(TextureKindFilter.Images, captured!.Kind);
        Assert.False(captured.SortByName);
        Assert.False(captured.IncludeLodBuffers);
        Pump(view.ApplyFilterAsync(new Dictionary<string, string> { ["kind"] = "1", ["sort"] = "0" }));
        Assert.Equal(TextureKindFilter.Lighting, captured!.Kind);
        Assert.True(captured.NamedFirst);
        Assert.Equal("1", view.CaptureFilter()["kind"]);
        Pump(view.ApplyFilterAsync(new Dictionary<string, string> { ["kind"] = "2" }));
        Assert.Equal(TextureKindFilter.All, captured!.Kind);
        Assert.True(captured.IncludeLodBuffers);
        Invoke(view, "Reset_Click", view, new RoutedEventArgs());
        Pump(WaitUntilAsync(() => captured!.Kind == TextureKindFilter.Images && !captured.SortByName));
    });

    [Fact]
    public void SelectingACardRemovesItsOldThumbnailUntilTheDetailSourceIsReady() => Run((view, catalog) =>
    {
        Pump(WaitUntilAsync(() => Field<HashSet<Task>>(view, "operations").Count == 0));
        var identity = catalog.Entries[0].Identity;
        var thumbs = Field<Dictionary<ResourceIdentity, BitmapSource>>(view, "nativeThumbnails");
        thumbs[identity] = BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        var gate = Field<SemaphoreSlim>(view, "nativeGate");
        Assert.True(gate.Wait(0));
        try
        {
            Pump(view.SelectResourceAsync(identity));
            Assert.False(thumbs.ContainsKey(identity));
            Assert.Equal(TexturePreviewState.Loading, view.CurrentPreviewState);
        }
        finally { gate.Release(); }
        Pump(WaitUntilAsync(() => view.CurrentPreviewState == TexturePreviewState.Ready));
        view.CancelWork();
        Assert.Equal(TexturePreviewState.Cancelled, view.CurrentPreviewState);
        Assert.Empty(thumbs);
    });

    [Fact]
    public void FailedConversionDoesNotRepublishAnOldThumbnailForTheSelectedCard() => Run((view, catalog) =>
    {
        var identity = catalog.Entries[0].Identity;
        var thumbs = Field<Dictionary<ResourceIdentity, BitmapSource>>(view, "nativeThumbnails");
        thumbs[identity] = BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        Pump(view.SelectResourceAsync(identity));
        Pump(WaitUntilAsync(() => view.CurrentPreviewState == TexturePreviewState.Failed));
        Assert.Equal("UnsupportedNativeTextureConversion", view.PreviewFailure!.Code);
        Assert.False(thumbs.ContainsKey(identity));
        Assert.Null(view.NativeImageBrowser.CurrentSource);
    }, (source, token) => throw new TextureFormatException("UnsupportedNativeTextureConversion", "This texture conversion is unavailable."));

    [Theory]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef_Room_Specular.dds", "Room_Specular.dds")]
    [InlineData("0123456789abcdef0123456789abcdef-Room.dds", "Room.dds")]
    [InlineData("{01234567-89ab-cdef-0123-456789abcdef}_Room.dds", "Room.dds")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdefRoom.dds", "01234567-89ab-cdef-0123-456789abcdefRoom.dds")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdeg_Room.dds", "01234567-89ab-cdef-0123-456789abcdeg_Room.dds")]
    [InlineData("Room_Specular.dds", "Room_Specular.dds")]
    public void CardProjectionElidesOnlyAValidLeadingGuidAndSeparator(string original, string display)
        => Assert.Equal(display, TextureExplorerCard.DisplayName(original));

    [Fact]
    public void NamedCardsExposeReadableSourceTailContextAndFullPathWhileAliasesStayLiteral() => Run((view, catalog) =>
    {
        var original = "01234567-89ab-cdef-0123-456789abcdef_Room_Specular.dds";
        var entry = catalog.Entries[0] with
        {
            Label = original, ResourceName = original, ResourcePath = "lighting/interior/" + original,
            ContextLabel = "Interior room ceiling reflection", VerifiedRole = "Lighting", NameKind = TextureNameKind.OriginalPath
        };
        var card = new TextureExplorerCard(entry);
        Assert.Equal("Room_Specular.dds", card.Label); Assert.Equal(entry.ContextLabel, card.Context);
        Assert.Equal(entry.ResourcePath, card.SourceTooltip); Assert.Equal(original, card.Entry.Label);
        Assert.Equal(entry.ExportStem, card.Entry.ExportStem);
        var alias = "01234567-89ab-cdef-0123-456789abcdef_User alias";
        var aliased = new TextureExplorerCard(entry with { Alias = alias });
        Assert.Equal(alias, aliased.Label); Assert.Equal("Room_Specular.dds", aliased.RecoveredName); Assert.Equal("Alias", aliased.Inferred);
        catalog.Replace(entry); Pump(view.SelectResourceAsync(entry.Identity));
        Assert.Equal("Room_Specular.dds", ((TextBlock)view.FindName("SelectedName")).Text);
        Pump(view.RefreshAsync());
        Assert.Equal("Room_Specular.dds", view.CurrentResources.Single(item => item.Identity == entry.Identity).Label);
    });

    [Fact]
    public void BackgroundMetadataUpdatesDoNotStarveAnActiveQuery() => Run((view, catalog) =>
    {
        var updated = catalog.Entries[0] with { Label = "Loaded surface" };
        catalog.QueryOverride = async (query, token) =>
        {
            await Task.Delay(600, token);
            return catalog.Page(query, token);
        };
        var updates = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        updates.Tick += (_, _) => catalog.Replace(updated);
        try
        {
            updates.Start(); catalog.Replace(updated);
            Pump(WaitUntilAsync(() => view.CurrentResources.Any(card => card.Label == "Loaded surface")));
            Assert.True(updates.IsEnabled);
        }
        finally { updates.Stop(); }
    });

    [Fact]
    public void FallbackCardsShowIdentityWithoutMissingMetadataLabels() => Run((_, catalog) =>
    {
        var entry = catalog.Entries[0] with
        {
            Label = "0x0000002A · ? x ? · Unknown format · r10", ResourceId = 42,
            NameKind = TextureNameKind.Fallback, ResourceName = null, ResourcePath = null,
            Width = null, Height = null, Format = null, ContextLabel = null, Owners = [], VerifiedRole = null
        };
        var card = new TextureExplorerCard(entry);
        Assert.Equal("Texture 0000002A", card.Label);
        Assert.Equal("", card.SizeFormat); Assert.Equal("", card.Context);
        Assert.DoesNotContain("Unknown", card.SourceTooltip);
        Assert.Equal("128 × 64 · BC1", new TextureExplorerCard(entry with { Width = 128, Height = 64, Format = "BC1" }).SizeFormat);
    });

    [Fact]
    public void SearchConvergesToItsCurrentFilterAfterAnUpdateDuringTheFirstQuery() => Run((view, catalog) =>
    {
        var first = catalog.Entries[0] with { Alias = "Distinct alias" }; catalog.Replace(first);
        var calls = 0;
        catalog.QueryOverride = async (query, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) await Task.Delay(500, token);
            return catalog.Page(query, token);
        };
        var query = view.ApplyFilterAsync(new Dictionary<string, string> { ["search"] = "Distinct alias" });
        Assert.False(query.IsCompleted);
        Pump(query.WaitAsync(TimeSpan.FromSeconds(5)));
        Pump(WaitUntilAsync(() => !view.IsQueryPending && view.TotalMatches == 1 && view.CurrentResources.SingleOrDefault()?.Identity == first.Identity));
        Assert.Equal("Distinct alias", view.CurrentResources.Single().Label); Assert.True(view.PublishedQueryVersion > 0); Assert.True(calls >= 2);
    });

    [Fact]
    public void CatalogUpdatesKeepSelectionAndAliasDraftAndResolvedLinksNavigate() => Run((view, catalog) =>
    {
        view.ThumbnailsEnabled = true;
        var first = catalog.Entries[0]; Pump(view.SelectResourceAsync(first.Identity));
        Pump(WaitUntilAsync(() => view.NativeImageBrowser.CurrentSource?.SelectedIdentity == first.Identity));
        Pump(WaitUntilAsync(() => Field<Dictionary<ResourceIdentity, BitmapSource>>(view, "nativeThumbnails").Count == 3));
        Pump(Task.Delay(50));
        CapturePreview(view);
        var alias = (TextBox)view.FindName("Alias"); alias.Text = "Draft label";
        catalog.Replace(first with { Label = "Updated surface" });
        Pump(WaitUntilAsync(() => view.CurrentResources.Any(card => card.Label == "Updated surface")));
        Assert.Equal(first.Identity, view.SelectedIdentity); Assert.Equal("Draft label", alias.Text);
        var selected = view.SelectedIdentity; var version = view.SelectionVersion;
        TextureDiscoveryLink? navigated = null; view.LinkSelected += (_, args) => navigated = args.Link;
        var rows = (ListBox)view.FindName("OwnerRows"); rows.SelectedItem = first.Owners[0];
        Invoke(view, "OpenOwner_Click", view, new RoutedEventArgs()); Assert.Equal(first.Owners[0], navigated);
        Assert.Single(rows.Items.Cast<TextureDiscoveryLink>());
        navigated = null; rows.SelectedItem = null;
        Invoke(view, "OpenOwner_Click", view, new RoutedEventArgs()); Assert.Null(navigated);
        ((Button)view.FindName("SaveAlias")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(WaitUntilAsync(() => view.SelectedEntry?.Alias == "Draft label"));
        Assert.Equal("Updated surface", view.SelectedEntry!.Label);
        Assert.Equal("Draft label", view.SelectedLabel);
        Assert.Equal(selected, view.SelectedIdentity); Assert.Equal(version, view.SelectionVersion);
        Pump(view.ApplyFilterAsync(new Dictionary<string, string> { ["search"] = "Draft label" }));
        Assert.Equal(first.Identity, Assert.Single(view.CurrentResources).Identity);
        var filter = view.CaptureFilter(); Assert.Equal("Draft label", filter["search"]);
        Pump(view.ApplyFilterAsync(new Dictionary<string, string> { ["width"] = "4", ["height"] = "2", ["format"] = "BGRA8", ["role"] = "Diffuse", ["relationships"] = "1" }));
        Assert.Equal(first.Identity, Assert.Single(view.CurrentResources).Identity);
        Assert.Contains("Original name", ((TextBlock)view.FindName("Provenance")).Text);
    });

    [Fact]
    public void RgbThumbnailsAndNativePreviewDrainBeforeResumeAndClear() => Run((view, catalog) =>
    {
        view.ThumbnailsEnabled = true;
        var first = catalog.Entries[0]; Pump(view.SelectResourceAsync(first.Identity));
        Pump(WaitUntilAsync(() => view.NativeImageBrowser.CurrentSource?.SelectedIdentity == first.Identity));
        var channels = (ComboBox)view.NativeImageBrowser.ImageBrowser.FindName("Channels"); Assert.Equal("RGB", channels.SelectedItem);
        var thumbs = Field<Dictionary<ResourceIdentity, BitmapSource>>(view, "nativeThumbnails");
        Pump(WaitUntilAsync(() => thumbs.ContainsKey(first.Identity)));
        var pixel = new byte[4]; new FormatConvertedBitmap(thumbs[first.Identity], System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        Assert.Equal(255, pixel[3]);
        Pump(view.SuspendAsync()); Assert.True(view.IsSuspended); Assert.Null(view.NativeImageBrowser.CurrentSource); Assert.Empty(thumbs);
        Assert.Empty(Field<HashSet<Task>>(view, "operations"));
        view.Resume(); Pump(WaitUntilAsync(() => view.NativeImageBrowser.CurrentSource?.SelectedIdentity == first.Identity));
        Invoke(view, "Cancel_Click", view, new RoutedEventArgs()); Pump(WaitUntilAsync(() => view.NativeImageBrowser.CurrentSource is null));
        Pump(view.SelectResourceAsync(catalog.Entries[1].Identity));
        Pump(WaitUntilAsync(() => view.NativeImageBrowser.CurrentSource?.SelectedIdentity == catalog.Entries[1].Identity));
        Pump(view.ClearAsync()); Assert.Null(view.SelectedIdentity); Assert.Null(view.LoadedRevision); Assert.Empty(view.CurrentResources);
        Assert.Empty(Field<HashSet<Task>>(view, "operations"));
    });

    [Fact]
    public void CancelAndNavigationCancelOriginalExportCallbacksAndDrain() => Run((view, catalog) =>
    {
        Pump(view.SelectResourceAsync(catalog.Entries[0].Identity));
        ResourceIdentity? exported = null;
        view.ExportOriginalAsync = async (entry, destination, overwrite, token) =>
        { exported = entry.Identity; await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        var pending = view.ExportToAsync("texture.bin");
        Assert.Equal(catalog.Entries[0].Identity, exported);
        Invoke(view, "Cancel_Click", view, new RoutedEventArgs());
        Pump(Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending));
        Pump(view.SuspendAsync()); Assert.Empty(Field<HashSet<Task>>(view, "operations"));
        view.Resume();
        var next = view.ExportToAsync("texture.bin");
        Pump(view.SuspendAsync());
        Pump(Assert.ThrowsAnyAsync<OperationCanceledException>(() => next));
        Assert.Empty(Field<HashSet<Task>>(view, "operations"));
    });

    private static void Run(Action<TextureExplorerView, TestCatalog> check,
        Func<NativeTextureSource, CancellationToken, DdsTextureDocument>? reader = null,
        Action<TextureExplorerView, TestCatalog>? beforeLoad = null) => WorkspaceCommitTests.Run((window, fixture) =>
    {
        NativeTextureFixture.WriteArchive(fixture.Index, zeroAlpha: true); Pump(window.OpenArchiveAsync(fixture.Index));
        var snapshot = window.CurrentSession!.Snapshot; var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        var catalogs = new TextureCatalogSession(snapshot, revision); var native = new NativeTextureSession(catalogs);
        var discovery = new TestCatalog(snapshot); var view = new TextureExplorerView();
        var host = new Window { Content = view, Width = 1200, Height = 800, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        host.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Deviant;component/ControlStyles.xaml") });
        host.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
        try
        {
            host.Show(); Pump(catalogs.EnrichmentTask);
            beforeLoad?.Invoke(view, discovery);
            Pump(view.LoadAsync(discovery, native, new CacheManager(Path.Combine(fixture.Root, "texture-settings"), new(fixture.Cache)), reader ?? ((source, token) => new NativeTextureDecoder().Read(source, token))));
            Pump(Task.Delay(50)); check(view, discovery);
        }
        finally
        {
            Pump(view.DisposeAsync().AsTask()); host.Close(); Pump(native.DisposeAsync().AsTask()); Pump(catalogs.DisposeAsync().AsTask());
        }
    });

    private sealed class NativeReadObserver(HashSet<string> keys) : IObserver<KeyValuePair<string, object?>>
    {
        public ConcurrentQueue<AssetLoadMeasurement> Reads { get; } = new();
        public void OnNext(KeyValuePair<string, object?> item)
        {
            if (item.Value is AssetLoadMeasurement { Stage: "ArchiveRead", Key: not null } read && keys.Contains(read.Key))
                Reads.Enqueue(read);
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private sealed class TestCatalog : ITextureDiscoveryCatalog
    {
        public TestCatalog(ArchiveSnapshot snapshot)
        {
            Snapshot = snapshot; Revision = new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
            var owner = new ResourceIdentity(snapshot.IndexContentId, 1);
            var evidence = new TextureEvidence(owner, Revision, "Synthetic reader", "1", "synthetic-hash", "Material surface");
            Entries = snapshot.Records.Where(row => row.Type is 2229 or 2230).Select((row, index) => new TextureDiscoveryEntry(
                new(snapshot.IndexContentId, row.Ordinal), row.Id, row.Type, $"Surface {index + 1}", TextureNameKind.OriginalName,
                $"Surface {index + 1}", $"materials/surface-{index + 1}", null, 4, 2, "BGRA8", index == 0 ? "Diffuse" : null,
                index == 0 ? [new(owner, "Sample material", TextureRelationshipKind.Material, true, true, [evidence], "Diffuse"), new(owner, "Candidate material", TextureRelationshipKind.Material, true, false, [])] : [],
                [], [new($"Surface {index + 1}", TextureNameKind.OriginalName, [evidence])], ["Synthetic metadata"], [], ["Synthetic catalog"],
                $"surface-{row.Id:X8}-{row.Ordinal}", $"Surface {index + 1} materials/surface-{index + 1}")).ToArray();
        }
        public ArchiveSnapshot Snapshot { get; }
        public SourceRevision Revision { get; }
        public TextureDiscoveryEntry[] Entries { get; private set; }
        public Func<TextureDiscoveryFilterOptions>? FilterQuery { get; set; }
        public TextureDiscoveryFilterOptions FilterOptions => FilterQuery?.Invoke() ?? new([new(4, 2)], ["BGRA8"], ["Diffuse"], []);
        public TextureNamingCoverage Coverage => new(Entries.Length, Entries.Length, Entries.Length, 0, 0, 0, Entries.Count(entry => entry.Alias is not null), 0, 1, 1, false, ["Synthetic metadata"], []);
        public event EventHandler? Updated;
        public Func<TextureDiscoveryQuery, CancellationToken, Task<TextureDiscoveryPage>>? QueryOverride { get; set; }
        public Task<TextureDiscoveryPage> QueryAsync(TextureDiscoveryQuery query, CancellationToken token = default)
        {
            if (QueryOverride is not null) return QueryOverride(query, token);
            return Task.FromResult(Page(query, token));
        }
        public TextureDiscoveryPage Page(TextureDiscoveryQuery query, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var matches = Entries.Where(entry => (string.IsNullOrWhiteSpace(query.Search) || (entry.Label + " " + entry.SearchText + " " + entry.Alias).Contains(query.Search, StringComparison.OrdinalIgnoreCase)) &&
                (query.Width is null || entry.Width == query.Width) && (query.Height is null || entry.Height == query.Height) &&
                (query.Format is null || entry.Format == query.Format) && (query.VerifiedRole is null || entry.VerifiedRole == query.VerifiedRole) &&
                (query.HasVerifiedOwner is null || entry.Owners.Any(link => link.Verified) == query.HasVerifiedOwner)).ToArray();
            return new TextureDiscoveryPage(matches.Skip((int)query.Offset).Take(query.Limit).ToArray(), matches.Length, query.Offset);
        }
        public Task<TextureDiscoveryEntry?> GetDetailsAsync(ResourceIdentity identity, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(Entries.FirstOrDefault(entry => entry.Identity == identity)); }
        public Task SetAliasAsync(ResourceIdentity identity, string? alias, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Replace(Entries.Single(entry => entry.Identity == identity) with { Alias = alias }); return Task.CompletedTask; }
        public void Replace(TextureDiscoveryEntry entry)
        { Entries = Entries.Select(previous => previous.Identity == entry.Identity ? entry : previous).ToArray(); Updated?.Invoke(this, EventArgs.Empty); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(target)!;
    private static void CapturePreview(FrameworkElement view)
    {
        var path = Environment.GetEnvironmentVariable("DEVIANT_TEXTURE_UI_CAPTURE");
        if (string.IsNullOrWhiteSpace(path)) return;
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var background = new System.Windows.Media.DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(view.TryFindResource("WindowBackground") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White, null, new Rect(0, 0, view.ActualWidth, view.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); png.Save(stream);
    }
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(target, args);
    private static async Task WaitUntilAsync(Func<bool> predicate)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!predicate()) await Task.Delay(10, timeout.Token); }
    private static void Pump(Task task)
    { var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher; task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult(); }
}
