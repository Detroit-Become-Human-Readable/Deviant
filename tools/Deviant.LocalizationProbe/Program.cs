using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Browser;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Localization.Model;
using Microsoft.Data.Sqlite;

internal static class Program
{
    private const int MaxResourceBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ProbeOptions.Parse(args);
            var snapshot = await new ArchiveReader().OpenAsync(options.IndexPath).ConfigureAwait(false);
            var output = Path.GetFullPath(options.OutputPath);
            var sourceRoot = Path.GetFullPath(snapshot.SourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (output.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The private report must be written outside the archive source directory.");

            var cacheFile = GetCatalogPath(snapshot, options.CacheRoot);
            if (!File.Exists(cacheFile))
                throw new FileNotFoundException("The requested warmed localization cache was not found; this probe never starts a cold localization scan.");

            var sample = await LoadCachedSampleAsync(snapshot, options.CacheRoot, cacheFile, options.MaximumRecords)
                .ConfigureAwait(false);
            var discovery = LocalizationDiscovery.Analyze(sample.Records,
                new LocalizationDiscoveryOptions(PrefixDepth: options.PrefixDepth,
                    MaximumPrefixGroups: options.MaximumPrefixGroups, SamplesPerPrefix: options.SamplesPerPrefix,
                    MaximumPlaceholderOccurrences: options.MaximumPlaceholderOccurrences));

            var (relationships, referenceSummary, inspectedLocalizationResources, inspectedDialogueHeaders) =
                await ProbeReferencesAsync(snapshot, options.ReferenceResourceLimit, options.PointerSourceOrdinal)
                .ConfigureAwait(false);
            var pointerInvestigation = await InspectPointerPayloadsAsync(snapshot, options.CacheRoot, cacheFile,
                sample.CacheVersion, relationships, options.PointerLimit, options.PointerResourceLimit,
                options.PointerSourceOrdinal, options.PointerSkipEligibleResources).ConfigureAwait(false);
            var localizedLinks = LocalizationDiscovery.ResolveDirectReferences(sample.Records, relationships);
            var dialogueAudioTargets = LocalizationDiscovery.ResolveDialogueAudioTargets(inspectedLocalizationResources,
                relationships, inspectedDialogueHeaders);
            var report = new ProbeReport(
                DateTimeOffset.UtcNow,
                snapshot.Records.Count,
                snapshot.Records.Count(record => record.Type == 1016),
                sample.CacheVersion,
                sample.CatalogRows,
                sample.Records.Count,
                discovery,
                referenceSummary,
                pointerInvestigation,
                new
                {
                    Total = dialogueAudioTargets.Count,
                    AudioResourceLinks = dialogueAudioTargets.Count(target => target.TargetResourceType == 1033 && target.Status == LocalizationReferenceLinkStatus.Linked),
                    DialogueEventLinks = dialogueAudioTargets.Count(target => target.TargetResourceType == 1031 && target.Status == LocalizationReferenceLinkStatus.Linked),
                    Unmatched = dialogueAudioTargets.Count(target => target.Status == LocalizationReferenceLinkStatus.Unmatched),
                    Ambiguous = dialogueAudioTargets.Count(target => target.Status == LocalizationReferenceLinkStatus.Ambiguous),
                    Unresolved = dialogueAudioTargets.Count(target => target.Status == LocalizationReferenceLinkStatus.Unresolved),
                    Unverified = dialogueAudioTargets.Count(target => target.Status == LocalizationReferenceLinkStatus.Unverified),
                    TargetResourceNotSampled = dialogueAudioTargets.Count(target => target.Status == LocalizationReferenceLinkStatus.TargetResourceNotSampled)
                },
                new
                {
                    DirectType1016References = localizedLinks.Count,
                    Linked = localizedLinks.Count(link => link.Status == LocalizationReferenceLinkStatus.Linked),
                    Unmatched = localizedLinks.Count(link => link.Status == LocalizationReferenceLinkStatus.Unmatched),
                    Ambiguous = localizedLinks.Count(link => link.Status == LocalizationReferenceLinkStatus.Ambiguous),
                    Unresolved = localizedLinks.Count(link => link.Status == LocalizationReferenceLinkStatus.Unresolved),
                    Unverified = localizedLinks.Count(link => link.Status == LocalizationReferenceLinkStatus.Unverified),
                    TargetResourceNotSampled = localizedLinks.Count(link => link.Status == LocalizationReferenceLinkStatus.TargetResourceNotSampled)
                });

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(stream, report, JsonOptions).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            Console.WriteLine($"Analyzed {sample.Records.Count:N0} cached occurrences; report saved as {Path.GetFileName(output)}.");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or SqliteException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static async Task<CachedSample> LoadCachedSampleAsync(ArchiveSnapshot snapshot, string cacheRoot,
        string cacheFile, int maximumRecords)
    {
        var revision = SourceIdentity.DerivedCacheKey(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        var versions = await ReadLocalizationVersionsAsync(cacheFile, revision).ConfigureAwait(false);
        var itemVersion = versions.OrderByDescending(pair => pair.Value).Select(pair => pair.Key).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(itemVersion))
            throw new InvalidOperationException("The warmed cache contains no localization record items.");

        using var features = FeatureStore.Open(snapshot, cacheRoot);
        var catalogRows = features.Count(new AssetQuery(Family: LocalizationSchema.AssetFamily));
        if (catalogRows == 0) throw new InvalidOperationException("The warmed cache contains no localization catalog rows.");

        var languages = await ReadCachedLanguagesAsync(cacheFile, revision).ConfigureAwait(false);
        var limitPerLanguage = Math.Max(1, maximumRecords / Math.Max(1, languages.Count));
        var records = new List<LocalizationRecord>(maximumRecords);
        var sampledIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var language in languages)
        {
            if (records.Count >= maximumRecords) break;
            var languageQuery = new AssetQuery(Language: language, Family: LocalizationSchema.AssetFamily, Limit: 1);
            var languageRows = features.Count(languageQuery);
            var requested = Math.Min(limitPerLanguage, maximumRecords - records.Count);
            var segmentSize = Math.Max(1, (requested + 2) / 3);
            var offsets = languageRows <= requested
                ? new long[] { 0 }
                : new[] { 0L, Math.Max(0, languageRows / 2 - segmentSize / 2), Math.Max(0, languageRows - segmentSize) };
            foreach (var offset in offsets.Distinct())
            {
                if (records.Count >= maximumRecords) break;
                var room = maximumRecords - records.Count;
                var descriptors = features.Query(languageQuery with
                {
                    Offset = offset,
                    Limit = (int)Math.Min(segmentSize, room)
                });
                LoadDescriptors(descriptors, itemVersion, features, records, sampledIdentities, maximumRecords);
            }
        }

        if (records.Count < maximumRecords)
        {
            var room = maximumRecords - records.Count;
            var descriptors = features.Query(new AssetQuery(Family: LocalizationSchema.AssetFamily, Limit: room));
            LoadDescriptors(descriptors, itemVersion, features, records, sampledIdentities, maximumRecords);
        }
        if (records.Count == 0)
            throw new InvalidOperationException("The warmed cache did not contain localization items matching its indexed descriptors.");
        return new(itemVersion, catalogRows, records.AsReadOnly());
    }

    private static void LoadDescriptors(IReadOnlyList<AssetDescriptor> descriptors, string version,
        FeatureStore features, List<LocalizationRecord> records, HashSet<string> sampledIdentities, int targetCount)
    {
        foreach (var descriptor in descriptors)
        {
            if (records.Count >= targetCount) break;
            if (!sampledIdentities.Add(descriptor.Identity.Key)) continue;
            var record = features.LoadItem<LocalizationRecord>(LocalizationSchema.ItemCollection, descriptor.Identity.Key, version);
            if (record is not null) records.Add(record);
            else sampledIdentities.Remove(descriptor.Identity.Key);
        }
    }

    private static async Task<Dictionary<string, long>> ReadLocalizationVersionsAsync(string databasePath, string revision)
    {
        await using var connection = await OpenCatalogReadOnlyAsync(databasePath).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, COUNT(*) FROM asset_features WHERE revision=$revision AND instr(feature,$prefix)=1 GROUP BY version";
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$prefix", LocalizationSchema.ItemCollection + "/item/");
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var rows = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await rows.ReadAsync().ConfigureAwait(false)) result.Add(rows.GetString(0), rows.GetInt64(1));
        return result;
    }

