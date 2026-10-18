using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deviant.Core;
using Microsoft.Data.Sqlite;
namespace Deviant.Catalog;

public sealed partial class FeatureStore : IDisposable, Deviant.Assets.IAssetCatalog
{
    private readonly SqliteConnection connection;
    private readonly string revision;
    private readonly string path;
    private readonly object gate = new();
    private readonly object disposalGate = new();
    private bool disposed;
    private volatile bool characterProjectionWritesEnabled;
    private volatile bool characterProjectionReady;
    private readonly Dictionary<Deviant.Assets.AssetQuery,long> counts = [];
    private long countDataVersion = -1;
    public static FeatureStore Open(ArchiveSnapshot snapshot, string? cacheRoot = null)
    {
        CacheStorageBudget.ClearFailure();
        cacheRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deviant", "cache");
        if (DirectoryBoundary.IsWithin(cacheRoot, snapshot.SourceRoot)) throw new ArchiveException("UnsafeCache", "Cache must be outside the archive directory.");
        Directory.CreateDirectory(cacheRoot);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(IndexLayout.ParserVersion + "\n" + SourceIdentity.IndexCacheKey(snapshot.IndexContentId)))).ToLowerInvariant();
        try { return new(Path.Combine(cacheRoot, "catalog-" + key + ".sqlite3"), SourceIdentity.DerivedCacheKey(snapshot.IndexContentId, snapshot.PhysicalSourceStamp)); }
        catch (SqliteException error) when (error.SqliteErrorCode == 13) { throw CacheStorageBudget.StorageFailure(error); }
    }
    private FeatureStore(string path, string revision)
    {
        this.path = path;
        this.revision = revision;
        var storage = CacheStorageBudget.ForRoot(Path.GetDirectoryName(path)!);
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        if (storage.Limit == 0)
        {
            builder.DataSource = "deviant-features-" + Guid.NewGuid().ToString("N");
            builder.Mode = SqliteOpenMode.Memory; builder.Cache = SqliteCacheMode.Shared;
        }
        else builder.Vfs = storage.Vfs;
        connection = new(builder.ToString());
        try
        {
        connection.Open();
        connection.CreateFunction<string, string, bool>("deviant_contains", static (text, term) => text.Contains(term, StringComparison.OrdinalIgnoreCase), isDeterministic: true);
        connection.CreateFunction<string,string>("deviant_natural",static text=>Deviant.Assets.NaturalOrder.Key(text),isDeterministic:true);
        connection.CreateFunction<string,long>("deviant_ordinal",static text=>IdentityOrdinal(text),isDeterministic:true);
        connection.CreateFunction<string,string>("deviant_member",static text=>IdentityMember(text),isDeterministic:true);
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS asset_features (revision TEXT NOT NULL, feature TEXT NOT NULL, version TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(revision,feature));
            DROP TABLE IF EXISTS assets_v1;
            CREATE TABLE IF NOT EXISTS assets_v2 (row_id INTEGER PRIMARY KEY, revision TEXT NOT NULL, identity TEXT NOT NULL, family TEXT NOT NULL,
                label TEXT NOT NULL, search TEXT NOT NULL, search_fold TEXT NOT NULL, sort_key TEXT NOT NULL, bank TEXT, language TEXT, codec TEXT, duration REAL, channels INTEGER,
                availability INTEGER NOT NULL, completeness INTEGER NOT NULL, payload TEXT NOT NULL, lookup_key TEXT, UNIQUE(revision,identity));
            CREATE INDEX IF NOT EXISTS assets_v2_bank ON assets_v2(revision,bank,sort_key,identity);
            CREATE INDEX IF NOT EXISTS assets_v2_family_sort ON assets_v2(revision,family,sort_key,identity);
            CREATE INDEX IF NOT EXISTS assets_v2_language ON assets_v2(revision,family,language,sort_key,identity);
            CREATE INDEX IF NOT EXISTS assets_v2_codec ON assets_v2(revision,family,codec,sort_key,identity);
            CREATE VIRTUAL TABLE IF NOT EXISTS assets_search_v2 USING fts5(search_fold,content='assets_v2',content_rowid='row_id',tokenize='trigram case_sensitive 1');
            CREATE TRIGGER IF NOT EXISTS assets_v2_ai AFTER INSERT ON assets_v2 BEGIN
                INSERT INTO assets_search_v2(rowid,search_fold) VALUES(new.row_id,new.search_fold); END;
            CREATE TRIGGER IF NOT EXISTS assets_v2_ad AFTER DELETE ON assets_v2 BEGIN
                INSERT INTO assets_search_v2(assets_search_v2,rowid,search_fold) VALUES('delete',old.row_id,old.search_fold); END;
            DROP TRIGGER IF EXISTS assets_v2_au;
            CREATE TRIGGER IF NOT EXISTS assets_v2_au2 AFTER UPDATE OF search_fold ON assets_v2 WHEN old.search_fold != new.search_fold BEGIN
                INSERT INTO assets_search_v2(assets_search_v2,rowid,search_fold) VALUES('delete',old.row_id,old.search_fold);
                INSERT INTO assets_search_v2(rowid,search_fold) VALUES(new.row_id,new.search_fold); END;
            """;
        schema.ExecuteNonQuery();
        bool hasLookupKey=false;
        using(var columns=connection.CreateCommand()){columns.CommandText="PRAGMA table_info(assets_v2)";using var rows=columns.ExecuteReader();while(rows.Read())if(rows.GetString(1)=="lookup_key")hasLookupKey=true;}
        if(!hasLookupKey){using var migrate=connection.CreateCommand();migrate.CommandText="ALTER TABLE assets_v2 ADD COLUMN lookup_key TEXT";migrate.ExecuteNonQuery();}
        using var temporary=connection.CreateCommand();temporary.CommandText="CREATE TEMP TABLE asset_replacement_keys(identity TEXT PRIMARY KEY); CREATE INDEX IF NOT EXISTS assets_v2_lookup ON assets_v2(revision,family,lookup_key,bank,sort_key,identity)";temporary.ExecuteNonQuery();
        InitializeLocalizationStreamedGroups();
        InitializeLinkedFeatures();
        }
        catch { connection.Dispose(); throw; }
    }

    private SqliteConnection CreateReaderConnection()
    {
        var builder = new SqliteConnectionStringBuilder(connection.ConnectionString);
        if (builder.Mode != SqliteOpenMode.Memory) builder.Mode = SqliteOpenMode.ReadOnly;
        return new(builder.ToString());
    }
    public T? Load<T>(string key, string version, CancellationToken token = default)
    {
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM asset_features WHERE revision=$revision AND feature=$key AND version=$version";
            command.Parameters.AddWithValue("$revision", revision); command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$version", version);
            return command.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<T>(payload) : default;
        });
    }
    public void Save<T>(string key, string version, T value, CancellationToken token = default)
    {
        var payload = JsonSerializer.Serialize(value);
        lock (gate) CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO asset_features VALUES($revision,$key,$version,$payload)";
            command.Parameters.AddWithValue("$revision", revision); command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$version", version); command.Parameters.AddWithValue("$payload", payload);
            return command.ExecuteNonQuery();
        });
    }
    public void Checkpoint() { lock (gate) { using var command = connection.CreateCommand(); command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; command.ExecuteNonQuery(); } }
    public void SaveItems<T>(string collection, string version, IEnumerable<T> items, CancellationToken token = default)
    {
        SaveItemsCore(collection, version, items, _ => true, token);
    }

    private TResult SaveItemsCore<T, TResult>(string collection, string version, IEnumerable<T> items,
        Func<SqliteTransaction, TResult> completion, CancellationToken token, Func<SqliteTransaction, bool>? prepare = null)
    {
        var prefix = collection + "/item/";
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var transaction = connection.BeginTransaction();
            if (prepare is not null && !prepare(transaction)) return default!;
            using (var remove = connection.CreateCommand())
            {
                remove.Transaction = transaction; remove.CommandText = "DELETE FROM asset_features WHERE revision=$revision AND feature>=$prefix AND feature<$end";
                remove.Parameters.AddWithValue("$revision", revision); remove.Parameters.AddWithValue("$prefix", prefix);
                remove.Parameters.AddWithValue("$end", collection + "/item0"); remove.ExecuteNonQuery();
            }
            using var write = connection.CreateCommand(); write.Transaction = transaction;
            write.CommandText = "INSERT INTO asset_features VALUES($revision,$key,$version,$payload)";
            write.Parameters.AddWithValue("$revision", revision); var key = write.Parameters.Add("$key", SqliteType.Text);
            write.Parameters.AddWithValue("$version", version); var payload = write.Parameters.Add("$payload", SqliteType.Text);
            var ordinal = 0L;
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested(); key.Value = prefix + ordinal++.ToString("D12", System.Globalization.CultureInfo.InvariantCulture);
                payload.Value = JsonSerializer.Serialize(item); write.ExecuteNonQuery();
            }
            var result = completion(transaction);
            token.ThrowIfCancellationRequested(); transaction.Commit(); return result;
        });
    }
    public List<T> LoadItems<T>(string collection, string version, CancellationToken token = default)
    {
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var read = connection.CreateCommand(); read.CommandText = "SELECT payload FROM asset_features WHERE revision=$revision AND feature>=$prefix AND feature<$end AND version=$version ORDER BY feature";
            read.Parameters.AddWithValue("$revision", revision); read.Parameters.AddWithValue("$prefix", collection + "/item/");
            read.Parameters.AddWithValue("$end", collection + "/item0"); read.Parameters.AddWithValue("$version", version);
            var items = new List<T>(); using var rows = read.ExecuteReader();
            while (rows.Read()) { token.ThrowIfCancellationRequested(); items.Add(JsonSerializer.Deserialize<T>(rows.GetString(0))!); }
            return items;
        });
    }
    public T? LoadItem<T>(string collection,string key,string version,CancellationToken token=default)
        => Load<T>(collection+"/item/"+key,version,token);
    public void UpsertItems<T>(string collection,string version,IEnumerable<KeyValuePair<string,T>> items,CancellationToken token=default)
    {
        lock(gate) CatalogService.ExecuteInterruptible(connection,token,()=>
        {
            using var transaction=connection.BeginTransaction(); using var write=connection.CreateCommand(); write.Transaction=transaction;
            write.CommandText="INSERT OR REPLACE INTO asset_features VALUES($revision,$key,$version,$payload)";
            write.Parameters.AddWithValue("$revision",revision); write.Parameters.AddWithValue("$version",version);
            var key=write.Parameters.Add("$key",SqliteType.Text); var payload=write.Parameters.Add("$payload",SqliteType.Text);
            foreach(var item in items) { token.ThrowIfCancellationRequested(); key.Value=collection+"/item/"+item.Key; payload.Value=JsonSerializer.Serialize(item.Value); write.ExecuteNonQuery(); }
            transaction.Commit(); return true;
        });
    }
    public void UpsertAssets(IEnumerable<Deviant.Assets.AssetDescriptor> assets, CancellationToken token = default)
        => StoreAssets(assets, null, token);
    public void ReplaceAssets(string family, IEnumerable<Deviant.Assets.AssetDescriptor> assets, CancellationToken token = default)
        => StoreAssets(assets, family, token);
    private void StoreAssets(IEnumerable<Deviant.Assets.AssetDescriptor> assets, string? replaceFamily, CancellationToken token)
        => StoreAssetsCore(assets, replaceFamily, _ => true, token);

    private TResult StoreAssetsCore<TResult>(IEnumerable<Deviant.Assets.AssetDescriptor> assets, string? replaceFamily,
        Func<SqliteTransaction, TResult> completion, CancellationToken token, Func<SqliteTransaction, bool>? prepare = null)
    {
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            InitializeCharacterProjectionForAssetWrites();
            using var transaction = connection.BeginTransaction();
            if (prepare is not null && !prepare(transaction)) return default!;
            using (var clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM asset_replacement_keys";
                clear.ExecuteNonQuery();
            }
            using var keep=connection.CreateCommand();keep.Transaction=transaction;keep.CommandText="INSERT OR IGNORE INTO asset_replacement_keys VALUES($identity)";
            keep.Parameters.Add("$identity",SqliteType.Text);
            bool hasNonModelAssets=false;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO assets_v2(revision,identity,family,label,search,search_fold,sort_key,bank,language,codec,duration,channels,availability,completeness,payload,lookup_key)
                VALUES($revision,$identity,$family,$label,$search,$fold,$sort,$bank,$language,$codec,$duration,$channels,$availability,$completeness,$payload,$lookup)
                ON CONFLICT(revision,identity) DO UPDATE SET family=excluded.family,label=excluded.label,search=excluded.search,
                    search_fold=excluded.search_fold,sort_key=excluded.sort_key,bank=excluded.bank,language=excluded.language,codec=excluded.codec,
                    duration=excluded.duration,channels=excluded.channels,availability=excluded.availability,completeness=excluded.completeness,payload=excluded.payload,lookup_key=excluded.lookup_key
                """;
            command.Parameters.AddWithValue("$revision", revision);
            foreach (var name in new[] { "identity", "family", "label", "search", "fold", "sort", "bank", "language", "codec", "duration", "channels", "availability", "completeness", "payload", "lookup" })
                command.Parameters.AddWithValue("$" + name, DBNull.Value);
            foreach (var asset in assets)
            {
                token.ThrowIfCancellationRequested();
                if (replaceFamily is not null && asset.Family != replaceFamily) throw new ArgumentException("Asset family differs from the replaced family.");
                command.Parameters["$identity"].Value = asset.Identity.Key;
                command.Parameters["$lookup"].Value = (object?)asset.LookupKey ?? DBNull.Value;
                keep.Parameters["$identity"].Value=asset.Identity.Key;keep.ExecuteNonQuery();
                command.Parameters["$family"].Value = asset.Family; command.Parameters["$label"].Value = asset.Label;
                command.Parameters["$search"].Value = asset.SearchTerms ?? string.Join('\n', asset.Names.Select(n => n.Text).Prepend(asset.Label).Append(asset.Identity.Key));
                command.Parameters["$fold"].Value = ((string)command.Parameters["$search"].Value!).ToUpperInvariant();
                command.Parameters["$sort"].Value = asset.SortKey ?? Deviant.Assets.NaturalOrder.Key(asset.Label);
                command.Parameters["$bank"].Value = (object?)asset.GroupKey ?? DBNull.Value; command.Parameters["$language"].Value = (object?)asset.Language ?? DBNull.Value;
                command.Parameters["$codec"].Value = (object?)asset.Codec ?? DBNull.Value; command.Parameters["$duration"].Value = (object?)asset.DurationSeconds ?? DBNull.Value;
                command.Parameters["$channels"].Value = (object?)asset.Channels ?? DBNull.Value; command.Parameters["$availability"].Value = (int)asset.Availability;
                command.Parameters["$completeness"].Value = (int)asset.Completeness; command.Parameters["$payload"].Value = JsonSerializer.Serialize(asset); command.ExecuteNonQuery();
                if (characterProjectionWritesEnabled && asset.Family == "character-model")
                {
                    using var row = connection.CreateCommand(); row.Transaction = transaction;
                    row.CommandText = "SELECT row_id FROM assets_v2 WHERE revision=$revision AND identity=$identity";
                    row.Parameters.AddWithValue("$revision", revision); row.Parameters.AddWithValue("$identity", asset.Identity.Key);
                    RefreshCharacterProjection(asset, (long)row.ExecuteScalar()!, transaction);
                }
                else if (characterProjectionWritesEnabled)
                {
                    hasNonModelAssets = true;
                }
            }
            if(replaceFamily is not null)
            {
                using var delete=connection.CreateCommand();delete.Transaction=transaction;
                delete.CommandText="DELETE FROM assets_v2 WHERE revision=$revision AND family=$family AND identity NOT IN (SELECT identity FROM asset_replacement_keys)";
                delete.Parameters.AddWithValue("$revision",revision);delete.Parameters.AddWithValue("$family",replaceFamily);delete.ExecuteNonQuery();
            }
            if (characterProjectionWritesEnabled)
            {
                if (replaceFamily == "character-model") RemoveMissingCharacterProjections(transaction);
                else if (replaceFamily is not null || hasNonModelAssets)
                    RemoveReplacedCharacterProjections(transaction);
            }
            var result = completion(transaction);
            token.ThrowIfCancellationRequested(); transaction.Commit(); counts.Clear(); return result;
        });
    }

    private void RemoveReplacedCharacterProjections(SqliteTransaction transaction)
    {
        using var list = connection.CreateCommand(); list.Transaction = transaction;
        list.CommandText = "SELECT model.identity,model.actor,model.name_profile,model.classification_profile " +
            "FROM temp.asset_replacement_keys changed JOIN character_projection_models model " +
            "ON model.revision=$revision AND model.identity=changed.identity " +
            "WHERE NOT EXISTS(SELECT 1 FROM assets_v2 asset WHERE asset.revision=model.revision " +
            "AND asset.identity=model.identity AND asset.family='character-model')";
        list.Parameters.AddWithValue("$revision", revision);
        var removed = new List<(string Identity, string Actor, string Profile, string Classification)>();
        using (var rows = list.ExecuteReader())
            while (rows.Read()) removed.Add((rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3)));

        var touchedGroups = new HashSet<(string Actor, string Profile, string Classification)>();
        foreach (var item in removed)
        {
            var key = (item.Actor, item.Profile, item.Classification);
            RemoveCharacterProjectionModel(item.Identity, key, transaction);
            if (key.Actor.Length > 0) touchedGroups.Add(key);
        }
        foreach (var key in touchedGroups)
            RebuildCharacterProjectionGroup(key.Actor, key.Profile, key.Classification, transaction);
    }

    public long Count(Deviant.Assets.AssetQuery query, CancellationToken token = default)
    {
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        { using var versionCommand=connection.CreateCommand();versionCommand.CommandText="PRAGMA data_version";var dataVersion=(long)versionCommand.ExecuteScalar()!;
          if(dataVersion!=countDataVersion){counts.Clear();countDataVersion=dataVersion;}
          var key=query with { Offset=0,Limit=1,Sort=Deviant.Assets.AssetSort.Default,Descending=false }; if(counts.TryGetValue(key,out var cached)) return cached;
          using var command = AssetCommand(query, "COUNT(*)"); var count=(long)command.ExecuteScalar()!;
          if(counts.Count>=128) counts.Clear(); counts[key]=count; return count; });
    }
    public IReadOnlyList<Deviant.Assets.AssetDescriptor> Query(Deviant.Assets.AssetQuery query, CancellationToken token = default)
        => QueryRetained(query, null, token);
    public IReadOnlyList<Deviant.Assets.AssetDescriptor> QueryRetained(Deviant.Assets.AssetQuery query,
        Action<int>? retainPayload, CancellationToken token = default)
    {
        if (query.Offset < 0 || query.Limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(query));
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var command = AssetPageCommand(query, "payload");
            var rows = new List<Deviant.Assets.AssetDescriptor>(); using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var payload = reader.GetString(0); retainPayload?.Invoke(payload.Length);
                rows.Add(JsonSerializer.Deserialize<Deviant.Assets.AssetDescriptor>(payload)!);
            }
            return rows;
        });
    }
    public IReadOnlyList<string> QueryIdentityKeys(Deviant.Assets.AssetQuery query, CancellationToken token = default)
    {
        if (query.Offset < 0 || query.Limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(query));
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var command = AssetPageCommand(query, "identity");
            var keys = new List<string>(); using var reader = command.ExecuteReader();
            while (reader.Read()) { token.ThrowIfCancellationRequested(); keys.Add(reader.GetString(0)); }
            return keys;
        });
    }
    public long CaptureAssetPageBoundary(CancellationToken token = default)
    {
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(MAX(row_id),0) FROM assets_v2 WHERE revision=$revision";
            command.Parameters.AddWithValue("$revision", revision);
            return (long)command.ExecuteScalar()!;
        });
    }
    public IReadOnlyList<Deviant.Assets.AssetDescriptor> QueryCharacterGroups(Deviant.Assets.AssetQuery query,
        out long total, CancellationToken token = default, Action<int>? retainPayload = null)
    {
        if (!characterProjectionReady) throw new InvalidOperationException("Character projections must be prepared on a worker before querying.");
        var page = QueryCharacterGroupsReadOnly(query, token);
        total = page.Total;
        if (retainPayload is not null)
            foreach (var asset in page.Groups) retainPayload(JsonSerializer.Serialize(asset).Length);
        return page.Groups;
    }
    private SqliteCommand AssetPageCommand(Deviant.Assets.AssetQuery query, string projection,
        SqliteConnection? readConnection = null, SqliteTransaction? transaction = null, bool prepareIndexes = true)
    {
        if (prepareIndexes && query.Sort == Deviant.Assets.AssetSort.Name)
        {
            using var index = (readConnection ?? connection).CreateCommand(); index.Transaction = transaction;
            index.CommandText = "CREATE INDEX IF NOT EXISTS assets_v2_name_v1 ON assets_v2(revision,family,deviant_natural(label),sort_key,deviant_ordinal(identity),deviant_member(identity),identity)";
            index.ExecuteNonQuery();
        }
        var command = AssetCommand(query, "row_id", readConnection, transaction, allowNameIndex: prepareIndexes);
        var order = SortOrder(query);
        command.CommandText = "WITH selected AS MATERIALIZED (" + command.CommandText + " ORDER BY " + order +
            " LIMIT $limit OFFSET $offset) SELECT a." + projection + " FROM selected s JOIN assets_v2 a ON a.row_id=s.row_id ORDER BY " + order;
        command.Parameters.AddWithValue("$limit", query.Limit);
        command.Parameters.AddWithValue("$offset", query.Offset);
        return command;
    }
    private static string SortOrder(Deviant.Assets.AssetQuery query)
    {
        var field=query.Sort switch
        {
            Deviant.Assets.AssetSort.Default=>"sort_key",
            Deviant.Assets.AssetSort.Name=>"deviant_natural(label)",
            Deviant.Assets.AssetSort.Language=>"language",
            Deviant.Assets.AssetSort.Codec=>"codec",
            Deviant.Assets.AssetSort.Duration=>"duration",
            Deviant.Assets.AssetSort.Channels=>"channels",
            Deviant.Assets.AssetSort.Availability=>"availability",
            Deviant.Assets.AssetSort.Completeness=>"completeness",
            Deviant.Assets.AssetSort.Animations=>"CASE WHEN json_extract(payload,'$.Metadata.\"model.animation-state\"') IN ('complete','partial') THEN CAST(json_extract(payload,'$.Metadata.\"model.animation-count\"') AS INTEGER) END",
            _=>throw new ArgumentOutOfRangeException(nameof(query.Sort))
        };
        return field+(query.Descending?" DESC":" ASC")+" NULLS LAST,"+(field=="sort_key"?"":"sort_key ASC,")+"deviant_ordinal(identity) ASC,deviant_member(identity) ASC,identity ASC";
    }
    private static long IdentityOrdinal(string key)
    {
        var span=key.AsSpan();var split=span.IndexOf(':');if(split<0)return long.MaxValue;
        span=span[(split+1)..];split=span.IndexOf(':');if(split>=0)span=span[..split];
        return long.TryParse(span,System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out var ordinal)?ordinal:long.MaxValue;
    }
    private static string IdentityMember(string key)
    {
        var first=key.IndexOf(':');var second=first<0?-1:key.IndexOf(':',first+1);
        return second<0?"":Deviant.Assets.NaturalOrder.Key(key[(second+1)..]);
    }
    private SqliteCommand AssetCommand(Deviant.Assets.AssetQuery query, string select,
        SqliteConnection? readConnection = null, SqliteTransaction? transaction = null, bool allowNameIndex = true)
    {
        var useTrigram=query.GroupKey is null&&!string.IsNullOrWhiteSpace(query.Search)&&query.Search.EnumerateRunes().Count()>=3;
        var indexHint=useTrigram?" NOT INDEXED":query.GroupKey is not null&&query.ExactKey is null?" INDEXED BY assets_v2_bank":"";
        if(!useTrigram&&query.GroupKey is null&&query.Sort==Deviant.Assets.AssetSort.Name&&query.Language is not null&&query.Family is not null)
            indexHint=" INDEXED BY assets_v2_language";
        if(select=="row_id"&&query.Sort is Deviant.Assets.AssetSort.Name or Deviant.Assets.AssetSort.Default&&query.Family is not null&&query.GroupKey is null&&string.IsNullOrWhiteSpace(query.Search)&&query.ExactKey is null&&query.Language is null&&query.Codec is null&&query.Channels is null&&query.Availability is null&&query.Completeness is null&&query.MinimumDuration is null&&query.MaximumDuration is null)
            indexHint=query.Sort==Deviant.Assets.AssetSort.Name?(allowNameIndex?" INDEXED BY assets_v2_name_v1":""):" INDEXED BY assets_v2_family_sort";
        var command = (readConnection ?? connection).CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT {select} FROM assets_v2{indexHint} WHERE revision=$revision";
        command.Parameters.AddWithValue("$revision", revision);
        void Filter(string column, string op, object? value)
        { if (value is null) return; command.CommandText += $" AND {column}{op}${column}"; command.Parameters.AddWithValue("$" + column, value); }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            command.CommandText += " AND deviant_contains(search,$search)"; command.Parameters.AddWithValue("$search", query.Search);
            if (useTrigram)
            {
                command.CommandText += " AND row_id IN (SELECT rowid FROM assets_search_v2 WHERE assets_search_v2 MATCH $term)";
                command.Parameters.AddWithValue("$term", "\""+query.Search.ToUpperInvariant().Replace("\"","\"\"")+"\"");
            }
        }
        Filter("bank", "=", query.GroupKey); Filter("language", "=", query.Language); Filter("codec", "=", query.Codec);
        Filter("lookup_key", "=", query.ExactKey);
        Filter("family", "=", query.Family);
        if (query.CachedPayloadFilter is { } cachedPayload)
        {
            if (query.Family is null || cachedPayload.MaximumOrdinal < 0 || cachedPayload.MaximumPayloadBytes < 1)
                throw new ArgumentException("Cached payload filtering requires a bounded asset family.", nameof(query));
            command.CommandText += """
                 AND CASE WHEN length(CAST(payload AS BLOB))<=$payloadMaximum AND json_valid(payload) THEN
                    json_type(payload)='object' AND json_type(payload,'$.Identity')='object'
                    AND json_type(payload,'$.Identity.Ordinal')='integer'
                    AND json_extract(payload,'$.Identity.Ordinal')>=0 AND json_extract(payload,'$.Identity.Ordinal')<$payloadOrdinalCount
                    AND json_extract(payload,'$.Identity.IndexContentId')=$payloadIndex
                    AND json_extract(payload,'$.Identity.MemberPath')=$payloadMember
                    AND identity=$payloadIndex||':'||CAST(json_extract(payload,'$.Identity.Ordinal') AS TEXT)||':'||$payloadMember
                    AND json_extract(payload,'$.Revision.IndexContentId')=$payloadIndex
                    AND json_extract(payload,'$.Revision.PhysicalSourceStamp')=$payloadStamp
                    AND json_extract(payload,'$.Family')=family AND json_type(payload,'$.Label')='text'
                    AND json_type(payload,'$.Names')='array' AND json_type(payload,'$.Extents')='array'
                    AND json_type(payload,'$.Metadata')='object'
                    AND json_extract(payload,$payloadMetadataPath)=$payloadMetadataValue
                    AND NOT EXISTS(SELECT 1 FROM json_each(payload,'$.Names') n WHERE COALESCE(
                        CASE WHEN n.type='object' THEN json_type(n.value,'$.Text')='text'
                            AND json_type(n.value,'$.Source')='text'
                            AND json_type(n.value,'$.Origin')='integer'
                            AND json_extract(n.value,'$.Origin') BETWEEN 0 AND 5
                            AND (json_type(n.value,'$.CollisionGroup') IS NULL OR json_type(n.value,'$.CollisionGroup') IN ('null','text'))
                        ELSE 0 END,0)=0)
                    AND NOT EXISTS(SELECT 1 FROM json_each(payload,'$.Extents') e WHERE COALESCE(
                        CASE WHEN e.type='object' THEN json_type(e.value,'$.RelativeSource')='text'
                            AND json_type(e.value,'$.Offset')='integer' AND json_type(e.value,'$.Length')='integer'
                            AND json_extract(e.value,'$.Offset')>=0 AND json_extract(e.value,'$.Length')>=0
                            AND (json_type(e.value,'$.PayloadSha256') IS NULL OR json_type(e.value,'$.PayloadSha256') IN ('null','text'))
                        ELSE 0 END,0)=0)
                    AND NOT EXISTS(SELECT 1 FROM json_each(payload,'$.Metadata') m WHERE m.type!='text')
                 ELSE 0 END
                """;
            command.Parameters.AddWithValue("$payloadMaximum", cachedPayload.MaximumPayloadBytes);
            command.Parameters.AddWithValue("$payloadOrdinalCount", cachedPayload.MaximumOrdinal);
            command.Parameters.AddWithValue("$payloadIndex", cachedPayload.Revision.IndexContentId);
            command.Parameters.AddWithValue("$payloadStamp", cachedPayload.Revision.PhysicalSourceStamp);
            command.Parameters.AddWithValue("$payloadMember", cachedPayload.MemberPath);
            command.Parameters.AddWithValue("$payloadMetadataPath", "$.Metadata." + JsonSerializer.Serialize(cachedPayload.MetadataKey));
            command.Parameters.AddWithValue("$payloadMetadataValue", cachedPayload.MetadataValue);
        }
        if (query.MaximumCatalogRow is { } boundary)
        {
            command.CommandText += " AND row_id<=$catalogBoundary";
            command.Parameters.AddWithValue("$catalogBoundary", boundary);
        }
        if (query.ModelKind is { } kind)
        {
            command.CommandText += " AND json_extract(payload,'$.Metadata.\"model.kind\"')=$modelKind";
            command.Parameters.AddWithValue("$modelKind", kind);
        }
        if (query.ModelNameProfile is { } nameProfile)
        {
            command.CommandText += " AND json_extract(payload,'$.Metadata.\"model.name-profile\"')=$modelNameProfile";
            command.Parameters.AddWithValue("$modelNameProfile", nameProfile);
        }
        if (query.CharacterGroup is { } character)
        {
            command.CommandText += " AND json_extract(payload,'$.Metadata.\"character.key\"')=$characterGroup";
            command.Parameters.AddWithValue("$characterGroup", character);
        }
        Filter("channels", "=", query.Channels); Filter("availability", "=", query.Availability is { } a ? (int)a : null);
        Filter("completeness", "=", query.Completeness is { } c ? (int)c : null);
        if (query.MinimumDuration is { } min) { command.CommandText += " AND duration >= $minimum"; command.Parameters.AddWithValue("$minimum", min); }
        if (query.MaximumDuration is { } max) { command.CommandText += " AND duration <= $maximum"; command.Parameters.AddWithValue("$maximum", max); }
        return command;
    }
    public void Dispose()
    {
        lock (disposalGate)
        {
            if (disposed) return;
            DisposeLinkedFeatureReaders();
            DisposeTextureProjectionReaders();
            DisposeCharacterReadConnection();
            lock (gate)
            {
                try { Checkpoint(); }
                catch (SqliteException error) when (error.SqliteErrorCode == 13)
                {
                    // Committed WAL frames remain recoverable when checkpoint duplication needs more space.
                    Deviant.Assets.AssetLoadDiagnostics.Counters("CacheCheckpointDeferred", revision,
                        new Dictionary<string, long> { ["SqliteError"] = error.SqliteErrorCode });
                }
                finally { connection.Dispose(); disposed = true; }
            }
        }
    }
}
