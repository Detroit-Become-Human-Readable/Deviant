using System.Buffers.Binary;
using System.Globalization;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

public sealed partial class NativeTextureSession
{
    /// <summary>Resolves archive texture metadata and loads only the data needed for its selected preview mip.</summary>
    public async Task<NativeTextureReadResult> ReadPreviewAsync(ResourceIdentity selectedIdentity,
        AssetMemoryBudget memoryBudget, int maximumPreviewDimension = 1024, CancellationToken token = default,
        long reservedSourceBytes = 0, ResourceIdentity? metadataIdentity = null,
        IReadOnlyList<ResourceIdentity>? knownCatalogs = null)
    {
        ArgumentNullException.ThrowIfNull(selectedIdentity);
        ArgumentNullException.ThrowIfNull(memoryBudget);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (maximumPreviewDimension < 1) throw new ArgumentOutOfRangeException(nameof(maximumPreviewDimension));
        if (reservedSourceBytes < 0 || reservedSourceBytes > MaximumRetainedSourceBytes)
            throw new ArgumentOutOfRangeException(nameof(reservedSourceBytes));
        if (selectedIdentity.IndexContentId != Snapshot.IndexContentId || selectedIdentity.MemberPath is not null ||
            Snapshot.FindRecord(selectedIdentity.Ordinal)?.Type is not (2229 or 2230))
            throw new ArgumentException("Select an archive texture resource.", nameof(selectedIdentity));

        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        using var shared = ArchiveSourceSession.Acquire(Snapshot, (bytes, reason) => memoryBudget.Reserve(bytes, reason));
        await shared.Session.ValidateIndexAsync(own.Token).ConfigureAwait(false);
        if (knownCatalogs is not null)
        {
            if (knownCatalogs.Count > 128) throw new ArgumentException("Too many texture catalog contexts.", nameof(knownCatalogs));
            foreach (var catalog in knownCatalogs.Distinct())
            {
                own.Token.ThrowIfCancellationRequested();
                if (catalog.IndexContentId != Revision.IndexContentId || catalog.MemberPath is not null ||
                    Snapshot.FindRecord(catalog.Ordinal) is not { Type: 3, Validity: ResourceValidity.Valid })
                    throw new ArchiveException("TextureCatalogSourceMismatch", "The texture catalog belongs to another workspace.");
                // An already indexed binding remains usable after cancelling background discovery.
                // ReadCatalogAsync below still revalidates its reference table before metadata is consumed.
                if (!catalogs.FindGroupsForTexture(selectedIdentity).Any(group => group.CatalogIdentity == catalog))
                    await catalogs.ReadCatalogAsync(catalog, own.Token).ConfigureAwait(false);
            }
        }
        await readGate.WaitAsync(own.Token).ConfigureAwait(false);
        NativeContainerLease? selectedCompanion = null;
        bool transferLease = false;
        try
        {
            var groups = catalogs.FindGroupsForTexture(selectedIdentity);
            if (groups.Count == 0 && !catalogs.EnrichmentTask.IsCompleted)
                return new(null, "Reading texture metadata…", Pending: true) { FailureCode = "TextureMetadataPending" };

            MetadataPreviewCandidate? candidate = null;
            var metadataIdentities = new List<ResourceIdentity>();
            foreach (var group in groups)
            {
                own.Token.ThrowIfCancellationRequested();
                var table = await ReadCatalogAsync(group, own.Token).ConfigureAwait(false);
                var companionRows = table.Relationships.Where(item => item.Reference.ResourceType == 29).ToArray();
                if (companionRows.Length != 1 || companionRows[0].Resolution is not
                    { Status: ReferenceStatus.Unique, Candidates.Count: 1 } || companionRows[0].NavigationTarget is not { } companionIdentity)
                    continue;
                if (metadataIdentity is not null && companionIdentity.Ordinal != metadataIdentity.Ordinal) continue;

                var inspectedCompanion = await NativeContainerLease.OpenMetadataAsync(Snapshot,
                    companionIdentity, memoryBudget, own.Token, allowLargeData: true).ConfigureAwait(false);
                bool retainInspectedCompanion = false;
                try
                {
                    ValidateCompanion(inspectedCompanion);
                    var metadataCandidates = new List<(ResourceIdentity? NativeIdentity, NativeTextureMetadataPlan Metadata)>();
                    long? selectedMetadataOffset = metadataIdentity?.MemberPath is { } memberPath &&
                        memberPath.StartsWith("filetext/", StringComparison.Ordinal) &&
                        long.TryParse(memberPath.AsSpan("filetext/".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedOffset)
                        ? parsedOffset : null;
                    // FILETEXT identities address the decoded body, including records inside native frames.
                    // The exact parsed identity and catalog references are still checked below.
                    foreach (var frame in inspectedCompanion.Directory.Objects.Where(item => item.NativeType == 2137 &&
                        (selectedMetadataOffset is not { } offset || item.PayloadExtent.Offset <= offset &&
                            offset - item.PayloadExtent.Offset < item.PayloadExtent.Length)))
                    {
                        own.Token.ThrowIfCancellationRequested();
                        var metadata = await BuildAnyNativeMetadataAsync(frame.Identity, group.CatalogIdentity,
                            inspectedCompanion, own.Token, reservePlanMemory: false)
                            .ConfigureAwait(false);
                        if (metadata is not null) metadataCandidates.Add((frame.Identity, metadata));
                    }
                    bool foundSelectedMetadata = metadataCandidates.Any(item => item.Metadata.Record.References.Any(reference =>
                        reference.Resolution.Candidates.Contains(selectedIdentity)));
                    if (!foundSelectedMetadata && (inspectedCompanion.Directory.Objects.Count > 0 || inspectedCompanion.Directory.BodyExtent.Length <= MaximumResourceBytes))
                    {
                        var body = inspectedCompanion.Directory.Objects.Count > 0
                            ? new NativeContainerMetadataBody(inspectedCompanion.Directory.RawBody, inspectedCompanion.PhysicalExtents)
                            : await inspectedCompanion.ReadDeclaredMetadataBodyAsync(own.Token).ConfigureAwait(false);
                        var membership = new TextureCatalogMembershipReader().Read(Snapshot, group.CatalogIdentity,
                            Revision, table.Relationships);
                        foreach (var metadata in ParseLegacyMetadataBody(inspectedCompanion, body,
                            group.ReferenceTableSha256, membership, own.Token))
                            metadataCandidates.Add((null, metadata));
                    }

                    foreach (var item in metadataCandidates)
                    {
                        var metadata = item.Metadata;
                        if (metadataIdentity is not null && metadata.Record.Identity != metadataIdentity) continue;
                        var selectedReferences = metadata.Record.References.Where(reference =>
                            reference.Resolution.Candidates.Contains(selectedIdentity)).ToArray();
                        if (selectedReferences.Length == 0) continue;
                        if (selectedReferences.Length != 1 || selectedReferences[0].Resolution is not
                            { Status: ReferenceStatus.Unique, Candidates.Count: 1 })
                            return new(null, "Texture references are missing or ambiguous.", CandidateIdentities: [metadata.Record.Identity])
                            { FailureCode = "TextureReferenceUnavailable" };

                        metadataIdentities.Add(metadata.Record.Identity);
                        if (metadata.Record.References.Any(reference => reference.Resolution is not
                            { Status: ReferenceStatus.Unique, Candidates.Count: 1 }))
                            return new(null, "Texture references are missing or ambiguous.", CandidateIdentities: [metadata.Record.Identity])
                            { FailureCode = "TextureReferenceUnavailable" };

                        var metadataSource = CreateMetadataSource(item.NativeIdentity ?? inspectedCompanion.Identity,
                            group.CatalogIdentity, inspectedCompanion, metadata);
                        var plan = NativeTextureDecoder.PlanPreview(metadataSource, maximumPreviewDimension, own.Token);
                        long retainedBytes = metadata.Bytes.Length;
                        foreach (var identity in plan.RequiredPayloadIdentities)
                        {
                            var row = Snapshot.RequireRecord(identity.Ordinal);
                            if (row.Validity != ResourceValidity.Valid || row.StoredSize < 1 || row.StoredSize > MaximumResourceBytes)
                                return new(null, "The selected mip exceeds the supported resource limit.", CandidateIdentities: [metadata.Record.Identity])
                                { FailureCode = "TextureResourceLimit" };
                            if (row.StoredSize > MaximumRetainedSourceBytes - reservedSourceBytes - retainedBytes)
                                return new(null, "The texture exceeds the source memory budget.", CandidateIdentities: [metadata.Record.Identity])
                                { FailureCode = "TextureSourceBudget" };
                            retainedBytes = checked(retainedBytes + row.StoredSize);
                        }

                        if (candidate is not null && Hash(candidate.Metadata.Bytes.Span) != Hash(metadata.Bytes.Span))
                        {
                            System.Diagnostics.Trace.TraceInformation("Texture metadata conflict: {0} {1}; {2} {3}",
                                candidate.Metadata.Record.Identity.Key, Hash(candidate.Metadata.Bytes.Span),
                                metadata.Record.Identity.Key, Hash(metadata.Bytes.Span));
                            return new(null, "Multiple texture records reference this resource.", CandidateIdentities:
                                [candidate.Metadata.Record.Identity, metadata.Record.Identity])
                            { FailureCode = "TextureMetadataAmbiguous" };
                        }

                        if (candidate is null)
                        {
                            selectedCompanion = inspectedCompanion;
                            retainInspectedCompanion = true;
                            candidate = new(item.NativeIdentity, group.CatalogIdentity, group.ReferenceTableSha256,
                                metadata, plan.RequiredPayloadIdentities, groups.Count);
                        }
                    }
                }
                finally { if (!retainInspectedCompanion) inspectedCompanion.Dispose(); }
            }

            if (candidate is null)
                return new(null, groups.Count == 0 ? "No texture metadata catalog is available." : "Texture metadata is unavailable.",
                    CandidateIdentities: metadataIdentities.Count == 0 ? null : metadataIdentities.Distinct().ToArray())
                { FailureCode = "TextureMetadataUnavailable" };

            var payloads = new List<NativeTexturePayload>(candidate.RequiredPayloads.Count);
            foreach (var identity in candidate.RequiredPayloads)
            {
                own.Token.ThrowIfCancellationRequested();
                var row = Snapshot.RequireRecord(identity.Ordinal);
                var reservation = memoryBudget.Reserve(row.StoredSize, "native texture preview payload");
                selectedCompanion!.OwnPreparedMetadata(reservation);
                payloads.Add(await ReadPayloadAsync(identity, own.Token).ConfigureAwait(false));
                shared.Session.EnsureCurrent(row.Package);
            }
            own.Token.ThrowIfCancellationRequested();
            selectedCompanion!.EnsureUnchanged();
            var source = new NativeTextureSource(selectedIdentity, Revision, candidate.CatalogIdentity,
                candidate.CatalogSha256, selectedCompanion.MetadataSource, candidate.Metadata.Record,
                candidate.Metadata.Bytes, Hash(candidate.Metadata.Bytes.Span), payloads.AsReadOnly(),
                candidate.BindingCount, selectedCompanion)
            { NativeTextureIdentity = candidate.NativeIdentity, PreviewDimension = maximumPreviewDimension };
            transferLease = true;
            return new(source, null);
        }
        finally
        {
            if (!transferLease) selectedCompanion?.Dispose();
            readGate.Release();
        }
    }

    /// <summary>Loads every FILETEXT payload for an explicit full-resolution action while retaining metadata-only source ownership.</summary>
    public async Task<NativeTextureReadResult> ReadFullResolutionAsync(NativeTextureSource preview,
        AssetMemoryBudget memoryBudget, CancellationToken token = default, long reservedSourceBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(memoryBudget);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (preview.NativeTextureIdentity is not null || preview.CompanionLease is null || !preview.CompanionLease.IsMetadataOnly)
            throw new ArgumentException("This full-resolution route is for legacy FILETEXT metadata sources.", nameof(preview));
        if (reservedSourceBytes < 0 || reservedSourceBytes > MaximumRetainedSourceBytes)
            throw new ArgumentOutOfRangeException(nameof(reservedSourceBytes));

        var result = await ReadPreviewAsync(preview.SelectedIdentity, memoryBudget,
            preview.PreviewDimension ?? 1024, token, reservedSourceBytes, preview.Metadata.Identity).ConfigureAwait(false);
        var source = result.Source;
        if (source is null) return result;
        bool transferLease = false;
        try
        {
            if (source.Metadata.Identity != preview.Metadata.Identity || source.MetadataSha256 != preview.MetadataSha256 ||
                source.CatalogIdentity != preview.CatalogIdentity || source.CatalogSha256 != preview.CatalogSha256)
                throw new ArchiveException("TextureSourceMismatch", "The legacy texture metadata changed before full-resolution loading.");

            var expected = new List<(ResourceIdentity Identity, uint Type, uint Id)>();
            foreach (var reference in source.Metadata.References)
            {
                token.ThrowIfCancellationRequested();
                if (reference.Resolution is not { Status: ReferenceStatus.Unique, Candidates.Count: 1 } ||
                    reference.Reference.Namespace != IdentifierNamespace.Archive ||
                    !uint.TryParse(reference.Reference.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    return new(null, "Texture references are missing or ambiguous.", CandidateIdentities: [source.Metadata.Identity])
                    { FailureCode = "TextureReferenceUnavailable" };
                var identity = reference.Resolution.Candidates[0];
                var row = Snapshot.RequireRecord(identity.Ordinal);
                if (identity.IndexContentId != Snapshot.IndexContentId || identity.MemberPath is not null ||
                    row.Type != reference.Reference.ResourceType || row.Id != id || row.Validity != ResourceValidity.Valid ||
                    row.StoredSize < 1 || row.StoredSize > MaximumResourceBytes)
                    return new(null, "A texture mip exceeds the supported resource limit.", CandidateIdentities: [source.Metadata.Identity])
                    { FailureCode = "TextureResourceLimit" };
                if (expected.All(item => item.Identity != identity)) expected.Add((identity, row.Type, row.Id));
            }

            var existing = source.Payloads.GroupBy(payload => payload.Identity).ToDictionary(group => group.Key, group => group.ToArray());
            long retained = source.MetadataBytes.Length;
            foreach (var item in expected)
            {
                var row = Snapshot.RequireRecord(item.Identity.Ordinal);
                if (row.StoredSize > MaximumRetainedSourceBytes - reservedSourceBytes - retained)
                    return new(null, "The texture exceeds the source memory budget.", CandidateIdentities: [source.Metadata.Identity])
                    { FailureCode = "TextureSourceBudget" };
                retained += row.StoredSize;
            }

            var payloads = new List<NativeTexturePayload>(expected.Count);
            foreach (var item in expected)
            {
                token.ThrowIfCancellationRequested();
                if (existing.TryGetValue(item.Identity, out var matches) && matches.Length == 1)
                    payloads.Add(matches[0]);
                else
                {
                    var row = Snapshot.RequireRecord(item.Identity.Ordinal);
                    source.CompanionLease!.OwnPreparedMetadata(memoryBudget.Reserve(row.StoredSize,
                        "full-resolution texture payload"));
                    payloads.Add(await ReadPayloadAsync(item.Identity, token).ConfigureAwait(false));
                }
            }
            token.ThrowIfCancellationRequested();
            source.CompanionLease!.EnsureUnchanged();
            var fullSource = source with { Payloads = payloads.AsReadOnly(), PreviewDimension = null };
            transferLease = true;
            return new(fullSource, null);
        }
        finally { if (!transferLease) source.CompanionLease?.Dispose(); }
    }

    private IReadOnlyList<NativeTextureMetadataPlan> ParseLegacyMetadataBody(NativeContainerLease companion,
        NativeContainerMetadataBody body, string catalogSha256,
        TextureCatalogMembership membership, CancellationToken token)
    {
        if (body.Bytes.Length > companion.Directory.BodyExtent.Length)
            throw new ArchiveException("NativeMetadataUnavailable", "The declared metadata body is incomplete.");
        var bytes = body.Bytes.Span;
        var extent = companion.Directory.BodyExtent with { Length = body.Bytes.Length };
        var matches = new List<NativeTextureMetadataPlan>();
        var reader = new TextureFileTextV24Reader();
        for (var tagOffset = 0; tagOffset <= bytes.Length - 12; tagOffset++)
        {
            if ((tagOffset & 4095) == 0) token.ThrowIfCancellationRequested();
            if (!bytes.Slice(tagOffset, 8).SequenceEqual("FILETEXT"u8) ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[(tagOffset + 8)..]) != 24) continue;
            var maximumEnd = Math.Min(bytes.Length - 12, checked(tagOffset + TextureFileTextV24Reader.MaximumRecordBytes));
            for (var endOffset = tagOffset + 12; endOffset <= maximumEnd; endOffset++)
            {
                if ((endOffset & 4095) == 0) token.ThrowIfCancellationRequested();
                if (!bytes.Slice(endOffset, 8).SequenceEqual("FTEXCRAW"u8) ||
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes[(endOffset + 8)..]) != 1) continue;
                var record = reader.Parse(bytes, tagOffset, endOffset, companion.Identity, Revision, extent,
                    body.PhysicalExtents, membership, token).Value;
                if (record is not null)
                {
                    var consumed = checked((int)record.ExactConsumedDecodedExtent.Length);
                    matches.Add(new(record, body.Bytes.Slice(tagOffset, consumed), catalogSha256));
                    tagOffset += consumed - 1;
                }
                break;
            }
        }
        return matches.AsReadOnly();
    }

    private async Task<NativeTextureMetadataPlan?> BuildAnyNativeMetadataAsync(ResourceIdentity nativeTextureIdentity,
        ResourceIdentity catalogIdentity, NativeContainerLease companion, CancellationToken token,
        bool reservePlanMemory = true)
    {
        var native = await BuildNativeMetadataAsync(nativeTextureIdentity, catalogIdentity, companion, token,
            reservePlanMemory).ConfigureAwait(false);
        return native ?? await BuildLegacyMetadataAsync(nativeTextureIdentity, catalogIdentity, companion, token,
            reservePlanMemory).ConfigureAwait(false);
    }

    private async Task<NativeTextureMetadataPlan?> BuildLegacyMetadataAsync(ResourceIdentity nativeTextureIdentity,
        ResourceIdentity catalogIdentity, NativeContainerLease companion, CancellationToken token,
        bool reservePlanMemory)
    {
        if (nativeTextureIdentity.IndexContentId != Snapshot.IndexContentId ||
            nativeTextureIdentity.Ordinal != companion.Identity.Ordinal || nativeTextureIdentity.MemberPath is null ||
            catalogIdentity.IndexContentId != Snapshot.IndexContentId || catalogIdentity.MemberPath is not null ||
            Snapshot.FindRecord(catalogIdentity.Ordinal)?.Type != 3)
            throw new ArchiveException("InvalidTextureIdentity", "Select a texture and its catalog from this archive.");
        ValidateCompanion(companion);
        var frames = companion.Directory.Objects.Where(frame => frame.Identity == nativeTextureIdentity && frame.NativeType == 2137).ToArray();
        if (frames.Length != 1) return null;

        await references.ValidateSnapshotIndexHashAsync(Snapshot, token).ConfigureAwait(false);
        var table = await references.ReadAsync(Snapshot, catalogIdentity, token).ConfigureAwait(false);
        if (table.Status != LocalizationReferenceReadStatus.Verified || table.ReferenceTableSha256 is null)
            throw new ArchiveException("TextureCatalogUnavailable", "The texture catalog is unavailable.");
        await references.ValidateReferenceTableHashAsync(Snapshot, table, token).ConfigureAwait(false);
        var companionRows = table.Relationships.Where(item => item.Reference.ResourceType == 29).ToArray();
        if (companionRows.Length != 1 || companionRows[0].Resolution is not
            { Status: ReferenceStatus.Unique, Candidates.Count: 1 } || companionRows[0].NavigationTarget != companion.Identity)
            throw new ArchiveException("TextureSourceMismatch", "The texture companion does not match its catalog.");

        var membership = new TextureCatalogMembershipReader().Read(Snapshot, catalogIdentity, Revision, table.Relationships);
        var frame = frames[0];
        var bytes = frame.RawPayload.Span;
        var logicalExtent = new DecodedExtent(companion.Identity, frame.PayloadExtent.Offset, bytes.Length);
        NativeTextureMetadataPlan? match = null;
        var reader = new TextureFileTextV24Reader();
        for (var tagOffset = 0; tagOffset <= bytes.Length - 12; tagOffset++)
        {
            if ((tagOffset & 4095) == 0) token.ThrowIfCancellationRequested();
            if (!bytes.Slice(tagOffset, 8).SequenceEqual("FILETEXT"u8) ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[(tagOffset + 8)..]) != 24) continue;
            var maximumEnd = Math.Min(bytes.Length - 12, checked(tagOffset + TextureFileTextV24Reader.MaximumRecordBytes));
            for (var endOffset = tagOffset + 12; endOffset <= maximumEnd; endOffset++)
            {
                if ((endOffset & 4095) == 0) token.ThrowIfCancellationRequested();
                if (!bytes.Slice(endOffset, 8).SequenceEqual("FTEXCRAW"u8) ||
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes[(endOffset + 8)..]) != 1) continue;
                var parsed = reader.Parse(bytes, tagOffset, endOffset, companion.Identity, Revision, logicalExtent,
                    [companion.MetadataSource.Extent], membership, token).Value;
                if (parsed is not null)
                {
                    if (match is not null)
                        throw new ArchiveException("TextureMetadataAmbiguous", "Multiple FILETEXT records occur in the selected texture member.");
                    var consumed = checked((int)parsed.ExactConsumedDecodedExtent.Length);
                    match = new(parsed, frame.RawPayload.Slice(tagOffset, consumed), table.ReferenceTableSha256);
                }
                break;
            }
        }

        if (match is not null && reservePlanMemory)
            companion.OwnPreparedMetadata(companion.MemoryBudget.Reserve(
                checked(4096L + match.Record.References.Count * 2048L), "native texture source plan"));
        return match;
    }

    private sealed record MetadataPreviewCandidate(ResourceIdentity? NativeIdentity, ResourceIdentity CatalogIdentity,
        string CatalogSha256, NativeTextureMetadataPlan Metadata, IReadOnlyList<ResourceIdentity> RequiredPayloads,
        int BindingCount);
}
