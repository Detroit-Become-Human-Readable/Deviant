using Deviant.Assets;

namespace Deviant.Browser;

public sealed partial class CharacterCatalogSession
{
    public async Task<(CharacterCatalogAssetPage Page, long Total)> QueryPreparedPageAsync(
        AssetQuery query, bool models, CancellationToken token = default)
    {
        ValidateExpectedRevision(Revision);
        ValidateCurrentSourceStamps();
        await Task.Run(() => RepairPendingAliasRefreshes(token), token).ConfigureAwait(false);
        using var retained = new ReservationGroup();
        retained.Add(memoryBudget.Reserve(checked(query.Limit * 4096L), "character page preparation"));
        IReadOnlyList<AssetDescriptor> rows = [];
        long total = 0;
        if (features is { } store)
        {
            if (models)
            { var result = await store.QueryCharacterModelsAsync(query, token).ConfigureAwait(false); rows = result.Models; total = result.Total; }
            else
            { var result = await store.QueryCharacterGroupsAsync(query, token).ConfigureAwait(false); rows = result.Groups; total = result.Total; }
        }
        token.ThrowIfCancellationRequested();
        ValidateCurrentSourceStamps();
        var textBytes = rows.Sum(row => (row.Label.Length + (row.SearchTerms?.Length ?? 0) +
            row.Names.Sum(name => name.Text.Length + name.Source.Length) + row.Metadata.Sum(pair => pair.Key.Length + pair.Value.Length)) * 8L);
        if (textBytes > query.Limit * 4096L)
            retained.Add(memoryBudget.Reserve(textBytes - query.Limit * 4096L, "character page text"));
        return (new((models ? rows.Select(ApplyCurrentAlias) : rows).ToArray(), retained.Detach()), total);
    }
}
