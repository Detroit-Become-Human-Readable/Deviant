using System.Security.Cryptography;
using Deviant.Archives;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Narrative;

namespace Deviant.Browser;

/// <summary>Owns one bounded ScriptBundle read, its source pin, and at most one selected inspection result.</summary>
public sealed class ScriptInspectionLease : IDisposable
{
    private const int MaximumStoredBytes = 16 * 1024 * 1024;
    private const long SourceReadOverheadBytes = 256 * 1024;
    private const long FailedResultReservationBytes = 4 * 1024;
    private const long MaximumLuaRetainedBytes = 32 * 1024 * 1024;
    private const long MaximumPropertyRetainedBytes = 32 * 1024 * 1024;

    private readonly object gate = new();
    private readonly AssetMemoryBudget budget;
    private SourceExtentVerificationLease? verification;
    private AssetMemoryBudget.Reservation? bundleReservation;
    private AssetMemoryBudget.Reservation? luaReservation;
    private AssetMemoryBudget.Reservation? propertyReservation;
    private DecodeResult<ScriptBundleDocument>? bundle;
    private ScriptBundleChunk? selectedChunk;
    private ScriptBundleMetadata? selectedMetadata;
    private DecodeResult<Lua51ChunkDocument>? luaChunk;
    private DecodeResult<QdPropertyValueDocument>? propertyValues;
    private bool disposed;

    private ScriptInspectionLease(AssetMemoryBudget budget, SourceExtentVerificationLease verification,
        AssetMemoryBudget.Reservation bundleReservation, DecodeResult<ScriptBundleDocument> bundle)
    {
        this.budget = budget;
        this.verification = verification;
        this.bundleReservation = bundleReservation;
        this.bundle = bundle;
    }

    public DecodeResult<ScriptBundleDocument> Bundle
    {
        get { lock (gate) { ThrowIfDisposed(); return bundle!; } }
    }

    public ScriptBundleChunk? SelectedChunk
    {
        get { lock (gate) { ThrowIfDisposed(); return selectedChunk; } }
    }

    public DecodeResult<Lua51ChunkDocument>? LuaChunk
    {
        get { lock (gate) { ThrowIfDisposed(); return luaChunk; } }
    }

    public ScriptBundleMetadata? SelectedMetadata
    {
        get { lock (gate) { ThrowIfDisposed(); return selectedMetadata; } }
    }

    public DecodeResult<QdPropertyValueDocument>? PropertyValues
    {
        get { lock (gate) { ThrowIfDisposed(); return propertyValues; } }
    }

