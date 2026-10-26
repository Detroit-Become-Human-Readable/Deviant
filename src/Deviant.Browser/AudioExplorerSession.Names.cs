using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Formats.Audio.Model;
using AudioNameEvidence = Deviant.Formats.Audio.Model.NameEvidence;

namespace Deviant.Browser;

public sealed partial class AudioExplorerSession
{
    private sealed record AudioSourceNames(uint Id, string Bank, string Name, string DisplayLabel,
        NameKind Kind, AudioNameEvidence[] Evidence, string[] Aliases);
    private readonly Dictionary<string, AudioSourceNames> retainedSourceNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> retainedSourceNameReads = new(StringComparer.Ordinal);
    private long retainedSourceNameBytes;
    private long retainedSourceNameReadBytes;

    private void RememberSourceNameRead(string key)
    { if (retainedSourceNameReads.Add(key)) retainedSourceNameReadBytes += 64L + 2L * key.Length; }

    private static long SourceNameBytes(AudioSourceNames names) => 256L + 2L *
        (names.Bank.Length + names.Name.Length + names.DisplayLabel.Length + names.Aliases.Sum(alias => (long)alias.Length) +
         names.Evidence.Sum(name => (long)name.Text.Length + name.Namespace.Length + name.Source.Length +
             (name.Confidence?.Length ?? 0) + (name.HashMethod?.Length ?? 0) +
             (name.CollisionGroup?.Length ?? 0) + (name.InputSha256?.Length ?? 0))) +
        names.Evidence.LongLength * 128 + names.Aliases.LongLength * 32;

    private static int SourceNameRank(NameKind kind) => kind switch
    { NameKind.Verified => 3, NameKind.Stored => 2, NameKind.Description => 1, _ => 0 };

    private static (string Label, NameKind Kind) PreferredSourceName(MediaEntry media)
    {
        var choices = new[] { (media.DisplayLabel, media.NameKind), (media.Name, media.NameKind) }
            .Concat(media.Names.Select(name => (name.Text, name.Kind)));
        return choices.Where(name => SourceNameRank(name.Item2) > 0 &&
                !string.IsNullOrWhiteSpace(name.Item1) && !LooksLikeNumericId(name.Item1, media.Id))
            .OrderByDescending(name => SourceNameRank(name.Item2)).FirstOrDefault();
    }

