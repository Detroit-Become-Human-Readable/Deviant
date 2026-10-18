using System.Text.Json;
using Deviant.Assets;
using Deviant.Core;
using Microsoft.Data.Sqlite;

namespace Deviant.Catalog;

public sealed record LinkedFeatureScope(string Key, string Version, string Collection, string CollectionVersion, string Family)
{
    public IReadOnlyList<string> AdditionalFeatures { get; init; } = [];
}
public sealed record LinkedFeatureReference(ResourceIdentity Identity, string FeatureKey);
public sealed record LinkedFeatureDependencies(string Revision, LinkedFeatureScope Scope, long CollectionEpoch, long FamilyEpoch)
{
    public long AdditionalEpoch { get; init; }
}
public sealed record LinkedFeatureHead<THead>(long Generation, THead Head);
public sealed record LinkedFeatureRow<TPayload>(AssetDescriptor Asset, TPayload Feature)
{
    public long SerializedBytes { get; init; }
}
public sealed record LinkedFeaturePage<THead, TPayload>(long Generation, THead Head, long Total, IReadOnlyList<LinkedFeatureRow<TPayload>> Rows);

public sealed partial class FeatureStore
{
    public const int MaximumLinkedFeaturePageRows = 128;
    public const int MaximumLinkedFeaturePageBytes = 32 * 1024 * 1024;
    public const int MaximumLinkedFeatureHeadBytes = 1024 * 1024;
    private readonly object linkedReadGate = new();
    private readonly CancellationTokenSource linkedReadLifetime = new();
    private readonly HashSet<SqliteConnection> linkedReadConnections = [];
    private bool linkedReadDisposed;

