using System.Diagnostics;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Deviant.Assets;
using Deviant.Browser;
using HelixToolkit.SharpDX;
using SharpDX.Direct3D11;
using SampleDescription = SharpDX.DXGI.SampleDescription;
using Xunit;
using Xunit.Abstractions;
using PerspectiveCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;

namespace Deviant.Desktop.Tests;

[Collection("WPF")]
public sealed class ViewportSceneDepthTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpaqueDepthAndCutoutCoverageReachTheTransparentShader(bool cutout) => Run(() =>
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var budget = new AssetMemoryBudget();
        using var service = new AssetViewportService(Dispatcher.CurrentDispatcher);
        using var snapshot = Snapshot(budget, cutout);
        var view = new AssetViewport(); view.SetService(service);
        view.Viewport.ShowViewCube = false;
        var window = new Window { Content = view, Width = 256, Height = 256, Left = -10000, Top = -10000,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        int frames = 0;
        view.Viewport.OnRendered += (_, _) => frames++;
        try
        {
            window.Show();
            Pump(view.LoadSnapshotAsync(snapshot));
            var camera = Assert.IsType<PerspectiveCamera>(view.Viewport.Camera);
            camera.Position = new Point3D(0, 0, 5); camera.LookDirection = new Vector3D(0, 0, -5);
            camera.UpDirection = new Vector3D(0, 1, 0); camera.NearPlaneDistance = .1; camera.FarPlaneDistance = 20;
            int before = frames; view.Viewport.InvalidateRender();
            Pump(Wait(() => frames >= before + 3 || view.LastRenderException is not null));
            Assert.Null(view.LastRenderException);
            Assert.Empty(view.RendererIssues);
            var buffer = view.Viewport.RenderHost!.RenderBuffer!;
            Assert.True(buffer.HasMSAA);
            Assert.NotSame(buffer.DepthStencilBuffer, buffer.DepthStencilBufferNoMSAA);
            var depthProxy = buffer.DepthStencilBufferNoMSAA ?? throw new InvalidOperationException("The depth prepass target is unavailable.");
            var device = buffer.Device ?? throw new InvalidOperationException("The render device is unavailable.");
            using var depth = depthProxy.Resource!.QueryInterface<Texture2D>();
            output.WriteLine($"Depth format {depth.Description.Format}, size {depth.Description.Width}x{depth.Description.Height}, opaque {view.Viewport.RenderHost.PerFrameOpaqueNodesInFrustum.Count}, frames {frames}");
            float raw = ReadDepth(device, depth);
            float expectedDepth = cutout ? 1f : (float)(20 / 19.9 - 2 / (19.9 * 5));
            Assert.InRange(raw, expectedDepth - .000002f, expectedDepth + .000002f);
            var colorProxy = buffer.ColorBuffer ?? throw new InvalidOperationException("The color target is unavailable.");
            using var color = colorProxy.Resource!.QueryInterface<Texture2D>();
            byte[] pixel = ReadColor(device, color);
            int expectedColor = cutout ? 255 : 51;
            foreach (byte channel in pixel.Take(3)) Assert.InRange((int)channel, expectedColor - 3, Math.Min(255, expectedColor + 3));
            // An off-axis point has a longer eye distance but the same camera-space depth.
            byte[] offAxis = ReadColor(device, color, .9);
            foreach (byte channel in offAxis.Take(3)) Assert.InRange((int)channel, expectedColor - 3, Math.Min(255, expectedColor + 3));
            Pump(view.UnloadAsync());
            Assert.Equal(MSAALevel.Disable, view.Viewport.MSAA);
            service.ReleaseUnusedResources();
            service.ReleaseShaderBytecodeCache();
            Assert.Equal(0, service.GpuBytes);
            Assert.Equal(0, budget.UsedBytes);
        }
        finally { Pump(view.DisposeAsync().AsTask()); window.Close(); }
    });

    private static RenderAssetSnapshot Snapshot(AssetMemoryBudget budget, bool cutout)
    {
        var revision = new SourceRevision("generated-depth-scene", "revision");
        var meshes = new Dictionary<string, RenderMesh>(); var materials = new Dictionary<ResourceIdentity, RenderMaterial>();
        var occurrences = new List<RenderOccurrence>();
        for (int index = 0; index < 2; index++)
        {
            var identity = new ResourceIdentity(revision.IndexContentId, index);
            float z = index;
            var primitive = new MeshPrimitive("plane", PrimitiveTopology.Triangles,
                [new(-2,-2,z), new(2,-2,z), new(2,2,z), new(-2,2,z)], [0,1,2,0,2,3], IndexElementFormat.UInt16,
                [new("plane",0,6,[])], Normals: Enumerable.Repeat(Vector3.UnitZ,4).ToArray(),
                Bounds: new(new(-2,-2,z),new(2,2,z)));
            var geometry = new GeometryAsset(new("generated", "1"), [], [primitive], new Dictionary<string,string>());
            var mesh = new RenderMesh(identity, 0, geometry, new Dictionary<string,ResourceIdentity> { ["plane"] = identity });
            meshes.Add(mesh.Key,mesh);
            occurrences.Add(new("plane"+index,identity,identity,"Plane",mesh.Key,Matrix4x4.Identity,primitive.Bounds!,[]));
            var opacity = index == 0 ? Constant(cutout ? 0 : 1) : new MaterialExpression(MaterialExpressionOperation.Divide,
                MaterialValueType.Scalar, [new(MaterialExpressionOperation.Subtract,MaterialValueType.Scalar,
                    [Input(MaterialInput.SceneViewDepth),Input(MaterialInput.ViewDepth)]), Constant(5)]);
            var graph = new MaterialSurfaceGraph(Constant(index,index,index),opacity,index == 0 && cutout ? Constant(.5f) : null,
                index == 0 ? [] : [MaterialInput.SceneViewDepth,MaterialInput.ViewDepth],[],[]) { IsUnlit = true };
            var asset = new MaterialAsset(identity,revision,"surface-expression",[],[],[])
            { SurfaceGraph = graph, NativeRenderParameters = new(1,(uint)index,0x400,0,0,new(identity,0,35)) };
            materials.Add(identity,new(asset,new Dictionary<int,RenderTexture>()));
        }
        return new(revision,meshes,occurrences,materials,new(new(-2,-2,0),new(2,2,1)),[],[],new(0,0,0,0,0,0),[],budget);
    }

    private static MaterialExpression Constant(params float[] values) => new(MaterialExpressionOperation.Constant,(MaterialValueType)values.Length,[],Constants:values);
    private static MaterialExpression Input(MaterialInput input) => new(MaterialExpressionOperation.Input,MaterialValueType.Scalar,[],Input:input);
    private static float ReadDepth(Device device, Texture2D source)
    {
        var desc=source.Description; desc.BindFlags=BindFlags.None; desc.Usage=ResourceUsage.Staging; desc.CpuAccessFlags=CpuAccessFlags.Read; desc.OptionFlags=ResourceOptionFlags.None;
        using var staging=new Texture2D(device,desc); device.ImmediateContext.CopyResource(source,staging);
        var mapped=device.ImmediateContext.MapSubresource(staging,0,MapMode.Read,MapFlags.None);
        try
        {
            int stride = desc.Format is SharpDX.DXGI.Format.R32G8X24_Typeless or SharpDX.DXGI.Format.D32_Float_S8X24_UInt ? 8 : 4;
            int stored = Marshal.ReadInt32(mapped.DataPointer,desc.Height/2*mapped.RowPitch+desc.Width/2*stride);
            return desc.Format is SharpDX.DXGI.Format.R24G8_Typeless or SharpDX.DXGI.Format.D24_UNorm_S8_UInt
                ? (stored & 0xFFFFFF) / 16777215f : BitConverter.Int32BitsToSingle(stored);
        }
        finally { device.ImmediateContext.UnmapSubresource(staging,0); }
    }
    private static byte[] ReadColor(Device device, Texture2D source, double horizontalFraction = .5)
    {
        var desc=source.Description; desc.SampleDescription=new SampleDescription(1,0); desc.BindFlags=BindFlags.None; desc.OptionFlags=ResourceOptionFlags.None;
        using var resolved=new Texture2D(device,desc);
        if(source.Description.SampleDescription.Count>1)device.ImmediateContext.ResolveSubresource(source,0,resolved,0,desc.Format);
        else device.ImmediateContext.CopyResource(source,resolved);
        desc.Usage=ResourceUsage.Staging; desc.CpuAccessFlags=CpuAccessFlags.Read;
        using var staging=new Texture2D(device,desc); device.ImmediateContext.CopyResource(resolved,staging);
        var mapped=device.ImmediateContext.MapSubresource(staging,0,MapMode.Read,MapFlags.None);
        try { var pixel=new byte[4]; Marshal.Copy(mapped.DataPointer+desc.Height/2*mapped.RowPitch+(int)(desc.Width*horizontalFraction)*4,pixel,0,4); return pixel; }
        finally { device.ImmediateContext.UnmapSubresource(staging,0); }
    }
    private static async Task Wait(Func<bool> ready)
    { var watch=Stopwatch.StartNew(); while(!ready()){if(watch.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException("The depth frame did not render."); await Task.Delay(20);} }
    private static void Pump(Task task)
    { var frame=new DispatcherFrame(); var dispatcher=Dispatcher.CurrentDispatcher; _=task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult(); }
    private static void Run(Action action)
    { Exception? failure=null; var thread=new Thread(()=>{SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));try{action();}catch(Exception e){failure=e;}}); thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(60)),"Depth render check timed out.");if(failure is not null)ExceptionDispatchInfo.Capture(failure).Throw(); }
}
