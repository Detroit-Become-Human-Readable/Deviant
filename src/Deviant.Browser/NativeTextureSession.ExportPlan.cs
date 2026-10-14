using System.Globalization;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

public sealed record NativeTextureExportPayloadPlan(ResourceIdentity Identity, uint Type, uint Id, SourceExtent Extent);
public sealed record NativeTextureExportFingerprint(ResourceIdentity NativeIdentity, ResourceIdentity SelectedIdentity,
    SourceRevision Revision, ResourceIdentity CatalogIdentity, string CatalogSha256, ResourceIdentity MetadataIdentity,
    string MetadataSha256, IReadOnlyList<SourceExtent> MetadataSources, IReadOnlyList<NativeTextureExportPayloadPlan> Payloads);
public sealed record NativeTextureExportSourceEvidence(NativeTextureExportFingerprint Plan,
    IReadOnlyList<NativeTextureExportPayloadPlan> Payloads);
internal sealed record NativeTextureExportPlan(NativeTextureExportFingerprint Fingerprint,
    NativeTextureSource MetadataSource, NativeContainerLease Companion, long EstimatedSourceBytes);

public sealed partial class NativeTextureSession
{
    internal async Task<NativeTextureExportPlan> PlanExportAsync(ResourceIdentity identity, ResourceIdentity catalog,
        NativeContainerLease companion, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        ValidateCompanion(companion); using var borrowed = companion.AcquireLease();
        await readGate.WaitAsync(own.Token).ConfigureAwait(false);
        try
        {
            var metadata = await ReadNativeMetadataAsync(identity, catalog, companion, own.Token).ConfigureAwait(false)
                ?? throw new ArchiveException("TextureMetadataUnavailable", "Texture metadata is unavailable.");
            var record = metadata.Record;
            if (record.References.Any(reference => reference.Resolution is not { Status: ReferenceStatus.Unique, Candidates.Count: 1 }))
                throw new ArchiveException("AmbiguousNativeTextureReference", "Texture references are missing or ambiguous.");
            if (record.References.Count != Math.Max(1, (int)record.DescriptorCount) ||
                record.References[0].Reference.ResourceType != 2229 || record.References.Skip(1).Any(reference => reference.Reference.ResourceType != 2230))
                throw new TextureFormatException("UnsupportedNativeTextureLayout", "This texture reference layout is unavailable.");
            if (record.Mip0Width == 0 || record.Mip0Height == 0 || record.MipSizeCount == 0 ||
                record.Mip0Width > DdsTextureReader.MaximumDimension || record.Mip0Height > DdsTextureReader.MaximumDimension ||
                (long)record.Mip0Width * record.Mip0Height > DdsTextureReader.MaximumPixelCount)
                throw new TextureFormatException("InvalidNativeTextureDimensions", "Texture dimensions or mip count are invalid.");
            int maximumMips = 1;
            for (int dimension = Math.Max((int)record.Mip0Width, record.Mip0Height); dimension > 1; dimension >>= 1) maximumMips++;
            if (record.MipSizeCount > maximumMips)
                throw new TextureFormatException("InvalidNativeTextureDimensions", "Texture dimensions or mip count are invalid.");
            var payloads = new List<NativeTextureExportPayloadPlan>(); long estimated = metadata.Bytes.Length;
            foreach (var reference in record.References)
            {
                own.Token.ThrowIfCancellationRequested(); var target = reference.Resolution.Candidates[0];
                if (target.IndexContentId != Snapshot.IndexContentId || target.MemberPath is not null ||
                    reference.Reference.Namespace != IdentifierNamespace.Archive ||
                    !uint.TryParse(reference.Reference.Id, NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
                    throw Mismatch();
                var row = Snapshot.RequireRecord(target.Ordinal);
                if (row.Type != reference.Reference.ResourceType || row.Id != id) throw Mismatch();
                if (row.Validity != ResourceValidity.Valid || row.StoredSize < 1 || row.StoredSize > MaximumResourceBytes)
                    throw new ArchiveException("TextureResourceLimit", "The texture source exceeds the supported resource limit.");
                if (payloads.Any(payload => payload.Identity == target)) continue;
                estimated = checked(estimated + row.StoredSize);
                if (estimated > MaximumRetainedSourceBytes) throw new ArchiveException("TextureSourceBudget", "The texture exceeds the source memory budget.");
                payloads.Add(new(target, row.Type, row.Id, new(row.VolumeName, row.Offset, row.StoredSize)));
            }
            // COM_CONT hashes cover the fixed 20-byte prefix and its ordered nine-byte typed reference rows.
            var table = await references.ReadAsync(Snapshot, catalog, own.Token).ConfigureAwait(false);
            if (table.Status != LocalizationReferenceReadStatus.Verified || table.ReferenceTableSha256 != metadata.CatalogSha256) throw Mismatch();
            var catalogRow = Snapshot.RequireRecord(catalog.Ordinal);
            var catalogExtent = new SourceExtent(catalogRow.VolumeName, catalogRow.Offset,
                checked(20L + table.Relationships.Count * 9L), metadata.CatalogSha256);
            var sources = companion.SourceExtents.Append(catalogExtent).Distinct().ToArray();
            var fingerprint = new NativeTextureExportFingerprint(identity, record.References[0].Resolution.Candidates[0], Revision,
                catalog, metadata.CatalogSha256, record.Identity, Hash(metadata.Bytes.Span), sources, payloads.AsReadOnly());
            companion.EnsureUnchanged(); own.Token.ThrowIfCancellationRequested();
            return new(fingerprint, CreateMetadataSource(identity, catalog, companion, metadata), companion, estimated);
        }
        finally { readGate.Release(); }
    }

    internal NativeTextureExportSourceEvidence CaptureExportEvidence(NativeTextureExportPlan plan,
        NativeTextureSource source, CancellationToken token)
    {
        var validation = CaptureNativeValidation(source, token);
        if (validation.NativeIdentity != plan.Fingerprint.NativeIdentity || validation.SelectedIdentity != plan.Fingerprint.SelectedIdentity ||
            validation.Revision != plan.Fingerprint.Revision || validation.CatalogIdentity != plan.Fingerprint.CatalogIdentity ||
            validation.CatalogSha256 != plan.Fingerprint.CatalogSha256 || validation.MetadataIdentity != plan.Fingerprint.MetadataIdentity ||
            validation.MetadataSha256 != plan.Fingerprint.MetadataSha256 || source.Payloads.Count != plan.Fingerprint.Payloads.Count) throw Mismatch();
        var payloads = source.Payloads.Select(payload => new NativeTextureExportPayloadPlan(payload.Identity,
            payload.Type, payload.Id, payload.Extent with { PayloadSha256 = payload.Sha256 })).ToArray();
        var evidence = new NativeTextureExportSourceEvidence(plan.Fingerprint, payloads);
        ValidateExportEvidenceShape(plan, evidence); return evidence;
    }

    internal async Task<SourceExtentVerificationLease> VerifyExportEvidenceAsync(NativeTextureExportPlan plan,
        NativeTextureExportSourceEvidence evidence, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ValidateCompanion(plan.Companion);
        ValidateExportEvidenceShape(plan, evidence);
        var extents = plan.Fingerprint.MetadataSources.Concat(evidence.Payloads.Select(payload => payload.Extent)).Distinct().ToArray();
        using var scratch = plan.Companion.MemoryBudget.Reserve(128 * 1024, "export texture source verification");
        var verified = await new SourceExtentVerifier().AcquireAsync(Snapshot, extents, token,
            maximumBytes: SourceExtentVerifier.MaximumBytes).ConfigureAwait(false);
        try { plan.Companion.EnsureUnchanged(); verified.EnsureUnchanged(); return verified; }
        catch { verified.Dispose(); throw; }
    }

    internal NativeTextureValidationSnapshot ExportValidation(NativeTextureExportPlan plan,
        NativeTextureExportSourceEvidence evidence)
    {
        ValidateExportEvidenceShape(plan, evidence); var p = plan.Fingerprint;
        return new(p.NativeIdentity, p.SelectedIdentity, p.Revision, p.CatalogIdentity, p.CatalogSha256, p.MetadataIdentity,
            p.MetadataSha256, plan.Companion, evidence.Payloads.Select(payload => new NativeTextureValidationPayload(
                payload.Identity, payload.Type, payload.Id, payload.Extent.PayloadSha256!)).ToArray());
    }

    private void ValidateExportEvidenceShape(NativeTextureExportPlan plan, NativeTextureExportSourceEvidence evidence)
    {
        if (plan.Fingerprint.Revision != Revision || JsonSerializer.Serialize(plan.Fingerprint) != JsonSerializer.Serialize(evidence.Plan) ||
            evidence.Payloads.Count != plan.Fingerprint.Payloads.Count) throw Mismatch();
        foreach (var expected in plan.Fingerprint.Payloads)
        {
            var matching = evidence.Payloads.Where(payload => payload.Identity == expected.Identity).ToArray();
            if (matching.Length != 1) throw Mismatch(); var actual = matching[0];
            if (actual.Type != expected.Type || actual.Id != expected.Id || actual.Extent.RelativeSource != expected.Extent.RelativeSource ||
                actual.Extent.Offset != expected.Extent.Offset || actual.Extent.Length != expected.Extent.Length ||
                actual.Extent.PayloadSha256 is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit)) throw Mismatch();
        }
    }

    private static ArchiveException Mismatch() => new("TextureSourceMismatch", "The texture source does not match its export plan.");
}
