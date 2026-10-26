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

            var output = ReadBoundedAssetFeatureRows<TPayload>(read, transaction, query.Family, identities,
                featurePrefix, featureVersion, maximumPayloadBytes, skipMissing: false, cancellation);
            return new BoundedAssetFeaturePage<TPayload>(total, output);
        });
    }

    public IReadOnlyList<BoundedAssetFeatureRow<TPayload>> ReadBoundedAssetFeaturesByIdentity<TPayload>(
        string family, IReadOnlyList<ResourceIdentity> identities, string featurePrefix, string featureVersion,
        int maximumPayloadBytes, CancellationToken token = default) where TPayload : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentException.ThrowIfNullOrWhiteSpace(featurePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(featureVersion);
        if (maximumPayloadBytes is < 1 or > MaximumLinkedFeaturePageBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));

        var keys = BoundedAssetIdentityKeys(identities, token);
        if (keys.Count == 0) return Array.Empty<BoundedAssetFeatureRow<TPayload>>();

        return WithLinkedRead(token, (read, transaction, cancellation) =>
            ReadBoundedAssetFeatureRows<TPayload>(read, transaction, family, keys, featurePrefix, featureVersion,
                maximumPayloadBytes, skipMissing: true, cancellation));
    }

    /// <summary>
    /// Reads retained source evidence whose browse projection dependency epochs may have advanced.
    /// Callers must validate the typed payload's identity, source and domain before retaining facts;
    /// these rows do not establish that the linked browse projection is current.
    /// </summary>
    public IReadOnlyList<BoundedAssetFeatureRow<TPayload>> ReadRetainedLinkedFeatureEvidence<TPayload>(
        LinkedFeatureScope scope, IReadOnlyList<ResourceIdentity> identities, int maxPayloadBytes,
        CancellationToken token = default) where TPayload : class
    {
        ValidateLinkedScope(scope);
        if (maxPayloadBytes is < 1 or > MaximumLinkedFeaturePageBytes)
            throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
        var keys = BoundedAssetIdentityKeys(identities, token);
        if (keys.Count == 0) return Array.Empty<BoundedAssetFeatureRow<TPayload>>();

        return WithLinkedRead<IReadOnlyList<BoundedAssetFeatureRow<TPayload>>>(token, (read, transaction, cancellation) =>
        {
            using var head = LinkedCommand(read, transaction, scope, """
                SELECT generation FROM linked_feature_heads
                WHERE revision=$revision AND projection_key=$key AND projection_version=$version
                    AND collection=$collection AND collection_version=$collectionVersion AND family=$family
                    AND generation>0
                """);
            var generation = head.ExecuteScalar();
            cancellation.ThrowIfCancellationRequested();
            if (generation is null) return Array.Empty<BoundedAssetFeatureRow<TPayload>>();
            return ReadBoundedAssetFeatureRows<TPayload>(read, transaction, scope.Family, keys,
                string.Empty, scope.CollectionVersion, maxPayloadBytes, skipMissing: true, cancellation,
                scope, Convert.ToInt64(generation));
        });
    }

    private static IReadOnlyList<string> BoundedAssetIdentityKeys(
        IReadOnlyList<ResourceIdentity> identities, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(identities);
        if (identities.Count > MaximumLinkedFeaturePageRows)
            throw new ArgumentOutOfRangeException(nameof(identities));
        var keys = new List<string>(identities.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentException.ThrowIfNullOrWhiteSpace(identity.IndexContentId);
            if (identity.Ordinal < 0) throw new ArgumentOutOfRangeException(nameof(identities));
            if (seen.Add(identity.Key)) keys.Add(identity.Key);
        }
        token.ThrowIfCancellationRequested();
        return keys;
    }

    private IReadOnlyList<BoundedAssetFeatureRow<TPayload>> ReadBoundedAssetFeatureRows<TPayload>(
        SqliteConnection read, SqliteTransaction transaction, string family, IReadOnlyList<string> identities,
        string featurePrefix, string featureVersion, int maximumPayloadBytes, bool skipMissing,
        CancellationToken cancellation, LinkedFeatureScope? retainedScope = null, long retainedGeneration = 0)
        where TPayload : class
    {
        using var command = read.CreateCommand(); command.Transaction = transaction;
        string featureJoin = retainedScope is null
            ? "LEFT JOIN asset_features f ON f.revision=a.revision AND f.feature=$prefix||a.identity"
            : """
                JOIN linked_feature_links l ON l.revision=a.revision AND l.identity=a.identity
                    AND l.projection_key=$projectionKey AND l.projection_version=$projectionVersion
                    AND l.generation=$generation
                    AND l.feature_key>=$collection||'/item/' AND l.feature_key<$collection||'/item0'
                LEFT JOIN asset_features f ON f.revision=l.revision AND f.feature=l.feature_key
                """;
        command.CommandText = $"""
            SELECT length(CAST(a.payload AS BLOB)),
                CASE WHEN length(CAST(a.payload AS BLOB))<=$pageMaximum THEN a.payload ELSE NULL END,
                a.lookup_key,f.version,length(CAST(f.payload AS BLOB)),
                CASE WHEN f.version=$version AND length(CAST(f.payload AS BLOB))<=$maximum THEN f.payload ELSE NULL END
            FROM assets_v2 a {featureJoin}
            WHERE a.revision=$revision AND a.family=$family AND a.identity=$identity
            """;
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$family", family);
        if (retainedScope is null) command.Parameters.AddWithValue("$prefix", featurePrefix);
        else
        {
            command.Parameters.AddWithValue("$projectionKey", retainedScope.Key);
            command.Parameters.AddWithValue("$projectionVersion", retainedScope.Version);
            command.Parameters.AddWithValue("$collection", retainedScope.Collection);
            command.Parameters.AddWithValue("$generation", retainedGeneration);
        }
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
            if (!row.Read())
            {
                if (skipMissing) continue;
                throw new InvalidDataException("BoundedAssetPageLimit");
            }
            if (row.IsDBNull(0) || row.IsDBNull(1))
                throw new InvalidDataException("BoundedAssetPageLimit");
            long retained = row.GetInt64(0);
            if (retained < 1 || retained > MaximumLinkedFeaturePageBytes - bytes)
                throw new InvalidDataException("BoundedAssetPageLimit");
            var asset = LinkedJson<AssetDescriptor>(row.GetString(1));
            if (asset.Identity is null || asset.Identity.Key != key || asset.Family != family || asset.Revision is null ||
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
        return output.AsReadOnly();
    }
}