    private static AudioSourceNames? CaptureSourceNames(MediaEntry media)
    {
        if (media.Name is null || media.DisplayLabel is null || media.BankKey is null || media.Names is null ||
            media.Aliases is null || media.Aliases.Any(alias => alias is null) ||
            media.Names.Any(name => name is null || name.Text is null || name.Namespace is null || name.Source is null)) return null;
        var preferred = PreferredSourceName(media);
        if (string.IsNullOrWhiteSpace(preferred.Label)) return null;
        var evidence = media.Names.Where(name => SourceNameRank(name.Kind) > 0).Distinct().ToList();
        if (SourceNameRank(media.NameKind) > 0 && media.Identity is { } owner)
            foreach (var text in new[] { media.Name, media.DisplayLabel })
                if (!string.IsNullOrWhiteSpace(text) && !LooksLikeNumericId(text, media.Id) &&
                    !evidence.Any(name => name.Text == text && name.Kind == media.NameKind))
                    evidence.Add(new(text, "MediaLabel", media.Id, media.NameKind, owner.Key));
        var aliases = media.Aliases.Where(alias => evidence.Any(name =>
                string.Equals(name.Text, alias, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var explicitName = SourceNameRank(media.NameKind) == SourceNameRank(preferred.Kind) &&
            new[] { media.Name, media.DisplayLabel }.Any(name => name == preferred.Label);
        return new(media.Id, media.BankKey, explicitName ? media.Name : preferred.Label,
            explicitName ? media.DisplayLabel : preferred.Label, preferred.Kind, evidence.ToArray(), aliases);
    }

    private static void MergeSourceNames(MediaEntry incoming, AudioSourceNames? retained)
    {
        if (retained is not null && (retained.Id != incoming.Id || retained.Bank != incoming.BankKey)) retained = null;
        var current = CaptureSourceNames(incoming);
        var preferred = retained is not null && (current is null || SourceNameRank(retained.Kind) > SourceNameRank(current.Kind))
            ? retained : current;
        if (preferred is not null)
        {
            incoming.Name = preferred.Name;
            incoming.DisplayLabel = preferred.DisplayLabel;
            incoming.NameKind = preferred.Kind;
        }
        incoming.Names = incoming.Names.Concat(current?.Evidence ?? []).Concat(retained?.Evidence ?? []).Distinct().ToList();
        incoming.Aliases = incoming.Aliases.Concat(retained?.Aliases ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Capture values rather than reader-owned lists: incremental readers can reuse a media object.
    private void RememberSourceNames(MediaEntry media)
    {
        if (media.Identity is { } identity && CaptureSourceNames(media) is { } names)
        {
            if (retainedSourceNames.TryGetValue(identity.Key, out var old))
            {
                retainedSourceNameBytes -= SourceNameBytes(old);
                if (old.Id == names.Id && old.Bank == names.Bank)
                {
                    var preferred = SourceNameRank(old.Kind) > SourceNameRank(names.Kind) ? old : names;
                    names = preferred with { Evidence = names.Evidence.Concat(old.Evidence).Distinct().ToArray(),
                        Aliases = names.Aliases.Concat(old.Aliases).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
                }
            }
            retainedSourceNames[identity.Key] = names;
            retainedSourceNameBytes += SourceNameBytes(names);
        }
    }

    private async Task RetainSourceNamesAsync(IReadOnlyList<MediaEntry> entries, CancellationToken token,
        FeatureStore? sourceStore = null)
    {
        sourceStore ??= featureStore;
        var missing = new List<ResourceIdentity>();
        var residentNames = new Dictionary<string, AudioSourceNames>(StringComparer.Ordinal);
        lock (catalogGate)
            foreach (var media in entries)
                if (media.Identity is { } identity)
                {
                    if (browseRows.TryGetValue(identity.Key, out var row) && row.SourceMedia is { } resident &&
                        row.Descriptor.Revision == new SourceRevision(Snapshot.IndexContentId, Snapshot.PhysicalSourceStamp) &&
                        CaptureSourceNames(resident) is { } names) residentNames[identity.Key] = names;
                    if (!retainedSourceNameReads.Contains(identity.Key)) missing.Add(identity);
                }
        var storedNames = new Dictionary<string, AudioSourceNames>(StringComparer.Ordinal);
        async Task ReadAsync(ResourceIdentity[] identities)
        {
            if (identities.Length == 0) return;
            IReadOnlyList<BoundedAssetFeatureRow<AudioRecoveryCheckpoint>> rows;
            try
            {
                rows = await Task.Run(() => sourceStore.ReadBoundedAssetFeaturesByIdentity<AudioRecoveryCheckpoint>(
                    AssetFamily, identities, RecoveryFeaturePrefix, RecoveryVersion, MaximumRecoveryPayloadBytes, token), token)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException error)
            {
                if (identities.Length == 1)
                {
                    WorkflowDiagnostics.Failure("Audio.NameRetention", error, identities[0].Key, partial: true);
                    return;
                }
                var middle = identities.Length / 2;
                await ReadAsync(identities[..middle]).ConfigureAwait(false);
                await ReadAsync(identities[middle..]).ConfigureAwait(false);
                return;
            }
            foreach (var row in rows)
                if (TryReadRecoveryMedia(row, out var media, out _) && CaptureSourceNames(media) is { } names)
                    storedNames[row.Asset.Identity.Key] = names;
            var linked = rows.Select(row => row.Asset.Identity).ToArray();
            if (linked.Length > 0)
            {
                IReadOnlyList<BoundedAssetFeatureRow<MediaEntry>> evidence;
                try
                {
                    evidence = await Task.Run(() => sourceStore.ReadRetainedLinkedFeatureEvidence<MediaEntry>(
                        BrowseScope, linked, MaximumRecoveryPayloadBytes, token), token).ConfigureAwait(false);
                }
                catch (InvalidDataException error)
                {
                    if (identities.Length == 1)
                    {
                        WorkflowDiagnostics.Failure("Audio.NameRetention", error, identities[0].Key, partial: true);
                        return;
                    }
                    var middle = identities.Length / 2;
                    await ReadAsync(identities[..middle]).ConfigureAwait(false);
                    await ReadAsync(identities[middle..]).ConfigureAwait(false);
                    return;
                }
                foreach (var row in evidence)
                    if (row.ErrorCode is null && row.Feature is { } media && media.Identity == row.Asset.Identity &&
                        row.Asset.Revision == new SourceRevision(Snapshot.IndexContentId, Snapshot.PhysicalSourceStamp) &&
                        CaptureSourceNames(media) is { } names)
                    {
                        if (storedNames.TryGetValue(row.Asset.Identity.Key, out var recovery) &&
                            recovery.Id == names.Id && recovery.Bank == names.Bank)
                        {
                            var preferred = SourceNameRank(recovery.Kind) >= SourceNameRank(names.Kind) ? recovery : names;
                            names = preferred with { Evidence = recovery.Evidence.Concat(names.Evidence).Distinct().ToArray(),
                                Aliases = recovery.Aliases.Concat(names.Aliases).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
                        }
                        storedNames[row.Asset.Identity.Key] = names;
                    }
            }
        }
        foreach (var batch in missing.Distinct().Chunk(FeatureStore.MaximumLinkedFeaturePageRows))
            await ReadAsync(batch).ConfigureAwait(false);
        lock (catalogGate)
            foreach (var media in entries)
            {
                token.ThrowIfCancellationRequested();
                if (media.Identity is not { } identity) continue;
                MergeSourceNames(media, storedNames.GetValueOrDefault(identity.Key));
                if (residentNames.TryGetValue(identity.Key, out var resident)) MergeSourceNames(media, resident);
                if (retainedSourceNames.TryGetValue(identity.Key, out var retained)) MergeSourceNames(media, retained);
                if (mediaByIdentity.ContainsKey(identity.Key)) RememberSourceNameRead(identity.Key);
            }
    }
}