    private void InitializeLinkedFeatures()
    {
        using var transaction = connection.BeginTransaction();
        using var schema = connection.CreateCommand(); schema.Transaction = transaction;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS linked_feature_dependencies(revision TEXT NOT NULL,kind INTEGER NOT NULL,dependency_key TEXT NOT NULL,
                epoch INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(revision,kind,dependency_key)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS linked_feature_heads(revision TEXT NOT NULL,projection_key TEXT NOT NULL,projection_version TEXT NOT NULL,
                collection TEXT NOT NULL,collection_version TEXT NOT NULL,family TEXT NOT NULL,generation INTEGER NOT NULL,
                collection_epoch INTEGER NOT NULL,family_epoch INTEGER NOT NULL,row_count INTEGER NOT NULL,payload TEXT NOT NULL,
                additional_features TEXT NOT NULL DEFAULT '[]',additional_epoch INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(revision,projection_key,projection_version)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS linked_feature_links(revision TEXT NOT NULL,projection_key TEXT NOT NULL,projection_version TEXT NOT NULL,
                generation INTEGER NOT NULL,identity TEXT NOT NULL,feature_key TEXT NOT NULL,
                PRIMARY KEY(revision,projection_key,projection_version,identity),UNIQUE(revision,projection_key,projection_version,feature_key)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS linked_feature_schema(version INTEGER PRIMARY KEY);
            """;
        schema.ExecuteNonQuery();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        schema.CommandText = "PRAGMA table_info(linked_feature_heads)";
        using (var rows = schema.ExecuteReader()) while (rows.Read()) columns.Add(rows.GetString(1));
        foreach (var column in new[] { (Name: "additional_features", Type: "TEXT NOT NULL DEFAULT '[]'"), (Name: "additional_epoch", Type: "INTEGER NOT NULL DEFAULT 0") })
            if (!columns.Contains(column.Name))
            { schema.CommandText = "ALTER TABLE linked_feature_heads ADD COLUMN " + column.Name + " " + column.Type; schema.ExecuteNonQuery(); }
        schema.CommandText = "SELECT 1 FROM linked_feature_schema WHERE version=2";
        if (schema.ExecuteScalar() is not null) { transaction.Commit(); return; }
        foreach (string trigger in new[] { "linked_feature_identical_insert", "linked_feature_insert", "linked_feature_delete", "linked_feature_update",
            "linked_asset_insert", "linked_asset_delete", "linked_asset_update" })
        { schema.CommandText = "DROP TRIGGER IF EXISTS " + trigger; schema.ExecuteNonQuery(); }
        schema.CommandText = """
            CREATE TRIGGER linked_feature_identical_insert BEFORE INSERT ON asset_features
                WHEN EXISTS(SELECT 1 FROM asset_features f WHERE f.revision=new.revision AND f.feature=new.feature AND f.version=new.version AND f.payload=new.payload)
                BEGIN SELECT RAISE(IGNORE); END;
            CREATE TRIGGER linked_feature_insert BEFORE INSERT ON asset_features
                WHEN NOT EXISTS(SELECT 1 FROM asset_features f WHERE f.revision=new.revision AND f.feature=new.feature AND f.version=new.version AND f.payload=new.payload)
                BEGIN UPDATE linked_feature_dependencies SET epoch=epoch+1 WHERE revision=new.revision AND
                    ((kind=0 AND new.feature>=dependency_key||'/item/' AND new.feature<dependency_key||'/item0') OR (kind=2 AND dependency_key=new.feature)); END;
            CREATE TRIGGER linked_feature_delete AFTER DELETE ON asset_features BEGIN
                UPDATE linked_feature_dependencies SET epoch=epoch+1 WHERE revision=old.revision AND
                    ((kind=0 AND old.feature>=dependency_key||'/item/' AND old.feature<dependency_key||'/item0') OR (kind=2 AND dependency_key=old.feature)); END;
            CREATE TRIGGER linked_feature_update AFTER UPDATE ON asset_features
                WHEN old.revision IS NOT new.revision OR old.feature IS NOT new.feature OR old.version IS NOT new.version OR old.payload IS NOT new.payload
                BEGIN UPDATE linked_feature_dependencies SET epoch=epoch+1 WHERE
                    ((revision=old.revision AND ((kind=0 AND old.feature>=dependency_key||'/item/' AND old.feature<dependency_key||'/item0') OR (kind=2 AND dependency_key=old.feature))) OR
                     (revision=new.revision AND ((kind=0 AND new.feature>=dependency_key||'/item/' AND new.feature<dependency_key||'/item0') OR (kind=2 AND dependency_key=new.feature)))); END;
            CREATE TRIGGER linked_asset_insert AFTER INSERT ON assets_v2 BEGIN
                UPDATE linked_feature_dependencies SET epoch=epoch+1 WHERE kind=1 AND revision=new.revision AND dependency_key=new.family; END;
            CREATE TRIGGER linked_asset_delete AFTER DELETE ON assets_v2 BEGIN
                UPDATE linked_feature_dependencies SET epoch=epoch+1 WHERE kind=1 AND revision=old.revision AND dependency_key=old.family; END;
            """;
        schema.ExecuteNonQuery();
        string changed = string.Join(" OR ", new[] { "revision", "identity", "family", "label", "search", "search_fold", "sort_key", "bank",
            "language", "codec", "duration", "channels", "availability", "completeness", "payload", "lookup_key" }
            .Select(column => $"old.{column} IS NOT new.{column}"));
        schema.CommandText = $"CREATE TRIGGER linked_asset_update AFTER UPDATE ON assets_v2 WHEN {changed} BEGIN " +
            "UPDATE linked_feature_dependencies SET epoch=epoch+1 WHERE kind=1 AND " +
            "((revision=old.revision AND dependency_key=old.family) OR (revision=new.revision AND dependency_key=new.family)); END;";
        schema.ExecuteNonQuery();
        schema.CommandText = "INSERT INTO linked_feature_schema VALUES(2)"; schema.ExecuteNonQuery(); transaction.Commit();
    }
    public LinkedFeatureDependencies CaptureLinkedFeatureDependencies(LinkedFeatureScope scope, CancellationToken token = default)
    {
        ValidateLinkedScope(scope);
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var transaction = connection.BeginTransaction();
            EnsureLinkedDependencies(scope, transaction);
            var result = LinkedDependencies(scope, transaction);
            token.ThrowIfCancellationRequested(); transaction.Commit(); return result;
        });
    }

    public LinkedFeatureDependencies SaveLinkedFeatureItems<T>(LinkedFeatureScope scope, IEnumerable<T> items, CancellationToken token = default)
    {
        ValidateLinkedScope(scope); ArgumentNullException.ThrowIfNull(items);
        return SaveItemsCore(scope.Collection, scope.CollectionVersion, items,
            transaction => LinkedDependencies(scope, transaction), token,
            transaction => { EnsureLinkedDependencies(scope, transaction); return true; });
    }

    public LinkedFeatureDependencies? TrySaveLinkedFeatureItems<T>(LinkedFeatureDependencies expected,
        IEnumerable<T> items, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(items);
        ValidateLinkedScope(expected.Scope);
        if (expected.Revision != revision) throw new InvalidDataException("LinkedFeatureDependenciesInvalid");
        return SaveItemsCore<T, LinkedFeatureDependencies?>(expected.Scope.Collection, expected.Scope.CollectionVersion, items,
            transaction => LinkedDependencies(expected.Scope, transaction), token, transaction =>
            {
                EnsureLinkedDependencies(expected.Scope, transaction);
                return LinkedDependencies(expected.Scope, transaction) == expected;
            });
    }

    private void EnsureLinkedDependencies(LinkedFeatureScope scope, SqliteTransaction transaction)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var create = LinkedCommand(connection, transaction, scope, "INSERT OR IGNORE INTO linked_feature_dependencies(revision,kind,dependency_key) " +
            "VALUES($revision,0,$collection),($revision,1,$family)");
        create.ExecuteNonQuery();
        using var additional = LinkedCommand(connection, transaction, scope,
            "INSERT OR IGNORE INTO linked_feature_dependencies(revision,kind,dependency_key) VALUES($revision,2,$additionalKey)");
        var key = additional.Parameters.Add("$additionalKey", SqliteType.Text);
        foreach (string feature in CanonicalLinkedFeatureKeys(scope)) { key.Value = feature; additional.ExecuteNonQuery(); }
    }
    public LinkedFeatureDependencies SaveLinkedFeatureCheckpoint(LinkedFeatureScope scope, BoundedFeatureWrite checkpoint, CancellationToken token = default)
        => SaveLinkedFeatureCheckpointCore(scope, checkpoint, null, token)!;

    public LinkedFeatureDependencies? TrySaveLinkedFeatureCheckpoint(LinkedFeatureDependencies expected,
        BoundedFeatureWrite checkpoint, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Revision != revision) throw new InvalidDataException("LinkedFeatureDependenciesInvalid");
        return SaveLinkedFeatureCheckpointCore(expected.Scope, checkpoint, expected, token);
    }

    public LinkedFeatureDependencies? ReplaceLinkedFeatureAssets(LinkedFeatureDependencies expected,
        IEnumerable<AssetDescriptor> assets, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(assets);
        ValidateLinkedScope(expected.Scope);
        if (expected.Revision != revision) throw new InvalidDataException("LinkedFeatureDependenciesInvalid");
        return StoreAssetsCore<LinkedFeatureDependencies?>(assets, expected.Scope.Family,
            transaction => LinkedDependencies(expected.Scope, transaction), token, transaction =>
            {
                EnsureLinkedDependencies(expected.Scope, transaction);
                return LinkedDependencies(expected.Scope, transaction) == expected;
            });
    }

    private LinkedFeatureDependencies? SaveLinkedFeatureCheckpointCore(LinkedFeatureScope scope,
        BoundedFeatureWrite checkpoint, LinkedFeatureDependencies? expected, CancellationToken token)
    {
        ValidateLinkedScope(scope); ArgumentNullException.ThrowIfNull(checkpoint);
        if (!CanonicalLinkedFeatureKeys(scope).Contains(checkpoint.Key, StringComparer.Ordinal))
            throw new InvalidDataException("LinkedFeatureCheckpointDependencyMissing");
        ValidateBoundedFeatureWrites([checkpoint]);
        lock (gate) return CatalogService.ExecuteInterruptible<LinkedFeatureDependencies?>(connection, token, () =>
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var transaction = connection.BeginTransaction();
            EnsureLinkedDependencies(scope, transaction);
            if (expected is not null && LinkedDependencies(scope, transaction) != expected) return null;
            using var write = connection.CreateCommand(); write.Transaction = transaction; write.CommandText = BoundedFeatureUpsertSql;
            write.Parameters.AddWithValue("$revision", revision); write.Parameters.AddWithValue("$key", checkpoint.Key);
            write.Parameters.AddWithValue("$version", checkpoint.Version); write.Parameters.AddWithValue("$payload", checkpoint.Payload);
            write.ExecuteNonQuery();
            var result = LinkedDependencies(scope, transaction);
            AssetLoadDiagnostics.Counters("LinkedFeatures.CheckpointPrepared", scope.Key, new Dictionary<string, long> { ["AdditionalEpoch"] = result.AdditionalEpoch });
            token.ThrowIfCancellationRequested(); transaction.Commit(); return result;
        });
    }

    private LinkedFeatureDependencies LinkedDependencies(LinkedFeatureScope scope, SqliteTransaction transaction)
    {
        using var command = LinkedCommand(connection, transaction, scope, """
            SELECT
                (SELECT epoch FROM linked_feature_dependencies WHERE revision=$revision AND kind=0 AND dependency_key=$collection),
                (SELECT epoch FROM linked_feature_dependencies WHERE revision=$revision AND kind=1 AND dependency_key=$family),
                (SELECT COALESCE(SUM(epoch),0) FROM linked_feature_dependencies WHERE revision=$revision AND kind=2
                    AND dependency_key IN(SELECT value FROM json_each($additionalFeatures))),
                (SELECT COUNT(*) FROM linked_feature_dependencies WHERE revision=$revision AND kind=2
                    AND dependency_key IN(SELECT value FROM json_each($additionalFeatures)))
            """);
        using var rows = command.ExecuteReader();
        if (!rows.Read() || rows.IsDBNull(0) || rows.IsDBNull(1) || rows.GetInt64(3) != CanonicalLinkedFeatureKeys(scope).Length)
            throw new InvalidDataException("LinkedFeatureDependenciesUnavailable");
        return new(revision, scope, rows.GetInt64(0), rows.GetInt64(1)) { AdditionalEpoch = rows.GetInt64(2) };
    }
    public long? PublishLinkedFeatureProjection<THead>(LinkedFeatureDependencies expected,
        IReadOnlyList<LinkedFeatureReference> references, THead head, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(references);
        ValidateLinkedScope(expected.Scope);
        if (expected.Revision != revision) throw new InvalidDataException("LinkedFeatureDependenciesInvalid");
        var scope = expected.Scope;
        string payload = BoundedFeatureWrite.Create(scope.Key, scope.Version, head, MaximumLinkedFeatureHeadBytes).Payload;
        var identities = new HashSet<string>(StringComparer.Ordinal); var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in references)
        {
            token.ThrowIfCancellationRequested();
            if (item?.Identity is null || item.FeatureKey is null || !item.FeatureKey.StartsWith(scope.Collection + "/item/", StringComparison.Ordinal) ||
                !identities.Add(item.Identity.Key) || !keys.Add(item.FeatureKey)) throw new InvalidDataException("LinkedFeatureMappingInvalid");
        }
        lock (gate) return CatalogService.ExecuteInterruptible<long?>(connection, token, () =>
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var transaction = connection.BeginTransaction();
            if (LinkedDependencies(scope, transaction) != expected) return null;
            using var previous = LinkedCommand(connection, transaction, scope,
                "SELECT generation FROM linked_feature_heads WHERE revision=$revision AND projection_key=$key AND projection_version=$version");
            long generation = checked(Convert.ToInt64(previous.ExecuteScalar() ?? 0L) + 1);
            using (var clear = LinkedCommand(connection, transaction, scope,
                "DELETE FROM linked_feature_links WHERE revision=$revision AND projection_key=$key AND projection_version=$version")) clear.ExecuteNonQuery();
            using (var write = LinkedCommand(connection, transaction, scope, "INSERT INTO linked_feature_links VALUES($revision,$key,$version,$generation,$identity,$feature)"))
            {
                write.Parameters.AddWithValue("$generation", generation);
                var identity = write.Parameters.Add("$identity", SqliteType.Text); var feature = write.Parameters.Add("$feature", SqliteType.Text);
                foreach (var item in references)
                { token.ThrowIfCancellationRequested(); identity.Value = item.Identity.Key; feature.Value = item.FeatureKey; write.ExecuteNonQuery(); }
            }
            using (var validate = LinkedCommand(connection, transaction, scope, """
                SELECT (SELECT COUNT(*) FROM assets_v2 WHERE revision=$revision AND family=$family),
                    (SELECT COUNT(*) FROM linked_feature_links l JOIN assets_v2 a ON a.revision=l.revision AND a.identity=l.identity
                        JOIN asset_features f ON f.revision=l.revision AND f.feature=l.feature_key
                     WHERE l.revision=$revision AND l.projection_key=$key AND l.projection_version=$version
                        AND a.family=$family AND f.version=$collectionVersion)
                """))
            using (var rows = validate.ExecuteReader())
                if (!rows.Read() || rows.GetInt64(0) != references.Count || rows.GetInt64(1) != references.Count)
                    throw new InvalidDataException("LinkedFeatureMappingIncomplete");
            using var publish = LinkedCommand(connection, transaction, scope, """
                INSERT INTO linked_feature_heads(revision,projection_key,projection_version,collection,collection_version,family,generation,collection_epoch,family_epoch,row_count,payload,additional_features,additional_epoch)
                VALUES($revision,$key,$version,$collection,$collectionVersion,$family,$generation,$collectionEpoch,$familyEpoch,$count,$payload,$additionalFeatures,$additionalEpoch)
                ON CONFLICT(revision,projection_key,projection_version) DO UPDATE SET collection=excluded.collection,collection_version=excluded.collection_version,
                    family=excluded.family,generation=excluded.generation,collection_epoch=excluded.collection_epoch,family_epoch=excluded.family_epoch,
                    row_count=excluded.row_count,payload=excluded.payload,additional_features=excluded.additional_features,additional_epoch=excluded.additional_epoch
                """);
            publish.Parameters.AddWithValue("$generation", generation); publish.Parameters.AddWithValue("$collectionEpoch", expected.CollectionEpoch);
            publish.Parameters.AddWithValue("$familyEpoch", expected.FamilyEpoch); publish.Parameters.AddWithValue("$count", references.Count);
            publish.Parameters.AddWithValue("$payload", payload); publish.Parameters.AddWithValue("$additionalEpoch", expected.AdditionalEpoch); publish.ExecuteNonQuery();
            AssetLoadDiagnostics.Counters("LinkedFeatures.PublishPrepared", scope.Key, new Dictionary<string, long> { ["Generation"] = generation, ["Rows"] = references.Count });
            token.ThrowIfCancellationRequested(); transaction.Commit(); return generation;
        });
    }

    public LinkedFeatureHead<THead>? ReadLinkedFeatureHead<THead>(LinkedFeatureScope scope, CancellationToken token = default)
    {
        ValidateLinkedScope(scope);
        return WithLinkedRead<LinkedFeatureHead<THead>?>(token, (read, transaction, cancellation) =>
        {
            var row = ReadLinkedHead(scope, read, transaction, cancellation);
            return row is null ? null : new LinkedFeatureHead<THead>(row.Generation, LinkedJson<THead>(row.Payload));
        });
    }

    public LinkedFeaturePage<THead, TPayload>? ReadLinkedFeaturePage<THead, TPayload>(LinkedFeatureScope scope, AssetQuery query,
        long? expectedGeneration = null, CancellationToken token = default)
    {
        ValidateLinkedScope(scope); ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 || query.Limit is < 1 or > MaximumLinkedFeaturePageRows || expectedGeneration is < 1)
            throw new ArgumentOutOfRangeException(nameof(query));
        if (query.Family is not null && query.Family != scope.Family) throw new ArgumentException("The query family differs from the linked projection.", nameof(query));
        query = query with { Family = scope.Family };
        return WithLinkedRead<LinkedFeaturePage<THead, TPayload>?>(token, (read, transaction, cancellation) =>
        {
            var head = ReadLinkedHead(scope, read, transaction, cancellation);
            if (expectedGeneration is { } requested && head?.Generation != requested)
                throw new ArchiveException("LinkedFeatureGenerationUnavailable", "The cached page changed. Refresh the page.");
            if (head is null) return null;
            AssetLoadDiagnostics.Counters("LinkedFeatures.ReadSnapshot", scope.Key, new Dictionary<string, long> { ["Generation"] = head.Generation });
            cancellation.ThrowIfCancellationRequested();
            long total;
            using (var count = AssetCommand(query, "COUNT(*)", read, transaction, allowNameIndex: false)) total = Convert.ToInt64(count.ExecuteScalar());
            var identities = new List<string>();
            using (var page = AssetPageCommand(query, "identity", read, transaction, prepareIndexes: false))
            using (var rows = page.ExecuteReader()) while (rows.Read()) { cancellation.ThrowIfCancellationRequested(); identities.Add(rows.GetString(0)); }
            long bytes = head.Bytes;
            var rowBytes = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var sizes = LinkedCommand(read, transaction, scope, """
                SELECT length(CAST(a.payload AS BLOB)),length(CAST(f.payload AS BLOB)),f.version,l.generation
                FROM assets_v2 a LEFT JOIN linked_feature_links l ON l.revision=a.revision AND l.identity=a.identity
                    AND l.projection_key=$key AND l.projection_version=$version
                LEFT JOIN asset_features f ON f.revision=l.revision AND f.feature=l.feature_key
                WHERE a.revision=$revision AND a.identity=$identity AND a.family=$family
                """))
            {
                var identity = sizes.Parameters.Add("$identity", SqliteType.Text);
                foreach (string key in identities)
                {
                    cancellation.ThrowIfCancellationRequested(); identity.Value = key;
                    using var rows = sizes.ExecuteReader();
                    if (!rows.Read() || rows.IsDBNull(0) || rows.IsDBNull(1) || rows.IsDBNull(2) || rows.IsDBNull(3) ||
                        rows.GetString(2) != scope.CollectionVersion || rows.GetInt64(3) != head.Generation)
                        throw new InvalidDataException("LinkedFeatureMappingInvalid");
                    long serializedBytes = checked(rows.GetInt64(0) + rows.GetInt64(1));
                    rowBytes.Add(key, serializedBytes); bytes = checked(bytes + serializedBytes);
                    if (bytes > MaximumLinkedFeaturePageBytes) throw new InvalidDataException("LinkedFeaturePageLimit");
                }
            }
            var output = new List<LinkedFeatureRow<TPayload>>(identities.Count);
            using (var payloads = LinkedCommand(read, transaction, scope, """
                SELECT a.payload,f.payload,a.lookup_key FROM assets_v2 a JOIN linked_feature_links l ON l.revision=a.revision AND l.identity=a.identity
                    AND l.projection_key=$key AND l.projection_version=$version
                JOIN asset_features f ON f.revision=l.revision AND f.feature=l.feature_key
                WHERE a.revision=$revision AND a.identity=$identity AND a.family=$family
                """))
            {
                var identity = payloads.Parameters.Add("$identity", SqliteType.Text);
                foreach (string key in identities)
                {
                    cancellation.ThrowIfCancellationRequested(); identity.Value = key;
                    using var rows = payloads.ExecuteReader();
                    if (!rows.Read()) throw new InvalidDataException("LinkedFeatureMappingInvalid");
                    var asset = LinkedJson<AssetDescriptor>(rows.GetString(0));
                    if (asset.Identity is null || asset.Identity.Key != key || asset.Family != scope.Family || asset.Revision is null ||
                        SourceIdentity.DerivedCacheKey(asset.Revision.IndexContentId, asset.Revision.PhysicalSourceStamp) != revision)
                        throw new InvalidDataException("LinkedFeatureAssetInvalid");
                    asset = asset with { LookupKey = rows.IsDBNull(2) ? null : rows.GetString(2) };
                    output.Add(new(asset, LinkedJson<TPayload>(rows.GetString(1))) { SerializedBytes = rowBytes[key] });
                }
            }
            cancellation.ThrowIfCancellationRequested();
            AssetLoadDiagnostics.Counters("LinkedFeatures.Page", scope.Key, new Dictionary<string, long>
                { ["Generation"] = head.Generation, ["Rows"] = output.Count, ["JsonBytes"] = bytes });
            return new LinkedFeaturePage<THead, TPayload>(head.Generation, LinkedJson<THead>(head.Payload), total, output.AsReadOnly());
        });
    }

    private sealed record LinkedHeadRow(long Generation, string Payload, long Bytes);
    private LinkedHeadRow? ReadLinkedHead(LinkedFeatureScope scope, SqliteConnection read, SqliteTransaction transaction, CancellationToken token)
    {
        using var command = LinkedCommand(read, transaction, scope, """
            SELECT h.generation,length(CAST(h.payload AS BLOB)),CASE WHEN length(CAST(h.payload AS BLOB))<=$maximum THEN h.payload ELSE NULL END
            FROM linked_feature_heads h JOIN linked_feature_dependencies c ON c.revision=h.revision AND c.kind=0 AND c.dependency_key=h.collection AND c.epoch=h.collection_epoch
                JOIN linked_feature_dependencies a ON a.revision=h.revision AND a.kind=1 AND a.dependency_key=h.family AND a.epoch=h.family_epoch
            WHERE h.revision=$revision AND h.projection_key=$key AND h.projection_version=$version
                AND h.collection=$collection AND h.collection_version=$collectionVersion AND h.family=$family
                AND h.additional_features=$additionalFeatures AND h.additional_epoch=(SELECT COALESCE(SUM(d.epoch),0) FROM linked_feature_dependencies d
                    WHERE d.revision=h.revision AND d.kind=2 AND d.dependency_key IN(SELECT value FROM json_each(h.additional_features)))
                AND json_array_length(h.additional_features)=(SELECT COUNT(*) FROM linked_feature_dependencies d
                    WHERE d.revision=h.revision AND d.kind=2 AND d.dependency_key IN(SELECT value FROM json_each(h.additional_features)))
            """);
        command.Parameters.AddWithValue("$maximum", MaximumLinkedFeatureHeadBytes);
        using var rows = command.ExecuteReader(); token.ThrowIfCancellationRequested();
        if (!rows.Read()) return null;
        if (rows.GetInt64(1) < 1 || rows.GetInt64(1) > MaximumLinkedFeatureHeadBytes || rows.IsDBNull(2))
            throw new InvalidDataException("LinkedFeatureHeadLimit");
        return new(rows.GetInt64(0), rows.GetString(2), rows.GetInt64(1));
    }

    private T WithLinkedRead<T>(CancellationToken token, Func<SqliteConnection, SqliteTransaction, CancellationToken, T> action)
    {
        SqliteConnection read; CancellationTokenSource own;
        lock (linkedReadGate)
        {
            ObjectDisposedException.ThrowIf(linkedReadDisposed, this);
            own = CancellationTokenSource.CreateLinkedTokenSource(token, linkedReadLifetime.Token);
            read = CreateReaderConnection();
            linkedReadConnections.Add(read);
        }
        try
        {
            own.Token.ThrowIfCancellationRequested(); read.Open();
            read.CreateFunction<string, string, bool>("deviant_contains", static (text, term) => text.Contains(term, StringComparison.OrdinalIgnoreCase), isDeterministic: true);
            read.CreateFunction<string, string>("deviant_natural", static text => NaturalOrder.Key(text), isDeterministic: true);
            read.CreateFunction<string, long>("deviant_ordinal", IdentityOrdinal, isDeterministic: true);
            read.CreateFunction<string, string>("deviant_member", IdentityMember, isDeterministic: true);
            return CatalogService.ExecuteInterruptible(read, own.Token, () =>
            {
                using (var pragmas = read.CreateCommand()) { pragmas.CommandText = "PRAGMA busy_timeout=5000; PRAGMA query_only=ON;"; pragmas.ExecuteNonQuery(); }
                using var transaction = read.BeginTransaction(deferred: true);
                var result = action(read, transaction, own.Token);
                own.Token.ThrowIfCancellationRequested(); transaction.Commit(); return result;
            });
        }
        finally
        {
            lock (linkedReadGate)
            {
                try { read.Dispose(); }
                finally { linkedReadConnections.Remove(read); Monitor.PulseAll(linkedReadGate); }
            }
            own.Dispose();
        }
    }

    private void DisposeLinkedFeatureReaders()
    {
        lock (linkedReadGate)
        {
            linkedReadDisposed = true; linkedReadLifetime.Cancel();
            foreach (var read in linkedReadConnections) if (read.State == System.Data.ConnectionState.Open) SQLitePCL.raw.sqlite3_interrupt(read.Handle);
            while (linkedReadConnections.Count > 0) Monitor.Wait(linkedReadGate);
            linkedReadLifetime.Dispose();
        }
    }

    private SqliteCommand LinkedCommand(SqliteConnection db, SqliteTransaction transaction, LinkedFeatureScope scope, string sql)
    {
        var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        command.Parameters.AddWithValue("$revision", revision); command.Parameters.AddWithValue("$key", scope.Key);
        command.Parameters.AddWithValue("$version", scope.Version); command.Parameters.AddWithValue("$collection", scope.Collection);
        command.Parameters.AddWithValue("$collectionVersion", scope.CollectionVersion); command.Parameters.AddWithValue("$family", scope.Family);
        command.Parameters.AddWithValue("$additionalFeatures", JsonSerializer.Serialize(CanonicalLinkedFeatureKeys(scope)));
        return command;
    }
    private static T LinkedJson<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json) is { } value ? value : throw new InvalidDataException("LinkedFeaturePayloadInvalid"); }
        catch (Exception error) when (error is JsonException or NotSupportedException) { throw new InvalidDataException("LinkedFeaturePayloadInvalid", error); }
    }
    private static string[] CanonicalLinkedFeatureKeys(LinkedFeatureScope scope) =>
        scope.AdditionalFeatures.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    private static void ValidateLinkedScope(LinkedFeatureScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(scope.Collection);
        ArgumentNullException.ThrowIfNull(scope.AdditionalFeatures);
        foreach (string key in scope.AdditionalFeatures) ArgumentNullException.ThrowIfNull(key);
        foreach (string value in new[] { scope.Key, scope.Version, scope.CollectionVersion, scope.Family }) ArgumentException.ThrowIfNullOrWhiteSpace(value);
    }
}