    private static async Task<IReadOnlyList<string>> ReadCachedLanguagesAsync(string databasePath, string revision)
    {
        await using var connection = await OpenCatalogReadOnlyAsync(databasePath).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT language FROM assets_v2 WHERE revision=$revision AND family=$family AND language IS NOT NULL ORDER BY language";
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$family", LocalizationSchema.AssetFamily);
        var result = new List<string>();
        await using var rows = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await rows.ReadAsync().ConfigureAwait(false)) result.Add(rows.GetString(0));
        return result;
    }

    private static async Task<SqliteConnection> OpenCatalogReadOnlyAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task<(IReadOnlyList<AssetRelationship> Relationships, ReferenceSummary Summary,
        IReadOnlyList<ResourceIdentity> LocalizationResources, IReadOnlyList<ResourceIdentity> DialogueHeaders)> ProbeReferencesAsync(
        ArchiveSnapshot snapshot, int resourceLimit, long? pointerSourceOrdinal)
    {
        var referenceReader = new LocalizationReferenceReader();
        var relationships = new List<AssetRelationship>();
        var candidateResources = snapshot.Records.Where(record => record.Type is 4091 or 1016).Take(
            pointerSourceOrdinal is null ? resourceLimit : Math.Max(0, resourceLimit - 1)).ToList();
        if (pointerSourceOrdinal is { } selectedOrdinal &&
            !candidateResources.Any(record => record.Ordinal == selectedOrdinal))
        {
            var selected = snapshot.Records.SingleOrDefault(record => record.Ordinal == selectedOrdinal && record.Type == 1016);
            if (selected is not null) candidateResources.Add(selected);
        }
        var candidates = candidateResources.ToArray();
        long scannedContainers = 0;
        long malformedContainers = 0;
        long skippedBySize = 0;
        long localizationResourcesWithoutContainer = 0;
        var coReferencedDialoguePairs = new HashSet<long>();
        var relationCounts = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var resource in candidates)
        {
            var identity = new ResourceIdentity(snapshot.IndexContentId, resource.Ordinal);
            LocalizationReferenceReadResult read;
            try
            {
                read = await referenceReader.ReadAsync(snapshot, identity).ConfigureAwait(false);
            }
            catch (ArchiveException exception) when (exception.Code == "ReferenceResourceSizeLimit")
            { skippedBySize++; continue; }
            catch (ArchiveException exception) when (exception.Code is "ReferenceCountLimit" or "MalformedReferenceTable" or "UnsupportedReferenceContainerVersion")
            {
                if (resource.Type == 4091) malformedContainers++;
                else localizationResourcesWithoutContainer++;
                continue;
            }
            if (read.Status != LocalizationReferenceReadStatus.Verified)
            {
                if (resource.Type == 4091) malformedContainers++;
                else localizationResourcesWithoutContainer++;
                continue;
            }

            scannedContainers++;
            var uniqueAudio = false;
            var uniqueDialogue = false;
            foreach (var relationship in read.Relationships)
            {
                relationships.Add(relationship);
                var countKey = $"{resource.Type}->{relationship.Reference.ResourceType}:{relationship.Resolution.Status}";
                relationCounts[countKey] = relationCounts.GetValueOrDefault(countKey) + 1;
                if (resource.Type == 4091 && relationship.Reference.ResourceType == 1033 && relationship.Resolution.Status == ReferenceStatus.Unique) uniqueAudio = true;
                if (resource.Type == 4091 && relationship.Reference.ResourceType == 1031 && relationship.Resolution.Status == ReferenceStatus.Unique) uniqueDialogue = true;
            }
            if (resource.Type == 4091 && uniqueAudio && uniqueDialogue) coReferencedDialoguePairs.Add(resource.Ordinal);
        }

        return (relationships.AsReadOnly(), new(scannedContainers, malformedContainers, skippedBySize,
            localizationResourcesWithoutContainer,
            candidates.Count(resource => resource.Type == 4091), candidates.Count(resource => resource.Type == 1016),
            coReferencedDialoguePairs.Count, relationCounts),
            candidates.Where(resource => resource.Type == 1016).Select(resource => new ResourceIdentity(snapshot.IndexContentId, resource.Ordinal)).ToArray(),
            candidates.Where(resource => resource.Type == 4091).Select(resource => new ResourceIdentity(snapshot.IndexContentId, resource.Ordinal)).ToArray());
    }

    private static async Task<PointerInvestigationSummary> InspectPointerPayloadsAsync(ArchiveSnapshot snapshot,
        string cacheRoot, string cacheFile, string cacheVersion, IReadOnlyList<AssetRelationship> sourceRelationships,
        int pointerLimit, int resourceLimit, long? pointerSourceOrdinal, int skipEligibleResources)
    {
        var resolver = new ArchiveResourceResolver(snapshot);
        var byTypeAndId = snapshot.Records.GroupBy(record => (record.Type, record.Id))
            .ToDictionary(group => group.Key, group => group.ToArray());
        using var features = FeatureStore.Open(snapshot, cacheRoot);
        var examinedResources = 0;
        var eligibleResources = 0;
        var attempted = 0;
        var validRecordExtents = 0;
        var validFieldExtents = 0;
        var payloadBytesMatchedRecord = 0;
        var exactReferenceMatches = 0;
        var uniqueHeaderMatches = 0;
        var uniqueAudioLinks = 0;
        var uniqueEventLinks = 0;
        var headerIdWindowMatches = 0;
        var uniqueHeaderIdWordMatches = 0;
        var ambiguousHeaderIdWordMatches = 0;
        var audioIdWindowMatches = 0;
        var eventIdWindowMatches = 0;
        var headerAudioIdWindowMatches = 0;
        var headerEventIdWindowMatches = 0;
        var sourceTypeIdPairMatches = 0;
        var reorderedFullReferenceMatches = 0;
        var reorderedReferenceCandidateRows = 0;
        var uniquePermutedHeaderReferences = 0;
        var missingPermutedHeaderTargets = 0;
        var ambiguousPermutedHeaderTargets = 0;
        var headerIdOffsets = new Dictionary<string, long>(StringComparer.Ordinal);
        var audioIdOffsets = new Dictionary<string, long>(StringComparer.Ordinal);
        var eventIdOffsets = new Dictionary<string, long>(StringComparer.Ordinal);
        var headerAudioIdOffsets = new Dictionary<string, long>(StringComparer.Ordinal);
        var headerEventIdOffsets = new Dictionary<string, long>(StringComparer.Ordinal);
        var sourceTypeIdPairMatchesByOffsetAndType = new Dictionary<string, long>(StringComparer.Ordinal);
        var ambiguousReferenceMatches = 0;
        var missingHeaderTargets = 0;
        var malformedHeaderResources = 0;
        var verifiedPointerRecords = new List<LocalizationRecord>(pointerLimit);
        var versionCounts = new Dictionary<string, long>(StringComparer.Ordinal);

        var pointerResources = snapshot.Records.Where(record => record.Type == 1016 &&
                (pointerSourceOrdinal is null || record.Ordinal == pointerSourceOrdinal))
            .Take(resourceLimit);
        foreach (var resource in pointerResources)
        {
            if (attempted >= pointerLimit) break;
            examinedResources++;
            if (resource.StoredSize is <= 0 or > MaxResourceBytes) continue;
            var sourceIdentity = new ResourceIdentity(snapshot.IndexContentId, resource.Ordinal);
            var sourceBytes = await resolver.ReadAsync(sourceIdentity, 0, checked((int)resource.StoredSize)).ConfigureAwait(false);
            if (!TryParseComContainer(sourceBytes, out var containerReferences, out var referenceOffset)) continue;
            var uniqueHeaderReferences = containerReferences
                .Where(reference => reference.Type == 4091)
                .Where(reference => byTypeAndId.TryGetValue((reference.Type, reference.Id), out var rows) && rows.Length == 1)
                .ToArray();
            if (uniqueHeaderReferences.Length == 0) continue;
            eligibleResources++;
            if (eligibleResources <= skipEligibleResources) continue;
            var uniqueHeaderIds = uniqueHeaderReferences.Select(reference => reference.Id).ToHashSet();
            var uniqueAudioIds = containerReferences.Where(reference => reference.Type == 1033)
                .Where(reference => byTypeAndId.TryGetValue((reference.Type, reference.Id), out var rows) && rows.Length == 1)
                .Select(reference => reference.Id).ToHashSet();
            var uniqueEventIds = containerReferences.Where(reference => reference.Type == 1031)
                .Where(reference => byTypeAndId.TryGetValue((reference.Type, reference.Id), out var rows) && rows.Length == 1)
                .Select(reference => reference.Id).ToHashSet();

            var descriptors = features.Query(new AssetQuery(GroupKey: sourceIdentity.Key, Codec: LocalizationRecordKind.Pointer.ToString(),
                Family: LocalizationSchema.AssetFamily, Limit: 1000));
            foreach (var descriptor in descriptors)
            {
                if (attempted >= pointerLimit) break;
                var record = features.LoadItem<LocalizationRecord>(LocalizationSchema.ItemCollection,
                    descriptor.Identity.Key, cacheVersion);
                if (record is null || record.Kind != LocalizationRecordKind.Pointer || record.Status != LocalizationRecordStatus.Unresolved ||
                    record.PointerKind != 1 || record.PointerPayloadBase64 is null) continue;
                attempted++;
                var versionKey = record.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
                versionCounts[versionKey] = versionCounts.GetValueOrDefault(versionKey) + 1;

                var actualResourceEnd = checked(resource.Offset + resource.StoredSize);
                if (record.RecordExtent.Offset < resource.Offset || record.RecordExtent.Length <= 0 ||
                    record.RecordExtent.Offset > actualResourceEnd - record.RecordExtent.Length ||
                    record.RecordExtent.Length > int.MaxValue) continue;

                var recordRelativeOffset = record.RecordExtent.Offset - resource.Offset;
                var recordBytes = await resolver.ReadAsync(sourceIdentity, recordRelativeOffset,
                    checked((int)record.RecordExtent.Length)).ConfigureAwait(false);
                if (recordBytes.LongLength == record.RecordExtent.Length) validRecordExtents++;

                var fieldsValid = true;
                byte[]? payload = null;
                var seenFields = new HashSet<string>(StringComparer.Ordinal);
                var orderedFieldExtents = record.FieldExtents.OrderBy(field => field.Offset).ToArray();
                var previousFieldEnd = record.RecordExtent.Offset;
                foreach (var field in orderedFieldExtents)
                {
                    var fieldEnd = checked(field.Offset + field.Length);
                    if (field.RelativeSource != record.RecordExtent.RelativeSource || field.Offset < record.RecordExtent.Offset ||
                        field.Length < 0 || fieldEnd > record.RecordExtent.Offset + record.RecordExtent.Length)
                    { fieldsValid = false; continue; }
                    if (field.Offset < previousFieldEnd) fieldsValid = false;
                    previousFieldEnd = fieldEnd;
                    seenFields.Add(field.Field);
                    var fieldSlice = recordBytes.AsSpan(checked((int)(field.Offset - record.RecordExtent.Offset)), checked((int)field.Length));
                    if (field.Field == "PointerKind" && (field.Length != 1 || fieldSlice[0] != record.PointerKind)) fieldsValid = false;
                    if (field.Field == "PointerKey" && !string.Equals(Encoding.ASCII.GetString(fieldSlice), record.Key, StringComparison.Ordinal)) fieldsValid = false;
                    if (field.Field == "PointerPayload")
                    {
                        if (field.Length != 9) { fieldsValid = false; continue; }
                        payload = fieldSlice.ToArray();
                    }
                }
                var requiredFields = new[] { "RecordHeader", "PointerKeyLength", "PointerKey", "PointerKind", "PointerPayload" };
                if (requiredFields.Any(field => !seenFields.Contains(field))) fieldsValid = false;
                if (fieldsValid && recordBytes.Length >= 8)
                {
                    var pointerCount = BinaryPrimitives.ReadUInt32LittleEndian(recordBytes.AsSpan(4, 4));
                    if (record.UnknownFields.TryGetValue("pointerCount", out var pointerCountValue) &&
                        (!uint.TryParse(pointerCountValue, out var declaredCount) || declaredCount != pointerCount)) fieldsValid = false;
                }
                if (fieldsValid && payload is not null && Convert.ToBase64String(payload) == record.PointerPayloadBase64)
                {
                    validFieldExtents++;
                    payloadBytesMatchedRecord++;
                }
                else
                {
                    continue;
                }
                verifiedPointerRecords.Add(record);

                var headerIdMatched = false;
                var audioIdMatched = false;
                var eventIdMatched = false;
                var matchedHeaderIds = new HashSet<uint>();
                for (var byteOffset = 0; byteOffset <= payload.Length - sizeof(uint); byteOffset++)
                {
                    var word = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(byteOffset, sizeof(uint)));
                    if (uniqueHeaderIds.Contains(word))
                    {
                        headerIdMatched = true;
                        matchedHeaderIds.Add(word);
                        var key = byteOffset.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        headerIdOffsets[key] = headerIdOffsets.GetValueOrDefault(key) + 1;
                    }
                    if (uniqueAudioIds.Contains(word))
                    {
                        audioIdMatched = true;
                        var key = byteOffset.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        audioIdOffsets[key] = audioIdOffsets.GetValueOrDefault(key) + 1;
                    }
                    if (uniqueEventIds.Contains(word))
                    {
                        eventIdMatched = true;
                        var key = byteOffset.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        eventIdOffsets[key] = eventIdOffsets.GetValueOrDefault(key) + 1;
                    }
                }
                if (headerIdMatched) headerIdWindowMatches++;
                if (audioIdMatched) audioIdWindowMatches++;
                if (eventIdMatched) eventIdWindowMatches++;
                for (var byteOffset = 0; byteOffset <= 1; byteOffset++)
                {
                    var type = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(byteOffset, sizeof(uint)));
                    var id = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(byteOffset + sizeof(uint), sizeof(uint)));
                    var matchingRows = Enumerable.Range(0, containerReferences.Count)
                        .Where(index => containerReferences[index].Type == type && containerReferences[index].Id == id)
                        .ToArray();
                    if (matchingRows.Length > 0)
                    {
                        sourceTypeIdPairMatches++;
                        var key = $"{byteOffset}:{type}";
                        sourceTypeIdPairMatchesByOffsetAndType[key] = sourceTypeIdPairMatchesByOffsetAndType.GetValueOrDefault(key) + 1;
                        if (byteOffset == 1)
                        {
                            reorderedReferenceCandidateRows += matchingRows.Length;
                            var matchingFlagRows = matchingRows.Where(index =>
                                sourceBytes[referenceOffset + index * 9 + 8] == payload[0]).ToArray();
                            if (matchingFlagRows.Length > 0)
                            {
                                reorderedFullReferenceMatches++;
                                if (type == 4091)
                                {
                                    if (!byTypeAndId.TryGetValue((4091u, id), out var matchingHeaderRows) || matchingHeaderRows.Length == 0)
                                        missingPermutedHeaderTargets++;
                                    else if (matchingHeaderRows.Length > 1 || matchingFlagRows.Length > 1)
                                        ambiguousPermutedHeaderTargets++;
                                    else
                                        uniquePermutedHeaderReferences++;
                                }
                            }
                        }
                    }
                }

                if (matchedHeaderIds.Count == 1)
                {
                    uniqueHeaderIdWordMatches++;
                    var matchedHeaderId = matchedHeaderIds.Single();
                    if (byTypeAndId.TryGetValue((4091u, matchedHeaderId), out var matchedHeaderRows) && matchedHeaderRows.Length == 1 &&
                        matchedHeaderRows[0].StoredSize is > 0 and <= MaxResourceBytes)
                    {
                        var matchedHeaderIdentity = new ResourceIdentity(snapshot.IndexContentId, matchedHeaderRows[0].Ordinal);
                        var matchedHeaderBytes = await resolver.ReadAsync(matchedHeaderIdentity, 0,
                            checked((int)matchedHeaderRows[0].StoredSize)).ConfigureAwait(false);
                        if (TryParseComContainer(matchedHeaderBytes, out var matchedHeaderReferences, out _))
                        {
                            foreach (var targetType in new uint[] { 1033, 1031 })
                            {
                                var ids = matchedHeaderReferences.Where(reference => reference.Type == targetType)
                                    .Select(reference => reference.Id).Distinct().ToArray();
                                if (ids.Length != 1 || !byTypeAndId.TryGetValue((targetType, ids[0]), out var targetRows) ||
                                    targetRows.Length != 1 || !HasValidResourceExtent(snapshot, targetRows[0])) continue;
                                if (targetType == 1033) uniqueAudioLinks++;
                                else uniqueEventLinks++;
                                for (var byteOffset = 0; byteOffset <= payload.Length - sizeof(uint); byteOffset++)
                                {
                                    if (BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(byteOffset, sizeof(uint))) != ids[0]) continue;
                                    var offsetKey = byteOffset.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                    if (targetType == 1033)
                                    {
                                        headerAudioIdWindowMatches++;
                                        headerAudioIdOffsets[offsetKey] = headerAudioIdOffsets.GetValueOrDefault(offsetKey) + 1;
                                    }
                                    else
                                    {
                                        headerEventIdWindowMatches++;
                                        headerEventIdOffsets[offsetKey] = headerEventIdOffsets.GetValueOrDefault(offsetKey) + 1;
                                    }
                                }
                            }
                        }
                    }
                }
                else if (matchedHeaderIds.Count > 1)
                    ambiguousHeaderIdWordMatches++;

                var matchingReferenceIndexes = new List<int>();
                for (var index = 0; index < containerReferences.Count; index++)
                {
                    var rowOffset = checked(referenceOffset + index * 9);
                    if (sourceBytes.AsSpan(rowOffset, 9).SequenceEqual(payload)) matchingReferenceIndexes.Add(index);
                }
                if (matchingReferenceIndexes.Count > 1)
                {
                    ambiguousReferenceMatches++;
                    continue;
                }
                if (matchingReferenceIndexes.Count == 0) continue;
                exactReferenceMatches++;
                var matched = containerReferences[matchingReferenceIndexes[0]];
                if (matched.Type is 1033 or 1031 && byTypeAndId.TryGetValue((matched.Type, matched.Id), out var directRows) && directRows.Length == 1)
                {
                    if (matched.Type == 1033) uniqueAudioLinks++;
                    else uniqueEventLinks++;
                    continue;
                }
                if (matched.Type != 4091) continue;
                if (!byTypeAndId.TryGetValue((4091u, matched.Id), out var headerRows) || headerRows.Length == 0)
                {
                    missingHeaderTargets++;
                    continue;
                }
                if (headerRows.Length != 1) { ambiguousReferenceMatches++; continue; }
                uniqueHeaderMatches++;

                var header = headerRows[0];
                if (header.StoredSize is <= 0 or > MaxResourceBytes) { malformedHeaderResources++; continue; }
                var headerIdentity = new ResourceIdentity(snapshot.IndexContentId, header.Ordinal);
                var headerBytes = await resolver.ReadAsync(headerIdentity, 0, checked((int)header.StoredSize)).ConfigureAwait(false);
                if (!TryParseComContainer(headerBytes, out var headerReferences, out _))
                { malformedHeaderResources++; continue; }

                foreach (var targetType in new uint[] { 1033, 1031 })
                {
                    var targets = headerReferences.Where(reference => reference.Type == targetType)
                        .Select(reference => reference.Id).Distinct().ToArray();
                    if (targets.Length != 1) continue;
                    if (byTypeAndId.TryGetValue((targetType, targets[0]), out var targetRows) && targetRows.Length == 1)
                    {
                        if (targetType == 1033) uniqueAudioLinks++;
                        else uniqueEventLinks++;
                    }
                }
            }
        }

        var pointerLinks = LocalizationDiscovery.ResolvePointerReferences(verifiedPointerRecords, sourceRelationships);
        var pointerLinkStatuses = pointerLinks.GroupBy(link => link.Status.ToString(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (long)group.Count(), StringComparer.Ordinal);
        var pointerTargetTypes = pointerLinks.Where(link => link.Relationship is not null)
            .GroupBy(link => link.TargetResourceType.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (long)group.Count(), StringComparer.Ordinal);

        return new(examinedResources, eligibleResources, attempted, validRecordExtents, validFieldExtents,
            payloadBytesMatchedRecord, exactReferenceMatches, uniqueHeaderMatches, uniqueAudioLinks,
            uniqueEventLinks, headerIdWindowMatches, audioIdWindowMatches, uniqueHeaderIdWordMatches,
            ambiguousHeaderIdWordMatches, eventIdWindowMatches,
            headerAudioIdWindowMatches, headerEventIdWindowMatches, sourceTypeIdPairMatches,
            reorderedFullReferenceMatches, reorderedReferenceCandidateRows, uniquePermutedHeaderReferences,
            missingPermutedHeaderTargets, ambiguousPermutedHeaderTargets,
            headerIdOffsets, audioIdOffsets, eventIdOffsets, headerAudioIdOffsets, headerEventIdOffsets,
            sourceTypeIdPairMatchesByOffsetAndType,
            ambiguousReferenceMatches, missingHeaderTargets, malformedHeaderResources,
            pointerLinks.Count, pointerLinks.Count(link => link.Status == LocalizationReferenceLinkStatus.Linked && link.TargetResourceType == 4091),
            pointerLinkStatuses, pointerTargetTypes, versionCounts);
    }

    private static bool TryParseComContainer(ReadOnlySpan<byte> bytes, out List<ContainerReference> references,
        out int referenceOffset)
    {
        references = [];
        referenceOffset = 20;
        if (bytes.Length < 20 || !bytes[..8].SequenceEqual("COM_CONT"u8)) return false;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..12]);
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..20]);
        if (version != 6 || count > 4096 || payloadLength > bytes.Length - 16 || 20L + count * 9L > bytes.Length) return false;
        for (var index = 0; index < count; index++)
        {
            var offset = referenceOffset + checked((int)index * 9);
            references.Add(new(BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4)), bytes[offset + 8]));
        }
        return true;
    }

    private static bool HasValidResourceExtent(ArchiveSnapshot snapshot, ResourceRecord resource)
    {
        if (resource.Validity != ResourceValidity.Valid || resource.Offset < 0 || resource.StoredSize <= 0) return false;
        var volume = snapshot.Volumes.SingleOrDefault(candidate => candidate.Package == resource.Package);
        return volume is not null && volume.Stamp.Exists && resource.Offset <= volume.Stamp.Length - resource.StoredSize;
    }

    private static string GetCatalogPath(ArchiveSnapshot snapshot, string cacheRoot)
    {
        var indexKey = SourceIdentity.IndexCacheKey(snapshot.IndexContentId);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(IndexLayout.ParserVersion + "\n" + indexKey))).ToLowerInvariant();
        return Path.Combine(cacheRoot, "catalog-" + digest + ".sqlite3");
    }

    private sealed record ContainerReference(uint Type, uint Id, byte Trailer);
    private sealed record CachedSample(string CacheVersion, long CatalogRows, IReadOnlyList<LocalizationRecord> Records);
    private sealed record ReferenceSummary(long ScannedContainers, long MalformedContainers, long SkippedBySize,
        long LocalizationResourcesWithoutContainer,
        int DialogueHeadersExamined, int LocalizationResourcesExamined, int UniqueCoReferencedAudioDialogueHeaders,
        IReadOnlyDictionary<string, long> CountsBySourceTargetAndStatus);
    private sealed record ProbeReport(DateTimeOffset GeneratedAt, int IndexedResources, int LocalizationResources,
        string CacheVersion, long CachedLocalizationRows, int SampleOccurrences,
        LocalizationDiscoveryResult Discovery, ReferenceSummary References,
        PointerInvestigationSummary PointerInvestigation, object DialogueAudioTargets, object DirectLocalizationLinks);
    private sealed record PointerInvestigationSummary(int LocalizationResourcesExamined, int ResourcesWithUniqueDialogueHeaderReference,
        int PointerKindOneOccurrencesExamined, int ValidRecordExtentCount, int ValidFieldExtentCount,
        int PayloadsMatchingStoredOccurrence, int ExactContainerReferenceMatches, int UniqueDialogueHeaderMatches,
        int UniqueAudioTargets, int UniqueDialogueEventTargets, int HeaderIdWindowMatches, int AudioIdWindowMatches,
        int UniqueHeaderIdWordMatches, int AmbiguousHeaderIdWordMatches, int DialogueEventIdWindowMatches,
        int HeaderAudioIdWindowMatches, int HeaderDialogueEventIdWindowMatches,
        int SourceTypeIdPairMatches, int ReorderedFullReferenceMatches, int ReorderedReferenceCandidateRows,
        int UniquePermutedHeaderReferences, int MissingPermutedHeaderTargets, int AmbiguousPermutedHeaderTargets,
        IReadOnlyDictionary<string, long> HeaderIdMatchesByByteOffset,
        IReadOnlyDictionary<string, long> AudioIdMatchesByByteOffset,
        IReadOnlyDictionary<string, long> DialogueEventIdMatchesByByteOffset,
        IReadOnlyDictionary<string, long> HeaderAudioIdMatchesByByteOffset,
        IReadOnlyDictionary<string, long> HeaderDialogueEventIdMatchesByByteOffset,
        IReadOnlyDictionary<string, long> SourceTypeIdPairMatchesByByteOffsetAndType,
        int AmbiguousReferenceMatches,
        int MissingDialogueHeaderTargets, int MalformedDialogueHeaders,
        int PerKeyReferenceLinks, int PerKeyUniqueHeaderLinks,
        IReadOnlyDictionary<string, long> PerKeyReferenceStatuses,
        IReadOnlyDictionary<string, long> PerKeyTargetTypes,
        IReadOnlyDictionary<string, long> OccurrenceVersions);

    private sealed record ProbeOptions(string IndexPath, string CacheRoot, string OutputPath, int MaximumRecords,
        int PrefixDepth, int MaximumPrefixGroups, int SamplesPerPrefix, int MaximumPlaceholderOccurrences,
        int ReferenceResourceLimit, int PointerLimit, int PointerResourceLimit, long? PointerSourceOrdinal,
        int PointerSkipEligibleResources)
    {
        public static ProbeOptions Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < args.Length; index++)
            {
                if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                    throw new ArgumentException("Expected --name value arguments.");
                values[args[index][2..]] = args[++index];
            }
            string Required(string key) => values.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing --{key}.");
            int Number(string key, int fallback, int minimum, int maximum) => values.TryGetValue(key, out var value)
                ? int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum
                    ? parsed : throw new ArgumentException($"--{key} must be between {minimum} and {maximum}.")
                : fallback;
            long? OptionalOrdinal(string key) => !values.TryGetValue(key, out var value) ? null :
                long.TryParse(value, out var parsed) && parsed >= 0 ? parsed :
                throw new ArgumentException($"--{key} must be a nonnegative resource ordinal.");
            return new(Required("index"), Required("cache"), Required("out"),
                Number("limit", 2500, 1, 100_000), Number("prefix-depth", 1, 1, 8),
                Number("prefix-groups", 200, 1, 10_000), Number("prefix-samples", 3, 0, 100),
                Number("placeholder-limit", 10_000, 0, 1_000_000), Number("reference-limit", 128, 1, 4096),
                Number("pointer-limit", 16, 0, 16), Number("pointer-resource-limit", 32, 1, 64),
                OptionalOrdinal("pointer-source-ordinal"), Number("pointer-skip-eligible", 0, 0, 128));
        }
    }
}
