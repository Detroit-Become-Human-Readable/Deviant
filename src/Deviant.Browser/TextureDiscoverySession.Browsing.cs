using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;

namespace Deviant.Browser;

public sealed partial class TextureDiscoverySession
{
    private readonly HashSet<BrowseSession> browseSessions = [];
    private readonly HashSet<Task<ITextureBrowseSession>> browseOpenTasks = [];
    private long memoryBrowseGeneration;

    public Task<ITextureBrowseSession> OpenBrowseAsync(TextureDiscoveryQuery query, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 || query.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(query));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var task = Task.Run(() => OpenBrowseCoreAsync(query, token));
            browseOpenTasks.Add(task);
            _ = task.ContinueWith(done => { lock (gate) browseOpenTasks.Remove(done); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private async Task<ITextureBrowseSession> OpenBrowseCoreAsync(TextureDiscoveryQuery query, CancellationToken token)
    {
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        var source = ArchiveSourceSession.Acquire(Snapshot);
        FeatureStore.TextureBrowseReadLease? lease = null;
        try
        {
            await source.Session.ValidateIndexAsync(own.Token).ConfigureAwait(false);
            EnsureBrowseSourceCurrent(source.Session, own.Token);
            var store = projectionReady ? Volatile.Read(ref features) : null;
            if (query.CatalogKey is { } requested)
            {
                var group = catalogs.Groups.SingleOrDefault(group => group.CatalogIdentity.Key == requested);
                if (group is null)
                {
                    var identity = ParseBrowseCatalog(requested);
                    await catalogs.ReadCatalogAsync(identity, own.Token).ConfigureAwait(false);
                    group = catalogs.Groups.SingleOrDefault(group => group.CatalogIdentity == identity)
                        ?? throw new ArchiveException("UnknownTextureCatalog", "The selected texture catalog is unavailable.");
                }
                await EnsureDirectCatalogProjectionAsync(group, own.Token).ConfigureAwait(false);
            }
            AssetLoadDiagnostics.Counters("TextureBrowser.Browse.BackendSnapshot", Snapshot.IndexContentId,
                new Dictionary<string, long> { ["ProjectionReady"] = projectionReady ? 1 : 0 });
            if (store is not null)
                lease = store.AcquireTextureBrowseReadLease(ProjectionProfile, Revision, token: own.Token,
                    evidenceValidator: ValidBrowseEvidence);
            TextureBrowseResponse? memory = null;
            if (lease is null)
            {
                lock (gate)
                {
                    own.Token.ThrowIfCancellationRequested();
                    var groups = catalogs.Groups;
                    var all = OrderedEntries(new(IncludeLodBuffers: true), own.Token, capturedGroups: groups);
                    var found = OrderedEntries(query, own.Token, all, groups);
                    var options = new TextureDiscoveryFilterOptions(
                        all.Where(entry => entry.Width.HasValue && entry.Height.HasValue)
                            .Select(entry => new TextureDimensionOption(entry.Width!.Value, entry.Height!.Value)).Distinct().ToArray(),
                        all.Select(entry => entry.Format).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                        all.SelectMany(entry => entry.Owners.Where(owner => owner.Verified).Select(owner => owner.Role))
                            .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                        all.Where(entry => entry.ContextKey is not null && entry.ContextLabel is not null)
                            .Select(entry => new TextureContextOption(entry.ContextKey!, entry.ContextLabel!)).Distinct().ToArray())
                    { Catalogs = groups.Select(group => new TextureContextOption(group.CatalogIdentity.Key, group.Label)).ToArray() };
                    var coverage = new TextureBrowseIndexCoverage(all.LongLength, all.LongCount(entry => entry.MetadataIdentities.Count > 0),
                        all.LongCount(entry => entry.NameKind is TextureNameKind.OriginalName or TextureNameKind.OriginalPath),
                        all.LongCount(entry => entry.NameKind == TextureNameKind.VerifiedOwnerRole),
                        all.LongCount(entry => entry.NameKind == TextureNameKind.InferredDescription),
                        all.LongCount(entry => entry.NameKind == TextureNameKind.Fallback), all.LongCount(entry => entry.Alias is not null),
                        all.LongCount(entry => entry.ConflictLines.Count > 0), all.LongCount(entry => entry.Kind == TextureKind.Image),
                        all.LongCount(entry => entry.Kind == TextureKind.Lighting), all.LongCount(entry => entry.Kind == TextureKind.LodBuffer),
                        all.SelectMany(entry => entry.MetadataIdentities).Distinct().LongCount());
                    memory = new(new(Revision, ProjectionProfile + "/memory", ++memoryBrowseGeneration),
                        new(found, found.LongLength, 0), options, coverage);
                }
            }
            lock (gate)
            {
                own.Token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                var session = new BrowseSession(this, query, store, lease, memory, source, lifetime.Token);
                browseSessions.Add(session);
                return session;
            }
        }
        catch { lease?.Dispose(); await source.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private ResourceIdentity ParseBrowseCatalog(string key)
    {
        var parts = key.Split(':');
        if (parts.Length != 3 || parts[0] != Revision.IndexContentId || parts[2].Length != 0 ||
            !long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ordinal) ||
            !resources.TryGetValue(ordinal, out var row) || row.Type != 3 || row.Validity != ResourceValidity.Valid)
            throw new ArchiveException("UnknownTextureCatalog", "The selected texture catalog is unavailable.");
        return new(Revision.IndexContentId, ordinal);
    }

    private bool ValidBrowseEvidence(TextureEvidence evidence, TextureProjectionOwnerCheckpoint? checkpoint)
    {
        var checkpoints = new Dictionary<ResourceIdentity, TextureProjectionOwnerCheckpoint>();
        if (checkpoint is not null && ValidProjectionCheckpoint(checkpoint)) checkpoints[checkpoint.Identity] = checkpoint;
        return ValidProjectionEvidence(evidence, checkpoints);
    }

    private void EnsureBrowseSourceCurrent(ArchiveSourceSession source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        source.EnsureCurrent();
        var stamps = new FileRevisionProvider();
        foreach (var volume in Snapshot.Volumes)
        {
            token.ThrowIfCancellationRequested();
            if (!SourceIdentity.SameStamp(volume.Stamp, stamps.Capture(volume.FilePath)))
                throw new ArchiveException("SourceChanged", "The archive changed. Reopen the installation.");
        }
    }

    private async Task DrainBrowseSessionsAsync()
    {
        Task<ITextureBrowseSession>[] opening;
        lock (gate) opening = browseOpenTasks.ToArray();
        try { await Task.WhenAll(opening).ConfigureAwait(false); } catch { }
        BrowseSession[] sessions;
        lock (gate) sessions = browseSessions.ToArray();
        foreach (var session in sessions) await session.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class BrowseSession : ITextureBrowseSession
    {
        private readonly TextureDiscoverySession owner;
        private readonly TextureDiscoveryQuery key;
        private readonly FeatureStore? store;
        private FeatureStore.TextureBrowseReadLease? lease;
        private readonly TextureBrowseResponse? memory;
        private readonly ArchiveSourceSession.Lease source;
        private readonly CancellationTokenSource cancellation;
        private readonly SemaphoreSlim serial = new(1, 1);
        private readonly object gate = new();
        private readonly HashSet<Task> reads = [];
        private Task? disposal;
        public BrowseSession(TextureDiscoverySession owner, TextureDiscoveryQuery query, FeatureStore? store,
            FeatureStore.TextureBrowseReadLease? lease, TextureBrowseResponse? memory, ArchiveSourceSession.Lease source,
            CancellationToken lifetime)
        {
            this.owner = owner; key = query with { Offset = 0, Limit = 1 }; this.store = store;
            this.lease = lease; this.memory = memory; this.source = source;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        }
        public Task<TextureBrowseResponse> ReadAsync(TextureDiscoveryQuery query, CancellationToken token = default)
            => StartRead(query, async cancellation => (await ReadCoreAsync(query, null, cancellation).ConfigureAwait(false))!, token);

        public Task<TextureBrowseResponse?> LocateAsync(ResourceIdentity identity, TextureDiscoveryQuery query, CancellationToken token = default)
            => StartRead(query, cancellation => ReadCoreAsync(query, identity, cancellation), token);

        private Task<T> StartRead<T>(TextureDiscoveryQuery query, Func<CancellationToken, Task<T>> read, CancellationToken token)
        {
            if (query.Offset < 0 || query.Limit is < 1 or > 1000) return Task.FromException<T>(new ArgumentOutOfRangeException(nameof(query)));
            if ((query with { Offset = 0, Limit = 1 }) != key)
                return Task.FromException<T>(new ArgumentException("Open a new browse session when filters change.", nameof(query)));
            lock (gate)
            {
                if (disposal is not null) return Task.FromCanceled<T>(new CancellationToken(true));
                var task = Task.Run(() => read(token));
                reads.Add(task);
                _ = task.ContinueWith(done => { lock (gate) reads.Remove(done); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return task;
            }
        }
        private async Task<TextureBrowseResponse?> ReadCoreAsync(TextureDiscoveryQuery query, ResourceIdentity? identity, CancellationToken token)
        {
            using var own = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation.Token);
            await serial.WaitAsync(own.Token).ConfigureAwait(false);
            try
            {
                owner.EnsureBrowseSourceCurrent(source.Session, own.Token);
                if (identity is not null)
                {
                    long? offset;
                    if (lease is not null) offset = await lease.FindPageOffsetAsync(identity, query, own.Token).ConfigureAwait(false);
                    else
                    {
                        int index = -1;
                        for (int i = 0; i < memory!.Page.Entries.Count; i++)
                            if (memory.Page.Entries[i].Identity == identity) { index = i; break; }
                        offset = index < 0 ? null : index / query.Limit * query.Limit;
                    }
                    if (offset is null) return null;
                    query = query with { Offset = offset.Value };
                }
                TextureBrowseResponse result;
                if (lease is null)
                {
                    var captured = memory!;
                    result = captured with { Page = new(captured.Page.Entries.Skip(checked((int)Math.Min(query.Offset, int.MaxValue)))
                        .Take(query.Limit).ToArray(), captured.Page.Total, query.Offset) };
                }
                else
                {
                    try { result = await lease.ReadAsync(query, own.Token).ConfigureAwait(false); }
                    catch (TextureBrowseEvidenceException error)
                    {
                        own.Token.ThrowIfCancellationRequested();
                        lock (owner.projectionStoreGate)
                        {
                            own.Token.ThrowIfCancellationRequested();
                            owner.RepairProjectedPage(store!, error.Identities, own.Token, validationGeneration: lease.Token.Generation);
                        }
                        lease.Dispose();
                        lease = store!.AcquireTextureBrowseReadLease(owner.ProjectionProfile, owner.Revision, token: own.Token,
                            evidenceValidator: owner.ValidBrowseEvidence);
                        if (identity is not null)
                        {
                            var offset = await lease.FindPageOffsetAsync(identity, query, own.Token).ConfigureAwait(false);
                            if (offset is null) return null;
                            query = query with { Offset = offset.Value };
                        }
                        result = await lease.ReadAsync(query, own.Token).ConfigureAwait(false);
                    }
                }
                owner.EnsureBrowseSourceCurrent(source.Session, own.Token);
                own.Token.ThrowIfCancellationRequested();
                return result;
            }
            finally { serial.Release(); }
        }
        public ValueTask DisposeAsync()
        {
            lock (gate)
            {
                if (disposal is not null) return new(disposal);
                cancellation.Cancel();
                var active = reads.ToArray();
                disposal = Task.Run(async () =>
                {
                    try { await Task.WhenAll(active).ConfigureAwait(false); } catch { }
                    try { lease?.Dispose(); }
                    finally
                    {
                        try { await source.DisposeAsync().ConfigureAwait(false); }
                        finally
                        {
                            cancellation.Dispose(); serial.Dispose();
                            lock (owner.gate) owner.browseSessions.Remove(this);
                        }
                    }
                });
                return new(disposal);
            }
        }
    }
}
