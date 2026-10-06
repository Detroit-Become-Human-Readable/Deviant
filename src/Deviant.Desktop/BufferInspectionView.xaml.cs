using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Export;
using Microsoft.Win32;

namespace Deviant.Desktop;

public sealed record BufferRelatedChoice(AssetRelationship Relationship, string Label);

public partial class BufferInspectionView : UserControl, IAsyncDisposable
{
    private const int PageBytes = 4096;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource workspace = new();
    private CancellationTokenSource? loadCancellation, pageCancellation, jobCancellation, navigationCancellation;
    private readonly HashSet<Task> operations = [];
    private ArchiveSnapshot? snapshot;
    private DecodeResult<BufferInspectionDocument>? inspection;
    private long generation, pageGeneration, relatedGeneration;
    private int byteOffset;
    private bool updating;
    private Task pageTask = Task.CompletedTask;
    private Task? exportTask;

    public BufferInspectionView() => InitializeComponent();
    public event EventHandler? SelectedResourceChanged;
    public DecodeResult<BufferInspectionDocument>? CurrentInspection => inspection;
    public ResourceIdentity? SelectedIdentity => inspection?.Identity;
    public string? SelectedLabel => inspection is null ? null : SourceName.Text;
    public SourceRevision? LoadedRevision => inspection?.Revision;
    public BufferInspectionSection? SelectedSection => Sections.SelectedItem as BufferInspectionSection;
    public Func<ResourceIdentity, CancellationToken, Task>? RelatedNavigator { get; set; }
    public int CurrentByteOffset => byteOffset;

