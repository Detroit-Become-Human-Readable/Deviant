using System.Text;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Export.Models;
using Deviant.Formats.Models;

namespace Deviant.Browser;

/// <summary>Builds a bind-pose skeleton input directly from the selected source mesh and its typed rig binding.</summary>
public static class NativeModelBindFbxInputs
{
    private const int MaximumJoints = 8192;
    private const double AffineTolerance = 1e-9;
    private const double PivotTolerance = 1e-12;

    /// <summary>
    /// The caller owns and keeps <paramref name="rigLease"/> alive through the complete export call.
    /// The returned input retains the container, projection buffers and its planning reservation.
    /// </summary>
    public static NativeModelFbxSkeletonInputs Create(NativeContainerLease container, RenderMesh mesh,
        NativeSkeletonBindingSession.NativeSkeletonBindingLease rigLease, AssetMemoryBudget budget,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(rigLease);
        ArgumentNullException.ThrowIfNull(budget);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(budget, container.MemoryBudget))
            throw new InvalidDataException("NativeBindFbxBudgetMismatch");

        var containerRetention = container.AcquireLease();
        IDisposable? planningReservation = null;
        NativeMeshFbxSkinLease? projection = null;
        try
        {
            container.EnsureUnchanged();
            rigLease.EnsureUnchanged();
            if (mesh.Pose is not null || mesh.SkinSourceGeometry is not { } geometry ||
                mesh.SkinSourceMetadata is not { } metadata || mesh.SkinAttributes is not { } attributes)
                throw new InvalidDataException("NativeBindFbxSourceSkinUnavailable");

            var skeleton = rigLease.Skeleton;
            var binding = rigLease.Binding;
            var sessionResult = rigLease.Result;
            if (sessionResult.Status != NativeSkeletonBindingSessionStatus.Bound || binding is null ||
                skeleton.Version != 15 || skeleton.Identity != sessionResult.SelectedIdentity ||
                sessionResult.ContainerIdentity != container.Identity || sessionResult.Revision != container.Revision ||
                binding.SkeletonIdentity != skeleton.Identity || binding.Revision != container.Revision ||
                binding.EntityNode != skeleton.EntityNode || skeleton.EntityNode is null ||
                sessionResult.NodeArrayIdentity != binding.NodeArrayIdentity ||
                skeleton.MeshPrefix.GroupIndex != (uint)mesh.GroupIndex || mesh.GroupIndex < 0 ||
                binding.MatrixCount is < 1 or > MaximumJoints || binding.Entries.Count != binding.MatrixCount ||
                skeleton.Matrices.Count != binding.MatrixCount || mesh.Identity.IndexContentId != container.Identity.IndexContentId ||
                mesh.Identity.Ordinal != container.Identity.Ordinal ||
                metadata.Identity != mesh.Identity || attributes.Identity != mesh.Identity ||
                attributes.GroupIndex != mesh.GroupIndex || (uint)mesh.GroupIndex >= (uint)metadata.Groups.Count)
                throw new InvalidDataException("NativeBindFbxSourceMismatch");

            var nodeArray = rigLease.NodeArray;
            if (nodeArray is null || nodeArray.Identity != binding.NodeArrayIdentity ||
                nodeArray.Revision != binding.Revision || nodeArray.BodyVersion != 14 ||
                nodeArray.Nodes.Count != binding.MatrixCount)
                throw new InvalidDataException("NativeBindFbxNodeArrayMismatch");

            ValidateSelectedLod(container, skeleton, mesh, token);
            var jointCount = binding.MatrixCount;
            planningReservation = budget.Reserve(checked(4096L + jointCount * 4096L),
                "FBX source bind skeleton matrices");
            var parents = ValidateSourceHierarchy(binding.Entries, token);
            var names = ReadSourceNames(binding.Entries, token);

            var bindWorlds = new double[jointCount][];
            var keys = new string[jointCount];
            for (var ordinal = 0; ordinal < jointCount; ordinal++)
            {
                if ((ordinal & 127) == 0) token.ThrowIfCancellationRequested();
                var entry = binding.Entries[ordinal];
                if (entry.Ordinal != ordinal || entry.StoredMatrix.Ordinal != ordinal ||
                    entry.Node.Ordinal != ordinal || entry.StoredMatrix.RawBytes.Length != 64)
                    throw new InvalidDataException("NativeBindFbxOrdinalMismatch");

                var geometryToJoint = ReadNativeMatrix(entry.StoredMatrix, ordinal);
                bindWorlds[ordinal] = InvertAffine(geometryToJoint, $"matrix {ordinal}");
                keys[ordinal] = string.Concat(skeleton.Identity.Key, "/joint/",
                    ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            var joints = new FbxJointExportInput[jointCount];
            for (var ordinal = 0; ordinal < jointCount; ordinal++)
            {
                if ((ordinal & 127) == 0) token.ThrowIfCancellationRequested();
                var parent = parents[ordinal];
                var localBind = parent < 0
                    ? (double[])bindWorlds[ordinal].Clone()
                    : MultiplyAffine(bindWorlds[ordinal], InvertAffine(bindWorlds[parent], $"parent {parent}"));
                EnsureAffine(localBind, $"local bind {ordinal}");
                var localElements = new float[16];
                for (var element = 0; element < 16; element++)
                {
                    var value = (float)localBind[element];
                    if (!float.IsFinite(value)) throw new InvalidDataException("NativeBindFbxLocalBindInvalid");
                    localElements[element] = value;
                }

                joints[ordinal] = new(keys[ordinal], parent < 0 ? null : keys[parent], names[ordinal],
                    new(Array.AsReadOnly(bindWorlds[ordinal])),
                    new(Array.AsReadOnly(localElements), MatrixLayout.RowMajor, VectorMultiplicationSide.Row));
            }

            var meshBindWorld = IdentityMatrix();
            ValidateBindPosePrecision(joints, parents, bindWorlds, token);
            var matrixRowToJoint = new Dictionary<uint, int>(jointCount);
            for (var ordinal = 0; ordinal < jointCount; ordinal++)
                matrixRowToJoint.Add(checked((uint)ordinal * 3), ordinal);

            projection = NativeMeshFbxSkinProjection.Create(metadata, attributes, geometry, meshBindWorld,
                matrixRowToJoint, budget.Reserve, token: token);
            if (projection.Skins.Count == 0)
                throw new InvalidDataException("NativeBindFbxSkinUnavailable");

            var retainedSkeleton = new FbxSkeletonExportInput(skeleton.Identity, container.Revision,
                Array.AsReadOnly(joints), projection.Skins, cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    container.EnsureUnchanged();
                    rigLease.EnsureUnchanged();
                    return Task.CompletedTask;
                });
            var decodedGeometry = new DecodeResult<GeometryAsset>(geometry, mesh.Identity, container.Revision,
                container.SourceExtents, [], [], [], new Dictionary<string, string>());

            var retained = new IDisposable[] { containerRetention, planningReservation!, projection };
            var result = new NativeModelFbxSkeletonInputs(budget, decodedGeometry, retainedSkeleton, null,
                FbxCoordinateMetadataPolicy.PreserveSourceNumbers, retained);
            containerRetention = null!;
            planningReservation = null;
            projection = null;
            return result;
        }
        catch
        {
            projection?.Dispose();
            planningReservation?.Dispose();
            containerRetention?.Dispose();
            throw;
        }
    }

