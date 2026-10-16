using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Deviant.Formats.Textures;

public sealed record ConvertedTextureChunk(int Index, int TileCount, int BodyOffset, int EncodedLength,
    int NormalizedLength, int BitsConsumed, string EncodedSha256);
public sealed record ConvertedTextureInfo(int Width, int Height, ushort Selector, ushort Quality,
    IReadOnlyList<float> Scale, IReadOnlyList<float> Bias, int ConsumedBodyBytes, int TrailingBodyBytes,
    IReadOnlyList<ConvertedTextureChunk> Chunks);
public sealed record ConvertedTextureDocument(ReadOnlyMemory<byte> Rgba8, ConvertedTextureInfo Info);

/// <summary>Decodes supported version-16 coefficient packets into texture samples.</summary>
public sealed class ConvertedTextureDecoder
{
    public const string Version = "converted-texture-v16-5";
    private const int HeaderLength = 52;
    private const int DescriptorLength = 16;
    private const int MaximumChunkCount = 4096;
    private static readonly HuffmanTable DcLuma = new(ConvertedTextureMath.DcLumaCounts, ConvertedTextureMath.DcLumaSymbols);
    private static readonly HuffmanTable AcLuma = new(ConvertedTextureMath.AcLumaCounts, ConvertedTextureMath.AcLumaSymbols);
    private static readonly HuffmanTable DcChroma = new(ConvertedTextureMath.DcChromaCounts, ConvertedTextureMath.DcChromaSymbols);
    private static readonly HuffmanTable AcChroma = new(ConvertedTextureMath.AcChromaCounts, ConvertedTextureMath.AcChromaSymbols);

    public static long EstimateDocumentWorkingSetBytes(int width, int height, int chunkCount) =>
        checked(3L * width * height * 4 + chunkCount * 256L + 16 * 1024);