    public Task LoadAsync(ArchiveSnapshot source, DecodeResult<BufferInspectionDocument> result, string label, CancellationToken token = default)
    { ObjectDisposedException.ThrowIf(lifetime.IsCancellationRequested, this); return Track(LoadCoreAsync(source, result, label, token)); }
    private async Task LoadCoreAsync(ArchiveSnapshot source, DecodeResult<BufferInspectionDocument> result, string label, CancellationToken token)
    {
        var version = ++generation; ++pageGeneration; ++relatedGeneration;
        workspace.Cancel(); workspace.Dispose(); workspace = new(); loadCancellation?.Cancel(); pageCancellation?.Cancel(); jobCancellation?.Cancel(); navigationCancellation?.Cancel(); Reset();
        var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspace.Token); loadCancellation = own; Status.Text = "Reading inspection…";
        try
        {
            await Task.Run(() =>
            {
                own.Token.ThrowIfCancellationRequested();
                if (result.Identity.IndexContentId != source.IndexContentId || result.Identity.MemberPath is not null || result.Revision != new SourceRevision(source.IndexContentId, source.PhysicalSourceStamp))
                    throw new ArchiveException("SourceChanged", "The inspection belongs to another source revision.");
                if (result.Value is not { } document || document.Sections.Count == 0 || !document.Sections.Any(section => section.Bytes.Length > 0)) return;
                var row = source.Records.Single(item => item.Ordinal == result.Identity.Ordinal);
                if (row.StoredSize > 16 * 1024 * 1024) throw new ArchiveException("InspectionSizeLimit", "The resource exceeds the 16 MiB inspection limit.");
                var volume = source.Volumes.Single(item => item.Package == row.Package);
                var extent = document.OriginalExtent;
                if (extent.RelativeSource != volume.RelativeName || extent.Offset != row.Offset || extent.Length != row.StoredSize || !IsDigest(extent.PayloadSha256))
                    throw new ArchiveException("InvalidInspectionExtent", "The inspection must retain the original source extent and payload digest.");
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var section in document.Sections)
                {
                    own.Token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(section.Key) || !keys.Add(section.Key) || string.IsNullOrWhiteSpace(section.Label)) throw new ArgumentException("Inspection sections need unique keys and readable labels.");
                    if (!string.Equals(section.Sha256, Digest(section.Bytes.Span), StringComparison.OrdinalIgnoreCase)) throw new ArchiveException("InvalidInspectionPayload", "A section payload differs from its recorded digest.");
                    if (section.DecodedExtent is { } decoded && (decoded.Container != result.Identity || decoded.Offset < 0 || decoded.Length != section.Length)) throw new ArgumentException("The decoded extent does not match its section bytes.");
                    if (section.PhysicalExtent is { } physical && (physical.RelativeSource != extent.RelativeSource || physical.Offset < extent.Offset || physical.Length < 0 || physical.Offset > extent.Offset + extent.Length - physical.Length)) throw new ArgumentException("The physical section extent exceeds the original resource.");
                }
            }, own.Token);
            own.Token.ThrowIfCancellationRequested(); if (version != generation) return;
            if (result.Value is not { } value || value.Sections.Count == 0 || !value.Sections.Any(section => section.Bytes.Length > 0)) { Advanced.Text = string.Join("\n", result.Diagnostics); Status.Text = "Inspection unavailable"; return; }
            snapshot = source; inspection = result; SourceName.Text = label; Summary.Text = $"{value.Format} · {value.OriginalExtent.Length:N0} bytes";
            updating = true;
            try
            {
                Sections.ItemsSource = value.Sections; Sections.SelectedIndex = 0;
                var links = value.Relationships.Where(HasVerifiedTarget).Select((relationship, index) => new BufferRelatedChoice(relationship, $"Reference {index + 1}")).ToArray();
                Related.ItemsSource = links; RelatedPanel.Visibility = links.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { updating = false; }
            ExportOriginal.IsEnabled = ExportJson.IsEnabled = true; SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
            await RequestPageAsync(false); if (version == generation) Status.Text = $"{value.Sections.Count:N0} sections";
        }
        catch (OperationCanceledException) { if (version == generation) Status.Text = "Cancelled"; }
        catch (Exception error) { if (version == generation) Status.Text = error.Message; }
        finally { if (ReferenceEquals(loadCancellation, own)) loadCancellation = null; own.Dispose(); }
    }
    public Task SelectSectionAsync(string key, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); var section = inspection?.Value?.Sections.SingleOrDefault(item => item.Key == key) ?? throw new InvalidOperationException("The section is unavailable.");
        updating = true; try { Sections.SelectedItem = section; } finally { updating = false; }
        byteOffset = 0; return RequestPageAsync(false, token);
    }
    private Task RequestPageAsync(bool retainOffset, CancellationToken token = default)
    { if (!retainOffset) byteOffset = 0; pageTask = Track(RenderPageAsync(token)); return pageTask; }
    private async Task RenderPageAsync(CancellationToken token)
    {
        pageCancellation?.Cancel(); var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspace.Token); pageCancellation = own;
        var section = SelectedSection; var version = ++pageGeneration; var ownerVersion = generation; var offset = byteOffset;
        Hex.Text = ""; Previous.IsEnabled = Next.IsEnabled = false; SectionName.Text = section?.Label ?? "Select a section";
        try
        {
            if (section is null) return;
            var text = await Task.Run(() =>
            {
                var bytes = section.Bytes.Span.Slice(offset, Math.Min(PageBytes, section.Bytes.Length - offset)); var builder = new StringBuilder(bytes.Length * 3);
                for (var index = 0; index < bytes.Length; index++)
                { if ((index & 255) == 0) own.Token.ThrowIfCancellationRequested(); if (index > 0) builder.Append(index % 16 == 0 ? '\n' : ' '); builder.Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture)); }
                return builder.ToString();
            }, own.Token);
            own.Token.ThrowIfCancellationRequested(); if (version != pageGeneration || ownerVersion != generation || !ReferenceEquals(section, SelectedSection)) return;
            Hex.Text = text; Page.Text = $"{(section.Length == 0 ? 0 : offset + 1):N0}–{Math.Min(offset + PageBytes, section.Length):N0} / {section.Length:N0} bytes";
            Previous.IsEnabled = offset > 0; Next.IsEnabled = offset + PageBytes < section.Length; ShowAdvanced();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (version == pageGeneration && ownerVersion == generation) Status.Text = error.Message; }
        finally { if (ReferenceEquals(pageCancellation, own)) pageCancellation = null; own.Dispose(); }
    }
    private void ShowAdvanced()
    {
        if (inspection is not { Value: { } document } current) { Advanced.Text = ""; return; }
        Advanced.Text = $"Resource {current.Identity.Ordinal}\nIndex {current.Revision.IndexContentId}\nRevision {current.Revision.PhysicalSourceStamp}\nProfile {document.Profile}\nBoundary basis {document.BoundaryBasis}\nOriginal {document.OriginalExtent.RelativeSource}: {document.OriginalExtent.Offset} + {document.OriginalExtent.Length}\nOriginal SHA-256 {document.OriginalExtent.PayloadSha256}\n" +
            string.Join("\n", document.Fields.Select(field => $"{field.Key}: {field.Value}")) + "\n" + string.Join("\n", current.UnknownFields.Select(field => $"{field.Key}: {field.Value}")) + "\n" + string.Join("\n", current.Diagnostics);
        if (SelectedSection is { } section)
        {
            Advanced.Text += $"\nSection {section.Key}\nKind {section.Kind}\nSHA-256 {section.Sha256}\nByte page {byteOffset} + {Math.Min(PageBytes, section.Bytes.Length - byteOffset)}";
            if (section.PhysicalExtent is { } physical) Advanced.Text += $"\nPhysical {physical.RelativeSource}: {physical.Offset} + {physical.Length}";
            if (section.DecodedExtent is { } decoded) Advanced.Text += $"\nDecoded {decoded.Offset} + {decoded.Length}";
        }
        foreach (var item in document.Relationships)
            Advanced.Text += $"\nReference {item.Reference.Namespace} type {item.Reference.ResourceType} ID {item.Reference.Id}: {item.Resolution.Status}";
        if (Related.SelectedItem is BufferRelatedChoice { Relationship: { } relationship })
        {
            Advanced.Text += $"\nReference {relationship.Reference.Namespace} · type {relationship.Reference.ResourceType} · ID {relationship.Reference.Id}\nResolution {relationship.Resolution.Status}\nEvidence {relationship.Evidence.Source}\n{relationship.Evidence.Description}\nTrailer {relationship.Evidence.SourceReferenceTrailer}\n";
            if (relationship.Evidence.PhysicalExtent is { } extent) Advanced.Text += $"Source {extent.RelativeSource}: {extent.Offset} + {extent.Length}\nSHA-256 {extent.PayloadSha256}\n";
            Advanced.Text += string.Join("\n", relationship.Resolution.Candidates.Select(candidate => candidate.Key));
        }
        OpenRelated.IsEnabled = CanNavigate(Related.SelectedItem as BufferRelatedChoice);
    }
    private bool HasVerifiedTarget(AssetRelationship relationship)
    {
        if (snapshot is not { } source || inspection is null || relationship.NavigationTarget is not { } target || relationship.Source != inspection.Identity ||
            relationship.Reference.Owner != inspection.Identity || relationship.Reference.Namespace != IdentifierNamespace.Archive || relationship.Reference.ResourceType is null || target.IndexContentId != source.IndexContentId || target.MemberPath is not null) return false;
        var row = target.Ordinal >= 0 && target.Ordinal < source.Records.Count ? source.Records[(int)target.Ordinal] : null;
        return row is not null && row.Ordinal == target.Ordinal && row.Type == relationship.Reference.ResourceType && uint.TryParse(relationship.Reference.Id, out var id) && row.Id == id;
    }
    private bool CanNavigate(BufferRelatedChoice? choice) => RelatedNavigator is not null && choice is not null && HasVerifiedTarget(choice.Relationship);
    public Task OpenSelectedRelatedAsync(CancellationToken token = default) => Track(NavigateAsync(token));
    private async Task NavigateAsync(CancellationToken token)
    {
        if (!CanNavigate(Related.SelectedItem as BufferRelatedChoice)) return;
        var choice = (BufferRelatedChoice)Related.SelectedItem; var target = choice.Relationship.NavigationTarget!; var source = snapshot!; var current = inspection!;
        var version = generation; var selection = relatedGeneration; var navigate = RelatedNavigator!;
        navigationCancellation?.Cancel(); var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); navigationCancellation = own;
        using var validation = CancellationTokenSource.CreateLinkedTokenSource(own.Token, workspace.Token);
        try
        {
            await ValidateOriginalAsync(source, current, validation.Token);
            own.Token.ThrowIfCancellationRequested(); if (version != generation || selection != relatedGeneration || !ReferenceEquals(current, inspection) || !ReferenceEquals(choice, Related.SelectedItem)) return;
            await navigate(target, own.Token);
        }
        finally { if (ReferenceEquals(navigationCancellation, own)) navigationCancellation = null; own.Dispose(); }
    }
    private async Task OpenSafelyAsync()
    { var version = generation; var selection = relatedGeneration; try { await OpenSelectedRelatedAsync(); } catch (OperationCanceledException) { } catch (Exception error) { if (version == generation && selection == relatedGeneration) Status.Text = error.Message; } }
    private async void SectionChanged(object sender, SelectionChangedEventArgs e) { if (!updating) await RequestPageAsync(false); }
    private void RelatedChanged(object sender, SelectionChangedEventArgs e) { if (!updating) { ++relatedGeneration; navigationCancellation?.Cancel(); ShowAdvanced(); } }
    private async void Open_Click(object sender, RoutedEventArgs e) => await OpenSafelyAsync();
    private async void RelatedKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && CanNavigate(Related.SelectedItem as BufferRelatedChoice)) { e.Handled = true; await OpenSafelyAsync(); } }
    private async void RelatedDoubleClick(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is DependencyObject origin && ItemsControl.ContainerFromElement(Related, origin) is ListBoxItem && CanNavigate(Related.SelectedItem as BufferRelatedChoice)) { e.Handled = true; await OpenSafelyAsync(); } }
    private async void Previous_Click(object sender, RoutedEventArgs e) { if (byteOffset >= PageBytes) { byteOffset -= PageBytes; await RequestPageAsync(true); } }
    private async void Next_Click(object sender, RoutedEventArgs e) { if (byteOffset + PageBytes < (SelectedSection?.Length ?? 0)) { byteOffset += PageBytes; await RequestPageAsync(true); } }
    private async void Original_Click(object sender, RoutedEventArgs e) => await ExportAsync(AssetExportFormat.Original);
    private async void Json_Click(object sender, RoutedEventArgs e) => await ExportAsync(AssetExportFormat.Json);
    private async Task ExportAsync(AssetExportFormat format)
    {
        if (inspection is null || exportTask is { IsCompleted: false }) return;
        var dialog = new SaveFileDialog { Title = "Export inspection", Filter = format == AssetExportFormat.Json ? "JSON (*.json)|*.json" : "Binary (*.bin)|*.bin", FileName = (SelectedLabel ?? "Resource") + (format == AssetExportFormat.Json ? ".json" : ".bin"), OverwritePrompt = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var version = generation;
        try { await ExportToAsync(format, dialog.FileName, true); if (version == generation) Status.Text = "Exported"; }
        catch (OperationCanceledException) { if (version == generation) Status.Text = "Export cancelled"; }
        catch (Exception error) { if (version == generation) Status.Text = error.Message; }
    }
    public Task ExportToAsync(AssetExportFormat format, string destination, bool overwrite = false, CancellationToken token = default) => Track(ExportCoreAsync(format, destination, overwrite, token));
    private async Task ExportCoreAsync(AssetExportFormat format, string destination, bool overwrite, CancellationToken token)
    {
        var current = inspection ?? throw new InvalidOperationException("Open an inspection first."); var source = snapshot!; var document = current.Value!;
        if (format is not (AssetExportFormat.Original or AssetExportFormat.Json)) throw new ArgumentOutOfRangeException(nameof(format));
        if (exportTask is { IsCompleted: false }) throw new InvalidOperationException("An export is already running.");
        var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, workspace.Token); jobCancellation = own;
        Task task;
        if (format == AssetExportFormat.Json)
            task = InspectorFileExport.WriteAsync(source, current.Identity, current.Revision, document.OriginalExtent, destination, overwrite,
                (stream, cancellation) => JsonSerializer.SerializeAsync(stream, current, new JsonSerializerOptions { WriteIndented = true }, cancellation), own.Token, lifetime.Token);
        else task = Task.Run(async () =>
        {
            var row = source.Records.Single(item => item.Ordinal == current.Identity.Ordinal); var volume = source.Volumes.Single(item => item.Package == row.Package);
            using var indexPin = SourceIdentity.OpenPinned(source.IndexPath); using var volumePin = SourceIdentity.OpenPinned(volume.FilePath);
            await ValidateOriginalAsync(source, current, own.Token);
            var full = Path.GetFullPath(destination); var exporter = new RawExportService();
            var plan = exporter.Plan(source, new(Path.GetDirectoryName(full)!, overwrite), [row], own.Token); plan = plan with { Items = [plan.Items[0] with { RelativeOutput = Path.GetFileName(full) }] };
            var report = await exporter.RunAsync(plan, cancellationToken: own.Token); var entry = report.Manifest.Entries.Single();
            if (entry.Status == ExportStatus.Cancelled) throw new OperationCanceledException(own.Token);
            if (entry.Status != ExportStatus.Completed) throw new ArchiveException(entry.ErrorCode ?? "ExportFailed", entry.Error ?? "The output could not be completed.");
        }, own.Token);
        exportTask = task;
        try { await task; }
        finally { if (ReferenceEquals(exportTask, task)) exportTask = null; if (ReferenceEquals(jobCancellation, own)) jobCancellation = null; own.Dispose(); }
    }
    private static Task ValidateOriginalAsync(ArchiveSnapshot source, DecodeResult<BufferInspectionDocument> current, CancellationToken token) => Task.Run(async () =>
    {
        if (current.Revision != new SourceRevision(source.IndexContentId, source.PhysicalSourceStamp)) throw new ArchiveException("SourceChanged", "The inspection belongs to another source revision.");
        var document = current.Value!; var row = source.Records.Single(item => item.Ordinal == current.Identity.Ordinal); var volume = source.Volumes.Single(item => item.Package == row.Package);
        using var indexPin = SourceIdentity.OpenPinned(source.IndexPath); using var volumePin = SourceIdentity.OpenPinned(volume.FilePath); var revisions = new FileRevisionProvider();
        SourceIdentity.EnsureUnchanged(revisions, indexPin, source.IndexPath, source.IndexStamp); SourceIdentity.EnsureUnchanged(revisions, volumePin, volume.FilePath, volume.Stamp);
        if (await SourceIdentity.HashAsync(indexPin, source.IndexStamp.Length, token) != source.IndexContentId) throw new ArchiveException("SourceChanged", "The archive index changed. Reopen it before continuing.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[128 * 1024]; long offset = 0;
        while (offset < row.StoredSize)
        {
            token.ThrowIfCancellationRequested(); var count = await RandomAccess.ReadAsync(volumePin, buffer.AsMemory(0, checked((int)Math.Min(buffer.Length, row.StoredSize - offset))), checked(row.Offset + offset), token);
            if (count == 0) throw new ArchiveException("SourceChanged", "The source ended during validation."); hash.AppendData(buffer.AsSpan(0, count)); offset += count;
        }
        if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), document.OriginalExtent.PayloadSha256, StringComparison.OrdinalIgnoreCase)) throw new ArchiveException("SourceChanged", "The inspected resource changed. Reopen it before continuing.");
        SourceIdentity.EnsureUnchanged(revisions, indexPin, source.IndexPath, source.IndexStamp); SourceIdentity.EnsureUnchanged(revisions, volumePin, volume.FilePath, volume.Stamp);
    }, token);
    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private Task Track(Task task) { operations.Add(task); _ = ObserveAsync(task); return task; }
    private async Task ObserveAsync(Task task) { try { await task; } catch (Exception) { } finally { operations.Remove(task); } }
    private void Cancel_Click(object sender, RoutedEventArgs e) { loadCancellation?.Cancel(); pageCancellation?.Cancel(); jobCancellation?.Cancel(); navigationCancellation?.Cancel(); }
    private void Reset()
    {
        snapshot = null; inspection = null; byteOffset = 0; updating = true;
        try { Sections.ItemsSource = Related.ItemsSource = null; RelatedPanel.Visibility = Visibility.Collapsed; SourceName.Text = "Select a resource"; Summary.Text = Hex.Text = Page.Text = Advanced.Text = ""; SectionName.Text = "Select a section"; ExportOriginal.IsEnabled = ExportJson.IsEnabled = Previous.IsEnabled = Next.IsEnabled = OpenRelated.IsEnabled = false; }
        finally { updating = false; }
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
    }
    public async Task ClearAsync()
    {
        ++generation; ++pageGeneration; ++relatedGeneration; workspace.Cancel(); loadCancellation?.Cancel(); pageCancellation?.Cancel(); jobCancellation?.Cancel(); navigationCancellation?.Cancel(); Reset(); Status.Text = "";
        await Task.WhenAll(operations.Select(async task => { try { await task; } catch (Exception) { } }).ToArray()); pageTask = Task.CompletedTask;
    }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); await ClearAsync(); }
}
