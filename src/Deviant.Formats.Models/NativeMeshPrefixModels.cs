using Deviant.Assets;

namespace Deviant.Formats.Models;

public sealed record NativeMeshPrefixFixedFields(uint Word0, uint Word1,
    ResourceReference Reference0, ResourceReference Reference1, DecodedExtent Extent);

public sealed record NativeMeshContextEntry(int Ordinal, byte[] StoredNameBytes,
    ResourceReference Reference, uint? ControlWord, uint? SecondaryByteCount,
    DecodedExtent Extent, DecodedExtent NameExtent, DecodedExtent ReferenceExtent,
    DecodedExtent? ControlExtent, DecodedExtent? SecondaryLengthExtent);

public sealed record NativeMeshPrefixReference(ResourceReference Reference, DecodedExtent Extent);

public sealed record NativeMeshPrefixFinalFields(uint Word, byte Flag, ulong RawSixBytes,
    IReadOnlyList<uint> Words, DecodedExtent Extent);

/// <summary>A bounded MESH v29 prefix with owned stored names and source-coordinate extents.</summary>
public sealed record NativeMeshPrefixDocument(ResourceIdentity Identity, SceneEntityRecord Entity,
    uint Version, IReadOnlyList<NativeMeshLod> Lods, uint NativeFlags, uint GroupIndex,
    NativeMeshPrefixFixedFields FixedFields, uint PackedContextControl, byte ContextMode,
    IReadOnlyList<NativeMeshContextEntry> ContextEntries, DecodedExtent ContextBlobExtent,
    DecodedExtent SecondaryContextBlobExtent, IReadOnlyList<NativeMeshPrefixReference> ExtraReferences,
    NativeMeshPrefixFinalFields FinalFields, DecodedExtent PrefixExtent, int NextClassOffset,
    DecodedExtent TailExtent, IReadOnlyDictionary<string, DecodedExtent> FieldExtents)
{
    public long RetainedNameBytes => ContextEntries.Sum(entry => (long)entry.StoredNameBytes.Length);
}
