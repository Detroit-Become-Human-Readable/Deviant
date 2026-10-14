using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

public enum TextureCatalogSessionState { Running, Completed, StoppedAtLimit, Cancelled, Failed }

public sealed record TextureCatalogSessionOptions(
    int MaximumCatalogs = 16_384,
    int MaximumReferenceRows = 1_000_000,
    int MaximumCatalogResourceBytes = LocalizationReferenceReader.MaximumResourceBytes,
    int MaximumReferencesPerCatalog = LocalizationReferenceReader.MaximumReferenceCount,
    IReadOnlyList<ResourceIdentity>? PriorityCatalogs = null);

public sealed record TextureCatalogGroup(
    string Label,
    ResourceIdentity CatalogIdentity,
    uint CatalogId,
    SourceExtent CatalogExtent,
    string ReferenceTableSha256,
    int SourceReferenceCount,
    IReadOnlyList<TextureCatalogReference> References,
    IReadOnlyList<ResourceIdentity> UniqueTextureMembers,
    int MissingCount,
    int AmbiguousCount,
    int UnresolvedCount,
    int DuplicateReferenceCount);

public sealed record TextureCatalogRelatedGroup(TextureCatalogGroup Catalog,
    IReadOnlyList<TextureCatalogReference> MatchingReferences);

public sealed record TextureCatalogRelationshipQuery(ResourceIdentity TextureIdentity, SourceRevision Revision,
    TextureCatalogSessionProgress Discovery, IReadOnlyList<TextureCatalogSessionDiagnostic> Diagnostics,
    IReadOnlyList<TextureCatalogRelatedGroup> Catalogs);

public sealed record TextureCatalogSessionDiagnostic(ResourceIdentity? CatalogIdentity, string Code, string Message);

public sealed record TextureCatalogSessionProgress(
    TextureCatalogSessionState State,
    int CatalogsAvailable,
    int CatalogsVisited,
    int TextureCatalogsFound,
    int ReferenceRowsRead,
    int TotalCatalogs,
    bool DiscoveryIncomplete,
    string? Message = null);

public sealed class TextureCatalogSessionUpdatedEventArgs(TextureCatalogSessionProgress progress) : EventArgs
{
    public TextureCatalogSessionProgress Progress { get; } = progress;
}

/// <summary>
/// Incrementally reads verified COM_CONT reference tables from type-3 catalog rows.
/// It reads no texture payloads.
/// </summary>
public sealed class TextureCatalogSession : IAsyncDisposable
{
    private readonly ICatalogReferenceProvider referenceProvider;
    private readonly CatalogReferenceProvider? ownedReferenceProvider;
    private readonly TextureCatalogMembershipReader memberships;
    private readonly TextureCatalogSessionOptions options;
    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly SynchronizationContext? updateContext;
    private readonly ResourceRecord[] catalogRows;
    private readonly Dictionary<long, ResourceRecord> catalogRowsByOrdinal;
    private readonly Dictionary<long, int> catalogPositions;
    private readonly HashSet<long> attemptedCatalogs = [];
    private readonly Dictionary<long, TextureCatalogGroup> groupsByOrdinal = [];
    private readonly Dictionary<ResourceIdentity, HashSet<long>> groupsByTexture = [];
    private readonly Dictionary<ResourceIdentity, HashSet<long>> groupsByTextureCandidate = [];
    private readonly List<TextureCatalogSessionDiagnostic> diagnostics = [];
    private int catalogsVisited;
    private int referenceRowsRead;
    private int textureCatalogsFound;
    private TextureCatalogSessionState state = TextureCatalogSessionState.Running;
    private bool discoveryIncomplete;
    private string? message;
    private Exception? error;
    private bool disposed;
    private int backgroundPauses;
    private TaskCompletionSource backgroundReady = CompletedBackgroundSignal();

