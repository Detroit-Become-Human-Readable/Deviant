using Deviant.Formats.Textures;
using Xunit;

namespace Deviant.Texture.Tests;

public sealed class NativeTextureSamplingDecoderTests
{
    [Fact]
    public void SelectorOneUsesLinearTexelsNearestMipAndZeroMaxLod()
    {
        var profile = NativeTextureSamplingDecoder.Decode(Compose(selectorA: 1, selectorB: 1), 1);

        Assert.Equal(NativeTextureTexelFilter.Linear, profile.TexelFilter);
        Assert.Equal(NativeTextureMipFilter.Nearest, profile.MipFilter);
        Assert.Equal((byte)0, profile.MaximumMipLevel);
        Assert.Equal(1, profile.MaximumAnisotropy);
        Assert.False(profile.AnisotropyEnabled);
    }

    [Theory]
    [InlineData(0, NativeTextureTexelFilter.Point, NativeTextureMipFilter.Nearest, 0, 0)]
    [InlineData(1, NativeTextureTexelFilter.Linear, NativeTextureMipFilter.Nearest, 0, 0)]
    [InlineData(2, NativeTextureTexelFilter.Linear, NativeTextureMipFilter.Linear, 0, 0)]
    [InlineData(3, NativeTextureTexelFilter.Linear, NativeTextureMipFilter.Linear, 0, 2)]
    [InlineData(4, NativeTextureTexelFilter.Linear, NativeTextureMipFilter.Linear, 0, 4)]
    [InlineData(5, NativeTextureTexelFilter.Linear, NativeTextureMipFilter.Linear, 0, 8)]
    [InlineData(6, NativeTextureTexelFilter.Linear, NativeTextureMipFilter.Linear, 0, 16)]
    [InlineData(7, NativeTextureTexelFilter.Point, NativeTextureMipFilter.Nearest, 0, 0)]
    [InlineData(8, NativeTextureTexelFilter.Linear, NativeTextureMipFilter.Nearest, 0, 0)]
    [InlineData(15, NativeTextureTexelFilter.Point, NativeTextureMipFilter.Nearest, 0, 0)]
    public void DecodesEverySelectorBranch(int selector, NativeTextureTexelFilter texelFilter,
        NativeTextureMipFilter mipFilter, byte maximumMipLevel, int anisotropy)
    {
        var profile = NativeTextureSamplingDecoder.Decode(Compose(selectorA: selector, selectorB: selector), 1);

        Assert.Equal((byte)selector, profile.SelectedSelector);
        Assert.Equal(texelFilter, profile.TexelFilter);
        Assert.Equal(mipFilter, profile.MipFilter);
        Assert.Equal(maximumMipLevel, profile.MaximumMipLevel);
        Assert.Equal(anisotropy == 0 ? 1 : anisotropy, profile.MaximumAnisotropy);
        Assert.Equal(anisotropy != 0, profile.AnisotropyEnabled);
    }

