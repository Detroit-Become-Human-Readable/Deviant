using Deviant.Formats.Animation;
using Xunit;

namespace Deviant.Animation.Tests;

public sealed class AnimationVersion14SizingCalculatorTests
{
    [Fact]
    public void FlagTwoClearUsesTwentyAndSevenBytesPerPrimaryEntry()
    {
        var input = new AnimationVersion14SizingInput(0x800,
            new byte[] { 0xFF, 0x10, 0x03 }, new byte[] { 0xFF, 0xFF }, ScalarN: 7, PostWordK: 2);

        var evidence = Calculate(input);

        Assert.Equal((byte)0x10, evidence.VectorMask);
        Assert.Equal(60UL, evidence.SizeA);
        Assert.Equal(21UL, evidence.SizeB);
        Assert.Equal(0UL, evidence.SizeC);
        Assert.Equal(4UL, evidence.WindowCount);
        Assert.Equal(303UL, evidence.LogicalByteLength);
        Assert.Equal(384UL, evidence.AlignedSourceByteLength);
    }

    [Fact]
    public void FlagTwoUsesMaskedPrimaryWidthsAndQualifyingSecondaryBytes()
    {
        var input = new AnimationVersion14SizingInput(0x2,
            new byte[] { 0x02, 0x01, 0x08, 0x00 },
            new byte[] { 0x03, 0x20, 0x11 }, ScalarN: 5, PostWordK: 2);

        var evidence = Calculate(input);

        Assert.Equal((byte)0x0B, evidence.VectorMask);
        Assert.Equal(40UL, evidence.SizeA);
        Assert.Equal(12UL, evidence.SizeB);
        Assert.Equal(9UL, evidence.SizeCUnaligned);
        Assert.Equal(12UL, evidence.SizeC);
        Assert.Equal(3UL, evidence.WindowCount);
        Assert.Equal(180UL, evidence.LogicalByteLength);
        Assert.Equal(256UL, evidence.AlignedSourceByteLength);
        Assert.Equal(4, evidence.AllocationGuardBytes);
        Assert.Equal(256UL, evidence.AlignedSourceByteLength); // guard bytes are outside the read length
    }

    [Fact]
    public void WideMaskAndPostWordOneFollowTheirSeparateBranches()
    {
        var primary = new byte[] { 0x12, 0x11, 0x18, 0x10 };
        var secondary = new byte[] { 0x1B, 0x11, 0x02 };
        var wideMask = Calculate(new(0x802, primary, secondary, ScalarN: 5, PostWordK: 2));
        var postWordOne = Calculate(new(0x2, primary, secondary, ScalarN: 5, PostWordK: 1));

        Assert.Equal((byte)0x10, wideMask.VectorMask);
        Assert.Equal(40UL, wideMask.SizeA);
        Assert.Equal(12UL, wideMask.SizeB);
        Assert.Equal(9UL, wideMask.SizeCUnaligned);
        Assert.Equal(12UL, wideMask.SizeC);
        Assert.Equal(0UL, postWordOne.SizeCUnaligned);
        Assert.Equal(0UL, postWordOne.SizeC);
        Assert.Equal(5UL, postWordOne.WindowCount);
        Assert.Equal(220UL, postWordOne.LogicalByteLength);
        Assert.Equal(256UL, postWordOne.AlignedSourceByteLength);
    }

    [Fact]
    public void UnusedRawBitsAreReportedWithoutBeingGivenMeaning()
    {
        var evidence = Calculate(new(0x6ACE,
            new byte[] { 0x4B, 0x50 }, new byte[] { 0x52 }, ScalarN: 3, PostWordK: 2));

        Assert.Equal(0x6ACEu & ~0x802u, evidence.UnusedHeaderFlagBits);
        Assert.Equal((byte)0x40, evidence.UnusedPrimaryVectorBitUnion);
        Assert.Equal((byte)0x40, evidence.UnusedSecondaryVectorBitUnion);
    }

    [Fact]
    public void ZeroCountsVectorLimitsAndOversizedSourceAreRejected()
    {
        AssertRejected(new(0, new byte[] { 0 }, ReadOnlyMemory<byte>.Empty, 0, 1), "AnimationSizingScalarNZero");
        AssertRejected(new(0, new byte[] { 0 }, ReadOnlyMemory<byte>.Empty, 1, 0), "AnimationSizingPostWordKZero");
        AssertRejected(new(0, new byte[AnimationVersion14SizingCalculator.MaximumVectorCount + 1], ReadOnlyMemory<byte>.Empty, 1, 1),
            "AnimationSizingVectorLimit");
        AssertRejected(new(0, new byte[] { 0 }, ReadOnlyMemory<byte>.Empty, AnimationVersion14SizingCalculator.MaximumScalarN + 1, 1),
            "AnimationSizingScalarNLimit");

        var wideVector = Enumerable.Repeat((byte)0x1B,
            AnimationVersion14SizingCalculator.MaximumVectorCount).ToArray();
        AssertRejected(new(0x2 | 0x800, wideVector, ReadOnlyMemory<byte>.Empty,
            AnimationVersion14SizingCalculator.MaximumScalarN, 1), "AnimationSizingSourceLengthLimit");
    }

    [Fact]
    public void PreCancelledCalculationPropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => AnimationVersion14SizingCalculator.TryCalculate(
            new(0x2, new byte[1024], new byte[1024], 300, 21),
            out _, out _, cancellation.Token));
    }

    private static AnimationVersion14SizingEvidence Calculate(AnimationVersion14SizingInput input)
    {
        Assert.True(AnimationVersion14SizingCalculator.TryCalculate(input,
            out var evidence, out var diagnostic), diagnostic);
        return Assert.IsType<AnimationVersion14SizingEvidence>(evidence);
    }

    private static void AssertRejected(AnimationVersion14SizingInput input, string expected)
    {
        Assert.False(AnimationVersion14SizingCalculator.TryCalculate(input,
            out var evidence, out var diagnostic));
        Assert.Null(evidence);
        Assert.Equal(expected, diagnostic);
    }
}
