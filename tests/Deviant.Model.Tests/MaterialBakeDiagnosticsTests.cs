using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using Deviant.Assets;
using Deviant.Export.Models;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Model.Tests;

public sealed class MaterialBakeDiagnosticsTests
{
    [Fact]
    public void EnabledDiagnosticsReportAggregateWorkWithoutChangingBakedBytes()
    {
        var (material, primitive, textures) = Fixture();
        var baseline = MaterialSurfaceMapBaker.Bake(material, primitive, textures, 2, 2, Reserve);
        var observer = new Observer(material.Identity.IndexContentId);
        using var subscription = AssetLoadDiagnostics.Listener.Subscribe(observer,
            name => name is AssetLoadDiagnostics.MeasurementName or AssetLoadDiagnostics.CounterName);
        var measured = MaterialSurfaceMapBaker.Bake(material, primitive, textures, 2, 2, Reserve);
        Assert.Equal(baseline.Document.OriginalBytes.ToArray(), measured.Document.OriginalBytes.ToArray());
        Assert.Equal(baseline.Surface.Rgba8.ToArray(), measured.Surface.Rgba8.ToArray());
        Assert.Equal(baseline.GraphSha256, measured.GraphSha256);
        var counts = Assert.Single(observer.Events.Select(item => item.Value).OfType<AssetLoadCounterMeasurement>());
        Assert.Equal("BakeRaster", counts.Stage);
        Assert.Equal(2, counts.Values["Width"]);
        Assert.Equal(2, counts.Values["Height"]);
        Assert.Equal(4, counts.Values["OutputPixels"]);
        Assert.Equal(3, counts.Values["VertexPixelEvaluations"]);
        Assert.Equal(4, counts.Values["RasterPixelEvaluations"]);
        Assert.Equal(7, counts.Values["TextureSamples"]);
        Assert.Equal(2, counts.Values["GraphNodes"]);
        Assert.Equal(2, counts.Values["RootNodeOccurrences"]);
        Assert.Equal(4, counts.Values["CoveredTexels"]);
        Assert.Equal(0, counts.Values["RevisitedTexels"]);
        Assert.Equal(1, counts.Values["Completed"]);
        var timings = observer.Events.Select(item => item.Value).OfType<AssetLoadMeasurement>().ToArray();
        Assert.Equal(new[] { "BakeDds", "BakeGraphCompile", "BakePreflight", "BakeRaster" },
            timings.Select(timing => timing.Stage).Order(StringComparer.Ordinal));
        Assert.All(timings, timing => Assert.True(timing.ElapsedMilliseconds >= 0));
    }

    [Fact]
    public void CancelledPreflightEmitsPartialCountersAndOnlyReachedStageTimings()
    {
        var (material, primitive, textures) = Fixture();
        var observer = new Observer(material.Identity.IndexContentId);
        using var subscription = AssetLoadDiagnostics.Listener.Subscribe(observer,
            name => name is AssetLoadDiagnostics.MeasurementName or AssetLoadDiagnostics.CounterName);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            MaterialSurfaceMapBaker.Bake(material, primitive, textures, 2, 2, Reserve, cancellation.Token));
        var counts = Assert.Single(observer.Events.Select(item => item.Value).OfType<AssetLoadCounterMeasurement>());
        Assert.Equal(0, counts.Values["Completed"]);
        Assert.Equal(0, counts.Values["VertexPixelEvaluations"]);
        Assert.Equal(0, counts.Values["RasterPixelEvaluations"]);
        Assert.Equal(0, counts.Values["TextureSamples"]);
        Assert.Equal(new[] { "BakeGraphCompile", "BakePreflight" },
            observer.Events.Select(item => item.Value).OfType<AssetLoadMeasurement>().Select(item => item.Stage).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CounterPayloadIsCapturedAndFieldCountIsBounded()
    {
        var key = "generated-diagnostic-" + Guid.NewGuid().ToString("N");
        var observer = new Observer(key);
        using var subscription = AssetLoadDiagnostics.Listener.Subscribe(observer,
            name => name == AssetLoadDiagnostics.CounterName);
        var values = new Dictionary<string, long> { ["Pixels"] = 4 };
        AssetLoadDiagnostics.Counters("BakeRaster", key, values);
        values["Pixels"] = 8;
        var captured = Assert.Single(observer.Events.Select(item => item.Value).OfType<AssetLoadCounterMeasurement>());
        Assert.Equal(4, captured.Values["Pixels"]);
        var tooMany = Enumerable.Range(0, AssetLoadDiagnostics.MaximumCounterFields + 1)
            .ToDictionary(index => "Count" + index, index => (long)index);
        Assert.Throws<ArgumentException>(() => AssetLoadDiagnostics.Counters("BakeRaster", key, tooMany));
        Assert.Single(observer.Events);
    }

    private static (MaterialAsset Material, MeshPrimitive Primitive, IReadOnlyDictionary<int, MaterialBakeTexture> Textures) Fixture()
    {
        var identity = new ResourceIdentity("generated-diagnostic-" + Guid.NewGuid().ToString("N"), 0);
        var revision = new SourceRevision(identity.IndexContentId, "generated-revision");
        var uv = new MaterialExpression(MaterialExpressionOperation.Input, MaterialValueType.Vector2, [], Input: MaterialInput.Uv0);
        var color = new MaterialExpression(MaterialExpressionOperation.TextureSample, MaterialValueType.Vector4, [uv], TextureSlot: 0);
        var material = new MaterialAsset(identity, revision, "generated", [], [], [])
        { SurfaceGraph = new(color, null, null, [MaterialInput.Uv0], [0], []) };
        var primitive = new MeshPrimitive("surface", PrimitiveTopology.Triangles,
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2], IndexElementFormat.UInt16, [],
            TextureCoordinates: [[Vector2.Zero, Vector2.UnitX, Vector2.UnitY]]);
        var bytes = new byte[164]; "DDS "u8.CopyTo(bytes);
        foreach (var (offset, value) in new (int, uint)[]
        { (4,124), (8,0x100f), (12,2), (16,2), (20,8), (76,32), (80,4), (108,0x1000), (128,28), (132,3), (140,1) })
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        "DX10"u8.CopyTo(bytes.AsSpan(84));
        new byte[] { 32,64,96,255, 64,96,128,255, 96,128,160,255, 128,160,192,255 }.CopyTo(bytes, 148);
        var surface = new DdsTextureDecoder().DecodeMip(new DdsTextureReader().Read(bytes));
        return (material, primitive, new Dictionary<int, MaterialBakeTexture> { [0] = new(surface, NativeTextureSamplingDecoder.Decode(0)) });
    }

    private static IDisposable Reserve(long bytes, string category) => new Reservation();
    private sealed class Reservation : IDisposable { public void Dispose() { } }

    private sealed class Observer(string keyPrefix) : IObserver<KeyValuePair<string, object?>>
    {
        public ConcurrentQueue<KeyValuePair<string, object?>> Events { get; } = new();
        public void OnNext(KeyValuePair<string, object?> value)
        {
            var key = value.Value switch
            {
                AssetLoadMeasurement measurement => measurement.Key,
                AssetLoadCounterMeasurement counters => counters.Key,
                _ => null
            };
            if (key?.StartsWith(keyPrefix, StringComparison.Ordinal) == true) Events.Enqueue(value);
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
