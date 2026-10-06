using Deviant.Core;

namespace Deviant.Browser;

public static class NavigationBuilder
{
    public static IReadOnlyList<NavigationNode> Build(ArchiveSnapshot snapshot, CancellationToken token = default)
    {
        var counts = new Dictionary<(uint Package, uint Type), long>();
        foreach (var row in snapshot.Records)
        {
            token.ThrowIfCancellationRequested(); var key = (row.Package, row.Type); counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        var packages = snapshot.Volumes.Select(volume => new NavigationNode(volume.RelativeName, volume.Package, null,
            counts.Where(c => c.Key.Package == volume.Package).Sum(c => c.Value),
            counts.Where(c => c.Key.Package == volume.Package).OrderBy(c => c.Key.Type)
                .Select(c => new NavigationNode($"Type 0x{c.Key.Type:X8}", volume.Package, c.Key.Type, c.Value, [])).ToArray())).ToArray();
        return [new NavigationNode("All resources", null, null, snapshot.Records.Count, packages)];
    }
}
