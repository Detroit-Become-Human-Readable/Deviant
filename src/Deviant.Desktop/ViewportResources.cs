using System.IO;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Formats.Textures;
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
    private readonly ViewportResourceCache unusedGeometries = new();
    private readonly Dictionary<string, MaterialEntry> materials = [];
    private readonly SharedTexturePool<TextureEntry> textures = new();
    private long gpuBytes;
    private bool disposed;
    private readonly DefaultEffectsManager effects;
    private readonly byte[] normalPixelBytecode;
    private int geometryDeviceGeneration;

    public AssetViewportService(Dispatcher? dispatcher = null)
    {
        this.dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        this.dispatcher.VerifyAccess();
        effects = new DefaultEffectsManager();
        try { normalPixelBytecode = RegisterNormalShader(effects); }
        catch { effects.Dispose(); throw; }
        effects.DisposingResources += OnGeometryDeviceInvalidated;
        effects.Reinitialized += OnGeometryDeviceInvalidated;
    }

    public IEffectsManager EffectsManager => effects;
    public long GpuBytes => gpuBytes;
    public long GpuLimitBytes => ReadGpuLimit();
    public long ReportedGpuBudgetBytes => ReadGpuBudget();

    public ViewportResourceCacheStatistics GeometryReturnCacheStatistics
    {
        get
        {
            dispatcher.VerifyAccess();
            var unused = geometries.Values.Where(entry => entry.References == 0).ToArray();
            var sizes = unused.Select(NativeGeometryBufferBytes).ToArray();
            return new(unusedGeometries.Count, unusedGeometries.CpuBytes, unusedGeometries.GpuBytes,
                sizes.Count(bytes => bytes > 0), sizes.Sum(), unusedGeometries.Hits, unusedGeometries.Evictions);
        }
    }

    public void EnsureResourceAdmission(AssetMemoryBudget budget, long requiredCpuBytes, long requiredGpuBytes = 0)
    {
        dispatcher.VerifyAccess(); ArgumentNullException.ThrowIfNull(budget);
        if (requiredCpuBytes < 0 || requiredGpuBytes < 0) throw new ArgumentOutOfRangeException(nameof(requiredCpuBytes));
        long limit = ReadGpuLimit();
        unusedGeometries.TrimUntil(() => requiredCpuBytes <= budget.MaximumBytes - budget.UsedBytes && requiredGpuBytes <= limit - gpuBytes);
    }

    public void ReleaseUnusedResources() { dispatcher.VerifyAccess(); unusedGeometries.Clear(); }

    public void DrainResourceCache()
    {
        dispatcher.VerifyAccess(); ReleaseUnusedResources();
        if (geometries.Count != 0 || materials.Count != 0 || textures.Count != 0 || graphTechniques.Count != 0)
            throw new InvalidOperationException("Close the asset viewports before draining their resources.");
    }

    private void OnGeometryDeviceInvalidated(object? sender, EventArgs args)
    {
        Interlocked.Increment(ref geometryDeviceGeneration);
        if (dispatcher.CheckAccess()) ReleaseUnusedResources();
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke((Action)ReleaseUnusedResources, DispatcherPriority.Send);
    }

    private static byte[] RegisterNormalShader(DefaultEffectsManager manager)
    {
        using var stream = typeof(AssetViewportService).Assembly.GetManifestResourceStream("Deviant.Desktop.Rendering.NormalPhong.hlsl")
            ?? throw new InvalidOperationException("The normal shader is missing.");
        using var reader = new StreamReader(stream);
        string source = reader.ReadToEnd().Replace("// HIDDEN_GRID_HELPER", ReadShader("HiddenGrid"), StringComparison.Ordinal);
        using var compiled = ShaderBytecode.Compile(source, "main", "ps_5_0", ShaderFlags.OptimizationLevel3);
        var bytecode = compiled.Bytecode.Data;
        var pixel = new ShaderDescription("NormalPhong", ShaderStage.Pixel, new ShaderReflector(), bytecode);
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
        return bytecode;
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
        if (bytes >= 0) unusedGeometries.TrimUntil(() => bytes <= limit - gpuBytes);
        if (bytes < 0 || bytes > limit - gpuBytes)
            throw new InvalidOperationException($"The {category} exceeds the graphics memory limit.");
        gpuBytes = checked(gpuBytes + bytes);
        return new ActionLease(() => gpuBytes -= bytes);
    }

    internal async Task<GeometryLease> AcquireGeometryAsync(RenderAssetSnapshot snapshot, RenderMesh mesh,
        MeshPrimitive primitive, GeometrySubmesh submesh, int uvSet, bool normalMapped, CancellationToken token)
    {
        using var timing = AssetLoadDiagnostics.Measure("ViewportGeometry", primitive.Key);
        dispatcher.VerifyAccess();
        int deviceGeneration = Volatile.Read(ref geometryDeviceGeneration);
        var key = $"{snapshot.Revision}/{mesh.Key}/{mesh.Pose}/{primitive.Key}/{submesh.Key}/{uvSet}/{normalMapped}/device/{deviceGeneration}";
        bool geometryHit = geometries.TryGetValue(key, out var cached);
        AssetLoadDiagnostics.Cache("GeometryCache", key, geometryHit);
        if (geometryHit)
        {
            if (cached!.References == 0) unusedGeometries.Take(key);
            cached.References++; return new(this, key, cached);
        }
        bool colors = primitive.Colors?.Count == primitive.Positions.Count;
        long gpuSize = ViewportGeometry.GeometryBytes(primitive.Positions.Count, submesh.IndexCount, colors);
        long cpuSize = ViewportGeometry.RetainedConversionBytes(primitive.Positions.Count, submesh.IndexCount, colors);
        long uploadSize = checked((long)primitive.Positions.Count * HelixToolkit.SharpDX.DefaultVertex.SizeInBytes);
        EnsureResourceAdmission(snapshot.MemoryBudget, checked(cpuSize + uploadSize), gpuSize);
        var gpu = ReserveGpu(gpuSize, "mesh buffers");
        AssetMemoryBudget.Reservation? cpu = null;
        AssetMemoryBudget.Reservation? upload = null;
        ViewportMeshGeometry? preparedGeometry = null;
        IGeometryBufferModel? ownedBuffer = null;
        try
        {
            cpu = snapshot.MemoryBudget.Reserve(cpuSize, "mesh conversion");
            // Admission happens on the load task; hidden or unattached meshes retain this until their upload callback.
            upload = snapshot.MemoryBudget.Reserve(uploadSize, "vertex upload scratch");
            var geometry = await Task.Run(() => ViewportGeometry.Convert(primitive, submesh, uvSet, normalMapped, token), token);
            preparedGeometry = (ViewportMeshGeometry)geometry;
            preparedGeometry.UploadBudget = snapshot.MemoryBudget;
            preparedGeometry.SetPendingUpload(upload); upload = null;
            token.ThrowIfCancellationRequested();
            dispatcher.VerifyAccess();
            if (deviceGeneration != Volatile.Read(ref geometryDeviceGeneration))
                throw new OperationCanceledException("The geometry device changed during preparation.", token);
            // Another viewport can have finished the same conversion while this one awaited.
            if (geometries.TryGetValue(key, out cached))
            { preparedGeometry.ReleasePendingUpload(); gpu.Dispose(); cpu.Dispose(); if (cached.References == 0) unusedGeometries.Take(key); cached.References++; return new(this, key, cached); }
            // Helix keys registrations by buffer type and Geometry.GUID. This independent reference
            // keeps the actual vertex/index proxies alive after a scene node releases its reference.
            ownedBuffer = effects.GeometryBufferManager?.Register<ViewportMeshBuffer>(Guid.NewGuid(), geometry)
                ?? throw new InvalidOperationException("The geometry buffer manager is unavailable.");
            var entry = new GeometryEntry(geometry, cpu, gpu, ownedBuffer, cpuSize, gpuSize, deviceGeneration,
                canRetain: mesh.Pose is null && !preparedGeometry.Animated);
            geometries.Add(key, entry);
            ownedBuffer = null;
            return new(this, key, entry);
        }
        catch { ownedBuffer?.Dispose(); preparedGeometry?.ReleasePendingUpload(); upload?.Dispose(); gpu.Dispose(); cpu?.Dispose(); throw; }
    }

    internal async Task<MaterialLease> AcquireMaterialAsync(RenderAssetSnapshot snapshot, RenderMaterial? source,
        MeshPrimitive primitive, CancellationToken token)
    {
        using var timing = AssetLoadDiagnostics.Measure("ViewportMaterial", source?.Asset.Identity.Key);
        dispatcher.VerifyAccess();
        if (source is not null && source.Asset.Revision != snapshot.Revision)
            throw new InvalidDataException("The material belongs to another source revision.");
        var inputs = new HashSet<MaterialInput> { MaterialInput.WorldPosition, MaterialInput.WorldNormal, MaterialInput.ViewDirection, MaterialInput.ViewNormal, MaterialInput.ViewDepth, MaterialInput.SceneViewDepth };
        if (primitive.TextureCoordinates is { } uv)
            for (var index = 0; index < Math.Min(4, uv.Count); index++)
                if (uv[index].Count == primitive.Positions.Count) inputs.Add((MaterialInput)((int)MaterialInput.Uv0 + index));
        if (primitive.Colors?.Count == primitive.Positions.Count) inputs.Add(MaterialInput.VertexColor);
        var primitiveNormalUvSets = primitive.TextureCoordinates is { } uvSets
            ? Enumerable.Range(0, uvSets.Count).Where(index => uvSets[index].Count == primitive.Positions.Count).ToHashSet()
            : new HashSet<int>();
        bool normalsAvailable = ViewportGeometry.HasUsableNormals(primitive, token);
        bool normalFrameAvailable = ViewportGeometry.HasUsableTangentFrame(primitive, token);
        var state = ViewportMaterialStates.Create(source?.Asset.NativeRenderParameters);
        var key = $"{snapshot.Revision}/{source?.Asset.Identity.Key ?? "neutral"}/{string.Join(',', inputs.Order())}" +
            $"/normal-uv/{string.Join(',', primitiveNormalUvSets.Order())}/normals/{normalsAvailable}/tbn/{normalFrameAvailable}" +
            $"/samplers/{string.Join(';', source?.TexturesBySlot.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value.Sampling?.RawWord}") ?? [])}/state/{state.Key}";
        bool materialHit = materials.TryGetValue(key, out var cached);
        AssetLoadDiagnostics.Cache("MaterialCache", key, materialHit);
        if (materialHit) { TrackMaterialShaderUse(snapshot, cached!.GraphTechnique); cached.References++; return new(this, key, cached); }
        TextureLease? texture = null;
        GraphTechniqueLease? graphTechnique = null;
        var graphTextures = new List<TextureLease>();
        try
        {
            var diagnostics = new List<string>();
            if (!normalsAvailable)
                diagnostics.Add("Normal: Face normals used for display.");
            if (source?.Asset.NativeRenderParameters is { BlendSelector: >= 8 } unavailableState)
                diagnostics.Add($"Blend: Mode {unavailableState.BlendSelector} is unavailable.");
            if (source?.Asset.SurfaceGraph is { } graph)
            {
                var availableGraph = RestrictMissingTextures(graph, source.TexturesBySlot, diagnostics);
                graphTechnique = await AcquireGraphTechniqueAsync(snapshot, availableGraph, inputs, source.TexturesBySlot, state, diagnostics, token);
                token.ThrowIfCancellationRequested();
                if (graphTechnique is not null)
                {
                    foreach (int slot in graphTechnique.TextureSlots)
                        graphTextures.Add(await AcquireTextureAsync(snapshot, source.TexturesBySlot[slot], normalMap: false, token));
                }
            }
            if (graphTechnique is null && source?.Asset.NativeRenderParameters is not null)
                graphTechnique = AcquireNormalTechnique(snapshot, state);
            var availableNormalUvSets = primitiveNormalUvSets.Where(index => graphTechnique?.IsSurfaceGraph != true || index == 0).ToHashSet();
            var normalTexture = ViewportMaterials.FindUsableNormalTexture(source, availableNormalUvSets, normalFrameAvailable);
            if (normalTexture is not null) texture = await AcquireTextureAsync(snapshot, normalTexture, normalMap: true, token);
            var prepared = ViewportMaterials.Create(source, surface =>
            {
                if (!ReferenceEquals(surface, normalTexture) || texture is null)
                    throw new InvalidDataException("The normal texture changed during material preparation.");
                return texture.Texture;
            }, availableNormalUvSets, normalFrameAvailable);
            diagnostics.AddRange(prepared.Diagnostics);
            PhongMaterial material = prepared.Material;
            if (graphTechnique is not null)
            {
                material = new SurfacePhongMaterial(prepared.Material, graphTextures.Select(x => x.Texture).ToArray(),
                    graphTechnique.TextureSlots.Select(slot => SurfaceSampler(source!.TexturesBySlot[slot].Sampling)).ToArray(), graphTechnique.SamplerSlots,
                    graphTechnique.RequiresSceneDepth);
            }
            // The surface VS consumes raw UV0. The normal affine transform remains a PS uniform.
            int uvSet = graphTechnique?.IsSurfaceGraph == true ? 0 : prepared.TextureCoordinateSet;
            if (materials.TryGetValue(key, out cached))
            {
                foreach (var graphTexture in graphTextures) graphTexture.Dispose();
                graphTechnique?.Dispose(); texture?.Dispose(); cached.References++;
                TrackMaterialShaderUse(snapshot, cached.GraphTechnique);
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

    private async Task<TextureLease> AcquireTextureAsync(RenderAssetSnapshot snapshot, RenderTexture source,
        bool normalMap, CancellationToken token)
    {
        if (source.Surface.PixelSource is null) return AcquireTexture(snapshot, source, normalMap);
        var surface = source.Surface;
        if (surface.IsHdr || surface.DisplayTransform is not null)
            throw new InvalidDataException("The material requires an RGBA8 texture.");
        bool srgb = !normalMap && surface.IsSrgb;
        var key = TextureContentKey.Create(source, snapshot.Revision, srgb);
        if (textures.TryAcquire(key, out var existing)) return new(this, existing!);
        EnsureResourceAdmission(snapshot.MemoryBudget, surface.PixelSource.ByteLength, surface.PixelSource.ByteLength);
        IDisposable? held = snapshot.AcquireLease();
        RawTextureLoader? loader = null;
        try
        {
            loader = await RawTextureLoader.CreateBackedAsync(surface, srgb, snapshot.MemoryBudget, token);
            token.ThrowIfCancellationRequested();
            var leased = textures.Acquire(key, () =>
            {
                var gpu = ReserveGpu(surface.PixelSource.ByteLength, "texture buffers");
                try
                {
                    var model = new TextureModel(Guid.NewGuid(), loader);
                    var entry = new TextureEntry(model, loader, null, gpu, held);
                    loader = null; held = null; return entry;
                }
                catch { gpu.Dispose(); throw; }
            });
            return new(this, leased);
        }
        finally { loader?.Dispose(); held?.Dispose(); }
    }

    private TextureLease AcquireTexture(RenderAssetSnapshot snapshot, RenderTexture source, bool normalMap)
    {
        var s = source.Surface;
        bool srgb = !normalMap && s.IsSrgb;
        if (s.EncodedDds is { } encoded)
        {
            var encodedKey = TextureContentKey.CreateEncoded(source, snapshot.Revision, srgb);
            return new(this, textures.Acquire(encodedKey, () =>
            {
                var gpu = ReserveGpu(encoded.Info.EncodedLength, "texture buffers");
                IDisposable? sourceLease = null;
                RawTextureLoader? loader = null;
                try
                {
                    sourceLease = snapshot.AcquireLease();
                    loader = RawTextureLoader.CreateEncoded2D(encoded, snapshot.Revision, srgb, snapshot.MemoryBudget);
                    var model = new TextureModel(Guid.NewGuid(), loader);
                    return new TextureEntry(model, loader, null, gpu, sourceLease);
                }
                catch { loader?.Dispose(); sourceLease?.Dispose(); gpu.Dispose(); throw; }
            }));
        }
        if (s.IsHdr || s.DisplayTransform is not null || s.Width <= 0 || s.Height <= 0 ||
            (long)s.Width * s.Height * 4 != s.Rgba8.Length)
            throw new InvalidDataException("The material requires an RGBA8 texture.");
        if (s.Revision is not null && s.Revision != snapshot.Revision)
            throw new InvalidDataException("The texture belongs to another source revision.");
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
                    EnsureResourceAdmission(snapshot.MemoryBudget, s.Rgba8.Length);
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
        {
            ((ViewportMeshGeometry)e.Geometry).ReleasePendingUpload();
            if (e.CanRetain && e.DeviceGeneration == Volatile.Read(ref geometryDeviceGeneration) &&
                e.Buffer is DisposeObject { IsDisposed: false } &&
                unusedGeometries.Retain(key, e.CpuBytes, e.GpuBytes, () => DestroyGeometry(key, e))) return;
            DestroyGeometry(key, e);
        }
    }

    private void DestroyGeometry(string key, GeometryEntry entry)
    {
        geometries.Remove(key);
        ((ViewportMeshGeometry)entry.Geometry).ReleasePendingUpload();
        try { entry.Buffer.Dispose(); } finally { entry.Cpu.Dispose(); entry.Gpu.Dispose(); }
    }

    private static long NativeGeometryBufferBytes(GeometryEntry entry)
    {
        if (entry.Buffer is not HelixToolkit.SharpDX.Core.GeometryBufferModel { IsDisposed: false } buffer) return 0;
        long bytes = 0;
        foreach (var proxy in buffer.VertexBuffer)
            if (proxy?.Buffer is { IsDisposed: false } value) bytes = checked(bytes + value.Description.SizeInBytes);
        if (buffer.IndexBuffer?.Buffer is { IsDisposed: false } index) bytes = checked(bytes + index.Description.SizeInBytes);
        return bytes;
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
        DrainResourceCache();
        if (geometries.Count != 0 || materials.Count != 0 || textures.Count != 0 || graphTechniques.Count != 0)
            throw new InvalidOperationException("Close the asset viewports before disposing their shared service.");
        disposed = true;
        effects.DisposingResources -= OnGeometryDeviceInvalidated;
        effects.Reinitialized -= OnGeometryDeviceInvalidated;
        ReleaseShaderBytecodeCache();
        effects.Dispose();
    }

    internal sealed class RawTextureLoader(byte[] pixels, int width, int height, bool srgb) : ITextureInfoLoader, IDisposable
    {
        public static async Task<RawTextureLoader> CreateBackedAsync(TextureSurface surface, bool srgb,
            AssetMemoryBudget budget, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(surface); ArgumentNullException.ThrowIfNull(budget);
            if (surface.PixelSource is null || surface.IsHdr || surface.DisplayTransform is not null)
                throw new InvalidDataException("The prepared RGBA texture is unavailable.");
            TextureSurfaceLease? prefetched = await surface.AcquireRgba8Async(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                var loader = new RawTextureLoader([], surface.Width, surface.Height, srgb)
                { backing = surface, backingBudget = budget, firstPixels = prefetched, pixelLifetime = new() };
                prefetched = null; return loader;
            }
            finally { prefetched?.Dispose(); }
        }
        private byte[]? data = pixels;
        private bool compressedStream;
        private IDisposable? encodedCopyReservation;
        private readonly object uploadGate = new();
        private TextureSurface? backing;
        private AssetMemoryBudget? backingBudget;
        private TextureSurfaceLease? firstPixels;
        private CancellationTokenSource? pixelLifetime;
        private readonly Dictionary<Guid, (TextureSurfaceLease Pixels, IDisposable? Copy)> uploads = [];

        public static RawTextureLoader CreateEncoded2D(DdsTextureDocument document, SourceRevision revision,
            bool srgb, AssetMemoryBudget budget)
        {
            TextureContentKey.ValidateEncoded2D(document, revision);
            if (srgb && document.Info.Format is TextureStorageFormat.Bc4 or TextureStorageFormat.Bc5 or TextureStorageFormat.Luminance8)
                throw new InvalidDataException("The scalar texture requires linear sampling.");
            var rewrite = document.Info.IsSrgb != srgb;
            AssetMemoryBudget.Reservation? copy = null;
            try
            {
                byte[] bytes;
                if (!rewrite && MemoryMarshal.TryGetArray(document.OriginalBytes, out var segment) &&
                    segment.Array is { } backing && segment.Offset == 0 && segment.Count == backing.Length)
                    bytes = backing;
                else
                {
                    copy = budget.Reserve(document.OriginalBytes.Length, "encoded texture upload copy");
                    bytes = document.OriginalBytes.ToArray();
                }
                if (rewrite)
                {
                    if (document.Info.HeaderLength != 148 || !bytes.AsSpan(84, 4).SequenceEqual("DX10"u8))
                        throw new InvalidDataException("The encoded texture sampling header is unavailable.");
                    uint format = document.Info.Format switch
                    {
                        TextureStorageFormat.Bc1 => srgb ? 72u : 71u,
                        TextureStorageFormat.Bc3 => srgb ? 78u : 77u,
                        _ => throw new InvalidDataException("The encoded texture sampling format is unavailable.")
                    };
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(128, 4), format);
                    var alias = new DdsTextureReader().Read(bytes, document.Identity, document.Revision);
                    TextureContentKey.ValidateEncoded2D(alias, revision);
                    if (alias.Info.IsSrgb != srgb || alias.Info.Format != document.Info.Format ||
                        !alias.OriginalBytes.Span[148..].SequenceEqual(document.OriginalBytes.Span[148..]))
                        throw new InvalidDataException("The encoded texture sampling alias is invalid.");
                }
                var result = new RawTextureLoader(bytes, 0, 0, false)
                { compressedStream = true, encodedCopyReservation = copy };
                copy = null;
                return result;
            }
            finally { copy?.Dispose(); }
        }
        public static RawTextureLoader CreateCube(Deviant.Formats.Textures.DdsTextureDocument document,
            SourceRevision revision)
        {
            TextureContentKey.ValidateCube(document, revision);
            return new(document.OriginalBytes.ToArray(), 0, 0, false) { compressedStream = true };
        }
        public TextureInfo Load(Guid id)
        {
            lock (uploadGate)
            {
            var bytes = data ?? throw new ObjectDisposedException(nameof(RawTextureLoader));
            if (backing is not null)
            {
                if (uploads.ContainsKey(id)) throw new InvalidOperationException("The texture upload is already active.");
                TextureSurfaceLease? leased = firstPixels ?? backing.AcquireRgba8Async(pixelLifetime!.Token).GetAwaiter().GetResult();
                firstPixels = null;
                IDisposable? copy = null;
                try
                {
                    var rgba = leased.Surface.Rgba8;
                    if (MemoryMarshal.TryGetArray(rgba, out var segment) && segment.Array is { } array &&
                        segment.Offset == 0 && segment.Count == array.Length) bytes = array;
                    else
                    {
                        copy = backingBudget!.Reserve(rgba.Length, "backed texture upload copy");
                        bytes = rgba.ToArray();
                    }
                    var info = new TextureInfo(bytes, srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm,
                        width, height, generateMipMaps: false);
                    uploads.Add(id, (leased, copy)); leased = null; copy = null;
                    return info;
                }
                finally { leased?.Dispose(); copy?.Dispose(); }
            }
            return compressedStream
                ? new TextureInfo(new MemoryStream(bytes, writable: false), generateMipMaps: false)
                : new TextureInfo(bytes, srgb ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm,
                    width, height, generateMipMaps: false);
            }
        }
        public void Complete(Guid id, TextureInfo? info, bool succeeded)
        {
            lock (uploadGate)
            {
                if (uploads.Remove(id, out var upload))
                { upload.Pixels.Dispose(); upload.Copy?.Dispose(); }
            }
            if (compressedStream) info?.Texture?.Dispose();
            if (!succeeded) throw new InvalidOperationException("The texture could not be uploaded.");
        }
        public void Dispose()
        {
            pixelLifetime?.Cancel();
            lock (uploadGate)
            {
            data = null;
            Interlocked.Exchange(ref encodedCopyReservation, null)?.Dispose();
                firstPixels?.Dispose(); firstPixels = null;
                foreach (var upload in uploads.Values) { upload.Pixels.Dispose(); upload.Copy?.Dispose(); }
                uploads.Clear(); backing = null;
                pixelLifetime?.Dispose(); pixelLifetime = null;
            }
        }
    }
    private sealed class ActionLease(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
    internal sealed class GeometryEntry(MeshGeometry3D geometry, IDisposable cpu, IDisposable gpu,
        IGeometryBufferModel buffer, long cpuBytes, long gpuBytes, int deviceGeneration, bool canRetain)
    {
        public MeshGeometry3D Geometry { get; } = geometry; public IDisposable Cpu { get; } = cpu; public IDisposable Gpu { get; } = gpu;
        public IGeometryBufferModel Buffer { get; } = buffer; public long CpuBytes { get; } = cpuBytes; public long GpuBytes { get; } = gpuBytes;
        public int DeviceGeneration { get; } = deviceGeneration; public bool CanRetain { get; } = canRetain; public int References = 1;
    }
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
        public bool SurfaceMapped => entry.GraphTechnique?.IsSurfaceGraph == true;
        public bool IsTransparent => entry.GraphTechnique?.IsTransparent ?? false;
        public bool RequiresSceneDepth => entry.GraphTechnique?.RequiresSceneDepth == true;
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
