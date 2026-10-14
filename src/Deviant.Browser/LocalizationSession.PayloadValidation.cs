using Deviant.Assets;
using Deviant.Core;
using Deviant.Formats.Localization.Model;

namespace Deviant.Browser;

public sealed partial class LocalizationSession
{
    public async Task<LocalizationPayloadVerificationLease> AcquirePayloadVerificationAsync(
        IEnumerable<ResourceIdentity> sourceResources, CancellationToken token = default,
        LocalizationPayloadVerificationLease? verifiedIndexLease = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sourceResources);
        var identities = sourceResources.DistinctBy(identity => identity.Key).ToArray();
        var rows = new List<ResourceRecord>(identities.Length);
        var digests = new Dictionary<long, string>();
        await featureGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            foreach (var identity in identities)
            {
                if (identity.IndexContentId != snapshot.IndexContentId || identity.MemberPath is not null ||
                    identity.Ordinal < 0 || identity.Ordinal >= snapshot.Records.Count || snapshot.Records[(int)identity.Ordinal].Type != 1016)
                    throw new ArchiveException("InvalidIdentity", "The text source does not belong to this archive.");
                LocalizationInputDigest? input;
                lock (stateGate) inputDigests.TryGetValue(identity.Ordinal, out input);
                if (input is not { Status: LocalizationInputStatus.Read, Sha256: { } digest })
                    throw new ArchiveException("LocalizationDigestUnavailable", "Reopen the archive to verify this text source.");
                rows.Add(snapshot.Records[(int)identity.Ordinal]);
                digests[identity.Ordinal] = digest;
            }
        }
        finally { featureGate.Release(); }
        var lease = await new LocalizationPayloadVerifier().VerifyAsync(snapshot, digests, rows, sourceRevisions, token, verifiedIndexLease).ConfigureAwait(false);
        if (lease.DigestsMatch) return lease;
        await lease.DisposeAsync().ConfigureAwait(false);
        throw new ArchiveException("SourceChanged", "The text source changed. Reopen the archive.");
    }

    public async Task ValidateAudioLookupPointerSourcesAsync(IEnumerable<ResourceIdentity> sources,
        CancellationToken token = default)
    {
        var owners = sources.Select(identity => new ResourceIdentity(identity.IndexContentId, identity.Ordinal)).DistinctBy(identity => identity.Key);
        await using var lease = await AcquirePayloadVerificationAsync(owners, token).ConfigureAwait(false);
        lease.EnsureUnchanged();
    }
}
