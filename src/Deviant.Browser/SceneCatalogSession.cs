using System.Security.Cryptography;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Catalog;
using Deviant.Core;
using Deviant.Formats.Containers;
using Deviant.Formats.Scenes;

namespace Deviant.Browser;

public sealed partial class SceneCatalogSession : ISceneCatalogSession
{
    public const string AssetFamily = "scene.section";
    private const string CacheCollection = "scene.section-metadata";
    private static readonly string CacheVersion = SceneSectionReader.Version + ":" +
        typeof(SceneSectionReader).Assembly.ManifestModule.ModuleVersionId;
    private readonly FeatureStore store;
    private readonly SerializedWorker worker;
    private readonly AliasStore aliases;
    private readonly SceneDiscoveryOptions options;
    private readonly ArchiveResourceResolver source;
    private readonly FileRevisionProvider revisions = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly object gate = new();
    private readonly HashSet<Task> operations = [];
    private readonly Dictionary<string, AssetDescriptor> verified = new(StringComparer.Ordinal);
    private readonly List<ResourceIdentity> candidates = [];
    private readonly HashSet<long> candidateOrdinals = [];
    private CancellationTokenSource discoveryCancellation = new();
    private Task? discoveryTask, disposeTask;
    private int nextCandidate;
    private long? nextDependencyOwner = 0;
    private bool running, stopped;
    private string? message;

    private SceneCatalogSession(ArchiveSnapshot snapshot, FeatureStore store, SerializedWorker worker,
        AliasStore aliases, SceneDiscoveryOptions options)
    {
        Snapshot = snapshot; Revision = new(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        this.store = store; this.worker = worker; this.aliases = aliases; this.options = options;
        source = new(snapshot);
    }

    public ArchiveSnapshot Snapshot { get; }
    public SourceRevision Revision { get; }
    public event EventHandler? Updated;
    public SceneDiscoveryProgress Progress
    {
        get { lock (gate) return new(running, nextCandidate, candidates.Count, verified.Count,
            nextCandidate < candidates.Count || nextDependencyOwner is not null, message); }
    }

    public static async Task<SceneCatalogSession> OpenAsync(ArchiveSnapshot snapshot, CacheManager cache,
        AliasStore aliases, SceneDiscoveryOptions? options = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(cache); ArgumentNullException.ThrowIfNull(aliases);
        options ??= new();
        if (options.MaximumCandidatesPerBatch is < 1 or > 128 || options.MaximumContainerStoredBytes is < 1 or > 16 * 1024 * 1024 ||
            options.MaximumContainerDecodedBytes is < 1024 or > 64 * 1024 * 1024 ||
            options.MaximumStoredBytesPerBatch < options.MaximumContainerStoredBytes || options.MaximumStoredBytesPerBatch > 256L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options));
        cache.RegisterProtectedDirectory(aliases.LibraryRoot);
        var worker = new SerializedWorker("Deviant scene catalog");
        try
        {
            var store = await worker.InvokeAsync(() => FeatureStore.Open(snapshot, cache.Location), token).ConfigureAwait(false);
            return new(snapshot, store, worker, aliases, options);
        }
        catch { await worker.StopAsync().ConfigureAwait(false); throw; }
    }

