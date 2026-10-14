using System.Globalization;
using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Textures;

namespace Deviant.Browser;

public sealed record NativeTextureSourceEstimate(long RetainedSourceBytes, int Width, int Height,
    int MipCount, bool Converted, byte NativeFormat, int RequiredPayloadCount, int MetadataBytes);

public sealed partial class NativeTextureSession
{
    public async Task<NativeTextureSourceEstimate> EstimateNativeSourceAsync(ResourceIdentity nativeTextureIdentity,
        ResourceIdentity catalogIdentity, NativeContainerLease companion, CancellationToken token = default)
        => await EstimateNativeSourceAsync(nativeTextureIdentity, catalogIdentity, companion, int.MaxValue, token).ConfigureAwait(false);

    public async Task<NativeTextureSourceEstimate> EstimateNativeSourceAsync(ResourceIdentity nativeTextureIdentity,
        ResourceIdentity catalogIdentity, NativeContainerLease companion, int maximumPreviewDimension, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(nativeTextureIdentity);
        ArgumentNullException.ThrowIfNull(catalogIdentity);
        ArgumentNullException.ThrowIfNull(companion);
        ObjectDisposedException.ThrowIf(disposed, this);
        using var own = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        own.Token.ThrowIfCancellationRequested();
        ValidateCompanion(companion);
        using var borrowed = companion.AcquireLease();
        await readGate.WaitAsync(own.Token).ConfigureAwait(false);
        try
        {
            var metadata = await ReadNativeMetadataAsync(nativeTextureIdentity, catalogIdentity, companion, own.Token)
                .ConfigureAwait(false);
            if (metadata is null)
                throw new ArchiveException("TextureMetadataUnavailable", "Texture metadata is unavailable.");
            var record = metadata.Record;
            if (record.References.Any(reference => reference.Resolution.Status != ReferenceStatus.Unique ||
                reference.Resolution.Candidates.Count != 1))
                throw new ArchiveException("AmbiguousNativeTextureReference", "Texture references are missing or ambiguous.");
            if (record.References.Count != Math.Max(1, (int)record.DescriptorCount) ||
                record.References[0].Reference.ResourceType != 2229 ||
                record.References.Skip(1).Any(reference => reference.Reference.ResourceType != 2230))
                throw new TextureFormatException("UnsupportedNativeTextureLayout", "This texture reference layout is unavailable.");
            if (record.Mip0Width == 0 || record.Mip0Height == 0 ||
                record.Mip0Width > DdsTextureReader.MaximumDimension || record.Mip0Height > DdsTextureReader.MaximumDimension ||
                (long)record.Mip0Width * record.Mip0Height > DdsTextureReader.MaximumPixelCount || record.MipSizeCount == 0)
                throw new TextureFormatException("InvalidNativeTextureDimensions", "Texture dimensions or mip count are invalid.");
            var maximumMipCount = 1;
            for (var dimension = Math.Max((int)record.Mip0Width, record.Mip0Height); dimension > 1; dimension >>= 1)
                maximumMipCount++;
            if (record.MipSizeCount > maximumMipCount)
                throw new TextureFormatException("InvalidNativeTextureDimensions", "Texture dimensions or mip count are invalid.");

            long retained = metadata.Bytes.Length;
            var identities = new HashSet<ResourceIdentity>();
            var required = maximumPreviewDimension == int.MaxValue ? null
                : NativeTextureDecoder.PlanPreview(CreateMetadataSource(nativeTextureIdentity, catalogIdentity, companion, metadata),
                    maximumPreviewDimension, own.Token).RequiredPayloadIdentities;
            foreach (var reference in record.References)
            {
                own.Token.ThrowIfCancellationRequested();
                var identity = reference.Resolution.Candidates[0];
                if (identity.IndexContentId != Snapshot.IndexContentId || identity.MemberPath is not null ||
                    reference.Reference.Namespace != IdentifierNamespace.Archive ||
                    !uint.TryParse(reference.Reference.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var nativeId))
                    throw new ArchiveException("TextureSourceMismatch", "The texture payload does not match its metadata.");
                var row = Snapshot.FindRecord(identity.Ordinal);
                if (row is null || row.Type != reference.Reference.ResourceType || row.Id != nativeId)
                    throw new ArchiveException("TextureSourceMismatch", "The texture payload does not match its metadata.");
                if (row.Validity != ResourceValidity.Valid || row.StoredSize < 1 || row.StoredSize > MaximumResourceBytes)
                    throw new ArchiveException("TextureResourceLimit", "The texture source exceeds the supported resource limit.");
                if (required is not null && !required.Contains(identity)) continue;
                if (!identities.Add(identity)) continue;
                if (row.StoredSize > MaximumRetainedSourceBytes - retained)
                    throw new ArchiveException("TextureSourceBudget", "The texture exceeds the source memory budget.");
                retained += row.StoredSize;
            }
            companion.EnsureUnchanged(); own.Token.ThrowIfCancellationRequested();
            return new(retained, record.Mip0Width, record.Mip0Height, record.MipSizeCount,
                record.PostSelectorBoolean, record.FirstByte, identities.Count, metadata.Bytes.Length);
        }
        finally { readGate.Release(); }
    }
}
