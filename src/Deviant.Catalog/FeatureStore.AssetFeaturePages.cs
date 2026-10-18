using Deviant.Assets;
using Deviant.Core;
using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

public sealed record BoundedAssetFeatureRow<TPayload>(AssetDescriptor Asset, TPayload? Feature,
    long SerializedBytes, string? ErrorCode) where TPayload : class;
public sealed record BoundedAssetFeaturePage<TPayload>(long Total,
    IReadOnlyList<BoundedAssetFeatureRow<TPayload>> Rows) where TPayload : class;

public sealed partial class FeatureStore
{
    public BoundedAssetFeaturePage<TPayload> ReadBoundedAssetFeaturePage<TPayload>(AssetQuery query,
        string featurePrefix, string featureVersion, int maximumPayloadBytes, CancellationToken token = default)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Family);
        ArgumentException.ThrowIfNullOrWhiteSpace(featurePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(featureVersion);
        if (query.Offset < 0 || query.Limit is < 1 or > MaximumLinkedFeaturePageRows ||
            maximumPayloadBytes is < 1 or > MaximumLinkedFeaturePageBytes)
            throw new ArgumentOutOfRangeException(nameof(query));

        return WithLinkedRead(token, (read, transaction, cancellation) =>
        {
            long total;
            using (var count = AssetCommand(query, "COUNT(*)", read, transaction, allowNameIndex: false))
                total = Convert.ToInt64(count.ExecuteScalar());
            AssetLoadDiagnostics.Counters("BoundedFeatures.ReadSnapshot", featurePrefix,
                new Dictionary<string, long> { ["Rows"] = total });
            cancellation.ThrowIfCancellationRequested();
            var identities = new List<string>();
            using (var page = AssetPageCommand(query, "identity", read, transaction, prepareIndexes: false))
            using (var rows = page.ExecuteReader())
                while (rows.Read()) { cancellation.ThrowIfCancellationRequested(); identities.Add(rows.GetString(0)); }

            using var command = read.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                SELECT length(CAST(a.payload AS BLOB)),
                    CASE WHEN length(CAST(a.payload AS BLOB))<=$pageMaximum THEN a.payload ELSE NULL END,
                    a.lookup_key,f.version,length(CAST(f.payload AS BLOB)),
                    CASE WHEN f.version=$version AND length(CAST(f.payload AS BLOB))<=$maximum THEN f.payload ELSE NULL END
                FROM assets_v2 a LEFT JOIN asset_features f ON f.revision=a.revision AND f.feature=$prefix||a.identity
                WHERE a.revision=$revision AND a.family=$family AND a.identity=$identity
                """;
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$family", query.Family);
            command.Parameters.AddWithValue("$prefix", featurePrefix);
            command.Parameters.AddWithValue("$version", featureVersion);
            command.Parameters.AddWithValue("$maximum", maximumPayloadBytes);
            command.Parameters.AddWithValue("$pageMaximum", MaximumLinkedFeaturePageBytes);
            var identity = command.Parameters.Add("$identity", SqliteType.Text);
            var output = new List<BoundedAssetFeatureRow<TPayload>>(identities.Count);
            long bytes = 0;
            foreach (string key in identities)
            {
                cancellation.ThrowIfCancellationRequested(); identity.Value = key;
                using var row = command.ExecuteReader();
                if (!row.Read() || row.IsDBNull(0) || row.IsDBNull(1))
                    throw new InvalidDataException("BoundedAssetPageLimit");
                long retained = row.GetInt64(0);
                if (retained < 1 || retained > MaximumLinkedFeaturePageBytes - bytes)
                    throw new InvalidDataException("BoundedAssetPageLimit");
                var asset = LinkedJson<AssetDescriptor>(row.GetString(1));
                if (asset.Identity is null || asset.Identity.Key != key || asset.Family != query.Family || asset.Revision is null ||
                    SourceIdentity.DerivedCacheKey(asset.Revision.IndexContentId, asset.Revision.PhysicalSourceStamp) != revision)
                    throw new InvalidDataException("BoundedAssetInvalid");
                asset = asset with { LookupKey = row.IsDBNull(2) ? null : row.GetString(2) };
                TPayload? feature = null;
                string? error = row.IsDBNull(3) ? "BoundedFeatureMissing" :
                    row.GetString(3) != featureVersion ? "BoundedFeatureVersion" :
                    row.IsDBNull(4) || row.GetInt64(4) < 1 || row.GetInt64(4) > maximumPayloadBytes
                        ? "BoundedFeatureLimit" : null;
                if (error is null)
                {
                    long payloadBytes = row.GetInt64(4);
                    if (payloadBytes > MaximumLinkedFeaturePageBytes - bytes - retained)
                        throw new InvalidDataException("BoundedAssetPageLimit");
                    retained += payloadBytes;
                    try { feature = LinkedJson<TPayload>(row.GetString(5)); }
                    catch (InvalidDataException) { error = "BoundedFeaturePayload"; }
                }
                bytes = checked(bytes + retained);
                output.Add(new(asset, feature, retained, error));
            }
            cancellation.ThrowIfCancellationRequested();
            return new BoundedAssetFeaturePage<TPayload>(total, output.AsReadOnly());
        });
    }
}
