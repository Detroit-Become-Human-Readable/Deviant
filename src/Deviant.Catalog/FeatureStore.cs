using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deviant.Core;
using Microsoft.Data.Sqlite;
namespace Deviant.Catalog;

public sealed class FeatureStore : IDisposable, Deviant.Assets.IAssetCatalog
{
    private readonly SqliteConnection connection;
    private readonly string revision;
    private readonly object gate = new();
    private readonly Dictionary<Deviant.Assets.AssetQuery,long> counts = [];
    private long countDataVersion = -1;
    public static FeatureStore Open(ArchiveSnapshot snapshot, string? cacheRoot = null)
    {
        cacheRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deviant", "cache");
        if (DirectoryBoundary.IsWithin(cacheRoot, snapshot.SourceRoot)) throw new ArchiveException("UnsafeCache", "Cache must be outside the archive directory.");
        Directory.CreateDirectory(cacheRoot);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(IndexLayout.ParserVersion + "\n" + SourceIdentity.IndexCacheKey(snapshot.IndexContentId)))).ToLowerInvariant();
        return new(Path.Combine(cacheRoot, "catalog-" + key + ".sqlite3"), SourceIdentity.DerivedCacheKey(snapshot.IndexContentId, snapshot.PhysicalSourceStamp));
    }
    private FeatureStore(string path, string revision)
    {
        this.revision = revision;
        connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
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
        var prefix = collection + "/item/";
        lock (gate) CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var transaction = connection.BeginTransaction();
            using (var remove = connection.CreateCommand())
            {
                remove.Transaction = transaction; remove.CommandText = "DELETE FROM asset_features WHERE revision=$revision AND instr(feature,$prefix)=1";
                remove.Parameters.AddWithValue("$revision", revision); remove.Parameters.AddWithValue("$prefix", prefix); remove.ExecuteNonQuery();
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
            transaction.Commit(); return true;
        });
    }
    public List<T> LoadItems<T>(string collection, string version, CancellationToken token = default)
    {
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var read = connection.CreateCommand(); read.CommandText = "SELECT payload FROM asset_features WHERE revision=$revision AND instr(feature,$prefix)=1 AND version=$version ORDER BY feature";
            read.Parameters.AddWithValue("$revision", revision); read.Parameters.AddWithValue("$prefix", collection + "/item/"); read.Parameters.AddWithValue("$version", version);
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
    {
        lock (gate) CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var transaction = connection.BeginTransaction();
            if (replaceFamily is not null)
            {
                using var clear = connection.CreateCommand(); clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM asset_replacement_keys"; clear.ExecuteNonQuery();
            }
            using var keep=connection.CreateCommand();keep.Transaction=transaction;keep.CommandText="INSERT OR IGNORE INTO asset_replacement_keys VALUES($identity)";
            keep.Parameters.Add("$identity",SqliteType.Text);
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
                if(replaceFamily is not null){keep.Parameters["$identity"].Value=asset.Identity.Key;keep.ExecuteNonQuery();}
                command.Parameters["$family"].Value = asset.Family; command.Parameters["$label"].Value = asset.Label;
                command.Parameters["$search"].Value = asset.SearchTerms ?? string.Join('\n', asset.Names.Select(n => n.Text).Prepend(asset.Label).Append(asset.Identity.Key));
                command.Parameters["$fold"].Value = ((string)command.Parameters["$search"].Value!).ToUpperInvariant();
                command.Parameters["$sort"].Value = asset.SortKey ?? Deviant.Assets.NaturalOrder.Key(asset.Label);
                command.Parameters["$bank"].Value = (object?)asset.GroupKey ?? DBNull.Value; command.Parameters["$language"].Value = (object?)asset.Language ?? DBNull.Value;
                command.Parameters["$codec"].Value = (object?)asset.Codec ?? DBNull.Value; command.Parameters["$duration"].Value = (object?)asset.DurationSeconds ?? DBNull.Value;
                command.Parameters["$channels"].Value = (object?)asset.Channels ?? DBNull.Value; command.Parameters["$availability"].Value = (int)asset.Availability;
                command.Parameters["$completeness"].Value = (int)asset.Completeness; command.Parameters["$payload"].Value = JsonSerializer.Serialize(asset); command.ExecuteNonQuery();
            }
            if(replaceFamily is not null)
            {
                using var delete=connection.CreateCommand();delete.Transaction=transaction;
                delete.CommandText="DELETE FROM assets_v2 WHERE revision=$revision AND family=$family AND identity NOT IN (SELECT identity FROM asset_replacement_keys)";
                delete.Parameters.AddWithValue("$revision",revision);delete.Parameters.AddWithValue("$family",replaceFamily);delete.ExecuteNonQuery();
            }
            transaction.Commit(); counts.Clear(); return true;
        });
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
    {
        if (query.Offset < 0 || query.Limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(query));
        lock (gate) return CatalogService.ExecuteInterruptible(connection, token, () =>
        {
            using var command = AssetPageCommand(query, "payload");
            var rows = new List<Deviant.Assets.AssetDescriptor>(); using var reader = command.ExecuteReader();
            while (reader.Read()) { token.ThrowIfCancellationRequested(); rows.Add(JsonSerializer.Deserialize<Deviant.Assets.AssetDescriptor>(reader.GetString(0))!); }
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
    private SqliteCommand AssetPageCommand(Deviant.Assets.AssetQuery query, string projection)
    {
        if (query.Sort == Deviant.Assets.AssetSort.Name)
        {
            using var index = connection.CreateCommand();
            index.CommandText = "CREATE INDEX IF NOT EXISTS assets_v2_name_v1 ON assets_v2(revision,family,deviant_natural(label),sort_key,deviant_ordinal(identity),deviant_member(identity),identity)";
            index.ExecuteNonQuery();
        }
        var command = AssetCommand(query, "row_id");
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
    private SqliteCommand AssetCommand(Deviant.Assets.AssetQuery query, string select)
    {
        var useTrigram=query.GroupKey is null&&!string.IsNullOrWhiteSpace(query.Search)&&query.Search.EnumerateRunes().Count()>=3;
        var indexHint=useTrigram?" NOT INDEXED":query.GroupKey is not null&&query.ExactKey is null?" INDEXED BY assets_v2_bank":"";
        if(!useTrigram&&query.GroupKey is null&&query.Sort==Deviant.Assets.AssetSort.Name&&query.Language is not null&&query.Family is not null)
            indexHint=" INDEXED BY assets_v2_language";
        if(select=="row_id"&&query.Sort is Deviant.Assets.AssetSort.Name or Deviant.Assets.AssetSort.Default&&query.Family is not null&&query.GroupKey is null&&string.IsNullOrWhiteSpace(query.Search)&&query.ExactKey is null&&query.Language is null&&query.Codec is null&&query.Channels is null&&query.Availability is null&&query.Completeness is null&&query.MinimumDuration is null&&query.MaximumDuration is null)
            indexHint=query.Sort==Deviant.Assets.AssetSort.Name?" INDEXED BY assets_v2_name_v1":" INDEXED BY assets_v2_family_sort";
        var command = connection.CreateCommand(); command.CommandText = $"SELECT {select} FROM assets_v2{indexHint} WHERE revision=$revision";
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
        Filter("channels", "=", query.Channels); Filter("availability", "=", query.Availability is { } a ? (int)a : null);
        Filter("completeness", "=", query.Completeness is { } c ? (int)c : null);
        if (query.MinimumDuration is { } min) { command.CommandText += " AND duration >= $minimum"; command.Parameters.AddWithValue("$minimum", min); }
        if (query.MaximumDuration is { } max) { command.CommandText += " AND duration <= $maximum"; command.Parameters.AddWithValue("$maximum", max); }
        return command;
    }
    public void Dispose() { lock (gate) { Checkpoint(); connection.Dispose(); } }
}
