using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Containers;
using Deviant.Formats.Shaders;
using Microsoft.Win32;

namespace Deviant.Desktop;

public sealed record StoredShaderChoice(StoredGlslSource Source, string Label);

public partial class StoredShaderView : UserControl, IAsyncDisposable
{
    private const int PageSize = 100;
    private const int MaximumPreviewCharacters = 1024 * 1024;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource workspaceCancellation = new();
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private CancellationTokenSource? loadCancellation, queryCancellation, jobCancellation;
    private ArchiveSnapshot? snapshot;
    private StoredGlslScan? scan;
    private SourceExtent? originalExtent;
    private StoredShaderChoice[] sources = [], matches = [];
    private long loadGeneration, queryGeneration, selectionGeneration;
    private int pageOffset;
    private bool updating;
    private Task? activeJob;

    public StoredShaderView() => InitializeComponent();
    public event EventHandler? SelectedResourceChanged;
    public ResourceIdentity? SelectedIdentity => (Rows.SelectedItem as StoredShaderChoice)?.Source.Identity;
    public string? SelectedLabel => (Rows.SelectedItem as StoredShaderChoice)?.Label;
    public ResourceIdentity? ContainerIdentity => scan?.ContainerIdentity;
    public StoredGlslScan? CurrentScan => scan;
    public StoredGlslSource? SelectedSource => (Rows.SelectedItem as StoredShaderChoice)?.Source;
    public IReadOnlyList<StoredShaderChoice> CurrentSources => Rows.ItemsSource as StoredShaderChoice[] ?? [];

