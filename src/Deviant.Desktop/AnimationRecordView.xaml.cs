using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Export;
using Deviant.Export.Animation;
using Deviant.Formats.Animation;
using Microsoft.Win32;

namespace Deviant.Desktop;

public sealed record AnimationResourceChoice(ResourceIdentity Identity, ResourceRecord Resource, string Label);
public sealed record AnimationTableChoice(AnimationScalarTable Table, string Label);
public sealed record AnimationTableRowChoice(AnimationScalarRow Row)
{
    public int Number => Row.Ordinal + 1;
    public int FieldCount => Row.Fields.Count;
}

public partial class AnimationRecordView : UserControl, IAsyncDisposable
{
    private const int PageSize = 100;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource workspaceCancellation = new();
    private readonly SemaphoreSlim decodeGate = new(1, 1);
    private CancellationTokenSource? loadCancellation, selectionCancellation, queryCancellation, jobCancellation;
    private ArchiveSnapshot? snapshot;
    private AnimationResourceChoice[] resources = [], matches = [];
    private DecodeResult<AnimationDataDocument>? decoded;
    private long loadGeneration, selectionGeneration, queryGeneration;
    private int pageOffset;
    private bool updating;
    private Task? activeJob;
    private Task detailTask = Task.CompletedTask;
    private AnimationReadProfile profile = AnimationReadProfile.FramedRecords;
    private int tablePageOffset;
    private string advancedBase = "";

    public AnimationRecordView() => InitializeComponent();
    public event EventHandler? SelectedResourceChanged;
    public ResourceIdentity? SelectedIdentity => (Rows.SelectedItem as AnimationResourceChoice)?.Identity;
    public string? SelectedLabel => (Rows.SelectedItem as AnimationResourceChoice)?.Label;
    public DecodeResult<AnimationDataDocument>? CurrentDocument => decoded;
    public AnimationReadProfile CurrentProfile => profile;
    public AnimationScalarTable? SelectedTable => (TableSelect.SelectedItem as AnimationTableChoice)?.Table;
    public AnimationScalarRow? SelectedTableRow => (TableRows.SelectedItem as AnimationTableRowChoice)?.Row;
    public IReadOnlyList<AnimationScalarRow> CurrentTableRows => TableRows.ItemsSource is AnimationTableRowChoice[] rows ? rows.Select(item => item.Row).ToArray() : [];
    public SourceRevision? LoadedRevision => snapshot is null ? null : new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
    public IReadOnlyList<AnimationResourceChoice> CurrentResources => Rows.ItemsSource as AnimationResourceChoice[] ?? [];

