using System.Numerics;
using Deviant.Assets;
using Deviant.Export.Models;
using Xunit;

namespace Deviant.Model.Tests;

public sealed partial class FbxGeometryExporterTests
{
    [Theory]
    [InlineData(FbxMediaPackaging.Sidecars)]
    [InlineData(FbxMediaPackaging.Embedded)]
    public async Task PreservesSourceNumbersWithoutAssigningUnknownAxesOrUnits(FbxMediaPackaging packaging)
    {
        var root = NewRoot(); var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        try
        {
            var asset = CreateSkinAsset() with { Coordinates = new("source-numeric", "1",
                MatrixLayout: MatrixLayout.RowMajor, VectorSide: VectorMultiplicationSide.Row) };
            var decoded = CreateDecoded(asset);
            var output = Path.Combine(root, "numbers.fbx");
            var report = await new FbxGeometryExporter().ExportAsync(new(decoded, new(decoded.Identity, "detail"), Reserve,
                Skeleton: CreateExportSkeleton(decoded.Revision), CoordinateMetadata: FbxCoordinateMetadataPolicy.PreserveSourceNumbers),
                source, output, new(MediaPackaging: packaging));
            Assert.Equal(FbxCoordinateMetadataPolicy.PreserveSourceNumbers, report.CoordinateMetadata);
            Assert.Contains(report.Losses, loss => loss.Code == "CoordinateMetadataOmitted");
            if (packaging == FbxMediaPackaging.Sidecars)
            {
                var text = await File.ReadAllTextAsync(output);
                Assert.DoesNotContain("UnitScaleFactor", text);
                Assert.DoesNotContain("P: \"UpAxis\"", text);
                Assert.DoesNotContain("DeviantMetersPerUnit", text);
                Assert.Matches(@"Vertices: \*9 \{ a:\s*0,0,0,1,0,0,0,1,0", text);
            }
            else
            {
                var doc = ParseBinary7400(await File.ReadAllBytesAsync(output));
                var props = doc.Roots.Single(n => n.Name == "GlobalSettings").Children.Single(n => n.Name == "Properties70").Children;
                Assert.DoesNotContain(props, n => ReadString(n.Properties[0]) is "UnitScaleFactor" or "UpAxis" or "DeviantMetersPerUnit");
                var vertices = doc.Roots.Single(n => n.Name == "Objects").Children.Single(n => n.Name == "Geometry").Children.Single(n => n.Name == "Vertices");
                Assert.Equal(new double[] { 0,0,0,1,0,0,0,1,0 }, ReadDoubles(vertices.Properties.Single()));
            }
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData(FbxMediaPackaging.Sidecars)]
    [InlineData(FbxMediaPackaging.Embedded)]
    public async Task WritesJointHierarchyBindPoseAndMergedControlPointInfluences(FbxMediaPackaging packaging)
    {
        var root = NewRoot();
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        try
        {
            var decoded = CreateDecoded(CreateSkinAsset());
            var skeleton = CreateExportSkeleton(decoded.Revision);
            var output = Path.Combine(root, "skin.fbx");
            var report = await new FbxGeometryExporter().ExportAsync(
                new(decoded, new(decoded.Identity, "detail"), Reserve, Skeleton: skeleton), source, output,
                new(MediaPackaging: packaging));
            Assert.Equal(4, report.NodeCount); // mesh owner, mesh, and both joints
            if (packaging == FbxMediaPackaging.Sidecars)
            {
                var text = await File.ReadAllTextAsync(output);
                Assert.Contains("\"LimbNode\"", text);
                Assert.Contains("\"BindPose\"", text);
                Assert.Contains("\"Skin\"", text);
                Assert.Contains("\"Cluster\"", text);
                Assert.Matches(@"Weights: \*3 \{ a:\s*0.25,0.5,1", text);
                Assert.Contains("Joint_1", text);
            }
            else
            {
                var doc = ParseBinary7400(await File.ReadAllBytesAsync(output));
                var objects = doc.Roots.Single(n => n.Name == "Objects").Children;
                Assert.Equal(2, objects.Count(n => n.Name == "Model" && ReadString(n.Properties[2]) == "LimbNode"));
                Assert.Single(objects, n => n.Name == "Pose");
                var clusters = objects.Where(n => n.Name == "Deformer" && ReadString(n.Properties[2]) == "Cluster").ToArray();
                Assert.Equal(2, clusters.Length);
                Assert.Equal(new double[] { .25, .5, 1 }, ReadDoubles(clusters[0].Children.Single(n => n.Name == "Weights").Properties.Single()));
                Assert.Equal(BindElements(Matrix4x4.CreateTranslation(0, 1, 0)),
                    ReadDoubles(clusters[0].Children.Single(n => n.Name == "TransformLink").Properties.Single()));
                Assert.Equal(BindElements(Matrix4x4.CreateTranslation(0, -1, 0)),
                    ReadDoubles(clusters[0].Children.Single(n => n.Name == "Transform").Properties.Single()));
            }
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData("cycle", "SkeletonHierarchyInvalid")]
    [InlineData("singular", "BindMatrixInvalid")]
    [InlineData("ordinal", "SkinInfluenceInvalid")]
    [InlineData("empty", "SkinInfluenceInvalid")]
    [InlineData("instanced", "SkinInstanceAmbiguous")]
    public async Task RejectsInvalidSkinBeforeCreatingOutput(string kind, string code)
    {
        var root = NewRoot();
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        try
        {
            var asset = CreateSkinAsset();
            if (kind == "instanced") asset = asset with { Nodes = [.. asset.Nodes, new("other", null, Ref("other"), ["mesh"])] };
            var decoded = CreateDecoded(asset);
            var skeleton = CreateExportSkeleton(decoded.Revision);
            if (kind == "cycle") skeleton = skeleton with { Joints = [skeleton.Joints[0] with { ParentKey = "child" }, skeleton.Joints[1]] };
            if (kind == "singular") skeleton = skeleton with { Joints = [skeleton.Joints[0] with { BindWorld = new(new double[16]) }, skeleton.Joints[1]] };
            if (kind is "ordinal" or "empty") skeleton = skeleton with { Skins = [skeleton.Skins[0] with {
                VertexInfluences = [kind == "ordinal" ? new FbxJointInfluence[] { new(2, 1) } : Array.Empty<FbxJointInfluence>(), [new(0, 1)], [new(0, 1)]] }] };
            var output = Path.Combine(root, "invalid.fbx");
            var error = await Assert.ThrowsAsync<FbxGeometryExportException>(() => new FbxGeometryExporter().ExportAsync(
                new(decoded, new(decoded.Identity, "detail"), Reserve, Skeleton: skeleton), source, output));
            Assert.Equal(code, error.Code);
            Assert.False(File.Exists(output));
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SkinRevisionChangeCancelsPublicationAndReleasesReservation(int changedAt)
    {
        var root = NewRoot(); var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        var counter = new ReservationCounter(); int validations = 0;
        try
        {
            var decoded = CreateDecoded(CreateSkinAsset());
            var skeleton = CreateExportSkeleton(decoded.Revision) with { ValidateSourceRevision = _ =>
                ++validations < changedAt ? Task.CompletedTask : Task.FromException(new IOException("changed")) };
            var output = Path.Combine(root, "changed.fbx");
            await Assert.ThrowsAsync<IOException>(() => new FbxGeometryExporter().ExportAsync(
                new(decoded, new(decoded.Identity, "detail"), counter.Reserve, Skeleton: skeleton), source, output));
            Assert.False(File.Exists(output));
            Assert.Equal(0, counter.ActiveReservations);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task BindMatricesRemainCoherentWhenCallerArraysChangeAfterPlanning()
    {
        var root = NewRoot(); var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        try
        {
            var decoded = CreateDecoded(CreateSkinAsset());
            var skeleton = CreateExportSkeleton(decoded.Revision);
            skeleton = skeleton with { ValidateSourceRevision = _ =>
            {
                ((double[])skeleton.Joints[0].BindWorld.Elements)[13] = 40;
                ((double[])skeleton.Skins[0].MeshBindWorld.Elements)[12] = 80;
                return Task.CompletedTask;
            }};
            var output = Path.Combine(root, "snapshot.fbx");
            await new FbxGeometryExporter().ExportAsync(
                new(decoded, new(decoded.Identity, "detail"), Reserve, Skeleton: skeleton), source, output,
                new(MediaPackaging: FbxMediaPackaging.Embedded));
            var objects = ParseBinary7400(await File.ReadAllBytesAsync(output)).Roots.Single(n => n.Name == "Objects").Children;
            var cluster = objects.First(n => n.Name == "Deformer" && ReadString(n.Properties[2]) == "Cluster");
            Assert.Equal(BindElements(Matrix4x4.CreateTranslation(0, 1, 0)), ReadDoubles(cluster.Children.Single(n => n.Name == "TransformLink").Properties.Single()));
            Assert.Equal(BindElements(Matrix4x4.CreateTranslation(0, -1, 0)), ReadDoubles(cluster.Children.Single(n => n.Name == "Transform").Properties.Single()));
            var meshPose = objects.Single(n => n.Name == "Pose").Children.First(n => n.Name == "PoseNode");
            Assert.Equal(BindElements(Matrix4x4.Identity), ReadDoubles(meshPose.Children.Single(n => n.Name == "Matrix").Properties.Single()));
        }
        finally { DeleteRoot(root); }
    }

    private static GeometryAsset CreateSkinAsset() => new(CreateProfile(),
        [new("mesh-owner", null, Ref("owner"), ["mesh"])], [CreatePrimitive()], new Dictionary<string, string>());

    [Fact]
    public async Task CancellationDuringLargeInfluenceRowReleasesThePlan()
    {
        var root = NewRoot(); var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        using var cancellation = new CancellationTokenSource(); var counter = new ReservationCounter();
        try
        {
            var decoded = CreateDecoded(CreateSkinAsset()); var skeleton = CreateExportSkeleton(decoded.Revision);
            skeleton = skeleton with { Skins = [skeleton.Skins[0] with { VertexInfluences =
                [new CancelingInfluenceRow(cancellation), [new(0,1)], [new(0,1)]] }] };
            var output = Path.Combine(root,"canceled.fbx");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FbxGeometryExporter().ExportAsync(
                new(decoded,new(decoded.Identity,"detail"),counter.Reserve,Skeleton:skeleton),source,output,cancellationToken:cancellation.Token));
            Assert.False(File.Exists(output)); Assert.Equal(0,counter.ActiveReservations);
        }
        finally { DeleteRoot(root); }
    }

    private sealed class CancelingInfluenceRow(CancellationTokenSource cancellation) : IReadOnlyList<FbxJointInfluence>
    {
        public int Count => 4096;
        public FbxJointInfluence this[int index]
        {
            get { if (index == 4) cancellation.Cancel(); return new(0, 1); }
        }
        public IEnumerator<FbxJointInfluence> GetEnumerator() { for (var i=0;i<Count;i++) yield return this[i]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static FbxSkeletonExportInput CreateExportSkeleton(SourceRevision revision) => new(
        new(revision.IndexContentId, 2), revision,
        [new("root", null, "Root", new(BindElements(Matrix4x4.CreateTranslation(0, 1, 0))),
            ToTransform(Matrix4x4.CreateRotationZ(MathF.PI / 2))),
         new("child", "root", null, new(BindElements(Matrix4x4.CreateTranslation(2, 1, 0))),
            ToTransform(Matrix4x4.CreateRotationX(MathF.PI / 4) * Matrix4x4.CreateTranslation(2, 0, 0)))],
        [new("mesh", new(BindElements(Matrix4x4.Identity)),
            [[new(0, .1), new(0, .15), new(1, .75), new(1, 0)], [new(0, .5), new(1, .5)], [new(0, 1)]])],
        _ => Task.CompletedTask);

    private static double[] BindElements(Matrix4x4 m) =>
        [m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
}
