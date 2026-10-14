using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text.Json;
using System.Numerics;
using Deviant.Assets;
using Deviant.Export.Models;
using Deviant.Formats.Models;

namespace Deviant.Browser;

public sealed record NativeModelPosePackage(int Version, NativeSkinningPoseSource PoseSource,
    int SerializedLodOrdinal, ReadOnlyMemory<byte> MatrixRows, IReadOnlyList<FbxJointExportInput> Joints,
    FbxBindMatrix MeshBindWorld, IReadOnlyDictionary<uint, int> JointOrdinalsByMatrixRow)
{
    public SourceTransform? MaterialWorldTransform { get; init; }
}

/// <summary>A selected pose file and its budget remain retained until every consumer finishes.</summary>
public sealed class NativeModelPosePackageLease : IDisposable
{
    private readonly object gate = new();
    private NativeModelPosePackage? package;
    private FileStream? file;
    private IDisposable? reservation;
    private int references = 1;
    private readonly long fileLength;
    private bool disposed;
    internal NativeModelPosePackageLease(NativeModelPosePackage package, FileStream file,
        IDisposable reservation, AssetMemoryBudget budget, string sha256)
    { this.package = package; this.file = file; fileLength = file.Length; this.reservation = reservation; MemoryBudget = budget; Sha256 = sha256; }
    public AssetMemoryBudget MemoryBudget { get; }
    public string Sha256 { get; }
    public Consumer AcquireLease()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            checked { references++; }
            return new(this);
        }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        Release();
    }
    private void Release()
    {
        lock (gate)
        {
            if (--references != 0) return;
            package = null; file?.Dispose(); file = null;
            reservation?.Dispose(); reservation = null;
        }
    }
    public sealed class Consumer : IDisposable
    {
        private NativeModelPosePackageLease? owner;
        internal Consumer(NativeModelPosePackageLease owner) => this.owner = owner;
        public NativeModelPosePackage Package
        {
            get
            {
                var current = owner ?? throw new ObjectDisposedException(nameof(Consumer));
                lock (current.gate) return current.package ?? throw new ObjectDisposedException(nameof(Consumer));
            }
        }
        public AssetMemoryBudget MemoryBudget => (owner ?? throw new ObjectDisposedException(nameof(Consumer))).MemoryBudget;
        public string Sha256 => (owner ?? throw new ObjectDisposedException(nameof(Consumer))).Sha256;
        public void EnsureUnchanged()
        {
            var current = owner ?? throw new ObjectDisposedException(nameof(Consumer));
            lock (current.gate)
                if (current.file is null || current.file.Length != current.fileLength)
                    throw new IOException("The selected pose file changed.");
        }
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
}

