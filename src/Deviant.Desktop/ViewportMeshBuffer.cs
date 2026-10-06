using System.Numerics;
using Deviant.Browser;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.SharpDX.Render;
using HelixToolkit.SharpDX.Utilities;

namespace Deviant.Desktop;

internal sealed class ViewportMeshGeometry : MeshGeometry3D
{
    public AssetMemoryBudget? UploadBudget { get; set; }
    private AssetMemoryBudget.Reservation? pendingUpload;
    public void SetPendingUpload(AssetMemoryBudget.Reservation reservation) => pendingUpload = reservation;
    public AssetMemoryBudget.Reservation? TakePendingUpload() => Interlocked.Exchange(ref pendingUpload, null);
    public void ReleasePendingUpload() => TakePendingUpload()?.Dispose();
}

/// <summary>Uses an exact temporary vertex array instead of Helix's retained thread buffer.</summary>
internal sealed class ViewportMeshBuffer : DefaultMeshGeometryBufferModel
{
    public ViewportMeshBuffer() { }
    protected override void OnCreateVertexBuffer(DeviceContextProxy context, IElementsBufferProxy buffer,
        int bufferIndex, Geometry3D? geometry, IDeviceResources? deviceResources)
    {
        if (bufferIndex != 0 || geometry is not ViewportMeshGeometry mesh || mesh.Positions is null)
        {
            base.OnCreateVertexBuffer(context, buffer, bufferIndex, geometry, deviceResources);
            return;
        }
        int count = mesh.Positions.Count;
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
    }
}
