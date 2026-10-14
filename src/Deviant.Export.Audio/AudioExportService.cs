using System.Security.Cryptography;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deviant.Assets;
using Deviant.Audio;
using Deviant.Core;
using Deviant.Export;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;
using Microsoft.Win32.SafeHandles;

namespace Deviant.Export.Audio;

public sealed record AudioExportSource(ResourceIdentity Identity, MediaEntry Media);
public sealed record AudioExportRequest(ArchiveSnapshot Snapshot, IReadOnlyList<AudioExportSource> Sources, bool ClassificationComplete);

public enum AudioExportFormat { Original, Wav, Ogg }
public enum AudioExportStatus { Pending, Completed, Failed, Cancelled, Skipped }

public sealed record AudioExportOptions(string OutputDirectory, AudioExportFormat Format,
    WavFormat WavFormat = WavFormat.Pcm16, bool Overwrite = false, bool AllowPartialOriginal = false, int ConversionWorkers = 0);

public sealed record AudioExportEntry(
    string Identity, long Ordinal, string? MemberPath, uint MediaId, string SourceVolume, long SourceOffset, long SourceLength, string? SourceSha256, string RelativeOutput,
    MediaCompleteness Completeness, SourceAvailability Availability, string? Codec, int? SourceSampleRate, int? SourceChannels,
    AudioExportFormat Format, AudioOperation? Operation, int? OutputSampleRate, int? OutputChannels,
    string? OutputPrecision, bool? SampleRateChanged, bool? ChannelsChanged, long? OutputLength, string? Sha256,
    string? DecoderVersion, AudioExportStatus Status, string? ErrorCode = null, string? Diagnostic = null,
    string? RelatedDescriptorIdentity = null, uint? BankId = null,
    string? PayloadIdentity = null, long? PayloadOrdinal = null, string? PayloadMemberPath = null);

public sealed record AudioExportManifest(int SchemaVersion, string SourceIndex, string IndexContentId, string PhysicalSourceStamp,
    string Verification, string ToolFingerprint, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, bool Cancelled,
    AudioExportOptionsSummary Options, IReadOnlyList<AudioExportEntry> Entries);

public sealed record AudioExportOptionsSummary(AudioExportFormat Format, WavFormat WavFormat, bool Overwrite, bool AllowPartialOriginal, int ConversionWorkers);

public sealed record AudioExportReport(AudioExportManifest Manifest, string ManifestPath);

public sealed class AudioManifestWriteException(AudioExportManifest manifest, string path, Exception innerException)
    : IOException("AudioManifestWriteFailed: exported outputs were retained; the completed report remains available in memory.", innerException)
{
    public AudioExportManifest Manifest { get; } = manifest;
    public string ManifestPath { get; } = path;
}

