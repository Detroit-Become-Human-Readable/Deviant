using Deviant.Assets;

namespace Deviant.Export.Models;

public sealed partial class FbxGeometryExporter
{
    private sealed record FbxClusterPlan(long Id, long JointId, string Name, FbxBindMatrix BindWorld, FbxBindMatrix MeshToJoint,
        List<int> Vertices, List<double> Weights);
    private sealed record FbxSkinPlan(long Id, long PoseId, long GeometryId, long MeshModelId,
        string Name, FbxBindMatrix MeshBindWorld, List<FbxClusterPlan> Clusters);

    private static long EstimateSkinPlanningBytes(FbxSkeletonExportInput? skeleton,
        FbxGeometryExportOptions options, CancellationToken token)
    {
        if (skeleton is null) return 0;
        ArgumentNullException.ThrowIfNull(skeleton.Joints);
        ArgumentNullException.ThrowIfNull(skeleton.Skins);
        ArgumentNullException.ThrowIfNull(skeleton.ValidateSourceRevision);
        if (skeleton.Joints.Count == 0 || skeleton.Skins.Count == 0 || skeleton.Skins.Count > options.MaximumNodes)
            throw SkinError("SkeletonHierarchyInvalid", "skeleton", "A skin export requires joints and bounded mesh bindings.");
        long count = 0, vertices = 0;
        foreach (var skin in skeleton.Skins)
        {
            token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(skin);
            ArgumentNullException.ThrowIfNull(skin.VertexInfluences);
            vertices = checked(vertices + skin.VertexInfluences.Count);
            if (vertices > options.MaximumVertices)
                throw SkinError("VertexLimit", skin.PrimitiveKey, "Skin control-point rows exceed the vertex limit.");
            foreach (var row in skin.VertexInfluences)
            {
                token.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(row);
                count = checked(count + row.Count);
                if (count > options.MaximumSkinInfluences)
                    throw SkinError("SkinInfluenceLimit", skin.PrimitiveKey, "Stored influences exceed the configured limit.");
            }
        }
        // Includes per-joint cluster metadata for each primitive, merged rows and binary AST nodes.
        return checked(skeleton.Joints.Count * 4096L + skeleton.Skins.Count * 2048L +
            (long)skeleton.Joints.Count * skeleton.Skins.Count * 2048L + vertices * 128L + count * 96L);
    }

