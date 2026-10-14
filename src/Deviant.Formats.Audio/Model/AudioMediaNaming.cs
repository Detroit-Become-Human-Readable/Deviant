namespace Deviant.Formats.Audio.Model;

/// <summary>Projects resolved event names onto linked media rows for browser display.</summary>
public static class AudioMediaNaming
{
    public static void Apply(AudioCatalog catalog, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var eventsByKey = catalog.Events.ToDictionary(entry => entry.Key, StringComparer.Ordinal);

        foreach (var media in catalog.Media)
        {
            token.ThrowIfCancellationRequested();
            var labels = media.EventKeys
                .Where(eventsByKey.ContainsKey)
                .Select(key => eventsByKey[key])
                .Where(entry => entry.Behavior is EventBehavior.StateChange or EventBehavior.Playback or EventBehavior.Mixed)
                .Where(entry => IsMeaningful(entry.Name))
                .Select(entry => (Label: entry.Name.Trim(), Event: entry,
                    Evidence: entry.Names.FirstOrDefault(name => string.Equals(name.Text, entry.Name, StringComparison.OrdinalIgnoreCase))))
                .GroupBy(link => link.Label, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(link => EventRank(link.Event))
                    .ThenBy(link => link.Event.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(link => link.Event.Key, StringComparer.Ordinal).First())
                .ToArray();

            foreach (var link in labels.OrderBy(link => link.Label, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(link => link.Label, StringComparer.Ordinal))
            {
                if (!media.Aliases.Contains(link.Label, StringComparer.OrdinalIgnoreCase))
                    media.Aliases.Add(link.Label);

                var source = link.Evidence;
                var projected = new NameEvidence(link.Label, "MediaLabel", media.Id, NameKind.Description,
                    source?.Source ?? link.Event.Key, source?.Offset ?? 0, source?.Confidence ?? "High");
                if (!media.Names.Contains(projected)) media.Names.Add(projected);
            }

            if (HasStoredMediaName(media))
            {
                media.DisplayLabel = media.Name;
                continue;
            }

            var preferred = labels
                .OrderBy(link => EventRank(link.Event))
                .ThenBy(link => link.Event.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(link => link.Event.Key, StringComparer.Ordinal)
                .FirstOrDefault();
            if (preferred.Event is not null)
            {
                media.DisplayLabel = preferred.Event.Name.Trim();
                media.NameKind = NameKind.Description;
            }
        }
    }

    private static bool HasStoredMediaName(MediaEntry media) =>
        (media.NameKind is NameKind.Stored or NameKind.Verified or NameKind.Description) && IsMeaningful(media.Name);

    private static int EventRank(AudioEvent entry) => entry.Behavior == EventBehavior.StateChange ? 0 : 1;

    private static bool IsMeaningful(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (uint.TryParse(text, out _)) return false;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return text.Length > 2 && !text[2..].All(Uri.IsHexDigit);
        return true;
    }
}
