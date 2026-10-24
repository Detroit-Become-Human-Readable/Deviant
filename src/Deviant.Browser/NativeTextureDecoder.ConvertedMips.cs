using System.Buffers.Binary;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

public sealed partial class NativeTextureDecoder
{
    private NativeTextureDecodedDocument ReadConvertedChain(NativeTextureSource source, CancellationToken token)
    {
        _ = SelectConvertedMip(source, int.MaxValue);
        var metadata = source.Metadata;
        if (metadata.MipSizeCount == 1) return ReadConverted(source, int.MaxValue, token);
        if (ConvertedChainOutputBytes(source) > maximumDecodedBytes) throw Error("NativeTextureDecodeBudget", "Mip data exceeds the decode budget.");
        var documents = new SortedDictionary<int, (DdsTextureDocument Document, NativeTextureMipSource Evidence)>();
        ConvertedTextureInfo? conversion = null;
        for (int descriptor = 0; descriptor < metadata.DescriptorCount - 1; descriptor++)
        {
            token.ThrowIfCancellationRequested();
            var lod = metadata.LodDescriptors[descriptor];
            var decoded = ReadConverted(source, Math.Max(lod.Width, lod.Height), token);
            var evidence = decoded.SupportedMips.Single();
            if (evidence.MipIndex != FindMipIndex(metadata, lod) || !documents.TryAdd(evidence.MipIndex, (decoded.Document, evidence)))
                throw Error("AmbiguousNativeMip", "Multiple resources describe the same mip.");
            conversion ??= decoded.Conversion;
        }
        var tail = metadata.LodDescriptors[^1];
        int firstTail = FindMipIndex(metadata, tail);
        long normalizedBytes = 0;
        for (int mip = firstTail; mip < metadata.MipSizeCount; mip++)
            normalizedBytes = checked(normalizedBytes + MipByteLength(metadata.FirstByte,
                Math.Max(1, metadata.Mip0Width >> mip), Math.Max(1, metadata.Mip0Height >> mip)));
        var reference = metadata.References[0];
        if (firstTail < 0 || reference.FollowingUInt32 != ((normalizedBytes + 255) & ~255L))
            throw Error("NativeMipSizeMismatch", "Mip byte size does not match its dimensions.");
        var payload = GetPayload(source, reference, 2229);
        if (!TextureBufferPatternInspector.TryGetPayload(payload.OriginalBytes.Span, payload.Extent, payload.Type,
            out _, out var body, out var payloadExtent, out _))
            throw Error("UnsupportedNativeMipPayload", "The mip payload uses an unavailable encoding.");
        int cursor = 0;
        for (int mip = firstTail; mip < metadata.MipSizeCount; mip++)
        {
            token.ThrowIfCancellationRequested();
            int width = Math.Max(1, metadata.Mip0Width >> mip), height = Math.Max(1, metadata.Mip0Height >> mip);
            var remaining = body[cursor..];
            if (remaining.Length < 80 || BinaryPrimitives.ReadUInt16LittleEndian(remaining) != 16 ||
                BinaryPrimitives.ReadUInt16LittleEndian(remaining[6..]) != width || BinaryPrimitives.ReadUInt16LittleEndian(remaining[8..]) != height)
                throw Error("InvalidConvertedTextureRange", "The mip packet dimensions or extent are invalid.");
            ushort selector = BinaryPrimitives.ReadUInt16LittleEndian(remaining[2..]);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(remaining[40..]);
            DdsTextureDocument document;
            int consumed;
            if (selector == 255 || selector == 12 && metadata.PostSelectorByte1 == 12)
            {
                var packet = NativeTextureBlockPacketReader.ReadPacket(remaining, width, height,
                    MipByteLength(metadata.FirstByte, width, height), maximumDecodedBytes, token);
                consumed = packet.ConsumedBodyBytes;
                document = ConvertedMipDocument(source, width, height, packet.Blocks.Span, nativeBlocks: true, token);
            }
            else if (selector == metadata.PostSelectorByte1 && count == 1 && BinaryPrimitives.ReadUInt32LittleEndian(remaining[52..]) == 0)
            {
                int bytes = checked((int)MipByteLength(metadata.FirstByte, width, height));
                if (BinaryPrimitives.ReadUInt16LittleEndian(remaining[4..]) > 100 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(remaining[42..]) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(remaining[44..]) != 0 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(remaining[56..]) != bytes ||
                    BinaryPrimitives.ReadUInt32LittleEndian(remaining[60..]) != bytes || BinaryPrimitives.ReadUInt32LittleEndian(remaining[64..]) != 0 ||
                    bytes > remaining.Length - 80)
                    throw Error("InvalidConvertedTextureRange", "The literal mip packet extent is invalid.");
                consumed = checked(80 + bytes);
                document = ConvertedMipDocument(source, width, height, remaining.Slice(80, bytes), nativeBlocks: true, token);
            }
            else
            {
                var decoded = new ConvertedTextureDecoder().Read(remaining, metadata.PostSelectorByte1, width, height, maximumDecodedBytes, token);
                conversion ??= decoded.Info;
                consumed = decoded.Info.ConsumedBodyBytes;
                var pixels = decoded.Rgba8.ToArray();
                if (metadata.FirstByte == 5 && decoded.Info.Selector is 17 or 19)
                    for (int pixel = 3; pixel < pixels.Length; pixel += 4) pixels[pixel] = 255;
                document = ConvertedMipDocument(source, width, height, pixels, nativeBlocks: false, token);
            }
            if (consumed < 1 || consumed > remaining.Length || !documents.TryAdd(mip, (document,
                new(mip, width, height, payload.Identity, payload.Sha256, Hash(document.OriginalBytes.Span.Slice(document.Info.HeaderLength, checked((int)document.Info.EncodedLength))), payloadExtent!,
                    new(payload.Identity, cursor, consumed)))))
                throw Error("AmbiguousNativeMip", "Multiple resources describe the same mip.");
            cursor = checked(cursor + consumed);
        }
        if (cursor != body.Length || documents.Count != metadata.MipSizeCount ||
            !documents.Keys.SequenceEqual(Enumerable.Range(0, metadata.MipSizeCount)))
            throw Error("InvalidConvertedTextureRange", "The converted mip chain does not match its source extent.");
        var first = documents[0].Document;
        bool sameEncoding = documents.Values.All(value => value.Document.Info.Format == first.Info.Format &&
            value.Document.Info.IsSrgb == first.Info.IsSrgb);
        var data = new List<byte[]>(documents.Count);
        foreach (var value in documents.Values)
        {
            token.ThrowIfCancellationRequested();
            if (sameEncoding) data.Add(value.Document.OriginalBytes.Slice(value.Document.Info.HeaderLength,
                checked((int)value.Document.Info.EncodedLength)).ToArray());
            else data.Add(new DdsTextureDecoder(maximumDecodedBytes).DecodeSubresourceAsync(value.Document,
                value.Document.DefaultSelection, token).GetAwaiter().GetResult().Rgba8.ToArray());
        }
        var dds = new byte[checked(148 + data.Sum(bytes => bytes.Length))];
        first.OriginalBytes.Span[..148].CopyTo(dds);
        bool compressed = sameEncoding && first.Info.Format is TextureStorageFormat.Bc1 or TextureStorageFormat.Bc3 or
            TextureStorageFormat.Bc4 or TextureStorageFormat.Bc5 or TextureStorageFormat.Bc6Unsigned;
        Put(8, (documents.Count > 1 ? 0x21007u : 0x1007u) | (compressed ? 0x80000u : 8u));
        Put(20, compressed ? (uint)data[0].Length : (uint)metadata.Mip0Width * (sameEncoding && first.Info.Format == TextureStorageFormat.Luminance8 ? 1u : 4u));
        Put(28, (uint)documents.Count); Put(108, documents.Count > 1 ? 0x401008u : 0x1000u);
        if (!sameEncoding) Put(128, IsSrgb(source) ? 29u : 28u);
        cursor = 148;
        foreach (var bytes in data) { token.ThrowIfCancellationRequested(); bytes.CopyTo(dds, cursor); cursor += bytes.Length; }
        return new(ReadNativeDocument(dds, source, token), documents.Values.Select(value => value.Evidence).ToArray(),
            metadata.MipSizeCount, []) { Conversion = conversion };
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(offset), value);
    }

    private static long ConvertedChainOutputBytes(NativeTextureSource source)
    {
        bool nativeBlocks = !IsScalarSrgb(source);
        foreach (var payload in source.Payloads)
        {
            if (!TextureBufferPatternInspector.TryGetPayload(payload.OriginalBytes.Span, payload.Extent, payload.Type,
                out _, out var body, out _, out _)) { nativeBlocks = false; break; }
            int cursor = 0;
            while (cursor < body.Length && nativeBlocks)
            {
                var packet = body[cursor..];
                if (packet.Length < 80 || BinaryPrimitives.ReadUInt16LittleEndian(packet) != 16)
                { nativeBlocks = false; break; }
                int selector = BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]);
                int count = BinaryPrimitives.ReadUInt16LittleEndian(packet[40..]);
                if (count is < 1 or > 4096 || 52 + count * 16 > packet.Length)
                { nativeBlocks = false; break; }
                bool literal = selector == 12 && source.Metadata.PostSelectorByte1 == 12 ||
                    selector == source.Metadata.PostSelectorByte1 && count == 1 && BinaryPrimitives.ReadUInt32LittleEndian(packet[52..]) == 0;
                if (!literal && selector != 255) { nativeBlocks = false; break; }
                long end = literal ? (52 + count * 16 + 15) & ~15 : 0;
                for (int row = 0; row < count; row++)
                {
                    var descriptor = packet.Slice(52 + row * 16, 16);
                    long encoded = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]);
                    end = literal ? checked(end + encoded) : Math.Max(end,
                        checked((long)BinaryPrimitives.ReadUInt32LittleEndian(descriptor[12..]) + encoded));
                }
                end = checked(end + BinaryPrimitives.ReadUInt32LittleEndian(packet[44..]));
                if (end < 1 || end > packet.Length) { nativeBlocks = false; break; }
                cursor = checked(cursor + (int)end);
            }
            if (!nativeBlocks) break;
        }
        long bytes = 0;
        for (int mip = 0; mip < source.Metadata.MipSizeCount; mip++)
        {
            int width = Math.Max(1, source.Metadata.Mip0Width >> mip), height = Math.Max(1, source.Metadata.Mip0Height >> mip);
            bytes = checked(bytes + (nativeBlocks ? MipByteLength(source.Metadata.FirstByte, width, height) : (long)width * height * 4));
        }
        return bytes;
    }
    private DdsTextureDocument ConvertedMipDocument(NativeTextureSource source, int width, int height,
        ReadOnlySpan<byte> bytes, bool nativeBlocks, CancellationToken token)
    {
        var dds = new byte[checked(148 + bytes.Length)]; "DDS "u8.CopyTo(dds);
        bool compressed = nativeBlocks && source.Metadata.FirstByte is 5 or 7 or 18 or 19 or 25;
        Put(4, 124); Put(8, 0x1007u | (compressed ? 0x80000u : 8u)); Put(12, (uint)height); Put(16, (uint)width);
        Put(20, compressed ? (uint)bytes.Length : (uint)width * (nativeBlocks && source.Metadata.FirstByte == 11 ? 1u : 4u));
        Put(28, 1); Put(76, 32); Put(80, 4); "DX10"u8.CopyTo(dds.AsSpan(84)); Put(108, 0x1000);
        Put(128, nativeBlocks ? DxgiFormat(source.Metadata.FirstByte, IsSrgb(source)) : IsSrgb(source) ? 29u : 28u);
        Put(132, 3); Put(140, 1); if (compressed && source.Metadata.FirstByte != 7) Put(144, 3);
        bytes.CopyTo(dds.AsSpan(148));
        return ReadNativeDocument(dds, source, token);
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(offset), value);
    }
}
