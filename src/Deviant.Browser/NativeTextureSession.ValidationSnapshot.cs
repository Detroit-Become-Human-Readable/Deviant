using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Browser;

internal sealed record NativeTextureValidationPayload(ResourceIdentity Identity, uint Type, uint Id, string Sha256);
internal sealed record NativeTextureValidationSnapshot(ResourceIdentity NativeIdentity, ResourceIdentity SelectedIdentity,
    SourceRevision Revision, ResourceIdentity CatalogIdentity, string CatalogSha256, ResourceIdentity MetadataIdentity,
    string MetadataSha256, NativeContainerLease Companion, IReadOnlyList<NativeTextureValidationPayload> Payloads);

public sealed partial class NativeTextureSession
{
    internal NativeTextureValidationSnapshot CaptureNativeValidation(NativeTextureSource source, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var companion = source.CompanionLease ?? throw new ArgumentException("A native texture companion is required.", nameof(source));
        ValidateCompanion(companion);
        if (source.NativeTextureIdentity is null || source.Revision != Revision || source.Container != companion.MetadataSource ||
            source.Metadata.Revision != Revision || source.Metadata.Identity.Ordinal != companion.Identity.Ordinal ||
            Hash(source.MetadataBytes.Span) != source.MetadataSha256 || source.Payloads.Count is < 1 or > 64)
            throw new ArchiveException("TextureSourceMismatch", "The texture source does not match its companion metadata.");
        var payloads = new List<NativeTextureValidationPayload>();
        foreach (var payload in source.Payloads)
        {
            token.ThrowIfCancellationRequested();
            if (Hash(payload.OriginalBytes.Span) != payload.Sha256)
                throw new ArchiveException("TextureSourceMismatch", "The texture payload does not match its source.");
            payloads.Add(new(payload.Identity, payload.Type, payload.Id, payload.Sha256));
        }
        return new(source.NativeTextureIdentity, source.SelectedIdentity, source.Revision, source.CatalogIdentity,
            source.CatalogSha256, source.Metadata.Identity, source.MetadataSha256, companion, payloads.AsReadOnly());
    }

    internal async Task RevalidateNativeSnapshotAsync(NativeTextureValidationSnapshot source, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await readGate.WaitAsync(own.Token).ConfigureAwait(false);
        try
        {
            if (source.Revision != Revision) throw new ArchiveException("SourceChanged", "The texture belongs to another archive revision.");
            ValidateCompanion(source.Companion);
            using var borrowed = source.Companion.AcquireLease();
            var metadata = await ReadNativeMetadataAsync(source.NativeIdentity, source.CatalogIdentity, source.Companion, own.Token).ConfigureAwait(false);
            if (metadata is null || metadata.CatalogSha256 != source.CatalogSha256 ||
                metadata.Record.Identity != source.MetadataIdentity || Hash(metadata.Bytes.Span) != source.MetadataSha256 ||
                metadata.Record.References.Any(reference => reference.Resolution is not { Status: ReferenceStatus.Unique, Candidates.Count: 1 }) ||
                metadata.Record.References.Select(reference => reference.Resolution.Candidates[0]).Distinct().Count() != source.Payloads.Count ||
                metadata.Record.References[0].Resolution.Candidates[0] != source.SelectedIdentity)
                throw new ArchiveException("TextureSourceMismatch", "The texture source does not match its catalog metadata.");
            foreach (var reference in metadata.Record.References)
            {
                own.Token.ThrowIfCancellationRequested();
                var expected = source.Payloads.Where(payload => payload.Identity == reference.Resolution.Candidates[0]).ToArray();
                if (expected.Length != 1) throw new ArchiveException("TextureSourceMismatch", "The texture payload set changed.");
                await VerifyPayloadAsync(expected[0].Identity, expected[0].Type, expected[0].Id, expected[0].Sha256, own.Token).ConfigureAwait(false);
            }
            source.Companion.EnsureUnchanged();
        }
        finally { readGate.Release(); }
    }
}
