using Deviant.Assets;
using SharpDX.Direct3D11;
using Xunit;

namespace Deviant.Desktop.Tests;

public sealed class ViewportMaterialStateTests
{
    [Fact]
    public void OpaqueColorReplacesTheDestinationAndWritesDepth()
    {
        var state = ViewportMaterialStates.Create(Source(0));
        Assert.False(state.Blend.RenderTarget[0].IsBlendEnabled);
        Assert.False(state.IsTransparent);
        Assert.Equal(DepthWriteMask.All, state.Depth.DepthWriteMask);
        Assert.Equal(Comparison.LessEqual, state.Depth.DepthComparison);
    }

    [Fact]
    public void AlphaColorUsesCoverageForBothColorChannelsAndAlpha()
    {
        var state = ViewportMaterialStates.Create(Source(1));
        var target = state.Blend.RenderTarget[0];
        Assert.True(target.IsBlendEnabled);
        Assert.True(state.IsTransparent);
        Assert.Equal(BlendOption.SourceAlpha, target.SourceBlend);
        Assert.Equal(BlendOption.InverseSourceAlpha, target.DestinationBlend);
        Assert.Equal(target.SourceBlend, target.SourceAlphaBlend);
        Assert.Equal(target.DestinationBlend, target.DestinationAlphaBlend);
        Assert.Equal(DepthWriteMask.Zero, state.Depth.DepthWriteMask);
        Assert.Equal(Comparison.Always, state.Depth.DepthComparison);
    }

    [Fact]
    public void AdditiveColorKeepsTheExistingDestinationInsteadOfAttenuatingIt()
    {
        var state = ViewportMaterialStates.Create(Source(3, flags: 0x400));
        var target = state.Blend.RenderTarget[0];
        Assert.Equal(BlendOption.One, target.SourceBlend);
        Assert.Equal(BlendOption.One, target.DestinationBlend);
        Assert.Equal(BlendOperation.Add, target.BlendOperation);
        Assert.Equal(Comparison.LessEqual, state.Depth.DepthComparison);
        Assert.Equal(DepthWriteMask.Zero, state.Depth.DepthWriteMask);
    }

    [Fact]
    public void SeparateAlphaDoesNotChangeTheColorEquation()
    {
        var source = Source(1);
        var normal = ViewportMaterialStates.Create(source);
        var separate = ViewportMaterialStates.Create(source, separateAlpha: true);
        Assert.Equal(normal.Blend.RenderTarget[0].SourceBlend, separate.Blend.RenderTarget[0].SourceBlend);
        Assert.Equal(normal.Blend.RenderTarget[0].DestinationBlend, separate.Blend.RenderTarget[0].DestinationBlend);
        Assert.Equal(BlendOption.Zero, separate.Blend.RenderTarget[0].SourceAlphaBlend);
        Assert.NotEqual(normal.Key, separate.Key);
    }

    [Fact]
    public void MaximumAndPremultipliedColorUseDifferentEquations()
    {
        var maximum = ViewportMaterialStates.Create(Source(4));
        var premultiplied = ViewportMaterialStates.Create(Source(6));
        Assert.Equal(BlendOperation.Maximum, maximum.Blend.RenderTarget[0].BlendOperation);
        Assert.Equal(BlendOption.One, premultiplied.Blend.RenderTarget[0].SourceBlend);
        Assert.Equal(BlendOption.InverseSourceAlpha, premultiplied.Blend.RenderTarget[0].DestinationBlend);
        Assert.NotEqual(maximum.Key, premultiplied.Key);
    }

    [Fact]
    public void MaterialFlagsAndRenderOptionsPreserveIndependentDepthControls()
    {
        var writing = ViewportMaterialStates.Create(Source(1, flags: 4));
        var testing = ViewportMaterialStates.Create(Source(1, flags: 0x400));
        var explicitOpaque = ViewportMaterialStates.Create(Source(0) with { RenderOptions = 2 });
        Assert.Equal(DepthWriteMask.All, writing.Depth.DepthWriteMask);
        Assert.Equal(Comparison.Always, writing.Depth.DepthComparison);
        Assert.Equal(DepthWriteMask.Zero, testing.Depth.DepthWriteMask);
        Assert.Equal(Comparison.LessEqual, testing.Depth.DepthComparison);
        Assert.Equal(DepthWriteMask.Zero, explicitOpaque.Depth.DepthWriteMask);
        Assert.Equal(Comparison.Always, explicitOpaque.Depth.DepthComparison);
        Assert.NotEqual(writing.Key, testing.Key);
    }

    private static NativeMaterialRenderParameters Source(uint selector, uint flags = 0) =>
        new(1, selector, flags, 0, 0, new(new("generated", 0), 44, 35));
}
