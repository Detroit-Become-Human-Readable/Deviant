using System.Buffers.Binary;
using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class TextureFileTextSamplingReaderTests
{
    [Theory]
    [InlineData(4, 4)]
    [InlineData(1, 1)]
    public void PreservesSourceFilterAddressesAndCreationDefaults(int mips, int expectedFilter)
    {
        var metadata = NativeMipFixture.Create().Metadata;
        var prefix = metadata.RawFixedPrefix84.ToArray();
        prefix[7] = (byte)mips; prefix[8] = 4; prefix[9] = 1; prefix[10] = 3; prefix[11] = 2;
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteSingleLittleEndian(prefix.AsSpan(68 + i * 4), 1);
        var result = TextureFileTextSamplingReader.Read(metadata with { RawFixedPrefix84 = prefix, MipSizeCount = (byte)mips });
        Assert.Equal(expectedFilter, result.RawSelectorA);
        Assert.Equal(6, result.RawSelectorB);
        Assert.Equal(NativeTextureAddressMode.ClampToEdge, result.AddressU);
        Assert.Equal(NativeTextureAddressMode.ClampToBorder, result.AddressV);
        Assert.Equal(NativeTextureAddressMode.MirroredRepeat, result.AddressW);
        Assert.Equal(NativeTextureBorderColor.OpaqueWhite, result.BorderColor);
        Assert.False(result.ComparisonEnabled); Assert.Equal(0, result.MipLodBias);
    }

    [Theory]
    [InlineData(0, 0, 0, 1, NativeTextureBorderColor.OpaqueBlack)]
    [InlineData(1, 1, 1, 1, NativeTextureBorderColor.OpaqueWhite)]
    [InlineData(1, 0, 0, 0, NativeTextureBorderColor.TransparentBlack)]
    public void UsesTheNativeBorderColourSelection(float r, float g, float b, float a, NativeTextureBorderColor expected)
    {
        var metadata = NativeMipFixture.Create().Metadata;
        var prefix = metadata.RawFixedPrefix84.ToArray(); var values = new[] { r, g, b, a };
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteSingleLittleEndian(prefix.AsSpan(68 + i * 4), values[i]);
        Assert.Equal(expected, TextureFileTextSamplingReader.Read(metadata with { RawFixedPrefix84 = prefix }).BorderColor);
    }

    [Fact]
    public void RejectsInvalidSourceFieldsAndNonfiniteBorderValues()
    {
        var metadata = NativeMipFixture.Create().Metadata;
        var prefix = metadata.RawFixedPrefix84.ToArray(); prefix[9] = 8;
        Assert.Throws<TextureFormatException>(() => TextureFileTextSamplingReader.Read(metadata with { RawFixedPrefix84 = prefix }));
        prefix[9] = 0; BinaryPrimitives.WriteSingleLittleEndian(prefix.AsSpan(68), float.NaN);
        Assert.Throws<TextureFormatException>(() => TextureFileTextSamplingReader.Read(metadata with { RawFixedPrefix84 = prefix }));
    }
}
