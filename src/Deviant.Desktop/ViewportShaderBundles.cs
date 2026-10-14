using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using SharpDX.D3DCompiler;

namespace Deviant.Desktop;

public sealed partial class AssetViewportService
{
    private ConditionalWeakTable<RenderAssetSnapshot, ShaderBundleUse> shaderBundleUses = new();
    private sealed class ShaderBundleUse
    {
        public readonly HashSet<string> Keys = new(StringComparer.Ordinal);
        public Task<bool>? Restore;
    }

    private void TrackShaderUse(RenderAssetSnapshot snapshot, IDisposable consumer)
    {
        dispatcher.VerifyAccess();
        if (consumer is not ShaderBytecodeCache.ShaderBytecodeLease shader) return;
        var use = shaderBundleUses.GetOrCreateValue(snapshot);
        use.Keys.Add(shader.Key);
        if (invariantSurfaceVertex is not null) use.Keys.Add(invariantSurfaceVertex.Key);
    }

    internal void TrackMaterialShaderUse(RenderAssetSnapshot snapshot, GraphTechniqueLease? technique)
    {
        if (technique is not null) TrackShaderUse(snapshot, technique.BytecodeConsumer);
    }

    /// <summary>Call after this snapshot's complete material upload while its viewport consumers remain active.</summary>
    public Task<bool> PrepareShaderBundleAsync(RenderAssetSnapshot snapshot, CancellationToken token = default)
    {
        dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(disposed, this); token.ThrowIfCancellationRequested();
        if (shaderDiskCache is null || shaderBytecodeCache is null || surfaceCompilerIdentity is null ||
            !shaderBundleUses.TryGetValue(snapshot, out var use) || use.Keys.Count == 0) return Task.FromResult(false);
        var consumers = graphTechniques.Values.Select(entry => entry.Cpu).OfType<ShaderBytecodeCache.ShaderBytecodeLease>()
            .Where(shader => use.Keys.Contains(shader.Key)).ToList();
        if (invariantSurfaceVertex is not null && use.Keys.Contains(invariantSurfaceVertex.Key)) consumers.Add(invariantSurfaceVertex);
        if (consumers.Select(shader => shader.Key).Distinct(StringComparer.Ordinal).Count() != use.Keys.Count) return Task.FromResult(false);
        return shaderBytecodeCache.PrepareBundleAsync(ShaderBundleKey(snapshot, surfaceCompilerIdentity, token), consumers,
            snapshot.MemoryBudget, token);
    }

    /// <summary>Call before the first prepared-scene upload; each snapshot performs at most one bundle read.</summary>
    public Task<bool> RestoreShaderBundleAsync(RenderAssetSnapshot snapshot, CancellationToken token = default)
    {
        dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(disposed, this); token.ThrowIfCancellationRequested();
        if (shaderDiskCache is null) return Task.FromResult(false);
        var use = shaderBundleUses.GetOrCreateValue(snapshot);
        if (use.Restore?.IsCanceled == true || use.Restore?.IsFaulted == true) use.Restore = null;
        if (use.Restore is not null) return use.Restore.WaitAsync(token);
        shaderBytecodeCache ??= new(shaderDiskCache);
        surfaceCompilerIdentity ??= ShaderCompilerIdentity.FromLoadedCompiler();
        use.Restore = shaderBytecodeCache.RestoreBundleAsync(ShaderBundleKey(snapshot, surfaceCompilerIdentity, token),
            snapshot.MemoryBudget, token);
        return use.Restore;
    }