public static class NativeModelPosePackageReader
{
    public const int MaximumFileBytes = 8 * 1024 * 1024;
    public const double MaximumRepresentationError = 2e-6;
    public static async Task<NativeModelPosePackageLease> ReadAsync(string path, AssetMemoryBudget budget,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); ArgumentNullException.ThrowIfNull(budget);
        token.ThrowIfCancellationRequested();
        FileStream? file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        IDisposable? reservation = null;
        try
        {
            if (file.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException("PoseFileLimit");
            reservation = budget.Reserve(checked(file.Length * 8 + 64 * 1024), "imported pose package");
            var bytes = new byte[checked((int)file.Length)];
            await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            var package = JsonSerializer.Deserialize<NativeModelPosePackage>(bytes, new JsonSerializerOptions { MaxDepth = 32 })
                ?? throw new InvalidDataException("PoseFileInvalid");
            Validate(package, budget, token);
            package = package with
            {
                Joints = Array.AsReadOnly(package.Joints.Select(joint => joint with
                {
                    BindWorld = new(Array.AsReadOnly(joint.BindWorld.Elements.ToArray())),
                    PoseLocal = joint.PoseLocal with { Elements = Array.AsReadOnly(joint.PoseLocal.Elements.ToArray()) }
                }).ToArray()),
                MeshBindWorld = new(Array.AsReadOnly(package.MeshBindWorld.Elements.ToArray())),
                MaterialWorldTransform = package.MaterialWorldTransform is { } materialWorld
                    ? materialWorld with { Elements = Array.AsReadOnly(materialWorld.Elements.ToArray()) } : null,
                JointOrdinalsByMatrixRow = new System.Collections.ObjectModel.ReadOnlyDictionary<uint,int>(
                    package.JointOrdinalsByMatrixRow.ToDictionary(pair => pair.Key, pair => pair.Value))
            };
            var result = new NativeModelPosePackageLease(package, file, reservation, budget,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            file = null; reservation = null;
            return result;
        }
        finally { file?.Dispose(); reservation?.Dispose(); }
    }

    private static void Validate(NativeModelPosePackage package, AssetMemoryBudget budget, CancellationToken token)
    {
        if (package.Version != 1 || package.SerializedLodOrdinal < 0 || package.PoseSource is null ||
            package.MatrixRows.IsEmpty || package.Joints is not { Count: > 0 and <= 8192 } ||
            package.MeshBindWorld?.Elements is not { Count: 16 } || package.JointOrdinalsByMatrixRow is null ||
            package.JointOrdinalsByMatrixRow.Count > 8192)
            throw new InvalidDataException("PoseFileInvalid");
        using var rows = new NativeSkinningPoseRowsReader().Read(package.PoseSource, package.MatrixRows.Span,
            package.PoseSource.MeshIdentity, package.PoseSource.RigIdentity, package.PoseSource.Revision,
            package.PoseSource.SnapshotSequence, package.Joints.Count, budget.Reserve, token: token);
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < package.Joints.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var joint = package.Joints[i];
            if (joint is null || string.IsNullOrWhiteSpace(joint.Key) || !byKey.TryAdd(joint.Key, i) ||
                joint.BindWorld?.Elements is not { Count: 16 } || joint.BindWorld.Elements.Any(value => !double.IsFinite(value)) ||
                joint.PoseLocal?.Elements is not { Count: 16 } || joint.PoseLocal.Elements.Any(value => !float.IsFinite(value)) ||
                !Enum.IsDefined(joint.PoseLocal.Layout) || !Enum.IsDefined(joint.PoseLocal.VectorSide) ||
                joint.PoseLocal.Layout == MatrixLayout.Unknown || joint.PoseLocal.VectorSide == VectorMultiplicationSide.Unknown)
                throw new InvalidDataException("PoseJointInvalid");
            var e = joint.PoseLocal.Elements;
            if (joint.PoseLocal.Layout != MatrixLayout.RowMajor || joint.PoseLocal.VectorSide != VectorMultiplicationSide.Row ||
                e[3] != 0 || e[7] != 0 || e[11] != 0 || e[15] != 1)
                throw new InvalidDataException("PoseTransformConventionUnsupported");
        }
        var parents = new int[package.Joints.Count];
        for (var i = 0; i < parents.Length; i++)
        {
            var parent = package.Joints[i].ParentKey;
            if (parent is null) parents[i] = -1;
            else if (!byKey.TryGetValue(parent, out parents[i])) throw new InvalidDataException("PoseJointParentInvalid");
        }
        var states = new byte[parents.Length];
        for (var i = 0; i < parents.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var p = i;
            while (p >= 0 && states[p] == 0) { states[p] = 1; p = parents[p]; }
            if (p >= 0 && states[p] == 1) throw new InvalidDataException("PoseJointParentInvalid");
            p = i; while (p >= 0 && states[p] == 1) { states[p] = 2; p = parents[p]; }
        }
        if (package.JointOrdinalsByMatrixRow.Count != package.Joints.Count)
            throw new InvalidDataException("PoseJointMappingInvalid");
        var targets = new HashSet<int>();
        foreach (var (row, joint) in package.JointOrdinalsByMatrixRow)
            if (row % 3 != 0 || row > rows.RowCount - 3 || joint < 0 || joint >= package.Joints.Count || !targets.Add(joint))
                throw new InvalidDataException("PoseJointMappingInvalid");
        if (package.MeshBindWorld.Elements.Any(value => !double.IsFinite(value))) throw new InvalidDataException("PoseBindInvalid");
        if (package.MaterialWorldTransform is { } materialWorld)
        {
            var e = materialWorld.Elements;
            if (e is not { Count: 16 } || e.Any(value => !float.IsFinite(value)) ||
                materialWorld.Layout != MatrixLayout.RowMajor || materialWorld.VectorSide != VectorMultiplicationSide.Row ||
                e[3] != 0 || e[7] != 0 || e[11] != 0 || e[15] != 1)
                throw new InvalidDataException("PoseMaterialWorldInvalid");
        }
        ValidateNumericRepresentation(package, parents, token);
    }

