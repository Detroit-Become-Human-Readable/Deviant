using Deviant.Assets;
using Deviant.Core;
using Deviant.Export.Models;
using Deviant.Formats.Models;

namespace Deviant.Browser;

/// <summary>Captures the caller's exact mesh group and LOD choice for one export.</summary>
public sealed record NativeModelFbxExportSelection(string GeometryKey, ResourceIdentity Identity,
    int GroupIndex, int LodOrdinal, FbxGeometrySelection WriterSelection);

/// <summary>Material inputs and their source leases, retained through the complete writer call.</summary>
public sealed class NativeModelFbxMaterialInputs : IDisposable
{
    private List<IDisposable>? retainedResources;

    public NativeModelFbxMaterialInputs(AssetMemoryBudget memoryBudget,
        IReadOnlyList<FbxMaterialExportInput> inputs, IEnumerable<IDisposable>? retainedResources = null)
    {
        MemoryBudget = memoryBudget ?? throw new ArgumentNullException(nameof(memoryBudget));
        ArgumentNullException.ThrowIfNull(inputs);
        Inputs = Array.AsReadOnly(inputs.ToArray());
        this.retainedResources = retainedResources?.ToList() ?? [];
    }

    public AssetMemoryBudget MemoryBudget { get; }
    public IReadOnlyList<FbxMaterialExportInput> Inputs { get; }

