using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Catalog;

public sealed record AssetIdentitySelection(SourceRevision Revision, long? FamilyEpoch, long MatchedCount,
    IReadOnlyList<string> IdentityKeys, long RetainedBytes);

public sealed partial class FeatureStore
{
    public const int MaximumAssetSelectionIdentities = 131072;
    public const long MaximumAssetSelectionKeyBytes = 16L * 1024 * 1024;

    public AssetIdentitySelection ReadAssetIdentitySelection(SourceRevision source, AssetQuery query,
        int maximumIdentities = MaximumAssetSelectionIdentities, long maximumKeyBytes = MaximumAssetSelectionKeyBytes,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateFacetSource(source, query.Family!);
        if (maximumIdentities is < 1 or > MaximumAssetSelectionIdentities || maximumKeyBytes is < 1 or > MaximumAssetSelectionKeyBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumIdentities));
        if (!Enum.IsDefined(query.Sort) || query.Sort == AssetSort.Animations || query.ModelKind is not null ||
            query.ModelNameProfile is not null || query.CharacterGroup is not null || query.CachedPayloadFilter is not null)
            throw new ArgumentException("Matching selection accepts scalar filters and sorts.", nameof(query));
        var filter = query with { Offset = 0, Limit = 1 };
        return WithLinkedRead(token, (read, transaction, cancellation) =>
        {
            long? epoch = ReadFacetEpoch(read, transaction, query.Family!);
            long matched;
            using (var count = AssetCommand(filter, "COUNT(*)", read, transaction, allowNameIndex: false))
                matched = (long)count.ExecuteScalar()!;
            if (matched < 0 || matched > maximumIdentities)
                throw SelectionLimit();
            AssetLoadDiagnostics.Counters("AssetSelections.ReadSnapshot", query.Family!, new Dictionary<string, long>
                { ["MatchedCount"] = matched, ["FamilyEpoch"] = epoch ?? -1, ["JsonBytes"] = 0 });
            cancellation.ThrowIfCancellationRequested();
            var keys = new List<string>((int)matched);
            long retainedBytes = 0;
            using (var command = AssetCommand(filter,
                "CASE WHEN length(CAST(identity AS BLOB))<=2048 THEN identity END,length(CAST(identity AS BLOB))", read, transaction,
                allowNameIndex: false))
            {
                command.CommandText += " ORDER BY " + SortOrder(filter);
                using var rows = command.ExecuteReader();
                while (rows.Read())
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (keys.Count == maximumIdentities || rows.IsDBNull(0) || rows.GetInt64(1) is < 1 or > 2048)
                        throw SelectionLimit();
                    var key = rows.GetString(0);
                    long bytes = checked(key.Length * 2L + 32 + IntPtr.Size);
                    if (bytes > maximumKeyBytes - retainedBytes) throw SelectionLimit();
                    retainedBytes += bytes; keys.Add(key);
                }
            }
            if (keys.Count != matched) throw new InvalidDataException("The matching identity count differs from its database snapshot.");
            cancellation.ThrowIfCancellationRequested();
            AssetLoadDiagnostics.Counters("AssetSelections.Complete", query.Family!, new Dictionary<string, long>
                { ["MatchedCount"] = matched, ["FamilyEpoch"] = epoch ?? -1, ["RetainedBytes"] = retainedBytes, ["JsonBytes"] = 0 });
            return new AssetIdentitySelection(source, epoch, matched, keys.AsReadOnly(), retainedBytes);
        });
    }

    private static ArchiveException SelectionLimit() => new("AssetSelectionLimit",
        "The matching identity selection exceeds its supported memory limit. Narrow the filters and try again.");
}
