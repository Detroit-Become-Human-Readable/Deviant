using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Export;
using Deviant.Formats.Localization;
using Deviant.Formats.Localization.Model;
using Microsoft.Win32.SafeHandles;

namespace Deviant.Export.Localization;

public sealed class LocalizationExportService(ArchiveSnapshot snapshot, FeatureStore featureStore,
    PublishedLocalizationGeneration<LocalizationGenerationHead> generation,
    ISourceRevisionProvider? revisionProvider = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly ISourceRevisionProvider revisions = revisionProvider ?? new FileRevisionProvider();

    public async Task<LocalizationExportReport> ExportAsync(LocalizationExportSelection selection,
        LocalizationExportFormat format, string destination, IProgress<LocalizationReadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if ((selection.Identities is null) == (selection.MatchingQuery is null))
            throw new ArgumentException("Choose either concrete localization identities or one matching query.", nameof(selection));
        var output = Path.GetFullPath(destination);
        OutputSafety.ValidateFilePath(output, snapshot.SourceRoot);
        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        using var parentPin = OutputSafety.PinDirectory(directory);
        OutputSafety.ValidateFilePath(output, snapshot.SourceRoot);
        var manifestPath = output + ".manifest.json";
        OutputSafety.ValidateFilePath(manifestPath, snapshot.SourceRoot);
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var manifestTemporary = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var started = DateTimeOffset.UtcNow;
        var matchedCount = selection.Identities?.Count ?? await CountAsync(selection.MatchingQuery!, CancellationToken.None).ConfigureAwait(false);
        var counts = new ExportCounts(matchedCount);
        var sourceHandles = new Dictionary<uint, SafeFileHandle>();
        SafeFileHandle? indexHandle = null;
        var committedData = false;
        string? outputHash = null;
        string? failureDiagnostic = null;
        try
        {
            indexHandle = SourceIdentity.OpenPinned(snapshot.IndexPath);
            SourceIdentity.EnsureUnchanged(revisions, indexHandle, snapshot.IndexPath, snapshot.IndexStamp);
            if (!string.Equals(await SourceIdentity.HashAsync(indexHandle, snapshot.IndexStamp.Length, CancellationToken.None).ConfigureAwait(false), snapshot.IndexContentId, StringComparison.Ordinal))
                throw new ArchiveException("SourceChanged", "The archive index contents differ from the selected localization revision.");
            await PinLocalizationVolumesAsync(indexHandle, sourceHandles, CancellationToken.None).ConfigureAwait(false);
            await VerifyUnreliableResourcesAsync(selection, matchedCount, cancellationToken).ConfigureAwait(false);

            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                if (format == LocalizationExportFormat.Json)
                    await WriteJsonAsync(stream, selection, counts, progress, cancellationToken).ConfigureAwait(false);
                else
                    await WriteCsvAsync(stream, selection, counts, progress, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            SourceIdentity.EnsureUnchanged(revisions, indexHandle, snapshot.IndexPath, snapshot.IndexStamp);
            foreach (var (package, handle) in sourceHandles)
            {
                var volume = snapshot.Volumes.Single(item => item.Package == package);
                SourceIdentity.EnsureUnchanged(revisions, handle, volume.FilePath, volume.Stamp);
            }
            OutputSafety.ValidateFilePath(output, snapshot.SourceRoot);
            outputHash = await HashFileAsync(temporary, CancellationToken.None).ConfigureAwait(false);
            File.Move(temporary, output, overwrite: true);
            committedData = true;
        }
        catch (OperationCanceledException)
        {
            counts.SetCancelledRemaining();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            var detail = ex is ArchiveException archive ? $"{archive.Code}: {ex.Message}" : ex.Message;
            failureDiagnostic = SanitizeDiagnostic(detail, snapshot, output);
            counts.FailAll();
        }
        finally
        {
            indexHandle?.Dispose();
            foreach (var handle in sourceHandles.Values) handle.Dispose();
            if (!committedData)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
            }
        }
        if (!committedData) outputHash = null;

        var manifest = new LocalizationExportManifest(1, Path.GetFileName(snapshot.IndexPath), snapshot.IndexContentId,
            snapshot.PhysicalSourceStamp, format, started, DateTimeOffset.UtcNow, counts.Cancelled > 0,
            matchedCount, counts.Completed, counts.Failed, counts.Cancelled, outputHash, failureDiagnostic);
        try
        {
            await using (var stream = new FileStream(manifestTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                32 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new LocalizationExportManifestFile(manifest, outputHash),
                    JsonOptions, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (!SourceIdentity.SameStamp(snapshot.IndexStamp, revisions.Capture(snapshot.IndexPath)))
                throw new ArchiveException("SourceChanged", "The archive index changed before the localization report was committed.");
            foreach (var volume in snapshot.Volumes.Where(volume => volume.Stamp.Exists))
                if (!SourceIdentity.SameStamp(volume.Stamp, revisions.Capture(volume.FilePath)))
                    throw new ArchiveException("SourceChanged", "An archive volume changed before the localization report was committed.");
            OutputSafety.ValidateFilePath(manifestPath, snapshot.SourceRoot);
            File.Move(manifestTemporary, manifestPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            var failedManifest = manifest with { FailureDiagnostic = SanitizeDiagnostic(ex.Message, snapshot, output) };
            throw new LocalizationManifestWriteException(failedManifest, manifestPath, output, ex);
        }
        finally
        {
            try { if (File.Exists(manifestTemporary)) File.Delete(manifestTemporary); }
            catch (IOException) { }
        }
        return new LocalizationExportReport(manifest, output, manifestPath);
    }

    private async Task<long> CountAsync(LocalizationQuery query, CancellationToken token)
    {
        var assetQuery = ToAssetQuery(query with { Offset = 0, Limit = 10_000 });
        var page = await Task.Run(() => featureStore.QueryLocalizationPage<LocalizationGenerationHead, LocalizationRecord>(
            generation, assetQuery with { Limit = 1 }, token), token).ConfigureAwait(false);
        return page.Total;
    }

    private async Task VerifyUnreliableResourcesAsync(LocalizationExportSelection selection, long matchedCount,
        CancellationToken token)
    {
        var unreliablePackages = snapshot.Volumes.Where(volume => !volume.Stamp.Reliable)
            .Select(volume => volume.Package).ToHashSet();
        if (unreliablePackages.Count == 0) return;

        var resources = snapshot.Records.Where(item => item.Type == 1016 && unreliablePackages.Contains(item.Package)).ToArray();
        if (resources.Length == 0) return;
        var volumes = snapshot.Volumes.ToDictionary(item => item.Package);

        async Task<Dictionary<string, LocalizationRecord>> ReadResourceAsync(ResourceRecord resource)
        {
            token.ThrowIfCancellationRequested();
            var parsed = await new LocalizationSnapshotReader(revisions)
                .ReadResourceAsync(snapshot, resource, token).ConfigureAwait(false);
            return parsed.Records.ToDictionary(record => record.Identity.Key, StringComparer.Ordinal);
        }

        async Task VerifyAsync(ResourceIdentity identity, ResourceRecord resource,
            IReadOnlyDictionary<string, LocalizationRecord> currentRecords)
        {
            token.ThrowIfCancellationRequested();
            if (identity.IndexContentId != snapshot.IndexContentId) return;
            var cached = await Task.Run(() => featureStore.LoadLocalizationRecord<LocalizationGenerationHead, LocalizationRecord>(
                generation, identity.Key, token), token).ConfigureAwait(false);
            if (cached is null) return;
            if (cached.ResourceOrdinal != resource.Ordinal ||
                !currentRecords.TryGetValue(identity.Key, out var fresh) || !RecordsSemanticallyEqual(cached, fresh))
                throw new ArchiveException("SourceChanged", "A selected localization record no longer matches the source resource.");
        }

        if (selection.Identities is { } identities)
        {
            foreach (var group in identities.Where(identity => identity.IndexContentId == snapshot.IndexContentId)
                         .OrderBy(identity => identity.Ordinal).GroupBy(identity => identity.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (group.Key < 0 || group.Key >= snapshot.Records.Count) continue;
                var resource = snapshot.Records[checked((int)group.Key)];
                if (resource.Ordinal != group.Key || resource.Type != 1016 || !unreliablePackages.Contains(resource.Package)) continue;
                var currentRecords = await ReadResourceAsync(resource).ConfigureAwait(false);
                foreach (var identity in group)
                    await VerifyAsync(identity, resource, currentRecords).ConfigureAwait(false);
            }
            return;
        }

        var query = selection.MatchingQuery!;
        var baseQuery = ToAssetQuery(query with { Offset = 0, Limit = 10_000 });
        long expectedUnreliableRows = 0;
        long validatedUnreliableRows = 0;
        foreach (var resource in resources)
        {
            token.ThrowIfCancellationRequested();
            var owner = new ResourceIdentity(snapshot.IndexContentId, resource.Ordinal);
            if (query.Source is { } selectedSource && selectedSource.Key != owner.Key) continue;
            if (!volumes.TryGetValue(resource.Package, out var volume) || volume.Stamp.Reliable) continue;
            var resourceQuery = baseQuery with { GroupKey = owner.Key };
            var countPage = await Task.Run(() => featureStore.QueryLocalizationPage<LocalizationGenerationHead, LocalizationRecord>(
                generation, resourceQuery with { Limit = 1 }, token), token).ConfigureAwait(false);
            var resourceCount = countPage.Total;
            if (resourceCount == 0) continue;
            expectedUnreliableRows = checked(expectedUnreliableRows + resourceCount);

            var currentRecords = await ReadResourceAsync(resource).ConfigureAwait(false);
            long offset = 0;
            while (offset < resourceCount)
            {
                token.ThrowIfCancellationRequested();
                var pageLimit = (int)Math.Min(10_000, resourceCount - offset);
                var page = await Task.Run(() => featureStore.QueryLocalizationPage<LocalizationGenerationHead, LocalizationRecord>(
                    generation, resourceQuery with { Offset = offset, Limit = pageLimit }, token), token).ConfigureAwait(false);
                if (page.Records.Count == 0) break;
                foreach (var record in page.Records)
                {
                    token.ThrowIfCancellationRequested();
                    await VerifyAsync(record.Identity, resource, currentRecords).ConfigureAwait(false);
                    offset++;
                }
            }
            if (offset < resourceCount)
                throw new ArchiveException("CatalogChanged", "The localization catalog changed during source validation; fewer rows were available than selected.");
            validatedUnreliableRows = checked(validatedUnreliableRows + offset);
        }
        if (validatedUnreliableRows != expectedUnreliableRows)
            throw new ArchiveException("CatalogChanged", "The localization catalog changed during source validation; not all matching records were checked.");
        if (expectedUnreliableRows > matchedCount)
            throw new ArchiveException("CatalogChanged", "The localization catalog changed during source validation; matching source counts exceeded the selected query count.");
    }

    private static bool RecordsSemanticallyEqual(LocalizationRecord left, LocalizationRecord right) =>
        left.Identity.Key == right.Identity.Key && left.ResourceIdentity.Key == right.ResourceIdentity.Key &&
        left.ResourceOrdinal == right.ResourceOrdinal && left.ResourceType == right.ResourceType && left.Version == right.Version &&
        left.LanguageIndex == right.LanguageIndex && left.RawLanguageCode == right.RawLanguageCode && left.Language == right.Language &&
        left.RecordOffset == right.RecordOffset && left.KeyIndex == right.KeyIndex && left.Kind == right.Kind && left.Status == right.Status &&
        left.Key == right.Key && left.Text == right.Text && left.Encoding == right.Encoding && left.RecordExtent == right.RecordExtent &&
        left.FieldExtents.SequenceEqual(right.FieldExtents) && left.UnknownFields.Count == right.UnknownFields.Count &&
        left.UnknownFields.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SequenceEqual(right.UnknownFields.OrderBy(pair => pair.Key, StringComparer.Ordinal)) &&
        left.PointerKind == right.PointerKind && left.PointerPayloadBase64 == right.PointerPayloadBase64 && left.Diagnostic == right.Diagnostic;

    private async Task WriteJsonAsync(Stream stream, LocalizationExportSelection selection, ExportCounts counts,
        IProgress<LocalizationReadProgress>? progress, CancellationToken token)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("sourceIndex", Path.GetFileName(snapshot.IndexPath));
        writer.WriteString("indexContentId", snapshot.IndexContentId);
        writer.WriteString("physicalSourceStamp", snapshot.PhysicalSourceStamp);
        writer.WriteStartArray("entries");
        try
        {
            await VisitSelectionAsync(selection, counts.Matched, (entry, index, total) =>
            {
                JsonSerializer.Serialize(writer, entry, JsonOptions);
                counts.Mark(entry.Status);
                try { progress?.Report(new("Exporting localization JSON", index, total,
                    counts.Completed + counts.Failed + counts.Cancelled, entry.Record?.Key)); }
                catch { /* Progress observers do not affect the export. */ }
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { counts.SetCancelledRemaining(); }
        writer.WriteEndArray();
        writer.WriteNumber("matchedCount", counts.Matched);
        writer.WriteNumber("completedCount", counts.Completed);
        writer.WriteNumber("failedCount", counts.Failed);
        writer.WriteNumber("cancelledCount", counts.Cancelled);
        writer.WriteBoolean("cancelled", counts.Cancelled > 0);
        writer.WriteEndObject();
        await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WriteCsvAsync(Stream stream, LocalizationExportSelection selection, ExportCounts counts,
        IProgress<LocalizationReadProgress>? progress, CancellationToken token)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024, leaveOpen: true);
        await writer.WriteLineAsync("status,identity,resourceOrdinal,recordOffset,recordLength,languageRaw,language,key,text,recordKind,recordStatus,source,recordExtents,fieldExtents,pointerKind,pointerPayloadBase64,diagnostic,version,resourceType,languageIndex,keyIndex,encoding,unknownFields,errorCode");
        try
        {
            await VisitSelectionAsync(selection, counts.Matched, (entry, index, total) =>
            {
                writer.WriteLine(CsvLine(entry));
                counts.Mark(entry.Status);
                try { progress?.Report(new("Exporting localization CSV", index, total,
                    counts.Completed + counts.Failed + counts.Cancelled, entry.Record?.Key)); }
                catch { /* Progress observers do not affect the export. */ }
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { counts.SetCancelledRemaining(); }
        await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task VisitSelectionAsync(LocalizationExportSelection selection, long matchedCount,
        Action<LocalizationExportEntry, long, long> visit, CancellationToken token)
    {
        if (selection.Identities is { } identities)
        {
            for (var index = 0; index < identities.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var identity = identities[index];
                LocalizationExportEntry entry;
                if (identity.IndexContentId != snapshot.IndexContentId)
                    entry = new(null, LocalizationExportStatus.Failed, "StaleIdentity", "The selected identity belongs to another archive revision.") { Identity = identity };
                else
                {
                    try
                    {
                        var record = await Task.Run(() => featureStore.LoadLocalizationRecord<LocalizationGenerationHead, LocalizationRecord>(
                            generation, identity.Key, token), token).ConfigureAwait(false);
                        entry = record is null
                            ? new(null, LocalizationExportStatus.Failed, "MissingCachedRecord", "The selected localization record is not in the current catalog.") { Identity = identity }
                            : new(record, LocalizationExportStatus.Completed);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    { entry = new(null, LocalizationExportStatus.Failed, ex.GetType().Name, SanitizeDiagnostic(ex.Message, snapshot, snapshot.IndexPath)) { Identity = identity }; }
                }
                visit(entry, index + 1, identities.Count);
            }
            return;
        }

        var query = selection.MatchingQuery!;
        var assetQuery = ToAssetQuery(query with { Offset = 0, Limit = 10_000 });
        long offset = 0;
        while (offset < matchedCount)
        {
            token.ThrowIfCancellationRequested();
            var pageLimit = (int)Math.Min(10_000, matchedCount - offset);
            var page = await Task.Run(() => featureStore.QueryLocalizationPage<LocalizationGenerationHead, LocalizationRecord>(
                generation, assetQuery with { Offset = offset, Limit = pageLimit }, token), token).ConfigureAwait(false);
            if (page.Records.Count == 0) break;
            foreach (var record in page.Records)
            {
                token.ThrowIfCancellationRequested();
                var entry = new LocalizationExportEntry(record, LocalizationExportStatus.Completed);
                offset++;
                visit(entry, offset, matchedCount);
            }
        }
        if (offset < matchedCount)
            throw new ArchiveException("CatalogChanged", "The localization catalog changed during export; fewer rows were available than the selected query matched.");
    }

    private AssetQuery ToAssetQuery(LocalizationQuery query) => new(
        Search: string.IsNullOrWhiteSpace(query.Search) ? null : query.Search,
        GroupKey: query.Source?.Key,
        Language: query.Language,
        Codec: query.Kind?.ToString(),
        Availability: query.Availability,
        Offset: query.Offset,
        Limit: query.Limit,
        Family: LocalizationSchema.AssetFamily,
        ExactKey: query.ExactKey,Sort:query.Sort,Descending:query.Descending);

    private static string CsvLine(LocalizationExportEntry entry)
    {
        var row = entry.Record;
        var fields = new[]
        {
            entry.Status.ToString(), row?.Identity.Key ?? entry.Identity?.Key ?? "",
            row?.ResourceOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? entry.Identity?.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            row?.RecordOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", row?.RecordExtent.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            row?.RawLanguageCode ?? "", row?.Language ?? "", row?.Key ?? "", row?.Text ?? "", row?.Kind.ToString() ?? "",
            row?.Status.ToString() ?? "", row?.RecordExtent.RelativeSource ?? "", JsonSerializer.Serialize(row?.RecordExtent),
            JsonSerializer.Serialize(row?.FieldExtents ?? []), row?.PointerKind?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            row?.PointerPayloadBase64 ?? "", entry.Diagnostic ?? row?.Diagnostic ?? "",
            row?.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            row?.ResourceType.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            row?.LanguageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            row?.KeyIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            row?.Encoding ?? "", JsonSerializer.Serialize(row?.UnknownFields ?? new Dictionary<string, string>()), entry.ErrorCode ?? ""
        };
        return string.Join(',', fields.Select(EscapeCsv));
    }

    private static string EscapeCsv(string value) => value.IndexOfAny([',', '"', '\r', '\n']) < 0
        ? value : '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private async Task PinLocalizationVolumesAsync(SafeFileHandle index,
        Dictionary<uint, SafeFileHandle> handles, CancellationToken token)
    {
        foreach (var volume in snapshot.Volumes)
        {
            token.ThrowIfCancellationRequested();
            if (!volume.Stamp.Exists || volume.Stamp.Error is not null) continue;
            var handle = SourceIdentity.OpenPinned(volume.FilePath);
            try { SourceIdentity.EnsureUnchanged(revisions, handle, volume.FilePath, volume.Stamp); }
            catch { handle.Dispose(); throw; }
            handles.Add(volume.Package, handle);
        }
        SourceIdentity.EnsureUnchanged(revisions, index, snapshot.IndexPath, snapshot.IndexStamp);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
    }

    private sealed record LocalizationExportManifestFile(LocalizationExportManifest Manifest, string? DataSha256);

    private static string SanitizeDiagnostic(string message, ArchiveSnapshot source, string outputPath)
    {
        foreach (var path in new[] { source.IndexPath, source.SourceRoot, outputPath, Path.GetTempPath(),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.CurrentDirectory }
                 .Where(value => !string.IsNullOrWhiteSpace(value)).OrderByDescending(value => value.Length))
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            message = message.Replace(full, "<local-path>", StringComparison.OrdinalIgnoreCase)
                .Replace(full.Replace('\\', '/'), "<local-path>", StringComparison.OrdinalIgnoreCase)
                .Replace(full.Replace('/', '\\'), "<local-path>", StringComparison.OrdinalIgnoreCase);
        }
        foreach (var volume in source.Volumes.OrderByDescending(volume => volume.FilePath.Length))
            message = message.Replace(Path.GetFullPath(volume.FilePath), $"<source-volume:{volume.RelativeName}>", StringComparison.OrdinalIgnoreCase);
        return message;
    }

    private sealed class ExportCounts(long matched)
    {
        public long Matched { get; } = matched;
        public long Completed { get; private set; }
        public long Failed { get; private set; }
        public long Cancelled { get; private set; }

        public void Mark(LocalizationExportStatus status)
        {
            switch (status)
            {
                case LocalizationExportStatus.Completed: Completed++; break;
                case LocalizationExportStatus.Failed: Failed++; break;
                case LocalizationExportStatus.Cancelled: Cancelled++; break;
            }
        }

        public void SetCancelledRemaining() => Cancelled = Math.Max(Cancelled, Matched - Completed - Failed);
        public void FailAll() { Completed = 0; Failed = Matched; Cancelled = 0; }
    }
}