    private static void ValidateSelectedLod(NativeContainerLease container, NativeSkeletonDocument skeleton,
        RenderMesh mesh, CancellationToken token)
    {
        var resolver = new NativeReferenceResolver(container.Directory);
        var found = false;
        foreach (var lod in skeleton.MeshPrefix.Lods)
        {
            token.ThrowIfCancellationRequested();
            var resolved = resolver.Resolve(lod.MeshData);
            if (resolved is { Status: ReferenceStatus.Unique, Candidates.Count: 1 } &&
                resolved.Candidates[0] == mesh.Identity)
                found = true;
        }
        if (!found) throw new InvalidDataException("NativeBindFbxMeshLodMismatch");
    }

    private static void ValidateBindPosePrecision(IReadOnlyList<FbxJointExportInput> joints, int[] parents,
        IReadOnlyList<double[]> expectedWorlds, CancellationToken token)
    {
        var worlds = new double[joints.Count][];
        var path = new Stack<int>();
        for (int ordinal = 0; ordinal < joints.Count; ordinal++)
        {
            token.ThrowIfCancellationRequested();
            int current = ordinal;
            while (current >= 0 && worlds[current] is null) { path.Push(current); current = parents[current]; }
            while (path.TryPop(out current))
            {
                token.ThrowIfCancellationRequested();
                var local = joints[current].PoseLocal.Elements.Select(value => (double)value).ToArray();
                worlds[current] = parents[current] < 0 ? local : MultiplyAffine(local, worlds[parents[current]]);
                for (int element = 0; element < 16; element++)
                    if (Math.Abs(worlds[current][element] - expectedWorlds[current][element]) > 2e-6)
                        throw new InvalidDataException("NativeBindFbxPosePrecisionUnsupported");
            }
        }
    }

