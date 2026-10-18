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

    internal async Task<LocalizationAudioSourceVerification> AcquireAudioLookupSourceVerificationAsync(
        LocalizationPayloadVerificationLease verifiedIndexLease,CancellationToken token)
    {
        var rows=new List<ResourceRecord>();
        var digests=new Dictionary<long,string>();
        var unverified=new List<ResourceIdentity>();
        await featureGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            foreach(var resource in Resources)
            {
                token.ThrowIfCancellationRequested();
                LocalizationInputDigest? input;
                lock(stateGate)inputDigests.TryGetValue(resource.Ordinal,out input);
                if(input is { Status:LocalizationInputStatus.Read,Sha256:{ } digest }&&
                   resource.Validity==ResourceValidity.Valid)
                { rows.Add(resource);digests[resource.Ordinal]=digest; }
                else unverified.Add(new(snapshot.IndexContentId,resource.Ordinal));
            }
        }
        finally{featureGate.Release();}
        LocalizationPayloadVerificationLease? lease=null;
        try
        {
            var verifier=new LocalizationPayloadVerifier();
            lease=(sourceRevisions is null or FileRevisionProvider)&&MetadataCacheSourcePolicy.CanReuseStampHead(snapshot)
                ?await verifier.MonitorVolumesAsync(snapshot,rows,token,verifiedIndexLease).ConfigureAwait(false)
                :await verifier.VerifyAsync(snapshot,digests,rows,sourceRevisions,token,verifiedIndexLease).ConfigureAwait(false);
            if(!lease.DigestsMatch)
                throw new ArchiveException("SourceChanged","The text source changed. Reopen the archive.");
            lease.EnsureUnchanged();
            return new(lease,unverified.AsReadOnly());
        }
        catch(ArchiveException error) when(error.Code is "SourceBusy" or "SourceUnavailable")
        {
            if(lease is not null)await lease.DisposeAsync().ConfigureAwait(false);
            unverified.AddRange(rows.Select(resource=>new ResourceIdentity(snapshot.IndexContentId,resource.Ordinal)));
            return new(null,unverified.AsReadOnly());
        }
        catch
        {
            if(lease is not null)await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
    public async Task ValidateAudioLookupPointerSourcesAsync(IEnumerable<ResourceIdentity> sources,
        CancellationToken token = default)
    {
        var owners = sources.Select(identity => new ResourceIdentity(identity.IndexContentId, identity.Ordinal)).DistinctBy(identity => identity.Key);
        await using var lease = await AcquirePayloadVerificationAsync(owners, token).ConfigureAwait(false);
        lease.EnsureUnchanged();
    }
}

internal sealed record LocalizationAudioSourceVerification(LocalizationPayloadVerificationLease? Lease,
    IReadOnlyList<ResourceIdentity> UnverifiedSources);
