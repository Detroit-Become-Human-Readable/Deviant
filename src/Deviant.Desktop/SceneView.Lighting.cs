using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Deviant.Assets;
using Deviant.Formats.Textures;

namespace Deviant.Desktop;

internal sealed record SceneLightingChoice(string Label, DecodeResult<IblLightStoredFields> Result,
    IReadOnlyList<ReferenceResolution> TextureResolutions);

internal sealed record SceneLightingTextureRow(int Slot, string Label, ResourceIdentity? Target,
    ReferenceStatus NativeStatus, string Detail, RenderTextureSelection? Preview = null);

public partial class SceneView
{
    private ResourceIdentity? lightingNode;
    private bool updatingLighting;
    private SceneLightingChoice? SelectedLight => LightingChoice.SelectedItem as SceneLightingChoice;

    private IReadOnlyList<SceneLightingChoice> LightingChoicesForNode(IReadOnlyList<SceneEntityRecord> entities)
    {
        var choices = entities.Where(entity => section!.IblLightsByEntity.ContainsKey(entity.Identity))
            .Select(entity => section!.IblLightsByEntity[entity.Identity])
            .Select(light => new SceneLightingChoice(light.Result.Value is { } fields
                ? SceneNameDisplay.Project(fields.OwnerName.Text, "IBL") : "IBL · Unavailable",
                light.Result, light.TextureResolutions)).ToArray();
        var labels = new Dictionary<ResourceIdentity, string>();
        foreach (var group in choices.GroupBy(choice => choice.Label, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            var number = 0;
            foreach (var choice in group.OrderBy(choice => choice.Result.Identity.MemberPath, StringComparer.Ordinal))
                labels[choice.Result.Identity] = $"{choice.Label} · {++number}";
        }
        return choices.Select(choice => labels.TryGetValue(choice.Result.Identity, out var label)
            ? choice with { Label = label } : choice).ToArray();
    }

    private void SetLightingInspector(ResourceIdentity node, IReadOnlyList<SceneLightingChoice> choices,
        ResourceIdentity? requestedEntity)
    {
        var preferred = requestedEntity ?? (lightingNode == node ? SelectedLight?.Result.Identity : null);
        lightingNode = node;
        updatingLighting = true;
        try
        {
            LightingChoice.ItemsSource = choices;
            LightingChoice.SelectedItem = preferred is not null
                ? choices.FirstOrDefault(item => item.Result.Identity == preferred)
                : choices.FirstOrDefault();
        }
        finally { updatingLighting = false; }
        if (requestedEntity is null && SelectedLight is { } selected) selectedEntity = selected.Result.Identity;
        LightingPanel.Visibility = choices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShowLightingFields();
    }

    private void LightingChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingLighting || selectedNode is null) return;
        selectedEntity = SelectedLight?.Result.Identity;
        textureCancellation?.Cancel();
        ++textureGeneration;
        ApplyInspector(selectedNode.Node);
        SelectedResourceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowLightingFields()
    {
        var choice = SelectedLight;
        var fields = choice?.Result.Value;
        LightingValues.Visibility = fields is null ? Visibility.Collapsed : Visibility.Visible;
        LightingDetails.Visibility = choice is null ? Visibility.Collapsed : Visibility.Visible;
        TextureResultsHeading.Text = choice is null ? "Related textures" : "Textures";
        if (choice is null)
        {
            LightingDetails.Text = "";
            return;
        }
        if (fields is null)
        {
            LightingDetails.Text = $"IBL {choice.Result.Identity.Key}\n{string.Join("\n", choice.Result.Diagnostics)}";
            QueryTextures.IsEnabled = false;
            return;
        }
        LightEnabled.Text = fields.Enabled.IsNonzero ? "On" : "Off";
        LightIntensity.Text = Scalar(fields.Intensity);
        LightAttenuation.Text = Scalar(fields.AttenuationStart);
        LightSpecular.Text = fields.SpecularTexturesEnabled.IsNonzero ? "On" : "Off";
        LightExtentBounds.Text = BoundsText(fields.ExtentBounds);
        LightLocalBoxBounds.Text = BoundsText(fields.LocalBoxBounds);
        LightingDetails.Text = LightingDetailsText(choice);
        TextureResults.ItemsSource = BuildLightingTextureRows(fields, choice.TextureResolutions, null);
        QueryTextures.IsEnabled = TextureOwnerQueryProvider is not null && fields.TextureReferences.Count > 0;
    }

