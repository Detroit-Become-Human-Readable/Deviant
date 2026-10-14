using Deviant.Assets;
using Deviant.Browser;
using Deviant.Core;

namespace Deviant.Desktop;

public sealed record SceneRenderTreeItemState(string Key, bool IsVisible, bool IsSelected, bool IsExpanded);

/// <summary>Lightweight, budget-accounted scene viewport state that can outlive its render snapshot.</summary>
public sealed class SceneRenderViewState : IDisposable
{
    private readonly AssetMemoryBudget memoryBudget;
    private readonly AssetMemoryBudget.Reservation reservation;
    private readonly IReadOnlyList<SceneRenderTreeItemState> treeItems;
    private int disposed;

    internal SceneRenderViewState(SourceRevision revision, ResourceIdentity sectionIdentity,
        ResourceIdentity branchIdentity, GeometryBounds region, SceneRenderScope scope,
        int maximumPreviewDimension, ModelCameraState camera, bool showHidden,
        SceneRenderTreeItemState[] treeItems, string? selectedOccurrenceKey,
        AssetMemoryBudget memoryBudget, AssetMemoryBudget.Reservation reservation,
        ResourceIdentity? selectedStructureIdentity = null, bool allowLargeData = false,
        IReadOnlyList<RenderMeshSelection>? selectedMeshes = null)
    {
        RevisionValue = revision;
        SectionIdentityValue = sectionIdentity;
        BranchIdentityValue = branchIdentity;
        RegionValue = region;
        ScopeValue = scope;
        MaximumPreviewDimensionValue = maximumPreviewDimension;
        CameraValue = camera;
        ShowHiddenValue = showHidden;
        this.treeItems = Array.AsReadOnly(treeItems);
        SelectedOccurrenceKeyValue = selectedOccurrenceKey;
        SelectedStructureIdentityValue = selectedStructureIdentity;
        this.memoryBudget = memoryBudget;
        this.reservation = reservation;
        AllowLargeDataValue = allowLargeData;
        selectedMeshItems = (selectedMeshes ?? (selectedOccurrenceKey is null ? [] : [new(selectedOccurrenceKey)])).ToArray();
    }

    private SourceRevision RevisionValue { get; }
    private ResourceIdentity SectionIdentityValue { get; }
    private ResourceIdentity BranchIdentityValue { get; }
    private GeometryBounds RegionValue { get; }
    private SceneRenderScope ScopeValue { get; }
    private int MaximumPreviewDimensionValue { get; }
    private ModelCameraState CameraValue { get; }
    private bool ShowHiddenValue { get; }
    private string? SelectedOccurrenceKeyValue { get; }
    private ResourceIdentity? SelectedStructureIdentityValue { get; }
    private bool AllowLargeDataValue { get; }
    private readonly IReadOnlyList<RenderMeshSelection> selectedMeshItems;
    public IReadOnlyList<RenderMeshSelection> SelectedMeshes { get { ThrowIfDisposed(); return selectedMeshItems; } }

    public SourceRevision Revision { get { ThrowIfDisposed(); return RevisionValue; } }
    public ResourceIdentity SectionIdentity { get { ThrowIfDisposed(); return SectionIdentityValue; } }
    public ResourceIdentity BranchIdentity { get { ThrowIfDisposed(); return BranchIdentityValue; } }
    public GeometryBounds Region { get { ThrowIfDisposed(); return RegionValue; } }
    public SceneRenderScope Scope { get { ThrowIfDisposed(); return ScopeValue; } }
    public int MaximumPreviewDimension { get { ThrowIfDisposed(); return MaximumPreviewDimensionValue; } }
    public ModelCameraState Camera { get { ThrowIfDisposed(); return CameraValue; } }
    public bool ShowHidden { get { ThrowIfDisposed(); return ShowHiddenValue; } }
    public IReadOnlyList<SceneRenderTreeItemState> TreeItems { get { ThrowIfDisposed(); return treeItems; } }
    public string? SelectedOccurrenceKey { get { ThrowIfDisposed(); return SelectedOccurrenceKeyValue; } }
    public ResourceIdentity? SelectedStructureIdentity { get { ThrowIfDisposed(); return SelectedStructureIdentityValue; } }
    public AssetMemoryBudget MemoryBudget { get { ThrowIfDisposed(); return memoryBudget; } }
    public long ReservedBytes { get { ThrowIfDisposed(); return reservation.Bytes; } }
    public bool AllowLargeData { get { ThrowIfDisposed(); return AllowLargeDataValue; } }
    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) reservation.Dispose();
    }
}
