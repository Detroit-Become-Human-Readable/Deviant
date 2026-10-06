using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Export;
using Microsoft.Win32;

namespace Deviant.Desktop;

public sealed record AssetFamilyChoice(ResourceIdentity Identity, ResourceRecord Resource, string Label, int Number, SourceExtent Extent)
{
    public string SizeLabel => Resource.StoredSize < 0 ? "—" : Resource.StoredSize.ToString("N0", CultureInfo.CurrentCulture);
}
public sealed record AssetFamilyGroupChoice(string Key,string Label,IReadOnlyList<ResourceIdentity> Members)
{public override string ToString()=>Label;}
public sealed record AssetFamilyRelatedChoice(string Key,string Label,string Status,bool CanNavigate);

public partial class AssetFamilyView : UserControl, IAsyncDisposable
{
    private const int PageSize = 100;
    private readonly CancellationTokenSource lifetime = new();
    private readonly IExportService exporter;
    private readonly SemaphoreSlim queryGate = new(1, 1);
    private CancellationTokenSource workspace = new();
    private CancellationTokenSource? loadCancellation, queryCancellation, exportCancellation;
    private readonly HashSet<Task> operations = [];
    private ArchiveSnapshot? snapshot;
    private AssetFamilyChoice[] resources = [], matches = [];
    private Task? activeExport;
    private Task queryTask = Task.CompletedTask;
    private int pageOffset;
    private string renderedGroupKey="";
    private long loadGeneration, queryGeneration, selectionGeneration;
    private bool updating;
    private readonly Dictionary<string,HashSet<ResourceIdentity>> groupMembers=new(StringComparer.Ordinal);
    public Func<string,CancellationToken,Task>? GroupValidator {get;set;}
    public Action? DiscoveryCancel {get;set;}
    public Func<string,Task>? RelatedNavigator {get;set;}

    public AssetFamilyView() : this(new RawExportService()) { }
    public AssetFamilyView(IExportService exporter)
    {
        this.exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        InitializeComponent(); updating = true;
        try { Sort.ItemsSource = new[] { "Source order", "Name", "Smallest first", "Largest first" }; Sort.SelectedIndex = 0; }
        finally { updating = false; }
    }
    public event EventHandler? SelectedResourceChanged;
    public string PreviewUnavailableReason { get; set; } = "No decoder is available.";
    public ResourceIdentity? SelectedIdentity => (Rows.SelectedItem as AssetFamilyChoice)?.Identity;
    public string? SelectedLabel => (Rows.SelectedItem as AssetFamilyChoice)?.Label;
    public AssetFamilyChoice? SelectedResource => Rows.SelectedItem as AssetFamilyChoice;
    public SourceRevision? LoadedRevision => snapshot is null ? null : new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
    public IReadOnlyList<AssetFamilyChoice> CurrentResources => Rows.ItemsSource as AssetFamilyChoice[] ?? [];
    public int TotalMatches => matches.Length;
    public long SelectionVersion => selectionGeneration;

