namespace Deviant.Formats.Animation;

/// <summary>Inputs copied from the current v14 scalar layout and byte vectors.</summary>
public sealed record AnimationVersion14SizingInput(uint Flags,
    ReadOnlyMemory<byte> PrimaryVector, ReadOnlyMemory<byte> SecondaryVector,
    uint ScalarN, uint PostWordK);

/// <summary>
/// Raw size-helper outputs and checked allocator arithmetic. Field names describe byte
/// widths and counts only; they do not identify channels, time, joints, or transforms.
/// </summary>
public sealed record AnimationVersion14SizingEvidence(uint RawFlags, byte VectorMask,
    uint ScalarN, uint PostWordK, int PrimaryVectorCount, int SecondaryVectorCount,
    ulong SizeA, ulong SizeB, ulong SizeCUnaligned, ulong SizeC,
    ulong WindowCount, ulong LogicalByteLength, ulong AlignedSourceByteLength,
    int AllocationGuardBytes, uint UnusedHeaderFlagBits,
    byte UnusedPrimaryVectorBitUnion, byte UnusedSecondaryVectorBitUnion);

/// <summary>Pure bounded model of native v14 byte-length helpers and blob allocator math.</summary>
public static class AnimationVersion14SizingCalculator
{
    public const int MaximumVectorCount = 65_536;
    public const uint MaximumScalarN = 1_000_000;
    public const ulong MaximumAlignedSourceByteLength = 16UL * 1024 * 1024;
    public const int NativeAllocationGuardBytes = 4;

    private const uint FlagUseVectors = 0x2;
    private const uint FlagUseWideMask = 0x800;
    private const byte UsedVectorBits = 0x1B;

    public static bool TryCalculate(AnimationVersion14SizingInput input,
        out AnimationVersion14SizingEvidence? evidence, out string? diagnostic,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        token.ThrowIfCancellationRequested();
        evidence = null;
        diagnostic = null;

        if (input.PrimaryVector.Length > MaximumVectorCount ||
            input.SecondaryVector.Length > MaximumVectorCount)
            return Reject(out evidence, out diagnostic, "AnimationSizingVectorLimit");
        if (input.ScalarN == 0) return Reject(out evidence, out diagnostic, "AnimationSizingScalarNZero");
        if (input.ScalarN > MaximumScalarN) return Reject(out evidence, out diagnostic, "AnimationSizingScalarNLimit");
        if (input.PostWordK == 0) return Reject(out evidence, out diagnostic, "AnimationSizingPostWordKZero");

        var mask = (input.Flags & FlagUseWideMask) != 0 ? (byte)0x10 : (byte)0x0B;
        ulong sizeA;
        ulong sizeB;
        ulong sizeCUnaligned = 0;
        ulong sizeC = 0;
        byte unusedPrimaryBits = 0;
        byte unusedSecondaryBits = 0;

        try
        {
            if ((input.Flags & FlagUseVectors) == 0)
            {
                sizeA = checked(20UL * (ulong)input.PrimaryVector.Length);
                sizeB = checked(7UL * (ulong)input.PrimaryVector.Length);
            }
            else
            {
                sizeA = SumPrimary(input.PrimaryVector.Span, mask, 8, 12, 12,
                    ref unusedPrimaryBits, token);
                sizeA = checked(sizeA + SumSecondary(input.SecondaryVector.Span, mask, 4,
                    ref unusedSecondaryBits, token));

                sizeB = SumPrimary(input.PrimaryVector.Span, mask, 4, 3, 3,
                    ref unusedPrimaryBits, token);
                sizeB = checked(sizeB + SumSecondary(input.SecondaryVector.Span, mask, 1,
                    ref unusedSecondaryBits, token));

                if (input.PostWordK != 1)
                {
                    sizeCUnaligned = SumPrimary(input.PrimaryVector.Span, mask, 1, 3, 3,
                        ref unusedPrimaryBits, token);
                    sizeCUnaligned = checked(sizeCUnaligned + SumSecondary(input.SecondaryVector.Span,
                        mask, 1, ref unusedSecondaryBits, token));
                    sizeC = Align(sizeCUnaligned, 4);
                }
            }

            token.ThrowIfCancellationRequested();
            var windowCount = checked(((ulong)input.ScalarN - 1) / input.PostWordK + 1);
            if (windowCount > input.ScalarN)
                return Reject(out evidence, out diagnostic, "AnimationSizingWindowCountInvalid");
            var logicalLength = checked(windowCount * checked(sizeA + sizeC) +
                checked(((ulong)input.ScalarN - windowCount) * sizeB));
            var alignedLength = Align(logicalLength, 128);
            if (alignedLength > MaximumAlignedSourceByteLength)
                return Reject(out evidence, out diagnostic, "AnimationSizingSourceLengthLimit");

            evidence = new(input.Flags, mask, input.ScalarN, input.PostWordK,
                input.PrimaryVector.Length, input.SecondaryVector.Length,
                sizeA, sizeB, sizeCUnaligned, sizeC, windowCount, logicalLength,
                alignedLength, NativeAllocationGuardBytes,
                input.Flags & ~(FlagUseVectors | FlagUseWideMask),
                unusedPrimaryBits, unusedSecondaryBits);
            return true;
        }
        catch (OverflowException)
        {
            return Reject(out evidence, out diagnostic, "AnimationSizingArithmeticOverflow");
        }
    }

    private static bool Reject(out AnimationVersion14SizingEvidence? evidence,
        out string? diagnostic, string code)
    { evidence = null; diagnostic = code; return false; }

    private static ulong SumPrimary(ReadOnlySpan<byte> vector, byte mask,
        int bit2Width, int bit1Width, int bit8Width, ref byte unusedBits,
        CancellationToken token)
    {
        ulong total = 0;
        for (var index = 0; index < vector.Length; index++)
        {
            if ((index & 255) == 0) token.ThrowIfCancellationRequested();
            var value = vector[index];
            unusedBits |= (byte)(value & ~UsedVectorBits);
            if ((value & mask) == 0) continue;
            if ((value & 0x2) != 0) total = checked(total + (uint)bit2Width);
            if ((value & 0x1) != 0) total = checked(total + (uint)bit1Width);
            if ((value & 0x8) != 0) total = checked(total + (uint)bit8Width);
        }
        return total;
    }

    private static ulong SumSecondary(ReadOnlySpan<byte> vector, byte mask,
        int width, ref byte unusedBits, CancellationToken token)
    {
        ulong total = 0;
        for (var index = 0; index < vector.Length; index++)
        {
            if ((index & 255) == 0) token.ThrowIfCancellationRequested();
            var value = vector[index];
            unusedBits |= (byte)(value & ~UsedVectorBits);
            if ((value & mask) != 0 && (value & 0x0B) != 0)
                total = checked(total + (uint)width);
        }
        return total;
    }

    private static ulong Align(ulong value, uint alignment) =>
        checked(value + alignment - 1) & ~((ulong)alignment - 1);
}
