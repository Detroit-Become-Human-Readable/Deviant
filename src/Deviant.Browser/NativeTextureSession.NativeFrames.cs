using System.Buffers.Binary;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Containers;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

public sealed partial class NativeTextureSession
{
    public async Task<NativeTextureReadResult> ReadForNativeAsync(ResourceIdentity nativeTextureIdentity,
        ResourceIdentity catalogIdentity, NativeContainerLease companion, CancellationToken token = default,
        long reservedSourceBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(nativeTextureIdentity);
        ArgumentNullException.ThrowIfNull(catalogIdentity);
        ArgumentNullException.ThrowIfNull(companion);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (reservedSourceBytes < 0 || reservedSourceBytes > MaximumRetainedSourceBytes)
            throw new ArgumentOutOfRangeException(nameof(reservedSourceBytes));
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        own.Token.ThrowIfCancellationRequested();
        ValidateCompanion(companion);
        using var borrowed = companion.AcquireLease();
        await readGate.WaitAsync(own.Token).ConfigureAwait(false);
        try
        {
            var metadata = await ReadNativeMetadataAsync(nativeTextureIdentity, catalogIdentity, companion, own.Token)
                .ConfigureAwait(false);
            if (metadata is null) return new(null, "Texture metadata is unavailable.");
            if (metadata.Value.Record.References.Any(reference => reference.Resolution.Status != ReferenceStatus.Unique ||
                reference.Resolution.Candidates.Count != 1))
                return new(null, "Texture references are missing or ambiguous.",
                    CandidateIdentities: [metadata.Value.Record.Identity]);
            var record = metadata.Value.Record;
            if (record.References.Count < 1 || record.References[0].Reference.ResourceType != 2229 ||
                record.References.Skip(1).Any(reference => reference.Reference.ResourceType != 2230))
                return new(null, "This texture reference layout is unavailable.", CandidateIdentities: [record.Identity]);
            var identities = record.References.Select(reference => reference.Resolution.Candidates[0]).Distinct().ToArray();
            long retainedBytes = metadata.Value.Bytes.Length;
            foreach (var identity in identities)
            {
                var size = Snapshot.Records.Single(row => row.Ordinal == identity.Ordinal).StoredSize;
                if (size < 0 || size > MaximumRetainedSourceBytes - reservedSourceBytes - retainedBytes)
                    return new(null, "The texture exceeds the source memory budget.", CandidateIdentities: [record.Identity]);
                retainedBytes += size;
            }
            var payloads = new List<NativeTexturePayload>();
            foreach (var identity in identities) payloads.Add(await ReadPayloadAsync(identity, own.Token).ConfigureAwait(false));
            companion.EnsureUnchanged(); own.Token.ThrowIfCancellationRequested();
            var source = new NativeTextureSource(record.References[0].Resolution.Candidates[0], Revision,
                catalogIdentity, metadata.Value.CatalogSha256, companion.StoredSource, record, metadata.Value.Bytes,
                Hash(metadata.Value.Bytes.Span), payloads.AsReadOnly(), CompanionLease: companion)
            { NativeTextureIdentity = nativeTextureIdentity };
            return new(source, null);
        }
        finally { readGate.Release(); }
    }

    private void ValidateCompanion(NativeContainerLease companion)
    {
        if (companion.Revision != Revision || companion.Snapshot.IndexContentId != Snapshot.IndexContentId ||
            companion.Snapshot.PhysicalSourceStamp != Snapshot.PhysicalSourceStamp ||
            companion.Identity.IndexContentId != Snapshot.IndexContentId || companion.Identity.MemberPath is not null ||
            companion.StoredSource.Identity != companion.Identity || companion.StoredSource.Type != 29 ||
            companion.Directory.Identity != companion.Identity || companion.Directory.Revision != Revision)
            throw new ArchiveException("SourceChanged", "The texture companion belongs to another archive revision.");
        companion.EnsureUnchanged();
    }

