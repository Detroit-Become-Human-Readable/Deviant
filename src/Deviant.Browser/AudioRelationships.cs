using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Audio;
using Deviant.Formats.Audio.Model;
using AudioEvent = Deviant.Formats.Audio.Model.AudioEvent;
using AudioMedia = Deviant.Formats.Audio.Model.MediaEntry;
using ResourceReference = Deviant.Assets.ResourceReference;

namespace Deviant.Browser;

public sealed record AudioRelationshipReport(
    ResourceIdentity BankDescriptor,
    IReadOnlyList<AssetRelationship> Relationships,
    IReadOnlyList<string> Diagnostics);

/// <summary>Builds navigation links from parsed resource ownership and Wwise references.</summary>
public sealed class AudioRelationships
{
    private const int MaximumRelatedBanks = 32;
    private const int MaximumCachedBankGraphs = 64;
    private const int MaximumConcurrentBankGraphReads = 4;
    private const long MaximumIndexHashBytes = 128L * 1024 * 1024;
    private const long MaximumBankHashBytes = 256L * 1024 * 1024;
    private const long MaximumCachedBankGraphBytes = 512L * 1024 * 1024;
    private readonly ArchiveSnapshot snapshot;
    private readonly AudioCatalog catalog;
    private readonly DetroitAudioSnapshotReader reader;
    private readonly Func<ArchiveSnapshot, ResourceIdentity, CancellationToken, Task<BankInfo>> readBankGraph;
    private readonly ISourceRevisionProvider revisions;
    private readonly AudioMetadataVerifier metadataVerifier;
    private readonly object bankGraphCacheGate = new();
    private readonly Dictionary<BankGraphCacheKey, BankGraphCacheEntry> bankGraphCache = [];
    private readonly SemaphoreSlim bankGraphReadGate = new(MaximumConcurrentBankGraphReads);
    private readonly SemaphoreSlim bankRevisionVerificationGate = new(2);
    private long bankGraphUseClock;
    private long bankGraphCachePayloadBytes;
    private int sourceRevisionInvalidated;

