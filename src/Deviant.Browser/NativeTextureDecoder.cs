using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

public sealed record NativeTextureMipSource(int MipIndex, int Width, int Height,
    ResourceIdentity Identity, string OriginalSha256, string DecodedSha256, SourceExtent PayloadExtent,
    DecodedExtent? MipExtent = null)
{ public CubeFace? Face { get; init; } }
public sealed record NativeTextureDecodedDocument(DdsTextureDocument Document,
    IReadOnlyList<NativeTextureMipSource> SupportedMips, int TotalMipCount, IReadOnlyList<int> OmittedMipIndices)
{ public ConvertedTextureInfo? Conversion { get; init; } }

/// <summary>Builds texture documents from supported SEGS mip layouts.</summary>
public sealed partial class NativeTextureDecoder
{
    public const string Version = "native-texture-v10";
    public const long DefaultMaximumDecodedBytes = 32L * 1024 * 1024;
    private readonly long maximumDecodedBytes;
    public NativeTextureDecoder(long maximumDecodedBytes = DefaultMaximumDecodedBytes)
    {
        if (maximumDecodedBytes is < 1 or > 128L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumDecodedBytes));
        this.maximumDecodedBytes = maximumDecodedBytes;
    }
    public DdsTextureDocument Read(NativeTextureSource source, CancellationToken token = default)
        => ReadDetailed(source, token).Document;

    public static bool IsSrgb(NativeTextureSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Metadata.FirstByte is not (18 or 19 or 25) && source.Metadata.RawFixedPrefix84.Length > 14 &&
            source.Metadata.RawFixedPrefix84[14] == 1;
    }

    public long EstimateWorkingSetBytes(NativeTextureSource source, int maximumPreviewDimension = int.MaxValue)
    {
        if (maximumPreviewDimension < 1) throw new ArgumentOutOfRangeException(nameof(maximumPreviewDimension));
        var metadata = ValidateMetadata(source, default);
        long decodedBytes;
        if (metadata.PostSelectorBoolean)
        {
            var selected = SelectConvertedMip(source, maximumPreviewDimension);
            decodedBytes = checked((long)selected.Descriptor.Width * selected.Descriptor.Height * 4);
        }
        else decodedBytes = metadata.MipByteLengthGroups.Sum(group => group.Sum(length => (long)length));
        if (decodedBytes > maximumDecodedBytes)
            throw Error("NativeTextureDecodeBudget", "Mip data exceeds the decode budget.");
        return checked(3 * decodedBytes + 1024 * 1024);
    }

    public NativeTextureDecodedDocument ReadDetailed(NativeTextureSource source, CancellationToken token = default,
        int maximumPreviewDimension = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maximumPreviewDimension < 1) throw new ArgumentOutOfRangeException(nameof(maximumPreviewDimension));
        if (source.PreviewDimension is { } previewDimension)
            return ReadPreview(source, previewDimension, token);
        var metadata = ValidateMetadata(source, token);
        if (metadata.PostSelectorBoolean) return ReadConverted(source, maximumPreviewDimension, token);
        if (metadata.SizeGroupSelector == 2) return ReadCubemap(source, metadata, token);

        var mips = new SortedDictionary<int, (byte[] Bytes, NativeTextureMipSource Evidence)>();
        long decodedBytes = 0;
        if (metadata.FirstByte is 5 or 7 or 11 or 18 or 19 or 25)
            for (var index = 0; index < metadata.MipSizeCount; index++)
                if (metadata.MipByteLengthGroups[0][index] != MipByteLength(metadata.FirstByte,
                    Math.Max(1, metadata.Mip0Width >> index), Math.Max(1, metadata.Mip0Height >> index)))
                    throw Error("NativeMipSizeMismatch", "Mip byte size does not match its dimensions.");
        {
            var descriptor = metadata.LodDescriptors[^1];
            var firstPackedMip = FindMipIndex(metadata, descriptor);
            if (firstPackedMip < 0 || descriptor.ThirdU16 > 1 || descriptor.TrailingByte != metadata.MipSizeCount - firstPackedMip)
                throw Error("NativeMipDescriptorMismatch", "Mip dimensions do not match the texture metadata.");
            var nominalBytes = metadata.MipByteLengthGroups[0].Skip(firstPackedMip).Sum(length => (long)length);
            var packedBytes = checked((nominalBytes + 255) & ~255L);
            if (packedBytes > TextureBufferPatternInspector.MaximumDecodedBytes || packedBytes > maximumDecodedBytes)
                throw Error("NativeTextureDecodeBudget", "Mip data exceeds the decode budget.");
            var reference = metadata.References[0];
            if (reference.FollowingUInt32 != packedBytes)
                throw Error("NativeMipSizeMismatch", "Mip byte size does not match its dimensions.");
            var payload = GetPayload(source, reference, 2229);
            var buffer = ReadBuffer(source, payload, TextureBufferPatternKind.Raw, packedBytes, token);
            var packedOffset = 0;
            for (var index = firstPackedMip; index < metadata.MipSizeCount; index++)
            {
                token.ThrowIfCancellationRequested();
                var length = checked((int)metadata.MipByteLengthGroups[0][index]);
                var bytes = buffer.Segments.DecodedPrefix.AsSpan(packedOffset, length).ToArray();
                mips.Add(index, (bytes, new(index, Math.Max(1, metadata.Mip0Width >> index),
                    Math.Max(1, metadata.Mip0Height >> index), payload.Identity, payload.Sha256, Hash(bytes),
                    buffer.PayloadExtent, new(payload.Identity, packedOffset, length))));
                packedOffset = checked(packedOffset + length);
            }
            decodedBytes = nominalBytes;
        }
        for (var descriptorIndex = 0; descriptorIndex < metadata.References.Count - 1; descriptorIndex++)
        {
            token.ThrowIfCancellationRequested();
            var reference = metadata.References[descriptorIndex + 1];
            var descriptor = metadata.LodDescriptors[descriptorIndex];
            if (reference.Reference.ResourceType != 2230 || descriptor.ThirdU16 > 1)
                throw Error("UnsupportedNativeTextureLayout", "This mip layout is unavailable.");
            var index = FindMipIndex(metadata, descriptor);
            if (index < 0 || descriptor.TrailingByte != metadata.MipSizeCount - index)
                throw Error("NativeMipDescriptorMismatch", "Mip dimensions do not match the texture metadata.");
            if (mips.ContainsKey(index)) throw Error("AmbiguousNativeMip", "Multiple resources describe the same mip.");
            var expectedBytes = MipByteLength(metadata.FirstByte, descriptor.Width, descriptor.Height);
            if (expectedBytes > TextureBufferPatternInspector.MaximumDecodedBytes || expectedBytes > maximumDecodedBytes - decodedBytes)
                throw Error("NativeTextureDecodeBudget", "Mip data exceeds the decode budget.");
            if (reference.FollowingUInt32 != expectedBytes || metadata.MipByteLengthGroups[0][index] != expectedBytes)
                throw Error("NativeMipSizeMismatch", "Mip byte size does not match its dimensions.");
            var payload = GetPayload(source, reference, 2230);
            var buffer = ReadBuffer(source, payload, TextureBufferPatternKind.RawLod, expectedBytes, token);
            decodedBytes += expectedBytes;
            var bytes = buffer.Segments.DecodedPrefix;
            mips.Add(index, (bytes, new(index, descriptor.Width, descriptor.Height, payload.Identity,
                payload.Sha256, Hash(bytes), buffer.PayloadExtent, new(payload.Identity, 0, expectedBytes))));
        }
        if (!mips.ContainsKey(0)) throw Error("NativeBaseMipUnavailable", "The base mip is unavailable.");
        var supportedCount = 0;
        while (mips.ContainsKey(supportedCount)) supportedCount++;
        var supported = mips.Take(supportedCount).Select(pair => pair.Value).ToArray();
        var size = checked(148 + supported.Sum(mip => mip.Bytes.Length));
        var dds = new byte[size]; "DDS "u8.CopyTo(dds);
        var compressed = metadata.FirstByte is 5 or 7 or 18 or 19 or 25;
        Put(4, 124); Put(8, (supportedCount > 1 ? 0x21007u : 0x1007u) | (compressed ? 0x80000u : 8u));
        Put(12, metadata.Mip0Height); Put(16, metadata.Mip0Width);
        Put(20, compressed ? (uint)supported[0].Bytes.Length : (uint)metadata.Mip0Width * (metadata.FirstByte == 11 ? 1u : 4u)); Put(28, (uint)supportedCount);
        Put(76, 32); Put(80, 4); "DX10"u8.CopyTo(dds.AsSpan(84)); Put(108, supportedCount > 1 ? 0x401008u : 0x1000u);
        Put(128, DxgiFormat(metadata.FirstByte, IsSrgb(source)));
        Put(132, 3); Put(140, 1); if (compressed && metadata.FirstByte != 7) Put(144, 3);
        var cursor = 148;
        foreach (var mip in supported) { token.ThrowIfCancellationRequested(); mip.Bytes.CopyTo(dds, cursor); cursor += mip.Bytes.Length; }
        token.ThrowIfCancellationRequested();
        var document = new DdsTextureReader().Read(dds, metadata.Identity, source.Revision);
        token.ThrowIfCancellationRequested();
        return new(document, supported.Select(mip => mip.Evidence).ToArray(), metadata.MipSizeCount,
            Enumerable.Range(supportedCount, metadata.MipSizeCount - supportedCount).ToArray());
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(offset), value);
    }

    private NativeTextureDecodedDocument ReadCubemap(NativeTextureSource source, TextureFileTextV24Document metadata, CancellationToken token)
    {
        var faces = new[] { CubeFace.PositiveX, CubeFace.NegativeX, CubeFace.PositiveY, CubeFace.NegativeY, CubeFace.PositiveZ, CubeFace.NegativeZ };
        var mips = new Dictionary<(int Face, int Mip), (byte[] Bytes, NativeTextureMipSource Evidence)>();
        for (var face = 0; face < faces.Length; face++)
            for (var mip = 0; mip < metadata.MipSizeCount; mip++)
                if (metadata.MipByteLengthGroups[face][mip] != MipByteLength(metadata.FirstByte,
                    Math.Max(1, metadata.Mip0Width >> mip), Math.Max(1, metadata.Mip0Height >> mip)))
                    throw Error("NativeMipSizeMismatch", "Cubemap mip sizes do not match their dimensions.");

        if (metadata.DescriptorCount == 1)
        {
            var descriptor = metadata.LodDescriptors[0];
            if (descriptor.Width != metadata.Mip0Width || descriptor.Height != metadata.Mip0Height ||
                descriptor.ThirdU16 != 1 || descriptor.TrailingByte != metadata.MipSizeCount)
                throw Error("NativeMipDescriptorMismatch", "Mip dimensions do not match the texture metadata.");
            var length = metadata.MipByteLengthGroups.Sum(group => group.Sum(value => (long)value));
            var paddedLength = checked((length + 255) & ~255L);
            EnsureBudget(paddedLength);
            var reference = metadata.References[0];
            if (reference.FollowingUInt32 != paddedLength)
                throw Error("NativeMipSizeMismatch", "Cubemap byte size does not match its metadata.");
            var payload = GetPayload(source, reference, 2229);
            var buffer = ReadBuffer(source, payload, TextureBufferPatternKind.Raw, paddedLength, token);
            var offset = 0;
            // The stored stream groups faces by mip; DDS groups mip chains by face.
            for (var mip = 0; mip < metadata.MipSizeCount; mip++)
                for (var face = 0; face < faces.Length; face++)
                {
                    var count = checked((int)metadata.MipByteLengthGroups[face][mip]);
                    Add(face, mip, buffer.Segments.DecodedPrefix.AsSpan(offset, count).ToArray(), payload, buffer, offset);
                    offset = checked(offset + count);
                }
        }
        else
        {
            long decodedBytes = 0;
            for (var index = 0; index < metadata.References.Count - 1; index++)
            {
                token.ThrowIfCancellationRequested();
                var descriptor = metadata.LodDescriptors[index];
                var mip = FindMipIndex(metadata, descriptor);
                if (mip < 0 || descriptor.ThirdU16 != 1 || descriptor.TrailingByte != metadata.MipSizeCount - mip)
                    throw Error("NativeMipDescriptorMismatch", "Mip dimensions do not match the texture metadata.");
                if (mips.ContainsKey((0, mip))) throw Error("AmbiguousNativeMip", "Multiple resources describe the same mip.");
                var faceLength = checked((int)metadata.MipByteLengthGroups[0][mip]);
                var length = checked((long)faceLength * faces.Length);
                EnsureBudget(checked(decodedBytes + length));
                var reference = metadata.References[index + 1];
                if (reference.FollowingUInt32 != length)
                    throw Error("NativeMipSizeMismatch", "Cubemap byte size does not match its metadata.");
                var payload = GetPayload(source, reference, 2230);
                var buffer = ReadBuffer(source, payload, TextureBufferPatternKind.RawLod, length, token);
                decodedBytes += length;
                for (var face = 0; face < faces.Length; face++)
                    Add(face, mip, buffer.Segments.DecodedPrefix.AsSpan(face * faceLength, faceLength).ToArray(), payload, buffer, face * faceLength);
            }
        }
        if (!mips.ContainsKey((0, 0))) throw Error("NativeBaseMipUnavailable", "The base mip is unavailable.");
        var supportedCount = 0;
        while (mips.ContainsKey((0, supportedCount))) supportedCount++;
        var supported = Enumerable.Range(0, faces.Length).SelectMany(face => Enumerable.Range(0, supportedCount)
            .Select(mip => mips[(face, mip)])).ToArray();
        var dds = new byte[checked(148 + supported.Sum(mip => mip.Bytes.Length))]; "DDS "u8.CopyTo(dds);
        var compressed = metadata.FirstByte is 5 or 7 or 18 or 19 or 25;
        Put(4, 124); Put(8, (supportedCount > 1 ? 0x21007u : 0x1007u) | (compressed ? 0x80000u : 8u));
        Put(12, metadata.Mip0Height); Put(16, metadata.Mip0Width);
        Put(20, compressed ? (uint)supported[0].Bytes.Length : (uint)metadata.Mip0Width * (metadata.FirstByte == 11 ? 1u : 4u)); Put(28, (uint)supportedCount);
        Put(76, 32); Put(80, 4); "DX10"u8.CopyTo(dds.AsSpan(84));
        Put(108, supportedCount > 1 ? 0x401008u : 0x1008u); Put(112, 0xFE00);
        Put(128, DxgiFormat(metadata.FirstByte, IsSrgb(source)));
        Put(132, 3); Put(136, 4); Put(140, 1); if (compressed && metadata.FirstByte != 7) Put(144, 3);
        var cursor = 148;
        foreach (var mip in supported) { token.ThrowIfCancellationRequested(); mip.Bytes.CopyTo(dds, cursor); cursor += mip.Bytes.Length; }
        var document = new DdsTextureReader().Read(dds, metadata.Identity, source.Revision);
        return new(document, supported.Select(mip => mip.Evidence).ToArray(), metadata.MipSizeCount,
            Enumerable.Range(supportedCount, metadata.MipSizeCount - supportedCount).ToArray());

        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(offset), value);
        void EnsureBudget(long bytes)
        {
            if (bytes > TextureBufferPatternInspector.MaximumDecodedBytes || bytes > maximumDecodedBytes)
                throw Error("NativeTextureDecodeBudget", "Mip data exceeds the decode budget.");
        }
        void Add(int face, int mip, byte[] bytes, NativeTexturePayload payload, TextureBufferPatternInspection buffer, int offset)
        {
            token.ThrowIfCancellationRequested();
            mips.Add((face, mip), (bytes, new NativeTextureMipSource(mip, Math.Max(1, metadata.Mip0Width >> mip),
                Math.Max(1, metadata.Mip0Height >> mip), payload.Identity, payload.Sha256, Hash(bytes), buffer.PayloadExtent,
                new(payload.Identity, offset, bytes.Length)) { Face = faces[face] }));
        }
    }

    private static TextureFileTextV24Document ValidateMetadata(NativeTextureSource source, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source); token.ThrowIfCancellationRequested();
        var metadata = source.Metadata;
        if (source.MetadataBytes.Length < 96 || !source.MetadataBytes.Span[..8].SequenceEqual("FILETEXT"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(source.MetadataBytes.Span[8..]) != 24 ||
            source.MetadataSha256 != Hash(source.MetadataBytes.Span) || metadata.RawFixedPrefix84.Length != 84 ||
            !metadata.RawFixedPrefix84.AsSpan().SequenceEqual(source.MetadataBytes.Span.Slice(12, 84)) ||
            metadata.Revision != source.Revision || metadata.Identity.IndexContentId != source.Revision.IndexContentId)
            throw Error("NativeTextureMetadataMismatch", "Texture metadata does not match its source.");
        if (!MatchesSerializedMetadata(metadata, source.MetadataBytes.Span))
            throw Error("NativeTextureMetadataMismatch", "Texture metadata fields do not match their serialized values.");
        if (metadata.FirstByte is not (0 or 5 or 7 or 11 or 18 or 19 or 25) || metadata.RawFixedPrefix84[0] != metadata.FirstByte ||
            metadata.FirstByte == 11 && IsSrgb(source))
            throw Error("UnsupportedNativeTextureFormat", "This texture format is unavailable.");
        if (metadata.SizeGroupSelector is not (1 or 2) || metadata.SerializedSelector != 1 || metadata.ThirdU16 != 1 ||
            metadata.SkipLodSizeTables || metadata.MipByteLengthGroups.Count != (metadata.SizeGroupSelector == 2 ? 6 : 1) ||
            metadata.MipByteLengthGroups.Any(group => group.Count != metadata.MipSizeCount) ||
            metadata.SizeGroupSelector == 2 && (metadata.Mip0Width != metadata.Mip0Height || metadata.PostSelectorBoolean))
            throw Error("UnsupportedNativeTextureLayout", "This texture layout is unavailable.");
        if (metadata.PostSelectorBoolean && !IsSupportedConversion(metadata))
            throw Error("UnsupportedNativeTextureConversion", "This texture conversion is unavailable.");
        if (metadata.Mip0Width == 0 || metadata.Mip0Height == 0 ||
            metadata.Mip0Width > DdsTextureReader.MaximumDimension || metadata.Mip0Height > DdsTextureReader.MaximumDimension ||
            (long)metadata.Mip0Width * metadata.Mip0Height > DdsTextureReader.MaximumPixelCount ||
            metadata.MipSizeCount == 0 || metadata.MipSizeCount > MaximumMipCount(metadata.Mip0Width, metadata.Mip0Height))
            throw Error("InvalidNativeTextureDimensions", "Texture dimensions or mip count are invalid.");
        if (metadata.DescriptorCount < 1 || metadata.FirstByte == 0 && metadata.DescriptorCount < 2 ||
            metadata.LodDescriptors.Count != metadata.DescriptorCount ||
            metadata.References.Count != metadata.DescriptorCount)
            throw Error("NativeBaseMipUnavailable", "The base mip is unavailable.");
        if (metadata.References.Any(reference => reference.Resolution.Status != ReferenceStatus.Unique ||
            reference.Resolution.Candidates.Count != 1 || reference.CatalogOwner != source.CatalogIdentity ||
            reference.Reference.Owner != metadata.Identity || reference.Reference.Namespace != IdentifierNamespace.Archive))
            throw Error("AmbiguousNativeTextureReference", "Texture references are missing or ambiguous.");
        if (metadata.References[0].Reference.ResourceType != 2229)
            throw Error("UnsupportedNativeTextureLayout", "This texture reference layout is unavailable.");

        return metadata;
    }

    private static bool IsSupportedConversion(TextureFileTextV24Document metadata) =>
        (metadata.FirstByte, metadata.PostSelectorByte1) is (5, 7) or (5, 17) or (5, 19) or (7, 1) or (11, 15) or (18, 21) or (19, 23);

    private sealed record ConvertedMip(int Index, TextureFileTextV24LodDescriptor Descriptor,
        TextureFileTextV24ResourceReference Reference);

    private static ConvertedMip SelectConvertedMip(NativeTextureSource source, int maximumPreviewDimension, bool validatePayloads = true)
    {
        var metadata = source.Metadata;
        if (!IsSupportedConversion(metadata) || metadata.MipByteLengthGroups[0].Any(length => length != 0))
            throw Error("UnsupportedNativeTextureConversion", "This texture conversion is unavailable.");
        var seen = new HashSet<int>();
        for (var descriptor = 0; descriptor < metadata.LodDescriptors.Count; descriptor++)
        {
            var value = metadata.LodDescriptors[descriptor]; var index = FindMipIndex(metadata, value);
            if (index < 0 || value.ThirdU16 > 1 || value.TrailingByte != metadata.MipSizeCount - index || !seen.Add(index))
                throw Error("NativeMipDescriptorMismatch", "Mip dimensions do not match the texture metadata.");
        }
        if (validatePayloads)
            foreach (var reference in metadata.References)
                GetPayload(source, reference, reference.Sequence == 0 ? 2229u : 2230u);
        if (metadata.DescriptorCount == 1)
        {
            if (FindMipIndex(metadata, metadata.LodDescriptors[0]) != 0)
                throw Error("NativeBaseMipUnavailable", "The base mip is unavailable.");
            return new(0, metadata.LodDescriptors[0], metadata.References[0]);
        }
        var candidates = Enumerable.Range(0, metadata.References.Count - 1)
            .Select(index => new ConvertedMip(FindMipIndex(metadata, metadata.LodDescriptors[index]),
                metadata.LodDescriptors[index], metadata.References[index + 1])).OrderBy(value => value.Index).ToArray();
        var baseMip = candidates.FirstOrDefault(value => value.Index == 0)
            ?? throw Error("NativeBaseMipUnavailable", "The base mip is unavailable.");
        return candidates.FirstOrDefault(value => value.Descriptor.Width <= maximumPreviewDimension &&
            value.Descriptor.Height <= maximumPreviewDimension) ?? baseMip;
    }

    private NativeTextureDecodedDocument ReadConverted(NativeTextureSource source, int maximumPreviewDimension, CancellationToken token,
        bool validatePayloads = true)
    {
        var selected = SelectConvertedMip(source, maximumPreviewDimension, validatePayloads);
        var metadata = source.Metadata; var width = selected.Descriptor.Width; var height = selected.Descriptor.Height;
        var normalizedBytes = MipByteLength(metadata.FirstByte, width, height);
        if (selected.Reference.FollowingUInt32 < normalizedBytes ||
            selected.Reference.Reference.ResourceType == 2230 && selected.Reference.FollowingUInt32 != normalizedBytes)
            throw Error("NativeMipSizeMismatch", "Mip byte size does not match its dimensions.");
        var payload = GetPayload(source, selected.Reference, metadata.DescriptorCount == 1 ? 2229u : 2230u);
        if (!TextureBufferPatternInspector.TryGetPayload(payload.OriginalBytes.Span, payload.Extent, payload.Type,
            out _, out var body, out var payloadExtent, out _))
            throw Error("UnsupportedNativeMipPayload", "The mip payload uses an unavailable encoding.");
        if (body.Length >= 4 && BinaryPrimitives.ReadUInt16LittleEndian(body) == 16 && BinaryPrimitives.ReadUInt16LittleEndian(body[2..]) == 255)
        {
            var packet = NativeTextureBlockPacketReader.ReadPacket(body, width, height, normalizedBytes, maximumDecodedBytes, token);
            var blocks = packet.Blocks;
            var blockDds = new byte[checked(148 + blocks.Length)]; "DDS "u8.CopyTo(blockDds);
            bool compressed = metadata.FirstByte is 5 or 7 or 18 or 19 or 25;
            Write(4, 124); Write(8, 0x1007u | (compressed ? 0x80000u : 8u)); Write(12, height); Write(16, width);
            Write(20, compressed ? (uint)blocks.Length : (uint)width * (metadata.FirstByte == 11 ? 1u : 4u));
            Write(28, 1); Write(76, 32); Write(80, 4); "DX10"u8.CopyTo(blockDds.AsSpan(84)); Write(108, 0x1000);
            Write(128, DxgiFormat(metadata.FirstByte, IsSrgb(source))); Write(132, 3); Write(140, 1);
            if (compressed && metadata.FirstByte != 7) Write(144, 3);
            blocks.Span.CopyTo(blockDds.AsSpan(148));
            var blockDocument = new DdsTextureReader().Read(blockDds, metadata.Identity, source.Revision);
            var blockEvidence = new NativeTextureMipSource(selected.Index, width, height, payload.Identity,
                payload.Sha256, Hash(blocks.Span), payloadExtent!, new(payload.Identity, 0, blocks.Length));
            return new(blockDocument, [blockEvidence], metadata.MipSizeCount,
                Enumerable.Range(0, metadata.MipSizeCount).Where(index => index != selected.Index).ToArray());
            void Write(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(blockDds.AsSpan(offset), value);
        }
        var decoded = new ConvertedTextureDecoder().Read(body, metadata.PostSelectorByte1, width, height, maximumDecodedBytes, token);
        var compactRed = !validatePayloads && (decoded.Info.Selector is 15 or 21) && !IsSrgb(source);
        var bytesPerPixel = compactRed ? 1 : 4;
        var dds = new byte[checked(148 + width * height * bytesPerPixel)]; "DDS "u8.CopyTo(dds);
        Put(4, 124); Put(8, 0x100F); Put(12, height); Put(16, width); Put(20, (uint)width * (uint)bytesPerPixel);
        Put(28, 1); Put(76, 32); Put(80, 4); "DX10"u8.CopyTo(dds.AsSpan(84)); Put(108, 0x1000);
        Put(128, compactRed ? 61u : IsSrgb(source) ? 29u : 28u); Put(132, 3); Put(140, 1);
        if (compactRed)
        {
            var pixels = decoded.Rgba8.Span;
            for (int pixel = 0; pixel < width * height; pixel++)
            {
                if ((pixel & 0xffff) == 0) token.ThrowIfCancellationRequested();
                if (pixels[pixel * 4 + 1] != 0 || pixels[pixel * 4 + 2] != 0 || pixels[pixel * 4 + 3] != 255)
                    throw Error("NativeScalarChannels", "The scalar texture channels do not match their source profile.");
                dds[148 + pixel] = pixels[pixel * 4];
            }
        }
        else decoded.Rgba8.Span.CopyTo(dds.AsSpan(148));
        token.ThrowIfCancellationRequested();
        if (metadata.FirstByte == 5 && decoded.Info.Selector is 17 or 19)
        {
            // RGB BC1 has opaque sampling alpha; the coefficient scratch channel is zero.
            Put(144, 3);
            for (var pixel = 148 + 3; pixel < dds.Length; pixel += 4)
            {
                if ((pixel & 4095) == 3) token.ThrowIfCancellationRequested();
                dds[pixel] = 255;
            }
        }
        var document = new DdsTextureReader().Read(dds, metadata.Identity, source.Revision);
        var evidence = new NativeTextureMipSource(selected.Index, width, height, payload.Identity, payload.Sha256,
            compactRed ? Hash(decoded.Rgba8.Span) : Hash(dds.AsSpan(148)), payloadExtent!, new(payload.Identity, 0, decoded.Rgba8.Length));
        return new(document, [evidence], metadata.MipSizeCount,
            Enumerable.Range(0, metadata.MipSizeCount).Where(index => index != selected.Index).ToArray())
        { Conversion = decoded.Info };
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(offset), value);
    }

    private static uint DxgiFormat(byte format, bool srgb) => format switch
    {
        0 => srgb ? 91u : 87u,
        5 => srgb ? 72u : 71u,
        7 => srgb ? 78u : 77u,
        11 => 61u,
        18 => 80u,
        19 => 83u,
        25 => 95u,
        _ => throw Error("UnsupportedNativeTextureFormat", "This texture format is unavailable.")
    };

    private static long MipByteLength(byte format, int width, int height) => format is 5 or 7 or 18 or 19 or 25
        ? checked(((width + 3L) / 4) * ((height + 3L) / 4) * (format is 7 or 19 or 25 ? 16 : 8))
        : checked((long)width * height * (format == 11 ? 1 : 4));

    private static NativeTexturePayload GetPayload(NativeTextureSource source,
        TextureFileTextV24ResourceReference reference, uint type)
    {
        var identity = reference.Resolution.Candidates[0];
        var payloads = source.Payloads.Where(payload => payload.Identity == identity).ToArray();
        if (identity.IndexContentId != source.Revision.IndexContentId || identity.MemberPath is not null ||
            reference.Reference.ResourceType != type || payloads.Length != 1 || payloads[0].Type != type ||
            !uint.TryParse(reference.Reference.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || payloads[0].Id != id)
            throw Error("AmbiguousNativeTextureReference", "The mip source is missing or ambiguous.");
        var payload = payloads[0];
        if (payload.Sha256 != Hash(payload.OriginalBytes.Span))
            throw Error("NativeTextureSourceMismatch", "The mip bytes do not match their source hash.");
        return payload;
    }

    private static TextureBufferPatternInspection ReadBuffer(NativeTextureSource source, NativeTexturePayload payload,
        TextureBufferPatternKind kind, long expectedBytes, CancellationToken token)
    {
        var inspected = new TextureBufferPatternInspector().Parse(payload.OriginalBytes.Span, payload.Identity, source.Revision,
            payload.Extent, payload.Type, TextureBufferPatternProfile.ObservedEtfSeGsCandidateV1, cancellationToken: token);
        if (inspected.Value is not { } buffer || buffer.Kind != kind || buffer.Segments.RawSuffix.Length != 0 ||
            buffer.Segments.ActualDecodedLength != expectedBytes || buffer.Segments.DecodedPrefix.Length != expectedBytes)
            throw Error("UnsupportedNativeMipPayload", "The mip payload is incomplete or uses an unavailable encoding.");
        return buffer;
    }

    private static int FindMipIndex(TextureFileTextV24Document metadata, TextureFileTextV24LodDescriptor descriptor)
    {
        for (var index = 0; index < metadata.MipSizeCount; index++)
            if (Math.Max(1, metadata.Mip0Width >> index) == descriptor.Width && Math.Max(1, metadata.Mip0Height >> index) == descriptor.Height)
                return index;
        return -1;
    }
    private static bool MatchesSerializedMetadata(TextureFileTextV24Document metadata, ReadOnlySpan<byte> bytes)
    {
        var prefix = metadata.RawFixedPrefix84.AsSpan();
        if (metadata.FirstByte != prefix[0] || metadata.Mip0Width != BinaryPrimitives.ReadUInt16LittleEndian(prefix[1..]) ||
            metadata.Mip0Height != BinaryPrimitives.ReadUInt16LittleEndian(prefix[3..]) || metadata.ThirdU16 != BinaryPrimitives.ReadUInt16LittleEndian(prefix[5..]) ||
            metadata.MipSizeCount != prefix[7] || metadata.SizeGroupSelector != prefix[12] || metadata.SkipLodSizeTables != (prefix[13] == 1)) return false;
        var cursor = 96;
        if (bytes.Length - cursor < 1 || bytes[cursor++] != metadata.SerializedSelector) return false;
        if (metadata.SerializedSelector == 1)
        {
            if (bytes.Length - cursor < 2 || bytes[cursor++] != metadata.SelectorLeadingByte || bytes[cursor++] != metadata.DescriptorCount ||
                metadata.LodDescriptors.Count != metadata.DescriptorCount) return false;
            foreach (var descriptor in metadata.LodDescriptors)
            {
                if (bytes.Length - cursor < 7 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[cursor..]) != descriptor.Width ||
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes[(cursor + 2)..]) != descriptor.Height ||
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes[(cursor + 4)..]) != descriptor.ThirdU16 || bytes[cursor + 6] != descriptor.TrailingByte) return false;
                cursor += 7;
            }
        }
        if (bytes.Length - cursor < 3 || (bytes[cursor++] == 1) != metadata.PostSelectorBoolean ||
            bytes[cursor++] != metadata.PostSelectorByte0 || bytes[cursor++] != metadata.PostSelectorByte1) return false;
        foreach (var group in metadata.MipByteLengthGroups)
            foreach (var length in group)
            {
                if (bytes.Length - cursor < 4 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[cursor..]) != length) return false;
                cursor += 4;
            }
        for (var sequence = 0; sequence < metadata.References.Count; sequence++)
        {
            var reference = metadata.References[sequence];
            if (bytes.Length - cursor < 12 || reference.Sequence != sequence ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[cursor..]) != reference.Reference.ResourceType ||
                !uint.TryParse(reference.Reference.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[(cursor + 4)..]) != id ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[(cursor + 8)..]) != reference.FollowingUInt32) return false;
            cursor += 12;
        }
        return cursor == bytes.Length;
    }
    private static int MaximumMipCount(int width, int height)
    { var count = 1; for (var edge = Math.Max(width, height); edge > 1; edge >>= 1) count++; return count; }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static TextureFormatException Error(string code, string message) => new(code, message);
}
