using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Deviant.Core;
using Deviant.Export;
using Microsoft.Win32;

namespace Deviant.Desktop;

public sealed class ExportFailureWindow : Window
{
    private const int EntriesPerPage = 100;
    private readonly ExportManifest manifest;
    private readonly string? manifestPath;
    private readonly string[] sourceRoots;
    private readonly bool acknowledgementRequired;
    private readonly TextBlock pageLabel;
    private readonly TextBlock statusLabel;
    private readonly TextBox entriesText;
    private readonly Button previousButton;
    private readonly Button nextButton;
    private readonly Button saveButton;
    private int pageIndex;

    public ExportFailureWindow(ExportManifest manifest, string? manifestPath, bool acknowledgementRequired = true,
        IReadOnlyList<string>? sourceRoots = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        this.manifest = manifest;
        this.manifestPath = manifestPath;
        this.sourceRoots = (sourceRoots ?? []).Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        this.acknowledgementRequired = acknowledgementRequired;
        Title = acknowledgementRequired ? "Report not saved" : "Export results";
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "WindowForeground");
        Width = 760;
        Height = 560;
        MinWidth = 540;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        var root = new DockPanel { Margin = new Thickness(16) };
        var title = new TextBlock
        {
            Text = acknowledgementRequired ? "Report not saved" : "Export results",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        statusLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 };
        DockPanel.SetDock(statusLabel, Dock.Left);
        footer.Children.Add(statusLabel);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        saveButton = new Button { Content = "Save report…", MinWidth = 100 };
        saveButton.Click += SaveReport_Click;
        buttons.Children.Add(saveButton);
        if (acknowledgementRequired)
        {
            var keepOpenButton = new Button { Content = "Keep open", IsCancel = true, MinWidth = 92 };
            keepOpenButton.Click += (_, _) => DialogResult = false;
            buttons.Children.Add(keepOpenButton);
            var acknowledgeButton = new Button { Content = "Close anyway", IsDefault = true, MinWidth = 104 };
            acknowledgeButton.Click += (_, _) => DialogResult = true;
            buttons.Children.Add(acknowledgeButton);
        }
        else
        {
            var closeButton = new Button { Content = "Close", IsCancel = true, IsDefault = true, MinWidth = 84 };
            closeButton.Click += (_, _) => DialogResult = false;
            buttons.Children.Add(closeButton);
        }
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var paging = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 6) };
        previousButton = new Button { Content = "Previous", MinWidth = 82 };
        previousButton.Click += (_, _) => { pageIndex--; RefreshPage(); };
        pageLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 92, TextAlignment = TextAlignment.Center };
        nextButton = new Button { Content = "Next", MinWidth = 82 };
        nextButton.Click += (_, _) => { pageIndex++; RefreshPage(); };
        paging.Children.Add(previousButton);
        paging.Children.Add(pageLabel);
        paging.Children.Add(nextButton);
        DockPanel.SetDock(paging, Dock.Bottom);
        root.Children.Add(paging);

        var summary = new TextBlock
        {
            Text = BuildSummary(manifest, manifestPath),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(summary, Dock.Top);
        root.Children.Add(summary);

        entriesText = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12
        };
        root.Children.Add(entriesText);
        Content = root;
        RefreshPage();
    }

    private static string BuildSummary(ExportManifest manifest, string? manifestPath)
    {
        var text = new StringBuilder();
        var completed = 0;
        var failed = 0;
        var cancelled = 0;
        var skipped = 0;
        foreach (var entry in manifest.Entries)
        {
            switch (entry.Status)
            {
                case ExportStatus.Completed: completed++; break;
                case ExportStatus.Failed: failed++; break;
                case ExportStatus.Cancelled: cancelled++; break;
                case ExportStatus.Skipped: skipped++; break;
            }
        }
        text.AppendLine($"{manifest.Entries.Count:N0} entries · {completed:N0} complete · {failed:N0} failed · {cancelled:N0} cancelled · {skipped:N0} skipped");
        text.AppendLine(manifest.IndexName);
        if (manifestPath is not null) text.Append(manifestPath);
        return text.ToString();
    }

    private void RefreshPage()
    {
        var pageCount = Math.Max(1, (manifest.Entries.Count + EntriesPerPage - 1) / EntriesPerPage);
        pageIndex = Math.Clamp(pageIndex, 0, pageCount - 1);
        pageLabel.Text = $"{pageIndex + 1:N0} of {pageCount:N0}";
        previousButton.IsEnabled = pageIndex > 0;
        nextButton.IsEnabled = pageIndex + 1 < pageCount;
        var text = new StringBuilder();
        var start = pageIndex * EntriesPerPage;
        var stop = Math.Min(manifest.Entries.Count, start + EntriesPerPage);
        for (var i = start; i < stop; i++)
        {
            var entry = manifest.Entries[i];
            text.AppendLine($"{entry.Ordinal:N0}  {entry.Status}  {entry.RelativeOutput}");
            if (!string.IsNullOrWhiteSpace(entry.Error)) text.AppendLine($"  {entry.ErrorCode}: {entry.Error}");
        }
        entriesText.Text = text.ToString();
    }

    private async void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save export report",
            Filter = "JSON report (*.json)|*.json|All files (*.*)|*.*",
            FileName = "export-report.json",
            AddExtension = true,
            DefaultExt = ".json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        saveButton.IsEnabled = false;
        statusLabel.Text = "Saving…";
        var destination = Path.GetFullPath(dialog.FileName);
        var directory = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var temporaryOwned = false;
        try
        {
            using var directoryPin = OutputSafety.PinDirectory(directory);
            foreach (var sourceRoot in sourceRoots) OutputSafety.ValidateFilePath(destination, sourceRoot);
            foreach (var sourceRoot in sourceRoots) OutputSafety.ValidateFilePath(temporary, sourceRoot);
            await Task.Run(async () =>
            {
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    temporaryOwned = true;
                    await JsonSerializer.SerializeAsync(file, manifest,
                        new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
                    await file.FlushAsync();
                }
                foreach (var sourceRoot in sourceRoots) OutputSafety.ValidateFilePath(destination, sourceRoot);
                foreach (var sourceRoot in sourceRoots) OutputSafety.ValidateFilePath(temporary, sourceRoot);
                File.Move(temporary, destination, overwrite: true);
                temporaryOwned = false;
            });
            statusLabel.Text = "Saved";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            statusLabel.Text = "Could not save the report.";
            MessageBox.Show(this, error.Message, "Save report failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            try { if (temporaryOwned && File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
            saveButton.IsEnabled = true;
        }
    }
}
