namespace Deviant.Formats.Textures;

/// <summary>Decodes the native 64-bit sampler key into API-neutral sampling settings.</summary>
public static class NativeTextureSamplingDecoder
{
    /// <summary>Version of this consumer-side decoding contract, not a serialized texture version.</summary>
    public const uint CurrentProfileVersion = 1;

    public static NativeTextureSamplingProfile Decode(ulong rawWord,
        uint profileVersion = CurrentProfileVersion)
    {
        if (profileVersion != CurrentProfileVersion)
            throw new NotSupportedException($"Native texture sampling profile version {profileVersion} is unsupported.");

        var rawSelectorA = (byte)((rawWord >> 19) & 0xF);
        var rawSelectorB = (byte)((rawWord >> 23) & 0xF);
        var selector = Math.Min(rawSelectorA, rawSelectorB);
        var rawMaximumMipLevel = (byte)((rawWord >> 56) & 0x1F);

        var texelFilter = NativeTextureTexelFilter.Point;
        var mipFilter = NativeTextureMipFilter.Nearest;
        var maximumAnisotropy = 1;
        var anisotropyEnabled = false;
        byte maximumMipLevel = 0;

        switch (selector)
        {
            case 0:
                break;
            case 1:
                texelFilter = NativeTextureTexelFilter.Linear;
                break;
            case 2:
                texelFilter = NativeTextureTexelFilter.Linear;
                mipFilter = NativeTextureMipFilter.Linear;
                maximumMipLevel = rawMaximumMipLevel;
                break;
            case 3:
            case 4:
            case 5:
            case 6:
                texelFilter = NativeTextureTexelFilter.Linear;
                mipFilter = NativeTextureMipFilter.Linear;
                maximumAnisotropy = 1 << (selector - 2);
                anisotropyEnabled = true;
                maximumMipLevel = rawMaximumMipLevel;
                break;
            case 7:
                maximumMipLevel = rawMaximumMipLevel;
                break;
            case 8:
                texelFilter = NativeTextureTexelFilter.Linear;
                maximumMipLevel = rawMaximumMipLevel;
                break;
            default:
                // The native fallback uses point sampling, nearest mip mode, and max LOD 0.
                break;
        }

        var addressUIndex = (byte)((rawWord >> 27) & 0x7);
        var addressVIndex = (byte)((rawWord >> 30) & 0x7);
        var addressWIndex = (byte)((rawWord >> 33) & 0x7);
        var comparisonIndex = (byte)((rawWord >> 16) & 0x7);
        var borderIndex = (byte)((rawWord >> 53) & 0x3);
        var rawBiasWord = (ushort)rawWord;
        var biasUnits = (rawBiasWord & 0x4000) == 0
            ? (int)(rawBiasWord & 0x3FFF)
            : (int)(rawBiasWord & 0x3FFF) - 16384;

        return new NativeTextureSamplingProfile(
            profileVersion,
            rawWord,
            rawSelectorA,
            rawSelectorB,
            selector,
            rawMaximumMipLevel,
            texelFilter,
            mipFilter,
            maximumAnisotropy,
            anisotropyEnabled,
            maximumMipLevel,
            addressUIndex,
            addressVIndex,
            addressWIndex,
            DecodeAddress(addressUIndex),
            DecodeAddress(addressVIndex),
            DecodeAddress(addressWIndex),
            ((rawWord >> 36) & 1) != 0,
            comparisonIndex,
            (NativeTextureCompareOperation)comparisonIndex,
            borderIndex,
            DecodeBorderColor(borderIndex),
            rawBiasWord,
            checked((short)biasUnits),
            biasUnits / 256f,
            true,
            0f);
    }

    private static NativeTextureAddressMode DecodeAddress(byte index) => index switch
    {
        0 or 5 or 6 => NativeTextureAddressMode.Repeat,
        1 or 7 => NativeTextureAddressMode.ClampToEdge,
        2 or 4 => NativeTextureAddressMode.MirroredRepeat,
        3 => NativeTextureAddressMode.ClampToBorder,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private static NativeTextureBorderColor DecodeBorderColor(byte index) => index switch
    {
        0 or 3 => NativeTextureBorderColor.TransparentBlack,
        1 => NativeTextureBorderColor.OpaqueBlack,
        2 => NativeTextureBorderColor.OpaqueWhite,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
}
