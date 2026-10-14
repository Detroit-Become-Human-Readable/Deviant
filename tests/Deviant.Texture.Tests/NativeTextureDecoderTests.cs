using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class NativeTextureDecoderTests
{
    [Fact]
    public void PreviewExtractsOnlyTheSelectedMipFromALargeInlinePackage()
    {
        var (source, expectedMip) = NativeMipFixture.CreateLargeInlinePackage();
        var plan = NativeTextureDecoder.PlanPreview(source with { Payloads = [] }, 1024);

        Assert.Equal(2, plan.MipIndex);
        Assert.True(plan.PackageDecodedBytes > TextureBufferPatternInspector.MaximumDecodedBytes);
        Assert.Equal(expectedMip.Length, plan.SelectedEncodedBytes);

        var preview = new NativeTextureDecoder().ReadPreview(source, 1024);
        Assert.Equal(1024, preview.Document.Info.Width);
        Assert.Equal(1024, preview.Document.Info.Height);
        var mip = Assert.Single(preview.SupportedMips);
        Assert.Equal(2, mip.MipIndex);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expectedMip)).ToLowerInvariant(), mip.DecodedSha256);
        Assert.Equal(expectedMip, preview.Document.OriginalBytes.Span[148..].ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PreviewPlansWithoutPayloadsAndDecodesOnlyTheSelectedMip(bool packed, bool srgb)
    {
        var source = NativeMipFixture.Create(packed: packed, srgb: srgb); var decoder = new NativeTextureDecoder();
        var full = decoder.ReadDetailed(source); var fullHash = SHA256.HashData(full.Document.OriginalBytes.Span);
        foreach (int dimension in new[] { 8, 4, 2, 1 })
        {
            var plan = NativeTextureDecoder.PlanPreview(source with { Payloads = [] }, dimension);
            var selected = source with { Payloads = source.Payloads.Where(payload => plan.RequiredPayloadIdentities.Contains(payload.Identity)).ToArray() };
            Assert.Single(plan.RequiredPayloadIdentities); Assert.Single(selected.Payloads);
            var preview = decoder.ReadPreview(selected, dimension);
            Assert.Equal(1, preview.Document.Info.MipCount); Assert.Equal(plan.Width, preview.Document.Info.Width);
            Assert.Equal(plan.MipIndex, Assert.Single(preview.SupportedMips).MipIndex);
            Assert.Equal(full.SupportedMips[plan.MipIndex].MipExtent, preview.SupportedMips[0].MipExtent);
            Assert.Equal(full.SupportedMips[plan.MipIndex].OriginalSha256, preview.SupportedMips[0].OriginalSha256);
            var expected = new DdsTextureDecoder().DecodeMip(full.Document, plan.MipIndex);
            var actual = new DdsTextureDecoder().DecodeMip(preview.Document);
            Assert.Equal(expected.Rgba8.ToArray(), actual.Rgba8.ToArray()); Assert.Equal(expected.IsSrgb, actual.IsSrgb);
            Assert.Equal(source.Metadata.MipSizeCount - 1, preview.OmittedMipIndices.Count);
            Assert.True(decoder.EstimatePreviewWorkingSetBytes(selected, dimension) > 0);
        }
        Assert.Equal(fullHash, SHA256.HashData(decoder.ReadDetailed(source).Document.OriginalBytes.Span));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(18)]
    public void PreviewPreservesSourceChannelsAndAlphaForOrdinaryFormats(byte format)
    {
        var source = NativeMipFixture.Create(format: format); var decoder = new NativeTextureDecoder();
        var full = decoder.ReadDetailed(source); var plan = NativeTextureDecoder.PlanPreview(source, 4);
        var partial = source with { Payloads = source.Payloads.Where(payload => plan.RequiredPayloadIdentities.Contains(payload.Identity)).ToArray() };
        var preview = decoder.ReadPreview(partial, 4);
        Assert.Equal(new DdsTextureDecoder().DecodeMip(full.Document, plan.MipIndex).Rgba8.ToArray(),
            new DdsTextureDecoder().DecodeMip(preview.Document).Rgba8.ToArray());
        Assert.Equal(full.Document.Info.Format, preview.Document.Info.Format);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CubePreviewKeepsAllFacesAtTheOriginalSelectedMip(bool split)
    {
        var source = NativeMipFixture.CreateCube(split: split, varyMipColors: true); var decoder = new NativeTextureDecoder();
        var full = decoder.ReadDetailed(source);
        foreach (int dimension in new[] { 4, 2, 1 })
        {
            var plan = NativeTextureDecoder.PlanPreview(source with { Payloads = [] }, dimension);
            var partial = source with { Payloads = source.Payloads.Where(payload => plan.RequiredPayloadIdentities.Contains(payload.Identity)).ToArray() };
            var preview = decoder.ReadPreview(partial, dimension);
            Assert.True(preview.Document.Info.IsCubeMap); Assert.Equal(1, preview.Document.Info.MipCount);
            Assert.Equal(6, preview.Document.Subresources.Count); Assert.Equal(6, preview.SupportedMips.Count);
            Assert.Single(partial.Payloads);
            foreach (var evidence in preview.SupportedMips)
            {
                Assert.Equal(plan.MipIndex, evidence.MipIndex);
                var original = full.SupportedMips.Single(item => item.MipIndex == plan.MipIndex && item.Face == evidence.Face);
                Assert.Equal(original.MipExtent, evidence.MipExtent); Assert.Equal(original.DecodedSha256, evidence.DecodedSha256);
                Assert.Equal(new DdsTextureDecoder().DecodeSubresource(full.Document, new(0, evidence.Face, plan.MipIndex)).Rgba8.ToArray(),
                    new DdsTextureDecoder().DecodeSubresource(preview.Document, new(0, evidence.Face, 0)).Rgba8.ToArray());
            }
        }
    }

    [Fact]
    public void HdrCubePreviewPreservesFloatingPointPixelsWithoutAnImplicitDisplayTransform()
    {
        var source = NativeMipFixture.CreateCube(format: 25); var decoder = new NativeTextureDecoder();
        var full = decoder.ReadDetailed(source); var preview = decoder.ReadPreview(source, 2);
        Assert.True(preview.Document.Info.IsHdr); Assert.True(preview.Document.Info.IsCubeMap);
        foreach (var item in preview.SupportedMips)
        {
            var expected = new DdsTextureDecoder().DecodeSubresource(full.Document, new(0, item.Face, item.MipIndex));
            var actual = new DdsTextureDecoder().DecodeSubresource(preview.Document, new(0, item.Face, 0));
            Assert.Equal(expected.HdrPixels.ToArray(), actual.HdrPixels.ToArray()); Assert.Null(actual.DisplayTransform);
        }
    }

    [Fact]
    public void ConvertedPreviewAcceptsOnlyItsRequiredPayloadAndRetainsFullPathValidation()
    {
        var source = ConvertedTextureFixture.Source(); var decoder = new NativeTextureDecoder();
        var plan = NativeTextureDecoder.PlanPreview(source with { Payloads = [] }, 16);
        var partial = source with { Payloads = source.Payloads.Where(payload => plan.RequiredPayloadIdentities.Contains(payload.Identity)).ToArray() };
        Assert.Single(partial.Payloads); Assert.Equal(1, plan.MipIndex);
        var expected = decoder.ReadDetailed(source, maximumPreviewDimension: 16); var actual = decoder.ReadPreview(partial, 16);
        Assert.Equal(expected.Document.OriginalBytes.ToArray(), actual.Document.OriginalBytes.ToArray());
        Assert.Equal(expected.SupportedMips, actual.SupportedMips);
        Assert.Equal(decoder.EstimateWorkingSetBytes(source, 16), decoder.EstimatePreviewWorkingSetBytes(partial, 16));
        Assert.Throws<TextureFormatException>(() => decoder.ReadDetailed(partial, maximumPreviewDimension: 16));
    }

    [Theory]
    [InlineData("following", "NativeMipSizeMismatch")]
    [InlineData("table", "NativeMipSizeMismatch")]
    [InlineData("duplicate", "AmbiguousNativeMip")]
    public void PreviewRejectsMalformedMetadataBeforePayloadSelection(string defect, string code)
    {
        var source = NativeMipFixture.Create(defect: defect);
        Assert.Equal(code, Assert.Throws<TextureFormatException>(() => NativeTextureDecoder.PlanPreview(source with { Payloads = [] }, 4)).Code);
    }

    [Theory]
    [InlineData("decoded")]
    [InlineData("suffix")]
    public void PreviewDoesNotAcceptMalformedSelectedPackedPackages(string defect)
    {
        var source = NativeMipFixture.Create(packed: true, defect: defect);
        Assert.Equal("UnsupportedNativeMipPayload", Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder().ReadPreview(source, 2)).Code);
    }

    [Fact]
    public void PreviewRetainsPayloadHashDecodeBudgetAndCancellationChecks()
    {
        var source = NativeMipFixture.Create(); var plan = NativeTextureDecoder.PlanPreview(source, 4);
        var payload = source.Payloads.Single(item => item.Identity == plan.RequiredPayloadIdentities[0]);
        var changed = payload.OriginalBytes.ToArray(); changed[^1] ^= 1;
        Assert.Equal("NativeTextureSourceMismatch", Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder().ReadPreview(
            source with { Payloads = [payload with { OriginalBytes = changed }] }, 4)).Code);
        Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder().ReadPreview(source with { Payloads = [payload, payload] }, 4));
        Assert.Equal("NativeTextureDecodeBudget", Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder(7)
            .EstimatePreviewWorkingSetBytes(source, 4)).Code);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new NativeTextureDecoder().ReadPreview(source, 4, cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodesSplitBc1MipsAndPackedTailWithTheirSourceIdentity(bool srgb)
    {
        var source = NativeMipFixture.Create(srgb: srgb);
        var result = new NativeTextureDecoder().ReadDetailed(source);
        Assert.Equal(TextureStorageFormat.Bc1, result.Document.Info.Format);
        Assert.Equal(srgb, result.Document.Info.IsSrgb);
        Assert.Equal(4, result.Document.Info.MipCount);
        Assert.Empty(result.OmittedMipIndices);
        var surface = new DdsTextureDecoder().DecodeMip(result.Document);
        Assert.Equal(source.Metadata.Identity, surface.Identity);
        Assert.Equal(source.Revision, surface.Revision);
        AssertPixels(surface, [0, 0, 0, 255]);
        AssertPixels(new DdsTextureDecoder().DecodeMip(result.Document, 1), [255, 0, 0, 255]);
        Assert.Equal(new long[] { 0, 0, 0, 8 }, result.SupportedMips.Select(mip => mip.MipExtent!.Offset));
        AssertPixels(new DdsTextureDecoder().DecodeMip(result.Document, 2), [0, 255, 0, 255]);
        AssertPixels(new DdsTextureDecoder().DecodeMip(result.Document, 3), [0, 0, 255, 255]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodesEveryPackedMipWithoutTreatingAllocationPaddingAsPixels(bool srgb)
    {
        var source = NativeMipFixture.Create(packed: true, srgb: srgb);
        var result = new NativeTextureDecoder().ReadDetailed(source);
        Assert.Equal(4, result.Document.Info.MipCount);
        Assert.Empty(result.OmittedMipIndices);
        Assert.Equal(new long[] { 0, 16, 24, 32 }, result.SupportedMips.Select(mip => mip.MipExtent!.Offset));
        Assert.Equal(new long[] { 16, 8, 8, 8 }, result.SupportedMips.Select(mip => mip.MipExtent!.Length));
        Assert.Equal(40, result.Document.Info.EncodedLength);
        Assert.Equal(0, result.Document.Info.TrailingLength);
        byte[][] colors = [[0, 0, 0, 255], [255, 0, 0, 255], [0, 255, 0, 255], [0, 0, 255, 255]];
        for (var mip = 0; mip < colors.Length; mip++)
        {
            AssertPixels(new DdsTextureDecoder().DecodeMip(result.Document, mip), colors[mip]);
            Assert.Equal(source.SelectedIdentity, result.SupportedMips[mip].Identity);
            Assert.Equal(source.Payloads[0].Sha256, result.SupportedMips[mip].OriginalSha256);
        }
        Assert.Equal(4, result.TotalMipCount);
    }

    [Fact]
    public void PackedTwoDimensionalChainPreservesZeroDescriptorThirdField()
    {
        var source = NativeMipFixture.Create(packed: true, descriptorThird: 0);
        var result = new NativeTextureDecoder().ReadDetailed(source);
        Assert.Equal(0, source.Metadata.LodDescriptors[0].ThirdU16);
        Assert.Equal(4, result.Document.Info.MipCount);
        Assert.Empty(result.OmittedMipIndices);
        Assert.Equal(new long[] { 0, 16, 24, 32 }, result.SupportedMips.Select(mip => mip.MipExtent!.Offset));
        AssertPixels(new DdsTextureDecoder().DecodeMip(result.Document, 1), [255, 0, 0, 255]);
        Assert.Equal(source.Metadata.Identity, result.Document.Identity);
        Assert.Equal(source.Revision, result.Document.Revision);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(ushort.MaxValue)]
    public void PackedTwoDimensionalChainRejectsUnsupportedDescriptorThirdField(ushort third)
    {
        var error = Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder().ReadDetailed(
            NativeMipFixture.Create(packed: true, descriptorThird: third)));
        Assert.Equal("NativeMipDescriptorMismatch", error.Code);
    }

    [Fact]
    public void PackedCubemapPreservesFacesAndMipOrder()
    {
        var source = NativeMipFixture.CreateCube();
        var result = new NativeTextureDecoder().ReadDetailed(source);
        Assert.True(result.Document.Info.IsCubeMap);
        Assert.Equal(3, result.Document.Info.MipCount);
        Assert.Equal(18, result.Document.Subresources.Count);
        Assert.Equal(18, result.SupportedMips.Count);
        Assert.Equal(6, result.SupportedMips.Select(mip => mip.Face).Distinct().Count());
        var expected = new byte[][] { [255, 0, 0, 255], [0, 255, 0, 255], [0, 0, 255, 255], [255, 255, 255, 255], [255, 255, 0, 255], [255, 0, 255, 255] };
        var faces = result.Document.Subresources.Select(item => item.Selection.Face).Distinct().ToArray();
        for (var face = 0; face < faces.Length; face++)
            for (var mip = 0; mip < 3; mip++)
                AssertPixels(new DdsTextureDecoder().DecodeSubresource(result.Document, new(0, faces[face], mip)), expected[face]);
    }

    [Fact]
    public void HdrCubemapKeepsFloatingPointPixelsAndCanBeToneMappedForPreview()
    {
        var source = NativeMipFixture.CreateCube(format: 25);
        var result = new NativeTextureDecoder().ReadDetailed(source);
        Assert.Equal(TextureStorageFormat.Bc6Unsigned, result.Document.Info.Format);
        Assert.True(result.Document.Info.IsHdr); Assert.True(result.Document.Info.IsCubeMap);
        Assert.False(NativeTextureDecoder.IsSrgb(source));
        var hdr = new DdsTextureDecoder().DecodeMip(result.Document);
        Assert.True(hdr.IsHdr); Assert.Equal(16, hdr.HdrPixels.Length);
        var display = HdrDisplayTransform.ToSdr(hdr);
        Assert.False(display.IsHdr); Assert.True(display.SourceIsHdr); Assert.Equal(64, display.Rgba8.Length);
    }

    [Fact]
    public void RawBc3PreservesColorAndAlpha()
    {
        var document = new NativeTextureDecoder().Read(NativeMipFixture.Create(format: 7, packed: true));
        Assert.Equal(TextureStorageFormat.Bc3, document.Info.Format);
        AssertPixels(new DdsTextureDecoder().DecodeMip(document, 1), [255, 0, 0, 200]);
    }

    [Fact]
    public void RawBc4KeepsItsScalarChannel()
    {
        var document = new NativeTextureDecoder().Read(NativeMipFixture.Create(format: 18, packed: true));
        Assert.Equal(TextureStorageFormat.Bc4, document.Info.Format);
        var surface = new DdsTextureDecoder().DecodeMip(document);
        Assert.Equal(200, surface.Rgba8.Span[0]); Assert.Equal(255, surface.Rgba8.Span[3]);
    }

    [Fact]
    public void RawRedChannelKeepsTheNativeSamplingChannels()
    {
        var result = new NativeTextureDecoder().ReadDetailed(NativeMipFixture.Create(format: 11, packed: true));
        Assert.Equal("R8_UNORM", result.Document.Info.FormatName);
        Assert.Equal(4, result.Document.Info.MipCount);
        AssertPixels(new DdsTextureDecoder().DecodeMip(result.Document), [200, 0, 0, 255]);
    }

    [Fact]
    public void SeparateBaseMipAcceptsZeroDescriptorFieldAndKeepsAllMips()
    {
        var result = new NativeTextureDecoder().ReadDetailed(NativeMipFixture.Create(descriptorThird: 0));
        Assert.Equal(4, result.Document.Info.MipCount);
        Assert.Empty(result.OmittedMipIndices);
        AssertPixels(new DdsTextureDecoder().DecodeMip(result.Document), [0, 0, 0, 255]);
    }

    [Fact]
    public void KeepsBgraChannelAndAlphaBehavior()
    {
        var result = new NativeTextureDecoder().ReadDetailed(NativeMipFixture.Create(format: 0));
        Assert.Equal(TextureStorageFormat.Bgra8, result.Document.Info.Format);
        AssertPixels(new DdsTextureDecoder().DecodeMip(result.Document), [11, 22, 33, 44]);
    }

    [Theory]
    [InlineData("following", "NativeMipSizeMismatch")]
    [InlineData("table", "NativeMipSizeMismatch")]
    [InlineData("decoded", "UnsupportedNativeMipPayload")]
    [InlineData("suffix", "UnsupportedNativeMipPayload")]
    public void RejectsPackedSizeAndPayloadDisagreements(string defect, string code)
    {
        var source = NativeMipFixture.Create(packed: true, defect: defect);
        Assert.Equal(code, Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder().Read(source)).Code);
    }

    [Fact]
    public void RejectsChangedStoredBytesMetadataAndAmbiguousPackedSources()
    {
        var source = NativeMipFixture.Create(packed: true);
        var payload = source.Payloads[0];
        var changed = payload.OriginalBytes.ToArray(); changed[^1] ^= 1;
        Assert.Equal("NativeTextureSourceMismatch", Assert.Throws<TextureFormatException>(() =>
            new NativeTextureDecoder().Read(source with { Payloads = [payload with { OriginalBytes = changed }] })).Code);
        Assert.Equal("NativeTextureMetadataMismatch", Assert.Throws<TextureFormatException>(() =>
            new NativeTextureDecoder().Read(source with { Metadata = source.Metadata with { Mip0Width = 16 } })).Code);
        Assert.Equal("NativeTextureMetadataMismatch", Assert.Throws<TextureFormatException>(() =>
            new NativeTextureDecoder().Read(source with { MetadataSha256 = new string('0', 64) })).Code);
        Assert.Equal("AmbiguousNativeTextureReference", Assert.Throws<TextureFormatException>(() =>
            new NativeTextureDecoder().Read(source with { Payloads = [payload, payload] })).Code);
    }

    [Fact]
    public void KeepsConversionFormatBudgetAndCancellationGuards()
    {
        Assert.Equal("UnsupportedNativeTextureConversion", Assert.Throws<TextureFormatException>(() =>
            new NativeTextureDecoder().Read(NativeMipFixture.Create(packed: true, conversion: true))).Code);
        Assert.Equal("UnsupportedNativeTextureFormat", Assert.Throws<TextureFormatException>(() =>
            new NativeTextureDecoder().Read(NativeMipFixture.Create(format: 6))).Code);
        Assert.Equal("NativeBaseMipUnavailable", Assert.Throws<TextureFormatException>(() =>
            new NativeTextureDecoder().Read(NativeMipFixture.Create(format: 0, packed: true))).Code);
        Assert.Equal("NativeTextureDecodeBudget", Assert.Throws<TextureFormatException>(() =>
            new NativeTextureDecoder(255).Read(NativeMipFixture.Create(packed: true))).Code);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new NativeTextureDecoder().Read(
            NativeMipFixture.Create(packed: true), cancelled.Token));
    }

    private static void AssertPixels(TextureSurface surface, byte[] expected)
    {
        Assert.Equal(surface.Width * surface.Height * 4, surface.Rgba8.Length);
        for (var offset = 0; offset < surface.Rgba8.Length; offset += 4)
            Assert.Equal(expected, surface.Rgba8.Slice(offset, 4).ToArray());
    }
}

