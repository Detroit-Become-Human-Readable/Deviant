using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Textures;

namespace Deviant.Export.Models;

public sealed record MaterialBakeTexture(TextureSurface Surface, NativeTextureSamplingProfile Sampling);
public sealed record MaterialSurfaceMap(DdsTextureDocument Document, TextureSurface Surface, string GraphSha256,
    int TextureCoordinateSet = 0)
{
    public IReadOnlyList<Vector2>? PolygonCornerCoordinates { get; init; }
}

/// <summary>Resamples a surface expression into a primary-UV colour and coverage map.</summary>
public static class MaterialSurfaceMapBaker
{
    private static readonly float[] SrgbByteValues = CreateSrgbByteValues();
    private const long MaximumRasterMemoBytes = 32L * 1024 * 1024;
    private const int MaximumRasterMemoPixels = 512 * 512;
    private const int MaximumMemoTriangleSignatures = 4096;
    public static MaterialSurfaceMap Bake(MaterialAsset material, MeshPrimitive primitive,
        IReadOnlyDictionary<int, MaterialBakeTexture> textures, int width, int height,
        FbxExportMemoryReservation reserve, CancellationToken token = default, int textureCoordinateSet = 0,
        IReadOnlyList<Vector2>? polygonCornerCoordinates = null, long reservedOutputBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(material); ArgumentNullException.ThrowIfNull(primitive);
        ArgumentNullException.ThrowIfNull(textures); ArgumentNullException.ThrowIfNull(reserve);
        var graph = material.SurfaceGraph ?? throw new InvalidDataException("The material has no surface graph.");
        if (graph.BaseColor is null) throw new InvalidDataException("The material has no colour expression.");
        if (width < 1 || height < 1 || width > DdsTextureReader.MaximumDimension || height > DdsTextureReader.MaximumDimension ||
            (long)width * height > DdsTextureReader.MaximumPixelCount)
            throw new InvalidDataException("The bake extent exceeds the texture limits.");
        if (reservedOutputBytes < 0) throw new ArgumentOutOfRangeException(nameof(reservedOutputBytes));
        var diagnosticKey = $"{material.Identity.Key}|{primitive.Key}|uv={textureCoordinateSet}|{width}x{height}";
        bool collectCounters = AssetLoadDiagnostics.CountersEnabled;
        long vertexEvaluations = 0, rasterEvaluations = 0, textureSamples = 0, coveredTexels = 0, revisitedTexels = 0, gutterTexels = 0;
        long exactInputReuses = 0, memoBytes = 0;
        int activeTriangle = -1;
        int rootNodeOccurrences = 0;
        var diagnosticNodes = collectCounters ? new HashSet<MaterialExpression>(ReferenceEqualityComparer.Instance) : null;
        bool rasterStarted = false, uvOnly = false, completed = false;
        try
        {
            using var graphTiming = AssetLoadDiagnostics.Measure("BakeGraphCompile", diagnosticKey);
            var output = new MaterialBakeExpressionEvaluator(graph);
            var inputs = new HashSet<MaterialInput>(); var slots = new HashSet<int>();
            foreach (var root in new[] { graph.BaseColor, graph.Opacity, graph.AlphaCutoff }.OfType<MaterialExpression>()) Gather(root);
            graphTiming?.Dispose();
            using var preflightTiming = AssetLoadDiagnostics.Measure("BakePreflight", diagnosticKey);
            if (inputs.Any(input => input is MaterialInput.ViewDepth or MaterialInput.ViewDirection or MaterialInput.ViewNormal or MaterialInput.SceneViewDepth))
                throw new InvalidDataException("The surface depends on the viewing position or camera; it cannot be represented by a fixed UV map.");
            foreach (var slot in slots)
                if (!textures.TryGetValue(slot, out var texture) || texture.Surface.IsHdr || texture.Sampling.ComparisonEnabled)
                    throw new InvalidDataException($"Texture slot {slot} cannot be sampled as a colour surface.");
            if (primitive.Topology != PrimitiveTopology.Triangles || primitive.Indices.Count % 3 != 0 ||
                primitive.TextureCoordinates is not { Count: > 0 } sets || textureCoordinateSet < 0 ||
                (polygonCornerCoordinates is null ? textureCoordinateSet >= sets.Count || sets[textureCoordinateSet].Count != primitive.Positions.Count
                    : textureCoordinateSet != sets.Count || polygonCornerCoordinates.Count != primitive.Indices.Count ||
                        polygonCornerCoordinates.Any(uv => !Finite(uv) || uv.X < 0 || uv.Y < 0 || uv.X > 1 || uv.Y > 1)))
                throw new InvalidDataException("A triangle surface with a primary UV set is required for baking.");
            if (sets.Take(4).Any(set => set.Count != primitive.Positions.Count))
                throw new InvalidDataException("Each vertex UV set must match the position count.");
            foreach (var input in inputs)
            {
                if (input is >= MaterialInput.Uv0 and <= MaterialInput.Uv3 &&
                    ((int)input >= sets.Count || sets[(int)input].Count != primitive.Positions.Count))
                    throw new InvalidDataException($"The {input} coordinate set is unavailable.");
                if (input == MaterialInput.VertexColor && primitive.Colors?.Count != primitive.Positions.Count ||
                    input == MaterialInput.WorldNormal && primitive.Normals?.Count != primitive.Positions.Count)
                    throw new InvalidDataException($"The {input} vertex input is unavailable.");
            }
            if (polygonCornerCoordinates is null)
            {
                foreach (var coordinate in sets[textureCoordinateSet])
                {
                    token.ThrowIfCancellationRequested();
                    if (!Finite(coordinate)) throw new InvalidDataException("Nonfinite UV coordinate.");
                    if (coordinate.X < 0 || coordinate.Y < 0 || coordinate.X > 1 || coordinate.Y > 1)
                        throw new MaterialBakeAtlasRequiredException("UV coordinates outside the output map require an atlas.");
                }
            }
            var requiredUvSets = Enumerable.Range(0, Math.Min(4, sets.Count)).Where(set => inputs.Contains((MaterialInput)set)).ToArray();
            bool needsColor = inputs.Contains(MaterialInput.VertexColor), needsPosition = inputs.Contains(MaterialInput.WorldPosition),
                needsNormal = inputs.Contains(MaterialInput.WorldNormal);
            int pixels = checked(width * height);
            // The output DDS holds the raster directly; coverage and one reusable gutter buffer are scratch.
            using var scratch = reserve(checked(pixels * 6L + 16384L -
                Math.Min(reservedOutputBytes, pixels * 4L + 148)), "material map baking");
            var bytes = CreateDdsBuffer(width, height);
            var rgba = bytes.AsMemory(148); var covered = new bool[pixels];
            var uv = new Vector4[4]; Vector4 vertexColor = default, position = default, normal = default;
            Func<MaterialInput, Vector4> readInput = input => input switch
            {
                >= MaterialInput.Uv0 and <= MaterialInput.Uv3 => uv[(int)input],
                MaterialInput.VertexColor => vertexColor, MaterialInput.WorldPosition => position,
                MaterialInput.WorldNormal => normal,
                _ => throw new InvalidDataException($"The {input} vertex input cannot be baked.")
            };
            Func<int, Vector2, Vector4> sample = (slot, point) =>
            {
                if (collectCounters) textureSamples++;
                return Sample(textures[slot], point);
            };
            uvOnly = polygonCornerCoordinates is null && inputs.All(input => (int)input == textureCoordinateSet && input is >= MaterialInput.Uv0 and <= MaterialInput.Uv3);
            var memoRequiredInputs = inputs.OrderBy(input => (int)input).ToArray();
            long requestedMemoBytes = checked((long)pixels * memoRequiredInputs.Length * 16 + 128);
            IDisposable? memoMemory = null;
            if (!uvOnly && polygonCornerCoordinates is null && pixels <= MaximumRasterMemoPixels &&
                requestedMemoBytes <= MaximumRasterMemoBytes &&
                HasRepeatedSourceUvTriangle(primitive, sets[textureCoordinateSet], reserve, token))
            {
                try { memoMemory = reserve(requestedMemoBytes, "material raster input memo"); }
                catch (ArchiveException error) when (error.Code == "AssetMemoryLimit") { }
            }
            using var memoReservation = memoMemory;
            var memoValues = memoReservation is null ? null : new Vector4[checked(pixels * memoRequiredInputs.Length)];
            if (memoValues is not null) memoBytes = requestedMemoBytes;
            using var vertexScratch = reserve(checked(primitive.Positions.Count * 64L + 4096L), "material bake UV preflight");
            var vertexValues = new Dictionary<Vector2, uint>();
            Span<byte> vertexPixel = stackalloc byte[4];
            for (int vertex = 0; polygonCornerCoordinates is null && vertex < primitive.Positions.Count; vertex++)
            {
                token.ThrowIfCancellationRequested(); SetInputs(vertex, vertex, vertex, 1, 0, 0);
                var coordinate = sets[textureCoordinateSet][vertex];
                if (!Finite(coordinate)) throw new InvalidDataException("Nonfinite UV coordinate.");
                var address = coordinate;
                EvaluatePixel(vertexPixel); uint packed = BinaryPrimitives.ReadUInt32LittleEndian(vertexPixel);
                if (vertexValues.TryGetValue(address, out uint previous))
                {
                    for (int channel = 0; channel < 4; channel++)
                        if (Math.Abs((int)((previous >> (channel * 8)) & 255) - vertexPixel[channel]) > 2)
                            throw new MaterialBakeAtlasRequiredException("Overlapping UVs at a shared vertex require different surface values.");
                }
                else vertexValues.Add(address, packed);
            }
            preflightTiming?.Dispose();
            rasterStarted = true;
            using var rasterTiming = AssetLoadDiagnostics.Measure("BakeRaster", diagnosticKey);
            if (uvOnly)
            {
                for (int y = 0; y < height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int x = 0; x < width; x++)
                    {
                        uv[textureCoordinateSet] = new((x + .5f) / width, (y + .5f) / height, 0, 0);
                        WritePixel(y * width + x);
                    }
                }
            }
            else
            {
                for (int triangle = 0; triangle < primitive.Indices.Count; triangle += 3)
                {
                    activeTriangle = triangle / 3;
                    token.ThrowIfCancellationRequested();
                    int a = Index(triangle), b = Index(triangle + 1), c = Index(triangle + 2);
                    var pa = polygonCornerCoordinates is null ? sets[textureCoordinateSet][a] : polygonCornerCoordinates[triangle];
                    var pb = polygonCornerCoordinates is null ? sets[textureCoordinateSet][b] : polygonCornerCoordinates[triangle + 1];
                    var pc = polygonCornerCoordinates is null ? sets[textureCoordinateSet][c] : polygonCornerCoordinates[triangle + 2];
                    if (!Finite(pa) || !Finite(pb) || !Finite(pc)) throw new InvalidDataException("Nonfinite UV coordinate.");
                    var minimum = Vector2.Min(pa, Vector2.Min(pb, pc)); var maximum = Vector2.Max(pa, Vector2.Max(pb, pc));
                    if (minimum.X < -4 || minimum.Y < -4 || maximum.X > 5 || maximum.Y > 5)
                        throw new InvalidDataException("The surface exceeds the bounded UV tile range.");
                    float area = Cross(pb - pa, pc - pa);
                    if (polygonCornerCoordinates is not null && area < 0)
                    { (b, c) = (c, b); (pb, pc) = (pc, pb); area = -area; }
                    if (MathF.Abs(area) < 1e-12f) continue;
                    double atlasArea = polygonCornerCoordinates is null ? 0 : Edge(pa, pb, pc);
                    for (int tileY = polygonCornerCoordinates is null ? (int)MathF.Floor(minimum.Y) : 0;
                        tileY <= (polygonCornerCoordinates is null ? (int)MathF.Floor(maximum.Y) : 0); tileY++)
                    for (int tileX = polygonCornerCoordinates is null ? (int)MathF.Floor(minimum.X) : 0;
                        tileX <= (polygonCornerCoordinates is null ? (int)MathF.Floor(maximum.X) : 0); tileX++)
                    {
                        int x0 = Math.Clamp((int)MathF.Ceiling((minimum.X - tileX) * width - .5f), 0, width - 1);
                        int x1 = Math.Clamp((int)MathF.Floor((maximum.X - tileX) * width - .5f), 0, width - 1);
                        int y0 = Math.Clamp((int)MathF.Ceiling((minimum.Y - tileY) * height - .5f), 0, height - 1);
                        int y1 = Math.Clamp((int)MathF.Floor((maximum.Y - tileY) * height - .5f), 0, height - 1);
                        for (int y = y0; y <= y1; y++)
                        {
                            token.ThrowIfCancellationRequested();
                            int rowStart = x0, rowEnd = x1;
                            if (polygonCornerCoordinates is not null)
                            {
                                float rowY = (y + .5f) / height;
                                double left = double.PositiveInfinity, right = double.NegativeInfinity;
                                Intersect(pa, pb); Intersect(pb, pc); Intersect(pc, pa);
                                if (!double.IsFinite(left) || !double.IsFinite(right)) continue;
                                // One pixel of slack retains coverage at floating-point edge intersections.
                                rowStart = Math.Max(x0, (int)Math.Floor(left * width - .5) - 1);
                                rowEnd = Math.Min(x1, (int)Math.Ceiling(right * width - .5) + 1);
                                void Intersect(Vector2 start, Vector2 end)
                                {
                                    if (rowY < Math.Min(start.Y, end.Y) || rowY > Math.Max(start.Y, end.Y)) return;
                                    if (start.Y == end.Y)
                                    { left = Math.Min(left, Math.Min(start.X, end.X)); right = Math.Max(right, Math.Max(start.X, end.X)); return; }
                                    double at = start.X + ((double)rowY - start.Y) * ((double)end.X - start.X) / ((double)end.Y - start.Y);
                                    left = Math.Min(left, at); right = Math.Max(right, at);
                                }
                            }
                            for (int x = rowStart; x <= rowEnd; x++)
                            {
                                var p = new Vector2((x + .5f) / width + tileX, (y + .5f) / height + tileY);
                                float wa, wb, wc;
                                if (polygonCornerCoordinates is not null)
                                {
                                    double ea = Edge(pb, pc, p), eb = Edge(pc, pa, p), ec = Edge(pa, pb, p);
                                    if (!Owns(ea,pb,pc) || !Owns(eb,pc,pa) || !Owns(ec,pa,pb)) continue;
                                    wa = (float)(ea / atlasArea); wb = (float)(eb / atlasArea); wc = (float)(ec / atlasArea);
                                }
                                else
                                {
                                    wb = Cross(p - pa, pc - pa) / area; wc = Cross(pb - pa, p - pa) / area;
                                    wa = 1 - wb - wc;
                                    if (wa < -1e-6f || wb < -1e-6f || wc < -1e-6f) continue;
                                }
                                SetInputs(a, b, c, wa, wb, wc);
                                WritePixel(y * width + x);
                            }
                        }
                    }
                }
                if (!covered.Any(value => value)) throw new InvalidDataException("No UV samples were covered by the surface.");
                rasterTiming?.Dispose();
                using var gutterTiming = AssetLoadDiagnostics.Measure("BakeGutters", diagnosticKey);
                // A small gutter keeps bilinear sampling at island boundaries away from empty texels.
                Span<int> neighbors = stackalloc int[4];
                var previous = new bool[pixels];
                for (int pass = 0; pass < 4; pass++)
                {
                    covered.CopyTo(previous, 0);
                    for (int y = 0; y < height; y++)
                    {
                        token.ThrowIfCancellationRequested();
                        for (int x = 0; x < width; x++)
                        {
                            int destination = y * width + x;
                            if (previous[destination]) continue;
                            neighbors[0] = y * width + (polygonCornerCoordinates is null ? (x + width - 1) % width : Math.Max(0,x-1));
                            neighbors[1] = y * width + (polygonCornerCoordinates is null ? (x + 1) % width : Math.Min(width-1,x+1));
                            neighbors[2] = (polygonCornerCoordinates is null ? (y + height - 1) % height : Math.Max(0,y-1)) * width + x;
                            neighbors[3] = (polygonCornerCoordinates is null ? (y + 1) % height : Math.Min(height-1,y+1)) * width + x;
                            foreach (int neighbor in neighbors)
                                if (previous[neighbor])
                                {
                                    rgba.Span.Slice(neighbor * 4, 4).CopyTo(rgba.Span.Slice(destination * 4)); covered[destination] = true;
                                    if (collectCounters) gutterTexels++;
                                    break;
                                }
                        }
                    }
                }
            }
            rasterTiming?.Dispose();
            using var ddsTiming = AssetLoadDiagnostics.Measure("BakeDds", diagnosticKey);
            token.ThrowIfCancellationRequested();
            var document = new DdsTextureReader().TakeOwnership(bytes, material.Identity, material.Revision);
            var surface = new DdsTextureDecoder().ViewRgbaSubresource(document, document.DefaultSelection, token);
            string graphHash = GraphHash(graph);
            completed = true;
            return new(document, surface, graphHash, textureCoordinateSet) { PolygonCornerCoordinates = polygonCornerCoordinates };

            int Index(int offset) => primitive.Indices[offset] < primitive.Positions.Count
                ? checked((int)primitive.Indices[offset]) : throw new InvalidDataException("A triangle index exceeds the vertex array.");
            static double Edge(Vector2 a, Vector2 b, Vector2 point) =>
                ((double)b.X-a.X)*((double)point.Y-a.Y)-((double)b.Y-a.Y)*((double)point.X-a.X);
            static bool Owns(double value, Vector2 a, Vector2 b) => value > 0 || value == 0 &&
                (b.Y > a.Y || b.Y == a.Y && b.X < a.X);
            void Gather(MaterialExpression root)
            {
                var visited = new HashSet<MaterialExpression>(ReferenceEqualityComparer.Instance); var pending = new Stack<MaterialExpression>(); pending.Push(root);
                while (pending.TryPop(out var node))
                {
                    if (!visited.Add(node)) continue;
                    diagnosticNodes?.Add(node);
                    if (visited.Count > 4096) throw new InvalidDataException("The material graph exceeds the bake limit.");
                    if (node.Input is { } input) inputs.Add(input);
                    if (node.TextureSlot is { } slot) slots.Add(slot);
                    foreach (var argument in node.Arguments) pending.Push(argument);
                }
                if (collectCounters) rootNodeOccurrences += visited.Count;
            }
            void SetInputs(int a, int b, int c, float wa, float wb, float wc)
            {
                foreach (int set in requiredUvSets)
                {
                    var coordinate = sets[set][a] * wa + sets[set][b] * wb + sets[set][c] * wc;
                    uv[set] = new(coordinate, 0, 0);
                }
                if (needsColor) vertexColor = primitive.Colors![a] * wa + primitive.Colors[b] * wb + primitive.Colors[c] * wc;
                if (needsPosition) position = new(primitive.Positions[a] * wa + primitive.Positions[b] * wb + primitive.Positions[c] * wc, 0);
                if (needsNormal)
                {
                    var n = primitive.Normals![a] * wa + primitive.Normals[b] * wb + primitive.Normals[c] * wc;
                    normal = new(n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.Zero, 0);
                }
            }
            void EvaluatePixel(Span<byte> pixel)
            {
                if (collectCounters)
                {
                    if (rasterStarted) rasterEvaluations++;
                    else vertexEvaluations++;
                }
                var value = output.Evaluate(readInput, sample, token);
                pixel[0] = Encode(value.X); pixel[1] = Encode(value.Y); pixel[2] = Encode(value.Z); pixel[3] = Quantize(value.W);
            }
            void WritePixel(int index)
            {
                int memoOffset = memoValues is null ? 0 : index * memoRequiredInputs.Length;
                if (memoValues is not null && covered[index])
                {
                    bool same = true;
                    for (int input = 0; input < memoRequiredInputs.Length; input++)
                    {
                        var previous = memoValues[memoOffset + input];
                        var current = readInput(memoRequiredInputs[input]);
                        if (BitConverter.SingleToInt32Bits(previous.X) != BitConverter.SingleToInt32Bits(current.X) ||
                            BitConverter.SingleToInt32Bits(previous.Y) != BitConverter.SingleToInt32Bits(current.Y) ||
                            BitConverter.SingleToInt32Bits(previous.Z) != BitConverter.SingleToInt32Bits(current.Z) ||
                            BitConverter.SingleToInt32Bits(previous.W) != BitConverter.SingleToInt32Bits(current.W))
                        { same = false; break; }
                    }
                    if (same)
                    {
                        if (collectCounters) { exactInputReuses++; revisitedTexels++; }
                        return;
                    }
                }
                Span<byte> pixel = stackalloc byte[4]; EvaluatePixel(pixel);
                var destination = rgba.Span.Slice(index * 4, 4);
                if (covered[index])
                {
                    if (collectCounters) revisitedTexels++;
                    for (int channel = 0; channel < 4; channel++)
                        if (Math.Abs(destination[channel] - pixel[channel]) > 2)
                    {
                        if (collectCounters) AssetLoadDiagnostics.Counters("BakeOverlap", diagnosticKey, new Dictionary<string, long>
                        {
                            ["Triangle"] = activeTriangle, ["PixelX"] = index % width, ["PixelY"] = index / width,
                            ["Channel"] = channel, ["Previous"] = destination[channel], ["Current"] = pixel[channel]
                        });
                        throw new MaterialBakeAtlasRequiredException($"Overlapping UVs require different surface values at pixel {index % width},{index / width}, triangle {activeTriangle}; use separate UV islands for this export.");
                    }
                }
                else if (collectCounters) coveredTexels++;
                if (memoValues is not null)
                    for (int input = 0; input < memoRequiredInputs.Length; input++)
                        memoValues[memoOffset + input] = readInput(memoRequiredInputs[input]);
                pixel.CopyTo(destination); covered[index] = true;
            }
        }
        finally
        {
            if (collectCounters)
                AssetLoadDiagnostics.Counters("BakeRaster", diagnosticKey, new Dictionary<string, long>
                {
                    ["Width"] = width, ["Height"] = height, ["OutputPixels"] = (long)width * height,
                    ["Vertices"] = primitive.Positions.Count, ["Triangles"] = primitive.Indices.Count / 3,
                    ["UvSet"] = textureCoordinateSet, ["Atlas"] = polygonCornerCoordinates is null ? 0 : 1,
                    ["UvOnly"] = uvOnly ? 1 : 0, ["GraphNodes"] = diagnosticNodes?.Count ?? 0,
                    ["RootNodeOccurrences"] = rootNodeOccurrences,
                    ["VertexPixelEvaluations"] = vertexEvaluations, ["RasterPixelEvaluations"] = rasterEvaluations,
                    ["TextureSamples"] = textureSamples, ["CoveredTexels"] = coveredTexels,
                    ["RevisitedTexels"] = revisitedTexels, ["GutterFilledTexels"] = gutterTexels,
                    ["ExactInputReuses"] = exactInputReuses, ["MemoBytes"] = memoBytes,
                    ["Completed"] = completed ? 1 : 0
                });
        }
    }

    private static bool HasRepeatedSourceUvTriangle(MeshPrimitive primitive, IReadOnlyList<Vector2> coordinates,
        FbxExportMemoryReservation reserve, CancellationToken token)
    {
        IDisposable memory;
        try { memory = reserve(512L * 1024, "material raster memo eligibility"); }
        catch (ArchiveException error) when (error.Code == "AssetMemoryLimit") { return false; }
        using (memory)
        {
            var signatures = new HashSet<(int, int, int, int, int, int)>(MaximumMemoTriangleSignatures);
            for (int corner = 0; corner < primitive.Indices.Count; corner += 3)
            {
                if ((corner & 255) == 0) token.ThrowIfCancellationRequested();
                var a = primitive.Indices[corner]; var b = primitive.Indices[corner + 1]; var c = primitive.Indices[corner + 2];
                if (a >= coordinates.Count || b >= coordinates.Count || c >= coordinates.Count) return false;
                var first = coordinates[(int)a]; var second = coordinates[(int)b]; var third = coordinates[(int)c];
                if (!Finite(first) || !Finite(second) || !Finite(third)) return false;
                var signature = (BitConverter.SingleToInt32Bits(first.X), BitConverter.SingleToInt32Bits(first.Y),
                    BitConverter.SingleToInt32Bits(second.X), BitConverter.SingleToInt32Bits(second.Y),
                    BitConverter.SingleToInt32Bits(third.X), BitConverter.SingleToInt32Bits(third.Y));
                if (signatures.Contains(signature)) return true;
                // An unproven long prefix is ineligible rather than adding more discovery work.
                if (signatures.Count >= MaximumMemoTriangleSignatures) return false;
                signatures.Add(signature);
            }
            return false;
        }
    }

    /// <summary>Converts coverage alpha to linear RGB transparency (one minus opacity).</summary>
    public static MaterialSurfaceMap CreateTransparencyMap(MaterialSurfaceMap colorMap, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(colorMap);
        var diagnosticKey = $"{colorMap.Document.Identity?.Key}|transparency|{colorMap.Surface.Width}x{colorMap.Surface.Height}";
        using var ddsTiming = AssetLoadDiagnostics.Measure("BakeDds", diagnosticKey);
        var bytes = CreateDdsBuffer(colorMap.Surface.Width, colorMap.Surface.Height, srgb: false);
        var rgba = bytes.AsSpan(148);
        colorMap.Surface.Rgba8.Span.CopyTo(rgba);
        for (int offset = 0; offset < rgba.Length; offset += 4)
        {
            if ((offset & 16383) == 0) token.ThrowIfCancellationRequested();
            byte transparency = (byte)(255 - rgba[offset + 3]);
            rgba[offset] = rgba[offset + 1] = rgba[offset + 2] = transparency; rgba[offset + 3] = 255;
        }
        var document = new DdsTextureReader().TakeOwnership(bytes, colorMap.Document.Identity, colorMap.Document.Revision);
        if (AssetLoadDiagnostics.CountersEnabled)
            AssetLoadDiagnostics.Counters("BakeDds", diagnosticKey, new Dictionary<string, long>
            {
                ["Width"] = colorMap.Surface.Width, ["Height"] = colorMap.Surface.Height,
                ["Pixels"] = (long)colorMap.Surface.Width * colorMap.Surface.Height, ["Transparency"] = 1
            });
        return new(document, new DdsTextureDecoder().ViewRgbaSubresource(document, document.DefaultSelection, token), colorMap.GraphSha256, colorMap.TextureCoordinateSet)
            { PolygonCornerCoordinates = colorMap.PolygonCornerCoordinates };
    }

    public static Vector4 Sample(MaterialBakeTexture texture, Vector2 uv)
    {
        if (!Finite(uv)) throw new InvalidDataException("The texture coordinate is not finite.");
        var surface = texture.Surface; var profile = texture.Sampling;
        if (surface.IsHdr || !profile.NormalizedCoordinates || profile.ComparisonEnabled)
            throw new InvalidDataException("This sampler cannot produce an SDR colour sample.");
        if (profile.TexelFilter == NativeTextureTexelFilter.Point)
            return Read((int)MathF.Floor(uv.X * surface.Width), (int)MathF.Floor(uv.Y * surface.Height));
        float px = uv.X * surface.Width - .5f, py = uv.Y * surface.Height - .5f;
        int x = (int)MathF.Floor(px), y = (int)MathF.Floor(py);
        return Vector4.Lerp(Vector4.Lerp(Read(x, y), Read(x + 1, y), px - x),
            Vector4.Lerp(Read(x, y + 1), Read(x + 1, y + 1), px - x), py - y);
        Vector4 Read(int x, int y)
        {
            if (!Address(ref x, surface.Width, profile.AddressU) || !Address(ref y, surface.Height, profile.AddressV))
                return profile.BorderColor switch { NativeTextureBorderColor.OpaqueWhite => Vector4.One, NativeTextureBorderColor.OpaqueBlack => new(0, 0, 0, 1), _ => Vector4.Zero };
            var data = surface.Rgba8.Span; int offset = (y * surface.Width + x) * 4;
            return new(Decode(data[offset], surface.IsSrgb), Decode(data[offset + 1], surface.IsSrgb), Decode(data[offset + 2], surface.IsSrgb), data[offset + 3] / 255f);
        }
    }
    private static bool Address(ref int value, int extent, NativeTextureAddressMode mode)
    {
        switch (mode)
        {
            case NativeTextureAddressMode.Repeat: value = (value % extent + extent) % extent; return true;
            case NativeTextureAddressMode.MirroredRepeat:
                value = (value % (extent * 2) + extent * 2) % (extent * 2); if (value >= extent) value = extent * 2 - value - 1; return true;
            case NativeTextureAddressMode.ClampToEdge: value = Math.Clamp(value, 0, extent - 1); return true;
            case NativeTextureAddressMode.ClampToBorder: return value >= 0 && value < extent;
            default: throw new InvalidDataException("Unknown texture address mode.");
        }
    }
    /// <summary>Fingerprints the source graph after validating each root's bounded expression structure. Bake validates output suitability.</summary>
    public static string ComputeGraphHash(MaterialSurfaceGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        try
        {
            foreach (var root in new[] { graph.BaseColor, graph.Opacity, graph.AlphaCutoff }.OfType<MaterialExpression>())
                _ = new MaterialSurfaceExpressionEvaluator(root);
        }
        catch (ArgumentException error) when (error.ParamName == "root")
        { throw new InvalidDataException("The material expression graph is invalid: " + error.Message, error); }
        return GraphHash(graph);
    }
    private static string GraphHash(MaterialSurfaceGraph graph)
    {
        var nodes = new List<object>(); var indices = new Dictionary<MaterialExpression, int>(ReferenceEqualityComparer.Instance);
        int? Visit(MaterialExpression? node)
        {
            if (node is null) return null;
            if (indices.TryGetValue(node, out int existing)) return existing;
            foreach (var item in MaterialExpressionGraph.GetPostOrder(node))
            {
                if (indices.ContainsKey(item)) continue;
                var arguments = item.Arguments.Select(argument => indices[argument]).ToArray();
                int index = nodes.Count; indices.Add(item, index);
                nodes.Add(new { item.Operation, item.ValueType, Arguments = arguments, item.Constants, item.Input, item.TextureSlot, item.Components });
            }
            return indices[node];
        }
        int? color = Visit(graph.BaseColor), opacity = Visit(graph.Opacity), cutoff = Visit(graph.AlphaCutoff);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Color = color, Opacity = opacity, Cutoff = cutoff, Nodes = nodes }))).ToLowerInvariant();
    }
    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    private static byte Quantize(float value) => checked((byte)MathF.Round(Math.Clamp(value, 0, 1) * 255));
    private static byte Encode(float value) => Quantize(value <= .0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1 / 2.4f) - .055f);
    private static float Decode(byte value, bool srgb) => srgb ? SrgbByteValues[value] : value / 255f;
    private static float[] CreateSrgbByteValues()
    {
        var values = new float[256];
        for (int index = 0; index < values.Length; index++)
        {
            float value = index / 255f;
            values[index] = value <= .04045f ? value / 12.92f : MathF.Pow((value + .055f) / 1.055f, 2.4f);
        }
        return values;
    }
    private static byte[] CreateDdsBuffer(int width, int height, bool srgb = true)
    {
        var bytes = new byte[checked(148 + width * height * 4)]; "DDS "u8.CopyTo(bytes);
        Write(4, 124); Write(8, 0x100f); Write(12, (uint)height); Write(16, (uint)width); Write(20, (uint)width * 4);
        Write(76, 32); Write(80, 4); "DX10"u8.CopyTo(bytes.AsSpan(84)); Write(108, 0x1000);
        Write(128, srgb ? 29u : 28u); Write(132, 3); Write(140, 1); return bytes;
        void Write(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    }
}
