using Deviant.Core;
using Deviant.Browser;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class WindowBehaviorTests
{
    [Fact]
    public void ResourceRowShowsDecimalAndHexadecimalFields()
    {
        var resource = new ResourceRecord(17, 1022, 0x80000000, 0x1A2B, 2, 4096, 8192, 31,
            "package-02.bin", ResourceValidity.Valid);

        var row = new ResourceRowViewModel(resource, isSelected: false);

        Assert.Equal("6699 / 0x00001A2B", row.Id);
        Assert.Equal("1022 / 0x000003FE", row.Type);
        Assert.Equal("0x80000000", row.Flags);
        Assert.Equal("0x1000 (4,096)", row.Offset);
        Assert.Equal("31 / 0x0000001F", row.AlternativeSize);
    }

    [Fact]
    public void SelectionUpdatesControllerAndStateRefreshDoesNotEcho()
    {
        var changes = new List<(long Ordinal, bool Selected)>();
        var row = new ResourceRowViewModel(new ResourceRecord(8, 7, 0, 9, 1, 0, 4, 0,
            "package-01.bin"), false, (ordinal, selected) => changes.Add((ordinal, selected)));

        row.IsSelected = true;
        row.UpdateSelected(false);

        Assert.Equal(new[] { (8L, true) }, changes);
        Assert.False(row.IsSelected);
    }

    [Fact]
    public void ReplacingAPagePublishesOneResetInsteadOfPerRowNotifications()
    {
        var rows = new ResourceRowCollection();
        var changes = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, args) => changes.Add(args.Action);
        var page = Enumerable.Range(0, 500).Select(ordinal => new ResourceRowViewModel(
            new ResourceRecord(ordinal, 3, 0, (uint)ordinal, 0, ordinal * 4, 4, 0, "package.bin"), false));

        rows.ReplaceAll(page);

        Assert.Equal(500, rows.Count);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, changes);
    }

    [Fact]
    public void ClosingAnEmptyWindowCompletesAfterTheClosingEventReturns()
    {
        RunSta(() =>
        {
            var window = new MainWindow(new EmptyController())
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                Opacity = 0,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            var frame = new DispatcherFrame();
            var closed = false;
            window.Closed += (_, _) => { closed = true; frame.Continue = false; };

            window.Show();
            window.Close();
            Dispatcher.PushFrame(frame);

            Assert.True(closed);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("The WPF STA check failed.", error);
    }

    private sealed class EmptyController : IBrowserController
    {
        public BrowserState State { get; } = new();
        public event EventHandler? StateChanged;
        public Task OpenAsync(string input, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task QueryAsync(ResourceQuery query, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void SetSelected(long ordinal, bool selected) { }
        public void SelectPage() { }
        public void ClearSelection() { }
        public void Focus(ResourceRecord? record) { }
        public void CancelOpen() { }
        public void CancelExport() { }
        public Task<ExportReview?> PrepareExportAsync(ExportScope scope, CancellationToken cancellationToken = default) => Task.FromResult<ExportReview?>(null);
        public Task ExportAsync(ExportReview review, ExportOptions options, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> CloseAsync(bool acknowledgeManifestFailure = false) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void RaiseChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
