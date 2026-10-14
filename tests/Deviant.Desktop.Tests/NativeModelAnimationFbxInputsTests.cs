using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Reflection;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Export.Models;
using Deviant.Formats.Animation;
using Deviant.Formats.Models;
using Deviant.Formats.Scenes;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class NativeModelAnimationFbxInputsTests
{
    [Fact]
    public async Task SourceOwnerMovementIsFoldedIntoRootsAndExplicitMappingAllowsReorderedJoints()
    {
        using var fixture = new Fixture(); var budget = new AssetMemoryBudget();
        using (var input = await NativeModelAnimationFbxInputs.CreateAsync(fixture.Binding, fixture.Skeleton,
            fixture.Mapping, "Motion", budget, _ => Task.CompletedTask))
        {
            Assert.Same(fixture.Skeleton, input.Skeleton);
            var root = input.Animation.Tracks.Single(track => track.JointKey == "rig-root");
            var child = input.Animation.Tracks.Single(track => track.JointKey == "rig-child");
            Assert.Equal(0, root.Keys[0].TimeSeconds);
            Assert.Equal((double)fixture.Binding.Timing.Duration, root.Keys[^1].TimeSeconds);
            Assert.InRange(root.Keys[^1].LocalTransform.Elements[12], 19.999f, 20.001f);
            Assert.Equal(0, child.Keys[^1].LocalTransform.Elements[12]);
            Assert.InRange(child.Keys[^1].LocalTransform.Elements[13], 1.999f, 2.001f);
            Assert.True(budget.UsedBytes > 0);
            Assert.Equal(fixture.Binding.ClipIdentity, input.Animation.Identity);
            Assert.Equal(fixture.Binding.Revision, input.Animation.Revision);
        }
        Assert.Equal(0, budget.UsedBytes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("parent")]
    [InlineData("revision")]
    public async Task InvalidJointJoinsFailAndReleaseMemory(string kind)
    {
        using var fixture = new Fixture(); var budget = new AssetMemoryBudget();
        IReadOnlyDictionary<int, string> mapping = fixture.Mapping; var skeleton = fixture.Skeleton;
        if (kind == "missing") mapping = new Dictionary<int, string> { [0] = "rig-root" };
        if (kind == "duplicate") mapping = new Dictionary<int, string> { [0] = "rig-root", [1] = "rig-root" };
        if (kind == "parent") skeleton = skeleton with { Joints = [skeleton.Joints[0] with { ParentKey = null }, skeleton.Joints[1]] };
        if (kind == "revision") skeleton = skeleton with { Revision = new(skeleton.Revision.IndexContentId, "changed") };
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeModelAnimationFbxInputs.CreateAsync(fixture.Binding,
            skeleton, mapping, "Motion", budget, _ => Task.CompletedTask));
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task RevisionChangeAfterSamplingDiscardsAllRetainedKeys()
    {
        using var fixture = new Fixture(); var budget = new AssetMemoryBudget(); int checks = 0;
        await Assert.ThrowsAsync<IOException>(() => NativeModelAnimationFbxInputs.CreateAsync(fixture.Binding, fixture.Skeleton,
            fixture.Mapping, "Motion", budget, _ => ++checks < 2 ? Task.CompletedTask : Task.FromException(new IOException("changed"))));
        Assert.Equal(2, checks); Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task KeyLimitAndCancellationReleaseMemory()
    {
        using var fixture = new Fixture(); var budget = new AssetMemoryBudget();
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeModelAnimationFbxInputs.CreateAsync(fixture.Binding,
            fixture.Skeleton, fixture.Mapping, "Motion", budget, _ => Task.CompletedTask, new(MaximumKeys: 4)));
        Assert.Equal(0, budget.UsedBytes);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeModelAnimationFbxInputs.CreateAsync(fixture.Binding,
            fixture.Skeleton, fixture.Mapping, "Motion", budget, _ => { cancellation.Cancel(); return Task.CompletedTask; }, token: cancellation.Token));
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public async Task DisposedSamplingInputReleasesMemoryAndRejectsReuse()
    {
        using var fixture = new Fixture(); var budget = new AssetMemoryBudget();
        var input = await NativeModelAnimationFbxInputs.CreateAsync(fixture.Binding, fixture.Skeleton,
            fixture.Mapping, "Motion", budget, _ => Task.CompletedTask);
        input.Dispose(); input.Dispose();
        Assert.Equal(0, budget.UsedBytes);
        Assert.Throws<ObjectDisposedException>(() => input.Animation);
        Assert.Throws<ObjectDisposedException>(() => input.Skeleton);
    }

    [Fact]
    public async Task CurvedQuaternionMotionIsRefinedAndCheckedAtFractionalTimes()
    {
        using var fixture = new Fixture(curved: true); var budget = new AssetMemoryBudget();
        using var input = await NativeModelAnimationFbxInputs.CreateAsync(fixture.Binding, fixture.Skeleton,
            fixture.Mapping, "Motion", budget, _ => Task.CompletedTask,
            new(MaximumSamplesPerSecond: 1920, MaximumRotationErrorRadians: .0002));
        var root = input.Animation.Tracks.Single(track => track.JointKey == "rig-root");
        Assert.True(root.Keys.Count > 14);
        // Independent interior XYZ reconstruction mirrors the public FBX interpolation contract.
        for (int i = 1; i < root.Keys.Count; i++)
        {
            var a = Euler(Matrix(root.Keys[i - 1].LocalTransform)); var b = Euler(Matrix(root.Keys[i].LocalTransform));
            var delta = new Vector3(Wrap(b.X-a.X), Wrap(b.Y-a.Y), Wrap(b.Z-a.Z));
            foreach (float fraction in new[] { .25f, .5f, .75f })
            {
                double seconds = root.Keys[i - 1].TimeSeconds + (root.Keys[i].TimeSeconds-root.Keys[i-1].TimeSeconds)*fraction;
                var expected = fixture.Binding.LocalPose((float)seconds)[0] * fixture.Binding.RootLocalAt((float)seconds);
                Matrix4x4.Decompose(expected, out _, out var q, out _);
                var angles = (a + delta*fraction) * (MathF.PI/180);
                var predicted = Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateRotationX(angles.X)*Matrix4x4.CreateRotationY(angles.Y)*Matrix4x4.CreateRotationZ(angles.Z));
                Assert.InRange(Error(q,predicted), 0, .000201);
            }
        }
    }

    [Fact]
    public void SkinnedFramePreservesUvIndicesAndCanRemoveOwnerMovement()
    {
        using var fixture = new Fixture(); var budget = new AssetMemoryBudget();
        var primitive = new MeshPrimitive("surface", PrimitiveTopology.Triangles,
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2], IndexElementFormat.UInt32, [],
            Normals: [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            Tangents: [new(1, 0, 0, 1), new(1, 0, 0, 1), new(1, 0, 0, 1)],
            TextureCoordinates: [[Vector2.Zero, Vector2.UnitX, Vector2.UnitY]]);
        var geometry = new GeometryAsset(new("generated", "1"), [], [primitive], new Dictionary<string, string>());
        var skin = new FbxSkinExportInput(primitive.Key, fixture.Skeleton.Joints[0].BindWorld,
            Enumerable.Range(0, 3).Select(_ => (IReadOnlyList<FbxJointInfluence>)[new(0, 1)]).ToArray());
        var skeleton = fixture.Skeleton with { Skins = [skin] };
        using (var frame = ModelAnimationFrame.Create(geometry, skeleton, fixture.Binding, fixture.Mapping, .2f, false, budget))
        {
            var posed = frame.Primitives[primitive.Key];
            Assert.Equal(new Vector3(20, 2, 0), posed.Positions[0]);
            Assert.Same(primitive.Indices, posed.Indices); Assert.Same(primitive.TextureCoordinates, posed.TextureCoordinates);
            Assert.Equal(primitive.Normals, posed.Normals); Assert.Equal(primitive.Tangents, posed.Tangents);
            Assert.Equal(Vector3.Zero, primitive.Positions[0]); Assert.True(budget.UsedBytes > 0);
        }
        Assert.Equal(0, budget.UsedBytes);
        using (var frame = ModelAnimationFrame.Create(geometry, skeleton, fixture.Binding, fixture.Mapping, .2f, true, budget))
            Assert.Equal(new Vector3(0, 2, 0), frame.Primitives[primitive.Key].Positions[0]);
        Assert.Equal(0, budget.UsedBytes);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ModelAnimationFrame.Create(geometry, skeleton, fixture.Binding,
            fixture.Mapping, 0, true, budget, cancellation.Token)); Assert.Equal(0, budget.UsedBytes);
        var invalid = skeleton with { Skins = [skin with { VertexInfluences = [[new(20, 1)], [new(0, 1)], [new(0, 1)]] }] };
        Assert.Throws<InvalidDataException>(() => ModelAnimationFrame.Create(geometry, invalid, fixture.Binding, fixture.Mapping, 0, true, budget));
        Assert.Equal(0, budget.UsedBytes);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly AnimationWideSamples clip;
        public NativeAnimationRigBinding Binding { get; }
        public FbxSkeletonExportInput Skeleton { get; }
        public IReadOnlyDictionary<int,string> Mapping { get; } = new Dictionary<int,string> { [0]="rig-root", [1]="rig-child" };
        public Fixture(bool curved = false)
        {
            var identity = new ResourceIdentity("synthetic-index", 1); var revision = new SourceRevision(identity.IndexContentId,"synthetic-revision");
            var extent = new DecodedExtent(identity,0,64);
            var entries = new NativeSkeletonNodeArrayBindingEntry[2];
            for (int ordinal=0;ordinal<2;ordinal++)
            {
                var components = new byte[40]; float[] values=[1,0,0,0,0,0,0,1,1,1];
                for(int i=0;i<values.Length;i++) BinaryPrimitives.WriteSingleLittleEndian(components.AsSpan(i*4,4),values[i]);
                var node = new NodeArrayBodyNode(ordinal,0,new(components,extent),0,ordinal==0?uint.MaxValue:0,[],0,extent);
                uint key=(uint)(ordinal+1)*10;
                entries[ordinal]=new(ordinal,new(ordinal,extent,new byte[64]),node,new(key,0,0,0,extent),new(key,NativeSkeletonNodeNameMatchStatus.Missing,[]));
            }
            var rig = new NativeSkeletonNodeArrayBindingDocument(identity,new(identity.IndexContentId,2),revision,
                new(IdentifierNamespace.Native,"entity",identity),extent,extent,extent,extent,Array.AsReadOnly(entries),4096);
            AnimationTransformTrack[] tracks=[new(0,0,0,17),new(10,0,0,18),new(20,0,0,17)];
            var samples=new AnimationTransformSample[9];
            for(int frame=0;frame<3;frame++)
            {
                samples[frame*3]=new(new(0,0,0,1),new(frame*10,0,0,0),new(1,1,1,0));
                var q=curved?Quaternion.CreateFromYawPitchRoll(frame*.35f,frame*.55f,frame*.2f):Quaternion.Identity;
                samples[frame*3+1]=new(new(q.X,q.Y,q.Z,q.W),Vector4.Zero,new(1,1,1,0));
                samples[frame*3+2]=new(new(0,0,0,1),new(0,frame,0,0),new(1,1,1,0));
            }
            // Synthetic decoded samples isolate export interpolation from stream-codec tests.
            var constructor=typeof(AnimationWideSamples).GetConstructors(BindingFlags.NonPublic|BindingFlags.Instance).Single();
            clip=(AnimationWideSamples)constructor.Invoke([new AnimationVersion14Timing(3,.1f,.3f),2u,tracks,samples,new Noop(),1f]);
            Binding=new(rig,clip,new(identity.IndexContentId,3),revision,1);
            var bind=new FbxBindMatrix([1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]);
            var local=new SourceTransform([1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1],MatrixLayout.RowMajor,VectorMultiplicationSide.Row);
            Skeleton=new(identity,revision,[new("rig-child","rig-root",null,bind,local),new("rig-root",null,null,bind,local)],[],_=>Task.CompletedTask);
        }
        public void Dispose()=>clip.Dispose();
    }
    private sealed class Noop : IDisposable { public void Dispose() { } }
    private static Matrix4x4 Matrix(SourceTransform t) { var e=t.Elements;return new(e[0],e[1],e[2],e[3],e[4],e[5],e[6],e[7],e[8],e[9],e[10],e[11],e[12],e[13],e[14],e[15]); }
    private static Vector3 Euler(Matrix4x4 m)
    {
        Matrix4x4.Decompose(m,out _,out var q,out _);m=Matrix4x4.CreateFromQuaternion(q);
        float y=MathF.Asin(Math.Clamp(-m.M13,-1,1));float x=MathF.Atan2(m.M23,m.M33);float z=MathF.Atan2(m.M12,m.M11);
        return new Vector3(x,y,z)*(180f/MathF.PI);
    }
    private static float Wrap(float x)=>(float)(x-360d*Math.Round(x/360d));
    private static double Error(Quaternion a,Quaternion b)
    {
        double dot=(double)a.X*b.X+(double)a.Y*b.Y+(double)a.Z*b.Z+(double)a.W*b.W;
        double aa=(double)a.X*a.X+(double)a.Y*a.Y+(double)a.Z*a.Z+(double)a.W*a.W;
        double bb=(double)b.X*b.X+(double)b.Y*b.Y+(double)b.Z*b.Z+(double)b.W*b.W;
        return 2*Math.Acos(Math.Clamp(Math.Abs(dot)/Math.Sqrt(aa*bb),0,1));
    }
}
