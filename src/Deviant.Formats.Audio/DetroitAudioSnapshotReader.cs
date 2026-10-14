using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Deviant.Core;
using Deviant.Formats.Audio.Model;
using ResourceRecord = Deviant.Formats.Audio.Model.ResourceRecord;
using ResourceIdentity = Deviant.Assets.ResourceIdentity;

namespace Deviant.Formats.Audio;

/// <summary>Reads the managed, metadata-only view of Detroit: Become Human PC audio resources.</summary>
public sealed partial class DetroitAudioSnapshotReader(ISourceRevisionProvider? revisionProvider = null)
{
    public const int EventMediaVersion = 2;
    private const int MaximumWrapperBytes = 16 * 1024 * 1024;
    private const int MaximumProbeBytes = 4 * 1024;
    private const uint WwiseVorbisPluginId = 0x00040001;
    private const byte LanguageSpecificSourceFlag = 1;
    private static readonly HashSet<uint> AudioResourceTypes = [29, 65, 1016, 1022, 1023, 1031, 1033, 4091];
    private static readonly byte[] BankContainerMagic = Encoding.ASCII.GetBytes("CSNDBKDT");
    private static readonly byte[] DialogueMagic = Encoding.ASCII.GetBytes("CSNDDATA");
    private static readonly byte[] EventMagic = Encoding.ASCII.GetBytes("CSNDEVNT");
    private static readonly byte[] LocalizationMagic = Encoding.ASCII.GetBytes("LOCALIZ_");
    private static readonly byte[] RiffMagic = Encoding.ASCII.GetBytes("RIFF");

    private static List<ResourceRecord> MapResources(ArchiveSnapshot snapshot)
    {
        var volumes = snapshot.Volumes.ToDictionary(volume => volume.Package);
        var occurrences = new Dictionary<(uint Type, uint Id), int>();
        var sourceRecords = snapshot.Records.Where(record => AudioResourceTypes.Contains(record.Type))
            .OrderBy(record => record.Ordinal).ToArray();
        var mapped = new List<ResourceRecord>(sourceRecords.Length);
        foreach (var record in sourceRecords)
        {
            var occurrenceKey = (record.Type, record.Id);
            var occurrence = occurrences.GetValueOrDefault(occurrenceKey);
            occurrences[occurrenceKey] = occurrence + 1;
            var path = volumes.TryGetValue(record.Package, out var volume) ? volume.FilePath : Path.Combine(snapshot.SourceRoot, record.VolumeName);
            var key = $"{snapshot.IndexContentId}:resource:{record.Ordinal}";
            var readable = record.Validity == ResourceValidity.Valid && volume?.Stamp.Exists == true && volume.Stamp.Error is null;
            var resource = new ResourceRecord
            {
                Key = key, Identity = new ResourceIdentity(snapshot.IndexContentId, record.Ordinal), IndexOrdinal = record.Ordinal,
                Type = record.Type, Id = record.Id, Flags = record.Flags,
                AlternativeSize = record.AlternativeSize, Package = checked((int)record.Package), Occurrence = occurrence,
                Slice = new DataSlice(path, record.Offset, record.StoredSize), SourceReadable = readable
            };
            mapped.Add(resource);
        }
        return mapped;
    }

    private void ValidateSnapshotSources(ArchiveSnapshot snapshot)
    {
        var provider = revisionProvider ?? new FileRevisionProvider();
        try
        {
            using var indexHandle = SourceIdentity.OpenPinned(snapshot.IndexPath);
            SourceIdentity.EnsureUnchanged(provider, indexHandle, snapshot.IndexPath, snapshot.IndexStamp);
        }
        catch (ArchiveException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new ArchiveException("SourceChanged", "The archive index is no longer available at the selected revision.", ex); }
        foreach (var volume in snapshot.Volumes)
        {
            if (!volume.Stamp.Exists) continue;
            try
            {
                using var handle = SourceIdentity.OpenPinned(volume.FilePath);
                SourceIdentity.EnsureUnchanged(provider, handle, volume.FilePath, volume.Stamp);
            }
            catch (ArchiveException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new ArchiveException("SourceChanged", "An audio source volume is no longer available at the selected archive revision.", ex); }
        }
    }

    public async Task<AudioSnapshotResult> ReadSnapshotAsync(ArchiveSnapshot snapshot,
        IProgress<AudioReadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var catalog = new AudioCatalog
        {
            SourcePath = snapshot.IndexPath,
            Fingerprint = snapshot.IndexContentId,
            IndexContentId = snapshot.IndexContentId,
            PhysicalSourceStamp = snapshot.PhysicalSourceStamp,
            IndexStamp = snapshot.IndexStamp,
            SourceVolumes = snapshot.Volumes.ToList(),
            IndexedResourceCount = snapshot.Records.Count,
            Resources = MapResources(snapshot)
        };
        ValidateSnapshotSources(snapshot);
        using (var archive = new PackageReader(snapshot, revisionProvider))
            await PopulateCatalogAsync(catalog, archive, progress, () => ValidateSnapshotSources(snapshot), cancellationToken).ConfigureAwait(false);
        ValidateSnapshotSources(snapshot);
        if (progress is not null)
        {
            var changedMedia = catalog.Media.ToArray();
            var changedKeys = changedMedia.Select(media => media.Identity).Where(identity => identity is not null)
                .Select(identity => identity!.Key).ToHashSet(StringComparer.Ordinal);
            var changedCopies = new List<MediaEntry>(changedMedia.Length);
            var finalSnapshot = CreateSnapshot(catalog, changedMediaKeys: changedKeys, changedMediaCopies: changedCopies);
            progress.Report(new AudioReadProgress("Audio ready", catalog.Media.Count, catalog.Media.Count,
                Snapshot: finalSnapshot)
            { ChangedMediaIdentities = changedMedia
                    .Select(media => media.Identity).Where(identity => identity is not null).Select(identity => identity!)
                    .Distinct().ToArray(), ChangedMedia = changedCopies });
        }
        return new(catalog, catalog.Diagnostics.AsReadOnly());
    }

    public async Task<BankInfo> ReadBankGraphAsync(ArchiveSnapshot snapshot, ResourceIdentity bankDescriptorIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(bankDescriptorIdentity);
        if (!StringComparer.Ordinal.Equals(bankDescriptorIdentity.IndexContentId, snapshot.IndexContentId) || bankDescriptorIdentity.MemberPath is not null)
            throw new ArchiveException("InvalidIdentity", "The bank descriptor identity does not belong to this archive snapshot.");
        ValidateSnapshotSources(snapshot);
        var resources = MapResources(snapshot);
        var descriptor = resources.SingleOrDefault(resource => resource.IndexOrdinal == bankDescriptorIdentity.Ordinal && resource.Type == 1022)
            ?? throw new ArchiveException("MissingBankDescriptor", "The selected bank descriptor is absent from the archive snapshot.");
        if (!descriptor.SourceReadable) throw new ArchiveException("SourceUnavailable", "The bank descriptor is not readable at the selected snapshot revision.");
        using var archive = new PackageReader(snapshot, revisionProvider);
        using var metadataReadScope = archive.BeginMetadataReadScope(descriptor.Identity!, "descriptor", bankScope: true);
        var bytes = archive.ReadResource(descriptor, MaximumWrapperBytes);
        if (!TryParseBankDescriptor(bytes, out var bankId, out var bankName, out var containerId))
            throw new ArchiveException("MalformedBankDescriptor", "The bank descriptor did not contain a bounded name, ID, and container reference.");
        var containers = resources.Where(resource => resource.Type == 29 && resource.Id == containerId).ToArray();
        if (containers.Length != 1)
            throw new ArchiveException(containers.Length == 0 ? "MissingBankContainer" : "AmbiguousBankContainer",
                "The bank descriptor does not resolve to exactly one archive container record.");
        var container = containers[0];
        if (!archive.TryFindEmbeddedBank(container.Slice, cancellationToken, out var bankOffset, out var bankLength, out var diagnostic))
            throw new ArchiveException(diagnostic, "The referenced container does not contain one bounded bank payload.");
        if (!archive.TryReadBankHeader(container.Slice.FilePath, checked(container.Slice.Offset + bankOffset), bankLength, out _, out var actualId))
            throw new ArchiveException("MalformedBankPayload", "The selected bank payload has no valid BKHD header.");
        var catalog = new AudioCatalog { SourcePath = snapshot.IndexPath, Fingerprint = snapshot.IndexContentId,
            IndexContentId = snapshot.IndexContentId, PhysicalSourceStamp = snapshot.PhysicalSourceStamp,
            IndexStamp = snapshot.IndexStamp,
            SourceVolumes = snapshot.Volumes.ToList() };
        var slice = new DataSlice(container.Slice.FilePath, checked(container.Slice.Offset + bankOffset), bankLength);
        var graph = ParseBankFromSlice(descriptor.Key, actualId == bankId ? bankName : actualId.ToString(CultureInfo.InvariantCulture),
            actualId, slice, descriptor.Identity!, container.Identity!, new Dictionary<uint, (string, ResourceRecord, bool)>(), catalog, archive);
        if (actualId != bankId)
            graph.Diagnostics.Add(new Diagnostic("BankIdMismatch", "The bank header ID differs from its descriptor ID; the descriptor name was not attached.", descriptor.Key));
        ValidateSnapshotSources(snapshot);
        archive.AttachToCurrentMetadataReadScope(graph);
        foreach (var media in catalog.Media.Where(item => item.BankKey == graph.Key))
            archive.AttachToCurrentMetadataReadScope(media);
        return graph;
    }

