using Deviant.Assets;
using Deviant.Core;
using Deviant.Export.Models;
using Deviant.Formats.Models;

namespace Deviant.Browser;

public static class NativeModelPoseFbxInputs
{
    /// <summary>The caller keeps the pose and rig consumers alive through the coordinator's complete export call.</summary>
    public static NativeModelFbxSkeletonInputs Create(NativeContainerLease container, RenderMesh mesh,
        NativeModelPoseInputs inputs, AssetMemoryBudget budget, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(mesh); ArgumentNullException.ThrowIfNull(inputs);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(budget, container.MemoryBudget) || mesh.Pose != inputs.Identity ||
            mesh.SkinSourceGeometry is not { } geometry || mesh.SkinSourceMetadata is not { } metadata ||
            mesh.SkinAttributes is not { } attributes)
            throw new ArchiveException("NativePoseSourceMismatch", "The displayed model has no matching skin source data.");
        inputs.Validate(container, container.Revision, [new(mesh.Identity, mesh.GroupIndex)]);
        var package = inputs.Pose.Package;
        var projection = NativeMeshFbxSkinProjection.Create(metadata, attributes, geometry, package.MeshBindWorld,
            package.JointOrdinalsByMatrixRow, budget.Reserve, token: token);
        try
        {
            var joints = package.Joints.ToArray();
            for (var sourceOrdinal = 0; sourceOrdinal < joints.Length; sourceOrdinal++)
            {
                token.ThrowIfCancellationRequested();
                var target = package.JointOrdinalsByMatrixRow[checked((uint)sourceOrdinal * 3)];
                var name = inputs.Rig.Binding!.Entries[sourceOrdinal].NameMatch;
                if (joints[target].Name is null && name.Status == NativeSkeletonNodeNameMatchStatus.Unique &&
                    name.Entries.Count == 1 && name.Entries[0].TryGetUtf8Text(out var text, token) && !string.IsNullOrWhiteSpace(text))
                    joints[target] = joints[target] with { Name = text };
            }
            var skeleton = new FbxSkeletonExportInput(package.PoseSource.RigIdentity, package.PoseSource.Revision,
                joints, projection.Skins, _ =>
                {
                    token.ThrowIfCancellationRequested(); inputs.Pose.EnsureUnchanged(); inputs.Rig.EnsureUnchanged(); container.EnsureUnchanged();
                    return Task.CompletedTask;
                });
            var decoded = new DecodeResult<GeometryAsset>(geometry, mesh.Identity, package.PoseSource.Revision,
                [container.StoredSource.Extent], [], [], [], new Dictionary<string,string>());
            return new(budget, decoded, skeleton, mesh.Pose, FbxCoordinateMetadataPolicy.PreserveSourceNumbers, [projection]);
        }
        catch { projection.Dispose(); throw; }
    }
}
