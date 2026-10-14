using System.Buffers.Binary;

namespace Deviant.Formats.Animation;

public sealed record AnimationVersion14StreamDescriptor(ReadOnlyMemory<byte> PrimaryFlags,
    ReadOnlyMemory<byte> SecondaryFlags, ReadOnlyMemory<byte> ConstantVectors,
    ReadOnlyMemory<byte> CoefficientLow, ReadOnlyMemory<byte> CoefficientHigh,
    AnimationVersion14SizingEvidence Sizing);

/// <summary>Joins inspected native header regions to checked external-stream sizing.</summary>
public static class AnimationVersion14Streams
{
    public static AnimationVersion14StreamDescriptor ReadDescriptor(AnimationDataDocument document,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document); token.ThrowIfCancellationRequested();
        var layout = document.Version14Layout ?? throw new InvalidDataException("AnimationStreamLayoutUnavailable");
        if (document.Data.Version != 14 || layout.Variant != 2 || layout.Timing is not { } timing ||
            layout.PostTableDiagnostics.Count != 0 || (layout.Flags & 0x42) != 0x42 ||
            (layout.Flags & 0x9000) != 0 || layout.PrimaryCount20 > AnimationVersion14SizingCalculator.MaximumVectorCount ||
            (layout.SecondaryCount28 ?? 0) > AnimationVersion14SizingCalculator.MaximumVectorCount)
            throw new InvalidDataException("AnimationStreamProfileUnsupported");
        var primaryTable = layout.Tables.SingleOrDefault(x => x.Kind == "0x40");
        var secondaryTable = layout.Tables.SingleOrDefault(x => x.Kind == "0x400+0x40");
        if (primaryTable?.DeclaredCount != layout.PrimaryCount20 ||
            (layout.SecondaryCount28 is not null && secondaryTable?.DeclaredCount != layout.SecondaryCount28))
            throw new InvalidDataException("AnimationStreamTablesMismatch");
        var flags = Region("0x2", checked((long)layout.PrimaryCount20 + (layout.SecondaryCount28 ?? 0)));
        var word = Region("U32", 4);
        uint window = BinaryPrimitives.ReadUInt32LittleEndian(word.Span);
        if (Region("U8", 1).Span[0] != 0) throw new InvalidDataException("AnimationStreamStateUnsupported");
        ReadOnlyMemory<byte> low = default, high = default, constants = default;
        if (window != 1) { low = Region("1024-A", 1024); high = Region("1024-B", 1024); }
        if ((layout.Flags & 0x200) != 0)
        {
            var block = Region("0x200", null);
            if (block.Length < 4) throw new InvalidDataException("AnimationStreamConstantsInvalid");
            var count = BinaryPrimitives.ReadUInt32LittleEndian(block.Span);
            if (block.Length != checked(4L + count * 16L)) throw new InvalidDataException("AnimationStreamConstantsInvalid");
            constants = block[4..];
        }
        var primary = flags[..checked((int)layout.PrimaryCount20)];
        var secondary = flags[checked((int)layout.PrimaryCount20)..];
        if (!AnimationVersion14SizingCalculator.TryCalculate(new(layout.Flags, primary, secondary,
                checked((uint)timing.Count), window), out var sizing, out var diagnostic, token))
            throw new InvalidDataException(diagnostic);
        return new(primary, secondary, constants, low, high, sizing!);

        ReadOnlyMemory<byte> Region(string kind, long? length)
        {
            token.ThrowIfCancellationRequested();
            var block = layout.PostTableBlocks.SingleOrDefault(x => x.Kind == kind);
            if (block is null || length is not null && block.Length != length || block.Length < 0 ||
                block.ResourceOffset < document.Data.BodyOffset || block.ResourceOffset > document.EncodedResource.Length ||
                block.Length > document.EncodedResource.Length - block.ResourceOffset ||
                block.ResourceOffset + block.Length > document.Data.BodyOffset + document.Data.BodyLength)
                throw new InvalidDataException("AnimationStreamRegionInvalid");
            return document.EncodedResource.Slice(checked((int)block.ResourceOffset), checked((int)block.Length));
        }
    }
}
