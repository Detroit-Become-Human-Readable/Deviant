using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Deviant.Browser;
using Deviant.Core;
using Microsoft.Win32;

namespace Deviant.Desktop;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly IBrowserController controller;
    private readonly DispatcherTimer filterTimer;
    private readonly int[] pageSizes = [100, 500, 1000];
    private bool syncingRows;
    private bool syncingFilters;
    private bool handlingGridSelection;
    private bool closeApproved;
    private bool closePending;
    private string? localError;
    private BrowserState? deferredControllerState;
    private ResourceRecord? detailRecord;
    private ArchiveSnapshot? detailSnapshot;

    public event PropertyChangedEventHandler? PropertyChanged;
    public Func<ArchiveSnapshot,ResourceRecord,string,Task>? InspectionRequested { get; set; }
    public bool CanInspect=>InspectionRequested is not null&&State.Snapshot is not null&&State.Focused is not null;
    public BrowserState State { get; private set; }
    public ResourceRowCollection Rows { get; } = [];
    public IReadOnlyList<int> PageSizes => pageSizes;
    public ResourceDetails Details => new(detailSnapshot, detailRecord);
    public string SourceLabel => State.Snapshot is { } snapshot ? Path.GetFileName(snapshot.IndexPath) : "No source open";
    public string? SourceIndexPath => State.Snapshot?.IndexPath;
    public IReadOnlyList<NavigationNode> Navigation => State.Navigation;
    public bool IsOpening => State.IsOpening;
    public bool IsExporting => State.IsExporting;
    public bool IsProcessing => State.IsOpening || State.IsQuerying || State.IsExporting;
    public bool HasReport => State.LastExport is not null || State.RetainedManifest is not null;
    public string StatusText => localError ?? State.Error ?? State.Status;
    public string ProgressText => GetProgressText();
    public double ProgressMaximum => Math.Max(1, State.IsExporting ? State.ExportProgress?.TotalFiles ?? 1 : State.ArchiveProgress?.Total ?? 1);
    public double ProgressValue => State.IsExporting ? State.ExportProgress?.CompletedFiles ?? 0 : State.ArchiveProgress?.Completed ?? 0;
    public bool IsProgressIndeterminate => State.IsQuerying || State.IsOpening && State.ArchiveProgress is null || State.IsExporting && State.ExportProgress is null;
    public bool CanStartOpen => !State.IsOpening && !State.IsClosing && State.RetainedManifest is null;
    public bool CanRefresh => State.InputPath is not null && !State.IsOpening && !State.IsClosing && State.RetainedManifest is null;
    public bool CanExport => State.CanExport && State.Total > 0 && State.RetainedManifest is null;
    public bool CanExportSelected => State.CanExport && State.Selected.Count > 0 && State.RetainedManifest is null;
    public bool CanPreviousPage => State.Query.Offset > 0 && !State.IsQuerying;
    public bool CanNextPage => State.Query.Offset + State.Query.Limit < State.Total && !State.IsQuerying;
    public string PageText
    {
        get
        {
            var page = State.Total == 0 ? 0 : State.Query.Offset / Math.Max(1, State.Query.Limit) + 1;
            var pages = State.Total == 0 ? 0 : (State.Total + State.Query.Limit - 1) / State.Query.Limit;
            return $"{page:N0} of {pages:N0}  ·  {State.Total:N0} rows";
        }
    }
    public string SelectionText => $"{State.Selected.Count:N0} selected";

    public MainWindow(IBrowserController controller)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        State = controller.State;
        InitializeComponent();
        DataContext = this;
        filterTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        filterTimer.Tick += FilterTimer_Tick;
        controller.StateChanged += Controller_StateChanged;
        ApplyState(controller.State, rebuildRows: true);
        Closed += MainWindow_Closed;
    }

    private void Controller_StateChanged(object? sender, EventArgs e)
    {
        if (handlingGridSelection)
        {
            deferredControllerState = controller.State;
            return;
        }
        ApplyState(controller.State, rebuildRows: false);
        OnPropertyChanged(nameof(CanInspect));
    }
    private void Inspect_Click(object sender,RoutedEventArgs e)
    {
        if(InspectionRequested is not{}inspect||State.Snapshot is not{}source||State.Focused is not{}row)return;
        var menu=new ContextMenu();
        void Add(string label,string kind){var item=new MenuItem{Header=label};item.Click+=async(_,_)=>await RunControllerAsync(()=>inspect(source,row,kind));menu.Items.Add(item);}
        Add("References","references");
        if(row.Type==29)Add("Shader sources","shader");
        if(row.Type==29)Add("Physics","physics");
        if(row.Type==29)Add("Common groups","gmk");
        if(row.Type==Deviant.Formats.Animation.AnimationDataReader.ResourceType)Add("Animation record","animation");
        if(row.Type is 2229 or 2230)Add("Buffer","buffer");
        menu.PlacementTarget=sender as UIElement;if(sender is FrameworkElement element)element.ContextMenu=menu;menu.IsOpen=true;
    }

    private void ApplyState(BrowserState state, bool rebuildRows)
    {
        var previous = State;
        var sourceChanged = state.Snapshot is { } nextSnapshot &&
            (previous.Snapshot is not { } previousSnapshot ||
             !string.Equals(previous.InputPath, state.InputPath, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(previousSnapshot.IndexContentId, nextSnapshot.IndexContentId, StringComparison.Ordinal) ||
             !string.Equals(previousSnapshot.PhysicalSourceStamp, nextSnapshot.PhysicalSourceStamp, StringComparison.Ordinal));
        State = state;
        localError = null;
        if (sourceChanged)
        {
            syncingFilters = true;
            filterTimer.Stop();
            IdFilterTextBox.Text = state.Query.Id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            SearchTextBox.Text = state.Query.Search ?? string.Empty;
            syncingFilters = false;
        }
        var rowsDiffer = rebuildRows || Rows.Count != state.Rows.Count ||
            !Rows.Select(row => row.Resource).SequenceEqual(state.Rows);
        if (rowsDiffer)
        {
            syncingRows = true;
            ResourceGrid.UnselectAll();
            if (Rows.Count == state.Rows.Count)
            {
                for (var i = 0; i < Rows.Count; i++)
                {
                    var record = state.Rows[i];
                    Rows[i].UpdateResource(record, state.Selected.Contains(record.Ordinal));
                }
            }
            else
            {
                var common = Math.Min(Rows.Count, state.Rows.Count);
                var replacement = new ResourceRowViewModel[state.Rows.Count];
                for (var i = 0; i < common; i++)
                {
                    var record = state.Rows[i];
                    var row = Rows[i];
                    row.UpdateResource(record, state.Selected.Contains(record.Ordinal), notify: false);
                    replacement[i] = row;
                }
                for (var i = common; i < state.Rows.Count; i++)
                {
                    var record = state.Rows[i];
                    replacement[i] = new ResourceRowViewModel(record, state.Selected.Contains(record.Ordinal),
                        (ordinal, selected) => controller.SetSelected(ordinal, selected));
                }
                Rows.ReplaceAll(replacement);
            }
            SynchronizeGridSelection(state.Selected);
            syncingRows = false;
        }
        else
        {
            foreach (var row in Rows) row.UpdateSelected(state.Selected.Contains(row.Ordinal));
            SynchronizeGridSelection(state.Selected);
        }
        if (!ReferenceEquals(detailRecord, state.Focused) || !ReferenceEquals(detailSnapshot, state.Snapshot))
        {
            detailRecord = state.Focused;
            detailSnapshot = state.Snapshot;
            OnPropertyChanged(nameof(Details));
        }
        if (PageSizeComboBox is not null && (PageSizeComboBox.SelectedItem is not int currentSize || currentSize != state.Query.Limit))
            PageSizeComboBox.SelectedItem = state.Query.Limit;
        if (GoToPageTextBox is not null)
        {
            var page = state.Total == 0 ? 1 : state.Query.Offset / Math.Max(1, state.Query.Limit) + 1;
            var pageText = page.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(GoToPageTextBox.Text, pageText, StringComparison.Ordinal)) GoToPageTextBox.Text = pageText;
        }
        if (!ReferenceEquals(previous.Navigation, state.Navigation)) OnPropertyChanged(nameof(Navigation));
        if (!string.Equals(previous.Snapshot?.IndexPath, state.Snapshot?.IndexPath, StringComparison.OrdinalIgnoreCase))
            OnPropertyChanged(nameof(SourceIndexPath));
        if (previous.IsOpening != state.IsOpening) OnPropertyChanged(nameof(IsOpening));
        if (previous.IsExporting != state.IsExporting) OnPropertyChanged(nameof(IsExporting));
        if (!ReferenceEquals(previous.LastExport, state.LastExport) || !ReferenceEquals(previous.RetainedManifest, state.RetainedManifest))
            OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(ProgressMaximum));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(CanStartOpen));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanExportSelected));
        OnPropertyChanged(nameof(CanPreviousPage));
        OnPropertyChanged(nameof(CanNextPage));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(SelectionText));
    }

    private string GetProgressText()
    {
        if (State.IsOpening && State.ArchiveProgress is { } archive)
            return $"{archive.Phase}: {archive.Completed:N0} / {archive.Total:N0}";
        if (State.IsExporting && State.ExportProgress is { } export)
            return $"Exporting resource {export.Ordinal:N0}: {export.BytesWritten:N0} / {export.Length:N0} bytes · {export.CompletedFiles:N0} / {export.TotalFiles:N0} files";
        if (State.IsExporting) return State.Status;
        return string.Empty;
    }

    private async void OpenIndex_Click(object sender, RoutedEventArgs e) => await ChooseIndexAsync();
    private async void OpenFolder_Click(object sender, RoutedEventArgs e) => await ChooseFolderAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunControllerAsync(() => controller.RefreshAsync());
    private void CancelOpen_Click(object sender, RoutedEventArgs e) => controller.CancelOpen();
    private void CancelExport_Click(object sender, RoutedEventArgs e) => controller.CancelExport();

    private async Task ChooseIndexAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a resource index",
            Filter = "Detroit index (*.idx)|*.idx|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) await RunControllerAsync(() => controller.OpenAsync(dialog.FileName));
    }

    private async Task ChooseFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Choose an archive folder", Multiselect = false };
        if (dialog.ShowDialog(this) == true) await RunControllerAsync(() => controller.OpenAsync(dialog.FolderName));
    }

    private async Task RunControllerAsync(Func<Task> operation)
    {
        localError = null;
        OnPropertyChanged(nameof(StatusText));
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            localError = error.Message;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private void FilterText_Changed(object sender, TextChangedEventArgs e)
    {
        if (syncingFilters || filterTimer is null) return;
        filterTimer.Stop();
        filterTimer.Start();
    }

    private async void FilterTimer_Tick(object? sender, EventArgs e)
    {
        filterTimer.Stop();
        await ApplyFilterAsync(resetOffset: true);
    }

    private async Task ApplyFilterAsync(bool resetOffset, uint? type = null, uint? package = null, bool navigationChanged = false)
    {
        uint? id = null;
        var idText = IdFilterTextBox.Text.Trim();
        if (idText.Length > 0)
        {
            if (!TryParseId(idText, out var parsed))
            {
                localError = "Enter an unsigned decimal ID or hexadecimal ID such as 0x1A2B.";
                OnPropertyChanged(nameof(StatusText));
                return;
            }
            id = parsed;
        }

        var query = State.Query with
        {
            Id = id,
            Type = navigationChanged ? type : State.Query.Type,
            Package = navigationChanged ? package : State.Query.Package,
            Search = SearchTextBox.Text.Trim(),
            Offset = resetOffset ? 0 : State.Query.Offset
        };
        await RunControllerAsync(() => controller.QueryAsync(query));
    }

    private static bool TryParseId(string text, out uint id)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id);
        if (text.Any(character => character is >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            return uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id);
        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    private async void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        filterTimer.Stop();
        IdFilterTextBox.Clear();
        SearchTextBox.Clear();
        filterTimer.Stop();
        ClearNavigationSelection(NavigationTree);
        await RunControllerAsync(() => controller.QueryAsync(new ResourceQuery(Limit: State.Query.Limit)));
    }

    private async void NavigationTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is NavigationNode node) await ApplyFilterAsync(resetOffset: true, type: node.Type, package: node.Package, navigationChanged: true);
    }

    private static void ClearNavigationSelection(ItemsControl parent)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) continue;
            container.IsSelected = false;
            ClearNavigationSelection(container);
        }
    }

    private void ResourceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingRows) return;
        var removed = e.RemovedItems.OfType<ResourceRowViewModel>().Select(row => row.Ordinal).ToArray();
        var added = e.AddedItems.OfType<ResourceRowViewModel>().Select(row => row.Ordinal).ToArray();
        if (removed.Length == 0 && added.Length == 0) return;
        handlingGridSelection = true;
        try
        {
            controller.UpdateSelection(added, removed);
            if (e.AddedItems.OfType<ResourceRowViewModel>().LastOrDefault() is { } focused)
                controller.Focus(focused.Resource);
        }
        finally
        {
            handlingGridSelection = false;
            if (deferredControllerState is { } state)
            {
                deferredControllerState = null;
                ApplyState(state, rebuildRows: false);
            }
        }
    }

    private void SynchronizeGridSelection(IReadOnlySet<long> selected)
    {
        var desired = Rows.Where(row => selected.Contains(row.Ordinal)).ToHashSet();
        var current = ResourceGrid.SelectedItems.OfType<ResourceRowViewModel>().ToArray();
        if (current.Length == desired.Count && current.All(desired.Contains)) return;
        var wasSyncing = syncingRows;
        syncingRows = true;
        if (desired.Count == 0)
        {
            ResourceGrid.UnselectAll();
        }
        else if (desired.Count == Rows.Count)
        {
            ResourceGrid.SelectAll();
        }
        else if (desired.Count > Rows.Count / 2)
        {
            ResourceGrid.SelectAll();
            foreach (var row in Rows)
                if (!desired.Contains(row)) ResourceGrid.SelectedItems.Remove(row);
        }
        else
        {
            ResourceGrid.UnselectAll();
            foreach (var row in desired) ResourceGrid.SelectedItems.Add(row);
        }
        syncingRows = wasSyncing;
    }

    private void ResourceList_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (syncingRows || FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is not { Content: ResourceRowViewModel row }) return;
        controller.Focus(row.Resource);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = LogicalTreeHelper.GetParent(current) ??
                (current is Visual ? VisualTreeHelper.GetParent(current) : null);
        }
        return null;
    }

    private void ResourceGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            controller.SelectPage();
            e.Handled = true;
        }
    }

    private void SelectPage_Click(object sender, RoutedEventArgs e) => controller.SelectPage();
    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        controller.ClearSelection();
        ResourceGrid.UnselectAll();
    }

    private async void PageSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || PageSizeComboBox.SelectedItem is not int size || size == State.Query.Limit) return;
        await RunControllerAsync(() => controller.QueryAsync(State.Query with { Limit = size, Offset = 0 }));
    }

    private async void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        var offset = Math.Max(0, State.Query.Offset - State.Query.Limit);
        await RunControllerAsync(() => controller.QueryAsync(State.Query with { Offset = offset }));
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (!CanNextPage) return;
        await RunControllerAsync(() => controller.QueryAsync(State.Query with { Offset = State.Query.Offset + State.Query.Limit }));
    }

    private async void GoToPage_Click(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(GoToPageTextBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var page) || page < 1)
        {
            localError = "Enter a page number greater than zero.";
            OnPropertyChanged(nameof(StatusText));
            return;
        }
        var maximumPage = Math.Max(1, (State.Total + State.Query.Limit - 1) / State.Query.Limit);
        page = Math.Min(page, maximumPage);
        var offset = checked((page - 1) * State.Query.Limit);
        await RunControllerAsync(() => controller.QueryAsync(State.Query with { Offset = offset }));
    }

    private async void ExportSelected_Click(object sender, RoutedEventArgs e) => await BeginExportAsync(ExportScope.Selected);
    private async void ExportMatching_Click(object sender, RoutedEventArgs e) => await BeginExportAsync(ExportScope.Matching);

    private void ViewReport_Click(object sender, RoutedEventArgs e)
    {
        var manifest = State.LastExport?.Manifest ?? State.RetainedManifest;
        if (manifest is null) return;
        _ = new ExportFailureWindow(manifest, State.LastExport?.ManifestPath, acknowledgementRequired: false,
            sourceRoots: ReportSourceRoots()) { Owner = this }.ShowDialog();
    }

    private IReadOnlyList<string> ReportSourceRoots() =>
        new[] { State.Snapshot?.SourceRoot, State.ReportSourceRoot }
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => root!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private async Task BeginExportAsync(ExportScope scope)
    {
        ExportReview? review = null;
        await RunControllerAsync(async () => review = await controller.PrepareExportAsync(scope));
        if (review is null) return;
        var dialog = new ExportReviewWindow(review) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Options is { } options)
            await RunControllerAsync(() => controller.ExportAsync(review, options));
    }

    private void OpenIndex_Executed(object sender, ExecutedRoutedEventArgs e) => _ = ChooseIndexAsync();
    private void Refresh_Executed(object sender, ExecutedRoutedEventArgs e) => _ = RunControllerAsync(() => controller.RefreshAsync());

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (State.IsExporting) controller.CancelExport();
            if (State.IsOpening) controller.CancelOpen();
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closeApproved) return;
        e.Cancel = true;
        if (closePending) return;
        closePending = true;
        try
        {
            if (await controller.CloseAsync())
            {
                closeApproved = true;
                _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);
                return;
            }

            var report = State.LastExport?.Manifest ?? State.RetainedManifest;
            if (report is not null)
            {
                var failureWindow = new ExportFailureWindow(report, State.LastExport?.ManifestPath, acknowledgementRequired: true,
                    sourceRoots: ReportSourceRoots()) { Owner = this };
                if (failureWindow.ShowDialog() == true && await controller.CloseAsync(acknowledgeManifestFailure: true))
                {
                    closeApproved = true;
                    _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);
                }
            }
            else
            {
                localError = "The current operation has not finished closing. Try again when it completes.";
                OnPropertyChanged(nameof(StatusText));
            }
        }
        catch (Exception error)
        {
            localError = error.Message;
            OnPropertyChanged(nameof(StatusText));
        }
        finally { closePending = false; }
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        controller.StateChanged -= Controller_StateChanged;
        filterTimer.Stop();
        ExplorerProcessing.Dispose();
        await controller.DisposeAsync();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public sealed class ResourceDetails(ArchiveSnapshot? snapshot, ResourceRecord? resource)
    {
        public string Title => resource is null ? "Select a resource" : $"Resource {resource.Ordinal:N0}";
        public string Ordinal => resource?.Ordinal.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
        public string Id => resource is null ? "—" : $"{resource.Id.ToString(CultureInfo.InvariantCulture)} / 0x{resource.Id:X8}";
        public string Type => resource is null ? "—" : $"{resource.Type.ToString(CultureInfo.InvariantCulture)} / 0x{resource.Type:X8}";
        public string Flags => resource is null ? "—" : $"{resource.Flags.ToString(CultureInfo.InvariantCulture)} / 0x{resource.Flags:X8}";
        public string PackageVolume => resource is null ? "—" : $"{resource.Package} · {resource.VolumeName}";
        public string Offset => resource is null ? "—" : $"0x{resource.Offset:X} ({resource.Offset.ToString("N0", CultureInfo.InvariantCulture)})";
        public string StoredSize => resource?.StoredSize.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
        public string AlternativeSize => resource is null ? "—" : $"{resource.AlternativeSize.ToString(CultureInfo.InvariantCulture)} / 0x{resource.AlternativeSize:X8}";
        public string Validity => resource?.Validity.ToString() ?? "—";
        public string Diagnostic => resource is null || resource.Validity == ResourceValidity.Valid ? string.Empty :
            resource.Diagnostic ?? resource.Validity.ToString();
        public string IndexName => snapshot is null ? "—" : Path.GetFileName(snapshot.IndexPath);
        public string IndexContentId => snapshot?.IndexContentId ?? "—";
        public string PhysicalSourceStamp => snapshot?.PhysicalSourceStamp ?? "—";
    }
}

