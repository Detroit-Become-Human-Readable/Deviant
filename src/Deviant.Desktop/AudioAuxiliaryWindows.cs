using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Export;
using Deviant.Export.Audio;
using Deviant.Audio;
using Deviant.Core;
using Microsoft.Win32;

namespace Deviant.Desktop;

internal sealed class AudioExportOptionsDialog : Window
{
    private readonly ComboBox format = new() { ItemsSource = Enum.GetValues<AudioExportFormat>(), SelectedItem = AudioExportFormat.Original, Margin = new Thickness(0, 4, 0, 8) };
    private readonly ComboBox wavFormat = new() { ItemsSource = Enum.GetValues<WavFormat>(), SelectedItem = WavFormat.Pcm16, Margin = new Thickness(0, 4, 0, 8) };
    private readonly CheckBox allowPartial;

    public AudioExportOptionsDialog(AudioExportReview review, bool explicitPartial)
    {
        Title = "Review audio export"; Width = 430; Height = 340; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize; FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        allowPartial = new CheckBox { Content = "Include original partial fragments", IsChecked = explicitPartial, Margin = new Thickness(0, 8, 0, 4) };
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = $"{review.Rows.Count:N0} resources · {review.Bytes:N0} source bytes", FontSize = 16, FontWeight = FontWeights.SemiBold });
        if (review.PartialCount > 0 || review.UnavailableCount > 0)
            content.Children.Add(new TextBlock { Text = $"Partial: {review.PartialCount:N0} · Unavailable: {review.UnavailableCount:N0}", Margin = new Thickness(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "Format" }); content.Children.Add(format);
        content.Children.Add(new TextBlock { Text = "WAV precision" }); content.Children.Add(wavFormat);
        content.Children.Add(allowPartial);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(4) };
        var export = new Button { Content = "Export", IsDefault = true, MinWidth = 80, Margin = new Thickness(4) };
        export.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(export); content.Children.Add(buttons);
        Content = content;
    }

    public AudioExportOptions CreateOptions(string outputDirectory, int conversionWorkers) => new(outputDirectory,
        format.SelectedItem is AudioExportFormat selected ? selected : AudioExportFormat.Original,
        wavFormat.SelectedItem is WavFormat precision ? precision : WavFormat.Pcm16,
        Overwrite: false, AllowPartialOriginal: allowPartial.IsChecked == true, ConversionWorkers: conversionWorkers);
}

