using System.Windows;
using System.Windows.Controls;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private readonly StackPanel commonGroupHeader = new() { Visibility = Visibility.Collapsed, Margin = new(16, 8, 16, 0) };
    private readonly ComboBox commonGroupFrames = new() { DisplayMemberPath = nameof(CommonGroupChoice.Label), MinWidth = 180, MaxWidth = 480, Margin = new(0, 0, 8, 0) };
    private readonly ComboBox commonGroupItems = new() { DisplayMemberPath = nameof(CommonGroupItemChoice.Label), MinWidth = 180, MaxWidth = 480 };
    private readonly TextBox commonGroupText = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 8, 0, 0) };
    private readonly TextBlock commonGroupStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) };
    private NativeContainerLease? commonGroupContainer;
    private GmkInspectionLease? commonGroupLease;
    private AssetMemoryBudget.Reservation? commonGroupFrameReservation, commonGroupItemReservation;
    private Task commonGroupNavigationDrain = Task.CompletedTask;
    private long commonGroupRequestVersion;
    private bool commonGroupMode, updatingCommonGroups;
    internal Func<string, CancellationToken, Task>? CommonGroupNavigationGateAsync { get; set; }
    public ResourceIdentity? CommonGroupSelectedIdentity => commonGroupLease?.MemberIdentity;

    private void InitializeCommonGroups()
    {
        foreach (var picker in new[] { commonGroupFrames, commonGroupItems })
        {
            picker.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            picker.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
            picker.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        }
        var host = new DockPanel();
        var choices = new WrapPanel();
        choices.Children.Add(commonGroupFrames); choices.Children.Add(commonGroupItems);
        bufferView.CancellationRequested += (_, _) => bufferReadCancellation?.Cancel();
        commonGroupHeader.Children.Add(choices); commonGroupHeader.Children.Add(commonGroupText); commonGroupHeader.Children.Add(commonGroupStatus);
        DockPanel.SetDock(commonGroupHeader, Dock.Top); host.Children.Add(commonGroupHeader); host.Children.Add(bufferView); BufferHost.Content = host;
        commonGroupFrames.SelectionChanged += async (_, _) =>
        {
            if (updatingCommonGroups || commonGroupFrames.SelectedItem is not CommonGroupChoice choice) return;
            try { await SelectCommonGroupFrameAsync(choice.Identity); } catch (OperationCanceledException) { } catch (Exception error) { commonGroupStatus.Text = error.Message; }
        };
        commonGroupItems.SelectionChanged += async (_, _) =>
        {
            if (updatingCommonGroups || commonGroupItems.SelectedItem is not CommonGroupItemChoice choice) return;
            try { await SelectCommonGroupItemAsync(choice.Index); } catch (OperationCanceledException) { } catch (Exception error) { commonGroupStatus.Text = error.Message; }
        };
    }

    public async Task InspectCommonGroupsAsync(ResourceIdentity identity, CancellationToken token = default)
    {
        EnsureBufferRequestAllowed();
        var navigation = BeginNavigation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, navigation.Token);
        await InspectCommonGroupsCoreAsync(identity, navigation.Generation, linked.Token);
    }

    private async Task InspectCommonGroupsCoreAsync(ResourceIdentity identity, long navigation, CancellationToken token)
    {
        await commonGroupNavigationDrain.WaitAsync(token);
        if (navigation != navigationGeneration || closingInProgress) return;
        EnsureBufferRequestAllowed();
        var owner = session ?? throw new InvalidOperationException("Open an archive first.");
        var row = owner.Snapshot.Records.SingleOrDefault(item => item.Ordinal == identity.Ordinal);
        if (identity.IndexContentId != owner.Snapshot.IndexContentId || row is null || row.Type != 29)
            throw new InvalidOperationException("Choose a container from this archive.");
        if (row.StoredSize > 16 * 1024 * 1024) throw new InvalidOperationException("This container exceeds the inspection size limit.");
        await ClearBufferAsync(); token.ThrowIfCancellationRequested();
        if (navigation != navigationGeneration || !ReferenceEquals(owner, session)) return;
        commonGroupMode = true; var request = ++commonGroupRequestVersion;
        ShowBrowse("buffer"); commonGroupHeader.Visibility = Visibility.Visible; commonGroupStatus.Text = "Reading groups…";
        await Task.WhenAll(sceneNavigationDrain, textureNavigationDrain).WaitAsync(token);
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, navigationCancellation.Token);
        bufferReadCancellation = own; renderMemoryBudget ??= new();
        var read = Task.Run(async () =>
        {
            if (CommonGroupNavigationGateAsync is { } gate) await gate("container", own.Token);
            own.Token.ThrowIfCancellationRequested();
            return await NativeContainerLease.OpenAsync(owner.Snapshot, new(identity.IndexContentId, identity.Ordinal), renderMemoryBudget, own.Token);
        }, own.Token);
        bufferReadTask = read; bufferReads.Add(read);
        NativeContainerLease? loaded = null;
        AssetMemoryBudget.Reservation? choicesReservation = null;
        try
        {
            loaded = await read; own.Token.ThrowIfCancellationRequested();
            if (!CommonGroupsCurrent(owner, request)) return;
            var count = loaded.Directory.Objects.Count(frame => frame.NativeType == 4241);
            choicesReservation = renderMemoryBudget.Reserve(checked(count * 256L), "Common-group choices");
            var choices = loaded.Directory.Objects.Where(frame => frame.NativeType == 4241)
                .Select(frame => new CommonGroupChoice(frame.Identity, $"Group {frame.DirectoryIndex + 1}")).ToArray();
            var selected = identity.MemberPath is null ? choices.Length == 1 ? choices[0] : null : choices.SingleOrDefault(choice => choice.Identity == identity);
            if (identity.MemberPath is not null && selected is null) throw new InvalidOperationException("The selected group is unavailable.");
            commonGroupContainer = loaded; loaded = null;
            commonGroupFrameReservation = choicesReservation; choicesReservation = null;
            updatingCommonGroups = true;
            try { commonGroupFrames.ItemsSource = choices; commonGroupFrames.SelectedIndex = -1; }
            finally { updatingCommonGroups = false; }
            commonGroupStatus.Text = choices.Length == 0 ? "This container has no common groups." : $"{choices.Length:N0} groups · Choose a group";
            if (selected is not null)
            {
                if (ReferenceEquals(bufferReadCancellation, own)) bufferReadCancellation = null;
                await SelectCommonGroupFrameAsync(selected.Identity, own.Token);
            }
        }
        catch (OperationCanceledException) { if (CommonGroupsCurrent(owner, request)) commonGroupStatus.Text = "Cancelled"; throw; }
        catch (Exception error) { if (CommonGroupsCurrent(owner, request)) commonGroupStatus.Text = error.Message; throw; }
        finally
        {
            loaded?.Dispose(); choicesReservation?.Dispose(); bufferReads.Remove(read);
            if (ReferenceEquals(bufferReadTask, read)) bufferReadTask = null;
            if (ReferenceEquals(bufferReadCancellation, own)) bufferReadCancellation = null;
        }
    }

    public Task SelectCommonGroupFrameAsync(ResourceIdentity identity, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); EnsureBufferRequestAllowed();
        var choice = commonGroupFrames.Items.Cast<CommonGroupChoice>().SingleOrDefault(item => item.Identity == identity)
            ?? throw new InvalidOperationException("Choose an available group.");
        updatingCommonGroups = true;
        try { commonGroupFrames.SelectedItem = choice; }
        finally { updatingCommonGroups = false; }
        return LoadCommonGroupItemAsync(identity, null, token);
    }

    public Task SelectCommonGroupItemAsync(int index, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); EnsureBufferRequestAllowed();
        if (commonGroupLease?.Document.Value is not { } document || index < 0 || index >= document.Items.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "Choose an available item.");
        var member = commonGroupLease?.MemberIdentity ?? throw new InvalidOperationException("Choose a group first.");
        return LoadCommonGroupItemAsync(member, index, token);
    }

    private async Task LoadCommonGroupItemAsync(ResourceIdentity member, int? itemIndex, CancellationToken token)
    {
        EnsureBufferRequestAllowed();
        var owner = session ?? throw new InvalidOperationException("Open an archive first.");
        var container = commonGroupContainer ?? throw new InvalidOperationException("Choose a container first.");
        if (!commonGroupMode || currentView != "buffer") throw new InvalidOperationException("Open common groups first.");
        var request = ++commonGroupRequestVersion; bufferReadCancellation?.Cancel();
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, navigationCancellation.Token); bufferReadCancellation = own;
        await bufferView.ClearAsync();
        commonGroupLease?.Dispose(); commonGroupLease = null;
        commonGroupItemReservation?.Dispose(); commonGroupItemReservation = null;
        updatingCommonGroups = true;
        try { commonGroupItems.ItemsSource = null; }
        finally { updatingCommonGroups = false; }
        commonGroupText.Clear(); commonGroupStatus.Text = "Reading item…";
        var read = Task.Run(async () =>
        {
            if (CommonGroupNavigationGateAsync is { } gate) await gate("member", own.Token);
            own.Token.ThrowIfCancellationRequested();
            return GmkInspectionLease.FromContainer(container, member, own.Token, itemIndex);
        }, own.Token);
        bufferReadTask = read; bufferReads.Add(read);
        GmkInspectionLease? loaded = null;
        GmkInspectionLease? published = null;
        AssetMemoryBudget.Reservation? publishedChoices = null;
        var publicationFailed = false;
        try
        {
            loaded = await read; own.Token.ThrowIfCancellationRequested();
            if (!CommonGroupsCurrent(owner, request) || !ReferenceEquals(container, commonGroupContainer)) return;
            loaded.EnsureUnchanged();
            if (loaded.Document.Value is { } document)
            {
                publishedChoices = container.MemoryBudget.Reserve(checked(document.Items.Count * 512L), "Common-group item choices");
                var choices = document.Items.Select((item, index) =>
                {
                    var name = item.StoredName.IsValidUtf8 ? item.StoredName.DisplayText : "";
                    if (name.Length > 96) name = name[..96] + "…";
                    return new CommonGroupItemChoice(index, string.IsNullOrWhiteSpace(name) ? $"Item {index + 1}" : $"{name} · {index + 1}");
                }).ToArray();
                commonGroupItemReservation = publishedChoices;
                updatingCommonGroups = true;
                try { commonGroupItems.ItemsSource = choices; commonGroupItems.SelectedIndex = loaded.SelectedItemIndex ?? -1; }
                finally { updatingCommonGroups = false; }
                if (loaded.SelectedItemIndex is { } selected)
                {
                    var text = document.Items[selected].StoredText;
                    commonGroupText.Text = text.IsValidUtf8 ? text.DisplayText : "";
                    commonGroupStatus.Text = text.IsValidUtf8 ? $"Item {selected + 1:N0} of {choices.Length:N0}" : "Text encoding unavailable";
                }
                else commonGroupStatus.Text = "This group has no items.";
            }
            if (loaded.Document.Value is null) { commonGroupStatus.Text = "This group could not be read."; publicationFailed = true; }
            commonGroupLease = published = loaded; loaded = null;
            await bufferView.LoadAsync(owner.Snapshot, published.Inspection, "Common group", own.Token);
            own.Token.ThrowIfCancellationRequested();
            if (!CommonGroupsCurrent(owner, request)) return;
            published.EnsureUnchanged();
            SelectionForLibraryChanged();
        }
        catch (OperationCanceledException) { publicationFailed = true; if (CommonGroupsCurrent(owner, request)) commonGroupStatus.Text = "Cancelled"; throw; }
        catch (Exception error) { publicationFailed = true; if (CommonGroupsCurrent(owner, request)) commonGroupStatus.Text = error.Message; throw; }
        finally
        {
            loaded?.Dispose(); bufferReads.Remove(read);
            if (published is not null && (publicationFailed || !CommonGroupsCurrent(owner, request)) && ReferenceEquals(commonGroupLease, published))
            {
                if (ReferenceEquals(bufferView.CurrentInspection, published.Inspection)) await bufferView.ClearAsync();
                published.Dispose(); publishedChoices?.Dispose();
                if (ReferenceEquals(commonGroupLease, published)) commonGroupLease = null;
                if (ReferenceEquals(commonGroupItemReservation, publishedChoices)) commonGroupItemReservation = null;
            }
            else if (published is null) publishedChoices?.Dispose();
            if (ReferenceEquals(bufferReadTask, read)) bufferReadTask = null;
            if (ReferenceEquals(bufferReadCancellation, own)) bufferReadCancellation = null;
        }
    }

    private bool CommonGroupsCurrent(AudioExplorerSession owner, long request) =>
        commonGroupMode && currentView == "buffer" && !closingInProgress && bufferDrainDepth == 0 && workspaceTransitionDepth == 0 &&
        request == commonGroupRequestVersion && ReferenceEquals(owner, session);

    private void PauseCommonGroupsForNavigation()
    {
        if (!commonGroupMode) return;
        commonGroupMode = false; ++commonGroupRequestVersion; bufferReadCancellation?.Cancel();
        commonGroupNavigationDrain = DrainCommonGroupsAsync(commonGroupNavigationDrain);
        TrackLibraryTask(commonGroupNavigationDrain);
    }
    private async Task DrainCommonGroupsAsync(Task preceding)
    { try { await preceding; } catch { } await ClearBufferAsync(); }
    private void ReleaseCommonGroups()
    {
        commonGroupMode = false; ++commonGroupRequestVersion;
        commonGroupLease?.Dispose(); commonGroupLease = null;
        commonGroupContainer?.Dispose(); commonGroupContainer = null;
        commonGroupItemReservation?.Dispose(); commonGroupItemReservation = null;
        commonGroupFrameReservation?.Dispose(); commonGroupFrameReservation = null;
        updatingCommonGroups = true;
        try { commonGroupFrames.ItemsSource = commonGroupItems.ItemsSource = null; }
        finally { updatingCommonGroups = false; }
        commonGroupText.Clear(); commonGroupStatus.Text = ""; commonGroupHeader.Visibility = Visibility.Collapsed;
    }
    private sealed record CommonGroupChoice(ResourceIdentity Identity, string Label);
    private sealed record CommonGroupItemChoice(int Index, string Label);
}





