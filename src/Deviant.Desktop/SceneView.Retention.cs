using System.Numerics;
using System.Windows.Media.Media3D;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;

namespace Deviant.Desktop;

public partial class SceneView
{
    private const long MaximumRetainedSceneStateBytes = 32L * 1024 * 1024;

    public SceneRenderViewState? CaptureRenderState()
    {
        Dispatcher.VerifyAccess();
        if (renderSnapshot is not { } snapshot || lastSuccessfulSceneRenderRequest is not { } request ||
            section is not { } currentSection || session is not { } currentSession ||
            !ReferenceEquals(request.Section, currentSection) || request.Section.Descriptor.Identity != currentSection.Descriptor.Identity ||
            request.Section.Result.Revision != currentSession.Revision || snapshot.Revision != currentSession.Revision ||
            !ReferenceEquals(snapshot.MemoryBudget, lastSuccessfulRenderBudget))
            return null;

        var structureIdentity = selectedOccurrence is null && selectedNode is not null
            ? selectedEntity ?? selectedNode.Identity : null;
        long estimate = 512;
        checked
        {
            foreach (var row in renderRows.Values)
                estimate += 80L + row.Node.Key.Length * sizeof(char);
            if (selectedOccurrence is { } occurrence) estimate += 24L + occurrence.Key.Length * sizeof(char);
            foreach (var selected in SceneViewport.SelectedMeshes)
                estimate += 64L + (selected.OccurrenceKey.Length + (selected.PrimitiveKey?.Length ?? 0)) * sizeof(char);
            if (structureIdentity is not null)
                estimate += 128L + (structureIdentity.IndexContentId.Length + (structureIdentity.MemberPath?.Length ?? 0)) * sizeof(char);
        }
        if (estimate > MaximumRetainedSceneStateBytes)
            throw new ArchiveException("AssetMemoryLimit", "The scene view state exceeds its 32 MiB retention limit.");

        var reservation = snapshot.MemoryBudget.Reserve(estimate, "retained scene view state");
        try
        {
            var items = renderRows.Values.Select(row => new SceneRenderTreeItemState(
                new string(row.Node.Key.AsSpan()), row.IsVisible, row.IsSelected, row.IsExpanded)).ToArray();
            var selectedKey = selectedOccurrence is { } selected ? new string(selected.Key.AsSpan()) : null;
            var camera = SceneViewport.Viewport.Camera as HelixToolkit.Wpf.SharpDX.PerspectiveCamera
                ?? throw new InvalidOperationException("The scene camera is unavailable.");
            var position = new Vector3((float)camera.Position.X, (float)camera.Position.Y, (float)camera.Position.Z);
            var direction = new Vector3((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z);
            var up = new Vector3((float)camera.UpDirection.X, (float)camera.UpDirection.Y, (float)camera.UpDirection.Z);
            var savedStructure = structureIdentity is null ? null : new ResourceIdentity(
                new string(structureIdentity.IndexContentId.AsSpan()), structureIdentity.Ordinal,
                structureIdentity.MemberPath is { } path ? new string(path.AsSpan()) : null);
            return new SceneRenderViewState(currentSession.Revision, currentSection.Descriptor.Identity,
                request.BranchInstance, request.Region, request.Scope, request.MaximumPreviewDimension,
                new(position, position + direction, up, "Perspective", camera.FieldOfView),
                SceneViewport.ShowHidden, items, selectedKey, snapshot.MemoryBudget, reservation, savedStructure,
                request.AllowLargeData, SceneViewport.SelectedMeshes.Select(item => new RenderMeshSelection(
                    new string(item.OccurrenceKey.AsSpan()), item.PrimitiveKey is { } part ? new string(part.AsSpan()) : null)).ToArray());
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    internal Task<SceneLoadedSection> LoadSceneStructureAsync(ResourceIdentity identity, CancellationToken token = default)
    {
        Dispatcher.VerifyAccess();
        token.ThrowIfCancellationRequested();
        return TrackLoadAsync(identity, token);
    }

    public async Task RestoreRenderStateAsync(SceneRenderViewState state, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        Dispatcher.VerifyAccess();
        if (state.IsDisposed) throw new ObjectDisposedException(nameof(state));
        var currentSection = section ?? throw new InvalidOperationException("Open the saved scene section first.");
        var currentSession = session ?? throw new InvalidOperationException("Open the saved archive first.");
        if (state.Revision != currentSession.Revision || state.SectionIdentity != currentSection.Descriptor.Identity)
            throw new InvalidOperationException("The saved scene state belongs to another source revision or section.");
        if ((lastSuccessfulRenderBudget is { } previousBudget && !ReferenceEquals(state.MemoryBudget, previousBudget)) ||
            (renderSnapshot is { } currentSnapshot && !ReferenceEquals(state.MemoryBudget, currentSnapshot.MemoryBudget)))
            throw new InvalidOperationException("The saved scene state belongs to another render budget.");
        if (state.ReservedBytes is <= 0 or > MaximumRetainedSceneStateBytes)
            throw new InvalidOperationException("The saved scene state has no valid memory reservation.");
        token.ThrowIfCancellationRequested();

        var branch = BranchChoice.Items.OfType<SceneBranchChoice>()
            .FirstOrDefault(item => item.Instance.Identity == state.BranchIdentity)
            ?? throw new InvalidOperationException("The saved scene branch is unavailable.");
        var generation = ++restoreGeneration;
        updatingBranches = true;
        try { BranchChoice.SelectedItem = branch; }
        finally { updatingBranches = false; }
        SetRegion(state.Region);
        var expectedRenderGeneration = renderGeneration + 1;
        await LoadRegionAsync(token, state.Scope, state.MaximumPreviewDimension, state.Region,
            state.AllowLargeData, state.MemoryBudget);
        token.ThrowIfCancellationRequested();
        if (generation != restoreGeneration || renderGeneration != expectedRenderGeneration || !ReferenceEquals(section, currentSection) ||
            session?.Revision != state.Revision || renderSnapshot is not { } restored)
            throw new OperationCanceledException(token);
        if (!ReferenceEquals(restored.MemoryBudget, state.MemoryBudget))
        {
            await ClearRenderingAsync();
            throw new InvalidOperationException("The scene render budget changed while restoring the saved view.");
        }

        ApplyCameraState(state.Camera);
        selectingRenderTree = true;
        try
        {
            if (state.SelectedOccurrenceKey is { } occurrenceKey &&
                !restored.Occurrences.Any(item => item.Key == occurrenceKey))
                throw new InvalidOperationException("The saved scene occurrence is unavailable in this render.");
            if (state.SelectedOccurrenceKey is not null) SelectMeshes(state.SelectedMeshes);
            else
            {
                selectedOccurrence = null;
                SceneViewport.SetSelection(null);
            }

            var savedItems = state.TreeItems.ToDictionary(item => item.Key, StringComparer.Ordinal);
            foreach (var root in RenderHierarchy.Items.OfType<RenderTreeRow>()) RestoreTree(root);
            void RestoreTree(RenderTreeRow row)
            {
                if (savedItems.TryGetValue(row.Node.Key, out var saved))
                {
                    row.IsVisible = saved.IsVisible;
                    row.IsExpanded = saved.IsExpanded;
                    row.IsSelected = saved.IsSelected;
                }
                foreach (var child in row.Children) RestoreTree(child);
            }
        }
        finally { selectingRenderTree = false; }

        SceneViewport.ShowHidden = state.ShowHidden;
        ShowHidden.IsChecked = state.ShowHidden;
        UpdateRenderedCount();
        if (state.SelectedStructureIdentity is { } structureIdentity && !TrySelectIdentity(currentSection, structureIdentity))
            throw new InvalidOperationException("The saved scene selection is unavailable in this section.");
        if (state.AllowLargeData) ApproveLargeData(currentSection);
    }

    private void ApplyCameraState(ModelCameraState state)
    {
        if (SceneViewport.Viewport.Camera is not HelixToolkit.Wpf.SharpDX.PerspectiveCamera camera)
            throw new InvalidOperationException("The scene camera is unavailable.");
        camera.Position = new Point3D(state.Position.X, state.Position.Y, state.Position.Z);
        camera.LookDirection = new Vector3D(state.Target.X - state.Position.X,
            state.Target.Y - state.Position.Y, state.Target.Z - state.Position.Z);
        camera.UpDirection = new Vector3D(state.Up.X, state.Up.Y, state.Up.Z);
        camera.FieldOfView = state.FieldOfView;
    }
}
