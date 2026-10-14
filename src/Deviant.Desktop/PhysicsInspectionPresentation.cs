using Deviant.Assets;
using Deviant.Formats.Scenes;

namespace Deviant.Desktop;

public sealed record PhysicsTypeRow(PhysicsTypeDeclaration Source, string Label, int Slot, int ParameterCount)
{
    public PhysicsTypeSlotIdentity Identity => Source.Identity;
}

public sealed record PhysicsLayoutRow(PhysicsTypeLayoutRecord Source, string Label, int Slot,
    int MemberCount, PhysicsLayoutReferenceStatus ParentStatus)
{
    public PhysicsLayoutRecordIdentity Identity => Source.Identity;
}

public sealed record PhysicsItemRow(PhysicsIndexedItemRow Source, string Label, int Ordinal,
    string TypeLabel, string Status)
{
    public PhysicsIndexedItemIdentity Identity => Source.Identity;
}

public sealed record PhysicsInspectionDiagnostic(string Stage, string Code);

/// <summary>Projects accepted physics metadata into rows while retaining each complete source record.</summary>
public sealed class PhysicsInspectionPresentation
{
    public PhysicsInspectionPresentation(PhysicsMetadataInspectionResult sourceResult)
    {
        ArgumentNullException.ThrowIfNull(sourceResult);
        SourceResult = sourceResult;

        var declarations = sourceResult.TypeDeclarations?.Value?.Declarations ?? Array.Empty<PhysicsTypeDeclaration>();
        var declarationGroups = declarations.GroupBy(declaration => declaration.Identity)
            .ToDictionary(group => group.Key, group => group.ToArray());

        Types = Array.AsReadOnly(declarations.Select((declaration, index) => new PhysicsTypeRow(
            declaration,
            WithContext(Printable(declaration.Name?.PrintableText), $"Type {index + 1}", declaration.Identity.Slot),
            declaration.Identity.Slot,
            declaration.Parameters.Count)).ToArray());

        var layouts = sourceResult.TypeLayouts?.Value?.Records ?? Array.Empty<PhysicsTypeLayoutRecord>();
        Layouts = Array.AsReadOnly(layouts.Select((layout, index) =>
        {
            var typeIdentity = new PhysicsTypeSlotIdentity(layout.Identity.Owner,
                layout.Identity.TypeChunkOrdinal, layout.Identity.Slot);
            var typeName = UniqueDeclarationName(declarationGroups, typeIdentity);
            return new PhysicsLayoutRow(layout,
                WithContext(typeName, $"Layout {layout.Identity.RecordOrdinal + 1}", layout.Identity.Slot),
                layout.Identity.Slot, layout.Members.Count, layout.Parent.Status);
        }).ToArray());

        var items = sourceResult.IndexedMetadata?.Value?.Items ?? Array.Empty<PhysicsIndexedItemRow>();
        Items = Array.AsReadOnly(items.Select(item =>
        {
            var typeLabel = DescribeType(item, declarationGroups);
            var labelPrefix = item.IsReservedRow ? "Reserved row" : ResolvedTypeName(item, declarationGroups);
            var itemLabel = WithContext(labelPrefix, $"Item {item.Identity.RowOrdinal + 1}", null);
            return new PhysicsItemRow(item, itemLabel, item.Identity.RowOrdinal, typeLabel, SummarizeStatus(item));
        }).ToArray());

        Diagnostics = Array.AsReadOnly(CollectDiagnostics(sourceResult).ToArray());
    }

    /// <summary>The complete accepted result, including raw extents and unresolved candidate records.</summary>
    public PhysicsMetadataInspectionResult SourceResult { get; }
    public IReadOnlyList<PhysicsTypeRow> Types { get; }
    public IReadOnlyList<PhysicsLayoutRow> Layouts { get; }
    public IReadOnlyList<PhysicsItemRow> Items { get; }
    public IReadOnlyList<PhysicsInspectionDiagnostic> Diagnostics { get; }

    private static IEnumerable<PhysicsInspectionDiagnostic> CollectDiagnostics(PhysicsMetadataInspectionResult result)
    {
        static IEnumerable<PhysicsInspectionDiagnostic> From<T>(string stage, DecodeResult<T>? decoded)
        {
            if (decoded is null) yield break;
            foreach (var code in decoded.Diagnostics)
                if (!string.IsNullOrWhiteSpace(code)) yield return new(stage, code);
        }

        return From("Envelope", result.Envelope)
            .Concat(From("Tagged metadata", result.TaggedMetadata))
            .Concat(From("Type declarations", result.TypeDeclarations))
            .Concat(From("Type layouts", result.TypeLayouts))
            .Concat(From("Indexed metadata", result.IndexedMetadata));
    }

    private static string? UniqueDeclarationName(
        IReadOnlyDictionary<PhysicsTypeSlotIdentity, PhysicsTypeDeclaration[]> declarations,
        PhysicsTypeSlotIdentity identity)
    {
        if (!declarations.TryGetValue(identity, out var matches) || matches.Length != 1) return null;
        return Printable(matches[0].Name?.PrintableText);
    }