    private static int[] ValidateSourceHierarchy(IReadOnlyList<NativeSkeletonNodeArrayBindingEntry> entries,
        CancellationToken token)
    {
        var count = entries.Count;
        var parents = new int[count];
        var childOwners = new int[count];
        Array.Fill(childOwners, -1);
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            token.ThrowIfCancellationRequested();
            var entry = entries[ordinal];
            if (entry.Ordinal != ordinal || entry.Node.Ordinal != ordinal || entry.StoredMatrix.Ordinal != ordinal ||
                entry.Node.Children is null)
                throw new InvalidDataException("NativeBindFbxHierarchyInvalid");
            if (entry.Node.StoredParent == uint.MaxValue) parents[ordinal] = -1;
            else if (entry.Node.StoredParent >= (uint)count || entry.Node.StoredParent == (uint)ordinal)
                throw new InvalidDataException("NativeBindFbxHierarchyInvalid");
            else parents[ordinal] = (int)entry.Node.StoredParent;
        }

        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            token.ThrowIfCancellationRequested();
            var entry = entries[ordinal];
            foreach (var child in entry.Node.Children)
            {
                token.ThrowIfCancellationRequested();
                if (child >= (uint)count || child == (uint)ordinal || childOwners[child] >= 0 ||
                    parents[child] != ordinal)
                    throw new InvalidDataException("NativeBindFbxHierarchyInvalid");
                childOwners[child] = ordinal;
            }
        }
        for (var ordinal = 0; ordinal < count; ordinal++)
            if (childOwners[ordinal] != parents[ordinal])
                throw new InvalidDataException("NativeBindFbxHierarchyInvalid");

        var states = new byte[count];
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            token.ThrowIfCancellationRequested();
            var current = ordinal;
            while (current >= 0 && states[current] == 0)
            {
                states[current] = 1;
                current = parents[current];
            }
            if (current >= 0 && states[current] == 1)
                throw new InvalidDataException("NativeBindFbxHierarchyCycle");
            current = ordinal;
            while (current >= 0 && states[current] == 1)
            {
                states[current] = 2;
                current = parents[current];
            }
        }
        return parents;
    }

    private static string?[] ReadSourceNames(IReadOnlyList<NativeSkeletonNodeArrayBindingEntry> entries,
        CancellationToken token)
    {
        var names = new string?[entries.Count];
        for (var ordinal = 0; ordinal < entries.Count; ordinal++)
        {
            token.ThrowIfCancellationRequested();
            var match = entries[ordinal].NameMatch;
            if (match.Status == NativeSkeletonNodeNameMatchStatus.Unique && match.Entries.Count == 1 &&
                match.Entries[0].TryGetUtf8Text(out var name, token) && !string.IsNullOrWhiteSpace(name) &&
                !name.Any(char.IsControl) && Encoding.UTF8.GetByteCount(name) <= 1024)
                names[ordinal] = name;
        }
        return names;
    }

    private static double[] ReadNativeMatrix(SkeletonStoredMatrix stored, int ordinal)
    {
        var matrix = new double[16];
        for (var element = 0; element < matrix.Length; element++)
        {
            var value = BitConverter.UInt32BitsToSingle(stored.GetNativeWordBits(element));
            if (!float.IsFinite(value)) throw new InvalidDataException($"NativeBindFbxMatrixNonFinite:{ordinal}");
            matrix[element] = value;
        }
        EnsureAffine(matrix, $"stored matrix {ordinal}");
        matrix[3] = matrix[7] = matrix[11] = 0;
        matrix[15] = 1;
        return matrix;
    }

    private static double[] InvertAffine(IReadOnlyList<double> matrix, string subject)
    {
        EnsureAffine(matrix, subject);
        var augmented = new double[3, 6];
        var maximum = 0d;
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                var value = matrix[row * 4 + column];
                augmented[row, column] = value;
                maximum = Math.Max(maximum, Math.Abs(value));
            }
            augmented[row, row + 3] = 1;
        }
        if (maximum == 0) throw new InvalidDataException($"NativeBindFbxMatrixSingular:{subject}");
        var minimumPivot = maximum * PivotTolerance;
        for (var column = 0; column < 3; column++)
        {
            var pivotRow = column;
            var pivotMagnitude = Math.Abs(augmented[pivotRow, column]);
            for (var row = column + 1; row < 3; row++)
            {
                var candidate = Math.Abs(augmented[row, column]);
                if (candidate > pivotMagnitude) { pivotRow = row; pivotMagnitude = candidate; }
            }
            if (!double.IsFinite(pivotMagnitude) || pivotMagnitude <= minimumPivot)
                throw new InvalidDataException($"NativeBindFbxMatrixSingular:{subject}");
            if (pivotRow != column)
                for (var cell = 0; cell < 6; cell++)
                    (augmented[column, cell], augmented[pivotRow, cell]) =
                        (augmented[pivotRow, cell], augmented[column, cell]);

            var pivot = augmented[column, column];
            for (var cell = 0; cell < 6; cell++) augmented[column, cell] /= pivot;
            for (var row = 0; row < 3; row++)
            {
                if (row == column) continue;
                var factor = augmented[row, column];
                for (var cell = 0; cell < 6; cell++) augmented[row, cell] -= factor * augmented[column, cell];
            }
        }

        var inverse = IdentityMatrix().Elements.ToArray();
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                inverse[row * 4 + column] = augmented[row, column + 3];
        for (var column = 0; column < 3; column++)
            inverse[12 + column] = -(matrix[12] * inverse[column] +
                matrix[13] * inverse[4 + column] + matrix[14] * inverse[8 + column]);
        EnsureAffine(inverse, $"inverse {subject}");
        return inverse;
    }

    private static double[] MultiplyAffine(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        EnsureAffine(left, "left matrix"); EnsureAffine(right, "right matrix");
        var result = new double[16];
        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 4; column++)
                for (var inner = 0; inner < 4; inner++)
                    result[row * 4 + column] += left[row * 4 + inner] * right[inner * 4 + column];
        EnsureAffine(result, "matrix product");
        result[3] = result[7] = result[11] = 0;
        result[15] = 1;
        return result;
    }

    private static void EnsureAffine(IReadOnlyList<double> matrix, string subject)
    {
        if (matrix.Count != 16 || matrix.Any(value => !double.IsFinite(value)) ||
            Math.Abs(matrix[3]) > AffineTolerance || Math.Abs(matrix[7]) > AffineTolerance ||
            Math.Abs(matrix[11]) > AffineTolerance || Math.Abs(matrix[15] - 1) > AffineTolerance)
            throw new InvalidDataException($"NativeBindFbxMatrixNotAffine:{subject}");
    }

    private static FbxBindMatrix IdentityMatrix() => new(Array.AsReadOnly<double>(
        [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]));
}
