using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Deviant.Assets;
using Microsoft.Win32;

namespace Deviant.Desktop;

public sealed class DiagnosticsWindow : Window
{
    private readonly LocalDiagnosticLog? log;
    private readonly TextBox details = new() { IsReadOnly = true, AcceptsReturn = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock status = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button copy = new() { Content = "Copy error" };

    public DiagnosticsWindow(LocalDiagnosticLog? log)
    {
        this.log = log; Title = "Diagnostics"; Width = 780; Height = 520; MinWidth = 480; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackground"); SetResourceReference(ForegroundProperty, "WindowForeground");
        var layout = new DockPanel { Margin = new Thickness(12) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var open = new Button { Content = "Open logs", IsEnabled = log is not null };
        var export = new Button { Content = "Export diagnostics", IsEnabled = log is not null };
        var refresh = new Button { Content = "Refresh" };
        actions.Children.Add(copy); actions.Children.Add(open); actions.Children.Add(export); actions.Children.Add(refresh);
        DockPanel.SetDock(actions, Dock.Top); layout.Children.Add(actions);
        var build = new TextBlock { Text = log is null ? "Build unavailable" : "Build " + log.BuildId,
            Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(build, Dock.Top); layout.Children.Add(build);
        DockPanel.SetDock(status, Dock.Bottom); layout.Children.Add(status); layout.Children.Add(details); Content = layout;
        refresh.Click += (_, _) => RefreshError();
        copy.Click += (_, _) => { try { Clipboard.SetText(details.Text); } catch (Exception error) { status.Text = error.Message; } };
        open.Click += (_, _) =>
        {
            try { if (this.log is not null) Process.Start(new ProcessStartInfo(this.log.DirectoryPath) { UseShellExecute = true }); }
            catch (Exception error) { status.Text = error.Message; }
        };
        export.Click += async (_, _) =>
        {
            if (this.log is null) return;
            var dialog = new SaveFileDialog { Filter = "Diagnostics (*.zip)|*.zip", FileName = "Deviant-diagnostics.zip" };
            if (dialog.ShowDialog(this) != true) return;
            export.IsEnabled = false;
            try { await this.log.ExportRedactedAsync(dialog.FileName); status.Text = "Diagnostics exported."; }
            catch (Exception error) { WorkflowDiagnostics.Failure("Diagnostics.Export", error); status.Text = error.Message; }
            finally { export.IsEnabled = true; }
        };
        RefreshError();
    }
    private void RefreshError()
    {
        var failure = log?.LastFailure;
        details.Text = failure is null ? "No errors recorded." :
            $"Build: {failure.BuildId}\nOperation: {failure.Operation}\nOperation ID: {failure.OperationId}\nPhase: {failure.Phase}\nCode: {failure.ErrorCode}\n" +
            $"Elapsed: {failure.ElapsedMilliseconds:N1} ms\nCache generation: {failure.CacheGeneration}\n" +
            $"Progress: {failure.Completed} / {failure.Total} {failure.Unit}\nPartial: {failure.Partial}\nRetryable: {failure.Retryable}\n" +
            (failure.MaterialKey is null ? "" : $"Material: {failure.MaterialKey}\n") +
            (failure.AssetKey is null ? "" : $"Asset: {failure.AssetKey}\n") + failure.Exception +
            (failure.Truncated ? "\nThe captured error exceeded the log record limit." : "");
        copy.IsEnabled = failure is not null;
        status.Text = log is null ? "Logging is unavailable." :
            $"{log.DirectoryPath}\nDropped records: {log.DroppedRecords:N0}" + (log.LastWriteError is null ? "" : "\nSome log records could not be written.");
    }
}
