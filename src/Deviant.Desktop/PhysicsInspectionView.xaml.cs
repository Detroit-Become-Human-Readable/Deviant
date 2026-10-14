using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Deviant.Assets;
using Deviant.Formats.Scenes;

namespace Deviant.Desktop;

/// <summary>Displays accepted physics metadata records without assigning meaning to raw fields.</summary>
public partial class PhysicsInspectionView : UserControl
{
    private PhysicsInspectionPresentation? presentation;
    private ICollectionView? typesView;
    private ICollectionView? layoutsView;
    private ICollectionView? itemsView;

    public PhysicsInspectionView() => InitializeComponent();

    public void SetResult(PhysicsMetadataInspectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ClearCore(collapseDiagnostics: false);

        var current = new PhysicsInspectionPresentation(result);
        presentation = current;
        typesView = CreateView(current.Types, SearchTypes);
        layoutsView = CreateView(current.Layouts, SearchLayouts);
        itemsView = CreateView(current.Items, SearchItems);
        Types.ItemsSource = typesView;
        Layouts.ItemsSource = layoutsView;
        Items.ItemsSource = itemsView;
        Diagnostics.ItemsSource = current.Diagnostics;
        Summary.Text = BuildSummary(current);
        Status.Text = current.Diagnostics.Count == 0
            ? "Inspection results"
            : $"{current.Diagnostics.Count.ToString(CultureInfo.InvariantCulture)} diagnostic entries";
        Sections.SelectedIndex = 0;
    }

    public void Clear() => ClearCore(collapseDiagnostics: true);

    private void ClearCore(bool collapseDiagnostics)
    {
        Types.SelectionChanged -= TypeSelected;
        Layouts.SelectionChanged -= LayoutSelected;
        Items.SelectionChanged -= ItemSelected;
        Types.ItemsSource = null;
        Layouts.ItemsSource = null;
        Items.ItemsSource = null;
        Diagnostics.ItemsSource = null;
        typesView = null;
        layoutsView = null;
        itemsView = null;
        presentation = null;
        Types.SelectedItem = null;
        Layouts.SelectedItem = null;
        Items.SelectedItem = null;
        TypeTitle.Text = "Select a type";
        LayoutTitle.Text = "Select a layout";
        ItemTitle.Text = "Select an item";
        TypeDetails.Text = TypeAdvanced.Text = "";
        LayoutDetails.Text = LayoutAdvanced.Text = "";
        ItemDetails.Text = ItemAdvanced.Text = "";
        Search.Clear();
        Summary.Text = "";
        Status.Text = "";
        if (collapseDiagnostics) DiagnosticsExpander.IsExpanded = false;
        Types.SelectionChanged += TypeSelected;
        Layouts.SelectionChanged += LayoutSelected;
        Items.SelectionChanged += ItemSelected;
    }

    private static ICollectionView CreateView<T>(IReadOnlyList<T> source, Predicate<object> filter)
    {
        var view = CollectionViewSource.GetDefaultView(source);
        view.Filter = filter;
        return view;
    }

    private static string BuildSummary(PhysicsInspectionPresentation current)
    {
        var sections = new List<string>();
        if (current.SourceResult.TypeDeclarations?.Value is { } declarations)
            sections.Add($"{declarations.Declarations.Count:N0} types");
        if (current.SourceResult.TypeLayouts?.Value is { } layouts)
            sections.Add($"{layouts.Records.Count:N0} layouts");
        if (current.SourceResult.IndexedMetadata?.Value is { } indexed)
            sections.Add($"{indexed.Items.Count:N0} items");
        return sections.Count == 0 ? "No complete metadata stages" : string.Join(" · ", sections);
    }

    private string Query => Search.Text.Trim();

    private bool SearchTypes(object value) => Matches(Query,
        value is PhysicsTypeRow row ? $"{row.Label} {row.Slot} {row.ParameterCount} {row.Identity}" : "");

    private bool SearchLayouts(object value) => Matches(Query,
        value is PhysicsLayoutRow row ? $"{row.Label} {row.Slot} {row.MemberCount} {row.ParentStatus} {row.Identity}" : "");

    private bool SearchItems(object value) => Matches(Query,
        value is PhysicsItemRow row ? $"{row.Label} {row.Ordinal} {row.TypeLabel} {row.Status} {row.Identity}" : "");

