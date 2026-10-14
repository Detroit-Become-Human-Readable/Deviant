using Deviant.Assets;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Shaders;
using SharpDX.Direct3D11;

namespace Deviant.Desktop;

internal sealed record ViewportMaterialState(BlendStateDescription Blend,
    DepthStencilStateDescription Depth, bool IsTransparent, string Key);

internal static class ViewportMaterialStates
{
    private static readonly BlendOption[] Sources =
    [BlendOption.One, BlendOption.SourceAlpha, BlendOption.InverseSourceAlpha, BlendOption.One,
        BlendOption.One, BlendOption.One, BlendOption.One, BlendOption.Zero];
    private static readonly BlendOption[] Destinations =
    [BlendOption.Zero, BlendOption.InverseSourceAlpha, BlendOption.SourceAlpha, BlendOption.One,
        BlendOption.One, BlendOption.One, BlendOption.InverseSourceAlpha, BlendOption.Zero];
    private static readonly BlendOption[] SeparateAlphaSources =
    [BlendOption.Zero, BlendOption.Zero, BlendOption.Zero, BlendOption.Zero,
        BlendOption.One, BlendOption.One, BlendOption.Zero, BlendOption.Zero];

    public static ViewportMaterialState Create(NativeMaterialRenderParameters? source, bool separateAlpha = false)
    {
        if (source is null || source.BlendSelector >= Sources.Length)
            return new(DefaultBlendStateDescriptions.BSAlphaBlend,
                DefaultDepthStencilDescriptions.DSSDepthLessEqual, true, "default");

        int selector = source.BlendSelector;
        bool transparent = selector != 0 || separateAlpha;
        // BlendStateDescription contains an array; allocate our own targets so
        // another material cannot change this state or the shared defaults.
        var blend = new BlendStateDescription
        { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
        var target = blend.RenderTarget[0];
        target.IsBlendEnabled = transparent;
        target.SourceBlend = Sources[selector];
        target.DestinationBlend = Destinations[selector];
        target.BlendOperation = selector == 4 ? BlendOperation.Maximum : BlendOperation.Add;
        target.SourceAlphaBlend = separateAlpha ? SeparateAlphaSources[selector] : Sources[selector];
        target.DestinationAlphaBlend = Destinations[selector];
        target.AlphaBlendOperation = target.BlendOperation;
        target.RenderTargetWriteMask = ColorWriteMaskFlags.All;
        blend.RenderTarget[0] = target;

        bool opaqueDepth = selector == 0 && (source.RenderOptions & 2) == 0;
        bool depthWrite = opaqueDepth || (source.Flags & 4) != 0;
        if (source.ProgramMode == 11) depthWrite = false;
        // Native color passes use greater/equal with reversed depth. The viewport
        // projection has near depth zero, so the corresponding test is less/equal.
        var comparison = opaqueDepth || (source.Flags & 0x400) != 0
            ? Comparison.LessEqual : Comparison.Always;
        var depth = DefaultDepthStencilDescriptions.DSSDepthLessEqual;
        depth.IsDepthEnabled = true;
        depth.DepthWriteMask = depthWrite ? DepthWriteMask.All : DepthWriteMask.Zero;
        depth.DepthComparison = comparison;
        return new(blend, depth, transparent, $"{selector}-{separateAlpha}-{depthWrite}-{comparison}");
    }
}
