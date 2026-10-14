namespace Deviant.Formats.Animation;

public static class AnimationRetargeting
{
    public static float ReadSourceHeight(AnimationDataDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var layout = document.Version14Layout ?? throw new InvalidDataException("AnimationRetargetLayoutUnavailable");
        if ((layout.Flags & 0x2000) == 0) return 0;
        var block = layout.PostTableBlocks.SingleOrDefault(x => x.Kind == "0x2000");
        if (block is not { Length: 5 } || block.Fields.Count != 2 ||
            block.Fields[0].BlockOffset != 0 || block.Fields[0].Width != 1 ||
            block.Fields[1].BlockOffset != 1 || block.Fields[1].Width != 4)
            throw new InvalidDataException("AnimationRetargetRegionUnavailable");
        var height = BitConverter.Int32BitsToSingle(unchecked((int)block.Fields[1].Bits));
        if (!float.IsFinite(height)) throw new InvalidDataException("AnimationRetargetHeightNonfinite");
        return block.Fields[0].Bits == 0 || block.Fields[0].Bits == 1 && height < .5f ? 0 : height;
    }

    public static float TranslationRatio(float sourceHeight, float targetHeight)
    {
        if (!float.IsFinite(sourceHeight) || !float.IsFinite(targetHeight) || sourceHeight < 0 || targetHeight < 0)
            throw new InvalidDataException("AnimationRetargetHeightInvalid");
        float ratio = sourceHeight == 0 || targetHeight == 0 ? 1 : targetHeight / sourceHeight;
        if (!float.IsFinite(ratio) || ratio <= 0) throw new InvalidDataException("AnimationRetargetRatioInvalid");
        return ratio;
    }
}
