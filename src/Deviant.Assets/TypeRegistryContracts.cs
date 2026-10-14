namespace Deviant.Assets;

public enum TypeDomain { Archive, Native, SerializedClass }
public enum EvidenceLevel { ObservedStructure, ConsumerConfirmed, RuntimeValidated, Inferred }

public sealed record TypeKey(TypeDomain Domain, uint? Id, string? Tag = null, uint? Version = null)
{
    public string Key => $"{Domain}:{Id?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""}:{Tag ?? ""}:{Version?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""}";
}

public sealed record TypeEvidence(string Source, string Statement, EvidenceLevel Level,
    string? ExecutableSha256 = null, long? FunctionRva = null);
public sealed record TypeFieldSchema(string Name, string DataType, string Offset,
    string? Units, EvidenceLevel Evidence, string? Meaning = null);
public sealed record TypeSupport(string Structure = "Unknown", string Properties = "Unknown",
    string References = "Unknown", string Placement = "Unknown", string Visualization = "None", string Export = "Original");

public sealed record TypeDefinition(TypeKey Type, string Name, string Category,
    IReadOnlyList<TypeEvidence> Evidence, IReadOnlyList<TypeFieldSchema> Fields,
    TypeSupport Support, IReadOnlyList<string> Questions, bool Registered = false,
    IReadOnlyList<string>? CompetingNames = null, IReadOnlyList<string>? Tags = null);

public sealed record TypeObservation(TypeKey Type, long Count, long ContainerCount,
    IReadOnlyList<ResourceIdentity> Samples, bool ValidatedHeader = false);

public sealed record TypeRegistryEntry(TypeDefinition Definition, long Count, long ContainerCount,
    IReadOnlyList<ResourceIdentity> Samples)
{
    public TypeKey Type => Definition.Type;
    public string Name => Definition.Name;
    public string Category => Definition.Category;
    public string Presence => Count > 0 ? "Observed" : Definition.Registered ? "Registered" : "Not observed";
}

public sealed record InspectedProperty(string Name, string Value, string DataType,
    DecodedExtent? Extent = null, EvidenceLevel Evidence = EvidenceLevel.ObservedStructure,
    string? Units = null);
public sealed record InspectedReference(string Name, ResourceReference Reference, ReferenceResolution Resolution);
public sealed record UninterpretedRange(string Name, long Offset, long Length);
public sealed record ObjectInspection(ResourceIdentity Identity, SourceRevision Revision, string Label,
    TypeKey Type, IReadOnlyList<InspectedProperty> Properties, IReadOnlyList<InspectedReference> References,
    IReadOnlyList<UninterpretedRange> UnknownRanges, IReadOnlyList<SourceExtent> Sources,
    IReadOnlyList<string> Diagnostics, string? PayloadSha256 = null);