    public async Task LoadAsync(ArchiveSnapshot source, ResourceIdentity identity, CancellationToken token = default)
    {
        if (identity.IndexContentId != source.IndexContentId || identity.MemberPath is not null)
            throw new ArgumentException("Select a top-level resource from this archive.", nameof(identity));
        var row = source.Records.SingleOrDefault(item => item.Ordinal == identity.Ordinal)
            ?? throw new InvalidOperationException("The resource is unavailable.");
        var generation = ++loadGeneration;
        workspaceCancellation.Cancel(); workspaceCancellation.Dispose(); workspaceCancellation = new();
        loadCancellation?.Cancel(); queryCancellation?.Cancel(); jobCancellation?.Cancel();
        ++queryGeneration; ++selectionGeneration;
        snapshot = null; scan = null; originalExtent = null; sources = matches = []; pageOffset = 0;
        updating = true;
        try { Rows.ItemsSource = null; Search.Text = ""; ClearDetails(); }
        finally { updating = false; }
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
        loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspaceCancellation.Token);
        var own = loadCancellation; var entered = false; Status.Text = "Reading shader sources…";
        try
        {
            await scanGate.WaitAsync(own.Token); entered = true;
            var result = await Task.Run(async () =>
            {
                var decoded = await new SegsContainerReader().ReadAsync(source, row, cancellationToken: own.Token);
                own.Token.ThrowIfCancellationRequested();
                var found = StoredGlslReader.Scan(decoded, own.Token);
                own.Token.ThrowIfCancellationRequested();
                return (Scan: found, Extent: decoded.Extents.FirstOrDefault(), Choices: found.Sources.Select(item => new StoredShaderChoice(item, $"Shader{item.RegionOrdinal + 1}")).ToArray());
            }, own.Token);
            own.Token.ThrowIfCancellationRequested();
            if (generation != loadGeneration) return;
            snapshot = source; scan = result.Scan; originalExtent = result.Extent; sources = matches = result.Choices;
            RenderPage();
            Status.Text = $"{sources.Length:N0} shader sources";
            if (sources.Length == 0) Advanced.Text = string.Join("\n", result.Scan.Diagnostics.Select(item => item.Message));
        }
        catch (OperationCanceledException) { if (generation == loadGeneration) Status.Text = "Cancelled"; }
        catch (Exception error) { if (generation == loadGeneration) Status.Text = error.Message; }
        finally { if (entered) scanGate.Release(); if (ReferenceEquals(loadCancellation, own)) loadCancellation = null; own.Dispose(); }
    }

    public Task SelectResourceAsync(ResourceIdentity identity, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var index = Array.FindIndex(sources, item => item.Source.Identity == identity);
        if (index < 0) throw new InvalidOperationException("The shader source is unavailable.");
        queryCancellation?.Cancel(); ++queryGeneration;
        updating = true;
        try { Search.Text = ""; }
        finally { updating = false; }
        matches = sources; pageOffset = index / PageSize * PageSize; RenderPage();
        Rows.SelectedItem = sources[index]; Rows.ScrollIntoView(sources[index]);
        return Task.CompletedTask;
    }

    private void RenderPage()
    {
        updating = true;
        try { Rows.ItemsSource = matches.Skip(pageOffset).Take(PageSize).ToArray(); }
        finally { updating = false; }
        ClearDetails(); ++selectionGeneration; SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
        Page.Text = $"{(matches.Length == 0 ? 0 : pageOffset + 1):N0}–{Math.Min(pageOffset + PageSize, matches.Length):N0} / {matches.Length:N0}";
        Previous.IsEnabled = pageOffset > 0; Next.IsEnabled = pageOffset + PageSize < matches.Length;
    }

    private async void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (updating || snapshot is null) return;
        queryCancellation?.Cancel();
        queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var own = queryCancellation; var generation = ++queryGeneration; var text = Search.Text.Trim(); var current = sources;
        try
        {
            var filtered = await Task.Run(() => current.Where(item =>
            {
                own.Token.ThrowIfCancellationRequested();
                return item.Label.Contains(text, StringComparison.OrdinalIgnoreCase) || (item.Source.Text?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);
            }).ToArray(), own.Token);
            if (generation != queryGeneration || own.IsCancellationRequested) return;
            matches = filtered; pageOffset = 0; RenderPage();
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(queryCancellation, own)) queryCancellation = null; own.Dispose(); }
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating) return;
        ClearDetails(); ++selectionGeneration;
        var choice = Rows.SelectedItem as StoredShaderChoice;
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
        if (choice is null) return;
        var source = choice.Source; SelectedName.Text = choice.Label;
        Code.Text = source.Text is null ? "" : source.Text[..Math.Min(MaximumPreviewCharacters, source.Text.Length)];
        PartialBadge.Visibility = source.HasNullTerminator ? Visibility.Collapsed : Visibility.Visible;
        ExportSource.IsEnabled = ExportBinary.IsEnabled = true;
        Advanced.Text = $"Resource {source.Identity.Ordinal}\nMember {source.Identity.MemberPath}\nIndex {source.Revision.IndexContentId}\nRevision {source.Revision.PhysicalSourceStamp}\nSHA-256 {source.Sha256}\n" +
            string.Join("\n", source.PhysicalExtents.Select(extent => $"Physical source {extent.RelativeSource}: {extent.Offset} + {extent.Length}\nPhysical SHA-256 {extent.PayloadSha256}")) + "\n" +
            $"Decoded source: {source.DecodedExtent.Offset} + {source.DecodedExtent.Length}\nDecoded context: {source.ContextExtent.Offset} + {source.ContextExtent.Length}\nContext bytes: {Convert.ToHexString(source.ContextBefore.Span)}\nNUL terminated: {source.HasNullTerminator}\n" +
            string.Join("\n", source.DecodeSteps.Select(step => $"{step.Decoder} {step.Version}: {step.Options}")) + "\n" +
            string.Join("\n", scan?.Diagnostics.Select(item => item.Message) ?? []);
        Status.Text = source.Text is null ? $"{source.Utf8Bytes.Length:N0} bytes · UTF-8 preview unavailable" : source.Text.Length > MaximumPreviewCharacters ?
            $"{source.Utf8Bytes.Length:N0} bytes · preview {MaximumPreviewCharacters:N0} / {source.Text.Length:N0} characters" : $"{source.Utf8Bytes.Length:N0} bytes";
    }

    private void ClearDetails()
    { Code.Text = Advanced.Text = ""; SelectedName.Text = "Select a shader source"; PartialBadge.Visibility = Visibility.Collapsed; ExportSource.IsEnabled = ExportBinary.IsEnabled = false; }
    private void Previous_Click(object sender, RoutedEventArgs e) { if (pageOffset >= PageSize) { pageOffset -= PageSize; RenderPage(); } }
    private void Next_Click(object sender, RoutedEventArgs e) { if (pageOffset + PageSize < matches.Length) { pageOffset += PageSize; RenderPage(); } }
    private async void ExportSource_Click(object sender, RoutedEventArgs e) => await ExportAsync(false);
    private async void ExportBinary_Click(object sender, RoutedEventArgs e) => await ExportAsync(true);
    private async Task ExportAsync(bool binary)
    {
        if (SelectedSource is null || activeJob is { IsCompleted: false }) return;
        var dialog = new SaveFileDialog { Title = "Export shader source", Filter = binary ? "Binary (*.bin)|*.bin" : "GLSL (*.glsl)|*.glsl", FileName = (SelectedLabel ?? "Shader") + (binary ? ".bin" : ".glsl"), OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var version = selectionGeneration;
        try { await ExportToAsync(dialog.FileName, true); if (version == selectionGeneration) Status.Text = "Exported"; }
        catch (OperationCanceledException) { if (version == selectionGeneration) Status.Text = "Export cancelled"; }
        catch (Exception error) { if (version == selectionGeneration) Status.Text = error.Message; }
    }

    public async Task ExportToAsync(string destination, bool overwrite = false, CancellationToken token = default)
    {
        var selected = SelectedSource ?? throw new InvalidOperationException("Select a shader source first.");
        var current = snapshot ?? throw new InvalidOperationException("Open a workspace first.");
        var extent = originalExtent ?? throw new InvalidOperationException("The container source is unavailable.");
        if (activeJob is { IsCompleted: false }) throw new InvalidOperationException("An export is already running.");
        var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspaceCancellation.Token);
        jobCancellation = own;
        var task = InspectorFileExport.WriteAsync(current, selected.Identity, selected.Revision, extent, destination, overwrite,
            async (stream, cancellation) =>
            {
                if (!string.Equals(selected.Sha256, Convert.ToHexString(SHA256.HashData(selected.Utf8Bytes.Span)), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The shader payload changed.");
                await selected.WriteOriginalAsync(stream, cancellation);
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

    private void Cancel_Click(object sender, RoutedEventArgs e) { loadCancellation?.Cancel(); queryCancellation?.Cancel(); jobCancellation?.Cancel(); }
    public async Task ClearAsync()
    {
        ++loadGeneration; ++queryGeneration; ++selectionGeneration;
        workspaceCancellation.Cancel();
        loadCancellation?.Cancel(); queryCancellation?.Cancel(); jobCancellation?.Cancel();
        snapshot = null; scan = null; originalExtent = null; sources = matches = []; pageOffset = 0;
        updating = true;
        try { Rows.ItemsSource = null; Search.Text = ""; ClearDetails(); Page.Text = ""; Status.Text = ""; Previous.IsEnabled = Next.IsEnabled = false; }
        finally { updating = false; }
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
        var job = activeJob;
        if (job is not null) try { await job; } catch (Exception) { }
        await scanGate.WaitAsync(); scanGate.Release();
    }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); await ClearAsync(); }
}
