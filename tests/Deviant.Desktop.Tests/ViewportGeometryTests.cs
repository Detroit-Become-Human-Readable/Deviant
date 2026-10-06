using System.IO;
using System.Numerics;
using Deviant.Assets;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class ViewportGeometryTests
{
    [Theory]
    [InlineData(1f, 1f)]
    [InlineData(-1f, -1f)]
    public void PreservesAuthoredTangentHandednessAndNativeUvs(float sign, float binormalY)
    {
        var mesh = Triangle(sign);
        var result = ViewportGeometry.Convert(mesh, mesh.Submeshes[0], requireTangentFrame: true);
        Assert.Equal(Vector3.UnitZ, result.Normals![0]);
        Assert.Equal(Vector3.UnitX, result.Tangents![0]);
        Assert.Equal(new Vector3(0, binormalY, 0), result.BiTangents![0]);
        Assert.Equal(new Vector2(.25f, .75f), result.TextureCoordinates![0]);
    }

    [Fact]
    public void UsesOnlyTheRequestedTriangleRange()
    {
        var mesh = Triangle(1) with { Indices = [0, 1, 2, 2, 1, 0] };
        var result = ViewportGeometry.Convert(mesh, new("part", 3, 3, []));
        Assert.Equal([2, 1, 0], result.TriangleIndices!);
    }

    [Fact]
    public void RejectsMissingNormalMapAttributesAndInvalidTriangleIndices()
    {
        var mesh = Triangle(1);
        Assert.Throws<InvalidDataException>(() => ViewportGeometry.Convert(mesh with { Tangents = null }, mesh.Submeshes[0], requireTangentFrame: true));
        Assert.Throws<InvalidDataException>(() => ViewportGeometry.Convert(mesh with { Indices = [0, 1, 3] }, mesh.Submeshes[0]));
        Assert.Throws<InvalidDataException>(() => ViewportGeometry.Convert(mesh with { Positions = [new(float.NaN, 0, 0), Vector3.UnitX, Vector3.UnitY] }, mesh.Submeshes[0]));
    }

    [Fact]
    public void CancellationStopsConversionBeforePublishingGeometry()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var mesh = Triangle(1);
        Assert.Throws<OperationCanceledException>(() => ViewportGeometry.Convert(mesh, mesh.Submeshes[0], token: cancellation.Token));
    }

    [Fact]
    public void PublishesVertexColorsAndAccountsForTheirUpload()
    {
        var mesh = Triangle(1) with { Colors = [new(.2f, .4f, .6f, .8f), Vector4.One, Vector4.Zero] };
        var result = ViewportGeometry.Convert(mesh, mesh.Submeshes[0]);
        Assert.Equal(.2f, result.Colors![0].Red); Assert.Equal(.8f, result.Colors[0].Alpha);
        Assert.Equal(48, ViewportGeometry.GeometryBytes(3, 3, true) - ViewportGeometry.GeometryBytes(3, 3));
        Assert.Equal(48, ViewportGeometry.ConversionBytes(3, 3, true) - ViewportGeometry.ConversionBytes(3, 3));
    }

    [Fact]
    public void AccountsForUploadScratchSeparatelyFromRetainedGeometry()
    {
        Assert.Equal(3L * HelixToolkit.SharpDX.DefaultVertex.SizeInBytes,
            ViewportGeometry.ConversionBytes(3, 3, true) - ViewportGeometry.RetainedConversionBytes(3, 3, true));
        Assert.Equal(3L * 72 + 3L * 4, ViewportGeometry.RetainedConversionBytes(3, 3, true));
    }

    [Fact]
    public void DeferredUploadKeepsItsAdmissionUntilCallbackOrDisposal()
    {
        var budget = new Deviant.Browser.AssetMemoryBudget(1024);
        var mesh = Triangle(1);
        var geometry = (ViewportMeshGeometry)ViewportGeometry.Convert(mesh, mesh.Submeshes[0]);
        geometry.SetPendingUpload(budget.Reserve(156, "vertex upload scratch"));
        Assert.Equal(156, budget.UsedBytes);
        using (var callback = geometry.TakePendingUpload())
        { Assert.NotNull(callback); Assert.Equal(156, budget.UsedBytes); }
        Assert.Equal(0, budget.UsedBytes);
        geometry.SetPendingUpload(budget.Reserve(156, "vertex upload scratch"));
        geometry.ReleasePendingUpload(); geometry.ReleasePendingUpload();
        Assert.Equal(0, budget.UsedBytes);
    }

    [Fact]
    public void KeepsComposedParentRotationAndTranslation()
    {
        var local = Matrix4x4.CreateTranslation(2, 0, 0);
        var parent = Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 4, 3);
        var composed = local * parent;
        var adapted = ViewportGeometry.NativeMatrix(composed);
        Assert.Equal(composed, adapted);
        var point = Vector3.Transform(Vector3.Zero, adapted);
        Assert.InRange(point.X, 9.99999f, 10.00001f);
        Assert.InRange(point.Y, 3.99999f, 4.00001f);
        Assert.InRange(point.Z, .99999f, 1.00001f);
    }

    private static MeshPrimitive Triangle(float sign) => new("triangle", PrimitiveTopology.Triangles,
        [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2], IndexElementFormat.UInt16,
        [new("surface", 0, 3, [])], [Vector3.UnitZ * 2, Vector3.UnitZ * 2, Vector3.UnitZ * 2],
        [new(3, 0, 0, sign), new(3, 0, 0, sign), new(3, 0, 0, sign)],
        [[new(.25f, .75f), Vector2.UnitX, Vector2.UnitY]]);
}
