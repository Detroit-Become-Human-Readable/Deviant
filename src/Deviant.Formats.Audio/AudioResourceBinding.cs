using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Audio.Model;
using ArchiveResourceRecord = Deviant.Core.ResourceRecord;

namespace Deviant.Formats.Audio;

public sealed record AudioResourceOwner(ResourceIdentity Identity, ArchiveResourceRecord Resource, VolumeSource? Volume);

public sealed record AudioPayloadBinding(ResourceIdentity LogicalIdentity, ResourceIdentity PayloadIdentity,
    ArchiveResourceRecord LogicalOwner, ArchiveResourceRecord PayloadOwner, VolumeSource Volume, DataSlice Extent);

/// <summary>Resolves an audio row's logical identity separately from the archive row that owns its bytes.</summary>
public static class AudioResourceBinding
{
    public static AudioResourceOwner ResolveOwner(ArchiveSnapshot snapshot, ResourceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(identity);
        if (!StringComparer.Ordinal.Equals(identity.IndexContentId, snapshot.IndexContentId) ||
            identity.Ordinal < 0 || identity.Ordinal >= snapshot.Records.Count)
            throw new ArchiveException("InvalidIdentity", "The audio identity does not belong to this archive revision.");
        var resource = snapshot.Records[(int)identity.Ordinal];
        var volume = snapshot.Volumes.SingleOrDefault(candidate => candidate.Package == resource.Package);
        return new(identity, resource, volume);
    }

    public static AudioPayloadBinding Resolve(ArchiveSnapshot snapshot, MediaEntry media, bool original = false)
    {
        ArgumentNullException.ThrowIfNull(media);
        var logical = media.Identity ?? throw new ArchiveException("InvalidMediaIdentity", "The audio row has no stable logical identity.");
        var payload = media.PayloadIdentity ?? logical;
        var logicalOwner = ResolveOwner(snapshot, logical);
        var payloadOwner = ResolveOwner(snapshot, payload);
        var volume = payloadOwner.Volume;
        if (logicalOwner.Resource.Validity != ResourceValidity.Valid)
            throw new ArchiveException("InvalidLogicalOwner", "The audio declaration owner is not valid in this archive revision.");
        if (payloadOwner.Resource.Validity != ResourceValidity.Valid || volume is null || !volume.Stamp.Exists || volume.Stamp.Error is not null)
            throw new ArchiveException("SourceUnavailable", "The concrete audio payload owner is unavailable in this archive revision.");
        var expectedVolumeName = IndexLayout.VolumeName(Path.GetFileNameWithoutExtension(snapshot.IndexPath), payloadOwner.Resource.Package);
        var expectedVolumePath = Path.GetFullPath(Path.Combine(snapshot.SourceRoot, expectedVolumeName));
        var nameComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (!nameComparer.Equals(payloadOwner.Resource.VolumeName, expectedVolumeName) ||
            !nameComparer.Equals(volume.RelativeName, expectedVolumeName) ||
            !PathEquals(volume.FilePath, expectedVolumePath))
            throw new ArchiveException("InvalidPayloadSource", "The payload owner, package, and source volume do not agree.");
        var extent = original ? media.OriginalSlice ?? media.DidxExtent ?? media.Slice : media.Slice;
        if (payloadOwner.Resource.StoredSize < 0 || extent.Length <= 0)
            throw new ArchiveException("InvalidRange", "The audio payload has no bounded source extent.");
        if (!PathEquals(extent.FilePath, volume.FilePath) ||
            !IndexLayout.InBounds(extent.Offset, extent.Length, volume.Stamp.Length) ||
            !IndexLayout.InBounds(extent.Offset - payloadOwner.Resource.Offset, extent.Length, payloadOwner.Resource.StoredSize))
            throw new ArchiveException("InvalidPayloadExtent", "The audio payload extent is outside its concrete archive owner.");
        if (payload.MemberPath is null)
        {
            if (extent.Offset != payloadOwner.Resource.Offset || extent.Length != payloadOwner.Resource.StoredSize)
                throw new ArchiveException("InvalidPayloadExtent", "A top-level payload must match its owning archive resource extent.");
        }
        else if (payload.MemberPath == "midi")
        {
            if (payloadOwner.Resource.Type != 65)
                throw new ArchiveException("InvalidPayloadIdentity", "A MIDI member must be owned by a type-65 resource.");
        }
        else if (payload.MemberPath == "wem")
        {
            if (payloadOwner.Resource.Type != 1033)
                throw new ArchiveException("InvalidPayloadIdentity", "A WEM member must be owned by a type-1033 audio resource.");
        }
        else if (payload.MemberPath == "bank" || payload.MemberPath.StartsWith("media/", StringComparison.Ordinal))
        {
            if (payloadOwner.Resource.Type != 29)
                throw new ArchiveException("InvalidPayloadIdentity", "A bank or DIDX media member must be owned by a type-29 container.");
        }
        else
            throw new ArchiveException("InvalidPayloadIdentity", "The nested audio payload path is unsupported for physical reads.");
        return new(logical, payload, logicalOwner.Resource, payloadOwner.Resource, volume, extent);
    }

    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
