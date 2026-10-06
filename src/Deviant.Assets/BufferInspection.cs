using System.Text.Json.Serialization;

namespace Deviant.Assets;

public enum BufferSectionKind { Header, StoredPayload, DecodedBuffer, DecodedChunk, Gap, RawSuffix }

public sealed record BufferInspectionSection(string Key,string Label,BufferSectionKind Kind,
    SourceExtent? PhysicalExtent,DecodedExtent? DecodedExtent,string Sha256,
    [property:JsonIgnore] ReadOnlyMemory<byte> Bytes)
{
    public long Length => Bytes.Length;
}

public sealed record BufferInspectionDocument(string Format,string Profile,string BoundaryBasis,
    SourceExtent OriginalExtent,IReadOnlyList<BufferInspectionSection> Sections,
    IReadOnlyList<AssetRelationship> Relationships,IReadOnlyDictionary<string,string> Fields);