internal sealed class AudioResultsWindow : Window
{
    private readonly AudioExportReport report;
    private readonly string? sourceRoot;
    private readonly TextBlock saveStatus = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    public AudioResultsWindow(AudioExportReport report, string? sourceRoot = null)
    {
        this.report = report;
        this.sourceRoot = sourceRoot;
        Title = "Audio export results"; Width = 960; Height = 620; MinWidth = 720; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        var rows = report.Manifest.Entries.Select(entry => new AudioResultRow(
            Path.GetFileName(entry.RelativeOutput), entry.Completeness switch
            {
                Deviant.Formats.Audio.Model.MediaCompleteness.Fragment => "Partial",
                _ => ""
            }, entry.Status.ToString(), entry.Diagnostic ?? entry.ErrorCode ?? "", entry.RelativeOutput)).ToArray();
        var list = new ListView { ItemsSource = rows, Margin = new Thickness(12) };
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Output", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(AudioResultRow.FileName)), Width = 240 });
        view.Columns.Add(new GridViewColumn { Header = "Completeness", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(AudioResultRow.Completeness)), Width = 170 });
        view.Columns.Add(new GridViewColumn { Header = "Result", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(AudioResultRow.Status)), Width = 90 });
        view.Columns.Add(new GridViewColumn { Header = "Details", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(AudioResultRow.Diagnostic)), Width = 380 });
        list.View = view;
        var panel = new DockPanel();
        var summary = new TextBlock { Text = $"{report.Manifest.Entries.Count(entry => entry.Status == AudioExportStatus.Completed):N0} completed · {report.Manifest.Entries.Count(entry => entry.Status == AudioExportStatus.Failed):N0} failed · {report.Manifest.Entries.Count(entry => entry.Status == AudioExportStatus.Skipped):N0} skipped · {report.Manifest.Entries.Count(entry => entry.Status == AudioExportStatus.Cancelled):N0} cancelled\n{report.ManifestPath}", Margin = new Thickness(12, 10, 12, 0), TextWrapping = TextWrapping.Wrap };
        var footer = new DockPanel { Margin = new Thickness(12, 0, 12, 10) };
        var save = new Button { Content = "Save report", HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += async (_, _) => await SaveReportAsync();
        DockPanel.SetDock(save, Dock.Right); footer.Children.Add(save); footer.Children.Add(saveStatus);
        DockPanel.SetDock(summary, Dock.Top); DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(summary); panel.Children.Add(footer); panel.Children.Add(list); Content = panel;
    }

    private async Task SaveReportAsync()
    {
        if (sourceRoot is null)
        {
            saveStatus.Text = "The archive source is not available for output safety checks.";
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Save audio export report",
            Filter = "JSON report (*.json)|*.json",
            FileName = Path.GetFileName(report.ManifestPath),
            AddExtension = true,
            DefaultExt = ".json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var target = Path.GetFullPath(dialog.FileName);
        var parent = Path.GetDirectoryName(target)!;
        string? temporary = null;
        try
        {
            OutputSafety.ValidateFilePath(target, sourceRoot);
            Directory.CreateDirectory(parent);
            using var parentPin = OutputSafety.PinDirectory(parent);
            OutputSafety.ValidateFilePath(target, sourceRoot);
            temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, report.Manifest,
                    new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } },
                    CancellationToken.None);
                await stream.FlushAsync(CancellationToken.None);
                stream.Flush(flushToDisk: true);
            }
            OutputSafety.ValidateFilePath(target, sourceRoot);
            File.Move(temporary, target, overwrite: true);
            saveStatus.Text = "Report saved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            saveStatus.Text = ex is ArchiveException archive ? archive.Message : "The report could not be saved.";
        }
        finally
        {
            if (temporary is not null)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private sealed record AudioResultRow(string FileName, string Completeness, string Status, string Diagnostic, string RelativePath);
}

internal sealed class AudioCacheWindow : Window
{
    private readonly CacheManager cache;
    private readonly string? sourceRoot;
    private readonly Func<CancellationToken, Task> drain;
    private readonly Func<CancellationToken, Task> reopen;
    private readonly TextBlock location = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock usage = new();
    private readonly TextBox limit = new() { Width = 100, Text = "4" };
    private readonly ComboBox workers = new() { Width = 100, ItemsSource = new[] { "Auto", "1", "2", "4" } };

