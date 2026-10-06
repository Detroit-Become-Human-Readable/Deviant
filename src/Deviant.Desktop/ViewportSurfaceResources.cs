using System.IO;
using Deviant.Assets;
using Deviant.Browser;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Shaders;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using ShaderDescription = HelixToolkit.SharpDX.Shaders.ShaderDescription;

namespace Deviant.Desktop;

public sealed partial class AssetViewportService
{
    private readonly Dictionary<string, GraphTechniqueEntry> graphTechniques = [];

    private async Task<GraphTechniqueLease?> AcquireGraphTechniqueAsync(RenderAssetSnapshot snapshot,
        MaterialSurfaceGraph graph, IReadOnlySet<MaterialInput> inputs, List<string> diagnostics, CancellationToken token)
    {
        using var scratch = snapshot.MemoryBudget.Reserve(1024 * 1024, "material program conversion");
        var program = await Task.Run(() => MaterialGraphCompiler.Compile(graph, inputs, token), token);
        diagnostics.AddRange(program.Diagnostics);
        if (!program.HasBaseColor && !program.HasOpacity && !program.HasAlphaCutoff) return null;
        if (graphTechniques.TryGetValue(program.Signature, out var cached))
        { cached.References++; return new(this, program.Signature, cached, program.TextureSlots); }
        var bytecodes = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            string layouts = ReadShader("SurfaceLayouts");
            string vertexSource = layouts + ReadShader("SurfaceVertex");
            string pixelSource = layouts + program.Declarations + ReadShader("SurfacePixel")
                .Replace("// HIDDEN_GRID_HELPER", ReadShader("HiddenGrid"), StringComparison.Ordinal)
                .Replace("// MATERIAL_BODY", program.Body, StringComparison.Ordinal);
            using var vertex = ShaderBytecode.Compile(vertexSource, "main", "vs_5_0", ShaderFlags.OptimizationLevel3);
            token.ThrowIfCancellationRequested();
            using var pixel = ShaderBytecode.Compile(pixelSource, "main", "ps_5_0", ShaderFlags.OptimizationLevel3);
            token.ThrowIfCancellationRequested();
            return (Vertex: vertex.Bytecode.Data, Pixel: pixel.Bytecode.Data);
        }, token);
        dispatcher.VerifyAccess();
        token.ThrowIfCancellationRequested();
        if (graphTechniques.TryGetValue(program.Signature, out cached))
        { cached.References++; return new(this, program.Signature, cached, program.TextureSlots); }
        string techniqueName = "Surface_" + program.Signature;
        var retained = snapshot.MemoryBudget.Reserve(checked(bytecodes.Vertex.Length + bytecodes.Pixel.Length), "material shader bytecode");
        IDisposable? gpu = null;
        try
        {
            gpu = ReserveGpu(checked(bytecodes.Vertex.Length + bytecodes.Pixel.Length), "material shaders");
            effects.AddTechnique(new TechniqueDescription(techniqueName)
            {
                InputLayoutDescription = new InputLayoutDescription(bytecodes.Vertex, DefaultInputLayout.VSInputInstancing),
                PassDescriptions = [new ShaderPassDescription(DefaultPassNames.Default)
                {
                    ShaderList = [new ShaderDescription("SurfaceVertex", ShaderStage.Vertex, new ShaderReflector(), bytecodes.Vertex),
                        new ShaderDescription(techniqueName, ShaderStage.Pixel, new ShaderReflector(), bytecodes.Pixel)],
                    BlendStateDescription = DefaultBlendStateDescriptions.BSAlphaBlend,
                    DepthStencilStateDescription = DefaultDepthStencilDescriptions.DSSDepthLessEqual
                }]
            });
            var entry = new GraphTechniqueEntry(techniqueName, retained, gpu);
            graphTechniques.Add(program.Signature, entry);
            return new(this, program.Signature, entry, program.TextureSlots);
        }
        catch { gpu?.Dispose(); retained.Dispose(); effects.RemoveTechnique(techniqueName); throw; }
    }

    private static string ReadShader(string name)
    {
        using var stream = typeof(AssetViewportService).Assembly.GetManifestResourceStream($"Deviant.Desktop.Rendering.{name}.hlsl")
            ?? throw new InvalidOperationException("The material shader is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static MaterialSurfaceGraph RestrictMissingTextures(MaterialSurfaceGraph graph,
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
                { diagnostics.Add($"{role}: The expression is too large."); return null; }
                if (node.Operation == MaterialExpressionOperation.TextureSample &&
                    (node.TextureSlot is not { } slot || !textures.ContainsKey(slot)))
                { diagnostics.Add($"{role}: The texture is unavailable."); return null; }
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

    private void ReleaseGraphTechnique(string key)
    {
        dispatcher.VerifyAccess();
        if (graphTechniques.TryGetValue(key, out var entry) && --entry.References == 0)
        {
            graphTechniques.Remove(key); effects.RemoveTechnique(entry.TechniqueName);
            entry.Cpu.Dispose(); entry.Gpu.Dispose();
        }
    }

    internal sealed class GraphTechniqueEntry(string techniqueName, IDisposable cpu, IDisposable gpu)
    {
        public string TechniqueName { get; } = techniqueName;
        public IDisposable Cpu { get; } = cpu; public IDisposable Gpu { get; } = gpu; public int References = 1;
    }
    internal sealed class GraphTechniqueLease(AssetViewportService owner, string key, GraphTechniqueEntry entry,
        IReadOnlyList<int> textureSlots) : IDisposable
    {
        private AssetViewportService? current = owner; public string TechniqueName => entry.TechniqueName;
        public IReadOnlyList<int> TextureSlots { get; } = textureSlots;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.ReleaseGraphTechnique(key);
    }
}
