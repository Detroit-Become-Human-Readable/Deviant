using System.Globalization;
using Deviant.Formats.Audio.Model;

namespace Deviant.Formats.Audio;

public sealed partial class DetroitAudioSnapshotReader
{
    private static void ReadMidiResources(AudioCatalog catalog, PackageReader archive,
        IProgress<AudioReadProgress>? progress, Action ensureSourceRevision, CancellationToken token)
    {
        var resources = catalog.Resources.Where(resource => resource.Type == 65).ToArray();
        List<MediaEntry>? pendingMedia = progress is null ? null : [];
        for (int index = 0; index < resources.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var resource = resources[index];
            using var scope = archive.BeginMetadataReadScope(resource.Identity!, "MIDI");
            try
            {
                if (!resource.SourceReadable) throw new InvalidDataException("The MIDI source is unavailable.");
                var document = MidiReader.ReadWrapped(resource.Slice,
                    slice => archive.ReadAt(slice.FilePath, slice.Offset, checked((int)slice.Length)), token);
                var identity = resource.Identity! with { MemberPath = "midi" };
                var name = document.Names.FirstOrDefault()?.Text ?? resource.Id.ToString(CultureInfo.InvariantCulture);
                var entry = new MediaEntry
                {
                    Identity = identity, PayloadIdentity = identity, Key = identity.Key, Id = resource.Id,
                    Name = name, DisplayLabel = name, BankName = "MIDI", Category = "MIDI", Codec = "MIDI", Midi = document.Metadata,
                    Slice = document.Slice, OriginalSlice = document.Slice, State = MediaState.CompleteEmbedded,
                    Completeness = MediaCompleteness.Complete, ContainerValidity = ContainerValidity.Valid,
                    Availability = SourceAvailability.Available, Names = document.Names.ToList(),
                    Aliases = document.Names.Select(evidence => evidence.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                };
                archive.AttachToCurrentMetadataReadScope(entry);
                catalog.Media.Add(entry); catalog.Names.AddRange(entry.Names);
                pendingMedia?.Add(entry);
                if (index == 0 || (index + 1) % 512 == 0 || index + 1 == resources.Length)
                {
                    ensureSourceRevision(); scope.Dispose();
                    PublishSnapshot(progress, catalog, "Reading MIDI", index + 1, resources.Length, name, pendingMedia);
                    pendingMedia?.Clear();
                }
            }
            catch (Deviant.Core.ArchiveException error) when (error.Code == "SourceChanged") { throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException)
            {
                catalog.Diagnostics.Add(new("MalformedMidiResource", error.Message, resource.Key));
            }
        }
    }
}