    internal static string ShaderBundleKey(RenderAssetSnapshot snapshot, ShaderCompilerIdentity compiler, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(compiler);
        if (snapshot.Materials.Count > 30000 || snapshot.Meshes.Count > 100000 || snapshot.SourceExtents.Count > 100000)
            throw new InvalidDataException("The shader bundle source profile exceeds its limits.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var scratch = snapshot.MemoryBudget.Reserve(4L * 1024 * 1024, "shader bundle source profile");
        Add(new { ShaderBytecodeCache.BundleProfile, ShaderBytecodeCache.Profile, snapshot.Revision,
            Compiler = compiler with { Sha256 = compiler.Sha256.ToLowerInvariant() },
            Flags = (int)ShaderFlags.OptimizationLevel3, VertexTarget = "vs_5_0", PixelTarget = "ps_5_0",
            Backend = typeof(AssetViewportService).Assembly.GetName().Version?.ToString() });
        foreach (string name in new[] { "SurfaceLayouts", "SurfaceVertex", "SurfacePixel", "HiddenGrid" }) Add(ReadShader(name));
        foreach (var extent in snapshot.SourceExtents.OrderBy(extent => extent.RelativeSource, StringComparer.Ordinal)
            .ThenBy(extent => extent.Offset).ThenBy(extent => extent.Length)) Add(extent);
        foreach (var pair in snapshot.Materials.OrderBy(pair => pair.Key.Key, StringComparer.Ordinal))
        {
            var material = pair.Value; Add(new { material.Asset.Identity, material.Asset.Revision, material.Asset.Profile,
                material.Asset.NativeRenderParameters });
            foreach (var texture in material.TexturesBySlot.OrderBy(pair => pair.Key))
                Add(new { Slot = texture.Key, texture.Value.NativeIdentity, Sampler = texture.Value.Sampling?.RawWord });
            if (material.Asset.SurfaceGraph is not { } graph) { Add("no-graph"); continue; }
            Add(new { graph.IsUnlit, graph.RequiredInputs, graph.TextureSlots });
            var nodes = new List<MaterialExpression>(); var ordinals = new Dictionary<MaterialExpression, int>(ReferenceEqualityComparer.Instance);
            int Index(MaterialExpression? node)
            {
                if (node is null) return -1;
                if (ordinals.TryGetValue(node, out int value)) return value;
                if (nodes.Count >= MaterialGraphCompiler.MaximumNodes) throw new InvalidDataException("The material graph exceeds the shader bundle limit.");
                value = nodes.Count; ordinals.Add(node, value); nodes.Add(node); return value;
            }
            Add(new[] { Index(graph.BaseColor), Index(graph.Opacity), Index(graph.AlphaCutoff) });
            for (int index = 0; index < nodes.Count; index++)
            {
                var node = nodes[index];
                if (node.Arguments.Count > MaterialGraphCompiler.MaximumNodes || node.Constants?.Count > 16)
                    throw new InvalidDataException("The material graph field exceeds the shader bundle limit.");
                Add(new { node.Operation, node.ValueType, node.Constants, node.Input, node.TextureSlot, node.Components,
                    Arguments = node.Arguments.Select(argument => Index(argument)).ToArray() });
            }
        }
        // Available vertex channels can change generated graph programs even when a material is shared.
        foreach (var mesh in snapshot.Meshes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Add(new { mesh.Key, Materials = mesh.Value.MaterialsByPrimitive?.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { pair.Key, pair.Value }).ToArray() });
            foreach (var primitive in mesh.Value.Geometry.Primitives)
                Add(new { primitive.Key, Positions = primitive.Positions.Count, Normals = primitive.Normals?.Count,
                    Tangents = primitive.Tangents?.Count, Colors = primitive.Colors?.Count,
                    Uvs = primitive.TextureCoordinates?.Select(channel => channel.Count).ToArray() });
        }
        return CacheManager.PreparedKey("shader-bundles", ShaderBytecodeCache.BundleProfile,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());

        void Add<T>(T value)
        {
            token.ThrowIfCancellationRequested(); byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            if (bytes.Length > ShaderBytecodeCache.MaximumSourceBytes) throw new InvalidDataException("The shader bundle profile field exceeds its limit.");
            hash.AppendData(bytes); hash.AppendData(new byte[] { 0 });
        }
    }
}
