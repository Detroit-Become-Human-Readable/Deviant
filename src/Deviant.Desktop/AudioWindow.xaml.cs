using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Audio;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Export.Audio;
using Deviant.Formats.Audio.Model;
using Microsoft.Win32;

namespace Deviant.Desktop;

public partial class AudioWindow : Window, IAssetNavigator
{
    private readonly CacheManager cache = new();
    private readonly AliasStore aliases = new();
    private readonly AudioToolPaths tools;
    private readonly Player player = new();
    private readonly DispatcherTimer filterTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer seekTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly HashSet<string> selectedIdentities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioExplorerRow> selectedAudioRows = new(StringComparer.Ordinal);
    private ResourceIdentity? focusedIdentity;
    private string? focusedSourceStamp;
    private AudioExplorerSession? session;
    private AudioExplorerSession? observedAudioEnrichment;
    private PreviewSession? activePreview;
    private AudioExplorerQuery currentQuery = new();
    private CancellationTokenSource? openCancellation;
    private CancellationTokenSource? queryCancellation;
    private Task? foregroundAudioQuery;
    private Task? audioQueryRefresh;
    private bool audioQueryRefreshPending;
    private long audioQueryEpoch;
    private readonly HashSet<Task> audioQueryTasks = [];
    private CancellationTokenSource? jobCancellation;
    private long openGeneration;
    private long queryGeneration;
    private bool syncingControls;
    private bool suppressSelectionTracking;
    private bool closeApproved;
    private bool closingInProgress;
    private bool seeking;
    private bool updatingSeekSlider;
    private double pendingSeek;
    private string? inputPath;
    private AudioExportReport? lastReport;
    private string? lastReportSourceRoot;
    private Task? activeJob;
    private Task? activeOpen;
    private readonly SemaphoreSlim workspaceCommit = new(1,1);
    private int workspaceTransitionDepth;
    private readonly HashSet<Task> pendingOpens = [];
    private bool tickingPlayback;
    private readonly List<(MainWindow Window, IBrowserController Controller)> advancedWindows = [];
    private readonly ObservableCollection<AudioOutputDevice> outputDevices=[new("","Default output")];
    private readonly IAudioOutputDeviceProvider deviceProvider;
    private readonly AliasDraftStore aliasDrafts=new();
    private PlaybackContext? playing;
    private long playbackGeneration;
    private long previewRequestVersion;
    private CancellationTokenSource? previewCancellation;
    private ResourceIdentity? aliasEditorIdentity;
    private bool updatingAlias,updatingDevices;
    private readonly StringsView stringsView=new();
    private readonly DdsImageView ddsView=new();
    private readonly AnimationRecordView animationView=new();
    private readonly StoredShaderView shaderView=new();
    private readonly SceneReferencesView referencesView=new();
    private readonly AssetFamilyView familyView=new();
    private readonly BufferInspectionView bufferView=new();
    private readonly SceneView sceneView = new();
    private readonly RecordCategoriesView recordsView = new();
    private ISceneCatalogSession? sceneSession;
    private bool stringsMode;
    public TextureCatalogSessionOptions? TextureCatalogOptions { get; set; }
    public TextureDiscoverySessionOptions? TextureDiscoveryOptions { get; set; }
    public SceneDiscoveryOptions? SceneOptions { get; set; }
    public Func<ArchiveSnapshot, CacheManager, AliasStore, SceneDiscoveryOptions?, CancellationToken, Task<ISceneCatalogSession>>? SceneSessionFactory { get; set; }

    public AudioWindow(CacheManager? cacheManager = null, AliasStore? aliasStore = null, AudioToolPaths? audioTools = null, Player? playbackPlayer = null,IAudioOutputDeviceProvider? outputDeviceProvider=null)
    {
        if (cacheManager is not null) cache = cacheManager;
        if (aliasStore is not null) aliases = aliasStore;
        cache.RegisterProtectedDirectory(aliases.LibraryRoot);
        if (playbackPlayer is not null) player = playbackPlayer;
        tools = audioTools ?? AudioToolPaths.Discover(applicationDirectory: AppContext.BaseDirectory);
        deviceProvider=outputDeviceProvider??new AudioOutputDeviceProvider();
        InitializeComponent();
        StringsHost.Content=stringsView;
        stringsView.RelatedNavigator=NavigateStringRelatedAsync;stringsView.RelatedLabelResolver=StringRelatedLabel;
        DdsHost.Content=ddsView;
        AnimationHost.Content=animationView;ShaderHost.Content=shaderView;
        InitializePhysics();
        InitializeAudioDialogue();
        InitializeCharacters();
        ReferencesHost.Content=referencesView;
        ScenesHost.Content = sceneView;
        RecordsHost.Child = recordsView;
        sceneView.AssetNavigator = this;
        sceneView.TextureOwnerQueryProvider = QueryTexturesByOwnerAsync;
        InitializeSceneRendering();
        InitializeTextureExplorer();
        InitializeCommonGroups();
        familyView.PreviewUnavailableReason="";
        player.PlaybackStopped+=Player_PlaybackStopped;
        filterTimer.Tick += async (_, _) => { filterTimer.Stop(); await QueryAsync(resetOffset: true); };
        playbackTimer.Tick += PlaybackTimer_Tick;
        seekTimer.Tick += SeekTimer_Tick;
        DeviceBox.ItemsSource=outputDevices;
        DeviceBox.SelectedIndex = 0;
        DevicesReady = LoadDevicesAsync();
        MediaList.ItemsSource = new ObservableCollection<AudioExplorerRow>();
        AudioSortBox.ItemsSource=new[]{new AssetSortChoice("Name",AssetSort.Name),new("Language",AssetSort.Language),new("Codec",AssetSort.Codec),new("Duration",AssetSort.Duration),new("Channels",AssetSort.Channels),new("Completeness",AssetSort.Completeness)};AudioSortBox.SelectedIndex=0;
        AvailabilityBox.ItemsSource = new[] { "Any", "Available", "Missing", "Ambiguous", "Unsupported", "Malformed" };
        CompletenessBox.ItemsSource = new[] { "Any", "Complete", "Unclassified", "Partial" };
        AvailabilityBox.SelectedIndex = 0;
        CompletenessBox.SelectedIndex = 0;
        InitializeNavigation();
        InitializeActivity();
    }