    private void ClearLightingInspector()
    {
        lightingNode = null;
        updatingLighting = true;
        try { LightingChoice.ItemsSource = null; }
        finally { updatingLighting = false; }
        LightingPanel.Visibility = LightingValues.Visibility = LightingDetails.Visibility = Visibility.Collapsed;
        LightingDetails.Text = "";
        TextureResultsHeading.Text = "Related textures";
        LightEnabled.Text = LightIntensity.Text = LightAttenuation.Text = LightSpecular.Text = "";
        LightExtentBounds.Text = LightLocalBoxBounds.Text = "";
    }

    internal static IReadOnlyList<SceneLightingTextureRow> BuildLightingTextureRows(IblLightStoredFields fields,
        IReadOnlyList<ReferenceResolution> resolutions, TextureOwnerQueryResult? result)
    {
        var rows = new List<SceneLightingTextureRow>();
        if (result is not null && (result.Owner != fields.Identity || result.Revision != fields.Revision))
            throw new InvalidOperationException("The texture results belong to another light or revision.");
        var complete = result is { State: TextureOwnerQueryState.Ready, DiscoveryIncomplete: false } &&
            result.Page.Entries.Count >= result.Page.Total;
        for (var slot = 0; slot < fields.TextureReferences.Count; slot++)
        {
            var reference = fields.TextureReferences[slot];
            var resolution = slot < resolutions.Count ? resolutions[slot]
                : new ReferenceResolution(ReferenceStatus.Unresolved, []);
            var prefix = $"Texture {slot + 1}";
            if (!reference.IsBound)
            {
                rows.Add(new(slot, prefix + " · Unbound", null, ReferenceStatus.Unresolved, "Stored pair is unbound."));
                continue;
            }
            if (resolution is not { Status: ReferenceStatus.Unique, Candidates.Count: 1 })
            {
                rows.Add(new(slot, prefix + " · " + resolution.Status, null, resolution.Status, resolution.Reason ?? ""));
                continue;
            }
            var native = resolution.Candidates[0];
            if (reference.Type != 2137 || native.IndexContentId != fields.Identity.IndexContentId ||
                native.Ordinal != fields.Identity.Ordinal || native.MemberPath is null)
            {
                rows.Add(new(slot, prefix + " · Unavailable", null, resolution.Status,
                    "The native result does not identify a supported texture in this container."));
                continue;
            }
            if (result is null)
            {
                rows.Add(new(slot, prefix, null, resolution.Status, "Archive texture query has not run."));
                continue;
            }
            var matches = result.Page.Entries.Where(entry =>
                entry.Identity.IndexContentId == fields.Identity.IndexContentId && entry.Identity.MemberPath is null &&
                entry.ResourceType is 2229 or 2230 && entry.Owners.Any(MatchesOwner))
                .DistinctBy(entry => entry.Identity).ToArray();
            if (matches.Length == 0)
            {
                rows.Add(new(slot, prefix + (complete ? " · Unavailable" : " · Pending"), null, resolution.Status,
                    result.Reason ?? (complete ? "No verified archive texture matches this reference." : "Texture discovery or the result page is incomplete.")));
                continue;
            }
            foreach (var entry in matches)
            {
                var label = prefix + " · " + SceneNameDisplay.Project(entry.Label, "Texture");
                var detail = $"Native {native.Key}\nArchive {entry.Identity.Key}";
                var typedField = FormattableString.Invariant($"Typed archive reference {entry.ResourceType}/{entry.ResourceId}");
                var catalogs = entry.Owners.Where(MatchesOwner).SelectMany(owner => owner.Evidence)
                    .Where(evidence => evidence.Reader == nameof(TextureCatalogMembershipReader) &&
                        evidence.ReaderVersion == "catalog-membership-v1" && evidence.Field == typedField &&
                        evidence.Revision == fields.Revision && evidence.Source.IndexContentId == fields.Identity.IndexContentId &&
                        evidence.Source.MemberPath is null && evidence.Source.Ordinal >= 0 &&
                        evidence.DecodedExtent is null && evidence.PhysicalExtent is { Length: 9 } extent &&
                        evidence.SourceSha256 == extent.PayloadSha256)
                    .Select(evidence => evidence.Source).Distinct().OrderBy(identity => identity.Ordinal).ToArray();
                if (catalogs.Length == 0)
                {
                    rows.Add(new(slot, label + (complete ? " · Unavailable" : " · Pending"), null, resolution.Status,
                        detail + "\nNo verified catalog reference is available."));
                    continue;
                }
                for (var index = 0; index < catalogs.Length; index++)
                {
                    var catalog = catalogs[index];
                    var display = catalogs.Length == 1 ? label : $"{label} · {index + 1}";
                    rows.Add(new(slot, display, entry.Identity, resolution.Status, detail + $"\nCatalog {catalog.Key}",
                        new(entry.Identity, native, catalog, fields.Revision, entry.Label)));
                }
            }

            bool MatchesOwner(TextureDiscoveryLink owner) => owner.Verified && owner.Identity == fields.Identity &&
                owner.Evidence.Any(evidence => evidence.ReaderVersion == TextureContextReader.Version &&
                    evidence.Field == "Native texture reference" && evidence.Revision == fields.Revision &&
                    evidence.Source.IndexContentId == fields.Identity.IndexContentId && evidence.Source.Ordinal == fields.Identity.Ordinal &&
                    evidence.DecodedExtent == reference.Extent);
        }
        return rows.AsReadOnly();
    }

