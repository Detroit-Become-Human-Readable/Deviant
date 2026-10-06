using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Browser;
using Deviant.Export;
using Deviant.Formats.Containers;
using Deviant.Formats.Scenes;
using Deviant.Graphs;
using Microsoft.Win32;

namespace Deviant.Desktop;

public sealed record SceneReferenceRow(int Ordinal, string Label, string Word0, string Word1, string Match,
    DependencyCandidate? Dependency = null, DcInfoDirectoryEntry? DirectoryEntry = null, AssetRelationship? ArchiveReference = null)
{
    public ResourceIdentity? NavigationTarget => ArchiveReference is { Reference.Namespace: IdentifierNamespace.Archive, Reference.ResourceType: not null } relationship
        ? relationship.NavigationTarget : null;
}

public partial class SceneReferencesView : UserControl, IAsyncDisposable
{
    private const int PageSize = 100;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource workspaceCancellation = new();
    private readonly SemaphoreSlim loadGate = new(1, 1);
    private CancellationTokenSource? loadCancellation, queryCancellation, jobCancellation;
    private ArchiveSnapshot? snapshot;
    private ResourceIdentity? identity;
    private byte[] originalBytes = [];
    private SourceExtent? originalExtent, dependencyExtent;
    private string? dependencyPath;
    private FileStamp? dependencyStamp;
    private SceneReferenceRow[] dependencies = [], directoryRows = [], archiveRows = [], matches = [];
    private readonly LocalizationReferenceReader referenceReader = new();
    private readonly HashSet<Task> navigationTasks = [];
    private CancellationTokenSource? navigationCancellation;
    private long referenceGeneration;
    private string[] inspectionDiagnostics = [];
    private long generation, queryGeneration;
    private int pageOffset;
    private bool updating;
    private Task? activeJob, activeQuery;
    private DependencyCache? dependencyCache;
    private sealed record DependencyCache(string Path, string IndexContentId, string Revision, FileStamp Stamp, string Sha256, byte[] Bytes);

    public SceneReferencesView() { InitializeComponent(); InitializeReferenceGraph(); }
    public event EventHandler? SelectedResourceChanged;
    public Func<ResourceIdentity, CancellationToken, Task>? RelatedNavigator { get; set; }
    public LocalizationReferenceReadResult? CurrentArchiveReferences { get; private set; }
    public SceneReferenceRow? SelectedReference => Rows.SelectedItem as SceneReferenceRow;
    public ResourceIdentity? SelectedIdentity => identity;
    public string? SelectedLabel => identity is null ? null : SourceName.Text;
    public SourceRevision? LoadedRevision => snapshot is null ? null : new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
    public DecodeResult<SceneDependencyInspection>? CurrentDependencyInspection { get; private set; }
    public DecodeResult<DcInfoDirectoryInspection>? CurrentDirectoryInspection { get; private set; }
    public IReadOnlyList<SceneReferenceRow> CurrentReferences => Rows.ItemsSource as SceneReferenceRow[] ?? [];

