using System.Windows;
using System.Windows.Controls;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Formats.Scenes;

namespace Deviant.Desktop;

public partial class AudioWindow
{
    private readonly PhysicsInspectionView physicsView = new();
    private readonly ComboBox physicsFrames = new() { DisplayMemberPath = nameof(PhysicsFrameChoice.Label), MinWidth = 220, MaxWidth = 540, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock physicsStatus = new() { Margin = new(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap };
    private readonly HashSet<Task<NativeContainerLease>> physicsContainerReads = [];
    private readonly HashSet<Task<PhysicsInspectionLease>> physicsMemberReads = [];
    private NativeContainerLease? physicsContainer;
    private PhysicsInspectionLease? physicsLease;
    private CancellationTokenSource? physicsCancellation;
    private CancellationTokenSource? physicsMemberCancellation;
    private long physicsGeneration;
    private long physicsRequestVersion;
    private int physicsDrainDepth;
    private int physicsWorkspaceDrainDepth;
    private bool updatingPhysics;
    private bool physicsBusy;
    internal Func<string, CancellationToken, Task>? PhysicsNavigationGateAsync { get; set; }
    public PhysicsInspectionView PhysicsBrowser => physicsView;
    public ResourceIdentity? PhysicsSelectedIdentity => physicsLease?.MemberIdentity;

    private void InitializePhysics()
    {
        var host = new DockPanel { Margin = new(8) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Physics", Margin = new(0, 0, 0, 8) });
        var actions = new WrapPanel(); actions.Children.Add(physicsFrames);
        var cancel = new Button { Content = "Cancel", Margin = new(8, 0, 0, 0) };
        cancel.Click += (_, _) => CancelPhysics(); actions.Children.Add(cancel);
        header.Children.Add(actions); header.Children.Add(physicsStatus);
        DockPanel.SetDock(header, Dock.Top); host.Children.Add(header); host.Children.Add(physicsView);
        PhysicsHost.Content = host;
        physicsFrames.SelectionChanged += async (_, _) =>
        {
            if (updatingPhysics || physicsFrames.SelectedItem is not PhysicsFrameChoice choice) return;
            try { await SelectPhysicsFrameAsync(choice.Identity, navigationCancellation.Token); }
            catch (OperationCanceledException) { }
            catch (Exception error) { SetPhysicsStatus(error.Message); }
        };
    }

    public async Task InspectPhysicsAsync(ResourceIdentity identity, CancellationToken token = default)
    {
        if (closingInProgress || physicsWorkspaceDrainDepth != 0 || workspaceTransitionDepth != 0) throw new InvalidOperationException("The workspace is changing.");
        var owner = session ?? throw new InvalidOperationException("Open an archive first.");
        var request = ++physicsRequestVersion;
        await ClearPhysicsAsync(workspaceDrain: false);
        token.ThrowIfCancellationRequested();
        if (request != physicsRequestVersion || !ReferenceEquals(owner, session) || closingInProgress) return;
        ShowBrowse("physics");
        await sceneNavigationDrain.WaitAsync(token);
        if (request != physicsRequestVersion || !ReferenceEquals(owner, session) || closingInProgress || currentView != "physics") return;
        var generation = ++physicsGeneration;
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, navigationCancellation.Token);
        physicsCancellation = own;
        SetPhysicsStatus("Reading physics records…", busy: true);
        renderMemoryBudget ??= new();
        var budget = renderMemoryBudget;
        var read = Task.Run(async () =>
        {
            if (PhysicsNavigationGateAsync is {} gate) await gate("container", own.Token);
            own.Token.ThrowIfCancellationRequested();
            return await NativeContainerLease.OpenAsync(owner.Snapshot,
                new(identity.IndexContentId, identity.Ordinal), budget, own.Token);
        }, own.Token);
        physicsContainerReads.Add(read);
        NativeContainerLease? loaded = null;
        try
        {
            loaded = await read; own.Token.ThrowIfCancellationRequested();
            if (!PhysicsCurrent(generation) || !ReferenceEquals(owner, session)) return;
            var choices = loaded.Directory.Objects.Where(frame => frame.NativeType == PhysicsEnvelopeReader.NativeType)
                .Select(frame => new PhysicsFrameChoice(frame.Identity, $"Physics record {frame.DirectoryIndex + 1}" )).ToArray();
            physicsContainer = loaded; loaded = null;
            updatingPhysics = true;
            try { physicsFrames.ItemsSource = choices; physicsFrames.SelectedIndex = -1; }
            finally { updatingPhysics = false; }
            SetPhysicsStatus(choices.Length == 0 ? "This container has no physics records." : $"{choices.Length:N0} records · Choose a record");
            if (identity.MemberPath is not null)
            {
                var matches = choices.Where(choice => choice.Identity == identity).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("The physics record is missing or ambiguous.");
                updatingPhysics = true;
                try { physicsFrames.SelectedItem = matches[0]; }
                finally { updatingPhysics = false; }
                await SelectPhysicsFrameAsync(identity, own.Token);
            }
        }
        catch (OperationCanceledException) { if (PhysicsCurrent(generation)) { SuspendPhysics(); SetPhysicsStatus("Cancelled"); } throw; }
        catch (Exception error) { if (PhysicsCurrent(generation)) { SuspendPhysics(); SetPhysicsStatus(error.Message); } throw; }
        finally
        {
            loaded?.Dispose(); physicsContainerReads.Remove(read);
            if (ReferenceEquals(physicsCancellation, own)) physicsCancellation = null;
        }
    }

    private bool PhysicsCurrent(long generation) => !closingInProgress && physicsDrainDepth == 0 && generation == physicsGeneration && currentView == "physics";

    private async Task SelectPhysicsFrameAsync(ResourceIdentity identity, CancellationToken token)
    {
        var container = physicsContainer ?? throw new InvalidOperationException("Choose a container first.");
        physicsMemberCancellation?.Cancel();
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, navigationCancellation.Token);
        physicsMemberCancellation = own;
        physicsView.Clear(); physicsLease?.Dispose(); physicsLease = null;
        var selected = ++physicsGeneration;
        SetPhysicsStatus("Reading metadata…", busy: true);
        var read = Task.Run(async () =>
        {
            if (PhysicsNavigationGateAsync is {} gate) await gate("member", own.Token);
            own.Token.ThrowIfCancellationRequested();
            return PhysicsInspectionLease.FromContainer(container, identity, own.Token);
        }, own.Token);
        physicsMemberReads.Add(read);
        PhysicsInspectionLease? loaded = null;
        try
        {
            loaded = await read; own.Token.ThrowIfCancellationRequested();
            if (!PhysicsCurrent(selected) || !ReferenceEquals(container, physicsContainer)) return;
            loaded.EnsureUnchanged(); physicsView.SetResult(loaded.Result);
            physicsLease = loaded; loaded = null;
            SetPhysicsStatus("Ready"); SelectionForLibraryChanged();
        }
        catch (OperationCanceledException)
        {
            if (PhysicsCurrent(selected)) SetPhysicsStatus("Cancelled");
            throw;
        }
        catch when (!PhysicsCurrent(selected)) { }
        catch (Exception error)
        {
            physicsView.Clear();
            SetPhysicsStatus(error.Message);
            throw;
        }
        finally { loaded?.Dispose(); physicsMemberReads.Remove(read); if (ReferenceEquals(physicsMemberCancellation, own)) physicsMemberCancellation = null; }
    }

    private void SuspendPhysics(bool invalidateRequests = true)
    {
        physicsBusy = false;
        if (invalidateRequests) ++physicsRequestVersion;
        ++physicsGeneration; physicsCancellation?.Cancel(); physicsMemberCancellation?.Cancel();
        physicsView.Clear();
        updatingPhysics = true;
        try { physicsFrames.ItemsSource = null; }
        finally { updatingPhysics = false; }
        physicsLease?.Dispose(); physicsLease = null;
        physicsContainer?.Dispose(); physicsContainer = null;
    }

    private async Task ClearPhysicsAsync(bool workspaceDrain = true)
    {
        if (workspaceDrain) ++physicsWorkspaceDrainDepth;
        ++physicsDrainDepth;
        try
        {
            SuspendPhysics(invalidateRequests: workspaceDrain);
            foreach (var task in physicsMemberReads.ToArray()) { try { (await task).Dispose(); } catch { } }
            foreach (var task in physicsContainerReads.ToArray()) { try { (await task).Dispose(); } catch { } }
        }
        finally { --physicsDrainDepth; if (workspaceDrain) --physicsWorkspaceDrainDepth; }
    }

    private sealed record PhysicsFrameChoice(ResourceIdentity Identity, string Label);

    private void CancelPhysics()
    {
        physicsCancellation?.Cancel(); physicsMemberCancellation?.Cancel();
        if (physicsBusy) SetPhysicsStatus("Cancelling…", busy: true);
    }

    private void SetPhysicsStatus(string message, bool busy = false)
    {
        physicsStatus.Text = message; physicsBusy = busy;
        if (currentView == "physics")
        {
            if (!foregroundBusy) StatusText.Text = message;
            RefreshActivity();
        }
    }
}
