using Deviant.Core;

namespace Deviant.Browser;

public interface IBrowserController : IAsyncDisposable
{
    BrowserState State { get; }
    event EventHandler? StateChanged;
    Task OpenAsync(string input, CancellationToken cancellationToken = default);
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task QueryAsync(ResourceQuery query, CancellationToken cancellationToken = default);
    void SetSelected(long ordinal, bool selected);
    void UpdateSelection(IEnumerable<long> added, IEnumerable<long> removed)
    {
        foreach (var ordinal in removed) SetSelected(ordinal, false);
        foreach (var ordinal in added) SetSelected(ordinal, true);
    }
    void SelectPage();
    void ClearSelection();
    void Focus(ResourceRecord? record);
    void CancelOpen();
    void CancelExport();
    Task<ExportReview?> PrepareExportAsync(ExportScope scope, CancellationToken cancellationToken = default);
    Task ExportAsync(ExportReview review, ExportOptions options, CancellationToken cancellationToken = default);
    Task<bool> CloseAsync(bool acknowledgeManifestFailure = false);
}
