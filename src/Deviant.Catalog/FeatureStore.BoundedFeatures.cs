using System.Text.Json;
using Deviant.Assets;
using Deviant.Core;
using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

/// <summary>A feature payload serialized and size-checked before acquiring the catalog writer lock.</summary>
public sealed class BoundedFeatureWrite
{
    private BoundedFeatureWrite(string key, string version, string payload, int utf8ByteCount)
    { Key = key; Version = version; Payload = payload; Utf8ByteCount = utf8ByteCount; }

    public string Key { get; }
    public string Version { get; }
    public int Utf8ByteCount { get; }
    internal string Payload { get; }

    public static BoundedFeatureWrite Create<T>(string key, string version, T value, int maximumPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPayloadBytes);
        if (value is null) throw new ArgumentNullException(nameof(value));
        var payload = BoundedFeatureJson.Serialize(value, maximumPayloadBytes);
        return new(key, version, payload, System.Text.Encoding.UTF8.GetByteCount(payload));
    }
}

/// <summary>Pairs a bounded evidence feature with the asset row it publishes.</summary>
public sealed record BoundedFeatureAssetWrite(BoundedFeatureWrite Feature, AssetDescriptor Asset);

public sealed partial class FeatureStore
{
    public const int BoundedAssetPublicationLimit = 128;
    public const int MaximumBoundedFeatureBatchBytes = 32 * 1024 * 1024;
    private const string BoundedFeatureUpsertSql = """
        INSERT INTO asset_features(revision,feature,version,payload) VALUES($revision,$key,$version,$payload)
        ON CONFLICT(revision,feature) DO UPDATE SET version=excluded.version,payload=excluded.payload
        WHERE asset_features.version!=excluded.version OR asset_features.payload!=excluded.payload
        """;

    /// <summary>Loads one feature only when its stored UTF-8 JSON is within the supplied byte limit.</summary>
    public T? LoadBounded<T>(string key, string version, int maxPayloadBytes, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadBytes);
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            using (var size = connection.CreateCommand())
            {
                size.Transaction = transaction;
                size.CommandText = "SELECT length(CAST(payload AS BLOB)) FROM asset_features WHERE revision=$revision AND feature=$key AND version=$version";
                size.Parameters.AddWithValue("$revision", revision);
                size.Parameters.AddWithValue("$key", key);
                size.Parameters.AddWithValue("$version", version);
                var rawLength = size.ExecuteScalar();
                if (rawLength is null or DBNull)
                {
                    transaction.Commit();
                    return default;
                }

                var length = Convert.ToInt64(rawLength, System.Globalization.CultureInfo.InvariantCulture);
                if (length < 0 || length > maxPayloadBytes)
                    throw new InvalidDataException("The cached feature exceeds its configured size limit.");
            }

            token.ThrowIfCancellationRequested();
            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT payload FROM asset_features WHERE revision=$revision AND feature=$key AND version=$version";
            read.Parameters.AddWithValue("$revision", revision);
            read.Parameters.AddWithValue("$key", key);
            read.Parameters.AddWithValue("$version", version);
            var payload = read.ExecuteScalar() as string;
            if (payload is null)
                throw new InvalidDataException("The cached feature payload is invalid.");

