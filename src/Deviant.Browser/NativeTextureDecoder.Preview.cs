using System.Buffers.Binary;
using System.Globalization;
using Deviant.Assets;
using Deviant.Formats.Containers;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

/// <summary>Source selection for one preview mip; cube previews retain all six faces.
/// Converted size fields conservatively allow RGBA8 output before its packet encoding is inspected.</summary>
public sealed record NativeTexturePreviewPlan(int MipIndex, int Width, int Height, bool IsCubeMap, bool IsConverted,
    IReadOnlyList<ResourceIdentity> RequiredPayloadIdentities, long PackageDecodedBytes, long SelectedEncodedBytes)
{
    public bool UsesNativeBlocks { get; init; }
    public long OutputEncodedBytes { get; init; }
}

public sealed partial class NativeTextureDecoder
{
    public const string PreviewVersion = "native-texture-preview-v4-shared-source-surfaces/" + Version;
    private sealed record PreviewSelection(NativeTexturePreviewPlan Plan, TextureFileTextV24ResourceReference Reference,
        TextureBufferPatternKind Kind, IReadOnlyList<long> FaceOffsets, int FaceBytes);

    /// <summary>Requires validated metadata, but does not require or read any payload bytes.</summary>
    public static NativeTexturePreviewPlan PlanPreview(NativeTextureSource metadataSource, int maximumPreviewDimension,
        CancellationToken token = default) => SelectPreview(metadataSource, maximumPreviewDimension, token).Plan;

    public long EstimatePreviewWorkingSetBytes(NativeTextureSource source, int maximumPreviewDimension,
        CancellationToken token = default)
        => EstimatePreviewPlanBytes(PlanPreview(source, maximumPreviewDimension, token));

    private long EstimatePreviewPlanBytes(NativeTexturePreviewPlan plan)
    {
        long outputBytes = plan.OutputEncodedBytes == 0 ? plan.SelectedEncodedBytes : plan.OutputEncodedBytes;
        if (outputBytes > maximumDecodedBytes)
            throw Error("NativeTextureDecodeBudget", "The selected texture representation exceeds the decode budget.");
        long expansionBytes = plan.OutputEncodedBytes > plan.SelectedEncodedBytes ? checked(2 * outputBytes) : 0;
        if (plan.IsConverted)
        {
            long decodedBytes = plan.UsesNativeBlocks ? plan.SelectedEncodedBytes : checked((long)plan.Width * plan.Height * 4);
            if (decodedBytes > maximumDecodedBytes) throw Error("NativeTextureDecodeBudget", "Mip data exceeds the decode budget.");
            return checked(decodedBytes * 3 + 1024 * 1024);
        }
        if (plan.PackageDecodedBytes > TextureBufferPatternInspector.MaximumDecodedBytes)
        {
            if (plan.PackageDecodedBytes > (long)SegsContainerReader.DefaultDecodedSegmentBytes * 2048 ||
                plan.SelectedEncodedBytes > maximumDecodedBytes ||
                plan.SelectedEncodedBytes > TextureBufferPatternInspector.MaximumDecodedBytes)
                throw Error("NativeTextureDecodeBudget", "The selected mip exceeds the decode budget.");
            return checked(plan.SelectedEncodedBytes * 3 + expansionBytes + SegsContainerReader.DefaultDecodedSegmentBytes + 1024 * 1024);
        }
        if (plan.PackageDecodedBytes > maximumDecodedBytes || plan.PackageDecodedBytes > TextureBufferPatternInspector.MaximumDecodedBytes)
            throw Error("NativeTextureDecodeBudget", "Mip data exceeds the decode budget.");
        return checked(plan.PackageDecodedBytes * 3 + plan.SelectedEncodedBytes * 2 + expansionBytes + 1024 * 1024);
    }

