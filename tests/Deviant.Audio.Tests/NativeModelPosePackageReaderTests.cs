using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Export.Models;
using Deviant.Formats.Models;
using Xunit;

namespace Deviant.Audio.Tests;

public sealed class NativeModelPosePackageReaderTests
{
    [Fact]
    public async Task ConsumerSurvivesOwnerDisposalAndReleasesFileAndBudgetAfterJob()
    {
        var path = Path.Combine(Path.GetTempPath(), "deviant-pose-" + Guid.NewGuid().ToString("N") + ".json");
        var budget = new AssetMemoryBudget();
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Package()));
            var owner = await NativeModelPosePackageReader.ReadAsync(path, budget);
            var consumer = owner.AcquireLease();
            owner.Dispose();
            Assert.Throws<ObjectDisposedException>(() => owner.AcquireLease());
            Assert.Equal(48, consumer.Package.MatrixRows.Length);
            Assert.Single(consumer.Package.Joints);
            consumer.EnsureUnchanged(); Assert.True(budget.UsedBytes > 0);
            Assert.Throws<IOException>(() => File.Open(path,FileMode.Open,FileAccess.Write,FileShare.Read));
            consumer.Dispose(); consumer.Dispose();
            Assert.Equal(0,budget.UsedBytes);
            Assert.Throws<ObjectDisposedException>(() => consumer.Package);
            using var writable = File.Open(path,FileMode.Open,FileAccess.Write,FileShare.Read);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("parent")]
    [InlineData("mapping")]
    [InlineData("pose")]
    [InlineData("layout")]
    [InlineData("projective")]
    [InlineData("material-world")]
    public async Task InvalidPackageReleasesReadHandleAndAllReservations(string field)
    {
        var path = Path.Combine(Path.GetTempPath(), "deviant-pose-" + Guid.NewGuid().ToString("N") + ".json");
        var budget = new AssetMemoryBudget(); var package = Package();
        package = field switch
        {
            "digest" => package with { PoseSource = package.PoseSource with { MatrixRowsSha256 = new string('0',64) } },
            "parent" => package with { Joints = [package.Joints[0] with {ParentKey="root"}] },
            "pose" => package with { Joints = [package.Joints[0] with {PoseLocal = package.Joints[0].PoseLocal with {
                Elements = new float[]{1,0,0,0,0,1,0,0,0,0,1,0,1,0,0,1} }}] },
            "layout" => package with { Joints = [package.Joints[0] with {PoseLocal = package.Joints[0].PoseLocal with {Layout=(MatrixLayout)99}}] },
            "projective" => package with { Joints = [package.Joints[0] with {PoseLocal = package.Joints[0].PoseLocal with {
                Elements = new float[]{1,0,0,1,0,1,0,0,0,0,1,0,0,0,0,1} }}] },
            "material-world" => package with { MaterialWorldTransform = new(new float[]{1,0,0,1,0,1,0,0,0,0,1,0,0,0,0,1},
                MatrixLayout.RowMajor,VectorMultiplicationSide.Row) },
            _ => package with { JointOrdinalsByMatrixRow = new Dictionary<uint,int>{{0,1}} }
        };
        try
        {
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(package));
            await Assert.ThrowsAsync<InvalidDataException>(() => NativeModelPosePackageReader.ReadAsync(path,budget));
            Assert.Equal(0,budget.UsedBytes);
            using var writable = File.Open(path,FileMode.Open,FileAccess.Write,FileShare.Read);
        }
        finally { File.Delete(path); }
    }

    private static NativeModelPosePackage Package()
    {
        var mesh = new ResourceIdentity("generated",1,"mesh"); var rig = new ResourceIdentity("generated",1,"rig");
        var rows = new byte[48];
        using (var stream = new MemoryStream(rows))
        using (var writer = new BinaryWriter(stream))
            foreach(var value in new float[]{1,0,0,0,0,1,0,0,0,0,1,0}) writer.Write(value);
        float[] identity = [1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];
        var bind = new FbxBindMatrix(identity.Select(value=>(double)value).ToArray());
        var source = new NativeSkinningPoseSource(mesh,rig,new("generated","revision"),0,1,
            new string('a',64),Convert.ToHexString(SHA256.HashData(rows)).ToLowerInvariant(),new(rig,0,48));
        return new(1,source,0,rows,[new("root",null,null,bind,new(identity,MatrixLayout.RowMajor,VectorMultiplicationSide.Row))],
            bind,new Dictionary<uint,int>{{0,0}});
    }

    [Theory]
    [InlineData("misaligned")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public async Task IncompleteOrAmbiguousRowMapIsRejectedBeforeUse(string kind)
    {
        var package = TwoJointPackage();
        package = package with { JointOrdinalsByMatrixRow = kind switch
        {
            "misaligned" => new Dictionary<uint,int>{{0,0},{1,1}},
            "missing" => new Dictionary<uint,int>{{0,0}},
            _ => new Dictionary<uint,int>{{0,0},{3,0}}
        }};
        var path=Path.Combine(Path.GetTempPath(),"deviant-pose-"+Guid.NewGuid().ToString("N")+".json"); var budget=new AssetMemoryBudget();
        try
        {
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(package));
            await Assert.ThrowsAsync<InvalidDataException>(()=>NativeModelPosePackageReader.ReadAsync(path,budget));
            Assert.Equal(0,budget.UsedBytes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExplicitJointPermutationKeepsEachMatrixRowAssociation()
    {
        var package=TwoJointPackage();
        package=package with { Joints=[package.Joints[1],package.Joints[0]],JointOrdinalsByMatrixRow=new Dictionary<uint,int>{{0,1},{3,0}} };
        var path=Path.Combine(Path.GetTempPath(),"deviant-pose-"+Guid.NewGuid().ToString("N")+".json"); var budget=new AssetMemoryBudget();
        try
        {
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(package));
            using(var owner=await NativeModelPosePackageReader.ReadAsync(path,budget))
            using(var consumer=owner.AcquireLease()) Assert.Equal("other",consumer.Package.Joints[0].Key);
            Assert.Equal(0,budget.UsedBytes);
        }
        finally { File.Delete(path); }
    }

    private static NativeModelPosePackage TwoJointPackage()
    {
        var package=Package(); var rows=package.MatrixRows.ToArray().Concat(package.MatrixRows.ToArray()).ToArray();
        BitConverter.GetBytes(2f).CopyTo(rows,60);
        var local=package.Joints[0].PoseLocal.Elements.ToArray(); local[12]=2;
        return package with
        {
            MatrixRows=rows,
            PoseSource=package.PoseSource with {MatrixRowsExtent=package.PoseSource.MatrixRowsExtent with {Length=96},
                MatrixRowsSha256=Convert.ToHexString(SHA256.HashData(rows)).ToLowerInvariant()},
            Joints=[package.Joints[0],package.Joints[0] with {Key="other",PoseLocal=package.Joints[0].PoseLocal with {Elements=local}}],
            JointOrdinalsByMatrixRow=new Dictionary<uint,int>{{0,0},{3,1}}
        };
    }
}