    private static async Task PopulateCatalogAsync(AudioCatalog catalog, PackageReader archive,
        IProgress<AudioReadProgress>? progress, Action ensureSourceRevision, CancellationToken token)
    {
        var bankContainers = catalog.Resources.Where(resource => resource.Type == 29)
            .GroupBy(resource => resource.Id).ToDictionary(group => group.Key, group => group.ToArray());
        var bankNameById = new Dictionary<uint, (string Name, ResourceRecord Resource, bool Verified)>();
        var bankDescriptors = catalog.Resources.Where(resource => resource.Type == 1022).ToList();
        var bankNameEvidenceByDescriptor = new Dictionary<string, int>(StringComparer.Ordinal);
        var namedEvents = new Dictionary<uint, string>();
        var dialogueEvents = new Dictionary<uint, string>();
        var dialogueResourceNames = new Dictionary<uint, string>();
        var relationshipReadBaselines = new Dictionary<string, (List<AudioMetadataReadDigest> Digests, bool Complete)>(StringComparer.Ordinal);
        var dialogueEventByResource = new Dictionary<uint, AudioEvent>();
        var eventRecords = catalog.Resources.Where(resource => resource.Type is 1023 or 1031).ToList();
        var dialogueRecordsById = eventRecords.Where(resource => resource.Type == 1031)
            .GroupBy(resource => resource.Id).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var headerRecords = catalog.Resources.Where(resource => resource.Type == 4091).ToList();
        var audioResources = catalog.Resources.Where(resource => resource.Type == 1033).ToList();
        var hasLocalizationResources = catalog.Resources.Any(resource => resource.Type == 1016);
        var resourceCountsByTypeAndId = catalog.Resources.GroupBy(resource => (resource.Type, resource.Id))
            .ToDictionary(group => group.Key, group => group.Count());
        var eventIndex = 0;
        var completed = 0;
        var preclassifiedBankKeys = new HashSet<string>(StringComparer.Ordinal);
        List<MediaEntry>? pendingBankMedia = progress is null ? null : [];

        foreach (var descriptor in bankDescriptors)
        {
            token.ThrowIfCancellationRequested();
            var firstBankMediaIndex = catalog.Media.Count;
            var firstBankDeclarationIndex = catalog.SourceDeclarations.Count;
            using var metadataReadScope = archive.BeginMetadataReadScope(descriptor.Identity!, "descriptor", bankScope: true);
            try
            {
                var bytes = archive.ReadResource(descriptor, MaximumWrapperBytes);
                if (!TryParseBankDescriptor(bytes, out var bankId, out var bankName, out var containerId))
                {
                    catalog.Diagnostics.Add(new Diagnostic("MalformedBankDescriptor", "The bank descriptor did not contain a bounded name, ID, and data-container reference.", descriptor.Key));
                    continue;
                }
                var verified = Fnv1LowerUtf8(bankName) == bankId;
                bankNameEvidenceByDescriptor[descriptor.Key] = catalog.Names.Count;
                catalog.Names.Add(new NameEvidence(bankName, "WwiseBank", bankId, NameKind.Stored, descriptor.Key, descriptor.Slice.Offset));
                if (bankNameById.TryGetValue(bankId, out var collision) && !string.Equals(collision.Name, bankName, StringComparison.Ordinal))
                    catalog.Diagnostics.Add(new Diagnostic("DuplicateBankId", "Multiple stored bank names reference the same bank ID.", descriptor.Key));
                else
                    bankNameById[bankId] = (bankName, descriptor, verified);

                var containers = bankContainers.GetValueOrDefault(containerId, []);
                if (containers.Length != 1)
                {
                    var code = containers.Length == 0 ? "MissingBankContainer" : "AmbiguousBankContainer";
                    catalog.Diagnostics.Add(new Diagnostic(code, containers.Length == 0
                        ? $"The descriptor refers to missing data-container resource {containerId}."
                        : $"The descriptor reference to data-container resource {containerId} is ambiguous across {containers.Length} archive records.", descriptor.Key));
                    catalog.Banks.Add(new BankInfo { Key = descriptor.Key, Identity = descriptor.Identity, Id = bankId, Name = bankName,
                        Slice = new DataSlice(descriptor.Slice.FilePath, descriptor.Slice.Offset, 0),
                        Diagnostics = [new Diagnostic(code, "The descriptor's data-container reference did not resolve uniquely.", descriptor.Key)] });
                    continue;
                }
                var container = containers[0];

                if (!archive.TryFindEmbeddedBank(container.Slice, token, out var bankOffset, out var bankLength, out var bankDiagnostic))
                {
                    var diagnostic = new Diagnostic(bankDiagnostic, "The referenced data container did not contain a complete, structurally valid BKHD bank payload.", descriptor.Key);
                    catalog.Diagnostics.Add(diagnostic);
                    catalog.Banks.Add(new BankInfo { Key = descriptor.Key, Identity = descriptor.Identity, Id = bankId, Name = bankName,
                        Slice = new DataSlice(container.Slice.FilePath, container.Slice.Offset, 0), Diagnostics = [diagnostic] });
                    continue;
                }

                if (!archive.TryReadBankHeader(container.Slice.FilePath, checked(container.Slice.Offset + bankOffset), bankLength, out var actualVersion, out var actualId))
                {
                    catalog.Diagnostics.Add(new Diagnostic("MalformedBankPayload", "The selected CSNDBKDT member did not contain a valid BKHD chunk.", descriptor.Key));
                    continue;
                }
                var name = actualId == bankId ? bankName : actualId.ToString(CultureInfo.InvariantCulture);
                var bankKey = descriptor.Key;
                var dataSlice = new DataSlice(container.Slice.FilePath, checked(container.Slice.Offset + bankOffset), bankLength);
                var bank = ParseBankFromSlice(bankKey, name, actualId, dataSlice, descriptor.Identity!, container.Identity!, bankNameById, catalog, archive);
                var localDeclarations = catalog.SourceDeclarations.Skip(firstBankDeclarationIndex)
                    .GroupBy(source => source.Declaration.Id)
                    .ToDictionary(group => group.Key, group => group.ToArray());
                var localEmbeddedMedia = catalog.Media.Skip(firstBankMediaIndex)
                    .Where(media => media.BankKey == bank.Key && !media.Key.Contains(":source:", StringComparison.Ordinal)).ToArray();
                ClassifyEmbeddedMediaForBank(bank, localEmbeddedMedia,
                    mediaId => localDeclarations.GetValueOrDefault(mediaId, []));
                preclassifiedBankKeys.Add(bank.Key);
                archive.AttachToCurrentMetadataReadScope(bank);
                foreach (var media in catalog.Media.Where(item => item.BankKey == bank.Key))
                    archive.AttachToCurrentMetadataReadScope(media);
                if (actualId != bankId)
                    bank.Diagnostics.Add(new Diagnostic("BankIdMismatch", $"Stored descriptor ID {bankId} disagrees with BKHD ID {actualId}; the descriptor name was not attached to the bank.", descriptor.Key));
                else
                {
                    if (verified && bankNameEvidenceByDescriptor.TryGetValue(descriptor.Key, out var evidenceIndex))
                        catalog.Names[evidenceIndex] = catalog.Names[evidenceIndex] with { Kind = NameKind.Verified, Confidence = "High", HashMethod = "FNV-1 lower UTF-8" };
                }
                EnrichEventsFromBank(catalog, bank, namedEvents);
                catalog.Banks.Add(bank);
                if (pendingBankMedia is not null)
                    pendingBankMedia.AddRange(catalog.Media.Skip(firstBankMediaIndex));
                completed++;

                if (completed == 1 || completed % 32 == 0 || completed == bankDescriptors.Count)
                {
                    ensureSourceRevision();
                    metadataReadScope.Dispose();
                    PublishSnapshot(progress, catalog, "Reading banks", completed, bankDescriptors.Count, bankName,
                        pendingBankMedia);
                    pendingBankMedia?.Clear();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                catalog.Diagnostics.Add(new Diagnostic("BankParseError", ex.Message, descriptor.Key));
            }
            if ((++eventIndex & 0x3F) == 0)
                progress?.Report(new AudioReadProgress("Reading banks", eventIndex, bankDescriptors.Count, descriptor.Key));
        }

        foreach (var resource in eventRecords)
        {
            token.ThrowIfCancellationRequested();
            using var metadataReadScope = resource.Identity is null ? null : archive.BeginMetadataReadScope(resource.Identity, "descriptor");
            if (archive.TryReadResource(resource, MaximumWrapperBytes, out var bytes) && TryParseEventResource(bytes, resource.Type == 1031, out var wwiseId, out var name))
            {
                if (resource.Type == 1031)
                {
                    dialogueEvents[wwiseId] = name;
                    dialogueResourceNames[resource.Id] = name;
                }
                else namedEvents[wwiseId] = name;
                var key = resource.Type == 1031
                    ? $"event:dialogue-resource:{resource.Id:X8}:{resource.Occurrence}"
                    : $"event:sound:{wwiseId:X8}:{resource.Occurrence}";
                var eventRecord = new AudioEvent { Key = key, Identity = resource.Identity, Id = wwiseId, Name = name, IsDialogue = resource.Type == 1031 };
                eventRecord.Names.Add(new NameEvidence(name, "WwiseEvent", wwiseId, NameKind.Stored, resource.Key, resource.Slice.Offset));
                catalog.Names.AddRange(eventRecord.Names);
                var existing = resource.Type == 1023
                    ? catalog.Events.Where(item => item.Key == key || (!item.IsDialogue && item.Id == wwiseId && item.BankKey.Length > 0)).ToList()
                    : catalog.Events.Where(item => item.Key == key).ToList();
                if (existing.Count == 1)
                {
                    existing[0].Name = name;
                    if (existing[0].BankKey.Length == 0) existing[0].Key = key;
                    existing[0].Names.AddRange(eventRecord.Names);
                    eventRecord = existing[0];
                }
                else
                {
                    if (existing.Count > 1)
                        catalog.Diagnostics.Add(new Diagnostic("AmbiguousSoundEventName", "A stored sound-event name matches multiple HIRC event objects and was not linked.", resource.Key));
                    catalog.Events.Add(eventRecord);
                }
                if (resource.Type == 1031) dialogueEventByResource[resource.Id] = eventRecord;
                if (metadataReadScope is not null)
                    relationshipReadBaselines[resource.Key] = (metadataReadScope.ReadDigests, metadataReadScope.CoverageComplete);
            }
            else
                catalog.Diagnostics.Add(new Diagnostic("MalformedEventMetadata", "A CSNDEVNT resource could not be parsed using its declared record lengths.", resource.Key));
        }

        // The 4091 COM_CONT records are the authoritative bridge from named dialogue headers to
        // their type-1033 audio resources and type-1031 dialogue-event resources.
        if (headerRecords.Count > MaximumDialogueHeaders) throw new ArchiveException("DialogueHeaderLimit", "The archive contains too many dialogue headers.");
        var audioTargets = DialogueTargets(audioResources);
        var eventTargets = DialogueTargets(eventRecords.Where(row => row.Type == 1031));
        var audioToDialogue = new Dictionary<uint, List<(uint EventId, string HeaderName, ResourceRecord Header)>>();
        foreach (var header in headerRecords)
        {
            token.ThrowIfCancellationRequested();
            using var metadataReadScope = header.Identity is null ? null : archive.BeginMetadataReadScope(header.Identity, "descriptor");
            if (!archive.TryReadResource(header, MaximumWrapperBytes, out var bytes) || !TryParseComContainer(bytes, out var references))
            {
                catalog.Diagnostics.Add(new Diagnostic("MalformedDialogueHeader", "The dialogue header did not contain a valid COM_CONT reference table.", header.Key));
                if (header.Identity is { } malformedIdentity)
                {
                    var unresolved = new Deviant.Assets.ReferenceResolution(Deviant.Assets.ReferenceStatus.Unresolved, [], "The dialogue header reference table could not be read.");
                    catalog.DialogueHeaderAssociations.Add(new(malformedIdentity, unresolved, unresolved,
                        metadataReadScope?.ReadDigests.ToArray() ?? [], metadataReadScope?.CoverageComplete == true));
                }
                continue;
            }
            var audioIds = references.Where(reference => reference.Type == 1033).Select(reference => reference.Id).Distinct().ToArray();
            var eventIds = references.Where(reference => reference.Type == 1031).Select(reference => reference.Id).Distinct().ToArray();
            if (header.Identity is { } headerIdentity)
                catalog.DialogueHeaderAssociations.Add(new(headerIdentity,
                    ResolveDialogueTargets(audioIds, audioTargets, "audio"),
                    ResolveDialogueTargets(eventIds, eventTargets, "event"),
                    metadataReadScope?.ReadDigests.ToArray() ?? [], metadataReadScope?.CoverageComplete == true));
            var headerName = string.Empty;
            if (audioIds.Length != 1 || eventIds.Length != 1)
            {
                catalog.Diagnostics.Add(new Diagnostic("DialogueReferenceCardinality", "A dialogue header did not resolve to exactly one audio resource and one dialogue event.", header.Key));
                continue;
            }
            if (resourceCountsByTypeAndId.GetValueOrDefault((1033u, audioIds[0])) != 1 ||
                resourceCountsByTypeAndId.GetValueOrDefault((1031u, eventIds[0])) != 1)
            {
                catalog.Diagnostics.Add(new Diagnostic("DialogueReferenceAmbiguous", "A dialogue header reference matches multiple same-type archive records and was not linked.", header.Key));
                continue;
            }
            if (!audioToDialogue.TryGetValue(audioIds[0], out var links)) audioToDialogue[audioIds[0]] = links = [];
            links.Add((eventIds[0], headerName, header));
            if (metadataReadScope is not null)
                relationshipReadBaselines[header.Key] = (metadataReadScope.ReadDigests, metadataReadScope.CoverageComplete);
        }

        catalog.DialogueHeaderVersion = DialogueHeaderAssociationVersion;
        if (hasLocalizationResources)
            catalog.Diagnostics.Add(new Diagnostic("LocalizationDeferred", "Localization resources are retained for the dedicated localization reader and are not scanned during audio enrichment."));
        var audioIndex = 0;
        List<MediaEntry>? pendingDialogueMedia = progress is null ? null : [];
        foreach (var resource in audioResources)
        {
            token.ThrowIfCancellationRequested();
            audioIndex++;
            using var metadataReadScope = resource.Identity is null ? null : archive.BeginMetadataReadScope(resource.Identity, "WEM");
            try
            {
                if (!TryParseDialogueMedia(archive, resource, out var parsed))
                {
                    catalog.Diagnostics.Add(new Diagnostic("MalformedDialogueMedia", "The CSNDDATA resource did not contain a valid stored name and bounded audio extent.", resource.Key));
                    continue;
                }

                var entry = MakeMediaEntry(resource.Key, resource, parsed.Name, parsed.Language, parsed.MediaSlice,
                    parsed.State, parsed.IsRiff, parsed.Codec, parsed.SampleRate, parsed.Channels, parsed.Duration, parsed.Validity,
                    originalSlice: new DataSlice(resource.Slice.FilePath, parsed.AudioOffset, parsed.AudioLength));
                if (metadataReadScope is not null) archive.AttachToCurrentMetadataReadScope(entry);
                catalog.Names.AddRange(entry.Names);
                if (audioToDialogue.TryGetValue(resource.Id, out var links))
                {
                    var associationDigests = new List<AudioMetadataReadDigest>();
                    var associationCoverageComplete = true;
                    foreach (var link in links)
                    {
                        var eventId = link.EventId;
                        var eventRecord = dialogueRecordsById.GetValueOrDefault(eventId);
                        if (eventRecord is not null)
                        {
                            if (relationshipReadBaselines.TryGetValue(eventRecord.Key, out var eventBaseline))
                            { associationDigests.AddRange(eventBaseline.Digests); associationCoverageComplete &= eventBaseline.Complete; }
                            else associationCoverageComplete = false;
                        }
                        if (relationshipReadBaselines.TryGetValue(link.Header.Key, out var headerBaseline))
                        { associationDigests.AddRange(headerBaseline.Digests); associationCoverageComplete &= headerBaseline.Complete; }
                        else associationCoverageComplete = false;
                        var eventName = dialogueResourceNames.GetValueOrDefault(eventId, eventId.ToString(CultureInfo.InvariantCulture));
                        dialogueEventByResource.TryGetValue(eventId, out var linkedEvent);
                        entry.EventKeys.Add(linkedEvent?.Key ?? $"event:dialogue-resource:{eventId:X8}:0");
                        entry.Aliases.Add(eventName);
                        if (!string.IsNullOrWhiteSpace(link.HeaderName))
                            entry.Names.Add(new NameEvidence(link.HeaderName, "DialogueHeader", resource.Id, NameKind.Stored, link.Header.Key, link.Header.Slice.Offset));
                    }
                    var merged = MergeMetadataReadDigests(entry.MetadataReadDigests, associationDigests);
                    entry.MetadataReadCoverageComplete = associationCoverageComplete && merged is not null;
                    entry.MetadataReadDigests = merged ?? [];
                }
                catalog.Media.Add(entry);
                pendingDialogueMedia?.Add(entry);
                if (audioIndex == 1 || audioIndex % 4096 == 0 || audioIndex == audioResources.Count)
                {
                    ensureSourceRevision();
                    metadataReadScope?.Dispose();
                    PublishSnapshot(progress, catalog, "Reading dialogue media", audioIndex, audioResources.Count,
                        entry.DisplayName, pendingDialogueMedia);
                    pendingDialogueMedia?.Clear();
                }
                else if (audioIndex % 512 == 0)
                    progress?.Report(new AudioReadProgress("Reading dialogue media", audioIndex, audioResources.Count, entry.DisplayName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                catalog.Diagnostics.Add(new Diagnostic("DialogueMediaParseError", ex.Message, resource.Key));
            }
        }

        RecoverExecutableNames(catalog, token, progress);
        ResolveAllBankMedia(catalog, token, progress, ensureSourceRevision, preclassifiedBankKeys);
        ReadMidiResources(catalog, archive, progress, ensureSourceRevision, token);
        RetainEventMediaLinks(catalog, token);
        if (catalog.Banks.Any(bank => bank.IndexedObjectCount > 0))
            catalog.Diagnostics.Add(new Diagnostic("EventGraphDeferred", "HIRC playback graphs are loaded on demand for a selected bank."));

        MarkNameCollisions(catalog);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Rebuilds event links from cached bank extents and their verified metadata.</summary>
    public async Task<AudioSnapshotResult> ReadCachedEventLinksAsync(ArchiveSnapshot snapshot, AudioCatalog cached,
        IProgress<AudioReadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(cached);
        if (cached.IndexContentId != snapshot.IndexContentId || cached.PhysicalSourceStamp != snapshot.PhysicalSourceStamp)
            throw new ArchiveException("InvalidIdentity", "The cached audio catalog belongs to another archive revision.");
        ValidateSnapshotSources(snapshot);
        var catalog = CreateSnapshot(cached, copyMediaMetadata: false);
        if (cached.EventMediaVersion >= 1)
        {
            AudioEventNameRecovery.Apply(catalog, cancellationToken);
            AudioMediaNaming.Apply(catalog, cancellationToken);
            MarkNameCollisions(catalog);
            catalog.EventMediaVersion = EventMediaVersion;
            ValidateSnapshotSources(snapshot);
            return new(catalog, catalog.Diagnostics.AsReadOnly());
        }
        var verifier = new AudioMetadataVerifier(snapshot, revisionProvider);
        using var archive = new PackageReader(snapshot, revisionProvider);
        var completed = 0;
        foreach (var bank in catalog.Banks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunks = bank.Chunks.Where(chunk => chunk.Tag == "HIRC").ToArray();
            if (bank.Version is 118 or 120 && chunks.Length == 1 && chunks[0].Slice.Length <= MaximumWrapperBytes)
            {
                var hirc = chunks[0].Slice;
                if (bank.Identity is null || !StringComparer.OrdinalIgnoreCase.Equals(hirc.FilePath, bank.Slice.FilePath) ||
                    hirc.Offset < bank.Slice.Offset || hirc.Length < 0 ||
                    !IndexLayout.InBounds(hirc.Offset - bank.Slice.Offset, hirc.Length, bank.Slice.Length))
                    throw new ArchiveException("InvalidRange", "The cached bank metadata is outside its bank payload.");
                await verifier.VerifyBankAsync(bank, cancellationToken).ConfigureAwait(false);
                var offset = hirc.Offset;
                var bytes = archive.ReadAt(bank.Slice.FilePath, offset, checked((int)hirc.Length), "HIRC");
                ParseHirc(bank, bytes, new AudioCatalog(), offset);
            }
            progress?.Report(new AudioReadProgress("Updating audio names", ++completed, catalog.Banks.Count, bank.Name));
        }
        RetainEventMediaLinks(catalog, cancellationToken);
        MarkNameCollisions(catalog);
        ValidateSnapshotSources(snapshot);
        return new(catalog, catalog.Diagnostics.AsReadOnly());
    }

    private static void RetainEventMediaLinks(AudioCatalog catalog, CancellationToken token)
    {
        CatalogRelationships.Build(catalog, token);
        AudioEventNameRecovery.Apply(catalog, token);
        AudioMediaNaming.Apply(catalog, token);
        foreach (var bank in catalog.Banks) bank.Objects.Clear();
        catalog.EventMediaVersion = EventMediaVersion;
    }

    private static bool TryParseComContainer(ReadOnlySpan<byte> bytes, out List<ResourceReference> references)
    {
        references = [];
        if (bytes.Length < 20 || !bytes[..8].SequenceEqual("COM_CONT"u8)) return false;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..12]);
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..20]);
        if (version != 6 || count > 4096 || payloadLength > bytes.Length - 16 || 20L + count * 9L > bytes.Length) return false;
        var position = 20;
        for (var i = 0; i < count; i++)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(position, 4));
            var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(position + 4, 4));
            references.Add(new ResourceReference(type, id));
            position += 9;
        }
        return true;
    }

    private static bool TryParseBankDescriptor(ReadOnlySpan<byte> bytes, out uint bankId, out string bankName, out uint containerId)
    {
        bankId = 0; bankName = string.Empty; containerId = 0;
        if (!TryParseComContainer(bytes, out var refs)) return false;
        var containerRefs = refs.Where(r => r.Type == 29).Select(r => r.Id).Distinct().ToArray();
        if (containerRefs.Length != 1) return false;
        containerId = containerRefs[0];
        var marker = bytes.IndexOf("CSNDBNK_"u8);
        if (marker < 0 || marker + 16 > bytes.Length) return false;
        var pos = marker + 8;
        var recordVersion = ReadUInt32(bytes, ref pos);
        var recordSize = ReadUInt32(bytes, ref pos);
        if (recordVersion != 2 || recordSize > bytes.Length - pos) return false;
        var end = pos + checked((int)recordSize);
        if (!TryReadLengthPrefixedAscii(bytes, ref pos, end, 64, out var idText) ||
            !TryReadLengthPrefixedAscii(bytes, ref pos, end, 512, out bankName) ||
            !uint.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out bankId)) return false;
        return !string.IsNullOrWhiteSpace(bankName);
    }

    private static bool TryParseEventResource(ReadOnlySpan<byte> bytes, bool dialogue, out uint eventId, out string name)
    {
        eventId = 0; name = string.Empty;
        var marker = bytes.IndexOf(EventMagic);
        if (marker < 0 || marker + 16 > bytes.Length) return false;
        var hasQzip = bytes.Length >= 5 && bytes[..5].SequenceEqual("QZIP\0"u8) && marker == 5;
        var hasContainer = bytes.Length >= 16 && bytes[..8].SequenceEqual("COM_CONT"u8);
        if (!hasQzip && !hasContainer) return false;
        if (hasContainer)
        {
            var containerVersion = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8, 4));
            var containerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12, 4));
            if (containerVersion is not (5 or 6) || containerSize > bytes.Length - 16) return false;
        }
        var pos = marker + EventMagic.Length;
        var version = ReadUInt32(bytes, ref pos);
        var declared = ReadUInt32(bytes, ref pos);
        if (version > 1 || declared > bytes.Length - pos + (version == 0 ? 4L : 0L)) return false;
        var end = checked((int)Math.Min(bytes.Length, pos + (long)declared));
        if (!TryReadLengthPrefixedAscii(bytes, ref pos, end, 64, out var idText) ||
            !TryReadLengthPrefixedAscii(bytes, ref pos, end, 4096, out name) ||
            !uint.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out eventId)) return false;
        return true;
    }

    private static bool TryParseComContainerName(ReadOnlySpan<byte> bytes, byte[] targetTag, out string name)
    {
        name = string.Empty;
        var marker = bytes.IndexOf(targetTag);
        if (marker < 0 || marker + 20 > bytes.Length) return false;
        var pos = marker + targetTag.Length;
        _ = ReadUInt32(bytes, ref pos);
        var declared = ReadUInt32(bytes, ref pos);
        if (declared > bytes.Length - pos) return false;
        var end = pos + checked((int)declared);
        _ = TryReadLengthPrefixedAscii(bytes, ref pos, end, 64, out _);
        return TryReadLengthPrefixedAscii(bytes, ref pos, end, 4096, out name);
    }

    private static bool TryReadLengthPrefixedAscii(ReadOnlySpan<byte> bytes, ref int position, int end, int maximum, out string text)
    {
        text = string.Empty;
        if (position > end - 4) return false;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(position, 4));
        position += 4;
        if (length > maximum || length > end - position) return false;
        var value = bytes.Slice(position, (int)length);
        if (value.IndexOfAnyInRange((byte)0x80, byte.MaxValue) >= 0) return false;
        text = Encoding.ASCII.GetString(value);
        position += (int)length;
        return true;
    }

    private static bool TryParseDialogueMedia(PackageReader archive, ResourceRecord resource, out ParsedDialogue parsed)
    {
        parsed = default;
        if (resource.Slice.Length < 64) return false;
        var headerLength = checked((int)Math.Min(resource.Slice.Length, 4096));
        var header = archive.ReadAt(resource.Slice.FilePath, resource.Slice.Offset, headerLength);
        return TryParseDialogueHeader(header, resource, out parsed) &&
            ProbeDialoguePayload(archive, resource, parsed, out parsed);
    }

    private static bool TryParseDialogueHeader(ReadOnlySpan<byte> header, ResourceRecord resource, out ParsedDialogue parsed)
    {
        parsed = default;
        if (header.Length < 21 || !header[..5].SequenceEqual("QZIP\0"u8)) return false;
        var marker = header.IndexOf(DialogueMagic);
        if (marker != 5 || marker + 16 > header.Length) return false;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(marker + DialogueMagic.Length, 4));
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(marker + DialogueMagic.Length + 4, 4));
        if (version != 0 || declared > resource.Slice.Length - 21 || declared == 0) return false;
        var fixedNameOffset = marker + 0x2C;
        if (fixedNameOffset + 4 > header.Length) return false;
        var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(fixedNameOffset, 4));
        if (nameLength is 0 or > 512 || fixedNameOffset + 4L + nameLength + 4 > header.Length) return false;
        var name = Encoding.UTF8.GetString(header.Slice(fixedNameOffset + 4, (int)nameLength));
        if (name.Any(char.IsControl)) return false;
        var audioLengthOffset = fixedNameOffset + 4 + (int)nameLength;
        var audioLength = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(audioLengthOffset, 4));
        var audioRelative = checked(audioLengthOffset + 4);
        if (audioLength == 0 || audioRelative > resource.Slice.Length || audioLength > resource.Slice.Length - audioRelative)
            return false;
        var audioOffset = checked(resource.Slice.Offset + audioRelative);
        if (audioRelative + audioLength > 21L + declared) return false;
        parsed = new ParsedDialogue(name, ParseLanguage(name), new DataSlice(resource.Slice.FilePath, audioOffset, audioLength), audioOffset, audioLength,
            MediaState.Unsupported, false, "Unknown", 0, 0, null, ContainerValidity.Unsupported);
        return true;
    }

    private static bool ProbeDialoguePayload(PackageReader archive, ResourceRecord resource, ParsedDialogue parsed, out ParsedDialogue result)
    {
        result = parsed;
        var probe = archive.ProbeWem(parsed.MediaSlice.FilePath, parsed.AudioOffset, parsed.AudioLength);
        result = parsed with { MediaSlice = probe.Slice with { FilePath = parsed.MediaSlice.FilePath }, State = probe.State, IsRiff = probe.IsRiff, Codec = probe.Codec, SampleRate = probe.SampleRate, Channels = probe.Channels, Duration = probe.Duration, Validity = probe.Validity };
        return probe.Slice.Length > 0;
    }

    private static ParsedProbe ProbeWem(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long offset, long length,
        Action<long, byte[]>? metadataRead = null)
    {
        if (length < 8) return new ParsedProbe(new DataSlice(string.Empty, offset, Math.Max(0, length)), MediaState.Malformed, false, "Unknown", 0, 0, null, ContainerValidity.Truncated);
        var probeLength = (int)Math.Min(length, MaximumProbeBytes);
        var bytes = new byte[probeLength];
        ReadAtExactly(handle, offset, bytes, metadataRead);
        if (!bytes.AsSpan(0, 4).SequenceEqual(RiffMagic))
            return new ParsedProbe(new DataSlice(string.Empty, offset, length), MediaState.Unsupported, false, "Unknown", 0, 0, null, ContainerValidity.Unsupported);
        var riffLength = (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) + 8;
        if (riffLength < 12 || riffLength > length)
            return new ParsedProbe(new DataSlice(string.Empty, offset, length), MediaState.Malformed, true, "Unknown", 0, 0, null, riffLength > length ? ContainerValidity.Truncated : ContainerValidity.Malformed);

        if (!bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return new ParsedProbe(new DataSlice(string.Empty, offset, length), MediaState.Malformed, true, "Unknown", 0, 0, null, ContainerValidity.Malformed);
        var codec = "Unknown";
        var sampleRate = 0;
        var channels = 0;
        long? sampleCount = null;
        long position = 12;
        var foundFormat = false;
        var foundData = false;
        var chunks = 0;
        while (position <= riffLength - 8)
        {
            if (++chunks > 4096) return new ParsedProbe(new DataSlice(string.Empty, offset, length), MediaState.Malformed, true, codec, sampleRate, channels, null, ContainerValidity.Malformed);
            var chunkHeader = new byte[8];
            ReadAtExactly(handle, offset + position, chunkHeader, metadataRead);
            var tag = Encoding.ASCII.GetString(chunkHeader, 0, 4);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4, 4));
            var payload = position + 8;
            if (chunkSize > riffLength - payload)
                return new ParsedProbe(new DataSlice(string.Empty, offset, length), MediaState.Malformed, true, codec, sampleRate, channels, null, ContainerValidity.Malformed);
            if (tag == "fmt " && chunkSize >= 16)
            {
                var formatBytes = new byte[(int)Math.Min(chunkSize, 66)];
                ReadAtExactly(handle, offset + payload, formatBytes, metadataRead);
                var format = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes.AsSpan(0, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes.AsSpan(2, 2));
                var rate = BinaryPrimitives.ReadUInt32LittleEndian(formatBytes.AsSpan(4, 4));
                if (rate > int.MaxValue || channels == 0 || rate == 0)
                    return new ParsedProbe(new DataSlice(string.Empty, offset, length), MediaState.Malformed, true, codec, 0, channels, null, ContainerValidity.Malformed);
                sampleRate = (int)rate;
                codec = format switch { 1 => "PCM", 2 => "Wwise ADPCM", 0x69 => "XMA", 0xFFFF => "Wwise Vorbis", 0x0165 => "AAC", _ => $"Wwise format 0x{format:X4}" };
                foundFormat = true;
                if (format == 0xFFFF && chunkSize >= 28) sampleCount = BinaryPrimitives.ReadUInt32LittleEndian(formatBytes.AsSpan(24, 4));
            }
            else if (tag == "vorb" && chunkSize >= 4)
            {
                var count = new byte[4]; ReadAtExactly(handle, offset + payload, count, metadataRead);
                sampleCount = BinaryPrimitives.ReadUInt32LittleEndian(count);
            }
            else if (tag == "data") foundData = true;
            position = payload + chunkSize;
            if ((chunkSize & 1) != 0 && position < riffLength) position++;
        }
        if (!foundFormat || !foundData || position != riffLength)
            return new ParsedProbe(new DataSlice(string.Empty, offset, length), MediaState.Malformed, true, codec, sampleRate, channels, null, ContainerValidity.Malformed);
        double? duration = sampleCount is > 0 && sampleRate > 0 ? sampleCount.Value / (double)sampleRate : null;
        return new ParsedProbe(new DataSlice(string.Empty, offset, riffLength), MediaState.CompleteEmbedded, true, codec, sampleRate, channels, duration, ContainerValidity.Valid);
    }

    private static void ReadAtExactly(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long offset, byte[] destination,
        Action<long, byte[]>? metadataRead = null)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var amount = RandomAccess.Read(handle, destination.AsSpan(read), checked(offset + read));
            if (amount == 0) throw new EndOfStreamException("The package ended during a bounded media-header read.");
            read += amount;
        }
        metadataRead?.Invoke(offset, destination);
    }

    private static MediaEntry MakeMediaEntry(string key, ResourceRecord resource, string name, string language, DataSlice mediaSlice,
        MediaState state, bool isRiff, string codec, int sampleRate, int channels, double? duration,
        ContainerValidity validity = ContainerValidity.Unknown, DataSlice? originalSlice = null)
    {
        var mediaIdentity = resource.Type == 1033 && resource.Identity is { } sourceIdentity
            ? sourceIdentity with { MemberPath = "wem" } : resource.Identity;
        var entry = new MediaEntry
        {
            Key = key, Identity = mediaIdentity, PayloadIdentity = mediaIdentity,
            Id = resource.Id, Name = name, Language = language, Category = language == "SFX" ? "Unknown" : "Dialogue",
            Codec = codec, SampleRate = sampleRate, Channels = channels, Duration = duration, Slice = mediaSlice,
            OriginalSlice = originalSlice ?? mediaSlice,
            State = state, IsRiff = isRiff, NameKind = NameKind.Stored,
            Completeness = state == MediaState.CompleteEmbedded ? MediaCompleteness.Complete : MediaCompleteness.Unverified,
            ContainerValidity = validity,
            Availability = mediaSlice.Length > 0 ? SourceAvailability.Available : SourceAvailability.Missing
        };
        if (resource.Type != 0 || key == "wem:standalone")
            entry.Names.Add(new NameEvidence(name, language == "SFX" ? "Media" : "Dialogue", resource.Id, NameKind.Stored, resource.Key, resource.Slice.Offset));
        return entry;
    }

    private static BankInfo ParseBankFromSlice(string key, string name, uint expectedId, DataSlice bankSlice,
        ResourceIdentity bankIdentity, ResourceIdentity mediaOwnerIdentity,
        IReadOnlyDictionary<uint, (string Name, ResourceRecord Resource, bool Verified)> knownNames, AudioCatalog catalog, PackageReader archive)
    {
        var bank = new BankInfo { Key = key, Identity = bankIdentity, ContainerIdentity = mediaOwnerIdentity with { MemberPath = "bank" }, Name = name, Id = expectedId, Slice = bankSlice };
        var chunks = new Dictionary<string, (long Offset, uint Length)>();
        long position = 0;
        var chunkCount = 0;
        while (position <= bankSlice.Length - 8)
        {
            if (++chunkCount > 4096)
            {
                bank.Diagnostics.Add(new Diagnostic("BankChunkCountLimit", "The bank contains too many chunks to parse safely.", key));
                break;
            }
            var header = archive.ReadAt(bankSlice.FilePath, checked(bankSlice.Offset + position), 8, "HIRC");
            var tag = Encoding.ASCII.GetString(header, 0, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            if (size > bankSlice.Length - position - 8)
            {
                bank.Diagnostics.Add(new Diagnostic("MalformedBankChunk", $"Chunk {tag} extends beyond the declared bank payload.", key));
                break;
            }
            var payloadOffset = position + 8;
            bank.Chunks.Add(new BankChunk(tag, new DataSlice(bankSlice.FilePath, checked(bankSlice.Offset + payloadOffset), size)));
            chunks[tag] = (payloadOffset, size);
            position = checked(payloadOffset + size);
        }
        if (position != bankSlice.Length)
            bank.Diagnostics.Add(new Diagnostic("BankChunkTableTail", $"The bank has {bankSlice.Length - position} byte(s) outside complete chunk headers.", key));

        if (!chunks.TryGetValue("BKHD", out var headerChunk) || headerChunk.Length < 8)
        {
            bank.Diagnostics.Add(new Diagnostic("MissingBankHeader", "The bank has no complete BKHD header.", key));
            return bank;
        }
        var bkhdLength = checked((int)Math.Min(headerChunk.Length, 16));
        var bkhd = archive.ReadAt(bankSlice.FilePath, checked(bankSlice.Offset + headerChunk.Offset), bkhdLength, "HIRC");
        bank.Version = BinaryPrimitives.ReadUInt32LittleEndian(bkhd.AsSpan(0, 4));
        bank.Id = BinaryPrimitives.ReadUInt32LittleEndian(bkhd.AsSpan(4, 4));
        bank.LanguageId = bkhd.Length >= 12 ? BinaryPrimitives.ReadUInt32LittleEndian(bkhd.AsSpan(8, 4)) : 0;
        bank.HasFeedback = bank.Version <= 126 && bkhd.Length >= 16 &&
            (BinaryPrimitives.ReadUInt32LittleEndian(bkhd.AsSpan(12, 4)) & 1) != 0;
        if (bank.Version is not (118 or 120))
            bank.Diagnostics.Add(new Diagnostic("UnsupportedBankVersion", $"Wwise bank version {bank.Version} is preserved, but HIRC fields were not interpreted.", key));

        var didxRows = new List<(uint Id, uint Offset, uint Size, int Occurrence, int RecordIndex)>();
        long dataOffset = -1;
        long dataLength = 0;
        if (chunks.TryGetValue("DIDX", out var didx))
        {
            if (didx.Length % 12 != 0)
                bank.Diagnostics.Add(new Diagnostic("MalformedDIDX", "The DIDX chunk length is not divisible by its 12-byte record size.", key));
            if (didx.Length > MaximumWrapperBytes)
                bank.Diagnostics.Add(new Diagnostic("DIDXSizeLimit", "The DIDX table exceeds the metadata read limit; embedded media slices remain available through the bank bytes.", key));
            else
            {
                var bytes = archive.ReadAt(bankSlice.FilePath, checked(bankSlice.Offset + didx.Offset), checked((int)didx.Length), "DIDX");
                var mediaOccurrences = new Dictionary<uint, int>();
                for (var local = 0; local + 12 <= bytes.Length; local += 12)
                {
                    var row = bytes.AsSpan(local, 12);
                    var mediaId = BinaryPrimitives.ReadUInt32LittleEndian(row[..4]);
                    var mediaOffset = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(4, 4));
                    var mediaSize = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(8, 4));
                    var occurrence = mediaOccurrences.GetValueOrDefault(mediaId);
                    mediaOccurrences[mediaId] = occurrence + 1;
                    didxRows.Add((mediaId, mediaOffset, mediaSize, occurrence, local / 12));
                }
            }
        }
        if (chunks.TryGetValue("DATA", out var data))
        {
            dataOffset = data.Offset;
            dataLength = data.Length;
        }
        else if (didxRows.Count > 0)
            bank.Diagnostics.Add(new Diagnostic("MissingDATA", "The bank has DIDX entries but no DATA chunk.", key));

        foreach (var (id, offset, size, occurrence, recordIndex) in didxRows)
        {
            if (dataOffset < 0 || offset > dataLength || size > dataLength - offset)
            {
                var missingKey = $"{key}:media:{id:X8}:{occurrence}";
                var missingIdentity = mediaOwnerIdentity with { MemberPath = $"media/{id:X8}/{recordIndex}" };
                catalog.Media.Add(new MediaEntry { Key = missingKey, Identity = missingIdentity, PayloadIdentity = missingIdentity, Id = id, BankKey = key, BankName = name,
                    Name = id.ToString(CultureInfo.InvariantCulture), Slice = new DataSlice(bankSlice.FilePath, bankSlice.Offset, 0),
                    Availability = SourceAvailability.Missing, ContainerValidity = ContainerValidity.Malformed,
                    State = MediaState.Malformed, NameKind = NameKind.Stored, Category = "Malformed embedded media" });
                bank.Diagnostics.Add(new Diagnostic("MediaRangeOutOfBounds", $"DIDX media {id} points outside DATA; its location is retained as malformed.", key));
                continue;
            }
            var absolute = checked(bankSlice.Offset + dataOffset + offset);
            var probe = archive.ProbeWem(bankSlice.FilePath, absolute, size);
            var entryKey = $"{key}:media:{id:X8}:{occurrence}";
            var resource = new ResourceRecord
            {
                Key = entryKey, Identity = mediaOwnerIdentity with { MemberPath = $"media/{id:X8}/{recordIndex}" },
                Type = 0, Id = id, Occurrence = occurrence, Slice = new DataSlice(bankSlice.FilePath, absolute, size)
            };
            var media = MakeMediaEntry(entryKey, resource, id.ToString(CultureInfo.InvariantCulture), "SFX", probe.Slice, probe.State,
                probe.IsRiff, probe.Codec, probe.SampleRate, probe.Channels, probe.Duration, probe.Validity, originalSlice: resource.Slice);
            media.MetadataReadDigests = probe.ReadDigests ?? [];
            media.MetadataReadCoverageComplete = probe.ReadCoverageComplete;
            media.BankKey = key;
            media.BankName = name;
            media.RawLanguageId = bank.LanguageId;
            media.DidxExtent = resource.Slice;
            media.Completeness = MediaCompleteness.Unverified;
            if (probe.State == MediaState.Unsupported) media.Category = "Other";
            catalog.Media.Add(media);
        }

        if (chunks.TryGetValue("HIRC", out var hirc))
        {
            if (bank.Version is 118 or 120 && hirc.Length <= MaximumWrapperBytes)
            {
                var hircBytes = archive.ReadAt(bankSlice.FilePath, checked(bankSlice.Offset + hirc.Offset), checked((int)hirc.Length), "HIRC");
                ParseHirc(bank, hircBytes, catalog, checked(bankSlice.Offset + hirc.Offset));
            }
            else if (bank.Version is 118 or 120)
                bank.Diagnostics.Add(new Diagnostic("HIRCSizeLimit", "HIRC metadata exceeds the bounded parsing limit; raw bank export remains available.", key));
            else
                bank.Diagnostics.Add(new Diagnostic("UnsupportedHIRC", "Unknown HIRC objects were preserved as bounded opaque chunks.", key));
        }
        if (!chunks.ContainsKey("DIDX")) bank.Diagnostics.Add(new Diagnostic("NoEmbeddedMedia", "The bank has no DIDX embedded-media table.", key));
        if (chunks.TryGetValue("STID", out var strings) && strings.Length <= MaximumWrapperBytes)
        {
            var bytes = archive.ReadAt(bankSlice.FilePath, checked(bankSlice.Offset + strings.Offset), checked((int)strings.Length));
            if (bytes.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 1)
            {
                var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
                var offset = 8; var names = new List<NameEvidence>();
                for (uint index = 0; index < count && index < 100_000; index++)
                {
                    if (offset > bytes.Length - 5) { names.Clear(); break; }
                    var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
                    var length = bytes[offset + 4]; offset += 5;
                    if (length > bytes.Length - offset) { names.Clear(); break; }
                    var text = Encoding.UTF8.GetString(bytes, offset, length).TrimEnd('\0');
                    if (text.Length > 0) names.Add(new(text, "WwiseBank", id, NameKind.Stored, key, bankSlice.Offset + strings.Offset + offset));
                    offset += length;
                }
                if (count <= 100_000 && names.Count == count) catalog.Names.AddRange(names);
                else bank.Diagnostics.Add(new("MalformedSTID", "The bank string mapping is truncated or exceeds the entry limit.", key));
            }
            else bank.Diagnostics.Add(new("UnsupportedSTID", "This string mapping type is not supported.", key));
        }
        return bank;
    }

    private static void ParseHirc(BankInfo bank, ReadOnlySpan<byte> bytes, AudioCatalog catalog, long hircOffset)
    {
        if (bytes.Length < 4)
        {
            bank.Diagnostics.Add(new Diagnostic("MalformedHIRC", "The HIRC object count is truncated.", bank.Key));
            return;
        }
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]);
        if (count > 500_000)
        {
            bank.Diagnostics.Add(new Diagnostic("HIRCObjectCountLimit", "The HIRC object count exceeds the safety limit.", bank.Key));
            return;
        }
        bank.IndexedObjectCount = checked((int)count);
        bank.AmbiguousObjectIds.Clear();
        var position = 4;
        for (uint i = 0; i < count; i++)
        {
            if (position > bytes.Length - 9)
            {
                bank.Diagnostics.Add(new Diagnostic("MalformedHIRC", "The HIRC object header is truncated.", bank.Key));
                return;
            }
            var type = bytes[position];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(position + 1, 4));
            var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(position + 5, 4));
            if (size < 4 || size > bytes.Length - position - 5)
            {
                bank.Diagnostics.Add(new Diagnostic("MalformedHIRCObject", $"HIRC object {id} has an invalid declared size.", bank.Key));
                return;
            }
            var payloadLength = checked((int)size - 4);
            var payload = bytes.Slice(position + 9, payloadLength);
            var objectOccurrence = bank.Objects.ContainsKey(id) ? 1 : 0;
            var item = new BankObject
            {
                Id = id, Type = type, Identity = bank.Identity is null ? null : bank.Identity with { MemberPath = $"hirc/{id:X8}/{objectOccurrence}" },
                BodySlice = new DataSlice(bank.Slice.FilePath, checked(hircOffset + position + 9), payloadLength)
            };
            WwiseNodeReader.Parse(item, payload, bank.Version, bank.HasFeedback);
            catalog.SourceDeclarations.AddRange(item.Sources.Select(source =>
                new MediaSourceEvidence(bank.Key, item.Id, source, item.BodySlice?.Offset ?? 0)));
            if (bank.Objects.ContainsKey(id))
            {
                bank.Objects.Remove(id);
                bank.AmbiguousObjectIds.Add(id);
                bank.Diagnostics.Add(new Diagnostic("DuplicateHircObjectId", $"HIRC ID {id} occurs more than once in this bank.", bank.Key));
            }
            else if (bank.AmbiguousObjectIds.Contains(id))
                bank.Diagnostics.Add(new Diagnostic("DuplicateHircObjectId", $"HIRC ID {id} occurs more than once in this bank.", bank.Key));
            else
                bank.Objects.Add(id, item);
            position = checked(position + 5 + (int)size);
        }
        if (position != bytes.Length)
            bank.Diagnostics.Add(new Diagnostic("HIRCUnparsedTail", $"HIRC contains {bytes.Length - position} trailing byte(s).", bank.Key));
        LinkBankObjects(bank);
    }

    private static void ParseLocalization(ReadOnlySpan<byte> bytes, ResourceRecord resource, AudioCatalog catalog)
    {
        var marker = bytes.IndexOf(LocalizationMagic);
        if (marker < 0)
        {
            catalog.Diagnostics.Add(new Diagnostic("LocalizationSignatureMissing", "The resource does not contain a LOCALIZ_ payload.", resource.Key));
            return;
        }
        var start = marker + LocalizationMagic.Length;
        if (start > bytes.Length - 4) { catalog.Diagnostics.Add(new("MalformedLocalization", "The localization version field is truncated.", resource.Key)); return; }
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start, 4));
        int body;
        uint languageCount;
        int end;
        if (version == 6)
        {
            if (start + 12 > bytes.Length) { catalog.Diagnostics.Add(new Diagnostic("MalformedLocalization", "Version 6 localization header is truncated.", resource.Key)); return; }
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start + 4, 4));
            languageCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start + 8, 4));
            body = start + 12;
            end = Math.Min(bytes.Length, checked(body + (int)Math.Min(declared, (uint)(bytes.Length - body))));
        }
        else if (version == 5)
        {
            if (start + 13 > bytes.Length) { catalog.Diagnostics.Add(new Diagnostic("MalformedLocalization", "Version 5 localization header is truncated.", resource.Key)); return; }
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start + 4, 4));
            languageCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start + 9, 4));
            body = start + 13;
            end = Math.Min(bytes.Length, checked(body + (int)Math.Min(declared, (uint)(bytes.Length - body))));
        }
        else
        {
            catalog.Diagnostics.Add(new Diagnostic("UnsupportedLocalizationVersion", $"Localization version {version} is preserved but not interpreted.", resource.Key));
            return;
        }
        if (languageCount > 64)
        {
            catalog.Diagnostics.Add(new Diagnostic("LocalizationLanguageCountLimit", "The localization language count exceeds the safety limit.", resource.Key));
            return;
        }

        var pointerCapacity = TryParseComContainer(bytes, out var comReferences) ? comReferences.Count : 0;

        var cursor = body;
        var unresolvedPointers = 0;
        for (var languageNumber = 0; languageNumber < languageCount && cursor < end; languageNumber++)
        {
            if (cursor > end - 8 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, 4)) != 0x00000301)
            {
                catalog.Diagnostics.Add(new Diagnostic("MalformedLocalizationLanguage", "A language block has an invalid marker or truncated header.", resource.Key));
                return;
            }
            var language = Encoding.ASCII.GetString(bytes.Slice(cursor + 5, 3));
            cursor += 8;
            while (cursor <= end - 8)
            {
                // The next language marker terminates this language's records.
                if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, 4)) == 0x00000301) break;
                var keyCountOrFlags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, 4));
                var keyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor + 4, 4));
                cursor += 8;
                if (keyLength == 0) break;

                if (keyLength <= 256 && keyLength <= end - cursor && IsAsciiKey(bytes.Slice(cursor, (int)keyLength)))
                {
                    var keyOffset = cursor;
                    var key = Encoding.ASCII.GetString(bytes.Slice(cursor, (int)keyLength));
                    cursor += (int)keyLength;
                    if (cursor > end - 4) break;
                    var textLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, 4));
                    cursor += 4;
                    if ((textLength & 1) != 0 || textLength > end - cursor)
                    {
                        catalog.Diagnostics.Add(new Diagnostic("MalformedLocalizationText", "A UTF-16 text record extends beyond its resource extent.", resource.Key));
                        return;
                    }
                    var text = Encoding.Unicode.GetString(bytes.Slice(cursor, (int)textLength));
                    catalog.Localization.Add(new LocalizedText(key, language, text, resource.Key, resource.Slice.Offset + keyOffset));
                    cursor += (int)textLength;
                }
                else if (keyLength is > 0 and <= 64 && keyLength <= pointerCapacity)
                {
                    // Pointer records hold several keys whose display text is stored elsewhere.
                    // Keep the keys out of the subtitle index until a source pointer is validated.
                    var count = keyLength;
                    var valid = true;
                    for (var keyIndex = 0; keyIndex < count; keyIndex++)
                    {
                        if (cursor > end - 4) { valid = false; break; }
                        var pointerKeyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, 4));
                        cursor += 4;
                        if (pointerKeyLength is 0 or > 256 || pointerKeyLength > end - cursor || !IsAsciiKey(bytes.Slice(cursor, (int)pointerKeyLength))) { valid = false; break; }
                        cursor += (int)pointerKeyLength;
                        if (cursor >= end) { valid = false; break; }
                        var pointerKind = bytes[cursor++];
                        var pointerLength = pointerKind switch { 0 => 0, 1 => 9, _ => -1 };
                        if (pointerLength < 0 || pointerLength > end - cursor) { valid = false; break; }
                        cursor += pointerLength;
                    }
                    if (!valid)
                    {
                        catalog.Diagnostics.Add(new Diagnostic("MalformedLocalizationPointer", "A multi-key localization pointer record is malformed and was not associated with text.", resource.Key));
                        return;
                    }
                    unresolvedPointers += (int)count;
                }
                else
                {
                    catalog.Diagnostics.Add(new Diagnostic("UnsupportedLocalizationRecord", "An unknown localization record form was not interpreted.", resource.Key));
                    return;
                }
            }
        }
        if (unresolvedPointers > 0)
            catalog.Diagnostics.Add(new Diagnostic("LocalizationPointersUnresolved", $"{unresolvedPointers} multi-key pointer reference(s) were kept out of the subtitle index because target text could not be validated.", resource.Key));
    }

    private static bool IsAsciiKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return false;
        foreach (var value in bytes)
            if (value < 0x20 || value > 0x7E) return false;
        return true;
    }

    private static void LinkBankObjects(BankInfo bank)
    {
        foreach (var parent in bank.Objects.Values)
        {
            var missingChildren = parent.Children.Where(childId => !bank.Objects.ContainsKey(childId)).ToArray();
            if (missingChildren.Length > 0)
            {
                parent.Support = PlanSupport.Approximate;
                parent.Limitations.Add("Some explicitly serialized child IDs are outside this bank; cross-bank resolution is deferred until graph linking.");
            }
            foreach (var childId in parent.Children)
            {
                if (bank.Objects.TryGetValue(childId, out var child) && child.ParentId is null) child.ParentId = parent.Id;
            }
        }
    }

    private static void ClassifyEmbeddedMediaForBank(BankInfo bank, IReadOnlyList<MediaEntry> entries,
        Func<uint, IReadOnlyList<MediaSourceEvidence>> sourceEvidenceForId)
    {
        foreach (var entry in entries)
        {
            var evidence = sourceEvidenceForId(entry.Id);
            try
            {
                entry.SourceEvidence.AddRange(evidence);
                entry.Completeness = MediaCompleteness.Unverified;
                if (entry.Slice.Length == 0) { entry.Availability = SourceAvailability.Missing; continue; }
                entry.Availability = SourceAvailability.Available;
                var declarations = evidence.Select(item => item.Declaration).Distinct().ToArray();
                if (declarations.Length == 0)
                {
                    entry.ResolutionDetails = "No matching HIRC source declaration.";
                    continue;
                }
                var streamModes = declarations.Select(value => value.StreamType).Distinct().ToArray();
                var prefetch = declarations.Any(value => value.StreamType is 1 or 2 || (value.Flags & 2) != 0);
                if (prefetch)
                {
                    entry.Completeness = MediaCompleteness.Fragment;
                    entry.State = MediaState.PrefetchOnly;
                    entry.ResolutionDetails = "Embedded streaming prefix; a full stream is required.";
                    // Keep DIDX extent, including any bytes after a locally valid RIFF prefix.
                    if (entry.DidxExtent is not null) entry.Slice = entry.DidxExtent;
                }
                else if (streamModes.Length == 1 && streamModes[0] == 0 &&
                         declarations.All(value => (value.PluginId & 15) is not (2 or 5) && (value.Flags & 0x74) == 0 &&
                             (value.InMemorySize == 0 || value.InMemorySize == entry.Slice.Length)) && entry.ContainerValidity == ContainerValidity.Valid)
                {
                    entry.Completeness = MediaCompleteness.Complete;
                    entry.State = MediaState.CompleteEmbedded;
                    entry.ResolutionDetails = "Bounded embedded payload matches Data source declarations.";
                }
                else entry.ResolutionDetails = "Source declaration, declared size, flags or container bounds require verification.";
                if (streamModes.Length > 1)
                {
                    entry.ResolutionDetails += " Conflicting stream declarations were retained.";
                    bank.Diagnostics.Add(new("ConflictingSourceDeclarations", $"Media {entry.Id} has conflicting stream declarations.", entry.Key));
                }
            }
            finally { entry.SourceDeclarationProcessingComplete = true; }
        }
    }

    private static void ResolveAllBankMedia(AudioCatalog catalog, CancellationToken token,
        IProgress<AudioReadProgress>? progress, Action ensureSourceRevision, IReadOnlySet<string> preclassifiedBankKeys)
    {
        var mediaByBankAndId = catalog.Media.Where(item => !item.Key.Contains(":source:", StringComparison.Ordinal))
            .GroupBy(item => (BankKey: item.BankKey, Id: item.Id)).ToDictionary(group => group.Key, group => group.ToArray());
        var mediaById = catalog.Media.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.ToArray());
        var mediaByBank = mediaByBankAndId.GroupBy(pair => pair.Key.BankKey)
            .ToDictionary(group => group.Key, group => group.SelectMany(pair => pair.Value).ToArray());
        var declarationsByBankAndId = catalog.SourceDeclarations
            .Select(source => (BankKey: source.BankKey, Source: source))
            .GroupBy(item => (BankKey: item.BankKey, Id: item.Source.Declaration.Id))
            .ToDictionary(group => group.Key, group => group.Select(item => item.Source).ToArray());
        var declarationsByBank = declarationsByBankAndId.GroupBy(pair => pair.Key.BankKey)
            .ToDictionary(group => group.Key, group => group.Select(pair => (Id: pair.Key.Id, Evidence: pair.Value)).ToArray());
        // All embedded occurrences must be classified before any is offered as a full stream.
        var embeddedBanksCompleted = 0;
        List<MediaEntry>? pendingClassificationMedia = progress is null ? null : [];
        foreach (var bank in catalog.Banks)
        {
            token.ThrowIfCancellationRequested();
            List<MediaEntry>? changedMedia = progress is null ? null : [];
            var alreadyClassified = preclassifiedBankKeys.Contains(bank.Key);
            var bankMedia = mediaByBank.GetValueOrDefault(bank.Key, []);
            if (!alreadyClassified)
            {
                if (progress is not null) changedMedia!.AddRange(bankMedia.Where(entry => entry.Identity is not null));
                ClassifyEmbeddedMediaForBank(bank, bankMedia,
                    mediaId => declarationsByBankAndId.GetValueOrDefault((bank.Key, mediaId), []));
                if (pendingClassificationMedia is not null) pendingClassificationMedia.AddRange(changedMedia!);
            }
            embeddedBanksCompleted++;
            if (embeddedBanksCompleted == 1 || embeddedBanksCompleted % 64 == 0 || embeddedBanksCompleted == catalog.Banks.Count)
            {
                ensureSourceRevision();
                if (alreadyClassified)
                    progress?.Report(new AudioReadProgress("Classifying embedded media", embeddedBanksCompleted,
                        catalog.Banks.Count, bank.Name));
                else
                    PublishSnapshot(progress, catalog, "Classifying embedded media", embeddedBanksCompleted,
                        catalog.Banks.Count, bank.Name, pendingClassificationMedia);
                if (!alreadyClassified) pendingClassificationMedia?.Clear();
            }
        }
        // A fixed set prevents newly synthesized resolutions from becoming candidates.
        var independent = catalog.Media.Where(value => value.Completeness == MediaCompleteness.Complete &&
            value.ContainerValidity == ContainerValidity.Valid && value.Availability == SourceAvailability.Available).ToArray();
        var independentById = independent.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.ToArray());
        var comparisonCache = new ResolutionComparisonCache();
        var externalBanksCompleted = 0;
        List<MediaEntry>? pendingExternalMedia = progress is null ? null : [];
        foreach (var bank in catalog.Banks)
        {
            token.ThrowIfCancellationRequested();
            List<MediaEntry>? changedMedia = progress is null ? null : [];
            foreach (var (mediaId, group) in declarationsByBank.GetValueOrDefault(bank.Key, []))
            {
                var declarations = group.Select(value => value.Declaration).Distinct().ToArray();
                var embedded = mediaByBankAndId.GetValueOrDefault((bank.Key, mediaId), []);
                if (embedded.Any(item => item.Completeness == MediaCompleteness.Complete)) continue;
                // Unmapped embedded bytes stay visible, but never satisfy a streaming declaration.
                if (declarations.All(value => value.StreamType == 0 && (value.Flags & 2) == 0) && embedded.Length > 0) continue;
                var source = declarations[0];
                var nonWem = declarations.Any(value => (value.PluginId & 15) is 2 or 5);
                var languageSpecific = declarations.Any(value => (value.Flags & 1) != 0);
                var candidatePool = nonWem ? [] : independentById.GetValueOrDefault(source.Id, []).Where(item => item.BankKey != bank.Key &&
                    (languageSpecific ? item.RawLanguageId == bank.LanguageId : item.RawLanguageId is null or 0 || item.RawLanguageId == bank.LanguageId))
                    .DistinctBy(item => item.Slice).ToList();
                var candidates = candidatePool.ToList();
                if (source.StreamType == 0 && source.InMemorySize > 0)
                    candidates = candidates.Where(item => item.Slice.Length == source.InMemorySize).ToList();
                var requestedCodec = GetDeclaredWemCodec(declarations);
                var codecRejected = new List<MediaCandidateRejection>();
                if (requestedCodec is null)
                {
                    codecRejected.AddRange(candidates.Select(item => new MediaCandidateRejection(item.Slice,
                        "The source declarations do not identify one supported WEM codec.")));
                    candidates.Clear();
                }
                else
                {
                    codecRejected.AddRange(candidates.Where(item => !CodecMatches(item.Codec, requestedCodec))
                        .Select(item => new MediaCandidateRejection(item.Slice,
                            $"Declared codec {requestedCodec} does not match candidate codec {item.Codec}.")));
                    candidates = candidates.Where(item => CodecMatches(item.Codec, requestedCodec)).ToList();
                }
                var resolvedFromRaw = false;
                if (candidates.Count == 0 && !nonWem && CanInspectInMemoryWemCandidates(declarations))
                {
                    var request = declarations[0];
                    var rawCandidates = mediaById.GetValueOrDefault(mediaId, []).Where(item => item.BankKey != bank.Key &&
                            item.State == MediaState.CompleteEmbedded && item.IsRiff && item.ContainerValidity == ContainerValidity.Valid &&
                            item.Availability == SourceAvailability.Available && item.Completeness == MediaCompleteness.Unverified &&
                            item.SourceEvidence.Count == 0 && item.Slice.Length == request.InMemorySize &&
                            (languageSpecific ? item.RawLanguageId == bank.LanguageId : item.RawLanguageId is null or 0 || item.RawLanguageId == bank.LanguageId))
                        .DistinctBy(item => item.Slice).ToList();
                    codecRejected.AddRange(rawCandidates.Where(item => !CodecMatchesDeclarations(item.Codec, declarations))
                        .Select(item => new MediaCandidateRejection(item.Slice,
                            $"Candidate codec {item.Codec} does not match every source declaration ({DescribeDeclaredCodecs(declarations)}).")));
                    candidates = rawCandidates.Where(item => CodecMatchesDeclarations(item.Codec, declarations)).ToList();
                    resolvedFromRaw = candidates.Count > 0 && IsSupportedInMemoryWemRequest(declarations);
                    if (!resolvedFromRaw) candidates.Clear();
                }
                comparisonCache.BeginResolution();
                var equivalence = GroupEquivalentCandidates(candidates, token, comparisonCache, resolvedFromRaw);
                var selected = equivalence.Count == 1 ? equivalence[0].Representative : null;
                var state = nonWem ? MediaState.Unsupported : equivalence.Count == 1 ? MediaState.CompleteExternal :
                    equivalence.Count > 1 ? MediaState.Ambiguous : MediaState.MissingExternal;
                var metadataReadDigests = MergeMetadataReadDigests(bank.MetadataReadDigests,
                    candidatePool.SelectMany(candidate => candidate.MetadataReadDigests),
                    selected?.MetadataReadDigests ?? [], comparisonCache.Digests);
                var declarationPath = string.Join(";", group.Select(item => $"{item.ObjectId:X8}-{item.Offset:X}-{item.Declaration.StreamType}")
                    .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal));
                var entry = new MediaEntry
                {
                    Key = $"{bank.Key}:source:{source.Id:X8}:{source.StreamType}",
                    Identity = bank.Identity is null ? null : bank.Identity with { MemberPath = $"source/{declarationPath}/media/{source.Id:X8}" },
                    PayloadIdentity = selected?.PayloadIdentity ?? selected?.Identity,
                    Id = source.Id, BankKey = bank.Key, BankName = bank.Name,
                    Name = selected?.Name ?? "", Language = selected?.Language ?? "SFX", RawLanguageId = bank.LanguageId,
                    Category = selected?.Category ?? "Unknown", Codec = selected?.Codec ?? "Unknown", SampleRate = selected?.SampleRate ?? 0,
                    Channels = selected?.Channels ?? 0, Duration = selected?.Duration, IsRiff = selected?.IsRiff ?? false,
                    Slice = selected?.Slice ?? new DataSlice(bank.Slice.FilePath, bank.Slice.Offset, 0),
                    OriginalSlice = selected?.OriginalSlice ?? selected?.Slice, State = state,
                    Completeness = selected is null || resolvedFromRaw ? MediaCompleteness.Unverified : MediaCompleteness.Complete,
                    ContainerValidity = selected?.ContainerValidity ?? ContainerValidity.Unknown,
                    Availability = selected is not null ? SourceAvailability.Available : candidates.Count > 1 ? SourceAvailability.Ambiguous : SourceAvailability.Missing,
                    MetadataReadDigests = metadataReadDigests ?? [],
                    MetadataReadCoverageComplete = bank.MetadataReadCoverageComplete &&
                        candidatePool.All(candidate => candidate.MetadataReadCoverageComplete) &&
                        (selected?.MetadataReadCoverageComplete ?? true) && comparisonCache.CoverageComplete && metadataReadDigests is not null,
                    NameKind = selected is null ? NameKind.Candidate : NameKind.Description,
                    ResolutionDetails = selected is not null ? resolvedFromRaw
                        ? $"A compatible HIRC memory-source declaration matches this valid raw WEM by media ID and exact size; {equivalence[0].Locations.Count} byte-identical source location(s) were retained. The payload remains independently unverified."
                        : equivalence[0].Locations.Count > 1
                            ? $"Full stream resolved from {equivalence[0].Locations.Count} byte-identical sources; a deterministic representative was selected."
                            : $"Full stream from {selected.Key}."
                        : equivalence.Count > 1
                            ? $"{equivalence.Count} distinct compatible payloads match; no candidate selected."
                            : codecRejected.Count > 0
                                ? $"Full external stream candidates were rejected: {string.Join(" ", codecRejected.Select(rejection => rejection.Reason).Distinct(StringComparer.Ordinal))}"
                                : "Full external stream was not found."
                };
                entry.SourceEvidence.AddRange(group);
                entry.Candidates.AddRange(candidates.Select(candidate => candidate.Slice));
                entry.CandidateRejections.AddRange(codecRejected);
                if (resolvedFromRaw || equivalence.Count > 1 || equivalence.Any(group => group.Locations.Count > 1))
                    foreach (var equivalenceGroup in equivalence)
                        entry.EquivalenceEvidence.Add(new(equivalenceGroup.Sha256, equivalenceGroup.Locations,
                            resolvedFromRaw ? "Compatible media ID/language, supported non-streamed WEM declaration, exact declared size, SHA-256, and exact bytes; raw source records remain unverified"
                                : "SHA-256, length, and exact bytes in compatible media ID/language context"));
                catalog.Media.Add(entry);
                if (entry.Identity is not null) changedMedia?.Add(entry);
                if (entry.Identity is not null) pendingExternalMedia?.Add(entry);
                if (selected is not null)
                {
                    entry.Names.Add(new(selected.DisplayName, "ExternalMediaCandidate", source.Id, NameKind.Description, selected.Key, selected.Slice.Offset));
                    catalog.Names.AddRange(entry.Names);
                }
                else bank.Diagnostics.Add(new(nonWem ? "NonWemSourcePlugin" : equivalence.Count > 1 ? "AmbiguousExternalMedia" : "MissingExternalMedia", entry.ResolutionDetails, entry.Key));
            }
            externalBanksCompleted++;
            if (externalBanksCompleted == 1 || externalBanksCompleted % 64 == 0 || externalBanksCompleted == catalog.Banks.Count)
            {
                ensureSourceRevision();
                PublishSnapshot(progress, catalog, "Resolving bank media", externalBanksCompleted, catalog.Banks.Count,
                    bank.Name, pendingExternalMedia);
                pendingExternalMedia?.Clear();
            }
        }
    }

    private static bool IsSupportedInMemoryWemRequest(IReadOnlyCollection<MediaReference> declarations) =>
        declarations.Count > 0 && declarations.All(value => value.StreamType == 0 && value.PluginId == WwiseVorbisPluginId &&
            value.InMemorySize > 0 && (value.Flags & ~LanguageSpecificSourceFlag) == 0) && declarations.Distinct().Count() == 1;

    private static bool CanInspectInMemoryWemCandidates(IReadOnlyCollection<MediaReference> declarations) =>
        declarations.Count > 0 && declarations.All(value => value.StreamType == 0 && value.InMemorySize > 0 &&
            (value.Flags & ~LanguageSpecificSourceFlag) == 0) && declarations.Select(value => value.InMemorySize).Distinct().Count() == 1;

    private static bool CodecMatchesDeclarations(string? candidateCodec, IReadOnlyCollection<MediaReference> declarations) =>
        candidateCodec is not null && declarations.Count > 0 && declarations.All(value =>
            GetDeclaredWemCodec([value]) is { } declaredCodec && CodecMatches(candidateCodec, declaredCodec));

    private static string DescribeDeclaredCodecs(IReadOnlyCollection<MediaReference> declarations) =>
        string.Join(", ", declarations.Select(value => GetDeclaredWemCodec([value]) ?? $"unsupported plugin 0x{value.PluginId:X8}").Distinct(StringComparer.Ordinal));

    private static string? GetDeclaredWemCodec(IReadOnlyCollection<MediaReference> declarations)
    {
        if (declarations.Count == 0) return null;
        var codecs = declarations.Select(value => value.PluginId switch
        {
            0x00010001 => "PCM",
            WwiseVorbisPluginId => "Wwise Vorbis",
            _ => null
        }).Distinct(StringComparer.Ordinal).ToArray();
        return codecs.Length == 1 ? codecs[0] : null;
    }

    private static void RecoverExecutableNames(AudioCatalog catalog, CancellationToken token, IProgress<AudioReadProgress>? progress)
    {
        var directory = Path.GetDirectoryName(catalog.SourcePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        var observed = new Dictionary<uint, HashSet<string>>();
        foreach (var entry in catalog.Events.Where(entry => !entry.IsDialogue)) AddTarget(entry.Id, "WwiseEvent");
        foreach (var bank in catalog.Banks) AddTarget(bank.Id, "WwiseBank");

        var candidates = new HashSet<(string Namespace, uint Id, string Text, string Sha256, long Offset)>();
        const long maximumTotalBytes = 512L * 1024 * 1024;
        long scannedBytes = 0;
        string[] executablePaths;
        try { executablePaths = Directory.EnumerateFiles(directory, "*.exe").Order(StringComparer.OrdinalIgnoreCase).Take(4).ToArray(); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        if (executablePaths.Length == 0) return;
        progress?.Report(new AudioReadProgress("Recovering audio names", 0, executablePaths.Length));
        var scannedExecutables = 0;
        foreach (var path in executablePaths)
        {
            token.ThrowIfCancellationRequested();
            long length;
            try { length = new FileInfo(path).Length; }
            catch (IOException) { length = -1; }
            catch (UnauthorizedAccessException) { length = -1; }
            if (length < 64 || length > 256L * 1024 * 1024 || length > maximumTotalBytes - scannedBytes)
            {
                scannedExecutables++;
                progress?.Report(new AudioReadProgress("Recovering audio names", scannedExecutables, executablePaths.Length));
                continue;
            }
            scannedBytes += length;
            IReadOnlyList<ExecutableStringScanner.ExecutableString> strings;
            try { strings = ExecutableStringScanner.Read(path, token); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { strings = []; }
            catch (UnauthorizedAccessException) { strings = []; }
            foreach (var item in strings)
            {
                token.ThrowIfCancellationRequested();
                var id = Fnv1LowerUtf8(item.Text);
                foreach (var nameSpace in observed.GetValueOrDefault(id, []))
                    candidates.Add((nameSpace, id, item.Text, item.InputSha256, item.Offset));
            }
            scannedExecutables++;
            progress?.Report(new AudioReadProgress("Recovering audio names", scannedExecutables, executablePaths.Length));
        }

        foreach (var item in candidates)
        {
            var evidence = new NameEvidence(item.Text, item.Namespace, item.Id, NameKind.Candidate,
                "Executable data", item.Offset, "Medium", "FNV-1 lowercase UTF-8", InputSha256: item.Sha256);
            if (!catalog.Names.Contains(evidence)) catalog.Names.Add(evidence);
            if (item.Namespace == "WwiseEvent")
            {
                foreach (var entry in catalog.Events.Where(entry => !entry.IsDialogue && entry.Id == item.Id))
                {
                    if (!entry.Names.Contains(evidence)) entry.Names.Add(evidence);
                    var names = entry.Names.Select(name => name.Text).Where(name => !IsNumericId(name, entry.Id))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (IsNumericId(entry.Name, entry.Id) && names.Length == 1) entry.Name = names[0];
                }
            }
            else if (item.Namespace == "WwiseBank")
            {
                foreach (var bank in catalog.Banks.Where(bank => bank.Id == item.Id))
                {
                    if (!bank.Names.Contains(evidence)) bank.Names.Add(evidence);
                    var names = bank.Names.Select(name => name.Text).Where(name => !IsNumericId(name, bank.Id))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (IsNumericId(bank.Name, bank.Id) && names.Length == 1) bank.Name = names[0];
                    foreach (var media in catalog.Media.Where(media => media.BankKey == bank.Key)) media.BankName = bank.Name;
                }
            }
        }

        void AddTarget(uint id, string nameSpace)
        {
            if (!observed.TryGetValue(id, out var namespaces)) observed[id] = namespaces = new(StringComparer.Ordinal);
            namespaces.Add(nameSpace);
        }
    }

    private static bool IsNumericId(string? value, uint id) => string.IsNullOrWhiteSpace(value) ||
        string.Equals(value, id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
        string.Equals(value, id.ToString("X8", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

    private static bool CodecMatches(string? candidateCodec, string? declaredCodec) =>
        declaredCodec is not null && string.Equals(candidateCodec, declaredCodec, StringComparison.OrdinalIgnoreCase);

    private static List<EquivalentCandidateGroup> GroupEquivalentCandidates(List<MediaEntry> candidates, CancellationToken token,
        ResolutionComparisonCache comparisonCache, bool requireHash)
    {
        var groups = new List<EquivalentCandidateGroup>();
        foreach (var candidate in candidates.OrderBy(item => item.Key, StringComparer.Ordinal)
                     .ThenBy(item => item.Slice.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Slice.Offset))
        {
            token.ThrowIfCancellationRequested();
            var slice = candidate.Slice;
            var hash = requireHash || candidates.Count > 1
                ? comparisonCache.Hash(slice, candidate.PayloadIdentity ?? candidate.Identity, token) : "";
            var match = hash.Length == 0 ? null : groups.FirstOrDefault(group => group.Representative.Slice.Length == candidate.Slice.Length &&
                group.Sha256.Equals(hash, StringComparison.Ordinal) && comparisonCache.Equal(
                    group.Representative.Slice, candidate.Slice,
                    group.Representative.PayloadIdentity ?? group.Representative.Identity,
                    candidate.PayloadIdentity ?? candidate.Identity, token));
            if (match is null) groups.Add(new(candidate, hash, [candidate.Slice]));
            else match.Locations.Add(candidate.Slice);
        }
        return groups;
    }

    private static List<AudioMetadataReadDigest>? MergeMetadataReadDigests(params IEnumerable<AudioMetadataReadDigest>[] sources)
    {
        const int maximumExtents = 65_536;
        const long maximumBytes = 1L * 1024 * 1024 * 1024;
        var merged = new Dictionary<(string Path, long Offset, long Length, string Role, string? Owner), AudioMetadataReadDigest>();
        long bytes = 0;
        foreach (var digest in sources.SelectMany(source => source))
        {
            var role = digest.Role ?? "metadata";
            var key = (Path.GetFullPath(digest.Slice.FilePath), digest.Slice.Offset, digest.Slice.Length, role, digest.Owner?.Key);
            if (merged.ContainsKey(key)) continue;
            if (merged.Count >= maximumExtents || digest.Slice.Length < 0 || digest.Slice.Length > maximumBytes - bytes)
                return null;
            bytes += digest.Slice.Length;
            merged.Add(key, digest with { Slice = digest.Slice with { FilePath = key.Item1 } });
        }
        return merged.Values.ToList();
    }

    private static string HashSlice(DataSlice slice, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(slice.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Position = slice.Offset; var remaining = slice.Length; var buffer = new byte[128 * 1024];
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException("A media candidate changed during resolution.");
            hash.AppendData(buffer, 0, read); remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class EquivalentCandidateGroup(MediaEntry representative, string sha256, List<DataSlice> locations)
    {
        public MediaEntry Representative { get; } = representative;
        public string Sha256 { get; } = sha256;
        public List<DataSlice> Locations { get; } = locations;
    }

    private sealed class ResolutionComparisonCache
    {
        private const int MaximumResolutionDigests = 65_536;
        private readonly Dictionary<DataSlice, string> _hashes = [];
        private readonly Dictionary<(DataSlice Left, DataSlice Right), CachedEquality> _equal = [];
        private readonly Dictionary<(DataSlice Slice, string Sha256, string Role, string? Owner), AudioMetadataReadDigest> _resolutionDigests = [];
        private bool _resolutionCoverageComplete = true;
        public bool CoverageComplete => _resolutionCoverageComplete;
        public IReadOnlyList<AudioMetadataReadDigest> Digests => _resolutionDigests.Values.ToArray();

        public void BeginResolution()
        {
            _resolutionDigests.Clear();
            _resolutionCoverageComplete = true;
        }

        public string Hash(DataSlice slice, ResourceIdentity? owner, CancellationToken token)
        {
            if (!_hashes.TryGetValue(slice, out var value))
            {
                token.ThrowIfCancellationRequested();
                value = HashSlice(slice, token);
                _hashes.Add(slice, value);
            }
            AddResolutionDigest(new(new DataSlice(Path.GetFullPath(slice.FilePath), slice.Offset, slice.Length), value, "WEM", owner));
            return value;
        }

        public bool Equal(DataSlice left, DataSlice right, ResourceIdentity? leftOwner, ResourceIdentity? rightOwner,
            CancellationToken token)
        {
            if (left == right) return true;
            if (CompareSlices(left, right) > 0)
            {
                (left, right) = (right, left);
                (leftOwner, rightOwner) = (rightOwner, leftOwner);
            }
            var key = (left, right);
            if (_equal.TryGetValue(key, out var cached))
            {
                _resolutionCoverageComplete &= cached.CoverageComplete;
                ReplayEqualityDigests(cached.Digests, left, right, leftOwner, rightOwner);
                return cached.Matches;
            }
            token.ThrowIfCancellationRequested();
            var comparison = EqualSlicesAndCapture(left, right, leftOwner, rightOwner, token);
            _equal.Add(key, new CachedEquality(comparison.Matches, comparison.Digests, comparison.CoverageComplete));
            _resolutionCoverageComplete &= comparison.CoverageComplete;
            ReplayEqualityDigests(comparison.Digests, left, right, leftOwner, rightOwner);
            return comparison.Matches;
        }

        private (bool Matches, IReadOnlyList<AudioMetadataReadDigest> Digests, bool CoverageComplete) EqualSlicesAndCapture(DataSlice left, DataSlice right, ResourceIdentity? leftOwner,
            ResourceIdentity? rightOwner, CancellationToken token)
        {
            if (left.Length != right.Length) return (false, [], true);
            using var first = new FileStream(left.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var second = new FileStream(right.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            first.Position = left.Offset; second.Position = right.Offset;
            var a = new byte[64 * 1024]; var b = new byte[a.Length]; var remaining = left.Length;
            var digests = new List<AudioMetadataReadDigest>();
            var coverageComplete = true;
            long consumed = 0;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested(); var wanted = (int)Math.Min(a.Length, remaining);
                var readA = first.Read(a, 0, wanted); var readB = second.Read(b, 0, wanted);
                if (readA != wanted || readB != wanted) throw new EndOfStreamException("A media candidate changed during comparison.");
                if (digests.Count <= MaximumResolutionDigests - 2)
                {
                    digests.Add(new(new DataSlice(Path.GetFullPath(left.FilePath), checked(left.Offset + consumed), wanted),
                        Convert.ToHexString(SHA256.HashData(a.AsSpan(0, wanted))).ToLowerInvariant(), "WEM", leftOwner));
                    digests.Add(new(new DataSlice(Path.GetFullPath(right.FilePath), checked(right.Offset + consumed), wanted),
                        Convert.ToHexString(SHA256.HashData(b.AsSpan(0, wanted))).ToLowerInvariant(), "WEM", rightOwner));
                }
                else coverageComplete = false;
                if (!a.AsSpan(0, wanted).SequenceEqual(b.AsSpan(0, wanted))) return (false, digests, coverageComplete);
                remaining -= wanted; consumed += wanted;
            }
            return (true, digests, coverageComplete);
        }

        private void ReplayEqualityDigests(IEnumerable<AudioMetadataReadDigest> digests, DataSlice left, DataSlice right,
            ResourceIdentity? leftOwner, ResourceIdentity? rightOwner)
        {
            foreach (var digest in digests)
            {
                var owner = IsWithin(digest.Slice, left) ? leftOwner : IsWithin(digest.Slice, right) ? rightOwner : digest.Owner;
                AddResolutionDigest(digest with { Owner = owner });
            }
        }

        private void AddResolutionDigest(AudioMetadataReadDigest digest)
        {
            var key = (digest.Slice, digest.Sha256, digest.Role ?? "WEM", digest.Owner?.Key);
            if (_resolutionDigests.ContainsKey(key)) return;
            if (_resolutionDigests.Count >= MaximumResolutionDigests)
            {
                _resolutionCoverageComplete = false;
                return;
            }
            _resolutionDigests.Add(key, digest);
        }

        private static bool IsWithin(DataSlice part, DataSlice source) =>
            StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(part.FilePath), Path.GetFullPath(source.FilePath)) &&
            part.Offset >= source.Offset && part.Length >= 0 && part.Length <= source.Length &&
            part.Offset - source.Offset <= source.Length - part.Length;

        private sealed record CachedEquality(bool Matches, IReadOnlyList<AudioMetadataReadDigest> Digests, bool CoverageComplete);

        private static int CompareSlices(DataSlice left, DataSlice right)
        {
            var path = StringComparer.OrdinalIgnoreCase.Compare(left.FilePath, right.FilePath);
            if (path != 0) return path;
            var offset = left.Offset.CompareTo(right.Offset);
            return offset != 0 ? offset : left.Length.CompareTo(right.Length);
        }
    }

    private static void EnrichEventsFromBank(AudioCatalog catalog, BankInfo bank, Dictionary<uint, string> namedEvents)
    {
        var existingKeys = catalog.Events.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var obj in bank.Objects.Values.Where(value => value.Type == 4))
        {
            var name = namedEvents.GetValueOrDefault(obj.Id, obj.Id.ToString(CultureInfo.InvariantCulture));
            var key = $"event:sound:{obj.Id:X8}:{bank.Key}";
            if (!existingKeys.Add(key)) continue;
            var audioEvent = new AudioEvent { Key = key, Identity = obj.Identity, Id = obj.Id, Name = name, BankKey = bank.Key };
            if (namedEvents.ContainsKey(obj.Id))
                audioEvent.Names.Add(new NameEvidence(name, "WwiseEvent", obj.Id, NameKind.Stored, "CSNDEVNT metadata"));
            catalog.Events.Add(audioEvent);
        }

    }

    private static void Unsupported(BankObject item, string reason)
    {
        item.Support = PlanSupport.Unsupported;
        item.Limitations.Add(reason);
    }

    private static void MarkNameCollisions(AudioCatalog catalog)
    {
        var groups = catalog.Names.Select((evidence, index) => (evidence, index))
            .GroupBy(item => (item.evidence.Namespace, item.evidence.Id));
        foreach (var group in groups)
        {
            if (group.Select(item => item.evidence.Text).Distinct(StringComparer.Ordinal).Skip(1).Any())
            {
                var collision = $"{group.Key.Namespace}:{group.Key.Id:X8}";
                foreach (var item in group)
                    catalog.Names[item.index] = item.evidence with { CollisionGroup = collision };
            }
        }
    }

    private static void PublishSnapshot(IProgress<AudioReadProgress>? progress, AudioCatalog catalog, string phase,
        int done, int total, string? detail, IReadOnlyList<MediaEntry>? changedMedia = null)
    {
        if (progress is null) return;
        var changedKeys = new HashSet<string>(StringComparer.Ordinal);
        if (changedMedia is not null)
            foreach (var media in changedMedia)
                if (media.Identity is { } identity) changedKeys.Add(identity.Key);
        var changedCopies = new List<MediaEntry>(changedMedia?.Count ?? 0);
        var snapshot = CreateSnapshot(catalog, includeDialogueHeaderAssociations: false,
            changedMediaKeys: changedKeys, changedMediaCopies: changedCopies);
        var changedIdentities = changedMedia is null ? Array.Empty<ResourceIdentity>() : changedMedia
            .Select(media => media.Identity).Where(identity => identity is not null).Select(identity => identity!)
            .Distinct().ToArray();
        progress.Report(new AudioReadProgress(phase, done, total, detail, snapshot)
        { ChangedMediaIdentities = changedIdentities, ChangedMedia = changedCopies });
    }

    private static AudioCatalog CreateSnapshot(AudioCatalog source, bool copyMediaMetadata = true,
        bool includeDialogueHeaderAssociations = true, IReadOnlySet<string>? changedMediaKeys = null,
        List<MediaEntry>? changedMediaCopies = null)
    {
        var snapshot = new AudioCatalog
            {
                SourcePath = source.SourcePath, Fingerprint = source.Fingerprint, IndexContentId = source.IndexContentId,
                PhysicalSourceStamp = source.PhysicalSourceStamp, SourceVolumes = source.SourceVolumes.ToList(),
                IndexStamp = source.IndexStamp,
                Resources = source.Resources.ToArray(), IndexedResourceCount = source.IndexedResourceCount,
                EventMediaVersion = source.EventMediaVersion,
                DialogueHeaderVersion = includeDialogueHeaderAssociations ? source.DialogueHeaderVersion : 0
            };
        if (includeDialogueHeaderAssociations) snapshot.DialogueHeaderAssociations.AddRange(source.DialogueHeaderAssociations);
        snapshot.Diagnostics.AddRange(source.Diagnostics);
        snapshot.Names.AddRange(source.Names);
        snapshot.Localization.AddRange(source.Localization);
        snapshot.SourceDeclarations.AddRange(source.SourceDeclarations);
        foreach (var bank in source.Banks)
        {
            var copy = new BankInfo { Key = bank.Key, Identity = bank.Identity, ContainerIdentity = bank.ContainerIdentity, Id = bank.Id, Name = bank.Name, Version = bank.Version, HasFeedback = bank.HasFeedback, LanguageId = bank.LanguageId, Slice = bank.Slice, IndexedObjectCount = bank.IndexedObjectCount };
            copy.MetadataReadDigests.AddRange(bank.MetadataReadDigests);
            copy.MetadataReadCoverageComplete = bank.MetadataReadCoverageComplete;
            copy.Names.AddRange(bank.Names);
            copy.Chunks.AddRange(bank.Chunks);
            copy.Diagnostics.AddRange(bank.Diagnostics);
            copy.AmbiguousObjectIds.UnionWith(bank.AmbiguousObjectIds);
            snapshot.Banks.Add(copy);
        }
        foreach (var media in source.Media)
        {
            var copy = new MediaEntry
            {
                Key = media.Key, Identity = media.Identity, PayloadIdentity = media.PayloadIdentity, Id = media.Id, BankKey = media.BankKey, BankName = media.BankName, Name = media.Name,
                DisplayLabel = media.DisplayLabel, RawLanguageId = media.RawLanguageId,
                Language = media.Language, Category = media.Category, Codec = media.Codec, SampleRate = media.SampleRate,
                Channels = media.Channels, Duration = media.Duration, MeasuredDuration = media.MeasuredDuration, Slice = media.Slice, Midi = media.Midi,
                OriginalSlice = media.OriginalSlice, DidxExtent = media.DidxExtent, State = media.State,
                NameKind = media.NameKind, IsRiff = media.IsRiff, Subtitle = media.Subtitle,
                MetadataReadCoverageComplete = media.MetadataReadCoverageComplete,
                Completeness = media.Completeness, SourceDeclarationProcessingComplete = media.SourceDeclarationProcessingComplete,
                ContainerValidity = media.ContainerValidity, Availability = media.Availability, ResolutionDetails = media.ResolutionDetails
            };
            if (copyMediaMetadata)
            {
                copy.MetadataReadDigests.AddRange(media.MetadataReadDigests);
                copy.SourceEvidence.AddRange(media.SourceEvidence);
                copy.Candidates.AddRange(media.Candidates); copy.CandidateRejections.AddRange(media.CandidateRejections);
                copy.EquivalenceEvidence.AddRange(media.EquivalenceEvidence);
            }
            else
            {
                copy.MetadataReadDigests = media.MetadataReadDigests;
                copy.SourceEvidence = media.SourceEvidence;
                copy.Candidates = media.Candidates; copy.CandidateRejections = media.CandidateRejections;
                copy.EquivalenceEvidence = media.EquivalenceEvidence;
            }
            copy.Aliases.AddRange(media.Aliases); copy.EventKeys.AddRange(media.EventKeys); copy.Names.AddRange(media.Names);
            snapshot.Media.Add(copy);
            if (changedMediaCopies is not null && copy.Identity is { } identity && changedMediaKeys?.Contains(identity.Key) == true)
                changedMediaCopies.Add(copy);
        }
        foreach (var item in source.Events)
        {
            var copy = new AudioEvent { Key = item.Key, Identity = item.Identity, Id = item.Id, Name = item.Name, BankKey = item.BankKey, IsDialogue = item.IsDialogue, Behavior = item.Behavior, LinkStatus = item.LinkStatus };
            copy.StateValues.AddRange(item.StateValues); copy.MediaLinks.AddRange(item.MediaLinks); copy.RelatedPlayEventKeys.AddRange(item.RelatedPlayEventKeys); copy.Diagnostics.AddRange(item.Diagnostics);
            copy.MediaKeys.AddRange(item.MediaKeys); copy.Names.AddRange(item.Names); snapshot.Events.Add(copy);
        }
        return snapshot;
    }

    private static string ParseLanguage(string name)
    {
        var suffix = name.AsSpan().TrimEnd();
        var underscore = suffix.LastIndexOf('_');
        if (underscore >= 0 && suffix.Length - underscore == 4)
        {
            var code = suffix[(underscore + 1)..].ToString().ToUpperInvariant();
            return code == "JAP" ? "JPN" : code;
        }
        return "SFX";
    }

    private static string LocalizationLanguageName(string code) => code.Equals("JAP", StringComparison.OrdinalIgnoreCase) ? "JPN" : code.ToUpperInvariant();

    private static string DialogueStem(string name)
    {
        var index = name.LastIndexOf('_');
        return index > 0 ? name[..index] : name;
    }

    private static uint Fnv1LowerUtf8(string value)
    {
        uint hash = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes(value.ToLowerInvariant()))
        {
            hash = unchecked(hash * 16777619);
            hash ^= b;
        }
        return hash;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, ref int position)
    {
        if (position < 0 || position > bytes.Length - 4) throw new InvalidDataException("A length-prefixed field is truncated.");
        var result = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(position, 4));
        position += 4;
        return result;
    }

    private readonly record struct ParsedDialogue(string Name, string Language, DataSlice MediaSlice, long AudioOffset, long AudioLength,
        MediaState State, bool IsRiff, string Codec, int SampleRate, int Channels, double? Duration, ContainerValidity Validity = ContainerValidity.Unknown);
    private readonly record struct ParsedProbe(DataSlice Slice, MediaState State, bool IsRiff, string Codec, int SampleRate, int Channels, double? Duration, ContainerValidity Validity = ContainerValidity.Unknown)
    {
        public List<AudioMetadataReadDigest>? ReadDigests { get; init; }
        public bool ReadCoverageComplete { get; init; }
    }

    private sealed class PackageReader : IDisposable
    {
        private const int MaximumMetadataDigestExtents = 65_536;
        private const long MaximumMetadataDigestBytes = 1L * 1024 * 1024 * 1024;
        private readonly Dictionary<string, FileStamp> _expectedStamps;
        private readonly ISourceRevisionProvider _revisionProvider;
        private readonly Dictionary<string, Microsoft.Win32.SafeHandles.SafeFileHandle> _handles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _lengths = new(StringComparer.OrdinalIgnoreCase);
        private MetadataReadScope? _currentMetadataReadScope;
        public PackageReader(ArchiveSnapshot snapshot, ISourceRevisionProvider? revisionProvider)
        {
            _expectedStamps = snapshot.Volumes.ToDictionary(volume => Path.GetFullPath(volume.FilePath), volume => volume.Stamp,
                StringComparer.OrdinalIgnoreCase);
            _revisionProvider = revisionProvider ?? new FileRevisionProvider();
        }

        public byte[] ReadResource(ResourceRecord resource, int maximum)
        {
            if (!resource.SourceReadable) throw new InvalidDataException("The archive record is not valid at the selected source revision.");
            if (resource.Slice.Length > maximum) throw new InvalidDataException("The resource exceeds the parser's bounded metadata read limit.");
            return ReadAt(resource.Slice.FilePath, resource.Slice.Offset, checked((int)resource.Slice.Length));
        }

        public bool TryReadResource(ResourceRecord resource, int maximum, out byte[] bytes)
        {
            bytes = [];
            if (!resource.SourceReadable || !File.Exists(resource.Slice.FilePath) || resource.Slice.Length > maximum) return false;
            try { bytes = ReadResource(resource, maximum); return true; }
            catch (IOException) { return false; }
        }

        public MetadataReadScope BeginMetadataReadScope(ResourceIdentity owner, string role, bool bankScope = false)
        {
            if (_currentMetadataReadScope is not null)
                throw new InvalidOperationException("Audio metadata read scopes cannot be nested.");
            var scope = new MetadataReadScope(owner, role, this, bankScope);
            _currentMetadataReadScope = scope;
            return scope;
        }

        public void AttachToCurrentMetadataReadScope(BankInfo bank) => _currentMetadataReadScope?.Attach(bank);
        public void AttachToCurrentMetadataReadScope(MediaEntry media) => _currentMetadataReadScope?.Attach(media);

        public byte[] ReadAt(string filePath, long offset, int length, string? role = null)
        {
            var handle = GetHandle(filePath);
            var totalLength = _lengths[filePath];
            if (offset < 0 || length < 0 || offset > totalLength || length > totalLength - offset)
                throw new InvalidDataException("The requested resource range falls outside its package file.");
            var bytes = new byte[length];
            ReadAtExactly(handle, offset, bytes);
            _currentMetadataReadScope?.Record(filePath, offset, bytes, role);
            ValidateSource(handle, filePath);
            return bytes;
        }

        public bool TryFindEmbeddedBank(DataSlice container, CancellationToken token, out long bankOffset, out long bankLength, out string diagnostic)
        {
            bankOffset = 0;
            bankLength = 0;
            diagnostic = "BankPayloadNotFound";
            if (container.Length <= 0 || !File.Exists(container.FilePath)) return false;
            var handle = GetHandle(container.FilePath);
            var fileLength = _lengths[container.FilePath];
            if (container.Offset < 0 || container.Offset > fileLength || container.Length > fileLength - container.Offset)
            {
                diagnostic = "MalformedBankContainerRange";
                return false;
            }

            if (!TryGetDataContainerPayload(container, out var payloadOffset, out var payloadLength, out var containerError))
            {
                diagnostic = "MalformedDataContainer_" + containerError;
                return false;
            }

            const int windowSize = 1024 * 1024;
            var buffer = new byte[windowSize + BankContainerMagic.Length - 1];
            var carry = 0;
            long consumed = 0;
            var allZeroContent = true;
            var sawMarker = false;
            var payloadEnd = checked(payloadOffset + payloadLength);
            while (consumed < payloadLength)
            {
                token.ThrowIfCancellationRequested();
                var count = (int)Math.Min(windowSize, payloadLength - consumed);
                var read = ReadAt(container.FilePath, checked(container.Offset + payloadOffset + consumed), count);
                read.CopyTo(buffer, carry);
                var contentStart = checked(payloadOffset + 16 - (payloadOffset + consumed));
                if (allZeroContent && contentStart < read.Length && read.AsSpan((int)Math.Max(0, contentStart)).IndexOfAnyExcept((byte)0) >= 0)
                    allZeroContent = false;
                var available = carry + read.Length;
                var relative = 0;
                while (relative <= available - BankContainerMagic.Length)
                {
                    var hit = buffer.AsSpan(relative, available - relative).IndexOf(BankContainerMagic);
                    if (hit < 0) break;
                    var markerInWindow = relative + hit;
                    var markerInContainer = payloadOffset + consumed - carry + markerInWindow;
                    sawMarker = true;
                    if (markerInContainer >= payloadOffset && markerInContainer <= payloadOffset + payloadLength - 16)
                    {
                        var prefix = ReadAt(container.FilePath, checked(container.Offset + markerInContainer), 16);
                        var candidateBankLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(12, 4));
                        var bankPayloadOffset = markerInContainer + 16;
                        if (candidateBankLength >= 12 && candidateBankLength <= payloadEnd - bankPayloadOffset &&
                            TryReadBankHeader(container.FilePath, checked(container.Offset + bankPayloadOffset), candidateBankLength, out _, out _))
                        {
                            bankOffset = bankPayloadOffset;
                            bankLength = candidateBankLength;
                            diagnostic = string.Empty;
                            return true;
                        }
                    }
                    relative = markerInWindow + 1;
                }
                carry = Math.Min(BankContainerMagic.Length - 1, available);
                Buffer.BlockCopy(buffer, available - carry, buffer, 0, carry);
                consumed += read.Length;
            }
            if (sawMarker) diagnostic = "MalformedBankPayload";
            else if (allZeroContent) diagnostic = "ZeroFilledBankContainer";
            return false;
        }

        public bool TryReadBankHeader(string filePath, long offset, long length, out uint version, out uint bankId)
        {
            version = 0;
            bankId = 0;
            if (length < 16) return false;
            if (!File.Exists(filePath)) return false;
            var handle = GetHandle(filePath);
            var fileLength = _lengths[filePath];
            if (offset < 0 || offset > fileLength || length > fileLength - offset) return false;
            var position = 0L;
            var chunks = 0;
            var foundHeader = false;
            while (position <= length - 8)
            {
                if (++chunks > 4096) return false;
                var header = ReadAt(filePath, checked(offset + position), 8);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
                if (size > length - position - 8) return false;
                if (header.AsSpan(0, 4).SequenceEqual("BKHD"u8))
                {
                    if (foundHeader || size < 8) return false;
                    var payload = ReadAt(filePath, checked(offset + position + 8), 8);
                    version = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
                    bankId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
                    foundHeader = true;
                }
                position = checked(position + 8 + size);
            }
            return foundHeader && position == length;
        }

        private bool TryGetDataContainerPayload(DataSlice container, out long payloadOffset, out long payloadLength, out string error)
        {
            payloadOffset = 0;
            payloadLength = 0;
            error = "Short";
            if (container.Length < 37) return false;
            var prefix = ReadAt(container.FilePath, container.Offset, (int)Math.Min(container.Length, 1024));
            error = "Magic";
            if (!prefix.AsSpan(0, 5).SequenceEqual("QZIP\0"u8) || !prefix.AsSpan(5, 8).SequenceEqual("DC_INFO "u8)) return false;
            var infoVersion = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(13, 4));
            var infoSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(17, 4));
            error = "InfoHeader";
            if (infoVersion != 3 || infoSize < 8 || infoSize > container.Length - 21 || infoSize > MaximumWrapperBytes || (infoSize - 8) % 8 != 0) return false;
            var infoBody = ReadAt(container.FilePath, checked(container.Offset + 21), checked((int)infoSize));
            var referenceCount = BinaryPrimitives.ReadUInt32LittleEndian(infoBody.AsSpan(4, 4));
            error = "InfoReferences";
            if (referenceCount != (infoSize - 8) / 8) return false;
            for (var index = 0; index < referenceCount; index++)
            {
                var referenceOffset = 8 + checked((int)index) * 8;
                _ = BinaryPrimitives.ReadUInt32LittleEndian(infoBody.AsSpan(referenceOffset, 4));
                _ = BinaryPrimitives.ReadUInt32LittleEndian(infoBody.AsSpan(referenceOffset + 4, 4));
            }
            var dataPosition = 21L + infoSize;
            error = "DataHeaderBounds";
            if (dataPosition > container.Length - 20) return false;
            var dataHeader = ReadAt(container.FilePath, checked(container.Offset + dataPosition), 20);
            error = "DataTag";
            if (!dataHeader.AsSpan(0, 8).SequenceEqual("DC_DATA "u8)) return false;
            var dataVersion = BinaryPrimitives.ReadUInt32LittleEndian(dataHeader.AsSpan(8, 4));
            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(dataHeader.AsSpan(12, 4));
            error = "DataVersion";
            if (dataVersion == 0) return false;
            payloadOffset = dataPosition + 16;
            payloadLength = dataSize;
            error = "DataBounds";
            if (payloadLength > container.Length - payloadOffset) return false;
            error = string.Empty;
            return true;
        }

        public ParsedProbe ProbeWem(string path, long offset, long length)
        {
            var handle = GetHandle(path);
            var totalLength = _lengths[path];
            if (offset < 0 || offset > totalLength || length > totalLength - offset)
                throw new InvalidDataException("The WEM slice does not belong to the referenced package segment.");
            var readDigests = new List<AudioMetadataReadDigest>();
            void CaptureProbeRead(long readOffset, byte[] bytes)
            {
                if (_currentMetadataReadScope?.RecordProbe(path, readOffset, bytes) is { } digest)
                    readDigests.Add(digest);
            }
            var probe = DetroitAudioSnapshotReader.ProbeWem(handle, offset, length, CaptureProbeRead);
            ValidateSource(handle, path);
            return probe with { Slice = probe.Slice with { FilePath = path }, ReadDigests = readDigests,
                ReadCoverageComplete = _currentMetadataReadScope?.CoverageComplete == true && readDigests.Count > 0 };
        }

        private Microsoft.Win32.SafeHandles.SafeFileHandle GetHandle(string path)
        {
            if (_handles.TryGetValue(path, out var existing)) return existing;
            var fullPath = Path.GetFullPath(path);
            if (!_expectedStamps.TryGetValue(fullPath, out var expected) || !expected.Exists)
                throw new ArchiveException("SourceUnavailable", "The resource does not map to an available archive volume.");
            var handle = SourceIdentity.OpenPinned(fullPath);
            try { SourceIdentity.EnsureUnchanged(_revisionProvider, handle, fullPath, expected); }
            catch { handle.Dispose(); throw; }
            _handles[path] = handle;
            _lengths[path] = RandomAccess.GetLength(handle);
            return handle;
        }

        private void ValidateSource(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!_expectedStamps.TryGetValue(fullPath, out var expected))
                throw new ArchiveException("SourceUnavailable", "The resource does not map to an archive volume.");
            SourceIdentity.EnsureUnchanged(_revisionProvider, handle, fullPath, expected);
        }

        public void Dispose()
        {
            _currentMetadataReadScope?.Dispose();
            _currentMetadataReadScope = null;
            foreach (var handle in _handles.Values) handle.Dispose();
            _handles.Clear();
        }

        private void EndMetadataReadScope(MetadataReadScope scope)
        {
            if (!ReferenceEquals(_currentMetadataReadScope, scope))
                throw new InvalidOperationException("Audio metadata read scopes must be disposed in order.");
            _currentMetadataReadScope = null;
            scope.Apply();
        }

        public sealed class MetadataReadScope : IDisposable
        {
            private readonly ResourceIdentity _owner;
            private readonly string _defaultRole;
            private readonly PackageReader _reader;
            private readonly bool _bankScope;
            private readonly Dictionary<(string Path, long Offset, long Length, string Role), AudioMetadataReadDigest> _digests = [];
            private readonly List<object> _attachments = [];
            private long _capturedBytes;
            private bool _coverageComplete = true;
            private bool _disposed;

            internal MetadataReadScope(ResourceIdentity owner, string defaultRole, PackageReader reader, bool bankScope)
            { _owner = owner; _defaultRole = defaultRole; _reader = reader; _bankScope = bankScope; }

            public List<AudioMetadataReadDigest> ReadDigests => _coverageComplete ? _digests.Values.ToList() : [];
            public bool CoverageComplete => _coverageComplete && _digests.Count > 0;

            public void Attach(BankInfo bank) => _attachments.Add(bank);
            public void Attach(MediaEntry media) => _attachments.Add(media);

            public AudioMetadataReadDigest? RecordProbe(string filePath, long offset, byte[] bytes) => _bankScope
                ? new(new DataSlice(Path.GetFullPath(filePath), offset, bytes.LongLength),
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "WEM", _owner)
                : Record(filePath, offset, bytes, "WEM");

            public AudioMetadataReadDigest? Record(string filePath, long offset, byte[] bytes, string? role = null)
            {
                if (_disposed || !_coverageComplete || bytes.Length == 0) return null;
                var normalizedRole = string.IsNullOrWhiteSpace(role) ? _defaultRole : role;
                var slice = new DataSlice(Path.GetFullPath(filePath), offset, bytes.LongLength);
                var key = (slice.FilePath, slice.Offset, slice.Length, normalizedRole);
                if (_digests.TryGetValue(key, out var existing)) return existing;
                if (_digests.Count >= MaximumMetadataDigestExtents || bytes.LongLength > MaximumMetadataDigestBytes - _capturedBytes)
                { _coverageComplete = false; _digests.Clear(); return null; }
                _capturedBytes += bytes.LongLength;
                var digest = new AudioMetadataReadDigest(slice, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    normalizedRole, _owner);
                _digests.Add(key, digest);
                return digest;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _reader.EndMetadataReadScope(this);
            }

            internal void Apply()
            {
                var values = _coverageComplete ? _digests.Values.ToList() : [];
                // Bank metadata is shared; each embedded clip retains its own WEM reads.
                var mediaValues = _attachments.Any(attachment => attachment is BankInfo)
                    ? values.Where(digest => digest.Role != "WEM").ToList() : values;
                foreach (var attachment in _attachments)
                {
                    if (attachment is BankInfo bank)
                    { bank.MetadataReadDigests = values.ToList(); bank.MetadataReadCoverageComplete = _coverageComplete && values.Count > 0; }
                    else if (attachment is MediaEntry media)
                    {
                        var hadPriorBaseline = media.MetadataReadDigests.Count > 0;
                        var merged = MergeMetadataReadDigests(media.MetadataReadDigests, mediaValues);
                        media.MetadataReadDigests = merged ?? [];
                        media.MetadataReadCoverageComplete = _coverageComplete && values.Count > 0 &&
                            (!hadPriorBaseline || media.MetadataReadCoverageComplete) && merged is not null;
                    }
                }
                _attachments.Clear();
            }
        }
    }
}