    public ConvertedTextureDocument Read(ReadOnlySpan<byte> body, ushort selector, int width, int height,
        long maximumDecodedBytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var header = ReadHeader(body, selector, width, height);
        var pixelBytes = checked((long)width * height * 4);
        if (pixelBytes > maximumDecodedBytes || pixelBytes > int.MaxValue)
            throw Error("ConvertedTextureBudget", "Texture data exceeds the decode budget.");
        var tileEdge = selector is 15 or 17 or 19 or 21 or 23 ? 8 : 16;
        var normalizedTileBytes = selector is 1 or 15 ? 256 : selector == 7 ? 128 : selector == 23 ? 64 : 32;
        var expectedTiles = checked((long)width * height / (tileEdge * tileEdge));
        long totalTiles = 0, totalNormalized = 0;
        var end = header.HeaderBytes;
        // Validate every source and destination extent before allocating pixels or decoding coefficients.
        for (var index = 0; index < header.ChunkCount; index++)
        {
            token.ThrowIfCancellationRequested();
            var descriptor = Descriptor(body, index);
            if (descriptor.Tiles == 0)
                throw Error("UnsupportedConvertedTextureChunk", "This texture chunk encoding is unavailable.");
            if (descriptor.Offset % 16 != 0 || descriptor.Offset < end || descriptor.EncodedLength < 1 ||
                descriptor.Offset > body.Length || descriptor.EncodedLength > body.Length - descriptor.Offset ||
                descriptor.NormalizedLength != checked((long)descriptor.Tiles * normalizedTileBytes))
                throw Error("InvalidConvertedTextureRange", "Texture chunk extents are invalid.");
            end = checked(descriptor.Offset + descriptor.EncodedLength);
            totalTiles = checked(totalTiles + descriptor.Tiles);
            totalNormalized = checked(totalNormalized + descriptor.NormalizedLength);
        }
        if (totalTiles != expectedTiles || totalNormalized != expectedTiles * normalizedTileBytes ||
            header.FooterBytes > body.Length - end)
            throw Error("InvalidConvertedTextureSize", "Texture chunk sizes do not match the dimensions.");

        var pixels = new byte[checked((int)pixelBytes)];
        var chunks = new List<ConvertedTextureChunk>(header.ChunkCount);
        Span<short> lumaQuant = stackalloc short[64]; Span<short> chromaQuant = stackalloc short[64];
        Span<short> coefficients = stackalloc short[64]; Span<short> planes = stackalloc short[640];
        Span<byte> tile = stackalloc byte[1024]; Span<byte> scalarSamples = stackalloc byte[64]; Span<int> predictors = stackalloc int[4];
        ConvertedTextureMath.Quantization(header.Quality, lumaQuant, chromaQuant);
        var planeCount = selector is 17 or 23 ? 2 : selector == 19 ? 3 : selector is 15 or 21 ? 1 : selector == 1 ? 10 : 6;
        var tileIndex = 0;
        for (var chunk = 0; chunk < header.ChunkCount; chunk++)
        {
            token.ThrowIfCancellationRequested();
            var descriptor = Descriptor(body, chunk);
            var encoded = body.Slice(descriptor.Offset, descriptor.EncodedLength);
            var bits = new BitReader(encoded); predictors.Clear();
            for (var block = 0; block < descriptor.Tiles; block++)
            {
                if ((block & 15) == 0) token.ThrowIfCancellationRequested();
                for (var plane = 0; plane < planeCount; plane++)
                {
                    var component = selector is 17 or 19 or 23 ? plane : plane < 4 ? 0 : plane == 4 ? 1 : plane == 5 ? 2 : 3;
                    var chroma = selector is not (17 or 19 or 23) && (component is 1 or 2);
                    ReadCoefficients(ref bits, chroma ? DcChroma : DcLuma, chroma ? AcChroma : AcLuma,
                        ref predictors[component], coefficients);
                    if (selector == 15) ConvertedTextureMath.InverseDctScalar(coefficients, lumaQuant, scalarSamples);
                    else ConvertedTextureMath.InverseDct(coefficients, chroma ? chromaQuant : lumaQuant,
                        planes.Slice(plane * 64, 64));
                }
                if (selector is 17 or 19 or 23)
                {
                    for (var pixel = 0; pixel < 64; pixel++)
                    {
                        tile[pixel * 4] = ConvertedTextureMath.Clamp(planes[pixel]);
                        tile[pixel * 4 + 1] = ConvertedTextureMath.Clamp(planes[64 + pixel]);
                        tile[pixel * 4 + 2] = selector == 19 ? ConvertedTextureMath.Clamp(planes[128 + pixel]) : (byte)0;
                        tile[pixel * 4 + 3] = selector == 23 ? (byte)255 : (byte)0;
                    }
                }
                else if (selector is 15 or 21)
                {
                    for (var pixel = 0; pixel < 64; pixel++)
                    {
                        tile[pixel * 4] = selector == 15 ? scalarSamples[pixel] : ConvertedTextureMath.Clamp(planes[pixel]);
                        tile[pixel * 4 + 1] = 0; tile[pixel * 4 + 2] = 0; tile[pixel * 4 + 3] = 255;
                    }
                }
                else ConvertedTextureMath.Color(planes, selector == 1, tile);
                var x = tileIndex % (width / tileEdge) * tileEdge;
                var y = tileIndex / (width / tileEdge) * tileEdge;
                for (var row = 0; row < tileEdge; row++)
                    tile.Slice(row * tileEdge * 4, tileEdge * 4).CopyTo(pixels.AsSpan(((y + row) * width + x) * 4));
                tileIndex++;
            }
            if ((bits.Position + 7) / 8 != encoded.Length)
                throw Error("ConvertedTextureEntropyExtent", "Texture coefficients do not consume their encoded extent.");
            chunks.Add(new(chunk, descriptor.Tiles, descriptor.Offset, descriptor.EncodedLength,
                descriptor.NormalizedLength, bits.Position, Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant()));
        }
        token.ThrowIfCancellationRequested();
        var consumed = checked(end + header.FooterBytes);
        return new(pixels, new(width, height, selector, header.Quality, header.Scale, header.Bias,
            consumed, body.Length - consumed, chunks.AsReadOnly()));
    }

    public static int ReadChunkCount(ReadOnlySpan<byte> body, ushort selector, int width, int height) =>
        ReadHeader(body, selector, width, height).ChunkCount;

