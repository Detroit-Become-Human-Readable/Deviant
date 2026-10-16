using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Numerics;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Export.Models;
using Deviant.Export;
using Deviant.Export.Textures;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

/// <summary>Prepared original textures and winning material-map coordinates shared by export jobs.</summary>
public sealed class NativeModelExportPreparationCache(CacheManager cache)
{
    public const string Profile = "native-fbx-preparation-v3-source-plan-original-base-mip-maps";
    public const string MaterialProfile = "native-fbx-material-maps-v5-uv-atlas";
    private const int MaximumHeaderBytes = 64 * 1024;
    private const long MaximumArtifactBytes = 2L * 1024 * 1024 * 1024;
    private sealed record SurfaceHeader(int Width, int Height, bool IsHdr, bool SourceIsHdr, bool IsSrgb,
        int MipIndex, TextureStorageFormat Format, string FormatName, string SourceSha256,
        TextureSubresourceSelection Subresource, ResourceIdentity? Identity, SourceRevision? Revision,
        TextureDisplayTransform? DisplayTransform);
    private sealed record Header(string Key, string Producer, ResourceIdentity? DocumentIdentity, SourceRevision? Revision,
        int DdsBytes, int PixelBytes, SurfaceHeader Surface)
    {
        public NativeTextureExportSourceEvidence? SourceEvidence { get; init; }
    }

    private sealed record MapHeader(MaterialTextureRole Role, int UvSet, ResourceIdentity Identity, SourceRevision Revision,
        string DdsSha256, int Width, int Height, bool IsSrgb, long Length, string PngSha256, string GraphSha256,
        IReadOnlyList<string> PrimitiveKeys, IReadOnlyList<FbxBakeTextureSource> Sources, int CornerCount);
    private sealed record MapsHeader(string Key, string Producer, IReadOnlyList<MapHeader> Maps, IReadOnlyList<string> Diagnostics);
    private sealed record MediaHeader(string Key, string Producer, TextureExportResult Result);

    /// <summary>Copies a prepared original PNG or DDS to the export staging directory with bounded buffers.</summary>
    public async Task<TextureExportResult> PublishTextureMediaAsync(string textureKey, DdsTextureDocument document,
        TextureSurface surface, string sourceRoot, string stageRoot, string temporaryName,
        Func<CancellationToken, Task> validateSource, AssetMemoryBudget budget, CancellationToken token = default)
    {
        string key = CacheManager.PreparedKey("export-encoded", Profile, textureKey,
            $"{document.ContentSha256}|{surface.IsHdr}|{surface.IsSrgb}|{surface.Subresource}|{JsonSerializer.Serialize(surface.DisplayTransform)}");
        await validateSource(token).ConfigureAwait(false);
        FileStream? pin = null;
        MediaHeader? header = null;
        using var scratch = budget.Reserve(256 * 1024, "prepared texture media streaming");
        try
        {
            await using var lease = await cache.AcquirePreparedAsync("export-encoded", key, ".encoded", async (path, cancellation) =>
            {
                string root = Path.Combine(Path.GetTempPath(), "Deviant-export-" + Guid.NewGuid().ToString("N"));
                try
                {
                    using var encoderMemory = budget.Reserve(surface.IsHdr ? 128 * 1024 : checked(surface.Width * 8L + 256 * 1024), "prepared texture encoder scratch");
                    var result = await new TextureExportService().ExportAsync(document, surface,
                        surface.IsHdr ? AssetExportFormat.Dds : AssetExportFormat.Png, sourceRoot, root,
                        surface.IsHdr ? "media.dds" : "media.png", cancellationToken: cancellation,
                        expectedRevision: document.Revision, preCommitValidation: validateSource).ConfigureAwait(false);
                    byte[] json = JsonSerializer.SerializeToUtf8Bytes(new MediaHeader(key, Profile, result));
                    if (json.Length > MaximumHeaderBytes) throw new InvalidDataException("PreparedMediaHeaderLimit");
                    await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
                    byte[] prefix = new byte[12]; "DVEPMED1"u8.CopyTo(prefix); BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(8), json.Length);
                    await output.WriteAsync(prefix, cancellation).ConfigureAwait(false); await output.WriteAsync(json, cancellation).ConfigureAwait(false);
                    await using var input = new FileStream(Path.Combine(root, result.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
                    await CopyRangeAsync(input, output, result.Length, cancellation).ConfigureAwait(false);
                    if (output.Length > MaximumArtifactBytes - 32) throw new InvalidDataException("PreparedMediaLimit");
                    await output.FlushAsync(cancellation).ConfigureAwait(false); output.Position = 0;
                    byte[] digest = await SHA256.HashDataAsync(output, cancellation).ConfigureAwait(false);
                    await output.WriteAsync(digest, cancellation).ConfigureAwait(false); await output.FlushAsync(cancellation).ConfigureAwait(false);
                }
                finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            }, async (path, cancellation) =>
            {
                pin?.Dispose(); pin = null; bool accepted = false;
                try
                {
                    pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 128 * 1024, FileOptions.Asynchronous);
                    if (pin.Length is < 44 or > MaximumArtifactBytes) return false;
                    byte[] prefix = new byte[12]; await pin.ReadExactlyAsync(prefix, cancellation).ConfigureAwait(false);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(8));
                    if (!prefix.AsSpan(0, 8).SequenceEqual("DVEPMED1"u8) || length is < 1 or > MaximumHeaderBytes) return false;
                    byte[] json = new byte[length]; await pin.ReadExactlyAsync(json, cancellation).ConfigureAwait(false);
                    header = JsonSerializer.Deserialize<MediaHeader>(json);
                    if (header is null || header.Key != key || header.Producer != Profile || header.Result.Length < 1 ||
                        header.Result.InputPayloadSha256 != document.ContentSha256 || header.Result.Identity != document.Identity ||
                        header.Result.Revision != document.Revision || pin.Position + header.Result.Length + 32 != pin.Length ||
                        header.Result.Format != (surface.IsHdr ? AssetExportFormat.Dds : AssetExportFormat.Png) ||
                        (!surface.IsHdr && header.Result.SelectedSubresource != surface.Subresource) || header.Result.Sha256.Length != 64) return false;
                    long offset = pin.Position;
                    if (!await VerifyFooterAsync(pin, cancellation).ConfigureAwait(false)) return false;
                    pin.Position = offset; accepted = true; return true;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException or OverflowException or ArgumentException or NullReferenceException) { return false; }
                finally { if (!accepted) { pin?.Dispose(); pin = null; header = null; } }
            }, token).ConfigureAwait(false);
            // AcquirePrepared validates while this stream is pinned; do not reopen and hash the cache file again.
            if (pin is null || header is null) throw new InvalidDataException("PreparedMediaValidationMissing");
            await validateSource(token).ConfigureAwait(false);
            OutputSafety.ValidateOutputRoot(stageRoot, sourceRoot);
            Directory.CreateDirectory(stageRoot);
            using var directoryPin = OutputSafety.PinDirectory(stageRoot);
            string path = OutputSafety.ResolveOutputPath(stageRoot, temporaryName);
            bool created = false;
            try
            {
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
                { created = true; await CopyRangeAsync(pin, output, header.Result.Length, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); }
                await validateSource(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
                return header.Result with { RelativePath = temporaryName };
            }
            catch { if (created) File.Delete(path); throw; }
        }
        finally { pin?.Dispose(); }
    }

