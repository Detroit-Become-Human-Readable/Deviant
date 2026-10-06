namespace Deviant.Formats.Animation;

public readonly record struct AnimationBlockField(int BlockOffset, int Width, uint Bits);

/// <summary>A bounded encoded region; field values are stored bits without channel semantics.</summary>
public sealed record AnimationDataBlock(string Kind, long ResourceOffset, long Length,
    IReadOnlyList<AnimationBlockField> Fields);