    private async Task<(TextureFileTextV24Document Record, ReadOnlyMemory<byte> Bytes, string CatalogSha256)?>
        ReadNativeMetadataAsync(ResourceIdentity nativeTextureIdentity, ResourceIdentity catalogIdentity,
            NativeContainerLease companion, CancellationToken token)
    {
        if (nativeTextureIdentity.IndexContentId != Snapshot.IndexContentId ||
            nativeTextureIdentity.Ordinal != companion.Identity.Ordinal || nativeTextureIdentity.MemberPath is null ||
            catalogIdentity.IndexContentId != Snapshot.IndexContentId || catalogIdentity.MemberPath is not null ||
            Snapshot.Records.SingleOrDefault(row => row.Ordinal == catalogIdentity.Ordinal)?.Type != 3)
            throw new ArchiveException("InvalidTextureIdentity", "Select a texture and its catalog from this archive.");
        ValidateCompanion(companion);
        var frames = companion.Directory.Objects.Where(frame => frame.Identity == nativeTextureIdentity && frame.NativeType == 2137).ToArray();
        if (frames.Length != 1) return null;
        await references.ValidateSnapshotIndexHashAsync(Snapshot, token).ConfigureAwait(false);
        var table = await references.ReadAsync(Snapshot, catalogIdentity, token).ConfigureAwait(false);
        if (table.Status != LocalizationReferenceReadStatus.Verified || table.ReferenceTableSha256 is null)
            throw new ArchiveException("TextureCatalogUnavailable", "The texture catalog is unavailable.");
        await references.ValidateReferenceTableHashAsync(Snapshot, table, token).ConfigureAwait(false);
        var companions = table.Relationships.Where(relationship => relationship.Reference.ResourceType == 29).ToArray();
        if (companions.Length != 1 || companions[0].Resolution.Status != ReferenceStatus.Unique ||
            companions[0].Resolution.Candidates.Count != 1 || companions[0].NavigationTarget != companion.Identity)
            throw new ArchiveException("TextureSourceMismatch", "The texture companion does not match its catalog.");
        var membership = new TextureCatalogMembershipReader().Read(Snapshot, catalogIdentity, Revision, table.Relationships);
        var frame = frames[0];
        if (!new NativeCommonPrefixReader().TryRead(frame, out var prefix, out _, token) || prefix!.ClassCount != 4)
            return null;
        var end = frame.RawPayload.Length - 12;
        if (end < prefix.NextClassOffset + 96 || !frame.RawPayload.Span.Slice(end, 8).SequenceEqual("FTEXCRAW"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(frame.RawPayload.Span[(end + 8)..]) != 1)
            return null;
        var parsed = new TextureFileTextV24Reader().Parse(frame.RawPayload.Span, prefix.NextClassOffset, end,
            companion.Identity, Revision, frame.PayloadExtent, [companion.StoredSource.Extent], membership, token);
        if (parsed.Value is not { } record) return null;
        var bytes = frame.RawPayload.Slice(prefix.NextClassOffset,
            checked((int)record.ExactConsumedDecodedExtent.Length));
        return (record, bytes, table.ReferenceTableSha256);
    }

    private async Task RevalidateNativeSourceAsync(NativeTextureSource source, CancellationToken token)
    {
        var companion = source.CompanionLease!;
        ValidateCompanion(companion);
        using var borrowed = companion.AcquireLease();
        if (source.NativeTextureIdentity is null || source.Container != companion.StoredSource ||
            source.Metadata.Revision != Revision || source.Metadata.Identity.Ordinal != companion.Identity.Ordinal ||
            source.MetadataSha256 != Hash(source.MetadataBytes.Span))
            throw new ArchiveException("TextureSourceMismatch", "The texture source does not match its companion metadata.");
        var metadata = await ReadNativeMetadataAsync(source.NativeTextureIdentity, source.CatalogIdentity, companion, token)
            .ConfigureAwait(false);
        if (metadata is null || metadata.Value.CatalogSha256 != source.CatalogSha256 ||
            metadata.Value.Record.Identity != source.Metadata.Identity || Hash(metadata.Value.Bytes.Span) != source.MetadataSha256 ||
            metadata.Value.Record.References.Any(reference => reference.Resolution.Status != ReferenceStatus.Unique ||
                reference.Resolution.Candidates.Count != 1) ||
            metadata.Value.Record.References[0].Resolution.Candidates[0] != source.SelectedIdentity)
            throw new ArchiveException("TextureSourceMismatch", "The texture source does not match its catalog metadata.");
        foreach (var reference in metadata.Value.Record.References)
        {
            token.ThrowIfCancellationRequested();
            var expected = source.Payloads.Where(payload => payload.Identity == reference.Resolution.Candidates[0]).ToArray();
            if (expected.Length != 1 || expected[0].Sha256 != Hash(expected[0].OriginalBytes.Span))
                throw new ArchiveException("TextureSourceMismatch", "The texture payload does not match its source.");
            var actual = await ReadPayloadAsync(expected[0].Identity, token).ConfigureAwait(false);
            if (actual.Sha256 != expected[0].Sha256 || actual.Type != expected[0].Type || actual.Id != expected[0].Id)
                throw new ArchiveException("SourceChanged", "The texture source changed. Reopen the archive.");
        }
        companion.EnsureUnchanged();
    }
}
