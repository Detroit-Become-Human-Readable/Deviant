namespace Deviant.Formats.Animation;

public readonly record struct AnimationFrameInterval(int First, int Second, float Fraction,
    uint Window, uint WindowFirstFrame);

/// <summary>Converts native clip time to the adjacent sample pair and anchor window.</summary>
public static class AnimationFrameIntervals
{
    public static AnimationFrameInterval At(AnimationVersion14Timing timing, uint framesPerWindow, float seconds)
    {
        ArgumentNullException.ThrowIfNull(timing);
        if (timing.Count is < 2 or > (int)AnimationVersion14SizingCalculator.MaximumScalarN ||
            !float.IsFinite(timing.Period) || timing.Period <= 0 ||
            !float.IsFinite(timing.Duration) || timing.Duration <= 0)
            throw new ArgumentOutOfRangeException(nameof(timing));
        if (framesPerWindow == 0) throw new ArgumentOutOfRangeException(nameof(framesPerWindow));
        if (!float.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        float sample = Math.Clamp(seconds / timing.Period, 0, timing.Count - 1);
        int first = Math.Min((int)sample, timing.Count - 2);
        uint window = (uint)first / framesPerWindow;
        return new(first, first + 1, sample - first, window, checked(window * framesPerWindow));
    }
}
