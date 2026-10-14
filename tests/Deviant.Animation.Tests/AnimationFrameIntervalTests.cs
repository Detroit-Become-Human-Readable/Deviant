using Deviant.Formats.Animation;
using Xunit;

namespace Deviant.Animation.Tests;

public sealed class AnimationFrameIntervalTests
{
    [Theory]
    [InlineData(-1f, 0, 0f)]
    [InlineData(0f, 0, 0f)]
    [InlineData(.125f, 0, .5f)]
    [InlineData(.5f, 2, 0f)]
    [InlineData(.75f, 2, 1f)]
    [InlineData(1f, 2, 1f)]
    [InlineData(float.MaxValue, 2, 1f)]
    public void SelectsAdjacentSamplesAndHoldsTheFinalSample(float seconds, int first, float fraction)
    {
        var interval = AnimationFrameIntervals.At(new(4, .25f, 1f), 2, seconds);
        Assert.Equal(first, interval.First);
        Assert.Equal(first + 1, interval.Second);
        Assert.Equal(fraction, interval.Fraction);
        Assert.Equal((uint)first / 2, interval.Window);
        Assert.Equal(interval.Window * 2, interval.WindowFirstFrame);
    }

    [Fact]
    public void RejectsInvalidTimingAndWindowInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AnimationFrameIntervals.At(new(1, 1, 1), 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnimationFrameIntervals.At(new(4, 0, 1), 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnimationFrameIntervals.At(new(4, float.NaN, 1), 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnimationFrameIntervals.At(new(4, 1, 4), 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnimationFrameIntervals.At(new(4, 1, 4), 1, float.PositiveInfinity));
    }
}
