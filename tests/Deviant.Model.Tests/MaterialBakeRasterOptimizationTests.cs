using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Export.Models;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class MaterialBakeRasterOptimizationTests
{
    [Fact]
    public void EverySrgbByteDecodesWithTheOriginalFloatArithmetic()
    {
        var rgba = Enumerable.Range(0, 256).SelectMany(value => new byte[] { (byte)value, (byte)(255 - value), (byte)value, 255 }).ToArray();
        var texture = Texture(256, 1, rgba, srgb: true);
        for (int index = 0; index < 256; index++)
        {
            var sample = MaterialSurfaceMapBaker.Sample(texture, new((index + .5f) / 256, .5f));
            Assert.Equal(BitConverter.SingleToInt32Bits(ExpectedDecode((byte)index)), BitConverter.SingleToInt32Bits(sample.X));
            Assert.Equal(BitConverter.SingleToInt32Bits(ExpectedDecode((byte)(255 - index))), BitConverter.SingleToInt32Bits(sample.Y));
            Assert.Equal(BitConverter.SingleToInt32Bits(ExpectedDecode((byte)index)), BitConverter.SingleToInt32Bits(sample.Z));
            Assert.Equal(1f, sample.W);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OutputRootsShareOnlyTheSameTextureExpressionAndKeepExpectedPixels(bool shared)
    {
        var uv = Input(MaterialInput.Uv0, MaterialValueType.Vector2);
        var sample = new MaterialExpression(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4, [uv], TextureSlot: 0);
        MaterialExpression SeparateSample() => new(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4, [uv], TextureSlot: 0);
        var opacity = new MaterialExpression(MaterialExpressionOperation.Swizzle, MaterialValueType.Scalar, [shared ? sample : SeparateSample()], Components: "w");
        var cutoff = new MaterialExpression(MaterialExpressionOperation.Swizzle, MaterialValueType.Scalar, [shared ? sample : SeparateSample()], Components: "x");
        var material = Material(sample, opacity, cutoff);
        var sourceGraphHash = MaterialSurfaceMapBaker.ComputeGraphHash(material.SurfaceGraph!);
        var texture = Texture(2, 2, [255,0,0,0, 0,255,0,127, 0,0,255,128, 255,255,255,255]);
        var observer = new CounterObserver(material.Identity.IndexContentId);
        using var subscription = AssetLoadDiagnostics.Listener.Subscribe(observer, name => name == AssetLoadDiagnostics.CounterName);
        var result = MaterialSurfaceMapBaker.Bake(material, Triangle(), new Dictionary<int, MaterialBakeTexture> { [0] = texture }, 2, 2, Reserve);
        Assert.Equal(new byte[] { 255,0,0,0, 0,255,0,127, 0,0,255,128, 255,255,255,255 }, result.Surface.Rgba8.ToArray());
        var counts = Assert.Single(observer.Events, item => item.Stage == "BakeRaster");
        Assert.Equal(shared ? 7 : 21, counts.Values["TextureSamples"]);
        Assert.Equal(3, counts.Values["VertexPixelEvaluations"]);
        Assert.Equal(4, counts.Values["RasterPixelEvaluations"]);
        Assert.Same(sample, material.SurfaceGraph!.BaseColor);
        if (shared)
        {
            Assert.Same(sample, material.SurfaceGraph.Opacity!.Arguments[0]);
            Assert.Same(sample, material.SurfaceGraph.AlphaCutoff!.Arguments[0]);
        }
        else
        {
            Assert.NotSame(sample, material.SurfaceGraph.Opacity!.Arguments[0]);
            Assert.NotSame(sample, material.SurfaceGraph.AlphaCutoff!.Arguments[0]);
        }
        Assert.Equal(material.Identity, result.Document.Identity);
        Assert.Equal(material.Revision, result.Surface.Revision);
        Assert.Equal(sourceGraphHash, result.GraphSha256);
    }

    [Theory]
    [InlineData(.499f, .5f, 0)]
    [InlineData(.5f, .5f, 128)]
    [InlineData(.501f, .5f, 128)]
    [InlineData(-1f, -2f, 0)]
    [InlineData(2f, 2f, 255)]
    public void CutoffEqualityAndClampedCoverageMatchTheOriginalConditional(float opacity, float cutoff, int expectedAlpha)
    {
        var map = MaterialSurfaceMapBaker.Bake(Material(Scalar(.25f), Scalar(opacity), Scalar(cutoff)), Triangle(),
            new Dictionary<int, MaterialBakeTexture>(), 1, 1, Reserve);
        Assert.Equal(new byte[] { 137,137,137,(byte)expectedAlpha }, map.Surface.Rgba8.ToArray());
    }

    [Theory]
    [InlineData(MaterialInput.WorldPosition, 137, 137, 0)]
    [InlineData(MaterialInput.WorldNormal, 0, 0, 255)]
    [InlineData(MaterialInput.VertexColor, 137, 188, 255)]
    public void RequiredVertexInputsRetainTheirInterpolationWhileUnusedUvsDoNotAffectThePixel(MaterialInput input, int r, int g, int b)
    {
        var primitive = Triangle() with
        {
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            Colors = [new(.25f,.5f,1,1), new(.25f,.5f,1,1), new(.25f,.5f,1,1)],
            TextureCoordinates = [Triangle().TextureCoordinates![0],
                new Vector2[] { new(float.NaN), new(float.NaN), new(float.NaN) }]
        };
        var root = Input(input, input == MaterialInput.VertexColor ? MaterialValueType.Vector4 : MaterialValueType.Vector3);
        var map = MaterialSurfaceMapBaker.Bake(Material(root), primitive, new Dictionary<int, MaterialBakeTexture>(), 2, 2, Reserve);
        Assert.Equal(new byte[] { (byte)r,(byte)g,(byte)b,255 }, map.Surface.Rgba8.Span[..4].ToArray());
    }

    [Fact]
    public void ColourAlphaRemainsFiniteCheckedEvenThoughCoverageComesFromAnotherRoot()
    {
        var first = new MaterialExpression(MaterialExpressionOperation.Constant, MaterialValueType.Vector4, [], [1,1,1,float.MaxValue]);
        var second = new MaterialExpression(MaterialExpressionOperation.Constant, MaterialValueType.Vector4, [], [1,1,1,2]);
        var color = new MaterialExpression(MaterialExpressionOperation.Multiply, MaterialValueType.Vector4, [first, second]);
        Assert.Throws<InvalidOperationException>(() => MaterialSurfaceMapBaker.Bake(Material(color), Triangle(),
            new Dictionary<int, MaterialBakeTexture>(), 1, 1, Reserve));
    }

    [Fact]
    public void AtlasInterpolationRetainsTheExpectedInteriorPixel()
    {
        var uv = Input(MaterialInput.Uv0, MaterialValueType.Vector2);
        var color = new MaterialExpression(MaterialExpressionOperation.Construct, MaterialValueType.Vector3, [uv, Scalar(0)]);
        Vector2[] corners = [new(.125f,.125f), new(.875f,.125f), new(.125f,.875f)];
        var map = MaterialSurfaceMapBaker.Bake(Material(color), Triangle(), new Dictionary<int, MaterialBakeTexture>(), 4, 4, Reserve,
            textureCoordinateSet: 1, polygonCornerCoordinates: corners);
        Assert.Equal(new byte[] { 156,156,0,255 }, map.Surface.Rgba8.Span.Slice(20,4).ToArray());
        Assert.Same(corners, map.PolygonCornerCoordinates);
    }

    [Fact]
    public void SourceRootsAtTheExistingDepthLimitStillBake()
    {
        var source = Scalar(.25f);
        for (int depth = 1; depth < 64; depth++)
            source = new MaterialExpression(MaterialExpressionOperation.Abs, MaterialValueType.Scalar, [source]);
        var map = MaterialSurfaceMapBaker.Bake(Material(source, Scalar(.5f), Scalar(.5f)), Triangle(),
            new Dictionary<int, MaterialBakeTexture>(), 1, 1, Reserve);
        Assert.Equal(new byte[] { 137,137,137,128 }, map.Surface.Rgba8.ToArray());
    }

    [Fact]
    public void ASharedOutputBakeStillHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MaterialSurfaceMapBaker.Bake(Material(Scalar(.25f)), Triangle(),
            new Dictionary<int, MaterialBakeTexture>(), 1, 1, Reserve, cancellation.Token));
    }

    [Fact]
    public void GraphFingerprintRejectsCyclesBeforeCacheLookup()
    {
        var arguments = new List<MaterialExpression>();
        var cyclic = new MaterialExpression(MaterialExpressionOperation.Abs, MaterialValueType.Scalar, arguments);
        arguments.Add(cyclic);
        var graph = new MaterialSurfaceGraph(cyclic, null, null, [], [], []);
        Assert.Throws<ArgumentException>(() => MaterialSurfaceMapBaker.ComputeGraphHash(graph));
    }

    [Fact]
    public void IdenticalRasterInputsReuseOnlyThePreviouslyEvaluatedPixel()
    {
        var material = Material(Input(MaterialInput.VertexColor, MaterialValueType.Vector4));
        var (primitive, _) = OverlappingColors([new(.25f,.5f,1,1), new(.25f,.5f,1,1)]);
        var observer = new CounterObserver(material.Identity.IndexContentId);
        using var subscription = AssetLoadDiagnostics.Listener.Subscribe(observer, name => name == AssetLoadDiagnostics.CounterName);
        var map = MaterialSurfaceMapBaker.Bake(material, primitive, new Dictionary<int, MaterialBakeTexture>(), 2, 2, Reserve,
            textureCoordinateSet: 0);
        Assert.Equal(Enumerable.Range(0,4).SelectMany(_ => new byte[] { 137,188,255,255 }), map.Surface.Rgba8.ToArray());
        var counts = Assert.Single(observer.Events, item => item.Stage == "BakeRaster");
        Assert.Equal(3, counts.Values["ExactInputReuses"]);
        Assert.Equal(3, counts.Values["RasterPixelEvaluations"]);
        Assert.Equal(3, counts.Values["RevisitedTexels"]);
        Assert.InRange(counts.Values["MemoBytes"], 1, 32L*1024*1024);
    }

    [Fact]
    public void ChangedInputsWithinToleranceOverwriteAndReplaceTheMemoKey()
    {
        var material = Material(Input(MaterialInput.VertexColor, MaterialValueType.Vector4));
        var (primitive, _) = OverlappingColors([new(0,0,0,1), new(.0006f,.0006f,.0006f,1), new(0,0,0,1)]);
        var observer = new CounterObserver(material.Identity.IndexContentId);
        using var subscription = AssetLoadDiagnostics.Listener.Subscribe(observer, name => name == AssetLoadDiagnostics.CounterName);
        var map = MaterialSurfaceMapBaker.Bake(material, primitive, new Dictionary<int, MaterialBakeTexture>(), 2, 2, Reserve,
            textureCoordinateSet: 0);
        Assert.Equal(Enumerable.Range(0,4).SelectMany(_ => new byte[] { 0,0,0,255 }), map.Surface.Rgba8.ToArray());
        var counts = Assert.Single(observer.Events, item => item.Stage == "BakeRaster");
        Assert.Equal(0, counts.Values["ExactInputReuses"]);
        Assert.Equal(9, counts.Values["RasterPixelEvaluations"]);
        Assert.Equal(6, counts.Values["RevisitedTexels"]);
    }

    [Fact]
    public void DifferentInputBitsStillProduceTheOriginalOverlapError()
    {
        var value = new MaterialExpression(MaterialExpressionOperation.Swizzle, MaterialValueType.Scalar,
            [Input(MaterialInput.VertexColor, MaterialValueType.Vector4)], Components: "x");
        var inverse = new MaterialExpression(MaterialExpressionOperation.Subtract, MaterialValueType.Scalar, [Scalar(1), value]);
        var material = Material(new(MaterialExpressionOperation.Multiply, MaterialValueType.Scalar, [value, inverse]));
        var (primitive, _) = OverlappingColors([new(0,0,0,1), new(0,0,0,1)]);
        primitive = primitive with { Colors = [new(0,0,0,1),new(0,0,0,1),new(0,0,0,1),new(0,0,0,1),new(1,0,0,1),new(0,0,0,1)] };
        var error = Assert.Throws<MaterialBakeAtlasRequiredException>(() => MaterialSurfaceMapBaker.Bake(material, primitive,
            new Dictionary<int, MaterialBakeTexture>(), 2, 2, Reserve));
        Assert.Contains("Overlapping UVs", error.Message);
        Assert.Contains("at pixel", error.Message);
    }

    [Fact]
    public void EqualOutputBytesDoNotPermitReuseWhenAnotherRequiredVectorComponentChanges()
    {
        var material = Material(Input(MaterialInput.VertexColor, MaterialValueType.Vector4));
        var (primitive, _) = OverlappingColors([new(.25f,.5f,1,.5f), new(.25f,.5f,1,1)]);
        var observer = new CounterObserver(material.Identity.IndexContentId);
        using var subscription = AssetLoadDiagnostics.Listener.Subscribe(observer, name => name == AssetLoadDiagnostics.CounterName);
        var map = MaterialSurfaceMapBaker.Bake(material, primitive, new Dictionary<int, MaterialBakeTexture>(), 2, 2, Reserve,
            textureCoordinateSet: 0);
        Assert.Equal(Enumerable.Range(0,4).SelectMany(_ => new byte[] { 137,188,255,255 }), map.Surface.Rgba8.ToArray());
        var counts = Assert.Single(observer.Events, item => item.Stage == "BakeRaster");
        Assert.Equal(0, counts.Values["ExactInputReuses"]);
        Assert.Equal(6, counts.Values["RasterPixelEvaluations"]);
    }

    [Fact]
    public void MemoBudgetExhaustionFallsBackWithoutChangingBakedPixels()
    {
        var material = Material(Input(MaterialInput.VertexColor, MaterialValueType.Vector4));
        var (primitive, _) = OverlappingColors([new(.25f,.5f,1,1), new(.25f,.5f,1,1)]);
        bool attempted = false;
        IDisposable Limited(long bytes, string category)
        {
            if (category == "material raster input memo") { attempted = true; throw new ArchiveException("AssetMemoryLimit", "Generated budget limit."); }
            return new Reservation();
        }
        var map = MaterialSurfaceMapBaker.Bake(material, primitive, new Dictionary<int, MaterialBakeTexture>(), 2, 2, Limited,
            textureCoordinateSet: 0);
        Assert.True(attempted);
        Assert.Equal(Enumerable.Range(0,4).SelectMany(_ => new byte[] { 137,188,255,255 }), map.Surface.Rgba8.ToArray());
    }

    [Fact]
    public void DerivedAtlasDoesNotAllocateMemoEvenForRepeatedFaces()
    {
        var material = Material(Input(MaterialInput.VertexColor, MaterialValueType.Vector4));
        var (primitive, corners) = OverlappingColors([new(.25f,.5f,1,1), new(.25f,.5f,1,1)]);
        var observer = new CounterObserver(material.Identity.IndexContentId);
        using var subscription = AssetLoadDiagnostics.Listener.Subscribe(observer, name => name == AssetLoadDiagnostics.CounterName);
        var map = MaterialSurfaceMapBaker.Bake(material, primitive, new Dictionary<int, MaterialBakeTexture>(), 2, 2, Reserve,
            textureCoordinateSet: 1, polygonCornerCoordinates: corners);
        Assert.Equal(Enumerable.Range(0,4).SelectMany(_ => new byte[] { 137,188,255,255 }), map.Surface.Rgba8.ToArray());
        var counts = Assert.Single(observer.Events, item => item.Stage == "BakeRaster");
        Assert.Equal(0, counts.Values["MemoBytes"]);
        Assert.Equal(0, counts.Values["ExactInputReuses"]);
        Assert.Equal(6, counts.Values["RasterPixelEvaluations"]);
    }

    [Fact]
    public void UniqueSourceUvTriangleSignaturesDoNotAllocateMemo()
    {
        var material = Material(Input(MaterialInput.VertexColor, MaterialValueType.Vector4));
        var (primitive, _) = OverlappingColors([new(.25f,.5f,1,1), new(.25f,.5f,1,1)]);
        primitive = primitive with { TextureCoordinates = [new Vector2[]
            { Vector2.Zero,Vector2.UnitX,Vector2.UnitY, Vector2.UnitX,Vector2.One,Vector2.UnitY }] };
        var observer = new CounterObserver(material.Identity.IndexContentId);
        using var subscription = AssetLoadDiagnostics.Listener.Subscribe(observer, name => name == AssetLoadDiagnostics.CounterName);
        var map = MaterialSurfaceMapBaker.Bake(material, primitive, new Dictionary<int, MaterialBakeTexture>(), 2, 2, Reserve);
        Assert.Equal(Enumerable.Range(0,4).SelectMany(_ => new byte[] { 137,188,255,255 }), map.Surface.Rgba8.ToArray());
        var counts = Assert.Single(observer.Events, item => item.Stage == "BakeRaster");
        Assert.Equal(0, counts.Values["MemoBytes"]);
        Assert.Equal(0, counts.Values["ExactInputReuses"]);
    }

    [Fact]
    public void MapsAboveTheMemoCapSkipItsAllocationAndStillHonorCancellation()
    {
        var material = Material(Input(MaterialInput.VertexColor, MaterialValueType.Vector4));
        var (primitive, corners) = OverlappingColors([Vector4.One]);
        bool attempted = false;
        IDisposable Record(long bytes, string category)
        { if (category == "material raster input memo") attempted = true; return new Reservation(); }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MaterialSurfaceMapBaker.Bake(material, primitive,
            new Dictionary<int, MaterialBakeTexture>(), 1536, 1536, Record, cancellation.Token,
            textureCoordinateSet: 1, polygonCornerCoordinates: corners));
        Assert.False(attempted);
    }

    private static (MeshPrimitive Primitive, Vector2[] Corners) OverlappingColors(Vector4[] colors)
    {
        var positions = colors.SelectMany(_ => new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }).ToArray();
        var coordinates = colors.SelectMany(_ => new Vector2[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY }).ToArray();
        var primitive = new MeshPrimitive("surface", PrimitiveTopology.Triangles, positions,
            Enumerable.Range(0,positions.Length).Select(index => (uint)index).ToArray(), IndexElementFormat.UInt32, [],
            TextureCoordinates: [coordinates], Colors: colors.SelectMany(color => Enumerable.Repeat(color,3)).ToArray());
        return (primitive, coordinates);
    }

    private static float ExpectedDecode(byte value)
    {
        float normalized = value / 255f;
        return normalized <= .04045f ? normalized / 12.92f : MathF.Pow((normalized + .055f) / 1.055f, 2.4f);
    }

    private static MaterialExpression Input(MaterialInput input, MaterialValueType type) =>
        new(MaterialExpressionOperation.Input, type, [], Input: input);
    private static MaterialExpression Scalar(float value) => new(MaterialExpressionOperation.Constant, MaterialValueType.Scalar, [], [value]);
    private static MaterialAsset Material(MaterialExpression color, MaterialExpression? opacity = null, MaterialExpression? cutoff = null)
    {
        var identity = new ResourceIdentity("generated-bake-" + Guid.NewGuid().ToString("N"), 0);
        return new(identity, new(identity.IndexContentId, "generated-revision"), "generated", [], [], [])
        { SurfaceGraph = new(color, opacity, cutoff, [], [], []) };
    }
    private static MeshPrimitive Triangle() => new("surface", PrimitiveTopology.Triangles,
        [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0,1,2], IndexElementFormat.UInt16, [],
        TextureCoordinates: [[Vector2.Zero, Vector2.UnitX, Vector2.UnitY]]);
    private static MaterialBakeTexture Texture(int width, int height, byte[] rgba, bool srgb = false)
    {
        var bytes = new byte[148 + rgba.Length]; "DDS "u8.CopyTo(bytes);
        foreach (var (offset, value) in new (int, uint)[]
        { (4,124), (8,0x100f), (12,(uint)height), (16,(uint)width), (20,(uint)width*4), (76,32), (80,4), (108,0x1000), (128,srgb ? 29u : 28u), (132,3), (140,1) })
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        "DX10"u8.CopyTo(bytes.AsSpan(84)); rgba.CopyTo(bytes, 148);
        var surface = new DdsTextureDecoder().DecodeMip(new DdsTextureReader().Read(bytes));
        return new(surface, NativeTextureSamplingDecoder.Decode(0));
    }
    private static IDisposable Reserve(long bytes, string category) => new Reservation();
    private sealed class Reservation : IDisposable { public void Dispose() { } }
    private sealed class CounterObserver(string prefix) : IObserver<KeyValuePair<string, object?>>
    {
        public ConcurrentQueue<AssetLoadCounterMeasurement> Events { get; } = new();
        public void OnNext(KeyValuePair<string, object?> item)
        {
            if (item.Value is AssetLoadCounterMeasurement value && value.Key?.StartsWith(prefix, StringComparison.Ordinal) == true)
                Events.Enqueue(value);
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