    public async Task LoadAsync(ArchiveSnapshot source, ResourceIdentity owner, CancellationToken token = default, string? sourceLabel = null)
    {
        if (owner.IndexContentId != source.IndexContentId || owner.MemberPath is not null)
            throw new ArgumentException("Select a top-level resource from this archive.", nameof(owner));
        var row = source.Records.SingleOrDefault(r => r.Ordinal == owner.Ordinal)
            ?? throw new InvalidOperationException("The resource is unavailable.");
        var version = ++generation; var cachedDependencies = dependencyCache;
        workspaceCancellation.Cancel(); workspaceCancellation.Dispose(); workspaceCancellation = new();
        loadCancellation?.Cancel(); queryCancellation?.Cancel(); jobCancellation?.Cancel(); ++queryGeneration;
        Reset(); SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
        var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspaceCancellation.Token);
        loadCancellation = own; var entered = false; Status.Text = "Reading references…";
        try
        {
            await loadGate.WaitAsync(own.Token); entered = true;
            var result = await Task.Run(async () =>
            {
                using var indexPin = SourceIdentity.OpenPinned(source.IndexPath); var indexProvider = new FileRevisionProvider();
                SourceIdentity.EnsureUnchanged(indexProvider, indexPin, source.IndexPath, source.IndexStamp);
                if (!source.IndexStamp.Reliable)
                {
                    var indexHash = await SourceIdentity.HashAsync(indexPin, source.IndexStamp.Length, own.Token);
                    if (!string.Equals(indexHash, source.IndexContentId, StringComparison.OrdinalIgnoreCase))
                        throw new ArchiveException("SourceChanged", "Archive index changed. Reopen it before continuing.");
                }
                var resolver = new ArchiveResourceResolver(source);
                var prefix = await resolver.ReadAsync(owner, 0, checked((int)Math.Min(8, row.StoredSize)), own.Token);
                var isContainer = prefix.AsSpan().SequenceEqual("COM_CONT"u8);
                byte[] raw = []; LocalizationReferenceReadResult? archiveReferences = null; var diagnostics = new List<string>();
                if (isContainer)
                {
                    try
                    {
                        await referenceReader.ValidateSnapshotIndexHashAsync(source, own.Token);
                        archiveReferences = await referenceReader.ReadAsync(source, owner, own.Token);
                        await referenceReader.ValidateReferenceTableHashAsync(source, archiveReferences, own.Token);
                        await referenceReader.ValidateSnapshotIndexHashAsync(source, own.Token);
                    }
                    catch (ArchiveException error) when (error.Code is "UnsupportedReferenceContainerVersion" or "ReferenceCountLimit" or "MalformedReferenceTable" or "ReferenceResourceSizeLimit")
                    { diagnostics.Add(error.Message); }
                }
                else if (row.StoredSize <= 16 * 1024 * 1024)
                    raw = await resolver.ReadAsync(owner, 0, checked((int)row.StoredSize), own.Token);
                else diagnostics.Add("Resource bytes exceed the inspection limit.");
                var volume = source.Volumes.Single(v => v.Package == row.Package);
                var extent = new SourceExtent(volume.RelativeName, row.Offset, row.StoredSize, !isContainer && row.StoredSize <= 16 * 1024 * 1024 ? Hash(raw) : null);
                var depPath = Path.ChangeExtension(source.IndexPath, ".dep");
                DecodeResult<SceneDependencyInspection>? dep = null; SourceExtent? depExtent = null; FileStamp? depStamp = null; DependencyCache? nextCache = null;
                if (File.Exists(depPath))
                {
                    using var depPin = SourceIdentity.OpenPinned(depPath); var provider = new FileRevisionProvider();
                    depStamp = provider.Capture(depPin, depPath);
                    if (depStamp.Length > new SceneDependencyLimits().MaximumBytes) throw new IOException("Dependency table exceeds the read limit.");
                    byte[] bytes;
                    var cacheMatches = cachedDependencies is { } cached && cached.Path == depPath &&
                        cached.IndexContentId == source.IndexContentId && cached.Revision == source.PhysicalSourceStamp && SourceIdentity.SameStamp(cached.Stamp, depStamp);
                    if (cacheMatches && (!depStamp.Reliable || !source.IndexStamp.Reliable))
                    {
                        var sourceHash = await SourceIdentity.HashAsync(depPin, depStamp.Length, own.Token);
                        cacheMatches = string.Equals(sourceHash, cachedDependencies!.Sha256, StringComparison.OrdinalIgnoreCase);
                    }
                    if (cacheMatches) bytes = cachedDependencies!.Bytes;
                    else
                    {
                        bytes = new byte[checked((int)depStamp.Length)];
                        await Deviant.Archives.ArchiveReader.ReadExactlyAsync(depPin, bytes, 0, own.Token);
                    }
                    SourceIdentity.EnsureUnchanged(provider, depPin, depPath, depStamp);
                    dep = new SceneDependencyReader().Inspect(bytes, source, owner, Path.GetFileName(depPath), token: own.Token);
                    depExtent = new(Path.GetFileName(depPath), 0, bytes.Length, Hash(bytes));
                    nextCache = new(depPath, source.IndexContentId, source.PhysicalSourceStamp, depStamp, depExtent.PayloadSha256!, bytes);
                }
                DecodeResult<DcInfoDirectoryInspection>? directory = null;
                var revision = new SourceRevision(source.IndexContentId, source.PhysicalSourceStamp);
                if (raw.AsSpan().StartsWith("DC_INFO "u8))
                    directory = new DcInfoDirectoryReader().Inspect(raw, owner, revision, [extent], token: own.Token);
                else if (raw.AsSpan().StartsWith("segs"u8))
                {
                    var container = new SegsContainerReader().Parse(raw, owner, revision, extent, row.AlternativeSize, cancellationToken: own.Token);
                    if (container.Value is { } decoded)
                        directory = new DcInfoDirectoryReader().Inspect(decoded.DecodedPrefix.AsSpan(0, Math.Min(decoded.DecodedPrefix.Length, new DcInfoDirectoryLimits().MaximumBytes)),
                            owner, revision, container.Extents, precedingSteps: container.Steps, token: own.Token);
                }
                own.Token.ThrowIfCancellationRequested();
                SourceIdentity.EnsureUnchanged(indexProvider, indexPin, source.IndexPath, source.IndexStamp);
                return (Raw: raw, Extent: extent, Dep: dep, Directory: directory, DepPath: depPath, DepExtent: depExtent, DepStamp: depStamp, Cache: nextCache, ArchiveReferences: archiveReferences, Diagnostics: diagnostics.ToArray());
            }, own.Token);
            own.Token.ThrowIfCancellationRequested(); if (version != generation) return;
            snapshot = source; identity = owner; originalBytes = result.Raw; originalExtent = result.Extent;
            dependencyCache = result.Cache; dependencyPath = result.DepPath; dependencyExtent = result.DepExtent; dependencyStamp = result.DepStamp;
            CurrentDependencyInspection = result.Dep; CurrentDirectoryInspection = result.Directory; CurrentArchiveReferences = result.ArchiveReferences; inspectionDiagnostics = result.Diagnostics;
            dependencies = result.Dep?.Value?.References.Select((r, i) => new SceneReferenceRow(i, $"Reference {i + 1}", Number(r.Words.Word0), Number(r.Words.Word1),
                r.TopLevelMatch.Status switch { ReferenceStatus.Unique => "Candidate", ReferenceStatus.Ambiguous => "Ambiguous", _ => "No top-level match" }, r)).ToArray() ?? [];
            directoryRows = result.Directory?.Value?.Entries.Select((r, i) => new SceneReferenceRow(i, $"Entry {i + 1}", Number(r.Word0), Number(r.Word1), "Directory pair", DirectoryEntry: r)).ToArray() ?? [];
            archiveRows = result.ArchiveReferences?.Relationships.Select((relationship, index) => new SceneReferenceRow(index, $"Reference {index + 1}",
                Number(relationship.Reference.ResourceType ?? 0), relationship.Reference.Id,
                relationship.Resolution.Status switch { ReferenceStatus.Unique => "Resolved", ReferenceStatus.Ambiguous => "Ambiguous", ReferenceStatus.Missing => "Missing", _ => "Unavailable" }, ArchiveReference: relationship)).ToArray() ?? [];
            ArchiveReferenceMode.IsEnabled = result.ArchiveReferences?.Status == LocalizationReferenceReadStatus.Verified && archiveRows.Length > 0;
            ArchiveReferenceMode.Visibility = ArchiveReferenceMode.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            SourceName.Text = sourceLabel ?? $"Resource {owner.Ordinal + 1}"; matches = dependencies; RenderPage();
            ExportOriginal.IsEnabled = true; ExportJson.IsEnabled = result.Dep?.Value is not null || result.Directory?.Value is not null || archiveRows.Length > 0;
            SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { if (version == generation) Status.Text = "Cancelled"; }
        catch (Exception error) { if (version == generation) Status.Text = error.Message; }
        finally { if (entered) loadGate.Release(); if (ReferenceEquals(loadCancellation, own)) loadCancellation = null; own.Dispose(); }
    }

