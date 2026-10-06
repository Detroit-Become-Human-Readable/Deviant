using System.IO;
using System.Runtime.InteropServices;
using Deviant.Assets;
using Deviant.Browser;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Shaders;
using HelixToolkit.Wpf.SharpDX;
using SharpDX;
using HelixToolkit.Maths;
using Vector2 = System.Numerics.Vector2;
using ShaderDescription = HelixToolkit.SharpDX.Shaders.ShaderDescription;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Windows.Threading;

namespace Deviant.Desktop;

/// <summary>Application-owned Direct3D resources shared by the scene and model viewports.</summary>
public sealed partial class AssetViewportService : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly Dictionary<string, GeometryEntry> geometries = [];
    private readonly Dictionary<string, MaterialEntry> materials = [];
    private readonly SharedTexturePool<TextureEntry> textures = new();
    private long gpuBytes;
    private bool disposed;
    private readonly DefaultEffectsManager effects;

    public AssetViewportService(Dispatcher? dispatcher = null)
    {
        this.dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        this.dispatcher.VerifyAccess();
        effects = new DefaultEffectsManager();
        try { RegisterNormalShader(effects); }
        catch { effects.Dispose(); throw; }
    }

    public IEffectsManager EffectsManager => effects;
    public long GpuBytes => gpuBytes;
    public long GpuLimitBytes => ReadGpuLimit();
    public long ReportedGpuBudgetBytes => ReadGpuBudget();

    private static void RegisterNormalShader(DefaultEffectsManager manager)
    {
        using var stream = typeof(AssetViewportService).Assembly.GetManifestResourceStream("Deviant.Desktop.Rendering.NormalPhong.hlsl")
            ?? throw new InvalidOperationException("The normal shader is missing.");
        using var reader = new StreamReader(stream);
        string source = reader.ReadToEnd().Replace("// HIDDEN_GRID_HELPER", ReadShader("HiddenGrid"), StringComparison.Ordinal);
        using var compiled = ShaderBytecode.Compile(source, "main", "ps_5_0", ShaderFlags.OptimizationLevel3);
        var pixel = new ShaderDescription("NormalPhong", ShaderStage.Pixel, new ShaderReflector(), compiled.Bytecode.Data);
        manager.RemoveTechnique(DefaultRenderTechniqueNames.InstancingMesh);
        manager.AddTechnique(new TechniqueDescription(DefaultRenderTechniqueNames.InstancingMesh)
        {
            InputLayoutDescription = new InputLayoutDescription(DefaultVSShaderByteCodes.VSMeshInstancing, DefaultInputLayout.VSInputInstancing),
            PassDescriptions = [new ShaderPassDescription(DefaultPassNames.Default)
            {
                ShaderList = [DefaultVSShaderDescriptions.VSMeshInstancing, pixel],
                BlendStateDescription = DefaultBlendStateDescriptions.BSAlphaBlend,
                DepthStencilStateDescription = DefaultDepthStencilDescriptions.DSSDepthLessEqual
            }]
        });
    }

    private long ReadGpuLimit()
        => Math.Min(512L * 1024 * 1024, ReadGpuBudget() / 4);

    private long ReadGpuBudget()
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        // Query the adapter backing the actual Helix device, including OS pressure.
        using var dxgiDevice = effects.Device!.QueryInterface<SharpDX.DXGI.Device>();
        using var adapter = dxgiDevice.Adapter;
        using var adapter3 = adapter.QueryInterface<Adapter3>();
        var budget = adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).Budget;
        if (budget <= 0) throw new InvalidOperationException("The graphics memory budget is unavailable.");
        return budget;
    }

    internal IDisposable ReserveGpu(long bytes, string category)
    {
        dispatcher.VerifyAccess();
        long limit = ReadGpuLimit();
        if (bytes < 0 || bytes > limit - gpuBytes)
            throw new InvalidOperationException($"The {category} exceeds the graphics memory limit.");
        gpuBytes = checked(gpuBytes + bytes);
        return new ActionLease(() => gpuBytes -= bytes);
    }

    internal async Task<GeometryLease> AcquireGeometryAsync(RenderAssetSnapshot snapshot, RenderMesh mesh,
        MeshPrimitive primitive, GeometrySubmesh submesh, int uvSet, bool normalMapped, CancellationToken token)
    {
        dispatcher.VerifyAccess();
        var key = $"{snapshot.Revision}/{mesh.Key}/{primitive.Key}/{submesh.Key}/{uvSet}/{normalMapped}";
        if (geometries.TryGetValue(key, out var cached)) { cached.References++; return new(this, key, cached); }
        bool colors = primitive.Colors?.Count == primitive.Positions.Count;
        long gpuSize = ViewportGeometry.GeometryBytes(primitive.Positions.Count, submesh.IndexCount, colors);
        var gpu = ReserveGpu(gpuSize, "mesh buffers");
        AssetMemoryBudget.Reservation? cpu = null;
        AssetMemoryBudget.Reservation? upload = null;
        ViewportMeshGeometry? preparedGeometry = null;
        try
        {
            cpu = snapshot.MemoryBudget.Reserve(ViewportGeometry.RetainedConversionBytes(primitive.Positions.Count, submesh.IndexCount, colors), "mesh conversion");
            // Admission happens on the load task; hidden or unattached meshes retain this until their upload callback.
            upload = snapshot.MemoryBudget.Reserve(checked((long)primitive.Positions.Count * HelixToolkit.SharpDX.DefaultVertex.SizeInBytes), "vertex upload scratch");
            var geometry = await Task.Run(() => ViewportGeometry.Convert(primitive, submesh, uvSet, normalMapped, token), token);
            preparedGeometry = (ViewportMeshGeometry)geometry;
            preparedGeometry.UploadBudget = snapshot.MemoryBudget;
            preparedGeometry.SetPendingUpload(upload); upload = null;
            token.ThrowIfCancellationRequested();
            dispatcher.VerifyAccess();
            // Another viewport can have finished the same conversion while this one awaited.
            if (geometries.TryGetValue(key, out cached))
            { preparedGeometry.ReleasePendingUpload(); gpu.Dispose(); cpu.Dispose(); cached.References++; return new(this, key, cached); }
            var entry = new GeometryEntry(geometry, cpu, gpu);
            geometries.Add(key, entry);
            return new(this, key, entry);
        }
        catch { preparedGeometry?.ReleasePendingUpload(); upload?.Dispose(); gpu.Dispose(); cpu?.Dispose(); throw; }
    }

    internal async Task<MaterialLease> AcquireMaterialAsync(RenderAssetSnapshot snapshot, RenderMaterial? source,
        MeshPrimitive primitive, CancellationToken token)
    {
        dispatcher.VerifyAccess();
        if (source is not null && source.Asset.Revision != snapshot.Revision)
            throw new InvalidDataException("The material belongs to another source revision.");
        var inputs = new HashSet<MaterialInput> { MaterialInput.WorldPosition, MaterialInput.WorldNormal, MaterialInput.ViewDirection };
        if (primitive.TextureCoordinates is { Count: > 0 } uv && uv[0].Count == primitive.Positions.Count) inputs.Add(MaterialInput.Uv0);
        if (primitive.Colors?.Count == primitive.Positions.Count) inputs.Add(MaterialInput.VertexColor);
        var primitiveNormalUvSets = primitive.TextureCoordinates is { } uvSets
            ? Enumerable.Range(0, uvSets.Count).Where(index => uvSets[index].Count == primitive.Positions.Count).ToHashSet()
            : new HashSet<int>();
        bool normalFrameAvailable = primitive.Normals?.Count == primitive.Positions.Count &&
            primitive.Tangents?.Count == primitive.Positions.Count;
        var key = $"{snapshot.Revision}/{source?.Asset.Identity.Key ?? "neutral"}/{string.Join(',', inputs.Order())}" +
            $"/normal-uv/{string.Join(',', primitiveNormalUvSets.Order())}/tbn/{normalFrameAvailable}";
        if (materials.TryGetValue(key, out var cached)) { cached.References++; return new(this, key, cached); }
        TextureLease? texture = null;
        GraphTechniqueLease? graphTechnique = null;
        var graphTextures = new List<TextureLease>();
        try
        {
            var diagnostics = new List<string>();
            if (source?.Asset.SurfaceGraph is { } graph)
            {
                var availableGraph = RestrictMissingTextures(graph, source.TexturesBySlot, diagnostics);
                graphTechnique = await AcquireGraphTechniqueAsync(snapshot, availableGraph, inputs, diagnostics, token);
                token.ThrowIfCancellationRequested();
                if (graphTechnique is not null)
                {
                    foreach (int slot in graphTechnique.TextureSlots)
                        graphTextures.Add(AcquireTexture(snapshot, source.TexturesBySlot[slot], normalMap: false));
                }
            }
            var availableNormalUvSets = primitiveNormalUvSets.Where(index => graphTechnique is null || index == 0).ToHashSet();
            var prepared = ViewportMaterials.Create(source, surface =>
            {
                texture = AcquireTexture(snapshot, surface, normalMap: true);
                return texture.Texture;
            }, availableNormalUvSets, normalFrameAvailable);
            diagnostics.AddRange(prepared.Diagnostics);
            PhongMaterial material = prepared.Material;
            if (graphTechnique is not null)
            {
                material = new SurfacePhongMaterial(prepared.Material, graphTextures.Select(x => x.Texture).ToArray());
                material.DiffuseMapSampler = SurfaceSampler();
            }
            // The surface VS consumes raw UV0. The normal affine transform remains a PS uniform.
            int uvSet = graphTechnique is null ? prepared.TextureCoordinateSet : 0;
            if (materials.TryGetValue(key, out cached))
            {
                foreach (var graphTexture in graphTextures) graphTexture.Dispose();
                graphTechnique?.Dispose(); texture?.Dispose(); cached.References++;
                return new(this, key, cached);
            }
            var entry = new MaterialEntry(material, texture, graphTextures, graphTechnique, uvSet, prepared.NormalMapped, diagnostics);
            materials.Add(key, entry);
            return new(this, key, entry);
        }
        catch
        {
            foreach (var graphTexture in graphTextures) graphTexture.Dispose();
            graphTechnique?.Dispose(); texture?.Dispose(); throw;
        }
    }

    private TextureLease AcquireTexture(RenderAssetSnapshot snapshot, RenderTexture source, bool normalMap)
    {
        var s = source.Surface;
        if (s.IsHdr || s.DisplayTransform is not null || s.Width <= 0 || s.Height <= 0 ||
            (long)s.Width * s.Height * 4 != s.Rgba8.Length)
            throw new InvalidDataException("The material requires an RGBA8 texture.");
        if (s.Revision is not null && s.Revision != snapshot.Revision)
            throw new InvalidDataException("The texture belongs to another source revision.");
        bool srgb = !normalMap && s.IsSrgb;
        var key = TextureContentKey.Create(source, snapshot.Revision, srgb);
        return new(this, textures.Acquire(key, () =>
        {
            var gpu = ReserveGpu(s.Rgba8.Length, "texture buffers");
            AssetMemoryBudget.Reservation? cpu = null;
            IDisposable? sourceLease = null;
            try
            {
                byte[] pixels;
                if (MemoryMarshal.TryGetArray(s.Rgba8, out var segment) && segment.Array is { } array &&
                    segment.Offset == 0 && segment.Count == array.Length)
                {
                    sourceLease = snapshot.AcquireLease();
                    pixels = array;
                }
                else
                {
                    cpu = snapshot.MemoryBudget.Reserve(s.Rgba8.Length, "texture upload copy");
                    pixels = s.Rgba8.ToArray();
                }
                var loader = new RawTextureLoader(pixels, s.Width, s.Height, srgb);
                var model = new TextureModel(Guid.NewGuid(), loader);
                return new TextureEntry(model, loader, cpu, gpu, sourceLease);
            }
            catch { gpu.Dispose(); cpu?.Dispose(); sourceLease?.Dispose(); throw; }
        }));
    }

    private void ReleaseGeometry(string key)
    {
        dispatcher.VerifyAccess();
        if (geometries.TryGetValue(key, out var e) && --e.References == 0)
        { geometries.Remove(key); ((ViewportMeshGeometry)e.Geometry).ReleasePendingUpload(); e.Cpu.Dispose(); e.Gpu.Dispose(); }
    }
    private void ReleaseMaterial(string key)
    {
        dispatcher.VerifyAccess();
        if (materials.TryGetValue(key, out var e) && --e.References == 0)
        {
            materials.Remove(key); e.Material.NormalMap = null;
            foreach (var texture in e.GraphTextures) texture.Dispose();
            e.GraphTechnique?.Dispose(); e.Texture?.Dispose();
        }
    }
    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        if (geometries.Count != 0 || materials.Count != 0 || textures.Count != 0 || graphTechniques.Count != 0)
            throw new InvalidOperationException("Close the asset viewports before disposing their shared service.");
        disposed = true;
        effects.Dispose();
    }

    internal sealed class RawTextureLoader(byte[] pixels, int width, int height, bool srgb) : ITextureInfoLoader, IDisposable
    {
        private byte[]? data = pixels;
        public TextureInfo Load(Guid id) => new(data ?? throw new ObjectDisposedException(nameof(RawTextureLoader)),
            srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm, width, height, generateMipMaps: false);
        public void Complete(Guid id, TextureInfo? info, bool succeeded)
        { if (!succeeded) throw new InvalidOperationException("The texture could not be uploaded."); }
        public void Dispose() => data = null;
    }
    private sealed class ActionLease(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
    internal sealed class GeometryEntry(MeshGeometry3D geometry, IDisposable cpu, IDisposable gpu)
    { public MeshGeometry3D Geometry { get; } = geometry; public IDisposable Cpu { get; } = cpu; public IDisposable Gpu { get; } = gpu; public int References = 1; }
    internal sealed class MaterialEntry(PhongMaterial material, TextureLease? texture, IReadOnlyList<TextureLease> graphTextures,
        GraphTechniqueLease? graphTechnique, int uvSet, bool normalMapped, IReadOnlyList<string> diagnostics)
    {
        public PhongMaterial Material { get; } = material; public TextureLease? Texture { get; } = texture;
        public IReadOnlyList<TextureLease> GraphTextures { get; } = graphTextures;
        public GraphTechniqueLease? GraphTechnique { get; } = graphTechnique;
        public IReadOnlyList<string> Diagnostics { get; } = diagnostics;
        public int UvSet { get; } = uvSet; public bool NormalMapped { get; } = normalMapped; public int References = 1;
    }
    internal sealed class TextureEntry(TextureModel texture, RawTextureLoader loader, IDisposable? cpu, IDisposable gpu,
        IDisposable? sourceLease) : IDisposable
    {
        public TextureModel Texture { get; } = texture;
        public void Dispose() { loader.Dispose(); cpu?.Dispose(); gpu.Dispose(); sourceLease?.Dispose(); }
    }
    internal sealed class GeometryLease(AssetViewportService owner, string key, GeometryEntry entry) : IDisposable
    { private AssetViewportService? current = owner; public MeshGeometry3D Geometry => entry.Geometry; public void Dispose() => Interlocked.Exchange(ref current, null)?.ReleaseGeometry(key); }
    internal sealed class MaterialLease(AssetViewportService owner, string key, MaterialEntry entry) : IDisposable
    {
        private AssetViewportService? current = owner; public PhongMaterial Material => entry.Material;
        public int UvSet => entry.UvSet; public bool NormalMapped => entry.NormalMapped;
        public string TechniqueName => entry.GraphTechnique?.TechniqueName ?? DefaultRenderTechniqueNames.InstancingMesh;
        public IReadOnlyList<string> Diagnostics => entry.Diagnostics;
        public bool SurfaceMapped => entry.GraphTechnique is not null;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.ReleaseMaterial(key);
    }
    internal sealed class TextureLease(AssetViewportService owner, SharedTexturePool<TextureEntry>.Lease lease) : IDisposable
    {
        private SharedTexturePool<TextureEntry>.Lease? current = lease;
        private readonly TextureModel texture = lease.Resource.Texture;
        public TextureModel Texture => texture;
        public void Dispose() { owner.dispatcher.VerifyAccess(); Interlocked.Exchange(ref current, null)?.Dispose(); }
    }
}