    public async Task LoadAsync(ArchiveSnapshot source, CancellationToken token = default)
    {
        var generation = ++loadGeneration;
        workspaceCancellation.Cancel(); workspaceCancellation.Dispose(); workspaceCancellation = new();
        loadCancellation?.Cancel(); selectionCancellation?.Cancel(); queryCancellation?.Cancel(); jobCancellation?.Cancel();
        ++selectionGeneration; ++queryGeneration;
        snapshot = null; resources = matches = []; pageOffset = 0;
        updating = true;
        try { Rows.ItemsSource = null; Search.Text = ""; ClearDetails(); }
        finally { updating = false; }
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
        loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspaceCancellation.Token);
        var own = loadCancellation;
        Status.Text = "Reading animations…";
        try
        {
            var choices = await Task.Run(() =>
            {
                var result = new List<AnimationResourceChoice>();
                foreach (var row in source.Records)
                {
                    own.Token.ThrowIfCancellationRequested();
                    if (row.Type == AnimationDataReader.ResourceType)
                        result.Add(new(new(source.IndexContentId, row.Ordinal), row, $"Animation{result.Count + 1}"));
                }
                return result.ToArray();
            }, own.Token);
            own.Token.ThrowIfCancellationRequested();
            if (generation != loadGeneration) return;
            snapshot = source; resources = matches = choices; RenderPage();
            Status.Text = $"{resources.Length:N0} animations";
        }
        catch (OperationCanceledException) { if (generation == loadGeneration) Status.Text = "Cancelled"; }
        catch (Exception error) { if (generation == loadGeneration) Status.Text = error.Message; }
        finally { if (ReferenceEquals(loadCancellation, own)) loadCancellation = null; own.Dispose(); }
    }

    public async Task SelectResourceAsync(ResourceIdentity identity, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var index = Array.FindIndex(resources, item => item.Identity == identity);
        if (index < 0) throw new InvalidOperationException("The animation is unavailable.");
        queryCancellation?.Cancel(); ++queryGeneration;
        updating = true;
        try { Search.Text = ""; }
        finally { updating = false; }
        matches = resources; pageOffset = index / PageSize * PageSize; RenderPage();
        updating = true;
        try { Rows.SelectedItem = resources[index]; Rows.ScrollIntoView(resources[index]); }
        finally { updating = false; }
        await ReadSelectionAsync(token);
    }

    private void RenderPage()
    {
        selectionCancellation?.Cancel(); ++selectionGeneration;
        updating = true;
        try { Rows.ItemsSource = matches.Skip(pageOffset).Take(PageSize).ToArray(); }
        finally { updating = false; }
        ClearDetails(); SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
        Page.Text = $"{(matches.Length == 0 ? 0 : pageOffset + 1):N0}\u2013{Math.Min(pageOffset + PageSize, matches.Length):N0} / {matches.Length:N0}";
        Previous.IsEnabled = pageOffset > 0; Next.IsEnabled = pageOffset + PageSize < matches.Length;
    }

    private async void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (updating || snapshot is null) return;
        selectionCancellation?.Cancel(); ++selectionGeneration; ClearDetails();
        queryCancellation?.Cancel();
        queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var own = queryCancellation; var generation = ++queryGeneration; var text = Search.Text.Trim(); var current = resources;
        try
        {
            var filtered = await Task.Run(() => current.Where(item =>
            { own.Token.ThrowIfCancellationRequested(); return item.Label.Contains(text, StringComparison.OrdinalIgnoreCase); }).ToArray(), own.Token);
            if (generation != queryGeneration || own.IsCancellationRequested) return;
            matches = filtered; pageOffset = 0; RenderPage();
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(queryCancellation, own)) queryCancellation = null; own.Dispose(); }
    }

    private async void SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!updating) await ReadSelectionAsync(); }

    private Task ReadSelectionAsync(CancellationToken token = default)
    { detailTask = ReadSelectionCoreAsync(token); return detailTask; }
    private async Task ReadSelectionCoreAsync(CancellationToken token)
    {
        selectionCancellation?.Cancel();
        selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        var own = selectionCancellation; var generation = ++selectionGeneration;
        var source = snapshot; var choice = Rows.SelectedItem as AnimationResourceChoice;
        ClearDetails(); SelectedName.Text = choice?.Label ?? "Select an animation";
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
        if (source is null || choice is null) { selectionCancellation = null; own.Dispose(); return; }
        Status.Text = "Reading animation…"; var entered = false;
        try
        {
            await decodeGate.WaitAsync(own.Token); entered = true;
            var result = await Task.Run(async () =>
            {
                var row = choice.Resource;
                if (row.Validity != ResourceValidity.Valid || row.StoredSize > AnimationDataReader.MaximumResourceBytes || row.StoredSize < 0)
                    throw new IOException("The animation has no supported source range.");
                var bytes = await new ArchiveResourceResolver(source).ReadAsync(choice.Identity, 0, checked((int)row.StoredSize), own.Token);
                own.Token.ThrowIfCancellationRequested();
                var volume = source.Volumes.Single(item => item.Package == row.Package);
                return new AnimationDataReader().Decode(bytes, choice.Identity, new(source.IndexContentId, source.PhysicalSourceStamp),
                    new(volume.RelativeName, row.Offset, bytes.Length));
            }, own.Token);
            own.Token.ThrowIfCancellationRequested();
            if (generation != selectionGeneration || !ReferenceEquals(source, snapshot)) return;
            ApplyDocument(result, AnimationReadProfile.FramedRecords);

        }
        catch (OperationCanceledException) { if (generation == selectionGeneration) Status.Text = "Cancelled"; }
        catch (Exception error) { if (generation == selectionGeneration) Status.Text = error.Message; }
        finally { if (entered) decodeGate.Release(); if (ReferenceEquals(selectionCancellation, own)) selectionCancellation = null; own.Dispose(); }
    }

    public Task SetProfileAsync(AnimationReadProfile requested, CancellationToken token = default)
    {
        if (requested is not (AnimationReadProfile.FramedRecords or AnimationReadProfile.Version14Tables)) throw new ArgumentOutOfRangeException(nameof(requested));
        var current = decoded ?? throw new InvalidOperationException("Select an animation first.");
        if (requested == AnimationReadProfile.Version14Tables && current.Value?.Data.Version != 14) throw new InvalidOperationException("Tables require ANIMDATA version 14.");
        detailTask = ChangeProfileAsync(current, requested, token); return detailTask;
    }
    private async Task ChangeProfileAsync(DecodeResult<AnimationDataDocument> current, AnimationReadProfile requested, CancellationToken token)
    {
        var source = snapshot; var previousProfile = profile; var generation = ++selectionGeneration;
        selectionCancellation?.Cancel(); var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspaceCancellation.Token); selectionCancellation = own;
        updating = true; try { TablesToggle.IsChecked = requested == AnimationReadProfile.Version14Tables; } finally { updating = false; }
        ExportOriginal.IsEnabled = ExportJson.IsEnabled = false; Status.Text = "Reading records…"; var entered = false;
        try
        {
            await decodeGate.WaitAsync(own.Token); entered = true;
            var result = await Task.Run(() =>
            {
                own.Token.ThrowIfCancellationRequested();
                var document = current.Value ?? throw new InvalidOperationException("The animation could not be decoded.");
                var value = new AnimationDataReader().Decode(document.EncodedResource, current.Identity, current.Revision, current.Extents[0], requested);
                own.Token.ThrowIfCancellationRequested(); return value;
            }, own.Token);
            own.Token.ThrowIfCancellationRequested();
            if (generation != selectionGeneration || !ReferenceEquals(source, snapshot)) return;
            ApplyDocument(result, requested);
        }
        catch (OperationCanceledException) { if (generation == selectionGeneration) { ApplyDocument(current, previousProfile); Status.Text = "Cancelled"; } }
        catch (Exception error) { if (generation == selectionGeneration) { ApplyDocument(current, previousProfile); Status.Text = error.Message; } }
        finally { if (entered) decodeGate.Release(); if (ReferenceEquals(selectionCancellation, own)) selectionCancellation = null; own.Dispose(); }
    }
    private void ApplyDocument(DecodeResult<AnimationDataDocument> result, AnimationReadProfile currentProfile)
    {
        decoded = result; profile = currentProfile;
        var choice = Rows.SelectedItem as AnimationResourceChoice; var document = result.Value;
        Sections.ItemsSource = document?.Sections;
        Words.ItemsSource = document?.HeaderWords.Select((word, index) => new { Word = index + 1, Hex = $"0x{word.Bits:X8}" }).ToArray();
        advancedBase = $"Resource {result.Identity.Ordinal}\nID 0x{choice?.Resource.Id:X8}\nIndex {result.Revision.IndexContentId}\nRevision {result.Revision.PhysicalSourceStamp}\nProfile {currentProfile}\n" +
            string.Join("\n", result.Extents.Select(extent => $"Source {extent.RelativeSource}: {extent.Offset} + {extent.Length}\nSHA-256 {extent.PayloadSha256}")) + "\n" +
            string.Join("\n", document?.Sections.Select(section => $"{section.Tag}: record {section.ResourceOffset}, body {section.BodyOffset} + {section.BodyLength}") ?? []) + "\n" +
            string.Join("\n", document?.HeaderWords.Select(word => $"Header +{word.BodyOffset}: 0x{word.Bits:X8}") ?? []) + "\n" + string.Join("\n", result.Diagnostics);
        var layout = document?.Version14Layout;
        if (layout is not null)
        {
            var previewLength = checked((int)Math.Min(64, layout.RemainingLength));
            advancedBase += $"\nVariant {layout.Variant}\nFlags 0x{layout.Flags:X8}\nRemaining bytes {layout.RemainingResourceOffset} + {layout.RemainingLength}\nRemaining prefix {Convert.ToHexString(document!.EncodedResource.Span.Slice(checked((int)layout.RemainingResourceOffset), previewLength))}";
        }
        updating = true;
        try
        {
            TablesToggle.IsEnabled = document?.Data.Version == 14; TablesToggle.IsChecked = currentProfile == AnimationReadProfile.Version14Tables;
            TableTab.Visibility = layout is null ? Visibility.Collapsed : Visibility.Visible;
            if (layout is not null) InspectorTabs.SelectedItem = TableTab;
            else if (ReferenceEquals(InspectorTabs.SelectedItem, TableTab)) InspectorTabs.SelectedIndex = 0;
            TableSelect.ItemsSource = layout?.Tables.Select((table, index) => new AnimationTableChoice(table, $"Table {index + 1}")).ToArray(); TableSelect.SelectedIndex = layout?.Tables.Count > 0 ? 0 : -1;
        }
        finally { updating = false; }
        tablePageOffset = 0; RenderTablePage();
        ExportOriginal.IsEnabled = ExportJson.IsEnabled = document is not null;
        Status.Text = document is null ? "Records unavailable" : currentProfile == AnimationReadProfile.Version14Tables ? layout is null ? "Tables unavailable" : $"{layout.Tables.Count:N0} tables" : $"{document.Sections.Count} records";
    }
    private void RenderTablePage()
    {
        var table = SelectedTable; updating = true;
        try { TableRows.ItemsSource = table?.Rows.Skip(tablePageOffset).Take(PageSize).Select(row => new AnimationTableRowChoice(row)).ToArray(); }
        finally { updating = false; }
        var count = table?.Rows.Count ?? 0;
        TablePage.Text = $"{(count == 0 ? 0 : tablePageOffset + 1):N0}-{Math.Min(tablePageOffset + PageSize, count):N0} / {count:N0}";
        TablePrevious.IsEnabled = tablePageOffset > 0; TableNext.IsEnabled = tablePageOffset + PageSize < count; UpdateTableAdvanced();
    }
    private void UpdateTableAdvanced()
    {
        Advanced.Text = advancedBase;
        if (SelectedTable is not { } table) return;
        Advanced.Text += $"\nTable {table.Kind}\nCount {table.DeclaredCount} at resource {table.CountResourceOffset}\nRows at resource {table.RowsResourceOffset}\nSerialized stride {table.SerializedStride}";
        if (SelectedTableRow is not { } row) return;
        Advanced.Text += $"\nRow {row.Ordinal + 1} at resource {row.ResourceOffset}";
        for (var index = 0; index < row.Fields.Count; index++)
        {
            var field = row.Fields[index];
            Advanced.Text += $"\nField {index + 1}: {field.Width} bytes, row +{field.RowOffset}, resource {row.ResourceOffset + field.RowOffset}, 0x{field.Bits.ToString(field.Width == 2 ? "X4" : "X8")}";
        }
    }
    private async void TablesToggleChanged(object sender, RoutedEventArgs e)
    { if (!updating && decoded?.Value is not null) await SetProfileAsync(TablesToggle.IsChecked == true ? AnimationReadProfile.Version14Tables : AnimationReadProfile.FramedRecords); }
    private void TableChanged(object sender, SelectionChangedEventArgs e) { if (!updating) { tablePageOffset = 0; RenderTablePage(); } }
    private void TableRowChanged(object sender, SelectionChangedEventArgs e) { if (!updating) UpdateTableAdvanced(); }
    private void TablePrevious_Click(object sender, RoutedEventArgs e) { if (tablePageOffset >= PageSize) { tablePageOffset -= PageSize; RenderTablePage(); } }
    private void TableNext_Click(object sender, RoutedEventArgs e) { if (tablePageOffset + PageSize < (SelectedTable?.Rows.Count ?? 0)) { tablePageOffset += PageSize; RenderTablePage(); } }
    private async Task WaitForDetailsAsync(CancellationToken token)
    {
        while (true) { token.ThrowIfCancellationRequested(); lifetime.Token.ThrowIfCancellationRequested(); var current = detailTask; await current.WaitAsync(token); if (ReferenceEquals(current, detailTask)) return; }
    }
    private void ClearDetails()
    {
        var previous = updating; updating = true;
        try
        {
            decoded = null; profile = AnimationReadProfile.FramedRecords; advancedBase = ""; tablePageOffset = 0;
            Sections.ItemsSource = Words.ItemsSource = TableRows.ItemsSource = TableSelect.ItemsSource = null;
            TablesToggle.IsEnabled = false; TablesToggle.IsChecked = false; TableTab.Visibility = Visibility.Collapsed;
            if (ReferenceEquals(InspectorTabs.SelectedItem, TableTab)) InspectorTabs.SelectedIndex = 0;
            TablePage.Text = ""; TablePrevious.IsEnabled = TableNext.IsEnabled = false;
            Advanced.Text = ""; SelectedName.Text = "Select an animation"; ExportOriginal.IsEnabled = ExportJson.IsEnabled = false;
        }
        finally { updating = previous; }
    }
    private void Previous_Click(object sender, RoutedEventArgs e) { if (pageOffset >= PageSize) { pageOffset -= PageSize; RenderPage(); } }
    private void Next_Click(object sender, RoutedEventArgs e) { if (pageOffset + PageSize < matches.Length) { pageOffset += PageSize; RenderPage(); } }
    private async void ExportOriginal_Click(object sender, RoutedEventArgs e) => await ExportAsync(AssetExportFormat.Original);
    private async void ExportJson_Click(object sender, RoutedEventArgs e) => await ExportAsync(AssetExportFormat.Json);
    private async Task ExportAsync(AssetExportFormat format)
    {
        if (decoded?.Value is null || activeJob is { IsCompleted: false }) return;
        var dialog = new SaveFileDialog { Title = "Export animation", Filter = format == AssetExportFormat.Json ? "JSON (*.json)|*.json" : "Binary (*.bin)|*.bin", FileName = (SelectedLabel ?? "Animation") + (format == AssetExportFormat.Json ? ".json" : ".bin"), OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var version = selectionGeneration;
        try { await ExportToAsync(format, dialog.FileName, true); if (version == selectionGeneration) Status.Text = "Exported"; }
        catch (OperationCanceledException) { if (version == selectionGeneration) Status.Text = "Export cancelled"; }
        catch (Exception error) { if (version == selectionGeneration) Status.Text = error.Message; }
    }

    public async Task ExportToAsync(AssetExportFormat format, string destination, bool overwrite = false, CancellationToken token = default)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspaceCancellation.Token);
        var requestedIdentity = SelectedIdentity;
        await WaitForDetailsAsync(request.Token); request.Token.ThrowIfCancellationRequested();
        if (requestedIdentity != SelectedIdentity) throw new OperationCanceledException("The selected animation changed.", request.Token);
        var current = decoded ?? throw new InvalidOperationException("Select an animation first.");
        var document = current.Value ?? throw new InvalidOperationException("The animation could not be decoded.");
        var source = snapshot ?? throw new InvalidOperationException("Open a workspace first.");
        if (format is not (AssetExportFormat.Original or AssetExportFormat.Json)) throw new ArgumentOutOfRangeException(nameof(format));
        if (activeJob is { IsCompleted: false }) throw new InvalidOperationException("An export is already running.");
        var own = CancellationTokenSource.CreateLinkedTokenSource(request.Token, lifetime.Token, workspaceCancellation.Token);
        jobCancellation = own;
        var task = InspectorFileExport.WriteAsync(source, current.Identity, current.Revision, current.Extents[0], destination, overwrite,
            async (stream, cancellation) =>
            {
                if (!string.Equals(current.Extents[0].PayloadSha256, Convert.ToHexString(SHA256.HashData(document.EncodedResource.Span)), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The animation payload changed.");
                if (format == AssetExportFormat.Json) await AnimationJsonWriter.WriteAsync(stream, current, cancellation);
                else await stream.WriteAsync(document.EncodedResource, cancellation);
            }, own.Token, lifetime.Token);
        activeJob = task;
        try { await task; }
        finally
        {
            if (ReferenceEquals(activeJob, task)) activeJob = null;
            if (ReferenceEquals(jobCancellation, own)) jobCancellation = null;
            own.Dispose();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    { loadCancellation?.Cancel(); selectionCancellation?.Cancel(); queryCancellation?.Cancel(); jobCancellation?.Cancel(); }
    public async Task ClearAsync()
    {
        ++loadGeneration; ++selectionGeneration; ++queryGeneration;
        workspaceCancellation.Cancel();
        loadCancellation?.Cancel(); selectionCancellation?.Cancel(); queryCancellation?.Cancel(); jobCancellation?.Cancel();
        snapshot = null; resources = matches = []; pageOffset = 0;
        updating = true;
        try { Rows.ItemsSource = null; Search.Text = ""; ClearDetails(); Page.Text = ""; Status.Text = ""; Previous.IsEnabled = Next.IsEnabled = false; }
        finally { updating = false; }
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
        var job = activeJob;
        if (job is not null) try { await job; } catch (Exception) { }
        await decodeGate.WaitAsync(); decodeGate.Release();
    }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); await ClearAsync(); }

}

internal static class InspectorFileExport
{
    public static Task WriteAsync(ArchiveSnapshot snapshot, ResourceIdentity identity, SourceRevision revision, SourceExtent extent,
        string destination, bool overwrite, Func<Stream, CancellationToken, Task> write, CancellationToken token, CancellationToken lifetime,
        Func<CancellationToken, Task>? additionalValidation = null) =>
        AssetFileExporter.WriteAsync(snapshot, identity, revision, extent, destination, overwrite, write, token, lifetime, additionalValidation);
}