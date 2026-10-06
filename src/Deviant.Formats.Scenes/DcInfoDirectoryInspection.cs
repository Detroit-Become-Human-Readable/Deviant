using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public sealed record DcInfoDirectoryEntry(long Ordinal, uint Word0, uint Word1, DecodedExtent Extent);
public sealed record DcInfoDuplicate(uint Word0, uint Word1, IReadOnlyList<long> EntryOrdinals);
public sealed record DcInfoDirectoryInspection(uint Version, uint DeclaredBodyLength, uint UnknownWord,
    uint DeclaredEntryCount, DecodedExtent Extent, IReadOnlyList<DcInfoDirectoryEntry> Entries,
    IReadOnlyList<DcInfoDuplicate> Duplicates, DecodedExtent? UnparsedRemainder);
public sealed record DcInfoDirectoryLimits(int MaximumBytes = 16 * 1024 * 1024, int MaximumEntries = 262_144);
