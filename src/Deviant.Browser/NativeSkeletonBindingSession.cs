using Deviant.Assets;
using Deviant.Archives;
using Deviant.Core;
using Deviant.Formats.Models;
using Deviant.Formats.Scenes;

namespace Deviant.Browser;

/// <summary>
/// Owns a verified skeleton and, when uniquely referenced, its typed node-array projection.
/// Dispose the owner to prevent future acquisitions; existing consumer leases remain valid.
/// </summary>
public sealed class NativeSkeletonBindingSession : IDisposable
{
    private readonly object sync = new();
    private Core? core;
    private bool disposed;

    private NativeSkeletonBindingSession(Core core) => this.core = core;

    public NativeSkeletonBindingSessionResult Result
    {
        get { lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); return core!.Result; } }
    }

    /// <summary>Open the exact selected container from the supplied snapshot and retain a borrower for this session.</summary>
    public static async Task<NativeSkeletonBindingSession> OpenAsync(ArchiveSnapshot snapshot,
        CharacterModelBindingResult selectedBinding, AssetMemoryBudget sharedBudget,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selectedBinding);
        ArgumentNullException.ThrowIfNull(sharedBudget);
        token.ThrowIfCancellationRequested();
        var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
        if (selectedBinding.Revision != revision)
            throw new ArchiveException("NativeSkeletonBindingRevisionMismatch", "The selected model belongs to another archive revision.");
        var container = await NativeContainerLease.OpenMetadataAsync(snapshot, selectedBinding.ContainerIdentity,
            sharedBudget, token).ConfigureAwait(false);
        try
        {
            var session = await FromContainerAsync(container, selectedBinding, revision, token).ConfigureAwait(false);
            return session;
        }
        finally { container.Dispose(); }
    }

    /// <summary>
    /// Build from a caller-owned native-container lease. The caller retains ownership of that lease.
    /// The explicit revision is the revision under which the selected skeleton frame was read.
    /// </summary>
    public static async Task<NativeSkeletonBindingSession> FromContainerAsync(
        NativeContainerLease container, CharacterModelBindingResult selectedBinding,
        SourceRevision skeletonRevision, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(selectedBinding);
        ArgumentNullException.ThrowIfNull(skeletonRevision);
        token.ThrowIfCancellationRequested();
        ValidateSelected(container, selectedBinding, skeletonRevision);

        IDisposable? containerRetention = null;
        List<AssetMemoryBudget.Reservation>? reservations = [];
        SourceExtentVerificationLease? verifier = null;
        AssetMemoryBudget.Reservation ReserveStage(long bytes, string category)
        {
            if (bytes < 0) throw new ArchiveException("NativeSkeletonBindingMemoryLimit", "A binding stage has an invalid memory estimate.");
            long alreadyReserved = 0;
            foreach (var item in reservations!) alreadyReserved = checked(alreadyReserved + item.Bytes);
            if (bytes > MaximumWorkingBytes - alreadyReserved)
                throw new ArchiveException("NativeSkeletonBindingMemoryLimit", "The staged binding exceeds the session working-memory limit.");
            var next = container.MemoryBudget.Reserve(bytes, category);
            reservations!.Add(next);
            return next;
        }
        try
        {
            containerRetention = container.AcquireLease();
            container.EnsureUnchanged();
            var owner = FindUniqueFrame(container, selectedBinding.SelectedIdentity, 2013,
                "NativeSkeletonBindingOwnerUnavailable");
            ValidateMemberIdentity(container, owner, 2013);
            var candidateRows = selectedBinding.SourceExtents;
            if (candidateRows is null || candidateRows.Count is < 1 or > MaximumSourceExtents)
                throw new ArchiveException("NativeSkeletonBindingExtentLimit", "The selected source extent set is unavailable or too large.");
            var extentScratchReservation = ReserveStage(checked(candidateRows.Count * 512L + MaximumSourceProvenanceBytes),
                "skeleton binding source checks");
            var validatedRows = new SourceExtent[candidateRows.Count];
            for (var index = 0; index < validatedRows.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                validatedRows[index] = candidateRows[index] ?? throw new ArchiveException(
                    "NativeSkeletonBindingExtentInvalid", "A selected source extent is missing.");
            }
            var sourceExtents = validatedRows.DistinctBy(item => (item.RelativeSource, item.Offset, item.Length, item.PayloadSha256)).ToArray();
            var containerExtents = container.SourceExtents.ToArray();
            foreach (var required in containerExtents)
                if (!sourceExtents.Contains(required))
                    throw new ArchiveException("NativeSkeletonBindingExtentInvalid", "The selected binding omits its native-container source proof.");
            var containerExtentKeys = containerExtents.Select(ExtentKey).ToHashSet();
            var additionalExtents = sourceExtents.Where(extent => !containerExtentKeys.Contains(ExtentKey(extent))).ToArray();
            long sourceBytes = 0;
            foreach (var extent in sourceExtents)
            {
                token.ThrowIfCancellationRequested();
                if (extent is null || extent.Length < 0 || extent.Offset < 0 || extent.Offset > long.MaxValue - extent.Length)
                    throw new ArchiveException("NativeSkeletonBindingExtentInvalid", "A selected source extent is invalid.");
                sourceBytes = checked(sourceBytes + extent.Length);
            }
            if (sourceBytes > MaximumSourceBytes)
                throw new ArchiveException("NativeSkeletonBindingExtentLimit", "The selected source extent set exceeds its byte limit.");

            if (additionalExtents.Length != 0)
            {
                verifier = await new SourceExtentVerifier().AcquireAsync(container.Snapshot, additionalExtents, token,
                    maximumBytes: MaximumSourceBytes, maximumExtentCount: MaximumSourceExtents).ConfigureAwait(false);
                verifier.EnsureUnchanged();
            }

            // The frame payload is already owned by the retained container. Reserve its possible reader copies,
            // bounded parser metadata, and the two decoded mesh-prefix names before allocating skeleton DTOs.
            var skeletonStage = ReserveStage(checked(owner.RawPayload.Length * 3L + MaximumParserMetadataBytes +
                MaximumSharedPrefixNameBytes + SkeletonWorkingScratchBytes), "skeleton binding parse");
            var entityReader = new SceneEntityReader();
            if (!entityReader.TryRead(owner, out var entity, out var entityDiagnostic, token) || entity is null)
                throw new ArchiveException("NativeSkeletonBindingEntityUnavailable", entityDiagnostic ?? "The selected skeleton entity is unavailable.");
            if (!new NativeSkeletonReader().TryRead(owner, entity,
                    new NativeSkeletonLimits(MaximumMetadataBytes: MaximumParserMetadataBytes),
                    out var skeleton, out var skeletonDiagnostic, token) || skeleton is null)
                throw new ArchiveException("NativeSkeletonBindingSkeletonUnavailable", skeletonDiagnostic ?? "The selected skeleton is unavailable.");
            if (skeleton.Identity != selectedBinding.SelectedIdentity || skeleton.Version != 15 ||
                skeleton.MeshPrefix.GroupIndex != selectedBinding.GroupIndex ||
                (selectedBinding.Rig is { } rig && (rig.Version != skeleton.Version || rig.MatrixCount != skeleton.Matrices.Count)))
                throw new ArchiveException("NativeSkeletonBindingSelectionMismatch", "The parsed skeleton does not match the selected binding.");
            if (skeleton.EntityNode != selectedBinding.EntityNode)
                throw new ArchiveException("NativeSkeletonBindingReferenceChanged", "The stored EntityNode reference changed since catalog discovery.");
            if (skeleton.RetainedPayloadBytes > owner.RawPayload.Length ||
                skeleton.SharedPrefixNameBytes > MaximumSharedPrefixNameBytes)
                throw new ArchiveException("NativeSkeletonBindingMemoryEstimate", "The parsed skeleton exceeds its payload or prefix-name reservation.");
            var skeletonRetained = checked(skeleton.RetainedPayloadBytes + skeleton.AccountedMetadataBytes +
                skeleton.SharedPrefixNameBytes + RetainedStageOverheadBytes);
            ReduceStage(skeletonStage, skeletonRetained);

            var resolver = new NativeReferenceResolver(container.Directory);
            var resolution = skeleton.EntityNode is null
                ? new ReferenceResolution(ReferenceStatus.Unresolved, [], "The skeleton has no typed EntityNode reference.")
                : resolver.Resolve(skeleton.EntityNode);
            ValidateCachedResolution(selectedBinding.EntityNodeResolution, resolution);

            NativeNodeArrayBodyDocument? nodeArray = null;
            NativeSkeletonNodeArrayBindingDocument? projection = null;
            ResourceIdentity? nodeIdentity = null;
            string? diagnostic = null;
            if (resolution.Status == ReferenceStatus.Unique && resolution.Candidates.Count == 1)
            {
                nodeIdentity = resolution.Candidates[0];
                var nodeFrame = FindUniqueFrame(container, nodeIdentity, 2138, "NativeSkeletonBindingNodeArrayUnavailable");
                ValidateMemberIdentity(container, nodeFrame, 2138);
                if (nodeFrame.RawPayload.Length > MaximumNodePayloadBytes)
                    throw new ArchiveException("NativeSkeletonBindingNodeArrayLimit", "The referenced node array exceeds its payload limit.");
                var nodeStage = ReserveStage(checked(nodeFrame.RawPayload.Length + MaximumParserMetadataBytes +
                    NodeParserScratchBytes), "skeleton binding node array");
                var prefix = new NativeNodeArrayReader().Read(nodeFrame, skeletonRevision,
                    container.SourceExtents, container.Segments?.Steps,
                    new NativeNodeArrayLimits(MaximumPayloadBytes: MaximumNodePayloadBytes), token);
                if (prefix.Value is null)
                    throw new ArchiveException("NativeSkeletonBindingNodeArrayPrefix", string.Join("; ", prefix.Diagnostics));
                var body = new NativeNodeArrayBodyReader().Read(prefix,
                    new NativeNodeArrayBodyLimits(MaximumMetadataBytes: MaximumParserMetadataBytes), token);
                nodeArray = body.Value ?? throw new ArchiveException("NativeSkeletonBindingNodeArrayBody", string.Join("; ", body.Diagnostics));
                if (nodeArray.Owner.RetainedPayloadBytes > nodeFrame.RawPayload.Length)
                    throw new ArchiveException("NativeSkeletonBindingMemoryEstimate", "The parsed node array exceeds its retained payload reservation.");
                var nodeRetained = checked(nodeArray.Owner.RetainedPayloadBytes + nodeArray.AccountedMetadataBytes +
                    RetainedStageOverheadBytes);
                ReduceStage(nodeStage, nodeRetained);
                var projectionStage = ReserveStage(MaximumBindingProjectionBytes, "skeleton binding projection");
                var bindingReader = new NativeSkeletonNodeArrayBindingReader();
                if (!bindingReader.TryRead(skeleton, skeletonRevision, nodeArray,
                        new NativeSkeletonNodeArrayBindingLimits(MaximumMetadataBytes: MaximumParserMetadataBytes),
                        out projection, out diagnostic, token) || projection is null)
                    throw new ArchiveException(diagnostic ?? "NativeSkeletonBindingJoinFailed", "The stored skeleton and node-array documents could not be joined.");
                ReduceStage(projectionStage, checked(projection.AccountedMetadataBytes + RetainedStageOverheadBytes));
            }
            else
            {
                diagnostic = resolution.Status switch
                {
                    ReferenceStatus.Missing => "NativeSkeletonBindingNodeArrayMissing",
                    ReferenceStatus.Ambiguous => "NativeSkeletonBindingNodeArrayAmbiguous",
                    _ => "NativeSkeletonBindingEntityNodeUnresolved"
                };
            }

            token.ThrowIfCancellationRequested();
            container.EnsureUnchanged();
            verifier?.EnsureUnchanged();
            var provenanceRetained = checked((verifier is null ? 0 : additionalExtents.Length * 256L) +
                resolution.Candidates.Count * 96L + RetainedStageOverheadBytes);
            ReduceStage(extentScratchReservation, provenanceRetained);
            long retainedBytes = 0;
            foreach (var item in reservations!) retainedBytes = checked(retainedBytes + item.Bytes);
            if (retainedBytes > MaximumWorkingBytes)
                throw new ArchiveException("NativeSkeletonBindingMemoryLimit", "The retained binding exceeds the session working-memory limit.");
            var status = resolution.Status switch
            {
                ReferenceStatus.Unique when projection is not null => NativeSkeletonBindingSessionStatus.Bound,
                ReferenceStatus.Missing => NativeSkeletonBindingSessionStatus.EntityNodeMissing,
                ReferenceStatus.Ambiguous => NativeSkeletonBindingSessionStatus.EntityNodeAmbiguous,
                _ => NativeSkeletonBindingSessionStatus.EntityNodeUnresolved
            };
            var result = new NativeSkeletonBindingSessionResult(selectedBinding.SelectedIdentity,
                container.Identity, skeletonRevision, skeleton.MeshPrefix.GroupIndex, status,
                skeleton.EntityNode, resolution, nodeIdentity, diagnostic);
            var core = new Core(container, containerRetention, verifier, reservations!.ToArray(), result,
                skeleton, nodeArray, projection);
            containerRetention = null; verifier = null; reservations = null;
            return new(core);
        }
        catch
        {
            if (reservations is not null)
                foreach (var item in reservations) item.Dispose();
            verifier?.Dispose(); containerRetention?.Dispose();
            throw;
        }
    }

    public NativeSkeletonBindingLease AcquireLease()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var current = core!;
            current.Acquire();
            return new NativeSkeletonBindingLease(current);
        }
    }

    public void Dispose()
    {
        Core? current;
        lock (sync) { if (disposed) return; disposed = true; current = core; core = null; }
        current?.Release();
    }

    private static void ValidateSelected(NativeContainerLease container,
        CharacterModelBindingResult binding, SourceRevision skeletonRevision)
    {
        if (binding.OwnerNativeType != 2013 || binding.SelectedIdentity.MemberPath is null ||
            binding.Revision != skeletonRevision || skeletonRevision != container.Revision ||
            binding.ContainerIdentity != container.Identity || binding.SelectedIdentity.IndexContentId != container.Identity.IndexContentId ||
            binding.SelectedIdentity.Ordinal != container.Identity.Ordinal || binding.SelectedIdentity.MemberPath.Length > 4096 ||
            binding.GroupIndex is null || binding.CatalogIdentity.IndexContentId != container.Identity.IndexContentId ||
            binding.CatalogIdentity.MemberPath is not null || binding.CatalogContainerResolution.Status != ReferenceStatus.Unique ||
            binding.CatalogContainerResolution.Candidates.Count != 1 || binding.CatalogContainerResolution.Candidates[0] != container.Identity)
            throw new ArchiveException("NativeSkeletonBindingSelectionMismatch", "The selected model does not match the supplied container and revision.");
        if (container.Snapshot.FindRecord(binding.CatalogIdentity.Ordinal) is not
            { Type: 3, Validity: ResourceValidity.Valid })
            throw new ArchiveException("NativeSkeletonBindingCatalogMismatch", "The selected catalog is unavailable in the current archive.");
        var expectedPrefix = "dc-object/";
        var parts = binding.SelectedIdentity.MemberPath.Split('/');
        if (!binding.SelectedIdentity.MemberPath.StartsWith(expectedPrefix, StringComparison.Ordinal) || parts.Length != 4 ||
            parts[0] != "dc-object" || !uint.TryParse(parts[2], out var type) || type != 2013 ||
            !uint.TryParse(parts[3], out var id) || id != binding.OwnerNativeId)
            throw new ArchiveException("NativeSkeletonBindingSelectionMismatch", "The selected identity is not a canonical skeleton member.");
    }

    private static NativeObjectFrame FindUniqueFrame(NativeContainerLease container, ResourceIdentity identity,
        uint type, string code)
    {
        var matches = container.Directory.Objects.Where(frame => frame.Identity == identity && frame.NativeType == type).Take(2).ToArray();
        if (matches.Length != 1) throw new ArchiveException(code, "The requested typed native member is missing or duplicated.");
        return matches[0];
    }

    private static void ValidateMemberIdentity(NativeContainerLease container, NativeObjectFrame frame, uint type)
    {
        var expected = $"dc-object/{frame.DirectoryIndex}/{type}/{frame.NativeId}";
        if (frame.Identity != new ResourceIdentity(container.Identity.IndexContentId, container.Identity.Ordinal, expected) ||
            frame.PayloadExtent.Container != container.Identity || frame.PayloadExtent.Length != frame.RawPayload.Length)
            throw new ArchiveException("NativeSkeletonBindingMemberIdentity", "The native member identity or source extent is inconsistent.");
    }

    private static void ValidateCachedResolution(ReferenceResolution? cached, ReferenceResolution current)
    {
        if (cached is null || cached.Status != current.Status || !cached.Candidates.SequenceEqual(current.Candidates))
            throw new ArchiveException("NativeSkeletonBindingReferenceChanged", "The EntityNode resolution changed since catalog discovery.");
    }

    private static (string Source, long Offset, long Length, string? Hash) ExtentKey(SourceExtent extent) =>
        (extent.RelativeSource, extent.Offset, extent.Length, extent.PayloadSha256);

    private static void ReduceStage(AssetMemoryBudget.Reservation reservation, long retainedBytes)
    {
        if (retainedBytes < 0 || retainedBytes > reservation.Bytes)
            throw new ArchiveException("NativeSkeletonBindingMemoryEstimate", "A binding stage exceeded its reserved retained-memory estimate.");
        reservation.ReduceTo(retainedBytes);
    }

    private const int MaximumSourceExtents = 512;
    private const long MaximumSourceBytes = 128L * 1024 * 1024;
    private const long MaximumWorkingBytes = 384L * 1024 * 1024;
    private const long MaximumBindingProjectionBytes = 64L * 1024 * 1024;
    private const long MaximumParserMetadataBytes = 64L * 1024 * 1024;
    private const long MaximumSharedPrefixNameBytes = 2L * 1024 * 1024;
    private const long MaximumSourceProvenanceBytes = 128L * 1024;
    private const long SkeletonWorkingScratchBytes = 128L * 1024;
    private const long NodeParserScratchBytes = 128L * 1024;
    private const long RetainedStageOverheadBytes = 4096;
    private const int MaximumNodePayloadBytes = 64 * 1024 * 1024;

    internal sealed class Core(NativeContainerLease container, IDisposable containerRetention,
        SourceExtentVerificationLease? verifier, AssetMemoryBudget.Reservation[] reservations,
        NativeSkeletonBindingSessionResult result, NativeSkeletonDocument skeleton,
        NativeNodeArrayBodyDocument? nodeArray, NativeSkeletonNodeArrayBindingDocument? binding)
    {
        private readonly object gate = new();
        private int references = 1;
        private bool released;
        private readonly AssetMemoryBudget.Reservation[] reservations = reservations;
        public NativeSkeletonBindingSessionResult Result { get; } = result;
        public NativeSkeletonDocument Skeleton { get; } = skeleton;
        public NativeNodeArrayBodyDocument? NodeArray { get; } = nodeArray;
        public NativeSkeletonNodeArrayBindingDocument? Binding { get; } = binding;

        public void Acquire()
        {
            lock (gate) { ObjectDisposedException.ThrowIf(released, this); checked { references++; } }
        }
        public void EnsureUnchanged()
        {
            lock (gate) { ObjectDisposedException.ThrowIf(released, this); }
            container.EnsureUnchanged(); verifier?.EnsureUnchanged();
        }
        public void Release()
        {
            var cleanup = false;
            lock (gate) { if (released) return; if (--references == 0) { released = true; cleanup = true; } }
            if (cleanup)
            {
                foreach (var reservation in reservations) reservation.Dispose();
                verifier?.Dispose(); containerRetention.Dispose();
            }
        }
    }

    public sealed class NativeSkeletonBindingLease : IDisposable
    {
        private Core? core;
        internal NativeSkeletonBindingLease(Core core) => this.core = core;
        private Core Current => core ?? throw new ObjectDisposedException(nameof(NativeSkeletonBindingLease));
        public NativeSkeletonBindingSessionResult Result => Current.Result;
        public NativeSkeletonDocument Skeleton => Current.Skeleton;
        public NativeNodeArrayBodyDocument? NodeArray => Current.NodeArray;
        public NativeSkeletonNodeArrayBindingDocument? Binding => Current.Binding;
        public void EnsureUnchanged() => Current.EnsureUnchanged();
        public void Dispose() => Interlocked.Exchange(ref core, null)?.Release();
    }
}
