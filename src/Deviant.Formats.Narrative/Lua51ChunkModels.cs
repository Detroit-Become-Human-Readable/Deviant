using Deviant.Assets;

namespace Deviant.Formats.Narrative;

public sealed record Lua51ReadOptions(
    int MaximumChunkBytes = 16 * 1024 * 1024,
    int MaximumDepth = 64,
    int MaximumPrototypes = 100_000,
    int MaximumCollectionEntries = 1_000_000,
    int MaximumStringBytes = 4 * 1024 * 1024,
    long MaximumRetainedBytes = 32 * 1024 * 1024);

public sealed record Lua51ChunkDocument(ResourceIdentity Identity, SourceRevision Revision,
    DecodedExtent InputExtent, DecodedExtent ConsumedExtent, byte[] RawBytes, byte[] RawHeader,
    Lua51Prototype RootPrototype, IReadOnlyList<SourceExtent> OriginalPhysicalExtents);

public sealed record Lua51Prototype(ResourceIdentity Identity, DecodedExtent Extent,
    Lua51RawString SourceName, uint FirstLineDefined, uint LastLineDefined, byte UpvalueCount,
    byte ParameterCount, byte VarargFlags, byte MaximumStackSize,
    IReadOnlyList<Lua51InstructionWord> Code, IReadOnlyList<Lua51Constant> Constants,
    IReadOnlyList<Lua51Prototype> Children, IReadOnlyList<uint> LineInfo,
    IReadOnlyList<Lua51LocalVariable> Locals, IReadOnlyList<Lua51RawString> UpvalueNames);

public sealed record Lua51RawString(bool IsNull, byte[] SerializedBytes, byte[] ContentBytes,
    string DisplayText, DecodedExtent Extent);

public sealed record Lua51Constant(byte Tag, Lua51ConstantKind Kind, DecodedExtent Extent,
    bool? BooleanValue = null, uint? NumberBits = null, string? NumberDisplay = null,
    Lua51RawString? StringValue = null)
{
    /// <summary>The original one-byte boolean encoding, including noncanonical nonzero values.</summary>
    public byte? BooleanRawByte { get; init; }
}

public enum Lua51ConstantKind { Nil, Boolean, Number, String }

public sealed record Lua51LocalVariable(Lua51RawString Name, uint StartProgramCounter,
    uint EndProgramCounter, DecodedExtent Extent);

public sealed record Lua51InstructionWord(int ProgramCounter, uint RawWord, DecodedExtent Extent, byte RawOpcode,
    string OpcodeName, byte A, ushort B, ushort C, uint Bx, int SBx,
    Lua51InstructionRole Role, int? AssociatedProgramCounter = null);

public enum Lua51InstructionRole { Instruction, SetListExtraData, ClosureBinding }
