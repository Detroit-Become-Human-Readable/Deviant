using System.Globalization;
using Deviant.Assets;
using Deviant.Formats.Audio.Model;

namespace Deviant.Desktop;

public static class AudioRelationshipPresentation
{
    public static string? EventLabel(AssetRelationship relationship, IReadOnlyList<AudioEvent> events)
    {
        if (relationship.Kind != AssetRelationshipKind.Event) return null;
        var targets = relationship.Resolution.Candidates.ToHashSet();
        var targetEvents = events.Where(item => item.Identity is {} identity && targets.Contains(identity)).ToArray();
        if (targetEvents.Length > 0)
        {
            var labels = targetEvents.Select(Label).Distinct(StringComparer.Ordinal).ToArray();
            var ambiguity = relationship.Resolution.Status == ReferenceStatus.Ambiguous || targets.Count > 1 || labels.Length > 1;
            return string.Join(" / ", labels) + (ambiguity ? " · Ambiguous" : "");
        }
        var sourceEvents = events.Where(item => item.Identity == relationship.Source).ToArray();
        if (sourceEvents.Length == 0) return null;
        var sourceLabels = sourceEvents.Select(Label).Distinct(StringComparer.Ordinal).ToArray();
        return string.Join(" / ", sourceLabels) + (sourceLabels.Length > 1 ? " · Ambiguous" : "") + " · Action";
    }

    private static string Label(AudioEvent item) => string.IsNullOrWhiteSpace(item.Name) ||
        item.Name == item.Id.ToString(CultureInfo.InvariantCulture) ? $"Event 0x{item.Id:X8}" : item.Name;
}