    public Task FilterAsync(string text, bool directory = false, CancellationToken token = default) => FilterCoreAsync(text, directory ? 1 : 0, token);
    public Task FilterArchiveReferencesAsync(string text, CancellationToken token = default)
    {
        if (ArchiveReferenceMode.Visibility != Visibility.Visible) throw new InvalidOperationException("Archive reference data is unavailable.");
        return FilterCoreAsync(text, 2, token);
    }
    private Task FilterCoreAsync(string text, int mode, CancellationToken token)
    {
        queryCancellation?.Cancel();
        var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspaceCancellation.Token);
        queryCancellation = own; var version = ++queryGeneration; var source = mode == 2 ? archiveRows : mode == 1 ? directoryRows : dependencies;
        updating = true; try { Search.Text = text; Mode.SelectedIndex = mode; Word0Column.Visibility = Word1Column.Visibility = mode == 2 ? Visibility.Collapsed : Visibility.Visible; } finally { updating = false; }
        var task = ApplyAsync(); activeQuery = task; return task;
        async Task ApplyAsync()
        {
            try
            {
                var found = await Task.Run(() => source.Where(r =>
                {
                    own.Token.ThrowIfCancellationRequested();
                    return string.Join(' ', r.Label, r.Word0, r.Word1, r.Match, r.Dependency?.Words.ToString()).Contains(text.Trim(), StringComparison.OrdinalIgnoreCase);
                }).ToArray(), own.Token);
                if (own.IsCancellationRequested || version != queryGeneration) return;
                matches = found; pageOffset = 0; RenderPage();
            }
            catch (OperationCanceledException) { }
            finally { if (ReferenceEquals(queryCancellation, own)) queryCancellation = null; own.Dispose(); }
        }
    }
    private async void SearchChanged(object sender, TextChangedEventArgs e) { if (!updating && snapshot is not null) await FilterCoreAsync(Search.Text, Mode.SelectedIndex, CancellationToken.None); }
    private async void ModeChanged(object sender, SelectionChangedEventArgs e) { if (!updating && snapshot is not null) await FilterCoreAsync(Search.Text, Mode.SelectedIndex, CancellationToken.None); }
    private void RenderPage()
    {
        ++referenceGeneration; navigationCancellation?.Cancel();
        updating = true; try { Rows.ItemsSource = matches.Skip(pageOffset).Take(PageSize).ToArray(); } finally { updating = false; }
        Page.Text = $"{(matches.Length == 0 ? 0 : pageOffset + 1):N0}–{Math.Min(pageOffset + PageSize, matches.Length):N0} / {matches.Length:N0}";
        Previous.IsEnabled = pageOffset > 0; Next.IsEnabled = pageOffset + PageSize < matches.Length; ShowAdvanced(); UpdateListStatus();
        RefreshReferenceGraph();
    }
    private void UpdateListStatus()
    {
        var count = matches.Length;
        Status.Text = Mode.SelectedIndex switch
        {
            2 => $"{count:N0} archive {(count == 1 ? "reference" : "references")}",
            1 => CurrentDirectoryInspection?.Value is null ? "Directory unavailable" : $"{count:N0} directory {(count == 1 ? "entry" : "entries")}",
            _ => CurrentDependencyInspection?.Value is null ? "Dependency table unavailable" : $"{count:N0} {(count == 1 ? "dependency" : "dependencies")}"
        };
    }
    private void ReferenceSelected(object sender, SelectionChangedEventArgs e) { if (!updating) { ++referenceGeneration; navigationCancellation?.Cancel(); SyncGraphSelection(); ShowAdvanced(); } }
    private void ShowAdvanced()
    {
        OpenReference.IsEnabled = CanNavigate(SelectedReference);
        if (identity is null) { Advanced.Text = ""; return; }
        var text = $"Resource {identity.Ordinal}\nIndex {identity.IndexContentId}\nRevision {snapshot!.PhysicalSourceStamp}\n";
        text += string.Join("\n", new[] { originalExtent, dependencyExtent }.OfType<SourceExtent>().Select(e => $"Physical source {e.RelativeSource}: {e.Offset} + {e.Length}" + (e.PayloadSha256 is null ? "" : $"\nSHA-256 {e.PayloadSha256}")));
        if ((Rows.SelectedItem as SceneReferenceRow)?.Dependency is { } dependency)
            text += $"\nRaw words: {dependency.Words.Word0}, {dependency.Words.Word1}, {dependency.Words.Word2}, {dependency.Words.Word3}, {dependency.Words.Word4}\nDependency extent: {dependency.Extent.Offset} + {dependency.Extent.Length}\nCandidate matches: {dependency.TopLevelMatch.Candidates.Count}\n" + string.Join("\n", dependency.TopLevelMatch.Candidates.Select(c => c.Key));
        if ((Rows.SelectedItem as SceneReferenceRow)?.DirectoryEntry is { } entry)
            text += $"\nRaw pair: {entry.Word0}, {entry.Word1}\nDecoded extent: {entry.Extent.Offset} + {entry.Extent.Length}";
        if (CurrentDirectoryInspection?.Value is { } directory)
            text += $"\nDirectory unknown word: {directory.UnknownWord}\nDirectory count: {directory.DeclaredEntryCount}\nDuplicate pairs: {directory.Duplicates.Count}\nDecoded directory: {directory.Extent.Offset} + {directory.Extent.Length}";
        if (SelectedReference?.ArchiveReference is { } relationship)
        {
            var reference = relationship.Reference; var evidence = relationship.Evidence;
            text += $"\nArchive type {reference.ResourceType}\nArchive ID {reference.Id}\nResolution {relationship.Resolution.Status}\nCandidates {relationship.Resolution.Candidates.Count}\nSource {evidence.Source}\nTrailer 0x{evidence.SourceReferenceTrailer:X2}\n";
            if (evidence.PhysicalExtent is { } referenceExtent) text += $"Reference extent {referenceExtent.RelativeSource}: {referenceExtent.Offset} + {referenceExtent.Length}\nReference SHA-256 {referenceExtent.PayloadSha256}\n";
            text += string.Join("\n", relationship.Resolution.Candidates.Select(candidate => candidate.Key));
        }
        if (CurrentArchiveReferences is { } archiveReferences) text += $"\nReference table SHA-256 {archiveReferences.ReferenceTableSha256}";
        text += "\n" + string.Join("\n", (CurrentDependencyInspection?.Diagnostics ?? []).Concat(CurrentDirectoryInspection?.Diagnostics ?? []).Concat(inspectionDiagnostics));
        Advanced.Text = text;
    }
    private bool CanNavigate(SceneReferenceRow? row)
    {
        if (RelatedNavigator is null || snapshot is not { } source || identity is null || CurrentArchiveReferences is not { Status: LocalizationReferenceReadStatus.Verified } references ||
            row?.ArchiveReference is not { } relationship || row.NavigationTarget is not { } target ||
            !references.Relationships.Contains(relationship) || relationship.Source != identity || relationship.Reference.Owner != identity ||
            target.IndexContentId != source.IndexContentId || target.MemberPath is not null) return false;
        var record = target.Ordinal >= 0 && target.Ordinal < source.Records.Count && source.Records[(int)target.Ordinal].Ordinal == target.Ordinal
            ? source.Records[(int)target.Ordinal] : source.Records.SingleOrDefault(candidate => candidate.Ordinal == target.Ordinal);
        return record is not null && record.Type == relationship.Reference.ResourceType && uint.TryParse(relationship.Reference.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && record.Id == id;
    }
    public Task OpenSelectedReferenceAsync(CancellationToken token = default)
    {
        var task = NavigateReferenceAsync(token); navigationTasks.Add(task); _ = ObserveNavigationAsync(task); return task;
    }
    private async Task ObserveNavigationAsync(Task task) { try { await task; } catch (Exception) { } finally { navigationTasks.Remove(task); } }
    private async Task NavigateReferenceAsync(CancellationToken token)
    {
        if (!CanNavigate(SelectedReference)) return;
        var row = SelectedReference!; var target = row.NavigationTarget!; var source = snapshot!; var owner = identity!;
        var references = CurrentArchiveReferences!; var sourceVersion = generation; var selectionVersion = referenceGeneration; var navigate = RelatedNavigator!;
        navigationCancellation?.Cancel(); var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); navigationCancellation = own;
        using var validation = CancellationTokenSource.CreateLinkedTokenSource(own.Token, workspaceCancellation.Token);
        try
        {
            await Task.Run(async () =>
            {
                await referenceReader.ValidateSnapshotIndexHashAsync(source, validation.Token);
                await referenceReader.ValidateReferenceTableHashAsync(source, references, validation.Token);
                await referenceReader.ValidateSnapshotIndexHashAsync(source, validation.Token);
            }, validation.Token);
            own.Token.ThrowIfCancellationRequested();
            if (sourceVersion != generation || selectionVersion != referenceGeneration || !ReferenceEquals(source, snapshot) || owner != identity || !ReferenceEquals(row, SelectedReference)) return;
            // Routing may replace this inspector's source. The row epoch guards the handoff;
            // the callback token remains independent of the old workspace read token.
            await navigate(target, own.Token);
        }
        finally { if (ReferenceEquals(navigationCancellation, own)) navigationCancellation = null; own.Dispose(); }
    }
    private async Task OpenReferenceSafelyAsync()
    {
        var version = generation; var selection = referenceGeneration;
        try { await OpenSelectedReferenceAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (version == generation && selection == referenceGeneration) Status.Text = error.Message; }
    }
    private async void OpenReference_Click(object sender, RoutedEventArgs e) => await OpenReferenceSafelyAsync();
    private async void ReferenceKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter && CanNavigate(SelectedReference)) { e.Handled = true; await OpenReferenceSafelyAsync(); } }
    private async void ReferenceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject origin || ItemsControl.ContainerFromElement(Rows, origin) is not DataGridRow) return;
        if (CanNavigate(SelectedReference)) { e.Handled = true; await OpenReferenceSafelyAsync(); }
    }
    private void Previous_Click(object sender, RoutedEventArgs e) { if (pageOffset >= PageSize) { pageOffset -= PageSize; RenderPage(); } }
    private void Next_Click(object sender, RoutedEventArgs e) { if (pageOffset + PageSize < matches.Length) { pageOffset += PageSize; RenderPage(); } }
    private async void ExportOriginal_Click(object sender, RoutedEventArgs e) => await ExportAsync(AssetExportFormat.Original);
    private async void ExportJson_Click(object sender, RoutedEventArgs e) => await ExportAsync(AssetExportFormat.Json);
    private async Task ExportAsync(AssetExportFormat format)
    {
        if (identity is null || activeJob is { IsCompleted: false }) return;
        var dialog = new SaveFileDialog { Title = "Export resource references", Filter = format == AssetExportFormat.Json ? "JSON (*.json)|*.json" : "Binary (*.bin)|*.bin", FileName = $"Resource-{identity.Ordinal + 1}" + (format == AssetExportFormat.Json ? ".json" : ".bin"), OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var version = generation;
        try { await ExportToAsync(format, dialog.FileName, true); if (version == generation) Status.Text = "Exported"; }
        catch (OperationCanceledException) { if (version == generation) Status.Text = "Export cancelled"; }
        catch (Exception error) { if (version == generation) Status.Text = error.Message; }
    }

    public async Task ExportToAsync(AssetExportFormat format, string destination, bool overwrite = false, CancellationToken token = default)
    {
        var source = snapshot ?? throw new InvalidOperationException("Open a workspace first.");
        var owner = identity ?? throw new InvalidOperationException("Select a resource first.");
        var extent = originalExtent ?? throw new InvalidOperationException("The resource source is unavailable.");
        var graph = GraphToggle.IsChecked == true ? CurrentReferenceGraph : null;
        if (format is not (AssetExportFormat.Original or AssetExportFormat.Json or AssetExportFormat.Svg)) throw new ArgumentOutOfRangeException(nameof(format));
        if (format == AssetExportFormat.Svg && graph is null) throw new InvalidOperationException("Open an archive reference graph first.");
        if (format == AssetExportFormat.Json && CurrentDependencyInspection?.Value is null && CurrentDirectoryInspection?.Value is null && archiveRows.Length == 0)
            throw new InvalidOperationException("Reference data is unavailable.");
        if (activeJob is { IsCompleted: false }) throw new InvalidOperationException("An export is already running.");
        var raw = originalBytes; var dep = CurrentDependencyInspection; var directory = CurrentDirectoryInspection; var archiveReferences = CurrentArchiveReferences;
        var depPath = dependencyPath; var depStamp = dependencyStamp; var depExtent = dependencyExtent;
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspaceCancellation.Token);
        jobCancellation = own;
        async Task CheckMetadataAsync(CancellationToken cancellation)
        {
            if (archiveReferences is { Status: LocalizationReferenceReadStatus.Verified })
            {
                await referenceReader.ValidateSnapshotIndexHashAsync(source, cancellation);
                await referenceReader.ValidateReferenceTableHashAsync(source, archiveReferences, cancellation);
            }
            if (format != AssetExportFormat.Json || graph is not null || depPath is null || depStamp is null || depExtent?.PayloadSha256 is null) return;
            using var pin = SourceIdentity.OpenPinned(depPath); var provider = new FileRevisionProvider();
            SourceIdentity.EnsureUnchanged(provider, pin, depPath, depStamp);
            var digest = await SourceIdentity.HashAsync(pin, depStamp.Length, cancellation);
            if (!string.Equals(digest, depExtent.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                throw new ArchiveException("SourceChanged", "The dependency table changed. Reopen it before exporting.");
            SourceIdentity.EnsureUnchanged(provider, pin, depPath, depStamp);
        }
        async Task WriteJsonAsync(Stream stream, CancellationToken cancellation)
        {
            var revision = new SourceRevision(source.IndexContentId, source.PhysicalSourceStamp);
            if (graph is not null && format == AssetExportFormat.Svg) await EvidenceGraphExport.WriteSvgAsync(graph, revision, stream, graph.Nodes.Count, cancellation);
            else if (graph is not null) await EvidenceGraphExport.WriteJsonAsync(graph, revision, stream, cancellation);
            else await JsonSerializer.SerializeAsync(stream, new { Identity = owner, Revision = revision, OriginalExtent = extent, Dependencies = dep, Directory = directory, ArchiveReferences = archiveReferences }, new JsonSerializerOptions { WriteIndented = true }, cancellation);
        }
        Task task;
        if (extent.PayloadSha256 is null)
        {
            if (format == AssetExportFormat.Original)
            {
                task = Task.Run(async () =>
                {
                    await CheckMetadataAsync(own.Token);
                    var exporter = new RawExportService(); var full = Path.GetFullPath(destination);
                    var row = source.Records.Single(item => item.Ordinal == owner.Ordinal);
                    var plan = exporter.Plan(source, new(Path.GetDirectoryName(full)!, overwrite), [row], own.Token);
                    plan = plan with { Items = [plan.Items[0] with { RelativeOutput = Path.GetFileName(full) }] };
                    var report = await exporter.RunAsync(plan, cancellationToken: own.Token); var entry = report.Manifest.Entries.Single();
                    if (entry.Status == ExportStatus.Cancelled) throw new OperationCanceledException(own.Token);
                    if (entry.Status != ExportStatus.Completed) throw new ArchiveException(entry.ErrorCode ?? "ExportFailed", entry.Error ?? "The output could not be completed.");
                }, own.Token);
            }
            else task = WriteMetadataJsonAsync(source, owner, destination, overwrite, WriteJsonAsync, CheckMetadataAsync, own.Token);
        }
        else
        {
            task = InspectorFileExport.WriteAsync(source, owner, new(source.IndexContentId, source.PhysicalSourceStamp), extent,
                destination, overwrite, async (stream, cancellation) =>
                {
                    if (!string.Equals(extent.PayloadSha256, Hash(raw), StringComparison.OrdinalIgnoreCase)) throw new IOException("The resource payload changed.");
                    if (format == AssetExportFormat.Original) await stream.WriteAsync(raw, cancellation);
                    else await WriteJsonAsync(stream, cancellation);
                }, own.Token, lifetime.Token, additionalValidation: CheckMetadataAsync);
        }
        activeJob = task;
        try { await task; }
        finally { if (ReferenceEquals(activeJob, task)) activeJob = null; if (ReferenceEquals(jobCancellation, own)) jobCancellation = null; }
    }

    private Task WriteMetadataJsonAsync(ArchiveSnapshot source, ResourceIdentity owner, string destination, bool overwrite,
        Func<Stream, CancellationToken, Task> write, Func<CancellationToken, Task> validateMetadata, CancellationToken token) => Task.Run(async () =>
    {
        var full = Path.GetFullPath(destination); var directory = Path.GetDirectoryName(full)!;
        OutputSafety.ValidateFilePath(full, source.SourceRoot); if (!overwrite && File.Exists(full)) throw new IOException("The output file already exists.");
        Directory.CreateDirectory(directory); using var outputPin = OutputSafety.PinDirectory(directory);
        using var indexPin = SourceIdentity.OpenPinned(source.IndexPath);
        var row = source.Records.Single(item => item.Ordinal == owner.Ordinal); var volume = source.Volumes.Single(item => item.Package == row.Package);
        using var volumePin = SourceIdentity.OpenPinned(volume.FilePath); var revisions = new FileRevisionProvider();
        async Task ValidateAsync()
        {
            token.ThrowIfCancellationRequested(); SourceIdentity.EnsureUnchanged(revisions, indexPin, source.IndexPath, source.IndexStamp);
            SourceIdentity.EnsureUnchanged(revisions, volumePin, volume.FilePath, volume.Stamp);
            if (await SourceIdentity.HashAsync(indexPin, source.IndexStamp.Length, token) != source.IndexContentId) throw new ArchiveException("SourceChanged", "The archive index changed. Reopen it before exporting.");
            await validateMetadata(token);
            SourceIdentity.EnsureUnchanged(revisions, indexPin, source.IndexPath, source.IndexStamp); SourceIdentity.EnsureUnchanged(revisions, volumePin, volume.FilePath, volume.Stamp);
        }
        await ValidateAsync(); var temporary = Path.Combine(directory, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        OutputSafety.ValidateFilePath(temporary, source.SourceRoot);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            { await write(stream, token); await stream.FlushAsync(token); stream.Flush(true); }
            await ValidateAsync(); token.ThrowIfCancellationRequested(); OutputSafety.ValidateFilePath(full, source.SourceRoot); File.Move(temporary, full, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, token);
    private void Cancel_Click(object sender, RoutedEventArgs e) { loadCancellation?.Cancel(); queryCancellation?.Cancel(); jobCancellation?.Cancel(); navigationCancellation?.Cancel(); }
    private void Reset()
    {
        snapshot = null; identity = null; originalExtent = dependencyExtent = null; dependencyStamp = null; dependencyPath = null;
        originalBytes = []; CurrentDependencyInspection = null; CurrentDirectoryInspection = null; CurrentArchiveReferences = null; dependencies = directoryRows = archiveRows = matches = []; inspectionDiagnostics = []; pageOffset = 0; ++referenceGeneration;
        updating = true;
        try { Rows.ItemsSource = null; Search.Text = ""; Mode.SelectedIndex = 0; Word0Column.Visibility = Word1Column.Visibility = Visibility.Visible; ArchiveReferenceMode.Visibility = Visibility.Collapsed; ArchiveReferenceMode.IsEnabled = false; OpenReference.IsEnabled = false; SourceName.Text = "Select a resource in Explorer"; Advanced.Text = Page.Text = Status.Text = ""; Previous.IsEnabled = Next.IsEnabled = ExportOriginal.IsEnabled = ExportJson.IsEnabled = false; }
        finally { updating = false; }
        ResetReferenceGraph();
    }
    public async Task ClearAsync()
    {
        ++generation; ++queryGeneration; dependencyCache = null; workspaceCancellation.Cancel(); loadCancellation?.Cancel(); queryCancellation?.Cancel(); jobCancellation?.Cancel();
        navigationCancellation?.Cancel();
        Reset(); SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
        var navigation = navigationTasks.ToArray(); try { await Task.WhenAll(navigation); } catch (Exception) { }
        var job = activeJob; var query = activeQuery;
        if (job is not null) try { await job; } catch (Exception) { }
        if (query is not null) try { await query; } catch (OperationCanceledException) { }
        await loadGate.WaitAsync(); loadGate.Release();
    }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); await ClearAsync(); }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Number(uint value) => value.ToString(CultureInfo.InvariantCulture) + $" / 0x{value:X8}";
}