    private static string PrintableTypeName(PhysicsIndexedItemRow item,
        IReadOnlyDictionary<PhysicsTypeSlotIdentity, PhysicsTypeDeclaration[]> declarations)
    {
        if (item.Type is not { } type) return item.IsReservedRow ? "Reserved row" : item.Projection.ToString();
        if (type.DeclarationStatus == PhysicsIndexedReferenceStatus.Unique && type.DeclarationTarget is { } target)
            return UniqueDeclarationName(declarations, target) ?? $"Type slot {target.Slot}";
        return DeclarationStatusLabel(type.RawSlot, type.DeclarationStatus, type.DeclarationCandidates.Count);
    }

    private static string? ResolvedTypeName(PhysicsIndexedItemRow item,
        IReadOnlyDictionary<PhysicsTypeSlotIdentity, PhysicsTypeDeclaration[]> declarations) =>
        item.Type is { DeclarationStatus: PhysicsIndexedReferenceStatus.Unique, DeclarationTarget: { } target }
            ? UniqueDeclarationName(declarations, target) : null;

    private static string DescribeType(PhysicsIndexedItemRow item,
        IReadOnlyDictionary<PhysicsTypeSlotIdentity, PhysicsTypeDeclaration[]> declarations)
    {
        if (item.Type is not { } type) return item.IsReservedRow ? "Reserved" : item.Projection.ToString();
        var declaration = PrintableTypeName(item, declarations);
        var layout = type.LayoutStatus switch
        {
            PhysicsIndexedReferenceStatus.Unique => "layout resolved",
            PhysicsIndexedReferenceStatus.Ambiguous => $"layout ambiguous ({type.LayoutCandidates.Count} candidates)",
            PhysicsIndexedReferenceStatus.Missing => "layout missing",
            PhysicsIndexedReferenceStatus.Unresolved => "layout unresolved",
            PhysicsIndexedReferenceStatus.OutOfRange => "layout out of range",
            PhysicsIndexedReferenceStatus.NoLayoutForInitialSlot => "no layout for initial slot",
            PhysicsIndexedReferenceStatus.NoRedirect => "no layout link",
            PhysicsIndexedReferenceStatus.NotProjected => "layout not projected",
            _ => "layout invalid"
        };
        return $"{declaration} · {layout}";
    }

    private static string DeclarationStatusLabel(uint rawSlot, PhysicsIndexedReferenceStatus status, int candidates) => status switch
    {
        PhysicsIndexedReferenceStatus.InitialSlot => $"Initial type slot {rawSlot}",
        PhysicsIndexedReferenceStatus.Ambiguous => $"Type slot {rawSlot} · declaration ambiguous ({candidates} candidates)",
        PhysicsIndexedReferenceStatus.Missing => $"Type slot {rawSlot} · declaration missing",
        PhysicsIndexedReferenceStatus.Unresolved => $"Type slot {rawSlot} · declaration unresolved",
        PhysicsIndexedReferenceStatus.OutOfRange => $"Type slot {rawSlot} · declaration out of range",
        PhysicsIndexedReferenceStatus.Invalid => $"Type slot {rawSlot} · declaration invalid",
        PhysicsIndexedReferenceStatus.NotProjected => $"Type slot {rawSlot} · not projected",
        PhysicsIndexedReferenceStatus.NoLayoutForInitialSlot => $"Initial type slot {rawSlot}",
        PhysicsIndexedReferenceStatus.NoRedirect => $"Type slot {rawSlot} · no link",
        _ => $"Type slot {rawSlot} · unknown"
    };

    private static string SummarizeStatus(PhysicsIndexedItemRow item)
    {
        if (item.Redirect is { } redirect)
            return redirect.Status switch
            {
                PhysicsIndexedReferenceStatus.Unique => "Resolved",
                PhysicsIndexedReferenceStatus.Ambiguous => "Ambiguous",
                _ => "Unknown"
            };
        if (item.Type is not { } type) return "Unknown";
        if (type.DeclarationStatus == PhysicsIndexedReferenceStatus.Ambiguous ||
            type.LayoutStatus == PhysicsIndexedReferenceStatus.Ambiguous) return "Ambiguous";
        if (item.Projection == PhysicsIndexedItemProjection.UnsupportedKind ||
            item.DataPosition.Status is PhysicsIndexedDataPositionStatus.OutOfRange or
                PhysicsIndexedDataPositionStatus.Unresolved or PhysicsIndexedDataPositionStatus.UnsupportedKind)
            return "Unknown";
        return type.DeclarationStatus == PhysicsIndexedReferenceStatus.Unique &&
            type.LayoutStatus == PhysicsIndexedReferenceStatus.Unique ? "Resolved" : "Unknown";
    }

    private static string WithContext(string? recoveredName, string context, int? slot)
    {
        var display = string.IsNullOrWhiteSpace(recoveredName) ? context : $"{recoveredName} · {context}";
        return slot is { } value ? $"{display} · slot {value}" : display;
    }

    private static string? Printable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
