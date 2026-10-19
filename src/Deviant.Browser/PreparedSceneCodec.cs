using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Textures;
using Deviant.Formats.Models;

namespace Deviant.Browser;

/// <summary>Versioned scene preparation retaining exact geometry, source ranges and material expressions.</summary>
public static class PreparedSceneCodec
{
    public const string Profile = "prepared-scene-v4-backed-pixels";
    private const uint Magic = 0x314E4353;
    private const int FormatVersion = 4;
    private const int MaximumMetadataBytes = 64 * 1024 * 1024;
    private const long MaximumFileBytes = 20L * 1024 * 1024 * 1024;
    private const long MaximumOwnedBytes = 2L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions json = new() { IncludeFields = true, MaxDepth = 128 };

    public static Task WriteAsync(string path, string key, RenderAssetSnapshot source, CancellationToken token = default)
        => WriteCoreAsync(() => new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous), true, key, source, token);

    /// <summary>Writes a complete scene to an empty readable, seekable output owned by the caller.</summary>
    public static Task WriteAsync(Stream output, string key, RenderAssetSnapshot source, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanRead || !output.CanWrite || !output.CanSeek || output.Position != 0 || output.Length != 0)
            throw new ArgumentException("Use an empty readable, writable, seekable output.", nameof(output));
        return WriteCoreAsync(() => output, false, key, source, token);
    }

    private static async Task WriteCoreAsync(Func<Stream> openOutput, bool ownsOutput, string key,
        RenderAssetSnapshot source, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source); token.ThrowIfCancellationRequested();
        if (source.HasIncompleteMaterials) throw new InvalidDataException("The scene has incomplete materials.");
        using var held = source.AcquireLease();
        using var scratch = source.MemoryBudget.Reserve(MaximumMetadataBytes + 1024 * 1024, "prepared scene encoding");
        if (source.Meshes.Values.Any(mesh => mesh.Pose is not null || mesh.SkinSourceGeometry is not null || mesh.SkinSourceMetadata is not null || mesh.SkinAttributes is not null))
            throw new InvalidDataException("A posed character requires its character preparation profile.");
        var textures = source.Materials.Values.SelectMany(material => material.TexturesBySlot.Values)
            .DistinctBy(texture => (texture.NativeIdentity, texture.RgbaSha256, texture.Sampling?.RawWord)).ToArray();
        var textureIds = textures.Select((texture, index) => (texture, index)).ToDictionary(pair =>
            (pair.texture.NativeIdentity, pair.texture.RgbaSha256, pair.texture.Sampling?.RawWord), pair => pair.index);
        var manifest = new Manifest(key, source.Revision, EstimateRetainedBytes(source), source.Occurrences.ToArray(),
            source.Region, source.Issues.ToArray(), source.Hierarchy.ToArray(), source.Coverage, source.SourceExtents.ToArray(),
            source.Materials.Values.Select(material => new MaterialRow(material.Asset with { SurfaceGraph = null },
                PackGraph(material.Asset.SurfaceGraph), material.TexturesBySlot.Select(pair => new SlotRow(pair.Key,
                    textureIds[(pair.Value.NativeIdentity, pair.Value.RgbaSha256, pair.Value.Sampling?.RawWord)])).ToArray())).ToArray(),
            textures.Select(texture => TextureRow.From(texture)).ToArray(), source.Meshes.Count)
        {
            RequiredMaterialKeys = source.Meshes.Values.SelectMany(mesh => mesh.MaterialsByPrimitive?.Values ??
                Enumerable.Empty<ResourceIdentity>()).Select(identity => identity.Key).Distinct(StringComparer.Ordinal).ToArray()
        };
        var file = openOutput();
        try
        {
        using (var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, true))
        {
            writer.Write(Magic); writer.Write(FormatVersion); WriteJson(writer, manifest);
            foreach (var mesh in source.Meshes.Values)
            {
                token.ThrowIfCancellationRequested();
                WriteJson(writer, mesh with { Geometry = mesh.Geometry with { Primitives = [] } });
                writer.Write(mesh.Geometry.Primitives.Count);
                foreach (var primitive in mesh.Geometry.Primitives)
                {
                    token.ThrowIfCancellationRequested();
                    WriteJson(writer, primitive with { Positions = [], Indices = [], Normals = null,
                        Tangents = null, TextureCoordinates = null, Colors = null });
                    WriteValues(writer, primitive.Positions, source.MemoryBudget, token);
                    WriteValues(writer, primitive.Indices, source.MemoryBudget, token);
                    WriteOptional(writer, primitive.Normals, source.MemoryBudget, token);
                    WriteOptional(writer, primitive.Tangents, source.MemoryBudget, token);
                    writer.Write(primitive.TextureCoordinates?.Count ?? -1);
                    foreach (var uv in primitive.TextureCoordinates ?? []) WriteValues(writer, uv, source.MemoryBudget, token);
                    WriteOptional(writer, primitive.Colors, source.MemoryBudget, token);
                }
            }
            var writtenBlobs = new HashSet<(TextureEncodingKind Kind, string Hash)>();
            foreach (var texture in textures)
            {
                token.ThrowIfCancellationRequested();
                var row = TextureRow.From(texture);
                if (!row.ValidMetadata() || texture.Surface.PixelSource is { } backing &&
                    (!string.Equals(backing.RgbaSha256, row.RgbaSha256, StringComparison.OrdinalIgnoreCase) ||
                     backing.ByteLength != (long)row.Width * row.Height * 4))
                    throw new InvalidDataException("The prepared texture identity is invalid.");
                if (!writtenBlobs.Add((row.EncodingKind, row.BlobSha256))) { writer.Write(0); continue; }
                using var hydrated = texture.Surface.PixelSource is not null
                    ? await texture.Surface.AcquireRgba8Async(token).ConfigureAwait(false) : null;
                var surface = hydrated?.Surface ?? texture.Surface;
                var bytes = surface.EncodedDds?.OriginalBytes ?? surface.Rgba8;
                if (surface.IsHdr || !IsDigest(row.RgbaSha256) || Hash(bytes.Span) != row.BlobSha256 ||
                    row.EncodingKind is TextureEncodingKind.Rgba8 or TextureEncodingKind.BackedRgba8 && (!surface.HasRgba8 || row.BlobSha256 != row.RgbaSha256) ||
                    row.EncodingKind == TextureEncodingKind.Dds && (surface.HasRgba8 || surface.EncodedDdsSelection is null ||
                        !TextureSurface.CanRetainEncodedDds(surface.EncodedDds!, surface.EncodedDdsSelection, token)))
                    throw new InvalidDataException("The prepared texture content changed.");
                writer.Write(bytes.Length); writer.Write(bytes.Span);
            }
            writer.Flush(); await file.FlushAsync(token).ConfigureAwait(false);
        }
        file.Position = 0;
        var digest = await SHA256.HashDataAsync(file, token).ConfigureAwait(false);
        file.Seek(0, SeekOrigin.End);
        await file.WriteAsync(digest, token).ConfigureAwait(false);
        await file.FlushAsync(token).ConfigureAwait(false);
        }
        finally { if (ownsOutput) await file.DisposeAsync().ConfigureAwait(false); }
    }

    public static async Task<bool> ValidateAsync(string path, string key, SourceRevision revision, CancellationToken token = default,
        AssetMemoryBudget? budget = null)
    {
        try
        {
            await using var file = File.OpenRead(path);
            if (file.Length is < 48 or > MaximumFileBytes) return false;
            using var reader = new BinaryReader(file, System.Text.Encoding.UTF8, true);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion) return false;
            int metadataBytes = reader.ReadInt32();
            if (metadataBytes is < 1 or > MaximumMetadataBytes || metadataBytes > file.Length - 44) return false;
            using var scratch = budget?.Reserve(checked(metadataBytes * 4L + 128 * 1024), "prepared scene validation");
            file.Position = sizeof(uint) + sizeof(int);
            var manifest = ReadJson<Manifest>(reader);
            if (manifest.Key != key || manifest.Revision != revision || manifest.OwnedBytes is < 1 or > MaximumOwnedBytes ||
                manifest.MeshCount is < 0 or > NativeModelSession.MaximumLargeSelections ||
                manifest.Textures is null || manifest.Textures.Any(row => !row.ValidMetadata()) || !MaterialsComplete(manifest)) return false;
            if (manifest.RequiredMaterialKeys is null && !LegacyMaterialReferencesComplete(reader, manifest, budget, token)) return false;
            file.Position = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long remaining = file.Length - 32;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var count = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                if (count == 0) return false;
                hash.AppendData(buffer, 0, count); remaining -= count;
            }
            var expected = new byte[32]; await file.ReadExactlyAsync(expected, token).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected);
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or OverflowException) { return false; }
    }

    public static async Task<RenderAssetSnapshot> ReadAsync(string path, string key, ArchiveSourceSession source,
        ArchiveSnapshot archive, AssetMemoryBudget budget, bool allowLargeData, CancellationToken token = default,
        Func<IDisposable>? acquireBackingOwner = null)
    {
        using var timing = AssetLoadDiagnostics.Measure("PreparedSceneRead", key);
        var resources = new List<IDisposable>();
        if (allowLargeData) resources.Add(budget.AllowLargeSceneLoad());
        try
        {
            using var scratch = budget.Reserve(MaximumMetadataBytes + 1024 * 1024, "prepared scene decoding");
            using var file = File.OpenRead(path);
            using var reader = new BinaryReader(file);
            if (file.Length is < 48 or > MaximumFileBytes || reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion)
                throw new InvalidDataException("The scene preparation format is invalid.");
            var manifest = ReadJson<Manifest>(reader);
            if (manifest.Key != key || manifest.Revision != new SourceRevision(archive.IndexContentId, archive.PhysicalSourceStamp) ||
                manifest.OwnedBytes is < 1 or > MaximumOwnedBytes || manifest.MeshCount is < 0 or > NativeModelSession.MaximumLargeSelections ||
                manifest.Textures is null || manifest.Textures.Any(row => !row.ValidMetadata()) || !MaterialsComplete(manifest))
                throw new InvalidDataException("The scene preparation identity is invalid.");
            resources.Add(budget.Reserve(manifest.OwnedBytes, "prepared scene content"));
            var admission = new BufferAdmission(manifest.OwnedBytes);
            admission.Use(checked(manifest.MeshCount * 128L));
            var meshes = new Dictionary<string, RenderMesh>(StringComparer.Ordinal);
            for (int meshIndex = 0; meshIndex < manifest.MeshCount; meshIndex++)
            {
                token.ThrowIfCancellationRequested();
                var mesh = ReadJson<RenderMesh>(reader); int count = Count(reader, 65_536);
                admission.Use(checked(count * 8L + 24));
                var primitives = new MeshPrimitive[count];
                for (int index = 0; index < count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var primitive = ReadJson<MeshPrimitive>(reader);
                    var positions = ReadValues<Vector3>(reader, admission, token); var indices = ReadValues<uint>(reader, admission, token);
                    var normals = ReadOptional<Vector3>(reader, admission, token); var tangents = ReadOptional<Vector4>(reader, admission, token);
                    int sets = reader.ReadInt32(); if (sets is < -1 or > 16) throw new InvalidDataException("The texture coordinate count is invalid.");
                    Vector2[][]? uv = sets < 0 ? null : new Vector2[sets][];
                    for (int set = 0; set < sets; set++) uv![set] = ReadValues<Vector2>(reader, admission, token);
                    primitives[index] = primitive with { Positions = positions, Indices = indices, Normals = normals,
                        Tangents = tangents, TextureCoordinates = uv, Colors = ReadOptional<Vector4>(reader, admission, token) };
                }
                meshes.Add(mesh.Key, mesh with { Geometry = mesh.Geometry with { Primitives = primitives } });
            }
            var materialKeys = manifest.Materials.Select(row => row.Asset.Identity.Key).ToHashSet(StringComparer.Ordinal);
            if (meshes.Values.Any(mesh => mesh.MaterialsByPrimitive?.Values.Any(identity => !materialKeys.Contains(identity.Key)) == true))
                throw new InvalidDataException("A required prepared material is missing.");
            var textures = new RenderTexture[manifest.Textures.Length];
            var blobs = new Dictionary<(TextureEncodingKind Kind, string Hash), byte[]>();
            var backings = new Dictionary<(TextureEncodingKind Kind, string Hash), PreparedTexturePixelSource>();
            var documents = new Dictionary<string, DdsTextureDocument>(StringComparer.Ordinal);
            for (int index = 0; index < textures.Length; index++)
            {
                token.ThrowIfCancellationRequested(); var row = manifest.Textures[index];
                int length = Count(reader, checked((int)DdsTextureReader.MaximumInputBytes));
                var blobKey = (row.EncodingKind, row.BlobSha256);
                if (row.EncodingKind == TextureEncodingKind.BackedRgba8)
                {
                    PreparedTexturePixelSource pixels;
                    if (length == 0)
                    {
                        if (!backings.TryGetValue(blobKey, out pixels!))
                            throw new InvalidDataException("The prepared texture blob reference is invalid.");
                    }
                    else
                    {
                        if (length != checked(row.Width * row.Height * 4) || length > file.Length - file.Position - 32 || backings.ContainsKey(blobKey))
                            throw new InvalidDataException("The prepared pixel range is invalid.");
                        long offset = file.Position;
                        pixels = await PreparedTexturePixelSource.OpenRangeAsync(path, offset, length, row.RgbaSha256,
                            row.RestoreBacking, budget, row.Resized, token, acquireBackingOwner).ConfigureAwait(false);
                        resources.Add(pixels); backings.Add(blobKey, pixels);
                        file.Position = checked(offset + length);
                    }
                    textures[index] = new(row.Identity, row.RestoreBacking(pixels), row.RgbaSha256, row.SourceIdentity, row.CatalogIdentity)
                        { PreviewWasResized = row.Resized, Sampling = row.Sampling };
                    continue;
                }
                byte[] bytes;
                if (length == 0)
                {
                    if (!blobs.TryGetValue(blobKey, out bytes!)) throw new InvalidDataException("The prepared texture blob reference is invalid.");
                }
                else
                {
                    if (row.EncodingKind == TextureEncodingKind.Rgba8 && length != checked(row.Width * row.Height * 4))
                        throw new InvalidDataException("Prepared pixels have invalid dimensions.");
                    if (length > file.Length - file.Position - 32) throw new InvalidDataException("The prepared texture extent is invalid.");
                    admission.Use(checked(length + 24L));
                    bytes = reader.ReadBytes(length);
                    if (bytes.Length != length || Hash(bytes) != row.BlobSha256 || blobs.ContainsKey(blobKey))
                        throw new InvalidDataException("Prepared texture content is corrupt.");
                    blobs.Add(blobKey, bytes);
                }
                DdsTextureDocument? document = null;
                if (row.EncodingKind == TextureEncodingKind.Dds && !documents.TryGetValue(row.BlobSha256, out document))
                {
                    document = new DdsTextureReader().TakeOwnership(bytes, row.SurfaceIdentity, row.SurfaceRevision);
                    if (document.ContentSha256 != row.BlobSha256) throw new InvalidDataException("The prepared DDS digest is invalid.");
                    documents.Add(row.BlobSha256, document);
                }
                textures[index] = row.Restore(bytes, document);
            }
            if (file.Position != file.Length - 32) throw new InvalidDataException("The scene preparation length is invalid.");
            var materials = manifest.Materials.ToDictionary(row => row.Asset.Identity,
                row => new RenderMaterial(row.Asset with { SurfaceGraph = UnpackGraph(row.Graph) },
                    row.Slots.ToDictionary(slot => slot.Slot, slot => textures[slot.Texture])));
            if (manifest.Occurrences.Any(row => !meshes.ContainsKey(row.GeometryKey)) || meshes.Values.Any(mesh =>
                !GeometryValidator.Validate(mesh.Geometry, token).IsValid)) throw new InvalidDataException("Prepared scene geometry is invalid.");
            await source.VerifyAsync(manifest.Extents, token, SourceExtentVerifier.MaximumBytes,
                SourceExtentVerifier.MaximumLargeExtentCount).ConfigureAwait(false);
            source.EnsureCurrent();
            return new(manifest.Revision, meshes, manifest.Occurrences, materials, manifest.Region, manifest.Issues,
                manifest.Hierarchy, manifest.Coverage, manifest.Extents, budget, resources);
        }
        catch { foreach (var owned in resources) owned.Dispose(); throw; }
    }

    private static long EstimateRetainedBytes(RenderAssetSnapshot snapshot)
        => checked(16L * 1024 * 1024 + snapshot.Occurrences.Count * 4096L + snapshot.Hierarchy.Count * 4096L +
            snapshot.Materials.Count * 64L * 1024 + snapshot.Meshes.Values.Sum(mesh => mesh.Geometry.Primitives.Sum(p =>
                p.Positions.Count * 12L + p.Indices.Count * 4L + (p.Normals?.Count ?? 0) * 12L + (p.Tangents?.Count ?? 0) * 16L +
                (p.TextureCoordinates?.Sum(uv => uv.Count) ?? 0) * 8L + (p.Colors?.Count ?? 0) * 16L + 4096)) +
            snapshot.Materials.Values.SelectMany(material => material.TexturesBySlot.Values).DistinctBy(texture =>
                (TextureRow.Encoding(texture.Surface),
                 texture.Surface.EncodedDds?.ContentSha256 ?? texture.RgbaSha256))
                .Sum(texture => texture.Surface.PixelSource is null
                    ? (texture.Surface.EncodedDds?.OriginalBytes.Length ?? texture.Surface.Rgba8.Length) + 4096L : 0));

    private static void WriteValues<T>(BinaryWriter writer, IReadOnlyList<T> values, AssetMemoryBudget budget, CancellationToken token) where T : unmanaged
    {
        token.ThrowIfCancellationRequested(); writer.Write(values.Count);
        if (values is T[] array) { writer.Write(MemoryMarshal.AsBytes(array.AsSpan())); return; }
        using var temporary = budget.Reserve(checked((long)values.Count * Marshal.SizeOf<T>()), "prepared vector encoding");
        writer.Write(MemoryMarshal.AsBytes(values.ToArray().AsSpan()));
    }
    private static T[] ReadValues<T>(BinaryReader reader, BufferAdmission admission, CancellationToken token) where T : unmanaged
    {
        int count = Count(reader, 16_777_216); long bytes = checked((long)count * Marshal.SizeOf<T>());
        if (bytes > reader.BaseStream.Length - reader.BaseStream.Position - 32) throw new InvalidDataException("The prepared vector extent is invalid.");
        admission.Use(checked(bytes + 24));
        var array = new T[count]; var destination = MemoryMarshal.AsBytes(array.AsSpan());
        while (!destination.IsEmpty)
        {
            token.ThrowIfCancellationRequested(); int length = Math.Min(destination.Length, 1024 * 1024);
            reader.BaseStream.ReadExactly(destination[..length]); destination = destination[length..];
        }
        return array;
    }
    private static void WriteOptional<T>(BinaryWriter writer, IReadOnlyList<T>? values, AssetMemoryBudget budget, CancellationToken token) where T : unmanaged
    { writer.Write(values is not null); if (values is not null) WriteValues(writer, values, budget, token); }
    private static T[]? ReadOptional<T>(BinaryReader reader, BufferAdmission admission, CancellationToken token) where T : unmanaged
        => reader.ReadBoolean() ? ReadValues<T>(reader, admission, token) : null;
    private sealed class BufferAdmission(long remaining)
    {
        public void Use(long bytes)
        {
            if (bytes < 0 || bytes > remaining) throw new InvalidDataException("Prepared buffers exceed their admitted content size.");
            remaining -= bytes;
        }
    }
    private static int Count(BinaryReader reader, int maximum)
    { int count = reader.ReadInt32(); return count >= 0 && count <= maximum ? count : throw new InvalidDataException("The prepared collection count is invalid."); }
    private static void WriteJson<T>(BinaryWriter writer, T value)
    { var bytes = JsonSerializer.SerializeToUtf8Bytes(value, json); if (bytes.Length > MaximumMetadataBytes) throw new InvalidDataException("Scene metadata exceeds its preparation limit."); writer.Write(bytes.Length); writer.Write(bytes); }
    private static T ReadJson<T>(BinaryReader reader)
    { int length = Count(reader, MaximumMetadataBytes); var bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException(); return JsonSerializer.Deserialize<T>(bytes, json) ?? throw new InvalidDataException("Scene metadata is invalid."); }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static GraphRow? PackGraph(MaterialSurfaceGraph? graph)
    {
        if (graph is null) return null;
        var nodes = new List<ExpressionRow>(); var visited = new Dictionary<MaterialExpression, int>(ReferenceEqualityComparer.Instance);
        int Add(MaterialExpression? value)
        {
            if (value is null) return -1; if (visited.TryGetValue(value, out int index)) return index;
            var arguments = value.Arguments.Select(argument => Add(argument)).ToArray(); index = nodes.Count;
            if (index > 65_536) throw new InvalidDataException("The material expression graph exceeds its preparation limit.");
            nodes.Add(new(value.Operation, value.ValueType, arguments, value.Constants?.ToArray(), value.Input, value.TextureSlot, value.Components, value.SourceExtent));
            visited.Add(value, index); return index;
        }
        int color = Add(graph.BaseColor), opacity = Add(graph.Opacity), cutoff = Add(graph.AlphaCutoff);
        return new(nodes.ToArray(), color, opacity, cutoff, graph.RequiredInputs.ToArray(), graph.TextureSlots.ToArray(), graph.Diagnostics.ToArray(), graph.IsUnlit);
    }
    private static MaterialSurfaceGraph? UnpackGraph(GraphRow? graph)
    {
        if (graph is null) return null;
        if (graph.Nodes.Length > 65_537) throw new InvalidDataException("The prepared material graph is invalid.");
        var nodes = new MaterialExpression[graph.Nodes.Length];
        for (int index = 0; index < nodes.Length; index++)
        {
            var row = graph.Nodes[index]; if (row.Arguments.Any(argument => argument < 0 || argument >= index)) throw new InvalidDataException("The prepared expression order is invalid.");
            nodes[index] = new(row.Operation, row.Type, row.Arguments.Select(argument => nodes[argument]).ToArray(),
                row.Constants, row.Input, row.Slot, row.Components, row.Extent);
        }
        MaterialExpression? Get(int index) => index == -1 ? null : index >= 0 && index < nodes.Length ? nodes[index] : throw new InvalidDataException("The prepared expression root is invalid.");
        return new(Get(graph.Color), Get(graph.Opacity), Get(graph.Cutoff), graph.Inputs, graph.Slots, graph.Diagnostics) { IsUnlit = graph.IsUnlit };
    }

    private static bool MaterialsComplete(Manifest manifest)
    {
        if (manifest.Materials is null || manifest.Materials.Any(row => row is null || row.Asset is null ||
            row.Asset.Bindings is null || row.Asset.Diagnostics is null || row.Slots is null ||
            row.Graph is { Slots: null } || row.Slots.Any(slot => slot is null || slot.Slot < 0 ||
                slot.Texture < 0 || slot.Texture >= manifest.Textures.Length) ||
            row.Slots.Select(slot => slot.Slot).Distinct().Count() != row.Slots.Length ||
            !RenderMaterial.HasRequiredSlots(row.Asset, row.Slots.Select(slot => slot.Slot), row.Graph?.Slots ?? []))) return false;
        var available = manifest.Materials.Select(row => row.Asset.Identity.Key).ToHashSet(StringComparer.Ordinal);
        return available.Count == manifest.Materials.Length &&
            (manifest.RequiredMaterialKeys is null || manifest.RequiredMaterialKeys.All(available.Contains));
    }

    private static bool LegacyMaterialReferencesComplete(BinaryReader reader, Manifest manifest, AssetMemoryBudget? budget,
        CancellationToken token)
    {
        var available = manifest.Materials.Select(row => row.Asset.Identity.Key).ToHashSet(StringComparer.Ordinal);
        for (int index = 0; index < manifest.MeshCount; index++)
        {
            token.ThrowIfCancellationRequested();
            long at = reader.BaseStream.Position;
            int length = Count(reader, MaximumMetadataBytes);
            using var scratch = budget?.Reserve(checked(length * 4L + 128 * 1024), "prepared material references");
            reader.BaseStream.Position = at;
            var mesh = ReadJson<RenderMesh>(reader);
            if (mesh.MaterialsByPrimitive?.Values.Any(identity => !available.Contains(identity.Key)) == true) return false;
            int primitives = Count(reader, 65_536);
            for (int primitive = 0; primitive < primitives; primitive++)
            {
                token.ThrowIfCancellationRequested();
                SkipBytes(Count(reader, MaximumMetadataBytes));
                SkipValues(12); SkipValues(4);
                if (reader.ReadBoolean()) SkipValues(12);
                if (reader.ReadBoolean()) SkipValues(16);
                int sets = reader.ReadInt32();
                if (sets is < -1 or > 16) throw new InvalidDataException("The texture coordinate count is invalid.");
                for (int set = 0; set < sets; set++) SkipValues(8);
                if (reader.ReadBoolean()) SkipValues(16);
            }
        }
        return true;

        void SkipValues(int elementBytes) => SkipBytes(checked((long)Count(reader, 16_777_216) * elementBytes));
        void SkipBytes(long bytes)
        {
            if (bytes < 0 || bytes > reader.BaseStream.Length - reader.BaseStream.Position - 32)
                throw new InvalidDataException("The prepared geometry extent is invalid.");
            reader.BaseStream.Position = checked(reader.BaseStream.Position + bytes);
        }
    }

    private sealed record Manifest(string Key, SourceRevision Revision, long OwnedBytes, RenderOccurrence[] Occurrences,
        GeometryBounds Region, RenderIssue[] Issues, RenderHierarchyNode[] Hierarchy, RenderCoverage Coverage,
        SourceExtent[] Extents, MaterialRow[] Materials, TextureRow[] Textures, int MeshCount)
    {
        public string[]? RequiredMaterialKeys { get; init; }
    }
    private sealed record SlotRow(int Slot, int Texture);
    private sealed record MaterialRow(MaterialAsset Asset, GraphRow? Graph, SlotRow[] Slots);
    private sealed record ExpressionRow(MaterialExpressionOperation Operation, MaterialValueType Type, int[] Arguments,
        float[]? Constants, MaterialInput? Input, int? Slot, string? Components, DecodedExtent? Extent);
    private sealed record GraphRow(ExpressionRow[] Nodes, int Color, int Opacity, int Cutoff, MaterialInput[] Inputs,
        int[] Slots, string[] Diagnostics, bool IsUnlit);
    private enum TextureEncodingKind { Rgba8, Dds, BackedRgba8 }
    private sealed record TextureRow(ResourceIdentity Identity, string RgbaSha256, ResourceIdentity? SourceIdentity,
        ResourceIdentity? CatalogIdentity, bool Resized, NativeTextureSamplingProfile? Sampling,
        int Width, int Height, bool SourceIsHdr, bool IsSrgb, int Mip, TextureStorageFormat Format, string FormatName,
        string PayloadSha256, TextureSubresourceSelection Subresource, ResourceIdentity? SurfaceIdentity,
        SourceRevision? SurfaceRevision, TextureDisplayTransform? DisplayTransform, TextureEncodingKind EncodingKind,
        string BlobSha256, TextureSubresourceSelection? EncodedSelection)
    {
        public static TextureRow From(RenderTexture texture)
        { var surface = texture.Surface; return new(texture.NativeIdentity, texture.RgbaSha256, texture.SourceIdentity,
            texture.CatalogIdentity, texture.PreviewWasResized, texture.Sampling, surface.Width, surface.Height, surface.SourceIsHdr,
            surface.IsSrgb, surface.MipIndex, surface.SourceFormat, surface.SourceFormatName, surface.SourcePayloadSha256,
            surface.Subresource, surface.Identity, surface.Revision, surface.DisplayTransform,
            Encoding(surface),
            surface.EncodedDds?.ContentSha256 ?? texture.RgbaSha256, surface.EncodedDdsSelection); }
        public bool ValidMetadata() => Width is >= 1 and <= DdsTextureReader.MaximumDimension &&
            Height is >= 1 and <= DdsTextureReader.MaximumDimension && (long)Width * Height <= DdsTextureReader.MaximumPixelCount &&
            Mip >= 0 && IsDigest(RgbaSha256) && IsDigest(BlobSha256) && IsDigest(PayloadSha256) &&
            Subresource is not null && Subresource.MipIndex == Mip && Enum.IsDefined(Format) && !string.IsNullOrWhiteSpace(FormatName) &&
            Enum.IsDefined(EncodingKind) && (EncodingKind is TextureEncodingKind.Rgba8 or TextureEncodingKind.BackedRgba8
                ? BlobSha256 == RgbaSha256 && EncodedSelection is null
                : EncodedSelection is not null && DisplayTransform is null && !SourceIsHdr && !Resized);
        public static TextureEncodingKind Encoding(TextureSurface surface) => surface.PixelSource is not null
            ? TextureEncodingKind.BackedRgba8 : surface.EncodedDds is null ? TextureEncodingKind.Rgba8 : TextureEncodingKind.Dds;
        public TextureSurface RestoreBacking(ITexturePixelSource pixels) => TextureSurface.RestoreBackedRgba8(Width, Height,
            pixels, SourceIsHdr, IsSrgb, Mip, Format, FormatName, PayloadSha256, Subresource, SurfaceIdentity,
            SurfaceRevision, DisplayTransform);
        public RenderTexture Restore(byte[] bytes, DdsTextureDocument? document) => new(Identity,
            EncodingKind == TextureEncodingKind.Dds
                ? TextureSurface.RestoreEncodedDds(Width, Height, document!, SourceIsHdr, IsSrgb, Mip, Format, FormatName,
                    PayloadSha256, Subresource, SurfaceIdentity, SurfaceRevision, EncodedSelection)
                : TextureSurface.RestoreRgba8(Width, Height, bytes, SourceIsHdr, IsSrgb, Mip, Format, FormatName,
                    PayloadSha256, Subresource, SurfaceIdentity, SurfaceRevision, DisplayTransform), RgbaSha256, SourceIdentity, CatalogIdentity)
            { PreviewWasResized = Resized, Sampling = Sampling };
    }
}
