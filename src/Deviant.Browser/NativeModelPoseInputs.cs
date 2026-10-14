using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Models;
using Deviant.Export.Models;

namespace Deviant.Browser;

/// <summary>The host acquires these consumers before dispatch and holds them until the model job finishes.</summary>
public sealed record NativeModelPoseInputs(NativeModelPosePackageLease.Consumer Pose,
    NativeSkeletonBindingSession.NativeSkeletonBindingLease Rig)
{
    public RenderPoseIdentity Identity => new(Pose.Package.PoseSource.RigIdentity,
        Pose.Package.PoseSource.SnapshotSequence, Pose.Package.PoseSource.MatrixRowsSha256,
        Pose.Package.PoseSource.CaptureEvidenceSha256, Pose.Package.SerializedLodOrdinal);

    public void Validate(NativeContainerLease container, SourceRevision revision,
        IReadOnlyList<NativeModelSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(Pose); ArgumentNullException.ThrowIfNull(Rig);
        Pose.EnsureUnchanged(); Rig.EnsureUnchanged(); container.EnsureUnchanged();
        var package = Pose.Package; var source = package.PoseSource;
        if (!ReferenceEquals(Pose.MemoryBudget, container.MemoryBudget) || source.Revision != revision ||
            Rig.Result.Revision != revision || Rig.Result.ContainerIdentity != container.Identity ||
            source.RigIdentity != Rig.Result.SelectedIdentity || Rig.Binding is not { } binding ||
            binding.SkeletonIdentity != source.RigIdentity || binding.MatrixCount != package.Joints.Count ||
            Rig.Skeleton.MeshPrefix.GroupIndex != source.GroupIndex ||
            selections.Distinct().Count() != 1 || selections[0].MeshIdentity != source.MeshIdentity || selections[0].GroupIndex != source.GroupIndex)
            throw new ArchiveException("NativePoseSourceMismatch", "The pose does not match the selected model and rig sources.");
        var prefix = Rig.Skeleton.MeshPrefix;
        if (package.SerializedLodOrdinal < 0 || package.SerializedLodOrdinal >= prefix.Lods.Count)
            throw new ArchiveException("NativePoseLodMismatch", "The selected pose detail is unavailable.");
        var resolved = new NativeReferenceResolver(container.Directory).Resolve(prefix.Lods[package.SerializedLodOrdinal].MeshData);
        if (resolved is not { Status: ReferenceStatus.Unique, Candidates.Count: 1 } || resolved.Candidates[0] != source.MeshIdentity)
            throw new ArchiveException("NativePoseLodMismatch", "The pose mesh is not the selected rig detail.");
        var keys = package.Joints.Select((joint, i) => (joint.Key, i)).ToDictionary(pair => pair.Key, pair => pair.i, StringComparer.Ordinal);
        for (var i = 0; i < package.Joints.Count; i++)
        {
            var target = package.JointOrdinalsByMatrixRow[checked((uint)i * 3)];
            var sourceParent = binding.Entries[i].Node.StoredParent;
            var parent = package.Joints[target].ParentKey;
            var expectedParent = sourceParent == uint.MaxValue ? -1 : package.JointOrdinalsByMatrixRow[checked(sourceParent * 3)];
            if (parent is null ? expectedParent >= 0 : !keys.TryGetValue(parent, out var ordinal) || expectedParent != ordinal)
                throw new ArchiveException("NativePoseHierarchyMismatch", "The imported joint hierarchy differs from the selected rig.");
            var offset = FbxGeometryExporter.CalculateMeshToJointBind(package.MeshBindWorld, package.Joints[target].BindWorld);
            var storedMatrix = binding.Entries[i].StoredMatrix;
            for (var element = 0; element < 16; element++)
                if (Math.Abs(offset.Elements[element] - BitConverter.UInt32BitsToSingle(storedMatrix.GetNativeWordBits(element))) >
                    NativeModelPosePackageReader.MaximumRepresentationError)
                    throw new ArchiveException("NativePoseBindMismatch", "The imported numerical bind offset differs from the selected source matrix.");
        }
    }
}