    private static string Scalar(IblFloat32 value) => value.GetValue().ToString("G9", CultureInfo.InvariantCulture);
    private static string BoundsText(IblStoredBounds bounds) =>
        $"X  {Scalar(bounds.MinimumX)} – {Scalar(bounds.MaximumX)}\n" +
        $"Y  {Scalar(bounds.MinimumY)} – {Scalar(bounds.MaximumY)}\n" +
        $"Z  {Scalar(bounds.MinimumZ)} – {Scalar(bounds.MaximumZ)}";

    private static string LightingDetailsText(SceneLightingChoice choice)
    {
        var fields = choice.Result.Value!;
        var text = new StringBuilder();
        text.AppendLine($"IBL {fields.Identity.Key}");
        text.AppendLine($"Revision {fields.Revision.PhysicalSourceStamp}");
        text.AppendLine($"Name {fields.OwnerName.Text}");
        text.AppendLine($"Source path {fields.SourcePath.Text}");
        text.AppendLine($"Class {fields.Extent.Offset} + {fields.Extent.Length}");
        text.AppendLine($"Enabled 0x{fields.Enabled.Value:X2} @ {fields.Enabled.Extent.Offset}");
        AddBounds("Extent bounds", fields.ExtentBounds);
        AddBounds("Local box bounds", fields.LocalBoxBounds);
        text.AppendLine($"Stored pair {fields.StoredReference.Type}/{fields.StoredReference.Id} @ {fields.StoredReference.Extent.Offset}");
        AddFloat("Attenuation", fields.AttenuationStart); AddFloat("Intensity", fields.Intensity);
        AddFloat("Scalar77", fields.Scalar77); AddFloat("Scalar83", fields.Scalar83);
        text.AppendLine($"Specular textures 0x{fields.SpecularTexturesEnabled.Value:X2} @ {fields.SpecularTexturesEnabled.Extent.Offset}");
        text.AppendLine($"Byte82 0x{fields.Byte82.Value:X2} @ {fields.Byte82.Extent.Offset}");
        text.AppendLine($"Word91 0x{fields.Word91.Value:X8} @ {fields.Word91.Extent.Offset}");
        text.AppendLine($"First set index {fields.FirstSpecularSetIndex.Value} @ {fields.FirstSpecularSetIndex.Extent.Offset}");
        AddFloat("Trailing scalar 0", fields.TrailingScalar0); AddFloat("Trailing scalar 1", fields.TrailingScalar1);
        for (var slot = 0; slot < fields.TextureReferences.Count; slot++)
        {
            var reference = fields.TextureReferences[slot];
            text.AppendLine($"Texture {slot + 1}: {reference.Type}/{reference.Id} @ {reference.Extent.Offset}");
            if (slot >= choice.TextureResolutions.Count) continue;
            var resolution = choice.TextureResolutions[slot];
            text.AppendLine($"  {resolution.Status}: {resolution.Reason}");
            foreach (var candidate in resolution.Candidates) text.AppendLine($"  {candidate.Key}");
        }
        foreach (var diagnostic in choice.Result.Diagnostics) text.AppendLine(diagnostic);
        return text.ToString().TrimEnd();

        void AddFloat(string label, IblFloat32 value) =>
            text.AppendLine($"{label} {Scalar(value)} · 0x{value.Bits:X8} @ {value.Extent.Offset}");
        void AddBounds(string label, IblStoredBounds bounds)
        {
            text.AppendLine($"{label}: finite {bounds.IsFinite}, ordered {bounds.IsOrdered}, positive volume {bounds.HasPositiveVolume}");
            AddFloat("  Min X", bounds.MinimumX); AddFloat("  Max X", bounds.MaximumX);
            AddFloat("  Min Y", bounds.MinimumY); AddFloat("  Max Y", bounds.MaximumY);
            AddFloat("  Min Z", bounds.MinimumZ); AddFloat("  Max Z", bounds.MaximumZ);
        }
    }
}