    private static Header ReadHeader(ReadOnlySpan<byte> body, ushort selector, int width, int height)
    {
        var tileEdge = selector is 15 or 17 or 19 or 21 or 23 ? 8 : 16;
        if (selector is not (1 or 7 or 15 or 17 or 19 or 21 or 23) || body.Length < HeaderLength ||
            body.Length > TextureBufferPatternInspector.MaximumStoredBytes ||
            BinaryPrimitives.ReadUInt16LittleEndian(body) != 16 ||
            BinaryPrimitives.ReadUInt16LittleEndian(body[2..]) != selector ||
            BinaryPrimitives.ReadUInt16LittleEndian(body[4..]) > 100 ||
            BinaryPrimitives.ReadUInt16LittleEndian(body[6..]) != width ||
            BinaryPrimitives.ReadUInt16LittleEndian(body[8..]) != height ||
            width < tileEdge || height < tileEdge || width % tileEdge != 0 || height % tileEdge != 0 ||
            width > DdsTextureReader.MaximumDimension || height > DdsTextureReader.MaximumDimension ||
            (long)width * height > DdsTextureReader.MaximumPixelCount ||
            BinaryPrimitives.ReadUInt16LittleEndian(body[42..]) != 0)
            throw Error("UnsupportedConvertedTextureHeader", "This texture packet layout is unavailable.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(body[40..]);
        var headerBytes = checked((HeaderLength + count * DescriptorLength + 15) & ~15);
        var footerBytes = BinaryPrimitives.ReadUInt32LittleEndian(body[44..]);
        if (count == 0 || count > MaximumChunkCount || headerBytes > body.Length || footerBytes > 15)
            throw Error("InvalidConvertedTextureHeader", "Texture packet bounds are invalid.");
        var scale = new float[3]; var bias = new float[3];
        for (var index = 0; index < 3; index++)
        {
            scale[index] = BinaryPrimitives.ReadSingleLittleEndian(body[(12 + index * 4)..]);
            bias[index] = BinaryPrimitives.ReadSingleLittleEndian(body[(24 + index * 4)..]);
            if (!float.IsFinite(scale[index]) || !float.IsFinite(bias[index]))
                throw Error("InvalidConvertedTextureHeader", "Texture packet values are invalid.");
        }
        return new(count, headerBytes, (int)footerBytes, BinaryPrimitives.ReadUInt16LittleEndian(body[4..]),
            Array.AsReadOnly(scale), Array.AsReadOnly(bias));
    }

    private static ChunkDescriptor Descriptor(ReadOnlySpan<byte> body, int index)
    {
        var bytes = body.Slice(HeaderLength + index * DescriptorLength, DescriptorLength);
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) > int.MaxValue ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) > int.MaxValue ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) > int.MaxValue)
            throw Error("InvalidConvertedTextureRange", "Texture chunk extents are invalid.");
        return new(BinaryPrimitives.ReadUInt16LittleEndian(bytes), (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
    }

    private static void ReadCoefficients(scoped ref BitReader bits, HuffmanTable dc, HuffmanTable ac,
        scoped ref int predictor, scoped Span<short> result)
    {
        result.Clear(); var size = dc.Read(ref bits);
        if (size > 15) throw Error("InvalidConvertedTextureCoefficients", "Texture coefficient sizes are invalid.");
        predictor = checked(predictor + Signed(ref bits, size));
        if (predictor is < short.MinValue or > short.MaxValue)
            throw Error("InvalidConvertedTextureCoefficients", "Texture coefficient values are invalid.");
        result[0] = (short)predictor;
        for (var index = 1; index < 64;)
        {
            var symbol = ac.Read(ref bits); var run = symbol >> 4; size = symbol & 15;
            if (size == 0)
            {
                if (run == 0) return;
                if (run != 15 || index > 48)
                    throw Error("InvalidConvertedTextureCoefficients", "Texture coefficient runs are invalid.");
                index += 16; continue;
            }
            index += run;
            if (index >= 64) throw Error("InvalidConvertedTextureCoefficients", "Texture coefficient runs are invalid.");
            result[ConvertedTextureMath.Zigzag[index++]] = (short)Signed(ref bits, size);
        }
    }
    private static int Signed(ref BitReader bits, int size)
    {
        if (size == 0) return 0;
        var value = bits.Read(size);
        return value < 1 << (size - 1) ? value + 1 - (1 << size) : value;
    }
    private readonly record struct Header(int ChunkCount, int HeaderBytes, int FooterBytes, ushort Quality,
        IReadOnlyList<float> Scale, IReadOnlyList<float> Bias);
    private readonly record struct ChunkDescriptor(int Tiles, int EncodedLength, int NormalizedLength, int Offset);
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> data;
        public BitReader(ReadOnlySpan<byte> data) { this.data = data; Position = 0; }
        public int Position { get; private set; }
        public int Read(int count)
        {
            if (count < 0 || count > 16 || count > data.Length * 8 - Position)
                throw Error("TruncatedConvertedTexture", "The texture coefficient stream is truncated.");
            var value = 0;
            for (var bit = 0; bit < count; bit++, Position++)
                value = (value << 1) | ((data[Position >> 3] >> (7 - (Position & 7))) & 1);
            return value;
        }
    }
    private sealed class HuffmanTable
    {
        private readonly int[] counts = new int[17], first = new int[17], offsets = new int[17];
        private readonly byte[] symbols;
        public HuffmanTable(byte[] lengths, byte[] symbols)
        {
            this.symbols = symbols; var code = 0; var total = 0;
            for (var length = 1; length <= 16; length++)
            {
                counts[length] = lengths[length]; first[length] = code; offsets[length] = total;
                total += lengths[length]; code = (code + lengths[length]) << 1;
            }
            if (total != symbols.Length) throw new InvalidOperationException("Invalid Huffman table.");
        }
        public int Read(ref BitReader bits)
        {
            var code = 0;
            for (var length = 1; length <= 16; length++)
            {
                code = (code << 1) | bits.Read(1); var at = code - first[length];
                if (at >= 0 && at < counts[length]) return symbols[offsets[length] + at];
            }
            throw Error("InvalidConvertedTextureHuffmanCode", "The texture coefficient stream is invalid.");
        }
    }
    private static TextureFormatException Error(string code, string message) => new(code, message);
}
