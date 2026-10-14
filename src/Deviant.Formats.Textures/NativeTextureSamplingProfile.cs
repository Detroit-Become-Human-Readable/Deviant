namespace Deviant.Formats.Textures;

public enum NativeTextureTexelFilter : byte
{
    Point,
    Linear
}

public enum NativeTextureMipFilter : byte
{
    Nearest,
    Linear
}

public enum NativeTextureAddressMode : byte
{
    Repeat,
    MirroredRepeat,
    ClampToEdge,
    ClampToBorder
}

public enum NativeTextureCompareOperation : byte
{
    Never = 0,
    Less = 1,
    Equal = 2,
    LessOrEqual = 3,
    Greater = 4,
    NotEqual = 5,
    GreaterOrEqual = 6,
    Always = 7
}

public enum NativeTextureBorderColor : byte
{
    TransparentBlack,
    OpaqueBlack,
    OpaqueWhite
}

/// <summary>Generic decoded values and retained source bits for one native sampler key.</summary>
public sealed record NativeTextureSamplingProfile(
    uint ProfileVersion,
    ulong RawWord,
    byte RawSelectorA,
    byte RawSelectorB,
    byte SelectedSelector,
    byte RawMaximumMipLevel,
    NativeTextureTexelFilter TexelFilter,
    NativeTextureMipFilter MipFilter,
    int MaximumAnisotropy,
    bool AnisotropyEnabled,
    byte MaximumMipLevel,
    byte RawAddressUIndex,
    byte RawAddressVIndex,
    byte RawAddressWIndex,
    NativeTextureAddressMode AddressU,
    NativeTextureAddressMode AddressV,
    NativeTextureAddressMode AddressW,
    bool ComparisonEnabled,
    byte RawComparisonOperationIndex,
    NativeTextureCompareOperation ComparisonOperation,
    byte RawBorderColorIndex,
    NativeTextureBorderColor BorderColor,
    ushort RawBiasWord,
    short BiasUnits256,
    float MipLodBias,
    bool NormalizedCoordinates,
    float MinimumLod);
