using System.IO;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Formats.Textures;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Shaders;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using ShaderDescription = HelixToolkit.SharpDX.Shaders.ShaderDescription;

namespace Deviant.Desktop;

public sealed partial class AssetViewportService
{
    private sealed class UnsupportedMaterialPassException(string message) : Exception(message);

    private readonly Dictionary<string, GraphTechniqueEntry> graphTechniques = [];
    private CacheManager? shaderDiskCache;
    private ShaderBytecodeCache? shaderBytecodeCache;
    private ShaderCompilerIdentity? surfaceCompilerIdentity;
    private ShaderBytecodeCache.ShaderBytecodeLease? invariantSurfaceVertex;

    public void ConfigureShaderBytecodeCache(CacheManager? cache)
    {
        dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(disposed, this);
        if (ReferenceEquals(shaderDiskCache, cache)) return;
        ReleaseShaderBytecodeCache(); shaderDiskCache = cache;
    }

    /// <summary>Call after draining viewports when closing a workspace or the shared viewport service.</summary>
    public void ReleaseShaderBytecodeCache()
    {
        dispatcher.VerifyAccess();
        if (graphTechniques.Count != 0) throw new InvalidOperationException("Close material users before releasing the shader bytecode cache.");
        invariantSurfaceVertex?.Dispose(); invariantSurfaceVertex = null;
        shaderBytecodeCache?.Dispose(); shaderBytecodeCache = null;
        surfaceCompilerIdentity = null;
        shaderBundleUses = new();
    }

