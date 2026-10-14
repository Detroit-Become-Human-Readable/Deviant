using System.Numerics;
using Deviant.Assets;
using Deviant.Export.Models;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class MaterialUvAtlasTests
{
    [Fact]
    public void SeparateChartsKeepCoincidentFacesDistinctWithoutChangingSourceGeometry()
    {
        if (!OperatingSystem.IsWindows()) return;
        var primitive = new MeshPrimitive("surface", PrimitiveTopology.Triangles,
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2, 0, 1, 2],
            IndexElementFormat.UInt32, []);
        var positions = primitive.Positions.ToArray(); var indices = primitive.Indices.ToArray();
        var budget = new Budget();
        var atlas = MaterialUvAtlas.Generate(primitive, 128, 16 * 1024 * 1024,
            budget.Reserve, separateFaces: true);
        Assert.Equal(6, atlas.PolygonCornerCoordinates.Count);
        Assert.Equal(positions, primitive.Positions); Assert.Equal(indices, primitive.Indices);
        var a = atlas.PolygonCornerCoordinates.Take(3).ToArray();
        var b = atlas.PolygonCornerCoordinates.Skip(3).ToArray();
        Assert.False(a.SequenceEqual(b));
        Assert.False(Inside((a[0] + a[1] + a[2]) / 3, b));
        Assert.False(Inside((b[0] + b[1] + b[2]) / 3, a));
        Assert.Equal(0, budget.Bytes);

        static bool Inside(Vector2 point, Vector2[] triangle)
        {
            static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
            float a = Cross(triangle[1] - triangle[0], point - triangle[0]);
            float b = Cross(triangle[2] - triangle[1], point - triangle[1]);
            float c = Cross(triangle[0] - triangle[2], point - triangle[2]);
            return a > 0 && b > 0 && c > 0 || a < 0 && b < 0 && c < 0;
        }
    }

    [Fact]
    public void ManagedNativeAtlasPreservesCornerCountAcrossCoordinateScales()
    {
        if(!OperatingSystem.IsWindows())return;
        var original=new MeshPrimitive("surface",PrimitiveTopology.Triangles,
            [Vector3.Zero,new(0.00001f,0,0),new(0.00001f,0.00001f,0),new(0,0.00001f,0)],
            [0,1,2,0,2,3],IndexElementFormat.UInt32,[]);
        var expected=original.Positions.ToArray();var budget=new Budget();
        var atlas=MaterialUvAtlas.Generate(original,128,16*1024*1024,budget.Reserve);
        Assert.Equal(6,atlas.PolygonCornerCoordinates.Count);
        Assert.All(atlas.PolygonCornerCoordinates,uv=>{Assert.InRange(uv.X,0,1);Assert.InRange(uv.Y,0,1);});
        Assert.Equal(expected,original.Positions);Assert.Equal(0,budget.Bytes);
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(()=>MaterialUvAtlas.Generate(original,128,16*1024*1024,budget.Reserve,canceled.Token));
        Assert.Equal(0,budget.Bytes);
    }

    private sealed class Budget
    {
        public long Bytes;
        public IDisposable Reserve(long bytes,string category){Bytes+=bytes;return new Lease(this,bytes);}
        private sealed class Lease(Budget owner,long bytes):IDisposable
        {
            private bool released;
            public void Dispose(){if(!released){owner.Bytes-=bytes;released=true;}}
        }
    }
}