    public NativeTextureDecodedDocument ReadPreview(NativeTextureSource source, int maximumPreviewDimension,
        CancellationToken token = default)
    {
        var selected = SelectPreview(source, maximumPreviewDimension, token);
        _ = EstimatePreviewPlanBytes(selected.Plan);
        if (selected.Plan.IsConverted) return ReadConverted(source, maximumPreviewDimension, token, validatePayloads: false);
        var plan = selected.Plan; var metadata = source.Metadata;
        var payload = GetPayload(source, selected.Reference, selected.Kind == TextureBufferPatternKind.Raw ? 2229u : 2230u);
        byte[][] faceBytes;
        SourceExtent payloadExtent;
        if (plan.PackageDecodedBytes > TextureBufferPatternInspector.MaximumDecodedBytes)
        {
            var ranges = ReadSelectedRanges(source, payload, selected, token);
            faceBytes = ranges.DecodedRanges.ToArray();
            payloadExtent = ranges.PayloadExtent;
        }
        else
        {
            var buffer = ReadBuffer(source, payload, selected.Kind, plan.PackageDecodedBytes, token);
            payloadExtent = buffer.PayloadExtent;
            faceBytes = new byte[selected.FaceOffsets.Count][];
            for (int face = 0; face < faceBytes.Length; face++)
            {
                token.ThrowIfCancellationRequested();
                long offset = selected.FaceOffsets[face];
                if (offset < 0 || offset > buffer.Segments.DecodedPrefix.Length - (long)selected.FaceBytes)
                    throw Error("NativeMipSizeMismatch", "The selected mip exceeds its decoded source package.");
                faceBytes[face] = buffer.Segments.DecodedPrefix.AsSpan(checked((int)offset), selected.FaceBytes).ToArray();
            }
        }
        var evidence = new NativeTextureMipSource[selected.FaceOffsets.Count];
        CubeFace[] faces = [CubeFace.PositiveX, CubeFace.NegativeX, CubeFace.PositiveY, CubeFace.NegativeY, CubeFace.PositiveZ, CubeFace.NegativeZ];
        for (int face = 0; face < faceBytes.Length; face++)
        {
            token.ThrowIfCancellationRequested();
            long offset = selected.FaceOffsets[face];
            var bytes = faceBytes[face];
            evidence[face] = new(plan.MipIndex, plan.Width, plan.Height, payload.Identity, payload.Sha256, Hash(bytes),
                payloadExtent, new(payload.Identity, offset, selected.FaceBytes)) { Face = plan.IsCubeMap ? faces[face] : null };
        }
        var dds = new byte[checked(148 + (int)plan.SelectedEncodedBytes)]; "DDS "u8.CopyTo(dds);
        bool compressed = metadata.FirstByte is 5 or 7 or 18 or 19 or 25;
        Put(4, 124); Put(8, 0x1007u | (compressed ? 0x80000u : 8u)); Put(12, (uint)plan.Height); Put(16, (uint)plan.Width);
        Put(20, compressed ? (uint)selected.FaceBytes : (uint)plan.Width * (metadata.FirstByte == 11 ? 1u : 4u)); Put(28, 1);
        Put(76, 32); Put(80, 4); "DX10"u8.CopyTo(dds.AsSpan(84)); Put(108, plan.IsCubeMap ? 0x1008u : 0x1000u);
        if (plan.IsCubeMap) Put(112, 0xFE00);
        Put(128, DxgiFormat(metadata.FirstByte, IsSrgb(source))); Put(132, 3); Put(136, plan.IsCubeMap ? 4u : 0u); Put(140, 1);
        if (compressed && metadata.FirstByte != 7) Put(144, 3);
        int cursor = 148;
        foreach (var bytes in faceBytes) { token.ThrowIfCancellationRequested(); bytes.CopyTo(dds, cursor); cursor += bytes.Length; }
        var document = ReadNativeDocument(dds, source, token);
        token.ThrowIfCancellationRequested();
        return new(document, Array.AsReadOnly(evidence), metadata.MipSizeCount,
            Enumerable.Range(0, metadata.MipSizeCount).Where(index => index != plan.MipIndex).ToArray());
        void Put(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(at), value);
    }