    public Task DiscoverMoreAsync(CancellationToken token = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (discoveryTask is { IsCompleted: false }) return discoveryTask.WaitAsync(token);
            discoveryCancellation.Dispose(); discoveryCancellation = new();
            var discoveryToken = discoveryCancellation.Token;
            discoveryTask = Track(async linked =>
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(linked, discoveryToken);
                await DiscoverBatchAsync(cancel.Token).ConfigureAwait(false);
                return true;
            }, token);
            return discoveryTask;
        }
    }

    private async Task DiscoverBatchAsync(CancellationToken token)
    {
        lock (gate) { running = true; message = null; }
        Notify();
        try
        {
            if (nextCandidate >= candidates.Count && nextDependencyOwner is not null)
                await ReadCandidatePageAsync(token).ConfigureAwait(false);
            long bytes = 0; var visited = 0;
            while (nextCandidate < candidates.Count && visited < options.MaximumCandidatesPerBatch)
            {
                token.ThrowIfCancellationRequested();
                var identity = candidates[nextCandidate]; var row = Snapshot.Records[(int)identity.Ordinal];
                var eligible = row.Validity == ResourceValidity.Valid && row.StoredSize > 0 && row.StoredSize <= options.MaximumContainerStoredBytes;
                if (eligible && row.StoredSize > options.MaximumStoredBytesPerBatch - bytes) break;
                try
                {
                    if (eligible)
                    {
                        bytes += row.StoredSize;
                        await readGate.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            var raw = await ReadSourceAsync(identity, token).ConfigureAwait(false);
                            var hash = Hash(raw);
                            var cached = await worker.InvokeAsync(() =>
                            {
                                try { return store.LoadItem<SceneCacheEntry>(CacheCollection, identity.Key, CacheVersion, token); }
                                catch (JsonException) { return null; }
                            }, token).ConfigureAwait(false);
                            if (cached?.Descriptor is not null && cached.SourceHash == hash && cached.Descriptor.Revision == Revision &&
                                cached.Descriptor.Identity == (identity with { MemberPath = SceneSectionReader.MemberPath }))
                                await PublishAsync(cached.Descriptor, identity, hash, token).ConfigureAwait(false);
                            else
                            {
                                var decoded = await Task.Run(() => Decode(identity, raw, token), token).ConfigureAwait(false);
                                if (decoded.Result.Value is { } section)
                                    await PublishAsync(Describe(decoded.Result, section), identity, hash, token).ConfigureAwait(false);
                            }
                        }
                        finally { readGate.Release(); }
                    }
                }
                catch (ArchiveException error) when (error.Code == "SourceChanged") { throw; }
                catch (IOException) { lock (gate) message = "Some sections are unavailable."; }
                token.ThrowIfCancellationRequested();
                lock (gate) nextCandidate++;
                visited++; Notify();
            }
        }
        catch (OperationCanceledException) { lock (gate) message = "Cancelled"; throw; }
        catch { lock (gate) message = "Discovery failed"; throw; }
        finally { lock (gate) running = false; Notify(); }
    }

    private async Task ReadCandidatePageAsync(CancellationToken token)
    {
        var start = nextDependencyOwner;
        if (start is null) return;
        var path = Path.ChangeExtension(Snapshot.IndexPath, ".dep");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
        if (input.Length > 64 * 1024 * 1024) throw new ArchiveException("DependencyByteLimit", "The dependency table exceeds its limit.");
        var stamp = revisions.Capture(input.SafeFileHandle, path);
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        SourceIdentity.EnsureUnchanged(revisions, input.SafeFileHandle, path, stamp);
        var page = await Task.Run(() => new SceneDependencyReader().FindOwners(bytes, Snapshot, Path.GetFileName(path),
            new HashSet<uint> { SceneEnvironmentReader.NativeType }, start.Value, 1024, 8, token: token), token).ConfigureAwait(false);
        SourceIdentity.EnsureUnchanged(revisions, input.SafeFileHandle, path, stamp);
        EnsureIndexStamp();
        lock (gate)
        {
            foreach (var owner in page.Owners.Where(owner => owner.OwnerType == 29))
                if (candidateOrdinals.Add(owner.Owner.Ordinal)) candidates.Add(owner.Owner);
            nextDependencyOwner = page.HasMore ? page.NextOrdinal : null;
        }
        Notify();
    }

    public Task<SceneCatalogPage> QueryAsync(string? search = null, long offset = 0, int limit = 100, CancellationToken token = default)
    {
        if (offset < 0 || limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        return Track(async linked =>
        {
            EnsureSourceStamps();
            Dictionary<string, AssetDescriptor> accepted;
            lock (gate) accepted = new(verified, StringComparer.Ordinal);
            return await worker.InvokeAsync(() =>
            {
                var query = new AssetQuery(Search: search, Family: AssetFamily, Limit: 256);
                var total = store.Count(query, linked); long acceptedCount = 0;
                var result = new List<AssetDescriptor>(limit);
                for (long cursor = 0; cursor < total; cursor += 256)
                {
                    linked.ThrowIfCancellationRequested();
                    foreach (var entry in store.Query(query with { Offset = cursor }, linked))
                    {
                        if (!accepted.TryGetValue(entry.Identity.Key, out var expected) || expected.Revision != entry.Revision) continue;
                        if (acceptedCount++ >= offset && result.Count < limit) result.Add(entry);
                    }
                }
                return new SceneCatalogPage(result, acceptedCount, offset);
            }, linked).ConfigureAwait(false);
        }, token);
    }

    public Task<SceneLoadedSection> LoadAsync(ResourceIdentity identity, CancellationToken token = default) => Track(async linked =>
    {
        if (identity.IndexContentId != Snapshot.IndexContentId || identity.Ordinal < 0 || identity.Ordinal >= Snapshot.Records.Count ||
            identity.MemberPath is not (null or SceneSectionReader.MemberPath))
            throw new ArchiveException("InvalidSceneIdentity", "The scene belongs to another source.");
        var container = identity with { MemberPath = null };
        await readGate.WaitAsync(linked).ConfigureAwait(false);
        try
        {
            var raw = await ReadSourceAsync(container, linked).ConfigureAwait(false);
            var decoded = await Task.Run(() => Decode(container, raw, linked), linked).ConfigureAwait(false);
            if (decoded.Result.Value is not { } section)
                throw new ArchiveException("UnsupportedScene", "This section is unavailable.");
            var descriptor = Describe(decoded.Result, section);
            var loaded = await EnrichAsync(descriptor, decoded, linked).ConfigureAwait(false);
            await PublishAsync(descriptor, container, Hash(raw), linked).ConfigureAwait(false);
            return loaded;
        }
        finally { readGate.Release(); }
    }, token);

    private async Task<byte[]> ReadSourceAsync(ResourceIdentity identity, CancellationToken token)
    {
        var row = Snapshot.Records[(int)identity.Ordinal];
        if (row.Type != 29 || row.Validity != ResourceValidity.Valid || row.StoredSize < 1 || row.StoredSize > options.MaximumContainerStoredBytes)
            throw new ArchiveException("SceneSourceUnavailable", "The scene source is unavailable or exceeds its limit.");
        return await source.ReadAsync(identity, 0, checked((int)row.StoredSize), token).ConfigureAwait(false);
    }

    private DecodedSection Decode(ResourceIdentity identity, byte[] raw, CancellationToken token)
    {
        var row = Snapshot.Records[(int)identity.Ordinal];
        var container = new SegsContainerReader().Parse(raw, identity, Revision,
            new(row.VolumeName, row.Offset, row.StoredSize, Hash(raw)), row.AlternativeSize,
            new(options.MaximumContainerStoredBytes, options.MaximumContainerDecodedBytes), token);
        var directory = new NativeObjectDirectoryLoader().Read(container, token);
        return new(new SceneSectionReader().Read(directory, token), directory.Value);
    }

    private async Task PublishAsync(AssetDescriptor descriptor, ResourceIdentity sourceIdentity, string hash, CancellationToken token)
    {
        descriptor = ApplyAlias(descriptor);
        await worker.InvokeAsync(() =>
        {
            store.UpsertAssets([descriptor], token);
            store.UpsertItems(CacheCollection, CacheVersion,
                [new KeyValuePair<string, SceneCacheEntry>(sourceIdentity.Key, new(hash, descriptor))], token);
            return true;
        }, token).ConfigureAwait(false);
        lock (gate) verified[descriptor.Identity.Key] = descriptor;
        Notify();
    }

    private void EnsureIndexStamp()
    {
        if (revisions.Capture(Snapshot.IndexPath) != Snapshot.IndexStamp)
            throw new ArchiveException("SourceChanged", "The source changed. Reopen the archive.");
    }

    private void EnsureSourceStamps()
    {
        EnsureIndexStamp();
        foreach (var volume in Snapshot.Volumes)
            if (revisions.Capture(volume.FilePath) != volume.Stamp)
                throw new ArchiveException("SourceChanged", "The source changed. Reopen the archive.");
    }

    public void CancelDiscovery() { lock (gate) { if (!stopped) discoveryCancellation.Cancel(); } }
    private void Notify() { if (!stopped) Updated?.Invoke(this, EventArgs.Empty); }

    private Task<T> Track<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            var task = Run(); operations.Add(task); _ = RemoveWhenComplete(task); return task;
        }
        async Task<T> Run()
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            linked.Token.ThrowIfCancellationRequested(); return await action(linked.Token).ConfigureAwait(false);
        }
    }

    private async Task RemoveWhenComplete(Task task)
    { try { await task.ConfigureAwait(false); } catch { } finally { lock (gate) operations.Remove(task); } }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposeTask is not null) return new(disposeTask);
            stopped = true; lifetime.Cancel(); discoveryCancellation.Cancel();
            var pending = operations.ToArray(); disposeTask = DisposeCoreAsync(pending); return new(disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task[] pending)
    {
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { }
        await worker.StopAsync(store.Dispose).ConfigureAwait(false);
        readGate.Dispose(); discoveryCancellation.Dispose(); lifetime.Dispose();
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed record SceneCacheEntry(string SourceHash, AssetDescriptor Descriptor);
    private sealed record DecodedSection(DecodeResult<SceneSectionDocument> Result, NativeObjectDirectoryDocument? Directory);
}