    [Theory]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 13)] [InlineData(2, 31)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 13)] [InlineData(3, 31)]
    [InlineData(4, 0)] [InlineData(4, 1)] [InlineData(4, 13)] [InlineData(4, 31)]
    [InlineData(5, 0)] [InlineData(5, 1)] [InlineData(5, 13)] [InlineData(5, 31)]
    [InlineData(6, 0)] [InlineData(6, 1)] [InlineData(6, 13)] [InlineData(6, 31)]
    [InlineData(7, 0)] [InlineData(7, 1)] [InlineData(7, 13)] [InlineData(7, 31)]
    [InlineData(8, 0)] [InlineData(8, 1)] [InlineData(8, 13)] [InlineData(8, 31)]
    public void EnabledMipSelectorsReadTheUpperMipLimit(int selector, int upperMipLimit)
    {
        var profile = NativeTextureSamplingDecoder.Decode(Compose(selectorA: selector, selectorB: selector,
            rawMaximumMipLevel: upperMipLimit), 1);

        Assert.Equal((byte)upperMipLimit, profile.RawMaximumMipLevel);
        Assert.Equal((byte)upperMipLimit, profile.MaximumMipLevel);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 13)] [InlineData(0, 31)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 13)] [InlineData(1, 31)]
    [InlineData(15, 0)] [InlineData(15, 1)] [InlineData(15, 13)] [InlineData(15, 31)]
    public void NonMipSelectorsKeepTheirZeroMaximumMipClamp(int selector, int upperMipLimit)
    {
        var profile = NativeTextureSamplingDecoder.Decode(Compose(selectorA: selector, selectorB: selector,
            rawMaximumMipLevel: upperMipLimit), 1);

        Assert.Equal((byte)upperMipLimit, profile.RawMaximumMipLevel);
        Assert.Equal((byte)0, profile.MaximumMipLevel);
    }

    [Theory]
    [InlineData(0, 7, 0, NativeTextureTexelFilter.Point, NativeTextureMipFilter.Nearest, false, 1)]
    [InlineData(2, 6, 2, NativeTextureTexelFilter.Linear, NativeTextureMipFilter.Linear, false, 1)]
    [InlineData(2, 12, 2, NativeTextureTexelFilter.Linear, NativeTextureMipFilter.Linear, false, 1)]
    [InlineData(3, 7, 3, NativeTextureTexelFilter.Linear, NativeTextureMipFilter.Linear, true, 2)]
    [InlineData(9, 3, 3, NativeTextureTexelFilter.Linear, NativeTextureMipFilter.Linear, true, 2)]
    [InlineData(14, 15, 14, NativeTextureTexelFilter.Point, NativeTextureMipFilter.Nearest, false, 1)]
    public void SelectsTheSmallerRawSelectorAndUsesItsNativeBranch(int first, int second, int selected,
        NativeTextureTexelFilter expectedTexelFilter, NativeTextureMipFilter expectedMipFilter,
        bool expectedAnisotropyEnabled, int expectedMaximumAnisotropy)
    {
        const uint version = NativeTextureSamplingDecoder.CurrentProfileVersion;
        var rawWord = Compose(selectorA: first, selectorB: second);

        var profile = NativeTextureSamplingDecoder.Decode(rawWord, version);

        Assert.Equal(version, profile.ProfileVersion);
        Assert.Equal(rawWord, profile.RawWord);
        Assert.Equal((byte)first, profile.RawSelectorA);
        Assert.Equal((byte)second, profile.RawSelectorB);
        Assert.Equal((byte)selected, profile.SelectedSelector);
        Assert.Equal(expectedTexelFilter, profile.TexelFilter);
        Assert.Equal(expectedMipFilter, profile.MipFilter);
        Assert.Equal(expectedAnisotropyEnabled, profile.AnisotropyEnabled);
        Assert.Equal(expectedMaximumAnisotropy, profile.MaximumAnisotropy);
    }

    [Theory]
    [InlineData(0, NativeTextureAddressMode.Repeat)]
    [InlineData(1, NativeTextureAddressMode.ClampToEdge)]
    [InlineData(2, NativeTextureAddressMode.MirroredRepeat)]
    [InlineData(3, NativeTextureAddressMode.ClampToBorder)]
    [InlineData(4, NativeTextureAddressMode.MirroredRepeat)]
    [InlineData(5, NativeTextureAddressMode.Repeat)]
    [InlineData(6, NativeTextureAddressMode.Repeat)]
    [InlineData(7, NativeTextureAddressMode.ClampToEdge)]
    public void MapsAddressIndexAndPreservesIt(int index, NativeTextureAddressMode expected)
    {
        var profile = NativeTextureSamplingDecoder.Decode(Compose(addressU: index, addressV: index,
            addressW: index), 1);

        Assert.Equal((byte)index, profile.RawAddressUIndex);
        Assert.Equal((byte)index, profile.RawAddressVIndex);
        Assert.Equal((byte)index, profile.RawAddressWIndex);
        Assert.Equal(expected, profile.AddressU);
        Assert.Equal(expected, profile.AddressV);
        Assert.Equal(expected, profile.AddressW);
    }

    [Theory]
    [InlineData(0, NativeTextureCompareOperation.Never)]
    [InlineData(1, NativeTextureCompareOperation.Less)]
    [InlineData(2, NativeTextureCompareOperation.Equal)]
    [InlineData(3, NativeTextureCompareOperation.LessOrEqual)]
    [InlineData(4, NativeTextureCompareOperation.Greater)]
    [InlineData(5, NativeTextureCompareOperation.NotEqual)]
    [InlineData(6, NativeTextureCompareOperation.GreaterOrEqual)]
    [InlineData(7, NativeTextureCompareOperation.Always)]
    public void PreservesComparisonIndexAndEnableSeparately(int index, NativeTextureCompareOperation expected)
    {
        var profile = NativeTextureSamplingDecoder.Decode(Compose(compareEnabled: true,
            compareOperation: index), 1);

        Assert.True(profile.ComparisonEnabled);
        Assert.Equal((byte)index, profile.RawComparisonOperationIndex);
        Assert.Equal(expected, profile.ComparisonOperation);
    }

    [Fact]
    public void RetainsComparisonOperationWhenComparisonIsDisabled()
    {
        var profile = NativeTextureSamplingDecoder.Decode(Compose(compareEnabled: false,
            compareOperation: 6), 1);

        Assert.False(profile.ComparisonEnabled);
        Assert.Equal((byte)6, profile.RawComparisonOperationIndex);
        Assert.Equal(NativeTextureCompareOperation.GreaterOrEqual, profile.ComparisonOperation);
    }

    [Theory]
    [InlineData(0, NativeTextureBorderColor.TransparentBlack)]
    [InlineData(1, NativeTextureBorderColor.OpaqueBlack)]
    [InlineData(2, NativeTextureBorderColor.OpaqueWhite)]
    [InlineData(3, NativeTextureBorderColor.TransparentBlack)]
    public void PreservesBorderCodeAndMapsColor(int index, NativeTextureBorderColor expected)
    {
        var profile = NativeTextureSamplingDecoder.Decode(Compose(borderColor: index), 1);

        Assert.Equal((byte)index, profile.RawBorderColorIndex);
        Assert.Equal(expected, profile.BorderColor);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(16383, 16383)]
    [InlineData(16384, -16384)]
    [InlineData(32767, -1)]
    [InlineData(32768, 0)]
    [InlineData(65535, -1)]
    public void DecodesSignedBiasUnitsAndIgnoresHighBiasBit(int rawBiasWord, int expectedUnits)
    {
        var profile = NativeTextureSamplingDecoder.Decode(Compose(biasWord: (ushort)rawBiasWord), 1);

        Assert.Equal((ushort)rawBiasWord, profile.RawBiasWord);
        Assert.Equal((short)expectedUnits, profile.BiasUnits256);
        Assert.Equal(expectedUnits / 256f, profile.MipLodBias);
    }

    [Fact]
    public void UsesNormalizedCoordinatesAndZeroMinimumLod()
    {
        var profile = NativeTextureSamplingDecoder.Decode(Compose(), 1);

        Assert.True(profile.NormalizedCoordinates);
        Assert.Equal(0f, profile.MinimumLod);
    }

    [Fact]
    public void RejectsAnUnsupportedConsumerProfileVersion()
    {
        Assert.Throws<NotSupportedException>(() => NativeTextureSamplingDecoder.Decode(0, 2));
        Assert.Throws<NotSupportedException>(() => NativeTextureSamplingDecoder.Decode(0, 37));
    }

    private static ulong Compose(int selectorA = 0, int selectorB = 0,
        int addressU = 0, int addressV = 0, int addressW = 0,
        bool compareEnabled = false, int compareOperation = 0,
        int borderColor = 0, ushort biasWord = 0, int rawMaximumMipLevel = 0)
    {
        if ((uint)selectorA > 15 || (uint)selectorB > 15 ||
            (uint)addressU > 7 || (uint)addressV > 7 || (uint)addressW > 7 ||
            (uint)compareOperation > 7 || (uint)borderColor > 3 || (uint)rawMaximumMipLevel > 31)
            throw new ArgumentOutOfRangeException(nameof(selectorA));
        return biasWord |
            ((ulong)selectorA << 19) | ((ulong)selectorB << 23) |
            ((ulong)addressU << 27) | ((ulong)addressV << 30) | ((ulong)addressW << 33) |
            (compareEnabled ? 1UL << 36 : 0) | ((ulong)compareOperation << 16) |
            ((ulong)borderColor << 53) | ((ulong)rawMaximumMipLevel << 56);
    }
}