    public AudioRelationships(ArchiveSnapshot snapshot, AudioCatalog catalog, DetroitAudioSnapshotReader? reader = null,
        Func<ArchiveSnapshot, ResourceIdentity, CancellationToken, Task<BankInfo>>? bankGraphLoader = null,
        ISourceRevisionProvider? revisionProvider = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.IndexContentId != snapshot.IndexContentId || catalog.PhysicalSourceStamp != snapshot.PhysicalSourceStamp)
            throw new ArchiveException("SnapshotMismatch", "Audio relationships require catalog metadata from the selected archive revision.");
        this.snapshot = snapshot;
        this.catalog = catalog;
        this.reader = reader ?? new DetroitAudioSnapshotReader();
        readBankGraph = bankGraphLoader ?? this.reader.ReadBankGraphAsync;
        revisions = revisionProvider ?? new FileRevisionProvider();
        metadataVerifier = new AudioMetadataVerifier(snapshot, revisions);
    }

    public async Task<AudioRelationshipReport> ResolveBankAsync(ResourceIdentity bankDescriptorIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bankDescriptorIdentity);
        ThrowIfSourceRevisionInvalidated();
        ValidateTopLevelIdentity(bankDescriptorIdentity, expectedType: 1022);
        var bankMetadata = catalog.Banks.Where(bank => bank.Identity?.Key == bankDescriptorIdentity.Key).ToArray();
        if (bankMetadata.Length != 1)
            throw new ArchiveException(bankMetadata.Length == 0 ? "MissingBankDescriptor" : "AmbiguousBankDescriptor",
                "The bank descriptor does not resolve to exactly one audio bank in this catalog.");

        var relationships = new List<AssetRelationship>();
        var diagnostics = new List<string>();
        var bank = bankMetadata[0];
        await VerifyBankMetadataBaselineAsync(bank, cancellationToken).ConfigureAwait(false);
        AddContainerRelationship(bank, relationships);
        var cacheAllowed = snapshot.IndexStamp.Length is >= 0 and <= MaximumIndexHashBytes;
        var indexHash = cacheAllowed ? await VerifyIndexContentAsync(cancellationToken).ConfigureAwait(false) : null;
        BankInfo bankGraph;
        try { bankGraph = await ReadBankGraphCachedAsync(bankDescriptorIdentity, cacheAllowed, cancellationToken).ConfigureAwait(false); }
        catch (ArchiveException ex) when (ex.Code is "MissingBankContainer" or "AmbiguousBankContainer" or "MalformedBankDescriptor" or "MalformedBankPayload" or "SourceUnavailable")
        {
            if (indexHash is not null && !StringComparer.Ordinal.Equals(indexHash, await VerifyIndexContentAsync(cancellationToken).ConfigureAwait(false)))
                throw new ArchiveException("SourceChanged", "The archive index changed while bank references were being read.");
            diagnostics.Add($"Bank 0x{bank.Id:X8}: {ex.Code}: {ex.Message}");
            return new(bankDescriptorIdentity, relationships.AsReadOnly(), diagnostics.AsReadOnly());
        }
        cancellationToken.ThrowIfCancellationRequested();

        var loadedBanks = new Dictionary<string, BankInfo>(StringComparer.Ordinal) { [bankGraph.Key] = bankGraph };
        var unavailableBankIds = new Dictionary<uint, (ReferenceStatus Status, string? Reason)>();
        await LoadExplicitTargetBanksAsync(bankGraph, loadedBanks, unavailableBankIds, diagnostics, cacheAllowed, cancellationToken).ConfigureAwait(false);
        var graphCatalog = new AudioCatalog { Banks = loadedBanks.Values.ToList() };
        var graph = new CatalogGraph(graphCatalog);

        foreach (var loadedBank in loadedBanks.Values.OrderBy(bank => bank.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddDeclaredMediaRelationships(loadedBank, relationships);
            AddAmbiguousObjectRelationships(loadedBank, relationships);
        }
        AddEventRelationships(bankGraph, graph, loadedBanks, unavailableBankIds, relationships, diagnostics, cancellationToken);
        foreach (var relatedBank in loadedBanks.Values)
            await VerifyBankMetadataBaselineAsync(relatedBank, cancellationToken).ConfigureAwait(false);
        foreach (var media in catalog.Media.Where(item => loadedBanks.ContainsKey(item.BankKey) &&
                     AudioDiscovery.IsCurrentMediaIdentity(snapshot, catalog, item)))
            await VerifyMediaBaselineAsync(media, cancellationToken).ConfigureAwait(false);
        if (indexHash is not null && !string.Equals(indexHash, await VerifyIndexContentAsync(cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
            throw new ArchiveException("SourceChanged", "The archive index changed during audio relationship resolution.");

        return new(bankDescriptorIdentity, relationships.AsReadOnly(), diagnostics.AsReadOnly());
    }

    /// <summary>Verifies the exact metadata bytes used to classify a media row before it is consumed.</summary>
    public async Task VerifyMediaBaselineAsync(AudioMedia media, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        ThrowIfSourceRevisionInvalidated();
        if (!AudioDiscovery.IsCurrentMediaIdentity(snapshot, catalog, media))
            throw new ArchiveException("InvalidIdentity", "The selected media row does not belong to this archive revision.");
        try { await metadataVerifier.VerifyMediaAsync(media, cancellationToken).ConfigureAwait(false); }
        catch (ArchiveException ex) when (ex.Code == "SourceChanged")
        { InvalidateSourceRevision(); ClearBankGraphCache(); throw; }
    }

    private async Task VerifyBankMetadataBaselineAsync(BankInfo bank, CancellationToken token)
    {
        try { await metadataVerifier.VerifyBankAsync(bank, token).ConfigureAwait(false); }
        catch (ArchiveException ex) when (ex.Code == "SourceChanged")
        { InvalidateSourceRevision(); ClearBankGraphCache(); throw; }
    }

    /// <summary>Clears parsed bank graphs when the owning archive session is replaced.</summary>
    public void ClearBankGraphCache()
    {
        BankGraphCacheEntry[] entries;
        lock (bankGraphCacheGate)
        {
            entries = bankGraphCache.Values.Where(entry => !entry.Completed).ToArray();
            foreach (var entry in entries) entry.CancelRequested = true;
            bankGraphCache.Clear();
            bankGraphCachePayloadBytes = 0;
        }
        foreach (var entry in entries) CancelLoad(entry);
    }

    private async Task<BankInfo> ReadBankGraphCachedAsync(ResourceIdentity identity, bool cacheAllowed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!cacheAllowed) return await ReadBankGraphBoundedAsync(identity, token).ConfigureAwait(false);
        var bankMetadata = catalog.Banks.Where(bank => bank.Identity?.Key == identity.Key).ToArray();
        if (bankMetadata.Length != 1 || bankMetadata[0].Slice.Length is <= 0 or > MaximumBankHashBytes)
            return await ReadBankGraphBoundedAsync(identity, token).ConfigureAwait(false);

        var key = new BankGraphCacheKey(snapshot.IndexContentId, snapshot.PhysicalSourceStamp,
            Path.GetFullPath(snapshot.IndexPath), identity.Ordinal);
        BankGraphCacheEntry? entry = null;
        var startLoad = false;
        var bypassCache = false;
        var verifyAfterWait = false;
        lock (bankGraphCacheGate)
        {
            if (bankGraphCache.TryGetValue(key, out entry) && entry.CancelRequested)
            {
                RemoveBankGraphCacheEntryUnsafe(key, entry);
                entry = null;
            }
            if (entry is null)
            {
                if (!MakeBankGraphCacheRoomUnsafe(bankMetadata[0].Slice.Length)) bypassCache = true;
                else
                {
                    entry = new BankGraphCacheEntry
                    { LastUse = ++bankGraphUseClock, Waiters = 1, ReservedBytes = bankMetadata[0].Slice.Length };
                    bankGraphCache.Add(key, entry);
                    bankGraphCachePayloadBytes += entry.ReservedBytes;
                    startLoad = true;
                }
            }
            else
            {
                entry.Waiters++;
                entry.LastUse = ++bankGraphUseClock;
                verifyAfterWait = entry.Completed;
                if (entry.Graph is not null && !SameBankSlice(entry.Graph.Slice, bankMetadata[0].Slice))
                    throw new ArchiveException("SourceChanged", "Cached and current bank payload extents differ for the selected archive revision.");
            }
        }

        if (bypassCache) return await LoadBankGraphWithContentCheckAsync(identity, bankMetadata[0].Slice, token).ConfigureAwait(false);
        var current = entry!;
        if (startLoad) _ = PopulateBankGraphCacheAsync(key, current, identity, bankMetadata[0].Slice);
        try
        {
            var graph = await current.Completion.Task.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (verifyAfterWait) await VerifyCachedBankGraphAsync(current, bankMetadata[0].Slice, token).ConfigureAwait(false);
            return graph;
        }
        finally { ReleaseBankGraphWaiter(current); }
    }

    private async Task PopulateBankGraphCacheAsync(BankGraphCacheKey key, BankGraphCacheEntry entry,
        ResourceIdentity identity, DataSlice expectedSlice)
    {
        var gateHeld = false;
        try
        {
            await bankGraphReadGate.WaitAsync(entry.LoadCancellation.Token).ConfigureAwait(false);
            gateHeld = true;
            var expectedHash = await HashBankSliceAsync(expectedSlice, entry.LoadCancellation.Token).ConfigureAwait(false);
            var graph = await readBankGraph(snapshot, identity, entry.LoadCancellation.Token).ConfigureAwait(false);
            if (!SameBankSlice(expectedSlice, graph.Slice))
                throw new ArchiveException("SourceChanged", "The parsed bank payload extent changed from the selected catalog snapshot.");
            var payloadHash = await HashBankSliceAsync(graph.Slice, entry.LoadCancellation.Token).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(expectedHash, payloadHash))
                throw new ArchiveException("SourceChanged", "The bank payload changed while its graph was being read.");
            lock (bankGraphCacheGate)
            {
                entry.Graph = graph;
                entry.PayloadSha256 = payloadHash;
                entry.Completed = true;
                entry.LastUse = ++bankGraphUseClock;
            }
            entry.Completion.TrySetResult(graph);
        }
        catch (OperationCanceledException ex)
        {
            RemoveBankGraphCacheEntry(key, entry);
            entry.Completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            if (ex is ArchiveException { Code: "SourceChanged" }) InvalidateSourceRevision();
            RemoveBankGraphCacheEntry(key, entry);
            entry.Completion.TrySetException(ex);
            _ = entry.Completion.Task.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        finally
        {
            if (gateHeld) bankGraphReadGate.Release();
            entry.LoadCancellation.Dispose();
        }
    }

    private async Task<BankInfo> LoadBankGraphWithContentCheckAsync(ResourceIdentity identity, DataSlice expectedSlice,
        CancellationToken token)
    {
        await bankGraphReadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            try { return await LoadBankGraphAndCheckSliceAsync(identity, expectedSlice, token).ConfigureAwait(false); }
            catch (ArchiveException ex) when (ex.Code == "SourceChanged")
            { InvalidateSourceRevision(); throw; }
        }
        finally { bankGraphReadGate.Release(); }
    }

    private async Task<BankInfo> ReadBankGraphBoundedAsync(ResourceIdentity identity, CancellationToken token)
    {
        await bankGraphReadGate.WaitAsync(token).ConfigureAwait(false);
        try { return await readBankGraph(snapshot, identity, token).ConfigureAwait(false); }
        catch (ArchiveException ex) when (ex.Code == "SourceChanged")
        { InvalidateSourceRevision(); throw; }
        finally { bankGraphReadGate.Release(); }
    }

    private async Task<BankInfo> LoadBankGraphAndCheckSliceAsync(ResourceIdentity identity, DataSlice expectedSlice,
        CancellationToken token)
    {
        var expectedHash = await HashBankSliceAsync(expectedSlice, token).ConfigureAwait(false);
        var graph = await readBankGraph(snapshot, identity, token).ConfigureAwait(false);
        if (!SameBankSlice(expectedSlice, graph.Slice))
            throw new ArchiveException("SourceChanged", "The parsed bank payload extent changed from the selected catalog snapshot.");
        var payloadHash = await HashBankSliceAsync(graph.Slice, token).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(expectedHash, payloadHash))
            throw new ArchiveException("SourceChanged", "The bank payload changed while its graph was being read.");
        return graph;
    }

    private async Task VerifyCachedBankGraphAsync(BankGraphCacheEntry entry, DataSlice expectedSlice, CancellationToken token)
    {
        if (entry.Graph is null || entry.PayloadSha256 is null || !SameBankSlice(entry.Graph.Slice, expectedSlice))
            throw new ArchiveException("SourceChanged", "The cached bank graph does not match the current catalog extent.");
        try
        {
            var currentHash = await HashBankSliceAsync(entry.Graph.Slice, token).ConfigureAwait(false);
            if (StringComparer.Ordinal.Equals(currentHash, entry.PayloadSha256)) return;
            throw new ArchiveException("SourceChanged", "The bank payload hash changed after its graph was cached.");
        }
        catch (ArchiveException ex) when (ex.Code == "SourceChanged")
        { InvalidateSourceRevision(); ClearBankGraphCache(); throw; }
    }

    private async Task<string> VerifyIndexContentAsync(CancellationToken token)
    {
        await bankRevisionVerificationGate.WaitAsync(token).ConfigureAwait(false);
        try { return await VerifyIndexContentCoreAsync(token).ConfigureAwait(false); }
        catch (ArchiveException ex) when (ex.Code == "SourceChanged")
        { InvalidateSourceRevision(); throw; }
        finally { bankRevisionVerificationGate.Release(); }
    }

    private async Task<string> VerifyIndexContentCoreAsync(CancellationToken token)
    {
        if (snapshot.IndexStamp.Length < 0 || snapshot.IndexStamp.Length > MaximumIndexHashBytes)
            throw new ArchiveException("IndexVerificationLimit", "The archive index exceeds the bounded relationship revision check.");
        using var handle = SourceIdentity.OpenPinned(snapshot.IndexPath);
        SourceIdentity.EnsureUnchanged(revisions, handle, snapshot.IndexPath, snapshot.IndexStamp);
        var hash = await SourceIdentity.HashAsync(handle, snapshot.IndexStamp.Length, token, phase: "Verifying audio relationships").ConfigureAwait(false);
        SourceIdentity.EnsureUnchanged(revisions, handle, snapshot.IndexPath, snapshot.IndexStamp);
        if (!StringComparer.Ordinal.Equals(hash, snapshot.IndexContentId))
            throw new ArchiveException("SourceChanged", "The archive index content differs from the selected audio revision.");
        return hash;
    }

    private async Task<string> HashBankSliceAsync(DataSlice slice, CancellationToken token)
    {
        await bankRevisionVerificationGate.WaitAsync(token).ConfigureAwait(false);
        try { return await HashBankSliceCoreAsync(slice, token).ConfigureAwait(false); }
        catch (ArchiveException ex) when (ex.Code == "SourceChanged")
        { InvalidateSourceRevision(); throw; }
        finally { bankRevisionVerificationGate.Release(); }
    }

    private async Task<string> HashBankSliceCoreAsync(DataSlice slice, CancellationToken token)
    {
        if (slice.Offset < 0 || slice.Length <= 0 || slice.Length > MaximumBankHashBytes)
            throw new ArchiveException("BankPayloadVerificationLimit", "The bank payload is outside the bounded cache verification size.");
        var volumes = snapshot.Volumes.Where(volume => PathsEqual(volume.FilePath, slice.FilePath)).Take(2).ToArray();
        if (volumes.Length != 1 || !volumes[0].Stamp.Exists || volumes[0].Stamp.Error is not null)
            throw new ArchiveException("SourceUnavailable", "The bank payload source volume is unavailable in this archive snapshot.");
        var volume = volumes[0];
        using var handle = SourceIdentity.OpenPinned(volume.FilePath);
        SourceIdentity.EnsureUnchanged(revisions, handle, volume.FilePath, volume.Stamp);
        if (!IndexLayout.InBounds(slice.Offset, slice.Length, RandomAccess.GetLength(handle)))
            throw new ArchiveException("InvalidRange", "The bank payload exceeds its bounded source volume.");
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long readTotal = 0;
        while (readTotal < slice.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, (int)Math.Min(buffer.Length, slice.Length - readTotal)),
                checked(slice.Offset + readTotal), token).ConfigureAwait(false);
            if (read == 0) throw new ArchiveException("SourceChanged", "The bank payload ended while it was being hashed.");
            hash.AppendData(buffer.AsSpan(0, read));
            readTotal += read;
        }
        SourceIdentity.EnsureUnchanged(revisions, handle, volume.FilePath, volume.Stamp);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private void ReleaseBankGraphWaiter(BankGraphCacheEntry entry)
    {
        var cancel = false;
        lock (bankGraphCacheGate)
        {
            if (entry.Waiters > 0) entry.Waiters--;
            if (entry.Waiters == 0 && !entry.Completed && !entry.CancelRequested)
            { entry.CancelRequested = true; cancel = true; }
        }
        if (cancel) CancelLoad(entry);
    }

    private void RemoveBankGraphCacheEntry(BankGraphCacheKey key, BankGraphCacheEntry entry)
    {
        lock (bankGraphCacheGate)
        {
            entry.Completed = true;
            RemoveBankGraphCacheEntryUnsafe(key, entry);
        }
    }

    private void RemoveBankGraphCacheEntryUnsafe(BankGraphCacheKey key, BankGraphCacheEntry entry)
    {
        if (!bankGraphCache.TryGetValue(key, out var current) || !ReferenceEquals(current, entry)) return;
        bankGraphCache.Remove(key);
        bankGraphCachePayloadBytes -= entry.ReservedBytes;
        entry.ReservedBytes = 0;
    }

    private bool MakeBankGraphCacheRoomUnsafe(long additionalBytes)
    {
        while (bankGraphCache.Count >= MaximumCachedBankGraphs ||
               bankGraphCachePayloadBytes > MaximumCachedBankGraphBytes - additionalBytes)
        {
            BankGraphCacheKey? oldestKey = null;
            BankGraphCacheEntry? oldestEntry = null;
            var oldestUse = long.MaxValue;
            foreach (var pair in bankGraphCache)
                if (pair.Value.Completed && pair.Value.LastUse < oldestUse)
                { oldestKey = pair.Key; oldestEntry = pair.Value; oldestUse = pair.Value.LastUse; }
            if (oldestKey is not { } key || oldestEntry is null) return false;
            RemoveBankGraphCacheEntryUnsafe(key, oldestEntry);
        }
        return true;
    }

    private static void CancelLoad(BankGraphCacheEntry entry)
    {
        try { entry.LoadCancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static bool SameBankSlice(DataSlice left, DataSlice right) =>
        PathsEqual(left.FilePath, right.FilePath) && left.Offset == right.Offset && left.Length == right.Length;

    private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public async Task<IReadOnlyList<AssetRelationship>> ResolveForMediaAsync(ResourceIdentity mediaIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaIdentity);
        ThrowIfSourceRevisionInvalidated();
        if (mediaIdentity.IndexContentId != snapshot.IndexContentId)
            throw new ArchiveException("InvalidIdentity", "The selected media identity belongs to another archive revision.");
        var matches = catalog.Media.Where(media => media.Identity?.Key == mediaIdentity.Key).ToArray();
        if (matches.Length == 0) throw new ArchiveException("MissingMedia", "The selected media identity is not in this audio catalog.");
        if (matches.Length > 1) throw new ArchiveException("AmbiguousMedia", "The selected identity appears more than once in this audio catalog.");

        var media = matches[0];
        if (!AudioDiscovery.IsCurrentMediaIdentity(snapshot, catalog, media))
            throw new ArchiveException("StaleMediaIdentity", "The selected media row does not belong to the current archive resource owner.");
        var result = new List<AssetRelationship>();
        AddDialogueEventRelationships(media, result);
        AddPayloadOwnership(media, result);
        var banks = catalog.Banks.Where(bank => bank.Key == media.BankKey).ToArray();
        if (banks.Length == 1 && IsTopLevelIdentity(banks[0].Identity, 1022) && banks[0].Identity is { } bankIdentity)
        {
            result.Add(CreateRelationship(mediaIdentity, AssetRelationshipKind.Member, "Bank",
                new ResourceReference(IdentifierNamespace.Bank, banks[0].Id.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), mediaIdentity, 1022),
                new(ReferenceStatus.Unique, [bankIdentity]), verified: true, source: "Audio media bank ownership",
                description: "The audio media row belongs to the bank identified by its parsed BankKey."));
            var report = await ResolveBankAsync(bankIdentity, cancellationToken).ConfigureAwait(false);
            result.AddRange(report.Relationships.Where(link => link.Kind == AssetRelationshipKind.Container));
            result.AddRange(report.Relationships.Where(link => link.NavigationTarget?.Key == mediaIdentity.Key ||
                link.Resolution.Candidates.Any(target => target.Key == mediaIdentity.Key) || link.Source.Key == mediaIdentity.Key));
        }
        else if (media.BankKey.Length > 0)
        {
            result.Add(CreateRelationship(mediaIdentity, AssetRelationshipKind.Member, "Owning bank",
                new ResourceReference(IdentifierNamespace.Bank, media.BankKey, mediaIdentity),
                ResolveBankCandidates(banks), verified: banks.Length > 0,
                source: "Audio media ownership", description: "MediaEntry.BankKey did not identify a unique bank descriptor."));
        }
        return result.Distinct().ToArray();
    }

    private void AddPayloadOwnership(AudioMedia media, List<AssetRelationship> output)
    {
        if (media.Identity is null) return;
        try
        {
            var binding = AudioResourceBinding.Resolve(snapshot, media, original: true);
            var owner = binding.PayloadOwner;
            output.Add(CreateRelationship(media.Identity, AssetRelationshipKind.Member, "Payload owner",
                new ResourceReference(IdentifierNamespace.Archive, owner.Id.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), media.Identity, owner.Type),
                new(ReferenceStatus.Unique, [binding.PayloadIdentity]), verified: true, source: "Resolved audio payload binding",
                description: $"The audio payload resolves to archive resource {owner.Ordinal} in package {owner.Package}.",
                physicalExtent: new SourceExtent(binding.Volume.RelativeName, binding.Extent.Offset, binding.Extent.Length),
                decodedExtent: binding.PayloadIdentity.MemberPath is null ? null :
                    new DecodedExtent(binding.PayloadIdentity with { MemberPath = null }, binding.Extent.Offset - owner.Offset, binding.Extent.Length)));
        }
        catch (ArchiveException ex)
        {
            output.Add(CreateRelationship(media.Identity, AssetRelationshipKind.Member, "Payload owner",
                new ResourceReference(IdentifierNamespace.Archive, media.PayloadIdentity?.Key ?? media.Identity.Key, media.Identity),
                new(ReferenceStatus.Unresolved, [], ex.Code), verified: false, source: "Audio payload binding",
                description: ex.Message));
        }
    }

    private void AddContainerRelationship(BankInfo bank, List<AssetRelationship> output)
    {
        var descriptorIdentity = bank.Identity;
        if (descriptorIdentity is null) return;
        var diagnostics = bank.Diagnostics.Where(item => item.Code is "MissingBankContainer" or "AmbiguousBankContainer")
            .Select(item => item.Message).Distinct(StringComparer.Ordinal).ToArray();
        if (diagnostics.Length > 0)
        {
            var status = bank.Diagnostics.Any(item => item.Code == "AmbiguousBankContainer") ? ReferenceStatus.Ambiguous : ReferenceStatus.Missing;
            var explanation = string.Join(" ", diagnostics);
            output.Add(CreateRelationship(descriptorIdentity, AssetRelationshipKind.Container, "Bank container",
                new ResourceReference(IdentifierNamespace.Archive, "type-29-container", descriptorIdentity, 29),
                new(status, [], explanation), verified: true, source: "Type-1022 bank descriptor",
                description: explanation));
            return;
        }

        if (bank.ContainerIdentity is not { } containerIdentity ||
            containerIdentity.IndexContentId != snapshot.IndexContentId || containerIdentity.Ordinal < 0 ||
            containerIdentity.Ordinal >= snapshot.Records.Count || containerIdentity.MemberPath != "bank")
        {
            output.Add(CreateRelationship(descriptorIdentity, AssetRelationshipKind.Container, "Bank container",
                new ResourceReference(IdentifierNamespace.Archive, "type-29-container", descriptorIdentity, 29),
                new(ReferenceStatus.Missing, [], "No valid container identity was retained for this bank."), verified: false,
                source: "Audio bank metadata", description: "No navigable container identity was retained."));
            return;
        }

        var row = snapshot.Records[(int)containerIdentity.Ordinal];
        if (row.Type != 29 || row.Validity != ResourceValidity.Valid)
        {
            output.Add(CreateRelationship(descriptorIdentity, AssetRelationshipKind.Container, "Bank container",
                new ResourceReference(IdentifierNamespace.Archive, row.Id.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), descriptorIdentity, 29),
                new(ReferenceStatus.Unresolved, [], "The stored container row is not a valid type-29 resource."), verified: false,
                source: "Type-1022 bank descriptor", description: "The container row did not pass archive validation."));
            return;
        }

        var extent = bank.Slice.Offset >= row.Offset && bank.Slice.Length >= 0 && bank.Slice.Offset - row.Offset <= row.StoredSize &&
                     bank.Slice.Length <= row.StoredSize - (bank.Slice.Offset - row.Offset)
            ? new DecodedExtent(containerIdentity, bank.Slice.Offset - row.Offset, bank.Slice.Length)
            : null;
        var volume = snapshot.Volumes.SingleOrDefault(item => item.Package == row.Package);
        var sourceExtent = volume is null ? null : new SourceExtent(volume.RelativeName, row.Offset, row.StoredSize);
        output.Add(CreateRelationship(descriptorIdentity, AssetRelationshipKind.Container, "Bank container",
            new ResourceReference(IdentifierNamespace.Archive, row.Id.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), descriptorIdentity, 29),
            new(ReferenceStatus.Unique, [containerIdentity]), verified: true, source: "Type-1022 bank descriptor",
            description: "The descriptor resolved to one readable type-29 container row.", physicalExtent: sourceExtent, decodedExtent: extent));
    }

    private void AddDeclaredMediaRelationships(BankInfo bank, List<AssetRelationship> output)
    {
        foreach (var node in bank.Objects.Values.OrderBy(item => item.Id))
        {
            if (node.Identity is null) continue;
            foreach (var source in node.Sources)
            {
                var candidates = FindMediaForDeclaration(bank.Key, node.Id, source);
                var resolution = ResolveCandidates(candidates, candidates.Count == 0
                    ? "No audio row retained this bank-scoped HIRC media declaration." : null);
                var hasExactEvidence = catalog.SourceDeclarations.Any(item => item.BankKey == bank.Key && item.ObjectId == node.Id && item.Declaration == source);
                var extent = candidates.Count == 1 && AudioDiscovery.IsCurrentMediaIdentity(snapshot, catalog, candidates[0])
                    ? GetPhysicalExtent(candidates[0]) : null;
                output.Add(CreateRelationship(node.Identity, AssetRelationshipKind.Media, $"Media 0x{source.Id:X8}",
                    new ResourceReference(IdentifierNamespace.Media, source.Id.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), node.Identity),
                    resolution, verified: hasExactEvidence, source: "Parsed HIRC source declaration",
                    description: hasExactEvidence
                        ? $"HIRC object 0x{node.Id:X8} type {node.Type} declares media 0x{source.Id:X8} in bank '{bank.Name}'."
                        : "The HIRC node contained a media ID without matching source-declaration evidence.", physicalExtent: extent));
            }
            foreach (var clip in node.Clips)
            {
                var candidates = FindMediaByBankAndId(bank.Key, clip.MediaId);
                var resolution = ResolveCandidates(candidates, candidates.Count == 0
                    ? "No audio row retained this bank-scoped music clip media ID." : null);
                var extent = candidates.Count == 1 && AudioDiscovery.IsCurrentMediaIdentity(snapshot, catalog, candidates[0])
                    ? GetPhysicalExtent(candidates[0]) : null;
                output.Add(CreateRelationship(node.Identity, AssetRelationshipKind.Media, $"Music clip media 0x{clip.MediaId:X8}",
                    new ResourceReference(IdentifierNamespace.Media, clip.MediaId.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), node.Identity),
                    resolution, verified: true, source: "Parsed HIRC music clip",
                    description: $"Music track 0x{node.Id:X8} contains a clip with media ID 0x{clip.MediaId:X8}.", physicalExtent: extent));
            }
        }
    }

    private void AddEventRelationships(BankInfo startBank, CatalogGraph graph, IReadOnlyDictionary<string, BankInfo> loadedBanks,
        IReadOnlyDictionary<uint, (ReferenceStatus Status, string? Reason)> unavailableBankIds,
        List<AssetRelationship> output, List<string> diagnostics, CancellationToken token)
    {
        foreach (var eventNode in startBank.Objects.Values.Where(item => item.Type == 4).OrderBy(item => item.Id))
        {
            token.ThrowIfCancellationRequested();
            if (eventNode.Identity is null) continue;
            foreach (var actionId in eventNode.Actions)
            {
                var resolution = graph.Resolve(startBank, actionId, type: 3);
                var actionCandidates = resolution.Matches.Select(item => item.Object.Identity).Where(identity => identity is not null)
                    .Cast<ResourceIdentity>().ToArray();
                var actionResolution = new ReferenceResolution(actionCandidates.Length switch
                {
                    0 when resolution.Ambiguous => ReferenceStatus.Ambiguous,
                    0 => ReferenceStatus.Missing,
                    1 => ReferenceStatus.Unique,
                    _ => ReferenceStatus.Ambiguous
                }, actionCandidates,
                    actionCandidates.Length == 0 ? resolution.Ambiguous ? "The HIRC action ID is ambiguous." : "The HIRC action ID is missing." : null);
                output.Add(CreateRelationship(eventNode.Identity, AssetRelationshipKind.Event, $"Action 0x{actionId:X8}",
                    new ResourceReference(IdentifierNamespace.Hirc, actionId.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), eventNode.Identity),
                    actionResolution, verified: true, source: "Parsed HIRC event action list",
                    description: $"HIRC event 0x{eventNode.Id:X8} type 4 references action ID 0x{actionId:X8}."));

                if (resolution.Unique is not { } action || action.Object.ActionType >> 8 != 4 || action.Object.TargetId is not { } targetId)
                    continue;

                var targetBankId = action.Object.TargetBankId;
                BankInfo[] targetBanks = targetBankId is null
                    ? [action.Bank]
                    : unavailableBankIds.ContainsKey(targetBankId.Value)
                        ? []
                        : loadedBanks.Values.Where(item => item.Id == targetBankId.Value).ToArray();
                var targetBank = targetBanks.Length == 1 ? targetBanks[0] : null;
                if (targetBank is null)
                {
                    var targetBankResolution = unavailableBankIds.TryGetValue(targetBankId!.Value, out var unavailable)
                        ? new ReferenceResolution(unavailable.Status, [], unavailable.Reason)
                        : new ReferenceResolution(ReferenceStatus.Missing, [], "The referenced target bank was not loaded.");
                    output.Add(CreateRelationship(action.Object.Identity ?? eventNode.Identity, AssetRelationshipKind.Dependency,
                        $"Target bank 0x{targetBankId:X8}",
                        new ResourceReference(IdentifierNamespace.Bank, targetBankId.Value.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), action.Object.Identity),
                        targetBankResolution, verified: true, source: "Wwise Play action target bank",
                        description: "The Play action names a bank ID that did not resolve uniquely."));
                    continue;
                }

                var targetResolution = graph.Resolve(targetBank, targetId, bankId: targetBankId);
                var targetCandidates = targetResolution.Matches.Select(item => item.Object.Identity).Where(identity => identity is not null)
                    .Cast<ResourceIdentity>().ToArray();
                var objectResolution = new ReferenceResolution(targetCandidates.Length switch
                {
                    0 when targetResolution.Ambiguous => ReferenceStatus.Ambiguous,
                    0 => ReferenceStatus.Missing,
                    1 => ReferenceStatus.Unique,
                    _ => ReferenceStatus.Ambiguous
                }, targetCandidates,
                    targetCandidates.Length == 0 ? targetResolution.Ambiguous ? "The Play action target object is ambiguous." : "The Play action target object is missing." : null);
                output.Add(CreateRelationship(action.Object.Identity ?? eventNode.Identity, AssetRelationshipKind.Dependency,
                    $"Play target 0x{targetId:X8}",
                    new ResourceReference(IdentifierNamespace.Hirc, targetId.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), action.Object.Identity),
                    objectResolution, verified: true, source: "Parsed Wwise Play action",
                    description: $"Play action 0x{action.Object.Id:X8} references HIRC object 0x{targetId:X8}."));
                if (targetResolution.Unique is null) continue;

                var traversal = graph.Explore(action.Bank, targetId, bankId: targetBankId, token: token);
                foreach (var graphDiagnostic in traversal.Diagnostics) diagnostics.Add($"Event 0x{eventNode.Id:X8}: {graphDiagnostic.Code}: {graphDiagnostic.Message}");
                foreach (var source in traversal.Sources)
                {
                    var candidates = FindMediaByBankAndId(source.BankKey, source.MediaId);
                    var mediaResolution = ResolveCandidates(candidates, candidates.Count == 0
                        ? "The HIRC playback graph references media that is absent from the matching bank's audio rows." : null);
                    var mediaExtent = candidates.Count == 1 && AudioDiscovery.IsCurrentMediaIdentity(snapshot, catalog, candidates[0])
                        ? GetPhysicalExtent(candidates[0]) : null;
                    var owningBank = loadedBanks.GetValueOrDefault(source.BankKey);
                    output.Add(CreateRelationship(eventNode.Identity, AssetRelationshipKind.Media,
                        $"Event 0x{eventNode.Id:X8} → media 0x{source.MediaId:X8}",
                        new ResourceReference(IdentifierNamespace.Media, source.MediaId.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), owningBank?.Identity),
                        mediaResolution, verified: true, source: "HIRC event → Play action → playback graph",
                        description: $"The parsed HIRC graph reaches media ID 0x{source.MediaId:X8} in bank context '{source.BankKey}'.",
                        physicalExtent: mediaExtent));
                }
            }
        }
    }

    private async Task LoadExplicitTargetBanksAsync(BankInfo start, Dictionary<string, BankInfo> loaded,
        Dictionary<uint, (ReferenceStatus Status, string? Reason)> unavailable, List<string> diagnostics, bool cacheAllowed, CancellationToken token)
    {
        var targetIds = start.Objects.Values.Where(item => item.Type == 3 && item.ActionType >> 8 == 4)
            .Select(item => item.TargetBankId).Where(id => id is not null).Select(id => id!.Value).Distinct().Order().ToArray();
        foreach (var targetId in targetIds.Take(MaximumRelatedBanks))
        {
            token.ThrowIfCancellationRequested();
            var candidates = catalog.Banks.Where(bank => bank.Id == targetId && bank.Identity is not null).ToArray();
            if (candidates.Length != 1)
            {
                unavailable[targetId] = (candidates.Length == 0 ? ReferenceStatus.Missing : ReferenceStatus.Ambiguous,
                    candidates.Length == 0 ? "No bank descriptor has this Wwise bank ID." : "Multiple bank descriptors have this Wwise bank ID.");
                continue;
            }
            var descriptor = candidates[0].Identity!;
            if (descriptor.Key == start.Identity?.Key) continue;
            try
            {
                var graph = await ReadBankGraphCachedAsync(descriptor, cacheAllowed, token).ConfigureAwait(false);
                loaded[graph.Key] = graph;
            }
            catch (ArchiveException ex)
            {
                unavailable[targetId] = (ex.Code.Contains("Ambiguous", StringComparison.Ordinal) ? ReferenceStatus.Ambiguous : ReferenceStatus.Unresolved, ex.Message);
                diagnostics.Add($"Target bank 0x{targetId:X8}: {ex.Code}: {ex.Message}");
            }
        }
        if (targetIds.Length > MaximumRelatedBanks)
            diagnostics.Add($"Only the first {MaximumRelatedBanks} explicit Play-action target banks were loaded for this relationship request.");
    }

    private void AddDialogueEventRelationships(AudioMedia media, List<AssetRelationship> output)
    {
        if (media.Identity is null) return;
        foreach (var key in media.EventKeys.Distinct(StringComparer.Ordinal))
        {
            var events = catalog.Events.Where(item => item.Key == key).ToArray();
            var candidates = events.Select(item => item.Identity).Where(identity => IsTopLevelIdentity(identity, 1031))
                .Cast<ResourceIdentity>().ToArray();
            var hasStaleCandidate = events.Any(item => item.Identity is not null && !IsTopLevelIdentity(item.Identity, 1031));
            var resolution = new ReferenceResolution(hasStaleCandidate ? ReferenceStatus.Unresolved : candidates.Length switch
            {
                0 => ReferenceStatus.Missing,
                1 => ReferenceStatus.Unique,
                _ => ReferenceStatus.Ambiguous
            }, candidates, hasStaleCandidate ? "The event key resolves only to an identity outside its type-1031 archive owner." :
                candidates.Length == 0 ? "The explicit dialogue event key has no event record." : null);
            output.Add(CreateRelationship(media.Identity, AssetRelationshipKind.Event, "Dialogue event",
                new ResourceReference(IdentifierNamespace.Archive, key, media.Identity, 1031), resolution,
                verified: true, source: "Unique type-4091 COM_CONT references",
                description: $"The parsed dialogue header links type-1033 audio resource {media.Identity.Ordinal} to event key '{key}'."));
        }
    }

    private void AddAmbiguousObjectRelationships(BankInfo bank, List<AssetRelationship> output)
    {
        if (bank.Identity is null) return;
        foreach (var objectId in bank.AmbiguousObjectIds.Order())
            output.Add(CreateRelationship(bank.Identity, AssetRelationshipKind.Dependency, $"Ambiguous HIRC object 0x{objectId:X8}",
                new ResourceReference(IdentifierNamespace.Hirc, objectId.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), bank.Identity),
                new(ReferenceStatus.Ambiguous, [], "Duplicate HIRC IDs were retained as ambiguous and have no unique object identity."),
                verified: true, source: "Parsed HIRC object table", description: $"HIRC ID 0x{objectId:X8} occurs more than once in bank '{bank.Name}'."));
    }

    private List<AudioMedia> FindMediaForDeclaration(string bankKey, uint objectId, MediaReference declaration)
    {
        var candidates = catalog.Media.Where(media => media.BankKey == bankKey && media.Id == declaration.Id &&
            media.SourceEvidence.Any(evidence => evidence.BankKey == bankKey &&
                evidence.ObjectId == objectId && evidence.Declaration == declaration)).ToList();
        return PreferComplete(candidates);
    }

    private List<AudioMedia> FindMediaByBankAndId(string bankKey, uint mediaId) =>
        PreferComplete(catalog.Media.Where(media => media.BankKey == bankKey && media.Id == mediaId).ToList());

    private static List<AudioMedia> PreferComplete(List<AudioMedia> candidates)
    {
        var complete = candidates.Where(MediaPolicy.IsComplete).ToList();
        return complete.Count > 0 ? complete : candidates;
    }

    private SourceExtent? GetPhysicalExtent(AudioMedia media)
    {
        try
        {
            var binding = AudioResourceBinding.Resolve(snapshot, media);
            return new(binding.Volume.RelativeName, binding.Extent.Offset, binding.Extent.Length);
        }
        catch (ArchiveException) { return null; }
    }

    private ReferenceResolution ResolveCandidates(IReadOnlyList<AudioMedia> media, string? missingReason)
    {
        var validMedia = media.Where(item => AudioDiscovery.IsCurrentMediaIdentity(snapshot, catalog, item)).ToArray();
        var invalidCount = media.Count - validMedia.Length;
        var identities = validMedia.Select(item => item.Identity).Where(identity => identity is not null).Cast<ResourceIdentity>()
            .DistinctBy(identity => identity.Key).ToArray();
        if (invalidCount > 0)
            return new(ReferenceStatus.Unresolved, identities, "One or more matching media rows have stale or mismatched archive ownership identities.");
        return new(identities.Length switch { 0 => ReferenceStatus.Missing, 1 => ReferenceStatus.Unique, _ => ReferenceStatus.Ambiguous },
            identities, identities.Length == 0 ? missingReason ?? "The referenced media row is missing." : null);
    }

    private ReferenceResolution ResolveBankCandidates(IReadOnlyList<BankInfo> banks)
    {
        var valid = banks.Where(bank => IsTopLevelIdentity(bank.Identity, 1022)).Select(bank => bank.Identity!)
            .DistinctBy(identity => identity.Key).ToArray();
        var invalid = banks.Count - valid.Length;
        if (invalid > 0)
            return new(ReferenceStatus.Unresolved, valid, "One or more bank candidates do not belong to a current type-1022 archive row.");
        var identities = valid;
        return new(identities.Length switch { 0 => ReferenceStatus.Missing, 1 => ReferenceStatus.Unique, _ => ReferenceStatus.Ambiguous }, identities);
    }

    private AssetRelationship CreateRelationship(ResourceIdentity sourceIdentity, AssetRelationshipKind kind, string label,
        ResourceReference reference, ReferenceResolution resolution, bool verified, string source, string description,
        SourceExtent? physicalExtent = null, DecodedExtent? decodedExtent = null) =>
        new(sourceIdentity, kind, label, reference, resolution,
            new RelationshipEvidence(verified, source, description, physicalExtent, decodedExtent));

    private void ValidateTopLevelIdentity(ResourceIdentity identity, uint expectedType)
    {
        if (identity.IndexContentId != snapshot.IndexContentId || identity.MemberPath is not null || identity.Ordinal < 0 ||
            identity.Ordinal >= snapshot.Records.Count || snapshot.Records[(int)identity.Ordinal].Type != expectedType)
            throw new ArchiveException("InvalidIdentity", "The selected identity is not a top-level resource of the expected type in this archive revision.");
    }

    private bool IsTopLevelIdentity(ResourceIdentity? identity, uint expectedType) =>
        identity is not null && identity.IndexContentId == snapshot.IndexContentId && identity.MemberPath is null &&
        identity.Ordinal >= 0 && identity.Ordinal < snapshot.Records.Count && snapshot.Records[(int)identity.Ordinal].Type == expectedType;

    private void ThrowIfSourceRevisionInvalidated()
    {
        if (Volatile.Read(ref sourceRevisionInvalidated) != 0)
            throw new ArchiveException("SourceChanged", "The selected archive revision changed; reopen it before resolving more audio relationships.");
    }

    private void InvalidateSourceRevision() => Interlocked.Exchange(ref sourceRevisionInvalidated, 1);

    private readonly record struct BankGraphCacheKey(string IndexContentId, string PhysicalSourceStamp,
        string IndexPath, long DescriptorOrdinal);

    private sealed class BankGraphCacheEntry
    {
        public TaskCompletionSource<BankInfo> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource LoadCancellation { get; } = new();
        public BankInfo? Graph { get; set; }
        public string? PayloadSha256 { get; set; }
        public int Waiters { get; set; }
        public long LastUse { get; set; }
        public long ReservedBytes { get; set; }
        public bool Completed { get; set; }
        public bool CancelRequested { get; set; }
    }
}