    public IDisposable PauseBackgroundDiscovery()
    {
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (backgroundPauses++ == 0) backgroundReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new BackgroundPause(this);
        }
    }
    private void ResumeBackgroundDiscovery()
    { lock (stateGate) if (--backgroundPauses == 0) backgroundReady.TrySetResult(); }
    private static TaskCompletionSource CompletedBackgroundSignal()
    { var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); signal.SetResult(); return signal; }
    private sealed class BackgroundPause(TextureCatalogSession owner) : IDisposable
    { private TextureCatalogSession? owner = owner; public void Dispose() => Interlocked.Exchange(ref owner, null)?.ResumeBackgroundDiscovery(); }

    public TextureCatalogSession(ArchiveSnapshot snapshot, SourceRevision revision,
        LocalizationReferenceReader? referenceReader = null,
        TextureCatalogMembershipReader? membershipReader = null,
        TextureCatalogSessionOptions? sessionOptions = null,
        ICatalogReferenceProvider? referenceProvider = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(revision);
        if (!StringComparer.Ordinal.Equals(revision.IndexContentId, snapshot.IndexContentId) ||
            !StringComparer.Ordinal.Equals(revision.PhysicalSourceStamp, snapshot.PhysicalSourceStamp))
            throw new ArchiveException("InvalidTextureCatalogRevision", "The texture catalog revision does not belong to the selected archive snapshot.");

        options = sessionOptions ?? new TextureCatalogSessionOptions();
        ValidateOptions(options);
        Snapshot = snapshot;
        Revision = revision;
        if (referenceProvider is not null &&
            (!ReferenceEquals(referenceProvider.Snapshot, snapshot) || referenceProvider.Revision != revision))
            throw new ArgumentException("The catalog reference provider belongs to another archive revision.", nameof(referenceProvider));
        ownedReferenceProvider = referenceProvider is null
            ? new CatalogReferenceProvider(snapshot, new AssetMemoryBudget(), referenceReader: referenceReader)
            : null;
        this.referenceProvider = referenceProvider ?? ownedReferenceProvider!;
        memberships = membershipReader ?? new TextureCatalogMembershipReader();
        updateContext = SynchronizationContext.Current;
        catalogRows = snapshot.Records.Where(row => row.Type == 3 && row.Validity == ResourceValidity.Valid)
            .OrderBy(row => row.Ordinal).ToArray();
        catalogRowsByOrdinal = catalogRows.ToDictionary(row => row.Ordinal);
        catalogPositions = catalogRows.Select((row, index) => (row.Ordinal, index))
            .ToDictionary(item => item.Ordinal, item => item.index);
        ValidatePriorities(options.PriorityCatalogs);
        EnrichmentTask = Task.Run(EnrichAsync);
    }

    public ArchiveSnapshot Snapshot { get; }
    public SourceRevision Revision { get; }
    public ICatalogReferenceProvider ReferenceProvider => referenceProvider;
    public Task EnrichmentTask { get; }
    public TextureCatalogSessionProgress Progress { get { lock (stateGate) return CreateProgress(); } }
    public Exception? Error { get { lock (stateGate) return error; } }
    public IReadOnlyList<TextureCatalogGroup> Groups { get { lock (stateGate) return groupsByOrdinal.Values.OrderBy(group => group.CatalogIdentity.Ordinal).ToArray(); } }
    public IReadOnlyList<TextureCatalogSessionDiagnostic> Diagnostics { get { lock (stateGate) return diagnostics.ToArray(); } }
    public event EventHandler<TextureCatalogSessionUpdatedEventArgs>? Updated;

    /// <summary>Reads one selected catalog through the same serialized, bounded path as background discovery.</summary>
    public async Task<TextureCatalogGroup?> ReadCatalogAsync(ResourceIdentity catalogIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalogIdentity);
        ValidateCatalogIdentity(catalogIdentity);
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, cancellationToken);
        await readGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (TryGetGroup(catalogIdentity.Ordinal, out var existing)) return existing;
            if (IsAttempted(catalogIdentity.Ordinal)) return null;
            if (HasReachedCatalogLimit()) return null;
            var row = GetCatalogRow(catalogIdentity.Ordinal);
            return await ReadCatalogCoreAsync(row, linked.Token).ConfigureAwait(false);
        }
        catch (ArchiveException exception) when (exception.Code == "SourceChanged")
        {
            SetFatal(exception);
            throw;
        }
        finally { readGate.Release(); }
    }

    /// <summary>Returns groups whose uniquely resolved reference points to the selected texture identity.</summary>
    public IReadOnlyList<TextureCatalogGroup> FindGroupsForTexture(ResourceIdentity textureIdentity)
    {
        ArgumentNullException.ThrowIfNull(textureIdentity);
        if (!StringComparer.Ordinal.Equals(textureIdentity.IndexContentId, Snapshot.IndexContentId) || textureIdentity.MemberPath is not null)
            return Array.Empty<TextureCatalogGroup>();
        lock (stateGate)
        {
            if (!groupsByTexture.TryGetValue(textureIdentity, out var ordinals)) return Array.Empty<TextureCatalogGroup>();
            return ordinals.Select(ordinal => groupsByOrdinal[ordinal]).OrderBy(group => group.CatalogIdentity.Ordinal).ToArray();
        }
    }

    /// <summary>Finds catalog rows that reference a selected texture and verifies the source tables before returning them.</summary>
    public async Task<TextureCatalogRelationshipQuery> FindCatalogRelationshipsForTextureAsync(
        ResourceIdentity textureIdentity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(textureIdentity);
        if (!IsTextureIdentity(textureIdentity))
            throw new ArchiveException("InvalidTextureIdentity", "The selected texture identity does not belong to this archive snapshot.");
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, cancellationToken);
        await referenceProvider.ValidateSnapshotAsync(linked.Token).ConfigureAwait(false);
        await readGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            TextureCatalogGroup[] candidates;
            TextureCatalogSessionProgress discovery;
            TextureCatalogSessionDiagnostic[] discoveryDiagnostics;
            lock (stateGate)
            {
                candidates = groupsByTextureCandidate.TryGetValue(textureIdentity, out var ordinals)
                    ? ordinals.Select(ordinal => groupsByOrdinal[ordinal]).OrderBy(group => group.CatalogIdentity.Ordinal).ToArray()
                    : Array.Empty<TextureCatalogGroup>();
                discovery = CreateProgress();
                discoveryDiagnostics = diagnostics.ToArray();
            }

            var related = new List<TextureCatalogRelatedGroup>(candidates.Length);
            foreach (var group in candidates)
            {
                linked.Token.ThrowIfCancellationRequested();
                using var readLease = await referenceProvider.ReadAsync(group.CatalogIdentity, linked.Token).ConfigureAwait(false);
                var read = readLease.Result;
                if (read.Status != LocalizationReferenceReadStatus.Verified ||
                    !StringComparer.Ordinal.Equals(read.ReferenceTableSha256, group.ReferenceTableSha256))
                    throw new ArchiveException("SourceChanged", "A related catalog reference table changed after discovery.");
                await referenceProvider.RevalidateAsync(read, linked.Token).ConfigureAwait(false);
                var matches = group.References.Where(reference =>
                    (reference.ResolutionStatus == ReferenceStatus.Unique || reference.ResolutionStatus == ReferenceStatus.Ambiguous) &&
                    reference.Candidates.Contains(textureIdentity)).ToArray();
                if (matches.Length > 0) related.Add(new(group, matches));
            }
            return new(textureIdentity, Revision, discovery, discoveryDiagnostics, related.AsReadOnly());
        }
        catch (ArchiveException exception) when (exception.Code == "SourceChanged")
        {
            SetFatal(exception);
            throw;
        }
        finally { readGate.Release(); }
    }

    /// <summary>Revalidates the index and selected reference table against the table hash captured during discovery.</summary>
    public async Task RevalidateGroupAsync(ResourceIdentity catalogIdentity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalogIdentity);
        ValidateCatalogIdentity(catalogIdentity);
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, cancellationToken);
        await readGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!TryGetGroup(catalogIdentity.Ordinal, out var expected))
                throw new ArchiveException("TextureCatalogNotDiscovered", "The selected catalog has no discovered texture references.");

            await referenceProvider.ValidateSnapshotAsync(linked.Token).ConfigureAwait(false);
            using var readLease = await referenceProvider.ReadAsync(catalogIdentity, linked.Token).ConfigureAwait(false);
            var read = readLease.Result;
            if (read.Status != LocalizationReferenceReadStatus.Verified ||
                !StringComparer.Ordinal.Equals(read.ReferenceTableSha256, expected.ReferenceTableSha256))
                throw new ArchiveException("SourceChanged", "The selected catalog reference table changed after discovery.");
            await referenceProvider.RevalidateAsync(read, linked.Token).ConfigureAwait(false);
        }
        catch (ArchiveException exception) when (exception.Code == "SourceChanged")
        {
            SetFatal(exception);
            throw;
        }
        finally { readGate.Release(); }
    }

    public void Cancel()
    {
        if (!disposed)
        {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        cancellation.Cancel();
        try { await EnrichmentTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { }
        await readGate.WaitAsync().ConfigureAwait(false);
        readGate.Release();
        readGate.Dispose();
        cancellation.Dispose();
        if (ownedReferenceProvider is not null) await ownedReferenceProvider.DisposeAsync().ConfigureAwait(false);
        lock (stateGate)
        {
            if (state == TextureCatalogSessionState.Running) state = TextureCatalogSessionState.Cancelled;
        }
        PublishUpdate();
    }

    private async Task EnrichAsync()
    {
        try
        {
            await referenceProvider.ValidateSnapshotAsync(cancellation.Token).ConfigureAwait(false);
            PublishUpdate();

            var priorityRows = (options.PriorityCatalogs ?? Array.Empty<ResourceIdentity>())
                .Select(identity => GetCatalogRow(identity.Ordinal)).ToArray();
            var priorityOrdinals = priorityRows.Select(row => row.Ordinal).ToHashSet();
            var orderedRows = priorityRows.Concat(catalogRows.Where(row => !priorityOrdinals.Contains(row.Ordinal)));
            foreach (var row in orderedRows)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Task admission; lock (stateGate) admission = backgroundReady.Task;
                await admission.WaitAsync(cancellation.Token).ConfigureAwait(false);
                if (LimitReachedBefore(row)) break;
                await readGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try
                {
                    if (!IsAttempted(row.Ordinal)) await ReadCatalogCoreAsync(row, cancellation.Token).ConfigureAwait(false);
                }
                finally { readGate.Release(); }
                if (CurrentState != TextureCatalogSessionState.Running) break;
            }

            lock (stateGate)
            {
                if (state == TextureCatalogSessionState.Running)
                {
                    state = TextureCatalogSessionState.Completed;
                    message = discoveryIncomplete
                        ? "Catalog discovery finished with skipped catalogs."
                        : "Catalog discovery complete.";
                }
            }
            PublishUpdate();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            lock (stateGate)
            {
                if (state == TextureCatalogSessionState.Running)
                {
                    state = TextureCatalogSessionState.Cancelled;
                    message = "Catalog discovery stopped.";
                }
            }
            PublishUpdate();
            throw;
        }
        catch (ArchiveException exception) when (exception.Code == "SourceChanged")
        {
            SetFatal(exception);
            throw;
        }
        catch (Exception exception)
        {
            SetFatal(exception);
            throw;
        }
    }

    private async Task<TextureCatalogGroup?> ReadCatalogCoreAsync(ResourceRecord row, CancellationToken token)
    {
        if (IsAttempted(row.Ordinal)) return TryGetGroup(row.Ordinal, out var existing) ? existing : null;
        if (row.StoredSize > options.MaximumCatalogResourceBytes)
        {
            MarkAttempted(row.Ordinal, 0, "CatalogResourceLimit", $"The catalog exceeds the configured {options.MaximumCatalogResourceBytes}-byte resource limit.", incomplete: true);
            return null;
        }

        var identity = new ResourceIdentity(Snapshot.IndexContentId, row.Ordinal);
        try
        {
            using var readLease = await referenceProvider.ReadAsync(identity, token).ConfigureAwait(false);
            var read = readLease.Result;
            if (read.Status != LocalizationReferenceReadStatus.Verified)
            {
                MarkAttempted(row.Ordinal, 0, "UnsupportedCatalog", "The type-3 resource is not a supported COM_CONT v6 reference table.", incomplete: true);
                return null;
            }
            if (read.Relationships.Count > options.MaximumReferencesPerCatalog)
            {
                MarkAttempted(row.Ordinal, 0, "CatalogReferenceLimit", $"The catalog has more than the configured {options.MaximumReferencesPerCatalog} reference rows.", incomplete: true);
                return null;
            }

            var exceedsReferenceLimit = false;
            lock (stateGate)
            {
                if (referenceRowsRead + read.Relationships.Count > options.MaximumReferenceRows)
                {
                    state = TextureCatalogSessionState.StoppedAtLimit;
                    discoveryIncomplete = true;
                    message = $"Catalog discovery stopped at the configured {options.MaximumReferenceRows}-row limit.";
                    diagnostics.Add(new(identity, "ReferenceRowLimit", message));
                    attemptedCatalogs.Add(row.Ordinal);
                    catalogsVisited++;
                    exceedsReferenceLimit = true;
                }
            }
            if (exceedsReferenceLimit)
            {
                PublishUpdate();
                return null;
            }

            var membership = memberships.Read(Snapshot, identity, read.Revision, read.Relationships);
            var group = BuildGroup(row, membership, read.ReferenceTableSha256!);
            lock (stateGate)
            {
                attemptedCatalogs.Add(row.Ordinal);
                catalogsVisited++;
                referenceRowsRead += read.Relationships.Count;
                if (group is not null)
                {
                    groupsByOrdinal[row.Ordinal] = group;
                    textureCatalogsFound++;
                    foreach (var member in group.UniqueTextureMembers)
                    {
                        if (!groupsByTexture.TryGetValue(member, out var ordinals)) groupsByTexture.Add(member, ordinals = []);
                        ordinals.Add(row.Ordinal);
                    }
                    foreach (var candidate in group.References
                        .Where(reference => reference.ResolutionStatus is ReferenceStatus.Unique or ReferenceStatus.Ambiguous)
                        .SelectMany(reference => reference.Candidates).Distinct())
                    {
                        if (!groupsByTextureCandidate.TryGetValue(candidate, out var ordinals))
                            groupsByTextureCandidate.Add(candidate, ordinals = []);
                        ordinals.Add(row.Ordinal);
                    }
                }
            }
            PublishUpdate();
            return group;
        }
        catch (OperationCanceledException) { throw; }
        catch (ArchiveException exception) when (exception.Code == "SourceChanged") { throw; }
        catch (Exception exception)
        {
            MarkAttempted(row.Ordinal, 0, exception is ArchiveException archive ? archive.Code : "CatalogReadFailed",
                exception.Message, incomplete: true);
            return null;
        }
    }

    private TextureCatalogGroup? BuildGroup(ResourceRecord row, TextureCatalogMembership membership, string tableSha256)
    {
        if (membership.TextureReferences.Count == 0) return null;
        var uniqueMembers = membership.TextureReferences
            .Where(reference => reference.ResolutionStatus == ReferenceStatus.Unique && reference.Candidates.Count == 1)
            .Select(reference => reference.Candidates[0]).Distinct().OrderBy(identity => identity.Ordinal).ToArray();
        var position = catalogPositions[row.Ordinal];
        var groupNumber = position + 1;
        return new TextureCatalogGroup($"Catalog {groupNumber}", membership.CatalogIdentity, membership.CatalogId,
            membership.CatalogExtent, tableSha256, membership.SourceReferenceCount, membership.TextureReferences,
            uniqueMembers,
            membership.TextureReferences.Count(reference => reference.ResolutionStatus == ReferenceStatus.Missing),
            membership.TextureReferences.Count(reference => reference.ResolutionStatus == ReferenceStatus.Ambiguous),
            membership.TextureReferences.Count(reference => reference.ResolutionStatus == ReferenceStatus.Unresolved),
            membership.TextureReferences.Count(reference => reference.IsDuplicate));
    }

    private bool LimitReachedBefore(ResourceRecord next)
    {
        lock (stateGate)
        {
            if (catalogsVisited < options.MaximumCatalogs) return false;
            if (attemptedCatalogs.Contains(next.Ordinal)) return false;
            state = TextureCatalogSessionState.StoppedAtLimit;
            discoveryIncomplete = true;
            message = $"Catalog discovery stopped at the configured {options.MaximumCatalogs}-catalog limit.";
            diagnostics.Add(new(null, "CatalogLimit", message));
        }
        PublishUpdate();
        return true;
    }

    private bool HasReachedCatalogLimit()
    {
        lock (stateGate)
        {
            if (state != TextureCatalogSessionState.Running) return true;
            if (catalogsVisited < options.MaximumCatalogs) return false;
            state = TextureCatalogSessionState.StoppedAtLimit;
            discoveryIncomplete = true;
            message = $"Catalog discovery stopped at the configured {options.MaximumCatalogs}-catalog limit.";
            diagnostics.Add(new(null, "CatalogLimit", message));
        }
        PublishUpdate();
        return true;
    }

    private void MarkAttempted(long ordinal, int referenceCount, string? code, string? detail, bool incomplete)
    {
        lock (stateGate)
        {
            if (attemptedCatalogs.Add(ordinal)) catalogsVisited++;
            referenceRowsRead += referenceCount;
            if (code is not null && detail is not null)
                diagnostics.Add(new(new ResourceIdentity(Snapshot.IndexContentId, ordinal), code, detail));
            if (incomplete) discoveryIncomplete = true;
        }
        PublishUpdate();
    }

    private void SetFatal(Exception exception)
    {
        lock (stateGate)
        {
            if (state == TextureCatalogSessionState.Failed) return;
            error = exception;
            state = TextureCatalogSessionState.Failed;
            discoveryIncomplete = true;
            message = exception.Message;
            diagnostics.Add(new(null, exception is ArchiveException archive ? archive.Code : "CatalogDiscoveryFailed", exception.Message));
        }
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        PublishUpdate();
    }

    private bool TryGetGroup(long ordinal, out TextureCatalogGroup group)
    {
        lock (stateGate) return groupsByOrdinal.TryGetValue(ordinal, out group!);
    }

    private bool IsAttempted(long ordinal)
    {
        lock (stateGate) return attemptedCatalogs.Contains(ordinal);
    }

    private ResourceRecord GetCatalogRow(long ordinal) => catalogRowsByOrdinal.TryGetValue(ordinal, out var row) ? row
        : throw new ArchiveException("InvalidTextureCatalogPriority", "The selected catalog is not a valid type-3 resource in this archive snapshot.");

    private void ValidateCatalogIdentity(ResourceIdentity identity)
    {
        if (!StringComparer.Ordinal.Equals(identity.IndexContentId, Snapshot.IndexContentId) || identity.MemberPath is not null)
            throw new ArchiveException("InvalidTextureCatalog", "The selected catalog identity does not belong to the session snapshot.");
        var row = GetCatalogRow(identity.Ordinal);
        if (row.Type != 3) throw new ArchiveException("InvalidTextureCatalog", "Texture references can only be read from type-3 catalogs.");
    }

    private bool IsTextureIdentity(ResourceIdentity identity)
    {
        if (!StringComparer.Ordinal.Equals(identity.IndexContentId, Snapshot.IndexContentId) || identity.MemberPath is not null)
            return false;
        ResourceRecord? row = null;
        if (identity.Ordinal >= 0 && identity.Ordinal < Snapshot.Records.Count)
        {
            var candidate = Snapshot.Records[checked((int)identity.Ordinal)];
            if (candidate.Ordinal == identity.Ordinal) row = candidate;
        }
        row ??= Snapshot.Records.SingleOrDefault(candidate => candidate.Ordinal == identity.Ordinal);
        return row is { Validity: ResourceValidity.Valid, Type: 2229 or 2230 };
    }

    private void ValidatePriorities(IReadOnlyList<ResourceIdentity>? priorities)
    {
        if (priorities is null) return;
        foreach (var identity in priorities) ValidateCatalogIdentity(identity);
    }

    private static void ValidateOptions(TextureCatalogSessionOptions value)
    {
        if (value.MaximumCatalogs <= 0 || value.MaximumCatalogs > 100_000)
            throw new ArgumentOutOfRangeException(nameof(value.MaximumCatalogs));
        if (value.MaximumReferenceRows <= 0 || value.MaximumReferenceRows > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(value.MaximumReferenceRows));
        if (value.MaximumCatalogResourceBytes is < 20 or > LocalizationReferenceReader.MaximumResourceBytes)
            throw new ArgumentOutOfRangeException(nameof(value.MaximumCatalogResourceBytes));
        if (value.MaximumReferencesPerCatalog <= 0 || value.MaximumReferencesPerCatalog > LocalizationReferenceReader.MaximumReferenceCount)
            throw new ArgumentOutOfRangeException(nameof(value.MaximumReferencesPerCatalog));
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(TextureCatalogSession));
    }

    private TextureCatalogSessionState CurrentState { get { lock (stateGate) return state; } }

    private TextureCatalogSessionProgress CreateProgress() => new(state, catalogRows.Length, catalogsVisited,
        textureCatalogsFound, referenceRowsRead, catalogRows.Length, discoveryIncomplete, message);

    private void PublishUpdate()
    {
        TextureCatalogSessionProgress progress;
        lock (stateGate) progress = CreateProgress();
        PublishUpdate(progress);
    }

    private void PublishUpdate(TextureCatalogSessionProgress progress)
    {
        var handler = Updated;
        if (handler is null) return;
        void Raise() => handler(this, new TextureCatalogSessionUpdatedEventArgs(progress));
        if (updateContext is not null && SynchronizationContext.Current != updateContext)
            updateContext.Post(_ => Raise(), null);
        else
            Raise();
    }
}
