using System.Buffers.Binary;
using System.Numerics;
using Deviant.Assets;
using Deviant.Export.Models;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class MaterialSurfaceMapBakerTests
{
    [Fact]
    public void ReservedOutputAndGutterReusePreserveTheCompleteDds()
    {
        var material = Material(Constant(.25f, .5f, 1f), Scalar(.2f), Scalar(.5f));
        var baseline = new MemoryAccount();
        var expected = MaterialSurfaceMapBaker.Bake(material, Triangle(), new Dictionary<int, MaterialBakeTexture>(), 32, 32, baseline.Reserve);
        var credited = new MemoryAccount();
        using (credited.Reserve(expected.Document.OriginalBytes.Length, "output"))
        {
            var actual = MaterialSurfaceMapBaker.Bake(material, Triangle(), new Dictionary<int, MaterialBakeTexture>(),
                32, 32, credited.Reserve, reservedOutputBytes: expected.Document.OriginalBytes.Length);
            Assert.Equal(expected.Document.OriginalBytes.ToArray(), actual.Document.OriginalBytes.ToArray());
            Assert.Equal(expected.GraphSha256, actual.GraphSha256);
            Assert.Equal(expected.Document.OriginalBytes.Length, credited.UsedBytes);
            Assert.Equal(MaterialSurfaceMapBaker.CreateTransparencyMap(expected).Document.OriginalBytes.ToArray(),
                MaterialSurfaceMapBaker.CreateTransparencyMap(actual).Document.OriginalBytes.ToArray());
        }
        Assert.Equal(0, credited.UsedBytes);
        Assert.Equal(baseline.PeakBytes, credited.PeakBytes);
    }
    [Fact]
    public void LinearSamplingDecodesSrgbBeforeFilteringAndPreservesAlpha()
    {
        var texture = Texture([0, 0, 0, 0, 255, 255, 255, 255], true, 1);
        var sample = MaterialSurfaceMapBaker.Sample(texture, new(.5f, .5f));
        Assert.Equal(new Vector4(.5f), sample);
    }

    [Theory]
    [InlineData(0, 1.25f, 0f)]
    [InlineData(0, -.25f, 1f)]
    [InlineData(1, -.25f, 0f)]
    [InlineData(2, 1.25f, 1f)]
    [InlineData(3, 1.25f, 0f)]
    public void PointSamplingUsesAddressMode(int address, float u, float expected)
    {
        var texture = Texture([0, 0, 0, 255, 255, 255, 255, 255], false, 0, address);
        Assert.Equal(expected, MaterialSurfaceMapBaker.Sample(texture, new(u, .5f)).X);
    }

    [Theory]
    [InlineData(1f, 255)]
    [InlineData(-1f, 0)]
    public void OutOfDomainClampSamplesRequireAtlasAndRetainSourceCoordinates(float offset, int expected)
    {
        var uv = new MaterialExpression(MaterialExpressionOperation.Input, MaterialValueType.Vector2, [], Input: MaterialInput.Uv0);
        var material = Material(new(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4, [uv], TextureSlot: 0));
        var texture = Texture([0, 0, 0, 255, 255, 255, 255, 255], false, 0, address: 1);
        var textures = new Dictionary<int, MaterialBakeTexture> { [0] = texture };
        var primitive = Triangle() with { TextureCoordinates = [[new(offset, 0), new(offset + 1, 0), new(offset, 1)]] };
        var originalCoordinates = primitive.TextureCoordinates![0].ToArray();
        var budget = new MemoryAccount();

        Assert.Throws<MaterialBakeAtlasRequiredException>(() => MaterialSurfaceMapBaker.Bake(material, primitive, textures, 2, 2, budget.Reserve));
        Vector2[] corners = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY];
        var map = MaterialSurfaceMapBaker.Bake(material, primitive, textures, 2, 2, budget.Reserve,
            textureCoordinateSet: 1, polygonCornerCoordinates: corners);

        Assert.Equal(new byte[] { (byte)expected, (byte)expected, (byte)expected, 255 }, map.Surface.Rgba8.Span[..4].ToArray());
        Assert.Equal(originalCoordinates, primitive.TextureCoordinates[0]);
        Assert.Same(corners, map.PolygonCornerCoordinates);
        Assert.Equal(0, budget.UsedBytes);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(-1f)]
    public void AtlasEvaluatesNonperiodicExpressionsAtOriginalUvs(float offset)
    {
        var uv = new MaterialExpression(MaterialExpressionOperation.Input, MaterialValueType.Vector2, [], Input: MaterialInput.Uv0);
        var u = new MaterialExpression(MaterialExpressionOperation.Swizzle, MaterialValueType.Scalar, [uv], Components: "x");
        var material = Material(new(MaterialExpressionOperation.Subtract, MaterialValueType.Scalar, [u, Scalar(offset)]));
        var primitive = Triangle() with { TextureCoordinates = [[new(offset, 0), new(offset + 1, 0), new(offset, 1)]] };
        var budget = new MemoryAccount();
        Assert.Throws<MaterialBakeAtlasRequiredException>(() => MaterialSurfaceMapBaker.Bake(material, primitive,
            new Dictionary<int, MaterialBakeTexture>(), 2, 2, budget.Reserve));
        var map = MaterialSurfaceMapBaker.Bake(material, primitive, new Dictionary<int, MaterialBakeTexture>(), 2, 2,
            budget.Reserve, textureCoordinateSet: 1, polygonCornerCoordinates: [Vector2.Zero, Vector2.UnitX, Vector2.UnitY]);
        Assert.Equal(new byte[] { 137, 137, 137, 255 }, map.Surface.Rgba8.Span[..4].ToArray());
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void InDomainUvOnlyBakeRetainsTheFullTextureMap()
    {
        var uv = new MaterialExpression(MaterialExpressionOperation.Input, MaterialValueType.Vector2, [], Input: MaterialInput.Uv0);
        var material = Material(new(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4, [uv], TextureSlot: 0));
        var textures = new Dictionary<int, MaterialBakeTexture> { [0] = Texture([0, 0, 0, 255, 255, 255, 255, 255], false, 0, address: 1) };
        var budget = new MemoryAccount();
        var map = MaterialSurfaceMapBaker.Bake(material, Triangle(), textures, 2, 2, budget.Reserve);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 0, 255, 255, 255, 255, 255 }, map.Surface.Rgba8.ToArray());
        Assert.Null(map.PolygonCornerCoordinates);
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void ExistingInDomainOutputSetCanBakeOutOfDomainSourceUvs()
    {
        var uv = new MaterialExpression(MaterialExpressionOperation.Input, MaterialValueType.Vector2, [], Input: MaterialInput.Uv0);
        var material = Material(new(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4, [uv], TextureSlot: 0));
        var textures = new Dictionary<int, MaterialBakeTexture> { [0] = Texture([0, 0, 0, 255, 255, 255, 255, 255], false, 0, address: 1) };
        var primitive = Triangle() with { TextureCoordinates = [
            [new(1, 0), new(2, 0), new(1, 1)], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY]] };
        var budget = new MemoryAccount();
        Assert.Throws<MaterialBakeAtlasRequiredException>(() => MaterialSurfaceMapBaker.Bake(material, primitive, textures, 2, 2, budget.Reserve));
        var map = MaterialSurfaceMapBaker.Bake(material, primitive, textures, 2, 2, budget.Reserve, textureCoordinateSet: 1);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, map.Surface.Rgba8.Span[..4].ToArray());
        Assert.Equal(1, map.TextureCoordinateSet);
        Assert.Null(map.PolygonCornerCoordinates);
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void AtlasCanBakeWhenOtherOutputSetHasNoCoverage()
    {
        var uv = new MaterialExpression(MaterialExpressionOperation.Input, MaterialValueType.Vector2, [], Input: MaterialInput.Uv0);
        var material = Material(new(MaterialExpressionOperation.Swizzle, MaterialValueType.Scalar, [uv], Components: "x"));
        var primitive = Triangle() with { TextureCoordinates = [
            [new(2, 0), new(2, 0), new(2, 0)], [Vector2.Zero, Vector2.Zero, Vector2.Zero]] };
        var textures = new Dictionary<int, MaterialBakeTexture>();
        var budget = new MemoryAccount();
        Assert.Throws<MaterialBakeAtlasRequiredException>(() => MaterialSurfaceMapBaker.Bake(material, primitive, textures, 2, 2, budget.Reserve));
        var failure = Assert.Throws<InvalidDataException>(() => MaterialSurfaceMapBaker.Bake(material, primitive, textures, 2, 2,
            budget.Reserve, textureCoordinateSet: 1));
        Assert.Contains("No UV samples", failure.Message);
        var map = MaterialSurfaceMapBaker.Bake(material, primitive, textures, 2, 2, budget.Reserve,
            textureCoordinateSet: 2, polygonCornerCoordinates: [Vector2.Zero, Vector2.UnitX, Vector2.UnitY]);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, map.Surface.Rgba8.Span[..4].ToArray());
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void MissingGraphInputRemainsInvalidWhenOutputUvsRequireAtlas()
    {
        var material = Material(new(MaterialExpressionOperation.Input, MaterialValueType.Vector4, [], Input: MaterialInput.VertexColor));
        var primitive = Triangle() with { TextureCoordinates = [[new(1, 0), new(2, 0), new(1, 1)]] };
        var budget = new MemoryAccount();
        var error = Assert.Throws<InvalidDataException>(() => MaterialSurfaceMapBaker.Bake(material, primitive,
            new Dictionary<int, MaterialBakeTexture>(), 2, 2, budget.Reserve));
        Assert.Contains("VertexColor", error.Message);
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void PrimaryUvMapCombinesColourAndCoverageWithVerifiedDdsProvenance()
    {
        var budget = new MemoryAccount();
        var material = Material(Constant(.25f, .5f, 1f), Scalar(.2f), Scalar(.5f));
        var baked = MaterialSurfaceMapBaker.Bake(material, Triangle(), new Dictionary<int, MaterialBakeTexture>(), 4, 4, budget.Reserve);
        Assert.Equal(0, budget.UsedBytes);
        Assert.Equal(material.Identity, baked.Document.Identity);
        Assert.Equal(material.Revision, baked.Surface.Revision);
        Assert.Equal(baked.Document.ContentSha256, baked.Surface.SourcePayloadSha256);
        Assert.True(baked.Surface.IsSrgb);
        Assert.Equal(new byte[] { 137, 188, 255, 0 }, baked.Surface.Rgba8.Span[..4].ToArray());
        Assert.Equal(64, baked.GraphSha256.Length);
        var transparency = MaterialSurfaceMapBaker.CreateTransparencyMap(baked);
        Assert.False(transparency.Surface.IsSrgb);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, transparency.Surface.Rgba8.Span[..4].ToArray());
    }

    [Fact]
    public void SecondaryUvInputUsesTriangleInterpolation()
    {
        var coordinate = new MaterialExpression(MaterialExpressionOperation.Input, MaterialValueType.Vector2, [], Input: MaterialInput.Uv1);
        var color = new MaterialExpression(MaterialExpressionOperation.Construct, MaterialValueType.Vector3,
            [coordinate, Scalar(0)]);
        var material = Material(color);
        var triangle = Triangle() with { TextureCoordinates = [
            [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            [Vector2.One, new(0, 1), new(1, 0)]] };
        var budget = new MemoryAccount();
        var map = MaterialSurfaceMapBaker.Bake(material, triangle, new Dictionary<int, MaterialBakeTexture>(), 4, 4, budget.Reserve);
        Assert.Equal(240, map.Surface.Rgba8.Span[0]);
        Assert.Equal(240, map.Surface.Rgba8.Span[1]);
        Assert.Equal(255, map.Surface.Rgba8.Span[3]);
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void OverlappingUvIslandsWithDifferentVertexColoursAreRejected()
    {
        var input = new MaterialExpression(MaterialExpressionOperation.Input, MaterialValueType.Vector4, [], Input: MaterialInput.VertexColor);
        var material = Material(input); var triangle = Triangle();
        var mesh = triangle with { Positions = [.. triangle.Positions, .. triangle.Positions], Indices = [0, 1, 2, 3, 4, 5],
            TextureCoordinates = [[.. triangle.TextureCoordinates![0], .. triangle.TextureCoordinates[0]]],
            Colors = [Vector4.Zero, Vector4.Zero, Vector4.Zero, Vector4.One, Vector4.One, Vector4.One] };
        var budget = new MemoryAccount();
        var error = Assert.Throws<MaterialBakeAtlasRequiredException>(() => MaterialSurfaceMapBaker.Bake(material, mesh,
            new Dictionary<int, MaterialBakeTexture>(), 4, 4, budget.Reserve));
        Assert.Contains("Overlapping UVs", error.Message);
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void DistinctSecondaryUvIslandsRepresentConflictingPrimaryVertexColours()
    {
        var input = new MaterialExpression(MaterialExpressionOperation.Input, MaterialValueType.Vector4, [], Input: MaterialInput.VertexColor);
        var triangle = Triangle();
        var mesh = triangle with { Positions = [.. triangle.Positions, .. triangle.Positions], Indices = [0, 1, 2, 3, 4, 5],
            TextureCoordinates = [[.. triangle.TextureCoordinates![0], .. triangle.TextureCoordinates[0]],
                [new(0, 0), new(.4f, 0), new(0, .4f), new(.6f, .6f), new(1, .6f), new(.6f, 1)]],
            Colors = [Vector4.Zero, Vector4.Zero, Vector4.Zero, Vector4.One, Vector4.One, Vector4.One] };
        var budget = new MemoryAccount();
        var map = MaterialSurfaceMapBaker.Bake(Material(input), mesh, new Dictionary<int, MaterialBakeTexture>(), 8, 8,
            budget.Reserve, textureCoordinateSet: 1);
        Assert.Equal(1, map.TextureCoordinateSet);
        Assert.Equal(0, map.Surface.Rgba8.Span[(1 * 8 + 1) * 4]);
        Assert.Equal(255, map.Surface.Rgba8.Span[(5 * 8 + 5) * 4]);
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void CornerAtlasBakesConflictingVertexColoursWithoutChangingSourceGeometry()
    {
        var input = new MaterialExpression(MaterialExpressionOperation.Input, MaterialValueType.Vector4, [], Input: MaterialInput.VertexColor);
        var triangle = Triangle();
        var mesh = triangle with { Positions = [.. triangle.Positions, .. triangle.Positions], Indices = [0, 1, 2, 3, 4, 5],
            TextureCoordinates = [[.. triangle.TextureCoordinates![0], .. triangle.TextureCoordinates[0]]],
            Colors = [Vector4.Zero, Vector4.Zero, Vector4.Zero, Vector4.One, Vector4.One, Vector4.One] };
        Vector2[] corners = [new(.1f,.1f),new(.4f,.1f),new(.1f,.4f),new(.6f,.6f),new(1,.6f),new(.6f,1)];
        var budget = new MemoryAccount();
        var map = MaterialSurfaceMapBaker.Bake(Material(input), mesh, new Dictionary<int, MaterialBakeTexture>(), 8, 8,
            budget.Reserve, textureCoordinateSet: 1, polygonCornerCoordinates: corners);
        Assert.Same(corners,map.PolygonCornerCoordinates);
        Assert.Equal(0,map.Surface.Rgba8.Span[(1 * 8 + 1) * 4]);
        Assert.Equal(255,map.Surface.Rgba8.Span[(5 * 8 + 5) * 4]);
        Assert.Equal(6,mesh.Positions.Count);Assert.Single(mesh.TextureCoordinates!);
        Assert.Equal(0,budget.UsedBytes);
    }

    [Fact]
    public void AdjacentAtlasTrianglesOwnSharedEdgePixelsOnceAndRejectInteriorOverlap()
    {
        var input = new MaterialExpression(MaterialExpressionOperation.Input,MaterialValueType.Vector4,[],Input:MaterialInput.VertexColor);
        var triangle = Triangle();
        var mesh = triangle with { Positions = [..triangle.Positions,..triangle.Positions],Indices=[0,1,2,3,4,5],
            TextureCoordinates=[[..triangle.TextureCoordinates![0],..triangle.TextureCoordinates[0]]],
            Colors=[Vector4.Zero,Vector4.Zero,Vector4.Zero,Vector4.One,Vector4.One,Vector4.One] };
        Vector2[] adjacent = [new(0,0),new(1,0),new(0,1),new(1,0),new(1,1),new(0,1)];
        var account=new MemoryAccount();
        var map=MaterialSurfaceMapBaker.Bake(Material(input),mesh,new Dictionary<int,MaterialBakeTexture>(),8,8,
            account.Reserve,textureCoordinateSet:1,polygonCornerCoordinates:adjacent);
        Assert.Equal(0,map.Surface.Rgba8.Span[(1*8+1)*4]);
        Assert.Equal(255,map.Surface.Rgba8.Span[(6*8+6)*4]);
        Vector2[] overlapping=[new(0,0),new(1,0),new(0,1),new(0,0),new(1,0),new(0,1)];
        Assert.Throws<MaterialBakeAtlasRequiredException>(()=>MaterialSurfaceMapBaker.Bake(Material(input),mesh,
            new Dictionary<int,MaterialBakeTexture>(),8,8,account.Reserve,textureCoordinateSet:1,polygonCornerCoordinates:overlapping));
        Assert.Equal(0,account.UsedBytes);
    }

    [Theory]
    [InlineData(MaterialInput.ViewNormal)]
    [InlineData(MaterialInput.ViewDepth)]
    [InlineData(MaterialInput.SceneViewDepth)]
    public void ViewDependentSurfaceAndCancellationLeaveBudgetUnchanged(MaterialInput cameraInput)
    {
        var budget = new MemoryAccount();
        var input = new MaterialExpression(MaterialExpressionOperation.Input,
            cameraInput == MaterialInput.ViewNormal ? MaterialValueType.Vector3 : MaterialValueType.Scalar, [], Input: cameraInput);
        Assert.Throws<InvalidDataException>(() => MaterialSurfaceMapBaker.Bake(Material(input), Triangle(),
            new Dictionary<int, MaterialBakeTexture>(), 4, 4, budget.Reserve));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MaterialSurfaceMapBaker.Bake(Material(Constant(1, 1, 1)), Triangle(),
            new Dictionary<int, MaterialBakeTexture>(), 4, 4, budget.Reserve, canceled.Token));
        Assert.Equal(0, budget.UsedBytes);
    }

    private static MaterialAsset Material(MaterialExpression color, MaterialExpression? opacity = null, MaterialExpression? cutoff = null)
    {
        var identity = new ResourceIdentity("generated", 0); var revision = new SourceRevision("generated", "revision");
        return new(identity, revision, "generated", [], [], [])
            { SurfaceGraph = new(color, opacity, cutoff, [], [], []) };
    }
    private static MaterialExpression Constant(params float[] values) => new(MaterialExpressionOperation.Constant, (MaterialValueType)values.Length, [], values);
    private static MaterialExpression Scalar(float value) => Constant(value);
    private static MeshPrimitive Triangle() => new("surface", PrimitiveTopology.Triangles,
        [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2], IndexElementFormat.UInt16, [],
        TextureCoordinates: [[Vector2.Zero, Vector2.UnitX, Vector2.UnitY]]);
    private static MaterialBakeTexture Texture(byte[] pixels, bool srgb, int filter, int address = 0)
    {
        var bytes = new byte[148 + pixels.Length]; "DDS "u8.CopyTo(bytes);
        Write(4, 124); Write(8, 0x100f); Write(12, 1); Write(16, 2); Write(20, 8);
        Write(76, 32); Write(80, 4); "DX10"u8.CopyTo(bytes.AsSpan(84)); Write(108, 0x1000);
        Write(128, srgb ? 29u : 28u); Write(132, 3); Write(140, 1); pixels.CopyTo(bytes, 148);
        var document = new DdsTextureReader().Read(bytes); var surface = new DdsTextureDecoder().DecodeMip(document);
        return new(surface, NativeTextureSamplingDecoder.Decode((ulong)filter << 19 | (ulong)filter << 23 | (ulong)address << 27));
        void Write(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    }
    private sealed class MemoryAccount
    {
        public long UsedBytes { get; private set; }
        public long PeakBytes { get; private set; }
        public IDisposable Reserve(long bytes, string category) { UsedBytes += bytes; PeakBytes = Math.Max(PeakBytes, UsedBytes); return new Lease(this, bytes); }
        private sealed class Lease(MemoryAccount owner, long bytes) : IDisposable
        {
            private bool disposed;
            public void Dispose() { if (!disposed) { owner.UsedBytes -= bytes; disposed = true; } }
        }
    }
}