    public AudioCacheWindow(CacheManager cache, string? sourceRoot,
        Func<CancellationToken, Task> drain, Func<CancellationToken, Task> reopen)
    {
        this.cache = cache; this.sourceRoot = sourceRoot; this.drain = drain; this.reopen = reopen;
        Title = "Cache"; Width = 580; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Location", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(location); panel.Children.Add(usage);
        var limitPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 6) };
        limitPanel.Children.Add(new TextBlock { Text = "Disk cache limit (GB)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        limit.Text = (cache.LimitBytes / (1024d * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture);
        limitPanel.Children.Add(limit); var save = new Button { Content = "Save limit", Margin = new Thickness(8, 0, 0, 0) };
        save.Click += async (_, _) => await SaveLimitAsync(); limitPanel.Children.Add(save); panel.Children.Add(limitPanel);
        var workerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 10) };
        workerPanel.Children.Add(new TextBlock { Text = "Conversion workers", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        workers.SelectedItem = cache.Workers==0?"Auto":cache.Workers.ToString(CultureInfo.InvariantCulture); workerPanel.Children.Add(workers);
        var saveWorkers = new Button { Content = "Save", Margin = new Thickness(8, 0, 0, 0) };
        saveWorkers.Click += async (_, _) => await SaveWorkersAsync(); workerPanel.Children.Add(saveWorkers); panel.Children.Add(workerPanel);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var move = new Button { Content = "Move cache" }; move.Click += async (_, _) => await MoveAsync();
        var clear = new Button { Content = "Clear cache" }; clear.Click += async (_, _) => await ClearAsync();
        var rebuild = new Button { Content = "Rebuild metadata" }; rebuild.Click += async (_, _) => await RebuildAsync();
        actions.Children.Add(move); actions.Children.Add(clear); actions.Children.Add(rebuild); panel.Children.Add(actions);
        if(cache.PreviousLocations.Count>0)
        {
            var previous=new ComboBox{ItemsSource=cache.PreviousLocations,SelectedIndex=0,Margin=new Thickness(0,12,0,4)};
            var remove=new Button{Content="Remove previous cache",HorizontalAlignment=HorizontalAlignment.Left};
            remove.Click+=async(_,_)=>
            {
                if(previous.SelectedItem is not string path) return;
                if(sourceRoot is not null&&DirectoryBoundary.IsWithin(path,sourceRoot)) {MessageBox.Show(this,"Choose a cache outside the archive folder.");return;}
                if(MessageBox.Show(this,"Remove the selected previous cache?","Remove cache",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                try{await cache.RemovePreviousAsync(path);previous.ItemsSource=cache.PreviousLocations;await RefreshTextAsync();}catch(Exception e){MessageBox.Show(this,e.Message,"Cache");}
            };
            panel.Children.Add(previous);panel.Children.Add(remove);
        }
        Content = panel; _ = RefreshTextAsync();
    }

    private async Task RefreshTextAsync()
    {
        location.Text = cache.Location;
        try
        {
            var values = await Task.Run(() => (Preview: cache.PreviewUsageBytes, Metadata: cache.MetadataUsageBytes, Total:cache.UsageBytes, Free: cache.AvailableBytes));
            usage.Text = $"Previews {values.Preview / 1048576d:N1} MB · Metadata {values.Metadata / 1048576d:N1} MB\nTotal {values.Total / 1048576d:N1} MB · Free {values.Free / 1073741824d:N1} GB";
        }
        catch (Exception ex) { usage.Text = ex.Message; }
    }

    private async Task SaveLimitAsync()
    {
        if (!double.TryParse(limit.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) || gb < 0 || gb > long.MaxValue / (1024d * 1024 * 1024))
        { MessageBox.Show(this, "Enter zero or a positive cache size in GB.", "Audio cache", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try { await cache.SetLimitAsync(checked((long)(gb * 1024 * 1024 * 1024))); await RefreshTextAsync(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Audio cache", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task SaveWorkersAsync()
    {
        if (workers.SelectedItem is not string value) return;
        var count=value=="Auto"?0:int.Parse(value,CultureInfo.InvariantCulture);
        try { await cache.SetWorkersAsync(count); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Audio cache", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task MoveAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Choose an empty cache folder" };
        if (dialog.ShowDialog(this) != true) return;
        var destination = Path.GetFullPath(dialog.FolderName);
        if (sourceRoot is not null && DirectoryBoundary.IsWithin(destination, sourceRoot))
        { MessageBox.Show(this, "Cache cannot be inside the archive source folder.", "Audio cache", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try { await cache.RelocateAsync(destination, drain); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Audio cache", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { try { await reopen(CancellationToken.None); } catch { } await RefreshTextAsync(); }
    }

    private async Task ClearAsync()
    {
        try { await cache.ClearAsync(drain); await RefreshTextAsync(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Audio cache", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task RebuildAsync()
    {
        try { await cache.ClearAsync(drain); await reopen(CancellationToken.None); await RefreshTextAsync(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Audio cache", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