            T? value;
            try { value = JsonSerializer.Deserialize<T>(payload); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            { throw new InvalidDataException("The cached feature JSON is invalid.", ex); }
            if (value is null)
                throw new InvalidDataException("The cached feature JSON contains a null value.");

            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return value;
        });
    }

    /// <summary>Serializes a feature within a UTF-8 byte limit before atomically replacing its cached value.</summary>
    public void SaveBounded<T>(string key, string version, T value, int maxPayloadBytes, CancellationToken token = default)
    {
        SaveBoundedBatch([BoundedFeatureWrite.Create(key, version, value, maxPayloadBytes)], token);
    }

    /// <summary>Commits already bounded feature payloads in one transaction.</summary>
    public void SaveBoundedBatch(IReadOnlyList<BoundedFeatureWrite> writes, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0) return;
        ValidateBoundedFeatureWrites(writes);
        token.ThrowIfCancellationRequested();

        lock (gate) CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var transaction = connection.BeginTransaction();
            using var write = connection.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = BoundedFeatureUpsertSql;
            write.Parameters.AddWithValue("$revision", revision);
            var key = write.Parameters.Add("$key", SqliteType.Text);
            var version = write.Parameters.Add("$version", SqliteType.Text);
            var payload = write.Parameters.Add("$payload", SqliteType.Text);
            foreach (var item in writes)
            {
                token.ThrowIfCancellationRequested();
                key.Value = item.Key; version.Value = item.Version; payload.Value = item.Payload;
                write.ExecuteNonQuery();
            }
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        });
    }

    /// <summary>Publishes bounded feature evidence and its asset projections in the same transaction.</summary>
    /// <returns>Identities whose evidence and asset family passed the atomic ownership check.</returns>
    public IReadOnlySet<ResourceIdentity> PublishBoundedAssetsBatch(
        IReadOnlyList<BoundedFeatureAssetWrite> writes, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0) return new HashSet<ResourceIdentity>();
        if (writes.Count > BoundedAssetPublicationLimit) throw new ArgumentOutOfRangeException(nameof(writes));
        var totalBytes = ValidateBoundedAssetWrites(writes);
        token.ThrowIfCancellationRequested();

        var preparedAssets = new PreparedBoundedAssetRow[writes.Count];
        for (var index = 0; index < writes.Count; index++)
        {
            preparedAssets[index] = PrepareBoundedAssetRow(writes[index].Asset);
            totalBytes = checked(totalBytes + System.Text.Encoding.UTF8.GetByteCount(preparedAssets[index].Payload));
            if (totalBytes > MaximumBoundedFeatureBatchBytes) throw new InvalidDataException("The bounded publication batch exceeds its configured size limit.");
        }
        var persisted = new HashSet<ResourceIdentity>();
        lock (gate) CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            InitializeCharacterProjectionForAssetWrites();
            using var transaction = connection.BeginTransaction();
            var existing = ReadBoundedAssetStates(preparedAssets, transaction);
            var accepted = new List<int>(writes.Count);
            for (var index = 0; index < writes.Count; index++)
            {
                var item = writes[index];
                if (existing.TryGetValue(item.Asset.Identity.Key, out var prior) &&
                    !string.Equals(prior.Family, item.Asset.Family, StringComparison.Ordinal))
                    continue;
                accepted.Add(index);
            }

            using var feature = connection.CreateCommand(); feature.Transaction = transaction;
            feature.CommandText = BoundedFeatureUpsertSql;
            feature.Parameters.AddWithValue("$revision", revision);
            var featureKey = feature.Parameters.Add("$key", SqliteType.Text);
            var featureVersion = feature.Parameters.Add("$version", SqliteType.Text);
            var featurePayload = feature.Parameters.Add("$payload", SqliteType.Text);
            foreach (var index in accepted)
            {
                token.ThrowIfCancellationRequested();
                var item = writes[index].Feature;
                featureKey.Value = item.Key; featureVersion.Value = item.Version; featurePayload.Value = item.Payload;
                feature.ExecuteNonQuery();
                persisted.Add(writes[index].Asset.Identity);
            }

            var assetIndexes = new List<int>(accepted.Count);
            foreach (var index in accepted)
            {
                var asset = writes[index].Asset;
                var prior = existing.GetValueOrDefault(asset.Identity.Key);
                if (PreserveExistingCompleteModel(asset, prior))
                {
                    if (prior!.ProjectionExists) continue;
                    var repaired = PreparePreservedProjectionRepair(asset.Identity, transaction);
                    totalBytes = checked(totalBytes - System.Text.Encoding.UTF8.GetByteCount(preparedAssets[index].Payload) +
                        System.Text.Encoding.UTF8.GetByteCount(repaired.Payload));
                    if (totalBytes > MaximumBoundedFeatureBatchBytes)
                        throw new InvalidDataException("The bounded projection repair batch exceeds its configured size limit.");
                    preparedAssets[index] = repaired;
                }
                else if (prior is { AssetMatches: true } &&
                    (asset.Family == "character-model" ? prior.ProjectionExists : !prior.ProjectionExists))
                    continue;
                assetIndexes.Add(index);
            }
            StoreBoundedAssetRows(preparedAssets, assetIndexes, existing, transaction, token);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            if (assetIndexes.Count > 0) counts.Clear();
            return true;
        });
        return persisted;
    }

    private static void ValidateBoundedFeatureWrites(IReadOnlyList<BoundedFeatureWrite> writes)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var write in writes)
        {
            ArgumentNullException.ThrowIfNull(write);
            if (!keys.Add(write.Key)) throw new ArgumentException("A feature key may appear only once in a transaction.", nameof(writes));
            bytes = checked(bytes + write.Utf8ByteCount);
        }
        if (bytes > MaximumBoundedFeatureBatchBytes) throw new InvalidDataException("The bounded feature batch exceeds its configured size limit.");
    }

    private static long ValidateBoundedAssetWrites(IReadOnlyList<BoundedFeatureAssetWrite> writes)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var write in writes)
        {
            ArgumentNullException.ThrowIfNull(write);
            ArgumentNullException.ThrowIfNull(write.Feature);
            ArgumentNullException.ThrowIfNull(write.Asset);
            if (!keys.Add(write.Feature.Key)) throw new ArgumentException("A feature key may appear only once in a transaction.", nameof(writes));
            if (!identities.Add(write.Asset.Identity.Key)) throw new ArgumentException("An asset identity may appear only once in a transaction.", nameof(writes));
            bytes = checked(bytes + write.Feature.Utf8ByteCount);
        }
        return bytes;
    }

    private PreparedBoundedAssetRow PrepareBoundedAssetRow(AssetDescriptor asset)
    {
        var assetRevision = SourceIdentity.DerivedCacheKey(asset.Revision.IndexContentId, asset.Revision.PhysicalSourceStamp);
        if (!string.Equals(assetRevision, revision, StringComparison.Ordinal))
            throw new ArchiveException("SourceRevisionMismatch", "The asset does not belong to this feature store revision.");
        var payload = BoundedFeatureJson.Serialize(asset, MaximumBoundedFeatureBatchBytes);
        var search = asset.SearchTerms ?? string.Join('\n', asset.Names.Select(name => name.Text).Prepend(asset.Label).Append(asset.Identity.Key));
        return new(asset, payload, search, search.ToUpperInvariant(), asset.SortKey ?? NaturalOrder.Key(asset.Label));
    }

    private Dictionary<string, ExistingBoundedAssetState> ReadBoundedAssetStates(
        IReadOnlyList<PreparedBoundedAssetRow> prepared, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "WITH incoming(identity,payload,search,search_fold,sort_key,lookup_key) AS (VALUES " +
            string.Join(",", Enumerable.Range(0, prepared.Count).Select(index =>
                $"($identity{index},$payload{index},$search{index},$fold{index},$sort{index},$lookup{index})")) + ") " +
            "SELECT a.identity,a.family,a.completeness,COALESCE(p.model_kind,''),COALESCE(p.actor,''),COALESCE(p.name_profile,''),COALESCE(p.classification_profile,'')," +
            "a.payload=i.payload AND a.search=i.search AND a.search_fold=i.search_fold AND a.sort_key=i.sort_key AND a.lookup_key IS i.lookup_key,p.identity IS NOT NULL " +
            "FROM assets_v2 a LEFT JOIN character_projection_models p ON p.revision=a.revision AND p.identity=a.identity " +
            "JOIN incoming i ON i.identity=a.identity WHERE a.revision=$revision";
        command.Parameters.AddWithValue("$revision", revision);
        for (var index = 0; index < prepared.Count; index++)
        {
            var item = prepared[index];
            command.Parameters.AddWithValue("$identity" + index, item.Asset.Identity.Key);
            command.Parameters.AddWithValue("$payload" + index, item.Payload);
            command.Parameters.AddWithValue("$search" + index, item.Search);
            command.Parameters.AddWithValue("$fold" + index, item.SearchFold);
            command.Parameters.AddWithValue("$sort" + index, item.SortKey);
            command.Parameters.AddWithValue("$lookup" + index, (object?)item.Asset.LookupKey ?? DBNull.Value);
        }
        var found = new Dictionary<string, ExistingBoundedAssetState>(StringComparer.Ordinal);
        using var rows = command.ExecuteReader();
        while (rows.Read())
            found[rows.GetString(0)] = new(rows.GetString(1), (AssetCompleteness)rows.GetInt32(2), rows.GetString(3),
                rows.GetString(4), rows.GetString(5), rows.GetString(6), rows.GetBoolean(7), rows.GetBoolean(8));
        return found;
    }

    private PreparedBoundedAssetRow PreparePreservedProjectionRepair(ResourceIdentity identity, SqliteTransaction transaction)
    {
        using var read = connection.CreateCommand(); read.Transaction = transaction;
        read.CommandText = "SELECT payload,search,lookup_key FROM assets_v2 WHERE revision=$revision AND identity=$identity " +
            "AND length(CAST(payload AS BLOB))+length(CAST(search AS BLOB))<=$maximum";
        read.Parameters.AddWithValue("$revision", revision); read.Parameters.AddWithValue("$identity", identity.Key);
        read.Parameters.AddWithValue("$maximum", MaximumBoundedFeatureBatchBytes);
        using var row = read.ExecuteReader();
        if (!row.Read()) throw new InvalidDataException("The preserved model payload is unavailable or exceeds its configured size limit.");
        AssetDescriptor asset;
        try { asset = JsonSerializer.Deserialize<AssetDescriptor>(row.GetString(0))
                ?? throw new InvalidDataException("The preserved model payload is invalid."); }
        catch (JsonException error) { throw new InvalidDataException("The preserved model payload is invalid.", error); }
        if (asset.Identity != identity || asset.Family != "character-model")
            throw new InvalidDataException("The preserved model payload does not match its catalog row.");
        return PrepareBoundedAssetRow(asset with { SearchTerms = row.GetString(1), LookupKey = row.IsDBNull(2) ? null : row.GetString(2) });
    }

    private static bool PreserveExistingCompleteModel(AssetDescriptor incoming, ExistingBoundedAssetState? existing)
    {
        if (existing is null || existing.Completeness != AssetCompleteness.Complete || incoming.Family != "character-model")
            return false;
        var incomingKind = incoming.Metadata.GetValueOrDefault("model.kind") ?? "unclassified";
        return incomingKind == "unclassified" || incoming.Completeness != AssetCompleteness.Complete;
    }

    private void StoreBoundedAssetRows(PreparedBoundedAssetRow[] prepared, IReadOnlyList<int> indexes,
        IReadOnlyDictionary<string, ExistingBoundedAssetState> existing, SqliteTransaction transaction, CancellationToken token)
    {
        if (indexes.Count == 0) return;
        var touchedGroups = new HashSet<(string Actor, string Profile, string Classification)>();
        var repairedGroups = new HashSet<(string Actor, string Profile, string Classification)>();
        using var upsert = connection.CreateCommand(); upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO assets_v2(revision,identity,family,label,search,search_fold,sort_key,bank,language,codec,duration,channels,availability,completeness,payload,lookup_key)
            VALUES($revision,$identity,$family,$label,$search,$fold,$sort,$bank,$language,$codec,$duration,$channels,$availability,$completeness,$payload,$lookup)
            ON CONFLICT(revision,identity) DO UPDATE SET family=excluded.family,label=excluded.label,search=excluded.search,
                search_fold=excluded.search_fold,sort_key=excluded.sort_key,bank=excluded.bank,language=excluded.language,codec=excluded.codec,
                duration=excluded.duration,channels=excluded.channels,availability=excluded.availability,completeness=excluded.completeness,payload=excluded.payload,lookup_key=excluded.lookup_key
            """;
        upsert.Parameters.AddWithValue("$revision", revision);
        foreach (var name in new[] { "identity", "family", "label", "search", "fold", "sort", "bank", "language", "codec", "duration", "channels", "availability", "completeness", "payload", "lookup" })
            upsert.Parameters.AddWithValue("$" + name, DBNull.Value);

        using var row = connection.CreateCommand(); row.Transaction = transaction;
        row.CommandText = "SELECT row_id FROM assets_v2 WHERE revision=$revision AND identity=$identity";
        row.Parameters.AddWithValue("$revision", revision);
        row.Parameters.Add("$identity", SqliteType.Text);

        foreach (var index in indexes)
        {
            token.ThrowIfCancellationRequested();
            var item = prepared[index]; var asset = item.Asset;
            if (existing.TryGetValue(asset.Identity.Key, out var old) && old.Actor.Length > 0)
                touchedGroups.Add((old.Actor, old.NameProfile, old.ClassificationProfile));

            upsert.Parameters["$identity"].Value = asset.Identity.Key;
            upsert.Parameters["$family"].Value = asset.Family; upsert.Parameters["$label"].Value = asset.Label;
            upsert.Parameters["$search"].Value = item.Search; upsert.Parameters["$fold"].Value = item.SearchFold;
            upsert.Parameters["$sort"].Value = item.SortKey;
            upsert.Parameters["$bank"].Value = (object?)asset.GroupKey ?? DBNull.Value;
            upsert.Parameters["$language"].Value = (object?)asset.Language ?? DBNull.Value;
            upsert.Parameters["$codec"].Value = (object?)asset.Codec ?? DBNull.Value;
            upsert.Parameters["$duration"].Value = (object?)asset.DurationSeconds ?? DBNull.Value;
            upsert.Parameters["$channels"].Value = (object?)asset.Channels ?? DBNull.Value;
            upsert.Parameters["$availability"].Value = (int)asset.Availability;
            upsert.Parameters["$completeness"].Value = (int)asset.Completeness;
            upsert.Parameters["$payload"].Value = item.Payload;
            upsert.Parameters["$lookup"].Value = (object?)asset.LookupKey ?? DBNull.Value;
            upsert.ExecuteNonQuery();

            if (characterProjectionWritesEnabled)
            {
                row.Parameters["$identity"].Value = asset.Identity.Key;
                var rowId = (long)row.ExecuteScalar()!;
                RefreshCharacterProjection(asset, rowId, transaction, rebuildGroups: false);
                if (asset.Family == "character-model" && CharacterProjectionActorKey(asset) is { Length: > 0 } actor)
                {
                    var key = (actor, asset.Metadata.GetValueOrDefault("model.name-profile") ?? "",
                        asset.Metadata.GetValueOrDefault("model.classification-profile") ?? "");
                    touchedGroups.Add(key);
                    if (existing.TryGetValue(asset.Identity.Key, out var prior) && !prior.ProjectionExists)
                        repairedGroups.Add(key);
                }
            }
        }

        foreach (var key in repairedGroups)
        {
            token.ThrowIfCancellationRequested();
            ReconcileRepairedProjectionClips(key.Actor, key.Profile, key.Classification, transaction);
        }
        foreach (var key in touchedGroups)
        {
            token.ThrowIfCancellationRequested();
            RebuildCharacterProjectionGroup(key.Actor, key.Profile, key.Classification, transaction);
        }
    }

    private void ReconcileRepairedProjectionClips(string actor, string profile, string classification,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM character_projection_clips
            WHERE revision=$revision AND actor=$actor AND name_profile=$profile AND classification_profile=$classification;
            INSERT INTO character_projection_clips(revision,actor,name_profile,classification_profile,clip_identity,ref_count)
            SELECT $revision,$actor,$profile,$classification,j.value,COUNT(DISTINCT m.identity)
            FROM character_projection_models m,
                json_each(CASE WHEN json_valid(json_extract(m.payload,'$.Metadata."model.animation-identities"'))
                    THEN json_extract(m.payload,'$.Metadata."model.animation-identities"') ELSE '[]' END) j
            WHERE m.revision=$revision AND m.actor=$actor AND m.name_profile=$profile
                AND m.classification_profile=$classification AND m.model_kind='character'
                AND j.type='text' AND j.value!=''
            GROUP BY j.value
            """;
        command.Parameters.AddWithValue("$revision", revision); command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$profile", profile); command.Parameters.AddWithValue("$classification", classification);
        command.ExecuteNonQuery();
    }

    private sealed record PreparedBoundedAssetRow(AssetDescriptor Asset, string Payload, string Search, string SearchFold, string SortKey);
    private sealed record ExistingBoundedAssetState(string Family, AssetCompleteness Completeness, string ModelKind,
        string Actor, string NameProfile, string ClassificationProfile, bool AssetMatches, bool ProjectionExists);
}
