using System.Globalization;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Audio.Model;
using ArchiveResourceRecord = Deviant.Core.ResourceRecord;
using ResourceRecord = Deviant.Formats.Audio.Model.ResourceRecord;
using ResourceIdentity = Deviant.Assets.ResourceIdentity;

namespace Deviant.Formats.Audio;

public enum SelectedAudioClassificationScope { EmbeddedBank, Standalone, Midi }

public sealed record SelectedAudioMediaResult(MediaEntry Media, BankInfo? Bank,
    SelectedAudioClassificationScope Scope);

public sealed partial class DetroitAudioSnapshotReader
{
    /// <summary>Reads one native media occurrence and its local classification inputs.</summary>
    public async Task<SelectedAudioMediaResult> ReadSelectedMediaAsync(ArchiveSnapshot snapshot,
        ResourceIdentity mediaIdentity, ResourceIdentity? bankDescriptorIdentity = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(mediaIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        var owner = AudioResourceBinding.ResolveOwner(snapshot, mediaIdentity);
        var resource = MapSelectedResource(snapshot, owner.Resource, owner.Volume);
        var verifier = new AudioMetadataVerifier(snapshot, revisionProvider);
        await verifier.VerifyIndexRevisionAsync(cancellationToken).ConfigureAwait(false);
        using var archive = new PackageReader(snapshot, revisionProvider);
        SelectedAudioMediaResult result;
        if (resource.Type == 29 && TryParseDidxMember(mediaIdentity.MemberPath, out var member))
            result = ReadSelectedBankMedia(snapshot, archive, mediaIdentity, resource, bankDescriptorIdentity, member, cancellationToken);
        else if (resource.Type == 1033 && mediaIdentity.MemberPath == "wem")
        {
            if (bankDescriptorIdentity is not null)
                throw new ArchiveException("BankOwnerMismatch", "Standalone media does not belong to a bank descriptor.");
            using var scope = archive.BeginMetadataReadScope(resource.Identity!, "WEM");
            if (!TryParseDialogueMedia(archive, resource, out var parsed))
                throw new ArchiveException("MalformedDialogueMedia", "The selected resource has no bounded audio wrapper.");
            var media = MakeMediaEntry(resource.Key, resource, parsed.Name, parsed.Language, parsed.MediaSlice,
                parsed.State, parsed.IsRiff, parsed.Codec, parsed.SampleRate, parsed.Channels, parsed.Duration, parsed.Validity,
                originalSlice: new(resource.Slice.FilePath, parsed.AudioOffset, parsed.AudioLength));
            archive.AttachToCurrentMetadataReadScope(media);
            scope.Dispose();
            result = new(media, null, SelectedAudioClassificationScope.Standalone);
        }
        else if (resource.Type == 65 && mediaIdentity.MemberPath == "midi")
        {
            if (bankDescriptorIdentity is not null)
                throw new ArchiveException("BankOwnerMismatch", "Archive MIDI does not belong to a bank descriptor.");
            using var scope = archive.BeginMetadataReadScope(resource.Identity!, "MIDI");
            var document = MidiReader.ReadWrapped(resource.Slice,
                slice => archive.ReadAt(slice.FilePath, slice.Offset, checked((int)slice.Length)), cancellationToken);
            var name = document.Names.FirstOrDefault()?.Text ?? resource.Id.ToString(CultureInfo.InvariantCulture);
            var media = new MediaEntry
            {
                Identity = mediaIdentity, PayloadIdentity = mediaIdentity, Key = mediaIdentity.Key, Id = resource.Id,
                Name = name, DisplayLabel = name, BankName = "MIDI", Category = "MIDI", Codec = "MIDI", Midi = document.Metadata,
                Slice = document.Slice, OriginalSlice = document.Slice, State = MediaState.CompleteEmbedded,
                Completeness = MediaCompleteness.Complete, ContainerValidity = ContainerValidity.Valid,
                Availability = SourceAvailability.Available, Names = document.Names.ToList(),
                Aliases = document.Names.Select(evidence => evidence.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
            archive.AttachToCurrentMetadataReadScope(media);
            scope.Dispose();
            result = new(media, null, SelectedAudioClassificationScope.Midi);
        }
        else throw new ArchiveException("SelectedMediaUnsupported", "This media identity requires a different source resolution path.");

        cancellationToken.ThrowIfCancellationRequested();
        AudioResourceBinding.Resolve(snapshot, result.Media);
        AudioResourceBinding.Resolve(snapshot, result.Media, original: true);
        if (!result.Media.MetadataReadCoverageComplete)
            throw new ArchiveException("SelectedMetadataLimit", "The selected media metadata exceeds its verification limits.");
        await verifier.VerifyMediaAsync(result.Media, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Resolves a stored bank group key in the selected archive generation.</summary>
    public static ResourceIdentity ResolveBankDescriptorIdentity(ArchiveSnapshot snapshot, string groupKey)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var prefix = snapshot.IndexContentId + ":resource:";
        if (groupKey is null || !groupKey.StartsWith(prefix, StringComparison.Ordinal) ||
            !long.TryParse(groupKey.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) ||
            ordinal < 0 || groupKey != prefix + ordinal.ToString(CultureInfo.InvariantCulture) ||
            snapshot.FindRecord(ordinal) is not { Type: 1022 })
            throw new ArchiveException("InvalidBankDescriptor", "The bank group key does not identify a descriptor in this archive generation.");
        return new(snapshot.IndexContentId, ordinal);
    }

    private static ResourceRecord MapSelectedResource(ArchiveSnapshot snapshot, ArchiveResourceRecord row, VolumeSource? volume)
    {
        if (row.Validity != ResourceValidity.Valid || volume is null || !volume.Stamp.Exists || volume.Stamp.Error is not null)
            throw new ArchiveException("SourceUnavailable", "The selected audio resource is unavailable at this archive revision.");
        return new ResourceRecord
        {
            Key = $"{snapshot.IndexContentId}:resource:{row.Ordinal}", Identity = new(snapshot.IndexContentId, row.Ordinal),
            IndexOrdinal = row.Ordinal, Type = row.Type, Id = row.Id, Flags = row.Flags, AlternativeSize = row.AlternativeSize,
            Package = checked((int)row.Package), Slice = new(volume.FilePath, row.Offset, row.StoredSize), SourceReadable = true
        };
    }

    private static bool TryParseDidxMember(string? path, out (uint Id, int RecordIndex) member)
    {
        member = default;
        if (path is null) return false;
        var parts = path.Split('/');
        if (parts.Length != 3 || parts[0] != "media" || parts[1].Length != 8 ||
            !uint.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 ||
            path != $"media/{id:X8}/{index.ToString(CultureInfo.InvariantCulture)}") return false;
        member = (id, index); return true;
    }

    private static SelectedAudioMediaResult ReadSelectedBankMedia(ArchiveSnapshot snapshot, PackageReader archive,
        ResourceIdentity mediaIdentity, ResourceRecord selectedOwner, ResourceIdentity? descriptorIdentity,
        (uint Id, int RecordIndex) member, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (descriptorIdentity is null || descriptorIdentity.MemberPath is not null)
            throw new ArchiveException("InvalidBankDescriptor", "Embedded media requires its exact bank descriptor.");
        var descriptorOwner = AudioResourceBinding.ResolveOwner(snapshot, descriptorIdentity);
        if (descriptorOwner.Resource.Type != 1022)
            throw new ArchiveException("InvalidBankDescriptor", "The selected bank owner is not a bank descriptor.");
        var descriptor = MapSelectedResource(snapshot, descriptorOwner.Resource, descriptorOwner.Volume);
        using var shared = ArchiveSourceSession.TryAcquire(snapshot)
            ?? throw new ArchiveException("SourceIndexUnavailable", "The archive source index must be open to resolve bank dependencies.");
        using var scope = archive.BeginMetadataReadScope(descriptorIdentity, "descriptor", bankScope: true);
        var bytes = archive.ReadResource(descriptor, MaximumWrapperBytes);
        if (!TryParseBankDescriptor(bytes, out var bankId, out var bankName, out var containerId))
            throw new ArchiveException("MalformedBankDescriptor", "The bank descriptor has no bounded container reference.");
        var containers = shared.Session.FindCandidates(containerId, 29);
        if (containers.Length != 1)
            throw new ArchiveException(containers.Length == 0 ? "MissingBankContainer" : "AmbiguousBankContainer",
                "The bank descriptor does not resolve to exactly one container.");
        if (containers[0].Ordinal != selectedOwner.IndexOrdinal)
            throw new ArchiveException("BankOwnerMismatch", "The bank descriptor does not own this media occurrence.");
        token.ThrowIfCancellationRequested();
        if (!archive.TryFindEmbeddedBank(selectedOwner.Slice, token, out var offset, out var length, out var diagnostic))
            throw new ArchiveException(diagnostic, "The selected container has no bounded bank payload.");
        var slice = new DataSlice(selectedOwner.Slice.FilePath, checked(selectedOwner.Slice.Offset + offset), length);
        if (!archive.TryReadBankHeader(slice.FilePath, slice.Offset, slice.Length, out _, out var actualId))
            throw new ArchiveException("MalformedBankPayload", "The selected bank has no valid header.");
        if (actualId != bankId)
            throw new ArchiveException("BankIdMismatch", "The bank payload ID differs from its descriptor ID.");
        var catalog = new AudioCatalog();
        var bank = ParseBankFromSlice(descriptor.Key, bankName, bankId, slice, descriptorIdentity, selectedOwner.Identity!,
            new Dictionary<uint, (string, ResourceRecord, bool)>(), catalog, archive, member, token);
        if (bank.Diagnostics.Any(item => item.Code is "BankChunkCountLimit" or "MalformedBankChunk" or "BankChunkTableTail" or
            "AmbiguousBankChunk" or
            "MissingBankHeader" or "UnsupportedBankVersion" or "MalformedDIDX" or "DIDXSizeLimit" or "MissingDATA" or
            "MalformedHIRC" or "MalformedHIRCObject" or "HIRCObjectCountLimit" or "HIRCUnparsedTail" or "HIRCSizeLimit" or
            "UnsupportedHIRC" or "DuplicateHircObjectId"))
            throw new ArchiveException("SelectedClassificationUnavailable", "The bank metadata could not be completely interpreted for this selection.");
        var media = catalog.Media.SingleOrDefault(item => item.Identity == mediaIdentity)
            ?? throw new ArchiveException("SelectedMediaMissing", "The DIDX record index and media ID do not identify an embedded occurrence.");
        var localEvidence = catalog.SourceDeclarations.Where(item => item.Declaration.Id == media.Id).ToArray();
        ClassifyEmbeddedMediaForBank(bank, [media], _ => localEvidence);
        archive.AttachToCurrentMetadataReadScope(bank);
        archive.AttachToCurrentMetadataReadScope(media);
        scope.Dispose();
        return new(media, bank, SelectedAudioClassificationScope.EmbeddedBank);
    }
}
