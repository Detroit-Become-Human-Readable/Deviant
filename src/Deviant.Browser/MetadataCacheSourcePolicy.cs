using Deviant.Core;

namespace Deviant.Browser;

internal static class MetadataCacheSourcePolicy
{
    public static bool CanReuseStampHead(ArchiveSnapshot snapshot,IEnumerable<ResourceRecord>? resources=null)
    {
        var packages=resources?.Select(row=>row.Package).ToHashSet();
        if(packages is not null&&packages.Except(snapshot.Volumes.Select(volume=>volume.Package)).Any())return false;
        return snapshot.Volumes.Where(volume=>packages is null||packages.Contains(volume.Package))
            .All(volume=>!volume.Stamp.Exists||volume.Stamp.Reliable);
    }
    public static bool SameVolumes(IReadOnlyList<VolumeSource> cached,IReadOnlyList<VolumeSource> current)
    {
        if(cached.Count!=current.Count||cached.Select(volume=>volume.Package).Distinct().Count()!=cached.Count)return false;
        var byPackage=current.ToDictionary(volume=>volume.Package);
        return cached.All(volume=>byPackage.TryGetValue(volume.Package,out var now)&&
            string.Equals(volume.FilePath,now.FilePath,StringComparison.OrdinalIgnoreCase)&&
            string.Equals(volume.RelativeName,now.RelativeName,StringComparison.OrdinalIgnoreCase)&&
            SourceIdentity.SameStamp(volume.Stamp,now.Stamp));
    }
}
