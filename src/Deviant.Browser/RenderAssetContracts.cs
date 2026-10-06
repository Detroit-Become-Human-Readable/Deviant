using System.Numerics;
using Deviant.Assets;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

public sealed record RenderMesh(ResourceIdentity Identity, int GroupIndex, GeometryAsset Geometry,
    IReadOnlyDictionary<string, ResourceIdentity>? MaterialsByPrimitive = null)
{
    public string Key => $"{Identity.Key}/group/{GroupIndex}";
}

public sealed record RenderOccurrence(string Key, ResourceIdentity Entity, ResourceIdentity Node,
    string Label, string GeometryKey, Matrix4x4 World, GeometryBounds WorldBounds,
    IReadOnlyList<ResourceIdentity> InstancePath)
{
    public bool SourceVisible { get; init; } = true;
    public uint? NativeFlags { get; init; }
}

public sealed record RenderHierarchyNode(string Key, string? ParentKey, ResourceIdentity Node,
    string Label, Matrix4x4 Local, Matrix4x4 World, string? OccurrenceKey,
    IReadOnlyList<ResourceReference> References);

public sealed record RenderTexture(ResourceIdentity NativeIdentity, TextureSurface Surface, string RgbaSha256,
    ResourceIdentity? SourceIdentity = null, ResourceIdentity? CatalogIdentity = null)
{
    public bool PreviewWasResized { get; init; }
}

public sealed record RenderMaterial(MaterialAsset Asset, IReadOnlyDictionary<int, RenderTexture> TexturesBySlot);

public sealed record RenderIssue(ResourceIdentity Identity, string Code, string Message,
    string? OccurrenceKey = null);

public sealed record RenderCoverage(int TestedOccurrences, int IncludedOccurrences, int OutsideOccurrences,
    int RenderedParts, int Containers, int CatalogInstances);

public sealed record RenderLoadProgress(string Stage, int Completed, int Total, long RetainedBytes);

public enum SceneRenderScope { Region, Branch, Scene }

public sealed record SceneRenderRegion(ResourceIdentity InstanceIdentity, string Label,
    GeometryBounds Bounds, GeometryBounds PreviewBounds, int OccurrenceCount)
{
    public int? VisibleOccurrenceCount { get; init; }
}

public sealed record SceneRegionDiscovery(IReadOnlyList<SceneRenderRegion> Regions,
    IReadOnlyList<RenderIssue> Issues);

public sealed record SceneRenderRequest(SceneLoadedSection Section, ResourceIdentity BranchInstance,
    GeometryBounds Region, SceneRenderScope Scope = SceneRenderScope.Region)
{
    public int MaximumPreviewDimension { get; init; } = 1024;
    public bool AllowLargeData { get; init; }
}

/// <summary>A shared geometry/material result retained while scene or individual-model views use it.</summary>
public sealed class RenderAssetSnapshot : IDisposable
{
    private readonly object sync = new();
    private List<IDisposable>? resources;
    private int references = 1;
    private bool ownerDisposed;

    public RenderAssetSnapshot(SourceRevision revision, IReadOnlyDictionary<string, RenderMesh> meshes,
        IReadOnlyList<RenderOccurrence> occurrences, IReadOnlyDictionary<ResourceIdentity, RenderMaterial> materials,
        GeometryBounds region, IReadOnlyList<RenderIssue> issues, IReadOnlyList<RenderHierarchyNode> hierarchy,
        RenderCoverage coverage, IReadOnlyList<SourceExtent> sourceExtents, AssetMemoryBudget memoryBudget,
        IEnumerable<IDisposable>? resources = null)
    {
        Revision = revision;
        Meshes = meshes;
        Occurrences = occurrences;
        Materials = materials;
        Region = region;
        Issues = issues;
        Hierarchy = hierarchy;
        Coverage = coverage;
        SourceExtents = sourceExtents;
        MemoryBudget = memoryBudget;
        this.resources = resources?.ToList() ?? [];
    }

    public SourceRevision Revision { get; }
    public IReadOnlyDictionary<string, RenderMesh> Meshes { get; }
    public IReadOnlyList<RenderOccurrence> Occurrences { get; }
    public IReadOnlyDictionary<ResourceIdentity, RenderMaterial> Materials { get; }
    public GeometryBounds Region { get; }
    public IReadOnlyList<RenderIssue> Issues { get; }
    public IReadOnlyList<RenderHierarchyNode> Hierarchy { get; }
    public RenderCoverage Coverage { get; }
    public IReadOnlyList<SourceExtent> SourceExtents { get; }
    public AssetMemoryBudget MemoryBudget { get; }

    public IDisposable AcquireLease()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(resources is null, this);
            checked { references++; }
            return new SnapshotLease(this);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (ownerDisposed) return;
            ownerDisposed = true;
        }
        Release();
    }

    private void Release()
    {
        List<IDisposable>? released = null;
        lock (sync)
        {
            if (--references == 0) { released = resources; resources = null; }
        }
        if (released is null) return;
        for (var index = released.Count - 1; index >= 0; index--) released[index].Dispose();
    }

    private sealed class SnapshotLease(RenderAssetSnapshot owner) : IDisposable
    {
        private RenderAssetSnapshot? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Release();
    }
}
