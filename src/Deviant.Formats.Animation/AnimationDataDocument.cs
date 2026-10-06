using Deviant.Assets;

namespace Deviant.Formats.Animation;

public sealed record AnimationRecordSection(string Tag, uint Version, long ResourceOffset,
    long BodyOffset, uint BodyLength, ReadOnlyMemory<byte> Body);

public sealed record AnimationHeaderWord(int BodyOffset, uint Bits);

public sealed record AnimationDataDocument(IReadOnlyList<AnimationRecordSection> Sections,
    IReadOnlyList<AnimationHeaderWord> HeaderWords, ReadOnlyMemory<byte> EncodedResource)
{
    public AnimationVersion14Layout? Version14Layout { get; init; }
    public AnimationRecordSection Data => Sections[^1];
    public bool HasVersion14Header => Data.Version == 14 && HeaderWords.Count >= 7;
}
