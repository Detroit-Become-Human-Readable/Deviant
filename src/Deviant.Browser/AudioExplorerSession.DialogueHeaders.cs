using System.Security.Cryptography;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;

namespace Deviant.Browser;

public sealed partial class AudioExplorerSession
{
    private const string DialogueHeaderCacheVersion = "audio-dialogue-header-associations-v1";
    private const int MaximumDialogueHeaderCacheBytes = 128 * 1024 * 1024;
    private const int MaximumDialogueCandidates = 200_000;
    private const int MaximumDialogueDigests = 200_000;

    private sealed record DialogueHeaderEnvelope(string IndexContentId, string PhysicalSourceStamp,
        FileStamp IndexStamp, IReadOnlyList<VolumeSource> Volumes, int SchemaVersion, bool Complete,
        int Count, string RowsSha256, IReadOnlyList<DialogueHeaderAssociation> Rows);

    public bool AreDialogueHeadersReady => Catalog.DialogueHeaderVersion == DetroitAudioSnapshotReader.DialogueHeaderAssociationVersion;

    private static string DialogueHeaderKey(string featureKey) => featureKey + "/dialogue-headers";

    private static async Task LoadDialogueHeadersAsync(FeatureStore store, string featureKey,
        ArchiveSnapshot snapshot, AudioCatalog catalog, CancellationToken token)
    {
        await Task.Run(() =>
        {
            ValidateDialogueSnapshot(snapshot, token);
            DialogueHeaderEnvelope? envelope;
            try { envelope = store.LoadBounded<DialogueHeaderEnvelope>(DialogueHeaderKey(featureKey),
                DialogueHeaderCacheVersion, MaximumDialogueHeaderCacheBytes, token); }
            catch (Exception error) when (error is InvalidDataException or JsonException) { return; }
            if (envelope is null || !envelope.Complete || envelope.IndexStamp is null || envelope.Volumes is null ||
                envelope.Volumes.Any(volume => volume is null || volume.Stamp is null) ||
                envelope.SchemaVersion != DetroitAudioSnapshotReader.DialogueHeaderAssociationVersion ||
                envelope.IndexContentId != snapshot.IndexContentId || envelope.PhysicalSourceStamp != snapshot.PhysicalSourceStamp ||
                !SourceIdentity.SameStamp(envelope.IndexStamp, snapshot.IndexStamp) ||
                !MetadataCacheSourcePolicy.SameVolumes(envelope.Volumes, snapshot.Volumes)) return;
            try
            {
                ValidateDialogueRows(snapshot, envelope.Rows, token);
                if (envelope.Count != envelope.Rows.Count || envelope.RowsSha256 != DialogueRowsHash(envelope.Rows)) return;
            }
            catch (InvalidDataException) { return; }
            ValidateDialogueSnapshot(snapshot, token);
            catalog.DialogueHeaderAssociations = envelope.Rows.ToList();
            catalog.DialogueHeaderVersion = envelope.SchemaVersion;
        }, token).ConfigureAwait(false);
    }

    private static async Task SaveDialogueHeadersAsync(FeatureStore store, string featureKey,
        ArchiveSnapshot snapshot, AudioCatalog catalog, CancellationToken token)
    {
        await Task.Run(() =>
        {
            ValidateDialogueSnapshot(snapshot, token);
            ValidateDialogueRows(snapshot, catalog.DialogueHeaderAssociations, token);
            var rows = catalog.DialogueHeaderAssociations.ToArray();
            var envelope = new DialogueHeaderEnvelope(snapshot.IndexContentId, snapshot.PhysicalSourceStamp,
                snapshot.IndexStamp, snapshot.Volumes, DetroitAudioSnapshotReader.DialogueHeaderAssociationVersion,
                true, rows.Length, DialogueRowsHash(rows), rows);
            token.ThrowIfCancellationRequested();
            store.SaveBounded(DialogueHeaderKey(featureKey), DialogueHeaderCacheVersion, envelope,
                MaximumDialogueHeaderCacheBytes, token);
            ValidateDialogueSnapshot(snapshot, token);
        }, token).ConfigureAwait(false);
    }

