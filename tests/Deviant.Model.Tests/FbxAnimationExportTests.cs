using System.Buffers.Binary;
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
    public async Task AnimationUsesExactSecondsAndQualifiedJointConnections(FbxMediaPackaging packaging)
    {
        var root = NewRoot(); var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        try
        {
            var decoded = CreateDecoded(CreateSkinAsset()); var skeleton = CreateExportSkeleton(decoded.Revision);
            var animation = CreateAnimation(skeleton);
            var output = Path.Combine(root, "motion.fbx");
            await new FbxGeometryExporter().ExportAsync(new(decoded, new(decoded.Identity, "detail"), Reserve, Skeleton: skeleton)
                { Animation = animation }, source, output, new(MediaPackaging: packaging));
            if (packaging == FbxMediaPackaging.Sidecars)
            {
                var text = await File.ReadAllTextAsync(output);
                Assert.Contains("AnimStack::Motion", text);
                Assert.Contains("KeyTime: *3", text);
                Assert.Contains("0,11546539500,46186158000", text);
                Assert.Contains("\"Lcl Translation\"", text);
                Assert.Contains("\"d|X\"", text);
                Assert.Contains("KeyAttrFlags: *1 { a: 4 }", text);
                Assert.Contains("LocalTime: 0,46186158000", text);
            }
            else
            {
                var document = ParseBinary7400(await File.ReadAllBytesAsync(output));
                var objects = document.Roots.Single(node => node.Name == "Objects").Children;
                Assert.Single(objects, node => node.Name == "AnimationStack");
                Assert.Single(objects, node => node.Name == "AnimationLayer");
                Assert.Equal(3, objects.Count(node => node.Name == "AnimationCurveNode"));
                var curves = objects.Where(node => node.Name == "AnimationCurve").ToArray();
                Assert.Equal(9, curves.Length);
                var times = curves[0].Children.Single(node => node.Name == "KeyTime").Properties.Single();
                Assert.Equal((byte)'l', times.TypeCode);
                Assert.Equal(new long[] { 0, 11_546_539_500, 46_186_158_000 }, Enumerable.Range(0, 3)
                    .Select(i => BinaryPrimitives.ReadInt64LittleEndian(times.Payload.AsSpan(i * 8, 8))).ToArray());
                var values = curves[0].Children.Single(node => node.Name == "KeyValueFloat").Properties.Single();
                Assert.Equal(new float[] { 0, 2, 4 }, Enumerable.Range(0, 3)
                    .Select(i => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(values.Payload.AsSpan(i * 4, 4)))).ToArray());
                var joints = objects.Where(node => node.Name == "Model" && ReadString(node.Properties[2]) == "LimbNode").ToArray();
                var jointId = BinaryPrimitives.ReadInt64LittleEndian(joints[0].Properties[0].Payload);
                var connections = document.Roots.Single(node => node.Name == "Connections").Children;
                Assert.Equal(3, connections.Count(node => ReadString(node.Properties[0]) == "OP" &&
                    BinaryPrimitives.ReadInt64LittleEndian(node.Properties[2].Payload) == jointId));
                var definitions = document.Roots.Single(node => node.Name == "Definitions");
                var declared = BinaryPrimitives.ReadInt32LittleEndian(definitions.Children.Single(node => node.Name == "Count").Properties[0].Payload);
                Assert.Equal(objects.Count, declared);
            }
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData("unknown", "AnimationJointInvalid")]
    [InlineData("duplicate", "AnimationJointInvalid")]
    [InlineData("unordered", "AnimationTimeInvalid")]
    [InlineData("end", "AnimationTimeInvalid")]
    [InlineData("nonfinite", "TransformNonFinite")]
    [InlineData("revision", "AnimationIdentityMismatch")]
    [InlineData("skeleton", "AnimationIdentityMismatch")]
    [InlineData("limit", "AnimationKeyLimit")]
    public async Task MalformedAnimationsFailBeforePublication(string kind, string expected)
    {
        var root = NewRoot(); var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        var counter = new ReservationCounter();
        try
        {
            var decoded = CreateDecoded(CreateSkinAsset()); var skeleton = CreateExportSkeleton(decoded.Revision);
            var animation = CreateAnimation(skeleton);
            var track = animation.Tracks[0];
            switch (kind)
            {
                case "unknown": animation = animation with { Tracks = [track with { JointKey = "absent" }] }; break;
                case "duplicate": animation = animation with { Tracks = [track, track] }; break;
                case "unordered": animation = animation with { Tracks = [track with { Keys = [track.Keys[1], track.Keys[0], track.Keys[2]] }] }; break;
                case "end": animation = animation with { DurationSeconds = 2 }; break;
                case "nonfinite":
                    var matrix = Matrix4x4.Identity; matrix.M41 = float.NaN;
                    animation = animation with { Tracks = [track with { Keys = [track.Keys[0] with { LocalTransform = ToTransform(matrix) }, track.Keys[1], track.Keys[2]] }] }; break;
                case "revision": animation = animation with { Revision = new(skeleton.Revision.IndexContentId, "different") }; break;
                case "skeleton": animation = animation with { SkeletonIdentity = new(skeleton.Revision.IndexContentId, 42) }; break;
            }
            var output = Path.Combine(root, "invalid-motion.fbx");
            var error = await Assert.ThrowsAsync<FbxGeometryExportException>(() => new FbxGeometryExporter().ExportAsync(
                new(decoded, new(decoded.Identity, "detail"), counter.Reserve, Skeleton: skeleton) { Animation = animation },
                source, output, new(MaximumAnimationKeys: kind == "limit" ? 2 : 100)));
            Assert.Equal(expected, error.Code); Assert.False(File.Exists(output)); Assert.Equal(0, counter.ActiveReservations);
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AnimationRevisionValidationProtectsPublication(int changedAt)
    {
        var root = NewRoot(); var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        var counter = new ReservationCounter(); var validations = 0;
        try
        {
            var decoded = CreateDecoded(CreateSkinAsset()); var skeleton = CreateExportSkeleton(decoded.Revision);
            var animation = CreateAnimation(skeleton) with { ValidateSourceRevision = _ => ++validations < changedAt
                ? Task.CompletedTask : Task.FromException(new IOException("revision changed")) };
            var output = Path.Combine(root, "changed-motion.fbx");
            await Assert.ThrowsAsync<IOException>(() => new FbxGeometryExporter().ExportAsync(new(decoded,
                new(decoded.Identity, "detail"), counter.Reserve, Skeleton: skeleton) { Animation = animation }, source, output));
            Assert.False(File.Exists(output)); Assert.Equal(0, counter.ActiveReservations);
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task AnimationSnapshotsSamplesBeforeSourceValidationCallback()
    {
        var root = NewRoot(); var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        try
        {
            var decoded = CreateDecoded(CreateSkinAsset()); var skeleton = CreateExportSkeleton(decoded.Revision);
            var samples = CreateAnimation(skeleton).Tracks[0].Keys.ToArray();
            var animation = CreateAnimation(skeleton) with { Tracks = [new("root", samples)], ValidateSourceRevision = _ =>
            { samples[1] = samples[1] with { LocalTransform = ToTransform(Matrix4x4.CreateTranslation(99, 0, 0)) }; return Task.CompletedTask; } };
            var output = Path.Combine(root, "snapshot.fbx");
            await new FbxGeometryExporter().ExportAsync(new(decoded, new(decoded.Identity, "detail"), Reserve, Skeleton: skeleton)
                { Animation = animation }, source, output);
            Assert.Matches(@"KeyValueFloat: \*3 \{ a:\s*0,2,4", await File.ReadAllTextAsync(output));
        }
        finally { DeleteRoot(root); }
    }

    private static FbxAnimationExportInput CreateAnimation(FbxSkeletonExportInput skeleton) => new(
        new(skeleton.Revision.IndexContentId, 3), skeleton.Revision, "Motion", skeleton.Identity, skeleton.Revision, 1,
        [new("root", [new(0, ToTransform(Matrix4x4.Identity)), new(.25, ToTransform(Matrix4x4.CreateTranslation(2, 0, 0))),
            new(1, ToTransform(Matrix4x4.CreateTranslation(4, 0, 0)))])], _ => Task.CompletedTask);

    [Fact]
    public async Task CancelingAnimationValidationReleasesPlanningMemory()
    {
        var root = NewRoot(); var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        var counter = new ReservationCounter(); using var cancellation = new CancellationTokenSource();
        try
        {
            var decoded = CreateDecoded(CreateSkinAsset()); var skeleton = CreateExportSkeleton(decoded.Revision);
            var animation = CreateAnimation(skeleton) with { ValidateSourceRevision = token =>
            { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; } };
            var output = Path.Combine(root, "cancel-motion.fbx");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FbxGeometryExporter().ExportAsync(new(decoded,
                new(decoded.Identity, "detail"), counter.Reserve, Skeleton: skeleton) { Animation = animation },
                source, output, cancellationToken: cancellation.Token));
            Assert.False(File.Exists(output)); Assert.Equal(0, counter.ActiveReservations);
        }
        finally { DeleteRoot(root); }
    }
}
