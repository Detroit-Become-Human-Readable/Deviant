using System.Diagnostics;
using System.IO;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Deviant.Browser;
using Deviant.Core;
using Deviant.Desktop;
using Deviant.Export;

namespace Deviant.UiCheck;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i++) if (args[i].StartsWith("--")) options[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
        if (!options.TryGetValue("out", out var output)) { Console.Error.WriteLine("Deviant.UiCheck --out <private directory> [--input <index>] [--keep-open]"); return 1; }
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var input = options.GetValueOrDefault("input") ?? SyntheticArchive.Create(Path.Combine(output, "synthetic"), 375_000);
        var app = new App { ThemeOverride = options.GetValueOrDefault("theme") switch { "light" => true, "dark" => false, _ => null } };
        app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var dispatcher = new MeasuredDispatcher(app.Dispatcher);
        var exports = new ObservedExports();
        var archiveReported = new ConcurrentDictionary<ArchiveProgress, long>();
        var controller = new BrowserController(dispatcher, Path.Combine(output, "cache"),
            async (path, cache, progress, token) => await CatalogSession.OpenAsync(path, cache, new CallbackProgress<ArchiveProgress>(value =>
            { archiveReported[value] = Stopwatch.GetTimestamp(); progress?.Report(value); }), token), exports);
        var result = 1;
        app.ControllerFactory = _ => controller;
        app.WindowInitialized = window =>
        {
            if (options.TryGetValue("width", out var width)) window.Width = double.Parse(width, System.Globalization.CultureInfo.InvariantCulture);
            window.Loaded += async (_, _) =>
            {
            try
            {
                var report = await ExerciseAsync(window, controller, dispatcher, exports, archiveReported, input, output);
                File.WriteAllText(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(JsonSerializer.Serialize(report)); result = report.Passed ? 0 : 2;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "ui-error.txt"), ex.ToString()); Console.Error.WriteLine(ex); result = 1;
            }
            finally
            {
                if (!options.ContainsKey("keep-open")) { await controller.CloseAsync(true); window.Close(); app.Shutdown(result); }
            }
            };
        };
        app.Run(); return result;
    }

    private static async Task<Report> ExerciseAsync(MainWindow window, BrowserController controller, MeasuredDispatcher dispatcher, ObservedExports exports,
        ConcurrentDictionary<ArchiveProgress, long> archiveReported, string input, string output)
    {
        var heartbeat = new List<double>(); var beatClock = Stopwatch.StartNew(); var last = beatClock.Elapsed.TotalMilliseconds;
        var phase = "Open"; var delayed = new List<string>(); var lastGcPause = GC.GetTotalPauseDuration().TotalMilliseconds;
        var progressLatency = new List<double>(); var archiveLatency = new List<double>(); ExportProgress? shown = null; ArchiveProgress? shownArchive = null;
        controller.StateChanged += (_, _) =>
        {
            var archiveValue = controller.State.ArchiveProgress;
            if (archiveValue is not null && !ReferenceEquals(archiveValue, shownArchive))
            {
                shownArchive = archiveValue;
                if (archiveReported.TryGetValue(archiveValue, out var reportedAt)) archiveLatency.Add(Stopwatch.GetElapsedTime(reportedAt).TotalMilliseconds);
            }
            var value = controller.State.ExportProgress;
            if (value is null || ReferenceEquals(value, shown)) return;
            shown = value;
            if (exports.Reported.TryGetValue(value, out var timestamp))
                progressLatency.Add(Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds);
        };
        var queuedInput = new List<double>(); var probePending = 0;
        using var probe = new Timer(_ =>
        {
            if (Interlocked.CompareExchange(ref probePending, 1, 0) != 0) return;
            var queuedAt = Stopwatch.GetTimestamp();
            _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                queuedInput.Add(Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds);
                Volatile.Write(ref probePending, 0);
            }));
        }, null, Timeout.Infinite, Timeout.Infinite);
        var timer = new DispatcherTimer(DispatcherPriority.Input, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            var now = beatClock.Elapsed.TotalMilliseconds; var delay = Math.Max(0, now - last - 20);
            var gcPause = GC.GetTotalPauseDuration().TotalMilliseconds;
            heartbeat.Add(delay); if (delay > 100) delayed.Add($"{phase}: {delay:F1}ms (GC pause {gcPause - lastGcPause:F1}ms)");
            last = now; lastGcPause = gcPause;
        };
        await Task.Delay(300); last = beatClock.Elapsed.TotalMilliseconds;
        timer.Start(); probe.Change(0, 20); dispatcher.Durations.Clear();
        var open = Stopwatch.StartNew(); await controller.OpenAsync(input); open.Stop();
        Require(controller.State.Snapshot is not null, "Catalog opened: " + controller.State.Error);
        Require(window.Icon is BitmapSource { PixelWidth: 256, PixelHeight: 256 }, "Window icon is loaded");
        Require(controller.State.Rows.Count <= 500, "Only current page is bound");
        await Idle(window.Dispatcher);
        timer.Stop(); probe.Change(Timeout.Infinite, Timeout.Infinite); await Idle(window.Dispatcher);
        Capture(window, Path.Combine(output, "browser.png")); last = beatClock.Elapsed.TotalMilliseconds; timer.Start(); probe.Change(0, 20);
        var timings = new List<double>();
        phase = "Warm queries and rendering";
        for (var i = 0; i < 24; i++)
        {
            var query = (i % 3) switch
            {
                0 => new ResourceQuery(Offset: i * 100L, Limit: 100),
                1 => new ResourceQuery(Package: controller.State.Snapshot!.Volumes[i % controller.State.Snapshot.Volumes.Count].Package, Limit: 100),
                _ => new ResourceQuery(Type: controller.State.Snapshot!.Records[i % controller.State.Snapshot.Records.Count].Type, Limit: 100)
            };
            var watch = Stopwatch.StartNew(); await controller.QueryAsync(query); watch.Stop(); timings.Add(watch.Elapsed.TotalMilliseconds);
            await Idle(window.Dispatcher);
        }
        phase = "Package and type navigation";
        var tree = (TreeView)window.FindName("NavigationTree");
        var treeRoot = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0); treeRoot.IsExpanded = true; await Idle(window.Dispatcher);
        var packageItem = (TreeViewItem)treeRoot.ItemContainerGenerator.ContainerFromIndex(0); packageItem.IsExpanded = true; await Idle(window.Dispatcher);
        var typeItem = (TreeViewItem)packageItem.ItemContainerGenerator.ContainerFromIndex(0);
        var typeNode = controller.State.Navigation[0].Children[0].Children[0]; typeItem.IsSelected = true;
        await Until(() => controller.State.Query.Package == typeNode.Package && controller.State.Query.Type == typeNode.Type && !controller.State.IsQuerying);
        Require(controller.State.Total == typeNode.Count, "Navigation count matches filtered catalog");
        Require(controller.State.Rows.All(r => r.Package == typeNode.Package && r.Type == typeNode.Type), "Navigation selects its package and type");
        var matchingReview = await controller.PrepareExportAsync(Deviant.Browser.ExportScope.Matching);
        Require(matchingReview?.Records.Count == typeNode.Count, "All-matching review includes rows beyond the page");
        treeRoot.IsSelected = true; await Until(() => controller.State.Query.Package is null && controller.State.Query.Type is null && !controller.State.IsQuerying);
        phase = "Selection and export"; await controller.QueryAsync(new(Limit: 100));
        var first = controller.State.Rows[0]; controller.SetSelected(first.Ordinal, true); controller.Focus(first);
        await controller.QueryAsync(new(Offset: 100, Limit: 100));
        Require(controller.State.Selected.Contains(first.Ordinal), "Selection survives paging");
        var second = controller.State.Rows[0]; controller.SetSelected(second.Ordinal, true);
        var review = await controller.PrepareExportAsync(Deviant.Browser.ExportScope.Selected); Require(review is not null, "Selected review exists");
        await controller.ExportAsync(review!, new(Path.Combine(output, "raw-" + Guid.NewGuid().ToString("N"))));
        Require(controller.State.LastExport?.Manifest.Entries.All(e => e.Status == ExportStatus.Completed) == true, "Small raw batch completed");
        Require(File.Exists(controller.State.LastExport!.ManifestPath), "Export manifest exists");
        await VerifyRawAsync(review!.Snapshot, controller.State.LastExport);
        await controller.QueryAsync(new(Id: first.Id, Limit: 100));
        var exactReview = await controller.PrepareExportAsync(Deviant.Browser.ExportScope.Matching);
        Require(exactReview is not null && exactReview.Records.Count == controller.State.Total, "Matching export freezes the filtered selection");
        await controller.ExportAsync(exactReview!, new(Path.Combine(output, "matching-" + Guid.NewGuid().ToString("N"))));
        Require(controller.State.LastExport!.Manifest.Entries.All(e => e.Status == ExportStatus.Completed), "All-matching raw export completed");
        await VerifyRawAsync(exactReview!.Snapshot, controller.State.LastExport);
        phase = "Cancellation and source switching";
        var cancelRecords = controller.State.Snapshot!.Records.Where(r => r.Validity == ResourceValidity.Valid && r.StoredSize > 0)
            .OrderBy(r => r.StoredSize).Take(5000).ToArray();
        var cancelReview = new ExportReview(controller.State.Snapshot, cancelRecords, cancelRecords.Sum(r => r.StoredSize), 0);
        exports.Reset();
        var pendingExport = controller.ExportAsync(cancelReview, new(Path.Combine(output, "cancelled-" + Guid.NewGuid().ToString("N"))));
        await exports.FirstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // Refresh while extraction uses its frozen snapshot, then cancel after progress is visible.
        await controller.RefreshAsync(); await Idle(window.Dispatcher); await Task.Delay(150);
        var feedback = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelTimestamp = Stopwatch.GetTimestamp();
        EventHandler feedbackHandler = (_, _) =>
        {
            if (controller.State.Status.StartsWith("Cancelling export", StringComparison.Ordinal))
                feedback.TrySetResult(Stopwatch.GetElapsedTime(cancelTimestamp).TotalMilliseconds);
        };
        controller.StateChanged += feedbackHandler;
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        var feedbackMs = await feedback.Task.WaitAsync(TimeSpan.FromSeconds(5)); controller.StateChanged -= feedbackHandler;
        await pendingExport;
        var finalizationMs = Stopwatch.GetElapsedTime(cancelTimestamp).TotalMilliseconds;
        var cancelledReport = controller.State.LastExport!;
        Require(cancelledReport.Manifest.Cancelled, "Cancellation is recorded");
        Require(cancelledReport.Manifest.Entries.Any(e => e.Status == ExportStatus.Completed), "Committed files survive cancellation");
        Require(cancelledReport.Manifest.Entries.Any(e => e.Status == ExportStatus.Cancelled), "Unfinished files are cancelled");
        Require(cancelledReport.Manifest.Entries.All(e => e.Status != ExportStatus.Pending), "Every file has a terminal manifest status");
        Require(!await Task.Run(() => Directory.EnumerateFiles(Path.GetDirectoryName(cancelledReport.ManifestPath)!, "*.tmp", SearchOption.AllDirectories).Any()), "No owned incomplete file remains");
        await VerifyRawAsync(cancelReview.Snapshot, cancelledReport);
        phase = "Filter controls and keyboard routing";
        var idInput = (TextBox)window.FindName("IdFilterTextBox");
        idInput.Text = $"0x{first.Id:X8}";
        await Until(() => controller.State.Query.Id == first.Id && !controller.State.IsQuerying);
        Require(controller.State.Rows.All(r => r.Id == first.Id), "Hex ID textbox applies its debounced filter");
        idInput.Text = first.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await Task.Delay(300); await Idle(window.Dispatcher);
        Require(controller.State.Query.Id == first.Id, "Decimal ID textbox applies its filter");
        idInput.Clear(); await Until(() => controller.State.Query.Id is null && !controller.State.IsQuerying);
        var searchInput = (TextBox)window.FindName("SearchTextBox");
        searchInput.Text = "no-match-long-string";
        await Until(() => controller.State.Total == 0 && !controller.State.IsQuerying);
        searchInput.Clear(); await Until(() => controller.State.Total > 0 && !controller.State.IsQuerying);
        var sizes = (ComboBox)window.FindName("PageSizeComboBox"); sizes.SelectedItem = 1000;
        await Until(() => controller.State.Query.Limit == 1000 && !controller.State.IsQuerying);
        Require(controller.State.Rows.Count == 1000, "Page size control binds the requested page"); await Idle(window.Dispatcher);
        controller.SelectPage(); await Until(() => controller.State.Rows.All(r => controller.State.Selected.Contains(r.Ordinal))); await Idle(window.Dispatcher);
        var selectedGrid = (ListView)window.FindName("ResourceGrid"); selectedGrid.UnselectAll(); selectedGrid.SelectedItem = selectedGrid.Items[0];
        await Until(() => controller.State.Selected.Count == 1); await Idle(window.Dispatcher);
        controller.ClearSelection(); await Until(() => controller.State.Selected.Count == 0); await Idle(window.Dispatcher);
        Require(searchInput.Focus(), "Search accepts keyboard focus");
        Require(searchInput.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)), "Tab traversal reaches another control");
        var beforeRefresh = controller.State.Snapshot;
        NavigationCommands.Refresh.Execute(null, window);
        await Until(() => !ReferenceEquals(beforeRefresh, controller.State.Snapshot) && !controller.State.IsOpening);
        Require(window.InputBindings.OfType<KeyBinding>().Any(k => k.Key == Key.O && k.Modifiers == ModifierKeys.Control), "Open shortcut is registered");
        Require(window.InputBindings.OfType<KeyBinding>().Any(k => k.Key == Key.F5), "Refresh shortcut is registered");
        phase = "Broad search and supersession"; await controller.QueryAsync(new(Search: "no-match-long-string", Limit: 100)); Require(controller.State.Total == 0, "Empty filter works");
        var slow = controller.QueryAsync(new(Search: "1", Limit: 100)); var cancelWatch = Stopwatch.StartNew();
        var latest = controller.QueryAsync(new(Id: first.Id, Limit: 100)); await Task.WhenAll(slow, latest); cancelWatch.Stop();
        Require(controller.State.Rows.All(r => r.Id == first.Id), "Superseded search cannot publish stale results");
        phase = "Final 500-row page"; await controller.QueryAsync(new(Limit: 500));
        controller.Focus(controller.State.Rows[0]); await Idle(window.Dispatcher);
        var grid = (ListView)window.FindName("ResourceGrid"); grid.ScrollIntoView(grid.Items[0]); await Idle(window.Dispatcher);
        var realized = CountVisual<ListViewItem>(grid);
        Require(realized < controller.State.Rows.Count, "Table row virtualization is active");
        timer.Stop();
        probe.Change(Timeout.Infinite, Timeout.Infinite); await Idle(window.Dispatcher);
        Capture(window, Path.Combine(output, "browser-selected.png"));
        Capture(window, Path.Combine(output, "browser-render-144dpi.png"), 144);
        Capture(window, Path.Combine(output, "browser-render-192dpi.png"), 192);
        sizes.IsDropDownOpen = true; await Idle(window.Dispatcher);
        if (sizes.Template.FindName("PART_Popup", sizes) is Popup { Child: FrameworkElement popup })
            Capture(popup, Path.Combine(output, "page-size-popup.png"));
        sizes.IsDropDownOpen = false;
        var maximumUi = dispatcher.Durations.DefaultIfEmpty().Max(); var p95Heartbeat = Percentile(heartbeat, .95); var maximumHeartbeat = heartbeat.DefaultIfEmpty().Max();
        var p95Page = Percentile(timings, .95);
        var maximumProgress = progressLatency.DefaultIfEmpty().Max(); var maximumArchiveProgress = archiveLatency.DefaultIfEmpty().Max();
        var passed = maximumUi <= 50 && p95Heartbeat <= 100 && maximumHeartbeat <= 250 && p95Page <= 250 &&
            feedbackMs <= 100 && maximumProgress <= 250 && maximumArchiveProgress <= 250 && queuedInput.DefaultIfEmpty().Max() <= 250;
        return new(passed, controller.State.Snapshot!.Records.Count, open.Elapsed.TotalMilliseconds, p95Page, maximumUi,
            p95Heartbeat, maximumHeartbeat, cancelWatch.Elapsed.TotalMilliseconds, realized, timings, dispatcher.Durations.ToArray(), delayed,
            Percentile(queuedInput, .95), queuedInput.DefaultIfEmpty().Max(), feedbackMs, maximumProgress,
            cancelledReport.Manifest.Entries.Count(e => e.Status == ExportStatus.Completed),
            cancelledReport.Manifest.Entries.Count(e => e.Status == ExportStatus.Cancelled), maximumArchiveProgress, finalizationMs,
            controller.State.Snapshot.IndexContentId, VisualTreeHelper.GetDpi(window).PixelsPerInchX);
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("A browser control did not reach its expected state.");
            await Task.Delay(20);
        }
    }
    private static Task VerifyRawAsync(ArchiveSnapshot snapshot, ExportReport report) => Task.Run(async () =>
    {
        foreach (var entry in report.Manifest.Entries.Where(e => e.Status == ExportStatus.Completed))
        {
            var destination = Path.Combine(Path.GetDirectoryName(report.ManifestPath)!, entry.RelativeOutput);
            var bytes = await File.ReadAllBytesAsync(destination);
            var volume = snapshot.Volumes.Single(v => v.Package == entry.Package);
            using var source = File.OpenHandle(volume.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var expected = new byte[bytes.Length]; var read = 0;
            while (read < expected.Length)
            {
                var count = await RandomAccess.ReadAsync(source, expected.AsMemory(read), entry.Offset + read);
                Require(count > 0, "Source slice is complete"); read += count;
            }
            Require(bytes.AsSpan().SequenceEqual(expected), "Raw bytes match independent source slice");
            Require(Convert.ToHexString(SHA256.HashData(bytes)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase), "Manifest hash matches output");
        }
    });
    private static Task Idle(Dispatcher dispatcher) => dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task;
    private static void Require(bool condition, string name) { if (!condition) throw new InvalidOperationException("UI check failed: " + name); }
    private static double Percentile(List<double> values, double fraction) => values.Count == 0 ? 0 : values.Order().ElementAt((int)Math.Ceiling(values.Count * fraction) - 1);
    private static int CountVisual<T>(DependencyObject root) where T : DependencyObject
    { var count = root is T ? 1 : 0; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) count += CountVisual<T>(VisualTreeHelper.GetChild(root, i)); return count; }
    public static void Capture(FrameworkElement element, string path, double? renderDpi = null)
    {
        element.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(element);
        var dpiX = renderDpi ?? dpi.PixelsPerInchX; var dpiY = renderDpi ?? dpi.PixelsPerInchY;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpiX / 96), (int)Math.Ceiling(element.ActualHeight * dpiY / 96), dpiX, dpiY, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class MeasuredDispatcher(Dispatcher dispatcher) : IUiDispatcher
    {
        public List<double> Durations { get; } = [];
        public Task InvokeAsync(Action action)
        {
            if (dispatcher.CheckAccess()) { Measure(action); return Task.CompletedTask; }
            return dispatcher.InvokeAsync(() => Measure(action), DispatcherPriority.DataBind).Task;
        }
        private void Measure(Action action) { var timer = Stopwatch.StartNew(); action(); timer.Stop(); Durations.Add(timer.Elapsed.TotalMilliseconds); }
    }
    private sealed record Report(bool Passed, int Resources, double OpenMs, double WarmPageP95Ms, double MaximumDispatcherCallbackMs,
        double HeartbeatP95DelayMs, double MaximumHeartbeatDelayMs, double SupersededQueryDrainMs, int RealizedRows,
        IReadOnlyList<double> PageSamplesMs, IReadOnlyList<double> DispatcherSamplesMs, IReadOnlyList<string> DelayedPhases,
        double InputQueueP95Ms, double MaximumInputQueueMs, double CancelFeedbackMs, double MaximumProgressLatencyMs,
        int CompletedBeforeCancel, int CancelledFiles, double MaximumArchiveProgressLatencyMs, double CancelToManifestMs,
        string IndexContentId, double NativeDpi);

    private sealed class CallbackProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }

    private sealed class ObservedExports : IExportService
    {
        private readonly RawExportService inner = new();
        public ConcurrentDictionary<ExportProgress, long> Reported { get; } = new();
        public TaskCompletionSource FirstCompleted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reset() { Reported.Clear(); FirstCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public ExportPlan Plan(ArchiveSnapshot snapshot, ExportOptions options, IEnumerable<ResourceRecord> selected) => inner.Plan(snapshot, options, selected);
        public ExportPlan Plan(ArchiveSnapshot snapshot, ExportOptions options, IEnumerable<ResourceRecord> selected, CancellationToken token, IProgress<ArchiveProgress>? progress = null) => inner.Plan(snapshot, options, selected, token, progress);
        public Task<ExportReport> RunAsync(ExportPlan plan, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default) =>
            inner.RunAsync(plan, new InlineProgress(value =>
            {
                Reported[value] = Stopwatch.GetTimestamp(); progress?.Report(value);
                if (value.CompletedFiles > 0) FirstCompleted.TrySetResult();
            }), cancellationToken);
        private sealed class InlineProgress(Action<ExportProgress> action) : IProgress<ExportProgress> { public void Report(ExportProgress value) => action(value); }
    }

    private static class SyntheticArchive
    {
        public static string Create(string root, int count)
        {
            Directory.CreateDirectory(root); var index = Path.Combine(root, "synthetic.idx");
            var bytes = new byte[IndexLayout.HeaderLength + count * IndexLayout.RecordLength]; IndexLayout.Magic.CopyTo(bytes);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 18); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(101), (uint)count);
            var lengths = new int[30];
            for (var i = 0; i < count; i++)
            {
                var package = i % 30; var values = new[] { (uint)(1000 + i % 17), (uint)(i % 7), (uint)(i + 1), (uint)lengths[package], 8u, 0u, (uint)package };
                for (var j = 0; j < values.Length; j++) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(105 + i * 28 + j * 4), values[j]);
                lengths[package] += 8;
            }
            File.WriteAllBytes(index, bytes);
            for (uint p = 0; p < lengths.Length; p++) File.WriteAllBytes(Path.Combine(root, IndexLayout.VolumeName("synthetic", p)), new byte[lengths[p]]);
            return index;
        }
    }
}