    public AudioExplorerSession? CurrentSession => session;
    public Player PlaybackPlayer => player;
    public PlaybackContext? PlayingContext => playing;
    public Task DevicesReady { get; private set; }=Task.CompletedTask;
    public StringsView StringsBrowser => stringsView;
    public SceneView SceneBrowser => sceneView;
    public Task OpenAsync(ResourceIdentity identity, SourceRevision revision, CancellationToken token = default) =>
        NavigateAssetAsync(identity, revision, token);
    public async Task ShowScenesAsync(CancellationToken token = default)
    {
        if (!RequireWorkspaceNavigation()) return;
        var current = session;
        if (current is null) return;
        var navigation = navigationGeneration;
        await sceneNavigationDrain.WaitAsync(token);
        if (navigation != navigationGeneration || !ReferenceEquals(current, session) || closingInProgress) return;
        await modelView.UnloadAsync();
        ShowBrowse("scenes");
        ScenesHost.Content = sceneView;
        var revision = new SourceRevision(current.Snapshot.IndexContentId, current.Snapshot.PhysicalSourceStamp);
        if (sceneSession is null || sceneSession.Revision != revision)
        {
            await ClearSceneSessionAsync();
            var factory = SceneSessionFactory;
            var created = factory is null
                ? await OpenDefaultSceneSessionAsync(current.Snapshot, SceneOptions, token)
                : await factory(current.Snapshot, cache, aliases, SceneOptions, token);
            if (token.IsCancellationRequested || navigation != navigationGeneration || !ReferenceEquals(current, session) || closingInProgress)
            {
                await created.DisposeAsync();
                token.ThrowIfCancellationRequested();
                return;
            }
            sceneSession = created;
        }
        await sceneView.LoadAsync(sceneSession, token);
        if (navigation != navigationGeneration || !ReferenceEquals(current, session) || closingInProgress) return;
        await RestoreSceneReturnStateAsync(navigation, current, token);
        if (sceneSession.Progress.HasMore || sceneSession.Progress.Candidates == 0)
            await sceneView.DiscoverAllAsync(token);
    }
    public async Task ShowStringsAsync(CancellationToken token=default)
    {
        if (!RequireWorkspaceNavigation()) return;
        ShowBrowse("strings");
        if(session is not null&&(stringsView.CurrentSession?.Snapshot.PhysicalSourceStamp!=session.Snapshot.PhysicalSourceStamp||stringsView.CurrentSession?.Snapshot.IndexContentId!=session.Snapshot.IndexContentId))
            await stringsView.LoadAsync(session.Snapshot,cache.Location,token);
        else await stringsView.RetryIndexingAsync(token);
    }
    public Task StopPreviewAudioAsync() => StopPlaybackAsync();
    public IReadOnlyList<AudioExplorerRow> CurrentRows => MediaList.ItemsSource is IEnumerable<AudioExplorerRow> rows ? rows.ToArray() : Array.Empty<AudioExplorerRow>();
    public Task OpenArchiveAsync(string path, CancellationToken cancellationToken = default) {BeginNavigation();return OpenAsync(path,cancellationToken);}
    public Task<AudioExplorerPage> QueryAudioAsync(AudioExplorerQuery query, CancellationToken cancellationToken = default) =>
        session?.QueryAsync(query, cancellationToken) ?? Task.FromException<AudioExplorerPage>(new InvalidOperationException("No audio archive is open."));
    public Task<AudioExportReport> ExportAudioAsync(AudioExportReview review, AudioExportOptions options,
        IProgress<AudioProgress>? progress = null, CancellationToken cancellationToken = default) =>
        session?.ExportAsync(review, options, progress, cancellationToken) ?? Task.FromException<AudioExportReport>(new InvalidOperationException("No audio archive is open."));

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Open Detroit archive index", Filter = "Index files (*.idx)|*.idx|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) { Home_Click(this, new()); await OpenArchiveAsync(dialog.FileName); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    { if (inputPath is not null) await OpenAsync(inputPath); }

    private Task OpenAsync(string path, CancellationToken externalCancellation = default)
    {
        var task = OpenCoreAsync(path, externalCancellation);
        pendingOpens.Add(task);
        activeOpen = task;
        return ClearActiveOpenAsync(task);
    }

    private async Task ClearActiveOpenAsync(Task task)
    {
        try { await task; }
        finally { pendingOpens.Remove(task); if (ReferenceEquals(activeOpen, task)) activeOpen = null; }
    }

    private async Task OpenCoreAsync(string path, CancellationToken externalCancellation)
    {
        var generation = Interlocked.Increment(ref openGeneration);
        var navigation=navigationGeneration;
        openCancellation?.Cancel(); openCancellation?.Dispose();
        openCancellation = externalCancellation.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(externalCancellation)
            : new CancellationTokenSource();
        var token = openCancellation.Token;
        AudioExplorerSession? opened=null;
        var committed=false;
        var entered=false;
        indexOpeningActive = true;
        SetBusy(true, "Opening archive index…");
        archiveProgress?.Dispose();
        var progress = new CoalescedProgress<AudioExplorerProgress>(value => Dispatcher.BeginInvoke(new Action(() => ReceiveArchiveProgress(value, generation))));
        archiveProgress = progress;
        try
        {
            opened = await AudioExplorerSession.OpenStagedAsync(path, cache, aliases, tools, progress, token, startEnrichment: false);
            ValidateWorkspace(opened.Snapshot);
            await workspaceCommit.WaitAsync(token);entered=true;
            ++workspaceTransitionDepth;
            if (generation != openGeneration || token.IsCancellationRequested || closingInProgress) return;
            await CancelQueryAsync();
            await StopPlaybackAsync();
            await ClearPhysicsAsync();
            await ClearAudioDialogueAsync();
            await ClearCharactersAsync();
            await stringsView.DisposeAsync();
            await ClearSceneSessionAsync();
            await animationView.ClearAsync();await shaderView.ClearAsync();
            await referencesView.ClearAsync();
            await ClearTextureCatalogsAsync();
            await ClearSharedCatalogReferencesAsync();
            await familyView.ClearAsync();
            await ClearBufferAsync();
            if (generation != openGeneration || token.IsCancellationRequested || closingInProgress) return;
            var old=session;
            var sameSource=old is not null&&old.Snapshot.IndexContentId==opened.Snapshot.IndexContentId&&old.Snapshot.PhysicalSourceStamp==opened.Snapshot.PhysicalSourceStamp;
            var previousFocus=sameSource?focusedIdentity??CurrentRow?.Identity:null;
            var previousSelection=sameSource?selectedIdentities.ToArray():Array.Empty<string>();
            if(old is not null)old.Updated-=Session_Updated;
            session = opened;
            admittedWorkspace = opened.Snapshot;
            workspace=null;
            committed=true;
            if(!sameSource)aliasDrafts.Clear();aliasEditorIdentity=null;
            inputPath = path; SourceLabel.Text = System.IO.Path.GetFileName(path);
            selectedIdentities.Clear();
            if (!sameSource) selectedAudioRows.Clear();
            selectedIdentities.UnionWith(previousSelection);
            if (sameSource && previousFocus is { } retainedFocus)
            {
                focusedIdentity = retainedFocus;
                focusedSourceStamp = opened.Snapshot.PhysicalSourceStamp;
            }
            else ClearFocusedIdentity();
            suppressSelectionTracking=true;try{MediaList.ItemsSource=Array.Empty<AudioExplorerRow>();}finally{suppressSelectionTracking=false;}
            SelectionText.Text="0 selected";PageText.Text="";AudioPrevious.IsEnabled=AudioNext.IsEnabled=false;UpdateSelectedDetails();
            opened.Updated += Session_Updated;
            if(old is not null)await old.DisposeAsync();
            if(!ReferenceEquals(session,opened)||closingInProgress)return;
            await WorkspaceOpenedAsync(path,opened.Snapshot,navigation,generation,opened);
            if(!ReferenceEquals(session,opened)||closingInProgress)return;
            if(generation==openGeneration) { indexOpeningActive = false; SetBusy(false, "Index ready"); }
            UpdateFilterChoices(opened);
            if (currentView == "audio" || opened.IsEnrichmentComplete) await QueryAsync(resetOffset: !sameSource);
            if(!ReferenceEquals(session,opened)||closingInProgress)return;
            if(previousFocus is not null&&CurrentRows.SingleOrDefault(row=>row.Identity==previousFocus) is{}focused)MediaList.SelectedItem=focused;
            EnsureAudioEnrichment();
            RefreshActivity();
            if(stringsMode&&!token.IsCancellationRequested) await stringsView.LoadAsync(opened.Snapshot,cache.Location,token);
        }
        catch (OperationCanceledException) { if (generation == openGeneration) SetBusy(false, "Open cancelled"); }
        catch (Exception ex) { WorkflowDiagnostics.Failure("Workspace.Open", ex, phase: "Opening archive"); if (generation == openGeneration) SetBusy(false, ex.Message); }
        finally
        {
            if (generation == openGeneration) { indexOpeningActive = false; RefreshActivity(); RefreshWorkspaceNavigation(); }
            if(entered) { --workspaceTransitionDepth; workspaceCommit.Release(); RefreshWorkspaceNavigation(); }
            if(opened is not null&&!committed)await opened.DisposeAsync();
        }
    }

    private void EnsureAudioEnrichment()
    {
        if (currentView != "audio") return;
        if (session is not { } current || current.IsEnrichmentComplete || ReferenceEquals(observedAudioEnrichment, current)) return;
        observedAudioEnrichment = current;
        _ = TrackLibraryTask(ObserveEnrichmentAsync(current, openGeneration, CancellationToken.None));
    }

    private async Task ObserveEnrichmentAsync(AudioExplorerSession opened, long generation, CancellationToken token)
    {
        try
        {
            await opened.PrepareBrowseAsync(token);
            if (!opened.IsEnrichmentComplete) await opened.EnrichmentTask;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { WorkflowDiagnostics.Failure("Audio.Operation", ex); _ = Dispatcher.BeginInvoke(() => { if (ReferenceEquals(session, opened)) HomeStatus.Text = StatusText.Text = ex.Message; }); }
        if (ReferenceEquals(session, opened)&&!closingInProgress&&(currentView is "audio" or "home"))
            await Dispatcher.InvokeAsync(async () => { UpdateFilterChoices(opened); UpdateExplorerGroups(); await RefreshAudioQueryAsync(); }).Task.Unwrap();
        if (ReferenceEquals(session, opened) && !closingInProgress)
            await Dispatcher.InvokeAsync(RefreshActivity);
    }

    private void Session_Updated(object? sender, AudioExplorerUpdate update)
    {
        if (sender is AudioExplorerSession current)
            audioUpdates?.Report(new(current, update, openGeneration));
    }

    private Task QueryAsync(bool resetOffset)
    {
        filterTimer.Stop();
        return foregroundAudioQuery = TrackAudioQueryAsync(RunAudioQueryAsync(resetOffset));
    }

    private async Task TrackAudioQueryAsync(Task task)
    {
        audioQueryTasks.Add(task);
        try { await task; }
        finally { audioQueryTasks.Remove(task); }
    }

    private Task RefreshAudioQueryAsync()
    {
        audioQueryRefreshPending = true;
        if (audioQueryRefresh is { IsCompleted: false }) return audioQueryRefresh;
        return audioQueryRefresh = TrackLibraryTask(DrainAudioQueryRefreshAsync());
    }

    private async Task DrainAudioQueryRefreshAsync()
    {
        var current = session;
        var epoch = audioQueryEpoch;
        while (audioQueryRefreshPending)
        {
            audioQueryRefreshPending = false;
            while (foregroundAudioQuery is { IsCompleted: false } foreground) await foreground;
            if (epoch != audioQueryEpoch || closingInProgress || !ReferenceEquals(current, session) ||
                currentView is not ("audio" or "home") || filterTimer.IsEnabled) return;
            await TrackAudioQueryAsync(RunAudioQueryAsync(resetOffset: false));
        }
    }

    private async Task RunAudioQueryAsync(bool resetOffset)
    {
        var current = session;
        if (current is null) return;
        queryCancellation?.Cancel(); queryCancellation?.Dispose();
        queryCancellation = new CancellationTokenSource();
        var token = queryCancellation.Token;
        var generation = Interlocked.Increment(ref queryGeneration);
        var offset = resetOffset ? 0 : currentQuery.Offset;
        currentQuery = ReadQuery(offset, 100);
        if (currentView == "audio" && !foregroundBusy)
            StatusText.Text = current.IsEnrichmentComplete ? "Searching…" : "Searching current audio snapshot…";
        try
        {
            var page = await current.QueryAsync(currentQuery, token);
            if (generation != queryGeneration || token.IsCancellationRequested || !ReferenceEquals(current, session)) return;
            while (offset > 0 && offset >= page.Total)
            {
                offset = page.Total == 0 ? 0 : (page.Total - 1) / currentQuery.Limit * currentQuery.Limit;
                currentQuery = currentQuery with { Offset = offset };
                page = await current.QueryAsync(currentQuery, token);
                if (generation != queryGeneration || token.IsCancellationRequested || !ReferenceEquals(current, session)) return;
            }
            var selection = selectedIdentities.ToArray();
            var refreshedSelection = current.IsCatalogLoaded
                ? await Task.Run(() => current.ResolveSelectionKeys(selection), token)
                : Array.Empty<AudioExplorerRow>();
            if (generation != queryGeneration || token.IsCancellationRequested || !ReferenceEquals(current, session)) return;
            foreach (var row in refreshedSelection) if (selectedIdentities.Contains(row.Identity.Key)) selectedAudioRows[row.Identity.Key] = row;
            var focusIdentity = IsFocusedInCurrentSource ? focusedIdentity?.Key : null;
            suppressSelectionTracking = true;
            try
            {
                MediaList.ItemsSource = page.Rows;
                foreach (var row in page.Rows.Where(row => selectedIdentities.Contains(row.Identity.Key))) selectedAudioRows[row.Identity.Key] = row;
                foreach (var row in page.Rows.Where(row => selectedIdentities.Contains(row.Identity.Key))) MediaList.SelectedItems.Add(row);
                if (focusIdentity is not null && page.Rows.FirstOrDefault(row => row.Identity.Key == focusIdentity) is { } focused)
                    MediaList.SelectedItem = focused;
            }
            finally { suppressSelectionTracking = false; }
            PageText.Text = $"{(page.Total == 0 ? 0 : offset + 1):N0}–{Math.Min(offset + page.Rows.Count, page.Total):N0} of {page.Total:N0}";
            audioResultStatus = $"{page.Total:N0} audio resources";
            AudioPrevious.IsEnabled = offset > 0;
            AudioNext.IsEnabled = offset + page.Rows.Count < page.Total;
            SelectionText.Text = $"{selectedIdentities.Count:N0} selected";
            if (currentView == "audio" && !foregroundBusy) StatusText.Text = audioResultStatus;
            RefreshActivity();
            UpdateSelectedDetails();
            RefreshSavedAudioFilters(current);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { WorkflowDiagnostics.Failure("Audio.Operation", ex); if (generation == queryGeneration && currentView == "audio" && !foregroundBusy) StatusText.Text = ex.Message; }
    }

    private AudioExplorerQuery ReadQuery(long offset, int limit)
    {
        var bank = (BankBox.SelectedItem as AudioBankChoice)?.Key;
        if (string.IsNullOrEmpty(bank)) bank = null;
        var language = (LanguageBox.SelectedItem as string) is { Length: > 0 } l && l != "Any" ? l : null;
        var codec = (CodecBox.SelectedItem as string) is { Length: > 0 } c && c != "Any" ? c : null;
        int? channels = int.TryParse(ChannelsBox.SelectedItem?.ToString(), out var ch) && ch > 0 ? ch : null;
        AssetAvailability? availability = Enum.TryParse<AssetAvailability>(AvailabilityBox.SelectedItem?.ToString(), out var a) && AvailabilityBox.SelectedIndex > 0 ? a : null;
        AssetCompleteness? completeness = CompletenessBox.SelectedIndex switch { 1 => AssetCompleteness.Complete, 2 => AssetCompleteness.Unverified, 3 => AssetCompleteness.Partial, _ => null };
        return new AudioExplorerQuery(SearchBox.Text, bank, language, codec,
            ParseNonnegative(MinimumDurationBox.Text), ParseNonnegative(MaximumDurationBox.Text), channels,
            availability, completeness is null ? null : completeness switch
            {
                AssetCompleteness.Complete => MediaCompleteness.Complete,
                AssetCompleteness.Partial => MediaCompleteness.Fragment,
                _ => MediaCompleteness.Unverified
            }, offset, limit,Sort:(AudioSortBox.SelectedItem as AssetSortChoice)?.Sort??AssetSort.Name,Descending:AudioDescending.IsChecked==true) with { Availability = availability };
    }

    private static double? ParseNonnegative(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;

    private void UpdateFilterChoices(AudioExplorerSession current)
    {
        syncingControls = true;
        var selectedBank = (BankBox.SelectedItem as AudioBankChoice)?.Key;
        BankBox.ItemsSource = new[] { new AudioBankChoice("", "All banks", current.MediaCount, 0, "") }.Concat(current.Banks).ToArray();
        BankBox.SelectedItem = BankBox.Items.Cast<AudioBankChoice>().FirstOrDefault(item => item.Key == selectedBank) ?? BankBox.Items[0];
        var lang = LanguageBox.SelectedItem as string; LanguageBox.ItemsSource = new[] { "Any" }.Concat(current.Languages).ToArray(); LanguageBox.SelectedItem = lang is not null && LanguageBox.Items.Contains(lang) ? lang : "Any";
        var codec = CodecBox.SelectedItem as string; CodecBox.ItemsSource = new[] { "Any" }.Concat(current.Codecs).ToArray(); CodecBox.SelectedItem = codec is not null && CodecBox.Items.Contains(codec) ? codec : "Any";
        var ch = ChannelsBox.SelectedItem; ChannelsBox.ItemsSource = new object[] { "Any" }.Concat(current.ChannelCounts.Cast<object>()).ToArray(); ChannelsBox.SelectedItem = ch is not null && ChannelsBox.Items.Contains(ch) ? ch : "Any";
        syncingControls = false;
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (syncingControls || session is null || closingInProgress) return;
        queryCancellation?.Cancel();Interlocked.Increment(ref queryGeneration);
        filterTimer.Stop(); filterTimer.Start();
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e) => FilterChanged(sender, new RoutedEventArgs());
    private void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        syncingControls = true; SearchBox.Clear(); MinimumDurationBox.Clear(); MaximumDurationBox.Clear();
        if (BankBox.Items.Count > 0) BankBox.SelectedIndex = 0; LanguageBox.SelectedIndex = CodecBox.SelectedIndex = ChannelsBox.SelectedIndex = 0;
        AvailabilityBox.SelectedIndex = CompletenessBox.SelectedIndex = 0; syncingControls = false; _ = QueryAsync(true);
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    { if (currentQuery.Offset >= currentQuery.Limit) { currentQuery = currentQuery with { Offset = currentQuery.Offset - currentQuery.Limit }; await QueryAsync(false); } }
    private async void Next_Click(object sender, RoutedEventArgs e)
    { if (AudioNext.IsEnabled) { currentQuery = currentQuery with { Offset = currentQuery.Offset + currentQuery.Limit }; await QueryAsync(false); } }

    private void MediaList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSelectionTracking) return;
        CancelSelectedAudioDetails();
        foreach (AudioExplorerRow row in e.RemovedItems)
        {
            selectedIdentities.Remove(row.Identity.Key);
            selectedAudioRows.Remove(row.Identity.Key);
            if (focusedIdentity == row.Identity) ClearFocusedIdentity();
        }
        foreach (AudioExplorerRow row in e.AddedItems)
        {
            selectedIdentities.Add(row.Identity.Key);
            selectedAudioRows[row.Identity.Key] = row;
            focusedIdentity = row.Identity;
            focusedSourceStamp = session?.Snapshot.PhysicalSourceStamp;
        }
        SelectionText.Text = $"{selectedIdentities.Count:N0} selected";
        UpdateSelectedDetails();
        SelectionForLibraryChanged();
    }

    private bool IsFocusedInCurrentSource => focusedIdentity is { } identity && session is { } current &&
        identity.IndexContentId == current.Snapshot.IndexContentId &&
        focusedSourceStamp == current.Snapshot.PhysicalSourceStamp && selectedIdentities.Contains(identity.Key);

    private void ClearFocusedIdentity()
    {
        focusedIdentity = null;
        focusedSourceStamp = null;
    }

    private AudioExplorerRow? CurrentRow
    {
        get
        {
            if (MediaList.SelectedItem is AudioExplorerRow visible)
                return selectedAudioRows.GetValueOrDefault(visible.Identity.Key) ?? visible;
            if (!IsFocusedInCurrentSource || focusedIdentity is not { } identity || session is not { } current) return null;
            return selectedAudioRows.GetValueOrDefault(identity.Key);
        }
    }
    private void UpdateSelectedDetails()
    {
        UpdateAudioDialogueSelection(CurrentRow);
        var selected = selectedIdentities.Select(key => selectedAudioRows.GetValueOrDefault(key)).OfType<AudioExplorerRow>().ToArray();
        var statuses = selected.Select(item => item.CompletenessBadge).Where(text => text.Length > 0).Distinct().ToArray();
        SelectionBadge.Text = string.Join(" · ", statuses);
        SelectionBadge.Visibility = statuses.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var row = CurrentRow;
        DetailName.Text = row?.Label ?? "Select a clip";
        DetailMetadata.Text = row is null ? "" : AudioMediaPresentation.Details(row);
        DetailDiagnostic.Text = row?.UnavailableReason ?? "";
        updatingAlias=true;
        try { aliasEditorIdentity=row?.Identity; AliasBox.Text=row is null?"":aliasDrafts.Read(row.Identity,row.UserAlias); }
        finally { updatingAlias=false; }
        AdvancedDetails.Text = row?.SourceMedia is null ? "" : $"Media {row.IdText}\nResource {row.Identity.Ordinal}\nMember {row.Identity.MemberPath}\nBank {row.Media.BankKey}\nOffset {row.Media.Slice.Offset}\nLength {row.Media.Slice.Length}\nIndex {row.Identity.IndexContentId}\n" +
            string.Join("\n", row.Media.Names.Select(name => $"{name.Kind} · {name.Text} · {name.Source}")) +
            (row.Media.EventKeys.Count == 0 ? "" : "\nEvents\n" + string.Join("\n",row.Media.EventKeys));
        PlayButton.IsEnabled = row?.CanPreview == true;
        PartialButton.IsEnabled = row?.CanPreviewPartial == true;
        PartialButton.Visibility = row?.SourceMedia?.Completeness == MediaCompleteness.Fragment ? Visibility.Visible : Visibility.Collapsed;
        ExportPartialButton.Visibility = row?.SourceMedia?.Completeness == MediaCompleteness.Fragment && row.CanExportOriginal
            ? Visibility.Visible : Visibility.Collapsed;
        _=RefreshRelatedAsync();
        ExportBankButton.IsEnabled = row?.SourceMedia is { } media && session?.Banks.Any(bank => bank.Key == media.BankKey && bank.CanExportOriginal) == true;
        EnsureSelectedAudioDetails(row);
    }

    private async void SaveAlias_Click(object sender, RoutedEventArgs e)
    {
        if (session is not { } current || CurrentRow is not { } row) return;
        try
        {
            var draft = AliasBox.Text;
            await current.SetAliasAsync(row.Identity, draft);
            aliasDrafts.Saved(row.Identity, draft);
            if (!ReferenceEquals(session, current)) return;
            if (selectedIdentities.Contains(row.Identity.Key)) selectedAudioRows[row.Identity.Key] = current.ResolveSelection([row.Identity]).Single();
            await QueryAsync(false);
        }
        catch (Exception ex) { WorkflowDiagnostics.Failure("Audio.Operation", ex); StatusText.Text = ex.Message; }
    }
    private async void RemoveAlias_Click(object sender, RoutedEventArgs e)
    {
        if (session is not { } current || CurrentRow is not { } row) return;
        try
        {
            await current.SetAliasAsync(row.Identity, null); aliasDrafts.Saved(row.Identity, null);
            if (!ReferenceEquals(session, current)) return;
            if (selectedIdentities.Contains(row.Identity.Key)) selectedAudioRows[row.Identity.Key] = current.ResolveSelection([row.Identity]).Single();
            await QueryAsync(false);
        }
        catch (Exception error) { WorkflowDiagnostics.Failure("Audio.Operation", error); StatusText.Text = error.Message; }
    }

    private void AliasBox_TextChanged(object sender,TextChangedEventArgs e)
    { if(!updatingAlias&&aliasEditorIdentity is not null) aliasDrafts.Edit(aliasEditorIdentity,AliasBox.Text); }

    private async void Play_Click(object sender, RoutedEventArgs e) { if (CurrentRow is { } row) await StartPreviewAsync(row, false); }
    private async void Partial_Click(object sender, RoutedEventArgs e) { if (CurrentRow is { CanPreviewPartial: true } row) await StartPreviewAsync(row, true); }
    private Task StartPreviewAsync(AudioExplorerRow row, bool partial) => StartPreviewAudioAsync(row, partial);
    public async Task StartPreviewAudioAsync(AudioExplorerRow row, bool partial = false, CancellationToken cancellationToken = default)
    {
        if (closingInProgress || workspaceTransitionDepth != 0) return;
        var request = ++previewRequestVersion;
        if (activeJob is { IsCompleted: false } pending)
        {
            if (previewCancellation is null) return;
            previewCancellation.Cancel();
            try { await pending; } catch { }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (request != previewRequestVersion || closingInProgress || workspaceTransitionDepth != 0) return;
        await TrackJobAsync(() => StartPreviewCoreAsync(row, partial, cancellationToken, request));
    }

    private async Task StartPreviewCoreAsync(AudioExplorerRow row, bool partial, CancellationToken externalCancellation, long request)
    {
        var current = session; if (current is null) return;
        jobCancellation?.Cancel(); jobCancellation?.Dispose();
        var ownCancellation = externalCancellation.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(externalCancellation) : new CancellationTokenSource();
        jobCancellation = ownCancellation;
        previewCancellation = ownCancellation;
        var token = ownCancellation.Token;
        PreviewSession? pendingPreview = null;
        bool acceptPreviewProgress = true;
        var volume = (float)VolumeSlider.Value;
        try
        {
            await StopPlaybackAsync(cancelPendingPreview: false);
            token.ThrowIfCancellationRequested();
            if (request != previewRequestVersion || !ReferenceEquals(current, session)) return;
            SetBusy(true, partial ? "Preparing partial preview…" : "Preparing preview…");
            row = await current.PrepareSelectedMediaAsync(row.Identity, token);
            using var previewProgress = new CoalescedProgress<AudioProgress>(value => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (acceptPreviewProgress && !token.IsCancellationRequested && request == previewRequestVersion && ReferenceEquals(current, session)
                    && !closingInProgress && !indexOpeningActive)
                {
                    foregroundStage = value.Phase; foregroundCompleted = value.Completed; foregroundTotal = value.Total;
                    RefreshActivity();
                }
            })));
            pendingPreview = await Task.Run(async () => await current.PreviewAsync(row.Identity, partial,
                progress: previewProgress, cancellationToken: token).ConfigureAwait(false), token);
            acceptPreviewProgress = false;
            if (token.IsCancellationRequested || request != previewRequestVersion || !ReferenceEquals(current, session)) return;
            await Task.Run(async () => await player.LoadAsync(pendingPreview.Path, volume, cancellationToken: token), token);
            if (token.IsCancellationRequested || request != previewRequestVersion || !ReferenceEquals(current, session)) { await Task.Run(player.Stop); return; }
            if(player.State==NAudio.Wave.PlaybackState.Stopped) throw new AudioToolException("Playback stopped before the preview was ready.");
            activePreview = pendingPreview; pendingPreview = null;
            playing=new(row.Identity,row.Label,row.Media.Completeness,++playbackGeneration);
            UpdatePlaybackPresentation();
            playbackTimer.Start(); SetBusy(false, partial ? "Partial preview · original fragment" : "Preview ready");
        }
        catch (OperationCanceledException) { if (request == previewRequestVersion && ReferenceEquals(current, session) && !indexOpeningActive && !closingInProgress) SetBusy(false, "Preview cancelled"); }
        catch (Exception ex) { WorkflowDiagnostics.Failure("Audio.Operation", ex); if (request == previewRequestVersion && ReferenceEquals(current, session) && !indexOpeningActive && !closingInProgress) SetBusy(false, ex.Message); }
        finally
        {
            acceptPreviewProgress = false;
            if (pendingPreview is not null) await pendingPreview.DisposeAsync();
            if (ReferenceEquals(jobCancellation, ownCancellation)) jobCancellation = null;
            if (ReferenceEquals(previewCancellation, ownCancellation)) previewCancellation = null;
            ownCancellation.Dispose();
        }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e) { try { await Task.Run(player.Toggle); PauseButton.Content = player.State == NAudio.Wave.PlaybackState.Paused ? "Resume" : "Pause"; } catch (Exception ex) { WorkflowDiagnostics.Failure("Audio.Operation", ex); StatusText.Text = ex.Message; } }
    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopPlaybackAsync();
    private async Task StopPlaybackAsync(bool cancelPendingPreview = true)
    {
        if (cancelPendingPreview)
        {
            ++previewRequestVersion;
            if (previewCancellation is {} pending)
            {
                pending.Cancel();
                if (!indexOpeningActive && !closingInProgress && ReferenceEquals(jobCancellation, pending)) SetBusy(false, "Stopped");
            }
        }
        playing=null; ++playbackGeneration; UpdatePlaybackPresentation();
        playbackTimer.Stop();
        seekTimer.Stop(); seeking = false;
        await Task.Run(player.Stop);
        var preview = activePreview; activePreview = null;
        if (preview is not null) await preview.DisposeAsync();
        SeekSlider.Value = 0; PlayTime.Text = "0:00";
    }
    private async void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (tickingPlayback) return;
        tickingPlayback = true;
        try
        {
        var position = await Task.Run(() => player.Position);
        var length = await Task.Run(() => player.Length);
        if (!seeking)
        {
            updatingSeekSlider = true;
            try { SeekSlider.Maximum = Math.Max(1, length); SeekSlider.Value = Math.Clamp(position, 0, SeekSlider.Maximum); }
            finally { updatingSeekSlider = false; }
        }
        PauseButton.Content = player.State == NAudio.Wave.PlaybackState.Paused ? "Resume" : "Pause";
        PlayTime.Text = $"{TimeSpan.FromSeconds(Math.Max(0, position)).ToString(@"m\:ss", CultureInfo.InvariantCulture)} / {TimeSpan.FromSeconds(Math.Max(0, length)).ToString(@"m\:ss", CultureInfo.InvariantCulture)}";
        if (length > 0 && position >= length - .05 && player.State == NAudio.Wave.PlaybackState.Stopped && activePreview is not null) await StopPlaybackAsync();
        }
        catch (Exception ex) { WorkflowDiagnostics.Failure("Audio.Operation", ex); StatusText.Text = ex.Message; }
        finally { tickingPlayback = false; }
    }
    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updatingSeekSlider) return;
        if (!IsLoaded || player.State == NAudio.Wave.PlaybackState.Stopped) return;
        seeking = true; pendingSeek = e.NewValue;
        seekTimer.Stop(); seekTimer.Start();
    }
    private async void SeekTimer_Tick(object? sender, EventArgs e)
    {
        seekTimer.Stop();
        if (!seeking || player.State == NAudio.Wave.PlaybackState.Stopped) { seeking = false; return; }
        var target = pendingSeek;
        try { await Task.Run(() => player.Position = target); }
        catch (Exception ex) { WorkflowDiagnostics.Failure("Audio.Operation", ex); StatusText.Text = ex.Message; }
        finally { seeking = false; }
    }
    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (IsLoaded) player.Volume = (float)e.NewValue; }
    private async void Device_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingDevices || !IsLoaded || DeviceBox.SelectedItem is not AudioOutputDevice device) return;
        try { await Task.Run(() => player.OutputDeviceId = string.IsNullOrEmpty(device.Id) ? null : device.Id); }
        catch (Exception ex) { WorkflowDiagnostics.Failure("Audio.Operation", ex); StatusText.Text = ex.Message; }
    }
    private async Task LoadDevicesAsync()
    {
        try
        {
            var devices = await deviceProvider.EnumerateAsync();
            var selected = DeviceBox.SelectedItem as AudioOutputDevice;
            updatingDevices=true;
            try
            {
                outputDevices.Clear(); outputDevices.Add(new("","Default output"));
                foreach(var device in devices.DistinctBy(item=>item.Id)) outputDevices.Add(device);
                DeviceBox.SelectedItem=outputDevices.FirstOrDefault(item=>item.Id==selected?.Id)??outputDevices[0];
            }
            finally { updatingDevices=false; }
        }
        catch (Exception ex) { WorkflowDiagnostics.Failure("Audio.Operation", ex); StatusText.Text = "Output devices unavailable: " + ex.Message; }
    }

    private async void ExportSelected_Click(object sender, RoutedEventArgs e) => await TrackJobAsync(ExportSelectedCoreAsync);

    private Task ExportSelectedCoreAsync() => PrepareSelectedAudioExportAsync(allowPartial: false);
    private async void ExportMatches_Click(object sender, RoutedEventArgs e) => await TrackJobAsync(ExportMatchesCoreAsync);
    private async Task ExportMatchesCoreAsync()
    {
        if (session is not { } current) return;
        jobCancellation?.Cancel(); jobCancellation?.Dispose(); jobCancellation = new CancellationTokenSource();
        var token = jobCancellation.Token; SetBusy(true, "Preparing matching export…");
        try { var rows = await current.ReadMatchingAsync(ReadQuery(0, 10000), cancellationToken: token); await ExportAsync(current.Review(rows), cancellationToken: token); }
        catch (OperationCanceledException) { SetBusy(false, "Export cancelled."); }
        catch (Exception ex) { WorkflowDiagnostics.Failure("Audio.Operation", ex); SetBusy(false, ex.Message); }
        finally { if (jobCancellation?.Token == token) { jobCancellation.Dispose(); jobCancellation = null; } }
    }
    private async void ExportPartial_Click(object sender, RoutedEventArgs e) => await TrackJobAsync(ExportPartialCoreAsync);
    private Task ExportPartialCoreAsync() => PrepareSelectedAudioExportAsync(allowPartial: true);

    private async void ExportBank_Click(object sender, RoutedEventArgs e) => await TrackJobAsync(ExportBankCoreAsync);
    private async Task ExportBankCoreAsync()
    {
        var current = session; var row = CurrentRow;
        if (current is null || row?.SourceMedia is not { } media || media.BankKey.Length == 0) return;
        var dialog = new OpenFolderDialog { Title = "Choose bank export folder", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        jobCancellation?.Cancel(); jobCancellation?.Dispose(); jobCancellation = new CancellationTokenSource();
        SetBusy(true, "Exporting original bank…");
        lastReportSourceRoot = current.Snapshot.SourceRoot;
        try
        {
            lastReport = await current.ExportBankAsync(row.Media.BankKey, dialog.FolderName, cancellationToken: jobCancellation.Token);
            ActivityText.Text = $"{lastReport.Manifest.Entries[0].Status}{(lastReport.Manifest.Entries[0].Completeness == MediaCompleteness.Fragment ? " · Partial" : "")}\n{lastReport.ManifestPath}";
            SetBusy(false, "Bank export report saved.");
        }
        catch (AudioManifestWriteException ex)
        {
            lastReport = new AudioExportReport(ex.Manifest, ex.ManifestPath);
            ActivityText.Text = "Bank output retained · manifest could not be saved; in-memory results are available.";
            SetBusy(false, "Bank output retained; manifest write failed.");
        }
        catch (OperationCanceledException) { SetBusy(false, "Bank export cancelled."); }
        catch (Exception ex) { WorkflowDiagnostics.Failure("Audio.Operation", ex); SetBusy(false, ex.Message); }
        finally { jobCancellation?.Dispose(); jobCancellation = null; }
    }

    private async Task ExportAsync(AudioExportReview review, bool allowPartial = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = session; if (current is null || review.Rows.Count == 0) { StatusText.Text = "No audio resources selected."; return; }
        if (!ReferenceEquals(current.Snapshot, review.Snapshot))
            throw new ArchiveException("StaleReview", "The workspace changed. Select the clips again.");
        var dialog = new OpenFolderDialog { Title = "Choose audio export folder", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        cancellationToken.ThrowIfCancellationRequested();
        var choose = new AudioExportOptionsDialog(review, allowPartial);
        choose.Owner = this;
        if (choose.ShowDialog() != true) return;
        cancellationToken.ThrowIfCancellationRequested();
        var ownsJobCancellation = !cancellationToken.CanBeCanceled;
        if (ownsJobCancellation)
        {
            jobCancellation?.Cancel(); jobCancellation?.Dispose(); jobCancellation = new CancellationTokenSource();
            cancellationToken = jobCancellation.Token;
        }
        SetBusy(true, "Exporting audio…");
        lastReportSourceRoot = current.Snapshot.SourceRoot;
        try
        {
            using var progress = JobProgress();
            var report = await current.ExportAsync(review, choose.CreateOptions(dialog.FolderName, cache.EffectiveWorkers), progress, cancellationToken);
            lastReport = report;
            ActivityText.Text = $"{report.Manifest.Entries.Count(item => item.Status == AudioExportStatus.Completed):N0} completed · {report.Manifest.Entries.Count(item => item.Status == AudioExportStatus.Failed):N0} failed · {report.Manifest.Entries.Count(item => item.Status == AudioExportStatus.Skipped):N0} skipped\n{report.ManifestPath}";
            SetBusy(false, "Export report saved.");
        }
        catch (AudioManifestWriteException ex)
        {
            lastReport = new AudioExportReport(ex.Manifest, ex.ManifestPath);
            ActivityText.Text = "Outputs retained · manifest could not be saved; in-memory results are available.";
            SetBusy(false, "Outputs retained; manifest write failed.");
        }
        catch (OperationCanceledException) { SetBusy(false, "Export cancelled."); }
        catch (Exception ex) { WorkflowDiagnostics.Failure("Audio.Operation", ex); SetBusy(false, ex.Message); }
        finally
        {
            if (ownsJobCancellation && jobCancellation is not null) { jobCancellation.Dispose(); jobCancellation = null; }
        }
    }

    private async void Results_Click(object sender, RoutedEventArgs e)
    {
        await Task.CompletedTask;
        if (lastReport is not null) new AudioResultsWindow(lastReport,lastReportSourceRoot) { Owner = this }.ShowDialog();
    }
    private void Advanced_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireWorkspaceNavigation()) return;
        var app = (App)Application.Current; var ui = new WpfUiDispatcher(Dispatcher);
        var controller = app.ControllerFactory?.Invoke(ui) ?? new BrowserController(ui,cache.Location);
        var window = new MainWindow(controller);
        AttachInspectors(window);
        advancedWindows.Add((window,controller));
        window.Closed += (_,_) => advancedWindows.RemoveAll(item => ReferenceEquals(item.Window,window));
        app.WindowInitialized?.Invoke(window); window.Owner = this; window.Show();
    }
    private void AudioMode_Click(object sender,RoutedEventArgs e)
    {ShowBrowse("audio");}
    private async void StringsMode_Click(object sender,RoutedEventArgs e)
    {
        try{await ShowStringsAsync();}catch(Exception error){StatusText.Text=error.Message;}
    }
    private void Cache_Click(object sender, RoutedEventArgs e) => new AudioCacheWindow(cache, session?.Snapshot.SourceRoot, DrainCacheUsersAsync, ReopenAfterCacheChangeAsync) { Owner = this }.ShowDialog();

    private async Task DrainCacheUsersAsync(CancellationToken token)
    {
        ++workspaceTransitionDepth;
        try
        {
        await ClearPhysicsAsync();
        await ClearAudioDialogueAsync();
        await ClearCharactersAsync();
        jobCancellation?.Cancel(); openCancellation?.Cancel();
        await CancelQueryAsync();
        if (activeJob is not null) try { await activeJob; } catch { }
        try{await Task.WhenAll(pendingOpens.ToArray());}catch{}
        await stringsView.DisposeAsync();
        await ClearSceneSessionAsync();
        await animationView.ClearAsync();await shaderView.ClearAsync();
        await referencesView.ClearAsync();
        await ClearTextureCatalogsAsync();
        await ClearSharedCatalogReferencesAsync();
        await familyView.ClearAsync();
        await ClearBufferAsync();
        foreach (var item in advancedWindows.ToArray()) { await item.Controller.CloseAsync(true); item.Window.Close(); }
        await StopPlaybackAsync();
        var current = session; session = null;
        admittedWorkspace = null;
        if (current is not null) await current.DisposeAsync();
        token.ThrowIfCancellationRequested();
        }
        finally { --workspaceTransitionDepth; RefreshWorkspaceNavigation(); }
    }
    private async Task ReopenAfterCacheChangeAsync(CancellationToken token)
    { if (inputPath is not null) await OpenAsync(inputPath, token); }
    private void CancelAudioQueries()
    {
        CancelSelectedAudioDetails();
        recoveryFilterCancellation?.Cancel();
        filterTimer.Stop();
        audioQueryRefreshPending = false;
        ++audioQueryEpoch;
        ++queryGeneration;
        queryCancellation?.Cancel();
    }

    private async Task CancelQueryAsync()
    {
        CancelAudioQueries();
        await Task.WhenAll(audioQueryTasks.ToArray());
        if (audioQueryRefresh is { } refresh) await refresh;
    }

    private void SetBusy(bool busy, string message)
    {
        foregroundBusy = busy; foregroundStage = message; foregroundCompleted = foregroundTotal = 0;
        StatusText.Text = message;
        RefreshActivity();
        OpenButton.IsEnabled = !busy; RefreshButton.IsEnabled = !busy && inputPath is not null;
        ExportSelectedButton.IsEnabled = !busy; ExportMatchesButton.IsEnabled = !busy;
        PauseButton.Content = player.State == NAudio.Wave.PlaybackState.Paused ? "Resume" : "Pause";
    }
    private CoalescedProgress<AudioProgress> JobProgress() => new(value => Dispatcher.BeginInvoke(() =>
    {
        if (activeJob is not { IsCompleted: false }) return;
        foregroundStage = value.Phase; foregroundCompleted = value.Completed; foregroundTotal = value.Total;
        RefreshActivity();
    }));
    private async Task TrackJobAsync(Func<Task> start)
    {
        if (activeJob is { IsCompleted: false } || closingInProgress || workspaceTransitionDepth != 0) return;
        var task = start(); activeJob = task;
        try { await task; }
        finally { if (ReferenceEquals(activeJob, task)) activeJob = null; }
    }
    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelPhysics();
        jobCancellation?.Cancel(); openCancellation?.Cancel();
        CancelAudioQueries();
        var metadata = session;
        var metadataCancelled = metadata?.CancelEnrichment() == true;
        SetBusy(true, "Cancelling…");
        if (activeJob is not null) try { await activeJob; } catch { }
        try{await Task.WhenAll(pendingOpens.ToArray());}catch{}
        await CancelQueryAsync();
        if (metadataCancelled && metadata is not null) { try { await metadata.EnrichmentTask; } catch (OperationCanceledException) { } }
        await StopPlaybackAsync(); SetBusy(false, "Cancelled.");
    }
    private async Task PlayerShortcutAsync(Func<Task> action)
    {try{await action();}catch(Exception error){StatusText.Text=error.Message;}}
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if(Keyboard.Modifiers!=ModifierKeys.None || IsTextEditing(Keyboard.FocusedElement as DependencyObject) || !IsPlayerShortcutFocus(Keyboard.FocusedElement as DependencyObject)) return;
        if (e.Key == Key.Space && CurrentRow is { } row && (row.CanPreview||row.CanPreviewPartial))
        { e.Handled=true; _=PlayerShortcutAsync(playing?.Identity==row.Identity && player.State!=NAudio.Wave.PlaybackState.Stopped ? ()=>Task.Run(player.Toggle) : ()=>StartPreviewAsync(row,row.CanPreviewPartial)); }
        else if (e.Key == Key.Left && player.State != NAudio.Wave.PlaybackState.Stopped) { e.Handled = true; _=PlayerShortcutAsync(()=>Task.Run(() => player.Position = Math.Max(0, player.Position - 5))); }
        else if (e.Key == Key.Right && player.State != NAudio.Wave.PlaybackState.Stopped) { e.Handled = true; _=PlayerShortcutAsync(()=>Task.Run(() => player.Position = Math.Min(player.Length, player.Position + 5))); }
    }
    internal string CloseStage { get; private set; } = "Ready";
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closeApproved) return;
        e.Cancel = true;
        if (closingInProgress) return;
        closingInProgress = true;
        try { await CloseOwnedResourcesAsync(); }
        catch (Exception error) { RecordCloseFailure("Window", error); }
        finally
        {
            closeApproved = true;
            CloseStage = closeFailures.Count == 0 ? "Closed" : "Closed with errors";
            _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Normal);
        }
    }
    private async Task ClearSceneSessionAsync()
    {
        await sceneNavigationDrain;
        ClearSceneReturnState();
        await modelView.UnloadAsync();
        await sceneView.UnloadAsync();
        await ClearSceneRenderingAsync();
        if (sceneSession is not null)
        {
            await sceneSession.DisposeAsync();
            sceneSession = null;
        }
    }
    private async Task<ISceneCatalogSession> OpenDefaultSceneSessionAsync(
        ArchiveSnapshot snapshot, SceneDiscoveryOptions? options, CancellationToken token)
    {
        return await SceneCatalogSession.OpenAsync(snapshot, cache, aliases, options, token);
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private void UpdatePlaybackPresentation()
    {
        PauseButton.IsEnabled = playing is not null;
        SeekSlider.IsEnabled = playing is not null;
        PlayingLabel.Text=playing?.Label??""; ActionBadge.Text=playing?.Badge??"";
        ActionBadge.Visibility=ActionBadge.Text.Length==0?Visibility.Collapsed:Visibility.Visible;
        StringsPlayingLabel.Text=playing is null?"":$"{playing.Label}  {playing.Badge}";
        StringsPlayerSummary.Visibility=currentView!="audio"&&playing is not null?Visibility.Visible:Visibility.Collapsed;
    }
    private void Player_PlaybackStopped(object? sender,NAudio.Wave.StoppedEventArgs e)
    {
        var context=playing; if(context is null) return;
        _=Dispatcher.InvokeAsync(async()=>
        {
            if(!ReferenceEquals(context,playing)) return;
            await StopPlaybackAsync();
            StatusText.Text=e.Exception is null?"Stopped":e.Exception.Message;
        });
    }
    private static bool IsTextEditing(DependencyObject? target)
    {
        while(target is not null)
        {
            if(target is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or ComboBox or Slider or System.Windows.Controls.Primitives.ButtonBase) return true;
            target=target is System.Windows.Media.Visual?System.Windows.Media.VisualTreeHelper.GetParent(target):LogicalTreeHelper.GetParent(target);
        }
        return false;
    }
    private bool IsPlayerShortcutFocus(DependencyObject? target)
    {
        while(target is not null)
        {
            if(ReferenceEquals(target,MediaList)||ReferenceEquals(target,PlayerPanel)) return true;
            target=target is System.Windows.Media.Visual?System.Windows.Media.VisualTreeHelper.GetParent(target):LogicalTreeHelper.GetParent(target);
        }
        return false;
    }
}

