using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Localization.Model;

namespace Deviant.Browser;

public sealed record LocalizationAudioLookupCacheIssue(string Code, long Count, IReadOnlyList<ResourceIdentity> SourceResources,
    LocalizationReferenceLinkStatus Status = LocalizationReferenceLinkStatus.Unresolved, string? Message = null);

public sealed partial class LocalizationSession
{
    private const string AudioLookupCollection = "localization.audio-lookup";
    private const string AudioLookupReadyKey = AudioLookupCollection + ".ready";
    private long audioLookupBuildGeneration;
    private long? activeAudioLookupBuild;
    private string? activeAudioLookupCacheVersion;
    private Guid activeAudioLookupToken;

    public async Task<bool> IsAudioLookupReadyAsync(string version, string contentId, CancellationToken token = default)
    {
        var cacheVersion = AudioLookupCacheVersion(version, contentId);
        await featureGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var head = await Task.Run(() => featureStore.Load<AudioLookupHead>(AudioLookupReadyKey, cacheVersion, token), token).ConfigureAwait(false);
            return head is { Complete: true } && head.MetadataContentId == contentId;
        }
        finally { featureGate.Release(); }
    }

    public async Task<long> BeginAudioLookupBuildAsync(string version, string contentId, CancellationToken token = default)
    {
        var cacheVersion = AudioLookupCacheVersion(version, contentId);
        await featureGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var generation = ++audioLookupBuildGeneration;
            activeAudioLookupBuild = generation;
            activeAudioLookupCacheVersion = cacheVersion;
            activeAudioLookupToken = Guid.NewGuid();
            await Task.Run(() =>
            {
                featureStore.ResetItemsWithFeature(AudioLookupCollection, cacheVersion, AudioLookupReadyKey,
                    new AudioLookupHead(contentId, false, null, activeAudioLookupToken), token);
            }, token).ConfigureAwait(false);
            return generation;
        }
        finally { featureGate.Release(); }
    }

    public async Task UpsertAudioLookupCandidatesAsync(ResourceIdentity headerIdentity,
        IReadOnlyList<ResourceIdentity> pointerIdentities, string version, string contentId, long generation,
        CancellationToken token = default)
    {
        ValidateAudioLookupHeader(headerIdentity);
        if (pointerIdentities.Count > 65_536) throw new ArchiveException("AudioLookupLimit", "This header has too many text links to index in one batch.");
        foreach (var pointer in pointerIdentities)
            if (pointer.IndexContentId != snapshot.IndexContentId || pointer.Ordinal < 0 || pointer.Ordinal >= snapshot.Records.Count ||
                snapshot.Records[(int)pointer.Ordinal].Type != 1016 || string.IsNullOrEmpty(pointer.MemberPath))
                throw new ArchiveException("InvalidIdentity", "The text link does not belong to this localization source.");
        var cacheVersion = AudioLookupCacheVersion(version, contentId);
        await featureGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureAudioLookupBuild(generation, cacheVersion);
            await Task.Run(() =>
            {
                var accepted = featureStore.TryUpdateItemWithFeatureFence<AudioLookupHead, AudioLookupHeader>(
                    AudioLookupCollection, headerIdentity.Key, cacheVersion, AudioLookupReadyKey,
                    head => head is { Complete: false } && head.BuildToken == activeAudioLookupToken && head.MetadataContentId == contentId,
                    previous =>
                    {
                        if (previous is not null && previous.HeaderIdentity != headerIdentity)
                            throw new ArchiveException("AudioLookupInvalid", "The cached dialogue link belongs to another header.");
                        var pointers = (previous?.PointerIdentities ?? Array.Empty<ResourceIdentity>()).Concat(pointerIdentities)
                            .DistinctBy(identity => identity.Key).ToArray();
                        if (pointers.Length > 65_536) throw new ArchiveException("AudioLookupLimit", "This header has too many text links to index.");
                        return new AudioLookupHeader(headerIdentity, pointers);
                    }, token);
                if (!accepted) throw AudioLookupReplaced();
            }, token).ConfigureAwait(false);
        }
        finally { featureGate.Release(); }
    }

    public async Task CompleteAudioLookupBuildAsync(string version, string contentId, long generation,
        IReadOnlyList<LocalizationAudioLookupCacheIssue> issues,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(issues);
        if (issues.Count > 256 || issues.Any(issue => string.IsNullOrWhiteSpace(issue.Code) || issue.Code.Length > 256 ||
            issue.Count < 0 || issue.Message?.Length > 4096 || issue.SourceResources.Count > 4096 || issue.SourceResources.Any(identity => identity.IndexContentId != snapshot.IndexContentId)))
            throw new ArgumentException("The dialogue-link issue summary is invalid.", nameof(issues));
        var cacheVersion = AudioLookupCacheVersion(version, contentId);
        await featureGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureAudioLookupBuild(generation, cacheVersion);
            token.ThrowIfCancellationRequested();
            var accepted = await Task.Run(() => featureStore.TrySaveFeatureWithFence(AudioLookupReadyKey, cacheVersion,
                (AudioLookupHead? head) => head is { Complete: false } && head.BuildToken == activeAudioLookupToken && head.MetadataContentId == contentId,
                new AudioLookupHead(contentId, true, issues, activeAudioLookupToken), token), token).ConfigureAwait(false);
            if (!accepted) throw AudioLookupReplaced();
            activeAudioLookupBuild = null;
            activeAudioLookupCacheVersion = null;
        }
        finally { featureGate.Release(); }
    }

    public async Task<IReadOnlyList<LocalizationAudioLookupCacheIssue>> GetAudioLookupIssuesAsync(string version,
        string contentId, CancellationToken token = default)
    {
        var cacheVersion = AudioLookupCacheVersion(version, contentId);
        await featureGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var head = await Task.Run(() => featureStore.Load<AudioLookupHead>(AudioLookupReadyKey, cacheVersion, token), token).ConfigureAwait(false);
            if (head is not { Complete: true } || head.MetadataContentId != contentId)
                throw new ArchiveException("AudioLookupPending", "Dialogue links are still being indexed.");
            return head.Issues ?? Array.Empty<LocalizationAudioLookupCacheIssue>();
        }
        finally { featureGate.Release(); }
    }

    public async Task<IReadOnlyList<ResourceIdentity>> GetAudioLookupCandidatesAsync(ResourceIdentity headerIdentity,
        string version, string contentId, CancellationToken token = default)
    {
        ValidateAudioLookupHeader(headerIdentity);
        var cacheVersion = AudioLookupCacheVersion(version, contentId);
        await featureGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run<IReadOnlyList<ResourceIdentity>>(() =>
            {
                var accepted = featureStore.TryLoadItemWithFeatureFence<AudioLookupHead, AudioLookupHeader>(
                    AudioLookupCollection, headerIdentity.Key, cacheVersion, AudioLookupReadyKey,
                    head => head is { Complete: true } && head.MetadataContentId == contentId,
                    out var row, token);
                if (!accepted)
                    throw new ArchiveException("AudioLookupPending", "Dialogue links are still being indexed.");
                if (row is not null && row.PointerIdentities.Any(pointer => pointer.IndexContentId != snapshot.IndexContentId ||
                    pointer.Ordinal < 0 || pointer.Ordinal >= snapshot.Records.Count || snapshot.Records[(int)pointer.Ordinal].Type != 1016 || string.IsNullOrEmpty(pointer.MemberPath)))
                    throw new ArchiveException("AudioLookupInvalid", "The cached text links do not belong to this localization source.");
                return row?.HeaderIdentity == headerIdentity ? row.PointerIdentities : Array.Empty<ResourceIdentity>();
            }, token).ConfigureAwait(false);
        }
        finally { featureGate.Release(); }
    }

    private string AudioLookupCacheVersion(string version, string contentId)
    {
        ThrowIfDisposed();
        if (version != LocalizationCacheContract.AudioLookupContractVersion) throw new ArgumentException("The lookup contract version is unsupported.", nameof(version));
        if (!IsEnrichmentComplete || string.IsNullOrEmpty(contentId) || MetadataContentId != contentId)
            throw new ArchiveException("LocalizationPending", "Wait for text indexing to finish before reading dialogue links.");
        return LocalizationCacheContract.AudioLookupVersion(contentId);
    }

    private void ValidateAudioLookupHeader(ResourceIdentity identity)
    {
        if (identity.IndexContentId != snapshot.IndexContentId || identity.MemberPath is not null ||
            identity.Ordinal < 0 || identity.Ordinal >= snapshot.Records.Count || snapshot.Records[(int)identity.Ordinal].Type != 4091)
            throw new ArchiveException("InvalidIdentity", "The dialogue header does not belong to this archive.");
    }

    private void EnsureAudioLookupBuild(long generation, string cacheVersion)
    {
        if (activeAudioLookupBuild != generation || activeAudioLookupCacheVersion != cacheVersion)
            throw new ArchiveException("AudioLookupSuperseded", "A newer dialogue-link indexing job replaced this one.");
    }

    private static ArchiveException AudioLookupReplaced() => new("AudioLookupSuperseded", "A newer dialogue-link indexing job replaced this one.");

    private sealed record AudioLookupHead(string MetadataContentId, bool Complete,
        IReadOnlyList<LocalizationAudioLookupCacheIssue>? Issues = null, Guid BuildToken = default);
    private sealed record AudioLookupHeader(ResourceIdentity HeaderIdentity, IReadOnlyList<ResourceIdentity> PointerIdentities);
}