public sealed class AudioExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly AudioService audioService;
    private readonly ISourceRevisionProvider revisions;
    private readonly string[] privateRoots;
    public string ToolFingerprint { get; }

    public AudioExportService(AudioService audioService, string toolFingerprint = "unknown",
        ISourceRevisionProvider? revisionProvider = null, IEnumerable<string>? privateRoots = null)
    {
        this.audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
        ToolFingerprint = string.IsNullOrWhiteSpace(toolFingerprint) ? "unknown" : toolFingerprint;
        revisions = revisionProvider ?? new FileRevisionProvider();
        this.privateRoots = privateRoots?.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray() ?? [];
    }

    public async Task<AudioExportReport> ExportAsync(AudioExportRequest review, AudioExportOptions options,
        IProgress<AudioProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(options);
        if (!review.ClassificationComplete)
            throw new ArchiveException("ClassificationPending", "Audio export cannot run until source declarations finish loading.");
        var snapshot = review.Snapshot;
        var root = Path.GetFullPath(options.OutputDirectory);
        OutputSafety.ValidateOutputRoot(root, snapshot.SourceRoot);
        if (review.Sources.Count == 0) throw new ArchiveException("EmptySelection", "No audio resources were selected for export.");
        Directory.CreateDirectory(root);
        using var rootPin = OutputSafety.PinDirectory(root);
        var started = DateTimeOffset.UtcNow;
        var audioCatalog = BuildCatalog(review, snapshot);
        var entries = new AudioExportEntry[review.Sources.Count];
        var items = new (AudioExportSource Source, AudioPayloadBinding Binding, string Relative)?[review.Sources.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < review.Sources.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = review.Sources[i];
            var media = source.Media;
            var identity = source.Identity;
            if (identity.IndexContentId != snapshot.IndexContentId || identity.Ordinal < 0 || identity.Ordinal >= snapshot.Records.Count ||
                !seen.Add(identity.Key))
                throw new ArchiveException("InvalidSelection", "An audio selection is duplicated or belongs to a different archive revision.");
            var logicalOwner = snapshot.Records[(int)identity.Ordinal];
            var logicalVolume = snapshot.Volumes.SingleOrDefault(value => value.Package == logicalOwner.Package);
            var fallbackVolumeName = logicalVolume?.RelativeName ?? logicalOwner.VolumeName;
            var exportExtent = options.Format == AudioExportFormat.Original
                ? media.OriginalSlice ?? media.DidxExtent ?? media.Slice : media.Slice;
            var extension = options.Format switch { AudioExportFormat.Original => media.Midi is not null || media.Codec == "MIDI" ? ".mid" : ".wem", AudioExportFormat.Wav => ".wav", _ => ".ogg" };
            var identityHash = SourceIdentity.HashFields("deviant-audio-export-name-v1", [identity.Key]);
            var relative = $"audio/p{logicalOwner.Package:D2}/id_{media.Id:X8}_r{logicalOwner.Ordinal:D8}_{identityHash[..12]}{extension}";
            entries[i] = CreateEntry(source, fallbackVolumeName, relative, options, AudioExportStatus.Pending);
            if (media.Identity?.Key != identity.Key)
            {
                entries[i] = FailOrSkip(entries[i], "InvalidMediaIdentity", SanitizeDiagnostic("The selected audio row does not match its logical archive identity.", snapshot, root), AudioExportStatus.Failed);
                continue;
            }
            if (options.Format == AudioExportFormat.Original)
            {
                if (!MediaPolicy.HasBytes(media))
                {
                    entries[i] = FailOrSkip(entries[i], "SourceUnavailable", SanitizeDiagnostic(MediaPolicy.GetUnavailableReason(media), snapshot, root), AudioExportStatus.Failed);
                    continue;
                }
                if (media.Completeness == MediaCompleteness.Fragment && !options.AllowPartialOriginal)
                {
                    entries[i] = FailOrSkip(entries[i], "PartialConfirmationRequired", SanitizeDiagnostic("Partial fragments require explicit permission for original-byte export.", snapshot, root), AudioExportStatus.Skipped);
                    continue;
                }
            }
            else if (!MediaPolicy.CanConvert(media))
            {
                entries[i] = FailOrSkip(entries[i], "ConversionUnavailable", SanitizeDiagnostic(MediaPolicy.GetUnavailableReason(media), snapshot, root), AudioExportStatus.Failed);
                continue;
            }
            AudioPayloadBinding binding;
            try
            {
                binding = AudioResourceBinding.Resolve(snapshot, media, original: options.Format == AudioExportFormat.Original);
            }
            catch (ArchiveException ex)
            {
                entries[i] = FailOrSkip(entries[i], ex.Code, SanitizeDiagnostic(ex.Message, snapshot, root), AudioExportStatus.Failed);
                continue;
            }
            var relativeVolume = binding.Volume.RelativeName;
            var payloadOwner = binding.PayloadOwner;
            relative = $"audio/p{payloadOwner.Package:D2}/id_{media.Id:X8}_r{payloadOwner.Ordinal:D8}_{identityHash[..12]}{extension}";
            entries[i] = CreateEntry(source, binding, relative, options, AudioExportStatus.Pending);
            _ = OutputSafety.ResolveOutputPath(root, relative);
            items[i] = (source, binding, relative);
        }

        using var metadataVerifier=new AudioMetadataVerifier(snapshot,revisions).CreateLease();
        SafeFileHandle? index = null;
        (string Code, string Message)? indexFailure = null;
        try
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    index = SourceIdentity.OpenPinned(snapshot.IndexPath);
                    SourceIdentity.EnsureUnchanged(revisions, index, snapshot.IndexPath, snapshot.IndexStamp);
                    if (!string.Equals(await SourceIdentity.HashAsync(index, snapshot.IndexStamp.Length, cancellationToken).ConfigureAwait(false), snapshot.IndexContentId, StringComparison.Ordinal))
                        throw new ArchiveException("SourceChanged", "The archive index contents differ from the selected revision.");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    indexFailure = (ex is ArchiveException archive ? archive.Code : ex.GetType().Name,
                        SanitizeDiagnostic(ex.Message, snapshot, root));
                }
            }
            var workerCount = options.ConversionWorkers is 1 or 2 or 4 ? options.ConversionWorkers : 2;
            var completedFiles = 0;
            var eligible = Enumerable.Range(0, items.Length).Where(i => entries[i].Status == AudioExportStatus.Pending && items[i] is not null);
            await Parallel.ForEachAsync(eligible, new ParallelOptions { MaxDegreeOfParallelism = workerCount }, async (i, _) =>
            {
                var item = items[i];
                if (entries[i].Status != AudioExportStatus.Pending || item is null) return;
                var work = item.Value;
                if (indexFailure is { } failure)
                {
                    entries[i] = entries[i] with { Status = AudioExportStatus.Failed, ErrorCode = failure.Code, Diagnostic = failure.Message };
                    return;
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    entries[i] = entries[i] with { Status = AudioExportStatus.Cancelled, ErrorCode = "Cancelled", Diagnostic = "Cancelled before export." };
                    return;
                }
                string? temporary = null;
                var committed = false;
                SafeFileHandle? volumeHandle = null;
                try
                {
                    SourceIdentity.EnsureUnchanged(revisions, index!, snapshot.IndexPath, snapshot.IndexStamp);
                    await VerifyRecordAsync(index!, snapshot, work.Binding.PayloadOwner, cancellationToken).ConfigureAwait(false);
                    volumeHandle = SourceIdentity.OpenPinned(work.Binding.Volume.FilePath);
                    SourceIdentity.EnsureUnchanged(revisions, volumeHandle, work.Binding.Volume.FilePath, work.Binding.Volume.Stamp);
                    await metadataVerifier.VerifyMediaAsync(work.Source.Media,cancellationToken).ConfigureAwait(false);
                    var destination = OutputSafety.ResolveOutputPath(root, work.Relative);
                    if (File.Exists(destination) && !options.Overwrite)
                    {
                        entries[i] = entries[i] with { Status = AudioExportStatus.Skipped, ErrorCode = "ExistingDestination", Diagnostic = "Existing output was left unchanged." };
                        return;
                    }
                    if (File.Exists(destination))
                    {
                        var existing = revisions.Capture(destination);
                        if (!string.IsNullOrEmpty(existing.FileIdentity) &&
                            (existing.FileIdentity == work.Binding.Volume.Stamp.FileIdentity || existing.FileIdentity == snapshot.IndexStamp.FileIdentity))
                            throw new ArchiveException("UnsafeOutput", "An existing output aliases an archive source file.");
                    }
                    var parent = Path.GetDirectoryName(destination)!;
                    Directory.CreateDirectory(parent);
                    using var parentPin = OutputSafety.PinDirectory(parent);
                    OutputSafety.ValidateFilePath(destination, snapshot.SourceRoot);
                    temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    AudioOperation? operation = null;
                    int? sampleRate = null, channels = null;
                    var sourceHash = await HashSliceAsync(volumeHandle, entries[i].SourceOffset, entries[i].SourceLength, cancellationToken).ConfigureAwait(false);
                    if (options.Format == AudioExportFormat.Original)
                        await CopySliceAsync(volumeHandle, entries[i].SourceOffset, entries[i].SourceLength, temporary, progress, i, items.Length, cancellationToken).ConfigureAwait(false);
                    else if (options.Format == AudioExportFormat.Wav)
                    {
                        var converted = await audioService.DecodeToWavAsync(audioCatalog,
                            work.Source.Media, temporary, options.WavFormat, progress, cancellationToken).ConfigureAwait(false);
                        operation = AudioOperation.Decode; sampleRate = converted.SampleRate; channels = converted.Channels;
                    }
                    else
                    {
                        var converted = await audioService.ConvertToOggAsync(audioCatalog, work.Source.Media, temporary, progress, cancellationToken).ConfigureAwait(false);
                        operation = converted.Operation; sampleRate = converted.SampleRate; channels = converted.Channels;
                    }
                    SourceIdentity.EnsureUnchanged(revisions, index!, snapshot.IndexPath, snapshot.IndexStamp);
                    SourceIdentity.EnsureUnchanged(revisions, volumeHandle, work.Binding.Volume.FilePath, work.Binding.Volume.Stamp);
                    var sourceHashAfter = await HashSliceAsync(volumeHandle, entries[i].SourceOffset, entries[i].SourceLength, cancellationToken).ConfigureAwait(false);
                    if (sourceHash != sourceHashAfter) throw new ArchiveException("SourceChanged", "Audio source bytes changed during export.");
                    await metadataVerifier.VerifyMediaAsync(work.Source.Media,cancellationToken).ConfigureAwait(false);
                    OutputSafety.ValidateFilePath(destination, snapshot.SourceRoot);
                    cancellationToken.ThrowIfCancellationRequested();
                    var info = new FileInfo(temporary);
                    var outputLength = info.Length;
                    var hash = await HashFileAsync(temporary, cancellationToken).ConfigureAwait(false);
                    File.Move(temporary, destination, options.Overwrite);
                    committed = true;
                    entries[i] = entries[i] with { Status = AudioExportStatus.Completed, SourceSha256 = sourceHash, Operation = operation,
                        OutputSampleRate = sampleRate, OutputChannels = channels,
                        OutputPrecision = options.Format switch { AudioExportFormat.Original => "Source", AudioExportFormat.Wav => options.WavFormat.ToString(), _ => "Lossy Vorbis" },
                        SampleRateChanged = sampleRate is null ? null : work.Source.Media.SampleRate > 0 && sampleRate != work.Source.Media.SampleRate,
                        ChannelsChanged = channels is null ? null : work.Source.Media.Channels > 0 && channels != work.Source.Media.Channels,
                        OutputLength = outputLength, Sha256 = hash, DecoderVersion = ToolFingerprint };
                    try { progress?.Report(new AudioProgress("Exporting audio", Interlocked.Increment(ref completedFiles), items.Length, work.Source.Media.DisplayLabel)); }
                    catch { /* Progress observers must not change a committed output's result. */ }
                }
                catch (OperationCanceledException)
                {
                    entries[i] = entries[i] with { Status = AudioExportStatus.Cancelled, ErrorCode = "Cancelled", Diagnostic = "Cancelled before output commit." };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or TimeoutException or Win32Exception)
                {
                    var archive = ex as ArchiveException;
                    entries[i] = entries[i] with { Status = AudioExportStatus.Failed,
                        ErrorCode = archive?.Code ?? ex.GetType().Name, Diagnostic = SanitizeDiagnostic(ex.Message, snapshot, root) };
                }
                finally
                {
                    volumeHandle?.Dispose();
                    if (!committed && temporary is not null)
                    {
                        try { File.Delete(temporary); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            entries[i] = entries[i] with { Status = AudioExportStatus.Failed, ErrorCode = "TemporaryCleanupFailed",
                                Diagnostic = "The incomplete temporary audio output could not be removed." };
                        }
                    }
                }
            }).ConfigureAwait(false);
            for (var i = 0; i < entries.Length; i++)
                if (entries[i].Status == AudioExportStatus.Pending)
                    entries[i] = entries[i] with { Status = AudioExportStatus.Cancelled, ErrorCode = "Cancelled", Diagnostic = "Cancelled before export." };
        }
        finally { index?.Dispose(); }

        var manifest = new AudioExportManifest(2, Path.GetFileName(snapshot.IndexPath), snapshot.IndexContentId,
            snapshot.PhysicalSourceStamp, "FreshIndexSha256+PinnedSourceStamps+SourceAndOutputSliceSha256", ToolFingerprint, started,
            DateTimeOffset.UtcNow, cancellationToken.IsCancellationRequested,
            new AudioExportOptionsSummary(options.Format, options.WavFormat, options.Overwrite, options.AllowPartialOriginal, options.ConversionWorkers), entries);
        var manifestPath = Path.Combine(root, $"audio-export-{started:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        var manifestTemp = manifestPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(manifestTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(manifestTemp, manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new AudioManifestWriteException(manifest, manifestPath, ex); }
        finally
        {
            try { if (File.Exists(manifestTemp)) File.Delete(manifestTemp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new(manifest, manifestPath);
    }

    public async Task<AudioExportReport> ExportBankAsync(ArchiveSnapshot snapshot, BankInfo bank, string outputDirectory,
        bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(bank);
        var root = Path.GetFullPath(outputDirectory);
        OutputSafety.ValidateOutputRoot(root, snapshot.SourceRoot);
        Directory.CreateDirectory(root);
        using var rootPin = OutputSafety.PinDirectory(root);
        var started = DateTimeOffset.UtcNow;
        var identity = bank.ContainerIdentity;
        var relativeVolume = "unknown-volume";
        var relative = $"audio/banks/bank_{bank.Id:X8}_{SourceIdentity.HashFields("deviant-audio-bank-name-v1", [bank.Key])[..12]}.bnk";
        AudioExportEntry entry;
        using var bankMetadataVerifier=new AudioMetadataVerifier(snapshot,revisions).CreateLease();
        SafeFileHandle? index = null;
        SafeFileHandle? volumeHandle = null;
        string? temporary = null;
        var committed = false;
        try
        {
            if (identity is null || identity.MemberPath != "bank" || identity.IndexContentId != snapshot.IndexContentId ||
                identity.Ordinal < 0 || identity.Ordinal >= snapshot.Records.Count || bank.Identity is null ||
                bank.Identity.IndexContentId != snapshot.IndexContentId || bank.Identity.Ordinal < 0 || bank.Identity.Ordinal >= snapshot.Records.Count)
                throw new ArchiveException("InvalidBankIdentity", "The selected bank does not have stable descriptor and container identities.");
            var owner = snapshot.Records[(int)identity.Ordinal];
            var descriptor = snapshot.Records[(int)bank.Identity.Ordinal];
            if (owner.Type != 29 || descriptor.Type != 1022)
                throw new ArchiveException("InvalidBankIdentity", "The bank payload and descriptor identities do not resolve to their declared resource types.");
            var volume = snapshot.Volumes.SingleOrDefault(candidate => candidate.Package == owner.Package)
                ?? throw new ArchiveException("SourceUnavailable", "The bank source volume is not part of the archive snapshot.");
            relativeVolume = volume.RelativeName;
            if (!PathEquals(bank.Slice.FilePath, volume.FilePath) || bank.Slice.Length <= 0 ||
                !IndexLayout.InBounds(bank.Slice.Offset, bank.Slice.Length, volume.Stamp.Length) ||
                bank.Slice.Offset < owner.Offset || bank.Slice.Offset > owner.Offset + owner.StoredSize ||
                bank.Slice.Length > owner.Offset + owner.StoredSize - bank.Slice.Offset)
                throw new ArchiveException("InvalidBankRange", "The bank extent does not fit its type-29 container row.");
            _ = OutputSafety.ResolveOutputPath(root, relative);
            index = SourceIdentity.OpenPinned(snapshot.IndexPath);
            SourceIdentity.EnsureUnchanged(revisions, index, snapshot.IndexPath, snapshot.IndexStamp);
            if (await SourceIdentity.HashAsync(index, snapshot.IndexStamp.Length, cancellationToken).ConfigureAwait(false) != snapshot.IndexContentId)
                throw new ArchiveException("SourceChanged", "The archive index contents differ from the selected revision.");
            await VerifyRecordAsync(index, snapshot, owner, cancellationToken).ConfigureAwait(false);
            await VerifyRecordAsync(index, snapshot, descriptor, cancellationToken).ConfigureAwait(false);
            volumeHandle = SourceIdentity.OpenPinned(volume.FilePath);
            SourceIdentity.EnsureUnchanged(revisions, volumeHandle, volume.FilePath, volume.Stamp);
            await bankMetadataVerifier.VerifyBankAsync(bank,cancellationToken).ConfigureAwait(false);
            var destination = OutputSafety.ResolveOutputPath(root, relative);
            if (File.Exists(destination) && !overwrite)
            {
                entry = CreateBankEntry(identity, bank, relativeVolume, bank.Slice, relative, AudioExportStatus.Skipped, "ExistingDestination", "Existing output was left unchanged.");
            }
            else
            {
                if (File.Exists(destination))
                {
                    var existing = revisions.Capture(destination);
                    if (!string.IsNullOrEmpty(existing.FileIdentity) &&
                        (existing.FileIdentity == volume.Stamp.FileIdentity || existing.FileIdentity == snapshot.IndexStamp.FileIdentity))
                        throw new ArchiveException("UnsafeOutput", "An existing output aliases an archive source file.");
                }
                var parent = Path.GetDirectoryName(destination)!; Directory.CreateDirectory(parent);
                using var parentPin = OutputSafety.PinDirectory(parent);
                OutputSafety.ValidateFilePath(destination, snapshot.SourceRoot);
                temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var sourceHash = await HashSliceAsync(volumeHandle, bank.Slice.Offset, bank.Slice.Length, cancellationToken).ConfigureAwait(false);
                await CopySliceAsync(volumeHandle, bank.Slice.Offset, bank.Slice.Length, temporary, null, 0, 1, cancellationToken).ConfigureAwait(false);
                SourceIdentity.EnsureUnchanged(revisions, index, snapshot.IndexPath, snapshot.IndexStamp);
                SourceIdentity.EnsureUnchanged(revisions, volumeHandle, volume.FilePath, volume.Stamp);
                if (sourceHash != await HashSliceAsync(volumeHandle, bank.Slice.Offset, bank.Slice.Length, cancellationToken).ConfigureAwait(false))
                    throw new ArchiveException("SourceChanged", "The bank source bytes changed during export.");
                await bankMetadataVerifier.VerifyBankAsync(bank,cancellationToken).ConfigureAwait(false);
                var outputLength = new FileInfo(temporary).Length;
                var hash = await HashFileAsync(temporary, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite); committed = true;
                entry = CreateBankEntry(identity, bank, relativeVolume, bank.Slice, relative, AudioExportStatus.Completed, null, null) with
                { SourceSha256 = sourceHash, OutputLength = outputLength, Sha256 = hash, DecoderVersion = ToolFingerprint };
            }
        }
        catch (OperationCanceledException)
        {
            entry = CreateBankEntry(identity, bank, relativeVolume, bank.Slice, relative, AudioExportStatus.Cancelled, "Cancelled", "Cancelled before output commit.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            entry = CreateBankEntry(identity, bank, relativeVolume, bank.Slice, relative,
                AudioExportStatus.Failed,
                (ex as ArchiveException)?.Code ?? ex.GetType().Name, SanitizeDiagnostic(ex.Message, snapshot, root));
        }
        finally
        {
            index?.Dispose(); volumeHandle?.Dispose();
            if (!committed && temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        var manifest = new AudioExportManifest(2, Path.GetFileName(snapshot.IndexPath), snapshot.IndexContentId,
            snapshot.PhysicalSourceStamp, "FreshIndexSha256+PinnedSourceStamps+SourceAndOutputSliceSha256", ToolFingerprint,
            started, DateTimeOffset.UtcNow, cancellationToken.IsCancellationRequested,
            new AudioExportOptionsSummary(AudioExportFormat.Original, WavFormat.Pcm16, overwrite, false, 1), [entry]);
        return await WriteManifestAsync(root, manifest).ConfigureAwait(false);
    }

    private AudioExportEntry CreateBankEntry(ResourceIdentity? containerIdentity, BankInfo bank, string volume,
        DataSlice slice, string relative, AudioExportStatus status, string? errorCode, string? diagnostic) => new(
        containerIdentity?.Key ?? bank.Identity?.Key ?? "unknown", containerIdentity?.Ordinal ?? bank.Identity?.Ordinal ?? -1,
        containerIdentity?.MemberPath, bank.Id, volume, slice.Offset, slice.Length, null,
        relative, MediaCompleteness.Complete, SourceAvailability.Available,
        "Wwise bank", null, null, AudioExportFormat.Original, null, null, null, "Source", false, false,
        null, null, ToolFingerprint, status, errorCode, diagnostic, bank.Identity?.Key, bank.Id,
        containerIdentity?.Key, containerIdentity?.Ordinal, containerIdentity?.MemberPath);

    private static async Task<AudioExportReport> WriteManifestAsync(string root, AudioExportManifest manifest)
    {
        var path = Path.Combine(root, $"audio-export-{manifest.StartedAt:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        var temporary = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
            return new(manifest, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new AudioManifestWriteException(manifest, path, ex); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private AudioExportEntry CreateEntry(AudioExportSource source, string volume, string relative,
        AudioExportOptions options, AudioExportStatus status)
    {
        var extent = options.Format == AudioExportFormat.Original
            ? source.Media.OriginalSlice ?? source.Media.DidxExtent ?? source.Media.Slice : source.Media.Slice;
        return new(source.Identity.Key, source.Identity.Ordinal, source.Identity.MemberPath, source.Media.Id, volume,
            extent.Offset, extent.Length, null, relative, source.Media.Completeness, source.Media.Availability, source.Media.Codec,
            source.Media.SampleRate > 0 ? source.Media.SampleRate : null, source.Media.Channels > 0 ? source.Media.Channels : null,
            options.Format, null, null, null, null, null, null, null, null, ToolFingerprint, status);
    }

    private AudioExportEntry CreateEntry(AudioExportSource source, AudioPayloadBinding binding, string relative,
        AudioExportOptions options, AudioExportStatus status)
    {
        var extent = binding.Extent;
        return new(source.Identity.Key, source.Identity.Ordinal, source.Identity.MemberPath, source.Media.Id,
            binding.Volume.RelativeName, extent.Offset, extent.Length, null, relative,
            source.Media.Completeness, source.Media.Availability, source.Media.Codec,
            source.Media.SampleRate > 0 ? source.Media.SampleRate : null, source.Media.Channels > 0 ? source.Media.Channels : null,
            options.Format, null, null, null, null, null, null, null, null, ToolFingerprint, status,
            PayloadIdentity: binding.PayloadIdentity.Key, PayloadOrdinal: binding.PayloadIdentity.Ordinal,
            PayloadMemberPath: binding.PayloadIdentity.MemberPath);
    }

    private static AudioExportEntry FailOrSkip(AudioExportEntry entry, string code, string? reason, AudioExportStatus status) =>
        entry with { Status = status, ErrorCode = code, Diagnostic = reason };

    private static AudioCatalog BuildCatalog(AudioExportRequest review, ArchiveSnapshot snapshot)
    {
        var catalog = new AudioCatalog { SourcePath = snapshot.IndexPath, Fingerprint = snapshot.IndexContentId,
            IndexContentId = snapshot.IndexContentId, PhysicalSourceStamp = snapshot.PhysicalSourceStamp,
            IndexStamp = snapshot.IndexStamp, SourceVolumes = snapshot.Volumes.ToList(), IndexedResourceCount = snapshot.Records.Count };
        catalog.Media.AddRange(review.Sources.Select(row => row.Media));
        return catalog;
    }

    private static async Task<string> HashSliceAsync(SafeFileHandle source, long offset, long length, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long readTotal = 0;
        while (readTotal < length)
        {
            token.ThrowIfCancellationRequested();
            var read = await RandomAccess.ReadAsync(source, buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - readTotal)),
                checked(offset + readTotal), token).ConfigureAwait(false);
            if (read == 0) throw new ArchiveException("SourceChanged", "The source ended while hashing an audio extent.");
            hash.AppendData(buffer.AsSpan(0, read)); readTotal += read;
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task CopySliceAsync(SafeFileHandle source, long offset, long length, string destination,
        IProgress<AudioProgress>? progress, int index, int total, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long copied = 0;
        while (copied < length)
        {
            token.ThrowIfCancellationRequested();
            var read = await RandomAccess.ReadAsync(source, buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied)), checked(offset + copied), token).ConfigureAwait(false);
            if (read == 0) throw new ArchiveException("SourceChanged", "The source ended during audio extraction.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            hash.AppendData(buffer.AsSpan(0, read)); copied += read;
            progress?.Report(new AudioProgress("Exporting audio", (int)Math.Min(int.MaxValue, copied), (int)Math.Min(int.MaxValue, length), $"{index + 1}/{total}"));
        }
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static async Task VerifyRecordAsync(SafeFileHandle index, ArchiveSnapshot snapshot, Deviant.Core.ResourceRecord expected, CancellationToken token)
    {
        var offset = checked(IndexLayout.HeaderLength + expected.Ordinal * IndexLayout.RecordLength);
        var bytes = new byte[IndexLayout.RecordLength];
        var read = await RandomAccess.ReadAsync(index, bytes, offset, token).ConfigureAwait(false);
        if (read != bytes.Length || !IndexLayout.SameStoredRecord(IndexLayout.ReadRecord(bytes, expected.Ordinal, Path.GetFileNameWithoutExtension(snapshot.IndexPath)), expected))
            throw new ArchiveException("SourceChanged", "The selected audio index row no longer matches its immutable resource identity.");
    }

    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private string SanitizeDiagnostic(string? message, ArchiveSnapshot snapshot, string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Operation failed.";
        var paths = new List<(string Path, string Label)>
        {
            (snapshot.IndexPath, "<source-index>"), (snapshot.SourceRoot, "<source>"), (outputRoot, "<output>"),
            (Path.GetTempPath(), "<temp>"),
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "<user-data>"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "<user-data>")
        };
        paths.AddRange(snapshot.Volumes.Select(volume => (volume.FilePath, $"<source-volume:{volume.RelativeName}>")));
        paths.AddRange(privateRoots.Select(path => (path, "<tool-data>")));
        foreach (var (path, label) in paths.Where(item => !string.IsNullOrWhiteSpace(item.Path))
                     .OrderByDescending(item => item.Path.Length))
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            message = message.Replace(full, label, StringComparison.OrdinalIgnoreCase)
                .Replace(full.Replace('\\', '/'), label, StringComparison.OrdinalIgnoreCase)
                .Replace(full.Replace('/', '\\'), label, StringComparison.OrdinalIgnoreCase);
        }
        return message;
    }
}
