using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deviant.Core;
using Microsoft.Win32.SafeHandles;

namespace Deviant.Export;

public sealed class ManifestWriteException(ExportManifest manifest, string path, Exception inner)
    : IOException("ManifestWriteFailed: outputs were retained; the complete report is available on this exception.", inner)
{
    public ExportManifest Manifest { get; } = manifest;
    public string ManifestPath { get; } = path;
}

public sealed class RawExportService(ISourceRevisionProvider? revisionProvider = null, IExportManifestWriter? manifestWriter = null) : IExportService
{
    private readonly ISourceRevisionProvider provider = revisionProvider ?? new FileRevisionProvider();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public ExportPlan Plan(ArchiveSnapshot snapshot, ExportOptions options, IEnumerable<ResourceRecord> selected)
        => Plan(snapshot, options, selected, CancellationToken.None);

    public ExportPlan Plan(ArchiveSnapshot snapshot, ExportOptions options, IEnumerable<ResourceRecord> selected,
        CancellationToken cancellationToken, IProgress<ArchiveProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = Path.GetFullPath(options.OutputDirectory); OutputSafety.ValidateOutputRoot(output, snapshot.SourceRoot);
        var items = new List<ExportItem>(); var seen = new HashSet<long>();
        var total = selected.TryGetNonEnumeratedCount(out var count) ? count : 0;
        foreach (var supplied in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(supplied.Ordinal)) continue;
            if (supplied.Ordinal < 0 || supplied.Ordinal >= snapshot.Records.Count ||
                !IndexLayout.SameStoredRecord(supplied, snapshot.Records[(int)supplied.Ordinal]))
                throw new ArchiveException("InvalidSelection", "The selected record does not belong to this index revision.");
            var r = snapshot.Records[(int)supplied.Ordinal];
            var relative = $"p{r.Package:D2}/t{r.Type:X8}/id_{r.Id:X8}_r{r.Ordinal:D8}.bin";
            OutputSafety.ResolveOutputPath(output, relative); items.Add(new(r, relative));
            if ((items.Count & 127) == 0) progress?.Report(new("Preparing export", items.Count, total));
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new("Preparing export", items.Count, total));
        if (items.Count == 0) throw new ArchiveException("EmptySelection", "No resources matched the selection.");
        return new(snapshot, options with { OutputDirectory = output }, items.AsReadOnly());
    }

    public async Task<ExportReport> RunAsync(ExportPlan plan, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var snapshot = plan.Snapshot; var root = plan.Options.OutputDirectory;
        OutputSafety.ValidateOutputRoot(root, snapshot.SourceRoot); Directory.CreateDirectory(root);
        using var rootPin = OutputSafety.PinDirectory(root);
        var started = DateTimeOffset.UtcNow;
        var entries = plan.Items.Select(item => Entry(item, ExportStatus.Pending)).ToArray();
        var blockedPackages = new Dictionary<uint, (string Code, string Message)>();
        SafeFileHandle? index = null; (string Code, string Message)? indexFailure = null;
        var completed = 0;
        try
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    index = SourceIdentity.OpenPinned(snapshot.IndexPath);
                    SourceIdentity.EnsureUnchanged(provider, index, snapshot.IndexPath, snapshot.IndexStamp);
                    if (await SourceIdentity.HashAsync(index, snapshot.IndexStamp.Length, cancellationToken) != snapshot.IndexContentId)
                        throw new ArchiveException("SourceChanged", "The index contents differ from the selected revision.");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { indexFailure = Error(ex); }
            }
            for (var i = 0; i < plan.Items.Count; i++)
            {
                var item = plan.Items[i]; var r = item.Resource;
                if (indexFailure is { } global) { entries[i] = Entry(item, ExportStatus.Failed, global.Code, global.Message); continue; }
                if (blockedPackages.TryGetValue(r.Package, out var blocked)) { entries[i] = Entry(item, ExportStatus.Failed, blocked.Code, blocked.Message); continue; }
                if (cancellationToken.IsCancellationRequested) { entries[i] = Entry(item, ExportStatus.Cancelled, "Cancelled", "Cancelled before completion."); continue; }
                SafeFileHandle? volumeHandle = null; string? temporary = null; var committed = false; var temporaryOwned = false;
                var volume = snapshot.Volumes.Single(v => v.Package == r.Package);
                try
                {
                    SourceIdentity.EnsureUnchanged(provider, index!, snapshot.IndexPath, snapshot.IndexStamp);
                    await VerifyRecordAsync(index!, snapshot, r, cancellationToken);
                    volumeHandle = SourceIdentity.OpenPinned(volume.FilePath);
                    SourceIdentity.EnsureUnchanged(provider, volumeHandle, volume.FilePath, volume.Stamp);
                    if (!IndexLayout.InBounds(r.Offset, r.StoredSize, RandomAccess.GetLength(volumeHandle)))
                        throw new ArchiveException("InvalidRange", "The selected range is outside the held source volume.");
                    var destination = OutputSafety.ResolveOutputPath(root, item.RelativeOutput);
                    if (File.Exists(destination) && !plan.Options.Overwrite) { entries[i] = Entry(item, ExportStatus.Skipped, "ExistingDestination", "Existing output was left unchanged."); continue; }
                    var parent = Path.GetDirectoryName(destination)!; Directory.CreateDirectory(parent);
                    using var parentPin = OutputSafety.PinDirectory(parent);
                    OutputSafety.ValidateFilePath(destination, snapshot.SourceRoot);
                    if (File.Exists(destination))
                    {
                        var destinationStamp = provider.Capture(destination);
                        if (destinationStamp.FileIdentity.Length > 0 && (destinationStamp.FileIdentity == volume.Stamp.FileIdentity || destinationStamp.FileIdentity == snapshot.IndexStamp.FileIdentity))
                            throw new ArchiveException("UnsafeOutput", "An existing output aliases a source file.");
                    }
                    temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
                    {
                        temporaryOwned = true;
                        var buffer = new byte[128 * 1024]; long written = 0;
                        while (written < r.StoredSize)
                        {
                            var read = await RandomAccess.ReadAsync(volumeHandle, buffer.AsMemory(0, (int)Math.Min(buffer.Length, r.StoredSize - written)), checked(r.Offset + written), cancellationToken);
                            if (read == 0) throw new ArchiveException("SourceChanged", "The source ended during raw extraction.");
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            digest.AppendData(buffer.AsSpan(0, read)); written += read;
                            progress?.Report(new(r.Ordinal, written, r.StoredSize, completed, plan.Items.Count));
                        }
                        await output.FlushAsync(cancellationToken); output.Flush(flushToDisk: true);
                    }
                    SourceIdentity.EnsureUnchanged(provider, index!, snapshot.IndexPath, snapshot.IndexStamp);
                    SourceIdentity.EnsureUnchanged(provider, volumeHandle, volume.FilePath, volume.Stamp);
                    OutputSafety.ValidateFilePath(destination, snapshot.SourceRoot);
                    cancellationToken.ThrowIfCancellationRequested();
                    var hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
                    File.Move(temporary, destination, plan.Options.Overwrite); committed = true;
                    entries[i] = Entry(item, ExportStatus.Completed) with { CommittedLength = r.StoredSize, Sha256 = hash };
                    completed++; progress?.Report(new(r.Ordinal, r.StoredSize, r.StoredSize, completed, plan.Items.Count));
                }
                catch (OperationCanceledException)
                {
                    if (!committed)
                    {
                        try
                        {
                            if (index is not null) SourceIdentity.EnsureUnchanged(provider, index, snapshot.IndexPath, snapshot.IndexStamp);
                            if (volumeHandle is not null) SourceIdentity.EnsureUnchanged(provider, volumeHandle, volume.FilePath, volume.Stamp);
                            entries[i] = Entry(item, ExportStatus.Cancelled, "Cancelled", "Cancelled before completion.");
                        }
                        catch (ArchiveException ex) { entries[i] = Entry(item, ExportStatus.Failed, ex.Code, ex.Message); SetSourceFailure(ex); }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    if (!committed) { var error = Error(ex); entries[i] = Entry(item, ExportStatus.Failed, error.Code, error.Message); SetSourceFailure(ex); }
                }
                finally
                {
                    volumeHandle?.Dispose();
                    if (temporaryOwned && temporary is not null)
                    {
                        try { File.Delete(temporary); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            if (!committed) entries[i] = Entry(item, ExportStatus.Failed, "TemporaryCleanupFailed", "The incomplete owned temporary file could not be removed.");
                        }
                    }
                }
                void SetSourceFailure(Exception ex)
                {
                    if (ex is not ArchiveException { Code: "SourceChanged" or "SourceBusy" or "SourceUnavailable" }) return;
                    try { if (index is not null) SourceIdentity.EnsureUnchanged(provider, index, snapshot.IndexPath, snapshot.IndexStamp); }
                    catch (ArchiveException changed) { indexFailure = (changed.Code, changed.Message); return; }
                    blockedPackages[r.Package] = Error(ex);
                }
            }
        }
        finally { index?.Dispose(); }
        var manifest = new ExportManifest(1, Path.GetFileName(snapshot.IndexPath), snapshot.IndexContentId, snapshot.PhysicalSourceStamp,
            "FreshIndexSha256+PinnedSourceStamps+ExportedSliceSha256", started, DateTimeOffset.UtcNow,
            cancellationToken.IsCancellationRequested, entries);
        var manifestPath = Path.Combine(root, $"export-{started:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        if (manifestWriter is not null)
        {
            try { await manifestWriter.WriteAsync(manifestPath, manifest); return new(manifest, manifestPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ManifestWriteException(manifest, manifestPath, ex); }
        }
        var manifestTemp = manifestPath + ".tmp";
        var manifestOwned = false;
        try
        {
            await using (var stream = new FileStream(manifestTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                manifestOwned = true;
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, CancellationToken.None);
                await stream.FlushAsync(CancellationToken.None); stream.Flush(flushToDisk: true);
            }
            File.Move(manifestTemp, manifestPath); return new(manifest, manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ManifestWriteException(manifest, manifestPath, ex); }
        finally
        {
            if (manifestOwned)
            {
                try { File.Delete(manifestTemp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Original manifest error and retained output report take precedence. */ }
            }
        }
    }
    private static ExportEntry Entry(ExportItem item, ExportStatus status, string? code = null, string? error = null)
    {
        var r = item.Resource; return new(r.Ordinal, r.Type, r.Id, r.Package, r.VolumeName, r.Offset, r.StoredSize,
            item.RelativeOutput, status, ErrorCode: code, Error: error);
    }
    private static (string Code, string Message) Error(Exception ex) =>
        ex is ArchiveException a ? (a.Code, a.Message) : ("ExportFailed", ex.Message);
    private static async Task VerifyRecordAsync(SafeFileHandle index, ArchiveSnapshot snapshot, ResourceRecord selected, CancellationToken token)
    {
        var row = new byte[IndexLayout.RecordLength]; var read = 0;
        while (read < row.Length)
        {
            var n = await RandomAccess.ReadAsync(index, row.AsMemory(read), checked(IndexLayout.HeaderLength + selected.Ordinal * IndexLayout.RecordLength + read), token);
            if (n == 0) throw new ArchiveException("SourceChanged", "The selected index record is truncated.");
            read += n;
        }
        var actual = IndexLayout.ReadRecord(row, selected.Ordinal, Path.GetFileNameWithoutExtension(snapshot.IndexPath));
        if (!IndexLayout.SameStoredRecord(actual, selected)) throw new ArchiveException("SourceChanged", "The selected index record differs from the held index.");
    }
}
