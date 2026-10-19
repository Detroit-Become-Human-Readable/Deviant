using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Deviant.Browser;
using Deviant.Export.Models;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class NativeScalarSrgbTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawScalarSrgbPreservesRedAndDefaultChannelsAcrossFullAndSelectedMips(bool packed)
    {
        var source = NativeMipFixture.Create(format: 11, packed: packed, srgb: true);
        var decoder = new NativeTextureDecoder();
        var full = decoder.ReadDetailed(source);
        Assert.True(full.Document.Info.IsSrgb); Assert.Equal(TextureStorageFormat.Rgba8, full.Document.Info.Format);
        Assert.Equal(29u, BinaryPrimitives.ReadUInt32LittleEndian(full.Document.OriginalBytes.Span[128..]));
        Assert.Equal(4, full.Document.Info.MipCount);
        for (int mip = 0; mip < 4; mip++) AssertPixels(new DdsTextureDecoder().DecodeMip(full.Document, mip), 200);
        foreach (int dimension in new[] { 8, 4, 2, 1 })
        {
            var plan = NativeTextureDecoder.PlanPreview(source with { Payloads = [] }, dimension);
            Assert.Equal((long)plan.Width * plan.Height, plan.SelectedEncodedBytes);
            var selected = source with { Payloads = source.Payloads.Where(payload => plan.RequiredPayloadIdentities.Contains(payload.Identity)).ToArray() };
            var preview = decoder.ReadPreview(selected, dimension);
            Assert.True(preview.Document.Info.IsSrgb); Assert.Equal(TextureStorageFormat.Rgba8, preview.Document.Info.Format);
            Assert.Equal(full.SupportedMips[plan.MipIndex].MipExtent, Assert.Single(preview.SupportedMips).MipExtent);
            Assert.Equal(full.SupportedMips[plan.MipIndex].DecodedSha256, preview.SupportedMips[0].DecodedSha256);
            AssertPixels(new DdsTextureDecoder().DecodeMip(preview.Document), 200);
        }
    }

    [Fact]
    public void ConvertedScalarSrgbPreservesEncodedRedAndOpaqueAlpha()
    {
        var original = ConvertedTextureFixture.Source(soleRaw: true, selector: 15);
        var bytes = original.MetadataBytes.ToArray(); bytes[26] = 1;
        var prefix = original.Metadata.RawFixedPrefix84.ToArray(); prefix[14] = 1;
        var source = original with { Metadata = original.Metadata with { RawFixedPrefix84 = prefix },
            MetadataBytes = bytes, MetadataSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
        var result = new NativeTextureDecoder().ReadPreview(source, 32);
        Assert.True(result.Document.Info.IsSrgb); Assert.Equal(TextureStorageFormat.Rgba8, result.Document.Info.Format);
        Assert.Equal(29u, BinaryPrimitives.ReadUInt32LittleEndian(result.Document.OriginalBytes.Span[128..]));
        AssertPixels(new DdsTextureDecoder().DecodeMip(result.Document), 64);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EveryGeneratedFaceAndMipPreservesEncodedSourceValues(bool cube, bool split)
    {
        var fixture = NativeScalarSrgbFixture.Create(cube, split); var source = fixture.Source;
        var metadataHash = SHA256.HashData(source.MetadataBytes.Span);
        var payloadHashes = source.Payloads.Select(payload => SHA256.HashData(payload.OriginalBytes.Span)).ToArray();
        var full = new NativeTextureDecoder().ReadDetailed(source);
        Assert.Equal(5, full.Document.Info.MipCount); Assert.True(full.Document.Info.IsSrgb);
        for (int face = 0; face < fixture.Planes.Length; face++)
        for (int mip = 0; mip < 5; mip++)
        {
            var selection = new TextureSubresourceSelection(Face: cube ? (CubeFace)face : null, MipIndex: mip);
            var pixels = new DdsTextureDecoder().DecodeSubresource(full.Document, selection);
            AssertRedPlane(pixels, fixture.Planes[face][mip]);
        }
        foreach (int dimension in new[] { 16, 8, 4, 2, 1 })
        {
            var plan = NativeTextureDecoder.PlanPreview(source with { Payloads = [] }, dimension);
            Assert.Equal((long)plan.Width * plan.Height * fixture.Planes.Length, plan.SelectedEncodedBytes);
            Assert.Equal(plan.SelectedEncodedBytes * 4, plan.OutputEncodedBytes);
            var selected = source with { Payloads = source.Payloads.Where(payload => plan.RequiredPayloadIdentities.Contains(payload.Identity)).ToArray() };
            var preview = new NativeTextureDecoder().ReadPreview(selected, dimension);
            Assert.Equal(1, preview.Document.Info.MipCount); Assert.True(preview.Document.Info.IsSrgb);
            for (int face = 0; face < fixture.Planes.Length; face++)
                AssertRedPlane(new DdsTextureDecoder().DecodeSubresource(preview.Document,
                    new(Face: cube ? (CubeFace)face : null)), fixture.Planes[face][plan.MipIndex]);
        }
        Assert.Equal(metadataHash, SHA256.HashData(source.MetadataBytes.Span));
        for (int index = 0; index < source.Payloads.Count; index++) Assert.Equal(payloadHashes[index], SHA256.HashData(source.Payloads[index].OriginalBytes.Span));
    }

    [Fact]
    public void SoftwareSamplingDecodesSrgbBeforeInterpolatingAndKeepsMissingChannels()
    {
        var fixture = NativeScalarSrgbFixture.Create(alternating: true);
        var document = new NativeTextureDecoder().Read(fixture.Source);
        var surface = new DdsTextureDecoder().DecodeMip(document);
        var linear = NativeTextureSamplingDecoder.Decode((1ul << 19) | (1ul << 23));
        var point = NativeTextureSamplingDecoder.Decode(0);
        var filtered = MaterialSurfaceMapBaker.Sample(new(surface, linear), new(1f / 16, .5f / 16));
        Assert.InRange(MathF.Abs(filtered.X - .5f), 0, 1e-6f);
        Assert.Equal(new Vector3(0, 0, 1), new Vector3(filtered.Y, filtered.Z, filtered.W));
        float incorrectlyDecodedAfterFiltering = Transfer(.5f);
        Assert.True(MathF.Abs(filtered.X - incorrectlyDecodedAfterFiltering) > .2f);
        var ramp = NativeScalarSrgbFixture.Create();
        var rampSurface = new DdsTextureDecoder().DecodeMip(new NativeTextureDecoder().Read(ramp.Source));
        for (int code = 0; code < 256; code++)
        {
            var sample = MaterialSurfaceMapBaker.Sample(new(rampSurface, point), new((code % 16 + .5f) / 16, (code / 16 + .5f) / 16));
            Assert.InRange(MathF.Abs(sample.X - Transfer(code / 255f)), 0, 1e-6f);
            Assert.Equal(0, sample.Y); Assert.Equal(0, sample.Z); Assert.Equal(1, sample.W);
        }
    }

    [Fact]
    public void DerivedRgbaAdmissionDoesNotReuseTheSmallerSourceByteAllowance()
    {
        var scalar = NativeScalarSrgbFixture.Create().Source;
        var linear = NativeScalarSrgbFixture.Create(srgb: false).Source;
        var plan = NativeTextureDecoder.PlanPreview(scalar, 16);
        Assert.Equal(256, plan.SelectedEncodedBytes); Assert.Equal(1024, plan.OutputEncodedBytes);
        var limited = new NativeTextureDecoder(512);
        Assert.Equal(TextureStorageFormat.Luminance8, limited.ReadPreview(linear, 16).Document.Info.Format);
        Assert.Equal("NativeTextureDecodeBudget", Assert.Throws<TextureFormatException>(() => limited.EstimatePreviewWorkingSetBytes(scalar, 16)).Code);
        Assert.Equal("NativeTextureDecodeBudget", Assert.Throws<TextureFormatException>(() => limited.ReadPreview(scalar, 16)).Code);
        Assert.Equal("NativeTextureDecodeBudget", Assert.Throws<TextureFormatException>(() => limited.EstimateWorkingSetBytes(scalar)).Code);
        Assert.Equal("NativeTextureDecodeBudget", Assert.Throws<TextureFormatException>(() => limited.Read(scalar)).Code);
        Assert.True(new NativeTextureDecoder().EstimatePreviewWorkingSetBytes(scalar, 16) > new NativeTextureDecoder().EstimatePreviewWorkingSetBytes(linear, 16));
    }

    [Fact]
    public void LinearScalarProfileAndIntegrityCancellationGuardsRemainUnchanged()
    {
        var linear = NativeScalarSrgbFixture.Create(srgb: false).Source;
        var document = new NativeTextureDecoder().Read(linear);
        Assert.Equal(61u, BinaryPrimitives.ReadUInt32LittleEndian(document.OriginalBytes.Span[128..])); Assert.False(document.Info.IsSrgb);
        Assert.Equal("native-texture-v12/" + ConvertedTextureDecoder.Version, NativeTextureDecoder.Version);
        var source = NativeScalarSrgbFixture.Create().Source;
        var payload = source.Payloads[0]; var corrupt = payload.OriginalBytes.ToArray(); corrupt[^1] ^= 1;
        Assert.Equal("NativeTextureSourceMismatch", Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder().Read(source with
            { Payloads = [payload with { OriginalBytes = corrupt }] })).Code);
        Assert.Equal("NativeTextureMetadataMismatch", Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder().Read(source with
            { MetadataSha256 = new string('0', 64) })).Code);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new NativeTextureDecoder().Read(source, canceled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => new NativeTextureDecoder().ReadPreview(source, 16, canceled.Token));
    }

    [Fact]
    public void LargePackedScalarPreviewKeepsRangeSelectionAndAdmitsItsExpandedOutput()
    {
        var (original, red) = NativeMipFixture.CreateLargeInlinePackage();
        var bytes = original.MetadataBytes.ToArray(); bytes[26] = 1;
        var prefix = original.Metadata.RawFixedPrefix84.ToArray(); prefix[14] = 1;
        var source = original with { Metadata = original.Metadata with { RawFixedPrefix84 = prefix },
            MetadataBytes = bytes, MetadataSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
        var plan = NativeTextureDecoder.PlanPreview(source with { Payloads = [] }, 1024);
        Assert.True(plan.PackageDecodedBytes > TextureBufferPatternInspector.MaximumDecodedBytes);
        Assert.Equal(red.Length, plan.SelectedEncodedBytes); Assert.Equal(4L * red.Length, plan.OutputEncodedBytes);
        var decoded = new NativeTextureDecoder().ReadPreview(source, 1024);
        var evidence = Assert.Single(decoded.SupportedMips);
        Assert.Equal(2, evidence.MipIndex); Assert.Equal(Convert.ToHexString(SHA256.HashData(red)).ToLowerInvariant(), evidence.DecodedSha256);
        var pixels = new DdsTextureDecoder().DecodeMip(decoded.Document);
        var expected = new byte[red.Length * 4];
        for (int index = 0; index < red.Length; index++) { expected[index * 4] = red[index]; expected[index * 4 + 3] = 255; }
        Assert.True(pixels.IsSrgb); Assert.Equal(expected, pixels.Rgba8.ToArray());
    }

    [Theory]
    [InlineData(29)]
    [InlineData(30)]
    public void OtherScalarNativeIdentifiersKeepTheirExistingAdmission(byte format)
    {
        var original = NativeMipFixture.Create(format: 11, packed: true, srgb: true);
        var bytes = original.MetadataBytes.ToArray(); bytes[12] = format;
        var prefix = original.Metadata.RawFixedPrefix84.ToArray(); prefix[0] = format;
        var source = original with { Metadata = original.Metadata with { FirstByte = format, RawFixedPrefix84 = prefix },
            MetadataBytes = bytes, MetadataSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
        Assert.Equal("UnsupportedNativeTextureFormat", Assert.Throws<TextureFormatException>(() => new NativeTextureDecoder().Read(source)).Code);
    }

    private static float Transfer(float encoded) => encoded <= .04045f ? encoded / 12.92f : MathF.Pow((encoded + .055f) / 1.055f, 2.4f);
    private static void AssertRedPlane(TextureSurface surface, byte[] red)
    {
        Assert.True(surface.IsSrgb); Assert.Equal(red.Length * 4, surface.Rgba8.Length);
        for (int index = 0; index < red.Length; index++) Assert.Equal(new byte[] { red[index], 0, 0, 255 }, surface.Rgba8.Slice(index * 4, 4).ToArray());
    }

    private static void AssertPixels(TextureSurface surface, byte red)
    {
        Assert.True(surface.IsSrgb);
        for (int index = 0; index < surface.Rgba8.Length; index += 4)
            Assert.Equal(new byte[] { red, 0, 0, 255 }, surface.Rgba8.Slice(index, 4).ToArray());
    }
}