    private static bool Matches(string query, string value) => query.Length == 0 ||
        value.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        typesView?.Refresh();
        layoutsView?.Refresh();
        itemsView?.Refresh();
    }

    private void SectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Sections) return;
        Search.Clear();
    }

    private void DiagnosticsExpanded(object sender, RoutedEventArgs e)
    {
        ResultsRow.MinHeight = 140;
        DiagnosticsSplitterRow.Height = new GridLength(8);
        DiagnosticsSplitter.Visibility = Visibility.Visible;
        UpdateExpandedDiagnosticsHeight();
    }

    private void DiagnosticsCollapsed(object sender, RoutedEventArgs e)
    {
        DiagnosticsSplitter.Visibility = Visibility.Collapsed;
        DiagnosticsSplitterRow.Height = new GridLength(0);
        DiagnosticsRow.MinHeight = 0;
        DiagnosticsRow.Height = GridLength.Auto;
        ResultsRow.MinHeight = 200;
    }

    private void InspectionLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DiagnosticsExpander.IsExpanded) UpdateExpandedDiagnosticsHeight();
    }

    private void UpdateExpandedDiagnosticsHeight()
    {
        if (InspectionLayout.ActualHeight <= 0) return;
        DiagnosticsRow.MinHeight = 80;
        var available = InspectionLayout.ActualHeight - 160 - 8;
        DiagnosticsRow.Height = new GridLength(Math.Clamp(available, 80, 140));
    }

    private void TypeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Types.SelectedItem is not PhysicsTypeRow row)
        {
            TypeTitle.Text = "Select a type";
            TypeDetails.Text = TypeAdvanced.Text = "";
            return;
        }

        var declaration = row.Source;
        TypeTitle.Text = row.Label;
        TypeDetails.Text = $"Slot {row.Slot}\n{row.ParameterCount} parameters";
        TypeAdvanced.Text = $"Identity: {row.Identity}\nExtent: {Format(declaration.Extent)}\nName index: {Format(declaration.NameIndex)}\nName: {Format(declaration.Name)}\nParameter count: {Format(declaration.ParameterCount)}\n" +
            string.Join("\n", declaration.Parameters.Select((parameter, index) =>
                $"Parameter {index + 1}: kind={parameter.Kind}; nameIndex={Format(parameter.NameIndex)}; name={Format(parameter.Name)}; value={Format(parameter.Value)}; target={parameter.Target}; targetsInitialSlot={parameter.TargetsInitialSlot}"));
    }

    private void LayoutSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Layouts.SelectedItem is not PhysicsLayoutRow row)
        {
            LayoutTitle.Text = "Select a layout";
            LayoutDetails.Text = LayoutAdvanced.Text = "";
            return;
        }

        var layout = row.Source;
        LayoutTitle.Text = row.Label;
        LayoutDetails.Text = $"Slot {row.Slot}\n{row.MemberCount} members\nParent: {DescribeReference(row.ParentStatus)}";
        LayoutAdvanced.Text = $"Identity: {row.Identity}\nExtent: {Format(layout.Extent)}\nSlot index: {Format(layout.SlotIndex)}\nMask: {Format(layout.Mask)}\nParent: {Format(layout.Parent)}\nSubtype: {Format(layout.Subtype)}\n" +
            string.Join("\n", layout.GatedValues.Select((value, index) => $"Gated value {index + 1}: maskBit={value.MaskBit}; fields={string.Join(", ", value.Fields.Select(Format))}")) + "\n" +
            string.Join("\n", layout.Members.Select((member, index) => $"Member {index + 1}: stream={member.StreamOrdinal}; nameIndex={Format(member.NameIndex)}; name={Format(member.Name)}; flags={Format(member.Flags)}; rawFlags=0x{member.RawFlagsUInt16:X4}; effectiveFlags=0x{member.EffectiveFlagsUInt16:X4}; thirdScalar={Format(member.ThirdScalar)}; packedWord=0x{member.PackedNativeWord:X8}; type={Format(member.Type)}; extent={Format(member.Extent)}")) + "\n" +
            string.Join("\n", layout.Interfaces.Select((item, index) => $"Interface {index + 1}: typeIndex={Format(item.TypeIndex)}; declarationTarget={item.DeclarationTarget}; secondScalar={Format(item.SecondScalar)}; extent={Format(item.Extent)}"));
    }

    private void ItemSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Items.SelectedItem is not PhysicsItemRow row)
        {
            ItemTitle.Text = "Select an item";
            ItemDetails.Text = ItemAdvanced.Text = "";
            return;
        }

        var item = row.Source;
        ItemTitle.Text = row.Label;
        ItemDetails.Text = $"Row {row.Ordinal}\nType: {row.TypeLabel}\nStatus: {row.Status}\nData position: {item.DataPosition.Status}\n" +
            (item.Type is { } type ? $"Declaration: {DescribeReference(type.DeclarationStatus)}\nLayout: {DescribeReference(type.LayoutStatus)}\n" : "Type reference unavailable\n") +
            (item.Redirect is { } redirect ? $"Redirect: {DescribeReference(redirect.Status)}" : "");
        ItemAdvanced.Text = $"Identity: {row.Identity}\nProjection: {item.Projection}\nReserved row: {item.IsReservedRow}\n" +
            $"Raw words: 0x{item.RawWord0:X8}, 0x{item.RawWord1:X8}, 0x{item.RawWord2:X8}\nWord 2 signed value: {item.Word2NativeInt32}\nKind nibble: 0x{item.RawKindNibble:X}\nUnknown word 0 bits: 0x{item.UnknownWord0Bits:X8}\n" +
            $"Word extents: {Format(item.Word0Extent)}; {Format(item.Word1Extent)}; {Format(item.Word2Extent)}\n" +
            $"Type reference: {Format(item.Type)}\nData position: {Format(item.DataPosition)}\nRedirect: {Format(item.Redirect)}\n" +
            (item.Type is { } typeCandidates ? $"Declaration candidates: {string.Join(", ", typeCandidates.DeclarationCandidates)}\nLayout candidates: {string.Join(", ", typeCandidates.LayoutCandidates)}\n" : "") +
            (item.Redirect is { } redirectCandidates ? $"Redirect candidates: {string.Join(", ", redirectCandidates.Candidates)}\n" : "");
    }

    private static string DescribeReference(PhysicsLayoutReferenceStatus status) => status switch
    {
        PhysicsLayoutReferenceStatus.Unique => "resolved to one record",
        PhysicsLayoutReferenceStatus.Ambiguous => "ambiguous; multiple candidates retained",
        PhysicsLayoutReferenceStatus.Missing => "no matching record",
        PhysicsLayoutReferenceStatus.Unresolved => "could not be resolved",
        PhysicsLayoutReferenceStatus.NoLinkRequested => "no parent link requested",
        _ => status.ToString()
    };

    private static string DescribeReference(PhysicsIndexedReferenceStatus status) => status switch
    {
        PhysicsIndexedReferenceStatus.Unique => "resolved to one record",
        PhysicsIndexedReferenceStatus.Ambiguous => "ambiguous; multiple candidates retained",
        PhysicsIndexedReferenceStatus.Missing => "no matching record",
        PhysicsIndexedReferenceStatus.Unresolved => "could not be resolved",
        PhysicsIndexedReferenceStatus.OutOfRange => "outside the available range",
        PhysicsIndexedReferenceStatus.InitialSlot => "initial slot",
        PhysicsIndexedReferenceStatus.NoLayoutForInitialSlot => "no layout for the initial slot",
        PhysicsIndexedReferenceStatus.NoRedirect => "no redirect link",
        PhysicsIndexedReferenceStatus.NotProjected => "not projected",
        PhysicsIndexedReferenceStatus.Invalid => "invalid reference",
        _ => status.ToString()
    };

    private static string Format(PhysicsTypeInteger value) =>
        $"raw=0x{value.RawBits:X}; signed={value.NativeInt32}; extent={Format(value.Extent)}";

    private static string Format(PhysicsTypeName? value) => value is null
        ? "unavailable"
        : $"index={value.Index}; text={value.PrintableText ?? "(no printable text)"}; extent={Format(value.Extent)}";

    private static string Format(PhysicsLayoutReference? value) => value is null
        ? "unavailable"
        : $"index={Format(value.Index)}; status={value.Status}; candidates={string.Join(", ", value.Candidates)}";

    private static string Format(PhysicsIndexedTypeReference? value) => value is null
        ? "unavailable"
        : $"rawSlot={value.RawSlot}; declaration={value.DeclarationStatus}; layout={value.LayoutStatus}";

    private static string Format(PhysicsIndexedDataPosition value) =>
        $"rawOffset=0x{value.RawOffset:X}; status={value.Status}; dataRelativeOffset={value.DataRelativeOffset}; position={Format(value.Position)}";

    private static string Format(PhysicsIndexedItemReference? value) => value is null
        ? "unavailable"
        : $"index={value.RawSignedIndex}; status={value.Status}; target={value.Target}; candidates={string.Join(", ", value.Candidates)}";

    private static string Format(DecodedExtent? value) => value is null
        ? "unavailable"
        : $"{value.Container} + {value.Offset} ({value.Length} bytes)";
}
