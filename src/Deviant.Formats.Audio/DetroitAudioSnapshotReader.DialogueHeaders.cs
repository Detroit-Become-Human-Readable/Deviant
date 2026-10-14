using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Audio.Model;
using ResourceRecord = Deviant.Formats.Audio.Model.ResourceRecord;

namespace Deviant.Formats.Audio;

public sealed partial class DetroitAudioSnapshotReader
{
    public const int DialogueHeaderAssociationVersion = 1;
    public const int MaximumDialogueHeaders = 100_000;

    private static Dictionary<uint, ResourceRecord[]> DialogueTargets(IEnumerable<ResourceRecord> rows) =>
        rows.GroupBy(row => row.Id).ToDictionary(group => group.Key, group => group.ToArray());

    private static ReferenceResolution ResolveDialogueTargets(IReadOnlyList<uint> ids,
        IReadOnlyDictionary<uint, ResourceRecord[]> rows, string kind)
    {
        if (ids.Count == 0) return new(ReferenceStatus.Missing, [], $"The header has no {kind} reference.");
        var candidates = new List<ResourceIdentity>();
        var missing = false;
        var ambiguous = ids.Count != 1;
        foreach (var id in ids)
        {
            var matches = rows.GetValueOrDefault(id, []);
            missing |= matches.Length == 0;
            ambiguous |= matches.Length > 1;
            candidates.AddRange(matches.Select(row => row.Identity).OfType<ResourceIdentity>());
        }
        var exact = candidates.DistinctBy(identity => identity.Key).ToArray();
        var status = ambiguous ? ReferenceStatus.Ambiguous : missing ? ReferenceStatus.Missing :
            exact.Length == 1 ? ReferenceStatus.Unique : ReferenceStatus.Unresolved;
        return new(status, exact, status == ReferenceStatus.Unique ? null : $"The header's {kind} reference is not unique.");
    }

    public Task<IReadOnlyList<DialogueHeaderAssociation>> ReadDialogueHeaderAssociationsAsync(
        ArchiveSnapshot snapshot, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Task.Run<IReadOnlyList<DialogueHeaderAssociation>>(() =>
        {
            token.ThrowIfCancellationRequested();
            ValidateSnapshotSources(snapshot);
            var resources = MapResources(snapshot);
            var headers = resources.Where(row => row.Type == 4091).ToArray();
            if (headers.Length > MaximumDialogueHeaders)
                throw new ArchiveException("DialogueHeaderLimit", "The archive contains too many dialogue headers.");
            var audio = DialogueTargets(resources.Where(row => row.Type == 1033));
            var events = DialogueTargets(resources.Where(row => row.Type == 1031));
            var associations = new List<DialogueHeaderAssociation>(headers.Length);
            using var archive = new PackageReader(snapshot, revisionProvider);
            foreach (var header in headers)
            {
                token.ThrowIfCancellationRequested();
                var identity = header.Identity ?? throw new ArchiveException("InvalidIdentity", "The header has no archive identity.");
                using var scope = archive.BeginMetadataReadScope(identity, "descriptor");
                ReferenceResolution audioResolution, eventResolution;
                if (!archive.TryReadResource(header, MaximumWrapperBytes, out var bytes) ||
                    !TryParseComContainer(bytes, out var references))
                {
                    audioResolution = eventResolution = new(ReferenceStatus.Unresolved, [], "The dialogue header reference table could not be read.");
                }
                else
                {
                    audioResolution = ResolveDialogueTargets(references.Where(reference => reference.Type == 1033)
                        .Select(reference => reference.Id).Distinct().ToArray(), audio, "audio");
                    eventResolution = ResolveDialogueTargets(references.Where(reference => reference.Type == 1031)
                        .Select(reference => reference.Id).Distinct().ToArray(), events, "event");
                }
                associations.Add(new(identity, audioResolution, eventResolution, scope.ReadDigests.ToArray(), scope.CoverageComplete));
            }
            ValidateSnapshotSources(snapshot);
            return associations.AsReadOnly();
        }, token);
    }
}