    public static async Task<ScriptInspectionLease> OpenAsync(ArchiveSnapshot snapshot,
        ResourceIdentity resource, AssetMemoryBudget budget, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(budget);
        token.ThrowIfCancellationRequested();
        if (resource.IndexContentId != snapshot.IndexContentId || resource.MemberPath is not null ||
            resource.Ordinal < 0 || resource.Ordinal >= snapshot.Records.Count)
            throw new ArchiveException("InvalidIdentity", "Select a top-level resource from this archive revision.");

        var row = snapshot.Records[checked((int)resource.Ordinal)];
        if (row.Ordinal != resource.Ordinal || row.Type != 63 || row.Validity != ResourceValidity.Valid ||
            row.StoredSize < 0 || row.StoredSize > MaximumStoredBytes)
            throw new ArchiveException("ScriptBundleLimit", "The selected resource is not a valid, bounded script bundle.");

        AssetMemoryBudget.Reservation? sourceReservation = null;
        AssetMemoryBudget.Reservation? bundleReservation = null;
        SourceExtentVerificationLease? verification = null;
        byte[]? sourceBytes = null;
        var transferred = false;
        try
        {
            sourceReservation = budget.Reserve(checked(row.StoredSize + SourceReadOverheadBytes), "script bundle input and read scratch");
            sourceBytes = await new ArchiveResourceResolver(snapshot).ReadAsync(resource, 0,
                checked((int)row.StoredSize), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var sourceExtent = new SourceExtent(row.VolumeName, row.Offset, sourceBytes.Length,
                Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant());
            verification = await new SourceExtentVerifier().AcquireAsync(snapshot, [sourceExtent], token,
                maximumBytes: MaximumStoredBytes, maximumExtentCount: 1).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var revision = new SourceRevision(snapshot.IndexContentId, snapshot.PhysicalSourceStamp);
            var inputExtent = new DecodedExtent(resource, 0, sourceBytes.Length);
            var limits = new ScriptBundleLimits(MaximumInputBytes: MaximumStoredBytes);
            bundleReservation = budget.Reserve(limits.MaximumRetainedBytes, "script bundle metadata");
            var bundle = new ScriptBundleReader().Read(sourceBytes, resource, revision, inputExtent,
                [sourceExtent], limits, token);
            token.ThrowIfCancellationRequested();
            verification.EnsureUnchanged();
            Array.Clear(sourceBytes);
            sourceBytes = null;
            sourceReservation.Dispose();
            sourceReservation = null;

            if (bundle.Value is null) bundleReservation.ReduceTo(FailedResultReservationBytes);
            var lease = new ScriptInspectionLease(budget, verification, bundleReservation, bundle);
            verification = null;
            bundleReservation = null;
            transferred = true;
            return lease;
        }
        finally
        {
            if (sourceBytes is not null) Array.Clear(sourceBytes, 0, sourceBytes.Length);
            sourceReservation?.Dispose();
            if (!transferred)
            {
                bundleReservation?.Dispose();
                verification?.Dispose();
            }
        }
    }

    /// <summary>Validates only the selected chunk's inner Lua 5.1 prototype tree.</summary>
    public DecodeResult<Lua51ChunkDocument> SelectChunk(int chunkIndex, CancellationToken token = default)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            ClearSelection();
            token.ThrowIfCancellationRequested();
            var document = bundle!.Value ?? throw new InvalidOperationException("The outer script bundle did not parse successfully.");
            if (chunkIndex < 0 || chunkIndex >= document.Chunks.Count)
            {
                var message = document.Chunks.Count == 0
                    ? "The parsed script bundle contains no chunks to select."
                    : $"Choose a chunk index between 0 and {document.Chunks.Count - 1}.";
                throw new ArgumentOutOfRangeException(nameof(chunkIndex), chunkIndex, message);
            }

            var selected = document.Chunks[chunkIndex];
            AssetMemoryBudget.Reservation? reservation = budget.Reserve(MaximumLuaRetainedBytes, "selected Lua chunk metadata");
            try
            {
                token.ThrowIfCancellationRequested();
                verification!.EnsureUnchanged();
                var parsed = new Lua51ChunkReader().Read(selected.BytecodeBytes.Span, selected.Identity,
                    bundle.Revision, selected.BytecodeSpan, document.OriginalPhysicalExtents,
                    new Lua51ReadOptions(MaximumChunkBytes: 16 * 1024 * 1024,
                        MaximumRetainedBytes: MaximumLuaRetainedBytes), token);
                token.ThrowIfCancellationRequested();
                verification!.EnsureUnchanged();
                if (parsed.Value is null) reservation.ReduceTo(FailedResultReservationBytes);
                selectedChunk = selected;
                luaChunk = parsed;
                luaReservation = reservation;
                reservation = null;
                return parsed;
            }
            finally { reservation?.Dispose(); }
        }
    }

    /// <summary>Decodes only the selected NOCHK payload as a QD property-value graph.</summary>
    public DecodeResult<QdPropertyValueDocument> SelectMetadata(int chunkIndex, int metadataIndex,
        CancellationToken token = default)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            ClearSelection();
            token.ThrowIfCancellationRequested();
            var document = bundle!.Value ?? throw new InvalidOperationException("The outer script bundle did not parse successfully.");
            if (chunkIndex < 0 || chunkIndex >= document.Chunks.Count)
            {
                var message = document.Chunks.Count == 0
                    ? "The parsed script bundle contains no chunks to select."
                    : $"Choose a chunk index between 0 and {document.Chunks.Count - 1}.";
                throw new ArgumentOutOfRangeException(nameof(chunkIndex), chunkIndex, message);
            }

            var chunk = document.Chunks[chunkIndex];
            if (metadataIndex < 0 || metadataIndex >= chunk.Metadata.Count)
            {
                var message = chunk.Metadata.Count == 0
                    ? "The selected chunk contains no metadata records to select."
                    : $"Choose a metadata index between 0 and {chunk.Metadata.Count - 1}.";
                throw new ArgumentOutOfRangeException(nameof(metadataIndex), metadataIndex, message);
            }

            var metadata = chunk.Metadata[metadataIndex];
            AssetMemoryBudget.Reservation? reservation = budget.Reserve(MaximumPropertyRetainedBytes,
                "selected QD property metadata");
            try
            {
                token.ThrowIfCancellationRequested();
                verification!.EnsureUnchanged();
                var parsed = new QdPropertyValueReader().Read(metadata.OpaquePayload, metadata.Identity,
                    bundle.Revision, metadata.PayloadExtent, document.OriginalPhysicalExtents,
                    new QdPropertyValueReadOptions(MaximumPayloadBytes: MaximumStoredBytes,
                        MaximumRetainedBytes: MaximumPropertyRetainedBytes), token);
                token.ThrowIfCancellationRequested();
                verification.EnsureUnchanged();
                if (parsed.Value is null) reservation.ReduceTo(FailedResultReservationBytes);
                selectedChunk = chunk;
                selectedMetadata = metadata;
                propertyValues = parsed;
                propertyReservation = reservation;
                reservation = null;
                return parsed;
            }
            finally { reservation?.Dispose(); }
        }
    }

    public void EnsureUnchanged()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            verification!.EnsureUnchanged();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            luaChunk = null;
            propertyValues = null;
            selectedChunk = null;
            selectedMetadata = null;
            bundle = null;
            Interlocked.Exchange(ref luaReservation, null)?.Dispose();
            Interlocked.Exchange(ref propertyReservation, null)?.Dispose();
            Interlocked.Exchange(ref bundleReservation, null)?.Dispose();
            Interlocked.Exchange(ref verification, null)?.Dispose();
        }
    }

    private void ClearSelection()
    {
        luaChunk = null;
        propertyValues = null;
        selectedChunk = null;
        selectedMetadata = null;
        Interlocked.Exchange(ref luaReservation, null)?.Dispose();
        Interlocked.Exchange(ref propertyReservation, null)?.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}
