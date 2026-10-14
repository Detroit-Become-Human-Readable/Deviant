using System.Numerics;
using Deviant.Assets;
using Deviant.Browser;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.SharpDX.Render;
using HelixToolkit.SharpDX.Utilities;
using SharpDX.Direct3D11;
using HelixToolkit;

namespace Deviant.Desktop;

internal sealed class ViewportMeshGeometry : MeshGeometry3D
{
    public bool Animated { get; set; }
    public AssetMemoryBudget? UploadBudget { get; set; }
    public IReadOnlyList<Vector2Collection> TextureCoordinateSets { get; set; } = [];
    private AssetMemoryBudget.Reservation? pendingUpload;
    public void SetPendingUpload(AssetMemoryBudget.Reservation reservation) => pendingUpload = reservation;
    public AssetMemoryBudget.Reservation? TakePendingUpload() => Interlocked.Exchange(ref pendingUpload, null);
    public void ReleasePendingUpload() => TakePendingUpload()?.Dispose();
}

/// <summary>Uses an exact temporary vertex array instead of Helix's retained thread buffer.</summary>
internal class ViewportMeshBuffer : DefaultMeshGeometryBufferModel
{
    private struct UvVertex
    {
        public Vector2 Uv0, Uv1, Uv2, Uv3;
        public const int SizeInBytes = 32;
    }
    public ViewportMeshBuffer() : this(false) { }
    protected ViewportMeshBuffer(bool animated) : base([
        animated ? new DynamicBufferProxy(DefaultVertex.SizeInBytes, BindFlags.VertexBuffer, ResourceOptionFlags.None, false)
            : new ImmutableBufferProxy(DefaultVertex.SizeInBytes, BindFlags.VertexBuffer),
        new ImmutableBufferProxy(UvVertex.SizeInBytes, BindFlags.VertexBuffer),
        new ImmutableBufferProxy(16, BindFlags.VertexBuffer)], false) { }
    protected override void OnCreateVertexBuffer(DeviceContextProxy context, IElementsBufferProxy buffer,
        int bufferIndex, Geometry3D? geometry, IDeviceResources? deviceResources)
    {
        using var timing = AssetLoadDiagnostics.Measure("VertexUpload");
        if (geometry is not ViewportMeshGeometry mesh || mesh.Positions is null || bufferIndex > 1)
        {
            base.OnCreateVertexBuffer(context, buffer, bufferIndex, geometry, deviceResources);
            return;
        }
        int count = mesh.Positions.Count;
        if (bufferIndex == 1)
        {
            using var uvScratch = mesh.UploadBudget?.Reserve(checked((long)count * UvVertex.SizeInBytes), "UV upload scratch");
            var coordinates = new UvVertex[count];
            for (var index = 0; index < count; index++)
                coordinates[index] = new UvVertex {
                    Uv0 = mesh.TextureCoordinates?[index] ?? Vector2.Zero,
                    Uv1 = Coordinate(1, index), Uv2 = Coordinate(2, index), Uv3 = Coordinate(3, index) };
            buffer.UploadDataToBuffer(context, coordinates, count, 0, geometry.PreDefinedVertexCount);
            return;
        }
        using var scratch = mesh.TakePendingUpload() ??
            mesh.UploadBudget?.Reserve(checked((long)count * DefaultVertex.SizeInBytes), "vertex upload scratch");
        var vertices = new DefaultVertex[count];
        for (int index = 0; index < count; index++)
        {
            vertices[index] = new DefaultVertex
            {
                Position = new Vector4(mesh.Positions[index], 1),
                Normal = mesh.Normals?[index] ?? Vector3.Zero,
                Tangent = mesh.Tangents?[index] ?? Vector3.Zero,
                BiTangent = mesh.BiTangents?[index] ?? Vector3.Zero
            };
        }
        buffer.UploadDataToBuffer(context, vertices, count, 0, geometry.PreDefinedVertexCount);

        Vector2 Coordinate(int set, int index) => set < mesh.TextureCoordinateSets.Count && mesh.TextureCoordinateSets[set].Count == count
            ? mesh.TextureCoordinateSets[set][index] : Vector2.Zero;
    }
}

internal sealed class ViewportAnimatedMeshBuffer : ViewportMeshBuffer
{ public ViewportAnimatedMeshBuffer() : base(true) { } }
