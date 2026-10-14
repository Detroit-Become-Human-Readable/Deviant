using System.Text.Json.Serialization;
using Deviant.Assets;

namespace Deviant.Formats.Narrative;

public sealed record GmkCommonGroupLimits(int MaximumPayloadBytes = 16 * 1024 * 1024,
    int MaximumItems = 65_536, int MaximumWords = 262_144, int MaximumStringBytes = 1024 * 1024,
    int MaximumPhysicalSources = 4096, int MaximumMetadataCharacters = 4096,
    long MaximumRetainedBytes = 64 * 1024 * 1024);

/// <summary>All returned byte views share an owned payload snapshot; they do not retain the input container lease.</summary>
public sealed record GmkCommonGroupDocument(ResourceIdentity Identity, SourceRevision Revision, uint NativeId,
    uint ClassVersion, uint Word16, DecodedExtent PayloadExtent, DecodedExtent HeaderExtent,
    DecodedExtent Word16Extent, DecodedExtent ItemCountExtent, IReadOnlyList<GmkCommonGroupItem> Items,
    IReadOnlyList<SourceExtent> OriginalPhysicalExtents, [property: JsonIgnore] ReadOnlyMemory<byte> RawPayload);

public sealed record GmkCommonGroupItem(ResourceIdentity Identity, uint FactoryClassId,
    GmkStoredText StoredName, uint ComputedNameCrc32, GmkStoredText StoredText,
    IReadOnlyList<GmkGroupWord> Words, byte ReferenceFlag, GmkUnclassifiedReferencePair? ReferencePair,
    uint FinalWord, DecodedExtent Extent, DecodedExtent WordCountExtent,
    DecodedExtent ReferenceFlagExtent, DecodedExtent FinalWordExtent);

public sealed record GmkStoredText(string DisplayText, bool IsValidUtf8, DecodedExtent LengthExtent,
    DecodedExtent TextExtent, [property: JsonIgnore] ReadOnlyMemory<byte> RawBytes);
public sealed record GmkGroupWord(int Index, uint Value, DecodedExtent Extent);
public enum GmkReferenceLookupClassification { Unclassified }
public sealed record GmkUnclassifiedReferencePair(uint RawWord0, uint RawWord1, uint NormalizedWord0,
    uint NormalizedWord1, bool LookupEligible, GmkReferenceLookupClassification LookupClassification,
    DecodedExtent Extent, [property: JsonIgnore] ReadOnlyMemory<byte> RawBytes);