    private static TextureBufferPatternRangeInspection ReadSelectedRanges(NativeTextureSource source,
        NativeTexturePayload payload, PreviewSelection selected, CancellationToken token)
    {
        var plan = selected.Plan;
        var ranges = selected.FaceOffsets.Select(offset => new SegsDecodedByteRange(offset, selected.FaceBytes)).ToArray();
        var result = new TextureBufferPatternInspector().ParseDecodedRanges(payload.OriginalBytes,
            payload.Identity, source.Revision, payload.Extent,
            selected.Kind == TextureBufferPatternKind.Raw ? 2229u : 2230u,
            TextureBufferPatternProfile.ObservedEtfSeGsCandidateV1, ranges,
            checked((uint)plan.PackageDecodedBytes), token);
        var inspection = result.Value;
        if (inspection is null || inspection.Kind != selected.Kind ||
            inspection.DeclaredDecodedLength != plan.PackageDecodedBytes ||
            inspection.DecodedRanges.Count != selected.FaceOffsets.Count ||
            inspection.DecodedRanges.Any(bytes => bytes.Length != selected.FaceBytes))
            throw Error("UnsupportedNativeMipPayload", "The selected mip range is incomplete or uses an unavailable encoding.");
        return inspection;
    }

    private static PreviewSelection SelectPreview(NativeTextureSource source, int maximumPreviewDimension, CancellationToken token)
    {
        if (maximumPreviewDimension < 1) throw new ArgumentOutOfRangeException(nameof(maximumPreviewDimension));
        var metadata = ValidateMetadata(source, token);
        // Every descriptor's typed identity is checked, including payloads omitted from this selection.
        foreach (var reference in metadata.References)
        {
            token.ThrowIfCancellationRequested(); var identity = reference.Resolution.Candidates[0];
            if (identity.IndexContentId != source.Revision.IndexContentId || identity.MemberPath is not null ||
                !uint.TryParse(reference.Reference.Id, NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
                reference.Reference.ResourceType != (reference.Sequence == 0 ? 2229u : 2230u))
                throw Error("AmbiguousNativeTextureReference", "The mip source identity does not match its metadata.");
        }
        if (metadata.PostSelectorBoolean)
        {
            var selected = SelectConvertedMip(source, maximumPreviewDimension, validatePayloads: false);
            long bytes = MipByteLength(metadata.FirstByte, selected.Descriptor.Width, selected.Descriptor.Height);
            if (selected.Reference.FollowingUInt32 < bytes || selected.Reference.Reference.ResourceType == 2230 && selected.Reference.FollowingUInt32 != bytes)
                throw Error("NativeMipSizeMismatch", "Mip byte size does not match its dimensions.");
            bool nativeBlocks = metadata.PostSelectorByte1 == 12;
            long outputBound = nativeBlocks ? bytes : checked((long)selected.Descriptor.Width * selected.Descriptor.Height * 4);
            return new(new(selected.Index, selected.Descriptor.Width, selected.Descriptor.Height, false, true,
                Array.AsReadOnly(new[] { selected.Reference.Resolution.Candidates[0] }), outputBound, outputBound)
                { UsesNativeBlocks = nativeBlocks, OutputEncodedBytes = outputBound }, selected.Reference,
                selected.Reference.Reference.ResourceType == 2229 ? TextureBufferPatternKind.Raw : TextureBufferPatternKind.RawLod, [0], checked((int)bytes));
        }
        bool cube = metadata.SizeGroupSelector == 2; int faceCount = cube ? 6 : 1;
        for (int face = 0; face < faceCount; face++)
        for (int mip = 0; mip < metadata.MipSizeCount; mip++)
        {
            token.ThrowIfCancellationRequested();
            if (metadata.MipByteLengthGroups[face][mip] != MipByteLength(metadata.FirstByte,
                Math.Max(1, metadata.Mip0Width >> mip), Math.Max(1, metadata.Mip0Height >> mip)))
                throw Error("NativeMipSizeMismatch", "Mip byte size does not match its dimensions.");
        }
        var available = new SortedDictionary<int, PreviewSelection>();
        if (!cube || metadata.DescriptorCount == 1)
        {
            var descriptor = metadata.LodDescriptors[^1]; int first = FindMipIndex(metadata, descriptor);
            if (first < 0 || descriptor.ThirdU16 > 1 || descriptor.TrailingByte != metadata.MipSizeCount - first ||
                cube && (first != 0 || descriptor.ThirdU16 != 1))
                throw Error("NativeMipDescriptorMismatch", "Mip dimensions do not match the texture metadata.");
            long nominal = 0;
            for (int mip = first; mip < metadata.MipSizeCount; mip++)
                for (int face = 0; face < faceCount; face++) nominal = checked(nominal + metadata.MipByteLengthGroups[face][mip]);
            long packageBytes = checked((nominal + 255) & ~255L);
            var reference = metadata.References[0];
            if (reference.FollowingUInt32 != packageBytes) throw Error("NativeMipSizeMismatch", "Packed mip sizes do not match their metadata.");
            long offset = 0;
            for (int mip = first; mip < metadata.MipSizeCount; mip++)
            {
                var offsets = new long[faceCount]; int bytes = checked((int)metadata.MipByteLengthGroups[0][mip]);
                for (int face = 0; face < faceCount; face++) { offsets[face] = offset; offset = checked(offset + metadata.MipByteLengthGroups[face][mip]); }
                available.Add(mip, Make(mip, reference, TextureBufferPatternKind.Raw, packageBytes, offsets, bytes));
            }
        }
        if (!cube || metadata.DescriptorCount > 1)
        for (int descriptorIndex = 0; descriptorIndex < metadata.References.Count - 1; descriptorIndex++)
        {
            token.ThrowIfCancellationRequested(); var descriptor = metadata.LodDescriptors[descriptorIndex];
            int mip = FindMipIndex(metadata, descriptor); var reference = metadata.References[descriptorIndex + 1];
            if (mip < 0 || descriptor.ThirdU16 > 1 || cube && descriptor.ThirdU16 != 1 ||
                descriptor.TrailingByte != metadata.MipSizeCount - mip)
                throw Error("NativeMipDescriptorMismatch", "Mip dimensions do not match the texture metadata.");
            if (available.ContainsKey(mip)) throw Error("AmbiguousNativeMip", "Multiple resources describe the same mip.");
            int bytes = checked((int)metadata.MipByteLengthGroups[0][mip]); long packageBytes = checked((long)bytes * faceCount);
            if (reference.FollowingUInt32 != packageBytes) throw Error("NativeMipSizeMismatch", "Mip byte size does not match its dimensions.");
            available.Add(mip, Make(mip, reference, TextureBufferPatternKind.RawLod, packageBytes,
                Enumerable.Range(0, faceCount).Select(face => (long)face * bytes).ToArray(), bytes));
        }
        if (!available.ContainsKey(0)) throw Error("NativeBaseMipUnavailable", "The base mip is unavailable.");
        int supported = 0; while (available.ContainsKey(supported)) supported++;
        var usable = available.Take(supported).Select(pair => pair.Value).ToArray();
        return usable.FirstOrDefault(value => value.Plan.Width <= maximumPreviewDimension && value.Plan.Height <= maximumPreviewDimension) ?? usable[^1];

        PreviewSelection Make(int mip, TextureFileTextV24ResourceReference reference, TextureBufferPatternKind kind,
            long packageBytes, IReadOnlyList<long> offsets, int bytes) => new(new(mip, Math.Max(1, metadata.Mip0Width >> mip),
                Math.Max(1, metadata.Mip0Height >> mip), cube, false,
                Array.AsReadOnly(new[] { reference.Resolution.Candidates[0] }), packageBytes, checked((long)bytes * faceCount))
                { OutputEncodedBytes = checked((long)bytes * faceCount * (IsScalarSrgb(source) ? 4 : 1)) },
                reference, kind, offsets, bytes);
    }
}