    private static void ValidateNumericRepresentation(NativeModelPosePackage package, int[] parents, CancellationToken token)
    {
        var globals = BuildWorldTransforms(package, parents, token);
        foreach (var (row, joint) in package.JointOrdinalsByMatrixRow)
        {
            token.ThrowIfCancellationRequested();
            FbxBindMatrix offset;
            try { offset = FbxGeometryExporter.CalculateMeshToJointBind(package.MeshBindWorld, package.Joints[joint].BindWorld); }
            catch (FbxGeometryExportException error) { throw new InvalidDataException("PoseBindInvalid", error); }
            var matrix = Multiply(offset.Elements, globals[joint]);
            for (var column = 0; column < 3; column++)
                for (var component = 0; component < 4; component++)
                {
                    var at = checked(((int)row + column) * 16 + component * 4);
                    var stored = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(package.MatrixRows.Span.Slice(at, 4)));
                    var value = matrix[component * 4 + column];
                    if (!double.IsFinite(value) || Math.Abs(stored - value) > MaximumRepresentationError)
                        throw new InvalidDataException("PoseRepresentationMismatch");
                }
        }
    }

    internal static Vector3[]? CreateMaterialWorldPositions(NativeModelPosePackage package, CancellationToken token)
    {
        if (package.MaterialWorldTransform is not { } transform) return null;
        var keys = package.Joints.Select((joint, i) => (joint.Key, i)).ToDictionary(item => item.Key, item => item.i);
        var parents = package.Joints.Select(joint => joint.ParentKey is null ? -1 : keys[joint.ParentKey]).ToArray();
        var globals = BuildWorldTransforms(package, parents, token);
        var modelWorld = transform.Elements.Select(value => (double)value).ToArray();
        var positions = new Vector3[globals.Length];
        foreach (var (row, joint) in package.JointOrdinalsByMatrixRow)
        {
            token.ThrowIfCancellationRequested();
            var world = Multiply(globals[joint], modelWorld);
            var position = new Vector3((float)world[12], (float)world[13], (float)world[14]);
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                throw new InvalidDataException("PoseMaterialWorldInvalid");
            positions[checked((int)(row / 3))] = position;
        }
        return positions;
    }

    private static double[][] BuildWorldTransforms(NativeModelPosePackage package, int[] parents, CancellationToken token)
    {
        var globals = new double[parents.Length][];
        var children = Enumerable.Range(0, parents.Length).Select(_ => new List<int>()).ToArray();
        var ready = new Queue<int>();
        for (var i = 0; i < parents.Length; i++)
            if (parents[i] < 0) ready.Enqueue(i); else children[parents[i]].Add(i);
        while (ready.TryDequeue(out var i))
        {
            token.ThrowIfCancellationRequested();
            var transform = package.Joints[i].PoseLocal;
            if (transform.Layout != MatrixLayout.RowMajor || transform.VectorSide != VectorMultiplicationSide.Row)
                throw new InvalidDataException("PoseTransformConventionUnsupported");
            var local = transform.Elements.Select(value => (double)value).ToArray();
            globals[i] = parents[i] < 0 ? local : Multiply(local, globals[parents[i]]);
            foreach (var child in children[i]) ready.Enqueue(child);
        }
        return globals;
    }

    private static double[] Multiply(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var result = new double[16];
        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 4; column++)
                for (var k = 0; k < 4; k++) result[row * 4 + column] += left[row * 4 + k] * right[k * 4 + column];
        return result;
    }
}