    public void Dispose()
    {
        var released = Interlocked.Exchange(ref retainedResources, null);
        if (released is null) return;
        Exception? failure = null;
        for (var index = released.Count - 1; index >= 0; index--)
            try { released[index].Dispose(); } catch (Exception error) { failure ??= error; }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

/// <summary>Source geometry, explicit skin data and leases retained for one FBX writer job.</summary>
public sealed class NativeModelFbxSkeletonInputs : IDisposable
{
    private List<IDisposable>? resources;
    public NativeModelFbxSkeletonInputs(AssetMemoryBudget memoryBudget, DecodeResult<GeometryAsset> geometry,
        FbxSkeletonExportInput skeleton, RenderPoseIdentity? pose,
        FbxCoordinateMetadataPolicy coordinateMetadata = FbxCoordinateMetadataPolicy.VerifiedBasis,
        IEnumerable<IDisposable>? retainedResources = null)
    {
        MemoryBudget = memoryBudget ?? throw new ArgumentNullException(nameof(memoryBudget));
        Geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
        Pose = pose; CoordinateMetadata = coordinateMetadata;
        resources = retainedResources?.ToList() ?? [];
    }
    public AssetMemoryBudget MemoryBudget { get; }
    public DecodeResult<GeometryAsset> Geometry { get; }
    public FbxSkeletonExportInput Skeleton { get; }
    public RenderPoseIdentity? Pose { get; }
    public FbxCoordinateMetadataPolicy CoordinateMetadata { get; }
    public void Dispose()
    {
        var released = Interlocked.Exchange(ref resources, null);
        if (released is not null) for (var i = released.Count - 1; i >= 0; i--) released[i].Dispose();
    }
}

/// <summary>Coordinates a native-model snapshot with the validated static FBX writer.</summary>
public sealed class NativeModelFbxExportCoordinator(FbxGeometryExporter? exporter = null)
{
    private readonly FbxGeometryExporter writer = exporter ?? new FbxGeometryExporter();

    /// <param name="materialInputProvider">
    /// Resolves original material and texture sources through the owning shared sessions, accounts
    /// retained and temporary data against the supplied budget, and returns leases plus source
    /// revision validators. Viewport preview pixels are not export sources.
    /// </param>
    public async Task<FbxGeometryExportReport> ExportAsync(RenderAssetSnapshot snapshot,
        SourceRevision expectedRevision, NativeModelFbxExportSelection selection,
        Func<RenderMesh, AssetMemoryBudget, CancellationToken, Task<NativeModelFbxMaterialInputs>> materialInputProvider,
        Func<CancellationToken, Task> validateSourceRevision,
        string sourceRoot, string destination, FbxGeometryExportOptions? options = null,
        CancellationToken cancellationToken = default,
        Func<RenderMesh, AssetMemoryBudget, CancellationToken, Task<NativeModelFbxSkeletonInputs>>? skeletonInputProvider = null,
        Func<RenderMesh, AssetMemoryBudget, CancellationToken, Task<NativeModelAnimationFbxInputs>>? animationInputProvider = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(selection.Identity);
        ArgumentNullException.ThrowIfNull(selection.WriterSelection);
        ArgumentNullException.ThrowIfNull(materialInputProvider);
        ArgumentNullException.ThrowIfNull(validateSourceRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (animationInputProvider is not null && skeletonInputProvider is null)
            throw Failure("AnimationSkeletonRequired", "Animation export requires the selected source skeleton.");

        using var snapshotLease = snapshot.AcquireLease();
        ValidateSelection(snapshot, expectedRevision, selection);
        var mesh = snapshot.Meshes[selection.GeometryKey];
        if (mesh.Pose?.SerializedLodOrdinal is { } poseLod && poseLod != selection.LodOrdinal)
            throw Failure("SkeletonSourceMismatch", "The displayed pose belongs to another selected detail.");
        if (skeletonInputProvider is null && mesh.Pose is not null)
            throw Failure("SkinExportUnsupported", "A posed snapshot requires its source geometry and skeleton inputs.");
        if (skeletonInputProvider is null) ValidateGeometry(mesh.Geometry);
        await validateSourceRevision(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        using var skeleton = skeletonInputProvider is null ? null :
            await skeletonInputProvider(mesh, snapshot.MemoryBudget, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The skeleton input provider returned no result.");
        var exportGeometry = mesh.Geometry;
        if (skeleton is not null)
        {
            if (!ReferenceEquals(skeleton.MemoryBudget, snapshot.MemoryBudget))
                throw Failure("SkeletonBudgetMismatch", "Skeleton preparation must use the snapshot's shared memory budget.");
            if (skeleton.Geometry.Identity != mesh.Identity || skeleton.Geometry.Revision != expectedRevision || skeleton.Pose != mesh.Pose)
                throw Failure("SkeletonSourceMismatch", "The source geometry and pose must match the exact selected model snapshot.");
            exportGeometry = skeleton.Geometry.Value ?? throw Failure("GeometryValidationFailed", "The skeleton provider returned no source geometry.");
            if (exportGeometry.Primitives.Count != mesh.Geometry.Primitives.Count ||
                exportGeometry.Primitives.Where((primitive, i) => primitive.Key != mesh.Geometry.Primitives[i].Key ||
                    primitive.Positions.Count != mesh.Geometry.Primitives[i].Positions.Count ||
                    !primitive.Indices.SequenceEqual(mesh.Geometry.Primitives[i].Indices)).Any())
                throw Failure("SkeletonSourceMismatch", "The source control points and primitive selection differ from the selected model.");
            ValidateGeometry(exportGeometry, true, skeleton.CoordinateMetadata);
            if (mesh.Pose is { } pose && pose.RigIdentity != skeleton.Skeleton.Identity)
                throw Failure("SkeletonSourceMismatch", "The skeleton differs from the displayed pose.");
        }

        using var materials = await materialInputProvider(mesh, snapshot.MemoryBudget, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The material input provider returned no result.");
        if (!ReferenceEquals(materials.MemoryBudget, snapshot.MemoryBudget))
            throw Failure("MaterialBudgetMismatch", "Material source preparation must use the snapshot's shared memory budget.");
        cancellationToken.ThrowIfCancellationRequested();
        await validateSourceRevision(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        using var animation = animationInputProvider is null ? null :
            await animationInputProvider(mesh, snapshot.MemoryBudget, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The animation input provider returned no result.");
        if (animation is not null && (!ReferenceEquals(animation.MemoryBudget, snapshot.MemoryBudget) ||
            !ReferenceEquals(animation.Skeleton, skeleton!.Skeleton)))
            throw Failure("AnimationSourceMismatch", "The animation must use the selected skeleton and shared memory budget.");

        var decoded = skeleton?.Geometry ?? new DecodeResult<GeometryAsset>(exportGeometry, mesh.Identity, snapshot.Revision,
            snapshot.SourceExtents, [], [], [], new Dictionary<string, string>());
        var input = new FbxModelExportInput(decoded, selection.WriterSelection,
            snapshot.MemoryBudget.Reserve, materials.Inputs)
        {
            ValidateGeometrySourceRevision = validateSourceRevision,
            Skeleton = skeleton?.Skeleton,
            Animation = animation?.Animation,
            CoordinateMetadata = skeleton?.CoordinateMetadata ?? FbxCoordinateMetadataPolicy.VerifiedBasis
        };
        return await writer.ExportAsync(input, sourceRoot, destination, options, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ValidateSelection(RenderAssetSnapshot snapshot, SourceRevision expectedRevision,
        NativeModelFbxExportSelection selection)
    {
        if (snapshot.Revision != expectedRevision)
            throw Failure("SourceRevisionMismatch", "The selected model snapshot no longer matches the expected source revision.");
        if (selection.GroupIndex < 0 || selection.LodOrdinal < 0)
            throw Failure("SelectionInvalid", "The selected group and LOD ordinals must be non-negative.");
        if (!StringComparer.Ordinal.Equals(selection.Identity.IndexContentId, expectedRevision.IndexContentId))
            throw Failure("SelectionIdentityMismatch", "The selected identity belongs to another source index.");
        if (string.IsNullOrWhiteSpace(selection.GeometryKey) ||
            !snapshot.Meshes.TryGetValue(selection.GeometryKey, out var mesh) ||
            !StringComparer.Ordinal.Equals(mesh.Key, selection.GeometryKey))
            throw Failure("GeometrySelectionMissing", "The exact selected geometry key is unavailable in this snapshot.");
        if (mesh.Identity != selection.Identity || mesh.GroupIndex != selection.GroupIndex)
            throw Failure("GeometrySelectionMismatch", "The exact geometry key does not match the selected identity and group.");
        if (selection.WriterSelection.Identity != selection.Identity ||
            selection.WriterSelection.GroupIndex != selection.GroupIndex ||
            selection.WriterSelection.LodIndex != selection.LodOrdinal ||
            !StringComparer.Ordinal.Equals(selection.WriterSelection.Key, selection.GeometryKey))
            throw Failure("WriterSelectionMismatch", "The writer selection does not match the exact mesh group and LOD choice.");
    }

    private static void ValidateGeometry(GeometryAsset geometry, bool hasSkeleton = false,
        FbxCoordinateMetadataPolicy coordinateMetadata = FbxCoordinateMetadataPolicy.VerifiedBasis)
    {
        var validation = GeometryValidator.Validate(geometry);
        if (!validation.IsValid)
            throw Failure("GeometryValidationFailed", "The selected geometry contains invalid references or vertex data.");
        if (coordinateMetadata == FbxCoordinateMetadataPolicy.VerifiedBasis && !geometry.Coordinates.HasVerifiedBasis)
            throw Failure("CoordinateProfileUnverified", "A verified coordinate basis is required before resolving materials.");
        if (!hasSkeleton && geometry.Primitives.Any(primitive => primitive.Skin is not null))
            throw Failure("SkinExportUnsupported", "Skinned geometry requires skeleton and bind-pose inputs.");
    }

    private static NativeModelFbxExportException Failure(string code, string message) => new(code, message);
}

public sealed class NativeModelFbxExportException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