    public Task LoadAsync(ArchiveSnapshot source, IEnumerable<uint> types, string label, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(types);
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A family label is required.", nameof(label));
        ObjectDisposedException.ThrowIf(lifetime.IsCancellationRequested, this);
        return Track(LoadCoreAsync(source, types.ToHashSet(), label.Trim(), token));
    }
    private async Task LoadCoreAsync(ArchiveSnapshot source, HashSet<uint> types, string label, CancellationToken token)
    {
        var generation = ++loadGeneration; ++queryGeneration; ++selectionGeneration;
        workspace.Cancel(); workspace.Dispose(); workspace = new();
        loadCancellation?.Cancel(); queryCancellation?.Cancel(); exportCancellation?.Cancel();
        ResetView(); FamilyName.Text = label; Status.Text = "Reading assets…";
        var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspace.Token); loadCancellation = own;
        try
        {
            var choices = await Task.Run(() =>
            {
                var volumes = source.Volumes.ToDictionary(item => item.Package); var rows = new List<AssetFamilyChoice>();
                foreach (var resource in source.Records)
                {
                    own.Token.ThrowIfCancellationRequested(); if (!types.Contains(resource.Type)) continue;
                    var number = rows.Count + 1;
                    var relativeSource = volumes.TryGetValue(resource.Package, out var volume) ? volume.RelativeName : resource.VolumeName;
                    rows.Add(new(new(source.IndexContentId, resource.Ordinal), resource, $"{label} {number}", number, new(relativeSource, resource.Offset, resource.StoredSize)));
                }
                return rows.ToArray();
            }, own.Token);
            own.Token.ThrowIfCancellationRequested(); if (generation != loadGeneration) return;
            snapshot = source; resources = matches = choices; RenderPage(); Status.Text = $"{resources.Length:N0} assets";
        }
        catch (OperationCanceledException) { if (generation == loadGeneration) Status.Text = "Cancelled"; }
        catch (Exception error) { if (generation == loadGeneration) Status.Text = error.Message; }
        finally { if (ReferenceEquals(loadCancellation, own)) loadCancellation = null; own.Dispose(); }
    }

    public Task RefreshAsync(CancellationToken token = default)
    {
        queryCancellation?.Cancel();
        var group=(Groups.SelectedItem as AssetFamilyGroupChoice)?.Key??"";
        var members=groupMembers.TryGetValue(group,out var memberSet)?memberSet:null;
        if(group!=renderedGroupKey){matches=[];pageOffset=0;RenderPage();Status.Text="Searching…";}
        queryTask = Track(QueryAsync(Search.Text.Trim(), Sort.SelectedIndex, TypeFilter.Text.Trim(), IdFilter.Text.Trim(), members,group, token)); return queryTask;
    }
    private async Task QueryAsync(string text, int sort, string typeText, string idText,HashSet<ResourceIdentity>? members,string groupKey, CancellationToken token)
    {
        var source = snapshot; if (source is null) return;
        var generation = ++queryGeneration; var current = resources;
        var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspace.Token); queryCancellation = own; var entered = false;
        try
        {
            if (!TryNumericFilter(typeText, out var type) || !TryNumericFilter(idText, out var id)) throw new ArgumentException("Enter a decimal or 0x hexadecimal Type and ID.");
            await queryGate.WaitAsync(own.Token); entered = true;
            var found = await Task.Run(() =>
            {
                var filtered = current.Where(item =>
                {
                    own.Token.ThrowIfCancellationRequested();
                    return (members is null||members.Contains(item.Identity))&&(type is null || item.Resource.Type == type) && (id is null || item.Resource.Id == id) &&
                        (item.Label.Contains(text, StringComparison.OrdinalIgnoreCase) || item.Extent.RelativeSource.Contains(text, StringComparison.OrdinalIgnoreCase));
                });
                var result = sort switch
                {
                    1 => filtered.OrderBy(item => item.Number),
                    2 => filtered.OrderBy(item => item.Resource.StoredSize).ThenBy(item => item.Number),
                    3 => filtered.OrderByDescending(item => item.Resource.StoredSize).ThenBy(item => item.Number),
                    _ => filtered.OrderBy(item => item.Resource.Ordinal)
                };
                return result.ToArray();
            }, own.Token);
            own.Token.ThrowIfCancellationRequested(); if (generation != queryGeneration || !ReferenceEquals(source, snapshot)) return;
            matches = found;renderedGroupKey=groupKey; pageOffset = 0; RenderPage(); Status.Text = $"{matches.Length:N0} assets";
        }
        catch (OperationCanceledException) { if(generation==queryGeneration)Status.Text="Cancelled"; }
        catch (Exception error) { if (generation == queryGeneration) Status.Text = error.Message; }
        finally { if (entered) queryGate.Release(); if (ReferenceEquals(queryCancellation, own)) queryCancellation = null; own.Dispose(); }
    }
    private static bool TryNumericFilter(string text, out uint? value)
    {
        value = null; if (text.Length == 0) return true;
        var hexadecimal = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!uint.TryParse(hexadecimal ? text[2..] : text, hexadecimal ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
        value = parsed; return true;
    }
    public async Task SelectResourceAsync(ResourceIdentity identity, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); var index = Array.FindIndex(resources, item => item.Identity == identity);
        if (index < 0) throw new InvalidOperationException("The asset is unavailable.");
        queryCancellation?.Cancel(); ++queryGeneration;
        updating = true;
        try { Search.Text = TypeFilter.Text = IdFilter.Text = ""; Sort.SelectedIndex = 0;Groups.SelectedIndex=0; }
        finally { updating = false; }
        matches = resources;
        renderedGroupKey="";
        pageOffset = index / PageSize * PageSize; RenderPage();
        Rows.SelectedItem = matches[index]; Rows.ScrollIntoView(matches[index]); await Task.CompletedTask;
    }
    private void RenderPage()
    {
        updating = true;
        try
        {
            Rows.ItemsSource = matches.Skip(pageOffset).Take(PageSize).ToArray();
            if(SelectedResource is{}choice&&!CurrentResources.Contains(choice))Rows.SelectedItem=null;
        }
        finally { updating = false; }
        ClearDetails(); ++selectionGeneration;PopulateDetails(); SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
        Page.Text = $"{(matches.Length == 0 ? 0 : pageOffset + 1):N0}–{Math.Min(pageOffset + PageSize, matches.Length):N0} / {matches.Length:N0}";
        Previous.IsEnabled = pageOffset > 0; Next.IsEnabled = pageOffset + PageSize < matches.Length;
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating) return; ClearDetails(); ++selectionGeneration;PopulateDetails(); SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
    }
    private void PopulateDetails()
    {
        if (SelectedResource is not { } choice || snapshot is not { } source) return;
        SelectedName.Text = choice.Label; PreviewStatus.Visibility = Visibility.Visible;
        var volume = source.Volumes.FirstOrDefault(item => item.Package == choice.Resource.Package);
        var group=(Groups.SelectedItem as AssetFamilyGroupChoice)?.Key??"";
        var inGroup=group.Length==0||groupMembers.TryGetValue(group,out var members)&&members.Contains(choice.Identity);
        var available = inGroup&&choice.Resource.Validity == ResourceValidity.Valid && volume?.Stamp.Exists == true && volume.Stamp.Error is null && choice.Resource.StoredSize >= 0;
        ExportOriginal.IsEnabled = available;
        Advanced.Text = $"Resource {choice.Resource.Ordinal}\nType {choice.Resource.Type} (0x{choice.Resource.Type:X})\nID {choice.Resource.Id} (0x{choice.Resource.Id:X8})\nPackage {choice.Resource.Package}\nFlags 0x{choice.Resource.Flags:X8}\nAlternative size {choice.Resource.AlternativeSize}\n" +
            $"Source {choice.Extent.RelativeSource}: {choice.Extent.Offset} + {choice.Extent.Length}\nIndex {source.IndexContentId}\nRevision {source.PhysicalSourceStamp}\n" +
            PreviewUnavailableReason + "\n" +
            (choice.Resource.Diagnostic ?? "") + (!available ? "\nOriginal export requires an available source range." : "");
    }
    private void ClearDetails() { SelectedName.Text = "Select an asset"; Advanced.Text = RelatedEvidence.Text = "";RelatedRows.ItemsSource=null;RelatedPanel.Visibility=Visibility.Collapsed; ExportOriginal.IsEnabled = false; PreviewStatus.Visibility = Visibility.Collapsed; }
    private void ResetView()
    {
        snapshot = null; resources = matches = []; pageOffset = 0;renderedGroupKey="";
        updating = true;
        try { Rows.ItemsSource = null; Search.Text = TypeFilter.Text = IdFilter.Text = ""; Sort.SelectedIndex = 0;Groups.ItemsSource=null;Groups.Visibility=Visibility.Collapsed;groupMembers.Clear();GroupValidator=null;ClearDetails(); Page.Text = Status.Text = ""; Previous.IsEnabled = Next.IsEnabled = false; }
        finally { updating = false; }
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
    }
    private async void FiltersChanged(object sender, TextChangedEventArgs e) { if (!updating && snapshot is not null) await RefreshAsync(); }
    private async void SortChanged(object sender, SelectionChangedEventArgs e) { if (!updating && snapshot is not null) await RefreshAsync(); }
    private async void GroupChanged(object sender,SelectionChangedEventArgs e){if(!updating&&snapshot is not null)await RefreshAsync();}
    private void Previous_Click(object sender, RoutedEventArgs e) { if (pageOffset >= PageSize) { pageOffset -= PageSize; RenderPage(); } }
    private void Next_Click(object sender, RoutedEventArgs e) { if (pageOffset + PageSize < matches.Length) { pageOffset += PageSize; RenderPage(); } }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedResource is null || activeExport is { IsCompleted: false }) return;
        var fileName = NativeImageBrowser?.CurrentSource?.SelectedIdentity == SelectedResource.Identity
            ? $"Texture-{SelectedResource.Resource.Type:X4}-{SelectedResource.Resource.Id:X8}-{SelectedResource.Identity.Ordinal}.bin"
            : (SelectedLabel ?? "Asset") + ".bin";
        var dialog = new SaveFileDialog { Title = "Export asset", Filter = "Binary (*.bin)|*.bin", FileName = fileName, OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var generation = selectionGeneration;
        try
        {
            var report = await ExportToAsync(dialog.FileName, true);
            if (generation == selectionGeneration) ShowExportSummary(report.Manifest);
        }
        catch (ManifestWriteException error) { if (generation == selectionGeneration) ShowExportSummary(error.Manifest, reportFailed: true); }
        catch (OperationCanceledException) { if (generation == selectionGeneration) Status.Text = "Export cancelled"; }
        catch (Exception error) { if (generation == selectionGeneration) Status.Text = error.Message; }
    }
    private void ShowExportSummary(ExportManifest manifest, bool reportFailed = false)
    {
        var entries = manifest.Entries;
        Status.Text = $"{entries.Count(item => item.Status == ExportStatus.Completed)} completed · {entries.Count(item => item.Status == ExportStatus.Failed)} failed · {entries.Count(item => item.Status == ExportStatus.Cancelled)} cancelled";
        var skipped = entries.Count(item => item.Status == ExportStatus.Skipped); if (skipped > 0) Status.Text += $" · {skipped} skipped";
        if (reportFailed) Status.Text += " · Report could not be saved";
        Advanced.Text += "\n" + string.Join("\n", entries.Select(item => item.Status == ExportStatus.Completed ? $"SHA-256 {item.Sha256}\nOutput {item.RelativeOutput}" : item.Error));
    }
    public Task<ExportReport> ExportToAsync(string destination, bool overwrite = false, CancellationToken token = default)
    { var task = ExportCoreAsync(destination, overwrite, token); Track(task); return task; }
    private async Task<ExportReport> ExportCoreAsync(string destination, bool overwrite, CancellationToken token)
    {
        var choice = SelectedResource ?? throw new InvalidOperationException("Select an asset first."); var source = snapshot ?? throw new InvalidOperationException("Open a workspace first.");
        if (activeExport is { IsCompleted: false }) throw new InvalidOperationException("An export is already running.");
        if (choice.Resource.Validity != ResourceValidity.Valid || choice.Resource.StoredSize < 0) throw new IOException("The asset has no supported source range.");
        var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspace.Token); exportCancellation = own;
        var group=(Groups.SelectedItem as AssetFamilyGroupChoice)?.Key;var validate=GroupValidator;
        if(!string.IsNullOrEmpty(group)&&(!groupMembers.TryGetValue(group,out var members)||!members.Contains(choice.Identity)))
        {own.Dispose();exportCancellation=null;throw new InvalidOperationException("Select an asset from this catalog.");}
        var operation = Task.Run(async () =>
        {
            if(!string.IsNullOrEmpty(group)&&validate is not null)await validate(group,own.Token);
            var full = Path.GetFullPath(destination); var directory = Path.GetDirectoryName(full)!;
            var plan = exporter.Plan(source, new ExportOptions(directory, overwrite), [choice.Resource], own.Token);
            plan = plan with { Items = [plan.Items[0] with { RelativeOutput = Path.GetFileName(full) }] };
            return await exporter.RunAsync(plan, cancellationToken: own.Token);
        }, own.Token);
        activeExport = operation;
        try { return await operation; }
        finally { if (ReferenceEquals(activeExport, operation)) activeExport = null; if (ReferenceEquals(exportCancellation, own)) exportCancellation = null; own.Dispose(); }
    }
    private Task Track(Task task) { operations.Add(task); _ = ObserveAsync(task); return task; }
    private async Task ObserveAsync(Task task) { try { await task; } catch (Exception) { } finally { operations.Remove(task); } }
    private void Cancel_Click(object sender, RoutedEventArgs e) { loadCancellation?.Cancel(); queryCancellation?.Cancel(); exportCancellation?.Cancel(); CancelNativeTextures(); DiscoveryCancel?.Invoke(); }
    public async Task ClearAsync()
    {
        ++loadGeneration; ++queryGeneration; ++selectionGeneration; workspace.Cancel(); loadCancellation?.Cancel(); queryCancellation?.Cancel(); exportCancellation?.Cancel();
        await ClearNativeTexturesAsync(); ResetView();
        await Task.WhenAll(operations.Select(async task => { try { await task; } catch (Exception) { } }).ToArray()); queryTask = Task.CompletedTask;
    }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); await ClearAsync(); await DisposeNativeTexturesAsync(); }
}
