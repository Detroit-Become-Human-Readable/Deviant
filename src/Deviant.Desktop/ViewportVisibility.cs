namespace Deviant.Desktop;

internal sealed class OccurrenceVisibility
{
    private readonly Dictionary<string, bool> source = new(StringComparer.Ordinal);
    private readonly HashSet<string> manuallyHidden = new(StringComparer.Ordinal);
    public bool ShowHidden { get; set; }

    public void Add(string key, bool sourceVisible) => source.Add(key, sourceVisible);
    public bool IsVisible(string key) => source.TryGetValue(key, out bool sourceVisible) &&
        !manuallyHidden.Contains(key) && (sourceVisible || ShowHidden);
    public bool IsManuallyVisible(string key) => source.ContainsKey(key) && !manuallyHidden.Contains(key);

    public bool SetManualVisibility(string key, bool visible)
    {
        if (!source.ContainsKey(key)) return false;
        return visible ? manuallyHidden.Remove(key) : manuallyHidden.Add(key);
    }

    public void Clear()
    {
        source.Clear(); manuallyHidden.Clear();
    }
}