    private static FbxPlan AddSkinPlan(FbxPlan plan, FbxSkeletonExportInput? skeleton,
        GeometryAsset asset, CancellationToken token)
    {
        if (skeleton is null) return plan;
        if (skeleton.Identity.IndexContentId != skeleton.Revision.IndexContentId)
            throw SkinError("SkeletonIdentityMismatch", "skeleton", "The skeleton identity and revision belong to different indexes.");
        IReadOnlyList<FbxJointExportInput> joints = skeleton.Joints.Select(joint =>
            joint with { BindWorld = SnapshotBind(joint.BindWorld, joint.Key) }).ToArray();
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < joints.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var joint = joints[i];
            ArgumentNullException.ThrowIfNull(joint);
            if (string.IsNullOrWhiteSpace(joint.Key) || !byKey.TryAdd(joint.Key, i))
                throw SkinError("SkeletonHierarchyInvalid", "skeleton", "Joint keys must be nonempty and unique.");
            ValidateBindMatrix(joint.BindWorld, joint.Key);
        }
        var parents = new int[joints.Count];
        for (var i = 0; i < joints.Count; i++)
        {
            var parent = joints[i].ParentKey;
            if (parent is null) parents[i] = -1;
            else if (!byKey.TryGetValue(parent, out parents[i]) || parents[i] == i)
                throw SkinError("SkeletonHierarchyInvalid", joints[i].Key, "The joint parent is missing or refers to itself.");
        }
        var state = new byte[joints.Count];
        for (var i = 0; i < joints.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var p = i;
            while (p >= 0 && state[p] == 0) { state[p] = 1; p = parents[p]; }
            if (p >= 0 && state[p] == 1)
                throw SkinError("SkeletonHierarchyInvalid", joints[i].Key, "The joint hierarchy contains a cycle.");
            p = i;
            while (p >= 0 && state[p] == 1) { state[p] = 2; p = parents[p]; }
        }
        long nextId = plan.NextId;
        var ids = new long[joints.Count];
        for (var i = 0; i < ids.Length; i++) ids[i] = nextId++;
        for (var i = 0; i < joints.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var name = string.IsNullOrWhiteSpace(joints[i].Name) ? $"Joint_{i}" : joints[i].Name!;
            if (name.Any(char.IsControl) || System.Text.Encoding.UTF8.GetByteCount(name) > 1024)
                throw SkinError("JointNameInvalid", joints[i].Key, "Joint names must contain printable text within 1024 UTF-8 bytes.");
            plan.Models.Add(new(ids[i], name, "LimbNode", null, parents[i] < 0 ? 0 : ids[parents[i]],
                ConvertTransform(joints[i].PoseLocal, asset.Coordinates, joints[i].Key), null, null));
        }
        var primitiveKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skin in skeleton.Skins)
        {
            token.ThrowIfCancellationRequested();
            if (!primitiveKeys.Add(skin.PrimitiveKey) || !plan.Geometries.TryGetValue(skin.PrimitiveKey, out var geometry))
                throw SkinError("SkinPrimitiveInvalid", skin.PrimitiveKey, "Each skin must select one existing primitive exactly once.");
            if (geometry.Primitive.Skin is { } sourceSkin && sourceSkin.Skeleton != skeleton.Identity)
                throw SkinError("SkeletonIdentityMismatch", skin.PrimitiveKey, "The caller skeleton differs from the primitive skin identity.");
            ValidateBindMatrix(skin.MeshBindWorld, skin.PrimitiveKey);
            var meshBind = SnapshotBind(skin.MeshBindWorld, skin.PrimitiveKey);
            var meshModels = plan.Models.Where(m => m.Primitive == geometry).ToArray();
            if (meshModels.Length != 1)
                throw SkinError("SkinInstanceAmbiguous", skin.PrimitiveKey, "A skinned primitive requires a single model instance and bind context.");
            if (skin.VertexInfluences.Count != geometry.Primitive.Positions.Count)
                throw SkinError("SkinInfluenceInvalid", skin.PrimitiveKey, "Influence rows must match the primitive control-point count.");
            var skinId = nextId++; var poseId = nextId++;
            var clusters = joints.Select((joint, i) => new FbxClusterPlan(nextId++, ids[i], $"{skin.PrimitiveKey}/{i}",
                joint.BindWorld, CalculateMeshToJointBind(meshBind, joint.BindWorld), [], [])).ToList();
            for (var v = 0; v < skin.VertexInfluences.Count; v++)
            {
                token.ThrowIfCancellationRequested();
                var merged = new SortedDictionary<int, double>();
                double sum = 0;
                var sourceRow = skin.VertexInfluences[v];
                for (var lane = 0; lane < sourceRow.Count; lane++)
                {
                    if ((lane & 1023) == 0) token.ThrowIfCancellationRequested();
                    var influence = sourceRow[lane];
                    if (influence is null || influence.JointOrdinal < 0 || influence.JointOrdinal >= joints.Count ||
                        !double.IsFinite(influence.Weight) || influence.Weight < 0)
                        throw SkinError("SkinInfluenceInvalid", skin.PrimitiveKey, "Influences require valid joint ordinals and finite nonnegative weights.");
                    if (influence.Weight == 0) continue;
                    merged.TryGetValue(influence.JointOrdinal, out var old);
                    merged[influence.JointOrdinal] = old + influence.Weight;
                    sum += influence.Weight;
                }
                if (!double.IsFinite(sum) || sum <= 0)
                    throw SkinError("SkinInfluenceInvalid", skin.PrimitiveKey, "Every control point requires a positive finite weight sum.");
                foreach (var (joint, weight) in merged)
                {
                    var normalized = weight / sum;
                    if (normalized <= 0 || !double.IsFinite(normalized))
                        throw SkinError("SkinInfluenceInvalid", skin.PrimitiveKey, "A positive influence cannot be represented after normalization.");
                    clusters[joint].Vertices.Add(v); clusters[joint].Weights.Add(normalized);
                }
            }
            plan.Skins.Add(new(skinId, poseId, geometry.GeometryId, meshModels[0].Id,
                skin.PrimitiveKey, meshBind, clusters));
        }
        foreach (var primitive in asset.Primitives)
            if (primitive.Skin is not null && !primitiveKeys.Contains(primitive.Key))
                throw SkinError("SkinPrimitiveInvalid", primitive.Key, "A primitive skin has no caller-qualified bind context.");
        return plan with { NextId = nextId };
    }

    private static void ValidateBindMatrix(FbxBindMatrix matrix, string subject)
    {
        if (matrix?.Elements is not { Count: 16 } e || e.Any(value => !double.IsFinite(value)) ||
            Math.Abs(e[3]) > 1e-9 || Math.Abs(e[7]) > 1e-9 || Math.Abs(e[11]) > 1e-9 || Math.Abs(e[15] - 1) > 1e-9)
            throw SkinError("BindMatrixInvalid", subject, "Bind matrices require sixteen finite affine row-vector elements.");
        var determinant = e[0] * (e[5] * e[10] - e[6] * e[9]) -
            e[1] * (e[4] * e[10] - e[6] * e[8]) + e[2] * (e[4] * e[9] - e[5] * e[8]);
        if (!double.IsFinite(determinant) || determinant == 0)
            throw SkinError("BindMatrixInvalid", subject, "Bind matrices must be invertible.");
    }

    private static FbxBindMatrix SnapshotBind(FbxBindMatrix matrix, string subject)
    {
        ValidateBindMatrix(matrix, subject);
        var snapshot = new FbxBindMatrix(matrix.Elements.ToArray());
        ValidateBindMatrix(snapshot, subject);
        return snapshot;
    }

    private static IEnumerable<(long Child, long Parent)> SkinConnections(FbxSkinPlan skin)
    {
        yield return (skin.Id, skin.GeometryId);
        foreach (var cluster in skin.Clusters)
        {
            yield return (cluster.Id, skin.Id);
            yield return (cluster.JointId, cluster.Id);
        }
    }

    private static IEnumerable<FbxBinary7400Node> BuildBinarySkin(FbxSkinPlan skin, CancellationToken token)
    {
        yield return new("Deformer", [FbxBinary7400Property.Int64(skin.Id),
            FbxBinary7400Property.String($"{skin.Name}\0\u0001Deformer"), FbxBinary7400Property.String("Skin")],
            [new("Version", [FbxBinary7400Property.Int32(101)]), new("Link_DeformAcuracy", [FbxBinary7400Property.Double(50)]),
                new("SkinningType", [FbxBinary7400Property.String("Linear")])]);
        foreach (var cluster in skin.Clusters)
        {
            token.ThrowIfCancellationRequested();
            yield return new("Deformer", [FbxBinary7400Property.Int64(cluster.Id),
                FbxBinary7400Property.String($"{cluster.Name}\0\u0001Deformer"), FbxBinary7400Property.String("Cluster")],
                [new("Version", [FbxBinary7400Property.Int32(100)]), new("UserData", [FbxBinary7400Property.String(""), FbxBinary7400Property.String("")]),
                    new("Mode", [FbxBinary7400Property.String("Normalize")]),
                    new("Indexes", [Int32Array(cluster.Vertices.Count, i => cluster.Vertices[i])]),
                    new("Weights", [DoubleArray(cluster.Weights.Count, i => cluster.Weights[i])]),
                    new("Transform", [DoubleArray(16, i => cluster.MeshToJoint.Elements[i])]),
                    new("TransformLink", [DoubleArray(16, i => cluster.BindWorld.Elements[i])])]);
        }
        yield return new("Pose", [FbxBinary7400Property.Int64(skin.PoseId),
            FbxBinary7400Property.String($"{skin.Name}\0\u0001Pose"), FbxBinary7400Property.String("BindPose")],
            [new("Type", [FbxBinary7400Property.String("BindPose")]), new("Version", [FbxBinary7400Property.Int32(100)]),
                new("NbPoseNodes", [FbxBinary7400Property.Int32(skin.Clusters.Count + 1)]),
                PoseNode(skin.MeshModelId, skin.MeshBindWorld), .. skin.Clusters.Select(c => PoseNode(c.JointId, c.BindWorld))]);
    }

    private static FbxBinary7400Node PoseNode(long id, FbxBindMatrix bind) => new("PoseNode", children:
        [new("Node", [FbxBinary7400Property.Int64(id)]), new("Matrix", [DoubleArray(16, i => bind.Elements[i])])]);

    private static void WriteSkin(TextWriter writer, FbxSkinPlan skin, CancellationToken token)
    {
        writer.WriteLine($"\tDeformer: {skin.Id}, \"Deformer::{Escape(skin.Name)}\", \"Skin\" {{");
        writer.WriteLine("\t\tVersion: 101\n\t\tLink_DeformAcuracy: 50\n\t\tSkinningType: \"Linear\"\n\t}");
        foreach (var cluster in skin.Clusters)
        {
            token.ThrowIfCancellationRequested();
            writer.WriteLine($"\tDeformer: {cluster.Id}, \"SubDeformer::{Escape(cluster.Name)}\", \"Cluster\" {{");
            writer.WriteLine("\t\tVersion: 100\n\t\tUserData: \"\",\"\"\n\t\tMode: \"Normalize\"");
            WriteSkinArray(writer, "Indexes", cluster.Vertices.Count, i => cluster.Vertices[i].ToString(System.Globalization.CultureInfo.InvariantCulture), token);
            WriteSkinArray(writer, "Weights", cluster.Weights.Count, i => Format(cluster.Weights[i]), token);
            WriteSkinArray(writer, "Transform", 16, i => Format(cluster.MeshToJoint.Elements[i]), token);
            WriteSkinArray(writer, "TransformLink", 16, i => Format(cluster.BindWorld.Elements[i]), token);
            writer.WriteLine("\t}");
        }
        writer.WriteLine($"\tPose: {skin.PoseId}, \"Pose::{Escape(skin.Name)}\", \"BindPose\" {{");
        writer.WriteLine($"\t\tType: \"BindPose\"\n\t\tVersion: 100\n\t\tNbPoseNodes: {skin.Clusters.Count + 1}");
        WritePoseNode(writer, skin.MeshModelId, skin.MeshBindWorld, token);
        foreach (var cluster in skin.Clusters) WritePoseNode(writer, cluster.JointId, cluster.BindWorld, token);
        writer.WriteLine("\t}");
    }

    private static void WritePoseNode(TextWriter writer, long id, FbxBindMatrix bind, CancellationToken token)
    {
        writer.WriteLine($"\t\tPoseNode: {{ Node: {id}");
        WriteSkinArray(writer, "Matrix", 16, i => Format(bind.Elements[i]), token);
        writer.WriteLine("\t\t}");
    }

    private static void WriteSkinArray(TextWriter writer, string name, int count, Func<int, string> value, CancellationToken token)
    {
        writer.Write($"\t\t{name}: *{count} {{ a: ");
        for (var i = 0; i < count; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            if (i != 0) writer.Write(',');
            writer.Write(value(i));
        }
        writer.WriteLine(" }");
    }

    private static FbxGeometryExportException SkinError(string code, string subject, string detail) =>
        Error(code, detail, [new(code, subject, detail)]);

    public static FbxBindMatrix CalculateMeshToJointBind(FbxBindMatrix mesh, FbxBindMatrix joint)
    {
        ValidateBindMatrix(mesh, "mesh"); ValidateBindMatrix(joint, "joint");
        var a = joint.Elements;
        var determinant = a[0] * (a[5] * a[10] - a[6] * a[9]) -
            a[1] * (a[4] * a[10] - a[6] * a[8]) + a[2] * (a[4] * a[9] - a[5] * a[8]);
        var inverse = new double[16];
        inverse[0] = (a[5] * a[10] - a[6] * a[9]) / determinant;
        inverse[1] = (a[2] * a[9] - a[1] * a[10]) / determinant;
        inverse[2] = (a[1] * a[6] - a[2] * a[5]) / determinant;
        inverse[4] = (a[6] * a[8] - a[4] * a[10]) / determinant;
        inverse[5] = (a[0] * a[10] - a[2] * a[8]) / determinant;
        inverse[6] = (a[2] * a[4] - a[0] * a[6]) / determinant;
        inverse[8] = (a[4] * a[9] - a[5] * a[8]) / determinant;
        inverse[9] = (a[1] * a[8] - a[0] * a[9]) / determinant;
        inverse[10] = (a[0] * a[5] - a[1] * a[4]) / determinant;
        inverse[15] = 1;
        for (var col = 0; col < 3; col++)
            inverse[12 + col] = -(a[12] * inverse[col] + a[13] * inverse[4 + col] + a[14] * inverse[8 + col]);
        var result = new double[16];
        for (var row = 0; row < 4; row++)
            for (var col = 0; col < 4; col++)
                for (var k = 0; k < 4; k++) result[row * 4 + col] += mesh.Elements[row * 4 + k] * inverse[k * 4 + col];
        if (result.Any(value => !double.IsFinite(value)))
            throw SkinError("BindMatrixInvalid", "skeleton", "The mesh-to-joint bind matrix is not finite.");
        return new(result);
    }
}
