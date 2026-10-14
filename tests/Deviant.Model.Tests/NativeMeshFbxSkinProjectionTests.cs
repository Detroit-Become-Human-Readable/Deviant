using System.Buffers.Binary;
using System.Numerics;
using Deviant.Assets;
using Deviant.Export.Models;
using Deviant.Formats.Models;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class NativeMeshFbxSkinProjectionTests
{
    [Fact]
    public void NonzeroOverlappingWindowsRebaseEachPrimitiveInfluenceRow()
    {
        var sample = NativeSkinningPositionEvaluatorTests.Sample(1);
        var buffer = sample.Mesh.VertexBuffers[0] with { ElementCount = 4 };
        var original = sample.Mesh.Groups[0].Parts[0];
        var parts = new[] { original with { BaseVertex = 1, VertexCount = 2 }, original with { BaseVertex = 2, VertexCount = 2 } };
        var mesh = sample.Mesh with { VertexBuffers = [buffer], Groups = [sample.Mesh.Groups[0] with { Parts = parts }] };
        var stride = checked((int)buffer.Streams[3].Stride);
        var gpu = new byte[(16 + stride * 4 + 15) & ~15];
        for (var vertex = 0; vertex < 4; vertex++)
            BinaryPrimitives.WriteUInt16LittleEndian(gpu.AsSpan(16 + stride * vertex + 24), BitConverter.HalfToUInt16Bits((Half)(vertex * 3)));
        long current = 0;
        IDisposable Reserve(long bytes, string category) { current += bytes; return new Lease(() => current -= bytes); }
        using (var attributes = new NativeSkinningAttributeDecoder().Decode(mesh, gpu, 0, Reserve))
        {
            var geometry = new GeometryAsset(new("generated", "1"), [], parts.Select((part, p) => new MeshPrimitive(
                $"{mesh.Identity.Key}/group/0/part/{p}", PrimitiveTopology.Triangles, [Vector3.Zero,Vector3.One], [], IndexElementFormat.UInt16, [])).ToArray(), new Dictionary<string,string>());
            var mapping = new Dictionary<uint,int> { [0]=10,[3]=11,[6]=12,[9]=13 };
            using var result = NativeMeshFbxSkinProjection.Create(mesh, attributes.Document, geometry,
                new([1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]), mapping, Reserve);
            Assert.Equal(new[] {11,12}, result.Skins[0].VertexInfluences.Select(row => Assert.Single(row).JointOrdinal));
            Assert.Equal(new[] {12,13}, result.Skins[1].VertexInfluences.Select(row => Assert.Single(row).JointOrdinal));
            Assert.All(result.Skins.SelectMany(skin => skin.VertexInfluences), row => Assert.Equal(1d, Assert.Single(row).Weight));
        }
        Assert.Equal(0,current);
    }

    private sealed class Lease(Action release) : IDisposable
    { private int done; public void Dispose() { if (Interlocked.Exchange(ref done,1)==0) release(); } }
}