    /// <summary>Fingerprints every input used to choose and rasterize export UVs, including original source validation keys.</summary>
    public static string MaterialKey(MaterialAsset material, MeshPrimitive primitive, IReadOnlyList<string> sourceKeys,
        IReadOnlyDictionary<int, MaterialBakeTexture> textures, int width, int height, long maximumPixels,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(material); ArgumentNullException.ThrowIfNull(primitive);
        ArgumentNullException.ThrowIfNull(sourceKeys); ArgumentNullException.ThrowIfNull(textures);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sink = new HashSink(hash); using var writer = new BinaryWriter(sink, Encoding.UTF8, true);
        writer.Write(MaterialProfile); WriteMaterialFingerprint(writer, material, token);
        writer.Write(width); writer.Write(height); writer.Write(maximumPixels); writer.Write(primitive.Key);
        writer.Write((int)primitive.Topology); writer.Write((int)primitive.IndexFormat);
        if (sourceKeys.Count > 4096 || textures.Count > 4096) throw new InvalidDataException("ExportMaterialFingerprintLimit");
        writer.Write(sourceKeys.Count); foreach (var source in sourceKeys) { token.ThrowIfCancellationRequested(); writer.Write(source); }
        writer.Write(textures.Count);
        foreach (var pair in textures.OrderBy(pair => pair.Key))
        {
            token.ThrowIfCancellationRequested(); writer.Write(pair.Key);
            var surface = pair.Value.Surface;
            writer.Write(surface.SourcePayloadSha256); writer.Write(surface.Width); writer.Write(surface.Height);
            writer.Write(surface.IsSrgb); writer.Write(JsonSerializer.Serialize(surface.Subresource));
            writer.Write(JsonSerializer.Serialize(surface.DisplayTransform)); writer.Write(JsonSerializer.Serialize(pair.Value.Sampling));
        }
        writer.Write(primitive.Positions.Count);
        for (int i = 0; i < primitive.Positions.Count; i++) { Check(i); V3(primitive.Positions[i]); }
        writer.Write(primitive.Indices.Count);
        for (int i = 0; i < primitive.Indices.Count; i++) { Check(i); writer.Write(primitive.Indices[i]); }
        writer.Write(primitive.TextureCoordinates?.Count ?? -1);
        foreach (var set in primitive.TextureCoordinates ?? [])
        { writer.Write(set.Count); for (int i = 0; i < set.Count; i++) { Check(i); V2(set[i]); } }
        writer.Write(primitive.Normals?.Count ?? -1);
        if (primitive.Normals is { } normals) for (int i = 0; i < normals.Count; i++) { Check(i); V3(normals[i]); }
        writer.Write(primitive.Colors?.Count ?? -1);
        if (primitive.Colors is { } colors) for (int i = 0; i < colors.Count; i++) { Check(i); V4(colors[i]); }
        writer.Write(primitive.Tangents?.Count ?? -1);
        if (primitive.Tangents is { } tangents) for (int i = 0; i < tangents.Count; i++) { Check(i); V4(tangents[i]); }
        token.ThrowIfCancellationRequested(); writer.Flush(); return CacheManager.PreparedKey("export-maps", MaterialProfile, Convert.ToHexString(hash.GetHashAndReset()));
        void Check(int i) { if ((i & 1023) == 0) token.ThrowIfCancellationRequested(); }
        void V2(Vector2 v) { writer.Write(v.X); writer.Write(v.Y); }
        void V3(Vector3 v) { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); }
        void V4(Vector4 v) { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); writer.Write(v.W); }
    }

    private static void WriteMaterialFingerprint(BinaryWriter writer, MaterialAsset material, CancellationToken token)
    {
        Identity(material.Identity); Text(material.Revision.IndexContentId); Text(material.Revision.PhysicalSourceStamp); Text(material.Profile);
        Count(material.Bindings.Count);
        foreach (var binding in material.Bindings)
        {
            token.ThrowIfCancellationRequested(); writer.Write(binding.SlotOrdinal); Reference(binding.Texture);
            writer.Write((int)binding.Role); writer.Write(binding.TextureCoordinateSet);
            writer.Write(binding.UvScale.X); writer.Write(binding.UvScale.Y); writer.Write(binding.UvOffset.X); writer.Write(binding.UvOffset.Y);
            writer.Write(binding.Evidence.Verified); Text(binding.Evidence.Source); Text(binding.Evidence.Description);
            var physical = binding.Evidence.PhysicalExtent; writer.Write(physical is not null);
            if (physical is not null) { Text(physical.RelativeSource); writer.Write(physical.Offset); writer.Write(physical.Length); Text(physical.PayloadSha256); }
            Decoded(binding.Evidence.DecodedExtent);
            writer.Write(binding.Evidence.SourceReferenceTrailer is not null);
            if (binding.Evidence.SourceReferenceTrailer is { } trailer) writer.Write(trailer);
        }
        Count(material.References.Count); foreach (var reference in material.References) { token.ThrowIfCancellationRequested(); Reference(reference); }
        Strings(material.Diagnostics);
        var render = material.NativeRenderParameters; writer.Write(render is not null);
        if (render is not null)
        {
            writer.Write(render.ProgramMode); writer.Write(render.StoredBlendSelector); writer.Write(render.Flags);
            writer.Write(render.DepthBiasBits); writer.Write(render.DrawSwitch); writer.Write(render.RenderOptions); Decoded(render.SourceExtent);
        }
        var graph = material.SurfaceGraph; writer.Write(graph is not null);
        if (graph is null) return;
        writer.Write(graph.IsUnlit);
        Count(graph.RequiredInputs.Count); foreach (var input in graph.RequiredInputs) { token.ThrowIfCancellationRequested(); writer.Write((int)input); }
        Count(graph.TextureSlots.Count); foreach (var slot in graph.TextureSlots) { token.ThrowIfCancellationRequested(); writer.Write(slot); }
        Strings(graph.Diagnostics);
        // IDs follow deterministic argument order. A shared expression is encoded once, rather than expanding its tree.
        var indices = new Dictionary<MaterialExpression, int>(ReferenceEqualityComparer.Instance);
        var active = new HashSet<MaterialExpression>(ReferenceEqualityComparer.Instance);
        var heights = new Dictionary<MaterialExpression, int>(ReferenceEqualityComparer.Instance);
        var nodes = new List<MaterialExpression>();
        int color = Visit(graph.BaseColor, 1), opacity = Visit(graph.Opacity, 1), cutoff = Visit(graph.AlphaCutoff, 1);
        writer.Write(color); writer.Write(opacity); writer.Write(cutoff); writer.Write(nodes.Count);
        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested(); writer.Write((int)node.Operation); writer.Write((int)node.ValueType);
            writer.Write(node.Arguments.Count); foreach (var argument in node.Arguments) writer.Write(indices[argument]);
            writer.Write(node.Constants?.Count ?? -1);
            foreach (float value in node.Constants ?? []) { token.ThrowIfCancellationRequested(); writer.Write(value); }
            writer.Write(node.Input is not null); if (node.Input is { } input) writer.Write((int)input);
            writer.Write(node.TextureSlot is not null); if (node.TextureSlot is { } slot) writer.Write(slot);
            Text(node.Components); Decoded(node.SourceExtent);
        }

        int Visit(MaterialExpression? node, int depth)
        {
            token.ThrowIfCancellationRequested(); if (node is null) return -1;
            if (depth > 64) throw new InvalidDataException("ExportMaterialGraphDepthLimit");
            if (indices.TryGetValue(node, out int index)) return index;
            if (!active.Add(node)) throw new InvalidDataException("ExportMaterialGraphCycle");
            if (indices.Count + active.Count > 3 * 4096 || node.Arguments.Count > 4 || node.Constants?.Count > 4)
                throw new InvalidDataException("ExportMaterialGraphNodeLimit");
            int height = 1;
            foreach (var argument in node.Arguments)
            {
                if (argument is null) throw new InvalidDataException("ExportMaterialGraphArgumentMissing");
                Visit(argument, depth + 1); height = Math.Max(height, heights[argument] + 1);
            }
            if (height > 64) throw new InvalidDataException("ExportMaterialGraphDepthLimit");
            active.Remove(node); index = nodes.Count; indices.Add(node, index); heights.Add(node, height); nodes.Add(node); return index;
        }
        void Count(int count) { token.ThrowIfCancellationRequested(); if (count > 4096) throw new InvalidDataException("ExportMaterialFingerprintLimit"); writer.Write(count); }
        void Text(string? value)
        {
            token.ThrowIfCancellationRequested(); writer.Write(value is not null); if (value is null) return;
            if (value.Length > 16 * 1024) throw new InvalidDataException("ExportMaterialFingerprintTextLimit"); writer.Write(value);
        }
        void Identity(ResourceIdentity? identity)
        { writer.Write(identity is not null); if (identity is not null) { Text(identity.IndexContentId); writer.Write(identity.Ordinal); Text(identity.MemberPath); } }
        void Reference(ResourceReference reference)
        { writer.Write((int)reference.Namespace); Text(reference.Id); Identity(reference.Owner); writer.Write(reference.ResourceType is not null); if (reference.ResourceType is { } type) writer.Write(type); }
        void Decoded(DecodedExtent? extent)
        { writer.Write(extent is not null); if (extent is not null) { Identity(extent.Container); writer.Write(extent.Offset); writer.Write(extent.Length); } }
        void Strings(IReadOnlyList<string> values) { Count(values.Count); foreach (string value in values) Text(value); }
    }

    /// <summary>Restores winning UV coordinates and export PNGs without invoking the rasterizer or atlas producer.</summary>
    public async Task<(IReadOnlyList<FbxStagedMaterialMapInput> Maps, IAsyncDisposable Lease, bool CacheHit, IReadOnlyList<string> Diagnostics)> AcquireMapsAsync(
        string key, Func<Task<IReadOnlyList<FbxStagedMaterialMapInput>>> producer,
        Func<CancellationToken, Task> validateSource, AssetMemoryBudget budget, CancellationToken token = default,
        Func<IReadOnlyList<string>>? diagnostics = null)
    {
        await validateSource(token).ConfigureAwait(false);
        IReadOnlyList<FbxStagedMaterialMapInput>? created = null;
        var leases = new LeaseSet();
        try
        {
            var bundle = await cache.AcquirePreparedAsync("export-maps", key, ".maps", async (path, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested(); created = await producer().ConfigureAwait(false);
                await WriteMapsAsync(path, key, created, diagnostics?.Invoke() ?? [], cancellation).ConfigureAwait(false);
            }, (path, cancellation) => ValidateMapsAsync(path, key, cancellation), token).ConfigureAwait(false);
            leases.Add(bundle);
            await validateSource(token).ConfigureAwait(false);
            await using var input = new FileStream(bundle.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
            var header = await ReadMapsHeaderAsync(input, token).ConfigureAwait(false);
            var maps = new List<FbxStagedMaterialMapInput>();
            foreach (var map in header.Maps)
            {
                token.ThrowIfCancellationRequested();
                var cornerBytes = checked(map.CornerCount * 8L);
                if (cornerBytes > 0) leases.Add(budget.Reserve(cornerBytes + 4096, "prepared atlas coordinates"));
                Vector2[]? corners = map.CornerCount == 0 ? null : new Vector2[map.CornerCount];
                var cornerBuffer = new byte[64 * 1024];
                if (corners is not null) for (int first = 0; first < corners.Length; first += cornerBuffer.Length / 8)
                {
                    token.ThrowIfCancellationRequested(); int count = Math.Min(corners.Length - first, cornerBuffer.Length / 8);
                    await input.ReadExactlyAsync(cornerBuffer.AsMemory(0, count * 8), token).ConfigureAwait(false);
                    for (int i = 0; i < count; i++)
                    {
                        int offset = i * 8;
                        corners[first + i] = new(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(cornerBuffer.AsSpan(offset))),
                            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(cornerBuffer.AsSpan(offset + 4))));
                        if (!float.IsFinite(corners[first + i].X) || !float.IsFinite(corners[first + i].Y)) throw new InvalidDataException("PreparedAtlasInvalid");
                    }
                }
                long pngOffset = input.Position;
                string mediaKey = CacheManager.PreparedKey("export-media", Profile, key, $"{map.Role}|{map.PngSha256}");
                var media = await cache.AcquirePreparedAsync("export-media", mediaKey, ".png", async (path, cancellation) =>
                {
                    await using var source = new FileStream(bundle.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
                    source.Position = pngOffset;
                    await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
                    await CopyRangeAsync(source, output, map.Length, cancellation).ConfigureAwait(false);
                }, (path, cancellation) => ValidateMediaAsync(path, map.Length, map.PngSha256, cancellation), token).ConfigureAwait(false);
                leases.Add(media);
                // The pin prevents modification between validation and publication, including across concurrent material jobs.
                leases.Add(new FileStream(media.Path, FileMode.Open, FileAccess.Read, FileShare.Read));
                input.Position = checked(pngOffset + map.Length);
                maps.Add(new(map.Role, map.UvSet, map.Identity, map.Revision, map.DdsSha256, map.Width, map.Height,
                    map.IsSrgb, media.Path, map.Length, map.PngSha256, map.GraphSha256, map.PrimitiveKeys, map.Sources,
                    validateSource) { PolygonCornerCoordinates = corners,
                        OpenPreparedContent = cancellation => { cancellation.ThrowIfCancellationRequested();
                            return Task.FromResult<Stream>(new FileStream(media.Path, FileMode.Open, FileAccess.Read,
                                FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan)); } });
            }
            await validateSource(token).ConfigureAwait(false);
            return (maps, leases, created is null, header.Diagnostics);
        }
        catch { await leases.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static async Task WriteMapsAsync(string path, string key, IReadOnlyList<FbxStagedMaterialMapInput> maps, IReadOnlyList<string> diagnostics, CancellationToken token)
    {
        if (maps.Count > 2) throw new InvalidDataException("PreparedMapCountInvalid");
        var header = new MapsHeader(key, MaterialProfile, maps.Select(map => new MapHeader(map.Role, map.TextureCoordinateSet,
            map.MaterialIdentity, map.MaterialRevision, map.DdsSha256, map.Width, map.Height, map.IsSrgb, map.Length,
            map.PngSha256, map.GraphSha256, map.PrimitiveKeys, map.Sources, map.PolygonCornerCoordinates?.Count ?? 0)).ToArray(), diagnostics);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(header);
        if (json.Length > MaximumHeaderBytes) throw new InvalidDataException("PreparedMapsHeaderLimit");
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        byte[] prefix = new byte[12]; "DVEPMAP1"u8.CopyTo(prefix); BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(8), json.Length);
        await output.WriteAsync(prefix, token).ConfigureAwait(false); await output.WriteAsync(json, token).ConfigureAwait(false);
        byte[] cornerBuffer = new byte[64 * 1024];
        foreach (var map in maps)
        {
            var corners = map.PolygonCornerCoordinates;
            if (corners is not null) for (int first = 0; first < corners.Count; first += cornerBuffer.Length / 8)
            {
                token.ThrowIfCancellationRequested(); int count = Math.Min(corners.Count - first, cornerBuffer.Length / 8);
                for (int i = 0; i < count; i++)
                {
                    var corner = corners[first + i];
                    if (!float.IsFinite(corner.X) || !float.IsFinite(corner.Y)) throw new InvalidDataException("PreparedAtlasInvalid");
                    BinaryPrimitives.WriteInt32LittleEndian(cornerBuffer.AsSpan(i * 8), BitConverter.SingleToInt32Bits(corner.X));
                    BinaryPrimitives.WriteInt32LittleEndian(cornerBuffer.AsSpan(i * 8 + 4), BitConverter.SingleToInt32Bits(corner.Y));
                }
                await output.WriteAsync(cornerBuffer.AsMemory(0, count * 8), token).ConfigureAwait(false);
            }
            await using var png = new FileStream(map.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
            if (png.Length != map.Length || !await ValidateMediaAsync(map.Path, map.Length, map.PngSha256, token).ConfigureAwait(false))
                throw new InvalidDataException("PreparedMapSourceChanged");
            await CopyRangeAsync(png, output, map.Length, token).ConfigureAwait(false);
        }
        if (output.Length > MaximumArtifactBytes - 32) throw new InvalidDataException("PreparedMapsLimit");
        await output.FlushAsync(token).ConfigureAwait(false); output.Position = 0;
        byte[] digest = await SHA256.HashDataAsync(output, token).ConfigureAwait(false);
        await output.WriteAsync(digest, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<MapsHeader> ReadMapsHeaderAsync(Stream input, CancellationToken token)
    {
        byte[] prefix = new byte[12]; await input.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(8));
        if (!prefix.AsSpan(0, 8).SequenceEqual("DVEPMAP1"u8) || length is < 1 or > MaximumHeaderBytes)
            throw new InvalidDataException("PreparedMapsHeaderInvalid");
        byte[] json = new byte[length]; await input.ReadExactlyAsync(json, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<MapsHeader>(json) ?? throw new InvalidDataException("PreparedMapsHeaderInvalid");
    }

    private static async Task<bool> ValidateMapsAsync(string path, string key, CancellationToken token)
    {
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
            if (input.Length is < 44 or > MaximumArtifactBytes) return false;
            var header = await ReadMapsHeaderAsync(input, token).ConfigureAwait(false);
            if (header.Key != key || header.Producer != MaterialProfile || header.Maps.Count > 2 || header.Diagnostics.Count > 128) return false;
            long expected = input.Position + 32;
            foreach (var map in header.Maps)
            {
                if (map.Length < 33 || map.CornerCount is < 0 or > 8_388_608 || map.UvSet < 0 ||
                    map.Width is < 1 or > DdsTextureReader.MaximumDimension || map.Height is < 1 or > DdsTextureReader.MaximumDimension ||
                    (long)map.Width * map.Height > DdsTextureReader.MaximumPixelCount || map.PngSha256.Length != 64) return false;
                expected = checked(expected + map.CornerCount * 8L + map.Length);
            }
            if (expected != input.Length) return false;
            return await VerifyFooterAsync(input, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or OverflowException or ArgumentException or NullReferenceException) { return false; }
    }

    private static async Task<bool> VerifyFooterAsync(Stream input, CancellationToken token)
    {
        input.Position = 0; byte[] buffer = new byte[128 * 1024]; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long left = input.Length - 32;
        while (left > 0)
        {
            int count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(left, buffer.Length)), token).ConfigureAwait(false);
            if (count == 0) return false; hash.AppendData(buffer, 0, count); left -= count;
        }
        byte[] digest = new byte[32]; await input.ReadExactlyAsync(digest, token).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), digest);
    }

    private static async Task<bool> ValidateMediaAsync(string path, long length, string digest, CancellationToken token)
    {
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
            return input.Length == length && StringComparer.OrdinalIgnoreCase.Equals(digest,
                Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)));
        }
        catch (IOException) { return false; }
    }

    private static async Task CopyRangeAsync(Stream input, Stream output, long length, CancellationToken token)
    {
        byte[] buffer = new byte[128 * 1024];
        while (length > 0)
        {
            int count = (int)Math.Min(length, buffer.Length); await input.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); length -= count;
        }
    }

    private sealed class LeaseSet : IAsyncDisposable
    {
        private readonly List<object> leases = [];
        public void Add(object lease) => leases.Add(lease);
        public async ValueTask DisposeAsync()
        {
            for (int i = leases.Count - 1; i >= 0; i--)
                if (leases[i] is IAsyncDisposable asyncLease) await asyncLease.DisposeAsync().ConfigureAwait(false);
                else if (leases[i] is IDisposable lease) lease.Dispose();
            leases.Clear();
        }
    }

    private sealed class HashSink(IncrementalHash hash) : Stream
    {
        private readonly byte[] buffer = new byte[64 * 1024]; private int used;
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { if (used == 0) return; hash.AppendData(buffer.AsSpan(0, used)); used = 0; }
        public override void Write(byte[] value, int offset, int count) => Write(value.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> value)
        {
            while (!value.IsEmpty)
            {
                int count = Math.Min(value.Length, buffer.Length - used); value[..count].CopyTo(buffer.AsSpan(used));
                used += count; value = value[count..]; if (used == buffer.Length) Flush();
            }
        }
        public override int Read(byte[] value, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    internal static string TextureKey(NativeTextureValidationSnapshot source, NativeFbxHdrConversion? hdr) =>
        CacheManager.PreparedKey("export-texture", Profile, JsonSerializer.Serialize(new
        {
            source.NativeIdentity, source.SelectedIdentity, source.Revision, source.CatalogIdentity, source.CatalogSha256,
            source.MetadataIdentity, source.MetadataSha256,
            Payloads = source.Payloads.OrderBy(payload => payload.Identity.Key, StringComparer.Ordinal).ToArray(),
            Companion = new
            {
                Source = new
                {
                    source.Companion.MetadataSource.Identity,
                    source.Companion.MetadataSource.Type,
                    source.Companion.MetadataSource.Id,
                    source.Companion.MetadataSource.Extent,
                    source.Companion.MetadataSource.Sha256
                },
                verifiedExtents = source.Companion.SourceExtents
                    .OrderBy(extent => extent.RelativeSource, StringComparer.Ordinal)
                    .ThenBy(extent => extent.Offset)
                    .ThenBy(extent => extent.Length)
                    .ToArray()
            },
            Hdr = hdr,
            NativeSource = NativeTextureSession.Version, NativeDecoder = NativeTextureDecoder.Version,
            Surface = "dds-base-mip-rgba-v1"
        }));

    public Task<(DdsTextureDocument Document, TextureSurface Surface, IAsyncDisposable Lease)> AcquireTextureAsync(
        string key, Func<CancellationToken, Task<(DdsTextureDocument Document, TextureSurface Surface)>> producer,
        Func<CancellationToken, Task> validateSource, AssetMemoryBudget budget, CancellationToken token = default)
        => AcquireTextureCoreAsync(key, producer, validateSource, budget, token);

    public static string PlannedTextureKey(NativeTextureExportFingerprint plan, NativeFbxHdrConversion? hdr = null)
        => CacheManager.PreparedKey("export-texture", Profile, JsonSerializer.Serialize(new
        { Plan = plan, Hdr = hdr, Source = NativeTextureSession.Version,
            Decoder = NativeTextureDecoder.Version, Surface = "dds-base-mip-rgba-v1" }));

    /// <summary>Looks up original preparation using metadata before the producer reads any native payloads.</summary>
    public async Task<(DdsTextureDocument Document, TextureSurface Surface, NativeTextureExportSourceEvidence Evidence,
        IAsyncDisposable Lease)> AcquirePlannedTextureAsync(NativeTextureExportFingerprint plan, NativeFbxHdrConversion? hdr,
        Func<CancellationToken, Task<(DdsTextureDocument Document, TextureSurface Surface, NativeTextureExportSourceEvidence Evidence)>> producer,
        Func<NativeTextureExportSourceEvidence, CancellationToken, Task<SourceExtentVerificationLease>> verifySource,
        Func<CancellationToken, Task> ensureCurrent, AssetMemoryBudget budget, CancellationToken token = default)
    {
        var leases = new LeaseSet(); NativeTextureExportSourceEvidence? evidence = null;
        try
        {
            var prepared = await AcquireTextureCoreAsync(PlannedTextureKey(plan, hdr), async cancellation =>
            {
                var value = await producer(cancellation).ConfigureAwait(false); evidence = value.Evidence;
                return (value.Document, value.Surface);
            }, ensureCurrent, budget, token, () => evidence, async (stored, cancellation) =>
            {
                if (stored is null || JsonSerializer.Serialize(stored.Plan) != JsonSerializer.Serialize(plan))
                    throw new ArchiveException("TextureSourceMismatch", "The cached texture belongs to another export plan.");
                var verification = await verifySource(stored, cancellation).ConfigureAwait(false);
                leases.Add(verification); evidence = stored;
            }).ConfigureAwait(false);
            leases.Add(prepared.Lease);
            if (evidence is null) throw new InvalidDataException("PreparedTextureSourceEvidenceMissing");
            return (prepared.Document, prepared.Surface, evidence, leases);
        }
        catch { await leases.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task<(DdsTextureDocument Document, TextureSurface Surface, IAsyncDisposable Lease)> AcquireTextureCoreAsync(
        string key, Func<CancellationToken, Task<(DdsTextureDocument Document, TextureSurface Surface)>> producer,
        Func<CancellationToken, Task> validateSource, AssetMemoryBudget budget, CancellationToken token,
        Func<NativeTextureExportSourceEvidence?>? producedEvidence = null,
        Func<NativeTextureExportSourceEvidence?, CancellationToken, Task>? validateEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(cache); ArgumentNullException.ThrowIfNull(producer); ArgumentNullException.ThrowIfNull(validateSource);
        await validateSource(token).ConfigureAwait(false);
        (DdsTextureDocument Document, TextureSurface Surface)? created = null;
        CacheLease? lease = null; FileStream? pinnedInput = null;
        try
        {
            lease = await cache.AcquirePreparedAsync("export-texture", key, ".data", async (path, cancellation) =>
            {
                created = await producer(cancellation).ConfigureAwait(false);
                await WriteAsync(path, key, created.Value.Document, created.Value.Surface, cancellation, producedEvidence?.Invoke()).ConfigureAwait(false);
            }, async (path, cancellation) =>
            {
                pinnedInput?.Dispose(); pinnedInput = null; bool accepted = false;
                try
                {
                    pinnedInput = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                        128 * 1024, FileOptions.Asynchronous);
                    if (!await ValidateTextureStreamAsync(pinnedInput, key, cancellation).ConfigureAwait(false)) return false;
                    pinnedInput.Position = 0;
                    var header = await ReadHeaderAsync(pinnedInput, cancellation).ConfigureAwait(false);
                    if (validateEvidence is not null)
                    {
                        if (header.SourceEvidence is null) return false;
                        await validateEvidence(header.SourceEvidence, cancellation).ConfigureAwait(false);
                    }
                    pinnedInput.Position = 0; accepted = true; return true;
                }
                finally { if (!accepted) { pinnedInput?.Dispose(); pinnedInput = null; } }
            }, token).ConfigureAwait(false);
            await validateSource(token).ConfigureAwait(false);
            if (pinnedInput is null) throw new InvalidDataException("PreparedTextureValidationMissing");
            var held = new LeaseSet(); held.Add(lease); held.Add(pinnedInput);
            if (created is { } value) return (value.Document, value.Surface, held);
            var input = pinnedInput;
            var header = await ReadHeaderAsync(input, token).ConfigureAwait(false);
            using var scratch = budget.Reserve(checked(header.DdsBytes + header.PixelBytes + 4096L), "prepared export texture input");
            var encoded = new byte[header.DdsBytes]; await input.ReadExactlyAsync(encoded, token).ConfigureAwait(false);
            var document = new DdsTextureReader().Read(encoded, header.DocumentIdentity, header.Revision);
            TextureSurface surface;
            if (header.Surface.IsHdr)
            {
                var decoder = new DdsTextureDecoder(budget.MaximumBytes);
                using var decodeScratch = budget.Reserve(decoder.EstimateWorkingSetBytes(document, header.Surface.Subresource), "prepared HDR texture pixels");
                surface = await decoder.DecodeSubresourceAsync(document, header.Surface.Subresource, token).ConfigureAwait(false);
            }
            else
            {
                var pixels = new byte[header.PixelBytes]; await input.ReadExactlyAsync(pixels, token).ConfigureAwait(false);
                var s = header.Surface;
                surface = TextureSurface.RestoreRgba8(s.Width, s.Height, pixels, s.SourceIsHdr, s.IsSrgb, s.MipIndex,
                    s.Format, s.FormatName, s.SourceSha256, s.Subresource, s.Identity, s.Revision, s.DisplayTransform);
            }
            return (document, surface, held);
        }
        catch { pinnedInput?.Dispose(); if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static async Task WriteAsync(string path, string key, DdsTextureDocument document, TextureSurface surface, CancellationToken token, NativeTextureExportSourceEvidence? evidence = null)
    {
        var s = new SurfaceHeader(surface.Width, surface.Height, surface.IsHdr, surface.SourceIsHdr, surface.IsSrgb,
            surface.MipIndex, surface.SourceFormat, surface.SourceFormatName, surface.SourcePayloadSha256, surface.Subresource,
            surface.Identity, surface.Revision, surface.DisplayTransform);
        var header = new Header(key, Profile, document.Identity, document.Revision, document.OriginalBytes.Length,
            surface.IsHdr ? 0 : surface.Rgba8.Length, s) { SourceEvidence = evidence };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(header);
        if (json.Length > MaximumHeaderBytes) throw new InvalidDataException("ExportPreparationHeaderLimit");
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] prefix = new byte[12]; "DVEPTEX1"u8.CopyTo(prefix); BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(8), json.Length);
        await Part(prefix); await Part(json); await Part(document.OriginalBytes);
        if (!surface.IsHdr) await Part(surface.Rgba8);
        await output.WriteAsync(hash.GetHashAndReset(), token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false);
        async Task Part(ReadOnlyMemory<byte> bytes) { token.ThrowIfCancellationRequested(); hash.AppendData(bytes.Span); await output.WriteAsync(bytes, token).ConfigureAwait(false); }
    }

    private static async Task<Header> ReadHeaderAsync(Stream input, CancellationToken token)
    {
        byte[] prefix = new byte[12]; await input.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(8));
        if (!prefix.AsSpan(0, 8).SequenceEqual("DVEPTEX1"u8) || length is < 1 or > MaximumHeaderBytes)
            throw new InvalidDataException("ExportPreparationHeaderInvalid");
        byte[] json = new byte[length]; await input.ReadExactlyAsync(json, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<Header>(json) ?? throw new InvalidDataException("ExportPreparationHeaderInvalid");
    }

    private static async Task<bool> ValidateTextureStreamAsync(Stream input, string key, CancellationToken token)
    {
        try
        {
            if (input.Length is < 44 or > MaximumArtifactBytes) return false;
            var header = await ReadHeaderAsync(input, token).ConfigureAwait(false); var s = header.Surface;
            if (header.Key != key || header.Producer != Profile || header.DdsBytes < 128 || header.PixelBytes < 0 ||
                s.Width is < 1 or > DdsTextureReader.MaximumDimension || s.Height is < 1 or > DdsTextureReader.MaximumDimension ||
                (long)s.Width * s.Height > DdsTextureReader.MaximumPixelCount ||
                header.PixelBytes != (s.IsHdr ? 0 : checked(s.Width * s.Height * 4)) ||
                input.Position + header.DdsBytes + header.PixelBytes + 32 != input.Length) return false;
            input.Position = 0; byte[] buffer = new byte[128 * 1024]; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long left = input.Length - 32;
            while (left > 0) { token.ThrowIfCancellationRequested(); int count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(left, buffer.Length)), token).ConfigureAwait(false); if (count == 0) return false; hash.AppendData(buffer, 0, count); left -= count; }
            byte[] digest = new byte[32]; await input.ReadExactlyAsync(digest, token).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), digest);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or OverflowException or ArgumentException or NullReferenceException) { return false; }
    }
}