    private async Task<GraphTechniqueLease?> AcquireGraphTechniqueAsync(RenderAssetSnapshot snapshot,
        MaterialSurfaceGraph graph, IReadOnlySet<MaterialInput> inputs, IReadOnlyDictionary<int, RenderTexture> textures,
        ViewportMaterialState state, List<string> diagnostics, CancellationToken token)
    {
        using var timing = AssetLoadDiagnostics.Measure("ShaderPreparation");
        using var scratch = snapshot.MemoryBudget.Reserve(1024 * 1024, "material program conversion");
        var samplerGroups = new Dictionary<int, int>();
        var samplerKeys = new Dictionary<ulong, int>();
        foreach (int slot in graph.TextureSlots.Order())
        {
            if (!textures.TryGetValue(slot, out var texture)) continue;
            // Dictionary keys cannot be null; the fallback profile has its own sentinel.
            ulong key = texture.Sampling?.RawWord ?? ulong.MaxValue;
            if (!samplerKeys.TryGetValue(key, out int group))
            {
                group = samplerKeys.Count;
                if (group >= 15) throw new InvalidDataException("The material uses more than fifteen distinct samplers.");
                samplerKeys.Add(key, group);
            }
            samplerGroups.Add(slot, group);
        }
        var program = await Task.Run(() => MaterialGraphCompiler.Compile(graph, inputs, token, samplerGroups), token);
        if (program.RequiresSceneDepth && !state.IsTransparent)
            throw new UnsupportedMaterialPassException("This material requires scene depth in an unsupported opaque pass.");
        diagnostics.AddRange(program.Diagnostics);
        if (!program.HasBaseColor && !program.HasOpacity && !program.HasAlphaCutoff) return null;
        string techniqueKey = program.Signature + "-" + state.Key;
        bool shaderHit = graphTechniques.TryGetValue(techniqueKey, out var cached);
        AssetLoadDiagnostics.Cache("ShaderCache", techniqueKey, shaderHit);
        if (shaderHit)
        { cached!.References++; TrackShaderUse(snapshot, cached.Cpu); return new(this, techniqueKey, cached, program.TextureSlots, program.TextureSlots.Select(slot => 1 + samplerGroups[slot]).ToArray()); }
        shaderBytecodeCache ??= new(shaderDiskCache);
        surfaceCompilerIdentity ??= ShaderCompilerIdentity.FromLoadedCompiler();
        string layouts = ReadShader("SurfaceLayouts");
        string vertexSource = layouts + ReadShader("SurfaceVertex");
        string pixelSource = layouts + program.Declarations + ReadShader("SurfacePixel")
            .Replace("// HIDDEN_GRID_HELPER", ReadShader("HiddenGrid"), StringComparison.Ordinal)
            .Replace("// MATERIAL_BODY", program.Body, StringComparison.Ordinal);
        const ShaderFlags flags = ShaderFlags.OptimizationLevel3;
        if (invariantSurfaceVertex is null)
        {
            var vertex = await shaderBytecodeCache.AcquireAsync(new(vertexSource, "main", "vs_5_0", (int)flags, surfaceCompilerIdentity),
                snapshot.MemoryBudget, cancellation => CompileAsync(vertexSource, "vs_5_0", cancellation), token);
            // Another viewport may have acquired the same invariant while this one awaited.
            if (invariantSurfaceVertex is null) invariantSurfaceVertex = vertex;
            else vertex.Dispose();
        }
        var vertexBytes = (invariantSurfaceVertex ?? throw new InvalidOperationException("The surface vertex shader is unavailable.")).Bytes;
        ShaderBytecodeCache.ShaderBytecodeLease? pixelBytes = await shaderBytecodeCache.AcquireAsync(
            new(pixelSource, "main", "ps_5_0", (int)flags, surfaceCompilerIdentity), snapshot.MemoryBudget,
            cancellation => CompileAsync(pixelSource, "ps_5_0", cancellation), token);
        try
        {
        var retained = pixelBytes ?? throw new InvalidOperationException("The surface pixel shader is unavailable.");
        var bytecodes = (Vertex: vertexBytes, Pixel: retained.Bytes);
        dispatcher.VerifyAccess();
        token.ThrowIfCancellationRequested();
        if (graphTechniques.TryGetValue(techniqueKey, out cached))
        { cached.References++; TrackShaderUse(snapshot, cached.Cpu); return new(this, techniqueKey, cached, program.TextureSlots, program.TextureSlots.Select(slot => 1 + samplerGroups[slot]).ToArray()); }
        string techniqueName = "Surface_" + techniqueKey;
        IDisposable? gpu = null;
        try
        {
            gpu = ReserveGpu(checked(bytecodes.Vertex.Length + bytecodes.Pixel.Length), "material shaders");
            effects.AddTechnique(new TechniqueDescription(techniqueName)
            {
                InputLayoutDescription = new InputLayoutDescription(bytecodes.Vertex,
                    [.. DefaultInputLayout.VSInputInstancing,
                        new InputElement("TEXCOORD", 6, SharpDX.DXGI.Format.R32G32_Float, 8, 1),
                        new InputElement("TEXCOORD", 7, SharpDX.DXGI.Format.R32G32_Float, 16, 1),
                        new InputElement("TEXCOORD", 8, SharpDX.DXGI.Format.R32G32_Float, 24, 1)]),
                PassDescriptions = [new ShaderPassDescription(DefaultPassNames.Default)
                {
                    ShaderList = [new ShaderDescription("SurfaceVertex", ShaderStage.Vertex, new ShaderReflector(), bytecodes.Vertex),
                        new ShaderDescription(techniqueName, ShaderStage.Pixel, new ShaderReflector(), bytecodes.Pixel)],
                    BlendStateDescription = state.Blend,
                    DepthStencilStateDescription = state.Depth
                }, new ShaderPassDescription(DefaultPassNames.DepthPrepass)
                {
                    ShaderList = [new ShaderDescription("SurfaceVertex", ShaderStage.Vertex, new ShaderReflector(), bytecodes.Vertex),
                        new ShaderDescription(techniqueName, ShaderStage.Pixel, new ShaderReflector(), bytecodes.Pixel)],
                    BlendStateDescription = DefaultBlendStateDescriptions.NoBlend,
                    DepthStencilStateDescription = DefaultDepthStencilDescriptions.DSSDepthLessEqual
                }]
            });
            var entry = new GraphTechniqueEntry(techniqueName, retained, gpu, state.IsTransparent, requiresSceneDepth: program.RequiresSceneDepth);
            graphTechniques.Add(techniqueKey, entry);
            TrackShaderUse(snapshot, retained);
            pixelBytes = null; // The device technique retains its bytecode consumer until release.
            return new(this, techniqueKey, entry, program.TextureSlots, program.TextureSlots.Select(slot => 1 + samplerGroups[slot]).ToArray());
        }
        catch { gpu?.Dispose(); retained.Dispose(); effects.RemoveTechnique(techniqueName); throw; }
        }
        finally { pixelBytes?.Dispose(); }

        static Task<byte[]> CompileAsync(string source, string target, CancellationToken cancellation) => Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            using var compiled = ShaderBytecode.Compile(source, "main", target, ShaderFlags.OptimizationLevel3);
            cancellation.ThrowIfCancellationRequested(); return compiled.Bytecode.Data;
        }, cancellation);
    }

    private static string ReadShader(string name)
    {
        using var stream = typeof(AssetViewportService).Assembly.GetManifestResourceStream($"Deviant.Desktop.Rendering.{name}.hlsl")
            ?? throw new InvalidOperationException("The material shader is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static MaterialSurfaceGraph RestrictMissingTextures(MaterialSurfaceGraph graph,
        IReadOnlyDictionary<int, RenderTexture> textures, List<string> diagnostics)
    {
        return graph with
        {
            BaseColor = Check("BaseColor", graph.BaseColor), Opacity = Check("Opacity", graph.Opacity),
            AlphaCutoff = Check("AlphaCutoff", graph.AlphaCutoff)
        };
        MaterialExpression? Check(string role, MaterialExpression? expression)
        {
            if (expression is null) return null;
            var visited = new HashSet<MaterialExpression>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<MaterialExpression>(); pending.Push(expression);
            while (pending.TryPop(out var node))
            {
                if (!visited.Add(node)) continue;
                if (visited.Count > MaterialGraphCompiler.MaximumNodes)
                {
                    if (role is "Opacity" or "AlphaCutoff") throw new InvalidDataException($"{role}: The coverage expression is too large.");
                    diagnostics.Add($"{role}: The expression is too large."); return null;
                }
                if (node.Operation == MaterialExpressionOperation.TextureSample &&
                    (node.TextureSlot is not { } slot || !textures.ContainsKey(slot)))
                {
                    if (role is "Opacity" or "AlphaCutoff") throw new InvalidDataException($"{role}: The coverage texture is unavailable.");
                    diagnostics.Add($"{role}: The texture is unavailable."); return null;
                }
                foreach (var argument in node.Arguments) pending.Push(argument);
            }
            return expression;
        }
    }

    private static SamplerStateDescription SurfaceSampler() => new()
    {
        Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
        AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap,
        MaximumLod = float.MaxValue, MaximumAnisotropy = 1
    };

    internal static SamplerStateDescription SurfaceSampler(NativeTextureSamplingProfile? profile)
    {
        if (profile is null) return SurfaceSampler();
        if (profile.ComparisonEnabled || !profile.NormalizedCoordinates)
            throw new InvalidDataException("This texture sampler cannot be used by a colour surface.");
        return new()
        {
            Filter = profile.AnisotropyEnabled ? Filter.Anisotropic : profile.TexelFilter == NativeTextureTexelFilter.Point
                ? Filter.MinMagMipPoint : profile.MipFilter == NativeTextureMipFilter.Linear ? Filter.MinMagMipLinear : Filter.MinMagLinearMipPoint,
            AddressU = Address(profile.AddressU), AddressV = Address(profile.AddressV), AddressW = Address(profile.AddressW),
            MaximumLod = profile.MaximumMipLevel, MinimumLod = profile.MinimumLod, MipLodBias = profile.MipLodBias,
            MaximumAnisotropy = profile.MaximumAnisotropy,
            BorderColor = profile.BorderColor switch
            {
                NativeTextureBorderColor.OpaqueWhite => new(1, 1, 1, 1),
                NativeTextureBorderColor.OpaqueBlack => new(0, 0, 0, 1),
                _ => new(0, 0, 0, 0)
            }
        };
        static TextureAddressMode Address(NativeTextureAddressMode value) => value switch
        {
            NativeTextureAddressMode.Repeat => TextureAddressMode.Wrap,
            NativeTextureAddressMode.MirroredRepeat => TextureAddressMode.Mirror,
            NativeTextureAddressMode.ClampToEdge => TextureAddressMode.Clamp,
            NativeTextureAddressMode.ClampToBorder => TextureAddressMode.Border,
            _ => throw new InvalidDataException("The texture addressing mode is invalid.")
        };
    }

    private void ReleaseGraphTechnique(string key)
    {
        dispatcher.VerifyAccess();
        if (graphTechniques.TryGetValue(key, out var entry) && --entry.References == 0)
        {
            graphTechniques.Remove(key); effects.RemoveTechnique(entry.TechniqueName);
            entry.Cpu.Dispose(); entry.Gpu.Dispose();
        }
    }

    private GraphTechniqueLease AcquireNormalTechnique(RenderAssetSnapshot snapshot, ViewportMaterialState state,
        bool includeDepthPrepass = true)
    {
        dispatcher.VerifyAccess();
        string suffix = includeDepthPrepass ? "" : "-no-depth-prepass";
        string key = "normal-" + state.Key + suffix;
        if (graphTechniques.TryGetValue(key, out var cached))
        { cached.References++; return new(this, key, cached, [], []); }
        var cpu = snapshot.MemoryBudget.Reserve(normalPixelBytecode.Length, "material shader bytecode");
        IDisposable? gpu = null;
        string name = "NativeNormal_" + state.Key + suffix;
        try
        {
            gpu = ReserveGpu(normalPixelBytecode.Length, "material shaders");
            var passes = new List<ShaderPassDescription>
            {
                new(DefaultPassNames.Default)
                {
                    ShaderList = [DefaultVSShaderDescriptions.VSMeshInstancing,
                        new ShaderDescription(name, ShaderStage.Pixel, new ShaderReflector(), normalPixelBytecode)],
                    BlendStateDescription = state.Blend, DepthStencilStateDescription = state.Depth
                }
            };
            if (includeDepthPrepass)
                passes.Add(new(DefaultPassNames.DepthPrepass)
                {
                    ShaderList = [DefaultVSShaderDescriptions.VSMeshInstancing,
                        new ShaderDescription(name, ShaderStage.Pixel, new ShaderReflector(), normalPixelBytecode)],
                    BlendStateDescription = DefaultBlendStateDescriptions.NoBlend,
                    DepthStencilStateDescription = DefaultDepthStencilDescriptions.DSSDepthLessEqual
                });
            effects.AddTechnique(new TechniqueDescription(name)
            {
                InputLayoutDescription = new InputLayoutDescription(DefaultVSShaderByteCodes.VSMeshInstancing, DefaultInputLayout.VSInputInstancing),
                PassDescriptions = passes
            });
            var entry = new GraphTechniqueEntry(name, cpu, gpu, state.IsTransparent, false);
            graphTechniques.Add(key, entry);
            return new(this, key, entry, [], []);
        }
        catch { gpu?.Dispose(); cpu.Dispose(); effects.RemoveTechnique(name); throw; }
    }

    internal sealed class GraphTechniqueEntry(string techniqueName, IDisposable cpu, IDisposable gpu, bool isTransparent,
        bool isSurfaceGraph = true, bool requiresSceneDepth = false)
    {
        public string TechniqueName { get; } = techniqueName;
        public bool IsTransparent { get; } = isTransparent;
        public bool IsSurfaceGraph { get; } = isSurfaceGraph;
        public bool RequiresSceneDepth { get; } = requiresSceneDepth;
        public IDisposable Cpu { get; } = cpu; public IDisposable Gpu { get; } = gpu; public int References = 1;
    }
    internal sealed class GraphTechniqueLease(AssetViewportService owner, string key, GraphTechniqueEntry entry,
        IReadOnlyList<int> textureSlots, IReadOnlyList<int> samplerSlots) : IDisposable
    {
        internal IDisposable BytecodeConsumer => entry.Cpu;
        private AssetViewportService? current = owner; public string TechniqueName => entry.TechniqueName;
        public bool IsTransparent => entry.IsTransparent;
        public bool IsSurfaceGraph => entry.IsSurfaceGraph;
        public bool RequiresSceneDepth => entry.RequiresSceneDepth;
        public IReadOnlyList<int> TextureSlots { get; } = textureSlots;
        public IReadOnlyList<int> SamplerSlots { get; } = samplerSlots;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.ReleaseGraphTechnique(key);
    }
}
