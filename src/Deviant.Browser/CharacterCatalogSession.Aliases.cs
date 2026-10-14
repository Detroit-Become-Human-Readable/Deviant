using Deviant.Assets;
using Deviant.Core;
using System.Globalization;
using System.Text.Json;

namespace Deviant.Browser;

public sealed partial class CharacterCatalogSession
{
    public static AssetDescriptor ApplyModelAnimationMetadata(AssetDescriptor descriptor,
        IReadOnlyList<CharacterModelBindingResult> bindings)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(bindings);
        var identities = bindings.Where(binding => binding.Diagnostic is null &&
                binding.SelectedIdentity == descriptor.Identity && binding.Revision == descriptor.Revision)
            .SelectMany(binding => binding.AnimationLinks.Where(link => link.SourceIdentity == descriptor.Identity &&
                link.AnimationIdentity.IndexContentId == descriptor.Identity.IndexContentId &&
                link.AnimationIdentity.Ordinal == descriptor.Identity.Ordinal && link.AnimationIdentity.MemberPath is not null &&
                link.AnimationVersion == CharacterModelAnimationLinks.SupportedAnimationVersion))
            .Select(link => link.AnimationIdentity).Distinct()
            .OrderBy(identity => identity.Ordinal).ThenBy(identity => identity.MemberPath, StringComparer.Ordinal).ToArray();
        var metadata = descriptor.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        metadata["model.animation-profile"] = CharacterModelAnimationLinks.Profile;
        metadata["model.animation-state"] = identities.Length > 0 ? "partial" : "unknown";
        metadata["model.animation-identities"] = JsonSerializer.Serialize(identities.Select(identity => identity.Key).ToArray());
        if (identities.Length > 0) metadata["model.animation-count"] = identities.Length.ToString(CultureInfo.InvariantCulture);
        else metadata.Remove("model.animation-count");
        return descriptor with { Metadata = metadata };
    }

    private const int MaximumPendingAliasRefreshes = 16;
    private readonly SemaphoreSlim aliasIndexGate = new(1, 1);
    private readonly Dictionary<string, ResourceIdentity> pendingAliasRefreshes = new(StringComparer.Ordinal);

    /// <summary>Changes one selected model alias and refreshes its indexed search row.</summary>
    public async Task SetAliasAsync(ResourceIdentity identity, string? alias,
        SourceRevision expectedRevision, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        ValidateExpectedRevision(expectedRevision);
        if (identity.IndexContentId != snapshot.IndexContentId || identity.MemberPath is null)
            throw new ArchiveException("CharacterAliasTargetInvalid", "The alias target is not a model member in this archive.");
        if (identity.Ordinal < 0 || identity.Ordinal >= snapshot.Records.Count ||
            snapshot.Records[checked((int)identity.Ordinal)] is not { Type: 29, Validity: ResourceValidity.Valid })
            throw new ArchiveException("CharacterAliasTargetInvalid", "The alias target is not in a current valid model container.");
        if (aliases is null || features is null)
            throw new InvalidOperationException("Character aliases require the session AliasStore and FeatureStore.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            EnsureAvailable();
            ValidateExpectedRevision(expectedRevision);
            await aliasIndexGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                EnsureAvailable();
                RepairPendingAliasRefreshesLocked(linked.Token);
                if (!pendingAliasRefreshes.ContainsKey(identity.Key) &&
                    pendingAliasRefreshes.Count >= MaximumPendingAliasRefreshes)
                    throw new InvalidOperationException("Character alias search refresh is full; retry after a query repairs pending rows.");

                using var reservation = memoryBudget.Reserve(
                    checked(MaximumPersistedEntryBytes * 3L), "character alias search refresh");
                ValidateCurrentSourceStamps();
                var descriptor = LoadAliasTarget(identity, expectedRevision, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                ValidateCurrentSourceStamps();
                await aliases!.SetAsync(identity, alias, linked.Token).ConfigureAwait(false);

                // AliasStore is committed at this point. Finish the bounded one-row index update even
                // if the caller cancels now; otherwise a later query could miss the saved alias.
                try
                {
                    features!.UpsertAssets([ApplyCurrentAlias(descriptor)], CancellationToken.None);
                    pendingAliasRefreshes.Remove(identity.Key);
                }
                catch
                {
                    pendingAliasRefreshes[identity.Key] = identity;
                    throw;
                }
                ValidateCurrentSourceStamps();
            }
            finally { aliasIndexGate.Release(); }
        }
        finally { operationGate.Release(); }
    }

    private AssetDescriptor LoadAliasTarget(ResourceIdentity identity, SourceRevision expectedRevision,
        CancellationToken token)
    {
        var descriptor = features!.LoadAssetByIdentity(identity.Key,
            checked((int)MaximumPersistedEntryBytes), token);
        if (descriptor is null || descriptor.Identity != identity || descriptor.Revision != expectedRevision ||
            !string.Equals(descriptor.Family, "character-model", StringComparison.Ordinal))
            throw new ArchiveException("CharacterAliasTargetInvalid",
                "The indexed alias target does not match this model identity and archive revision.");
        return descriptor;
    }

    private void RepairPendingAliasRefreshes(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        aliasIndexGate.Wait(linked.Token);
        try
        {
            EnsureAvailable();
            RepairPendingAliasRefreshesLocked(linked.Token);
        }
        finally { aliasIndexGate.Release(); }
    }

    private void RepairPendingAliasRefreshesLocked(CancellationToken token)
    {
        if (pendingAliasRefreshes.Count == 0) return;
        ValidateCurrentSourceStamps();
        foreach (var pending in pendingAliasRefreshes.ToArray())
        {
            token.ThrowIfCancellationRequested();
            using var reservation = memoryBudget.Reserve(
                checked(MaximumPersistedEntryBytes * 3L), "pending character alias search refresh");
            var descriptor = LoadAliasTarget(pending.Value, Revision, token);
            features!.UpsertAssets([ApplyCurrentAlias(descriptor)], CancellationToken.None);
            pendingAliasRefreshes.Remove(pending.Key);
        }
        ValidateCurrentSourceStamps();
    }
}