internal static class NativeMipFixture
{
    internal static (NativeTextureSource Source, byte[] ExpectedPreviewMip) CreateLargeInlinePackage()
    {
        const string index = "generated-large-inline-texture-index";
        const int width = 4096;
        const int mipCount = 13;
        const byte format = 11;
        var catalog = new ResourceIdentity(index, 0);
        var container = new ResourceIdentity(index, 1);
        var revision = new SourceRevision(index, "generated-large-inline-texture-source");
        var lengths = Enumerable.Range(0, mipCount)
            .Select(mip => Math.Max(1, width >> mip) * Math.Max(1, width >> mip)).ToArray();
        var packageLength = (lengths.Sum() + 255) & ~255;
        var package = new byte[packageLength];
        var expectedPreviewMip = new byte[lengths[2]];
        var offset = 0;
        for (var mip = 0; mip < lengths.Length; mip++)
        {
            var value = mip == 2 ? (byte)200 : (byte)(mip + 1);
            Array.Fill(package, value, offset, lengths[mip]);
            if (mip == 2) Array.Fill(expectedPreviewMip, value);
            offset += lengths[mip];
        }

        var raw = Payload(new(index, 2), 2229, 10, WrapLargeRaw(package));
        var prefix = new byte[84];
        prefix[0] = format;
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(1), width);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(3), width);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(5), 1);
        prefix[7] = mipCount;
        prefix[12] = 1;

        using var encodedStream = new MemoryStream();
        using (var writer = new BinaryWriter(encodedStream, System.Text.Encoding.UTF8, true))
        {
            writer.Write("FILETEXT"u8); writer.Write(24u); writer.Write(prefix);
            writer.Write((byte)1); writer.Write((byte)0); writer.Write((byte)1);
            writer.Write((ushort)width); writer.Write((ushort)width); writer.Write((ushort)0); writer.Write((byte)mipCount);
            writer.Write(false); writer.Write((byte)0); writer.Write((byte)0);
            foreach (var length in lengths) writer.Write((uint)length);
            writer.Write(2229u); writer.Write(raw.Id); writer.Write((uint)package.Length);
        }

        var encoded = encodedStream.ToArray();
        var row = new TextureCatalogReference(0, TextureCatalogEntryKind.Raw,
            new(IdentifierNamespace.Archive, raw.Id.ToString(), catalog, raw.Type), ReferenceStatus.Unique,
            [raw.Identity], 0, new("generated.dat", 0, 9), false, null);
        var membership = new TextureCatalogMembership(catalog, revision, 1,
            new("generated.dat", 0, 9), 1, 0, [row]);
        var parsed = new TextureFileTextV24Reader().Parse(encoded, 0, encoded.Length, container, revision,
            new(container, 0, encoded.Length), [new("generated.dat", 0, encoded.Length)], membership);
        var metadata = parsed.Value ?? throw new InvalidOperationException(
            "Generated large inline metadata is invalid: " + string.Join("; ", parsed.Diagnostics));
        return (new(raw.Identity, revision, catalog, "generated-large-inline-catalog",
            Payload(container, 29, 2, [1, 2, 3]), metadata, encoded, Hash(encoded), [raw]), expectedPreviewMip);
    }

    internal static NativeTextureSource CreateCube(byte format = 5, bool split = false, bool varyMipColors = false)
    {
        const string index = "generated-cube-index";
        var catalog = new ResourceIdentity(index, 0); var container = new ResourceIdentity(index, 1);
        var revision = new SourceRevision(index, "generated-source");
        var length = format == 25 ? 16u : 8u;
        using var packed = new MemoryStream();
        var colors = new ushort[] { 0xF800, 0x07E0, 0x001F, 0xFFFF, 0xFFE0, 0xF81F };
        for (var mip = 0; mip < 3; mip++)
            for (var face = 0; face < 6; face++)
            {
                if (format == 25) { var block = new byte[16]; block[0] = 3; packed.Write(block); }
                else packed.Write(TextureFixtureBuilder.SolidDxt1Block(varyMipColors ? (ushort)(colors[face] ^ (mip * 0x0841)) : colors[face], 0, 0));
            }
        var padded = new byte[(packed.Length + 255) & ~255L]; packed.ToArray().CopyTo(padded, 0);
        var payload = Payload(new(index, 2), 2229, 10, Wrap(padded, true));
        var payloads = new List<NativeTexturePayload> { payload };
        if (split)
            for (int mip = 0; mip < 3; mip++)
                payloads.Add(Payload(new(index, 3 + mip), 2230, (uint)(11 + mip),
                    Wrap(packed.ToArray().AsSpan(checked((int)(mip * 6 * length)), checked((int)(6 * length))).ToArray(), false)));
        var prefix = new byte[84]; prefix[0] = format;
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(1), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(3), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(5), 1);
        prefix[7] = 3; prefix[12] = 2;
        using var encodedStream = new MemoryStream();
        using (var writer = new BinaryWriter(encodedStream, System.Text.Encoding.UTF8, true))
        {
            writer.Write("FILETEXT"u8); writer.Write(24u); writer.Write(prefix);
            writer.Write((byte)1); writer.Write((byte)0); writer.Write(split ? (byte)4 : (byte)1);
            if (split)
                for (int mip = 0; mip < 3; mip++)
                { writer.Write((ushort)(4 >> mip)); writer.Write((ushort)(4 >> mip)); writer.Write((ushort)1); writer.Write((byte)(3 - mip)); }
            writer.Write(split ? (ushort)1 : (ushort)4); writer.Write(split ? (ushort)1 : (ushort)4);
            writer.Write((ushort)1); writer.Write(split ? (byte)1 : (byte)3);
            writer.Write(false); writer.Write((byte)0); writer.Write((byte)0);
            for (var face = 0; face < 6; face++) for (var mip = 0; mip < 3; mip++) writer.Write(length);
            writer.Write(2229u); writer.Write(payload.Id); writer.Write((uint)padded.Length);
            if (split)
                foreach (var lod in payloads.Skip(1)) { writer.Write(2230u); writer.Write(lod.Id); writer.Write(length * 6); }
        }
        var encoded = encodedStream.ToArray();
        var rows = payloads.Select((item, ordinal) => new TextureCatalogReference(ordinal,
            ordinal == 0 ? TextureCatalogEntryKind.Raw : TextureCatalogEntryKind.RawLod,
            new(IdentifierNamespace.Archive, item.Id.ToString(), catalog, item.Type), ReferenceStatus.Unique,
            [item.Identity], 0, new("generated.dat", ordinal * 9, 9), false, null)).ToArray();
        var membership = new TextureCatalogMembership(catalog, revision, 1, new("generated.dat", 0, rows.Length * 9), rows.Length, 0, rows);
        var metadata = new TextureFileTextV24Reader().Parse(encoded, 0, encoded.Length, container, revision,
            new(container, 0, encoded.Length), [new("generated.dat", 100, 3)], membership).Value!;
        return new(payload.Identity, revision, catalog, "generated-catalog", Payload(container, 29, 2, [1, 2, 3]),
            metadata, encoded, Hash(encoded), payloads);
    }

    internal static NativeTextureSource Create(byte format = 5, bool packed = false, bool srgb = false,
        bool conversion = false, string? defect = null, ushort descriptorThird = 1)
    {
        const string index = "generated-texture-index";
        var catalog = new ResourceIdentity(index, 0); var container = new ResourceIdentity(index, 1);
        var revision = new SourceRevision(index, "generated-source");
        var lengths = Enumerable.Range(0, 4).Select(mip => format is 0 or 11
            ? Math.Max(1, 8 >> mip) * Math.Max(1, 4 >> mip) * (format == 11 ? 1 : 4)
            : TextureFixtureBuilder.MipLength(Math.Max(1, 8 >> mip), Math.Max(1, 4 >> mip), format == 7 ? 16 : 8)).ToArray();
        var mipBytes = Enumerable.Range(0, 4).Select(mip => Pixels(format, mip, lengths[mip])).ToArray();
        var packedMips = mipBytes.Skip(packed ? 0 : 2).ToArray();
        var padded = new byte[(packedMips.Sum(mip => mip.Length) + 255) & ~255];
        Array.Fill(padded, (byte)0xA7); var cursor = 0;
        foreach (var mip in packedMips) { mip.CopyTo(padded, cursor); cursor += mip.Length; }
        if (defect == "decoded") Array.Resize(ref padded, padded.Length + 1);
        var raw = Payload(new(index, 2), 2229, 10, Wrap(padded, true, defect == "suffix"));
        var payloads = new List<NativeTexturePayload> { raw };
        if (!packed)
            for (var mip = 0; mip < 2; mip++)
                payloads.Add(Payload(new(index, mip + 3), 2230, (uint)mip + 11, Wrap(mipBytes[mip], false)));
        var descriptors = packed ? new[] { new TextureFileTextV24LodDescriptor(8, 4, descriptorThird, 4) } :
            new TextureFileTextV24LodDescriptor[] { new(8, 4, descriptorThird, 4), new(4, 2, 1, 3), new(2, 1, 1, 2) };
        if (defect == "duplicate" && !packed) descriptors[1] = descriptors[0];
        var prefix = new byte[84]; prefix[0] = format;
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(1), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(3), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(5), 1);
        prefix[7] = 4; prefix[12] = 1; prefix[14] = srgb ? (byte)1 : (byte)0;
        using var encodedStream = new MemoryStream();
        using (var writer = new BinaryWriter(encodedStream, System.Text.Encoding.UTF8, true))
        {
            writer.Write("FILETEXT"u8); writer.Write(24u); writer.Write(prefix);
            writer.Write((byte)1); writer.Write((byte)0); writer.Write((byte)descriptors.Length);
            foreach (var descriptor in descriptors)
            { writer.Write(descriptor.Width); writer.Write(descriptor.Height); writer.Write(descriptor.ThirdU16); writer.Write(descriptor.TrailingByte); }
            writer.Write(conversion); writer.Write((byte)0); writer.Write((byte)0);
            for (var mip = 0; mip < lengths.Length; mip++) writer.Write((uint)(lengths[mip] + (defect == "table" && mip == 1 ? 1 : 0)));
            writer.Write(2229u); writer.Write(raw.Id);
            writer.Write((uint)((packedMips.Sum(mip => mip.Length) + 255) & ~255) + (defect == "following" ? 128u : 0u));
            if (!packed)
                for (var mip = 0; mip < 2; mip++) { writer.Write(2230u); writer.Write((uint)mip + 11); writer.Write((uint)lengths[mip]); }
        }
        var encoded = encodedStream.ToArray();
        var rows = payloads.Select((payload, ordinal) => new TextureCatalogReference(ordinal,
            ordinal == 0 ? TextureCatalogEntryKind.Raw : TextureCatalogEntryKind.RawLod,
            new(IdentifierNamespace.Archive, payload.Id.ToString(), catalog, payload.Type), ReferenceStatus.Unique,
            [payload.Identity], 0, new("generated.dat", ordinal * 9, 9), false, null)).ToArray();
        var membership = new TextureCatalogMembership(catalog, revision, 1, new("generated.dat", 0, 100), rows.Length, 0, rows);
        var metadata = new TextureFileTextV24Reader().Parse(encoded, 0, encoded.Length, container, revision,
            new(container, 0, encoded.Length), [new("generated.dat", 100, 3)], membership).Value
            ?? throw new InvalidOperationException("Generated metadata is invalid.");
        return new(raw.Identity, revision, catalog, "generated-catalog", Payload(container, 29, 2, [1, 2, 3]),
            metadata, encoded, Hash(encoded), payloads);
    }

    private static byte[] Pixels(byte format, int mip, int length)
    {
        if (format == 11) return Enumerable.Repeat((byte)200, length).ToArray();
        var bytes = new byte[length];
        if (format == 0)
            for (var offset = 0; offset < bytes.Length; offset += 4) new byte[] { 33, 22, 11, 44 }.CopyTo(bytes, offset);
        else if (format == 7)
        {
            var color = new ushort[] { 0, 0xF800, 0x07E0, 0x001F }[mip];
            var block = new byte[16]; block[0] = 200; block[1] = 100;
            TextureFixtureBuilder.SolidDxt1Block(color, 0xFFFF, 0).CopyTo(block, 8);
            for (var offset = 0; offset < bytes.Length; offset += 16) block.CopyTo(bytes, offset);
        }
        else if (format == 18)
        {
            var block = new byte[] { 200, 100, 0, 0, 0, 0, 0, 0 };
            for (var offset = 0; offset < bytes.Length; offset += 8) block.CopyTo(bytes, offset);
        }
        else
        {
            var color = new ushort[] { 0, 0xF800, 0x07E0, 0x001F }[mip];
            var block = TextureFixtureBuilder.SolidDxt1Block(color, 0xFFFF, mip == 0 ? uint.MaxValue : 0);
            for (var offset = 0; offset < bytes.Length; offset += 8) block.CopyTo(bytes, offset);
        }
        return bytes;
    }

    private static NativeTexturePayload Payload(ResourceIdentity identity, uint type, uint id, byte[] bytes)
    { var hash = Hash(bytes); return new(identity, type, id, new("generated.dat", 0, bytes.Length, hash), bytes, hash); }

    private static byte[] Wrap(byte[] decoded, bool raw, bool suffix = false)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true)) zlib.Write(decoded);
        var block = compressed.ToArray(); var segs = new byte[32 + block.Length + (suffix ? 1 : 0)];
        "segs"u8.CopyTo(segs); BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(6), 1); BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(8), (uint)decoded.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(12), (uint)block.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(16), (ushort)block.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(18), (ushort)decoded.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(20), 1); block.CopyTo(segs, 32);
        var wrapped = new byte[32 + segs.Length];
        (raw ? "QZIP\0ETF_RAW_"u8 : "QZIP\0ETF_RAWL"u8).CopyTo(wrapped);
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(21), (uint)segs.Length); segs.CopyTo(wrapped, 32);
        return wrapped;
    }

    internal static byte[] WrapLargeRaw(byte[] decoded)
    {
        const int maximumDecodedChunkBytes = 64 * 1024;
        var chunks = new List<byte[]>();
        for (var offset = 0; offset < decoded.Length; offset += maximumDecodedChunkBytes)
        {
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(decoded, offset, Math.Min(maximumDecodedChunkBytes, decoded.Length - offset));
            chunks.Add(compressed.ToArray());
        }

        var tableEnd = 16 + chunks.Count * 8;
        var dataStart = (tableEnd + 15) & ~15;
        var offsets = new int[chunks.Count];
        var spans = new int[chunks.Count];
        var compressedSpan = 0;
        for (var index = 0; index < chunks.Count; index++)
        {
            if ((compressedSpan & 1) != 0) compressedSpan++;
            offsets[index] = compressedSpan;
            spans[index] = chunks[index].Length;
            compressedSpan += chunks[index].Length;
        }

        var segs = new byte[dataStart + compressedSpan];
        "segs"u8.CopyTo(segs);
        BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(6), checked((ushort)chunks.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(8), checked((uint)decoded.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(12), checked((uint)compressedSpan));
        for (var index = 0; index < chunks.Count; index++)
        {
            var entry = 16 + index * 8;
            BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(entry), checked((ushort)spans[index]));
            var decodedLength = Math.Min(maximumDecodedChunkBytes, decoded.Length - index * maximumDecodedChunkBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(segs.AsSpan(entry + 2),
                decodedLength == maximumDecodedChunkBytes ? (ushort)0 : checked((ushort)decodedLength));
            BinaryPrimitives.WriteUInt32LittleEndian(segs.AsSpan(entry + 4), checked((uint)offsets[index]) | 1u);
            chunks[index].CopyTo(segs, dataStart + offsets[index]);
        }

        var wrapped = new byte[32 + segs.Length];
        "QZIP\0ETF_RAW_"u8.CopyTo(wrapped);
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(21), checked((uint)segs.Length));
        segs.CopyTo(wrapped, 32);
        return wrapped;
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
