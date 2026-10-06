namespace Deviant.Formats.Animation;

public enum AnimationReadProfile { FramedRecords, Version14Tables, Version14Blocks }

public readonly record struct AnimationScalarField(int RowOffset, int Width, uint Bits);

public sealed record AnimationScalarRow(int Ordinal, long ResourceOffset,
    IReadOnlyList<AnimationScalarField> Fields);

public sealed record AnimationScalarTable(string Kind, long CountResourceOffset, long RowsResourceOffset,
    uint DeclaredCount, int SerializedStride, IReadOnlyList<AnimationScalarRow> Rows);

/// <summary>Stored scalar tables. Remaining offsets mark the first encoded byte
/// not inspected by this profile; native parsing continues after that boundary.</summary>
public sealed record AnimationVersion14Layout(uint Variant, uint GateWord4, uint GateWord8,
    uint Flags, uint Scalar16, uint PrimaryCount20, uint ScalarBits24, uint? SecondaryCount28,
    IReadOnlyList<AnimationScalarTable> Tables, long RemainingResourceOffset, long RemainingLength)
{
    public IReadOnlyList<AnimationDataBlock> PostTableBlocks { get; init; } = [];
    public long? PostTableRemainderResourceOffset { get; init; }
    public long? PostTableRemainderLength { get; init; }
    public IReadOnlyList<string> PostTableDiagnostics { get; init; } = [];
}
