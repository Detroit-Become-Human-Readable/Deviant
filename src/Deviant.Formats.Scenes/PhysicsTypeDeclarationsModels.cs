using System.Text.Json.Serialization;
using Deviant.Assets;

namespace Deviant.Formats.Scenes;

public enum PhysicsTypeDeclarationCoverage { CompleteStructure, Partial }
public enum PhysicsTypeParameterKind { NumericValue, LocalTypeSlot }
public sealed record PhysicsTypeDeclarationLimits(int MaximumChunks = 128, int MaximumDepth = 4,
    int MaximumLeafBytes = 32 * 1024, int MaximumStrings = 1024, int MaximumStringBytes = 256,
    int MaximumSlots = 4096, int MaximumParametersPerDeclaration = 256, int MaximumParameters = 8192,
    long MaximumMetadataBytes = 512 * 1024, int MaximumPhysicalExtents = 8192, int MaximumPrecedingSteps = 128);

public sealed record PhysicsTypeSlotIdentity(ResourceIdentity Owner, int TypeChunkOrdinal, int Slot);
public sealed record PhysicsTypeInteger(ulong RawBits, int NativeInt32, DecodedExtent Extent,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawEncoding);
public sealed record PhysicsTypeName(int Index, string? PrintableText, DecodedExtent Extent,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawBytes);
public sealed record PhysicsTypeParameter(PhysicsTypeInteger NameIndex, PhysicsTypeName Name,
    PhysicsTypeInteger Value, PhysicsTypeParameterKind Kind, PhysicsTypeSlotIdentity? Target,
    bool TargetsInitialSlot);
public sealed record PhysicsTypeDeclaration(PhysicsTypeSlotIdentity Identity, DecodedExtent Extent,
    PhysicsTypeInteger NameIndex, PhysicsTypeName? Name, PhysicsTypeInteger ParameterCount,
    IReadOnlyList<PhysicsTypeParameter> Parameters);
public sealed record PhysicsTypeDeclarationLeaf(int ChunkOrdinal, DecodedExtent HeaderExtent,
    DecodedExtent BodyExtent, uint RawBigEndianWord, string Sha256,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawHeader,
    [property: JsonIgnore] ReadOnlyMemory<byte> RawBody);
public sealed record PhysicsTypeDeclarationsDocument(ResourceIdentity Identity, SourceRevision Revision,
    DecodedExtent EmbeddedExtent, string InheritedEmbeddedSha256, int TypeChunkOrdinal,
    PhysicsTypeDeclarationLeaf StringTable, PhysicsTypeDeclarationLeaf DeclarationTable,
    int InitialSlotCount, PhysicsTypeInteger? FinalSlotCount,
    IReadOnlyList<PhysicsTypeName> Names, IReadOnlyList<PhysicsTypeDeclaration> Declarations,
    PhysicsTypeDeclarationCoverage Coverage, IReadOnlyList<DecodedExtent> UnknownRanges,
    long RetainedMetadataBytes);