    private async Task EnrichDialogueHeadersAsync(string featureKey,
        IProgress<AudioExplorerProgress>? progress, CancellationToken token)
    {
        try
        {
            ReportEnrichmentProgress("Updating dialogue links", 0, Snapshot.Records.Count(row => row.Type == 4091), progress);
            var rows = await new DetroitAudioSnapshotReader().ReadDialogueHeaderAssociationsAsync(Snapshot, token).ConfigureAwait(false);
            var candidate = new AudioCatalog { DialogueHeaderAssociations = rows.ToList(),
                DialogueHeaderVersion = DetroitAudioSnapshotReader.DialogueHeaderAssociationVersion };
            await SaveDialogueHeadersAsync(featureStore, featureKey, Snapshot, candidate, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (catalogGate)
            {
                Catalog.DialogueHeaderAssociations = candidate.DialogueHeaderAssociations;
                Catalog.DialogueHeaderVersion = candidate.DialogueHeaderVersion;
            }
            Interlocked.Exchange(ref enrichmentComplete, 1);
            Interlocked.Exchange(ref enrichmentProgressCompleted, Catalog.Media.Count);
            Interlocked.Exchange(ref enrichmentProgressTotal, Catalog.Media.Count);
            Volatile.Write(ref currentProgress, new("Catalogue ready", Catalog.Media.Count, Catalog.Media.Count));
            PublishUpdate("Dialogue links ready", null);
            progress?.Report(CurrentProgress);
        }
        catch (Exception)
        {
            var cancelled = token.IsCancellationRequested;
            Volatile.Write(ref currentProgress, new(cancelled ? "Cancelled" : "Dialogue links unavailable", 0, 0));
            PublishUpdate(CurrentProgress.Phase, null);
        }
    }

    private static void ValidateDialogueSnapshot(ArchiveSnapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var provider = new FileRevisionProvider();
        using var index = SourceIdentity.OpenPinned(snapshot.IndexPath);
        SourceIdentity.EnsureUnchanged(provider, index, snapshot.IndexPath, snapshot.IndexStamp);
        foreach (var volume in snapshot.Volumes)
        {
            token.ThrowIfCancellationRequested();
            if (!volume.Stamp.Exists) continue;
            using var handle = SourceIdentity.OpenPinned(volume.FilePath);
            SourceIdentity.EnsureUnchanged(provider, handle, volume.FilePath, volume.Stamp);
        }
    }

    private static string DialogueRowsHash(IReadOnlyList<DialogueHeaderAssociation> rows)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new DialogueHashStream(hash);
        JsonSerializer.Serialize(stream, rows);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed class DialogueHashStream(IncrementalHash hash) : Stream
    {
        private long written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > MaximumDialogueHeaderCacheBytes - written)
                throw new InvalidDataException("Dialogue header serialization exceeds its limit.");
            hash.AppendData(buffer); written += buffer.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static void ValidateDialogueRows(ArchiveSnapshot snapshot,
        IReadOnlyList<DialogueHeaderAssociation>? rows, CancellationToken token)
    {
        var expected = snapshot.Records.Where(row => row.Type == 4091).Select(row => row.Ordinal).ToHashSet();
        if (rows is null || rows.Count > DetroitAudioSnapshotReader.MaximumDialogueHeaders || rows.Count != expected.Count)
            throw new InvalidDataException("Dialogue header counts differ from the archive.");
        var seen = new HashSet<long>();
        long candidates = 0, digests = 0, characters = 0;
        var volumes = snapshot.Volumes.ToDictionary(volume => Path.GetFullPath(volume.FilePath), StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (row is null || row.HeaderIdentity is null || row.HeaderIdentity.IndexContentId != snapshot.IndexContentId ||
                row.HeaderIdentity.MemberPath is not null || !expected.Contains(row.HeaderIdentity.Ordinal) || !seen.Add(row.HeaderIdentity.Ordinal))
                throw new InvalidDataException("Dialogue header identity is invalid.");
            CheckResolution(row.AudioResolution, 1033);
            CheckResolution(row.EventResolution, 1031);
            if (row.ReadDigests is null) throw new InvalidDataException("Dialogue header provenance is missing.");
            digests += row.ReadDigests.Count;
            if (digests > MaximumDialogueDigests) throw new InvalidDataException("Dialogue provenance exceeds its limit.");
            foreach (var digest in row.ReadDigests)
            {
                token.ThrowIfCancellationRequested();
                if (digest?.Slice is null || digest.Owner != row.HeaderIdentity || digest.Sha256 is not { Length: 64 } sha || !sha.All(Uri.IsHexDigit) ||
                    digest.Slice.FilePath is not { Length: > 0 and <= 4096 } path || !volumes.TryGetValue(Path.GetFullPath(path), out var volume) ||
                    digest.Slice.Offset < 0 || digest.Slice.Length < 0 || digest.Slice.Offset > volume.Stamp.Length - digest.Slice.Length ||
                    digest.Role is { Length: > 1024 })
                    throw new InvalidDataException("Dialogue header provenance is invalid.");
                characters += path.Length + 64 + (digest.Role?.Length ?? 0);
            }
        }
        if (characters > MaximumDialogueHeaderCacheBytes / 6)
            throw new InvalidDataException("Dialogue header text exceeds its serialization limit.");

        void CheckResolution(ReferenceResolution? resolution, uint type)
        {
            if (resolution?.Candidates is null || !Enum.IsDefined(resolution.Status) || resolution.Reason is { Length: > 4096 })
                throw new InvalidDataException("Dialogue target resolution is invalid.");
            if (resolution.Status == ReferenceStatus.Unique && resolution.Candidates.Count != 1)
                throw new InvalidDataException("Unique dialogue target has an invalid count.");
            candidates += resolution.Candidates.Count;
            if (candidates > MaximumDialogueCandidates) throw new InvalidDataException("Dialogue candidates exceed their limit.");
            characters += resolution.Reason?.Length ?? 0;
            foreach (var identity in resolution.Candidates)
            {
                if (identity is null || identity.IndexContentId != snapshot.IndexContentId || identity.MemberPath is not null ||
                    identity.Ordinal < 0 || identity.Ordinal >= snapshot.Records.Count ||
                    snapshot.Records[(int)identity.Ordinal] is not { } record || record.Ordinal != identity.Ordinal || record.Type != type)
                    throw new InvalidDataException("Dialogue target identity is invalid.");
            }
        }
    }
}
